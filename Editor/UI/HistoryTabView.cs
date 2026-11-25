using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Project-wide commit history for the FRG Version Control window's History tab. Lists recent
    /// commits (newest first), each expandable to its changed files; clicking a changed file opens
    /// it at that revision in the shared Changes-Preview pane. Scale-safe: the list is fetched
    /// WITHOUT changed paths (hist.log includePaths:false) and paged 50 at a time; a commit's
    /// changed files are fetched only when its row is expanded (a single -r N:N call).
    /// </summary>
    internal sealed class HistoryTabView
    {
        private const int PAGE_SIZE = 50;
        private const int PATH_LIMIT = 500;

        private readonly DiffPane m_DiffPane;
        private readonly Action m_Repaint;

        private readonly List<SvnLogEntry> m_Entries = new List<SvnLogEntry>();
        private bool m_HasMore;
        private int m_LowestRev = int.MaxValue;
        private bool m_Loading;
        private string m_Error;
        private bool m_Started;
        private Vector2 m_Scroll;
        private CancellationTokenSource m_Cts;

        private readonly HashSet<int> m_Expanded = new HashSet<int>();
        private readonly Dictionary<int, List<SvnLogPath>> m_Paths = new Dictionary<int, List<SvnLogPath>>();
        private readonly Dictionary<int, int> m_PathTotal = new Dictionary<int, int>();
        private readonly HashSet<int> m_PathsLoading = new HashSet<int>();
        private readonly Dictionary<int, string> m_PathsError = new Dictionary<int, string>();

        private int m_SelectedRev = -1;
        private string m_SelectedRepoPath;
        private string m_Notice;
        private bool m_PendingRefresh;

        public HistoryTabView(DiffPane diffPane, Action repaint)
        {
            m_DiffPane = diffPane;
            m_Repaint = repaint;
        }

        /// <summary>Loads the first page the first time the tab is shown.</summary>
        public void EnsureStarted()
        {
            if (m_Started) return;
            m_Started = true;
            WorkingCopyLayout.EnsureFetched();
            RefreshNow();
        }

        /// <summary>Requests a refresh; deferred to the top of the next draw so the list isn't cleared
        /// mid-layout (which would desync IMGUI control counts for that event).</summary>
        public void Refresh() => m_PendingRefresh = true;

        private void RefreshNow()
        {
            CancelInFlight();
            m_Entries.Clear();
            m_Expanded.Clear();
            m_Paths.Clear();
            m_PathTotal.Clear();
            m_PathsLoading.Clear();
            m_PathsError.Clear();
            m_HasMore = false;
            m_LowestRev = int.MaxValue;
            m_Error = null;
            m_Notice = null;
            m_SelectedRev = -1;
            m_SelectedRepoPath = null;
            LoadPage(0);
        }

        public void Dispose() => CancelInFlight();

        private void CancelInFlight()
        {
            if (m_Cts != null) { try { m_Cts.Cancel(); } catch { } m_Cts.Dispose(); m_Cts = null; }
        }

        private void LoadPage(int startRevision)
        {
            CancelInFlight();
            m_Cts = new CancellationTokenSource();
            m_Loading = true;
            var token = m_Cts.Token;
            _ = SvnCommandRunner.FetchLogPageAsync(null, startRevision, 0, PAGE_SIZE, includePaths: false, pathLimit: 0, token)
                .ContinueWith(task =>
                {
                    SvnLogPage page = task.IsFaulted || task.IsCanceled ? null : task.Result;
                    SvnCommandRunner.s_UnityMainSync.Post(_ =>
                    {
                        if (token.IsCancellationRequested) return;
                        m_Loading = false;
                        if (page == null) { m_Error = "Could not load history. Try Refresh."; m_Repaint?.Invoke(); return; }
                        if (page.busy) { m_Error = "Another SVN operation is running. Try Refresh in a moment."; m_Repaint?.Invoke(); return; }
                        if (!string.IsNullOrEmpty(page.error)) { m_Error = page.error; m_Repaint?.Invoke(); return; }
                        m_Error = null;
                        foreach (var e in page.entries)
                        {
                            m_Entries.Add(e);
                            if (e.revision < m_LowestRev) m_LowestRev = e.revision;
                        }
                        m_HasMore = page.hasMore;
                        m_Repaint?.Invoke();
                    }, null);
                });
        }

        private void ToggleExpand(int revision)
        {
            if (!m_Expanded.Add(revision))
            {
                m_Expanded.Remove(revision);
                return;
            }
            if (!m_Paths.ContainsKey(revision) && !m_PathsLoading.Contains(revision))
            {
                LoadPaths(revision);
            }
        }

        private void LoadPaths(int revision)
        {
            m_PathsLoading.Add(revision);
            m_PathsError.Remove(revision);
            _ = SvnCommandRunner.FetchLogPageAsync(null, revision, revision, 1, includePaths: true, PATH_LIMIT)
                .ContinueWith(task =>
                {
                    SvnLogPage page = task.IsFaulted || task.IsCanceled ? null : task.Result;
                    SvnCommandRunner.s_UnityMainSync.Post(_ =>
                    {
                        m_PathsLoading.Remove(revision);
                        if (page == null) { m_PathsError[revision] = "Could not load changed files. Collapse and expand to retry."; m_Repaint?.Invoke(); return; }
                        if (page.busy) { m_PathsError[revision] = "Another SVN operation is running. Collapse and expand to retry."; m_Repaint?.Invoke(); return; }
                        if (!string.IsNullOrEmpty(page.error)) { m_PathsError[revision] = page.error; m_Repaint?.Invoke(); return; }
                        var entry = page.entries.Count > 0 ? page.entries[0] : null;
                        if (entry == null) { m_PathsError[revision] = "Revision details not available. Collapse and expand to retry."; m_Repaint?.Invoke(); return; }
                        m_Paths[revision] = entry.paths ?? new List<SvnLogPath>();
                        if (entry.pathsTruncated) m_PathTotal[revision] = entry.pathCount;
                        m_Repaint?.Invoke();
                    }, null);
                });
        }

        private void SelectFile(int revision, SvnLogPath p)
        {
            m_SelectedRev = revision;
            m_SelectedRepoPath = p.path;
            WorkingCopyLayout.EnsureFetched();
            string projectRel = WorkingCopyLayout.RepoPathToProjectRelative(p.path);
            if (string.IsNullOrEmpty(projectRel))
            {
                // Outside the Unity project folder, or meta.layout hasn't answered yet: can't open in-pane.
                m_Notice = "This file is outside the Unity project folder (or the working-copy layout is still loading), so it can't be previewed here.";
                m_DiffPane?.Clear();
                m_Repaint?.Invoke();
                return;
            }
            m_Notice = null;
            // "What changed in commit N" is rN-1 -> rN. A delete has no working node, so the core
            // answers path_not_found and the pane shows that; an add renders as a whole-file add.
            m_DiffPane?.ShowRevisions(projectRel, revision - 1, revision);
        }

        // ----- drawing -----

        public void DrawList()
        {
            EnsureStarted();
            if (m_PendingRefresh) { m_PendingRefresh = false; RefreshNow(); }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label(m_Entries.Count > 0 ? $"{m_Entries.Count} commit(s)" : (m_Loading ? "Loading…" : "History"),
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(m_Loading))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70))) Refresh();
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(m_Error))
            {
                EditorGUILayout.HelpBox(m_Error, MessageType.Warning);
            }
            if (!string.IsNullOrEmpty(m_Notice))
            {
                EditorGUILayout.HelpBox(m_Notice, MessageType.Info);
            }

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.ExpandHeight(true));
            if (m_Entries.Count == 0 && !m_Loading && string.IsNullOrEmpty(m_Error))
            {
                EditorGUILayout.HelpBox("No committed history for this project yet.", MessageType.Info);
            }
            for (int i = 0; i < m_Entries.Count; i++)
            {
                DrawCommitRow(m_Entries[i]);
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(m_Loading || !m_HasMore || m_LowestRev <= 1))
            {
                if (GUILayout.Button(new GUIContent("Load older", "Fetch the next page of older commits"), GUILayout.Width(110)))
                {
                    int next = m_LowestRev - 1;
                    if (next > 0) LoadPage(next);
                }
            }
        }

        private void DrawCommitRow(SvnLogEntry e)
        {
            EditorGUILayout.BeginHorizontal();
            bool expanded = m_Expanded.Contains(e.revision);
            var foldoutRect = GUILayoutUtility.GetRect(14f, 16f, GUILayout.Width(14f));
            bool toggled = EditorGUI.Foldout(foldoutRect, expanded, GUIContent.none, true);
            if (toggled != expanded) ToggleExpand(e.revision);
            GUILayout.Label($"r{e.revision}", GUILayout.Width(60));
            GUILayout.Label(string.IsNullOrEmpty(e.author) ? "—" : e.author, GUILayout.Width(110));
            GUILayout.Label(FormatDate(e.date), GUILayout.Width(140));
            GUILayout.Label(FirstLine(e.message), EditorStyles.label);
            EditorGUILayout.EndHorizontal();

            if (!expanded) return;

            EditorGUI.indentLevel += 2;
            if (m_PathsLoading.Contains(e.revision))
            {
                EditorGUILayout.LabelField("Loading changed files…", EditorStyles.miniLabel);
            }
            else if (m_PathsError.TryGetValue(e.revision, out var perr))
            {
                EditorGUILayout.HelpBox(perr, MessageType.Warning);
            }
            else if (m_Paths.TryGetValue(e.revision, out var paths))
            {
                if (paths.Count == 0)
                {
                    EditorGUILayout.LabelField("(no file changes in this revision)", EditorStyles.miniLabel);
                }
                foreach (var p in paths)
                {
                    DrawPathRow(e.revision, p);
                }
                if (m_PathTotal.TryGetValue(e.revision, out var total) && total > paths.Count)
                {
                    EditorGUILayout.LabelField($"+{total - paths.Count} more (too many to list)", EditorStyles.miniLabel);
                }
            }
            EditorGUI.indentLevel -= 2;
        }

        private void DrawPathRow(int revision, SvnLogPath p)
        {
            bool selected = revision == m_SelectedRev && string.Equals(p.path, m_SelectedRepoPath, StringComparison.Ordinal);
            var rect = EditorGUILayout.BeginHorizontal();
            if (selected && Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, new Color(0.3f, 0.5f, 0.8f, 0.25f));
            }
            GUILayout.Space(EditorGUI.indentLevel * 8f);
            GUILayout.Label(ActionGlyph(p.action), GUILayout.Width(18));
            if (string.Equals(p.kind, "dir", StringComparison.Ordinal))
            {
                GUILayout.Label(DisplayRepoPath(p.path) + "  (folder)", EditorStyles.miniLabel);
            }
            else if (GUILayout.Button(DisplayRepoPath(p.path), EditorStyles.label))
            {
                SelectFile(revision, p);
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string ActionGlyph(string action)
        {
            switch (action)
            {
                case "A": return "A";
                case "D": return "D";
                case "R": return "R";
                default: return "M";
            }
        }

        private static string DisplayRepoPath(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath)) return "";
            int slash = repoPath.LastIndexOf('/');
            return slash >= 0 && slash < repoPath.Length - 1 ? repoPath.Substring(slash + 1) + "    " + repoPath : repoPath;
        }

        private static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            int nl = message.IndexOf('\n');
            return nl < 0 ? message : message.Substring(0, nl);
        }

        private static string FormatDate(string iso)
        {
            if (DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return iso ?? "";
        }
    }
}
