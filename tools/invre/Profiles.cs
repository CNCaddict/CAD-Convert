// Which closed loops of a sketch a profile uses.
//
// Profile (22947391): a kind-2 list at +0x22 of paths (A477243B), one per closed loop. A path cross-refs at +0x22 a
// CC0F7521 node: {xref to an ASM node (F645595C) in the DC segment, u32 entity id}. That ASM block holds the sketch's
// loops as wire bodies (body -> lump -> shell -> wire -> coedges -> edges); the id is the body record's own id (its
// second field). The wire's edges carry INV_NMX_GEN_TAG names {1, 44, 0, item, 0, 2}: an empty tag path, then the
// sketch item number of the curve they come from (vertices: flags 1).
using System;
using System.Collections.Generic;
using System.Text;

namespace InvRe
{
    public static class Profiles
    {
        public const uint Path = 0xA477243B, Link = 0xCC0F7521, Asm = 0xF645595C;

        public class Loop
        {
            public int PathNode = -1;
            public List<int> Items = new List<int>();      // sketch item numbers of its curves
            public List<int> Entities = new List<int>();   // their sketch entities, where the sketch still has them
            public string Problem;                         // why it could not be read, or null
            public List<SabRecord> Records;                // the ASM block its edges are in
            public List<SabRecord> Edges = new List<SabRecord>();   // the outline itself, trimmed where its curves cross
        }

        static uint U32(byte[] d, int p) { return BitConverter.ToUInt32(d, p); }

        // The ASM blocks of the DC segment, read once per node.
        static Dictionary<int, List<SabRecord>> cache = new Dictionary<int, List<SabRecord>>();
        static NodeTable cachedFor;

        static List<SabRecord> Records(NodeTable t, int node)
        {
            if (cachedFor != t) { cache.Clear(); cachedFor = t; }
            List<SabRecord> r;
            if (cache.TryGetValue(node, out r)) return r;
            Node n = t.At(node);
            int at = n == null || n.Data == null ? -1 : Sab.Find(n.Data, 0);
            string error;
            r = at < 0 ? null : Sab.Read(n.Data, at, out error);
            cache[node] = r;
            return r;
        }

        public static List<Loop> Loops(NodeTable t, int profile, int sketchNode, Dictionary<string, int> items)
        {
            List<Loop> loops = new List<Loop>();
            Node pn = t.At(profile);
            if (pn == null || pn.Data == null) return loops;
            int p = 0x22 + Sketches.Shift(pn.Data);
            if (p + 8 > pn.Data.Length || U32(pn.Data, p) != 0x30000002) return loops;
            List<int> paths;
            Sketches.ReadList(pn.Data, p, out paths);
            foreach (int pathIndex in paths) loops.Add(ReadPath(t, pathIndex, sketchNode, items));
            return loops;
        }

        // One path node (a profile's A477243B, a sweep's FC203F47): its link, its body, its curves' sketch items.
        public static Loop ReadPath(NodeTable t, int pathIndex, int sketchNode, Dictionary<string, int> items)
        {
            Loop loop = new Loop { PathNode = pathIndex };
            {
                Node path = t.At(pathIndex);
                Node link = null;
                if (path != null && path.Data != null)
                    for (int q = 0x1A; q + 4 <= path.Data.Length && link == null; q++)
                    {
                        Node x = t.At(Sketches.XRef(path.Data, q));
                        if (x != null && x.Type != null && x.Type.Key == Link) link = x;
                    }
                if (link == null) { loop.Problem = "no link"; return loop; }
                int asm = -1, id = int.MinValue;
                for (int q = 0; q + 8 <= link.Data.Length; q++)
                {
                    Node x = t.At(Sketches.XRef(link.Data, q));
                    if (x != null && x.Type != null && x.Type.Key == Asm) { asm = x.Index; id = (int)U32(link.Data, q + 4); break; }
                }
                List<SabRecord> recs = asm < 0 ? null : Records(t, asm);
                if (recs == null) { loop.Problem = "no ASM block"; return loop; }
                SabRecord body = recs.Find(r => r.Type == "body" && r.Int(1) == id);
                if (body == null) { loop.Problem = "no body " + id; return loop; }
                HashSet<int> edges = new HashSet<int>();
                foreach (SabRecord first in Rings(recs, body))
                {
                    SabRecord c = first;
                    for (int k = 0; c != null && c.Type == "coedge" && k < 100000; k++)
                    {
                        SabRef e = c.Ref(6);
                        if (e != null && e.Target >= 0) edges.Add(e.Target);
                        c = At(recs, c.Ref(3));
                        if (c == first) break;
                    }
                }
                loop.Records = recs;
                foreach (int e in edges) if (e < recs.Count) loop.Edges.Add(recs[e]);
                foreach (SabRecord a in recs)
                {
                    string name;
                    List<object> data;
                    if (!Naming.NamingAttr(a, out name, out data) || name != "INV_NMX_GEN_TAG") continue;
                    SabRef owner = a.Ref(4);
                    if (owner == null || !edges.Contains(owner.Target)) continue;
                    // {1, 44, path count 0, item, 0, flags 2}
                    if (data.Count < 6 || !(data[2] is int) || (int)data[2] != 0 || !(data[3] is int)) continue;
                    int item = (int)data[3];
                    if (loop.Items.Contains(item)) continue;
                    loop.Items.Add(item);
                    int entity;
                    if (items.TryGetValue(sketchNode + " " + item, out entity)) loop.Entities.Add(entity);
                }
                if (loop.Items.Count == 0) loop.Problem = "no named edges";
            }
            return loop;
        }

        static SabRecord At(List<SabRecord> recs, SabRef r) { return r != null && r.Target >= 0 && r.Target < recs.Count ? recs[r.Target] : null; }

        // The first coedge of each ring in a body: body -> lumps (body field 3, lump field 3 next) -> shells (lump 4,
        // shell 3 next) -> wires (shell 6, wire 3 next; 2026 files) or faces (shell 5, face 3 next; older files, which
        // keep a profile as a sheet) -> loops (face 4, loop 3 next) -> coedges (wire or loop field 4).
        static List<SabRecord> Rings(List<SabRecord> recs, SabRecord body)
        {
            List<SabRecord> firsts = new List<SabRecord>();
            HashSet<SabRecord> seen = new HashSet<SabRecord>();
            Action<SabRecord> add = c => { if (c != null && c.Type == "coedge" && seen.Add(c)) firsts.Add(c); };
            for (SabRecord lump = At(recs, body.Ref(3)); lump != null && lump.Type == "lump" && seen.Add(lump); lump = At(recs, lump.Ref(3)))
                for (SabRecord shell = At(recs, lump.Ref(4)); shell != null && shell.Type == "shell" && seen.Add(shell); shell = At(recs, shell.Ref(3)))
                {
                    for (SabRecord wire = At(recs, shell.Ref(6)); wire != null && wire.Type == "wire" && seen.Add(wire); wire = At(recs, wire.Ref(3)))
                        add(At(recs, wire.Ref(4)));
                    for (SabRecord face = At(recs, shell.Ref(5)); face != null && face.Type == "face" && seen.Add(face); face = At(recs, face.Ref(3)))
                        for (SabRecord loop = At(recs, face.Ref(4)); loop != null && loop.Type == "loop" && seen.Add(loop); loop = At(recs, loop.Ref(3)))
                            add(At(recs, loop.Ref(4)));
                }
            return firsts;
        }

        // A loop in the truth's form: its sketch entities as "line (x1, y1) (x2, y2)" (ends sorted) or "circle (cx, cy) r",
        // sketch coordinates in mm, sorted.
        public static string Text(NodeTable t, Loop loop)
        {
            List<string> es = new List<string>();
            foreach (int e in loop.Entities)
            {
                double[] g = Sketches.Geometry2D(t, e);
                string s;
                if (g != null && g.Length == 4)
                {
                    string a = Xy(g[0], g[1]), b = Xy(g[2], g[3]);
                    s = "line " + (string.CompareOrdinal(a, b) <= 0 ? a + " " + b : b + " " + a);
                }
                else if (g != null && g.Length == 3) s = "circle " + Xy(g[0], g[1]) + " " + N(10 * g[2]);
                else s = Curves.ApiType(t, t.At(e));
                if (!es.Contains(s)) es.Add(s);
            }
            es.Sort(string.CompareOrdinal);
            return string.Join(", ", es.ToArray());
        }

        // A path adds material when bit 0x80 of its header byte +0x0D is set (0xA1); a hole has 0x21.
        public static bool AddsMaterial(NodeTable t, Loop l)
        {
            Node n = t.At(l.PathNode);
            return n != null && n.Data != null && n.Data.Length > 0x0D && (n.Data[0x0D] & 0x80) != 0;
        }

        // A profile in the truth's form: each loop as "+" (adds material) or "-" then its entities; loops sorted, "; ".
        // Null if any loop could not be read.
        public static string Describe(NodeTable t, int profile, int sketchNode, Dictionary<string, int> items)
        {
            List<string> paths = new List<string>();
            foreach (Loop l in Loops(t, profile, sketchNode, items))
            {
                if (l.Problem != null || l.Entities.Count != l.Items.Count) return null;
                paths.Add((AddsMaterial(t, l) ? "+" : "-") + Text(t, l));
            }
            if (paths.Count == 0) return null;
            paths.Sort(string.CompareOrdinal);
            return string.Join("; ", paths.ToArray());
        }

        // Every profile in a document: its sketch, and its loops with each path node's bytes after the header.
        public static string Report(NodeTable t)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<string, int> items = Naming.SketchItems(t);
            Dictionary<int, string> names = Sketches.BrowserNames(t);
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Type.Key != Features.Profile || n.Data == null) continue;
                int sketch = SketchOf(t, n.Index);
                string sn;
                if (!names.TryGetValue(sketch, out sn)) sn = "sketch #" + sketch.ToString("X");
                sb.AppendFormat("  profile #{0:X} in {1}: {2}\n", n.Index, sn, Hex(n.Data, 0x1A + Sketches.Shift(n.Data), 8));
                foreach (Loop l in Loops(t, n.Index, sketch, items))
                {
                    Node pn = t.At(l.PathNode);
                    sb.AppendFormat("    path #{0:X} [{1}]: {2}{3}\n", l.PathNode, pn == null || pn.Data == null ? "" : Hex(pn.Data, 0x06, pn.Data.Length - 6),
                        Text(t, l), l.Problem == null ? "" : " (" + l.Problem + ")");
                }
            }
            return sb.ToString();
        }

        static string Hex(byte[] d, int from, int count)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = from; i < from + count && i < d.Length; i++) sb.Append(d[i].ToString("X2")).Append(i + 1 < from + count ? " " : "");
            return sb.ToString();
        }

        // The sketch a profile belongs to: the link node (F9884C43) that cross-refs both.
        public static int SketchOf(NodeTable t, int profile)
        {
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Type.Key != Features.ProfileLink || n.Data == null) continue;
                int sketch = -1;
                bool mine = false;
                for (int p = 0; p + 4 <= n.Data.Length; p++)
                {
                    int x = Sketches.XRef(n.Data, p);
                    if (x == profile) mine = true;
                    Node target = t.At(x);
                    if (target != null && target.Type != null && target.Type.Key == Sketches.Sketch2D) sketch = target.Index;
                }
                if (mine && sketch >= 0) return sketch;
            }
            return -1;
        }

        static string N(double v) { return (Math.Round(v, 6) + 0.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture); }

        static string Xy(double x, double y) { return "(" + N(10 * x) + ", " + N(10 * y) + ")"; }
    }
}
