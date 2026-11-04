// The converter's neutral model: what a part holds, as JSON, for the target writers (tools/freecad/build_model.py).
// Lengths in mm, angles in degrees, sketch coordinates in the sketch's own plane. Curves and sketches are named by node
// index. "expect" is what Inventor's own B-rep says the result must look like, so a writer can check what it built.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace InvRe
{
    public static class CadModel
    {
        const double Mm = 10;

        static string Q(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 32) sb.AppendFormat("\\u{0:x4}", (int)c);
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        static string N(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture);
        }

        static string Arr(IEnumerable<double> v)
        {
            List<string> s = new List<string>();
            foreach (double x in v) s.Add(N(x));
            return "[" + string.Join(", ", s.ToArray()) + "]";
        }

        static string Ids(IEnumerable<int> v)
        {
            List<string> s = new List<string>();
            foreach (int x in v) s.Add(x.ToString());
            return "[" + string.Join(", ", s.ToArray()) + "]";
        }

        static double[] ToMm(double[] v) { return v == null ? null : Array.ConvertAll(v, x => x * Mm); }

        static string Vec(double[] v) { return v == null ? "null" : Arr(v); }

        // The model at modelPath (â€¦.model.json) and, where the part starts from an imported body, that body as it was
        // imported, from the B-rep's history, beside it (â€¦.made.step): the features after it are rebuilt on it.
        public static void Write(NodeTable t, List<Segment> segments, string file, string modelPath)
        {
            string made = null;
            Segment brep = segments.Find(s => string.Equals(s.Name, "PmBRepSegment", StringComparison.OrdinalIgnoreCase));
            string madePath = MadePath(modelPath);
            if (File.Exists(madePath)) File.Delete(madePath);
            if (brep != null && MoreFeatures.ImportedBodies(t).Exists(x => x.Kind == "imported body"))
                try { made = new StepWriter { AsMade = true }.Write(brep.Data, Path.GetFileNameWithoutExtension(file)); }
                catch (Exception) { made = null; }
            if (made != null) File.WriteAllText(madePath, made, new UTF8Encoding(false));
            File.WriteAllText(modelPath, Json(t, segments, file, made != null), new UTF8Encoding(false));
        }

        public static string MadePath(string modelPath)
        {
            return modelPath.EndsWith(".model.json", StringComparison.OrdinalIgnoreCase) ? modelPath.Substring(0, modelPath.Length - 11) + ".made.step" : modelPath + ".made.step";
        }

        public static string Json(NodeTable t, List<Segment> segments, string file, bool made = false)
        {
            List<string> parameters = new List<string>();
            foreach (Params.Decoded p in Params.Decode(t))
                parameters.Add(string.Format("{{\"name\": {0}, \"unit\": {1}, \"formula\": {2}, \"value\": {3}, \"shown\": {4}}}",
                    Q(p.Name), Q(p.Unit), Q(p.Formula), N(p.Value), Q(p.Shown)));

            List<string> sketches = new List<string>();
            Segment brepForSketches = segments.Find(s => string.Equals(s.Name, "PmBRepSegment", StringComparison.OrdinalIgnoreCase));
            Dictionary<int, Naming.Resolved> sketchFaceNames = null;
            Dictionary<int, string> browser = Sketches.BrowserNames(t);
            foreach (Sketches.SketchData s in Sketches.ReadAll(t))
            {
                // What it was drawn on: an origin plane (by its role), a work plane (where it is) or a face (its surface).
                string on = "null";
                int target = Sketches.SketchOn(t, s.Node);
                Node tn = t.At(target);
                if (tn != null && tn.TypeName == "Plane")
                {
                    string name;
                    browser.TryGetValue(target, out name);
                    Features.Plane wp = Features.WorkPlane(t, target);
                    on = string.Format("{{\"kind\": \"plane\", \"name\": {0}{1}}}", Q(name),
                        wp == null ? "" : ", \"origin\": " + Vec(ToMm(wp.Origin)) + ", \"normal\": " + Vec(wp.Normal));
                }
                else if (tn != null && tn.TypeName == "FaceItem")
                {
                    if (sketchFaceNames == null) sketchFaceNames = brepForSketches == null ? new Dictionary<int, Naming.Resolved>() : Naming.Resolve(t, brepForSketches.Data);
                    on = "{\"kind\": \"face\", \"face\": " + EdgesJson(new List<int> { target }, sketchFaceNames) + "}";
                }
                sketches.Add(SketchJson(s, on));
            }

            return "{\n\"name\": " + Q(Path.GetFileNameWithoutExtension(file.Split('*')[file.Split('*').Length - 1]))
                + ",\n\"file\": " + Q(file)
                + ",\n\"parameters\": [\n  " + string.Join(",\n  ", parameters.ToArray())
                + "\n],\n\"sketches\": [\n  " + string.Join(",\n  ", sketches.ToArray())
                + "\n],\n\"features\": [\n  " + string.Join(",\n  ", FeatureJson(t, segments, made).ToArray())
                + "\n],\n\"expect\": " + ExpectJson(segments)
                + "\n}\n";
        }

        static string SketchJson(Sketches.SketchData s, string on)
        {
            List<string> curves = new List<string>(), rules = new List<string>();
            foreach (Sketches.SketchCurve c in s.Curves)
                curves.Add(string.Format("{{\"id\": {0}, \"kind\": {1}, \"ref\": {2}, \"hidden\": {3}, \"x\": {4}, \"y\": {5}, \"r\": {6}, \"start\": {7}, \"end\": {8}, \"center\": {9}, \"on\": {10}, \"degree\": {11}, \"knots\": {12}, \"poles\": {13}, \"major\": {14}, \"minor\": {15}}}",
                    c.Node, Q(c.Kind), c.Reference ? "true" : "false", c.Hidden ? "true" : "false", N(c.X * Mm), N(c.Y * Mm), N(c.R * Mm), c.Start, c.End, c.Center, Ids(c.On),
                    c.Degree, Vec(c.Knots), Vec(ToMm(c.Poles)), Vec(c.Major), N(c.Minor * Mm)));
            foreach (Sketches.SketchRule r in s.Rules)
                rules.Add(string.Format("{{\"kind\": {0}, \"items\": {1}, \"param\": {2}}}", Q(r.Kind), Ids(r.Items), Q(r.ParamName)));
            double[] m = s.Placement == null ? null : (double[])s.Placement.Clone();
            if (m != null) { m[3] *= Mm; m[7] *= Mm; m[11] *= Mm; }
            return string.Format("{{\"id\": {0}, \"name\": {1}, \"matrix\": {2}, \"on\": {5},\n   \"curves\": [\n    {3}\n   ],\n   \"rules\": [\n    {4}\n   ]}}",
                s.Node, Q(s.Name), Vec(m), string.Join(",\n    ", curves.ToArray()), string.Join(",\n    ", rules.ToArray()), on);
        }

        static string LoopsJson(NodeTable t, int profile, int sketch, Dictionary<string, int> items)
        {
            if (profile < 0 || sketch < 0) return "null";
            List<string> loops = new List<string>();
            foreach (Profiles.Loop l in Profiles.Loops(t, profile, sketch, items))
            {
                if (l.Problem != null || l.Entities.Count != l.Items.Count) return "null";
                loops.Add(Ids(l.Entities));
            }
            return loops.Count == 0 ? "null" : "[" + string.Join(", ", loops.ToArray()) + "]";
        }

        // For each loop, whether it adds material (Profiles.AddsMaterial) rather than being a hole. Usually a loop inside
        // another is a hole, but a profile made without combining regions adds each loop in its own right (the generated
        // profile no-combine part: a circle inside a square that fills rather than cuts).
        static string AddsJson(NodeTable t, int profile, int sketch, Dictionary<string, int> items)
        {
            if (profile < 0 || sketch < 0) return "null";
            List<string> adds = new List<string>();
            foreach (Profiles.Loop l in Profiles.Loops(t, profile, sketch, items))
                adds.Add(Profiles.AddsMaterial(t, l) ? "true" : "false");
            return "[" + string.Join(", ", adds.ToArray()) + "]";
        }

        // Each loop's outline as Inventor trimmed it: for profiles bounded by curves that cross rather than meet.
        static string WiresJson(NodeTable t, int profile, int sketch, Dictionary<string, int> items)
        {
            if (profile < 0 || sketch < 0) return "null";
            List<string> loops = new List<string>();
            foreach (Profiles.Loop l in Profiles.Loops(t, profile, sketch, items))
            {
                if (l.Records == null) return "null";
                List<string> edges = new List<string>();
                foreach (SabRecord e in l.Edges) edges.Add(EdgeJson(l.Records, e));
                loops.Add("[" + string.Join(", ", edges.ToArray()) + "]");
            }
            return "[" + string.Join(", ", loops.ToArray()) + "]";
        }

        // "kCutOperation" -> "cut" and so on: the words the writers use.
        static string Op(string api)
        {
            switch (api)
            {
                case "kNewBodyOperation": return "new";
                case "kJoinOperation": return "join";
                case "kCutOperation": return "cut";
                case "kIntersectOperation": return "intersect";
                case "kSurfaceOperation": return "surface";
                default: return api;
            }
        }

        static string Dir(string api)
        {
            switch (api)
            {
                case "kPositiveExtentDirection": return "positive";
                case "kNegativeExtentDirection": return "negative";
                case "kSymmetricExtentDirection": return "symmetric";
                default: return api;
            }
        }

        static string Extent(string api)
        {
            switch (api)
            {
                case "kDistanceExtent": return "distance";
                case "kThroughAllExtent": return "through";
                case "kToNextExtent": return "next";
                case "kToExtent": return "to";
                case "kFromToExtent": return "fromto";
                case "kAngleExtent": return "angle";
                case "kFullSweepExtent": return "full";
                default: return api;
            }
        }

        // Features and patterns in the order Inventor runs them: as the document's first browser node (#1) lists them,
        // which isn't always their node order (a feature added after rolling back, or moved).
        public const uint EndOfFeatures = 0x8F41FD24;

        // The features the browser lists after its End of Part marker: rolled back, so not in the stored solid. In the
        // real parts tested, every fillet and chamfer listed after it made no faces in the B-rep, and every one before it did
        // (five parts). Empty when not listed.
        public static HashSet<int> RolledBack(NodeTable t)
        {
            HashSet<int> after = new HashSet<int>();
            List<int> listed;
            List<int> order = Timeline(t, out listed);
            int end = listed.FindIndex(x => t.At(x).Type != null && t.At(x).Type.Key == EndOfFeatures);
            // Only what the browser lists after it: sheet metal parts keep the marker first and their plates, which the
            // browser doesn't list (they come by node order), are all in the solid.
            for (int i = end + 1; i < listed.Count; i++) after.Add(listed[i]);
            return after;
        }

        public static List<int> Timeline(NodeTable t)
        {
            List<int> listed;
            return Timeline(t, out listed);
        }

        // listed: the part of the order the browser gives itself.
        public static List<int> Timeline(NodeTable t, out List<int> listed)
        {
            HashSet<int> wanted = new HashSet<int>();
            foreach (Node n in t.Nodes)
                if (n.Data != null && n.Type != null && (n.Type.Key == Features.Feature || n.Type.Key == EndOfFeatures || Patterns.IsPattern(n))) wanted.Add(n.Index);
            List<int> order = new List<int>();
            foreach (Node root in t.Nodes)
            {
                if (root.Type == null || root.Type.Key != 0x2B48A42B || root.Data == null) continue;
                for (int p = 0; p + 4 <= root.Data.Length; p += 1)
                {
                    int x = Sketches.XRef(root.Data, p);
                    if (wanted.Contains(x) && !order.Contains(x)) { order.Add(x); p += 3; }
                }
                break;
            }
            listed = new List<int>(order);
            // Whatever the browser doesn't list follows, by node order.
            List<int> rest = new List<int>(wanted);
            rest.Sort();
            foreach (int x in rest) if (!order.Contains(x)) order.Add(x);
            return order;
        }

        static string ShapeJson(Naming.Shape s)
        {
            return string.Format("{{\"kind\": {0}, \"a\": {1}, \"b\": {2}, \"r\": {3}, \"r2\": {4}, \"unbounded\": {5}}}", Q(s.Kind),
                Vec(ToMm(s.A)), s.Kind == "line" ? Vec(ToMm(s.B)) : Vec(s.B), N(s.R * Mm), N(s.R2 * Mm), s.Unbounded ? "true" : "false");
        }

        // One B-rep edge's geometry; a face's surface.
        static string EntityJson(Naming.Match m)
        {
            if (m.Entity.Type == "face")
            {
                Naming.Shape s = Naming.Surface(m.Records, m.Entity);
                return s == null ? "{\"kind\": \"unknown\"}" : ShapeJson(s);
            }
            return EdgeJson(m.Records, m.Entity);
        }

        static string EdgeJson(List<SabRecord> recs, SabRecord e)
        {
            Func<SabRef, SabRecord> at = r => r != null && r.Target >= 0 && r.Target < recs.Count ? recs[r.Target] : null;
            double[] a = Sab.VertexPosition(recs, at(e.Ref(3))), b = Sab.VertexPosition(recs, at(e.Ref(5)));
            SabRecord curve = at(e.Ref(8));
            if (curve != null && curve.Type == "straight-curve") return "{\"kind\": \"line\", \"a\": " + Vec(ToMm(a)) + ", \"b\": " + Vec(ToMm(b)) + ", \"unbounded\": false}";
            if (curve != null && curve.Type == "ellipse-curve" && curve.Fields.Count > 6 && curve.Fields[5] is double[] && curve.Fields[6] is double && Math.Abs((double)curve.Fields[6] - 1) < 1e-9)
            {
                double[] major = (double[])curve.Fields[5];
                return "{\"kind\": \"circle\", \"a\": " + Vec(ToMm(curve.Fields[3] as double[])) + ", \"b\": " + Vec(curve.Fields[4] as double[])
                    + ", \"r\": " + N(Mm * Math.Sqrt(major[0] * major[0] + major[1] * major[1] + major[2] * major[2])) + ", \"from\": " + Vec(ToMm(a)) + ", \"to\": " + Vec(ToMm(b))
                    + ", \"rev\": " + (e.Fields.Count > 9 && e.Fields[9] is bool && (bool)e.Fields[9] ? "true" : "false") + "}";
            }
            return "{\"kind\": \"curve\", \"a\": " + Vec(ToMm(a)) + ", \"b\": " + Vec(ToMm(b)) + "}";
        }

        // What each EdgeItem or FaceItem names, as geometry, best evidence first: each item is a list of alternatives.
        static string EdgesJson(List<int> items, Dictionary<int, Naming.Resolved> all)
        {
            List<string> list = new List<string>();
            foreach (int i in items)
            {
                Naming.Resolved r;
                List<string> found = new List<string>();
                if (all.TryGetValue(i, out r))
                {
                    foreach (Naming.Match m in r.Entities)
                    {
                        string j = EntityJson(m);
                        if (!found.Contains(j)) found.Add(j);
                    }
                    if (found.Count == 0 && r.Rebuilt != null && r.Rebuilt.Shape != null) found.Add(ShapeJson(r.Rebuilt.Shape));
                    if (found.Count == 0 && r.FromFaces != null && r.FromFaces.Shape != null) found.Add(ShapeJson(r.FromFaces.Shape));
                    if (found.Count == 0 && r.Surface1 != null && r.Surface2 != null) found.Add("{\"kind\": \"meet\", \"s1\": " + ShapeJson(r.Surface1) + ", \"s2\": " + ShapeJson(r.Surface2) + "}");
                    if (found.Count == 0) found.Add("{\"kind\": \"unknown\", \"why\": " + Q(Naming.Describe(r)) + "}");
                }
                else found.Add("{\"kind\": \"unknown\", \"why\": \"no name\"}");
                list.Add("[" + string.Join(", ", found.ToArray()) + "]");
            }
            return "[" + string.Join(", ", list.ToArray()) + "]";
        }

        static List<string> FeatureJson(NodeTable t, List<Segment> segments, bool made = false)
        {
            Dictionary<string, int> items = Naming.SketchItems(t);
            Dictionary<int, string> json = new Dictionary<int, string>();
            Segment brepSegment = segments.Find(s => string.Equals(s.Name, "PmBRepSegment", StringComparison.OrdinalIgnoreCase));
            Dictionary<int, Naming.Resolved> early = null;
            Func<Dictionary<int, Naming.Resolved>> faceNames = () => early ?? (early = brepSegment == null ? new Dictionary<int, Naming.Resolved>() : Naming.Resolve(t, brepSegment.Data));
            Func<Features.Plane, bool, string> planeJson = (p, work) => "{\"origin\": " + Vec(ToMm(p.Origin)) + ", \"normal\": " + Vec(p.Normal) + (work ? ", \"work\": true" : "") + "}";
            foreach (Features.ExtrudeInfo e in Features.Extrudes(t))
            {
                string to = "null";
                if (e.ToPlane != null) to = planeJson(e.ToPlane, false);
                else if (e.ToWork != null) to = planeJson(e.ToWork, true);
                json[e.Index] = string.Format(CultureInfo.InvariantCulture,
                    "{{\"type\": \"extrude\", \"sketch\": {0}, \"loops\": {1}, \"op\": {2}, \"extent\": {3}, \"dir\": {4}, \"axis\": {5}, \"param\": {6}, \"length\": {7}, \"extent2\": {8}, \"param2\": {9}, \"length2\": {10}, \"taperParam\": {11}, \"taper\": {12}, \"toPlane\": {13}, \"to\": {14}",
                    e.SketchNode, LoopsJson(t, e.ProfileNode, e.SketchNode, items), Q(Op(e.Operation)), Q(Extent(e.Extent)), Q(Dir(e.Direction)), Vec(e.Axis),
                    Q(e.DistanceParam), N(e.DistanceCm * Mm), Q(e.ExtentTwo == null ? null : Extent(e.ExtentTwo)), Q(e.DistanceTwoParam), N(e.DistanceTwoCm * Mm),
                    Q(e.Tapered ? e.TaperParam : null), Q(e.Tapered ? e.Taper : null), to, Q(e.To));
                json[e.Index] += ", \"wires\": " + WiresJson(t, e.ProfileNode, e.SketchNode, items) + ", \"adds\": " + AddsJson(t, e.ProfileNode, e.SketchNode, items);
                if (e.ToCylinder != null)
                    json[e.Index] += ", \"toCylinder\": {\"origin\": " + Vec(ToMm(e.ToCylinder.Origin)) + ", \"axis\": " + Vec(e.ToCylinder.Axis) + ", \"r\": " + N(e.ToCylinder.RadiusU * Mm) + "}";
                if (e.FromWork != null) json[e.Index] += ", \"fromPlane\": " + planeJson(e.FromWork, true);
                // A 2026 file names the "to" face only: its surface comes from the B-rep's naming, as for a fillet's edges.
                if (e.ToPlane == null && e.ToCylinder == null && e.ToFaceItem >= 0)
                    json[e.Index] += ", \"toFace\": " + EdgesJson(new List<int> { e.ToFaceItem }, faceNames());
            }
            foreach (Features.RevolveInfo r in Features.Revolves(t))
                json[r.Index] = string.Format("{{\"type\": \"revolve\", \"sketch\": {0}, \"loops\": {1}, \"op\": {2}, \"extent\": {3}, \"dir\": {4}, \"param\": {5}, \"angle\": {6}, \"axis\": [{7}, {8}]",
                    r.SketchNode, LoopsJson(t, r.ProfileNode, r.SketchNode, items), Q(Op(r.Operation)), Q(Extent(r.Extent)), Q(Dir(r.Direction)), Q(r.AngleParam), Q(r.Angle), Vec(ToMm(r.AxisStart)), Vec(ToMm(r.AxisEnd)))
                    + ", \"wires\": " + WiresJson(t, r.ProfileNode, r.SketchNode, items);
            foreach (Features.HoleInfo h in Features.Holes(t))
                json[h.Index] = string.Format("{{\"type\": \"hole\", \"kind\": {0}, \"extent\": {1}, \"dir\": {2}, \"axis\": {3}, \"sketch\": {4}, \"centers\": {5}, \"position\": {6}, \"diameter\": {7}, \"depth\": {8}, \"headDiameter\": {9}, \"headDepth\": {10}, \"headAngle\": {11}, \"tip\": {12}, \"flat\": {13}, \"thread\": {14}",
                    Q(h.HoleType), Q(Extent(h.Extent)), Q(Dir(h.Direction)), Vec(h.Axis), h.SketchNode, Ids(h.CenterNodes), Vec(ToMm(h.Position)),
                    Q(h.DiameterParam), Q(h.DepthParam), Q(h.HeadDiameterParam), Q(h.HeadDepthParam), Q(h.HeadAngleParam), Q(h.TipParam), h.FlatBottom == "true" ? "true" : "false",
                    Q(h.Thread == null ? null : h.Thread.Designation));
            Segment brep = segments.Find(s => string.Equals(s.Name, "PmBRepSegment", StringComparison.OrdinalIgnoreCase));
            Dictionary<int, Naming.Resolved> resolved = null;
            Func<Dictionary<int, Naming.Resolved>> names = () => resolved ?? (resolved = brep == null ? new Dictionary<int, Naming.Resolved>() : Naming.Resolve(t, brep.Data));
            foreach (Features.FilletInfo f in Features.Fillets(t))
            {
                List<string> sets = new List<string>();
                foreach (Features.EdgeSet s in f.Sets)
                    sets.Add(string.Format("{{\"param\": {0}, \"radius\": {1}, \"mode\": {2}, \"edges\": {3}}}", Q(s.RadiusParam), N(s.RadiusCm * Mm), s.Mode, EdgesJson(s.Items, names())));
                // The options that change the shape where a round runs out of face (checked against the generated fillet parts).
                json[f.Index] = "{\"type\": \"fillet\", \"sets\": [" + string.Join(", ", sets.ToArray()) + "]"
                    + ", \"rollAlongSharpEdges\": " + (f.RollAlongSharpEdges == "true" ? "true" : "false")
                    + ", \"rollingBall\": " + (f.RollingBallWherePossible == "true" ? "true" : "false")
                    + ", \"preserveAllFeatures\": " + (f.PreserveAllFeatures == "true" ? "true" : "false");
            }
            foreach (Features.ChamferInfo c in Features.Chamfers(t))
                json[c.Index] = string.Format("{{\"type\": \"chamfer\", \"kind\": {0}, \"param\": {1}, \"param2\": {2}, \"angleParam\": {3}, \"edges\": {4}, \"faces\": {5}",
                    Q(c.ChamferType), Q(c.SizeParam), Q(c.Size2Param), Q(c.AngleParam), EdgesJson(c.EdgeItems, names()), EdgesJson(c.FaceItems, names()));
            foreach (MoreFeatures.ThreadInfo x in MoreFeatures.Threads(t)) json[x.Index] = "{\"type\": \"thread\"";
            foreach (MoreFeatures.ShellInfo x in MoreFeatures.Shells(t))
                json[x.Index] = string.Format(CultureInfo.InvariantCulture, "{{\"type\": \"shell\", \"direction\": {0}, \"param\": {1}, \"thickness\": {2}, \"sets\": {3}, \"faces\": {4}",
                    Q(x.Direction), Q(x.ThicknessParam), N(x.ThicknessCm * Mm), x.Sets.Count, EdgesJson(x.Removed, names()));
            foreach (MoreFeatures.SweepInfo x in MoreFeatures.Sweeps(t))
                json[x.Index] = string.Format("{{\"type\": \"sweep\", \"op\": {0}, \"sweepType\": {1}, \"orientation\": {2}, \"scaling\": {3}, \"sketch\": {4}, \"loops\": {5}, \"wires\": {6}, \"path\": {7}, \"rail\": {8}, \"taper\": {9}, \"taperParam\": {10}, \"twist\": {11}, \"twistParam\": {12}",
                    Q(Op(x.Operation)), Q(x.SweepType), Q(x.Orientation), Q(x.Scaling), x.SketchNode, LoopsJson(t, x.ProfileNode, x.SketchNode, items), WiresJson(t, x.ProfileNode, x.SketchNode, items),
                    x.PathCurves == null ? "null" : string.Format("{{\"sketch\": {0}, \"curves\": {1}}}", x.PathSketch, Ids(x.PathCurves)),
                    x.RailCurves == null ? "null" : string.Format("{{\"sketch\": {0}, \"curves\": {1}}}", x.RailSketch, Ids(x.RailCurves)),
                    Q(x.Taper), Q(x.TaperParam), Q(x.Twist), Q(x.TwistParam));
            foreach (MoreFeatures.LoftInfo x in MoreFeatures.Lofts(t))
            {
                // A section is a profile (its loops, as for an extrude) or a sketch point; rails and the centerline are profiles too.
                Func<MoreFeatures.Section, string> sec = s => s == null ? "null"
                    : s.PointNode >= 0 ? string.Format("{{\"sketch\": {0}, \"point\": {1}}}", s.Sketch, s.PointNode)
                    : string.Format("{{\"sketch\": {0}, \"loops\": {1}, \"wires\": {2}}}", s.Sketch, LoopsJson(t, s.Profile, s.Sketch, items), WiresJson(t, s.Profile, s.Sketch, items));
                json[x.Index] = string.Format("{{\"type\": \"loft\", \"op\": {0}, \"loftType\": {1}, \"closed\": {2}, \"merge\": {3}, \"sections\": [{4}], \"rails\": [{5}], \"centerline\": {6}, \"first\": {{\"condition\": {7}, \"angle\": {8}, \"weight\": {9}}}, \"last\": {{\"condition\": {10}, \"angle\": {11}, \"weight\": {12}}}",
                    Q(Op(x.Operation)), Q(x.LoftType), x.Closed ? "true" : "false", x.Merge ? "true" : "false",
                    string.Join(", ", x.SectionList.ConvertAll(s => sec(s)).ToArray()), string.Join(", ", x.RailList.ConvertAll(s => sec(s)).ToArray()), sec(x.CenterlineSection),
                    Q(x.FirstCondition), Q(x.FirstAngle), Q(x.FirstImpact), Q(x.LastCondition), Q(x.LastAngle), Q(x.LastImpact));
            }
            foreach (MoreFeatures.SheetInfo x in MoreFeatures.Sheets(t))
            {
                // A plate is an extrude of its sketch by the thickness; a flange is built from its edge, bend and height.
                if (x.Kind == "flange" && !double.IsNaN(x.HeightCm + x.AngleRad + x.RadiusCm + x.ThicknessCm))
                    json[x.Index] = string.Format(CultureInfo.InvariantCulture,
                        "{{\"type\": \"flange\", \"dir\": {0}, \"height\": {1}, \"angle\": {2}, \"radius\": {3}, \"thickness\": {4}, \"edges\": {5}",
                        Q(Dir(x.Direction)), N(x.HeightCm * Mm), N(x.AngleRad * 180 / Math.PI), N(x.RadiusCm * Mm), N(x.ThicknessCm * Mm), EdgesJson(Naming.Inputs(t, t.At(x.Index)), names()));
                else if (x.Kind == "bend" || x.Kind == "corner")
                    json[x.Index] = "{\"type\": \"sheet metal\", \"kind\": " + Q(x.Kind);
                else if (x.Kind == "plate" && x.SketchNode >= 0 && !double.IsNaN(x.ThicknessCm))
                    json[x.Index] = string.Format(CultureInfo.InvariantCulture,
                        "{{\"type\": \"extrude\", \"sheet\": true, \"sketch\": {0}, \"loops\": {1}, \"op\": {2}, \"extent\": \"distance\", \"dir\": {3}, \"axis\": null, \"param\": {4}, \"length\": {5}, \"extent2\": null, \"param2\": null, \"length2\": null, \"taperParam\": null, \"taper\": null, \"toPlane\": null, \"to\": null, \"wires\": {6}",
                        x.SketchNode, LoopsJson(t, x.ProfileNode, x.SketchNode, items), Q(Op(x.Operation)), Q(Dir(x.Direction)), Q(x.ThicknessParam), N(x.ThicknessCm * Mm), WiresJson(t, x.ProfileNode, x.SketchNode, items));
                else json[x.Index] = "{\"type\": \"sheet metal\"";
            }
            // The first imported body is the one the history's first body is (the made STEP beside the model).
            bool firstImport = made;
            foreach (MoreFeatures.ImportedInfo x in MoreFeatures.ImportedBodies(t))
            {
                json[x.Index] = "{\"type\": \"imported body\"" + (firstImport && x.Kind == "imported body" ? ", \"made\": true" : "");
                if (x.Kind == "imported body") firstImport = false;
            }
            // Mirrors and patterns are nodes of their own, so they join the timeline by node order.
            List<KeyValuePair<int, string>> timeline = new List<KeyValuePair<int, string>>();
            HashSet<int> rolled = RolledBack(t);
            // The state byte (+0x0A): 04 suppressed or not computed, 02 failing (Inventor's kDriverLostHealth: in the 223
            // generated parts every 02 is that and every 00 is healthy, and the stored solid hasn't got the feature).
            Func<byte, string> computed = s => (s & 0x06) == 0 ? "true" : "false";
            Func<int, string> back = i => rolled.Contains(i) ? ", \"rolledBack\": true" : "";
            foreach (Patterns.PatternInfo x in Patterns.List(t))
            {
                string j = string.Format("{{\"type\": {0}, \"id\": {1}, \"parents\": {2}, \"ofBody\": {3}, \"origin\": {4}, \"normal\": {5}, \"n\": {6}, \"countParam\": [{7}, {8}], \"spacingParam\": [{9}, {10}], \"step\": {11}, \"dir1\": {12}, \"dir2\": {13}, \"hasY\": {14}, \"fitted\": [{15}, {16}], \"mid\": [{17}, {18}], \"angleParam\": {19}, \"angle\": {20}, \"axisPoint\": {21}, \"axisDir\": {22}, \"reversed\": {23}, \"incremental\": {24}",
                    Q(x.Kind), x.Index, Ids(x.ParentNodes), x.OfBody ? "true" : "false", Vec(ToMm(x.Origin)), Vec(x.Normal), Ids(x.N), Q(x.CountParam[0]), Q(x.CountParam[1]), Q(x.SpacingParam[0]), Q(x.SpacingParam[1]),
                    Arr(Array.ConvertAll(x.Step, v => v * Mm)), Vec(x.Direction[0]), Vec(x.Direction[1]), x.HasY ? "true" : "false", x.Fitted[0] ? "true" : "false", x.Fitted[1] ? "true" : "false", x.MidPlane[0] ? "true" : "false", x.MidPlane[1] ? "true" : "false",
                    Q(x.AngleParam), N(x.AngleRad * 180 / Math.PI), Vec(ToMm(x.AxisPoint)), Vec(x.AxisDirection), x.Reversed ? "true" : "false", x.Incremental ? "true" : "false");
                timeline.Add(new KeyValuePair<int, string>(x.Index, j + ", \"name\": " + Q(x.Name) + ", \"computed\": " + computed(x.State) + back(x.Index) + "}"));
            }
            foreach (Features.Info fi in Features.List(t))
            {
                string j;
                if (!json.TryGetValue(fi.Node.Index, out j)) j = "{\"type\": \"class " + fi.Kind.ToString("X8") + "\"";
                timeline.Add(new KeyValuePair<int, string>(fi.Node.Index, j + ", \"id\": " + fi.Node.Index + ", \"name\": " + Q(fi.Name) + ", \"computed\": " + computed(fi.State) + back(fi.Node.Index) + "}"));
            }
            List<int> order = Timeline(t);
            timeline.Sort((a, b) => order.IndexOf(a.Key).CompareTo(order.IndexOf(b.Key)));
            // Some files keep a plate's node after the fillets on it; a fillet needs a solid to work on, so the solid goes first.
            int maker = timeline.FindIndex(kv => !kv.Value.StartsWith("{\"type\": \"fillet\"") && !kv.Value.StartsWith("{\"type\": \"chamfer\""));
            if (maker > 0)
            {
                KeyValuePair<int, string> first = timeline[maker];
                timeline.RemoveAt(maker);
                timeline.Insert(0, first);
            }
            return timeline.ConvertAll(kv => kv.Value);
        }

        // Points along a circle or ellipse edge, so the bounding box sees past its end points. ellipse-curve: centre,
        // normal, major axis (fields 3, 4, 5), ratio (6); edge: start vertex, start parameter, end vertex, end parameter
        // (3, 4, 5, 6). A reversed edge keeps its parameters negated, so the start vertex says which way they run.
        static List<double[]> ArcSamples(List<SabRecord> records, SabRecord e)
        {
            List<double[]> pts = new List<double[]>();
            SabRef cr = e.Ref(8);
            SabRecord c = cr != null && cr.Target >= 0 && cr.Target < records.Count ? records[cr.Target] : null;
            if (c == null || c.Type != "ellipse-curve" || c.Fields.Count < 7 || !(c.Fields[6] is double) || !(e.Fields[4] is double) || !(e.Fields[6] is double)) return pts;
            double[] o = c.Fields[3] as double[], n = c.Fields[4] as double[], m = c.Fields[5] as double[];
            if (o == null || n == null || m == null) return pts;
            double ratio = (double)c.Fields[6], s = (double)e.Fields[4], t = (double)e.Fields[6];
            double[] v = { n[1] * m[2] - n[2] * m[1], n[2] * m[0] - n[0] * m[2], n[0] * m[1] - n[1] * m[0] };
            Func<double, double[]> at = a => new[] {
                o[0] + m[0] * Math.Cos(a) + v[0] * ratio * Math.Sin(a),
                o[1] + m[1] * Math.Cos(a) + v[1] * ratio * Math.Sin(a),
                o[2] + m[2] * Math.Cos(a) + v[2] * ratio * Math.Sin(a) };
            SabRef sv = e.Ref(3);
            double[] start = sv != null && sv.Target >= 0 && sv.Target < records.Count ? Sab.VertexPosition(records, records[sv.Target]) : null;
            double sign = 1;
            if (start != null)
            {
                double[] p = at(s), q = at(-s);
                Func<double[], double> dist = x => Math.Abs(x[0] - start[0]) + Math.Abs(x[1] - start[1]) + Math.Abs(x[2] - start[2]);
                if (dist(q) < dist(p)) sign = -1;
            }
            for (int i = 0; i <= 720; i++) pts.Add(at(sign * (s + (t - s) * i / 720)));
            return pts;
        }

        // Inventor's final solid: its bounding box, how many of each entity, and the surfaces its faces lie on.
        static string ExpectJson(List<Segment> segments)
        {
            Segment brep = segments.Find(s => string.Equals(s.Name, "PmBRepSegment", StringComparison.OrdinalIgnoreCase));
            if (brep == null || brep.Data == null) return "null";
            string error;
            List<SabRecord> records = Sab.Read(brep.Data, 0, out error);
            int faces = 0, edges = 0, vertices = 0;
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            List<string> surfaces = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            foreach (SabRecord r in records)
            {
                if (r.Old) continue;
                if (r.Type == "edge")
                {
                    edges++;
                    foreach (double[] p in ArcSamples(records, r))
                        for (int i = 0; i < 3; i++) { lo[i] = Math.Min(lo[i], p[i] * Mm); hi[i] = Math.Max(hi[i], p[i] * Mm); }
                }
                else if (r.Type == "vertex")
                {
                    vertices++;
                    double[] p = Sab.VertexPosition(records, r);
                    if (p != null) for (int i = 0; i < 3; i++) { lo[i] = Math.Min(lo[i], p[i] * Mm); hi[i] = Math.Max(hi[i], p[i] * Mm); }
                }
                else if (r.Type == "face")
                {
                    faces++;
                    Naming.Shape s = Naming.Surface(records, r);
                    if (s == null) continue;
                    string j = string.Format("{{\"kind\": {0}, \"a\": {1}, \"b\": {2}, \"r\": {3}, \"r2\": {4}}}", Q(s.Kind), Vec(ToMm(s.A)), Vec(s.B), N(s.R * Mm), N(s.R2 * Mm));
                    if (seen.Add(j)) surfaces.Add(j);
                }
            }
            return "{\"faces\": " + faces + ", \"edges\": " + edges + ", \"vertices\": " + vertices
                + ", \"bbox\": " + (vertices == 0 ? "null" : Arr(new[] { lo[0], lo[1], lo[2], hi[0], hi[1], hi[2] }))
                + ",\n  \"surfaces\": [\n    " + string.Join(",\n    ", surfaces.ToArray()) + "\n  ]}";
        }
    }
}
