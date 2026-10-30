using System;
using System.Collections.Generic;
using System.Text;
using FRGVersionControl.Core.Svn;
using UnityEngine;

namespace FRGVersionControl.Core.IpcClient
{
    /// <summary>svn-core JSON → Unity types. A garbled frame throws so callers keep their prior cache.</summary>
    internal static class IpcResponseParsers
    {
        [Serializable]
        private sealed class StatusEnvelope
        {
            public StatusResult result;
        }

        [Serializable]
        private sealed class StatusResult
        {
            public StatusEntryShim[] entries;
        }

        [Serializable]
        internal sealed class StatusEntryShim
        {
            public string path;
            public string wcStatus;
            public string propStatus;
            public string changelist;
            public string lockState;
            public string lockOwner;
            public long lockedRev;
        }

        [Serializable]
        private sealed class LogEnvelope
        {
            public LogResult result;
        }

        [Serializable]
        private sealed class LogResult
        {
            public LogShim[] revisions;
        }

        [Serializable]
        private sealed class LogShim
        {
            public long revision;
            public string author;
            public string date;
            public string message;
        }

        [Serializable]
        private sealed class NamesEnvelope
        {
            public NamesResult result;
        }

        [Serializable]
        private sealed class NamesResult
        {
            public string[] names;
        }

        private static StatusEntryShim[] Entries(string rawJson)
        {
            var env = JsonUtility.FromJson<StatusEnvelope>(rawJson ?? "");
            return env?.result?.entries ?? Array.Empty<StatusEntryShim>();
        }

        private static SvnStatusEntry ToStatusEntry(StatusEntryShim e) => new SvnStatusEntry
        {
            path = e.path,
            status = MapWcStatus(e.wcStatus, e.propStatus),
            lockState = MapLockState(e.lockState),
            lockOwner = e.lockOwner ?? "",
            changelist = string.IsNullOrEmpty(e.changelist) ? null : e.changelist,
            revision = 0,
            outOfDate = false,
        };

        public static List<SvnStatusEntry> ParseStatusEntries(string rawJson)
        {
            var list = new List<SvnStatusEntry>();
            foreach (var e in Entries(rawJson))
            {
                if (e != null && !string.IsNullOrEmpty(e.path)) list.Add(ToStatusEntry(e));
            }
            return list;
        }

        /// <summary>Keyed by the core's path spelling; a duplicate path keeps the last entry.</summary>
        public static Dictionary<string, SvnStatusEntry> ParseStatusMap(string rawJson)
        {
            var dict = new Dictionary<string, SvnStatusEntry>(StringComparer.Ordinal);
            foreach (var e in Entries(rawJson))
            {
                if (e != null && !string.IsNullOrEmpty(e.path)) dict[e.path] = ToStatusEntry(e);
            }
            return dict;
        }

        /// <summary>Only entries whose lock state is not None.</summary>
        public static Dictionary<string, SvnLockInfo> ParseLockMap(string rawJson)
        {
            var dict = new Dictionary<string, SvnLockInfo>(StringComparer.Ordinal);
            foreach (var e in Entries(rawJson))
            {
                if (e == null || string.IsNullOrEmpty(e.path)) continue;
                var state = MapLockState(e.lockState);
                if (state == LockState.None) continue;
                dict[e.path] = new SvnLockInfo
                {
                    path = e.path,
                    state = state,
                    lockOwner = e.lockOwner ?? "",
                    lockMessage = "",
                    lockCreated = null,
                };
            }
            return dict;
        }

        /// <summary>The first status entry, or null when the response lists none.</summary>
        public static SvnStatusEntry ParseFirstStatusEntry(string rawJson)
        {
            foreach (var e in Entries(rawJson))
            {
                if (e != null && !string.IsNullOrEmpty(e.path)) return ToStatusEntry(e);
            }
            return null;
        }

        public static List<SvnLogEntry> ParseLogEntries(string rawJson)
        {
            var entries = new List<SvnLogEntry>();
            var env = JsonUtility.FromJson<LogEnvelope>(rawJson ?? "");
            if (env?.result?.revisions == null) return entries;
            foreach (var r in env.result.revisions)
            {
                if (r == null) continue;
                entries.Add(new SvnLogEntry
                {
                    revision = (int)r.revision,
                    author = r.author ?? "",
                    date = r.date ?? "",
                    message = r.message ?? "",
                });
            }
            return entries;
        }

        public static List<string> ParseNamesArray(string rawJson)
        {
            var env = JsonUtility.FromJson<NamesEnvelope>(rawJson ?? "");
            var list = new List<string>();
            if (env?.result?.names == null) return list;
            foreach (var n in env.result.names)
            {
                if (!string.IsNullOrEmpty(n)) list.Add(n);
            }
            return list;
        }

        /// <summary>Changelist name → paths; JsonUtility cannot map a dictionary, so this uses <see cref="IpcJson"/>.</summary>
        public static Dictionary<string, List<string>> ParseChangelistMembership(string rawJson)
        {
            var dict = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(rawJson)) return dict;
            int key = IpcJson.FindKey(rawJson, "membership", 0);
            if (key < 0) return dict;
            int i = IpcJson.SkipWhitespace(rawJson, IpcJson.AfterColon(rawJson, key));
            if (i >= rawJson.Length || rawJson[i] != '{') return dict;
            int end = IpcJson.FindClose(rawJson, i);
            if (end < 0) throw new FormatException("cl.membership: unterminated membership object");
            i++;
            while (true)
            {
                i = IpcJson.SkipWhitespace(rawJson, i);
                if (i >= end) break;
                if (rawJson[i] == ',') { i++; continue; }
                if (rawJson[i] != '"') throw new FormatException("cl.membership: expected a changelist name at " + i);
                var name = IpcJson.ReadString(rawJson, i, out i);
                i = IpcJson.SkipWhitespace(rawJson, i);
                if (i >= end || rawJson[i] != ':') throw new FormatException("cl.membership: expected ':' after a changelist name");
                i = IpcJson.SkipWhitespace(rawJson, i + 1);
                if (i >= end || rawJson[i] != '[') throw new FormatException("cl.membership: expected a path array");
                dict[name] = IpcJson.ReadStringArray(rawJson, i, out i);
            }
            return dict;
        }

        /// <summary>A string→string object under <c>result.&lt;key&gt;</c> (e.g. prop.list's
        /// <c>props</c>); missing key → empty, garbled → throws.</summary>
        public static Dictionary<string, string> ParseStringMap(string rawJson, string key)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(rawJson)) return dict;
            int k = IpcJson.FindKey(rawJson, key, 0);
            if (k < 0) return dict;
            int i = IpcJson.SkipWhitespace(rawJson, IpcJson.AfterColon(rawJson, k));
            if (i >= rawJson.Length || rawJson[i] != '{') return dict;
            int end = IpcJson.FindClose(rawJson, i);
            if (end < 0) throw new FormatException(key + ": unterminated object");
            i++;
            while (true)
            {
                i = IpcJson.SkipWhitespace(rawJson, i);
                if (i >= end) break;
                if (rawJson[i] == ',') { i++; continue; }
                if (rawJson[i] != '"') throw new FormatException(key + ": expected a name at " + i);
                var name = IpcJson.ReadString(rawJson, i, out i);
                i = IpcJson.SkipWhitespace(rawJson, i);
                if (i >= end || rawJson[i] != ':') throw new FormatException(key + ": expected ':' after a name");
                i = IpcJson.SkipWhitespace(rawJson, i + 1);
                if (i >= end || rawJson[i] != '"') throw new FormatException(key + ": expected a string value");
                dict[name] = IpcJson.ReadString(rawJson, i, out i);
            }
            return dict;
        }

        public static SvnFileStatus MapWcStatus(string wc, string prop)
        {
            switch (wc)
            {
                case "modified": return SvnFileStatus.Modified;
                case "added": return SvnFileStatus.Added;
                case "deleted": return SvnFileStatus.Deleted;
                case "replaced": return SvnFileStatus.Replaced;
                case "conflicted": return SvnFileStatus.Conflict;
                case "missing": return SvnFileStatus.Missing;
                case "unversioned": return SvnFileStatus.Untracked;
                case "ignored": return SvnFileStatus.Ignored;
                case "normal":
                    return prop == "modified" || prop == "conflicted" ? SvnFileStatus.Modified : SvnFileStatus.None;
                default: return SvnFileStatus.None;
            }
        }

        public static LockState MapLockState(string s)
        {
            switch (s)
            {
                case "K": return LockState.K;
                case "O": return LockState.O;
                case "B": return LockState.B;
                case "T": return LockState.T;
                default: return LockState.None;
            }
        }
    }

    /// <summary>Minimal string-aware JSON scanning for the shapes JsonUtility cannot map.</summary>
    internal static class IpcJson
    {
        /// <summary>Index of the bracket closing the <c>{</c> or <c>[</c> at
        /// <paramref name="open"/>, skipping string literals; -1 when unterminated.</summary>
        public static int FindClose(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') { i = StringEnd(s, i); if (i < 0) return -1; continue; }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        /// <summary>Index of the quote closing the string literal opening at <paramref name="quote"/>,
        /// honouring backslash escapes; -1 when unterminated.</summary>
        public static int StringEnd(string s, int quote)
        {
            for (int i = quote + 1; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i;
            }
            return -1;
        }

        /// <summary>Index of the opening quote of the object key <paramref name="name"/> at or
        /// after <paramref name="from"/>, ignoring matches inside other string values.</summary>
        public static int FindKey(string s, string name, int from)
        {
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] != '"') continue;
                int end = StringEnd(s, i);
                if (end < 0) return -1;
                int j = SkipWhitespace(s, end + 1);
                if (j < s.Length && s[j] == ':' && end - i - 1 == name.Length &&
                    string.CompareOrdinal(s, i + 1, name, 0, name.Length) == 0)
                {
                    return i;
                }
                i = end;
            }
            return -1;
        }

        /// <summary>Index of the opening quote of key <paramref name="name"/> directly inside the
        /// object that opens at <paramref name="objectOpen"/> — never a key of a nested object or
        /// text inside a string value; -1 when absent.</summary>
        public static int FindKeyInObject(string s, int objectOpen, string name)
        {
            if (objectOpen < 0 || objectOpen >= s.Length || s[objectOpen] != '{') return -1;
            int depth = 0;
            for (int i = objectOpen; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    int end = StringEnd(s, i);
                    if (end < 0) return -1;
                    if (depth == 1)
                    {
                        int j = SkipWhitespace(s, end + 1);
                        if (j < s.Length && s[j] == ':' && end - i - 1 == name.Length &&
                            string.CompareOrdinal(s, i + 1, name, 0, name.Length) == 0)
                        {
                            return i;
                        }
                    }
                    i = end;
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') { depth--; if (depth == 0) return -1; }
            }
            return -1;
        }

        /// <summary>Key <paramref name="name"/> of the frame's top-level object.</summary>
        public static int FindTopLevelKey(string s, string name)
            => s == null ? -1 : FindKeyInObject(s, SkipWhitespace(s, 0), name);

        /// <summary>The literal value (true/false/number/null) or decoded string of a top-level
        /// key, or of <c>result.&lt;name&gt;</c> when <paramref name="inResult"/> is true; null when absent.</summary>
        public static string ReadScalar(string s, string name, bool inResult = false)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int obj = SkipWhitespace(s, 0);
            if (inResult)
            {
                int r = FindKeyInObject(s, obj, "result");
                if (r < 0) return null;
                obj = SkipWhitespace(s, AfterColon(s, r));
            }
            int k = FindKeyInObject(s, obj, name);
            if (k < 0) return null;
            int v = SkipWhitespace(s, AfterColon(s, k));
            if (v >= s.Length) return null;
            if (s[v] == '"') return ReadString(s, v, out _);
            int e = v;
            while (e < s.Length && s[e] != ',' && s[e] != '}' && s[e] != ']' && !char.IsWhiteSpace(s[e])) e++;
            return s.Substring(v, e - v);
        }

        public static int AfterColon(string s, int keyQuote)
        {
            int end = StringEnd(s, keyQuote);
            int j = SkipWhitespace(s, end + 1);
            return j + 1;
        }

        public static int SkipWhitespace(string s, int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
            return i;
        }

        /// <summary>Decodes the string literal opening at <paramref name="quote"/> (all JSON escapes,
        /// including <c>\uXXXX</c> surrogate pairs); <paramref name="next"/> is the index after it.</summary>
        public static string ReadString(string s, int quote, out int next)
        {
            int end = StringEnd(s, quote);
            if (end < 0) throw new FormatException("unterminated JSON string at " + quote);
            var sb = new StringBuilder(end - quote);
            for (int i = quote + 1; i < end; i++)
            {
                char c = s[i];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[++i];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                        i += 4;
                        break;
                    default: throw new FormatException("invalid JSON escape \\" + e + " at " + i);
                }
            }
            next = end + 1;
            return sb.ToString();
        }

        public static List<string> ReadStringArray(string s, int open, out int next)
        {
            var list = new List<string>();
            int close = FindClose(s, open);
            if (close < 0) throw new FormatException("unterminated JSON array at " + open);
            int i = open + 1;
            while (true)
            {
                i = SkipWhitespace(s, i);
                if (i >= close) break;
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') throw new FormatException("expected a string in the array at " + i);
                list.Add(ReadString(s, i, out i));
            }
            next = close + 1;
            return list;
        }
    }
}
