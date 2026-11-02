using System;
using System.Threading.Tasks;
using UnityEngine;
using FRGVersionControl.Core.IpcClient;

namespace FRGVersionControl.Core.Svn
{
    /// <summary>
    /// This project's SVN login through svn-core (SVN_CORE_API.md §5.11): the project's own saved
    /// login, the logins TortoiseSVN saved for the same server, reusing one of those, or signing in
    /// with a typed username and password. The editor never reads svn's auth files itself.
    /// </summary>
    public static class SvnLogin
    {
        [Serializable]
        public sealed class Login
        {
            public string username;
            public string realm;
        }

        [Serializable]
        public sealed class SavedLogin
        {
            public string id;
            public string username;
            public string realm;
        }

        [Serializable]
        public sealed class Status
        {
            public string url;
            public bool usesPasswordCache;
            public string localUsername;
            public Login projectLogin;
            public SavedLogin[] savedLogins;

            public string ProjectUsername => projectLogin != null && !string.IsNullOrEmpty(projectLogin.username) ? projectLogin.username : null;
        }

        [Serializable]
        public sealed class Outcome
        {
            public bool signedIn;
            public string username;
            public string url;
            public string kind;
            public string message;
            public string defaultAction;
        }

        [Serializable]
        private sealed class StatusEnvelope { public Status result; }

        [Serializable]
        private sealed class OutcomeEnvelope { public Outcome result; }

        public struct StatusResult
        {
            public Status status;
            public bool busy;
            public string error;
        }

        /// <summary>One auth.logins call (local files only; the server is not contacted).</summary>
        public static StatusResult FetchStatus()
        {
            try
            {
                var client = FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return new StatusResult { error = "svn-core.exe is not available. Reinstall the package, then restart the editor." };
                var r = client.InvokeRawAsync("auth.logins", "{}", client.BuildDefaultContext()).GetAwaiter().GetResult();
                return ToStatus(r);
            }
            catch (Exception e)
            {
                return new StatusResult { error = e.Message };
            }
        }

        public static async Task<StatusResult> FetchStatusAsync()
        {
            try
            {
                var client = FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return new StatusResult { error = "svn-core.exe is not available. Reinstall the package, then restart the editor." };
                var r = await client.InvokeRawAsync("auth.logins", "{}", client.BuildDefaultContext()).ConfigureAwait(false);
                return ToStatus(r);
            }
            catch (Exception e)
            {
                return new StatusResult { error = e.Message };
            }
        }

        internal static Status ParseStatus(string rawLine)
        {
            var status = JsonUtility.FromJson<StatusEnvelope>(rawLine)?.result;
            if (status != null && status.savedLogins == null) status.savedLogins = new SavedLogin[0];
            return status;
        }

        internal static Outcome ParseOutcome(string rawLine) => JsonUtility.FromJson<OutcomeEnvelope>(rawLine)?.result;

        private static StatusResult ToStatus(IpcRawResult r)
        {
            if (r.Ok)
            {
                var status = ParseStatus(r.RawLine);
                if (status == null) return new StatusResult { error = "svn-core returned an unreadable login list. Run the Setup Wizard again." };
                return new StatusResult { status = status };
            }
            if (r.Busy) return new StatusResult { busy = true, error = "SVN is busy right now. Try again in a moment." };
            return new StatusResult { error = Sentence(r) };
        }

        /// <summary>Reuses a login TortoiseSVN saved (svn-core checks it with the server first).</summary>
        public static Task<Outcome> AdoptAsync(string id)
            => InvokeAsync("auth.adoptSavedLogin", "{\"id\":\"" + SvnCommandRunner.IpcEscape(id) + "\"}");

        /// <summary>Signs this project in. The password goes to svn-core inside the request and is
        /// never logged.</summary>
        public static Task<Outcome> SignInAsync(string username, string password)
            => InvokeAsync("auth.signIn",
                "{\"username\":\"" + SvnCommandRunner.IpcEscape(username) + "\",\"password\":\"" + SvnCommandRunner.IpcEscape(password) + "\"}");

        private static async Task<Outcome> InvokeAsync(string op, string argsJson)
        {
            try
            {
                var client = FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return Failed("svn-core.exe is not available.", "Reinstall the package, then restart the editor.");
                var r = await client.InvokeRawAsync(op, argsJson, client.BuildDefaultContext()).ConfigureAwait(false);
                if (r.Ok)
                {
                    var outcome = ParseOutcome(r.RawLine);
                    return outcome ?? Failed("svn-core returned an unreadable answer.", "Run the Setup Wizard again.");
                }
                if (r.Busy) return Failed("SVN is busy right now.", "Try again when the current operation finishes.", "busy");
                if (r.StillRunning || r.PipeLost) return Failed("The sign-in is still being checked.", "Run the Setup Wizard again in a moment to see whether it worked.", "still_running");
                return Failed(r.ErrorMessage?.Trim(), r.ErrorDefaultAction, r.ErrorKind);
            }
            catch (Exception e)
            {
                return Failed(e.Message, "Run the Setup Wizard again.");
            }
        }

        private static Outcome Failed(string message, string action, string kind = null)
            => new Outcome { signedIn = false, message = message, defaultAction = action, kind = kind };

        private static string Sentence(IpcRawResult r)
        {
            var message = r.ErrorMessage?.Trim();
            if (string.IsNullOrEmpty(r.ErrorDefaultAction)) return message;
            return string.IsNullOrEmpty(message) ? r.ErrorDefaultAction : message.TrimEnd('.') + ". " + r.ErrorDefaultAction;
        }

        /// <summary>The server part of a realm, e.g. "svn.frgdev.net" from
        /// "&lt;https://svn.frgdev.net:443&gt; FRG SVN Repository".</summary>
        public static string ServerName(string realm)
        {
            if (string.IsNullOrEmpty(realm)) return "";
            int open = realm.IndexOf('<');
            int close = realm.IndexOf('>');
            if (open < 0 || close <= open) return realm;
            var inner = realm.Substring(open + 1, close - open - 1);
            try { return new Uri(inner).Host; }
            catch { return inner; }
        }
    }
}
