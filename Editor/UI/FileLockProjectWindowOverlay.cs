using System.IO;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.OverrideMode;
using FRGVersionControl.Core.Svn;
using System;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Displays lock status icons beside files in Unity's Project window
    /// </summary>
    [InitializeOnLoad]
    public static class FileLockProjectWindowOverlay
    {
        private static Texture2D s_LockIcon;
        private static Texture2D s_UntrackedIcon;
        private static Texture2D s_ModifiedIcon;
        // Phase 4 polish: red `!` icon for conflict overlays + the FRG Version Control window.
        // Reuses Unity's built-in console.erroricon (already red, white `!` glyph).
        private static Texture2D s_ConflictIcon;
        // FRG Version Control: dedicated override-active overlay icon (pencil-on-paper),
        // shown whenever IsInOverrideMode regardless of lock state. Replaces the previous
        // amber-tinted-lock-icon workaround, which only fired for the locked-by-other case.
        private static Texture2D s_OverrideIcon;
        private static GUIStyle s_IconStyle;
        // Meta-lock badge style. Copy-constructed from EditorStyles.miniBoldLabel so we can
        // mutate normal.textColor per-render without disturbing Unity's shared instance.
        // Used to draw the "M" glyph beside the lock icon when the asset's .meta is locked.
        private static GUIStyle s_MetaBadgeStyle;

        // Phase 4 polish: SVN conflict sidecars (Foo.png.r39, Foo.png.mine, etc.) appear in
        // the Project window during a conflict and confuse artists. We can't truly hide them
        // (Unity ignores OS Hidden flag; renaming breaks SVN's resolve path; moving outside
        // Assets/ breaks external resolution). Instead: dim the row + tooltip explaining
        // they're transient and auto-clean on resolve. Cached static-readonly to avoid
        // per-repaint allocation in OnProjectWindowItemGUI.
        private static readonly GUIContent s_SidecarTooltip = new GUIContent(
            string.Empty,
            "SVN conflict sidecar (transient). Resolve the conflict via " +
            "Window → FRG Version Control → Changes tab to remove.");

        // Global cache for git status - event-driven, no expiration timer
        private static System.Collections.Generic.Dictionary<string, SvnFileStatus> s_GlobalSvnStatusCache;
        private static bool s_CacheInitialized = false;

        static FileLockProjectWindowOverlay()
        {
            // Load built-in lock icon
            s_LockIcon = EditorGUIUtility.FindTexture("AssemblyLock");

            if (s_LockIcon == null)
            {
                Debug.LogError("[FRGVersionControl] Failed to load AssemblyLock icon! Icon will not display.");
            }

            // Load git status icons
            s_UntrackedIcon = EditorGUIUtility.FindTexture("Toolbar Plus");
            s_ModifiedIcon = EditorGUIUtility.FindTexture("TestPassed");

            if (s_UntrackedIcon == null)
            {
                Debug.LogWarning("[FRGVersionControl] Failed to load Toolbar Plus icon for untracked files.");
            }

            if (s_ModifiedIcon == null)
            {
                Debug.LogWarning("[FRGVersionControl] Failed to load TestPassed icon for modified files.");
            }

            // Phase 4 polish: red `!` for conflict states. Try the small variant first
            // (matches the overlay's typical 16-px size); fall back to the full-size variant.
            var conflictContent = EditorGUIUtility.IconContent("console.erroricon.sml");
            if (conflictContent == null || conflictContent.image == null)
            {
                conflictContent = EditorGUIUtility.IconContent("console.erroricon");
            }
            s_ConflictIcon = conflictContent?.image as Texture2D;
            if (s_ConflictIcon == null)
            {
                Debug.LogWarning("[FRGVersionControl] Failed to load console.erroricon for conflict overlay.");
            }

            // FRG Version Control: override-active overlay. CollabEdit Icon is a pencil-on-paper
            // glyph that reads as "edits pending". Fallback to console.warnicon.sml (yellow `!`)
            // if the named icon isn't found in this Unity version — visual distinction from the
            // red `!` conflict icon is preserved via the amber-tint applied at draw time.
            var overrideContent = EditorGUIUtility.IconContent("CollabEdit Icon");
            if (overrideContent == null || overrideContent.image == null)
            {
                overrideContent = EditorGUIUtility.IconContent("console.warnicon.sml");
            }
            s_OverrideIcon = overrideContent?.image as Texture2D;
            if (s_OverrideIcon == null)
            {
                Debug.LogWarning("[FRGVersionControl] Failed to load CollabEdit Icon for ignore-on-commit overlay.");
            }

            // Initialize icon style
            s_IconStyle = new GUIStyle();
            s_IconStyle.alignment = TextAnchor.MiddleCenter;

            // NOTE: s_MetaBadgeStyle is lazy-initialized on first OnGUI call (see
            // EnsureMetaBadgeStyle). EditorStyles.miniBoldLabel is NOT yet populated when
            // this [InitializeOnLoad] cctor fires — accessing it here yields null and
            // `new GUIStyle(null)` NREs, which cascades into the rest of the cctor failing
            // and breaks RefreshLockCache for the session.

            // Initialize git status cache
            s_GlobalSvnStatusCache = new System.Collections.Generic.Dictionary<string, SvnFileStatus>();

            // Register callbacks
            EditorApplication.projectWindowItemOnGUI -= OnProjectWindowItemGUI;
            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;

            // Initialize git status cache on startup
            RefreshGlobalSvnStatusCache();
        }

        private static void OnProjectWindowItemGUI(string guid, Rect selectionRect)
        {
            // Performance optimization: only draw during repaint
            if (Event.current.type != EventType.Repaint)
                return;

            // Skip when application is playing
            if (Application.isPlaying)
                return;

            // Skip if FileLockManager is not initialized
            if (!FileLockManager.Instance.IsInitialized)
                return;

            // Convert GUID to asset path
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(assetPath))
                return;

            // SVN conflict sidecars get a dim wash + tooltip and skip the rest of the overlay
            // pipeline. Sidecars can't legitimately be locked or modified, so suppressing the
            // lock/git-status branches below is correct. Regex is RegexOptions.Compiled —
            // negligible per-row cost.
            if (SvnStatusCache.IsSvnConflictSidecar(assetPath))
            {
                DrawSidecarMarker(selectionRect);
                return;
            }

            // Skip folders
            if (string.IsNullOrEmpty(Path.GetExtension(assetPath)))
                return;

            // Asset-side lock state
            var fileLock = FileLockManager.Instance.GetFileLock(assetPath);
            bool isLocked = fileLock != null;
            bool lockedByMe = FileLockManager.Instance.IsLockedByMe(assetPath);

            // Meta-side lock state — keyed independently from the asset. Two FindLock calls
            // per row per repaint; FileLocks.FindLock is linear today but negligible at
            // typical N (≤200 active locks). Promote to a Dictionary index if profiling shows
            // pressure. Pair-aware locking is explicitly NOT done — these locks stay
            // independent; the dual indicator is what makes partial state legible.
            string metaPath = assetPath + ".meta";
            var metaLock = FileLockManager.Instance.GetFileLock(metaPath);
            bool isMetaLocked = metaLock != null;
            bool metaLockedByMe = FileLockManager.Instance.IsLockedByMe(metaPath);

            // Phase 2 Stage 4.1: detect override-active state. Backed by in-memory
            // HashSet in FileLockOverrideManager — safe to call per-frame.
            bool isOverrideActive = FileLockOverrideManager.Instance.IsInOverrideMode(assetPath);

            // Check SVN status for both halves of the asset/meta pair. The status glyph fires
            // when EITHER side has uncommitted changes — a meta-only modification (e.g.,
            // Inspector compression change) still surfaces, with a tooltip noting it's the
            // .meta that's dirty. Mirrors the meta-aware lock display.
            SvnFileStatus assetStatus = GetCachedSvnStatus(assetPath);
            SvnFileStatus metaStatus = GetCachedSvnStatus(metaPath);

            // Override icon already conveys "this row has local edits" — don't double-code it
            // with the Modified status glyph. Higher-severity statuses (Conflict, Missing,
            // Deleted, Replaced) pass through because they signal something the override icon
            // doesn't (a merge conflict still needs resolving even if the file is in override).
            SvnFileStatus assetStatusForGlyph = (isOverrideActive && assetStatus == SvnFileStatus.Modified)
                ? SvnFileStatus.None : assetStatus;
            SvnFileStatus metaStatusForGlyph = (isOverrideActive && metaStatus == SvnFileStatus.Modified)
                ? SvnFileStatus.None : metaStatus;
            bool hasSvnStatus = assetStatusForGlyph != SvnFileStatus.None
                              || metaStatusForGlyph != SvnFileStatus.None;

            // Phase 4 polish: faint red row wash for conflicts. Either-side conflict triggers
            // the wash so a meta-only conflict (rare but possible — e.g., concurrent import
            // settings tweaks) is also unmissable. Painted BEFORE icons so the `!` lands on top.
            // Conflict wash uses the RAW status (not the override-suppressed one) — a conflict
            // on an override file still needs the row-level signal.
            if (assetStatus == SvnFileStatus.Conflict || metaStatus == SvnFileStatus.Conflict)
            {
                EditorGUI.DrawRect(selectionRect, new Color(0.85f, 0.2f, 0.2f, 0.18f));
            }

            // Top-right cluster: [override?][svn-status?][meta-badge?][lock?]. Ordered left→right
            // so the lock glyph stays anchored to the row's right edge (consistent with the
            // previous single-icon convention; users' eyes already track lock state to the far
            // right). Override sits at the leftmost cluster slot so adding/removing it doesn't
            // shift the lock anchor.
            int slotCount = (isOverrideActive ? 1 : 0) + (hasSvnStatus ? 1 : 0)
                          + (isMetaLocked ? 1 : 0) + (isLocked ? 1 : 0);
            if (slotCount > 0)
            {
                Rect[] rects = BuildTopRightCluster(selectionRect, slotCount);
                int i = 0;
                if (isOverrideActive) DrawOverrideMarkerAtRect(rects[i++]);
                if (hasSvnStatus)     DrawSvnStatusIconAtRect(rects[i++], assetStatusForGlyph, metaStatusForGlyph);
                if (isMetaLocked)     DrawMetaBadgeAtRect(rects[i++], metaLock, metaLockedByMe, fileLock, lockedByMe);
                if (isLocked)         DrawLockIconAtRect(rects[i++], fileLock, lockedByMe, isOverrideActive, metaLock, metaLockedByMe);
            }
        }

        private static void DrawOverrideMarkerAtRect(Rect iconRect)
        {
            if (s_OverrideIcon == null) return;

            GUI.color = new Color(1f, 0.8f, 0.2f, 1f); // amber
            GUI.Label(iconRect, new GUIContent(s_OverrideIcon,
                "Ignore-on-commit active — your edits are excluded from commits. " +
                "Use Window → FRG Version Control → Changes tab to clear."));
            GUI.color = Color.white;
        }

        // Translucent grey wash + tooltip-only label covering an SVN sidecar row. The wash
        // signals "ignore this" without breaking layout (DrawRect paints AFTER Unity has
        // rendered the row content during repaint, so the existing label/icon remain visible
        // but dimmed). GUI.Label with GUIStyle.none is the canonical tooltip-dispatch pattern
        // used elsewhere in this file (see DrawLockIconAtRect / DrawSvnStatusIconAtRect).
        private static void DrawSidecarMarker(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.1f, 0.1f, 0.1f, 0.55f));
            GUI.Label(rect, s_SidecarTooltip, GUIStyle.none);
        }

        /// <summary>
        /// Force repaint of Project window (call when lock status changes)
        /// </summary>
        public static void RepaintProjectWindow()
        {
            // 0.2.55 — EditorApplication.RepaintProjectWindow must be called from the main
            // thread. With the IPC rewires in 0.2.40-0.2.54, RefreshLockCache can now be
            // invoked from Task.Run continuations (FileLockManager via the IPC await chain),
            // so we marshal the Unity API call onto EditorApplication.delayCall which runs
            // on the next editor tick — guaranteed main thread.
            try
            {
                RefreshGlobalSvnStatusCache();
                EditorApplication.delayCall += () =>
                {
                    try { EditorApplication.RepaintProjectWindow(); }
                    catch { /* defensive — editor torn down mid-tick */ }
                };
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[FRGVersionControl] RepaintProjectWindow deferred-call failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Get cached git status for a file
        /// </summary>
        private static SvnFileStatus GetCachedSvnStatus(string assetPath)
        {
            // Initialize cache if not done yet
            if (!s_CacheInitialized)
            {
                RefreshGlobalSvnStatusCache();
            }

            // Lookup in global cache
            if (s_GlobalSvnStatusCache.TryGetValue(assetPath, out SvnFileStatus status))
            {
                return status;
            }

            // File not in cache means it has no uncommitted changes
            return SvnFileStatus.None;
        }

        private static bool s_RefreshScheduled;

        /// <summary>Coalesces overlay cache refreshes to one per editor tick. Unity invokes
        /// OnWillSaveAssets once per asset while it generates .meta files during a reimport, so
        /// a synchronous refresh there ran one full `svn status` per imported file.</summary>
        public static void ScheduleRefreshGlobalSvnStatusCache()
        {
            if (s_RefreshScheduled) return;
            s_RefreshScheduled = true;
            EditorApplication.delayCall += () =>
            {
                s_RefreshScheduled = false;
                try { RefreshGlobalSvnStatusCache(); }
                catch (System.Exception ex) { Debug.LogWarning($"[FRGVersionControl] deferred status refresh failed: {ex.Message}"); }
            };
        }

        /// <summary>
        /// Refresh the global git status cache by running a single git status command
        /// This is called automatically on startup and when RepaintProjectWindow() is called
        /// </summary>
        public static void RefreshGlobalSvnStatusCache()
        {
            // Get all file statuses in one batch operation (Phase 2 Step F: SVN backend)
            var rawSvn = SvnCommandRunner.GetAllFileStatuses();
            if (rawSvn == null)
            {
                s_CacheInitialized = true;
                return;
            }
            // SVN emits paths with backslashes on Windows; Unity asset paths use forward slashes.
            // Normalize at insert time so cache lookups by Unity asset path match.
            s_GlobalSvnStatusCache = new System.Collections.Generic.Dictionary<string, SvnFileStatus>(rawSvn.Count);
            foreach (var kv in rawSvn)
            {
                var key = (kv.Key ?? "").Replace('\\', '/');
                s_GlobalSvnStatusCache[key] = kv.Value;
            }
            s_CacheInitialized = true;
        }

        /// <summary>
        /// Get cached git status for a file without triggering a git command
        /// Returns true if file has uncommitted changes (Modified or Untracked)
        /// </summary>
        public static bool HasCachedUncommittedChanges(string assetPath)
        {
            if (!s_CacheInitialized)
            {
                RefreshGlobalSvnStatusCache();
            }

            if (s_GlobalSvnStatusCache.TryGetValue(assetPath, out SvnFileStatus status))
            {
                return status == SvnFileStatus.Modified || status == SvnFileStatus.Untracked;
            }

            return false; // Not in cache = no changes
        }

        /// <summary>
        /// Build a horizontal cluster of N rects anchored to the right edge (list view) or
        /// bottom-right corner (icon view) of <paramref name="rect"/>. Used by
        /// OnProjectWindowItemGUI to lay out the [svn-status?][meta?][lock?] glyph cluster
        /// with consistent spacing regardless of how many slots are populated.
        /// </summary>
        private static Rect[] BuildTopRightCluster(Rect rect, int slotCount)
        {
            if (slotCount <= 0) return System.Array.Empty<Rect>();

            bool isListView = rect.height <= 20;
            float iconSize = isListView ? 16f : Mathf.Min(16f, rect.height / 3.5f);
            const float spacing = 2f;

            float clusterWidth = slotCount * iconSize + (slotCount - 1) * spacing;
            float rightPadding = isListView ? 4f : 2f;
            float startX = rect.x + rect.width - clusterWidth - rightPadding;
            float y = isListView
                ? rect.y + (rect.height - iconSize) / 2f
                : rect.y + rect.height - iconSize - 2f;

            var rects = new Rect[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                rects[i] = new Rect(startX + i * (iconSize + spacing), y, iconSize, iconSize);
            }
            return rects;
        }

        /// <summary>
        /// Draw the SVN status icon at <paramref name="iconRect"/>. The effective status is
        /// the asset's status when non-None, otherwise the meta's status — so meta-only
        /// modifications (Inspector compression changes, etc.) still surface a glyph. The
        /// tooltip composes both sides so the user can distinguish "asset modified" from
        /// "only meta modified" from "both modified" without hovering twice.
        /// </summary>
        private static void DrawSvnStatusIconAtRect(Rect iconRect, SvnFileStatus assetStatus, SvnFileStatus metaStatus = SvnFileStatus.None)
        {
            // Asset takes priority for the icon choice — when both are dirty, the asset's
            // status is the more visually-primary one (artists are looking at the asset, not
            // the .meta). When only the meta has status, the meta's icon is what we render.
            bool useMetaForIcon = assetStatus == SvnFileStatus.None && metaStatus != SvnFileStatus.None;
            SvnFileStatus effective = useMetaForIcon ? metaStatus : assetStatus;

            Texture2D icon = null;
            Color iconColor = Color.white;
            string tooltip = "";

            if (effective == SvnFileStatus.Untracked)
            {
                icon = s_UntrackedIcon;
                iconColor = new Color(1f, 0.6f, 0f, 1f); // Orange
                tooltip = "New file (not committed)";
            }
            else if (effective == SvnFileStatus.Added)
            {
                // Added = newly tracked (post `svn add` or `svn move` destination). Reuse the
                // plus glyph from Untracked but tint green to signal "ready to commit".
                icon = s_UntrackedIcon;
                iconColor = new Color(0.3f, 1f, 0.4f, 1f); // Green
                tooltip = "Added (scheduled for commit — new file or move destination)";
            }
            else if (effective == SvnFileStatus.Modified)
            {
                icon = s_ModifiedIcon;
                iconColor = new Color(0.3f, 0.7f, 1f, 1f); // Light blue
                tooltip = "Modified (uncommitted changes)";
            }
            else if (effective == SvnFileStatus.Replaced)
            {
                // Replaced = scheduled-delete + scheduled-add at the same path. Show like
                // Modified-ish but with the green-add tint so it stands out as more than a
                // simple edit.
                icon = s_ModifiedIcon;
                iconColor = new Color(0.3f, 1f, 0.4f, 1f); // Green
                tooltip = "Replaced (scheduled delete + add at this path)";
            }
            else if (effective == SvnFileStatus.Deleted)
            {
                // Deleted = scheduled for removal at next commit. Reuse the plus glyph
                // tinted red — visually a "minus-like" silhouette by inversion.
                icon = s_UntrackedIcon;
                iconColor = new Color(1f, 0.3f, 0.3f, 1f); // Red
                tooltip = "Deleted (scheduled for commit — file removed)";
            }
            else if (effective == SvnFileStatus.Missing)
            {
                // Missing = file gone on disk, still tracked in wc.db (external mv/rm).
                // Reuse the conflict `!` icon tinted yellow so it reads "warning" not
                // "error" — distinct from the red `!` we use for real conflicts.
                icon = s_ConflictIcon;
                iconColor = new Color(1f, 0.85f, 0.2f, 1f); // Yellow
                tooltip = "Missing — file gone from disk but still tracked. Open Window → FRG Version Control → Changes tab to resolve.";
            }
            else if (effective == SvnFileStatus.Conflict)
            {
                // Phase 4 polish: red `!` (Unity's console.erroricon — already red, white glyph).
                // Distinct silhouette from the blue checkmark used for Modified rows; harder to
                // miss in a Project window full of files. No tint needed; icon is already red.
                icon = s_ConflictIcon;
                iconColor = Color.white;
                tooltip = "Merge conflict — open Window → FRG Version Control → Changes tab";
            }

            // Compose a meta-aware tooltip suffix so hovering this glyph alone tells the user
            // the FULL partial-pair state — analogous to the lock/meta-badge cross-tooltips.
            if (useMetaForIcon)
            {
                // The glyph is rendering the meta's status — clarify which side is dirty.
                tooltip += "\n(on the .meta file — asset itself is unchanged)";
            }
            else if (metaStatus != SvnFileStatus.None)
            {
                // Asset has status AND meta has status — append a one-liner about the meta.
                tooltip += $"\nMeta file also {DescribeStatusVerb(metaStatus)}.";
            }

            if (icon != null)
            {
                GUI.color = iconColor;
                GUI.Label(iconRect, new GUIContent(icon, tooltip));
                GUI.color = Color.white;
            }
        }

        /// <summary>
        /// Short verb phrase for the cross-aware status tooltip — used when both asset and
        /// meta have status so the secondary side can be named tersely (e.g.,
        /// "Meta file also modified.", "Meta file also in conflict.").
        /// </summary>
        private static string DescribeStatusVerb(SvnFileStatus s)
        {
            switch (s)
            {
                case SvnFileStatus.Modified:  return "modified";
                case SvnFileStatus.Untracked: return "untracked";
                case SvnFileStatus.Added:     return "added (scheduled for commit)";
                case SvnFileStatus.Deleted:   return "deleted (scheduled for commit)";
                case SvnFileStatus.Replaced:  return "replaced";
                case SvnFileStatus.Missing:   return "missing from disk";
                case SvnFileStatus.Conflict:  return "in conflict";
                default:                      return "changed";
            }
        }

        /// <summary>
        /// Draw lock icon at specific rect. When <paramref name="metaLock"/> is non-null the
        /// tooltip appends a cross-reference line so a user hovering the asset's lock glyph
        /// also sees that the meta is locked (and by whom) — critical for understanding
        /// partial-pair state, where asset and .meta can be independently held by different
        /// users.
        /// </summary>
        private static void DrawLockIconAtRect(Rect iconRect, FileLock fileLock, bool lockedByMe,
                                               bool isOverrideActive = false,
                                               FileLock metaLock = null, bool metaLockedByMe = false)
        {
            if (s_LockIcon == null)
                return;

            // Phase 2 Stage 4.1 + closure Test 5: visual states.
            //   green   = locked-by-me (K)
            //   amber   = locked-by-other-with-override-active
            //   magenta = lock BROKEN (B) — your lock was force-released by an admin
            //   yellow  = lock STOLEN (T) — another user took your lock
            //   red     = locked-by-other (O, no override)
            Color iconColor;
            string tooltip;

            // B/T flags surface only when YOU previously held this lock and something happened.
            // They override the normal lockedByMe/lockedByOther rendering.
            if (fileLock.lockState == "B")
            {
                iconColor = new Color(1f, 0.3f, 0.9f, 1f); // magenta
                tooltip = $"Asset lock BROKEN (force-released by an admin).\n" +
                          $"Run `svn update` to clear the broken-lock flag, then reacquire if needed.";
            }
            else if (fileLock.lockState == "T")
            {
                iconColor = new Color(1f, 1f, 0.3f, 1f); // yellow
                tooltip = $"Asset lock STOLEN by {fileLock.ownerName}.\n" +
                          $"Run `svn update` to reconcile working-copy state.";
            }
            else if (lockedByMe)
            {
                iconColor = new Color(0.3f, 1f, 0.3f, 1f); // green
                tooltip = $"Asset locked by you\n{fileLock.GetLockedAtTimeLocal()}";
            }
            else
            {
                // Override-active state is now signaled by a SEPARATE overlay icon
                // (DrawOverrideMarker) — locked-by-other always renders plain red here.
                string staleIndicator = fileLock.IsStale() ? " (STALE)" : "";
                iconColor = new Color(1f, 0.3f, 0.3f, 1f); // red
                tooltip = $"Asset locked by {fileLock.ownerName} ({fileLock.owner}){staleIndicator}\n{fileLock.GetLockedAtTimeLocal()}";
            }

            // Cross-reference: when the .meta is also locked, append a clarifying line so
            // hovering this glyph alone tells the user the FULL partial-pair state. Asset
            // and meta locks are independent — both can be held by different users.
            if (metaLock != null)
            {
                string metaOwner = metaLockedByMe ? "you" : metaLock.ownerName;
                tooltip += $"\nMeta also locked by {metaOwner}.";
            }

            // Draw icon with color tint
            GUI.color = iconColor;
            GUI.Label(iconRect, new GUIContent(s_LockIcon, tooltip));
            GUI.color = Color.white;
        }

        /// <summary>
        /// Draw the "M" meta-lock badge at <paramref name="rect"/>. Mirrors
        /// DrawLockIconAtRect's 4-state color palette (green=mine, red=other, yellow=stolen,
        /// magenta=broken) but renders a text glyph instead of the AssemblyLock icon — a
        /// 16-px row can't fit "meta" legibly. When <paramref name="assetLock"/> is non-null,
        /// the tooltip appends a cross-reference line so the user can see the full
        /// partial-pair state on hover.
        /// </summary>
        private static void DrawMetaBadgeAtRect(Rect rect, FileLock metaLock, bool metaLockedByMe,
                                                FileLock assetLock = null, bool assetLockedByMe = false)
        {
            if (metaLock == null) return;

            // Lazy-init: EditorStyles.miniBoldLabel returns null during [InitializeOnLoad]
            // cctor firing, so the style HAS to be built on first OnGUI call (when editor
            // styles are populated). Cheap one-time path; null-check is the guard.
            if (s_MetaBadgeStyle == null)
            {
                s_MetaBadgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 9,
                    padding = new RectOffset(0, 0, 0, 0)
                };
            }

            Color badgeColor;
            string tooltip;

            if (metaLock.lockState == "B")
            {
                badgeColor = new Color(1f, 0.3f, 0.9f, 1f); // magenta
                tooltip = $"Meta lock BROKEN (force-released by an admin).\n" +
                          $"Run `svn update` to clear the broken-lock flag, then reacquire if needed.";
            }
            else if (metaLock.lockState == "T")
            {
                badgeColor = new Color(1f, 1f, 0.3f, 1f); // yellow
                tooltip = $"Meta lock STOLEN by {metaLock.ownerName}.\n" +
                          $"Run `svn update` to reconcile working-copy state.";
            }
            else if (metaLockedByMe)
            {
                badgeColor = new Color(0.3f, 1f, 0.3f, 1f); // green
                tooltip = $"Meta locked by you\n{metaLock.GetLockedAtTimeLocal()}";
            }
            else
            {
                string staleIndicator = metaLock.IsStale() ? " (STALE)" : "";
                badgeColor = new Color(1f, 0.3f, 0.3f, 1f); // red
                tooltip = $"Meta locked by {metaLock.ownerName} ({metaLock.owner}){staleIndicator}\n{metaLock.GetLockedAtTimeLocal()}\n" +
                          $"Coordinate before editing import settings.";
            }

            // Cross-reference: when the asset is also locked, append a clarifying line so
            // hovering this glyph alone tells the user the FULL partial-pair state.
            if (assetLock != null)
            {
                string assetOwner = assetLockedByMe ? "you" : assetLock.ownerName;
                tooltip += $"\nAsset also locked by {assetOwner}.";
            }

            // Tint via the style's textColor (not GUI.color) — the style instance is private
            // (copy-constructed from EditorStyles.miniBoldLabel) so mutating its normal.textColor
            // is safe and doesn't affect Unity's shared style.
            s_MetaBadgeStyle.normal.textColor = badgeColor;
            GUI.Label(rect, new GUIContent("M", tooltip), s_MetaBadgeStyle);
        }
    }
}
