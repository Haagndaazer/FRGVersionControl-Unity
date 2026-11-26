using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.Services;

namespace FRGVersionControl.UI
{
    /// <summary>
    /// Shared restore-to-revision runner for the History tab (multi-select) and the per-asset
    /// history window (single file). One implementation of confirm → sequential restore →
    /// local-changes back-up → summary so the two callers can never drift. Each file keeps its own
    /// atomic replace + .pre-restore~ backup in the core (mutate.restoreRevision); this orchestrates
    /// the UX around it, holding a reload lock for the whole batch so a mid-batch recompile can't
    /// tear down the IPC client. Read-only files are never forced; local changes are overwritten
    /// only after one explicit, consolidated yes.
    /// </summary>
    internal static class RestoreFlow
    {
        internal readonly struct Item
        {
            public readonly string Key;        // the caller's selection key (repo path / asset path); echoed back as "resolved"
            public readonly string ProjectRel;
            public readonly int Revision;
            public readonly string Label;       // shown in dialogs/summary — project-relative path, so same-named files differ
            public Item(string key, string projectRel, int revision, string label)
            {
                Key = key;
                ProjectRel = projectRel;
                Revision = revision;
                Label = label;
            }
        }

        internal readonly struct Skip
        {
            public readonly string Label;
            public readonly string Reason;
            public Skip(string label, string reason)
            {
                Label = label;
                Reason = reason;
            }
        }

        private const int NameCap = 10;

        /// <summary>Runs the restore batch. The caller MUST have deferred this out of OnGUI
        /// (EditorApplication.delayCall) and set its own in-flight flag first. <paramref name="onNotice"/>
        /// receives progress and the final summary; <paramref name="onChanged"/> fires once (after the
        /// reload lock is released) when at least one file changed, so the caller can refresh assets and
        /// re-drive any diff view; <paramref name="onDone"/> always fires last, so the caller can clear
        /// its in-flight flag and selection. All callbacks run on the main thread.</summary>
        /// <summary><paramref name="onDone"/> receives the caller keys (Item.Key) of files that actually
        /// restored or were already current, so the caller clears only those from its selection and leaves
        /// read-only / failed / skipped files checked for a retry. It is empty when the user cancels.</summary>
        internal static async void Run(
            IReadOnlyList<Item> items,
            IReadOnlyList<Skip> preSkipped,
            Action<string> onNotice,
            Action onChanged,
            Action<IReadOnlyList<string>> onDone)
        {
            var resolved = new List<string>();
            try
            {
                await RunCore(items, preSkipped, onNotice, onChanged, resolved);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FRGVersionControl.Restore] Restore batch threw: {ex}");
                onNotice?.Invoke("Restore failed unexpectedly. Check the console for details.");
            }
            finally
            {
                onDone?.Invoke(resolved);
            }
        }

        private static async Task RunCore(
            IReadOnlyList<Item> items,
            IReadOnlyList<Skip> preSkipped,
            Action<string> onNotice,
            Action onChanged,
            List<string> resolved)
        {
            var toAttempt = items ?? Array.Empty<Item>();
            var skipped = new List<string>();
            if (preSkipped != null)
            {
                foreach (var s in preSkipped) skipped.Add($"{s.Label} ({s.Reason})");
            }

            if (toAttempt.Count == 0)
            {
                onNotice?.Invoke(skipped.Count > 0
                    ? "Nothing could be restored. " + JoinCapped("Skipped", skipped)
                    : "Nothing selected to restore.");
                return;
            }

            if (!Confirm(toAttempt, preSkipped))
            {
                onNotice?.Invoke(null);
                return;
            }

            var done = new List<string>();        // changed, no backup
            var backedUp = new List<string>();     // changed, backup written
            var noop = new List<string>();         // already current
            var readOnly = new List<string>();
            var failed = new List<string>();       // "name — reason"
            var localChanges = new List<Item>();
            var notAttempted = new List<string>();
            bool anyChanged = false;

            var hold = EditorReloadGuard.Hold();
            try
            {
                bool aborted = false;
                for (int i = 0; i < toAttempt.Count; i++)
                {
                    var it = toAttempt[i];
                    onNotice?.Invoke($"Restoring {i + 1} of {toAttempt.Count}: {it.Label}…");
                    var res = await SvnCommandRunner.RestoreRevisionAsync(it.ProjectRel, it.Revision, false);
                    if (res != null && res.busy)
                    {
                        // One bounded retry; a second consecutive busy aborts the rest rather than hammer.
                        await Task.Delay(1000);
                        res = await SvnCommandRunner.RestoreRevisionAsync(it.ProjectRel, it.Revision, false);
                        if (res != null && res.busy)
                        {
                            for (int j = i; j < toAttempt.Count; j++) notAttempted.Add(toAttempt[j].Label);
                            aborted = true;
                            break;
                        }
                    }
                    Bucket(it, res, resolved, done, backedUp, noop, readOnly, failed, localChanges, ref anyChanged);
                }

                if (localChanges.Count > 0)
                {
                    if (aborted)
                    {
                        // Phase 2 is skipped on an abort — report these so they aren't silently dropped.
                        foreach (var it in localChanges) skipped.Add($"{it.Label} (has local changes — not restored)");
                    }
                    else if (ConfirmOverwrite(localChanges))
                    {
                        var ignore = new List<Item>();
                        for (int i = 0; i < localChanges.Count; i++)
                        {
                            var it = localChanges[i];
                            onNotice?.Invoke($"Backing up and restoring {i + 1} of {localChanges.Count}: {it.Label}…");
                            var res = await SvnCommandRunner.RestoreRevisionAsync(it.ProjectRel, it.Revision, true);
                            // overwrite:true doesn't re-report localChanges; ignore list stays empty.
                            Bucket(it, res, resolved, done, backedUp, noop, readOnly, failed, ignore, ref anyChanged);
                        }
                    }
                    else
                    {
                        foreach (var it in localChanges) skipped.Add($"{it.Label} (kept your local changes)");
                    }
                }
            }
            finally
            {
                hold.Dispose();
                // After the reload lock is released, and even if the loop threw: refresh if anything changed.
                if (anyChanged) onChanged?.Invoke();
            }

            onNotice?.Invoke(Summary(done, backedUp, noop, readOnly, failed, notAttempted, skipped));
        }

        private static void Bucket(
            in Item it, SvnRestoreResult res, List<string> resolved,
            List<string> done, List<string> backedUp, List<string> noop,
            List<string> readOnly, List<string> failed, List<Item> localChanges, ref bool anyChanged)
        {
            if (res == null) { failed.Add($"{it.Label} — could not restore"); return; }
            if (!string.IsNullOrEmpty(res.error)) { failed.Add($"{it.Label} — {res.error}"); return; }
            if (string.Equals(res.blocked, "readOnly", StringComparison.Ordinal)) { readOnly.Add(it.Label); return; }
            if (string.Equals(res.blocked, "localChanges", StringComparison.Ordinal)) { localChanges.Add(it); return; }
            if (res.restored)
            {
                resolved.Add(it.Key);   // dealt with — the caller clears it from its selection
                if (!res.changed) { noop.Add(it.Label); return; }
                anyChanged = true;
                if (!string.IsNullOrEmpty(res.snapshotPath)) backedUp.Add(it.Label);
                else done.Add(it.Label);
                return;
            }
            failed.Add($"{it.Label} — could not restore");
        }

        private static bool Confirm(IReadOnlyList<Item> items, IReadOnlyList<Skip> preSkipped)
        {
            var sb = new StringBuilder();
            sb.Append(items.Count == 1
                ? $"Restore {items[0].Label} to r{items[0].Revision}?"
                : $"Restore {items.Count} file(s) to their selected revision(s)?");
            sb.Append("\n\nThis replaces each file in your working copy with the committed version shown. " +
                      "You can review the change and then commit it to keep it.\n\n");
            int shown = Math.Min(items.Count, NameCap);
            for (int i = 0; i < shown; i++) sb.Append($"  {items[i].Label}  →  r{items[i].Revision}\n");
            if (items.Count > NameCap) sb.Append($"  …and {items.Count - NameCap} more\n");

            bool anyMeta = false;
            foreach (var it in items)
            {
                if (it.Label.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) { anyMeta = true; break; }
            }
            if (anyMeta)
            {
                sb.Append("\nNote: a .meta file is selected. Restoring a .meta to an older revision can change " +
                          "an asset's import settings or GUID — review it carefully before committing.");
            }
            if (preSkipped != null && preSkipped.Count > 0)
            {
                sb.Append($"\n{preSkipped.Count} other selected file(s) can't be restored here and will be skipped.");
            }
            return EditorUtility.DisplayDialog("Restore file(s)", sb.ToString(), "Restore", "Cancel");
        }

        private static bool ConfirmOverwrite(List<Item> items)
        {
            var sb = new StringBuilder();
            sb.Append(items.Count == 1
                ? $"{items[0].Label} has local changes."
                : $"{items.Count} file(s) have local changes:");
            sb.Append("\n\n");
            int shown = Math.Min(items.Count, NameCap);
            for (int i = 0; i < shown; i++) sb.Append($"  {items[i].Label}\n");
            if (items.Count > NameCap) sb.Append($"  …and {items.Count - NameCap} more\n");
            sb.Append("\nRestore them too and back up your current version(s) next to the originals?");
            return EditorUtility.DisplayDialog("Replace your current changes?", sb.ToString(),
                "Restore and back up", "Cancel");
        }

        private static string Summary(
            List<string> done, List<string> backedUp, List<string> noop,
            List<string> readOnly, List<string> failed, List<string> notAttempted, List<string> skipped)
        {
            var sb = new StringBuilder();
            int restored = done.Count + backedUp.Count;
            if (restored > 0)
            {
                sb.Append($"Restored {restored} file(s)");
                if (backedUp.Count > 0) sb.Append($" ({backedUp.Count} backed up next to the original)");
                sb.Append(". Review in the Changes tab, then commit to keep the change(s).");
            }
            if (noop.Count > 0) Append(sb, $"{noop.Count} already current.");
            if (readOnly.Count > 0)
                Append(sb, JoinCapped("Skipped — read-only, lock them first then restore again", readOnly));
            if (failed.Count > 0) Append(sb, JoinCapped("Couldn't restore", failed));
            if (notAttempted.Count > 0) Append(sb, JoinCapped("Not attempted — SVN was busy, try again", notAttempted));
            if (skipped.Count > 0) Append(sb, JoinCapped("Skipped", skipped));
            if (sb.Length == 0) sb.Append("Nothing to restore.");

            LogFull(done, backedUp, noop, readOnly, failed, notAttempted, skipped);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string s)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
        }

        private static string JoinCapped(string prefix, List<string> names)
        {
            int n = names.Count;
            var shown = n > NameCap ? names.GetRange(0, NameCap) : names;
            string list = string.Join(", ", shown);
            if (n > NameCap) list += $", and {n - NameCap} more";
            return $"{prefix}: {list}.";
        }

        private static void LogFull(
            List<string> done, List<string> backedUp, List<string> noop,
            List<string> readOnly, List<string> failed, List<string> notAttempted, List<string> skipped)
        {
            if (failed.Count == 0 && readOnly.Count == 0 && notAttempted.Count == 0 && skipped.Count == 0) return;
            var sb = new StringBuilder("[FRGVersionControl.Restore] batch result\n");
            Section(sb, "restored", done);
            Section(sb, "restored (backed up)", backedUp);
            Section(sb, "already current", noop);
            Section(sb, "read-only (skipped)", readOnly);
            Section(sb, "failed", failed);
            Section(sb, "not attempted", notAttempted);
            Section(sb, "skipped", skipped);
            Debug.Log(sb.ToString());
        }

        private static void Section(StringBuilder sb, string label, List<string> names)
        {
            if (names.Count == 0) return;
            sb.Append($"  {label} ({names.Count}): ").Append(string.Join(", ", names)).Append('\n');
        }
    }
}
