using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Abstractions;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Hooks;
using UnityEditor;
using UnityEngine;

namespace FRGVersionControl.Tools
{
    /// <summary>
    /// One-click smoke test that exercises the svn-core IPC surface end-to-end against the
    /// current Unity project's working copy. Read-only by design — never mutates state.
    /// Reports per-op pass/fail/info plus the wrapped action sentence on any failure.
    ///
    /// Negative test: invokes `mutate.delete --keep-local` and counts the protocol-layer
    /// rejection as a PASS (the CLAUDE.md hard-rule guard MUST fire).
    /// </summary>
    public sealed class IpcSmokeTestWindow : EditorWindow
    {
        private static readonly Color s_Pass = new Color(0.36f, 0.78f, 0.36f);
        private static readonly Color s_Fail = new Color(0.90f, 0.40f, 0.40f);
        private static readonly Color s_Info = new Color(0.65f, 0.78f, 0.95f);

        private readonly List<SmokeRow> m_Rows = new List<SmokeRow>();
        private Vector2 m_Scroll;
        private bool m_Running;
        private CancellationTokenSource m_Cts;
        private string m_Summary;
        private string m_BinaryPath;

        [MenuItem("FRG Version Control/Debug/IPC Smoke Test", priority = 2002)]
        public static void Open()
        {
            var w = GetWindow<IpcSmokeTestWindow>("FRG IPC Smoke Test");
            w.minSize = new Vector2(640, 440);
            w.Show();
        }

        private void OnDisable()
        {
            m_Cts?.Cancel();
            m_Cts?.Dispose();
            m_Cts = null;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(m_Running))
                {
                    if (GUILayout.Button("Run smoke test", GUILayout.Height(32), GUILayout.Width(180)))
                    {
                        _ = RunAsync();
                    }
                }
                if (m_Running)
                {
                    EditorGUILayout.LabelField("Running…", EditorStyles.boldLabel, GUILayout.Width(80));
                    if (GUILayout.Button("Cancel", GUILayout.Width(80), GUILayout.Height(32)))
                    {
                        m_Cts?.Cancel();
                    }
                }
                GUILayout.FlexibleSpace();
            }

            if (!string.IsNullOrEmpty(m_BinaryPath))
            {
                EditorGUILayout.LabelField("svn-core.exe", m_BinaryPath, EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.ExpandHeight(true));
                if (m_Rows.Count == 0)
                {
                    EditorGUILayout.LabelField(
                        "Click \"Run smoke test\" to probe the svn-core IPC surface against this working copy.",
                        EditorStyles.miniLabel);
                }
                foreach (var row in m_Rows) DrawRow(row);
                EditorGUILayout.EndScrollView();
            }

            if (!string.IsNullOrEmpty(m_Summary))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(m_Summary, MessageType.Info);
            }
        }

        private void DrawRow(SmokeRow row)
        {
            var original = GUI.color;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.color = row.Verdict switch
                {
                    Verdict.Pass => s_Pass,
                    Verdict.Fail => s_Fail,
                    _ => s_Info,
                };
                GUILayout.Label(row.Glyph, EditorStyles.boldLabel, GUILayout.Width(20));
                GUI.color = original;
                GUILayout.Label(row.Op, EditorStyles.boldLabel, GUILayout.Width(180));
                GUILayout.Label(row.ElapsedMs.ToString() + "ms", EditorStyles.miniLabel, GUILayout.Width(60));
                EditorGUILayout.LabelField(row.Detail, EditorStyles.label);
            }
            if (!string.IsNullOrEmpty(row.Action))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(260);
                    EditorGUILayout.LabelField(row.Action, EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private async Task RunAsync()
        {
            m_Running = true;
            m_Rows.Clear();
            m_Summary = null;
            Repaint();

            var logger = new UnityLogger("FRGVersionControl.SmokeTest");
            var client = FileLockManager.Instance.IpcClient;
            m_BinaryPath = client.ResolveBinaryPath();
            if (!client.IsAvailable)
            {
                m_Rows.Add(new SmokeRow
                {
                    Op = "svn-core.exe",
                    Verdict = Verdict.Fail,
                    Glyph = "✗",
                    Detail = "binary not found",
                    Action = "Reinstall the package, or run Build-SvnCore.ps1 if developing the toolset.",
                });
                m_Running = false;
                Repaint();
                return;
            }
            m_Cts = new CancellationTokenSource();

            int pass = 0, fail = 0, info = 0;
            try
            {
                foreach (var probe in BuildProbes())
                {
                    if (m_Cts.IsCancellationRequested) break;
                    Repaint();
                    var row = await RunOne(client, probe, m_Cts.Token).ConfigureAwait(true);
                    m_Rows.Add(row);
                    switch (row.Verdict)
                    {
                        case Verdict.Pass: pass++; break;
                        case Verdict.Fail: fail++; break;
                        case Verdict.Info: info++; break;
                    }
                    Repaint();
                }
            }
            finally
            {
                m_Cts.Dispose();
                m_Cts = null;
                m_Running = false;
            }
            int total = pass + fail + info;
            m_Summary = $"Smoke test complete — {pass} pass · {fail} fail · {info} info  ({total} ops total). " +
                        "Read 'info' rows for ops that need a live upstream.";
            logger.Info(m_Summary);
            Repaint();
        }

        private static IEnumerable<Probe> BuildProbes()
        {
            // -- Local read-only ops (no upstream dependency, no state mutation) --
            yield return new Probe { Op = "meta.version", Args = "{}", Expectation = Expect.Ok,
                LabelOnPass = "core/svn/wire versions returned" };
            yield return new Probe { Op = "status.allEntries", Args = "{\"depth\":\"empty\"}", Expectation = Expect.Ok,
                LabelOnPass = "WC scan returned" };
            yield return new Probe { Op = "status.isVersioned", Args = "{\"path\":\"Assets\"}", Expectation = Expect.Ok,
                LabelOnPass = "Assets folder probe" };
            yield return new Probe { Op = "mutate.prepareDelete", Args = "{\"paths\":[\"Assets/__frg_smoke_nonexistent__\"]}", Expectation = Expect.Ok,
                LabelOnPass = "prepareDelete classified a nonexistent path (no mutation)" };
            yield return new Probe { Op = "cl.membership", Args = "{}", Expectation = Expect.Ok,
                LabelOnPass = "changelist membership returned" };
            yield return new Probe { Op = "conflict.list", Args = "{}", Expectation = Expect.Ok,
                LabelOnPass = "conflict scan completed" };
            yield return new Probe { Op = "override.list", Args = "{}", Expectation = Expect.Ok,
                LabelOnPass = "override registry read" };
            yield return new Probe { Op = "override.reconcile", Args = "{}", Expectation = Expect.Ok,
                LabelOnPass = "override reconcile completed" };
            yield return new Probe { Op = "shelf.list", Args = "{}", Expectation = Expect.Ok,
                LabelOnPass = "shelf summaries returned" };

            // -- Protocol-layer safety negative tests (MUST be rejected) --
            yield return new Probe { Op = "mutate.delete (safety guard)", InvokeAs = "mutate.delete",
                Args = "{\"paths\":[\"never-exists\"],\"keepLocal\":true}", Expectation = Expect.ProtocolReject,
                LabelOnPass = "--keep-local correctly rejected (CLAUDE.md hard rule)" };

            // -- Control ops --
            yield return new Probe { Op = "ipc.cancel (unknown id)", InvokeAs = "ipc.cancel",
                Args = "{\"targetId\":\"non-existent\"}", Expectation = Expect.OkOrInfo,
                LabelOnPass = "cancel of unknown id handled" };

            // -- Upstream-dependent ops (may surface info verdict if server is offline) --
            yield return new Probe { Op = "meta.info (upstream)", InvokeAs = "meta.info",
                Args = "{}", Expectation = Expect.OkOrInfo,
                LabelOnPass = "upstream reachable" };
            yield return new Probe { Op = "auth.bootstrap (upstream)", InvokeAs = "auth.bootstrap",
                Args = "{}", Expectation = Expect.OkOrInfo,
                LabelOnPass = "auth cache reachable" };
        }

        private static async Task<SmokeRow> RunOne(IpcSvnClient client, Probe probe, CancellationToken ct)
        {
            var ctx = client.BuildDefaultContext();
            string opName = probe.InvokeAs ?? probe.Op;
            IpcRawResult result;
            try
            {
                result = await client.InvokeRawAsync(opName, probe.Args, ctx, ct).ConfigureAwait(true);
            }
            catch (System.Exception ex)
            {
                return new SmokeRow
                {
                    Op = probe.Op,
                    Verdict = Verdict.Fail,
                    Glyph = "✗",
                    Detail = "infrastructure: " + ex.Message,
                };
            }

            return InterpretResult(probe, result);
        }

        private static SmokeRow InterpretResult(Probe probe, IpcRawResult result)
        {
            switch (probe.Expectation)
            {
                case Expect.Ok:
                    return result.Ok
                        ? new SmokeRow { Op = probe.Op, Verdict = Verdict.Pass, Glyph = "✓",
                            ElapsedMs = result.ElapsedMs, Detail = probe.LabelOnPass }
                        : new SmokeRow { Op = probe.Op, Verdict = Verdict.Fail, Glyph = "✗",
                            ElapsedMs = result.ElapsedMs,
                            Detail = result.ErrorKind + ": " + Truncate(result.ErrorMessage, 80),
                            Action = result.ErrorDefaultAction };

                case Expect.ProtocolReject:
                    bool rejectedAsExpected = !result.Ok && result.ErrorKind == "protocol";
                    return rejectedAsExpected
                        ? new SmokeRow { Op = probe.Op, Verdict = Verdict.Pass, Glyph = "✓",
                            ElapsedMs = result.ElapsedMs, Detail = probe.LabelOnPass }
                        : new SmokeRow { Op = probe.Op, Verdict = Verdict.Fail, Glyph = "✗",
                            ElapsedMs = result.ElapsedMs,
                            Detail = "guard did NOT fire — saw " + (result.Ok ? "ok=true" : (result.ErrorKind ?? "unknown error")) };

                case Expect.OkOrInfo:
                default:
                    return result.Ok
                        ? new SmokeRow { Op = probe.Op, Verdict = Verdict.Pass, Glyph = "✓",
                            ElapsedMs = result.ElapsedMs, Detail = probe.LabelOnPass }
                        : new SmokeRow { Op = probe.Op, Verdict = Verdict.Info, Glyph = "•",
                            ElapsedMs = result.ElapsedMs,
                            Detail = result.ErrorKind + ": " + Truncate(result.ErrorMessage, 80),
                            Action = result.ErrorDefaultAction };
            }
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private enum Expect { Ok, ProtocolReject, OkOrInfo }
        private enum Verdict { Pass, Fail, Info }

        private sealed class Probe
        {
            public string Op;
            public string InvokeAs;
            public string Args;
            public Expect Expectation;
            public string LabelOnPass;
        }

        private sealed class SmokeRow
        {
            public string Op;
            public Verdict Verdict;
            public string Glyph = "•";
            public long ElapsedMs;
            public string Detail;
            public string Action;
        }
    }
}
