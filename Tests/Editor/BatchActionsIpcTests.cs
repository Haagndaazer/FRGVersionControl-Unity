using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using FRGVersionControl.Core;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// Pins that a multi-selection revert-and-unlock is one IPC request per verb (chunked at
    /// 100 targets), with per-path outcomes for an unchanged file and a nonexistent path.
    /// Runs only inside the guarded scratch working copy (see ScratchWcGuard).
    /// </summary>
    public class BatchActionsIpcTests
    {
        private const int FileCount = 120;
        private static readonly string Folder = "Assets/batch-probe-" + System.DateTime.UtcNow.Ticks.ToString("x");

        [UnityTest]
        public IEnumerator RevertAndUnlock_120Files_IsOneRequestPerVerb_WithPerPathOutcomes()
        {
            if (!ScratchWcGuard.IsScratch(out var reason))
            {
                Assert.Ignore("scratch guard: " + reason);
                yield break;
            }

            var root = SvnCommandRunner.ProjectRoot;
            var dir = Path.Combine(root, Folder);
            Directory.CreateDirectory(dir);
            var paths = new List<string>();
            for (int i = 0; i < FileCount; i++)
            {
                var rel = Folder + "/f" + i.ToString("D3") + ".txt";
                File.WriteAllText(Path.Combine(root, rel), "base " + i + "\n");
                paths.Add(rel);
            }

            var add = SvnCommandRunner.Add(new[] { Folder });
            Assert.IsTrue(add.success, "svn add: " + add.error);
            var client = FileLockManager.Instance.IpcClient;
            foreach (var p in paths)
            {
                var prop = client.InvokeRawAsync("prop.set", "{\"path\":\"" + p + "\",\"name\":\"svn:needs-lock\",\"value\":\"*\"}", client.BuildDefaultContext()).GetAwaiter().GetResult();
                Assert.IsTrue(prop.Ok, "prop.set: " + prop.ErrorMessage);
            }
            var commitTask = SvnCommandRunner.CommitAsync(paths.Concat(new[] { Folder }).ToArray(), "batch probe seed", System.Threading.CancellationToken.None, null);
            while (!commitTask.IsCompleted) yield return null;
            Assert.IsTrue(commitTask.Result.success, "commit: " + commitTask.Result.error);

            var lockResult = SvnCommandRunner.Lock(paths.ToArray(), "batch probe");
            Assert.IsTrue(lockResult.success, "lock: " + lockResult.error);
            Assert.IsTrue(FileLockManager.Instance.RefreshLockCache(), "lock cache refresh");

            var unchangedPath = paths[0];
            for (int i = 1; i < FileCount; i++)
            {
                var full = Path.Combine(root, paths[i]);
                new FileInfo(full).IsReadOnly = false;
                File.AppendAllText(full, "modified\n");
            }
            var missingPath = Folder + "/does-not-exist.txt";
            var selection = new List<string>(paths) { missingPath };

            UnityEditor.AssetDatabase.Refresh();
            for (int i = 0; i < 10; i++) yield return null;
            while (IpcSvnClient.IsExclusiveOpInFlight) yield return null;
            int metaCount = paths.Count(p => File.Exists(Path.Combine(root, p + ".meta")));
            int targets = selection.Count + metaCount;
            int expectedRevertChunks = (targets + 99) / 100;

            var preStatus = SvnCommandRunner.StatusMany(new[] { unchangedPath });
            bool unchangedIsClean = preStatus != null && !(preStatus.TryGetValue(unchangedPath, out var preEntry) && SvnCommandRunner.IsChangedStatus(preEntry.status));
            UnityEngine.Debug.Log($"[BatchActionsIpcTests] {unchangedPath} clean before batch: {unchangedIsClean}");

            int revertBefore = IpcSvnClient.RequestCount("mutate.revert");
            int unlockBefore = IpcSvnClient.RequestCount("lock.release");
            int histBefore = IpcSvnClient.RequestCount("hist.record");
            int statusUBefore = IpcSvnClient.RequestCount("status.allEntries");

            var sw = Stopwatch.StartNew();
            var task = Task.Run(() => FileLockManager.Instance.RevertAndUnlockFilesAsync(selection));
            while (!task.IsCompleted) yield return null;
            sw.Stop();
            var result = task.Result;
            int revertDelta = IpcSvnClient.RequestCount("mutate.revert") - revertBefore;
            int unlockDelta = IpcSvnClient.RequestCount("lock.release") - unlockBefore;
            int histDelta = IpcSvnClient.RequestCount("hist.record") - histBefore;
            int statusDelta = IpcSvnClient.RequestCount("status.allEntries") - statusUBefore;
            int statusBeforeCompletion = IpcSvnClient.RequestCount("status.allEntries");
            FileLockManager.Instance.ApplyRevertUnlockCompletion(result);
            for (int i = 0; i < 5; i++) yield return null;
            int statusDuringCompletion = IpcSvnClient.RequestCount("status.allEntries") - statusBeforeCompletion;
            UnityEngine.Debug.Log($"[BatchActionsIpcTests] requests during batch: revert={revertDelta} unlock={unlockDelta} hist={histDelta} status.allEntries={statusDelta}; during completion (reimport): status.allEntries={statusDuringCompletion}");
            UnityEngine.Debug.Log($"[BatchActionsIpcTests] outcome: reverted={result.revertedCount} released={result.releasedCount} unknown={result.unknownPaths.Count} ({result.unknownReason}) failed={result.failureCount}");
            foreach (var f in result.GetFailed().Take(5)) UnityEngine.Debug.Log($"[BatchActionsIpcTests] failed: {f.path}: {f.errorMessage}");

            Assert.AreEqual(expectedRevertChunks, revertDelta, $"{targets} targets → {expectedRevertChunks} revert chunk(s)");
            int expectedUnlockChunks = (FileCount + BatchOutcomes.ChunkSize / 2 - 1) / (BatchOutcomes.ChunkSize / 2);
            Assert.AreEqual(expectedUnlockChunks, unlockDelta, $"120 locked targets → {expectedUnlockChunks} unlock chunk(s)");
            Assert.AreEqual(1, histDelta, "one batched history record");
            Assert.LessOrEqual(statusDelta, 3, "one lock-cache refresh plus the overlay refresh during the batch (one background tick tolerated)");
            Assert.LessOrEqual(statusDuringCompletion, 4, "the reimport of 120 files must not refresh status per asset");

            Assert.GreaterOrEqual(result.revertedCount, FileCount - 1, "every modified file reverted (svn may also report the unchanged locked file as reverted when it restores its read-only bit)");
            Assert.LessOrEqual(result.revertedCount, FileCount);
            Assert.AreEqual(FileCount, result.releasedCount, "every locked file released, including the unchanged one");
            Assert.IsEmpty(result.unknownPaths);
            var failed = result.GetFailed();
            Assert.AreEqual(1, failed.Count, "only the nonexistent path fails: " + string.Join(" | ", failed.Select(f => f.path + ": " + f.errorMessage)));
            Assert.AreEqual(missingPath, failed[0].path);
            StringAssert.Contains("Skipped by svn", failed[0].errorMessage);
            Assert.IsTrue(result.results.First(r => r.path == unchangedPath).success, "unchanged file counts as success");

            var after = SvnCommandRunner.FilterChanged(paths.ToArray());
            Assert.IsNotNull(after);
            Assert.IsEmpty(after, "no modified files remain");
            foreach (var p in paths) Assert.IsFalse(FileLockManager.Instance.IsLockedByMe(p), p + " still locked");

            UnityEngine.Debug.Log($"[BatchActionsIpcTests] revert+unlock of {selection.Count} paths took {sw.ElapsedMilliseconds} ms");
        }
    }
}
