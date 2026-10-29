using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.Core.OverrideMode;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Phase 4 Step 5: SceneView banner that warns the artist when the currently-edited scene or
    /// prefab is locked-by-other or out-of-date. Wise SVN cherry-pick + closure decoration.
    ///
    /// Three banner conditions:
    /// 1. Locked by another user (no override active) — red banner: "Locked by &lt;owner&gt;. Don't save."
    /// 2. Out-of-date (server has newer version) — yellow banner: "Server has changes. Run Update."
    /// 3. Both (locked-by-other AND out-of-date) — red banner with both messages stacked.
    ///
    /// Persistent suppression (Wise SVN cherry-pick #9): clicking [×] on a banner hides it for the
    /// current Unity session for that file. Re-prompts on Unity restart, on cache refresh that
    /// changes the state, or on switching to a different scene.
    /// </summary>
    [InitializeOnLoad]
    public static class FileLockSceneViewBanner
    {
        // Per-(scene-path, condition) suppression set; cleared on domain reload + on state-change refresh.
        private static readonly HashSet<string> s_DismissedKeys = new HashSet<string>();

        static FileLockSceneViewBanner()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            SvnStatusCache.SnapshotUpdated += OnSnapshotUpdated;
        }

        private static void OnSnapshotUpdated()
        {
            // When the cache refreshes, re-evaluate dismissal — if a file's state changed,
            // a banner that was previously dismissed should resurface.
            // Cheap implementation: clear all dismissals; the per-frame check re-applies them
            // until the user dismisses again. Aggressive but predictable.
            s_DismissedKeys.Clear();
        }

        private static void OnSceneGUI(SceneView sv)
        {
            if (FileLockManager.Instance == null || !FileLockManager.Instance.IsInitialized) return;

            var scenePath = EditorSceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath)) return;

            string lockMsg = null;
            string updateMsg = null;
            MessageType type = MessageType.None;

            var fl = FileLockManager.Instance.GetFileLock(scenePath);
            bool overrideActive = FileLockOverrideManager.Instance != null
                                   && FileLockOverrideManager.Instance.IsInOverrideMode(scenePath);

            if (fl != null && FileLockManager.Instance.IsLockedByOther(scenePath) && !overrideActive)
            {
                lockMsg = $"⚠ Scene is locked by {fl.owner}. Saving will not commit (server-side enforcement). " +
                          "Coordinate with the lock holder, or use Set Ignore on Commit if you must edit locally.";
                type = MessageType.Error;
            }

            var status = SvnStatusCache.Get(scenePath);
            // SVN doesn't put `outOfDate` into the per-file status enum; we approximate via
            // `Modified` server-status in the StatusUpstream entry. Conservative: only fire
            // the update banner when status -u says modified upstream.
            // (Cheap proxy: check the SvnStatusEntry from a fresh StatusUpstream call.)
            // To keep this banner cheap-per-frame, we ONLY rely on lock-by-other + override checks
            // here. The "out-of-date" condition is handled by the FRG Version Control → Changes tab + the
            // 5-min sync banner in the panel itself.

            if (lockMsg == null && updateMsg == null) return;

            string key = scenePath + "::lock-banner";
            if (s_DismissedKeys.Contains(key)) return;

            DrawBanner(sv, scenePath, type, lockMsg, updateMsg, () =>
            {
                s_DismissedKeys.Add(key);
                sv.Repaint();
            });
        }

        private static void DrawBanner(SceneView sv, string scenePath, MessageType type,
                                        string lockMsg, string updateMsg, System.Action onDismiss)
        {
            Handles.BeginGUI();
            try
            {
                const float pad = 6f;
                const float h = 60f;
                Rect rect = new Rect(pad, pad, sv.position.width - pad * 2, h);
                var prevColor = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.95f);

                GUILayout.BeginArea(rect, EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                if (!string.IsNullOrEmpty(lockMsg))
                {
                    EditorGUILayout.HelpBox(lockMsg, type);
                }
                if (!string.IsNullOrEmpty(updateMsg))
                {
                    EditorGUILayout.HelpBox(updateMsg, MessageType.Warning);
                }
                if (GUILayout.Button(new GUIContent("×", "Dismiss for this session"),
                                     GUILayout.Width(28), GUILayout.Height(50)))
                {
                    onDismiss?.Invoke();
                }
                EditorGUILayout.EndHorizontal();
                GUILayout.EndArea();

                GUI.color = prevColor;
            }
            finally
            {
                Handles.EndGUI();
            }
        }
    }
}
