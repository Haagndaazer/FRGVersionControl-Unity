using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Phase 4 Step 3: per-asset SVN log viewer. Right-click an asset → SVN → Show Log.
    /// Paginated via revision-range emulation (SVN has no `--offset`); designed for the
    /// eventual 11TB-scale repo (never loads all log entries at once).
    ///
    /// Behaviors:
    /// - First load: 50 newest entries. "Load older" fetches the next 50 with upperRev =
    ///   lowestSeenRev - 1; a short page means the file's earliest revision is reached.
    /// - Select an entry → in-editor DiffPane shows it vs your current file (or vs the next-older
    ///   entry); "Open externally" shells out to the configured diff tool as a fallback.
    /// - "Restore to rN" replaces your file with the selected revision via RestoreFlow.
    /// </summary>
    public class FileLockAssetLogWindow : EditorWindow
    {
        private const int PAGE_SIZE = 50;

        [MenuItem("Assets/FRG Version Control/Show History", false, 2006)]
        private static void ShowLogForSelection()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(path)) return;
            Open(path);
        }

        [MenuItem("Assets/FRG Version Control/Show History", true)]
        private static bool ShowLogForSelection_Validate()
        {
            return Selection.activeObject != null
                   && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(Selection.activeObject));
        }

        public static void Open(string assetPath)
        {
            var window = GetWindow<FileLockAssetLogWindow>(true, $"History: {Path.GetFileName(assetPath)}");
            window.minSize = new Vector2(560, 480);
            window.SetTarget(assetPath);
            window.Show();
        }

        private string m_AssetPath;
        private List<SvnLogEntry> m_Entries = new List<SvnLogEntry>();
        private int m_LowestRevSeen = int.MaxValue;
        private bool m_ReachedEarliest = false;
        private int m_SelectedIndex = -1;
        private Vector2 m_Scroll;
        private CancellationTokenSource m_Cts;
        private string m_StatusBanner = "";
        private DiffPane m_Diff;
        private bool m_Restoring;
        private int m_CompareMode;   // 0 = selected revision vs your current file, 1 = vs the next-older entry

        private void OnEnable()
        {
            m_Diff = new DiffPane(Repaint);
        }

        private void SetTarget(string path)
        {
            m_AssetPath = path;
            m_Entries.Clear();
            m_LowestRevSeen = int.MaxValue;
            m_ReachedEarliest = false;
            m_SelectedIndex = -1;
            m_CompareMode = 0;
            m_Diff?.Clear();
            m_StatusBanner = "Loading…";
            _ = LoadPageAsync(/*upperRev*/ -1);
        }

        private void OnDisable()
        {
            if (m_Cts != null) { try { m_Cts.Cancel(); } catch { } m_Cts.Dispose(); m_Cts = null; }
            m_Diff?.Dispose();
            m_Diff = null;
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(m_AssetPath))
            {
                EditorGUILayout.HelpBox("No asset selected.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label(m_AssetPath, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            GUILayout.Label($"Showing {m_Entries.Count} revision(s)" +
                            (m_ReachedEarliest ? " (file's earliest revision reached)" : ""),
                            EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Rev      Author       Date              Message", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < m_Entries.Count; i++)
            {
                var e = m_Entries[i];
                bool isSelected = i == m_SelectedIndex;
                var bg = isSelected ? new Color(0.3f, 0.5f, 0.8f, 0.3f) : new Color(0, 0, 0, 0);
                var rect = EditorGUILayout.BeginHorizontal();
                if (Event.current.type == EventType.Repaint && bg.a > 0)
                {
                    EditorGUI.DrawRect(rect, bg);
                }
                GUILayout.Label($"r{e.revision}", GUILayout.Width(60));
                GUILayout.Label(e.author, GUILayout.Width(100));
                GUILayout.Label(FormatDate(e.date), GUILayout.Width(140));
                if (GUILayout.Button(e.message.Split('\n')[0], EditorStyles.label))
                {
                    m_SelectedIndex = i;
                    m_CompareMode = 0;
                    DriveDiff();
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            // In-editor diff pane: reserve a fixed slab below the list (after the expanding
            // scrollview, so GetRect lands below it), clamped so the list keeps a usable height.
            float paneHeight = Mathf.Clamp(position.height * 0.42f, 140f, Mathf.Max(140f, position.height - 220f));
            var paneRect = GUILayoutUtility.GetRect(0f, paneHeight, GUILayout.ExpandWidth(true));
            m_Diff?.Draw(paneRect);

            DrawActionBar();

            if (!string.IsNullOrEmpty(m_StatusBanner))
            {
                EditorGUILayout.HelpBox(m_StatusBanner,
                    m_StatusBanner.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                        ? MessageType.Error : MessageType.Info);
            }
        }

        // Always draws every button (disabled when inapplicable) so the control count is stable
        // across the Layout and Repaint passes — selection changes in the click event, after Layout.
        private void DrawActionBar()
        {
            bool hasSel = m_SelectedIndex >= 0 && m_SelectedIndex < m_Entries.Count;
            bool hasOlder = hasSel && m_SelectedIndex + 1 < m_Entries.Count;
            bool isFolder = IsFolder();
            int selRev = hasSel ? m_Entries[m_SelectedIndex].revision : 0;

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(m_ReachedEarliest || m_LowestRevSeen <= 1))
            {
                if (GUILayout.Button(new GUIContent("Load older", "Fetch the next page of older revisions"),
                        GUILayout.Width(110)))
                {
                    int next = m_LowestRevSeen - 1;
                    if (next > 0) _ = LoadPageAsync(next);
                }
            }
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!hasSel))
            {
                bool vsCur = m_CompareMode == 0;
                if (GUILayout.Toggle(vsCur, new GUIContent("vs current", "Compare this revision against your current file"),
                        EditorStyles.miniButtonLeft, GUILayout.Width(78)) && !vsCur)
                {
                    m_CompareMode = 0; DriveDiff();
                }
                using (new EditorGUI.DisabledScope(!hasOlder))
                {
                    bool vsPrev = m_CompareMode == 1;
                    if (GUILayout.Toggle(vsPrev, new GUIContent("vs prev", "Compare this revision against the next-older entry in this file's history"),
                            EditorStyles.miniButtonRight, GUILayout.Width(66)) && !vsPrev)
                    {
                        m_CompareMode = 1; DriveDiff();
                    }
                }
                if (GUILayout.Button(new GUIContent("Open externally",
                        "Open the current comparison in the configured external diff tool (TortoiseMerge / Beyond Compare / kdiff3)."),
                        GUILayout.Width(110)))
                {
                    OpenExternalCurrent();
                }
            }
            using (new EditorGUI.DisabledScope(!hasSel || isFolder || m_Restoring))
            {
                if (GUILayout.Button(new GUIContent(hasSel ? $"Restore to r{selRev}" : "Restore to…",
                        "Replace your current file with its content from the selected revision"),
                        GUILayout.Width(130)))
                {
                    BeginRestore();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private bool IsFolder() => !string.IsNullOrEmpty(m_AssetPath) && AssetDatabase.IsValidFolder(m_AssetPath);

        // Drives the in-pane diff for the current selection + compare mode. vs current = the selected
        // revision against your working file; vs prev = the next-older entry in THIS file's log against
        // the selected revision (not rev-1).
        private void DriveDiff()
        {
            if (m_SelectedIndex < 0 || m_SelectedIndex >= m_Entries.Count || IsFolder())
            {
                m_Diff?.Clear();
                return;
            }
            int rev = m_Entries[m_SelectedIndex].revision;
            if (m_CompareMode == 1 && m_SelectedIndex + 1 < m_Entries.Count)
            {
                m_Diff?.ShowRevisions(m_AssetPath, m_Entries[m_SelectedIndex + 1].revision, rev);
            }
            else
            {
                m_Diff?.ShowRevisions(m_AssetPath, rev, null);
            }
        }

        private void OpenExternalCurrent()
        {
            if (m_SelectedIndex < 0 || m_SelectedIndex >= m_Entries.Count) return;
            int rev = m_Entries[m_SelectedIndex].revision;
            bool vsPrev = m_CompareMode == 1 && m_SelectedIndex + 1 < m_Entries.Count;
            int prevRev = vsPrev ? m_Entries[m_SelectedIndex + 1].revision : 0;
            // Deferred: DiffVsCurrentAsync/DiffRevisionsAsync set m_StatusBanner synchronously, which
            // would add the status HelpBox control mid-OnGUI-event if called straight from the button.
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                if (vsPrev) _ = DiffRevisionsAsync(prevRev, rev);
                else _ = DiffVsCurrentAsync(rev);
            };
        }

        // Restore the selected revision of THIS asset. Deferred out of OnGUI (modal); the asset path
        // and revision are captured at click time so a window re-target can't restore the wrong file.
        private void BeginRestore()
        {
            if (m_Restoring || m_SelectedIndex < 0 || m_SelectedIndex >= m_Entries.Count || IsFolder()) return;
            m_Restoring = true;
            string assetPath = m_AssetPath;
            int rev = m_Entries[m_SelectedIndex].revision;
            EditorApplication.delayCall += () => LaunchRestore(assetPath, rev);
        }

        private void LaunchRestore(string assetPath, int rev)
        {
            var items = new List<RestoreFlow.Item>
            {
                new RestoreFlow.Item(assetPath, assetPath, rev, assetPath),
            };
            RestoreFlow.Run(items, null,
                notice => { m_StatusBanner = notice ?? ""; if (this != null) Repaint(); },
                () => { AssetDatabase.Refresh(); if (this != null) { m_Diff?.Clear(); DriveDiff(); } },
                _ => { m_Restoring = false; if (this != null) Repaint(); });
        }

        private async Task LoadPageAsync(int upperRev)
        {
            CancelAndStart();
            var token = m_Cts.Token;
            try
            {
                var page = await SvnCommandRunner.FetchLogAsync(m_AssetPath, upperRev, PAGE_SIZE, token);
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    // A late page from a previous target (the window is reused via GetWindow): drop it,
                    // so Restore can't pair the new asset with a revision from the old file's log.
                    if (token.IsCancellationRequested) return;
                    if (page == null || page.Count == 0)
                    {
                        m_ReachedEarliest = true;
                        m_StatusBanner = m_Entries.Count == 0
                            ? "No log entries returned. Either no history yet, or check console for errors."
                            : "";
                    }
                    else
                    {
                        m_Entries.AddRange(page);
                        int newLowest = page.Min(x => x.revision);
                        if (newLowest < m_LowestRevSeen) m_LowestRevSeen = newLowest;
                        if (page.Count < PAGE_SIZE) m_ReachedEarliest = true;
                        m_StatusBanner = "";
                    }
                    Repaint();
                }, null);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    m_StatusBanner = $"Error fetching log: {ex.Message}. Try Refresh after fixing the issue.";
                    Repaint();
                }, null);
            }
        }

        private async Task DiffVsCurrentAsync(int revision)
        {
            m_StatusBanner = $"Exporting r{revision} for diff…";
            Repaint();
            try
            {
                CancelAndStart();
                var temp = await SvnCommandRunner.ExportRevisionToTempAsync(m_AssetPath, revision, m_Cts.Token);
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    if (string.IsNullOrEmpty(temp))
                    {
                        m_StatusBanner = $"Error: could not export r{revision}. Check console.";
                    }
                    else
                    {
                        var working = Path.IsPathRooted(m_AssetPath)
                            ? m_AssetPath
                            : Path.Combine(SvnCommandRunner.ProjectRoot, m_AssetPath);
                        ShellOutDiff(temp, working, $"r{revision}", "current");
                        m_StatusBanner = $"Diff opened in external tool (r{revision} vs current).";
                    }
                    Repaint();
                }, null);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    m_StatusBanner = $"Error: {ex.Message}";
                    Repaint();
                }, null);
            }
        }

        private async Task DiffRevisionsAsync(int prevRev, int newerRev)
        {
            m_StatusBanner = $"Exporting r{prevRev} and r{newerRev}…";
            Repaint();
            try
            {
                CancelAndStart();
                var prev = await SvnCommandRunner.ExportRevisionToTempAsync(m_AssetPath, prevRev, m_Cts.Token);
                var newer = await SvnCommandRunner.ExportRevisionToTempAsync(m_AssetPath, newerRev, m_Cts.Token);
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    if (string.IsNullOrEmpty(prev) || string.IsNullOrEmpty(newer))
                    {
                        m_StatusBanner = "Error: export of one or both revisions failed. Check console.";
                    }
                    else
                    {
                        ShellOutDiff(prev, newer, $"r{prevRev}", $"r{newerRev}");
                        m_StatusBanner = $"Diff opened in external tool (r{prevRev} vs r{newerRev}).";
                    }
                    Repaint();
                }, null);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    m_StatusBanner = $"Error: {ex.Message}";
                    Repaint();
                }, null);
            }
        }

        /// <summary>Spawn an external diff tool. Tries TortoiseMerge first (typically bundled with
        /// TortoiseSVN). Falls back to Beyond Compare, kdiff3, then a clear error message.</summary>
        private static void ShellOutDiff(string leftPath, string rightPath, string leftLabel, string rightLabel)
        {
            if (FRGVersionControl.Core.DiffToolLocator.Launch(leftPath, rightPath, leftLabel, rightLabel)) return;
            Debug.LogError("[FRGVersionControl.AssetLog] " + FRGVersionControl.Core.DiffToolLocator.NoToolSentence);
        }

        private void CancelAndStart()
        {
            if (m_Cts != null) { try { m_Cts.Cancel(); } catch { } m_Cts.Dispose(); }
            m_Cts = new CancellationTokenSource();
        }

        private static string FormatDate(string iso)
        {
            if (DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return iso;
        }
    }
}
