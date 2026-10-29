using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    public class SvnPropertiesModelTests
    {
        private static readonly string CR = ((char)13).ToString();
        private static readonly string LF = ((char)10).ToString();

        private sealed class FakeOps : IPropertyOps
        {
            public Dictionary<string, string> Props = new Dictionary<string, string>();
            public string FailOn;
            public List<string> Calls = new List<string>();

            public Task<PropListResult> List(string path) =>
                Task.FromResult(new PropListResult { success = true, props = new Dictionary<string, string>(Props) });

            public Task<SvnResult> Set(string path, string name, string value)
            {
                Calls.Add("set " + name);
                if (name == FailOn) return Task.FromResult(new SvnResult { success = false, error = "boom — Choose a different file or folder." });
                Props[name] = value;
                return Task.FromResult(new SvnResult { success = true });
            }

            public Task<SvnResult> Delete(string path, string name)
            {
                Calls.Add("delete " + name);
                if (name == FailOn) return Task.FromResult(new SvnResult { success = false, error = "boom" });
                Props.Remove(name);
                return Task.FromResult(new SvnResult { success = true });
            }
        }

        [Test]
        public void Plan_NoChange_IsEmpty()
        {
            Assert.IsEmpty(SvnPropertiesModel.Plan(false, true, true, "", ""));
            Assert.IsEmpty(SvnPropertiesModel.Plan(true, false, false, "a" + LF + "b" + LF, "a" + CR + LF + "b"));
        }

        [Test]
        public void Plan_NeedsLockOn_SetsStar_Off_Deletes()
        {
            var on = SvnPropertiesModel.Plan(false, false, true, "", "").Single();
            Assert.AreEqual(SvnPropertiesModel.NeedsLock, on.Name);
            Assert.IsFalse(on.Delete);
            Assert.AreEqual("*", on.Value);
            Assert.IsTrue(SvnPropertiesModel.Plan(false, true, false, "", "").Single().Delete);
        }

        [Test]
        public void Plan_Ignore_IsNeverPlannedForAFile()
        {
            Assert.IsEmpty(SvnPropertiesModel.Plan(false, false, false, "", "Library"));
        }

        [Test]
        public void Plan_IgnoreChanged_SetsLfNormalisedWithOneTrailingNewline()
        {
            var c = SvnPropertiesModel.Plan(true, false, false, "", "Library" + CR + LF + "Temp" + CR + LF + CR + LF).Single();
            Assert.AreEqual(SvnPropertiesModel.Ignore, c.Name);
            Assert.AreEqual("Library" + LF + "Temp" + LF, c.Value);
        }

        [Test]
        public void Plan_IgnoreBlanked_Deletes_ButBlankToBlankIsNothing()
        {
            Assert.IsTrue(SvnPropertiesModel.Plan(true, false, false, "Library" + LF, "   " + LF).Single().Delete);
            Assert.IsEmpty(SvnPropertiesModel.Plan(true, false, false, "", "  "));
        }

        [Test]
        public async Task Load_File_HidesNeedsLock_ShowsIgnoreAsOther()
        {
            var ops = new FakeOps { Props = { [SvnPropertiesModel.NeedsLock] = "*", [SvnPropertiesModel.Ignore] = "x", ["svn:mime-type"] = "image/png" } };
            var m = new SvnPropertiesModel("Assets/a.png", false);
            await m.LoadAsync(ops);
            Assert.IsTrue(m.Loaded);
            Assert.IsTrue(m.NeedsLockCurrent);
            CollectionAssert.AreEqual(new[] { SvnPropertiesModel.Ignore, "svn:mime-type" }, m.OtherProperties.Select(k => k.Key));
            Assert.IsFalse(m.HasPendingChanges);
        }

        [Test]
        public async Task Load_Folder_HidesIgnore_ShowsAStrayNeedsLockAsOther()
        {
            var ops = new FakeOps { Props = { [SvnPropertiesModel.NeedsLock] = "*", [SvnPropertiesModel.Ignore] = "Temp" + LF } };
            var m = new SvnPropertiesModel("Assets/Folder", true);
            await m.LoadAsync(ops);
            Assert.IsFalse(m.NeedsLockCurrent, "folders have no needs-lock toggle");
            Assert.AreEqual("Temp" + LF, m.IgnoreCurrent);
            CollectionAssert.AreEqual(new[] { SvnPropertiesModel.NeedsLock }, m.OtherProperties.Select(k => k.Key));
            Assert.IsEmpty(SvnPropertiesModel.Plan(true, false, true, "", ""), "needs-lock is never planned for a folder");
        }

        [Test]
        public async Task Apply_FailingDelete_StopsAndReportsIt()
        {
            var ops = new FakeOps { Props = { [SvnPropertiesModel.NeedsLock] = "*" }, FailOn = SvnPropertiesModel.NeedsLock };
            var m = new SvnPropertiesModel("Assets/a.png", false);
            await m.LoadAsync(ops);
            m.NeedsLockDesired = false;
            var r = await m.ApplyAsync(ops);
            Assert.IsFalse(r.Success);
            Assert.IsTrue(r.Failed.Value.Delete);
            Assert.IsEmpty(r.Applied);
            Assert.IsTrue(m.NeedsLockCurrent, "nothing landed, so the reload still shows it set");
        }

        [Test]
        public async Task Write_StopsAtTheFirstFailure_AndReportsWhatLanded()
        {
            var ops = new FakeOps { FailOn = "frg:second" };
            var plan = new List<PropChange>
            {
                new PropChange { Name = "frg:first", Value = "1" },
                new PropChange { Name = "frg:second", Value = "2" },
                new PropChange { Name = "frg:third", Value = "3" },
            };
            var r = await SvnPropertiesModel.WriteAsync(ops, "Assets/a.png", plan);
            Assert.IsFalse(r.Success);
            Assert.AreEqual("frg:second", r.Failed.Value.Name);
            CollectionAssert.AreEqual(new[] { "frg:first" }, r.Applied.Select(a => a.Name));
            CollectionAssert.AreEqual(new[] { "set frg:first", "set frg:second" }, ops.Calls, "nothing after the failure is attempted");
            StringAssert.Contains("Choose a different file or folder.", r.Error);
        }

        [Test]
        public async Task Apply_FailingWrite_ReloadsWhatActuallyLanded()
        {
            var ops = new FakeOps { FailOn = SvnPropertiesModel.Ignore };
            var m = new SvnPropertiesModel("Assets/Folder", true);
            await m.LoadAsync(ops);
            m.IgnoreDesired = "Temp";
            var r = await m.ApplyAsync(ops);
            Assert.IsFalse(r.Success);
            Assert.AreEqual("", m.IgnoreCurrent, "reload shows the failed write did not land");
            Assert.AreEqual("", m.IgnoreDesired, "the reload resets the edit to what is stored");
        }

        [Test]
        public async Task Apply_Success_LeavesNoPendingChanges()
        {
            var ops = new FakeOps();
            var m = new SvnPropertiesModel("Assets/Folder", true);
            await m.LoadAsync(ops);
            m.IgnoreDesired = "Library" + CR + LF + "Temp";
            var r = await m.ApplyAsync(ops);
            Assert.IsTrue(r.Success);
            Assert.AreEqual("Library" + LF + "Temp" + LF, ops.Props[SvnPropertiesModel.Ignore]);
            Assert.IsFalse(m.HasPendingChanges);
        }

        [Test]
        public async Task LoadFailure_IsReported_AndClearsTheOtherList()
        {
            var ok = new FakeOps { Props = { ["frg:notes"] = "x" } };
            var m = new SvnPropertiesModel("Assets/a.png", false);
            await m.LoadAsync(ok);
            Assert.AreEqual(1, m.OtherProperties.Count);
            await m.LoadAsync(new FailingListOps());
            Assert.IsFalse(m.Loaded);
            Assert.AreEqual(FailingListOps.Error, m.LoadError);
            Assert.IsEmpty(m.OtherProperties);
        }

        [Test]
        public void Publish_NullResult_IsAFailureWithAnActionSentence()
        {
            var m = new SvnPropertiesModel("Assets/a.png", false);
            m.Publish(null);
            Assert.IsFalse(m.Loaded);
            StringAssert.EndsWith("to try again.", m.LoadError);
        }

        private sealed class FailingListOps : IPropertyOps
        {
            public const string Error = "busy — A commit is in progress. Wait for it to finish, then retry.";
            public Task<PropListResult> List(string path) => Task.FromResult(new PropListResult { success = false, error = Error });
            public Task<SvnResult> Set(string path, string name, string value) => Task.FromResult(new SvnResult());
            public Task<SvnResult> Delete(string path, string name) => Task.FromResult(new SvnResult());
        }

        [Test]
        public void Preview_ShowsTheFirstLineCapped()
        {
            Assert.AreEqual("one …", SvnPropertiesModel.Preview("one" + LF + "two"));
            Assert.AreEqual("short", SvnPropertiesModel.Preview("short"));
            var longValue = new string('x', 100);
            Assert.AreEqual(new string('x', SvnPropertiesModel.OtherValuePreviewLength) + " …", SvnPropertiesModel.Preview(longValue));
        }
    }
}
