using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Modal selection window shown by FileLockAssetModificationProcessor when a save
    /// touches files locked by other users. Lists each locked-by-other path in
    /// Changes-tab row style with per-row checkboxes; user picks which files to mark
    /// as Ignore on Commit so the pending edit can be written. Cancel (or window-X)
    /// drops every locked file from the save (existing behavior, now visually obvious).
    ///
    /// Caller pattern (from OnWillSaveAssets):
    ///     var w = OverrideOnSaveWindow.Create(items);
    ///     w.ShowModal();   // genuinely blocking — safe inside save hook
    ///     if (!w.UserCancelled) { ... use w.SelectedPaths ... }
    /// (Sample button label: "Save with Ignore on Commit".)
    /// </summary>
    internal class OverrideOnSaveWindow : EditorWindow
    {
        public class Item
        {
            public string path;
            public FileLock existingLock;
            public bool selected;
        }

        public List<string> SelectedPaths { get; private set; } = new List<string>();
        public bool UserCancelled { get; private set; } = true; // safe default: window-X close = Cancel

        private List<Item> m_Items;
        private Vector2 m_Scroll;
        private int m_OtherSaveableCount;   // size of the rest of the save batch that's NOT blocked

        public static OverrideOnSaveWindow Create(
            IEnumerable<(string path, FileLock existingLock)> lockedByOther,
            int otherSaveableCount = 0)
        {
            var w = CreateInstance<OverrideOnSaveWindow>();
            w.m_Items = lockedByOther
                .Select(t => new Item { path = t.path, existingLock = t.existingLock, selected = false })
                .ToList();
            w.m_OtherSaveableCount = otherSaveableCount;
            return w;
        }

        private void OnGUI()
        {
            string otherHint = m_OtherSaveableCount > 0
                ? $" (The other {m_OtherSaveableCount} unblocked file(s) in this save will still be written.)"
                : "";
            EditorGUILayout.HelpBox(
                "These files are locked by other users. Tick the ones you want to mark as " +
                "Ignore on Commit so your edits get saved (the file stays uncommittable until " +
                "the holder unlocks). Cancel discards the pending edits to those files." +
                otherHint,
                MessageType.Warning);

            int selectedCount = m_Items.Count(i => i.selected);
            bool allSelected = selectedCount == m_Items.Count && m_Items.Count > 0;

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            bool newAll = EditorGUILayout.ToggleLeft(
                $"Select all ({selectedCount}/{m_Items.Count})",
                allSelected, GUILayout.Width(180));
            if (newAll != allSelected)
            {
                foreach (var i in m_Items) i.selected = newAll;
            }
            GUILayout.Label("Status  File                              Folder                            Lock",
                            EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            foreach (var item in m_Items) DrawRow(item);
            EditorGUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Cancel", GUILayout.Width(110), GUILayout.Height(24)))
            {
                SelectedPaths = new List<string>();
                UserCancelled = true;
                Close();
            }
            using (new EditorGUI.DisabledScope(selectedCount == 0))
            {
                if (GUILayout.Button($"Save with Ignore on Commit ({selectedCount})",
                                     GUILayout.Width(260), GUILayout.Height(24)))
                {
                    SelectedPaths = m_Items.Where(i => i.selected).Select(i => i.path).ToList();
                    UserCancelled = false;
                    Close();
                }
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(6);
        }

        private void DrawRow(Item item)
        {
            EditorGUILayout.BeginHorizontal();

            item.selected = EditorGUILayout.Toggle(item.selected, GUILayout.Width(20));

            // Status glyph: always "M" for these rows — the user is mid-save, so the
            // working-tree edit is pending. Mirrors the Changes-tab StatusGlyph for
            // SvnFileStatus.Modified.
            GUILayout.Label("M", GUILayout.Width(24));

            string fileName = System.IO.Path.GetFileName(DisplayPath(item.path));
            string folderPath = System.IO.Path.GetDirectoryName(item.path);
            if (!string.IsNullOrEmpty(folderPath)) folderPath = folderPath.Replace('\\', '/');

            if (GUILayout.Button(fileName, EditorStyles.label, GUILayout.ExpandWidth(false)))
            {
                // Translate .meta → underlying asset for the Project-view ping (LoadAssetAtPath
                // returns null for a .meta path), matching FrgVersionControlWindow.DrawRow.
                string pingPath = FileLockManager.GetLockabilityCheckPath(item.path);
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(pingPath);
                if (obj != null)
                {
                    EditorGUIUtility.PingObject(obj);
                    Selection.activeObject = obj;
                }
            }

            var prevColor = GUI.color;
            GUI.color = new Color(prevColor.r, prevColor.g, prevColor.b, prevColor.a * 0.6f);
            GUILayout.Label(folderPath ?? "", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            GUI.color = prevColor;

            string lockTag = FormatLockTag(item.existingLock);
            GUILayout.Label(lockTag, GUILayout.Width(260));

            EditorGUILayout.EndHorizontal();
        }

        private static string FormatLockTag(FileLock fl)
        {
            if (fl == null) return "";
            string ago;
            try
            {
                var elapsed = DateTime.UtcNow - fl.GetLockedAtTime();
                ago = FormatTimeAgo(elapsed);
            }
            catch
            {
                ago = "?";
            }
            string who = !string.IsNullOrEmpty(fl.ownerName) ? fl.ownerName : (fl.owner ?? "(unknown)");
            return $"🔒 {who} · {ago} ago";
        }

        private static string FormatTimeAgo(TimeSpan elapsed)
        {
            if (elapsed.TotalMinutes < 1) return "less than a minute";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} minute{((int)elapsed.TotalMinutes != 1 ? "s" : "")}";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} hour{((int)elapsed.TotalHours != 1 ? "s" : "")}";
            return $"{(int)elapsed.TotalDays} day{((int)elapsed.TotalDays != 1 ? "s" : "")}";
        }

        private static string DisplayPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                ? $"{path.Substring(0, path.Length - ".meta".Length)} (meta)"
                : path;
        }
    }
}
