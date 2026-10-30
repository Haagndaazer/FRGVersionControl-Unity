using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Changelists;
using FRGVersionControl.Core.OverrideMode;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// The override workflow and declared changelist names end to end through svn-core (0.2.92:
    /// the editor no longer writes either sidecar itself). Runs only inside the guarded scratch
    /// working copy (see ScratchWcGuard).
    /// </summary>
    public class OverrideAndChangelistIpcTests
    {
        [UnityTest]
        public IEnumerator Override_BeginThroughCore_ClearAndForgetThroughTheManager()
        {
            if (!ScratchWcGuard.IsScratch(out var reason))
            {
                Assert.Ignore("scratch guard: " + reason);
                yield break;
            }

            var root = SvnCommandRunner.ProjectRoot;
            var folder = "Assets/override-probe-" + System.DateTime.UtcNow.Ticks.ToString("x");
            var file = folder + "/art.bin";
            Directory.CreateDirectory(Path.Combine(root, folder));
            File.WriteAllText(Path.Combine(root, file), "original");
            var full = Path.Combine(root, file);

            Assert.IsTrue(SvnCommandRunner.Add(new[] { folder }).success, "svn add");
            var prop = SvnCommandRunner.PropSetAsync(file, "svn:needs-lock", "*");
            while (!prop.IsCompleted) yield return null;
            Assert.IsTrue(prop.Result.success, prop.Result.error);
            var commit = SvnCommandRunner.Commit(new[] { folder, folder + ".meta", file }.Where(p => File.Exists(Path.Combine(root, p)) || Directory.Exists(Path.Combine(root, p))).ToArray(), "override probe");
            Assert.IsTrue(commit.success, commit.error);
            Assert.IsTrue(new FileInfo(full).IsReadOnly, "needs-lock file is read-only after commit");

            var mgr = FileLockOverrideManager.Instance;

            // Start the override in svn-core directly (the manager's precondition needs a lock held
            // by another user, which a single-user scratch copy cannot produce).
            var begin = SvnCommandRunner.InvokeMutating("override.begin",
                "{\"paths\":[\"" + SvnCommandRunner.IpcEscape(file) + "\"],\"lockHolder\":\"someone-else\"}", out var beginError);
            Assert.IsNull(beginError, beginError);
            Assert.IsTrue(mgr.RefreshFromCore());
            Assert.IsTrue(mgr.IsInOverrideMode(file));
            Assert.IsTrue(mgr.IsInOverrideMode(file.Replace('/', '\\').ToUpperInvariant()), "lookups ignore separators and case");
            Assert.AreEqual("someone-else", mgr.GetOverride(file).lock_holder_at_start);
            Assert.IsFalse(new FileInfo(full).IsReadOnly, "writable during the override");

            File.WriteAllText(full, "edited during override");
            Assert.IsTrue(mgr.ClearIgnoreOnCommit(file, out var clearError), clearError);
            Assert.IsFalse(mgr.IsInOverrideMode(file));
            Assert.AreEqual("original", File.ReadAllText(full));
            Assert.IsTrue(new FileInfo(full).IsReadOnly, "read-only again after the override ends");

            Assert.IsFalse(mgr.ClearIgnoreOnCommit(file, out var secondError));
            StringAssert.Contains("No active ignore-on-commit entry", secondError);

            SvnCommandRunner.InvokeMutating("override.begin", "{\"paths\":[\"" + SvnCommandRunner.IpcEscape(file) + "\"]}", out beginError);
            Assert.IsNull(beginError, beginError);
            File.WriteAllText(full, "kept edits");
            Assert.IsTrue(mgr.Forget(file, out var forgetError), forgetError);
            Assert.IsFalse(mgr.IsInOverrideMode(file));
            Assert.AreEqual("kept edits", File.ReadAllText(full), "forget never touches the file");

            var cleanup = SvnCommandRunner.RevertDetailedAsync(new[] { file }, alsoRevertMeta: false);
            while (!cleanup.IsCompleted) yield return null;
            SvnCommandRunner.RemoveFromChangelist(new[] { file });
        }

        [Test]
        public void ChangelistNames_CreateRenameDelete_ThroughTheCore()
        {
            if (!ScratchWcGuard.IsScratch(out var reason)) Assert.Ignore("scratch guard: " + reason);

            var name = "frg-probe-" + System.DateTime.UtcNow.Ticks.ToString("x");
            var renamed = name + "-renamed";
            try
            {
                Assert.IsTrue(ChangelistService.Create(name).ok);
                var again = ChangelistService.Create(name);
                Assert.IsFalse(again.ok);
                StringAssert.Contains("already exists", again.reason);
                CollectionAssert.Contains(UserChangelistStore.ReadSnapshot(), name);

                Assert.IsTrue(ChangelistService.Rename(name, renamed, sidecarOnly: true).ok);
                var names = UserChangelistStore.ReadSnapshot();
                CollectionAssert.Contains(names, renamed);
                CollectionAssert.DoesNotContain(names, name);
                Assert.AreEqual(1, names.Count(n => n == renamed), "rename never duplicates");

                Assert.IsTrue(ChangelistService.Delete(renamed, ChangelistService.DeleteMode.RemoveOnlyIfEmpty).ok);
                CollectionAssert.DoesNotContain(UserChangelistStore.ReadSnapshot(), renamed);
            }
            finally
            {
                UserChangelistStore.Remove(name);
                UserChangelistStore.Remove(renamed);
            }
        }
    }
}
