using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Services
{
    /// <summary>
    /// Detects when the SVN working copy state changes (commit, update, lock, unlock,
    /// changelist mutation) and triggers an automatic lock-cache refresh.
    ///
    /// Phase 2 Step F: replaces FileLockGitDetectionService. The Git version watched
    /// `.git/refs/heads/main` mtime as a "git pull happened" signal. The SVN equivalent
    /// is `.svn/wc.db` — its mtime advances on every WC-state change, including:
    ///   - svn commit
    ///   - svn update
    ///   - svn lock / svn unlock
    ///   - svn changelist add/remove
    ///   - svn revert
    ///
    /// Higher false-positive rate than the Git case (every commit fires it), but cheap:
    /// the consequence of a false positive is just an extra background `svn status -u`
    /// invocation. Debounced to avoid storms during multi-file commits.
    /// </summary>
    [InitializeOnLoad]
    public static class FileLockSvnDetectionService
    {
        private const float CHECK_INTERVAL_SECONDS = 10f;
        private const float DEBOUNCE_DELAY_SECONDS = 3f;

        private static long s_LastWcDbTime = 0;
        private static double s_LastCheckTime = 0;
        private static double s_DetectionTime = 0;
        private static bool s_PendingRefresh = false;
        private static bool s_IsEnabled = true;

        static FileLockSvnDetectionService()
        {
            EditorApplication.update += OnEditorUpdate;
            // Phase 2 closure Test 1: complementary focus-gain trigger. wc.db mtime polling
            // catches changes that originate INSIDE this WC; focusChanged catches the cross-WC
            // case where another WC modified the lock state but never touched our wc.db. The
            // cost is one extra `svn status -u` per Unity gain-focus event.
            EditorApplication.focusChanged += OnFocusChanged;
            InitializeWcDbTime();
            Debug.Log("[FRGVersionControl] SVN detection service started (10s mtime poll + focus-gain trigger)");
        }

        private static void OnFocusChanged(bool hasFocus)
        {
            if (!hasFocus) return;
            if (!s_IsEnabled || !FileLockManager.AutoLockEnabled || !FileLockManager.Instance.IsInitialized) return;
            // Cheap path: just re-sync. SyncNow is idempotent and the user expects fresh state on focus.
            FileLockSyncService.SyncNow();
            FileLockSyncService.ResetTimer();
            // Phase 4 Step 1: also refresh the status cache so any newly-edited file in another
            // tool surfaces in the FRG Version Control → Changes tab near-instantly on focus return.
            _ = FRGVersionControl.Core.SvnStatusCache.RefreshAsync();
        }

        private static void InitializeWcDbTime()
        {
            string p = GetWcDbPath();
            if (File.Exists(p))
            {
                try { s_LastWcDbTime = new FileInfo(p).LastWriteTime.Ticks; }
                catch (Exception e)
                {
                    Debug.LogWarning($"[FRGVersionControl] Could not initialize wc.db mtime: {e.Message}");
                }
            }
        }

        private static void OnEditorUpdate()
        {
            if (!s_IsEnabled || !FileLockManager.AutoLockEnabled || !FileLockManager.Instance.IsInitialized)
            {
                s_PendingRefresh = false;
                s_LastCheckTime = 0;
                return;
            }

            double currentTime = EditorApplication.timeSinceStartup;
            if (currentTime - s_LastCheckTime < CHECK_INTERVAL_SECONDS) return;
            s_LastCheckTime = currentTime;

            if (s_PendingRefresh)
            {
                if (currentTime - s_DetectionTime >= DEBOUNCE_DELAY_SECONDS)
                {
                    s_PendingRefresh = false;
                    TriggerAutoRefresh();
                }
                return;
            }

            CheckForWcDbUpdate();
        }

        private static string s_WcDbPath;

        private static void CheckForWcDbUpdate()
        {
            WorkingCopyLayout.EnsureFetched();
            string p = GetWcDbPath();
            if (!string.Equals(p, s_WcDbPath, StringComparison.OrdinalIgnoreCase))
            {
                s_WcDbPath = p;
                s_LastWcDbTime = 0;
            }
            if (!File.Exists(p)) return; // not in an SVN WC (unusual)

            try
            {
                long currentMtime = new FileInfo(p).LastWriteTime.Ticks;
                if (s_LastWcDbTime == 0)
                {
                    s_LastWcDbTime = currentMtime;
                    return;
                }
                if (currentMtime != s_LastWcDbTime)
                {
                    Debug.Log("[FRGVersionControl] SVN working-copy state change detected (wc.db mtime advanced)");
                    s_LastWcDbTime = currentMtime;
                    s_DetectionTime = EditorApplication.timeSinceStartup;
                    s_PendingRefresh = true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FRGVersionControl] Failed to check wc.db mtime: {e.Message}");
            }
        }

        private static void TriggerAutoRefresh()
        {
            Debug.Log("[FRGVersionControl] Auto-refreshing locks after SVN WC state change...");
            FileLockSyncService.SyncNow();
            FileLockSyncService.ResetTimer();
        }

        private static string GetWcDbPath()
        {
            return Path.Combine(WorkingCopyLayout.WcRoot, ".svn", "wc.db");
        }

        public static void Enable()
        {
            s_IsEnabled = true;
            InitializeWcDbTime();
            Debug.Log("[FRGVersionControl] SVN detection service enabled");
        }

        public static void Disable()
        {
            s_IsEnabled = false;
            s_PendingRefresh = false;
            Debug.Log("[FRGVersionControl] SVN detection service disabled");
        }

        public static bool IsEnabled => s_IsEnabled;
    }
}
