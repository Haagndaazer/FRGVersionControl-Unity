using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tools
{
    /// <summary>
    /// Wizard for initial file-locking setup: a read-only preflight of this workstation.
    ///   1. svn-core finds a usable svn.exe (meta.version).
    ///   2. The project folder is in an SVN working copy — the checkout itself or a folder inside a
    ///      larger checkout such as trunk/ of the whole repository (meta.layout).
    ///   3. This project's auth cache (.svn-tooling-config) holds an SVN username.
    ///   4. The server answers for this project's URL with those credentials (auth.bootstrap).
    ///   5. Optional: TortoiseSVN auto-props set svn:needs-lock on files added outside Unity.
    /// Nothing is changed on the server or in the working copy.
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
        [SerializeField] private string m_StatusMessage = "";
        [SerializeField] private bool m_SetupComplete;
        [SerializeField] private bool m_NeedsBootstrap;
        [SerializeField] private string m_BootstrapRepoUrl = "";
        [SerializeField] private bool m_HasAutoRun;
        private bool m_Running;

        private void OnEnable()
        {
            // Run automatically the first time the window opens (not again after a script reload).
            if (!m_HasAutoRun)
            {
                m_HasAutoRun = true;
                EditorApplication.delayCall += () => { RunSetup(); Repaint(); };
            }
        }

        private void OnGUI()
        {
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);

            GUILayout.Label("File Locking System Setup (SVN backend)", EditorStyles.boldLabel);
            GUILayout.Space(10);

            GUILayout.Label("This wizard checks that this workstation is ready for SVN file locking. " +
                            "It runs when the window opens; click below to run it again after fixing something.",
                            EditorStyles.wordWrappedLabel);
            GUILayout.Space(5);

            GUILayout.Label("1. svn command-line client is installed (TortoiseSVN with command line client tools)", EditorStyles.wordWrappedLabel);
            GUILayout.Label("2. The project folder is in an SVN checkout", EditorStyles.wordWrappedLabel);
            GUILayout.Label("3. Your SVN username is cached for this project", EditorStyles.wordWrappedLabel);
            GUILayout.Label("4. The SVN server answers with your cached credentials", EditorStyles.wordWrappedLabel);
            GUILayout.Label("5. Optional: TortoiseSVN auto-props for svn:needs-lock", EditorStyles.wordWrappedLabel);

            GUILayout.Space(10);

            EditorGUILayout.HelpBox(
                "First-run note: if step 4 reports an untrusted certificate, accept it once from a terminal " +
                "in this project folder:\n" +
                $"  svn info <repo-url> --config-dir \"{SvnCommandRunner.ToolingConfigDir}\"\n" +
                "choose (p)ermanent when asked to trust the certificate, then run this wizard again.",
                MessageType.None);

            GUILayout.Space(10);

            using (new EditorGUI.DisabledScope(m_Running))
            {
                if (GUILayout.Button(new GUIContent("Run Setup Validation",
                                                    "Runs the five checks again. Safe to click anytime."),
                                     GUILayout.Height(30)))
                {
                    RunSetup();
                }
            }

            GUILayout.Space(10);

            if (!string.IsNullOrEmpty(m_StatusMessage))
            {
                EditorGUILayout.HelpBox(m_StatusMessage,
                                        m_SetupComplete ? MessageType.Info : MessageType.Warning);
            }

            // Inline bootstrap helper for the auth-cache failure case.
            if (m_NeedsBootstrap)
            {
                GUILayout.Space(8);
                EditorGUILayout.LabelField("Auth cache bootstrap helper", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Click below to copy the bootstrap command to your clipboard, then open a terminal in " +
                    "this project folder and paste it. SVN will prompt for your password and cache it for " +
                    "this project. Run the wizard again when done.",
                    MessageType.None);
                if (GUILayout.Button(new GUIContent("Copy Bootstrap Command to Clipboard",
                                                    "Copies an `svn info` command pre-filled with the repo URL " +
                                                    "and this project's config dir. You then paste it into a terminal."),
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
                if (GUILayout.Button(new GUIContent("Open Terminal in Project Folder",
                                                    "Opens cmd.exe in the project folder. Paste the copied " +
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
            catch (Exception ex)
            {
                Debug.LogWarning($"[FRGVersionControl.SetupWizard] Could not open terminal: {ex.Message}. " +
                                 $"Open a terminal manually at: {SvnCommandRunner.ProjectRoot}");
            }
        }

        private void Append(string text)
        {
            m_StatusMessage += text;
            Repaint();
        }

        private async void RunSetup()
        {
            if (m_Running) return;
            m_Running = true;
            try
            {
                await RunSetupSteps();
            }
            catch (Exception ex)
            {
                Append($"\n[FAIL] The wizard stopped unexpectedly: {ex.Message}. Run the wizard again; if it keeps failing, restart the editor.");
            }
            finally
            {
                m_Running = false;
                Repaint();
            }
        }

        private async Task RunSetupSteps()
        {
            m_StatusMessage = "Running setup...\n";
            m_SetupComplete = false;
            m_NeedsBootstrap = false;
            m_BootstrapRepoUrl = "";
            var client = FileLockManager.Instance.IpcClient;
            if (!client.IsAvailable)
            {
                Append("\n[FAIL] svn-core.exe was not found in the package. Reinstall the FRG Version Control package, then restart the editor.");
                return;
            }

            // Step 1: svn.exe found by svn-core.
            Append("\n1. Checking the svn command-line client...");
            var version = await client.InvokeRawAsync("meta.version", "{}", client.BuildDefaultContext()).ConfigureAwait(true);
            var svnVersion = version.Ok ? IpcJson.ReadScalar(version.RawLine, "svnVersion", inResult: true) : null;
            if (string.IsNullOrEmpty(svnVersion) || svnVersion == "not-found")
            {
                Append("\n[FAIL] svn.exe was not found. Install TortoiseSVN with the 'command line client tools' option, then restart the editor.");
                return;
            }
            Append($"\n[OK] svn {svnVersion}");

            // Step 2: where the project folder sits in its working copy.
            Append("\n\n2. Checking the working copy...");
            var layout = await Task.Run(() => WorkingCopyLayout.Fetch()).ConfigureAwait(true);
            if (layout.busy)
            {
                Append("\n[WAIT] SVN is busy right now. Run the wizard again in a moment.");
                return;
            }
            if (layout.info == null)
            {
                Append("\n[FAIL] " + (layout.error ?? "Could not read the working copy. Run the wizard again."));
                return;
            }
            foreach (var line in SetupWizardText.LayoutLines(layout.info)) Append("\n" + line);

            // Step 3: SVN identity from this project's auth cache.
            Append("\n\n3. Checking the cached SVN username for this project...");
            string cachedUsername = null;
            try
            {
                cachedUsername = SvnAuthCacheReader.GetUsernameForRepoUrl(layout.info.projectUrl);
            }
            catch (Exception ex)
            {
                Append($"\n[WARN] Reading the auth cache failed: {ex.Message}");
            }
            if (string.IsNullOrEmpty(cachedUsername))
            {
                if (!SvnAuthCacheReader.UsesPasswordCache(layout.info.projectUrl))
                {
                    Append("\n[NOTE] This repository's URL scheme does not keep passwords in the SVN auth cache, " +
                           "so no username can be read from it.");
                }
                else
                {
                    m_BootstrapRepoUrl = layout.info.projectUrl;
                    m_NeedsBootstrap = true;
                    Append("\n[FAIL] No SVN username is cached for this project. Use the bootstrap helper below, " +
                           "then run the wizard again.");
                    return;
                }
            }
            else
            {
                Append($"\n[OK] SVN username: {cachedUsername}");
            }

            // Step 4: the server answers for this project's URL with the cached credentials.
            Append("\n\n4. Contacting the SVN server...");
            var probe = await client.InvokeRawAsync("auth.bootstrap", "{}", client.BuildDefaultContext()).ConfigureAwait(true);
            if (!probe.Ok)
            {
                Append("\n[FAIL] " + (probe.Busy || probe.StillRunning
                    ? "SVN is busy right now. Run the wizard again in a moment."
                    : $"{probe.ErrorMessage?.Trim()} {probe.ErrorDefaultAction}"));
                return;
            }
            if (IpcJson.ReadScalar(probe.RawLine, "reachable", inResult: true) != "true")
            {
                var message = IpcJson.ReadScalar(probe.RawLine, "message", inResult: true);
                var action = IpcJson.ReadScalar(probe.RawLine, "defaultAction", inResult: true);
                Append($"\n[FAIL] The SVN server did not accept the request: {FirstLine(message)}\n   {action}");
                return;
            }
            var wcRevision = await client.InvokeRawAsync("meta.info", "{}", client.BuildDefaultContext()).ConfigureAwait(true);
            var rev = wcRevision.Ok ? IpcJson.ReadScalar(wcRevision.RawLine, "revision", inResult: true) : null;
            Append($"\n[OK] Server reachable at {layout.info.projectUrl}" + (string.IsNullOrEmpty(rev) ? "" : $" (working copy at r{rev})"));

            // Step 5: TortoiseSVN auto-props (advisory only).
            Append("\n\n5. Checking TortoiseSVN auto-props (optional)...");
            string userConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subversion", "config");
            string configText = null;
            try { if (File.Exists(userConfig)) configText = File.ReadAllText(userConfig); }
            catch (Exception ex) { Append($"\n[WARN] Could not read {userConfig}: {ex.Message}"); }
            if (SetupWizardText.AutoPropsSetNeedsLock(configText))
            {
                Append("\n[OK] TortoiseSVN auto-props set svn:needs-lock");
            }
            else
            {
                Append("\n[NOTE] TortoiseSVN auto-props for svn:needs-lock are not enabled in %APPDATA%\\Subversion\\config. " +
                       "Files added from Unity already get svn:needs-lock; this only matters for files you add with TortoiseSVN.");
            }

            m_StatusMessage += "\n\nSetup complete. File locking is ready.\n\n" +
                               "Note: locks are server-authoritative. The Ignore-on-Commit workflow (" +
                               "edit-while-locked-by-other) parks your edits in the `ignore-on-commit` " +
                               "changelist on top of clearing the read-only flag; svn:needs-lock " +
                               "server-side enforcement prevents accidental commits.";
            m_SetupComplete = true;
            Repaint();
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length > 0) return line;
            }
            return "";
        }
    }

    /// <summary>Pure text helpers for the Setup Wizard (unit-tested).</summary>
    public static class SetupWizardText
    {
        /// <summary>Step 2's lines for a working-copy layout. A checkout of the whole repository is
        /// a supported layout: it gets one informational note about disk space, never a warning.</summary>
        public static List<string> LayoutLines(WorkingCopyLayout.Info info)
        {
            var lines = new List<string>();
            if (info.isWcRoot)
            {
                lines.Add("[OK] SVN working copy detected at the project folder.");
            }
            else
            {
                lines.Add($"[OK] SVN working copy detected. This project is the '{info.projectPath}' folder of the checkout at {info.wcRoot}.");
                if (info.wcRootIsRepositoryRoot)
                {
                    lines.Add("Note: this checkout contains the whole repository, including every branch and tag. That works fine; " +
                              $"checking out only {info.projectUrl} would use less disk space.");
                }
            }
            return lines;
        }

        /// <summary>True when an SVN client config (ini) enables auto-props and has an active
        /// [auto-props] rule that sets svn:needs-lock. Comments and spacing are handled.</summary>
        public static bool AutoPropsSetNeedsLock(string configText)
        {
            if (string.IsNullOrEmpty(configText)) return false;
            string section = "";
            bool enabled = false;
            bool rule = false;
            foreach (var raw in configText.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                if (section == "miscellany" && string.Equals(key, "enable-auto-props", StringComparison.OrdinalIgnoreCase))
                {
                    enabled = value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1";
                }
                else if (section == "auto-props" && value.IndexOf("svn:needs-lock", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    rule = true;
                }
            }
            return enabled && rule;
        }
    }
}
