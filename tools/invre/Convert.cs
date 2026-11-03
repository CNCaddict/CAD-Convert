// invre convert: Inventor file -> neutral model -> the target's own engine -> a native file and a report.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace InvRe
{
    public static class Converter
    {
        public static string FindFreeCad()
        {
            string env = Environment.GetEnvironmentVariable("CADCONVERT_FREECAD");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            // The FreeCAD packaged with the converter (see tools\package.cmd) comes first.
            string packaged = Path.Combine(AppFolder(), "engine", "bin", "FreeCADCmd.exe");
            if (File.Exists(packaged)) return packaged;
            List<string> roots = new List<string> {
                Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "CADConvert"),
                Environment.GetEnvironmentVariable("ProgramFiles") ?? "",
                Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Programs") };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string[] dirs = Directory.GetDirectories(root, "FreeCAD*");
                Array.Sort(dirs);
                Array.Reverse(dirs);
                foreach (string d in dirs)
                    foreach (string exe in new[] { Path.Combine(d, "FreeCADCmd.exe"), Path.Combine(d, "bin", "FreeCADCmd.exe") })
                        if (File.Exists(exe)) return exe;
            }
            return null;
        }

        static string AppFolder() { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }

        // The scripts sit beside invre.exe in the package, and one level up from tools\bin in the source folder.
        static string ScriptPath()
        {
            string packaged = Path.Combine(AppFolder(), "freecad", "build_model.py");
            return File.Exists(packaged) ? packaged : Path.GetFullPath(Path.Combine(AppFolder(), "..", "freecad", "build_model.py"));
        }

        // Runs FreeCAD on a model; returns the report's text, or null with the reason.
        public static string RunFreeCad(string modelPath, string outFile, string reportPath, string stepPath, out string error, int seconds = 300, bool solidOnly = false)
        {
            error = null;
            string exe = FindFreeCad();
            if (exe == null) { error = "FreeCAD not found"; return null; }
            if (File.Exists(reportPath)) File.Delete(reportPath);
            ProcessStartInfo psi = new ProcessStartInfo(exe, "\"" + ScriptPath() + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.EnvironmentVariables["CADCONVERT_MODEL"] = modelPath;
            psi.EnvironmentVariables["CADCONVERT_OUT"] = outFile;
            psi.EnvironmentVariables["CADCONVERT_REPORT"] = reportPath;
            psi.EnvironmentVariables["CADCONVERT_TOOLS"] = Path.GetDirectoryName(ScriptPath());
            if (stepPath != null) psi.EnvironmentVariables["CADCONVERT_STEP"] = stepPath;
            if (solidOnly) psi.EnvironmentVariables["CADCONVERT_SOLID_ONLY"] = "1";
            Private(psi);
            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(seconds * 1000))
                {
                    KillTree(p);
                    error = "FreeCAD didn't finish within " + seconds + " s";
                    return null;
                }
            }
            if (!File.Exists(reportPath)) { error = "FreeCAD wrote no report"; return null; }
            return File.ReadAllText(reportPath, Encoding.UTF8);
        }

        // FreeCADCmd.exe hands over to a second process, which Kill alone leaves running.
        static void KillTree(Process p)
        {
            try
            {
                using (Process t = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { UseShellExecute = false, CreateNoWindow = true }))
                    t.WaitForExit(10000);
            }
            catch (Exception) { }
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
        }

        static string Base(string file)
        {
            string[] pieces = file.Split('*');
            return Path.GetFileNameWithoutExtension(pieces[pieces.Length - 1]);
        }

        static string Q(string s)
        {
            return s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // Finds files an assembly refers to: where its stored path says, else by name under the folder above it.
        class Finder
        {
            readonly Dictionary<string, string> byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Finder(string root)
            {
                foreach (string pattern in new[] { "*.ipt", "*.iam" })
                    foreach (string f in Directory.GetFiles(root, pattern, SearchOption.AllDirectories))
                        if (f.IndexOf("OldVersions", StringComparison.OrdinalIgnoreCase) < 0 && !byName.ContainsKey(Path.GetFileName(f))) byName[Path.GetFileName(f)] = f;
            }

            public string Locate(string stored)
            {
                if (stored == null) return null;
                string outer, path;
                Segment.Inside(stored, out outer);
                if (File.Exists(outer)) return stored;
                string name = outer.Substring(outer.LastIndexOfAny(new[] { '\\', '/' }) + 1);
                return byName.TryGetValue(name, out path) ? path + stored.Substring(outer.Length) : null;
            }
        }

        // What each assembly's file is called, where that isn't the name of the assembly.
        static readonly Dictionary<string, string> AssemblyFiles = new Dictionary<string, string>();

        static string OutputName(string path)
        {
            string name;
            if (path.EndsWith(".iam", StringComparison.OrdinalIgnoreCase) && AssemblyFiles.TryGetValue(Base(path).ToLowerInvariant(), out name)) return name;
            return Base(path);
        }

        // An assembly: its parts first (a few at a time, one process each), sub-assemblies below it, then links to them all.
        static int ConvertAssembly(string file, string outDir, TextWriter log, Finder finder, HashSet<string> done, bool solidCraft = false)
        {
            string reports = Path.Combine(outDir, "reports");
            string extension = solidCraft ? ".sc" : ".FCStd";
            Directory.CreateDirectory(reports);
            string name = Base(file);
            done.Add("iam:" + name.ToLowerInvariant());
            List<Segment> segs = Segment.ReadAll(file);
            Func<string, NodeTable> table = n => { Segment s = segs.Find(x => x.Name == n); return s == null ? null : NodeTable.Parse(s.Meta, s.Data); };
            NodeTable dc = table("AmDcSegment");
            if (dc == null) throw new Exception("no assembly data in " + file);
            List<Occurrence> occurrences = Assemblies.Read(dc, table("AmRxSegment"), table("DesignViewSegment"), file);

            Dictionary<Occurrence, string> found = new Dictionary<Occurrence, string>();
            List<string> parts = new List<string>();
            foreach (Occurrence o in occurrences)
            {
                string path = finder.Locate(o.Path);
                if (path == null) continue;
                found[o] = path;
                string b = (path.EndsWith(".iam", StringComparison.OrdinalIgnoreCase) ? "iam:" : "ipt:") + Base(path).ToLowerInvariant();
                if (done.Contains(b)) continue;
                if (path.EndsWith(".iam", StringComparison.OrdinalIgnoreCase))
                {
                    log.WriteLine("sub-assembly " + Base(path));
                    ConvertAssembly(path, outDir, log, finder, done, solidCraft);
                }
                else
                {
                    done.Add(b);
                    // Told to reuse parts: one already converted here stays, as joints are all that's being worked on.
                    if (Environment.GetEnvironmentVariable("CADCONVERT_REUSE_PARTS") != "1" || !File.Exists(Path.Combine(outDir, Base(path) + extension)) || !File.Exists(Path.Combine(reports, Base(path) + ".view"))) parts.Add(path);
                }
            }
            // An assembly holding a part of its own name (Bracket.iam, bracket.ipt) can't share that file, or it links to itself.
            foreach (Occurrence o in occurrences)
            {
                string held;
                if (found.TryGetValue(o, out held) && held.EndsWith(".ipt", StringComparison.OrdinalIgnoreCase) && string.Equals(Base(held), name, StringComparison.OrdinalIgnoreCase)) { name += " assembly"; break; }
            }
            AssemblyFiles[Base(file).ToLowerInvariant()] = name;
            string exe = Assembly.GetExecutingAssembly().Location;
            int[] codes = new int[parts.Count];
            System.Threading.Tasks.Parallel.For(0, parts.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, (solidCraft ? "solidcraft-part" : "convert") + " \"" + parts[i] + "\" \"" + outDir + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginErrorReadLine();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    codes[i] = p.ExitCode;
                }
            });
            int rebuilt = 0, solid = 0, other = 0;
            foreach (int c in codes) { if (c == 0) rebuilt++; else if (c == 3) solid++; else other++; }
            if (parts.Count > 0) log.WriteLine("  {0}: {1} parts converted: {2} with full history, {3} with Inventor's exact solid added, {4} other", name, parts.Count, rebuilt, solid, other);

            List<string> rows = new List<string>();
            foreach (Occurrence o in occurrences)
            {
                string path;
                string target = found.TryGetValue(o, out path) ? Path.GetFullPath(Path.Combine(outDir, OutputName(path) + extension)) : null;
                List<string> m = new List<string>();
                if (o.Matrix != null) foreach (double v in o.Matrix) m.Add(v.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                rows.Add("{\"name\": " + Q(o.Name ?? ("occurrence " + o.Number)) + ", \"file\": " + Q(o.File) + ", \"target\": " + Q(target) + ", \"matrix\": " + (o.Matrix == null ? "null" : "[" + string.Join(", ", m.ToArray()) + "]")
                    + ", \"suppressed\": " + (o.Suppressed ? "true" : "false") + ", \"visible\": " + (o.Visible ? "true" : "false") + ", \"flexible\": " + (o.Flexible ? "true" : "false") + ", \"grounded\": " + (o.Grounded ? "true" : "false") + "}");
            }
            // Constraints and joints: which occurrences (by place in the list above, -1 for the assembly's own planes) and the geometry each holds, in the assembly's coordinates.
            List<string> mates = new List<string>();
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (MateItem c in Assemblies.Constraints(dc, table("AmBrowserSegment"), occurrences, table("AmRxSegment")))
            {
                List<string> who = new List<string>(), what = new List<string>(), values = new List<string>(), axes = new List<string>();
                foreach (double[] a in c.Axes) axes.Add(a == null ? "null" : "[" + a[0].ToString("R", inv) + ", " + a[1].ToString("R", inv) + ", " + a[2].ToString("R", inv) + "]");
                foreach (int n in c.Occurrences) who.Add(occurrences.FindIndex(x => x.Dc == n).ToString(inv));
                foreach (string g in c.Geometry) what.Add(Q(g));
                foreach (double v in c.Values) values.Add(v.ToString("R", inv));
                mates.Add("{\"name\": " + Q(c.Name) + ", \"kind\": " + Q(c.Kind) + ", \"type\": " + c.Type + ", \"suppressed\": " + (c.Suppressed ? "true" : "false") + ", \"failing\": " + (c.Failing ? "true" : "false") + ", \"nested\": " + (c.Nested ? "true" : "false")
                    + ", \"occurrences\": [" + string.Join(", ", who.ToArray()) + "], \"geometry\": [" + string.Join(", ", what.ToArray()) + "], \"axes\": [" + string.Join(", ", axes.ToArray()) + "], \"values\": [" + string.Join(", ", values.ToArray()) + "], \"settings\": " + c.Settings + "}");
            }
            string model = Path.GetFullPath(Path.Combine(reports, name + ".model.json"));
            string report = Path.GetFullPath(Path.Combine(reports, name + (solidCraft ? ".result.json" : ".report.json")));
            string outFile = Path.GetFullPath(Path.Combine(outDir, name + extension));
            File.WriteAllText(model, "{\"name\": " + Q(name) + ", \"occurrences\": [\n  " + string.Join(",\n  ", rows.ToArray()) + "\n], \"constraints\": [\n  " + string.Join(",\n  ", mates.ToArray()) + "\n]}\n", new UTF8Encoding(false));
            if (File.Exists(report)) File.Delete(report);
            string builder = solidCraft ? Path.Combine(Path.GetDirectoryName(ScriptPath()), "..", "solidcraft", "build_assembly.py") : Path.Combine(Path.GetDirectoryName(ScriptPath()), "build_assembly.py");
            ProcessStartInfo run = new ProcessStartInfo(FindFreeCad(), "\"" + Path.GetFullPath(builder) + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            run.EnvironmentVariables["CADCONVERT_TOOLS"] = Path.GetDirectoryName(ScriptPath());
            run.EnvironmentVariables["CADCONVERT_MODEL"] = model;
            run.EnvironmentVariables["CADCONVERT_OUT"] = outFile;
            run.EnvironmentVariables["CADCONVERT_REPORT"] = report;
            Private(run);
            Action<ProcessStartInfo> go = psi =>
            {
                using (Process p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { };
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    // SolidCraft reloads every linked part for each run, which takes minutes on the biggest assemblies.
                    if (!p.WaitForExit(solidCraft ? 3600000 : 600000)) { KillTree(p); }
                }
            };
            go(run);
            // If the solver takes FreeCAD down, the parts can still go where they belong, just with no joints.
            if (!File.Exists(report) && !solidCraft)
            {
                log.WriteLine("  FreeCAD stopped while making the joints of " + name + ": converting it without them");
                run.EnvironmentVariables["CADCONVERT_NO_JOINTS"] = "1";
                go(run);
            }
            if (!File.Exists(report)) { log.WriteLine("  no report for " + name); return 2; }
            System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> r = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(report, Encoding.UTF8));
            if (r.ContainsKey("fatal")) { log.WriteLine("  fatal: " + r["fatal"]); return 2; }
            object[] missing = ToArray(r["missing"]);
            log.WriteLine("  {0}: {1} occurrences, {2} placed, {3} suppressed, {4} not found", name, r["occurrences"], r["linked"], r["suppressed"], missing.Length);
            foreach (object m in missing) log.WriteLine("    not found: " + m);
            foreach (object n in ToArray(r["notes"])) log.WriteLine("    note: " + n);
            if (r.ContainsKey("volume")) log.WriteLine("  solid volume {0:0.###} mm3 in {1} solids", ToD(r["volume"]), r["solids"]);
            if (r.ContainsKey("joints"))
            {
                Dictionary<string, object> j = (Dictionary<string, object>)r["joints"];
                log.WriteLine("  constraints and joints: {0} of {1} carried, {2} left out, parts moved by the solver up to {3:0.###} mm", j["carried"], j["total"], j["left_out"], ToD(j["moved_mm"]));
                foreach (object o in ToArray(j["why"])) log.WriteLine("    left out: " + o);
            }
            log.WriteLine("  " + (missing.Length == 0 ? "PLACED" : "INCOMPLETE") + "  " + outFile);
            return missing.Length == 0 ? 0 : 1;
        }

        public static int Convert(string file, string outDir, TextWriter log)
        {
            if (file.EndsWith(".iam", StringComparison.OrdinalIgnoreCase)) return ConvertAssemblyFile(file, outDir, log, false);

            string reports = Path.Combine(outDir, "reports");
            Directory.CreateDirectory(reports);
            string name = Path.GetFileNameWithoutExtension(file.Split('*')[file.Split('*').Length - 1]);
            string model = Path.GetFullPath(Path.Combine(reports, name + ".model.json"));
            string report = Path.GetFullPath(Path.Combine(reports, name + ".report.json"));
            string target = Path.GetFullPath(Path.Combine(outDir, name + ".FCStd"));
            string stepFile = Path.GetFullPath(Path.Combine(outDir, name + ".step"));
            ReadPart(file, name, model, ref stepFile, log);
            string error;
            int limit;
            if (!int.TryParse(Environment.GetEnvironmentVariable("CADCONVERT_PART_SECONDS"), out limit) || limit <= 0) limit = 300;
            string text = RunFreeCad(model, target, report, stepFile, out error, limit);
            if (text == null && stepFile != null && File.Exists(stepFile))
            {
                // A part whose history takes too long still gets a file, with Inventor's solid, so assemblies aren't missing it.
                log.WriteLine("  " + error + " rebuilding the history; writing Inventor's exact solid instead");
                text = RunFreeCad(model, target, report, stepFile, out error, 120, true);
            }
            if (text == null) { log.WriteLine("  failed: " + error); return 1; }
            int code = Summarize(text, target, log);
            if (File.Exists(target)) { string why = ExportView(target); if (why != null) log.WriteLine("  no picture for the viewer: " + why); }
            return code;
        }

        static int ConvertAssemblyFile(string file, string outDir, TextWriter log, bool solidCraft)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(file));
            string above = Path.GetDirectoryName(folder);
            // Parts may sit beside it or one level up, but never search a whole drive or the user's Desktop root.
            string root = above != null && Directory.GetFiles(above, "*.iam").Length + Directory.GetFiles(above, "*.ipt").Length > 0 ? above : folder;
            return ConvertAssembly(file, outDir, log, new Finder(root), new HashSet<string>(), solidCraft);
        }

        public static string FindSolidCraft()
        {
            string env = Environment.GetEnvironmentVariable("CADCONVERT_SOLIDCRAFT");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            string installed = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "SolidCraft", "solidcraft-cli.exe");
            return File.Exists(installed) ? installed : null;
        }

        // SolidCraft's output, in a folder of its own under the one saved to: a part or an assembly with everything it uses.
        public static int ConvertSolidCraft(string file, string outDir, TextWriter log)
        {
            if (FindSolidCraft() == null) { log.WriteLine("  failed: SolidCraft isn't installed"); return 2; }
            if (FindFreeCad() == null) { log.WriteLine("  failed: FreeCAD not found, and the check against Inventor needs it"); return 2; }
            string folder = Path.Combine(outDir, "SolidCraft");
            Directory.CreateDirectory(Path.Combine(folder, "reports"));
            if (file.EndsWith(".iam", StringComparison.OrdinalIgnoreCase)) return ConvertAssemblyFile(file, folder, log, true);
            return SolidCraftPart(file, folder, log);
        }

        // One part into its folder: arrives here from the app, or in its own process from an assembly.
        public static int SolidCraftPart(string file, string folder, TextWriter log)
        {
            string reports = Path.Combine(folder, "reports");
            Directory.CreateDirectory(reports);
            string name = Path.GetFileNameWithoutExtension(file.Split('*')[file.Split('*').Length - 1]);
            string model = Path.GetFullPath(Path.Combine(reports, name + ".model.json"));
            string report = Path.GetFullPath(Path.Combine(reports, name + ".result.json"));
            string target = Path.GetFullPath(Path.Combine(folder, name + ".sc"));
            string stepFile = Path.GetFullPath(Path.Combine(folder, name + ".step"));
            ReadPart(file, name, model, ref stepFile, log);
            if (File.Exists(report)) File.Delete(report);
            string builder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ScriptPath()), "..", "solidcraft", "build_part.py"));
            ProcessStartInfo psi = new ProcessStartInfo(FindFreeCad(), "\"" + builder + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.EnvironmentVariables["CADCONVERT_MODEL"] = model;
            psi.EnvironmentVariables["CADCONVERT_OUT"] = target;
            psi.EnvironmentVariables["CADCONVERT_REPORT"] = report;
            psi.EnvironmentVariables["CADCONVERT_VIEW"] = Path.GetFullPath(Path.Combine(reports, name + ".view"));
            psi.EnvironmentVariables["CADCONVERT_TOOLS"] = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ScriptPath()), ".."));
            psi.EnvironmentVariables["CADCONVERT_SOLIDCRAFT"] = FindSolidCraft();
            if (stepFile != null) psi.EnvironmentVariables["CADCONVERT_STEP"] = stepFile;
            Private(psi);
            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                // Rebuilding (up to 6 minutes of commands, then saving), checking, and Inventor's solid as the fallback.
                if (!p.WaitForExit(1200000))
                {
                    KillTree(p);
                    log.WriteLine("  failed: SolidCraft's build didn't finish in 20 minutes");
                    return 2;
                }
            }
            if (!File.Exists(report)) { log.WriteLine("  failed: no result written"); return 2; }
            System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> r = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(report, Encoding.UTF8));
            foreach (object n in ToArray(r["notes"])) log.WriteLine("    note: " + n);
            if (r.ContainsKey("fatal")) { log.WriteLine("  failed: " + r["fatal"]); return 2; }
            string outcome = (string)r["outcome"];
            int left = ToArray(r["left"]).Length;
            if (outcome == "rebuilt") { log.WriteLine("  REBUILT{0}  {1}", left > 0 ? " (" + left + " sketch rules or features left out)" : "", target); return 0; }
            if (outcome == "solid") { log.WriteLine("  EXACT SOLID  {0}", target); return 3; }
            if (outcome == "flawed") { log.WriteLine("  NEEDS CHECKING  {0}", target); return 4; }
            log.WriteLine("  failed: no solid in the file and nothing could be rebuilt");
            return 2;
        }

        // The neutral model and Inventor's solid, written where they're asked for. stepFile comes back null if there's no solid.
        static void ReadPart(string file, string name, string model, ref string stepFile, TextWriter log)
        {
            List<Segment> segs = Segment.ReadAll(file);
            Segment solid = segs.Find(s => string.Equals(s.Name, "PmBRepSegment", StringComparison.OrdinalIgnoreCase));
            StepWriter sw = new StepWriter();
            string stepText = null;
            try { if (solid != null) stepText = sw.Write(solid.Data, name); }
            catch (Exception ex) { log.WriteLine("  STEP not written: " + ex.Message); }
            if (stepText != null) File.WriteAllText(stepFile, stepText, new UTF8Encoding(false));
            else { stepFile = null; }
            foreach (KeyValuePair<string, int> kv in sw.NotCarried) log.WriteLine("  STEP gap: {0} x {1}", kv.Value, kv.Key);
            NodeTable t = null;
            foreach (Segment s in segs)
            {
                if (s.Name == "PmAppSegment" || s.Name == "AmAppSegment") Units.UseDocumentUnits(NodeTable.Parse(s.Meta, s.Data));
                if (s.Name == "PmDCSegment" || s.Name == "AmDcSegment") t = NodeTable.Parse(s.Meta, s.Data);
            }
            if (t == null) throw new Exception("no design data in " + file);
            File.WriteAllText(model, CadModel.Json(t, segs, file), new UTF8Encoding(false));
        }

        // What the viewer shows of a converted part (export_view.py), beside its report. Its own process: if FreeCAD can't
        // mesh something, the conversion has still happened. Returns why not, or null.
        public static string ExportView(string target)
        {
            string dir = Path.GetDirectoryName(target), name = Path.GetFileNameWithoutExtension(target);
            string report = Path.Combine(dir, "reports", name + ".report.json"), view = Path.Combine(dir, "reports", name + ".view"), step = Path.Combine(dir, name + ".step");
            foreach (string old in new[] { view, view + ".tmp", view + ".error.txt" }) if (File.Exists(old)) File.Delete(old);
            ProcessStartInfo psi = new ProcessStartInfo(FindFreeCad(), "\"" + Path.Combine(Path.GetDirectoryName(ScriptPath()), "export_view.py") + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.EnvironmentVariables["CADCONVERT_FC"] = target;
            psi.EnvironmentVariables["CADCONVERT_REPORT"] = report;
            psi.EnvironmentVariables["CADCONVERT_VIEW"] = view;
            if (File.Exists(step)) psi.EnvironmentVariables["CADCONVERT_STEP"] = step;
            Private(psi);
            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(180000))
                {
                    KillTree(p);
                    return "meshing didn't finish within 180 s";
                }
            }
            if (File.Exists(view)) return null;
            string[] lines = File.Exists(view + ".error.txt") ? File.ReadAllLines(view + ".error.txt") : new string[0];
            return lines.Length > 0 ? lines[lines.Length - 1] : "FreeCAD wrote nothing";
        }

        // A part's solid for the viewer, before any conversion. Returns why not, or null.
        public static string Preview(string file, string view)
        {
            Segment solid = Segment.ReadAll(file).Find(s => string.Equals(s.Name, "PmBRepSegment", StringComparison.OrdinalIgnoreCase));
            string text = solid == null ? null : new StepWriter().Write(solid.Data, Base(file));
            if (text == null) return "there's no solid in this file";
            view = Path.GetFullPath(view);
            string step = view + ".step";
            File.WriteAllText(step, text, new UTF8Encoding(false));
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(FindFreeCad(), "\"" + Path.Combine(Path.GetDirectoryName(ScriptPath()), "export_view.py") + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.EnvironmentVariables["CADCONVERT_STEP"] = step;
                psi.EnvironmentVariables["CADCONVERT_VIEW"] = view;
                psi.EnvironmentVariables["CADCONVERT_NAME"] = Base(file);
                Private(psi);
                using (Process p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { };
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(180000))
                    {
                        KillTree(p);
                        return "meshing didn't finish within 180 s";
                    }
                }
            }
            finally { File.Delete(step); }
            return File.Exists(view) ? null : "FreeCAD couldn't mesh it";
        }

        // The viewer's pictures for every part already converted under a folder, a few at a time.
        public static void ExportViews(string dir, TextWriter log)
        {
            List<string> targets = new List<string>();
            foreach (string report in Directory.GetFiles(dir, "*.report.json", SearchOption.AllDirectories))
            {
                string reports = Path.GetDirectoryName(report), name = Path.GetFileName(report).Replace(".report.json", "");
                string target = Path.Combine(Path.GetDirectoryName(reports), name + ".FCStd");
                string model = Path.Combine(reports, name + ".model.json");
                if (File.Exists(target) && File.ReadAllText(report, Encoding.UTF8).Contains("\"features\"")) targets.Add(target);
            }
            string[] why = new string[targets.Count];
            System.Threading.Tasks.Parallel.For(0, targets.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, i => why[i] = ExportView(targets[i]));
            int done = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                if (why[i] == null) done++;
                else log.WriteLine("no picture: {0}: {1}", targets[i].Substring(dir.TrimEnd('\\').Length + 1), why[i]);
            }
            log.WriteLine("{0} of {1} parts have pictures for the viewer", done, targets.Count);
        }

        // FreeCAD runs for the converter with settings of its own, so nothing here touches the user's, and it never saves
        // backups of files it's about to replace.
        static void Private(ProcessStartInfo psi)
        {
            string profile = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "CADConvert", "profile");
            Directory.CreateDirectory(profile);
            psi.EnvironmentVariables["FREECAD_USER_HOME"] = profile;
        }

        // The finishing pass (finish_view.py) over the .FCStd files in a folder changed since a moment. FreeCAD's GUI program
        // with no screen at all: its own process and settings, so it runs the same whether or not FreeCAD is open on the
        // desktop, and nothing appears. The files work without it; they just open with everything hidden.
        public static bool Finish(string outDir, DateTime since, TextWriter log)
        {
            List<string> files = new List<string>();
            foreach (string f in Directory.GetFiles(outDir, "*.FCStd", SearchOption.AllDirectories))
                if (File.GetLastWriteTimeUtc(f) >= since) files.Add(Path.GetFullPath(f));
            if (files.Count == 0) return true;
            string tools = Path.GetDirectoryName(ScriptPath());
            string reports = Path.Combine(outDir, "reports");
            Directory.CreateDirectory(reports);
            string list = Path.GetFullPath(Path.Combine(reports, "finish.list.txt")), status = Path.GetFullPath(Path.Combine(reports, "finish.status.json"));
            File.WriteAllLines(list, files.ToArray(), new UTF8Encoding(false));
            if (File.Exists(status)) File.Delete(status);
            // The real program, not the FreeCAD.exe launcher beside FreeCADCmd.exe, so the process here is the one doing the work.
            string root = Path.GetDirectoryName(FindFreeCad());
            string exe = Path.Combine(root, "bin", "freecad.exe");
            if (!File.Exists(exe)) exe = Path.Combine(root, "freecad.exe");
            ProcessStartInfo psi = new ProcessStartInfo(exe, "\"" + Path.Combine(tools, "finish_view.py") + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = false, RedirectStandardError = false };
            Private(psi);
            psi.EnvironmentVariables["CADCONVERT_LIST"] = list;
            psi.EnvironmentVariables["CADCONVERT_REPORT"] = status;
            psi.EnvironmentVariables["QT_QPA_PLATFORM"] = "offscreen";
            // Only a process started here has this, so the pass can never close a window it wasn't started in.
            psi.EnvironmentVariables["CADCONVERT_FINISHING"] = "1";
            bool done = false;
            List<string> left = new List<string>(files), skipped = new List<string>();
            // A file that takes FreeCAD down costs that file, not the ones after it: the pass starts again from the next.
            while (!done && left.Count > 0)
            {
                File.WriteAllLines(list, left.ToArray(), new UTF8Encoding(false));
                if (File.Exists(status)) File.Delete(status);
                using (Process p = Process.Start(psi))
                {
                    DateTime last = DateTime.UtcNow;
                    long size = -1;
                    while (!p.WaitForExit(1000) && (DateTime.UtcNow - last).TotalSeconds < 180)
                    {
                        if (!File.Exists(status)) continue;
                        long now = new FileInfo(status).Length;
                        if (now != size) { size = now; last = DateTime.UtcNow; }
                    }
                    if (!p.HasExited) { KillTree(p); }
                }
                string text = "";
                try { if (File.Exists(status)) text = File.ReadAllText(status, Encoding.UTF8); }
                catch (IOException) { }
                done = text.Contains("\"done\": true");
                if (done) break;
                // Python writes these paths with \\ and non-ASCII as \uXXXX: count the entries instead of matching them.
                int reached = Regex.Matches(text, "\": \"(ok|[A-Za-z]+Error|[A-Za-z]*Exception)").Count;
                skipped.Add(left[Math.Min(reached, left.Count - 1)]);
                left.RemoveRange(0, Math.Min(reached + 1, left.Count));
            }
            if (left.Count == 0) done = true;
            foreach (string f in skipped) log.WriteLine("FreeCAD stopped on " + Path.GetFileName(f) + ": it may open with the model hidden.");
            // Saving makes FreeCAD keep a backup of each file as it was a minute ago, which nobody needs.
            foreach (string f in files)
                foreach (string bak in Directory.GetFiles(Path.GetDirectoryName(f), Path.GetFileNameWithoutExtension(f) + ".*.FCBak"))
                    if (File.GetLastWriteTimeUtc(bak) >= since) File.Delete(bak);
            log.WriteLine(done ? "Views set up in " + files.Count + " files." : "The views' setup in FreeCAD stopped before finishing; the files still work, but may open with the model hidden.");
            return done;
        }

        // Every part's stored solid as STEP, then read back by FreeCAD: is it a valid solid, and does it agree with the B-rep?
        public static void StepAll(string dir, string outDir, TextWriter log)
        {
            Directory.CreateDirectory(outDir);
            List<string> files = new List<string>(Directory.GetFiles(dir, "*.ipt", SearchOption.AllDirectories));
            files.RemoveAll(f => f.IndexOf("OldVersions", StringComparison.OrdinalIgnoreCase) >= 0);
            files.Sort(string.CompareOrdinal);
            List<string> written = new List<string>();
            SortedDictionary<string, int> lost = new SortedDictionary<string, int>();
            Dictionary<string, string> sources = new Dictionary<string, string>();
            int none = 0;
            for (int i = 0; i < files.Count; i++)
            {
                string f = files[i];
                string outFile = Path.GetFullPath(Path.Combine(outDir, i + " " + Path.GetFileNameWithoutExtension(f) + ".step"));
                try
                {
                    Segment brep = Segment.ReadAll(f).Find(s => string.Equals(s.Name, "PmBRepSegment", StringComparison.OrdinalIgnoreCase));
                    StepWriter w = new StepWriter();
                    string text = brep == null ? null : w.Write(brep.Data, Path.GetFileNameWithoutExtension(f));
                    if (text == null) { none++; log.WriteLine("no solid   " + f.Substring(dir.Length)); continue; }
                    File.WriteAllText(outFile, text, new UTF8Encoding(false));
                    written.Add(outFile);
                    sources[outFile] = f.Substring(dir.TrimEnd('\\').Length + 1);
                    foreach (KeyValuePair<string, int> kv in w.NotCarried) { int c; lost.TryGetValue(kv.Key, out c); lost[kv.Key] = c + kv.Value; }
                    if (w.NotCarried.Count > 0) log.WriteLine("gaps       {0}: {1}", sources[outFile], string.Join(", ", new List<string>(w.NotCarried.Keys).ToArray()));
                }
                catch (Exception ex) { log.WriteLine("ERROR      {0}: {1}", f.Substring(dir.Length), ex.Message); }
            }
            string list = Path.Combine(outDir, "list.txt"), report = Path.Combine(outDir, "read.json");
            File.WriteAllLines(list, written.ToArray(), new UTF8Encoding(false));
            ProcessStartInfo psi = new ProcessStartInfo(FindFreeCad(), "\"" + Path.Combine(Path.GetDirectoryName(ScriptPath()), "read_step.py") + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.EnvironmentVariables["CADCONVERT_LIST"] = Path.GetFullPath(list);
            psi.EnvironmentVariables["CADCONVERT_REPORT"] = Path.GetFullPath(report);
            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
            }
            System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> r = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(report, Encoding.UTF8));
            int solid = 0, open = 0, invalid = 0, failed = 0;
            foreach (string w in written)
            {
                object o;
                if (!r.TryGetValue(w, out o)) { failed++; log.WriteLine("unread     " + sources[w]); continue; }
                Dictionary<string, object> m = (Dictionary<string, object>)o;
                if (m.ContainsKey("error")) { failed++; log.WriteLine("unread     {0}: {1}", sources[w], m["error"]); continue; }
                bool valid = (bool)m["valid"], closed = (bool)m["closed"];
                int solids = (int)m["solids"];
                if (valid && closed && solids > 0) solid++;
                else if (valid) { open++; log.WriteLine("sheet/open {0}: {1} solids, {2} faces", sources[w], solids, m["faces"]); }
                else { invalid++; log.WriteLine("invalid    {0}: {1} solids, {2} faces", sources[w], solids, m["faces"]); }
            }
            log.WriteLine("{0} parts: {1} valid solids, {2} valid but open, {3} invalid, {4} unread, {5} with no solid", files.Count, solid, open, invalid, failed, none);
            foreach (KeyValuePair<string, int> kv in lost) log.WriteLine("{0,5} x not carried: {1}", kv.Value, kv.Key);
        }

        // Every part under a folder (old versions left out), one line each, then what kept the others from matching.
        public static void ConvertAll(string dir, string outDir, TextWriter log)
        {
            List<string> files = new List<string>(Directory.GetFiles(dir, "*.ipt", SearchOption.AllDirectories));
            files.RemoveAll(f => f.IndexOf("OldVersions", StringComparison.OrdinalIgnoreCase) >= 0);
            files.Sort(string.CompareOrdinal);
            int matches = 0, differs = 0, failed = 0, withSolid = 0, flawed = 0;
            SortedDictionary<string, int> reasons = new SortedDictionary<string, int>();
            // One process per part, a few at a time: a bad file can't take the rest with it.
            string[] outputs = new string[files.Count];
            int[] codes = new int[files.Count];
            System.Threading.Tasks.Parallel.For(0, files.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                string rel = files[i].Substring(dir.TrimEnd('\\').Length + 1);
                string sub = Path.Combine(outDir, Path.GetDirectoryName(rel));
                ProcessStartInfo psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "convert \"" + files[i] + "\" \"" + sub + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginErrorReadLine();
                    outputs[i] = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    codes[i] = p.ExitCode;
                }
            });
            for (int i = 0; i < files.Count; i++)
            {
                string rel = files[i].Substring(dir.TrimEnd('\\').Length + 1);
                int code = codes[i];
                if (code == 0) matches++; else if (code == 3) withSolid++; else if (code == 4) flawed++; else if (code == 1) differs++; else failed++;
                log.WriteLine("{0,-8} {1}", code == 0 ? "matches" : code == 3 ? "solid" : code == 4 ? "check" : code == 1 ? "differs" : "FAILED", rel);
                foreach (string line in outputs[i].Split('\n'))
                {
                    string l = line.TrimEnd('\r');
                    if (l.Contains(": dropped ") || l.Contains("kept as a value") || (code != 0 && (l.Contains(": skipped:") || l.Contains(": failed:") || l.Contains(": approximate:") || l.Contains("fatal") || l.Contains("curves not carried:") || l.Contains("curves moved") || l.Contains("bounding box")))) log.WriteLine("        " + l);
                    Match m = Regex.Match(l, @"\((?<type>[^)]*)\): (?<st>skipped|failed|approximate): (?<why>.*)$");
                    string key = m.Success ? m.Groups["st"].Value + " " + m.Groups["type"].Value + ": " + Regex.Replace(m.Groups["why"].Value, @"[\d.]+( mm)?", "#") : null;
                    if (key != null) { int c; reasons.TryGetValue(key, out c); reasons[key] = c + 1; }
                }
                log.Flush();
            }
            log.WriteLine("{0} parts: {1} rebuilt with history and match, {2} with Inventor's exact solid added, {3} whose solid needs checking, {4} differ, {5} failed", files.Count, matches, withSolid, flawed, differs, failed);
            foreach (KeyValuePair<string, int> kv in reasons) log.WriteLine("{0,5} x {1}", kv.Value, kv.Key);
        }

        // The report in words. Returns 0 when everything Inventor has was carried and checks out.
        static int Summarize(string json, string target, TextWriter log)
        {
            System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> r = js.Deserialize<Dictionary<string, object>>(json);
            bool clean = true;
            if (r.ContainsKey("fatal")) { log.WriteLine("  fatal: " + r["fatal"]); if (r.ContainsKey("trace")) log.WriteLine(string.Join("\n", Array.ConvertAll((object[])ToArray(r["trace"]), x => x.ToString()))); return 2; }

            int pOk = 0, pValue = 0;
            foreach (object o in ToArray(r["parameters"]))
            {
                Dictionary<string, object> p = (Dictionary<string, object>)o;
                if ((string)p["status"] == "ok") pOk++;
                else { pValue++; log.WriteLine("  parameter {0} kept as a value: {1} ({2})", p["name"], p["formula"], p.ContainsKey("why") ? p["why"] : ""); }
            }
            log.WriteLine("  parameters: {0} with formulas, {1} as values", pOk, pValue);

            int constraints = 0, dims = 0, dropped = 0, moved = 0, skipped = 0;
            object[] sketches = ToArray(r["sketches"]);
            foreach (object o in sketches)
            {
                Dictionary<string, object> s = (Dictionary<string, object>)o;
                constraints += (int)s["constraints"];
                dims += (int)s["dimensions"];
                object[] d = ToArray(s["dropped"]), c = ToArray(s["skipped_curves"]);
                dropped += d.Length;
                skipped += c.Length;
                if (s.ContainsKey("moved")) moved += (int)s["moved"];
                if (d.Length > 0) log.WriteLine("  {0}: dropped {1}", s["name"], string.Join(", ", Array.ConvertAll(d, x => x.ToString())));
                if (c.Length > 0) log.WriteLine("  {0}: curves not carried: {1}", s["name"], string.Join(", ", Array.ConvertAll(c, x => x.ToString())));
                if (s.ContainsKey("moved") && (int)s["moved"] > 0) log.WriteLine("  {0}: {1} curves moved from where Inventor has them", s["name"], s["moved"]);
            }
            log.WriteLine("  sketches: {0}; {1} constraints, {2} dimensions, {3} dropped, {4} curves not carried, {5} moved", sketches.Length, constraints, dims, dropped, skipped, moved);

            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (object o in ToArray(r["features"]))
            {
                Dictionary<string, object> f = (Dictionary<string, object>)o;
                string st = (string)f["status"];
                int c;
                counts.TryGetValue(st, out c);
                counts[st] = c + 1;
                if (st != "ok") log.WriteLine("  {0} ({1}): {2}: {3}", f["name"], f["type"], st, f.ContainsKey("why") ? f["why"] : "");
                if (st == "skipped" || st == "failed") clean = false;
            }
            List<string> words = new List<string>();
            foreach (KeyValuePair<string, int> kv in counts) words.Add(kv.Value + " " + kv.Key);
            log.WriteLine("  features: " + string.Join(", ", words.ToArray()));

            if (r.ContainsKey("result") && r["result"] != null)
            {
                Dictionary<string, object> res = (Dictionary<string, object>)r["result"];
                log.WriteLine("  built: volume {0:0.###} mm3, {1} faces, {2} edges, {3} vertices", ToD(res["volume"]), res["faces"], res["edges"], res["vertices"]);
                if (r.ContainsKey("compare"))
                {
                    Dictionary<string, object> c = (Dictionary<string, object>)r["compare"];
                    Dictionary<string, object> inv = (Dictionary<string, object>)c["inventor"];
                    log.WriteLine("  Inventor: {0} faces, {1} edges, {2} vertices", inv["faces"], inv["edges"], inv["vertices"]);
                    double off = c.ContainsKey("bbox_off") ? ToD(c["bbox_off"]) : -1;
                    log.WriteLine("  bounding box differs by {0:0.######} mm; surfaces: {1} of {2} found, {3} built faces on no Inventor surface", off, (int)c["surfaces"] - (int)c["surfaces_missing_count"], c["surfaces"], c["faces_unmatched"]);
                    // Each solid's surface lying on the other settles it, as does subtracting where that could be done: the
                    // same shape can be on other surfaces (a blend FreeCAD makes as a torus and Inventor as a spline).
                    // Otherwise the surfaces must agree.
                    Dictionary<string, object> step = r.ContainsKey("step") ? r["step"] as Dictionary<string, object> : null;
                    bool settled = step != null && r.ContainsKey("rebuild_matches_solid") && (bool)r["rebuild_matches_solid"];
                    if (off > 0.01 || (!settled && ((int)c["surfaces_missing_count"] > 0 || (int)c["faces_unmatched"] > 0))) clean = false;
                }
            }
            else
            {
                // A part of sketches only has no solid to build.
                log.WriteLine("  nothing solid was built");
                if (ToArray(r["features"]).Length > 0) clean = false;
            }
            foreach (object o in ToArray(r["notes"])) log.WriteLine("  note: " + o);
            // The volume of Inventor's own solid, read back from the file's B-rep: the rebuild has to agree.
            if (r.ContainsKey("step") && r["step"] != null)
            {
                Dictionary<string, object> st = (Dictionary<string, object>)r["step"];
                if (st.ContainsKey("error")) log.WriteLine("  Inventor's solid wouldn't read back: " + st["error"]);
                else
                {
                    log.WriteLine("  Inventor's solid: volume {0:0.###} mm3, {1}{2}{3}", ToD(st["volume"]), (bool)st["valid"] ? "valid" : "NOT VALID", st.ContainsKey("volume_off") ? string.Format(", rebuild's volume differs by {0:0.####}%", 100 * ToD(st["volume_off"])) : "",
                        st.ContainsKey("left_over") ? string.Format(", {0:0.####}% left after subtracting", 100 * ToD(st["left_over"])) : "");
                    if (st.ContainsKey("surface_far")) log.WriteLine("  surfaces: {0:0.#}% of sampled points more than 0.05 mm from the other solid, furthest {1:0.####} mm", 100 * ToD(st["surface_far"]), ToD(st["surface_worst"]));
                    if (st.ContainsKey("surface_far") && !(bool)r["rebuild_matches_solid"]) clean = false;
                    if (st.ContainsKey("volume_off") && ToD(st["volume_off"]) > 1e-3) clean = false;
                    if (st.ContainsKey("left_over") && ToD(st["left_over"]) >= 1e-3) clean = false;
                }
            }
            bool frozen = r.ContainsKey("frozen_added"), flawed = r.ContainsKey("frozen_flawed") && (bool)r["frozen_flawed"];
            // Inventor's solid goes in only where the rebuild didn't give it, so that's never a match, even with nothing to rebuild.
            if (frozen) clean = false;
            log.WriteLine("  " + (clean ? "MATCHES" : flawed ? "SOLID NEEDS CHECKING" : frozen ? "EXACT SOLID ADDED" : "DIFFERS") + "  " + target);
            return clean ? 0 : flawed ? 4 : frozen ? 3 : 1;
        }

        static double ToD(object o) { return System.Convert.ToDouble(o); }

        static object[] ToArray(object o)
        {
            if (o is object[]) return (object[])o;
            System.Collections.ArrayList a = o as System.Collections.ArrayList;
            return a == null ? new object[0] : a.ToArray();
        }
    }
}
