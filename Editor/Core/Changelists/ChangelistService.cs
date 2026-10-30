using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Core.Changelists
{
    /// <summary>
    /// Orchestration layer for user-created changelists. All UI changelist operations route
    /// through here. Reserved-name enforcement lives in <see cref="ValidateName"/> — the single
    /// chokepoint that ensures no user can create or assign-to `ignore-on-commit` (reserved
    /// exclusively for the FRG override workflow per project hard rule).
    ///
    /// Membership lives in SVN's wc.db (queried via <see cref="SvnCommandRunner.GetAllChangelistMembership"/>);
    /// user-created-but-empty names live in <see cref="UserChangelistStore"/>'s sidecar.
    /// The "effective changelist list" surfaced by <see cref="GetAllChangelists"/> is the
    /// union of both, with <see cref="ReservedChangelistName"/> always included (marked reserved).
    /// </summary>
    public static class ChangelistService
    {
        /// <summary>The changelist name reserved for the override workflow. UI must never let
        /// the user create or assign-to this name; the only legitimate caller is
        /// <c>FileLockOverrideManager</c>.</summary>
        public const string ReservedChangelistName = "ignore-on-commit";

        /// <summary>The Changes tab's label for files that are in no changelist.</summary>
        public const string NoChangelistBucketName = "(no changelist)";

        public enum DeleteMode
        {
            /// <summary>Refuse delete if any file is still assigned to the changelist.</summary>
            RemoveOnlyIfEmpty,
            /// <summary>Unassign every member first (does NOT revert file edits — just clears
            /// the tag), then delete the name from the sidecar.</summary>
            UnassignAllFirst,
        }

        public struct ValidationResult
        {
            public bool ok;
            public string reason;
            public static ValidationResult Ok() => new ValidationResult { ok = true };
            public static ValidationResult Fail(string reason) => new ValidationResult { ok = false, reason = reason };
        }

        /// <summary>One row returned by <see cref="GetAllChangelists"/>. <see cref="paths"/> is the
        /// current SVN-tracked membership; empty for sidecar-only user-created changelists.</summary>
        public class ChangelistInfo
        {
            public string name;
            public bool isReserved;             // true for "ignore-on-commit"
            public bool isPersisted;            // true if recorded in the user-changelist sidecar
            public List<string> paths = new List<string>();
        }

        /// <summary>Single chokepoint for changelist-name validation. Used by Create and
        /// by Assign (so even a caller that bypasses Create is rejected when it tries to
        /// move files into the reserved name).</summary>
        public static ValidationResult ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return ValidationResult.Fail("Changelist name cannot be empty.");
            }
            if (string.Equals(name, ReservedChangelistName, StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Fail(
                    $"'{ReservedChangelistName}' is reserved for the FRG override workflow. " +
                    "Pick a different name.");
            }
            foreach (var c in name)
            {
                if (char.IsControl(c))
                {
                    return ValidationResult.Fail("Changelist name cannot contain control characters.");
                }
            }
            if (string.Equals(name.Trim(), NoChangelistBucketName, StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Fail($"'{NoChangelistBucketName}' is the label for files without a changelist. Pick a different name.");
            }
            if (name.IndexOfAny(new[] { '"', '\\', '/', '\n', '\r', '\t' }) >= 0)
            {
                return ValidationResult.Fail("Changelist name cannot contain quotes, slashes, or whitespace characters.");
            }
            return ValidationResult.Ok();
        }

        /// <summary>Create a new user changelist by name. Persists the name in the sidecar
        /// so it appears in the picker before any file is assigned (matches TortoiseSVN UX).
        /// Returns the validation result; on success, the name is recorded.</summary>
        public static ValidationResult Create(string name)
        {
            var v = ValidateName(name);
            if (!v.ok) return v;

            var outcome = UserChangelistStore.Add(name);
            if (!outcome.ok) return ValidationResult.Fail(outcome.error);
            if (!outcome.changed)
            {
                return ValidationResult.Fail($"Changelist '{name}' already exists.");
            }
            return ValidationResult.Ok();
        }

        /// <summary>Rename a user changelist. Updates SVN membership for every file currently
        /// assigned to the old name, then updates the sidecar.
        ///
        /// <paramref name="sidecarOnly"/> short-circuits the SVN call entirely (no
        /// <c>svn status --xml</c>, no <c>svn changelist</c>) — pass true when the caller
        /// knows the changelist has zero SVN-tracked members (typical for a freshly-created
        /// list still in inline-rename). On a large working copy the SVN status query alone
        /// can take seconds; the fast path is instant.</summary>
        public static ValidationResult Rename(string oldName, string newName, bool sidecarOnly = false)
        {
            var v = ValidateName(newName);
            if (!v.ok) return v;
            if (string.Equals(oldName, ReservedChangelistName, StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Fail(
                    $"'{ReservedChangelistName}' is reserved for the FRG override workflow and cannot be renamed.");
            }
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                return ValidationResult.Ok();
            }

            if (!sidecarOnly)
            {
                // Reassign any SVN-tracked members.
                var membership = SvnCommandRunner.GetAllChangelistMembership();
                if (membership.TryGetValue(oldName, out var members) && members.Count > 0)
                {
                    var result = SvnCommandRunner.MoveBetweenChangelists(members.ToArray(), oldName, newName);
                    if (!result.success)
                    {
                        return ValidationResult.Fail(
                            $"SVN reassign failed: {result.error?.Trim()}. Try again or check the working copy state.");
                    }
                }
            }

            var outcome = UserChangelistStore.Rename(oldName, newName);
            return outcome.ok ? ValidationResult.Ok() : ValidationResult.Fail(outcome.error);
        }

        /// <summary>Delete a user changelist. <see cref="DeleteMode.RemoveOnlyIfEmpty"/> refuses
        /// when files are still assigned; <see cref="DeleteMode.UnassignAllFirst"/> clears
        /// membership (files keep their edits) and then deletes the sidecar entry.</summary>
        public static ValidationResult Delete(string name, DeleteMode mode)
        {
            if (string.Equals(name, ReservedChangelistName, StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Fail(
                    $"'{ReservedChangelistName}' is reserved for the FRG override workflow and cannot be deleted.");
            }

            var membership = SvnCommandRunner.GetAllChangelistMembership();
            membership.TryGetValue(name, out var members);
            int memberCount = members?.Count ?? 0;

            if (mode == DeleteMode.RemoveOnlyIfEmpty && memberCount > 0)
            {
                return ValidationResult.Fail(
                    $"Changelist '{name}' still has {memberCount} file(s) assigned. " +
                    "Unassign first or use UnassignAllFirst mode.");
            }

            if (memberCount > 0)
            {
                var result = SvnCommandRunner.RemoveFromChangelist(members.ToArray());
                if (!result.success)
                {
                    return ValidationResult.Fail(
                        $"SVN unassign failed: {result.error?.Trim()}. Try again or check the working copy state.");
                }
            }

            var outcome = UserChangelistStore.Remove(name);
            return outcome.ok ? ValidationResult.Ok() : ValidationResult.Fail(outcome.error);
        }

        /// <summary>Assign paths to a changelist. Blocks any attempt to target the reserved
        /// name. Also auto-adds the target name to the sidecar so empty user changelists
        /// stay visible after the last file leaves.</summary>
        public static ValidationResult AssignPaths(string[] paths, string changelist)
        {
            if (paths == null || paths.Length == 0)
            {
                return ValidationResult.Fail("No paths supplied.");
            }
            var v = ValidateName(changelist);
            if (!v.ok) return v;

            var result = SvnCommandRunner.AddToChangelist(paths, changelist);
            if (!result.success)
            {
                return ValidationResult.Fail(
                    $"SVN assign failed: {result.error?.Trim()}. Check that the paths are versioned and the WC isn't locked.");
            }

            var outcome = UserChangelistStore.Add(changelist);
            if (!outcome.ok)
            {
                UnityEngine.Debug.LogWarning($"[FRGVersionControl] Files were assigned to '{changelist}', but the name could not be saved as a declared changelist: {outcome.error}");
            }
            return ValidationResult.Ok();
        }

        /// <summary>Remove paths from any changelist (SVN supports only one changelist per
        /// file, so "remove" means unassign). Does not touch the sidecar — empty user
        /// changelists remain visible as drop targets.</summary>
        public static ValidationResult RemovePaths(string[] paths)
        {
            if (paths == null || paths.Length == 0)
            {
                return ValidationResult.Fail("No paths supplied.");
            }
            var result = SvnCommandRunner.RemoveFromChangelist(paths);
            if (!result.success)
            {
                return ValidationResult.Fail(
                    $"SVN unassign failed: {result.error?.Trim()}. Check that the paths are versioned and the WC isn't locked.");
            }
            return ValidationResult.Ok();
        }

        /// <summary>Return every changelist surfaced in the UI: union of SVN-tracked membership
        /// + sidecar-recorded user-created names + the reserved name. Sorted: reserved last,
        /// everything else alphabetical (caller can re-sort if needed).</summary>
        public static List<ChangelistInfo> GetAllChangelists()
        {
            var byName = new Dictionary<string, ChangelistInfo>(StringComparer.Ordinal);

            var membership = SvnCommandRunner.GetAllChangelistMembership();
            foreach (var kv in membership)
            {
                byName[kv.Key] = new ChangelistInfo
                {
                    name = kv.Key,
                    isReserved = string.Equals(kv.Key, ReservedChangelistName, StringComparison.OrdinalIgnoreCase),
                    isPersisted = false,
                    paths = kv.Value ?? new List<string>(),
                };
            }

            foreach (var name in UserChangelistStore.ReadSnapshot())
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (byName.TryGetValue(name, out var existing))
                {
                    existing.isPersisted = true;
                }
                else
                {
                    byName[name] = new ChangelistInfo
                    {
                        name = name,
                        isReserved = false,
                        isPersisted = true,
                        paths = new List<string>(),
                    };
                }
            }

            // Always surface the reserved entry, even when empty, so the UI can render the
            // locked group as a drop-reject target.
            if (!byName.ContainsKey(ReservedChangelistName))
            {
                byName[ReservedChangelistName] = new ChangelistInfo
                {
                    name = ReservedChangelistName,
                    isReserved = true,
                    isPersisted = false,
                    paths = new List<string>(),
                };
            }

            return byName.Values
                .OrderBy(i => i.isReserved ? 1 : 0)
                .ThenBy(i => i.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static Task<ValidationResult> AssignPathsAsync(string[] paths, string changelist, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return AssignPaths(paths, changelist); }, ct);

        public static Task<ValidationResult> RemovePathsAsync(string[] paths, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return RemovePaths(paths); }, ct);

        public static Task<ValidationResult> RenameAsync(string oldName, string newName, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Rename(oldName, newName); }, ct);

        public static Task<ValidationResult> DeleteAsync(string name, DeleteMode mode, CancellationToken ct = default)
            => Task.Run(() => { ct.ThrowIfCancellationRequested(); return Delete(name, mode); }, ct);
    }
}
