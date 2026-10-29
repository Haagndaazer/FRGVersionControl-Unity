using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FRGVersionControl.Core.Abstractions;
using UnityEngine;

namespace FRGVersionControl.Core.IpcClient
{
    /// <summary>
    /// One-shot JSON-Lines IPC client to svn-core.exe. Each call spawns a fresh process,
    /// writes one request line, reads response frames until EOF.
    ///
    /// Persistent mode (length-prefixed JSON-Lines on a long-lived pipe) is reserved for
    /// hot-path callers like the status-refresh service; it lands once call-site rewires
    /// are merged.
    ///
    /// Phase: foundation (0.2.29). Only meta.version is wired through; subsequent versions
    /// add op-specific methods as call sites migrate off SvnCommandRunner.
    /// </summary>
    public sealed class IpcSvnClient
    {
        private readonly SvnCoreProcess m_Process;
        private readonly IFrgLogger m_Logger;
        private readonly IProjectPaths m_Paths;
        private long m_RequestCounter;

        // 0.2.68 — persistent-mode lifecycle. Single svn-core.exe process kept alive across
        // the editor session, serving all IPC requests via long-lived stdin/stdout pipes.
        // Reduces per-call cost from ~220ms (cold-start subprocess) to ~5-20ms (pipe round-
        // trip). Lazy-initialized on first call; killed on domain reload via Shutdown().
        private readonly object m_PersistentLock = new object();
        private Process m_PersistentProc;
        private StreamWriter m_PersistentWriter;
        private StreamReader m_PersistentReader;
        private Thread m_PersistentReaderThread;
        private int m_PipeGeneration;
        private readonly System.Collections.Generic.Dictionary<string, PendingRequest> m_PendingRequests =
            new System.Collections.Generic.Dictionary<string, PendingRequest>();
        private bool m_PersistentDead;

        private static int s_ExclusiveInFlight;
        private static string s_ExclusiveOpName;

        /// <summary>True while a long-exclusive op (commit/update/checkout/cleanup/vacuum/shelve)
        /// is in flight on any path. Other requests are answered busy without touching the pipe
        /// (SVN_CORE_API.md §2.4); the window uses it to refuse conflicting actions.</summary>
        public static bool IsExclusiveOpInFlight => Volatile.Read(ref s_ExclusiveInFlight) > 0;

        public static string ExclusiveOpName => s_ExclusiveOpName;

        private static readonly object s_ExclusiveLock = new object();

        private static void BeginExclusive(string op)
        {
            lock (s_ExclusiveLock)
            {
                s_ExclusiveOpName = op;
                Interlocked.Increment(ref s_ExclusiveInFlight);
            }
        }

        private static void EndExclusive()
        {
            lock (s_ExclusiveLock)
            {
                if (Interlocked.Decrement(ref s_ExclusiveInFlight) <= 0)
                {
                    Volatile.Write(ref s_ExclusiveInFlight, 0);
                    s_ExclusiveOpName = null;
                }
            }
        }

        /// <summary>Drops a pending entry's exclusive hold exactly once, for every path that
        /// removes entries from the map (terminal frame, kill, pipe death, shutdown).</summary>
        private static void ReleaseExclusiveFor(PendingRequest p)
        {
            if (p.Class == IpcOpClass.LongExclusive) EndExclusive();
        }

        private sealed class PendingRequest
        {
            public string Op;
            public IpcOpClass Class;
            public int Generation;
            public TaskCompletionSource<IpcRawResult> Tcs;
            public IProgress<string> Progress;
            public DateTime SentUtc;
            public DateTime? AbandonedUtc;
        }

        private struct PersistentSend
        {
            public PersistentOutcome Outcome;
            public TimeoutAction Action;
            public string Line;
            public int WaitedMs;
        }
        private System.Diagnostics.Stopwatch m_RequestStopwatch = new System.Diagnostics.Stopwatch();

        public IpcSvnClient(SvnCoreProcess process, IFrgLogger logger, IProjectPaths paths)
        {
            m_Process = process;
            m_Logger = logger;
            m_Paths = paths;
            // Tear down the persistent process on assembly reload — Unity recreates the
            // singleton on the next access, which lazy-spawns a fresh svn-core.exe.
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
        }

        /// <summary>Kills the persistent svn-core.exe (if any) and fails all in-flight
        /// requests with a protocol-kind error so awaiters don't hang.</summary>
        public void Shutdown()
        {
            lock (m_PersistentLock)
            {
                m_PersistentDead = true;
                bool mutatingPending = false;
                foreach (var kv in m_PendingRequests)
                {
                    if (kv.Value.Class != IpcOpClass.ReadOnly) mutatingPending = true;
                    try
                    {
                        kv.Value.Tcs.TrySetResult(new IpcRawResult
                        {
                            Op = kv.Value.Op,
                            Ok = false,
                            ErrorKind = "protocol",
                            ErrorMessage = "IPC pipe torn down (assembly reload)",
                            ErrorDefaultAction = IpcOpPolicy.PipeLostAction,
                        });
                    }
                    catch { }
                }
                foreach (var kv in m_PendingRequests) ReleaseExclusiveFor(kv.Value);
                m_PendingRequests.Clear();

                try { m_PersistentWriter?.Close(); } catch { }
                try { m_PersistentReader?.Close(); } catch { }
                if (mutatingPending)
                {
                    m_Logger?.Info("persistent IPC: mutating request(s) still in flight at shutdown — stdin closed, svn-core drains them (not killed)");
                }
                else
                {
                    try
                    {
                        if (m_PersistentProc != null && !m_PersistentProc.HasExited)
                        {
                            if (!m_PersistentProc.WaitForExit(500))
                            {
                                try { m_PersistentProc.Kill(); } catch { }
                            }
                        }
                    }
                    catch { }
                }
                m_PersistentProc = null;
                m_PersistentWriter = null;
                m_PersistentReader = null;
                m_PersistentReaderThread = null;
            }
        }

        public bool IsAvailable => m_Process != null && m_Process.IsAvailable;

        /// <summary>Per-op request counts, test diagnostics only (batch tests assert one
        /// request per verb).</summary>
        internal static readonly ConcurrentDictionary<string, int> RequestCounts = new ConcurrentDictionary<string, int>();

        internal static int RequestCount(string op) => RequestCounts.TryGetValue(op, out var n) ? n : 0;

        /// <summary>Set FRG_IPC_TRACE=&lt;op&gt; in the editor's environment to log a stack trace per
        /// request of that op (diagnosing refresh storms).</summary>
        private static readonly string s_TraceOp = Environment.GetEnvironmentVariable("FRG_IPC_TRACE");

        public string ResolveBinaryPath() => m_Process != null ? m_Process.ResolveBinaryPath() : null;

        public IpcRequestContext BuildDefaultContext(string username = null)
        {
            return new IpcRequestContext
            {
                wc = m_Paths.WorkingCopyRoot,
                configDir = m_Paths.ToolingConfigDir,
                username = username,
                certTrust = new[] { "unknown-ca", "cn-mismatch" },
                svnBinaryHint = null,
            };
        }

        /// <summary>
        /// Generic one-shot invocation. Builds the envelope, sends the request, returns the
        /// raw terminal JSON line. Useful for the smoke test and any caller that wants to
        /// parse the result shape itself. Throws on infrastructure failures only; protocol
        /// failures (ok=false) come back as the JSON line with the error field populated.
        /// </summary>
        public async Task<IpcRawResult> InvokeRawAsync(
            string op,
            string argsJson,
            IpcRequestContext ctx,
            CancellationToken ct = default,
            IProgress<string> progress = null,
            int? waitBudgetMs = null)
        {
            if (!IsAvailable)
            {
                m_Process?.LogMissingBinary();
                throw new InvalidOperationException("svn-core.exe is not available");
            }
            if (string.IsNullOrEmpty(argsJson)) argsJson = "{}";
            if (ctx == null) ctx = BuildDefaultContext();

            var id = "u" + Interlocked.Increment(ref m_RequestCounter).ToString();
            RequestCounts.AddOrUpdate(op, 1, (_, c) => c + 1);
            if (s_TraceOp != null && s_TraceOp == op) m_Logger?.Info("IPC trace " + op + " " + Environment.StackTrace);
            var ctxJson = JsonUtility.ToJson(ctx);
            // Hand-build the envelope so we can splat args as a pre-serialized JSON fragment.
            var requestJson = "{\"v\":1,\"id\":\"" + id + "\",\"op\":\"" + op + "\",\"ctx\":" +
                              ctxJson + ",\"args\":" + argsJson + "}";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cls = IpcOpPolicy.Classify(op);
            var plan = IpcOpPolicy.Plan(cls, IsExclusiveOpInFlight);
            if (plan.SkipAsBusy) return BusyResult(op, sw.ElapsedMilliseconds);

            var sent = await SendPersistentAsync(id, op, cls, requestJson, plan.TimeoutMs, waitBudgetMs, progress, ct).ConfigureAwait(false);
            string line = null;
            switch (sent.Outcome)
            {
                case PersistentOutcome.Completed:
                    line = sent.Line;
                    break;
                case PersistentOutcome.NotDelivered:
                    line = await RunOneShotTrackedAsync(op, cls, requestJson, progress, ct).ConfigureAwait(false);
                    break;
                case PersistentOutcome.PipeLost:
                    if (!IpcOpPolicy.MayOneShot(PersistentOutcome.PipeLost, cls)) return PipeLostResult(op, sw.ElapsedMilliseconds);
                    line = await RunOneShotTrackedAsync(op, cls, requestJson, progress, ct).ConfigureAwait(false);
                    break;
                case PersistentOutcome.TimedOut:
                    switch (sent.Action)
                    {
                        case TimeoutAction.KillPipeAndOneShot:
                            line = await RunOneShotTrackedAsync(op, cls, requestJson, progress, ct).ConfigureAwait(false);
                            break;
                        case TimeoutAction.AbandonAsBusy:
                            return BusyResult(op, sw.ElapsedMilliseconds);
                        default:
                            return StillRunningResult(op, sent.WaitedMs, sw.ElapsedMilliseconds);
                    }
                    break;
            }
            sw.Stop();
            if (string.IsNullOrEmpty(line))
            {
                return new IpcRawResult
                {
                    Op = op,
                    Ok = false,
                    ErrorKind = "protocol",
                    ErrorMessage = "svn-core.exe exited without a terminal response frame",
                    ErrorDefaultAction = IpcOpPolicy.PipeLostAction,
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
            }
            return ParseRaw(op, line, sw.ElapsedMilliseconds);
        }

        private static IpcRawResult BusyResult(string op, long elapsedMs)
        {
            var exclusive = s_ExclusiveOpName;
            return new IpcRawResult
            {
                Op = op,
                Ok = false,
                Busy = true,
                ErrorKind = IpcOpPolicy.BusyKind,
                ErrorMessage = exclusive != null ? IpcOpPolicy.BusyMessage(op, exclusive) : IpcOpPolicy.BusyMessageGeneric(op),
                ErrorDefaultAction = exclusive != null ? IpcOpPolicy.BusyAction(exclusive) : IpcOpPolicy.BusyActionGeneric,
                ElapsedMs = elapsedMs,
            };
        }

        private static IpcRawResult StillRunningResult(string op, int waitedMs, long elapsedMs) => new IpcRawResult
        {
            Op = op,
            Ok = false,
            StillRunning = true,
            ErrorKind = IpcOpPolicy.StillRunningKind,
            ErrorMessage = IpcOpPolicy.StillRunningMessage(op, waitedMs),
            ErrorDefaultAction = IpcOpPolicy.StillRunningAction,
            ElapsedMs = elapsedMs,
        };

        private static IpcRawResult PipeLostResult(string op, long elapsedMs) => new IpcRawResult
        {
            Op = op,
            Ok = false,
            ErrorKind = "protocol",
            ErrorMessage = IpcOpPolicy.PipeLostMessage(op),
            ErrorDefaultAction = IpcOpPolicy.PipeLostAction,
            ElapsedMs = elapsedMs,
        };

        private async Task<string> RunOneShotTrackedAsync(string op, IpcOpClass cls, string requestJson, IProgress<string> progress, CancellationToken ct)
        {
            if (cls == IpcOpClass.LongExclusive) BeginExclusive(op);
            try
            {
                var wireCt = cls == IpcOpClass.ReadOnly ? ct : CancellationToken.None;
                return await SendOneShotAsync(requestJson, wireCt, progress).ConfigureAwait(false);
            }
            finally
            {
                if (cls == IpcOpClass.LongExclusive) EndExclusive();
            }
        }

        /// <summary>mutate.commit with streamed progress lines. The caller's cancellation never
        /// reaches the wire: a commit is only ever abandoned by the core's own rules
        /// (SVN_CORE_API.md §4), never by a window action.</summary>
        public Task<IpcRawResult> CommitAsync(string[] paths, string message, IProgress<string> progress)
        {
            var args = new System.Text.StringBuilder("{\"paths\":[");
            for (int i = 0; i < paths.Length; i++)
            {
                if (i > 0) args.Append(",");
                args.Append("\"").Append(EscapeJsonString(paths[i])).Append("\"");
            }
            args.Append("],\"message\":\"").Append(EscapeJsonString(message ?? "")).Append("\"}");
            return InvokeRawAsync("mutate.commit", args.ToString(), BuildDefaultContext(), CancellationToken.None, progress);
        }

        private static IpcRawResult ParseRaw(string op, string line, long elapsedMs)
        {
            // Cheap structural parse — we only need ok, error.kind, error.message.
            // Full result text is preserved verbatim for the caller to display.
            var result = new IpcRawResult { Op = op, RawLine = line, ElapsedMs = elapsedMs };
            int okIdx = line.IndexOf("\"ok\":", StringComparison.Ordinal);
            if (okIdx >= 0)
            {
                // ok value follows ":" — either true, false, or whitespace+value
                var slice = line.Substring(okIdx + 5).TrimStart();
                result.Ok = slice.StartsWith("true", StringComparison.Ordinal);
            }
            if (!result.Ok)
            {
                // Pull out kind / message via JsonUtility on a minimal wrapper.
                try
                {
                    var minimal = JsonUtility.FromJson<IpcRawErrorWrapper>(line);
                    if (minimal != null && minimal.error != null)
                    {
                        result.ErrorKind = minimal.error.kind;
                        result.ErrorMessage = minimal.error.message;
                        result.ErrorDefaultAction = minimal.error.defaultAction;
                    }
                }
                catch { }
            }
            return result;
        }

        [System.Serializable]
        private sealed class IpcRawErrorWrapper
        {
            public IpcResponseError error;
        }

        /// <summary>
        /// Try-IPC wrapper for lock.acquire. Returns (success, errorMessage) tuple matching
        /// the legacy SvnCommandRunner.Lock shape. Returns null if IPC is unavailable so
        /// callers fall back to legacy.
        /// </summary>
        public async Task<(bool success, string error)?> TryLockAsync(string[] paths, string message, bool steal, CancellationToken ct = default)
        {
            if (!IsAvailable) return null;
            try
            {
                var ctx = BuildDefaultContext();
                var argsObj = new System.Text.StringBuilder("{\"paths\":[");
                for (int i = 0; i < paths.Length; i++)
                {
                    if (i > 0) argsObj.Append(",");
                    argsObj.Append("\"").Append(EscapeJsonString(paths[i])).Append("\"");
                }
                argsObj.Append("]");
                if (!string.IsNullOrEmpty(message)) argsObj.Append(",\"comment\":\"").Append(EscapeJsonString(message)).Append("\"");
                if (steal) argsObj.Append(",\"steal\":true");
                argsObj.Append("}");
                var result = await InvokeRawAsync("lock.acquire", argsObj.ToString(), ctx, ct).ConfigureAwait(false);
                if (result.Ok) return (true, null);
                var err = !string.IsNullOrEmpty(result.ErrorDefaultAction)
                    ? $"{result.ErrorMessage} — {result.ErrorDefaultAction}"
                    : result.ErrorMessage ?? result.ErrorKind ?? "lock failed";
                return (false, err);
            }
            catch { return null; }
        }

        /// <summary>Try-IPC wrapper for lock.release. Same contract as TryLockAsync.</summary>
        public async Task<(bool success, string error)?> TryUnlockAsync(string[] paths, bool force, CancellationToken ct = default)
        {
            if (!IsAvailable) return null;
            try
            {
                var ctx = BuildDefaultContext();
                var argsObj = new System.Text.StringBuilder("{\"paths\":[");
                for (int i = 0; i < paths.Length; i++)
                {
                    if (i > 0) argsObj.Append(",");
                    argsObj.Append("\"").Append(EscapeJsonString(paths[i])).Append("\"");
                }
                argsObj.Append("]");
                if (force) argsObj.Append(",\"force\":true");
                argsObj.Append("}");
                var result = await InvokeRawAsync("lock.release", argsObj.ToString(), ctx, ct).ConfigureAwait(false);
                if (result.Ok) return (true, null);
                var err = !string.IsNullOrEmpty(result.ErrorDefaultAction)
                    ? $"{result.ErrorMessage} — {result.ErrorDefaultAction}"
                    : result.ErrorMessage ?? result.ErrorKind ?? "unlock failed";
                return (false, err);
            }
            catch { return null; }
        }

        /// <summary>Generic try-IPC for any op with a {paths[],...} args. Returns (success,error) or null on infra failure.</summary>
        public async Task<(bool success, string error)?> TryInvokeWithPathsAsync(string op, string[] paths, string extraArgsJson = null, CancellationToken ct = default, int? waitBudgetMs = null)
        {
            if (!IsAvailable) return null;
            try
            {
                var ctx = BuildDefaultContext();
                var argsObj = new System.Text.StringBuilder("{\"paths\":[");
                for (int i = 0; i < paths.Length; i++)
                {
                    if (i > 0) argsObj.Append(",");
                    argsObj.Append("\"").Append(EscapeJsonString(paths[i])).Append("\"");
                }
                argsObj.Append("]");
                if (!string.IsNullOrEmpty(extraArgsJson)) argsObj.Append(",").Append(extraArgsJson);
                argsObj.Append("}");
                var result = await InvokeRawAsync(op, argsObj.ToString(), ctx, ct, null, waitBudgetMs).ConfigureAwait(false);
                if (result.Ok) return (true, null);
                var err = !string.IsNullOrEmpty(result.ErrorDefaultAction)
                    ? $"{result.ErrorMessage} — {result.ErrorDefaultAction}"
                    : result.ErrorMessage ?? result.ErrorKind ?? op + " failed";
                return (false, err);
            }
            catch { return null; }
        }

        private static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
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

        /// <summary>
        /// Returns the current upstream lock map for the working copy via
        /// `status.allEntries` with includeUpstream:true. Each entry's lock state is
        /// surfaced into the legacy SvnLockInfo shape so this can drop into existing call
        /// sites (FileLockManager.RefreshLockCache).
        ///
        /// Returns null on IPC failure — callers should fall back to the legacy CLI.
        /// </summary>
        public async Task<System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnLockInfo>>
            GetLockMapAsync(CancellationToken ct = default)
        {
            var r = await TryGetLockMapAsync(ct).ConfigureAwait(false);
            return r.Ok ? r.Value : null;
        }

        /// <summary>Lock map with the failure distinguished: Busy (a long-exclusive op is in
        /// flight — keep the prior cache, do not fall back) vs. a real error.</summary>
        public async Task<IpcMapResult<System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnLockInfo>>>
            TryGetLockMapAsync(CancellationToken ct = default)
        {
            var r = new IpcMapResult<System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnLockInfo>>();
            if (!IsAvailable) { r.Error = "svn-core.exe is not available"; return r; }
            try
            {
                var ctx = BuildDefaultContext();
                var args = "{\"depth\":\"infinity\",\"includeUpstream\":true}";
                var result = await InvokeRawAsync("status.allEntries", args, ctx, ct).ConfigureAwait(false);
                return r.From(result, ParseLockMap);
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                return r;
            }
        }

        /// <summary>
        /// Returns the full WC status map via `status.allEntries` (no `-u`, so cheap).
        /// Maps every entry into the legacy SvnStatusEntry shape so this can drop into
        /// SvnStatusCache.RefreshAsync. Returns null on IPC failure; callers fall back.
        /// </summary>
        public async Task<System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnStatusEntry>>
            GetAllStatusEntriesAsync(CancellationToken ct = default)
        {
            var r = await TryGetAllStatusEntriesAsync(ct).ConfigureAwait(false);
            return r.Ok ? r.Value : null;
        }

        /// <summary>One <c>status.query</c> for many paths (the batch replacement for per-path
        /// HasUncommittedChanges loops). Keys are normalised to forward slashes.</summary>
        public async Task<IpcMapResult<System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnStatusEntry>>>
            TryStatusQueryAsync(string[] paths, CancellationToken ct = default)
        {
            var r = new IpcMapResult<System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnStatusEntry>>();
            if (!IsAvailable) { r.Error = "svn-core.exe is not available"; return r; }
            try
            {
                var args = new System.Text.StringBuilder("{\"paths\":[");
                for (int i = 0; i < paths.Length; i++)
                {
                    if (i > 0) args.Append(",");
                    args.Append("\"").Append(EscapeJsonString(paths[i])).Append("\"");
                }
                args.Append("]}");
                var result = await InvokeRawAsync("status.query", args.ToString(), BuildDefaultContext(), ct).ConfigureAwait(false);
                return r.From(result, raw =>
                {
                    var map = ParseStatusMap(raw);
                    var normalized = new System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnStatusEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in map) normalized[(kv.Key ?? "").Replace('\\', '/')] = kv.Value;
                    return normalized;
                });
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                return r;
            }
        }

        public async Task<IpcMapResult<System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnStatusEntry>>>
            TryGetAllStatusEntriesAsync(CancellationToken ct = default)
        {
            var r = new IpcMapResult<System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnStatusEntry>>();
            if (!IsAvailable) { r.Error = "svn-core.exe is not available"; return r; }
            try
            {
                var ctx = BuildDefaultContext();
                var result = await InvokeRawAsync("status.allEntries", "{\"depth\":\"infinity\"}", ctx, ct).ConfigureAwait(false);
                return r.From(result, ParseStatusMap);
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                return r;
            }
        }

        private static System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnStatusEntry>
            ParseStatusMap(string rawJson)
        {
            var dict = new System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnStatusEntry>(
                System.StringComparer.Ordinal);
            int entriesIdx = rawJson.IndexOf("\"entries\":[", System.StringComparison.Ordinal);
            if (entriesIdx < 0) return dict;
            int pos = entriesIdx + "\"entries\":[".Length;
            while (pos < rawJson.Length)
            {
                int openBrace = rawJson.IndexOf('{', pos);
                if (openBrace < 0) break;
                int closeBrace = rawJson.IndexOf('}', openBrace);
                if (closeBrace < 0) break;
                var entryJson = rawJson.Substring(openBrace, closeBrace - openBrace + 1);
                pos = closeBrace + 1;

                var info = JsonUtility.FromJson<StatusEntryShim>(entryJson);
                if (info == null || string.IsNullOrEmpty(info.path)) continue;

                dict[info.path] = new FRGVersionControl.Core.Svn.SvnStatusEntry
                {
                    path = info.path,
                    status = MapWcStatus(info.wcStatus, info.propStatus),
                    lockState = MapLockState(info.lockState),
                    lockOwner = info.lockOwner ?? "",
                    changelist = info.changelist,
                    revision = 0, // not surfaced by status.allEntries — callers using this for display only
                    outOfDate = false, // requires `-u` and parsing repos-status, defer until needed
                };
                if (pos < rawJson.Length && rawJson[pos] == ']') break;
            }
            return dict;
        }

        private static FRGVersionControl.Core.Svn.SvnFileStatus MapWcStatus(string wc, string prop)
        {
            // svn status --xml wc-status item values mapped to SvnFileStatus enum.
            switch (wc)
            {
                case "modified": return FRGVersionControl.Core.Svn.SvnFileStatus.Modified;
                case "added": return FRGVersionControl.Core.Svn.SvnFileStatus.Added;
                case "deleted": return FRGVersionControl.Core.Svn.SvnFileStatus.Deleted;
                case "replaced": return FRGVersionControl.Core.Svn.SvnFileStatus.Replaced;
                case "conflicted": return FRGVersionControl.Core.Svn.SvnFileStatus.Conflict;
                case "missing": return FRGVersionControl.Core.Svn.SvnFileStatus.Missing;
                case "unversioned": return FRGVersionControl.Core.Svn.SvnFileStatus.Untracked;
                case "ignored": return FRGVersionControl.Core.Svn.SvnFileStatus.Ignored;
                case "normal":
                    // wc-status normal but prop-status modified → Modified (prop-only change)
                    return prop == "modified" || prop == "conflicted"
                        ? FRGVersionControl.Core.Svn.SvnFileStatus.Modified
                        : FRGVersionControl.Core.Svn.SvnFileStatus.None;
                default: return FRGVersionControl.Core.Svn.SvnFileStatus.None;
            }
        }

        private static FRGVersionControl.Core.Svn.LockState MapLockState(string s) => s switch
        {
            "K" => FRGVersionControl.Core.Svn.LockState.K,
            "O" => FRGVersionControl.Core.Svn.LockState.O,
            "B" => FRGVersionControl.Core.Svn.LockState.B,
            "T" => FRGVersionControl.Core.Svn.LockState.T,
            _ => FRGVersionControl.Core.Svn.LockState.None,
        };

        private static System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnLockInfo>
            ParseLockMap(string rawJson)
        {
            // Hand-roll the parse — JsonUtility doesn't handle the nested entries array
            // shape, and we only need three fields per entry (path, lockState, lockOwner).
            var dict = new System.Collections.Generic.Dictionary<string, FRGVersionControl.Core.Svn.SvnLockInfo>(
                System.StringComparer.Ordinal);
            int entriesIdx = rawJson.IndexOf("\"entries\":[", System.StringComparison.Ordinal);
            if (entriesIdx < 0) return dict;
            int pos = entriesIdx + "\"entries\":[".Length;
            while (pos < rawJson.Length)
            {
                int openBrace = rawJson.IndexOf('{', pos);
                if (openBrace < 0) break;
                int closeBrace = rawJson.IndexOf('}', openBrace);
                if (closeBrace < 0) break;
                var entryJson = rawJson.Substring(openBrace, closeBrace - openBrace + 1);
                pos = closeBrace + 1;

                var info = JsonUtility.FromJson<StatusEntryShim>(entryJson);
                if (info == null || string.IsNullOrEmpty(info.path)) continue;
                var state = info.lockState switch
                {
                    "K" => FRGVersionControl.Core.Svn.LockState.K,
                    "O" => FRGVersionControl.Core.Svn.LockState.O,
                    "B" => FRGVersionControl.Core.Svn.LockState.B,
                    "T" => FRGVersionControl.Core.Svn.LockState.T,
                    _ => FRGVersionControl.Core.Svn.LockState.None,
                };
                if (state == FRGVersionControl.Core.Svn.LockState.None) continue;
                dict[info.path] = new FRGVersionControl.Core.Svn.SvnLockInfo
                {
                    path = info.path,
                    state = state,
                    lockOwner = info.lockOwner ?? "",
                    lockMessage = "",
                    lockCreated = null,
                };
                if (rawJson[pos] == ']') break;
            }
            return dict;
        }

        [System.Serializable]
        private sealed class StatusEntryShim
        {
            public string path;
            public string wcStatus;
            public string propStatus;
            public string changelist;
            public string lockState;
            public string lockOwner;
            public long lockedRev;
        }

        /// <summary>
        /// Probe the core's `meta.version` op. Returns the result on success or throws
        /// <see cref="IpcOperationException"/> with the wrapped error on failure.
        /// </summary>
        public async Task<MetaVersionResult> MetaVersionAsync(CancellationToken ct = default)
        {
            if (!IsAvailable)
            {
                m_Process?.LogMissingBinary();
                throw new InvalidOperationException("svn-core.exe is not available");
            }

            var id = "u" + Interlocked.Increment(ref m_RequestCounter).ToString();
            var req = new IpcRequestNoArgs
            {
                v = 1,
                id = id,
                op = "meta.version",
                ctx = BuildDefaultContext(),
                args = new IpcEmptyArgs(),
            };
            var requestJson = JsonUtility.ToJson(req);

            var line = await SendOneShotAsync(requestJson, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line))
            {
                throw new InvalidOperationException("svn-core.exe exited without a terminal response frame");
            }
            var resp = JsonUtility.FromJson<IpcResponseWithMetaVersion>(line);
            if (resp == null)
            {
                throw new InvalidOperationException("svn-core.exe response was not parseable");
            }
            if (!resp.ok)
            {
                throw new IpcOperationException(resp.error);
            }
            return resp.result;
        }

        /// <summary>
        /// Send a request through the persistent svn-core.exe pipe and wait per the request
        /// policy (SVN_CORE_API.md §2.4). NotDelivered means the core never saw the request (the
        /// caller may one-shot it); TimedOut carries the action the policy chose; PipeLost means
        /// the core died with the request outstanding.
        /// </summary>
        private async Task<PersistentSend> SendPersistentAsync(
            string id, string op, IpcOpClass cls, string requestJson, int? timeoutMs, int? waitBudgetMs,
            IProgress<string> progress, CancellationToken ct)
        {
            var pending = new PendingRequest
            {
                Op = op,
                Class = cls,
                Tcs = new TaskCompletionSource<IpcRawResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                Progress = progress,
                SentUtc = DateTime.UtcNow,
            };
            lock (m_PersistentLock)
            {
                if (!EnsurePersistentStartedLocked()) return new PersistentSend { Outcome = PersistentOutcome.NotDelivered };
                pending.Generation = m_PipeGeneration;
                m_PendingRequests[id] = pending;
                try
                {
                    m_PersistentWriter.WriteLine(requestJson);
                    m_PersistentWriter.Flush();
                }
                catch (Exception ex)
                {
                    m_PendingRequests.Remove(id);
                    bool dead = m_PersistentProc == null || m_PersistentProc.HasExited;
                    m_Logger?.Warn($"persistent write failed: {ex.Message}; {(dead ? "pipe is dead, tearing down" : "request not delivered")}, falling back to one-shot for this request");
                    if (dead) KillPersistentLocked();
                    return new PersistentSend { Outcome = PersistentOutcome.NotDelivered };
                }
                if (cls == IpcOpClass.LongExclusive) BeginExclusive(op);
            }

            bool budgetBound = waitBudgetMs.HasValue && (!timeoutMs.HasValue || waitBudgetMs.Value < timeoutMs.Value);
            int? effectiveMs = budgetBound ? waitBudgetMs : timeoutMs;

            using (var timer = new CancellationTokenSource())
            using (ct.Register(() =>
            {
                lock (m_PersistentLock)
                {
                    if (m_PendingRequests.TryGetValue(id, out var p)) p.AbandonedUtc = DateTime.UtcNow;
                }
                pending.Tcs.TrySetCanceled();
            }))
            {
                if (effectiveMs.HasValue)
                {
                    var winner = await Task.WhenAny(pending.Tcs.Task, Task.Delay(effectiveMs.Value, timer.Token)).ConfigureAwait(false);
                    if (winner != pending.Tcs.Task)
                    {
                        return OnPersistentTimeout(id, op, cls, pending, effectiveMs.Value, budgetBound);
                    }
                    timer.Cancel();
                }

                IpcRawResult result;
                try
                {
                    result = await pending.Tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    return new PersistentSend { Outcome = PersistentOutcome.PipeLost };
                }
                if (result == null || string.IsNullOrEmpty(result.RawLine))
                {
                    return new PersistentSend { Outcome = PersistentOutcome.PipeLost };
                }
                return new PersistentSend { Outcome = PersistentOutcome.Completed, Line = result.RawLine };
            }
        }

        private PersistentSend OnPersistentTimeout(string id, string op, IpcOpClass cls, PendingRequest pending, int waitedMs, bool budgetBound)
        {
            TimeoutAction action;
            lock (m_PersistentLock)
            {
                if (budgetBound)
                {
                    action = TimeoutAction.AbandonKeepPending;
                }
                else
                {
                    int otherNonRead = 0;
                    var staleBefore = DateTime.UtcNow - IpcOpPolicy.AbandonedNoLongerBlocksKillAfter;
                    foreach (var kv in m_PendingRequests)
                    {
                        if (kv.Key == id || kv.Value.Class == IpcOpClass.ReadOnly) continue;
                        if (kv.Value.Class == IpcOpClass.Mutating && kv.Value.AbandonedUtc.HasValue && kv.Value.AbandonedUtc.Value < staleBefore) continue;
                        otherNonRead++;
                    }
                    action = IpcOpPolicy.OnTimeout(cls, IsExclusiveOpInFlight, otherNonRead);
                }

                switch (action)
                {
                    case TimeoutAction.KillPipeAndOneShot:
                        m_PendingRequests.Remove(id);
                        KillPersistentLocked();
                        break;
                    case TimeoutAction.AbandonAsBusy:
                        m_PendingRequests.Remove(id);
                        break;
                    default:
                        pending.AbandonedUtc = DateTime.UtcNow;
                        break;
                }
            }
            m_Logger?.Warn($"persistent IPC: {op} (id={id}) not answered within {waitedMs} ms → {action}");
            return new PersistentSend { Outcome = PersistentOutcome.TimedOut, Action = action, WaitedMs = waitedMs };
        }

        /// <summary>Spawns svn-core.exe --persistent if not already running. Caller must
        /// hold m_PersistentLock. Returns true if the pipe is ready to write.</summary>
        private bool EnsurePersistentStartedLocked()
        {
            if (m_PersistentDead) return false;
            if (m_PersistentProc != null && !m_PersistentProc.HasExited) return true;

            try
            {
                var psi = m_Process.BuildStartInfo(persistent: true);
                m_PersistentProc = Process.Start(psi);
                if (m_PersistentProc == null) return false;
                m_PersistentWriter = m_PersistentProc.StandardInput;
                m_PersistentReader = m_PersistentProc.StandardOutput;
                m_PipeGeneration++;
                var session = new ReaderSession { Reader = m_PersistentReader, Generation = m_PipeGeneration };

                // Single reader thread drains stdout and dispatches responses by id.
                m_PersistentReaderThread = new Thread(() => PersistentReaderLoop(session))
                {
                    IsBackground = true,
                    Name = "svn-core-ipc-reader",
                };
                m_PersistentReaderThread.Start();
                m_Logger?.Info("persistent IPC pipe started — subsequent calls use the long-lived process");
                return true;
            }
            catch (Exception ex)
            {
                m_Logger?.Warn($"failed to start persistent svn-core.exe: {ex.Message}; falling back to one-shot mode");
                m_PersistentProc = null;
                m_PersistentWriter = null;
                m_PersistentReader = null;
                return false;
            }
        }

        private void KillPersistentLocked()
        {
            foreach (var kv in m_PendingRequests)
            {
                try { kv.Value.Tcs.TrySetCanceled(); } catch { }
                ReleaseExclusiveFor(kv.Value);
            }
            m_PendingRequests.Clear();
            m_PipeGeneration++;
            try { m_PersistentWriter?.Close(); } catch { }
            try { m_PersistentReader?.Close(); } catch { }
            try
            {
                if (m_PersistentProc != null && !m_PersistentProc.HasExited)
                    m_PersistentProc.Kill();
            }
            catch { }
            m_PersistentProc = null;
            m_PersistentWriter = null;
            m_PersistentReader = null;
            m_PersistentReaderThread = null;
        }

        private sealed class ReaderSession
        {
            public StreamReader Reader;
            public int Generation;
        }

        /// <summary>Reads one pipe generation only: a thread left over from a killed pipe never
        /// touches the replacement core's reader, pending map or exclusive hold.</summary>
        private void PersistentReaderLoop(ReaderSession session)
        {
            try
            {
                while (true)
                {
                    var line = session.Reader.ReadLine();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var id = ExtractId(line);
                    if (id == null) continue;
                    bool terminal = line.IndexOf("\"ok\"", StringComparison.Ordinal) >= 0;

                    if (!terminal)
                    {
                        IProgress<string> progress = null;
                        lock (m_PersistentLock)
                        {
                            if (m_PendingRequests.TryGetValue(id, out var p) && p.Generation == session.Generation) progress = p.Progress;
                        }
                        if (progress != null) ReportProgressFrame(progress, line);
                        continue;
                    }

                    PendingRequest pending = null;
                    lock (m_PersistentLock)
                    {
                        if (m_PendingRequests.TryGetValue(id, out pending) && pending.Generation == session.Generation)
                        {
                            m_PendingRequests.Remove(id);
                            ReleaseExclusiveFor(pending);
                        }
                        else
                        {
                            pending = null;
                        }
                    }
                    if (pending != null)
                    {
                        pending.Tcs.TrySetResult(ParseRaw(pending.Op, line, 0));
                    }
                }
            }
            catch (Exception ex)
            {
                m_Logger?.Warn($"persistent reader loop exited: {ex.Message}");
            }
            finally
            {
                lock (m_PersistentLock)
                {
                    var mine = new System.Collections.Generic.List<string>();
                    foreach (var kv in m_PendingRequests)
                    {
                        if (kv.Value.Generation == session.Generation) mine.Add(kv.Key);
                    }
                    foreach (var id in mine)
                    {
                        var p = m_PendingRequests[id];
                        m_PendingRequests.Remove(id);
                        try { p.Tcs.TrySetCanceled(); } catch { }
                        ReleaseExclusiveFor(p);
                    }
                    if (ReferenceEquals(m_PersistentReader, session.Reader))
                    {
                        m_PersistentProc = null;
                        m_PersistentWriter = null;
                        m_PersistentReader = null;
                    }
                }
            }
        }

        private static string ExtractId(string line)
        {
            var idIdx = line.IndexOf("\"id\":\"", StringComparison.Ordinal);
            if (idIdx < 0) return null;
            var idStart = idIdx + "\"id\":\"".Length;
            var idEnd = line.IndexOf('"', idStart);
            if (idEnd <= idStart) return null;
            return line.Substring(idStart, idEnd - idStart);
        }

        private static void ReportProgressFrame(IProgress<string> progress, string line)
        {
            if (line.IndexOf("\"progress\"", StringComparison.Ordinal) < 0) return;
            try
            {
                var frame = JsonUtility.FromJson<IpcProgressFrame>(line);
                var msg = frame?.progress?.msg;
                if (!string.IsNullOrEmpty(msg)) progress.Report(msg);
            }
            catch { }
        }

        [System.Serializable]
        private sealed class IpcProgressFrame
        {
            public string id;
            public IpcProgressBody progress;
        }

        [System.Serializable]
        private sealed class IpcProgressBody
        {
            public string phase;
            public string msg;
        }

        private async Task<string> SendOneShotAsync(string requestJson, CancellationToken ct, IProgress<string> progress = null)
        {
            var psi = m_Process.BuildStartInfo(persistent: false);
            using (var proc = Process.Start(psi))
            {
                if (proc == null) throw new InvalidOperationException("Failed to start svn-core.exe");
                using (var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch { } }))
                {
                    await proc.StandardInput.WriteLineAsync(requestJson).ConfigureAwait(false);
                    await proc.StandardInput.FlushAsync().ConfigureAwait(false);
                    proc.StandardInput.Close();

                    string terminalLine = null;
                    string line;
                    while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        // We treat any line containing `"ok":` as terminal. Progress frames
                        // contain `"progress"` instead; for this minimal client they're skipped.
                        if (line.IndexOf("\"ok\"", StringComparison.Ordinal) >= 0)
                        {
                            terminalLine = line;
                        }
                        else if (progress != null)
                        {
                            ReportProgressFrame(progress, line);
                        }
                    }
                    await Task.Run(() => proc.WaitForExit(10_000)).ConfigureAwait(false);
                    return terminalLine;
                }
            }
        }
    }

    public sealed class IpcRawResult
    {
        public string Op;
        public bool Ok;
        public bool Busy;
        public bool StillRunning;
        public string RawLine;
        public string ErrorKind;
        public string ErrorMessage;
        public string ErrorDefaultAction;
        public long ElapsedMs;
    }

    /// <summary>Outcome of a map-shaped read (lock map, status map): Ok with Value, or Busy
    /// (long-exclusive op in flight — callers keep their prior cache), or a failure with Error.</summary>
    public sealed class IpcMapResult<T> where T : class
    {
        public bool Ok;
        public bool Busy;
        public T Value;
        public string Error;

        internal IpcMapResult<T> From(IpcRawResult raw, Func<string, T> parse)
        {
            if (raw.Ok)
            {
                Value = parse(raw.RawLine);
                Ok = true;
                return this;
            }
            Busy = raw.Busy;
            Error = !string.IsNullOrEmpty(raw.ErrorDefaultAction)
                ? raw.ErrorMessage + " — " + raw.ErrorDefaultAction
                : raw.ErrorMessage ?? raw.ErrorKind ?? "IPC failure";
            return this;
        }
    }

    public sealed class IpcOperationException : Exception
    {
        public IpcResponseError Error { get; }
        public IpcOperationException(IpcResponseError error)
            : base(BuildMessage(error))
        {
            Error = error;
        }

        private static string BuildMessage(IpcResponseError e)
        {
            if (e == null) return "IPC error (no detail)";
            if (!string.IsNullOrEmpty(e.defaultAction))
                return e.kind + ": " + e.message + " — " + e.defaultAction;
            return e.kind + ": " + e.message;
        }
    }
}
