using System.IO;
using System.Text;
using UXLabelshop.Rendering;

namespace UXLabelshop.Services;

/// <summary>
/// .lsd 导入自检：UXLabelshop.exe lsdtest [样本目录]
/// 用真实样本验证解析（幅面/标题/变量/包围盒/图片）与模板转换（元素、可渲染、JSON 往返）。
/// 不写数据库，纯只读。
/// </summary>
public class LsdImportTestService
{
    private readonly IServiceProvider _sp;

    public LsdImportTestService(IServiceProvider sp) => _sp = sp;

    public List<string> Run(string? dir)
    {
        var lines = new List<string> { $"lsdtest @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        var pass = 0;
        var fail = 0;

        void CheckTrue(string name, bool ok, string detail = "")
        {
            lines.Add(ok ? $"[OK] {name}{detail}" : $"[FAIL] {name}{detail}");
            if (ok) pass++; else fail++;
        }

        var samples = LsdTestSamples.Resolve(dir);
        lines.Add($"[INFO] 样本目录：{samples.Directory}（{samples.Files.Count} 个 .lsd）");
        if (samples.Files.Count == 0)
        {
            lines.Add("[INFO] 未找到样本，跳过（可用 lsdtest <目录> 指定）");
            lines.Add($"---- 结果：{pass} 通过 / {fail} 失败 ----");
            return lines;
        }

        foreach (var file in samples.Files)
        {
            try
            {
                var r = LsdImportService.Parse(file);
                var shortName = Path.GetFileName(file);
                lines.Add($"[INFO] {shortName}: {r.WidthMm:0.##}×{r.HeightMm:0.##}mm, " +
                          $"标题={r.SavedTitle ?? "-"}, 变量={r.RawVariableNames.Count}, " +
                          $"包围盒={r.Boxes.Count}, 类名记录={r.ClassBoxes.Count}, 图片={r.Pictures.Count}, " +
                          $"打印机={r.PrinterHint ?? "-"}");

                CheckTrue($"{shortName} 幅面有效",
                    r.WidthMm is >= 10 and <= 300 && r.HeightMm is >= 10 and <= 300);
                CheckTrue($"{shortName} 解析出变量名", r.RawVariableNames.Count > 0,
                    $"（首项 {r.RawVariableNames.FirstOrDefault() ?? "-"}）");
                CheckTrue($"{shortName} 解析出对象包围盒", r.Boxes.Count > 0);
                CheckTrue($"{shortName} dpi 取 203 兜底", r.Dpi == 203);

                var doc = LsdImportService.BuildDoc(r, "lsd_test", "测试", r.WidthMm, r.HeightMm, r.Dpi);
                CheckTrue($"{shortName} 变量转成 TemplateVar",
                    doc.Variables.Count == r.RawVariableNames.Count);
                CheckTrue($"{shortName} 变量 key 唯一",
                    doc.Variables.Select(v => v.Key).Distinct().Count() == doc.Variables.Count);
                CheckTrue($"{shortName} 生成元素", doc.Elements.Count > 0);
                CheckTrue($"{shortName} 元素坐标在幅面内",
                    doc.Elements.All(e => e.X >= 0 && e.Y >= 0
                                          && e.X + e.W <= doc.WidthMm + 0.5
                                          && e.Y + e.H <= doc.HeightMm + 0.5));
                CheckTrue($"{shortName} JSON 往返一致",
                    TemplateDoc.Parse(doc.ToJson()).Elements.Count == doc.Elements.Count);

                // 渲染不抛异常且确实画出了墨迹
                var renderer = new LabelRenderer();
                var values = doc.Variables.ToDictionary(v => v.Key, v => (string?)("[demo]"));
                using (var bmp = renderer.RenderGray(doc, values))
                {
                    var ink = 0;
                    for (var y = 0; y < bmp.Height; y += 2)
                        for (var x = 0; x < bmp.Width; x += 2)
                            if (bmp.GetPixel(x, y).R < 128) ink++;
                    CheckTrue($"{shortName} 导入模板可渲染", ink > 0, $"（采样墨迹 {ink}）");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {Path.GetFileName(file)} 解析/转换失败：{ex.GetType().Name}: {ex.Message}");
                fail++;
            }
        }

        lines.Add($"---- 结果：{pass} 通过 / {fail} 失败 ----");
        return lines;
    }
}

/// <summary>定位 .lsd 样本目录：优先用命令行参数，否则从 exe 目录向上找 samples/。</summary>
public static class LsdTestSamples
{
    public static (string Directory, List<string> Files) Resolve(string? explicitDir)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitDir)) candidates.Add(explicitDir);

        var cur = new DirectoryInfo(App.AppDir);
        // exe 可能在 bin/Debug/net8.0-windows/win-x64 或 client/publish，向上追到盘符为止
        while (cur != null)
        {
            candidates.Add(Path.Combine(cur.FullName, "samples"));
            cur = cur.Parent;
        }

        foreach (var c in candidates)
        {
            if (!Directory.Exists(c)) continue;
            var files = Directory.GetFiles(c, "*.lsd").OrderBy(x => x).ToList();
            if (files.Count > 0) return (c, files);
        }
        return (explicitDir ?? "(未找到)", new List<string>());
    }
}
