using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Core.OverrideMode
{
    /// <summary>
    /// Periodic reconciliation of override-mode invariants.
    ///
    /// Phase 2 Step E SVN port — surface area shrunk vs the LFS version:
    ///   Mode A — upstream changed file while override active. Detected via the
    ///            `outOfDate` flag from `svn status -u --xml`. User chooses Discard or Cancel.
    ///   Mode B — upstream lock released while override active. Detected via diff between
    ///            registry's lock_holder_at_start and current StatusUpstreamAll lock map.
    ///            Non-modal notification with prompt to convert to lock+commit.
    ///   Mode F (silent restore-source clobber) — DROPPED. Depended on stash file
    ///            (svn shelve unavailable in 1.14.5; Phase 1 Addendum Test 3).
    ///   Mode G (read-only drift)               — DROPPED. The only paths that re-apply
    ///            svn:needs-lock read-only are svn revert (intentional end-of-override) and
    ///            svn update bringing in a fresh upstream copy (Mode A territory).
    ///   Mode H (staged-deletion leak)          — DROPPED. SVN has no index; analogous
    ///            failure mode does not exist on this stack.
    ///
    /// Called from FileLockSyncService at the end of each sync cycle (default 5 min).
    /// Mode B is invoked separately (after RefreshLockCache) so it sees the freshest lock map.
    /// </summary>
    public static class OverrideReconciler
    {
        private const string SUPPRESS_MODE_A_PREF = "FRGVersionControl.IgnoreOnCommit.SuppressModeAModal";

        public static void RunOnce()
        {
            if (!FileLockOverrideManager.Instance.IsInitialized) return;

            var entries = FileLockOverrideManager.Instance.GetAllOverrides();
            if (entries.Count == 0) return;

            foreach (var entry in entries)
            {
                CheckModeA_UpstreamChanged(entry);
            }
        }

        // -------------------- Mode A: upstream changed file ----------------------

        private static void CheckModeA_UpstreamChanged(OverrideEntry entry)
        {
            // `svn status -u --xml <path>` reports `repos-status item="modified"` and
            // sets the `outOfDate` flag if HEAD is newer than working-copy BASE for this path.
            SvnStatusEntry status;
            try
            {
                status = SvnCommandRunner.StatusUpstream(entry.path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FRGVersionControl.IgnoreOnCommit] Mode A status failed for '{entry.path}': {e.Message}");
                return;
            }
            if (status == null || !status.outOfDate) return;

            // Notify once per session per (file, current upstream rev) tuple.
            // We don't have direct access to the head revision, so we use the post-edit
            // hash + path as the dedup key — it changes if the user edits more, which
            // resets the prompt (acceptable: re-confirms divergence after their re-save).
            string sessionKey = $"FRGVersionControl.IgnoreOnCommit.NotifiedModeA.{entry.path}";
            if (SessionState.GetBool(sessionKey, false)) return;
            SessionState.SetBool(sessionKey, true);

            if (EditorPrefs.GetBool(SUPPRESS_MODE_A_PREF, false))
            {
                Debug.Log($"[FRGVersionControl.IgnoreOnCommit] [suppressed] Mode A: upstream changed " +
                          $"'{entry.path}' (was r{entry.pre_ignore_on_commit_revision}) while you have " +
                          "ignore-on-commit active. Modal suppressed by user preference.");
                return;
            }

            int choice = EditorUtility.DisplayDialogComplex(
                "Ignore-on-commit conflicts upstream",
                $"The file with ignore-on-commit active has changed upstream.\n\n" +
                $"Path: {entry.path}\n" +
                $"Your ignore-on-commit started at revision: r{entry.pre_ignore_on_commit_revision}\n\n" +
                "What do you want to do?",
                "Discard local edits (svn revert + accept upstream)",  // 0
                "Cancel — decide later",                               // 1
                "Suppress this dialog");                               // 2

            if (choice == 0)
            {
                string err;
                if (!FileLockOverrideManager.Instance.ClearIgnoreOnCommit(entry.path, out err))
                {
                    Debug.LogError($"[FRGVersionControl.IgnoreOnCommit] Mode A ClearIgnoreOnCommit failed: {err}");
                }
                else
                {
                    // After ClearIgnoreOnCommit the file is reverted; an `svn update` brings in
                    // the upstream version. Best-effort — failures non-fatal.
                    var update = SvnCommandRunner.Update(entry.path);
                    if (!update.success)
                    {
                        Debug.LogWarning($"[FRGVersionControl.IgnoreOnCommit] Mode A: svn update failed " +
                                         $"after revert (run manually): {update.error}");
                    }
                }
            }
            else if (choice == 2)
            {
                EditorPrefs.SetBool(SUPPRESS_MODE_A_PREF, true);
            }
            // choice == 1: nothing — user keeps working in override mode.
        }

        // -------------------- Mode B: lock released upstream ---------------------
        //
        // Called from FileLockSyncService AFTER RefreshLockCache so it sees the new state.
        // Phase 2 Step E: switched from lock_id_at_start (LFS lock IDs were stable cross-WC)
        // to lock_holder_at_start. SVN lock tokens are working-copy-internal, so we match by
        // owner identity instead.

        public static void CheckModeB_LockReleasedUpstream()
        {
            if (!FileLockOverrideManager.Instance.IsInitialized) return;
            var entries = FileLockOverrideManager.Instance.GetAllOverrides();
            if (entries.Count == 0) return;

            // Read the freshest lock map directly from the server — don't trust
            // FileLockManager's cache here (Sync may have just refreshed it, but this is
            // a safety net for the case where the cache disagrees with reality).
            Dictionary<string, SvnLockInfo> currentLocks;
            try
            {
                currentLocks = SvnCommandRunner.StatusUpstreamAll(null);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FRGVersionControl.IgnoreOnCommit] Mode B status failed: {e.Message}");
                return;
            }
            if (currentLocks == null) return;

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.lock_holder_at_start)) continue;

                // Try both backslash and forward-slash variants (SVN emits backslash on Windows).
                string svnPath = entry.path.Replace('/', '\\');
                SvnLockInfo current = null;
                if (currentLocks.TryGetValue(svnPath, out current) ||
                    currentLocks.TryGetValue(entry.path, out current))
                {
                    // still locked — check if same holder
                    if (current != null &&
                        current.state != LockState.None &&
                        string.Equals(current.lockOwner, entry.lock_holder_at_start,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // same person still holds it
                    }
                }
                // Not in current lock map (or different holder) — original lock released.

                string sessionKey = $"FRGVersionControl.IgnoreOnCommit.NotifiedModeB.{entry.path}.{entry.lock_holder_at_start}";
                if (SessionState.GetBool(sessionKey, false)) continue;
                SessionState.SetBool(sessionKey, true);

                Debug.Log($"[FRGVersionControl.IgnoreOnCommit] Mode B: lock released upstream for " +
                          $"'{entry.path}' (was held by '{entry.lock_holder_at_start}'). " +
                          "Consider converting your ignore-on-commit entry to a real lock + commit.");

                if (EditorWindow.focusedWindow != null)
                {
                    EditorWindow.focusedWindow.ShowNotification(
                        new GUIContent($"Lock released on {Path.GetFileName(entry.path)} — " +
                                       "convert via Tools → File Locking?"));
                }
            }
        }
    }
}
