using System.IO;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Printing;
using UXLabelshop.Rendering;

namespace UXLabelshop.Services;

/// <summary>主流程自检：扫码解析 → 空单元格填充 → 导出打印 → 记录入库。</summary>
public class FlowTestService
{
    private readonly IServiceProvider _sp;
    private readonly TemplateService _templates;
    private readonly ScanRuleService _rules;
    private readonly PrintService _print;

    public FlowTestService(IServiceProvider sp, TemplateService templates, ScanRuleService rules, PrintService print)
    {
        _sp = sp;
        _templates = templates;
        _rules = rules;
        _print = print;
    }

    public List<string> Run()
    {
        var lines = new List<string>();
        var template = _templates.GetPublished().FirstOrDefault(t => t.Code == "ux5171u")
                       ?? _templates.GetPublished().First();
        var doc = _templates.LoadDoc(template.Id);
        lines.Add($"[INFO] 模板 {template.Code}：{doc.Elements.Count} 元素，{doc.Variables.Count} 变量");
        var editable = doc.Variables.Where(v => (v.Source ?? "scan") != "fixed").Select(v => v.Key).ToList();

        var samples = _rules.GetRules()
            .Where(r => r.TemplateId == template.Id && r.SampleCodes != null)
            .SelectMany(r => System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.SampleCodes!) ?? new())
            .ToList();
        lines.Add($"[INFO] 规则样本码 {samples.Count} 条");

        var rows = new List<Dictionary<string, string?>>();
        foreach (var code in samples)
        {
            var res = _rules.Parse(code, template.Id, editable);
            lines.Add(res.Matched
                ? $"[OK] 解析 {res.RuleName}：{string.Join(", ", res.Values.Take(5).Select(kv => $"{kv.Key}={kv.Value}"))}"
                : $"[FAIL] 未匹配：{code}");
            rows.Add(res.Matched
                ? editable.ToDictionary(k => k, k => res.Values.TryGetValue(k, out var v) ? v : "")
                : editable.ToDictionary(k => k, _ => (string?)""));
        }

        // 空白单元格批量填充（序列号列）
        var seqKey = editable.Contains("seq") ? "seq" : editable.FirstOrDefault();
        if (seqKey != null)
        {
            var seq = 1;
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row[seqKey]))
                    row[seqKey] = seq.ToString("000");
                seq++;
            }
            lines.Add($"[OK] 序列号列 {seqKey} 已填充");
        }

        var exportDir = Path.Combine(App.AppDir, "logs", "exports");
        var result = _print.PrintBatch(doc, template.Id, template.Code, rows, "", PrintMode.File, exportDir);
        lines.Add($"[{(result.Success ? "OK" : "WARN")}] {result.Message.Replace("\n", " / ")}");

        using var db = _sp.GetRequiredService<AppDbContext>();
        var record = db.PrintRecords.OrderByDescending(r => r.Id).FirstOrDefault();
        if (record != null)
        {
            var items = db.PrintRecordItems.Count(i => i.RecordId == record.Id);
            lines.Add($"[OK] 记录入库：{record.JobNo} 状态={record.Status} 明细={items} 打印人={record.Username} " +
                      $"快照长度={record.TemplateSnapshot?.Length ?? 0}");
        }
        else
        {
            lines.Add("[FAIL] 未找到打印记录");
        }

        lines.Add($"[INFO] 导出文件：{Directory.GetFiles(exportDir).Length} 个（{exportDir}）");

        lines.AddRange(TestFillDown());
        return lines;
    }

    /// <summary>验证「Ctrl+点击列标题向下填充」语义：上方最近非空值填入空白格，首行空白保持空白。</summary>
    private List<string> TestFillDown()
    {
        var lines = new List<string>();
        try
        {
            var vm = _sp.GetRequiredService<ViewModels.ScanPrintViewModel>();
            if (vm.Templates.Count == 0) return lines;
            vm.SelectedTemplate = vm.Templates[0];
            var keys = vm.EditableVars.Select(v => v.Key).ToList();
            if (keys.Count == 0) return lines;

            var key = keys[0];
            vm.Table.Rows.Clear();
            foreach (var value in new[] { "A", "", "", "B", "" })
            {
                var row = vm.Table.NewRow();
                row[key] = value;
                vm.Table.Rows.Add(row);
            }

            var filled = vm.FillBlanks(key, ViewModels.BatchFillMode.CopyPrevious, "", "", 1, 1, 0);
            var actual = string.Join("|", vm.Table.Rows.Cast<System.Data.DataRow>().Select(r => r[key]?.ToString()));
            var expected = "A|A|A|B|B";
            lines.Add(filled == 3 && actual == expected
                ? $"[OK] 向下填充：填入 {filled} 格，结果 {actual}"
                : $"[FAIL] 向下填充：填入 {filled} 格，结果 {actual}，期望 {expected}");
            vm.Table.Rows.Clear();
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 向下填充测试：{ex.Message}");
        }
        return lines;
    }
}
