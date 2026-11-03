// Decodes mirror and pattern features from a DC segment's nodes. They are not Feature (90874D91) nodes but nodes
// of their own types, with the same header (+06 browser node, +0A state, +12 owner, +16 tag, +1A record version).
using System;
using System.Collections.Generic;
using System.Text;

namespace InvRe
{
    public static class Patterns
    {
        public const uint Mirror = 0xFAD9A9B5, Rectangular = 0x20673244, Circular = 0x06977131, SketchDriven = 0xA37B053C;

        public static bool IsPattern(Node n)
        {
            if (n == null || n.Type == null || n.Data == null) return false;
            uint k = n.Type.Key;
            return k == Mirror || k == Rectangular || k == Circular || k == SketchDriven;
        }

        static uint U32(byte[] d, int p) { return BitConverter.ToUInt32(d, p); }

        // After the header: u32 1, an empty kind-2 list, u32 0, then the kind-2 list of what is copied. Returns where
        // that list ends (the type's fields start there), or -1.
        public static int Parents(Node n, out List<int> parents)
        {
            parents = new List<int>();
            byte[] d = n.Data;
            int p = 0x22 + Sketches.Shift(d);
            if (d.Length < p + 8 || U32(d, p) != 0x30000002) return -1;
            List<int> first;
            p = Sketches.ReadList(d, p, out first) + 4;
            if (d.Length < p + 8 || U32(d, p) != 0x30000002) return -1;
            return Sketches.ReadList(d, p, out parents);
        }

        // Every pattern node's fields after its parents list: cross refs in words, other non-zero bytes as hex.
        // Offsets are from the end of the parents list.
        public static string Slots(NodeTable t)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<int, string> names = Sketches.BrowserNames(t);
            foreach (Node n in t.Nodes)
            {
                if (!IsPattern(n)) continue;
                byte[] d = n.Data;
                List<int> parents;
                int q = Parents(n, out parents);
                string name;
                names.TryGetValue(n.Index, out name);
                sb.AppendFormat("  {0} (#{1:X}) {2}, state {3:X2}, tag {4}\n", name ?? "?", n.Index, n.TypeName, d[0x0A], U32(d, 0x16 + Sketches.Shift(d)));
                if (q < 0) { sb.AppendLine("    (no parents list)"); continue; }
                sb.AppendFormat("    parents: {0}\n", string.Join(", ", parents.ConvertAll(i => {
                    string pn; return names.TryGetValue(i, out pn) ? pn : t.At(i) == null ? "?" : t.At(i).TypeName + " #" + i.ToString("X");
                }).ToArray()));
                for (int p = q; p < d.Length; p++)
                {
                    Node target = p + 4 <= d.Length ? t.At(Sketches.XRef(d, p)) : null;
                    if (target != null && target.Type != null)
                    {
                        sb.AppendFormat("    +{0:X2} #{1,-4:X} {2}{3}\n", p - q, target.Index, Features.Property(t, target.Index), Geometry(target));
                        p += 3;
                    }
                    else if (d[p] != 0) sb.AppendFormat("    +{0:X2} byte {1:X2}\n", p - q, d[p]);
                }
            }
            return sb.ToString();
        }

        // Fields after the parents list, at offsets from its end (q):
        //   +00 Transformation (identity so far)   +04 8D6EF0BE   +08 ParameterRDxVar: compute type
        //   +0C Boolean: incremental (circular)    +10 99684A5A   +14 FeatureDimensions
        //   +19 Boolean: of a body                 +25 / +29 ObjectCollection (features / bodies)
        //   +5D u32 number of elements (copies, the original included)
        // then by type, from +69:
        //   mirror       +69 Plane
        //   rectangular  X and Y interleaved: +69/+6D count (RDxVar), +71/+75 spacing, +79/+7D direction,
        //                +81/+85 fitted, +89/+8D unknown, +91/+95 orientation follows the direction, +99/+9D mid-plane
        //   circular     +69 count (RDxVar), +6D angle, +71 Line3D axis, +75 8B2BE62E, +79 reversed, +7D mid-plane,
        //                +81 lock rotation
        public class PatternInfo : Features.FeatureInfo
        {
            public string Kind;   // mirror, rectangular, circular, sketch
            public int Tag;
            public byte State;
            public List<int> ParentNodes = new List<int>();
            public string Parents;
            public bool OfBody;
            public int Compute = -1;
            public int Elements;
            public double[] Origin, Normal;   // mirror plane (cm)
            // Rectangular: [0] X, [1] Y. Circular uses [0] for its count.
            public string[] Count = new string[2], Spacing = new string[2];
            public string[] CountParam = new string[2], SpacingParam = new string[2];   // the parameters' names
            public string AngleParam;
            public int[] N = new int[2];
            public double[] Step = { double.NaN, double.NaN };   // spacing (cm) as entered
            public double[][] Direction = new double[2][];        // unit vector, reversal included
            public bool HasY;
            public bool[] Fitted = new bool[2], Oriented = new bool[2], MidPlane = new bool[2];
            public string Angle;
            public double AngleRad = double.NaN;
            public double[] AxisPoint, AxisDirection;   // cm
            public bool Reversed, Incremental, Lock;
            public List<double[]> Transforms;   // per element: the 3 x 4 matrix by rows (translation in cm); null if unknown

            static string B(bool b) { return b ? "true" : "false"; }

            // The transform of the copy that names call instance k; null if unknown. Mirrors and circular patterns
            // number copies as their elements (1 = the original). Rectangular ones number copy (i, j) along the
            // diagonals: (i + j)(i + j + 1) / 2 + i + 1, so 1, 3, 6, 10, ... along X and 2, 5, 9, ... one row up.
            public double[] Instance(int k)
            {
                if (Transforms == null || k < 1) return null;
                if (Kind != "rectangular") return k <= Transforms.Count ? Transforms[k - 1] : null;
                int nx = N[0], ny = HasY ? N[1] : 1;
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                        if ((i + j) * (i + j + 1) / 2 + i + 1 == k) return i + j * nx < Transforms.Count ? Transforms[i + j * nx] : null;
                return null;
            }

            public override string Get(string key)
            {
                bool rect = Kind == "rectangular", circ = Kind == "circular";
                switch (key)
                {
                    case "plane": return Kind == "mirror" && Normal != null ? Naming.Plane(Normal, Origin) : null;
                    case "ofBody": return B(OfBody);
                    case "parents": return Parents;
                    case "computeType": return Compute == 0 ? "kIdenticalCompute" : Compute == 1 ? "kAdjustToModelCompute" : Compute == 2 ? "kOptimizedCompute" : null;
                    case "elements": return Elements.ToString();
                    case "transforms": return Transforms == null ? "unknown" : Format(Transforms);
                    case "xCount": return rect ? Count[0] : null;
                    case "xSpacing": return rect ? Spacing[0] : null;
                    case "xSpacingType": return rect ? (Fitted[0] ? "kFitted" : "kDefault") : null;
                    case "xMidPlane": return rect ? B(MidPlane[0]) : null;
                    case "yCount": return rect && HasY ? Count[1] : null;
                    case "ySpacing": return rect && HasY ? Spacing[1] : null;
                    case "ySpacingType": return rect && HasY ? (Fitted[1] ? "kFitted" : "kDefault") : null;
                    case "yMidPlane": return rect ? B(MidPlane[1]) : null;
                    case "orientation": return rect ? (Oriented[0] ? "kAdjustToDirection1" : Oriented[1] ? "kAdjustToDirection2" : "kIdentical") : null;
                    case "count": return circ ? Count[0] : null;
                    case "angle": return circ ? Angle : null;
                    case "fitWithinAngle": return circ ? B(!Incremental) : null;
                    case "positioning": return circ ? (Incremental ? "kIncrementalPositioningMethod" : "kFittedPositioningMethod") : null;
                    case "midPlane": return circ ? B(MidPlane[0]) : null;
                    case "lockRotation": return circ ? B(Lock) : null;
                    case "naturalAxis": return circ ? B(!Reversed) : null;
                    case "axis": return circ && AxisPoint != null ? AxisLine(AxisPoint, AxisDirection) : null;
                    default: return null;
                }
            }
        }

        public static string AxisLine(double[] p, double[] d)
        {
            return new Naming.Shape { Kind = "line", A = p, B = new[] { p[0] + d[0], p[1] + d[1], p[2] + d[2] }, Unbounded = true }.Canonical();
        }

        // As the truth writes them: "1: 1 0 0 0, 0 1 0 0, 0 0 1 0; 2: ..." (translation in mm).
        public static string Format(List<double[]> ts)
        {
            List<string> all = new List<string>();
            for (int k = 0; k < ts.Count; k++)
            {
                double[] m = ts[k];
                List<string> rows = new List<string>();
                for (int r = 0; r < 3; r++)
                    rows.Add(string.Join(" ", new[] { N9(m[4 * r]), N9(m[4 * r + 1]), N9(m[4 * r + 2]), N9(10 * m[4 * r + 3]) }));
                all.Add((k + 1) + ": " + string.Join(", ", rows.ToArray()));
            }
            return string.Join("; ", all.ToArray());
        }

        static string N9(double v) { return (Math.Round(v, 9) + 0.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture); }

        // Two transform lists in that form, equal within 1e-6 (and 1e-4 mm); "suppressed" marks are ignored.
        public static bool SameTransforms(string a, string b)
        {
            string[] x = a.Split(';'), y = b.Split(';');
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++)
            {
                string[] p = x[i].Substring(x[i].IndexOf(':') + 1).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                string[] q = y[i].Substring(y[i].IndexOf(':') + 1).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 12 || q.Length != 12) return false;
                for (int k = 0; k < 12; k++)
                    if (Math.Abs(double.Parse(p[k], System.Globalization.CultureInfo.InvariantCulture) - double.Parse(q[k], System.Globalization.CultureInfo.InvariantCulture)) > (k % 4 == 3 ? 1e-4 : 1e-6))
                        return false;
            }
            return true;
        }

        // "line through (a) along (d)": the same line if the directions agree and a lies on the other line.
        public static bool SameAxis(string a, string b)
        {
            double[][] x = ParseAxis(a), y = ParseAxis(b);
            if (x == null || y == null) return a == b;
            double[] d = { y[0][0] - x[0][0], y[0][1] - x[0][1], y[0][2] - x[0][2] };
            double along = d[0] * x[1][0] + d[1] * x[1][1] + d[2] * x[1][2];
            double off = Math.Sqrt(Math.Max(0, d[0] * d[0] + d[1] * d[1] + d[2] * d[2] - along * along));
            return Math.Abs(x[1][0] - y[1][0]) < 1e-6 && Math.Abs(x[1][1] - y[1][1]) < 1e-6 && Math.Abs(x[1][2] - y[1][2]) < 1e-6 && off < 1e-3;
        }

        static double[][] ParseAxis(string s)
        {
            System.Text.RegularExpressions.MatchCollection m = System.Text.RegularExpressions.Regex.Matches(s, "\\(([^)]*)\\)");
            if (!s.StartsWith("line through") || m.Count != 2) return null;
            return new[] {
                Array.ConvertAll(m[0].Groups[1].Value.Split(','), v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)),
                Array.ConvertAll(m[1].Groups[1].Value.Split(','), v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)) };
        }

        static Node Ref(NodeTable t, byte[] d, int p) { return p + 4 <= d.Length ? t.At(Sketches.XRef(d, p)) : null; }

        static bool Flag(NodeTable t, byte[] d, int p)
        {
            Node n = Ref(t, d, p);
            return n != null && n.Type != null && n.Type.Key == Features.Boolean && n.Data[n.Data.Length - 1] != 0;
        }

        // ParameterRDxVar (BB1DD5DF): after the header, {u32 n, UTF-16 name}, u32 0, then the value as u32 (twice).
        static int RdxValue(Node n)
        {
            if (n == null || n.Data == null || n.TypeName != "ParameterRDxVar") return -1;
            byte[] d = n.Data;
            int p = 0x22 + Sketches.Shift(d);
            if (p + 4 > d.Length) return -1;
            p += 4 + 2 * (int)U32(d, p) + 4;
            return p + 4 <= d.Length ? (int)U32(d, p) : -1;
        }

        public static List<PatternInfo> List(NodeTable t)
        {
            Dictionary<int, string> names = Sketches.BrowserNames(t);
            Dictionary<int, Params.Decoded> parameters = new Dictionary<int, Params.Decoded>();
            foreach (Params.Decoded p in Params.Decode(t)) parameters[p.Index] = p;
            // Count variables are tied to their parameter by a 17B3E814 node that cross-refs both.
            Dictionary<int, int> countParam = new Dictionary<int, int>();
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Type.Key != 0x17B3E814 || n.Data == null) continue;
                int rdx = -1, par = -1;
                for (int p = 0x1A; p + 4 <= n.Data.Length; p++)
                {
                    Node r = t.At(Sketches.XRef(n.Data, p));
                    if (r == null || r.Type == null) continue;
                    if (r.TypeName == "ParameterRDxVar") rdx = r.Index;
                    else if (r.Type.Key == Features.Parameter) par = r.Index;
                }
                if (rdx >= 0 && par >= 0) countParam[rdx] = par;
            }
            Func<Node, string> formula = n => { Params.Decoded p; return n != null && parameters.TryGetValue(n.Index, out p) ? p.Formula : null; };
            Func<Node, double> value = n => { Params.Decoded p; return n != null && parameters.TryGetValue(n.Index, out p) ? p.Value : double.NaN; };
            Func<Node, string> paramName = n => { Params.Decoded p; return n != null && parameters.TryGetValue(n.Index, out p) ? p.Name : null; };

            List<PatternInfo> list = new List<PatternInfo>();
            foreach (Node n in t.Nodes)
            {
                if (!IsPattern(n)) continue;
                byte[] d = n.Data;
                int s = Sketches.Shift(d);
                uint k = n.Type.Key;
                PatternInfo pi = new PatternInfo
                {
                    Index = n.Index,
                    Kind = k == Mirror ? "mirror" : k == Rectangular ? "rectangular" : k == Circular ? "circular" : "sketch",
                    State = d[0x0A],
                    Tag = d.Length >= 0x1A + s ? (int)U32(d, 0x16 + s) : -1
                };
                names.TryGetValue(n.Index, out pi.Name);
                int q = Parents(n, out pi.ParentNodes);
                if (q < 0) { list.Add(pi); continue; }
                pi.Parents = string.Join(", ", pi.ParentNodes.ConvertAll(i => { string pn; return names.TryGetValue(i, out pn) ? pn : "#" + i.ToString("X"); }).ToArray());
                pi.Compute = RdxValue(Ref(t, d, q + 0x08));
                pi.Incremental = Flag(t, d, q + 0x0C);
                pi.OfBody = Flag(t, d, q + 0x19);
                // The type's fields start at +69 in 2026 files, after the element count; older files lack the count
                // and 0x20 bytes, so they start at +49. Offsets below are as in 2026 files, shifted by tq.
                int tq = TypeFields(t, n, q, pi.Kind);
                if (tq == 0 && q + 0x61 <= d.Length) pi.Elements = (int)U32(d, q + 0x5D);
                q += tq;
                if (pi.Kind == "mirror")
                {
                    Node pl = Ref(t, d, q + 0x69);
                    if (pl != null && pl.TypeName == "Plane")
                    {
                        int ps = Sketches.Shift(pl.Data);
                        pi.Origin = Vec(pl.Data, 0x36 + ps);
                        double[] u = Vec(pl.Data, 0x4E + ps), v = Vec(pl.Data, 0x66 + ps);
                        if (u != null && v != null) pi.Normal = Unit(Cross(u, v));
                    }
                }
                else if (pi.Kind == "rectangular")
                {
                    for (int a = 0; a < 2; a++)
                    {
                        Node count = Ref(t, d, q + 0x69 + 4 * a), spacing = Ref(t, d, q + 0x71 + 4 * a);
                        if (count == null) continue;
                        if (a == 1) pi.HasY = true;
                        int cp = -1;
                        pi.Count[a] = countParam.TryGetValue(count.Index, out cp) ? formula(t.At(cp)) : null;
                        if (pi.Count[a] != null) pi.CountParam[a] = paramName(t.At(cp));
                        pi.SpacingParam[a] = paramName(spacing);
                        pi.N[a] = RdxValue(count);
                        pi.Spacing[a] = formula(spacing);
                        pi.Step[a] = value(spacing);
                        pi.Direction[a] = DirectionOf(t, Ref(t, d, q + 0x79 + 4 * a));
                        pi.Fitted[a] = Flag(t, d, q + 0x81 + 4 * a);
                        pi.Oriented[a] = Flag(t, d, q + 0x91 + 4 * a);
                        pi.MidPlane[a] = Flag(t, d, q + 0x99 + 4 * a);
                    }
                }
                else if (pi.Kind == "circular")
                {
                    Node count = Ref(t, d, q + 0x69), angle = Ref(t, d, q + 0x6D), axis = Ref(t, d, q + 0x71);
                    int cp = -1;
                    if (count != null) pi.Count[0] = countParam.TryGetValue(count.Index, out cp) ? formula(t.At(cp)) : null;
                    if (pi.Count[0] != null) pi.CountParam[0] = paramName(t.At(cp));
                    pi.AngleParam = paramName(angle);
                    pi.N[0] = RdxValue(count);
                    pi.Angle = formula(angle);
                    pi.AngleRad = value(angle);
                    double[][] line = axis == null ? null : Features.LineOf(t, axis.Index);
                    if (line != null) { pi.AxisPoint = line[0]; pi.AxisDirection = Unit(new[] { line[1][0] - line[0][0], line[1][1] - line[0][1], line[1][2] - line[0][2] }); }
                    pi.Reversed = Flag(t, d, q + 0x79);
                    pi.MidPlane[0] = Flag(t, d, q + 0x7D);
                    pi.Lock = Flag(t, d, q + 0x81);
                }
                pi.Transforms = Transforms(pi);
                if (tq != 0)
                    pi.Elements = pi.Kind == "mirror" ? 2 : pi.Kind == "rectangular" ? Math.Max(1, pi.N[0]) * (pi.HasY ? Math.Max(1, pi.N[1]) : 1) : Math.Max(0, pi.N[0]);
                list.Add(pi);
            }
            return list;
        }

        // Where the type's fields start, relative to +69: 0 in 2026 files, -0x20 in older ones (-1 if not found).
        // Found by the first cross ref to the type's first field (a Plane, or a count variable).
        static int TypeFields(NodeTable t, Node n, int q, string kind)
        {
            byte[] d = n.Data;
            foreach (int off in new[] { 0x69, 0x49 })
            {
                Node r = Ref(t, d, q + off);
                if (r != null && r.Type != null && (kind == "mirror" ? r.TypeName == "Plane" : r.TypeName == "ParameterRDxVar")) return off - 0x69;
            }
            return 0;
        }

        // DirectionAxis: the unit vector at +0x32. DirectionEdge (A244457B): a point at +0x30 and the edge's vector at
        // +0x48; the Boolean it cross-refs at +0x1E is taken as reversed (guess: false in the one example).
        static double[] DirectionOf(NodeTable t, Node n)
        {
            if (n == null || n.Data == null) return null;
            int s = Sketches.Shift(n.Data);
            if (n.TypeName == "DirectionAxis") return Vec(n.Data, 0x32 + s);
            if (n.Type.Key == 0xA244457B)
            {
                double[] v = Vec(n.Data, 0x48 + s);
                if (v == null) return null;
                v = Unit(v);
                return Flag(t, n.Data, 0x1E + s) ? new[] { -v[0], -v[1], -v[2] } : v;
            }
            return null;
        }

        // Each element's transform from the settings: a mirror's reflection; a rectangular grid (X fastest); turns
        // about a circular pattern's axis. Mid-plane patterns alternate sides: 0, +1, -1, +2, ... steps. Unknown
        // (null) for lock rotation (the copies move by a reference point not decoded) and missing settings.
        static List<double[]> Transforms(PatternInfo p)
        {
            List<double[]> ts = new List<double[]>();
            Func<int, bool, double> pos = (k, mid) => mid ? (k % 2 == 1 ? (k + 1) / 2 : -(k / 2)) : k;
            if (p.Kind == "mirror")
            {
                if (p.Normal == null) return null;
                double[] nn = p.Normal;
                double dist = nn[0] * p.Origin[0] + nn[1] * p.Origin[1] + nn[2] * p.Origin[2];
                ts.Add(Identity());
                double[] m = new double[12];
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++) m[4 * r + c] = (r == c ? 1 : 0) - 2 * nn[r] * nn[c];
                    m[4 * r + 3] = 2 * dist * nn[r];
                }
                ts.Add(m);
                return ts;
            }
            if (p.Kind == "rectangular")
            {
                int nx = p.N[0], ny = p.HasY ? p.N[1] : 1;
                if (nx < 1 || ny < 1 || p.Direction[0] == null || double.IsNaN(p.Step[0]) || (p.HasY && (p.Direction[1] == null || double.IsNaN(p.Step[1])))) return null;
                double sx = p.Fitted[0] && nx > 1 ? p.Step[0] / (nx - 1) : p.Step[0];
                double sy = p.HasY ? (p.Fitted[1] && ny > 1 ? p.Step[1] / (ny - 1) : p.Step[1]) : 0;
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        double[] m = Identity();
                        double a = pos(i, p.MidPlane[0]) * sx, b = pos(j, p.MidPlane[1]) * sy;
                        for (int r = 0; r < 3; r++) m[4 * r + 3] = a * p.Direction[0][r] + (p.HasY ? b * p.Direction[1][r] : 0);
                        ts.Add(m);
                    }
                return ts;
            }
            if (p.Kind == "circular")
            {
                int n = p.N[0];
                if (n < 1 || p.Lock || p.AxisPoint == null || double.IsNaN(p.AngleRad)) return null;
                double step = p.Incremental ? p.AngleRad : Math.Abs(Math.Abs(p.AngleRad) - 2 * Math.PI) < 1e-9 ? p.AngleRad / n : n > 1 ? p.AngleRad / (n - 1) : 0;
                for (int k = 0; k < n; k++)
                    ts.Add(Rotation(p.AxisPoint, p.AxisDirection, (p.Reversed ? -1 : 1) * pos(k, p.MidPlane[0]) * step));
                return ts;
            }
            return null;
        }

        static double[] Identity() { return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 }; }

        // Rotation by angle (rad, right hand) about the line through p along unit a.
        public static double[] Rotation(double[] p, double[] a, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle), t = 1 - c;
            double x = a[0], y = a[1], z = a[2];
            double[] r = {
                t * x * x + c,     t * x * y - s * z, t * x * z + s * y,
                t * x * y + s * z, t * y * y + c,     t * y * z - s * x,
                t * x * z - s * y, t * y * z + s * x, t * z * z + c };
            double[] m = new double[12];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) m[4 * i + j] = r[3 * i + j];
                m[4 * i + 3] = p[i] - (r[3 * i] * p[0] + r[3 * i + 1] * p[1] + r[3 * i + 2] * p[2]);
            }
            return m;
        }

        // m applied to a point (cm).
        public static double[] Apply(double[] m, double[] v)
        {
            return new[] {
                m[0] * v[0] + m[1] * v[1] + m[2] * v[2] + m[3],
                m[4] * v[0] + m[5] * v[1] + m[6] * v[2] + m[7],
                m[8] * v[0] + m[9] * v[1] + m[10] * v[2] + m[11] };
        }

        static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }

        static double[] Unit(double[] v)
        {
            double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return l == 0 ? v : new[] { v[0] / l, v[1] / l, v[2] / l };
        }

        // What each mirror and pattern was given, in the form the generator records ("picked"): the mirror plane as
        // "plane plane ..." (a work plane) or "face plane ..." (a face), the axes and directions as "axis line through
        // ..." or "edge line ...". Link nodes tie the copied geometry to its source: 90874D56 (a face's name) or
        // 0C48B861 (a work plane) to the Plane; 618C9E00 (+20 the work axis's Line3D, +2C a Boolean) to a DirectionAxis.
        public static List<string> Picks(NodeTable t)
        {
            Dictionary<int, List<Node>> linkedTo = new Dictionary<int, List<Node>>();
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Data == null || (n.Type.Key != 0x90874D56 && n.Type.Key != 0x0C48B861 && n.Type.Key != 0x618C9E00)) continue;
                for (int p = 0x16; p + 4 <= n.Data.Length; p++)
                {
                    int x = Sketches.XRef(n.Data, p);
                    if (x < 0 || t.At(x) == null) continue;
                    if (!linkedTo.ContainsKey(x)) linkedTo[x] = new List<Node>();
                    linkedTo[x].Add(n);
                }
            }
            Func<int, uint, Node> link = (i, type) => { List<Node> l; return linkedTo.TryGetValue(i, out l) ? l.Find(n => n.Type.Key == type) : null; };
            List<string> picks = new List<string>();
            foreach (PatternInfo p in List(t))
            {
                Node n = t.At(p.Index);
                List<int> parents;
                int q = Parents(n, out parents);
                if (q < 0) continue;
                q += TypeFields(t, n, q, p.Kind);
                if (p.Kind == "mirror" && p.Normal != null)
                {
                    Node pl = Ref(t, n.Data, q + 0x69);
                    picks.Add((pl != null && link(pl.Index, 0x90874D56) != null ? "face " : "plane ") + Naming.Plane(p.Normal, p.Origin));
                }
                else if (p.Kind == "rectangular")
                    for (int a = 0; a < (p.HasY ? 2 : 1); a++)
                    {
                        Node dir = Ref(t, n.Data, q + 0x79 + 4 * a);
                        if (dir == null) continue;
                        if (dir.Type.Key == 0xA244457B)
                        {
                            int s = Sketches.Shift(dir.Data);
                            double[] o = Vec(dir.Data, 0x30 + s), v = Vec(dir.Data, 0x48 + s);
                            if (o != null && v != null) picks.Add("edge " + Naming.Line(o, new[] { o[0] + v[0], o[1] + v[1], o[2] + v[2] }));
                            continue;
                        }
                        Node l = link(dir.Index, 0x618C9E00);
                        double[][] line = l == null ? null : Features.LineOf(t, Sketches.XRef(l.Data, 0x20 + Sketches.Shift(l.Data)));
                        if (line != null) picks.Add("axis " + AxisLine(line[0], Unit(new[] { line[1][0] - line[0][0], line[1][1] - line[0][1], line[1][2] - line[0][2] })));
                    }
                else if (p.Kind == "circular" && p.AxisPoint != null)
                    picks.Add("axis " + AxisLine(p.AxisPoint, p.AxisDirection));
            }
            return picks;
        }

        // What one pattern leaves undecoded, or set where all tests had it clear.
        public static List<string> Gaps(NodeTable t, PatternInfo p)
        {
            List<string> g = new List<string>();
            Node n = t.At(p.Index);
            byte[] d = n.Data;
            List<int> parents;
            int q = Parents(n, out parents);
            if (q < 0) { g.Add("no parents list"); return g; }
            if (p.Name == null) g.Add("no browser name");
            if (!p.OfBody && parents.Count == 0) g.Add("nothing copied");
            if (p.Parents != null && p.Parents.Contains("#")) g.Add("parent without a name");
            if (p.Compute < 0 || p.Compute > 2) g.Add("compute type " + p.Compute);
            if (p.Transforms == null) g.Add(p.Lock ? "lock rotation (transforms)" : "transforms not rebuilt");
            else if (p.Transforms.Count != p.Elements) g.Add("element count differs");
            if (p.Kind == "sketch") g.Add("sketch-driven");
            if (p.Kind == "rectangular" && (Flag(t, d, q + 0x89) || Flag(t, d, q + 0x8D))) g.Add("rectangular +89/+8D set");
            if (Flag(t, d, q + 0x1D)) g.Add("+1D set");
            if (Flag(t, d, q + 0x2D)) g.Add("+2D set");
            List<int> first;
            int p0 = 0x22 + Sketches.Shift(d);
            Sketches.ReadList(d, p0, out first);
            if (first.Count > 0) g.Add("first list not empty");
            if (p.Kind == "rectangular")
                for (int a = 0; a < (p.HasY ? 2 : 1); a++)
                {
                    Node dir = Ref(t, d, q + 0x79 + 4 * a);
                    if (dir != null && dir.TypeName != "DirectionAxis") g.Add("direction " + dir.TypeName);
                    if (p.Count[a] == null) g.Add("count parameter not found");
                }
            if (p.Kind == "circular" && p.Count[0] == null) g.Add("count parameter not found");
            return g;
        }

        public static string Describe(PatternInfo p)
        {
            StringBuilder sb = new StringBuilder();
            string copies = p.Elements + (p.Elements == 1 ? " element" : " elements");
            string of = p.OfBody ? "the body" : p.Parents;
            if (p.Kind == "mirror")
                sb.AppendFormat("mirror of {0} about {1}", of, p.Normal == null ? "?" : Naming.Plane(p.Normal, p.Origin));
            else if (p.Kind == "rectangular")
            {
                sb.AppendFormat("rectangular pattern of {0}: {1} x {2}{3} along {4}", of, p.Count[0] ?? p.N[0].ToString(), p.Spacing[0],
                    p.Fitted[0] ? " (fitted)" : "", Dir(p.Direction[0]));
                if (p.HasY)
                    sb.AppendFormat(", {0} x {1}{2} along {3}", p.Count[1] ?? p.N[1].ToString(), p.Spacing[1], p.Fitted[1] ? " (fitted)" : "", Dir(p.Direction[1]));
                if (p.MidPlane[0] || p.MidPlane[1]) sb.Append(", mid-plane");
            }
            else if (p.Kind == "circular")
            {
                sb.AppendFormat("circular pattern of {0}: {1} {2} {3} about {4}{5}", of, p.Count[0] ?? p.N[0].ToString(), p.Incremental ? "every" : "within",
                    p.Angle, p.AxisPoint == null ? "?" : AxisLine(p.AxisPoint, p.AxisDirection), p.Reversed ? ", reversed" : "");
                if (p.MidPlane[0]) sb.Append(", mid-plane");
                if (p.Lock) sb.Append(", rotation locked");
            }
            else sb.Append("sketch-driven pattern of " + of + " (not decoded)");
            sb.Append("; " + copies);
            if (p.State == 0x04) sb.Append(" (not computed)");
            return sb.ToString();
        }

        static double[] Vec(byte[] d, int p)
        {
            return p + 24 <= d.Length ? new[] { BitConverter.ToDouble(d, p), BitConverter.ToDouble(d, p + 8), BitConverter.ToDouble(d, p + 16) } : null;
        }

        static string Mm(double[] v) { return v == null ? "?" : "(" + string.Join(", ", Array.ConvertAll(v, x => Units.Num(Math.Round(x * 10, 6) + 0.0))) + ")"; }

        static string Dir(double[] v) { return v == null ? "?" : "(" + string.Join(", ", Array.ConvertAll(v, x => Units.Num(Math.Round(x, 6) + 0.0))) + ")"; }

        // Plane (CE52DF42): f64 origin, u, v at +0x36, +0x4E, +0x66 (cm); the normal is u x v.
        // DirectionAxis (CE52DF40): a unit vector at +0x32.
        static string Geometry(Node n)
        {
            byte[] d = n.Data;
            int s = Sketches.Shift(d);
            switch (n.TypeName)
            {
                case "Plane": return ": origin " + Mm(Vec(d, 0x36 + s)) + " u " + Dir(Vec(d, 0x4E + s)) + " v " + Dir(Vec(d, 0x66 + s));
                case "DirectionAxis": return ": " + Dir(Vec(d, 0x32 + s));
                default: return "";
            }
        }
    }
}
