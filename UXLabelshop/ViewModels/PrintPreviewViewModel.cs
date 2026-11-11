using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UXLabelshop.Printing;
using UXLabelshop.Rendering;
using UXLabelshop.Services;

namespace UXLabelshop.ViewModels;

public class PreviewItem
{
    public int Index { get; init; }
    public BitmapSource? Image { get; init; }
    /// <summary>数据摘要：仅作为缩略图悬停提示，正常情况下不在界面占位。</summary>
    public string Summary { get; init; } = "";
    /// <summary>渲染失败时置真，卡片下方才显示 Summary 里的错误信息。</summary>
    public bool IsError { get; init; }
}

public partial class PrintPreviewViewModel : ObservableObject
{
    private readonly PrintService _printService;
    private readonly ConfigService _config;
    private readonly LabelRenderer _renderer = new();
    private readonly TemplateDoc _doc;
    private readonly long _templateId;
    private readonly string _templateCode;
    private readonly List<Dictionary<string, string?>> _rows;

    public ObservableCollection<PreviewItem> Items { get; } = new();
    public List<PrinterInfo> Printers { get; } = new();

    [ObservableProperty] private PrinterInfo? _selectedPrinter;
    [ObservableProperty] private bool _printToPrinter = true;
    [ObservableProperty] private bool _exportToFile;
    [ObservableProperty] private string _exportDir;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;

    public string PageInfo => $"{_doc.WidthMm}×{_doc.HeightMm}mm · {_doc.Dpi}dpi · 共 {_rows.Count} 张";

    public PrintPreviewViewModel(PrintService printService, ConfigService config, TemplateDoc doc, long templateId,
        string templateCode, string templateName, List<Dictionary<string, string?>> rows)
    {
        _printService = printService;
        _config = config;
        _doc = doc;
        _templateId = templateId;
        _templateCode = templateCode;
        _rows = rows;
        _exportDir = Path.Combine(App.AppDir, "exports");
        Title = $"打印预览 — {templateName}";

        var list = PrinterProbe.List();
        Printers.AddRange(list);
        // 优先选中上次打印用的打印机；找不到（已卸载/换机）再退回系统默认或第一台
        SelectedPrinter = list.FirstOrDefault(p => p.Name == _config.Current.LastPrinterName)
            ?? list.FirstOrDefault(p => p.IsDefault)
            ?? list.FirstOrDefault();
        if (SelectedPrinter == null)
        {
            PrintToPrinter = false;
            ExportToFile = true;
            Status = "未发现可用打印机，已切换为导出 PNG 模式";
        }
    }

    /// <summary>渲染全部预览图（调用方在 UI 线程外执行亦可，BitmapSource 已冻结）。</summary>
    public void BuildPreviews()
    {
        Items.Clear();
        // 变量 key → 中文标签，摘要给悬停提示用，避免出现 lot2=... 这种内部键名
        var labels = new Dictionary<string, string>();
        foreach (var v in _doc.Variables)
        {
            if (string.IsNullOrEmpty(v.Key) || labels.ContainsKey(v.Key)) continue;
            labels[v.Key] = string.IsNullOrWhiteSpace(v.Label) ? v.Key : v.Label;
        }

        for (var i = 0; i < _rows.Count; i++)
        {
            try
            {
                using var mono = _renderer.RenderMono(_doc, _rows[i]);
                var summary = string.Join("　", _rows[i].Where(kv => !string.IsNullOrEmpty(kv.Value))
                    .Take(4).Select(kv => $"{labels.GetValueOrDefault(kv.Key, kv.Key)}：{kv.Value}"));
                Items.Add(new PreviewItem
                {
                    Index = i + 1,
                    Image = ImageUtil.ToBitmapSource(mono),
                    Summary = summary,
                });
            }
            catch (Exception ex)
            {
                Items.Add(new PreviewItem { Index = i + 1, Summary = "渲染失败：" + ex.Message, IsError = true });
            }
        }
    }

    [RelayCommand]
    public async Task PrintAsync()
    {
        if (Busy) return;
        if (PrintToPrinter && SelectedPrinter == null)
        {
            Status = "请选择打印机";
            return;
        }

        Busy = true;
        Status = "正在打印…";
        try
        {
            var mode = PrintToPrinter ? PrintMode.Windows : PrintMode.File;
            var printerName = SelectedPrinter?.Name ?? "";
            var result = await Task.Run(() => _printService.PrintBatch(
                _doc, _templateId, _templateCode, _rows, printerName, mode, ExportDir));

            Status = result.Message + (mode == PrintMode.File ? $"\n文件已导出到：{ExportDir}" : "");
            // 打印成功（送到真实打印机）时记住该打印机，下次预览默认选中
            if (mode == PrintMode.Windows && SelectedPrinter != null && result.FailCount == 0)
                _config.SetLastPrinter(SelectedPrinter.Name);
            if (result.FailCount == 0 && mode == PrintMode.File)
                System.Diagnostics.Process.Start("explorer.exe", ExportDir);
        }
        catch (Exception ex)
        {
            Status = "打印失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
