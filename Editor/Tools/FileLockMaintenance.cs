using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tools
{
    /// <summary>
    /// On-demand and reporting helpers for SVN working-copy disk maintenance.
    ///
    /// Phase 2 Step H — replaces the LFS prune wrapper. The expensive disk pile-up on SVN
    /// is the `.svn/pristine/` directory: per Phase 1 finding c33dd1f9c14e, pristine grows
    /// to roughly 3x WC size after only a few revisions and drops back to ~1x after
    /// `svn cleanup --vacuum-pristines`. Vacuum-pristines is NOT optional for long-lived
    /// working copies; bake it into the team's scheduled maintenance.
    ///
    /// 0.2.40 — vacuum-pristines now routes through the svn-core IPC binary
    /// (`maint.vacuumPristines` op) instead of the legacy SvnCommandRunner static. If the
    /// IPC binary is unavailable (e.g., a developer pulled fresh without running
    /// Build-SvnCore.ps1), this falls back to the legacy path so the toolset stays
    /// functional. The fallback is transitional — once Task #13's call-site rewire is
    /// complete and the legacy SvnCommandRunner is deleted, the fallback goes too.
    /// </summary>
    public static class FileLockMaintenance
    {
        public static async void RunMaintenanceNow()
        {
            string projectRoot = SvnCommandRunner.ProjectRoot;
            string wcRoot = WorkingCopyLayout.WcRoot;
            long beforeBytes = await Task.Run(() => MeasurePristineBytes(wcRoot)).ConfigureAwait(true);
            bool nested = !string.Equals(wcRoot, projectRoot.Replace('\\', '/'), System.StringComparison.OrdinalIgnoreCase);

            bool confirmed = EditorUtility.DisplayDialog(
                "Run SVN Maintenance?",
                "This will run `svn cleanup --vacuum-pristines` on the current working copy.\n\n" +
                (nested ? $"The pristine store belongs to the whole checkout at {wcRoot}.\n\n" : "") +
                $"Current `.svn/pristine/` size: {FormatBytes(beforeBytes)}\n\n" +
                "Do not run this while TortoiseSVN or another editor is running an SVN operation on this checkout: " +
                "cleanup takes over working-copy locks.\n\n" +
                "Vacuum-pristines drops unreferenced base-revision copies. Phase 1 measured " +
                "~3x working-copy size before vacuum, ~1x after. Safe to run anytime; " +
                "active ignore-on-commit entries and uncommitted modifications are protected because their " +
                "BASE entries are still referenced.\n\n" +
                "May take a moment on large working copies.",
                "Run Maintenance", "Cancel");

            if (!confirmed) return;

            EditorUtility.DisplayProgressBar("SVN Maintenance",
                "Running svn cleanup --vacuum-pristines...", 0.5f);
            try
            {
                bool success = await RunVacuumPristines(projectRoot).ConfigureAwait(true);
                long afterBytes = await Task.Run(() => MeasurePristineBytes(wcRoot)).ConfigureAwait(true);
                long freedBytes = beforeBytes - afterBytes;

                Debug.Log($"[FRGVersionControl.Maintenance] vacuum-pristines complete. " +
                          $"Pristine: {FormatBytes(beforeBytes)} -> {FormatBytes(afterBytes)} " +
                          $"(freed {FormatBytes(freedBytes)})");

                EditorUtility.DisplayDialog(
                    "Maintenance complete",
                    $"svn cleanup --vacuum-pristines finished.\n\n" +
                    $"Before: {FormatBytes(beforeBytes)}\n" +
                    $"After:  {FormatBytes(afterBytes)}\n" +
                    $"Freed:  {FormatBytes(freedBytes)}",
                    "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Routes vacuum-pristines through svn-core IPC. Falls back to the legacy
        /// SvnCommandRunner path if the IPC binary is unavailable (developer pulled fresh
        /// without running Build-SvnCore.ps1, or a corrupt UPM cache).
        /// </summary>
        private static async Task<bool> RunVacuumPristines(string projectRoot)
        {
            var client = FileLockManager.Instance.IpcClient;
            if (client.IsAvailable)
            {
                try
                {
                    var ctx = client.BuildDefaultContext();
                    var result = await client.InvokeRawAsync(
                        "maint.vacuumPristines", "{}", ctx).ConfigureAwait(true);
                    if (result.Ok) return true;
                    Debug.LogWarning(
                        $"[FRGVersionControl.Maintenance] IPC vacuum-pristines failed " +
                        $"({result.ErrorKind}): {result.ErrorMessage}. Falling back to legacy CLI.");
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning(
                        $"[FRGVersionControl.Maintenance] IPC vacuum-pristines threw: {ex.Message}. " +
                        $"Falling back to legacy CLI.");
                }
            }
            // Legacy fallback — kept until Task #13 retires SvnCommandRunner entirely.
            var legacy = SvnCommandRunner.VacuumPristines(projectRoot);
            if (!legacy.success && !string.IsNullOrEmpty(legacy.error))
            {
                Debug.LogWarning($"[FRGVersionControl.Maintenance] svn cleanup reported: {legacy.error}");
            }
            return legacy.success;
        }

        /// <summary>
        /// Filesystem-level read of `.svn/pristine/` byte count under the working-copy root
        /// (<see cref="WorkingCopyLayout.WcRoot"/>). Walks the whole pristine store, so call it off the
        /// main thread. Don't use `svn cleanup --dry-run` for this — `--dry-run` for cleanup
        /// behavior isn't reliable and we want a number that reflects actual disk usage anyway.
        /// </summary>
        public static long MeasurePristineBytes(string wcRoot)
        {
            string pristineDir = Path.Combine(wcRoot, ".svn", "pristine");
            if (!Directory.Exists(pristineDir)) return 0;

            try
            {
                return Directory.GetFiles(pristineDir, "*", SearchOption.AllDirectories)
                                .Sum(f => new FileInfo(f).Length);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[FRGVersionControl.Maintenance] Pristine size readout failed: {e.Message}");
                return 0;
            }
        }

        public static int CountPristineObjects(string wcRoot)
        {
            string pristineDir = Path.Combine(wcRoot, ".svn", "pristine");
            if (!Directory.Exists(pristineDir)) return 0;
            try
            {
                return Directory.GetFiles(pristineDir, "*", SearchOption.AllDirectories).Length;
            }
            catch
            {
                return 0;
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "-" + FormatBytes(-bytes);
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (bytes >= GB) return (bytes / (double)GB).ToString("F2", inv) + " GB";
            if (bytes >= MB) return (bytes / (double)MB).ToString("F2", inv) + " MB";
            if (bytes >= KB) return (bytes / (double)KB).ToString("F1", inv) + " KB";
            return bytes.ToString(inv) + " B";
        }
    }
}
