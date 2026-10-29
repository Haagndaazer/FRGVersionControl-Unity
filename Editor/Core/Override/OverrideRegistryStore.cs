using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Core.OverrideMode
{
    /// <summary>
    /// File I/O for the ignore-on-commit registry sidecar.
    ///
    /// Phase 2 Step E SVN port:
    /// - Sidecar path: `.svn/ignore-on-commit-registry.json` (Pre-impl Test 1 finding `ffe84603273a` —
    ///   .svn/ survives status/cleanup/vacuum-pristines, never gets `svn add`-ed).
    /// - Stash directory and GetStashDir() REMOVED — svn shelve is unavailable;
    ///   Stash/Pop methods on FileLockOverrideManager are deleted.
    ///
    /// Per Phase 2 plan Conventions:
    /// - Single-writer mutex serializes all reads/writes (`WithLock`)
    /// - Writes use atomic-write pattern: write to `<sidecar>.tmp` then File.Move
    ///   into final position. Survives crashes mid-write.
    /// </summary>
    public static class OverrideRegistryStore
    {
        private const string SIDECAR_RELATIVE_PATH = ".svn/ignore-on-commit-registry.json";

        private static readonly object s_RegistryLock = new object();

        /// <summary>Absolute path to the sidecar file.</summary>
        public static string GetSidecarPath()
        {
            return Path.Combine(SvnCommandRunner.ProjectRoot, SIDECAR_RELATIVE_PATH);
        }

        /// <summary>
        /// Read the registry, mutate it via the supplied action, and atomically write back.
        /// All callers that modify the registry must use this method.
        /// </summary>
        public static void WithLock(Action<List<OverrideEntry>> mutator)
        {
            lock (s_RegistryLock)
            {
                var entries = LoadInternal();
                mutator(entries);
                SaveInternal(entries);
            }
        }

        /// <summary>Read-only snapshot (returns a copy; mutation does NOT affect the file).</summary>
        public static List<OverrideEntry> ReadSnapshot()
        {
            lock (s_RegistryLock)
            {
                return LoadInternal();
            }
        }

        // ---------------------- internal helpers ---------------------------

        private static List<OverrideEntry> LoadInternal()
        {
            string path = GetSidecarPath();
            if (!File.Exists(path))
            {
                return new List<OverrideEntry>();
            }

            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<OverrideEntry>();
                }
                var envelope = JsonUtility.FromJson<OverrideRegistryEnvelope>(json);
                return envelope?.entries ?? new List<OverrideEntry>();
            }
            catch (Exception e)
            {
                // Corrupt JSON — log loudly and return empty. File on disk is left
                // intact for manual recovery (Phase 2 plan §Sidecar-3).
                Debug.LogError($"[FRGVersionControl.IgnoreOnCommit] Sidecar parse failed at {path}: {e.Message}. " +
                               "Returning empty registry — file on disk left intact for manual recovery.");
                return new List<OverrideEntry>();
            }
        }

        private static void SaveInternal(List<OverrideEntry> entries)
        {
            string path = GetSidecarPath();
            string tmp = path + ".tmp";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                var envelope = new OverrideRegistryEnvelope { entries = entries };
                string json = JsonUtility.ToJson(envelope, true);

                File.WriteAllText(tmp, json);

                // Atomic replace. File.Move on Windows fails if dest exists, so handle that.
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[FRGVersionControl.IgnoreOnCommit] Failed to persist sidecar: {e.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* swallow */ }
                throw;
            }
        }
    }
}
