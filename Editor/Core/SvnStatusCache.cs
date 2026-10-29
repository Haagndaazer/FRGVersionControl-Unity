using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FRGVersionControl.Core.Svn;
using UnityEngine;

namespace FRGVersionControl.Core
{
    /// <summary>
    /// Phase 4 Step 1: in-memory cache of working-copy status (parallel to FileLockManager's
    /// lock cache). Refreshed via FileLockSyncService periodic + focusChanged + wc.db mtime
    /// triggers; consumed by FileLockPendingChangesWindow for cache-first reads.
    ///
    /// Thread-safety: refreshes happen on background threads via SvnCommandRunner async API;
    /// reads are atomic via lock(s_Sync). The cache stores SvnStatusEntry per Unity-asset path
    /// (forward-slash normalized — SVN emits backslashes on Windows, panel expects forward).
    /// Widened from SvnFileStatus → SvnStatusEntry in Slice 1 to surface changelist membership
    /// directly without a second SVN call.
    ///
    /// Design contract:
    /// - Reads return the most-recent successful refresh result (cache-first; never blocks).
    /// - RefreshAsync is the only mutator; it queries the server and replaces the snapshot
    ///   atomically. Failed refresh leaves the prior snapshot intact (don't lose state on
    ///   transient network failures).
    /// - Subscribers register callbacks for "snapshot updated" events; UI panels Repaint
    ///   on the callback (marshaled to main thread via SvnCommandRunner.s_UnityMainSync).
    /// </summary>
    public static class SvnStatusCache
    {
        private static readonly object s_Sync = new object();
        private static Dictionary<string, SvnStatusEntry> s_Snapshot = new Dictionary<string, SvnStatusEntry>();
        private static DateTime s_LastRefreshUtc = DateTime.MinValue;
        private static bool s_RefreshInFlight = false;

        /// <summary>Fired on successful snapshot update. Marshaled to Unity main thread
        /// before invocation so subscribers can Repaint() / touch UnityEngine APIs safely.</summary>
        public static event Action SnapshotUpdated;

        /// <summary>Number of paths with a non-None status in the most-recent snapshot.</summary>
        public static int Count
        {
            get { lock (s_Sync) return s_Snapshot.Count; }
        }

        /// <summary>Time of last successful refresh, UTC. <c>MinValue</c> if never refreshed.</summary>
        public static DateTime LastRefreshUtc
        {
            get { lock (s_Sync) return s_LastRefreshUtc; }
        }

        /// <summary>True iff a refresh is currently running on a background thread.</summary>
        public static bool IsRefreshing
        {
            get { lock (s_Sync) return s_RefreshInFlight; }
        }

        /// <summary>Get the cached status for a path, or <c>None</c> if not in cache. Cache-first;
        /// never spawns svn. Returns immediately. Path can use either separator; normalized internally.</summary>
        public static SvnFileStatus Get(string assetPath)
        {
            var entry = GetEntry(assetPath);
            return entry?.status ?? SvnFileStatus.None;
        }

        /// <summary>Get the full cached entry for a path, or <c>null</c> if not in cache. Cache-first;
        /// never spawns svn. Provides changelist, revision, and other fields beyond raw status.</summary>
        public static SvnStatusEntry GetEntry(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            string key = Normalize(assetPath);
            lock (s_Sync)
            {
                return s_Snapshot.TryGetValue(key, out var v) ? v : null;
            }
        }

        /// <summary>Snapshot all paths with a non-None status. Returns a COPY (safe to enumerate
        /// without locking). Sorted alphabetically for stable UI rendering.</summary>
        public static List<KeyValuePair<string, SvnStatusEntry>> SnapshotAll()
        {
            lock (s_Sync)
            {
                return s_Snapshot.OrderBy(kv => kv.Key).ToList();
            }
        }

        /// <summary>Background refresh. Calls SvnCommandRunner.GetAllFileStatusesAsync, replaces
        /// the snapshot atomically on success, fires SnapshotUpdated on the main thread. Multiple
        /// concurrent refresh calls coalesce — if one is already in flight, this one no-ops.</summary>
        public static Task RefreshAsync(CancellationToken ct = default)
        {
            lock (s_Sync)
            {
                if (s_RefreshInFlight)
                {
                    return Task.CompletedTask;
                }
                s_RefreshInFlight = true;
            }

            return Task.Run(async () =>
            {
                try
                {
                    // 0.2.46 — route through svn-core IPC. Falls back to legacy CLI on
                    // IPC failure. Highest-frequency status path in the editor (called
                    // after every commit/lock/unlock + on toolbar Refresh).
                    var raw = await GetAllStatusEntriesViaIpcOrFallback(ct).ConfigureAwait(false);
                    if (raw == null) return;
                    var normalized = new Dictionary<string, SvnStatusEntry>(raw.Count);
                    foreach (var kv in raw)
                    {
                        // Phase 4 polish: drop SVN's conflict sidecars from the cache. They show
                        // up as Untracked in raw `svn status` (`?` rows) but they're SVN-managed
                        // artifacts, removed automatically on resolve. Surfacing them in the panel
                        // confuses artists.
                        if (IsSvnConflictSidecar(kv.Key)) continue;
                        var normalizedPath = Normalize(kv.Key);
                        var entry = kv.Value;
                        entry.path = normalizedPath;
                        normalized[normalizedPath] = entry;
                    }
                    lock (s_Sync)
                    {
                        s_Snapshot = normalized;
                        s_LastRefreshUtc = DateTime.UtcNow;
                    }
                    SvnCommandRunner.s_UnityMainSync.Post(_ =>
                    {
                        try { SnapshotUpdated?.Invoke(); }
                        catch (Exception ex) { Debug.LogWarning($"[SvnStatusCache] subscriber threw: {ex.Message}"); }
                    }, null);
                }
                catch (OperationCanceledException)
                {
                    // refresh cancelled; leave prior snapshot intact
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SvnStatusCache] RefreshAsync failed: {ex.Message}. " +
                                     "Prior snapshot retained.");
                }
                finally
                {
                    lock (s_Sync) s_RefreshInFlight = false;
                }
            }, ct);
        }

        /// <summary>Synchronous variant of <see cref="RefreshAsync"/>. Blocks the caller until a
        /// single `svn status` call completes (~100-500 ms typical). Used by the
        /// `FileLockAssetModificationProcessor` deferred auto-add processor that needs the
        /// snapshot before deciding what to add — it already runs after a debounce delay from
        /// `EditorApplication.update`, so blocking briefly is acceptable (the alternative is N
        /// `svn info` process spawns which would freeze the editor for minutes on mass-import).
        /// Skips if a refresh is already in flight (no-op; the in-flight one will finish first).</summary>
        public static void RefreshSync()
        {
            lock (s_Sync)
            {
                if (s_RefreshInFlight) return;
                s_RefreshInFlight = true;
            }
            try
            {
                var raw = SvnCommandRunner.GetAllStatusEntries();
                if (raw == null)
                {
                    Debug.LogWarning("[SvnStatusCache] RefreshSync skipped: status unavailable (busy or failed). Prior snapshot retained.");
                    return;
                }
                var normalized = new Dictionary<string, SvnStatusEntry>(raw.Count);
                foreach (var kv in raw)
                {
                    if (IsSvnConflictSidecar(kv.Key)) continue;
                    var normalizedPath = Normalize(kv.Key);
                    var entry = kv.Value;
                    entry.path = normalizedPath;
                    normalized[normalizedPath] = entry;
                }
                lock (s_Sync)
                {
                    s_Snapshot = normalized;
                    s_LastRefreshUtc = DateTime.UtcNow;
                }
                try { SnapshotUpdated?.Invoke(); }
                catch (Exception ex) { Debug.LogWarning($"[SvnStatusCache] subscriber threw: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SvnStatusCache] RefreshSync failed: {ex.Message}. Prior snapshot retained.");
            }
            finally
            {
                lock (s_Sync) s_RefreshInFlight = false;
            }
        }

        /// <summary>Status map via IPC, or <c>null</c> when the core is busy with a long operation
        /// or the read failed. Null means "keep the prior snapshot" — never an empty map.</summary>
        private static async Task<Dictionary<string, SvnStatusEntry>> GetAllStatusEntriesViaIpcOrFallback(CancellationToken ct)
        {
            var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
            if (!client.IsAvailable) return null;
            var r = await client.TryGetAllStatusEntriesAsync(ct).ConfigureAwait(false);
            if (r.Ok) return r.Value;
            Debug.LogWarning(r.Busy
                ? $"[SvnStatusCache] refresh skipped (prior snapshot retained): {r.Error}"
                : $"[SvnStatusCache] IPC status failed (prior snapshot retained): {r.Error}");
            return null;
        }

        /// <summary>Force-clear the cache. Used when toolset is uninitialized or for testing.</summary>
        public static void Clear()
        {
            lock (s_Sync)
            {
                s_Snapshot = new Dictionary<string, SvnStatusEntry>();
                s_LastRefreshUtc = DateTime.MinValue;
            }
        }

        private static string Normalize(string path) => (path ?? "").Replace('\\', '/');

        // Phase 4 polish: SVN conflict-sidecar regex.
        // Matches:
        //   *.r<digits>            base or theirs revision sidecar
        //   *.r<digits>.meta       Unity .meta sibling
        //   *.mine                 your local edit copy
        //   *.mine.meta
        //   *.prej                 property-rejection sidecar (rare; svn:needs-lock mismatches)
        //   *.prej.meta
        // Out of scope (NOT filtered): tree-conflict sidecars *.merge-left.r<N> /
        // *.merge-right.r<N> — they don't surface in this codebase's flow today;
        // surface them as-is if they appear, then revisit.
        private static readonly Regex s_ConflictSidecarRegex = new Regex(
            @"\.(?:r\d+|mine|prej)(?:\.meta)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsSvnConflictSidecar(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return s_ConflictSidecarRegex.IsMatch(path);
        }
    }
}
