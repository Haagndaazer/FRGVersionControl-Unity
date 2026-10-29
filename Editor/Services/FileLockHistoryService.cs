using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using FRGVersionControl.Core;

namespace FRGVersionControl.Services
{
    /// <summary>
    /// Tracks history of lock operations for auditing
    /// </summary>
    [Serializable]
    public class LockHistoryEntry
    {
        public string path;
        public string action; // "lock" or "unlock"
        public string user;
        public string userName;
        public string machineName;
        public string timestamp;
        public bool wasForced;

        public LockHistoryEntry(string path, string action, string user, string userName, string machineName, bool forced = false)
        {
            this.path = path;
            this.action = action;
            this.user = user;
            this.userName = userName;
            this.machineName = machineName;
            this.timestamp = DateTime.UtcNow.ToString("O");
            this.wasForced = forced;
        }

        // Constructor for Git-parsed entries with custom timestamp
        public LockHistoryEntry(string path, string action, string user, string userName, string machineName, string timestamp, bool forced = false)
        {
            this.path = path;
            this.action = action;
            this.user = user;
            this.userName = userName;
            this.machineName = machineName;
            this.timestamp = timestamp;
            this.wasForced = forced;
        }

        public DateTime GetTimestamp()
        {
            return DateTime.Parse(timestamp).ToUniversalTime();
        }
    }

    [Serializable]
    public class LockHistory
    {
        public List<LockHistoryEntry> entries = new List<LockHistoryEntry>();
    }

    public static class FileLockHistoryService
    {
        // 0.2.48 — Application.persistentDataPath must be read from Unity's main thread.
        // The previous design lazy-read it inside HistoryFilePath's getter which made the
        // first background-thread access (e.g., from a Task.Run continuation) throw and
        // permanently poison the type initializer state. We now cache the path during
        // editor load via [InitializeOnLoadMethod] (guaranteed main thread) and only fall
        // back to a lazy main-thread read if cache priming hasn't happened yet.
        private static string s_CachedHistoryFilePath;

        [UnityEditor.InitializeOnLoadMethod]
        private static void PrimePersistentPath()
        {
            try
            {
                s_CachedHistoryFilePath = Path.Combine(Application.persistentDataPath, "FileLockHistory.json");
                // Now that the path is known, load the on-disk history. Main thread guaranteed.
                LoadHistory();
            }
            catch { /* defensive — RuntimeInitializeOnLoad timing edge case */ }
        }

        private static string HistoryFilePath
        {
            get
            {
                // Reading the field is thread-safe (string ref assignment is atomic on .NET).
                // If priming hasn't completed (unlikely but defensive), we don't risk a
                // background-thread Unity API call here — return a sentinel path that
                // LoadHistory/SaveHistory treat as "history disabled until next main-thread tick".
                var cached = s_CachedHistoryFilePath;
                return cached ?? "";
            }
        }

        private static LockHistory s_History;
        private static List<LockHistoryEntry> s_CachedMergedHistory = null;

        static FileLockHistoryService()
        {
            // Defer LoadHistory; the [InitializeOnLoadMethod] primes the cache path,
            // then the first GetHistory/RefreshMergedHistory call will pick it up.
            s_History = new LockHistory();
        }

        public static void RecordLock(string path, string user, string userName, string machineName)
        {
            var entry = new LockHistoryEntry(path, "lock", user, userName, machineName);
            s_History.entries.Add(entry);
            SaveHistory();
            // 0.2.58 — also push to the WC-relative core-owned store. The Unity-local
            // persistentDataPath store stays in place as a per-engine cache during transition;
            // it'll be deleted once every caller reads exclusively via hist.list.
            TryIpcRecord("lock", path, user, userName, machineName, false);
        }

        public static void RecordUnlock(string path, string user, string userName, string machineName, bool forced = false)
        {
            var entry = new LockHistoryEntry(path, "unlock", user, userName, machineName, forced);
            s_History.entries.Add(entry);
            SaveHistory();
            TryIpcRecord("unlock", path, user, userName, machineName, forced);
        }

        /// <summary>Local-store half of a batched unlock record (main thread: the list is read
        /// by the UI). One file write for the whole batch.</summary>
        public static void RecordUnlocksLocal(IList<string> paths, string user, string userName, string machineName, bool forced = false)
        {
            if (paths == null || paths.Count == 0) return;
            foreach (var p in paths)
            {
                s_History.entries.Add(new LockHistoryEntry(p, "unlock", user, userName, machineName, forced));
            }
            SaveHistory();
        }

        /// <summary>Core-store half of a batched unlock record: one <c>hist.record</c> request
        /// with <c>entries[]</c> (SVN_CORE_API.md §5.10). Safe off the main thread.</summary>
        public static async Task RecordUnlocksIpcAsync(IList<string> paths, string user, string userName, string machineName, bool forced = false)
        {
            if (paths == null || paths.Count == 0) return;
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return;
                var sb = new System.Text.StringBuilder("{\"entries\":[");
                for (int i = 0; i < paths.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"event\":\"unlock\",\"path\":\"").Append(IpcEscape(paths[i]))
                      .Append("\",\"user\":\"").Append(IpcEscape(user ?? ""))
                      .Append("\",\"userName\":\"").Append(IpcEscape(userName ?? ""))
                      .Append("\",\"machineName\":\"").Append(IpcEscape(machineName ?? ""))
                      .Append("\",\"forced\":").Append(forced ? "true" : "false").Append("}");
                }
                sb.Append("]}");
                await client.InvokeRawAsync("hist.record", sb.ToString(), client.BuildDefaultContext()).ConfigureAwait(false);
            }
            catch { }
        }

        private static void TryIpcRecord(string ev, string path, string user, string userName, string machineName, bool forced)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return;
                var ctx = client.BuildDefaultContext();
                var args = "{" +
                    "\"event\":\"" + ev + "\"," +
                    "\"path\":\"" + IpcEscape(path) + "\"," +
                    "\"user\":\"" + IpcEscape(user ?? "") + "\"," +
                    "\"userName\":\"" + IpcEscape(userName ?? "") + "\"," +
                    "\"machineName\":\"" + IpcEscape(machineName ?? "") + "\"," +
                    "\"forced\":" + (forced ? "true" : "false") +
                    "}";
                // Fire-and-forget — history record failure should never block the lock op.
                _ = client.InvokeRawAsync("hist.record", args, ctx);
            }
            catch { /* defensive — history is best-effort */ }
        }

        private static string IpcEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static List<LockHistoryEntry> GetHistory(int maxEntries = 100)
        {
            // Return most recent entries
            int startIndex = Math.Max(0, s_History.entries.Count - maxEntries);
            return s_History.entries.GetRange(startIndex, s_History.entries.Count - startIndex);
        }

        public static List<LockHistoryEntry> GetHistoryForFile(string path)
        {
            return s_History.entries.FindAll(e => e.path == path);
        }

        /// <summary>
        /// Phase 2 LFS rewrite: the file-locks-branch git-history layer is retired.
        /// History is now per-clone local-only via Application.persistentDataPath.
        /// Cross-clone history visibility is lost as a known trade-off (see PHASE2_FINDINGS.md).
        ///
        /// Method retained as no-op for back-compat with any callers that still invoke it.
        /// </summary>
        public static List<LockHistoryEntry> GetGitHistory(int maxCommits = 500)
        {
            return new List<LockHistoryEntry>();
        }

        /// <summary>
        /// Get merged history from local storage + Git commits
        /// </summary>
        public static List<LockHistoryEntry> GetMergedHistory(int maxEntries = 500)
        {
            // Get local history
            var localHistory = GetHistory(maxEntries);

            // Get Git history
            var gitHistory = GetGitHistory(maxEntries);

            // Combine both lists
            var merged = new List<LockHistoryEntry>();
            merged.AddRange(localHistory);
            merged.AddRange(gitHistory);

            // Sort by timestamp (newest first)
            merged.Sort((a, b) => b.GetTimestamp().CompareTo(a.GetTimestamp()));

            // Deduplicate: Remove entries that are very close in time (within 5 seconds)
            // This handles cases where local and Git history have the same entry
            var deduplicated = new List<LockHistoryEntry>();
            for (int i = 0; i < merged.Count; i++)
            {
                bool isDuplicate = false;
                var current = merged[i];

                // Check if there's a nearly identical entry already added
                for (int j = 0; j < deduplicated.Count; j++)
                {
                    var existing = deduplicated[j];
                    var timeDiff = Math.Abs((current.GetTimestamp() - existing.GetTimestamp()).TotalSeconds);

                    // Same action, path, user within 5 seconds = duplicate
                    if (current.action == existing.action &&
                        current.path == existing.path &&
                        current.user == existing.user &&
                        timeDiff < 5)
                    {
                        isDuplicate = true;
                        // Keep local entry over Git entry (has machine name)
                        if (current.machineName != "unknown" && existing.machineName == "unknown")
                        {
                            deduplicated[j] = current; // Replace with better entry
                        }
                        break;
                    }
                }

                if (!isDuplicate)
                {
                    deduplicated.Add(current);
                }
            }

            // Return most recent entries
            int startIndex = Math.Max(0, deduplicated.Count - maxEntries);
            return deduplicated.GetRange(startIndex, deduplicated.Count - startIndex);
        }

        /// <summary>
        /// Refresh the merged history cache (call this when locks are refreshed)
        /// </summary>
        public static void RefreshMergedHistory()
        {
            s_CachedMergedHistory = GetMergedHistory(500);
        }

        /// <summary>
        /// Get cached merged history (refreshes automatically if cache is null)
        /// </summary>
        public static List<LockHistoryEntry> GetCachedMergedHistory()
        {
            if (s_CachedMergedHistory == null)
            {
                RefreshMergedHistory();
            }
            return s_CachedMergedHistory;
        }

        private static void LoadHistory()
        {
            var path = HistoryFilePath;
            if (string.IsNullOrEmpty(path))
            {
                // Cache not primed yet (editor still loading) — keep an empty in-memory store
                // until a subsequent call after [InitializeOnLoadMethod] has run.
                s_History = new LockHistory();
                return;
            }
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    s_History = JsonUtility.FromJson<LockHistory>(json);
                }
                catch
                {
                    s_History = new LockHistory();
                }
            }
            else
            {
                s_History = new LockHistory();
            }
        }

        private static void SaveHistory()
        {
            try
            {
                string json = JsonUtility.ToJson(s_History, true);
                File.WriteAllText(HistoryFilePath, json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[FRGVersionControl] Failed to save history: {e.Message}");
            }
        }

        public static void ClearHistory()
        {
            s_History = new LockHistory();
            SaveHistory();
            RefreshMergedHistory(); // Refresh cache after clearing
        }
    }
}
