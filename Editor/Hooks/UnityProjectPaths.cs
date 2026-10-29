using System.IO;
using FRGVersionControl.Core.Abstractions;
using UnityEngine;

namespace FRGVersionControl.Hooks
{
    public sealed class UnityProjectPaths : IProjectPaths
    {
        public string WorkingCopyRoot
        {
            get
            {
                var dp = Application.dataPath.Replace('\\', '/');
                if (dp.EndsWith("/Assets")) return dp.Substring(0, dp.Length - "/Assets".Length);
                return dp;
            }
        }

        public string AssetsRoot => Application.dataPath.Replace('\\', '/');
        public string LibraryRoot => Path.Combine(WorkingCopyRoot, "Library").Replace('\\', '/');
        public string ToolingConfigDir => Path.Combine(WorkingCopyRoot, ".svn-tooling-config").Replace('\\', '/');
    }
}
