using System.Text.RegularExpressions;

namespace UXLabelshop.Rendering;

/// <summary>
/// 变量解析器：把模板变量定义（scan/fixed/computed）应用到一行数据上。
/// - 缺省值：scan 变量为空时套 default_value
/// - 计算列：computed 变量按依赖拓扑序求值，无条件覆盖（用户确认的「依赖一变就重算」）
/// </summary>
public sealed class VariableResolver
{
    private readonly List<TemplateVar> _vars;
    private readonly List<TemplateVar> _computedOrdered = new();
    private readonly HashSet<string> _dependencyKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TemplateVar> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TemplateVar> ComputedOrdered => _computedOrdered;
    public IReadOnlyCollection<string> DependencyKeys => _dependencyKeys;
    public string? ValidationError { get; private set; }

    public VariableResolver(TemplateDoc doc) : this(doc.Variables) { }

    public VariableResolver(IEnumerable<TemplateVar> vars)
    {
        _vars = vars.Where(v => !string.IsNullOrWhiteSpace(v.Key)).ToList();
        foreach (var v in _vars) _byKey.TryAdd(v.Key, v);
        BuildOrder();
    }

    /// <summary>按依赖拓扑排序 computed 变量；循环依赖写入 ValidationError 并放弃这些列的计算。</summary>
    private void BuildOrder()
    {
        var computed = _vars.Where(v => v.IsComputed).ToList();
        foreach (var v in computed)
            foreach (var dep in FormulaEngine.Dependencies(v.Formula))
                _dependencyKeys.Add(dep);

        var remaining = new List<TemplateVar>(computed);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var guard = 0;
        while (remaining.Count > 0 && guard++ <= computed.Count + 1)
        {
            var progress = false;
            foreach (var v in remaining.ToList())
            {
                var deps = FormulaEngine.Dependencies(v.Formula);
                // 只关心「同为 computed 变量」的依赖，其余（scan/fixed）视为已就绪
                var pending = deps.Where(d => _byKey.TryGetValue(d, out var dv) && dv.IsComputed && !done.Contains(d)).ToList();
                if (pending.Count > 0) continue;
                _computedOrdered.Add(v);
                done.Add(v.Key);
                remaining.Remove(v);
                progress = true;
            }
            if (!progress) break;
        }

        if (remaining.Count > 0)
            ValidationError = "公式存在循环依赖：" + string.Join("、", remaining.Select(v => v.Key));
    }

    /// <summary>该模板是否有需要处理的缺省值或公式。</summary>
    public bool HasRules => _vars.Any(v => !string.IsNullOrEmpty(v.DefaultValue) || v.IsComputed);

    /// <summary>套用缺省值（只填空，不覆盖已有值）。</summary>
    public int ApplyDefaults(IDictionary<string, string?> row)
    {
        var n = 0;
        foreach (var v in _vars)
        {
            if (v.IsComputed || v.IsFixed || string.IsNullOrEmpty(v.DefaultValue)) continue;
            if (!row.TryGetValue(v.Key, out var cur) || string.IsNullOrEmpty(cur))
            {
                row[v.Key] = v.DefaultValue;
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// 重算该行全部 computed 列（无条件覆盖）。
    /// 会先把 fixed 变量的固定值并入上下文，使公式也能引用固定值变量。
    /// </summary>
    public int Recompute(IDictionary<string, string?> row)
    {
        if (_computedOrdered.Count == 0) return 0;
        var ctx = new Dictionary<string, string?>(row, StringComparer.OrdinalIgnoreCase);
        foreach (var v in _vars.Where(v => v.IsFixed))
            if (!ctx.ContainsKey(v.Key))
                ctx[v.Key] = FirstNonEmpty(v.Value, v.DefaultValue, v.Sample) ?? "";

        var n = 0;
        foreach (var v in _computedOrdered)
        {
            var val = FormulaEngine.Evaluate(v.Formula, ctx);
            ctx[v.Key] = val;
            row[v.Key] = val;
            n++;
        }
        return n;
    }

    /// <summary>整行处理：缺省值 → 计算列。返回是否发生改动。</summary>
    public bool Apply(IDictionary<string, string?> row)
    {
        var a = ApplyDefaults(row);
        var b = Recompute(row);
        return a + b > 0;
    }

    /// <summary>某个单元格变动时，是否需要重算该行的公式列。</summary>
    public bool ShouldRecompute(string changedKey) => _dependencyKeys.Contains(changedKey);

    /// <summary>
    /// 渲染前兜底：补齐 fixed 变量、缺省值，以及「调用方根本没有传入」的计算列。
    /// 清单已经算好并可能由用户手改过的计算列会被原样尊重，不重算覆盖。
    /// </summary>
    public void FillMissing(IDictionary<string, string?> values)
    {
        foreach (var (k, v) in LabelRenderer.FixedValues(_vars))
            if (!values.ContainsKey(k)) values[k] = v;
        ApplyDefaults(values);

        if (_computedOrdered.Count == 0) return;
        var missing = _computedOrdered.Where(v => !values.ContainsKey(v.Key)).ToList();
        if (missing.Count == 0) return;
        var ctx = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        foreach (var v in _computedOrdered)
        {
            var val = FormulaEngine.Evaluate(v.Formula, ctx);
            ctx[v.Key] = val;
            if (missing.Contains(v)) values[v.Key] = val;
        }
    }

    private static string? FirstNonEmpty(params string?[] xs) => xs.FirstOrDefault(x => !string.IsNullOrEmpty(x));

    /// <summary>把公式/正文里的 {{oldKey}} 替换为 {{newKey}}。</summary>
    public static string RenameRefs(string? text, string oldKey, string newKey)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var pattern = @"\{\{\s*" + Regex.Escape(oldKey) + @"\s*\}\}";
        return Regex.Replace(text, pattern, "{{" + newKey + "}}", RegexOptions.IgnoreCase);
    }
}
