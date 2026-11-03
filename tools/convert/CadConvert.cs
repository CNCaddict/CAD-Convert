// CAD Convert: the Windows front end. Picks Inventor parts, runs invre.exe on each (one process per file, so a bad file
// can't take the rest with it), and shows what each conversion carried and how it compares with Inventor's own solid.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CadConvert
{
    public class MainForm : Form
    {
        readonly ListView files = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, ShowItemToolTips = true, SmallImageList = new ImageList { ImageSize = new Size(48, 48), ColorDepth = ColorDepth.Depth32Bit } };
        readonly System.Collections.Concurrent.BlockingCollection<string> toThumbnail = new System.Collections.Concurrent.BlockingCollection<string>();
        int previewTicket;
        readonly ListView results = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
        readonly TextBox filter = new TextBox { Dock = DockStyle.Top };
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly ViewerPane viewer = new ViewerPane { Dock = DockStyle.Fill };
        List<Result> found = new List<Result>();
        static readonly string Remembered = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "CADConvert", "app.txt");
        static readonly string RememberedTarget = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "CADConvert", "target.txt");
        readonly TextBox output = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        readonly ComboBox target = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        readonly TextBox log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window };
        readonly Button convert = new Button { Text = "Convert", AutoSize = true };
        readonly Button openFolder = new Button { Text = "Open output folder", AutoSize = true };
        readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 8 };
        readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft };
        readonly string invre = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "invre.exe");

        public MainForm()
        {
            Text = "CAD Convert: Inventor to FreeCAD and SolidCraft";
            ClientSize = new Size(1380, 820);
            MinimumSize = new Size(900, 560);

            Button addFiles = new Button { Text = "Add files...", AutoSize = true };
            Button addFolder = new Button { Text = "Add folder...", AutoSize = true };
            Button remove = new Button { Text = "Remove", AutoSize = true };
            Button browse = new Button { Text = "Browse...", AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Top };
            target.Items.Add("FreeCAD (.FCStd)");
            target.Items.Add("SolidCraft (.sc), in a SolidCraft folder");
            target.Items.Add("Both");
            target.SelectedIndex = 0;
            output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CAD Convert");
            try
            {
                if (File.Exists(Remembered)) output.Text = File.ReadAllText(Remembered).Trim();
                int chosen;
                if (File.Exists(RememberedTarget) && int.TryParse(File.ReadAllText(RememberedTarget).Trim(), out chosen) && chosen >= 0 && chosen < target.Items.Count) target.SelectedIndex = chosen;
            }
            catch (IOException) { }
            target.SelectedIndexChanged += (s, e) =>
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(RememberedTarget)); File.WriteAllText(RememberedTarget, target.SelectedIndex.ToString()); }
                catch (IOException) { }
            };

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 0) };
            buttons.Controls.AddRange(new Control[] { addFiles, addFolder, remove });

            TableLayoutPanel settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(8, 4, 8, 4) };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settings.Controls.Add(new Label { Text = "Save to:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            settings.Controls.Add(output, 1, 0);
            settings.Controls.Add(browse, 2, 0);
            settings.Controls.Add(new Label { Text = "Convert to:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            settings.Controls.Add(target, 1, 1);
            FlowLayoutPanel run = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            run.Controls.AddRange(new Control[] { convert, openFolder });
            settings.Controls.Add(run, 1, 2);

            TabPage toConvert = new TabPage("To convert"), done = new TabPage("Results");
            files.Columns.Add("File", 240);
            files.Columns.Add("Folder", 260);
            toConvert.Controls.Add(files);
            toConvert.Controls.Add(buttons);
            new Thread(ThumbnailLoop) { IsBackground = true }.Start();
            results.Columns.Add("Name", 165);
            results.Columns.Add("Result", 155);
            results.Columns.Add("Folder", 70);
            results.SmallImageList = new ImageList { ImageSize = new Size(12, 12), ColorDepth = ColorDepth.Depth32Bit };
            foreach (KeyValuePair<string, Color> dot in new Dictionary<string, Color> { { "green", Color.FromArgb(46, 160, 67) }, { "amber", Color.FromArgb(226, 150, 20) }, { "red", Color.FromArgb(214, 48, 49) }, { "grey", Color.FromArgb(170, 174, 180) }, { "blue", Color.FromArgb(50, 110, 220) } })
            {
                Bitmap b = new Bitmap(12, 12);
                using (Graphics g = Graphics.FromImage(b))
                using (SolidBrush fill = new SolidBrush(dot.Value))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillEllipse(fill, 1.5f, 1.5f, 9f, 9f);
                }
                results.SmallImageList.Images.Add(dot.Key, b);
            }
            done.Controls.Add(results);
            done.Controls.Add(filter);
            tabs.TabPages.Add(toConvert);
            tabs.TabPages.Add(done);

            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(tabs);
            split.Panel2.Controls.Add(log);

            SplitContainer sides = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            sides.Panel1.Controls.Add(split);
            sides.Panel2.Controls.Add(viewer);

            Controls.Add(sides);
            Controls.Add(settings);
            Controls.Add(status);
            Controls.Add(progress);
            settings.SendToBack();
            status.SendToBack();
            progress.SendToBack();
            sides.BringToFront();
            Shown += (s, e) =>
            {
                sides.SplitterDistance = 400;
                split.SplitterDistance = Math.Max(120, split.Height * 2 / 3);
                Refill();
            };
            results.SelectedIndexChanged += (s, e) => { if (results.SelectedItems.Count == 1) viewer.Show((Result)results.SelectedItems[0].Tag); };
            filter.TextChanged += (s, e) => ShowResults();
            filter.HandleCreated += (s, e) => SendMessage(filter.Handle, 0x1501, IntPtr.Zero, "Filter by name");
            output.Leave += (s, e) => Refill();
            files.SelectedIndexChanged += (s, e) => { if (files.SelectedItems.Count == 1) ShowFile((string)files.SelectedItems[0].Tag); };

            AllowDrop = true;
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => { foreach (string p in (string[])e.Data.GetData(DataFormats.FileDrop)) { if (Directory.Exists(p)) AddFolder(p); else AddFile(p); } };

            addFiles.Click += (s, e) =>
            {
                using (OpenFileDialog d = new OpenFileDialog { Filter = "Inventor parts and assemblies (*.ipt;*.iam)|*.ipt;*.iam", Multiselect = true, Title = "Choose Inventor parts and assemblies" })
                    if (d.ShowDialog(this) == DialogResult.OK) foreach (string f in d.FileNames) AddFile(f);
            };
            addFolder.Click += (s, e) =>
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "Every part and assembly under this folder, old versions left out" })
                    if (d.ShowDialog(this) == DialogResult.OK) AddFolder(d.SelectedPath);
            };
            remove.Click += (s, e) => { foreach (ListViewItem i in new List<ListViewItem>(System.Linq.Enumerable.Cast<ListViewItem>(files.SelectedItems))) files.Items.Remove(i); };
            browse.Click += (s, e) =>
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog { SelectedPath = output.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) { output.Text = d.SelectedPath; Refill(); }
            };
            openFolder.Click += (s, e) => { Directory.CreateDirectory(output.Text); Process.Start("explorer.exe", "\"" + output.Text + "\""); };
            convert.Click += (s, e) => Start();
            status.Text = "Add files, then press Convert. Your Inventor files are only read, never changed, and Inventor doesn't need to be running.";
            // Files or folders dropped on the program's icon arrive as arguments.
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && (args[1] == "--shot" || args[1] == "--preview" || args[1] == "--soak" || args[1] == "--movie")) return;
            Shown += (s, e) => { for (int i = 1; i < args.Length; i++) { if (Directory.Exists(args[i])) AddFolder(args[i]); else AddFile(args[i]); } };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wide, string text);

        // Everything already converted in the folder being saved to, remembered for next time.
        void Refill(bool showSelected = false)
        {
            string root = output.Text;
            try { Directory.CreateDirectory(Path.GetDirectoryName(Remembered)); File.WriteAllText(Remembered, root); }
            catch (IOException) { }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<Result> list = Result.Scan(root);
                Ui(() =>
                {
                    found = list;
                    ShowResults();
                    if (list.Count > 0 && files.Items.Count == 0) tabs.SelectedIndex = 1;
                    // Just converted: what was showing as a preview has a result now.
                    if (showSelected && files.SelectedItems.Count == 1) ShowFile((string)files.SelectedItems[0].Tag);
                });
            });
        }

        void ShowResults()
        {
            results.BeginUpdate();
            results.Items.Clear();
            foreach (Result r in found)
            {
                if (filter.Text.Length > 0 && r.Name.IndexOf(filter.Text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                ListViewItem i = new ListViewItem(r.Name, r.Dot) { Tag = r, ToolTipText = r.Name };
                i.SubItems.Add(r.Words);
                i.SubItems.Add(r.Folder);
                results.Items.Add(i);
            }
            results.EndUpdate();
        }

        // For testing: shows one result in the real window, saves what's on screen, and closes.
        //   --shot <results folder> <name> <Both|Inventor|FreeCad|Differences> <feature, or -1> <picture>
        public void Shot(string root, string name, string mode, int feature, string picture)
        {
            output.Text = root;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Shown += (s, e) =>
            {
                new Thread(() =>
                {
                    Action wait = () => { do { Thread.Sleep(150); } while ((bool)Invoke(new Func<bool>(() => viewer.Busy))); Thread.Sleep(400); };
                    List<Result> list = Result.Scan(root);
                    Result r = list.Find(x => x.Name == name);
                    if (r == null) { File.WriteAllText(picture + ".txt", "no result " + name); Invoke(new Action(Close)); return; }
                    Invoke(new Action(() => { found = list; ShowResults(); tabs.SelectedIndex = 1; viewer.Show(r); }));
                    wait();
                    Invoke(new Action(() => { viewer.SetMode((ViewMode)Enum.Parse(typeof(ViewMode), mode)); viewer.View.Turn(35.264 * Math.PI / 180, -45 * Math.PI / 180); viewer.View.Fit(); }));
                    wait();
                    if (feature >= 0) { Invoke(new Action(() => viewer.PickFeature(feature))); wait(); }
                    if (Environment.GetEnvironmentVariable("CADCONVERT_TEST_DRAG") == "1") { Invoke(new Action(() => viewer.View.TestMouse(int.Parse(Environment.GetEnvironmentVariable("CADCONVERT_TEST_DX") ?? "90"), -35, int.Parse(Environment.GetEnvironmentVariable("CADCONVERT_TEST_WHEEL") ?? "240")))); Thread.Sleep(400); }
                    Invoke(new Action(() =>
                    {
                        Update();
                        using (Bitmap b = new Bitmap(Width, Height))
                        using (Graphics g = Graphics.FromImage(b))
                        {
                            g.CopyFromScreen(Location, Point.Empty, Size);
                            b.Save(picture, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        File.WriteAllText(picture + ".txt", viewer.InfoText);
                        Close();
                    }));
                }) { IsBackground = true }.Start();
            };
        }

        // For the README: the real window with a result in the viewer, the model turned a full circle over the frames,
        // each frame saved as frame_000.png... (tools\make_movie.py turns them into a GIF).
        //   --movie <results folder> <name> <mode> <frames> <out folder> [width height]
        public void Movie(string root, string name, string mode, int frames, string folder, Size size)
        {
            output.Text = root;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(40, 40);
            if (!size.IsEmpty) Size = size;
            TopMost = true;
            Directory.CreateDirectory(folder);
            Shown += (s, e) =>
            {
                new Thread(() =>
                {
                    Action wait = () => { do { Thread.Sleep(150); } while ((bool)Invoke(new Func<bool>(() => viewer.Busy))); Thread.Sleep(400); };
                    List<Result> list = Result.Scan(root);
                    Result r = list.Find(x => x.Name == name);
                    if (r == null) { File.WriteAllText(Path.Combine(folder, "movie.txt"), "no result " + name); Invoke(new Action(Close)); return; }
                    Invoke(new Action(() => { found = list; ShowResults(); tabs.SelectedIndex = 1; viewer.Show(r); }));
                    wait();
                    // Fitted at the first angle, then zoomed out so the part stays in view all the way round.
                    double zoom = double.Parse(Environment.GetEnvironmentVariable("CADCONVERT_MOVIE_ZOOM") ?? "1.4", System.Globalization.CultureInfo.InvariantCulture);
                    Invoke(new Action(() => { viewer.SetMode((ViewMode)Enum.Parse(typeof(ViewMode), mode)); viewer.View.Turn(35.264 * Math.PI / 180, -45 * Math.PI / 180); viewer.View.Fit(); }));
                    wait();
                    // After the viewer has settled: it fits again by itself once the model is in.
                    Invoke(new Action(() => viewer.View.Zoom(zoom)));
                    wait();
                    for (int k = 0; k < frames; k++)
                    {
                        Invoke(new Action(() =>
                        {
                            if (k > 0) viewer.View.Spin(2 * Math.PI / frames);
                            viewer.View.Refresh();
                            Update();
                            using (Bitmap b = new Bitmap(Width, Height))
                            using (Graphics g = Graphics.FromImage(b))
                            {
                                g.CopyFromScreen(Location, Point.Empty, Size);
                                b.Save(Path.Combine(folder, string.Format("frame_{0:000}.png", k)), System.Drawing.Imaging.ImageFormat.Png);
                            }
                        }));
                        Thread.Sleep(30);
                    }
                    File.WriteAllText(Path.Combine(folder, "movie.txt"), viewer.InfoText);
                    Invoke(new Action(Close));
                }) { IsBackground = true }.Start();
            };
        }

        // For testing: every result in turn, in each mode, in the real window, counting what's drawn on each side of the picture.
        //   --soak <results folder> <out.csv>
        public void Soak(string root, string csv)
        {
            output.Text = root;
            Shown += (s, e) =>
            {
                new Thread(() =>
                {
                    Action wait = () => { Thread.Sleep(60); while ((bool)Invoke(new Func<bool>(() => viewer.Busy))) Thread.Sleep(40); Thread.Sleep(60); };
                    List<Result> list = Result.Scan(root);
                    using (StreamWriter o = new StreamWriter(csv, false, new System.Text.UTF8Encoding(false)))
                    {
                        o.WriteLine("name,result,mode,left,right,info");
                        Result last = null;
                        foreach (Result r in list)
                        {
                            Result now = r;
                            Invoke(new Action(() => viewer.Show(now)));
                            wait();
                            foreach (ViewMode m in new[] { ViewMode.FreeCad, ViewMode.Both, ViewMode.Inventor })
                            {
                                ViewMode mode = m;
                                Invoke(new Action(() => viewer.SetMode(mode)));
                                wait();
                                int[] drawn = (int[])Invoke(new Func<int[]>(() => viewer.View.Drawn()));
                                o.WriteLine("\"{0}\",\"{1}\",{2},{3},{4},\"{5}\"", r.Name, r.Words, mode, drawn[0], drawn[1], ((string)Invoke(new Func<string>(() => viewer.InfoText))).Replace("\"", "'").Substring(0, 30));
                            }
                            o.Flush();
                            last = r;
                        }
                    }
                    Invoke(new Action(Close));
                }) { IsBackground = true }.Start();
            };
        }

        // For testing: adds a file as if opened, saves the screen straight after and again once the 3D view is up, and closes.
        //   --preview <file> <results folder> <picture>
        public void Preview(string file, string root, string picture)
        {
            output.Text = root;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Shown += (s, e) =>
            {
                new Thread(() =>
                {
                    Action<string> save = name => Invoke(new Action(() =>
                    {
                        Update();
                        using (Bitmap b = new Bitmap(Width, Height))
                        using (Graphics g = Graphics.FromImage(b))
                        {
                            g.CopyFromScreen(Location, Point.Empty, Size);
                            b.Save(name, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }));
                    Thread.Sleep(600);
                    Stopwatch watch = Stopwatch.StartNew();
                    Invoke(new Action(() => AddFile(file)));
                    Thread.Sleep(700);
                    save(picture + ".first.png");
                    string first = (string)Invoke(new Func<string>(() => viewer.InfoText));
                    while (watch.Elapsed.TotalSeconds < 170 && (bool)Invoke(new Func<bool>(() => !viewer.Shown3D || viewer.Busy))) Thread.Sleep(200);
                    double seconds = watch.Elapsed.TotalSeconds;
                    Thread.Sleep(500);
                    save(picture + ".3d.png");
                    File.WriteAllText(picture + ".txt", first + "\r\n" + seconds.ToString("0.0") + " s to 3D\r\n" + (string)Invoke(new Func<string>(() => viewer.InfoText)));
                    Invoke(new Action(Close));
                }) { IsBackground = true }.Start();
            };
        }

        static readonly string PreviewFolder = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "CADConvert", "preview");

        // A file picked in the list: its result if it has one; if not, its own picture at once, then its solid to turn round.
        void ShowFile(string file)
        {
            int ticket = ++previewTicket;
            string name = Path.GetFileNameWithoutExtension(file);
            Result done = found.Find(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (done != null) { viewer.Show(done); return; }
            bool assembly = string.Equals(Path.GetExtension(file), ".iam", StringComparison.OrdinalIgnoreCase);
            viewer.ShowPicture(null, "Reading " + name + "...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Bitmap image = Thumbnail.Read(file);
                Ui(() =>
                {
                    if (ticket != previewTicket) { if (image != null) image.Dispose(); return; }
                    viewer.ShowPicture(image, assembly ? "The picture stored in the file. An assembly shows in 3D once it's converted." : "The picture stored in the file. The solid is on its way...");
                });
                if (assembly) return;
                string why = null, view = null;
                try
                {
                    FileInfo info = new FileInfo(file);
                    string key = BitConverter.ToString(System.Security.Cryptography.MD5.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(file.ToLowerInvariant() + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length))).Replace("-", "");
                    Directory.CreateDirectory(PreviewFolder);
                    view = Path.Combine(PreviewFolder, key + ".view");
                    if (!File.Exists(view))
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(invre, "preview \"" + file + "\" \"" + view + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                        using (Process p = Process.Start(psi))
                        {
                            string text = p.StandardOutput.ReadToEnd();
                            p.WaitForExit();
                            if (p.ExitCode != 0 || !File.Exists(view)) why = text.Trim();
                        }
                    }
                }
                catch (Exception ex) { why = ex.Message; }
                Ui(() =>
                {
                    if (ticket != previewTicket) return;
                    if (why != null) viewer.Note("The picture stored in the file. No 3D view: " + why);
                    else viewer.Show(new Result { Name = name, Path = view, Words = "Not converted yet", Dot = "grey" });
                });
            });
        }

        void ThumbnailLoop()
        {
            foreach (string file in toThumbnail.GetConsumingEnumerable())
            {
                Bitmap image = Thumbnail.Read(file), small = null;
                if (image != null) { small = Thumbnail.Small(image, 48); image.Dispose(); }
                string key = file;
                Bitmap icon = small;
                Ui(() =>
                {
                    foreach (ListViewItem i in files.Items)
                    {
                        if ((string)i.Tag != key) continue;
                        if (icon != null) { files.SmallImageList.Images.Add(key, icon); i.ImageKey = key; }
                        break;
                    }
                });
            }
        }

        void AddFile(string f)
        {
            string ext = Path.GetExtension(f);
            if (!string.Equals(ext, ".ipt", StringComparison.OrdinalIgnoreCase) && !string.Equals(ext, ".iam", StringComparison.OrdinalIgnoreCase)) return;
            foreach (ListViewItem have in files.Items) if (string.Equals((string)have.Tag, f, StringComparison.OrdinalIgnoreCase)) return;
            ListViewItem item = new ListViewItem(Path.GetFileName(f)) { Tag = f, ToolTipText = f };
            item.SubItems.Add(Path.GetDirectoryName(f));
            files.Items.Add(item);
            toThumbnail.Add(f);
            // Opening a file shows it.
            tabs.SelectedIndex = 0;
            if (files.SelectedItems.Count == 0) item.Selected = true;
        }

        void AddFolder(string dir)
        {
            foreach (string pattern in new[] { "*.ipt", "*.iam" })
                foreach (string f in Directory.GetFiles(dir, pattern, SearchOption.AllDirectories))
                    if (f.IndexOf("OldVersions", StringComparison.OrdinalIgnoreCase) < 0) AddFile(f);
        }

        void Start()
        {
            if (files.Items.Count == 0) { status.Text = "Add at least one part first."; return; }
            List<string> list = new List<string>();
            foreach (ListViewItem i in files.Items) list.Add((string)i.Tag);
            string outDir = output.Text;
            bool toFreeCad = target.SelectedIndex != 1, toSolidCraft = target.SelectedIndex != 0;
            convert.Enabled = false;
            progress.Maximum = list.Count * (toFreeCad && toSolidCraft ? 2 : 1);
            progress.Value = 0;
            log.Clear();
            new Thread(() => Run(list, outDir, toFreeCad, toSolidCraft)) { IsBackground = true }.Start();
        }

        void Ui(Action a) { if (!IsDisposed) BeginInvoke(a); }

        void Run(List<string> list, string outDir, bool toFreeCad, bool toSolidCraft)
        {
            DateTime started = DateTime.UtcNow;
            int done = 0;
            Func<string, string, string, string> runAll = (verb, program, added) =>
            {
                int matched = 0, solid = 0, flawed = 0, differs = 0, failed = 0;
                foreach (string f in list)
                {
                    string file = f;
                    Ui(() => { status.Text = "Converting " + Path.GetFileName(file) + " to " + program + "..."; log.AppendText(Path.GetFileName(file) + " to " + program + "\r\n"); });
                    int code = 2;
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(invre, verb + " \"" + f + "\" \"" + outDir + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                        using (Process p = Process.Start(psi))
                        {
                            p.ErrorDataReceived += (s, e) => { if (e.Data != null) Ui(() => log.AppendText("  " + e.Data + "\r\n")); };
                            p.BeginErrorReadLine();
                            string line;
                            while ((line = p.StandardOutput.ReadLine()) != null)
                            {
                                string l = line;
                                Ui(() => log.AppendText(l + "\r\n"));
                            }
                            p.WaitForExit();
                            code = p.ExitCode;
                        }
                    }
                    catch (Exception ex) { Ui(() => log.AppendText("  " + ex.Message + "\r\n")); }
                    if (code == 0) matched++; else if (code == 3) solid++; else if (code == 4) flawed++; else if (code == 1) differs++; else failed++;
                    done++;
                    int d = done;
                    Ui(() => { progress.Value = d; log.AppendText("\r\n"); });
                }
                return string.Format("{0}: {1} rebuilt with full history; {2} {3}; {4} needing a check; {5} differ; {6} failed.", program, matched, solid, added, flawed, differs, failed);
            };
            string summary = "";
            if (toFreeCad)
            {
                summary = runAll("convert", "FreeCAD", "with Inventor's exact solid added");
                // FreeCAD sets up how the files open, in one pass over everything just converted.
                Ui(() => status.Text = "Setting up how the files open in FreeCAD...");
                int minutes = (int)Math.Ceiling((DateTime.UtcNow - started).TotalMinutes) + 1;
                try
                {
                    ProcessStartInfo finish = new ProcessStartInfo(invre, "finish \"" + outDir + "\" " + minutes) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    using (Process p = Process.Start(finish))
                    {
                        string text = p.StandardOutput.ReadToEnd();
                        p.WaitForExit();
                        Ui(() => log.AppendText(text.Replace("\n", "\r\n")));
                    }
                }
                catch (Exception ex) { Ui(() => log.AppendText("  " + ex.Message + "\r\n")); }
            }
            if (toSolidCraft) summary += (summary.Length > 0 ? "  " : "") + runAll("solidcraft", "SolidCraft", "as Inventor's exact solid");
            string final = summary;
            Ui(() =>
            {
                status.Text = final;
                convert.Enabled = true;
                Refill(true);
            });
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 3 && args[1] == "--check") { ViewerPane.CheckAll(args[2], args[3]); return; }
            MainForm form = new MainForm();
            if (args.Length > 6 && args[1] == "--shot") form.Shot(args[2], args[3], args[4], int.Parse(args[5]), args[6]);
            if (args.Length > 4 && args[1] == "--preview") form.Preview(args[2], args[3], args[4]);
            if (args.Length > 3 && args[1] == "--soak") form.Soak(args[2], args[3]);
            if (args.Length > 6 && args[1] == "--movie")
                form.Movie(args[2], args[3], args[4], int.Parse(args[5]), args[6], args.Length > 8 ? new Size(int.Parse(args[7]), int.Parse(args[8])) : Size.Empty);
            Application.Run(form);
        }
    }
}
