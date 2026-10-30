using System;
using System.IO;
using UnityEngine;

namespace FRGVersionControl.Core.Svn
{
    /// <summary>
    /// Reads SVN's on-disk auth cache to derive the current SVN username for a repo,
    /// without requiring the toolset to store identity strings in any committed file.
    ///
    /// Phase 3 (replaces Phase 2 Step D's `currentSvnUsername` storage in admin-config.json).
    ///
    /// SVN's cache format:
    ///   - Files at &lt;config-dir&gt;/auth/svn.simple/&lt;hash&gt; in K/V record format
    ///   - Records: K &lt;len&gt;\n&lt;key&gt;\nV &lt;len&gt;\n&lt;value&gt;\n... ending with END
    ///   - `username`, `passtype`, and `svn:realmstring` are plain text
    ///   - `password` may be wincrypt-encrypted on Windows (DPAPI) — not needed here
    ///
    /// Realm strings are server-scoped, NOT repo-scoped. Format for HTTP[S] basic:
    ///   &lt;scheme://host:port&gt; &lt;auth-realm-name&gt;
    /// e.g., `&lt;https://localhost:443&gt; VisualSVN Server` matches every repo on that
    /// VisualSVN host. We match by host:port substring, not full realm equality.
    ///
    /// Uses <see cref="SvnCommandRunner.ToolingConfigDir"/> so each Unity instance reads
    /// from its own per-WC auth cache (isolated from other Unity instances on the same
    /// machine — Phase 3 finding: SVN's global auth cache is overwritten by the latest
    /// authenticated user, not appended).
    /// </summary>
    public static class SvnAuthCacheReader
    {
        /// <summary>
        /// Returns the cached SVN username for the current repo, or null on cache miss.
        /// Caller (FileLockManager / SetupWizard) handles the bootstrap when null.
        /// </summary>
        public static string GetUsernameForCurrentRepo()
        {
            var info = SvnCommandRunner.Info(".");
            if (info == null || string.IsNullOrEmpty(info.repoUrl))
            {
                return null;
            }
            return GetUsernameForRepoUrl(info.repoUrl);
        }

        /// <summary>
        /// Lower-level lookup. Reads &lt;tooling-config-dir&gt;/auth/svn.simple/* and returns
        /// the username from the entry whose svn:realmstring contains the URL's host:port.
        /// On multiple matches, returns the most-recently-modified entry's username.
        /// </summary>
        public static string GetUsernameForRepoUrl(string repoUrl)
        {
            string hostPort = ExtractHostPort(repoUrl);
            if (string.IsNullOrEmpty(hostPort)) return null;

            string cacheDir = Path.Combine(SvnCommandRunner.ToolingConfigDir, "auth", "svn.simple");
            if (!Directory.Exists(cacheDir)) return null;

            string bestUsername = null;
            DateTime bestMtime = DateTime.MinValue;
            int matchCount = 0;

            foreach (var file in Directory.GetFiles(cacheDir))
            {
                try
                {
                    var entry = ParseEntry(file);
                    if (entry == null) continue;
                    string realm = entry.Get("svn:realmstring") ?? "";
                    if (realm.IndexOf(hostPort, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    string username = entry.Get("username");
                    if (string.IsNullOrEmpty(username)) continue;

                    matchCount++;
                    var mtime = File.GetLastWriteTimeUtc(file);
                    if (mtime > bestMtime)
                    {
                        bestMtime = mtime;
                        bestUsername = username;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SvnAuthCacheReader] Failed to parse {file}: {ex.Message}");
                }
            }

            if (matchCount > 1)
            {
                Debug.LogWarning($"[SvnAuthCacheReader] Multiple realm matches for host:port " +
                                 $"'{hostPort}'. Picked most-recently-written ({bestUsername}). " +
                                 "If wrong, re-run the bootstrap `svn info --config-dir " +
                                 $"\"{SvnCommandRunner.ToolingConfigDir}\" --username <you> {repoUrl}`.");
            }

            return bestUsername;
        }

        // -------------------- helpers --------------------

        /// <summary>Extracts `<scheme://host:port>` literal substring from a repo URL.
        /// SVN's realmstring includes this exact form so substring match is sufficient.</summary>
        public static string ExtractHostPort(string repoUrl)
        {
            try
            {
                var uri = new Uri(repoUrl);
                int port = uri.Port;
                if (port < 0)
                {
                    port = DefaultPort(uri.Scheme);
                    if (port < 0) port = 80;
                }
                return $"<{uri.Scheme}://{uri.Host}:{port}>";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Default port of a scheme whose credentials svn keeps in svn.simple; -1 for
        /// schemes that never use it (file://, svn+ssh://).</summary>
        public static int DefaultPort(string scheme)
        {
            switch ((scheme ?? "").ToLowerInvariant())
            {
                case "https": return 443;
                case "http": return 80;
                case "svn": return 3690;
                default: return -1;
            }
        }

        /// <summary>True when the repository URL's scheme stores passwords in svn.simple.</summary>
        public static bool UsesPasswordCache(string repoUrl)
        {
            try { return DefaultPort(new Uri(repoUrl).Scheme) > 0; }
            catch { return false; }
        }

        private class CacheEntry
        {
            private readonly System.Collections.Generic.Dictionary<string, string> m_Map =
                new System.Collections.Generic.Dictionary<string, string>();

            public void Set(string k, string v) => m_Map[k] = v;
            public string Get(string k) => m_Map.TryGetValue(k, out var v) ? v : null;
        }

        /// <summary>
        /// Parses a single svn.simple cache file. Format:
        ///   K &lt;len&gt;\n&lt;key&gt;\nV &lt;len&gt;\n&lt;value&gt;\n... \nEND\n
        /// where len is the byte length of the next line. Reads bytes (not chars) per
        /// SVN's spec; handles values with embedded newlines correctly.
        /// </summary>
        private static CacheEntry ParseEntry(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var entry = new CacheEntry();
            int i = 0;

            while (i < bytes.Length)
            {
                if (bytes[i] == (byte)'E' && i + 3 <= bytes.Length &&
                    bytes[i + 1] == (byte)'N' && bytes[i + 2] == (byte)'D')
                {
                    break;
                }

                if (bytes[i] != (byte)'K') return null;
                int kLen = ReadLength(bytes, ref i);
                if (kLen < 0) return null;
                string key = System.Text.Encoding.UTF8.GetString(bytes, i, kLen);
                i += kLen;
                if (i < bytes.Length && bytes[i] == (byte)'\n') i++;

                if (i >= bytes.Length || bytes[i] != (byte)'V') return null;
                int vLen = ReadLength(bytes, ref i);
                if (vLen < 0) return null;
                string val = System.Text.Encoding.UTF8.GetString(bytes, i, vLen);
                i += vLen;
                if (i < bytes.Length && bytes[i] == (byte)'\n') i++;

                entry.Set(key, val);
            }

            return entry;
        }

        /// <summary>Reads `K &lt;n&gt;\n` or `V &lt;n&gt;\n` and advances index past the newline.</summary>
        private static int ReadLength(byte[] bytes, ref int i)
        {
            // Skip the K or V byte
            i++;
            if (i >= bytes.Length || bytes[i] != (byte)' ') return -1;
            i++;
            int start = i;
            while (i < bytes.Length && bytes[i] != (byte)'\n') i++;
            if (i >= bytes.Length) return -1;
            string lenStr = System.Text.Encoding.ASCII.GetString(bytes, start, i - start);
            i++; // past newline
            return int.TryParse(lenStr, out var n) ? n : -1;
        }
    }
}
