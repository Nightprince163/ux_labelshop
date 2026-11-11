using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Rendering;

namespace UXLabelshop.Services;

/// <summary>
/// 模板修正：UXLabelshop tplfix [目录]
/// 把目录下的 *.fixed.json 覆盖写回数据库（变量表整体替换 + 生成新版本，保持原发布状态）。
/// 用于修正 gen_templates.py 早期版本把「变量名」当标签标题生成出来的模板。
/// </summary>
public class TemplateFixService
{
    private readonly IServiceProvider _sp;

    public TemplateFixService(IServiceProvider sp) => _sp = sp;

    public List<string> Run(string? dir)
    {
        var lines = new List<string> { $"tplfix @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            lines.Add($"[FAIL] 目录不存在：{dir}");
            return lines;
        }

        var files = Directory.GetFiles(dir, "*.fixed.json").OrderBy(f => f).ToList();
        if (files.Count == 0)
        {
            lines.Add($"[WARN] {dir} 下没有 *.fixed.json");
            return lines;
        }

        var templates = _sp.GetRequiredService<TemplateService>();
        var userId = _sp.GetRequiredService<AppSession>().CurrentUser?.Id;
        var ok = 0;
        var fail = 0;

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                var json = File.ReadAllText(file);
                var doc = TemplateDoc.Parse(json);
                var code = doc.Id;
                if (string.IsNullOrWhiteSpace(code))
                {
                    lines.Add($"[FAIL] {name}：缺少 id 字段");
                    fail++;
                    continue;
                }

                var row = templates.GetRows().FirstOrDefault(t => t.Code == code);
                if (row == null)
                {
                    lines.Add($"[FAIL] {name}：数据库里没有 code={code} 的模板");
                    fail++;
                    continue;
                }

                var before = templates.LoadDoc(row.Id);
                var beforeRows = before.Elements.Count(e => e.Type == "text" && e.Text?.Contains("{{") == true);
                var beforeCaps = string.Join(" | ", before.Elements
                    .Where(e => e.Type == "text" && e.Text != null && !e.Text.Contains("{{"))
                    .Select(e => e.Text!.TrimEnd('：', ':')));

                templates.SaveVariables(row.Id, doc.Variables);
                templates.SaveVersion(row.Id, doc, userId, "按 .lsd 真实标题重建（修正把变量名当标题的问题）");

                var afterRows = doc.Elements.Count(e => e.Type == "text" && e.Text?.Contains("{{") == true);
                var afterCaps = string.Join(" | ", doc.Elements
                    .Where(e => e.Type == "text" && e.Text != null && e.Text.Contains("{{"))
                    .Select(e => e.Text!.Split("{{")[0].TrimEnd('：', ':')));
                lines.Add($"[OK] {code}（{row.Name}）：{beforeRows} 行 → {afterRows} 行，变量 {doc.Variables.Count} 个");
                lines.Add($"     旧：{beforeCaps}");
                lines.Add($"     新：{afterCaps}");
                ok++;
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {name}：{ex.GetType().Name}: {ex.Message}");
                fail++;
            }
        }

        lines.Add($"---- 结果：{ok} 个已写库 / {fail} 个失败 ----");
        return lines;
    }
}
