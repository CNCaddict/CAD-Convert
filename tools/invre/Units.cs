// Decodes unit nodes.
//
// Unit (F8A779FD): u32 0, u16 id,
//   kind-3 list of child refs   factors multiplied (numerator)
//   kind-3 list of child refs   factors divided (denominator); the element kind is absent when a list is empty
//   u8 ?                        1 on parameter units, 0 on units derived inside formulas
//   u32 child ref               named unit the factors spell out (e.g. N for kg*m/s^2), or 0
// Factor: u32 0, u16 id, f64 scale (decimal prefix: 0.001 on metre = mm), f64 (always 1 so far).
// The factor's node type names the base unit, or the document's length or angle unit.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InvRe
{
    public static class Units
    {
        public const uint Unit = 0xF8A779FD;

        class Base
        {
            public string Symbol, Quantity;
            public double ToDatabase;   // one unit (scale 1) in database units: cm, kg, s, rad
            public Base(string symbol, string quantity, double toDatabase) { Symbol = symbol; Quantity = quantity; ToDatabase = toDatabase; }
        }

        // Confirmed by experiment (corpus/generated/unit).
        static readonly Dictionary<uint, Base> Bases = new Dictionary<uint, Base> {
            { 0xF8A779F5, new Base("m", "length", 100) },
            { 0xF8A779F6, new Base("in", "length", 2.54) },
            { 0xF8A779F7, new Base("ft", "length", 30.48) },
            { 0x5DFE5E70, new Base("mil", "length", 0.00254) },
            { 0x5C30CDFB, new Base("micron", "length", 1e-4) },
            { 0xF8A779F1, new Base("g", "mass", 0.001) },
            { 0xF8A779F3, new Base("lbmass", "mass", 0.45359237) },
            { 0x5F9D0025, new Base("s", "time", 1) },
            { 0x5C30CDF6, new Base("deg", "angle", Math.PI / 180) },
            { 0x5C30CDF2, new Base("rad", "angle", 1) },
            { 0x5F9D0023, new Base("ul", "none", 1) },
            { 0x5F9D0022, new Base("ul", "none", 1) },   // seen in real parts; Inventor shows ul
            { 0x40AFEBA3, new Base("N", "force", 100) } };

        // Factors meaning "the document's length / angle unit"; model parameters (d0, d1...) use these.
        // Parts saved by older Inventor versions use the second pair.
        static readonly Dictionary<uint, string> Placeholders = new Dictionary<uint, string> {
            { 0x624120BC, "length" }, { 0xCEA6CA2D, "length" }, { 0x5C30CDF0, "angle" }, { 0x2F6A0C3F, "angle" } };

        // Document units, from the App segment: a 5F9D0021 node cross-refs the current unit system (EBEE69D0
        // "Custom"), whose kind-3 list holds one factor per quantity, in no fixed order.
        static Dictionary<string, Base> document = new Dictionary<string, Base>();

        public static string UseDocumentUnits(NodeTable app)
        {
            document.Clear();
            foreach (Node n in app.Nodes)
            {
                if (n.Type == null || n.Type.Key != 0x5F9D0021 || n.Data == null) continue;
                Node system = app.At((int)(U32(n.Data, 6) & 0x7FFFFFFF) - 1);
                if (system == null || system.Data == null) return null;
                int p = 6;
                List<string> found = new List<string>();
                foreach (int slot in List3(system.Data, ref p))
                {
                    Node f = app.At(slot);
                    Base b;
                    if (f == null || f.Type == null || !Bases.TryGetValue(f.Type.Key, out b) || document.ContainsKey(b.Quantity)) continue;
                    double toDatabase;
                    string s = Factor(app, slot, out toDatabase);
                    document[b.Quantity] = new Base(s, b.Quantity, toDatabase);
                    found.Add(b.Quantity + " " + s);
                }
                return string.Join(", ", found.ToArray());
            }
            return null;
        }

        static readonly Dictionary<double, string> Prefixes = new Dictionary<double, string> {
            { 1e-9, "n" }, { 1e-6, "u" }, { 1e-3, "m" }, { 1e-2, "c" }, { 1e-1, "d" }, { 1, "" }, { 1e3, "k" }, { 1e6, "M" } };

        static uint U32(byte[] d, int p) { return BitConverter.ToUInt32(d, p); }

        public static bool IsUnit(Node n) { return n != null && n.Type != null && n.Type.Key == Unit && n.Data != null; }

        // Kind-3 list of child refs at p; returns node indexes and moves p past the list.
        static List<int> List3(byte[] d, ref int p)
        {
            List<int> r = new List<int>();
            if (U32(d, p) != 0x30000003) throw new Exception("not a kind-3 list at +" + p.ToString("X"));
            int count = (int)U32(d, p + 4);
            p += 8;
            if (count == 0) return r;
            p += 4;
            for (int i = 0; i < count; i++, p += 4) r.Add((int)U32(d, p) - 1);
            return r;
        }

        // Numerator and denominator factors, and the named unit they spell out (-1 if none).
        static void Parse(Node n, out List<int> num, out List<int> den, out int named)
        {
            byte[] d = n.Data;
            int p = 6;
            num = List3(d, ref p);
            den = List3(d, ref p);
            named = (int)U32(d, p + 1) - 1;
        }

        static string Factor(NodeTable t, int index, out double toDatabase)
        {
            toDatabase = double.NaN;
            Node f = t.At(index);
            if (f == null || f.Data == null || f.Data.Length < 22) return "?#" + index.ToString("X");
            double scale = BitConverter.ToDouble(f.Data, 6);
            Base b;
            string quantity;
            if (Placeholders.TryGetValue(f.Type.Key, out quantity) && scale == 1 && document.TryGetValue(quantity, out b))
            {
                toDatabase = b.ToDatabase;
                return b.Symbol;
            }
            if (!Bases.TryGetValue(f.Type.Key, out b)) return "?" + f.TypeName + (scale == 1 ? "" : "*" + Num(scale));
            toDatabase = b.ToDatabase * scale;
            string prefix;
            return Prefixes.TryGetValue(scale, out prefix) ? prefix + b.Symbol : Num(scale) + "*" + b.Symbol;
        }

        static string Product(NodeTable t, List<int> factors, ref double toDatabase, bool divide)
        {
            // Repeated factors print as a power: mm*mm -> mm^2.
            List<string> names = new List<string>();
            Dictionary<string, int> powers = new Dictionary<string, int>();
            foreach (int i in factors)
            {
                double f;
                string s = Factor(t, i, out f);
                toDatabase = divide ? toDatabase / f : toDatabase * f;
                if (!powers.ContainsKey(s)) { names.Add(s); powers[s] = 0; }
                powers[s]++;
            }
            List<string> parts = new List<string>();
            foreach (string s in names) parts.Add(powers[s] == 1 ? s : s + "^" + powers[s]);
            return string.Join("*", parts.ToArray());
        }

        // Inventor-style unit text ("mm", "mm^2", "mm/s", "N"); toDatabase = one of this unit in database units.
        public static string Name(NodeTable t, int index, out double toDatabase)
        {
            toDatabase = double.NaN;
            Node n = t.At(index);
            if (!IsUnit(n)) return null;
            List<int> num, den;
            int named;
            try { Parse(n, out num, out den, out named); }
            catch (Exception) { return "?unit"; }
            toDatabase = 1;
            string top = Product(t, num, ref toDatabase, false);
            string bottom = Product(t, den, ref toDatabase, true);
            if (named >= 0)
            {
                double ignored;
                string alias = Name(t, named, out ignored);
                if (alias != null) return alias;
            }
            if (top == "" && bottom == "") return "ul";
            if (bottom == "") return top;
            return (top == "" ? "1" : top) + "/" + (den.Count > 1 && bottom.IndexOf('*') >= 0 ? "(" + bottom + ")" : bottom);
        }

        public static string Name(NodeTable t, int index)
        {
            double ignored;
            return Name(t, index, out ignored);
        }

        // A database value shown in a unit: 0.1 (cm) with mm -> "1 mm". Like Inventor, unitless values say "ul".
        public static string Value(NodeTable t, double value, int unit)
        {
            double f;
            string name = Name(t, unit, out f);
            if (name == null || double.IsNaN(f) || f == 0) return Num(value);
            return Num(value / f) + " " + name;
        }

        public static string Num(double v)
        {
            // 12 significant digits hide unit-conversion noise (0.2 / 0.1 -> 2.0000000000000004).
            return double.Parse(v.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
        }

        // Every unit node in a segment, for exploring.
        public static string Report(NodeTable t)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Node n in t.Nodes)
            {
                if (!IsUnit(n)) continue;
                double f;
                string name = Name(t, n.Index, out f);
                sb.AppendFormat("  #{0,-4:X} {1,-14} = {2} database units\n", n.Index, name, Num(f));
            }
            return sb.ToString();
        }
    }
}
