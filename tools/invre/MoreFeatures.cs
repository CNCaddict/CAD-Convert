// Thread features and shells: decoded from their property slots, like the features in Features.cs.
using System;
using System.Collections.Generic;
using System.Text;

namespace InvRe
{
    public static class MoreFeatures
    {
        public const uint ThreadFeature = 0x28AA1906, Shell = 0xB83C9612;

        static int Slot(List<int> s, int i) { return i < s.Count ? s[i] : -1; }

        static bool Flag(NodeTable t, int index)
        {
            Node n = t.At(index);
            return n != null && n.Data != null && n.Type != null && n.Type.Key == Features.Boolean && n.Data[n.Data.Length - 1] != 0;
        }

        // Enum-like nodes: u16 count, u16 value after the header.
        static int EnumValue(NodeTable t, int index)
        {
            Node n = t.At(index);
            if (n == null || n.Data == null) return -1;
            int h = 0x22 + Sketches.Shift(n.Data);
            return n.Data.Length >= h + 4 ? BitConverter.ToUInt16(n.Data, h + 2) : -1;
        }

        static int ListCount(NodeTable t, int index)
        {
            Node n = t.At(index);
            if (n == null || n.Data == null) return -1;
            int p = 0x22 + Sketches.Shift(n.Data);
            if (p + 8 > n.Data.Length || BitConverter.ToUInt32(n.Data, p) != 0x30000002) return -1;
            List<int> items;
            Sketches.ReadList(n.Data, p, out items);
            return items.Count;
        }

        static string B(bool b) { return b ? "true" : "false"; }

        // Thread feature (28AA1906) slots:
        //    0 FaceCollection: the face       1 Boolean: full depth    2 Boolean: true in all (display in model?)
        //    3 depth                          4 offset                 5 EdgeCollection: the start edge
        //    6 Boolean: reversed              8 FeatureDimensions      9 ObjectCollection      10 4C478499
        // The thread itself (type, size, class, hand, internal) is a thread node it owns, as for holes.
        public class ThreadInfo : Features.FeatureInfo
        {
            public Features.Thread Thread;
            public bool FullDepth, Reversed;
            public string Depth, Offset;
            public int Face = -1, Edge = -1;

            public override string Get(string key)
            {
                switch (key)
                {
                    case "threadType": return Thread == null ? null : Thread.Type;
                    case "thread": return Thread == null ? null : Thread.Designation;
                    case "threadClass": return Thread == null ? null : Thread.Class;
                    case "internal": return Thread == null ? null : B(Thread.Internal);
                    case "rightHanded": return Thread == null ? null : B(Thread.RightHanded);
                    case "fullDepth": return B(FullDepth);
                    case "directionReversed": return B(Reversed);
                    case "depth": return FullDepth ? null : Depth;
                    case "offset": return FullDepth ? null : Offset;
                    default: return null;
                }
            }
        }

        public static List<ThreadInfo> Threads(NodeTable t)
        {
            Dictionary<int, string> formulas = new Dictionary<int, string>();
            foreach (Params.Decoded p in Params.Decode(t)) formulas[p.Index] = p.Formula;
            Dictionary<int, Features.Thread> threads = Features.Threads(t);
            List<ThreadInfo> list = new List<ThreadInfo>();
            foreach (Features.Info fi in Features.List(t))
            {
                if (fi.Kind != ThreadFeature) continue;
                List<int> s = Features.Properties(fi.Node);
                ThreadInfo th = new ThreadInfo { Index = fi.Node.Index, Name = fi.Name, FullDepth = Flag(t, Slot(s, 1)), Reversed = Flag(t, Slot(s, 6)) };
                threads.TryGetValue(fi.Node.Index, out th.Thread);
                formulas.TryGetValue(Slot(s, 3), out th.Depth);
                formulas.TryGetValue(Slot(s, 4), out th.Offset);
                List<int> faces = Items(t, Slot(s, 0)), edges = Items(t, Slot(s, 5));
                if (faces.Count > 0) th.Face = faces[0];
                if (edges.Count > 0) th.Edge = edges[0];
                list.Add(th);
            }
            return list;
        }

        static List<int> Items(NodeTable t, int index)
        {
            List<int> items = new List<int>();
            Node n = t.At(index);
            if (n == null || n.Data == null) return items;
            int p = 0x22 + Sketches.Shift(n.Data);
            if (p + 8 <= n.Data.Length && BitConverter.ToUInt32(n.Data, p) == 0x30000002) Sketches.ReadList(n.Data, p, out items);
            return items;
        }

        public static string Describe(ThreadInfo th)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(th.Thread == null ? "thread (no thread node)" : (th.Thread.Internal ? "internal " : "external ") + th.Thread.Designation + " " + th.Thread.Class
                + " (" + th.Thread.Type + ", " + (th.Thread.RightHanded ? "right hand" : "left hand") + ")");
            sb.Append(th.FullDepth ? ", full length" : ", " + th.Depth + " long from " + th.Offset);
            if (th.Reversed) sb.Append(", reversed");
            return sb.ToString();
        }

        // Shell (B83C9612) slots:
        //    0 B3A169E4: direction (0 inside, 1 outside, 2 both sides)   1 FaceCollection: the faces removed
        //    2 EFF2257A: kind-2 list of face sets (BF8B8868: +0x22 FaceCollection, +0x26 thickness)
        //    3 thickness   5 FeatureDimensions   6 a parameter, 10 ul in all (meaning unknown)
        //    7 A3B0404C, 8 637B1CC1, 10 74768907: enums, the same in every test (method and approximation, guess)
        static readonly string[] Directions = { "kInsideShellDirection", "kOutsideShellDirection", "kBothSidesShellDirection" };

        public class ShellInfo : Features.FeatureInfo
        {
            public string Direction, Thickness, ThicknessParam;
            public double ThicknessCm = double.NaN;
            public List<int> Removed = new List<int>();
            public List<KeyValuePair<string, int>> Sets = new List<KeyValuePair<string, int>>();   // thickness, face count
            // Each face set: its thickness (cm, NaN if unknown), the parameter, and the faces.
            public List<double> SetCm = new List<double>();
            public List<string> SetParams = new List<string>();
            public List<List<int>> SetFaces = new List<List<int>>();

            public override string Get(string key)
            {
                switch (key)
                {
                    case "direction": return Direction;
                    case "thickness": return Thickness;
                    case "removedFaces": return Removed.Count.ToString();
                    case "faceSets": return Sets.Count.ToString();
                    case "faceSet1": return Sets.Count == 0 ? null : Sets[0].Key + " on " + Sets[0].Value;
                    default: return null;
                }
            }
        }

        public static List<ShellInfo> Shells(NodeTable t)
        {
            Dictionary<int, string> formulas = new Dictionary<int, string>(), paramNames = new Dictionary<int, string>();
            Dictionary<int, double> values = new Dictionary<int, double>();
            foreach (Params.Decoded p in Params.Decode(t)) { formulas[p.Index] = p.Formula; values[p.Index] = p.Value; paramNames[p.Index] = p.Name; }
            List<ShellInfo> list = new List<ShellInfo>();
            foreach (Features.Info fi in Features.List(t))
            {
                if (fi.Kind != Shell) continue;
                List<int> s = Features.Properties(fi.Node);
                ShellInfo sh = new ShellInfo { Index = fi.Node.Index, Name = fi.Name };
                int dir = EnumValue(t, Slot(s, 0));
                sh.Direction = dir >= 0 && dir < Directions.Length ? Directions[dir] : "direction " + dir;
                formulas.TryGetValue(Slot(s, 3), out sh.Thickness);
                paramNames.TryGetValue(Slot(s, 3), out sh.ThicknessParam);
                if (!values.TryGetValue(Slot(s, 3), out sh.ThicknessCm)) sh.ThicknessCm = double.NaN;
                sh.Removed = Items(t, Slot(s, 1));
                foreach (int set in Items(t, Slot(s, 2)))
                {
                    Node n = t.At(set);
                    if (n == null || n.Data == null) continue;
                    int h = 0x22 + Sketches.Shift(n.Data);
                    string thick, param;
                    double cm;
                    formulas.TryGetValue(Sketches.XRef(n.Data, h + 4), out thick);
                    paramNames.TryGetValue(Sketches.XRef(n.Data, h + 4), out param);
                    if (!values.TryGetValue(Sketches.XRef(n.Data, h + 4), out cm)) cm = double.NaN;
                    List<int> faces = Items(t, Sketches.XRef(n.Data, h));
                    sh.Sets.Add(new KeyValuePair<string, int>(thick, faces.Count));
                    sh.SetCm.Add(cm);
                    sh.SetParams.Add(param);
                    sh.SetFaces.Add(faces);
                }
                list.Add(sh);
            }
            return list;
        }

        // Sweep (90BADA05) slots:
        //    0 profile          1 path (FC203F47, older files A477243B)   2 operation enum     3 taper
        //    4 extent enum (3)  7 FeatureDimensions   8 A29C84B7: type (0 path, 1 path and guide rail)
        //    9 31F02EED: orientation (0 normal to path, 1 parallel to the profile)   10 5CB011E2: scaling (0 XY)
        //   11 guide rail (a path)   15 ObjectCollection
        // The twist is a parameter nothing cross-refs: the node right after the taper (found by position).
        // A path holds a link (CC0F7521) to its curves' ASM body, as a profile's paths do. Its sketch: a 603428AE node
        // cross-refs the path and a 1E3A132C, which cross-refs a 90874D13, which cross-refs the Sketch2D.
        public const uint Sweep = 0x90BADA05, Loft = 0x2F54EFE0;
        static readonly string[] SweepTypes = { "kPathSweepType", "kPathAndGuidRailSweepType", "kPathAndGuidSurfaceSweepType", "kPathAndSectionTwistSweepType" };
        static readonly string[] Orientations = { "kNormalToPath", "kParallelToOriginalProfile", "kAlignToVector" };
        static readonly string[] Scalings = { "kXYProfileScaling", "kXProfileScaling", "kNoProfileScaling" };

        static string Named(string[] names, int v) { return v >= 0 && v < names.Length ? names[v] : "value " + v; }

        public class SweepInfo : Features.FeatureInfo
        {
            public string Operation, SweepType, Orientation, Scaling, Taper, Twist, Sketch, Profile, Path, GuideRail;
            // For the writers: the profile as for an extrude, and the path's and guide rail's sketch and curves.
            public int ProfileNode = -1, SketchNode = -1, PathSketch = -1, RailSketch = -1;
            public List<int> PathCurves, RailCurves;
            public string TaperParam, TwistParam;

            public override string Get(string key)
            {
                switch (key)
                {
                    case "operation": return Operation;
                    case "sweepType": return SweepType;
                    case "orientation": return Orientation;
                    case "scaling": return Scaling;
                    case "taper": return Taper;
                    case "twist": return Twist;
                    case "sketch": return Sketch;
                    case "profile": return Profile;
                    case "path": return Path;
                    case "guideRail": return GuideRail;
                    default: return null;
                }
            }
        }

        // A path's curves. A 603428AE node (older files: 424EB7D7) cross-refs the path and a 1E3A132C (A3277869), which lists one 90874D13 per curve:
        // {u32 start point item, 1, 0, u32 end point item, 1, u8, xref Sketch2D}. (The wire's own edge names just count
        // the path's curves from 1.) Each curve is the sketch's line or arc between those two points.
        static string PathText(NodeTable t, int path, Dictionary<int, string> names, Dictionary<string, int> items)
        {
            int sketch;
            List<int> curves;
            return PathText(t, path, names, items, out sketch, out curves);
        }

        // The same, and the path's sketch and curves (sketch -1 where they can't be read).
        static string PathText(NodeTable t, int path, Dictionary<int, string> names, Dictionary<string, int> items, out int pathSketch, out List<int> curves)
        {
            pathSketch = -1;
            curves = null;
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || (n.Type.Key != 0x603428AE && n.Type.Key != 0x424EB7D7) || n.Data == null) continue;
                bool mine = false;
                Node list = null;
                for (int p = 0x16; p + 4 <= n.Data.Length; p++)
                {
                    int x = Sketches.XRef(n.Data, p);
                    if (x == path) mine = true;
                    Node r = t.At(x);
                    if (r != null && r.Type != null && (r.Type.Key == 0x1E3A132C || r.Type.Key == 0xA3277869) && list == null) list = r;
                }
                if (!mine || list == null) continue;
                Profiles.Loop loop = new Profiles.Loop();
                int sketch = -1;
                // The entries' place in the list node differs between file versions, so it is scanned for them.
                for (int p = 0x0A; p + 4 <= list.Data.Length; p++)
                {
                    Node id = t.At(Sketches.XRef(list.Data, p));
                    if (id == null || id.Data == null || id.Type == null || id.Type.Key != 0x90874D13) continue;
                    p += 3;
                    // Read from the end: older nodes are 4 bytes shorter, without an owner to tell by.
                    int end = id.Data.Length;
                    if (end < 25) return null;
                    int a = (int)BitConverter.ToUInt32(id.Data, end - 25), b = (int)BitConverter.ToUInt32(id.Data, end - 13);
                    sketch = Sketches.XRef(id.Data, end - 4);
                    int curve = CurveBetween(t, sketch, a, b, items);
                    if (curve < 0) return null;
                    loop.Entities.Add(curve);
                }
                if (sketch < 0) return null;
                pathSketch = sketch;
                curves = new List<int>(loop.Entities);
                string sn;
                if (!names.TryGetValue(sketch, out sn)) sn = "sketch #" + sketch.ToString("X");
                return sn + ": " + Profiles.Text(t, loop);
            }
            return null;
        }

        // The sketch's line or arc whose two end points have these items; -1 if none.
        static int CurveBetween(NodeTable t, int sketch, int a, int b, Dictionary<string, int> items)
        {
            int pa, pb;
            if (!items.TryGetValue(sketch + " " + a, out pa) || !items.TryGetValue(sketch + " " + b, out pb)) return -1;
            string prefix = sketch + " ";
            foreach (KeyValuePair<string, int> kv in items)
            {
                if (!kv.Key.StartsWith(prefix)) continue;
                Node n = t.At(kv.Value);
                if (n == null || n.Type == null || (n.Type.Key != Sketches.Line2D && n.Type.Key != Sketches.Circle2D)) continue;
                List<int> pts = Sketches.CurvePoints(t, kv.Value);
                if (pts.Contains(pa) && pts.Contains(pb)) return kv.Value;
            }
            // A spline: its fit points are tied to it by SplineFitPoint2D nodes (each cross-refs the spline and a point).
            Dictionary<int, HashSet<int>> fits = new Dictionary<int, HashSet<int>>();
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Data == null || n.TypeName != "SplineFitPoint2D") continue;
                int spline = -1, point = -1;
                for (int p = 0x16; p + 4 <= n.Data.Length; p++)
                {
                    Node r = t.At(Sketches.XRef(n.Data, p));
                    if (r == null || r.Type == null) continue;
                    if (r.TypeName == "BSplineCurve2D") spline = r.Index;
                    else if (r.Type.Key == Sketches.Point2D) point = r.Index;
                }
                if (spline < 0 || point < 0) continue;
                if (!fits.ContainsKey(spline)) fits[spline] = new HashSet<int>();
                fits[spline].Add(point);
                if (fits[spline].Contains(pa) && fits[spline].Contains(pb)) return spline;
            }
            return -1;
        }

        public static List<SweepInfo> Sweeps(NodeTable t)
        {
            Dictionary<int, string> formulas = new Dictionary<int, string>();
            Dictionary<int, Params.Decoded> ps = new Dictionary<int, Params.Decoded>();
            foreach (Params.Decoded p in Params.Decode(t)) { formulas[p.Index] = p.Formula; ps[p.Index] = p; }
            Dictionary<int, string> names = Sketches.BrowserNames(t);
            Dictionary<string, int> items = null;
            List<SweepInfo> list = new List<SweepInfo>();
            foreach (Features.Info fi in Features.List(t))
            {
                if (fi.Kind != Sweep) continue;
                if (items == null) items = Naming.SketchItems(t);
                List<int> s = Features.Properties(fi.Node);
                SweepInfo sw = new SweepInfo { Index = fi.Node.Index, Name = fi.Name };
                sw.Operation = Named(Features.Operations, Features.EnumValue(t, Slot(s, 2)));
                sw.SweepType = Named(SweepTypes, EnumValue(t, Slot(s, 8)));
                sw.Orientation = Named(Orientations, EnumValue(t, Slot(s, 9)));
                sw.Scaling = Named(Scalings, EnumValue(t, Slot(s, 10)));
                formulas.TryGetValue(Slot(s, 3), out sw.Taper);
                formulas.TryGetValue(Slot(s, 3) + 1, out sw.Twist);
                Params.Decoded pd;
                if (ps.TryGetValue(Slot(s, 3), out pd)) sw.TaperParam = pd.Name;
                if (ps.TryGetValue(Slot(s, 3) + 1, out pd)) sw.TwistParam = pd.Name;
                int sketch = Profiles.SketchOf(t, Slot(s, 0));
                if (sketch >= 0)
                {
                    names.TryGetValue(sketch, out sw.Sketch);
                    sw.Profile = Profiles.Describe(t, Slot(s, 0), sketch, items);
                    sw.ProfileNode = Slot(s, 0);
                    sw.SketchNode = sketch;
                }
                sw.Path = PathText(t, Slot(s, 1), names, items, out sw.PathSketch, out sw.PathCurves);
                if (Slot(s, 11) >= 0) sw.GuideRail = PathText(t, Slot(s, 11), names, items, out sw.RailSketch, out sw.RailCurves);
                list.Add(sw);
            }
            return list;
        }

        public static string Describe(SweepInfo sw)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("{0} {1} along {2}", sw.Operation == null ? "?" : sw.Operation.Substring(1).Replace("Operation", "").ToLowerInvariant(), sw.Sketch ?? "(sketch?)", sw.Path ?? "(path?)");
            if (sw.GuideRail != null) sb.Append(", guide rail " + sw.GuideRail);
            if (sw.Orientation == "kParallelToOriginalProfile") sb.Append(", parallel to the profile");
            if (sw.Taper != null && Params.Normalize(sw.Taper) != Params.Normalize("0 deg")) sb.Append(", taper " + sw.Taper);
            if (sw.Twist != null && Params.Normalize(sw.Twist) != Params.Normalize("0 deg")) sb.Append(", twist " + sw.Twist);
            return sb.ToString();
        }

        // Loft (2F54EFE0) slots:
        //    0 312F9E50: kind-2 list of sections (4B3150E8)   1 operation enum   2 Boolean: closed
        //    7 312F9E50: rails, or the centerline (sections too)   9 Boolean: merge tangent faces (see below)
        //   10 E0E3E202: 1 centerline, 0 otherwise   13 ObjectCollection
        // Section: +0x22 link (CC0F7521) to its own copy of the loop, +0x27 3C6C1C6C (0 so far), +0x2B weight, +0x2F angle.
        // A 4FD0DC2A node (55279EE0 for a sketch point) cross-refs the section (+0x1E) and what was picked (+0x22): a
        // Profile or a Point2D.
        // The API's conditions aren't stored: a non-zero weight is "direction", a point section "sharp point", else free.
        public class LoftInfo : Features.FeatureInfo
        {
            public string Operation, LoftType, FirstCondition, LastCondition, FirstAngle, FirstImpact, LastAngle, LastImpact, Sections, Rail1, Centerline;
            public bool Closed, Merge;
            public int Rails = -1;
            // The same, for the writers: each section, rail and the centerline as what was picked.
            public List<Section> SectionList = new List<Section>(), RailList = new List<Section>();
            public Section CenterlineSection;

            public override string Get(string key)
            {
                switch (key)
                {
                    case "operation": return Operation;
                    case "loftType": return LoftType;
                    case "closed": return B(Closed);
                    case "mergeTangentFaces": return B(Merge);
                    case "firstCondition": return FirstCondition;
                    case "lastCondition": return LastCondition;
                    case "firstAngle": return FirstAngle;
                    case "firstImpact": return FirstImpact;
                    case "lastAngle": return LastAngle;
                    case "lastImpact": return LastImpact;
                    case "sections": return Sections;
                    case "rails": return Rails < 0 ? null : Rails.ToString();
                    case "rail1": return Rail1;
                    case "centerline": return Centerline;
                    default: return null;
                }
            }
        }

        // Profile: the Profile node picked, Sketch its Sketch2D; PointNode: the Point2D for a point section; else -1.
        public class Section { public string Text; public bool Point; public string Angle, Impact; public double ImpactValue; public int Mode; public int Profile = -1, Sketch = -1, PointNode = -1; }

        static Section ReadSection(NodeTable t, int index, Dictionary<int, int> picked, Dictionary<int, string> names, Dictionary<string, int> items, Dictionary<int, Params.Decoded> ps)
        {
            Section sec = new Section();
            Node n = t.At(index);
            if (n == null || n.Data == null) return sec;
            int h = 0x22 + Sketches.Shift(n.Data);
            Node modeNode = t.At(Sketches.XRef(n.Data, h + 5));
            if (modeNode != null && modeNode.Type != null && modeNode.Type.Key == 0x3C6C1C6C) sec.Mode = EnumValue(t, modeNode.Index);
            Params.Decoded p;
            if (ps.TryGetValue(Sketches.XRef(n.Data, h + 9), out p)) { sec.Impact = p.Formula; sec.ImpactValue = p.Value; }
            if (ps.TryGetValue(Sketches.XRef(n.Data, h + 13), out p)) sec.Angle = p.Formula;
            int target;
            if (!picked.TryGetValue(index, out target)) return sec;
            Node tn = t.At(target);
            if (tn == null || tn.Type == null) return sec;
            // A sketch point is picked through a Point3D copy: a 0830C1B0 node ties it (+0x1E) to the Point2D (+0x24).
            if (tn.TypeName == "Point3D")
                foreach (Node link in t.Nodes)
                    if (link.Type != null && link.Type.Key == 0x0830C1B0 && link.Data != null && Sketches.XRef(link.Data, 0x1E + Sketches.Shift(link.Data)) == target)
                    {
                        target = Sketches.XRef(link.Data, 0x24 + Sketches.Shift(link.Data));
                        tn = t.At(target);
                        if (tn == null || tn.Type == null) return sec;
                        break;
                    }
            string sn;
            if (tn.Type.Key == Features.Profile)
            {
                int sketch = Profiles.SketchOf(t, target);
                sec.Profile = target;
                sec.Sketch = sketch;
                if (!names.TryGetValue(sketch, out sn)) sn = "sketch #" + sketch.ToString("X");
                string d = Profiles.Describe(t, target, sketch, items);
                sec.Text = d == null ? null : sn + ": " + d;
            }
            else if (tn.Type.Key == Features.Point2D)
            {
                sec.Point = true;
                int sketch = Sketches.XRef(tn.Data, 0x26 + Sketches.Shift(tn.Data));
                sec.PointNode = target;
                sec.Sketch = sketch;
                if (!names.TryGetValue(sketch, out sn)) sn = "sketch #" + sketch.ToString("X");
                double[] g = Sketches.Geometry2D(t, target);
                sec.Text = sn + ": point (" + Units.Num(Math.Round(10 * g[0], 6) + 0.0) + ", " + Units.Num(Math.Round(10 * g[1], 6) + 0.0) + ")";
            }
            return sec;
        }

        public static List<LoftInfo> Lofts(NodeTable t)
        {
            Dictionary<int, Params.Decoded> ps = new Dictionary<int, Params.Decoded>();
            foreach (Params.Decoded p in Params.Decode(t)) ps[p.Index] = p;
            Dictionary<int, string> names = Sketches.BrowserNames(t);
            Dictionary<int, int> picked = new Dictionary<int, int>();
            foreach (Node n in t.Nodes)
                if (n.Type != null && (n.Type.Key == 0x4FD0DC2A || n.Type.Key == 0x55279EE0) && n.Data != null && n.Data.Length >= 0x26 + Sketches.Shift(n.Data))
                {
                    int s0 = Sketches.Shift(n.Data);
                    picked[Sketches.XRef(n.Data, 0x1E + s0)] = Sketches.XRef(n.Data, 0x22 + s0);
                }
            Dictionary<string, int> items = null;
            List<LoftInfo> list = new List<LoftInfo>();
            foreach (Features.Info fi in Features.List(t))
            {
                if (fi.Kind != Loft) continue;
                if (items == null) items = Naming.SketchItems(t);
                List<int> s = Features.Properties(fi.Node);
                LoftInfo l = new LoftInfo { Index = fi.Node.Index, Name = fi.Name };
                l.Operation = Named(Features.Operations, Features.EnumValue(t, Slot(s, 1)));
                l.Closed = Flag(t, Slot(s, 2));
                bool center = EnumValue(t, Slot(s, 10)) == 1;
                // Centerline lofts always merge: Inventor refuses to turn it off, and reports true even where an older
                // file's flag is false.
                l.Merge = Flag(t, Slot(s, 9)) || center;
                List<int> others = Items(t, Slot(s, 7));
                l.LoftType = center ? "kLoftWithCenterline" : others.Count > 0 ? "kLoftWithRails" : "kRegularLoft";
                List<Section> sections = Items(t, Slot(s, 0)).ConvertAll(i => ReadSection(t, i, picked, names, items, ps));
                l.Sections = string.Join(" | ", sections.ConvertAll(x => x.Text ?? "?").ToArray());
                l.SectionList = sections;
                if (sections.Count > 0)
                {
                    // +0x27 3C6C1C6C: 0 free, direction or sharp point; 1 tangent to a plane (a point section, tied to the plane by its 55279EE0).
                    Func<Section, string> cond = x => x.Mode == 1 ? "kTangentToPlaneLoftCondition" : x.Mode != 0 ? "condition " + x.Mode : x.Point ? "kSharpPointLoftCondition" : x.ImpactValue != 0 ? "kDirectionLoftCondition" : "kFreeLoftCondition";
                    Section a = sections[0], b = sections[sections.Count - 1];
                    l.FirstCondition = cond(a);
                    l.LastCondition = cond(b);
                    if (l.FirstCondition == "kDirectionLoftCondition" || a.Mode != 0) { l.FirstAngle = a.Angle; l.FirstImpact = a.Impact; }
                    if (l.LastCondition == "kDirectionLoftCondition" || b.Mode != 0) { l.LastAngle = b.Angle; l.LastImpact = b.Impact; }
                }
                if (center) { if (others.Count > 0) { l.CenterlineSection = ReadSection(t, others[0], picked, names, items, ps); l.Centerline = l.CenterlineSection.Text; } }
                else
                {
                    l.Rails = others.Count;
                    l.RailList = others.ConvertAll(i => ReadSection(t, i, picked, names, items, ps));
                    if (others.Count > 0) l.Rail1 = l.RailList[0].Text;
                }
                list.Add(l);
            }
            return list;
        }

        public static string Describe(LoftInfo l)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("{0} loft through {1}", l.Operation == null ? "?" : l.Operation.Substring(1).Replace("Operation", "").ToLowerInvariant(), l.Sections);
            if (l.Closed) sb.Append(", closed");
            if (l.Centerline != null) sb.Append(", centerline " + l.Centerline);
            if (l.Rails > 0) sb.Append(", " + l.Rails + " rail" + (l.Rails == 1 ? "" : "s") + ": " + l.Rail1);
            if (l.FirstAngle != null) sb.Append(", start " + l.FirstAngle + " weight " + l.FirstImpact);
            if (l.LastAngle != null) sb.Append(", end " + l.LastAngle + " weight " + l.LastImpact);
            return sb.ToString();
        }

        // Sheet metal.
        // Plate (55585FC6; the API's "Face" feature) slots:
        //    0 FC203F47: a path that a 4FD0DC2A node ties to the Profile   1 DirectionAxis (the sketch normal)
        //    2 5E50B969: direction (0 positive, 1 negative)   3 thickness (normally = Thickness)
        //    4 A96B5992: bend options   7 EdgeCollection: edges bent to the plate before it (none on the first)
        //    9-13 corner and bend nodes, when a plate joins others   16 operation enum
        // Bend (4CF758F2) slots: 0 EdgeCollection   1 radius (normally = BendRadius)   2 thickness   3 A96B5992
        //    4 96058864   7 265034E9   8 90B64134   10 Boolean (true in all). A plate that joins one before it makes
        //    a bend of its own, sharing its edge collection.
        // Corner (6880587F) slots: 0 8677CE83 (settings)   3 299B2DCE   4 the plate's path. Inventor adds these where
        //    two bends meet, and the API doesn't list them. Settings: +0x22 Boolean, +0x26, +0x2A parameters (gap, then relief size or
        //    similar: not told apart), +0x2E 2B3E0C72, +0x32 AFD8A8E0 corner type, +0x36 2B3E0C72, +0x3A parameter, +0x42 Boolean.
        // Corner feature the API does list (9F98DF82) slots: 0 AFD8A8E0 corner type (3 no overlap, 6 overlap)   1 Boolean: the overlap reversed
        //    2 thickness   3 gap   4 parameter   5 EdgeCollection   7, 8, 10, 14, 15 Boolean   12 relief size   13 2B3E0C72 relief shape   18 parameter.
        public const uint Plate = 0x55585FC6, Bend = 0x4CF758F2, Corner = 0x6880587F, ListedCorner = 0x9F98DF82, Imported = 0x0CA9112D, Composite = 0xC2C74308, ImportedSurface = 0xE34D8F79;
        static string CornerType(int value, bool reversed)
        {
            return value == 3 ? "kCornerNoOverlap" : value == 6 ? (reversed ? "kCornerReverseOverlap" : "kCornerOverlap") : "corner type " + value;
        }
        static readonly string[] PlateDirections = { "kPositiveExtentDirection", "kNegativeExtentDirection", "kSymmetricExtentDirection" };

        // A Plate with no profile is a flange (the API's Flange feature): a 6BF0A0AA node cross-refs its path at +0x7B and
        // holds +0x1E height, +0x22 angle, +0x26 thickness, +0x32 the EdgeItem flanged, +0x36 a Plane, +0x4E..+0x5A the
        // flange's four corner points in a sketch of its own, +0x62 bend radius, +0x87 5E50B969 (direction).
        public class SheetInfo : Features.FeatureInfo
        {
            public string Kind;   // plate, flange, bend, corner
            public string Operation, Direction, Sketch, Profile, Thickness, Radius, Height, Angle, CornerType, Gap;
            public int Edges = -1;
            public int SketchNode = -1, ProfileNode = -1;   // a plate's
            public string ThicknessParam;
            public double ThicknessCm = double.NaN, HeightCm = double.NaN, AngleRad = double.NaN, RadiusCm = double.NaN, GapCm = double.NaN;
            public string Alias;  // the API's name for a flange (Flange1, ...): numbered apart from the plates

            public override string Get(string key)
            {
                switch (key)
                {
                    case "operation": return Operation;
                    case "direction": return Direction;
                    case "sketch": return Sketch;
                    case "profile": return Profile;
                    case "bendRadius": return Radius;
                    case "height": return Height;
                    case "flangeAngle": return Angle;
                    case "cornerType": return CornerType;
                    case "gapWidth": return Gap;
                    default: return null;
                }
            }
        }

        public static List<SheetInfo> Sheets(NodeTable t)
        {
            Dictionary<int, string> formulas = new Dictionary<int, string>();
            Dictionary<int, double> values = new Dictionary<int, double>();
            Dictionary<int, string> paramNames = new Dictionary<int, string>();
            foreach (Params.Decoded p in Params.Decode(t)) { formulas[p.Index] = p.Formula; values[p.Index] = p.Value; paramNames[p.Index] = p.Name; }
            Dictionary<int, string> names = Sketches.BrowserNames(t);
            Dictionary<int, int> picked = new Dictionary<int, int>();
            foreach (Node n in t.Nodes)
                if (n.Type != null && n.Type.Key == 0x4FD0DC2A && n.Data != null && n.Data.Length >= 0x26 + Sketches.Shift(n.Data))
                {
                    int s0 = Sketches.Shift(n.Data);
                    picked[Sketches.XRef(n.Data, 0x1E + s0)] = Sketches.XRef(n.Data, 0x22 + s0);
                }
            Dictionary<int, Node> flanges = new Dictionary<int, Node>();
            foreach (Node n in t.Nodes)
                if (n.Type != null && n.Type.Key == 0x6BF0A0AA && n.Data != null && n.Data.Length >= 0x8B + Sketches.Shift(n.Data))
                    flanges[Sketches.XRef(n.Data, 0x7B + Sketches.Shift(n.Data))] = n;
            int flangeCount = 0;
            Dictionary<string, int> items = null;
            List<SheetInfo> list = new List<SheetInfo>();
            foreach (Features.Info fi in Features.List(t))
            {
                if (fi.Kind != Plate && fi.Kind != Bend && fi.Kind != Corner && fi.Kind != ListedCorner) continue;
                if (items == null) items = Naming.SketchItems(t);
                List<int> s = Features.Properties(fi.Node);
                SheetInfo x = new SheetInfo { Index = fi.Node.Index, Name = fi.Name };
                if (fi.Kind == Plate)
                {
                    x.Kind = "plate";
                    x.Operation = Named(Features.Operations, Features.EnumValue(t, Slot(s, 16)));
                    x.Direction = Named(PlateDirections, EnumValue(t, Slot(s, 2)));
                    formulas.TryGetValue(Slot(s, 3), out x.Thickness);
                    paramNames.TryGetValue(Slot(s, 3), out x.ThicknessParam);
                    if (!values.TryGetValue(Slot(s, 3), out x.ThicknessCm)) x.ThicknessCm = double.NaN;
                    x.Edges = Items(t, Slot(s, 7)).Count;
                    Node fl;
                    int profile;
                    if (flanges.TryGetValue(Slot(s, 0), out fl))
                    {
                        int h = Sketches.Shift(fl.Data);
                        x.Kind = "flange";
                        x.Alias = "Flange" + (++flangeCount);
                        formulas.TryGetValue(Sketches.XRef(fl.Data, 0x1E + h), out x.Height);
                        formulas.TryGetValue(Sketches.XRef(fl.Data, 0x22 + h), out x.Angle);
                        formulas.TryGetValue(Sketches.XRef(fl.Data, 0x62 + h), out x.Radius);
                        x.Direction = Named(PlateDirections, EnumValue(t, Sketches.XRef(fl.Data, 0x87 + h)));
                        // +0x2A: a Boolean set when the flange is flipped to the other side (the API's
                        // kNegativeExtentDirection in SetDistanceHeightExtent); the enum at +0x87 stays positive.
                        if (Flag(t, Sketches.XRef(fl.Data, 0x2A + h)))
                            x.Direction = x.Direction == "kNegativeExtentDirection" ? "kPositiveExtentDirection" : "kNegativeExtentDirection";
                        if (!values.TryGetValue(Sketches.XRef(fl.Data, 0x1E + h), out x.HeightCm)) x.HeightCm = double.NaN;
                        if (!values.TryGetValue(Sketches.XRef(fl.Data, 0x22 + h), out x.AngleRad)) x.AngleRad = double.NaN;
                        if (!values.TryGetValue(Sketches.XRef(fl.Data, 0x62 + h), out x.RadiusCm)) x.RadiusCm = double.NaN;
                    }
                    else if (picked.TryGetValue(Slot(s, 0), out profile))
                    {
                        int sketch = Profiles.SketchOf(t, profile);
                        if (sketch >= 0)
                        {
                            names.TryGetValue(sketch, out x.Sketch);
                            x.Profile = Profiles.Describe(t, profile, sketch, items);
                            x.SketchNode = sketch;
                            x.ProfileNode = profile;
                        }
                    }
                }
                else if (fi.Kind == Bend)
                {
                    x.Kind = "bend";
                    formulas.TryGetValue(Slot(s, 1), out x.Radius);
                    formulas.TryGetValue(Slot(s, 2), out x.Thickness);
                    if (!values.TryGetValue(Slot(s, 1), out x.RadiusCm)) x.RadiusCm = double.NaN;
                    if (!values.TryGetValue(Slot(s, 2), out x.ThicknessCm)) x.ThicknessCm = double.NaN;
                    x.Edges = Items(t, Slot(s, 0)).Count;
                }
                else if (fi.Kind == ListedCorner)
                {
                    x.Kind = "corner feature";
                    x.CornerType = CornerType(EnumValue(t, Slot(s, 0)), Flag(t, Slot(s, 1)));
                    formulas.TryGetValue(Slot(s, 3), out x.Gap);
                    if (!values.TryGetValue(Slot(s, 3), out x.GapCm)) x.GapCm = double.NaN;
                }
                else
                {
                    x.Kind = "corner";
                    Node set = t.At(Slot(s, 0));
                    if (set != null && set.Data != null && set.Type != null && set.Type.Key == 0x8677CE83)
                    {
                        int h = Sketches.Shift(set.Data);
                        x.CornerType = CornerType(EnumValue(t, Sketches.XRef(set.Data, 0x32 + h)), false);
                        formulas.TryGetValue(Sketches.XRef(set.Data, 0x26 + h), out x.Gap);
                    }
                }
                list.Add(x);
            }
            return list;
        }

        public static string Describe(SheetInfo x)
        {
            if (x.Kind == "plate") return "plate " + (x.Sketch ?? "(sketch?)") + ", thickness " + x.Thickness + ", " + (x.Direction ?? "?").Replace("k", "").Replace("ExtentDirection", "").ToLowerInvariant()
                + ", " + (x.Operation ?? "?").Substring(1).Replace("Operation", "").ToLowerInvariant() + (x.Edges > 0 ? ", " + x.Edges + " bend edges" : "");
            if (x.Kind == "flange") return "flange " + x.Height + " at " + x.Angle + ", bend radius " + x.Radius + ", thickness " + x.Thickness + ", " + (x.Direction ?? "?").Replace("k", "").Replace("ExtentDirection", "").ToLowerInvariant();
            if (x.Kind == "bend") return "bend radius " + x.Radius + ", " + x.Edges + " edges";
            return (x.Kind == "corner feature" ? "corner" : "corner (where two bends meet)") + ", " + (x.CornerType ?? "?") + ", gap " + (x.Gap ?? "?");
        }

        // Imported bodies (0CA9112D; the API's non-parametric base features): slots 0 and 1 SurfaceBody, 2 Boolean. No
        // parameters or history: the shape is only in the B-rep. Composite (C2C74308): imported surfaces. E34D8F79: an
        // imported body Inventor holds as a surface (the API's non-parametric base feature with IsSolid false; seen on
        // imported/two-blocks, the second block).
        public class ImportedInfo : Features.FeatureInfo
        {
            public string Kind;
            public override string Get(string key)
            {
                if (key == "isSolid") return B(Kind == "imported body");
                if (key == "isComposite") return B(Kind == "imported surfaces");
                return null;
            }
        }

        public static List<ImportedInfo> ImportedBodies(NodeTable t)
        {
            List<ImportedInfo> list = new List<ImportedInfo>();
            foreach (Features.Info fi in Features.List(t))
                if (fi.Kind == Imported || fi.Kind == Composite || fi.Kind == ImportedSurface)
                    list.Add(new ImportedInfo { Index = fi.Node.Index, Name = fi.Name, Kind = fi.Kind == Imported ? "imported body" : fi.Kind == Composite ? "imported surfaces" : "imported surface" });
            return list;
        }

        public static string Describe(ShellInfo sh)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("shell {0} {1}, {2} face{3} removed", sh.Thickness, sh.Direction.Replace("ShellDirection", "").Substring(1).ToLowerInvariant().Replace("bothsides", "both sides"),
                sh.Removed.Count, sh.Removed.Count == 1 ? "" : "s");
            foreach (KeyValuePair<string, int> set in sh.Sets) sb.AppendFormat("; {0} on {1} face{2}", set.Key, set.Value, set.Value == 1 ? "" : "s");
            return sb.ToString();
        }
    }
}
