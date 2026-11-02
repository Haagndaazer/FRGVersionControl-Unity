using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using FRGVersionControl.Core.BugReports;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.Services;
using FRGVersionControl.Tools;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// Report a Bug (0.2.95): report layout, size caps, code fences, password scrubbing, the op-log
    /// tail, the console capture, and collecting details from svn-core.
    /// </summary>
    public class BugReportTests
    {
        private static BugReport.Section S(string heading, string body, bool preformatted = false)
            => new BugReport.Section(heading, heading, body, preformatted);

        private static string Lines(int count, int width = 80)
            => string.Join("\n", Enumerable.Range(1, count).Select(i => i.ToString("D6") + " " + new string('x', width)));

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "frg-bug-report-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Test]
        public void Build_PutsSectionsInOrder_AndLeavesOutEmptyOnes()
        {
            var text = BugReport.Build(new List<BugReport.Section>
            {
                new BugReport.Section("Title", null, BugReport.Header("Lock  fails\non save", new DateTime(2026, 11, 2, 19, 40, 0, DateTimeKind.Utc), "mateo@example.com", "the Setup Wizard"), false),
                S("What happened", "It failed.\r\nTwice."),
                S("What I expected", "   "),
                S("Recent version control activity", "line 1\nline 2\n", true),
            });
            Assert.AreEqual(
                "# Lock fails on save\n\nReported 2026-11-02 19:40 UTC by mateo@example.com, from the Setup Wizard.\n\n" +
                "## What happened\n\nIt failed.\nTwice.\n\n" +
                "## Recent version control activity\n\n```\nline 1\nline 2\n```\n",
                text);
        }

        [Test]
        public void Build_ClosesACodeFenceTheReporterLeftOpen()
        {
            var text = BugReport.Build(new List<BugReport.Section>
            {
                S("What happened", "It printed:\n```\nNullReferenceException"),
                S("Steps to reproduce", "~~~~\nstep\n~~~~\nthen ```csharp\n```inline```"),
                S("Recent version control activity", "line", true),
            });
            StringAssert.Contains("```\nNullReferenceException\n```\n\n## Steps to reproduce", text);
            StringAssert.Contains("~~~~\nstep\n~~~~\nthen ```csharp\n```inline```\n\n## Recent", text);
            Assert.AreEqual("a\n````\nb\n````", BugReport.CloseOpenFence("a\n````\nb"));
            Assert.AreEqual("x\n```\ny\n```", BugReport.CloseOpenFence("x\n```\ny\n```"));
        }

        [Test]
        public void Header_TitleIsOneLine_AndCapped()
        {
            var header = BugReport.Header(new string('t', 500), DateTime.UtcNow, "u", "the menu");
            var firstLine = header.Split('\n')[0];
            Assert.AreEqual("# " + new string('t', BugReport.TitleMax), firstLine);
        }

        [Test]
        public void Fence_IsLongerThanAnyBacktickRunInside()
        {
            Assert.AreEqual("```\na\n```", BugReport.Fence("a"));
            Assert.AreEqual("````\nx ``` y\n````", BugReport.Fence("x ``` y"));
            Assert.AreEqual("`````\n````\n`````", BugReport.Fence("````"));
            Assert.AreEqual("```\n```", BugReport.Fence(""));
            Assert.AreEqual("```\n~~~\nz\n```", BugReport.Fence("~~~\nz"));
        }

        [Test]
        public void TailLines_KeepsTheNewestLines_AndCountsTheRest()
        {
            var text = string.Join("\n", Enumerable.Range(1, 100).Select(i => "line " + i));
            var tail = BugReport.TailLines(text, 60, BugReport.SectionMax);
            StringAssert.StartsWith("... 40 earlier lines omitted\nline 41\n", tail);
            StringAssert.EndsWith("\nline 100", tail);
            Assert.AreEqual("a\nb", BugReport.TailLines("a\r\nb\r\n", 60, BugReport.SectionMax));
            Assert.AreEqual("", BugReport.TailLines(null, 60, BugReport.SectionMax));
        }

        [Test]
        public void TailLines_RespectsTheCharacterCap()
        {
            var tail = BugReport.TailLines(Lines(5000), int.MaxValue, BugReport.SectionMax);
            Assert.LessOrEqual(tail.Length, BugReport.SectionMax);
            StringAssert.StartsWith("... ", tail);
            StringAssert.EndsWith(Lines(5000).Split('\n').Last(), tail);
        }

        [Test]
        public void WorstCaseReport_StaysUnderTheTarget_AndNeverCutsTheReportersText()
        {
            var big = new string('a', BugReport.DescriptionMax);
            var sections = new List<BugReport.Section>
            {
                new BugReport.Section("Title", null, BugReport.Header(new string('t', 500), DateTime.UtcNow, "someone@example.com", "the Setup Wizard"), false),
                S("What happened", big),
                S("What I expected", big),
                S("Steps to reproduce", big),
                S("Versions and project", Lines(40, 90)),
                S("Setup Wizard results", BugReport.TailLines(Lines(20000), int.MaxValue, BugReport.WizardMax), true),
                S("Recent version control activity", BugReport.TailLines(Lines(20000), BugReport.OpLogLines, BugReport.SectionMax), true),
                S("Recent FRG Version Control console messages", BugReport.TailLines(Lines(20000), int.MaxValue, BugReport.SectionMax), true),
            };
            var text = BugReport.Build(sections);
            Assert.LessOrEqual(text.Length, BugReport.ReportTarget);
            Assert.AreEqual(3, CountOf(text, big));
        }

        private static int CountOf(string text, string part)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
            return n;
        }

        [TestCase("svn co https://mateo:hunter2@svn.frgdev.net/svn/x", "svn co https://mateo:***@svn.frgdev.net/svn/x")]
        [TestCase("https://mateo@svn.frgdev.net/svn/x", "https://mateo@svn.frgdev.net/svn/x")]
        [TestCase("https://svn.frgdev.net:443/svn/visasoccer/icon@2x.png", "https://svn.frgdev.net:443/svn/visasoccer/icon@2x.png")]
        [TestCase("svn info --password hunter2 --username m", "svn info --password *** --username m")]
        [TestCase("svn info --password=hunter2", "svn info --password ***")]
        [TestCase("svn info --password \"two words\" -q", "svn info --password *** -q")]
        [TestCase("svn info --password-from-stdin --non-interactive", "svn info --password-from-stdin --non-interactive")]
        [TestCase("{\"username\":\"m\",\"password\":\"hunter2\"}", "{\"username\":\"m\",\"password\":\"***\"}")]
        [TestCase("Password: hunter2", "Password: ***")]
        [TestCase("pwd=abc&user=m", "pwd=***&user=m")]
        [TestCase("usesPasswordCache=true", "usesPasswordCache=true")]
        [TestCase("password saved for this project: yes", "password saved for this project: yes")]
        [TestCase("login saved for this project: yes", "login saved for this project: yes")]
        [TestCase("C:\\Users\\someone\\AppData\\Roaming\\Subversion\\auth", "C:\\Users\\someone\\AppData\\Roaming\\Subversion\\auth")]
        [TestCase("{\\\"username\\\":\\\"m\\\",\\\"password\\\":\\\"hunter2\\\"}", "{\\\"username\\\":\\\"m\\\",\\\"password\\\":\\\"***\\\"}")]
        [TestCase("newPassword=abc next", "newPassword=*** next")]
        [TestCase("{\"svnPassword\": \"x y\"}", "{\"svnPassword\": \"***\"}")]
        [TestCase("https://u:p@ss@svn.frgdev.net/svn/x", "https://u:***@svn.frgdev.net/svn/x")]
        [TestCase("Passwords are never included.", "Passwords are never included.")]
        public void Scrub_MasksPasswordLikeValues_AndIsStable(string input, string expected)
        {
            Assert.AreEqual(expected, BugReport.Scrub(input));
            Assert.AreEqual(expected, BugReport.Scrub(expected));
        }

        [Test]
        public void ScrubSections_MasksEverySection_AndSaysSo()
        {
            var sections = new List<BugReport.Section> { S("What happened", "I typed password=sentinel-7f3e"), S("Log", "fine", true) };
            Assert.IsTrue(BugReport.ScrubSections(sections));
            var text = BugReport.Build(sections);
            StringAssert.DoesNotContain("sentinel-7f3e", text);
            StringAssert.Contains("password=***", text);
            Assert.IsFalse(BugReport.ScrubSections(sections));
        }

        [Test]
        public void FileName_AndSavedMessage()
        {
            Assert.AreEqual("FRG-bug-report-20261102-074005.md", BugReport.FileName(new DateTime(2026, 11, 2, 7, 40, 5)));
            StringAssert.Contains("Paste it to " + BugReport.Recipient + " on Slack", BugReport.SavedMessage);
        }

        [Test]
        public void ReadTail_MissingFile_IsNull()
        {
            Assert.IsNull(BugReport.ReadTail(Path.Combine(TempDir(), "op-log.txt"), 1024));
        }

        [Test]
        public void ReadTail_SmallFile_IsWhole_AndAWindowStartsAtAWholeLine()
        {
            var path = Path.Combine(TempDir(), "op-log.txt");
            File.WriteAllText(path, string.Join("", Enumerable.Range(0, 100).Select(i => $"line {i:D3}\n")));
            Assert.AreEqual(900, BugReport.ReadTail(path, 64 * 1024).Length);
            var tail = BugReport.ReadTail(path, 50);
            StringAssert.StartsWith("line ", tail);
            StringAssert.EndsWith("line 099\n", tail);
            Assert.IsTrue(tail.Split('\n').Where(l => l.Length > 0).All(l => l.Length == 8), tail);
        }

        [Test]
        public void ReadTail_AWindowThatStartsExactlyAtALine_KeepsThatLine_AndOneLongLineKeepsItsEnd()
        {
            var path = Path.Combine(TempDir(), "op-log.txt");
            File.WriteAllText(path, string.Join("", Enumerable.Range(0, 100).Select(i => $"line {i:D3}\n")));
            StringAssert.StartsWith("line 094\n", BugReport.ReadTail(path, 54));

            var longLine = Path.Combine(TempDir(), "op-log.txt");
            File.WriteAllText(longLine, new string('x', 200) + "END");
            Assert.AreEqual(new string('x', 47) + "END", BugReport.ReadTail(longLine, 50));
        }

        [Test]
        public void ReadTail_WhileAnotherHandleIsAppending_ReadsThePartialLastLine()
        {
            var path = Path.Combine(TempDir(), "op-log.txt");
            File.WriteAllText(path, "first line\n");
            using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                var bytes = Encoding.UTF8.GetBytes("half a li");
                writer.Write(bytes, 0, bytes.Length);
                writer.Flush();
                Assert.AreEqual("first line\nhalf a li", BugReport.ReadTail(path, 1024));
            }
        }

        [Test]
        public void ReadOpLog_MissingFile_SaysSo()
        {
            Assert.AreEqual("No version control activity recorded yet.", BugReportDiagnostics.ReadOpLog(TempDir()));
        }

        [Test]
        public void Capture_KeepsThePluginsTaggedMessages()
        {
            var b = new ConsoleCaptureBuffer();
            b.Record("[FRGVersionControl] a", "", LogType.Log, DateTime.UtcNow);
            b.Record("[FrgVersionControl] b", "", LogType.Warning, DateTime.UtcNow);
            b.Record("[FRGVersionControl.Shelves] c", "", LogType.Log, DateTime.UtcNow);
            b.Record("[SvnStatusCache] d", "", LogType.Log, DateTime.UtcNow);
            b.Record("[PendingChanges] e", null, LogType.Error, DateTime.UtcNow);
            b.Record("[FRGGame] a game's own message", "", LogType.Log, DateTime.UtcNow);
            var entries = b.Snapshot(out var other);
            CollectionAssert.AreEqual(new[] { "a", "b", "c", "d", "e" }, entries.Select(e => e.message.Split(' ').Last()).ToArray());
            Assert.AreEqual(0, other);
        }

        [Test]
        public void Capture_KeepsUntaggedMessagesFromPluginCode_WithoutUnityDebugFrames()
        {
            var b = new ConsoleCaptureBuffer();
            b.Record("NullReferenceException: Object reference not set",
                     "UnityEngine.Debug:LogException (System.Exception)\nFRGVersionControl.UI.FrgVersionControlWindow:OnGUI () (at Editor/UI/FrgVersionControlWindow.cs:600)\nUnityEditor.HostView:OnGUI ()",
                     LogType.Exception, DateTime.UtcNow);
            var e = b.Snapshot(out _).Single();
            StringAssert.StartsWith("FRGVersionControl.UI.FrgVersionControlWindow:OnGUI", e.stack);
            StringAssert.DoesNotContain("UnityEngine.Debug", e.stack);
            Assert.AreEqual("Exception", e.type);
        }

        [Test]
        public void Capture_DropsOtherMessages_AndCountsOtherErrors()
        {
            var b = new ConsoleCaptureBuffer();
            b.Record("Shader warning in 'Water'", "Game.Water:Awake ()", LogType.Warning, DateTime.UtcNow);
            b.Record("Player is null", "Game.Player:Update ()", LogType.Error, DateTime.UtcNow);
            Assert.AreEqual(0, b.Snapshot(out var other).Count);
            Assert.AreEqual(1, other);
            var text = b.Format();
            StringAssert.Contains("No FRG Version Control messages", text);
            StringAssert.Contains("(1 other error from other code is not included.)", text);
            StringAssert.DoesNotContain("Player is null", text);
        }

        [Test]
        public void Capture_KeepsTheNewest100_AndCapsMessagesAndStacks()
        {
            var b = new ConsoleCaptureBuffer();
            var frames = string.Join("\n", Enumerable.Range(0, 20).Select(i => "FRGVersionControl.X:M" + i + " ()"));
            for (int i = 0; i < 130; i++) b.Record("[FRGVersionControl] " + i + " " + new string('m', 3000), frames, LogType.Log, DateTime.UtcNow);
            var entries = b.Snapshot(out _);
            Assert.AreEqual(ConsoleCaptureBuffer.Capacity, entries.Count);
            StringAssert.StartsWith("[FRGVersionControl] 30 ", entries[0].message);
            StringAssert.StartsWith("[FRGVersionControl] 129 ", entries.Last().message);
            Assert.AreEqual(ConsoleCaptureBuffer.MessageMax, entries[0].message.Length);
            Assert.AreEqual(ConsoleCaptureBuffer.StackLinesMax, entries[0].stack.Split('\n').Length);
        }

        [Test]
        public void Capture_SurvivesARoundTrip_AndACorruptValueStartsFresh()
        {
            var b = new ConsoleCaptureBuffer();
            b.Record("[FRGVersionControl] one", "", LogType.Log, DateTime.UtcNow);
            b.Record("[FRGVersionControl] two", "FRGVersionControl.X:Y ()", LogType.Warning, DateTime.UtcNow);
            b.Record("Other failure", "Game.Z:W ()", LogType.Error, DateTime.UtcNow);
            var restored = new ConsoleCaptureBuffer();
            restored.Restore(b.Serialize());
            var entries = restored.Snapshot(out var other);
            CollectionAssert.AreEqual(new[] { "[FRGVersionControl] one", "[FRGVersionControl] two" }, entries.Select(e => e.message).ToArray());
            Assert.AreEqual("FRGVersionControl.X:Y ()", entries[1].stack);
            Assert.AreEqual(1, other);

            var fresh = new ConsoleCaptureBuffer();
            Assert.DoesNotThrow(() => fresh.Restore("{not json"));
            Assert.DoesNotThrow(() => fresh.Restore(""));
            Assert.AreEqual(0, fresh.Snapshot(out _).Count);
        }

        [UnityTest]
        public IEnumerator Collect_WhileSvnCoreIsBusy_StopsBeforeTheFirstRequest()
        {
            var facts = BugReportDiagnostics.ReadEditorFacts();
            if (!facts.coreAvailable)
            {
                Assert.Ignore("svn-core.exe is not available in this project");
                yield break;
            }
            var task = Task.Run(() => BugReportDiagnostics.ReadCoreFactsAsync(facts, () => true));
            while (!task.IsCompleted) yield return null;
            var core = task.Result;
            Assert.AreEqual(1, core.notes.Count, string.Join(" | ", core.notes));
            StringAssert.StartsWith("svn-core was busy running ", core.notes[0]);
            var details = BugReportDiagnostics.FormatDetails(facts, core);
            StringAssert.Contains("- svn unknown at (unknown)", details);
            StringAssert.Contains("- Working-copy revision: not available", details);
        }

        [UnityTest]
        public IEnumerator Collect_AgainstTheScratchWorkingCopy_FillsTheDetails()
        {
            if (!ScratchWcGuard.IsScratch(out var reason))
            {
                Assert.Ignore("scratch guard: " + reason);
                yield break;
            }
            while (IpcSvnClient.IsExclusiveOpInFlight) yield return null;
            var facts = BugReportDiagnostics.ReadEditorFacts();
            var task = Task.Run(() => BugReportDiagnostics.ReadCoreFactsAsync(facts));
            while (!task.IsCompleted) yield return null;
            var core = task.Result;
            CollectionAssert.IsEmpty(core.notes, string.Join(" | ", core.notes));
            Assert.IsTrue(File.Exists(core.svnPath), core.svnPath);
            Assert.IsFalse(string.IsNullOrEmpty(core.revision));
            Assert.IsFalse(string.IsNullOrEmpty(facts.svnUser));
            var details = BugReportDiagnostics.FormatDetails(facts, core);
            StringAssert.Contains($"- svn {core.svnVersion} at {core.svnPath}", details);
            StringAssert.Contains("- Working-copy root: ", details);
            StringAssert.Contains("- Signed in as: " + facts.svnUser, details);
            StringAssert.DoesNotContain("svn.simple", details);
        }

        [UnityTest]
        public IEnumerator SignInPassword_NeverReachesTheReport()
        {
            if (!ScratchWcGuard.IsScratch(out var reason))
            {
                Assert.Ignore("scratch guard: " + reason);
                yield break;
            }
            const string sentinel = "Sentinel-Pw-5c9d1a";
            while (IpcSvnClient.IsExclusiveOpInFlight) yield return null;
            var before = SvnLogin.FetchStatusAsync();
            while (!before.IsCompleted) yield return null;
            var signIn = SvnLogin.SignInAsync("frg-report-probe", sentinel);
            while (!signIn.IsCompleted) yield return null;
            Assert.IsFalse(signIn.Result.signedIn, "a file: repository saves no login");
            var after = SvnLogin.FetchStatusAsync();
            while (!after.IsCompleted) yield return null;
            Assert.AreEqual(before.Result.status?.ProjectUsername, after.Result.status?.ProjectUsername, "the probe sign-in must not change the project login");

            var facts = BugReportDiagnostics.ReadEditorFacts();
            var task = Task.Run(() => BugReportDiagnostics.ReadCoreFactsAsync(facts));
            while (!task.IsCompleted) yield return null;
            var text = BugReport.Build(new List<BugReport.Section>
            {
                new BugReport.Section("Title", null, BugReport.Header("probe", DateTime.UtcNow, facts.svnUser, "a test"), false),
                S("Versions and project", BugReportDiagnostics.FormatDetails(facts, task.Result)),
                S("Recent version control activity", BugReportDiagnostics.ReadOpLog(facts.toolingDir), true),
                S("Recent FRG Version Control console messages", FrgConsoleCapture.Buffer.Format(), true),
            });
            StringAssert.DoesNotContain(sentinel, text);
            StringAssert.DoesNotContain(sentinel, FrgConsoleCapture.Buffer.Serialize());
        }
    }
}
