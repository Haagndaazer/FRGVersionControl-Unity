using NUnit.Framework;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.Hooks;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// Pure-planner coverage for the delete hook's disposition logic (0.2.87). These tests bind
    /// PlanDelete only: a planner that ignored the core's `versioned` list would fail the first
    /// three. They do NOT bind the hook wiring (that OnWillDeleteAsset calls PrepareDelete and
    /// honours the disposition) — that is covered by the core's repeatable live probe script
    /// (Test/LiveProbes/PrepareDelete-Probe.ps1) and the in-editor acceptance run recorded in
    /// TOOLING_NOTES.
    /// </summary>
    public class DeletePlanTests
    {
        private const string Asset = "Assets/Scenes/Foo/ReflectionProbe-0.exr";
        private const string Meta = Asset + ".meta";

        private static PrepareDeleteResult Reverted(params string[] paths) =>
            new PrepareDeleteResult { success = true, reverted = paths };

        private static PrepareDeleteResult Versioned(string path, string schedule = "normal") =>
            new PrepareDeleteResult
            {
                success = true,
                versioned = new[] { new IpcPrepareDeleteVersioned { path = path, schedule = schedule } },
            };

        private static PrepareDeleteResult Unversioned(params string[] paths) =>
            new PrepareDeleteResult { success = true, unversioned = paths };

        private static PrepareDeleteResult Failed(string path, string error) =>
            new PrepareDeleteResult
            {
                success = true,
                failed = new[] { new IpcReconcileFail { path = path, error = error } },
            };

        // ---- the 0.2.87 behaviour: a schedule-add path never reaches lock-then-delete ----

        [Test]
        public void AddedLockableAsset_Reverted_IsOsDelete()
        {
            var d = FileLockAssetModificationProcessor.PlanDelete(
                Reverted(Asset, Meta), Asset, isMeta: false, isLockable: true, lockedByMe: false);
            Assert.AreEqual(FileLockAssetModificationProcessor.DeleteDisposition.OsDelete, d);
        }

        [Test]
        public void AddedNonLockableAsset_Reverted_IsOsDelete()
        {
            var d = FileLockAssetModificationProcessor.PlanDelete(
                Reverted("Assets/Scripts/New.cs"), "Assets/Scripts/New.cs", isMeta: false, isLockable: false, lockedByMe: false);
            Assert.AreEqual(FileLockAssetModificationProcessor.DeleteDisposition.OsDelete, d);
        }

        [Test]
        public void AddedAsset_RevertFailed_IsStillOsDelete_NotFailedDelete()
        {
            var d = FileLockAssetModificationProcessor.PlanDelete(
                Failed(Asset, "svn: E155004: Working copy locked"), Asset, isMeta: false, isLockable: true, lockedByMe: false);
            Assert.AreEqual(FileLockAssetModificationProcessor.DeleteDisposition.OsDelete, d);
        }

        // ---- regression pins ----

        [Test]
        public void CommittedLockable_NotLocked_IsLockThenSvnDelete()
        {
            var d = FileLockAssetModificationProcessor.PlanDelete(
                Versioned(Asset), Asset, isMeta: false, isLockable: true, lockedByMe: false);
            Assert.AreEqual(FileLockAssetModificationProcessor.DeleteDisposition.LockThenSvnDelete, d);
        }

        [Test]
        public void CommittedLockable_LockedByMe_IsSvnDelete()
        {
            var d = FileLockAssetModificationProcessor.PlanDelete(
                Versioned(Asset), Asset, isMeta: false, isLockable: true, lockedByMe: true);
            Assert.AreEqual(FileLockAssetModificationProcessor.DeleteDisposition.SvnDelete, d);
        }

        [Test]
        public void CommittedMeta_IsSvnDelete_NeverLocked()
        {
            var d = FileLockAssetModificationProcessor.PlanDelete(
                Versioned(Meta), Meta, isMeta: true, isLockable: true, lockedByMe: false);
            Assert.AreEqual(FileLockAssetModificationProcessor.DeleteDisposition.SvnDelete, d);
        }

        [Test]
        public void CommittedNonLockable_IsSvnDelete()
        {
            var d = FileLockAssetModificationProcessor.PlanDelete(
                Versioned("Assets/Scripts/Old.cs"), "Assets/Scripts/Old.cs", isMeta: false, isLockable: false, lockedByMe: false);
            Assert.AreEqual(FileLockAssetModificationProcessor.DeleteDisposition.SvnDelete, d);
        }

        [Test]
        public void ScheduleDeleteOrReplace_StillVersioned_KeepsCommittedFlow()
        {
            foreach (var schedule in new[] { "delete", "replace", null })
            {
                var d = FileLockAssetModificationProcessor.PlanDelete(
                    Versioned(Asset, schedule), Asset, isMeta: false, isLockable: true, lockedByMe: true);
                Assert.AreEqual(FileLockAssetModificationProcessor.DeleteDisposition.SvnDelete, d, $"schedule={schedule}");
            }
        }

        [Test]
        public void Unversioned_IsOsDelete()
        {
            var d = FileLockAssetModificationProcessor.PlanDelete(
                Unversioned(Asset, Meta), Asset, isMeta: false, isLockable: true, lockedByMe: false);
            Assert.AreEqual(FileLockAssetModificationProcessor.DeleteDisposition.OsDelete, d);
        }

        [Test]
        public void IpcFailure_IsOsDelete()
        {
            var prep = new PrepareDeleteResult { success = false, error = "svn-core.exe is not available" };
            var d = FileLockAssetModificationProcessor.PlanDelete(prep, Asset, isMeta: false, isLockable: true, lockedByMe: false);
            Assert.AreEqual(FileLockAssetModificationProcessor.DeleteDisposition.OsDelete, d);
            Assert.AreEqual(FileLockAssetModificationProcessor.DeleteDisposition.OsDelete,
                FileLockAssetModificationProcessor.PlanDelete(null, Asset, false, true, false));
        }

        [Test]
        public void PathMatching_IsCaseInsensitive()
        {
            var prep = Versioned(Asset.ToUpperInvariant());
            Assert.IsTrue(prep.IsVersioned(Asset));
            Assert.IsTrue(Reverted(Asset.ToLowerInvariant()).WasReverted(Asset));
            Assert.IsNotNull(Failed(Asset.ToUpperInvariant(), "x").FailureFor(Asset));
        }
    }
}
