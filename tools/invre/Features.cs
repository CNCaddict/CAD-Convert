// Decodes part features from a DC segment's nodes.
//
// Feature (90874D91): u32 0, u16 id, +06 xref browser node (its name), +0A flags, [+0E u32 0 from 2024],
//   +12 xref owner, +16 u32 index, +1A u32 record version, u32 0, then a kind-2 list of cross refs: the
//   feature's properties, one per slot (0 = unused). What each slot means depends on the feature type.
// Property nodes use the common header; after it:
//   Boolean (90874D28)  ... u8 value (last byte)
//   Enum nodes          u16 ?, u16 value
//   Parameter           as in Params
using System;
using System.Collections.Generic;
using System.Text;

namespace InvRe
{
    public static class Features
    {
        public const uint Feature = 0x90874D91, Boolean = 0x90874D28, Parameter = 0x90874D26;

        static uint U32(byte[] d, int p) { return BitConverter.ToUInt32(d, p); }

        // The property list: the first kind-2 list after the header.
        public static List<int> Properties(Node f)
        {
            byte[] d = f.Data;
            for (int p = 0x1A + Sketches.Shift(d); p + 8 <= d.Length; p += 2)
            {
                if (U32(d, p) != 0x30000002) continue;
                List<int> items;
                Sketches.ReadList(d, p, out items);
                return items;
            }
            return new List<int>();
        }

        // One property in words: a parameter's formula, an enum's value, a Boolean, or the node type.
        public static string Property(NodeTable t, int index)
        {
            Node n = t.At(index);
            if (n == null || n.Data == null || n.Type == null) return "-";
            byte[] d = n.Data;
            int h = 0x22 + Sketches.Shift(d);
            if (n.Type.Key == Boolean) return "Boolean " + (d[d.Length - 1] != 0 ? "true" : "false");
            if (n.TypeName.StartsWith("Enum") && d.Length >= h + 4)
                return n.TypeName + " = " + BitConverter.ToUInt16(d, h + 2) + " (of " + BitConverter.ToUInt16(d, h) + ")";
            if (n.Type.Key == Parameter)
            {
                foreach (Params.Decoded p in Params.Decode(t))
                    if (p.Index == index) return "Parameter " + p.Name + " = " + p.Formula;
            }
            string label = NodeTable.Label(n);
            return n.TypeName + (label == null ? "" : " \"" + label + "\"");
        }

        public class Info
        {
            public Node Node;
            public string Name;
            public uint Kind;   // first u32 of the class GUID stored after the browser name; 0 if none found
            // +0x0A: 0x04 when Inventor did not compute the feature (it fails, or is suppressed).
            public byte State { get { return Node.Data != null && Node.Data.Length > 0x0A ? Node.Data[0x0A] : (byte)0; } }
            // The tag naming uses for what the feature made (u32 at +0x16).
            public int Tag { get { int p = 0x16 + Sketches.Shift(Node.Data); return Node.Data != null && Node.Data.Length >= p + 4 ? (int)U32(Node.Data, p) : -1; } }
        }

        // Every feature with its browser name and class: the browser node (2B48A42B) cross-refs the feature at
        // +0x12 and ends with {u32 n, UTF-16 name, 16-byte class GUID}.
        public static List<Info> List(NodeTable t)
        {
            Dictionary<int, Info> byFeature = new Dictionary<int, Info>();
            List<Info> list = new List<Info>();
            foreach (Node f in t.Nodes)
                if (f.Type != null && f.Type.Key == Feature && f.Data != null)
                {
                    Info i = new Info { Node = f };
                    byFeature[f.Index] = i;
                    list.Add(i);
                }
            foreach (Node b in t.Nodes)
            {
                if (b.Type == null || b.Type.Key != 0x2B48A42B || b.Data == null || b.Data.Length < 0x16) continue;
                Info i;
                if (!byFeature.TryGetValue(Sketches.XRef(b.Data, 0x12 + Sketches.Shift(b.Data)), out i) || i.Name != null) continue;
                int end;
                i.Name = NodeTable.Label(b, out end);
                if (i.Name != null && end + 16 <= b.Data.Length) i.Kind = U32(b.Data, end);
            }
            return list;
        }

        public static string Report(NodeTable t)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<int, string> described = new Dictionary<int, string>();
            foreach (ExtrudeInfo e in Extrudes(t)) described[e.Index] = Describe(e);
            foreach (RevolveInfo r in Revolves(t)) described[r.Index] = Describe(r);
            foreach (HoleInfo h in Holes(t)) described[h.Index] = Describe(h);
            foreach (FilletInfo f in Fillets(t)) described[f.Index] = Describe(f);
            foreach (ChamferInfo c in Chamfers(t)) described[c.Index] = Describe(c);
            foreach (MoreFeatures.ThreadInfo th in MoreFeatures.Threads(t)) described[th.Index] = MoreFeatures.Describe(th);
            foreach (MoreFeatures.ShellInfo sh in MoreFeatures.Shells(t)) described[sh.Index] = MoreFeatures.Describe(sh);
            foreach (MoreFeatures.SweepInfo sw in MoreFeatures.Sweeps(t)) described[sw.Index] = MoreFeatures.Describe(sw);
            foreach (MoreFeatures.LoftInfo lf in MoreFeatures.Lofts(t)) described[lf.Index] = MoreFeatures.Describe(lf);
            foreach (MoreFeatures.SheetInfo sm in MoreFeatures.Sheets(t)) described[sm.Index] = MoreFeatures.Describe(sm);
            foreach (MoreFeatures.ImportedInfo im in MoreFeatures.ImportedBodies(t)) described[im.Index] = im.Kind + " (no history: its shape is only in the B-rep)";
            foreach (Info fi in List(t))
            {
                Node f = fi.Node;
                List<int> props = Properties(f);
                sb.AppendFormat("  {0} (#{1:X}) class {2:X8}, {3} slots\n", fi.Name ?? "feature", f.Index, fi.Kind, props.Count);
                string words;
                if (described.TryGetValue(f.Index, out words)) sb.AppendFormat("    = {0}\n", words);
                for (int i = 0; i < props.Count; i++)
                    if (props[i] >= 0) sb.AppendFormat("    [{0,2}] #{1,-4:X} {2}\n", i, props[i], Property(t, props[i]));
            }
            foreach (Patterns.PatternInfo p in Patterns.List(t))
                sb.AppendFormat("  {0} (#{1:X}) {2}\n    = {3}\n", p.Name ?? "pattern", p.Index, p.Kind, Patterns.Describe(p));
            return sb.ToString();
        }

        public const uint Extrude = 0x0CA91131, SurfaceExtrude = 0xE597A4F0, Profile = 0x22947391, ProfileLink = 0xF9884C43;

        // Extrude property slots (31 in both classes):
        //    0 operation enum    1 profile          2 direction axis (sketch normal)   3 reversed
        //    4 distance          5 taper            6 extent enum                      7 symmetric
        //    8 from plane       11 to plane        13, 18 to face (3D8924FD, 151280F0; older files)
        //   15 dimensions       16 surface body    24 to-next bodies
        //   27 distance two     28 taper two       29 extent two (present only with a second direction)
        // Names are the Inventor API's, so they compare directly with the truth .json.
        public static readonly string[] Operations = { null, "kNewBodyOperation", "kCutOperation", "kJoinOperation", "kIntersectOperation", "kSurfaceOperation" };
        static readonly Dictionary<int, string> Extents = new Dictionary<int, string> {
            { 1, "kDistanceExtent" }, { 2, "kDistanceExtent" }, { 4, "kToNextExtent" }, { 5, "kThroughAllExtent" },
            { 6, "kFromToExtent" }, { 7, "kToExtent" } };

        // A decoded feature; Get(key) gives a setting under its truth .json key, null where the API gives none.
        public abstract class FeatureInfo
        {
            public int Index;
            public string Name;
            public abstract string Get(string key);
        }

        public class Cylinder { public double[] Origin, Axis; public double RadiusU, RadiusV; }

        public class ExtrudeInfo : FeatureInfo
        {
            public string Sketch, Operation, Extent, Direction, Distance, Taper, From, To, ExtentTwo, DistanceTwo, TaperTwo;
            public string Profile;   // its loops (Profiles.Describe), null if they could not be read
            public double[] Axis;   // sketch normal (unit vector); the extrude runs along it, or against it when reversed
            public Plane ToPlane;   // the "to" face's plane, where the file keeps a copy of it
            public Cylinder ToCylinder;   // or its cylinder
            public Plane ToWork, FromWork;   // a work plane as the "to" (or from-to's "from"), where it is
            public int ToFaceItem = -1;   // the "to" face's FaceItem (2026 files name it only), for the B-rep to resolve
            public int SketchNode = -1, ProfileNode = -1;
            public string DistanceParam, DistanceTwoParam, TaperParam;   // the parameters' names, where they are in the list
            public double DistanceCm = double.NaN;   // distance extents only
            public double DistanceTwoCm = double.NaN;   // the second direction, when it is a distance
            public bool Tapered;

            public override string Get(string key)
            {
                switch (key)
                {
                    case "operation": return Operation;
                    case "extent": return Extent;
                    case "direction": return Direction;
                    case "distance": return Distance;
                    case "taper": return Taper;
                    case "extentTwo": return ExtentTwo;
                    case "distanceTwo": return DistanceTwo;
                    case "taperTwo": return TaperTwo;
                    case "profile": return Profile;
                    default: return null;
                }
            }
        }

        public class Plane
        {
            public double[] Origin, U, V, Normal;   // cm; U and V lie in the plane, Normal = U x V
        }

        static double[] Vec(byte[] d, int p)
        {
            return new[] { BitConverter.ToDouble(d, p), BitConverter.ToDouble(d, p + 8), BitConverter.ToDouble(d, p + 16) };
        }

        static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }

        public static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

        // DirectionAxis (CE52DF40): the sketch normal as three f64 at +0x32.
        static double[] AxisOf(NodeTable t, int index)
        {
            Node n = t.At(index);
            if (n == null || n.Data == null || n.Type == null || n.Type.Key != 0xCE52DF40) return null;
            int p = 0x32 + Sketches.Shift(n.Data);
            return n.Data.Length >= p + 24 ? Vec(n.Data, p) : null;
        }

        // Face surface copy (151280F0), kept in files before 2024 next to the face reference. After the header:
        // u8 1, u32 surface type at +0x33; a plane (0x19) then has f64 origin, u, v at +0x3B, +0x53, +0x6B.
        // A cylinder (0x49; the same form as ASM's cone: cosine 1, sine 0) has f64 cosine, sine at +0x3F, +0x47, origin at +0x4F, the
        // the two radii at +0x67, +0x6F, the axis at +0x77, a direction across it at +0x8F. +0xC7, +0xCF: the angle's range (-pi, pi).
        static Plane FacePlane(NodeTable t, int index, out uint surface, out Cylinder cylinder)
        {
            surface = 0;
            cylinder = null;
            Node n = t.At(index);
            if (n == null || n.Data == null || n.Type == null || n.Type.Key != 0x151280F0) return null;
            byte[] d = n.Data;
            int s = Sketches.Shift(d);
            if (d.Length < 0x37 + s) return null;
            surface = U32(d, 0x33 + s);
            if (surface == 0x49 && d.Length >= 0xA7 + s && BitConverter.ToDouble(d, 0x3F + s) == 1 && BitConverter.ToDouble(d, 0x47 + s) == 0)
            {
                cylinder = new Cylinder { Origin = Vec(d, 0x4F + s), RadiusU = BitConverter.ToDouble(d, 0x67 + s), RadiusV = BitConverter.ToDouble(d, 0x6F + s), Axis = Vec(d, 0x77 + s) };
                return null;
            }
            if (surface != 0x19 || d.Length < 0x83 + s) return null;
            Plane pl = new Plane { Origin = Vec(d, 0x3B + s), U = Vec(d, 0x53 + s), V = Vec(d, 0x6B + s) };
            pl.Normal = Cross(pl.U, pl.V);
            return pl;
        }

        static string Mm(double[] v)
        {
            return "(" + string.Join(", ", Array.ConvertAll(v, x => Units.Num(Math.Round(x * 10, 6) + 0.0))) + ")";
        }

        static string Dir(double[] v)
        {
            return "(" + string.Join(", ", Array.ConvertAll(v, x => Units.Num(Math.Round(x, 6) + 0.0))) + ")";
        }

        public static int EnumValue(NodeTable t, int index)
        {
            Node n = t.At(index);
            if (n == null || n.Data == null || n.Type == null || !n.TypeName.StartsWith("Enum")) return -1;
            int h = 0x22 + Sketches.Shift(n.Data);
            return n.Data.Length >= h + 4 ? BitConverter.ToUInt16(n.Data, h + 2) : -1;
        }

        static bool Flag(NodeTable t, int index)
        {
            Node n = t.At(index);
            return n != null && n.Data != null && n.Type != null && n.Type.Key == Boolean && n.Data[n.Data.Length - 1] != 0;
        }

        // A work plane (Plane, CE52DF42), origin planes included: its last 72 bytes are f64 origin, u, v (cm), u and v in
        // the plane, normal u x v, as in the face copies. The generated to-plane part's Work Plane1 reads (0, 0, 2) with u,
        // v along X and Y; the XY plane (0, 0, 0). Older and newer nodes differ in length before them, not in this.
        public static Plane WorkPlane(NodeTable t, int index)
        {
            Node n = t.At(index);
            if (n == null || n.Data == null || n.TypeName != "Plane" || n.Data.Length < 0x36 + 72) return null;
            int p = n.Data.Length - 72;
            Plane pl = new Plane { Origin = Vec(n.Data, p), U = Vec(n.Data, p + 24), V = Vec(n.Data, p + 48) };
            pl.Normal = Cross(pl.U, pl.V);
            double l = Math.Sqrt(Dot(pl.Normal, pl.Normal));
            if (l < 1e-9 || double.IsNaN(l)) return null;
            pl.Normal = new[] { pl.Normal[0] / l, pl.Normal[1] / l, pl.Normal[2] / l };
            return pl;
        }

        static string Target(NodeTable t, int index, Dictionary<int, string> names)
        {
            Node n = t.At(index);
            if (n == null || n.Type == null) return null;
            string name;
            return names.TryGetValue(index, out name) ? name : n.TypeName + " #" + index.ToString("X");
        }

        // Profile -> sketch name: a link node (F9884C43) cross-refs both the profile and its Sketch2D.
        static Dictionary<int, string> ProfileSketches(NodeTable t, Dictionary<int, string> names)
        {
            Dictionary<int, int> nodes;
            return ProfileSketches(t, names, out nodes);
        }

        static Dictionary<int, string> ProfileSketches(NodeTable t, Dictionary<int, string> names, out Dictionary<int, int> nodes)
        {
            nodes = new Dictionary<int, int>();
            Dictionary<int, string> r = new Dictionary<int, string>();
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Type.Key != ProfileLink || n.Data == null) continue;
                int sketch = -1;
                List<int> profiles = new List<int>();
                for (int p = 0; p + 4 <= n.Data.Length; p++)
                {
                    Node target = t.At(Sketches.XRef(n.Data, p));
                    if (target == null || target.Type == null) continue;
                    if (target.Type.Key == Sketches.Sketch2D) sketch = target.Index;
                    else if (target.Type.Key == Profile) profiles.Add(target.Index);
                }
                if (sketch < 0) continue;
                string name;
                if (!names.TryGetValue(sketch, out name)) name = "Sketch2D #" + sketch.ToString("X");
                foreach (int p in profiles) { r[p] = name; nodes[p] = sketch; }
            }
            return r;
        }

        static Dictionary<int, string> Formulas(NodeTable t)
        {
            Dictionary<int, string> formulas = new Dictionary<int, string>();
            foreach (Params.Decoded p in Params.Decode(t)) formulas[p.Index] = p.Formula;
            return formulas;
        }

        public static List<ExtrudeInfo> Extrudes(NodeTable t)
        {
            Dictionary<int, string> formulas = new Dictionary<int, string>();
            Dictionary<int, double> values = new Dictionary<int, double>();
            Dictionary<int, string> paramNames = new Dictionary<int, string>();
            foreach (Params.Decoded p in Params.Decode(t)) { formulas[p.Index] = p.Formula; values[p.Index] = p.Value; paramNames[p.Index] = p.Name; }
            Dictionary<int, string> names = Sketches.BrowserNames(t);
            Dictionary<int, int> sketchNodes;
            Dictionary<int, string> sketches = ProfileSketches(t, names, out sketchNodes);
            List<ExtrudeInfo> list = new List<ExtrudeInfo>();
            Dictionary<string, int> items = null;
            bool first = true;
            foreach (Info fi in List(t))
            {
                bool isFirst = first;
                first = false;
                if (fi.Kind != Extrude && fi.Kind != SurfaceExtrude) continue;
                List<int> s = Properties(fi.Node);
                Func<int, int> slot = i => i < s.Count ? s[i] : -1;
                Func<int, string> param = i => { string f; return formulas.TryGetValue(slot(i), out f) ? f : null; };
                ExtrudeInfo e = new ExtrudeInfo { Index = fi.Node.Index, Name = fi.Name };
                sketches.TryGetValue(slot(1), out e.Sketch);
                if (!sketchNodes.TryGetValue(slot(1), out e.SketchNode)) e.SketchNode = -1;
                e.ProfileNode = slot(1);
                if (e.SketchNode >= 0) e.Profile = Profiles.Describe(t, slot(1), e.SketchNode, items ?? (items = Naming.SketchItems(t)));
                paramNames.TryGetValue(slot(4), out e.DistanceParam);
                paramNames.TryGetValue(slot(27), out e.DistanceTwoParam);
                paramNames.TryGetValue(slot(5), out e.TaperParam);
                int op = EnumValue(t, slot(0)), extent = EnumValue(t, slot(6)), two = EnumValue(t, slot(29));
                if ((extent == 1 || extent == 2) && !values.TryGetValue(slot(4), out e.DistanceCm)) e.DistanceCm = double.NaN;
                if ((two == 1 || two == 2) && !values.TryGetValue(slot(27), out e.DistanceTwoCm)) e.DistanceTwoCm = double.NaN;
                double taper, taperTwo;
                e.Tapered = (values.TryGetValue(slot(5), out taper) && Math.Abs(taper) > 1e-12)
                    || (two >= 0 && values.TryGetValue(slot(28), out taperTwo) && Math.Abs(taperTwo) > 1e-12);
                e.Operation = op > 0 && op < Operations.Length ? Operations[op] : "operation " + op;
                // A first feature made as a join (the generator does this) is stored as one, but there is nothing
                // to join, so the API reports a new body. Real parts' first features are stored as new bodies.
                if (isFirst && op == 3) e.Operation = Operations[1];
                if (!Extents.TryGetValue(extent, out e.Extent)) e.Extent = "extent " + extent;
                e.Direction = Flag(t, slot(7)) ? "kSymmetricExtentDirection"
                    : Flag(t, slot(3)) ? "kNegativeExtentDirection" : "kPositiveExtentDirection";
                // The distance parameter stays after a switch to another extent; it only counts for distance.
                if (extent == 1 || extent == 2) e.Distance = param(4);
                e.Taper = param(5);
                e.Axis = AxisOf(t, slot(2));
                e.From = Target(t, slot(8), names);
                e.To = Target(t, slot(11), names);
                e.FromWork = WorkPlane(t, slot(8));
                e.ToWork = WorkPlane(t, slot(11));
                Node faceItem = t.At(slot(12));
                if (faceItem != null && faceItem.TypeName == "FaceItem") e.ToFaceItem = faceItem.Index;
                if (e.To == null && slot(13) >= 0)
                {
                    // A face: from 2024 only its topological name (FaceItem in slot 12, FaceId), so it needs the B-rep;
                    // before that, slot 18 also holds a copy of the face's surface.
                    uint surface;
                    e.ToPlane = FacePlane(t, slot(18), out surface, out e.ToCylinder);
                    e.To = e.ToPlane != null ? "face at " + Mm(e.ToPlane.Origin) + " mm, normal " + Dir(e.ToPlane.Normal)
                        : e.ToCylinder != null ? "cylindrical face, axis through " + Mm(e.ToCylinder.Origin) + " mm along " + Dir(e.ToCylinder.Axis) + ", radius " + Units.Num(Math.Round(e.ToCylinder.RadiusU * 10, 6)) + " mm"
                        : surface != 0 ? "curved face (surface type " + surface.ToString("X") + ")" : "face (by topological name)";
                }
                if (two >= 0)
                {
                    if (!Extents.TryGetValue(two, out e.ExtentTwo)) e.ExtentTwo = "extent " + two;
                    if (two == 1 || two == 2) e.DistanceTwo = param(27);
                    e.TaperTwo = param(28);
                }
                list.Add(e);
            }
            return list;
        }

        // "kCutOperation" -> "cut", "kThroughAllExtent" -> "through all".
        static string Words(string api)
        {
            if (api == null || !api.StartsWith("k")) return api;
            string s = System.Text.RegularExpressions.Regex.Replace(api.Substring(1), "(Operation|ExtentDirection|Extent)$", "");
            return System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        }

        public static string Describe(ExtrudeInfo e)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("{0} {1}, ", Words(e.Operation), e.Sketch ?? "(sketch?)");
            if (e.Extent == "kToExtent" || e.Extent == "kFromToExtent")
            {
                if (e.From != null) sb.Append("from " + e.From + " ");
                sb.Append("to " + (e.To ?? "?"));
            }
            else sb.Append(Words(e.Extent));
            if (e.Distance != null) sb.Append(" " + e.Distance);
            sb.Append(", " + Words(e.Direction));
            if (e.Axis != null) sb.Append(" " + Dir(e.Axis));
            if (e.Taper != null && Params.Normalize(e.Taper) != Params.Normalize("0 deg")) sb.Append(", taper " + e.Taper);
            if (e.ExtentTwo != null)
            {
                sb.Append("; other side " + Words(e.ExtentTwo));
                if (e.DistanceTwo != null) sb.Append(" " + e.DistanceTwo);
                if (e.TaperTwo != null && Params.Normalize(e.TaperTwo) != Params.Normalize("0 deg")) sb.Append(", taper " + e.TaperTwo);
            }
            return sb.ToString() + LoopWords(e.Profile);
        }

        public const uint Revolve = 0x0054015F, Line3D = 0x8EF06C89;

        // Revolve property slots (20):
        //    0 operation enum   1 profile   2 axis (Line3D)   3 extent enum (1 angle, 3 full)   4 angle
        //    5 direction enum (0 positive, 1 negative, 2 symmetric)   7 dimensions   11 profile again
        //   16 Boolean: true in every 2026 part and in 8 older ones (fasteners) that lack slot 11; meaning unknown
        static readonly Dictionary<int, string> RevolveExtents = new Dictionary<int, string> { { 1, "kAngleExtent" }, { 3, "kFullSweepExtent" } };
        static readonly string[] Directions = { "kPositiveExtentDirection", "kNegativeExtentDirection", "kSymmetricExtentDirection" };

        public class RevolveInfo : FeatureInfo
        {
            public string Sketch, Operation, Extent, Direction, Angle;
            public double[] AxisStart, AxisEnd;   // cm; a work axis used as the axis is projected into the sketch as a line
            public int SketchNode = -1, ProfileNode = -1;
            public string Profile, AngleParam;

            public override string Get(string key)
            {
                switch (key)
                {
                    case "operation": return Operation;
                    case "extent": return Extent;
                    case "direction": return Direction;
                    case "angle": return Angle;
                    case "sketch": return Sketch;
                    case "profile": return Profile;
                    default: return null;
                }
            }
        }

        // Line3D: f64 start point and vector (cm) at +0x3A and +0x52; the end is start + vector. The vector is the
        // sketch line's length for revolve axes (on a screw: 3.49 + 12.51 = 16 mm, its length) and a unit
        // vector for work axes. Returns {start, end}.
        public static double[][] LineOf(NodeTable t, int index)
        {
            Node n = t.At(index);
            if (n == null || n.Data == null || n.Type == null || n.Type.Key != Line3D) return null;
            int p = 0x3A + Sketches.Shift(n.Data);
            if (n.Data.Length < p + 48) return null;
            double[] a = Vec(n.Data, p), v = Vec(n.Data, p + 24);
            return new[] { a, new[] { a[0] + v[0], a[1] + v[1], a[2] + v[2] } };
        }

        public static List<RevolveInfo> Revolves(NodeTable t)
        {
            Dictionary<int, string> formulas = Formulas(t);
            Dictionary<int, string> paramNames = new Dictionary<int, string>();
            foreach (Params.Decoded p in Params.Decode(t)) paramNames[p.Index] = p.Name;
            Dictionary<int, int> sketchNodes;
            Dictionary<int, string> sketches = ProfileSketches(t, Sketches.BrowserNames(t), out sketchNodes);
            Dictionary<string, int> items = null;
            List<RevolveInfo> list = new List<RevolveInfo>();
            foreach (Info fi in List(t))
            {
                if (fi.Kind != Revolve) continue;
                List<int> s = Properties(fi.Node);
                Func<int, int> slot = i => i < s.Count ? s[i] : -1;
                RevolveInfo r = new RevolveInfo { Index = fi.Node.Index, Name = fi.Name };
                sketches.TryGetValue(slot(1), out r.Sketch);
                if (!sketchNodes.TryGetValue(slot(1), out r.SketchNode)) r.SketchNode = -1;
                r.ProfileNode = slot(1);
                paramNames.TryGetValue(slot(4), out r.AngleParam);
                if (r.SketchNode >= 0) r.Profile = Profiles.Describe(t, slot(1), r.SketchNode, items ?? (items = Naming.SketchItems(t)));
                int op = EnumValue(t, slot(0)), extent = EnumValue(t, slot(3)), dir = EnumValue(t, slot(5));
                r.Operation = op > 0 && op < Operations.Length ? Operations[op] : "operation " + op;
                if (!RevolveExtents.TryGetValue(extent, out r.Extent)) r.Extent = "extent " + extent;
                // The angle and direction stay stored after a switch to full; they only count for an angle.
                if (extent == 1)
                {
                    r.Direction = dir >= 0 && dir < Directions.Length ? Directions[dir] : "direction " + dir;
                    formulas.TryGetValue(slot(4), out r.Angle);
                }
                double[][] axis = LineOf(t, slot(2));
                if (axis != null) { r.AxisStart = axis[0]; r.AxisEnd = axis[1]; }
                list.Add(r);
            }
            return list;
        }

        public static string Describe(RevolveInfo r)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("{0} {1}, ", Words(r.Operation), r.Sketch ?? "(sketch?)");
            sb.Append(r.Extent == "kFullSweepExtent" ? "full" : r.Extent == "kAngleExtent" ? "angle " + r.Angle + ", " + Words(r.Direction) : r.Extent);
            if (r.AxisStart != null) sb.Append(", about the line " + Mm(r.AxisStart) + " to " + Mm(r.AxisEnd) + " mm");
            return sb.ToString() + LoopWords(r.Profile);
        }

        // ", 3 loops (1 hole)" from a decoded profile; ", loops not read" if it couldn't be.
        static string LoopWords(string profile)
        {
            if (profile == null) return ", loops not read";
            string[] loops = profile.Split(new[] { "; " }, StringSplitOptions.None);
            int holes = Array.FindAll(loops, l => l.StartsWith("-")).Length;
            return ", " + loops.Length + (loops.Length == 1 ? " loop" : " loops") + (holes == 0 ? "" : " (" + holes + (holes == 1 ? " hole)" : " holes)"));
        }

        public const uint Hole = 0x1F757D1A, ThreadNode = 0xA4087E1F, DrillPointNode = 0x5A7CB7BF, Point2D = 0xCE52DF35;

        // Hole property slots (28; 29 from 2024):
        //    0 hole type enum   1 diameter (the tap drill when tapped)   2 depth
        //    3 head diameter (counterbore, countersink, spot face)   4 head depth (counterbore, spot face)
        //    5 countersink angle   6 drill point angle (0 deg: flat bottom)   7 centres (HoleCenterPoints)
        //    8 transformation   9 extent enum (1 distance, 5 through all)   16 direction axis   17 reversed
        //   19 dimensions   20 thread depth   21 placement (holes not placed on sketch points)   24 bodies
        //   28 drill point (5A7CB7BF: u16 count, u16 value), from 2024, and not for through all
        // Sizes that don't apply to the hole type keep default values. The thread is a node of its own (Threads).
        static readonly string[] HoleTypes = { "kDrilledHole", "kCounterSinkHole", "kCounterBoreHole", "kSpotFaceHole" };
        // Holes not placed on sketch points: the type of the placement's definition node.
        static readonly Dictionary<uint, string> Placements = new Dictionary<uint, string> {
            { 0x3A083C7B, "kLinearPlacementType" }, { 0xC428DB42, "kPointPlacementType" }, { 0x1EF28758, "kConcentricPlacementType" } };
        // 0 and 1 seen; 2..4 assumed to follow the API enum.
        static readonly string[] DrillPoints = { "kFlatDrillPointType", "kAngledDrillPointType", "kFlatWDrillPointType",
            "kAngledVDrillPointType", "kAngledYDrillPointType" };

        public class HoleInfo : FeatureInfo
        {
            public string Sketch, HoleType, Extent, Direction, Distance, Diameter, TapDrill, CboreDiameter, CboreDepth,
                CsinkDiameter, CsinkAngle, SpotFaceDiameter, SpotFaceDepth, TipAngle, FlatBottom, DrillPoint, Placement, Centers;
            public List<double[]> CenterPoints = new List<double[]>();   // sketch x, y (cm)
            public List<int> CenterNodes = new List<int>();   // the same points' nodes
            public int SketchNode = -1;
            public string DiameterParam, DepthParam, HeadDiameterParam, HeadDepthParam, HeadAngleParam, TipParam;   // the parameters' names
            public double[] Position;   // model x, y, z (cm) of a hole not placed on sketch points
            public double[] Axis;
            public Thread Thread;

            public override string Get(string key)
            {
                Thread th = Thread;
                switch (key)
                {
                    case "holeType": return HoleType;
                    case "extent": return Extent;
                    case "direction": return Direction;
                    case "distance": return Distance;
                    case "diameter": return Diameter;
                    case "cboreDiameter": return CboreDiameter;
                    case "cboreDepth": return CboreDepth;
                    case "csinkDiameter": return CsinkDiameter;
                    case "csinkAngle": return CsinkAngle;
                    case "spotFaceDiameter": return SpotFaceDiameter;
                    case "spotFaceDepth": return SpotFaceDepth;
                    case "tipAngle": return TipAngle;
                    case "flatBottom": return FlatBottom;
                    case "drillPoint": return DrillPoint;
                    case "placement": return Placement;
                    case "centers": return Centers;
                    case "tapped": return th != null ? "true" : "false";
                    case "threadType": return th == null ? null : th.Type;
                    case "thread": return th == null ? null : th.Designation;
                    case "threadClass": return th == null ? null : th.Class;
                    case "rightHanded": return th == null ? null : th.RightHanded ? "true" : "false";
                    case "fullThreadDepth": return th == null ? null : th.FullDepth ? "true" : "false";
                    case "threadDepth": return th == null || th.FullDepth ? null : th.Depth;
                    default: return null;
                }
            }
        }

        public class Thread
        {
            public string Size, Designation, Type, Depth, Class;
            public bool RightHanded, FullDepth, Internal;
        }

        // {u32 length, UTF-16 text}
        static string Text16(byte[] d, ref int p)
        {
            int n = (int)U32(d, p);
            if (n < 0 || n > 512 || p + 4 + 2 * n > d.Length) throw new Exception("no string at +" + p.ToString("X"));
            string s = Encoding.Unicode.GetString(d, p + 4, 2 * n);
            p += 4 + 2 * n;
            return s;
        }

        // A hole's thread (A4087E1F), keyed by the hole it belongs to (cross ref at +0x12). From +0x1E, strings:
        // nominal size, designation, thread type, depth ("" for full depth); u8 flags (0x20 full depth, 0x02 right
        // hand, 0x04 internal; 0xC8 always set so far); u32 0x10, 7 bytes, a string (unknown: "" in 2026 parts, "0.30" in an
        // older one), u32 0; class; then the thread table's diameters as text. Thread features (28AA1906) own one too.
        public static Dictionary<int, Thread> Threads(NodeTable t)
        {
            Dictionary<int, Thread> r = new Dictionary<int, Thread>();
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Type.Key != ThreadNode || n.Data == null) continue;
                byte[] d = n.Data;
                int s = Sketches.Shift(d), p = 0x1E + s;
                if (d.Length < p + 4) continue;
                Thread th = new Thread();
                try
                {
                    th.Size = Text16(d, ref p);
                    th.Designation = Text16(d, ref p);
                    th.Type = Text16(d, ref p);
                    th.Depth = Text16(d, ref p);
                    byte flags = d[p];
                    th.FullDepth = (flags & 0x20) != 0;
                    th.RightHanded = (flags & 0x02) != 0;
                    th.Internal = (flags & 0x04) != 0;
                    p += 12;
                    Text16(d, ref p);
                    p += 4;
                    th.Class = Text16(d, ref p);
                }
                catch (Exception) { continue; }
                r[Sketches.XRef(d, 0x12 + s)] = th;
            }
            return r;
        }

        public static List<HoleInfo> Holes(NodeTable t)
        {
            Dictionary<int, Params.Decoded> ps = new Dictionary<int, Params.Decoded>();
            foreach (Params.Decoded p in Params.Decode(t)) ps[p.Index] = p;
            Dictionary<int, string> names = Sketches.BrowserNames(t);
            Dictionary<int, Thread> threads = Threads(t);
            List<HoleInfo> list = new List<HoleInfo>();
            foreach (Info fi in List(t))
            {
                if (fi.Kind != Hole) continue;
                List<int> s = Properties(fi.Node);
                Func<int, int> slot = i => i < s.Count ? s[i] : -1;
                Func<int, string> param = i => { Params.Decoded p; return ps.TryGetValue(slot(i), out p) ? p.Formula : null; };
                HoleInfo h = new HoleInfo { Index = fi.Node.Index, Name = fi.Name };
                int type = EnumValue(t, slot(0)), extent = EnumValue(t, slot(9));
                h.HoleType = type >= 0 && type < HoleTypes.Length ? HoleTypes[type] : "hole type " + type;
                if (!Extents.TryGetValue(extent, out h.Extent)) h.Extent = "extent " + extent;
                h.Direction = Flag(t, slot(17)) ? "kNegativeExtentDirection" : "kPositiveExtentDirection";
                h.Axis = AxisOf(t, slot(16));
                threads.TryGetValue(fi.Node.Index, out h.Thread);
                Func<int, string> paramName = i => { Params.Decoded p; return ps.TryGetValue(slot(i), out p) ? p.Name : null; };
                h.DiameterParam = paramName(1);
                h.DepthParam = paramName(2);
                h.HeadDiameterParam = paramName(3);
                h.HeadDepthParam = paramName(4);
                h.HeadAngleParam = paramName(5);
                h.TipParam = paramName(6);
                if (h.Thread != null) h.TapDrill = param(1); else h.Diameter = param(1);
                if (extent == 1) h.Distance = param(2);
                if (type == 2) { h.CboreDiameter = param(3); h.CboreDepth = param(4); }
                if (type == 1) { h.CsinkDiameter = param(3); h.CsinkAngle = param(5); }
                if (type == 3) { h.SpotFaceDiameter = param(3); h.SpotFaceDepth = param(4); }
                if (h.Thread != null && !h.Thread.FullDepth) h.Thread.Depth = param(20) ?? h.Thread.Depth;
                // Drill point: its own enum from 2024; before that, and for through all, a 0 deg tip angle means flat.
                Node dp = t.At(slot(28));
                int point = -1;
                if (dp != null && dp.Type != null && dp.Type.Key == DrillPointNode && dp.Data.Length >= 0x26 + Sketches.Shift(dp.Data))
                    point = BitConverter.ToUInt16(dp.Data, 0x24 + Sketches.Shift(dp.Data));
                Params.Decoded tip;
                bool flat = point >= 0 ? point == 0 || point == 2 : !ps.TryGetValue(slot(6), out tip) || Math.Abs(tip.Value) < 1e-9;
                h.FlatBottom = flat ? "true" : "false";
                // A through-all hole has no bottom, so the API gives no tip angle for it.
                if (!flat && extent == 1) h.TipAngle = param(6);
                if (extent == 1) h.DrillPoint = point >= 0 && point < DrillPoints.Length ? DrillPoints[point] : point >= 0 ? "drill point " + point : DrillPoints[flat ? 0 : 1];
                // Centres: a list of sketch points, after the header.
                Node centers = t.At(slot(7));
                if (centers != null && centers.Type != null && centers.TypeName == "HoleCenterPoints")
                {
                    h.Placement = "kSketchPlacementType";
                    List<int> pts;
                    Sketches.ReadList(centers.Data, 0x22 + Sketches.Shift(centers.Data), out pts);
                    h.Centers = pts.Count.ToString();
                    foreach (int i in pts)
                    {
                        Node p = t.At(i);
                        if (p == null || p.Data == null || p.Type == null || p.Type.Key != Point2D) continue;
                        int ps2 = Sketches.Shift(p.Data);
                        h.CenterPoints.Add(new[] { BitConverter.ToDouble(p.Data, 0x2A + ps2), BitConverter.ToDouble(p.Data, 0x32 + ps2) });
                        h.CenterNodes.Add(i);
                        if (h.Sketch == null) { h.Sketch = Target(t, Sketches.XRef(p.Data, 0x26 + ps2), names); h.SketchNode = Sketches.XRef(p.Data, 0x26 + ps2); }
                    }
                }
                else
                {
                    // Placement (7256922C): cross refs at +0x22 transformation, +0x26 the hole's position (Point3D:
                    // f64 x, y, z at +0x32, cm), +0x2A the definition, whose type says how it is placed.
                    Node pl = t.At(slot(21));
                    if (pl != null && pl.Data != null && pl.TypeName == "Placement" && pl.Data.Length >= 0x2E + Sketches.Shift(pl.Data))
                    {
                        int ps2 = Sketches.Shift(pl.Data);
                        Node def = t.At(Sketches.XRef(pl.Data, 0x2A + ps2)), at = t.At(Sketches.XRef(pl.Data, 0x26 + ps2));
                        string how;
                        h.Placement = def != null && def.Type != null && Placements.TryGetValue(def.Type.Key, out how) ? how : "placement (not decoded)";
                        if (at != null && at.Data != null && at.TypeName == "Point3D" && at.Data.Length >= 0x4A + Sketches.Shift(at.Data))
                        {
                            h.Position = Vec(at.Data, 0x32 + Sketches.Shift(at.Data));
                            h.Centers = "1";
                        }
                    }
                }
                list.Add(h);
            }
            return list;
        }

        static readonly Dictionary<string, string> HoleWords = new Dictionary<string, string> {
            { "kDrilledHole", "drilled" }, { "kCounterSinkHole", "countersink" }, { "kCounterBoreHole", "counterbore" }, { "kSpotFaceHole", "spot face" } };
        static readonly Dictionary<string, string> PlacementWords = new Dictionary<string, string> {
            { "kLinearPlacementType", "placed from two edges" }, { "kPointPlacementType", "placed on a point" },
            { "kConcentricPlacementType", "placed concentric" } };

        public static string Describe(HoleInfo h)
        {
            StringBuilder sb = new StringBuilder();
            string type;
            if (!HoleWords.TryGetValue(h.HoleType, out type)) type = h.HoleType;
            Thread th = h.Thread;
            sb.Append(type + " hole");
            if (th != null) sb.AppendFormat(", thread {0} ({1}, {2}, {3} hand, {4}), tap drill {5}", th.Designation, th.Type, th.Class,
                th.RightHanded ? "right" : "left", th.FullDepth ? "full depth" : "depth " + th.Depth, h.TapDrill);
            else sb.Append(", diameter " + h.Diameter);
            sb.Append(", " + (h.Extent == "kDistanceExtent" ? "depth " + h.Distance : Words(h.Extent)));
            sb.Append(", " + Words(h.Direction));
            if (h.Axis != null) sb.Append(" " + Dir(h.Axis));
            if (h.CboreDiameter != null) sb.AppendFormat(", counterbore {0} x {1} deep", h.CboreDiameter, h.CboreDepth);
            if (h.CsinkDiameter != null) sb.AppendFormat(", countersink {0} at {1}", h.CsinkDiameter, h.CsinkAngle);
            if (h.SpotFaceDiameter != null) sb.AppendFormat(", spot face {0} x {1} deep", h.SpotFaceDiameter, h.SpotFaceDepth);
            if (h.Extent == "kDistanceExtent") sb.Append(h.FlatBottom == "true" ? ", flat bottom" : ", tip " + h.TipAngle);
            if (h.CenterPoints.Count > 0)
                sb.AppendFormat(", at {0} in {1}", string.Join(", ", h.CenterPoints.ConvertAll(c => Mm(c)).ToArray()) + " mm", h.Sketch ?? "(sketch?)");
            else if (h.Placement != null)
            {
                string how;
                if (!PlacementWords.TryGetValue(h.Placement, out how)) how = h.Placement;
                sb.Append(", " + how + (h.Position != null ? " at " + Mm(h.Position) + " mm" : ""));
            }
            return sb.ToString();
        }

        public const uint Fillet = 0xF1F715DC, Chamfer = 0xF900713F;

        // Fillet property slots (17):
        //    0 edge sets (FilletEdgeSetsConstantR: kind-2 list at +0x22 of FilletEdgeSetConstantR)
        //    2 automatic edge chain   3 rolling ball where possible   4 smooth radius transition (see below)
        //    5 preserve all features   7 dimensions   8 roll along sharp edges   11 fillet type enum (0 edge)
        //   15 ObjectCollection (in slot 16 instead in the all-rounds test and in 6 fastener fillets of real parts; unknown)
        // Slot 4 holds what was passed when the fillet was made, and the API reports the opposite, so it is inverted
        // here to compare. (Inventor only uses it for variable radius fillets.)
        // Edge set (FilletEdgeSetConstantR): cross refs at +0x22 EdgeCollection (none for all rounds), +0x26 radius,
        // +0x2A selection mode (4949374A: u16 count, u16 value; 0 the edges given, 4 all rounds), +0x2E, +0x32
        // Booleans (false in every test; unknown).
        // EdgeCollection: kind-2 list at +0x22 of EdgeItems, which name B-rep edges (EdgeId: creator/id pairs), so
        // finding the edges themselves needs the B-rep.
        static readonly string[] FilletTypes = { "kEdgeFillet", "kFaceFillet", "kFullRoundFillet" };

        public class EdgeSet
        {
            public string Radius;
            public double RadiusCm = double.NaN;
            public string RadiusParam;
            public List<int> Items = new List<int>();   // its EdgeItems
            public int Edges, Mode;
            public bool Unknown1, Unknown2;   // the Booleans at +0x2E and +0x32
        }

        public class FilletInfo : FeatureInfo
        {
            public string FilletType, AutomaticEdgeChain, SmoothRadiusTransition, RollAlongSharpEdges, RollingBallWherePossible, PreserveAllFeatures;
            public List<EdgeSet> Sets = new List<EdgeSet>();

            public override string Get(string key)
            {
                switch (key)
                {
                    case "filletType": return FilletType;
                    case "automaticEdgeChain": return AutomaticEdgeChain;
                    case "smoothRadiusTransition": return SmoothRadiusTransition;
                    case "rollAlongSharpEdges": return RollAlongSharpEdges;
                    case "rollingBallWherePossible": return RollingBallWherePossible;
                    case "preserveAllFeatures": return PreserveAllFeatures;
                    case "sets": return Sets.Count.ToString();
                }
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(key, @"^(radius|edges|allFillets|allRounds)(\d+)$");
                int i = m.Success ? int.Parse(m.Groups[2].Value) - 1 : -1;
                if (i < 0 || i >= Sets.Count) return null;
                EdgeSet s = Sets[i];
                switch (m.Groups[1].Value)
                {
                    case "radius": return s.Radius;
                    case "edges": return s.Edges.ToString();
                    // The mode value for all fillets is not seen yet, so another value shows as itself.
                    case "allRounds": return s.Mode == 4 ? "true" : s.Mode == 0 ? "false" : "mode " + s.Mode;
                    default: return s.Mode == 0 || s.Mode == 4 ? "false" : "mode " + s.Mode;
                }
            }
        }

        static string Bool(NodeTable t, int index) { return Flag(t, index) ? "true" : "false"; }

        // Items in the kind-2 list at +0x22 of a collection node (edges, faces, edge sets); -1 if it isn't one.
        static List<int> Items(NodeTable t, int index)
        {
            Node n = t.At(index);
            if (n == null || n.Data == null || n.Data.Length < 0x2A + Sketches.Shift(n.Data)) return null;
            int p = 0x22 + Sketches.Shift(n.Data);
            if (U32(n.Data, p) != 0x30000002) return null;
            List<int> items;
            Sketches.ReadList(n.Data, p, out items);
            return items;
        }

        public static List<FilletInfo> Fillets(NodeTable t)
        {
            Dictionary<int, string> formulas = new Dictionary<int, string>();
            Dictionary<int, double> values = new Dictionary<int, double>();
            Dictionary<int, string> paramNames = new Dictionary<int, string>();
            foreach (Params.Decoded p in Params.Decode(t)) { formulas[p.Index] = p.Formula; values[p.Index] = p.Value; paramNames[p.Index] = p.Name; }
            List<FilletInfo> list = new List<FilletInfo>();
            foreach (Info fi in List(t))
            {
                if (fi.Kind != Fillet) continue;
                List<int> s = Properties(fi.Node);
                Func<int, int> slot = i => i < s.Count ? s[i] : -1;
                FilletInfo f = new FilletInfo { Index = fi.Node.Index, Name = fi.Name };
                int type = EnumValue(t, slot(11));
                f.FilletType = type >= 0 && type < FilletTypes.Length ? FilletTypes[type] : "fillet type " + type;
                f.AutomaticEdgeChain = Bool(t, slot(2));
                f.RollingBallWherePossible = Bool(t, slot(3));
                f.SmoothRadiusTransition = Flag(t, slot(4)) ? "false" : "true";
                f.PreserveAllFeatures = Bool(t, slot(5));
                f.RollAlongSharpEdges = Bool(t, slot(8));
                foreach (int si in Items(t, slot(0)) ?? new List<int>())
                {
                    Node n = t.At(si);
                    if (n == null || n.Data == null || n.TypeName != "FilletEdgeSetConstantR") continue;
                    int sh = Sketches.Shift(n.Data);
                    EdgeSet es = new EdgeSet();
                    List<int> edges = Items(t, Sketches.XRef(n.Data, 0x22 + sh));
                    es.Edges = edges == null ? 0 : edges.Count;
                    if (edges != null) es.Items = edges;
                    paramNames.TryGetValue(Sketches.XRef(n.Data, 0x26 + sh), out es.RadiusParam);
                    formulas.TryGetValue(Sketches.XRef(n.Data, 0x26 + sh), out es.Radius);
                    values.TryGetValue(Sketches.XRef(n.Data, 0x26 + sh), out es.RadiusCm);
                    Node mode = t.At(Sketches.XRef(n.Data, 0x2A + sh));
                    es.Mode = mode != null && mode.Data != null && mode.Data.Length >= 0x26 + Sketches.Shift(mode.Data)
                        ? BitConverter.ToUInt16(mode.Data, 0x24 + Sketches.Shift(mode.Data)) : -1;
                    es.Unknown1 = Flag(t, Sketches.XRef(n.Data, 0x2E + sh));
                    es.Unknown2 = Flag(t, Sketches.XRef(n.Data, 0x32 + sh));
                    f.Sets.Add(es);
                }
                list.Add(f);
            }
            return list;
        }

        public static string Describe(FilletInfo f)
        {
            List<string> sets = new List<string>();
            foreach (EdgeSet s in f.Sets)
                sets.Add(s.Radius + (s.Mode == 4 ? " on all rounds" : s.Mode != 0 ? " (selection mode " + s.Mode + ")" : "")
                    + (s.Edges > 0 ? " on " + s.Edges + (s.Edges == 1 ? " edge" : " edges") : ""));
            List<string> options = new List<string>();
            if (f.AutomaticEdgeChain == "true") options.Add("tangent chain");
            if (f.RollingBallWherePossible == "true") options.Add("rolling ball");
            if (f.RollAlongSharpEdges == "true") options.Add("roll along sharp edges");
            if (f.PreserveAllFeatures == "true") options.Add("preserve features");
            string type = f.FilletType == "kEdgeFillet" ? "fillet" : f.FilletType;
            return type + " " + string.Join(", ", sets.ToArray()) + (options.Count > 0 ? "; " + string.Join(", ", options.ToArray()) : "");
        }

        // Chamfer property slots (13):
        //    0 edges (EdgeCollection)   1 face (FaceCollection; distance and angle, two distances)
        //    2 distance (distance one)  3 distance two   4 chamfer type enum (ChamferTypes)   5 automatic edge chain
        //    6 corner setback (only counts for distance chamfers)   8 dimensions   9 preserve all features
        //   10 angle   11 ObjectCollection
        // Sizes that don't apply to the type keep default values (distance two = distance, angle 45 deg).
        static readonly string[] ChamferTypes = { "kDistance", "kDistanceAndAngle", "kTwoDistances" };

        public class ChamferInfo : FeatureInfo
        {
            public string ChamferType, Distance, Angle, DistanceOne, DistanceTwo, Face, AutomaticEdgeChain, CornerSetback, PreserveAllFeatures;
            public string SizeParam, Size2Param, AngleParam;   // the parameters' names
            public List<int> EdgeItems = new List<int>(), FaceItems = new List<int>();
            public int Edges;

            public override string Get(string key)
            {
                switch (key)
                {
                    case "chamferType": return ChamferType;
                    case "distance": return Distance;
                    case "angle": return Angle;
                    case "distanceOne": return DistanceOne;
                    case "distanceTwo": return DistanceTwo;
                    case "face": return Face;
                    case "edges": return Edges.ToString();
                    case "automaticEdgeChain": return AutomaticEdgeChain;
                    case "cornerSetback": return CornerSetback;
                    case "preserveAllFeatures": return PreserveAllFeatures;
                    default: return null;
                }
            }
        }

        public static List<ChamferInfo> Chamfers(NodeTable t)
        {
            Dictionary<int, string> formulas = Formulas(t);
            Dictionary<int, string> paramNames = new Dictionary<int, string>();
            foreach (Params.Decoded p in Params.Decode(t)) paramNames[p.Index] = p.Name;
            List<ChamferInfo> list = new List<ChamferInfo>();
            foreach (Info fi in List(t))
            {
                if (fi.Kind != Chamfer) continue;
                List<int> s = Properties(fi.Node);
                Func<int, int> slot = i => i < s.Count ? s[i] : -1;
                Func<int, string> param = i => { string f; return formulas.TryGetValue(slot(i), out f) ? f : null; };
                ChamferInfo c = new ChamferInfo { Index = fi.Node.Index, Name = fi.Name };
                int type = EnumValue(t, slot(4));
                c.ChamferType = type >= 0 && type < ChamferTypes.Length ? ChamferTypes[type] : "chamfer type " + type;
                if (type == 0) c.Distance = param(2);
                if (type == 1) { c.Distance = param(2); c.Angle = param(10); }
                if (type == 2) { c.DistanceOne = param(2); c.DistanceTwo = param(3); }
                paramNames.TryGetValue(slot(2), out c.SizeParam);
                paramNames.TryGetValue(slot(3), out c.Size2Param);
                paramNames.TryGetValue(slot(10), out c.AngleParam);
                List<int> faces = Items(t, slot(1));
                if (faces != null && faces.Count > 0) { c.Face = "true"; c.FaceItems = faces; }
                List<int> edges = Items(t, slot(0));
                c.Edges = edges == null ? 0 : edges.Count;
                if (edges != null) c.EdgeItems = edges;
                c.AutomaticEdgeChain = Bool(t, slot(5));
                c.CornerSetback = type == 0 ? Bool(t, slot(6)) : "false";
                c.PreserveAllFeatures = Bool(t, slot(9));
                list.Add(c);
            }
            return list;
        }

        public static string Describe(ChamferInfo c)
        {
            StringBuilder sb = new StringBuilder("chamfer ");
            if (c.ChamferType == "kDistance") sb.Append(c.Distance);
            else if (c.ChamferType == "kDistanceAndAngle") sb.Append(c.Distance + " at " + c.Angle + " from a face");
            else if (c.ChamferType == "kTwoDistances") sb.Append(c.DistanceOne + " x " + c.DistanceTwo + " from a face");
            else sb.Append(c.ChamferType);
            sb.Append(" on " + c.Edges + (c.Edges == 1 ? " edge" : " edges"));
            List<string> options = new List<string>();
            if (c.AutomaticEdgeChain == "true") options.Add("tangent chain");
            if (c.CornerSetback == "true") options.Add("corner setback");
            if (c.PreserveAllFeatures == "true") options.Add("preserve features");
            if (options.Count > 0) sb.Append("; " + string.Join(", ", options.ToArray()));
            return sb.ToString();
        }

        static readonly System.Text.RegularExpressions.Regex TruthFeature = new System.Text.RegularExpressions.Regex(
            "\\{\"name\": \"(?<name>[^\"]*)\", \"type\": \"(?<type>\\w+)\"(?<rest>[^}]*)\\}");
        static readonly System.Text.RegularExpressions.Regex Field = new System.Text.RegularExpressions.Regex(
            "\"(?<key>\\w+)\": \"(?<value>[^\"]*)\"");

        // Per truth feature type: what it is called here, the field present only in truth with details, the keys compared.
        static readonly Dictionary<string, string[]> Checked = new Dictionary<string, string[]> {
            // "profile" (its loops) is compared where the truth has it (truth from before it was recorded doesn't).
            { "kExtrudeFeatureObject", new[] { "extrude", "operation", "operation", "extent", "direction", "distance", "taper", "extentTwo", "distanceTwo", "taperTwo", "profile" } },
            { "kRevolveFeatureObject", new[] { "revolve", "operation", "operation", "extent", "direction", "angle", "sketch", "profile" } },
            { "kHoleFeatureObject", new[] { "hole", "holeType", "holeType", "extent", "direction", "distance", "diameter", "cboreDiameter", "cboreDepth",
                "csinkDiameter", "csinkAngle", "spotFaceDiameter", "spotFaceDepth", "tipAngle", "flatBottom", "drillPoint", "placement", "centers",
                "tapped", "threadType", "thread", "threadClass", "rightHanded", "fullThreadDepth", "threadDepth" } },
            { "kFilletFeatureObject", FilletKeys() },
            // "edges" is compared too, though the API can't give it for saved parts (so far always left out).
            { "kChamferFeatureObject", new[] { "chamfer", "chamferType", "chamferType", "distance", "angle", "distanceOne", "distanceTwo",
                "edges", "face", "automaticEdgeChain", "cornerSetback", "preserveAllFeatures" } },
            // Mirrors and patterns (Patterns.cs); "transforms" is each copy's placement, rebuilt from the settings.
            { "kMirrorFeatureObject", new[] { "mirror", "ofBody", "plane", "ofBody", "parents", "computeType", "elements", "transforms" } },
            { "kRectangularPatternFeatureObject", new[] { "rectangular pattern", "xCount", "xCount", "xSpacing", "xSpacingType", "xMidPlane",
                "yCount", "ySpacing", "ySpacingType", "yMidPlane", "orientation", "ofBody", "parents", "computeType", "elements", "transforms" } },
            { "kCircularPatternFeatureObject", new[] { "circular pattern", "count", "count", "angle", "fitWithinAngle", "positioning", "midPlane",
                "lockRotation", "naturalAxis", "axis", "ofBody", "parents", "computeType", "elements", "transforms" } },
            // Thread features and shells (MoreFeatures.cs).
            { "kThreadFeatureObject", new[] { "thread feature", "fullDepth", "threadType", "thread", "threadClass", "internal", "rightHanded",
                "fullDepth", "directionReversed", "depth", "offset" } },
            { "kShellFeatureObject", new[] { "shell", "direction", "direction", "thickness", "removedFaces", "faceSets", "faceSet1" } },
            { "kSweepFeatureObject", new[] { "sweep", "sweepType", "operation", "sweepType", "orientation", "scaling", "taper", "twist", "sketch", "profile", "path", "guideRail" } },
            { "kLoftFeatureObject", new[] { "loft", "loftType", "operation", "loftType", "closed", "mergeTangentFaces", "firstCondition", "lastCondition", "firstAngle", "firstImpact", "lastAngle", "lastImpact", "sections", "rails", "rail1", "centerline" } },
            // Sheet metal: "Face" features are stored as Plates (see Compare).
            { "kFaceFeatureObject", new[] { "plate", "operation", "operation", "direction", "sketch", "profile" } },
            { "kFlangeFeatureObject", new[] { "flange", "height", "height", "flangeAngle", "bendRadius" } },
            { "kBendFeatureObject", new[] { "bend", "bendRadius", "bendRadius" } },
            { "kCornerFeatureObject", new[] { "corner feature", "cornerType", "cornerType", "gapWidth" } },
            { "kNonParametricBaseFeatureObject", new[] { "imported body", "isSolid", "isSolid", "isComposite" } } };

        // Fillet edge sets are numbered from 1; up to 16 are compared.
        static string[] FilletKeys()
        {
            List<string> k = new List<string> { "fillet", "filletType", "filletType", "automaticEdgeChain", "smoothRadiusTransition",
                "rollAlongSharpEdges", "rollingBallWherePossible", "preserveAllFeatures", "sets" };
            for (int i = 1; i <= 16; i++) { k.Add("radius" + i); k.Add("edges" + i); k.Add("allFillets" + i); k.Add("allRounds" + i); }
            return k.ToArray();
        }

        // Sizes and angles: compared as formulas.
        static readonly HashSet<string> FormulaKeys = new HashSet<string> { "distance", "taper", "distanceTwo", "taperTwo", "angle", "diameter",
            "cboreDiameter", "cboreDepth", "csinkDiameter", "csinkAngle", "spotFaceDiameter", "spotFaceDepth", "tipAngle", "threadDepth", "distanceOne",
            "xCount", "xSpacing", "yCount", "ySpacing", "count", "depth", "offset", "thickness", "twist", "bendRadius", "height", "flangeAngle", "firstAngle", "firstImpact", "lastAngle", "lastImpact" };

        // Decoded extrudes, revolves and holes vs a truth .json from 'invre gen' or 'invre truth'; counted adds up the
        // features checked by kind. Truth from before the details only names the features, and is skipped.
        public static List<string> Compare(NodeTable t, string truthJson, SortedDictionary<string, int> counted)
        {
            Dictionary<string, FeatureInfo> byName = new Dictionary<string, FeatureInfo>();
            foreach (ExtrudeInfo e in Extrudes(t)) byName[e.Name] = e;
            foreach (RevolveInfo r in Revolves(t)) byName[r.Name] = r;
            foreach (HoleInfo h in Holes(t)) byName[h.Name] = h;
            foreach (FilletInfo f in Fillets(t)) byName[f.Name] = f;
            foreach (ChamferInfo c in Chamfers(t)) byName[c.Name] = c;
            foreach (Patterns.PatternInfo p in Patterns.List(t)) if (p.Name != null) byName[p.Name] = p;
            foreach (MoreFeatures.ThreadInfo th in MoreFeatures.Threads(t)) byName[th.Name] = th;
            foreach (MoreFeatures.ShellInfo sh in MoreFeatures.Shells(t)) byName[sh.Name] = sh;
            foreach (MoreFeatures.SweepInfo sw in MoreFeatures.Sweeps(t)) byName[sw.Name] = sw;
            foreach (MoreFeatures.LoftInfo lf in MoreFeatures.Lofts(t)) byName[lf.Name] = lf;
            foreach (MoreFeatures.SheetInfo sm in MoreFeatures.Sheets(t)) { byName[sm.Name] = sm; if (sm.Alias != null) byName[sm.Alias] = sm; }
            foreach (MoreFeatures.ImportedInfo im in MoreFeatures.ImportedBodies(t)) byName[im.Name] = im;
            List<string> bad = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in TruthFeature.Matches(truthJson))
            {
                string[] spec;
                if (!Checked.TryGetValue(m.Groups["type"].Value, out spec)) continue;
                Dictionary<string, string> truth = new Dictionary<string, string>();
                foreach (System.Text.RegularExpressions.Match f in Field.Matches(m.Groups["rest"].Value)) truth[f.Groups["key"].Value] = f.Groups["value"].Value;
                if (!truth.ContainsKey(spec[1])) continue;
                string name = m.Groups["name"].Value;
                // The API calls a plate "Face1" where the browser node says "Plate1".
                if (m.Groups["type"].Value == "kFaceFeatureObject" && name.StartsWith("Face")) name = "Plate" + name.Substring(4);
                FeatureInfo e;
                if (!byName.TryGetValue(name, out e)) { bad.Add(name + ": not decoded"); continue; }
                int c;
                counted.TryGetValue(spec[0], out c);
                counted[spec[0]] = c + 1;
                for (int k = 2; k < spec.Length; k++)
                {
                    string key = spec[k], want, got = e.Get(key);
                    truth.TryGetValue(key, out want);
                    // The API gives no edge list for saved fillets and chamfers, so those counts are checked only if it does.
                    if (want == null && (key.StartsWith("edges") || key == "profile")) continue;
                    // Copies placed by a lock-rotation circular pattern need its reference point, not decoded.
                    if (key == "transforms" && got == "unknown") { counted.TryGetValue("transforms not rebuilt", out c); counted["transforms not rebuilt"] = c + 1; continue; }
                    bool same = want == null || got == null ? want == got
                        : key == "transforms" ? Patterns.SameTransforms(got, want)
                        : key == "axis" ? Patterns.SameAxis(got, want)
                        : key == "gapWidth" ? Assemblies.SameText(got, want)
                        : FormulaKeys.Contains(key) || key.StartsWith("radius") ? Params.Normalize(got) == Params.Normalize(want) : got == want;
                    if (!same) bad.Add(name + ": " + key + " " + (got ?? "none") + " (Inventor: " + (want ?? "none") + ")");
                }
            }
            return bad;
        }

        // "3 extrudes, 1 hole"
        public static string Counted(SortedDictionary<string, int> counted)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in counted) parts.Add(kv.Value + " " + kv.Key + (kv.Value == 1 ? "" : "s"));
            return parts.Count == 0 ? "no features" : string.Join(", ", parts.ToArray());
        }

        // For one feature class across a folder: what each property slot holds (type, enum value, Boolean), counted.
        public static string Slots(string dir, uint kind)
        {
            SortedDictionary<int, Dictionary<string, int>> seen = new SortedDictionary<int, Dictionary<string, int>>();
            int features = 0;
            foreach (string file in System.IO.Directory.GetFiles(dir, "*.ipt", System.IO.SearchOption.AllDirectories))
            {
                NodeTable t;
                try
                {
                    Segment dc = null;
                    foreach (Segment s in Segment.ReadAll(file)) if (s.Name == "PmDCSegment") dc = s;
                    if (dc == null) continue;
                    t = NodeTable.Parse(dc.Meta, dc.Data);
                }
                catch (Exception) { continue; }
                foreach (Info i in List(t))
                {
                    if (i.Kind != kind) continue;
                    features++;
                    List<int> props = Properties(i.Node);
                    for (int s = 0; s < props.Count; s++)
                    {
                        string what = "-";
                        Node n = t.At(props[s]);
                        if (n != null && n.Type != null)
                            what = n.Type.Key == Boolean || n.TypeName.StartsWith("Enum") || n.Type.Key == 0xCEFD3973 ? Shape(t, props[s]) : n.TypeName;
                        if (!seen.ContainsKey(s)) seen[s] = new Dictionary<string, int>();
                        int c;
                        seen[s].TryGetValue(what, out c);
                        seen[s][what] = c + 1;
                    }
                }
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("{0} features of class {1:X8}\n", features, kind);
            foreach (KeyValuePair<int, Dictionary<string, int>> kv in seen)
            {
                List<string> parts = new List<string>();
                foreach (KeyValuePair<string, int> w in kv.Value) parts.Add(w.Key + " x" + w.Value);
                parts.Sort();
                sb.AppendFormat("  [{0,2}] {1}\n", kv.Key, string.Join(", ", parts.ToArray()));
            }
            return sb.ToString();
        }

        // Booleans and enums by value (no parameter lookup, which is slow across a whole folder).
        static string Shape(NodeTable t, int index)
        {
            Node n = t.At(index);
            byte[] d = n.Data;
            int h = 0x22 + Sketches.Shift(d);
            if (n.Type.Key == Boolean) return "Boolean " + (d[d.Length - 1] != 0 ? "true" : "false");
            if (d.Length >= h + 4) return n.TypeName + "=" + BitConverter.ToUInt16(d, h + 2);
            return n.TypeName;
        }

        // Feature classes across a folder, with example names, to tie each class GUID to a feature type.
        public static string Census(string dir)
        {
            Dictionary<uint, int> count = new Dictionary<uint, int>();
            Dictionary<uint, SortedSet<string>> names = new Dictionary<uint, SortedSet<string>>();
            Dictionary<uint, SortedSet<int>> slots = new Dictionary<uint, SortedSet<int>>();
            List<string> files = new List<string>(System.IO.Directory.GetFiles(dir, "*.ipt", System.IO.SearchOption.AllDirectories));
            foreach (string file in files)
            {
                NodeTable t;
                try
                {
                    Segment dc = null;
                    foreach (Segment s in Segment.ReadAll(file)) if (s.Name == "PmDCSegment") dc = s;
                    if (dc == null) continue;
                    t = NodeTable.Parse(dc.Meta, dc.Data);
                }
                catch (Exception) { continue; }
                foreach (Info i in List(t))
                {
                    int c;
                    count.TryGetValue(i.Kind, out c);
                    count[i.Kind] = c + 1;
                    if (!names.ContainsKey(i.Kind)) { names[i.Kind] = new SortedSet<string>(); slots[i.Kind] = new SortedSet<int>(); }
                    string stem = System.Text.RegularExpressions.Regex.Replace(i.Name ?? "?", @"\d+$", "");
                    if (names[i.Kind].Count < 6) names[i.Kind].Add(stem);
                    slots[i.Kind].Add(Properties(i.Node).Count);
                }
            }
            StringBuilder sb = new StringBuilder();
            List<uint> keys = new List<uint>(count.Keys);
            keys.Sort((a, b) => count[b].CompareTo(count[a]));
            foreach (uint k in keys)
                sb.AppendFormat("  {0:X8} {1,5} features  slots {2,-10} names {3}\n", k, count[k],
                    string.Join("/", new List<int>(slots[k]).ConvertAll(x => x.ToString()).ToArray()), string.Join(", ", names[k]));
            return sb.ToString();
        }
    }
}
