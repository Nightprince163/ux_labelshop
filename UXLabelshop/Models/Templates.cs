using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UXLabelshop.Models;

/// <summary>模板主表 label_template（沿用服务器现有表结构与数据）。</summary>
[Table("label_template")]
public class LabelTemplate
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal WidthMm { get; set; }
    public decimal HeightMm { get; set; }
    public short Dpi { get; set; } = 203;
    public string Status { get; set; } = "draft"; // draft | published | disabled
    public long? CurrentVerId { get; set; }
    public string Source { get; set; } = "manual"; // manual | lsd_import
    public string? SourceFile { get; set; }
    public string? Remark { get; set; }
    public long? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>模板版本表 label_template_ver（沿用现有表，保存即新版本）。</summary>
[Table("label_template_ver")]
public class LabelTemplateVer
{
    public long Id { get; set; }
    public long TemplateId { get; set; }
    public int Ver { get; set; }
    public string Content { get; set; } = ""; // mm 坐标 JSON
    public string? ChangeNote { get; set; }
    public long? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public LabelTemplate? Template { get; set; }
}

/// <summary>扫码解析规则 label_scan_rule（沿用现有表）。</summary>
[Table("label_scan_rule")]
public class LabelScanRule
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Pattern { get; set; }         // regex 模式的正则（命名捕获组）
    public long? TemplateId { get; set; }
    public string? Mapping { get; set; }          // 捕获组 → 变量 key 的 JSON
    public int Priority { get; set; } = 100;      // 小值优先
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = "regex";   // regex | delimited
    public string? Delimiter { get; set; }        // delimited 分隔符
    public string? Marker { get; set; }           // 段首类型标记（如 A/B/C）
    public bool StripWrap { get; set; } = true;   // 去掉首尾空段
    public string? SeqMap { get; set; }           // 段序号 → 变量 JSON
    public string? SampleCodes { get; set; }      // 样本码 JSON
    public long? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
