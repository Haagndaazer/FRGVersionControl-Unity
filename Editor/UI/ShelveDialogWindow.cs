using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Modal dialog for creating a shelf. Pre-populates with the regular-row selection
    /// from the FRG window (if any) and lists every eligible modified file as a checklist.
    /// </summary>
    public class ShelveDialogWindow : EditorWindow
    {
        private FrgVersionControlWindow m_Owner;
        private List<string> m_Eligible;
        private HashSet<string> m_Selected;
        private string m_Name = "";
        private string m_LogMessage = "";
        private Vector2 m_Scroll;
        private bool m_FocusedNameField = false;

        public static void Show(FrgVersionControlWindow owner, List<string> eligible, List<string> initialSelected)
        {
            var w = CreateInstance<ShelveDialogWindow>();
            w.m_Owner = owner;
            w.m_Eligible = eligible ?? new List<string>();
            w.m_Selected = new HashSet<string>(initialSelected ?? w.m_Eligible);
            w.titleContent = new GUIContent("Shelve");
            w.minSize = new Vector2(520, 360);
            w.maxSize = new Vector2(900, 720);
            w.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("Name:", EditorStyles.boldLabel);
            GUI.SetNextControlName("FrgShelveName");
            m_Name = EditorGUILayout.TextField(m_Name);
            if (!m_FocusedNameField)
            {
                EditorGUI.FocusTextInControl("FrgShelveName");
                m_FocusedNameField = true;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Log message (optional):", EditorStyles.boldLabel);
            m_LogMessage = EditorGUILayout.TextArea(m_LogMessage, GUILayout.MinHeight(48), GUILayout.MaxHeight(96));

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField($"Files to shelve ({m_Selected.Count}/{m_Eligible.Count}):", EditorStyles.boldLabel);

            // Toggle-all bar.
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            bool allOn = m_Eligible.Count > 0 && m_Selected.Count == m_Eligible.Count;
            bool newAll = EditorGUILayout.ToggleLeft($"All ({m_Selected.Count}/{m_Eligible.Count})", allOn, GUILayout.Width(120));
            if (newAll != allOn)
            {
                if (newAll) foreach (var p in m_Eligible) m_Selected.Add(p);
                else m_Selected.Clear();
            }
            EditorGUILayout.EndHorizontal();

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, EditorStyles.helpBox);
            foreach (var p in m_Eligible)
            {
                bool was = m_Selected.Contains(p);
                bool now = EditorGUILayout.ToggleLeft(p, was);
                if (now != was)
                {
                    if (now) m_Selected.Add(p);
                    else m_Selected.Remove(p);
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Cancel", GUILayout.Width(90)))
            {
                Close();
            }
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_Name) || m_Selected.Count == 0))
            {
                if (GUILayout.Button($"Create Shelf ({m_Selected.Count})", GUILayout.Width(180)))
                {
                    var paths = m_Selected.ToArray();
                    var owner = m_Owner;
                    var name = m_Name.Trim();
                    var log = m_LogMessage;
                    Close();
                    if (owner != null)
                    {
                        _ = owner.DoCreateShelfAsync(name, log, paths);
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
