using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FRGVersionControl.Services
{
    /// <summary>
    /// Ring buffer of the plugin's recent console messages for bug reports. Errors from other code
    /// are counted, not stored.
    /// </summary>
    public sealed class ConsoleCaptureBuffer
    {
        public const int Capacity = 100;
        public const int MessageMax = 1000;
        public const int StackLinesMax = 8;

        private static readonly string[] s_Tags = { "[SvnCommandRunner]", "[SvnStatusCache]", "[PendingChanges]" };

        [Serializable]
        public sealed class Entry
        {
            public string utc;
            public string type;
            public string message;
            public string stack;
        }

        [Serializable]
        private sealed class Saved
        {
            public List<Entry> entries = new List<Entry>();
            public int otherErrors;
        }

        private readonly object m_Sync = new object();
        private readonly List<Entry> m_Entries = new List<Entry>();
        private int m_OtherErrors;

        public static bool IsOurs(string message, string stackTrace)
        {
            if (!string.IsNullOrEmpty(message))
            {
                var m = message.TrimStart();
                if (m.StartsWith("[FRGVersionControl", StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var tag in s_Tags)
                    if (m.StartsWith(tag, StringComparison.Ordinal)) return true;
            }
            return !string.IsNullOrEmpty(stackTrace) && stackTrace.IndexOf("FRGVersionControl.", StringComparison.Ordinal) >= 0;
        }

        public void Record(string message, string stackTrace, LogType type, DateTime utc)
        {
            bool isError = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
            if (!IsOurs(message, stackTrace))
            {
                if (isError) lock (m_Sync) m_OtherErrors++;
                return;
            }
            var entry = new Entry
            {
                utc = utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                type = type.ToString(),
                message = Cap(message ?? "", MessageMax),
                stack = StackLines(stackTrace),
            };
            lock (m_Sync)
            {
                m_Entries.Add(entry);
                if (m_Entries.Count > Capacity) m_Entries.RemoveRange(0, m_Entries.Count - Capacity);
            }
        }

        public List<Entry> Snapshot(out int otherErrors)
        {
            lock (m_Sync)
            {
                otherErrors = m_OtherErrors;
                return new List<Entry>(m_Entries);
            }
        }

        public string Serialize()
        {
            lock (m_Sync)
                return JsonUtility.ToJson(new Saved { entries = new List<Entry>(m_Entries), otherErrors = m_OtherErrors });
        }

        public void Restore(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            Saved saved;
            try { saved = JsonUtility.FromJson<Saved>(json); }
            catch { return; }
            if (saved?.entries == null) return;
            lock (m_Sync)
            {
                m_Entries.Clear();
                foreach (var e in saved.entries)
                    if (e != null) m_Entries.Add(e);
                if (m_Entries.Count > Capacity) m_Entries.RemoveRange(0, m_Entries.Count - Capacity);
                m_OtherErrors = Math.Max(0, saved.otherErrors);
            }
        }

        public string Format()
        {
            var entries = Snapshot(out var otherErrors);
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                sb.Append(e.utc).Append(' ').Append(e.type).Append(": ").Append(e.message).Append('\n');
                if (!string.IsNullOrEmpty(e.stack))
                    foreach (var line in e.stack.Split('\n'))
                        sb.Append("    ").Append(line).Append('\n');
            }
            if (entries.Count == 0) sb.Append("No FRG Version Control messages since the editor started.\n");
            if (otherErrors > 0)
                sb.Append(otherErrors == 1
                    ? "(1 other error from other code is not included.)\n"
                    : $"({otherErrors} other errors from other code are not included.)\n");
            return sb.ToString();
        }

        private static string StackLines(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return "";
            var kept = new List<string>();
            foreach (var raw in stackTrace.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("UnityEngine.Debug", StringComparison.Ordinal) ||
                    line.StartsWith("UnityEngine.Logger", StringComparison.Ordinal)) continue;
                kept.Add(Cap(line, 300));
                if (kept.Count == StackLinesMax) break;
            }
            return string.Join("\n", kept);
        }

        private static string Cap(string s, int max) => s.Length > max ? s.Substring(0, max) : s;
    }

    /// <summary>
    /// Captures the plugin's console messages from editor load onward, kept across script reloads,
    /// for the Report a Bug window.
    /// </summary>
    [InitializeOnLoad]
    public static class FrgConsoleCapture
    {
        private const string SessionKey = "FRGVersionControl.ConsoleCapture";

        public static ConsoleCaptureBuffer Buffer { get; } = new ConsoleCaptureBuffer();

        static FrgConsoleCapture()
        {
            Buffer.Restore(SessionState.GetString(SessionKey, ""));
            Application.logMessageReceivedThreaded += OnLog;
            AssemblyReloadEvents.beforeAssemblyReload += Save;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            try { Buffer.Record(condition, stackTrace, type, DateTime.UtcNow); }
            catch { }
        }

        private static void Save()
        {
            try { SessionState.SetString(SessionKey, Buffer.Serialize()); }
            catch { }
        }
    }
}
