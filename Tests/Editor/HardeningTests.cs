using System;
using System.IO;
using NUnit.Framework;
using FRGVersionControl.Core;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.Services;
using FRGVersionControl.Tools;

namespace FRGVersionControl.Tests
{
    /// <summary>Pure tests for the 0.2.92 hardening helpers (no IPC, no working copy).</summary>
    public class HardeningTests
    {
        [Test]
        public void ProjectFolder_IsTheParentOfAssets_WithNoStringMatching()
        {
            Assert.AreEqual("C:/Unity/Visa/trunk", ProjectFolder.FromDataPath("C:/Unity/Visa/trunk/Assets"));
            Assert.AreEqual("D:/Assets/MyGame", ProjectFolder.FromDataPath("D:/Assets/MyGame/Assets"));
            Assert.AreEqual("C:/Work/Assets Library/Game", ProjectFolder.FromDataPath("C:/Work/Assets Library/Game/Assets"));
            Assert.AreEqual("C:/Proj", ProjectFolder.FromDataPath("C:\\Proj\\Assets\\"));
        }

        [Test]
        public void PathKey_NormalizesSeparatorsAndComparesIgnoringCase()
        {
            Assert.AreEqual("Assets/a.png", PathKey.Normalize("./Assets\\a.png"));
            Assert.AreEqual("Assets/Folder", PathKey.Normalize("Assets/Folder/"));
            Assert.IsTrue(PathKey.Same("Assets\\A.PNG", "assets/a.png"));
            Assert.IsFalse(PathKey.Same("Assets/a.png", "Assets/a.png.meta"));
        }

        [Test]
        public void ProjectPrefsHash_IsStablePerFolder()
        {
            Assert.AreEqual(ProjectPrefs.HashOf("C:/Unity/Visa/trunk"), ProjectPrefs.HashOf("c:\\unity\\visa\\TRUNK\\"));
            Assert.AreNotEqual(ProjectPrefs.HashOf("C:/Unity/Visa/trunk"), ProjectPrefs.HashOf("C:/Unity/Visa/branches/b1"));
        }

        [Test]
        public void IpcJson_TopLevelLookup_IgnoresNestedKeysAndStringText()
        {
            string progress = "{\"v\":1,\"id\":\"r1\",\"progress\":{\"phase\":\"log\",\"msg\":\"Adding \\\"ok\\\": done\"}}";
            Assert.IsFalse(IpcSvnClient.IsTerminalFrame(progress));
            Assert.AreEqual("r1", IpcJson.ReadScalar(progress, "id"));

            string terminal = "{\"v\":1,\"id\":\"r1\",\"ok\":true,\"result\":{\"ok\":false,\"versioned\":true,\"tempPath\":\"C:/Users/Jos\\u00e9/a b.png\"}}";
            Assert.IsTrue(IpcSvnClient.IsTerminalFrame(terminal));
            Assert.AreEqual("true", IpcJson.ReadScalar(terminal, "ok"));
            Assert.AreEqual("false", IpcJson.ReadScalar(terminal, "ok", inResult: true));
            Assert.AreEqual("C:/Users/Jos" + (char)0xE9 + "/a b.png", IpcJson.ReadScalar(terminal, "tempPath", inResult: true));
            Assert.IsNull(IpcJson.ReadScalar(terminal, "missing", inResult: true));
        }

        [Test]
        public void VersionedState_OnlyAnAnswerIsDefinitive()
        {
            Assert.AreEqual(SvnCommandRunner.VersionedState.Unknown, SvnCommandRunner.ClassifyVersioned(new IpcRawResult { Busy = true }));
            Assert.AreEqual(SvnCommandRunner.VersionedState.Unknown, SvnCommandRunner.ClassifyVersioned(new IpcRawResult { StillRunning = true }));
            Assert.AreEqual(SvnCommandRunner.VersionedState.Unknown, SvnCommandRunner.ClassifyVersioned(new IpcRawResult { PipeLost = true }));
            Assert.AreEqual(SvnCommandRunner.VersionedState.Unknown, SvnCommandRunner.ClassifyVersioned(new IpcRawResult { Ok = false, ErrorKind = "path_not_found" }));
            Assert.AreEqual(SvnCommandRunner.VersionedState.Unknown, SvnCommandRunner.ClassifyVersioned(new IpcRawResult { Ok = false, ErrorKind = "timeout" }));
            Assert.AreEqual(SvnCommandRunner.VersionedState.Unknown, SvnCommandRunner.ClassifyVersioned(null));
            Assert.AreEqual(SvnCommandRunner.VersionedState.Versioned,
                SvnCommandRunner.ClassifyVersioned(new IpcRawResult { Ok = true, RawLine = "{\"v\":1,\"id\":\"x\",\"ok\":true,\"result\":{\"versioned\":true}}" }));
            Assert.AreEqual(SvnCommandRunner.VersionedState.Unversioned,
                SvnCommandRunner.ClassifyVersioned(new IpcRawResult { Ok = true, RawLine = "{\"v\":1,\"id\":\"x\",\"ok\":true,\"result\":{\"versioned\":false}}" }));
        }

        [Test]
        public void WizardLayoutLines_RootNestedAndWholeRepository()
        {
            var root = SetupWizardText.LayoutLines(new WorkingCopyLayout.Info { isWcRoot = true });
            Assert.AreEqual(1, root.Count);
            StringAssert.Contains("at the project folder", root[0]);

            var nested = SetupWizardText.LayoutLines(new WorkingCopyLayout.Info
            {
                isWcRoot = false, projectPath = "trunk", wcRoot = "C:/Unity/Visa",
                projectUrl = "https://svn.example/svn/visa/trunk", wcRootIsRepositoryRoot = true,
            });
            Assert.AreEqual(2, nested.Count);
            StringAssert.Contains("'trunk' folder of the checkout at C:/Unity/Visa", nested[0]);
            StringAssert.StartsWith("Note:", nested[1]);
            StringAssert.Contains("https://svn.example/svn/visa/trunk", nested[1]);
            StringAssert.DoesNotContain("[FAIL]", nested[1]);
            StringAssert.DoesNotContain("[WARN]", nested[1]);
        }

        [Test]
        public void AutoPropsCheck_ParsesTheIniInsteadOfMatchingText()
        {
            const string good = "[miscellany]\nenable-auto-props=YES\n[auto-props]\n*.png = svn:needs-lock=*\n";
            Assert.IsTrue(SetupWizardText.AutoPropsSetNeedsLock(good));
            const string commented = "[miscellany]\n# enable-auto-props = yes\n[auto-props]\n# *.png = svn:needs-lock=*\n";
            Assert.IsFalse(SetupWizardText.AutoPropsSetNeedsLock(commented));
            const string wrongSection = "[helpers]\nenable-auto-props = yes\n[auto-props]\n*.png = svn:needs-lock=*\n";
            Assert.IsFalse(SetupWizardText.AutoPropsSetNeedsLock(wrongSection));
            const string noRule = "[miscellany]\nenable-auto-props = yes\n[auto-props]\n*.txt = svn:eol-style=native\n";
            Assert.IsFalse(SetupWizardText.AutoPropsSetNeedsLock(noRule));
            Assert.IsFalse(SetupWizardText.AutoPropsSetNeedsLock(null));
        }

        [Test]
        public void ScratchGuard_DecodesFileUrls()
        {
            var under = Path.Combine(Path.GetTempPath(), "frg scratch & co", "repo");
            Assert.IsTrue(ScratchWcGuard.IsFileUrlUnderTemp(new Uri(under).AbsoluteUri));
            Assert.IsFalse(ScratchWcGuard.IsFileUrlUnderTemp("https://svn.example/repo"));
            Assert.IsFalse(ScratchWcGuard.IsFileUrlUnderTemp(new Uri("C:/NotTemp/repo").AbsoluteUri));
            Assert.IsFalse(ScratchWcGuard.IsFileUrlUnderTemp(null));
        }

        [Test]
        public void AuthRealmHostPort_UsesTheSchemesDefaultPort()
        {
            Assert.AreEqual("<https://svn.example:443>", SvnAuthCacheReader.ExtractHostPort("https://svn.example/svn/visa"));
            Assert.AreEqual("<svn://svn.example:3690>", SvnAuthCacheReader.ExtractHostPort("svn://svn.example/visa"));
            Assert.AreEqual("<https://svn.example:8443>", SvnAuthCacheReader.ExtractHostPort("https://svn.example:8443/svn/visa"));
            Assert.IsTrue(SvnAuthCacheReader.UsesPasswordCache("https://svn.example/svn/visa"));
            Assert.IsFalse(SvnAuthCacheReader.UsesPasswordCache("file:///C:/repo"));
        }

        [Test]
        public void ConflictMarkers_AnywhereInTheFile_BothEndsRequired()
        {
            Assert.IsTrue(FileLockConflictShadowService.HasConflictMarkerText(
                "using UnityEngine;\nclass A {\n<<<<<<< .mine\nint a;\n=======\nint b;\n>>>>>>> .r7\n}\n"));
            Assert.IsFalse(FileLockConflictShadowService.HasConflictMarkerText("// <<<<<<< .mine in a comment\nclass A {}\n"));
            Assert.IsFalse(FileLockConflictShadowService.HasConflictMarkerText("<<<<<<< .mine\nonly a start marker\n"));
            Assert.IsFalse(FileLockConflictShadowService.HasConflictMarkerText(""));
        }

        [Test]
        public void ConflictSidecarFilter_NeedsTheConflictedFileBesideIt()
        {
            Assert.IsFalse(SvnStatusCache.IsSvnConflictSidecar("Assets/__frg_no_such_dir__/save.r1"));
            Assert.IsFalse(SvnStatusCache.IsSvnConflictSidecar("Assets/__frg_no_such_dir__/notes.mine"));
            Assert.IsFalse(SvnStatusCache.IsSvnConflictSidecar("Assets/plain.png"));
        }

        [Test]
        public void ChangelistNames_RejectTheNoChangelistLabel()
        {
            Assert.IsFalse(FRGVersionControl.Core.Changelists.ChangelistService.ValidateName("(no changelist)").ok);
            Assert.IsTrue(FRGVersionControl.Core.Changelists.ChangelistService.ValidateName("Art pass").ok);
        }
    }
}
