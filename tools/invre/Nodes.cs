// Splits a decompressed segment into its nodes, using the block-size and type tables
// in the segment's meta stream.
//
// Meta (decompressed):  7 x u16 header
//                       sizes:  u32 count, count x u32 (bit 31 = present, low bits = data size), u32 section bytes
//                       two more sections (count, entries, u32 section bytes)
//                       types:  u32 count, count x {GUID, u16 version, u32, u16, u32 flags}, u32 section bytes
// Data:                 per present block: u32 header (low byte = type index), data, u32 size again, u8 trailer flag
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InvRe
{
    public class NodeType
    {
        public int Index;
        public Guid Uid;
        public ushort Version;
        public uint Flags;

        // First GUID field; the readable key used by InventorLoader's tables.
        public uint Key { get { return BitConverter.ToUInt32(Uid.ToByteArray(), 0); } }

        public string Name
        {
            get
            {
                string n;
                return NodeNames.Known.TryGetValue(Key, out n) ? n : Key.ToString("X8");
            }
        }
    }

    public class Node
    {
        public int Index;            // position in the size table; references use this number
        public int Offset;           // where the data starts in the decompressed segment
        public uint Header;
        public NodeType Type;
        public byte[] Data;          // null for a size-table entry without a block
        public byte[] Trailer;       // optional named values after the block (rare)
        public string TypeName { get { return Type == null ? "-" : Type.Name; } }
    }

    public class NodeTable
    {
        public List<NodeType> Types = new List<NodeType>();
        public List<Node> Nodes = new List<Node>();
        public string Error;         // null when the whole segment split cleanly
        public int End;              // offset after the last block
        public int DataLength;

        static uint U32(byte[] b, int p) { return BitConverter.ToUInt32(b, p); }

        public static NodeTable Parse(byte[] meta, byte[] data)
        {
            NodeTable t = new NodeTable();
            t.DataLength = data == null ? 0 : data.Length;
            if (meta == null || data == null) { t.Error = "segment not decoded"; return t; }
            try
            {
                int p = FindSizeTable(meta);
                if (p < 0) { t.Error = "size table not found in meta"; return t; }
                List<uint> sizes = new List<uint>();
                int n = (int)U32(meta, p);
                for (int i = 0; i < n; i++) sizes.Add(U32(meta, p + 4 + 4 * i));
                p += 8 + 4 * n;
                p = SkipSection(meta, p, 10);
                p = SkipSection(meta, p, -1);
                if (p < 0) { t.Error = "could not skip meta sections"; return t; }

                int typeStart = p;
                n = (int)U32(meta, p);
                p += 4;
                for (int i = 0; i < n; i++, p += 28)
                {
                    byte[] g = new byte[16];
                    Array.Copy(meta, p, g, 0, 16);
                    t.Types.Add(new NodeType { Index = i, Uid = new Guid(g), Version = BitConverter.ToUInt16(meta, p + 16), Flags = U32(meta, p + 24) });
                }
                if (U32(meta, p) != p - typeStart) { t.Error = "type table size check failed"; return t; }

                int q = 0;
                for (int i = 0; i < sizes.Count; i++)
                {
                    Node node = new Node { Index = i, Offset = -1 };
                    t.Nodes.Add(node);
                    if ((sizes[i] & 0x80000000) == 0) continue;
                    int size = (int)(sizes[i] & 0x7FFFFFFF);
                    node.Header = U32(data, q);
                    node.Offset = q + 4;
                    int ti = (int)(node.Header & 0xFF);
                    if (ti < t.Types.Count) node.Type = t.Types[ti];
                    node.Data = new byte[size];
                    Array.Copy(data, q + 4, node.Data, 0, size);
                    if (U32(data, q + 4 + size) != size)
                    {
                        t.Error = string.Format("block #{0:X} at 0x{1:X}: repeated size does not match", i, q);
                        return t;
                    }
                    q += 8 + size;
                    if (data[q++] != 0)
                    {
                        int start = q;
                        q = SkipTrailer(data, q);
                        if (q < 0)
                        {
                            t.Error = string.Format("block #{0:X}: trailer at 0x{1:X} not understood", i, start - 1);
                            return t;
                        }
                        node.Trailer = new byte[q - start];
                        Array.Copy(data, start, node.Trailer, 0, q - start);
                    }
                }
                t.End = q;
            }
            catch (Exception ex)
            {
                t.Error = ex.Message;
            }
            return t;
        }

        // The size table normally follows a 14-byte header; otherwise search for a self-consistent one.
        static int FindSizeTable(byte[] m)
        {
            if (IsSizeTable(m, 14)) return 14;
            for (int p = 0; p + 8 <= Math.Min(m.Length, 0x100); p++)
                if (IsSizeTable(m, p)) return p;
            return -1;
        }

        static bool IsSizeTable(byte[] m, int p)
        {
            if (p + 8 > m.Length) return false;
            long n = U32(m, p);
            long end = p + 4 + 4 * n;
            return n > 0 && end + 4 <= m.Length && U32(m, (int)end) == end - p;
        }

        // Optional block trailer (after a non-zero flag byte): named values, then a name -> node map.
        //   u32 n (bit 31 set = no entries), n x {u32 len, ASCII key, u32 kind, value sized by kind}
        //   u32 0x30000006, u32 count, [u32 key kind, u32 value kind, count x {u32 len, ASCII name, u32 ref}]
        static int SkipTrailer(byte[] d, int q)
        {
            uint n = U32(d, q);
            q += 4;
            if ((n & 0x80000000) == 0)
            {
                for (uint i = 0; i < n; i++)
                {
                    q += 4 + (int)U32(d, q);
                    uint kind = U32(d, q);
                    q += 4;
                    switch (kind)
                    {
                        case 0x1: q += 3; break;
                        case 0x3: case 0x7: q += 4; break;
                        case 0x8: case 0xA: q += 6; break;
                        case 0xB: q += 10; break;
                        case 0xE: q += 6 + (int)U32(d, q + 2); break;
                        default: return -1;
                    }
                }
            }
            if (U32(d, q) != 0x30000006) return -1;
            uint count = U32(d, q + 4);
            if (count == 0) return q + 8;   // the two kind fields are only written for a non-empty map
            q += 16;
            for (uint i = 0; i < count; i++) q += 8 + (int)U32(d, q);
            return q;
        }

        // A section is {u32 count, entries, u32 byte size of count + entries}.
        static int SkipSection(byte[] m, int p, int entrySize)
        {
            if (p < 0) return -1;
            long n = U32(m, p);
            if (entrySize > 0)
            {
                long end = p + 4 + n * entrySize;
                if (end + 4 <= m.Length && U32(m, (int)end) == end - p) return (int)end + 4;
            }
            for (int e = p + 4; e + 4 <= m.Length; e++)
                if (U32(m, e) == e - p) return e + 4;
            return -1;
        }

        public Node At(int index) { return index >= 0 && index < Nodes.Count ? Nodes[index] : null; }

        // A cross reference: u32 with bit 31 set; the low bits are the node index + 1 (0 = none).
        // Child (owning) references are the same number without bit 31, so they can't be told from plain integers.
        public int RefAt(byte[] d, int off)
        {
            if (off < 0 || off + 4 > d.Length || (d[off + 3] & 0x80) == 0) return -1;
            uint v = U32(d, off) & 0x7FFFFFFF;
            return v >= 1 && v <= Nodes.Count ? (int)v - 1 : -1;
        }

        public string Describe(int index)
        {
            Node n = At(index);
            if (n == null) return "#" + index.ToString("X");
            string label = Label(n);
            return string.Format("#{0:X} {1}{2}", index, n.TypeName, label == null ? "" : " \"" + label + "\"");
        }

        // First length-prefixed UTF-16 string after the node header (u32, u16 id), usually its name.
        // The header's first u32 is usually 0 but can be 1, which with the id reads like a 1-character string.
        public static string Label(Node n)
        {
            int end;
            return Label(n, out end);
        }

        // Also gives the offset just after the label (-1 if none).
        public static string Label(Node n, out int end)
        {
            end = -1;
            if (n.Data == null) return null;
            // With the common DC header (FF FF FF FF then the record version) the name comes right after it;
            // trust that position first, since header flag bytes can look like a short string.
            byte[] d = n.Data;
            foreach (int marker in new[] { 0x1A, 0x16 })
            {
                if (d.Length < marker + 12 || U32(d, marker) != 0xFFFFFFFF) continue;
                int p = marker + 8;
                string name = StringAt(d, p);
                if (name == null) continue;
                end = p + 4 + 2 * name.Length;
                return name;
            }
            foreach (KeyValuePair<int, string> s in Strings16(n.Data))
                if (s.Key >= 6)
                {
                    end = s.Key + 4 + 2 * s.Value.Length;
                    return s.Value;
                }
            return null;
        }

        // {u32 n, n UTF-16 chars} with printable characters.
        public static List<KeyValuePair<int, string>> Strings16(byte[] d)
        {
            List<KeyValuePair<int, string>> r = new List<KeyValuePair<int, string>>();
            for (int i = 0; i + 4 <= d.Length; i++)
            {
                string s = StringAt(d, i);
                if (s == null) continue;
                r.Add(new KeyValuePair<int, string>(i, s));
                i += 3 + 2 * s.Length;
            }
            return r;
        }

        // The {u32 n, n UTF-16 chars} string at p, or null if there isn't a printable one.
        public static string StringAt(byte[] d, int p)
        {
            if (p + 4 > d.Length) return null;
            uint n = U32(d, p);
            if (n == 0 || n > 256 || p + 4 + 2 * n > d.Length) return null;
            for (int k = 0; k < n; k++)
            {
                char c = (char)BitConverter.ToUInt16(d, p + 4 + 2 * k);
                if (c < 0x20 || c == 0xFFFF || char.IsControl(c) || char.IsSurrogate(c)) return null;
            }
            return Encoding.Unicode.GetString(d, p + 4, (int)(2 * n));
        }

        // Hex dump of one node with references, strings and plausible doubles annotated.
        public string View(int index)
        {
            Node n = At(index);
            StringBuilder sb = new StringBuilder();
            if (n == null || n.Data == null) return Describe(index) + ": no data\n";
            byte[] d = n.Data;
            sb.AppendFormat("{0}  type#{1} {2}  header {3:X8}  data @0x{4:X}, {5} bytes\n",
                Describe(index), n.Type == null ? -1 : n.Type.Index, n.Type == null ? "" : n.Type.Uid.ToString(), n.Header, n.Offset, d.Length);
            for (int row = 0; row < d.Length; row += 16)
            {
                StringBuilder ascii = new StringBuilder();
                for (int i = row; i < Math.Min(d.Length, row + 16); i++) ascii.Append(d[i] >= 0x20 && d[i] < 0x7F ? (char)d[i] : '.');
                sb.AppendFormat("  +{0:X4}  {1,-47}  {2}\n", row, Text.Hex(d, row, 16), ascii);
            }
            for (int i = 0; i + 4 <= d.Length; i++)
            {
                int r = RefAt(d, i);
                if (r >= 0) sb.AppendFormat("  ref  +{0:X4} -> {1}\n", i, Describe(r));
            }
            foreach (KeyValuePair<int, string> s in Strings16(d)) sb.AppendFormat("  str  +{0:X4} \"{1}\"\n", s.Key, s.Value);
            for (int i = 0; i + 8 <= d.Length; i++)
            {
                double v = BitConverter.ToDouble(d, i);
                double a = Math.Abs(v);
                if (a >= 1e-6 && a <= 1e6 && !double.IsNaN(v)) sb.AppendFormat(CultureInfo.InvariantCulture, "  f64  +{0:X4} {1:R}\n", i, v);
            }
            return sb.ToString();
        }
    }
}
