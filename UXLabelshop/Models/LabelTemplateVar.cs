using System.ComponentModel.DataAnnotations.Schema;

namespace UXLabelshop.Models;

/// <summary>
/// 模板变量子表 label_template_var：变量定义的唯一权威来源（当前态）。
/// 画布存在 label_template_ver.content，版本快照与打印快照仍内嵌完整变量以保证历史可复现。
/// </summary>
[Table("label_template_var")]
public class LabelTemplateVar
{
    public long Id { get; set; }
    public long TemplateId { get; set; }
    public string Key { get; set; } = "";
    public string? Label { get; set; }
    public string? Short { get; set; }
    /// <summary>scan 扫码录入 / fixed 固定值 / computed 公式计算</summary>
    public string? Source { get; set; }
    /// <summary>缺省值：scan 变量为空时套用</summary>
    public string? DefaultValue { get; set; }
    /// <summary>Excel 风格公式（以 = 开头），computed 变量必填</summary>
    public string? Formula { get; set; }
    /// <summary>样例值（编辑器预览 / 公式测试默认值）</summary>
    public string? Sample { get; set; }
    /// <summary>source=fixed 时的固定值</summary>
    public string? Value { get; set; }
    /// <summary>清单列顺序</summary>
    public int SortNo { get; set; }
    /// <summary>原模板 JSON 里未纳入本表的字段（如 required），避免迁移丢信息</summary>
    public string? ExtraJson { get; set; }
}
