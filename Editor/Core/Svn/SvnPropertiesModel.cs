using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FRGVersionControl.Core.Svn
{
    public interface IPropertyOps
    {
        Task<PropListResult> List(string path);
        Task<SvnResult> Set(string path, string name, string value);
        Task<SvnResult> Delete(string path, string name);
    }

    public sealed class IpcPropertyOps : IPropertyOps
    {
        public static readonly IpcPropertyOps Instance = new IpcPropertyOps();
        public Task<PropListResult> List(string path) => SvnCommandRunner.PropListAsync(path);
        public Task<SvnResult> Set(string path, string name, string value) => SvnCommandRunner.PropSetAsync(path, name, value);
        public Task<SvnResult> Delete(string path, string name) => SvnCommandRunner.PropDeleteAsync(path, name);
    }

    public struct PropChange
    {
        public string Name;
        public bool Delete;
        public string Value;

        public override string ToString() => Delete ? "remove " + Name : "set " + Name;
    }

    public sealed class PropApplyResult
    {
        public List<PropChange> Applied = new List<PropChange>();
        public PropChange? Failed;
        public string Error;
        public bool Success => Failed == null;
    }

    /// <summary>
    /// State and change planning for the Edit SVN Properties window. IPC happens only in the
    /// static <see cref="FetchAsync"/> / <see cref="WriteAsync"/> (safe on any thread); the
    /// instance state is changed only by <see cref="Publish"/> and the Desired setters, which the
    /// window calls on the main thread, so a background read never mutates what the UI draws.
    /// </summary>
    public sealed class SvnPropertiesModel
    {
        public const string NeedsLock = "svn:needs-lock";
        public const string Ignore = "svn:ignore";
        public const int OtherValuePreviewLength = 80;

        public string Path { get; }
        public bool IsDirectory { get; }
        public bool NeedsLockCurrent { get; private set; }
        public bool NeedsLockDesired { get; set; }
        public string IgnoreCurrent { get; private set; } = "";
        public string IgnoreDesired { get; set; } = "";
        public IReadOnlyList<KeyValuePair<string, string>> OtherProperties { get; private set; } = Array.Empty<KeyValuePair<string, string>>();
        public string LoadError { get; private set; }
        public bool Loaded { get; private set; }

        public SvnPropertiesModel(string path, bool isDirectory)
        {
            Path = path;
            IsDirectory = isDirectory;
        }

        public static Task<PropListResult> FetchAsync(IPropertyOps ops, string path) => ops.List(path);

        /// <summary>Replaces the whole state from one read; unapplied edits are discarded.</summary>
        public void Publish(PropListResult r)
        {
            if (r == null || !r.success)
            {
                LoadError = r?.error ?? "No response from svn-core. Click Reload to try again.";
                Loaded = false;
                OtherProperties = Array.Empty<KeyValuePair<string, string>>();
                return;
            }
            NeedsLockCurrent = !IsDirectory && r.props.ContainsKey(NeedsLock);
            NeedsLockDesired = NeedsLockCurrent;
            IgnoreCurrent = IsDirectory && r.props.TryGetValue(Ignore, out var ig) ? ig : "";
            IgnoreDesired = IgnoreCurrent;
            OtherProperties = r.props
                .Where(kv => IsDirectory ? kv.Key != Ignore : kv.Key != NeedsLock)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            LoadError = null;
            Loaded = true;
        }

        public async Task LoadAsync(IPropertyOps ops) => Publish(await FetchAsync(ops, Path).ConfigureAwait(false));

        public bool HasPendingChanges => PlanChanges().Count > 0;

        public List<PropChange> PlanChanges() =>
            Plan(IsDirectory, NeedsLockCurrent, NeedsLockDesired, IgnoreCurrent, IgnoreDesired);

        /// <summary>The ordered writes that turn the current state into the desired one:
        /// svn:needs-lock first (files only), then svn:ignore (folders only). An svn:ignore left
        /// blank is a removal; otherwise it is written with Windows line breaks reduced to LF.</summary>
        public static List<PropChange> Plan(bool isDirectory, bool needsLockCurrent, bool needsLockDesired,
                                            string ignoreCurrent, string ignoreDesired)
        {
            var plan = new List<PropChange>();
            if (!isDirectory && needsLockDesired != needsLockCurrent)
            {
                plan.Add(needsLockDesired
                    ? new PropChange { Name = NeedsLock, Value = "*" }
                    : new PropChange { Name = NeedsLock, Delete = true });
            }
            if (isDirectory && !IgnoreEquivalent(ignoreCurrent, ignoreDesired))
            {
                var desired = NormalizeIgnore(ignoreDesired);
                if (desired.Length == 0)
                {
                    if (NormalizeIgnore(ignoreCurrent).Length > 0) plan.Add(new PropChange { Name = Ignore, Delete = true });
                }
                else
                {
                    plan.Add(new PropChange { Name = Ignore, Value = desired + "\n" });
                }
            }
            return plan;
        }

        /// <summary>Two svn:ignore texts mean the same pattern list when they match after line
        /// breaks are reduced to LF and trailing blank lines/whitespace are dropped.</summary>
        public static bool IgnoreEquivalent(string a, string b) =>
            string.Equals(NormalizeIgnore(a), NormalizeIgnore(b), StringComparison.Ordinal);

        public static string NormalizeIgnore(string s) =>
            (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

        /// <summary>Runs <paramref name="plan"/> in order and stops at the first failure.</summary>
        public static async Task<PropApplyResult> WriteAsync(IPropertyOps ops, string path, IReadOnlyList<PropChange> plan)
        {
            var result = new PropApplyResult();
            foreach (var change in plan)
            {
                var r = change.Delete
                    ? await ops.Delete(path, change.Name).ConfigureAwait(false)
                    : await ops.Set(path, change.Name, change.Value).ConfigureAwait(false);
                if (!r.success)
                {
                    result.Failed = change;
                    result.Error = r.error;
                    break;
                }
                result.Applied.Add(change);
            }
            return result;
        }

        /// <summary>Single-threaded convenience: plan, write, then reload what actually landed.</summary>
        public async Task<PropApplyResult> ApplyAsync(IPropertyOps ops)
        {
            var result = await WriteAsync(ops, Path, PlanChanges()).ConfigureAwait(false);
            await LoadAsync(ops).ConfigureAwait(false);
            return result;
        }

        public static string Preview(string value)
        {
            var first = (value ?? "").Replace("\r\n", "\n").Split('\n')[0];
            bool more = first.Length > OtherValuePreviewLength || (value ?? "").IndexOf('\n') >= 0;
            if (first.Length > OtherValuePreviewLength) first = first.Substring(0, OtherValuePreviewLength);
            return more ? first + " …" : first;
        }
    }
}
