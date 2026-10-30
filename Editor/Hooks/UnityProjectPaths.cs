using FRGVersionControl.Core;
using FRGVersionControl.Core.Abstractions;

namespace FRGVersionControl.Hooks
{
    /// <summary>Project paths for the IPC context. <see cref="WorkingCopyRoot"/> is the Unity project
    /// folder sent as ctx.wc — the working-copy root or a versioned folder inside one; the core
    /// finds the real root itself (SVN_CORE_API.md §6.7).</summary>
    public sealed class UnityProjectPaths : IProjectPaths
    {
        public string WorkingCopyRoot => ProjectFolder.Path;
        public string AssetsRoot => ProjectFolder.Path + "/Assets";
        public string LibraryRoot => ProjectFolder.Path + "/Library";
        public string ToolingConfigDir => ProjectFolder.Path + "/.svn-tooling-config";
    }
}
