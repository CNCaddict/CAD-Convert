// Node-level diff of two segments: aligns nodes by type (LCS), then byte-diffs matched nodes
// after renumbering B's references to A's numbering, so inserted nodes don't make every later node differ.
using System;
using System.Collections.Generic;
using System.Text;

namespace InvRe
{
    public static class NodeDiff
    {
        public static string Report(NodeTable a, NodeTable b, bool full, int maxHunks)
        {
            StringBuilder sb = new StringBuilder();
            if (a.Error != null) sb.AppendFormat("  A: {0}\n", a.Error);
            if (b.Error != null) sb.AppendFormat("  B: {0}\n", b.Error);
            int[] bToA = Align(a.Nodes, b.Nodes);
            int[] aToB = new int[a.Nodes.Count];
            for (int i = 0; i < aToB.Length; i++) aToB[i] = -1;
            for (int j = 0; j < bToA.Length; j++) if (bToA[j] >= 0) aToB[bToA[j]] = j;

            for (int i = 0; i < a.Nodes.Count; i++)
                if (aToB[i] < 0)
                {
                    sb.AppendFormat("  - A{0}\n", a.Describe(i));
                    if (full) sb.Append(Indent(a.View(i)));
                }
            for (int j = 0; j < b.Nodes.Count; j++)
            {
                Node nb = b.Nodes[j];
                if (bToA[j] < 0)
                {
                    sb.AppendFormat("  + B{0}\n", b.Describe(j));
                    if (full) sb.Append(Indent(b.View(j)));
                    continue;
                }
                Node na = a.Nodes[bToA[j]];
                if (na.Data == null || nb.Data == null) continue;
                byte[] mapped = Renumber(b, nb.Data, bToA);
                if (Same(na.Data, mapped)) continue;
                bool tooDifferent;
                List<Hunk> hunks = Differ.Coalesce(Differ.Diff(na.Data, mapped, 2000, out tooDifferent), 4);
                sb.AppendFormat("  ~ A{0}  =  B#{1:X}  ({2} bytes changed{3})\n", a.Describe(na.Index), j,
                    Differ.ChangedBytes(hunks), tooDifferent ? ", too different" : "");
                if (full) { sb.Append(Indent(a.View(na.Index))); sb.Append(Indent(b.View(j))); }
                else sb.Append(Indent(Differ.Report(na.Data, mapped, hunks, maxHunks)));
            }
            return sb.ToString();
        }

        static string Indent(string s)
        {
            return "    " + s.TrimEnd('\n').Replace("\n", "\n    ") + "\n";
        }

        static bool Same(byte[] x, byte[] y)
        {
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        // Copy of B data with each reference to a matched node rewritten to A's index.
        static byte[] Renumber(NodeTable b, byte[] d, int[] bToA)
        {
            byte[] r = (byte[])d.Clone();
            for (int i = 0; i + 4 <= d.Length; i++)
            {
                int target = b.RefAt(d, i);
                if (target < 0 || bToA[target] < 0) continue;
                byte[] v = BitConverter.GetBytes((uint)(bToA[target] + 1) | 0x80000000);
                Array.Copy(v, 0, r, i, 4);
                i += 3;
            }
            return r;
        }

        // Longest common subsequence of node types (with the node's name when it has one).
        static int[] Align(List<Node> a, List<Node> b)
        {
            int n = a.Count, m = b.Count;
            int[] bToA = new int[m];
            for (int j = 0; j < m; j++) bToA[j] = -1;
            if ((long)n * m > 50000000L) throw new Exception("segments too large to align");
            string[] ka = new string[n], kb = new string[m];
            for (int i = 0; i < n; i++) ka[i] = Key(a[i]);
            for (int j = 0; j < m; j++) kb[j] = Key(b[j]);
            int[,] L = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    L[i, j] = ka[i] == kb[j] ? L[i + 1, j + 1] + 1 : Math.Max(L[i + 1, j], L[i, j + 1]);
            for (int i = 0, j = 0; i < n && j < m; )
            {
                if (ka[i] == kb[j]) { bToA[j] = i; i++; j++; }
                else if (L[i + 1, j] >= L[i, j + 1]) i++;
                else j++;
            }
            return bToA;
        }

        static string Key(Node n)
        {
            return n.Type == null ? "-" : n.Type.Key.ToString("X8");
        }
    }
}
