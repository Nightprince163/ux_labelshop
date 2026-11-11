using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;

namespace UXLabelshop.Rendering;

/// <summary>Pb 标志的字母样式（衬线体候选）。</summary>
public enum PbFreeStyle
{
    /// <summary>Times New Roman 常规</summary>
    TimesRegular,
    /// <summary>Times New Roman 粗体（默认，印刷观感最接近原标签）</summary>
    TimesBold,
    /// <summary>宋体 粗体（西文部分也是 Times 风格，中文系统一定有）</summary>
    SimSunBold,
    /// <summary>宋体 常规</summary>
    SimSunRegular,
}

/// <summary>
/// 内置矢量符号库（不依赖外部图片、不依赖字体文件分发，可 1:1 输出点阵直接打印）。
///
/// 坐标统一用「归一化单位」：外接正方形边长 = 1，圆心在 (0,0)；渲染时乘以目标像素边长。
/// 为了小尺寸（7mm@203dpi ≈ 56px）下衬线笔画不断线，内部按 4 倍超采样绘制后降采样。
/// </summary>
public static class Symbols
{
    /// <summary>已支持的符号（模板元素 type=symbol 时把 Key 写在 text 字段）。</summary>
    public static readonly IReadOnlyList<(string Key, string Title)> Catalog = new[]
    {
        ("pb-free", "Pb 无铅标志"),
    };

    /// <summary>Pb 标志的字母样式，默认 Times New Roman 粗体。</summary>
    public static PbFreeStyle Style { get; set; } = PbFreeStyle.SimSunBold;

    public static bool IsKnown(string? key) =>
        !string.IsNullOrWhiteSpace(key) && Catalog.Any(c => c.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Title(string? key) =>
        Catalog.FirstOrDefault(c => c.Key.Equals((key ?? "").Trim(), StringComparison.OrdinalIgnoreCase)).Title ?? "(未知符号)";

    // ---------- Pb 无铅标志：比例常量 ----------
    // 参照 label-system-kit/samples/样品图_UX2115.jpg 实测：
    // 圆环外径 = 外接正方形 100%、环宽 7.5%；字母为衬线体、宽约 72%、高约 50%；
    // 斜杠 45°（左上→右下），两端顶进圆环，不留缝。
    private const double RingStroke = 0.042;
    private const double SlashStroke = 0.046;
    private const double SlashEnd = 0.480;          // 端点半径：外缘 0.5、环内缘 0.425 → 0.48 稳埋在环里
    private const double LetterWidth = 0.615;       // 字母墨迹最大宽度（占边长）
    private const double LetterHeight = 0.555;      // 字母墨迹最大高度
    private const double LetterNudge = 0.0;      // 视觉居中：略微上移
    private const double Diag = 0.7071067811865476; // cos45°

    private static readonly Dictionary<PbFreeStyle, FontFamily> FamilyCache = new();

    /// <summary>在指定矩形内绘制符号（保持正方形比例并居中）。</summary>
    public static void Draw(Graphics g, string? key, double x, double y, double w, double h)
    {
        if (!IsKnown(key) || w <= 0 || h <= 0) return;
        var side = Math.Min(w, h);
        if (side < 4) return;                       // 太小画不出可辨识轮廓，直接留白
        var left = x + (w - side) / 2.0;
        var top = y + (h - side) / 2.0;

        // 超采样：小尺寸下衬线细节靠这一步保住
        var ss = side < 200 ? 4 : side < 500 ? 3 : 2;
        var px = Math.Max(8, (int)Math.Ceiling(side * ss));

        var oldSmooth = g.SmoothingMode;
        var oldPixel = g.PixelOffsetMode;
        var oldInterp = g.InterpolationMode;
        using var layer = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var lg = Graphics.FromImage(layer))
        {
            lg.Clear(Color.Transparent);
            lg.SmoothingMode = SmoothingMode.AntiAlias;
            lg.PixelOffsetMode = PixelOffsetMode.Half;
            lg.TextRenderingHint = TextRenderingHint.AntiAlias;
            DrawPbFree(lg, px / 2.0, px / 2.0, px);
        }

        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(layer, new RectangleF((float)left, (float)top, (float)side, (float)side));
        }
        finally
        {
            g.SmoothingMode = oldSmooth;
            g.PixelOffsetMode = oldPixel;
            g.InterpolationMode = oldInterp;
        }
    }

    private static void DrawPbFree(Graphics g, double cx, double cy, double side)
    {
        float Px(double u) => (float)(u * side);

        // 1) 圆环：外缘 0.5×边长；GDI+ 以路径为中心描边 → 半径取 0.5 - 半笔宽
        using (var ringPen = new Pen(Color.Black, Px(RingStroke)) { LineJoin = LineJoin.Round })
        {
            var r = Px(0.5 - RingStroke / 2);
            g.DrawEllipse(ringPen, (float)cx - r, (float)cy - r, r * 2, r * 2);
        }

        // 2) 字母 "Pb"：衬线体轮廓（GraphicsPath.AddString），矢量填充
        using (var path = BuildLetterPath(cx, cy, side))
        using (var brush = new SolidBrush(Color.Black))
            g.FillPath(brush, path);

        // 3) 斜杠：左上 → 右下（"\"），两端顶进圆环；画在字母之上，和原标签一致
        using (var slashPen = new Pen(Color.Black, Px(SlashStroke)) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
        {
            var e = SlashEnd * Diag;
            g.DrawLine(slashPen,
                new PointF((float)(cx - e * side), (float)(cy - e * side)),
                new PointF((float)(cx + e * side), (float)(cy + e * side)));
        }
    }

    /// <summary>
    /// 生成 "Pb" 的矢量轮廓，按 <see cref="LetterWidth"/> / <see cref="LetterHeight"/> 缩放并居中到 (cx,cy)。
    /// 用轮廓而非 DrawString，是为了拿到确定的墨迹边界（居中/缩放精确）且不经文字 hinting。
    /// </summary>
    private static GraphicsPath BuildLetterPath(double cx, double cy, double side)
    {
        var family = Family(Style);
        var style = Style is PbFreeStyle.TimesRegular or PbFreeStyle.SimSunRegular
            ? FontStyle.Regular
            : FontStyle.Bold;

        var path = new GraphicsPath();
        path.AddString("Pb", family, (int)style, 100f, PointF.Empty, StringFormat.GenericTypographic);

        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return path;

        var scale = Math.Min(LetterWidth * side / b.Width, LetterHeight * side / b.Height);
        var w = b.Width * scale;
        var h = b.Height * scale;
        var tx = cx - w / 2;                             // 先把墨迹平移到原点，再按墨迹框居中
        var ty = cy - h / 2 + LetterNudge * side;

        path.Transform(new Matrix(1, 0, 0, 1, -b.X, -b.Y));      // 墨迹左上角 → (0,0)
        path.Transform(new Matrix((float)scale, 0, 0, (float)scale, 0, 0));
        path.Transform(new Matrix(1, 0, 0, 1, (float)tx, (float)ty));
        return path;
    }

    private static FontFamily Family(PbFreeStyle style)
    {
        if (FamilyCache.TryGetValue(style, out var cached)) return cached;

        var preferred = style switch
        {
            PbFreeStyle.SimSunBold or PbFreeStyle.SimSunRegular => new[] { "SimSun", "宋体", "Times New Roman", "Georgia" },
            _ => new[] { "Times New Roman", "Georgia", "SimSun" },
        };
        FontFamily family;
        try
        {
            family = preferred.Select(n => new FontFamily(n)).First();
        }
        catch
        {
            family = FontFamily.GenericSerif;
        }
        FamilyCache[style] = family;
        return family;
    }

    /// <summary>离线渲染符号为位图（供预览、样张、自检用）。</summary>
    public static Bitmap Render(string key, int sidePx, bool mono = false)
    {
        var bmp = new Bitmap(sidePx, sidePx, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        Draw(g, key, 0, 0, sidePx, sidePx);
        if (!mono) return bmp;
        try
        {
            return LabelRenderer.ToMono(bmp);
        }
        finally
        {
            bmp.Dispose();
        }
    }

    /// <summary>SVG 输出（同一份几何与字体轮廓，供文档/网页/其它系统使用）。</summary>
    public static string ToSvg(string key = "pb-free", int size = 200)
    {
        if (!IsKnown(key)) return "";

        static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        var e = SlashEnd * Diag;
        // viewBox 0..100：圆心 (50,50)，边长 100
        var ringR = 50 - RingStroke * 50;
        using var letters = BuildLetterPath(50, 50, 100);

        var sb = new StringBuilder();
        sb.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 100 100" role="img" aria-label="Pb free">""");
        sb.AppendLine("  <!-- Pb 无铅标志：圆环 + 衬线 Pb + 45° 斜杠（两端顶进圆环），几何与客户端渲染代码同源 -->");
        sb.AppendLine($"""  <g fill="#000">""");
        sb.AppendLine($"""    <circle cx="50" cy="50" r="{F(ringR)}" fill="none" stroke="#000" stroke-width="{F(RingStroke * 100)}"/>""");
        sb.AppendLine($"""    <line x1="{F(50 - e * 100)}" y1="{F(50 - e * 100)}" x2="{F(50 + e * 100)}" y2="{F(50 + e * 100)}" stroke="#000" stroke-width="{F(SlashStroke * 100)}" stroke-linecap="butt"/>""");
        sb.AppendLine($"""    <path d="{PathData(letters)}"/>""");
        sb.AppendLine("  </g>");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    /// <summary>GraphicsPath → SVG path 的 d 属性（M/L/C/Z）。</summary>
    private static string PathData(GraphicsPath path)
    {
        static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var pts = path.PathPoints;
        var types = path.PathTypes;
        var sb = new StringBuilder();

        var i = 0;
        while (i < pts.Length)
        {
            var t = types[i] & 0x07;
            var last = i;
            switch (t)
            {
                case 0: // Start
                    sb.Append($"M {F(pts[i].X)} {F(pts[i].Y)} ");
                    i++;
                    break;
                case 1: // Line
                    sb.Append($"L {F(pts[i].X)} {F(pts[i].Y)} ");
                    i++;
                    break;
                case 3: // Bezier（3 个点）
                    if (i + 2 >= pts.Length) { i = pts.Length; break; }
                    sb.Append($"C {F(pts[i].X)} {F(pts[i].Y)} {F(pts[i + 1].X)} {F(pts[i + 1].Y)} {F(pts[i + 2].X)} {F(pts[i + 2].Y)} ");
                    last = i + 2;
                    i += 3;
                    break;
                default:
                    i++;
                    break;
            }
            if ((types[last] & 0x80) != 0) sb.Append("Z ");
        }
        return sb.ToString().Trim();
    }
}
