using System.Drawing.Printing;

namespace UXLabelshop.Printing;

public record PaperInfo(string Name, double WidthMm, double HeightMm);

public record PrinterInfo(string Name, int DpiX, int DpiY, bool IsDefault, List<PaperInfo> Papers)
{
    public string Display => $"{Name}{(IsDefault ? "（默认）" : "")} · {DpiX}dpi";
}

/// <summary>枚举系统打印机、探测分辨率与纸型（对应参考实现 printer.py 的探测逻辑）。</summary>
public static class PrinterProbe
{
    public static List<PrinterInfo> List()
    {
        var result = new List<PrinterInfo>();
        foreach (string name in PrinterSettings.InstalledPrinters)
        {
            var info = Probe(name);
            if (info != null) result.Add(info);
        }
        return result;
    }

    public static PrinterInfo? Probe(string name)
    {
        try
        {
            using var doc = new PrintDocument();
            var ps = doc.PrinterSettings;
            ps.PrinterName = name;
            if (!ps.IsValid) return null;

            var dpiX = 203;
            var dpiY = 203;
            try
            {
                using var g = ps.CreateMeasurementGraphics();
                dpiX = (int)Math.Round(g.DpiX);
                dpiY = (int)Math.Round(g.DpiY);
            }
            catch
            {
                // 部分虚拟打印机取不到 DPI，按 203 处理
            }

            var papers = new List<PaperInfo>();
            foreach (PaperSize p in ps.PaperSizes)
            {
                var w = Math.Round(p.Width / 100.0 * 25.4, 1);
                var h = Math.Round(p.Height / 100.0 * 25.4, 1);
                papers.Add(new PaperInfo(p.PaperName, w, h));
            }

            return new PrinterInfo(name, dpiX, dpiY, ps.IsDefaultPrinter, papers);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "探测打印机失败：{Name}", name);
            return null;
        }
    }

    /// <summary>默认打印机名；无默认则取第一台。</summary>
    public static string? DefaultPrinterName()
    {
        var list = List();
        return list.FirstOrDefault(p => p.IsDefault)?.Name ?? list.FirstOrDefault()?.Name;
    }
}
