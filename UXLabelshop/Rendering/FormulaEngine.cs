using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UXLabelshop.Rendering;

/// <summary>公式错误（可被 IFERROR 捕获），消息面向业务人员。</summary>
public class FormulaException : Exception
{
    public FormulaException(string message) : base(message) { }
}

/// <summary>
/// Excel 风格公式引擎：只认白名单函数，不 eval、不反射、不执行任意代码。
/// 语法：{{变量}} 引用、双引号字符串、数字、&amp; 连接、= &lt;&gt; &lt; &gt; &lt;= &gt;= 比较、
/// + - * / 四则、函数调用（大小写不敏感），公式可带前导 =。
/// </summary>
public static class FormulaEngine
{
    private static readonly Regex VarTokenRegex = new(@"^\{\{\s*([A-Za-z0-9_\-]+)\s*\}\}", RegexOptions.Compiled);
    private static readonly Regex DepRegex = new(@"\{\{\s*([A-Za-z0-9_\-]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// 查库能力宿主（DBLOOKUP 用）。App 启动时注入，自检时可换成假宿主做脱库测试。
    /// 未注入时 DBLOOKUP 抛「需要数据库连接」，可被 IFERROR 接住。
    /// </summary>
    public static IFormulaHost? DefaultHost { get; set; }

    /// <summary>求值并返回文本结果（清单单元格最终都是字符串）。</summary>
    public static string Evaluate(string? formula, IReadOnlyDictionary<string, string?> values,
        IFormulaHost? host = null)
    {
        if (string.IsNullOrWhiteSpace(formula)) return "";
        try
        {
            var expr = Parse(formula);
            return ToText(expr.Eval(new EvalContext(values) { Host = host ?? DefaultHost }));
        }
        catch (FormulaException ex)
        {
            return "#ERR: " + ex.Message;
        }
    }

    /// <summary>严格求值：错误直接抛出（自检 / 公式编辑器测试用）。</summary>
    public static string EvaluateStrict(string? formula, IReadOnlyDictionary<string, string?> values,
        IFormulaHost? host = null)
    {
        var expr = Parse(formula ?? "");
        return ToText(expr.Eval(new EvalContext(values) { Host = host ?? DefaultHost }));
    }

    /// <summary>公式引用了哪些变量（用于依赖分析与自动重算）。</summary>
    public static HashSet<string> Dependencies(string? formula)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(formula)) return set;
        foreach (Match m in DepRegex.Matches(formula))
            set.Add(m.Groups[1].Value);
        return set;
    }

    /// <summary>语法校验：可解析返回 null，否则返回错误消息。</summary>
    public static string? Validate(string? formula)
    {
        if (string.IsNullOrWhiteSpace(formula)) return null;
        try
        {
            var expr = Parse(formula);
            // 顺带做 DBLOOKUP 的静态 SQL 检查（纯文本，不连库），保存时就能拦住写语句
            return CheckSqlLiterals(expr);
        }
        catch (FormulaException ex) { return ex.Message; }
    }

    private static string? CheckSqlLiterals(Expr e) => e switch
    {
        Binary b => CheckSqlLiterals(b.A) ?? CheckSqlLiterals(b.B),
        Unary u => CheckSqlLiterals(u.A),
        Func f => CheckFunc(f),
        _ => null,
    };

    private static string? CheckFunc(Func f)
    {
        if (f.Name == "DBLOOKUP" && f.Args.Count > 0 && f.Args[0] is Literal { V: string sql })
        {
            var err = SqlGuard.Check(sql);
            if (err != null) return "DBLOOKUP：" + err;
        }
        foreach (var a in f.Args)
        {
            var inner = CheckSqlLiterals(a);
            if (inner != null) return inner;
        }
        return null;
    }

    // ---------------- 词法 ----------------

    private enum T { Number, Str, Var, Ident, Op, Lp, Rp, Comma, End }

    private readonly record struct Token(T Kind, string Text, object? Value, int Pos);

    private static List<Token> Tokenize(string src)
    {
        var s = src.Trim();
        if (s.StartsWith('=')) s = s[1..];
        var list = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '"')
            {
                var start = i;
                i++;
                var sb = new StringBuilder();
                var closed = false;
                while (i < s.Length)
                {
                    if (s[i] == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++; closed = true; break;
                    }
                    sb.Append(s[i++]);
                }
                if (!closed) throw new FormulaException($"第 {start + 1} 字符处字符串缺少结束引号");
                list.Add(new Token(T.Str, sb.ToString(), sb.ToString(), start));
                continue;
            }

            if (c == '{' && i + 1 < s.Length && s[i + 1] == '{')
            {
                var m = VarTokenRegex.Match(s[i..]);
                if (!m.Success) throw new FormulaException($"第 {i + 1} 字符处变量引用格式错误，应写 {{{{变量名}}}}");
                list.Add(new Token(T.Var, m.Groups[1].Value, m.Groups[1].Value, i));
                i += m.Length;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                var start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                var text = s[start..i];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    throw new FormulaException($"第 {start + 1} 字符处数字格式错误：{text}");
                list.Add(new Token(T.Number, text, num, start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                list.Add(new Token(T.Ident, s[start..i], null, start));
                continue;
            }

            if (c == '(') { list.Add(new Token(T.Lp, "(", null, i++)); continue; }
            if (c == ')') { list.Add(new Token(T.Rp, ")", null, i++)); continue; }
            if (c == ',') { list.Add(new Token(T.Comma, ",", null, i++)); continue; }

            var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            if (two is "<>" or "<=" or ">=")
            {
                list.Add(new Token(T.Op, two, null, i)); i += 2; continue;
            }
            if ("+-*/&=<>".Contains(c))
            {
                list.Add(new Token(T.Op, c.ToString(), null, i++)); continue;
            }
            throw new FormulaException($"第 {i + 1} 字符处无法识别的字符：{c}");
        }
        list.Add(new Token(T.End, "", null, s.Length));
        return list;
    }

    // ---------------- 语法树 ----------------

    internal abstract record Expr
    {
        public abstract object Eval(EvalContext ctx);
    }
    internal sealed record Literal(object V) : Expr
    {
        public override object Eval(EvalContext ctx) => V;
    }
    internal sealed record VarRef(string Key) : Expr
    {
        public override object Eval(EvalContext ctx) => ctx.Get(Key);
    }
    internal sealed record Unary(string Op, Expr A) : Expr
    {
        public override object Eval(EvalContext ctx)
        {
            var a = A.Eval(ctx);
            return Op switch
            {
                "-" => -ToNumber(a),
                "+" => ToNumber(a),
                _ => throw new FormulaException("未知一元运算 " + Op),
            };
        }
    }
    internal sealed record Binary(string Op, Expr A, Expr B) : Expr
    {
        public override object Eval(EvalContext ctx)
        {
            // IF / IFERROR 在函数里短路处理；这里都是普通二元运算
            var a = A.Eval(ctx);
            var b = B.Eval(ctx);
            return Op switch
            {
                "&" => ToText(a) + ToText(b),
                "+" => ToNumber(a) + ToNumber(b),
                "-" => ToNumber(a) - ToNumber(b),
                "*" => ToNumber(a) * ToNumber(b),
                "/" => Div(a, b),
                "=" => Compare(a, b) == 0,
                "<>" => Compare(a, b) != 0,
                "<" => Compare(a, b) < 0,
                ">" => Compare(a, b) > 0,
                "<=" => Compare(a, b) <= 0,
                ">=" => Compare(a, b) >= 0,
                _ => throw new FormulaException("未知运算 " + Op),
            };
        }
    }
    internal sealed record Func(string Name, List<Expr> Args) : Expr
    {
        public override object Eval(EvalContext ctx) => Functions.Call(Name, Args, ctx);
    }

    internal sealed class EvalContext
    {
        private readonly IReadOnlyDictionary<string, string?> _values;
        public IFormulaHost? Host { get; init; }
        public int Depth;
        public EvalContext(IReadOnlyDictionary<string, string?> values) => _values = values;
        public string Get(string key)
        {
            if (_values.TryGetValue(key, out var v)) return v ?? "";
            foreach (var kv in _values)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value ?? "";
            return "";
        }
        public void Enter()
        {
            if (++Depth > 60) throw new FormulaException("公式嵌套过深（超过 60 层）");
        }
        public void Exit() => Depth--;
    }

    private static object Div(object a, object b)
    {
        var d = ToNumber(b);
        if (Math.Abs(d) < 1e-12) throw new FormulaException("除数为 0");
        return ToNumber(a) / d;
    }

    private static int Compare(object a, object b)
    {
        if (a is bool ba && b is bool bb) return ba.CompareTo(bb);
        var na = TryNumber(a, out var da);
        var nb = TryNumber(b, out var db);
        if (na && nb) return da.CompareTo(db);
        return string.Compare(ToText(a), ToText(b), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNumber(object v, out double d)
    {
        if (v is double x) { d = x; return true; }
        if (v is bool bl) { d = bl ? 1 : 0; return true; }
        return double.TryParse(ToText(v).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
    }

    internal static double ToNumber(object v)
    {
        if (TryNumber(v, out var d)) return d;
        throw new FormulaException($"“{ToText(v)}”不是数字");
    }

    internal static bool ToBool(object v) => v switch
    {
        bool b => b,
        double d => Math.Abs(d) > 1e-12,
        _ => TryNumber(v, out var d) ? Math.Abs(d) > 1e-12
             : string.Equals(ToText(v).Trim(), "TRUE", StringComparison.OrdinalIgnoreCase),
    };

    internal static string ToText(object? v) => v switch
    {
        null => "",
        string s => s,
        double d => Math.Abs(d - Math.Round(d)) < 1e-9
            ? Math.Round(d).ToString(CultureInfo.InvariantCulture)
            : d.ToString("0.##########", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => v.ToString() ?? "",
    };

    // ---------------- 解析（递归下降） ----------------

    private sealed class Parser
    {
        private readonly List<Token> _t;
        private int _p;
        private Token Cur => _t[_p];
        public Parser(List<Token> t) => _t = t;

        public Expr Parse()
        {
            var e = Comparison();
            if (Cur.Kind != T.End) throw Err("公式末尾有多余内容：" + Cur.Text);
            return e;
        }

        private Expr Comparison()
        {
            var left = Concat();
            while (Cur.Kind == T.Op && Cur.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")
            {
                var op = Cur.Text; _p++;
                left = new Binary(op, left, Concat());
            }
            return left;
        }

        private Expr Concat()
        {
            var left = Additive();
            while (Cur.Kind == T.Op && Cur.Text == "&")
            {
                _p++;
                left = new Binary("&", left, Additive());
            }
            return left;
        }

        private Expr Additive()
        {
            var left = Multiplicative();
            while (Cur.Kind == T.Op && Cur.Text is "+" or "-")
            {
                var op = Cur.Text; _p++;
                left = new Binary(op, left, Multiplicative());
            }
            return left;
        }

        private Expr Multiplicative()
        {
            var left = UnaryExpr();
            while (Cur.Kind == T.Op && Cur.Text is "*" or "/")
            {
                var op = Cur.Text; _p++;
                left = new Binary(op, left, UnaryExpr());
            }
            return left;
        }

        private Expr UnaryExpr()
        {
            if (Cur.Kind == T.Op && Cur.Text is "-" or "+")
            {
                var op = Cur.Text; _p++;
                return new Unary(op, UnaryExpr());
            }
            if (Cur.Kind == T.Ident && Cur.Text.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                _p++;
                return new Func("NOT", new List<Expr> { UnaryExpr() });
            }
            return Primary();
        }

        private Expr Primary()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case T.Number:
                    _p++; return new Literal(t.Value!);
                case T.Str:
                    _p++; return new Literal(t.Text);
                case T.Var:
                    _p++; return new VarRef(t.Text);
                case T.Ident:
                    _p++;
                    if (Cur.Kind == T.Lp) return ParseCall(t.Text);
                    if (t.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return new Literal(true);
                    if (t.Text.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return new Literal(false);
                    throw new FormulaException($"“{t.Text}”不是函数，也不是 TRUE/FALSE。变量请写成 {{{{变量名}}}}");
                case T.Lp:
                    _p++;
                    var inner = Comparison();
                    Expect(T.Rp, "缺少右括号 )");
                    return inner;
                default:
                    throw Err("此处缺少值或表达式");
            }
        }

        private Expr ParseCall(string name)
        {
            // 语法校验阶段就拦下未知函数，避免保存后运行时才暴露
            if (!Functions.IsKnown(name))
                throw new FormulaException($"未知函数 {name}；可用函数：" + Functions.KnownNames);
            Expect(T.Lp, "函数名后缺少 (");
            var args = new List<Expr>();
            if (Cur.Kind != T.Rp)
            {
                while (true)
                {
                    args.Add(Comparison());
                    if (Cur.Kind == T.Comma) { _p++; continue; }
                    break;
                }
            }
            Expect(T.Rp, "函数 " + name + " 缺少右括号 )");
            return new Func(name.ToUpperInvariant(), args);
        }

        private void Expect(T kind, string msg)
        {
            if (Cur.Kind != kind) throw Err(msg);
            _p++;
        }

        private FormulaException Err(string msg) =>
            new($"{msg}（第 {Cur.Pos + 1} 字符附近）");
    }

    private static Expr Parse(string formula)
    {
        var tokens = Tokenize(formula);
        if (tokens.Count == 1) throw new FormulaException("公式为空");
        return new Parser(tokens).Parse();
    }

    // ---------------- 函数库 ----------------

    internal static class Functions
    {
        private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            "IF", "IFERROR", "AND", "OR", "NOT", "ISBLANK", "ISNUMBER",
            "LEFT", "RIGHT", "MID", "LEN", "FIND", "SEARCH", "SUBSTITUTE", "REPLACE",
            "TRIM", "UPPER", "LOWER", "CONCAT", "TEXTJOIN", "SPLIT",
            "ROUND", "INT", "ABS", "VALUE",
            "DBLOOKUP",
        };

        private static readonly Dictionary<string, (int Min, int Max)> Arity = new(StringComparer.OrdinalIgnoreCase)
        {
            ["IF"] = (3, 3), ["IFERROR"] = (2, 2), ["AND"] = (1, 32), ["OR"] = (1, 32),
            ["NOT"] = (1, 1), ["ISBLANK"] = (1, 1), ["ISNUMBER"] = (1, 1),
            ["LEFT"] = (1, 2), ["RIGHT"] = (1, 2), ["MID"] = (3, 3), ["LEN"] = (1, 1),
            ["FIND"] = (2, 3), ["SEARCH"] = (2, 3), ["SUBSTITUTE"] = (3, 4), ["REPLACE"] = (4, 4),
            ["TRIM"] = (1, 1), ["UPPER"] = (1, 1), ["LOWER"] = (1, 1),
            ["CONCAT"] = (1, 32), ["TEXTJOIN"] = (3, 34), ["SPLIT"] = (3, 3),
            ["ROUND"] = (1, 2), ["INT"] = (1, 1), ["ABS"] = (1, 1), ["VALUE"] = (1, 1),
            ["DBLOOKUP"] = (1, 2),
        };

        public static bool IsKnown(string name) => Known.Contains(name);

        public static string KnownNames =>
            "IF, IFERROR, AND, OR, NOT, ISBLANK, ISNUMBER, LEFT, RIGHT, MID, LEN, FIND, SEARCH, " +
            "SUBSTITUTE, REPLACE, TRIM, UPPER, LOWER, CONCAT, TEXTJOIN, SPLIT, ROUND, INT, ABS, VALUE, DBLOOKUP";

        public static object Call(string name, List<Expr> args, EvalContext ctx)
        {
            if (!Known.Contains(name)) throw new FormulaException($"未知函数 {name}");
            if (Arity.TryGetValue(name, out var arity) && (args.Count < arity.Min || args.Count > arity.Max))
                throw new FormulaException($"{name} 需要 {arity.Min}{(arity.Max == arity.Min ? "" : "~" + arity.Max)} 个参数，实际 {args.Count} 个");
            ctx.Enter();
            try
            {
                // 短路函数必须直接拿 Expr，不能预先求值
                if (name == "IF")
                {
                    Need(args, 3, 3, name);
                    return ToBool(args[0].Eval(ctx)) ? args[1].Eval(ctx) : args[2].Eval(ctx);
                }
                if (name == "IFERROR")
                {
                    Need(args, 2, 2, name);
                    try { return args[0].Eval(ctx); }
                    catch (FormulaException) { return args[1].Eval(ctx); }
                }
                if (name == "AND")
                {
                    foreach (var a in args) if (!ToBool(a.Eval(ctx))) return false;
                    return true;
                }
                if (name == "OR")
                {
                    foreach (var a in args) if (ToBool(a.Eval(ctx))) return true;
                    return false;
                }
                if (name == "DBLOOKUP")
                {
                    Need(args, 1, 2, name);
                    var sql = ToText(args[0].Eval(ctx));
                    var fallback = args.Count > 1 ? ToText(args[1].Eval(ctx)) : "";
                    return DbLookup(sql, fallback, ctx);
                }

                var v = args.Select(a => a.Eval(ctx)).ToList();
                return name switch
                {
                    "NOT" => !ToBool(V(v, 0)),
                    "ISBLANK" => string.IsNullOrEmpty(ToText(V(v, 0))),
                    "ISNUMBER" => TryNumber(V(v, 0), out _),
                    "LEFT" => Left(ToText(V(v, 0)), ArgCount(args, 1, 2, name) == 1 ? 1 : (int)ToNumber(V(v, 1))),
                    "RIGHT" => Right(ToText(V(v, 0)), ArgCount(args, 1, 2, name) == 1 ? 1 : (int)ToNumber(V(v, 1))),
                    "MID" => Mid(ToText(V(v, 0)), (int)ToNumber(V(v, 1)), (int)ToNumber(V(v, 2))),
                    "LEN" => (double)ToText(V(v, 0)).Length,
                    "FIND" => (double)Find(ToText(V(v, 0)), ToText(V(v, 1)), ArgCount(args, 2, 3, name) == 2 ? 1 : (int)ToNumber(V(v, 2)), false),
                    "SEARCH" => (double)Find(ToText(V(v, 0)), ToText(V(v, 1)), ArgCount(args, 2, 3, name) == 2 ? 1 : (int)ToNumber(V(v, 2)), true),
                    "SUBSTITUTE" => Substitute(ToText(V(v, 0)), ToText(V(v, 1)), ToText(V(v, 2)), ArgCount(args, 3, 4, name) == 3 ? 0 : (int)ToNumber(V(v, 3))),
                    "REPLACE" => Replace(ToText(V(v, 0)), (int)ToNumber(V(v, 1)), (int)ToNumber(V(v, 2)), ToText(V(v, 3))),
                    "TRIM" => ToText(V(v, 0)).Trim(),
                    "UPPER" => ToText(V(v, 0)).ToUpperInvariant(),
                    "LOWER" => ToText(V(v, 0)).ToLowerInvariant(),
                    "CONCAT" => string.Concat(v.Select(ToText)),
                    "TEXTJOIN" => TextJoin(v, name),
                    "SPLIT" => Split(ToText(V(v, 0)), ToText(V(v, 1)), (int)ToNumber(V(v, 2))),
                    "ROUND" => Math.Round(ToNumber(V(v, 0)), ArgCount(args, 1, 2, name) == 1 ? 0 : (int)ToNumber(V(v, 1)), MidpointRounding.AwayFromZero),
                    "INT" => Math.Floor(ToNumber(V(v, 0))),
                    "ABS" => Math.Abs(ToNumber(V(v, 0))),
                    "VALUE" => ToNumber(V(v, 0)),
                    _ => throw new FormulaException("未实现函数 " + name),
                };
            }
            finally { ctx.Exit(); }
        }

        private static object V(List<object> v, int i)
        {
            if (i < v.Count) return v[i];
            throw new FormulaException($"缺少第 {i + 1} 个参数");
        }

        private static void Need(List<Expr> a, int min, int max, string name)
        {
            if (a.Count < min || a.Count > max)
                throw new FormulaException($"{name} 需要 {min}{(max == min ? "" : "~" + max)} 个参数，实际 {a.Count} 个");
        }

        private static int ArgCount(List<Expr> a, int min, int max, string name)
        {
            Need(a, min, max, name);
            return a.Count;
        }

        /// <summary>
        /// DBLOOKUP(查询语句 [, 找不到时的默认值])：执行只读查询，取结果集首行首列。
        /// 变量以 @pN 参数形式传给驱动，不做字符串拼接；无结果返回默认值（缺省空串）。
        /// </summary>
        private static string DbLookup(string sqlTemplate, string fallback, EvalContext ctx)
        {
            var err = SqlGuard.Check(sqlTemplate);
            if (err != null) throw new FormulaException("DBLOOKUP：" + err);

            var host = ctx.Host
                ?? throw new FormulaException("DBLOOKUP 需要数据库连接，当前没有可用的数据源");

            var call = SqlGuard.Bind(sqlTemplate, ctx.Get);
            var value = host.Scalar(call.Sql, call.Args);
            return value ?? fallback;
        }

        private static string Left(string s, int n)
        {
            if (n < 0) throw new FormulaException("LEFT 的字数不能为负");
            return s[..Math.Min(n, s.Length)];
        }

        private static string Right(string s, int n)
        {
            if (n < 0) throw new FormulaException("RIGHT 的字数不能为负");
            return s[Math.Max(0, s.Length - Math.Min(n, s.Length))..];
        }

        private static string Mid(string s, int start, int len)
        {
            if (start < 1) throw new FormulaException("MID 的起始位置从 1 开始");
            if (len < 0) throw new FormulaException("MID 的字数不能为负");
            var i = start - 1;
            return i >= s.Length ? "" : s.Substring(i, Math.Min(len, s.Length - i));
        }

        private static int Find(string needle, string haystack, int start, bool ignoreCase)
        {
            if (start < 1) start = 1;
            var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var idx = haystack.IndexOf(needle, Math.Min(start - 1, haystack.Length), cmp);
            if (idx < 0) throw new FormulaException($"找不到“{needle}”");
            return idx + 1;
        }

        private static string Substitute(string s, string oldText, string newText, int which)
        {
            if (oldText.Length == 0) return s;
            if (which > 1)
            {
                var pos = -1;
                for (var k = 0; k < which; k++)
                {
                    pos = s.IndexOf(oldText, pos + 1, StringComparison.Ordinal);
                    if (pos < 0) return s;
                }
                return s[..pos] + newText + s[(pos + oldText.Length)..];
            }
            return s.Replace(oldText, newText, StringComparison.Ordinal);
        }

        private static string Replace(string s, int start, int len, string newText)
        {
            if (start < 1 || len < 0) throw new FormulaException("REPLACE 的起始位置从 1 开始");
            var i = Math.Min(start - 1, s.Length);
            var take = Math.Min(len, s.Length - i);
            return s[..i] + newText + s[(i + take)..];
        }

        private static string TextJoin(List<object> v, string name)
        {
            if (v.Count < 3) throw new FormulaException("TEXTJOIN 需要 分隔符, 是否忽略空值, 以及至少一个文本");
            var delim = ToText(v[0]);
            var ignoreEmpty = ToBool(v[1]);
            var parts = v.Skip(2).Select(ToText);
            if (ignoreEmpty) parts = parts.Where(p => p.Length > 0);
            return string.Join(delim, parts);
        }

        private static string Split(string s, string delim, int index)
        {
            if (delim.Length == 0) throw new FormulaException("SPLIT 的分隔符不能为空");
            if (index < 1) throw new FormulaException("SPLIT 的段号从 1 开始");
            var parts = s.Split(delim, StringSplitOptions.None);
            if (index > parts.Length) throw new FormulaException($"SPLIT 只有 {parts.Length} 段，取不到第 {index} 段");
            return parts[index - 1];
        }
    }
}
