using System.IO;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.UI;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Core.OverrideMode
{
    /// <summary>
    /// Mode B happy-path: the user had an override active, the upstream lock was released,
    /// and they want to convert their local edits into a real lock-and-commit.
    ///
    /// Phase 2 Step E SVN port — sequence (per Phase 1 Addendum + plan §5.1):
    ///   (1) `svn lock &lt;path&gt;` — acquires server-authoritative lock
    ///       Failure (W160035 already-locked): roll back nothing — overrride still safe
    ///       (svn:needs-lock enforcement keeps user's commits blocked); leave override active.
    ///   (2) `svn changelist --remove &lt;path&gt;` — clear the override changelist annotation
    ///       so the explicit-path commit below isn't filtered.
    ///   (3) `svn commit &lt;path&gt; -m &lt;msg&gt;` (explicit-path; canonical safe pattern from
    ///       Phase 1 Addendum Test 2 cognition `2c01d819f351`)
    ///   (4) Lock auto-releases on commit by default (Phase 1 finding `3cf9e40b48fa`); if
    ///       the team's client config has no-unlock=yes, the lock survives.
    ///   (5) Drop the override registry entry.
    ///
    /// On Step 1 failure (someone re-locked between release and conversion attempt),
    /// the override is NOT rolled back; the user's edits remain in working-tree under
    /// the changelist annotation, and svn:needs-lock enforcement prevents accidental commit.
    /// </summary>
    public static class OverrideConversion
    {
        public static bool ConvertIgnoreOnCommitToLock(string assetPath, out string error)
        {
            error = null;

            var entry = FileLockOverrideManager.Instance.GetOverride(assetPath);
            if (entry == null)
            {
                error = $"No active ignore-on-commit entry for '{assetPath}'";
                return false;
            }

            // Confirm with the user (this command commits — make sure they want to)
            string fileName = Path.GetFileName(assetPath);
            bool confirmed = EditorUtility.DisplayDialog(
                "Convert Ignore on Commit to Lock",
                $"Convert your local edits to '{fileName}' into a real lock + commit?\n\n" +
                "This will:\n" +
                "  1. Acquire an SVN lock\n" +
                "  2. Remove the file from the ignore-on-commit changelist\n" +
                "  3. Commit your changes (explicit-path commit)\n" +
                "  4. Drop the ignore-on-commit entry\n" +
                "  5. Lock auto-releases on commit (default SVN behavior)\n\n" +
                "If someone else has re-locked the file between the original release and now, " +
                "the conversion will be cancelled and your local edits left in place.",
                "Convert", "Cancel");

            if (!confirmed)
            {
                error = "Cancelled by user";
                return false;
            }

            // Step 1: acquire SVN lock
            var lockResult = SvnCommandRunner.Lock(new[] { assetPath },
                "[FRGVersionControl] convert ignore-on-commit to lock");
            if (!lockResult.success)
            {
                error = $"Lock conflict: {lockResult.error ?? lockResult.output}\n" +
                        "Your ignore-on-commit entry has been preserved — try again later or contact the lock holder.";
                Debug.LogWarning($"[FRGVersionControl.IgnoreOnCommit] Convert cancelled — lock conflict: {error}");
                return false;
            }

            // Step 2: remove from ignore-on-commit changelist (so explicit-path commit
            // isn't filtered out by changelist filtering on the SVN client side)
            var removeCl = SvnCommandRunner.RemoveFromChangelist(new[] { assetPath });
            if (!removeCl.success)
            {
                Debug.LogWarning($"[FRGVersionControl.IgnoreOnCommit] svn changelist --remove failed " +
                                 $"(non-fatal — explicit-path commit will proceed): {removeCl.error}");
            }

            // Step 3: explicit-path commit (canonical safe pattern)
            string commitMsg = $"Convert ignore-on-commit to commit: {assetPath}";
            var commit = SvnCommandRunner.Commit(new[] { assetPath }, commitMsg);
            if (!commit.success)
            {
                error = $"svn commit failed: {commit.error ?? commit.output}\n" +
                        "The lock was acquired but the commit failed. Resolve the issue and retry " +
                        "via Window/FRG Version Control/Locks tab.";
                Debug.LogError($"[FRGVersionControl.IgnoreOnCommit] Convert: commit failed (lock retained): {error}");
                return false;
            }

            // Step 4 (auto): lock auto-releases on commit by default. No explicit unlock needed.

            // Step 5: drop the override registry entry without touching the file (override.forget):
            // ClearIgnoreOnCommit would revert, but the committed changes must stay.
            if (!FileLockOverrideManager.Instance.Forget(assetPath, out var forgetError))
            {
                Debug.LogWarning($"[FRGVersionControl.IgnoreOnCommit] Convert: committed, but the override entry for " +
                                 $"'{assetPath}' could not be removed: {forgetError}");
            }

            // Refresh + repaint
            FileLockManager.Instance.RefreshLockCache();
            FileLockProjectWindowOverlay.RepaintProjectWindow();

            Debug.Log($"[FRGVersionControl.IgnoreOnCommit] Converted to lock+commit: {assetPath}");
            return true;
        }
    }
}
