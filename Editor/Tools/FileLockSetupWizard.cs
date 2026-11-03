using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.Core.BugReports;
using FRGVersionControl.UI;

namespace FRGVersionControl.Tools
{
    /// <summary>
    /// Wizard for initial file-locking setup: a preflight of this workstation that also signs the
    /// project in to SVN.
    ///   1. svn-core finds a usable svn.exe (meta.version).
    ///   2. The project folder is in an SVN working copy — the checkout itself or a folder inside a
    ///      larger checkout such as trunk/ of the whole repository (meta.layout).
    ///   3. The project is signed in to SVN (auth.logins). If not, a sign-in panel offers the logins
    ///      TortoiseSVN saved for the server ("Log in as ...", auth.adoptSavedLogin) and a username /
    ///      password form (auth.signIn).
    ///   4. The server answers for this project's URL with that login (auth.bootstrap); a rejected
    ///      login reopens the sign-in panel.
    ///   5. Optional: TortoiseSVN auto-props set svn:needs-lock on files added outside Unity.
    /// Nothing is changed on the server or in the working copy.
    /// </summary>
    public class FileLockSetupWizard : EditorWindow
    {
        [MenuItem("FRG Version Control/Setup Wizard", priority = 1)]
        public static void ShowWizard()
        {
            var window = GetWindow<FileLockSetupWizard>("File Locking Setup");
            window.minSize = new Vector2(540, 440);
            window.Show();
        }

        private Vector2 m_ScrollPosition;
        [SerializeField] private string m_StatusMessage = "";
        [SerializeField] private bool m_SetupComplete;
        [SerializeField] private bool m_HasAutoRun;
        [SerializeField] private bool m_ShowSignIn;
        [SerializeField] private bool m_ShowForm;
        [SerializeField] private string m_SignInNote = "";
        [SerializeField] private string m_SignInError = "";
        [SerializeField] private string m_Username = "";
        [SerializeField] private string m_CurrentUsername = "";
        [SerializeField] private SvnLogin.SavedLogin[] m_SavedLogins = new SvnLogin.SavedLogin[0];
        [NonSerialized] private string m_Password = "";
        private bool m_Running;
        private bool m_SigningIn;

        private void OnEnable()
        {
            // Run automatically the first time the window opens (not again after a script reload).
            if (!m_HasAutoRun)
            {
                m_HasAutoRun = true;
                EditorApplication.delayCall += () => { if (this == null) return; RunSetup(); Repaint(); };
            }
        }

        private void OnDisable()
        {
            m_Password = "";
        }

        private void OnGUI()
        {
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);

            GUILayout.Label("File Locking System Setup (SVN backend)", EditorStyles.boldLabel);
            GUILayout.Space(10);

            GUILayout.Label("This wizard checks that this workstation is ready for SVN file locking and signs this " +
                            "project in to SVN. It runs when the window opens; click below to run it again after fixing something.",
                            EditorStyles.wordWrappedLabel);
            GUILayout.Space(5);

            GUILayout.Label("1. svn command-line client is installed (TortoiseSVN with command line client tools)", EditorStyles.wordWrappedLabel);
            GUILayout.Label("2. The project folder is in an SVN checkout", EditorStyles.wordWrappedLabel);
            GUILayout.Label("3. You are signed in to SVN for this project", EditorStyles.wordWrappedLabel);
            GUILayout.Label("4. The SVN server accepts your login", EditorStyles.wordWrappedLabel);
            GUILayout.Label("5. Optional: TortoiseSVN auto-props for svn:needs-lock", EditorStyles.wordWrappedLabel);

            GUILayout.Space(10);

            using (new EditorGUI.DisabledScope(m_Running || m_SigningIn))
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

            if (m_ShowSignIn) DrawSignIn();

            GUILayout.Space(10);
            using (new EditorGUI.DisabledScope(m_Running || m_SigningIn))
            {
                if (GUILayout.Button(new GUIContent("Report a Bug...", "Saves a bug report that includes these results, for you to send to " + BugReport.Recipient + "."),
                                     GUILayout.Height(22)))
                {
                    var log = m_StatusMessage;
                    EditorApplication.delayCall += () => BugReportWindow.Open("the Setup Wizard", log);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSignIn()
        {
            GUILayout.Space(8);
            EditorGUILayout.LabelField("Sign in to SVN", EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(m_SignInNote)) EditorGUILayout.HelpBox(m_SignInNote, MessageType.Info);

            using (new EditorGUI.DisabledScope(m_Running || m_SigningIn))
            {
                var saved = m_SavedLogins ?? new SvnLogin.SavedLogin[0];
                foreach (var login in saved)
                {
                    var server = SvnLogin.ServerName(login.realm);
                    if (GUILayout.Button(new GUIContent($"Log in as {login.username}",
                                                        $"Uses the login TortoiseSVN saved for {server}. Your password is not shown or typed."),
                                         GUILayout.Height(28)))
                    {
                        var chosen = login;
                        EditorApplication.delayCall += () => Adopt(chosen);
                    }
                    EditorGUILayout.LabelField($"Saved by TortoiseSVN for {server}", EditorStyles.centeredGreyMiniLabel);
                }

                if (saved.Length > 0 && !m_ShowForm)
                {
                    GUILayout.Space(4);
                    if (GUILayout.Button("Use a different account", GUILayout.Height(22))) m_ShowForm = true;
                }

                if (m_ShowForm || saved.Length == 0)
                {
                    GUILayout.Space(6);
                    m_Username = EditorGUILayout.TextField("Username", m_Username);
                    m_Password = EditorGUILayout.PasswordField("Password", m_Password);
                    EditorGUILayout.LabelField("Use the same username and password as in TortoiseSVN. The password is saved " +
                                               "encrypted for this project only.", EditorStyles.wordWrappedMiniLabel);
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_Username) || string.IsNullOrEmpty(m_Password)))
                    {
                        if (GUILayout.Button("Sign In", GUILayout.Height(26))) EditorApplication.delayCall += SignIn;
                    }
                }
            }

            if (m_SigningIn) EditorGUILayout.HelpBox("Checking your login with the SVN server...", MessageType.None);
            if (!string.IsNullOrEmpty(m_SignInError)) EditorGUILayout.HelpBox(m_SignInError, MessageType.Warning);
        }

        private void OfferSignIn(SvnLogin.Status status, string note, string prefillUsername)
        {
            m_ShowSignIn = true;
            m_SignInNote = note;
            m_SignInError = "";
            m_SavedLogins = status.savedLogins ?? new SvnLogin.SavedLogin[0];
            m_ShowForm = m_SavedLogins.Length == 0;
            if (!string.IsNullOrEmpty(prefillUsername)) m_Username = prefillUsername;
            else if (string.IsNullOrEmpty(m_Username) && m_SavedLogins.Length > 0) m_Username = m_SavedLogins[0].username;
        }

        private async void Adopt(SvnLogin.SavedLogin login)
        {
            if (this == null || m_SigningIn || !ConfirmAccountChange(login.username)) return;
            m_SigningIn = true;
            m_SignInError = "";
            Repaint();
            try
            {
                var outcome = await SvnLogin.AdoptAsync(login.id).ConfigureAwait(true);
                if (outcome.signedIn)
                {
                    OnSignedIn();
                    return;
                }
                m_ShowForm = true;
                m_Username = login.username;
                m_SignInError = SetupWizardText.SignInFailure(outcome.kind, outcome.message, outcome.defaultAction);
            }
            finally
            {
                m_SigningIn = false;
                if (this != null) Repaint();
            }
        }

        private async void SignIn()
        {
            if (this == null || m_SigningIn) return;
            var username = (m_Username ?? "").Trim();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(m_Password) || !ConfirmAccountChange(username)) return;
            var password = m_Password;
            m_Password = "";
            GUI.FocusControl(null);
            m_SigningIn = true;
            m_SignInError = "";
            Repaint();
            try
            {
                var outcome = await SvnLogin.SignInAsync(username, password).ConfigureAwait(true);
                password = null;
                if (outcome.signedIn)
                {
                    OnSignedIn();
                    return;
                }
                m_SignInError = SetupWizardText.SignInFailure(outcome.kind, outcome.message, outcome.defaultAction);
            }
            finally
            {
                password = null;
                m_SigningIn = false;
                if (this != null) Repaint();
            }
        }

        private bool ConfirmAccountChange(string newUsername)
        {
            if (string.IsNullOrEmpty(m_CurrentUsername) || string.Equals(m_CurrentUsername, newUsername, StringComparison.Ordinal)) return true;
            return EditorUtility.DisplayDialog("Switch SVN account?",
                $"This project is signed in as {m_CurrentUsername}. Signing in as {newUsername} makes Unity work as {newUsername}; " +
                $"files locked by {m_CurrentUsername} stay locked by {m_CurrentUsername}.",
                "Switch account", "Cancel");
        }

        private void OnSignedIn()
        {
            m_ShowSignIn = false;
            m_ShowForm = false;
            m_SignInError = "";
            m_Password = "";
            FileLockManager.Instance.ReloadIdentity();
            if (this != null) RunSetup();
        }

        private void Append(string text)
        {
            m_StatusMessage += text;
            if (this != null) Repaint();
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
                if (this != null) Repaint();
            }
        }

        private async Task RunSetupSteps()
        {
            m_StatusMessage = "Running setup...\n";
            m_SetupComplete = false;
            m_ShowSignIn = false;
            m_SignInError = "";
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
            var svnPath = version.Ok ? IpcJson.ReadScalar(version.RawLine, "svnPath", inResult: true) : null;
            if (string.IsNullOrEmpty(svnVersion) || svnVersion == "not-found")
            {
                Append(string.IsNullOrEmpty(svnPath)
                    ? "\n[FAIL] svn.exe was not found. Install TortoiseSVN with the 'command line client tools' option, then restart the editor."
                    : $"\n[FAIL] {svnPath} did not report its version. Reinstall TortoiseSVN with the 'command line client tools' option, then restart the editor.");
                return;
            }
            Append($"\n[OK] svn {svnVersion}" + (string.IsNullOrEmpty(svnPath) ? "" : $" ({svnPath})"));

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

            // Step 3: this project's SVN login.
            Append("\n\n3. Checking your SVN login for this project...");
            var login = await SvnLogin.FetchStatusAsync().ConfigureAwait(true);
            if (login.status == null)
            {
                Append(login.busy ? "\n[WAIT] SVN is busy right now. Run the wizard again in a moment." : "\n[FAIL] " + login.error);
                return;
            }
            var status = login.status;
            if (!status.usesPasswordCache)
            {
                m_CurrentUsername = "";
                Append("\n[NOTE] This repository's URL does not use SVN passwords" +
                       (string.IsNullOrEmpty(status.localUsername) ? "." : $", so svn records your Windows user name ({status.localUsername})."));
            }
            else if (status.ProjectUsername != null)
            {
                m_CurrentUsername = status.ProjectUsername;
                Append($"\n[OK] Signed in as {status.ProjectUsername}");
            }
            else
            {
                m_CurrentUsername = "";
                OfferSignIn(status, SetupWizardText.SignInNote(status.savedLogins?.Length ?? 0), null);
                Append("\n[ACTION] You are not signed in to SVN for this project yet. Sign in below; the wizard continues by itself.");
                return;
            }

            // Step 4: the server answers for this project's URL with that login.
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
                var kind = IpcJson.ReadScalar(probe.RawLine, "kind", inResult: true);
                if (kind == "auth_failed" && status.usesPasswordCache)
                {
                    OfferSignIn(status, "The SVN server did not accept the login saved for this project (the password may have changed). Sign in again. If your login is right, ask your SVN admin for access to this repository.",
                                status.ProjectUsername);
                    Append("\n[ACTION] The SVN server did not accept your saved login. Sign in again below.");
                    return;
                }
                var message = IpcJson.ReadScalar(probe.RawLine, "message", inResult: true);
                var action = IpcJson.ReadScalar(probe.RawLine, "defaultAction", inResult: true);
                Append($"\n[FAIL] The SVN server did not accept the request: {SetupWizardText.LastLine(message)}\n   {action}");
                return;
            }
            client.ClearLoginRejected();
            FileLockManager.Instance.ApplyLoginStatus(status);
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
            if (this != null) Repaint();
        }
    }

    /// <summary>Pure text helpers for the Setup Wizard (unit-tested).</summary>
    public static class SetupWizardText
    {
        public static string SignInNote(int savedLoginCount) => savedLoginCount > 0
            ? "TortoiseSVN already has a login saved for this server. If the name below is yours, click it; otherwise use a different account."
            : "Sign in with the username and password you use in TortoiseSVN.";

        /// <summary>The sentence shown when a sign-in fails: a rejected login gets only its action
        /// sentence (svn's own text starts with a misleading "Unable to connect"); anything else gets
        /// svn's most specific line, then the action.</summary>
        public static string SignInFailure(string kind, string message, string action)
        {
            if (kind == "auth_failed" && !string.IsNullOrEmpty(action)) return action;
            var detail = LastLine(message);
            if (string.IsNullOrEmpty(detail)) return action ?? "";
            return string.IsNullOrEmpty(action) ? detail : detail + "\n" + action;
        }

        /// <summary>The last non-empty line: svn prints the most specific cause last.</summary>
        public static string LastLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length > 0) return line;
            }
            return "";
        }

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
