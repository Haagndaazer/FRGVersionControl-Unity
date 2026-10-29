using NUnit.Framework;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FRGVersionControl.Tests
{
    public class FRGVersionControlIntegrationTests
    {
        [Test]
        public void TestDataSerialization()
        {
            // Test FileLocks serialization
            var locks = new FileLocks();
            locks.AddLock(new FileLock("Assets/Test.unity", "test@test.com", "Test User", "TestPC"));

            string json = locks.ToJson();
            Assert.IsNotEmpty(json, "JSON should not be empty");

            var deserialized = FileLocks.FromJson(json);
            Assert.AreEqual(1, deserialized.locks.Count, "Should have 1 lock");
            Assert.AreEqual("Assets/Test.unity", deserialized.locks[0].path);
        }

        [Test]
        public void TestIsLockedByMe()
        {
            var locks = new FileLocks();
            locks.AddLock(new FileLock("Assets/Scene.unity", "user@test.com", "User", "PC"));

            bool isLocked = locks.IsLockedByUser("Assets/Scene.unity", "user@test.com", "PC");
            Assert.IsTrue(isLocked, "File should be locked by user on same machine");

            bool isLockedByOther = locks.IsLockedByOther("Assets/Scene.unity", "user@test.com", "PC");
            Assert.IsFalse(isLockedByOther, "File should not be locked by other when same email and machine");

            // Phase 2 LFS rewrite: machineName is no longer used for locked-by-other detection.
            // Git LFS doesn't track machine name in lock metadata, so same-email-different-machine
            // is treated as same-user. Same-user-different-machine is invisible under LFS.
            bool isLockedByOtherMachine = locks.IsLockedByOther("Assets/Scene.unity", "user@test.com", "PC2");
            Assert.IsFalse(isLockedByOtherMachine, "Phase 2: same email = same user, machineName ignored under LFS");
        }

        [Test]
        public void TestIsLockedByOther()
        {
            var locks = new FileLocks();
            locks.AddLock(new FileLock("Assets/Scene.unity", "user1@test.com", "User 1", "PC1"));

            bool isLockedByOther = locks.IsLockedByOther("Assets/Scene.unity", "user2@test.com", "PC2");
            Assert.IsTrue(isLockedByOther, "File should be locked by other user");
        }

        [Test]
        public void TestStaleLockDetection()
        {
            var testLock = new FileLock("Assets/Test.unity", "test@test.com", "Test User", "TestPC");

            // Set lock time to 25 hours ago
            testLock.lockedAt = DateTime.UtcNow.AddHours(-25).ToString("O");

            bool isStale = testLock.IsStale(24);
            Assert.IsTrue(isStale, "Lock should be detected as stale");

            // Test non-stale lock
            var freshLock = new FileLock("Assets/Test2.unity", "test@test.com", "Test User", "TestPC");
            bool isFresh = freshLock.IsStale(24);
            Assert.IsFalse(isFresh, "Fresh lock should not be stale");
        }

        [Test]
        public void TestMergeDriverLogic()
        {
            // Test earliest-timestamp-wins logic
            var lock1 = new FileLock("Assets/Scene.unity", "user1@test.com", "User 1", "PC1");
            lock1.lockedAt = DateTime.UtcNow.AddMinutes(-10).ToString("O");

            var lock2 = new FileLock("Assets/Scene.unity", "user2@test.com", "User 2", "PC2");
            lock2.lockedAt = DateTime.UtcNow.AddMinutes(-5).ToString("O");

            var time1 = lock1.GetLockedAtTime();
            var time2 = lock2.GetLockedAtTime();

            Assert.IsTrue(time1 < time2, "Lock 1 should be earlier than Lock 2");
        }

        [Test]
        public void TestAddRemoveLock()
        {
            var locks = new FileLocks();

            var lock1 = new FileLock("Assets/Scene1.unity", "user@test.com", "User", "PC");
            locks.AddLock(lock1);
            Assert.AreEqual(1, locks.locks.Count);

            // Add another lock for same path - should replace
            var lock2 = new FileLock("Assets/Scene1.unity", "user2@test.com", "User 2", "PC2");
            locks.AddLock(lock2);
            Assert.AreEqual(1, locks.locks.Count, "Should still have 1 lock (replaced)");
            Assert.AreEqual("user2@test.com", locks.locks[0].owner, "Should be new owner");

            // Remove lock
            bool removed = locks.RemoveLock("Assets/Scene1.unity");
            Assert.IsTrue(removed, "Lock should be removed");
            Assert.AreEqual(0, locks.locks.Count, "Should have 0 locks");
        }

        [Test]
        public void TestSvnCommandRunner()
        {
            // Phase 2 Step H: replaced TestGitCommandRunner. SvnCommandRunner is the only
            // VCS backend in this build; this test asserts svn CLI is reachable and
            // ProjectRoot resolves to a real directory.
            bool isAvailable = SvnCommandRunner.IsSvnAvailable();
            Assert.IsTrue(isAvailable, "svn CLI should be available (TortoiseSVN with CLI tools, or VisualSVN)");

            string projectRoot = SvnCommandRunner.ProjectRoot;
            Assert.IsNotEmpty(projectRoot, "Project root should be set");
            Assert.IsTrue(System.IO.Directory.Exists(projectRoot), "Project root must point at an extant directory");
        }

        [Test]
        public void TestFileLockManager()
        {
            // Test manager initialization
            var manager = FileLockManager.Instance;
            Assert.IsNotNull(manager, "Manager should not be null");

            // Note: IsInitialized may be false if svn is not available
            if (manager.IsInitialized)
            {
                Assert.IsNotEmpty(manager.UserEmail, "User email should be set");
            }
        }

        // -------------------------------------------------------------
        // Phase 2 Closure Test 6: case-mismatch ownership defensive tests.
        // -------------------------------------------------------------
        // SVN preserves the case the user authenticated with. Case drift between
        // admin-config.json (currentSvnUsername) and what `svn` reports as
        // lock owner would otherwise silently break ownership checks.
        // Step D added OrdinalIgnoreCase compares; these tests pin that contract.

        [Test]
        public void TestCaseMismatchOwnership_LowerOwnerVsUpperUsername()
        {
            var locks = new FileLocks();
            locks.AddLock(new FileLock("Assets/A.unity", "alice", "Alice", "PC1"));

            Assert.IsTrue(locks.IsLockedByUser("Assets/A.unity", "Alice", "PC1"),
                "Owner='alice' should be considered same user as 'Alice' (case-insensitive)");
            Assert.IsFalse(locks.IsLockedByOther("Assets/A.unity", "Alice", "PC1"),
                "Owner='alice' should NOT be locked-by-other when current user is 'Alice'");
        }

        [Test]
        public void TestCaseMismatchOwnership_UpperOwnerVsLowerUsername()
        {
            var locks = new FileLocks();
            locks.AddLock(new FileLock("Assets/B.unity", "ALICE", "ALICE", "PC1"));

            Assert.IsTrue(locks.IsLockedByUser("Assets/B.unity", "alice", "PC1"),
                "Owner='ALICE' should be considered same user as 'alice'");
            Assert.IsFalse(locks.IsLockedByOther("Assets/B.unity", "alice", "PC1"),
                "Owner='ALICE' should NOT be locked-by-other when current user is 'alice'");
        }

        [Test]
        public void TestCaseMismatchOwnership_DifferentUserStillDistinguished()
        {
            var locks = new FileLocks();
            locks.AddLock(new FileLock("Assets/C.unity", "alice", "Alice", "PC1"));

            Assert.IsFalse(locks.IsLockedByUser("Assets/C.unity", "bob", "PC1"),
                "alice's lock must not match user 'bob' regardless of case logic");
            Assert.IsTrue(locks.IsLockedByOther("Assets/C.unity", "bob", "PC1"),
                "alice's lock IS locked-by-other from bob's perspective");
        }

        // -------------------------------------------------------------
        // Phase 2 Closure WP-C remediation: SvnCommandRunner.Commit must throw
        // ArgumentException for null/empty paths BEFORE invoking svn (Phase 1
        // Addendum Test 2 — bare `svn commit -m` without paths is unsafe).
        // -------------------------------------------------------------

        [Test]
        public void TestCommitRejectsNullPaths()
        {
            Assert.Throws<System.ArgumentException>(
                () => FRGVersionControl.Core.Svn.SvnCommandRunner.Commit(null, "msg"),
                "Commit(null, ...) must throw ArgumentException — bare commit is unsafe");
        }

        [Test]
        public void TestCommitRejectsEmptyPathArray()
        {
            Assert.Throws<System.ArgumentException>(
                () => FRGVersionControl.Core.Svn.SvnCommandRunner.Commit(new string[0], "msg"),
                "Commit(empty array, ...) must throw ArgumentException");
        }

        [Test]
        public void TestCommitRejectsAllWhitespacePaths()
        {
            Assert.Throws<System.ArgumentException>(
                () => FRGVersionControl.Core.Svn.SvnCommandRunner.Commit(new[] { "  ", "" }, "msg"),
                "Commit(whitespace-only paths) must throw ArgumentException");
        }

        // -------------------------------------------------------------
        // Phase 3 closure Test 4: parser regression tests for K/O/B/T/None states.
        // -------------------------------------------------------------
        // Phase 3 Session 2 surfaced a parser bug: ParseSingleStatusEntry and
        // ParseLockInfoFromEntry mis-detected `B` because they required `<repos-status>`
        // element presence, but SVN omits that element when the server has no lock at all.
        // Fix detects server-was-queried via `<against>` element. These tests pin the
        // contract against future drift. XML samples captured live from svn 1.14.5 against
        // VisualSVN Server during Phase 3 closure — represent real wire format, not invented.
        // BOTH parsers tested because they have duplicated-but-not-identical logic.

        private const string TestPath = "Assets\\Scenes\\Cockpit\\Art\\Environment\\Materials\\AsteroidCluster_Mat.mat";

        private const string XmlNone = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<status>
<target path=""Assets\Scenes\Cockpit\Art\Environment\Materials\AsteroidCluster_Mat.mat"">
<against revision=""27""/>
</target>
</status>";

        private const string XmlK = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<status>
<target path=""Assets\Scenes\Cockpit\Art\Environment\Materials\AsteroidCluster_Mat.mat"">
<entry path=""Assets\Scenes\Cockpit\Art\Environment\Materials\AsteroidCluster_Mat.mat"">
<wc-status item=""normal"" revision=""24"" props=""normal"">
<commit revision=""24""><author>admin</author><date>2026-05-07T21:37:09.610377Z</date></commit>
<lock>
<token>opaquelocktoken:9e970409-3f68-0242-947e-00833a3da804</token>
<owner>admin</owner>
<comment>[xml-K]</comment>
<created>2026-05-07T22:44:14.890089Z</created>
</lock>
</wc-status>
<repos-status props=""none"" item=""none"">
<lock>
<token>opaquelocktoken:9e970409-3f68-0242-947e-00833a3da804</token>
<owner>admin</owner>
<comment>[xml-K]</comment>
<created>2026-05-07T22:44:14.890089Z</created>
</lock>
</repos-status>
</entry>
<against revision=""27""/>
</target>
</status>";

        private const string XmlO = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<status>
<target path=""Assets\Scenes\Cockpit\Art\Environment\Materials\AsteroidCluster_Mat.mat"">
<entry path=""Assets\Scenes\Cockpit\Art\Environment\Materials\AsteroidCluster_Mat.mat"">
<wc-status item=""normal"" revision=""27"" props=""normal""></wc-status>
<repos-status item=""none"" props=""none"">
<lock>
<token>opaquelocktoken:9e970409-3f68-0242-947e-00833a3da804</token>
<owner>admin</owner>
<comment>[xml-K]</comment>
<created>2026-05-07T22:44:14.890089Z</created>
</lock>
</repos-status>
</entry>
<against revision=""27""/>
</target>
</status>";

        // The exact case Phase 3 fixed: wcLock present, no <repos-status> at all,
        // <against> indicates server WAS queried — server has no lock; admin's WC has stale token.
        private const string XmlB = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<status>
<target path=""Assets\Scenes\Cockpit\Art\Environment\Materials\AsteroidCluster_Mat.mat"">
<entry path=""Assets\Scenes\Cockpit\Art\Environment\Materials\AsteroidCluster_Mat.mat"">
<wc-status item=""normal"" revision=""24"" props=""normal"">
<commit revision=""24""><author>admin</author><date>2026-05-07T21:37:09.610377Z</date></commit>
<lock>
<token>opaquelocktoken:9e970409-3f68-0242-947e-00833a3da804</token>
<owner>admin</owner>
<comment>[xml-K]</comment>
<created>2026-05-07T22:44:14.890089Z</created>
</lock>
</wc-status>
</entry>
<against revision=""27""/>
</target>
</status>";

        private const string XmlT = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<status>
<target path=""Assets\Scenes\Cockpit\Art\Environment\Materials\AsteroidCluster_Mat.mat"">
<entry path=""Assets\Scenes\Cockpit\Art\Environment\Materials\AsteroidCluster_Mat.mat"">
<wc-status item=""normal"" revision=""24"" props=""normal"">
<lock>
<token>opaquelocktoken:9e970409-3f68-0242-947e-00833a3da804</token>
<owner>admin</owner>
<comment>[xml-K]</comment>
<created>2026-05-07T22:44:14.890089Z</created>
</lock>
</wc-status>
<repos-status item=""none"" props=""none"">
<lock>
<token>opaquelocktoken:7d2bfae9-52e6-be40-abbb-6d1d562ba7c6</token>
<owner>artist</owner>
<comment>[xml-T-setup] artist new lock</comment>
<created>2026-05-07T22:44:34.379682Z</created>
</lock>
</repos-status>
</entry>
<against revision=""27""/>
</target>
</status>";

        // ----- ParseStatusXmlForTest (wraps ParseSingleStatusEntry) -----

        [Test]
        public void TestParseStatusEntry_None()
        {
            var entry = FRGVersionControl.Core.Svn.SvnCommandRunner.ParseStatusXmlForTest(XmlNone, TestPath);
            Assert.AreEqual(FRGVersionControl.Core.Svn.LockState.None, entry.lockState);
        }

        [Test]
        public void TestParseStatusEntry_K_LockedByMe()
        {
            var entry = FRGVersionControl.Core.Svn.SvnCommandRunner.ParseStatusXmlForTest(XmlK, TestPath);
            Assert.AreEqual(FRGVersionControl.Core.Svn.LockState.K, entry.lockState);
            Assert.AreEqual("admin", entry.lockOwner);
        }

        [Test]
        public void TestParseStatusEntry_O_LockedByOther()
        {
            var entry = FRGVersionControl.Core.Svn.SvnCommandRunner.ParseStatusXmlForTest(XmlO, TestPath);
            Assert.AreEqual(FRGVersionControl.Core.Svn.LockState.O, entry.lockState);
            Assert.AreEqual("admin", entry.lockOwner);
        }

        // The exact case Phase 3 fixed.
        [Test]
        public void TestParseStatusEntry_B_BrokenToken()
        {
            var entry = FRGVersionControl.Core.Svn.SvnCommandRunner.ParseStatusXmlForTest(XmlB, TestPath);
            Assert.AreEqual(FRGVersionControl.Core.Svn.LockState.B, entry.lockState);
            Assert.AreEqual("admin", entry.lockOwner);
        }

        [Test]
        public void TestParseStatusEntry_T_StolenToken()
        {
            var entry = FRGVersionControl.Core.Svn.SvnCommandRunner.ParseStatusXmlForTest(XmlT, TestPath);
            Assert.AreEqual(FRGVersionControl.Core.Svn.LockState.T, entry.lockState);
            // T case: lockOwner reports the server-side (new) owner, not the WC's stale one
            Assert.AreEqual("artist", entry.lockOwner);
        }

        // ----- ParseLockInfoFromXmlForTest (wraps ParseLockInfoFromEntry — used by StatusUpstreamAll) -----

        [Test]
        public void TestParseLockInfo_K_LockedByMe()
        {
            var info = FRGVersionControl.Core.Svn.SvnCommandRunner.ParseLockInfoFromXmlForTest(XmlK, TestPath);
            Assert.AreEqual(FRGVersionControl.Core.Svn.LockState.K, info.state);
            Assert.AreEqual("admin", info.lockOwner);
        }

        [Test]
        public void TestParseLockInfo_O_LockedByOther()
        {
            var info = FRGVersionControl.Core.Svn.SvnCommandRunner.ParseLockInfoFromXmlForTest(XmlO, TestPath);
            Assert.AreEqual(FRGVersionControl.Core.Svn.LockState.O, info.state);
            Assert.AreEqual("admin", info.lockOwner);
        }

        [Test]
        public void TestParseLockInfo_B_BrokenToken()
        {
            var info = FRGVersionControl.Core.Svn.SvnCommandRunner.ParseLockInfoFromXmlForTest(XmlB, TestPath);
            Assert.AreEqual(FRGVersionControl.Core.Svn.LockState.B, info.state);
            Assert.AreEqual("admin", info.lockOwner);
        }

        [Test]
        public void TestParseLockInfo_T_StolenToken()
        {
            var info = FRGVersionControl.Core.Svn.SvnCommandRunner.ParseLockInfoFromXmlForTest(XmlT, TestPath);
            Assert.AreEqual(FRGVersionControl.Core.Svn.LockState.T, info.state);
            Assert.AreEqual("artist", info.lockOwner);
        }

        // -------------------------------------------------------------
        // Phase 4 Step 0: async wrapper contracts + silence-service counters.
        // -------------------------------------------------------------

        [Test]
        public void TestRunAsync_HappyPath()
        {
            // svn --version --quiet always succeeds and exits fast; safe smoke for the async path.
            var task = SvnCommandRunner.RunAsync("--version --quiet");
            Assert.IsTrue(task.Wait(5000), "RunAsync should complete within 5s for a trivial command");
            var result = task.Result;
            Assert.IsTrue(result.success, $"--version --quiet should succeed; got error: {result.error}");
        }

        [Test]
        public void TestRunAsync_CancellationBeforeStart()
        {
            // Pre-cancelled token should throw on await.
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var task = SvnCommandRunner.RunAsync("--version --quiet", ct: cts.Token);
            // Wait throws AggregateException(TaskCanceledException) when the underlying op was cancelled.
            Assert.Throws(typeof(AggregateException), () => task.Wait(5000));
            Assert.IsTrue(task.IsCanceled || task.IsFaulted,
                "Pre-cancelled RunAsync should be Canceled or Faulted, not RanToCompletion");
        }

        [Test]
        public void TestSyncContext_Captured()
        {
            // Static-init capture should land a non-null SynchronizationContext.
            // (May be the default SynchronizationContext on test runner, but never null.)
            Assert.IsNotNull(SvnCommandRunner.s_UnityMainSync,
                "Unity main SynchronizationContext must be captured at static init");
        }

        [Test]
        public void TestSilenceService_RequestClearPaired()
        {
            Assert.IsFalse(FileLockSilenceService.IsSilenced, "Should start not-silenced");
            FileLockSilenceService.RequestSilence();
            Assert.IsTrue(FileLockSilenceService.IsSilenced, "After request, should be silenced");
            FileLockSilenceService.ClearSilence();
            Assert.IsFalse(FileLockSilenceService.IsSilenced, "After paired clear, back to not-silenced");
        }

        [Test]
        public void TestSilenceService_NestedCounters()
        {
            FileLockSilenceService.RequestSilence();
            FileLockSilenceService.RequestSilence();
            FileLockSilenceService.ClearSilence();
            Assert.IsTrue(FileLockSilenceService.IsSilenced,
                "Nested requests need matching clears; one clear is not enough");
            FileLockSilenceService.ClearSilence();
            Assert.IsFalse(FileLockSilenceService.IsSilenced,
                "After all clears match all requests, back to not-silenced");
        }

        [Test]
        public void TestSilenceService_MismatchedClearClampsToZero()
        {
            // Pre-condition: counter at 0
            Assert.IsFalse(FileLockSilenceService.IsSilenced);
            // Extra Clear without matching Request should clamp at 0, not go negative
            FileLockSilenceService.ClearSilence();
            Assert.IsFalse(FileLockSilenceService.IsSilenced,
                "Counter must clamp at 0; mismatched Clear should not produce a 'negative' silenced state");
            // Subsequent Request should still work normally
            FileLockSilenceService.RequestSilence();
            Assert.IsTrue(FileLockSilenceService.IsSilenced);
            FileLockSilenceService.ClearSilence();
        }

        [Test]
        public void TestSilenceService_DisableIsIndependent()
        {
            FileLockSilenceService.RequestSilence();
            Assert.IsTrue(FileLockSilenceService.IsSilenced);
            Assert.IsFalse(FileLockSilenceService.IsTemporarilyDisabled,
                "Silence and TemporaryDisable are independent counters");
            FileLockSilenceService.RequestTemporaryDisable();
            Assert.IsTrue(FileLockSilenceService.IsTemporarilyDisabled);
            FileLockSilenceService.ClearSilence();
            Assert.IsFalse(FileLockSilenceService.IsSilenced);
            Assert.IsTrue(FileLockSilenceService.IsTemporarilyDisabled,
                "Clearing Silence should not affect TemporaryDisable counter");
            FileLockSilenceService.ClearTemporaryDisable();
            Assert.IsFalse(FileLockSilenceService.IsTemporarilyDisabled);
        }

        // -------------------------------------------------------------
        // Phase 4 Step X: meta-aware locking — asset and meta locks are independent;
        // GetLockabilityCheckPath translates .meta paths to their underlying asset path
        // so the lockable-extensions gate evaluates against the asset, not the literal
        // ".meta" extension (which is never in the lockable set). Pure path logic; no svn calls.
        // -------------------------------------------------------------

        [Test]
        public void TestAssetAndMetaLocksAreIndependent()
        {
            // Asset locked by alice, meta locked by bob. Both must coexist as distinct entries
            // and FindLock must return the matching one for each path — never coalesce.
            var locks = new FileLocks();
            locks.AddLock(new FileLock("Assets/Tex/Foo.png", "alice", "Alice", "PC1"));
            locks.AddLock(new FileLock("Assets/Tex/Foo.png.meta", "bob", "Bob", "PC2"));

            var assetLock = locks.FindLock("Assets/Tex/Foo.png");
            var metaLock = locks.FindLock("Assets/Tex/Foo.png.meta");

            Assert.IsNotNull(assetLock, "Asset lock must be findable");
            Assert.IsNotNull(metaLock, "Meta lock must be findable");
            Assert.AreEqual("alice", assetLock.owner, "Asset lock owner");
            Assert.AreEqual("bob", metaLock.owner, "Meta lock owner");
            Assert.AreNotSame(assetLock, metaLock, "Asset and meta locks must be distinct entries");
            Assert.AreEqual(2, locks.locks.Count, "Both locks must coexist; pair-aware locking was abandoned");
        }

        [Test]
        public void TestMetaLockDoesNotMaskAssetLockup()
        {
            // Only the meta is locked. FindLock for the asset path must return null —
            // confirms paths are distinct keys (no stripping/coercion).
            var locks = new FileLocks();
            locks.AddLock(new FileLock("Assets/Tex/Foo.png.meta", "alice", "Alice", "PC1"));

            Assert.IsNull(locks.FindLock("Assets/Tex/Foo.png"),
                "Meta-only lock must NOT match the asset path's lookup");
            Assert.IsNotNull(locks.FindLock("Assets/Tex/Foo.png.meta"),
                "Meta-only lock must match the meta path's lookup");
        }

        [Test]
        public void TestGetLockabilityCheckPathTranslatesMeta()
        {
            // .meta strips to the underlying asset path; non-meta passes through unchanged.
            // Drives the OnWillSaveAssets gate that uses the lockable-extensions check against
            // the asset's extension rather than the literal ".meta".
            Assert.AreEqual("Assets/Tex/Foo.png",
                FileLockManager.GetLockabilityCheckPath("Assets/Tex/Foo.png.meta"),
                ".meta should strip to asset path");
            Assert.AreEqual("Assets/Foo.cs",
                FileLockManager.GetLockabilityCheckPath("Assets/Foo.cs.meta"),
                ".meta should strip regardless of asset's extension");
            Assert.AreEqual("Assets/Foo.png",
                FileLockManager.GetLockabilityCheckPath("Assets/Foo.png"),
                "non-meta path should pass through unchanged");
            Assert.AreEqual("",
                FileLockManager.GetLockabilityCheckPath(""),
                "empty input passes through");
            Assert.IsNull(FileLockManager.GetLockabilityCheckPath(null),
                "null input passes through");
            // Case-insensitive .META suffix should also be recognized.
            Assert.AreEqual("Assets/Foo.png",
                FileLockManager.GetLockabilityCheckPath("Assets/Foo.png.META"),
                ".META (upper) should strip same as .meta (lower)");
        }
    }
}
