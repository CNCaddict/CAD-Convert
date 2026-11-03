// Decodes parameters and their formula trees from a DC segment's nodes.
//
// Parameter (90874D26): ... name {u32 n, UTF-16}, u32 0, u32 unit (child), u32 formula (child), f64 nominal, f64 value
// Expression nodes share {u32 0, u16 id, u32 unit (child)}, then:
//   ParameterValue  f64 value (database units: cm, kg, s, rad), u16 decimals typed (FFFF: none typed);
//                   its unit is the one the number was typed in
//   ParameterRef    u32 cross ref to a Parameter
//   Op + - * / % ^  u32 left (child), u32 right (child)
//   Brackets        u32 operand (child): "( a + b )" as typed
//   ParameterFunction  list {u32 0x30000002, u32 count, u32 ?, u32 kind} of child operands, u16 function id
// Child refs are node index + 1 without bit 31; cross refs have bit 31 set.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InvRe
{
    public static class Params
    {
        const uint Table = 0x90874D63, Parameter = 0x90874D26, Value = 0xF8A77A04, Ref = 0xF8A77A05, Function = 0xF8A77A03;
        const uint Constant = 0x0AA8AF46, Negate = 0xF8A77A0C, Brackets = 0xF8A77A0D;

        static readonly Dictionary<uint, string> Operators = new Dictionary<uint, string> {
            { 0xF8A77A06, "+" }, { 0xF8A77A07, "-" }, { 0xF8A77A08, "*" }, { 0xF8A77A09, "/" },
            { 0xF8A77A0A, "%" }, { 0xF8A77A0B, "^" } };

        // Function ids confirmed by experiment (unit/functions.ipt). 7..9 are probably cosh/sinh/tanh, but
        // Inventor 2026 rejected those through the API, so they stay unnamed.
        static readonly Dictionary<int, string> Functions = new Dictionary<int, string> {
            { 0x01, "cos" }, { 0x02, "sin" }, { 0x03, "tan" }, { 0x04, "acos" }, { 0x05, "asin" }, { 0x06, "atan" },
            { 0x0A, "sqrt" }, { 0x0B, "exp" }, { 0x0C, "pow" }, { 0x0D, "ln" }, { 0x0E, "log" }, { 0x0F, "floor" },
            { 0x10, "ceil" }, { 0x11, "round" }, { 0x12, "abs" }, { 0x13, "sign" }, { 0x14, "max" }, { 0x15, "min" },
            { 0x16, "random" }, { 0x1A, "isolate" }, { 0x1B, "acosh" }, { 0x1C, "asinh" }, { 0x1D, "atanh" } };

        static uint U32(byte[] d, int p) { return BitConverter.ToUInt32(d, p); }

        static string Num(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        public class Decoded
        {
            public int Index;
            public string Name, Unit, Formula;
            public double Value;   // database units
            public string Shown;   // value in the parameter's unit
        }

        // The document's parameters: those owned by the first parameter table (node #2 in parts). Some real
        // parts have a second table with its own d-numbers (d91..d94 in one) that Inventor's
        // Parameters list leaves out.
        public static List<Decoded> Decode(NodeTable t)
        {
            List<Decoded> list = new List<Decoded>();
            int table = -1;
            foreach (Node n in t.Nodes)
                if (n.Type != null && n.Type.Key == Table) { table = n.Index; break; }
            foreach (Node n in t.Nodes)
            {
                if (n.Type == null || n.Type.Key != Parameter || n.Data == null) continue;
                int owner = (int)(U32(n.Data, 0x12 + Sketches.Shift(n.Data)) & 0x7FFFFFFF) - 1;
                if (owner != table) continue;
                int p;
                string name = NodeTable.Label(n, out p);
                if (name == null) continue;
                byte[] d = n.Data;
                int unit = (int)U32(d, p + 4) - 1, formula = (int)U32(d, p + 8) - 1;
                double value = BitConverter.ToDouble(d, p + 20);
                list.Add(new Decoded { Index = n.Index, Name = name, Unit = Units.Name(t, unit) ?? "?", Value = value,
                    Shown = Units.Value(t, value, unit), Formula = Expr(t, formula) });
            }
            return list;
        }

        public static string Report(NodeTable t)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Decoded p in Decode(t))
                sb.AppendFormat("  #{0,-4:X} {1,-10} {2,-8} value {3,-14} = {4}\n", p.Index, p.Name, p.Unit, p.Shown, p.Formula);
            return sb.ToString();
        }

        static readonly Regex TruthParam = new Regex(
            "\\{\"name\": \"(?<name>[^\"]*)\", \"expression\": \"(?<expr>[^\"]*)\", \"value\": (?<value>[^,]*), \"units\": \"(?<units>[^\"]*)\"");
        static readonly Regex Token = new Regex(@"\d+(\.\d*)?([eE][-+]?\d+)?|\w+|\S");

        // "10.000 mm" and "10 mm" compare equal: numbers are compared by value, spaces ignored.
        public static string Normalize(string expr)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Match m in Token.Matches(expr))
            {
                double v;
                bool number = char.IsDigit(m.Value[0]) && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
                sb.Append(number ? Units.Num(double.Parse(m.Value, CultureInfo.InvariantCulture)) : m.Value).Append(' ');
            }
            return sb.ToString().Trim();
        }

        // Decoded parameters vs a truth .json from 'invre gen': name, unit, value and formula. Returns mismatches.
        public static List<string> Compare(NodeTable t, string truthJson, out int checkedCount)
        {
            Dictionary<string, Decoded> byName = new Dictionary<string, Decoded>();
            foreach (Decoded p in Decode(t)) byName[p.Name] = p;
            List<string> bad = new List<string>();
            checkedCount = 0;
            foreach (Match m in TruthParam.Matches(truthJson))
            {
                checkedCount++;
                string name = m.Groups["name"].Value, units = m.Groups["units"].Value, expr = m.Groups["expr"].Value;
                double value;
                double.TryParse(m.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                Decoded d;
                if (!byName.TryGetValue(name, out d)) { bad.Add(name + ": not decoded"); continue; }
                if (d.Unit != units) bad.Add(name + ": unit " + d.Unit + " (Inventor: " + units + ")");
                if (Math.Abs(d.Value - value) > 1e-9 * Math.Max(1, Math.Abs(value))) bad.Add(name + ": value " + Num(d.Value) + " (Inventor: " + Num(value) + ")");
                // In sheet-metal parts the API's parameter list gives some formulas as their value ("0.500 mm" for
                // Thickness); the feature's own property gives the formula, so the file's is the real one. The value was
                // compared above.
                bool valueOnly = System.Text.RegularExpressions.Regex.IsMatch(expr, @"^[\d.]+ \w+$") && truthJson.Contains("\"name\": \"Thickness\"");
                if (Normalize(d.Formula) != Normalize(expr) && !valueOnly) bad.Add(name + ": formula " + d.Formula + " (Inventor: " + expr + ")");
            }
            return bad;
        }

        // Formula text as Inventor shows it: each number in its own unit ("Len + 1 in").
        public static string Expr(NodeTable t, int index)
        {
            int prec;
            return Expr(t, index, 0, out prec);
        }

        static int Precedence(string op)
        {
            return op == "+" || op == "-" ? 1 : op == "^" ? 3 : 2;
        }

        // prec: binding strength of the returned text (4 = atom), used to add only the brackets needed.
        static string Expr(NodeTable t, int index, int depth, out int prec)
        {
            prec = 4;
            Node n = t.At(index);
            if (n == null || n.Data == null || n.Type == null || depth > 50) return "?#" + index.ToString("X");
            byte[] d = n.Data;
            uint key = n.Type.Key;
            string op;
            int pl, pr;
            if (key == Value) return Units.Value(t, BitConverter.ToDouble(d, 10), (int)U32(d, 6) - 1);
            if (key == Ref)
            {
                Node target = t.At((int)(U32(d, 10) & 0x7FFFFFFF) - 1);
                return target == null ? "?ref" : NodeTable.Label(target);
            }
            if (Operators.TryGetValue(key, out op))
            {
                prec = Precedence(op);
                string l = Expr(t, (int)U32(d, 10) - 1, depth + 1, out pl);
                string r = Expr(t, (int)U32(d, 14) - 1, depth + 1, out pr);
                if (pl < prec || (op == "^" && pl == prec)) l = "(" + l + ")";
                if (pr < prec || (pr == prec && op != "+" && op != "*")) r = "(" + r + ")";
                return l + " " + op + " " + r;
            }
            if (key == Brackets) return "(" + Expr(t, (int)U32(d, 10) - 1, depth + 1, out pl) + ")";
            if (key == Negate)
            {
                string e = Expr(t, (int)U32(d, 10) - 1, depth + 1, out pl);
                return "-" + (pl < 4 ? "(" + e + ")" : e);
            }
            if (key == Function && U32(d, 10) == 0x30000002)
            {
                int count = (int)U32(d, 14);
                // An empty list (random()) is just {marker, 0}, without the two u32s before the operands.
                int id = BitConverter.ToUInt16(d, count == 0 ? 18 : 26 + 4 * count);
                string name;
                if (!Functions.TryGetValue(id, out name)) name = "fn" + id.ToString("X");
                List<string> args = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    int arg = (int)U32(d, 26 + 4 * i) - 1;
                    // isolate(expr;unit;unit): the units are stored as value nodes of 1 with no typed decimals.
                    Node a = t.At(arg);
                    if (name == "isolate" && i > 0 && a != null && a.Type != null && a.Type.Key == Value)
                        args.Add(Units.Name(t, (int)U32(a.Data, 6) - 1));
                    else args.Add(Expr(t, arg, depth + 1, out pl));
                }
                return name + "(" + string.Join(";", args.ToArray()) + ")";
            }
            if (key == Constant) return NodeTable.Label(n) ?? "const";
            return "?" + n.TypeName;
        }
    }
}
