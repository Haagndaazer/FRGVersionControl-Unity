using System.Threading;

namespace FRGVersionControl.Services
{
    /// <summary>
    /// Phase 4 Step 0: scriptable silence/disable toggles for the FRGVersionControl subsystem.
    /// Wise SVN cherry-pick item #10: lets batch tools (test runners, asset processors, build
    /// pipelines) suppress prompts and modification-processor handling during their own work.
    ///
    /// Two independent counters:
    /// - <b>Silence</b>: suppresses user-facing prompts and dialogs. Used when a tool is
    ///   running and should not interrupt the user with lock prompts, override modals, etc.
    /// - <b>TemporaryDisable</b>: suspends the entire modification-processor pipeline (no
    ///   auto-lock on save, no lock-of-other refusal, no folder-add prompts). Used when
    ///   a tool is making sweeping file changes that would otherwise trigger thousands
    ///   of dialogs.
    ///
    /// Both use Interlocked counters so nested Request/Clear calls work correctly without
    /// races. Caller MUST always pair every Request with a matching Clear (use try/finally).
    ///
    /// Usage:
    /// <code>
    /// FileLockSilenceService.RequestSilence();
    /// try { /* batch op that would otherwise trigger prompts */ }
    /// finally { FileLockSilenceService.ClearSilence(); }
    /// </code>
    /// </summary>
    public static class FileLockSilenceService
    {
        private static int s_SilenceCount = 0;
        private static int s_TemporaryDisableCount = 0;

        /// <summary>True iff at least one Request without a matching Clear is in flight.</summary>
        public static bool IsSilenced => s_SilenceCount > 0;

        /// <summary>True iff at least one Request without a matching Clear is in flight.</summary>
        public static bool IsTemporarilyDisabled => s_TemporaryDisableCount > 0;

        /// <summary>Increment the silence counter. UI prompts will be suppressed until ClearSilence.</summary>
        public static void RequestSilence() => Interlocked.Increment(ref s_SilenceCount);

        /// <summary>Decrement the silence counter. Idempotent if mismatched (clamps at 0).</summary>
        public static void ClearSilence()
        {
            int v = Interlocked.Decrement(ref s_SilenceCount);
            if (v < 0)
            {
                Interlocked.Exchange(ref s_SilenceCount, 0);
                UnityEngine.Debug.LogWarning(
                    "[FRGVersionControl.SilenceService] ClearSilence called more times than RequestSilence; " +
                    "counter clamped to 0. Check your try/finally pairing.");
            }
        }

        /// <summary>Increment the temporary-disable counter. The modification-processor
        /// pipeline (auto-lock-on-save, save-block, folder-add prompts) will be suspended.</summary>
        public static void RequestTemporaryDisable() => Interlocked.Increment(ref s_TemporaryDisableCount);

        /// <summary>Decrement the temporary-disable counter. Idempotent if mismatched.</summary>
        public static void ClearTemporaryDisable()
        {
            int v = Interlocked.Decrement(ref s_TemporaryDisableCount);
            if (v < 0)
            {
                Interlocked.Exchange(ref s_TemporaryDisableCount, 0);
                UnityEngine.Debug.LogWarning(
                    "[FRGVersionControl.SilenceService] ClearTemporaryDisable called more times than " +
                    "RequestTemporaryDisable; counter clamped to 0.");
            }
        }
    }
}
