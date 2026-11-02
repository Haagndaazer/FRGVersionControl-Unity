using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core.BugReports;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.Services;
using FRGVersionControl.Tools;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Report a Bug: the artist describes the problem, reviews the whole report (editable), and
    /// saves it to .svn-tooling-config/bug-reports/ with a copy on the clipboard. Nothing is sent
    /// over the network.
    /// </summary>
    public class BugReportWindow : EditorWindow
    {
        private enum Page { Describe, Review, Saved }

        [MenuItem("FRG Version Control/Report a Bug...", priority = 20)]
        public static void OpenFromMenu() => Open("the FRG Version Control menu", null);

        public static void Open(string entryPoint, string wizardLog)
        {
            var window = GetWindow<BugReportWindow>(false, "Report a Bug", true);
            window.minSize = new Vector2(560, 480);
            window.Begin(entryPoint, wizardLog);
            window.Show();
        }

        [SerializeField] private Page m_Page;
        [SerializeField] private string m_EntryPoint = "the FRG Version Control menu";
        [SerializeField] private string m_Title = "";
        [SerializeField] private string m_What = "";
        [SerializeField] private string m_Expected = "";
        [SerializeField] private string m_Steps = "";
        [SerializeField] private string m_WizardLog = "";
        [SerializeField] private bool m_IncludeDetails = true;
        [SerializeField] private bool m_IncludeActivity = true;
        [SerializeField] private bool m_IncludeConsole = true;
        [SerializeField] private bool m_IncludeWizard = true;
        [SerializeField] private bool m_Collected;
        [SerializeField] private string m_Reporter = "";
        [SerializeField] private string m_DetailsText = "";
        [SerializeField] private string m_ActivityText = "";
        [SerializeField] private string m_ConsoleText = "";
        [SerializeField] private List<BugReport.Section> m_Sections = new List<BugReport.Section>();
        [SerializeField] private bool m_Edited;
        [SerializeField] private string m_Notice = "";
        [SerializeField] private string m_SavedPath = "";
        [SerializeField] private string m_SavedText = "";
        [SerializeField] private string m_SaveError = "";
        [SerializeField] private string m_CollectStatus = "";
        private bool m_Collecting;
        private double m_CollectStartedAt;
        private int m_Length = -1;
        private Vector2 m_Scroll;
        private const double CollectTimeoutSeconds = 20;
        private static GUIStyle s_Wrapped;

        private static GUIStyle Wrapped => s_Wrapped ?? (s_Wrapped = new GUIStyle(EditorStyles.textArea) { wordWrap = true });

        private void Begin(string entryPoint, string wizardLog)
        {
            if (m_Page == Page.Saved) ResetDraft();
            if (m_Page == Page.Review)
            {
                if (m_Edited) return;
                m_Page = Page.Describe;
                m_Notice = "";
            }
            m_EntryPoint = string.IsNullOrEmpty(entryPoint) ? "the FRG Version Control menu" : entryPoint;
            m_WizardLog = wizardLog ?? "";
            Collect();
        }

        private void OnEnable()
        {
            if (!m_Collected && !m_Collecting && m_Page == Page.Describe) EditorApplication.delayCall += () => { if (this != null && !m_Collected && !m_Collecting) Collect(); };
        }

        private void OnDisable()
        {
            EditorApplication.update -= WatchCollect;
        }

        private void ResetDraft()
        {
            m_Page = Page.Describe;
            m_Title = m_What = m_Expected = m_Steps = m_WizardLog = "";
            m_IncludeDetails = m_IncludeActivity = m_IncludeConsole = m_IncludeWizard = true;
            m_Collected = false;
            m_CollectStatus = "";
            m_Sections = new List<BugReport.Section>();
            m_Edited = false;
            m_Notice = m_SavedPath = m_SavedText = m_SaveError = "";
        }

        private void Collect()
        {
            if (m_Collecting) return;
            m_Collecting = true;
            m_Collected = false;
            m_CollectStartedAt = EditorApplication.timeSinceStartup;
            m_CollectStatus = "Collecting details...";
            EditorApplication.update -= WatchCollect;
            EditorApplication.update += WatchCollect;
            m_Reporter = Environment.UserName + " (Windows user)";
            BugReportDiagnostics.EditorFacts facts;
            try
            {
                facts = BugReportDiagnostics.ReadEditorFacts();
                m_Reporter = string.IsNullOrEmpty(facts.svnUser) ? facts.windowsUser + " (Windows user; not signed in to SVN)" : facts.svnUser;
            }
            catch (Exception ex)
            {
                Finish("- Not available: the editor details could not be read (" + ex.Message + ").", "Some details could not be collected; the report says which.");
                return;
            }
            var sync = SvnCommandRunner.s_UnityMainSync;
            Task.Run(async () =>
            {
                string details = "- Not available: the details could not be collected.";
                string status = "Some details could not be collected; the report says which.";
                try
                {
                    var core = await BugReportDiagnostics.ReadCoreFactsAsync(facts).ConfigureAwait(false);
                    details = BugReportDiagnostics.FormatDetails(facts, core);
                    status = core.notes.Count == 0 ? "Details ready." : "Details ready. Not everything could be collected: " + core.notes[0];
                }
                catch (Exception ex)
                {
                    details = "- Not available: the details could not be collected (" + ex.Message + ").";
                }
                finally
                {
                    sync?.Post(_ => { if (this != null) Finish(details, status); }, null);
                }
            });
        }

        private void WatchCollect()
        {
            if (this == null || !m_Collecting) { EditorApplication.update -= WatchCollect; return; }
            if (EditorApplication.timeSinceStartup - m_CollectStartedAt < CollectTimeoutSeconds) return;
            Finish("- Not available: the details were still being collected after " + CollectTimeoutSeconds + " seconds.",
                   "Some details could not be collected in time; you can still review and save the report.");
        }

        private void Finish(string details, string status)
        {
            EditorApplication.update -= WatchCollect;
            if (m_Collected && m_Page != Page.Describe) return;
            m_DetailsText = details;
            m_CollectStatus = status;
            m_Collecting = false;
            m_Collected = true;
            Repaint();
        }

        private void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            if (m_Page == Page.Describe) DrawDescribe();
            else if (m_Page == Page.Review) DrawReview();
            else DrawSaved();
            EditorGUILayout.EndScrollView();
        }

        private void DrawDescribe()
        {
            EditorGUILayout.HelpBox(
                $"Describe the problem. The report is saved as a file on this computer and copied to the clipboard; nothing is sent anywhere. You then send it to {BugReport.Recipient}.",
                MessageType.Info);
            GUILayout.Space(4);
            m_Title = BugReport.Cap(EditorGUILayout.TextField(new GUIContent("Title", "One line that sums up the problem."), m_Title), BugReport.TitleMax);
            m_What = Area("What happened?", m_What, 90);
            m_Expected = Area("What did you expect to happen? (optional)", m_Expected, 60);
            m_Steps = Area("Steps to reproduce (optional)", m_Steps, 60);

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Include", EditorStyles.boldLabel);
            m_IncludeDetails = EditorGUILayout.ToggleLeft("Versions and project details", m_IncludeDetails);
            m_IncludeActivity = EditorGUILayout.ToggleLeft("Recent version control activity", m_IncludeActivity);
            m_IncludeConsole = EditorGUILayout.ToggleLeft("Recent FRG Version Control console messages", m_IncludeConsole);
            if (!string.IsNullOrEmpty(m_WizardLog))
                m_IncludeWizard = EditorGUILayout.ToggleLeft("Setup Wizard results", m_IncludeWizard);

            GUILayout.Space(6);
            EditorGUILayout.LabelField(m_CollectStatus ?? "", EditorStyles.wordWrappedMiniLabel);

            bool ready = m_Collected && !string.IsNullOrWhiteSpace(m_Title) && !string.IsNullOrWhiteSpace(m_What);
            using (new EditorGUI.DisabledScope(!ready))
            {
                if (GUILayout.Button(new GUIContent("Review Report", "Shows the whole report so you can check and edit it before saving."), GUILayout.Height(28)))
                    BuildSections();
            }
        }

        private static string Area(string label, string value, float height)
        {
            EditorGUILayout.LabelField(label);
            var text = BugReport.Cap(EditorGUILayout.TextArea(value ?? "", Wrapped, GUILayout.MinHeight(height)), BugReport.DescriptionMax);
            if (text.Length >= BugReport.DescriptionMax)
                EditorGUILayout.LabelField($"Only the first {BugReport.DescriptionMax:N0} characters are kept. Send long logs as a separate file.", EditorStyles.wordWrappedMiniLabel);
            return text;
        }

        private void BuildSections()
        {
            m_ConsoleText = BugReport.TailLines(FrgConsoleCapture.Buffer.Format(), int.MaxValue, BugReport.SectionMax);
            m_ActivityText = BugReportDiagnostics.ReadOpLog(SvnCommandRunner.ToolingConfigDir);
            var list = new List<BugReport.Section>
            {
                new BugReport.Section("Title", null, BugReport.Header(m_Title, DateTime.UtcNow, m_Reporter, m_EntryPoint), false),
                new BugReport.Section("What happened", "What happened", m_What, false),
            };
            if (!string.IsNullOrWhiteSpace(m_Expected)) list.Add(new BugReport.Section("What I expected", "What I expected", m_Expected, false));
            if (!string.IsNullOrWhiteSpace(m_Steps)) list.Add(new BugReport.Section("Steps to reproduce", "Steps to reproduce", m_Steps, false));
            if (m_IncludeDetails) list.Add(new BugReport.Section("Versions and project", "Versions and project", m_DetailsText, false));
            if (m_IncludeWizard && !string.IsNullOrEmpty(m_WizardLog))
                list.Add(new BugReport.Section("Setup Wizard results", "Setup Wizard results", BugReport.TailLines(m_WizardLog, int.MaxValue, BugReport.WizardMax), true));
            if (m_IncludeActivity) list.Add(new BugReport.Section("Recent version control activity", "Recent version control activity", m_ActivityText, true));
            if (m_IncludeConsole) list.Add(new BugReport.Section("Recent FRG Version Control console messages", "Recent FRG Version Control console messages", m_ConsoleText, true));

            m_Notice = BugReport.ScrubSections(list) ? "A password-like value was masked (shown as ***)." : "";
            m_Sections = list;
            m_Edited = false;
            m_Length = -1;
            m_Page = Page.Review;
            m_Scroll = Vector2.zero;
            GUI.FocusControl(null);
        }

        private void DrawReview()
        {
            EditorGUILayout.HelpBox(
                "This is exactly what will be saved. It includes file paths, your SVN and Windows user names, and the project's server address. " +
                "Edit or delete anything you don't want to share. Passwords are never included.",
                MessageType.Info);
            if (!string.IsNullOrEmpty(m_Notice)) EditorGUILayout.HelpBox(m_Notice, MessageType.Warning);

            foreach (var s in m_Sections)
            {
                GUILayout.Space(6);
                EditorGUILayout.LabelField(s.label, EditorStyles.boldLabel);
                var edited = EditorGUILayout.TextArea(s.body ?? "", Wrapped);
                if (edited != s.body) { s.body = edited; m_Edited = true; m_Length = -1; }
            }

            GUILayout.Space(8);
            if (m_Length < 0) m_Length = BugReport.Build(m_Sections).Length;
            EditorGUILayout.LabelField($"{m_Length:N0} characters", EditorStyles.miniLabel);
            if (m_Length > BugReport.ReportTarget)
                EditorGUILayout.HelpBox(
                    $"This is longer than a GitHub issue allows ({BugReport.GitHubIssueLimit:N0} characters); {BugReport.Recipient} may need to attach it as a file.",
                    MessageType.Warning);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Back", GUILayout.Height(26))) EditorApplication.delayCall += Back;
            if (GUILayout.Button("Save Report", GUILayout.Height(26))) EditorApplication.delayCall += Save;
            EditorGUILayout.EndHorizontal();
        }

        private void Back()
        {
            if (this == null) return;
            if (m_Edited && !EditorUtility.DisplayDialog("Report a Bug", "Discard your edits to the report?", "Discard", "Keep Editing")) return;
            m_Page = Page.Describe;
            m_Notice = "";
            m_Scroll = Vector2.zero;
            GUI.FocusControl(null);
            Repaint();
        }

        private void Save()
        {
            if (this == null || m_Page != Page.Review) return;
            if (BugReport.ScrubSections(m_Sections))
            {
                m_Notice = "A password-like value was masked (shown as ***). Check the report, then save again.";
                m_Length = -1;
                GUI.FocusControl(null);
                Repaint();
                return;
            }
            var text = BugReport.Build(m_Sections);
            EditorGUIUtility.systemCopyBuffer = text;
            m_SavedText = text;
            m_SaveError = "";
            m_SavedPath = "";
            try
            {
                var dir = Path.Combine(SvnCommandRunner.ToolingConfigDir, BugReport.FolderName);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, BugReport.FileName(DateTime.Now));
                File.WriteAllText(path, text, new UTF8Encoding(false));
                m_SavedPath = path;
            }
            catch (Exception ex)
            {
                m_SaveError = $"The report could not be saved as a file ({ex.Message}). It is on the clipboard: paste it to {BugReport.Recipient} on Slack.";
            }
            m_Page = Page.Saved;
            GUI.FocusControl(null);
            Repaint();
        }

        private void DrawSaved()
        {
            if (!string.IsNullOrEmpty(m_SaveError))
            {
                EditorGUILayout.HelpBox(m_SaveError, MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(BugReport.SavedMessage, MessageType.Info);
                EditorGUILayout.SelectableLabel(m_SavedPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            GUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(m_SavedPath)))
            {
                if (GUILayout.Button("Show File", GUILayout.Height(26))) EditorUtility.RevealInFinder(m_SavedPath);
            }
            if (GUILayout.Button("Copy Again", GUILayout.Height(26))) EditorGUIUtility.systemCopyBuffer = m_SavedText;
            if (GUILayout.Button("Close", GUILayout.Height(26))) EditorApplication.delayCall += () => { if (this != null) Close(); };
            EditorGUILayout.EndHorizontal();
        }
    }
}
