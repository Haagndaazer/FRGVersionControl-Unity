using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FRGVersionControl.Core.IpcClient;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FRGVersionControl.Core.Shelves
{
    /// <summary>
    /// Thin IPC shim for shelving. Every shelf operation routes through svn-core.exe via
    /// <see cref="FileLockManager"/>.Instance.IpcClient (shelf.list / shelf.create /
    /// shelf.apply / shelf.delete) — the engine plugin no longer runs svn.exe or touches
    /// the .svn shelf store directly. The shelf logic (revert-after-shelve stash semantics,
    /// SHA-256 baseline conflict detection, libsvn shelf2 byte format) lives in SvnCore and
    /// is shared byte-for-byte with the Unreal plugin.
    ///
    /// 0.2.84 — rewired off the pre-IPC ShelfService, which shelled out to svn.exe directly
    /// (engine-boundary hard-rule #1) and wrote the .svn shelf store itself (hard-rule #2).
    /// This shim makes zero Unity-API calls; the caller (FrgVersionControlWindow's
    /// post-result main-thread block) owns AssetDatabase.Refresh and cache invalidation.
    /// </summary>
    public static class ShelfService
    {
        public enum ConflictPolicy
        {
            Refuse,
            OverwriteMine,
            PromptPerFile,
        }

        /// <summary>One file inside a shelf, surfaced for the UI's per-shelf file list.
        /// Replaces the deleted ShelfStore.ShelfFileSummary; same field names the window
        /// reads (relPath / status).</summary>
        public class ShelfFileEntry
        {
            public string relPath;
            public FRGVersionControl.Core.Svn.SvnFileStatus status;
        }

        public class ShelfSummary
        {
            public string name;
            public int latestVersion;
            public string logMessage;
            public int fileCount;
            public List<ShelfFileEntry> files = new List<ShelfFileEntry>();
        }

        public class ShelfCreateResult
        {
            public bool ok;
            public string reason;
            public string shelfName;
            public int version;
            public int filesShelved;
            public List<string> autoIncludedMetaSiblings = new List<string>();
        }

        public class ShelfApplyResult
        {
            public bool ok;
            public string reason;
            public int filesApplied;
            public List<ShelfConflict> conflicts = new List<ShelfConflict>();
        }

        // -------------------- List --------------------

        public static List<ShelfSummary> ListShelves()
        {
            var summaries = new List<ShelfSummary>();
            try
            {
                var client = FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return summaries;
                var ctx = client.BuildDefaultContext();
                var r = client.InvokeRawAsync("shelf.list", "{}", ctx).GetAwaiter().GetResult();
                if (!r.Ok || string.IsNullOrEmpty(r.RawLine)) return summaries;

                var env = JsonUtility.FromJson<ListEnvelope>(r.RawLine);
                if (env?.result?.summaries == null) return summaries;
                foreach (var s in env.result.summaries)
                {
                    var sum = new ShelfSummary
                    {
                        name = s.name,
                        latestVersion = s.latestVersion,
                        logMessage = s.logMessage,
                        fileCount = s.fileCount,
                    };
                    if (s.files != null)
                    {
                        foreach (var f in s.files)
                            sum.files.Add(new ShelfFileEntry { relPath = f.path, status = MapNodeChar(f.nodeStatus) });
                    }
                    summaries.Add(sum);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FRGVersionControl.Shelves] shelf.list failed: {ex.Message}");
            }
            return summaries;
        }

        // -------------------- Create --------------------

        public static async Task<ShelfCreateResult> CreateShelfAsync(
            string name, string logMessage, string[] paths, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Fail("Shelf name cannot be empty.");
            if (paths == null || paths.Length == 0)
                return Fail("No paths supplied. Pick at least one modified file.");

            try
            {
                var client = FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable)
                    return Fail("svn-core.exe is not available. Restart the editor or reinstall the package.");
                var ctx = client.BuildDefaultContext();

                var sb = new StringBuilder();
                sb.Append("{\"name\":\"").Append(EscapeJson(name)).Append("\"");
                sb.Append(",\"message\":\"").Append(EscapeJson(logMessage ?? "")).Append("\"");
                sb.Append(",\"paths\":[");
                for (int i = 0; i < paths.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(EscapeJson(paths[i])).Append("\"");
                }
                sb.Append("]}");

                var r = await client.InvokeRawAsync("shelf.create", sb.ToString(), ctx, ct);
                if (!r.Ok) return Fail(ComposeError(r));

                var res = JsonUtility.FromJson<CreateEnvelope>(r.RawLine)?.result;
                return new ShelfCreateResult
                {
                    ok = true,
                    shelfName = name,
                    version = res?.version ?? 0,
                    filesShelved = res?.filesShelved ?? 0,
                    autoIncludedMetaSiblings = res?.autoIncluded != null
                        ? new List<string>(res.autoIncluded)
                        : new List<string>(),
                    // warning is non-null only on the revert-failed-after-save path (the core
                    // shelf is durably written; the WC just couldn't be auto-reverted). Surface
                    // it so the artist isn't told "all good" when the WC still holds the change.
                    reason = string.IsNullOrEmpty(res?.warning) ? null : res.warning,
                };
            }
            catch (OperationCanceledException)
            {
                return Fail("Shelve cancelled.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FRGVersionControl.Shelves] shelf.create threw: {ex}");
                return Fail($"Shelve failed: {ex.Message}. Open Console for full output.");
            }
        }

        // -------------------- Apply --------------------

        public static async Task<ShelfApplyResult> ApplyShelfAsync(
            string name, ConflictPolicy policy, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new ShelfApplyResult { ok = false, reason = "Shelf name cannot be empty." };

            try
            {
                var client = FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable)
                    return new ShelfApplyResult { ok = false, reason = "svn-core.exe is not available. Restart the editor or reinstall the package." };
                var ctx = client.BuildDefaultContext();

                // PromptPerFile / Refuse map to the spec "clean" policy (refuse + surface
                // conflicts, write nothing); OverwriteMine maps to "overwrite".
                string policyStr = policy == ConflictPolicy.OverwriteMine ? "overwrite" : "clean";
                var args = "{\"name\":\"" + EscapeJson(name) + "\",\"policy\":\"" + policyStr + "\"}";

                var r = await client.InvokeRawAsync("shelf.apply", args, ctx, ct);
                if (!r.Ok) return new ShelfApplyResult { ok = false, reason = ComposeError(r) };

                var res = JsonUtility.FromJson<ApplyEnvelope>(r.RawLine)?.result;
                int appliedCount = res?.appliedPaths?.Length ?? 0;
                int conflictCount = res?.conflicts?.Length ?? 0;

                // "clean" refusal: ok-with-empty-appliedPaths + populated conflicts. Map to
                // the legacy ok=false + conflicts shape so the window's prompt-then-overwrite
                // flow fires.
                if (conflictCount > 0 && appliedCount == 0)
                {
                    var list = new List<ShelfConflict>();
                    if (res.conflictDetails != null && res.conflictDetails.Length > 0)
                    {
                        foreach (var c in res.conflictDetails)
                        {
                            var sc = new ShelfConflict { relPath = c.path, detail = c.detail };
                            if (!string.IsNullOrEmpty(c.kind) && Enum.TryParse<ShelfConflict.Kind>(c.kind, out var k))
                                sc.kind = k;
                            list.Add(sc);
                        }
                    }
                    else
                    {
                        foreach (var p in res.conflicts)
                            list.Add(new ShelfConflict
                            {
                                relPath = p,
                                kind = ShelfConflict.Kind.BaselineDiverged,
                                detail = "Working copy has changed since the shelf was created.",
                            });
                    }
                    return new ShelfApplyResult
                    {
                        ok = false,
                        reason = $"{conflictCount} file(s) conflict with the current working copy.",
                        conflicts = list,
                    };
                }

                return new ShelfApplyResult { ok = true, filesApplied = appliedCount };
            }
            catch (OperationCanceledException)
            {
                return new ShelfApplyResult { ok = false, reason = "Apply cancelled." };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FRGVersionControl.Shelves] shelf.apply threw: {ex}");
                return new ShelfApplyResult { ok = false, reason = $"Apply failed: {ex.Message}. Open Console for full output." };
            }
        }

        // -------------------- Delete --------------------

        public static bool DeleteShelf(string name, out string reason)
        {
            reason = null;
            try
            {
                var client = FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable)
                {
                    reason = "svn-core.exe is not available.";
                    return false;
                }
                var ctx = client.BuildDefaultContext();
                var args = "{\"name\":\"" + EscapeJson(name) + "\"}";
                var r = client.InvokeRawAsync("shelf.delete", args, ctx).GetAwaiter().GetResult();
                if (r.Ok) return true;
                reason = ComposeError(r);
                return false;
            }
            catch (Exception ex)
            {
                reason = $"Delete failed: {ex.Message}";
                return false;
            }
        }

        // -------------------- helpers --------------------

        private static ShelfCreateResult Fail(string reason)
            => new ShelfCreateResult { ok = false, reason = reason };

        private static string ComposeError(IpcRawResult r)
        {
            var msg = r.ErrorMessage ?? r.ErrorKind ?? "operation failed";
            return string.IsNullOrEmpty(r.ErrorDefaultAction) ? msg : msg + " — " + r.ErrorDefaultAction;
        }

        private static FRGVersionControl.Core.Svn.SvnFileStatus MapNodeChar(string nodeStatus)
        {
            char c = string.IsNullOrEmpty(nodeStatus) ? 'M' : nodeStatus[0];
            switch (c)
            {
                case 'M': return FRGVersionControl.Core.Svn.SvnFileStatus.Modified;
                case 'A': return FRGVersionControl.Core.Svn.SvnFileStatus.Added;
                case 'D': return FRGVersionControl.Core.Svn.SvnFileStatus.Deleted;
                case 'R': return FRGVersionControl.Core.Svn.SvnFileStatus.Replaced;
                case 'C': return FRGVersionControl.Core.Svn.SvnFileStatus.Conflict;
                case '!': return FRGVersionControl.Core.Svn.SvnFileStatus.Missing;
                case '?': return FRGVersionControl.Core.Svn.SvnFileStatus.Untracked;
                case 'I': return FRGVersionControl.Core.Svn.SvnFileStatus.Ignored;
                // ' ' / '_' / 'X' / unknown — default to Modified (it's shelved for a reason).
                default:  return FRGVersionControl.Core.Svn.SvnFileStatus.Modified;
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // -------------------- IPC result DTOs (JsonUtility envelope shims) --------------------
        // Mirror the docs/SVN_CORE_API.md §5.7 result shapes. JsonUtility ignores the sibling
        // v/id/ok envelope fields and binds only `result`.

        [Serializable] private sealed class ListEnvelope { public ListResult result; }
        [Serializable] private sealed class ListResult { public SummaryDto[] summaries; }
        [Serializable] private sealed class SummaryDto { public string name; public int latestVersion; public string logMessage; public int fileCount; public FileDto[] files; }
        [Serializable] private sealed class FileDto { public string path; public string nodeStatus; }

        [Serializable] private sealed class CreateEnvelope { public CreateResult result; }
        [Serializable] private sealed class CreateResult { public int version; public int filesShelved; public string[] autoIncluded; public string warning; }

        [Serializable] private sealed class ApplyEnvelope { public ApplyResult result; }
        [Serializable] private sealed class ApplyResult { public string[] appliedPaths; public string[] conflicts; public ConflictDto[] conflictDetails; }
        [Serializable] private sealed class ConflictDto { public string path; public string kind; public string detail; }
    }
}
