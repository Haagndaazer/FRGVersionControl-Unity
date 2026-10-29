using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.OverrideMode;

namespace FRGVersionControl.Hooks
{
    [InitializeOnLoad]
    public static class FileLockInitializer
    {
        static FileLockInitializer()
        {
            EditorApplication.delayCall += Initialize;
        }

        private static void Initialize()
        {
            // Initialize the manager
            var manager = FileLockManager.Instance;

            if (manager.IsInitialized)
            {
                // Refresh lock cache on startup
                manager.RefreshLockCache();
            }

            // Phase 2 Stage 3: initialize the ignore-on-commit manager (loads
            // .svn/ignore-on-commit-registry.json sidecar, surfaces parse errors).
            var overrideMgr = FileLockOverrideManager.Instance;
            if (overrideMgr.IsInitialized)
            {
                int active = overrideMgr.GetAllOverrides().Count;
                if (active > 0)
                {
                    // Phase 2 plan §Stage 5 Mode E mitigation: surface forgotten ignore-on-commit
                    // entries on editor startup. For Stage 3 we just log loudly; Stage 4/5 will
                    // wire a non-modal banner via the FileLockWindow tab.
                    Debug.LogWarning($"[FRGVersionControl] You have {active} active ignore-on-commit file(s). " +
                                     "Open Window → FRG Version Control → Changes tab to view.");
                }
            }
        }
    }
}
