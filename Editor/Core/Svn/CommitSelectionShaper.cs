using System;
using System.Collections.Generic;

namespace FRGVersionControl.Core.Svn
{
    /// <summary>
    /// Unity-specific commit selection shaping. The core auto-includes an uncommitted parent
    /// folder when a file under it is committed (SVN_CORE_API.md §5.4); Unity's folder identity
    /// lives in the sibling <c>Folder.meta</c>, so that row travels with the folder whenever the
    /// status snapshot shows it as a pending add. A Modified or unselected meta is never pulled
    /// in — only an Added one, which has no committed history to lose.
    /// </summary>
    public static class CommitSelectionShaper
    {
        public static string[] ExpandFolderMetas(IEnumerable<string> selectedPaths, Func<string, SvnFileStatus?> statusOf)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in selectedPaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (seen.Add(Normalize(p))) result.Add(p);
            }
            foreach (var p in new List<string>(result))
            {
                var path = Normalize(p);
                var dirs = new List<string>();
                int idx = path.LastIndexOf('/');
                while (idx > 0)
                {
                    path = path.Substring(0, idx);
                    dirs.Add(path);
                    idx = path.LastIndexOf('/');
                }
                dirs.Reverse();
                foreach (var dir in dirs)
                {
                    var meta = dir + ".meta";
                    if (!seen.Contains(meta) && statusOf(meta) == SvnFileStatus.Added)
                    {
                        seen.Add(meta);
                        result.Add(meta);
                    }
                }
            }
            return result.ToArray();
        }

        private static string Normalize(string p)
        {
            var s = p.Replace('\\', '/');
            while (s.Length > 1 && s.EndsWith("/")) s = s.Substring(0, s.Length - 1);
            return s;
        }
    }
}
