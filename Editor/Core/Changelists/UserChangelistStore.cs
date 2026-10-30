using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Core.Changelists
{
    /// <summary>
    /// User-created changelist NAMES that aren't yet (or are no longer) backed by any file
    /// membership. SVN garbage-collects a changelist when its last file leaves, but the
    /// TortoiseSVN UX shows a name immediately on creation, so svn-core keeps the declared names
    /// in its sidecar (SVN_CORE_API.md §5.6); SVN's wc.db remains the source of truth for
    /// membership. Names are working-copy-wide, like svn changelists.
    ///
    /// Since 0.2.92 this is a pure IPC shim (cl.userListGet/Add/Remove/Rename) with a last-good
    /// cache for drawing: the editor never reads or writes the sidecar itself.
    /// </summary>
    public static class UserChangelistStore
    {
        public struct Outcome
        {
            public bool ok;
            public bool changed;
            public string error;
        }

        private const double RefreshAfterSeconds = 5.0;
        private const double FailedRetrySeconds = 1.0;
        private const double WarnEverySeconds = 60.0;

        private static readonly object s_Lock = new object();
        private static List<string> s_Cache;
        private static DateTime s_CachedAt = DateTime.MinValue;
        private static DateTime s_LastWarn = DateTime.MinValue;

        /// <summary>The declared names. Re-read from svn-core at most every few seconds; when the
        /// read fails (svn-core busy or unavailable) the last good list is returned.</summary>
        public static List<string> ReadSnapshot()
        {
            lock (s_Lock)
            {
                if ((DateTime.UtcNow - s_CachedAt).TotalSeconds < RefreshAfterSeconds)
                {
                    return s_Cache != null ? new List<string>(s_Cache) : new List<string>();
                }
            }

            string error = null;
            List<string> names = null;
            try
            {
                var client = FileLockManager.Instance.IpcClient;
                if (client.IsAvailable)
                {
                    var result = client.InvokeRawAsync("cl.userListGet", "{}", client.BuildDefaultContext()).GetAwaiter().GetResult();
                    if (result.Ok) names = IpcClient.IpcResponseParsers.ParseNamesArray(result.RawLine);
                    else error = result.ErrorMessage;
                }
                else
                {
                    error = "svn-core.exe is not available";
                }
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            lock (s_Lock)
            {
                if (names != null)
                {
                    s_Cache = names;
                    s_CachedAt = DateTime.UtcNow;
                    return new List<string>(names);
                }
                s_CachedAt = DateTime.UtcNow - TimeSpan.FromSeconds(RefreshAfterSeconds - FailedRetrySeconds);
                if ((DateTime.UtcNow - s_LastWarn).TotalSeconds >= WarnEverySeconds)
                {
                    s_LastWarn = DateTime.UtcNow;
                    Debug.LogWarning($"[FRGVersionControl] Could not read the changelist names ({error}); showing the last known list.");
                }
                return s_Cache != null ? new List<string>(s_Cache) : new List<string>();
            }
        }

        public static Outcome Add(string name)
        {
            var outcome = Mutate("cl.userListAdd", "{\"name\":\"" + SvnCommandRunner.IpcEscape(name) + "\"}", "added");
            if (outcome.ok) UpdateCache(names => { if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name); });
            return outcome;
        }

        public static Outcome Remove(string name)
        {
            var outcome = Mutate("cl.userListRemove", "{\"name\":\"" + SvnCommandRunner.IpcEscape(name) + "\"}", "removed");
            if (outcome.ok) UpdateCache(names => names.RemoveAll(n => string.Equals(n, name, StringComparison.Ordinal)));
            return outcome;
        }

        public static Outcome Rename(string from, string to)
        {
            var outcome = Mutate("cl.userListRename",
                "{\"from\":\"" + SvnCommandRunner.IpcEscape(from) + "\",\"to\":\"" + SvnCommandRunner.IpcEscape(to) + "\"}", "renamed");
            if (outcome.ok)
            {
                UpdateCache(names =>
                {
                    names.RemoveAll(n => string.Equals(n, from, StringComparison.Ordinal));
                    if (!names.Contains(to, StringComparer.Ordinal)) names.Add(to);
                });
            }
            return outcome;
        }

        private static Outcome Mutate(string op, string argsJson, string changedField)
        {
            var raw = SvnCommandRunner.InvokeMutating(op, argsJson, out var error);
            if (error != null)
            {
                Invalidate();
                return new Outcome { ok = false, error = error };
            }
            bool changed = IpcClient.IpcJson.ReadScalar(raw.RawLine, changedField, inResult: true) == "true";
            return new Outcome { ok = true, changed = changed };
        }

        private static void UpdateCache(Action<List<string>> change)
        {
            lock (s_Lock)
            {
                if (s_Cache == null) return;
                change(s_Cache);
                s_CachedAt = DateTime.UtcNow;
            }
        }

        private static void Invalidate()
        {
            lock (s_Lock) s_CachedAt = DateTime.MinValue;
        }
    }
}
