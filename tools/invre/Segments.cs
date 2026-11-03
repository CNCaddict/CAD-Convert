// Inventor model segments: RSeStorage/M<id> (metadata) + B<id> (data) stream pairs.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InvRe
{
    public class Segment
    {
        static readonly Regex SegmentName = new Regex(@"^[A-Za-z]\w*Segment$");

        public string Name;          // e.g. PmDCSegment
        public string StreamId;      // shared suffix of the M/B stream names
        public string DataCompression, MetaCompression;
        public long DataRawSize, MetaRawSize;
        public byte[] Data;          // decompressed B stream
        public byte[] Meta;          // decompressed part of the M stream (null if none found)

        // An embedded document's path reads "<file>*LocalDocs*<storage>*<name>": a whole document, kept in the file's
        // LocalDocs/<storage>. Returns the path of its streams inside the file ("" for an ordinary file).
        public const string Embedded = "*LocalDocs*";
        public static string Inside(string file, out string outer)
        {
            outer = file;
            int star = file.IndexOf(Embedded, StringComparison.OrdinalIgnoreCase);
            if (star < 0) return "";
            outer = file.Substring(0, star);
            string rest = file.Substring(star + Embedded.Length);
            int end = rest.IndexOf('*');
            return "/LocalDocs/" + (end < 0 ? rest : rest.Substring(0, end));
        }

        // Each document embedded in a file, as a path Segment.ReadAll takes.
        public static List<string> EmbeddedIn(string file)
        {
            List<string> r = new List<string>();
            CfbFile cfb = new CfbFile(file);
            CfbEntry local = cfb.Root.Children.Find(c => c.Name == "LocalDocs");
            if (local == null) return r;
            // Each one's own UFRxDoc starts with its own path, which ends in the name it had.
            foreach (CfbEntry doc in local.Children)
            {
                if (doc.Type != 1) continue;
                string name = doc.Name;
                CfbEntry own = doc.Children.Find(c => c.Name == "UFRxDoc");
                if (own != null)
                {
                    byte[] d = cfb.ReadStream(own);
                    foreach (KeyValuePair<int, string> h in Text.FindStrings(d, 6))
                    {
                        int at = h.Value.IndexOf(Embedded + doc.Name + "*", StringComparison.OrdinalIgnoreCase);
                        if (at < 0) continue;
                        string tail = h.Value.Substring(at + Embedded.Length + doc.Name.Length + 1);
                        foreach (string ext in new[] { ".ipt", ".iam" })
                        {
                            int e = tail.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                            if (e >= 0) { name = tail.Substring(0, e + 4); break; }
                        }
                        break;
                    }
                }
                r.Add(file + Embedded + doc.Name + "*" + name);
            }
            return r;
        }

        public static List<Segment> ReadAll(string file)
        {
            string outer, inside = Inside(file, out outer);
            CfbFile cfb = new CfbFile(outer);
            List<Segment> list = new List<Segment>();
            foreach (CfbEntry m in cfb.Entries)
            {
                if (m.Type != 2 || m.Path == null || !m.Path.StartsWith(inside + "/RSeStorage/M")) continue;
                CfbEntry bEntry = cfb.Find(inside + "/RSeStorage/B" + m.Name.Substring(1));
                if (bEntry == null) continue;

                Segment s = new Segment();
                s.StreamId = m.Name.Substring(1);
                byte[] mraw = cfb.ReadStream(m);
                s.MetaRawSize = mraw.Length;
                s.Name = FindName(mraw) ?? s.StreamId;
                // The meta header is plain text (version, name, timestamps); compressed data follows.
                s.Meta = Codec.DecodeAuto(mraw, 0x20, out s.MetaCompression);

                byte[] braw = cfb.ReadStream(bEntry);
                s.DataRawSize = braw.Length;
                s.Data = Codec.DecodeData(braw, out s.DataCompression);
                list.Add(s);
            }
            return list;
        }

        // The name is {u32 n, UTF-16[n]}; use n, because the GUID after it can look like more text.
        static string FindName(byte[] meta)
        {
            foreach (KeyValuePair<int, string> h in Text.FindStrings(meta, 6))
            {
                if (!h.Value.StartsWith("U ") || h.Key < 4) continue;
                int n = BitConverter.ToInt32(meta, h.Key - 4);
                string s = h.Value.Substring(2);
                if (n > 0 && n <= s.Length && SegmentName.IsMatch(s.Substring(0, n))) return s.Substring(0, n);
            }
            return null;
        }
    }
}
