using System.Drawing.Printing;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Printing;
using UXLabelshop.Rendering;

namespace UXLabelshop.Services;

/// <summary>
/// 打印机自检：UXLabelshop.exe printtest
/// 列出各打印机分辨率与默认纸型；若装有 PDF 虚拟打印机，再实际打一张示例标签，
/// 用于核对物理尺寸是否正确（对应 Print To PDF 曾出现的放大裁切问题）。
/// </summary>
public class PrintTestService
{
    public List<string> Run()
    {
        var lines = new List<string> { $"printtest @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };

        var names = PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        lines.Add($"[INFO] 本机打印机 {names.Count} 台");
        foreach (var name in names)
        {
            try
            {
                using var doc = new PrintDocument();
                doc.PrinterSettings.PrinterName = name;
                using var g = doc.PrinterSettings.CreateMeasurementGraphics();
                var paper = doc.PrinterSettings.DefaultPageSettings.PaperSize;
                lines.Add($"[INFO] {name}: {g.DpiX:F0}×{g.DpiY:F0}dpi, " +
                          $"PageUnit={g.PageUnit}, 默认纸型={paper.PaperName} " +
                          $"{paper.Width / 100.0 * 25.4:F0}×{paper.Height / 100.0 * 25.4:F0}mm");
            }
            catch (Exception ex)
            {
                lines.Add($"[WARN] {name}: {ex.Message}");
            }
        }

        var pdfPrinter = names.FirstOrDefault(n =>
            n.Contains("PDF", StringComparison.OrdinalIgnoreCase));
        if (pdfPrinter == null)
        {
            lines.Add("[WARN] 未找到 PDF 虚拟打印机，跳过实样打印");
            return lines;
        }

        // 取 PDF 打印机的实际分辨率，用于校验「按打印机 DPI 重渲染」是否生效
        int pdfDpi = 0;
        try
        {
            using var probe = new PrintDocument();
            probe.PrinterSettings.PrinterName = pdfPrinter;
            using var mg = probe.PrinterSettings.CreateMeasurementGraphics();
            pdfDpi = (int)Math.Round(mg.DpiX);
        }
        catch { }

        try
        {
            var tplSvc = _sp.GetRequiredService<TemplateService>();
            var tpl = tplSvc.GetRows().FirstOrDefault(t => t.Code.Contains("2115"))
                      ?? tplSvc.GetRows().First();
            var doc = tplSvc.LoadDoc(tpl.Id);
            var values = doc.Variables.Where(v => !string.IsNullOrEmpty(v.Key))
                .GroupBy(v => v.Key)
                .ToDictionary(g => g.Key, _ => (string?)"TEST12345");

            var outPath = Path.Combine(App.AppDir, "logs", "printtest.pdf");
            if (File.Exists(outPath)) File.Delete(outPath);

            var renderer = new LabelRenderer();
            using var mono = renderer.RenderMono(doc, values);
            lines.Add($"[INFO] 示例标签：{tpl.Code} {doc.WidthMm}×{doc.HeightMm}mm，" +
                      $"203dpi 源位图 {mono.Width}×{mono.Height}px");

            var result = new PrintDriver().Print(mono, doc.WidthMm, doc.HeightMm, pdfPrinter,
                dpi => renderer.RenderMono(doc, values, dpi), outPath);

            lines.Add(result.Success
                ? "[OK] 打印调用完成"
                : $"[FAIL] 打印调用失败：{result.Message}");

            if (File.Exists(outPath))
            {
                var size = new FileInfo(outPath).Length;
                lines.Add($"[OK] PDF 输出：{outPath}（{size} 字节）");

                // 完整性断言：修复前（PageUnit 错误）内嵌图是 203dpi 的 799×400；
                // 修复后应按打印机 DPI 重渲染为 widthMm/25.4*dpi 的大图。
                // 注意：Microsoft Print to PDF 会把大图切成多条横向 band（每条仍是完整宽度），
                // 因此取「最大宽度 + 各 band 高度之和」与期望比对。
                if (pdfDpi > 0)
                {
                    var expectW = (int)Math.Round(doc.WidthMm / 25.4 * pdfDpi);
                    var expectH = (int)Math.Round(doc.HeightMm / 25.4 * pdfDpi);
                    var (imgW, imgH) = ReadPdfImageSize(outPath);
                    if (imgW > 0 && imgH > 0)
                    {
                        var ok = Math.Abs(imgW - expectW) <= expectW * 0.1 &&
                                 Math.Abs(imgH - expectH) <= expectH * 0.1;
                        lines.Add(ok
                            ? $"[CHECK] 标签尺寸正确：PDF 内嵌图（含分条）{imgW}×{imgH} ≈ 期望 {expectW}×{expectH}（{pdfDpi}dpi），无放大裁切"
                            : $"[FAIL] 标签尺寸异常：PDF 内嵌图 {imgW}×{imgH}，期望 {expectW}×{expectH}（{pdfDpi}dpi）—— 仍可能被拉伸/裁切");
                    }
                }
            }
            else
            {
                lines.Add("[WARN] 未生成 PDF 文件（驱动可能仍要求手动选择保存位置）");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 实样打印：{ex.GetType().Name}: {ex.Message}");
        }

        return lines;
    }

    /// <summary>
    /// 从 PDF 读取所有图像 XObject 的像素尺寸，返回（最大宽度, 各图高度之和）。
    /// 用 ASCII 解析即可（PDF 结构标记是 ASCII，二进制图像流部分忽略）；
    /// Microsoft Print to PDF 会把大图切成多条横向 band（每条仍是完整宽度），
    /// 故宽度取最大、高度取总和，才能拼出完整标签。
    /// </summary>
    private static (int w, int h) ReadPdfImageSize(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            var maxW = 0;
            var sumH = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         text, @"\d+\s+0\s+obj\s*<<(?<dict>.*?)>>\s*stream",
                         System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                var dict = m.Groups["dict"].Value;
                if (!dict.Contains("/Subtype /Image") && !dict.Contains("/Subtype/Image")) continue;
                var wm = System.Text.RegularExpressions.Regex.Match(dict, @"/Width\s+(\d+)");
                var hm = System.Text.RegularExpressions.Regex.Match(dict, @"/Height\s+(\d+)");
                if (wm.Success && hm.Success)
                {
                    maxW = Math.Max(maxW, int.Parse(wm.Groups[1].Value));
                    sumH += int.Parse(hm.Groups[1].Value);
                }
            }
            return (maxW, sumH);
        }
        catch { }
        return (0, 0);
    }

    private readonly IServiceProvider _sp;
    public PrintTestService(IServiceProvider sp) => _sp = sp;
}
