// Formatting helpers: hex, printable strings, safe file names.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InvRe
{
    public static class Text
    {
        public static string Esc(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
                if (c < 32) sb.AppendFormat("\\x{0:X2}", (int)c); else sb.Append(c);
            return sb.ToString();
        }

        public static string Safe(string s)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c < 32) sb.AppendFormat("_x{0:X2}_", (int)c);
                else if (Array.IndexOf(bad, c) >= 0) sb.Append('_');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Hex(byte[] b, int off, int n)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = Math.Max(0, off); i < Math.Min(b.Length, off + n); i++) sb.Append(b[i].ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        // ASCII ("A ...") and UTF-16LE ("U ...") runs of printable characters, keyed by offset.
        public static List<KeyValuePair<int, string>> FindStrings(byte[] b, int minLen)
        {
            List<KeyValuePair<int, string>> hits = new List<KeyValuePair<int, string>>();
            int start = -1;
            for (int i = 0; i <= b.Length; i++)
            {
                bool ok = i < b.Length && b[i] >= 0x20 && b[i] < 0x7F;
                if (ok && start < 0) start = i;
                if (!ok && start >= 0)
                {
                    if (i - start >= minLen) hits.Add(new KeyValuePair<int, string>(start, "A " + Encoding.ASCII.GetString(b, start, i - start)));
                    start = -1;
                }
            }
            for (int phase = 0; phase < 2; phase++)
            {
                start = -1;
                for (int i = phase; i <= b.Length - 1; i += 2)
                {
                    bool ok = i + 1 < b.Length && b[i + 1] == 0 && b[i] >= 0x20 && b[i] < 0x7F;
                    if (ok && start < 0) start = i;
                    if (!ok && start >= 0)
                    {
                        if ((i - start) / 2 >= minLen) hits.Add(new KeyValuePair<int, string>(start, "U " + Encoding.Unicode.GetString(b, start, i - start)));
                        start = -1;
                    }
                }
            }
            hits.Sort((x, y) => x.Key.CompareTo(y.Key));
            return hits;
        }

        public static string Strings(byte[] b, int minLen, int maxLines)
        {
            List<KeyValuePair<int, string>> hits = FindStrings(b, minLen);
            StringBuilder sb = new StringBuilder();
            int lines = 0;
            foreach (KeyValuePair<int, string> h in hits)
            {
                if (lines++ >= maxLines) { sb.AppendFormat("... ({0} more)\n", hits.Count - maxLines); break; }
                sb.AppendFormat("{0:X8}  {1}\n", h.Key, h.Value);
            }
            return sb.ToString();
        }
    }
}
