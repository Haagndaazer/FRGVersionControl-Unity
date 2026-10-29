using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Core.Changelists
{
    /// <summary>
    /// Sidecar for user-created changelist NAMES that aren't yet (or are no longer) backed by
    /// any file membership. SVN garbage-collects a changelist when its last file leaves, but
    /// the TortoiseSVN UX shows a name immediately on creation. To match without subverting SVN,
    /// persist user-created names here; SVN's wc.db remains source-of-truth for membership.
    ///
    /// Sidecar path: `.svn/user-changelists.json` (same `.svn/` precedent as the override
    /// registry — finding ffe84603273a established that `.svn/` survives status/cleanup/
    /// vacuum-pristines and is never `svn add`-ed).
    ///
    /// 0.2.60 — IPC-routed via cl.userListGet/Set/Add/Remove/Rename. The legacy JsonUtility
    /// path is preserved as a fallback for cases where the IPC binary is unavailable.
    /// </summary>
    public static class UserChangelistStore
    {
        private const string SIDECAR_RELATIVE_PATH = ".svn/user-changelists.json";

        private static readonly object s_StoreLock = new object();

        public static string GetSidecarPath()
        {
            return Path.Combine(SvnCommandRunner.ProjectRoot, SIDECAR_RELATIVE_PATH);
        }

        public static void WithLock(Action<List<string>> mutator)
        {
            lock (s_StoreLock)
            {
                var names = ReadSnapshot();
                mutator(names);
                WriteAll(names);
            }
        }

        public static List<string> ReadSnapshot()
        {
            // Try IPC first.
            var ipc = TryIpcGet();
            if (ipc != null) return ipc;
            lock (s_StoreLock)
            {
                return LoadInternal();
            }
        }

        public static void WriteAll(List<string> names)
        {
            // Try IPC first.
            if (TryIpcSet(names)) return;
            lock (s_StoreLock)
            {
                SaveInternal(names);
            }
        }

        private static List<string> TryIpcGet()
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return null;
                var ctx = client.BuildDefaultContext();
                var result = client.InvokeRawAsync("cl.userListGet", "{}", ctx).GetAwaiter().GetResult();
                if (!result.Ok) return null;
                return FRGVersionControl.Core.IpcClient.IpcResponseParsers.ParseNamesArray(result.RawLine);
            }
            catch { return null; }
        }

        private static bool TryIpcSet(List<string> names)
        {
            try
            {
                var client = FRGVersionControl.Core.FileLockManager.Instance.IpcClient;
                if (!client.IsAvailable) return false;
                var ctx = client.BuildDefaultContext();
                var sb = new System.Text.StringBuilder("{\"names\":[");
                for (int i = 0; i < names.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(IpcEscape(names[i])).Append("\"");
                }
                sb.Append("]}");
                var result = client.InvokeRawAsync("cl.userListSet", sb.ToString(), ctx).GetAwaiter().GetResult();
                return result.Ok;
            }
            catch { return false; }
        }

        private static string IpcEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // Legacy JsonUtility fallback ----------------------------------------

        private static List<string> LoadInternal()
        {
            string path = GetSidecarPath();
            if (!File.Exists(path))
            {
                return new List<string>();
            }
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<string>();
                }
                var envelope = JsonUtility.FromJson<UserChangelistEnvelope>(json);
                return envelope?.names ?? new List<string>();
            }
            catch (Exception e)
            {
                Debug.LogError($"[FRGVersionControl.Changelists] Sidecar parse failed at {path}: {e.Message}. " +
                               "Returning empty list — file on disk left intact for manual recovery.");
                return new List<string>();
            }
        }

        private static void SaveInternal(List<string> names)
        {
            string path = GetSidecarPath();
            string tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var envelope = new UserChangelistEnvelope { names = names };
                string json = JsonUtility.ToJson(envelope, true);
                File.WriteAllText(tmp, json);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[FRGVersionControl.Changelists] Failed to persist sidecar: {e.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }

    [Serializable]
    internal class UserChangelistEnvelope
    {
        public List<string> names = new List<string>();
    }
}
