using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.OverrideMode;

namespace FRGVersionControl.Services
{
    /// <summary>
    /// Background service for periodic lock synchronization
    /// </summary>
    [InitializeOnLoad]
    public static class FileLockSyncService
    {
        private const float SYNC_INTERVAL_SECONDS = 5 * 60; // 5 minutes
        private static double s_LastSyncTime = 0;
        private static bool s_IsEnabled = true;

        static FileLockSyncService()
        {
            EditorApplication.update += OnEditorUpdate;
            s_LastSyncTime = EditorApplication.timeSinceStartup;

            Debug.Log("[FRGVersionControl] Background sync service started (5-minute interval)");
        }

        private static void OnEditorUpdate()
        {
            if (!s_IsEnabled || !FileLockManager.AutoLockEnabled || !FileLockManager.Instance.IsInitialized)
            {
                s_LastSyncTime = double.NegativeInfinity; // ensure elapsed >= interval on next re-enable check
                return;
            }

            double currentTime = EditorApplication.timeSinceStartup;
            double elapsed = currentTime - s_LastSyncTime;

            if (elapsed >= SYNC_INTERVAL_SECONDS)
            {
                s_LastSyncTime = currentTime;
                SyncLocks();
            }
        }

        private static void SyncLocks()
        {
            Debug.Log("[FRGVersionControl] Background sync: Refreshing lock cache + status cache...");

            bool success = FileLockManager.Instance.RefreshLockCache();

            if (success)
            {
                Debug.Log("[FRGVersionControl] Background sync: Complete");
            }
            else
            {
                Debug.LogWarning("[FRGVersionControl] Background sync: Failed (network issue?)");
            }

            // Phase 4 Step 1: refresh the status cache in the same cycle. Fire-and-forget;
            // SnapshotUpdated event fires on main thread when complete and any subscribed
            // panels (e.g. FileLockPendingChangesWindow) repaint themselves.
            _ = FRGVersionControl.Core.SvnStatusCache.RefreshAsync();

            // Phase 2 Step D: FileLockAutoUnlockService deleted — SVN auto-releases on commit
            // by default (Phase 1 finding 3cf9e40b48fa), making the auto-unlock-via-Git-push
            // detection pattern moot.

            // Phase 2 Step E: OverrideReconciler ported to SVN (Mode A via outOfDate flag,
            // Mode B via StatusUpstreamAll lock map; F/G/H dropped as plan §5.4 specifies).
            FRGVersionControl.Core.OverrideMode.FileLockOverrideManager.Instance.RefreshFromCore();
            FRGVersionControl.Core.OverrideMode.OverrideReconciler.CheckModeB_LockReleasedUpstream();
            FRGVersionControl.Core.OverrideMode.OverrideReconciler.RunOnce();
        }

        /// <summary>
        /// Manually trigger sync (for UI refresh button)
        /// </summary>
        public static void SyncNow()
        {
            SyncLocks();
            s_LastSyncTime = EditorApplication.timeSinceStartup;
        }

        /// <summary>
        /// Reset the sync timer (called after auto-refresh from git detection)
        /// </summary>
        public static void ResetTimer()
        {
            s_LastSyncTime = EditorApplication.timeSinceStartup;
        }

        public static void Enable()
        {
            s_IsEnabled = true;
            s_LastSyncTime = EditorApplication.timeSinceStartup;
        }

        public static void Disable()
        {
            s_IsEnabled = false;
        }
    }
}
