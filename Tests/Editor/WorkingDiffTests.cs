using System;
using NUnit.Framework;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Diff;
using FRGVersionControl.Core.IpcClient;

namespace FRGVersionControl.Tests
{
    public class WorkingDiffTests
    {
        private static string Reply(string result) => "{\"v\":1,\"id\":\"u1\",\"ok\":true,\"result\":" + result + "}";

        [Test]
        public void Parse_TextState_ReadsRowsCountsAndOptionalFields()
        {
            var diff = WorkingDiffParser.Parse(Reply(
                "{\"state\":\"text\",\"baseRevision\":12,\"movedFrom\":\"..\\\\b.txt\",\"hasBase\":true,\"hasWorking\":true," +
                "\"added\":2,\"removed\":1,\"truncated\":false,\"rows\":[" +
                "{\"kind\":\"gap\",\"skipped\":9}," +
                "{\"kind\":\"same\",\"left\":10,\"right\":10,\"leftText\":\"one\"}," +
                "{\"kind\":\"chg\",\"left\":11,\"right\":11,\"leftText\":\"two\",\"rightText\":\"TWO\"}," +
                "{\"kind\":\"del\",\"left\":12,\"leftText\":\"gone\"}," +
                "{\"kind\":\"add\",\"right\":12,\"rightText\":\"h\\u00E9\"}," +
                "{\"kind\":\"gap\"}]}"), "Assets/a.txt");

            Assert.IsTrue(diff.IsText);
            Assert.AreEqual("Assets/a.txt", diff.path);
            Assert.AreEqual(12, diff.baseRevision);
            Assert.AreEqual("..\\b.txt", diff.movedFrom);
            Assert.AreEqual(2, diff.added);
            Assert.AreEqual(1, diff.removed);
            Assert.AreEqual(6, diff.rows.Length);
            Assert.AreEqual(DiffRowKind.Gap, diff.rows[0].kind);
            Assert.AreEqual(9, diff.rows[0].skipped);
            Assert.AreEqual(DiffRowKind.Same, diff.rows[1].kind);
            Assert.AreEqual("one", diff.rows[1].leftText);
            Assert.AreEqual("one", diff.rows[1].rightText, "a same row carries its text once; both sides show it");
            Assert.AreEqual(DiffRowKind.Chg, diff.rows[2].kind);
            Assert.AreEqual("TWO", diff.rows[2].rightText);
            Assert.AreEqual(DiffRowKind.Del, diff.rows[3].kind);
            Assert.AreEqual(12, diff.rows[3].left);
            Assert.AreEqual(DiffRowKind.Add, diff.rows[4].kind);
            Assert.AreEqual("h\u00e9", diff.rows[4].rightText);
            Assert.AreEqual(0, diff.rows[5].skipped, "a final gap without a count");
        }

        [Test]
        public void Parse_ExpandsTabsForDisplay()
        {
            var diff = WorkingDiffParser.Parse(Reply(
                "{\"state\":\"text\",\"hasBase\":false,\"hasWorking\":true,\"added\":1,\"removed\":0,\"truncated\":false," +
                "\"rows\":[{\"kind\":\"add\",\"right\":1,\"rightText\":\"\\tx\"}]}"), "a");
            Assert.AreEqual("    x", diff.rows[0].rightText);
        }

        [TestCase("unchanged", "lineEndingsOnly")]
        [TestCase("unchanged", "propertiesOnly")]
        [TestCase("unsupported", "conflict")]
        public void Parse_NonTextStates_HaveReasonAndNoRows(string state, string reason)
        {
            var diff = WorkingDiffParser.Parse(Reply(
                "{\"state\":\"" + state + "\",\"reason\":\"" + reason + "\",\"hasBase\":true,\"hasWorking\":true," +
                "\"added\":0,\"removed\":0,\"truncated\":false,\"rows\":[]}"), "a");
            Assert.IsFalse(diff.IsText);
            Assert.AreEqual(state, diff.state);
            Assert.AreEqual(reason, diff.reason);
            Assert.AreEqual(0, diff.rows.Length);
        }

        [Test]
        public void Parse_TooLarge_CarriesTheLimit()
        {
            var diff = WorkingDiffParser.Parse(Reply(
                "{\"state\":\"tooLarge\",\"hasBase\":true,\"hasWorking\":true,\"added\":0,\"removed\":0,\"truncated\":false," +
                "\"limitBytes\":33554432,\"rows\":[]}"), "a");
            Assert.AreEqual(33554432L, diff.limitBytes);
            Assert.IsNull(diff.reason);
        }

        [Test]
        public void Parse_ReplyWithoutState_Throws()
        {
            Assert.Throws<FormatException>(() => WorkingDiffParser.Parse("{\"v\":1,\"id\":\"u1\",\"ok\":true,\"result\":{}}", "a"));
        }

        [Test]
        public void ErrorSentence_PairsADescriptionWithTheCoreAction()
        {
            var withAction = new IpcRawResult { ErrorKind = "timeout", ErrorMessage = "raw", ErrorDefaultAction = "Open it in an external compare tool." };
            Assert.AreEqual("Could not compare this file (timeout). Open it in an external compare tool.",
                WorkingDiffService.ErrorSentence(withAction, "Could not compare this file"));

            var bare = new IpcRawResult { ErrorMessage = "raw" };
            StringAssert.EndsWith(". Refresh, then click the file again.", WorkingDiffService.ErrorSentence(bare, "Could not compare this file"));
        }

        [Test]
        public void CoreWcLocked_IsBusyNotAnError()
        {
            Assert.IsTrue(WorkingDiffService.IsBusyKind(new IpcRawResult { Ok = false, ErrorKind = "wc_locked" }));
            Assert.IsFalse(WorkingDiffService.IsBusyKind(new IpcRawResult { Ok = false, ErrorKind = "timeout" }));
            Assert.IsFalse(WorkingDiffService.IsBusyKind(new IpcRawResult { Ok = true }));
        }

        [Test]
        public void DiffOps_AreReadOnly_AndGetTheLongerTimer()
        {
            Assert.AreEqual(IpcOpClass.ReadOnly, IpcOpPolicy.Classify("diff.working"));
            Assert.AreEqual(IpcOpClass.ReadOnly, IpcOpPolicy.Classify("diff.exportBase"));
            Assert.AreEqual(IpcOpPolicy.DiffWorkingTimeoutMs, IpcOpPolicy.ReadOnlyTimeoutFor("diff.working"));
            Assert.AreEqual(IpcOpPolicy.ReadOnlyTimeoutMs, IpcOpPolicy.ReadOnlyTimeoutFor("status.allEntries"));
            Assert.Greater(IpcOpPolicy.DiffWorkingTimeoutMs, 20000, "must outlast the core's 20 s diff.working deadline");
            Assert.Greater(IpcOpPolicy.ReadOnlyTimeoutFor("diff.exportBase"), 90000, "must outlast the core's 30 s mutex wait plus 60 s svn cat");
        }

        [Test]
        public void BuildArguments_TortoiseMergeTakesLabels_OthersTakeTwoPaths()
        {
            Assert.AreEqual("/base:\"C:\\t\\a b.txt\" /mine:\"C:\\p\\a.txt\" /basename:\"r5\" /minename:\"your file\"",
                DiffToolLocator.BuildArguments("C:\\Program Files\\TortoiseSVN\\bin\\TortoiseMerge.exe", "C:\\t\\a b.txt", "C:\\p\\a.txt", "r5", "your file"));
            Assert.AreEqual("\"C:\\t\\a b.txt\" \"C:\\p\\a.txt\"",
                DiffToolLocator.BuildArguments("C:\\Program Files\\Beyond Compare 5\\BCompare.exe", "C:\\t\\a b.txt", "C:\\p\\a.txt", "r5", "your file"));
        }
    }
}
