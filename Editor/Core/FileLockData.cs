using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FRGVersionControl.Core
{
    /// <summary>
    /// Represents a single file lock entry
    /// </summary>
    [Serializable]
    public class FileLock
    {
        public string path;           // Relative path from project root (e.g., "Assets/Scenes/Main.unity")
        public string owner;          // SVN username (lock-owner identifier; misnamed for legacy)
        public string ownerName;      // Display name
        public string machineName;    // Computer name
        public string lockedAt;       // ISO 8601 timestamp (UTC)
        public string lockId;         // GUID for unique identification
        public string lockState;      // SVN lock state: "K" | "O" | "B" | "T" | "" — surfaces broken/stolen states to UI (Phase 2 closure Test 5).

        public FileLock(string path, string owner, string ownerName, string machineName)
        {
            this.path = path;
            this.owner = owner;
            this.ownerName = ownerName;
            this.machineName = machineName;
            this.lockedAt = DateTime.UtcNow.ToString("O");
            this.lockId = Guid.NewGuid().ToString();
        }

        public DateTime GetLockedAtTime()
        {
            return DateTime.Parse(lockedAt).ToUniversalTime();
        }

        public DateTime GetLockedAtTimeLocal()
        {
            return DateTime.Parse(lockedAt).ToLocalTime();
        }

        public bool IsStale(int staleThresholdHours = 24)
        {
            var lockTime = GetLockedAtTime();
            var elapsed = DateTime.UtcNow - lockTime;
            return elapsed.TotalHours > staleThresholdHours;
        }
    }

    /// <summary>
    /// Root container for all locks (serialized to FileLocks.json)
    /// </summary>
    [Serializable]
    public class FileLocks
    {
        public int version = 1;
        public List<FileLock> locks = new List<FileLock>();

        public FileLock FindLock(string path)
        {
            return locks.Find(l => l.path == path);
        }

        public bool IsLockedByUser(string path, string userEmail, string machineName)
        {
            // Phase 2 Step D: SVN backend. The "userEmail" parameter is misnamed for legacy
            // back-compat — it is actually the SVN username (lock-owner identifier).
            // Comparison is case-insensitive because SVN preserves the case the user
            // authenticated with; case drift between admin-config.json and SVN auth
            // would otherwise silently break ownership checks.
            // The machineName parameter is retained for API back-compat but ignored
            // (SVN doesn't track machine in lock metadata; same as LFS).
            var fileLock = FindLock(path);
            return fileLock != null &&
                   string.Equals(fileLock.owner, userEmail, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsLockedByOther(string path, string userEmail, string machineName)
        {
            // Phase 2 Step D: case-insensitive compare; see note above.
            var fileLock = FindLock(path);
            return fileLock != null &&
                   !string.Equals(fileLock.owner, userEmail, StringComparison.OrdinalIgnoreCase);
        }

        public void AddLock(FileLock fileLock)
        {
            // Remove existing lock for same path if present
            locks.RemoveAll(l => l.path == fileLock.path);
            locks.Add(fileLock);
        }

        public bool RemoveLock(string path)
        {
            return locks.RemoveAll(l => l.path == path) > 0;
        }

        public string ToJson()
        {
            return JsonUtility.ToJson(this, true);
        }

        public static FileLocks FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new FileLocks();
            }
            return JsonUtility.FromJson<FileLocks>(json);
        }

        /// <summary>
        /// Phase 2 Step D: build a FileLocks container from SvnCommandRunner's
        /// StatusUpstreamAll output. Replaces FromLfsLocksJson.
        /// </summary>
        /// <param name="svnLockMap">Dictionary keyed by working-copy-relative path → SvnLockInfo.</param>
        /// <param name="selfUsername">Caller's SVN username (LockOwnerIdentifier). When a lock's
        /// owner matches, machineName is set to Environment.MachineName for UI display
        /// of "you locked this from this machine." When it doesn't match, machineName is "".</param>
        public static FileLocks FromSvnLockInfoMap(
            System.Collections.Generic.IDictionary<string, FRGVersionControl.Core.Svn.SvnLockInfo> svnLockMap,
            string selfUsername)
        {
            var container = new FileLocks();
            if (svnLockMap == null) return container;

            foreach (var kv in svnLockMap)
            {
                var info = kv.Value;
                if (info == null) continue;
                if (info.state == FRGVersionControl.Core.Svn.LockState.None) continue;

                var ownerStr = info.lockOwner ?? "";
                bool isSelf = !string.IsNullOrEmpty(selfUsername)
                              && string.Equals(ownerStr, selfUsername, StringComparison.OrdinalIgnoreCase);

                // SVN's status -u --xml emits paths with backslashes on Windows
                // (e.g., "Assets\Scenes\Foo.mat"). Unity Asset paths are always forward-slash.
                // Normalize at parse time so FindLock(unityAssetPath) matches.
                var canonicalPath = (kv.Key ?? "").Replace('\\', '/');

                var fl = new FileLock(
                    path: canonicalPath,
                    owner: ownerStr,
                    ownerName: ownerStr, // SVN exposes only one identifier; conflate (same as LFS path)
                    machineName: isSelf ? Environment.MachineName : "")
                {
                    lockId = null, // SVN tokens are WC-internal, not stable cross-WC; null per Step D plan
                    lockedAt = info.lockCreated.HasValue
                        ? info.lockCreated.Value.ToString("o")
                        : DateTime.UtcNow.ToString("o"),
                    lockState = info.state.ToString() // K | O | B | T (Phase 2 closure Test 5)
                };
                container.locks.Add(fl);
            }

            return container;
        }
    }

    /// <summary>
    /// Internal envelope used to deserialize the top-level array returned by
    /// `git lfs locks --json` (JsonUtility cannot deserialize a root array directly).
    /// </summary>
    [Serializable]
    internal class LfsLocksEnvelope
    {
        public List<LfsLockEntry> locks;
    }

    [Serializable]
    internal class LfsLockEntry
    {
        public string id;
        public string path;
        public string locked_at;
        public LfsLockOwner owner;
    }

    [Serializable]
    internal class LfsLockOwner
    {
        public string name;
    }

    /// <summary>
    /// Result of a single file operation in a batch
    /// </summary>
    public class FileOperationResult
    {
        public string path;
        public bool success;
        public string errorMessage;
        public FileLock existingLock; // For lock operations that failed due to existing lock

        public FileOperationResult(string path, bool success, string errorMessage = null, FileLock existingLock = null)
        {
            this.path = path;
            this.success = success;
            this.errorMessage = errorMessage;
            this.existingLock = existingLock;
        }
    }

    /// <summary>
    /// Result of a batch lock operation
    /// </summary>
    public class BatchLockResult
    {
        public List<FileOperationResult> results = new List<FileOperationResult>();
        public int successCount => results.Count(r => r.success);
        public int failureCount => results.Count(r => !r.success);
        public bool allSucceeded => failureCount == 0;
        public bool anySucceeded => successCount > 0;
        
        public void AddResult(string path, bool success, string errorMessage = null, FileLock existingLock = null)
        {
            results.Add(new FileOperationResult(path, success, errorMessage, existingLock));
        }

        public List<FileOperationResult> GetSuccessful()
        {
            return results.Where(r => r.success).ToList();
        }

        public List<FileOperationResult> GetFailed()
        {
            return results.Where(r => !r.success).ToList();
        }
    }

    /// <summary>
    /// Result of a batch unlock operation
    /// </summary>
    public class BatchUnlockResult : BatchLockResult
    {
        public int revertedCount;
        public List<string> revertedPaths = new List<string>();
    }

    /// <summary>Outcome of one batched revert-and-unlock (0.2.89). <c>unknownPaths</c> are
    /// paths whose request was abandoned (still running / busy) — the user is told to refresh,
    /// never that they failed.</summary>
    public class RevertUnlockBatchResult
    {
        public List<FileOperationResult> results = new List<FileOperationResult>();
        public List<string> revertedPaths = new List<string>();
        public List<string> releasedPaths = new List<string>();
        public List<string> unknownPaths = new List<string>();
        public string unknownReason;

        public int revertedCount => revertedPaths.Count;
        public int releasedCount => releasedPaths.Count;
        public int failureCount => results.Count(r => !r.success);
        public bool allSucceeded => failureCount == 0 && unknownPaths.Count == 0;

        public void Add(string path, bool success, string message = null)
        {
            results.Add(new FileOperationResult(path, success, message));
        }

        public List<FileOperationResult> GetFailed() => results.Where(r => !r.success).ToList();
    }
}
