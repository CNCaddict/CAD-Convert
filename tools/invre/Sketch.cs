// Decodes 2D sketches: geometry and constraints, from a DC segment's nodes.
//
// Sketch entities share a header; at +0x26 a cross ref to their Sketch2D.
//   Point2D   +0x2A f64 x, f64 y (cm); lists: end point of, center of
//   Line2D, Circle2D: +0x2A points list {0x30000002, count, capacity, 0x10, start, end, points lying on it...}
//     (start and end are 0 for a full circle), then u32 flag, u32; if flag != 0 two more lists; then
//     Line2D    f64 midpoint x, y; f64 unit direction x, y
//     Circle2D  center (cross ref); f64 radius; u8 1 = full circle, 0 = arc
//   A list with count 0 is just {marker, 0}.
//   +0x22 u32 state: 0x40 = reference (projected) geometry; 0x100000 = point owned by a projected curve
//     (Inventor does not list it as a sketch entity).
// Sketch2D, after the member list: xref transformation, xref direction, u32, u32 hash, then a type-2 list of
// helper points (e.g. intersection points kept for trims) that Inventor does not list as entities either.
// Constraints share a header; +0x1E Group2D, two maps, then [parameter for dimensions], u32 0x10, entities.
// Sketch2D holds the list {0x30000008, count, 0x10, refs} of its entities and constraints.
// Offsets are for Inventor 2026 files. Files from 2023 and earlier lack the u32 at +0x0E, so everything after it
// sits 4 bytes earlier; such nodes are recognised by the owner cross ref already at +0x0E.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InvRe
{
    public static class Sketches
    {
        public const uint Sketch2D = 0x90874D11;
        public const uint Point2D = 0xCE52DF35, Line2D = 0xCE52DF3A, Circle2D = 0xCE52DF3B;
        const uint Group2D = 0x90874D62, Parameter = 0x90874D26, Browser = 0x2B48A42B;

        // Curve types whose fields are not decoded yet; they still count as entities.
        static readonly HashSet<string> OtherCurves = new HashSet<string> {
            "OffsetSpline2D", "EquationCurve2D" };

        static uint U32(byte[] d, int p) { return BitConverter.ToUInt32(d, p); }
        static double F64(byte[] d, int p) { return BitConverter.ToDouble(d, p); }
        static string Num(double v) { return Math.Round(v, 9).ToString("R", CultureInfo.InvariantCulture); }

        public static int XRef(byte[] d, int p)
        {
            uint v = U32(d, p);
            return (v & 0x80000000) != 0 && (v & 0x7FFFFFFF) != 0 ? (int)(v & 0x7FFFFFFF) - 1 : -1;
        }

        // -4 for the shorter pre-2024 header, else 0.
        public static int Shift(byte[] d)
        {
            return d.Length >= 0x12 && (d[0x11] & 0x80) != 0 ? -4 : 0;
        }

        public class Counts { public int Entities, Constraints, Dimensions; }

        public static string Report(NodeTable t, List<Counts> counts)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<int, string> names = BrowserNames(t);
            foreach (Node sk in t.Nodes)
            {
                if (sk.Type == null || sk.Type.Key != Sketch2D || sk.Data == null) continue;
                string name;
                names.TryGetValue(sk.Index, out name);
                List<int> helpers;
                List<int> members = Members(sk.Data, out helpers);
                Counts c = new Counts();
                StringBuilder body = new StringBuilder();
                foreach (int m in members)
                {
                    Node n = t.At(m);
                    if (n == null || n.Data == null || n.Type == null) continue;
                    string line;
                    try
                    {
                        line = Geometry(t, n);
                        if (line != null)
                        {
                            if (helpers.Contains(m)) line += "  [helper point, not listed by Inventor]";
                            else if (Owned(n)) line += "  [owned by a projected curve, not listed by Inventor]";
                            else c.Entities++;
                        }
                        else
                        {
                            line = Constraint(t, n);
                            if (n.TypeName.StartsWith("Dim")) c.Dimensions++; else c.Constraints++;
                        }
                    }
                    catch (Exception ex) { line = n.TypeName + " (not decoded: " + ex.Message + ")"; }
                    body.AppendFormat("    #{0,-4:X} [{1}] {2}\n", n.Index, Text.Hex(n.Data, 0x0A, 4), line);
                }
                counts.Add(c);
                sb.AppendFormat("  {0} (#{1:X}): {2} entities, {3} constraints, {4} dimensions\n", name ?? "sketch", sk.Index, c.Entities, c.Constraints, c.Dimensions);
                sb.Append(body);
            }
            return sb.ToString();
        }

        // Compares decoded entities with Inventor's list (invre truth --entities output), sketch by sketch.
        public static string Compare(NodeTable t, string truthText)
        {
            Regex hexRef = new Regex(@"#[0-9A-F]+\("), extra = new Regex(@" \(\+\d+ points on it\)| from .*$| reference| construction");
            Func<string, string> norm = s => Regex.Replace(extra.Replace(hexRef.Replace(s, "("), ""), @"\s+", " ").Trim();

            Dictionary<string, List<string>> truth = new Dictionary<string, List<string>>();
            List<string> order = new List<string>();
            List<string> cur = null;
            foreach (string raw in truthText.Split('\n'))
            {
                string l = raw.TrimEnd('\r');
                if (l.Length == 0) continue;
                if (!l.StartsWith(" ") && !"{}[]\"".Contains(l.Substring(0, 1))) { cur = new List<string>(); truth[l] = cur; order.Add(l); continue; }
                Match m = Regex.Match(l, @"^\s{2}(\w+)\s+(.*)$");
                if (cur != null && m.Success && !l.StartsWith("   ")) cur.Add(norm(m.Groups[1].Value + " " + m.Groups[2].Value));
            }

            Dictionary<int, string> names = BrowserNames(t);
            Dictionary<string, List<string>> decoded = new Dictionary<string, List<string>>();
            foreach (Node sk in t.Nodes)
            {
                if (sk.Type == null || sk.Type.Key != Sketch2D || sk.Data == null) continue;
                string name;
                if (!names.TryGetValue(sk.Index, out name)) continue;
                List<string> list = new List<string>();
                List<int> helpers;
                foreach (int m in Members(sk.Data, out helpers))
                {
                    Node n = t.At(m);
                    if (n == null || n.Data == null || n.Type == null || helpers.Contains(m) || Owned(n)) continue;
                    string g;
                    try { g = Geometry(t, n); } catch (Exception) { g = n.TypeName + " (not decoded)"; }
                    if (g == null) continue;
                    if (n.Type.Key == Point2D) { Match pm = Regex.Match(g, @"^Point (\S+), (\S+?)( .*)?$"); if (pm.Success) g = "Point (" + pm.Groups[1].Value + ", " + pm.Groups[2].Value + ")"; }
                    list.Add(norm(g));
                }
                decoded[name] = list;
            }

            StringBuilder sb = new StringBuilder();
            foreach (string name in order)
            {
                List<string> left = new List<string>(truth[name]);
                List<string> d;
                if (!decoded.TryGetValue(name, out d)) d = new List<string>();
                List<string> onlyDecoded = new List<string>();
                foreach (string x in d) { if (!left.Remove(x)) onlyDecoded.Add(x); }
                sb.AppendFormat("{0}: Inventor {1}, decoded {2}; unmatched: Inventor {3}, decoded {4}\n", name, truth[name].Count, d.Count, left.Count, onlyDecoded.Count);
                foreach (string x in left) sb.AppendFormat("    only Inventor: {0}\n", x);
                foreach (string x in onlyDecoded) sb.AppendFormat("    only decoded:  {0}\n", x);
            }
            return sb.ToString();
        }

        // Browser entries (2B48A42B) carry the display name and, after the header, a cross ref to the object shown.
        public static Dictionary<int, string> BrowserNames(NodeTable t)
        {
            Dictionary<int, string> r = new Dictionary<int, string>();
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Type.Key != Browser || n.Data == null || n.Data.Length < 0x16) continue;
                string label = NodeTable.Label(n);
                int target = XRef(n.Data, 0x12 + Shift(n.Data));
                if (label != null && target >= 0 && !r.ContainsKey(target)) r[target] = label;
            }
            return r;
        }

        // Type-2 list of 4-byte refs at p: {0x30000002, count, capacity, kind, elements}, or {0x30000002, 0}.
        // Returns the offset after it; elements are node indexes (-1 for none).
        public static int ReadList(byte[] d, int p, out List<int> items)
        {
            items = new List<int>();
            if (U32(d, p) != 0x30000002) throw new Exception(string.Format("no list at +0x{0:X}", p));
            int count = (int)U32(d, p + 4);
            if (count == 0) return p + 8;
            for (int i = 0; i < count; i++)
            {
                uint v = U32(d, p + 16 + 4 * i) & 0x7FFFFFFF;
                items.Add(v == 0 ? -1 : (int)v - 1);
            }
            return p + 16 + 4 * count;
        }

        static uint State(Node n)
        {
            int p = 0x22 + Shift(n.Data);
            return p + 4 <= n.Data.Length ? U32(n.Data, p) : 0;
        }

        static bool Reference(Node n) { return (State(n) & 0x40) != 0; }

        static bool Owned(Node n) { return (State(n) & 0x100000) != 0; }

        // The sketch's member list, and its trailing list of helper points.
        static List<int> Members(byte[] d, out List<int> helpers)
        {
            helpers = new List<int>();
            for (int p = 0; p + 12 <= d.Length; p++)
            {
                if (U32(d, p) != 0x30000008) continue;
                int count = (int)U32(d, p + 4);
                List<int> r = new List<int>();
                for (int i = 0; i < count; i++) r.Add((int)(U32(d, p + 12 + 4 * i) & 0x7FFFFFFF) - 1);
                int after = p + 12 + 4 * count + 16;
                if (after + 8 <= d.Length && U32(d, after) == 0x30000002) ReadList(d, after, out helpers);
                return r;
            }
            return new List<int>();
        }

        static string Pt(NodeTable t, int index)
        {
            Node p = t.At(index);
            if (p == null || p.Data == null || p.Type == null || p.Type.Key != Point2D) return "#" + index.ToString("X");
            int s = Shift(p.Data);
            return string.Format("#{0:X}({1}, {2})", index, Num(F64(p.Data, 0x2A + s)), Num(F64(p.Data, 0x32 + s)));
        }

        static string Geometry(NodeTable t, Node n)
        {
            byte[] d = n.Data;
            int s = Shift(d);
            string g = Shape(t, n, d, s);
            return g != null && Reference(n) ? g + " reference" : g;
        }

        static string Shape(NodeTable t, Node n, byte[] d, int s)
        {
            switch (n.Type.Key)
            {
                case Point2D:
                    return string.Format("Point {0}, {1}", Num(F64(d, 0x2A + s)), Num(F64(d, 0x32 + s)));
                case Line2D:
                case Circle2D:
                    {
                        List<int> pts;
                        int p = ReadList(d, 0x2A + s, out pts);
                        if (U32(d, p) != 0) { List<int> skip; p = ReadList(d, p + 8, out skip); p = ReadList(d, p, out skip); }
                        else p += 8;
                        int start = pts.Count > 0 ? pts[0] : -1, end = pts.Count > 1 ? pts[1] : -1;
                        string on = pts.Count > 2 ? string.Format(" (+{0} points on it)", pts.Count - 2) : "";
                        if (n.Type.Key == Line2D) return string.Format("Line {0} -> {1}{2}", Pt(t, start), Pt(t, end), on);
                        string text = string.Format("{0} center {1} r={2}", d[p + 12] == 1 ? "Circle" : "Arc", Pt(t, XRef(d, p)), Num(F64(d, p + 4)));
                        if (start >= 0) text += string.Format(" from {0} to {1}", Pt(t, start), Pt(t, end));
                        return text + on;
                    }
            }
            if (n.Type.Key == Curves.BSpline || n.Type.Key == Curves.Spline2D || n.Type.Key == Curves.Ellipse2D || n.Type.Key == Curves.Text2D || n.Type.Key == Curves.Image) return Curves.Describe(t, n);
            if (OtherCurves.Contains(n.TypeName)) return n.TypeName + " (fields not decoded, no sample)";
            return null;
        }

        // A sketch entity's 2D geometry (cm), for rebuilding what features made from it:
        //   point: {x, y}; line: {x1, y1, x2, y2}; circle or arc: {cx, cy, r}. Null for other entities.
        public static double[] Geometry2D(NodeTable t, int index)
        {
            Node n = t.At(index);
            if (n == null || n.Data == null || n.Type == null) return null;
            byte[] d = n.Data;
            int s = Shift(d);
            if (n.Type.Key == Point2D) return new[] { F64(d, 0x2A + s), F64(d, 0x32 + s) };
            if (n.Type.Key != Line2D && n.Type.Key != Circle2D) return null;
            List<int> pts;
            int p = ReadList(d, 0x2A + s, out pts);
            if (U32(d, p) != 0) { List<int> skip; p = ReadList(d, p + 8, out skip); p = ReadList(d, p, out skip); }
            else p += 8;
            if (n.Type.Key == Circle2D)
            {
                double[] c = Geometry2D(t, XRef(d, p));
                return c == null ? null : new[] { c[0], c[1], F64(d, p + 4) };
            }
            double[] a = pts.Count > 1 ? Geometry2D(t, pts[0]) : null, b = pts.Count > 1 ? Geometry2D(t, pts[1]) : null;
            return a == null || b == null ? null : new[] { a[0], a[1], b[0], b[1] };
        }

        // A line's or arc's end points (the point list at +0x2A).
        public static List<int> CurvePoints(NodeTable t, int index)
        {
            List<int> pts = new List<int>();
            Node n = t.At(index);
            if (n == null || n.Data == null) return pts;
            int p = 0x2A + Shift(n.Data);
            if (p + 8 <= n.Data.Length && U32(n.Data, p) == 0x30000002) ReadList(n.Data, p, out pts);
            return pts;
        }

        // The sketch's placement: model = M * (x, y, 0, 1), M row-major 4x4. Transformation (90874D18): u32 at +0x22;
        // its low 16 bits mark elements that are 1, its high 16 bits elements that are 0 (both: -1); every other
        // element is an f64, stored in order after it. Found as the first Transformation the Sketch2D cross-refs.
        public static double[] Placement(NodeTable t, int sketch)
        {
            Node sk = t.At(sketch);
            if (sk == null || sk.Data == null) return null;
            for (int q = 0x1A + Shift(sk.Data); q + 4 <= sk.Data.Length; q++)
            {
                Node tr = t.At(XRef(sk.Data, q));
                if (tr == null || tr.TypeName != "Transformation" || tr.Data == null) continue;
                byte[] d = tr.Data;
                int p = 0x22 + Shift(d);
                if (p + 4 > d.Length) return null;
                uint bits = U32(d, p);
                p += 4;
                double[] m = new double[16];
                for (int i = 0; i < 16; i++)
                {
                    bool one = (bits & (1u << i)) != 0, zero = (bits & (1u << (16 + i))) != 0;
                    if (one) m[i] = zero ? -1 : 1;
                    else if (zero) m[i] = 0;
                    else { if (p + 8 > d.Length) return null; m[i] = F64(d, p); p += 8; }
                }
                return m;
            }
            return null;
        }

        // A sketch in structured form, for the converter.
        public class SketchCurve
        {
            public int Node;
            public string Kind;       // point, line, circle, arc, or the type name of a curve the converter can't carry yet
            public bool Reference;    // projected: follows other geometry, not free to move
            public bool Hidden;       // helper points and points owned by projected curves: Inventor doesn't list them
            public double X, Y, R;    // point: position; circle or arc: radius (cm)
            public int Start = -1, End = -1, Center = -1;
            public int Degree;        // spline
            public double[] Knots, Poles;
            public double[] Major;    // ellipse: unit major axis; R and Minor its radii
            public double Minor;
            public List<int> On = new List<int>();   // points lying on it
        }

        public class SketchRule
        {
            public string Kind;       // the node's type name
            public List<int> Items = new List<int>();   // the curves and points, in the file's order
            public string ParamName;  // a dimension's parameter
        }

        public class SketchData
        {
            public int Node;
            public string Name;
            public double[] Placement;
            public List<SketchCurve> Curves = new List<SketchCurve>();
            public List<SketchRule> Rules = new List<SketchRule>();
        }

        public static List<SketchData> ReadAll(NodeTable t)
        {
            List<SketchData> list = new List<SketchData>();
            Dictionary<int, string> names = BrowserNames(t);
            foreach (Node sk in t.Nodes)
            {
                if (sk.Type == null || sk.Type.Key != Sketch2D || sk.Data == null) continue;
                SketchData sd = new SketchData { Node = sk.Index, Placement = Placement(t, sk.Index) };
                names.TryGetValue(sk.Index, out sd.Name);
                List<int> helpers;
                List<int> members = Members(sk.Data, out helpers);
                HashSet<int> entities = new HashSet<int>();
                List<Node> rules = new List<Node>();
                foreach (int m in members)
                {
                    Node n = t.At(m);
                    if (n == null || n.Data == null || n.Type == null) continue;
                    SketchCurve c = ReadCurve(t, n);
                    if (c == null) { rules.Add(n); continue; }
                    c.Hidden = helpers.Contains(m) || Owned(n);
                    sd.Curves.Add(c);
                    entities.Add(m);
                }
                foreach (Node n in rules) sd.Rules.Add(ReadRule(t, n, entities));
                list.Add(sd);
            }
            return list;
        }

        static SketchCurve ReadCurve(NodeTable t, Node n)
        {
            byte[] d = n.Data;
            int s = Shift(d);
            SketchCurve c = new SketchCurve { Node = n.Index, Reference = Reference(n) };
            switch (n.Type.Key)
            {
                case Point2D:
                    c.Kind = "point";
                    c.X = F64(d, 0x2A + s);
                    c.Y = F64(d, 0x32 + s);
                    return c;
                case Line2D:
                case Circle2D:
                    {
                        List<int> pts;
                        int p = ReadList(d, 0x2A + s, out pts);
                        if (U32(d, p) != 0) { List<int> skip; p = ReadList(d, p + 8, out skip); p = ReadList(d, p, out skip); }
                        else p += 8;
                        c.Start = pts.Count > 0 ? pts[0] : -1;
                        c.End = pts.Count > 1 ? pts[1] : -1;
                        for (int i = 2; i < pts.Count; i++) c.On.Add(pts[i]);
                        if (n.Type.Key == Line2D) { c.Kind = "line"; return c; }
                        c.Center = XRef(d, p);
                        c.R = F64(d, p + 4);
                        c.Kind = d[p + 12] == 1 ? "circle" : "arc";
                        return c;
                    }
            }
            if (n.Type.Key == Curves.BSpline)
            {
                Curves.Spline sp = Curves.ReadSpline(n);
                c.Kind = n.TypeName;
                if (sp != null)
                {
                    c.Kind = "spline";
                    c.Degree = sp.Degree;
                    c.Knots = sp.Knots;
                    c.Poles = sp.Poles;
                    c.Start = sp.Points.Count > 0 ? sp.Points[0] : -1;
                    c.End = sp.Points.Count > 1 ? sp.Points[1] : -1;
                }
                return c;
            }
            if (n.Type.Key == Curves.Ellipse2D)
            {
                List<int> pts;
                ReadList(d, 0x2A + s, out pts);
                c.Kind = "ellipse";
                c.Start = pts.Count > 0 ? pts[0] : -1;
                c.End = pts.Count > 1 ? pts[1] : -1;
                c.Center = XRef(d, 0x4A + s);
                c.Major = new[] { F64(d, 0x4E + s), F64(d, 0x56 + s) };
                c.R = F64(d, 0x5E + s);
                c.Minor = F64(d, 0x66 + s);
                return c;
            }
            if (n.Type.Key == Curves.Spline2D || n.Type.Key == Curves.Text2D || n.Type.Key == Curves.Image || OtherCurves.Contains(n.TypeName))
            {
                c.Kind = n.TypeName;
                return c;
            }
            return null;
        }

        static SketchRule ReadRule(NodeTable t, Node n, HashSet<int> entities)
        {
            SketchRule r = new SketchRule { Kind = n.TypeName };
            byte[] d = n.Data;
            for (int p = 0x22 + Shift(d); p + 4 <= d.Length; p++)
            {
                int x = XRef(d, p);
                if (x < 0) continue;
                Node target = t.At(x);
                if (target == null || target.Type == null) continue;
                if (target.Type.Key == Parameter && r.ParamName == null) { r.ParamName = NodeTable.Label(target); p += 3; }
                else if (entities.Contains(x)) { if (!r.Items.Contains(x)) r.Items.Add(x); p += 3; }
            }
            return r;
        }

        // Type name, then the parameter (dimensions) and the entities it references.
        static string Constraint(NodeTable t, Node n)
        {
            StringBuilder sb = new StringBuilder(n.TypeName);
            List<int> seen = new List<int>();
            byte[] d = n.Data;
            for (int p = 0x22 + Shift(d); p + 4 <= d.Length; p++)
            {
                int r = XRef(d, p);
                if (r < 0 || seen.Contains(r)) continue;
                Node target = t.At(r);
                if (target == null || target.Type == null || target.Type.Key == Group2D) continue;
                seen.Add(r);
                if (target.Type.Key == Parameter)
                    sb.AppendFormat(" {0}={1}", NodeTable.Label(target), Params.Expr(t, (int)U32(target.Data, FormulaOffset(target)) - 1));
                else
                    sb.AppendFormat(" {0}#{1:X}", target.TypeName, r);
                p += 3;
            }
            return sb.ToString();
        }

        static int FormulaOffset(Node parameter)
        {
            int end;
            return NodeTable.Label(parameter, out end) == null ? 0 : end + 8;
        }
    }
}
