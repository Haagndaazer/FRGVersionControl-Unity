using System;
using System.IO;
using System.Threading.Tasks;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;
using UnityEngine;

namespace FRGVersionControl.Core.Diff
{
    public enum DiffRowKind
    {
        Same,
        Add,
        Del,
        Chg,
        Gap,
    }

    public struct DiffRowView
    {
        public DiffRowKind kind;
        public int left;
        public int right;
        public string leftText;
        public string rightText;
        public long skipped;
    }

    /// <summary>The <c>diff.working</c> result (SVN_CORE_API.md §5.14), or the reason there is none.</summary>
    public sealed class WorkingDiff
    {
        public string path;
        public string state;
        public string reason;
        public long baseRevision;
        public string movedFrom;
        public bool hasBase;
        public bool hasWorking;
        public int added;
        public int removed;
        public bool truncated;
        public long limitBytes;
        public DiffRowView[] rows = Array.Empty<DiffRowView>();

        /// <summary>Another operation was running; nothing was compared.</summary>
        public bool busy;
        /// <summary>User-facing failure text ("description. action."); null on success.</summary>
        public string error;

        public bool IsText => error == null && !busy && state == "text";
    }

    public static class WorkingDiffParser
    {
        [Serializable]
        private sealed class Envelope
        {
            public Result result;
        }

        [Serializable]
        private sealed class Result
        {
            public string state;
            public string reason;
            public long baseRevision;
            public string movedFrom;
            public bool hasBase;
            public bool hasWorking;
            public int added;
            public int removed;
            public bool truncated;
            public long limitBytes;
            public RowShim[] rows;
        }

        [Serializable]
        private sealed class RowShim
        {
            public string kind;
            public int left;
            public int right;
            public string leftText;
            public string rightText;
            public long skipped;
        }

        /// <summary>Parses a successful <c>diff.working</c> reply line. Throws on a garbled frame.</summary>
        public static WorkingDiff Parse(string rawLine, string path)
        {
            var env = JsonUtility.FromJson<Envelope>(rawLine);
            if (env == null || env.result == null || string.IsNullOrEmpty(env.result.state))
            {
                throw new FormatException("diff.working reply has no result.state");
            }
            var r = env.result;
            var diff = new WorkingDiff
            {
                path = path,
                state = r.state,
                reason = string.IsNullOrEmpty(r.reason) ? null : r.reason,
                baseRevision = r.baseRevision,
                movedFrom = string.IsNullOrEmpty(r.movedFrom) ? null : r.movedFrom,
                hasBase = r.hasBase,
                hasWorking = r.hasWorking,
                added = r.added,
                removed = r.removed,
                truncated = r.truncated,
                limitBytes = r.limitBytes,
            };
            int count = r.rows?.Length ?? 0;
            diff.rows = new DiffRowView[count];
            for (int i = 0; i < count; i++)
            {
                var s = r.rows[i];
                var kind = ParseKind(s.kind);
                diff.rows[i] = new DiffRowView
                {
                    kind = kind,
                    left = s.left,
                    right = s.right,
                    leftText = Display(s.leftText),
                    rightText = kind == DiffRowKind.Same ? Display(s.leftText) : Display(s.rightText),
                    skipped = s.skipped,
                };
            }
            return diff;
        }

        private static DiffRowKind ParseKind(string kind)
        {
            switch (kind)
            {
                case "add": return DiffRowKind.Add;
                case "del": return DiffRowKind.Del;
                case "chg": return DiffRowKind.Chg;
                case "gap": return DiffRowKind.Gap;
                default: return DiffRowKind.Same;
            }
        }

        private static string Display(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.IndexOf('\t') < 0 ? text : text.Replace("\t", "    ");
        }
    }

    /// <summary>Requests comparisons from svn-core off the main thread.</summary>
    public static class WorkingDiffService
    {
        public const string BusySentence = "Another SVN operation is running. The comparison will show when it finishes.";

        public static Task<WorkingDiff> FetchAsync(string path)
        {
            return Task.Run(() =>
            {
                try
                {
                    var client = FileLockManager.Instance.IpcClient;
                    if (client == null || !client.IsAvailable)
                    {
                        return Failed(path, "svn-core is not available. Reinstall the FRG Version Control package.");
                    }
                    var args = "{\"path\":\"" + SvnCommandRunner.IpcEscape(path) + "\"}";
                    var result = client.InvokeRawAsync("diff.working", args, client.BuildDefaultContext()).GetAwaiter().GetResult();
                    if (result.Busy || result.StillRunning || IsBusyKind(result)) return new WorkingDiff { path = path, busy = true };
                    if (!result.Ok) return Failed(path, ErrorSentence(result, "Could not compare this file"));
                    return WorkingDiffParser.Parse(result.RawLine, path);
                }
                catch (Exception ex)
                {
                    return Failed(path, "Could not compare this file (" + ex.Message + "). Click the file again to retry.");
                }
            });
        }

        /// <summary>Writes the committed base of <paramref name="path"/> to a temp file. Returns
        /// the temp path, or null with <paramref name="error"/> text in the tuple.</summary>
        public static Task<(string tempPath, string error)> ExportBaseAsync(string path)
        {
            return Task.Run(() =>
            {
                try
                {
                    var client = FileLockManager.Instance.IpcClient;
                    if (client == null || !client.IsAvailable)
                    {
                        return ((string)null, "svn-core is not available. Reinstall the FRG Version Control package.");
                    }
                    var args = "{\"path\":\"" + SvnCommandRunner.IpcEscape(path) + "\"}";
                    var result = client.InvokeRawAsync("diff.exportBase", args, client.BuildDefaultContext()).GetAwaiter().GetResult();
                    if (result.Busy || result.StillRunning) return (null, "Another SVN operation is running. Wait for it to finish, then try again.");
                    if (!result.Ok) return (null, ErrorSentence(result, "Could not get the committed version of this file"));
                    var temp = IpcJson.ReadScalar(result.RawLine, "tempPath", inResult: true);
                    if (string.IsNullOrEmpty(temp)) return (null, "svn-core returned no file. Try again.");
                    SweepOldExports(temp);
                    return (temp, (string)null);
                }
                catch (Exception ex)
                {
                    return (null, "Could not get the committed version of this file (" + ex.Message + "). Try again.");
                }
            });
        }

        /// <summary>The core answers <c>wc_locked</c> when another svn call held the working copy
        /// past its short wait; that is a "try again shortly", not a failure.</summary>
        internal static bool IsBusyKind(IpcRawResult result)
            => result != null && !result.Ok && result.ErrorKind == IpcOpPolicy.BusyKind;

        internal static string ErrorSentence(IpcRawResult result, string description)
        {
            string action = !string.IsNullOrEmpty(result.ErrorDefaultAction)
                ? result.ErrorDefaultAction
                : "Refresh, then click the file again.";
            string kind = string.IsNullOrEmpty(result.ErrorKind) ? "" : " (" + result.ErrorKind + ")";
            return description + kind + ". " + action;
        }

        private static WorkingDiff Failed(string path, string error) => new WorkingDiff { path = path, error = error };

        private static void SweepOldExports(string freshTempPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(freshTempPath);
                if (string.IsNullOrEmpty(dir)) return;
                var cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var file in Directory.GetFiles(dir, "svn-core-base-*"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
