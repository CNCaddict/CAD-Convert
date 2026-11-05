// invre: Inventor file reverse-engineering harness.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InvRe
{
    public static class Program
    {
        const string Usage =
@"invre dump <file> [extractDir]         container tree; optionally extract raw streams
invre segs <file> [outDir]             segments with compression and sizes; optionally save decompressed data
invre strings <binFile> [minLen]       printable ASCII/UTF-16 strings with offsets
invre hex <binFile> <offset> [length]  hex + ASCII view (offset in hex, e.g. 11D5)
invre diff <fileA> <fileB> [segment] [--meta] [--max N]
                                       byte diff of decompressed segments (all, or one by name)
invre nodes <file> [segment]           list a segment's nodes (default: the DC segment)
invre node <file> <segment> <index>... hex of nodes (index in hex) with refs, strings, doubles
invre refs <file> <segment> <index>... which nodes reference the given nodes (index in hex)
invre occurrences <file>            an assembly's occurrences: name, file, placement, state
invre constraints <file> [folder [text]] an assembly's constraints and joints; with a folder, what each grips
invre nodeid <file> <segment> <id>... the nodes with these ids (the u16 at +0x04)
invre acheck <dir> / accheck <dir>  those against each truth .json under a folder
invre atruth <file> <truth> <folder> one assembly against a truth file, following nesting under the folder
invre ascan <dir> [--names|--misses] every assembly under a folder, checked against itself
invre arefs <file>                  an assembly's referenced files and parts-list rows
invre sfind <file> <text|hex:..>    where a text or bytes occur, in every stream and segment
invre curves <file>                 each sketch's splines, ellipses, text and images
invre cscan <dir>                   all of those under a folder, checked against their own points
invre ccheck <dir> | <file> <truth> those curves against the API's curves in truth files
invre cproj <file> <index>          a projected curve's ASM record and how its ends sit against its points
invre ndiff <fileA><fileB> [segment] [--full] [--max N]
                                       node-level diff: inserted/removed nodes and changed bytes per node
invre check <dir>                      split every segment of every .ipt/.iam under dir into nodes; report failures
invre sketch <file>                    2D sketches: points, lines, circles/arcs and constraints
invre compare <file> <truth.txt>       decoded sketch entities vs a saved 'truth --entities' listing
invre params <file>                    parameters with units and their formulas rebuilt from the expression nodes
invre units <file>                     every unit node, decoded
invre features <file>                  part features with their property slots
invre fcensus <dir>                    feature classes across all parts under dir, with example names
invre fslots <dir> <classHex>          what each property slot of one feature class holds, across dir
invre fcheck <dir>                     decoded extrudes, revolves and holes vs the truth .json next to each generated part
invre fcompare <file> <truth.json>     decoded extrudes, revolves and holes vs one truth file
invre fscan <dir> [--list]             decode every extrude, revolve and hole under dir; counts by kind, and what is still unknown
invre pcheck <dir>                     decoded parameters vs the truth .json next to each generated part
invre pcompare <file> <truth.txt>      decoded parameters vs a saved 'truth' listing
invre pscan <dir>                      decode every parameter under dir; list what is still unknown
invre enum <EnumName>                 values of an Inventor API enum (from the interop assembly)
invre api <TypeName>                  methods and properties of an Inventor API type (from the interop assembly)
invre brep <file> [segment]            every ASM (B-rep) record in a segment (default PmBRepSegment)
invre names <file> [segment]           B-rep faces, edges and vertices with where they are and their naming attributes
invre edges <file>                     the edges and faces each feature uses, resolved to the B-rep (with geometry)
invre echeck <dir> | <file> <truth>    resolved edges and faces vs what the generator picked / the API lists
invre escan <dir>                      how many feature edges and faces resolve, across dir
invre nentry <file> <index>...         naming table entries (decimal index), as node hex
invre ncensus <dir>                    naming attributes, naming table entries and the nodes using them, across dir
invre status                          connect to Inventor and report its state
invre gen <experiment|all> <outDir>    make controlled test parts with Inventor (+ truth .json)
invre truth <file> [--entities]        what the Inventor API reports for a file (opened, not saved);
                                       --entities also lists every sketch entity with coordinates
invre close-under <folder>             close (without saving) hidden documents the harness left open from that folder";

        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                if (args.Length == 0) { Console.WriteLine(Usage); return 1; }
                switch (args[0])
                {
                    case "dump": Dump(args[1], args.Length > 2 ? args[2] : null); break;
                    case "timeline":
                    {
                        // Features in Inventor's order, with their names; with a truth file, whether it lists them the same way.
                        NodeTable t = Table(FindSegment(Segment.ReadAll(args[1]), null));
                        Dictionary<int, string> names = Sketches.BrowserNames(t);
                        // Entries the browser doesn't list itself (put in by node order) are marked "+".
                        List<int> listed;
                        List<string> order = CadModel.Timeline(t, out listed).ConvertAll(i => { string n; return (listed.Contains(i) ? "" : "+") + (names.TryGetValue(i, out n) ? n : "#" + i.ToString("X")); });
                        Console.WriteLine(string.Join(", ", order.ToArray()));
                        if (args.Length > 2)
                        {
                            List<string> truth = new List<string>();
                            string text = File.ReadAllText(args[2]);
                            int at = text.IndexOf("\"features\"");
                            int end = text.IndexOf("\"sketches\"", at < 0 ? 0 : at);
                            if (at >= 0)
                                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(end > at ? text.Substring(at, end - at) : text.Substring(at), "\\{\"name\": \"([^\"]*)\", \"type\""))
                                    truth.Add(m.Groups[1].Value);
                            Console.WriteLine(string.Join(", ", truth.ToArray()));
                            Console.WriteLine(string.Join("|", truth.ToArray()) == string.Join("|", order.ToArray()) ? "same order" : "DIFFERENT");
                        }
                        break;
                    }
                    case "step":
                    {
                        // A part's final solid as STEP, from the file alone.
                        // With --made, the first body as it was made (an imported body before the features after it).
                        StepWriter w = new StepWriter { AsMade = args.Length > 3 && args[3] == "--made" };
                        List<Segment> all = Segment.ReadAll(args[1]);
                        w.SurfaceBodies = StepWriter.SurfaceFlags(all);
                        string text = w.Write(FindSegment(all, "PmBRepSegment").Data, Path.GetFileNameWithoutExtension(args[1]));
                        if (text == null) { Console.WriteLine("no solid in " + args[1]); return 1; }
                        if (w.AsMade) Console.WriteLine("as made in history state " + w.MadeState);
                        File.WriteAllText(args[2], text, new UTF8Encoding(false));
                        Console.WriteLine("{0} solids, {1} sheets, {2} faces, {3} edges, {4} vertices", w.Solids, w.Sheets, w.Faces, w.Edges, w.Vertices);
                        foreach (KeyValuePair<string, int> kv in w.NotCarried) Console.WriteLine("  not carried: {0} x {1}", kv.Value, kv.Key);
                        break;
                    }
                    case "convert": return Converter.Convert(args[1], args[2], Console.Out);
                    case "solidcraft": return Converter.ConvertSolidCraft(args[1], args[2], Console.Out);
                    case "solidcraft-part": return Converter.SolidCraftPart(args[1], args[2], Console.Out);
                    case "convert-all": Converter.ConvertAll(args[1], args[2], Console.Out); break;
                    case "finish":
                        // The finishing pass over a folder; with a number of minutes, only files changed that recently.
                        Converter.Finish(args[1], args.Length > 2 ? DateTime.UtcNow.AddMinutes(-double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)) : DateTime.MinValue, Console.Out);
                        break;
                    case "views": Converter.ExportViews(args[1], Console.Out); break;
                    case "preview": { string why = Converter.Preview(args[1], args[2]); if (why != null) { Console.WriteLine(why); return 1; } break; }
                    case "step-all": Converter.StepAll(args[1], args[2], Console.Out); break;
                    case "model":
                    {
                        List<Segment> segs = Segment.ReadAll(args[1]);
                        CadModel.Write(DcWithUnits(args[1], true), segs, args[1], args[2]);
                        break;
                    }
                    case "segs": Segs(args[1], args.Length > 2 ? args[2] : null); break;
                    case "strings": Console.Write(Text.Strings(File.ReadAllBytes(args[1]), args.Length > 2 ? int.Parse(args[2]) : 4, 100000)); break;
                    case "hex": HexView(File.ReadAllBytes(args[1]), Convert.ToInt32(args[2], 16), args.Length > 3 ? Convert.ToInt32(args[3], 16) : 0x100); break;
                    case "diff": DiffCmd(args); break;
                    case "nodes": NodesCmd(args[1], args.Length > 2 ? args[2] : null); break;
                    case "node": NodeCmd(args); break;
                    case "refs": RefsCmd(args); break;
                    case "ndiff": NodeDiffCmd(args); break;
                    case "enum": Console.Write(InventorDriver.EnumValues(args[1])); break;
                    case "api": Console.Write(InventorDriver.Members(args[1])); break;
                    case "brep": BrepCmd(args[1], args.Length > 2 ? args[2] : "PmBRepSegment", false); break;
                    case "names": BrepCmd(args[1], args.Length > 2 ? args[2] : "PmBRepSegment", true); break;
                    case "ncensus": NamingCensusCmd(args[1]); break;
                    case "escan": EdgeScanCmd(args[1]); break;
                    case "explain":
                    {
                        List<Segment> segs = Segment.ReadAll(args[1]);
                        Console.Write(Naming.Explain(Table(FindSegment(segs, null)), FindSegment(segs, "PmBRepSegment").Data, Convert.ToInt32(args[2], 16)));
                        break;
                    }
                    case "echeck": EdgeCheckCmd(args[1], args.Length > 2 ? args[2] : null); break;
                    case "items":
                    {
                        // Sketch items: sketch, item number, entity.
                        NodeTable t = Table(FindSegment(Segment.ReadAll(args[1]), null));
                        Dictionary<int, string> names = Sketches.BrowserNames(t);
                        List<KeyValuePair<string, int>> all = new List<KeyValuePair<string, int>>(Naming.SketchItems(t));
                        all.Sort((a, b) => { string[] x = a.Key.Split(' '), y = b.Key.Split(' '); int c = int.Parse(x[0]).CompareTo(int.Parse(y[0])); return c != 0 ? c : int.Parse(x[1]).CompareTo(int.Parse(y[1])); });
                        foreach (KeyValuePair<string, int> kv in all)
                        {
                            string[] k = kv.Key.Split(' ');
                            string sk;
                            if (!names.TryGetValue(int.Parse(k[0]), out sk)) sk = "#" + int.Parse(k[0]).ToString("X");
                            if (args.Length > 2 && sk != args[2]) continue;
                            Console.WriteLine("  {0,-10} item {1,4}: #{2:X} {3}", sk, k[1], kv.Value, t.At(kv.Value).TypeName);
                        }
                        break;
                    }
                    case "swept":
                    {
                        // B-rep entities one sweep feature made (by its tag): item, flags, where.
                        byte[] brep = FindSegment(Segment.ReadAll(args[1]), "PmBRepSegment").Data;
                        string error;
                        List<SabRecord> records = Sab.Read(brep, 0, out error);
                        SortedSet<string> lines = new SortedSet<string>();
                        foreach (SabRecord a in records)
                        {
                            if (a.Fields.Count < 12 || (a.Fields[5] as string) != "INV_NMX_SWEEPGENERATED_TAG") continue;
                            int n0 = a.Int(6), i = 7 + n0, m = a.Int(i);
                            if (m != 1 || a.Int(i + 1) != int.Parse(args[2])) continue;
                            SabRecord o = records[a.Ref(4).Target];
                            lines.Add(string.Format("{0,-6} item {1,4} flags {2,5}: {3}{4}", o.Type, a.Int(i + 3), a.Int(i + 5), Sab.Where(records, o), o.Old ? " *" : ""));
                        }
                        foreach (string l in lines) Console.WriteLine("  " + l);
                        break;
                    }
                    case "fstate":
                    {
                        // Each feature's state byte (+0x0A) next to the health a truth file reports.
                        List<Segment> fsegs = Segment.ReadAll(args[1]);
                        NodeTable t = Table(FindSegment(fsegs, null));
                        byte[] fbrep = FindSegment(fsegs, "PmBRepSegment").Data;
                        string json = args.Length > 2 ? File.ReadAllText(args[2]) : "";
                        foreach (Features.Info fi in Features.List(t))
                        {
                            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(json,
                                "\"name\": \"" + System.Text.RegularExpressions.Regex.Escape(fi.Name ?? "") + "\"[^}]*\"health\": \"(\\w+)\"");
                            // Fillets and chamfers: whether the B-rep holds faces they made.
                            string made = fi.Kind == 0xF1F715DC || fi.Kind == 0xF900713F ? (Naming.MadeFaces(fbrep, fi.Tag) ? "made faces" : "made no faces") : "";
                            Console.WriteLine("  {0,-14} tag {1,-6} state {2:X2}  {3,-14} {4}", fi.Name, fi.Tag, fi.State, made, m.Success ? m.Groups[1].Value : "");
                        }
                        // Mirrors and patterns, with the whole node's leading bytes: where a failing one might differ.
                        foreach (Patterns.PatternInfo p in Patterns.List(t))
                        {
                            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(json,
                                "\"name\": \"" + System.Text.RegularExpressions.Regex.Escape(p.Name ?? "") + "\"[^}]*\"health\": \"(\\w+)\"");
                            byte[] d = t.At(p.Index).Data;
                            Console.WriteLine("  {0,-14} state {1:X2}  {2,-18} bytes {3}", p.Name, p.State, m.Success ? m.Groups[1].Value : "", BitConverter.ToString(d, 0, Math.Min(0x20, d.Length)));
                        }
                        break;
                    }
                    case "topo":
                    {
                        // For edges named by two faces (BF32E0A6): which kinds of entries name those faces, across a folder.
                        SortedDictionary<string, int> pairs = new SortedDictionary<string, int>();
                        foreach (string f in Directory.GetFiles(args[1], "*.ipt", SearchOption.AllDirectories))
                        {
                            NodeTable t;
                            try { t = Table(FindSegment(Segment.ReadAll(f), null)); } catch (Exception) { continue; }
                            Node table = Naming.MainTable(t);
                            if (table == null) continue;
                            Dictionary<int, int> entries = Naming.Entries(table);
                            foreach (int node in entries.Values)
                            {
                                Node e = t.At(node);
                                if (e == null || e.Type == null || e.Type.Key != 0xBF32E0A6) continue;
                                List<int> refs = Naming.EntryRefs(t, e);
                                List<string> kinds = new List<string>();
                                for (int i = 2; i < refs.Count && i < 4; i++) { int fn; kinds.Add(entries.TryGetValue(refs[i], out fn) ? t.At(fn).TypeName : "missing"); }
                                kinds.Sort();
                                string k = string.Join(" + ", kinds.ToArray());
                                int c; pairs.TryGetValue(k, out c); pairs[k] = c + 1;
                            }
                        }
                        foreach (KeyValuePair<string, int> kv in pairs) Console.WriteLine("  {0,6} x {1}", kv.Value, kv.Key);
                        break;
                    }
                    case "rcheck":
                    {
                        // Rebuilt-from-sketch geometry vs the B-rep, wherever both exist.
                        List<string> bad = new List<string>();
                        SortedDictionary<string, int> byRole = new SortedDictionary<string, int>();
                        int n = 0, ok = 0;
                        List<string> files = Directory.Exists(args[1]) ? new List<string>(Directory.GetFiles(args[1], "*.ipt", SearchOption.AllDirectories)) : new List<string> { args[1] };
                        foreach (string f in files)
                        {
                            List<Segment> segs;
                            try { segs = Segment.ReadAll(f); } catch (Exception) { continue; }
                            List<string> b = new List<string>();
                            Naming.RebuildCheck(Table(FindSegment(segs, null)), FindSegment(segs, "PmBRepSegment").Data, b, ref ok, ref n, byRole);
                            foreach (string x in b) bad.Add(Path.GetFileName(f) + ": " + x);
                        }
                        Console.WriteLine("{0} checks of inputs found in the B-rep against another way of finding them; {1} agree", n, ok);
                        SortedDictionary<string, int> failed = new SortedDictionary<string, int>();
                        List<string> keys = new List<string>(byRole.Keys);
                        keys.Sort((a, b) => b.Length.CompareTo(a.Length));   // the longest way name that fits
                        foreach (string x in bad)
                            foreach (string k in keys)
                                if (x.Substring(x.IndexOf(": ") + 2).StartsWith(k)) { int c; failed.TryGetValue(k, out c); failed[k] = c + 1; break; }
                        foreach (KeyValuePair<string, int> kv in byRole)
                        {
                            int f;
                            failed.TryGetValue(kv.Key, out f);
                            Console.WriteLine("  {0,5} x {1}{2}", kv.Value, kv.Key, f == 0 ? "" : " (" + f + " disagree)");
                        }
                        // An optional third argument shows only the disagreements containing it, all of them.
                        if (args.Length > 2) bad = bad.FindAll(x => x.Contains(args[2]));
                        foreach (string x in bad.GetRange(0, Math.Min(args.Length > 2 ? bad.Count : 15, bad.Count))) Console.WriteLine("  " + x);
                        if (bad.Count > 15 && args.Length <= 2) Console.WriteLine("  ... {0} more", bad.Count - 15);
                        break;
                    }
                    case "copycheck":
                    {
                        // Every sweep-named B-rep entity rebuilt from its name, mirror and pattern copies included.
                        List<string> bad = new List<string>();
                        SortedDictionary<string, int> byWay = new SortedDictionary<string, int>();
                        int n = 0, ok = 0;
                        List<string> files = Directory.Exists(args[1]) ? new List<string>(Directory.GetFiles(args[1], "*.ipt", SearchOption.AllDirectories)) : new List<string> { args[1] };
                        foreach (string f in files)
                        {
                            List<Segment> segs;
                            try { segs = Segment.ReadAll(f); } catch (Exception) { continue; }
                            List<string> b = new List<string>();
                            Naming.CopyCheck(Table(FindSegment(segs, null)), FindSegment(segs, "PmBRepSegment").Data, b, ref ok, ref n, byWay);
                            foreach (string x in b) bad.Add(Path.GetFileName(f) + ": " + x);
                        }
                        Console.WriteLine("{0} named B-rep entities rebuilt from their names; {1} agree with the B-rep", n, ok);
                        foreach (KeyValuePair<string, int> kv in byWay)
                        {
                            int f = bad.FindAll(x => x.Substring(x.IndexOf(": ") + 2).StartsWith(kv.Key + " ")).Count;
                            Console.WriteLine("  {0,5} x {1}{2}", kv.Value, kv.Key, f == 0 ? "" : " (" + f + " disagree)");
                        }
                        foreach (string x in bad.GetRange(0, Math.Min(15, bad.Count))) Console.WriteLine("  " + x);
                        if (bad.Count > 15) Console.WriteLine("  ... {0} more", bad.Count - 15);
                        break;
                    }
                    case "fillet-check":
                    {
                        List<string> failures = new List<string>();
                        List<double> ratios = new List<double>();
                        int n = 0, ok = 0;
                        foreach (string f in Directory.GetFiles(args[1], "*.ipt", SearchOption.AllDirectories))
                        {
                            List<Segment> segs;
                            try { segs = Segment.ReadAll(f); } catch (Exception) { continue; }
                            List<string> bad = new List<string>();
                            Naming.FilletCheck(Table(FindSegment(segs, null)), FindSegment(segs, "PmBRepSegment").Data, bad, ref n, ref ok, ratios);
                            foreach (string b in bad) failures.Add(Path.GetFileName(f) + ": " + b);
                        }
                        ratios.Sort();
                        int cannot = failures.FindAll(x => x.Contains("(can't check)")).Count;
                        failures = failures.FindAll(x => !x.Contains("(can't check)"));
                        Console.WriteLine("{0} resolved straight fillet edges checked; {1} have a parallel fillet cylinder of their radius{2}", n, ok,
                            ratios.Count == 0 ? "" : string.Format(" (axis distance / radius: min {0:0.###}, median {1:0.###}, max {2:0.###})", ratios[0], ratios[ratios.Count / 2], ratios[ratios.Count - 1]));
                        Console.WriteLine("{0} more could not be checked: their fillet made no cylinders (tori, splines)", cannot);
                        foreach (string b in failures.GetRange(0, Math.Min(20, failures.Count))) Console.WriteLine("  " + b);
                        if (failures.Count > 20) Console.WriteLine("  ... {0} more", failures.Count - 20);
                        break;
                    }
                    case "nentry":
                    {
                        // Naming table entries by index (decimal): their nodes in hex.
                        Dictionary<int, int> entries = Naming.Entries(Naming.MainTable(Table(FindSegment(Segment.ReadAll(args[1]), null))));
                        List<string> a = new List<string> { "node", args[1], "PmDCSegment" };
                        for (int i = 2; i < args.Length; i++) { int n; if (entries.TryGetValue(int.Parse(args[i]), out n)) a.Add(n.ToString("X")); }
                        NodeCmd(a.ToArray());
                        break;
                    }
                    case "edges":
                    {
                        List<Segment> segs = Segment.ReadAll(args[1]);
                        Console.Write(Naming.Report(Table(FindSegment(segs, null)), FindSegment(segs, "PmBRepSegment").Data));
                        break;
                    }
                    case "check": CheckCmd(args[1]); break;
                    case "compare": Console.Write(Sketches.Compare(Table(FindSegment(Segment.ReadAll(args[1]), null)), File.ReadAllText(args[2]))); break;
                    case "sketch":Console.Write(Sketches.Report(Table(FindSegment(Segment.ReadAll(args[1]), null)), new List<Sketches.Counts>())); break;
                    case "params": Console.Write(Params.Report(DcWithUnits(args[1]))); break;
                    case "units": Console.Write(Units.Report(DcWithUnits(args[1]))); break;
                    case "pcheck": ParamCheckCmd(args[1]); break;
                    case "pscan": ParamScanCmd(args[1]); break;
                    case "features": Console.Write(Features.Report(DcWithUnits(args[1]))); break;
                    case "fcensus": Console.Write(Features.Census(args[1])); break;
                    case "fslots": Console.Write(Features.Slots(args[1], Convert.ToUInt32(args[2], 16))); break;
                    case "pslots": Console.Write(Patterns.Slots(DcWithUnits(args[1], true))); break;
                    case "loops": Console.Write(Profiles.Report(Table(FindSegment(Segment.ReadAll(args[1]), null)))); break;
                    case "sfind": FindText(args[1], args[2]); break;
                    case "occurrences": Console.Write(Assemblies.Report(ReadOccurrences(args[1]))); break;
                    case "constraints":
                    {
                        // With a folder, also what each grips in the part files under it; with a filter too, how each was found.
                        if (args.Length > 2) FindReferencedUnder(args[2]);
                        if (args.Length > 3) Assemblies.Explaining = new StringBuilder();
                        List<Segment> all = Segment.ReadAll(args[1]);
                        List<Occurrence> occ = ReadOccurrences(args[1]);
                        if (args.Length > 3)
                        {
                            Assemblies.Constraints(Table(all.Find(x => x.Name == "AmDcSegment")), Table(all.Find(x => x.Name == "AmBrowserSegment")), occ, Table(all.Find(x => x.Name == "AmRxSegment")));
                            foreach (string block in Assemblies.Explaining.ToString().Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)) if (block.Contains(args[3])) Console.WriteLine(block);
                            break;
                        }
                        Console.Write(Assemblies.ConstraintReport(Assemblies.Constraints(Table(all.Find(x => x.Name == "AmDcSegment")), Table(all.Find(x => x.Name == "AmBrowserSegment")), occ, Table(all.Find(x => x.Name == "AmRxSegment"))), occ));
                        break;
                    }
                    case "accheck":
                    {
                        // Decoded constraints and joints vs each truth .json under a folder.
                        int parts = 0, ok = 0, total = 0, joints = 0;
                        FindReferencedUnder(Path.GetDirectoryName(args[1].TrimEnd('\\', '/')));
                        foreach (string f in Directory.GetFiles(args[1], "*.iam", SearchOption.AllDirectories))
                        {
                            string json = Path.ChangeExtension(f, ".json");
                            if (!File.Exists(json)) continue;
                            List<Segment> all = Segment.ReadAll(f);
                            List<Occurrence> occ = ReadOccurrences(f);
                            int n, j;
                            List<MateItem> items = Assemblies.Constraints(Table(all.Find(x => x.Name == "AmDcSegment")), Table(all.Find(x => x.Name == "AmBrowserSegment")), occ, Table(all.Find(x => x.Name == "AmRxSegment")));
                            string truth = File.ReadAllText(json);
                            List<string> bad = Assemblies.CompareConstraints(items, occ, truth, out n);
                            bad.AddRange(Assemblies.CompareJoints(items, occ, truth, out j));
                            parts++;
                            total += n;
                            joints += j;
                            if (bad.Count == 0) { ok++; continue; }
                            Console.WriteLine("  {0}:", Path.GetFileName(f));
                            foreach (string b in bad) Console.WriteLine("    " + b);
                        }
                        foreach (KeyValuePair<string, int> kv in Assemblies.gripsMatched) Console.WriteLine("  matched {0,4} {1}", kv.Value, kv.Key);
                        Console.WriteLine("{0} assemblies, {1} constraints, {2} joints, {3} grips ({4} with several entities carrying the geometry); {5} assemblies match Inventor exactly", parts, total, joints, Assemblies.gripsCompared, Assemblies.gripsAmbiguous, ok);
                        break;
                    }
                    case "nodeid":
                    {
                        // The nodes of a segment whose id (the u16 at +0x04 of each node) is one of these numbers.
                        NodeTable t = Table(FindSegment(Segment.ReadAll(args[1]), args[2]));
                        for (int i = 3; i < args.Length; i++)
                            foreach (Node n in t.Nodes)
                                if (n.Data != null && n.Data.Length > 6 && BitConverter.ToUInt16(n.Data, 4) == int.Parse(args[i]))
                                    Console.WriteLine("  id {0}: #{1:X} {2} {3}", args[i], n.Index, n.TypeName, NodeTable.Label(n));
                        break;
                    }
                    case "atruth":
                    {
                        // One assembly against a truth file: occurrences, constraints, joints, with the nesting followed under a folder.
                        FindReferencedUnder(args[3]);
                        List<Segment> all = Segment.ReadAll(args[1]);
                        List<Occurrence> occ = ReadOccurrences(args[1]);
                        string truth = File.ReadAllText(args[2]);
                        int a, c, j;
                        List<MateItem> items = Assemblies.Constraints(Table(all.Find(x => x.Name == "AmDcSegment")), Table(all.Find(x => x.Name == "AmBrowserSegment")), occ, Table(all.Find(x => x.Name == "AmRxSegment")));
                        int nested;
                        List<string> bad = Assemblies.Compare(occ, truth, out a, Assemblies.Tree(occ, Assemblies.FlexiblePlacements(Table(all.Find(x => x.Name == "AmRxSegment")))), out nested);
                        bad.AddRange(Assemblies.CompareConstraints(items, occ, truth, out c));
                        bad.AddRange(Assemblies.CompareJoints(items, occ, truth, out j));
                        foreach (string b in bad) Console.WriteLine("  " + b);
                        foreach (KeyValuePair<string, int> kv in Assemblies.gripsMatched) Console.WriteLine("  matched {0,4} {1}", kv.Value, kv.Key);
                        Console.WriteLine("{0} occurrences, {1} nested occurrences, {2} constraints, {3} grips ({4} ambiguous), {5} joints compared; {6} differences", a, nested, c, Assemblies.gripsCompared, Assemblies.gripsAmbiguous, j, bad.Count);
                        break;
                    }
                    case "arefs":
                    {
                        // The referenced files and the parts-list rows, in file order.
                        foreach (string path in Assemblies.References(args[1])) Console.WriteLine("  ref {0}", path.Substring(path.LastIndexOf('\\') + 1));
                        Segment rxs = Segment.ReadAll(args[1]).Find(x => x.Name == "AmRxSegment");
                        NodeTable rxt = Table(rxs);
                        foreach (Node n in rxt.Nodes)
                        {
                            if (n.Type == null || n.Type.Key != Assemblies.PartsRow) continue;
                            int count = BitConverter.ToInt32(n.Data, 4), name = 16 + 4 * count, len = BitConverter.ToInt32(n.Data, name), e = name + 4 + 2 * len;
                            Console.WriteLine("  row #{0:X} {1} flag {2:X} index {3} next {4:X}: {5}", n.Index, count, BitConverter.ToInt32(n.Data, e), BitConverter.ToInt32(n.Data, e + 4), BitConverter.ToInt32(n.Data, e + 8), Encoding.Unicode.GetString(n.Data, name + 4, 2 * len));
                        }
                        break;
                    }
                    case "imates":
                    {
                        // A document's iMates, in words.
                        List<Segment> all = Segment.ReadAll(args[1]);
                        Func<string, Segment> seg = e => all.Find(x => x.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
                        Console.Write(Imates.Report(Table(seg("DCSegment")), Table(seg("AppSegment")), Table(seg("BrowserSegment")), seg("BRepSegment") == null ? null : seg("BRepSegment").Data));
                        break;
                    }
                    case "imcheck":
                    {
                        // Decoded iMates vs each truth .json under a folder, or one file vs its truth.
                        int files = 0, ok = 0, total = 0, grips = 0;
                        foreach (string f in File.Exists(args[1]) || args[1].Contains(Segment.Embedded) ? new[] { args[1] } : Directory.GetFiles(args[1], "*.i*", SearchOption.AllDirectories))
                        {
                            if (!f.EndsWith(".ipt", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".iam", StringComparison.OrdinalIgnoreCase)) continue;
                            string json = args.Length > 2 ? args[2] : Path.ChangeExtension(f, ".json");
                            if (!File.Exists(json)) continue;
                            string truth = File.ReadAllText(json);
                            if (!truth.Contains("\"imates\"")) continue;
                            List<Segment> all = Segment.ReadAll(f);
                            Func<string, Segment> seg = e => all.Find(x => x.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
                            int n, g;
                            NodeTable own = Table(seg("DCSegment"));
                            Func<ImateInfo, Naming.Grip> inPart = null;
                            if (f.EndsWith(".iam", StringComparison.OrdinalIgnoreCase))
                            {
                                FindReferencedUnder(Path.GetDirectoryName(args[1].TrimEnd('\\', '/')));
                                List<Occurrence> occ = ReadOccurrences(f);
                                inPart = m => Assemblies.GripOfAssemblyImate(own, m, occ);
                            }
                            List<string> bad = Imates.Compare(own, Table(seg("AppSegment")), Table(seg("BrowserSegment")), seg("BRepSegment") == null ? null : seg("BRepSegment").Data, truth, out n, out g, inPart);
                            files++;
                            total += n;
                            grips += g;
                            if (bad.Count == 0) { ok++; continue; }
                            Console.WriteLine("  {0}:", Path.GetFileName(f));
                            foreach (string b in bad) Console.WriteLine("    " + b);
                        }
                        Console.WriteLine("{0} files, {1} iMates, {2} grips; {3} files match Inventor exactly", files, total, grips, ok);
                        break;
                    }
                    case "imscan":
                    {
                        // Every iMate under a folder, embedded documents too, read and looked up in its own B-rep.
                        List<string> docs = new List<string>(Directory.GetFiles(args[1], "*.ipt", SearchOption.AllDirectories));
                        foreach (string f in Directory.GetFiles(args[1], "*.iam", SearchOption.AllDirectories)) { docs.Add(f); docs.AddRange(Segment.EmbeddedIn(f)); }
                        SortedDictionary<string, int> tally = new SortedDictionary<string, int>();
                        Action<string> tick = k => { int c; tally.TryGetValue(k, out c); tally[k] = c + 1; };
                        foreach (string f in docs)
                        {
                            try
                            {
                                List<Segment> all = Segment.ReadAll(f);
                                Func<string, Segment> seg = e => all.Find(x => x.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
                                if (seg("DCSegment") == null || seg("AppSegment") == null) continue;
                                NodeTable dc = Table(seg("DCSegment"));
                                byte[] brep = seg("BRepSegment") == null ? null : seg("BRepSegment").Data;
                                List<ImateInfo> list = Imates.Read(dc, Table(seg("AppSegment")), seg("BrowserSegment") == null ? null : Table(seg("BrowserSegment")));
                                if (list.Count > 0) tick("documents with iMates");
                                foreach (ImateInfo m in list)
                                {
                                    tick("iMates");
                                    tick("kind " + m.Kind + " " + m.Word);
                                    if (m.MatchList.Count > 0) tick("with a match list");
                                    if (m.Kind == 8) continue;
                                    string work = Imates.WorkOf(dc, m);
                                    if (work != null) { tick("grip: " + work); continue; }
                                    Naming.Grip g = Imates.GripOf(dc, brep, m);
                                    if (g == null || g.Found.Count == 0) Console.WriteLine("  {0}: {1} not found ({2})", Path.GetFileName(f), m.Name, g == null ? "no name" : g.EntryType);
                                    tick(g == null ? "grip: no name" : g.Found.Count == 0 ? "grip: not found" : "grip: " + g.Kind + (g.ByPosition ? " found by its position" : " found"));
                                }
                            }
                            catch (Exception ex) { tick("errors"); Console.WriteLine("  {0}: {1}", f, ex.Message); }
                        }
                        foreach (KeyValuePair<string, int> kv in tally) Console.WriteLine("{0,6} {1}", kv.Value, kv.Key);
                        break;
                    }
                    case "patterns":
                    case "patcheck":
                    {
                        // Component patterns of an assembly, in words / vs each truth .json under a folder.
                        int files = 0, ok = 0, total = 0;
                        foreach (string f in File.Exists(args[1]) ? new[] { args[1] } : Directory.GetFiles(args[1], "*.iam", SearchOption.AllDirectories))
                        {
                            List<Segment> all = Segment.ReadAll(f);
                            List<Assemblies.PatternInfo> list = Assemblies.Patterns(Table(all.Find(x => x.Name == "AmDcSegment")), ReadOccurrences(f));
                            if (args[0] == "patterns")
                            {
                                foreach (Assemblies.PatternInfo p in list)
                                    Console.WriteLine("  {0}: {1}, {2} x {3}{4}{5}; elements: {6}", p.Name, p.Type, p.Count, p.Offset, p.Natural ? "" : " reversed", p.RowCount == null ? "" : ", rows " + p.RowCount + " x " + p.RowOffset + (p.RowNatural ? "" : " reversed"), string.Join("; ", p.Elements.ToArray()));
                                continue;
                            }
                            string json = Path.ChangeExtension(f, ".json");
                            if (!File.Exists(json)) { if (list.Count > 0) Console.WriteLine("  {0}: {1} patterns, no truth", Path.GetFileName(f), list.Count); continue; }
                            int n;
                            List<string> bad = Assemblies.ComparePatterns(list, File.ReadAllText(json), out n);
                            if (n == 0 && bad.Count == 0) continue;
                            files++;
                            total += n;
                            if (bad.Count == 0) { ok++; continue; }
                            Console.WriteLine("  {0}:", Path.GetFileName(f));
                            foreach (string b in bad) Console.WriteLine("    " + b);
                        }
                        if (args[0] == "patcheck") Console.WriteLine("{0} assemblies with patterns, {1} patterns; {2} match Inventor exactly", files, total, ok);
                        break;
                    }
                    case "surfaces":
                    {
                        // Each face's surface, in the comparable form ("cylinder ... r ..."), with how many faces carry it.
                        string error;
                        List<SabRecord> records = Sab.Read(FindSegment(Segment.ReadAll(args[1]), "PmBRepSegment").Data, 0, out error);
                        SortedDictionary<string, int> found = new SortedDictionary<string, int>();
                        foreach (SabRecord r in records)
                        {
                            if (r.Type != "face") continue;
                            string key = (r.Old ? "(earlier state) " : "") + (Naming.Canonical(records, r) ?? "other: " + Sab.Where(records, r));
                            int c;
                            found.TryGetValue(key, out c);
                            found[key] = c + 1;
                        }
                        foreach (KeyValuePair<string, int> kv in found) Console.WriteLine("{0,4} {1}", kv.Value, kv.Key);
                        break;
                    }
                    case "embedded":
                    {
                        // Each document embedded in the files under a folder (or in one file), read like any other file.
                        IEnumerable<string> files = File.Exists(args[1]) ? new[] { args[1] } : Directory.GetFiles(args[1], "*.i?*", SearchOption.AllDirectories);
                        int count = 0, bad = 0;
                        foreach (string f in files)
                        {
                            if (!f.EndsWith(".iam", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".ipt", StringComparison.OrdinalIgnoreCase)) continue;
                            List<string> inside;
                            try { inside = Segment.EmbeddedIn(f); } catch (Exception) { continue; }
                            foreach (string e in inside)
                            {
                                count++;
                                try
                                {
                                    List<Segment> all = Segment.ReadAll(e);
                                    Segment dc = all.Find(x => x.Name == "PmDCSegment"), brep = all.Find(x => x.Name == "PmBRepSegment");
                                    Console.WriteLine("{0}", e);
                                    Console.WriteLine("  {0} segments, DC {1} bytes, B-rep {2} bytes", all.Count, dc == null ? 0 : dc.Data.Length, brep == null ? 0 : brep.Data.Length);
                                    if (dc == null) continue;
                                    Console.WriteLine("  {0} features, {1} extrudes, {2} revolves", Features.List(Table(dc)).Count, Features.Extrudes(Table(dc)).Count, Features.Revolves(Table(dc)).Count);
                                }
                                catch (Exception ex) { bad++; Console.WriteLine("  {0}: {1}", e, ex.Message); }
                            }
                        }
                        Console.WriteLine("{0} embedded documents, {1} failed", count, bad);
                        break;
                    }
                    case "ascan":
                    {
                        // Every occurrence under a folder, checked against the rest of its file.
                        FindReferencedUnder(args[1]);
                        List<string> bad = new List<string>();
                        SortedDictionary<string, int> tally = new SortedDictionary<string, int>();
                        HashSet<string> there = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string f in Directory.GetFiles(args[1], "*.*", SearchOption.AllDirectories)) there.Add(Path.GetFileName(f));
                        HashSet<string> notThere = new HashSet<string>();
                        List<string> differ = new List<string>(), misses = new List<string>(), nameDiffs = new List<string>(), gripMisses = new List<string>();
                        int files = 0;
                        List<string> assemblies = new List<string>(Directory.GetFiles(args[1], "*.iam", SearchOption.AllDirectories));
                        foreach (string f in assemblies.ToArray())
                            foreach (string e in Segment.EmbeddedIn(f)) if (e.EndsWith(".iam", StringComparison.OrdinalIgnoreCase)) assemblies.Add(e);
                        foreach (string f in assemblies)
                        {
                            files++;
                            try
                            {
                                List<Occurrence> occ = ReadOccurrences(f);
                                Assemblies.Check(occ, Path.GetFileName(f), there, bad, tally, notThere, differ);
                                List<Segment> all = Segment.ReadAll(f);
                                foreach (MateItem c in Assemblies.Constraints(Table(all.Find(x => x.Name == "AmDcSegment")), Table(all.Find(x => x.Name == "AmBrowserSegment")), occ, Table(all.Find(x => x.Name == "AmRxSegment"))))
                                {
                                    Action<string> count = key => { int k; tally.TryGetValue(key, out k); tally[key] = k + 1; };
                                    count((c.IsJoint ? "joint: " : "constraint: ") + c.Kind);
                                    // The browser segment's name, where stored, against the one worked out from the type and number.
                                    count(string.IsNullOrEmpty(c.Shown) ? "constraint names: none in the browser" : c.Shown == c.Name ? "constraint names: agree with the browser" : "constraint names: differ from the browser");
                                    if (!string.IsNullOrEmpty(c.Shown) && c.Shown != c.Name) nameDiffs.Add(Path.GetFileName(f) + ": browser " + c.Shown + ", decoded " + c.Name);
                                    if (c.Failing) count("constraints marked failing in the file");
                                    foreach (GripItem g in c.Grips)
                                    {
                                        count("grips");
                                        if (g.Work != null) { count("grips: work feature: " + g.Work.Split(' ')[0]); continue; }
                                        if (g.Grip == null) { count("grips: not looked up: " + g.Why); gripMisses.Add(Path.GetFileName(f) + " " + c.Name + (c.Failing ? " [failing]" : "") + " in " + g.File + ": " + g.Why + "; " + g.Geometry); continue; }
                                        count("grips: in " + g.Target);
                                        count("grips: entry " + g.Grip.EntryType + " = " + g.Grip.Kind);
                                        HashSet<string> places = new HashSet<string>();
                                        foreach (Naming.Match m in g.Exact) places.Add(Naming.Place(m));
                                        string outcome = c.IsJoint ? (g.Grip.Found.Count > 0 ? "a joint origin's entity found" : "a joint origin's entity not found") : g.Exact.Count == 1 ? "one entity carrying its geometry" : places.Count == 1 ? "one place, the entity listed twice" : g.Exact.Count > 1 ? "several places carrying its geometry" : g.Grip.Found.Count > 0 ? "entities found, none carrying its geometry" : g.Grip.Vertex != null ? "a vertex known by position" : g.Grip.Surface != null ? "a surface known, no entity" : "nothing found";
                                        count((c.Failing ? "grips (failing): " : "grips: ") + outcome + (g.ByGeometry ? " (among a named face's edges)" : ""));
                                        if (g.Exact.Count != 1) count("  " + outcome + " / " + g.Grip.EntryType);
                                        if (g.Exact.Count == 0 && !(c.IsJoint && g.Grip.Found.Count > 0)) gripMisses.Add(Path.GetFileName(f) + " " + c.Name + (c.Failing ? " [failing]" : "") + " in " + g.File + ": " + g.Grip.Kind + " " + g.Grip.EntryType + ", " + g.Grip.Found.Count + " found, " + outcome + "; " + g.Geometry);
                                    }
                                    if (c.Suppressed) { count("constraints suppressed"); continue; }
                                    foreach (string g in c.Geometry) count("geometry: " + g.Split(' ')[0]);
                                    if (c.IsJoint || c.Type > 5) continue;
                                    double? miss = Assemblies.Miss(c);
                                    if (c.Failing && miss != null) { count(miss.Value <= 1e-4 ? "failing constraints that hold anyway" : "failing constraints that don't hold"); continue; }
                                    if (miss == null) { count("constraint pairs not checked: " + (c.Geometry.Count > 1 ? c.Geometry[0].Split(' ')[0] + "/" + c.Geometry[1].Split(' ')[0] : "?")); continue; }
                                    if (miss.Value <= 1e-4) count("constraints that hold (within 1 micron)");
                                    else if (miss.Value <= 1e-2) { count("constraints off by under 0.1 mm"); }
                                    else
                                    {
                                        count("constraints off by more");
                                        bool flexible = false;
                                        foreach (int a in c.Occurrences) { Occurrence o = occ.Find(x => x.Dc == a); if (o != null && o.Flexible) flexible = true; }
                                        count(flexible ? "  of which grip a flexible sub-assembly" : c.Nested ? "  of which grip inside a rigid sub-assembly" : "  of which grip top-level parts");
                                        misses.Add(Path.GetFileName(f) + " " + c.Name + (flexible ? " [flexible]" : c.Nested ? " [nested]" : "") + " off by " + miss.Value.ToString("G3") + ": " + string.Join(" | ", c.Geometry.ToArray()));
                                    }
                                }
                            }
                            catch (Exception ex) { bad.Add(Path.GetFileName(f) + ": " + ex.GetType().Name + " " + ex.Message + " " + ex.StackTrace.Replace("\r\n", " ")); }
                        }
                        Console.WriteLine("{0} assemblies", files);
                        foreach (KeyValuePair<string, int> kv in tally) Console.WriteLine("{0,6} {1}", kv.Value, kv.Key);
                        Console.WriteLine("{0} problems", bad.Count);
                        foreach (string b in bad.GetRange(0, Math.Min(40, bad.Count))) Console.WriteLine("  " + b);
                        if (args.Length > 2 && args[2] == "--names")
                        {
                            Console.WriteLine("Not in the folder: " + string.Join(", ", new List<string>(notThere).ToArray()));
                            Console.WriteLine("Names that differ:");
                            foreach (string b in differ) Console.WriteLine("  " + b);
                        }
                        foreach (string b in nameDiffs.GetRange(0, Math.Min(10, nameDiffs.Count))) Console.WriteLine("  name: " + b);
                        Console.WriteLine("Grips without an entity carrying their geometry: {0}", gripMisses.Count);
                        foreach (string b in gripMisses.GetRange(0, Math.Min(args.Length > 2 && args[2] == "--grips" ? gripMisses.Count : 25, gripMisses.Count))) Console.WriteLine("  " + b);
                        Console.WriteLine("Constraints off by more than 0.1 mm: {0}", misses.Count);
                        foreach (string b in misses.GetRange(0, Math.Min(args.Length > 2 && args[2] == "--misses" ? misses.Count : 15, misses.Count))) Console.WriteLine("  " + b);
                        break;
                    }
                    case "acheck":
                    {
                        // Decoded occurrences, with those nested in sub-assemblies, vs each truth .json under a folder.
                        int parts = 0, ok = 0, total = 0, inner = 0;
                        FindReferencedUnder(Path.GetDirectoryName(args[1].TrimEnd('\\', '/')));
                        foreach (string f in Directory.GetFiles(args[1], "*.iam", SearchOption.AllDirectories))
                        {
                            string json = Path.ChangeExtension(f, ".json");
                            if (!File.Exists(json)) continue;
                            int n, nested;
                            List<Occurrence> occ = ReadOccurrences(f);
                            Dictionary<string, double[]> flexible = Assemblies.FlexiblePlacements(Table(Segment.ReadAll(f).Find(x => x.Name == "AmRxSegment")));
                            List<string> bad = Assemblies.Compare(occ, File.ReadAllText(json), out n, Assemblies.Tree(occ, flexible), out nested);
                            parts++;
                            total += n;
                            inner += nested;
                            if (bad.Count == 0) { ok++; continue; }
                            Console.WriteLine("  {0}:", Path.GetFileName(f));
                            foreach (string b in bad) Console.WriteLine("    " + b);
                        }
                        Console.WriteLine("{0} assemblies, {1} occurrences, {2} nested; {3} assemblies match Inventor exactly", parts, total, inner, ok);
                        break;
                    }
                    case "curves": Console.Write(Curves.Report(Table(FindSegment(Segment.ReadAll(args[1]), null)))); break;
                    case "cscan":
                    {
                        // Every spline, ellipse and text item under a folder, checked against its own points.
                        List<string> bad = new List<string>();
                        SortedDictionary<string, int> tally = new SortedDictionary<string, int>();
                        foreach (string f in Directory.GetFiles(args[1], "*.ipt", SearchOption.AllDirectories))
                        {
                            NodeTable t;
                            try { t = Table(FindSegment(Segment.ReadAll(f), null)); } catch (Exception) { continue; }
                            try { Curves.Check(t, Path.GetFileName(f), bad, tally); }
                            catch (Exception ex) { bad.Add(Path.GetFileName(f) + ": " + ex.GetType().Name + " " + ex.Message); }
                        }
                        foreach (KeyValuePair<string, int> kv in tally) Console.WriteLine("{0,6} {1}", kv.Value, kv.Key);
                        Console.WriteLine("{0} problems", bad.Count);
                        foreach (string b in bad.GetRange(0, Math.Min(60, bad.Count))) Console.WriteLine("  " + b);
                        break;
                    }
                    case "cproj":
                    {
                        // A projected curve's record after its poles, and how its ends sit against its points.
                        NodeTable t = Table(FindSegment(Segment.ReadAll(args[1]), null));
                        Console.Write(Curves.ProjectedDetail(t, t.At(Convert.ToInt32(args[2], 16))));
                        break;
                    }
                    case "ccheck":
                    {
                        // Decoded splines, ellipses and text vs the "curves" in each truth .json under a folder,
                        // or one part against a truth file.
                        if (args.Length > 2)
                        {
                            int count;
                            List<string> diff = Curves.Compare(Table(FindSegment(Segment.ReadAll(args[1]), null)), File.ReadAllText(args[2]), out count);
                            foreach (string b in diff) Console.WriteLine("    " + b);
                            Console.WriteLine("{0} curves; {1} differences", count, diff.Count);
                            break;
                        }
                        int parts = 0, ok = 0, total = 0;
                        foreach (string f in Directory.GetFiles(args[1], "*.ipt", SearchOption.AllDirectories))
                        {
                            string json = Path.ChangeExtension(f, ".json");
                            if (!File.Exists(json)) continue;
                            string truth = File.ReadAllText(json);
                            if (!truth.Contains("\"curves\"")) continue;
                            int n;
                            List<string> bad = Curves.Compare(Table(FindSegment(Segment.ReadAll(f), null)), truth, out n);
                            parts++;
                            total += n;
                            if (bad.Count == 0) { ok++; continue; }
                            Console.WriteLine("  {0}:", Path.GetFileName(f));
                            foreach (string b in bad) Console.WriteLine("    " + b);
                        }
                        Console.WriteLine("{0} parts, {1} curves; {2} parts match Inventor exactly", parts, total, ok);
                        break;
                    }
                    case "tcount":
                    {
                        // DC nodes whose type name matches a pattern, across a folder: how many, and in which parts.
                        System.Text.RegularExpressions.Regex want = new System.Text.RegularExpressions.Regex(args[2]);
                        SortedDictionary<string, int> count = new SortedDictionary<string, int>();
                        SortedDictionary<string, SortedSet<string>> where = new SortedDictionary<string, SortedSet<string>>();
                        foreach (string f in Directory.GetFiles(args[1], args.Length > 3 && args[3] == "--iam" ? "*.iam" : "*.ipt", SearchOption.AllDirectories))
                        {
                            NodeTable t;
                            try { t = Table(FindSegment(Segment.ReadAll(f), null)); } catch (Exception) { continue; }
                            foreach (Node n in t.Nodes)
                            {
                                if (n.Type == null || !want.IsMatch(n.TypeName)) continue;
                                int c;
                                count.TryGetValue(n.TypeName, out c);
                                count[n.TypeName] = c + 1;
                                if (!where.ContainsKey(n.TypeName)) where[n.TypeName] = new SortedSet<string>();
                                where[n.TypeName].Add(Path.GetFileName(f));
                            }
                        }
                        foreach (KeyValuePair<string, int> kv in count)
                            Console.WriteLine("{0,6} x {1} in {2} parts: {3}", kv.Value, kv.Key, where[kv.Key].Count, string.Join(", ", new List<string>(where[kv.Key]).GetRange(0, Math.Min(6, where[kv.Key].Count)).ToArray()));
                        break;
                    }
                    case "mscan":
                    {
                        // Every thread feature, shell, sweep and loft under a folder, in words; with --list all of them, else the gaps.
                        bool all = args.Length > 2 && args[2] == "--list";
                        int threads = 0, shells = 0, sweeps = 0, lofts = 0;
                        SortedDictionary<string, int> sheet = new SortedDictionary<string, int>();
                        List<string> gaps = new List<string>();
                        foreach (string f in Directory.GetFiles(args[1], "*.ipt", SearchOption.AllDirectories))
                        {
                            NodeTable t;
                            try { t = DcWithUnits(f, true); } catch (Exception) { continue; }
                            string file = Path.GetFileName(f);
                            Action<string, string, bool> report = (name, words, gap) => {
                                if (all) Console.WriteLine("  {0} {1}: {2}", file, name, words);
                                if (gap) gaps.Add(file + " " + name + ": " + words);
                            };
                            foreach (MoreFeatures.ThreadInfo x in MoreFeatures.Threads(t)) { threads++; report(x.Name, MoreFeatures.Describe(x), x.Thread == null || (!x.FullDepth && x.Depth == null) || x.Face < 0); }
                            foreach (MoreFeatures.ShellInfo x in MoreFeatures.Shells(t)) { shells++; report(x.Name, MoreFeatures.Describe(x), x.Thickness == null || x.Direction.StartsWith("direction")); }
                            foreach (MoreFeatures.SweepInfo x in MoreFeatures.Sweeps(t)) { sweeps++; report(x.Name, MoreFeatures.Describe(x), x.Path == null || x.Profile == null || x.Sketch == null || x.SweepType.StartsWith("value")); }
                            foreach (MoreFeatures.LoftInfo x in MoreFeatures.Lofts(t)) { lofts++; report(x.Name, MoreFeatures.Describe(x), x.Sections == null || x.Sections.Contains("?") || (x.LoftType == "kLoftWithCenterline" && x.Centerline == null)); }
                            foreach (MoreFeatures.SheetInfo x in MoreFeatures.Sheets(t))
                            {
                                int c;
                                sheet.TryGetValue(x.Kind, out c); sheet[x.Kind] = c + 1;
                                report(x.Name, MoreFeatures.Describe(x), x.Kind == "plate" ? x.Profile == null || x.Thickness == null : x.Kind == "flange" ? x.Height == null || x.Angle == null || x.Radius == null : x.Kind == "bend" && x.Radius == null);
                            }
                            foreach (MoreFeatures.ImportedInfo x in MoreFeatures.ImportedBodies(t)) { int c; sheet.TryGetValue(x.Kind, out c); sheet[x.Kind] = c + 1; report(x.Name, x.Kind, false); }
                        }
                        Console.WriteLine("{0} thread features, {1} shells, {2} sweeps, {3} lofts; {4}; {5} with gaps", threads, shells, sweeps, lofts,
                            string.Join(", ", new List<KeyValuePair<string, int>>(sheet).ConvertAll(kv => kv.Value + " " + kv.Key).ToArray()), gaps.Count);
                        foreach (string g in gaps.GetRange(0, Math.Min(30, gaps.Count))) Console.WriteLine("  " + g);
                        break;
                    }
                    case "loopscan":
                    {
                        // Every extrude and revolve profile under a folder: are its loops read, and does every sketch curve
                        // the B-rep says the feature swept lie in one of them?
                        int features = 0, read = 0, checkedF = 0, agree = 0, loops = 0, holes = 0;
                        SortedDictionary<string, int> problems = new SortedDictionary<string, int>();
                        List<string> bad = new List<string>();
                        foreach (string f in Directory.GetFiles(args[1], "*.ipt", SearchOption.AllDirectories))
                        {
                            List<Segment> segs;
                            NodeTable t;
                            try { segs = Segment.ReadAll(f); t = Table(FindSegment(segs, null)); } catch (Exception) { continue; }
                            byte[] brep = FindSegment(segs, "PmBRepSegment").Data;
                            Dictionary<string, int> items = Naming.SketchItems(t);
                            Dictionary<int, Features.Info> infos = new Dictionary<int, Features.Info>();
                            foreach (Features.Info fi in Features.List(t)) infos[fi.Node.Index] = fi;
                            List<KeyValuePair<int, int[]>> sweeps = new List<KeyValuePair<int, int[]>>();   // feature -> {profile, sketch}
                            foreach (Features.Info fi in infos.Values)
                            {
                                if (fi.Kind != Features.Extrude && fi.Kind != Features.Revolve && fi.Kind != Features.SurfaceExtrude) continue;
                                List<int> s = Features.Properties(fi.Node);
                                int profile = s.Count > 1 ? s[1] : -1;
                                sweeps.Add(new KeyValuePair<int, int[]>(fi.Node.Index, new[] { profile, Profiles.SketchOf(t, profile) }));
                            }
                            foreach (KeyValuePair<int, int[]> sw in sweeps)
                            {
                                features++;
                                Features.Info fi = infos[sw.Key];
                                List<Profiles.Loop> ls = sw.Value[1] < 0 ? new List<Profiles.Loop>() : Profiles.Loops(t, sw.Value[0], sw.Value[1], items);
                                string why = sw.Value[1] < 0 ? "no sketch" : ls.Count == 0 ? "no loops" : null;
                                foreach (Profiles.Loop l in ls)
                                    if (why == null && l.Problem != null) why = l.Problem;
                                    else if (why == null && l.Entities.Count != l.Items.Count) why = "a curve item the sketch no longer has" + ((fi.State & 4) != 0 ? " (feature not computed)" : "");
                                if (why != null) { int c; problems.TryGetValue(why, out c); problems[why] = c + 1; if (bad.Count < 40) bad.Add(Path.GetFileName(f) + " " + fi.Name + ": " + why); continue; }
                                read++;
                                loops += ls.Count;
                                foreach (Profiles.Loop l in ls) if (!Profiles.AddsMaterial(t, l)) holes++;
                                if ((fi.State & 4) != 0) continue;
                                HashSet<int> swept = Naming.SweptCurves(brep, fi.Tag), inLoops = new HashSet<int>();
                                foreach (Profiles.Loop l in ls) inLoops.UnionWith(l.Items);
                                if (swept.Count == 0) continue;
                                checkedF++;
                                swept.ExceptWith(inLoops);
                                if (swept.Count == 0) agree++;
                                else if (bad.Count < 40) bad.Add(Path.GetFileName(f) + " " + fi.Name + ": swept curve items not in its loops: " + string.Join(",", new List<int>(swept).ConvertAll(x => x.ToString()).ToArray()));
                            }
                        }
                        Console.WriteLine("{0} extrude and revolve profiles; {1} read ({2} loops, {3} of them holes)", features, read, loops, holes);
                        foreach (KeyValuePair<string, int> kv in problems) Console.WriteLine("  {0,5} x {1}", kv.Value, kv.Key);
                        Console.WriteLine("{0} computed features with swept curves named in the B-rep; {1} have all of them in their loops", checkedF, agree);
                        foreach (string b in bad) Console.WriteLine("  " + b);
                        break;
                    }
                    case "patscan":
                    {
                        // Every mirror and pattern under a folder: counts by kind, and what is not decoded.
                        SortedDictionary<string, int> kinds = new SortedDictionary<string, int>(), gaps = new SortedDictionary<string, int>();
                        List<string> examples = new List<string>();
                        foreach (string f in Directory.GetFiles(args[1], "*.ipt", SearchOption.AllDirectories))
                        {
                            NodeTable t;
                            try { t = DcWithUnits(f, true); } catch (Exception) { continue; }
                            foreach (Patterns.PatternInfo p in Patterns.List(t))
                            {
                                int c;
                                kinds.TryGetValue(p.Kind, out c); kinds[p.Kind] = c + 1;
                                foreach (string g in Patterns.Gaps(t, p))
                                {
                                    gaps.TryGetValue(g, out c); gaps[g] = c + 1;
                                    if (examples.Count < 30) examples.Add(Path.GetFileName(f) + " " + p.Name + ": " + g);
                                }
                                if (args.Length > 2 && args[2] == "--list") Console.WriteLine("  {0} {1}: {2}", Path.GetFileName(f), p.Name, Patterns.Describe(p));
                            }
                        }
                        foreach (KeyValuePair<string, int> kv in kinds) Console.WriteLine("{0,5} {1}", kv.Value, kv.Key);
                        Console.WriteLine("gaps:");
                        foreach (KeyValuePair<string, int> kv in gaps) Console.WriteLine("{0,5} {1}", kv.Value, kv.Key);
                        foreach (string e in examples) Console.WriteLine("  " + e);
                        break;
                    }
                    case "fcheck": ExtrudeCheckCmd(args[1]); break;
                    case "fscan": ExtrudeScanCmd(args[1], args.Length > 2 && args[2] == "--list"); break;
                    case "fcompare":
                    {
                        SortedDictionary<string, int> n = new SortedDictionary<string, int>();
                        List<string> bad = Features.Compare(DcWithUnits(args[1], true), File.ReadAllText(args[2]), n);
                        foreach (string b in bad) Console.WriteLine("  " + b);
                        Console.WriteLine("{0}; {1} differences", Features.Counted(n), bad.Count);
                        break;
                    }
                    case "pcompare":
                    {
                        int n;
                        List<string> bad = Params.Compare(DcWithUnits(args[1], true), File.ReadAllText(args[2]), out n);
                        foreach (string b in bad) Console.WriteLine("  " + b);
                        Console.WriteLine("{0} parameters, {1} differ", n, bad.Count);
                        break;
                    }
                    case "status": Console.WriteLine(InventorDriver.Status()); break;
                    case "measure": Console.Write(InventorDriver.Measure(args[1])); break;
                    case "nudge-occurrences": Console.Write(InventorDriver.NudgeOccurrences(args[1])); break;
                    case "occurrence-freedom": Console.Write(InventorDriver.OccurrenceFreedom(args[1])); break;
                    case "sketch-freedom": Console.Write(InventorDriver.SketchFreedom(args[1])); break;
                    case "change-copy": Console.Write(InventorDriver.ChangeCopy(args[1], args[2], double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture))); break;
                    case "gen": Console.Write(InventorDriver.Generate(args[1], args[2])); break;
                    case "truth": Console.Write(InventorDriver.TruthOfFile(args[1], args.Length > 2 && args[2] == "--entities")); break;
                    case "truth-embedded": Console.Write(InventorDriver.TruthWithEmbedded(args[1], args[2], args[3])); break;
                    case "close-under": Console.Write(InventorDriver.CloseUnder(args[1])); break;
                    default: Console.WriteLine(Usage); return 1;
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                if (Environment.GetEnvironmentVariable("INVRE_TRACE") != null) Console.Error.WriteLine(ex.StackTrace);
                return 2;
            }
        }

        // Sub-assemblies found by file name under a folder, for following a path into them.
        static void FindReferencedUnder(string root)
        {
            Dictionary<string, string> where = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.GetFiles(root, "*.iam", SearchOption.AllDirectories))
                if (!f.Contains("OldVersions") && !where.ContainsKey(Path.GetFileName(f))) where[Path.GetFileName(f)] = f;
            Dictionary<string, List<Occurrence>> read = new Dictionary<string, List<Occurrence>>(StringComparer.OrdinalIgnoreCase);
            // An embedded document is found by the file that holds it, and keeps the rest of its path.
            Func<string, Dictionary<string, string>, string> locate = (stored, byName) => {
                string outer, path;
                Segment.Inside(stored, out outer);
                if (File.Exists(outer)) return stored;
                string name = outer.Substring(outer.LastIndexOfAny(new[] { '\\', '/' }) + 1);
                if (!where.TryGetValue(name, out path) && !byName.TryGetValue(name, out path)) return null;
                return path + stored.Substring(outer.Length);
            };
            // Its stored path if that still exists (the same name can be in several folders), else by its name.
            Assemblies.Referenced = stored => {
                List<Occurrence> r;
                if (read.TryGetValue(stored, out r)) return r;
                string path = locate(stored, where);
                if (path != null) { try { r = ReadOccurrences(path); } catch (Exception) { } }
                read[stored] = r;
                return r;
            };
            // Parts by name too, and their DC and B-rep segments.
            Dictionary<string, string> parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.GetFiles(root, "*.ipt", SearchOption.AllDirectories))
                if (!f.Contains("OldVersions") && !parts.ContainsKey(Path.GetFileName(f))) parts[Path.GetFileName(f)] = f;
            Dictionary<string, KeyValuePair<NodeTable, byte[]>> partData = new Dictionary<string, KeyValuePair<NodeTable, byte[]>>(StringComparer.OrdinalIgnoreCase);
            Assemblies.PartFile = stored => {
                KeyValuePair<NodeTable, byte[]> r;
                if (partData.TryGetValue(stored, out r)) return r;
                string path = locate(stored, parts);
                if (path != null)
                {
                    try
                    {
                        List<Segment> all = Segment.ReadAll(path);
                        Segment dc = all.Find(x => x.Name == "PmDCSegment"), brep = all.Find(x => x.Name == "PmBRepSegment");
                        if (dc != null && brep != null) r = new KeyValuePair<NodeTable, byte[]>(Table(dc), brep.Data);
                    }
                    catch (Exception) { }
                }
                partData[stored] = r;
                return r;
            };
            Dictionary<string, Dictionary<string, double[]>> flexible = new Dictionary<string, Dictionary<string, double[]>>(StringComparer.OrdinalIgnoreCase);
            Assemblies.ReferencedFlexible = stored => {
                Dictionary<string, double[]> r;
                if (flexible.TryGetValue(stored, out r)) return r;
                string path = locate(stored, where);
                if (path != null)
                {
                    try
                    {
                        Segment rx = Segment.ReadAll(path).Find(x => x.Name == "AmRxSegment");
                        if (rx != null) r = Assemblies.FlexiblePlacements(Table(rx));
                    }
                    catch (Exception) { }
                }
                flexible[stored] = r;
                return r;
            };
        }

        static List<Occurrence> ReadOccurrences(string file)
        {
            List<Segment> all = Segment.ReadAll(file);
            Func<string, NodeTable> table = name => { Segment s = all.Find(x => x.Name == name); return s == null ? null : Table(s); };
            return Assemblies.Read(table("AmDcSegment"), table("AmRxSegment"), table("DesignViewSegment"), file);
        }

        // Where a text (ASCII or UTF-16) occurs: every stream of the file, and every decompressed segment.
        static void FindText(string file, string text)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(text), wide = Encoding.Unicode.GetBytes(text);
            if (text.StartsWith("hex:"))
            {
                ascii = new byte[(text.Length - 4) / 2];
                for (int i = 0; i < ascii.Length; i++) ascii[i] = Convert.ToByte(text.Substring(4 + 2 * i, 2), 16);
                wide = ascii;
            }
            Action<string, byte[]> scan = (where, d) => {
                foreach (byte[] pat in ascii == wide ? new[] { ascii } : new[] { ascii, wide })
                    for (int i = 0; i + pat.Length <= d.Length; i++)
                    {
                        int k = 0;
                        while (k < pat.Length && d[i + k] == pat[k]) k++;
                        if (k < pat.Length) continue;
                        int from = Math.Max(0, i - 24), to = Math.Min(d.Length, i + pat.Length + 90);
                        Console.WriteLine("{0} +0x{1:X} {2}: {3}", where, i, pat == ascii ? "ascii" : "utf16", Encoding.Unicode.GetString(d, from & ~1, (to - (from & ~1)) & ~1).Replace('\0', '.').Replace('\n', ' ').Replace('\r', ' '));
                    }
            };
            CfbFile cfb = new CfbFile(file);
            Action<CfbEntry, string> walk = null;
            walk = (e, path) => {
                foreach (CfbEntry c in e.Children)
                {
                    if (c.Type == 1) walk(c, path + c.Name + "/");
                    else if (c.Type == 2) scan(path + Text.Esc(c.Name), cfb.ReadStream(c));
                }
            };
            walk(cfb.Root, "");
            foreach (Segment s in Segment.ReadAll(file))
            {
                scan(s.Name + " data", s.Data);
                if (s.Meta != null) scan(s.Name + " meta", s.Meta);
            }
        }

        static void Dump(string file, string extractDir)
        {
            CfbFile cfb = new CfbFile(file);
            Console.WriteLine("{0}\nCFB v{1}, {2}-byte sectors, {3} directory entries", file, cfb.MajorVersion, cfb.SectorSize, cfb.Entries.Count);
            Walk(cfb, cfb.Root, 0, extractDir);
        }

        static void Walk(CfbFile cfb, CfbEntry e, int depth, string dir)
        {
            string ind = new string(' ', depth * 2);
            foreach (CfbEntry c in e.Children)
            {
                if (c.Type == 1)
                {
                    Console.WriteLine("{0}[{1}]  clsid={2}", ind, Text.Esc(c.Name), c.Clsid);
                    Walk(cfb, c, depth + 1, dir == null ? null : Path.Combine(dir, Text.Safe(c.Name)));
                }
                else if (c.Type == 2)
                {
                    byte[] b = cfb.ReadStream(c);
                    Console.WriteLine("{0}{1,-34} {2,9:N0} B  {3}", ind, Text.Esc(c.Name), c.Size, Text.Hex(b, 0, 20));
                    if (dir != null)
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(Path.Combine(dir, Text.Safe(c.Name)), b);
                    }
                }
            }
        }

        static void HexView(byte[] b, int offset, int length)
        {
            for (int row = offset & ~0xF; row < Math.Min(b.Length, offset + length); row += 16)
            {
                StringBuilder ascii = new StringBuilder();
                for (int i = row; i < Math.Min(b.Length, row + 16); i++) ascii.Append(b[i] >= 0x20 && b[i] < 0x7F ? (char)b[i] : '.');
                Console.WriteLine("{0:X6}  {1,-47}  {2}", row, Text.Hex(b, row, 16), ascii);
            }
        }

        static void Segs(string file, string outDir)
        {
            Console.WriteLine(file);
            foreach (Segment s in Segment.ReadAll(file))
            {
                Console.WriteLine("  {0,-22} data {1,-5} {2,10:N0} -> {3,10}   meta {4,-10} {5,7:N0} -> {6,8}",
                    s.Name, s.DataCompression, s.DataRawSize, s.Data == null ? "?" : s.Data.Length.ToString("N0"),
                    s.MetaCompression, s.MetaRawSize, s.Meta == null ? "?" : s.Meta.Length.ToString("N0"));
                if (outDir != null)
                {
                    Directory.CreateDirectory(outDir);
                    if (s.Data != null) File.WriteAllBytes(Path.Combine(outDir, s.Name + ".B.bin"), s.Data);
                    if (s.Meta != null) File.WriteAllBytes(Path.Combine(outDir, s.Name + ".M.bin"), s.Meta);
                }
            }
            if (Codec.ZstdPath != null) Console.WriteLine("  (zstd via {0})", Codec.ZstdPath);
        }

        static void DiffCmd(string[] args)
        {
            string fa = args[1], fb = args[2], only = null;
            bool meta = false;
            int maxHunks = 30;
            for (int i = 3; i < args.Length; i++)
            {
                if (args[i] == "--meta") meta = true;
                else if (args[i] == "--max") maxHunks = int.Parse(args[++i]);
                else only = args[i];
            }
            List<Segment> sa = Segment.ReadAll(fa), sb = Segment.ReadAll(fb);
            Dictionary<string, Segment> byName = new Dictionary<string, Segment>();
            foreach (Segment s in sb) byName[s.Name] = s;

            Console.WriteLine("A: {0}\nB: {1}\n", fa, fb);
            Console.WriteLine("  {0,-22} {1,10} {2,10} {3,7} {4,9}", "segment" + (meta ? " (meta)" : ""), "A bytes", "B bytes", "hunks", "changed");
            StringBuilder details = new StringBuilder();
            foreach (Segment a in sa)
            {
                if (only != null && !string.Equals(a.Name, only, StringComparison.OrdinalIgnoreCase)) continue;
                Segment b;
                if (!byName.TryGetValue(a.Name, out b)) { Console.WriteLine("  {0,-22} only in A", a.Name); continue; }
                byte[] da = meta ? a.Meta : a.Data, db = meta ? b.Meta : b.Data;
                if (da == null || db == null) { Console.WriteLine("  {0,-22} not decoded", a.Name); continue; }
                bool tooDifferent;
                List<Hunk> hunks = Differ.Coalesce(Differ.Diff(da, db, 4000, out tooDifferent), 8);
                Console.WriteLine("  {0,-22} {1,10:N0} {2,10:N0} {3,7} {4,9:N0}{5}",
                    a.Name, da.Length, db.Length, hunks.Count, Differ.ChangedBytes(hunks), tooDifferent ? "  (too different)" : "");
                if (hunks.Count > 0 && !tooDifferent)
                    details.AppendFormat("\n== {0} ==\n{1}", a.Name, Differ.Report(da, db, hunks, maxHunks));
            }
            foreach (Segment b in sb)
                if (only == null && !ContainsName(sa, b.Name)) Console.WriteLine("  {0,-22} only in B", b.Name);
            Console.Write(details.ToString());
        }

        static Segment FindSegment(List<Segment> segs, string name)
        {
            foreach (Segment s in segs)
                if (name == null ? (s.Name == "PmDCSegment" || s.Name == "AmDcSegment") : string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return s;
            throw new Exception("segment not found: " + (name ?? "DC"));
        }

        static NodeTable Table(Segment s)
        {
            return NodeTable.Parse(s.Meta, s.Data);
        }

        // The DC segment, with the document's length and angle units read from the App segment.
        static NodeTable DcWithUnits(string file, bool quiet = false)
        {
            List<Segment> segs = Segment.ReadAll(file);
            foreach (Segment s in segs)
            {
                if (s.Name != "PmAppSegment" && s.Name != "AmAppSegment") continue;
                string units = Units.UseDocumentUnits(Table(s));
                if (units != null && !quiet) Console.WriteLine("  document units: " + units);
            }
            return Table(FindSegment(segs, null));
        }

        // Every parameter in every part under dir: counts, and what still decodes with "?" (unknown unit or node).
        static void ParamScanCmd(string dir)
        {
            int files = 0, ps = 0, unknown = 0;
            Dictionary<string, int> gaps = new Dictionary<string, int>();
            Dictionary<string, string> example = new Dictionary<string, string>();
            List<string> all = new List<string>(Directory.GetFiles(dir, "*.ipt", SearchOption.AllDirectories));
            all.AddRange(Directory.GetFiles(dir, "*.iam", SearchOption.AllDirectories));
            foreach (string f in all)
            {
                List<Params.Decoded> list;
                try { list = Params.Decode(DcWithUnits(f, true)); }
                catch (Exception ex) { Console.WriteLine("  {0}: {1}", f, ex.Message); continue; }
                files++;
                foreach (Params.Decoded p in list)
                {
                    ps++;
                    bool bad = false;
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(p.Unit + " " + p.Formula, @"\?[\w#]+|fn[0-9A-F]+\("))
                    {
                        bad = true;
                        int c;
                        gaps.TryGetValue(m.Value, out c);
                        gaps[m.Value] = c + 1;
                        if (!example.ContainsKey(m.Value)) example[m.Value] = Path.GetFileName(f) + ": " + p.Name + " [" + p.Unit + "] = " + p.Formula;
                    }
                    if (bad) unknown++;
                }
            }
            Console.WriteLine("{0} files, {1} parameters, {2} with something undecoded", files, ps, unknown);
            foreach (KeyValuePair<string, int> kv in gaps) Console.WriteLine("  {0,5} x {1,-16} e.g. {2}", kv.Value, kv.Key, example[kv.Key]);
        }

        // Decoded parameters vs the truth .json next to each generated part.
        static void ParamCheckCmd(string dir)
        {
            int files = 0, ok = 0, total = 0;
            foreach (string f in Directory.GetFiles(dir, "*.ipt", SearchOption.AllDirectories))
            {
                string json = Path.ChangeExtension(f, ".json");
                if (!File.Exists(json)) continue;
                files++;
                int n;
                List<string> bad = Params.Compare(DcWithUnits(f, true), File.ReadAllText(json), out n);
                total += n;
                if (bad.Count == 0) { ok++; continue; }
                Console.WriteLine("  {0}:", f.Substring(dir.Length).TrimStart('\\'));
                foreach (string b in bad) Console.WriteLine("    " + b);
            }
            Console.WriteLine("{0} parts, {1} parameters; {2} parts match Inventor exactly", files, total, ok);
        }

        // Decoded extrudes, revolves and holes vs the truth .json next to each generated part.
        static void ExtrudeCheckCmd(string dir)
        {
            int files = 0, ok = 0;
            SortedDictionary<string, int> total = new SortedDictionary<string, int>();
            foreach (string f in Directory.GetFiles(dir, "*.ipt", SearchOption.AllDirectories))
            {
                string json = Path.ChangeExtension(f, ".json");
                if (!File.Exists(json)) continue;
                SortedDictionary<string, int> n = new SortedDictionary<string, int>();
                List<string> bad = Features.Compare(DcWithUnits(f, true), File.ReadAllText(json), n);
                if (n.Count == 0 && bad.Count == 0) continue;
                files++;
                foreach (KeyValuePair<string, int> kv in n) { int c; total.TryGetValue(kv.Key, out c); total[kv.Key] = c + kv.Value; }
                if (bad.Count == 0) { ok++; continue; }
                Console.WriteLine("  {0}:", f.Substring(dir.Length).TrimStart('\\'));
                foreach (string b in bad) Console.WriteLine("    " + b);
            }
            Console.WriteLine("{0} parts, {1}; {2} parts match Inventor exactly", files, Features.Counted(total), ok);
        }

        // Every extrude in every part under dir: counts by operation, extent and direction, and what is still unknown.
        static void ExtrudeScanCmd(string dir, bool listAll)
        {
            int files = 0, extrudes = 0;
            SortedDictionary<string, int> kinds = new SortedDictionary<string, int>(), gaps = new SortedDictionary<string, int>();
            Dictionary<string, string> example = new Dictionary<string, string>();
            Action<SortedDictionary<string, int>, string, string> count = (d, key, where) => {
                int c;
                d.TryGetValue(key, out c);
                d[key] = c + 1;
                if (!example.ContainsKey(key)) example[key] = where;
            };
            int revolves = 0, holes = 0, fillets = 0, chamfers = 0;
            foreach (string f in Directory.GetFiles(dir, "*.ipt", SearchOption.AllDirectories))
            {
                List<Features.ExtrudeInfo> list;
                List<Features.RevolveInfo> rlist;
                List<Features.HoleInfo> hlist;
                List<Features.FilletInfo> flist;
                List<Features.ChamferInfo> clist;
                NodeTable t;
                try
                {
                    t = DcWithUnits(f, true);
                    list = Features.Extrudes(t);
                    rlist = Features.Revolves(t);
                    hlist = Features.Holes(t);
                    flist = Features.Fillets(t);
                    clist = Features.Chamfers(t);
                }
                catch (Exception ex) { Console.WriteLine("  {0}: {1}", f, ex.Message); continue; }
                files++;
                string rel = f.Substring(dir.Length).TrimStart('\\');
                foreach (Features.FilletInfo fl in flist)
                {
                    fillets++;
                    string where = Path.GetFileName(f) + ": " + fl.Name + " = " + Features.Describe(fl);
                    if (listAll) Console.WriteLine("  " + rel + ": " + fl.Name + " = " + Features.Describe(fl));
                    count(kinds, "fillet " + fl.FilletType + " chain=" + fl.AutomaticEdgeChain + " ball=" + fl.RollingBallWherePossible
                        + " sharp=" + fl.RollAlongSharpEdges + " preserve=" + fl.PreserveAllFeatures + " smooth=" + fl.SmoothRadiusTransition, where);
                    count(kinds, "fillet sets: " + (fl.Sets.Count > 3 ? "4+" : fl.Sets.Count.ToString()), where);
                    if (fl.FilletType.StartsWith("fillet type")) count(gaps, "fillet: unknown type", where);
                    if (fl.Sets.Count == 0) count(gaps, "fillet: no edge sets", where);
                    foreach (Features.EdgeSet es in fl.Sets)
                    {
                        if (es.Radius == null) count(gaps, "fillet: no radius", where);
                        if (es.Mode != 0 && es.Mode != 4) count(gaps, "fillet: selection mode " + es.Mode, where);
                        if (es.Mode == 0 && es.Edges == 0) count(gaps, "fillet: no edges", where);
                        if (es.Mode == 4) count(kinds, "(fillet set on all rounds)", where);
                        if (es.Unknown1 || es.Unknown2) count(kinds, "(fillet set Booleans " + es.Unknown1 + "/" + es.Unknown2 + ")", where);
                    }
                }
                foreach (Features.ChamferInfo c in clist)
                {
                    chamfers++;
                    string where = Path.GetFileName(f) + ": " + c.Name + " = " + Features.Describe(c);
                    if (listAll) Console.WriteLine("  " + rel + ": " + c.Name + " = " + Features.Describe(c));
                    count(kinds, "chamfer " + c.ChamferType + " chain=" + c.AutomaticEdgeChain + " setback=" + c.CornerSetback + " preserve=" + c.PreserveAllFeatures, where);
                    if (c.ChamferType.StartsWith("chamfer type")) count(gaps, "chamfer: unknown type", where);
                    if (c.Edges == 0) count(gaps, "chamfer: no edges", where);
                    if ((c.Distance ?? c.DistanceOne) == null || (c.ChamferType == "kDistanceAndAngle" && c.Angle == null)
                        || (c.ChamferType == "kTwoDistances" && c.DistanceTwo == null)) count(gaps, "chamfer: missing size", where);
                    if (c.ChamferType != "kDistance" && c.Face == null) count(gaps, "chamfer: no face", where);
                }
                foreach (Features.RevolveInfo r in rlist)
                {
                    revolves++;
                    string where = Path.GetFileName(f) + ": " + r.Name + " = " + Features.Describe(r);
                    if (listAll) Console.WriteLine("  " + rel + ": " + r.Name + " = " + Features.Describe(r));
                    count(kinds, "revolve " + r.Operation + " " + r.Extent + (r.Direction != null ? " " + r.Direction : ""), where);
                    if (r.Sketch == null) count(gaps, "revolve: no sketch", where);
                    if (r.AxisStart == null) count(gaps, "revolve: no axis", where);
                    if (r.Operation.StartsWith("operation") || r.Extent.StartsWith("extent") || (r.Direction ?? "").StartsWith("direction"))
                        count(gaps, "revolve: unknown enum", where);
                    if (r.Extent == "kAngleExtent" && r.Angle == null) count(gaps, "revolve: no angle", where);
                }
                foreach (Features.HoleInfo h in hlist)
                {
                    holes++;
                    string where = Path.GetFileName(f) + ": " + h.Name + " = " + Features.Describe(h);
                    if (listAll) Console.WriteLine("  " + rel + ": " + h.Name + " = " + Features.Describe(h));
                    count(kinds, "hole " + h.HoleType + " " + h.Extent + " " + h.Direction + (h.Thread != null ? " tapped" : "")
                        + (h.FlatBottom == "true" ? " flat" : ""), where);
                    if (h.HoleType.StartsWith("hole type") || h.Extent.StartsWith("extent") || (h.DrillPoint ?? "").StartsWith("drill point"))
                        count(gaps, "hole: unknown enum", where);
                    if (h.Placement == null) count(gaps, "hole: no placement", where);
                    else if (h.Placement.StartsWith("placement")) count(gaps, "hole: placement not decoded", where);
                    else if (h.Placement == "kSketchPlacementType" ? h.CenterPoints.Count == 0 || h.Sketch == null : h.Position == null)
                        count(gaps, "hole: position not found", where);
                    if (h.Axis == null) count(gaps, "hole: no axis", where);
                    if ((h.Thread != null ? h.TapDrill : h.Diameter) == null) count(gaps, "hole: no diameter", where);
                }
                // Thread nodes that no hole or Thread feature owns, or that fail to read.
                int read = Features.Threads(t).Count, threadNodes = 0;
                foreach (Node n in t.Nodes) if (n.Type != null && n.Type.Key == Features.ThreadNode) threadNodes++;
                if (threadNodes != read) count(gaps, "thread nodes not read", Path.GetFileName(f) + ": " + threadNodes + " thread nodes, " + read + " read");
                foreach (KeyValuePair<int, Features.Thread> kv in Features.Threads(t))
                {
                    Node owner = t.At(kv.Key);
                    if (owner == null || owner.Type == null || owner.Type.Key != Features.Feature) count(gaps, "thread: owner not a feature", Path.GetFileName(f));
                    else if (!hlist.Exists(h => h.Index == kv.Key)) count(kinds, "(thread owned by a Thread feature)", Path.GetFileName(f));
                }
                foreach (Features.ExtrudeInfo e in list)
                {
                    extrudes++;
                    string where = Path.GetFileName(f) + ": " + e.Name + " = " + Features.Describe(e);
                    if (listAll) Console.WriteLine("  " + f.Substring(dir.Length).TrimStart('\\') + ": " + e.Name + " = " + Features.Describe(e));
                    count(kinds, e.Operation + " " + e.Extent + " " + e.Direction + (e.ExtentTwo != null ? " +two" : ""), where);
                    if (e.Sketch == null) count(gaps, "no sketch", where);
                    if (e.Operation.StartsWith("operation")) count(gaps, e.Operation, where);
                    if (e.Extent.StartsWith("extent")) count(gaps, e.Extent, where);
                    if (e.Extent == "kToExtent" && e.To == null) count(gaps, "to: no target", where);
                    if (e.Extent == "kToExtent" && e.To != null && e.To.StartsWith("curved")) count(gaps, "to: curved face", where);
                    // The file's copy of the "to" face against the part's own faces (the face may be gone or split since).
                    if (e.ToPlane != null || e.ToCylinder != null)
                    {
                        string wanted = e.ToCylinder != null ? Naming.Cylinder(e.ToCylinder.Origin, e.ToCylinder.Axis, e.ToCylinder.RadiusU) : Naming.Plane(e.ToPlane.Normal, e.ToPlane.Origin);
                        bool there = false;
                        string error;
                        Segment brepSegment = Segment.ReadAll(f).Find(x => x.Name == "PmBRepSegment");
                        List<SabRecord> records = brepSegment == null ? new List<SabRecord>() : Sab.Read(brepSegment.Data, 0, out error);
                        foreach (SabRecord r in records) if (r.Type == "face" && !r.Old && Naming.Canonical(records, r) == wanted) there = true;
                        count(kinds, "(to face copy, " + (e.ToCylinder != null ? "cylinder" : "plane") + (there ? ": a face of the part carries it)" : ": no face of the part carries it)"), where + " wanted " + wanted);
                        if (!there && (listAll || e.ToCylinder != null)) Console.WriteLine("  not in the B-rep: {0} {1}", where, wanted);
                    }
                    if (e.Axis == null) count(gaps, "no axis", where);
                    // The "to" face can't be parallel to the extrude direction, so this tests which vector is the normal.
                    if (e.ToPlane != null && e.Axis != null)
                        count(kinds, Math.Abs(Features.Dot(e.ToPlane.Normal, e.Axis)) > 1e-6 ? "(to face: normal meets the axis)" : "(to face: normal is perpendicular to the axis)", where);
                    if (e.Extent == "kFromToExtent" && (e.From == null || e.To == null)) count(gaps, "from-to: no target", where);
                    if ((e.Distance ?? "") + (e.Taper ?? "") + (e.DistanceTwo ?? "") == "" && e.Extent == "kDistanceExtent") count(gaps, "no distance", where);
                }
            }
            Console.WriteLine("{0} files, {1} extrudes, {2} revolves, {3} holes, {4} fillets, {5} chamfers", files, extrudes, revolves, holes, fillets, chamfers);
            foreach (KeyValuePair<string, int> kv in kinds) Console.WriteLine("  {0,4} x {1}", kv.Value, kv.Key);
            Console.WriteLine("gaps:");
            foreach (KeyValuePair<string, int> kv in gaps) Console.WriteLine("  {0,4} x {1,-20} e.g. {2}", kv.Value, kv.Key, example[kv.Key]);
        }

        // Across a folder: B-rep naming attribute names (final body / earlier states), the node types the DC naming
        // table (CCE92042) lists, and the node types that refer into that table.
        static void NamingCensusCmd(string dir)
        {
            SortedDictionary<string, int> attribs = new SortedDictionary<string, int>(), entries = new SortedDictionary<string, int>(),
                users = new SortedDictionary<string, int>();
            Action<SortedDictionary<string, int>, string> count = (d, k) => { int c; d.TryGetValue(k, out c); d[k] = c + 1; };
            int files = 0, failed = 0;
            foreach (string f in Directory.GetFiles(dir, "*.ipt", SearchOption.AllDirectories))
            {
                List<Segment> segs;
                try { segs = Segment.ReadAll(f); } catch (Exception) { failed++; continue; }
                files++;
                foreach (Segment s in segs)
                {
                    if (s.Name != "PmBRepSegment") continue;
                    string error;
                    foreach (SabRecord r in Sab.Read(s.Data, 0, out error))
                        if (r.Type != null && r.Type.EndsWith("attrib") && r.Fields.Count > 5 && r.Fields[5] is string)
                            count(attribs, (string)r.Fields[5] + (r.Old ? " (earlier state)" : ""));
                    if (error != null && !error.Contains("End-of")) count(attribs, "(parse error: " + error.Substring(0, Math.Min(30, error.Length)) + ")");
                }
                NodeTable t;
                try { t = Table(FindSegment(segs, null)); } catch (Exception) { continue; }
                foreach (Node n in t.Nodes)
                {
                    if (n.Type == null || n.Data == null) continue;
                    if (n.Type.Key == 0xCCE92042)
                    {
                        // {u32 count at +0x0E (2026; +0x0A before), then {u32 index, xref entry}...}: scan for xrefs.
                        for (int p = 0x12; p + 4 <= n.Data.Length; p += 4)
                        {
                            Node e = t.At(Sketches.XRef(n.Data, p));
                            if (e != null && e.Type != null && e.Index != n.Index) count(entries, e.TypeName);
                        }
                        continue;
                    }
                    for (int p = 0; p + 8 <= n.Data.Length; p++)
                    {
                        Node e = t.At(Sketches.XRef(n.Data, p));
                        if (e == null || e.Type == null || e.Type.Key != 0xCCE92042) continue;
                        count(users, n.TypeName);
                        break;
                    }
                }
            }
            Console.WriteLine("{0} parts ({1} unreadable)", files, failed);
            Console.WriteLine("B-rep attributes:");
            foreach (KeyValuePair<string, int> kv in attribs) Console.WriteLine("  {0,7} x {1}", kv.Value, kv.Key);
            Console.WriteLine("naming table entries:");
            foreach (KeyValuePair<string, int> kv in entries) Console.WriteLine("  {0,7} x {1}", kv.Value, kv.Key);
            Console.WriteLine("nodes referring to a naming table:");
            foreach (KeyValuePair<string, int> kv in users) Console.WriteLine("  {0,7} x {1}", kv.Value, kv.Key);
        }

        // Feature inputs (edges and faces) across a folder: how many resolve to the B-rep, by feature class and kind.
        static void EdgeScanCmd(string dir)
        {
            SortedDictionary<string, int[]> by = new SortedDictionary<string, int[]>();   // {resolved, total}
            Dictionary<string, string> example = new Dictionary<string, string>();
            int[] outcome = new int[8];   // B-rep, rebuilt, named only, dead, unresolved, where its faces meet, its faces' surfaces, by a chain of names
            int files = 0, deadFeatures = 0;
            List<string> allDead = new List<string>();
            foreach (string f in Directory.GetFiles(dir, "*.ipt", SearchOption.AllDirectories))
            {
                NodeTable t;
                Dictionary<int, Naming.Resolved> all;
                try
                {
                    List<Segment> segs = Segment.ReadAll(f);
                    t = Table(FindSegment(segs, null));
                    all = Naming.Resolve(t, FindSegment(segs, "PmBRepSegment").Data);
                }
                catch (Exception) { continue; }
                files++;
                foreach (Features.Info fi in Features.List(t))
                {
                    int inputs = 0, dead = 0;
                    foreach (int i in Naming.Inputs(t, fi.Node))
                    {
                        Naming.Resolved r;
                        if (!all.TryGetValue(i, out r)) continue;
                        int o = r.Entities.Count > 0 ? 0 : r.Rebuilt != null && (r.Rebuilt.Dead || r.Rebuilt.Shape != null) ? (r.Rebuilt.Dead ? 3 : 1)
                            : r.FromFaces != null ? 5 : r.Surface1 != null && r.Surface2 != null ? 6 : r.ByChain ? 7 : r.Rebuilt != null ? 2 : 4;
                        outcome[o]++;
                        inputs++;
                        if (o == 3) dead++;
                        if (o != 4) continue;
                        string key = string.Format("{0}{1:X8} {2} via {3}", (fi.State & 4) != 0 ? "[not computed] " : "", fi.Kind, r.Item.TypeName, r.Id == null ? "(no id)" : r.Id.TypeName);
                        key += " -> entries " + string.Join(",", new List<string>(new SortedSet<string>(r.EntryTypes)).ToArray());
                        if (r.FacesKnown >= 0) key += ", " + r.FacesKnown + " of 2 faces known (" + r.FaceKinds + ")";
                        if (r.Unconfirmed.Count > 0) key += r.AtVertices ? ", shared edge at its vertices" : ", shared edge unconfirmed";
                        if (!by.ContainsKey(key)) by[key] = new int[2];
                        by[key][1]++;
                        if (!example.ContainsKey(key)) example[key] = Path.GetFileName(f) + ": " + fi.Name + " " + Naming.Describe(r);
                    }
                    // A check on "dead": a feature Inventor computes must have some input that is not.
                    // ... unless, like these, it made nothing: no fillet or chamfer faces of its tag in the B-rep.
                    if (dead > 0)
                    {
                        deadFeatures++;
                        if (dead == inputs && (fi.State & 4) == 0)
                            allDead.Add(Path.GetFileName(f) + ": " + fi.Name + " (" + dead + " inputs; "
                                + (Naming.MadeFaces(FindSegment(Segment.ReadAll(f), "PmBRepSegment").Data, fi.Tag) ? "CONTRADICTION: it made faces" : "it made no faces") + ")");
                    }
                }
            }
            Console.WriteLine("{0} features have dead inputs; computed ones with every input dead: {1}", deadFeatures, allDead.Count == 0 ? "none" : string.Join(", ", allDead.ToArray()));
            Console.WriteLine("{0} parts; {1} feature inputs (edges and faces):", files, outcome[0] + outcome[1] + outcome[2] + outcome[3] + outcome[4] + outcome[5] + outcome[6] + outcome[7]);
            Console.WriteLine("  {0,5} found in the B-rep\n  {1,5} rebuilt from the sketch (the B-rep no longer has them)\n  {5,5} where their two faces meet (the B-rep no longer has them)\n  {6,5} between two faces whose surfaces are known (a curve not rebuilt)\n  {7,5} between faces findable by rebuilding in order (fillet faces of findable edges; no geometry here)\n  {2,5} named, but not rebuilt (revolve, mirror/pattern copy, taper, or an extent without a distance)\n  {3,5} dead: what they are named by was deleted\n  {4,5} unresolved:",
                outcome[0], outcome[1], outcome[2], outcome[3], outcome[4], outcome[5], outcome[6], outcome[7]);
            foreach (KeyValuePair<string, int[]> kv in by)
            {
                string e;
                example.TryGetValue(kv.Key, out e);
                Console.WriteLine("  {0,5}   {1}{2}", kv.Value[1], kv.Key, e == null ? "" : "\n          e.g. " + e);
            }
        }

        // Resolved feature edges and faces vs truth: every generated part under a folder (its .json alongside),
        // or one part against a given truth file.
        static void EdgeCheckCmd(string path, string truth)
        {
            List<string[]> pairs = new List<string[]>();
            if (truth != null) pairs.Add(new[] { path, truth });
            else foreach (string f in Directory.GetFiles(path, "*.ipt", SearchOption.AllDirectories))
                    if (File.Exists(Path.ChangeExtension(f, ".json"))) pairs.Add(new[] { f, Path.ChangeExtension(f, ".json") });
            int parts = 0, ok = 0, total = 0;
            foreach (string[] p in pairs)
            {
                List<Segment> segs = Segment.ReadAll(p[0]);
                int n;
                List<string> bad = Naming.Compare(Table(FindSegment(segs, null)), FindSegment(segs, "PmBRepSegment").Data, File.ReadAllText(p[1]), out n);
                if (n == 0 && bad.Count == 0) continue;
                parts++;
                total += n;
                if (bad.Count == 0) { ok++; continue; }
                Console.WriteLine("  {0}:", truth != null ? p[0] : p[0].Substring(path.Length).TrimStart('\\'));
                foreach (string b in bad) Console.WriteLine("    " + b);
            }
            Console.WriteLine("{0} parts, {1} edges and faces checked; {2} parts match Inventor exactly", parts, total, ok);
        }

        // Every block of ASM data in a segment, record by record.
        static void BrepCmd(string file, string segment, bool names)
        {
            Segment s = FindSegment(Segment.ReadAll(file), segment);
            int at = 0, blocks = 0;
            while ((at = Sab.Find(s.Data, at)) >= 0)
            {
                string error;
                List<SabRecord> history;
                List<SabRecord> records = Sab.Read(s.Data, at, out error, out history);
                Console.WriteLine("== ASM data at 0x{0:X}: {1} entities ({2} from earlier states, marked *), {3} history records{4}", at,
                    records.Count, records.FindAll(r => r.Old).Count, history.Count, error == null ? "" : "; " + error);
                Console.Write(names ? Sab.Names(records) : Sab.Dump(records));
                if (!names && history.Count > 0)
                {
                    Console.WriteLine("-- history records (not numbered):");
                    Console.Write(Sab.Dump(history));
                }
                at++;
                blocks++;
            }
            if (blocks == 0) Console.WriteLine("no ASM data in " + s.Name);
        }

        static void NodesCmd(string file, string segment)
        {
            Segment s = FindSegment(Segment.ReadAll(file), segment);
            NodeTable t = Table(s);
            Console.WriteLine("{0} {1}: {2} nodes, {3} types, data 0x{4:X} bytes, blocks end at 0x{5:X}{6}",
                file, s.Name, t.Nodes.Count, t.Types.Count, t.DataLength, t.End, t.Error == null ? "" : "\n  ERROR: " + t.Error);
            foreach (Node n in t.Nodes)
            {
                if (n.Data == null) { Console.WriteLine("  #{0,-4:X} (no block)", n.Index); continue; }
                string label = NodeTable.Label(n);
                Console.WriteLine("  #{0,-4:X} @{1,-6:X} {2,6} {3,-24} {4}", n.Index, n.Offset, n.Data.Length, n.TypeName, label == null ? "" : "\"" + label + "\"");
            }
        }

        static void CheckCmd(string dir)
        {
            int files = 0, segs = 0, ok = 0, nodes = 0;
            Dictionary<string, int> failures = new Dictionary<string, int>();
            SortedDictionary<string, int> footers = new SortedDictionary<string, int>();
            List<string> files_ = new List<string>(Directory.GetFiles(dir, "*.ipt", SearchOption.AllDirectories));
            files_.AddRange(Directory.GetFiles(dir, "*.iam", SearchOption.AllDirectories));
            foreach (string f in files_)
            {
                files++;
                List<Segment> list;
                try { list = Segment.ReadAll(f); }
                catch (Exception ex) { Console.WriteLine("  {0}: {1}", f, ex.Message); continue; }
                foreach (Segment s in list)
                {
                    segs++;
                    NodeTable t = Table(s);
                    if (t.Error == null)
                    {
                        ok++;
                        nodes += t.Nodes.Count;
                        string fk = s.Name + " footer " + (t.DataLength - t.End);
                        int fc;
                        footers.TryGetValue(fk, out fc);
                        footers[fk] = fc + 1;
                        continue;
                    }
                    string key = s.Name + ": " + System.Text.RegularExpressions.Regex.Replace(t.Error, @"#?[0-9A-F]+ at 0x[0-9A-F]+|0x[0-9A-F]+", "N");
                    int c;
                    failures.TryGetValue(key, out c);
                    if (c < 2) Console.WriteLine("  {0} {1}: {2}", Path.GetFileName(f), s.Name, t.Error);
                    failures[key] = c + 1;
                }
            }
            Console.WriteLine("\n{0} files, {1} segments, {2} split cleanly ({3:N0} nodes)", files, segs, ok, nodes);
            foreach (KeyValuePair<string, int> kv in failures) Console.WriteLine("  {0,5} x {1}", kv.Value, kv.Key);
            Console.WriteLine("Bytes after the last block (segment footer):");
            foreach (KeyValuePair<string, int> kv in footers) if (kv.Value > 2) Console.WriteLine("  {0,5} x {1}", kv.Value, kv.Key);
        }

        // Nodes whose data contain a cross ref (bit 31) or a plausible child ref to the given node.
        static void RefsCmd(string[] args)
        {
            NodeTable t = Table(FindSegment(Segment.ReadAll(args[1]), args[2]));
            for (int a = 3; a < args.Length; a++)
            {
                int target = Convert.ToInt32(args[a], 16);
                uint x = (uint)(target + 1) | 0x80000000, c = (uint)(target + 1);
                Console.WriteLine("{0} is referenced by:", t.Describe(target));
                foreach (Node n in t.Nodes)
                {
                    if (n.Data == null) continue;
                    for (int p = 0; p + 4 <= n.Data.Length; p++)
                    {
                        uint v = BitConverter.ToUInt32(n.Data, p);
                        if (v != x && v != c) continue;
                        Console.WriteLine("  {0} +{1:X4} {2}", t.Describe(n.Index), p, v == x ? "cross ref" : "child ref?");
                    }
                }
            }
        }

        static void NodeCmd(string[] args)
        {
            NodeTable t = Table(FindSegment(Segment.ReadAll(args[1]), args[2]));
            for (int i = 3; i < args.Length; i++) Console.Write(t.View(Convert.ToInt32(args[i], 16)));
        }

        static void NodeDiffCmd(string[] args)
        {
            string only = null;
            bool full = false;
            int maxHunks = 12;
            for (int i = 3; i < args.Length; i++)
            {
                if (args[i] == "--full") full = true;
                else if (args[i] == "--max") maxHunks = int.Parse(args[++i]);
                else only = args[i];
            }
            List<Segment> sa = Segment.ReadAll(args[1]), sb = Segment.ReadAll(args[2]);
            Console.WriteLine("A: {0}\nB: {1}", args[1], args[2]);
            foreach (Segment a in sa)
            {
                if (only != null && !string.Equals(a.Name, only, StringComparison.OrdinalIgnoreCase)) continue;
                Segment b = null;
                foreach (Segment x in sb) if (x.Name == a.Name) b = x;
                if (b == null) { Console.WriteLine("\n== {0}: only in A", a.Name); continue; }
                NodeTable ta = Table(a), tb = Table(b);
                Console.WriteLine("\n== {0}: A {1} nodes, B {2} nodes", a.Name, ta.Nodes.Count, tb.Nodes.Count);
                Console.Write(NodeDiff.Report(ta, tb, full, maxHunks));
            }
        }

        static bool ContainsName(List<Segment> list, string name)
        {
            foreach (Segment s in list) if (s.Name == name) return true;
            return false;
        }
    }
}
