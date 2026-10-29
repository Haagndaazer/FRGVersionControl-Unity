using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.OverrideMode;
using FRGVersionControl.UI;

namespace FRGVersionControl.Hooks
{
    /// <summary>
    /// Intercepts asset save operations to enforce file locking
    /// </summary>
    public class FileLockAssetModificationProcessor : AssetModificationProcessor
    {
        /// <summary>
        /// Represents the result of a file operation during save
        /// </summary>
        private class FileOperationResult
        {
            public enum Status
            {
                Success,           // Successfully locked
                FailedToLock,      // Couldn't lock (with error)
                LockedByOther,     // Locked by different user/machine
                AlreadyLockedByMe, // Already locked by current user - OK to save
                Skipped            // Skipped (.meta, non-lockable, untracked)
            }

            public string path;
            public Status status;
            public string errorMessage;
            public FileLock existingLock;
            public bool canForceUnlock;
        }

        /// <summary>
        /// Called before saving assets
        /// </summary>
        static string[] OnWillSaveAssets(string[] paths)
        {
            if (!FileLockManager.Instance.IsInitialized)
            {
                return paths; // Locking disabled, allow all saves
            }

            if (!FileLockManager.AutoLockEnabled)
            {
                return paths; // User has disabled auto-lock; manual locking still available
            }

            // Categorize files first
            var filesToLock = new List<string>();
            var skippedFiles = new List<(string path, string reason)>();
            var lockedByOther = new List<(string path, FileLock existingLock)>();
            var alreadyLockedByMe = new List<string>();
            
            // First pass - categorize all files
            foreach (string path in paths)
            {
                // .meta paths: lockable iff the SIDECAR ASSET is lockable. Previously skipped
                // unconditionally; that left Inspector-only edits (texture compression, etc.)
                // unlocked, hiding partial-state from coworkers. Asset and meta locks remain
                // independent (pair-aware locking was explicitly abandoned).
                bool isMeta = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
                string lockabilityPath = isMeta
                    ? path.Substring(0, path.Length - ".meta".Length)
                    : path;

                if (isMeta)
                {
                    // Sidecar guard: never auto-lock the .meta of an SVN conflict sidecar
                    // (Foo.png.r39, Foo.png.mine, etc.) — these are transient and resolved
                    // via `svn resolve`, not via the lock workflow.
                    if (FRGVersionControl.Core.SvnStatusCache.IsSvnConflictSidecar(lockabilityPath))
                    {
                        skippedFiles.Add((path, "Conflict sidecar"));
                        continue;
                    }
                }

                // Skip non-lockable files (scripts, etc.). For .meta this checks the underlying
                // asset's extension against the lockable-extensions set.
                if (!IsLockableFile(lockabilityPath))
                {
                    skippedFiles.Add((path, isMeta ? ".meta of non-lockable asset" : "Non-lockable file type"));
                    continue;
                }

                // Skip uncommitted files silently (Untracked or Added — neither has a committed
                // base, so `svn lock` fails E155010 on both). Applies to .meta paths too — a
                // brand-new asset's .meta is handled by the AssetPostprocessor's auto-add path.
                if (FRGVersionControl.Core.Svn.SvnCommandRunner.IsFileUncommitted(path))
                {
                    Debug.Log($"[FRGVersionControl] Skipping uncommitted file (auto-lock): {path}");
                    skippedFiles.Add((path, "Uncommitted file"));
                    continue;
                }

                // Check lock status
                var existingLock = FileLockManager.Instance.GetFileLock(path);

                if (existingLock == null)
                {
                    // File not locked - need to lock it
                    filesToLock.Add(path);
                }
                else if (FileLockManager.Instance.IsLockedByOther(path))
                {
                    // Phase 2 Stage 3: ignore-on-commit files bypass the save-blocker.
                    // The user explicitly set ignore-on-commit; the file is in the
                    // ignore-on-commit changelist so it can't be accidentally committed,
                    // but Unity's save itself is allowed to write to the working-tree file.
                    if (FileLockOverrideManager.Instance.IsInOverrideMode(path))
                    {
                        Debug.Log($"[FRGVersionControl] Saving ignore-on-commit file: {path}");
                        alreadyLockedByMe.Add(path);
                    }
                    else
                    {
                        // Locked by someone else
                        lockedByOther.Add((path, existingLock));
                    }
                }
                else
                {
                    // Locked by us - allow save
                    alreadyLockedByMe.Add(path);
                    Debug.Log($"[FRGVersionControl] Saving locked file: {path}");
                }
            }

            // Build results list
            List<FileOperationResult> results = new List<FileOperationResult>();
            
            // Add skipped files
            foreach (var (path, reason) in skippedFiles)
            {
                results.Add(new FileOperationResult { path = path, status = FileOperationResult.Status.Skipped });
            }
            
            // Add already locked files
            foreach (var path in alreadyLockedByMe)
            {
                results.Add(new FileOperationResult
                {
                    path = path,
                    status = FileOperationResult.Status.AlreadyLockedByMe
                });
            }
            
            // Add locked by other files
            foreach (var (path, existingLock) in lockedByOther)
            {
                bool isSameEmail = existingLock.owner == FileLockManager.Instance.UserName /* Phase 2 LFS: owner == username, not email */;
                bool isAdmin = FileLockManager.Instance.IsUserAdmin();
                bool canForceUnlock = isSameEmail && isAdmin;

                results.Add(new FileOperationResult
                {
                    path = path,
                    status = FileOperationResult.Status.LockedByOther,
                    existingLock = existingLock,
                    canForceUnlock = canForceUnlock
                });
            }

            // Now handle files that need to be locked
            if (filesToLock.Count > 0)
            {
                if (filesToLock.Count >= 2)
                {
                    // Use batch locking for multiple files. Pass skipLockableCheck=true because
                    // the meta-aware gate above already verified lockability (against the asset
                    // path for .meta entries); without the flag the manager would re-check the
                    // raw `.meta` extension and silently drop those paths.
                    Debug.Log($"[FRGVersionControl] Batch auto-locking {filesToLock.Count} files...");
                    var batchResult = FileLockManager.Instance.BatchLockFiles(filesToLock, skipLockableCheck: true);
                    
                    // Convert batch results to FileOperationResult format
                    foreach (var fileResult in batchResult.results)
                    {
                        if (fileResult.success)
                        {
                            Debug.Log($"[FRGVersionControl] Auto-locked: {fileResult.path}");
                            results.Add(new FileOperationResult
                            {
                                path = fileResult.path,
                                status = FileOperationResult.Status.Success
                            });
                        }
                        else
                        {
                            Debug.LogWarning($"[FRGVersionControl] Failed to lock: {fileResult.path} - {fileResult.errorMessage}");
                            results.Add(new FileOperationResult
                            {
                                path = fileResult.path,
                                status = FileOperationResult.Status.FailedToLock,
                                errorMessage = fileResult.errorMessage,
                                existingLock = fileResult.existingLock
                            });
                        }
                    }
                }
                else
                {
                    // Single file - use regular lock method. skipLockableCheck=true mirrors the
                    // batch path's rationale: meta paths have been verified lockable via the
                    // underlying asset already.
                    string path = filesToLock[0];
                    bool success = FileLockManager.Instance.LockFile(path, out string errorMessage, skipLockableCheck: true);

                    if (success)
                    {
                        Debug.Log($"[FRGVersionControl] Auto-locked: {path}");
                        results.Add(new FileOperationResult
                        {
                            path = path,
                            status = FileOperationResult.Status.Success
                        });
                    }
                    else
                    {
                        Debug.LogWarning($"[FRGVersionControl] Failed to lock: {path} - {errorMessage}");
                        results.Add(new FileOperationResult
                        {
                            path = path,
                            status = FileOperationResult.Status.FailedToLock,
                            errorMessage = errorMessage
                        });
                    }
                }
            }

            // Now show batched dialog and handle results
            return HandleBatchedResults(paths, results);
        }

        /// <summary>
        /// Handles batched results from multiple file operations and shows appropriate dialogs
        /// </summary>
        private static string[] HandleBatchedResults(string[] originalPaths, List<FileOperationResult> results)
        {
            // Categorize results
            var successResults = new List<FileOperationResult>();
            var failedResults = new List<FileOperationResult>();
            var lockedByOtherResults = new List<FileOperationResult>();
            var alreadyLockedResults = new List<FileOperationResult>();

            foreach (var result in results)
            {
                switch (result.status)
                {
                    case FileOperationResult.Status.Success:
                        successResults.Add(result);
                        break;
                    case FileOperationResult.Status.FailedToLock:
                        failedResults.Add(result);
                        break;
                    case FileOperationResult.Status.LockedByOther:
                        lockedByOtherResults.Add(result);
                        break;
                    case FileOperationResult.Status.AlreadyLockedByMe:
                        alreadyLockedResults.Add(result);
                        break;
                    // Skipped files don't need to be shown
                }
            }

            // Build list of saveable paths (start with valid files)
            var saveablePaths = new List<string>();

            // Add successfully locked files
            saveablePaths.AddRange(successResults.Select(r => r.path));

            // Add already locked by me files
            saveablePaths.AddRange(alreadyLockedResults.Select(r => r.path));

            // Add skipped files (meta, non-lockable, untracked)
            saveablePaths.AddRange(results.Where(r => r.status == FileOperationResult.Status.Skipped).Select(r => r.path));

            // Handle locked-by-other files. The new dialog (OverrideOnSaveWindow) returns the
            // set of paths the user opted to set ignore-on-commit on; any path that SetIgnoreOnCommit
            // accepted joins the saveable list so the override-mode bypass at OnWillSaveAssets
            // line ~115 lets the actual save go through. Use BuildSaveableListAfterOverride for
            // defensive dedup (case-insensitive — Windows path matching).
            if (lockedByOtherResults.Count > 0)
            {
                var overriddenPaths = HandleLockedByOtherDialog(lockedByOtherResults, saveablePaths.Count);
                saveablePaths = BuildSaveableListAfterOverride(saveablePaths, overriddenPaths);
            }

            // Handle failed locks (let user choose to save anyway or skip)
            if (failedResults.Count > 0)
            {
                bool userChooseSaveAnyway = !HandleFailedLocksDialog(failedResults, saveablePaths.Count);
                if (userChooseSaveAnyway)
                {
                    // User chose "Save Anyway" - add failed files to saveable list
                    saveablePaths.AddRange(failedResults.Select(r => r.path));
                }
                // Otherwise skip these files
            }

            // Show success summary (only if there are successful locks and we saved something)
            if (successResults.Count > 0 && saveablePaths.Count > 0)
            {
                ShowSuccessSummary(successResults);
            }

            FileLockProjectWindowOverlay.ScheduleRefreshGlobalSvnStatusCache();

            return saveablePaths.ToArray();
        }

        /// <summary>
        /// Surfaces the OverrideOnSaveWindow for files locked by other users. The user picks
        /// which paths to set ignore-on-commit on; for each selected path that SetIgnoreOnCommit accepts,
        /// the path is returned to be re-added to the saveable list (the override-mode bypass
        /// at line ~115 lets the actual save go through).
        ///
        /// Force Unlock (admin/same-email-different-machine) is NOT surfaced here — it remains
        /// available via Window → FRG Version Control → Locks tab + the Project context menu.
        /// </summary>
        private static List<string> HandleLockedByOtherDialog(List<FileOperationResult> lockedResults, int saveableFileCount)
        {
            var window = OverrideOnSaveWindow.Create(
                lockedResults.Select(r => (r.path, r.existingLock)),
                otherSaveableCount: saveableFileCount);
            window.titleContent = new GUIContent("Files Locked — Save Anyway? (uses Ignore on Commit)");
            window.minSize = new Vector2(600, 320);
            window.ShowModal();   // blocks OnWillSaveAssets until user clicks Cancel / Save with Ignore on Commit / closes

            if (window.UserCancelled || window.SelectedPaths.Count == 0)
            {
                return new List<string>();
            }

            var overridden = new List<string>();
            var failures = new List<(string path, string err)>();
            foreach (var path in window.SelectedPaths)
            {
                if (FileLockOverrideManager.Instance.SetIgnoreOnCommit(path, out string err))
                {
                    overridden.Add(path);
                    Debug.Log($"[FRGVersionControl] Save-with-ignore-on-commit: set on {path}");
                }
                else
                {
                    failures.Add((path, err));
                }
            }

            if (failures.Count > 0)
            {
                var msg = string.Join("\n", failures.Select(f => $"• {DisplayPath(f.path)} — {f.err}"));
                Debug.LogError($"[FRGVersionControl] Save-with-ignore-on-commit failed for {failures.Count} file(s). " +
                               $"Open Window → FRG Version Control → Changes to inspect state.\n{msg}");
                EditorUtility.DisplayDialog("Set Ignore on Commit Failed",
                    $"Could not set ignore-on-commit on {failures.Count} file(s). These files were not saved.\n\n{msg}\n\n" +
                    "Open Window → FRG Version Control → Changes to inspect state.",
                    "OK");
            }

            return overridden;
        }

        /// <summary>
        /// Shows dialog for files that failed to lock
        /// Returns true if user wants to cancel (skip these files), false if user wants to save anyway
        /// </summary>
        private static bool HandleFailedLocksDialog(List<FileOperationResult> failedResults, int saveableFileCount)
        {
            string message;
            string saveableInfo = saveableFileCount > 0
                ? "\n(Other valid files will be saved regardless of your choice)"
                : "";

            if (failedResults.Count == 1)
            {
                // Single file - show detailed message
                var result = failedResults[0];
                message = $"Could not lock the file:\n\n{DisplayPath(result.path)}\n\n{result.errorMessage}\n\n" +
                         $"Skip this file or save anyway? (Save anyway is not recommended){saveableInfo}";
            }
            else
            {
                // Multiple files - show summary
                var fileList = new System.Text.StringBuilder();
                fileList.AppendLine($"Failed to lock {failedResults.Count} file(s):\n");

                foreach (var result in failedResults)
                {
                    fileList.AppendLine($"• {DisplayPath(result.path)}");
                    if (!string.IsNullOrEmpty(result.errorMessage))
                    {
                        fileList.AppendLine($"  Error: {result.errorMessage}");
                    }
                }

                fileList.AppendLine($"\nSkip these files or save anyway? (Save anyway is not recommended){saveableInfo}");
                message = fileList.ToString();
            }

            bool proceed = EditorUtility.DisplayDialog(
                "Lock Failed",
                message,
                "Skip These Files",
                "Save Anyway"
            );

            return proceed; // true = Skip (Cancel), false = Save Anyway
        }

        /// <summary>
        /// Shows success summary for successfully locked files
        /// </summary>
        private static void ShowSuccessSummary(List<FileOperationResult> successResults)
        {
            // Only show dialog if there are multiple files (single file auto-lock is not worth interrupting)
            if (successResults.Count <= 1)
            {
                return; // Silent success for single file
            }

            string message;

            if (successResults.Count <= 5)
            {
                // Show individual files if there aren't too many
                var fileList = new System.Text.StringBuilder();
                fileList.AppendLine($"Successfully locked {successResults.Count} file(s):\n");

                foreach (var result in successResults)
                {
                    fileList.AppendLine($"• {System.IO.Path.GetFileName(DisplayPath(result.path))}");
                }

                message = fileList.ToString();
            }
            else
            {
                // Just show count for many files
                message = $"Successfully locked {successResults.Count} files.\n\nYou can now continue editing.";
            }

            EditorUtility.DisplayDialog(
                "Files Locked",
                message,
                "OK"
            );
        }

        /// <summary>
        /// Called before an asset is moved/renamed
        /// Blocks moves if file is locked by another user or different machine
        /// </summary>
        static AssetMoveResult OnWillMoveAsset(string sourcePath, string destinationPath)
        {
            if (!FileLockManager.Instance.IsInitialized)
            {
                return AssetMoveResult.DidNotMove; // Locking disabled, allow move
            }

            // Phase 4 Step 5: temporary-disable bypass (batch tools that move files en masse)
            if (FRGVersionControl.Services.FileLockSilenceService.IsTemporarilyDisabled)
            {
                return AssetMoveResult.DidNotMove;
            }

            bool isMeta = sourcePath.EndsWith(".meta");

            if (!isMeta)
            {
                // Conflict / lock / folder-add validations apply to assets, not their .meta
                // siblings — Unity fires OnWillMoveAsset for both. Skip these for .meta.

                // Phase 4 Step 5 (Wise SVN cherry-pick #8): refuse move on conflicted files.
                var sourceStatus = FRGVersionControl.Core.SvnStatusCache.Get(sourcePath);
                if (sourceStatus == FRGVersionControl.Core.Svn.SvnFileStatus.Conflict)
                {
                    EditorUtility.DisplayDialog("⚠️ Cannot Move — File In Conflict",
                        $"Cannot rename/move this file:\n\n  {sourcePath}\n\n" +
                        "This file is currently in a conflicted state (post-update merge conflict). " +
                        "Resolve the conflict first via Window → FRG Version Control → Changes tab " +
                        "(Take Mine / Take Theirs on the conflicted row), then retry the move.",
                        "OK");
                    Debug.LogWarning($"[FRGVersionControl] Blocked move of conflicted file: {sourcePath}");
                    return AssetMoveResult.FailedMove;
                }

                // Phase 4 Step 5 (Wise SVN cherry-pick #7): prompt to `svn add` a destination folder
                // that exists locally but isn't versioned.
                if (!FRGVersionControl.Services.FileLockSilenceService.IsSilenced)
                {
                    CheckAndOfferUnversionedFolderAdd(destinationPath);
                }

                // Lock check: refuse moves on files locked by someone else.
                var fileLock = FileLockManager.Instance.GetFileLock(sourcePath);
                if (fileLock != null && !FileLockManager.Instance.IsLockedByMe(sourcePath))
                {
                    var elapsed = System.DateTime.UtcNow - fileLock.GetLockedAtTime();
                    string timeAgo = FormatTimeAgo(elapsed);
                    bool isSameEmail = fileLock.owner == FileLockManager.Instance.UserName;
                    string message = isSameEmail
                        ? $"Cannot rename/move this file:\n\nFile: {sourcePath}\n\n" +
                          $"This file is locked by you on machine '{fileLock.machineName}'.\n" +
                          $"Locked: {timeAgo} ago\n\n" +
                          "You can only rename files locked on this machine."
                        : $"Cannot rename/move this file:\n\nFile: {sourcePath}\n\n" +
                          $"This file is locked by {fileLock.ownerName} ({fileLock.owner})\n" +
                          $"Machine: {fileLock.machineName}\nLocked: {timeAgo} ago\n\n" +
                          "Contact the owner to release the lock before renaming.";
                    EditorUtility.DisplayDialog("⚠️ Cannot Rename - File Locked", message, "OK");
                    Debug.LogWarning($"[FRGVersionControl] Blocked rename/move of file locked by {(isSameEmail ? "different machine" : "other user")}: {sourcePath}");
                    return AssetMoveResult.FailedMove;
                }
            }

            // SVN propagation: run `svn move SRC DST` BEFORE Unity does its OS move so the
            // source path still exists on disk. svn move handles bytes + wc.db registration
            // atomically. Returning DidMove tells Unity to skip its own OS-level rename
            // (the file is already at destination). For unversioned files, fall through to
            // DidNotMove and let Unity rename via OS.
            if (!FRGVersionControl.Core.Svn.SvnCommandRunner.IsVersioned(sourcePath))
            {
                return AssetMoveResult.DidNotMove;
            }

            var moveResult = FRGVersionControl.Core.Svn.SvnCommandRunner.Move(sourcePath, destinationPath);
            if (!moveResult.success)
            {
                Debug.LogError($"[FRGVersionControl] svn move failed: {sourcePath} → {destinationPath}.\n" +
                               $"Output:\n{moveResult.output}\nError:\n{moveResult.error}");
                return AssetMoveResult.FailedMove;
            }

            // Proactively move the `.meta` sibling in the SAME pre-Unity-move window. If we
            // don't, Unity's internal asset-pair handling moves the .meta on disk before its
            // own OnWillMoveAsset callback fires for the .meta — by which point our IsVersioned
            // check on the (now-missing-on-disk) source fails and we silently return DidNotMove,
            // leaving the .meta in a `D-at-old + ?-at-new` half-state. Doing it here, while
            // both source files are still on disk, atomically pairs both moves.
            if (!sourcePath.EndsWith(".meta"))
            {
                string srcMeta = sourcePath + ".meta";
                string dstMeta = destinationPath + ".meta";
                if (FRGVersionControl.Core.Svn.SvnCommandRunner.IsVersioned(srcMeta))
                {
                    var metaResult = FRGVersionControl.Core.Svn.SvnCommandRunner.Move(srcMeta, dstMeta);
                    if (!metaResult.success)
                    {
                        // Log but don't fail the asset's move — the asset is already moved.
                        // The .meta will surface as a Deleted+Untracked pair which the
                        // FrgVersionControlWindow external-divergence reconciler handles.
                        Debug.LogError($"[FRGVersionControl] svn move failed for .meta sibling: {srcMeta} → {dstMeta}.\n" +
                                       $"Output:\n{metaResult.output}\nError:\n{metaResult.error}");
                    }
                }
            }

            // Refresh status cache + overlay so the rename surfaces in the panel immediately.
            FRGVersionControl.Core.Svn.SvnCommandRunner.s_UnityMainSync?.Post(_ =>
            {
                _ = FRGVersionControl.Core.SvnStatusCache.RefreshAsync();
                FRGVersionControl.UI.FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();
            }, null);
            return AssetMoveResult.DidMove;
        }

        /// <summary>
        /// Called before an asset is deleted. Propagates the delete to SVN automatically
        /// for versioned files (runs `svn rm` — both removes the bytes and registers the
        /// delete in wc.db atomically) so the next commit isn't broken by an orphan `!`
        /// (missing) row. Also refuses deletes on files locked by another user so the
        /// artist gets an immediate clear error instead of a confusing commit-time reject.
        /// </summary>
        static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
        {
            if (!FileLockManager.Instance.IsInitialized)
            {
                return AssetDeleteResult.DidNotDelete; // Locking disabled, allow deletion
            }

            bool isMeta = assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

            // 1. Block-on-locked-by-other PRE-CHECK. Mirrors OnWillMoveAsset's lock-validation.
            //    Without this, a delete of a locked-by-other file would land locally but be
            //    rejected by the server at commit time (E195022) — confusing UX. .meta files
            //    aren't lockable; skip the check for them.
            if (!isMeta)
            {
                var fl = FileLockManager.Instance.GetFileLock(assetPath);
                bool lockedByOther = fl != null && !FileLockManager.Instance.IsLockedByMe(assetPath);
                if (lockedByOther)
                {
                    EditorUtility.DisplayDialog("⚠️ Cannot Delete — File Locked",
                        $"Cannot delete '{System.IO.Path.GetFileName(assetPath)}':\n\n" +
                        $"This file is locked by {fl.ownerName}. Coordinate with them, " +
                        "or wait for the lock to release.",
                        "OK");
                    Debug.LogWarning($"[FRGVersionControl] Blocked delete of locked-by-other file: {assetPath}");
                    return AssetDeleteResult.FailedDelete;
                }
            }

            // 2. Ask the core what this delete means in SVN terms. Schedule-add nodes (added,
            //    never committed — e.g. freshly baked lightmaps) are reverted by the core right
            //    here, because neither `svn lock` (E155010) nor `svn delete` (E195006) can act
            //    on a node with no committed base. The .meta rides along as an independent
            //    path so one call covers the pair. Committed nodes come back untouched.
            var prepPaths = isMeta ? new[] { assetPath } : new[] { assetPath, assetPath + ".meta" };
            var prep = FRGVersionControl.Core.Svn.SvnCommandRunner.PrepareDelete(prepPaths);
            bool lockedByMe = FileLockManager.Instance.IsLockedByMe(assetPath);
            var disposition = PlanDelete(prep, assetPath, isMeta,
                FileLockManager.Instance.IsLockableFileType(assetPath), lockedByMe);

            if (disposition == DeleteDisposition.OsDelete)
            {
                LogPrepareDeleteOutcome(prep, assetPath, prepPaths);

                // 3. A lock I hold on a never-committed file would linger server-side after the
                //    OS delete; release it. Only when the core positively says "unversioned":
                //    a committed file whose classification failed keeps its lock (the reconcile
                //    banner's later delete needs it), and a reverted add cannot hold one.
                if (lockedByMe && prep.success && prep.IsUnversioned(assetPath))
                {
                    Debug.Log($"[FRGVersionControl] Auto-unlocking uncommitted file on delete: {assetPath}");
                    bool unlockSuccess = FileLockManager.Instance.UnlockFile(assetPath, force: false, skipRevertDialog: true);
                    if (!unlockSuccess)
                    {
                        Debug.LogWarning($"[FRGVersionControl] Failed to auto-unlock file on delete: {assetPath}");
                    }
                }

                if (prep.success && prep.WasReverted(assetPath))
                {
                    FRGVersionControl.Core.Svn.SvnCommandRunner.s_UnityMainSync?.Post(_ =>
                    {
                        _ = FRGVersionControl.Core.SvnStatusCache.RefreshAsync();
                        FRGVersionControl.UI.FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();
                    }, null);
                }
                return AssetDeleteResult.DidNotDelete;
            }

            // 4. Lockable files: server-side `svn:needs-lock` rejects ANY commit (including
            //    a delete) on a lockable file without a held lock. If this file is lockable
            //    and we don't currently hold the lock, acquire it now — both so the eventual
            //    commit goes through cleanly AND so no other user can start editing the file
            //    between this delete being staged and committed. .meta files are not lockable
            //    on their own; the asset's lock covers them.
            if (disposition == DeleteDisposition.LockThenSvnDelete)
            {
                string lockErr;
                bool locked = FileLockManager.Instance.LockFile(assetPath, out lockErr);
                if (!locked)
                {
                    EditorUtility.DisplayDialog("⚠️ Cannot Delete — Lock Acquisition Failed",
                        $"Could not acquire a lock on '{System.IO.Path.GetFileName(assetPath)}' " +
                        $"before deleting:\n\n{lockErr ?? "(no detail)"}\n\n" +
                        "Lockable files require a held lock for the delete to commit. " +
                        "Check the Console for the full svn output.",
                        "OK");
                    Debug.LogError($"[FRGVersionControl] Auto-lock-on-delete failed for {assetPath}: {lockErr}");
                    return AssetDeleteResult.FailedDelete;
                }
                Debug.Log($"[FRGVersionControl] Auto-acquired lock before deleting lockable file: {assetPath}");
            }

            // 5. Tracked path → run `svn rm` (deletes file + registers in wc.db).
            //    Returning DidDelete tells Unity to skip its own OS delete attempt.
            var result = FRGVersionControl.Core.Svn.SvnCommandRunner.Delete(new[] { assetPath });
            if (!result.success)
            {
                Debug.LogError($"[FRGVersionControl] svn rm failed for {assetPath}.\n" +
                               $"Output:\n{result.output}\nError:\n{result.error}");
                return AssetDeleteResult.FailedDelete;
            }

            // Refresh status cache + overlay so the new Deleted row appears immediately.
            FRGVersionControl.Core.Svn.SvnCommandRunner.s_UnityMainSync?.Post(_ =>
            {
                _ = FRGVersionControl.Core.SvnStatusCache.RefreshAsync();
                FRGVersionControl.UI.FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();
            }, null);
            return AssetDeleteResult.DidDelete;
        }

        /// <summary>What the delete hook does after the core has classified the path.</summary>
        public enum DeleteDisposition
        {
            /// <summary>Nothing left for SVN to do (unversioned, reverted schedule-add, or the
            /// core could not classify it) — return DidNotDelete and let Unity OS-delete.</summary>
            OsDelete,
            /// <summary>Committed lockable node not held by me — acquire the lock, then svn rm.</summary>
            LockThenSvnDelete,
            /// <summary>Committed node (held by me, non-lockable, or a .meta) — svn rm.</summary>
            SvnDelete,
        }

        /// <summary>
        /// Pure planner for <see cref="OnWillDeleteAsset"/>: maps the core's
        /// <c>mutate.prepareDelete</c> answer plus the engine-side lock facts to one
        /// disposition. Only a path the core reports as <c>versioned</c> reaches the
        /// lock-then-delete flow; everything else (reverted add, unversioned, per-path failure,
        /// or a failed IPC call) is an OS delete. Public + static for the NUnit suite.
        /// </summary>
        public static DeleteDisposition PlanDelete(
            FRGVersionControl.Core.Svn.PrepareDeleteResult prep,
            string assetPath, bool isMeta, bool isLockable, bool lockedByMe)
        {
            if (prep == null || !prep.success) return DeleteDisposition.OsDelete;
            if (!prep.IsVersioned(assetPath)) return DeleteDisposition.OsDelete;
            if (!isMeta && isLockable && !lockedByMe) return DeleteDisposition.LockThenSvnDelete;
            return DeleteDisposition.SvnDelete;
        }

        private static void LogPrepareDeleteOutcome(
            FRGVersionControl.Core.Svn.PrepareDeleteResult prep, string assetPath, string[] prepPaths)
        {
            if (prep == null || !prep.success)
            {
                Debug.LogWarning($"[FRGVersionControl] Could not classify '{assetPath}' before deleting " +
                                 $"({prep?.error ?? "no result"}). The delete was not registered with SVN. " +
                                 "Use FRG Version Control > Changes to reconcile any missing entry.");
                return;
            }
            foreach (var p in prepPaths)
            {
                if (prep.WasReverted(p))
                {
                    Debug.Log($"[FRGVersionControl] Reverted uncommitted add before delete: {p}");
                    continue;
                }
                var fail = prep.FailureFor(p);
                if (fail != null)
                {
                    Debug.LogWarning($"[FRGVersionControl] Could not prepare '{p}' for delete: {fail.error}. " +
                                     "Use FRG Version Control > Changes to reconcile the missing entry.");
                    continue;
                }
                if (p != assetPath && prep.IsVersioned(p))
                {
                    Debug.LogWarning($"[FRGVersionControl] '{p}' is committed but its asset is not; the OS delete " +
                                     "will leave it missing. Use FRG Version Control > Changes to reconcile it.");
                }
            }
        }

        /// <summary>
        /// Determine if file should be locked based on centralized configuration
        /// </summary>
        private static bool IsLockableFile(string path)
        {
            return FileLockManager.Instance.IsLockableFileType(path);
        }

        /// <summary>
        /// Pure helper: merge override-on-save results into the running saveable list.
        /// De-duplicates case-insensitively (Windows path semantics) so a path that already
        /// landed in the saveable list via another branch isn't double-emitted.
        /// Public + static so the NUnit suite can exercise it without spinning up the Editor
        /// AssetModificationProcessor pipeline.
        /// </summary>
        public static List<string> BuildSaveableListAfterOverride(
            IEnumerable<string> alreadySaveable,
            IEnumerable<string> overriddenPaths)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var p in alreadySaveable ?? Array.Empty<string>())
            {
                if (p != null && seen.Add(p)) result.Add(p);
            }
            foreach (var p in overriddenPaths ?? Array.Empty<string>())
            {
                if (p != null && seen.Add(p)) result.Add(p);
            }
            return result;
        }

        /// <summary>
        /// Render a path for user-facing dialogs: `.meta` paths display as `<asset> (meta)`
        /// so artists aren't confronted with a raw `.meta` extension they don't usually see
        /// in the Project window. Used by HandleLockedByOtherDialog / HandleFailedLocksDialog.
        /// </summary>
        private static string DisplayPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                ? $"{path.Substring(0, path.Length - ".meta".Length)} (meta)"
                : path;
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

        /// <summary>
        /// Phase 4 Step 5 (Wise SVN cherry-pick #7): when an asset is moved into a folder that
        /// exists locally but is NOT under SVN version control, the move would land outside source
        /// control. Walk up the destination's ancestors; if any unversioned-but-not-ignored folder
        /// is found, prompt the user to add it and (if accepted) `svn add` it before the move.
        /// </summary>
        private static void CheckAndOfferUnversionedFolderAdd(string destinationPath)
        {
            if (string.IsNullOrEmpty(destinationPath)) return;
            string destDir = System.IO.Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(destDir)) return;

            // Normalize to project-relative + forward-slash for cache lookup
            destDir = destDir.Replace('\\', '/');
            var status = FRGVersionControl.Core.SvnStatusCache.Get(destDir);
            if (status != FRGVersionControl.Core.Svn.SvnFileStatus.Untracked) return;

            bool addNow = EditorUtility.DisplayDialog(
                "Add Unversioned Folder?",
                $"The destination folder is not under SVN version control:\n\n  {destDir}\n\n" +
                "If you proceed without adding it, the moved asset will fall OUTSIDE source control. " +
                "Add the folder to SVN now (recommended)?",
                "Add to SVN", "Skip");
            if (!addNow) return;

            var r = FRGVersionControl.Core.Svn.SvnCommandRunner.Add(new[] { destDir });
            if (!r.success)
            {
                Debug.LogWarning($"[FRGVersionControl] svn add failed for unversioned folder " +
                                 $"'{destDir}': {r.error?.Split('\n')[0]}");
            }
            else
            {
                Debug.Log($"[FRGVersionControl] Added unversioned folder to SVN: {destDir}");
                _ = FRGVersionControl.Core.SvnStatusCache.RefreshAsync();
            }
        }
    }

    /// <summary>
    /// Detects newly created assets and updates lock/icon state
    /// </summary>
    public class FileLockAssetPostprocessor : AssetPostprocessor
    {
        // ---- Queue-then-defer auto-add state ----
        // Static so it survives across multiple OnPostprocessAllAssets invocations within a
        // single import batch (Unity calls it once per import). Paths queue up; debounced
        // processor flushes them via a single batched svn status + svn add.
        private static readonly HashSet<string> s_AutoAddQueue =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool s_AutoAddPending = false;
        private const double AUTO_ADD_DEBOUNCE_SECONDS = 0.8;
        private static double s_StartupEpoch = -1;

        /// <summary>
        /// Called after assets are imported/created
        /// </summary>
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (!FileLockManager.Instance.IsInitialized)
                return;

            // Phase 2 Stage 3: Mode F (silent `git restore --source` clobber) reconciliation
            // requires `post_edit_content_sha256` to be kept current after every save.
            // For each imported (== saved or re-imported) path that's in the override
            // registry, recompute the content hash. Non-override files pay zero cost
            // because IsInOverrideMode filters them out.
            if (FileLockOverrideManager.Instance.IsInitialized && importedAssets != null)
            {
                foreach (var path in importedAssets)
                {
                    if (FileLockOverrideManager.Instance.IsInOverrideMode(path))
                    {
                        FileLockOverrideManager.Instance.UpdatePostEditHash(path);
                    }
                }
            }

            // Handle moved/renamed assets
            // Lock both old path (deleted) and new path (added) to protect the full rename operation
            if (movedAssets.Length > 0)
            {
                for (int i = 0; i < movedAssets.Length; i++)
                {
                    string newPath = movedAssets[i];
                    string oldPath = movedFromAssetPaths[i];

                    // Skip .meta files
                    if (newPath.EndsWith(".meta"))
                        continue;

                    // Skip if not a lockable file type
                    if (!FileLockManager.Instance.IsLockableFileType(newPath))
                        continue;

                    // Check if this is a committed file (not untracked)
                    // The old path is tracked (being deleted), the new path appears untracked
                    bool oldPathWasTracked = !FRGVersionControl.Core.Svn.SvnCommandRunner.IsFileUncommitted(oldPath);

                    if (oldPathWasTracked)
                    {
                        // This is a rename of a committed file - lock both paths
                        Debug.Log($"[FRGVersionControl] Detected rename of committed file: {oldPath} -> {newPath}");

                        // Lock the old path (will show as deleted in Git)
                        FileLockManager.Instance.LockFile(oldPath, out string oldPathError);

                        // Lock the new path (will show as untracked/added in Git)
                        FileLockManager.Instance.LockFile(newPath, out string newPathError);

                        if (string.IsNullOrEmpty(oldPathError) && string.IsNullOrEmpty(newPathError))
                        {
                            Debug.Log($"[FRGVersionControl] Successfully locked both paths for rename:\n  Old: {oldPath}\n  New: {newPath}");
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(oldPathError))
                                Debug.LogWarning($"[FRGVersionControl] Failed to lock old path: {oldPathError}");
                            if (!string.IsNullOrEmpty(newPathError))
                                Debug.LogWarning($"[FRGVersionControl] Failed to lock new path: {newPathError}");
                        }
                    }
                }
            }

            // Check if any imported assets are lockable files
            bool hasLockableFiles = false;
            foreach (string assetPath in importedAssets)
            {
                // Skip .meta files
                if (assetPath.EndsWith(".meta"))
                    continue;

                // Check if file type is lockable
                if (FileLockManager.Instance.IsLockableFileType(assetPath))
                {
                    hasLockableFiles = true;
                    break;
                }
            }

            if (hasLockableFiles || movedAssets.Length > 0)
            {
                // Refresh locks to detect if new file is already locked by another user
                FileLockManager.Instance.RefreshLockCache();

                // Refresh git status cache and repaint to show icons
                FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();
                FileLockProjectWindowOverlay.RepaintProjectWindow();
            }

            // ---- Auto-add new files to SVN at import time ----
            // Queue truly-new paths; debounced processor (~0.8s) batches them through a single
            // `svn status` + `svn add --parents` to avoid the per-file process-spawn freeze
            // that synchronous IsVersioned checks would cause.
            //
            // Guards:
            //   - Skip during the first 5 seconds after domain reload (initial-import storm).
            //   - Skip while EditorApplication.isUpdating.
            //   - Skip during temporary-disable (batch tools).
            if (FRGVersionControl.Services.FileLockSilenceService.IsTemporarilyDisabled) return;
            if (s_StartupEpoch < 0) s_StartupEpoch = EditorApplication.timeSinceStartup;
            bool isStartupPhase = (EditorApplication.timeSinceStartup - s_StartupEpoch) < 5.0;
            if (isStartupPhase || EditorApplication.isUpdating) return;

            // Build the import set for .meta sibling logic.
            var importedSet = new HashSet<string>(importedAssets, StringComparer.OrdinalIgnoreCase);
            foreach (var p in importedAssets)
            {
                if (p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    // Skip .meta when its asset sibling is also in this batch — AddForCommit's
                    // expansion will pair them automatically. Orphan .meta (sibling not in batch)
                    // gets queued directly so it can be added on its own.
                    string sibling = p.Substring(0, p.Length - 5);
                    if (importedSet.Contains(sibling)) continue;
                }
                s_AutoAddQueue.Add(p);
            }
            if (s_AutoAddQueue.Count == 0) return;

            // Schedule the deferred processor (debounced — multiple postprocess calls coalesce).
            if (!s_AutoAddPending)
            {
                s_AutoAddPending = true;
                EditorApplication.delayCall += ScheduleAutoAddProcessor;
            }
        }

        private static void ScheduleAutoAddProcessor()
        {
            double targetTime = EditorApplication.timeSinceStartup + AUTO_ADD_DEBOUNCE_SECONDS;
            EditorApplication.update += WaitThenProcess;

            void WaitThenProcess()
            {
                if (EditorApplication.timeSinceStartup < targetTime) return;
                EditorApplication.update -= WaitThenProcess;
                ProcessAutoAddQueue();
            }
        }

        private static void ProcessAutoAddQueue()
        {
            s_AutoAddPending = false;
            if (s_AutoAddQueue.Count == 0) return;

            var paths = s_AutoAddQueue.ToArray();
            s_AutoAddQueue.Clear();

            // Refresh snapshot synchronously (single svn status call covers everything) so we
            // have accurate per-path status without N process spawns.
            FRGVersionControl.Core.SvnStatusCache.RefreshSync();

            // Build basename → Missing-path lookup for the rename-pair guard (B-2 protection).
            var missingByBasename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in FRGVersionControl.Core.SvnStatusCache.SnapshotAll())
            {
                if (kv.Value.status == FRGVersionControl.Core.Svn.SvnFileStatus.Missing)
                {
                    missingByBasename[System.IO.Path.GetFileName(kv.Key)] = kv.Key;
                }
            }

            var toAdd = new List<string>();
            foreach (var p in paths)
            {
                var status = FRGVersionControl.Core.SvnStatusCache.Get(p);
                // Cache stores only non-Normal entries — Get returning None means tracked-clean.
                // We only want Untracked here.
                if (status != FRGVersionControl.Core.Svn.SvnFileStatus.Untracked) continue;

                // Rename-pair guard: skip if a Missing row has the same basename. The user can
                // resolve via the divergence reconciler banner (which preserves rename history).
                if (missingByBasename.ContainsKey(System.IO.Path.GetFileName(p))) continue;

                toAdd.Add(p);
            }
            if (toAdd.Count == 0) return;

            var lockableExts = FileLockManager.Instance.GetLockableExtensionsSet();
            var addResult = FRGVersionControl.Core.Svn.SvnCommandRunner.AddForCommit(toAdd.ToArray(), lockableExts);
            if (!addResult.success)
            {
                Debug.LogWarning($"[FRGVersionControl] Auto-add at import failed for some paths — they'll show " +
                                 "as Untracked; use the per-row [Add to SVN] button to retry.\n" +
                                 $"Output:\n{addResult.output}\nError:\n{addResult.error}");
            }
            else
            {
                Debug.Log($"[FRGVersionControl] Auto-added {toAdd.Count} new file(s) to SVN.");
            }
            _ = FRGVersionControl.Core.SvnStatusCache.RefreshAsync();
            FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();
        }
    }
}
