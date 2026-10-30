using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Services
{
    /// <summary>
    /// Phase 4 polish: keep Unity compiling while a code-file conflict is unresolved.
    ///
    /// When SVN merges with conflicts on a `.cs` (or `.asmdef`/`.asmref`) file, it inserts
    /// merge markers (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>, <c>=======</c>, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>)
    /// directly into the file. The C# compiler chokes on those, so Assembly-CSharp fails to
    /// compile and the entire game-side codebase becomes uncallable until the conflict is
    /// resolved. That's a big artist-UX problem.
    ///
    /// This service detects compile-affecting code-file conflicts and replaces the working
    /// file content with a compile-safe stub (just C# comments). The user's original conflict
    /// data is FULLY preserved in SVN's own sidecars — <c>.mine</c>, <c>.r&lt;base&gt;</c>,
    /// <c>.r&lt;theirs&gt;</c> — which the conflict resolver reads from directly. When the user
    /// picks Take Mine / Take Theirs, <c>svn resolve --accept mine-full</c> /
    /// <c>--accept theirs-full</c> reads from those sidecars and writes the chosen version
    /// to the working file (ignoring our stub). The stub is invisible to the resolution path.
    ///
    /// Caveats (mentioned in the stub comment itself so anyone opening the file sees them):
    /// - Don't run `svn resolve --accept working` from CLI while a file is stubbed — that
    ///   commits the stub. Use the in-Unity resolver or `--accept mine-full` / `theirs-full`.
    /// - `svn revert` reverts to BASE (pre-conflict) — works fine; the stub is gone after.
    /// - Re-running update on a stubbed file: SVN sees status C and skips; our stub stays;
    ///   resolution still works.
    /// </summary>
    [InitializeOnLoad]
    public static class FileLockConflictShadowService
    {
        // Extensions whose conflict markers break Unity compilation. .meta excluded — those
        // are YAML and break import warnings, not compile.
        private static readonly HashSet<string> s_StubbableExts = new HashSet<string>(
            new[] { ".cs", ".asmdef", ".asmref" },
            StringComparer.OrdinalIgnoreCase);

        // Paths we've stubbed during this Unity session. Cleared on domain reload — that's
        // fine; we re-detect on the next SnapshotUpdated and the idempotency check (see
        // first-line-of-file) prevents double-stubbing.
        private static readonly HashSet<string> s_StubbedPaths = new HashSet<string>();

        private const string STUB_HEADER_CS = "// FRGVersionControl-conflict-stub-v1";

        private static readonly string s_StubBodyCs =
            STUB_HEADER_CS + "\n" +
            "// ============================================================================\n" +
            "// SVN merge conflict in this file. The original conflict markers were replaced\n" +
            "// with this stub so Unity can compile the rest of the project while you resolve.\n" +
            "//\n" +
            "// Resolve via:  Window / FRG Version Control / Changes tab\n" +
            "//   (Take Mine / Take Theirs / Open externally on the conflicted row)\n" +
            "//\n" +
            "// The full original content (with <<<<<<< ======= >>>>>>> markers) and the\n" +
            "// underlying versions live in SVN's sidecars next to this file:\n" +
            "//   <file>.mine        — your local edits\n" +
            "//   <file>.r<base>     — common ancestor\n" +
            "//   <file>.r<theirs>   — upstream version\n" +
            "// The resolver reads from those sidecars, not from this file. When you pick\n" +
            "// Take Mine / Take Theirs, SVN restores the chosen version and removes the\n" +
            "// sidecars. This stub is overwritten in the process — no manual cleanup needed.\n" +
            "//\n" +
            "// DO NOT run `svn resolve --accept working` from CLI on this file while this\n" +
            "// stub is present — that would commit the stub instead of a real resolution.\n" +
            "// Use the in-Unity resolver, or `--accept mine-full` / `--accept theirs-full`.\n" +
            "// `svn revert <file>` is also safe (reverts to BASE; conflict cleared).\n" +
            "// ============================================================================\n";

        private const string STUB_HEADER_ASMDEF = "/* FRGVersionControl-conflict-stub-v1 */";

        // Asmdef must be valid JSON. Provide a JSON document Unity will accept that
        // does NOT redefine an existing assembly name (we use a unique name embedding
        // the path). Unity will warn but won't break the assembly graph.
        private static string GetStubBodyAsmdef(string assetPath)
        {
            // Stub-name must be globally unique to avoid "duplicate assembly name" errors.
            string safeName = assetPath.Replace('/', '_').Replace('\\', '_').Replace('.', '_');
            return STUB_HEADER_ASMDEF + "\n{\n" +
                   $"  \"name\": \"FRGVersionControlConflictStub_{safeName}\",\n" +
                   "  \"references\": [],\n" +
                   "  \"includePlatforms\": [],\n" +
                   "  \"excludePlatforms\": [\"Editor\"],\n" +
                   "  \"allowUnsafeCode\": false,\n" +
                   "  \"overrideReferences\": false,\n" +
                   "  \"precompiledReferences\": [],\n" +
                   "  \"autoReferenced\": false,\n" +
                   "  \"defineConstraints\": [\"FILELOCKING_CONFLICT_STUB_NEVER_DEFINED\"],\n" +
                   "  \"versionDefines\": [],\n" +
                   "  \"noEngineReferences\": true\n" +
                   "}\n";
        }

        static FileLockConflictShadowService()
        {
            SvnStatusCache.SnapshotUpdated += OnSnapshotUpdated;
            EditorApplication.focusChanged += OnFocusChanged;
            // compilationStarted fires immediately before Unity invokes the C# compiler — the
            // last opportunity to stub conflict-marker files before the compiler reads source.
            // Pre-import via OnPreprocessAsset doesn't catch this for scripts because Unity
            // reads .cs source at compile time, not at import time. Force-bypasses the debounce
            // since compile-time correctness is non-negotiable.
            UnityEditor.Compilation.CompilationPipeline.compilationStarted += _ => ScanAndStubNow(force: true);
            // Domain-load eager pass — catches the project-reopened-mid-conflict case (user
            // closed Unity with conflicts, or ran `svn update` from CLI before launch). Runs
            // BEFORE Unity's first compile pass so no script errors flash. Bypasses the
            // ScanAndStubNow debounce since this is an explicit one-shot.
            EditorApplication.delayCall += () => ScanAndStubNow(force: true);
        }

        private static void OnFocusChanged(bool gained)
        {
            // External-CLI case: user runs `svn update` in a terminal, Alt-Tabs to Unity.
            // Stub conflicted code files in the focus-gained transition before AssetDatabase
            // notices the file changes.
            if (gained) ScanAndStubNow();
        }

        private static void OnSnapshotUpdated()
        {
            // Trigger 4 piggyback (in-Unity-no-panel-open case where the wc.db poll detects
            // an external CLI update before the user re-focuses Unity). Debounced so it doesn't
            // double-run when this handler fires right after panel-driven trigger 1.
            ScanAndStubNow();

            var snapshot = SvnStatusCache.SnapshotAll();
            var currentlyConflicted = new HashSet<string>();

            foreach (var kv in snapshot)
            {
                if (kv.Value.status != SvnFileStatus.Conflict) continue;
                string ext = Path.GetExtension(kv.Key);
                if (!s_StubbableExts.Contains(ext)) continue;
                currentlyConflicted.Add(kv.Key);
            }

            bool stubbedAny = false;
            foreach (var p in currentlyConflicted)
            {
                if (s_StubbedPaths.Contains(p)) continue;
                if (!HasConflictMarkers(FullPathOf(p))) continue;
                if (TryStub(p))
                {
                    s_StubbedPaths.Add(p);
                    stubbedAny = true;
                }
            }

            // Drop tracking for files no longer in conflict — SVN restored their content via
            // the resolution path, our stub is gone, nothing to do.
            s_StubbedPaths.RemoveWhere(p => !currentlyConflicted.Contains(p));

            if (stubbedAny)
            {
                // Force script recompile so Unity picks up the stub immediately and the
                // C# error spam goes away.
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            }
        }

        // SVN's marker forms (`<<<<<<< .mine`, `<<<<<<< working`, `<<<<<<< .merge-left.r<N>`,
        // closed by `>>>>>>> .r<N>`): marker + whitespace + label, at the start of ANY line.
        private static readonly Regex s_StartMarkerRegex = new Regex(@"^<<<<<<<\s+\S", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex s_EndMarkerRegex = new Regex(@"^>>>>>>>\s+\S", RegexOptions.Compiled | RegexOptions.Multiline);

        // Debounce guard for ScanAndStubNow. Prevents focus-toggle thrash and double-work
        // when triggers 1 (DoUpdateAsync) and 4 (OnSnapshotUpdated) fire on the same update.
        private static long s_LastScanTicks;
        private const long DEBOUNCE_TICKS = TimeSpan.TicksPerMillisecond * 500;

        /// <summary>
        /// Eagerly find conflicted `.cs`/`.asmdef`/`.asmref` files and stub them BEFORE Unity's
        /// AssetDatabase notices, eliminating the transient compile-error flash that occurs when
        /// SnapshotUpdated-driven stubbing races with auto-import. Walks SVN's own conflict
        /// sidecars (`*.mine`, `*.working`) under Assets/ and Packages/ — only conflicted files have
        /// them — and stubs a file only when it also contains conflict markers on some line.
        /// Synchronous, idempotent (TryStub's header check prevents double-stubbing). Triggers:
        /// panel DoUpdateAsync post-success, domain-load (one-shot via delayCall), focus-gained,
        /// compilationStarted, OnSnapshotUpdated piggyback.
        /// </summary>
        public static void ScanAndStubNow(bool force = false)
        {
            long now = DateTime.UtcNow.Ticks;
            if (!force && (now - s_LastScanTicks) < DEBOUNCE_TICKS) return;
            s_LastScanTicks = now;

            bool stubbedAny = false;
            foreach (var folder in new[] { "Assets", "Packages" })
            {
                string root = Path.Combine(SvnCommandRunner.ProjectRoot, folder);
                if (!Directory.Exists(root)) continue;
                foreach (var pattern in s_SidecarPatterns)
                {
                    string[] sidecars;
                    try
                    {
                        sidecars = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[FRGVersionControl] ScanAndStubNow enumerate failed for {pattern} under {folder}: {ex.Message}");
                        continue;
                    }

                    foreach (var sidecar in sidecars)
                    {
                        string fullPath = sidecar.Substring(0, sidecar.Length - (pattern.Length - 1));
                        if (!s_StubbableExts.Contains(Path.GetExtension(fullPath))) continue;
                        if (!File.Exists(fullPath) || !HasConflictMarkers(fullPath)) continue;

                        string assetPath;
                        try { assetPath = MakeAssetPath(fullPath); }
                        catch { continue; }

                        if (s_StubbedPaths.Contains(assetPath)) continue;
                        if (TryStub(assetPath))
                        {
                            s_StubbedPaths.Add(assetPath);
                            stubbedAny = true;
                        }
                    }
                }
            }

            if (stubbedAny)
            {
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            }
        }

        /// <summary>Pre-import path used by <see cref="FileLockConflictPreImporter"/>. Detects
        /// conflict markers in a single file and stubs in place if found. Synchronous, idempotent
        /// via TryStub's header-prefix check. This runs INSIDE Unity's import pipeline (before
        /// the script compiler reads the file), which is the only race-free way to prevent the
        /// CS-error flash on external-CLI updates — every other trigger races AssetDatabase.</summary>
        public static bool TryStubIfConflicted(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string ext = Path.GetExtension(assetPath);
            if (string.IsNullOrEmpty(ext) || !s_StubbableExts.Contains(ext)) return false;

            string fullPath = Path.IsPathRooted(assetPath)
                ? assetPath
                : Path.Combine(SvnCommandRunner.ProjectRoot, assetPath);
            if (!File.Exists(fullPath)) return false;

            if (!HasConflictEvidence(fullPath, assetPath) || !HasConflictMarkers(fullPath)) return false;

            if (TryStub(assetPath))
            {
                s_StubbedPaths.Add(assetPath);
                return true;
            }
            return false;
        }

        // SVN leaves these next to a text-conflicted file (update: .mine / .r<N>; merge: .working).
        private static readonly string[] s_SidecarPatterns = { "*.mine", "*.working" };

        private static string FullPathOf(string assetPath)
            => Path.IsPathRooted(assetPath) ? assetPath : Path.Combine(SvnCommandRunner.ProjectRoot, assetPath);

        /// <summary>SVN's own conflict evidence for a file: a .mine / .working / .r&lt;N&gt; sidecar
        /// beside it, or status Conflict in the loaded status cache. A healthy file that merely
        /// contains a marker-like line is never stubbed.</summary>
        internal static bool HasConflictEvidence(string fullPath, string assetPath)
        {
            try
            {
                if (File.Exists(fullPath + ".mine") || File.Exists(fullPath + ".working")) return true;
                string dir = Path.GetDirectoryName(fullPath);
                string name = Path.GetFileName(fullPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, name + ".r*"))
                    {
                        string suffix = f.Substring(fullPath.Length + 2);
                        if (suffix.Length > 0 && IsAllDigits(suffix)) return true;
                    }
                }
            }
            catch { }
            return SvnStatusCache.Get(assetPath) == SvnFileStatus.Conflict;
        }

        private static bool IsAllDigits(string s)
        {
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        /// <summary>A conflict start marker and an end marker, each at the start of some line.</summary>
        internal static bool HasConflictMarkers(string fullPath)
        {
            try
            {
                string text;
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    text = sr.ReadToEnd();
                }
                return HasConflictMarkerText(text);
            }
            catch
            {
                return false;
            }
        }

        internal static bool HasConflictMarkerText(string text)
            => !string.IsNullOrEmpty(text) && s_StartMarkerRegex.IsMatch(text) && s_EndMarkerRegex.IsMatch(text);

        private static string MakeAssetPath(string fullPath)
        {
            string root = SvnCommandRunner.ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            string normal = fullPath.Replace('\\', '/');
            if (normal.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return normal.Substring(root.Length);
            return normal;
        }

        private static bool TryStub(string assetPath)
        {
            string fullPath = Path.IsPathRooted(assetPath)
                ? assetPath
                : Path.Combine(SvnCommandRunner.ProjectRoot, assetPath);
            if (!File.Exists(fullPath)) return false;

            string ext = Path.GetExtension(assetPath);
            string body;
            string headerToCheck;

            if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                body = s_StubBodyCs;
                headerToCheck = STUB_HEADER_CS;
            }
            else if (ext.Equals(".asmdef", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".asmref", StringComparison.OrdinalIgnoreCase))
            {
                body = GetStubBodyAsmdef(assetPath);
                headerToCheck = STUB_HEADER_ASMDEF;
            }
            else
            {
                return false;
            }

            try
            {
                // Idempotency: if the file already starts with our header, we've already
                // stubbed it. Don't rewrite (avoids unnecessary recompiles).
                string firstLine = TryReadFirstLine(fullPath);
                if (firstLine != null && firstLine.StartsWith(headerToCheck)) return false;

                File.WriteAllText(fullPath, body);
                Debug.Log(
                    $"[FRGVersionControl] Stubbed conflicted code file: {assetPath}\n" +
                    "Unity can now compile. Resolve via Window / FRG Version Control / Changes tab " +
                    "(Take Mine / Take Theirs on the conflicted row).");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[FRGVersionControl] Could not stub conflicted file {assetPath}: {ex.Message}\n" +
                    "Unity may show compile errors until the conflict is manually resolved.");
                return false;
            }
        }

        private static string TryReadFirstLine(string path)
        {
            try
            {
                using (var sr = new StreamReader(path))
                {
                    return sr.ReadLine();
                }
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Phase 4 polish: pre-import hook that runs INSIDE Unity's asset import pipeline,
    /// before the script compiler reads the file. Closes the race window on external-CLI
    /// updates where AssetDatabase auto-imports a `.cs` file with merge markers between
    /// `svn update` finishing and the shadow service's other triggers (focusChanged /
    /// SnapshotUpdated) catching it.
    ///
    /// `OnPreprocessAsset` fires for every imported asset; we filter to stubbable extensions
    /// and stub in place if conflict markers are present. Modifying the source file from
    /// within OnPreprocessAsset is safe — Unity reads asset content after preprocessor
    /// callbacks complete, so the compiler sees the stubbed content. Idempotency via the
    /// existing TryStub header-prefix check prevents re-import recursion.
    /// </summary>
    public class FileLockConflictPreImporter : AssetPostprocessor
    {
        private void OnPreprocessAsset()
        {
            // assetPath is the inherited property pointing at the asset being imported.
            // TryStubIfConflicted is a no-op for unrelated extensions and for files without
            // the marker — cheap to call universally.
            FileLockConflictShadowService.TryStubIfConflicted(assetPath);
        }
    }
}
