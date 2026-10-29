using System.IO;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tools
{
    /// <summary>
    /// Wizard for initial file-locking setup.
    ///
    /// Phase 2 Step G — SVN backend version. The wizard's role is purely a preflight check
    /// that the workstation is configured correctly:
    ///   1. Verify the svn CLI is reachable (TortoiseSVN-with-CLI-tools install).
    ///   2. Confirm the project root is an SVN working copy.
    ///   3. Confirm admin-config.json has currentSvnUsername populated.
    ///   4. Probe the server (`svn info` against repo URL) — auth + connectivity check.
    ///   5. Confirm svn:needs-lock auto-props are configured in the user's SVN config.
    ///
    /// All checks are read-only; nothing is mutated server-side.
    /// </summary>
    public class FileLockSetupWizard : EditorWindow
    {
        public static void ShowWizard()
        {
            var window = GetWindow<FileLockSetupWizard>("File Locking Setup");
            window.minSize = new Vector2(540, 440);
            window.Show();
        }

        private Vector2 m_ScrollPosition;
        private string m_StatusMessage = "";
        private bool m_SetupComplete = false;
        private bool m_NeedsBootstrap = false;
        private string m_BootstrapRepoUrl = "";
        private bool m_AutoRunOnFirstOpen = true;

        private void OnEnable()
        {
            // Smart default: auto-run on first open so the user gets immediate feedback,
            // not a "click here to start" speed bump.
            if (m_AutoRunOnFirstOpen && string.IsNullOrEmpty(m_StatusMessage))
            {
                EditorApplication.delayCall += () => { RunSetup(); Repaint(); };
                m_AutoRunOnFirstOpen = false;
            }
        }

        private void OnGUI()
        {
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);

            GUILayout.Label("File Locking System Setup (SVN backend)", EditorStyles.boldLabel);
            GUILayout.Space(10);

            GUILayout.Label("This wizard validates your workstation for SVN-based file locking. " +
                            "Runs automatically on open; click below to re-run after fixing issues.",
                            EditorStyles.wordWrappedLabel);
            GUILayout.Space(5);

            GUILayout.Label("1. svn CLI is reachable (TortoiseSVN with CLI tools, or VisualSVN)",
                            EditorStyles.wordWrappedLabel);
            GUILayout.Label("2. Project root is an SVN working copy", EditorStyles.wordWrappedLabel);
            GUILayout.Label("3. Per-WC SVN auth cache has cached credentials",
                            EditorStyles.wordWrappedLabel);
            GUILayout.Label("4. Server is reachable + credentials are valid", EditorStyles.wordWrappedLabel);
            GUILayout.Label("5. svn:needs-lock auto-props are configured in your SVN config",
                            EditorStyles.wordWrappedLabel);

            GUILayout.Space(10);

            EditorGUILayout.HelpBox(
                "First-run note: if this validation fails on Step 4 (server probe), accept the " +
                "server's SSL certificate from a terminal:\n" +
                "  svn info <repo-url>  →  choose `(p)ermanent` when prompted to trust the cert\n" +
                "Then re-run this wizard.",
                MessageType.None);

            GUILayout.Space(10);

            if (GUILayout.Button(new GUIContent("Run Setup Validation",
                                                "Re-runs the 5 preflight checks. Safe to click anytime."),
                                 GUILayout.Height(30)))
            {
                RunSetup();
            }

            GUILayout.Space(10);

            if (!string.IsNullOrEmpty(m_StatusMessage))
            {
                EditorGUILayout.HelpBox(m_StatusMessage,
                                        m_SetupComplete ? MessageType.Info : MessageType.Warning);
            }

            // Phase 4 Step 2: inline bootstrap helper for the auth-cache failure case.
            if (m_NeedsBootstrap)
            {
                GUILayout.Space(8);
                EditorGUILayout.LabelField("Auth cache bootstrap helper", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Click below to copy the bootstrap command to your clipboard, then open a terminal in " +
                    "this project's root and paste it. SVN will prompt for your password and cache it for " +
                    "this working copy. Re-run the wizard when done.",
                    MessageType.None);
                if (GUILayout.Button(new GUIContent("Copy Bootstrap Command to Clipboard",
                                                    "Copies an `svn info` command pre-filled with the repo URL " +
                                                    "and per-WC config dir. You then paste it into a terminal."),
                                     GUILayout.Height(24)))
                {
                    string cmd = $"svn info {m_BootstrapRepoUrl} " +
                                 $"--config-dir \"{SvnCommandRunner.ToolingConfigDir}\" " +
                                 "--username <YOUR_USERNAME> " +
                                 "--trust-server-cert-failures unknown-ca,cn-mismatch";
                    EditorGUIUtility.systemCopyBuffer = cmd;
                    m_StatusMessage += "\n\n[clipboard] Bootstrap command copied. Replace <YOUR_USERNAME> " +
                                       "with your SVN username, paste in a terminal, run, enter password.";
                }
                if (GUILayout.Button(new GUIContent("Open Terminal in Project Root",
                                                    "Opens cmd.exe with cwd=project root. Paste the copied " +
                                                    "command and run it."),
                                     GUILayout.Height(24)))
                {
                    OpenTerminalInProjectRoot();
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private static void OpenTerminalInProjectRoot()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = SvnCommandRunner.ProjectRoot,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[FRGVersionControl.SetupWizard] Could not open terminal: {ex.Message}. " +
                                 $"Open a terminal manually at: {SvnCommandRunner.ProjectRoot}");
            }
        }

        private async void RunSetup()
        {
            m_StatusMessage = "Running setup...\n";
            m_SetupComplete = false;
            m_NeedsBootstrap = false;
            m_BootstrapRepoUrl = "";

            // Step 1: svn CLI reachable — probed via the svn-core IPC meta.version op.
            // The IPC binary itself resolves svn.exe via its discovery chain
            // (Tortoise → VisualSVN → PATH → ctx.svnBinaryHint).
            m_StatusMessage += "\n1. Checking svn CLI availability...";
            Repaint();
            bool svnAvailable = await ProbeSvnAvailableAsync();
            if (!svnAvailable)
            {
                m_StatusMessage += "\n[FAIL] svn CLI not found. Install TortoiseSVN with the " +
                                   "Command-Line Tools option enabled (or install VisualSVN). " +
                                   "Then restart Unity.";
                Repaint();
                return;
            }
            m_StatusMessage += "\n[OK] svn CLI detected";
            Repaint();

            // Step 2: project root is a WC
            m_StatusMessage += "\n\n2. Checking working-copy state...";
            Repaint();
            string projectRoot = SvnCommandRunner.ProjectRoot;
            string wcDb = Path.Combine(projectRoot, ".svn", "wc.db");
            if (!File.Exists(wcDb))
            {
                m_StatusMessage += $"\n[FAIL] Project root is not an SVN working copy. " +
                                   $"Expected `.svn/wc.db` at:\n  {projectRoot}\n" +
                                   "Run `svn checkout` from outside Unity to create the WC.";
                return;
            }
            m_StatusMessage += "\n[OK] SVN working copy detected at project root";
            Repaint();

            // Step 3: SVN identity from per-WC auth cache (Phase 3 — replaces
            // the prior admin-config.json `currentSvnUsername` storage check).
            m_StatusMessage += "\n\n3. Checking per-WC SVN auth cache for identity...";
            Repaint();
            string toolingConfigDir = SvnCommandRunner.ToolingConfigDir;
            string cachedUsername = null;
            try
            {
                cachedUsername = SvnAuthCacheReader.GetUsernameForCurrentRepo();
            }
            catch (System.Exception ex)
            {
                m_StatusMessage += $"\n[WARN] Auth cache read errored: {ex.Message}";
            }

            if (string.IsNullOrEmpty(cachedUsername))
            {
                // Try to derive the repo URL even though auth cache is empty — `svn info` on the
                // local WC's .svn metadata returns the URL without needing server creds.
                var localInfoResult = await ProbeRepoInfoAsync();
                m_BootstrapRepoUrl = localInfoResult.repoUrl ?? "<repo-url>";
                m_NeedsBootstrap = true;
                m_StatusMessage +=
                    "\n[FAIL] No SVN username found in per-WC auth cache. Bootstrap below " +
                    "(use the helper buttons after this validation finishes).";
                Repaint();
                return;
            }
            m_StatusMessage += $"\n[OK] SVN identity from auth cache: {cachedUsername}";
            Repaint();

            // Step 4: server reachable + auth — via svn-core IPC meta.info
            m_StatusMessage += "\n\n4. Probing SVN server (svn info)...";
            Repaint();
            var info = await ProbeRepoInfoAsync();
            if (string.IsNullOrEmpty(info.repoUrl))
            {
                m_StatusMessage += "\n[FAIL] `svn info` failed. Check that the server is reachable, " +
                                   "your credentials are valid, and (if applicable) the SSL " +
                                   "certificate is trusted. Try running `svn info` from a terminal " +
                                   "for a detailed error message.";
                if (!string.IsNullOrEmpty(info.errorDetail))
                {
                    m_StatusMessage += $"\n   {info.errorDetail}";
                }
                Repaint();
                return;
            }
            m_StatusMessage += $"\n[OK] Server reachable: {info.repoUrl} (rev {info.revision})";
            Repaint();

            // Step 5: auto-props configured
            m_StatusMessage += "\n\n5. Checking svn:needs-lock auto-props in user config...";
            Repaint();
            string userConfig = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "Subversion", "config");
            bool autoPropsConfigured = false;
            if (File.Exists(userConfig))
            {
                string text = File.ReadAllText(userConfig);
                if (text.Contains("svn:needs-lock") &&
                    text.Contains("enable-auto-props = yes"))
                {
                    autoPropsConfigured = true;
                }
            }
            if (!autoPropsConfigured)
            {
                m_StatusMessage += "\n[WARN] svn:needs-lock auto-props are not enabled in your SVN " +
                                   "config (%APPDATA%/Subversion/config). New lockable files added " +
                                   "via Unity may not get the property set automatically. Configure " +
                                   "auto-props per the team docs, or rely on a server-side pre-commit " +
                                   "hook.";
                // Not fatal — server-side enforcement is the safety net.
            }
            else
            {
                m_StatusMessage += "\n[OK] svn:needs-lock auto-props are configured";
            }
            Repaint();

            m_StatusMessage += "\n\nSetup complete. File locking is ready.\n\n" +
                               "Note: locks are server-authoritative. The Ignore-on-Commit workflow (" +
                               "edit-while-locked-by-other) parks your edits in the `ignore-on-commit` " +
                               "changelist on top of clearing the read-only flag; svn:needs-lock " +
                               "server-side enforcement prevents accidental commits.";
            m_SetupComplete = true;
        }

        // ====================================================================
        // 0.2.43 — IPC-routed probes used during wizard validation.
        // Falls back to legacy SvnCommandRunner if the IPC binary is unavailable.
        // ====================================================================

        private static async System.Threading.Tasks.Task<bool> ProbeSvnAvailableAsync()
        {
            var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
            if (client.IsAvailable)
            {
                try
                {
                    var ctx = client.BuildDefaultContext();
                    var result = await client.InvokeRawAsync("meta.version", "{}", ctx).ConfigureAwait(true);
                    if (result.Ok)
                    {
                        // meta.version returns {coreVersion, svnVersion, wireVersion}. If the
                        // core couldn't locate svn.exe, svnVersion comes back as "not-found".
                        return !result.RawLine.Contains("\"svnVersion\":\"not-found\"");
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[FRGVersionControl.SetupWizard] IPC meta.version threw, falling back: {ex.Message}");
                }
            }
            return SvnCommandRunner.IsSvnAvailable();
        }

        private struct RepoInfoProbe
        {
            public string repoUrl;
            public int revision;
            public string errorDetail;
        }

        private static async System.Threading.Tasks.Task<RepoInfoProbe> ProbeRepoInfoAsync()
        {
            var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
            if (client.IsAvailable)
            {
                try
                {
                    var ctx = client.BuildDefaultContext();
                    // meta.info defaults to ctx.wc when no path/url is supplied — same as
                    // SvnCommandRunner.Info(".").
                    var result = await client.InvokeRawAsync("meta.info", "{}", ctx).ConfigureAwait(true);
                    if (result.Ok)
                    {
                        // result.RawLine shape: {"v":1,"id":"...","ok":true,"result":{"url":"...","revision":N,...}}
                        // Parse the two fields we need via the existing JsonUtility envelope.
                        var parsed = UnityEngine.JsonUtility.FromJson<MetaInfoResponseShim>(result.RawLine);
                        return new RepoInfoProbe
                        {
                            repoUrl = parsed?.result?.url,
                            revision = parsed?.result?.revision ?? 0,
                            errorDetail = null,
                        };
                    }
                    return new RepoInfoProbe
                    {
                        repoUrl = null,
                        revision = 0,
                        errorDetail = !string.IsNullOrEmpty(result.ErrorDefaultAction)
                            ? $"{result.ErrorMessage} — {result.ErrorDefaultAction}"
                            : (result.ErrorMessage ?? result.ErrorKind),
                    };
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[FRGVersionControl.SetupWizard] IPC meta.info threw, falling back: {ex.Message}");
                }
            }
            var legacy = SvnCommandRunner.Info(".");
            return new RepoInfoProbe
            {
                repoUrl = legacy?.repoUrl,
                revision = legacy?.revision ?? 0,
                errorDetail = legacy == null ? "Legacy svn info returned null" : null,
            };
        }

        [System.Serializable]
        private sealed class MetaInfoResponseShim
        {
            public MetaInfoResultShim result;
        }

        [System.Serializable]
        private sealed class MetaInfoResultShim
        {
            public string url;
            public string repoRoot;
            public string repoUuid;
            public int revision;
        }
    }
}
