using NUnit.Framework;
using FRGVersionControl.Core.IpcClient;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// The client half of SVN_CORE_API.md §2.4. These pin the safety properties the r15 incident
    /// violated: a mutating request is never re-sent and never kills the core; a long-exclusive
    /// op has no client timeout; everything else fails fast as busy while one is in flight.
    /// </summary>
    public class IpcOpPolicyTests
    {
        private static readonly string[] ReadOnlyOps =
        {
            "meta.version", "meta.info", "status.query", "status.allEntries", "status.isVersioned",
            "hist.log", "hist.exportRevision", "hist.hashSha256", "hist.list", "cl.membership",
            "cl.userListGet", "override.list", "shelf.list", "shelf.diff", "prop.list", "conflict.list", "ipc.cancel",
        };

        private static readonly string[] LongExclusiveOps =
        {
            "mutate.commit", "mutate.update", "mutate.checkout", "maint.cleanup", "maint.vacuumPristines", "shelf.create", "shelf.apply",
        };

        private static readonly string[] MutatingOps =
        {
            "lock.acquire", "lock.release", "lock.releaseByUrl", "mutate.add", "mutate.addForCommit", "mutate.revert",
            "mutate.move", "mutate.delete", "mutate.reconcileMissing", "mutate.prepareDelete", "cl.add", "cl.remove",
            "cl.move", "cl.userListSet", "cl.userListAdd", "cl.userListRemove", "cl.userListRename", "conflict.resolve",
            "hist.record", "hist.clear", "auth.bootstrap", "override.begin", "override.end", "override.reconcile",
            "shelf.delete", "prop.set", "prop.delete",
        };

        [Test]
        public void Classify_MatchesTheSpecTable()
        {
            foreach (var op in ReadOnlyOps) Assert.AreEqual(IpcOpClass.ReadOnly, IpcOpPolicy.Classify(op), op);
            foreach (var op in LongExclusiveOps) Assert.AreEqual(IpcOpClass.LongExclusive, IpcOpPolicy.Classify(op), op);
            foreach (var op in MutatingOps) Assert.AreEqual(IpcOpClass.Mutating, IpcOpPolicy.Classify(op), op);
        }

        [Test]
        public void Classify_UnknownOrEmpty_IsMutating()
        {
            Assert.AreEqual(IpcOpClass.Mutating, IpcOpPolicy.Classify("mutate.somethingNew"));
            Assert.AreEqual(IpcOpClass.Mutating, IpcOpPolicy.Classify(""));
            Assert.AreEqual(IpcOpClass.Mutating, IpcOpPolicy.Classify(null));
        }

        [Test]
        public void Plan_ReadOnly_KeepsThe0270Bound_UnlessExclusiveInFlight()
        {
            var idle = IpcOpPolicy.Plan(IpcOpClass.ReadOnly, exclusiveInFlight: false);
            Assert.IsFalse(idle.SkipAsBusy);
            Assert.AreEqual(IpcOpPolicy.ReadOnlyTimeoutMs, idle.TimeoutMs);
            Assert.IsTrue(IpcOpPolicy.Plan(IpcOpClass.ReadOnly, exclusiveInFlight: true).SkipAsBusy);
        }

        [Test]
        public void Plan_Mutating_LivenessBound_AndBusyDuringExclusive()
        {
            var idle = IpcOpPolicy.Plan(IpcOpClass.Mutating, exclusiveInFlight: false);
            Assert.IsFalse(idle.SkipAsBusy);
            Assert.AreEqual(IpcOpPolicy.MutatingLivenessMs, idle.TimeoutMs);
            Assert.IsTrue(IpcOpPolicy.Plan(IpcOpClass.Mutating, exclusiveInFlight: true).SkipAsBusy);
        }

        [Test]
        public void Plan_LongExclusive_HasNoTimeout_AndIsBusyBehindAnotherExclusive()
        {
            var idle = IpcOpPolicy.Plan(IpcOpClass.LongExclusive, exclusiveInFlight: false);
            Assert.IsFalse(idle.SkipAsBusy);
            Assert.IsNull(idle.TimeoutMs);
            Assert.IsTrue(IpcOpPolicy.Plan(IpcOpClass.LongExclusive, exclusiveInFlight: true).SkipAsBusy);
        }

        [Test]
        public void OnTimeout_MutatingAndLongExclusive_NeverKill()
        {
            foreach (var cls in new[] { IpcOpClass.Mutating, IpcOpClass.LongExclusive })
            foreach (var exclusive in new[] { false, true })
            foreach (var others in new[] { 0, 1, 5 })
            {
                Assert.AreEqual(TimeoutAction.AbandonKeepPending, IpcOpPolicy.OnTimeout(cls, exclusive, others), $"{cls} excl={exclusive} others={others}");
            }
        }

        [Test]
        public void OnTimeout_ReadOnly_KillsOnlyWhenNothingMutatingIsPending()
        {
            Assert.AreEqual(TimeoutAction.KillPipeAndOneShot, IpcOpPolicy.OnTimeout(IpcOpClass.ReadOnly, false, 0));
            Assert.AreEqual(TimeoutAction.AbandonAsBusy, IpcOpPolicy.OnTimeout(IpcOpClass.ReadOnly, false, 1));
            Assert.AreEqual(TimeoutAction.AbandonAsBusy, IpcOpPolicy.OnTimeout(IpcOpClass.ReadOnly, true, 0));
        }

        [Test]
        public void MayOneShot_OnlyWhenNeverDelivered_OrReadOnlyPipeLoss()
        {
            foreach (var cls in new[] { IpcOpClass.ReadOnly, IpcOpClass.Mutating, IpcOpClass.LongExclusive })
            {
                Assert.IsTrue(IpcOpPolicy.MayOneShot(PersistentOutcome.NotDelivered, cls), cls.ToString());
                Assert.IsFalse(IpcOpPolicy.MayOneShot(PersistentOutcome.TimedOut, cls), cls.ToString());
                Assert.IsFalse(IpcOpPolicy.MayOneShot(PersistentOutcome.Completed, cls), cls.ToString());
            }
            Assert.IsTrue(IpcOpPolicy.MayOneShot(PersistentOutcome.PipeLost, IpcOpClass.ReadOnly));
            Assert.IsFalse(IpcOpPolicy.MayOneShot(PersistentOutcome.PipeLost, IpcOpClass.Mutating));
            Assert.IsFalse(IpcOpPolicy.MayOneShot(PersistentOutcome.PipeLost, IpcOpClass.LongExclusive));
        }

        [Test]
        public void SyntheticOutcomes_CarryActionSentences()
        {
            StringAssert.EndsWith("then retry.", IpcOpPolicy.BusyAction("mutate.commit"));
            StringAssert.Contains("commit", IpcOpPolicy.BusyAction("mutate.commit"));
            StringAssert.EndsWith("before retrying.", IpcOpPolicy.StillRunningAction);
            StringAssert.EndsWith("before retrying.", IpcOpPolicy.PipeLostAction);
            StringAssert.EndsWith("then retry.", IpcOpPolicy.BusyActionGeneric);
            Assert.AreEqual("wc_locked", IpcOpPolicy.BusyKind);
        }

        [Test]
        public void MainThreadBudget_IsShorterThanTheLivenessBound_AndAboveTheMutexWait()
        {
            Assert.Less(IpcOpPolicy.MainThreadWaitBudgetMs, IpcOpPolicy.MutatingLivenessMs);
            Assert.Greater(IpcOpPolicy.MainThreadWaitBudgetMs, 30000);
        }
    }
}
