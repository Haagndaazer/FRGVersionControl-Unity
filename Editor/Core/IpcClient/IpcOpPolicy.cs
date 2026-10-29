using System;
using System.Collections.Generic;

namespace FRGVersionControl.Core.IpcClient
{
    public enum IpcOpClass
    {
        ReadOnly,
        Mutating,
        LongExclusive,
    }

    public enum PersistentOutcome
    {
        NotDelivered,
        Completed,
        TimedOut,
        PipeLost,
    }

    public enum TimeoutAction
    {
        KillPipeAndOneShot,
        AbandonAsBusy,
        AbandonKeepPending,
    }

    public struct IpcRequestPlan
    {
        public bool SkipAsBusy;
        public int? TimeoutMs;
    }

    /// <summary>
    /// The client half of SVN_CORE_API.md §2.4 (recommended client request policy), kept as pure
    /// functions so the rules are unit-testable without a process. Unknown ops classify as
    /// Mutating: the safe side is "never re-send, never kill".
    /// </summary>
    public static class IpcOpPolicy
    {
        public const int ReadOnlyTimeoutMs = 5000;
        public const int MutatingLivenessMs = 60000;
        public const int MainThreadWaitBudgetMs = 35000;
        public static readonly TimeSpan AbandonedNoLongerBlocksKillAfter = TimeSpan.FromMinutes(5);

        public const string BusyKind = "wc_locked";
        public const string StillRunningKind = "still_running";

        private static readonly HashSet<string> s_ReadOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "meta.version", "meta.info",
            "status.query", "status.allEntries", "status.isVersioned",
            "hist.log", "hist.exportRevision", "hist.hashSha256", "hist.list",
            "cl.membership", "cl.userListGet",
            "override.list",
            "shelf.list", "shelf.diff",
            "prop.list",
            "conflict.list",
            "ipc.cancel",
        };

        private static readonly HashSet<string> s_LongExclusive = new HashSet<string>(StringComparer.Ordinal)
        {
            "mutate.commit", "mutate.update", "mutate.checkout",
            "maint.cleanup", "maint.vacuumPristines",
            "shelf.create", "shelf.apply",
        };

        public static IpcOpClass Classify(string op)
        {
            if (string.IsNullOrEmpty(op)) return IpcOpClass.Mutating;
            if (s_ReadOnly.Contains(op)) return IpcOpClass.ReadOnly;
            if (s_LongExclusive.Contains(op)) return IpcOpClass.LongExclusive;
            return IpcOpClass.Mutating;
        }

        public static IpcRequestPlan Plan(IpcOpClass cls, bool exclusiveInFlight)
        {
            switch (cls)
            {
                case IpcOpClass.LongExclusive:
                    return exclusiveInFlight
                        ? new IpcRequestPlan { SkipAsBusy = true }
                        : new IpcRequestPlan { TimeoutMs = null };
                case IpcOpClass.ReadOnly:
                    return exclusiveInFlight
                        ? new IpcRequestPlan { SkipAsBusy = true }
                        : new IpcRequestPlan { TimeoutMs = ReadOnlyTimeoutMs };
                default:
                    return exclusiveInFlight
                        ? new IpcRequestPlan { SkipAsBusy = true }
                        : new IpcRequestPlan { TimeoutMs = MutatingLivenessMs };
            }
        }

        /// <summary>What to do when a request's timer fires. The pipe is killed only for a
        /// read-only request when nothing mutating is pending behind it; a mutating request is
        /// left pending (the core still runs it) and never re-sent.</summary>
        public static TimeoutAction OnTimeout(IpcOpClass cls, bool exclusiveInFlight, int otherNonReadPendingCount)
        {
            if (cls != IpcOpClass.ReadOnly) return TimeoutAction.AbandonKeepPending;
            if (exclusiveInFlight) return TimeoutAction.AbandonAsBusy;
            return otherNonReadPendingCount == 0 ? TimeoutAction.KillPipeAndOneShot : TimeoutAction.AbandonAsBusy;
        }

        /// <summary>Whether a request may be re-issued through a fresh one-shot process after the
        /// persistent pipe gave the given outcome. Only a never-delivered request, or a read-only
        /// request whose pipe died, is safe to re-issue.</summary>
        public static bool MayOneShot(PersistentOutcome outcome, IpcOpClass cls)
        {
            if (outcome == PersistentOutcome.NotDelivered) return true;
            if (outcome == PersistentOutcome.PipeLost) return cls == IpcOpClass.ReadOnly;
            return false;
        }

        public static string BusyMessage(string op, string exclusiveOp) =>
            op + " skipped: " + exclusiveOp + " is in progress";

        public static string BusyMessageGeneric(string op) =>
            op + " skipped: another operation is still running";

        public const string BusyActionGeneric = "Another operation is still running. Wait for it to finish, then retry.";

        public static string BusyAction(string exclusiveOp) =>
            "A " + Describe(exclusiveOp) + " is in progress. Wait for it to finish, then retry.";

        public const string StillRunningAction = "Refresh status to see the outcome before retrying.";

        public static string StillRunningMessage(string op, int waitedMs) =>
            "svn-core is still running " + op + " after " + (waitedMs / 1000) + " s; the request was not cancelled";

        public const string PipeLostAction = "Refresh status before retrying.";

        public static string PipeLostMessage(string op) =>
            "svn-core exited while " + op + " was running";

        public static string Describe(string op)
        {
            switch (op)
            {
                case "mutate.commit": return "commit";
                case "mutate.update": return "update";
                case "mutate.checkout": return "checkout";
                case "maint.cleanup": return "cleanup";
                case "maint.vacuumPristines": return "pristine vacuum";
                case "shelf.create": return "shelve";
                case "shelf.apply": return "unshelve";
                default: return op;
            }
        }
    }
}
