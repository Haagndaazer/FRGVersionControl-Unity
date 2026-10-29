using System.IO;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Core
{
    /// <summary>
    /// Resolves the package's on-disk location and scaffolds per-project config on first run.
    ///
    /// When installed as a UPM package, the toolset's defaults live at
    /// `<PackageRoot>/Editor/DefaultConfig~/*.json` (tilde-suffixed = hidden from Unity AssetDB).
    /// On first <see cref="FileLockManager.Initialize"/>, missing config files are copied
    /// into `<ProjectRoot>/Assets/FRGVersionControl/<file>` so the user has editable,
    /// SVN-committable copies in their project.
    /// </summary>
    public static class FileLockPackagePaths
    {
        public static string PackageRoot
        {
            get
            {
                var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FileLockPackagePaths).Assembly);
                if (pkg != null) return pkg.resolvedPath;

                // Transition fallback: still embedded in Assets/, not yet installed as a package.
                return Path.Combine(SvnCommandRunner.ProjectRoot, "Assets/FRGVersionControl");
            }
        }

        public static string ProjectConfigDir =>
            Path.Combine(SvnCommandRunner.ProjectRoot, "Assets/FRGVersionControl");

        public static void EnsureProjectConfig(string filename)
        {
            string dest = Path.Combine(ProjectConfigDir, filename);
            if (File.Exists(dest)) return;

            string src = Path.Combine(PackageRoot, "Editor/DefaultConfig~", filename);
            if (!File.Exists(src))
            {
                Debug.LogError($"[FRGVersionControl] Default config not found at {src}. Cannot scaffold {filename}.");
                return;
            }

            Directory.CreateDirectory(ProjectConfigDir);
            File.Copy(src, dest, overwrite: false);
            // Defer AssetDatabase.Refresh to next editor tick — this may be called from
            // an active asset-import callback (OnWillSaveAssets), where Refresh is restricted.
            EditorApplication.delayCall += () => AssetDatabase.Refresh();
            Debug.Log($"[FRGVersionControl] Scaffolded {dest} from package default.");
        }
    }
}
