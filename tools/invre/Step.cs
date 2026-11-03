// Writes a part's final solid (the PmBRep segment's ASM data) as a STEP file, without Inventor.
//
// ASM and STEP describe a solid the same way, so this is mostly a translation, record for record:
//   body 3 lump; lump 3 next, 4 shell; shell 3 next, 5 face; face 3 next, 4 loop, 7 surface, 8 sense (T: reversed);
//   loop 3 next, 4 coedge; coedge 3 next, 6 edge, 7 sense; edge 3 start vertex, 5 end vertex, 8 curve, 9 sense.
//   plane-surface: origin, normal, u direction. cone-surface: centre, axis, major axis, ratio, ..., +9 sine, +10 cosine;
//     radius grows along the axis by sine/cosine; a negative cosine is a surface facing in.
//   sphere-surface: centre, radius (negative: facing in), u direction, pole.
//   torus-surface: centre, axis, major, minor (negative: facing in), u direction.
//   spline-surface, intcurve-curve: F/T reversed, "{", name, then somewhere "nubs" or "nurbs":
//     surfaces deg u, deg v, form u, form v, pole form u, pole form v, knots u, knots v, then {knot, mult} u and v, poles
//     (u fastest), with a weight after each pole for nurbs. Curves deg, form, knots, {knot, mult}, poles. The end knots'
//     multiplicity is the degree, one less than STEP wants. "ref" n instead of data points at the n-th "{" in the file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InvRe
{
    public class StepWriter
    {
        readonly StringBuilder body = new StringBuilder();
        int next = 1;
        List<SabRecord> recs;
        readonly Dictionary<SabRecord, int> vertices = new Dictionary<SabRecord, int>(), edges = new Dictionary<SabRecord, int>();
        readonly List<KeyValuePair<SabRecord, int>> subtypes = new List<KeyValuePair<SabRecord, int>>();
        public int Faces, Edges, Vertices, Solids, Sheets;
        public Dictionary<string, int> NotCarried = new Dictionary<string, int>();

        const double Mm = 10;

        static string F(double v)
        {
            string s = v.ToString("R", CultureInfo.InvariantCulture);
            if (s.Contains("E")) return v.ToString("E15", CultureInfo.InvariantCulture).Replace("E+", "E+").Replace("E-", "E-");
            return s.Contains(".") ? s : s + ".";
        }

        int Add(string text)
        {
            int id = next++;
            body.Append('#').Append(id).Append('=').Append(text).Append(";\n");
            return id;
        }

        SabRecord At(SabRef r) { return r != null && r.Target >= 0 && r.Target < recs.Count ? recs[r.Target] : null; }

        static double D(object o) { return o is double ? (double)o : o is int ? (int)o : double.NaN; }

        void Lost(string what)
        {
            int c;
            NotCarried.TryGetValue(what, out c);
            NotCarried[what] = c + 1;
        }

        int Point(double[] p) { return Add("CARTESIAN_POINT('',(" + F(p[0] * Mm) + "," + F(p[1] * Mm) + "," + F(p[2] * Mm) + "))"); }

        static double[] Unit(double[] v)
        {
            double n = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return n == 0 ? v : new[] { v[0] / n, v[1] / n, v[2] / n };
        }

        int Dir(double[] v)
        {
            double[] u = Unit(v);
            return Add("DIRECTION('',(" + F(u[0]) + "," + F(u[1]) + "," + F(u[2]) + "))");
        }

        static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

        // A right-handed frame: z, and x squared up to it (some files' u directions aren't quite perpendicular).
        int Frame(double[] origin, double[] z, double[] x)
        {
            double[] uz = Unit(z), ux = x == null ? null : Unit(x);
            if (ux == null || Math.Abs(Dot(ux, uz)) > 0.999)
                ux = Math.Abs(uz[0]) < 0.9 ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 };
            double k = Dot(ux, uz);
            ux = Unit(new[] { ux[0] - k * uz[0], ux[1] - k * uz[1], ux[2] - k * uz[2] });
            int o = Point(origin), a = Dir(uz), b = Dir(ux);
            return Add("AXIS2_PLACEMENT_3D('',#" + o + ",#" + a + ",#" + b + ")");
        }

        // ---------------------------------------------------------------- splines

        // A record's "{ ... }" block, following "ref" to the block it names. Returns false if there is none.
        bool Block(SabRecord r, out SabRecord holder, out int start, out int end)
        {
            holder = r;
            end = -1;
            start = r.Fields.FindIndex(x => x as string == "{");
            for (int hop = 0; hop < 8; hop++)
            {
                if (start < 0 || start + 2 >= holder.Fields.Count) return false;
                if (holder.Fields[start + 1] as string != "ref") break;
                if (!(holder.Fields[start + 2] is int)) return false;
                int n = (int)holder.Fields[start + 2];
                if (n < 0 || n >= subtypes.Count) return false;
                holder = subtypes[n].Key;
                start = subtypes[n].Value;
            }
            int depth = 0;
            for (int i = start; i < holder.Fields.Count; i++)
            {
                string s = holder.Fields[i] as string;
                if (s == "{") depth++;
                else if (s == "}" && --depth == 0) { end = i; return true; }
            }
            return false;
        }

        // The indexes of "nubs" and "nurbs" directly in a block, not in the blocks nested in it.
        static List<int> Candidates(List<object> f, int start, int end)
        {
            List<int> r = new List<int>();
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                string s = f[i] as string;
                if (s == "{") depth++;
                else if (s == "}") depth--;
                else if (depth == 1 && (s == "nubs" || s == "nurbs")) r.Add(i);
            }
            return r;
        }

        // {knot, multiplicity} pairs: ascending knots, whole multiplicities no bigger than degree + 1.
        static bool ReadKnots(List<object> f, ref int p, int end, int count, int degree, List<double> knots, List<int> mults, out int sum)
        {
            sum = 0;
            for (int i = 0; i < count; i++, p += 2)
            {
                if (p + 1 >= end) return false;
                double k = D(f[p]), m = D(f[p + 1]);
                if (double.IsNaN(k) || double.IsNaN(m) || m != Math.Floor(m) || m < 1 || m > degree + 1) return false;
                if (i > 0 && k <= knots[i - 1]) return false;
                knots.Add(k);
                mults.Add((int)m);
                sum += (int)m;
            }
            return true;
        }

        static bool Numbers(List<object> f, int p, int count, int end)
        {
            if (count <= 0 || p + count > end) return false;
            for (int i = 0; i < count; i++) if (double.IsNaN(D(f[p + i]))) return false;
            return true;
        }

        // ASM's knots and multiplicities to STEP's: one more at each end (or, where an end isn't clamped, one more knot).
        static void FullKnots(List<double> knots, List<int> mults, int degree)
        {
            if (mults[0] >= degree) mults[0] = degree + 1;
            else { knots.Insert(0, knots[0] - 1); mults.Insert(0, 1); }
            if (mults[mults.Count - 1] >= degree) mults[mults.Count - 1] = degree + 1;
            else { knots.Add(knots[knots.Count - 1] + 1); mults.Add(1); }
        }

        static string Ints(List<int> v) { return "(" + string.Join(",", v.ConvertAll(x => x.ToString()).ToArray()) + ")"; }
        static string Doubles(List<double> v) { return "(" + string.Join(",", v.ConvertAll(F).ToArray()) + ")"; }

        // A closed spline starting somewhere else on itself, exactly: a knot inserted until the curve passes through a pole
        // there, then the two pieces swapped. Needed when an edge runs across where the spline's ends meet: STEP's spline has
        // real ends, so a reader given the edge's two vertices goes the long way round. poles hold x, y, z, weight; returns
        // poles as x, y, z(, weight) and replaces the knots.
        static List<object> Reroot(List<double[]> poles, List<double> knots, List<int> mults, int degree, double start, bool rational)
        {
            double high = knots[knots.Count - 1], period = high - knots[0];
            // Landing on a knot, it has to be that knot exactly.
            foreach (double k in knots) if (Math.Abs(k - start) < period * 1e-9) start = k;
            List<double> u = new List<double>();
            for (int i = 0; i < knots.Count; i++)
                for (int m = 0; m < (i == 0 || i == knots.Count - 1 ? degree + 1 : mults[i]); m++) u.Add(knots[i]);
            int have = 0;
            foreach (double k in u) if (k == start) have++;
            for (int r = have; r < degree; r++)
            {
                int k = u.FindLastIndex(x => x <= start);
                List<double[]> q = new List<double[]>();
                for (int i = 0; i <= poles.Count; i++)
                {
                    if (i <= k - degree) q.Add(poles[i]);
                    else if (i > k) q.Add(poles[i - 1]);
                    else
                    {
                        double span = u[i + degree] - u[i], a = span <= 0 ? 0 : (start - u[i]) / span;
                        q.Add(new[] { a * poles[i][0] + (1 - a) * poles[i - 1][0], a * poles[i][1] + (1 - a) * poles[i - 1][1], a * poles[i][2] + (1 - a) * poles[i - 1][2], a * poles[i][3] + (1 - a) * poles[i - 1][3] });
                    }
                }
                u.Insert(k + 1, start);
                poles = q;
            }
            int through = u.FindLastIndex(x => x <= start) - degree;
            List<double[]> turned = new List<double[]>();
            for (int i = through; i < poles.Count; i++) turned.Add(poles[i]);
            for (int i = 1; i <= through; i++) turned.Add(poles[i]);
            List<double> newKnots = new List<double> { start };
            List<int> newMults = new List<int> { degree };
            for (int i = 1; i < knots.Count - 1; i++)
                if (knots[i] > start) { newKnots.Add(knots[i]); newMults.Add(mults[i]); }
            newKnots.Add(high);
            newMults.Add(degree);
            for (int i = 1; i < knots.Count - 1; i++)
                if (knots[i] < start) { newKnots.Add(knots[i] + period); newMults.Add(mults[i]); }
            newKnots.Add(start + period);
            newMults.Add(degree);
            knots.Clear();
            knots.AddRange(newKnots);
            mults.Clear();
            mults.AddRange(newMults);
            List<object> f = new List<object>();
            foreach (double[] p in turned)
            {
                f.Add(p[0] / p[3]);
                f.Add(p[1] / p[3]);
                f.Add(p[2] / p[3]);
                if (rational) f.Add(p[3]);
            }
            return f;
        }

        // A closed edge has one vertex, and STEP's spline has to start there or the edge collapses to nothing. Which way the
        // edge's parameter runs along the spline is settled by where it lands. Returns null if it already starts there.
        List<object> StartAtVertex(SabRecord edge, List<double[]> weighted, List<double> knots, List<int> mults, int degree, bool rational, int size)
        {
            if (edge.Ref(3) == null || edge.Ref(5) == null || edge.Ref(3).Target != edge.Ref(5).Target) return null;
            double[] v = Sab.VertexPosition(recs, At(edge.Ref(3)));
            double t0 = D(edge.Fields[4]), low = knots[0], high = knots[knots.Count - 1], period = high - low;
            if (v == null || double.IsNaN(t0)) return null;
            double[] first = weighted[0];
            double near = 5e-3;
            if (Far(new[] { first[0] / first[3], first[1] / first[3], first[2] / first[3] }, v) < near * near) return null;
            List<object> best = null;
            List<double> bestKnots = null;
            List<int> bestMults = null;
            double closest = near * near;
            foreach (double sign in new double[] { 1, -1 })
            {
                double root = sign * t0;
                while (root >= high) root -= period;
                while (root < low) root += period;
                if (root - low < period * 1e-9) continue;
                List<double> k = new List<double>(knots);
                List<int> m = new List<int>(mults);
                List<object> turned = Reroot(weighted, k, m, degree, root, rational);
                double far = Far(new[] { (double)turned[0], (double)turned[1], (double)turned[2] }, v);
                if (far < closest) { closest = far; best = turned; bestKnots = k; bestMults = m; }
            }
            if (best == null) return null;
            knots.Clear();
            knots.AddRange(bestKnots);
            mults.Clear();
            mults.AddRange(bestMults);
            return best;
        }

        // Where a closed spline should start for this edge, or NaN if the edge doesn't run across its ends.
        double RootFor(SabRecord edge, SabRecord curve, List<object> f, int p, int poles, int size, List<double> knots)
        {
            double t0 = D(edge.Fields[4]), t1 = D(edge.Fields[6]), low = knots[0], high = knots[knots.Count - 1], period = high - low, slack = period * 1e-6;
            if (double.IsNaN(t0) || double.IsNaN(t1) || t1 - t0 >= period - slack) return double.NaN;
            if ((t0 >= low - slack && t1 <= high + slack) || (-t1 >= low - slack && -t0 <= high + slack)) return double.NaN;
            int last = p + (poles - 1) * size;
            double gap = Math.Pow(D(f[p]) - D(f[last]), 2) + Math.Pow(D(f[p + 1]) - D(f[last + 1]), 2) + Math.Pow(D(f[p + 2]) - D(f[last + 2]), 2);
            if (gap > 1e-10) return double.NaN;
            bool backwards = (edge.Fields[9] is bool && (bool)edge.Fields[9]) != (curve.Fields[3] is bool && (bool)curve.Fields[3]);
            double middle = (backwards ? -1 : 1) * (t0 + t1) / 2 + period / 2;
            while (middle > high) middle -= period;
            while (middle < low) middle += period;
            return middle;
        }

        int SplineCurve(SabRecord r, SabRecord edge)
        {
            SabRecord h;
            int start, end;
            if (!Block(r, out h, out start, out end)) return -1;
            List<object> f = h.Fields;
            // The first spline that reads: the curve itself comes before the 2D curves that may follow it.
            foreach (int at in Candidates(f, start, end))
            {
                bool rational = f[at] as string == "nurbs";
                int p = at + 1;
                if (p + 3 >= end) continue;
                int degree = (int)D(f[p++]);
                while (p < end && f[p] is string) p++;
                if (p + 2 >= end) continue;
                int count = (int)D(f[p + 1]);
                p += 2;
                List<double> knots = new List<double>();
                List<int> mults = new List<int>();
                int sum;
                if (degree < 1 || count < 2 || !ReadKnots(f, ref p, end, count, degree, knots, mults, out sum)) continue;
                int poles = sum - degree + 1, size = rational ? 4 : 3;
                if (poles < degree + 1 || !Numbers(f, p, poles * size, end)) continue;
                List<double[]> weighted = new List<double[]>();
                for (int i = 0; i < poles; i++)
                {
                    double w = rational ? D(f[p + i * size + 3]) : 1;
                    weighted.Add(new[] { D(f[p + i * size]) * w, D(f[p + i * size + 1]) * w, D(f[p + i * size + 2]) * w, w });
                }
                List<object> started = StartAtVertex(edge, weighted, knots, mults, degree, rational, size);
                if (started != null) return WriteCurve(started, 0, degree, started.Count / size, size, rational, knots, mults);
                double root = RootFor(edge, r, f, p, poles, size, knots);
                if (!double.IsNaN(root))
                {
                    List<object> turned = Reroot(weighted, knots, mults, degree, root, rational);
                    return WriteCurve(turned, 0, degree, turned.Count / size, size, rational, knots, mults);
                }
                return WriteCurve(f, p, degree, poles, size, rational, knots, mults);
            }
            return -1;
        }

        int WriteCurve(List<object> f, int p, int degree, int poles, int size, bool rational, List<double> knots, List<int> mults)
        {
            List<string> ps = new List<string>();
            List<double> ws = new List<double>();
            for (int i = 0; i < poles; i++, p += size)
            {
                ps.Add("#" + Point(new[] { D(f[p]), D(f[p + 1]), D(f[p + 2]) }));
                if (rational) ws.Add(D(f[p + 3]));
            }
            FullKnots(knots, mults, degree);
            string list = "(" + string.Join(",", ps.ToArray()) + ")";
            if (!rational)
                return Add("B_SPLINE_CURVE_WITH_KNOTS(''," + degree + "," + list + ",.UNSPECIFIED.,.F.,.F.," + Ints(mults) + "," + Doubles(knots) + ",.UNSPECIFIED.)");
            return Add("(BOUNDED_CURVE() B_SPLINE_CURVE(" + degree + "," + list + ",.UNSPECIFIED.,.F.,.F.) B_SPLINE_CURVE_WITH_KNOTS(" + Ints(mults) + "," + Doubles(knots)
                + ",.UNSPECIFIED.) CURVE() GEOMETRIC_REPRESENTATION_ITEM() RATIONAL_B_SPLINE_CURVE(" + Doubles(ws) + ") REPRESENTATION_ITEM(''))");
        }

        int SplineSurface(SabRecord r)
        {
            SabRecord h;
            int start, end;
            if (!Block(r, out h, out start, out end)) return -1;
            List<object> f = h.Fields;
            // The last spline that reads as a surface: a blend or sweep lists its definition first, its approximation last.
            List<int> candidates = Candidates(f, start, end);
            for (int c = candidates.Count - 1; c >= 0; c--)
            {
                int at = candidates[c];
                bool rational = f[at] as string == "nurbs";
                int p = at + 1;
                if (p + 8 >= end) continue;
                int du = (int)D(f[p]), dv = (int)D(f[p + 1]);
                p += 2;
                while (p < end && f[p] is string) p++;
                if (!Numbers(f, p, 6, end)) continue;
                int ku = (int)D(f[p + 4]), kv = (int)D(f[p + 5]);
                p += 6;
                List<double> knotsU = new List<double>(), knotsV = new List<double>();
                List<int> multU = new List<int>(), multV = new List<int>();
                int su, sv;
                if (du < 1 || dv < 1 || ku < 2 || kv < 2 || !ReadKnots(f, ref p, end, ku, du, knotsU, multU, out su) || !ReadKnots(f, ref p, end, kv, dv, knotsV, multV, out sv)) continue;
                int nu = su - du + 1, nv = sv - dv + 1, size = rational ? 4 : 3;
                if (nu < du + 1 || nv < dv + 1 || !Numbers(f, p, nu * nv * size, end)) continue;
                return WriteSurface(f, p, du, dv, nu, nv, size, rational, knotsU, knotsV, multU, multV);
            }
            return -1;
        }

        int WriteSurface(List<object> f, int p, int du, int dv, int nu, int nv, int size, bool rational, List<double> knotsU, List<double> knotsV, List<int> multU, List<int> multV)
        {
            string[][] grid = new string[nu][];
            double[][] weights = new double[nu][];
            for (int i = 0; i < nu; i++) { grid[i] = new string[nv]; weights[i] = new double[nv]; }
            for (int v = 0; v < nv; v++)
                for (int u = 0; u < nu; u++, p += size)
                {
                    grid[u][v] = "#" + Point(new[] { D(f[p]), D(f[p + 1]), D(f[p + 2]) });
                    if (rational) weights[u][v] = D(f[p + 3]);
                }
            FullKnots(knotsU, multU, du);
            FullKnots(knotsV, multV, dv);
            List<string> rows = new List<string>(), wrows = new List<string>();
            for (int u = 0; u < nu; u++)
            {
                rows.Add("(" + string.Join(",", grid[u]) + ")");
                wrows.Add(Doubles(new List<double>(weights[u])));
            }
            string list = "(" + string.Join(",", rows.ToArray()) + ")";
            string knots = Ints(multU) + "," + Ints(multV) + "," + Doubles(knotsU) + "," + Doubles(knotsV) + ",.UNSPECIFIED.";
            if (!rational)
                return Add("B_SPLINE_SURFACE_WITH_KNOTS(''," + du + "," + dv + "," + list + ",.UNSPECIFIED.,.F.,.F.,.F.," + knots + ")");
            return Add("(BOUNDED_SURFACE() B_SPLINE_SURFACE(" + du + "," + dv + "," + list + ",.UNSPECIFIED.,.F.,.F.,.F.) B_SPLINE_SURFACE_WITH_KNOTS(" + knots
                + ") GEOMETRIC_REPRESENTATION_ITEM() RATIONAL_B_SPLINE_SURFACE((" + string.Join(",", wrows.ToArray()) + ")) REPRESENTATION_ITEM('') SURFACE())");
        }

        // ---------------------------------------------------------------- curves, surfaces

        int Curve(SabRecord e, out bool flipped)
        {
            flipped = false;
            SabRecord c = At(e.Ref(8));
            double[] a = Sab.VertexPosition(recs, At(e.Ref(3))), b = Sab.VertexPosition(recs, At(e.Ref(5)));
            if (c != null && c.Type == "straight-curve" && c.Fields.Count > 4 && c.Fields[3] is double[] && c.Fields[4] is double[])
            {
                int p = Point((double[])c.Fields[3]), d = Dir((double[])c.Fields[4]);
                int v = Add("VECTOR('',#" + d + ",1.)");
                return Add("LINE('',#" + p + ",#" + v + ")");
            }
            if (c != null && c.Type == "ellipse-curve" && c.Fields.Count > 6 && c.Fields[5] is double[] && c.Fields[6] is double)
            {
                double[] m = (double[])c.Fields[5];
                double major = Math.Sqrt(Dot(m, m)) * Mm, ratio = (double)c.Fields[6];
                int frame = Frame((double[])c.Fields[3], (double[])c.Fields[4], m);
                if (Math.Abs(ratio - 1) < 1e-9) return Add("CIRCLE('',#" + frame + "," + F(major) + ")");
                return Add("ELLIPSE('',#" + frame + "," + F(major) + "," + F(major * ratio) + ")");
            }
            if (c != null && c.Type == "intcurve-curve")
            {
                int s = SplineCurve(c, e);
                if (s >= 0)
                {
                    flipped = c.Fields.Count > 3 && c.Fields[3] is bool && (bool)c.Fields[3];
                    return s;
                }
                Lost("intcurve without spline data");
            }
            else Lost(c == null ? "edge without a curve" : c.Type);
            // No curve to go on: a straight line between its ends keeps the topology, and the checks will show the gap.
            if (a == null || b == null) return -1;
            int from = Point(a);
            double[] dv = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            if (Dot(dv, dv) < 1e-18) return -1;
            int vec = Add("VECTOR('',#" + Dir(dv) + ",1.)");
            return Add("LINE('',#" + from + ",#" + vec + ")");
        }

        // Points along a face's outline: every end, along each arc, and a spline's poles, which are near enough for this.
        List<double[]> Outline(SabRecord face, bool skipWholeCircles = false)
        {
            List<double[]> points = new List<double[]>();
            HashSet<SabRecord> seen = new HashSet<SabRecord>();
            for (SabRecord loop = At(face.Ref(4)); loop != null && loop.Type == "loop" && seen.Add(loop); loop = At(loop.Ref(3)))
            {
                SabRecord first = At(loop.Ref(4));
                if (skipWholeCircles && first != null && At(first.Ref(3)) == first) continue;
                for (SabRecord c = first; c != null && c.Type == "coedge"; )
                {
                    SabRecord e = At(c.Ref(6));
                    if (e != null)
                    {
                        double[] a = Sab.VertexPosition(recs, At(e.Ref(3))), b = Sab.VertexPosition(recs, At(e.Ref(5)));
                        if (a != null) points.Add(a);
                        if (b != null) points.Add(b);
                        AlongCurve(e, a, points);
                    }
                    c = At(c.Ref(3));
                    if (c == first) break;
                }
            }
            return points;
        }

        void AlongCurve(SabRecord e, double[] start, List<double[]> points)
        {
            SabRecord c = At(e.Ref(8));
            if (c == null) return;
            if (c.Type == "ellipse-curve" && c.Fields.Count > 6 && c.Fields[5] is double[] && c.Fields[6] is double && start != null)
            {
                double[] o = (double[])c.Fields[3], n = Unit((double[])c.Fields[4]), m = (double[])c.Fields[5];
                double ratio = (double)c.Fields[6], t0 = D(e.Fields[4]), t1 = D(e.Fields[6]);
                double[] minor = Cross(n, m);
                Func<double, double[]> at = t => new[] { o[0] + m[0] * Math.Cos(t) + minor[0] * ratio * Math.Sin(t), o[1] + m[1] * Math.Cos(t) + minor[1] * ratio * Math.Sin(t), o[2] + m[2] * Math.Cos(t) + minor[2] * ratio * Math.Sin(t) };
                // A reversed edge's parameters run along the curve backwards. Which way round is settled by where it starts.
                double[] p = at(t0), q = at(-t0);
                double sign = Far(p, start) <= Far(q, start) ? 1 : -1;
                for (int i = 1; i < 16; i++) points.Add(at(sign * (t0 + (t1 - t0) * i / 16)));
            }
            else if (c.Type == "intcurve-curve")
            {
                SabRecord h;
                int from, to;
                if (!Block(c, out h, out from, out to)) return;
                foreach (int at in Candidates(h.Fields, from, to))
                {
                    int size = h.Fields[at] as string == "nurbs" ? 4 : 3, p = at + 1;
                    int degree = (int)D(h.Fields[p++]);
                    while (p < to && h.Fields[p] is string) p++;
                    if (p + 2 >= to) continue;
                    int count = (int)D(h.Fields[p + 1]);
                    p += 2;
                    List<double> knots = new List<double>();
                    List<int> mults = new List<int>();
                    int sum;
                    if (degree < 1 || count < 2 || !ReadKnots(h.Fields, ref p, to, count, degree, knots, mults, out sum)) continue;
                    int poles = sum - degree + 1;
                    if (poles < degree + 1 || !Numbers(h.Fields, p, poles * size, to)) continue;
                    for (int i = 0; i < poles; i++, p += size) points.Add(new[] { D(h.Fields[p]), D(h.Fields[p + 1]), D(h.Fields[p + 2]) });
                    return;
                }
            }
        }

        static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
        static double Far(double[] a, double[] b) { return Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2) + Math.Pow(a[2] - b[2], 2); }

        // Where a round surface's angle should start: readers misplace a face that runs across that line, by one full turn, or
        // split it. In the widest gap the face leaves; or, if it goes all the way round, at a corner of a loop with several edges,
        // so that the line runs through a vertex. Null if neither applies. This moves no geometry.
        double[] Seam(SabRecord face, double[] centre, double[] axis)
        {
            double[] z = Unit(axis);
            double[] x = Unit(Cross(z, Math.Abs(z[0]) < 0.9 ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 })), y = Cross(z, x);
            // First a gap in the whole outline; then, on a band, a gap between the windows cut in it.
            foreach (bool skip in new[] { false, true })
            {
                List<double> angles = new List<double>();
                foreach (double[] p in Outline(face, skip))
                {
                    double[] d = { p[0] - centre[0], p[1] - centre[1], p[2] - centre[2] };
                    double px = Dot(d, x), py = Dot(d, y);
                    if (px * px + py * py > 1e-18) angles.Add(Math.Atan2(py, px));
                }
                if (angles.Count == 0) continue;
                angles.Sort();
                double widest = angles[0] + 2 * Math.PI - angles[angles.Count - 1], middle = angles[angles.Count - 1] + widest / 2;
                for (int i = 1; i < angles.Count; i++)
                    if (angles[i] - angles[i - 1] > widest) { widest = angles[i] - angles[i - 1]; middle = angles[i - 1] + widest / 2; }
                if (widest > 10 * Math.PI / 180) return new[] { x[0] * Math.Cos(middle) + y[0] * Math.Sin(middle), x[1] * Math.Cos(middle) + y[1] * Math.Sin(middle), x[2] * Math.Cos(middle) + y[2] * Math.Sin(middle) };
            }
            HashSet<SabRecord> seen = new HashSet<SabRecord>();
            for (SabRecord loop = At(face.Ref(4)); loop != null && loop.Type == "loop" && seen.Add(loop); loop = At(loop.Ref(3)))
            {
                SabRecord first = At(loop.Ref(4));
                if (first == null || At(first.Ref(3)) == first) continue;
                SabRecord e = At(first.Ref(6));
                double[] p = e == null ? null : Sab.VertexPosition(recs, At(e.Ref(3)));
                if (p == null) continue;
                double[] d = { p[0] - centre[0], p[1] - centre[1], p[2] - centre[2] };
                double k = Dot(d, z);
                double[] flat = { d[0] - k * z[0], d[1] - k * z[1], d[2] - k * z[2] };
                if (Dot(flat, flat) > 1e-18) return flat;
            }
            return null;
        }

        // The axis of a whole circle bounding a face. On a sphere that's where the poles should go: in the cap the circle cuts
        // off, not on the face, where readers lose track of a band running over them. Any axis gives the same sphere.
        double[] CircleAxis(SabRecord face)
        {
            HashSet<SabRecord> seen = new HashSet<SabRecord>();
            for (SabRecord loop = At(face.Ref(4)); loop != null && loop.Type == "loop" && seen.Add(loop); loop = At(loop.Ref(3)))
            {
                SabRecord first = At(loop.Ref(4));
                if (first == null || At(first.Ref(3)) != first) continue;
                SabRecord e = At(first.Ref(6)), c = e == null ? null : At(e.Ref(8));
                if (c != null && c.Type == "ellipse-curve" && c.Fields.Count > 6 && c.Fields[4] is double[] && c.Fields[6] is double && Math.Abs((double)c.Fields[6] - 1) < 1e-9) return (double[])c.Fields[4];
            }
            return null;
        }

        // STEP has no elliptical cone, but it's a rational spline exactly: an ellipse in u, a straight line in v. Each point goes
        // up the axis while the ellipse scales around it, by the slope k: radius per height, measured on the major axis.
        int EllipticalCone(SabRecord face, double[] centre, double[] axis, double[] major, double ratio, double k)
        {
            double[] n = Unit(axis);
            double[] minor = Cross(n, major);
            minor = new[] { minor[0] * ratio, minor[1] * ratio, minor[2] * ratio };
            double radius = Math.Sqrt(Dot(major, major));
            // The u line starts in the gap the face leaves, measured in the ellipse's own angle.
            double start = 0, low = double.MaxValue, high = double.MinValue;
            List<double> angles = new List<double>();
            foreach (double[] p in Outline(face))
            {
                double[] d = { p[0] - centre[0], p[1] - centre[1], p[2] - centre[2] };
                double h = Dot(d, n);
                low = Math.Min(low, h);
                high = Math.Max(high, h);
                double scale = 1 + h * k / radius;
                if (Math.Abs(scale) > 1e-9) angles.Add(Math.Atan2(Dot(d, Unit(minor)) / (radius * ratio * scale), Dot(d, Unit(major)) / (radius * scale)));
            }
            if (angles.Count == 0) { Lost("elliptical cone with no outline"); return -1; }
            angles.Sort();
            double widest = angles[0] + 2 * Math.PI - angles[angles.Count - 1];
            start = angles[angles.Count - 1] + widest / 2;
            for (int i = 1; i < angles.Count; i++)
                if (angles[i] - angles[i - 1] > widest) { widest = angles[i] - angles[i - 1]; start = angles[i - 1] + widest / 2; }
            // A little past the outline each way, but not through the tip.
            double margin = Math.Max((high - low) * 0.05, 1e-3);
            low -= margin;
            high += margin;
            if (Math.Abs(k) > 1e-12)
            {
                double tip = -radius / k;
                if (k < 0) high = Math.Min(high, tip * 0.999 + low * 0.001);
                else low = Math.Max(low, tip * 0.999 + high * 0.001);
            }
            // Nine poles round a whole ellipse: the even ones on it, the odd ones at the corners of the box around it.
            List<object> f = new List<object>();
            foreach (double h in new[] { low, high })
            {
                double scale = 1 + h * k / radius;
                for (int i = 0; i <= 8; i++)
                {
                    double t = start + i * Math.PI / 4, far = i % 2 == 0 ? 1 : Math.Sqrt(2), c = far * Math.Cos(t) * scale, s = far * Math.Sin(t) * scale;
                    for (int a = 0; a < 3; a++) f.Add(centre[a] + n[a] * h + major[a] * c + minor[a] * s);
                    f.Add(i % 2 == 0 ? 1.0 : Math.Sqrt(0.5));
                }
            }
            return WriteSurface(f, 0, 2, 1, 9, 2, 4, true, new List<double> { 0, 1, 2, 3, 4 }, new List<double> { 0, 1 }, new List<int> { 2, 2, 2, 2, 2 }, new List<int> { 1, 1 });
        }

        // The surface, and whether its own normal points the opposite way from ASM's idea of it.
        int Surface(SabRecord face, out bool inverted)
        {
            inverted = false;
            SabRecord s = At(face.Ref(7));
            if (s == null) { Lost("face without a surface"); return -1; }
            switch (s.Type)
            {
                case "plane-surface":
                    return Add("PLANE('',#" + Frame((double[])s.Fields[3], (double[])s.Fields[4], s.Fields[5] as double[]) + ")");
                case "cone-surface":
                    {
                        double[] c = (double[])s.Fields[3], axis = (double[])s.Fields[4], m = (double[])s.Fields[5];
                        double ratio = D(s.Fields[6]), sine = D(s.Fields[9]), cosine = D(s.Fields[10]), radius = Math.Sqrt(Dot(m, m)) * Mm;
                        inverted = cosine < 0;
                        if (Math.Abs(ratio - 1) > 1e-9)
                        {
                            if (Math.Abs(sine) > 1e-12) return EllipticalCone(face, c, axis, m, ratio, sine / cosine);
                            // An elliptical cylinder: an ellipse swept along the axis.
                            int e = Add("ELLIPSE('',#" + Frame(c, axis, m) + "," + F(radius) + "," + F(radius * ratio) + ")");
                            int v = Add("VECTOR('',#" + Dir(axis) + ",1.)");
                            return Add("SURFACE_OF_LINEAR_EXTRUSION('',#" + e + ",#" + v + ")");
                        }
                        double[] start = Seam(face, c, axis) ?? m;
                        if (Math.Abs(sine) < 1e-12) return Add("CYLINDRICAL_SURFACE('',#" + Frame(c, axis, start) + "," + F(radius) + ")");
                        double k = sine / cosine;
                        double[] up = k < 0 ? new[] { -axis[0], -axis[1], -axis[2] } : axis;
                        return Add("CONICAL_SURFACE('',#" + Frame(c, up, start) + "," + F(radius) + "," + F(Math.Atan(Math.Abs(k))) + ")");
                    }
                case "sphere-surface":
                    {
                        double r = D(s.Fields[4]);
                        inverted = r < 0;
                        double[] u = s.Fields.Count > 5 ? s.Fields[5] as double[] : null, pole = s.Fields.Count > 6 ? s.Fields[6] as double[] : null;
                        double[] toward = CircleAxis(face) ?? pole ?? new double[] { 0, 0, 1 };
                        return Add("SPHERICAL_SURFACE('',#" + Frame((double[])s.Fields[3], toward, Seam(face, (double[])s.Fields[3], toward) ?? u) + "," + F(Math.Abs(r) * Mm) + ")");
                    }
                case "torus-surface":
                    {
                        double major = D(s.Fields[5]), minor = D(s.Fields[6]);
                        inverted = minor < 0;
                        double[] u = s.Fields.Count > 7 ? s.Fields[7] as double[] : null;
                        string kind = Math.Abs(minor) > Math.Abs(major) ? "DEGENERATE_TOROIDAL_SURFACE" : "TOROIDAL_SURFACE";
                        string extra = kind == "TOROIDAL_SURFACE" ? "" : ",.T.";
                        return Add(kind + "('',#" + Frame((double[])s.Fields[3], (double[])s.Fields[4], u) + "," + F(Math.Abs(major) * Mm) + "," + F(Math.Abs(minor) * Mm) + extra + ")");
                    }
                case "spline-surface":
                    {
                        int id = SplineSurface(s);
                        if (id < 0) { Lost("spline surface without data"); return -1; }
                        inverted = s.Fields.Count > 3 && s.Fields[3] is bool && (bool)s.Fields[3];
                        return id;
                    }
            }
            Lost(s.Type);
            return -1;
        }

        // ---------------------------------------------------------------- topology

        int Vertex(SabRecord v)
        {
            int id;
            if (vertices.TryGetValue(v, out id)) return id;
            double[] p = Sab.VertexPosition(recs, v);
            if (p == null) return -1;
            id = Add("VERTEX_POINT('',#" + Point(p) + ")");
            vertices[v] = id;
            Vertices++;
            return id;
        }

        int Edge(SabRecord e)
        {
            int id;
            if (edges.TryGetValue(e, out id)) return id;
            SabRecord sv = At(e.Ref(3)), ev = At(e.Ref(5));
            if (sv == null || ev == null) return -1;
            int a = Vertex(sv), b = Vertex(ev);
            bool flipped;
            int curve = Curve(e, out flipped);
            if (a < 0 || b < 0 || curve < 0) { edges[e] = -1; return -1; }
            bool reversed = e.Fields.Count > 9 && e.Fields[9] is bool && (bool)e.Fields[9];
            id = Add("EDGE_CURVE('',#" + a + ",#" + b + ",#" + curve + "," + (reversed != flipped ? ".F." : ".T.") + ")");
            edges[e] = id;
            Edges++;
            return id;
        }

        bool Degenerate(SabRecord e) { return At(e.Ref(8)) == null && e.Ref(3) != null && e.Ref(5) != null && e.Ref(3).Target == e.Ref(5).Target; }

        int Face(SabRecord f)
        {
            bool inverted;
            int surface = Surface(f, out inverted);
            if (surface < 0) return -1;
            List<string> bounds = new List<string>();
            HashSet<SabRecord> seen = new HashSet<SabRecord>();
            for (SabRecord loop = At(f.Ref(4)); loop != null && loop.Type == "loop" && seen.Add(loop); loop = At(loop.Ref(3)))
            {
                List<string> oriented = new List<string>();
                SabRecord first = At(loop.Ref(4));
                for (SabRecord c = first; c != null && c.Type == "coedge"; )
                {
                    SabRecord edge = At(c.Ref(6));
                    // A cone's tip or a sphere's pole is an edge with no curve: STEP has no such thing, and readers put it back.
                    if (edge == null || !Degenerate(edge))
                    {
                        int eid = edge == null ? -1 : Edge(edge);
                        if (eid < 0) { Lost("edge not written"); return -1; }
                        bool rev = c.Fields.Count > 7 && c.Fields[7] is bool && (bool)c.Fields[7];
                        oriented.Add("#" + Add("ORIENTED_EDGE('',*,*,#" + eid + "," + (rev ? ".F." : ".T.") + ")"));
                    }
                    c = At(c.Ref(3));
                    if (c == first) break;
                }
                if (oriented.Count == 0) continue;
                int l = Add("EDGE_LOOP('',(" + string.Join(",", oriented.ToArray()) + "))");
                bounds.Add("#" + Add("FACE_BOUND('',#" + l + ",.T.)"));
            }
            bool reversed = f.Fields.Count > 8 && f.Fields[8] is bool && (bool)f.Fields[8];
            Faces++;
            return Add("ADVANCED_FACE('',(" + string.Join(",", bounds.ToArray()) + "),#" + surface + "," + (reversed != inverted ? ".F." : ".T.") + ")");
        }

        List<string> ShellFaces(SabRecord shell)
        {
            List<string> faces = new List<string>();
            HashSet<SabRecord> seen = new HashSet<SabRecord>();
            for (SabRecord f = At(shell.Ref(5)); f != null && f.Type == "face" && seen.Add(f); f = At(f.Ref(3)))
            {
                int id = Face(f);
                if (id >= 0) faces.Add("#" + id);
            }
            return faces;
        }

        // Every edge used by an even number of coedges (two, where nothing touches): a closed solid, not a sheet.
        bool Closed(SabRecord shell)
        {
            Dictionary<SabRecord, int> uses = new Dictionary<SabRecord, int>();
            HashSet<SabRecord> seen = new HashSet<SabRecord>();
            for (SabRecord f = At(shell.Ref(5)); f != null && f.Type == "face" && seen.Add(f); f = At(f.Ref(3)))
                for (SabRecord loop = At(f.Ref(4)); loop != null && loop.Type == "loop" && seen.Add(loop); loop = At(loop.Ref(3)))
                {
                    SabRecord first = At(loop.Ref(4));
                    for (SabRecord c = first; c != null && c.Type == "coedge"; )
                    {
                        SabRecord e = At(c.Ref(6));
                        if (e != null && !Degenerate(e)) { int n; uses.TryGetValue(e, out n); uses[e] = n + 1; }
                        c = At(c.Ref(3));
                        if (c == first) break;
                    }
                }
            // No edges at all: a whole sphere or torus, closed by itself.
            if (Environment.GetEnvironmentVariable("INVRE_DEBUG_CLOSED") == "1")
                foreach (KeyValuePair<SabRecord, int> kv in uses)
                    if (kv.Value != 2) Console.Error.WriteLine("edge used {0} times: {1}", kv.Value, string.Join(" ", kv.Key.Fields.ConvertAll(x => x == null ? "null" : x.ToString()).ToArray()));
            // Four is closed too: holes patterned touching each other meet along a line that four faces share
            // (the generated pattern-rect parts with "adjust to model").
            foreach (int n in uses.Values) if (n % 2 != 0) return false;
            return true;
        }

        // Numbers each "{" in the file, for "ref".
        void IndexSubtypes(List<SabRecord> records)
        {
            foreach (SabRecord r in records)
                for (int i = 0; i < r.Fields.Count; i++)
                    if (r.Fields[i] as string == "{" && !(i + 1 < r.Fields.Count && r.Fields[i + 1] as string == "ref")) subtypes.Add(new KeyValuePair<SabRecord, int>(r, i));
        }

        // Returns the STEP text for a part's final solid; null if it has none.
        public string Write(byte[] brep, string name)
        {
            List<int> items = new List<int>();
            int at = 0;
            while ((at = Sab.Find(brep, at)) >= 0)
            {
                string error;
                recs = Sab.Read(brep, at, out error);
                at++;
                subtypes.Clear();
                IndexSubtypes(recs);
                foreach (SabRecord b in recs)
                {
                    if (b.Type != "body" || b.Old) continue;
                    HashSet<SabRecord> seen = new HashSet<SabRecord>();
                    for (SabRecord lump = At(b.Ref(3)); lump != null && lump.Type == "lump" && seen.Add(lump); lump = At(lump.Ref(3)))
                    {
                        List<string> closed = new List<string>(), open = new List<string>();
                        for (SabRecord shell = At(lump.Ref(4)); shell != null && shell.Type == "shell" && seen.Add(shell); shell = At(shell.Ref(3)))
                        {
                            List<string> faces = ShellFaces(shell);
                            if (faces.Count == 0) continue;
                            string list = "(" + string.Join(",", faces.ToArray()) + ")";
                            if (Closed(shell)) closed.Add("#" + Add("CLOSED_SHELL(''," + list + ")"));
                            else open.Add("#" + Add("OPEN_SHELL(''," + list + ")"));
                        }
                        if (closed.Count == 1)
                        {
                            items.Add(Add("MANIFOLD_SOLID_BREP(''," + closed[0] + ")"));
                            Solids++;
                        }
                        else if (closed.Count > 1)
                        {
                            // The first shell is the outside, the rest are voids.
                            List<string> voids = new List<string>();
                            for (int i = 1; i < closed.Count; i++) voids.Add("#" + Add("ORIENTED_CLOSED_SHELL('',*," + closed[i] + ",.F.)"));
                            items.Add(Add("BREP_WITH_VOIDS(''," + closed[0] + ",(" + string.Join(",", voids.ToArray()) + "))"));
                            Solids++;
                        }
                        if (open.Count > 0)
                        {
                            items.Add(Add("SHELL_BASED_SURFACE_MODEL('',(" + string.Join(",", open.ToArray()) + "))"));
                            Sheets++;
                        }
                    }
                }
            }
            if (items.Count == 0) return null;
            return Wrap(items, name);
        }

        string Wrap(List<int> items, string name)
        {
            string q = name.Replace("'", "''");
            int length = Add("(LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.))");
            int angle = Add("(NAMED_UNIT(*) PLANE_ANGLE_UNIT() SI_UNIT($,.RADIAN.))");
            int solid = Add("(NAMED_UNIT(*) SI_UNIT($,.STERADIAN.) SOLID_ANGLE_UNIT())");
            int unc = Add("UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-05),#" + length + ",'distance_accuracy_value','')");
            int ctx = Add("(GEOMETRIC_REPRESENTATION_CONTEXT(3) GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#" + unc + ")) GLOBAL_UNIT_ASSIGNED_CONTEXT((#" + length + ",#" + angle + ",#" + solid + ")) REPRESENTATION_CONTEXT('',''))");
            int origin = Frame(new double[3], new double[] { 0, 0, 1 }, new double[] { 1, 0, 0 });
            List<string> list = items.ConvertAll(i => "#" + i);
            list.Add("#" + origin);
            int rep = Add("ADVANCED_BREP_SHAPE_REPRESENTATION('" + q + "',(" + string.Join(",", list.ToArray()) + "),#" + ctx + ")");
            int app = Add("APPLICATION_CONTEXT('automotive_design')");
            Add("APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,#" + app + ")");
            int pc = Add("PRODUCT_CONTEXT('',#" + app + ",'mechanical')");
            int product = Add("PRODUCT('" + q + "','" + q + "','',(#" + pc + "))");
            int form = Add("PRODUCT_DEFINITION_FORMATION('','',#" + product + ")");
            int dc = Add("PRODUCT_DEFINITION_CONTEXT('part definition',#" + app + ",'design')");
            int pd = Add("PRODUCT_DEFINITION('design','',#" + form + ",#" + dc + ")");
            int ds = Add("PRODUCT_DEFINITION_SHAPE('','',#" + pd + ")");
            Add("SHAPE_DEFINITION_REPRESENTATION(#" + ds + ",#" + rep + ")");
            return "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('CAD Convert: a part''s final solid'),'2;1');\nFILE_NAME('" + q + ".step','" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss") + "',(''),(''),'CAD Convert','CAD Convert','');\nFILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\nENDSEC;\nDATA;\n"
                + body + "ENDSEC;\nEND-ISO-10303-21;\n";
        }
    }
}
