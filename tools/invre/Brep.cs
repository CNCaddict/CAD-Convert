// Reads ASM (Autodesk ShapeManager, a fork of ACIS) binary data, "ASM BinaryFile4", as found in a part's PmBRep
// segment (the final solid) and in the DC segment (sketch and feature geometry).
//
// Tokens: a tag byte, then
//   02 u8   03 i16   04 i32   05 f32   06 f64   07/12 string (u8 length)   08 string (u16)   09 string (u32)
//   0A true   0B false   0C entity pointer (i32, -1 = none)   0D type name (u8 length)
//   0E type name part (u8 length; joined with the next name by "-", e.g. "cone-surface")
//   0F, 10 subtype open, close   11 end of record   13 position (3 f64)   14 vector (3 f64)   15 enum (i32)
//   16 2D vector (2 f64)
// A type's parts run most specific first: "tedge-edge" is a tolerant edge, "cone-surface" a cone.
// Records are numbered from 0 (the asmheader); pointers use those numbers. Every entity starts with: pointer
// to its first attribute, i32 (history id), and for non-attributes one more pointer (unused so far).
// History (rollback) data sits between "Begin-of-ASM-History-Data" and "End-of-ASM-History-Section", which have
// no terminator: its history_stream and delta_state records are not numbered, and the entities after it (from
// earlier states, e.g. the edges a fillet consumed) continue the numbering of the main section.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InvRe
{
    public class SabRecord
    {
        public int Index;
        public string Type;
        public List<object> Fields = new List<object>();   // int, double, string, bool, SabRef, double[]
        public bool History;   // a history_stream / delta_state record (Index is then its place among those)
        public bool Old;       // an entity after the history section: from an earlier state, not the final body

        public SabRef Ref(int i) { return i < Fields.Count ? Fields[i] as SabRef : null; }
        public int Int(int i) { return i < Fields.Count && Fields[i] is int ? (int)Fields[i] : int.MinValue; }
    }

    public class SabRef
    {
        public int Target;
        public override string ToString() { return Target < 0 ? "$-" : "$" + Target; }
    }

    public static class Sab
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("ASM BinaryFile4");

        public static int Find(byte[] d, int from)
        {
            for (int i = from; i + Magic.Length <= d.Length; i++)
            {
                int k = 0;
                while (k < Magic.Length && d[i + k] == Magic[k]) k++;
                if (k == Magic.Length) return i;
            }
            return -1;
        }

        // The numbered entities of the ASM data starting at or after 'from'; stops at an unknown tag or the end marker.
        // The changes each history state made: (state id, [old record, new record] pairs). In a delta_state, after its
        // id and links, each bulletin is "1 $old $new": $- old is an entity the state made, $- new one it deleted, and
        // both set an entity (numbered new) whose contents before the state were record old.
        public static List<KeyValuePair<int, List<int[]>>> States(List<SabRecord> history)
        {
            List<KeyValuePair<int, List<int[]>>> states = new List<KeyValuePair<int, List<int[]>>>();
            foreach (SabRecord h in history)
            {
                if (h.Type != "delta_state" || h.Int(0) == int.MinValue) continue;
                List<int[]> changes = new List<int[]>();
                for (int i = 8; i + 2 < h.Fields.Count; i++)
                    if (h.Int(i) == 1 && h.Ref(i + 1) != null && h.Ref(i + 2) != null)
                    {
                        changes.Add(new[] { h.Ref(i + 1).Target, h.Ref(i + 2).Target });
                        i += 2;
                    }
                states.Add(new KeyValuePair<int, List<int[]>>(h.Int(0), changes));
            }
            return states;
        }

        // The state that made the record numbered `index` (a body, say); -1 if none did.
        public static int MadeIn(List<SabRecord> history, int index)
        {
            foreach (KeyValuePair<int, List<int[]>> s in States(history))
                foreach (int[] c in s.Value)
                    if (c[0] < 0 && c[1] == index) return s.Key;
            return -1;
        }

        // The records as they were at the end of `state`: every later state undone, newest first. Each place holds the
        // record that was there then (an earlier one, from after the history section, where a state changed it); places
        // a later state made are left empty. Records are shared with `records`, not copied.
        public static List<SabRecord> AsOf(List<SabRecord> records, List<SabRecord> history, int state)
        {
            // Earlier records aren't there at the end; only what an undone state puts back is.
            List<SabRecord> was = records.ConvertAll(r => r.Old ? null : r);
            List<KeyValuePair<int, List<int[]>>> later = States(history).FindAll(s => s.Key > state);
            later.Sort((a, b) => b.Key.CompareTo(a.Key));
            foreach (KeyValuePair<int, List<int[]>> s in later)
                foreach (int[] c in s.Value)
                {
                    int old = c[0], now = c[1];
                    if (now >= 0 && now < was.Count) was[now] = old >= 0 && old < records.Count ? records[old] : null;
                    else if (now < 0 && old >= 0 && old < records.Count) was[old] = records[old];
                }
            return was;
        }

        public static List<SabRecord> Read(byte[] d, int from, out string error)
        {
            List<SabRecord> history;
            return Read(d, from, out error, out history);
        }

        public static List<SabRecord> Read(byte[] d, int from, out string error, out List<SabRecord> history)
        {
            error = null;
            List<SabRecord> records = new List<SabRecord>();
            history = new List<SabRecord>();
            bool inHistory = false, old = false;
            int start = Find(d, from);
            if (start < 0) { error = "no ASM data"; return records; }
            // Records start at the first type name; before it are the header strings and doubles.
            int p = start + Magic.Length;
            while (p + 2 < d.Length && !(d[p] == 0x0D && d[p + 1] > 0 && d[p + 1] < 64 && IsName(d, p + 2, d[p + 1]))) p++;
            SabRecord r = null;
            StringBuilder type = new StringBuilder();
            while (p < d.Length)
            {
                byte tag = d[p++];
                if (r == null) r = new SabRecord { Index = inHistory ? history.Count : records.Count, History = inHistory, Old = old };
                switch (tag)
                {
                    case 0x02: r.Fields.Add((int)d[p]); p += 1; break;
                    case 0x03: r.Fields.Add((int)BitConverter.ToInt16(d, p)); p += 2; break;
                    case 0x04: case 0x15: r.Fields.Add(BitConverter.ToInt32(d, p)); p += 4; break;
                    case 0x05: r.Fields.Add((double)BitConverter.ToSingle(d, p)); p += 4; break;
                    case 0x06: r.Fields.Add(BitConverter.ToDouble(d, p)); p += 8; break;
                    case 0x07: case 0x12: r.Fields.Add(Encoding.UTF8.GetString(d, p + 1, d[p])); p += 1 + d[p]; break;
                    case 0x08: { int n = BitConverter.ToUInt16(d, p); r.Fields.Add(Encoding.UTF8.GetString(d, p + 2, n)); p += 2 + n; break; }
                    case 0x09: { int n = BitConverter.ToInt32(d, p); r.Fields.Add(Encoding.UTF8.GetString(d, p + 4, n)); p += 4 + n; break; }
                    case 0x0A: r.Fields.Add(true); break;
                    case 0x0B: r.Fields.Add(false); break;
                    case 0x0C: r.Fields.Add(new SabRef { Target = BitConverter.ToInt32(d, p) }); p += 4; break;
                    case 0x0D:
                        {
                            string name = Encoding.ASCII.GetString(d, p + 1, d[p]);
                            p += 1 + d[p];
                            if (r.Type == null)
                            {
                                type.Append(name);
                                r.Type = type.ToString();
                                type.Length = 0;
                                // Tolerant topology, for geometry from elsewhere that doesn't quite meet: the plain record's fields,
                                // then its tolerances. To everything that reads these it is that record.
                                if (r.Type == "tcoedge-coedge" || r.Type == "tedge-edge" || r.Type == "tvertex-vertex") r.Type = name;
                            }
                            else r.Fields.Add(name);
                            // Section markers have no terminator.
                            if (r.Type == "Begin-of-ASM-History-Data") { inHistory = true; r = null; }
                            else if (r.Type == "End-of-ASM-History-Section") { inHistory = false; old = true; r = null; }
                            else if (r.Type == "End-of-ASM-data") return records;
                            break;
                        }
                    case 0x0E: type.Append(Encoding.ASCII.GetString(d, p + 1, d[p])).Append('-'); p += 1 + d[p]; break;
                    case 0x0F: r.Fields.Add("{"); break;
                    case 0x10: r.Fields.Add("}"); break;
                    case 0x11:
                        (r.History ? history : records).Add(r);
                        r = null;
                        break;
                    case 0x13: case 0x14:
                        r.Fields.Add(new[] { BitConverter.ToDouble(d, p), BitConverter.ToDouble(d, p + 8), BitConverter.ToDouble(d, p + 16) });
                        p += 24;
                        break;
                    case 0x16: r.Fields.Add(new[] { BitConverter.ToDouble(d, p), BitConverter.ToDouble(d, p + 8) }); p += 16; break;
                    default:
                        error = string.Format("unknown tag {0:X2} at 0x{1:X} in record {2} ({3})", tag, p - 1, records.Count, r.Type);
                        return records;
                }
            }
            return records;
        }

        static bool IsName(byte[] d, int p, int n)
        {
            if (p + n > d.Length) return false;
            for (int i = 0; i < n; i++) if (d[p + i] < 0x20 || d[p + i] > 0x7E) return false;
            return true;
        }

        public static string Field(object f)
        {
            if (f is double) return ((double)f).ToString("R", CultureInfo.InvariantCulture);
            if (f is bool) return (bool)f ? "T" : "F";
            if (f is double[]) return "(" + string.Join(", ", Array.ConvertAll((double[])f, x => Math.Round(x, 9).ToString("R", CultureInfo.InvariantCulture))) + ")";
            if (f is string) return "\"" + f + "\"";
            return f.ToString();
        }

        static SabRecord At(List<SabRecord> records, SabRef r)
        {
            return r != null && r.Target >= 0 && r.Target < records.Count ? records[r.Target] : null;
        }

        // An entity's attributes: its field 0 points to the first; an attribute's field 2 to the next.
        // Attribute fields: 0 own attribute, 1 i32, 2 next, 3 previous, 4 owner, 5 name (custom attributes), data.
        public static List<SabRecord> Attributes(List<SabRecord> records, SabRecord e)
        {
            List<SabRecord> list = new List<SabRecord>();
            SabRecord a = At(records, e.Ref(0));
            while (a != null && list.Count < 100) { list.Add(a); a = At(records, a.Ref(2)); }
            return list;
        }

        // Vertex: field 5 the point; point: field 3 the position (cm).
        public static double[] VertexPosition(List<SabRecord> records, SabRecord v)
        {
            SabRecord pt = v == null ? null : At(records, v.Ref(5));
            return pt != null && pt.Fields.Count > 3 ? pt.Fields[3] as double[] : null;
        }

        // Edge: fields 3 start vertex, 4 start parameter, 5 end vertex, 6 end parameter, 7 coedge, 8 curve.
        public static string Where(List<SabRecord> records, SabRecord e)
        {
            switch (e.Type)
            {
                case "edge":
                    {
                        SabRecord curve = At(records, e.Ref(8));
                        string ends = Pos(VertexPosition(records, At(records, e.Ref(3)))) + " to " + Pos(VertexPosition(records, At(records, e.Ref(5))));
                        // ellipse-curve: centre, normal, major axis (its length the radius), ratio; ratio 1 is a circle.
                        if (curve != null && curve.Type == "ellipse-curve" && curve.Fields.Count > 6 && curve.Fields[5] is double[] && curve.Fields[6] is double)
                        {
                            double[] major = (double[])curve.Fields[5];
                            double radius = Math.Sqrt(major[0] * major[0] + major[1] * major[1] + major[2] * major[2]);
                            string arc = e.Ref(3) != null && e.Ref(5) != null && e.Ref(3).Target == e.Ref(5).Target ? "" : ", " + ends;
                            if (Math.Abs((double)curve.Fields[6] - 1) < 1e-9)
                                return "circle centre " + Pos(curve.Fields[3] as double[]) + " radius " + Field(Math.Round(radius * 10, 6)) + " normal " + Field(curve.Fields[4]) + arc;
                        }
                        return (curve == null ? "?" : curve.Type.Replace("-curve", "")) + " " + ends;
                    }
                case "vertex": return Pos(VertexPosition(records, e));
                case "face":
                    {
                        // Face: field 7 the surface; a plane-surface has origin, normal at fields 3, 4.
                        SabRecord s = At(records, e.Ref(7));
                        if (s == null) return "?";
                        string w = s.Type.Replace("-surface", "");
                        if (s.Type == "plane-surface") w += " at " + Pos(s.Fields[3] as double[]) + " normal " + Field(s.Fields[4]);
                        return w;
                    }
                default: return "";
            }
        }

        static string Pos(double[] v)
        {
            return v == null ? "?" : Field(Array.ConvertAll(v, x => Math.Round(x * 10, 6) + 0.0));
        }

        // Faces, edges and vertices with their naming attributes ("INV_..." custom attributes, data only).
        public static string Names(List<SabRecord> records)
        {
            StringBuilder sb = new StringBuilder();
            foreach (SabRecord e in records)
            {
                if (e.Type != "face" && e.Type != "edge" && e.Type != "vertex") continue;
                sb.AppendFormat("{0,4}{1} {2,-6} {3}\n", e.Index, e.Old ? "*" : " ", e.Type, Where(records, e));
                foreach (SabRecord a in Attributes(records, e))
                {
                    string name = a.Fields.Count > 5 ? a.Fields[5] as string : null;
                    if (name == null || name == "INV_FEATURE_ORIENTATION_ATTR") continue;
                    List<string> data = new List<string>();
                    for (int i = 6; i < a.Fields.Count; i++) data.Add(Field(a.Fields[i]));
                    sb.AppendFormat("         {0} {1}\n", name.Replace("INV_NMX_", ""), string.Join(" ", data.ToArray()));
                }
            }
            return sb.ToString();
        }

        public static string Dump(List<SabRecord> records)
        {
            StringBuilder sb = new StringBuilder();
            foreach (SabRecord r in records)
            {
                sb.AppendFormat("{0,4}{1} {2}", r.Index, r.Old ? "*" : " ", r.Type);
                foreach (object f in r.Fields) sb.Append(' ').Append(Field(f));
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
