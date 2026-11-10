using System;
using System.Collections.Generic;
using System.IO;

namespace FRGVersionControl.Core
{
    /// <summary>
    /// Finds an installed external diff/merge tool: TortoiseMerge from the TortoiseSVN install
    /// folder recorded in the registry, then the standard Program Files locations (from the
    /// environment, so a different drive or folder still works) for TortoiseMerge, Beyond Compare
    /// 5/4 and KDiff3. Only existing files are returned, in that order.
    /// </summary>
    public static class DiffToolLocator
    {
        public static List<string> Candidates()
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string path)
            {
                if (string.IsNullOrEmpty(path)) return;
                try
                {
                    if (File.Exists(path) && seen.Add(Path.GetFullPath(path))) found.Add(path);
                }
                catch { }
            }

            var tortoise = TortoiseDirectory();
            if (!string.IsNullOrEmpty(tortoise)) Add(Path.Combine(tortoise, "bin", "TortoiseMerge.exe"));
            foreach (var root in ProgramFilesRoots())
            {
                Add(Path.Combine(root, "TortoiseSVN", "bin", "TortoiseMerge.exe"));
            }
            foreach (var root in ProgramFilesRoots())
            {
                Add(Path.Combine(root, "Beyond Compare 5", "BCompare.exe"));
                Add(Path.Combine(root, "Beyond Compare 4", "BCompare.exe"));
                Add(Path.Combine(root, "KDiff3", "kdiff3.exe"));
            }
            return found;
        }

        public const string NoToolSentence =
            "No compare tool found. Install TortoiseSVN (TortoiseMerge), Beyond Compare or KDiff3.";

        /// <summary>Arguments that open <paramref name="leftPath"/> against <paramref name="rightPath"/>
        /// in <paramref name="exe"/>. Only TortoiseMerge takes the labels.</summary>
        public static string BuildArguments(string exe, string leftPath, string rightPath, string leftLabel, string rightLabel)
            => IsTortoiseMerge(exe)
                ? $"/base:\"{leftPath}\" /mine:\"{rightPath}\" /basename:\"{leftLabel}\" /minename:\"{rightLabel}\""
                : $"\"{leftPath}\" \"{rightPath}\"";

        /// <summary>Opens the two files in the first installed compare tool that starts. False
        /// when none is installed or none could be started.</summary>
        public static bool Launch(string leftPath, string rightPath, string leftLabel, string rightLabel)
        {
            foreach (var exe in Candidates())
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = BuildArguments(exe, leftPath, rightPath, leftLabel, rightLabel),
                        UseShellExecute = true,
                    });
                    return true;
                }
                catch { }
            }
            return false;
        }

        public static bool IsTortoiseMerge(string exe)
            => string.Equals(Path.GetFileName(exe ?? ""), "TortoiseMerge.exe", StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> ProgramFilesRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = null;
                try { root = Environment.GetFolderPath(folder); } catch { }
                if (!string.IsNullOrEmpty(root) && seen.Add(root)) yield return root;
            }
            foreach (var root in new[] { @"C:\Program Files", @"C:\Program Files (x86)" })
            {
                if (seen.Add(root)) yield return root;
            }
        }

        private static string TortoiseDirectory()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\TortoiseSVN"))
                {
                    return key?.GetValue("Directory") as string;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
