// Byte-level diff (Myers O(ND)) of decompressed segments, with hunk reports that
// decode changed values as little-endian doubles.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InvRe
{
    public class Hunk
    {
        public int A, ALen, B, BLen;
    }

    public static class Differ
    {
        // Edit script as hunks (offsets into a and b). Gives up after maxD edits.
        public static List<Hunk> Diff(byte[] a, byte[] b, int maxD, out bool tooDifferent)
        {
            tooDifferent = false;
            List<Hunk> hunks = new List<Hunk>();
            int pre = 0;
            while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
            int suf = 0;
            while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
            int n = a.Length - pre - suf, m = b.Length - pre - suf;
            if (n == 0 && m == 0) return hunks;
            if (n == 0 || m == 0)
            {
                hunks.Add(new Hunk { A = pre, ALen = n, B = pre, BLen = m });
                return hunks;
            }

            // Forward pass; keep a snapshot of V for each d to backtrack the path.
            int off = maxD + 1;
            int[] v = new int[2 * maxD + 3];
            List<int[]> trace = new List<int[]>();
            int found = -1;
            for (int d = 0; d <= maxD && found < 0; d++)
            {
                for (int k = -d; k <= d; k += 2)
                {
                    int x = (k == -d || (k != d && v[off + k - 1] < v[off + k + 1])) ? v[off + k + 1] : v[off + k - 1] + 1;
                    int y = x - k;
                    while (x < n && y < m && a[pre + x] == b[pre + y]) { x++; y++; }
                    v[off + k] = x;
                    if (x >= n && y >= m) { found = d; break; }
                }
                int[] snap = new int[2 * d + 1];
                Array.Copy(v, off - d, snap, 0, 2 * d + 1);
                trace.Add(snap);
            }
            if (found < 0)
            {
                tooDifferent = true;
                hunks.Add(new Hunk { A = pre, ALen = n, B = pre, BLen = m });
                return hunks;
            }

            // Backtrack: collect matching runs {aStart, bStart, len}.
            List<int[]> matches = new List<int[]>();
            int cx = n, cy = m;
            for (int d = found; d > 0; d--)
            {
                int[] vp = trace[d - 1];
                int k = cx - cy;
                bool down = k == -d || (k != d && vp[k - 1 + d - 1] < vp[k + 1 + d - 1]);
                int prevK = down ? k + 1 : k - 1;
                int prevX = vp[prevK + d - 1];
                int prevY = prevX - prevK;
                int midX = down ? prevX : prevX + 1;
                if (cx > midX) matches.Add(new int[] { midX, midX - k, cx - midX });
                cx = prevX;
                cy = prevY;
            }
            if (cx > 0) matches.Add(new int[] { 0, 0, cx });
            matches.Reverse();

            int pa = 0, pb = 0;
            foreach (int[] mt in matches)
            {
                if (mt[0] > pa || mt[1] > pb) hunks.Add(new Hunk { A = pre + pa, ALen = mt[0] - pa, B = pre + pb, BLen = mt[1] - pb });
                pa = mt[0] + mt[2];
                pb = mt[1] + mt[2];
            }
            if (pa < n || pb < m) hunks.Add(new Hunk { A = pre + pa, ALen = n - pa, B = pre + pb, BLen = m - pb });
            return hunks;
        }

        // Merge hunks separated by fewer than minGap unchanged bytes (byte diffs fragment on chance matches).
        public static List<Hunk> Coalesce(List<Hunk> hunks, int minGap)
        {
            List<Hunk> r = new List<Hunk>();
            foreach (Hunk h in hunks)
            {
                if (r.Count > 0)
                {
                    Hunk last = r[r.Count - 1];
                    if (h.A - (last.A + last.ALen) < minGap)
                    {
                        last.ALen = h.A + h.ALen - last.A;
                        last.BLen = h.B + h.BLen - last.B;
                        continue;
                    }
                }
                r.Add(new Hunk { A = h.A, ALen = h.ALen, B = h.B, BLen = h.BLen });
            }
            return r;
        }

        public static int ChangedBytes(List<Hunk> hunks)
        {
            int t = 0;
            foreach (Hunk h in hunks) t += Math.Max(h.ALen, h.BLen);
            return t;
        }

        static bool Plausible(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            double a = Math.Abs(v);
            return a == 0 || (a >= 1e-6 && a <= 1e6);
        }

        public static string Report(byte[] a, byte[] b, List<Hunk> hunks, int maxHunks)
        {
            StringBuilder sb = new StringBuilder();
            int shown = 0;
            foreach (Hunk h in hunks)
            {
                if (shown++ >= maxHunks) { sb.AppendFormat("  ... {0} more hunks\n", hunks.Count - maxHunks); break; }
                sb.AppendFormat("  @@ A 0x{0:X} -{1}   B 0x{2:X} +{3}\n", h.A, h.ALen, h.B, h.BLen);
                sb.AppendFormat("     before: {0}\n", Text.Hex(a, h.A - 8, Math.Min(8, h.A)));
                sb.AppendFormat("     A: {0}{1}\n", Text.Hex(a, h.A, Math.Min(h.ALen, 48)), h.ALen > 48 ? " ..." : "");
                sb.AppendFormat("     B: {0}{1}\n", Text.Hex(b, h.B, Math.Min(h.BLen, 48)), h.BLen > 48 ? " ..." : "");
                // Doubles overlapping the change, read at the same relative position in A and B.
                for (int p = h.A - 7; p < h.A + Math.Max(h.ALen, 1); p++)
                {
                    int q = p - h.A + h.B;
                    if (p < 0 || q < 0 || p + 8 > a.Length || q + 8 > b.Length) continue;
                    double va = BitConverter.ToDouble(a, p), vb = BitConverter.ToDouble(b, q);
                    if (va != vb && Plausible(va) && Plausible(vb))
                        sb.AppendFormat(CultureInfo.InvariantCulture, "     f64 A@0x{0:X}: {1:R} -> {2:R}\n", p, va, vb);
                }
            }
            return sb.ToString();
        }
    }
}
