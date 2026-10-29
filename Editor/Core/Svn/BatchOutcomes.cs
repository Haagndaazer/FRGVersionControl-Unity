using System;
using System.Collections.Generic;
using System.Linq;

namespace FRGVersionControl.Core.Svn
{
    [Serializable]
    public class RevertFailure
    {
        public string path;
        public string error;
    }

    [Serializable]
    public class UnlockFailure
    {
        public string path;
        public string svnCode;
        public string line;
    }

    /// <summary>Per-path outcome of one or more batched <c>mutate.revert</c> requests
    /// (SVN_CORE_API.md §5.4). <c>unknown</c> holds paths whose request was abandoned
    /// (still_running / busy): the core may still have reverted them, so callers must say
    /// "refresh status", never "failed".</summary>
    public sealed class RevertOutcome
    {
        public bool success = true;
        public string error;
        public List<string> reverted = new List<string>();
        public List<RevertFailure> failed = new List<RevertFailure>();
        public List<string> unchanged = new List<string>();
        public List<string> unknown = new List<string>();
        public string unknownReason;

        public bool WasReverted(string path) => reverted.Any(r => BatchOutcomes.PathsMatch(r, path));
        public bool IsUnchanged(string path) => unchanged.Any(r => BatchOutcomes.PathsMatch(r, path));
        public bool IsUnknown(string path) => unknown.Any(r => BatchOutcomes.PathsMatch(r, path));
        public RevertFailure FailureFor(string path) => failed.FirstOrDefault(f => BatchOutcomes.PathsMatch(f.path, path));
    }

    /// <summary>Per-path outcome of batched <c>lock.release</c> requests (§5.3).</summary>
    public sealed class UnlockOutcome
    {
        public bool success = true;
        public string error;
        public List<string> released = new List<string>();
        public List<UnlockFailure> failed = new List<UnlockFailure>();
        public List<string> unknown = new List<string>();
        public string unknownReason;

        public bool WasReleased(string path) => released.Any(r => BatchOutcomes.PathsMatch(r, path));
        public bool IsUnknown(string path) => unknown.Any(r => BatchOutcomes.PathsMatch(r, path));
        public UnlockFailure FailureFor(string path) => failed.FirstOrDefault(f => BatchOutcomes.PathsMatch(f.path, path));
    }

    public static class BatchOutcomes
    {
        public const int ChunkSize = 100;

        [Serializable]
        private sealed class RevertEnvelope
        {
            public RevertBody result;
        }

        [Serializable]
        private sealed class RevertBody
        {
            public string[] reverted;
            public RevertFailure[] failed;
            public string[] unchanged;
        }

        [Serializable]
        private sealed class UnlockEnvelope
        {
            public UnlockBody result;
        }

        [Serializable]
        private sealed class UnlockBody
        {
            public string[] released;
            public UnlockFailure[] failed;
        }

        public static void MergeRevert(RevertOutcome into, string rawLine)
        {
            var env = SafeParse<RevertEnvelope>(rawLine);
            if (env?.result == null) return;
            if (env.result.reverted != null) into.reverted.AddRange(env.result.reverted);
            if (env.result.failed != null) into.failed.AddRange(env.result.failed.Where(f => f != null && !string.IsNullOrEmpty(f.path)));
            if (env.result.unchanged != null) into.unchanged.AddRange(env.result.unchanged);
        }

        public static void MergeUnlock(UnlockOutcome into, string rawLine)
        {
            var env = SafeParse<UnlockEnvelope>(rawLine);
            if (env?.result == null) return;
            if (env.result.released != null) into.released.AddRange(env.result.released);
            if (env.result.failed != null) into.failed.AddRange(env.result.failed.Where(f => f != null && !string.IsNullOrEmpty(f.path)));
        }

        [Serializable]
        private sealed class ErrorEnvelope
        {
            public ErrorBody error;
        }

        [Serializable]
        private sealed class ErrorBody
        {
            public UnlockFailure[] perPath;
        }

        /// <summary>Per-path entries carried on a core error frame (`error.perPath`), or none.</summary>
        public static List<UnlockFailure> ParseErrorPerPath(string rawLine)
        {
            var env = SafeParse<ErrorEnvelope>(rawLine);
            var list = new List<UnlockFailure>();
            if (env?.error?.perPath == null) return list;
            list.AddRange(env.error.perPath.Where(f => f != null && !string.IsNullOrEmpty(f.path)));
            return list;
        }

        public static RevertOutcome ParseRevert(string rawLine)
        {
            var o = new RevertOutcome();
            MergeRevert(o, rawLine);
            return o;
        }

        public static UnlockOutcome ParseUnlock(string rawLine)
        {
            var o = new UnlockOutcome();
            MergeUnlock(o, rawLine);
            return o;
        }

        public static IEnumerable<string[]> Chunk(IList<string> items, int size)
        {
            for (int i = 0; i < items.Count; i += size)
            {
                yield return items.Skip(i).Take(size).ToArray();
            }
        }

        /// <summary>The core reports paths in the caller's spelling, so a match is exact after
        /// separator/case normalisation; only an absolute (rooted) reported path may match a
        /// requested path as a path-boundary suffix, so "a.txt" never claims "sub/a.txt".</summary>
        public static bool PathsMatch(string reported, string requested)
        {
            var a = Normalize(reported);
            var b = Normalize(requested);
            if (a.Length == 0 || b.Length == 0) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            return IsRooted(a) && IsBoundarySuffix(a, b);
        }

        private static bool IsRooted(string p) =>
            p.StartsWith("/") || (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':');

        private static bool IsBoundarySuffix(string haystack, string needle)
        {
            if (haystack.Length <= needle.Length) return false;
            if (!haystack.EndsWith(needle, StringComparison.OrdinalIgnoreCase)) return false;
            return haystack[haystack.Length - needle.Length - 1] == '/';
        }

        private static string Normalize(string p)
        {
            var s = (p ?? "").Replace('\\', '/').Trim();
            while (s.Length > 1 && s.EndsWith("/")) s = s.Substring(0, s.Length - 1);
            if (s.StartsWith("./") && s.Length > 2) s = s.Substring(2);
            return s;
        }

        private static T SafeParse<T>(string rawLine) where T : class
        {
            if (string.IsNullOrEmpty(rawLine)) return null;
            try { return UnityEngine.JsonUtility.FromJson<T>(rawLine); }
            catch { return null; }
        }
    }
}
