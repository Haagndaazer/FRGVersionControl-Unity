using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    /// <summary>The Edit SVN Properties model end to end through real IPC, inside the guarded
    /// scratch working copy only (see ScratchWcGuard).</summary>
    public class PropertiesIpcTests
    {
        private static T Done<T>(Task<T> t)
        {
            if (t.IsFaulted) throw t.Exception.GetBaseException();
            return t.Result;
        }

        [UnityTest]
        public IEnumerator Model_ReadsAndWritesNeedsLockAndIgnore_ThroughTheCore()
        {
            if (!ScratchWcGuard.IsScratch(out var reason))
            {
                Assert.Ignore("scratch guard: " + reason);
                yield break;
            }

            var root = SvnCommandRunner.ProjectRoot;
            var folder = "Assets/props-probe-" + System.DateTime.UtcNow.Ticks.ToString("x");
            var file = folder + "/art.bin";
            Directory.CreateDirectory(Path.Combine(root, folder));
            File.WriteAllText(Path.Combine(root, file), "art");
            try
            {
                Assert.IsTrue(SvnCommandRunner.Add(new[] { folder }).success, "svn add");
                var ops = IpcPropertyOps.Instance;

                var fileModel = new SvnPropertiesModel(file, false);
                var load = SvnPropertiesModel.FetchAsync(ops, file);
                while (!load.IsCompleted) yield return null;
                fileModel.Publish(Done(load));
                Assert.IsTrue(fileModel.Loaded, fileModel.LoadError);
                Assert.IsFalse(fileModel.NeedsLockCurrent);

                fileModel.NeedsLockDesired = true;
                var apply = fileModel.ApplyAsync(ops);
                while (!apply.IsCompleted) yield return null;
                Assert.IsTrue(Done(apply).Success, apply.Result.Error);
                Assert.IsTrue(fileModel.NeedsLockCurrent, "reloaded after apply");

                var folderModel = new SvnPropertiesModel(folder, true);
                var loadFolder = folderModel.LoadAsync(ops);
                while (!loadFolder.IsCompleted) yield return null;
                folderModel.IgnoreDesired = "*.tmp\r\nLibrary";
                var applyIgnore = folderModel.ApplyAsync(ops);
                while (!applyIgnore.IsCompleted) yield return null;
                Assert.IsTrue(Done(applyIgnore).Success, applyIgnore.Result.Error);
                Assert.AreEqual("*.tmp\nLibrary\n", folderModel.IgnoreCurrent, "read back in the stored LF form");
                Assert.IsFalse(folderModel.HasPendingChanges);

                var wrongKind = ops.Set(folder, SvnPropertiesModel.NeedsLock, "*");
                while (!wrongKind.IsCompleted) yield return null;
                Assert.IsFalse(Done(wrongKind).success);
                StringAssert.Contains("Choose a different file or folder.", wrongKind.Result.error);

                var mergeinfo = ops.Set(file, "svn:mergeinfo", "/trunk:1");
                while (!mergeinfo.IsCompleted) yield return null;
                Assert.IsFalse(Done(mergeinfo).success);
                StringAssert.Contains("Use TortoiseSVN", mergeinfo.Result.error);

                fileModel.NeedsLockDesired = false;
                var clear = fileModel.ApplyAsync(ops);
                while (!clear.IsCompleted) yield return null;
                Assert.IsTrue(Done(clear).Success, clear.Result.Error);
                Assert.IsFalse(fileModel.NeedsLockCurrent);
            }
            finally
            {
                var cleanup = SvnCommandRunner.RevertDetailedAsync(new[] { file, folder }, alsoRevertMeta: true);
                cleanup.Wait(30000);
                Directory.Delete(Path.Combine(root, folder), true);
                var meta = Path.Combine(root, folder + ".meta");
                if (File.Exists(meta)) File.Delete(meta);
            }
        }
    }
}
