using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using FRGVersionControl.Core.Abstractions;

namespace FRGVersionControl.Core.IpcClient
{
    /// <summary>
    /// Locates the bundled svn-core.exe binary and runs one-shot or persistent processes.
    /// Persistent-mode lifecycle is owned by IpcSvnClient.
    /// </summary>
    public sealed class SvnCoreProcess
    {
        private readonly IFrgLogger m_Logger;
        private readonly IProjectPaths m_Paths;
        private string m_BinaryPath;
        private bool m_BinaryProbed;

        public SvnCoreProcess(IFrgLogger logger, IProjectPaths paths)
        {
            m_Logger = logger;
            m_Paths = paths;
        }

        public string ResolveBinaryPath()
        {
            // Cache only POSITIVE results. A negative cache (probed but not found) used to
            // stick across UPM re-adds because m_BinaryProbed was set true regardless of
            // outcome — so once the binary was missing at startup it stayed "missing" even
            // after the next package version brought it in. Re-probe on every call when the
            // cached path is null or the file is gone.
            if (m_BinaryProbed && !string.IsNullOrEmpty(m_BinaryPath) && File.Exists(m_BinaryPath))
            {
                return m_BinaryPath;
            }
            m_BinaryProbed = true;
            m_BinaryPath = null;

            // Primary: UPM package install location (Library/PackageCache/com.frg.versioncontrol@<hash>/Editor/SvnCore~/).
            // PackageInfo.FindForAssembly resolves the package root regardless of whether the
            // package is cached, embedded, or developed locally inside Packages/.
            var packageRoot = TryResolvePackageRoot();
            if (!string.IsNullOrEmpty(packageRoot))
            {
                var packaged = Path.Combine(packageRoot, "Editor/SvnCore~/svn-core.exe");
                if (File.Exists(packaged))
                {
                    m_BinaryPath = packaged.Replace('\\', '/');
                    return m_BinaryPath;
                }
            }

            // Fallbacks: in-repo development layout (the toolset's own source tree, not a
            // consumer's project) plus the raw dotnet publish output for "I just built it" cases.
            var candidates = new[]
            {
                Path.Combine(m_Paths.WorkingCopyRoot, "Plugins/Unity/Editor/SvnCore~/svn-core.exe"),
                Path.Combine(m_Paths.WorkingCopyRoot, "SvnCore/bin/Release/net9.0/win-x64/publish/svn-core.exe"),
                Path.Combine(m_Paths.WorkingCopyRoot, "SvnCore/bin/Release/net9.0/svn-core.exe"),
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c))
                {
                    m_BinaryPath = c.Replace('\\', '/');
                    return m_BinaryPath;
                }
            }
            return null;
        }

        private static string TryResolvePackageRoot()
        {
            try
            {
                var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SvnCoreProcess).Assembly);
                return pkg != null ? pkg.resolvedPath : null;
            }
            catch
            {
                return null;
            }
        }

        public bool IsAvailable => ResolveBinaryPath() != null;

        // BOM-free UTF-8 — Encoding.UTF8 emits a 0xEF 0xBB 0xBF preamble on the first write
        // because the singleton was constructed with emitBOM=true. If the child process's
        // Console.In falls back to the OEM code page (CP437 on US-Windows), the BOM bytes
        // decode to "∩╗┐" and the subsequent System.Text.Json parse fails at byte 0xE2.
        private static readonly Encoding s_BomlessUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public ProcessStartInfo BuildStartInfo(bool persistent)
        {
            var binary = ResolveBinaryPath();
            if (binary == null) throw new FileNotFoundException("svn-core.exe missing");
            var psi = new ProcessStartInfo
            {
                FileName = binary,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = s_BomlessUtf8,
                StandardInputEncoding = s_BomlessUtf8,
                StandardErrorEncoding = s_BomlessUtf8,
                WorkingDirectory = m_Paths.WorkingCopyRoot,
            };
            if (persistent) psi.ArgumentList.Add("--persistent");
            return psi;
        }

        public void LogMissingBinary()
        {
            m_Logger?.Error(
                "svn-core.exe missing — run Build-SvnCore.ps1 or reinstall the package. File locking features disabled.");
        }
    }
}
