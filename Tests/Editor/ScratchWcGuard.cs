using System;
using System.IO;
using System.Text.RegularExpressions;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// Shared gate for tests that mutate the working copy. They run ONLY inside a scratch
    /// working copy prepared by Test/LiveProbes/Setup-SlowCommitScratch.ps1: env var
    /// FRG_SLOW_COMMIT_WC equal to the project root, a .frg-scratch-wc marker in the root, and
    /// a file:/// repository under the OS temp directory. Anywhere else they are ignored, so
    /// a consumer project that lists the package as testable can never be touched.
    /// </summary>
    internal static class ScratchWcGuard
    {
        public static string Norm(string p) => (p ?? "").Replace('\\', '/').TrimEnd('/');

        public static bool IsScratch(out string reason)
        {
            var root = Norm(SvnCommandRunner.ProjectRoot);
            var env = Norm(Environment.GetEnvironmentVariable("FRG_SLOW_COMMIT_WC"));
            if (string.IsNullOrEmpty(env) || !string.Equals(env, root, StringComparison.OrdinalIgnoreCase))
            {
                reason = "FRG_SLOW_COMMIT_WC is not this project root";
                return false;
            }
            if (!File.Exists(Path.Combine(root, ".frg-scratch-wc")))
            {
                reason = ".frg-scratch-wc marker missing";
                return false;
            }
            var client = FileLockManager.Instance.IpcClient;
            if (!client.IsAvailable) { reason = "svn-core.exe unavailable"; return false; }
            var info = client.InvokeRawAsync("meta.info", "{\"path\":\".\"}", client.BuildDefaultContext()).GetAwaiter().GetResult();
            var m = info.Ok ? Regex.Match(info.RawLine ?? "", "\"url\":\"([^\"]+)\"") : null;
            var url = m != null && m.Success ? m.Groups[1].Value : "";
            var temp = Norm(Path.GetTempPath());
            if (!url.StartsWith("file:///" + temp, StringComparison.OrdinalIgnoreCase))
            {
                reason = "working copy is not a file:/// repository under the temp directory: " + url;
                return false;
            }
            reason = null;
            return true;
        }

        public static long HeadRevision()
        {
            var client = FileLockManager.Instance.IpcClient;
            var local = client.InvokeRawAsync("meta.info", "{\"path\":\".\"}", client.BuildDefaultContext()).GetAwaiter().GetResult();
            var urlMatch = Regex.Match(local.RawLine ?? "", "\"url\":\"([^\"]+)\"");
            if (!urlMatch.Success) return -1;
            var head = client.InvokeRawAsync("meta.info", "{\"url\":\"" + urlMatch.Groups[1].Value + "\"}", client.BuildDefaultContext()).GetAwaiter().GetResult();
            var m = Regex.Match(head.RawLine ?? "", "\"revision\":(\\d+)");
            return m.Success ? long.Parse(m.Groups[1].Value) : -1;
        }
    }
}
