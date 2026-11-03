// Assemblies. An occurrence is split over two segments, tied by a number:
//   AmDcSegment: 90874D61 (+0x06 its browser node, whose string is the occurrence's name; +0x0D bit 0x02 grounded) -> 90874D60
//     "DCx" (the number just before the string; then the 90874D61 and a CB072B3B whose +0x46 byte is 0 when suppressed).
//   AmRxSegment: root EC60C64B (+0x06 a kind-2 list) -> 232792BC (+0x06 FDCD32FE; a packed Transformation at +0x0A or +0x0E) ->
//     FDCD32FE (+0x08 the number). Parts-list rows 08B9DB46 list the numbers of the occurrences of each referenced file.
//   UFRxDoc stream: each referenced file's path and index.
//   DesignViewSegment: 71C22321 lists the numbers of the occurrences the view hides.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace InvRe
{
    public class Occurrence
    {
        public string Name, File, Path;
        public int Number;
        public uint RxId;     // the u32 ending its 232792BC: a flexible placement in a parent assembly names the child by it
        public double[] Matrix;
        public bool Grounded, Visible = true, Suppressed, Flexible;
        public int Dc = -1, Rx = -1;
    }

    // A constraint. Constraint (4E86F047): +0x06 browser node, +0x0C bit 0x40 and +0x0D bit 0x01 settings, +0x22 u8 type, +0x23 a
    // kind-2 list of B91FCE52 {+0x06 the occurrence (90874D61), +0x0A its geometry, in the part's coordinates}, a kind-2 list
    // of 258EC6E1 {+0x08 a Parameter}, then u8 flags (0x03 failing, 0x04 suppressed), u32 number of its default name, u32 settings
    // (0x2000 locked rotation), u8.
    public class MateItem
    {
        public string Name, Kind;
        public int Type;
        public string Health = "kUpToDateHealth";              // from the flags: 1 inconsistent, 3 driver lost
        public string Shown;                                   // the name in the browser segment, where one is stored
        public int Number;                                     // the number in its default name
        public bool Bit40, Bit01, Suppressed, Failing, Nested, FromImate;
        public uint Settings;                                 // 0x2000 locked rotation; joints: see JointFields
        public bool IsJoint { get { return Type >= 0x0E && Type <= 0x13; } }
        public List<int> Occurrences = new List<int>();        // 90874D61 nodes; -1 for a plane of the assembly
        public List<string> Geometry = new List<string>();     // in the assembly's coordinates, as the truth's text
        public List<double[]> Axes = new List<double[]>();     // for a joint, each origin's Z; else null
        public List<string> Parameters = new List<string>();
        public List<double> Values = new List<double>();       // the parameters' values (cm, radians)
        public List<GripItem> Grips = new List<GripItem>();    // what each geometry grips in its part, in order
    }

    // A constraint's grip on a part. The geometry has an id node: a string "PmDCSegment::...", the geometry, then {naming table, entry}.
    public class GripItem
    {
        public string Geometry, File, Why;                     // Why: the reason nothing could be looked up
        public string Work;                                    // a work feature: "kWorkPlaneObject XY Plane"
        public string Target;                                  // the type of the node the id's string points at
        public bool ByGeometry;                                // chosen among a named face's edges by the geometry
        public Naming.Grip Grip;
        public List<Naming.Match> Exact = new List<Naming.Match>();   // those of Grip.Found that carry the constraint's geometry
    }

    public static class Assemblies
    {
        public const uint OccurrenceDc = 0x90874D61, ComponentDc = 0x90874D60, Bounds = 0xCB072B3B, Root = 0xEC60C64B, OccurrenceRx = 0x232792BC, ComponentRx = 0xFDCD32FE, Hidden = 0x71C22321, PartsRow = 0x08B9DB46;

        static uint U32(byte[] d, int p) { return BitConverter.ToUInt32(d, p); }
        static double F64(byte[] d, int p) { return BitConverter.ToDouble(d, p); }
        static string Tok(double v) { return (Math.Round(v, 6) + 0.0).ToString("R", CultureInfo.InvariantCulture); }

        // A packed Transformation (see Sketches.Placement) at p: its last row is always 0 0 0 1. Null if it isn't one.
        static double[] Packed(byte[] d, int p)
        {
            if (p + 4 > d.Length) return null;
            uint bits = U32(d, p);
            if ((bits & 0x70008000) != 0x70008000) return null;
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

        // Each referenced file's path, in file order: u32 length, UTF-16 path; the first is the file itself.
        public static List<string> References(string file)
        {
            List<string> r = new List<string>();
            string outer, holder, inside = Segment.Inside(file, out outer);
            CfbFile cfb = new CfbFile(outer);
            CfbEntry doc = cfb.Find(inside + "/UFRxDoc");
            if (doc == null) return r;
            byte[] d = cfb.ReadStream(doc);
            for (int p = 0; p + 8 < d.Length; p++)
            {
                int n = (int)U32(d, p);
                if (n < 5 || n > 1000 || p + 4 + 2 * n + 4 + 18 + 32 + 4 > d.Length) continue;
                int end = p + 4 + 2 * n;
                string path = Encoding.Unicode.GetString(d, p + 4, 2 * n);
                if (!path.EndsWith(".ipt", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".iam", StringComparison.OrdinalIgnoreCase)) continue;
                // An embedded document lives in this very file, whatever the file was called when that path was saved.
                int star = path.IndexOf(Segment.Embedded, StringComparison.OrdinalIgnoreCase);
                if (star >= 0 && cfb.Find(Segment.Inside(path, out holder)) != null) path = outer + path.Substring(star);
                r.Add(path);
                p = end;
            }
            return r;
        }

        // Which reference each occurrence number places: the i-th parts-list row 08B9DB46 is the i-th reference. A row is
        // {kind-2 list of A20D9DC5 (raw ids), name string, u32, u32}, and each A20D9DC5 is a kind-2 list of occurrence numbers.
        static Dictionary<int, int> RowsOf(NodeTable rx)
        {
            Dictionary<int, int> r = new Dictionary<int, int>();
            int position = 0;
            foreach (Node row in rx.Nodes)
            {
                if (row.Type == null || row.Type.Key != PartsRow || row.Data == null) continue;
                byte[] d = row.Data;
                int count = (int)U32(d, 4);
                position++;
                for (int i = 0; i < count; i++)
                {
                    Node member = rx.At((int)U32(d, 16 + 4 * i) - 1);
                    if (member == null || member.Data == null || member.Type == null || member.Type.Key != 0xA20D9DC5) continue;
                    int numbers = (int)U32(member.Data, 4);
                    for (int k = 0; k < numbers && 16 + 4 * k + 4 <= member.Data.Length; k++) r[(int)U32(member.Data, 16 + 4 * k)] = position;
                }
            }
            return r;
        }

        // rx and view may be null (older or partial files); the file's path gives the references.
        public static List<Occurrence> Read(NodeTable dc, NodeTable rx, NodeTable view, string file)
        {
            Dictionary<int, Occurrence> byNumber = new Dictionary<int, Occurrence>();
            List<Occurrence> all = new List<Occurrence>();
            foreach (Node n in dc.Nodes)
            {
                if (n.Type == null || n.Type.Key != ComponentDc || n.Data == null) continue;
                Node occ = null, bounds = null;
                for (int p = 0x16; p + 4 <= n.Data.Length; p++)
                {
                    if (n.Data[p + 3] != 0x80) continue;
                    Node r = dc.At(Sketches.XRef(n.Data, p));
                    if (r == null || r.Type == null) continue;
                    if (r.Type.Key == OccurrenceDc && occ == null) occ = r;
                    if (r.Type.Key == Bounds && bounds == null) bounds = r;
                }
                if (occ == null) continue;
                int number = -1;
                for (int p = 0x20; p + 10 <= n.Data.Length; p++)
                    if (U32(n.Data, p) == 3 && n.Data[p + 4] == (byte)'D' && n.Data[p + 6] == (byte)'C' && n.Data[p + 8] == (byte)'x') { number = (int)U32(n.Data, p - 4); break; }
                int s = Sketches.Shift(occ.Data);
                Occurrence o = new Occurrence { Number = number, Dc = occ.Index };
                Node browser = dc.At(Sketches.XRef(occ.Data, 6));
                o.Name = browser == null ? null : NodeTable.Label(browser);
                o.Grounded = (occ.Data[0x0D] & 0x02) != 0;
                if (bounds != null) o.Suppressed = bounds.Data[0x46 + Sketches.Shift(bounds.Data)] == 0;
                all.Add(o);
                byNumber[number] = o;
            }
            if (rx != null)
                foreach (Node n in rx.Nodes)
                {
                    if (n.Type == null || n.Type.Key != OccurrenceRx || n.Data == null) continue;
                    Node c = rx.At(Sketches.XRef(n.Data, 6));
                    if (c == null || c.Data == null || c.Data.Length < 16 || n.Data.Length < 16) continue;
                    int number = (int)U32(c.Data, 8);
                    Occurrence o;
                    if (!byNumber.TryGetValue(number, out o)) { o = new Occurrence { Number = number }; all.Add(o); byNumber[number] = o; }
                    o.Rx = n.Index;
                    o.Matrix = Packed(n.Data, 0x0A) ?? Packed(n.Data, 0x0E);
                    o.Flexible = (U32(n.Data, n.Data.Length - 16) & 4) != 0;   // ends: this u32, number, 0, a u32 id
                    o.RxId = U32(n.Data, n.Data.Length - 4);
                    // Empty bounds mean suppressed, except on a flexible sub-assembly: it never has any.
                    if (o.Flexible) o.Suppressed = false;
                }
            if (view != null)
                foreach (Node n in view.Nodes)
                {
                    if (n.Type == null || n.Type.Key != Hidden || n.Data == null || n.Data.Length < 0x0E) continue;
                    int count = (int)U32(n.Data, 0x0A);
                    for (int i = 0; i < count && 0x16 + 4 * i + 4 <= n.Data.Length; i++)
                    {
                        Occurrence o;
                        if (byNumber.TryGetValue((int)U32(n.Data, 0x16 + 4 * i), out o)) o.Visible = false;
                    }
                }
            List<string> refs = References(file);
            Dictionary<int, int> rows = rx == null ? new Dictionary<int, int>() : RowsOf(rx);
            // Some files have no rows (the parts list was never built). There, with one occurrence per file, they run in order.
            if (rows.Count == 0 && all.Count == refs.Count - 1)
                for (int i = 0; i < all.Count; i++) rows[all[i].Number] = i + 1;
            foreach (Occurrence o in all)
            {
                int index;
                if (rows.TryGetValue(o.Number, out index) && index < refs.Count) { o.Path = refs[index]; o.File = o.Path.Substring(o.Path.LastIndexOfAny(new[] { '\\', '/' }) + 1); }
            }
            return all;
        }

        public static string MatrixText(double[] m)
        {
            if (m == null) return "?";
            List<string> t = new List<string>();
            for (int i = 0; i < 12; i++) t.Add(Tok(i % 4 == 3 ? m[i] : m[i]));
            return string.Join(" ", t.ToArray());
        }

        public static string Report(List<Occurrence> all)
        {
            StringBuilder b = new StringBuilder();
            foreach (Occurrence o in all)
                b.AppendFormat("  {0} [{1}] {2}{3}{4}{5}{6}\n      {7}\n", o.Name ?? "?", o.Number, o.File ?? "(file not found)", o.Grounded ? " grounded" : "", o.Visible ? "" : " hidden", o.Suppressed ? " suppressed" : "", o.Flexible ? " flexible" : "", MatrixText(o.Matrix));
            return b.ToString();
        }

        // Every occurrence of one assembly checked against what the file says elsewhere.
        public static void Check(List<Occurrence> all, string part, HashSet<string> filesThere, List<string> bad, SortedDictionary<string, int> tally, HashSet<string> notThere, List<string> differ)
        {
            Action<string> count = key => { int c; tally.TryGetValue(key, out c); tally[key] = c + 1; };
            HashSet<int> numbers = new HashSet<int>();
            foreach (Occurrence o in all)
            {
                string where = part + " " + (o.Name ?? "#" + o.Number) + ": ";
                count("occurrences");
                if (!numbers.Add(o.Number)) bad.Add(where + "number " + o.Number + " used twice");
                if (o.Dc < 0) { count("in the Rx segment only"); bad.Add(where + "no DC node"); }
                if (o.Rx < 0) { count("in the DC segment only"); bad.Add(where + "no Rx node"); }
                if (o.Matrix == null) bad.Add(where + "no matrix");
                else
                {
                    // The rotation part must be a true rotation.
                    double[] m = o.Matrix;
                    double worst = 0;
                    for (int a = 0; a < 3; a++)
                        for (int b = a; b < 3; b++)
                        {
                            double dot = m[4 * a] * m[4 * b] + m[4 * a + 1] * m[4 * b + 1] + m[4 * a + 2] * m[4 * b + 2];
                            worst = Math.Max(worst, Math.Abs(dot - (a == b ? 1 : 0)));
                        }
                    if (worst > 1e-6) bad.Add(where + "matrix is not a rotation (off by " + worst.ToString("G3") + ")");
                    else count("matrices that are rotations");
                }
                if (o.File == null) { count("file not found"); bad.Add(where + "no referenced file"); }
                else
                {
                    count("files read");
                    if (filesThere.Contains(o.File)) count("files present in the folder"); else { count("files not in the folder"); notThere.Add(o.File); }
                    // Occurrences are named after their file unless renamed: a check on the file, as nothing else says.
                    string stem = o.File.Substring(0, o.File.LastIndexOf('.'));
                    if (o.Name != null && o.Name.StartsWith(stem + ":", StringComparison.OrdinalIgnoreCase)) count("names that start with their file's name");
                    else { count("names that differ from their file's"); differ.Add(part + ": " + o.Name + " in " + o.File); }
                }
                if (o.Grounded) count("grounded");
                if (!o.Visible) count("hidden");
                if (o.Suppressed) count("suppressed");
                if (o.Flexible) count("flexible");
            }
        }

        public const uint ConstraintNode = 0x4E86F047, ConstraintArgument = 0xB91FCE52, ConstraintParameter = 0x258EC6E1, ConstraintFolder = 0xEA6894C1;

        static readonly Dictionary<int, string> Kinds = new Dictionary<int, string> {
            { 1, "kMateConstraintObject" }, { 2, "kFlushConstraintObject" }, { 3, "kAngleConstraintObject" }, { 4, "kTangentConstraintObject" },
            { 5, "kInsertConstraintObject" }, { 6, "kRotateRotateConstraintObject" }, { 7, "kRotateTranslateConstraintObject" }, { 0x14, "kAssemblySymmetryConstraintObject" } };

        static double[] Apply(double[] m, double x, double y, double z, double w)
        {
            if (m == null) return new[] { x, y, z };
            return new[] { m[0] * x + m[1] * y + m[2] * z + m[3] * w, m[4] * x + m[5] * y + m[6] * z + m[7] * w, m[8] * x + m[9] * y + m[10] * z + m[11] * w };
        }

        static string V3(double[] v) { return Tok(v[0]) + " " + Tok(v[1]) + " " + Tok(v[2]); }

        static double[] Unit(double[] v)
        {
            double n = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return n == 0 ? v : new[] { v[0] / n, v[1] / n, v[2] / n };
        }

        // A Plane, Line3D, Circle3D or Point3D node, moved by m, as the truth's text.
        public static string GeometryOf(Node g, double[] m)
        {
            if (g == null || g.Data == null || g.Type == null) return "?";
            byte[] d = g.Data;
            int s = Sketches.Shift(d);
            Func<int, int, double[]> at = (p, w) => Apply(m, F64(d, p + s), F64(d, p + 8 + s), F64(d, p + 16 + s), w);
            switch (g.TypeName)
            {
                case "Plane":
                {
                    double[] u = at(0x4E, 0), v = at(0x66, 0);
                    return "plane " + V3(at(0x36, 1)) + " normal " + V3(Unit(new[] { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] }));
                }
                case "Line3D": return "line " + V3(at(0x3A, 1)) + " direction " + V3(Unit(at(0x52, 0)));
                case "Point3D": return "point " + V3(at(0x32, 1));
                case "DirectionAxis": return "point " + V3(Unit(at(0x32, 0)));   // a direction; the truth prints it as a point
                case "Circle3D":
                {
                    // After a kind-2 list at +0x32, which is usually two u32s but can be empty.
                    int after = U32(d, 0x32 + s) == 0x30000002 && U32(d, 0x36 + s) == 0 ? 0x3A : 0x4A;
                    return "circle " + V3(at(after, 1)) + " axis " + V3(Unit(at(after + 0x18, 0))) + " r " + Tok(F64(d, after + 0x48 + s));
                }
                case "4116DA9E": return "cylinder " + V3(at(0x42, 1)) + " axis " + V3(Unit(at(0x5A, 0))) + " r " + Tok(F64(d, 0x3A + s));
                case "E70647C4": return "sphere " + V3(at(0x3A, 1)) + " r " + Tok(F64(d, 0x82 + s));
                case "E70647C3": return "torus " + V3(at(0x4A, 1)) + " axis " + V3(Unit(at(0x62, 0))) + " radii " + Tok(F64(d, 0x3A + s)) + " " + Tok(F64(d, 0x42 + s));
                case "CAFE99DF": return "bspline";
                case "55CE9B23":   // a joint origin: a packed Transformation, in the part's coordinates
                {
                    double[] frame = Packed(d, 0x32 + s);
                    return frame == null ? "?" : "point " + Tok(frame[3]) + " " + Tok(frame[7]) + " " + Tok(frame[11]);
                }
            }
            return g.TypeName;
        }

        // A joint origin's Z, in the assembly's coordinates, like its point. Null for anything else.
        public static double[] JointAxis(Node g)
        {
            if (g == null || g.Data == null || g.Type == null || g.TypeName != "55CE9B23") return null;
            double[] frame = Packed(g.Data, 0x32 + Sketches.Shift(g.Data));
            return frame == null ? null : new[] { frame[2], frame[6], frame[10] };
        }

        static double[] Compose(double[] a, double[] b)
        {
            if (a == null) return b;
            if (b == null) return a;
            double[] m = new double[16];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    for (int k = 0; k < 4; k++) m[4 * r + c] += a[4 * r + k] * b[4 * k + c];
            return m;
        }

        // A geometry node's +0x26 is a 375EAEE5 {"DCx", a path string of occurrence numbers such as "65:15", ...}. Inside a
        // sub-assembly the path has more than one number, and a kind-2 list then holds the placement in the top occurrence.
        static double[] InnerMatrix(NodeTable dc, Node g)
        {
            if (g == null || g.Data == null) return null;
            Node path = dc.At(Sketches.XRef(g.Data, 0x26 + Sketches.Shift(g.Data)));
            if (path == null || path.Data == null || path.Type == null || path.Type.Key != 0x375EAEE5) return null;
            byte[] d = path.Data;
            for (int p = 0x20; p + 16 < d.Length; p++)
            {
                if (U32(d, p) != 0x30000002) continue;
                int count = (int)U32(d, p + 4);
                if (count <= 0) return null;
                double[] m = null;
                int q = p + 12;
                for (int i = 0; i < count && q + 8 < d.Length; i++)
                {
                    uint size = U32(d, q);
                    double[] one = Packed(d, q + 4);
                    if (one == null) return m;
                    int nonTrivial = 0;
                    uint bits = U32(d, q + 4);
                    for (int b = 0; b < 16; b++) if ((bits & (1u << b)) == 0 && (bits & (1u << (16 + b))) == 0) nonTrivial++;
                    m = Compose(m, one);
                    q += 8 + 8 * nonTrivial;
                }
                return m;
            }
            return null;
        }

        // A flexible occurrence's children sit where its own assembly doesn't put them. Each F17A4562 in the Rx segment holds a kind-2
        // list of occurrence numbers (the path to a flexible sub-assembly) and a kind-2 list of 2238DCC5 {+0x06 the id ending the
        // child's 232792BC in the sub-assembly's own file, +0x0A u32 2, +0x0E a packed Transformation within that sub-assembly}.
        // Keyed by the path and that id, as "65#231".
        public static Dictionary<string, double[]> FlexiblePlacements(NodeTable rx)
        {
            Dictionary<string, double[]> r = new Dictionary<string, double[]>();
            if (rx == null) return r;
            foreach (Node n in rx.Nodes)
            {
                if (n.Type == null || n.Type.Key != 0xF17A4562 || n.Data == null) continue;
                byte[] d = n.Data;
                int count = (int)U32(d, 0x0A), p = 0x16;
                List<string> path = new List<string>();
                for (int i = 0; i < count && p + 4 <= d.Length; i++, p += 4) path.Add(((int)U32(d, p)).ToString());
                List<int> children;
                if (p + 8 > d.Length) continue;
                Sketches.ReadList(d, p, out children);
                foreach (int c in children)
                {
                    Node child = rx.At(c);
                    if (child == null || child.Data == null || child.Data.Length < 0x14) continue;
                    double[] m = Packed(child.Data, 0x0E);
                    if (m != null) r[string.Join(":", path.ToArray()) + "#" + U32(child.Data, 6)] = m;
                }
            }
            return r;
        }

        static readonly Dictionary<int, string> NameWords = new Dictionary<int, string> {
            { 1, "Mate" }, { 2, "Flush" }, { 3, "Angle" }, { 4, "Tangent" }, { 5, "Insert" }, { 6, "Rotation" }, { 7, "Rotation/Translation" }, { 0x14, "Symmetry" },
            { 0x0E, "Rotational" }, { 0x0F, "Slider" }, { 0x10, "Rigid" }, { 0x11, "Ball" }, { 0x12, "Cylindrical" }, { 0x13, "Planar" } };

        // A default name: a word for the type, and the number stored after the flags.
        public static string DefaultName(MateItem c)
        {
            string word;
            return NameWords.TryGetValue(c.Type, out word) ? word + ":" + c.Number : null;
        }

        // A geometry's id node. Its string "PmDCSegment::<stream>:<id>:N" ends in the id (the u16 at +0x04) of a node in that part's
        // segment: the solid body, for an entity named through the naming table, or the work feature itself. An id node for one
        // of the assembly's own work features has no string; it refers to the feature's node instead.
        public class GripId
        {
            public Node Table;          // and Entry: the name of a face, edge or vertex
            public int Entry;
            public int PartNode = -1;   // N, the id of the node in the part
            public Node OwnNode;        // a work feature of the assembly itself
        }

        static readonly HashSet<string> GeometryTypes = new HashSet<string> { "Plane", "Line3D", "Point3D", "DirectionAxis", "Circle3D", "4116DA9E", "E70647C4", "E70647C3", "CAFE99DF" };

        public static Dictionary<int, GripId> GripIds(NodeTable dc)
        {
            Dictionary<int, GripId> r = new Dictionary<int, GripId>();
            foreach (Node n in dc.Nodes)
            {
                if (n.Data == null || n.Type == null) continue;
                byte[] d = n.Data;
                int at;
                string label = NodeTable.Label(n, out at);
                bool outside = label != null && label.Contains("Segment::");
                bool work = n.Type.Key == 0x0C48B861 || n.Type.Key == 0x0C48B860 || n.Type.Key == 0xA76B22A0;   // plane, point, axis
                if (!outside && !work) continue;
                GripId id = new GripId();
                if (outside && !int.TryParse(label.Substring(label.LastIndexOf(':') + 1), out id.PartNode)) id.PartNode = -1;
                int geometry = -1, s = Sketches.Shift(d);
                if (work && d.Length >= 0x22 + s)
                {
                    Node x = dc.At(Sketches.XRef(d, 0x1E + s));
                    if (x != null && x.Type != null && GeometryTypes.Contains(x.TypeName)) geometry = x.Index;
                    if (!outside && d.Length >= 0x28 + s) id.OwnNode = dc.At(Sketches.XRef(d, 0x24 + s));
                }
                else
                    for (int p = at; p + 8 <= d.Length; p++)
                    {
                        if (d[p + 3] != 0x80) continue;
                        Node x = dc.At(Sketches.XRef(d, p));
                        if (x == null || x.Type == null) continue;
                        if (x.Type.Key == Naming.Table) { id.Table = x; id.Entry = (int)U32(d, p + 4); break; }
                        if (geometry < 0) geometry = x.Index;
                    }
                if (geometry >= 0 && !r.ContainsKey(geometry)) r[geometry] = id;
            }
            return r;
        }

        // The part file and its contents, given a path; set by whoever knows where the files are.
        public static Func<string, KeyValuePair<NodeTable, byte[]>> PartFile;

        static string WorkKind(Node n)
        {
            return n == null ? "?" : n.TypeName == "Plane" ? "kWorkPlaneObject" : n.TypeName == "Line3D" ? "kWorkAxisObject" : n.TypeName == "Point3D" ? "kWorkPointObject" : n.TypeName;
        }

        // A joint's origin (55CE9B23) is built by a C9A8BC23 that also refers to the Point3D the origin is named through.
        static Node JointPoint(NodeTable dc, Node origin)
        {
            if (origin == null) return null;
            foreach (Node n in dc.Nodes)
            {
                if (n.Type == null || n.Type.Key != 0xC9A8BC23 || n.Data == null) continue;
                Node point = null;
                bool mine = false;
                for (int p = 0x16; p + 4 <= n.Data.Length; p++)
                {
                    if (n.Data[p + 3] != 0x80) continue;
                    int x = Sketches.XRef(n.Data, p);
                    if (x == origin.Index) mine = true;
                    Node r = dc.At(x);
                    if (r != null && r.TypeName == "Point3D" && point == null) point = r;
                }
                if (mine && point != null) return point;
            }
            return null;
        }

        // Set to look into grips as they are found: the text is written here.
        public static StringBuilder Explaining;

        static GripItem FindGrip(NodeTable dc, Dictionary<int, GripId> ids, Node g, string route, Occurrence top)
        {
            GripItem item = new GripItem { Geometry = GeometryOf(g, null) };
            if (g == null) { item.Why = "no geometry"; return item; }
            GripId id;
            if (!ids.TryGetValue(g.Index, out id)) { item.Why = "no id node: " + g.TypeName + " #" + g.Index.ToString("X"); return item; }
            if (id.OwnNode != null)
            {
                string own;
                Sketches.BrowserNames(dc).TryGetValue(id.OwnNode.Index, out own);
                item.Work = WorkKind(id.OwnNode) + " " + own;
                item.File = "(the assembly)";
                return item;
            }
            // The part: down the path from the top occurrence.
            Occurrence leaf = top;
            if (route != null)
            {
                string[] parts = route.Split(':');
                for (int i = 1; i < parts.Length && leaf != null; i++)
                {
                    List<Occurrence> inside = leaf.Path == null || Referenced == null ? null : Referenced(leaf.Path);
                    leaf = inside == null ? null : inside.Find(x => x.Number.ToString() == parts[i]);
                }
            }
            if (leaf == null || leaf.Path == null) { item.Why = "part not known"; return item; }
            item.File = leaf.File;
            if (PartFile == null) { item.Why = "no files"; return item; }
            KeyValuePair<NodeTable, byte[]> part = PartFile(leaf.Path);
            if (part.Key == null || part.Value == null) { item.Why = "part file not read"; return item; }
            // The node the string points at: a work feature, or the body.
            Node target = null;
            if (id.PartNode >= 0)
                foreach (Node n in part.Key.Nodes)
                    if (n.Data != null && n.Data.Length > 6 && BitConverter.ToUInt16(n.Data, 4) == id.PartNode) { target = n; break; }
            if (id.Table == null)
            {
                if (target == null) { item.Why = "no table, and no node " + id.PartNode; return item; }
                string name;
                Sketches.BrowserNames(part.Key).TryGetValue(target.Index, out name);
                item.Work = WorkKind(target) + " " + name;
                return item;
            }
            item.Target = target == null ? "?" : target.TypeName;
            item.Grip = Naming.ResolveGrip(dc, id.Table, id.Entry, part.Key, part.Value);
            bool direction = g.TypeName == "DirectionAxis";
            if (Explaining != null) Explaining.AppendLine(leaf.File + ": " + item.Geometry + "\n" + Naming.ExplainGrip(dc, id.Table, id.Entry, part.Key, part.Value, item.Geometry, direction));
            // A point is named by an edge and a parameter along it (the f64 after the entry): usually one of its ends.
            // (Or a circle's centre, which the edge itself carries.)
            if (g.TypeName == "Point3D" && item.Grip.Kind == "edge")
            {
                if (item.Grip.Found.Count == 0) item.Grip.Found = item.Grip.Nearby;
                if (!item.Grip.Found.Exists(m => Naming.Carries(m, item.Geometry, direction))) Naming.EndsInstead(item.Grip);
            }
            item.Exact = item.Grip.Found.FindAll(m => Naming.Carries(m, item.Geometry, direction));
            if (item.Exact.Count == 0)
            {
                item.Exact = item.Grip.Nearby.FindAll(m => Naming.Carries(m, item.Geometry, direction));
                if (item.Exact.Count > 0) { item.Grip.Found = item.Exact; item.ByGeometry = true; }
            }
            return item;
        }

        // An assembly's own iMate: its geometry names an entity of a part, on the occurrence its path starts with.
        public static Naming.Grip GripOfAssemblyImate(NodeTable dc, ImateInfo m, List<Occurrence> occurrences)
        {
            if (m.Geometry == null) return null;
            string route = PathOf(dc, m.Geometry);
            if (route == null) return null;
            string first = route.Split(':')[0];
            GripItem item = FindGrip(dc, GripIds(dc), m.Geometry, route, occurrences.Find(x => x.Number.ToString() == first));
            return item.Grip;
        }

        // An iMate joint's argument (1CAEFC09) names the iMate by its id (+0x12), in the part the occurrence places.
        static GripItem GripThroughImate(Node g, string id, Occurrence top)
        {
            GripItem item = new GripItem { Geometry = GeometryOf(g, null) };
            if (top == null || top.Path == null) { item.Why = "part not known"; return item; }
            item.File = top.File;
            if (PartFile == null) { item.Why = "no files"; return item; }
            KeyValuePair<NodeTable, byte[]> part = PartFile(top.Path);
            if (part.Key == null || part.Value == null) { item.Why = "part file not read"; return item; }
            item.Grip = Imates.GripById(part.Key, part.Value, id);
            if (item.Grip == null) { item.Why = "no iMate with that id"; return item; }
            item.Target = "iMate";
            bool direction = g.TypeName == "DirectionAxis";
            item.Exact = item.Grip.Found.FindAll(m => Naming.Carries(m, item.Geometry, direction));
            return item;
        }

        // Component patterns. Rectangular 34AACB53, circular D739EDBB: +0x06 browser node, +0x2A a kind-2 list of the parent occurrences (90874D61),
        // a kind-2 list of elements (889E21C1), u32 their count, an empty list, u32 1, u32 0, then a node of cached values (88A7DE77, 6C5CD690).
        // Element: +0x06 u32 index, +0x0A u32 flags (1 suppressed, 2 independent), +0x0E a kind-2 list of its occurrences (empty if either),
        // then the pattern.
        // Settings, in a node whose +0x1E is that cached node:
        //   Rectangular, ABC2E4ED: +0x22, +0x26 row and column counts (ParameterRDxVar; a 17B3E814 {+0x1E Parameter, +0x22 it} gives the
        //     parameter, and without one the count is 1), +0x2A, +0x2E row and column offsets, +0x32, +0x36 row and column directions
        //     (DirectionAxis; a 618C9E00 {+0x20 the entity, +0x2C Boolean reversed, +0x30 it} says how it came about; no row, no direction).
        //   Circular, 6C5CD68F: +0x22 count, +0x26 angle, +0x2A the axis, +0x2E Boolean reversed.
        public class PatternInfo
        {
            public string Name, Type, Count, Offset, RowCount, RowOffset, Entity, RowEntity;
            public bool Natural = true, RowNatural = true;
            public double[] Direction, RowDirection;
            public List<int> Parents = new List<int>();
            public List<string> Elements = new List<string>();
        }

        static bool BoolAt(NodeTable dc, byte[] d, int p)
        {
            Node b = dc.At(Sketches.XRef(d, p));
            return b != null && b.Data != null && b.Data.Length > 0 && b.Data[b.Data.Length - 1] != 0;
        }

        public static List<PatternInfo> Patterns(NodeTable dc, List<Occurrence> occurrences)
        {
            List<PatternInfo> r = new List<PatternInfo>();
            Dictionary<int, string> formulas = new Dictionary<int, string>();
            foreach (Params.Decoded p in Params.Decode(dc)) formulas[p.Index] = p.Formula;
            Func<int, string> formula = i => { string f; return formulas.TryGetValue(i, out f) ? f : null; };
            Func<int, string> count = rdx => {
                foreach (Node n in dc.Nodes)
                    if (n.Type != null && n.Type.Key == 0x17B3E814 && n.Data != null && Sketches.XRef(n.Data, 0x22 + Sketches.Shift(n.Data)) == rdx)
                        return formula(Sketches.XRef(n.Data, 0x1E + Sketches.Shift(n.Data)));
                return "1 ul";
            };
            foreach (Node n in dc.Nodes)
            {
                if (n.Type == null || n.Data == null || (n.Type.Key != 0x34AACB53 && n.Type.Key != 0xD739EDBB)) continue;
                bool circular = n.Type.Key == 0xD739EDBB;
                byte[] d = n.Data;
                int s = Sketches.Shift(d);
                PatternInfo info = new PatternInfo { Type = circular ? "kCircularOccurrencePatternObject" : "kRectangularOccurrencePatternObject" };
                Node browser = dc.At(Sketches.XRef(d, 6));
                info.Name = browser == null ? null : NodeTable.Label(browser);
                if (string.IsNullOrEmpty(info.Name)) info.Name = "Component Pattern " + (r.Count + 1) + ":1";
                List<int> elements;
                int at = Sketches.ReadList(d, 0x2A + s, out info.Parents);
                at = Sketches.ReadList(d, at, out elements);
                List<int> none;
                at = Sketches.ReadList(d, at + 4, out none);
                int cached = Sketches.XRef(d, at + 8);
                foreach (int e in elements)
                {
                    Node el = dc.At(e);
                    if (el == null || el.Data == null) continue;
                    List<int> members;
                    Sketches.ReadList(el.Data, 0x0E, out members);
                    uint flags = U32(el.Data, 0x0A);
                    List<string> names = new List<string>();
                    foreach (int m in members) { Occurrence o = occurrences.Find(x => x.Dc == m); names.Add(o == null ? "?" : o.Name); }
                    info.Elements.Add(U32(el.Data, 6) + ((flags & 2) != 0 ? " independent " : " ") + ((flags & 1) != 0 ? "suppressed " : "") + string.Join(" + ", names.ToArray()));
                }
                foreach (Node h in dc.Nodes)
                {
                    if (h.Type == null || h.Data == null || (h.Type.Key != 0xABC2E4ED && h.Type.Key != 0x6C5CD68F)) continue;
                    int hs = Sketches.Shift(h.Data);
                    if (Sketches.XRef(h.Data, 0x1E + hs) != cached) continue;
                    byte[] v = h.Data;
                    if (circular)
                    {
                        info.Count = count(Sketches.XRef(v, 0x22 + hs));
                        info.Offset = formula(Sketches.XRef(v, 0x26 + hs));
                        info.Entity = WorkKind(dc.At(Sketches.XRef(v, 0x2A + hs)));
                        info.Natural = !BoolAt(dc, v, 0x2E + hs);
                        Node axis = dc.At(Sketches.XRef(v, 0x2A + hs));
                        if (axis != null && axis.Data != null) info.Direction = new[] { F64(axis.Data, 0x52 + Sketches.Shift(axis.Data)), F64(axis.Data, 0x5A + Sketches.Shift(axis.Data)), F64(axis.Data, 0x62 + Sketches.Shift(axis.Data)) };
                        continue;
                    }
                    info.RowCount = count(Sketches.XRef(v, 0x22 + hs));
                    info.Count = count(Sketches.XRef(v, 0x26 + hs));
                    info.RowOffset = formula(Sketches.XRef(v, 0x2A + hs));
                    info.Offset = formula(Sketches.XRef(v, 0x2E + hs));
                    int rowDir = Sketches.XRef(v, 0x32 + hs), colDir = Sketches.XRef(v, 0x36 + hs);
                    foreach (Node dir in dc.Nodes)
                    {
                        if (dir.Type == null || dir.Type.Key != 0x618C9E00 || dir.Data == null) continue;
                        int ds = Sketches.Shift(dir.Data), target = Sketches.XRef(dir.Data, 0x30 + ds);
                        if (target != rowDir && target != colDir) continue;
                        bool reversed = BoolAt(dc, dir.Data, 0x2C + ds);
                        string entity = WorkKind(dc.At(Sketches.XRef(dir.Data, 0x20 + ds)));
                        if (target == rowDir) { info.RowNatural = !reversed; info.RowEntity = entity; }
                        else { info.Natural = !reversed; info.Entity = entity; }
                    }
                    Func<int, double[]> direction = i => { Node a = dc.At(i); return a == null || a.Data == null ? null : new[] { F64(a.Data, 0x32 + Sketches.Shift(a.Data)), F64(a.Data, 0x3A + Sketches.Shift(a.Data)), F64(a.Data, 0x42 + Sketches.Shift(a.Data)) }; };
                    info.Direction = direction(colDir);
                    info.RowDirection = rowDir >= 0 ? direction(rowDir) : null;
                }
                r.Add(info);
            }
            return r;
        }

        // Numbers in the API's text compare by value ("3.000000 ul" and "3 ul"); formulas as they are.
        public static bool SameText(string decoded, string api)
        {
            if (decoded == api) return true;
            if (decoded == null || api == null) return false;
            string[] a = api.Split(' '), d = decoded.Split(' ');
            double u, v;
            return a.Length == 2 && d.Length == 2 && a[1] == d[1] && double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out u) && double.TryParse(d[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v) && Math.Abs(u - v) < 1e-9;
        }

        public static List<string> ComparePatterns(List<PatternInfo> all, string truth, out int compared)
        {
            compared = 0;
            List<string> bad = new List<string>();
            int start = truth.IndexOf("\"patterns\"", StringComparison.Ordinal);
            if (start < 0) return bad;
            int end = truth.IndexOf("\"imates\"", start, StringComparison.Ordinal);
            if (end < 0) end = truth.Length;
            HashSet<string> seen = new HashSet<string>();
            foreach (string raw in truth.Substring(start, end - start).Split('\n'))
            {
                string line = raw.Trim(), name = Field(line, "name");
                if (name == null) continue;
                compared++;
                seen.Add(name);
                PatternInfo p = all.Find(x => x.Name == name);
                if (p == null) { bad.Add(name + ": not decoded"); continue; }
                Action<string, string, string> same = (what, decoded, api) => { if (api != null && !SameText(decoded, api)) bad.Add(name + " " + what + ": Inventor " + api + ", decoded " + (decoded ?? "nothing")); };
                same("type", p.Type, Field(line, "type"));
                same("elements", string.Join("; ", p.Elements.ToArray()), Field(line, "elements"));
                same("count", p.Count, Field(line, "columnCount"));
                same("offset", p.Offset, Field(line, "columnOffset"));
                same("natural", p.Natural ? "true" : "false", Field(line, "columnNatural"));
                same("entity", p.Entity, Field(line, "columnEntity"));
                same("row count", p.RowCount, Field(line, "rowCount"));
                same("row offset", p.RowOffset, Field(line, "rowOffset"));
                same("row natural", p.RowNatural ? "true" : "false", Field(line, "rowNatural"));
                same("row entity", p.RowEntity, Field(line, "rowEntity"));
                same("count", p.Count, Field(line, "elementCount"));
                same("angle", p.Offset, Field(line, "angleOffset"));
                same("natural", p.Natural ? "true" : "false", Field(line, "axisNatural"));
                same("entity", p.Entity, Field(line, "axisEntity"));
            }
            foreach (PatternInfo p in all) if (!seen.Contains(p.Name)) bad.Add(p.Name + ": decoded, not in Inventor's list");
            return bad;
        }

        // Reads a referenced file's occurrences, given its path; set by whoever knows where the files are.
        public static Func<string, List<Occurrence>> Referenced;
        public static Func<string, Dictionary<string, double[]>> ReferencedFlexible;

        // The placement within the top occurrence of the item at a path such as "65:15:3": level by level, a flexible placement
        // if there is one, else the occurrence's own placement in the sub-assembly's file. Null if a file can't be read.
        static double[] Chain(string route, Occurrence top, Dictionary<string, double[]> flexible)
        {
            string[] parts = route.Split(':');
            double[] m = null;
            string file = top == null ? null : top.Path;
            List<string> path = new List<string> { parts[0] };
            List<KeyValuePair<Dictionary<string, double[]>, int>> scopes = new List<KeyValuePair<Dictionary<string, double[]>, int>> {
                new KeyValuePair<Dictionary<string, double[]>, int>(flexible, 0) };
            for (int i = 1; i < parts.Length; i++)
            {
                List<Occurrence> inside = file == null || Referenced == null ? null : Referenced(file);
                Occurrence child = inside == null ? null : inside.Find(x => x.Number.ToString() == parts[i]);
                if (child == null) return null;
                AddScope(scopes, file, path.Count);
                double[] level = PlacementIn(path, child, scopes);
                if (level == null) return null;
                m = Compose(m, level);
                file = child.Path;
                path.Add(parts[i]);
            }
            return m;
        }

        static void AddScope(List<KeyValuePair<Dictionary<string, double[]>, int>> scopes, string file, int start)
        {
            Dictionary<string, double[]> own = ReferencedFlexible == null || file == null ? null : ReferencedFlexible(file);
            if (own != null) scopes.Add(new KeyValuePair<Dictionary<string, double[]>, int>(own, start));
        }

        // A child's placement in its parent. Each file on the way down can hold a flexible placement for it, its path counted from
        // that file; the outermost file wins. Else its own placement.
        static double[] PlacementIn(List<string> parent, Occurrence child, List<KeyValuePair<Dictionary<string, double[]>, int>> scopes)
        {
            foreach (KeyValuePair<Dictionary<string, double[]>, int> scope in scopes)
            {
                double[] moved;
                string relative = string.Join(":", parent.GetRange(scope.Value, parent.Count - scope.Value).ToArray());
                if (scope.Key.TryGetValue(relative + "#" + child.RxId, out moved)) return moved;
            }
            return child.Matrix;
        }

        // Everything under the top occurrences, by name ("Link:2/Ball:1") with its placement in the assembly, read
        // through each sub-assembly's file. Occurrences whose file can't be read end the branch.
        public static Dictionary<string, double[]> Tree(List<Occurrence> top, Dictionary<string, double[]> flexible)
        {
            Dictionary<string, double[]> r = new Dictionary<string, double[]>();
            Action<Occurrence, string, List<string>, double[], List<KeyValuePair<Dictionary<string, double[]>, int>>> walk = null;
            walk = (o, name, path, placed, scopes) => {
                if (o.File == null || !o.File.EndsWith(".iam", StringComparison.OrdinalIgnoreCase) || Referenced == null) return;
                List<Occurrence> inside = Referenced(o.Path);
                if (inside == null) return;
                List<KeyValuePair<Dictionary<string, double[]>, int>> deeper = new List<KeyValuePair<Dictionary<string, double[]>, int>>(scopes);
                AddScope(deeper, o.Path, path.Count);
                foreach (Occurrence child in inside)
                {
                    double[] m = Compose(placed, PlacementIn(path, child, deeper));
                    string childName = name + "/" + child.Name;
                    r[childName] = m;
                    walk(child, childName, new List<string>(path) { child.Number.ToString() }, m, deeper);
                }
            };
            foreach (Occurrence o in top)
                walk(o, o.Name, new List<string> { o.Number.ToString() }, o.Matrix, new List<KeyValuePair<Dictionary<string, double[]>, int>> { new KeyValuePair<Dictionary<string, double[]>, int>(flexible, 0) });
            return r;
        }

        // The path string of a geometry's 375EAEE5.
        static string PathOf(NodeTable dc, Node g)
        {
            if (g == null || g.Data == null) return null;
            Node path = dc.At(Sketches.XRef(g.Data, 0x26 + Sketches.Shift(g.Data)));
            if (path == null || path.Data == null || path.Type == null || path.Type.Key != 0x375EAEE5) return null;
            byte[] d = path.Data;
            for (int p = 0x10; p + 14 <= d.Length; p++)
                if (U32(d, p) == 3 && d[p + 4] == (byte)'D' && d[p + 6] == (byte)'C' && d[p + 8] == (byte)'x')
                {
                    int n = (int)U32(d, p + 10);
                    return n >= 0 && n < 100 && p + 14 + 2 * n <= d.Length ? Encoding.Unicode.GetString(d, p + 14, 2 * n) : null;
                }
            return null;
        }

        // The constraint folder's entries in the browser: the names Inventor shows, in order, as a default name is stored only there.
        static List<string> BrowserConstraintNames(NodeTable browser)
        {
            List<string> names = new List<string>();
            if (browser == null) return names;
            foreach (Node n in browser.Nodes)
            {
                if (n.Type == null || n.Type.Key != ConstraintFolder || n.Data == null) continue;
                List<int> children;
                Sketches.ReadList(n.Data, 6, out children);
                foreach (int c in children)
                {
                    Node child = browser.At(c);
                    names.Add(child == null || child.Data == null ? "" : EntryText(child.Data));
                }
            }
            return names;
        }

        // A browser entry's name: the string just before its type name, which ends "Entry". Older files often leave it empty.
        static string EntryText(byte[] d)
        {
            for (int p = 0; p + 8 < d.Length; p++)
            {
                int n = (int)U32(d, p);
                if (n < 0 || n > 300 || p + 8 + 2 * n > d.Length) continue;
                int q = p + 4 + 2 * n, m = (int)U32(d, q);
                if (m < 5 || m > 60 || q + 4 + 2 * m > d.Length) continue;
                if (Encoding.Unicode.GetString(d, q + 4, 2 * m).EndsWith("Entry")) return Encoding.Unicode.GetString(d, p + 4, 2 * n);
            }
            return "";
        }

        public static List<MateItem> Constraints(NodeTable dc, NodeTable browser, List<Occurrence> occurrences, NodeTable rx = null)
        {
            List<MateItem> r = new List<MateItem>();
            Dictionary<string, double[]> flexible = FlexiblePlacements(rx);
            List<string> shown = BrowserConstraintNames(browser);
            List<Params.Decoded> parameters = Params.Decode(dc);
            Dictionary<int, GripId> ids = GripIds(dc);
            int position = 0;
            foreach (Node n in dc.Nodes)
            {
                if (n.Type == null || n.Type.Key != ConstraintNode || n.Data == null) continue;
                byte[] d = n.Data;
                int s = Sketches.Shift(d);
                MateItem c = new MateItem { Type = d[0x22 + s], Bit40 = (d[0x0C] & 0x40) != 0, Bit01 = (d[0x0D] & 0x01) != 0 };
                string kind;
                c.Kind = Kinds.TryGetValue(c.Type, out kind) || JointKinds.TryGetValue(c.Type, out kind) ? kind : "type " + c.Type;
                Node browserNode = dc.At(Sketches.XRef(d, 6));
                c.Name = browserNode == null ? null : NodeTable.Label(browserNode);
                if (position < shown.Count) c.Shown = shown[position];
                position++;
                List<int> args, ps;
                int p = Sketches.ReadList(d, 0x23 + s, out args);
                p = Sketches.ReadList(d, p, out ps);
                foreach (int a in args)
                {
                    Node arg = dc.At(a);
                    if (arg == null || arg.Data == null || arg.Data.Length < 14) continue;
                    int occ = Sketches.XRef(arg.Data, 6);
                    Occurrence o = occurrences.Find(x => x.Dc == occ);
                    c.Occurrences.Add(occ);
                    Node g = dc.At(Sketches.XRef(arg.Data, 10));
                    double[] inner = InnerMatrix(dc, g);
                    string route = PathOf(dc, g);
                    if (route != null && route.Contains(":")) inner = Chain(route, o, flexible) ?? inner;
                    if (inner != null) c.Nested = true;
                    c.Geometry.Add(GeometryOf(g, Compose(o == null ? null : o.Matrix, inner)));
                    c.Axes.Add(JointAxis(g));
                    if (arg.Type != null && arg.Type.Key == Imates.Argument && arg.Data.Length >= 0x22) { c.FromImate = true; c.Grips.Add(GripThroughImate(g, Imates.IdAt(arg.Data, 0x12), o)); }
                    else c.Grips.Add(FindGrip(dc, ids, c.IsJoint ? JointPoint(dc, g) : g, route, o));
                }
                foreach (int a in ps)
                {
                    Node param = dc.At(a);
                    if (param == null || param.Data == null || param.Data.Length < 12) continue;
                    int index = Sketches.XRef(param.Data, 8);
                    Params.Decoded value = parameters.Find(x => x.Index == index);
                    if (value != null) { c.Parameters.Add(value.Formula); c.Values.Add(value.Value); }
                }
                if (p < d.Length)
                {
                    c.Suppressed = (d[p] & 0x04) != 0;
                    c.Failing = (d[p] & 0x03) != 0;
                    c.Health = c.Suppressed ? "kSuppressedHealth" : (d[p] & 0x03) == 3 ? "kDriverLostHealth" : (d[p] & 0x03) == 1 ? "kInconsistentHealth" : (d[p] & 0x03) == 0 ? "kUpToDateHealth" : "flags " + (d[p] & 0x03);
                }
                if (p + 5 <= d.Length) c.Number = (int)U32(d, p + 1);
                if (p + 9 <= d.Length) c.Settings = U32(d, p + 5);
                if (string.IsNullOrEmpty(c.Name)) c.Name = DefaultName(c);
                r.Add(c);
            }
            return r;
        }

        public static string ConstraintReport(List<MateItem> all, List<Occurrence> occurrences)
        {
            StringBuilder b = new StringBuilder();
            foreach (MateItem c in all)
            {
                b.AppendFormat("  {0}: {1}{2}{3}", c.Name ?? "?", c.Kind, c.Suppressed ? " suppressed" : "", c.Failing ? " FAILING" : "");
                if (c.IsJoint)
                    foreach (KeyValuePair<string, string> f in JointFields(c)) b.AppendFormat(" {0}={1}", f.Key, f.Value);
                else
                {
                    b.Append((c.Bit40 ? " bit40" : "") + (c.Bit01 ? " bit01" : "") + ((c.Settings & 0x2000) != 0 ? " locked rotation" : ""));
                    if (c.Parameters.Count > 0) b.Append(" = " + string.Join(", ", c.Parameters.ToArray()));
                }
                b.Append("\n");
                for (int i = 0; i < c.Geometry.Count; i++)
                {
                    int dc = c.Occurrences[i];
                    Occurrence o = occurrences.Find(x => x.Dc == dc);
                    b.AppendFormat("      {0}: {1}\n", o == null ? "(the assembly)" : o.Name, c.Geometry[i]);
                    if (i < c.Grips.Count)
                    {
                        GripItem g = c.Grips[i];
                        if (g.Work != null) b.AppendFormat("        grips {0} in {1}\n", g.Work, g.File);
                        else if (g.Grip == null) b.AppendFormat("        grips: unknown, {0}\n", g.Why);
                        else b.AppendFormat("        grips {0} in {1} ({2}): {3}\n", g.Grip.Kind, g.File, g.Grip.EntryType, g.Exact.Count == 1 ? Sab.Where(g.Exact[0].Records, g.Exact[0].Entity) : g.Exact.Count + " of " + g.Grip.Found.Count + " carry its geometry");
                    }
                }
            }
            return b.ToString();
        }

        static bool SameWords(string a, string b)
        {
            string[] x = a.Split(' '), y = b.Split(' ');
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++)
            {
                double u, v;
                bool nu = double.TryParse(x[i], NumberStyles.Float, CultureInfo.InvariantCulture, out u), nv = double.TryParse(y[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
                if (nu != nv || (nu ? Math.Abs(u - v) > 1e-5 : x[i] != y[i])) return false;
            }
            return true;
        }

        // Counted by CompareConstraints: grips compared with Inventor, and those where several entities carried the geometry.
        public static int gripsCompared, gripsAmbiguous;
        public static SortedDictionary<string, int> gripsMatched = new SortedDictionary<string, int>();   // those that matched, by entry type

        // Decoded constraints against a truth .json.
        public static List<string> CompareConstraints(List<MateItem> all, List<Occurrence> occurrences, string truth, out int compared)
        {
            compared = 0;
            List<string> bad = new List<string>();
            int start = truth.IndexOf("\"constraints\"", StringComparison.Ordinal), end = truth.IndexOf("\"joints\"", StringComparison.Ordinal);
            if (start < 0 || end < 0) return bad;
            HashSet<string> seen = new HashSet<string>();
            foreach (string raw in truth.Substring(start, end - start).Split('\n'))
            {
                string line = raw.Trim(), name = Field(line, "name");
                if (name == null) continue;
                compared++;
                seen.Add(name);
                MateItem c = all.Find(x => x.Name == name);
                if (c == null) { bad.Add(name + ": not decoded"); continue; }
                Action<string, string, string> same = (what, decoded, api) => { if (api != null && decoded != api) bad.Add(name + " " + what + ": Inventor " + api + ", decoded " + decoded); };
                Func<int, string> occName = i => { if (i >= c.Occurrences.Count) return "(none)"; Occurrence o = occurrences.Find(x => x.Dc == c.Occurrences[i]); return o == null ? "(the assembly)" : o.Name; };
                same("type", c.Kind, Field(line, "type"));
                same("suppressed", c.Suppressed ? "true" : "false", Field(line, "suppressed"));
                same("occurrence one", occName(0), Field(line, "occurrenceOne"));
                same("occurrence two", occName(1), Field(line, "occurrenceTwo"));
                string first = c.Parameters.Count > 0 ? c.Parameters[0] : null;
                string health = Field(line, "health");
                if (health != null && health != c.Health) bad.Add(name + " health: Inventor " + health + ", decoded " + c.Health);
                foreach (string key in new[] { "offset", "angle", "distance", "ratio" }) same(key, RoundedLike(first, Field(line, key)), Field(line, key));
                same("directed", c.Bit01 ? "true" : "false", Field(line, "directed"));
                same("axes opposed", c.Bit01 ? "true" : "false", Field(line, "axesOpposed"));
                same("inside tangency", c.Bit40 ? "false" : "true", Field(line, "insideTangency"));
                same("forward", c.Bit40 ? "false" : "true", Field(line, "forward"));
                same("lock rotation", (c.Settings & 0x2000) != 0 ? "true" : "false", Field(line, "lockRotation"));
                if (Field(line, "mateSolution") == "kAlignedSolutionType" && !c.Bit40) bad.Add(name + ": aligned, not decoded so");
                for (int i = 0; i < 2 && i < c.Geometry.Count; i++)
                {
                    string api = Field(line, i == 0 ? "geometryOne" : "geometryTwo");
                    // ("?": a B-spline surface, which the truth has no words for.)
                    if (api != null && api != "?" && !SameWords(c.Geometry[i], api)) bad.Add(name + " geometry " + (i + 1) + ": Inventor " + api + ", decoded " + c.Geometry[i]);
                    string grip = Field(line, i == 0 ? "gripOne" : "gripTwo");
                    if (grip == null || i >= c.Grips.Count) continue;
                    gripsCompared++;
                    GripItem g = c.Grips[i];
                    if (g.Work != null) { if (g.Work != grip) bad.Add(name + " grip " + (i + 1) + ": Inventor " + grip + ", decoded " + g.Work); continue; }
                    if (g.Grip == null) { bad.Add(name + " grip " + (i + 1) + ": Inventor " + grip + ", decoded nothing: " + g.Why); continue; }
                    // Where the part has changed since the assembly was saved, the stored geometry is out of date and only the name counts.
                    List<Naming.Match> candidates = g.Exact.Count > 0 ? g.Exact : g.Grip.Found;
                    if (candidates.Count == 0) { bad.Add(name + " grip " + (i + 1) + ": Inventor " + grip + ", decoded " + g.Grip.Kind + " " + g.Grip.EntryType + ", nothing found"); continue; }
                    List<Naming.Match> right = candidates.FindAll(m => Naming.SameAsTruth(m, grip));
                    if (right.Count == 0) bad.Add(name + " grip " + (i + 1) + ": Inventor " + grip + ", decoded " + Sab.Where(candidates[0].Records, candidates[0].Entity));
                    else
                    {
                        if (candidates.Count > 1) gripsAmbiguous++;
                        string kind = g.Grip.EntryType + (g.ByGeometry ? " among a named face's edges" : "");
                        int seen1;
                        gripsMatched.TryGetValue(kind, out seen1);
                        gripsMatched[kind] = seen1 + 1;
                    }
                }
            }
            foreach (MateItem c in all) if (!c.IsJoint && !seen.Contains(c.Name ?? "")) bad.Add((c.Name ?? "?") + ": decoded, not in Inventor's list");
            return bad;
        }

        static double[] Numbers(string text, int at, int count)
        {
            string[] w = text.Split(' ');
            double[] r = new double[count];
            for (int i = 0; i < count; i++) r[i] = double.Parse(w[at + i], CultureInfo.InvariantCulture);
            return r;
        }

        static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        static double[] Minus(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        static double Length(double[] a) { return Math.Sqrt(Dot(a, a)); }
        static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }

        // How far (cm, or radians for an angle) a constraint is from holding, from its decoded geometry and placements; null where
        // this kind of pair isn't worked out.
        public static double? Miss(MateItem c)
        {
            if (c.Geometry.Count < 2 || c.Geometry[0] == "?" || c.Geometry[1] == "?") return null;
            string[] a = c.Geometry[0].Split(' '), b = c.Geometry[1].Split(' ');
            string pair = a[0] + "/" + b[0];
            double offset = c.Values.Count > 0 ? Math.Abs(c.Values[0]) : 0;
            Func<string[], double[]> root = w => new[] { double.Parse(w[1], CultureInfo.InvariantCulture), double.Parse(w[2], CultureInfo.InvariantCulture), double.Parse(w[3], CultureInfo.InvariantCulture) };
            Func<string[], double[]> dir = w => new[] { double.Parse(w[5], CultureInfo.InvariantCulture), double.Parse(w[6], CultureInfo.InvariantCulture), double.Parse(w[7], CultureInfo.InvariantCulture) };
            if (c.Type == 1 || c.Type == 2)
            {
                switch (pair)
                {
                    case "plane/plane": return Math.Max(Math.Abs(Math.Abs(Dot(dir(a), dir(b))) - 1), Math.Abs(Math.Abs(Dot(Minus(root(b), root(a)), dir(a))) - offset));
                    case "point/point": return Math.Abs(Length(Minus(root(a), root(b))) - offset);
                    case "line/line": return Math.Max(Length(Cross(dir(a), dir(b))), Math.Abs(Length(Cross(Minus(root(b), root(a)), dir(a))) - offset));
                    case "plane/point": return Math.Abs(Math.Abs(Dot(Minus(root(b), root(a)), dir(a))) - offset);
                    case "point/plane": return Math.Abs(Math.Abs(Dot(Minus(root(a), root(b)), dir(b))) - offset);
                    case "plane/line": return Math.Max(Math.Abs(Dot(dir(a), dir(b))), Math.Abs(Math.Abs(Dot(Minus(root(b), root(a)), dir(a))) - offset));
                    case "line/plane": return Math.Max(Math.Abs(Dot(dir(a), dir(b))), Math.Abs(Math.Abs(Dot(Minus(root(a), root(b)), dir(b))) - offset));
                    case "line/point": return Math.Abs(Length(Cross(Minus(root(b), root(a)), dir(a))) - offset);
                    case "point/line": return Math.Abs(Length(Cross(Minus(root(a), root(b)), dir(b))) - offset);
                }
                return null;
            }
            if (c.Type == 5 && pair == "circle/circle") return Math.Max(Length(Cross(dir(a), dir(b))), Math.Abs(Length(Minus(root(a), root(b))) - offset));
            if (c.Type == 4 && pair == "plane/cylinder") return Math.Abs(Math.Abs(Dot(Minus(root(b), root(a)), dir(a))) - (double.Parse(b[9], CultureInfo.InvariantCulture) + offset));
            if (c.Type == 4 && pair == "cylinder/plane") return Math.Abs(Math.Abs(Dot(Minus(root(a), root(b)), dir(b))) - (double.Parse(a[9], CultureInfo.InvariantCulture) + offset));
            Func<string[], double> radius = w => double.Parse(w[5], CultureInfo.InvariantCulture);
            if (c.Type == 4 && pair == "sphere/plane") return Math.Abs(Math.Abs(Dot(Minus(root(a), root(b)), dir(b))) - (radius(a) + offset));
            if (c.Type == 4 && pair == "plane/sphere") return Math.Abs(Math.Abs(Dot(Minus(root(b), root(a)), dir(a))) - (radius(b) + offset));
            if (c.Type == 4 && pair == "sphere/sphere")
            {
                double between = Length(Minus(root(a), root(b)));
                return Math.Min(Math.Abs(between - (radius(a) + radius(b) + offset)), Math.Abs(between - Math.Abs(radius(a) - radius(b)) - offset));
            }
            return null;
        }

        // A joint's settings: its parameters run gap (not cylindrical or ball), then angle and its limits (rotational, cylindrical),
        // then position and its limits (slider, cylindrical). Settings bits: 0x08 angle set, 0x10/0x20 angle start/end limit,
        // 0x40 position set, 0x80/0x100 position start/end limit, 0x200 flip origin, 0x400 locked, 0x1000 flip alignment.
        public static List<KeyValuePair<string, string>> JointFields(MateItem c)
        {
            List<KeyValuePair<string, string>> f = new List<KeyValuePair<string, string>>();
            int i = 0;
            Func<string> next = () => i < c.Parameters.Count ? c.Parameters[i++] : "?";
            Action<string, string> add = (k, v) => f.Add(new KeyValuePair<string, string>(k, v));
            if (c.Type != 0x11 && c.Type != 0x12) add("gap", next());
            if (c.Type == 0x0E || c.Type == 0x12)
            {
                add("angular", next());
                bool start = (c.Settings & 0x10) != 0, end = (c.Settings & 0x20) != 0;
                add("hasAngularLimits", start || end ? "true" : "false");
                if (start) add("angularStart", next());
                if (end) add("angularEnd", next());
            }
            if (c.Type == 0x0F || c.Type == 0x12)
            {
                add("linear", next());
                bool start = (c.Settings & 0x80) != 0, end = (c.Settings & 0x100) != 0;
                add("hasLinearStart", start ? "true" : "false");
                add("hasLinearEnd", end ? "true" : "false");
                if (start) add("linearStart", next());
                if (end) add("linearEnd", next());
            }
            add("flipOrigin", (c.Settings & 0x200) != 0 ? "true" : "false");
            add("flipAlignment", (c.Settings & 0x1000) != 0 ? "true" : "false");
            if (c.Type != 0x10 && c.Type != 0x11 && c.Type != 0x13) add("locked", (c.Settings & 0x400) != 0 ? "true" : "false");
            return f;
        }

        static readonly Dictionary<int, string> JointKinds = new Dictionary<int, string> {
            { 0x0E, "kRotationalJointType" }, { 0x0F, "kSlideJointType" }, { 0x10, "kRigidJointType" }, { 0x11, "kBallJointType" }, { 0x12, "kCylindricalJointType" }, { 0x13, "kPlanarJointType" } };

        // The API prints a plain number at the document's precision ("0.000 mm", "-0.19 rad") where the file holds the value: the
        // decoded value printed the way the API's text is, so the two compare. Formulas stay as they are.
        static string RoundedLike(string decoded, string api)
        {
            if (decoded == null || api == null) return decoded;
            string[] a = api.Split(' '), d = decoded.Split(' ');
            double u, v;
            if (a.Length != 2 || d.Length != 2 || a[1] != d[1] || !double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out u) || !double.TryParse(d[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return decoded;
            int dot = a[0].IndexOf('.'), places = dot < 0 ? 0 : a[0].Length - dot - 1;
            return Math.Abs(u - v) <= 0.5000001 * Math.Pow(10, -places) ? api : decoded;
        }

        public static List<string> CompareJoints(List<MateItem> all, List<Occurrence> occurrences, string truth, out int compared)
        {
            compared = 0;
            List<string> bad = new List<string>();
            int start = truth.IndexOf("\"joints\"", StringComparison.Ordinal), end = truth.IndexOf("\"patterns\"", StringComparison.Ordinal);
            if (start < 0) return bad;
            if (end < 0) end = truth.Length;
            HashSet<string> seen = new HashSet<string>();
            foreach (string raw in truth.Substring(start, end - start).Split('\n'))
            {
                string line = raw.Trim(), name = Field(line, "name");
                if (name == null) continue;
                compared++;
                seen.Add(name);
                MateItem c = all.Find(x => x.Name == name);
                if (c == null) { bad.Add(name + ": not decoded"); continue; }
                Action<string, string, string> same = (what, decoded, api) => { if (api != null && decoded != api) bad.Add(name + " " + what + ": Inventor " + api + ", decoded " + decoded); };
                Func<int, string> occName = i => { if (i >= c.Occurrences.Count) return "(none)"; Occurrence o = occurrences.Find(x => x.Dc == c.Occurrences[i]); return o == null ? "(the assembly)" : o.Name; };
                string kind;
                JointKinds.TryGetValue(c.Type, out kind);
                same("type", kind, Field(line, "type"));
                same("suppressed", c.Suppressed ? "true" : "false", Field(line, "suppressed"));
                same("occurrence one", occName(0), Field(line, "occurrenceOne"));
                same("occurrence two", occName(1), Field(line, "occurrenceTwo"));
                foreach (KeyValuePair<string, string> f in JointFields(c)) same(f.Key, RoundedLike(f.Value, Field(line, f.Key)), Field(line, f.Key));
                for (int i = 0; i < 2 && i < c.Geometry.Count; i++)
                {
                    string api = Field(line, i == 0 ? "originOne" : "originTwo");
                    if (api != null && !SameWords(c.Geometry[i], api)) bad.Add(name + " origin " + (i + 1) + ": Inventor " + api + ", decoded " + c.Geometry[i]);
                    string grip = Field(line, i == 0 ? "gripOne" : "gripTwo");
                    if (grip == null || i >= c.Grips.Count) continue;
                    gripsCompared++;
                    GripItem g = c.Grips[i];
                    if (g.Work != null) { if (g.Work != grip) bad.Add(name + " grip " + (i + 1) + ": Inventor " + grip + ", decoded " + g.Work); continue; }
                    if (g.Grip == null) { bad.Add(name + " grip " + (i + 1) + ": Inventor " + grip + ", decoded nothing: " + g.Why); continue; }
                    List<Naming.Match> candidates = g.Exact.Count > 0 ? g.Exact : g.Grip.Found;
                    if (candidates.Count == 0) { bad.Add(name + " grip " + (i + 1) + ": Inventor " + grip + ", decoded " + g.Grip.Kind + " " + g.Grip.EntryType + ", nothing found"); continue; }
                    if (!candidates.Exists(m => Naming.SameAsTruth(m, grip))) bad.Add(name + " grip " + (i + 1) + ": Inventor " + grip + ", decoded " + Sab.Where(candidates[0].Records, candidates[0].Entity));
                }
            }
            foreach (MateItem c in all) if (c.IsJoint && !seen.Contains(c.Name ?? "")) bad.Add((c.Name ?? "?") + ": decoded, not in Inventor's list");
            return bad;
        }

        public static string Field(string line, string key)
        {
            int at = line.IndexOf("\"" + key + "\": \"", StringComparison.Ordinal);
            if (at < 0) return null;
            at += key.Length + 5;
            int end = line.IndexOf('"', at);
            return end < 0 ? null : line.Substring(at, end - at);
        }

        static bool SameNumbers(string a, string b)
        {
            string[] x = a.Split(' '), y = b.Split(' ');
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++)
            {
                double u, v;
                if (!double.TryParse(x[i], NumberStyles.Float, CultureInfo.InvariantCulture, out u) || !double.TryParse(y[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v) || Math.Abs(u - v) > 1e-5) return false;
            }
            return true;
        }

        // Decoded occurrences against a truth .json; each difference as a line, compared counts the occurrences.
        // tree, if given, holds the nested occurrences (see Tree); those count in nested.
        public static List<string> Compare(List<Occurrence> all, string truth, out int compared, Dictionary<string, double[]> tree, out int nested)
        {
            compared = 0;
            nested = 0;
            List<string> bad = new List<string>();
            int start = truth.IndexOf("\"occurrences\"", StringComparison.Ordinal), end = truth.IndexOf("\"constraints\"", StringComparison.Ordinal);
            if (start < 0 || end < 0) return bad;
            HashSet<string> seen = new HashSet<string>();
            foreach (string raw in truth.Substring(start, end - start).Split('\n'))
            {
                string line = raw.Trim();
                string name = Field(line, "name");
                if (name == null) continue;
                if (name.Contains("/"))
                {
                    if (tree == null) continue;
                    nested++;
                    double[] placed;
                    if (!tree.TryGetValue(name, out placed)) { bad.Add(name + ": not found under its parent"); continue; }
                    string want = Field(line, "matrix");
                    if (want != null && !SameNumbers(MatrixText(placed), want)) bad.Add(name + " matrix: Inventor " + want + ", decoded " + MatrixText(placed));
                    continue;
                }
                compared++;
                seen.Add(name);
                Occurrence o = all.Find(x => x.Name == name);
                if (o == null) { bad.Add(name + ": not decoded"); continue; }
                Action<string, string, string> same = (what, decoded, api) => { if (decoded != api) bad.Add(name + " " + what + ": Inventor " + api + ", decoded " + decoded); };
                string matrix = Field(line, "matrix");
                if (matrix != null && !SameNumbers(MatrixText(o.Matrix), matrix)) bad.Add(name + " matrix: Inventor " + matrix + ", decoded " + MatrixText(o.Matrix));
                same("file", o.File, Field(line, "file"));
                same("grounded", o.Grounded ? "true" : "false", Field(line, "grounded"));
                same("visible", o.Visible ? "true" : "false", Field(line, "visible"));
                same("suppressed", o.Suppressed ? "true" : "false", Field(line, "suppressed"));
                if (Field(line, "flexible") != null) same("flexible", o.Flexible ? "true" : "false", Field(line, "flexible"));
            }
            foreach (Occurrence o in all) if (!seen.Contains(o.Name ?? "")) bad.Add((o.Name ?? "?") + ": decoded, not in Inventor's list");
            return bad;
        }
    }
}
