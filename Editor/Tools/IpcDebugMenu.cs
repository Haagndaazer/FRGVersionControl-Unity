using System;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Abstractions;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Hooks;
using UnityEditor;
using UnityEngine;

namespace FRGVersionControl.Tools
{
    /// <summary>
    /// Foundation debug entry point for the svn-core IPC binary. Verifies the IPC pipe is
    /// reachable and reports svn-core / svn / wire versions in the Console. Used during the
    /// 0.2.29 verification handoff; later versions will route real ops through the same
    /// client and remove this menu item.
    /// </summary>
    public static class IpcDebugMenu
    {
        [MenuItem("FRG Version Control/Debug/Test svn-core IPC", priority = 2000)]
        public static async void TestIpc()
        {
            var logger = new UnityLogger("FRGVersionControl.Ipc");
            var client = FileLockManager.Instance.IpcClient;

            if (!client.IsAvailable)
            {
                EditorUtility.DisplayDialog(
                    "svn-core.exe missing",
                    "svn-core.exe was not found inside the FRG Version Control package.\n\n" +
                    "If you installed via UPM, reinstall the package — the binary ships inside " +
                    "`Editor/SvnCore~/`.\n\n" +
                    "If you're developing the toolset itself, run `Build-SvnCore.ps1` from the " +
                    "toolset repo root to publish the binary into that path.",
                    "OK");
                logger.Error("svn-core.exe missing — reinstall the package or run Build-SvnCore.ps1 in the toolset repo.");
                return;
            }

            try
            {
                logger.Info("Probing svn-core meta.version at " + client.ResolveBinaryPath());
                var result = await client.MetaVersionAsync().ConfigureAwait(true);
                var msg = "svn-core: " + result.coreVersion +
                          " | svn.exe: " + result.svnVersion +
                          " | wire: v" + result.wireVersion;
                logger.Info(msg);
                EditorUtility.DisplayDialog("svn-core IPC OK", msg, "OK");
            }
            catch (IpcOperationException ipcEx)
            {
                logger.Error("svn-core returned error: " + ipcEx.Message);
                EditorUtility.DisplayDialog("svn-core IPC error", ipcEx.Message, "OK");
            }
            catch (Exception ex)
            {
                logger.Error("svn-core IPC probe failed", ex);
                EditorUtility.DisplayDialog("svn-core IPC failure", ex.Message, "OK");
            }
        }

        [MenuItem("FRG Version Control/Debug/Show svn-core binary path", priority = 2001)]
        public static void ShowBinaryPath()
        {
            var client = FileLockManager.Instance.IpcClient;
            var path = client.ResolveBinaryPath();
            if (string.IsNullOrEmpty(path))
            {
                EditorUtility.DisplayDialog(
                    "svn-core.exe not found",
                    "Run Build-SvnCore.ps1 from the repo root to publish the binary.",
                    "OK");
                return;
            }
            EditorUtility.DisplayDialog("svn-core binary path", path, "OK");
            Debug.Log("[FRGVersionControl.Ipc] svn-core binary: " + path);
        }
    }
}
