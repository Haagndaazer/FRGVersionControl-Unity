using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.BugReports;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.Services;

namespace FRGVersionControl.Tools
{
    /// <summary>
    /// Gathers the "Versions and project" and activity sections of a bug report. Cached editor
    /// state first; svn-core is asked only for what is not cached, never while it runs a long
    /// operation, within <see cref="IpcBudgetMs"/> in total.
    /// </summary>
    public static class BugReportDiagnostics
    {
        public const int IpcBudgetMs = 10000;

        public sealed class EditorFacts
        {
            public string packageLine;
            public IpcSvnClient client;
            public bool coreAvailable;
            public string corePath;
            public IpcRequestContext ctx;
            public string unityVersion;
            public string os;
            public string editorLog;
            public string serialization;
            public string metaFiles;
            public bool autoLock;
            public bool backgroundChecks;
            public string projectRoot;
            public string toolingDir;
            public WorkingCopyLayout.Info layout;
            public string svnUser;
            public string windowsUser;
        }

        public sealed class CoreFacts
        {
            public string coreVersion;
            public string wireVersion;
            public string svnVersion;
            public string svnPath;
            public string revision;
            public WorkingCopyLayout.Info layout;
            public SvnLogin.Status login;
            public readonly List<string> notes = new List<string>();
        }

        [Serializable]
        private sealed class LayoutEnvelope { public WorkingCopyLayout.Info result; }

        /// <summary>Editor state for the report. Main thread only.</summary>
        public static EditorFacts ReadEditorFacts()
        {
            var f = new EditorFacts();
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BugReportDiagnostics).Assembly);
            f.packageLine = pkg == null
                ? "FRG Version Control (not installed as a package)"
                : $"FRG Version Control {pkg.version} ({pkg.source}: {pkg.packageId})";
            var mgr = FileLockManager.Instance;
            var client = mgr?.IpcClient;
            f.client = client;
            f.coreAvailable = client != null && client.IsAvailable;
            f.corePath = client?.ResolveBinaryPath();
            if (f.coreAvailable) f.ctx = client.BuildDefaultContext();
            f.unityVersion = Application.unityVersion;
            f.os = SystemInfo.operatingSystem;
            f.editorLog = Application.consoleLogPath;
            f.serialization = SerializationName(EditorSettings.serializationMode);
            f.metaFiles = VersionControlSettings.mode;
            f.autoLock = FileLockManager.AutoLockEnabled;
            f.backgroundChecks = FileLockSvnDetectionService.IsEnabled;
            f.projectRoot = SvnCommandRunner.ProjectRoot;
            f.toolingDir = SvnCommandRunner.ToolingConfigDir;
            f.layout = WorkingCopyLayout.Current;
            f.svnUser = mgr?.UserName;
            f.windowsUser = Environment.UserName;
            return f;
        }

        /// <summary>The svn-core details. Safe on any thread; never throws.</summary>
        public static async Task<CoreFacts> ReadCoreFactsAsync(EditorFacts facts, Func<bool> isBusy = null, CancellationToken ct = default)
        {
            var c = new CoreFacts();
            if (!facts.coreAvailable)
            {
                c.notes.Add("svn-core.exe was not found in the package, so the svn details were not collected.");
                return c;
            }
            var caller = new Caller
            {
                client = facts.client,
                ctx = facts.ctx,
                isBusy = isBusy ?? (() => IpcSvnClient.IsExclusiveOpInFlight),
                notes = c.notes,
            };
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                budget.CancelAfter(IpcBudgetMs);
                caller.ct = budget.Token;

                var version = await caller.Call("meta.version", "the svn and svn-core versions").ConfigureAwait(false);
                if (version != null)
                {
                    c.coreVersion = IpcJson.ReadScalar(version, "coreVersion", inResult: true);
                    c.wireVersion = IpcJson.ReadScalar(version, "wireVersion", inResult: true);
                    c.svnVersion = IpcJson.ReadScalar(version, "svnVersion", inResult: true);
                    c.svnPath = IpcJson.ReadScalar(version, "svnPath", inResult: true);
                }
                if (facts.layout == null)
                {
                    var layout = await caller.Call("meta.layout", "the working-copy layout").ConfigureAwait(false);
                    if (layout != null) c.layout = JsonUtility.FromJson<LayoutEnvelope>(layout)?.result;
                }
                var info = await caller.Call("meta.info", "the working-copy revision").ConfigureAwait(false);
                if (info != null) c.revision = IpcJson.ReadScalar(info, "revision", inResult: true);
                var logins = await caller.Call("auth.logins", "the sign-in status").ConfigureAwait(false);
                if (logins != null) c.login = SvnLogin.ParseStatus(logins);
            }
            return c;
        }

        public static string FormatDetails(EditorFacts e, CoreFacts c)
        {
            var sb = new StringBuilder();
            void Line(string text) => sb.Append("- ").Append(text).Append('\n');

            Line(e.packageLine);
            Line($"svn-core {c.coreVersion ?? "unknown"}, wire v{c.wireVersion ?? "?"}, at {e.corePath ?? "(not found)"}");
            Line($"svn {c.svnVersion ?? "unknown"} at {c.svnPath ?? "(unknown)"}");
            Line($"Unity {e.unityVersion} on {e.os}");
            Line($"Editor log: {e.editorLog}");
            Line($"Asset serialization: {e.serialization}; version control mode: {e.metaFiles}");
            Line($"Auto-Lock: {OnOff(e.autoLock)}; background status checks: {OnOff(e.backgroundChecks)}");
            Line($"Project folder: {e.projectRoot}");
            var layout = c.layout ?? e.layout;
            if (layout != null)
            {
                var where = layout.isWcRoot ? "the project folder is the working-copy root" : $"the project folder is {layout.projectPath} inside it";
                Line($"Working-copy root: {layout.wcRoot} ({where})");
                Line($"Project URL: {layout.projectUrl}; repository root: {layout.repoRoot}");
            }
            else
            {
                Line("Working-copy layout: not available");
            }
            Line("Working-copy revision: " + (string.IsNullOrEmpty(c.revision) ? "not available" : "r" + c.revision));
            Line($"Signed in as: {(string.IsNullOrEmpty(e.svnUser) ? "not signed in" : e.svnUser)}; login saved for this project: {LoginSaved(c.login)}");
            Line($"Windows user: {e.windowsUser}");
            foreach (var note in c.notes) Line(note);
            return sb.ToString();
        }

        public static string ReadOpLog(string toolingDir)
        {
            try
            {
                var raw = BugReport.ReadTail(Path.Combine(toolingDir ?? "", "op-log.txt"), BugReport.OpLogReadBytes);
                if (string.IsNullOrWhiteSpace(raw)) return "No version control activity recorded yet.";
                return BugReport.TailLines(raw, BugReport.OpLogLines, BugReport.SectionMax);
            }
            catch (Exception ex)
            {
                return "Not available: the activity log could not be read (" + ex.Message + ").";
            }
        }

        private static string LoginSaved(SvnLogin.Status login)
        {
            if (login == null) return "unknown";
            if (!login.usesPasswordCache) return "not needed for this repository";
            return login.projectLogin != null ? "yes" : "no";
        }

        private static string OnOff(bool on) => on ? "on" : "off";

        private static string SerializationName(SerializationMode mode)
        {
            if (mode == SerializationMode.ForceText) return "Force Text";
            if (mode == SerializationMode.ForceBinary) return "Force Binary";
            return "Mixed";
        }

        private sealed class Caller
        {
            public IpcSvnClient client;
            public IpcRequestContext ctx;
            public Func<bool> isBusy;
            public List<string> notes;
            public CancellationToken ct;
            private bool m_Stopped;

            public async Task<string> Call(string op, string what)
            {
                if (m_Stopped) return null;
                if (isBusy())
                {
                    m_Stopped = true;
                    var running = IpcSvnClient.ExclusiveOpName;
                    notes.Add($"svn-core was busy running {(string.IsNullOrEmpty(running) ? "another operation" : running)}, so {what} and the details after it were not collected.");
                    return null;
                }
                try
                {
                    var r = await client.InvokeRawAsync(op, "{}", ctx, ct).ConfigureAwait(false);
                    if (r.Ok) return r.RawLine;
                    if (r.Busy)
                    {
                        m_Stopped = true;
                        notes.Add($"svn-core was busy, so {what} and the details after it were not collected.");
                        return null;
                    }
                    var reason = string.IsNullOrEmpty(r.ErrorDefaultAction) ? r.ErrorMessage : r.ErrorDefaultAction;
                    notes.Add($"Not available: {what} ({op}: {reason}).");
                    return null;
                }
                catch (OperationCanceledException)
                {
                    m_Stopped = true;
                    notes.Add($"Not available: svn-core did not answer within {IpcBudgetMs / 1000} seconds, so {what} and the details after it were not collected.");
                    return null;
                }
                catch (Exception ex)
                {
                    notes.Add($"Not available: {what} ({ex.Message}).");
                    return null;
                }
            }
        }
    }
}
