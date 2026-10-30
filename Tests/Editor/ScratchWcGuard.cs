using System;
using System.IO;
using NUnit.Framework;
using FRGVersionControl.Core;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// Shared gate for tests that mutate the working copy. They run ONLY inside a scratch
    /// working copy prepared by Test/LiveProbes/Setup-SlowCommitScratch.ps1: env var
    /// FRG_SLOW_COMMIT_WC equal to the project root, a .frg-scratch-wc marker in the root, and
    /// a file:/// repository under the OS temp directory. Anywhere else they are ignored, so
    /// a consumer project that lists the package as testable can never be touched. A run that
    /// opted in (FRG_SLOW_COMMIT_WC names this project) but fails a later check FAILS instead of
    /// skipping, so a green run always means the tests really ran.
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
                Assert.Fail("FRG_SLOW_COMMIT_WC names this project but the .frg-scratch-wc marker is missing.");
            }
            var client = FileLockManager.Instance.IpcClient;
            if (!client.IsAvailable) Assert.Fail("FRG_SLOW_COMMIT_WC names this project but svn-core.exe is unavailable.");
            var info = client.InvokeRawAsync("meta.info", "{\"path\":\".\"}", client.BuildDefaultContext()).GetAwaiter().GetResult();
            var url = info.Ok ? IpcJson.ReadScalar(info.RawLine, "url", inResult: true) : null;
            if (!IsFileUrlUnderTemp(url))
            {
                Assert.Fail("FRG_SLOW_COMMIT_WC names this project but its working copy is not a file:/// repository under the temp directory: " + url);
            }
            reason = null;
            return true;
        }

        /// <summary>Compares the decoded local path of a file:// URL (spaces and other characters
        /// arrive percent-encoded) with the full temp path.</summary>
        internal static bool IsFileUrlUnderTemp(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
            var local = Norm(Path.GetFullPath(uri.LocalPath));
            var temp = Norm(Path.GetFullPath(Path.GetTempPath()));
            return local.StartsWith(temp + "/", StringComparison.OrdinalIgnoreCase);
        }

        public static long HeadRevision()
        {
            var client = FileLockManager.Instance.IpcClient;
            var local = client.InvokeRawAsync("meta.info", "{\"path\":\".\"}", client.BuildDefaultContext()).GetAwaiter().GetResult();
            var url = IpcJson.ReadScalar(local.RawLine, "url", inResult: true);
            if (string.IsNullOrEmpty(url)) return -1;
            var head = client.InvokeRawAsync("meta.info", "{\"url\":\"" + SvnCommandRunner.IpcEscape(url) + "\"}", client.BuildDefaultContext()).GetAwaiter().GetResult();
            var rev = IpcJson.ReadScalar(head.RawLine, "revision", inResult: true);
            return long.TryParse(rev, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : -1;
        }
    }
}
