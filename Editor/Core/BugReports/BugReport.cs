using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FRGVersionControl.Core.BugReports
{
    /// <summary>
    /// Builds the Markdown bug report the Report a Bug window saves: section caps, code fences,
    /// password scrubbing and log tails. Plain C#, no Unity APIs, so it is unit-testable.
    /// </summary>
    public static class BugReport
    {
        public const int TitleMax = 200;
        public const int DescriptionMax = 6000;
        public const int SectionMax = 12000;
        public const int WizardMax = 8000;
        public const int ReportTarget = 60000;
        public const int GitHubIssueLimit = 65536;
        public const int OpLogLines = 60;
        public const int OpLogReadBytes = 64 * 1024;
        public const string Recipient = "Colton Dyck";
        public const string FolderName = "bug-reports";

        [Serializable]
        public sealed class Section
        {
            public string label;
            public string heading;
            public string body;
            public bool preformatted;

            public Section() { }

            public Section(string label, string heading, string body, bool preformatted)
            {
                this.label = label;
                this.heading = heading;
                this.body = body ?? "";
                this.preformatted = preformatted;
            }
        }

        public static string SavedMessage =>
            $"Report saved and copied to the clipboard. Paste it to {Recipient} on Slack, or attach the file.";

        public static string CleanTitle(string title)
        {
            var t = Regex.Replace(title ?? "", @"\s+", " ").Trim();
            return t.Length > TitleMax ? t.Substring(0, TitleMax) : t;
        }

        public static string Cap(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length > max ? text.Substring(0, max) : text;
        }

        public static string Header(string title, DateTime utc, string reporter, string entryPoint)
        {
            var when = utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return $"# {CleanTitle(title)}\n\nReported {when} UTC by {reporter}, from {entryPoint}.";
        }

        public static string Build(IList<Section> sections)
        {
            var sb = new StringBuilder();
            foreach (var s in sections)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.body)) continue;
                var body = Normalize(s.body).Trim('\n');
                if (!string.IsNullOrEmpty(s.heading)) sb.Append("## ").Append(s.heading).Append("\n\n");
                sb.Append(s.preformatted ? Fence(body) : CloseOpenFence(body)).Append("\n\n");
            }
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        /// <summary>Closes a code fence the reporter's text left open, so later headings still render.</summary>
        public static string CloseOpenFence(string body)
        {
            char marker = '\0';
            int open = 0;
            foreach (var raw in Normalize(body ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length < 3 || (line[0] != '`' && line[0] != '~')) continue;
                int run = 0;
                while (run < line.Length && line[run] == line[0]) run++;
                if (run < 3) continue;
                if (line[0] == '`' && line.IndexOf('`', run) >= 0) continue;
                if (open == 0) { marker = line[0]; open = run; }
                else if (line[0] == marker && run >= open && run == line.Length) { open = 0; }
            }
            return open == 0 ? body : body + "\n" + new string(marker, open);
        }

        public static string Fence(string body)
        {
            body = Normalize(body ?? "").Trim('\n');
            int longest = 0, run = 0;
            foreach (var c in body)
            {
                run = c == '`' ? run + 1 : 0;
                if (run > longest) longest = run;
            }
            var fence = new string('`', Math.Max(3, longest + 1));
            return body.Length == 0 ? fence + "\n" + fence : fence + "\n" + body + "\n" + fence;
        }

        /// <summary>Keeps the newest lines within both limits, prefixed with how many were left out.</summary>
        public static string TailLines(string text, int maxLines, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var lines = Normalize(text).TrimEnd('\n').Split('\n');
            const int reserve = 48;
            int budget = Math.Max(0, maxChars - reserve);
            var kept = new List<string>();
            int used = 0;
            for (int i = lines.Length - 1; i >= 0 && kept.Count < maxLines; i--)
            {
                var line = lines[i];
                if (used + line.Length + 1 > budget)
                {
                    if (kept.Count == 0) kept.Add(line.Substring(line.Length - Math.Min(line.Length, budget)));
                    break;
                }
                kept.Add(line);
                used += line.Length + 1;
            }
            kept.Reverse();
            int omitted = lines.Length - kept.Count;
            var result = string.Join("\n", kept);
            return omitted > 0 ? $"... {omitted} earlier line{(omitted == 1 ? "" : "s")} omitted\n" + result : result;
        }

        /// <summary>The end of a text file that another process may be appending to, starting at a
        /// whole line; null when the file does not exist.</summary>
        public static string ReadTail(string path, int maxBytes)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - maxBytes);
                long from = start > 0 ? start - 1 : 0;
                fs.Seek(from, SeekOrigin.Begin);
                var buffer = new byte[fs.Length - from];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = fs.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                var text = new UTF8Encoding(false).GetString(buffer, 0, read);
                if (start > 0 && text.Length > 0)
                {
                    int nl = text.IndexOf('\n');
                    text = nl < 0 ? text.Substring(1) : text.Substring(nl + 1);
                }
                return text;
            }
        }

        public static string FileName(DateTime localNow) =>
            "FRG-bug-report-" + localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".md";

        private static readonly Regex s_UrlUserInfo = new Regex(
            @"(?<lead>[a-z][a-z0-9+.\-]*://[^\s:/@]+:)[^\s/]*@",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex s_PasswordOption = new Regex(
            @"(?<![\w\-])--password(?![\w\-])(?:\s*=\s*|\s+)(?:""[^""]*""|'[^']*'|\S+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex s_PasswordKey = new Regex(
            @"(?<![A-Za-z0-9_])(?<q>\\?[""']?)(?<key>[A-Za-z0-9_]*(?:password|passwd)|pwd)\k<q>(?![A-Za-z0-9_])(?<sep>\s*[:=]\s*)(?<value>\\""(?:[^""\\]|\\[^""])*\\""|""[^""]*""|'[^']*'|[^\s,;&}]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Scrubs every section body; true when anything was masked.</summary>
        public static bool ScrubSections(IList<Section> sections)
        {
            bool changed = false;
            foreach (var s in sections)
            {
                if (s == null) continue;
                var scrubbed = Scrub(s.body);
                if (scrubbed != s.body) { s.body = scrubbed; changed = true; }
            }
            return changed;
        }

        /// <summary>Masks password-like values: URL user info, --password options and password keys.</summary>
        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var s = s_UrlUserInfo.Replace(text, m => m.Groups["lead"].Value + "***@");
            s = s_PasswordOption.Replace(s, "--password ***");
            s = s_PasswordKey.Replace(s, m =>
            {
                var value = m.Groups["value"].Value;
                var masked = value.StartsWith("\\\"") ? "\\\"***\\\"" : value.StartsWith("\"") ? "\"***\"" : value.StartsWith("'") ? "'***'" : "***";
                return m.Groups["q"].Value + m.Groups["key"].Value + m.Groups["q"].Value + m.Groups["sep"].Value + masked;
            });
            return s;
        }

        private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
