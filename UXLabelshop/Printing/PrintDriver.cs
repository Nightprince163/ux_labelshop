using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;

namespace UXLabelshop.Printing;

public enum PrintMode
{
    Windows,
    File,
}

public class PrintOutcome
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public static PrintOutcome Ok(string msg = "") => new() { Success = true, Message = msg };
    public static PrintOutcome Fail(string msg) => new() { Success = false, Message = msg };
}

/// <summary>
/// 打印驱动：1bpp 点阵 1:1 送本地打印机（GDI），或导出 PNG（开发机无打印机时验证）。
/// </summary>
public class PrintDriver
{
    /// <summary>标签尺寸（毫米）→ 打印机自定义纸型（百分之一英寸）。</summary>
    public static PaperSize MakePaperSize(double widthMm, double heightMm, string name = "Label")
    {
        return new PaperSize(name,
            (int)Math.Round(widthMm / 25.4 * 100),
            (int)Math.Round(heightMm / 25.4 * 100));
    }

    /// <summary>
    /// 打印一张标签：位图按物理尺寸 1:1 绘制到页面左上角。
    /// </summary>
    /// <param name="mono">按模板标称 DPI（通常 203）渲染的 1bpp 位图。</param>
    /// <param name="renderAtDpi">可选：按打印机实际 DPI 重新渲染，避免位图被拉伸重采样而损失条码精度。</param>
    /// <param name="printToFileName">仅自检用：让支持 PrintToFile 的驱动直接写到文件。</param>
    public PrintOutcome Print(Bitmap mono, double widthMm, double heightMm, string printerName,
        Func<int, Bitmap>? renderAtDpi = null, string? printToFileName = null)
    {
        try
        {
            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = printerName;
            if (!doc.PrinterSettings.IsValid)
                return PrintOutcome.Fail($"打印机不可用：{printerName}");

            doc.PrintController = new StandardPrintController(); // 不弹进度框
            doc.DocumentName = "UX_LabelShop";

            // 自定义纸型本身就是「宽×高」，不能再设 Landscape=true，
            // 否则驱动会把纸张再旋转 90°（之前打印到 PDF 得到 A4 横向就是这个原因之一）。
            var paper = MakePaperSize(widthMm, heightMm,
                $"UXLABEL_{Math.Round(widthMm)}x{Math.Round(heightMm)}");
            doc.PrinterSettings.DefaultPageSettings.PaperSize = paper;
            doc.PrinterSettings.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            doc.DefaultPageSettings.PaperSize = paper;
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            doc.DefaultPageSettings.Landscape = false;

            if (!string.IsNullOrWhiteSpace(printToFileName))
            {
                doc.PrinterSettings.PrintToFile = true;
                doc.PrinterSettings.PrintFileName = printToFileName;
            }

            // 源位图的隐含分辨率（像素 / 英寸）
            var sourceDpi = mono.Width / (widthMm / 25.4);

            doc.PrintPage += (_, e) =>
            {
                var g = e.Graphics!;
                // 关键修复：打印 Graphics 的 PageUnit 默认是 Display（1/100 英寸），不是像素。
                // 旧代码把 wIn * g.DpiX 当作目标尺寸传进去，在 600dpi 的
                // Microsoft Print To PDF 上会把标签放大 6 倍，A4 页面只装得下左上角，
                // 右侧二维码整块被裁掉。显式切到 Pixel 后，DPI 计算才成立。
                g.PageUnit = GraphicsUnit.Pixel;

                var dpi = (int)Math.Round(g.DpiX);
                var targetW = (float)(widthMm / 25.4 * g.DpiX);
                var targetH = (float)(heightMm / 25.4 * g.DpiY);

                Bitmap? hiRes = null;
                var source = mono;
                if (renderAtDpi != null && dpi is >= 50 and <= 2400 &&
                    Math.Abs(dpi - sourceDpi) > 1)
                {
                    try
                    {
                        hiRes = renderAtDpi(dpi);
                        source = hiRes;
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "按打印机 DPI 重渲染失败，回退原始位图");
                    }
                }

                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(source, new RectangleF(0, 0, targetW, targetH));
                hiRes?.Dispose();
                e.HasMorePages = false;
            };

            doc.Print();
            return PrintOutcome.Ok();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "打印失败 {Printer}", printerName);
            return PrintOutcome.Fail(ex.Message);
        }
    }

    /// <summary>导出模式：把标签存为 PNG（文件名带序号），返回文件路径。</summary>
    public static string ExportPng(Bitmap mono, string dir, string baseName)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{baseName}.png");
        var i = 1;
        while (File.Exists(path))
            path = Path.Combine(dir, $"{baseName}-{i++}.png");
        mono.Save(path, ImageFormat.Png);
        return path;
    }
}
