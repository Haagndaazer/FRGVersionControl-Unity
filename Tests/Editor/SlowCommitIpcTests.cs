using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using FRGVersionControl.Core;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// End-to-end pin for the r15 incident class: a commit that outlives the old 5 s persistent
    /// budget is never killed or re-sent, concurrent reads fail fast as busy, and the first read
    /// after the commit succeeds promptly. Runs ONLY inside a scratch working copy prepared by
    /// Test/LiveProbes/Setup-SlowCommitScratch.ps1 (env var FRG_SLOW_COMMIT_WC equal to the
    /// project root, a .frg-scratch-wc marker in the root, and a file:/// repository under the OS
    /// temp directory); anywhere else it is ignored, so it can never commit to a real project.
    /// </summary>
    public class SlowCommitIpcTests
    {
        private const string ProbeFile = "Assets/slow-commit-probe.txt";

        [UnityTest]
        public IEnumerator SlowCommit_IsNeverKilledOrResent_ReadsFailFast_ThenRecover()
        {
            if (!ScratchWcGuard.IsScratch(out var reason))
            {
                Assert.Ignore("scratch guard: " + reason);
                yield break;
            }

            var logs = new ConcurrentBag<string>();
            Application.LogCallback handler = (msg, stack, type) => logs.Add(msg);
            Application.logMessageReceivedThreaded += handler;
            var progressLines = new ConcurrentBag<string>();
            var refreshes = new List<(double seconds, bool busy)>();
            try
            {
                var full = Path.Combine(SvnCommandRunner.ProjectRoot, ProbeFile);
                File.AppendAllText(full, "slow commit " + DateTime.UtcNow.ToString("o") + "\n");
                long before = ScratchWcGuard.HeadRevision();
                Assert.GreaterOrEqual(before, 1, "scratch repo must have a seed revision");

                var commit = SvnCommandRunner.CommitAsync(new[] { ProbeFile }, "slow commit test", CancellationToken.None, new Progress<string>(progressLines.Add));
                var started = EditorApplication.timeSinceStartup;
                double next = started + 1.0;
                while (!commit.IsCompleted)
                {
                    if (EditorApplication.timeSinceStartup >= next && IpcSvnClient.IsExclusiveOpInFlight)
                    {
                        var sw = Stopwatch.StartNew();
                        var map = SvnCommandRunner.StatusUpstreamAll();
                        sw.Stop();
                        refreshes.Add((sw.Elapsed.TotalSeconds, map == null));
                        next += 1.0;
                    }
                    if (EditorApplication.timeSinceStartup - started > 120) Assert.Fail("commit did not complete within 120 s");
                    yield return null;
                }
                var elapsed = EditorApplication.timeSinceStartup - started;
                var result = commit.Result;

                Assert.IsTrue(result.success, "commit failed: " + result.error);
                Assert.Greater(elapsed, 5.0, "the scratch pre-commit hook must make the commit outlive the old 5 s budget");
                Assert.AreEqual(before + 1, result.revision, "exactly one new revision expected from the commit result");
                Assert.AreEqual(before + 1, ScratchWcGuard.HeadRevision(), "exactly one new revision expected in the repository");
                Assert.IsFalse(IpcSvnClient.IsExclusiveOpInFlight, "exclusive flag must clear on the terminal frame");

                Assert.IsNotEmpty(refreshes, "at least one refresh should have run while the commit was in flight");
                foreach (var r in refreshes)
                {
                    Assert.IsTrue(r.busy, "a refresh during the commit must be answered busy (null), not block");
                    Assert.Less(r.seconds, 3.0, "a busy refresh must return promptly (it never waits for the commit)");
                }

                var after = Stopwatch.StartNew();
                var mapAfter = SvnCommandRunner.StatusUpstreamAll();
                after.Stop();
                Assert.IsNotNull(mapAfter, "the first read after the commit must succeed");
                Assert.Less(after.Elapsed.TotalSeconds, 5.0, "the config-dir mutex must be released promptly after the commit");

                Assert.IsNotEmpty(progressLines, "svn's progress lines must reach the caller");
                foreach (var l in logs)
                {
                    StringAssert.DoesNotMatch("KillPipeAndOneShot|falling back to one-shot|AbandonKeepPending", l);
                }
            }
            finally
            {
                Application.logMessageReceivedThreaded -= handler;
            }
        }
    }
}
