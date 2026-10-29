using NUnit.Framework;
using FRGVersionControl.UI;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// Pure-predicate coverage for the wc-locked detector that drives the cleanup recovery
    /// banner. The UI state-machine itself (set flag on error / clear on refresh / clear on
    /// dismiss) is verified manually per CLAUDE.md polish-bar in-editor flow.
    /// </summary>
    public class CleanupBannerStateTests
    {
        [Test]
        public void IsWcLockedError_E155004_True()
        {
            Assert.IsTrue(FrgVersionControlWindow.IsWcLockedError(
                "svn: E155004: Working copy 'C:/foo' locked"));
        }

        [Test]
        public void IsWcLockedError_E155010_True()
        {
            Assert.IsTrue(FrgVersionControlWindow.IsWcLockedError("svn: E155010: foo"));
        }

        [Test]
        public void IsWcLockedError_E155007_True()
        {
            Assert.IsTrue(FrgVersionControlWindow.IsWcLockedError("svn: E155007: foo"));
        }

        [Test]
        public void IsWcLockedError_OtherError_False()
        {
            Assert.IsFalse(FrgVersionControlWindow.IsWcLockedError("svn: E195022: lock missing"));
            Assert.IsFalse(FrgVersionControlWindow.IsWcLockedError("svn: E165001: hook rejected"));
        }

        [Test]
        public void IsWcLockedError_EmptyOrNull_False()
        {
            Assert.IsFalse(FrgVersionControlWindow.IsWcLockedError(""));
            Assert.IsFalse(FrgVersionControlWindow.IsWcLockedError(null));
        }

        [Test]
        public void IsWcLockedError_MultilineErrorContainingCode_True()
        {
            string multiline = "svn: warning: foo\nsvn: E155004: Working copy locked\nsvn: bar";
            Assert.IsTrue(FrgVersionControlWindow.IsWcLockedError(multiline));
        }
    }
}
