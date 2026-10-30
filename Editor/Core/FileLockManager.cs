using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Services;
using FRGVersionControl.UI;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.Core.Abstractions;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Hooks;

namespace FRGVersionControl.Core
{
    /// <summary>
    /// Central manager for file locking operations (Singleton).
    ///
    /// Phase 2 Step D backend: uses native SVN locks (`svn lock` / `svn unlock` /
    /// `svn status -u --xml`) via <see cref="SvnCommandRunner"/>. The previous
    /// Git LFS path is retired.
    ///
    /// Identity (currentSvnUsername) is read from admin-config.json — Setup Wizard
    /// (Step G) populates it. If missing, Initialize() refuses to operate (fail-loud)
    /// because silent fallback to Environment.UserName would mismatch the SVN-stamped
    /// lock owner and break ownership checks invisibly.
    ///
    /// Public API is preserved for back-compat with UI/Hooks/Services callers.
    /// </summary>
    public class FileLockManager
    {
        private static FileLockManager s_Instance;
        public static FileLockManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = new FileLockManager();
                    s_Instance.Initialize();
                }
                return s_Instance;
            }
        }

        private const string ADMIN_CONFIG_PATH = "Assets/FRGVersionControl/admin-config.json";
        private const string LOCKABLE_TYPES_CONFIG_PATH = "Assets/FRGVersionControl/lockable-file-types.json";

        private FileLocks m_CachedLocks;
        private string m_UserEmail;
        private string m_UserName;
        private string m_MachineName;
        private bool m_Initialized = false;
        private HashSet<string> m_AdminEmails = new HashSet<string>();
        private HashSet<string> m_LockableExtensions = new HashSet<string>();

        public bool IsInitialized => m_Initialized && !BuildPipeline.isBuildingPlayer;
        public string UserEmail => m_UserEmail;
        public string UserName => m_UserName;
        public string MachineName => m_MachineName;

        // Phase 2 Step D: SVN reports lock owner as the authenticated username
        // (svn auth `--username admin` → owner stamp "admin"). m_UserName is populated
        // from admin-config.json `currentSvnUsername`. Comparison against this identifier
        // is case-insensitive (FileLocks.IsLockedByUser) since SVN preserves the case
        // the user authenticated with.
        private string LockOwnerIdentifier => m_UserName;

        private const string AUTO_LOCK_PREF_KEY = "FRGVersionControl.AutoLockEnabled";

        public static bool AutoLockEnabled
        {
            get => ProjectPrefs.GetBool(AUTO_LOCK_PREF_KEY, true);
            set => ProjectPrefs.SetBool(AUTO_LOCK_PREF_KEY, value);
        }

        // ====================================================================
        // svn-core IPC singleton (lazy)
        //
        // Provides shared IpcSvnClient access for call sites being migrated off
        // the legacy SvnCommandRunner statics. Constructed on first access; the
        // underlying SvnCoreProcess locates the bundled svn-core.exe under the
        // UPM package's Editor/SvnCore~/ directory.
        // ====================================================================
        private IpcSvnClient m_IpcClient;
        public IpcSvnClient IpcClient
        {
            get
            {
                if (m_IpcClient == null)
                {
                    var logger = new UnityLogger("FRGVersionControl.Ipc");
                    var paths = new UnityProjectPaths();
                    var process = new SvnCoreProcess(logger, paths);
                    m_IpcClient = new IpcSvnClient(process, logger, paths);
                }
                return m_IpcClient;
            }
        }

        private void Initialize()
        {
            // First-run scaffold: copy package defaults into Assets/FRGVersionControl/ if missing.
            // Must run before any config-read; the IsFRGVersionControlEnabled() guard below
            // reads admin-config.json and would fail on a fresh package install otherwise.
            FileLockPackagePaths.EnsureProjectConfig("admin-config.json");
            FileLockPackagePaths.EnsureProjectConfig("lockable-file-types.json");

            // Check if file locking is globally disabled via configuration
            if (!IsFRGVersionControlEnabled())
            {
                Debug.Log("[FRGVersionControl] File locking is globally disabled via admin-config.json");
                m_Initialized = false;
                return;
            }

            if (!SvnCommandRunner.IsSvnAvailable())
            {
                Debug.LogError("[FRGVersionControl] svn is not available. File locking disabled. " +
                               "Install TortoiseSVN with command-line client tools at the default location.");
                return;
            }

            // Load admin config — display name + email come from there (used for
            // history records). SVN username is NOT stored in any committed file
            // (Phase 3 decision); it's read at runtime from SVN's auth cache via
            // SvnAuthCacheReader.
            LoadAdminConfig();

            // Identity bootstrap: derive SVN username from SVN's own auth cache. Each
            // Unity instance uses a per-WC config dir (SvnCommandRunner.ToolingConfigDir)
            // so two Unity instances on the same machine each have their own cache.
            string cachedUsername = null;
            try
            {
                cachedUsername = FRGVersionControl.Core.Svn.SvnAuthCacheReader.GetUsernameForCurrentRepo();
            }
            catch (Exception cacheEx)
            {
                Debug.LogWarning($"[FRGVersionControl] Auth cache read failed: {cacheEx.Message}");
            }

            if (!string.IsNullOrEmpty(cachedUsername))
            {
                m_UserName = cachedUsername;
            }
            else if (!string.IsNullOrEmpty(m_UserName))
            {
                // Back-compat: admin-config.json's deprecated currentSvnUsername field is
                // still respected if present, with a warning. Remove the field once your
                // WC has been bootstrapped (svn info --config-dir <ProjectRoot>/.svn-tooling-config).
                Debug.LogWarning(
                    "[FRGVersionControl] Using deprecated currentSvnUsername from admin-config.json " +
                    $"('{m_UserName}'). Remove this field after bootstrapping the per-WC SVN " +
                    "auth cache by running once from a terminal:\n" +
                    $"  svn info <repo-url> --config-dir \"{FRGVersionControl.Core.Svn.SvnCommandRunner.ToolingConfigDir}\" " +
                    "--username <you> --trust-server-cert-failures unknown-ca,cn-mismatch");
            }

            // FAIL LOUD if no username could be derived from either source.
            if (string.IsNullOrEmpty(m_UserName))
            {
                Debug.LogError(
                    "[FRGVersionControl] No SVN username found. Bootstrap the per-WC auth cache by " +
                    "running once from a terminal in the project root:\n" +
                    $"  svn info <repo-url> --config-dir \"{FRGVersionControl.Core.Svn.SvnCommandRunner.ToolingConfigDir}\" " +
                    "--username <you> --trust-server-cert-failures unknown-ca,cn-mismatch\n" +
                    "Then re-open Unity. SVN will prompt for password and cache it locally.");

                // Show a one-shot modal dialog the first time this happens per session,
                // surfacing the Setup Wizard so artists don't have to dig through Console.
                const string SessionShownKey = "FRGVersionControl.SetupDialogShownThisSession";
                if (!SessionState.GetBool(SessionShownKey, false))
                {
                    SessionState.SetBool(SessionShownKey, true);
                    EditorApplication.delayCall += () =>
                    {
                        bool openWizard = EditorUtility.DisplayDialog(
                            "FRG Version Control: SVN Setup Required",
                            "FRG Version Control needs your SVN credentials cached locally before it can manage locks.\n\n" +
                            "Click \"Open Setup Wizard\" to walk through the bootstrap (it copies the command to your clipboard and opens a terminal in this project root for you).\n\n" +
                            "Until that's done, lock/unlock/commit/update from Unity will not work.",
                            "Open Setup Wizard",
                            "Later");
                        if (openWizard)
                        {
                            FRGVersionControl.Tools.FileLockSetupWizard.ShowWizard();
                        }
                    };
                }

                m_Initialized = false;
                return;
            }

            m_MachineName = System.Environment.MachineName;
            m_CachedLocks = new FileLocks();

            // Load lockable extensions
            LoadLockableFileTypesConfig();

            Debug.Log($"[FRGVersionControl] Initialized for user: {m_UserName} ({m_UserEmail}) [SVN backend]");
            m_Initialized = true;

            // Initial cache populate (best-effort; failures are non-fatal here —
            // background sync will retry every 5 min)
            try { RefreshLockCache(); } catch { /* ignore at init */ }
        }

        /// <summary>
        /// Load admin configuration from admin-config.json. Phase 2 Step D: also loads
        /// per-user identity fields (currentSvnUsername, currentDisplayName, currentEmail)
        /// since SVN has no `git config user.*` equivalent.
        /// </summary>
        private void LoadAdminConfig()
        {
            m_AdminEmails.Clear();
            m_UserName = null;
            m_UserEmail = null;

            string configPath = Path.Combine(SvnCommandRunner.ProjectRoot, ADMIN_CONFIG_PATH);
            if (!File.Exists(configPath))
            {
                Debug.LogWarning("[FRGVersionControl] No admin config found at " + configPath);
                return;
            }

            try
            {
                string json = File.ReadAllText(configPath);
                var config = JsonUtility.FromJson<AdminConfig>(json);

                if (config != null && config.adminEmails != null)
                {
                    foreach (var email in config.adminEmails)
                    {
                        if (!string.IsNullOrEmpty(email))
                        {
                            m_AdminEmails.Add(email.ToLowerInvariant());
                        }
                    }
                }

                if (config != null)
                {
                    m_UserName = config.currentSvnUsername;
                    m_UserEmail = config.currentEmail;
                    // currentDisplayName is unused at the manager level today; UI may consume later.
                }

                Debug.Log($"[FRGVersionControl] Loaded admin config: {m_AdminEmails.Count} admin email(s); " +
                          $"identity: username='{m_UserName}', email='{m_UserEmail}'");
            }
            catch (Exception e)
            {
                Debug.LogError($"[FRGVersionControl] Failed to load admin config: {e.Message}");
            }
        }

        /// <summary>
        /// Load lockable file types configuration from lockable-file-types.json
        /// </summary>
        private void LoadLockableFileTypesConfig()
        {
            m_LockableExtensions.Clear();

            string configPath = Path.Combine(SvnCommandRunner.ProjectRoot, LOCKABLE_TYPES_CONFIG_PATH);
            if (!File.Exists(configPath))
            {
                Debug.LogWarning("[FRGVersionControl] No lockable file types config found. All files will be lockable.");
                return;
            }

            try
            {
                string json = File.ReadAllText(configPath);
                var config = JsonUtility.FromJson<LockableFileTypesConfig>(json);

                if (config != null && config.lockableExtensions != null)
                {
                    foreach (var ext in config.lockableExtensions)
                    {
                        if (!string.IsNullOrEmpty(ext))
                        {
                            m_LockableExtensions.Add(ext.ToLowerInvariant());
                        }
                    }
                    Debug.Log($"[FRGVersionControl] Loaded {m_LockableExtensions.Count} lockable file type(s)");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[FRGVersionControl] Failed to load lockable file types config: {e.Message}");
            }
        }

        /// <summary>
        /// Check if file locking is globally enabled via configuration
        /// </summary>
        private bool IsFRGVersionControlEnabled()
        {
            string configPath = Path.Combine(SvnCommandRunner.ProjectRoot, ADMIN_CONFIG_PATH);
            if (!File.Exists(configPath))
            {
                // If config doesn't exist, default to enabled
                return true;
            }

            try
            {
                string json = File.ReadAllText(configPath);
                var config = JsonUtility.FromJson<AdminConfig>(json);

                // If config exists but field is not set, default to enabled
                return config?.fileLockingEnabled ?? true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FRGVersionControl] Failed to read enabled state from config: {e.Message}. Defaulting to enabled.");
                return true;
            }
        }

        /// <summary>
        /// Check if the current user is an admin (can force unlock fresh locks)
        /// </summary>
        public bool IsUserAdmin()
        {
            return m_AdminEmails.Contains(m_UserEmail?.ToLowerInvariant());
        }

        /// <summary>
        /// Translate a path to the form that should be checked against the lockable-extensions
        /// set: `.meta` paths return their underlying asset path (so a meta is lockable iff
        /// its asset is lockable); all other paths pass through unchanged. Pure path logic —
        /// does not touch the file system. Used by the OnWillSaveAssets meta-aware gate.
        /// </summary>
        public static string GetLockabilityCheckPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                ? path.Substring(0, path.Length - ".meta".Length)
                : path;
        }

        /// <summary>
        /// Check if a file type is lockable based on configuration
        /// </summary>
        public bool IsLockableFileType(string assetPath)
        {
            // If no config loaded or empty, allow all files
            if (m_LockableExtensions.Count == 0)
                return true;

            string ext = Path.GetExtension(assetPath)?.ToLowerInvariant();
            return !string.IsNullOrEmpty(ext) && m_LockableExtensions.Contains(ext);
        }

        /// <summary>Return the lockable-extension set for callers that need O(1) batch lookup
        /// (e.g., `SvnCommandRunner.AddForCommit` which propsets `svn:needs-lock` on matching
        /// paths). Returns the live HashSet — caller should treat it as read-only.</summary>
        public HashSet<string> GetLockableExtensionsSet()
        {
            return m_LockableExtensions;
        }

        /// <summary>
        /// Refresh lock cache. 0.2.44 — routes through svn-core IPC
        /// (status.allEntries with includeUpstream:true) when available; falls back to
        /// the legacy SvnCommandRunner.StatusUpstreamAll on IPC failure.
        ///
        /// Stays sync (returns bool) for source-compat with the many call sites that
        /// invoke it from sync contexts (focus changes, wc.db mtime polls, the
        /// 5-minute timer). The blocking semantics match the legacy path's
        /// Process.Start + WaitForExit pattern.
        /// </summary>
        public bool RefreshLockCache()
        {
            if (!m_Initialized) return false;

            try
            {
                var svnLockMap = TryRefreshViaIpc();
                if (svnLockMap == null) return false;
                m_CachedLocks = FileLocks.FromSvnLockInfoMap(svnLockMap, LockOwnerIdentifier);

                Debug.Log($"[FRGVersionControl] Refreshed locks: {m_CachedLocks.locks.Count} active locks");

                // Refresh history cache (tied to lock refresh)
                FileLockHistoryService.RefreshMergedHistory();

                // Repaint Project window to update lock icons
                FileLockProjectWindowOverlay.RepaintProjectWindow();

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[FRGVersionControl] Failed to refresh lock cache: {e.Message}");
                return false;
            }
        }

        private Dictionary<string, SvnLockInfo> TryRefreshViaIpc()
        {
            try
            {
                var client = IpcClient;
                if (!client.IsAvailable) return null;
                // Block via GetAwaiter().GetResult — safe because InvokeRawAsync internally
                // uses ConfigureAwait(false) end-to-end and doesn't capture the editor
                // sync context. Same blocking profile as the legacy CLI invocation.
                var r = client.TryGetLockMapAsync().GetAwaiter().GetResult();
                if (r.Ok) return r.Value;
                Debug.LogWarning(r.Busy
                    ? $"[FRGVersionControl] Lock refresh skipped (prior cache kept): {r.Error}"
                    : $"[FRGVersionControl] Lock refresh failed (prior cache kept): {r.Error}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FRGVersionControl] Lock refresh failed (prior cache kept): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Lock a file via `svn lock`. When <paramref name="skipLockableCheck"/> is true the
        /// caller has already verified lockability against the underlying asset (used for
        /// .meta paths — `.meta` is never in the lockable-extensions set, but locking the
        /// meta of a lockable asset is the whole point of the meta auto-lock path).
        /// </summary>
        public bool LockFile(string assetPath, out string errorMessage, bool skipLockableCheck = false)
        {
            errorMessage = null;

            if (!m_Initialized)
            {
                errorMessage = "File locking not initialized";
                return false;
            }

            // Check if file type is lockable (silently skip if not). Callers locking a
            // .meta path pass skipLockableCheck=true after verifying the underlying asset.
            if (!skipLockableCheck && !IsLockableFileType(assetPath))
            {
                return false;
            }

            // Refresh to get latest state (defense-in-depth; LFS server arbitrates atomically
            // so a stale local cache won't cause double-locks, but we want UI consistency).
            RefreshLockCache();

            // Check existing lock
            var existingLock = m_CachedLocks.FindLock(assetPath);
            if (existingLock != null)
            {
                if (string.Equals(existingLock.owner, LockOwnerIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    // Already locked by us
                    return true;
                }
                else
                {
                    errorMessage = $"File is locked by {existingLock.ownerName} ({existingLock.owner})";
                    return false;
                }
            }

            // Acquire SVN lock
            var result = SvnCommandRunner.Lock(new[] { assetPath }, "[FRGVersionControl] lock acquired");
            if (!result.success)
            {
                errorMessage = $"Failed to acquire SVN lock: {(string.IsNullOrEmpty(result.error) ? result.output : result.error)}";
                Debug.LogWarning($"[FRGVersionControl] {errorMessage}");
                return false;
            }

            // Refresh cache to pick up the new lock
            RefreshLockCache();

            // Record history
            FileLockHistoryService.RecordLock(assetPath, m_UserEmail, m_UserName, m_MachineName);

            // Repaint Project window to show lock icon
            FileLockProjectWindowOverlay.RepaintProjectWindow();

            Debug.Log($"[FRGVersionControl] Locked: {assetPath}");
            return true;
        }

        /// <summary>
        /// Unlock a file via `git lfs unlock` (or `git lfs unlock --force`).
        /// </summary>
        /// <param name="assetPath">Path to the asset</param>
        /// <param name="force">Force unlock even if owned by another user (admin operation)</param>
        /// <param name="skipRevertDialog">Skip the revert dialog (used when called from RevertAndUnlockFile)</param>
        public bool UnlockFile(string assetPath, bool force = false, bool skipRevertDialog = false)
        {
            if (!m_Initialized) return false;

            // Refresh cache
            RefreshLockCache();

            var existingLock = m_CachedLocks.FindLock(assetPath);
            if (existingLock == null)
            {
                // Not locked
                return true;
            }

            // Permission: own the lock OR force-unlock as admin
            bool isLockedByMe = string.Equals(existingLock.owner, LockOwnerIdentifier, StringComparison.OrdinalIgnoreCase);
            bool canUnlock = force || isLockedByMe;

            if (!canUnlock)
            {
                Debug.LogWarning($"[FRGVersionControl] Cannot unlock file owned by {existingLock.ownerName}. Use force-unlock if you have admin privileges.");
                return false;
            }

            // Check if file has uncommitted changes (only for files locked by us, unless forced)
            if (!skipRevertDialog && (isLockedByMe || force))
            {
                bool hasChanges = SvnCommandRunner.HasUncommittedChanges(assetPath);

                if (hasChanges)
                {
                    int choice = EditorUtility.DisplayDialogComplex(
                        "Unlock File with Changes",
                        $"The file '{Path.GetFileName(assetPath)}' has uncommitted changes.\n\n" +
                        "What would you like to do?",
                        "Revert Changes",  // option 0
                        "Cancel",          // option 1
                        "Keep Changes"     // option 2
                    );

                    if (choice == 1)
                    {
                        Debug.Log($"[FRGVersionControl] Unlock cancelled by user for: {assetPath}");
                        return false;
                    }
                    else if (choice == 0)
                    {
                        bool reverted = SvnCommandRunner.Revert(new[] { assetPath }, alsoRevertMeta: true).success;
                        if (!reverted)
                        {
                            Debug.LogError($"[FRGVersionControl] Failed to revert file: {assetPath}");
                            return false;
                        }

                        Debug.Log($"[FRGVersionControl] Reverted changes for: {assetPath}");
                        AssetDatabase.Refresh();
                    }
                    // choice == 2: Keep Changes — continue with unlock without reverting
                }
            }

            // Release SVN lock
            var result = SvnCommandRunner.Unlock(new[] { assetPath }, force: force);
            if (!result.success)
            {
                Debug.LogError($"[FRGVersionControl] Failed to release SVN lock: {(string.IsNullOrEmpty(result.error) ? result.output : result.error)}");
                return false;
            }

            // Refresh cache
            RefreshLockCache();

            // Record history
            FileLockHistoryService.RecordUnlock(assetPath, m_UserEmail, m_UserName, m_MachineName, force);

            // Repaint Project window to remove lock icon
            FileLockProjectWindowOverlay.RepaintProjectWindow();

            Debug.Log($"[FRGVersionControl] Unlocked: {assetPath}");
            return true;
        }

        /// <summary>
        /// Check if file is locked
        /// </summary>
        public FileLock GetFileLock(string assetPath)
        {
            return m_CachedLocks?.FindLock(assetPath);
        }

        /// <summary>
        /// Check if file is locked by current user.
        /// (Phase 2 LFS rewrite: same-user-different-machine is invisible — LFS doesn't track machine.)
        /// </summary>
        public bool IsLockedByMe(string assetPath)
        {
            // Phase 2 LFS rewrite: pass username (LFS owner identifier), not email.
            return m_CachedLocks?.IsLockedByUser(assetPath, LockOwnerIdentifier, m_MachineName) ?? false;
        }

        /// <summary>
        /// Check if file is locked by someone else.
        /// </summary>
        public bool IsLockedByOther(string assetPath)
        {
            return m_CachedLocks?.IsLockedByOther(assetPath, LockOwnerIdentifier, m_MachineName) ?? false;
        }

        /// <summary>
        /// Get all current locks (for UI display)
        /// </summary>
        public List<FileLock> GetAllLocks()
        {
            return m_CachedLocks?.locks ?? new List<FileLock>();
        }

        /// <summary>
        /// Revert a file to its last committed state and unlock it
        /// </summary>
        public bool RevertAndUnlockFile(string assetPath)
        {
            if (!m_Initialized) return false;
            if (!IsLockedByMe(assetPath))
            {
                Debug.LogWarning($"[FRGVersionControl] Cannot revert and unlock - file is not locked by you: {assetPath}");
                return false;
            }
            var result = RevertAndUnlockFilesAsync(new[] { assetPath }).GetAwaiter().GetResult();
            ApplyRevertUnlockCompletion(result);
            foreach (var f in result.GetFailed())
            {
                Debug.LogError($"[FRGVersionControl] Revert and unlock failed for {f.path}: {f.errorMessage}");
            }
            return result.allSucceeded;
        }

        /// <summary>
        /// Batched revert-and-unlock (0.2.89): one <c>mutate.revert</c> for every path (plus
        /// .meta siblings), one <c>lock.release</c> for the paths the lock cache says are mine
        /// AND whose revert succeeded or was unchanged, one history record, one lock-cache
        /// refresh. IPC only — safe off the main thread; the caller runs
        /// <see cref="ApplyRevertUnlockCompletion"/> on the main thread afterwards.
        /// </summary>
        public async Task<RevertUnlockBatchResult> RevertAndUnlockFilesAsync(IList<string> assetPaths, IProgress<string> progress = null)
        {
            var result = new RevertUnlockBatchResult();
            if (assetPaths == null || assetPaths.Count == 0) return result;
            if (!m_Initialized)
            {
                foreach (var p in assetPaths) result.Add(p, false, "File locking not initialized. Run the Setup Wizard, then retry.");
                return result;
            }

            var lockedByMe = new HashSet<string>(assetPaths.Where(IsLockedByMe), StringComparer.OrdinalIgnoreCase);
            progress?.Report($"Reverting {assetPaths.Count} file(s)…");
            var revert = await SvnCommandRunner.RevertDetailedAsync(assetPaths.ToArray(), alsoRevertMeta: true).ConfigureAwait(false);

            var toUnlock = assetPaths.Where(p => lockedByMe.Contains(p) && !revert.IsUnknown(p) && revert.FailureFor(p) == null
                                                 && (revert.success || revert.WasReverted(p) || revert.IsUnchanged(p))).ToList();
            UnlockOutcome unlock = null;
            Debug.Log($"[FRGVersionControl] revert+unlock batch: {assetPaths.Count} path(s), {lockedByMe.Count} locked by me, revert reverted={revert.reverted.Count} failed={revert.failed.Count} unchanged={revert.unchanged.Count} unknown={revert.unknown.Count}, unlocking {toUnlock.Count}");
            if (toUnlock.Count > 0)
            {
                progress?.Report($"Releasing {toUnlock.Count} lock(s)…");
                unlock = await SvnCommandRunner.UnlockDetailedAsync(toUnlock.ToArray(), force: false).ConfigureAwait(false);
            }

            foreach (var p in assetPaths)
            {
                if (revert.IsUnknown(p))
                {
                    result.unknownPaths.Add(p);
                    result.unknownReason = revert.unknownReason;
                    result.Add(p, false, "Outcome unknown: " + revert.unknownReason);
                    continue;
                }
                var rf = revert.FailureFor(p);
                if (rf != null)
                {
                    result.Add(p, false, "Revert failed: " + rf.error);
                    continue;
                }
                if (!revert.success && !revert.WasReverted(p) && !revert.IsUnchanged(p))
                {
                    result.Add(p, false, "Revert failed: " + (revert.error ?? "no response for this path. Refresh status before retrying."));
                    continue;
                }
                if (revert.WasReverted(p)) result.revertedPaths.Add(p);

                if (!lockedByMe.Contains(p))
                {
                    result.Add(p, true, "Reverted; was not locked by you");
                    continue;
                }
                if (unlock != null && unlock.WasReleased(p))
                {
                    result.releasedPaths.Add(p);
                    result.Add(p, true);
                    continue;
                }
                if (unlock != null && unlock.IsUnknown(p))
                {
                    result.unknownPaths.Add(p);
                    result.unknownReason = unlock.unknownReason;
                    result.Add(p, false, "Reverted; lock outcome unknown: " + unlock.unknownReason);
                    continue;
                }
                var uf = unlock?.FailureFor(p);
                var detail = uf != null ? uf.line : (unlock?.error ?? "no response for this path.");
                var suffix = detail.TrimEnd().EndsWith(".") && detail.Contains("Refresh status") ? "" : " Refresh status and retry the unlock.";
                result.Add(p, false, $"Reverted, but the lock was not released: {detail}{suffix}");
            }

            if (result.releasedPaths.Count > 0)
            {
                await FileLockHistoryService.RecordUnlocksIpcAsync(result.releasedPaths, m_UserEmail, m_UserName, m_MachineName, false).ConfigureAwait(false);
            }
            RefreshLockCache();
            return result;
        }

        /// <summary>Main-thread completion for <see cref="RevertAndUnlockFilesAsync"/>: local
        /// history write, asset reimport, Project-window repaint.</summary>
        public void ApplyRevertUnlockCompletion(RevertUnlockBatchResult result)
        {
            if (result == null) return;
            if (result.releasedPaths.Count > 0)
            {
                FileLockHistoryService.RecordUnlocksLocal(result.releasedPaths, m_UserEmail, m_UserName, m_MachineName, false);
            }
            if (result.revertedPaths.Count > 0)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
            FileLockProjectWindowOverlay.RepaintProjectWindow();
        }

        /// <summary>
        /// Lock multiple files. Phase 2 LFS rewrite: best-effort per-file (LFS has no batch
        /// lock API; each `git lfs lock` is independent server-side). The previous implementation
        /// was transactional via single commit/push — that semantics no longer applies.
        /// Already-acquired locks are NOT rolled back if a subsequent file fails.
        /// When <paramref name="skipLockableCheck"/> is true, all input paths are treated as
        /// lockable — used by the meta auto-lock path where callers have verified lockability
        /// via the underlying asset (`.meta` itself is never in the lockable-extensions set).
        /// </summary>
        public BatchLockResult BatchLockFiles(List<string> assetPaths, bool skipLockableCheck = false)
        {
            var result = new BatchLockResult();

            if (!m_Initialized)
            {
                foreach (var path in assetPaths ?? new List<string>())
                {
                    result.AddResult(path, false, "File locking not initialized");
                }
                return result;
            }

            if (assetPaths == null || assetPaths.Count == 0)
            {
                return result;
            }

            // Filter non-lockable (skipped entirely when caller has pre-verified lockability)
            var lockablePaths = skipLockableCheck ? assetPaths : assetPaths.Where(p => IsLockableFileType(p)).ToList();
            var nonLockablePaths = skipLockableCheck ? new List<string>() : assetPaths.Where(p => !IsLockableFileType(p)).ToList();

            foreach (var path in nonLockablePaths)
            {
                result.AddResult(path, true, "File type not lockable (skipped)");
            }

            if (lockablePaths.Count == 0)
            {
                return result;
            }

            // Refresh once before iterating (defense-in-depth)
            RefreshLockCache();

            int successCount = 0;
            foreach (var assetPath in lockablePaths)
            {
                var existingLock = m_CachedLocks.FindLock(assetPath);
                if (existingLock != null)
                {
                    if (string.Equals(existingLock.owner, LockOwnerIdentifier, StringComparison.OrdinalIgnoreCase))
                    {
                        result.AddResult(assetPath, true, "Already locked by you");
                        continue;
                    }
                    result.AddResult(assetPath, false,
                        $"File is locked by {existingLock.ownerName} ({existingLock.owner})", existingLock);
                    continue;
                }

                var lockResult = SvnCommandRunner.Lock(new[] { assetPath }, "[FRGVersionControl] batch lock");
                if (lockResult.success)
                {
                    result.AddResult(assetPath, true);
                    FileLockHistoryService.RecordLock(assetPath, m_UserEmail, m_UserName, m_MachineName);
                    successCount++;
                }
                else
                {
                    result.AddResult(assetPath, false,
                        $"Failed to acquire SVN lock: {(string.IsNullOrEmpty(lockResult.error) ? lockResult.output : lockResult.error)}");
                }
            }

            // Single refresh + repaint at the end
            if (successCount > 0)
            {
                RefreshLockCache();
                FileLockProjectWindowOverlay.RepaintProjectWindow();
                Debug.Log($"[FRGVersionControl] Batch locked {successCount} file(s) (best-effort; partial failures preserved)");
            }

            return result;
        }

        /// <summary>
        /// Unlock multiple files. Phase 2 LFS rewrite: best-effort per-file. Revert dialog
        /// is shown once up front for any files with uncommitted changes; the user's choice
        /// applies to all of them in this batch.
        /// </summary>
        public BatchUnlockResult BatchUnlockFiles(List<string> assetPaths, bool force = false, bool skipRevertDialog = false)
        {
            var result = new BatchUnlockResult();

            if (!m_Initialized)
            {
                foreach (var path in assetPaths ?? new List<string>())
                {
                    result.AddResult(path, false, "File locking not initialized");
                }
                return result;
            }

            if (assetPaths == null || assetPaths.Count == 0)
            {
                return result;
            }

            RefreshLockCache();

            // Categorize
            var filesToUnlock = new List<(string path, FileLock existingLock)>();
            var filesWithChanges = new List<string>();
            var unauthorizedFiles = new List<(string path, FileLock existingLock)>();

            foreach (var assetPath in assetPaths)
            {
                var existingLock = m_CachedLocks.FindLock(assetPath);
                if (existingLock == null)
                {
                    result.AddResult(assetPath, true, "File was not locked");
                    continue;
                }

                bool isLockedByMe = string.Equals(existingLock.owner, LockOwnerIdentifier, StringComparison.OrdinalIgnoreCase);
                bool canUnlock = force || isLockedByMe;

                if (!canUnlock)
                {
                    unauthorizedFiles.Add((assetPath, existingLock));
                    continue;
                }

                filesToUnlock.Add((assetPath, existingLock));
            }

            if (!skipRevertDialog && filesToUnlock.Count > 0)
            {
                var changed = SvnCommandRunner.FilterChanged(filesToUnlock.Select(f => f.path).ToArray());
                if (changed == null)
                {
                    EditorUtility.DisplayDialog("Unlock Files",
                        "Could not read SVN status for the selection. Wait for the in-progress operation to finish, then retry.", "OK");
                    foreach (var (path, _) in filesToUnlock) result.AddResult(path, false, "Unlock skipped: SVN status unavailable. Retry after the in-progress operation finishes.");
                    return result;
                }
                filesWithChanges.AddRange(changed);
            }

            // Unauthorized files
            foreach (var (path, existingLock) in unauthorizedFiles)
            {
                result.AddResult(path, false,
                    $"Cannot unlock file owned by {existingLock.ownerName} (force-unlock requires admin)",
                    existingLock);
            }

            // Single dialog for files with changes
            bool revertChanges = false;
            if (!skipRevertDialog && filesWithChanges.Count > 0)
            {
                string fileList = string.Join("\n", filesWithChanges.Take(10).Select(f => $"  • {Path.GetFileName(f)}"));
                if (filesWithChanges.Count > 10)
                {
                    fileList += $"\n  ... and {filesWithChanges.Count - 10} more";
                }

                int choice = EditorUtility.DisplayDialogComplex(
                    "Unlock Files with Changes",
                    $"{filesWithChanges.Count} file(s) have uncommitted changes:\n\n{fileList}\n\n" +
                    "What would you like to do?",
                    "Revert Changes",  // option 0
                    "Cancel",          // option 1
                    "Keep Changes"     // option 2
                );

                if (choice == 1)
                {
                    foreach (var path in assetPaths)
                    {
                        result.AddResult(path, false, "Unlock cancelled by user");
                    }
                    return result;
                }

                revertChanges = (choice == 0);
            }

            // Revert if requested
            if (revertChanges && filesWithChanges.Count > 0)
            {
                var revert = SvnCommandRunner.RevertDetailedAsync(filesWithChanges.ToArray(), alsoRevertMeta: true).GetAwaiter().GetResult();
                foreach (var path in filesWithChanges)
                {
                    if (revert.WasReverted(path))
                    {
                        result.revertedPaths.Add(path);
                        result.revertedCount++;
                    }
                    else
                    {
                        var rf = revert.FailureFor(path);
                        Debug.LogError($"[FRGVersionControl] Failed to revert file: {path}: {rf?.error ?? revert.unknownReason ?? revert.error}");
                    }
                }

                if (result.revertedCount > 0)
                {
                    AssetDatabase.Refresh();
                }
            }

            var unlock = SvnCommandRunner.UnlockDetailedAsync(filesToUnlock.Select(f => f.path).ToArray(), force: force).GetAwaiter().GetResult();
            var released = new List<string>();
            foreach (var (assetPath, existingLock) in filesToUnlock)
            {
                if (unlock.WasReleased(assetPath))
                {
                    result.AddResult(assetPath, true);
                    released.Add(assetPath);
                }
                else if (unlock.IsUnknown(assetPath))
                {
                    result.AddResult(assetPath, false, "Outcome unknown: " + unlock.unknownReason);
                }
                else
                {
                    var uf = unlock.FailureFor(assetPath);
                    result.AddResult(assetPath, false,
                        $"Failed to release SVN lock: {uf?.line ?? unlock.error ?? "no response for this path"} Refresh status and retry.");
                }
            }

            if (released.Count > 0)
            {
                FileLockHistoryService.RecordUnlocksLocal(released, m_UserEmail, m_UserName, m_MachineName, force);
                FileLockHistoryService.RecordUnlocksIpcAsync(released, m_UserEmail, m_UserName, m_MachineName, force).GetAwaiter().GetResult();
                RefreshLockCache();
                FileLockProjectWindowOverlay.RepaintProjectWindow();
                Debug.Log($"[FRGVersionControl] Batch unlocked {released.Count} file(s) (best-effort)");
            }

            return result;
        }
    }

    /// <summary>
    /// Admin configuration data structure (admin-config.json schema).
    /// </summary>
    [Serializable]
    public class AdminConfig
    {
        public string[] adminEmails;
        public bool fileLockingEnabled = true;

        /// <summary>
        /// SVN username this user authenticates as (e.g. "admin" or "artist"). MUST match
        /// the username SVN stamps on locks; otherwise IsLockedByMe returns false silently.
        /// Phase 2 Step D: required field — Initialize() refuses to operate if unset.
        /// </summary>
        public string currentSvnUsername;

        /// <summary>Human-readable display name for UI / history. Optional.</summary>
        public string currentDisplayName;

        /// <summary>Contact email for history / audit. Optional.</summary>
        public string currentEmail;
    }

    /// <summary>
    /// Lockable file types configuration data structure
    /// </summary>
    [Serializable]
    public class LockableFileTypesConfig
    {
        public string[] lockableExtensions;
    }
}
