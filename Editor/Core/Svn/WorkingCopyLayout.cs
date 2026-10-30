using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace FRGVersionControl.Core.Svn
{
    /// <summary>
    /// Where the Unity project folder sits in its SVN working copy (svn-core meta.layout,
    /// SVN_CORE_API.md §5.1, §6.7): the project folder may be the checkout itself or a folder inside
    /// a larger checkout (for example trunk/ of a whole-repository checkout). Fetched once in the
    /// background after editor load and retried on demand; until then <see cref="WcRoot"/> falls back
    /// to the project folder.
    /// </summary>
    public static class WorkingCopyLayout
    {
        [Serializable]
        public sealed class Info
        {
            public string projectRoot;
            public string wcRoot;
            public bool isWcRoot;
            public string projectPath;
            public string projectUrl;
            public string wcRootUrl;
            public string repoRoot;
            public bool wcRootIsRepositoryRoot;
        }

        public struct FetchResult
        {
            public Info info;
            public bool busy;
            public string error;
        }

        private const double RetryAfterSeconds = 30.0;

        private static volatile Info s_Info;
        private static int s_Fetching;
        private static DateTime s_LastAttempt = DateTime.MinValue;

        public static Info Current => s_Info;

        /// <summary>The folder holding the working copy's .svn (forward slashes); the project folder
        /// until meta.layout has answered.</summary>
        public static string WcRoot
        {
            get
            {
                var info = s_Info;
                return string.IsNullOrEmpty(info?.wcRoot) ? ProjectFolder.Path : info.wcRoot.Replace('\\', '/');
            }
        }

        [InitializeOnLoadMethod]
        private static void StartAfterLoad() => EditorApplication.delayCall += () => EnsureFetched();

        /// <summary>Starts a background meta.layout read unless one is loaded, running, or failed
        /// less than <see cref="RetryAfterSeconds"/> ago.</summary>
        public static void EnsureFetched()
        {
            if (s_Info != null) return;
            if ((DateTime.UtcNow - s_LastAttempt).TotalSeconds < RetryAfterSeconds) return;
            if (Interlocked.Exchange(ref s_Fetching, 1) == 1) return;
            s_LastAttempt = DateTime.UtcNow;
            Task.Run(() =>
            {
                try
                {
                    var r = Fetch();
                    if (r.info != null) s_Info = r.info;
                }
                finally
                {
                    Interlocked.Exchange(ref s_Fetching, 0);
                }
            });
        }

        /// <summary>One synchronous meta.layout call (safe on any thread once the project folder is
        /// primed). Busy means svn-core is running a long operation; try again later.</summary>
        public static FetchResult Fetch()
        {
            try
            {
                var client = FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return new FetchResult { error = "svn-core.exe is not available. Reinstall the package, then restart the editor." };
                var r = client.InvokeRawAsync("meta.layout", "{}", client.BuildDefaultContext()).GetAwaiter().GetResult();
                if (r.Ok)
                {
                    var info = JsonUtility.FromJson<Envelope>(r.RawLine)?.result;
                    if (info != null) s_Info = info;
                    return new FetchResult { info = info };
                }
                if (r.Busy) return new FetchResult { busy = true, error = r.ErrorDefaultAction ?? r.ErrorMessage };
                return new FetchResult { error = string.IsNullOrEmpty(r.ErrorDefaultAction) ? r.ErrorMessage : r.ErrorDefaultAction };
            }
            catch (Exception e)
            {
                return new FetchResult { error = e.Message };
            }
        }

        [Serializable]
        private sealed class Envelope
        {
            public Info result;
        }
    }
}
