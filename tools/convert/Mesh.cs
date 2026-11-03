// What the viewer draws: the view files export_view.py writes, the meshes in them, and how far one mesh lies from another.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CadConvert
{
    public class Mesh
    {
        public float[] V = new float[0], N = new float[0];
        public int[] I = new int[0];
        public byte[] C;
        public float[] Stray;   // At each point, how far its face's mesh may be from the true surface.
        public List<float[]> Lines = new List<float[]>();
        public uint FaceList, LineList;
        public bool Uploaded;

        public static Mesh Parse(byte[] d)
        {
            int at = 0;
            Func<int> next = () => { int v = BitConverter.ToInt32(d, at); at += 4; return v; };
            int faces = next();
            int start = at, points = 0, triangles = 0;
            for (int f = 0; f < faces; f++)
            {
                int np = next(), nt = next();
                at += 4;
                points += np;
                triangles += nt;
                at += np * 12 + nt * 12;
            }
            Mesh m = new Mesh { V = new float[points * 3], N = new float[points * 3], I = new int[triangles * 3], Stray = new float[points] };
            at = start;
            int vAt = 0, iAt = 0;
            for (int f = 0; f < faces; f++)
            {
                int np = next(), nt = next();
                float off = BitConverter.ToSingle(d, at);
                at += 4;
                for (int i = vAt; i < vAt + np; i++) m.Stray[i] = off;
                Buffer.BlockCopy(d, at, m.V, vAt * 3 * 4, np * 12);
                at += np * 12;
                Buffer.BlockCopy(d, at, m.I, iAt * 4, nt * 12);
                at += nt * 12;
                for (int i = iAt; i < iAt + nt * 3; i++) m.I[i] += vAt;
                m.FaceNormals(iAt, nt * 3);
                vAt += np;
                iAt += nt * 3;
            }
            m.Normalize();
            int lines = next();
            for (int l = 0; l < lines; l++)
            {
                int np = next();
                float[] p = new float[np * 3];
                Buffer.BlockCopy(d, at, p, 0, np * 12);
                at += np * 12;
                m.Lines.Add(p);
            }
            return m;
        }

        // Each face's points get the sum of its triangles' normals: smooth within a face, a crease between faces.
        void FaceNormals(int from, int count)
        {
            for (int i = from; i < from + count; i += 3)
            {
                int a = I[i] * 3, b = I[i + 1] * 3, c = I[i + 2] * 3;
                float ux = V[b] - V[a], uy = V[b + 1] - V[a + 1], uz = V[b + 2] - V[a + 2];
                float vx = V[c] - V[a], vy = V[c + 1] - V[a + 1], vz = V[c + 2] - V[a + 2];
                float x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
                foreach (int k in new[] { a, b, c }) { N[k] += x; N[k + 1] += y; N[k + 2] += z; }
            }
        }

        void Normalize()
        {
            for (int k = 0; k < N.Length; k += 3)
            {
                double l = Math.Sqrt(N[k] * N[k] + N[k + 1] * N[k + 1] + N[k + 2] * N[k + 2]);
                if (l > 0) { N[k] = (float)(N[k] / l); N[k + 1] = (float)(N[k + 1] / l); N[k + 2] = (float)(N[k + 2] / l); }
            }
        }

        public static Mesh Combine(List<Mesh> parts)
        {
            if (parts.Count == 1) return parts[0];
            int nv = 0, ni = 0;
            foreach (Mesh p in parts) { nv += p.V.Length; ni += p.I.Length; }
            Mesh m = new Mesh { V = new float[nv], N = new float[nv], I = new int[ni], Stray = new float[nv / 3] };
            int va = 0, ia = 0;
            foreach (Mesh p in parts)
            {
                Array.Copy(p.V, 0, m.V, va, p.V.Length);
                Array.Copy(p.N, 0, m.N, va, p.N.Length);
                Array.Copy(p.Stray, 0, m.Stray, va / 3, p.Stray.Length);
                for (int i = 0; i < p.I.Length; i++) m.I[ia + i] = p.I[i] + va / 3;
                va += p.V.Length;
                ia += p.I.Length;
                m.Lines.AddRange(p.Lines);
            }
            return m;
        }

        // The same surface with long edges cut up, so that colours can change across what was one big flat triangle. Cuts run
        // across the triangle, parallel to its shortest side: halving the longest edge instead turns one long thin triangle
        // into the square of its length in pieces, and a cylinder is nothing but long thin triangles.
        public Mesh Subdivide(double longest, int limit)
        {
            List<float> v = new List<float>(V), n = new List<float>(N), off = new List<float>(Stray);
            List<int> done = new List<int>(I.Length * 2);
            Stack<int> todo = new Stack<int>();
            for (int i = I.Length - 3; i >= 0; i -= 3) { todo.Push(I[i + 2]); todo.Push(I[i + 1]); todo.Push(I[i]); }
            double most = longest * longest;
            while (todo.Count > 0)
            {
                int a = todo.Pop(), b = todo.Pop(), c = todo.Pop();
                double ab = Far(v, a, b), bc = Far(v, b, c), ca = Far(v, c, a);
                double top = Math.Max(ab, Math.Max(bc, ca));
                if (!(top > most) || done.Count / 3 + todo.Count / 3 >= limit) { done.Add(a); done.Add(b); done.Add(c); continue; }
                // Turn the triangle so its shortest side is b-c, with a at the point.
                if (ab <= bc && ab <= ca) { int t = a; a = c; c = b; b = t; }
                else if (ca <= ab && ca <= bc) { int t = a; a = b; b = c; c = t; }
                int cuts = Math.Min(64, (int)Math.Ceiling(Math.Sqrt(top / most)));
                int lastB = a, lastC = a;
                for (int i = 1; i <= cuts; i++)
                {
                    int nextB = i == cuts ? b : Between(v, n, off, a, b, (float)i / cuts), nextC = i == cuts ? c : Between(v, n, off, a, c, (float)i / cuts);
                    Push(todo, lastB, nextB, nextC);
                    if (i > 1) Push(todo, lastB, nextC, lastC);
                    lastB = nextB;
                    lastC = nextC;
                }
            }
            Mesh s = new Mesh { V = v.ToArray(), N = n.ToArray(), I = done.ToArray(), Lines = Lines, Stray = off.ToArray() };
            s.Normalize();
            return s;
        }

        static void Push(Stack<int> todo, int a, int b, int c) { todo.Push(c); todo.Push(b); todo.Push(a); }

        static int Between(List<float> v, List<float> n, List<float> off, int a, int b, float t)
        {
            int m = v.Count / 3;
            for (int k = 0; k < 3; k++) v.Add(v[a * 3 + k] * (1 - t) + v[b * 3 + k] * t);
            for (int k = 0; k < 3; k++) n.Add(n[a * 3 + k] * (1 - t) + n[b * 3 + k] * t);
            off.Add(Math.Max(off[a], off[b]));
            return m;
        }

        static double Far(List<float> v, int a, int b)
        {
            double x = v[a * 3] - v[b * 3], y = v[a * 3 + 1] - v[b * 3 + 1], z = v[a * 3 + 2] - v[b * 3 + 2];
            return x * x + y * y + z * z;
        }

        public void Grow(double[] box, float[] matrix)
        {
            Action<float, float, float> add = (x, y, z) =>
            {
                double px = x, py = y, pz = z;
                if (matrix != null)
                {
                    px = matrix[0] * x + matrix[4] * y + matrix[8] * z + matrix[12];
                    py = matrix[1] * x + matrix[5] * y + matrix[9] * z + matrix[13];
                    pz = matrix[2] * x + matrix[6] * y + matrix[10] * z + matrix[14];
                }
                if (px < box[0]) box[0] = px;
                if (py < box[1]) box[1] = py;
                if (pz < box[2]) box[2] = pz;
                if (px > box[3]) box[3] = px;
                if (py > box[4]) box[4] = py;
                if (pz > box[5]) box[5] = pz;
            };
            for (int i = 0; i < V.Length; i += 3) add(V[i], V[i + 1], V[i + 2]);
            foreach (float[] l in Lines) for (int i = 0; i < l.Length; i += 3) add(l[i], l[i + 1], l[i + 2]);
        }

        public static double[] EmptyBox() { return new[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue }; }
    }

    // How far points lie from a mesh: triangles sorted into a grid, searched outwards from the point.
    public class Nearest
    {
        readonly float[] v, stray;
        readonly int[] tri;
        readonly double[] box;
        readonly double cell;
        public readonly double Cap;
        readonly int nx, ny, nz;
        readonly List<int>[] cells;

        public Nearest(Mesh m, double[] sharedBox, double cap)
        {
            v = m.V;
            stray = m.Stray;
            tri = m.I;
            box = sharedBox;
                        double dx = box[3] - box[0], dy = box[4] - box[1], dz = box[5] - box[2];
            double most = Math.Max(dx, Math.Max(dy, dz));
            // Cells by the space the part takes up, so a long thin part still gets a few triangles to a cell.
            cell = Math.Max(Math.Max(most / 256, 1e-3), Math.Pow(Math.Max(dx, most / 256) * Math.Max(dy, most / 256) * Math.Max(dz, most / 256) / (tri.Length / 3 + 1), 1.0 / 3) * 1.5);
            // Looking further than this for every point away from the other mesh takes minutes; "over this far" is enough.
            Cap = Math.Min(cap, 12 * cell);
            nx = (int)(dx / cell) + 1;
            ny = (int)(dy / cell) + 1;
            nz = (int)(dz / cell) + 1;
            cells = new List<int>[nx * ny * nz];
            for (int t = 0; t < tri.Length; t += 3)
            {
                double[] low = { double.MaxValue, double.MaxValue, double.MaxValue }, high = { double.MinValue, double.MinValue, double.MinValue };
                for (int c = 0; c < 3; c++)
                    for (int k = 0; k < 3; k++)
                    {
                        double p = v[tri[t + c] * 3 + k];
                        if (p < low[k]) low[k] = p;
                        if (p > high[k]) high[k] = p;
                    }
                int x0 = X(low[0]), x1 = X(high[0]), y0 = Y(low[1]), y1 = Y(high[1]), z0 = Z(low[2]), z1 = Z(high[2]);
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int at = x + nx * (y + ny * z);
                            if (cells[at] == null) cells[at] = new List<int>(4);
                            cells[at].Add(t);
                        }
            }
        }

        int X(double p) { return Math.Max(0, Math.Min(nx - 1, (int)((p - box[0]) / cell))); }
        int Y(double p) { return Math.Max(0, Math.Min(ny - 1, (int)((p - box[1]) / cell))); }
        int Z(double p) { return Math.Max(0, Math.Min(nz - 1, (int)((p - box[2]) / cell))); }

        public double Distance(double px, double py, double pz)
        {
            int cx = X(px), cy = Y(py), cz = Z(pz);
            double best = Cap * Cap;
            int nearest = -1;
            int rings = (int)(Cap / cell) + 2;
            for (int r = 0; r <= rings; r++)
            {
                double reach = (r - 1) * cell;
                if (reach > 0 && best <= reach * reach) break;
                for (int z = Math.Max(0, cz - r); z <= Math.Min(nz - 1, cz + r); z++)
                    for (int y = Math.Max(0, cy - r); y <= Math.Min(ny - 1, cy + r); y++)
                        for (int x = Math.Max(0, cx - r); x <= Math.Min(nx - 1, cx + r); x++)
                        {
                            if (Math.Abs(x - cx) != r && Math.Abs(y - cy) != r && Math.Abs(z - cz) != r) continue;
                            List<int> list = cells[x + nx * (y + ny * z)];
                            if (list == null) continue;
                            foreach (int t in list)
                            {
                                double d = ToTriangle(px, py, pz, t);
                                if (d < best) { best = d; nearest = t; }
                            }
                        }
            }
            // As far apart as the two meshes can show: what either might stray from its surface doesn't count.
            return Math.Max(0, Math.Sqrt(best) - (nearest >= 0 ? stray[tri[nearest]] : 0));
        }

        // The square of the distance to one triangle, by which of its regions is closest.
        double ToTriangle(double px, double py, double pz, int t)
        {
            int a = tri[t] * 3, b = tri[t + 1] * 3, c = tri[t + 2] * 3;
            double ax = v[a], ay = v[a + 1], az = v[a + 2];
            double abx = v[b] - ax, aby = v[b + 1] - ay, abz = v[b + 2] - az;
            double acx = v[c] - ax, acy = v[c + 1] - ay, acz = v[c + 2] - az;
            double apx = px - ax, apy = py - ay, apz = pz - az;
            double d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
            double u, w;
            if (d1 <= 0 && d2 <= 0) { u = 0; w = 0; }
            else
            {
                double bpx = px - v[b], bpy = py - v[b + 1], bpz = pz - v[b + 2];
                double d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
                double cpx = px - v[c], cpy = py - v[c + 1], cpz = pz - v[c + 2];
                double d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
                double vc = d1 * d4 - d3 * d2, vb = d5 * d2 - d1 * d6, va = d3 * d6 - d5 * d4;
                if (d3 >= 0 && d4 <= d3) { u = 1; w = 0; }
                else if (d6 >= 0 && d5 <= d6) { u = 0; w = 1; }
                else if (vc <= 0 && d1 >= 0 && d3 <= 0) { u = d1 / (d1 - d3); w = 0; }
                else if (vb <= 0 && d2 >= 0 && d6 <= 0) { u = 0; w = d2 / (d2 - d6); }
                else if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); u = 1 - w; }
                else { double s = 1 / (va + vb + vc); u = vb * s; w = vc * s; }
            }
            double x = apx - abx * u - acx * w, y = apy - aby * u - acy * w, z = apz - abz * u - acz * w;
            return x * x + y * y + z * z;
        }
    }

    // Where two meshes part, as the viewer colours it: the first is drawn whole, and of the second only what's away from the first.
    public class Comparison
    {
        public const double FarMm = 0.05;
        public Mesh First, SecondFar;
        public double FirstShare, SecondShare, Worst, Cap;

        public static double[] Together(Mesh a, Mesh b)
        {
            double[] box = Mesh.EmptyBox();
            a.Grow(box, null);
            b.Grow(box, null);
            return box;
        }

        static float[] Distances(Mesh m, Nearest other)
        {
            float[] d = new float[m.V.Length / 3];
            Parallel.For(0, d.Length, i => { d[i] = (float)Math.Max(0, other.Distance(m.V[i * 3], m.V[i * 3 + 1], m.V[i * 3 + 2]) - m.Stray[i]); });
            return d;
        }

        static double FarShare(Mesh m, float[] d, double far)
        {
            double all = 0, away = 0;
            for (int i = 0; i < m.I.Length; i += 3)
            {
                int a = m.I[i] * 3, b = m.I[i + 1] * 3, c = m.I[i + 2] * 3;
                double ux = m.V[b] - m.V[a], uy = m.V[b + 1] - m.V[a + 1], uz = m.V[b + 2] - m.V[a + 2];
                double vx = m.V[c] - m.V[a], vy = m.V[c + 1] - m.V[a + 1], vz = m.V[c + 2] - m.V[a + 2];
                double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
                double area = Math.Sqrt(x * x + y * y + z * z);
                all += area;
                if ((d[m.I[i]] + d[m.I[i + 1]] + d[m.I[i + 2]]) / 3 > far) away += area;
            }
            return all > 0 ? away / all : 0;
        }

        static byte[] Paint(float[] d, double far, double fullAt, float[] same, float[] apart)
        {
            byte[] c = new byte[d.Length * 3];
            for (int i = 0; i < d.Length; i++)
            {
                double t = d[i] <= far ? 0 : 0.55 + 0.45 * Math.Min(1, (d[i] - far) / fullAt);
                for (int k = 0; k < 3; k++) c[i * 3 + k] = (byte)(255 * (same[k] * (1 - t) + apart[k] * t));
            }
            return c;
        }

        public static Comparison Of(Mesh first, Mesh second, float[] same, float[] firstColor, float[] secondColor)
        {
            double[] box = Together(first, second);
            double diagonal = Math.Sqrt(Math.Pow(box[3] - box[0], 2) + Math.Pow(box[4] - box[1], 2) + Math.Pow(box[5] - box[2], 2));
            double cap = Math.Max(2, diagonal / 40);
            double longest = Math.Max(diagonal / 40, 0.2);
            Mesh a = first.Subdivide(longest, 1500000), b = second.Subdivide(longest, 1500000);
            Nearest nb = new Nearest(b, box, cap), na = new Nearest(a, box, cap);
            float[] da = Distances(a, nb), db = Distances(b, na);
            Comparison r = new Comparison { Cap = Math.Min(na.Cap, nb.Cap) };
            r.FirstShare = FarShare(a, da, FarMm);
            r.SecondShare = FarShare(b, db, FarMm);
            foreach (float d in da) if (d > r.Worst) r.Worst = d;
            foreach (float d in db) if (d > r.Worst) r.Worst = d;
            double fullAt = Math.Max(0.5, Math.Min(5, diagonal / 100));
            a.C = Paint(da, FarMm, fullAt, same, firstColor);
            b.C = Paint(db, FarMm, fullAt, same, secondColor);
            r.First = a;
            List<int> keep = new List<int>();
            for (int i = 0; i < b.I.Length; i += 3)
                if (db[b.I[i]] > FarMm || db[b.I[i + 1]] > FarMm || db[b.I[i + 2]] > FarMm) { keep.Add(b.I[i]); keep.Add(b.I[i + 1]); keep.Add(b.I[i + 2]); }
            r.SecondFar = new Mesh { V = b.V, N = b.N, C = b.C, I = keep.ToArray() };
            return r;
        }

        // A step of the history with what that step changed picked out.
        public static Mesh Changed(Mesh now, Mesh before, float[] plain, float[] changed)
        {
            double[] box = Together(now, before);
            double diagonal = Math.Sqrt(Math.Pow(box[3] - box[0], 2) + Math.Pow(box[4] - box[1], 2) + Math.Pow(box[5] - box[2], 2));
            Mesh a = now.Subdivide(Math.Max(diagonal / 60, 0.2), 800000);
            float[] d = Distances(a, new Nearest(before.Subdivide(Math.Max(diagonal / 60, 0.2), 800000), box, Math.Max(2, diagonal / 20)));
            a.C = Paint(d, 0.06, 0.01, plain, changed);
            return a;
        }
    }

    public class ViewFile
    {
        public string Path;
        public Dictionary<string, object> Head;
        long[][] blocks;

        public static ViewFile Open(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] magic = new byte[4];
                fs.Read(magic, 0, 4);
                if (Encoding.ASCII.GetString(magic) != "CCVW") throw new InvalidDataException("not a view file");
                fs.Seek(-8, SeekOrigin.End);
                byte[] end = new byte[8];
                fs.Read(end, 0, 8);
                long at = BitConverter.ToInt64(end, 0);
                byte[] json = new byte[fs.Length - 8 - at];
                fs.Seek(at, SeekOrigin.Begin);
                fs.Read(json, 0, json.Length);
                ViewFile v = new ViewFile { Path = path };
                JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                v.Head = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(json));
                object[] list = Items(v.Head["blocks"]);
                v.blocks = new long[list.Length][];
                for (int i = 0; i < list.Length; i++)
                {
                    object[] pair = Items(list[i]);
                    v.blocks[i] = new[] { Convert.ToInt64(pair[0]), Convert.ToInt64(pair[1]) };
                }
                return v;
            }
        }

        public Mesh Read(int block)
        {
            byte[] packed = new byte[blocks[block][1]];
            using (FileStream fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(blocks[block][0], SeekOrigin.Begin);
                int got = 0;
                while (got < packed.Length) got += fs.Read(packed, got, packed.Length - got);
            }
            using (GZipStream gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress))
            using (MemoryStream all = new MemoryStream())
            {
                gz.CopyTo(all);
                return Mesh.Parse(all.ToArray());
            }
        }

        public static object[] Items(object o)
        {
            if (o is object[]) return (object[])o;
            ArrayList a = o as ArrayList;
            return a == null ? new object[0] : a.ToArray();
        }

        public bool Flag(string key) { object o; return Head.TryGetValue(key, out o) && o is bool && (bool)o; }
        public double? Number(string key) { object o; if (Head.TryGetValue(key, out o) && o != null) return Convert.ToDouble(o); return null; }
    }
}
