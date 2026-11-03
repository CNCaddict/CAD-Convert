// The viewer: the history beside the 3D view, showing what went in (Inventor's solid, read from the file) and what came out
// (FreeCAD's file), together, one at a time, or as the places they differ. Only ever reads what the converter wrote.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CadConvert
{
    public enum ViewMode { Both, Inventor, FreeCad, Differences }

    public class Result
    {
        public string Name, Path, Folder, Words, Dot;
        public bool Assembly;

        // A converted part's result in words; shared by the list of results and the viewer.
        public static void Describe(ViewFile f, out string words, out string dot)
        {
            bool history = false;
            foreach (object o in ViewFile.Items(f.Head["nodes"]))
                if (((Dictionary<string, object>)o)["object"] != null) history = true;
            if (f.Flag("preview")) { words = "Not converted yet"; dot = "grey"; }
            else if (f.Flag("flawed")) { words = "Solid needs checking"; dot = "red"; }
            else if (f.Flag("exact_added")) { words = history ? "Exact solid + partial history" : "Exact solid only"; dot = "amber"; }
            else if (history) { words = "Rebuilt, matches"; dot = "green"; }
            else { words = "No solid"; dot = "grey"; }
        }

        // Everything converted under a folder.
        public static List<Result> Scan(string root)
        {
            List<Result> found = new List<Result>();
            if (!Directory.Exists(root)) return found;
            foreach (string reports in Directory.GetDirectories(root, "reports", SearchOption.AllDirectories))
            {
                string above = System.IO.Path.GetDirectoryName(reports);
                string folder = above.Length > root.TrimEnd('\\').Length ? above.Substring(root.TrimEnd('\\').Length + 1) : "";
                foreach (string v in Directory.GetFiles(reports, "*.view"))
                {
                    try
                    {
                        ViewFile f = ViewFile.Open(v);
                        Result r = new Result { Name = System.IO.Path.GetFileNameWithoutExtension(v), Path = v, Folder = folder };
                        Describe(f, out r.Words, out r.Dot);
                        found.Add(r);
                    }
                    catch (Exception) { }
                }
                foreach (string m in Directory.GetFiles(reports, "*.model.json"))
                    if (IsAssembly(m))
                        found.Add(new Result { Name = System.IO.Path.GetFileName(m).Replace(".model.json", ""), Path = m, Folder = folder, Words = "Assembly", Dot = "blue", Assembly = true });
            }
            found.Sort((a, b) =>
            {
                if (a.Assembly != b.Assembly) return a.Assembly ? -1 : 1;
                int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
            });
            return found;
        }

        public static bool IsAssembly(string modelJson)
        {
            try
            {
                using (StreamReader s = new StreamReader(modelJson, Encoding.UTF8))
                {
                    char[] start = new char[1024];
                    int n = s.Read(start, 0, start.Length);
                    return Regex.IsMatch(new string(start, 0, n), "^\\{\"name\": \"(?:[^\"\\\\]|\\\\.)*\", \"occurrences\":");
                }
            }
            catch (Exception) { return false; }
        }
    }

    public class ViewerPane : UserControl
    {
        static readonly float[] InventorColor = { 0.58f, 0.67f, 0.78f }, FreeCadColor = { 0.76f, 0.74f, 0.64f }, Same = { 0.74f, 0.75f, 0.76f }, OnlyRebuild = { 0.90f, 0.16f, 0.12f }, OnlyInventor = { 0.08f, 0.32f, 0.95f }, Change = { 0.98f, 0.55f, 0.05f }, Picked = { 0.98f, 0.55f, 0.05f };
        public readonly GlView View = new GlView { Dock = DockStyle.Fill };
        readonly TreeView tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true, ImageList = new ImageList { ImageSize = new Size(12, 12), ColorDepth = ColorDepth.Depth32Bit } };
        readonly PictureBox picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(236, 238, 241), Visible = false };
        readonly TextBox info = new TextBox { Dock = DockStyle.Bottom, Height = 62, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, ScrollBars = ScrollBars.Vertical };
        readonly Dictionary<ViewMode, RadioButton> modes = new Dictionary<ViewMode, RadioButton>();
        readonly CheckBox edges = new CheckBox { Text = "Edges", AutoSize = true, Checked = true, Margin = new Padding(12, 6, 3, 3) };
        readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
        readonly Dictionary<string, Comparison> comparisons = new Dictionary<string, Comparison>();
        ViewMode mode = ViewMode.FreeCad;
        ViewFile part;
        List<Occ> flat;
        string shownPath;
        object picked;
        int generation;
        volatile bool busy;

        class Feature { public string Name, Type, Status, Why, Object; public int Body = -1, Block = -1, Index; public double? Volume; public List<Sketch> Sketches = new List<Sketch>(); }
        class Sketch { public string Name; public int Block; public Feature Of; }
        class Occ { public string Name, Words, Dot; public ViewFile File; public float[] Matrix; public bool Drawn; public List<Occ> Children = new List<Occ>(); public string Note; }
        List<Feature> features = new List<Feature>();

        public bool Busy { get { return busy; } }

        public ViewerPane()
        {
            foreach (KeyValuePair<string, Color> dot in new Dictionary<string, Color> { { "green", Color.FromArgb(46, 160, 67) }, { "amber", Color.FromArgb(226, 150, 20) }, { "red", Color.FromArgb(214, 48, 49) }, { "grey", Color.FromArgb(170, 174, 180) }, { "blue", Color.FromArgb(50, 110, 220) }, { "pink", Color.FromArgb(190, 20, 140) } })
                tree.ImageList.Images.Add(dot.Key, Dot(dot.Value));

            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 3, 4, 2) };
            foreach (KeyValuePair<ViewMode, string> m in new Dictionary<ViewMode, string> { { ViewMode.FreeCad, "Converted" }, { ViewMode.Inventor, "Inventor" }, { ViewMode.Both, "Side by side" }, { ViewMode.Differences, "Differences" } })
            {
                ViewMode which = m.Key;
                RadioButton b = new RadioButton { Text = m.Value, Appearance = Appearance.Button, AutoSize = true, Checked = which == mode, Margin = new Padding(1, 2, 1, 2) };
                b.CheckedChanged += (s, e) => { if (b.Checked && mode != which) { mode = which; Compose(true); } };
                modes[which] = b;
                bar.Controls.Add(b);
            }
            bar.Controls.Add(edges);
            edges.CheckedChanged += (s, e) => { View.Edges = edges.Checked; View.Invalidate(); };
            AddTurn(bar, "Fit", null);
            // Inventor's own default: Y up, looking down Z.
            AddTurn(bar, "Iso", new[] { 35.264, -45.0 });
            AddTurn(bar, "Front", new[] { 0.0, 0.0 });
            AddTurn(bar, "Top", new[] { 90.0, 0.0 });
            AddTurn(bar, "Right", new[] { 0.0, -90.0 });
            bar.Controls.Add(new Label { Text = "Drag to turn, right-drag to move, wheel to zoom", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 7, 3, 3) });

            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(tree);
            split.Panel2.Controls.Add(View);
            Controls.Add(split);
            Controls.Add(info);
            Controls.Add(bar);
            split.BringToFront();
            split.Panel2.Controls.Add(picture);
            picture.BringToFront();
            HandleCreated += (s, e) => { split.SplitterDistance = 230; split.Panel1MinSize = 120; };
            tree.AfterSelect += (s, e) => { picked = e.Node.Tag; Compose(false); };
            info.Text = "Pick a result to see it here.";
        }

        void AddTurn(FlowLayoutPanel bar, string text, double[] degrees)
        {
            Button b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(1, 2, 1, 2) };
            b.Click += (s, e) =>
            {
                if (degrees != null) View.Turn(degrees[0] * Math.PI / 180, degrees[1] * Math.PI / 180);
                View.Fit();
            };
            bar.Controls.Add(b);
        }

        static Bitmap Dot(Color c)
        {
            Bitmap b = new Bitmap(12, 12);
            using (Graphics g = Graphics.FromImage(b))
            using (SolidBrush fill = new SolidBrush(c))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(fill, 1.5f, 1.5f, 9f, 9f);
            }
            return b;
        }

        public void Clear(string why)
        {
            generation++;
            busy = false;
            Forget();
            part = null;
            flat = null;
            shownPath = null;
            tree.Nodes.Clear();
            View.LeftItems = new List<Item>();
            View.RightItems = new List<Item>();
            View.LeftCaption = View.RightCaption = "";
            View.Invalidate();
            info.Text = why;
        }

        void Forget()
        {
            lock (meshes)
            {
                foreach (Mesh m in meshes.Values) View.Drop(m);
                meshes.Clear();
                foreach (Comparison c in comparisons.Values) { View.Drop(c.First); View.Drop(c.SecondFar); }
                comparisons.Clear();
            }
        }

        Mesh Get(ViewFile f, int block)
        {
            string key = f.Path + "|" + block;
            lock (meshes)
            {
                Mesh m;
                if (meshes.TryGetValue(key, out m)) return m;
            }
            Mesh read = f.Read(block);
            lock (meshes)
            {
                Mesh m;
                if (meshes.TryGetValue(key, out m)) return m;
                meshes[key] = read;
            }
            return read;
        }

        // A file's own stored picture, straight away, until there's something to turn round.
        public void ShowPicture(Image image, string why)
        {
            Clear(why);
            Image old = picture.Image;
            picture.Image = image;
            if (old != null) old.Dispose();
            picture.Visible = image != null;
        }

        public void Show(Result r)
        {
            picture.Visible = false;
            if (r == null) { Clear("Pick a result to see it here."); return; }
            if (r.Path == shownPath) return;
            int gen = ++generation;
            busy = true;
            info.Text = "Loading " + r.Name + "...";
            string path = r.Path;
            bool assembly = r.Assembly;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                ViewFile f = null;
                List<Feature> list = null;
                Occ top = null;
                string error = null;
                try
                {
                    if (assembly)
                    {
                        top = new Occ { Name = r.Name, Children = new List<Occ>() };
                        Dictionary<string, ViewFile> files = new Dictionary<string, ViewFile>(StringComparer.OrdinalIgnoreCase);
                        ReadAssembly(path, null, top, files, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    }
                    else { f = ViewFile.Open(path); list = ReadFeatures(f); }
                }
                catch (Exception ex) { error = ex.Message; }
                Ui(() =>
                {
                    if (gen != generation) return;
                    if (error != null) { busy = false; info.Text = "Couldn't read this result: " + error; return; }
                    Forget();
                    shownPath = path;
                    part = f;
                    features = list ?? new List<Feature>();
                    flat = null;
                    if (top != null) { flat = new List<Occ>(); Flatten(top, flat); }
                    picked = null;
                    Fill(top);
                    Compose(true);
                });
            });
        }

        void Ui(Action a) { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }

        static List<Feature> ReadFeatures(ViewFile f)
        {
            List<Feature> list = new List<Feature>();
            foreach (object o in ViewFile.Items(f.Head["nodes"]))
            {
                Dictionary<string, object> n = (Dictionary<string, object>)o;
                Feature x = new Feature { Name = n["name"] as string, Type = n["type"] as string, Status = n["status"] as string, Why = n["why"] as string, Object = n["object"] as string, Index = list.Count };
                if (n["body"] != null) x.Body = Convert.ToInt32(n["body"]);
                if (n["block"] != null) x.Block = Convert.ToInt32(n["block"]);
                if (n["volume"] != null) x.Volume = Convert.ToDouble(n["volume"]);
                foreach (object so in ViewFile.Items(n["sketches"]))
                {
                    Dictionary<string, object> s = (Dictionary<string, object>)so;
                    x.Sketches.Add(new Sketch { Name = s["name"] as string, Block = s["block"] == null ? -1 : Convert.ToInt32(s["block"]), Of = x });
                }
                list.Add(x);
            }
            return list;
        }

        // An assembly's occurrences, sub-assemblies inside them, each with its place in the whole.
        static void ReadAssembly(string modelJson, float[] outer, Occ into, Dictionary<string, ViewFile> files, HashSet<string> open)
        {
            if (!open.Add(modelJson)) return;
            JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> model = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(modelJson, Encoding.UTF8));
            string reports = System.IO.Path.GetDirectoryName(modelJson);
            foreach (object o in ViewFile.Items(model["occurrences"]))
            {
                Dictionary<string, object> m = (Dictionary<string, object>)o;
                Occ c = new Occ { Name = m["name"] as string };
                into.Children.Add(c);
                c.Matrix = Times(outer, ToGl(ViewFile.Items(m["matrix"])));
                bool suppressed = m["suppressed"] is bool && (bool)m["suppressed"], visible = !(m["visible"] is bool) || (bool)m["visible"];
                string target = m["target"] as string;
                string baseName = target == null ? null : System.IO.Path.GetFileNameWithoutExtension(target);
                if (baseName == null) { c.Words = "Not found beside the assembly"; c.Dot = "red"; continue; }
                string view = System.IO.Path.Combine(reports, baseName + ".view"), sub = System.IO.Path.Combine(reports, baseName + ".model.json");
                if (!File.Exists(view) && !File.Exists(sub))
                {
                    string other = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target), "reports");
                    view = System.IO.Path.Combine(other, baseName + ".view");
                    sub = System.IO.Path.Combine(other, baseName + ".model.json");
                }
                if (File.Exists(view))
                {
                    ViewFile f;
                    if (!files.TryGetValue(view, out f)) { f = ViewFile.Open(view); files[view] = f; }
                    c.File = f;
                    Result.Describe(f, out c.Words, out c.Dot);
                    c.Drawn = !suppressed && visible;
                }
                else if (File.Exists(sub) && Result.IsAssembly(sub))
                {
                    c.Words = "Sub-assembly";
                    c.Dot = "blue";
                    if (!suppressed && visible) ReadAssembly(sub, c.Matrix, c, files, open);
                }
                else { c.Words = "No preview"; c.Dot = "grey"; }
                if (suppressed) { c.Note = "suppressed"; c.Dot = "grey"; }
                else if (!visible) { c.Note = "hidden in Inventor"; c.Dot = "grey"; }
            }
            open.Remove(modelJson);
        }

        static float[] ToGl(object[] m)
        {
            if (m.Length < 12) return new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            Func<int, float> v = i => (float)Convert.ToDouble(m[i]);
            // Stored by rows, positions in cm.
            return new[] { v(0), v(4), v(8), 0f, v(1), v(5), v(9), 0f, v(2), v(6), v(10), 0f, v(3) * 10, v(7) * 10, v(11) * 10, 1f };
        }

        static float[] Times(float[] a, float[] b) { return a == null ? b : GlView.Times(a, b); }

        static void Flatten(Occ o, List<Occ> into)
        {
            foreach (Occ c in o.Children)
            {
                if (c.File != null && c.Drawn) into.Add(c);
                Flatten(c, into);
            }
        }

        void Fill(Occ top)
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();
            TreeNode root;
            if (top != null)
            {
                root = new TreeNode(top.Name) { Tag = "root", ImageKey = "blue", SelectedImageKey = "blue" };
                tree.Nodes.Add(root);
                AddOccurrences(root, top);
                root.Expand();
            }
            else
            {
                string words, dot;
                Result.Describe(part, out words, out dot);
                root = new TreeNode(part.Head["name"] as string ?? "Part") { Tag = "root", ImageKey = dot, SelectedImageKey = dot, ToolTipText = words };
                tree.Nodes.Add(root);
                object[] parameters = ViewFile.Items(part.Head["parameters"]);
                if (parameters.Length > 0)
                {
                    TreeNode p = new TreeNode("Parameters (" + parameters.Length + ")") { Tag = "root", ImageKey = "grey", SelectedImageKey = "grey" };
                    root.Nodes.Add(p);
                    foreach (object o in parameters)
                    {
                        Dictionary<string, object> m = (Dictionary<string, object>)o;
                        p.Nodes.Add(new TreeNode(m["name"] + " = " + (m["formula"] ?? m["shown"])) { Tag = "root", ImageKey = "grey", SelectedImageKey = "grey" });
                    }
                }
                foreach (Feature f in features)
                {
                    string dotKey = f.Status == "ok" && f.Block >= 0 ? "green" : f.Status == "approximate" ? "amber" : f.Status == "failed" || f.Status == "skipped" ? "red" : f.Status == "ok" ? "amber" : "grey";
                    TreeNode n = new TreeNode((f.Name ?? f.Type) + (f.Status == "ok" ? "" : "  (" + f.Status + ")")) { Tag = f, ImageKey = dotKey, SelectedImageKey = dotKey, ToolTipText = f.Why ?? "" };
                    foreach (Sketch s in f.Sketches)
                        n.Nodes.Add(new TreeNode(s.Name) { Tag = s, ImageKey = "pink", SelectedImageKey = "pink" });
                    root.Nodes.Add(n);
                }
                root.Expand();
            }
            tree.SelectedNode = null;
            tree.EndUpdate();
        }

        void AddOccurrences(TreeNode into, Occ o)
        {
            foreach (Occ c in o.Children)
            {
                TreeNode n = new TreeNode(c.Name + (c.Note != null ? "  (" + c.Note + ")" : "")) { Tag = c, ImageKey = c.Dot ?? "grey", SelectedImageKey = c.Dot ?? "grey", ToolTipText = c.Words ?? "" };
                into.Nodes.Add(n);
                AddOccurrences(n, c);
            }
        }

        public void SetMode(ViewMode m) { modes[m].Checked = true; }

        public int FeatureCount { get { return features.Count; } }

        public void PickFeature(int index)
        {
            foreach (TreeNode n in tree.Nodes[0].Nodes)
            {
                Feature f = n.Tag as Feature;
                if (f != null && f.Index == index) { tree.SelectedNode = n; return; }
            }
        }

        public string InfoText { get { return info.Text; } }
        public void Note(string text) { info.Text = text; }
        public bool Shown3D { get { return shownPath != null && !picture.Visible; } }

        // Body by body, the state the part is in once a feature has run.
        List<Mesh> Stage(int upTo)
        {
            Dictionary<int, int> last = new Dictionary<int, int>();
            foreach (Feature f in features)
            {
                if (f.Index > upTo) break;
                if (f.Body >= 0 && f.Block >= 0) last[f.Body] = f.Block;
            }
            List<Mesh> list = new List<Mesh>();
            foreach (int b in last.Values) list.Add(Get(part, b));
            return list;
        }

        static string Percent(double v) { return v == 0 ? "0%" : v < 1e-6 ? "under 0.0001%" : (100 * v).ToString(v < 0.001 ? "0.####" : "0.##", CultureInfo.InvariantCulture) + "%"; }

        // For testing: the Differences measurement on every part with a rebuild, beside the converter's own check.
        public static void CheckAll(string root, string csv)
        {
            using (StreamWriter o = new StreamWriter(csv, false, new UTF8Encoding(false)))
            {
                o.WriteLine("name,result,rebuild_far,inventor_far,worst_mm,checker_far,checker_worst_mm,seconds");
                foreach (Result r in Result.Scan(root))
                {
                    if (r.Assembly) continue;
                    ViewFile f = ViewFile.Open(r.Path);
                    Dictionary<int, int> last = new Dictionary<int, int>();
                    foreach (object n in ViewFile.Items(f.Head["nodes"]))
                    {
                        Dictionary<string, object> m = (Dictionary<string, object>)n;
                        if (m["body"] != null && m["block"] != null) last[Convert.ToInt32(m["body"])] = Convert.ToInt32(m["block"]);
                    }
                    if (last.Count == 0 || f.Head["inventor"] == null) continue;
                    List<Mesh> finals = new List<Mesh>();
                    foreach (int b in last.Values) finals.Add(f.Read(b));
                    DateTime t = DateTime.UtcNow;
                    Comparison c = Comparison.Of(Mesh.Combine(finals), f.Read(Convert.ToInt32(f.Head["inventor"])), Same, OnlyRebuild, OnlyInventor);
                    o.WriteLine(string.Format(CultureInfo.InvariantCulture, "\"{0}\",\"{1}\",{2:0.######},{3:0.######},{4:0.###},{5},{6},{7:0.#}", r.Name, r.Words, c.FirstShare, c.SecondShare, c.Worst, f.Number("surface_far"), f.Number("surface_worst"), (DateTime.UtcNow - t).TotalSeconds));
                    o.Flush();
                }
            }
        }
        static string Mm(double v) { return v.ToString(v < 1 ? "0.###" : "0.#", CultureInfo.InvariantCulture) + " mm"; }

        string PartSummary()
        {
            string words, dot;
            Result.Describe(part, out words, out dot);
            StringBuilder s = new StringBuilder();
            bool exact = part.Flag("exact_added") || part.Flag("flawed");
            if (dot == "green") s.Append("Rebuilt from its history, and the rebuild matches Inventor's solid. The FreeCAD file opens showing the rebuild.");
            else if (words == "Exact solid + partial history") s.Append(ToSolidCraft ? "The rebuild falls short, so the SolidCraft file holds Inventor's exact solid. The rebuild is kept in the reports folder." : "The rebuild falls short, so the FreeCAD file opens showing Inventor's exact solid, with the history hidden beside it.");
            else if (words == "Exact solid only") s.Append("Nothing here could be rebuilt (usually a vendor part with no history). The FreeCAD file holds Inventor's exact solid.");
            else if (words == "Solid needs checking") s.Append(ToSolidCraft ? "Neither the rebuild nor Inventor's solid comes through SolidCraft cleanly, so check the file: the picture here is Inventor's solid, not what the file holds." : "The solid stored in the Inventor file reads back with flaws. The FreeCAD file holds it as it is; check it.");
            else s.Append("There's no solid in this part.");
            double? off = part.Number("volume_off"), far = part.Number("surface_far"), worst = part.Number("surface_worst");
            if (off != null) s.Append(" Rebuild's volume is off by " + Percent(off.Value) + ".");
            if (far != null) s.Append(" Converter's check: " + Percent(far.Value) + " of sampled points further than 0.05 mm" + (worst != null ? ", furthest " + Mm(worst.Value) : "") + ".");
            double? left = part.Number("stages_left_out");
            if (left != null && left.Value > 0) s.Append(" " + left.Value + " steps of the history have no picture: they took too long to mesh.");
            return s.ToString();
        }

        class Scene { public List<Item> Left = new List<Item>(), Right = new List<Item>(); public string LeftCaption = "", RightCaption = "", Info = ""; public bool Two; }

        void Compose(bool fit)
        {
            if (part == null && flat == null) return;
            int gen = ++generation;
            busy = true;
            ViewMode m = mode;
            object sel = picked;
            if (m == ViewMode.Differences && part != null) info.Text = "Measuring the differences...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Scene s = null;
                string error = null;
                try { s = flat != null ? AssemblyScene(m, sel) : PartScene(m, sel); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                Ui(() =>
                {
                    if (gen != generation) return;
                    busy = false;
                    if (error != null) { info.Text = "Couldn't show this: " + error; return; }
                    View.Two = s.Two;
                    View.LeftItems = s.Left;
                    View.RightItems = s.Right;
                    View.LeftCaption = s.LeftCaption;
                    View.RightCaption = s.RightCaption;
                    info.Text = s.Info;
                    if (fit) View.Fit(); else View.Invalidate();
                });
            });
        }

        // What's in the Inventor file: the solid it stores. It stores no solid for the steps along the way, only the last.
        List<Item> InventorItems()
        {
            List<Item> items = new List<Item>();
            int b = part.Head["inventor"] == null ? -1 : Convert.ToInt32(part.Head["inventor"]);
            if (b >= 0) items.Add(new Item { M = Get(part, b), Color = InventorColor });
            return items;
        }

        bool ToSolidCraft { get { return shownPath != null && shownPath.IndexOf("\\SolidCraft\\reports\\", StringComparison.OrdinalIgnoreCase) >= 0; } }

        // The words are FreeCAD's by default; a result in the SolidCraft folder says SolidCraft, and its rebuild isn't inside the file.
        string Named(string text)
        {
            if (text == null || !ToSolidCraft) return text;
            return text.Replace("(hidden in the file: it opens showing Inventor's exact solid)", "(kept in the reports folder: the file holds Inventor's exact solid)")
                .Replace("the hidden rebuild; the file opens showing", "the rebuild in the reports folder; the file holds")
                .Replace("file opens showing the rebuild", "file holds the rebuild")
                .Replace("FreeCAD", "SolidCraft").Replace(", as it opens", "").Replace(": as it opens", "");
        }

        Scene PartScene(ViewMode m, object sel)
        {
            Scene s = OneScene(m, sel);
            s.LeftCaption = Named(s.LeftCaption);
            s.RightCaption = Named(s.RightCaption);
            s.Info = Named(s.Info);
            return s;
        }

        Scene OneScene(ViewMode m, object sel)
        {
            Scene s = new Scene { Two = m == ViewMode.Both };
            if (part.Flag("preview"))
            {
                s.Two = false;
                s.Left = InventorItems();
                s.LeftCaption = "Inventor file: the solid stored in it";
                s.Info = "Read straight from the Inventor file. Press Convert to make the FreeCAD file and compare the two.";
                return s;
            }
            Feature f = sel as Feature;
            Sketch sk = sel as Sketch;
            List<Item> inventor = InventorItems();
            string inventorCaption = inventor.Count > 0 ? "Inventor file: the solid stored in it" : "Inventor file: no solid";
            bool exact = (part.Flag("exact_added") || part.Flag("flawed")) && inventor.Count > 0;

            if (m == ViewMode.Differences)
            {
                List<Mesh> finals = Stage(int.MaxValue);
                if (finals.Count == 0 || inventor.Count == 0)
                {
                    s.Left = inventor.Count > 0 ? inventor : s.Left;
                    s.LeftCaption = "Nothing to compare";
                    s.Info = "There's no rebuild to compare with Inventor's solid here. " + PartSummary();
                    return s;
                }
                Comparison c;
                lock (meshes) comparisons.TryGetValue("final", out c);
                if (c == null)
                {
                    c = Comparison.Of(Mesh.Combine(finals), inventor[0].M, Same, OnlyRebuild, OnlyInventor);
                    lock (meshes) comparisons["final"] = c;
                }
                s.Left.Add(new Item { M = c.First });
                s.Left.Add(new Item { M = c.SecondFar });
                s.LeftCaption = "Differences: FreeCAD's rebuild against Inventor's solid\nGrey: within 0.05 mm     Red: only in the rebuild     Blue: only in Inventor's solid";
                s.Info = (c.FirstShare < 1e-5 && c.SecondShare < 1e-5 ? "No surface further apart than 0.05 mm, measured on the meshes, allowing for how closely each mesh follows its surface." :
                    string.Format("Measured on the meshes, allowing for how closely each follows its surface: {0} of the rebuild's surface and {1} of Inventor's are further apart than 0.05 mm; furthest {2}{3}.", Percent(c.FirstShare), Percent(c.SecondShare), c.Worst >= c.Cap * 0.9 ? "over " : "", Mm(Math.Min(c.Worst, c.Cap))))
                    + (exact ? " This compares the hidden rebuild; the file opens showing Inventor's exact solid." : "") + " " + PartSummary();
                return s;
            }

            List<Item> output = new List<Item>();
            string outputCaption, detail = null;
            if (f != null || sk != null)
            {
                Feature at = f ?? sk.Of;
                int upTo = f != null ? at.Index : at.Index - 1;
                List<Mesh> state = Stage(upTo);
                Mesh changed = null;
                if (f != null && at.Block >= 0)
                {
                    List<Mesh> before = Stage(at.Index - 1);
                    if (before.Count > 0 && m != ViewMode.Inventor)
                    {
                        Comparison c;
                        string key = "step" + at.Index;
                        lock (meshes) comparisons.TryGetValue(key, out c);
                        if (c == null)
                        {
                            c = new Comparison { First = Comparison.Changed(Mesh.Combine(state), Mesh.Combine(before), FreeCadColor, Change), SecondFar = new Mesh() };
                            lock (meshes) comparisons[key] = c;
                        }
                        changed = c.First;
                    }
                }
                if (changed != null) output.Add(new Item { M = changed });
                else foreach (Mesh x in state) output.Add(new Item { M = x, Color = FreeCadColor });
                if (sk != null)
                {
                    if (sk.Block >= 0) output.Add(new Item { M = Get(part, sk.Block), Overlay = true });
                    outputCaption = "FreeCAD file: sketch " + sk.Name + ", before " + (at.Name ?? at.Type);
                    detail = sk.Name + " is the sketch " + (at.Name ?? at.Type) + " is built from (pink), on the part as it stands just before that step.";
                }
                else
                {
                    outputCaption = "FreeCAD file, after " + (at.Name ?? at.Type) + (changed != null ? " (orange: what it changed)" : "");
                    if (at.Block >= 0) detail = (at.Name ?? at.Type) + " (" + at.Type + "): " + (at.Status == "ok" ? "in FreeCAD's history" : at.Status + (at.Why != null ? ": " + at.Why : "")) + (at.Volume != null ? ". Volume after it: " + at.Volume.Value.ToString("0.###", CultureInfo.InvariantCulture) + " mm3" : "") + ".";
                    else detail = (at.Name ?? at.Type) + " (" + at.Type + ") isn't in FreeCAD's history: " + at.Status + (at.Why != null ? ", " + at.Why : "") + ". The picture is the part just before it.";
                }
                if (exact) outputCaption += "\n(hidden in the file: it opens showing Inventor's exact solid)";
            }
            else if (exact)
            {
                foreach (Item i in inventor) output.Add(new Item { M = i.M, Color = FreeCadColor });
                outputCaption = "FreeCAD file: Inventor's exact solid, as it opens";
            }
            else
            {
                foreach (Mesh x in Stage(int.MaxValue)) output.Add(new Item { M = x, Color = FreeCadColor });
                outputCaption = output.Count > 0 ? "FreeCAD file: rebuilt from history, as it opens" : "FreeCAD file: nothing solid";
            }
            s.Info = detail ?? PartSummary();
            if (m == ViewMode.Both) { s.Left = inventor; s.LeftCaption = inventorCaption; s.Right = output; s.RightCaption = outputCaption; }
            else if (m == ViewMode.Inventor) { s.Left = inventor; s.LeftCaption = inventorCaption; }
            else { s.Left = output; s.LeftCaption = outputCaption; }
            return s;
        }

        // One part's meshes in an assembly: what its file holds of Inventor's, and what its FreeCAD file opens showing.
        List<Mesh> OccMeshes(ViewFile f, bool input)
        {
            List<Mesh> list = new List<Mesh>();
            int inv = f.Head["inventor"] == null ? -1 : Convert.ToInt32(f.Head["inventor"]);
            if (input || f.Flag("exact_added") || f.Flag("flawed"))
            {
                if (inv >= 0) list.Add(Get(f, inv));
                if (input || inv >= 0) return list;
            }
            Dictionary<int, int> last = new Dictionary<int, int>();
            foreach (object o in ViewFile.Items(f.Head["nodes"]))
            {
                Dictionary<string, object> n = (Dictionary<string, object>)o;
                if (n["body"] != null && n["block"] != null) last[Convert.ToInt32(n["body"])] = Convert.ToInt32(n["block"]);
            }
            foreach (int b in last.Values) list.Add(Get(f, b));
            return list;
        }

        Scene AssemblyScene(ViewMode m, object sel)
        {
            Scene s = new Scene { Two = m == ViewMode.Both };
            Occ pick = sel as Occ;
            List<Occ> chosen = new List<Occ>();
            if (pick != null) { if (pick.File != null && pick.Drawn) chosen.Add(pick); Flatten(pick, chosen); }
            int rebuilt = 0, exact = 0, flawed = 0;
            foreach (Occ o in flat)
            {
                bool picked = chosen.Contains(o);
                float[] status = o.Dot == "green" ? new[] { 0.45f, 0.72f, 0.50f } : o.Dot == "amber" ? new[] { 0.90f, 0.70f, 0.30f } : o.Dot == "red" ? new[] { 0.88f, 0.35f, 0.32f } : Same;
                if (o.Dot == "green") rebuilt++; else if (o.Dot == "amber") exact++; else if (o.Dot == "red") flawed++;
                if (m != ViewMode.FreeCad && m != ViewMode.Differences)
                    foreach (Mesh x in OccMeshes(o.File, true)) s.Left.Add(new Item { M = x, Matrix = o.Matrix, Color = picked ? Picked : InventorColor });
                if (m == ViewMode.Both || m == ViewMode.FreeCad || m == ViewMode.Differences)
                {
                    List<Item> side = m == ViewMode.Both ? s.Right : s.Left;
                    float[] plain = m == ViewMode.Differences ? status : FreeCadColor;
                    foreach (Mesh x in OccMeshes(o.File, false)) side.Add(new Item { M = x, Matrix = o.Matrix, Color = picked ? Picked : plain });
                }
            }
            string input = "Inventor: each part's stored solid, where the assembly puts it", output = Named("FreeCAD file: as it opens, parts linked at the same places");
            if (m == ViewMode.Both) { s.LeftCaption = input; s.RightCaption = output; }
            else if (m == ViewMode.Inventor) s.LeftCaption = input;
            else if (m == ViewMode.FreeCad) s.LeftCaption = output;
            else s.LeftCaption = "Each part by how it converted\nGreen: rebuilt, matches     Amber: Inventor's exact solid     Red: solid needs checking";
            s.Info = pick != null
                ? pick.Name + ": " + (pick.Words ?? "") + (pick.Note != null ? " (" + pick.Note + ")" : "") + "."
                : string.Format("{0} parts placed: {1} rebuilt and matching, {2} using Inventor's exact solid, {3} whose solid needs checking.", flat.Count, rebuilt, exact, flawed);
            return s;
        }
    }
}
