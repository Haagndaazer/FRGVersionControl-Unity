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
using FRGVersionControl.Core;
using FRGVersionControl.Core.Changelists;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.OverrideMode;
using FRGVersionControl.Core.Shelves;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.Services;
using FRGVersionControl.Tools;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// FRG Version Control: unified Editor window combining Pending Changes (commit/update/revert
    /// + inline conflict resolution + inline override section) and Lock Manager (lock list,
    /// batch unlock, force unlock, auto-lock toggle). Replaces both the prior FileLockPendingChangesWindow
    /// and FileLockWindow with a 2-tab structure (Changes / Locks) and an always-visible footer.
    ///
    /// Cache-first: reads SvnStatusCache for status rows + FileLockManager for lock decoration.
    /// All network ops dispatch via SvnCommandRunner async wrappers (Step 0). UI never blocks.
    ///
    /// Lifecycle:
    /// - On Enable: subscribe to SvnStatusCache.SnapshotUpdated; restore active tab from EditorPrefs; trigger initial refresh.
    /// - On Disable: unsubscribe + cancel any in-flight async op.
    /// - On Repaint: render rows from current snapshot; show in-flight spinner if applicable.
    /// </summary>
    public class FrgVersionControlWindow : EditorWindow
    {
        // ----- Static menu hook -----
        [MenuItem("Window/FRG Version Control")]
        public static void Open()
        {
            var w = GetWindow<FrgVersionControlWindow>("FRG Version Control");
            w.minSize = new Vector2(640, 360);
            w.Show();
        }

        // ----- Tab state (EditorPrefs across-session) -----
        private enum Tab { Changes = 0, Locks = 1 }
        private const string TAB_PREFS_KEY = "FrgVersionControl.ActiveTab";
        private Tab m_ActiveTab;

        // ----- Per-row UI state -----
        // Phase 4 polish: rows are CLASSES (not structs) so we can mutate Texture2D
        // refs in-place without worrying about struct-copy-by-value bugs.
        private class Row
        {
            public string path;             // forward-slash normalized
            public SvnFileStatus status;
            public string changelist;       // SVN changelist membership; null if unassigned. `ignore-on-commit` for override rows.
            public string lockOwner;        // null if no lock
            public string lockState;        // "K", "O", "B", "T", or null
            public bool selected;
            public bool isExpanded;         // Conflict-row preview pair / Override-row metadata foldout
            public Texture2D minePreview;   // lazy-loaded on first expand (.mine sidecar)
            public Texture2D theirsPreview; // lazy-loaded on first expand (.r<theirs> sidecar)
            public bool isOverride;         // FRG: file is in active override workflow (ignore-on-commit)
        }

        // Slice 1: rows grouped by changelist for the Changes tab. Conflicts are surfaced
        // separately (always-visible top section); overrides reuse the existing amber section.
        // Any other changelist (user-created or "(no changelist)") renders as a foldout group
        // with per-group commit + gear menu.
        private const string NO_CHANGELIST_GROUP_NAME = "(no changelist)";
        private class RowGroup
        {
            public string changelistName;   // NO_CHANGELIST_GROUP_NAME / user changelist / ChangelistService.ReservedChangelistName
            public bool isReserved;         // true for `ignore-on-commit` — renders with amber treatment
            public bool isNoChangelist;     // true for the "(no changelist)" group
            public List<Row> rows = new List<Row>();
        }

        // ----- Conflict-row UX constants (migrated from FileLockConflictResolverWindow) -----
        // YAML-text extensions where TortoiseMerge is the right answer for "Open externally."
        private static readonly HashSet<string> s_TextConflictExts = new HashSet<string>(
            new[] { ".unity", ".prefab", ".mat", ".asset", ".controller", ".anim",
                    ".playable", ".lighting", ".mixer", ".inputactions", ".preset" },
            StringComparer.OrdinalIgnoreCase);

        // Tier-A texture extensions (Texture2D.LoadImage handles raw bytes).
        private static readonly HashSet<string> s_TextureExts = new HashSet<string>(
            new[] { ".png", ".jpg", ".jpeg", ".tga", ".exr", ".hdr" },
            StringComparer.OrdinalIgnoreCase);

        // Conflict icon (red `!`) — loaded once, reused per row.
        private static Texture2D s_ConflictIcon;

        private List<Row> m_Rows = new List<Row>();
        private Vector2 m_Scroll;
        private string m_CommitMessage = "";
        private CancellationTokenSource m_OpCts;
        private string m_StatusBanner = "";  // shown at the bottom for "Refreshing…" / "Committing 3 files…" / errors

        // Override-row selection lives in a SEPARATE set from r.selected (regular-change
        // selection). The two lists are independent: Commit Selected only touches regular
        // changes; Clear Selected Overrides only touches this set. Reset on Rebuild so
        // stale paths (resolved/converted overrides) don't linger.
        private HashSet<string> m_SelectedOverridePaths = new HashSet<string>();

        // Slice 1: per-changelist-group foldout state. Default expanded. Survives rebuilds.
        // Keyed by group name (NO_CHANGELIST_GROUP_NAME / user changelist name).
        private Dictionary<string, bool> m_GroupExpandedState = new Dictionary<string, bool>();

        // Slice 1: Changes-tab search/filter text. View filter only — never mutates rows.
        private const string CHANGES_SEARCH_PREFS_KEY = "FrgVersionControl.ChangesSearch";
        private string m_ChangesSearchFilter = "";

        // Slice 1: cleanup recovery banner. Set when an svn op surfaces E155004 / E155010 /
        // E155007 (wc.db lock contention). Banner offers one-click `svn cleanup` recovery.
        // Auto-cleared on the next successful refresh.
        private bool m_CleanupBannerNeeded = false;
        private bool m_CleanupInFlight = false;

        // Slice 1 follow-up: queue of user changelist names whose last file just committed.
        // Populated by DoCommitSelectedAsync, consumed by OnSnapshotUpdated (which fires after
        // the post-commit refresh). We defer the prompt until refresh so the user sees the
        // already-empty group before the dialog appears.
        private List<string> m_PendingEmptyChangelistPrompts = new List<string>();

        // Slice 2: shelves cache. Refreshed on demand (eagerly on OnEnable + after any
        // shelve mutation). As of Slice 5 (v0.2.22) each shelf renders as its own peer
        // section via DrawSection; there's no more wrapper foldout, so no parent expand-state
        // constant.
        private List<ShelfService.ShelfSummary> m_CachedShelves = new List<ShelfService.ShelfSummary>();
        private bool m_ShelvesCacheReady = false;

        // Slice 1 follow-up (post-0.2.13): inline-rename state for changelist group headers.
        // Replaces the modal ChangelistNamePromptWindow which hung the editor on multi-monitor
        // setups (ShowModalUtility opens off-screen on primary). When non-null, the group
        // header renders a TextField instead of a label; commit on Return or focus loss.
        private const string RENAME_FIELD_NAME = "FrgChangelistRenameField";
        private string m_RenamingGroupName;   // original name being renamed; null when not in rename mode
        private string m_RenamingText;        // working buffer for the typed name
        private bool m_RenameFieldFocusPending; // grab focus on the next OnGUI pass

        // Per-name "rename in progress" set. Populated by CommitInlineRename right after we
        // exit edit mode, cleared when the async SVN work lands. Drives the "(processing…)"
        // italic appendix next to the group label so the user sees that the field accepted
        // their Enter even while svn churns in the background.
        private HashSet<string> m_RenamesInFlight = new HashSet<string>(StringComparer.Ordinal);
        private static GUIStyle s_RenameProcessingStyle;

        // ----- Locks-tab state (migrated from FileLockWindow) -----
        [SerializeField] private Vector2 m_LocksScroll;
        [SerializeField] private string m_LocksSearchFilter = "";
        [SerializeField] private bool m_ShowOnlyMyLocks = true;
        [SerializeField] private bool m_ShowStaleLocks = true;
        // HashSet doesn't serialize; selection-loss on domain reload is acceptable
        // (matches prior FileLockWindow behavior).
        private HashSet<string> m_SelectedLockPaths = new HashSet<string>();
        // Color scheme for lock entries (migrated from FileLockWindow).
        private static readonly Color s_LockColorMine  = new Color(0.3f, 1f, 0.3f, 0.3f);
        private static readonly Color s_LockColorOther = new Color(1f, 1f, 1f, 1f);

        // ----- Footer cache-status state (migrated from FileLockWindow) -----
        private double m_CacheReadAt = 0;
        private long m_CachedCacheBytes = -1;
        private int m_CachedCacheCount = 0;

        // ----- Lifecycle -----
        private void OnEnable()
        {
            m_ActiveTab = (Tab)EditorPrefs.GetInt(TAB_PREFS_KEY, 0);
            m_ChangesSearchFilter = EditorPrefs.GetString(CHANGES_SEARCH_PREFS_KEY, "");
            SvnStatusCache.SnapshotUpdated += OnSnapshotUpdated;
            EnsureConflictIconLoaded();
            // Trigger an initial refresh so the panel populates on first open.
            // Fire-and-forget; OnSnapshotUpdated will repaint when ready.
            _ = SvnStatusCache.RefreshAsync();
            RebuildRowsFromCache();
            // Eagerly populate the shelves cache on enable so existing shelves show on the
            // first paint — fixes the pre-0.2.18 lazy-load gap where shelves only appeared
            // after the user created a new one (which forced a cache refresh).
            RefreshShelvesCache();
        }

        private void OnDisable()
        {
            SvnStatusCache.SnapshotUpdated -= OnSnapshotUpdated;
            // Phase 4 polish: destroy any lingering preview textures (Unity does NOT garbage-collect
            // Texture2D objects created via LoadImage — they leak unless DestroyImmediate'd).
            foreach (var r in m_Rows)
            {
                ClearRowPreviews(r);
            }
            if (m_OpCts != null)
            {
                try { m_OpCts.Cancel(); } catch { }
                m_OpCts.Dispose();
                m_OpCts = null;
            }
        }

        private static void EnsureConflictIconLoaded()
        {
            if (s_ConflictIcon != null) return;
            var content = EditorGUIUtility.IconContent("console.erroricon.sml");
            if (content == null || content.image == null)
            {
                content = EditorGUIUtility.IconContent("console.erroricon");
            }
            s_ConflictIcon = content?.image as Texture2D;
        }

        private void OnSnapshotUpdated()
        {
            // Slice 1: a successful refresh means the wc.db is responding cleanly, so the
            // cleanup-recovery banner has resolved itself implicitly.
            m_CleanupBannerNeeded = false;
            RebuildRowsFromCache();
            Repaint();

            // Slice 1 follow-up: surface delete-empty-changelist prompts queued by a prior
            // commit. We wait for the refresh-completion handler (this method) so the user
            // sees the empty group in the UI before the modal appears.
            if (m_PendingEmptyChangelistPrompts.Count > 0)
            {
                var pending = m_PendingEmptyChangelistPrompts;
                m_PendingEmptyChangelistPrompts = new List<string>();
                EditorApplication.delayCall += () => PromptDeleteDrainedChangelists(pending);
            }
        }

        /// <summary>Identify user changelists whose entire commit-eligible membership is part
        /// of the supplied selection — i.e., a successful commit will leave them with zero
        /// members. Excludes the reserved name and the "(no changelist)" bucket.</summary>
        private List<string> ComputeChangelistsBeingDrained(List<Row> rowsBeingCommitted)
        {
            var result = new List<string>();
            var pathsBeingCommitted = new HashSet<string>(
                rowsBeingCommitted.Select(r => r.path), StringComparer.Ordinal);

            var byChangelist = rowsBeingCommitted
                .Where(r => !string.IsNullOrEmpty(r.changelist)
                            && !string.Equals(r.changelist, ChangelistService.ReservedChangelistName,
                                              StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => r.changelist);

            foreach (var grp in byChangelist)
            {
                // Every row in m_Rows that's a commit-eligible member of this changelist.
                int totalEligible = m_Rows.Count(r =>
                    !r.isOverride
                    && r.status != SvnFileStatus.Untracked
                    && r.status != SvnFileStatus.Conflict
                    && string.Equals(r.changelist, grp.Key, StringComparison.Ordinal));
                int beingCommitted = grp.Count();
                if (totalEligible > 0 && totalEligible == beingCommitted)
                {
                    result.Add(grp.Key);
                }
            }
            return result;
        }

        private void PromptDeleteDrainedChangelists(List<string> names)
        {
            foreach (var name in names)
            {
                // Re-verify against the freshly-rebuilt m_Rows in case something changed
                // since the snapshot. If any commit-eligible row still has this changelist
                // tag, skip the prompt — something else (manual move, parallel op) re-populated
                // it during the refresh window.
                bool stillEmpty = !m_Rows.Any(r =>
                    string.Equals(r.changelist, name, StringComparison.Ordinal));
                if (!stillEmpty) continue;

                bool shouldDelete = EditorUtility.DisplayDialog(
                    "Delete empty changelist?",
                    $"Changelist '{name}' has no remaining files after the commit. " +
                    "Delete the empty bucket, or keep it for ongoing work?",
                    "Delete", "Keep");
                if (shouldDelete)
                {
                    var v = ChangelistService.Delete(name, ChangelistService.DeleteMode.RemoveOnlyIfEmpty);
                    if (v.ok)
                    {
                        m_StatusBanner = $"Deleted empty changelist '{name}'.";
                        m_GroupExpandedState.Remove(name);
                    }
                    else
                    {
                        m_StatusBanner = $"Couldn't delete '{name}': {v.reason}";
                    }
                    Repaint();
                }
            }
        }

        private void RebuildRowsFromCache()
        {
            // Preserve selection + expansion state + preview textures across rebuilds.
            // Rows whose path no longer appears in the new snapshot get their textures destroyed.
            var oldByPath = m_Rows.ToDictionary(r => r.path, r => r);

            var snapshot = SvnStatusCache.SnapshotAll();
            var lockMgr = FileLockManager.Instance;
            var overrideMgr = FileLockOverrideManager.Instance;
            var newPaths = new HashSet<string>(snapshot.Select(kv => kv.Key));

            // Texture-lifetime cleanup: rows abandoned in the rebuild lose their Texture2D refs.
            foreach (var oldRow in m_Rows)
            {
                if (!newPaths.Contains(oldRow.path))
                {
                    ClearRowPreviews(oldRow);
                }
            }

            m_Rows = snapshot.Select(kv =>
            {
                var fl = lockMgr != null && lockMgr.IsInitialized ? lockMgr.GetFileLock(kv.Key) : null;
                bool isOverride = overrideMgr != null && overrideMgr.IsInOverrideMode(kv.Key);
                Row row;
                if (oldByPath.TryGetValue(kv.Key, out var prev))
                {
                    // Preserve existing row's selection + expansion + textures.
                    row = prev;
                    row.status = kv.Value.status;
                    row.changelist = kv.Value.changelist;
                    row.lockOwner = fl?.owner;
                    row.lockState = fl?.lockState;
                    row.isOverride = isOverride;
                    // If status is no longer Conflict, drop the preview textures (the file resolved).
                    // (Override-row metadata foldouts don't use minePreview/theirsPreview, so this
                    // is conflict-specific.)
                    if (kv.Value.status != SvnFileStatus.Conflict)
                    {
                        ClearRowPreviews(row);
                        if (!isOverride) row.isExpanded = false;
                    }
                }
                else
                {
                    row = new Row
                    {
                        path = kv.Key,
                        status = kv.Value.status,
                        changelist = kv.Value.changelist,
                        lockOwner = fl?.owner,
                        lockState = fl?.lockState,
                        selected = false,
                        isExpanded = false,
                        isOverride = isOverride
                    };
                }
                return row;
            }).ToList();

            // FRG: union override registry into the row list. `svn status` does NOT report
            // clean-but-in-changelist files (verified empirically — the prior "impossible by
            // construction" assumption was wrong). An override-active file that hasn't been
            // edited yet won't appear in the snapshot at all, so we need to add synthetic rows
            // for any override path missing from the snapshot.
            var rowPaths = new HashSet<string>(m_Rows.Select(r => r.path));
            if (overrideMgr != null)
            {
                foreach (var entry in overrideMgr.GetAllOverrides())
                {
                    if (rowPaths.Contains(entry.path)) continue;
                    var fl = lockMgr != null && lockMgr.IsInitialized ? lockMgr.GetFileLock(entry.path) : null;
                    Row syntheticRow;
                    if (oldByPath.TryGetValue(entry.path, out var prev))
                    {
                        syntheticRow = prev;
                        syntheticRow.status = SvnFileStatus.None;
                        syntheticRow.lockOwner = fl?.owner;
                        syntheticRow.lockState = fl?.lockState;
                        syntheticRow.isOverride = true;
                    }
                    else
                    {
                        syntheticRow = new Row
                        {
                            path = entry.path,
                            status = SvnFileStatus.None,
                            lockOwner = fl?.owner,
                            lockState = fl?.lockState,
                            selected = false,
                            isExpanded = false,
                            isOverride = true
                        };
                    }
                    m_Rows.Add(syntheticRow);
                }
            }

            // Prune the override-selection set: drop any paths no longer in the override set
            // (resolved / cleared / converted-to-lock since last rebuild).
            var overridePathsNow = new HashSet<string>(m_Rows.Where(r => r.isOverride).Select(r => r.path));
            m_SelectedOverridePaths.RemoveWhere(p => !overridePathsNow.Contains(p));

            // Sort tiers: Conflicts (0) > Modified/Added/Deleted/Replaced (1) > Untracked (2)
            // > Overrides (3, pushed to bottom) > else (4). Overrides live below regular
            // changes — they're "stashed work" semantically, not active edits to commit.
            // Alphabetical within tier.
            m_Rows.Sort((a, b) =>
            {
                int aTier = RowTier(a), bTier = RowTier(b);
                if (aTier != bTier) return aTier.CompareTo(bTier);
                return string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static int RowTier(Row r)
        {
            if (r.status == SvnFileStatus.Conflict) return 0;
            if (r.isOverride) return 3;
            return StatusTier(r.status);
        }

        /// <summary>Slice 1: partition the current rows into changelist groups for the grouped
        /// renderer. Conflicts are pulled out into a synthetic "conflicts" pseudo-group that
        /// always renders at the top. Override rows go into a synthetic group keyed by
        /// ChangelistService.ReservedChangelistName ("ignore-on-commit") so the existing amber
        /// section still owns rendering. Empty user-created changelists (from the sidecar)
        /// appear as drop targets even with zero current rows.</summary>
        private List<RowGroup> BuildRowGroups(out List<Row> conflictRows)
        {
            conflictRows = new List<Row>();
            var byName = new Dictionary<string, RowGroup>(StringComparer.Ordinal);

            // Seed groups so empty user changelists appear in the picker. Render path stays
            // cache-only: read the sidecar (cheap JSON I/O), NOT ChangelistService.GetAllChangelists
            // (which spawns `svn status --xml` synchronously and would freeze the UI every repaint).
            List<string> declared;
            try { declared = UserChangelistStore.ReadSnapshot(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FrgVersionControl] UserChangelistStore.ReadSnapshot threw: {ex.Message}");
                declared = new List<string>();
            }
            foreach (var name in declared)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (string.Equals(name, ChangelistService.ReservedChangelistName, StringComparison.OrdinalIgnoreCase)) continue;
                byName[name] = new RowGroup
                {
                    changelistName = name,
                    isReserved = false,
                    isNoChangelist = false,
                };
            }

            // Bin rows.
            foreach (var r in m_Rows)
            {
                if (r.status == SvnFileStatus.Conflict)
                {
                    conflictRows.Add(r);
                    continue;
                }
                if (r.isOverride)
                {
                    var key = ChangelistService.ReservedChangelistName;
                    if (!byName.TryGetValue(key, out var ovrGroup))
                    {
                        ovrGroup = new RowGroup
                        {
                            changelistName = key,
                            isReserved = true,
                            isNoChangelist = false,
                        };
                        byName[key] = ovrGroup;
                    }
                    ovrGroup.rows.Add(r);
                    continue;
                }
                var clName = string.IsNullOrEmpty(r.changelist) ? NO_CHANGELIST_GROUP_NAME : r.changelist;
                if (!byName.TryGetValue(clName, out var grp))
                {
                    grp = new RowGroup
                    {
                        changelistName = clName,
                        isReserved = false,
                        isNoChangelist = clName == NO_CHANGELIST_GROUP_NAME,
                    };
                    byName[clName] = grp;
                }
                grp.rows.Add(r);
            }

            // Always surface "(no changelist)" even when empty so the user sees the bucket exists.
            if (!byName.ContainsKey(NO_CHANGELIST_GROUP_NAME))
            {
                byName[NO_CHANGELIST_GROUP_NAME] = new RowGroup
                {
                    changelistName = NO_CHANGELIST_GROUP_NAME,
                    isReserved = false,
                    isNoChangelist = true,
                };
            }

            // Sort rows within each group by tier then path (same as the prior flat sort).
            foreach (var g in byName.Values)
            {
                g.rows.Sort((a, b) =>
                {
                    int aT = RowTier(a), bT = RowTier(b);
                    if (aT != bT) return aT.CompareTo(bT);
                    return string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase);
                });
            }

            // Order: (no changelist) first, then user changelists alphabetical, then reserved last.
            return byName.Values
                .OrderBy(g => g.isReserved ? 2 : (g.isNoChangelist ? 0 : 1))
                .ThenBy(g => g.changelistName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool IsGroupExpanded(string groupName)
        {
            if (!m_GroupExpandedState.TryGetValue(groupName, out var v)) return true;
            return v;
        }

        private bool MatchesSearch(Row r)
        {
            if (string.IsNullOrEmpty(m_ChangesSearchFilter)) return true;
            return r.path.IndexOf(m_ChangesSearchFilter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int StatusTier(SvnFileStatus s)
        {
            switch (s)
            {
                case SvnFileStatus.Conflict:  return 0;
                case SvnFileStatus.Modified:
                case SvnFileStatus.Added:
                case SvnFileStatus.Deleted:
                case SvnFileStatus.Replaced:
                case SvnFileStatus.Missing:   return 1; // External-tool removals — sort with regular changes
                case SvnFileStatus.Untracked: return 2;
                default:                      return 4;
            }
        }

        private static void ClearRowPreviews(Row r)
        {
            if (r == null) return;
            if (r.minePreview != null)   { UnityEngine.Object.DestroyImmediate(r.minePreview);   r.minePreview = null; }
            if (r.theirsPreview != null) { UnityEngine.Object.DestroyImmediate(r.theirsPreview); r.theirsPreview = null; }
        }

        // ----- GUI -----
        private void OnGUI()
        {
            // Tab toolbar at top
            int newTab = GUILayout.Toolbar((int)m_ActiveTab, new[] { "Changes", "Locks" });
            if (newTab != (int)m_ActiveTab)
            {
                m_ActiveTab = (Tab)newTab;
                EditorPrefs.SetInt(TAB_PREFS_KEY, newTab);
                m_SelectedLockPaths.Clear(); // selection state is per-tab; reset on switch
            }

            if (m_ActiveTab == Tab.Changes) DrawChangesTab();
            else                            DrawLocksTab();

            DrawCacheStatusBar(); // footer — visible on all tabs
        }

        private void DrawChangesTab()
        {
            DrawCleanupRecoveryBanner();
            DrawToolbar();
            DrawRows();
            // Shelf sections render inside DrawRows (inside the scroll view) so they sit
            // immediately after the override section visually, scrolling together.
            DrawCommitArea();
            DrawStatusBanner();
        }

        // Slice 5 (v0.2.22): shelves render as peer sections, not nested inside a wrapper
        // foldout. Each shelf is a sibling of the user changelist groups visually. The
        // block always renders its "Shelves (N) [Shelve…]" header so the create-shelf
        // entry point stays discoverable even when no shelves exist yet.
        private void DrawShelfSections()
        {
            if (!m_ShelvesCacheReady)
            {
                RefreshShelvesCache();
            }

            // Block title bar — always rendered. Bold "Shelves (N)" label + [Shelve…]
            // button left-aligned next to each other so the action stays prominent.
            // Disabled when there's nothing in the WC to shelve (or an op is in flight).
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"Shelves ({m_CachedShelves.Count})", EditorStyles.boldLabel,
                            GUILayout.ExpandWidth(false));
            using (new EditorGUI.DisabledScope(m_Rows.Count == 0 || IsOpInFlight()))
            {
                if (GUILayout.Button(new GUIContent("Shelve…",
                        "Save selected changes to a named shelf. Working copy is reverted to clean " +
                        "after a successful shelve (matches TortoiseSVN default)."),
                        GUILayout.Width(80)))
                {
                    OpenShelveDialog();
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            foreach (var s in m_CachedShelves)
            {
                var captured = s; // closure capture safety
                DrawSection(new SectionSpec
                {
                    expandKey = "$shelf/" + captured.name,
                    label = $"{captured.name} ({captured.fileCount} file{(captured.fileCount == 1 ? "" : "s")}, v{captured.latestVersion})",
                    subLabel = captured.logMessage,
                    defaultExpanded = false, // collapse by default; less clutter when many shelves exist
                    drawHeaderActions = () =>
                    {
                        using (new EditorGUI.DisabledScope(IsOpInFlight()))
                        {
                            if (GUILayout.Button(new GUIContent("Restore",
                                    "Restore the shelved changes back into the working copy. Conflicts " +
                                    "are surfaced before any file is overwritten."),
                                    EditorStyles.toolbarButton, GUILayout.Width(75)))
                            {
                                _ = DoApplyShelfAsync(captured.name);
                            }
                            if (GUILayout.Button(new GUIContent("Delete",
                                    "Permanently remove this shelf. Cannot be undone."),
                                    EditorStyles.toolbarButton, GUILayout.Width(70)))
                            {
                                DeleteShelfWithLockPrompt(captured);
                            }
                        }
                    },
                    drawBody = () =>
                    {
                        if (captured.files == null) return;
                        foreach (var f in captured.files)
                        {
                            EditorGUILayout.BeginHorizontal();
                            var spec = new FileRowSpec
                            {
                                path = f.relPath,
                                status = f.status,
                                lockOwner = null,
                                lockState = null,
                                isOverride = false,
                                isConflict = false,
                            };
                            DrawFileRowInner(in spec, selectable: null, onSelectChanged: null);
                            EditorGUILayout.EndHorizontal();
                        }
                    },
                });
            }
        }

        internal void RefreshShelvesCache()
        {
            try
            {
                m_CachedShelves = ShelfService.ListShelves();
                m_ShelvesCacheReady = true;
                Repaint();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FrgVersionControl] Refreshing shelves cache failed: {ex.Message}");
                m_CachedShelves = new List<ShelfService.ShelfSummary>();
                m_ShelvesCacheReady = true;
            }
        }

        private void OpenShelveDialog()
        {
            // Pre-populate the dialog with regular-row selection (if any), excluding
            // ineligible kinds. Empty selection means "everything eligible".
            var eligible = m_Rows.Where(r => !r.isOverride
                                             && r.status != SvnFileStatus.Untracked
                                             && r.status != SvnFileStatus.Missing
                                             && r.status != SvnFileStatus.Conflict
                                             && r.status != SvnFileStatus.Replaced)
                                 .Select(r => r.path).ToList();
            var initialSelected = m_Rows.Where(r => r.selected && !r.isOverride
                                                    && eligible.Contains(r.path))
                                       .Select(r => r.path).ToList();
            ShelveDialogWindow.Show(this, eligible, initialSelected);
        }

        /// <summary>Delete-shelf flow with a lock-release prompt. If the user still holds
        /// SVN locks on any of the shelved files, surface a 3-way dialog so they can release
        /// those locks at the same time — common case when discarding a shelf signals "done
        /// with this work entirely". When no locks are held, falls back to the 2-way confirm.</summary>
        private void DeleteShelfWithLockPrompt(ShelfService.ShelfSummary s)
        {
            var lockMgr = FileLockManager.Instance;
            var heldLockPaths = new List<string>();
            if (lockMgr != null && lockMgr.IsInitialized && s.files != null)
            {
                foreach (var f in s.files)
                {
                    var fl = lockMgr.GetFileLock(f.relPath);
                    if (fl != null && fl.lockState == "K")
                    {
                        heldLockPaths.Add(f.relPath);
                    }
                }
            }

            bool unlockToo = false;
            if (heldLockPaths.Count == 0)
            {
                if (!EditorUtility.DisplayDialog(
                        "Delete shelf",
                        $"Permanently delete shelf '{s.name}'? Cannot be undone.",
                        "Delete", "Cancel"))
                    return;
            }
            else
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Delete shelf",
                    $"Permanently delete shelf '{s.name}'?\n\n" +
                    $"You currently hold SVN locks on {heldLockPaths.Count} of the shelved file(s). " +
                    "Release those locks too, or keep them (e.g. if you'll re-shelve later)?",
                    $"Delete + Release {heldLockPaths.Count} lock(s)",  // 0
                    "Cancel",                                            // 1
                    "Delete only");                                      // 2
                if (choice == 1) return;
                unlockToo = (choice == 0);
            }

            if (!ShelfService.DeleteShelf(s.name, out var why))
            {
                EditorUtility.DisplayDialog("Delete failed", why ?? "Unknown error.", "OK");
                return;
            }

            if (unlockToo && heldLockPaths.Count > 0)
            {
                // Use the canonical batch-unlock path — it handles per-file svn unlock,
                // refreshes the lock cache, and repaints the Project window overlay
                // internally. skipRevertDialog=true because we don't need to prompt the
                // user about local changes (the shelf delete already discarded those
                // intentions). force=false — only the user's own locks should be released
                // through this path.
                if (lockMgr != null && lockMgr.IsInitialized)
                {
                    var batchResult = lockMgr.BatchUnlockFiles(heldLockPaths, force: false, skipRevertDialog: true);
                    m_StatusBanner = $"Deleted shelf '{s.name}' and released {batchResult.successCount} of {heldLockPaths.Count} lock(s).";
                }
                else
                {
                    m_StatusBanner = $"Deleted shelf '{s.name}'. (Lock release skipped — FileLockManager not initialized.)";
                }
            }
            else
            {
                m_StatusBanner = $"Deleted shelf '{s.name}'.";
            }
            RefreshShelvesCache();
        }

        internal async Task DoCreateShelfAsync(string name, string logMessage, string[] paths)
        {
            if (RefuseWhileExclusiveOpInFlight("shelving")) return;
            CancelAndStartCts();
            m_StatusBanner = $"Shelving {paths.Length} file(s) as '{name}'…";
            Repaint();
            var result = await ShelfService.CreateShelfAsync(name, logMessage, paths, m_OpCts.Token);
            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                if (result.ok)
                {
                    m_StatusBanner = result.reason != null
                        ? result.reason
                        : $"Shelved {result.filesShelved} file(s) as '{result.shelfName}' (v{result.version}).";
                    // The core reverted the shelved paths on disk — reimport so Unity drops
                    // the now-reverted bytes. (ShelfService is a pure IPC shim; the window owns
                    // all main-thread Unity-API work.)
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    RefreshShelvesCache();
                    _ = SvnStatusCache.RefreshAsync();
                    // Slice 3: Project-window overlay maintains its own cache (s_GlobalSvnStatusCache);
                    // SnapshotUpdated doesn't propagate to it. Without this call the M glyph stays on
                    // freshly-shelved (now-reverted) files until the next focus-change. Lock icons
                    // intentionally persist — shelve doesn't release locks (matches Tortoise design).
                    FileLockProjectWindowOverlay.RepaintProjectWindow();
                }
                else
                {
                    EditorUtility.DisplayDialog("Shelve failed", result.reason, "OK");
                }
                Repaint();
            }, null);
        }

        private async Task DoApplyShelfAsync(string name)
        {
            if (RefuseWhileExclusiveOpInFlight("restoring a shelf")) return;
            CancelAndStartCts();
            m_StatusBanner = $"Restoring shelf '{name}'…";
            Repaint();
            var result = await ShelfService.ApplyShelfAsync(name, ShelfService.ConflictPolicy.PromptPerFile, m_OpCts.Token);
            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                if (result.ok)
                {
                    // Auto-delete the shelf — once restored, it's served its purpose.
                    // Locks on the restored files are KEPT so the user can keep editing
                    // (matches the Delete-shelf flow's distinction: restore preserves locks,
                    // standalone delete prompts to release them).
                    if (ShelfService.DeleteShelf(name, out var why))
                    {
                        m_StatusBanner = $"Restored and deleted shelf '{name}' — {result.filesApplied} file(s).";
                    }
                    else
                    {
                        m_StatusBanner = $"Restored shelf '{name}' — {result.filesApplied} file(s). Auto-delete failed: {why}";
                    }
                    // Shelved bytes were written back into the WC — reimport.
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    RefreshShelvesCache();
                    _ = SvnStatusCache.RefreshAsync();
                    FileLockProjectWindowOverlay.RepaintProjectWindow();
                }
                else if (result.conflicts != null && result.conflicts.Count > 0)
                {
                    // MVP conflict UI: single dialog with overwrite-all-or-cancel choice.
                    // Per-file resolution panel is a future polish item.
                    var conflictList = string.Join("\n  ", result.conflicts.Take(10).Select(c => "• " + c.relPath + " — " + c.detail));
                    string extra = result.conflicts.Count > 10 ? $"\n  …and {result.conflicts.Count - 10} more" : "";
                    bool overwrite = EditorUtility.DisplayDialog(
                        "Shelf restore: conflicts",
                        $"{result.conflicts.Count} file(s) have changed since this shelf was created:\n\n  {conflictList}{extra}\n\n" +
                        "Overwrite the working-copy versions with the shelved bytes? Cannot be undone.",
                        "Overwrite mine", "Cancel");
                    if (overwrite)
                    {
                        _ = DoApplyShelfWithOverwriteAsync(name);
                    }
                    else
                    {
                        m_StatusBanner = "Shelf restore cancelled.";
                    }
                }
                else
                {
                    EditorUtility.DisplayDialog("Restore failed", result.reason, "OK");
                }
                Repaint();
            }, null);
        }

        private async Task DoApplyShelfWithOverwriteAsync(string name)
        {
            if (RefuseWhileExclusiveOpInFlight("restoring a shelf")) return;
            if (m_OpCts == null) CancelAndStartCts();
            var result = await ShelfService.ApplyShelfAsync(name, ShelfService.ConflictPolicy.OverwriteMine, m_OpCts.Token);
            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                if (result.ok)
                {
                    // Match the clean-restore path: auto-delete the shelf after a successful
                    // overwrite restore. Locks are kept (user is presumably about to keep
                    // editing the restored files).
                    if (ShelfService.DeleteShelf(name, out var why))
                    {
                        m_StatusBanner = $"Restored and deleted shelf '{name}' — {result.filesApplied} file(s) overwritten.";
                    }
                    else
                    {
                        m_StatusBanner = $"Restored shelf '{name}' — {result.filesApplied} file(s) overwritten. Auto-delete failed: {why}";
                    }
                    // Shelved bytes were written back into the WC — reimport.
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    RefreshShelvesCache();
                    _ = SvnStatusCache.RefreshAsync();
                    FileLockProjectWindowOverlay.RepaintProjectWindow();
                }
                else
                {
                    EditorUtility.DisplayDialog("Restore failed", result.reason ?? "Unknown error.", "OK");
                }
                Repaint();
            }, null);
        }

        /// <summary>Slice 1: cleanup recovery banner. Shows at the top of the Changes tab when
        /// a prior svn op surfaced E155004 / E155010 / E155007 (working-copy lock contention,
        /// typically caused by an interrupted op). One-click [Run Cleanup] resolves it.</summary>
        private void DrawCleanupRecoveryBanner()
        {
            if (!m_CleanupBannerNeeded) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(m_CleanupInFlight
                ? "Running svn cleanup…"
                : "⚠ Working copy may be locked from an interrupted SVN operation. " +
                  "Run cleanup to recover.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(m_CleanupInFlight))
            {
                if (GUILayout.Button(new GUIContent("Run Cleanup",
                        "Run `svn cleanup` on the working copy to release stale wc.db locks."),
                        GUILayout.Width(120)))
                {
                    _ = DoCleanupRecoveryAsync();
                }
                if (GUILayout.Button(new GUIContent("Dismiss",
                        "Hide this banner without running cleanup. It will reappear on the next E155004."),
                        GUILayout.Width(80)))
                {
                    m_CleanupBannerNeeded = false;
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private async Task DoCleanupRecoveryAsync()
        {
            if (RefuseWhileExclusiveOpInFlight("running cleanup")) return;
            CancelAndStartCts();
            m_CleanupInFlight = true;
            m_StatusBanner = "Running svn cleanup…";
            Repaint();
            try
            {
                // 0.2.41 — route through svn-core IPC. Falls back to legacy CLI if the IPC
                // binary is unavailable. Task #13 rewire — keeps the recovery banner UI flow
                // identical from the artist's perspective.
                var (success, error) = await RunCleanupViaIpcOrFallback(m_OpCts.Token).ConfigureAwait(false);
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    m_CleanupInFlight = false;
                    if (success)
                    {
                        m_CleanupBannerNeeded = false;
                        m_StatusBanner = "Cleanup completed. Working copy unlocked.";
                        _ = SvnStatusCache.RefreshAsync();
                    }
                    else
                    {
                        SetErrorBanner("Cleanup failed", error);
                    }
                    Repaint();
                }, null);
            }
            catch (OperationCanceledException)
            {
                m_CleanupInFlight = false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FrgVersionControl] Cleanup threw exception: {ex}");
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    m_CleanupInFlight = false;
                    SetErrorBanner("Cleanup failed", ex.Message);
                    Repaint();
                }, null);
            }
        }

        private static async Task<(bool success, string error)> RunCleanupViaIpcOrFallback(CancellationToken ct)
        {
            var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
            if (client.IsAvailable)
            {
                try
                {
                    var ctx = client.BuildDefaultContext();
                    var result = await client.InvokeRawAsync("maint.cleanup", "{}", ctx, ct).ConfigureAwait(false);
                    if (result.Ok) return (true, null);
                    // Surface the IPC-side error verbatim — defaultAction is already wrapped.
                    var msg = !string.IsNullOrEmpty(result.ErrorDefaultAction)
                        ? $"{result.ErrorMessage} — {result.ErrorDefaultAction}"
                        : result.ErrorMessage ?? result.ErrorKind ?? "IPC reported failure";
                    return (false, msg);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FrgVersionControl] IPC cleanup threw, falling back to legacy: {ex.Message}");
                }
            }
            // Legacy fallback — transitional until SvnCommandRunner is retired.
            var legacy = await SvnCommandRunner.CleanupAsync(SvnCommandRunner.ProjectRoot, ct);
            return (legacy.success, legacy.error);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button(new GUIContent("Refresh", "Re-read working-copy status from disk + server"),
                                 EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                _ = SvnStatusCache.RefreshAsync();
                m_StatusBanner = "Refreshing…";
            }
            if (GUILayout.Button(new GUIContent("Update", "svn update — pull upstream changes"),
                                 EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                _ = DoUpdateAsync();
            }
            if (GUILayout.Button(new GUIContent("Revert Selected",
                                                "Discard local changes for selected files. Cannot be undone."),
                                 EditorStyles.toolbarButton, GUILayout.Width(110)))
            {
                _ = DoRevertSelectedAsync();
            }
            // Revert & Unlock Selected — reverts for ALL selected, additionally releases the
            // lock for any selected row you own ("K" lock state). Disabled when none of the
            // selected rows are locked-by-you (in that state, plain Revert Selected does the
            // same thing — no need for a duplicate-behavior button).
            int selectedLockedByMeCount = m_Rows.Count(r => r.selected && !r.isOverride && r.lockState == "K");
            using (new EditorGUI.DisabledScope(selectedLockedByMeCount == 0))
            {
                if (GUILayout.Button(new GUIContent(
                        $"Revert & Unlock Selected ({selectedLockedByMeCount})",
                        "Discard local changes AND release the lock for selected rows you own. " +
                        "Non-locked selected files are simply reverted. Cannot be undone."),
                        EditorStyles.toolbarButton, GUILayout.Width(180)))
                {
                    _ = DoRevertAndUnlockSelectedAsync();
                }
            }
            // Add Selected to SVN — bulk-register untracked rows. Disabled when no Untracked
            // rows are selected (the typical case once import-time auto-add does its job).
            int selectedUntrackedCount = m_Rows.Count(r => r.selected && r.status == SvnFileStatus.Untracked);
            using (new EditorGUI.DisabledScope(selectedUntrackedCount == 0))
            {
                if (GUILayout.Button(new GUIContent(
                        $"Add Selected to SVN ({selectedUntrackedCount})",
                        "Bulk-register selected untracked files. Auto-pairs .meta siblings " +
                        "and sets svn:needs-lock for lockable extensions. Manual fallback when " +
                        "import-time auto-add couldn't register a file (e.g., svn:ignore'd folder)."),
                        EditorStyles.toolbarButton, GUILayout.Width(180)))
                {
                    _ = DoBulkAddToSvnAsync();
                }
            }
            GUILayout.FlexibleSpace();
            // Slice 1: view filter over the displayed rows + groups. Pure view filter — no
            // data mutation. Persisted across sessions via EditorPrefs.
            var newFilter = GUILayout.TextField(m_ChangesSearchFilter ?? "",
                                                EditorStyles.toolbarSearchField, GUILayout.Width(180));
            if (newFilter != m_ChangesSearchFilter)
            {
                m_ChangesSearchFilter = newFilter;
                EditorPrefs.SetString(CHANGES_SEARCH_PREFS_KEY, m_ChangesSearchFilter ?? "");
            }
            string lastRefreshTxt = SvnStatusCache.LastRefreshUtc == DateTime.MinValue
                ? "never"
                : SvnStatusCache.LastRefreshUtc.ToLocalTime().ToString("HH:mm:ss");
            GUILayout.Label($"Last refresh: {lastRefreshTxt}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            // Slice 1: secondary toolbar row for changelist-management actions. Keeps the
            // top toolbar focused on file-level operations (Refresh / Update / Revert /
            // Add) and breaks out organizational actions to a less-crowded second row.
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button(new GUIContent("+ Changelist",
                    "Create a new (empty) changelist. The group header becomes editable so you " +
                    "can type its name; Enter saves, Escape cancels."),
                    EditorStyles.toolbarButton, GUILayout.Width(110)))
            {
                CreateChangelistAndStartRename();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // Slice 5: modular section widget. Used by user changelist groups, the
        // Ignore-on-Commit section, and individual shelves so all three render identically.
        // Update DrawSection once, propagates everywhere.
        private struct SectionSpec
        {
            public string expandKey;
            public string label;               // used when drawLabel is null
            public Action drawLabel;            // optional custom label renderer (used by inline-rename)
            public string subLabel;
            public bool defaultExpanded;
            public Action<Rect> handleDrop;
            public Action drawHeaderActions;
            public Action drawBody;
        }

        private void DrawSection(SectionSpec spec)
        {
            var headerRect = EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            spec.handleDrop?.Invoke(headerRect);

            bool prev = m_GroupExpandedState.TryGetValue(spec.expandKey, out var v) ? v : spec.defaultExpanded;
            bool next = EditorGUILayout.Foldout(prev, GUIContent.none, true);
            if (next != prev) m_GroupExpandedState[spec.expandKey] = next;

            if (spec.drawLabel != null) spec.drawLabel();
            else GUILayout.Label(spec.label, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            spec.drawHeaderActions?.Invoke();
            EditorGUILayout.EndHorizontal();

            if (!next) return;

            if (!string.IsNullOrEmpty(spec.subLabel))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(20);
                var prevColor = GUI.color;
                GUI.color = new Color(prevColor.r, prevColor.g, prevColor.b, prevColor.a * 0.7f);
                EditorGUILayout.LabelField(spec.subLabel, EditorStyles.miniLabel);
                GUI.color = prevColor;
                EditorGUILayout.EndHorizontal();
            }

            spec.drawBody?.Invoke();
        }

        private void DrawRows()
        {
            if (m_Rows.Count == 0)
            {
                EditorGUILayout.HelpBox(SvnStatusCache.IsRefreshing
                    ? "Refreshing working-copy status…"
                    : "No pending changes. Working copy is clean.",
                    MessageType.Info);
                // Even with a clean WC, shelves should still be visible (restoring a shelf
                // back into a clean WC is a common workflow).
                m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
                DrawShelfSections();
                EditorGUILayout.EndScrollView();
                return;
            }

            int overrideCount = m_Rows.Count(r => r.isOverride && r.status != SvnFileStatus.Conflict);
            var groups = BuildRowGroups(out var conflictRows);
            int conflictCount = conflictRows.Count;

            // Phase 4 polish: conflict banner — impossible-to-miss top-of-rows callout.
            if (conflictCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"⚠ {conflictCount} file(s) need conflict resolution before any commit can proceed. " +
                    "Use Take Mine / Take Theirs / Open externally on each conflict row below.",
                    MessageType.Error);
            }

            // External-tool divergence reconciler: pair Missing+Untracked rows (likely renames)
            // and surface orphan Missing rows (external deletes). Both are produced when something
            // outside Unity (terminal mv/rm, agent-driven Bash, etc.) modified the working copy
            // without going through `svn move` / `svn rm`. Bulk-resolve buttons turn the divergence
            // into proper SVN tracking so the next commit isn't broken by E200009.
            DrawExternalDivergenceBanners();

            // Top select-all toolbar applies to REGULAR-CHANGE rows only (conflicts +
            // modified + untracked). Override rows have their own selection set and their
            // own All-toggle inside the Override section header below.
            var regularRows = m_Rows.Where(r => !r.isOverride).ToList();
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            int selectedRegular = regularRows.Count(r => r.selected);
            bool allRegularSelected = regularRows.Count > 0 && selectedRegular == regularRows.Count;
            bool newAllRegularSelected = EditorGUILayout.ToggleLeft(
                $"All ({selectedRegular}/{regularRows.Count})", allRegularSelected, GUILayout.Width(120));
            if (newAllRegularSelected != allRegularSelected)
            {
                foreach (var r in regularRows) r.selected = newAllRegularSelected;
            }
            GUILayout.Label("Status  File                              Folder                            Lock",
                            EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            // Track "did we render anything yet" to drive 15px spacers between section
            // TYPES (changelists / ignore-on-commit / shelves). No spacing within a type.
            bool somethingRendered = false;

            // Conflicts always render first, ungrouped — the conflict banner above directs
            // the user here. Counted as part of the "row list" type (no spacer between
            // conflicts and changelists).
            foreach (var r in conflictRows.Where(MatchesSearch))
            {
                DrawRow(r);
                somethingRendered = true;
            }

            // Non-reserved groups (sorted: (no changelist) → user changelists → reserved last).
            foreach (var grp in groups)
            {
                if (grp.isReserved)
                {
                    var overrideRowsToRender = grp.rows.Where(r => r.status != SvnFileStatus.Conflict).ToList();
                    if (overrideRowsToRender.Count == 0) continue;
                    if (somethingRendered) GUILayout.Space(15);
                    DrawOverrideSection(overrideRowsToRender);
                    somethingRendered = true;
                    continue;
                }

                // ALL groups — including the "(no changelist)" default — render their headers
                // even when empty so they remain valid drag-drop targets. Without this, after
                // moving every file out of the default group there'd be no way to drag a file
                // back into it.
                var visibleRows = grp.rows.Where(MatchesSearch).ToList();
                bool hasAnyHidden = grp.rows.Count > visibleRows.Count;

                bool expanded = DrawChangelistGroupHeader(grp, visibleRows.Count, hasAnyHidden);
                somethingRendered = true;
                if (expanded)
                {
                    foreach (var r in visibleRows)
                    {
                        DrawRow(r);
                    }
                }
            }

            // Shelf sections render inside the same scroll view, immediately after the
            // override section. The block always renders (so the [Shelve…] button stays
            // discoverable); 15px spacer above it only when there's content above to space
            // away from.
            if (somethingRendered) GUILayout.Space(15);
            DrawShelfSections();

            EditorGUILayout.EndScrollView();

            // Blur-commit for inline rename. After we've successfully focused the rename
            // field, any subsequent Repaint where the focused control is NOT ours means the
            // user clicked elsewhere — commit the rename. The m_RenameFieldFocusPending
            // gate ensures we don't fire on the very first frame before focus latches.
            if (m_RenamingGroupName != null
                && !m_RenameFieldFocusPending
                && Event.current.type == EventType.Repaint
                && GUI.GetNameOfFocusedControl() != RENAME_FIELD_NAME)
            {
                CommitInlineRename();
            }
        }

        /// <summary>Render a user changelist group via the shared <see cref="DrawSection"/>
        /// helper. Body iteration happens in the caller (in <see cref="DrawRows"/>) gated on
        /// whether the foldout is expanded — we mirror that decision here so the caller can
        /// branch without re-querying <see cref="m_GroupExpandedState"/>.
        /// Returns the expanded state so the caller can iterate rows when true.</summary>
        private bool DrawChangelistGroupHeader(RowGroup grp, int visibleCount, bool hasHidden)
        {
            bool isRenaming = !grp.isReserved && !grp.isNoChangelist
                              && m_RenamingGroupName != null
                              && string.Equals(m_RenamingGroupName, grp.changelistName, StringComparison.Ordinal);

            DrawSection(new SectionSpec
            {
                expandKey = grp.changelistName,
                defaultExpanded = true,
                handleDrop = rect => HandleGroupHeaderDrop(grp, rect),
                drawLabel = () => DrawChangelistGroupLabel(grp, visibleCount, hasHidden, isRenaming),
                drawHeaderActions = () => DrawChangelistGroupHeaderActions(grp),
                // Body iteration stays in DrawRows so it can apply the search filter once
                // outside the section helper; drawBody is intentionally null here.
                drawBody = null,
            });

            return IsGroupExpanded(grp.changelistName);
        }

        /// <summary>Renders either the bold "name (N)" label OR the inline-rename text field,
        /// plus the "(processing…)" italic suffix when an async rename is mid-flight.</summary>
        private void DrawChangelistGroupLabel(RowGroup grp, int visibleCount, bool hasHidden, bool isRenaming)
        {
            if (isRenaming)
            {
                // Detect Enter / Escape BEFORE the TextField renders so the TextField doesn't
                // consume the keystroke first (0.2.15 bug — post-render check never fired).
                bool focused = GUI.GetNameOfFocusedControl() == RENAME_FIELD_NAME;
                bool submitOnEnter = Event.current.type == EventType.KeyDown
                                     && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                                     && focused;
                bool cancelOnEscape = Event.current.type == EventType.KeyDown
                                      && Event.current.keyCode == KeyCode.Escape
                                      && focused;

                GUI.SetNextControlName(RENAME_FIELD_NAME);
                m_RenamingText = GUILayout.TextField(m_RenamingText ?? "", GUILayout.MinWidth(180));
                if (m_RenameFieldFocusPending && Event.current.type == EventType.Repaint)
                {
                    EditorGUI.FocusTextInControl(RENAME_FIELD_NAME);
                    m_RenameFieldFocusPending = false;
                }
                GUILayout.Label($"({grp.rows.Count})", EditorStyles.miniLabel);

                if (submitOnEnter)
                {
                    Event.current.Use();
                    CommitInlineRename();
                    GUI.FocusControl(null);
                }
                else if (cancelOnEscape)
                {
                    Event.current.Use();
                    CancelInlineRename();
                    GUI.FocusControl(null);
                }
                return;
            }

            string label = $"{grp.changelistName} ({grp.rows.Count})";
            if (hasHidden) label += $"  ({grp.rows.Count - visibleCount} hidden by filter)";
            GUILayout.Label(label, EditorStyles.boldLabel);

            if (m_RenamesInFlight.Contains(grp.changelistName))
            {
                if (s_RenameProcessingStyle == null)
                {
                    s_RenameProcessingStyle = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Italic };
                }
                GUILayout.Label("(processing…)", s_RenameProcessingStyle);
            }
        }

        /// <summary>Right-side actions for a user changelist group header: Commit Changelist
        /// + gear menu. Disabled appropriately for the (no changelist) bucket.</summary>
        private void DrawChangelistGroupHeaderActions(RowGroup grp)
        {
            int commitable = grp.rows.Count(r => r.status != SvnFileStatus.Untracked);
            using (new EditorGUI.DisabledScope(commitable == 0 || IsOpInFlight()))
            {
                if (GUILayout.Button(
                        new GUIContent($"Commit Changelist ({commitable})",
                            "Commit all commit-eligible files in this changelist. " +
                            "Prefills the message with the changelist name; you can edit before committing."),
                        EditorStyles.toolbarButton, GUILayout.Width(160)))
                {
                    PrepareGroupCommit(grp);
                }
            }
            using (new EditorGUI.DisabledScope(grp.isNoChangelist))
            {
                if (GUILayout.Button(new GUIContent("⋮", "Group actions"),
                                     EditorStyles.toolbarButton, GUILayout.Width(22)))
                {
                    ShowChangelistGroupGearMenu(grp);
                }
            }
        }

        /// <summary>Make a changelist group header into a drop target for row drags.
        /// (no-changelist) accepts drops and unassigns the rows. User changelists accept
        /// drops and reassign. Reserved (`ignore-on-commit`) is rejected with a toast since
        /// the override workflow owns that name exclusively.</summary>
        private void HandleGroupHeaderDrop(RowGroup grp, Rect headerRect)
        {
            var evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            if (!headerRect.Contains(evt.mousePosition)) return;
            var payload = DragAndDrop.GetGenericData(DRAG_PAYLOAD_KEY) as List<string>;
            if (payload == null || payload.Count == 0) return;

            if (grp.isReserved)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                if (evt.type == EventType.DragPerform)
                {
                    m_StatusBanner = "ignore-on-commit is reserved for the override workflow. " +
                                     "Use the row's [Set Ignore on Commit] action instead.";
                    DragAndDrop.AcceptDrag();
                    evt.Use();
                }
                else
                {
                    evt.Use();
                }
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Move;
            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                if (grp.isNoChangelist)
                {
                    var res = ChangelistService.RemovePaths(payload.ToArray());
                    if (!res.ok) m_StatusBanner = res.reason;
                    else m_StatusBanner = $"Unassigned {payload.Count} file(s) from their changelist.";
                }
                else
                {
                    var res = ChangelistService.AssignPaths(payload.ToArray(), grp.changelistName);
                    if (!res.ok) m_StatusBanner = res.reason;
                    else m_StatusBanner = $"Assigned {payload.Count} file(s) to '{grp.changelistName}'.";
                }
                _ = SvnStatusCache.RefreshAsync();
                evt.Use();
            }
            else
            {
                evt.Use();
            }
        }

        private void PrepareGroupCommit(RowGroup grp)
        {
            // Select only this group's commit-eligible rows; clear other selections so the
            // commit-area count reflects only the group. Prefill the commit message; focus
            // the field so the user can immediately edit.
            foreach (var r in m_Rows)
            {
                r.selected = !r.isOverride && r.status != SvnFileStatus.Untracked
                             && grp.rows.Contains(r);
            }
            if (string.IsNullOrEmpty(m_CommitMessage))
            {
                m_CommitMessage = $"{grp.changelistName}: ";
            }
            EditorGUI.FocusTextInControl("FrgCommitMessage");
            Repaint();
        }

        private void ShowChangelistGroupGearMenu(RowGroup grp)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Select all in group"), false, () =>
            {
                foreach (var r in grp.rows)
                {
                    if (!r.isOverride && r.status != SvnFileStatus.Untracked) r.selected = true;
                }
                Repaint();
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Rename…"), false, () => PromptRenameChangelist(grp.changelistName));
            menu.AddItem(new GUIContent("Delete…"), false, () => PromptDeleteChangelist(grp.changelistName, grp.rows.Count));
            menu.ShowAsContext();
        }

        private void PromptRenameChangelist(string oldName)
        {
            StartInlineRename(oldName);
        }

        /// <summary>Switch a group header into inline-edit mode. Replaces the prior modal
        /// (ChangelistNamePromptWindow) which hung the editor on multi-monitor setups.
        /// The header renders a TextField on the next OnGUI; commit on Return / focus loss.</summary>
        private void StartInlineRename(string groupName)
        {
            m_RenamingGroupName = groupName;
            m_RenamingText = groupName;
            m_RenameFieldFocusPending = true;
            m_GroupExpandedState[groupName] = true; // ensure the group is visible
            Repaint();
        }

        /// <summary>Create a new user changelist with an auto-generated default name, then
        /// drop the group header into inline-rename so the user can immediately type the
        /// real name. If <paramref name="assignPath"/> is non-null, the path is assigned
        /// to the new changelist (used by the right-click "New changelist" flow).</summary>
        private void CreateChangelistAndStartRename(string assignPath = null)
        {
            string name = GenerateUniqueDefaultChangelistName();
            var res = ChangelistService.Create(name);
            if (!res.ok)
            {
                m_StatusBanner = res.reason;
                return;
            }
            if (!string.IsNullOrEmpty(assignPath))
            {
                var assign = ChangelistService.AssignPaths(new[] { assignPath }, name);
                if (!assign.ok)
                {
                    m_StatusBanner = assign.reason;
                }
            }
            m_StatusBanner = $"Created changelist '{name}'. Type a new name and press Enter.";
            StartInlineRename(name);
            _ = SvnStatusCache.RefreshAsync();
        }

        private string GenerateUniqueDefaultChangelistName()
        {
            const string baseName = "new-changelist";
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var n in UserChangelistStore.ReadSnapshot()) existing.Add(n);
            }
            catch { }
            foreach (var r in m_Rows)
            {
                if (!string.IsNullOrEmpty(r.changelist)) existing.Add(r.changelist);
            }
            if (!existing.Contains(baseName)) return baseName;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{baseName} ({i})";
                if (!existing.Contains(candidate)) return candidate;
            }
            // Pathological fallback; unique by timestamp.
            return $"{baseName}-{DateTime.UtcNow.Ticks}";
        }

        /// <summary>Apply the inline-rename buffer. Snaps back to the original name on
        /// validation failure and surfaces the reason in the status banner.
        ///
        /// Two paths to avoid the UI freezes that the synchronous svn call caused in 0.2.14:
        ///   1. If the changelist has no SVN-tracked members (typical for a fresh + Changelist),
        ///      take the sidecar-only fast path — instant, no svn process spawned.
        ///   2. Otherwise wrap the rename in Task.Run so the slow svn status / svn changelist
        ///      work happens off the UI thread, with a "Renaming…" status banner for feedback.</summary>
        private void CommitInlineRename()
        {
            if (m_RenamingGroupName == null) return;
            string oldName = m_RenamingGroupName;
            string newName = (m_RenamingText ?? "").Trim();
            m_RenamingGroupName = null;
            m_RenamingText = null;
            m_RenameFieldFocusPending = false;

            if (string.IsNullOrEmpty(newName) || string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                Repaint();
                return;
            }

            // Probe m_Rows to see if the rename is "empty" (no SVN-tracked members) — that's
            // the common case for the just-created + Changelist flow. Sidecar-only avoids the
            // svn status spawn entirely.
            bool hasMembers = m_Rows.Any(r => string.Equals(r.changelist, oldName, StringComparison.Ordinal));

            // Render the new name immediately with a "(processing…)" italic suffix until the
            // SVN op completes. Even for the fast path we add+remove so the in-flight set
            // semantics stay consistent.
            m_RenamesInFlight.Add(newName);
            // Migrate expansion state NOW so the new-name header renders expanded the moment
            // edit mode exits.
            if (m_GroupExpandedState.TryGetValue(oldName, out var wasExpanded))
            {
                m_GroupExpandedState[newName] = wasExpanded;
                m_GroupExpandedState.Remove(oldName);
            }
            Repaint();

            if (!hasMembers)
            {
                Debug.Log($"[FrgVersionControl] Rename '{oldName}' → '{newName}' (sidecar-only fast path)");
                var v = ChangelistService.Rename(oldName, newName, sidecarOnly: true);
                FinishInlineRename(oldName, newName, v);
                return;
            }

            // Slow path: changelist has files. Off-load to background so the editor doesn't freeze
            // while svn status + svn changelist run.
            m_StatusBanner = $"Renaming '{oldName}' → '{newName}'…";
            Debug.Log($"[FrgVersionControl] Rename '{oldName}' → '{newName}' (async, has SVN members)");
            _ = Task.Run(() => ChangelistService.Rename(oldName, newName))
                    .ContinueWith(t =>
                    {
                        SvnCommandRunner.s_UnityMainSync.Post(_ =>
                        {
                            if (t.IsFaulted)
                            {
                                m_StatusBanner = $"Rename failed: {t.Exception?.GetBaseException().Message ?? "unknown error"}";
                                m_RenamesInFlight.Remove(newName);
                                Repaint();
                                return;
                            }
                            FinishInlineRename(oldName, newName, t.Result);
                        }, null);
                    });
        }

        private void FinishInlineRename(string oldName, string newName, ChangelistService.ValidationResult v)
        {
            m_RenamesInFlight.Remove(newName);
            if (!v.ok)
            {
                // SVN failed mid-rename — revert the optimistic expansion-state migration so
                // the user can re-engage with the original group.
                if (m_GroupExpandedState.TryGetValue(newName, out var was))
                {
                    m_GroupExpandedState[oldName] = was;
                    m_GroupExpandedState.Remove(newName);
                }
                m_StatusBanner = v.reason;
                Repaint();
                return;
            }
            m_StatusBanner = $"Renamed changelist '{oldName}' → '{newName}'.";
            _ = SvnStatusCache.RefreshAsync();
            Repaint();
        }

        private void CancelInlineRename()
        {
            m_RenamingGroupName = null;
            m_RenamingText = null;
            m_RenameFieldFocusPending = false;
            Repaint();
        }

        private void PromptDeleteChangelist(string name, int memberCount)
        {
            string message = memberCount == 0
                ? $"Delete empty changelist '{name}'?"
                : $"Delete changelist '{name}'? {memberCount} file(s) will be unassigned " +
                  "(file edits stay intact; only the changelist tag is cleared).";
            if (!EditorUtility.DisplayDialog("Delete changelist", message, "Delete", "Cancel"))
                return;

            var mode = memberCount == 0
                ? ChangelistService.DeleteMode.RemoveOnlyIfEmpty
                : ChangelistService.DeleteMode.UnassignAllFirst;
            var v = ChangelistService.Delete(name, mode);
            if (!v.ok)
            {
                EditorUtility.DisplayDialog("Delete failed", v.reason, "OK");
                return;
            }
            m_StatusBanner = $"Deleted changelist '{name}'.";
            m_GroupExpandedState.Remove(name);
            _ = SvnStatusCache.RefreshAsync();
        }

        /// <summary>Slice 5: render the Ignore-on-Commit section via the shared DrawSection
        /// helper so it looks identical to user changelist groups. Body iteration (the
        /// individual override rows) happens here in drawBody. Returns nothing — caller
        /// no longer needs to iterate rows after the header.</summary>
        private void DrawOverrideSection(List<Row> overrideRows)
        {
            if (overrideRows.Count == 0) return;
            string key = ChangelistService.ReservedChangelistName;

            DrawSection(new SectionSpec
            {
                expandKey = key,
                label = $"Ignore on Commit ({overrideRows.Count})",
                defaultExpanded = true,
                handleDrop = rect => HandleGroupHeaderDrop(
                    new RowGroup { changelistName = key, isReserved = true }, rect),
                drawHeaderActions = () =>
                {
                    int selectedOverrideCount = m_SelectedOverridePaths.Count;
                    bool allOn = overrideRows.Count > 0 && selectedOverrideCount == overrideRows.Count;
                    bool newAll = EditorGUILayout.ToggleLeft(
                        $"All ({selectedOverrideCount}/{overrideRows.Count})", allOn, GUILayout.Width(110));
                    if (newAll != allOn)
                    {
                        if (newAll) foreach (var r in overrideRows) m_SelectedOverridePaths.Add(r.path);
                        else m_SelectedOverridePaths.Clear();
                    }
                    using (new EditorGUI.DisabledScope(selectedOverrideCount == 0))
                    {
                        if (GUILayout.Button(new GUIContent(
                                $"Clear Selected ({selectedOverrideCount})",
                                "Discard local edits for selected ignore-on-commit entries and restore each file to its upstream version."),
                                EditorStyles.toolbarButton, GUILayout.Width(160)))
                        {
                            _ = DoBulkClearOverrideAsync();
                        }
                    }
                },
                drawBody = () =>
                {
                    foreach (var r in overrideRows.Where(MatchesSearch))
                    {
                        DrawRow(r);
                    }
                },
            });
        }

        // Slice 1: drag-and-drop generic data key shared between DrawRow (drag source) and
        // DrawChangelistGroupHeader (drop target). Type-tagging the payload prevents picking
        // up drags from other Unity windows.
        private const string DRAG_PAYLOAD_KEY = "FrgVersionControl.RowPaths";

        /// <summary>Visual spec for the shared file-row widget — used by Changes-tab rows
        /// AND shelf-bundle rows so they look identical (carbon-copy visual pattern).
        /// Updates happen ONCE in <see cref="DrawFileRowInner"/> and propagate everywhere.</summary>
        private struct FileRowSpec
        {
            public string path;             // forward-slash normalized
            public SvnFileStatus status;
            public string lockOwner;        // optional
            public string lockState;        // optional
            public bool isOverride;
            public bool isConflict;
        }

        /// <summary>Render the SHARED inner-row visuals — optional checkbox, status glyph,
        /// filename (clickable → ping asset), folder path (dimmed), lock tag column.
        ///
        /// Caller owns the surrounding <c>EditorGUILayout.BeginHorizontal</c> /
        /// <c>EndHorizontal</c>, the override-row amber background wash, drag-source +
        /// context-menu detection (which need the rowRect), and any per-context action
        /// buttons (Add to SVN, Clear, Convert, etc.). Pass <paramref name="selectable"/>
        /// = null to skip the checkbox column.
        ///
        /// This is the canonical file-row visual — used by Changes rows AND shelf-file rows.
        /// Update here once, propagate everywhere.</summary>
        private void DrawFileRowInner(in FileRowSpec spec, bool? selectable, Action<bool> onSelectChanged,
            Action onFilenameClick = null)
        {
            // Checkbox column (only when selectable hint provided).
            if (selectable.HasValue)
            {
                bool was = selectable.Value;
                bool now = EditorGUILayout.Toggle(was, GUILayout.Width(20));
                if (now != was) onSelectChanged?.Invoke(now);
            }
            else
            {
                // Reserve checkbox-column space anyway so columns line up across rows that
                // do have a checkbox and rows that don't (visual consistency between
                // Changes rows and shelf-file rows).
                GUILayout.Space(20);
            }

            // Status indicator: red `!` for conflict; IGN tag for override; text glyph otherwise.
            if (spec.isConflict && s_ConflictIcon != null)
            {
                GUILayout.Label(new GUIContent(s_ConflictIcon, "Merge conflict — resolve below"),
                                GUILayout.Width(24), GUILayout.Height(18));
            }
            else if (spec.isOverride)
            {
                GUILayout.Label(new GUIContent("IGN", "Ignore-on-commit active — your edits are excluded from commits."),
                                EditorStyles.miniBoldLabel, GUILayout.Width(24));
            }
            else
            {
                GUILayout.Label(StatusGlyph(spec.status), GUILayout.Width(24));
            }

            // Filename (click → ping asset) + folder column (dimmed).
            string fileName = System.IO.Path.GetFileName(spec.path);
            string folderPath = System.IO.Path.GetDirectoryName(spec.path);
            if (!string.IsNullOrEmpty(folderPath)) folderPath = folderPath.Replace('\\', '/');

            if (GUILayout.Button(fileName, EditorStyles.label, GUILayout.ExpandWidth(false)))
            {
                // Translate .meta → underlying asset for the Project-view select. LoadAssetAtPath
                // returns null for a .meta path, so the click would no-op on meta rows otherwise.
                string pingPath = FileLockManager.GetLockabilityCheckPath(spec.path);
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(pingPath);
                if (obj != null)
                {
                    EditorGUIUtility.PingObject(obj);
                    Selection.activeObject = obj;
                }
                onFilenameClick?.Invoke();
            }

            var prevColor = GUI.color;
            GUI.color = new Color(prevColor.r, prevColor.g, prevColor.b, prevColor.a * 0.6f);
            GUILayout.Label(folderPath ?? "", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            GUI.color = prevColor;

            // Lock tag column — always 120px reserved so the right edge aligns whether or
            // not the file has lock info.
            GUILayout.Label(LockTag(spec.lockState, spec.lockOwner), GUILayout.Width(120));
        }

        private void DrawRow(Row r)
        {
            bool isConflict = r.status == SvnFileStatus.Conflict;
            bool isOverride = r.isOverride && !isConflict; // conflict trumps override visually

            // ----- Main row line (checkbox / icon / path / lock tag / inline override actions) -----
            // (Pre-0.2.23 painted an amber wash on override rows; dropped per user feedback.
            // The IGN status badge in the row's status column remains as the override identifier.)
            var rowRect = EditorGUILayout.BeginHorizontal();

            // Slice 1: drag-and-drop source. Only regular non-conflict rows can be dragged.
            // Dragging a selected row drags the whole regular-row selection; dragging an
            // unselected row drags just that row (Project window convention).
            if (!isConflict && !isOverride && r.status != SvnFileStatus.Untracked
                && Event.current.type == EventType.MouseDrag
                && rowRect.Contains(Event.current.mousePosition))
            {
                var dragPaths = SelectionPathsOrSingle(r);
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.SetGenericData(DRAG_PAYLOAD_KEY, new List<string>(dragPaths));
                DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
                DragAndDrop.StartDrag(dragPaths.Length == 1
                    ? System.IO.Path.GetFileName(dragPaths[0])
                    : $"{dragPaths.Length} files");
                Event.current.Use();
            }

            // Shared inner visuals: checkbox + status glyph + filename + folder + lock tag.
            // Override rows use the separate m_SelectedOverridePaths set; regular rows use
            // r.selected. The shared widget abstracts both behind a (selected, onChange) pair.
            var spec = new FileRowSpec
            {
                path = r.path,
                status = r.status,
                lockOwner = r.lockOwner,
                lockState = r.lockState,
                isOverride = isOverride,
                isConflict = isConflict,
            };
            bool currentlySelected = isOverride
                ? m_SelectedOverridePaths.Contains(r.path)
                : r.selected;
            DrawFileRowInner(in spec, currentlySelected, now =>
            {
                if (isOverride)
                {
                    if (now) m_SelectedOverridePaths.Add(r.path);
                    else m_SelectedOverridePaths.Remove(r.path);
                }
                else
                {
                    r.selected = now;
                }
            }, onFilenameClick: () =>
            {
                if (isConflict || isOverride)
                {
                    ToggleConflictRowExpansion(r);
                }
            });

            // Inline `[Add to SVN]` button on Untracked rows — manual fallback when the
            // import-time auto-add failed (svn:ignore'd folder, network blip, etc.). Most
            // untracked files get added automatically via OnPostprocessAllAssets, but anything
            // that didn't can be resolved here per-row.
            if (r.status == SvnFileStatus.Untracked)
            {
                if (GUILayout.Button(new GUIContent("Add to SVN",
                        "Register this untracked file with SVN. Sets svn:needs-lock automatically " +
                        "for lockable extensions and pairs the .meta sibling if present."),
                        GUILayout.Width(100), GUILayout.Height(18)))
                {
                    _ = AddSingleToSvnAsync(r);
                }
            }

            // Inline ignore-on-commit action buttons — Clear always, Convert to Lock
            // only when the upstream lock has been released.
            if (isOverride)
            {
                if (GUILayout.Button(new GUIContent("Clear",
                        "Discard local edits and restore this file to the upstream version."),
                        GUILayout.Width(80), GUILayout.Height(18)))
                {
                    _ = ClearSingleOverrideAsync(r);
                }
                bool upstreamLockReleased = FileLockManager.Instance != null
                                            && FileLockManager.Instance.GetFileLock(r.path) == null;
                if (upstreamLockReleased)
                {
                    if (GUILayout.Button(new GUIContent("Convert to Lock",
                            "Upstream lock has been released. Acquire the lock and promote your local " +
                            "edits to a normal commit-ready state."),
                            GUILayout.Width(130), GUILayout.Height(18)))
                    {
                        ConvertOverrideToLock(r);
                    }
                }
            }

            EditorGUILayout.EndHorizontal();

            // Slice 1: right-click context menu on regular (non-override, non-conflict) rows.
            // Conflict and override rows already have purpose-built action buttons in the row;
            // a context menu there would be noise. Untracked rows are also excluded — they need
            // [Add to SVN] before changelist assignment makes sense.
            if (!isConflict && !isOverride && r.status != SvnFileStatus.Untracked
                && Event.current.type == EventType.ContextClick
                && rowRect.Contains(Event.current.mousePosition))
            {
                ShowRowContextMenu(r);
                Event.current.Use();
            }

            // ----- Conflict sub-rows: action buttons + expanded preview -----
            if (isConflict)
            {
                DrawConflictActionButtons(r);
                if (r.isExpanded)
                {
                    DrawConflictExpandedPreview(r);
                }
            }
            // ----- Ignore-on-commit foldout: metadata only (Clear / Convert to Lock are inline above) -----
            else if (isOverride && r.isExpanded)
            {
                DrawOverrideExpandedMetadata(r);
            }
        }

        private void ShowRowContextMenu(Row r)
        {
            var menu = new GenericMenu();

            // Build the "Assign to changelist" submenu from existing changelists, excluding
            // the reserved name. Always include "New changelist…" as the last entry.
            List<ChangelistService.ChangelistInfo> all;
            try { all = ChangelistService.GetAllChangelists(); }
            catch { all = new List<ChangelistService.ChangelistInfo>(); }

            foreach (var info in all)
            {
                if (info.isReserved) continue;
                bool isCurrent = string.Equals(info.name, r.changelist, StringComparison.Ordinal);
                menu.AddItem(
                    new GUIContent($"Assign to changelist/{info.name}"),
                    isCurrent,
                    () => AssignSelectedOrSingleTo(r, info.name));
            }
            if (all.Count(i => !i.isReserved) > 0)
            {
                menu.AddSeparator("Assign to changelist/");
            }
            menu.AddItem(new GUIContent("Assign to changelist/New changelist…"), false, () =>
            {
                // Inline-rename UX: create with auto-name, assign the clicked row, then
                // focus the group header for typing the real name. No modal.
                var paths = SelectionPathsOrSingle(r);
                string firstPath = paths.Length > 0 ? paths[0] : r.path;
                CreateChangelistAndStartRename(firstPath);
                // Multi-row selection: also assign the remaining paths to the same new list.
                if (paths.Length > 1 && m_RenamingGroupName != null)
                {
                    var rest = paths.Skip(1).ToArray();
                    if (rest.Length > 0)
                    {
                        var assign = ChangelistService.AssignPaths(rest, m_RenamingGroupName);
                        if (!assign.ok) m_StatusBanner = assign.reason;
                    }
                }
            });

            // Remove from changelist — only meaningful when the row currently has one.
            if (!string.IsNullOrEmpty(r.changelist)
                && !string.Equals(r.changelist, ChangelistService.ReservedChangelistName, StringComparison.OrdinalIgnoreCase))
            {
                menu.AddItem(new GUIContent("Remove from changelist"), false, () =>
                {
                    var paths = SelectionPathsOrSingle(r);
                    var res = ChangelistService.RemovePaths(paths);
                    if (!res.ok)
                    {
                        m_StatusBanner = res.reason;
                    }
                    _ = SvnStatusCache.RefreshAsync();
                });
            }

            menu.ShowAsContext();
        }

        /// <summary>If the right-clicked row is part of the regular-row selection (and there
        /// are multiple selected rows), operate on the whole selection. Otherwise just on
        /// the clicked row alone. Matches Unity Project window convention.</summary>
        private string[] SelectionPathsOrSingle(Row r)
        {
            var selectedRegular = m_Rows.Where(x => x.selected && !x.isOverride
                                                    && x.status != SvnFileStatus.Untracked
                                                    && x.status != SvnFileStatus.Conflict)
                                        .ToList();
            if (r.selected && selectedRegular.Count > 1)
            {
                return selectedRegular.Select(x => x.path).ToArray();
            }
            return new[] { r.path };
        }

        private void AssignSelectedOrSingleTo(Row r, string changelist)
        {
            var paths = SelectionPathsOrSingle(r);
            var res = ChangelistService.AssignPaths(paths, changelist);
            if (!res.ok)
            {
                EditorUtility.DisplayDialog("Assign failed", res.reason, "OK");
                return;
            }
            m_StatusBanner = $"Assigned {paths.Length} file(s) to '{changelist}'.";
            _ = SvnStatusCache.RefreshAsync();
        }

        private void DrawOverrideExpandedMetadata(Row r)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(48);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var entry = FileLockOverrideManager.Instance.GetAllOverrides()
                .FirstOrDefault(e => e.path == r.path);
            if (entry != null)
            {
                string startedDisplay = entry.started_at;
                if (DateTime.TryParse(entry.started_at, out var started))
                {
                    startedDisplay = started.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                }
                EditorGUILayout.LabelField($"Started: {startedDisplay}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Lock holder at start: {entry.lock_holder_at_start}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Lock ID at start: {entry.lock_id_at_start}", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("(No metadata recorded for this entry.)", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawConflictActionButtons(Row r)
        {
            // Lockable types (Unity scenes/prefabs/textures/models/etc.) are designed for
            // pick-a-side resolution — diffing them in our toolset would be misleading.
            // Power users can still diff via TortoiseSVN directly. Null-guard the very-early-init
            // case (FileLockManager not yet bootstrapped) by erring toward disclosure.
            bool isLockable = FileLockManager.Instance != null
                              && FileLockManager.Instance.IsLockableFileType(r.path);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(48); // visual indent under the checkbox + status icon
            if (GUILayout.Button(new GUIContent("Take Mine",
                    "Keep your local edits; discard the upstream version. " +
                    "Runs `svn resolve --accept mine-full` and reimports the asset."),
                    GUILayout.Width(110), GUILayout.Height(22)))
            {
                _ = ResolveConflictAsync(r, ResolveAccept.MineFull);
            }
            if (GUILayout.Button(new GUIContent("Take Theirs",
                    "Discard your local edits; take the upstream version. " +
                    "Runs `svn resolve --accept theirs-full` and reimports the asset."),
                    GUILayout.Width(110), GUILayout.Height(22)))
            {
                _ = ResolveConflictAsync(r, ResolveAccept.TheirsFull);
            }
            if (!isLockable)
            {
                if (GUILayout.Button(new GUIContent("Open externally",
                        "Open the configured external diff/merge tool (TortoiseMerge / Beyond Compare / kdiff3) " +
                        "with the conflict sidecars (.r<base>, .mine, .r<theirs>)."),
                        GUILayout.Width(130), GUILayout.Height(22)))
                {
                    OpenExternalMerge(r);
                }
            }
            GUILayout.FlexibleSpace();
            // Foldout hint
            string foldHint = r.isExpanded ? "▾ click path to collapse" : "▸ click path to expand preview";
            GUILayout.Label(foldHint, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawConflictExpandedPreview(Row r)
        {
            string ext = Path.GetExtension(r.path);
            bool isLockable = FileLockManager.Instance != null
                              && FileLockManager.Instance.IsLockableFileType(r.path);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(48);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (s_TextureExts.Contains(ext))
            {
                // Texture extensions are all lockable by definition (entries in lockable-file-types.json).
                EnsureRowTexturePreviewsLoaded(r);
                EditorGUILayout.BeginHorizontal();
                DrawTextureCell("MINE (your edits)", r.minePreview, r.path, theirsNotMine: false);
                DrawTextureCell("THEIRS (upstream)", r.theirsPreview, r.path, theirsNotMine: true);
                EditorGUILayout.EndHorizontal();
            }
            else if (s_TextConflictExts.Contains(ext))
            {
                // Lockable Unity-YAML — picking a side wholesale is the only sensible path
                // in our toolset; per-line merging of scene/prefab YAML is too error-prone.
                EditorGUILayout.LabelField(
                    "Lockable file — pick a side via Take Mine / Take Theirs. " +
                    "(Diff viewing in TortoiseSVN if needed.)",
                    EditorStyles.wordWrappedLabel);
                DrawSidecarSizes(r.path);
            }
            else if (isLockable)
            {
                EditorGUILayout.LabelField(
                    "There's no inline preview for this binary type — pick the side you intended.",
                    EditorStyles.wordWrappedLabel);
                DrawSidecarSizes(r.path);
            }
            else
            {
                EditorGUILayout.LabelField(
                    "Conflict in a non-lockable file — Take Mine / Take Theirs picks a side wholesale, " +
                    "or click Open externally for a 3-way merge in TortoiseMerge.",
                    EditorStyles.wordWrappedLabel);
                DrawSidecarSizes(r.path);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawTextureCell(string label, Texture2D tex, string assetPath, bool theirsNotMine)
        {
            const float maxDim = 240f;
            EditorGUILayout.BeginVertical();
            GUILayout.Label(label, EditorStyles.boldLabel);
            if (tex != null)
            {
                Rect rect = GUILayoutUtility.GetRect(maxDim, maxDim);
                GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit);
                long bytes = TryGetSidecarSize(assetPath, theirsNotMine);
                GUILayout.Label($"{tex.width} × {tex.height}   ({FormatBytes(bytes)})",
                                EditorStyles.miniLabel);
            }
            else
            {
                GUILayout.Box("(preview not available)", GUILayout.Width(maxDim), GUILayout.Height(maxDim));
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawSidecarSizes(string assetPath)
        {
            long mineBytes = TryGetSidecarSize(assetPath, theirsNotMine: false);
            long theirsBytes = TryGetSidecarSize(assetPath, theirsNotMine: true);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"MINE: {FormatBytes(mineBytes)}", GUILayout.Width(180));
            GUILayout.Label($"THEIRS: {FormatBytes(theirsBytes)}", GUILayout.Width(180));
            EditorGUILayout.EndHorizontal();
        }

        private void ToggleConflictRowExpansion(Row r)
        {
            // One-row-expanded invariant: collapse all other rows + free their previews.
            if (!r.isExpanded)
            {
                foreach (var other in m_Rows)
                {
                    if (other != r && other.isExpanded)
                    {
                        ClearRowPreviews(other);
                        other.isExpanded = false;
                    }
                }
                r.isExpanded = true;
            }
            else
            {
                ClearRowPreviews(r);
                r.isExpanded = false;
            }
        }

        private void DrawCommitArea()
        {
            EditorGUILayout.LabelField("Commit message:", EditorStyles.boldLabel);
            GUI.SetNextControlName("FrgCommitMessage");
            m_CommitMessage = EditorGUILayout.TextArea(m_CommitMessage,
                GUILayout.MinHeight(40), GUILayout.MaxHeight(120));

            // r.selected is the regular-changes selection set; override rows have their own
            // independent set (m_SelectedOverridePaths) and never appear in r.selected, so
            // no override filtering needed here. Filter Untracked rows OUT of the commit set —
            // they need an explicit `svn add` first (the inline [Add to SVN] button or the
            // bulk toolbar button). Including them would fail commit with E200009.
            int commitableSelectedCount = m_Rows.Count(r => r.selected && r.status != SvnFileStatus.Untracked);
            int selectedUntracked = m_Rows.Count(r => r.selected && r.status == SvnFileStatus.Untracked);
            bool hasConflicts = m_Rows.Any(r => r.status == SvnFileStatus.Conflict);
            bool canCommit = commitableSelectedCount > 0 && !string.IsNullOrWhiteSpace(m_CommitMessage)
                              && !IsOpInFlight() && !hasConflicts;

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(!canCommit))
            {
                string tooltip;
                if (hasConflicts)
                    tooltip = "Resolve all conflicts first (Take Mine / Take Theirs on each conflict row; non-lockable files also support Open externally).";
                else if (selectedUntracked > 0)
                    tooltip = $"{selectedUntracked} untracked row(s) selected will be skipped — click [Add to SVN] per row, " +
                              "or [Add Selected to SVN] in the toolbar, then commit again.";
                else
                    tooltip = "Commit the selected files with the message above.";
                if (GUILayout.Button(new GUIContent($"Commit Selected ({commitableSelectedCount})", tooltip),
                                     GUILayout.Height(28), GUILayout.MinWidth(180)))
                {
                    _ = DoCommitSelectedAsync();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatusBanner()
        {
            if (string.IsNullOrEmpty(m_StatusBanner)) return;
            EditorGUILayout.HelpBox(m_StatusBanner,
                m_StatusBanner.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                    ? MessageType.Error : MessageType.Info);
        }

        // ----- Action dispatchers (all async) -----

        private async Task DoCommitSelectedAsync()
        {
            if (RefuseWhileExclusiveOpInFlight("committing")) return;
            // r.selected only ever tracks regular-change rows — override rows use a separate
            // selection set (m_SelectedOverridePaths) and can never appear here. Untracked rows
            // are also filtered out: they need explicit `svn add` first via the inline
            // [Add to SVN] button (Part 3) or [Add Selected to SVN] toolbar (Part 4). The
            // count-display + tooltip in DrawCommitArea inform the user about skipped Untracked.
            var selectedRows = m_Rows.Where(r => r.selected && r.status != SvnFileStatus.Untracked).ToList();
            var selectedPaths = selectedRows.Select(r => r.path).ToArray();
            if (selectedPaths.Length == 0) return;
            if (string.IsNullOrWhiteSpace(m_CommitMessage)) return;
            var paths = CommitSelectionShaper.ExpandFolderMetas(selectedPaths, p => SvnStatusCache.GetEntry(p)?.status);
            var message = m_CommitMessage;

            // Slice 1 follow-up: identify user changelists that this commit will EMPTY. We
            // compute this pre-commit (cheap, in-memory) so we can prompt for cleanup after
            // the refresh lands. A changelist is "fully drained" when every commit-eligible
            // row in it is part of this commit. Skip the reserved name and "(no changelist)"
            // bucket — neither is a user-managed name.
            var changelistsBeingDrained = ComputeChangelistsBeingDrained(selectedRows);

            m_StatusBanner = $"Committing {selectedPaths.Length} file(s)… (recompile deferred while committing)";
            Repaint();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var reloadHold = EditorReloadGuard.Hold();
            try
            {
                var progress = new Progress<string>(line =>
                {
                    m_StatusBanner = line;
                    SvnCommandRunner.s_UnityMainSync.Post(_ => { if (this != null) Repaint(); }, null);
                });
                var result = await SvnCommandRunner.CommitAsync(paths, message, CancellationToken.None, progress);
                sw.Stop();
                FileLockOpLog.Record("commit", $"{selectedPaths.Length}-files", sw.ElapsedMilliseconds, result.success);
                if (!result.success)
                {
                    Debug.LogError($"[FrgVersionControl] Commit failed.\nOutput:\n{result.output}\nError:\n{result.error}");
                }
                _ = SvnStatusCache.RefreshAsync();
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    if (this == null) return;
                    if (result.success)
                    {
                        m_StatusBanner = result.revision > 0
                            ? $"Committed {selectedPaths.Length} file(s) as {result.output}."
                            : result.output;
                        m_CommitMessage = "";
                        if (result.revision > 0 && changelistsBeingDrained.Count > 0)
                        {
                            m_PendingEmptyChangelistPrompts = changelistsBeingDrained;
                        }
                    }
                    else
                    {
                        SetErrorBanner("Commit failed", result.error);
                    }
                    Repaint();
                }, null);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FrgVersionControl] Commit threw exception: {ex}");
                _ = SvnStatusCache.RefreshAsync();
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    if (this == null) return;
                    SetErrorBanner("Commit failed", ex.Message);
                    Repaint();
                }, null);
            }
            finally
            {
                reloadHold.Dispose();
            }
        }

        private async Task DoUpdateAsync()
        {
            if (RefuseWhileExclusiveOpInFlight("updating")) return;
            CancelAndStartCts();
            m_StatusBanner = "Updating working copy… (recompile deferred while updating)";
            Repaint();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var reloadHold = EditorReloadGuard.Hold();
            try
            {
                // 0.2.42 — route through svn-core IPC (mutate.update). Falls back to legacy
                // SvnCommandRunner if the IPC binary is unavailable. Task #13 rewire.
                var (success, error) = await RunUpdateViaIpcOrFallback(m_OpCts.Token).ConfigureAwait(false);
                sw.Stop();
                FileLockOpLog.Record("update", "", sw.ElapsedMilliseconds, success);
                if (success)
                {
                    // Stub conflicted code files BEFORE AssetDatabase notices the file changes.
                    // ScanAndStubNow is pure file I/O (no Unity API calls) so off-thread is safe;
                    // ordering matters — must precede RefreshAsync so the snapshot-driven path
                    // doesn't race with an unstubbed `.cs` getting auto-imported.
                    FileLockConflictShadowService.ScanAndStubNow();
                    _ = SvnStatusCache.RefreshAsync();
                }
                else
                {
                    Debug.LogError($"[FrgVersionControl] Update failed.\nError:\n{error}");
                }
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    if (success)
                    {
                        m_StatusBanner = "Update complete.";
                    }
                    else
                    {
                        SetErrorBanner("Update failed", error);
                    }
                    Repaint();
                }, null);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.LogError($"[FrgVersionControl] Update threw exception: {ex}");
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    SetErrorBanner("Update failed", ex.Message);
                    Repaint();
                }, null);
            }
            finally
            {
                reloadHold.Dispose();
            }
        }

        private static async Task<(bool success, string error)> RunUpdateViaIpcOrFallback(CancellationToken ct)
        {
            var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
            if (client.IsAvailable)
            {
                try
                {
                    var ctx = client.BuildDefaultContext();
                    var result = await client.InvokeRawAsync("mutate.update", "{}", ctx, ct).ConfigureAwait(false);
                    if (result.Ok) return (true, null);
                    var msg = !string.IsNullOrEmpty(result.ErrorDefaultAction)
                        ? $"{result.ErrorMessage} — {result.ErrorDefaultAction}"
                        : result.ErrorMessage ?? result.ErrorKind ?? "IPC reported failure";
                    return (false, msg);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return (false, $"IPC update threw: {ex.Message} — Refresh status before retrying.");
                }
            }
            return (false, "svn-core IPC unavailable for update. Reinstall the FRG Version Control package, or run Build-SvnCore.ps1 from the toolset repo root.");
        }

        private async Task DoRevertSelectedAsync()
        {
            if (RefuseWhileExclusiveOpInFlight("reverting")) return;
            var selectedRows = m_Rows.Where(r => r.selected).ToList();
            if (selectedRows.Count == 0) return;
            var paths = selectedRows.Select(r => r.path).ToArray();

            // Count Added rows — revert on these unschedules the add but leaves the file on
            // disk as Untracked. Offer a "Revert & Delete from Disk" option so the user can
            // fully discard new files in one step.
            var addedRows = selectedRows.Where(r => r.status == SvnFileStatus.Added).ToList();
            int addedCount = addedRows.Count;

            string list = string.Join("\n  ", paths.Take(10));
            string suffix = paths.Length > 10 ? $"\n  …and {paths.Length - 10} more" : "";

            bool deleteAdded = false;
            if (addedCount > 0)
            {
                // 3-option dialog when Added rows are present.
                string msg = $"Discard local changes for {paths.Length} file(s). " +
                             $"{addedCount} are Added (not yet committed) — these would otherwise " +
                             "remain as Untracked files after revert. Choose 'Revert & Delete Added' " +
                             "to also remove them (and their .meta siblings) from disk.\n\n" +
                             $"  {list}{suffix}";
                int choice = EditorUtility.DisplayDialogComplex(
                    "Revert Selected?",
                    msg,
                    "Revert",                  // 0 — just unschedule, leave files on disk
                    "Cancel",                  // 1
                    "Revert & Delete Added");  // 2 — also delete Added files from disk
                if (choice == 1) return;
                deleteAdded = (choice == 2);
            }
            else
            {
                bool ok = EditorUtility.DisplayDialog(
                    "Revert Selected?",
                    $"Discard local changes for {paths.Length} file(s)? This cannot be undone.\n\n" +
                    $"  {list}{suffix}",
                    "Revert", "Cancel");
                if (!ok) return;
            }

            CancelAndStartCts();
            m_StatusBanner = $"Reverting {paths.Length} file(s)…";
            Repaint();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await SvnCommandRunner.RevertAsync(paths, alsoRevertMeta: true, ct: m_OpCts.Token);
                sw.Stop();
                FileLockOpLog.Record("revert", $"{paths.Length}-files", sw.ElapsedMilliseconds, result.success);
                if (!result.success)
                {
                    Debug.LogError($"[FrgVersionControl] Revert failed.\nOutput:\n{result.output}\nError:\n{result.error}");
                }

                int deletedFiles = 0;
                if (result.success && deleteAdded && addedRows.Count > 0)
                {
                    // Delete Added files from disk now that they're unscheduled. Also delete
                    // their .meta siblings if present (svn revert with alsoRevertMeta already
                    // unscheduled the metas; now we remove them from disk too).
                    foreach (var ar in addedRows)
                    {
                        deletedFiles += TryDeleteFileAndMeta(ar.path);
                    }
                }

                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    if (result.success)
                    {
                        // Reverted content is back on disk (or freshly removed for Added+Delete);
                        // tell Unity to reimport so the AssetDatabase reflects the new state.
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    }
                    if (result.success)
                    {
                        m_StatusBanner = deleteAdded && deletedFiles > 0
                            ? $"Reverted {paths.Length} file(s); deleted {deletedFiles} from disk."
                            : $"Reverted {paths.Length} file(s).";
                    }
                    else
                    {
                        SetErrorBanner("Revert failed", result.error);
                    }
                    Repaint();
                }, null);
                if (result.success) _ = SvnStatusCache.RefreshAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.LogError($"[FrgVersionControl] Revert threw exception: {ex}");
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    SetErrorBanner("Revert failed", ex.Message);
                    Repaint();
                }, null);
            }
        }

        /// <summary>Delete an asset file and its `.meta` sibling from disk. Returns the count
        /// of files actually removed (0-2). Used by Revert & Delete Added after `svn revert`
        /// unscheduled the add. Clears the read-only attribute first — `svn:needs-lock` set
        /// during AddForCommit leaves the OS read-only flag set even after the property is
        /// reverted, which would block `File.Delete` with Access Denied. This is an explicit
        /// user-requested discard, so clearing read-only is intentional (not a casual `attrib -r`).</summary>
        private static int TryDeleteFileAndMeta(string assetPath)
        {
            int count = 0;
            string fullPath = Path.IsPathRooted(assetPath)
                ? assetPath
                : Path.Combine(SvnCommandRunner.ProjectRoot, assetPath);
            count += TryDeleteOne(fullPath);

            // Pair the .meta sibling. Skip if the asset path was already a .meta (don't go to .meta.meta).
            if (!assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                count += TryDeleteOne(fullPath + ".meta");
            }
            return count;
        }

        private static int TryDeleteOne(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath)) return 0;
                // Clear read-only flag if set (left over from `svn:needs-lock` propset during
                // AddForCommit). Otherwise File.Delete throws UnauthorizedAccessException.
                var attrs = File.GetAttributes(fullPath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(fullPath, attrs & ~FileAttributes.ReadOnly);
                }
                File.Delete(fullPath);
                return 1;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FrgVersionControl] Failed to delete {fullPath}: {ex.Message}");
                return 0;
            }
        }

        private async Task DoRevertAndUnlockSelectedAsync()
        {
            // Snapshot the selected non-override rows. We need lock-state info per row so
            // we know which to RevertAndUnlock vs which to plain Revert.
            var selectedRows = m_Rows.Where(r => r.selected && !r.isOverride).ToList();
            if (selectedRows.Count == 0) return;

            int lockedByMeCount = selectedRows.Count(r => r.lockState == "K");
            int plainRevertCount = selectedRows.Count - lockedByMeCount;

            string list = string.Join("\n  ", selectedRows.Take(10).Select(r => r.path));
            string suffix = selectedRows.Count > 10 ? $"\n  …and {selectedRows.Count - 10} more" : "";
            string countLine = lockedByMeCount > 0 && plainRevertCount > 0
                ? $"{lockedByMeCount} will be reverted + unlocked; {plainRevertCount} will be plain-reverted."
                : lockedByMeCount > 0
                    ? $"{lockedByMeCount} file(s) will be reverted + unlocked."
                    : $"{plainRevertCount} file(s) will be reverted (no locks held by you).";

            bool ok = EditorUtility.DisplayDialog(
                "Revert & Unlock Selected?",
                $"{countLine}\nLocal changes will be discarded; locks released where applicable. " +
                $"Cannot be undone.\n\n  {list}{suffix}",
                "Revert & Unlock", "Cancel");
            if (!ok) return;

            m_StatusBanner = $"Reverting + unlocking {selectedRows.Count} file(s)…";
            Repaint();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Iterate on the MAIN THREAD: RevertAndUnlockFile calls AssetDatabase.Refresh
            // internally, which throws if invoked off the main thread. Task.Yield between
            // iterations lets the UI repaint so the progress banner updates and the user
            // doesn't see a frozen editor for multi-file batches.
            int succeeded = 0;
            int unlocked = 0;
            var failed = new List<string>();
            for (int i = 0; i < selectedRows.Count; i++)
            {
                var r = selectedRows[i];
                m_StatusBanner = $"Reverting + unlocking {i + 1}/{selectedRows.Count}: {System.IO.Path.GetFileName(r.path)}…";
                Repaint();
                try
                {
                    if (r.lockState == "K")
                    {
                        // Revert + unlock together. RevertAndUnlockFile is main-thread-safe
                        // (calls AssetDatabase.Refresh internally) and handles both ops.
                        bool ok2 = FileLockManager.Instance.RevertAndUnlockFile(r.path);
                        if (ok2) { succeeded++; unlocked++; }
                        else { failed.Add(System.IO.Path.GetFileName(r.path)); }
                    }
                    else
                    {
                        // Plain revert via SvnCommandRunner (synchronous — runs svn binary;
                        // blocks the editor for ~tens of ms per file).
                        var rv = SvnCommandRunner.Revert(new[] { r.path }, alsoRevertMeta: true);
                        if (rv.success) { succeeded++; }
                        else
                        {
                            Debug.LogError($"[FrgVersionControl] Revert failed for {r.path}.\n" +
                                           $"Output:\n{rv.output}\nError:\n{rv.error}");
                            failed.Add(System.IO.Path.GetFileName(r.path));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[FrgVersionControl] Revert+Unlock threw for {r.path}: {ex}");
                    failed.Add(System.IO.Path.GetFileName(r.path));
                }

                // Yield to give the editor a frame to repaint the progress banner.
                await Task.Yield();
            }
            sw.Stop();
            FileLockOpLog.Record("revert+unlock", $"{selectedRows.Count}-files", sw.ElapsedMilliseconds, failed.Count == 0);

            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                // Force a single AssetDatabase.Refresh covering the whole batch (per-file
                // RevertAndUnlockFile already calls Refresh, but the plain-revert path does
                // not — and a final pass ensures the UI is consistent).
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                if (failed.Count == 0)
                {
                    m_StatusBanner = unlocked > 0
                        ? $"Reverted {succeeded} file(s); released {unlocked} lock(s)."
                        : $"Reverted {succeeded} file(s).";
                }
                else
                {
                    string failedList = string.Join(", ", failed.Take(3));
                    string failedSuffix = failed.Count > 3 ? $", …{failed.Count - 3} more" : "";
                    m_StatusBanner = $"Revert+Unlock: {succeeded}/{selectedRows.Count} ok " +
                                     $"({unlocked} unlocked). {failed.Count} failed: {failedList}{failedSuffix}. " +
                                     "Open Console for details.";
                }
                _ = SvnStatusCache.RefreshAsync();
                FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();
                Repaint();
            }, null);
        }

        // ----- Helpers -----

        private void CancelAndStartCts()
        {
            if (m_OpCts != null) { try { m_OpCts.Cancel(); } catch { } m_OpCts.Dispose(); }
            m_OpCts = new CancellationTokenSource();
        }

        private bool IsOpInFlight()
        {
            return IpcSvnClient.IsExclusiveOpInFlight
                || (m_OpCts != null && !m_OpCts.IsCancellationRequested && SvnStatusCache.IsRefreshing);
        }

        private bool RefuseWhileExclusiveOpInFlight(string verb)
        {
            if (!IpcSvnClient.IsExclusiveOpInFlight) return false;
            m_StatusBanner = $"A {IpcOpPolicy.Describe(IpcSvnClient.ExclusiveOpName)} is in progress. Wait for it to finish before {verb}.";
            Repaint();
            return true;
        }

        private static string StatusGlyph(SvnFileStatus s)
        {
            switch (s)
            {
                case SvnFileStatus.Modified:    return "M";
                case SvnFileStatus.Added:       return "A";
                case SvnFileStatus.Deleted:     return "D";
                case SvnFileStatus.Replaced:    return "R";
                case SvnFileStatus.Conflict:    return "C";
                case SvnFileStatus.Untracked:   return "?";
                case SvnFileStatus.Ignored:     return "I";
                case SvnFileStatus.Missing:     return "!"; // gone on disk, still in wc.db
                default:                        return "·";
            }
        }

        private static string LockTag(string lockState, string lockOwner)
        {
            if (string.IsNullOrEmpty(lockState)) return "";
            switch (lockState)
            {
                case "K": return "🔒 you";
                case "O": return $"🔒 {lockOwner}";
                case "B": return "⚠ broken";
                case "T": return $"⚠ stolen by {lockOwner}";
                default:  return "";
            }
        }

        // ----- Inline conflict resolution (migrated from FileLockConflictResolverWindow) -----

        private async Task ResolveConflictAsync(Row r, ResolveAccept accept)
        {
            string path = r.path;
            string fname = Path.GetFileName(path);
            m_StatusBanner = $"Resolving {fname} (accept {accept})…";
            Repaint();
            try
            {
                var result = await SvnCommandRunner.ResolveAsync(path, accept);
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    if (result.success)
                    {
                        // Drop preview textures + collapse the row before refresh — the row
                        // itself disappears from the next snapshot once the conflict clears.
                        ClearRowPreviews(r);
                        r.isExpanded = false;

                        // Reimport so Unity picks up restored content: .cs recompiles
                        // (overwriting the conflict-shadow stub), textures re-import (Materials
                        // referencing them refresh), prefabs/animators re-resolve. Trade-off
                        // accepted per user direction: ForceUpdate walks the project; on the
                        // 11TB-scale repo this may need to migrate to AssetDatabase.ImportAsset
                        // targeting the resolved path. Revisit if it becomes a UX issue.
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

                        m_StatusBanner = $"Resolved {fname} ({accept}). Asset reimported.";
                        _ = SvnStatusCache.RefreshAsync();
                    }
                    else
                    {
                        Debug.LogError($"[FrgVersionControl] Resolve failed on {path}.\nOutput:\n{result.output}\nError:\n{result.error}");
                        // Polish-bar format: <description>. <action> <target>.
                        string otherSide = accept == ResolveAccept.MineFull ? "Theirs" : "Mine";
                        string firstLine = result.error?.Split('\n')[0] ?? "(no detail)";
                        m_StatusBanner = $"Resolve failed: {firstLine}. " +
                                         $"Open Console for full output, or try Take {otherSide} instead.";
                    }
                    Repaint();
                }, null);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FrgVersionControl] Resolve threw exception: {ex}");
                SvnCommandRunner.s_UnityMainSync.Post(_ =>
                {
                    m_StatusBanner = $"Resolve failed: {ex.Message}. Open Console for full output.";
                    Repaint();
                }, null);
            }
        }

        // Reachable only from non-lockable conflict rows (gated in DrawConflictActionButtons
        // via FileLockManager.IsLockableFileType). For lockable files, picking a side wholesale
        // is the only supported resolution in our toolset.
        private void OpenExternalMerge(Row r)
        {
            string fullPath = Path.IsPathRooted(r.path)
                ? r.path
                : Path.Combine(SvnCommandRunner.ProjectRoot, r.path);

            string mine = TryFindSidecar(fullPath, ".mine");
            string theirs = TryFindSidecarRevisionFile(fullPath, theirsNotMine: true);
            string baseFile = TryFindSidecarRevisionFile(fullPath, theirsNotMine: false);

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
                    string args;
                    if (exe.EndsWith("TortoiseMerge.exe"))
                    {
                        args = $"/base:\"{baseFile ?? mine}\" /theirs:\"{theirs ?? fullPath}\" " +
                               $"/mine:\"{mine ?? fullPath}\" /merged:\"{fullPath}\"";
                    }
                    else
                    {
                        args = $"\"{mine ?? fullPath}\" \"{theirs ?? fullPath}\"";
                    }
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe, Arguments = args, UseShellExecute = true
                    });
                    m_StatusBanner = "External merge tool opened. Resolve there, then return and " +
                                     "click Take Mine / Take Theirs to finalize " +
                                     "(or run `svn resolve --accept working` from CLI after editing the merged file directly).";
                    Repaint();
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PendingChanges] failed to spawn {exe}: {ex.Message}");
                }
            }
            m_StatusBanner = "External merge tool not found. Install TortoiseSVN with TortoiseMerge, " +
                             "Beyond Compare, or kdiff3 — or pick a side via Take Mine / Take Theirs.";
            Repaint();
        }

        private static void EnsureRowTexturePreviewsLoaded(Row r)
        {
            if (r.minePreview != null && r.theirsPreview != null) return;

            string fullPath = Path.IsPathRooted(r.path)
                ? r.path
                : Path.Combine(SvnCommandRunner.ProjectRoot, r.path);

            // For binary conflicts SVN does NOT emit a `.mine` sidecar — it leaves the
            // working file as the user's local edits and only sidecars `.r<base>` /
            // `.r<theirs>`. Fall back to fullPath so MINE renders the working file.
            string mine = TryFindSidecar(fullPath, ".mine") ?? fullPath;
            string theirs = TryFindSidecarRevisionFile(fullPath, theirsNotMine: true);

            if (r.minePreview == null)   r.minePreview   = LoadTextureFromBytes(mine);
            if (r.theirsPreview == null) r.theirsPreview = LoadTextureFromBytes(theirs);
        }

        private static Texture2D LoadTextureFromBytes(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2);
                if (tex.LoadImage(bytes)) return tex;
                UnityEngine.Object.DestroyImmediate(tex);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PendingChanges] LoadTextureFromBytes failed for {path}: {ex.Message}");
            }
            return null;
        }

        private static string TryFindSidecar(string fullPath, string suffix)
        {
            string candidate = fullPath + suffix;
            return File.Exists(candidate) ? candidate : null;
        }

        /// <summary>SVN names sidecars `.r&lt;base&gt;` and `.r&lt;theirs&gt;` by revision; we don't
        /// know the rev numbers, so glob for them.</summary>
        private static string TryFindSidecarRevisionFile(string fullPath, bool theirsNotMine)
        {
            string dir = Path.GetDirectoryName(fullPath);
            string name = Path.GetFileName(fullPath);
            if (dir == null || !Directory.Exists(dir)) return null;
            var matches = Directory.GetFiles(dir, name + ".r*")
                .Where(p =>
                {
                    var ext = Path.GetExtension(p);
                    return ext.Length > 2 && ext.StartsWith(".r") && int.TryParse(ext.Substring(2), out _);
                })
                .OrderBy(p => int.Parse(Path.GetExtension(p).Substring(2)))
                .ToList();
            if (matches.Count == 0) return null;
            return theirsNotMine ? matches.Last() : matches.First();
        }

        private static long TryGetSidecarSize(string assetPath, bool theirsNotMine)
        {
            try
            {
                string fullPath = Path.IsPathRooted(assetPath)
                    ? assetPath
                    : Path.Combine(SvnCommandRunner.ProjectRoot, assetPath);
                // MINE side: fall back to the working file when SVN didn't emit a .mine
                // sidecar (binary-conflict default — the working file IS your local edits).
                string sidecar = theirsNotMine
                    ? TryFindSidecarRevisionFile(fullPath, theirsNotMine: true)
                    : (TryFindSidecar(fullPath, ".mine") ?? fullPath);
                return string.IsNullOrEmpty(sidecar) ? 0L : new FileInfo(sidecar).Length;
            }
            catch { return 0L; }
        }

        private static string FormatBytes(long b)
        {
            if (b <= 0) return "—";
            if (b < 1024) return $"{b} B";
            if (b < 1024 * 1024) return $"{b / 1024.0:0.#} KB";
            return $"{b / (1024.0 * 1024.0):0.##} MB";
        }

        /// <summary>Wrap an svn error code with a one-line action sentence per CLAUDE.md polish bar.</summary>
        private static string WrapError(string what, string svnError)
        {
            if (string.IsNullOrEmpty(svnError))
                return $"Error: {what}. Try Refresh and retry.";
            // Common error codes mapped to action sentences
            if (svnError.Contains("E195022") || svnError.Contains("E160038"))
                return $"{what}: lock missing. Acquire the lock first (right-click → Lock) or use Set Ignore on Commit.";
            if (svnError.Contains("E155004"))
                return $"{what}: another SVN operation is in progress. Try the [Run Cleanup] banner above, or retry in a few seconds.";
            if (svnError.Contains("E170013") || svnError.Contains("E215004"))
                return $"{what}: cannot reach server or auth failed. Check VPN/network and credentials in your SVN config.";
            if (svnError.Contains("E165001"))
                return $"{what}: server-side hook rejected the commit. Read the message above for the policy violated.";
            if (svnError.Contains("E155010") || svnError.Contains("E155007"))
                return $"{what}: working-copy state inconsistent. Use the [Run Cleanup] banner above to recover.";
            return $"{what}: {svnError.Split('\n')[0]}. Open Console for full output.";
        }

        /// <summary>Slice 1: wrap an svn error AND register it with the cleanup banner state
        /// machine. Replaces direct `m_StatusBanner = WrapError(...)` at every catch site so the
        /// banner fires for any wc-locked error surfaced through the window.</summary>
        private void SetErrorBanner(string what, string svnError)
        {
            m_StatusBanner = WrapError(what, svnError);
            RecordSvnErrorForBanner(svnError);
        }

        /// <summary>Slice 1: set the cleanup-banner flag when the svn error code indicates a
        /// working-copy lock (E155004 / E155010 / E155007). Called from every catch site that
        /// also calls <see cref="WrapError"/>. Cross-tab: an error on Locks tab sets the flag;
        /// banner shows next time the user is on Changes tab.</summary>
        private void RecordSvnErrorForBanner(string svnError)
        {
            if (IsWcLockedError(svnError))
            {
                m_CleanupBannerNeeded = true;
            }
        }

        /// <summary>Pure predicate: does this svn stderr text indicate a working-copy lock
        /// state (the kind that `svn cleanup` resolves)? Public so the test asmdef can pin
        /// the contract without instantiating a window.</summary>
        public static bool IsWcLockedError(string svnError)
        {
            if (string.IsNullOrEmpty(svnError)) return false;
            return svnError.Contains("E155004")
                || svnError.Contains("E155010")
                || svnError.Contains("E155007");
        }

        // ----------------------------------------------------------------
        // Add-to-SVN actions (inline + bulk) — manual fallback for auto-add failures
        // ----------------------------------------------------------------

        private async Task AddSingleToSvnAsync(Row r)
        {
            m_StatusBanner = $"Adding {Path.GetFileName(r.path)} to SVN…";
            Repaint();
            var lockableExts = FileLockManager.Instance.GetLockableExtensionsSet();
            var result = await SvnCommandRunner.AddForCommitAsync(new[] { r.path }, lockableExts);
            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                if (result.success)
                {
                    m_StatusBanner = $"Added {Path.GetFileName(r.path)} to SVN.";
                }
                else
                {
                    Debug.LogError($"[FrgVersionControl] Add to SVN failed for {r.path}.\n" +
                                   $"Output:\n{result.output}\nError:\n{result.error}");
                    m_StatusBanner = $"Add to SVN failed: {result.error?.Split('\n')[0] ?? "(no detail)"}. Open Console for details.";
                }
                _ = SvnStatusCache.RefreshAsync();
                FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();
                Repaint();
            }, null);
        }

        private async Task DoBulkAddToSvnAsync()
        {
            var untrackedPaths = m_Rows
                .Where(r => r.selected && r.status == SvnFileStatus.Untracked)
                .Select(r => r.path)
                .ToArray();
            if (untrackedPaths.Length == 0) return;

            m_StatusBanner = $"Adding {untrackedPaths.Length} file(s) to SVN…";
            Repaint();
            var lockableExts = FileLockManager.Instance.GetLockableExtensionsSet();
            var result = await SvnCommandRunner.AddForCommitAsync(untrackedPaths, lockableExts);
            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                if (result.success)
                {
                    m_StatusBanner = $"Added {untrackedPaths.Length} file(s) to SVN.";
                }
                else
                {
                    Debug.LogError($"[FrgVersionControl] Bulk Add to SVN failed.\n" +
                                   $"Output:\n{result.output}\nError:\n{result.error}");
                    m_StatusBanner = $"Bulk add failed: {result.error?.Split('\n')[0] ?? "(no detail)"}. " +
                                     "Some paths may have succeeded — check Console.";
                }
                _ = SvnStatusCache.RefreshAsync();
                FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();
                Repaint();
            }, null);
        }

        // ----------------------------------------------------------------
        // Override actions (Changes tab inline section)
        // ----------------------------------------------------------------

        private async Task ClearSingleOverrideAsync(Row r)
        {
            bool ok = EditorUtility.DisplayDialog(
                "Clear Ignore on Commit?",
                $"Clear ignore-on-commit for '{Path.GetFileName(r.path)}'?\n\n" +
                "Local changes will be discarded and the file restored to the upstream version.",
                "Clear", "Cancel");
            if (!ok) return;

            string err;
            bool success = FileLockOverrideManager.Instance.ClearIgnoreOnCommit(r.path, out err);
            await Task.Yield();
            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                if (success)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    m_StatusBanner = $"Cleared {Path.GetFileName(r.path)}.";
                    _ = SvnStatusCache.RefreshAsync();
                    FileLockProjectWindowOverlay.RepaintProjectWindow();
                }
                else
                {
                    m_StatusBanner = $"Clear failed: {err ?? "unknown error"}. Open Console for full output.";
                }
                Repaint();
            }, null);
        }

        private void ConvertOverrideToLock(Row r)
        {
            // OverrideConversion has its own confirmation dialog — don't double-confirm here.
            string err;
            bool success = OverrideConversion.ConvertIgnoreOnCommitToLock(r.path, out err);
            if (success)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                m_StatusBanner = $"Converted {Path.GetFileName(r.path)} to a lock.";
                _ = SvnStatusCache.RefreshAsync();
                FileLockProjectWindowOverlay.RepaintProjectWindow();
            }
            else if (!string.IsNullOrEmpty(err))
            {
                m_StatusBanner = $"Convert to Lock failed: {err}. Open Console for full output.";
            }
            Repaint();
        }

        private async Task DoBulkClearOverrideAsync()
        {
            // Operates on the independent override-selection set, NOT the regular-changes
            // r.selected. Snapshot to an array first since ClearIgnoreOnCommit mutates the registry
            // which the user's checkbox state depends on.
            var paths = m_SelectedOverridePaths.ToArray();
            if (paths.Length == 0) return;

            string list = string.Join("\n  ", paths.Take(10));
            string suffix = paths.Length > 10 ? $"\n  …and {paths.Length - 10} more" : "";
            bool ok = EditorUtility.DisplayDialog(
                "Clear Ignore on Commit on Selected?",
                $"Clear ignore-on-commit on {paths.Length} file(s)? Local edits will be discarded.\n\n  {list}{suffix}",
                "Clear", "Cancel");
            if (!ok) return;

            m_StatusBanner = $"Clearing {paths.Length} file(s)…";
            Repaint();

            int succeeded = 0;
            var failed = new List<string>();
            foreach (var p in paths)
            {
                string err;
                if (FileLockOverrideManager.Instance.ClearIgnoreOnCommit(p, out err))
                {
                    succeeded++;
                }
                else
                {
                    failed.Add(Path.GetFileName(p));
                    Debug.LogWarning($"[FrgVersionControl] Clear failed for {p}: {err}");
                }
                await Task.Yield();
            }

            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                if (failed.Count == 0)
                {
                    m_StatusBanner = $"Cleared {succeeded}.";
                }
                else
                {
                    string failedList = string.Join(", ", failed.Take(3));
                    string failedSuffix = failed.Count > 3 ? $", …{failed.Count - 3} more" : "";
                    m_StatusBanner = $"Cleared {succeeded}/{paths.Length}. " +
                                     $"{failed.Count} failed: {failedList}{failedSuffix}. Open Console for details.";
                }
                _ = SvnStatusCache.RefreshAsync();
                FileLockProjectWindowOverlay.RepaintProjectWindow();
                Repaint();
            }, null);
        }

        // ----------------------------------------------------------------
        // Locks tab (migrated from FileLockWindow)
        // ----------------------------------------------------------------

        private void DrawLocksTab()
        {
            DrawLocksToolbar();
            DrawLocksSelectionBar();
            DrawLockList();
        }

        private void DrawLocksToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            bool isEnabled = FileLockManager.Instance.IsInitialized;
            GUI.enabled = isEnabled;

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                FileLockSyncService.SyncNow();
                Repaint();
            }

            GUILayout.Space(10);
            m_LocksSearchFilter = GUILayout.TextField(m_LocksSearchFilter, EditorStyles.toolbarSearchField, GUILayout.ExpandWidth(true));
            GUILayout.Space(10);

            m_ShowOnlyMyLocks = GUILayout.Toggle(m_ShowOnlyMyLocks, "My Locks Only", EditorStyles.toolbarButton);
            m_ShowStaleLocks = GUILayout.Toggle(m_ShowStaleLocks, "Show Stale", EditorStyles.toolbarButton);

            GUILayout.FlexibleSpace();

            GUI.enabled = true;
            if (GUILayout.Button("Setup", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                FileLockSetupWizard.ShowWizard();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawLocksSelectionBar()
        {
            if (!FileLockManager.Instance.IsInitialized) return;

            var filteredLocks = FilterLocks(FileLockManager.Instance.GetAllLocks());
            int selectableCount = filteredLocks.Count(CanUnlockLock);
            int selectedCount = m_SelectedLockPaths.Count;

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Select All", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                m_SelectedLockPaths.Clear();
                foreach (var fl in filteredLocks)
                {
                    if (CanUnlockLock(fl)) m_SelectedLockPaths.Add(fl.path);
                }
            }
            if (GUILayout.Button("Deselect All", EditorStyles.toolbarButton, GUILayout.Width(90)))
            {
                m_SelectedLockPaths.Clear();
            }

            GUILayout.Space(10);
            EditorGUILayout.LabelField($"Selected: {selectedCount} of {selectableCount}", GUILayout.Width(150));
            GUILayout.Space(10);

            int cleanLocksCount = filteredLocks.Count(IsMyCleanLock);
            GUI.enabled = cleanLocksCount > 0;
            if (GUILayout.Button($"Unlock Unmodified ({cleanLocksCount})", EditorStyles.toolbarButton, GUILayout.Width(170)))
            {
                QuickUnlockCleanLocks(filteredLocks);
            }
            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            GUI.enabled = selectedCount > 0;
            if (GUILayout.Button($"Unlock Selected ({selectedCount})", EditorStyles.toolbarButton, GUILayout.Width(150)))
            {
                BatchUnlockSelected(filteredLocks);
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();
        }

        private void DrawLockList()
        {
            if (!FileLockManager.Instance.IsInitialized)
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.HelpBox(
                    "FILE LOCKING IS CURRENTLY DISABLED\n\n" +
                    "File locking has been globally disabled via admin-config.json.\n\n" +
                    "To re-enable:\n" +
                    "1. Open Assets/FRGVersionControl/admin-config.json\n" +
                    "2. Set \"fileLockingEnabled\": true\n" +
                    "3. Restart Unity",
                    MessageType.Warning);
                EditorGUILayout.Space(10);
                return;
            }

            var allLocks = FileLockManager.Instance.GetAllLocks();
            var filteredLocks = FilterLocks(allLocks);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Locks: {filteredLocks.Count} of {allLocks.Count}", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            if (filteredLocks.Count == 0)
            {
                EditorGUILayout.HelpBox("No locks found.", MessageType.Info);
                return;
            }

            m_LocksScroll = EditorGUILayout.BeginScrollView(m_LocksScroll);
            foreach (var fileLock in filteredLocks.OrderBy(l => l.path, StringComparer.OrdinalIgnoreCase))
            {
                DrawLockEntry(fileLock);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawLockEntry(FileLock fileLock)
        {
            bool isMyLock = fileLock.owner == FileLockManager.Instance.UserName &&
                            fileLock.machineName == FileLockManager.Instance.MachineName;
            bool isStale = fileLock.IsStale();
            bool canUnlock = CanUnlockLock(fileLock);

            Color bgColor = isMyLock ? s_LockColorMine : s_LockColorOther;
            GUI.backgroundColor = bgColor;
            Rect entryRect = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = Color.white;

            EditorGUILayout.BeginHorizontal();

            // Checkbox (disabled when can't unlock)
            if (canUnlock)
            {
                bool isSelected = m_SelectedLockPaths.Contains(fileLock.path);
                bool newSelected = EditorGUILayout.Toggle(isSelected, GUILayout.Width(20));
                if (newSelected != isSelected)
                {
                    if (newSelected) m_SelectedLockPaths.Add(fileLock.path);
                    else             m_SelectedLockPaths.Remove(fileLock.path);
                }
            }
            else
            {
                GUI.enabled = false;
                EditorGUILayout.Toggle(false, GUILayout.Width(20));
                GUI.enabled = true;
            }

            // Path
            EditorGUILayout.BeginVertical();
            string fileName = Path.GetFileName(fileLock.path);
            string directory = Path.GetDirectoryName(fileLock.path);
            EditorGUILayout.LabelField(new GUIContent(fileName, fileLock.path), EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
            if (!string.IsNullOrEmpty(directory))
            {
                var smallStyle = new GUIStyle(EditorStyles.miniLabel);
                smallStyle.normal.textColor = Color.gray;
                EditorGUILayout.SelectableLabel(directory + "/" + fileName, smallStyle,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            EditorGUILayout.EndVertical();

            DrawLockActionButtons(fileLock, isMyLock, isStale);
            EditorGUILayout.EndHorizontal();

            // Lock details
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Owner: {fileLock.ownerName} ({fileLock.owner})", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Machine: {fileLock.machineName}", EditorStyles.miniLabel);
            var lockTime = fileLock.GetLockedAtTime();
            var elapsed = DateTime.UtcNow - lockTime;
            EditorGUILayout.LabelField($"Locked: {FormatTimeAgo(elapsed)} ago{(isStale ? " (STALE)" : "")}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            if (Event.current.type == EventType.MouseDown && entryRect.Contains(Event.current.mousePosition))
            {
                // Translate .meta → underlying asset (meta-only locks now exist post-0.2.0;
                // LoadAssetAtPath on a .meta returns null, which would silently no-op the ping).
                string pingPath = FileLockManager.GetLockabilityCheckPath(fileLock.path);
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(pingPath);
                if (asset != null)
                {
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
                Event.current.Use();
            }

            EditorGUILayout.Space();
        }

        private void DrawLockActionButtons(FileLock fileLock, bool isMyLock, bool isStale)
        {
            if (isMyLock)
            {
                bool hasChanges = FileLockProjectWindowOverlay.HasCachedUncommittedChanges(fileLock.path);
                GUI.enabled = hasChanges;
                if (GUILayout.Button("Revert & Unlock", GUILayout.Width(120)))
                {
                    if (EditorUtility.DisplayDialog("Revert and Unlock",
                        $"This will revert all uncommitted changes to '{Path.GetFileName(fileLock.path)}' and unlock it.\n\nAre you sure?",
                        "Yes", "Cancel"))
                    {
                        FileLockManager.Instance.RevertAndUnlockFile(fileLock.path);
                        Repaint();
                    }
                }
                GUI.enabled = true;
                if (GUILayout.Button("Unlock", GUILayout.Width(70)))
                {
                    FileLockManager.Instance.UnlockFile(fileLock.path);
                    Repaint();
                }
            }
            else if (isStale)
            {
                if (GUILayout.Button("Force Unlock", GUILayout.Width(100)))
                {
                    if (ShowForceUnlockDialog(fileLock, isStale))
                    {
                        FileLockManager.Instance.UnlockFile(fileLock.path, force: true);
                        Repaint();
                    }
                }
            }
            else if (FileLockManager.Instance.IsUserAdmin())
            {
                if (GUILayout.Button("Unlock", GUILayout.Width(70)))
                {
                    if (ShowForceUnlockDialog(fileLock, isStale))
                    {
                        FileLockManager.Instance.UnlockFile(fileLock.path, force: true);
                        Repaint();
                    }
                }
            }
        }

        private List<FileLock> FilterLocks(List<FileLock> locks)
        {
            IEnumerable<FileLock> filtered = locks;
            if (m_ShowOnlyMyLocks)
                filtered = filtered.Where(l => l.owner == FileLockManager.Instance.UserName);
            if (!m_ShowStaleLocks)
                filtered = filtered.Where(l => !l.IsStale());
            if (!string.IsNullOrEmpty(m_LocksSearchFilter))
                filtered = filtered.Where(l => l.path.ToLower().Contains(m_LocksSearchFilter.ToLower()));
            return filtered.ToList();
        }

        private bool CanUnlockLock(FileLock fileLock)
        {
            bool isMyLock = fileLock.owner == FileLockManager.Instance.UserName &&
                            fileLock.machineName == FileLockManager.Instance.MachineName;
            return isMyLock || fileLock.IsStale() || FileLockManager.Instance.IsUserAdmin();
        }

        private bool IsMyCleanLock(FileLock fileLock)
        {
            bool isMyLock = fileLock.owner == FileLockManager.Instance.UserName &&
                            fileLock.machineName == FileLockManager.Instance.MachineName;
            if (!isMyLock) return false;
            return !FileLockProjectWindowOverlay.HasCachedUncommittedChanges(fileLock.path);
        }

        private static string FormatTimeAgo(TimeSpan elapsed)
        {
            if (elapsed.TotalMinutes < 1) return "less than a minute";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} minute{((int)elapsed.TotalMinutes != 1 ? "s" : "")}";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} hour{((int)elapsed.TotalHours != 1 ? "s" : "")}";
            return $"{(int)elapsed.TotalDays} day{((int)elapsed.TotalDays != 1 ? "s" : "")}";
        }

        private bool ShowForceUnlockDialog(FileLock fileLock, bool isStale)
        {
            var elapsed = DateTime.UtcNow - fileLock.GetLockedAtTime();
            string message = $"Force unlock file: {Path.GetFileName(fileLock.path)}\n\n";
            message += $"Owner: {fileLock.ownerName} ({fileLock.owner})\n";
            message += $"Locked: {FormatTimeAgo(elapsed)} ago\n\n";
            if (isStale)
                message += "This lock is stale (>24 hours old).\n\n";
            else
                message += "WARNING: This lock is fresh and the owner may be actively working on this file.\n" +
                           "Force unlocking may disrupt their work and cause potential conflicts.\n\n";
            message += "Are you sure you want to force unlock this file?";
            return EditorUtility.DisplayDialog("Force Unlock", message, "Yes, Force Unlock", "Cancel");
        }

        private void BatchUnlockSelected(List<FileLock> allLocks)
        {
            if (m_SelectedLockPaths.Count == 0) return;

            var selectedLocks = allLocks.Where(l => m_SelectedLockPaths.Contains(l.path)).ToList();
            var myLocks = new List<FileLock>();
            var staleLocks = new List<FileLock>();
            var freshOtherLocks = new List<FileLock>();
            foreach (var fl in selectedLocks)
            {
                bool isMyLock = fl.owner == FileLockManager.Instance.UserName &&
                                fl.machineName == FileLockManager.Instance.MachineName;
                if (isMyLock) myLocks.Add(fl);
                else if (fl.IsStale()) staleLocks.Add(fl);
                else freshOtherLocks.Add(fl);
            }

            var myLocksWithChanges = myLocks
                .Where(l => FileLockProjectWindowOverlay.HasCachedUncommittedChanges(l.path)).ToList();

            string message = $"You are about to unlock {m_SelectedLockPaths.Count} lock(s):\n\n";
            if (myLocks.Count > 0)       message += $"• {myLocks.Count} of your locks\n";
            if (staleLocks.Count > 0)    message += $"• {staleLocks.Count} stale lock(s) (>24hrs old)\n";
            if (freshOtherLocks.Count > 0) message += $"• {freshOtherLocks.Count} fresh lock(s) owned by others (admin override)\n";
            if (myLocksWithChanges.Count > 0)
            {
                message += $"\n⚠ WARNING: {myLocksWithChanges.Count} of your locks have uncommitted changes:\n";
                foreach (var fl in myLocksWithChanges.Take(5))
                    message += $"  - {Path.GetFileName(fl.path)}\n";
                if (myLocksWithChanges.Count > 5)
                    message += $"  ... and {myLocksWithChanges.Count - 5} more\n";
                message += "\nChoose 'Revert & Unlock All' to discard changes, or 'Unlock All' to keep them.\n";
            }
            if (freshOtherLocks.Count > 0)
                message += "\n⚠ WARNING: You are force unlocking fresh locks owned by others.\nThis may disrupt their work.\n";
            message += "\nAre you sure you want to proceed?";

            int choice = EditorUtility.DisplayDialogComplex(
                "Batch Unlock Confirmation",
                message,
                "Unlock All",
                "Cancel",
                myLocksWithChanges.Count > 0 ? "Revert & Unlock All" : "");
            if (choice == 1) return;

            bool revertChanges = (choice == 2);
            var paths = selectedLocks.Select(l => l.path).ToList();
            bool needsForce = selectedLocks.Any(l =>
                l.owner != FileLockManager.Instance.UserName ||
                l.machineName != FileLockManager.Instance.MachineName);

            var result = FileLockManager.Instance.BatchUnlockFiles(paths, force: needsForce, skipRevertDialog: !revertChanges);
            m_SelectedLockPaths.Clear();
            Repaint();

            string resultMessage = $"Batch unlock complete:\n\n✓ Successfully unlocked: {result.successCount}\n";
            if (result.revertedCount > 0) resultMessage += $"↻ Reverted changes: {result.revertedCount}\n";
            if (result.failureCount > 0)
            {
                resultMessage += $"✗ Failed to unlock: {result.failureCount}\n\nFailed files:\n";
                foreach (var failed in result.GetFailed().Take(10))
                {
                    resultMessage += $"  - {Path.GetFileName(failed.path)}\n";
                    if (!string.IsNullOrEmpty(failed.errorMessage))
                        resultMessage += $"    Error: {failed.errorMessage}\n";
                }
                if (result.GetFailed().Count > 10)
                    resultMessage += $"  ... and {result.GetFailed().Count - 10} more\n";
            }
            EditorUtility.DisplayDialog(
                result.failureCount > 0 ? "Batch Unlock Completed with Errors" : "Batch Unlock Successful",
                resultMessage, "OK");
        }

        private void QuickUnlockCleanLocks(List<FileLock> allLocks)
        {
            var eligibleLocks = allLocks.Where(IsMyCleanLock).ToList();
            if (eligibleLocks.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Unlock Unmodified",
                    "No unmodified locks found.\n\nAll your locks either have uncommitted changes or are already unlocked.",
                    "OK");
                return;
            }

            var paths = eligibleLocks.Select(l => l.path).ToList();
            var result = FileLockManager.Instance.BatchUnlockFiles(paths, force: false, skipRevertDialog: true);
            Repaint();

            string resultMessage = $"Unlock unmodified complete:\n\n✓ Successfully unlocked: {result.successCount} unmodified lock(s)\n";
            if (result.failureCount > 0)
            {
                resultMessage += $"✗ Failed to unlock: {result.failureCount}\n\nFailed files:\n";
                foreach (var failed in result.GetFailed().Take(10))
                {
                    resultMessage += $"  - {Path.GetFileName(failed.path)}\n";
                    if (!string.IsNullOrEmpty(failed.errorMessage))
                        resultMessage += $"    Error: {failed.errorMessage}\n";
                }
                if (result.GetFailed().Count > 10)
                    resultMessage += $"  ... and {result.GetFailed().Count - 10} more\n";
            }
            EditorUtility.DisplayDialog(
                result.failureCount > 0 ? "Unlock Completed with Errors" : "Unlock Successful",
                resultMessage, "OK");
        }

        // ----------------------------------------------------------------
        // External-tool divergence reconciler (Part 4)
        // ----------------------------------------------------------------

        /// <summary>Detect external-tool divergence — Missing rows (file gone on disk, still
        /// tracked) and Deleted+Untracked pairs (delete registered but new file's add wasn't).
        /// Renders banners above the regular-changes section with bulk-resolve buttons. The
        /// Deleted+Untracked pair case specifically handles the meta-sibling half-state that
        /// occurs when Unity moves a .meta on disk before/without our OnWillMoveAsset firing.</summary>
        private void DrawExternalDivergenceBanners()
        {
            var missingRows = m_Rows.Where(r => r.status == SvnFileStatus.Missing).ToList();
            var deletedRows = m_Rows.Where(r => r.status == SvnFileStatus.Deleted).ToList();
            var untrackedRows = m_Rows.Where(r => r.status == SvnFileStatus.Untracked).ToList();

            // Quick exit if nothing interesting present.
            if (missingRows.Count == 0 && deletedRows.Count == 0) return;

            // Match Missing → Untracked by basename (1:1 only — multi-candidate stays unpaired).
            var pairs = new List<(Row missing, Row untracked)>();
            var orphanMissing = new List<Row>();
            var usedUntracked = new HashSet<string>();
            foreach (var miss in missingRows)
            {
                string missBase = System.IO.Path.GetFileName(miss.path);
                var candidates = untrackedRows.Where(u =>
                    !usedUntracked.Contains(u.path) &&
                    string.Equals(System.IO.Path.GetFileName(u.path), missBase, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 1)
                {
                    pairs.Add((miss, candidates[0]));
                    usedUntracked.Add(candidates[0].path);
                }
                else
                {
                    orphanMissing.Add(miss);
                }
            }

            // Match Deleted (already svn-rm'd) → Untracked by basename. This catches the
            // meta-half-state case (asset move propagated for .mat but Unity moved the .meta
            // on disk before its OnWillMoveAsset could fire — old .meta got svn-rm'd somewhere
            // in the pipeline but new .meta is sitting at `?`). Resolution: just svn-add the
            // untracked side; the delete is already registered (rename history is lost — fine
            // for meta files).
            var addPairs = new List<(Row deleted, Row untracked)>();
            foreach (var del in deletedRows)
            {
                string delBase = System.IO.Path.GetFileName(del.path);
                var candidates = untrackedRows.Where(u =>
                    !usedUntracked.Contains(u.path) &&
                    string.Equals(System.IO.Path.GetFileName(u.path), delBase, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 1)
                {
                    addPairs.Add((del, candidates[0]));
                    usedUntracked.Add(candidates[0].path);
                }
            }

            if (pairs.Count > 0)
            {
                string firstExample = $"{System.IO.Path.GetFileName(pairs[0].missing.path)} → {System.IO.Path.GetFileName(pairs[0].untracked.path)}";
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox(
                    $"External renames detected: {pairs.Count} likely rename pair(s) — e.g., {firstExample}. " +
                    "These look like files moved outside Unity (terminal, agent, etc.); register them in SVN so the next commit doesn't break.",
                    MessageType.Warning);
                if (GUILayout.Button(new GUIContent(
                        $"Register {pairs.Count} as SVN moves",
                        "Run `svn move` for each detected pair, replacing the orphan Missing+Untracked rows with proper Deleted+Added tracking."),
                        GUILayout.Width(200), GUILayout.Height(38)))
                {
                    _ = DoBulkRegisterExternalRenamesAsync(pairs);
                }
                EditorGUILayout.EndHorizontal();
            }

            if (addPairs.Count > 0)
            {
                string firstExample = $"{System.IO.Path.GetFileName(addPairs[0].deleted.path)} → {System.IO.Path.GetFileName(addPairs[0].untracked.path)}";
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox(
                    $"Half-tracked moves detected: {addPairs.Count} pair(s) — e.g., {firstExample}. " +
                    "The delete side is registered but the new path is still untracked — typically a .meta sibling left behind by a Unity rename. Adding the new path fixes the commit.",
                    MessageType.Warning);
                if (GUILayout.Button(new GUIContent(
                        $"Register {addPairs.Count} new path(s) as SVN adds",
                        "Run `svn add` on each untracked side. The delete side is already registered; this completes the pair as a delete+add (no rename history preserved, fine for meta files)."),
                        GUILayout.Width(220), GUILayout.Height(38)))
                {
                    _ = DoBulkRegisterAddsAsync(addPairs.Select(p => p.untracked.path).ToArray());
                }
                EditorGUILayout.EndHorizontal();
            }

            if (orphanMissing.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox(
                    $"Missing files detected: {orphanMissing.Count} file(s) gone on disk but still tracked in wc.db. " +
                    "These look like files deleted outside Unity. Reconcile them so the next commit doesn't break — " +
                    "committed files are registered as deletes; files that were added but never committed are cleared.",
                    MessageType.Warning);
                if (GUILayout.Button(new GUIContent(
                        $"Reconcile {orphanMissing.Count} missing file(s)",
                        "Routes each through svn-core: a committed entry is scheduled for delete (svn rm); a file added but never committed is reverted to clear the orphaned add (svn delete would fail on it)."),
                        GUILayout.Width(200), GUILayout.Height(38)))
                {
                    _ = DoBulkRegisterExternalDeletesAsync(orphanMissing.Select(r => r.path).ToArray());
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private async Task DoBulkRegisterExternalRenamesAsync(List<(Row missing, Row untracked)> pairs)
        {
            m_StatusBanner = $"Registering {pairs.Count} external rename(s) as SVN moves…";
            Repaint();
            int succeeded = 0;
            var failed = new List<string>();
            foreach (var (miss, untracked) in pairs)
            {
                // The untracked file already exists on disk; the missing file is gone. We can't
                // call `svn move` (source missing). Instead: `svn rm OLD` + `svn add NEW`.
                // This loses rename-history within SVN but matches the actual filesystem state.
                var rmResult = await SvnCommandRunner.DeleteAsync(new[] { miss.path });
                if (!rmResult.success)
                {
                    failed.Add(System.IO.Path.GetFileName(miss.path));
                    Debug.LogError($"[FrgVersionControl] external-rename rm failed for {miss.path}.\n" +
                                   $"Output:\n{rmResult.output}\nError:\n{rmResult.error}");
                    continue;
                }
                var addResult = await SvnCommandRunner.AddAsync(new[] { untracked.path });
                if (!addResult.success)
                {
                    failed.Add(System.IO.Path.GetFileName(untracked.path));
                    Debug.LogError($"[FrgVersionControl] external-rename add failed for {untracked.path}.\n" +
                                   $"Output:\n{addResult.output}\nError:\n{addResult.error}");
                    continue;
                }
                succeeded++;
            }

            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                if (failed.Count == 0)
                {
                    m_StatusBanner = $"Registered {succeeded} external rename(s).";
                }
                else
                {
                    string list = string.Join(", ", failed.Take(3));
                    string suffix = failed.Count > 3 ? $", …{failed.Count - 3} more" : "";
                    m_StatusBanner = $"Registered {succeeded}/{pairs.Count} renames. {failed.Count} failed: {list}{suffix}. Open Console for details.";
                }
                _ = SvnStatusCache.RefreshAsync();
                FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();
                Repaint();
            }, null);
        }

        private async Task DoBulkRegisterExternalDeletesAsync(string[] paths)
        {
            m_StatusBanner = $"Reconciling {paths.Length} missing file(s)…";
            Repaint();
            // The core op (mutate.reconcileMissing) owns the per-path decision: a committed
            // entry is scheduled for delete; an orphaned uncommitted add is reverted (a plain
            // svn delete would fail E200005). The engine is a pure pass-through — it just
            // renders what the core did.
            var result = await SvnCommandRunner.ReconcileMissingAsync(paths);

            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                if (!result.success)
                {
                    Debug.LogError($"[FrgVersionControl] reconcile-missing failed.\nError:\n{result.error}");
                    m_StatusBanner = $"Reconcile failed: {result.error?.Split('\n')[0] ?? "(no detail)"}. Open Console for details.";
                }
                else
                {
                    int nDel = result.deleted?.Length ?? 0;
                    int nRev = result.reverted?.Length ?? 0;
                    int nFail = result.failed?.Length ?? 0;
                    if (nFail == 0)
                    {
                        m_StatusBanner = $"Reconciled {nDel} delete(s)" + (nRev > 0 ? $"; cleared {nRev} orphaned add(s)." : ".");
                    }
                    else
                    {
                        if (result.failed != null)
                        {
                            foreach (var f in result.failed)
                                Debug.LogError($"[FrgVersionControl] reconcile-missing: '{f.path}' failed: {f.error}");
                        }
                        m_StatusBanner = $"Reconciled {nDel + nRev} file(s); {nFail} failed. Open Console for details.";
                    }
                }
                _ = SvnStatusCache.RefreshAsync();
                FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();
                Repaint();
            }, null);
        }

        private async Task DoBulkRegisterAddsAsync(string[] paths)
        {
            m_StatusBanner = $"Registering {paths.Length} untracked file(s) as SVN adds…";
            Repaint();
            var result = await SvnCommandRunner.AddAsync(paths);

            SvnCommandRunner.s_UnityMainSync.Post(_ =>
            {
                if (result.success)
                {
                    m_StatusBanner = $"Registered {paths.Length} file(s) as SVN adds. Delete+add pairs now commit-ready.";
                }
                else
                {
                    Debug.LogError($"[FrgVersionControl] external-add failed.\n" +
                                   $"Output:\n{result.output}\nError:\n{result.error}");
                    m_StatusBanner = $"Register adds failed: {result.error?.Split('\n')[0] ?? "(no detail)"}. Open Console for details.";
                }
                _ = SvnStatusCache.RefreshAsync();
                FileLockProjectWindowOverlay.RefreshGlobalSvnStatusCache();
                Repaint();
            }, null);
        }

        // ----------------------------------------------------------------
        // Footer (cache status + Auto-Lock toggle + Run Maintenance)
        // Migrated from FileLockWindow. Visible on all tabs.
        // ----------------------------------------------------------------

        private void DrawCacheStatusBar()
        {
            double now = EditorApplication.timeSinceStartup;
            if (m_CachedCacheBytes < 0 || (now - m_CacheReadAt) > 5.0)
            {
                m_CachedCacheBytes = FileLockMaintenance.MeasurePristineBytes(SvnCommandRunner.ProjectRoot);
                m_CachedCacheCount = FileLockMaintenance.CountPristineObjects(SvnCommandRunner.ProjectRoot);
                m_CacheReadAt = now;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label(
                $".svn/pristine: {FileLockMaintenance.FormatBytes(m_CachedCacheBytes)} " +
                $"({m_CachedCacheCount} objects)",
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            bool autoLock = GUILayout.Toggle(FileLockManager.AutoLockEnabled, "Auto-Lock", EditorStyles.toolbarButton);
            if (autoLock != FileLockManager.AutoLockEnabled)
                FileLockManager.AutoLockEnabled = autoLock;
            if (GUILayout.Button("Setup Wizard", EditorStyles.toolbarButton, GUILayout.Width(110)))
            {
                FileLockSetupWizard.ShowWizard();
            }
            if (GUILayout.Button("Run Maintenance", EditorStyles.toolbarButton, GUILayout.Width(120)))
            {
                FileLockMaintenance.RunMaintenanceNow();
                m_CachedCacheBytes = -1; // force refresh on next repaint
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
