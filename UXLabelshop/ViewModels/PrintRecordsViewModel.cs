using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Models;
using UXLabelshop.Services;

namespace UXLabelshop.ViewModels;

public class RecordRow
{
    public long Id { get; init; }
    public string JobNo { get; init; } = "";
    public string? TemplateCode { get; init; }
    public string? Username { get; init; }
    public string? PrinterName { get; init; }
    public int TotalQty { get; init; }
    public int OkQty { get; init; }
    public string Status { get; init; } = "";
    public DateTime CreatedAt { get; init; }

    public string StatusText => Status switch
    {
        "done" => "完成",
        "partial" => "部分完成",
        "failed" => "失败",
        "cancelled" => "已取消",
        _ => Status,
    };

    public string CreatedText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public int FailQty => TotalQty - OkQty;
}

public class RecordItemRow
{
    public int Seq { get; init; }
    public string? Barcode { get; init; }
    public string? Model { get; init; }
    public string? Batch { get; init; }
    public string? Lot { get; init; }
    public string? Dc { get; init; }
    public string? Qty { get; init; }
    public string Status { get; init; } = "ok";
    public string StatusText => Status == "ok" ? "成功" : "失败";
}

public partial class PrintRecordsViewModel : ObservableObject
{
    private readonly IServiceProvider _sp;

    public ObservableCollection<RecordRow> Records { get; } = new();
    public ObservableCollection<RecordItemRow> Items { get; } = new();

    [ObservableProperty] private RecordRow? _selectedRecord;
    [ObservableProperty] private DateTime? _dateFrom = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime? _dateTo = DateTime.Today;
    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private string? _statusMessage;

    public PrintRecordsViewModel(IServiceProvider sp) => _sp = sp;

    [RelayCommand]
    public void Search()
    {
        Records.Clear();
        Items.Clear();

        using var db = _sp.GetRequiredService<AppDbContext>();
        var query = db.PrintRecords.AsQueryable();

        if (DateFrom is { } from) query = query.Where(r => r.CreatedAt >= from.Date);
        if (DateTo is { } to) query = query.Where(r => r.CreatedAt < to.Date.AddDays(1));

        var kw = Keyword?.Trim() ?? "";
        if (kw.Length > 0)
        {
            query = query.Where(r => r.JobNo.Contains(kw)
                                     || (r.TemplateCode != null && r.TemplateCode.Contains(kw))
                                     || (r.Username != null && r.Username.Contains(kw))
                                     || r.Items!.Any(i => i.Barcode != null && i.Barcode.Contains(kw)));
        }

        var list = query.OrderByDescending(r => r.Id).Take(500).ToList();
        foreach (var r in list)
        {
            Records.Add(new RecordRow
            {
                Id = r.Id,
                JobNo = r.JobNo,
                TemplateCode = r.TemplateCode,
                Username = r.Username,
                PrinterName = r.PrinterName,
                TotalQty = r.TotalQty,
                OkQty = r.OkQty,
                Status = r.Status,
                CreatedAt = r.CreatedAt,
            });
        }
        StatusMessage = $"共 {list.Count} 条记录（最多显示 500 条）";
        SelectedRecord = Records.FirstOrDefault();
    }

    partial void OnSelectedRecordChanged(RecordRow? value)
    {
        Items.Clear();
        if (value == null) return;

        using var db = _sp.GetRequiredService<AppDbContext>();
        var items = db.PrintRecordItems.Where(i => i.RecordId == value.Id).OrderBy(i => i.Seq).ToList();
        foreach (var i in items)
        {
            Items.Add(new RecordItemRow
            {
                Seq = i.Seq,
                Barcode = i.Barcode,
                Model = i.Model,
                Batch = i.Batch,
                Lot = i.Lot,
                Dc = i.Dc,
                Qty = i.Qty,
                Status = i.Status,
            });
        }
    }

    public string BuildCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("任务号,打印时间,模板,操作人,打印机,总张数,成功,失败,状态,序号,条码,型号,批号,LOT,D/C,数量");
        using var db = _sp.GetRequiredService<AppDbContext>();

        foreach (var r in Records)
        {
            var items = db.PrintRecordItems.Where(i => i.RecordId == r.Id).OrderBy(i => i.Seq).ToList();
            if (items.Count == 0)
            {
                sb.AppendLine(string.Join(",",
                    Csv(r.JobNo), Csv(r.CreatedText), Csv(r.TemplateCode), Csv(r.Username), Csv(r.PrinterName),
                    r.TotalQty, r.OkQty, r.FailQty, Csv(r.StatusText), "", "", "", "", "", "", ""));
                continue;
            }
            foreach (var i in items)
            {
                sb.AppendLine(string.Join(",",
                    Csv(r.JobNo), Csv(r.CreatedText), Csv(r.TemplateCode), Csv(r.Username), Csv(r.PrinterName),
                    r.TotalQty, r.OkQty, r.FailQty, Csv(r.StatusText),
                    i.Seq, Csv(i.Barcode), Csv(i.Model), Csv(i.Batch), Csv(i.Lot), Csv(i.Dc), Csv(i.Qty)));
            }
        }
        return sb.ToString();
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    public void ExportCsv(string path)
    {
        File.WriteAllText(path, BuildCsv(), new UTF8Encoding(true)); // 带 BOM，Excel 直接打开不乱码
    }
}
