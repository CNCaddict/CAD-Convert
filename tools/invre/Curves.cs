// Sketch splines, ellipses and text.
//
// BSplineCurve2D (F9372FD4): u32 state at +0x22 (0x40 reference, 0x04 closed), +0x26 the sketch, +0x2A a kind-2 list of
// points (start, end, then the other fit points), a kind-2 list of u32 giving the fit order (indexes into the points;
// a closed spline repeats the first), then u32 degree, f64 tolerance 1e-9, {u32 n, u32 capacity, u32 8} n f64 knots,
// 8 bytes, {u32 8, u32 m, u32 capacity, u32 8} m (x, y) poles in cm; then f64 1e-12, and a second copy of the block.
// Spline2D (317B7346): a projected curve. +0x2A its two end points, then u32 0, u32 0, u32 5, u32 0, u32 id: the id of
// an intcurve-curve record in the DC segment's ASM block, holding the curve in model coordinates (cm).
// Ellipse2D (4507D460): as a circle: +0x2A end points (null for a whole ellipse), +0x4A centre, +0x4E unit major axis
// (2 f64), +0x5E major radius, +0x66 minor radius.
// Text2D (2510347F): +0x1E its Point2D, +0x22 a 1FBB3C01 node: f64 x, y at +0x22, +0x2A (cm), the string with "\A1;" style
// codes, and the box's two corners after the string.
// Sketch image (DC93DB08): +0x22 the sketch, f64 width, height, x, y from +0x26 (cm); 8FEC335F links it to its first point
// and four lines. Frame (7A98AD0E, named TextBox2D): the four lines and four points around a text or an image.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InvRe
{
    public static class Curves
    {
        public const uint BSpline = 0xF9372FD4, Spline2D = 0x317B7346, Ellipse2D = 0x4507D460, Text2D = 0x2510347F, FitPoint = 0x7C6D149E, ControlPoint = 0x5D8C859D,
            Image = 0xDC93DB08, ImageLink = 0x8FEC335F, Frame = 0x7A98AD0E;

        static uint U32(byte[] d, int p) { return BitConverter.ToUInt32(d, p); }
        static double F64(byte[] d, int p) { return BitConverter.ToDouble(d, p); }
        static string Tok(double v) { return (Math.Round(v, 6) + 0.0).ToString("R", CultureInfo.InvariantCulture); }

        public class Spline
        {
            public int Degree;
            public double[] Knots, Poles;     // poles x, y, x, y, ... (cm)
            public List<int> Points = new List<int>(), Order = new List<int>();
            public double[] Parameters;       // fit-point parameters (ACAD fits only)
            public int Kind;                  // 0 ACAD fit, 2 smooth fit, 3 sweet fit, 5 fixed, 6 control points
            public bool Closed;
            public double FitTolerance;       // a projected curve's: how far it may be from the true curve (cm)
            public double Form;               // a projected curve's form: 0 open, otherwise its ends meet (the API says closed)
        }

        // The tolerance 1e-9 marks where the B-spline starts.
        static int Marker(byte[] d, int from)
        {
            for (int p = from; p + 8 <= d.Length; p++)
                if (d[p] == 0x95 && d[p + 1] == 0xD6 && d[p + 2] == 0x26 && d[p + 3] == 0xE8 && d[p + 4] == 0x0B && d[p + 5] == 0x2E && d[p + 6] == 0x11 && d[p + 7] == 0x3E) return p;
            return -1;
        }

        public static Spline ReadSpline(Node n)
        {
            byte[] d = n.Data;
            int s = Sketches.Shift(d);
            Spline sp = new Spline();
            sp.Closed = (U32(d, 0x22 + s) & 0x04) != 0;
            List<int> pts;
            int p = Sketches.ReadList(d, 0x2A + s, out pts);
            sp.Points = pts;
            int m = Marker(d, p);
            if (m < 8) return null;
            for (int q = p; q + 16 <= m; q++)
                if (U32(d, q) == 0x30000002 && U32(d, q + 12) == 8)
                {
                    int c = (int)U32(d, q + 4);
                    for (int i = 0; i < c && q + 16 + 4 * i + 4 <= m; i++) sp.Order.Add((int)U32(d, q + 16 + 4 * i));
                    break;
                }
            sp.Degree = (int)U32(d, m - 4);
            int k = m + 8, nk = (int)U32(d, k);
            if (nk <= 0 || nk > 100000 || k + 12 + 8 * nk > d.Length) return null;
            sp.Knots = new double[nk];
            for (int i = 0; i < nk; i++) sp.Knots[i] = F64(d, k + 12 + 8 * i);
            int q2 = k + 12 + 8 * nk + 8;   // u32 8, u32 m, u32 capacity, u32 8
            int np = (int)U32(d, q2 + 4);
            if (np <= 0 || np > 100000 || q2 + 16 + 16 * np > d.Length) return null;
            sp.Poles = new double[2 * np];
            for (int i = 0; i < 2 * np; i++) sp.Poles[i] = F64(d, q2 + 16 + 8 * i);
            // f64 1e-12, u32 1, u32 1, f64 first parameter, f64 last parameter, {u32 n, u32 capacity, u32 8} n f64 (the
            // fit points' parameters, ACAD fits only), u8 kind.
            int t = q2 + 16 + 16 * np, nf = t + 36 <= d.Length ? (int)U32(d, t + 32) : -1;
            if (nf < 0 || nf > 100000 || t + 44 + 8 * nf >= d.Length) return null;
            sp.Parameters = new double[nf];
            for (int i = 0; i < nf; i++) sp.Parameters[i] = F64(d, t + 44 + 8 * i);
            sp.Kind = d[t + 44 + 8 * nf];
            return sp;
        }

        static string Xy(NodeTable t, int point)
        {
            double[] g = Sketches.Geometry2D(t, point);
            return g == null ? "? ?" : Tok(10 * g[0]) + " " + Tok(10 * g[1]);
        }

        static string SplineNumbers(Spline sp, bool closed)
        {
            StringBuilder b = new StringBuilder("order " + (sp.Degree + 1) + " closed " + (closed ? "true" : "false") + " rational false periodic false poles");
            foreach (double v in sp.Poles) b.Append(" " + Tok(10 * v));
            b.Append(" knots");
            foreach (double v in sp.Knots) b.Append(" " + Tok(v));
            return b.ToString();
        }

        // Every spline, ellipse and text item of one sketch, in the truth's form ("curves").
        public static List<string> OfSketch(NodeTable t, int sketch, Dictionary<int, List<SabRecord>> asm)
        {
            List<string> r = new List<string>();
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Data == null) continue;
                uint key = n.Type.Key;
                if (key != BSpline && key != Spline2D && key != Ellipse2D && key != Text2D && key != Image) continue;
                string text = Text(t, n, sketch, asm);
                if (text != null) r.Add(text);
            }
            return r;
        }

        public static int SketchOfCurve(Node n)
        {
            return Sketches.XRef(n.Data, 0x26 + Sketches.Shift(n.Data));
        }

        static bool Reference(Node n) { return (U32(n.Data, 0x22 + Sketches.Shift(n.Data)) & 0x40) != 0; }

        public static string Text(NodeTable t, Node n, int sketch, Dictionary<int, List<SabRecord>> asm)
        {
            byte[] d = n.Data;
            int s = Sketches.Shift(d);
            uint key = n.Type.Key;
            if (key == Text2D)
            {
                Node box = t.At(Sketches.XRef(d, 0x22 + s));
                if (box == null || box.Data == null) return null;
                int bs = Sketches.Shift(box.Data);
                if (TextSketch(t, box) != sketch) return null;
                string label = System.Text.RegularExpressions.Regex.Replace(NodeTable.Label(box) ?? "", @"\\[A-Za-z][^;]*;", "");
                string words = "text '" + label + "' " + Tok(10 * F64(box.Data, 0x22 + bs)) + " " + Tok(10 * F64(box.Data, 0x2A + bs));
                double[] c = TextCorners(box);
                if (c != null && (c[2] != c[0] || c[3] != c[1])) words += " box " + Tok(10 * (c[2] - c[0])) + " " + Tok(10 * (c[3] - c[1]));
                return words;
            }
            if (key == Image)
            {
                if (Sketches.XRef(d, 0x22 + s) != sketch) return null;
                string name;
                Sketches.BrowserNames(t).TryGetValue(n.Index, out name);
                return "image name '" + name + "' width " + Tok(10 * F64(d, 0x26 + s)) + " height " + Tok(10 * F64(d, 0x2E + s)) + " position " + Tok(10 * F64(d, 0x36 + s)) + " " + Tok(10 * F64(d, 0x3E + s));
            }
            if (SketchOfCurve(n) != sketch) return null;
            string refd = Reference(n) ? " reference" : "";
            if (key == Ellipse2D)
            {
                List<int> pts;
                Sketches.ReadList(d, 0x2A + s, out pts);
                int centre = Sketches.XRef(d, 0x4A + s);
                string text = (pts.Count > 1 && pts[0] >= 0 ? "ellipse-arc" : "ellipse") + " center " + Xy(t, centre) + " major " + Tok(F64(d, 0x4E + s)) + " " + Tok(F64(d, 0x56 + s))
                    + " radii " + Tok(10 * F64(d, 0x5E + s)) + " " + Tok(10 * F64(d, 0x66 + s));
                if (pts.Count > 1 && pts[0] >= 0) text += " ends " + Xy(t, pts[0]) + " " + Xy(t, pts[1]);
                return text + refd;
            }
            if (key == Spline2D) return ProjectedText(t, n, sketch, asm) + refd;
            Spline sp = ReadSpline(n);
            if (sp == null) return "spline (not read)";
            if (sp.Kind == 6)
            {
                StringBuilder b = new StringBuilder("controlpointspline control");
                foreach (int c in ControlPoints(t, n.Index)) b.Append(" " + Xy(t, c));
                return b + " " + SplineNumbers(sp, sp.Closed) + refd;
            }
            if (sp.Kind == 5) return "fixedspline " + SplineNumbers(sp, sp.Closed) + refd;
            StringBuilder f = new StringBuilder("spline fit");
            foreach (int i in sp.Order) if (i >= 0 && i < sp.Points.Count) f.Append(" " + Xy(t, sp.Points[i]));
            f.Append(" method " + Method(sp.Kind));
            return f + " " + SplineNumbers(sp, sp.Closed) + refd;
        }

        static string Method(int kind) { return kind == 0 ? "kACADSplineFit" : kind == 2 ? "kSmoothSplineFit" : kind == 3 ? "kSweetSplineFit" : "kind" + kind; }

        // A control-point spline's control points: 5D8C859D holds a list of {the spline, its control points, the lines
        // of the control polygon}.
        static List<int> ControlPoints(NodeTable t, int spline)
        {
            List<int> r = new List<int>();
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Type.Key != ControlPoint || n.Data == null) continue;
                int at = -1;
                for (int p = 0x16; p + 4 <= n.Data.Length && at < 0; p++)
                    if (Sketches.XRef(n.Data, p) == spline) at = p;
                if (at < 0) continue;
                for (int p = at + 4; p + 4 <= n.Data.Length; p += 4)
                {
                    int x = Sketches.XRef(n.Data, p);
                    Node target = t.At(x);
                    if (target == null || target.Type == null || target.Type.Key != Sketches.Point2D) break;
                    r.Add(x);
                }
                break;
            }
            return r;
        }

        // A text node's box: x1, y1, x2, y2 (cm), 0x20 bytes after the string, as two 3D points.
        public static double[] TextCorners(Node box)
        {
            string label = NodeTable.Label(box);
            if (label == null) return null;
            byte[] d = box.Data;
            for (int p = 0x30; p + 4 + 2 * label.Length + 0x48 <= d.Length; p++)
            {
                if (U32(d, p) != label.Length || Encoding.Unicode.GetString(d, p + 4, 2 * label.Length) != label) continue;
                int e = p + 4 + 2 * label.Length;
                return new[] { F64(d, e + 0x20), F64(d, e + 0x28), F64(d, e + 0x38), F64(d, e + 0x40) };
            }
            return null;
        }

        static int TextSketch(NodeTable t, Node box)
        {
            for (int p = 0x1A; p + 4 <= box.Data.Length; p++)
            {
                Node r = t.At(Sketches.XRef(box.Data, p));
                if (r != null && r.Type != null && r.Type.Key == Sketches.Sketch2D) return r.Index;
            }
            return -1;
        }

        // The ASM block's records by intcurve id.
        public static Dictionary<int, List<SabRecord>> AsmBlocks(NodeTable t)
        {
            Dictionary<int, List<SabRecord>> r = new Dictionary<int, List<SabRecord>>();
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Type.Key != Profiles.Asm || n.Data == null) continue;
                int at = Sab.Find(n.Data, 0);
                if (at < 0) continue;
                string error;
                List<SabRecord> recs = Sab.Read(n.Data, at, out error);
                foreach (SabRecord x in recs)
                    if (x.Type != null && x.Type.EndsWith("-curve") && x.Int(1) >= 0 && !r.ContainsKey(x.Int(1))) r[x.Int(1)] = new List<SabRecord> { x };
            }
            return r;
        }

        static double Num(object o) { return o is int ? (int)o : o is double ? (double)o : double.NaN; }

        // A projected curve: Spline2D's id names an intcurve holding {"nubs", degree, form, n, (knot, multiplicity) x n,
        // poles (x, y, z) in model cm}. End knots repeat degree times there, degree + 1 in the API. A sense flag of T
        // reverses the curve (the API then gives reversed poles and negated knots).
        static string ProjectedText(NodeTable t, Node n, int sketch, Dictionary<int, List<SabRecord>> asm)
        {
            string reason;
            Spline sp = Projected(t, n, sketch, asm, out reason);
            return sp == null ? "spline projected (" + reason + ")" : "spline projected " + SplineNumbers(sp, sp.Form != 0);
        }

        public static Spline Projected(NodeTable t, Node n, int sketch, Dictionary<int, List<SabRecord>> asm, out string reason)
        {
            reason = null;
            byte[] d = n.Data;
            int s = Sketches.Shift(d);
            List<int> pts;
            int p = Sketches.ReadList(d, 0x2A + s, out pts);
            int id = (int)U32(d, p + 16);   // u32 0, u32 0, u32 5, u32 0, u32 id
            List<SabRecord> found;
            if (!asm.TryGetValue(id, out found)) { reason = "id not in the ASM block"; return null; }
            SabRecord c = found[0];
            if (c.Type != "intcurve-curve") { reason = c.Type; return null; }
            int i = c.Fields.FindIndex(f => (f as string) == "nubs");
            if (i < 0) { reason = "intcurve without nubs: " + string.Join(" ", c.Fields.GetRange(0, Math.Min(8, c.Fields.Count)).ConvertAll(f => f == null ? "null" : f.ToString()).ToArray()); return null; }
            int degree = (int)Num(c.Fields[i + 1]), nk = (int)Num(c.Fields[i + 3]);
            List<double> knots = new List<double>();
            int q = i + 4, total = 0;
            for (int k = 0; k < nk; k++, q += 2)
            {
                double v = Num(c.Fields[q]);
                int mult = (int)Num(c.Fields[q + 1]);
                total += mult;
                for (int e = 0; e < mult + (k == 0 || k == nk - 1 ? 1 : 0); e++) knots.Add(v);
            }
            int np = total - degree + 1;
            double[] m = Sketches.Placement(t, sketch);
            if (m == null) { reason = "no placement"; return null; }
            List<double> poles = new List<double>();
            for (int k = 0; k < np; k++)
            {
                double x = Num(c.Fields[q + 3 * k]) - m[3], y = Num(c.Fields[q + 3 * k + 1]) - m[7], z = Num(c.Fields[q + 3 * k + 2]) - m[11];
                poles.Add(x * m[0] + y * m[4] + z * m[8]);
                poles.Add(x * m[1] + y * m[5] + z * m[9]);
            }
            if (c.Fields.Count > 3 && c.Fields[3] is bool && (bool)c.Fields[3])
            {
                List<double> rev = new List<double>();
                for (int k = np - 1; k >= 0; k--) { rev.Add(poles[2 * k]); rev.Add(poles[2 * k + 1]); }
                poles = rev;
                knots.Reverse();
                knots = knots.ConvertAll(v => -v);
            }
            double fit = q + 3 * np < c.Fields.Count ? Num(c.Fields[q + 3 * np]) : 0;
            return new Spline { Degree = degree, Knots = knots.ToArray(), Poles = poles.ToArray(), FitTolerance = double.IsNaN(fit) ? 0 : fit, Form = Num(c.Fields[i + 2]), Kind = c.Fields.Contains("int_int_cur") ? 1 : 0 };
        }

        // A B-spline's point at parameter u (de Boor), poles x, y, x, y, ...
        public static double[] Eval(Spline sp, double u)
        {
            int p = sp.Degree, n = sp.Poles.Length / 2;
            double[] U = sp.Knots;
            int k = p;
            while (k < n - 1 && u >= U[k + 1]) k++;
            double[] x = new double[p + 1], y = new double[p + 1];
            for (int j = 0; j <= p; j++) { x[j] = sp.Poles[2 * (k - p + j)]; y[j] = sp.Poles[2 * (k - p + j) + 1]; }
            for (int r = 1; r <= p; r++)
                for (int j = p; j >= r; j--)
                {
                    double den = U[j + 1 + k - r] - U[j + k - p], a = den == 0 ? 0 : (u - U[j + k - p]) / den;
                    x[j] = (1 - a) * x[j - 1] + a * x[j];
                    y[j] = (1 - a) * y[j - 1] + a * y[j];
                }
            return new[] { x[p], y[p] };
        }

        // How far (cm) a point is from the spline.
        public static double Distance(Spline sp, double x, double y)
        {
            double lo = sp.Knots[sp.Degree], hi = sp.Knots[sp.Knots.Length - sp.Degree - 1], best = double.MaxValue, at = lo;
            int steps = 40 * Math.Max(1, sp.Poles.Length / 2);
            Func<double, double> dist = u => { double[] q = Eval(sp, u); return Math.Sqrt((q[0] - x) * (q[0] - x) + (q[1] - y) * (q[1] - y)); };
            for (int i = 0; i <= steps; i++)
            {
                double u = lo + (hi - lo) * i / steps, e = dist(u);
                if (e < best) { best = e; at = u; }
            }
            double w = (hi - lo) / steps, a = Math.Max(lo, at - w), b = Math.Min(hi, at + w);
            for (int i = 0; i < 80; i++)
            {
                double m1 = a + (b - a) / 3, m2 = b - (b - a) / 3;
                if (dist(m1) < dist(m2)) b = m2; else a = m1;
            }
            return Math.Min(best, dist((a + b) / 2));
        }

        // Every curve of a part checked against what the file says elsewhere; problems go to bad, counts to tally.
        public static void Check(NodeTable t, string part, List<string> bad, SortedDictionary<string, int> tally)
        {
            Action<string> count = key => { int c; tally.TryGetValue(key, out c); tally[key] = c + 1; };
            Dictionary<int, List<SabRecord>> asm = null;
            const double tol = 1e-4;
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Data == null) continue;
                string where = part + " #" + n.Index.ToString("X") + " " + n.TypeName + ": ";
                if (n.Type.Key == BSpline)
                {
                    Spline sp = ReadSpline(n);
                    if (sp == null) { count("bspline: not read"); bad.Add(where + "not read"); continue; }
                    count("bspline: kind " + sp.Kind + (sp.Parameters.Length > 0 ? " with parameters" : ""));
                    count("bspline: order " + (sp.Degree + 1));
                    if (sp.Closed) count("bspline: closed");
                    if (sp.Knots.Length != sp.Poles.Length / 2 + sp.Degree + 1) { bad.Add(where + "knots " + sp.Knots.Length + " poles " + sp.Poles.Length / 2 + " degree " + sp.Degree); continue; }
                    double[] first = Eval(sp, sp.Knots[sp.Degree]), last = Eval(sp, sp.Knots[sp.Knots.Length - sp.Degree - 1]);
                    double[] a = sp.Points.Count > 0 ? Sketches.Geometry2D(t, sp.Points[0]) : null, b = sp.Points.Count > 1 ? Sketches.Geometry2D(t, sp.Points[1]) : null;
                    if (a == null || b == null) { bad.Add(where + "no end points"); continue; }
                    double e1 = Math.Sqrt((first[0] - a[0]) * (first[0] - a[0]) + (first[1] - a[1]) * (first[1] - a[1])), e2 = Math.Sqrt((last[0] - b[0]) * (last[0] - b[0]) + (last[1] - b[1]) * (last[1] - b[1]));
                    if (e1 > tol || e2 > tol) bad.Add(where + "ends off by " + e1.ToString("G3") + ", " + e2.ToString("G3") + " cm");
                    else count("bspline: ends agree");
                    foreach (int i in sp.Order) if (i < 0 || i >= sp.Points.Count) bad.Add(where + "fit index " + i + " of " + sp.Points.Count);
                    if (sp.Kind == 6)
                    {
                        List<int> control = ControlPoints(t, n.Index);
                        if (control.Count * 2 != sp.Poles.Length) { bad.Add(where + control.Count + " control points, " + sp.Poles.Length / 2 + " poles"); continue; }
                        for (int i = 0; i < control.Count; i++)
                        {
                            double[] c = Sketches.Geometry2D(t, control[i]);
                            if (c == null || Math.Abs(c[0] - sp.Poles[2 * i]) > tol || Math.Abs(c[1] - sp.Poles[2 * i + 1]) > tol) bad.Add(where + "control point " + i + " is not pole " + i);
                        }
                        count("bspline: control points agree");
                    }
                    else
                    {
                        int fits = 0;
                        foreach (int i in sp.Points)
                        {
                            double[] c = Sketches.Geometry2D(t, i);
                            if (c == null) continue;
                            double e = Distance(sp, c[0], c[1]);
                            if (e > tol) bad.Add(where + "point #" + i.ToString("X") + " is " + e.ToString("G3") + " cm off the curve");
                            fits++;
                        }
                        tally["bspline: fit points on the curve"] = (tally.ContainsKey("bspline: fit points on the curve") ? tally["bspline: fit points on the curve"] : 0) + fits;
                    }
                }
                else if (n.Type.Key == Ellipse2D)
                {
                    byte[] d = n.Data;
                    int s = Sketches.Shift(d);
                    double ux = F64(d, 0x4E + s), uy = F64(d, 0x56 + s), R = F64(d, 0x5E + s), r = F64(d, 0x66 + s);
                    double[] c = Sketches.Geometry2D(t, Sketches.XRef(d, 0x4A + s));
                    List<int> pts;
                    Sketches.ReadList(d, 0x2A + s, out pts);
                    bool arc = pts.Count > 1 && pts[0] >= 0;
                    count("ellipse: " + (arc ? "arc" : "whole"));
                    if (c == null || Math.Abs(ux * ux + uy * uy - 1) > 1e-6 || R < r || r <= 0) { bad.Add(where + "centre or axes wrong: u " + ux + " " + uy + " radii " + R + " " + r); continue; }
                    if (arc)
                        foreach (int i in pts)
                        {
                            double[] q = Sketches.Geometry2D(t, i);
                            if (q == null) continue;
                            double dx = q[0] - c[0], dy = q[1] - c[1], a = (dx * ux + dy * uy) / R, b = (-dx * uy + dy * ux) / r, e = Math.Abs(Math.Sqrt(a * a + b * b) - 1) * r;
                            if (e > tol) bad.Add(where + "point #" + i.ToString("X") + " is about " + e.ToString("G3") + " cm off the ellipse");
                            else count("ellipse: arc points on it");
                        }
                }
                else if (n.Type.Key == Spline2D)
                {
                    if (asm == null) asm = AsmBlocks(t);
                    string reason;
                    Spline sp = Projected(t, n, SketchOfCurve(n), asm, out reason);
                    count("projected: " + (sp != null ? "nubs" : reason));
                    if (sp == null) continue;
                    count("projected: form " + sp.Form + (sp.Kind == 1 ? ", intersection curve" : ", exact curve"));
                    double[] from = Eval(sp, sp.Knots[sp.Degree]), to = Eval(sp, sp.Knots[sp.Knots.Length - sp.Degree - 1]);
                    if (Math.Abs(from[0] - to[0]) + Math.Abs(from[1] - to[1]) < 1e-6) count("projected: curve's ends meet");
                    List<int> pts;
                    Sketches.ReadList(n.Data, 0x2A + Sketches.Shift(n.Data), out pts);
                    foreach (int i in pts)
                    {
                        double[] c = Sketches.Geometry2D(t, i);
                        if (c == null) continue;
                        double e = Distance(sp, c[0], c[1]);
                        if (e > Math.Max(tol, sp.FitTolerance)) bad.Add(where + "point #" + i.ToString("X") + " is " + e.ToString("G3") + " cm off the curve, fit tolerance " + sp.FitTolerance.ToString("G3"));
                        else count(e > tol ? "projected: points off the curve, within the record's fit tolerance" : "projected: points on the curve");
                    }
                }
                else if (n.Type.Key == Text2D)
                {
                    Node box = t.At(Sketches.XRef(n.Data, 0x22 + Sketches.Shift(n.Data)));
                    string label = box == null ? null : NodeTable.Label(box);
                    count("text: " + (label != null ? "read" : "no string"));
                }
                else if (n.Type.Key == Image) count("image: read");
                else if (n.Type.Key == Frame)
                {
                    // Its corners against the box of the text or image that uses one of its points.
                    List<double[]> corners = new List<double[]>();
                    HashSet<int> points = new HashSet<int>();
                    int lines = 0;
                    for (int p = 0x16; p + 4 <= n.Data.Length; p += 1)
                    {
                        if ((U32(n.Data, p) & 0x80000000) == 0 || (n.Data[p + 3] != 0x80)) continue;
                        Node r = t.At(Sketches.XRef(n.Data, p));
                        if (r == null || r.Type == null) continue;
                        if (r.Type.Key == Sketches.Line2D) lines++;
                        if (r.Type.Key == Sketches.Point2D && points.Add(r.Index)) corners.Add(Sketches.Geometry2D(t, r.Index));
                    }
                    if (lines != 4 || corners.Count != 4) { bad.Add(where + lines + " lines, " + corners.Count + " points"); continue; }
                    double[] box = FrameContent(t, points);
                    if (box == null) { count("frame: owner not found"); bad.Add(where + "owner not found"); continue; }
                    double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
                    foreach (double[] c in corners) { x1 = Math.Min(x1, c[0]); y1 = Math.Min(y1, c[1]); x2 = Math.Max(x2, c[0]); y2 = Math.Max(y2, c[1]); }
                    if (Math.Abs(x1 - box[0]) > tol || Math.Abs(y1 - box[1]) > tol || Math.Abs(x2 - box[2]) > tol || Math.Abs(y2 - box[3]) > tol)
                        bad.Add(where + string.Format("corners {0:F4} {1:F4} {2:F4} {3:F4}, its owner says {4:F4} {5:F4} {6:F4} {7:F4}", x1, y1, x2, y2, box[0], box[1], box[2], box[3]));
                    else count("frame: corners agree with its " + (box[4] == 0 ? "text" : "image"));
                }
            }
        }

        // The rectangle x1, y1, x2, y2, kind (0 text, 1 image) of the text or image holding one of these points.
        static double[] FrameContent(NodeTable t, HashSet<int> points)
        {
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Data == null) continue;
                int s = Sketches.Shift(n.Data);
                if (n.Type.Key == Text2D && points.Contains(Sketches.XRef(n.Data, 0x1E + s)))
                {
                    Node box = t.At(Sketches.XRef(n.Data, 0x22 + s));
                    double[] c = box == null ? null : TextCorners(box);
                    if (c != null) return new[] { c[0], c[1], c[2], c[3], 0 };
                }
                if (n.Type.Key == ImageLink)
                {
                    bool mine = false;
                    Node image = null;
                    for (int p = 0x16; p + 4 <= n.Data.Length; p += 4)
                    {
                        Node r = t.At(Sketches.XRef(n.Data, p));
                        if (r == null || r.Type == null) continue;
                        if (r.Type.Key == Sketches.Point2D && points.Contains(r.Index)) mine = true;
                        if (r.Type.Key == Image) image = r;
                    }
                    if (!mine || image == null) continue;
                    int si = Sketches.Shift(image.Data);
                    double w = F64(image.Data, 0x26 + si), h = F64(image.Data, 0x2E + si), x = F64(image.Data, 0x36 + si), y = F64(image.Data, 0x3E + si);
                    return new[] { x, y - h, x + w, y, 1 };   // (x, y) is the top left corner
                }
            }
            return null;
        }

        // The API's name for a curve's type, as profile entities list it.
        public static string ApiType(NodeTable t, Node n)
        {
            if (n.Type.Key == Spline2D) return "kSketchSplineObject";
            if (n.Type.Key == Ellipse2D)
            {
                List<int> pts;
                Sketches.ReadList(n.Data, 0x2A + Sketches.Shift(n.Data), out pts);
                return pts.Count > 1 && pts[0] >= 0 ? "kSketchEllipticalArcObject" : "kSketchEllipseObject";
            }
            if (n.Type.Key != BSpline) return n.TypeName;
            Spline sp = ReadSpline(n);
            return sp != null && sp.Kind == 6 ? "kSketchControlPointSplineObject" : sp != null && sp.Kind == 5 ? "kSketchFixedSplineObject" : "kSketchSplineObject";
        }

        // An image's turn, in radians: the file keeps no angle, but its frame turns with it. 8FEC335F lists the frame's lines, then the
        // image and its first point; the first line runs along the image's width.
        public static double ImageRotation(NodeTable t, Node image)
        {
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Type.Key != 0x8FEC335F || n.Data == null) continue;
                Node first = null;
                bool mine = false;
                for (int p = 0x16; p + 4 <= n.Data.Length; p++)
                {
                    if (n.Data[p + 3] != 0x80) continue;
                    Node x = t.At(Sketches.XRef(n.Data, p));
                    if (x == null || x.Type == null) continue;
                    if (x.Index == image.Index) mine = true;
                    if (first == null && x.TypeName == "Line2D") first = x;
                }
                if (!mine || first == null) continue;
                double[] g = Sketches.Geometry2D(t, first.Index);
                return g != null && g.Length >= 4 ? Math.Atan2(g[3] - g[1], g[2] - g[0]) : 0;
            }
            return 0;
        }

        // Reuses the last table's ASM curves: a sketch report asks for them once per curve.
        static NodeTable asmOf;
        static Dictionary<int, List<SabRecord>> asmCache;
        public static string Describe(NodeTable t, Node n)
        {
            if (asmOf != t) { asmOf = t; asmCache = AsmBlocks(t); }
            int s = Sketches.Shift(n.Data);
            string text = Text(t, n, n.Type.Key == Text2D ? TextSketch(t, t.At(Sketches.XRef(n.Data, 0x22 + s))) : n.Type.Key == Image ? Sketches.XRef(n.Data, 0x22 + s) : SketchOfCurve(n), asmCache);
            if (n.Type.Key == Image)
            {
                double angle = ImageRotation(t, n);
                return Math.Abs(angle) > 1e-9 ? text + " rotated " + Tok(angle) + " rad" : text;
            }
            if (n.Type.Key != Spline2D) return text;
            List<int> pts;
            Sketches.ReadList(n.Data, 0x2A + Sketches.Shift(n.Data), out pts);
            return pts.Count > 1 ? text + " from " + Xy(t, pts[0]) + " to " + Xy(t, pts[1]) : text;
        }

        public static string ProjectedDetail(NodeTable t, Node n)
        {
            StringBuilder b = new StringBuilder();
            Dictionary<int, List<SabRecord>> asm = AsmBlocks(t);
            string reason;
            Spline sp = Projected(t, n, SketchOfCurve(n), asm, out reason);
            if (sp == null) return reason + "\n";
            List<int> pts;
            int p = Sketches.ReadList(n.Data, 0x2A + Sketches.Shift(n.Data), out pts);
            SabRecord c = asm[(int)U32(n.Data, p + 16)][0];
            int i = c.Fields.FindIndex(f => (f as string) == "nubs");
            int after = i + 4 + 2 * (int)Num(c.Fields[i + 3]) + 3 * (sp.Poles.Length / 2);
            b.Append("before nubs:");
            for (int k = 0; k < i; k++) b.Append(" " + (c.Fields[k] ?? "null"));
            b.Append("\nafter the poles:");
            for (int k = after; k < Math.Min(after + 14, c.Fields.Count); k++) b.Append(" " + (c.Fields[k] ?? "null"));
            b.Append("\n");
            double[] first = Eval(sp, sp.Knots[sp.Degree]), last = Eval(sp, sp.Knots[sp.Knots.Length - sp.Degree - 1]);
            b.AppendFormat("curve ends (cm) {0} {1}  {2} {3}\n", first[0], first[1], last[0], last[1]);
            foreach (int q in pts)
            {
                double[] g = Sketches.Geometry2D(t, q);
                if (g == null) continue;
                b.AppendFormat("point #{0:X} {1} {2}  distance {3:G4}  to first end {4:G4}  to last end {5:G4}\n", q, g[0], g[1], Distance(sp, g[0], g[1]),
                    Math.Sqrt((g[0] - first[0]) * (g[0] - first[0]) + (g[1] - first[1]) * (g[1] - first[1])), Math.Sqrt((g[0] - last[0]) * (g[0] - last[0]) + (g[1] - last[1]) * (g[1] - last[1])));
            }
            return b.ToString();
        }

        public static string Report(NodeTable t)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<int, List<SabRecord>> asm = AsmBlocks(t);
            Dictionary<int, string> names = Sketches.BrowserNames(t);
            foreach (Node sk in t.Nodes)
            {
                if (sk.Type == null || sk.Type.Key != Sketches.Sketch2D) continue;
                List<string> curves = OfSketch(t, sk.Index, asm);
                if (curves.Count == 0) continue;
                string name;
                names.TryGetValue(sk.Index, out name);
                sb.AppendFormat("  {0}:\n", name ?? "sketch #" + sk.Index.ToString("X"));
                foreach (string c in curves) sb.AppendFormat("    {0}\n", c);
                foreach (Node im in t.Nodes)
                {
                    if (im.Type == null || im.Type.Key != Image || im.Data == null || Sketches.XRef(im.Data, 0x22 + Sketches.Shift(im.Data)) != sk.Index) continue;
                    double angle = ImageRotation(t, im);
                    if (Math.Abs(angle) > 1e-9) sb.AppendFormat("    (the image is rotated {0} rad)\n", Tok(angle));
                }
            }
            return sb.ToString();
        }

        // Curves against a truth .json: "curves" per sketch, matched by words, numbers within 1e-4.
        static readonly System.Text.RegularExpressions.Regex TruthSketch = new System.Text.RegularExpressions.Regex(
            "\\{\"name\": \"(?<name>[^\"]*)\", \"entities\": \\d+, \"dimensions\": \\d+, \"constraints\": \\d+, \"curves\": \"(?<curves>[^\"]*)\"\\}");

        public static List<string> Compare(NodeTable t, string truthJson, out int compared)
        {
            compared = 0;
            List<string> bad = new List<string>();
            Dictionary<int, List<SabRecord>> asm = AsmBlocks(t);
            Dictionary<int, string> names = Sketches.BrowserNames(t);
            Dictionary<string, int> sketches = new Dictionary<string, int>();
            foreach (KeyValuePair<int, string> kv in names)
            {
                Node n = t.At(kv.Key);
                if (n != null && n.Type != null && n.Type.Key == Sketches.Sketch2D) sketches[kv.Value] = kv.Key;
            }
            foreach (System.Text.RegularExpressions.Match m in TruthSketch.Matches(truthJson))
            {
                int sk;
                if (!sketches.TryGetValue(m.Groups["name"].Value, out sk)) { bad.Add(m.Groups["name"].Value + ": no such sketch"); continue; }
                List<string> left = OfSketch(t, sk, asm);
                foreach (string raw in m.Groups["curves"].Value.Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries))
                {
                    // Rotation is always 0 in the samples (the API refused to set it); image links have no API property.
                    string want = System.Text.RegularExpressions.Regex.Replace(raw, " rotation .*$", "");
                    compared++;
                    int hit = left.FindIndex(g => Same(g, want));
                    if (hit >= 0) left.RemoveAt(hit);
                    else bad.Add(m.Groups["name"].Value + ": Inventor has " + Short(want) + (left.Count > 0 ? "; decoded " + Short(left[0]) : ""));
                }
                foreach (string extra in left) bad.Add(m.Groups["name"].Value + ": decoded " + Short(extra) + ", not in Inventor's list");
            }
            return bad;
        }

        static string Short(string s) { return s.Length > 320 ? s.Substring(0, 320) + " ..." : s; }

        static bool Same(string a, string b)
        {
            string[] x = a.Split(' '), y = b.Split(' ');
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++)
            {
                double u, v;
                bool nu = double.TryParse(x[i], NumberStyles.Float, CultureInfo.InvariantCulture, out u), nv = double.TryParse(y[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
                if (nu != nv) return false;
                if (nu ? Math.Abs(u - v) > 1e-4 : x[i] != y[i]) return false;
            }
            return true;
        }
    }
}
