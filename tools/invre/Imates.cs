// iMates. Each is split over two segments, tied by a 16-byte id:
//   DC segment: 38C2654E: +0x1E u32 kind (0 mate, 1 flush, 2 angle, 3 tangent, 4 insert, 8 composite), +0x22 the id. The document's root
//     node (90874D63) lists {offset Parameter, geometry, id node, 38C2654E} one after another for each.
//   App segment: a node with the same id at +0x06, its type depending on the kind (55231213 mate, FD1F3F21 flush, 6D8A4AC7 angle,
//     6D8A4AC8 tangent, 6D8A4AC9 insert, 81AFC10F composite):
//     +0x16 u32, u32 1, {u32 n, UTF-16 name} (empty for a default name, "iMate:1"), then q:
//     q+0 u32 the default name's number, q+4 u32 kind, q+0x0C u32 creation order, q+0x10 u32 suppressed, q+0x14 u32 n and n {u32 length, UTF-16} the match
//     list, then a composite has a kind-2 list of its members' app nodes; the others u32 geometry code (0x0D plane, 0x08 a line, 0x0E
//     cylinder, 0x09 circle, 0x14 angle), f64 value (cm, radians), a copy of the geometry, and last a byte: angle reversed, tangent
//     outside, insert axes opposed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InvRe
{
    public class ImateInfo
    {
        public string Id, Name, Word;
        public int Kind, Number, Position, Order, Code;   // Number: the one in its default name, counted within its kind
        public bool Suppressed, Flag, Member;
        public double Value;
        public List<string> MatchList = new List<string>();
        public List<string> Members = new List<string>();
        public Node App, Geometry, IdNode;
        public string Formula;          // the value's formula, from the Parameter listed before its geometry
    }

    public static class Imates
    {
        public const uint IdType = 0x38C2654E, Argument = 0x1CAEFC09, Result = 0x8324D282;
        static readonly string[] Words = { "iMate", "iFlush", "iAngle", "iTangent", "iInsert" };
        static readonly Dictionary<int, string> Types = new Dictionary<int, string> {
            { 0, "kMateiMateDefinitionObject" }, { 1, "kFlushiMateDefinitionObject" }, { 2, "kAngleiMateDefinitionObject" }, { 3, "kTangentiMateDefinitionObject" }, { 4, "kInsertiMateDefinitionObject" }, { 8, "kCompositeiMateDefinitionObject" } };

        public static string IdAt(byte[] d, int p)
        {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < 16; i++) b.Append(d[p + i].ToString("X2"));
            return b.ToString();
        }

        // The iMate ids in a document, each with the nodes the root lists just before it.
        static Dictionary<string, KeyValuePair<Node, Node[]>> Ids(NodeTable dc)
        {
            Dictionary<string, KeyValuePair<Node, Node[]>> r = new Dictionary<string, KeyValuePair<Node, Node[]>>();
            Node root = null;
            foreach (Node n in dc.Nodes) if (n.Type != null && n.Type.Key == 0x90874D63 && n.Data != null) { root = n; break; }
            if (root == null) return r;
            List<Node> listed = new List<Node>();
            for (int p = 0x0A; p + 16 <= root.Data.Length; p++)
            {
                if (BitConverter.ToUInt32(root.Data, p) != 0x30000002) continue;
                int size = BitConverter.ToInt32(root.Data, p + 4);
                if (size <= 0 || p + 16 + 4 * (long)size > root.Data.Length) continue;
                List<int> list;
                Sketches.ReadList(root.Data, p, out list);
                List<Node> nodes = list.ConvertAll(x => dc.At(x));
                if (!nodes.Exists(x => x != null && x.Type != null && x.Type.Key == IdType)) continue;
                listed = nodes;
                break;
            }
            for (int i = 0; i < listed.Count; i++)
            {
                Node n = listed[i];
                if (n == null || n.Type == null || n.Type.Key != IdType || n.Data == null) continue;
                r[IdAt(n.Data, n.Data.Length - 16)] = new KeyValuePair<Node, Node[]>(n, new[] { i > 0 ? listed[i - 1] : null, i > 1 ? listed[i - 2] : null, i > 2 ? listed[i - 3] : null });
            }
            return r;
        }

        // A part's iMate names its entity in the part's own table: the id node just before the 38C2654E holds {table, entry}.
        static bool NameOf(NodeTable dc, Node idNode, out Node table, out int entry)
        {
            table = null;
            entry = 0;
            if (idNode == null || idNode.Data == null) return false;
            for (int p = 0x16; p + 8 <= idNode.Data.Length; p++)
            {
                if (idNode.Data[p + 3] != 0x80) continue;
                Node x = dc.At(Sketches.XRef(idNode.Data, p));
                if (x == null || x.Type == null || x.Type.Key != Naming.Table) continue;
                table = x;
                entry = BitConverter.ToInt32(idNode.Data, p + 4);
                return true;
            }
            return false;
        }

        // What the iMate with this id grips in a part: for an iMate joint's argument, which names the iMate by its id.
        public static Naming.Grip GripById(NodeTable dc, byte[] brep, string id)
        {
            KeyValuePair<Node, Node[]> kv;
            Node table;
            int entry;
            if (!Ids(dc).TryGetValue(id, out kv) || !NameOf(dc, kv.Value[0], out table, out entry)) return null;
            return Naming.ResolveGrip(dc, table, entry, dc, brep);
        }

        public static List<ImateInfo> Read(NodeTable dc, NodeTable app, NodeTable browser)
        {
            List<ImateInfo> r = new List<ImateInfo>();
            if (dc == null || app == null) return r;
            Dictionary<string, KeyValuePair<Node, Node[]>> ids = Ids(dc);
            if (ids.Count == 0) return r;
            Dictionary<int, string> formulas = new Dictionary<int, string>();
            foreach (Params.Decoded p in Params.Decode(dc)) formulas[p.Index] = p.Formula;
            Dictionary<int, ImateInfo> byApp = new Dictionary<int, ImateInfo>();
            foreach (Node a in app.Nodes)
            {
                if (a.Data == null || a.Data.Length < 0x22) continue;
                string id = IdAt(a.Data, 6);
                KeyValuePair<Node, Node[]> kv;
                if (!ids.TryGetValue(id, out kv)) continue;
                byte[] d = a.Data;
                ImateInfo m = new ImateInfo { Id = id, App = a };
                int len = BitConverter.ToInt32(d, 0x1E), q = 0x22 + 2 * len;
                m.Name = len > 0 ? Encoding.Unicode.GetString(d, 0x22, 2 * len) : null;
                m.Number = BitConverter.ToInt32(d, q);
                m.Kind = BitConverter.ToInt32(d, q + 4);
                m.Order = BitConverter.ToInt32(d, q + 0x0C);
                m.Suppressed = BitConverter.ToInt32(d, q + 0x10) != 0;
                int count = BitConverter.ToInt32(d, q + 0x14), p = q + 0x18;
                for (int i = 0; i < count; i++)
                {
                    int n = BitConverter.ToInt32(d, p);
                    m.MatchList.Add(Encoding.Unicode.GetString(d, p + 4, 2 * n));
                    p += 4 + 2 * n;
                }
                m.Word = m.Kind >= 0 && m.Kind < Words.Length ? Words[m.Kind] : "iComposite";
                if (m.Kind == 8)
                {
                    List<int> members;
                    Sketches.ReadList(d, p, out members);
                    m.Members = members.ConvertAll(x => x.ToString());
                }
                else
                {
                    m.Code = BitConverter.ToInt32(d, p);
                    m.Value = BitConverter.ToDouble(d, p + 4);
                    m.Flag = d[d.Length - 1] != 0;
                    Node[] before = kv.Value;
                    if (before[0] != null && before[0].TypeName != "Parameter" && before[0].Type.Key != IdType) m.IdNode = before[0];
                    if (before[1] != null && before[1].TypeName != "Parameter" && before[1].Type.Key != IdType) m.Geometry = before[1];
                    if (before[2] != null && before[2].TypeName == "Parameter") formulas.TryGetValue(before[2].Index, out m.Formula);
                }
                byApp[a.Index] = m;
                r.Add(m);
            }
            r.Sort((x, y) => x.Order.CompareTo(y.Order));
            foreach (ImateInfo m in r)
            {
                if (m.Name == null) m.Name = m.Word + ":" + m.Number;
            }
            foreach (ImateInfo m in r)
            {
                if (m.Kind != 8) continue;
                List<string> names = new List<string>();
                foreach (string s in m.Members)
                {
                    ImateInfo k;
                    if (byApp.TryGetValue(int.Parse(s), out k)) { k.Member = true; names.Add(k.Name); }
                }
                m.Members = names;
            }
            // Its place in the list the API gives: composites' members aren't in it.
            int position = 0;
            foreach (ImateInfo m in r) if (!m.Member) m.Position = ++position;
            return r;
        }

        // A work feature an iMate sits on: its id node is 0C48B861 (plane), A76B22A0 (axis) or 0C48B860 (point), +0x24 the feature's node.
        public static string WorkOf(NodeTable dc, ImateInfo m)
        {
            Node id = m.IdNode;
            if (id == null || id.Data == null || id.Type == null || (id.Type.Key != 0x0C48B861 && id.Type.Key != 0xA76B22A0 && id.Type.Key != 0x0C48B860)) return null;
            Node feature = dc.At(Sketches.XRef(id.Data, 0x24 + Sketches.Shift(id.Data)));
            string name = null;
            if (feature != null) Sketches.BrowserNames(dc).TryGetValue(feature.Index, out name);
            return (id.Type.Key == 0x0C48B861 ? "kWorkPlaneObject " : id.Type.Key == 0xA76B22A0 ? "kWorkAxisObject " : "kWorkPointObject ") + name;
        }

        // What an iMate grips (null where it has no name). A part's names are in its own table; an assembly's iMate is on an occurrence,
        // and is looked up in that part (inPart, set by whoever knows the occurrences).
        public static Naming.Grip GripOf(NodeTable dc, byte[] brep, ImateInfo m, Func<ImateInfo, Naming.Grip> inPart = null)
        {
            if (inPart != null) return inPart(m);
            Node table;
            int entry;
            if (brep == null || !NameOf(dc, m.IdNode, out table, out entry)) return null;
            Naming.Grip g = Naming.ResolveGrip(dc, table, entry, dc, brep);
            // An edge named by two faces, where the name doesn't lead to one edge: chosen among their edges by the geometry.
            if (g.Found.Count == 0 && m.Geometry != null)
            {
                string geometry = Assemblies.GeometryOf(m.Geometry, null);
                g.Found = g.Nearby.FindAll(x => Naming.Carries(x, geometry, false));
                // Or the file's copy of the geometry, among all the part's entities of that kind (the name can't be followed in every older part).
                if (g.Found.Count == 0 && (g.Kind == "edge" || g.Kind == "face"))
                {
                    string error;
                    List<SabRecord> records = Sab.Read(brep, 0, out error);
                    foreach (SabRecord r in records)
                        if (r.Type == g.Kind && !r.Old)
                        {
                            Naming.Match candidate = new Naming.Match { Records = records, Entity = r };
                            if (Naming.Carries(candidate, geometry, false)) g.Found.Add(candidate);
                        }
                    g.ByPosition = g.Found.Count > 0;
                }
            }
            return g;
        }

        public static string Report(NodeTable dc, NodeTable app, NodeTable browser, byte[] brep)
        {
            StringBuilder b = new StringBuilder();
            foreach (ImateInfo m in Read(dc, app, browser))
            {
                b.AppendFormat("  {0}: {1}{2}{3}", m.Name, Types.ContainsKey(m.Kind) ? Types[m.Kind] : "kind " + m.Kind, m.Suppressed ? " suppressed" : "", m.Member ? " (in a composite)" : "");
                if (m.Kind == 8) b.Append(" of " + string.Join(", ", m.Members.ToArray()));
                else
                {
                    b.Append(" = " + (m.Formula ?? m.Value.ToString("R", CultureInfo.InvariantCulture)));
                    if (m.Kind == 2 && m.Flag) b.Append(", reversed");
                    if (m.Kind == 3) b.Append(m.Flag ? ", outside" : ", inside");
                    if (m.Kind == 4 && m.Flag) b.Append(", axes opposed");
                    if (m.Code == 8) b.Append(", as a line");
                }
                if (m.MatchList.Count > 0) b.Append("; matches " + string.Join(", ", m.MatchList.ToArray()));
                b.Append("\n");
                string work = WorkOf(dc, m);
                if (work != null) b.AppendFormat("      grips {0}\n", work);
                Naming.Grip g = GripOf(dc, brep, m);
                if (g != null) b.AppendFormat("      grips {0} ({1}): {2}\n", g.Kind, g.EntryType, g.Found.Count == 0 ? "not found; the geometry is " + (m.Geometry == null ? "?" : Assemblies.GeometryOf(m.Geometry, null)) + ", " + g.Nearby.Count + " edges nearby" : Sab.Where(g.Found[0].Records, g.Found[0].Entity) + (g.Found.Count > 1 ? " and " + (g.Found.Count - 1) + " more" : ""));
            }
            return b.ToString();
        }

        // Decoded iMates vs the truth's "imates". The API prints "0 cm" where nothing was typed; numbers compare by value.
        public static List<string> Compare(NodeTable dc, NodeTable app, NodeTable browser, byte[] brep, string truth, out int compared, out int grips, Func<ImateInfo, Naming.Grip> inPart = null)
        {
            compared = 0;
            grips = 0;
            List<string> bad = new List<string>();
            int start = truth.IndexOf("\"imates\"", StringComparison.Ordinal);
            if (start < 0) return bad;
            int end = truth.IndexOf("\"imateResults\"", start, StringComparison.Ordinal);
            if (end < 0) end = truth.Length;
            List<ImateInfo> all = Read(dc, app, browser);
            HashSet<string> seen = new HashSet<string>();
            foreach (string raw in truth.Substring(start, end - start).Split('\n'))
            {
                string line = raw.Trim(), name = Assemblies.Field(line, "name");
                if (name == null) continue;
                compared++;
                seen.Add(name);
                ImateInfo m = all.Find(x => x.Name == name);
                if (m == null) { bad.Add(name + ": not decoded"); continue; }
                Action<string, string, string> same = (what, decoded, api) => { if (api != null && decoded != api) bad.Add(name + " " + what + ": Inventor " + api + ", decoded " + decoded); };
                same("type", Types.ContainsKey(m.Kind) ? Types[m.Kind] : "kind " + m.Kind, Assemblies.Field(line, "type"));
                same("position", m.Position.ToString(), Assemblies.Field(line, "index"));
                same("suppressed", m.Suppressed ? "true" : "false", Assemblies.Field(line, "suppressed"));
                same("match list", string.Join(",", m.MatchList.ToArray()), Assemblies.Field(line, "matchList"));
                same("members", string.Join(",", m.Members.ToArray()), Assemblies.Field(line, "members"));
                if (Assemblies.Field(line, "inferred") != null) same("inferred", m.Code == 8 ? "kInferredLine" : "kNoInference", Assemblies.Field(line, "inferred"));
                if (Assemblies.Field(line, "inside") != null) same("inside", m.Flag ? "false" : "true", Assemblies.Field(line, "inside"));
                if (Assemblies.Field(line, "opposed") != null) same("opposed", m.Flag ? "true" : "false", Assemblies.Field(line, "opposed"));
                if (Assemblies.Field(line, "reversed") != null) same("reversed", m.Flag ? "true" : "false", Assemblies.Field(line, "reversed"));
                foreach (string key in new[] { "offset", "angle", "distance" })
                {
                    string api = Assemblies.Field(line, key);
                    if (api == null) continue;
                    double v;
                    string[] w = api.Split(' ');
                    double scale = w.Length > 1 && w[1] == "mm" ? 0.1 : w.Length > 1 && w[1] == "deg" ? Math.PI / 180 : 1;
                    if (w.Length == 2 && double.TryParse(w[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v) && Math.Abs(v * scale - m.Value) > 1e-9) bad.Add(name + " " + key + ": Inventor " + api + ", decoded " + m.Value);
                    if (m.Formula != null && w.Length == 2 && !(w[0] == "0" && w[1] == "cm") && m.Formula != api && !double.TryParse(w[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) bad.Add(name + " " + key + " formula: Inventor " + api + ", decoded " + m.Formula);
                }
                string grip = Assemblies.Field(line, "grip");
                if (grip == null) continue;
                grips++;
                string work = WorkOf(dc, m);
                if (work != null) { if (work != grip) bad.Add(name + " grip: Inventor " + grip + ", decoded " + work); continue; }
                Naming.Grip g = GripOf(dc, brep, m, inPart);
                if (g == null) bad.Add(name + " grip: Inventor " + grip + ", decoded nothing (geometry " + (m.Geometry == null ? "none" : m.Geometry.TypeName) + ")");
                else if (g.Found.Count == 0) bad.Add(name + " grip: Inventor " + grip + ", decoded " + g.Kind + " " + g.EntryType + ", nothing found");
                else if (!g.Found.Exists(x => Naming.SameAsTruth(x, grip))) bad.Add(name + " grip: Inventor " + grip + ", decoded " + Sab.Where(g.Found[0].Records, g.Found[0].Entity));
            }
            foreach (ImateInfo m in all) if (!m.Member && !seen.Contains(m.Name)) bad.Add(m.Name + ": decoded, not in Inventor's list");
            return bad;
        }
    }
}
