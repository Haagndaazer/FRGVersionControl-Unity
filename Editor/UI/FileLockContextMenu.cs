using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.OverrideMode;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Context menu integration for file locking
    /// </summary>
    public static class FileLockContextMenu
    {
        [MenuItem("Assets/FRG Version Control/Lock File(s)", false, 2000)]
        private static void LockSelectedFiles()
        {
            var selectedPaths = new List<string>();
            
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;
                selectedPaths.Add(path);
            }

            if (selectedPaths.Count == 0)
                return;

            // Use batch operation if multiple files selected, otherwise use single file operation
            if (selectedPaths.Count >= 2)
            {
                // Batch operation
                var result = FileLockManager.Instance.BatchLockFiles(selectedPaths);
                
                // Handle results
                var failed = result.GetFailed();
                if (failed.Count > 0)
                {
                    // Show dialog for failed locks
                    ShowBatchLockErrorDialog(failed);
                }
                
                if (result.successCount > 0)
                {
                    Debug.Log($"[FRGVersionControl] Successfully locked {result.successCount} file(s)");
                }
            }
            else
            {
                // Single file operation (backward compatibility)
                string path = selectedPaths[0];
                FileLockManager.Instance.LockFile(path, out string error);

                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogError($"[FRGVersionControl] Failed to lock {path}: {error}");
                }
            }

            AssetDatabase.Refresh();
        }

        [MenuItem("Assets/FRG Version Control/Unlock File(s)", false, 2001)]
        private static void UnlockSelectedFiles()
        {
            var selectedPaths = new List<string>();
            
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;
                selectedPaths.Add(path);
            }

            if (selectedPaths.Count == 0)
                return;

            // Use batch operation if multiple files selected
            if (selectedPaths.Count >= 2)
            {
                // Check if any files need force unlock
                bool needsForce = false;
                foreach (var path in selectedPaths)
                {
                    var fileLock = FileLockManager.Instance.GetFileLock(path);
                    if (fileLock != null && !FileLockManager.Instance.IsLockedByMe(path))
                    {
                        needsForce = true;
                        break;
                    }
                }

                // Batch unlock
                var result = FileLockManager.Instance.BatchUnlockFiles(selectedPaths, force: needsForce);
                
                // Handle failed unlocks
                var failed = result.GetFailed();
                if (failed.Count > 0)
                {
                    ShowBatchUnlockErrorDialog(failed);
                }
                
                if (result.successCount > 0)
                {
                    Debug.Log($"[FRGVersionControl] Successfully unlocked {result.successCount} file(s)");
                }
            }
            else
            {
                // Single file operation (preserves existing dialog behavior)
                string path = selectedPaths[0];
                var fileLock = FileLockManager.Instance.GetFileLock(path);

                // If file is not locked, nothing to do
                if (fileLock == null)
                {
                    Debug.LogWarning($"[FRGVersionControl] File is not locked: {path}");
                    return;
                }

                // If locked by current user on current machine, unlock normally
                if (FileLockManager.Instance.IsLockedByMe(path))
                {
                    FileLockManager.Instance.UnlockFile(path);
                }
                // If locked by same email on different machine
                else if (fileLock.owner == FileLockManager.Instance.UserName /* Phase 2 LFS: owner == username, not email */)
                {
                    if (FileLockManager.Instance.IsUserAdmin())
                    {
                        // Admin can unlock same-email-different-machine locks
                        if (ShowForceUnlockDialog(fileLock))
                        {
                            FileLockManager.Instance.UnlockFile(path, force: false);
                        }
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(
                            "Cannot Unlock",
                            $"File: {System.IO.Path.GetFileName(path)}\n\n" +
                            $"This file is locked by you on machine '{fileLock.machineName}'.\n\n" +
                            "You can only unlock this file from that machine, or contact an admin for assistance.",
                            "OK");
                    }
                }
                // If locked by different user and user is admin, show force unlock dialog
                else if (FileLockManager.Instance.IsUserAdmin())
                {
                    if (ShowForceUnlockDialog(fileLock))
                    {
                        FileLockManager.Instance.UnlockFile(path, force: true);
                    }
                }
                // If locked by different user and user is NOT admin, show error
                else
                {
                    EditorUtility.DisplayDialog(
                        "Cannot Unlock",
                        $"File: {System.IO.Path.GetFileName(path)}\n\n" +
                        $"This file is locked by {fileLock.ownerName} ({fileLock.owner}).\n\n" +
                        "You can only unlock files that you have locked yourself.",
                        "OK");
                }
            }

            AssetDatabase.Refresh();
        }

        [MenuItem("Assets/FRG Version Control/Revert File(s)", false, 2002)]
        private static void RevertSelectedFiles()
        {
            // Collect files with changes
            var filesToRevert = new System.Collections.Generic.List<string>();
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                if (FRGVersionControl.Core.Svn.SvnCommandRunner.HasUncommittedChanges(path))
                {
                    filesToRevert.Add(path);
                }
            }

            if (filesToRevert.Count == 0)
            {
                Debug.Log("[FRGVersionControl] No files with uncommitted changes to revert.");
                return;
            }

            // Show confirmation dialog
            string fileList = string.Join("\n", filesToRevert.ConvertAll(p => "  • " + p));
            string message = $"Revert uncommitted changes for {filesToRevert.Count} file(s)?\n\n" +
                           "This will discard all local changes and cannot be undone.\n\n" +
                           $"Files to revert:\n{fileList}";

            bool confirmed = EditorUtility.DisplayDialog(
                "Revert Files",
                message,
                "Revert",
                "Cancel");

            if (!confirmed)
            {
                Debug.Log("[FRGVersionControl] Revert cancelled by user.");
                return;
            }

            // Revert each file
            int successCount = 0;
            foreach (string path in filesToRevert)
            {
                bool reverted = FRGVersionControl.Core.Svn.SvnCommandRunner.Revert(new[] { path }, alsoRevertMeta: true).success;
                if (reverted)
                {
                    successCount++;
                    Debug.Log($"[FRGVersionControl] Reverted: {path}");
                }
                else
                {
                    Debug.LogError($"[FRGVersionControl] Failed to revert: {path}");
                }
            }

            AssetDatabase.Refresh();
            Debug.Log($"[FRGVersionControl] Successfully reverted {successCount} of {filesToRevert.Count} file(s).");
        }

        [MenuItem("Assets/FRG Version Control/Revert and Unlock File(s)", false, 2003)]
        private static void RevertAndUnlockSelectedFiles()
        {
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                FileLockManager.Instance.RevertAndUnlockFile(path);
            }

            AssetDatabase.Refresh();
        }

        [MenuItem("Assets/FRG Version Control/View Lock Info", false, 2004)]
        private static void ViewLockInfo()
        {
            if (Selection.objects.Length == 0) return;

            string path = AssetDatabase.GetAssetPath(Selection.objects[0]);
            var fileLock = FileLockManager.Instance.GetFileLock(path);

            if (fileLock == null)
            {
                EditorUtility.DisplayDialog("Not Locked", $"{path}\n\nThis file is not currently locked.", "OK");
            }
            else
            {
                var lockTime = fileLock.GetLockedAtTime();
                var elapsed = System.DateTime.UtcNow - lockTime;

                EditorUtility.DisplayDialog("Lock Info",
                    $"File: {path}\n\n" +
                    $"Owner: {fileLock.ownerName}\n" +
                    $"Email: {fileLock.owner}\n" +
                    $"Machine: {fileLock.machineName}\n" +
                    $"Locked: {elapsed.TotalHours:F1} hours ago\n" +
                    $"Status: {(fileLock.IsStale() ? "STALE" : "Active")}",
                    "OK");
            }
        }

        // ----------------- Phase 2 Stage 4.3: Ignore-on-commit commands -----------------

        [MenuItem("Assets/FRG Version Control/Set Ignore on Commit", false, 2010)]
        private static void SetIgnoreOnCommitOnSelected()
        {
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                string err;
                if (!FileLockOverrideManager.Instance.SetIgnoreOnCommit(path, out err))
                {
                    EditorUtility.DisplayDialog("Set Ignore on Commit Failed",
                        $"Could not set ignore-on-commit for '{System.IO.Path.GetFileName(path)}':\n\n{err}", "OK");
                }
            }

            AssetDatabase.Refresh();
            FileLockProjectWindowOverlay.RepaintProjectWindow();
        }

        [MenuItem("Assets/FRG Version Control/Set Ignore on Commit", true)]
        private static bool ValidateSetIgnoreOnCommit()
        {
            if (Selection.objects.Length == 0 || !FileLockManager.Instance.IsInitialized)
                return false;

            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                if (FileLockManager.Instance.IsLockedByOther(path) &&
                    !FileLockOverrideManager.Instance.IsInOverrideMode(path))
                {
                    return true;
                }
            }
            return false;
        }

        [MenuItem("Assets/FRG Version Control/Clear Ignore on Commit", false, 2011)]
        private static void ClearIgnoreOnCommitOnSelected()
        {
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                if (!FileLockOverrideManager.Instance.IsInOverrideMode(path)) continue;

                bool ok = EditorUtility.DisplayDialog(
                    "Clear Ignore on Commit?",
                    $"Clear ignore-on-commit for '{System.IO.Path.GetFileName(path)}'?\n\n" +
                    "Local changes will be discarded and the file restored to the upstream version.",
                    "Clear", "Cancel");
                if (!ok) continue;

                string err;
                if (!FileLockOverrideManager.Instance.ClearIgnoreOnCommit(path, out err))
                {
                    EditorUtility.DisplayDialog("Clear Ignore on Commit Failed",
                        $"Could not clear ignore-on-commit for '{System.IO.Path.GetFileName(path)}':\n\n{err}", "OK");
                }
            }

            AssetDatabase.Refresh();
            FileLockProjectWindowOverlay.RepaintProjectWindow();
        }

        [MenuItem("Assets/FRG Version Control/Clear Ignore on Commit", true)]
        private static bool ValidateClearIgnoreOnCommit()
        {
            if (Selection.objects.Length == 0 || !FileLockManager.Instance.IsInitialized)
                return false;

            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path) &&
                    FileLockOverrideManager.Instance.IsInOverrideMode(path))
                {
                    return true;
                }
            }
            return false;
        }

        // Phase 2 Step E: Stash/Pop menu items removed — svn shelve unavailable in 1.14.5
        // (Phase 1 Addendum Test 3 finding 11b787c648a3). Clear Ignore on Commit is the sole exit path.

        [MenuItem("Assets/FRG Version Control/Convert Ignore on Commit to Lock", false, 2014)]
        private static void ConvertIgnoreOnCommitOnSelected()
        {
            // Single-file operation — convert is intentionally explicit and synchronous;
            // doing it across many files at once would mask individual conflict cases.
            if (Selection.objects.Length != 1) return;
            string path = AssetDatabase.GetAssetPath(Selection.objects[0]);
            if (string.IsNullOrEmpty(path)) return;

            string err;
            if (!OverrideConversion.ConvertIgnoreOnCommitToLock(path, out err) && !string.IsNullOrEmpty(err))
            {
                EditorUtility.DisplayDialog("Convert to Lock Failed",
                    $"Could not convert '{System.IO.Path.GetFileName(path)}' to a lock:\n\n{err}",
                    "OK");
            }
            AssetDatabase.Refresh();
            FileLockProjectWindowOverlay.RepaintProjectWindow();
        }

        [MenuItem("Assets/FRG Version Control/Convert Ignore on Commit to Lock", true)]
        private static bool ValidateConvertIgnoreOnCommit()
        {
            if (Selection.objects.Length != 1 || !FileLockManager.Instance.IsInitialized)
                return false;

            string path = AssetDatabase.GetAssetPath(Selection.objects[0]);
            if (string.IsNullOrEmpty(path)) return false;

            var entry = FileLockOverrideManager.Instance.GetOverride(path);
            if (entry == null) return false;

            // Show only when the original lock has been released (Mode B condition)
            var fileLock = FileLockManager.Instance.GetFileLock(path);
            return fileLock == null;
        }

        // -------------------------------------------------------------------------

        [MenuItem("Assets/FRG Version Control/Refresh Locks and SVN Status", false, 2005)]
        private static void RefreshLocksAndGitStatus()
        {
            // Refresh locks cache
            bool lockRefreshSuccess = FileLockManager.Instance.RefreshLockCache();

            // Refresh git status cache
            FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();

            // Repaint project window
            EditorApplication.RepaintProjectWindow();

            // Log result
            if (lockRefreshSuccess)
            {
                Debug.Log("[FRGVersionControl] Successfully refreshed locks and git status cache");
            }
            else
            {
                Debug.LogWarning("[FRGVersionControl] Failed to refresh locks cache, but git status was refreshed");
            }

            AssetDatabase.Refresh();
        }

        // Validation methods (show/hide menu items based on context)

        [MenuItem("Assets/FRG Version Control/Lock File(s)", true)]
        private static bool ValidateLockFiles()
        {
            if (Selection.objects.Length == 0 || !FileLockManager.Instance.IsInitialized)
                return false;

            // Grey out menu if ALL selected files are untracked
            // Enable if at least one file is tracked
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                // Check if file type is lockable
                if (!FileLockManager.Instance.IsLockableFileType(path))
                    continue;

                // If any lockable file is NOT untracked, enable the menu
                if (!FRGVersionControl.Core.Svn.SvnCommandRunner.IsFileUntracked(path))
                    return true;
            }

            // All lockable files are untracked (or no lockable files selected)
            return false;
        }

        [MenuItem("Assets/FRG Version Control/Unlock File(s)", true)]
        private static bool ValidateUnlockFiles()
        {
            if (Selection.objects.Length == 0 || !FileLockManager.Instance.IsInitialized)
                return false;

            // Grey out menu if ALL selected files are untracked
            // Enable if at least one file is tracked
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                // Check if file type is lockable
                if (!FileLockManager.Instance.IsLockableFileType(path))
                    continue;

                // If any lockable file is NOT untracked, enable the menu
                if (!FRGVersionControl.Core.Svn.SvnCommandRunner.IsFileUntracked(path))
                    return true;
            }

            // All lockable files are untracked (or no lockable files selected)
            return false;
        }

        [MenuItem("Assets/FRG Version Control/Revert File(s)", true)]
        private static bool ValidateRevertFiles()
        {
            if (Selection.objects.Length == 0 || !FileLockManager.Instance.IsInitialized)
                return false;

            // Only show if at least one selected file has uncommitted changes
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                if (FRGVersionControl.Core.Svn.SvnCommandRunner.HasUncommittedChanges(path))
                    return true;
            }

            return false;
        }

        [MenuItem("Assets/FRG Version Control/Revert and Unlock File(s)", true)]
        private static bool ValidateRevertAndUnlockFiles()
        {
            if (Selection.objects.Length == 0 || !FileLockManager.Instance.IsInitialized)
                return false;

            // Only show if at least one selected file is locked by the current user
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                if (FileLockManager.Instance.IsLockedByMe(path))
                    return true;
            }

            return false;
        }

        [MenuItem("Assets/FRG Version Control/View Lock Info", true)]
        private static bool ValidateViewLockInfo()
        {
            return Selection.objects.Length == 1 && FileLockManager.Instance.IsInitialized;
        }

        [MenuItem("Assets/FRG Version Control/Refresh Locks and Git Status", true)]
        private static bool ValidateRefreshLocksAndGitStatus()
        {
            return FileLockManager.Instance.IsInitialized;
        }

        // Helper method for force unlock dialog
        private static bool ShowForceUnlockDialog(FileLock fileLock)
        {
            var lockTime = fileLock.GetLockedAtTime();
            var elapsed = System.DateTime.UtcNow - lockTime;
            string timeAgo = FormatTimeAgo(elapsed);
            bool isStale = fileLock.IsStale();

            string message = $"Force unlock file: {System.IO.Path.GetFileName(fileLock.path)}\n\n";
            message += $"Owner: {fileLock.ownerName} ({fileLock.owner})\n";
            message += $"Locked: {timeAgo} ago\n\n";

            if (isStale)
            {
                message += "This lock is stale (>24 hours old).\n\n";
            }
            else
            {
                message += "WARNING: This lock is fresh and the owner may be actively working on this file.\n";
                message += "Force unlocking may disrupt their work and cause potential conflicts.\n\n";
            }

            message += "Are you sure you want to force unlock this file?";

            return EditorUtility.DisplayDialog("Force Unlock", message, "Yes, Force Unlock", "Cancel");
        }

        private static string FormatTimeAgo(System.TimeSpan elapsed)
        {
            if (elapsed.TotalMinutes < 1)
                return "less than a minute";
            if (elapsed.TotalMinutes < 60)
                return $"{(int)elapsed.TotalMinutes} minute{((int)elapsed.TotalMinutes != 1 ? "s" : "")}";
            if (elapsed.TotalHours < 24)
                return $"{(int)elapsed.TotalHours} hour{((int)elapsed.TotalHours != 1 ? "s" : "")}";
            return $"{(int)elapsed.TotalDays} day{((int)elapsed.TotalDays != 1 ? "s" : "")}";
        }

        // Helper method to show batch lock error dialog
        private static void ShowBatchLockErrorDialog(List<FileOperationResult> failedResults)
        {
            if (failedResults.Count == 0)
                return;

            var message = $"Failed to lock {failedResults.Count} file(s):\n\n";
            
            // Show up to 10 files
            var displayResults = failedResults.Take(10).ToList();
            foreach (var result in displayResults)
            {
                message += $"• {System.IO.Path.GetFileName(result.path)}\n";
                if (!string.IsNullOrEmpty(result.errorMessage))
                {
                    message += $"  Error: {result.errorMessage}\n";
                }
            }
            
            if (failedResults.Count > 10)
            {
                message += $"\n... and {failedResults.Count - 10} more files";
            }

            EditorUtility.DisplayDialog("Lock Failed", message, "OK");
        }

        // Helper method to show batch unlock error dialog
        private static void ShowBatchUnlockErrorDialog(List<FileOperationResult> failedResults)
        {
            if (failedResults.Count == 0)
                return;

            var message = $"Failed to unlock {failedResults.Count} file(s):\n\n";
            
            // Show up to 10 files
            var displayResults = failedResults.Take(10).ToList();
            foreach (var result in displayResults)
            {
                message += $"• {System.IO.Path.GetFileName(result.path)}\n";
                if (!string.IsNullOrEmpty(result.errorMessage))
                {
                    message += $"  Error: {result.errorMessage}\n";
                }
            }
            
            if (failedResults.Count > 10)
            {
                message += $"\n... and {failedResults.Count - 10} more files";
            }

            EditorUtility.DisplayDialog("Unlock Failed", message, "OK");
        }
    }
}
