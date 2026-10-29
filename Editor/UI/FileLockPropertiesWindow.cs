using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.OverrideMode;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Edit SVN Properties: svn:needs-lock (files), svn:ignore (folders), the other properties on
    /// the path (read-only) and a one-off custom property, through the core's prop.* ops
    /// (SVN_CORE_API.md §5.13). IPC runs off the main thread; results are published to the model
    /// on the main thread, and a result from a superseded operation is dropped.
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
            var w = GetWindow<FileLockPropertiesWindow>(true, $"SVN Properties: {Path.GetFileName(path)}");
            w.minSize = new Vector2(520, 360);
            w.Init(path);
            w.Show();
        }

        [SerializeField] private string m_Path;
        [SerializeField] private bool m_IsDirectory;
        [SerializeField] private string m_CustomKey = "";
        [SerializeField] private string m_CustomValue = "";

        private SvnPropertiesModel m_Model;
        private string m_StatusBanner = "";
        private bool m_StatusIsError;
        private bool m_Busy;
        private int m_Generation;
        private Vector2 m_OtherScroll;

        private void Init(string path)
        {
            if (m_Model != null && m_Model.Loaded && m_Model.HasPendingChanges && path != m_Path &&
                !EditorUtility.DisplayDialog("Discard unapplied changes?",
                    $"You have unapplied property changes on {Path.GetFileName(m_Path)}. Open {Path.GetFileName(path)} and discard them?",
                    "Discard", "Keep editing"))
            {
                return;
            }
            m_Path = path;
            string fullPath = Path.IsPathRooted(path) ? path : Path.Combine(SvnCommandRunner.ProjectRoot, path);
            m_IsDirectory = Directory.Exists(fullPath);
            titleContent = new GUIContent($"SVN Properties: {Path.GetFileName(path)}");
            Reload(null);
        }

        private void OnEnable()
        {
            if (!string.IsNullOrEmpty(m_Path) && m_Model == null) Reload(null);
        }

        private void Reload(string bannerAfter)
        {
            var model = new SvnPropertiesModel(m_Path, m_IsDirectory);
            var path = m_Path;
            RunAsync("Loading properties…", () => SvnPropertiesModel.FetchAsync(IpcPropertyOps.Instance, path), fetched =>
            {
                model.Publish(fetched);
                m_Model = model;
                if (model.LoadError != null) SetBanner("Error reading properties: " + model.LoadError, true);
                else SetBanner(bannerAfter ?? "", false);
            });
        }

        private void RunAsync<T>(string pendingBanner, Func<Task<T>> work, Action<T> onDone)
        {
            int generation = ++m_Generation;
            m_Busy = true;
            SetBanner(pendingBanner, false);
            Task.Run(work).ContinueWith(t =>
            {
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    if (this == null || generation != m_Generation) return;
                    m_Busy = false;
                    if (t.IsFaulted) SetBanner("Error: " + t.Exception?.GetBaseException().Message + " Click Reload to try again.", true);
                    else onDone(t.Result);
                    Repaint();
                }, null);
            });
        }

        private void SetBanner(string text, bool isError)
        {
            m_StatusBanner = text ?? "";
            m_StatusIsError = isError;
        }

        private bool IsUnderOverride()
        {
            var mgr = FileLockOverrideManager.Instance;
            return mgr != null && mgr.IsInitialized && mgr.IsInOverrideMode(m_Path);
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(m_Path))
            {
                EditorGUILayout.HelpBox("No asset selected. Right-click an asset and choose FRG Version Control > Edit SVN Properties.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"Path: {m_Path}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Type: {(m_IsDirectory ? "folder" : "file")}", EditorStyles.miniLabel);
            EditorGUILayout.Space();

            var model = m_Model;
            bool editable = model != null && model.Loaded && !m_Busy && model.Path == m_Path;
            bool pending = editable && model.HasPendingChanges;

            using (new EditorGUI.DisabledScope(!editable))
            {
                if (!m_IsDirectory)
                {
                    EditorGUILayout.LabelField(SvnPropertiesModel.NeedsLock, EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox(
                        "When set, the file becomes read-only on disk once the change is committed, and stays " +
                        "read-only until someone locks it, so two people cannot edit it at once. Almost always " +
                        "wanted on binary art assets.",
                        MessageType.None);
                    bool overridden = IsUnderOverride();
                    using (new EditorGUI.DisabledScope(overridden))
                    {
                        bool shown = editable && model.NeedsLockDesired;
                        EditorGUI.BeginChangeCheck();
                        bool toggled = EditorGUILayout.Toggle(
                            new GUIContent("Property set", "Whether svn:needs-lock is set on this file."), shown);
                        if (EditorGUI.EndChangeCheck() && editable) model.NeedsLockDesired = toggled;
                    }
                    if (overridden)
                    {
                        EditorGUILayout.HelpBox("svn:needs-lock is managed by the active override on this file. End the override before changing it.", MessageType.Warning);
                    }
                    EditorGUILayout.Space();
                }
                else
                {
                    EditorGUILayout.LabelField(SvnPropertiesModel.Ignore, EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox(
                        "One pattern per line. Matching files in this folder are left out of the Changes tab and are not added to SVN. " +
                        "Common entries: Library, Temp, *.tmp, *.user. Leave it empty to remove the property.",
                        MessageType.None);
                    string shown = editable ? model.IgnoreDesired ?? "" : "";
                    EditorGUI.BeginChangeCheck();
                    string typed = EditorGUILayout.TextArea(shown, GUILayout.MinHeight(80));
                    if (EditorGUI.EndChangeCheck() && editable) model.IgnoreDesired = typed;
                    EditorGUILayout.Space();
                }
            }

            if (editable && model.OtherProperties.Count > 0)
            {
                EditorGUILayout.LabelField("Other properties on this path", EditorStyles.boldLabel);
                m_OtherScroll = EditorGUILayout.BeginScrollView(m_OtherScroll, GUILayout.MaxHeight(90));
                foreach (var kv in model.OtherProperties)
                {
                    EditorGUILayout.LabelField(kv.Key, SvnPropertiesModel.Preview(kv.Value), EditorStyles.miniLabel);
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.Space();
            }

            EditorGUILayout.LabelField("Custom property (one-off)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("For properties beyond svn:needs-lock and svn:ignore. Use sparingly.", MessageType.None);
            using (new EditorGUI.DisabledScope(!editable))
            {
                EditorGUILayout.BeginHorizontal();
                m_CustomKey = EditorGUILayout.TextField(new GUIContent("Key"), m_CustomKey);
                m_CustomValue = EditorGUILayout.TextField(new GUIContent("Value"), m_CustomValue);
                EditorGUILayout.EndHorizontal();
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_CustomKey) || pending))
                {
                    var tip = pending
                        ? "Apply or reload your other changes first."
                        : "Sets the property on this path. Commit to share it.";
                    if (GUILayout.Button(new GUIContent("Set custom property", tip)))
                    {
                        SetCustomProperty();
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(m_Busy))
            {
                if (GUILayout.Button(new GUIContent("Reload", "Re-read the SVN properties from the working copy"),
                                     GUILayout.Width(100), GUILayout.Height(28)))
                {
                    if (!pending || EditorUtility.DisplayDialog("Discard unapplied changes?",
                            "Reloading discards the property changes you have not applied yet.", "Discard", "Keep editing"))
                    {
                        Reload("Reloaded.");
                    }
                }
            }
            using (new EditorGUI.DisabledScope(!pending))
            {
                if (GUILayout.Button(new GUIContent("Apply Changes", "Write the changed properties to the working copy"),
                                     GUILayout.Width(140), GUILayout.Height(28)))
                {
                    ApplyChanges();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(m_StatusBanner))
            {
                EditorGUILayout.HelpBox(m_StatusBanner, m_StatusIsError ? MessageType.Error : MessageType.Info);
            }
        }

        private string CommitHint(string path) =>
            $"Property changes are local until you commit {Path.GetFileName(path)}. Commit it from the Changes tab.";

        private void ApplyChanges()
        {
            var model = m_Model;
            var path = model.Path;
            var isDirectory = model.IsDirectory;
            List<PropChange> plan = model.PlanChanges();
            RunAsync("Applying…", async () =>
            {
                var written = await SvnPropertiesModel.WriteAsync(IpcPropertyOps.Instance, path, plan);
                var fetched = await SvnPropertiesModel.FetchAsync(IpcPropertyOps.Instance, path);
                return (written, fetched);
            }, outcome =>
            {
                var reloaded = new SvnPropertiesModel(path, isDirectory);
                reloaded.Publish(outcome.fetched);
                m_Model = reloaded;
                _ = SvnStatusCache.RefreshAsync();
                var result = outcome.written;
                if (result.Success)
                {
                    SetBanner("Changes applied. " + CommitHint(path), false);
                }
                else
                {
                    var landed = result.Applied.Count > 0 ? $" Applied before the failure: {string.Join(", ", result.Applied)}." : "";
                    SetBanner($"Error: could not {result.Failed}: {result.Error}{landed}", true);
                }
                if (reloaded.LoadError != null) SetBanner(m_StatusBanner + " Reload failed: " + reloaded.LoadError, true);
            });
        }

        private void SetCustomProperty()
        {
            var path = m_Path;
            var key = m_CustomKey.Trim();
            var value = m_CustomValue ?? "";
            RunAsync($"Setting {key}…", () => IpcPropertyOps.Instance.Set(path, key, value), r =>
            {
                if (r.success)
                {
                    m_CustomKey = "";
                    m_CustomValue = "";
                    _ = SvnStatusCache.RefreshAsync();
                    Reload($"Set {key}. " + CommitHint(path));
                }
                else
                {
                    SetBanner($"Error setting {key}: {r.error}", true);
                }
            });
        }
    }
}
