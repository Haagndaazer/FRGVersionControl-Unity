using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core.IpcClient;
using Debug = UnityEngine.Debug;

namespace FRGVersionControl.Core.Svn
{
    /// <summary>
    /// 0.2.63 — Pure IPC shim. Every public method routes through
    /// FileLockManager.Instance.IpcClient. The legacy CLI invocation paths, XML parsers,
    /// process spawner, and runner internals were deleted in this cleanup; svn-core.exe
    /// is now the only path to the SVN binary.
    ///
    /// Why this class still exists as a compatibility shim:
    /// - Preserves source-compat with the 40+ call sites in UI/Hooks/Services/Tools that
    ///   already call `SvnCommandRunner.Lock(...)` etc.
    /// - Holds engine-specific helpers used by other Unity code (ProjectRoot,
    ///   ToolingConfigDir, s_UnityMainSync, the SvnResult / SvnStatusEntry / SvnLockInfo /
    ///   SvnInfo / SvnLogEntry / LockState / SvnFileStatus / ResolveAccept DTOs).
    /// - Eventual goal: callers migrate to call FileLockManager.Instance.IpcClient
    ///   directly, then this class deletes entirely. For now it's the thin
    ///   sync-blocking adapter that bridges Unity's sync editor code to the async IPC
    ///   client.
    /// </summary>
    public static class SvnCommandRunner
    {
        // ====================================================================
        // Project-root helper (used by Unity-side code that needs the WC path).
        // ====================================================================

        private static string s_ProjectRoot;
        public static string ProjectRoot
        {
            get
            {
                if (s_ProjectRoot == null)
                {
                    s_ProjectRoot = Application.dataPath
                        .Replace("/Assets", "")
                        .Replace("\\Assets", "");
                }
                return s_ProjectRoot;
            }
        }

        /// <summary>Per-WC tooling config dir for SVN auth isolation. Routed through SvnCore
        /// via ctx.configDir; this property is kept as a documentation pointer + back-compat
        /// for any caller that constructs an explicit svn invocation outside the IPC path.</summary>
        public static string ToolingConfigDir => Path.Combine(ProjectRoot, ".svn-tooling-config");

        /// <summary>Unity main-thread SynchronizationContext, captured at static init. Used by
        /// async paths to marshal completions back to the editor main thread.</summary>
        public static readonly SynchronizationContext s_UnityMainSync =
            SynchronizationContext.Current ?? new SynchronizationContext();

        private static int s_MainThreadId = -1;

        [UnityEditor.InitializeOnLoadMethod]
        private static void CaptureMainThread() => s_MainThreadId = Thread.CurrentThread.ManagedThreadId;

        /// <summary>True on the Unity main thread (captured at editor load). Sync IPC wrappers
        /// called here get a bounded wait budget so a long core operation cannot freeze the editor.</summary>
        public static bool IsMainThread => s_MainThreadId == Thread.CurrentThread.ManagedThreadId;

        private static int? MainThreadBudget(string op) =>
            IsMainThread && FRGVersionControl.Core.IpcClient.IpcOpPolicy.Classify(op) != FRGVersionControl.Core.IpcClient.IpcOpClass.ReadOnly
                ? FRGVersionControl.Core.IpcClient.IpcOpPolicy.MainThreadWaitBudgetMs
                : (int?)null;

        // ====================================================================
        // Public Run escape hatch — deprecated. Returns a wrapped error to signal that
        // direct CLI invocation is no longer the supported path.
        // ====================================================================

        [Obsolete("0.2.63 — Direct svn.exe invocation is no longer supported. Use the typed " +
                  "SvnCommandRunner.* methods or FileLockManager.Instance.IpcClient directly.")]
        public static SvnResult Run(string arguments, string workingDirectory = null, int timeoutSeconds = 30)
        {
            return new SvnResult
            {
                success = false,
                exitCode = -1,
                output = "",
                error = "SvnCommandRunner.Run is deprecated — svn-core IPC is the supported path. " +
                        "Use the typed Lock/Unlock/Status/etc. methods.",
            };
        }

        [Obsolete("0.2.63 — Use the typed async methods instead.")]
        public static Task<SvnResult> RunAsync(string arguments, string workingDirectory = null,
                                                int timeoutSeconds = 30,
                                                CancellationToken ct = default)
            => Task.FromResult(Run(arguments, workingDirectory, timeoutSeconds));

        public static bool IsSvnAvailable()
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return false;
                var ctx = client.BuildDefaultContext();
                var result = client.InvokeRawAsync("meta.version", "{}", ctx).GetAwaiter().GetResult();
                if (!result.Ok) return false;
                // result.RawLine contains `"svnVersion":"1.14.5"` or `"svnVersion":"not-found"`.
                return !result.RawLine.Contains("\"svnVersion\":\"not-found\"");
            }
            catch { return false; }
        }

        // ====================================================================
        // Status (single-path + bulk)
        // ====================================================================

        public static SvnStatusEntry Status(string path) =>
            ExecuteIpcSingleStatus(path, upstream: false);

        public static SvnStatusEntry StatusUpstream(string path) =>
            ExecuteIpcSingleStatus(path, upstream: true);

        private static SvnStatusEntry ExecuteIpcSingleStatus(string path, bool upstream)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return null;
                var ctx = client.BuildDefaultContext();
                var sb = new StringBuilder("{\"paths\":[\"").Append(IpcEscape(path ?? "."))
                    .Append("\"]");
                if (upstream) sb.Append(",\"includeUpstream\":true");
                sb.Append("}");
                var result = client.InvokeRawAsync("status.query", sb.ToString(), ctx).GetAwaiter().GetResult();
                if (!result.Ok) return null;
                return FRGVersionControl.Core.IpcClient.IpcResponseParsers.ParseFirstStatusEntry(result.RawLine);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SvnCommandRunner] IPC single-status failed: {ex.Message}");
                return null;
            }
        }

        public static bool HasUncommittedChanges(string path)
        {
            var entry = Status(path);
            if (entry == null) return false;
            switch (entry.status)
            {
                case SvnFileStatus.Modified:
                case SvnFileStatus.Added:
                case SvnFileStatus.Deleted:
                case SvnFileStatus.Replaced:
                case SvnFileStatus.Conflict:
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsFileUntracked(string path)
        {
            var entry = Status(path);
            return entry != null && entry.status == SvnFileStatus.Untracked;
        }

        /// <summary>True when the path has no committed base: never added (Untracked) or added
        /// but not yet committed (Added). Neither can hold an SVN lock — `svn lock` fails
        /// E155010 on both — so auto-lock paths skip them.</summary>
        public static bool IsFileUncommitted(string path)
        {
            var entry = Status(path);
            return entry != null &&
                   (entry.status == SvnFileStatus.Untracked || entry.status == SvnFileStatus.Added);
        }

        public static SvnFileStatus GetFileSvnStatus(string path)
        {
            var entry = Status(path);
            return entry != null ? entry.status : SvnFileStatus.None;
        }

        /// <summary>Status map, or <c>null</c> when the core is busy with a long operation or the
        /// read failed — callers keep their prior cache instead of wiping it.</summary>
        public static Dictionary<string, SvnFileStatus> GetAllFileStatuses(string workingCopyRoot = null)
        {
            var entries = GetAllStatusEntries(workingCopyRoot);
            if (entries == null) return null;
            var dict = new Dictionary<string, SvnFileStatus>();
            foreach (var kv in entries)
            {
                if (kv.Value.status != SvnFileStatus.None) dict[kv.Key] = kv.Value.status;
            }
            return dict;
        }

        /// <summary>Upstream lock map, or <c>null</c> when the core is busy with a long operation
        /// or the read failed — callers keep their prior cache instead of wiping it.</summary>
        public static Dictionary<string, SvnLockInfo> StatusUpstreamAll(string workingCopyRoot = null)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return null;
                var r = client.TryGetLockMapAsync().GetAwaiter().GetResult();
                if (r.Ok) return r.Value;
                Debug.LogWarning(r.Busy
                    ? $"[SvnCommandRunner] StatusUpstreamAll skipped (prior cache kept): {r.Error}"
                    : $"[SvnCommandRunner] IPC StatusUpstreamAll failed (prior cache kept): {r.Error}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SvnCommandRunner] IPC StatusUpstreamAll failed (prior cache kept): {ex.Message}");
                return null;
            }
        }

        /// <summary>Full status map, or <c>null</c> when the core is busy with a long operation or
        /// the read failed — callers keep their prior snapshot instead of wiping it.</summary>
        public static Dictionary<string, SvnStatusEntry> GetAllStatusEntries(string workingCopyRoot = null)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return null;
                var r = client.TryGetAllStatusEntriesAsync().GetAwaiter().GetResult();
                if (r.Ok) return r.Value;
                Debug.LogWarning(r.Busy
                    ? $"[SvnCommandRunner] GetAllStatusEntries skipped (prior snapshot kept): {r.Error}"
                    : $"[SvnCommandRunner] IPC GetAllStatusEntries failed (prior snapshot kept): {r.Error}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SvnCommandRunner] IPC GetAllStatusEntries failed (prior snapshot kept): {ex.Message}");
                return null;
            }
        }

        public static Dictionary<string, List<string>> GetAllChangelistMembership(string workingCopyRoot = null)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return new Dictionary<string, List<string>>();
                var ctx = client.BuildDefaultContext();
                var result = client.InvokeRawAsync("cl.membership", "{}", ctx).GetAwaiter().GetResult();
                if (!result.Ok) return new Dictionary<string, List<string>>();
                return FRGVersionControl.Core.IpcClient.IpcResponseParsers.ParseChangelistMembership(result.RawLine);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SvnCommandRunner] IPC cl.membership failed: {ex.Message}");
                return new Dictionary<string, List<string>>();
            }
        }

        public static SvnInfo Info(string path)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return null;
                var ctx = client.BuildDefaultContext();
                var args = $"{{\"path\":\"{IpcEscape(path)}\"}}";
                var result = client.InvokeRawAsync("meta.info", args, ctx).GetAwaiter().GetResult();
                if (!result.Ok) return null;
                var shim = UnityEngine.JsonUtility.FromJson<IpcInfoShim>(result.RawLine);
                if (shim?.result == null) return null;
                return new SvnInfo
                {
                    path = path,
                    repoUrl = shim.result.url,
                    repoUuid = shim.result.repoUuid,
                    revision = (int)shim.result.revision,
                    lockToken = shim.result.lockToken,
                    lockOwner = shim.result.lockOwner,
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SvnCommandRunner] IPC info failed: {ex.Message}");
                return null;
            }
        }

        public static bool IsVersioned(string path)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return false;
                var ctx = client.BuildDefaultContext();
                var args = $"{{\"path\":\"{IpcEscape(path)}\"}}";
                var result = client.InvokeRawAsync("status.isVersioned", args, ctx).GetAwaiter().GetResult();
                return result.Ok && result.RawLine.Contains("\"versioned\":true");
            }
            catch { return false; }
        }

        // ====================================================================
        // Locks
        // ====================================================================

        public static SvnResult Lock(string[] paths, string message)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));
            return ExecuteIpcLock(paths, message, steal: false);
        }

        public static SvnResult LockForce(string[] paths, string message)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));
            return ExecuteIpcLock(paths, message, steal: true);
        }

        public static SvnResult Unlock(string[] paths, bool force = false)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));
            return ExecuteIpcUnlock(paths, force);
        }

        public static SvnResult UnlockUrl(string[] urls, bool force = true)
        {
            if (urls == null || urls.Length == 0)
                throw new ArgumentException("urls must be non-empty", nameof(urls));
            return ExecuteIpcUnlock(urls, force);
        }

        // ====================================================================
        // Mutations
        // ====================================================================

        public static SvnResult Commit(string[] paths, string message, int timeoutSeconds = 300)
        {
            if (paths == null || paths.Length == 0 || paths.All(p => string.IsNullOrWhiteSpace(p)))
            {
                throw new ArgumentException(
                    "paths must be non-empty. Bare 'svn commit -m' from automation is forbidden " +
                    "per CLAUDE.md hard rule.", nameof(paths));
            }
            if (message == null) throw new ArgumentNullException(nameof(message));
            var filtered = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            return ExecuteIpcGeneric("mutate.commit", filtered, $"\"message\":\"{IpcEscape(message)}\"");
        }

        public static SvnResult Revert(string[] paths, bool alsoRevertMeta = true)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));
            return ExecuteIpcGeneric("mutate.revert", ExpandMetaSiblings(paths, alsoRevertMeta).ToArray(), null);
        }

        private static List<string> ExpandMetaSiblings(IEnumerable<string> paths, bool alsoRevertMeta)
        {
            var allPaths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p) || !seen.Add(p)) continue;
                allPaths.Add(p);
                if (!alsoRevertMeta || p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                var meta = p + ".meta";
                var fullMeta = Path.IsPathRooted(meta) ? meta : Path.Combine(ProjectRoot, meta);
                if (File.Exists(fullMeta) && seen.Add(meta)) allPaths.Add(meta);
            }
            return allPaths;
        }

        private static string PathsJson(IEnumerable<string> paths, string extraJson = null)
        {
            var sb = new StringBuilder("{\"paths\":[");
            bool first = true;
            foreach (var p in paths)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("\"").Append(IpcEscape(p)).Append("\"");
            }
            sb.Append("]");
            if (!string.IsNullOrEmpty(extraJson)) sb.Append(",").Append(extraJson);
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>Batched revert with per-path outcomes (0.2.89): one <c>mutate.revert</c> per
        /// chunk of <see cref="BatchOutcomes.ChunkSize"/> targets (after .meta expansion).
        /// Never raises for a per-path problem; an abandoned chunk lands in <c>unknown</c>.</summary>
        public static async Task<RevertOutcome> RevertDetailedAsync(string[] paths, bool alsoRevertMeta = true)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));
            var outcome = new RevertOutcome();
            var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
            if (!client.IsAvailable)
            {
                outcome.success = false;
                outcome.error = IpcUnavailable("mutate.revert").error;
                return outcome;
            }
            var distinct = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var assetChunk in BatchOutcomes.Chunk(distinct, BatchOutcomes.ChunkSize / 2))
            {
                var chunk = ExpandMetaSiblings(assetChunk, alsoRevertMeta).ToArray();
                FRGVersionControl.Core.IpcClient.IpcRawResult raw;
                try
                {
                    raw = await client.InvokeRawAsync("mutate.revert", PathsJson(chunk), client.BuildDefaultContext()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    raw = new FRGVersionControl.Core.IpcClient.IpcRawResult { Op = "mutate.revert", Ok = false, ErrorKind = "protocol", ErrorMessage = ex.Message, ErrorDefaultAction = "Refresh status before retrying." };
                }
                if (raw.Ok)
                {
                    int before = outcome.reverted.Count + outcome.failed.Count + outcome.unchanged.Count;
                    BatchOutcomes.MergeRevert(outcome, raw.RawLine);
                    int attributed = outcome.reverted.Count + outcome.failed.Count + outcome.unchanged.Count - before;
                    Debug.Log($"[SvnCommandRunner] revert chunk: {chunk.Length} targets → {attributed} attributed ({outcome.reverted.Count} reverted so far, {outcome.failed.Count} failed, {outcome.unchanged.Count} unchanged)");
                }
                else if (raw.StillRunning || raw.Busy)
                {
                    outcome.unknown.AddRange(chunk);
                    outcome.unknownReason = FormatIpcError(raw, "mutate.revert");
                    Debug.LogWarning($"[SvnCommandRunner] revert chunk of {chunk.Length} abandoned: {outcome.unknownReason}");
                }
                else
                {
                    outcome.success = false;
                    outcome.error = FormatIpcError(raw, "mutate.revert");
                    var perPath = BatchOutcomes.ParseErrorPerPath(raw.RawLine);
                    foreach (var p in chunk)
                    {
                        var hit = perPath.FirstOrDefault(f => BatchOutcomes.PathsMatch(f.path, p));
                        outcome.failed.Add(new RevertFailure { path = p, error = hit != null ? hit.line + " Check the path and retry." : outcome.error });
                    }
                }
            }
            return outcome;
        }

        /// <summary>Batched unlock with per-path outcomes (0.2.89): one <c>lock.release</c> per
        /// chunk. Never raises for a per-path problem; an abandoned chunk lands in <c>unknown</c>.</summary>
        public static async Task<UnlockOutcome> UnlockDetailedAsync(string[] paths, bool force = false)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));
            var outcome = new UnlockOutcome();
            var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
            if (!client.IsAvailable)
            {
                outcome.success = false;
                outcome.error = IpcUnavailable("lock.release").error;
                return outcome;
            }
            foreach (var chunk in BatchOutcomes.Chunk(paths, BatchOutcomes.ChunkSize / 2))
            {
                FRGVersionControl.Core.IpcClient.IpcRawResult raw;
                try
                {
                    raw = await client.InvokeRawAsync("lock.release", PathsJson(chunk, force ? "\"force\":true" : null), client.BuildDefaultContext()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    raw = new FRGVersionControl.Core.IpcClient.IpcRawResult { Op = "lock.release", Ok = false, ErrorKind = "protocol", ErrorMessage = ex.Message, ErrorDefaultAction = "Refresh status before retrying." };
                }
                if (raw.Ok)
                {
                    int before = outcome.released.Count;
                    BatchOutcomes.MergeUnlock(outcome, raw.RawLine);
                    Debug.Log($"[SvnCommandRunner] unlock chunk: {chunk.Length} targets → {outcome.released.Count - before} released, {outcome.failed.Count} failed so far");
                }
                else if (raw.StillRunning || raw.Busy)
                {
                    outcome.unknown.AddRange(chunk);
                    outcome.unknownReason = FormatIpcError(raw, "lock.release");
                    Debug.LogWarning($"[SvnCommandRunner] unlock chunk of {chunk.Length} abandoned: {outcome.unknownReason}");
                }
                else
                {
                    outcome.success = false;
                    outcome.error = FormatIpcError(raw, "lock.release");
                    var perPath = BatchOutcomes.ParseErrorPerPath(raw.RawLine);
                    foreach (var p in chunk)
                    {
                        var hit = perPath.FirstOrDefault(f => BatchOutcomes.PathsMatch(f.path, p));
                        outcome.failed.Add(hit != null ? new UnlockFailure { path = p, svnCode = hit.svnCode, line = hit.line } : new UnlockFailure { path = p, svnCode = null, line = outcome.error });
                    }
                }
            }
            return outcome;
        }

        /// <summary>One <c>status.query</c> for many paths; <c>null</c> when the core is busy or
        /// the read failed. Keys use forward slashes; a path absent from the map has no status
        /// (unmodified).</summary>
        public static Dictionary<string, SvnStatusEntry> StatusMany(string[] paths)
        {
            if (paths == null || paths.Length == 0) return new Dictionary<string, SvnStatusEntry>();
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return null;
                var r = client.TryStatusQueryAsync(paths).GetAwaiter().GetResult();
                if (r.Ok) return r.Value;
                Debug.LogWarning($"[SvnCommandRunner] StatusMany {(r.Busy ? "skipped" : "failed")}: {r.Error}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SvnCommandRunner] StatusMany failed: {ex.Message}");
                return null;
            }
        }

        public static bool IsChangedStatus(SvnFileStatus status)
        {
            switch (status)
            {
                case SvnFileStatus.Modified:
                case SvnFileStatus.Added:
                case SvnFileStatus.Deleted:
                case SvnFileStatus.Replaced:
                case SvnFileStatus.Conflict:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The subset of <paramref name="paths"/> with uncommitted changes, from one
        /// status query. Returns null when status is unavailable.</summary>
        public static List<string> FilterChanged(string[] paths)
        {
            var map = StatusMany(paths);
            if (map == null) return null;
            var changed = new List<string>();
            foreach (var p in paths)
            {
                if (map.TryGetValue((p ?? "").Replace('\\', '/'), out var e) && IsChangedStatus(e.status)) changed.Add(p);
            }
            return changed;
        }

        /// <summary>Every versioned property on one path (SVN_CORE_API.md §5.13). svn: values
        /// arrive in their stored LF form.</summary>
        public static async Task<PropListResult> PropListAsync(string path)
        {
            var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
            if (!client.IsAvailable) return new PropListResult { success = false, error = IpcUnavailable("prop.list").error };
            try
            {
                var raw = await client.InvokeRawAsync("prop.list", "{\"path\":\"" + IpcEscape(path) + "\"}", client.BuildDefaultContext()).ConfigureAwait(false);
                if (!raw.Ok) return new PropListResult { success = false, error = FormatIpcError(raw, "prop.list") };
                return new PropListResult { success = true, props = IpcResponseParsers.ParseStringMap(raw.RawLine, "props") };
            }
            catch (Exception ex)
            {
                return new PropListResult { success = false, error = $"Could not read SVN properties: {ex.Message}. Click Reload to try again." };
            }
        }

        public static Task<SvnResult> PropSetAsync(string path, string name, string value) =>
            PropWriteAsync("prop.set", "{\"path\":\"" + IpcEscape(path) + "\",\"name\":\"" + IpcEscape(name) + "\",\"value\":\"" + IpcEscape(value ?? "") + "\"}");

        public static Task<SvnResult> PropDeleteAsync(string path, string name) =>
            PropWriteAsync("prop.delete", "{\"path\":\"" + IpcEscape(path) + "\",\"name\":\"" + IpcEscape(name) + "\"}");

        private static async Task<SvnResult> PropWriteAsync(string op, string argsJson)
        {
            var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
            if (!client.IsAvailable) return IpcUnavailable(op);
            try
            {
                var raw = await client.InvokeRawAsync(op, argsJson, client.BuildDefaultContext()).ConfigureAwait(false);
                return raw.Ok
                    ? new SvnResult { success = true, output = op + " ok (ipc)" }
                    : new SvnResult { success = false, error = FormatIpcError(raw, op) };
            }
            catch (Exception ex)
            {
                return new SvnResult { success = false, error = $"IPC {op} threw: {ex.Message} — Click Reload, then try again." };
            }
        }

        public static SvnResult Add(string[] paths)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));
            return ExecuteIpcGeneric("mutate.add", paths, null);
        }

        public static SvnResult AddForCommit(string[] paths, HashSet<string> lockableExtensions)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));

            // Expand .meta siblings (Unity-specific — stays engine-side)
            var expanded = new List<string>(paths.Length * 2);
            foreach (var p in paths)
            {
                expanded.Add(p);
                if (!p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    string metaPath = p + ".meta";
                    string fullMeta = Path.IsPathRooted(metaPath) ? metaPath : Path.Combine(ProjectRoot, metaPath);
                    if (File.Exists(fullMeta)) expanded.Add(metaPath);
                }
            }
            var distinct = expanded.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return IpcUnavailable("addForCommit");
                var ctx = client.BuildDefaultContext();
                var sb = new StringBuilder("{\"paths\":[");
                for (int i = 0; i < distinct.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(IpcEscape(distinct[i])).Append("\"");
                }
                sb.Append("],\"lockableExtensions\":[");
                if (lockableExtensions != null)
                {
                    bool first = true;
                    foreach (var ext in lockableExtensions)
                    {
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("\"").Append(IpcEscape(ext)).Append("\"");
                    }
                }
                sb.Append("]}");
                var result = client.InvokeRawAsync("mutate.addForCommit", sb.ToString(), ctx).GetAwaiter().GetResult();
                if (result.Ok) return new SvnResult { success = true, output = "addForCommit ok (ipc)" };
                return new SvnResult { success = false, output = "", error = FormatIpcError(result, "addForCommit") };
            }
            catch (Exception ex)
            {
                return new SvnResult { success = false, error = $"IPC addForCommit threw: {ex.Message}" };
            }
        }

        public static SvnResult Move(string srcPath, string dstPath)
        {
            if (string.IsNullOrEmpty(srcPath)) throw new ArgumentException("srcPath required", nameof(srcPath));
            if (string.IsNullOrEmpty(dstPath)) throw new ArgumentException("dstPath required", nameof(dstPath));
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return IpcUnavailable("move");
                var ctx = client.BuildDefaultContext();
                var args = $"{{\"from\":\"{IpcEscape(srcPath)}\",\"to\":\"{IpcEscape(dstPath)}\"}}";
                var result = client.InvokeRawAsync("mutate.move", args, ctx).GetAwaiter().GetResult();
                if (result.Ok) return new SvnResult { success = true, output = "move ok (ipc)" };
                return new SvnResult { success = false, error = FormatIpcError(result, "move") };
            }
            catch (Exception ex)
            {
                return new SvnResult { success = false, error = $"IPC move threw: {ex.Message}" };
            }
        }

        public static SvnResult Delete(string[] paths)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));
            return ExecuteIpcGeneric("mutate.delete", paths, null);
        }

        /// <summary>Reconcile missing (svn `!`) working-copy entries via the engine-agnostic
        /// core op `mutate.reconcileMissing` — the core decides per path whether to revert (an
        /// orphaned uncommitted add) or delete (a committed entry) and reports what it did. The
        /// engine is a pure pass-through; it makes no delete-vs-revert decision itself.</summary>
        public static ReconcileMissingResult ReconcileMissing(string[] paths)
        {
            if (paths == null || paths.Length == 0)
                return new ReconcileMissingResult { success = false, error = "paths must be non-empty" };
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable)
                    return new ReconcileMissingResult { success = false, error = "svn-core.exe is not available. Restart the editor or reinstall the package." };
                var ctx = client.BuildDefaultContext();
                var sb = new System.Text.StringBuilder("{\"paths\":[");
                for (int i = 0; i < paths.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(IpcEscape(paths[i])).Append('"');
                }
                sb.Append("]}");
                var result = client.InvokeRawAsync("mutate.reconcileMissing", sb.ToString(), ctx).GetAwaiter().GetResult();
                if (!result.Ok)
                    return new ReconcileMissingResult { success = false, error = FormatIpcError(result, "reconcileMissing") };
                var shim = UnityEngine.JsonUtility.FromJson<IpcReconcileShim>(result.RawLine);
                var r = shim?.result;
                return new ReconcileMissingResult
                {
                    success = true,
                    reverted = r?.reverted ?? new string[0],
                    deleted = r?.deleted ?? new string[0],
                    failed = r?.failed ?? new IpcReconcileFail[0],
                };
            }
            catch (Exception ex)
            {
                return new ReconcileMissingResult { success = false, error = $"IPC reconcileMissing threw: {ex.Message}" };
            }
        }

        public static Task<ReconcileMissingResult> ReconcileMissingAsync(string[] paths, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return ReconcileMissing(paths); }, ct);

        /// <summary>Ask the core what an engine-initiated delete of these on-disk paths means
        /// in SVN terms (`mutate.prepareDelete`). The core reverts schedule-add nodes (depth
        /// infinity) and reports everything else untouched; the engine never decides
        /// revert-vs-delete itself. Never throws.</summary>
        public static PrepareDeleteResult PrepareDelete(string[] paths)
        {
            if (paths == null || paths.Length == 0)
                return new PrepareDeleteResult { success = false, error = "paths must be non-empty" };
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable)
                    return new PrepareDeleteResult { success = false, error = "svn-core.exe is not available. Restart the editor or reinstall the package." };
                var ctx = client.BuildDefaultContext();
                var sb = new System.Text.StringBuilder("{\"paths\":[");
                for (int i = 0; i < paths.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(IpcEscape(paths[i])).Append('"');
                }
                sb.Append("]}");
                var result = client.InvokeRawAsync("mutate.prepareDelete", sb.ToString(), ctx).GetAwaiter().GetResult();
                if (!result.Ok)
                    return new PrepareDeleteResult { success = false, error = FormatIpcError(result, "prepareDelete") };
                var shim = UnityEngine.JsonUtility.FromJson<IpcPrepareDeleteShim>(result.RawLine);
                var r = shim?.result;
                return new PrepareDeleteResult
                {
                    success = true,
                    reverted = r?.reverted ?? new string[0],
                    versioned = r?.versioned ?? new IpcPrepareDeleteVersioned[0],
                    unversioned = r?.unversioned ?? new string[0],
                    failed = r?.failed ?? new IpcReconcileFail[0],
                };
            }
            catch (Exception ex)
            {
                return new PrepareDeleteResult { success = false, error = $"IPC prepareDelete threw: {ex.Message}" };
            }
        }

        public static SvnResult Update(string path = null, int timeoutSeconds = 300)
        {
            var arr = string.IsNullOrEmpty(path) ? new string[0] : new[] { path };
            return ExecuteIpcGeneric("mutate.update", arr, null);
        }

        public static SvnResult Checkout(string repoUrl, string targetDirectory, string username = null, int timeoutSeconds = 600)
        {
            // mutate.checkout — rare; engine code rarely needs this directly.
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return IpcUnavailable("checkout");
                var ctx = client.BuildDefaultContext();
                if (!string.IsNullOrEmpty(username)) ctx.username = username;
                var args = $"{{\"url\":\"{IpcEscape(repoUrl)}\",\"dest\":\"{IpcEscape(targetDirectory)}\"}}";
                var result = client.InvokeRawAsync("mutate.checkout", args, ctx).GetAwaiter().GetResult();
                if (result.Ok) return new SvnResult { success = true, output = "checkout ok (ipc)" };
                return new SvnResult { success = false, error = FormatIpcError(result, "checkout") };
            }
            catch (Exception ex)
            {
                return new SvnResult { success = false, error = $"IPC checkout threw: {ex.Message}" };
            }
        }

        // ====================================================================
        // Changelists
        // ====================================================================

        public static SvnResult AddToChangelist(string[] paths, string changelistName)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));
            if (string.IsNullOrEmpty(changelistName))
                throw new ArgumentException("changelistName must be non-empty", nameof(changelistName));
            return ExecuteIpcGeneric("cl.add", paths, $"\"name\":\"{IpcEscape(changelistName)}\"");
        }

        public static SvnResult RemoveFromChangelist(string[] paths)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));
            return ExecuteIpcGeneric("cl.remove", paths, null);
        }

        public static SvnResult MoveBetweenChangelists(string[] paths, string fromCl, string toCl)
        {
            if (paths == null || paths.Length == 0)
                throw new ArgumentException("paths must be non-empty", nameof(paths));
            if (string.IsNullOrEmpty(toCl))
                throw new ArgumentException("toCl must be non-empty", nameof(toCl));
            return AddToChangelist(paths, toCl);
        }

        // ====================================================================
        // Conflict resolution
        // ====================================================================

        public static SvnResult Resolve(string path, ResolveAccept accept)
        {
            string acceptArg;
            switch (accept)
            {
                case ResolveAccept.Working:    acceptArg = "working"; break;
                case ResolveAccept.Base:       acceptArg = "base"; break;
                case ResolveAccept.MineFull:   acceptArg = "mine-full"; break;
                case ResolveAccept.TheirsFull: acceptArg = "theirs-full"; break;
                default:                       acceptArg = "working"; break;
            }
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return IpcUnavailable("resolve");
                var ctx = client.BuildDefaultContext();
                var args = $"{{\"path\":\"{IpcEscape(path)}\",\"accept\":\"{acceptArg}\"}}";
                var result = client.InvokeRawAsync("conflict.resolve", args, ctx).GetAwaiter().GetResult();
                if (result.Ok) return new SvnResult { success = true, output = "resolve ok (ipc)" };
                return new SvnResult { success = false, error = FormatIpcError(result, "resolve") };
            }
            catch (Exception ex)
            {
                return new SvnResult { success = false, error = $"IPC resolve threw: {ex.Message}" };
            }
        }

        // ====================================================================
        // Maintenance
        // ====================================================================

        public static SvnResult Cleanup(string workingCopyRoot = null) =>
            ExecuteIpcArgless("maint.cleanup");

        public static SvnResult VacuumPristines(string workingCopyRoot = null, int timeoutSeconds = 300) =>
            ExecuteIpcArgless("maint.vacuumPristines");

        // ====================================================================
        // History
        // ====================================================================

        public static List<SvnLogEntry> FetchLog(string path, int upperRev, int limit)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return new List<SvnLogEntry>();
                var ctx = client.BuildDefaultContext();
                var args = $"{{\"path\":\"{IpcEscape(path)}\",\"limit\":{limit}}}";
                var result = client.InvokeRawAsync("hist.log", args, ctx).GetAwaiter().GetResult();
                if (!result.Ok) return new List<SvnLogEntry>();
                return FRGVersionControl.Core.IpcClient.IpcResponseParsers.ParseLogEntries(result.RawLine);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SvnCommandRunner] IPC hist.log failed: {ex.Message}");
                return new List<SvnLogEntry>();
            }
        }

        public static Task<List<SvnLogEntry>> FetchLogAsync(string path, int upperRev, int limit,
                                                             CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return FetchLog(path, upperRev, limit); }, ct);

        public static string ExportRevisionToTemp(string path, int revision)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return null;
                var ctx = client.BuildDefaultContext();
                var args = $"{{\"path\":\"{IpcEscape(path)}\",\"revision\":{revision}}}";
                var result = client.InvokeRawAsync("hist.exportRevision", args, ctx).GetAwaiter().GetResult();
                if (!result.Ok) return null;
                int idx = result.RawLine.IndexOf("\"tempPath\":\"", StringComparison.Ordinal);
                if (idx < 0) return null;
                int start = idx + "\"tempPath\":\"".Length;
                int end = result.RawLine.IndexOf('"', start);
                if (end <= start) return null;
                return result.RawLine.Substring(start, end - start).Replace("\\\\", "\\").Replace("\\/", "/");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SvnCommandRunner] IPC hist.exportRevision failed: {ex.Message}");
                return null;
            }
        }

        public static Task<string> ExportRevisionToTempAsync(string path, int revision,
                                                              CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return ExportRevisionToTemp(path, revision); }, ct);

        // ====================================================================
        // Local file hashing (no svn invocation)
        // ====================================================================

        public static string HashSha256(string path)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = File.OpenRead(path);
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch { return null; }
        }

        // ====================================================================
        // Async variants (Task.Run wrappers; the sync methods already block via .GetAwaiter().GetResult())
        // ====================================================================

        public static Task<SvnStatusEntry> StatusAsync(string path, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Status(path); }, ct);

        public static Task<SvnStatusEntry> StatusUpstreamAsync(string path, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return StatusUpstream(path); }, ct);

        public static Task<bool> HasUncommittedChangesAsync(string path, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return HasUncommittedChanges(path); }, ct);

        public static Task<bool> IsFileUntrackedAsync(string path, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return IsFileUntracked(path); }, ct);

        public static Task<Dictionary<string, SvnFileStatus>> GetAllFileStatusesAsync(
            string workingCopyRoot = null, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return GetAllFileStatuses(workingCopyRoot); }, ct);

        public static Task<Dictionary<string, SvnLockInfo>> StatusUpstreamAllAsync(
            string workingCopyRoot = null, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return StatusUpstreamAll(workingCopyRoot); }, ct);

        public static Task<SvnInfo> InfoAsync(string path, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Info(path); }, ct);

        public static Task<SvnResult> LockAsync(string[] paths, string message, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Lock(paths, message); }, ct);

        public static Task<SvnResult> LockForceAsync(string[] paths, string message, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return LockForce(paths, message); }, ct);

        public static Task<SvnResult> UnlockAsync(string[] paths, bool force = false, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Unlock(paths, force); }, ct);

        public static Task<SvnResult> UnlockUrlAsync(string[] urls, bool force = true, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return UnlockUrl(urls, force); }, ct);

        public static Task<SvnResult> AddToChangelistAsync(string[] paths, string changelistName, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return AddToChangelist(paths, changelistName); }, ct);

        public static Task<SvnResult> RemoveFromChangelistAsync(string[] paths, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return RemoveFromChangelist(paths); }, ct);

        public static Task<SvnResult> MoveBetweenChangelistsAsync(string[] paths, string fromCl, string toCl, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return MoveBetweenChangelists(paths, fromCl, toCl); }, ct);

        public static Task<Dictionary<string, List<string>>> GetAllChangelistMembershipAsync(string workingCopyRoot = null, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return GetAllChangelistMembership(workingCopyRoot); }, ct);

        public static Task<Dictionary<string, SvnStatusEntry>> GetAllStatusEntriesAsync(string workingCopyRoot = null, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return GetAllStatusEntries(workingCopyRoot); }, ct);

        public static Task<SvnResult> CommitAsync(string[] paths, string message,
                                                    int timeoutSeconds = 300, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Commit(paths, message, timeoutSeconds); }, ct);

        /// <summary>Streaming commit (0.2.88): svn's Adding/Sending/Transmitting lines reach
        /// <paramref name="lineProgress"/>. The token only says whether the caller still wants the
        /// result; it never reaches the wire, because a window action must not abort a commit.</summary>
        public static async Task<SvnResult> CommitAsync(string[] paths, string message,
                                                          CancellationToken ct, IProgress<string> lineProgress)
        {
            if (paths == null || paths.Length == 0 || paths.All(p => string.IsNullOrWhiteSpace(p)))
            {
                throw new ArgumentException(
                    "paths must be non-empty. Bare 'svn commit -m' from automation is forbidden " +
                    "per CLAUDE.md hard rule.", nameof(paths));
            }
            if (message == null) throw new ArgumentNullException(nameof(message));
            var filtered = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
            if (!client.IsAvailable) return IpcUnavailable("mutate.commit");
            FRGVersionControl.Core.IpcClient.IpcRawResult raw;
            try
            {
                raw = await client.CommitAsync(filtered, message, lineProgress).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new SvnResult { success = false, error = $"IPC mutate.commit threw: {ex.Message} — Refresh status before retrying." };
            }
            ct.ThrowIfCancellationRequested();
            if (!raw.Ok) return new SvnResult { success = false, error = FormatIpcError(raw, "mutate.commit") };
            var parsed = ParseCommitResult(raw.RawLine);
            return new SvnResult
            {
                success = true,
                output = DescribeCommit(parsed.revision, parsed.includedAncestors),
                revision = parsed.revision,
                includedAncestors = parsed.includedAncestors ?? new string[0],
            };
        }

        [Serializable]
        private sealed class IpcCommitEnvelope
        {
            public IpcCommitResult result;
        }

        [Serializable]
        private sealed class IpcCommitResult
        {
            public long revision;
            public string[] committedPaths;
            public string[] includedAncestors;
        }

        private static IpcCommitResult ParseCommitResult(string rawLine)
        {
            try
            {
                var env = UnityEngine.JsonUtility.FromJson<IpcCommitEnvelope>(rawLine);
                return env?.result ?? new IpcCommitResult();
            }
            catch
            {
                return new IpcCommitResult();
            }
        }

        /// <summary>Banner-ready summary: "r12", "r12 (+2 parent folder(s): Assets/New, Assets/New/Sub)",
        /// or "Nothing to commit." when svn created no revision.</summary>
        public static string DescribeCommit(long revision, string[] includedAncestors)
        {
            if (revision <= 0) return "Nothing to commit.";
            var s = "r" + revision;
            if (includedAncestors != null && includedAncestors.Length > 0)
            {
                s += $" (+{includedAncestors.Length} parent folder(s): {string.Join(", ", includedAncestors)})";
            }
            return s;
        }

        public static Task<SvnResult> RevertAsync(string[] paths, bool alsoRevertMeta = true, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Revert(paths, alsoRevertMeta); }, ct);

        public static Task<SvnResult> AddAsync(string[] paths, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Add(paths); }, ct);

        public static Task<SvnResult> AddForCommitAsync(string[] paths, HashSet<string> lockableExtensions, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return AddForCommit(paths, lockableExtensions); }, ct);

        public static Task<SvnResult> MoveAsync(string srcPath, string dstPath, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Move(srcPath, dstPath); }, ct);

        public static Task<SvnResult> DeleteAsync(string[] paths, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Delete(paths); }, ct);

        public static Task<SvnResult> UpdateAsync(string path = null,
                                                    CancellationToken ct = default,
                                                    int timeoutSeconds = 300)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Update(path, timeoutSeconds); }, ct);

        public static Task<SvnResult> CheckoutAsync(string repoUrl, string targetDirectory,
                                                     string username = null, int timeoutSeconds = 600,
                                                     CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Checkout(repoUrl, targetDirectory, username, timeoutSeconds); }, ct);

        public static Task<SvnResult> ResolveAsync(string path, ResolveAccept accept, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Resolve(path, accept); }, ct);

        public static Task<SvnResult> CleanupAsync(string workingCopyRoot = null, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Cleanup(workingCopyRoot); }, ct);

        public static Task<SvnResult> VacuumPristinesAsync(string workingCopyRoot = null,
                                                            int timeoutSeconds = 300,
                                                            CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return VacuumPristines(workingCopyRoot, timeoutSeconds); }, ct);

        // ====================================================================
        // IPC bridge helpers (private)
        // ====================================================================

        private static SvnResult ExecuteIpcLock(string[] paths, string message, bool steal)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return IpcUnavailable("lock");
                var pair = client.TryLockAsync(paths, message, steal).GetAwaiter().GetResult();
                if (!pair.HasValue) return IpcUnavailable("lock");
                return new SvnResult
                {
                    success = pair.Value.success,
                    output = pair.Value.success ? "lock acquired (ipc)" : "",
                    error = pair.Value.error ?? "",
                };
            }
            catch (Exception ex)
            {
                return new SvnResult { success = false, error = $"IPC lock threw: {ex.Message}" };
            }
        }

        private static SvnResult ExecuteIpcUnlock(string[] paths, bool force)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return IpcUnavailable("unlock");
                var pair = client.TryUnlockAsync(paths, force).GetAwaiter().GetResult();
                if (!pair.HasValue) return IpcUnavailable("unlock");
                return new SvnResult
                {
                    success = pair.Value.success,
                    output = pair.Value.success ? "lock released (ipc)" : "",
                    error = pair.Value.error ?? "",
                };
            }
            catch (Exception ex)
            {
                return new SvnResult { success = false, error = $"IPC unlock threw: {ex.Message}" };
            }
        }

        private static SvnResult ExecuteIpcGeneric(string op, string[] paths, string extraArgsJson)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return IpcUnavailable(op);
                var pair = client.TryInvokeWithPathsAsync(op, paths, extraArgsJson, default, MainThreadBudget(op)).GetAwaiter().GetResult();
                if (!pair.HasValue) return IpcUnavailable(op);
                return new SvnResult
                {
                    success = pair.Value.success,
                    output = pair.Value.success ? op + " ok (ipc)" : "",
                    error = pair.Value.error ?? "",
                };
            }
            catch (Exception ex)
            {
                return new SvnResult { success = false, error = $"IPC {op} threw: {ex.Message}" };
            }
        }

        private static SvnResult ExecuteIpcArgless(string op)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return IpcUnavailable(op);
                var ctx = client.BuildDefaultContext();
                var result = client.InvokeRawAsync(op, "{}", ctx, default, null, MainThreadBudget(op)).GetAwaiter().GetResult();
                if (result.Ok) return new SvnResult { success = true, output = op + " ok (ipc)" };
                return new SvnResult { success = false, error = FormatIpcError(result, op) };
            }
            catch (Exception ex)
            {
                return new SvnResult { success = false, error = $"IPC {op} threw: {ex.Message}" };
            }
        }

        private static SvnResult IpcUnavailable(string op) => new SvnResult
        {
            success = false,
            error = $"svn-core IPC unavailable for {op}. Reinstall the FRG Version Control " +
                    "package, or run Build-SvnCore.ps1 from the toolset repo root.",
        };

        private static string FormatIpcError(IpcRawResult result, string op)
        {
            if (result == null) return $"{op}: null IPC result";
            return !string.IsNullOrEmpty(result.ErrorDefaultAction)
                ? $"{result.ErrorMessage} — {result.ErrorDefaultAction}"
                : result.ErrorMessage ?? result.ErrorKind ?? $"{op} failed";
        }

        private static string IpcEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        [Serializable]
        private sealed class IpcInfoShim
        {
            public IpcInfoResultShim result;
        }

        [Serializable]
        private sealed class IpcInfoResultShim
        {
            public string url;
            public string repoRoot;
            public string repoUuid;
            public long revision;
            public string schedule;
            public string lockToken;
            public string lockOwner;
        }

        [Serializable]
        private sealed class IpcReconcileShim
        {
            public IpcReconcileResultShim result;
        }

        [Serializable]
        private sealed class IpcReconcileResultShim
        {
            public string[] reverted;
            public string[] deleted;
            public IpcReconcileFail[] failed;
        }

        [Serializable]
        private sealed class IpcPrepareDeleteShim
        {
            public IpcPrepareDeleteResultShim result;
        }

        [Serializable]
        private sealed class IpcPrepareDeleteResultShim
        {
            public string[] reverted;
            public IpcPrepareDeleteVersioned[] versioned;
            public string[] unversioned;
            public IpcReconcileFail[] failed;
        }

    }

    // ========================================================================
    // Supporting types
    // ========================================================================

    public sealed class PropListResult
    {
        public bool success;
        public string error;
        public Dictionary<string, string> props = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public class SvnResult
    {
        public long revision;
        public string[] includedAncestors;
        public bool success;
        public int exitCode;
        public string output;
        public string error;
        public override string ToString()
        {
            return success ? output : $"Error (exit {exitCode}): {error}";
        }
    }

    public enum SvnFileStatus
    {
        None,
        Untracked,
        Modified,
        Added,
        Deleted,
        Replaced,
        Conflict,
        Ignored,
        /// <summary>File is missing on disk but still in wc.db (the `!` flag in `svn status`).
        /// Typically caused by an external tool (Bash mv/rm, terminal, etc.) deleting or
        /// moving the file without going through `svn rm` / `svn move`. The FRG Version Control
        /// window surfaces these via an external-divergence reconciler banner.</summary>
        Missing,
    }

    public enum LockState
    {
        None,
        K,
        O,
        B,
        T,
    }

    public class SvnStatusEntry
    {
        public string path;
        public SvnFileStatus status;
        public LockState lockState;
        public string lockOwner;
        public string changelist;
        public int revision;
        public bool outOfDate;
    }

    public class SvnLockInfo
    {
        public string path;
        public LockState state;
        public string lockOwner;
        public string lockMessage;
        public DateTime? lockCreated;
    }

    public class SvnInfo
    {
        public string path;
        public string repoUrl;
        public string repoUuid;
        public int revision;
        public string lockToken;
        public string lockOwner;
    }

    /// <summary>One path that mutate.reconcileMissing could neither revert nor delete.</summary>
    [Serializable]
    public class IpcReconcileFail
    {
        public string path;
        public string error;
    }

    /// <summary>Result of <see cref="SvnCommandRunner.ReconcileMissing"/>. The core decided the
    /// verb per path; <c>reverted</c> = orphaned uncommitted adds un-scheduled, <c>deleted</c> =
    /// committed entries scheduled for removal, <c>failed</c> = per-path failures.</summary>
    public class ReconcileMissingResult
    {
        public bool success;
        public string error;
        public string[] reverted = new string[0];
        public string[] deleted = new string[0];
        public IpcReconcileFail[] failed = new IpcReconcileFail[0];
    }

    [Serializable]
    public class IpcPrepareDeleteVersioned
    {
        public string path;
        public string schedule;
    }

    /// <summary>Result of <see cref="SvnCommandRunner.PrepareDelete"/>. <c>reverted</c> =
    /// schedule-add nodes the core un-scheduled (engine OS-deletes), <c>unversioned</c> = not
    /// in SVN at all (engine OS-deletes), <c>versioned</c> = committed nodes left untouched
    /// (engine runs its lock-then-delete flow), <c>failed</c> = per-path svn info/revert
    /// failures. Paths are echoed exactly as passed.</summary>
    public class PrepareDeleteResult
    {
        public bool success;
        public string error;
        public string[] reverted = new string[0];
        public IpcPrepareDeleteVersioned[] versioned = new IpcPrepareDeleteVersioned[0];
        public string[] unversioned = new string[0];
        public IpcReconcileFail[] failed = new IpcReconcileFail[0];

        public bool WasReverted(string path) => Contains(reverted, path);
        public bool IsUnversioned(string path) => Contains(unversioned, path);

        public bool IsVersioned(string path)
        {
            foreach (var v in versioned ?? new IpcPrepareDeleteVersioned[0])
            {
                if (v != null && string.Equals(v.path, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public IpcReconcileFail FailureFor(string path)
        {
            foreach (var f in failed ?? new IpcReconcileFail[0])
            {
                if (f != null && string.Equals(f.path, path, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return null;
        }

        private static bool Contains(string[] arr, string path)
        {
            foreach (var p in arr ?? new string[0])
            {
                if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    public enum ResolveAccept
    {
        Working,
        Base,
        MineFull,
        TheirsFull,
    }

    public class SvnLogEntry
    {
        public int revision;
        public string author;
        public string date;
        public string message;
    }
}
