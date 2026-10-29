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
    /// - First load: 50 newest entries.
    /// - "Load older": fetches the next 50 with upperRev = lowestSeenRev - 1.
    /// - When a fetch returns fewer than the limit, the file's earliest revision is reached.
    /// - Click an entry → action buttons: "Diff vs current" / "Diff vs prev" (shells out to
    ///   the configured diff tool, falling back to TortoiseMerge).
    /// </summary>
    public class FileLockAssetLogWindow : EditorWindow
    {
        private const int PAGE_SIZE = 50;

        [MenuItem("Assets/FRG Version Control/Show Log", false, 2010)]
        private static void ShowLogForSelection()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(path)) return;
            Open(path);
        }

        [MenuItem("Assets/FRG Version Control/Show Log", true)]
        private static bool ShowLogForSelection_Validate()
        {
            return Selection.activeObject != null
                   && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(Selection.activeObject));
        }

        public static void Open(string assetPath)
        {
            var window = GetWindow<FileLockAssetLogWindow>(true, $"Log: {Path.GetFileName(assetPath)}");
            window.minSize = new Vector2(560, 360);
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

        private void SetTarget(string path)
        {
            m_AssetPath = path;
            m_Entries.Clear();
            m_LowestRevSeen = int.MaxValue;
            m_ReachedEarliest = false;
            m_SelectedIndex = -1;
            m_StatusBanner = "Loading…";
            _ = LoadPageAsync(/*upperRev*/ -1);
        }

        private void OnDisable()
        {
            if (m_Cts != null) { try { m_Cts.Cancel(); } catch { } m_Cts.Dispose(); m_Cts = null; }
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

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
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
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            // Load older
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(m_ReachedEarliest || m_LowestRevSeen <= 1))
            {
                if (GUILayout.Button(new GUIContent("Load older",
                                                    "Fetch the next page of older revisions"),
                                     GUILayout.Width(110)))
                {
                    int next = m_LowestRevSeen - 1;
                    if (next > 0) _ = LoadPageAsync(next);
                }
            }
            GUILayout.FlexibleSpace();
            if (m_SelectedIndex >= 0 && m_SelectedIndex < m_Entries.Count)
            {
                if (GUILayout.Button(new GUIContent("Diff vs current",
                                                    "Open the configured diff tool (TortoiseMerge / Beyond Compare / kdiff3) " +
                                                    "comparing this revision against your current working copy."),
                                     GUILayout.Width(120)))
                {
                    _ = DiffVsCurrentAsync(m_Entries[m_SelectedIndex].revision);
                }
                using (new EditorGUI.DisabledScope(m_SelectedIndex >= m_Entries.Count - 1))
                {
                    if (GUILayout.Button(new GUIContent("Diff vs prev",
                                                        "Open the configured diff tool comparing this revision " +
                                                        "against the next-older revision."),
                                         GUILayout.Width(110)))
                    {
                        var sel = m_Entries[m_SelectedIndex];
                        var prev = m_Entries[m_SelectedIndex + 1];
                        _ = DiffRevisionsAsync(prev.revision, sel.revision);
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(m_StatusBanner))
            {
                EditorGUILayout.HelpBox(m_StatusBanner,
                    m_StatusBanner.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                        ? MessageType.Error : MessageType.Info);
            }
        }

        private async Task LoadPageAsync(int upperRev)
        {
            CancelAndStart();
            try
            {
                var page = await SvnCommandRunner.FetchLogAsync(m_AssetPath, upperRev, PAGE_SIZE, m_Cts.Token);
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
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
            string[] candidates = new[]
            {
                @"C:\Program Files\TortoiseSVN\bin\TortoiseMerge.exe",
                @"C:\Program Files\Beyond Compare 4\BCompare.exe",
                @"C:\Program Files\KDiff3\kdiff3.exe",
                @"C:\Program Files (x86)\TortoiseSVN\bin\TortoiseMerge.exe"
            };
            foreach (var exe in candidates)
            {
                if (!File.Exists(exe)) continue;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = exe.EndsWith("TortoiseMerge.exe")
                            ? $"/base:\"{leftPath}\" /mine:\"{rightPath}\" /basename:\"{leftLabel}\" /minename:\"{rightLabel}\""
                            : $"\"{leftPath}\" \"{rightPath}\"",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FRGVersionControl.AssetLog] failed to spawn {exe}: {ex.Message}");
                }
            }
            Debug.LogError("[FRGVersionControl.AssetLog] No diff tool found. " +
                           "Install TortoiseMerge (bundled with TortoiseSVN) or Beyond Compare or kdiff3.");
        }

        private void CancelAndStart()
        {
            if (m_Cts != null) { try { m_Cts.Cancel(); } catch { } m_Cts.Dispose(); }
            m_Cts = new CancellationTokenSource();
        }

        private static string FormatDate(string iso)
        {
            if (DateTime.TryParse(iso, out var dt))
                return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return iso;
        }
    }
}
