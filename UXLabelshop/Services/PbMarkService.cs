using System.IO;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Models;
using UXLabelshop.Rendering;

namespace UXLabelshop.Services;

/// <summary>
/// Pb 无铅标志：从 .lsd 自动识别位置 → 写入模板，并把内置矢量标志渲染成本地样张。
///
/// 命令：UXLabelshop pbmark [lsd目录] [--apply]
///   - 不带 --apply：只做体检（列每个 .lsd 的图片对象 + 与模板的对应关系 + 渲染标志样张）
///   - 带 --apply  ：把标志元素补进数据库模板（已有则跳过），每个模板存一个新版本
/// </summary>
public class PbMarkService
{
    public const string SymbolKey = "pb-free";

    private readonly IServiceProvider _sp;
    public PbMarkService(IServiceProvider sp) => _sp = sp;

    public List<string> Run(string? lsdDir, bool apply)
    {
        var lines = new List<string> { $"pbmark @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}  apply={apply}" };

        // 1) 内置矢量标志样张（不同尺寸，肉眼检查在点阵上是否还认得出）
        try
        {
            lines.AddRange(RenderSamples(Path.Combine(App.AppDir, "logs", "pb")));
        }
        catch (Exception ex)
        {
            lines.Add($"[FAIL] 渲染标志样张：{ex.GetType().Name}: {ex.Message}");
        }

        // 2) 扫描 .lsd
        var found = new List<(string File, string Stem, LsdPicture Pic)>();
        if (string.IsNullOrWhiteSpace(lsdDir) || !Directory.Exists(lsdDir))
        {
            lines.Add($"[WARN] 未找到 .lsd 目录：{lsdDir ?? "(未提供)"}，跳过识别");
        }
        else
        {
            lines.Add($"— 扫描 {lsdDir}");
            foreach (var path in Directory.EnumerateFiles(lsdDir, "*.lsd").OrderBy(p => p))
            {
                var name = Path.GetFileName(path);
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    var page = LsdReader.TryReadPageSize(bytes);
                    var pageText = page is { } pg ? $"幅面 {pg.W / 100.0:0.#}×{pg.H / 100.0:0.#}mm" : "幅面未知";
                    var pics = LsdReader.FindPictures(bytes);
                    if (pics.Count == 0)
                    {
                        lines.Add($"  {name,-34} {pageText}  无图片对象（原标签没有 Pb 标志）");
                        continue;
                    }
                    foreach (var p in pics)
                    {
                        lines.Add($"  {name,-34} {pageText}  图片 @{p.XMm:0.00},{p.YMm:0.00} {p.WMm:0.00}×{p.HMm:0.00}mm"
                                  + (p.LikelyPbMark ? "  → Pb 无铅标志" : "  → 非标志图片（尺寸不符）"));
                        if (p.LikelyPbMark) found.Add((name, Path.GetFileNameWithoutExtension(path), p));
                    }
                }
                catch (Exception ex)
                {
                    lines.Add($"  {name,-34} [FAIL] {ex.Message}");
                }
            }
        }

        // 3) 与模板对号入座
        using var db = _sp.GetRequiredService<AppDbContext>();
        var templates = db.LabelTemplates.OrderBy(t => t.Code).ToList();
        var plan = new List<(LabelTemplate Tpl, LsdPicture Pic, string Reason)>();
        foreach (var t in templates)
        {
            var hit = Match(t, found);
            if (hit.Pic == null)
            {
                lines.Add($"  模板 {t.Code,-12} {t.Name,-22} 未匹配到带 Pb 标志的 .lsd（source={(t.SourceFile ?? "-")}）");
                continue;
            }
            lines.Add($"  模板 {t.Code,-12} {t.Name,-22} ← {hit.File}（{hit.Reason}）");
            plan.Add((t, hit.Pic!, hit.Reason));
        }

        if (!apply)
        {
            lines.Add($"[OK] 体检完成：{found.Count} 个 .lsd 含 Pb 标志，{plan.Count} 个模板待处理（未写库）");
            return lines;
        }

        // 4) 写入模板（幂等）
        var templatesSvc = _sp.GetRequiredService<TemplateService>();
        var userId = _sp.GetRequiredService<AppSession>().CurrentUser?.Id;
        var changed = 0;
        foreach (var (tpl, pic, _) in plan)
        {
            try
            {
                var doc = templatesSvc.LoadDoc(tpl.Id);
                var exist = doc.Elements.FirstOrDefault(e =>
                    string.Equals(e.Type, "symbol", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Text, SymbolKey, StringComparison.OrdinalIgnoreCase));

                // .lsd 坐标是「原标签」的坐标；我们重建的模板排版可能略有出入，落点若压到文字/条码就就近挪开
                var (px, py, moved) = Place(doc, pic, exist);
                var where = moved ? $"（原 .lsd 位置 {pic.XMm},{pic.YMm} 被占用，已就近移到 {px},{py}）" : "";

                if (exist != null)
                {
                    if (Same(exist, px, py, pic))
                    {
                        lines.Add($"  {tpl.Code}: 标志已在正确位置（{px},{py} {pic.WMm}×{pic.HMm}mm），跳过");
                        continue;
                    }
                    exist.X = px;
                    exist.Y = py;
                    exist.W = pic.WMm;
                    exist.H = pic.HMm;
                    lines.Add($"  {tpl.Code}: 已有标志，刷新为 {px},{py} {pic.WMm}×{pic.HMm}mm{where}");
                }
                else
                {
                    doc.Elements.Add(new TemplateElement
                    {
                        Type = "symbol",
                        Text = SymbolKey,
                        X = px,
                        Y = py,
                        W = pic.WMm,
                        H = pic.HMm,
                    });
                    lines.Add($"  {tpl.Code}: 新增 Pb 标志 @ {px},{py} {pic.WMm}×{pic.HMm}mm{where}");
                }
                templatesSvc.SaveVersion(tpl.Id, doc, userId, $"{SymbolKey} 自动识别（来源 .lsd）");
                changed++;
            }
            catch (Exception ex)
            {
                lines.Add($"  {tpl.Code}: [FAIL] {ex.GetType().Name}: {ex.Message}");
            }
        }
        lines.Add($"[OK] 已写入 {changed} 个模板（每个模板一个新版本，历史版本不受影响）");
        return lines;
    }

    /// <summary>坐标是否已一致（0.05mm 容差，避免无意义地新存版本）。</summary>
    private static bool Same(TemplateElement el, double x, double y, LsdPicture pic) =>
        Math.Abs(el.X - x) < 0.05 && Math.Abs(el.Y - y) < 0.05
        && Math.Abs(el.W - pic.WMm) < 0.05 && Math.Abs(el.H - pic.HMm) < 0.05;

    /// <summary>
    /// 决定标志落点：优先用 .lsd 的原坐标；若压在其它元素上（重建模板排版有出入时会发生），
    /// 以 1.5mm 步长向外螺旋找最近的空位，最后兜底从右下角扫描。
    /// </summary>
    private static (double X, double Y, bool Moved) Place(TemplateDoc doc, LsdPicture pic, TemplateElement? self)
    {
        var w = pic.WMm;
        var h = pic.HMm;

        bool Free(double x, double y)
        {
            if (x < 1 || y < 1 || x + w > doc.WidthMm - 1 || y + h > doc.HeightMm - 1) return false;
            foreach (var el in doc.Elements)
            {
                if (ReferenceEquals(el, self) || el.Type == "symbol") continue;
                if (Overlaps(el, x, y, w, h)) return false;
            }
            return true;
        }

        if (Free(pic.XMm, pic.YMm)) return (pic.XMm, pic.YMm, false);

        (double Dx, double Dy)[] dirs =
        {
            (1, 0), (-1, 0), (0, -1), (0, 1),
            (1, -1), (1, 1), (-1, -1), (-1, 1),
        };
        for (var r = 1.5; r <= 15.0; r += 1.5)
            foreach (var (dx, dy) in dirs)
            {
                var x = Math.Round(pic.XMm + dx * r, 2);
                var y = Math.Round(pic.YMm + dy * r, 2);
                if (Free(x, y)) return (x, y, true);
            }

        // 兜底：从右下角往左上扫，找第一块空地
        for (var y = Math.Round(doc.HeightMm - h - 1, 2); y >= 1; y -= 1)
            for (var x = Math.Round(doc.WidthMm - w - 1, 2); x >= 1; x -= 1)
                if (Free(x, y)) return (x, y, true);

        return (pic.XMm, pic.YMm, false);   // 实在没地方，保持原坐标
    }

    /// <summary>元素包围盒与「标志框 + 0.4mm 间隙」是否相交。</summary>
    private static bool Overlaps(TemplateElement el, double x, double y, double w, double h)
    {
        const double pad = 0.4;
        var x1 = Math.Min(el.X, el.X2 ?? el.X);
        var y1 = Math.Min(el.Y, el.Y2 ?? el.Y);
        var x2 = Math.Max(el.X, el.X2 ?? el.X) + (el.Type == "line" ? 0 : el.W);
        var y2 = Math.Max(el.Y, el.Y2 ?? el.Y) + (el.Type == "line" ? 0 : el.H);
        return x1 < x + w + pad && x2 > x - pad && y1 < y + h + pad && y2 > y - pad;
    }

    /// <summary>模板 ↔ .lsd：先看 source_file，再退回编码/名称包含关系。</summary>
    private static (string File, LsdPicture? Pic, string Reason) Match(
        LabelTemplate t, List<(string File, string Stem, LsdPicture Pic)> found)
    {
        if (found.Count == 0) return ("", null, "");

        if (!string.IsNullOrWhiteSpace(t.SourceFile))
        {
            var bySource = found.FirstOrDefault(f =>
                string.Equals(f.File, t.SourceFile, StringComparison.OrdinalIgnoreCase));
            if (bySource.Pic != null) return (bySource.File, bySource.Pic, "source_file");
        }

        static string Norm(string s) => s.Replace("_", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();

        var byCode = found.FirstOrDefault(f => Norm(f.Stem).Contains(Norm(t.Code)));
        if (byCode.Pic != null) return (byCode.File, byCode.Pic, "编码匹配");

        var byName = found.FirstOrDefault(f => Norm(f.Stem).Contains(Norm(t.Name)));
        if (byName.Pic != null) return (byName.File, byName.Pic, "名称匹配");

        return ("", null, "");
    }

    /// <summary>渲染标志样张（RGB + 1bpp 点阵），供人工确认小尺寸下是否清晰。</summary>
    public static List<string> RenderSamples(string outDir)
    {
        var lines = new List<string>();
        Directory.CreateDirectory(outDir);

        // 7mm@203dpi ≈ 56px（真机标签上的实际大小），另加 28 / 112 / 300 三档观察
        foreach (var px in new[] { 28, 56, 112, 300 })
        {
            using var color = Symbols.Render(SymbolKey, px);
            color.Save(Path.Combine(outDir, $"pb-free-{px}px.png"), System.Drawing.Imaging.ImageFormat.Png);
            using var mono = Symbols.Render(SymbolKey, px, mono: true);
            mono.Save(Path.Combine(outDir, $"pb-free-{px}px-1bpp.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(outDir, "pb-free.svg"), Symbols.ToSvg(SymbolKey), new UTF8Encoding(false));

        // 字体候选样张（供人工挑选字母样式）
        var keep = Symbols.Style;
        foreach (PbFreeStyle st in Enum.GetValues<PbFreeStyle>())
        {
            Symbols.Style = st;
            using var big = Symbols.Render(SymbolKey, 300);
            big.Save(Path.Combine(outDir, $"style-{st}-300px.png"), System.Drawing.Imaging.ImageFormat.Png);
            using var small = Symbols.Render(SymbolKey, 56);
            small.Save(Path.Combine(outDir, $"style-{st}-56px.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        Symbols.Style = keep;

        lines.Add($"[OK] 标志样张 → {outDir}（28/56/112/300px + svg）");

        // 实心像素占比粗检：太小说明没画出来，太大说明糊成一团
        using var probe = Symbols.Render(SymbolKey, 56, mono: true);
        var black = CountInk(probe);
        var share = black / (double)(probe.Width * probe.Height);
        lines.Add($"  56px 墨点占比 {share:P1}（参考：圆环+字母+斜杠约 25%~45%）");
        return lines;
    }

    private static int CountInk(System.Drawing.Bitmap mono)
    {
        var count = 0;
        var rect = new System.Drawing.Rectangle(0, 0, mono.Width, mono.Height);
        var data = mono.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format1bppIndexed);
        try
        {
            var line = new byte[data.Stride];
            for (var y = 0; y < mono.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, line, 0, data.Stride);
                for (var x = 0; x < mono.Width; x++)
                    if ((line[x >> 3] & (0x80 >> (x & 7))) == 0) count++;   // 0 = 黑
            }
        }
        finally
        {
            mono.UnlockBits(data);
        }
        return count;
    }
}
