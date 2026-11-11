using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Models;

namespace UXLabelshop.Services;

public class ScanParseResult
{
    public bool Matched { get; init; }
    public string Message { get; init; } = "";
    public long? TemplateId { get; init; }
    public string? RuleName { get; init; }
    public Dictionary<string, string?> Values { get; init; } = new();
    public string Raw { get; init; } = "";

    public static ScanParseResult Ok(long? templateId, string ruleName, Dictionary<string, string?> values, string raw)
        => new() { Matched = true, TemplateId = templateId, RuleName = ruleName, Values = values, Raw = raw };

    public static ScanParseResult NoMatch(string raw, string msg = "未匹配任何扫码规则，请手动录入")
        => new() { Matched = false, Raw = raw, Message = msg };
}

/// <summary>
/// 扫码解析：兼容现有两类规则
/// 1) regex：*pattern* 命名捕获组 + mapping(组名→变量)；
/// 2) delimited：按分隔符切段 → 去首尾空段 → 可选 marker 校验并移除 → seq_map(段序号→变量, 1 起)。
/// </summary>
public class ScanRuleService
{
    private readonly IServiceProvider _sp;

    public ScanRuleService(IServiceProvider sp) => _sp = sp;

    public List<LabelScanRule> GetRules()
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        return db.ScanRules.Where(r => r.Enabled).OrderBy(r => r.Priority).ThenBy(r => r.Id).ToList();
    }

    /// <summary>按优先级匹配；优先当前模板的规则，再退回通用规则（TemplateId 为空）。</summary>
    public ScanParseResult Parse(string raw, long? templateId, IReadOnlyCollection<string>? allowedKeys = null)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return ScanParseResult.NoMatch(raw, "扫码内容为空");

        var rules = GetRules();
        foreach (var rule in rules)
        {
            if (rule.TemplateId != null && templateId != null && rule.TemplateId != templateId) continue;
            if (rule.TemplateId != null && templateId == null) continue;

            var values = TryMatch(rule, raw);
            if (values == null) continue;

            if (allowedKeys != null)
                values = values.Where(kv => allowedKeys.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (values.Count == 0) continue;
            return ScanParseResult.Ok(rule.TemplateId, rule.Name, values, raw);
        }
        return ScanParseResult.NoMatch(raw);
    }

    private static Dictionary<string, string?>? TryMatch(LabelScanRule rule, string raw)
    {
        try
        {
            return rule.Kind == "delimited" ? MatchDelimited(rule, raw) : MatchRegex(rule, raw);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "规则 {Name} 解析异常", rule.Name);
            return null;
        }
    }

    private static Dictionary<string, string?>? MatchDelimited(LabelScanRule rule, string raw)
    {
        if (string.IsNullOrEmpty(rule.Delimiter) || string.IsNullOrEmpty(rule.SeqMap)) return null;

        var parts = raw.Split(rule.Delimiter).ToList();
        if (rule.StripWrap)
        {
            while (parts.Count > 0 && parts[0].Length == 0) parts.RemoveAt(0);
            while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        }

        if (!string.IsNullOrEmpty(rule.Marker))
        {
            if (parts.Count == 0 || parts[0].Trim() != rule.Marker) return null;
            parts.RemoveAt(0);
        }

        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(rule.SeqMap);
        if (map == null) return null;

        var values = new Dictionary<string, string?>();
        foreach (var (seqText, key) in map)
        {
            if (!int.TryParse(seqText, out var seq) || seq <= 0) continue;
            values[key] = seq <= parts.Count ? parts[seq - 1].Trim() : "";
        }
        return values.Count > 0 ? values : null;
    }

    private static Dictionary<string, string?>? MatchRegex(LabelScanRule rule, string raw)
    {
        if (string.IsNullOrEmpty(rule.Pattern)) return null;

        // 兼容 Python 命名组写法 (?P<name>...) → .NET (?<name>...)
        var pattern = Regex.Replace(rule.Pattern, @"\(\?P<([A-Za-z_][A-Za-z0-9_]*)>", "(?<$1>");
        var m = Regex.Match(raw, pattern, RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var mapping = string.IsNullOrEmpty(rule.Mapping)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(rule.Mapping);

        var values = new Dictionary<string, string?>();
        if (mapping != null)
            foreach (var (group, key) in mapping)
                values[key] = m.Groups[group].Success ? m.Groups[group].Value : "";
        else
            foreach (var groupName in m.Groups.Keys)
                if (!int.TryParse(groupName, out _))
                    values[groupName] = m.Groups[groupName].Value;

        return values.Count > 0 ? values : null;
    }

    // ---------- 规则维护 ----------

    public List<LabelScanRule> GetByTemplate(long templateId)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        return db.ScanRules.Where(r => r.TemplateId == templateId).OrderBy(r => r.Priority).ToList();
    }

    /// <summary>
    /// 模板没有规则时自动生成一条（按变量顺序做段映射，分隔符默认 |，先禁用待校准）。
    /// 返回新建的规则；已有规则则返回 null。
    /// </summary>
    public LabelScanRule? EnsureDefaultRule(long templateId, IReadOnlyList<string> keys, long? userId)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        if (db.ScanRules.Any(r => r.TemplateId == templateId)) return null;

        var seqMap = keys.Select((k, i) => new { seq = (i + 1).ToString(), key = k })
            .ToDictionary(x => x.seq, x => x.key);

        var rule = new LabelScanRule
        {
            Name = "自动规则（待校准）",
            TemplateId = templateId,
            Kind = "delimited",
            Delimiter = "|",
            Marker = null,
            StripWrap = true,
            SeqMap = JsonSerializer.Serialize(seqMap),
            Priority = 50,
            Enabled = false,
            UpdatedBy = userId,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        db.ScanRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    public void SaveRule(LabelScanRule rule, long? userId)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        if (rule.Id == 0)
        {
            rule.CreatedAt = DateTime.Now;
            rule.UpdatedAt = DateTime.Now;
            rule.UpdatedBy = userId;
            db.ScanRules.Add(rule);
        }
        else
        {
            var r = db.ScanRules.Single(x => x.Id == rule.Id);
            r.Name = rule.Name;
            r.Kind = rule.Kind;
            r.Pattern = rule.Pattern;
            r.Mapping = rule.Mapping;
            r.Delimiter = rule.Delimiter;
            r.Marker = rule.Marker;
            r.StripWrap = rule.StripWrap;
            r.SeqMap = rule.SeqMap;
            r.SampleCodes = rule.SampleCodes;
            r.Priority = rule.Priority;
            r.Enabled = rule.Enabled;
            r.TemplateId = rule.TemplateId;
            r.UpdatedAt = DateTime.Now;
            r.UpdatedBy = userId;
        }
        db.SaveChanges();
    }

    public void DeleteRule(long ruleId)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        var r = db.ScanRules.FirstOrDefault(x => x.Id == ruleId);
        if (r == null) return;
        db.ScanRules.Remove(r);
        db.SaveChanges();
    }

    /// <summary>试匹配（不落库）：供规则对话框预览。</summary>
    public ScanParseResult TestMatch(LabelScanRule rule, string sample)
    {
        var values = TryMatch(rule, sample);
        return values == null
            ? ScanParseResult.NoMatch(sample, "未匹配")
            : ScanParseResult.Ok(rule.TemplateId, rule.Name, values, sample);
    }
}
