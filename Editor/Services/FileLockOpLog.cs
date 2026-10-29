using System;
using System.IO;
using System.Threading;
using FRGVersionControl.Core.Svn;
using UnityEngine;

namespace FRGVersionControl.Services
{
    /// <summary>
    /// Phase 4 Step 6: lightweight telemetry op-log. Every SVN-equivalent operation initiated
    /// through the toolset writes a single-line entry. Used to measure the success metric:
    /// ≥90% of artist-class operations through Unity in a typical week.
    ///
    /// Sink: <c>&lt;ProjectRoot&gt;/.svn-tooling-config/op-log.txt</c>. NOT <c>Library/</c> — Library
    /// is rebuilt by Unity on platform switches / asset DB resets, which would lose telemetry.
    /// The .svn-tooling-config dir is per-WC, already svn:ignored at repo root, and outlives
    /// Library rebuilds.
    ///
    /// Format (one line per op): <c>&lt;ISO-timestamp&gt; &lt;user&gt; &lt;op&gt; &lt;path-or-empty&gt; &lt;wall-ms&gt; &lt;success|fail&gt;</c>
    ///
    /// Thread-safe via lock(s_Sync); writes are append-only.
    /// </summary>
    public static class FileLockOpLog
    {
        private static readonly object s_Sync = new object();
        private static string s_PathCache;

        private static string LogPath
        {
            get
            {
                if (s_PathCache == null)
                {
                    string dir = SvnCommandRunner.ToolingConfigDir;
                    s_PathCache = Path.Combine(dir, "op-log.txt");
                }
                return s_PathCache;
            }
        }

        /// <summary>Record one operation. Safe to call from any thread.</summary>
        public static void Record(string op, string path, long wallMs, bool success, string user = null)
        {
            try
            {
                string line = $"{DateTime.UtcNow:O} " +
                              $"{user ?? Environment.UserName} " +
                              $"{op} " +
                              $"{path ?? ""} " +
                              $"{wallMs} " +
                              $"{(success ? "success" : "fail")}";
                lock (s_Sync)
                {
                    string dir = Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                // Telemetry failure should never break the user's op
                Debug.LogWarning($"[FRGVersionControl.OpLog] Failed to record '{op}': {ex.Message}");
            }
        }

        /// <summary>Convenience wrapper that times an action and records the op-log entry.</summary>
        public static T TimeAndRecord<T>(string op, string path, Func<T> action,
                                          Func<T, bool> successPredicate = null,
                                          string user = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            T result = default;
            bool success = false;
            try
            {
                result = action();
                success = successPredicate != null ? successPredicate(result) : true;
                return result;
            }
            catch
            {
                success = false;
                throw;
            }
            finally
            {
                sw.Stop();
                Record(op, path, sw.ElapsedMilliseconds, success, user);
            }
        }
    }
}
