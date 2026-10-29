using System.IO;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Phase 4 Step 5: lightweight SVN properties editor.
    ///
    /// View / set common SVN properties on a selected asset:
    /// - svn:needs-lock (checkbox: present or absent)
    /// - svn:ignore (text area; for folders only)
    /// - Custom property (one-off key/value)
    ///
    /// Read via `svn proplist -v`; write via `svn propset` / `svn propdel`. Both invoke the
    /// existing SvnCommandRunner.Run pipeline (config-dir + retry + cert flags).
    ///
    /// Power-user surface; not on the daily-driver loop. No async wrappers necessary —
    /// proplist on a single file is fast (&lt; 100ms typical), and the dialog is modal.
    /// </summary>
    public class FileLockPropertiesWindow : EditorWindow
    {
        [MenuItem("Assets/FRG Version Control/Edit SVN Properties", false, 2020)]
        private static void OpenForSelection()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(path)) return;
            Open(path);
        }

        [MenuItem("Assets/FRG Version Control/Edit SVN Properties", true)]
        private static bool ValidateOpenForSelection()
        {
            return Selection.activeObject != null
                   && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(Selection.activeObject));
        }

        public static void Open(string path)
        {
            var w = GetWindow<FileLockPropertiesWindow>(true,
                $"SVN Properties: {Path.GetFileName(path)}");
            w.minSize = new Vector2(520, 320);
            w.Init(path);
            w.Show();
        }

        private string m_Path;
        private bool m_NeedsLockCurrent;
        private bool m_NeedsLockNew;
        private string m_IgnoreCurrent = "";
        private string m_IgnoreNew = "";
        private string m_CustomKey = "";
        private string m_CustomValue = "";
        private string m_StatusBanner = "";
        private bool m_IsDirectory;

        private void Init(string path)
        {
            m_Path = path;
            string fullPath = Path.IsPathRooted(path) ? path : Path.Combine(SvnCommandRunner.ProjectRoot, path);
            m_IsDirectory = Directory.Exists(fullPath);
            ReloadProperties();
        }

        private void ReloadProperties()
        {
            // svn:needs-lock — propget; non-zero exit (W200017) means not set
            var needsLockResult = SvnCommandRunner.Run($"propget svn:needs-lock \"{m_Path}\"");
            m_NeedsLockCurrent = needsLockResult.success
                                 && !string.IsNullOrEmpty(needsLockResult.output);
            m_NeedsLockNew = m_NeedsLockCurrent;

            if (m_IsDirectory)
            {
                var ignoreResult = SvnCommandRunner.Run($"propget svn:ignore \"{m_Path}\"");
                m_IgnoreCurrent = ignoreResult.success ? (ignoreResult.output ?? "") : "";
                m_IgnoreNew = m_IgnoreCurrent;
            }
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(m_Path))
            {
                EditorGUILayout.HelpBox("No asset selected.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"Path: {m_Path}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Type: {(m_IsDirectory ? "directory" : "file")}", EditorStyles.miniLabel);

            EditorGUILayout.Space();

            // svn:needs-lock
            EditorGUILayout.LabelField("svn:needs-lock", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "When set, the file is read-only on disk by default. `svn lock` makes it writable + " +
                "registers a server-side lock. Required for the Ignore-on-Commit workflow's safety property. " +
                "Almost always wanted on binary art assets.",
                MessageType.None);
            m_NeedsLockNew = EditorGUILayout.Toggle(
                new GUIContent("Property set",
                               "Whether svn:needs-lock=* is currently set on this file."),
                m_NeedsLockNew);

            EditorGUILayout.Space();

            // svn:ignore (folders only)
            if (m_IsDirectory)
            {
                EditorGUILayout.LabelField("svn:ignore", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "One pattern per line; matches are excluded from `svn status`, `svn add`, etc. " +
                    "Common entries: Library, Temp, *.tmp, *.user, ~$*",
                    MessageType.None);
                m_IgnoreNew = EditorGUILayout.TextArea(m_IgnoreNew, GUILayout.MinHeight(80));
                EditorGUILayout.Space();
            }

            // Custom prop
            EditorGUILayout.LabelField("Custom property (one-off)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "For ad-hoc properties beyond svn:needs-lock and svn:ignore. Use sparingly.",
                MessageType.None);
            EditorGUILayout.BeginHorizontal();
            m_CustomKey = EditorGUILayout.TextField(new GUIContent("Key"), m_CustomKey);
            m_CustomValue = EditorGUILayout.TextField(new GUIContent("Value"), m_CustomValue);
            EditorGUILayout.EndHorizontal();
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(m_CustomKey)))
            {
                if (GUILayout.Button(new GUIContent("Set custom property",
                                                    "Runs `svn propset <key> <value> <path>`")))
                {
                    SetCustomProperty();
                }
            }

            EditorGUILayout.Space();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("Reload",
                                                "Re-read the current SVN properties from the working copy"),
                                 GUILayout.Width(100), GUILayout.Height(28)))
            {
                ReloadProperties();
                m_StatusBanner = "Reloaded.";
            }
            using (new EditorGUI.DisabledScope(!HasPendingChanges()))
            {
                if (GUILayout.Button(new GUIContent("Apply Changes",
                                                    "Apply property additions/removals/changes via svn propset/propdel"),
                                     GUILayout.Width(140), GUILayout.Height(28)))
                {
                    ApplyChanges();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(m_StatusBanner))
            {
                EditorGUILayout.HelpBox(m_StatusBanner,
                    m_StatusBanner.StartsWith("Error", System.StringComparison.OrdinalIgnoreCase)
                        ? MessageType.Error : MessageType.Info);
            }
        }

        private bool HasPendingChanges()
        {
            if (m_NeedsLockNew != m_NeedsLockCurrent) return true;
            if (m_IsDirectory && m_IgnoreNew != m_IgnoreCurrent) return true;
            return false;
        }

        private void ApplyChanges()
        {
            if (m_NeedsLockNew != m_NeedsLockCurrent)
            {
                if (m_NeedsLockNew)
                {
                    string tmp = WriteTempPropFile("*");
                    var r = SvnCommandRunner.Run($"propset svn:needs-lock --file=\"{tmp}\" \"{m_Path}\"");
                    File.Delete(tmp);
                    if (!r.success)
                    {
                        m_StatusBanner = $"Error setting svn:needs-lock: {r.error?.Split('\n')[0]}. " +
                                          "Check Console for full output.";
                        return;
                    }
                }
                else
                {
                    var r = SvnCommandRunner.Run($"propdel svn:needs-lock \"{m_Path}\"");
                    if (!r.success)
                    {
                        m_StatusBanner = $"Error removing svn:needs-lock: {r.error?.Split('\n')[0]}. " +
                                          "Check Console for full output.";
                        return;
                    }
                }
            }

            if (m_IsDirectory && m_IgnoreNew != m_IgnoreCurrent)
            {
                if (string.IsNullOrEmpty(m_IgnoreNew.Trim()))
                {
                    var r = SvnCommandRunner.Run($"propdel svn:ignore \"{m_Path}\"");
                    if (!r.success)
                    {
                        m_StatusBanner = $"Error removing svn:ignore: {r.error?.Split('\n')[0]}.";
                        return;
                    }
                }
                else
                {
                    string tmp = WriteTempPropFile(m_IgnoreNew);
                    var r = SvnCommandRunner.Run($"propset svn:ignore --file=\"{tmp}\" \"{m_Path}\"");
                    File.Delete(tmp);
                    if (!r.success)
                    {
                        m_StatusBanner = $"Error setting svn:ignore: {r.error?.Split('\n')[0]}.";
                        return;
                    }
                }
            }

            m_StatusBanner = "Changes applied.";
            ReloadProperties();
            // Refresh the status cache so the panel reflects the property changes
            _ = FRGVersionControl.Core.SvnStatusCache.RefreshAsync();
        }

        private void SetCustomProperty()
        {
            string tmp = WriteTempPropFile(m_CustomValue ?? "");
            var r = SvnCommandRunner.Run($"propset {m_CustomKey} --file=\"{tmp}\" \"{m_Path}\"");
            File.Delete(tmp);
            if (!r.success)
            {
                m_StatusBanner = $"Error setting {m_CustomKey}: {r.error?.Split('\n')[0]}. " +
                                  "Check Console.";
            }
            else
            {
                m_StatusBanner = $"Set {m_CustomKey}={m_CustomValue}";
                m_CustomKey = "";
                m_CustomValue = "";
            }
        }

        /// <summary>Write a value to a temp file and return its path. Used to avoid shell-glob
        /// expansion of the value (e.g., '*' for svn:needs-lock would shell-glob without --file).</summary>
        private static string WriteTempPropFile(string content)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"FRGVersionControl-prop-{System.Guid.NewGuid():N}.txt");
            File.WriteAllText(tmp, content);
            return tmp;
        }
    }
}
