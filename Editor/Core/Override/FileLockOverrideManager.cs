using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.UI;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Core.OverrideMode
{
    /// <summary>
    /// Manager for the locked-but-locally-editable override workflow (Singleton).
    ///
    /// Phase 2 Step E SVN port. The override mechanic lets a user edit a file that another
    /// user holds the SVN lock on, using `attrib -r` + the `ignore-on-commit` changelist
    /// for visibility. Safety is provided by SVN's server-side `svn:needs-lock` enforcement
    /// — the artist physically cannot commit without the lock token (Phase 1 Addendum
    /// Test 1 cognition `d83c166de76d`).
    ///
    /// Begin:
    ///   attrib -r &lt;path&gt;
    ///   svn changelist ignore-on-commit &lt;path&gt;
    ///   user edits freely
    ///
    /// End:
    ///   svn revert &lt;path&gt;        (discards mods AND restores read-only flag — Phase 1 Mode H)
    ///   svn changelist --remove &lt;path&gt;  (REQUIRED — revert does NOT remove from changelist;
    ///                                     Phase 1 finding `d35deb3f7c01`)
    ///
    /// Stash/Pop METHODS DELETED: `svn shelve` is unavailable in svn 1.14.5 (Phase 1 Addendum
    /// Test 3 finding `11b787c648a3`). Old sidecar entries with stashed=true must be cleared
    /// before Step E toolset use (resolved decision #10).
    /// </summary>
    public class FileLockOverrideManager
    {
        private const string IGNORE_ON_COMMIT_CHANGELIST = "ignore-on-commit";

        private static FileLockOverrideManager s_Instance;
        public static FileLockOverrideManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = new FileLockOverrideManager();
                    s_Instance.Initialize();
                }
                return s_Instance;
            }
        }

        private bool m_Initialized;

        // In-memory path cache so per-frame IsInOverrideMode queries (called from
        // FileLockProjectWindowOverlay.OnProjectWindowItemGUI) don't hit disk.
        // Refreshed on every Begin/End and at Initialize.
        private readonly HashSet<string> m_OverridePathCache = new HashSet<string>();

        public bool IsInitialized => m_Initialized;

        public void Initialize()
        {
            try
            {
                RefreshPathCache();
                Debug.Log($"[FRGVersionControl.IgnoreOnCommit] Initialized. Active entries: {m_OverridePathCache.Count}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[FRGVersionControl.IgnoreOnCommit] Init failed: {e.Message}");
            }
            m_Initialized = true;
        }

        private void RefreshPathCache()
        {
            var snapshot = OverrideRegistryStore.ReadSnapshot();
            m_OverridePathCache.Clear();
            foreach (var entry in snapshot)
            {
                m_OverridePathCache.Add(entry.path);
            }
        }

        // -------------------- query API ----------------------------------

        public bool IsInOverrideMode(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            return m_OverridePathCache.Contains(assetPath);
        }

        public OverrideEntry GetOverride(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            return OverrideRegistryStore.ReadSnapshot().FirstOrDefault(e => e.path == assetPath);
        }

        public List<OverrideEntry> GetAllOverrides()
        {
            return OverrideRegistryStore.ReadSnapshot();
        }

        public string GetSidecarPath() => OverrideRegistryStore.GetSidecarPath();

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

            // Step 1: clear OS read-only flag so the user can edit
            try
            {
                string fullPath = Path.Combine(SvnCommandRunner.ProjectRoot, assetPath);
                if (File.Exists(fullPath))
                {
                    var attrs = File.GetAttributes(fullPath);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(fullPath, attrs & ~FileAttributes.ReadOnly);
                    }
                }
            }
            catch (Exception e)
            {
                error = $"Failed to clear read-only flag: {e.Message}";
                return false;
            }

            // Step 2: add to ignore-on-commit changelist (visibility annotation;
            // server-side svn:needs-lock enforcement is the actual safety property).
            var addCl = SvnCommandRunner.AddToChangelist(new[] { assetPath }, IGNORE_ON_COMMIT_CHANGELIST);
            if (!addCl.success)
            {
                error = $"Failed to add to ignore-on-commit changelist: {addCl.error}";
                // Roll back read-only flag
                TryRestoreReadOnly(assetPath);
                return false;
            }

            // Step 3: capture pre-override revision (WC's BASE rev) + initial post-edit content hash
            var info = SvnCommandRunner.Info(assetPath);
            int preRevision = info != null ? info.revision : 0;

            string fullAssetPath = Path.Combine(SvnCommandRunner.ProjectRoot, assetPath);
            string postSha = SvnCommandRunner.HashSha256(fullAssetPath) ?? "";

            // Step 4: persist
            try
            {
                OverrideRegistryStore.WithLock(entries =>
                {
                    entries.Add(new OverrideEntry(
                        assetPath,
                        preRevision,
                        postSha,
                        existingLock?.lockId ?? "",
                        existingLock?.owner ?? ""));
                });
            }
            catch (Exception e)
            {
                error = $"Failed to persist sidecar: {e.Message}";
                // Roll back: remove from changelist + restore read-only
                SvnCommandRunner.RemoveFromChangelist(new[] { assetPath });
                TryRestoreReadOnly(assetPath);
                return false;
            }

            RefreshPathCache();
            FileLockProjectWindowOverlay.RepaintProjectWindow();
            Debug.Log($"[FRGVersionControl.IgnoreOnCommit] SetIgnoreOnCommit: {assetPath}");
            return true;
        }

        /// <summary>
        /// Clear Ignore on Commit and discard local changes (restores upstream version).
        /// </summary>
        public bool ClearIgnoreOnCommit(string assetPath, out string error)
        {
            error = null;
            var entry = GetOverride(assetPath);
            if (entry == null)
            {
                error = $"No active ignore-on-commit entry for '{assetPath}'";
                return false;
            }

            // Step 1: svn revert — discards local mods.
            //
            // Phase 2 Step E finding (contradicts Phase 1 e494ba79100a): when the WC has a
            // cached lock token AND the file is modified, svn revert restores CONTENT but
            // does NOT re-apply the svn:needs-lock read-only flag. The Phase 1 finding was
            // measured on a clean WC without a cached lock token. We can't rely on revert
            // to restore RO in the override end-state — re-apply it explicitly below.
            var revert = SvnCommandRunner.Revert(new[] { assetPath }, alsoRevertMeta: false);
            if (!revert.success)
            {
                error = $"Failed to revert file: {revert.error}";
                return false;
            }

            // Step 1b: explicitly restore the read-only flag so the file once again signals
            // "you don't hold the lock — don't edit this".
            try
            {
                string fullRevPath = Path.Combine(SvnCommandRunner.ProjectRoot, assetPath);
                if (File.Exists(fullRevPath))
                {
                    var attrs2 = File.GetAttributes(fullRevPath);
                    File.SetAttributes(fullRevPath, attrs2 | FileAttributes.ReadOnly);
                }
            }
            catch (Exception attrEx)
            {
                Debug.LogWarning($"[FRGVersionControl.IgnoreOnCommit] Failed to re-apply read-only after revert " +
                                 $"(non-fatal — file is reverted): {attrEx.Message}");
            }

            // Step 2: remove from ignore-on-commit changelist (REQUIRED — revert does
            // NOT auto-remove from changelist per Phase 1 finding `d35deb3f7c01`).
            var removeCl = SvnCommandRunner.RemoveFromChangelist(new[] { assetPath });
            if (!removeCl.success)
            {
                Debug.LogWarning($"[FRGVersionControl.IgnoreOnCommit] svn changelist --remove failed " +
                                 $"(non-fatal — file content was reverted): {removeCl.error}");
                // Continue anyway; the sidecar entry will be removed below.
            }

            // Step 3: drop from registry
            OverrideRegistryStore.WithLock(entries =>
            {
                entries.RemoveAll(e => e.path == assetPath);
            });

            AssetDatabase.Refresh();
            RefreshPathCache();
            FileLockProjectWindowOverlay.RepaintProjectWindow();
            Debug.Log($"[FRGVersionControl.IgnoreOnCommit] ClearIgnoreOnCommit: {assetPath}");
            return true;
        }

        // -------------------- save-event hook ---------------------------

        /// <summary>
        /// Update the post-edit content hash for an active override after the user saves.
        /// Used by reconciliation Mode F (silent clobber detection).
        /// </summary>
        public void UpdatePostEditHash(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            string fullPath = Path.Combine(SvnCommandRunner.ProjectRoot, assetPath);
            string newHash = SvnCommandRunner.HashSha256(fullPath);
            if (string.IsNullOrEmpty(newHash)) return;

            OverrideRegistryStore.WithLock(entries =>
            {
                var e = entries.FirstOrDefault(x => x.path == assetPath);
                if (e != null)
                {
                    e.post_edit_content_sha256 = newHash;
                }
            });
        }

        // -------------------- helpers -----------------------------------

        private static void TryRestoreReadOnly(string assetPath)
        {
            try
            {
                string fullPath = Path.Combine(SvnCommandRunner.ProjectRoot, assetPath);
                if (File.Exists(fullPath))
                {
                    var attrs = File.GetAttributes(fullPath);
                    File.SetAttributes(fullPath, attrs | FileAttributes.ReadOnly);
                }
            }
            catch { /* swallow — best-effort rollback */ }
        }
    }
}
