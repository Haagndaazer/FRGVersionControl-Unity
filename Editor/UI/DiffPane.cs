using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Diff;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Side-by-side comparison of one changed file (committed base on the left, working file on
    /// the right) drawn from a <c>diff.working</c> result. Requests are delayed briefly and run
    /// one at a time, so clicking through rows asks svn-core only for the row the user stops on.
    /// </summary>
    internal sealed class DiffPane
    {
        private const double RequestDelaySeconds = 0.2;
        private const float RowHeight = 16f;
        private const float HeaderHeight = 22f;
        private const float NoticeHeight = 18f;
        private const float GutterWidth = 46f;
        private const float ScrollbarSize = 14f;

        private static readonly Color s_AddWash = new Color(0.25f, 0.8f, 0.35f, 0.22f);
        private static readonly Color s_DelWash = new Color(0.95f, 0.3f, 0.3f, 0.22f);
        private static readonly Color s_EmptyWash = new Color(0.5f, 0.5f, 0.5f, 0.09f);
        private static readonly Color s_GapWash = new Color(0.5f, 0.5f, 0.5f, 0.18f);
        private static readonly Color s_Divider = new Color(0.5f, 0.5f, 0.5f, 0.45f);

        private static GUIStyle s_Text;
        private static GUIStyle s_Gutter;
        private static GUIStyle s_GapLabel;
        private static GUIStyle s_Message;

        private readonly Action m_Repaint;

        private string m_Path;
        private string m_Signature;
        private WorkingDiff m_Diff;
        private bool m_Loading;
        private string m_Notice;

        private string m_PendingPath;
        private string m_PendingSignature;
        private double m_PendingAt;
        private bool m_InFlight;
        private bool m_Pumping;
        private bool m_Exporting;
        private bool m_Disposed;

        private float m_ScrollY;
        private float m_ScrollX;
        private float m_ContentWidth;
        private int m_WidestRow = -1;

        public DiffPane(Action repaint)
        {
            m_Repaint = repaint;
        }

        public string Path => m_Path;

        /// <summary>Shows <paramref name="path"/> (project-relative). <paramref name="statusKey"/>
        /// is whatever the caller knows about the row's svn state; together with the file's size
        /// and write time it decides whether the comparison on screen is still current.</summary>
        public void Show(string path, string statusKey)
        {
            if (string.IsNullOrEmpty(path))
            {
                Clear();
                return;
            }
            string signature = Signature(path, statusKey);
            bool samePath = m_Path != null && PathKey.Same(m_Path, path);
            if (samePath && signature == m_Signature && (m_Diff == null || !m_Diff.busy || m_PendingPath != null)) return;

            if (!samePath)
            {
                m_Diff = null;
                m_Notice = null;
                m_ScrollY = 0f;
                m_ScrollX = 0f;
            }
            m_Path = path;
            m_Signature = signature;
            m_Loading = true;
            m_PendingPath = path;
            m_PendingSignature = signature;
            m_PendingAt = EditorApplication.timeSinceStartup + RequestDelaySeconds;
            StartPump();
            m_Repaint?.Invoke();
        }

        public void Clear()
        {
            m_Path = null;
            m_Signature = null;
            m_Diff = null;
            m_Loading = false;
            m_Notice = null;
            m_PendingPath = null;
        }

        public void Dispose()
        {
            m_Disposed = true;
            StopPump();
        }

        private static string Signature(string path, string statusKey)
        {
            long length = -1;
            long ticks = 0;
            try
            {
                var info = new FileInfo(Absolute(path));
                if (info.Exists)
                {
                    length = info.Length;
                    ticks = info.LastWriteTimeUtc.Ticks;
                }
            }
            catch { }
            return statusKey + "|" + length + "|" + ticks;
        }

        private static string Absolute(string path)
            => System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(SvnCommandRunner.ProjectRoot, path);

        private void StartPump()
        {
            if (m_Pumping || m_Disposed) return;
            m_Pumping = true;
            EditorApplication.update += Pump;
        }

        private void StopPump()
        {
            if (!m_Pumping) return;
            m_Pumping = false;
            EditorApplication.update -= Pump;
        }

        private void Pump()
        {
            if (m_PendingPath == null)
            {
                if (!m_InFlight) StopPump();
                return;
            }
            if (m_InFlight || EditorApplication.timeSinceStartup < m_PendingAt) return;

            string path = m_PendingPath;
            string signature = m_PendingSignature;
            m_PendingPath = null;
            m_InFlight = true;
            WorkingDiffService.FetchAsync(path).ContinueWith(task =>
            {
                WorkingDiff diff = task.IsFaulted || task.IsCanceled
                    ? new WorkingDiff { path = path, error = "Could not compare this file. Click the file again to retry." }
                    : task.Result;
                SvnCommandRunner.s_UnityMainSync.Post(_ => OnFetched(path, signature, diff), null);
            });
        }

        private void OnFetched(string path, string signature, WorkingDiff diff)
        {
            m_InFlight = false;
            if (m_Disposed) return;
            if (m_Path == null || !PathKey.Same(m_Path, path) || signature != m_Signature)
            {
                if (m_PendingPath != null) StartPump();
                return;
            }
            m_Diff = diff;
            m_Loading = false;
            m_WidestRow = -1;
            if (diff != null && diff.busy)
            {
                m_Loading = true;
                m_PendingPath = path;
                m_PendingSignature = signature;
                m_PendingAt = EditorApplication.timeSinceStartup + 2.0;
                StartPump();
            }
            m_Repaint?.Invoke();
        }

        private static void EnsureStyles()
        {
            if (s_Text != null) return;
            Font mono = null;
            try { mono = EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf") as Font; } catch { }
            if (mono == null)
            {
                try
                {
                    mono = Font.CreateDynamicFontFromOSFont(
                        new[] { "Consolas", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "Courier New" }, 12);
                }
                catch { }
            }
            s_Text = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                richText = false,
                clipping = TextClipping.Clip,
                padding = new RectOffset(4, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            if (mono != null) s_Text.font = mono;
            s_Gutter = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                padding = new RectOffset(0, 5, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                clipping = TextClipping.Clip,
            };
            s_GapLabel = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleCenter };
            s_Message = new GUIStyle(EditorStyles.wordWrappedLabel) { alignment = TextAnchor.MiddleCenter };
        }

        public void Draw(Rect rect)
        {
            EnsureStyles();
            if (rect.height < HeaderHeight + RowHeight) return;

            var header = new Rect(rect.x, rect.y, rect.width, HeaderHeight);
            DrawHeader(header);
            var body = new Rect(rect.x, rect.y + HeaderHeight, rect.width, rect.height - HeaderHeight);

            if (!string.IsNullOrEmpty(m_Notice))
            {
                var notice = new Rect(body.x, body.y, body.width, NoticeHeight);
                EditorGUI.HelpBox(notice, m_Notice, MessageType.None);
                body.y += NoticeHeight;
                body.height -= NoticeHeight;
            }

            string message = Message();
            if (message != null)
            {
                GUI.Label(new Rect(body.x + 8, body.y, body.width - 16, body.height), message, s_Message);
                return;
            }

            if (m_Diff.truncated)
            {
                var cut = new Rect(body.x, body.y, body.width, NoticeHeight);
                EditorGUI.HelpBox(cut, "This change is too large to show in full: only the first part is listed. Use Open in external tool to see all of it.", MessageType.None);
                body.y += NoticeHeight;
                body.height -= NoticeHeight;
            }
            DrawRows(body);
        }

        private void DrawHeader(Rect rect)
        {
            if (Event.current.type == EventType.Repaint) EditorStyles.toolbar.Draw(rect, false, false, false, false);
            const float buttonWidth = 150f;
            var labelRect = new Rect(rect.x + 6, rect.y, rect.width - buttonWidth - 14, rect.height);
            GUI.Label(labelRect, HeaderText(), EditorStyles.miniBoldLabel);

            bool canExternal = m_Path != null && !m_Exporting && m_Diff != null && m_Diff.error == null && !m_Diff.busy
                               && m_Diff.hasBase && m_Diff.hasWorking && m_Diff.state != "unsupported";
            using (new EditorGUI.DisabledScope(!canExternal))
            {
                var buttonRect = new Rect(rect.xMax - buttonWidth - 4, rect.y + 1, buttonWidth, rect.height - 3);
                if (GUI.Button(buttonRect, new GUIContent("Open in external tool",
                        "Open the committed version and your file in TortoiseMerge, Beyond Compare or KDiff3."),
                        EditorStyles.toolbarButton))
                {
                    OpenExternal();
                }
            }
        }

        private string HeaderText()
        {
            if (m_Path == null) return "Changes preview";
            string name = System.IO.Path.GetFileName(m_Path);
            if (m_Diff == null || m_Diff.error != null || m_Diff.busy) return name;
            string left = !m_Diff.hasBase ? "new file"
                : m_Diff.baseRevision > 0 ? "r" + m_Diff.baseRevision : "committed";
            if (m_Diff.movedFrom != null) left += " (moved from " + System.IO.Path.GetFileName(m_Diff.movedFrom.Replace('\\', '/')) + ")";
            string right = m_Diff.hasWorking ? "your file" : "deleted";
            string counts = m_Diff.state == "text" ? "    +" + m_Diff.added + "  −" + m_Diff.removed : "";
            return name + "    " + left + "  ↔  " + right + counts;
        }

        /// <summary>The sentence shown in place of rows, or null when rows are drawn.</summary>
        private string Message()
        {
            if (m_Path == null) return "Click a file in the list above to see what changed in it.";
            if (m_Diff == null) return m_Loading ? "Comparing…" : "";
            if (m_Diff.busy) return WorkingDiffService.BusySentence;
            if (m_Diff.error != null) return m_Diff.error;
            switch (m_Diff.state)
            {
                case "text":
                    return m_Diff.rows.Length == 0 ? "This file is empty." : null;
                case "binary":
                    return "This is not a text file, so its changes cannot be shown line by line.";
                case "tooLarge":
                    long mb = Math.Max(1, m_Diff.limitBytes / (1024 * 1024));
                    return "This file is larger than " + mb + " MB. Use Open in external tool to compare it.";
                case "unchanged":
                    switch (m_Diff.reason)
                    {
                        case "lineEndingsOnly": return "Only the line endings of this file changed.";
                        case "propertiesOnly": return "Only the SVN properties of this file changed, not its content.";
                        default: return "This file has no changes.";
                    }
                case "unsupported":
                    switch (m_Diff.reason)
                    {
                        case "directory": return "This is a folder. Click a file to see what changed in it.";
                        case "conflict": return "This file is in conflict. Use Take Mine, Take Theirs or Open externally on its row.";
                        case "notFound": return "This file is no longer on disk. Refresh the list.";
                        default: return "SVN cannot compare this file in its current state. Refresh the list.";
                    }
                default:
                    return "This file cannot be compared (" + m_Diff.state + "). Update the FRG Version Control package.";
            }
        }

        private void DrawRows(Rect body)
        {
            var rows = m_Diff.rows;
            float viewWidth = body.width - ScrollbarSize;
            float columnWidth = Mathf.Floor(viewWidth / 2f);
            float textWidth = Mathf.Max(10f, columnWidth - GutterWidth);
            MeasureContent(rows);

            bool needHorizontal = m_ContentWidth > textWidth;
            float viewHeight = body.height - (needHorizontal ? ScrollbarSize : 0f);
            float totalHeight = rows.Length * RowHeight;
            float maxY = Mathf.Max(0f, totalHeight - viewHeight);
            float maxX = Mathf.Max(0f, m_ContentWidth - textWidth);

            var e = Event.current;
            if (e.type == EventType.ScrollWheel && body.Contains(e.mousePosition))
            {
                if (e.shift) m_ScrollX += e.delta.y * 40f;
                else
                {
                    m_ScrollY += e.delta.y * RowHeight * 3f;
                    m_ScrollX += e.delta.x * 40f;
                }
                e.Use();
            }

            var vRect = new Rect(body.xMax - ScrollbarSize, body.y, ScrollbarSize, viewHeight);
            if (maxY > 0f) m_ScrollY = GUI.VerticalScrollbar(vRect, m_ScrollY, viewHeight, 0f, totalHeight);
            if (needHorizontal)
            {
                var hRect = new Rect(body.x + GutterWidth, body.yMax - ScrollbarSize, viewWidth - GutterWidth, ScrollbarSize);
                m_ScrollX = GUI.HorizontalScrollbar(hRect, m_ScrollX, textWidth, 0f, m_ContentWidth);
            }
            m_ScrollY = Mathf.Clamp(m_ScrollY, 0f, maxY);
            m_ScrollX = Mathf.Clamp(m_ScrollX, 0f, maxX);

            if (e.type != EventType.Repaint) return;

            var view = new Rect(body.x, body.y, viewWidth, viewHeight);
            GUI.BeginClip(view);
            int first = Mathf.Max(0, Mathf.FloorToInt(m_ScrollY / RowHeight));
            int last = Mathf.Min(rows.Length - 1, Mathf.CeilToInt((m_ScrollY + viewHeight) / RowHeight));
            for (int i = first; i <= last; i++)
            {
                float y = i * RowHeight - m_ScrollY;
                DrawRow(rows[i], y, columnWidth, textWidth);
            }
            EditorGUI.DrawRect(new Rect(columnWidth, 0f, 1f, viewHeight), s_Divider);
            GUI.EndClip();
        }

        private void DrawRow(in DiffRowView row, float y, float columnWidth, float textWidth)
        {
            if (row.kind == DiffRowKind.Gap)
            {
                var bar = new Rect(0f, y, columnWidth * 2f, RowHeight);
                EditorGUI.DrawRect(bar, s_GapWash);
                string text = row.skipped > 0
                    ? "⋯ " + row.skipped + (row.skipped == 1 ? " unchanged line ⋯" : " unchanged lines ⋯")
                    : "⋯ more unchanged lines ⋯";
                GUI.Label(bar, text, s_GapLabel);
                return;
            }

            bool leftHas = row.kind != DiffRowKind.Add;
            bool rightHas = row.kind != DiffRowKind.Del;
            var leftRect = new Rect(0f, y, columnWidth, RowHeight);
            var rightRect = new Rect(columnWidth, y, columnWidth, RowHeight);
            if (row.kind == DiffRowKind.Del || row.kind == DiffRowKind.Chg) EditorGUI.DrawRect(leftRect, s_DelWash);
            if (row.kind == DiffRowKind.Add || row.kind == DiffRowKind.Chg) EditorGUI.DrawRect(rightRect, s_AddWash);
            if (!leftHas) EditorGUI.DrawRect(leftRect, s_EmptyWash);
            if (!rightHas) EditorGUI.DrawRect(rightRect, s_EmptyWash);

            if (leftHas) DrawSide(0f, y, textWidth, row.left, row.leftText);
            if (rightHas) DrawSide(columnWidth, y, textWidth, row.right, row.rightText);
        }

        private void DrawSide(float x, float y, float textWidth, int lineNumber, string text)
        {
            GUI.Label(new Rect(x, y, GutterWidth, RowHeight), lineNumber > 0 ? lineNumber.ToString() : "", s_Gutter);
            if (string.IsNullOrEmpty(text)) return;
            GUI.BeginClip(new Rect(x + GutterWidth, y, textWidth, RowHeight));
            GUI.Label(new Rect(-m_ScrollX, 0f, Mathf.Max(textWidth, m_ContentWidth), RowHeight), text, s_Text);
            GUI.EndClip();
        }

        private void MeasureContent(DiffRowView[] rows)
        {
            if (m_WidestRow >= 0) return;
            int widest = 0;
            string longest = "";
            for (int i = 0; i < rows.Length; i++)
            {
                var l = rows[i].leftText;
                var r = rows[i].rightText;
                if (l != null && l.Length > longest.Length) { longest = l; widest = i; }
                if (r != null && r.Length > longest.Length) { longest = r; widest = i; }
            }
            m_WidestRow = widest;
            m_ContentWidth = longest.Length == 0 ? 0f : s_Text.CalcSize(new GUIContent(longest)).x + 12f;
        }

        private void OpenExternal()
        {
            string path = m_Path;
            long revision = m_Diff != null ? m_Diff.baseRevision : 0;
            m_Exporting = true;
            m_Notice = "Getting the committed version…";
            WorkingDiffService.ExportBaseAsync(path).ContinueWith(task =>
            {
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    m_Exporting = false;
                    if (m_Disposed) return;
                    if (task.IsFaulted || task.IsCanceled)
                    {
                        m_Notice = "Could not get the committed version of this file. Try again.";
                    }
                    else if (task.Result.tempPath == null)
                    {
                        m_Notice = task.Result.error;
                    }
                    else
                    {
                        string leftLabel = revision > 0 ? "r" + revision : "committed";
                        m_Notice = DiffToolLocator.Launch(task.Result.tempPath, Absolute(path), leftLabel, "your file")
                            ? null
                            : DiffToolLocator.NoToolSentence;
                    }
                    m_Repaint?.Invoke();
                }, null);
            });
        }
    }
}
