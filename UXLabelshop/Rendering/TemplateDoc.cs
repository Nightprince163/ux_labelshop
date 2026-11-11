using System.Text.Json;
using System.Text.Json.Serialization;

namespace UXLabelshop.Rendering;

/// <summary>模板文档（mm 坐标 JSON，与服务器现有模板格式一致）。</summary>
public class TemplateDoc
{
    [JsonPropertyName("width_mm")] public double WidthMm { get; set; } = 100;
    [JsonPropertyName("height_mm")] public double HeightMm { get; set; } = 50;
    [JsonPropertyName("dpi")] public int Dpi { get; set; } = 203;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("saved_title")] public string? SavedTitle { get; set; }
    [JsonPropertyName("source_file")] public string? SourceFile { get; set; }
    [JsonPropertyName("printer_hint")] public string? PrinterHint { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("variables")] public List<TemplateVar> Variables { get; set; } = new();
    [JsonPropertyName("elements")] public List<TemplateElement> Elements { get; set; } = new();

    /// <summary>保留未知字段，编辑保存时原样回写。</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static TemplateDoc Parse(string json) =>
        JsonSerializer.Deserialize<TemplateDoc>(json, Options)
        ?? throw new InvalidOperationException("模板 JSON 解析失败");

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

public class TemplateVar
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("short")] public string? Short { get; set; }
    [JsonPropertyName("sample")] public string? Sample { get; set; }
    /// <summary>scan：扫码/录入；fixed：固定值（渲染时自动补，不进清单）；computed：公式计算</summary>
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    /// <summary>缺省值：scan 变量为空时套用（如 BIN → BIN1）</summary>
    [JsonPropertyName("default_value")] public string? DefaultValue { get; set; }
    /// <summary>Excel 风格公式，以 = 开头（computed 变量必填）</summary>
    [JsonPropertyName("formula")] public string? Formula { get; set; }
    /// <summary>清单列顺序</summary>
    [JsonPropertyName("sort_no")] public int SortNo { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public string EffectiveSource => string.IsNullOrWhiteSpace(Source) ? "scan" : Source!.Trim();
    public bool IsFixed => EffectiveSource == "fixed";
    public bool IsComputed => EffectiveSource == "computed";
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Key : $"{Label} ({Key})";
}

public class TemplateElement
{
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("w")] public double W { get; set; }
    [JsonPropertyName("h")] public double H { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("size_mm")] public double? SizeMm { get; set; }
    /// <summary>字号（磅，纯数字，类似 Excel 字号下拉）。仅 text 元素有效；
    /// 优先于 size_mm；旧模板无此字段时继续用 size_mm（字高毫米），渲染结果不变。</summary>
    [JsonPropertyName("font_size")] public double? FontSize { get; set; }
    [JsonPropertyName("font")] public string? Font { get; set; }
    [JsonPropertyName("bold")] public bool? Bold { get; set; }
    /// <summary>斜体；null/旧模板 = 关闭。</summary>
    [JsonPropertyName("italic")] public bool? Italic { get; set; }
    /// <summary>自动换行：文本超出元素宽度时折行显示；null/旧模板 = 关闭（单行缩小/截断）。</summary>
    [JsonPropertyName("word_wrap")] public bool? WordWrap { get; set; }
    [JsonPropertyName("align")] public string? Align { get; set; }
    [JsonPropertyName("valign")] public string? Valign { get; set; }
    [JsonPropertyName("thickness_mm")] public double? ThicknessMm { get; set; }
    [JsonPropertyName("x2")] public double? X2 { get; set; }
    [JsonPropertyName("y2")] public double? Y2 { get; set; }
    [JsonPropertyName("src")] public string? Src { get; set; }
    [JsonPropertyName("symbology")] public string? Symbology { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
