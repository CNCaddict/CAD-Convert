// Resolves the B-rep edges and faces that features refer to.
//
// A feature input such as a fillet edge is an EdgeItem (FaceItem for faces) in the DC segment. An id node
// (EdgeId, 90874D55 / FaceId / FaceSurfaceId for faces) cross-refs the item and names it through the naming table
// (CCE92042): u32 id at +0x16, ..., then {xref table, u32 entry index} pairs.
// The B-rep keeps the link the other way: entities the name was matched to carry INV_NMX_MATCHED_ATTRIB
// {1, 44, 1, id, -1, entry index, flag}: flag 0 for the entity itself (the edge or face), 1 for entities
// alongside it (an edge's vertices). Entities a later feature consumed (an edge rounded by its fillet) are kept
// among the B-rep's earlier-state entities, so they still resolve, with their geometry.
using System;
using System.Collections.Generic;
using System.Text;

namespace InvRe
{
    public static class Naming
    {
        public const uint Table = 0xCCE92042;
        static readonly HashSet<string> IdTypes = new HashSet<string> { "EdgeId", "90874D55", "FaceId", "FaceSurfaceId" };

        public class Match
        {
            public List<SabRecord> Records;   // the ASM block the entity is in
            public SabRecord Entity;
            public int Entry, Flag;
        }

        // Naming attributes come in two forms. From 2024, ATTRIB_CUSTOM records named "INV_NMX_..." with data
        // {1, 44, ...}. Before that, typed records "NMx_<kind>-...-NamingMatching-attrib" with the same data but
        // without the leading 1. Both give the INV name and the data from the 1 on (fields after the owner).
        static readonly Dictionary<string, string> OldNames = new Dictionary<string, string> {
            { "NMx_Sweep_Generated_Brep_tag", "INV_NMX_SWEEPGENERATED_TAG" }, { "NMx_blend_tag", "INV_NMX_BLEND_TAG" },
            { "NMx_Matched_Entity", "INV_NMX_MATCHED_ATTRIB" }, { "NMx_Generated_Brep_tag", "INV_NMX_GEN_TAG" } };

        public static bool NamingAttr(SabRecord a, out string name, out List<object> data)
        {
            name = null;
            data = null;
            if (a.Type == null || !a.Type.EndsWith("attrib") || a.Fields.Count < 6) return false;
            if (a.Type == "ATTRIB_CUSTOM-attrib")
            {
                name = a.Fields[5] as string;
                if (name == null) return false;
                data = a.Fields.GetRange(6, a.Fields.Count - 6);
                return true;
            }
            if (!a.Type.StartsWith("NMx_") || !a.Type.EndsWith("NamingMatching-attrib")) return false;
            string kind = a.Type.Substring(0, a.Type.IndexOf('-'));
            if (!OldNames.TryGetValue(kind, out name)) name = kind;
            data = new List<object> { 1 };
            data.AddRange(a.Fields.GetRange(5, a.Fields.Count - 5));
            return true;
        }

        static int Di(List<object> d, int i) { return i >= 0 && i < d.Count && d[i] is int ? (int)d[i] : int.MinValue; }

        static SabRecord Owner(List<SabRecord> records, SabRecord a)
        {
            SabRef o = a.Ref(4);
            return o != null && o.Target >= 0 && o.Target < records.Count ? records[o.Target] : null;
        }

        // B-rep entities by the id of the name they were matched to.
        public static Dictionary<int, List<Match>> Matches(byte[] brep)
        {
            Dictionary<int, List<Match>> r = new Dictionary<int, List<Match>>();
            int at = 0;
            while ((at = Sab.Find(brep, at)) >= 0)
            {
                string error;
                List<SabRecord> records = Sab.Read(brep, at, out error);
                foreach (SabRecord a in records)
                {
                    string name;
                    List<object> data;
                    if (!NamingAttr(a, out name, out data) || name != "INV_NMX_MATCHED_ATTRIB" || data.Count < 7) continue;
                    SabRecord owner = Owner(records, a);
                    int id = Di(data, 3);
                    if (owner == null || id == int.MinValue) continue;
                    if (!r.ContainsKey(id)) r[id] = new List<Match>();
                    r[id].Add(new Match { Records = records, Entity = owner, Entry = Di(data, 5), Flag = Di(data, 6) });
                }
                at++;
            }
            return r;
        }

        public class Resolved
        {
            public Node Item, Id;
            public int Name = -1;              // the id node's id (+0x16)
            public List<int> Entries = new List<int>();   // naming table entries it lists
            public List<Match> Entities = new List<Match>();   // flag 0 matches of the item's kind (edge or face)
            public string Via;                 // "matched" (MATCHED attribute) or "generated" (same generator name)
            public List<string> EntryTypes = new List<string>();
            public List<Generator> Generators = new List<Generator>();
            public Rebuilt Rebuilt;            // from its own sweep name (edge for an edge, face for a face), when it has one
            public Rebuilt FromFaces;          // an edge from the two faces its entry names: where they meet
            public List<Match> CommonEdges = new List<Match>();   // ... or the B-rep edge those faces share, on that line
            public List<Match> Unconfirmed = new List<Match>();   // shared edges where the faces' meeting line is unknown
            public bool AtVertices;            // the shared edges (common or unconfirmed) were kept because they end at its two vertices
            public string MeetKinds;           // the two surfaces FromFaces met, e.g. "cone+plane"
            public Shape Surface1, Surface2;   // the surfaces of the two faces an edge is named by, where known
            public bool ByChain;               // not found, but every name it is built from is (fillet faces of findable edges)
            public int FacesKnown = -1;        // of the edge entry's two faces, how many have a known surface
            public string FaceKinds;
        }

        // Per document: what resolving needs, loaded when first used.
        class Context
        {
            public NodeTable Dc;
            public NodeTable PartDc;     // where the features are, when Dc is an assembly naming a part's entities
            public byte[] Brep;
            Dictionary<string, List<Match>> generated, blends, lofts;
            public HashSet<int> Found;   // naming entries some input resolved through
            Document doc;
            Dictionary<int, int> main;
            public Dictionary<string, List<Match>> Generated { get { return generated ?? (generated = Naming.Generated(Brep)); } }
            public Dictionary<string, List<Match>> Blends { get { return blends ?? (blends = Naming.Blends(Brep)); } }
            public Dictionary<string, List<Match>> Lofts { get { return lofts ?? (lofts = Naming.Lofts(Brep)); } }
            Dictionary<string, List<Match>> holes, flanges;
            public Dictionary<string, List<Match>> Holes { get { return holes ?? (holes = TaggedFaces(Brep, "HOLE", 2)); } }
            public Dictionary<string, List<Match>> Flanges { get { return flanges ?? (flanges = TaggedFaces(Brep, "FLANGE", 4)); } }
            Dictionary<string, List<Match>> shells, bends;
            public Dictionary<string, List<Match>> Shells { get { return shells ?? (shells = TaggedFaces(Brep, "SHELL", 4)); } }
            public Dictionary<string, List<Match>> Bends { get { return bends ?? (bends = TaggedFaces(Brep, "BEND", 4)); } }
            public Document Doc { get { return doc ?? (doc = new Document(PartDc ?? Dc)); } }
            public void UseTable(NodeTable dc, Node table) { Dc = dc; main = Entries(table); }
            public Node Entry(int index)
            {
                if (main == null) { Node t = MainTable(Dc); main = t == null ? new Dictionary<int, int>() : Entries(t); }
                int n;
                return main.TryGetValue(index, out n) ? Dc.At(n) : null;
            }
        }

        // What an assembly's naming entry grips in a part. The assembly's entries are laid out as the part's, after the path of
        // occurrence numbers to the part, and name the entities the part's B-rep carries.
        public class Grip
        {
            public string EntryType, Kind = "?";
            public List<Match> Found = new List<Match>();    // the B-rep entities it names, current ones if there are any
            public double[] Vertex;                          // a vertex known only by where it is
            public Shape Surface;                            // a face or edge known only by its surface or line
            public List<Match> Nearby = new List<Match>();   // edges of a named face, when the name doesn't lead to one edge
            public bool ByPosition;                          // Found holds entities in the place the file's copy of the geometry says, not by name
        }

        static readonly Dictionary<byte[], Context> partContexts = new Dictionary<byte[], Context>();

        public static Grip ResolveGrip(NodeTable outer, Node table, int entryIndex, NodeTable partDc, byte[] brep)
        {
            Context c;
            if (!partContexts.TryGetValue(brep, out c)) partContexts[brep] = c = new Context { PartDc = partDc, Brep = brep };
            c.UseTable(outer, table);
            Node entry = c.Entry(entryIndex);
            Grip g = new Grip { EntryType = entry == null ? "missing" : entry.TypeName };
            if (entry == null || entry.Type == null) return g;
            Func<List<Match>, List<Match>> current = list => { List<Match> now = list.FindAll(m => !m.Entity.Old); return now.Count > 0 ? now : list; };
            if (IsFaceEntry(entry) || entry.Type.Key == 0x8E5D4198 || entry.Type.Key == 0x357D669C || IsTaggedFace(entry))
            {
                g.Kind = "face";
                string what;
                g.Found = current(FaceMatches(c, entry, out g.Surface, out what));
            }
            else if (entry.Type.Key == 0xB1ED010F)
            {
                // A face named with its neighbours: a list of entries, the face first. A later feature can split a face and its
                // name then covers each piece; the piece meant is the one touching the neighbours.
                g.Kind = "face";
                List<int> refs = EntryRefs(outer, entry);
                string what;
                if (refs.Count > 0) g.Found = current(FaceMatches(c, c.Entry(refs[0]), out g.Surface, out what));
                for (int i = 1; i < refs.Count && g.Found.Count > 1; i++)
                {
                    Shape s;
                    List<Match> others = current(FaceMatches(c, c.Entry(refs[i]), out s, out what));
                    if (others.Count == 0) continue;
                    List<Match> touching = g.Found.FindAll(f => {
                        HashSet<int> edges = FaceEdges(f.Records, f.Entity);
                        return others.Exists(o => { if (o.Records != f.Records) return false; HashSet<int> shared = FaceEdges(o.Records, o.Entity); shared.IntersectWith(edges); return shared.Count > 0; });
                    });
                    if (touching.Count > 0) g.Found = touching;
                }
            }
            else if (IsEdgeEntry(entry))
            {
                g.Kind = "edge";
                Generator made = EntryGenerator(entry);
                List<Match> found;
                if (made != null && c.Generated.TryGetValue(Key(made), out found)) g.Found = current(found);
                if (g.Found.Count == 0)
                {
                    Resolved r = new Resolved();
                    EdgeFromFaces(c, r, entry);
                    g.Found = current(r.CommonEdges.Count > 0 ? r.CommonEdges : r.Unconfirmed);
                    if (r.FromFaces != null) g.Surface = r.FromFaces.Shape;
                    // A circle or arc: every edge the two faces share, for the constraint's geometry to choose among.
                    if (g.Found.Count == 0) g.Found = current(SharedEdges(c, entry));
                    // One of the two can be a face that can't be looked up from here (a fillet's name counts entries in the part's
                    // own table): then the edges of the other, for the geometry to choose among.
                    if (g.Found.Count == 0) { g.Nearby = current(EdgesOfNamedFaces(c, entry)); g.Kind = "edge"; }
                }
            }
            else if (entry.Type.Key == 0x05C619B6 || entry.Type.Key == 0x2E04A208 || entry.Type.Key == 0x90F4820A)
            {
                g.Kind = "vertex";
                Generator made = EntryGenerator(entry);
                List<Match> found;
                if (made != null && c.Generated.TryGetValue(Key(made), out found)) g.Found = current(found);
                if (g.Found.Count == 0) g.Vertex = VertexAt(c, entry);
            }
            return g;
        }

        static List<Match> SharedEdges(Context c, Node entry)
        {
            List<Match> r = new List<Match>();
            List<int> faces = NamingFaces(c.Dc, entry);
            if (faces.Count != 2) return r;
            Shape s;
            string w;
            List<Match> f1 = FaceMatches(c, c.Entry(faces[0]), out s, out w), f2 = FaceMatches(c, c.Entry(faces[1]), out s, out w);
            foreach (Match a in f1)
                foreach (Match b in f2)
                {
                    if (a.Records != b.Records) continue;
                    HashSet<int> shared = FaceEdges(a.Records, a.Entity);
                    shared.IntersectWith(FaceEdges(b.Records, b.Entity));
                    foreach (int e in shared)
                        if (!r.Exists(m => m.Records == a.Records && m.Entity.Index == e)) r.Add(new Match { Records = a.Records, Entity = a.Records[e] });
                }
            return r;
        }

        static List<Match> EdgesOfNamedFaces(Context c, Node entry)
        {
            List<Match> r = new List<Match>();
            foreach (int f in NamingFaces(c.Dc, entry))
            {
                Shape s;
                string w;
                foreach (Match m in FaceMatches(c, c.Entry(f), out s, out w))
                    foreach (int e in FaceEdges(m.Records, m.Entity))
                        if (!r.Exists(x => x.Records == m.Records && x.Entity.Index == e)) r.Add(new Match { Records = m.Records, Entity = m.Records[e] });
            }
            return r;
        }

        // Where an entity is, to tell entities in different places from the same one found twice: its type and the box around it.
        public static string Place(Match m)
        {
            List<double[]> pts = new List<double[]>();
            if (m.Entity.Type == "vertex") { double[] p = Sab.VertexPosition(m.Records, m.Entity); if (p != null) pts.Add(p); }
            else if (m.Entity.Type == "edge") pts.AddRange(EdgePoints(m.Records, m.Entity));
            else if (m.Entity.Type == "face") foreach (int e in FaceEdges(m.Records, m.Entity)) pts.AddRange(EdgePoints(m.Records, m.Records[e]));
            if (pts.Count == 0) return m.Entity.Type;
            double[] low = { double.MaxValue, double.MaxValue, double.MaxValue }, high = { double.MinValue, double.MinValue, double.MinValue };
            foreach (double[] p in pts) for (int k = 0; k < 3; k++) { low[k] = Math.Min(low[k], p[k]); high[k] = Math.Max(high[k], p[k]); }
            return m.Entity.Type + " " + Math.Round(low[0], 4) + " " + Math.Round(low[1], 4) + " " + Math.Round(low[2], 4) + " " + Math.Round(high[0], 4) + " " + Math.Round(high[1], 4) + " " + Math.Round(high[2], 4);
        }

        // For looking into a grip: the entry, what it refers to, and which entities in the part carry the geometry, with their names.
        public static string ExplainGrip(NodeTable outer, Node table, int entryIndex, NodeTable partDc, byte[] brep, string geometry, bool direction)
        {
            StringBuilder b = new StringBuilder();
            Context c;
            if (!partContexts.TryGetValue(brep, out c)) partContexts[brep] = c = new Context { PartDc = partDc, Brep = brep };
            c.UseTable(outer, table);
            Action<Node, string> one = null;
            one = (e, indent) => {
                if (e == null) { b.AppendLine(indent + "(missing)"); return; }
                Generator g = EntryGenerator(e);
                List<Match> found = null;
                if (g != null) c.Generated.TryGetValue(Key(g), out found);
                b.AppendFormat("{0}#{1:X} {2} {3} -> {4} in the B-rep\n", indent, e.Index, e.TypeName, g == null ? "(no generator)" : Key(g), found == null ? 0 : found.Count);
            };
            Node entry = c.Entry(entryIndex);
            one(entry, "  ");
            foreach (int r in EntryRefs(outer, entry)) one(c.Entry(r), "    ");
            int at = 0;
            while ((at = Sab.Find(brep, at)) >= 0)
            {
                string error;
                List<SabRecord> records = Sab.Read(brep, at, out error);
                foreach (SabRecord e in records)
                {
                    if (e.Type != "face" && e.Type != "edge" && e.Type != "vertex") continue;
                    if (!Carries(new Match { Records = records, Entity = e }, geometry, direction)) continue;
                    b.AppendFormat("  carries it: {0}{1} {2}\n", e.Type, e.Old ? "*" : "", Sab.Where(records, e));
                    if (e.Type == "edge")
                        foreach (SabRecord f in EdgeFaces(records, e))
                        {
                            b.AppendFormat("     face {0}\n", Sab.Where(records, f));
                            foreach (SabRecord a in records)
                            {
                                string name;
                                List<object> data;
                                if (!NamingAttr(a, out name, out data) || Owner(records, a) != f) continue;
                                b.AppendFormat("        {0} {1}\n", name, string.Join(" ", data.ConvertAll(x => x == null ? "null" : x.ToString()).ToArray()));
                            }
                        }
                }
                at++;
            }
            return b.ToString();
        }

        // The edges found become their end vertices.
        public static void EndsInstead(Grip g)
        {
            List<Match> ends = new List<Match>();
            foreach (Match m in g.Found)
                foreach (int f in new[] { 3, 5 })
                {
                    SabRef r = m.Entity.Ref(f);
                    if (r == null || r.Target < 0 || r.Target >= m.Records.Count) continue;
                    if (!ends.Exists(x => x.Records == m.Records && x.Entity == m.Records[r.Target])) ends.Add(new Match { Records = m.Records, Entity = m.Records[r.Target] });
                }
            g.Found = ends;
            g.Kind = "vertex";
        }

        static double[] Words(string[] w, int at) { return new[] { double.Parse(w[at], System.Globalization.CultureInfo.InvariantCulture), double.Parse(w[at + 1], System.Globalization.CultureInfo.InvariantCulture), double.Parse(w[at + 2], System.Globalization.CultureInfo.InvariantCulture) }; }

        // Does this B-rep entity carry the constraint's geometry, as text in the part's coordinates: "plane p normal n",
        // "line p direction d", "circle c axis n r R", "point p", "cylinder p axis a r R", "sphere c r R", "torus c axis a radii R r".
        // A direction (an angle constraint's) is the normal or axis of a face or the line of an edge.
        public static bool Carries(Match m, string geometry, bool direction)
        {
            const double tol = 2e-5;
            string[] w = geometry.Split(' ');
            Func<double[], double[], double> dist = (p, q) => Len(new[] { p[0] - q[0], p[1] - q[1], p[2] - q[2] });
            if (m.Entity.Type == "vertex")
            {
                double[] at = Sab.VertexPosition(m.Records, m.Entity);
                return at != null && w[0] == "point" && !direction && dist(at, Words(w, 1)) < tol;
            }
            if (m.Entity.Type == "face")
            {
                Shape s = Surface(m.Records, m.Entity);
                if (s == null) return w[0] == "bspline";
                switch (w[0])
                {
                    case "plane": return s.Kind == "plane" && Len(Cross(Unit(s.B), Words(w, 5))) < tol && OnSurface(s, Words(w, 1));
                    case "cylinder": return s.Kind == "cylinder" && Len(Cross(Unit(s.B), Words(w, 5))) < tol && OnSurface(s, Words(w, 1).Length == 3 ? Plus(Words(w, 1), RadialOf(Words(w, 5)), double.Parse(w[9], System.Globalization.CultureInfo.InvariantCulture)) : null);
                    case "line":
                        // A cylinder's, cone's or torus's axis, inferred from the face.
                        if (s.Kind == "plane" || s.Kind == "sphere") return false;
                        double[] d = Words(w, 5), r = Words(w, 1);
                        return Len(Cross(Unit(s.B), d)) < tol && Len(Cross(new[] { r[0] - s.A[0], r[1] - s.A[1], r[2] - s.A[2] }, Unit(s.B))) < tol;
                    case "point":
                        if (direction) return s.B != null && Len(Cross(Unit(s.B), Words(w, 1))) < tol;
                        return s.Kind == "sphere" && dist(s.A, Words(w, 1)) < tol;   // a sphere's centre, inferred
                    case "sphere": return s.Kind == "sphere" && dist(s.A, Words(w, 1)) < tol && Math.Abs(s.R - double.Parse(w[5], System.Globalization.CultureInfo.InvariantCulture)) < tol;
                    case "torus": return s.Kind == "torus" && dist(s.A, Words(w, 1)) < tol && Len(Cross(Unit(s.B), Words(w, 5))) < tol;
                }
                return false;
            }
            if (m.Entity.Type == "edge")
            {
                List<double[]> pts = EdgePoints(m.Records, m.Entity);
                Func<SabRef, SabRecord> ref1 = x => x != null && x.Target >= 0 && x.Target < m.Records.Count ? m.Records[x.Target] : null;
                SabRecord curve = ref1(m.Entity.Ref(8));
                switch (w[0])
                {
                    case "line":
                        double[] d = Words(w, 5), r = Words(w, 1);
                        return pts.Count > 0 && pts.TrueForAll(p => Len(Cross(new[] { p[0] - r[0], p[1] - r[1], p[2] - r[2] }, d)) < tol);
                    case "circle":
                        if (curve == null || curve.Type != "ellipse-curve") return false;
                        double[] c = (double[])curve.Fields[3], n = (double[])curve.Fields[4];
                        return dist(c, Words(w, 1)) < tol && Len(Cross(Unit(n), Words(w, 5))) < tol;
                    case "point":
                        if (direction) return curve != null && curve.Type == "straight-curve" && pts.Count >= 2 && Len(Cross(Unit(new[] { pts[1][0] - pts[0][0], pts[1][1] - pts[0][1], pts[1][2] - pts[0][2] }), Words(w, 1))) < tol;
                        return curve != null && curve.Type == "ellipse-curve" && dist((double[])curve.Fields[3], Words(w, 1)) < tol;   // a circle's centre, inferred
                }
            }
            return false;
        }

        static double[] RadialOf(double[] axis)
        {
            double[] other = Math.Abs(axis[0]) < 0.9 ? new[] { 1.0, 0, 0 } : new[] { 0, 1.0, 0 };
            return Unit(Cross(axis, other));
        }

        // A B-rep entity against the truth's text for it, in the part's coordinates: "face kPlaneSurface edges 4 point x y z",
        // "edge x y z x y z", "vertex x y z".
        public static bool SameAsTruth(Match m, string truth)
        {
            const double tol = 2e-5;
            string[] w = truth.Split(' ');
            Func<double[], double[], double> dist = (p, q) => Len(new[] { p[0] - q[0], p[1] - q[1], p[2] - q[2] });
            // A point on an edge: the API can call that the edge, where the name gives its end.
            if (w[0] == "edge" && m.Entity.Type == "vertex")
            {
                double[] at = Sab.VertexPosition(m.Records, m.Entity);
                return at != null && (dist(at, Words(w, 1)) < tol || dist(at, Words(w, 4)) < tol);
            }
            if (w[0] != m.Entity.Type) return false;
            if (w[0] == "vertex") { double[] at = Sab.VertexPosition(m.Records, m.Entity); return at != null && dist(at, Words(w, 1)) < tol; }
            if (w[0] == "edge")
            {
                List<double[]> pts = EdgePoints(m.Records, m.Entity);
                if (pts.Count < 2) return false;
                double[] a = Words(w, 1), b = Words(w, 4);
                return (dist(pts[0], a) < tol && dist(pts[1], b) < tol) || (dist(pts[0], b) < tol && dist(pts[1], a) < tol);
            }
            Shape s = Surface(m.Records, m.Entity);
            string kind = s == null ? "other" : "k" + char.ToUpperInvariant(s.Kind[0]) + s.Kind.Substring(1) + "Surface";
            if (s != null && kind != w[1]) return false;
            if (FaceEdges(m.Records, m.Entity).Count.ToString() != w[3]) return false;
            if (s != null && !OnSurface(s, Words(w, 5))) return false;
            // On a plane the API's point must also be within the face's extent: faces on one plane can have the same name.
            if (s == null || s.Kind != "plane") return true;
            double[] low = { double.MaxValue, double.MaxValue, double.MaxValue }, high = { double.MinValue, double.MinValue, double.MinValue }, onFace = Words(w, 5);
            foreach (int e in FaceEdges(m.Records, m.Entity))
            {
                foreach (double[] corner in EdgePoints(m.Records, m.Records[e]))
                    for (int k = 0; k < 3; k++) { low[k] = Math.Min(low[k], corner[k]); high[k] = Math.Max(high[k], corner[k]); }
                // A circle reaches further than its points: centre ± r √(1 - n²) along each axis.
                SabRef cr = m.Records[e].Ref(8);
                SabRecord curve = cr != null && cr.Target >= 0 && cr.Target < m.Records.Count ? m.Records[cr.Target] : null;
                if (curve != null && curve.Type == "ellipse-curve" && curve.Fields.Count > 5 && curve.Fields[5] is double[])
                {
                    double[] c = (double[])curve.Fields[3], n = Unit((double[])curve.Fields[4]);
                    double r = Len((double[])curve.Fields[5]);
                    for (int k = 0; k < 3; k++)
                    {
                        double reach = r * Math.Sqrt(Math.Max(0, 1 - n[k] * n[k]));
                        low[k] = Math.Min(low[k], c[k] - reach);
                        high[k] = Math.Max(high[k], c[k] + reach);
                    }
                }
            }
            for (int k = 0; k < 3; k++) if (low[k] != double.MaxValue && (onFace[k] < low[k] - tol || onFace[k] > high[k] + tol)) return false;
            return true;
        }

        static string Key(Generator g) { return g.Kind + " " + string.Join("+", g.Tags.ConvertAll(x => x.ToString()).ToArray()) + " " + g.Item + " " + g.Flags; }

        // A face entry: the B-rep faces it names, and its plane (or cylinder) and what it is, where they can be told.
        static List<Match> FaceMatches(Context c, Node entry, out Shape surface, out string what)
        {
            List<Match> list = new List<Match>();
            surface = null;
            what = entry == null ? "?" : entry.TypeName;
            if (entry == null || entry.Type == null) return list;
            if (IsFaceEntry(entry))
            {
                Generator g = EntryGenerator(entry);
                if (g == null) return list;
                List<Match> found;
                if (c.Generated.TryGetValue(Key(g), out found)) list.AddRange(found);
                Rebuilt rb = Rebuild(c.Doc, g);
                if (rb != null) { what = rb.Text; if (rb.Shape != null && rb.Shape.Kind != "line" && rb.Shape.Kind != "circle" && rb.Shape.Kind != "point") surface = rb.Shape; }
            }
            else if (entry.Type.Key == 0x8E5D4198)
            {
                string key = BlendKey(entry);
                List<Match> found;
                if (key != null && c.Blends.TryGetValue(key, out found)) list.AddRange(found);
                Features.Info fi;
                int tag;
                what = key != null && int.TryParse(key.Split(' ')[0].Split('+')[0], out tag) && c.Doc.ByTag.TryGetValue(tag, out fi) ? fi.Name + " face" : "a fillet face";
            }
            else if (entry.Type.Key == 0x357D669C)
            {
                string key = LoftKey(entry);
                List<Match> found;
                if (key != null && c.Lofts.TryGetValue(key, out found)) list.AddRange(found);
                what = "a loft face (" + key + ")";
            }
            else if (IsTaggedFace(entry))
            {
                uint type = entry.Type.Key;
                string key = type == HoleEntry ? HoleKey(entry) : type == FlangeEntry ? FlangeKey(entry) : type == ShellEntry ? ShellKey(entry) : BendKey(entry);
                List<Match> found;
                if (key != null && (type == HoleEntry ? c.Holes : type == FlangeEntry ? c.Flanges : type == ShellEntry ? c.Shells : c.Bends).TryGetValue(key, out found)) list.AddRange(found);
                what = "a tagged face";
            }
            // From the B-rep: the current state first (earlier states can predate an edit that moved the face), and only
            // when every face of that name lies on the same surface (a name can cover faces a later feature split).
            if (surface == null) surface = OneSurface(list.FindAll(m => !m.Entity.Old)) ?? OneSurface(list);
            return list;
        }

        static Shape OneSurface(List<Match> faces)
        {
            Shape one = null;
            string key = null;
            foreach (Match m in faces)
            {
                Shape s = Surface(m.Records, m.Entity);
                if (s == null) return null;
                string k = s.Kind == "plane" ? Plane(s.B, s.A) : s.Kind == "cylinder" ? "cylinder " + Dir3(Unit(s.B)) + " " + Mm3(Plus(s.A, Unit(s.B), -Dot(s.A, Unit(s.B)))) + " " + Math.Round(s.R * 10, 4)
                    : s.Canonical();
                if (key != null && k != key) return null;
                key = k;
                one = s;
            }
            return one;
        }

        // A vertex entry's position: the B-rep vertex with its sweep name, or rebuilt from the sketch. A vertex named
        // by three faces (05C619B6: u8 1, u32 3, then {xref table, u32 entry} for each face) is the B-rep vertex the
        // three share, or where their three planes meet.
        static double[] VertexAt(Context c, Node entry)
        {
            if (entry != null && entry.Type != null && entry.Type.Key == 0x05C619B6)
            {
                List<int> refs = NamingFaces(c.Dc, entry);
                if (refs.Count != 3) return null;
                List<List<Match>> faces = new List<List<Match>>();
                List<Shape> planes = new List<Shape>();
                foreach (int r in refs)
                {
                    Shape s;
                    string w;
                    faces.Add(FaceMatches(c, c.Entry(r), out s, out w));
                    planes.Add(s);
                }
                // Face names can cover several B-rep faces, so every vertex the three share is collected; more than one
                // place is ambiguous.
                List<double[]> at = new List<double[]>();
                foreach (Match a in faces[0])
                {
                    HashSet<int> common = FaceVertices(a.Records, a.Entity);
                    foreach (List<Match> other in faces.GetRange(1, 2))
                    {
                        HashSet<int> any = new HashSet<int>();
                        foreach (Match b in other) if (b.Records == a.Records) any.UnionWith(FaceVertices(b.Records, b.Entity));
                        common.IntersectWith(any);
                    }
                    foreach (int v in common)
                    {
                        double[] p = Sab.VertexPosition(a.Records, a.Records[v]);
                        if (p != null && !at.Exists(q => Len(new[] { p[0] - q[0], p[1] - q[1], p[2] - q[2] }) < 1e-6)) at.Add(p);
                    }
                }
                if (at.Count > 0) return at.Count == 1 ? at[0] : null;
                return planes.TrueForAll(s => s != null && s.Kind == "plane") ? ThreePlanes(planes[0], planes[1], planes[2]) : null;
            }
            Generator g = EntryGenerator(entry);
            if (g == null || g.Kind != "vertex") return null;
            List<Match> found;
            if (c.Generated.TryGetValue(Key(g), out found))
                foreach (Match m in found) { double[] p = Sab.VertexPosition(m.Records, m.Entity); if (p != null) return p; }
            Rebuilt rb = Rebuild(c.Doc, g);
            return rb != null && rb.Shape != null && rb.Shape.Kind == "point" ? rb.Shape.A : null;
        }

        // The face entries an edge or vertex entry is named by. BF32E0A6: two vertex refs, u8 1, a tag path (often
        // empty) that, when present, is followed by -1, u32 k and k refs to the face the edge lies on, u8; then u32 n
        // and the n faces. So its faces are the last two refs. 9BB4281C and F4360D18: refs 2 and 3 (their own name's
        // face ref comes after). 05C619B6 (a vertex): its last three refs.
        static List<int> NamingFaces(NodeTable dc, Node entry)
        {
            List<int> refs = EntryRefs(dc, entry);
            if (entry == null || entry.Type == null) return new List<int>();
            if (entry.Type.Key == 0x05C619B6) return refs.Count >= 3 ? refs.GetRange(refs.Count - 3, 3) : new List<int>();
            if (entry.Type.Key == 0xBF32E0A6) return refs.Count >= 4 ? refs.GetRange(refs.Count - 2, 2) : new List<int>();
            return refs.Count >= 4 ? refs.GetRange(2, 2) : new List<int>();
        }

        // Why an entry can name nothing: what it is built from is gone. A sweep name whose sketch entity was deleted;
        // an edge or vertex named by faces, one of which is dead; a fillet face whose rounded edge is dead. Returns
        // what is dead, or null.
        static string DeadEntry(Context c, Node entry, int depth)
        {
            if (entry == null || entry.Type == null || depth > 8) return null;
            Generator g = EntryGenerator(entry);
            if (g != null)
            {
                Rebuilt rb = Rebuild(c.Doc, g);
                return rb != null && rb.Dead ? rb.Text : null;
            }
            // A name built on an entry the naming table no longer has can't be evaluated either.
            if (IsEdgeEntry(entry) || entry.Type.Key == 0x05C619B6)
            {
                foreach (int f in NamingFaces(c.Dc, entry))
                {
                    if (c.Entry(f) == null) return "naming entry " + f + ", which the file no longer has";
                    string why = DeadEntry(c, c.Entry(f), depth + 1);
                    if (why != null) return why;
                }
                return null;
            }
            if (entry.Type.Key == 0x8E5D4198)
            {
                string key = BlendKey(entry);
                int edge;
                if (key != null && int.TryParse(key.Substring(key.LastIndexOf(' ') + 1), out edge))
                    return c.Entry(edge) == null ? "a fillet face of naming entry " + edge + ", which the file no longer has" : DeadEntry(c, c.Entry(edge), depth + 1);
            }
            return null;
        }

        // Can a converter that rebuilds the features in order find what this entry names? A face is findable when the
        // B-rep has it, its surface is known, or it is a fillet face whose rounded edge is findable (rebuilding that
        // fillet makes it); an edge when both its faces are. Returns null if so, else what is missing.
        static string Unfindable(Context c, int index, int depth)
        {
            Node entry = c.Entry(index);
            if (entry == null || entry.Type == null) return "a missing entry";
            if (depth > 8) return "a chain too deep";
            // Any input naming this entry that resolved (matched in the B-rep, or rebuilt) makes it findable.
            if (c.Found != null && c.Found.Contains(index)) return null;
            if (IsFaceEntry(entry) || entry.Type.Key == 0x357D669C || entry.Type.Key == 0x8E5D4198)
            {
                Shape s;
                string w;
                if (FaceMatches(c, entry, out s, out w).Count > 0 || s != null) return null;
                if (entry.Type.Key == 0x8E5D4198)
                {
                    string key = BlendKey(entry);
                    int edge;
                    if (key != null && int.TryParse(key.Substring(key.LastIndexOf(' ') + 1), out edge))
                    {
                        Generator g = EntryGenerator(c.Entry(edge));
                        if (g != null && (c.Generated.ContainsKey(Key(g)) || ((Func<Rebuilt, bool>)(rb => rb != null && rb.Shape != null))(Rebuild(c.Doc, g)))) return null;
                        string inner = Unfindable(c, edge, depth + 1);
                        return inner == null ? null : "fillet face of an edge with " + inner;
                    }
                }
                return w;
            }
            if (IsEdgeEntry(entry))
            {
                List<int> faces = NamingFaces(c.Dc, entry);
                foreach (int f in faces) { string why = Unfindable(c, f, depth + 1); if (why != null) return why; }
                return faces.Count == 2 ? null : "an edge without two faces";
            }
            return entry.TypeName;
        }

        // An edge from its entry's two faces: the B-rep edge they share, and where their surfaces meet (bounded by
        // the entry's two vertices when both are known).
        static void EdgeFromFaces(Context c, Resolved item, Node edgeEntry)
        {
            List<int> refs = EntryRefs(c.Dc, edgeEntry), faces = NamingFaces(c.Dc, edgeEntry);
            if (refs.Count < 4 || faces.Count != 2) return;
            Shape s1, s2;
            string w1, w2;
            List<Match> f1 = FaceMatches(c, c.Entry(faces[0]), out s1, out w1), f2 = FaceMatches(c, c.Entry(faces[1]), out s2, out w2);
            item.FacesKnown = (s1 != null ? 1 : 0) + (s2 != null ? 1 : 0);
            Node e1 = c.Entry(faces[0]), e2 = c.Entry(faces[1]);
            item.FaceKinds = (e1 == null ? "missing" : e1.TypeName) + "+" + (e2 == null ? "missing" : e2.TypeName);
            foreach (Match a in f1)
                foreach (Match b in f2)
                {
                    if (a.Records != b.Records) continue;
                    HashSet<int> shared = FaceEdges(a.Records, a.Entity);
                    shared.IntersectWith(FaceEdges(b.Records, b.Entity));
                    foreach (int e in shared)
                        if (!item.CommonEdges.Exists(m => m.Records == a.Records && m.Entity.Index == e))
                            item.CommonEdges.Add(new Match { Records = a.Records, Entity = a.Records[e] });
                }
            double[] v1 = VertexAt(c, c.Entry(refs[0])), v2 = VertexAt(c, c.Entry(refs[1]));
            // A face name can match several B-rep faces (a face split by later features keeps its name), so the shared
            // edges are kept only where they end at the edge's own vertices, when those are known.
            item.AtVertices = v1 != null && v2 != null && Len(new[] { v1[0] - v2[0], v1[1] - v2[1], v1[2] - v2[2] }) > 1e-6;
            if (v1 != null && v2 != null)
                item.CommonEdges = item.CommonEdges.FindAll(m => {
                    if (m.Entity.Type != "edge") return false;
                    double[] a = Sab.VertexPosition(m.Records, m.Records[m.Entity.Ref(3).Target]), b = Sab.VertexPosition(m.Records, m.Records[m.Entity.Ref(5).Target]);
                    Func<double[], double[], bool> same = (x, y) => x != null && y != null && Len(new[] { x[0] - y[0], x[1] - y[1], x[2] - y[2] }) < 1e-5;
                    return (same(a, v1) && same(b, v2)) || (same(a, v2) && same(b, v1));
                });
            item.Surface1 = s1;
            item.Surface2 = s2;
            Shape line = Meet(s1, s2, v1 ?? v2);
            if (s1 != null && s2 != null) item.MeetKinds = string.CompareOrdinal(s1.Kind, s2.Kind) <= 0 ? s1.Kind + "+" + s2.Kind : s2.Kind + "+" + s1.Kind;
            // Without a line to check them against, the shared edges are kept apart: unconfirmed.
            if (line == null) { item.Unconfirmed = item.CommonEdges; item.CommonEdges = new List<Match>(); return; }
            item.CommonEdges = item.CommonEdges.FindAll(m => Agrees(line, m.Records, m.Entity));
            // Shared edges in more than one place, or a face name covering several current faces without the edge's
            // vertices to tell them apart: which edge is meant is unknown.
            HashSet<string> places = new HashSet<string>();
            foreach (Match m in item.CommonEdges) places.Add(Canonical(m.Records, m.Entity) ?? Sab.Where(m.Records, m.Entity));
            Func<List<Match>, bool> several = fs => fs.FindAll(m => !m.Entity.Old).Count > 1;
            if (places.Count > 1 || ((several(f1) || several(f2)) && (v1 == null || v2 == null)))
            { item.Unconfirmed = item.CommonEdges; item.CommonEdges = new List<Match>(); }
            double t1, t2;
            if (line.Kind == "line" && v1 != null && v2 != null && Len(new[] { v1[0] - v2[0], v1[1] - v2[1], v1[2] - v2[2] }) > 1e-6
                && OnLine(v1, line.A, line.B, 1e-5, out t1) && OnLine(v2, line.A, line.B, 1e-5, out t2))
                line = new Shape { Kind = "line", A = v1, B = v2 };
            item.FromFaces = new Rebuilt { Shape = line, Text = "where " + w1 + " meets " + w2 };
        }

        // What a naming table entry says made the entity: the features' tags, the profile item, and flags.
        // Entries for a vertex (2E04A208), face (0645C2A5) and edge (9BB4281C) generated by a sweep (extrude,
        // revolve) hold a kind-2 list of feature tags, then -1, then (after a reference and padding) the item and
        // flags, as the B-rep's INV_NMX_SWEEPGENERATED_TAG {1, 44, n, tags..., -1, item, 0, flags} does:
        //   vertex: -1, u32, item, flags   face: -1, xref, u32, item, flags
        //   edge: its last list, -1, xref table, u32 entry (the face it lies on), u32, item, flags.
        // An edge entry first refers to its two vertex entries and its two face entries.
        // Flags seen: 9 / 10 / 12 vertex / edge / face on the end cap, + 0x1000 on the start cap; 18 / 20 edge /
        // face along the sweep. Items number the profile's points and curves.
        public class Generator
        {
            public string Kind;
            public List<int> Tags = new List<int>();
            public int Item, Flags;
            public override string ToString() { return Kind + " of feature " + string.Join("+", Tags.ConvertAll(x => x.ToString()).ToArray()) + " item " + Item + " flags " + Flags; }
        }

        // 90F4820A, 56A95F20 and F4360D18 are laid out as the vertex, face and edge entries (an edge again lists its
        // vertices and faces first); they name what revolves and older parts' features made (GEN_TAG in the B-rep).
        static readonly Dictionary<uint, string> SweepEntries = new Dictionary<uint, string> {
            { 0x2E04A208, "vertex" }, { 0x0645C2A5, "face" }, { 0x9BB4281C, "edge" },
            { 0x90F4820A, "vertex" }, { 0x56A95F20, "face" }, { 0xF4360D18, "edge" } };

        static bool IsFaceEntry(Node n) { string k; return n != null && n.Type != null && SweepEntries.TryGetValue(n.Type.Key, out k) && k == "face"; }

        // Edge entries that list two vertex entries, then two face entries.
        static bool IsEdgeEntry(Node n) { return n != null && n.Type != null && (n.Type.Key == 0xBF32E0A6 || n.Type.Key == 0x9BB4281C || n.Type.Key == 0xF4360D18 || n.Type.Key == 0xFF46726C || n.Type.Key == 0x22178C64); }

        // Loft faces (357D669C entries; B-rep NMx_Loft_Surface_Brep_tag): the loft's tag path, -1, then the section
        // curves the face spans, each as {section tag, curve item, 258, 0, 0} in the entry (u32 0, then a kind-2 list
        // of 5-value elements) and {item, section tag, 258, 0, 0} in the B-rep (a count, then the elements).
        // Keyed "path section:item ..." (sorted).
        static string LoftKey(Node entry)
        {
            if (entry == null || entry.Data == null || entry.Type == null || entry.Type.Key != 0x357D669C) return null;
            byte[] d = entry.Data;
            for (int p = 0x22 + Sketches.Shift(d); p + 16 <= d.Length; p++)
            {
                if (U32(d, p) != 0x30000002 || U32(d, p + 4) == 0 || U32(d, p + 4) > 8) continue;
                int count = 2 * (int)U32(d, p + 4) - 1, p2 = p + 16 + 4 * count, l = p2 + 8;
                if (l + 16 > d.Length || U32(d, l) != 0x30000002) return null;
                List<string> tags = new List<string>(), curves = new List<string>();
                for (int i = 0; i < count; i++) tags.Add(U32(d, p + 16 + 4 * i).ToString());
                int k = (int)U32(d, l + 4);
                for (int i = 0; i < k && l + 16 + 20 * i + 8 <= d.Length; i++) curves.Add(U32(d, l + 16 + 20 * i) + ":" + U32(d, l + 20 + 20 * i));
                curves.Sort(string.CompareOrdinal);
                return string.Join("+", tags.ToArray()) + " " + string.Join(" ", curves.ToArray());
            }
            return null;
        }

        // The sketch curve items a sweep feature (by tag) named current-state B-rep entities after: faces along the sweep
        // (20) and cap edges (10, older start cap 2, newer 0x1000 + 10).
        public static HashSet<int> SweptCurves(byte[] brep, int tag)
        {
            HashSet<int> items = new HashSet<int>();
            foreach (KeyValuePair<string, List<Match>> kv in Generated(brep))
            {
                string[] p = kv.Key.Split(' ');
                if (p.Length != 4 || p[1] != tag.ToString() || !kv.Value.Exists(m => !m.Entity.Old)) continue;
                int flags = int.Parse(p[3]) & 0xFFF, item = int.Parse(p[2]);
                if ((flags == 20 || flags == 10 || flags == 2) && item > 0) items.Add(item);
            }
            return items;
        }

        // Does the B-rep's current state hold a face a fillet or chamfer with this tag made (BLEND / EDGE_BLEND names)?
        public static bool MadeFaces(byte[] brep, int tag)
        {
            int at = 0;
            while ((at = Sab.Find(brep, at)) >= 0)
            {
                string error;
                List<SabRecord> records = Sab.Read(brep, at, out error);
                foreach (SabRecord a in records)
                {
                    string name;
                    List<object> data;
                    if (a.Old || !NamingAttr(a, out name, out data) || data.Count < 4) continue;
                    if (name != "INV_NMX_BLEND_TAG" && name != "INV_NMX_EDGE_BLEND_TAG" && name != "NMx_edge_blend_tag") continue;
                    int i = 1 + Di(data, 0), m = Di(data, i);
                    if (m > 0 && m < 8 && Di(data, i + 2 * m - 1) == tag) return true;
                }
                at++;
            }
            return false;
        }

        static Dictionary<string, List<Match>> Lofts(byte[] brep)
        {
            Dictionary<string, List<Match>> r = new Dictionary<string, List<Match>>();
            int at = 0;
            while ((at = Sab.Find(brep, at)) >= 0)
            {
                string error;
                List<SabRecord> records = Sab.Read(brep, at, out error);
                foreach (SabRecord a in records)
                {
                    string name;
                    List<object> data;
                    if (!NamingAttr(a, out name, out data) || (name != "NMx_Loft_Surface_Brep_tag" && name != "INV_NMX_ORDERED_GEN_TAG") || data.Count < 6) continue;
                    SabRecord owner = Owner(records, a);
                    if (owner == null || owner.Type != "face") continue;
                    int i = 1 + Di(data, 0), m = Di(data, i);
                    if (m <= 0 || m > 8) continue;
                    m = 2 * m - 1;
                    List<string> tags = new List<string>(), curves = new List<string>();
                    for (int k = 0; k < m; k++) tags.Add(Di(data, i + 1 + k).ToString());
                    int c = i + 1 + m + 1, n = Di(data, c);
                    for (int k = 0; k < n && c + 1 + 5 * k + 1 < data.Count; k++) curves.Add(Di(data, c + 2 + 5 * k) + ":" + Di(data, c + 1 + 5 * k));
                    curves.Sort(string.CompareOrdinal);
                    string key = string.Join("+", tags.ToArray()) + " " + string.Join(" ", curves.ToArray());
                    if (!r.ContainsKey(key)) r[key] = new List<Match>();
                    r[key].Add(new Match { Records = records, Entity = owner });
                }
                at++;
            }
            return r;
        }

        // The other entries an entry refers to, in order ({xref naming table, u32 entry} pairs). An edge entry
        // (9BB4281C, BF32E0A6) lists its two vertices, then its two faces.
        public static List<int> EntryRefs(NodeTable t, Node entry)
        {
            List<int> r = new List<int>();
            if (entry == null || entry.Data == null) return r;
            byte[] d = entry.Data;
            for (int p = 0x1A + Sketches.Shift(d); p + 8 <= d.Length; p++)
            {
                Node x = t.At(Sketches.XRef(d, p));
                if (x == null || x.Type == null || x.Type.Key != Table) continue;
                r.Add((int)U32(d, p + 4));
                p += 7;
            }
            return r;
        }

        public static Generator EntryGenerator(Node entry)
        {
            string kind;
            if (entry == null || entry.Data == null || entry.Type == null || !SweepEntries.TryGetValue(entry.Type.Key, out kind)) return null;
            byte[] d = entry.Data;
            int list = -1;
            for (int p = 0x22 + Sketches.Shift(d); p + 16 <= d.Length; p++)
                if (U32(d, p) == 0x30000002 && U32(d, p + 4) > 0 && U32(d, p + 4) < 16) { list = p; if (kind != "edge") break; }
            if (list < 0) return null;
            Generator g = new Generator { Kind = kind };
            // A tag path: count levels as 2 * count - 1 values, {tag, instance}... then the feature's tag; e.g. [463, 2,
            // 414] is instance 2 of feature 463 (a mirror) of what feature 414 made.
            int count = 2 * (int)U32(d, list + 4) - 1;
            if (list + 16 + 4 * count + 4 > d.Length) return null;
            for (int i = 0; i < count; i++) g.Tags.Add((int)U32(d, list + 16 + 4 * i));
            int p2 = list + 16 + 4 * count;   // the -1
            int item = kind == "vertex" ? p2 + 8 : kind == "face" ? p2 + 12 : p2 + 16;
            if (item + 8 > d.Length) return null;
            g.Item = (int)U32(d, item);
            g.Flags = (int)U32(d, item + 4);
            return g;
        }

        // Hole faces and flange faces, by their B-rep tags. Entry AE0E267A: a tag path, -1, u32 0, u32 item, u8 part of the hole;
        // B-rep HOLE_TAG {1, 44, n, path, -1, item, part}. Entry 6F891B34: a tag path, -1, u32 0, u8 a, u8 b, u8 c, u32 d, ...;
        // B-rep FLANGE_TAG {1, 44, n, path, -1, a, b, c, d, ...}. A hole's edges (FF46726C) are named by their two faces.
        // Entry 31D7A200 (a shell's inner face): a tag path, -1, u32 0, u32 item, f64 thickness, u8, u32; B-rep SHELL_TAG {..., -1, item,
        // thickness, a, b}. Entry 07BA7419 (a bend's): a tag path, -1, u32 0, u32 a, u8 b, u8 c, u8 d; B-rep BEND_TAG {..., -1, a, b, c, d, ...}.
        // Their edges (22178C64 on a loft too) are named by their two faces, as other edges are.
        const uint HoleEntry = 0xAE0E267A, FlangeEntry = 0x6F891B34, ShellEntry = 0x31D7A200, BendEntry = 0x07BA7419;

        static bool IsTaggedFace(Node n) { return n != null && n.Type != null && (n.Type.Key == HoleEntry || n.Type.Key == FlangeEntry || n.Type.Key == ShellEntry || n.Type.Key == BendEntry); }

        static string ShellKey(Node entry)
        {
            int m;
            string path = EntryPath(entry, out m);
            if (path == null || m + 25 > entry.Data.Length) return null;
            byte[] d = entry.Data;
            return path + " " + U32(d, m + 8) + " " + int.MinValue + " " + d[m + 20] + " " + U32(d, m + 21);
        }

        static string BendKey(Node entry)
        {
            int m;
            string path = EntryPath(entry, out m);
            if (path == null || m + 15 > entry.Data.Length) return null;
            byte[] d = entry.Data;
            return path + " " + U32(d, m + 8) + " " + d[m + 12] + " " + d[m + 13] + " " + d[m + 14];
        }

        // The tag path after an entry's first kind-2 list past the occurrence path, and where the -1 after it is.
        static string EntryPath(Node entry, out int minusOne)
        {
            minusOne = -1;
            if (entry == null || entry.Data == null) return null;
            byte[] d = entry.Data;
            for (int p = 0x22 + Sketches.Shift(d); p + 16 <= d.Length; p++)
            {
                if (U32(d, p) != 0x30000002 || U32(d, p + 4) == 0 || U32(d, p + 4) > 8) continue;
                int count = 2 * (int)U32(d, p + 4) - 1, end = p + 16 + 4 * count;
                if (end + 4 > d.Length) return null;
                List<string> tags = new List<string>();
                for (int i = 0; i < count; i++) tags.Add(U32(d, p + 16 + 4 * i).ToString());
                minusOne = end;
                return string.Join("+", tags.ToArray());
            }
            return null;
        }

        static string HoleKey(Node entry)
        {
            int m;
            string path = EntryPath(entry, out m);
            if (path == null || m + 13 > entry.Data.Length) return null;
            return path + " " + U32(entry.Data, m + 8) + " " + entry.Data[m + 12];
        }

        static string FlangeKey(Node entry)
        {
            int m;
            string path = EntryPath(entry, out m);
            if (path == null || m + 15 > entry.Data.Length) return null;
            byte[] d = entry.Data;
            return path + " " + d[m + 8] + " " + d[m + 9] + " " + d[m + 10] + " " + U32(d, m + 11);
        }

        // Faces by a B-rep tag whose name has this word, keyed "path v1 v2 ..." with this many values after the -1.
        static Dictionary<string, List<Match>> TaggedFaces(byte[] brep, string word, int values)
        {
            Dictionary<string, List<Match>> r = new Dictionary<string, List<Match>>();
            int at = 0;
            while ((at = Sab.Find(brep, at)) >= 0)
            {
                string error;
                List<SabRecord> records = Sab.Read(brep, at, out error);
                foreach (SabRecord a in records)
                {
                    string name;
                    List<object> data;
                    if (!NamingAttr(a, out name, out data) || name.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0 || data.Count < 4 || Di(data, 0) != 1) continue;
                    SabRecord owner = Owner(records, a);
                    int m = Di(data, 2);
                    if (owner == null || owner.Type != "face" || m <= 0 || m > 8) continue;
                    m = 2 * m - 1;
                    if (data.Count < 3 + m + 1 + values) continue;
                    List<string> key = new List<string>();
                    for (int k = 0; k < m; k++) key.Add(Di(data, 3 + k).ToString());
                    string text = string.Join("+", key.ToArray());
                    for (int k = 0; k < values; k++) text += " " + Di(data, 3 + m + 1 + k);
                    if (!r.ContainsKey(text)) r[text] = new List<Match>();
                    r[text].Add(new Match { Records = records, Entity = owner });
                }
                at++;
            }
            return r;
        }

        // Fillet faces (8E5D4198 entries; B-rep INV_NMX_BLEND_TAG): the fillet's tag path, -1, then the entry index
        // of the edge the face rounded. Entry: path, -1, u32 0, u8 1, u32 1, u32 edge entry. B-rep: {1, 44, n,
        // path, -1, 1, 1, edge entry, ...}. Keyed "path edge-entry".
        static string BlendKey(Node entry)
        {
            if (entry == null || entry.Data == null || entry.Type == null || entry.Type.Key != 0x8E5D4198) return null;
            byte[] d = entry.Data;
            for (int p = 0x22 + Sketches.Shift(d); p + 16 <= d.Length; p++)
            {
                if (U32(d, p) != 0x30000002 || U32(d, p + 4) == 0 || U32(d, p + 4) > 8) continue;
                int count = 2 * (int)U32(d, p + 4) - 1, end = p + 16 + 4 * count;
                if (end + 17 > d.Length) return null;
                List<string> tags = new List<string>();
                for (int i = 0; i < count; i++) tags.Add(U32(d, p + 16 + 4 * i).ToString());
                return string.Join("+", tags.ToArray()) + " " + U32(d, end + 13);
            }
            return null;
        }

        static Dictionary<string, List<Match>> Blends(byte[] brep)
        {
            Dictionary<string, List<Match>> r = new Dictionary<string, List<Match>>();
            int at = 0;
            while ((at = Sab.Find(brep, at)) >= 0)
            {
                string error;
                List<SabRecord> records = Sab.Read(brep, at, out error);
                foreach (SabRecord a in records)
                {
                    string name;
                    List<object> data;
                    if (!NamingAttr(a, out name, out data) || name != "INV_NMX_BLEND_TAG" || data.Count < 8) continue;
                    SabRecord owner = Owner(records, a);
                    if (owner == null || owner.Type != "face" || Di(data, 0) != 1) continue;
                    int m = Di(data, 2);
                    if (m <= 0 || m > 8) continue;
                    m = 2 * m - 1;
                    List<string> tags = new List<string>();
                    for (int k = 0; k < m; k++) tags.Add(Di(data, 3 + k).ToString());
                    string key = string.Join("+", tags.ToArray()) + " " + Di(data, 3 + m + 3);
                    if (!r.ContainsKey(key)) r[key] = new List<Match>();
                    r[key].Add(new Match { Records = records, Entity = owner });
                }
                at++;
            }
            return r;
        }

        // A face's edges: its loops (face field 4, then loop field 3), each loop's coedges (loop field 4, then
        // coedge field 3 until back at the first), each coedge's edge (field 6).
        static HashSet<int> FaceEdges(List<SabRecord> records, SabRecord face)
        {
            HashSet<int> edges = new HashSet<int>();
            Func<SabRef, SabRecord> at = x => x != null && x.Target >= 0 && x.Target < records.Count ? records[x.Target] : null;
            int loops = 0;
            for (SabRecord loop = at(face.Ref(4)); loop != null && loop.Type == "loop" && loops++ < 1000; loop = at(loop.Ref(3)))
            {
                SabRecord first = at(loop.Ref(4)), c = first;
                int n = 0;
                while (c != null && c.Type == "coedge" && n++ < 10000)
                {
                    SabRef e = c.Ref(6);
                    if (e != null && e.Target >= 0) edges.Add(e.Target);
                    c = at(c.Ref(3));
                    if (c == first) break;
                }
            }
            return edges;
        }

        // The faces on either side of an edge: its coedge (edge field 7), and the coedges partnered with it (coedge
        // fields 3 next, 4 previous, 5 partner, 6 edge, 7 sense, 8 loop), each in a loop (field 5 its face).
        public static List<SabRecord> EdgeFaces(List<SabRecord> records, SabRecord edge)
        {
            List<SabRecord> faces = new List<SabRecord>();
            Func<SabRef, SabRecord> at = x => x != null && x.Target >= 0 && x.Target < records.Count ? records[x.Target] : null;
            SabRecord first = at(edge.Ref(7)), c = first;
            for (int n = 0; c != null && c.Type == "coedge" && n < 16; n++)
            {
                SabRecord loop = at(c.Ref(8)), face = loop == null ? null : at(loop.Ref(5));
                if (face != null && face.Type == "face" && !faces.Contains(face)) faces.Add(face);
                c = at(c.Ref(5));
                if (c == first) break;
            }
            return faces;
        }

        // Points on a B-rep edge: its vertices, and three points around a circle.
        static List<double[]> EdgePoints(List<SabRecord> records, SabRecord edge)
        {
            List<double[]> pts = new List<double[]>();
            Func<SabRef, SabRecord> at = x => x != null && x.Target >= 0 && x.Target < records.Count ? records[x.Target] : null;
            foreach (int f in new[] { 3, 5 }) { double[] p = Sab.VertexPosition(records, at(edge.Ref(f))); if (p != null) pts.Add(p); }
            SabRecord curve = at(edge.Ref(8));
            if (curve != null && curve.Type == "ellipse-curve" && curve.Fields.Count > 6 && curve.Fields[5] is double[] && curve.Fields[6] is double && Math.Abs((double)curve.Fields[6] - 1) < 1e-9)
            {
                double[] c = (double[])curve.Fields[3], n = Unit((double[])curve.Fields[4]), u = (double[])curve.Fields[5], v = Cross(n, u);
                for (int k = 0; k < 3; k++)
                {
                    double t = 2 * Math.PI * k / 3, cs = Math.Cos(t), sn = Math.Sin(t);
                    pts.Add(new[] { c[0] + u[0] * cs + v[0] * sn, c[1] + u[1] * cs + v[1] * sn, c[2] + u[2] * cs + v[2] * sn });
                }
            }
            return pts;
        }

        // Is p on the surface (within 1e-5 cm)?
        static bool OnSurface(Shape s, double[] p)
        {
            const double tol = 1e-5;
            double[] w = { p[0] - s.A[0], p[1] - s.A[1], p[2] - s.A[2] };
            if (s.Kind == "sphere") return Math.Abs(Len(w) - s.R) < tol;
            double[] a = Unit(s.B);
            double h = Dot(w, a), rho = Len(Cross(w, a));
            switch (s.Kind)
            {
                case "plane": return Math.Abs(h) < tol;
                case "cylinder": return Math.Abs(rho - s.R) < tol;
                case "cone": return Math.Abs(rho - (s.R + s.K * h)) < tol;
                case "torus": return Math.Abs(Math.Sqrt((rho - s.R) * (rho - s.R) + h * h) - s.R2) < tol;
            }
            return false;
        }

        // Two surfaces the same, within 1e-6 (cm): planes alike; cylinders, cones and tori on one axis with the same
        // radii (a cone's checked at two heights); spheres with one centre and radius.
        public static bool SameSurface(Shape a, Shape b)
        {
            const double tol = 1e-6;
            if (a == null || b == null || a.Kind != b.Kind) return false;
            Func<double[], double[], double> dist = (p, q) => Len(new[] { p[0] - q[0], p[1] - q[1], p[2] - q[2] });
            if (a.Kind == "sphere") return dist(a.A, b.A) < tol && Math.Abs(a.R - b.R) < tol;
            double[] ua = Unit(a.B), ub = Unit(b.B);
            if (Len(Cross(ua, ub)) > tol) return false;
            double[] w = { b.A[0] - a.A[0], b.A[1] - a.A[1], b.A[2] - a.A[2] };
            if (a.Kind == "plane") return Math.Abs(Dot(ua, w)) < tol;
            if (Len(Cross(w, ua)) > tol) return false;   // b's centre off a's axis
            if (a.Kind == "cylinder") return Math.Abs(a.R - b.R) < tol;
            if (a.Kind == "torus") return dist(a.A, b.A) < tol && Math.Abs(a.R - b.R) < tol && Math.Abs(a.R2 - b.R2) < tol;
            if (a.Kind == "cone")
                foreach (double h in new[] { 0.0, 1.0 })
                {
                    double[] p = Plus(a.A, ua, h);
                    double hb = Dot(new[] { p[0] - b.A[0], p[1] - b.A[1], p[2] - b.A[2] }, ub);
                    if (Math.Abs(a.R + a.K * h - (b.R + b.K * hb)) > tol) return false;
                }
            return a.Kind == "cone";
        }

        // A face's vertices (the ends of its edges).
        static HashSet<int> FaceVertices(List<SabRecord> records, SabRecord face)
        {
            HashSet<int> v = new HashSet<int>();
            foreach (int e in FaceEdges(records, face))
                foreach (int f in new[] { 3, 5 })
                {
                    SabRef r = records[e].Ref(f);
                    if (r != null && r.Target >= 0) v.Add(r.Target);
                }
            return v;
        }

        // Where three planes meet (null if two are parallel).
        static double[] ThreePlanes(Shape a, Shape b, Shape c)
        {
            double[] n1 = Unit(a.B), n2 = Unit(b.B), n3 = Unit(c.B);
            double[] x23 = Cross(n2, n3), x31 = Cross(n3, n1), x12 = Cross(n1, n2);
            double det = Dot(n1, x23);
            if (Math.Abs(det) < 1e-9) return null;
            double d1 = Dot(n1, a.A), d2 = Dot(n2, b.A), d3 = Dot(n3, c.A);
            return new[] { (d1 * x23[0] + d2 * x31[0] + d3 * x12[0]) / det, (d1 * x23[1] + d2 * x31[1] + d3 * x12[1]) / det, (d1 * x23[2] + d2 * x31[2] + d3 * x12[2]) / det };
        }

        // The surface of a face as a plane {point, normal} or a cylinder {centre, axis, radius}; null otherwise.
        public static Shape Surface(List<SabRecord> records, SabRecord face)
        {
            SabRef sr = face.Ref(7);
            SabRecord s = sr != null && sr.Target >= 0 && sr.Target < records.Count ? records[sr.Target] : null;
            if (s == null) return null;
            if (s.Type == "plane-surface") return new Shape { Kind = "plane", A = (double[])s.Fields[3], B = (double[])s.Fields[4] };
            if (s.Type == "cone-surface" && s.Fields.Count > 10 && s.Fields[9] is double && s.Fields[10] is double && s.Fields[5] is double[]
                && s.Fields[6] is double && Math.Abs((double)s.Fields[6] - 1) < 1e-9)
            {
                double sine = (double)s.Fields[9], cosine = (double)s.Fields[10];
                Shape c = new Shape { Kind = Math.Abs(sine) < 1e-12 ? "cylinder" : "cone", A = (double[])s.Fields[3], B = (double[])s.Fields[4], R = Len((double[])s.Fields[5]) };
                if (c.Kind == "cone") c.K = sine / cosine;
                return c;
            }
            // torus: centre, axis, major radius, minor radius (negative: the surface faces in, as a concave fillet does).
            if (s.Type == "torus-surface" && s.Fields.Count > 6 && s.Fields[5] is double && s.Fields[6] is double)
                return new Shape { Kind = "torus", A = (double[])s.Fields[3], B = (double[])s.Fields[4], R = (double)s.Fields[5], R2 = Math.Abs((double)s.Fields[6]) };
            if (s.Type == "sphere-surface" && s.Fields.Count > 4 && s.Fields[4] is double)
                return new Shape { Kind = "sphere", A = (double[])s.Fields[3], R = Math.Abs((double)s.Fields[4]) };
            return null;
        }

        // Where two surfaces meet along a line: two planes, or a plane and a cylinder touching it (a fillet's edge on
        // a face it rounds into). An unbounded line: A a point on it, B one unit along.
        static Shape Meet(Shape a, Shape b, double[] hint = null)
        {
            if (a == null || b == null) return null;
            if (!((a.Kind == "plane" || a.Kind == "cylinder") && (b.Kind == "plane" || b.Kind == "cylinder"))) return RevolutionMeet(a, b, hint);
            if (a.Kind == "cylinder" && b.Kind == "plane") { Shape t = a; a = b; b = t; }
            if (a.Kind == "plane" && b.Kind == "plane")
            {
                double[] n1 = Unit(a.B), n2 = Unit(b.B), dir = Cross(n1, n2);
                double len = Len(dir);
                if (len < 1e-9) return null;
                dir = new[] { dir[0] / len, dir[1] / len, dir[2] / len };
                double d1 = Dot(n1, a.A), d2 = Dot(n2, b.A), c = Dot(n1, n2);
                double k1 = (d1 - d2 * c) / (1 - c * c), k2 = (d2 - d1 * c) / (1 - c * c);
                double[] p = { k1 * n1[0] + k2 * n2[0], k1 * n1[1] + k2 * n2[1], k1 * n1[2] + k2 * n2[2] };
                return new Shape { Kind = "line", A = p, B = Plus(p, dir, 1), Unbounded = true };
            }
            if (a.Kind == "plane" && b.Kind == "cylinder")
            {
                double[] n = Unit(a.B), axis = Unit(b.B);
                double dist = Dot(n, new[] { b.A[0] - a.A[0], b.A[1] - a.A[1], b.A[2] - a.A[2] });
                // Square to the axis: the circle where the plane cuts the cylinder (a hole's rim).
                if (Len(Cross(n, axis)) < 1e-9)
                    return new Shape { Kind = "circle", A = Plus(b.A, n, -dist), B = n, R = b.R };
                if (Math.Abs(Dot(n, axis)) > 1e-9) return null;
                if (Math.Abs(Math.Abs(dist) - b.R) > 1e-6) return null;
                double[] p = Plus(b.A, n, -dist);
                return new Shape { Kind = "line", A = p, B = Plus(p, axis, 1), Unbounded = true };
            }
            return null;
        }

        // Surfaces of revolution about one axis (a plane square to it counts) meet in circles about that axis, where
        // their profiles cross. In (h along the axis, rho from it): a plane is h = c, a cylinder rho = R, a cone a
        // sloped line, a torus or sphere a circle. hint, a point the edge passes through, picks among several circles;
        // without one, the answer must be unique. Null when the surfaces don't share an axis.
        static Shape RevolutionMeet(Shape a, Shape b, double[] hint)
        {
            Shape axisOf = a.Kind != "plane" && a.Kind != "sphere" ? a : b.Kind != "plane" && b.Kind != "sphere" ? b : null;
            double[] o, ax;
            if (axisOf != null) { o = axisOf.A; ax = Unit(axisOf.B); }
            else if (a.Kind == "sphere" && b.Kind == "plane") { o = a.A; ax = Unit(b.B); }
            else if (b.Kind == "sphere" && a.Kind == "plane") { o = b.A; ax = Unit(a.B); }
            else return null;
            foreach (Shape s in new[] { a, b })
            {
                if (s.Kind == "plane") { if (Len(Cross(Unit(s.B), ax)) > 1e-7) return null; continue; }
                double[] w = { s.A[0] - o[0], s.A[1] - o[1], s.A[2] - o[2] };
                if (Len(Cross(w, ax)) > 1e-6) return null;   // its centre is off the axis
                if (s.Kind != "sphere" && Len(Cross(Unit(s.B), ax)) > 1e-7) return null;
            }
            List<double[]> points = ProfileCross(Profile(a, o, ax), Profile(b, o, ax));
            List<Shape> circles = points.ConvertAll(p => new Shape { Kind = "circle", A = Plus(o, ax, p[0]), B = ax, R = p[1] });
            if (hint != null)
                circles = circles.FindAll(c => {
                    double[] v = { hint[0] - c.A[0], hint[1] - c.A[1], hint[2] - c.A[2] };
                    return Math.Abs(Dot(v, ax)) < 1e-5 && Math.Abs(Len(v) - c.R) < 1e-5;
                });
            return circles.Count == 1 ? circles[0] : null;
        }

        // A profile in (h, rho): a line {kind 0, point, direction} or a circle {kind 1, centre, radius}.
        static double[] Profile(Shape s, double[] o, double[] ax)
        {
            double h = Dot(new[] { s.A[0] - o[0], s.A[1] - o[1], s.A[2] - o[2] }, ax);
            switch (s.Kind)
            {
                case "plane": return new double[] { 0, h, 0, 0, 1 };
                case "cylinder": return new double[] { 0, h, s.R, 1, 0 };
                case "cone": return new double[] { 0, h, s.R, 1, s.K * (Dot(Unit(s.B), ax) > 0 ? 1 : -1) };
                case "torus": return new double[] { 1, h, s.R, s.R2 };
                case "sphere": return new double[] { 1, h, 0, s.R };
            }
            return null;
        }

        // Where two profiles cross, rho > 0; touching counts once.
        static List<double[]> ProfileCross(double[] p, double[] q)
        {
            List<double[]> r = new List<double[]>();
            if (p == null || q == null) return r;
            if (p[0] == 1 && q[0] == 0) { double[] t = p; p = q; q = t; }
            const double tol = 1e-7;
            if (p[0] == 0 && q[0] == 0)
            {
                double det = p[3] * -q[4] - p[4] * -q[3];
                if (Math.Abs(det) < 1e-12) return r;
                double dx = q[1] - p[1], dy = q[2] - p[2], t = (dx * -q[4] - dy * -q[3]) / det;
                r.Add(new[] { p[1] + t * p[3], p[2] + t * p[4] });
            }
            else if (p[0] == 0 && q[0] == 1)
            {
                double l = Math.Sqrt(p[3] * p[3] + p[4] * p[4]), ux = p[3] / l, uy = p[4] / l;
                double fx = q[1] - p[1], fy = q[2] - p[2], along = fx * ux + fy * uy;
                double[] foot = { p[1] + along * ux, p[2] + along * uy };
                double d = Math.Sqrt(Math.Pow(q[1] - foot[0], 2) + Math.Pow(q[2] - foot[1], 2));
                if (d > q[3] + tol) return r;
                if (Math.Abs(d - q[3]) <= tol) r.Add(foot);
                else { double k = Math.Sqrt(q[3] * q[3] - d * d); r.Add(new[] { foot[0] + k * ux, foot[1] + k * uy }); r.Add(new[] { foot[0] - k * ux, foot[1] - k * uy }); }
            }
            else
            {
                double dx = q[1] - p[1], dy = q[2] - p[2], d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 1e-12 || d > p[3] + q[3] + tol || d < Math.Abs(p[3] - q[3]) - tol) return r;
                double a = (p[3] * p[3] - q[3] * q[3] + d * d) / (2 * d), hh = Math.Sqrt(Math.Max(0, p[3] * p[3] - a * a));
                double mx = p[1] + a * dx / d, my = p[2] + a * dy / d;
                if (hh <= tol) r.Add(new[] { mx, my });
                else { r.Add(new[] { mx + hh * dy / d, my - hh * dx / d }); r.Add(new[] { mx - hh * dy / d, my + hh * dx / d }); }
            }
            return r.FindAll(x => x[1] > 1e-9);
        }

        static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

        // B-rep entities by their sweep generator name, keyed "kind tags item flags".
        static Dictionary<string, List<Match>> Generated(byte[] brep)
        {
            Dictionary<string, List<Match>> r = new Dictionary<string, List<Match>>();
            int at = 0;
            while ((at = Sab.Find(brep, at)) >= 0)
            {
                string error;
                List<SabRecord> records = Sab.Read(brep, at, out error);
                foreach (SabRecord a in records)
                {
                    string name;
                    List<object> data;
                    // Revolves (and older parts' other features) name what they made with GEN_TAG, laid out the same.
                    if (!NamingAttr(a, out name, out data) || (name != "INV_NMX_SWEEPGENERATED_TAG" && name != "INV_NMX_GEN_TAG") || data.Count < 6) continue;
                    SabRecord owner = Owner(records, a);
                    if (owner == null) continue;
                    int i = 0, n = Di(data, i);
                    if (n < 0 || n > 8) continue;
                    i += 1 + n;
                    int m = Di(data, i);
                    if (m <= 0 || m > 8) continue;
                    m = 2 * m - 1;   // a tag path, as in the naming entries
                    List<string> tags = new List<string>();
                    for (int k = 0; k < m; k++) tags.Add(Di(data, i + 1 + k).ToString());
                    i += 1 + m;   // the -1
                    string key = owner.Type + " " + string.Join("+", tags.ToArray()) + " " + Di(data, i + 1) + " " + Di(data, i + 3);
                    if (!r.ContainsKey(key)) r[key] = new List<Match>();
                    r[key].Add(new Match { Records = records, Entity = owner });
                }
                at++;
            }
            return r;
        }

        static uint U32(byte[] d, int p) { return BitConverter.ToUInt32(d, p); }

        // Geometry in one comparable form, from cm to mm, used both for the decoded B-rep and for what the
        // generator picked through the API: "line (a) (b)" (ends sorted), "circle (centre) r",
        // "plane (unit normal, first non-zero component positive) distance from the origin".
        static string Dir3(double[] v) { return "(" + string.Join(", ", Array.ConvertAll(v, x => (Math.Round(x, 4) + 0.0).ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")"; }

        static string Mm3(double[] v) { return "(" + string.Join(", ", Array.ConvertAll(v, x => (Math.Round(x * 10, 3) + 0.0).ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")"; }

        public static string Line(double[] a, double[] b)
        {
            string x = Mm3(a), y = Mm3(b);
            return string.CompareOrdinal(x, y) <= 0 ? "line " + x + " " + y : "line " + y + " " + x;
        }

        public static string Circle(double[] c, double r) { return "circle " + Mm3(c) + " " + (Math.Round(r * 10, 3) + 0.0).ToString(System.Globalization.CultureInfo.InvariantCulture); }

        public static string Plane(double[] n, double[] p)
        {
            double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            double[] u = { n[0] / len, n[1] / len, n[2] / len };
            int first = Math.Abs(u[0]) > 1e-9 ? 0 : Math.Abs(u[1]) > 1e-9 ? 1 : 2;
            if (u[first] < 0) for (int i = 0; i < 3; i++) u[i] = -u[i];
            double d = u[0] * p[0] + u[1] * p[1] + u[2] * p[2];
            return "plane (" + string.Join(", ", Array.ConvertAll(u, x => (Math.Round(x, 4) + 0.0).ToString(System.Globalization.CultureInfo.InvariantCulture))) + ") "
                + (Math.Round(d * 10, 3) + 0.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // A cylinder: its axis (the point on it nearest the origin, and its direction with the first non-zero component
        // positive) and radius, in mm.
        public static string Cylinder(double[] p, double[] axis, double r)
        {
            double[] a = Unit(axis);
            int first = Math.Abs(a[0]) > 1e-9 ? 0 : Math.Abs(a[1]) > 1e-9 ? 1 : 2;
            if (a[first] < 0) a = new[] { -a[0], -a[1], -a[2] };
            double[] foot = Plus(p, a, -Dot(p, a));
            return "cylinder " + Mm3(foot) + " along " + Dir3(a) + " r " + (Math.Round(r * 10, 3) + 0.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // A B-rep edge or face in that form; null for other geometry.
        public static string Canonical(List<SabRecord> records, SabRecord e)
        {
            Func<SabRef, SabRecord> at = r => r != null && r.Target >= 0 && r.Target < records.Count ? records[r.Target] : null;
            if (e.Type == "edge")
            {
                SabRecord curve = at(e.Ref(8));
                if (curve == null) return null;
                if (curve.Type == "straight-curve")
                {
                    double[] a = Sab.VertexPosition(records, at(e.Ref(3))), b = Sab.VertexPosition(records, at(e.Ref(5)));
                    return a == null || b == null ? null : Line(a, b);
                }
                if (curve.Type == "ellipse-curve" && curve.Fields.Count > 6 && curve.Fields[5] is double[] && curve.Fields[6] is double && Math.Abs((double)curve.Fields[6] - 1) < 1e-9)
                {
                    double[] m = (double[])curve.Fields[5];
                    return Circle((double[])curve.Fields[3], Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]));
                }
                return null;
            }
            if (e.Type == "face")
            {
                SabRecord s = at(e.Ref(7));
                if (s != null && s.Type == "plane-surface") return Plane((double[])s.Fields[4], (double[])s.Fields[3]);
                Shape c = Surface(records, e);
                return c != null && c.Kind == "cylinder" ? Cylinder(c.A, c.B, c.R) : null;
            }
            return null;
        }

        // Geometry (cm): a point A, a segment A-B, a circle (centre A, normal B, radius R), or a plane (point A, normal B).
        public class Shape
        {
            public string Kind;
            public double[] A, B;
            public double R;
            public double R2, K;     // torus: minor radius; cone: radius change per unit along its axis (tan of the half angle)
            public bool Unbounded;   // a line known only as a line, not where it ends

            public string Canonical()
            {
                switch (Kind)
                {
                    case "line": return Unbounded ? "line through " + Mm3(A) + " along " + Dir3(new[] { B[0] - A[0], B[1] - A[1], B[2] - A[2] }) : Line(A, B);
                    case "circle": return Circle(A, R);
                    case "plane": return Plane(B, A);
                    case "cylinder": return "cylinder through " + Mm3(A) + " along " + Dir3(Unit(B)) + " r " + (Math.Round(R * 10, 3) + 0.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case "cone": return "cone at " + Mm3(A) + " along " + Dir3(Unit(B)) + " r " + Math.Round(R * 10, 3) + " slope " + Math.Round(K, 6);
                    case "torus": return "torus at " + Mm3(A) + " along " + Dir3(Unit(B)) + " r " + Math.Round(R * 10, 3) + " / " + Math.Round(R2 * 10, 3);
                    case "sphere": return "sphere at " + Mm3(A) + " r " + Math.Round(R * 10, 3);
                    default: return "point " + Mm3(A);
                }
            }
        }

        // What a sweep name means, rebuilt from the sketch: which feature and sketch entity, and the geometry where
        // the extent gives it. Dead: the sketch entity is gone (deleted after the name was made).
        public class Rebuilt
        {
            public string Text;
            public Shape Shape;
            public bool Dead;
        }

        public class Document
        {
            public NodeTable Dc;
            public Dictionary<int, Features.Info> ByTag = new Dictionary<int, Features.Info>();
            public Dictionary<int, Patterns.PatternInfo> Copiers = new Dictionary<int, Patterns.PatternInfo>();   // mirrors and patterns by tag
            public Dictionary<int, Features.ExtrudeInfo> Extrudes = new Dictionary<int, Features.ExtrudeInfo>();
            public Dictionary<int, Features.RevolveInfo> Revolves = new Dictionary<int, Features.RevolveInfo>();
            public Dictionary<string, int> Items;
            public Dictionary<int, string> Names;

            public Document(NodeTable dc)
            {
                Dc = dc;
                foreach (Features.Info fi in Features.List(dc)) if (fi.Tag > 0 && !ByTag.ContainsKey(fi.Tag)) ByTag[fi.Tag] = fi;
                foreach (Patterns.PatternInfo p in Patterns.List(dc)) if (p.Tag > 0 && !Copiers.ContainsKey(p.Tag)) Copiers[p.Tag] = p;
                foreach (Features.ExtrudeInfo e in Features.Extrudes(dc)) Extrudes[e.Index] = e;
                foreach (Features.RevolveInfo rv in Features.Revolves(dc)) Revolves[rv.Index] = rv;
                Items = SketchItems(dc);
                Names = Sketches.BrowserNames(dc);
            }
        }

        static readonly Dictionary<int, string> Roles = new Dictionary<int, string> {
            { 9, "vertex at the end" }, { 10, "edge at the end" }, { 12, "end face" }, { 18, "edge along the sweep" }, { 20, "face along the sweep" } };

        static double[] Apply(double[] m, double x, double y)
        {
            return new[] { m[0] * x + m[1] * y + m[3], m[4] * x + m[5] * y + m[7], m[8] * x + m[9] * y + m[11] };
        }

        static double[] Plus(double[] p, double[] n, double k) { return new[] { p[0] + n[0] * k, p[1] + n[1] * k, p[2] + n[2] * k }; }

        public static Rebuilt Rebuild(Document doc, Generator g)
        {
            if (g == null || g.Tags.Count == 0) return null;
            Features.Info fi;
            // A name made by a feature the document no longer has (deleted): it can name nothing.
            foreach (int tag in g.Tags.FindAll(x => g.Tags.IndexOf(x) % 2 == 0))
                if (!doc.ByTag.ContainsKey(tag) && !doc.Copiers.ContainsKey(tag))
                    return new Rebuilt { Dead = true, Text = "feature tag " + tag + ", which the document no longer has" };
            if (!doc.ByTag.TryGetValue(g.Tags[g.Tags.Count - 1], out fi)) return null;
            if (g.Tags.Count > 1)
            {
                // Copy k of mirror or pattern P ([P, k, ...rest]): what the rest names, moved by that copy's transform
                // (see PatternInfo.Instance). Outer copies come first in the path, so they apply last.
                Patterns.PatternInfo p;
                doc.Copiers.TryGetValue(g.Tags[0], out p);
                Rebuilt inner = Rebuild(doc, new Generator { Kind = g.Kind, Item = g.Item, Flags = g.Flags, Tags = g.Tags.GetRange(2, g.Tags.Count - 2) });
                if (inner == null) return null;
                int k = g.Tags[1];
                Rebuilt copy = new Rebuilt { Dead = inner.Dead, Text = inner.Text + ", copy " + k + " made by " + (p == null ? "feature " + g.Tags[0] : p.Name ?? p.Kind) };
                double[] move = p == null ? null : p.Instance(k);
                if (move == null)
                    copy.Text += " (copy not placed: " + (p == null ? "copier not found" : p.Transforms == null ? "its transforms are not rebuilt" : "no such element") + ")";
                else if (inner.Shape != null) copy.Shape = Moved(inner.Shape, move);
                return copy;
            }
            // Older parts mark the start cap 1 / 2 / 4 (vertex / edge / face) instead of 0x1000 + 9 / 10 / 12.
            int flags = g.Flags & 0xFFF;
            bool start = (g.Flags & 0x1000) != 0 || flags == 1 || flags == 2 || flags == 4;
            if (flags == 1 || flags == 2 || flags == 4) flags += 8;
            string role;
            if (!Roles.TryGetValue(flags, out role)) role = "flags " + g.Flags;
            if (start) role = role.Replace("the end", "the start").Replace("end face", "start face");
            Features.ExtrudeInfo e;
            Features.RevolveInfo rv;
            if (doc.Revolves.TryGetValue(fi.Node.Index, out rv) && rv.SketchNode >= 0) return RebuildRevolve(doc, g, fi, rv, flags, role);
            if (!doc.Extrudes.TryGetValue(fi.Node.Index, out e) || e.SketchNode < 0)
                return new Rebuilt { Text = fi.Name + " " + role + ", from item " + g.Item + " (not rebuilt: not an extrude or revolve)" };
            string sketch;
            if (!doc.Names.TryGetValue(e.SketchNode, out sketch)) sketch = "sketch #" + e.SketchNode.ToString("X");
            // Item 0 is the whole profile, not a sketch entity: cap faces (12), and faces flagged 2052 (meaning unknown).
            bool capFace = flags == 12 && g.Item == 0;
            if (g.Item == 0 && !capFace) return new Rebuilt { Text = fi.Name + " " + role + ", from " + sketch + " (the profile; not rebuilt)" };
            int entity = -1;
            if (!capFace && !doc.Items.TryGetValue(e.SketchNode + " " + g.Item, out entity))
                return new Rebuilt { Dead = true, Text = fi.Name + " " + role + ", from " + sketch + " item " + g.Item + ", which no longer exists" };
            Rebuilt r = new Rebuilt { Text = fi.Name + " " + role + ", from " + sketch + (capFace ? " (the profile)" : " " + doc.Dc.At(entity).TypeName.Replace("2D", "").ToLowerInvariant() + " #" + entity.ToString("X")) };
            double[] m = Sketches.Placement(doc.Dc, e.SketchNode), geo = capFace ? new double[0] : Sketches.Geometry2D(doc.Dc, entity);
            if (m == null || geo == null) return r;
            // A taper tilts the faces along the sweep; not rebuilt.
            if (e.Tapered) { r.Text += " (tapered: geometry not rebuilt)"; return r; }
            double[] n = e.Axis ?? new[] { m[2], m[6], m[10] };
            // Without a distance (through all, to a face, ...) only what runs along the sweep is known: faces, and
            // edges as whole lines.
            if (double.IsNaN(e.DistanceCm))
            {
                if (flags == 18 && geo.Length == 2) { double[] p = Apply(m, geo[0], geo[1]); r.Shape = new Shape { Kind = "line", A = p, B = Plus(p, n, 1), Unbounded = true }; }
                else if (flags == 20) r.Shape = AlongSweep(m, geo, n);
                return r;
            }
            double d = e.DistanceCm;
            double lo = e.Direction == "kSymmetricExtentDirection" ? -d / 2 : e.Direction == "kNegativeExtentDirection" ? -d : 0;
            double hi = e.Direction == "kSymmetricExtentDirection" ? d / 2 : e.Direction == "kNegativeExtentDirection" ? 0 : d;
            // A second direction runs the other way from the sketch plane.
            if (!double.IsNaN(e.DistanceTwoCm)) { if (e.Direction == "kNegativeExtentDirection") hi = e.DistanceTwoCm; else lo = -e.DistanceTwoCm; }
            // The start cap is the one the sweep leaves from; with a negative extrude that is still the sketch plane.
            double cap = start ? (e.Direction == "kNegativeExtentDirection" ? hi : lo) : (e.Direction == "kNegativeExtentDirection" ? lo : hi);
            switch (flags)
            {
                case 9: if (geo.Length == 2) r.Shape = new Shape { Kind = "point", A = Plus(Apply(m, geo[0], geo[1]), n, cap) }; break;
                case 18:
                    if (geo.Length == 2) { double[] p = Apply(m, geo[0], geo[1]); r.Shape = new Shape { Kind = "line", A = Plus(p, n, lo), B = Plus(p, n, hi) }; }
                    break;
                case 10:
                    if (geo.Length == 4) r.Shape = new Shape { Kind = "line", A = Plus(Apply(m, geo[0], geo[1]), n, cap), B = Plus(Apply(m, geo[2], geo[3]), n, cap) };
                    else if (geo.Length == 3) r.Shape = new Shape { Kind = "circle", A = Plus(Apply(m, geo[0], geo[1]), n, cap), B = n, R = geo[2] };
                    break;
                case 12: r.Shape = new Shape { Kind = "plane", A = Plus(Apply(m, 0, 0), n, cap), B = n }; break;
                case 20: r.Shape = AlongSweep(m, geo, n); break;
            }
            return r;
        }

        // What a revolve made from one sketch entity, about its axis (Line3D: point and vector): a point sweeps a circle
        // (an edge along the sweep, flags 18); a line a plane, cylinder or cone, and an arc or circle a torus or sphere
        // (a face along the sweep, flags 20). End caps of partial revolves are not rebuilt.
        static Rebuilt RebuildRevolve(Document doc, Generator g, Features.Info fi, Features.RevolveInfo rv, int flags, string role)
        {
            string sketch;
            if (!doc.Names.TryGetValue(rv.SketchNode, out sketch)) sketch = "sketch #" + rv.SketchNode.ToString("X");
            // Item 0 is the whole profile (the caps of a partial revolve), not a sketch entity.
            if (g.Item == 0) return new Rebuilt { Text = fi.Name + " " + role + ", from " + sketch + " (the profile; not rebuilt)" };
            int entity;
            if (!doc.Items.TryGetValue(rv.SketchNode + " " + g.Item, out entity))
                return new Rebuilt { Dead = true, Text = fi.Name + " " + role + ", from " + sketch + " item " + g.Item + ", which no longer exists" };
            Rebuilt r = new Rebuilt { Text = fi.Name + " " + role + ", from " + sketch + " " + doc.Dc.At(entity).TypeName.Replace("2D", "").ToLowerInvariant() + " #" + entity.ToString("X") };
            double[] m = Sketches.Placement(doc.Dc, rv.SketchNode), geo = Sketches.Geometry2D(doc.Dc, entity);
            if (m == null || geo == null || rv.AxisStart == null) return r;
            double[] o = rv.AxisStart, ax = Unit(new[] { rv.AxisEnd[0] - o[0], rv.AxisEnd[1] - o[1], rv.AxisEnd[2] - o[2] });
            Func<double[], double[]> hr = p => { double[] w = { p[0] - o[0], p[1] - o[1], p[2] - o[2] }; return new[] { Dot(w, ax), Len(Cross(w, ax)) }; };
            const double tol = 1e-9;
            if (flags == 18 && geo.Length == 2)
            {
                double[] p = hr(Apply(m, geo[0], geo[1]));
                if (p[1] > tol) r.Shape = new Shape { Kind = "circle", A = Plus(o, ax, p[0]), B = ax, R = p[1] };
            }
            else if (flags == 20 && geo.Length == 4)
            {
                double[] p = hr(Apply(m, geo[0], geo[1])), q = hr(Apply(m, geo[2], geo[3]));
                if (Math.Abs(p[0] - q[0]) < tol) r.Shape = new Shape { Kind = "plane", A = Plus(o, ax, p[0]), B = ax };
                else if (Math.Abs(p[1] - q[1]) < tol) r.Shape = p[1] > tol ? new Shape { Kind = "cylinder", A = o, B = ax, R = p[1] } : null;
                else r.Shape = new Shape { Kind = "cone", A = Plus(o, ax, p[0]), B = ax, R = p[1], K = (q[1] - p[1]) / (q[0] - p[0]) };
            }
            else if (flags == 20 && geo.Length == 3)
            {
                double[] c = hr(Apply(m, geo[0], geo[1]));
                r.Shape = c[1] < 1e-7 ? new Shape { Kind = "sphere", A = Plus(o, ax, c[0]), R = geo[2] }
                    : new Shape { Kind = "torus", A = Plus(o, ax, c[0]), B = ax, R = c[1], R2 = geo[2] };
            }
            return r;
        }

        // The face a sketch line or circle sweeps along n: a plane, or a cylinder (centre, axis, radius).
        static Shape AlongSweep(double[] m, double[] geo, double[] n)
        {
            if (geo.Length == 4)
            {
                double[] a = Apply(m, geo[0], geo[1]), b = Apply(m, geo[2], geo[3]);
                return new Shape { Kind = "plane", A = a, B = Cross(new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] }, n) };
            }
            if (geo.Length == 3) return new Shape { Kind = "cylinder", A = Apply(m, geo[0], geo[1]), B = n, R = geo[2] };
            return null;
        }

        // A shape moved by a 3 x 4 transform (Patterns): points move, directions (a line's B for unbounded lines is a
        // point too; a circle's or plane's B is a normal) only turn.
        static Shape Moved(Shape s, double[] m)
        {
            Func<double[], double[]> turn = v => new[] { m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[4] * v[0] + m[5] * v[1] + m[6] * v[2], m[8] * v[0] + m[9] * v[1] + m[10] * v[2] };
            bool normal = s.Kind != "line" && s.Kind != "point";
            return new Shape { Kind = s.Kind, A = Patterns.Apply(m, s.A), B = s.B == null ? null : normal ? turn(s.B) : Patterns.Apply(m, s.B), R = s.R, R2 = s.R2, K = s.K, Unbounded = s.Unbounded };
        }

        // Does the B-rep geometry lie on the rebuilt shape (a trimmed edge lies within the rebuilt segment)?
        public static bool Agrees(Shape s, List<SabRecord> records, SabRecord e)
        {
            Func<SabRef, SabRecord> at = x => x != null && x.Target >= 0 && x.Target < records.Count ? records[x.Target] : null;
            const double tol = 1e-5;
            if (e.Type == "edge")
            {
                SabRecord curve = at(e.Ref(8));
                if (curve == null) return false;
                if (s.Kind == "line" && curve.Type == "straight-curve")
                {
                    // On the same line and overlapping: joined features merge collinear edges, so the B-rep edge can be
                    // longer than the one this feature made; later features can trim it shorter.
                    double[] p = Sab.VertexPosition(records, at(e.Ref(3))), q = Sab.VertexPosition(records, at(e.Ref(5)));
                    if (p == null || q == null) return false;
                    double tp, tq;
                    if (!OnLine(p, s.A, s.B, tol, out tp) || !OnLine(q, s.A, s.B, tol, out tq)) return false;
                    return s.Unbounded || (Math.Max(tp, tq) > tol && Math.Min(tp, tq) < 1 - tol);
                }
                if (s.Kind == "circle" && curve.Type == "ellipse-curve" && curve.Fields[5] is double[])
                {
                    double[] c = (double[])curve.Fields[3];
                    return Len(new[] { c[0] - s.A[0], c[1] - s.A[1], c[2] - s.A[2] }) < tol && Math.Abs(Len((double[])curve.Fields[5]) - s.R) < tol;
                }
                return false;
            }
            if (e.Type == "face" && s.Kind == "plane")
            {
                SabRecord surf = at(e.Ref(7));
                if (surf == null || surf.Type != "plane-surface") return false;
                double[] o = (double[])surf.Fields[3], nn = Unit((double[])surf.Fields[4]), sn = Unit(s.B);
                return Len(Cross(nn, sn)) < tol && Math.Abs(nn[0] * (o[0] - s.A[0]) + nn[1] * (o[1] - s.A[1]) + nn[2] * (o[2] - s.A[2])) < tol;
            }
            if (e.Type == "face" && (s.Kind == "cylinder" || s.Kind == "cone" || s.Kind == "torus" || s.Kind == "sphere"))
                return SameSurface(Surface(records, e), s);
            if (e.Type == "vertex" && s.Kind == "point")
            {
                double[] p = Sab.VertexPosition(records, e);
                return p != null && Len(new[] { p[0] - s.A[0], p[1] - s.A[1], p[2] - s.A[2] }) < tol;
            }
            return false;
        }

        // Is p on the line through a and b? t: where along it (0 at a, 1 at b).
        static bool OnLine(double[] p, double[] a, double[] b, double tol, out double t)
        {
            double[] ab = { b[0] - a[0], b[1] - a[1], b[2] - a[2] }, ap = { p[0] - a[0], p[1] - a[1], p[2] - a[2] };
            double len = Len(ab);
            t = 0;
            if (len < tol) return Len(ap) < tol;
            t = (ab[0] * ap[0] + ab[1] * ap[1] + ab[2] * ap[2]) / (len * len);
            return Len(Cross(ab, ap)) / len < tol;
        }

        public const uint SketchItem = 0x90874D15;

        // Sweep items are sketch entities: a 90874D15 node cross-refs the entity at +0x12 and holds its item number
        // (u32 at +0x22), a per-sketch counter in creation order (deleted entities leave gaps). Keyed "sketch item".
        public static Dictionary<string, int> SketchItems(NodeTable t)
        {
            Dictionary<string, int> r = new Dictionary<string, int>();
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Type.Key != SketchItem || n.Data == null) continue;
                int s = Sketches.Shift(n.Data);
                if (n.Data.Length < 0x26 + s) continue;
                Node e = t.At(Sketches.XRef(n.Data, 0x12 + s));
                if (e == null || e.Data == null || e.Data.Length < 0x2A + Sketches.Shift(e.Data)) continue;
                int sketch = Sketches.XRef(e.Data, 0x26 + Sketches.Shift(e.Data));
                r[sketch + " " + U32(n.Data, 0x22 + s)] = e.Index;
            }
            return r;
        }

        // Naming table (CCE92042): u32 count at +0x0E, then {u32 entry index, xref entry node} pairs from +0x12.
        public static Dictionary<int, int> Entries(Node table)
        {
            Dictionary<int, int> r = new Dictionary<int, int>();
            byte[] d = table.Data;
            if (d == null || d.Length < 0x12) return r;
            int count = (int)U32(d, 0x0E);
            for (int i = 0, p = 0x12; i < count && p + 8 <= d.Length; i++, p += 8) r[(int)U32(d, p)] = Sketches.XRef(d, p + 4);
            return r;
        }

        // The document's main naming table: the one with the most entries.
        public static Node MainTable(NodeTable t)
        {
            Node best = null;
            foreach (Node n in t.Nodes)
                if (n.Type != null && n.Type.Key == Table && n.Data != null && (best == null || n.Data.Length > best.Data.Length)) best = n;
            return best;
        }

        // Every EdgeItem and FaceItem with its id node and the B-rep entities it names. withFaces: also work out
        // edges from their faces where they resolve otherwise (to check one against the other).
        public static Dictionary<int, Resolved> Resolve(NodeTable dc, byte[] brep, bool withFaces = false)
        {
            Dictionary<int, List<Match>> matches = Matches(brep);
            Dictionary<string, List<Match>> generated = null;
            Document doc = null;
            Dictionary<int, Dictionary<int, int>> tables = new Dictionary<int, Dictionary<int, int>>();
            Dictionary<int, Resolved> r = new Dictionary<int, Resolved>();
            foreach (Node n in dc.Nodes)
                if (n.Type != null && n.Data != null && (n.TypeName == "EdgeItem" || n.TypeName == "FaceItem"))
                    r[n.Index] = new Resolved { Item = n };
            foreach (Node n in dc.Nodes)
            {
                if (n.Type == null || n.Data == null || !IdTypes.Contains(n.TypeName)) continue;
                byte[] d = n.Data;
                int s = Sketches.Shift(d);
                Resolved item = null;
                List<int> entries = new List<int>();
                List<Node> entryNodes = new List<Node>();
                for (int p = 0x1A + s; p + 4 <= d.Length; p++)
                {
                    int x = Sketches.XRef(d, p);
                    Resolved found;
                    if (item == null && r.TryGetValue(x, out found)) item = found;
                    Node t = dc.At(x);
                    if (t == null || t.Type == null || t.Type.Key != Table || p + 8 > d.Length) continue;
                    int e = (int)U32(d, p + 4), node;
                    entries.Add(e);
                    if (!tables.ContainsKey(t.Index)) tables[t.Index] = Entries(t);
                    if (tables[t.Index].TryGetValue(e, out node)) entryNodes.Add(dc.At(node));
                }
                if (item == null || item.Id != null) continue;
                item.Id = n;
                item.Name = (int)U32(d, 0x16 + s);
                item.Entries = entries;
                List<Match> list;
                string kind = item.Item.TypeName == "EdgeItem" ? "edge" : "face";
                if (matches.TryGetValue(item.Name, out list))
                    foreach (Match m in list) if (m.Flag == 0 && m.Entity.Type == kind) item.Entities.Add(m);
                foreach (Node en in entryNodes)
                {
                    if (en != null) item.EntryTypes.Add(en.TypeName);
                    Generator g = EntryGenerator(en);
                    if (g == null) continue;
                    item.Generators.Add(g);
                    if (g.Kind == kind && item.Rebuilt == null)
                    {
                        if (doc == null) doc = new Document(dc);
                        item.Rebuilt = Rebuild(doc, g);
                    }
                }
                if (item.Entities.Count > 0) { item.Via = "matched"; continue; }
                // No match recorded: an entity carrying the same generator name as one of the entries.
                foreach (Generator g in item.Generators)
                {
                    if (g.Kind != kind) continue;
                    if (generated == null) generated = Generated(brep);
                    if (generated.TryGetValue(kind + " " + string.Join("+", g.Tags.ConvertAll(x => x.ToString()).ToArray()) + " " + g.Item + " " + g.Flags, out list))
                        item.Entities.AddRange(list);
                }
                if (item.Entities.Count > 0) { item.Via = "generated"; continue; }
            }
            // Edges still not found: from the two faces their entry names.
            Context ctx = new Context { Dc = dc, Brep = brep };
            foreach (Resolved item in r.Values)
            {
                if (item.Id == null || item.Item.TypeName != "EdgeItem") continue;
                if (item.Entities.Count > 0 && !withFaces) continue;
                foreach (int e in item.Entries)
                {
                    Node en = ctx.Entry(e);
                    if (!IsEdgeEntry(en)) continue;
                    EdgeFromFaces(ctx, item, en);
                    break;
                }
                if (item.Entities.Count == 0 && item.CommonEdges.Count > 0) { item.Entities.AddRange(item.CommonEdges); item.Via = "faces"; }
            }
            // Still nothing: dead if what its name is built from is gone (a deleted sketch entity, maybe a few names down).
            foreach (Resolved item in r.Values)
            {
                if (item.Id == null || item.Entities.Count > 0 || item.FromFaces != null || (item.Rebuilt != null && (item.Rebuilt.Shape != null || item.Rebuilt.Dead))) continue;
                foreach (int e in item.Entries)
                {
                    string why = DeadEntry(ctx, ctx.Entry(e), 0);
                    if (why == null) continue;
                    item.Rebuilt = new Rebuilt { Dead = true, Text = "named through " + why };
                    break;
                }
            }
            // Entries some input resolved through, then inputs findable by a chain of such names.
            ctx.Found = new HashSet<int>();
            foreach (Resolved item in r.Values)
                if (item.Id != null && (item.Entities.Count > 0 || item.FromFaces != null || (item.Rebuilt != null && item.Rebuilt.Shape != null)))
                    ctx.Found.UnionWith(item.Entries);
            foreach (Resolved item in r.Values)
            {
                if (item.Id == null || item.Entities.Count > 0 || item.FromFaces != null || (item.Rebuilt != null && (item.Rebuilt.Shape != null || item.Rebuilt.Dead))) continue;
                if (item.Surface1 != null && item.Surface2 != null) continue;
                foreach (int e in item.Entries)
                    if (IsEdgeEntry(ctx.Entry(e)) && Unfindable(ctx, e, 0) == null) { item.ByChain = true; break; }
            }
            return r;
        }

        // The EdgeItems and FaceItems a feature uses: reached from its property slots through collections and
        // definitions (not through other features, parameters, sketches or the naming nodes).
        public static List<int> Inputs(NodeTable t, Node feature)
        {
            List<int> found = new List<int>();
            HashSet<int> seen = new HashSet<int>();
            Queue<KeyValuePair<int, int>> todo = new Queue<KeyValuePair<int, int>>();
            foreach (int p in Features.Properties(feature)) if (p >= 0) todo.Enqueue(new KeyValuePair<int, int>(p, 0));
            while (todo.Count > 0)
            {
                KeyValuePair<int, int> kv = todo.Dequeue();
                if (!seen.Add(kv.Key)) continue;
                Node n = t.At(kv.Key);
                if (n == null || n.Type == null || n.Data == null) continue;
                if (n.TypeName == "EdgeItem" || n.TypeName == "FaceItem") { found.Add(n.Index); continue; }
                if (kv.Value >= 4 || n.Type.Key == Features.Feature || n.Type.Key == Features.Parameter || n.Type.Key == Sketches.Sketch2D
                    || n.Type.Key == Table || IdTypes.Contains(n.TypeName) || n.TypeName == "SurfaceBody" || n.Type.Key == 0x90874D63) continue;
                for (int p = 0x1A + Sketches.Shift(n.Data); p + 4 <= n.Data.Length; p++)
                {
                    int x = Sketches.XRef(n.Data, p);
                    if (x >= 0 && !seen.Contains(x)) todo.Enqueue(new KeyValuePair<int, int>(x, kv.Value + 1));
                }
            }
            return found;
        }

        public static string Describe(Resolved r)
        {
            string kind = r.Item.TypeName == "EdgeItem" ? "edge" : "face";
            if (r.Id == null) return kind + " (no name found)";
            if (r.Entities.Count == 0 && r.Rebuilt != null && (r.Rebuilt.Shape != null || r.Rebuilt.Dead || r.FromFaces == null))
                return kind + " " + (r.Rebuilt.Shape != null ? r.Rebuilt.Shape.Canonical() + " = " : "") + r.Rebuilt.Text
                    + (r.Rebuilt.Dead ? "" : r.Rebuilt.Shape != null ? " [rebuilt from the sketch]" : "");
            if (r.Entities.Count == 0 && r.FromFaces != null)
                return kind + " " + r.FromFaces.Shape.Canonical() + " = " + r.FromFaces.Text + " [from its faces]";
            if (r.Entities.Count == 0 && r.Surface1 != null && r.Surface2 != null)
                return kind + " where " + r.Surface1.Canonical() + " meets " + r.Surface2.Canonical() + " [by its faces' surfaces; the curve is not rebuilt]";
            if (r.Entities.Count == 0) return kind + " named " + r.Name + " (entries " + string.Join(",", r.Entries.ConvertAll(e => e.ToString()).ToArray()) + "): not found in the B-rep";
            List<string> where = new List<string>();
            foreach (Match m in r.Entities)
            {
                string w = Sab.Where(m.Records, m.Entity) + (m.Entity.Old ? " (earlier state)" : "");
                if (!where.Contains(w)) where.Add(w);
            }
            return kind + " " + string.Join(" | ", where.ToArray()) + (r.Via == "generated" ? " [by generator name]" : r.Via == "faces" ? " [shared by its two faces]" : "");
        }

        // A resolved input in the comparable form ("edge line ..."); several when its entities disagree, none if unresolved.
        public static List<string> Shapes(Resolved r)
        {
            List<string> s = new List<string>();
            string kind = r.Item.TypeName == "EdgeItem" ? "edge " : "face ";
            foreach (Match m in r.Entities)
            {
                string c = Canonical(m.Records, m.Entity);
                c = kind + (c ?? Sab.Where(m.Records, m.Entity));
                if (!s.Contains(c)) s.Add(c);
            }
            return s;
        }

        static readonly System.Text.RegularExpressions.Regex Picked = new System.Text.RegularExpressions.Regex("\"picked\": \\[(?<list>[^\\]]*)\\]");
        static readonly System.Text.RegularExpressions.Regex Quoted = new System.Text.RegularExpressions.Regex("\"(?<v>[^\"]*)\"");
        static readonly System.Text.RegularExpressions.Regex Chamfer = new System.Text.RegularExpressions.Regex(
            "\\{\"name\": \"(?<name>[^\"]*)\", \"type\": \"kChamferFeatureObject\"[^}]*\"edgesAt\": \"(?<at>[^\"]*)\"");

        // Resolved inputs vs the truth: a generated part's "picked" list (all features together), and each chamfer's
        // "edgesAt" (older parts, where the API lists the edges). Returns the differences; checked counts inputs compared.
        public static List<string> Compare(NodeTable dc, byte[] brep, string truthJson, out int checkedCount)
        {
            Dictionary<int, Resolved> all = Resolve(dc, brep);
            List<string> bad = new List<string>();
            checkedCount = 0;
            // A sheet-metal plate and the bend it makes share their edge collection, so each input counts once.
            HashSet<int> counted = new HashSet<int>();
            Func<Features.Info, List<string>> shapes = fi => {
                List<string> got = new List<string>();
                foreach (int i in Inputs(dc, fi.Node))
                {
                    if (!counted.Add(i)) continue;
                    Resolved r;
                    List<string> s = all.TryGetValue(i, out r) ? Shapes(r) : new List<string>();
                    got.Add(s.Count == 1 ? s[0] : s.Count == 0 ? "(unresolved " + (r == null ? "?" : r.Item.TypeName) + ")" : "(disagree: " + string.Join(" / ", s.ToArray()) + ")");
                }
                return got;
            };
            System.Text.RegularExpressions.Match pm = Picked.Match(truthJson);
            if (pm.Success)
            {
                List<string> want = new List<string>(), got = new List<string>();
                foreach (System.Text.RegularExpressions.Match q in Quoted.Matches(pm.Groups["list"].Value)) want.Add(q.Groups["v"].Value);
                foreach (Features.Info fi in Features.List(dc)) got.AddRange(shapes(fi));
                got.AddRange(Patterns.Picks(dc));
                checkedCount += want.Count;
                Diff(want, got, "picked", bad);
            }
            Dictionary<string, Features.Info> byName = new Dictionary<string, Features.Info>();
            foreach (Features.Info fi in Features.List(dc)) if (fi.Name != null) byName[fi.Name] = fi;
            foreach (System.Text.RegularExpressions.Match cm in Chamfer.Matches(truthJson))
            {
                Features.Info fi;
                if (!byName.TryGetValue(cm.Groups["name"].Value, out fi)) { bad.Add(cm.Groups["name"].Value + ": not decoded"); continue; }
                counted.Clear();
                List<string> want = new List<string>();
                foreach (string e in cm.Groups["at"].Value.Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries)) want.Add("edge " + e);
                List<string> got = shapes(fi).FindAll(s => !s.StartsWith("face"));
                checkedCount += want.Count;
                Diff(want, got, cm.Groups["name"].Value, bad);
            }
            return bad;
        }

        static void Diff(List<string> want, List<string> got, string what, List<string> bad)
        {
            List<string> left = new List<string>(got);
            foreach (string w in want)
                if (!left.Remove(w)) bad.Add(what + ": Inventor has " + w + ", not decoded");
            foreach (string g in left) bad.Add(what + ": decoded " + g + ", not in Inventor's list");
        }

        // A check that needs no Inventor: each resolved straight fillet edge should have, among the B-rep faces the
        // same fillet made (INV_NMX_BLEND_TAG with its tag, the feature's +0x16), a cylinder of the fillet's radius
        // whose axis runs parallel to the edge, at least a radius away from it. cone-surface: centre, axis, major
        // axis (length = radius), ratio, 2 Booleans, sine and cosine of the half angle (0, 1 for a cylinder).
        public static void FilletCheck(NodeTable dc, byte[] brep, List<string> failures, ref int checkedCount, ref int passed, List<double> ratios)
        {
            Dictionary<int, Resolved> all = Resolve(dc, brep);
            Dictionary<int, Features.FilletInfo> fillets = new Dictionary<int, Features.FilletInfo>();
            foreach (Features.FilletInfo f in Features.Fillets(dc)) fillets[f.Index] = f;
            // Cylinders by the tag of the feature that made them.
            Dictionary<int, List<double[][]>> cylinders = new Dictionary<int, List<double[][]>>();
            int at = 0;
            while ((at = Sab.Find(brep, at)) >= 0)
            {
                string error;
                List<SabRecord> records = Sab.Read(brep, at, out error);
                foreach (SabRecord a in records)
                {
                    string name;
                    List<object> data;
                    if (!NamingAttr(a, out name, out data) || name != "INV_NMX_BLEND_TAG" || data.Count < 4) continue;
                    SabRecord face = Owner(records, a);
                    SabRecord s = face == null || face.Type != "face" ? null : face.Ref(7) != null && face.Ref(7).Target >= 0 && face.Ref(7).Target < records.Count ? records[face.Ref(7).Target] : null;
                    if (s == null || s.Type != "cone-surface" || s.Fields.Count < 11 || !(s.Fields[9] is double) || Math.Abs((double)s.Fields[9]) > 1e-9) continue;
                    int n = Di(data, 0);
                    if (n < 0 || n > 8) continue;
                    int m = Di(data, 1 + n);
                    for (int k = 0; k < 2 * m - 1 && m < 8; k++)
                    {
                        int tag = Di(data, 2 + n + k);
                        if (!cylinders.ContainsKey(tag)) cylinders[tag] = new List<double[][]>();
                        cylinders[tag].Add(new[] { (double[])s.Fields[3], (double[])s.Fields[4], (double[])s.Fields[5] });
                    }
                }
                at++;
            }
            foreach (Features.Info fi in Features.List(dc))
            {
                Features.FilletInfo f;
                if (!fillets.TryGetValue(fi.Node.Index, out f)) continue;
                int tag = (int)U32(fi.Node.Data, 0x16 + Sketches.Shift(fi.Node.Data));
                List<double[][]> cyl;
                cylinders.TryGetValue(tag, out cyl);
                foreach (int i in Inputs(dc, fi.Node))
                {
                    Resolved r;
                    if (!all.TryGetValue(i, out r) || r.Entities.Count == 0) continue;
                    Match e = r.Entities[0];
                    SabRef cr = e.Entity.Ref(8);
                    SabRecord curve = cr != null && cr.Target >= 0 && cr.Target < e.Records.Count ? e.Records[cr.Target] : null;
                    if (curve == null || curve.Type != "straight-curve") continue;
                    double[] p = Sab.VertexPosition(e.Records, e.Records[e.Entity.Ref(3).Target]), q = Sab.VertexPosition(e.Records, e.Records[e.Entity.Ref(5).Target]);
                    if (p == null || q == null) continue;
                    // No cylinders from this fillet (its faces are tori or splines, or it made none): nothing to check against.
                    if (cyl == null) { failures.Add("(can't check) " + fi.Name); continue; }
                    checkedCount++;
                    double[] d = Unit(new[] { q[0] - p[0], q[1] - p[1], q[2] - p[2] });
                    bool ok = false;
                    double best = double.MaxValue;
                    foreach (double[][] c in cyl ?? new List<double[][]>())
                    {
                        double radius = Len(c[2]);
                        double[] axis = Unit(c[1]);
                        if (Len(Cross(axis, d)) > 1e-6) continue;
                        bool radiusOk = false;
                        foreach (Features.EdgeSet es in f.Sets) if (Math.Abs(es.RadiusCm - radius) < 1e-6) radiusOk = true;
                        if (!radiusOk) continue;
                        // Distance between the edge line and the cylinder axis.
                        double[] w = { p[0] - c[0][0], p[1] - c[0][1], p[2] - c[0][2] };
                        double dist = Len(Cross(w, axis));
                        if (dist < radius * (1 - 1e-6)) continue;
                        ok = true;
                        best = Math.Min(best, dist / radius);
                    }
                    if (ok) { passed++; ratios.Add(best); }
                    else failures.Add(fi.Name + ": " + Sab.Where(e.Records, e.Entity) + ", radius " + string.Join("/", f.Sets.ConvertAll(s => s.Radius).ToArray())
                        + (cyl == null ? " (no fillet cylinders for tag " + tag + ")" : ""));
                }
            }
        }

        // Every sweep-named B-rep entity, rebuilt from its name (from the sketch, and moved by each mirror or pattern
        // copy's transform), against where the entity is. byWay counts by "original" / "<kind> copy", and entity type.
        public static void CopyCheck(NodeTable dc, byte[] brep, List<string> bad, ref int agree, ref int checkedCount, SortedDictionary<string, int> byWay)
        {
            // A shell going outward or both ways moves the outer faces, which keep the sweep's names: their rebuilt
            // places are from before the shell, so this check doesn't apply. (Inward shells leave them where they are.)
            if (MoreFeatures.Shells(dc).Exists(s => s.Direction != "kInsideShellDirection"))
            {
                int skipped;
                byWay.TryGetValue("parts skipped: an outward shell moves the faces", out skipped);
                byWay["parts skipped: an outward shell moves the faces"] = skipped + 1;
                return;
            }
            Document doc = null;
            foreach (KeyValuePair<string, List<Match>> kv in Generated(brep))
            {
                string[] parts = kv.Key.Split(' ');   // entity type, tag path, item, flags
                if (parts.Length != 4) continue;
                if (doc == null) doc = new Document(dc);
                Generator g = new Generator { Kind = parts[0], Item = int.Parse(parts[2]), Flags = int.Parse(parts[3]) };
                foreach (string x in parts[1].Split('+')) g.Tags.Add(int.Parse(x));
                Rebuilt r = Rebuild(doc, g);
                // The B-rep's own current names can't be dead: Inventor just made those entities.
                if (r != null && r.Dead)
                {
                    string w = "dead, current state";
                    if (kv.Value.TrueForAll(m => m.Entity.Old)) w = "dead, earlier states only";
                    int d;
                    byWay.TryGetValue(w, out d);
                    byWay[w] = d + 1;
                    if (w == "dead, current state") { checkedCount++; bad.Add(w + " " + r.Text); }
                    continue;
                }
                if (r == null || r.Shape == null) continue;
                Patterns.PatternInfo p;
                string way = g.Tags.Count == 1 ? "original" : (doc.Copiers.TryGetValue(g.Tags[0], out p) ? p.Kind : "?") + " copy" + (g.Tags.Count > 3 ? " of a copy" : "");
                way += " " + parts[0];
                checkedCount++;
                int c;
                byWay.TryGetValue(way, out c);
                byWay[way] = c + 1;
                if (kv.Value.Exists(m => Agrees(r.Shape, m.Records, m.Entity))) agree++;
                else bad.Add(way + " " + r.Text + ": " + r.Shape.Canonical() + ", B-rep " + string.Join(" / ", kv.Value.ConvertAll(m => Canonical(m.Records, m.Entity) ?? Sab.Where(m.Records, m.Entity)).ToArray()));
            }
        }

        // Where an input resolves to the B-rep and also has a sweep name that rebuilds: do the two agree?
        public static void RebuildCheck(NodeTable dc, byte[] brep, List<string> bad, ref int agree, ref int checkedCount, SortedDictionary<string, int> byRole)
        {
            foreach (Resolved r in Resolve(dc, brep, true).Values)
            {
                if (r.Entities.Count == 0) continue;
                // Each other way of finding it, where it gives an answer: rebuilt from the sketch, where its faces meet,
                // and the edge its faces share (not when that is how it was found).
                List<KeyValuePair<string, Shape>> ways = new List<KeyValuePair<string, Shape>>();
                if (r.Rebuilt != null && r.Rebuilt.Shape != null)
                {
                    string role = r.Rebuilt.Text.Contains(",") ? r.Rebuilt.Text.Substring(r.Rebuilt.Text.IndexOf(' ') + 1, r.Rebuilt.Text.IndexOf(',') - r.Rebuilt.Text.IndexOf(' ') - 1) : "?";
                    ways.Add(new KeyValuePair<string, Shape>((r.Rebuilt.Text.Contains(", copy ") ? "sketch, copied: " : "sketch: ") + role, r.Rebuilt.Shape));
                }
                if (r.FromFaces != null && r.FromFaces.Shape != null && r.Via != "faces")
                    ways.Add(new KeyValuePair<string, Shape>("faces meet" + (r.FromFaces.Shape.Kind == "circle" ? ", circle (" + r.MeetKinds + ")"
                        : r.FromFaces.Shape.Unbounded ? "" : " (bounded)"), r.FromFaces.Shape));
                foreach (KeyValuePair<string, Shape> w in ways)
                {
                    checkedCount++;
                    int c;
                    byRole.TryGetValue(w.Key, out c);
                    byRole[w.Key] = c + 1;
                    bool ok = false;
                    foreach (Match m in r.Entities) if (Agrees(w.Value, m.Records, m.Entity)) ok = true;
                    if (ok) agree++;
                    else bad.Add(w.Key + " " + (w.Key.StartsWith("sketch") ? r.Rebuilt.Text : r.FromFaces.Text) + ": " + w.Value.Canonical() + ", B-rep " + string.Join(" / ", Shapes(r).ToArray()));
                }
                // The two surfaces its faces' names give: the B-rep edge found should lie between faces on them.
                if (r.Surface1 != null && r.Surface2 != null && r.Item.TypeName == "EdgeItem")
                {
                    string key = "faces' surfaces (" + (string.CompareOrdinal(r.Surface1.Kind, r.Surface2.Kind) <= 0 ? r.Surface1.Kind + "+" + r.Surface2.Kind : r.Surface2.Kind + "+" + r.Surface1.Kind) + ")";
                    checkedCount++;
                    int c;
                    byRole.TryGetValue(key, out c);
                    byRole[key] = c + 1;
                    // Between faces on those surfaces, or (earlier-state edges keep fewer neighbours) lying on both.
                    bool ok = r.Entities.Exists(m => {
                        if (m.Entity.Type != "edge") return false;
                        List<Shape> on = EdgeFaces(m.Records, m.Entity).ConvertAll(f => Surface(m.Records, f));
                        if (on.Exists(s => SameSurface(s, r.Surface1)) && on.Exists(s => SameSurface(s, r.Surface2))) return true;
                        List<double[]> pts = EdgePoints(m.Records, m.Entity);
                        return pts.Count > 0 && pts.TrueForAll(p => OnSurface(r.Surface1, p) && OnSurface(r.Surface2, p));
                    });
                    if (ok) agree++;
                    else bad.Add(key + ": " + r.Surface1.Canonical() + " and " + r.Surface2.Canonical() + ", B-rep " + string.Join(" / ", Shapes(r).ToArray()));
                }
                foreach (KeyValuePair<string, List<Match>> shared in new[] {
                    new KeyValuePair<string, List<Match>>("faces share", r.CommonEdges),
                    new KeyValuePair<string, List<Match>>(r.AtVertices ? "faces share, unconfirmed, at its vertices" : "faces share, unconfirmed", r.Unconfirmed) })
                {
                    if (shared.Value.Count == 0 || r.Via == "faces") continue;
                    checkedCount++;
                    int c;
                    byRole.TryGetValue(shared.Key, out c);
                    byRole[shared.Key] = c + 1;
                    // The B-rep keeps copies of an edge from earlier states, so compare where they are, not which record.
                    Func<Match, string> shape = m => Canonical(m.Records, m.Entity) ?? Sab.Where(m.Records, m.Entity);
                    if (shared.Value.Exists(ce => r.Entities.Exists(m => shape(m) == shape(ce)))) agree++;
                    else bad.Add(shared.Key + ": " + Sab.Where(shared.Value[0].Records, shared.Value[0].Entity) + ", B-rep " + string.Join(" / ", Shapes(r).ToArray()));
                }
            }
        }

        static double Len(double[] v) { return Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); }
        static double[] Unit(double[] v) { double l = Len(v); return new[] { v[0] / l, v[1] / l, v[2] / l }; }
        static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }

        // How one input resolves, step by step (for finding out why one doesn't).
        public static string Explain(NodeTable dc, byte[] brep, int itemIndex)
        {
            StringBuilder sb = new StringBuilder();
            Resolved r;
            if (!Resolve(dc, brep, true).TryGetValue(itemIndex, out r) || r.Id == null) return "no such item, or no name\n";
            Context c = new Context { Dc = dc, Brep = brep };
            sb.AppendFormat("{0} #{1:X}: id #{2:X} {3}, name {4}, entries {5}; {6}\n", r.Item.TypeName, itemIndex, r.Id.Index, r.Id.TypeName, r.Name,
                string.Join(",", r.Entries.ConvertAll(x => x.ToString()).ToArray()), Describe(r));
            foreach (int e in r.Entries)
            {
                Node en = c.Entry(e);
                sb.AppendFormat("  entry {0}: {1}\n", e, en == null ? "missing" : en.TypeName + " #" + en.Index.ToString("X") + " " + EntryGenerator(en));
                if (en == null) continue;
                List<int> refs = EntryRefs(dc, en);
                for (int i = 0; i < refs.Count; i++)
                {
                    Node x = c.Entry(refs[i]);
                    Shape s;
                    string w;
                    s = null;
                    List<Match> m = x != null && (IsFaceEntry(x) || x.Type.Key == 0x8E5D4198 || x.Type.Key == 0x357D669C) ? FaceMatches(c, x, out s, out w) : null;
                    sb.AppendFormat("    refers to {0}: {1} {2}{3}{4}\n", refs[i], x == null ? "missing" : x.TypeName, x == null ? "" : (object)EntryGenerator(x) ?? BlendKey(x) ?? LoftKey(x),
                        m == null ? "" : ", " + m.Count + " B-rep faces", s == null ? "" : ", surface " + s.Canonical());
                    if (m != null)
                        foreach (Match f in m)
                        {
                            Shape fs = Surface(f.Records, f.Entity);
                            sb.AppendFormat("      face {0}{1}: {2}\n", f.Entity.Index, f.Entity.Old ? "*" : "", fs == null ? Sab.Where(f.Records, f.Entity) : fs.Canonical());
                        }
                }
                if (r.FromFaces != null) sb.AppendFormat("  where its faces meet ({0}): {1}\n", r.MeetKinds, r.FromFaces.Shape.Canonical());
            }
            return sb.ToString();
        }

        // Per feature: the edges and faces it uses, resolved.
        public static string Report(NodeTable dc, byte[] brep)
        {
            Dictionary<int, Resolved> all = Resolve(dc, brep);
            StringBuilder sb = new StringBuilder();
            foreach (Features.Info fi in Features.List(dc))
            {
                List<int> inputs = Inputs(dc, fi.Node);
                if (inputs.Count == 0) continue;
                sb.AppendFormat("  {0} ({1:X8}): {2} input(s)\n", fi.Name, fi.Kind, inputs.Count);
                foreach (int i in inputs)
                {
                    Resolved r;
                    sb.AppendFormat("    #{0:X} {1}\n", i, all.TryGetValue(i, out r) ? Describe(r) : "?");
                }
            }
            return sb.ToString();
        }
    }
}

