using System.ComponentModel.DataAnnotations.Schema;

namespace UXLabelshop.Models;

/// <summary>打印记录 label_print_record。模板快照固化，保证历史可复现。每行=一次清单打印。</summary>
[Table("label_print_record")]
public class LabelPrintRecord
{
    public long Id { get; set; }
    public string JobNo { get; set; } = "";          // e.g. 20261104-0001
    public long? TemplateId { get; set; }
    public string? TemplateCode { get; set; }
    public string? TemplateSnapshot { get; set; }    // 打印时模板 JSON 快照
    public long? UserId { get; set; }
    public string? Username { get; set; }
    public string? PrinterName { get; set; }
    public string PrintMode { get; set; } = "windows"; // windows | zpl | file
    public int TotalQty { get; set; }
    public int OkQty { get; set; }
    public string Status { get; set; } = "done";     // done | partial | failed | cancelled
    public DateTime CreatedAt { get; set; }

    public List<LabelPrintRecordItem> Items { get; set; } = new();
}

/// <summary>打印明细 label_print_record_item。每张标签一行（一行=1 份）。</summary>
[Table("label_print_record_item")]
public class LabelPrintRecordItem
{
    public long Id { get; set; }
    public long RecordId { get; set; }
    public int Seq { get; set; }
    public string? Barcode { get; set; }
    public string? Model { get; set; }
    public string? Batch { get; set; }
    public string? Lot { get; set; }
    public string? Dc { get; set; }
    public string? Qty { get; set; }
    public string? Data { get; set; }                // 完整变量数据 JSON
    public string Status { get; set; } = "ok";       // ok | fail | skipped

    public LabelPrintRecord? Record { get; set; }
}
