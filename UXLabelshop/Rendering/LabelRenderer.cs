using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using QRCoder;

namespace UXLabelshop.Rendering;

/// <summary>
/// 标签渲染引擎：模板 JSON + 数据 → 1:1 点阵位图（1bpp，1=白/0=黑）。
/// 坐标单位毫米，原点左上角；px = mm / 25.4 * dpi，与参考实现一致。
/// 元素类型：text / barcode / qrcode / rect / line / image / symbol。
/// </summary>
public class LabelRenderer
{
    private static readonly Regex VarRegex = new(@"\{\{\s*([A-Za-z0-9_\-]+)\s*\}\}", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> FontMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["微软雅黑"] = "Microsoft YaHei",
        ["微软雅黑粗体"] = "Microsoft YaHei",
        ["宋体"] = "SimSun",
        ["黑体"] = "SimHei",
        ["楷体"] = "KaiTi",
        ["等线"] = "DengXian",
        ["仿宋"] = "FangSong",
        ["arial"] = "Arial",
        ["arialbd"] = "Arial",
        ["consolas"] = "Consolas",
        ["courier"] = "Courier New",
    };

    private readonly Dictionary<(string, float, bool, bool), Font> _fontCache = new();

    // ---------- 公开接口 ----------

    /// <summary>渲染一张标签为 1bpp 单色位图（黑白纯点阵，供打印）。</summary>
    public Bitmap RenderMono(TemplateDoc doc, IDictionary<string, string?>? values, int? dpi = null)
    {
        var gray = RenderGray(doc, values, dpi);
        try
        {
            return ToMono(gray);
        }
        finally
        {
            gray.Dispose();
        }
    }

    /// <summary>渲染为 24bpp 灰度/彩色位图（供预览或导出）。</summary>
    public Bitmap RenderGray(TemplateDoc doc, IDictionary<string, string?>? values, int? dpi = null)
    {
        var d = dpi ?? doc.Dpi;
        var dpm = d / 25.4;
        var wPx = Math.Max(1, (int)Math.Round(doc.WidthMm * dpm));
        var hPx = Math.Max(1, (int)Math.Round(doc.HeightMm * dpm));

        var merged = MergeFixed(doc, values);
        var bmp = new Bitmap(wPx, hPx, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

        foreach (var el in doc.Elements)
        {
            try
            {
                switch (el.Type)
                {
                    case "text": DrawText(g, el, merged, dpm); break;
                    case "barcode": DrawBarcode(g, el, merged, dpm); break;
                    case "qrcode": DrawQr(g, el, merged, dpm); break;
                    case "rect": DrawRect(g, el, dpm); break;
                    case "line": DrawLine(g, el, dpm); break;
                    case "image": DrawImage(g, el, dpm); break;
                    case "symbol": Symbols.Draw(g, el.Text, el.X * dpm, el.Y * dpm, el.W * dpm, el.H * dpm); break;
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "元素 {Type} 渲染失败", el.Type);
            }
        }

        return bmp;
    }

    /// <summary>预览用 PNG（2 倍最近邻放大，前端显示更清楚）。</summary>
    public byte[] RenderPreviewPng(TemplateDoc doc, IDictionary<string, string?>? values, int scale = 2)
    {
        using var mono = RenderMono(doc, values);
        using var bmp = scale == 1 ? (Bitmap)mono.Clone() : Upscale(mono, scale);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static Bitmap Upscale(Bitmap src, int scale)
    {
        var dst = new Bitmap(src.Width * scale, src.Height * scale, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height));
        return dst;
    }

    // ---------- 变量 ----------

    /// <summary>模板里 source=fixed 的变量（如 MSL、固定包装数）渲染时自动补。</summary>
    public static Dictionary<string, string?> FixedValues(TemplateDoc doc) => FixedValues(doc.Variables);

    public static Dictionary<string, string?> FixedValues(IEnumerable<TemplateVar> vars)
    {
        var result = new Dictionary<string, string?>();
        foreach (var v in vars)
        {
            if (!v.IsFixed || string.IsNullOrEmpty(v.Key)) continue;
            var val = FirstNonEmpty(v.Value, v.DefaultValue, v.Sample);
            result[v.Key] = val;
        }
        return result;
    }

    private static string? FirstNonEmpty(params string?[] xs) => xs.FirstOrDefault(x => !string.IsNullOrEmpty(x));

    public static Dictionary<string, string?> MergeFixed(TemplateDoc doc, IDictionary<string, string?>? values)
    {
        var merged = FixedValues(doc);
        if (values != null)
            foreach (var (k, v) in values)
                if (v != null) merged[k] = v;
        // 补齐缺省值与「调用方未传入」的计算列（编辑器预览/试打/自检只有样例值）。
        // 清单里已经算好、甚至被用户手改过的计算列不会被覆盖。
        new VariableResolver(doc.Variables).FillMissing(merged);
        return merged;
    }

    public static string Fill(string? text, IDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return VarRegex.Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : "");
    }

    // ---------- 各元素绘制 ----------

    private void DrawText(Graphics g, TemplateElement el, IDictionary<string, string?> values, double dpm)
    {
        var txt = Fill(el.Text, values);
        if (txt.Length == 0) return;

        // 字号优先取 font_size（磅，1pt = 1/72 英寸）；旧模板无此字段时仍用 size_mm 字高，逐像素不变。
        // dpm = dpi/25.4，故 pt → px = pt * dpm * 25.4/72。
        var sizePx = el.FontSize is { } pt && pt > 0
            ? (float)Math.Max(6, Math.Round(pt * dpm * 25.4 / 72.0))
            : (float)Math.Max(6, Math.Round((el.SizeMm ?? 2.5) * dpm));
        var x = (float)(el.X * dpm);
        var y = (float)(el.Y * dpm);
        var w = (float)(el.W * dpm);
        var h = (float)(el.H * dpm);
        if (w <= 0) w = g.VisibleClipBounds.Width - x;
        if (h <= 0) h = sizePx * 1.4f;

        var bold = el.Bold == true;
        var italic = el.Italic == true;
        var wrap = el.WordWrap == true;

        var (font, fitted) = wrap
            ? FitWrapped(txt, el.Font, sizePx, w, h, bold, italic)
            : FitText(txt, el.Font, sizePx, w, bold, italic);
        try
        {
            using var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                // 自动换行：允许按宽度折行，只画完整行且裁剪在元素框内；
                // 关闭时维持原有单行行为（NoWrap + 缩小字号/截断），旧模板输出不变。
                FormatFlags = wrap
                    ? StringFormatFlags.LineLimit
                    : StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
                Alignment = (el.Align ?? "left") switch
                {
                    "center" => StringAlignment.Center,
                    "right" => StringAlignment.Far,
                    _ => StringAlignment.Near,
                },
                LineAlignment = (el.Valign ?? "middle") switch
                {
                    "top" => StringAlignment.Near,
                    "bottom" => StringAlignment.Far,
                    _ => StringAlignment.Center,
                },
            };
            using var brush = new SolidBrush(Color.Black);
            g.DrawString(fitted, font, brush, new RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)), sf);
        }
        finally
        {
            // 字体来自缓存，不在此释放
        }
    }

    private (Font Font, string Text) FitText(string text, string? fontName, float sizePx, float maxWidth,
        bool bold, bool italic)
    {
        var font = GetFont(fontName, sizePx, bold, italic);
        if (maxWidth <= 0) return (font, text);

        var width = Measure(font, text);
        if (width <= maxWidth) return (font, text);

        // 1) 等比缩小字号
        var ratio = maxWidth / Math.Max(1f, width);
        var smaller = GetFont(fontName, Math.Max(6, (float)(sizePx * ratio * 0.98)), bold, italic);
        if (Measure(smaller, text) <= maxWidth) return (smaller, text);

        // 2) 仍超宽 → 截断加省略号
        const string ellipsis = "…";
        var output = "";
        foreach (var ch in text)
        {
            if (Measure(smaller, output + ch + ellipsis) > maxWidth) break;
            output += ch;
        }
        return (smaller, output.Length > 0 ? output + ellipsis : text[..1]);
    }

    /// <summary>自动换行：按元素宽度折行，若总行高超出元素高度则等比缩小字号（下限 6px）。</summary>
    private (Font Font, string Text) FitWrapped(string text, string? fontName, float sizePx,
        float maxWidth, float maxHeight, bool bold, bool italic)
    {
        var font = GetFont(fontName, sizePx, bold, italic);
        if (maxWidth <= 0 || maxHeight <= 0) return (font, text);

        for (var i = 0; i < 12 && sizePx > 6; i++)
        {
            var needed = MeasureWrapped(font, text, maxWidth);
            if (needed <= maxHeight) break;
            sizePx = Math.Max(6, sizePx * 0.88f);
            font = GetFont(fontName, sizePx, bold, italic);
        }
        return (font, text);
    }

    private static float MeasureWrapped(Font font, string text, float maxWidth)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.LineLimit,
        };
        // 按指定宽度排版后返回所需高度（含自动折行与显式 \n）
        return g.MeasureString(text, font, (int)Math.Max(1, maxWidth), sf).Height;
    }

    private static float Measure(Font font, string text)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        return g.MeasureString(text, font, new PointF(0, 0), StringFormat.GenericTypographic).Width;
    }

    private Font GetFont(string? name, float sizePx, bool bold, bool italic)
    {
        var key = (name ?? "微软雅黑", (float)Math.Round(sizePx), bold, italic);
        if (_fontCache.TryGetValue(key, out var cached)) return cached;

        var family = name != null && FontMap.TryGetValue(name, out var mapped) ? mapped : (name ?? "Microsoft YaHei");
        var style = FontStyle.Regular;
        if (bold) style |= FontStyle.Bold;
        if (italic) style |= FontStyle.Italic;
        Font font;
        try
        {
            font = new Font(family, Math.Max(6f, sizePx), style, GraphicsUnit.Pixel);
        }
        catch
        {
            font = new Font("Microsoft YaHei", Math.Max(6f, sizePx), style, GraphicsUnit.Pixel);
        }
        _fontCache[key] = font;
        return font;
    }

    private static void DrawBarcode(Graphics g, TemplateElement el, IDictionary<string, string?> values, double dpm)
    {
        var data = Fill(el.Text, values);
        if (string.IsNullOrEmpty(data)) return;

        var (bits, modules) = Code128.Encode(data);
        var x0 = el.X * dpm;
        var y0 = el.Y * dpm;
        var w = el.W * dpm;
        var h = el.H * dpm;
        if (w <= 0 || modules <= 0) return;

        var module = w / modules;
        var y0i = (int)Math.Round(y0);
        var y1i = (int)Math.Round(y0 + h) - 1;
        if (y1i < y0i) return;

        using var brush = new SolidBrush(Color.Black);
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i] != '1') continue;
            var x1 = (int)Math.Round(x0 + i * module);
            var x2 = (int)Math.Round(x0 + (i + 1) * module) - 1;
            if (x2 < x1) x2 = x1;
            g.FillRectangle(brush, x1, y0i, x2 - x1 + 1, y1i - y0i + 1);
        }
    }

    private static void DrawQr(Graphics g, TemplateElement el, IDictionary<string, string?> values, double dpm)
    {
        var data = Fill(el.Text, values);
        if (string.IsNullOrEmpty(data)) return;

        var sizeMm = el.SizeMm ?? (el.W > 0 ? el.W : 15);
        var target = (int)(sizeMm * dpm);

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(data, QRCodeGenerator.ECCLevel.M);
        var matrix = qrData.ModuleMatrix;
        const int border = 4;                       // QRCoder 默认 4 模块静区，模板坐标不含静区
        var n = matrix.Count - border * 2;
        if (n <= 0) return;

        var module = Math.Max(1, target / n);
        var x0 = el.X * dpm;
        var y0 = el.Y * dpm;
        var real = module * n;
        x0 += Math.Max(0, (target - real) / 2.0);
        y0 += Math.Max(0, (target - real) / 2.0);

        using var brush = new SolidBrush(Color.Black);
        for (var r = 0; r < n; r++)
        {
            var row = matrix[r + border];
            var c = 0;
            while (c < n)
            {
                if (!row[c + border]) { c++; continue; }
                var c2 = c;
                while (c2 < n && row[c2 + border]) c2++;
                g.FillRectangle(brush,
                    (float)(x0 + c * module), (float)(y0 + r * module),
                    c2 * module - c * module, module);
                c = c2;
            }
        }
    }

    private static void DrawRect(Graphics g, TemplateElement el, double dpm)
    {
        var x = (float)(el.X * dpm);
        var y = (float)(el.Y * dpm);
        var w = (float)(el.W * dpm);
        var h = (float)(el.H * dpm);
        var t = Math.Max(1, (int)Math.Round((el.ThicknessMm ?? 0.15) * dpm));
        using var pen = new Pen(Color.Black, t);
        g.DrawRectangle(pen, x, y, w, h);
    }

    private static void DrawLine(Graphics g, TemplateElement el, double dpm)
    {
        var x1 = (float)(el.X * dpm);
        var y1 = (float)(el.Y * dpm);
        var x2 = (float)((el.X2 ?? el.X) * dpm);
        var y2 = (float)((el.Y2 ?? el.Y) * dpm);
        var t = Math.Max(1, (int)Math.Round((el.ThicknessMm ?? 0.15) * dpm));
        using var pen = new Pen(Color.Black, t);
        g.DrawLine(pen, x1, y1, x2, y2);
    }

    private static void DrawImage(Graphics g, TemplateElement el, double dpm)
    {
        if (string.IsNullOrEmpty(el.Src)) return;

        Image? image = null;
        try
        {
            if (el.Src.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                var idx = el.Src.IndexOf(',');
                if (idx < 0) return;
                var bytes = Convert.FromBase64String(el.Src[(idx + 1)..]);
                using var ms = new MemoryStream(bytes);
                image = Image.FromStream(ms);
            }
            else if (File.Exists(el.Src))
            {
                image = Image.FromFile(el.Src);
            }

            if (image == null) return;

            var w = (int)(el.W * dpm);
            var h = (int)(el.H * dpm);
            var x = (int)(el.X * dpm);
            var y = (int)(el.Y * dpm);
            if (w <= 0 || h <= 0)
            {
                g.DrawImage(image, x, y);
                return;
            }

            using var resized = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var rg = Graphics.FromImage(resized))
            {
                rg.Clear(Color.White);
                rg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                rg.DrawImage(image, new Rectangle(0, 0, w, h));
            }
            g.DrawImage(resized, x, y);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "图片元素加载失败");
        }
        finally
        {
            image?.Dispose();
        }
    }

    // ---------- 单色化 ----------

    /// <summary>24bpp → 1bpp 单色（阈值 128，无抖动，保证打印 1:1 点阵）。</summary>
    public static Bitmap ToMono(Bitmap src)
    {
        var w = src.Width;
        var h = src.Height;
        var mono = new Bitmap(w, h, PixelFormat.Format1bppIndexed);
        var palette = mono.Palette;
        palette.Entries[0] = Color.Black;
        palette.Entries[1] = Color.White;
        mono.Palette = palette;

        var rect = new Rectangle(0, 0, w, h);
        var monoData = mono.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
        var srcData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = monoData.Stride;
            var rowBytes = new byte[stride];
            var srcStride = srcData.Stride;
            var srcLine = new byte[srcStride];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(srcData.Scan0 + y * srcStride, srcLine, 0, srcStride);
                Array.Clear(rowBytes);
                for (var x = 0; x < w; x++)
                {
                    var i = x * 3;
                    var lum = (srcLine[i + 2] * 299 + srcLine[i + 1] * 587 + srcLine[i] * 114) / 1000;
                    if (lum >= 128) rowBytes[x >> 3] |= (byte)(0x80 >> (x & 7)); // 1 = 白
                }
                Marshal.Copy(rowBytes, 0, monoData.Scan0 + y * stride, stride);
            }
        }
        finally
        {
            src.UnlockBits(srcData);
            mono.UnlockBits(monoData);
        }
        return mono;
    }
}
