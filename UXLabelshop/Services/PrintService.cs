using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UXLabelshop.Models;
using UXLabelshop.Printing;
using UXLabelshop.Rendering;

namespace UXLabelshop.Services;

public class PrintBatchResult
{
    public bool Success { get; init; }
    public string JobNo { get; init; } = "";
    public int OkCount { get; init; }
    public int FailCount { get; init; }
    public string Message { get; init; } = "";
    public long? RecordId { get; init; }
}

/// <summary>打印执行 + 记录入库（每行=1 份，一次打印=一条记录）。</summary>
public class PrintService
{
    private readonly IServiceProvider _sp;
    private readonly AppSession _session;
    private readonly LabelRenderer _renderer = new();

    public PrintService(IServiceProvider sp, AppSession session)
    {
        _sp = sp;
        _session = session;
    }

    public PrintBatchResult PrintBatch(
        TemplateDoc doc,
        long templateId,
        string templateCode,
        IReadOnlyList<Dictionary<string, string?>> rows,
        string printerName,
        PrintMode mode,
        string exportDir)
    {
        if (rows.Count == 0) return new PrintBatchResult { Success = false, Message = "清单为空" };

        var driver = new PrintDriver();
        var ok = 0;
        var fail = 0;
        var items = new List<LabelPrintRecordItem>();
        var errors = new List<string>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var item = new LabelPrintRecordItem
            {
                Seq = i + 1,
                Barcode = Pick(row, "barcode", "track", "serial", "partno", "part_no", "partno3", "model", "model1", "po", "order"),
                Model = Pick(row, "model", "model1", "partno", "part_no", "partno3", "package3", "package"),
                Batch = Pick(row, "batch", "batch2", "batch3"),
                Lot = Pick(row, "lot", "lot2", "lot3"),
                Dc = Pick(row, "dc", "dcode", "dcode2", "dcode3"),
                Qty = Pick(row, "qty"),
                Data = JsonSerializer.Serialize(row, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }),
            };

            var itemOk = false;
            try
            {
                using var mono = _renderer.RenderMono(doc, row);
                if (mode == PrintMode.File)
                {
                    PrintDriver.ExportPng(mono, exportDir, $"{templateCode}-{i + 1:000}");
                    itemOk = true;
                }
                else
                {
                    // 打印机分辨率常与模板标称的 203dpi 不同（如 Print To PDF 为 600dpi），
                    // 按实际 DPI 重新渲染，让点阵 1:1 落纸、条码不被重采样
                    var outcome = driver.Print(mono, doc.WidthMm, doc.HeightMm, printerName,
                        dpi => _renderer.RenderMono(doc, row, dpi));
                    itemOk = outcome.Success;
                    if (!itemOk) errors.Add($"#{i + 1} {outcome.Message}");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"#{i + 1} {ex.Message}");
            }

            item.Status = itemOk ? "ok" : "fail";
            if (itemOk) ok++; else fail++;
            items.Add(item);
        }

        var jobNo = NextJobNo();
        var record = new LabelPrintRecord
        {
            JobNo = jobNo,
            TemplateId = templateId,
            TemplateCode = templateCode,
            TemplateSnapshot = doc.ToJson(),
            UserId = _session.CurrentUser?.Id,
            Username = _session.CurrentUser?.Username,
            PrinterName = mode == PrintMode.File ? "(导出文件)" : printerName,
            PrintMode = mode == PrintMode.File ? "file" : "windows",
            TotalQty = rows.Count,
            OkQty = ok,
            Status = fail == 0 ? "done" : ok == 0 ? "failed" : "partial",
            CreatedAt = DateTime.Now,
            Items = items,
        };

        using (var db = _sp.GetRequiredService<AppDbContext>())
        {
            db.PrintRecords.Add(record);
            db.SaveChanges();
        }

        var msg = fail == 0
            ? $"打印完成：{ok} 张（任务号 {jobNo}）"
            : $"完成 {ok} 张，失败 {fail} 张（任务号 {jobNo}）" +
              (errors.Count > 0 ? "\n" + string.Join("\n", errors.Take(5)) : "");

        return new PrintBatchResult
        {
            Success = fail == 0,
            JobNo = jobNo,
            OkCount = ok,
            FailCount = fail,
            Message = msg,
            RecordId = record.Id,
        };
    }

    public string NextJobNo()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        using var db = _sp.GetRequiredService<AppDbContext>();
        var used = db.PrintRecords
            .Where(r => r.JobNo.StartsWith(today))
            .Select(r => r.JobNo)
            .ToList();

        var seq = 1;
        while (used.Contains($"{today}-{seq:0000}")) seq++;
        return $"{today}-{seq:0000}";
    }

    private static string? Pick(Dictionary<string, string?> row, params string[] keys)
    {
        foreach (var k in keys)
        {
            var hit = row.FirstOrDefault(kv => string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(hit.Value)) return hit.Value;
        }
        return null;
    }
}
