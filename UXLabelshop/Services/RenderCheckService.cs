using System.Drawing.Imaging;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Rendering;

namespace UXLabelshop.Services;

/// <summary>渲染内核自检：把全部模板按样例值渲染成 PNG，供与样张比对。</summary>
public class RenderCheckService
{
    private readonly IServiceProvider _sp;

    public RenderCheckService(IServiceProvider sp) => _sp = sp;

    /// <summary>返回每张模板的渲染结果摘要行。</summary>
    public List<string> Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var lines = new List<string>();
        var renderer = new LabelRenderer();

        using var db = _sp.GetRequiredService<AppDbContext>();
        var templates = (from t in db.LabelTemplates
                         join v in db.TemplateVers on t.CurrentVerId equals v.Id
                         orderby t.Code
                         select new { t.Id, t.Code, t.Name, v.Content }).ToList();
        var templateService = _sp.GetRequiredService<TemplateService>();

        foreach (var t in templates)
        {
            void Progress(string msg) =>
                File.AppendAllText(Path.Combine(outputDir, "progress.txt"),
                    $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");

            Progress($"start {t.Code}");
            var doc = templateService.LoadDoc(t.Id);
            Progress($"parsed {t.Code} elements={doc.Elements.Count} vars={doc.Variables.Count}");
            var values = doc.Variables
                .Where(v => !string.IsNullOrEmpty(v.Key))
                .ToDictionary(v => v.Key, v => v.Sample ?? "");
            try
            {
                using var mono = renderer.RenderMono(doc, values);
                Progress($"rendered {t.Code} {mono.Width}x{mono.Height}");
                var path = Path.Combine(outputDir, t.Code + ".png");
                mono.Save(path, ImageFormat.Png);
                Progress($"saved {t.Code}");
                lines.Add($"[OK] {t.Code} {mono.Width}x{mono.Height}px  {t.Name}");
            }
            catch (Exception ex)
            {
                lines.Add($"[FAIL] {t.Code}: {ex.Message}");
                Progress($"fail {t.Code}: {ex.Message}");
            }
        }
        return lines;
    }
}
