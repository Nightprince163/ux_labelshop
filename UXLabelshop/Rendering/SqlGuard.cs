using System.Text;
using System.Text.RegularExpressions;

namespace UXLabelshop.Rendering;

/// <summary>参数化后的查询：SQL 只含 @p0..@pN，变量值全在 Args 里。</summary>
public sealed record SqlCall(string Sql, IReadOnlyList<string?> Args);

/// <summary>
/// DBLOOKUP 的 SQL 守卫（纯函数，不连库）：
/// 1) 只允许单条 SELECT / WITH ... SELECT；
/// 2) 字面量外出现分号（多语句）直接拒绝；
/// 3) 写操作与危险函数黑名单（词边界匹配，跳过字符串内容）；
/// 4) 把 {{变量}} 改成 @pN 参数占位符，值单独传给驱动，杜绝拼接注入。
/// </summary>
public static class SqlGuard
{
    private const int MaxLength = 4000;
    private const int MaxArgs = 20;

    private static readonly Regex VarRegex =
        new(@"\{\{\s*([A-Za-z0-9_\-]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>写操作、危险函数、文件/锁相关关键字。命中即拒绝。</summary>
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        // 注意：不能黑 REPLACE —— 它是合法的字符串函数 REPLACE(a,b,c)；
        // "REPLACE INTO" 这种写法必然以 REPLACE 开头，已被「只能 SELECT/WITH 开头」挡住。
        "INSERT", "UPDATE", "DELETE",
        "DROP", "ALTER", "TRUNCATE", "CREATE", "RENAME",
        "GRANT", "REVOKE", "CALL", "PREPARE", "EXECUTE", "DEALLOCATE", "HANDLER",
        "LOAD", "OUTFILE", "DUMPFILE", "INTO",
        "LOCK", "UNLOCK", "FLUSH", "SHUTDOWN", "KILL",
        "SLEEP", "BENCHMARK", "GET_LOCK", "RELEASE_LOCK",
        "PROCEDURE", "TRIGGER", "EVENT",
    };

    /// <summary>
    /// 静态校验（不连库）。返回 null 表示通过，否则为面向业务人员的错误消息。
    /// 用于保存公式那一刻就把 "DELETE ..." 拦住。
    /// </summary>
    public static string? Check(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "查询语句不能为空";
        if (sql.Length > MaxLength) return $"查询语句过长（上限 {MaxLength} 字符）";

        var words = new List<string>();
        var lastSemicolon = -1;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c is '\'' or '"')
            {
                if (!TrySkipLiteral(sql, ref i)) return "字符串引号没有闭合";
                continue;
            }
            if (c == '`')
            {
                if (!TrySkipQuoted(sql, ref i, '`')) return "反引号没有闭合";
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { SkipLine(sql, ref i); continue; }
            if (c == '#') { SkipLine(sql, ref i); continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return "注释 /* 没有闭合";
                i = end + 2;
                continue;
            }
            if (c == ';') { lastSemicolon = i; i++; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                words.Add(sql[start..i].ToUpperInvariant());
                continue;
            }
            i++;
        }

        if (words.Count == 0) return "查询语句不能为空";

        var head = words[0];
        if (head != "SELECT" && head != "WITH")
            return $"只允许 SELECT 查询（以 SELECT 或 WITH 开头），当前是 {head}";

        if (lastSemicolon >= 0 && sql[(lastSemicolon + 1)..].Trim().Length > 0)
            return "只允许一条语句，不能用分号拼接多条 SQL";

        foreach (var w in words)
            if (Forbidden.Contains(w))
                return $"查询里不允许出现 {w}，DBLOOKUP 只能做只读查询";

        return null;
    }

    /// <summary>
    /// 把 {{变量}} 替换为 @pN。
    /// 若变量独占整个字符串字面量（'{{prodno}}'），连引号一起去掉；
    /// 若与其他文字混在一段字面量里（'%{{partno}}%'），改写成 CONCAT('%', @p0, '%')。
    /// </summary>
    public static SqlCall Bind(string sql, Func<string, string?> resolve)
    {
        var args = new List<string?>();
        var sb = new StringBuilder(sql.Length + 32);
        var segStart = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];

            if (c is '\'' or '"')
            {
                var litStart = i;
                var closed = TrySkipLiteral(sql, ref i);
                var body = closed ? sql[(litStart + 1)..(i - 1)] : sql[(litStart + 1)..i];
                if (!body.Contains("{{")) continue;

                sb.Append(sql[segStart..litStart]);
                sb.Append(RebuildLiteral(c, body, args, resolve));
                segStart = i;
                continue;
            }
            if (c == '`')
            {
                if (!TrySkipQuoted(sql, ref i, '`'))
                    throw new FormulaException("DBLOOKUP 的查询语句里反引号没有闭合");
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { SkipLine(sql, ref i); continue; }
            if (c == '#') { SkipLine(sql, ref i); continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }
            if (c == '{' && i + 1 < sql.Length && sql[i + 1] == '{')
            {
                var m = VarRegex.Match(sql[i..]);
                if (m.Success)
                {
                    sb.Append(sql[segStart..i]);
                    sb.Append(Param(args, m.Groups[1].Value, resolve));
                    i += m.Length;
                    segStart = i;
                    continue;
                }
            }
            i++;
        }
        sb.Append(sql[segStart..]);
        return new SqlCall(sb.ToString(), args);
    }

    /// <summary>字面量内部：按 {{变量}} 切段，全参数则退化为裸 @pN，否则拼 CONCAT。</summary>
    private static string RebuildLiteral(char quote, string body, List<string?> args,
        Func<string, string?> resolve)
    {
        var segs = new List<(bool IsParam, string Text)>();
        var pos = 0;
        foreach (Match m in VarRegex.Matches(body))
        {
            if (m.Index > pos) segs.Add((false, body[pos..m.Index]));
            segs.Add((true, m.Groups[1].Value));
            pos = m.Index + m.Length;
        }
        if (pos < body.Length) segs.Add((false, body[pos..]));

        foreach (var (isParam, text) in segs)
            if (!isParam && text.Contains("{{"))
                throw new FormulaException("DBLOOKUP 的变量引用格式错误，应写 {{变量名}}（只能字母、数字、下划线、短横线）");

        // 整段就是一个变量 → 连引号去掉，直接给参数占位符
        if (segs.Count == 1 && segs[0].IsParam)
            return Param(args, segs[0].Text, resolve);

        // 一个参数 + 空的其他段，同上
        var effective = segs.Where(s => s.IsParam || s.Text.Length > 0).ToList();
        if (effective.Count == 1 && effective[0].IsParam)
            return Param(args, effective[0].Text, resolve);

        var sb = new StringBuilder("CONCAT(");
        var first = true;
        foreach (var (isParam, text) in effective)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(isParam ? Param(args, text, resolve) : Quote(text, quote));
        }
        if (effective.Count < 2) sb.Append(", ''");   // CONCAT 至少两个参数
        sb.Append(')');
        return sb.ToString();
    }

    private static string Param(List<string?> args, string name, Func<string, string?> resolve)
    {
        args.Add(resolve(name) ?? "");
        if (args.Count > MaxArgs)
            throw new FormulaException($"DBLOOKUP 的变量太多（上限 {MaxArgs} 个）");
        return "@p" + (args.Count - 1);
    }

    private static string Quote(string text, char quote) =>
        quote + text.Replace("\\", "\\\\").Replace(quote.ToString(), new string(quote, 2)) + quote;

    // ---------------- 跳过辅助（i 指向字符之后） ----------------

    private static bool TrySkipLiteral(string s, ref int i)
    {
        var q = s[i];
        i++;
        while (i < s.Length)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { i += 2; continue; }
            if (s[i] == q)
            {
                if (i + 1 < s.Length && s[i + 1] == q) { i += 2; continue; }
                i++;
                return true;
            }
            i++;
        }
        return false;
    }

    private static bool TrySkipQuoted(string s, ref int i, char q)
    {
        i++;
        while (i < s.Length)
        {
            if (s[i] == q)
            {
                if (i + 1 < s.Length && s[i + 1] == q) { i += 2; continue; }
                i++;
                return true;
            }
            i++;
        }
        return false;
    }

    private static void SkipLine(string s, ref int i)
    {
        while (i < s.Length && s[i] != '\n') i++;
    }
}
