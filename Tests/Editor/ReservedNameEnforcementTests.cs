using NUnit.Framework;
using FRGVersionControl.Core.Changelists;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// Single-chokepoint enforcement of the reserved `ignore-on-commit` changelist name.
    /// The project hard rule (CLAUDE.md) reserves this name exclusively for the override
    /// workflow. Any UI codepath that creates or assigns-to a changelist routes through
    /// <see cref="ChangelistService.ValidateName"/>; these tests pin the chokepoint.
    /// </summary>
    public class ReservedNameEnforcementTests
    {
        [Test]
        public void ValidateName_IgnoreOnCommit_LowerCase_Rejected()
        {
            var v = ChangelistService.ValidateName("ignore-on-commit");
            Assert.IsFalse(v.ok);
            StringAssert.Contains("reserved", v.reason);
        }

        [Test]
        public void ValidateName_IgnoreOnCommit_MixedCase_Rejected()
        {
            var v = ChangelistService.ValidateName("Ignore-On-Commit");
            Assert.IsFalse(v.ok, "case-insensitive match must reject this variant");
        }

        [Test]
        public void ValidateName_IgnoreOnCommit_UpperCase_Rejected()
        {
            var v = ChangelistService.ValidateName("IGNORE-ON-COMMIT");
            Assert.IsFalse(v.ok);
        }

        [Test]
        public void ValidateName_Empty_Rejected()
        {
            var v = ChangelistService.ValidateName("");
            Assert.IsFalse(v.ok);
            StringAssert.Contains("empty", v.reason.ToLower());
        }

        [Test]
        public void ValidateName_Whitespace_Rejected()
        {
            var v = ChangelistService.ValidateName("   ");
            Assert.IsFalse(v.ok);
        }

        [Test]
        public void ValidateName_ControlCharacters_Rejected()
        {
            var v = ChangelistService.ValidateName("name\twith\ttab");
            Assert.IsFalse(v.ok);
        }

        [Test]
        public void ValidateName_QuotesAndSlashes_Rejected()
        {
            Assert.IsFalse(ChangelistService.ValidateName("name\"with\"quotes").ok);
            Assert.IsFalse(ChangelistService.ValidateName("name/with/slashes").ok);
            Assert.IsFalse(ChangelistService.ValidateName("name\\with\\backslashes").ok);
        }

        [Test]
        public void ValidateName_NormalUserName_Accepted()
        {
            var v = ChangelistService.ValidateName("frontend-cleanup");
            Assert.IsTrue(v.ok, v.reason);
        }

        [Test]
        public void ValidateName_NameWithSpaces_Accepted()
        {
            var v = ChangelistService.ValidateName("my work in progress");
            Assert.IsTrue(v.ok, v.reason);
        }

        [Test]
        public void ReservedChangelistName_Constant_IsIgnoreOnCommit()
        {
            // Pin the constant so a future rename surfaces here as a test diff before it
            // silently breaks every override codepath.
            Assert.AreEqual("ignore-on-commit", ChangelistService.ReservedChangelistName);
        }
    }
}
