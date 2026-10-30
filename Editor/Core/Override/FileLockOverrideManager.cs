using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.UI;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Core.OverrideMode
{
    /// <summary>
    /// Manager for the locked-but-locally-editable override workflow (Singleton).
    ///
    /// The override lets a user edit a file another user holds the SVN lock on: the file is made
    /// writable and parked in the `ignore-on-commit` changelist; svn:needs-lock enforcement on the
    /// server is what keeps it from being committed (Phase 1 Addendum Test 1 `d83c166de76d`).
    ///
    /// Since 0.2.92 every step runs in svn-core (override.begin / override.end / override.forget,
    /// SVN_CORE_API.md §5.8, §6.6): the registry entry is written FIRST, then the read-only flag is
    /// cleared, then the changelist is set, and a failed start is rolled back by the core. This
    /// class keeps the user-facing checks and an in-memory snapshot of the registry so per-frame
    /// queries (Project window overlay, menus) never touch IPC.
    /// </summary>
    public class FileLockOverrideManager
    {
        private static readonly object s_InstanceLock = new object();
        private static volatile FileLockOverrideManager s_Instance;

        public static FileLockOverrideManager Instance
        {
            get
            {
                var instance = s_Instance;
                if (instance != null) return instance;
                lock (s_InstanceLock)
                {
                    if (s_Instance == null)
                    {
                        var created = new FileLockOverrideManager();
                        created.Initialize();
                        s_Instance = created;
                    }
                    return s_Instance;
                }
            }
        }

        public const string LoadingError = "Override state is still loading. Try again in a moment.";
        private const double RetryUnloadedSeconds = 10.0;

        private sealed class Snapshot
        {
            public readonly List<OverrideEntry> Entries;
            public readonly HashSet<string> Paths;

            public Snapshot(List<OverrideEntry> entries)
            {
                Entries = entries;
                Paths = new HashSet<string>(entries.Select(e => PathKey.Normalize(e.path)), PathKey.Comparer);
            }
        }

        private volatile Snapshot m_Snapshot;
        private bool m_Initialized;
        private double m_NextRetryAt;

        public bool IsInitialized => m_Initialized;

        /// <summary>True once the registry has been read from svn-core at least once.</summary>
        public bool IsLoaded => m_Snapshot != null;

        public void Initialize()
        {
            if (RefreshFromCore())
            {
                Debug.Log($"[FRGVersionControl.IgnoreOnCommit] Initialized. Active entries: {m_Snapshot.Entries.Count}");
            }
            else
            {
                Debug.LogWarning("[FRGVersionControl.IgnoreOnCommit] Override state could not be read yet; retrying shortly.");
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    EditorApplication.update -= RetryWhileUnloaded;
                    EditorApplication.update += RetryWhileUnloaded;
                }, null);
            }
            m_Initialized = true;
        }

        private void RetryWhileUnloaded()
        {
            if (IsLoaded)
            {
                EditorApplication.update -= RetryWhileUnloaded;
                return;
            }
            if (EditorApplication.timeSinceStartup < m_NextRetryAt) return;
            m_NextRetryAt = EditorApplication.timeSinceStartup + RetryUnloadedSeconds;
            if (RefreshFromCore()) EditorApplication.update -= RetryWhileUnloaded;
        }

        /// <summary>Re-reads override.list. A failed read keeps the previous snapshot (an IPC
        /// failure is never treated as "no overrides"). Returns whether the read succeeded.</summary>
        public bool RefreshFromCore()
        {
            try
            {
                var client = FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return false;
                var r = client.InvokeRawAsync("override.list", "{}", client.BuildDefaultContext()).GetAwaiter().GetResult();
                if (!r.Ok) return false;
                var parsed = JsonUtility.FromJson<ListEnvelope>(r.RawLine);
                Publish(parsed?.result?.entries);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FRGVersionControl.IgnoreOnCommit] Reading override state failed: {e.Message}");
                return false;
            }
        }

        private void Publish(OverrideEntry[] entries)
        {
            m_Snapshot = new Snapshot(entries != null ? entries.ToList() : new List<OverrideEntry>());
        }

        // -------------------- query API (snapshot only, no IPC) -----------

        public bool IsInOverrideMode(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            var snap = m_Snapshot;
            return snap != null && snap.Paths.Contains(PathKey.Normalize(assetPath));
        }

        public OverrideEntry GetOverride(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var snap = m_Snapshot;
            return snap?.Entries.FirstOrDefault(e => PathKey.Same(e.path, assetPath));
        }

        public List<OverrideEntry> GetAllOverrides()
        {
            var snap = m_Snapshot;
            return snap != null ? new List<OverrideEntry>(snap.Entries) : new List<OverrideEntry>();
        }

        // -------------------- begin / end --------------------------------

        /// <summary>
        /// Mark a file as Ignore on Commit. Refuses unless the file is currently locked
        /// by another user.
        /// </summary>
        public bool SetIgnoreOnCommit(string assetPath, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(assetPath))
            {
                error = "Path is null or empty";
                return false;
            }

            if (!FileLockManager.Instance.IsInitialized)
            {
                error = "FileLockManager not initialized";
                return false;
            }

            if (!EnsureLoaded(out error)) return false;

            // Refresh lock cache so we're operating on current state
            FileLockManager.Instance.RefreshLockCache();

            if (IsInOverrideMode(assetPath))
            {
                error = $"Ignore on Commit is already active for '{assetPath}'";
                return false;
            }

            if (!FileLockManager.Instance.IsLockedByOther(assetPath))
            {
                error = "Ignore on Commit only makes sense when the file is locked by another user. " +
                        "If you want to edit the file, lock it normally instead.";
                return false;
            }

            var existingLock = FileLockManager.Instance.GetFileLock(assetPath);
            var args = new StringBuilder("{\"paths\":[\"").Append(SvnCommandRunner.IpcEscape(assetPath)).Append("\"]");
            if (!string.IsNullOrEmpty(existingLock?.owner)) args.Append(",\"lockHolder\":\"").Append(SvnCommandRunner.IpcEscape(existingLock.owner)).Append('"');
            if (!string.IsNullOrEmpty(existingLock?.lockId)) args.Append(",\"lockId\":\"").Append(SvnCommandRunner.IpcEscape(existingLock.lockId)).Append('"');
            args.Append('}');

            var outcome = Invoke("override.begin", args.ToString());
            if (!outcome.Delivered(out error)) return false;

            var body = JsonUtility.FromJson<ChangeEnvelope>(outcome.Raw.RawLine)?.result;
            if (body?.snapshot != null) Publish(body.snapshot);
            var failure = body?.failed?.FirstOrDefault(f => PathKey.Same(f.path, assetPath));
            if (failure != null)
            {
                error = FailureText(failure);
                FileLockProjectWindowOverlay.RepaintProjectWindow();
                return false;
            }
            if (body?.alreadyOverridden != null && body.alreadyOverridden.Any(p => PathKey.Same(p, assetPath)))
            {
                error = $"Ignore on Commit is already active for '{assetPath}'";
                return false;
            }

            FileLockProjectWindowOverlay.RepaintProjectWindow();
            Debug.Log($"[FRGVersionControl.IgnoreOnCommit] SetIgnoreOnCommit: {assetPath}");
            return true;
        }

        /// <summary>
        /// Clear Ignore on Commit and discard local changes (restores upstream version).
        /// svn-core reverts only a path that has a registry entry, and keeps the entry and the
        /// edits when the revert fails.
        /// </summary>
        public bool ClearIgnoreOnCommit(string assetPath, out string error)
        {
            error = null;
            if (!EnsureLoaded(out error)) return false;
            if (GetOverride(assetPath) == null)
            {
                error = $"No active ignore-on-commit entry for '{assetPath}'";
                return false;
            }

            var outcome = Invoke("override.end", "{\"paths\":[\"" + SvnCommandRunner.IpcEscape(assetPath) + "\"]}");
            if (!outcome.Delivered(out error)) return false;

            var body = JsonUtility.FromJson<ChangeEnvelope>(outcome.Raw.RawLine)?.result;
            if (body?.snapshot != null) Publish(body.snapshot);
            FileLockProjectWindowOverlay.RepaintProjectWindow();

            var failure = body?.failed?.FirstOrDefault(f => PathKey.Same(f.path, assetPath));
            if (failure != null)
            {
                error = FailureText(failure);
                return false;
            }
            if (body?.notOverridden != null && body.notOverridden.Any(p => PathKey.Same(p, assetPath)))
            {
                error = $"No active ignore-on-commit entry for '{assetPath}'";
                return false;
            }
            if (body?.warnings != null && body.warnings.Length > 0)
            {
                var text = string.Join("\n\n", body.warnings.Select(w => w.defaultAction));
                foreach (var w in body.warnings)
                {
                    Debug.LogWarning($"[FRGVersionControl.IgnoreOnCommit] {w.message}. {w.defaultAction}");
                }
                EditorUtility.DisplayDialog("Override ended with a warning", text, "OK");
            }

            AssetDatabase.Refresh();
            Debug.Log($"[FRGVersionControl.IgnoreOnCommit] ClearIgnoreOnCommit: {assetPath}");
            return true;
        }

        /// <summary>Drops the registry entry without touching the file (the override ended some other
        /// way, e.g. it was converted into a real lock and committed).</summary>
        public bool Forget(string assetPath, out string error)
        {
            var outcome = Invoke("override.forget", "{\"paths\":[\"" + SvnCommandRunner.IpcEscape(assetPath) + "\"]}");
            if (!outcome.Delivered(out error)) return false;
            var body = JsonUtility.FromJson<ChangeEnvelope>(outcome.Raw.RawLine)?.result;
            if (body?.snapshot != null) Publish(body.snapshot);
            FileLockProjectWindowOverlay.RepaintProjectWindow();
            return true;
        }

        // -------------------- helpers -----------------------------------

        private bool EnsureLoaded(out string error)
        {
            error = null;
            if (IsLoaded || RefreshFromCore()) return true;
            error = LoadingError;
            return false;
        }

        private static string FailureText(OverrideFailure f)
        {
            var message = string.IsNullOrEmpty(f.message) ? "" : f.message.TrimEnd('.') + ". ";
            return message + f.defaultAction;
        }

        private readonly struct Outcome
        {
            public readonly IpcRawResult Raw;
            public readonly string Error;

            public Outcome(IpcRawResult raw, string error)
            {
                Raw = raw;
                Error = error;
            }

            public bool Delivered(out string error)
            {
                error = Error;
                return Error == null;
            }
        }

        /// <summary>Runs a mutating override op through <see cref="SvnCommandRunner.InvokeMutating"/>.
        /// Still-running or a lost pipe means the core may have applied it, so nothing is rolled
        /// back here; the snapshot is re-read shortly instead.</summary>
        private Outcome Invoke(string op, string argsJson)
        {
            var raw = SvnCommandRunner.InvokeMutating(op, argsJson, out var error);
            if (raw != null && (raw.StillRunning || raw.PipeLost)) ScheduleRefreshes();
            else if (error != null && (raw == null || !raw.Busy)) RefreshFromCore();
            return new Outcome(raw, error);
        }

        private void ScheduleRefreshes()
        {
            double start = 0;
            int done = 0;
            void Tick()
            {
                double elapsed = EditorApplication.timeSinceStartup - start;
                if (done == 0 && elapsed >= 3.0) { done = 1; RefreshFromCore(); FileLockProjectWindowOverlay.RepaintProjectWindow(); }
                if (done == 1 && elapsed >= 15.0) { done = 2; RefreshFromCore(); FileLockProjectWindowOverlay.RepaintProjectWindow(); EditorApplication.update -= Tick; }
            }
            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                start = EditorApplication.timeSinceStartup;
                EditorApplication.update += Tick;
            }, null);
        }

        [Serializable]
        private sealed class ListEnvelope
        {
            public ListBody result;
        }

        [Serializable]
        private sealed class ListBody
        {
            public OverrideEntry[] entries;
        }

        [Serializable]
        private sealed class ChangeEnvelope
        {
            public ChangeBody result;
        }

        [Serializable]
        private sealed class ChangeBody
        {
            public string[] alreadyOverridden;
            public string[] notOverridden;
            public string[] removed;
            public OverrideFailure[] failed;
            public OverrideFailure[] warnings;
            public OverrideEntry[] snapshot;
        }
    }

    [Serializable]
    public class OverrideFailure
    {
        public string path;
        public string svnCode;
        public string message;
        public string defaultAction;
    }
}
