using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UXLabelshop.Rendering;

namespace UXLabelshop.Services;

/// <summary>
/// LabelShop .lsd 导入结果。
/// .lsd 是「纯文本头 + MFC 二进制序列化流」的混合容器：幅面、变量名表、对象包围盒、
/// 以及每个文本对象的静态文字/上次输入样例值都可以稳定读出；只有对象样式、条码参数
/// 被压缩块封装，无法逐字节还原。
/// 因此版面（位置/尺寸/字号）按实测包围盒 1:1 重建，文字内容按明文段还原，
/// 个别行的文字顺序与字体细节需在编辑器里按实物微调。
/// </summary>
public sealed class LsdImportResult
{
    public string FileName { get; init; } = "";
    public string? SavedTitle { get; init; }
    public string? Software { get; init; }
    public string? PrinterHint { get; init; }
    public double WidthMm { get; init; } = 100;
    public double HeightMm { get; init; } = 50;
    /// <summary>.lsd 未记录 dpi；按现有模板统一取 203，允许用户在对话框改。</summary>
    public int Dpi { get; init; } = 203;
    public List<string> RawVariableNames { get; init; } = new();
    public List<string> Fonts { get; init; } = new();
    public List<LsdBox> Boxes { get; init; } = new();
    public List<LsdBox> ClassBoxes { get; init; } = new();
    public List<LsdPicture> Pictures { get; init; } = new();
    /// <summary>明文段读出的对象文字：静态标签（以 : / ： 结尾）与上次输入的样例值。</summary>
    public List<LsdConstText> ConstTexts { get; init; } = new();
    public int ConstTextCount => ConstTexts.Count;
    /// <summary>静态标签，保持 .lsd 对象记录顺序（不再按字段名重排，避免套错命名体系）。</summary>
    public List<string> StaticLabels => ConstTexts.Where(t => t.IsLabel).Select(t => t.Text).ToList();
    public List<string> SampleValues => ConstTexts.Where(t => !t.IsLabel).Select(t => t.Text).ToList();

    public string InfoText =>
        $"文件：{FileName}\n" +
        $"幅面：{WidthMm:0.##} × {HeightMm:0.##} mm　·　dpi：{Dpi}（.lsd 未记录，默认 203）\n" +
        $"原始标题：{SavedTitle ?? "未读到"}　·　打印机：{PrinterHint ?? "未读到"}\n" +
        $"变量：{RawVariableNames.Count} 个　·　对象包围盒：{Boxes.Count} 个　·　图片：{Pictures.Count} 个　·　" +
        $"静态文字：{StaticLabels.Count} 个　·　样例值：{SampleValues.Count} 个";
}

/// <summary>对象包围盒（0.01mm），Type 来自类名记录，为空表示需靠几何推断。</summary>
public record LsdBox(int X1, int Y1, int X2, int Y2, string Type, int Offset);

/// <summary>.lsd 明文段读出的一个文本（长度前缀 + GBK）。</summary>
public record LsdConstText(string Text, int Offset, bool IsLabel);

public static class LsdImportService
{
    private static readonly Encoding Gbk = CreateGbk();

    private static Encoding CreateGbk()
    {
        // .NET 8 默认只带 Unicode 代码页；.lsd 是 GBK
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
    }

    private static readonly string[] ClassTags =
    {
        "CVariable", "CDrawText", "CDrawBarcode", "CDrawPicture", "CDrawImage",
        "CDrawRect", "CDrawLine", "CDrawEllipse",
    };

    private static readonly string[] FontHints =
    {
        "宋体", "黑体", "楷体", "仿宋", "微软雅黑", "新宋体", "等线",
        "Arial", "Times", "Verdana", "Tahoma", "Courier", "Calibri",
    };

    private static readonly string[] PrinterBrands =
    {
        "Citizen", "Zebra", "ZDesigner", "TSC", "Godex", "Argox", "SATO",
        "Brother", "Postek", "Sewoo", "Xprinter", "Epson", "Canon",
    };

    // 变量名 → (key, 短标签)。沿用 gen_templates.py 的映射，未收录的按 v<序号> 生成。
    private static readonly Dictionary<string, (string Key, string Short)> VarMap = new(StringComparer.Ordinal)
    {
        ["序列"] = ("seq", "序列"), ["产品型号"] = ("partno", "产品型号"),
        ["批号"] = ("batch", "批号"), ["LOT ID"] = ("lot", "LOT ID"),
        ["D/C"] = ("dcode", "D/C"), ["数量"] = ("qty", "数量"),
        ["订单号一维码"] = ("order", "订单号"), ["片号"] = ("wafer", "片号"),
        ["编号"] = ("serial", "编号"), ["型号3"] = ("model3", "型号3"),
        ["BIN"] = ("bin", "BIN"), ["二维码1"] = ("qr1", "二维码1"),
        ["字段2"] = ("field2", "字段"), ["Part NO"] = ("part_no", "Part NO"),
        ["批次"] = ("batch2", "批次"), ["lot no"] = ("lot2", "Lot No"),
        ["Date Code"] = ("dcode2", "Date Code"), ["Track ID"] = ("track", "Track ID"),
        ["PO#"] = ("po", "PO#"), ["Package"] = ("package", "Package"),
        ["Part No"] = ("part_no", "Part No"), ["型号4"] = ("model4", "型号4"),
        ["二维码2"] = ("qr2", "二维码2"),
        ["其他"] = ("seq3", "其他"), ["型号"] = ("partno3", "型号"),
        ["批次号"] = ("batch3", "批次号"), ["L/N"] = ("lot3", "L/N"),
        ["DC"] = ("dcode3", "DC"), ["一维码"] = ("track3", "一维码"),
        ["bin"] = ("bin3", "bin"), ["package"] = ("package3", "package"),
        ["型号0"] = ("model0", "型号0"), ["型号1"] = ("model1", "型号1"),
        ["一维码数量+pcs"] = ("qty_text", "数量+pcs"),
        ["一维码型号+批次"] = ("model_batch", "型号+批次"),
    };

    /// <summary>
    /// 标签上的静态文字 → 变量 key。用于把「订单号:」这样的文字绑定到 {{order}}。
    /// 用 Ordinal（精确）：.lsd 里「BIN」「bin」「Date Code」「DATE CODE」是不同模板的不同文字，
    /// 若忽略大小写会被后面的赋值覆盖，导致绑定丢失（LabelKey 里再做忽略大小写兜底）。
    /// </summary>
    private static readonly Dictionary<string, string> LabelKeyMap = new(StringComparer.Ordinal)
    {
        ["订单号"] = "order", ["产品型号"] = "partno", ["批号"] = "batch",
        ["LOT ID"] = "lot", ["LOT NO"] = "lot2", ["D/C"] = "dcode",
        ["DATE CODE"] = "dcode", ["DC"] = "dcode3", ["Date Code"] = "dcode2",
        ["数量"] = "qty", ["编号"] = "serial", ["片号"] = "wafer",
        ["BIN"] = "bin", ["bin"] = "bin3", ["序列"] = "seq",
        ["型号"] = "partno3", ["型号3"] = "model3", ["型号4"] = "model4",
        ["批次"] = "batch2", ["批次号"] = "batch3", ["L/N"] = "lot3",
        ["PO#"] = "po", ["Package"] = "package", ["package"] = "package3",
        ["Part NO"] = "part_no", ["Part No"] = "part_no", ["Track ID"] = "track",
        ["其他"] = "seq3", ["一维码"] = "track3",
    };

    /// <summary>解析 .lsd，失败抛 InvalidOperationException（由 UI 转成友好提示）。</summary>
    public static LsdImportResult Parse(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("文件不存在：" + path);
        var data = File.ReadAllBytes(path);
        var name = Path.GetFileName(path);

        var page = LsdReader.TryReadPageSize(data);
        var width = page.HasValue ? Math.Round(page.Value.W / 100.0, 2) : 100.0;
        var height = page.HasValue ? Math.Round(page.Value.H / 100.0, 2) : 50.0;

        var header = Encoding.ASCII.GetString(data, 0, Math.Min(200, data.Length));
        var ver = Regex.Match(header, @"Professional\s+([\d.]+)\s+Build\s+(\d+)");
        var software = ver.Success ? $"LabelShop Professional {ver.Groups[1].Value} Build {ver.Groups[2].Value}" : null;

        var (rawVars, savedTitle) = ReadVariableTable(data);
        var boxes = ReadAllBoxes(data);
        var classBoxes = ReadClassBoxes(data);
        var pictures = LsdReader.FindPictures(data);
        var fonts = FindFonts(data);
        var printer = FindPrinter(data);
        var texts = ReadConstTexts(data, savedTitle, printer);

        return new LsdImportResult
        {
            FileName = name,
            SavedTitle = savedTitle,
            Software = software,
            PrinterHint = printer,
            WidthMm = width,
            HeightMm = height,
            Dpi = 203,
            RawVariableNames = rawVars,
            Fonts = fonts,
            Boxes = boxes,
            ClassBoxes = classBoxes,
            Pictures = pictures,
            ConstTexts = texts,
        };
    }

    // ---------- 底层读取 ----------

    /// <summary>变量表：锚点 "字段1\n"(GBK)，之后是 "序号\r名称\n" 序列。</summary>
    private static (List<string> Names, string? Title) ReadVariableTable(byte[] data)
    {
        var names = new List<string>();
        string? title = null;
        var idx = Gbk.GetBytes("字段1\n");
        var at = IndexOf(data, idx, 0);
        if (at < 0) return (names, title);

        var pos = at + idx.Length;
        while (pos < data.Length)
        {
            // 最多 3 位数字 + \r
            var p = pos;
            var digits = 0;
            while (p < data.Length && data[p] >= (byte)'0' && data[p] <= (byte)'9' && digits < 3)
            {
                p++; digits++;
            }
            if (digits == 0 || p >= data.Length || data[p] != (byte)'\r') break;
            pos = p + 1;
            var end = Array.IndexOf(data, (byte)'\n', pos);
            if (end < 0) break;
            var raw = Gbk.GetString(data, pos, end - pos).Trim('\0').Trim();
            pos = end + 1;
            if (raw.Length == 0 || raw.Contains('\ufffd')) break;
            names.Add(raw);
        }

        // 标题：变量表末尾 "\x00\x00\x00<长度><GBK 文本>"
        if (pos + 5 <= data.Length && data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 0)
        {
            var len = data[pos + 3];
            if (len is > 0 and < 80 && pos + 4 + len <= data.Length)
            {
                var t = Gbk.GetString(data, pos + 4, len).Trim('\0').Trim();
                if (t.Length > 0 && !t.Contains('\ufffd')) title = t;
            }
        }
        return (names, title);
    }

    /// <summary>全量对象包围盒：颜色字 0x00FFFFFF 起点 + 18 字节，后接 4×int32。</summary>
    private static List<LsdBox> ReadAllBoxes(byte[] data)
    {
        var result = new List<LsdBox>();
        var marker = new byte[] { 0xff, 0xff, 0xff, 0x00 };
        var from = 0;
        while (true)
        {
            var at = IndexOf(data, marker, from);
            if (at < 0) break;
            from = at + marker.Length;
            var o = at + 18;
            if (o + 16 > data.Length) break;
            var x1 = ReadI32(data, o); var y1 = ReadI32(data, o + 4);
            var x2 = ReadI32(data, o + 8); var y2 = ReadI32(data, o + 12);
            if (BoxOk(x1, y1, x2, y2, 13000, 13000, 9000, 8000))
                result.Add(new LsdBox(x1, y1, x2, y2, "", o));
        }
        return result;
    }

    /// <summary>类名记录只覆盖每类首个对象，作为类型提示用。</summary>
    private static List<LsdBox> ReadClassBoxes(byte[] data)
    {
        var result = new List<LsdBox>();
        foreach (var tag in ClassTags)
        {
            var bytes = Encoding.ASCII.GetBytes(tag);
            var from = 0;
            while (true)
            {
                var at = IndexOf(data, bytes, from);
                if (at < 0) break;
                from = at + bytes.Length;
                var o = at + bytes.Length + 12;
                if (o + 16 > data.Length) continue;
                var x1 = ReadI32(data, o); var y1 = ReadI32(data, o + 4);
                var x2 = ReadI32(data, o + 8); var y2 = ReadI32(data, o + 12);
                if (!BoxOk(x1, y1, x2, y2, 13000, 13000, 9000, 8000)) continue;
                result.Add(new LsdBox(x1, y1, x2, y2, Classify(tag), at));
            }
        }
        return result;
    }

    private static string Classify(string tag) => tag switch
    {
        "CDrawText" => "文本",
        "CDrawBarcode" => "一维码",
        "CDrawPicture" or "CDrawImage" => "图片",
        "CDrawRect" or "CDrawEllipse" => "矩形",
        "CDrawLine" => "线条",
        _ => "变量表",
    };

    private static bool BoxOk(int x1, int y1, int x2, int y2, int maxX, int maxY, int maxW, int maxH) =>
        0 <= x1 && x1 < x2 && x2 <= maxX && 0 <= y1 && y1 < y2 && y2 <= maxY
        && 50 <= x2 - x1 && x2 - x1 <= maxW && 50 <= y2 - y1 && y2 - y1 <= maxH;

    /// <summary>
    /// 按「输入数据：」锚点把文件切成一条条对象记录，只在段内取主文字。
    /// 早期版本全文件扫描「长度前缀 + GBK 字符串」，会把压缩块/位图字节误读成中文
    /// （如「鶄鄥N」「纭:」），导致导入模板出现乱码。对象记录结构固定为：
    ///   ...编码块尾 | 01 80 xx 00 00 00 00 00 00 00 00 00 | &lt;len&gt;&lt;主文字&gt; |
    ///   固定模板 0123456789 / 输入数据： / 1234567890 | 下一个编码块
    /// 因此按段提取可稳定拿到每个对象的显示文字，且天然排除编码块噪声。
    /// </summary>
    private static List<LsdConstText> ReadConstTexts(byte[] data, string? title, string? printer)
    {
        var result = new List<LsdConstText>();
        var anchor = Gbk.GetBytes("输入数据：");
        var anchors = new List<int>();
        for (var i = 0; i <= data.Length - anchor.Length; i++)
        {
            if (!MatchAt(data, i, anchor)) continue;
            anchors.Add(i);
            i += anchor.Length - 1;
        }
        if (anchors.Count == 0) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var from = 200;   // 文件头之后
        foreach (var a in anchors)
        {
            var end = Math.Min(data.Length, a + anchor.Length + 60);
            var bestOffset = -1;
            string? best = null;
            for (var i = from; i < end - 1; i++)
            {
                var len = data[i];
                if (len is < 2 or > 40 || i + 1 + len > end) continue;
                // 严格 GBK：非法字节序列直接判为噪声，不产生替换字符
                string body;
                try { body = StrictGbk.GetString(data, i + 1, len); }
                catch (DecoderFallbackException) { continue; }
                var t = body.Trim();
                if (!IsCleanRecordText(t) || IsNoise(t, title, printer)) continue;
                if (RecordNoiseRegex.IsMatch(t)) continue;
                bestOffset = i;
                best = t;      // 取段内最靠近锚点的候选（固定模板之后就是主文字）
            }
            if (best != null && seen.Add(best))
            {
                var isLabel = best.EndsWith(":", StringComparison.Ordinal) ||
                              best.EndsWith("：", StringComparison.Ordinal);
                result.Add(new LsdConstText(best, bestOffset, isLabel));
            }
            from = a + anchor.Length + 1;
        }
        return result;
    }

    /// <summary>严格 GBK 解码器：遇到非法字节直接抛错，避免把位图/压缩数据读成中文乱码。</summary>
    private static readonly Encoding StrictGbk =
        Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback, DecoderFallback.ExceptionFallback);

    private static readonly Regex RecordNoiseRegex =
        new(@"^[QX][A-Za-z0-9+<>.]{6,}$", RegexOptions.Compiled);

    /// <summary>对象主文字：ASCII 可打印 / 常用汉字 / 常见标点，不含控制字符与生僻符号。</summary>
    private static bool IsCleanRecordText(string t)
    {
        if (t.Length is < 2 or > 40) return false;
        // 对象记录里的「变量值表」长串（|A|UX2115|E4Eb1H|…）不是标签上的显示文字
        if (t.Length > 20 && (t.Contains('|') || t.Contains('}'))) return false;
        foreach (var c in t)
        {
            if (c is >= ' ' and <= '~') continue;
            if (c is >= '\u4e00' and <= '\u9fa5') continue;
            if (c is '：' or '。' or '（' or '）' or '、' or '，') continue;
            return false;
        }
        return true;
    }

    private static bool MatchAt(byte[] data, int at, byte[] needle)
    {
        for (var k = 0; k < needle.Length; k++)
            if (data[at + k] != needle[k]) return false;
        return true;
    }

    private static bool IsNoise(string t, string? title, string? printer)
    {
        if (t == title || t == printer) return true;
        if (FontHints.Contains(t, StringComparer.OrdinalIgnoreCase)) return true;
        if (t is "0123456789" or "1234567890" or "输入数据：" or "输入数据:") return true;
        if (t.StartsWith("http", StringComparison.OrdinalIgnoreCase) || t.StartsWith("-----")) return true;
        if (t.Contains("LabelShop", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Profession", StringComparison.OrdinalIgnoreCase)) return true;
        // 编码块碎片，如 QEOkF7Js / QEJsF7JZ
        if (t.StartsWith("QE", StringComparison.Ordinal) && t.Contains("F7")) return true;
        return false;
    }

    /// <summary>
    /// 标签规范化：去掉末尾冒号/句点，让 "Part No.:" / "Part No:" / "Part No."
    /// 都归一为 "Part No"，避免同一标签在不同 .lsd 里绑不上变量或排错行序。
    /// </summary>
    private static string NormalizeLabel(string label) =>
        label.Trim().TrimEnd(':', '：', '.').Trim();

    /// <summary>
    /// 标签的视觉行序。.lsd 的对象记录顺序是「对象创建顺序」，不是标签从上到下的顺序
    /// （UX5176S 记录序是 PO#/Part No/Q'TY/Lot No/Date Code/Package/BIN，视觉序却是
    /// PO#/Part No/Package/Date Code/Lot No/Q'TY/BIN）。因此按行业通用字段顺序给 rank，
    /// 未知标签排末尾并保持记录顺序。
    /// 大小写必须敏感：UX51xx 用 "Date Code"（第 4 行），TIA 用 "DATE CODE"（倒数第 2 行）。
    /// </summary>
    private static int LabelVisualRank(string label)
    {
        var k = NormalizeLabel(label);
        if (k is "订单号" or "PO#" or "PO" or "Order") return 0;
        if (k is "产品型号" or "Part No" or "Part NO" or "PartNo" or "型号") return 10;
        if (k is "Package" or "package" or "PACKAGE" or "批号" or "批次号") return 20;
        if (k is "Date Code" or "Date code") return 30;
        if (k is "Lot No" or "Lot NO" or "LOT ID" or "L/N" or "lot no" or "Lot Id") return 40;
        if (k is "片号") return 42;
        if (k is "编号") return 44;
        if (k is "Q'TY" or "QTY" or "数量") return 50;
        if (k is "DATE CODE" or "DC" or "D/C") return 55;
        if (k is "BIN" or "bin" or "Bin") return 60;
        if (k is "Track ID" or "Tracking NO" or "Tracking No") return 70;
        return 100;
    }

    private static List<string> VisualOrderedLabels(IEnumerable<string> labels) =>
        labels.Select((t, i) => (Text: t, Index: i))
              .OrderBy(x => LabelVisualRank(x.Text))
              .ThenBy(x => x.Index)
              .Select(x => x.Text)
              .ToList();

    /// <summary>把标签绑定到当前模板里实际存在、且尚未被占用的变量 key。</summary>
    private static string? BindLabelKey(string label, List<TemplateVar> variables,
        HashSet<string> usedKeys)
    {
        foreach (var c in LabelKeyCandidates(label))
            if (variables.Any(v => v.Key == c) && usedKeys.Add(c)) return c;
        return null;
    }

    /// <summary>
    /// 按标签语义从样例值里挑一个最像的值。认领过的样例值不再参与后续匹配，
    /// 剩下未被认领的就是「右栏独立文字」（如 5176 / S02T4H1 / MSL:3）。
    /// 4 位纯数字留给 DATE CODE，数量只认 5~7 位，避免两者互抢。
    /// </summary>
    private static string? PickSampleForLabel(string label, List<string> pool,
        HashSet<string> used)
    {
        var k = NormalizeLabel(label);
        string? Pick(Func<string, bool> pred) =>
            pool.FirstOrDefault(s => !used.Contains(s) && pred(s));
        return k switch
        {
            "订单号" or "PO#" or "Order" or "一维码" =>
                Pick(s => Regex.IsMatch(s, @"^[A-Za-z]{1,4}[-_]\d{6}[-_]\d{2}", RegexOptions.IgnoreCase))
                ?? Pick(s => s.Length >= 10 && s.Count(c => c == '-') >= 2),
            "产品型号" or "Part No" or "Part NO" or "PartNo" or "型号" =>
                Pick(s => Regex.IsMatch(s, @"^[A-Z]{2,4}\d{3,5}[A-Z0-9]*$")),
            "Package" or "package" or "PACKAGE" =>
                Pick(s => s.Contains('*') ||
                          Regex.IsMatch(s, @"^(QFN|BGA|QFP|SOP|SOT|DFN|LGA)", RegexOptions.IgnoreCase)),
            "Date Code" or "Date code" =>
                Pick(s => Regex.IsMatch(s, @"^[A-Z0-9]{9,11}$") && s.Any(char.IsDigit) && s.Any(char.IsUpper)),
            "DATE CODE" or "DC" or "D/C" =>
                Pick(s => Regex.IsMatch(s, @"^\d{4}$")),
            // UX51xx 的 Lot No / L/N 是 S7T405；TIA 的 LOT ID 是 N41474.1、
            // 批号是 C2Fx2X8，三者形态不同，分开匹配才不会互抢。
            "Lot No" or "Lot NO" or "L/N" or "lot no" =>
                Pick(s => Regex.IsMatch(s, @"^[A-Z]\d[A-Z]\d{3}$")),
            "LOT ID" or "Lot Id" =>
                Pick(s => Regex.IsMatch(s, @"^[A-Z]\d{5}\.\d$"))
                ?? Pick(s => Regex.IsMatch(s, @"^[A-Za-z0-9]{6,10}$")
                            && s.Any(char.IsUpper) && s.Any(char.IsLower) && s.Any(char.IsDigit)),
            "批号" or "批次号" =>
                Pick(s => Regex.IsMatch(s, @"^[A-Za-z0-9]{6,10}$")
                         && s.Any(char.IsUpper) && s.Any(char.IsLower) && s.Any(char.IsDigit)),
            "Q'TY" or "QTY" or "数量" =>
                Pick(s => Regex.IsMatch(s, @"^\d{5,7}$")),
            "BIN" or "bin" or "Bin" =>
                Pick(s => s.StartsWith("BIN", StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
    }

    /// <summary>
    /// 样例值 → 变量预览值，并返回「未被标签认领」的值供右栏独立文字使用。
    /// 旧版按剩余变量顺序兜底回填，会把 BIN1 / pcs / MSL:3 错配到 seq3 / lot3 / dcode3。
    /// </summary>
    private static (Dictionary<string, string> ByKey, List<string> Free) BuildSamples(
        LsdImportResult r, List<TemplateVar> variables)
    {
        var pool = r.SampleValues
            .Where(s => s.Length is >= 2 and <= 30)
            .Where(s => !Regex.IsMatch(s, @"^12345678$"))   // 输入模板占位，不是真实值
            .ToList();
        var usedSamples = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var label in VisualOrderedLabels(r.StaticLabels))
        {
            var sample = PickSampleForLabel(label, pool, usedSamples);
            if (sample != null) usedSamples.Add(sample);
            var key = BindLabelKey(label, variables, usedKeys);
            if (key != null && sample != null) byKey[key] = sample;
        }
        return (byKey, pool.Where(s => !usedSamples.Contains(s)).ToList());
    }

    private static List<string> FindFonts(byte[] data)
    {
        var result = new List<string>();
        var text = Gbk.GetString(data);   // 字体名是 GBK，Latin1 扫不到「微软雅黑」
        foreach (var f in FontHints)
            if (text.Contains(f, StringComparison.OrdinalIgnoreCase) && !result.Contains(f))
                result.Add(f);
        return result;
    }

    private static string? FindPrinter(byte[] data)
    {
        var head = Encoding.Latin1.GetString(data, 0, Math.Min(data.Length, 2000));
        foreach (var brand in PrinterBrands)
        {
            var i = head.IndexOf(brand, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var end = i;
            while (end < head.Length && head[end] is >= ' ' and <= '~') end++;
            var s = head[i..end].Trim();
            if (s.Length is >= 5 and <= 40) return s;
        }
        return null;
    }

    private static int ReadI32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        var last = haystack.Length - needle.Length;
        for (var i = Math.Max(0, from); i <= last; i++)
        {
            var k = 0;
            while (k < needle.Length && haystack[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }

    // ---------- 转成系统模板 ----------

    /// <summary>
    /// 把解析结果转成 TemplateDoc。版面按 .lsd 实测包围盒生成；若用户改了幅面，
    /// 按比例缩放所有元素坐标，保证不会跑到标签外面。
    /// </summary>
    public static TemplateDoc BuildDoc(LsdImportResult r, string code, string name,
        double widthMm, double heightMm, int dpi)
    {
        var variables = new List<TemplateVar>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < r.RawVariableNames.Count; i++)
        {
            var raw = r.RawVariableNames[i];
            var (key, shortName) = VarMap.TryGetValue(raw, out var m) ? m : ($"v{i + 1}", raw);
            if (!used.Add(key)) key = $"{key}{i + 1}";
            variables.Add(new TemplateVar
            {
                Key = key,
                Label = raw,
                Short = shortName,
                Source = "scan",
                SortNo = i,
            });
        }

        // 样例值按标签语义回填（只影响预览）；未被认领的值交给右栏独立文字
        var (sampleByKey, freeSamples) = BuildSamples(r, variables);
        foreach (var v in variables)
            v.Sample = sampleByKey.TryGetValue(v.Key, out var s) ? s : $"[{v.Key}]";

        var elements = BuildElements(r, variables, freeSamples);
        var sx = r.WidthMm > 0 ? widthMm / r.WidthMm : 1.0;
        var sy = r.HeightMm > 0 ? heightMm / r.HeightMm : 1.0;
        if (Math.Abs(sx - 1) > 1e-6 || Math.Abs(sy - 1) > 1e-6)
        {
            foreach (var el in elements)
            {
                el.X = Math.Round(el.X * sx, 2);
                el.Y = Math.Round(el.Y * sy, 2);
                el.W = Math.Round(el.W * sx, 2);
                el.H = Math.Round(el.H * sy, 2);
                if (el.X2.HasValue) el.X2 = Math.Round(el.X2.Value * sx, 2);
                if (el.Y2.HasValue) el.Y2 = Math.Round(el.Y2.Value * sy, 2);
                if (el.SizeMm.HasValue) el.SizeMm = Math.Round(el.SizeMm.Value * (sx + sy) / 2, 2);
            }
        }

        var labels = r.StaticLabels;
        var samples = r.SampleValues;
        var hint = new StringBuilder();
        hint.Append($"由 .lsd「{r.FileName}」导入：{r.WidthMm:0.##}×{r.HeightMm:0.##}mm 实测幅面，");
        hint.Append($"按 {elements.Count(e => e.Type == "text")} 行实测文本包围盒 1:1 重建版面。");
        if (labels.Count > 0) hint.Append($" 静态文字：{string.Join(" / ", labels.Take(20))}。");
        if (samples.Count > 0) hint.Append($" 原样例值：{string.Join(" / ", samples.Take(20))}。");
        hint.Append(" 文字与变量的对应顺序、字体粗细需按实物在编辑器中核对微调。");

        return new TemplateDoc
        {
            Id = code,
            Name = name,
            WidthMm = widthMm,
            HeightMm = heightMm,
            Dpi = dpi,
            SavedTitle = r.SavedTitle,
            SourceFile = r.FileName,
            PrinterHint = r.PrinterHint,
            Note = hint.ToString(),
            Variables = variables,
            Elements = elements,
        };
    }

    private static List<TemplateElement> BuildElements(LsdImportResult r,
        List<TemplateVar> variables, List<string> freeSamples)
    {
        var width = r.WidthMm;
        var height = r.HeightMm;
        var boxes = new List<LsdBox>(r.Boxes);

        // 类名提示的框若在颜色字记录里缺失，补进来（否则整条码/边框会丢失）
        foreach (var h in r.ClassBoxes)
        {
            if (!boxes.Any(b => Near(b, h))) boxes.Add(h);
        }

        string? HintOf(LsdBox b) => r.ClassBoxes.FirstOrDefault(h => Near(b, h))?.Type is { Length: > 0 } t ? t : null;

        // 正文字高 ≈ 包围盒高度众数（0.1mm 粒度）
        var hMode = boxes.Count > 0
            ? boxes.GroupBy(b => Math.Round((b.Y2 - b.Y1) / 100.0, 1))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key
            : 3.0;

        (double X, double Y, double W, double H)? barcode = null;
        (double X, double Y, double Size)? qr = null;
        // 一个标签可能有多个二维码（如 UX5176S 右上 + 右下），全部收集，按位置依次绑定 qr1/qr2…
        var qrs = new List<(double X, double Y, double Size)>();
        var shapes = new List<(double X, double Y, double W, double H, string Type)>();
        var texts = new List<LsdBox>();

        // 有类名提示的优先，避免噪声框抢先占掉条码/二维码
        foreach (var b in boxes.OrderBy(x => HintOf(x) == null ? 1 : 0))
        {
            double x1 = b.X1 / 100.0, y1 = b.Y1 / 100.0;
            double w = (b.X2 - b.X1) / 100.0, h = (b.Y2 - b.Y1) / 100.0;
            var hint = HintOf(b);

            if (hint is "矩形" or "线条")
            {
                shapes.Add((x1, y1, w, h, hint));
                continue;
            }
            if (hint == "图片") continue;      // 用内置矢量 Pb 标志重绘
            if (hint == "一维码")
            {
                barcode ??= (x1, y1, w, h);
                continue;
            }
            if (hint == "文本")
            {
                if (Math.Abs(w - h) <= 0.3 && w >= 8) AddQr(x1, y1, w);
                else texts.Add(b);
                continue;
            }
            if (hint == "变量表") continue;

            // 无提示 → 几何推断
            if (Math.Abs(w - h) <= 0.3 && w >= 8)
            {
                AddQr(x1, y1, w);
            }
            else if (w > 20 && h > 6)
            {
                barcode ??= (x1, y1, w, h);
            }
            else if (Math.Abs(h - hMode) <= 0.4 && w is >= 0.8 and <= 45)
            {
                texts.Add(b);
            }
        }

        // 二维码去重（同一框可能被颜色字记录与类名记录各读到一次）
        void AddQr(double x, double y, double size)
        {
            if (qrs.Any(q => Math.Abs(q.X - x) < 1.0 && Math.Abs(q.Y - y) < 1.0)) return;
            qrs.Add((x, y, size));
            if (qr == null || size > qr.Value.Size) qr = (x, y, size);
        }

        var elements = new List<TemplateElement>();

        // 一维码：内容优先绑订单号，其次追踪号，再退到第一个变量
        var barVar = variables.FirstOrDefault(v => v.Key == "order")
                     ?? variables.FirstOrDefault(v => v.Key.StartsWith("track", StringComparison.OrdinalIgnoreCase))
                     ?? variables.FirstOrDefault();
        var barBottom = 0.0;

        if (barcode.HasValue)
        {
            var bx = Math.Max(1.5, barcode.Value.X);
            var by = Math.Max(1.5, barcode.Value.Y);
            var bw = Math.Min(barcode.Value.W, Math.Max(6, width - bx - 1.5));
            var bh = barcode.Value.H;
            elements.Add(new TemplateElement
            {
                Type = "barcode", X = Math.Round(bx, 2), Y = Math.Round(by, 2),
                W = Math.Round(bw, 2), H = Math.Round(bh, 2),
                Text = barVar != null ? "{{" + barVar.Key + "}}" : "[barcode]",
                Symbology = "code128",
            });
            barBottom = by + bh;
        }

        // ---------- 正文：按真实包围盒的几何位置重建 ----------
        // .lsd 把「文字内容」和「对象包围盒」分成两段独立序列化，顺序不对应。
        // 因此：按 y 把文本框分组得到真实行位，按 x 区分「左栏标签行」与「右栏独立文字」；
        // 标签按行业通用视觉序均匀铺在实测首末行之间（实测包围盒常漏记 1 行），
        // 右栏独立文字用「未被标签认领的样例值」按框宽就近匹配。
        if (texts.Count > 0)
        {
            var font = r.Fonts.FirstOrDefault(f => f.Any(c => c > 0x2E80)) ?? r.Fonts.FirstOrDefault();
            var rowH = texts.GroupBy(b => Math.Round((b.Y2 - b.Y1) / 100.0, 1))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;

            // 左右分栏：右栏至少要有两个框，避免把单栏模板里偏右的「值框」误判为右栏
            var mid = width * 0.55;
            var rightBoxes = texts.Where(b => b.X1 / 100.0 >= mid).ToList();
            var split = rightBoxes.Count >= 2;
            if (!split) rightBoxes.Clear();
            var leftBoxes = split ? texts.Where(b => b.X1 / 100.0 < mid).ToList() : texts;

            // 同一行的「标签框 + 值框」y 常有 0.08mm 级差，容差 0.6mm 合并
            List<List<LsdBox>> GroupRows(IEnumerable<LsdBox> src)
            {
                var groups = new List<List<LsdBox>>();
                foreach (var b in src.OrderBy(b => b.Y1).ThenBy(b => b.X1))
                {
                    var y = b.Y1 / 100.0;
                    var g = groups.LastOrDefault(t => Math.Abs(t.Average(u => u.Y1 / 100.0) - y) < 0.6);
                    if (g == null) groups.Add(g = new List<LsdBox>());
                    g.Add(b);
                }
                return groups;
            }
            var rows = GroupRows(leftBoxes);
            var rightRows = GroupRows(rightBoxes);

            var labels = VisualOrderedLabels(r.StaticLabels);
            if (labels.Count == 0)
                labels = variables
                    .Where(v => !v.Key.StartsWith("qr", StringComparison.OrdinalIgnoreCase))
                    .Take(8).Select(v => (v.Short ?? v.Key) + "：").ToList();

            var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? Bind(string label) => BindLabelKey(label, variables, usedKeys);

            // 统一 x / 右边界：个别窄框（如 0.85mm 分隔框）会把整行字压得极小，必须统一
            var leftX = leftBoxes.Min(b => b.X1) / 100.0;
            var measuredRight = leftBoxes.Max(b => b.X2) / 100.0;
            var qrLeft = qrs.Count > 0 ? qrs.Min(q => q.X) : width;
            var rightLeft = rightRows.Count > 0
                ? rightRows.Min(g => g.Min(b => b.X1) / 100.0) : width;
            var rightEdge = Math.Min(width - 1.0,
                Math.Max(measuredRight + 1.5, Math.Min(qrLeft, rightLeft) - 1.0));
            var rowW = Math.Max(8.0, rightEdge - leftX);

            // 实测首末行之间均匀铺所有标签，补齐漏记行且不越界
            var y0 = rows.Count > 0 ? rows.Min(g => g.Min(b => b.Y1) / 100.0) : 2.0;
            var yLast = rows.Count > 0 ? rows.Max(g => g.Min(b => b.Y1) / 100.0) : y0 + rowH * 6;
            var n = labels.Count;
            var step = n > 1 ? (yLast - y0) / (n - 1) : rowH + 1.0;

            // 中文标签原模板多为黑体（粗），纯西文标签（Arial）保持常规字重
            var bold = labels.Any(l => l.Any(c => c > 0x2E80));
            var unitWord = freeSamples.FirstOrDefault(s =>
                string.Equals(s, "pcs", StringComparison.OrdinalIgnoreCase));

            for (var i = 0; i < n; i++)
            {
                var label = labels[i];
                var key = Bind(label);
                // 原标签西文冒号后带一个空格（PO#: WX-...），中文全角冒号自带间距
                var gap = label.Any(c => c > 0x2E80) ? "" : " ";
                var text = label + (key != null ? gap + "{{" + key + "}}" : "");
                var bare = NormalizeLabel(label);
                if (unitWord != null &&
                    (bare.Equals("Q'TY", StringComparison.OrdinalIgnoreCase) ||
                     bare.Equals("QTY", StringComparison.OrdinalIgnoreCase) || bare == "数量"))
                    text += " " + unitWord;
                AddText(leftX, Math.Round(y0 + i * step, 2), rowW, rowH, text, font, bold);
            }

            // 右栏独立文字（如 5176 / S02T4H1 / MSL:3）：只用未被标签认领的样例值
            var usedFree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rightRows.OrderBy(g => g.Min(b => b.Y1)))
            {
                var rx = row.Min(b => b.X1) / 100.0;
                var ry = row.Min(b => b.Y1) / 100.0;
                var rw = Math.Max(4.0, row.Max(b => b.X2) / 100.0 - rx);
                var rh = Math.Max(1.2, row.Max(b => b.Y2) / 100.0 - ry);
                var targetEm = rw / Math.Max(1.0, rh * 0.9);
                var best = freeSamples
                    .Where(s => !usedFree.Contains(s) && s.Length >= 2)
                    .Where(s => !string.Equals(s, unitWord, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => Math.Abs(TextWidth(s) - targetEm))
                    .FirstOrDefault();
                if (best == null) continue;
                usedFree.Add(best);
                AddText(rx, ry, Math.Min(rw, width - rx - 1.0), rh, best, font, bold);
            }

            void AddText(double x, double y, double w, double h, string text,
                string? fontName, bool isBold)
            {
                elements.Add(new TemplateElement
                {
                    Type = "text", X = Math.Round(x, 2), Y = Math.Round(y, 2),
                    W = Math.Round(w, 2), H = Math.Round(Math.Max(1.2, h), 2),
                    Text = text, Font = fontName, Bold = isBold,
                    SizeMm = Math.Clamp(Math.Round(Math.Max(1.2, h) * 0.9, 2), 1.2, 12.0),
                    Align = "left", Valign = "middle",
                });
            }
        }
        else if (barcode.HasValue)
        {
            // 极少数样本读不到文本包围盒：退回旧栅格，保证仍生成可用模板
            BuildFallbackBody(r, variables, barVar,
                variables.FirstOrDefault(v => v.Key.StartsWith("qr", StringComparison.OrdinalIgnoreCase)),
                qr, barBottom, elements);
        }

        // 二维码：按从上到下顺序依次绑定 qr1 / qr2…
        var qrVars = variables.Where(v => v.Key.StartsWith("qr", StringComparison.OrdinalIgnoreCase)).ToList();
        var orderedQrs = qrs.OrderBy(q => q.Y).ThenBy(q => q.X).ToList();
        // 部分 .lsd（如 UX5176S）第二个二维码没有颜色字包围盒记录，但变量表里明确有
        // qr1/qr2。按第一个二维码的 x/size 在幅面下方补位，避免右侧整块留空。
        if (qrVars.Count > orderedQrs.Count && orderedQrs.Count > 0)
        {
            var first = orderedQrs[0];
            for (var i = orderedQrs.Count; i < qrVars.Count; i++)
            {
                var y = height - first.Size - 2.0 - (i - 1) * (first.Size + 2.0);
                if (y < first.Y + first.Size + 1.0) break;
                orderedQrs.Add((first.X, y, first.Size));
            }
        }
        for (var i = 0; i < orderedQrs.Count; i++)
        {
            var q = orderedQrs[i];
            var qs = q.Size;
            var qx = Math.Min(Math.Max(1.5, q.X), Math.Max(1.5, width - qs - 1.5));
            var qy = Math.Min(Math.Max(1.5, q.Y), Math.Max(1.5, height - qs - 1.0));
            var v = i < qrVars.Count ? qrVars[i] : null;
            elements.Add(new TemplateElement
            {
                Type = "qrcode", X = Math.Round(qx, 2), Y = Math.Round(qy, 2),
                W = Math.Round(qs, 2), H = Math.Round(qs, 2), SizeMm = Math.Round(qs, 2),
                Text = v != null ? "{{" + v.Key + "}}" : "[qr]",
                Valign = "middle",
            });
        }

        // 边框 / 线条（实测位置；线宽读不到，统一 0.15mm）
        foreach (var (sx, sy, sw, sh, st) in shapes)
        {
            if (st == "线条")
            {
                if (sw >= sh)
                    elements.Add(new TemplateElement
                    {
                        Type = "line", X = Math.Round(sx, 2), Y = Math.Round(sy + sh / 2, 2),
                        X2 = Math.Round(sx + sw, 2), Y2 = Math.Round(sy + sh / 2, 2), ThicknessMm = 0.15,
                    });
                else
                    elements.Add(new TemplateElement
                    {
                        Type = "line", X = Math.Round(sx + sw / 2, 2), Y = Math.Round(sy, 2),
                        X2 = Math.Round(sx + sw / 2, 2), Y2 = Math.Round(sy + sh, 2), ThicknessMm = 0.15,
                    });
            }
            else
            {
                elements.Add(new TemplateElement
                {
                    Type = "rect", X = Math.Round(sx, 2), Y = Math.Round(sy, 2),
                    W = Math.Round(sw, 2), H = Math.Round(sh, 2), ThicknessMm = 0.15,
                });
            }
        }

        // Pb 无铅标志：原 .lsd 是内嵌位图，用内置矢量符号按实测位置重绘
        var pb = r.Pictures.FirstOrDefault(p => p.LikelyPbMark);
        if (pb != null)
        {
            elements.Add(new TemplateElement
            {
                Type = "symbol", Text = "pb-free",
                X = pb.XMm, Y = pb.YMm, W = pb.WMm, H = pb.HMm,
            });
        }

        return elements;
    }

    /// <summary>
    /// 估算文字渲染宽度（em 单位），仅用于「文字 ↔ 包围盒」的排序匹配：
    /// 中文按 1em、西文大写按 0.62em、小写/数字按 0.55em、标点按 0.3em。
    /// 不需要绝对精确，只要相对顺序稳定即可。
    /// </summary>
    private static double TextWidth(string s)
    {
        double w = 0;
        foreach (var c in s)
        {
            if (c > 0x2E80) w += 1.0;
            else if (char.IsUpper(c)) w += 0.62;
            else if (char.IsLetterOrDigit(c)) w += 0.55;
            else w += 0.3;
        }
        return w;
    }

    /// <summary>
    /// 标签 → 候选变量 key。.lsd 有两套命名体系（TIA: order/partno/batch…；
    /// UX51xx: track3/partno3/package3…），同一个标签在不同模板里对应不同 key，
    /// 因此返回候选列表，由调用方按「当前模板实际存在的变量」挑选。
    /// </summary>
    private static string[] LabelKeyCandidates(string label)
    {
        var k = NormalizeLabel(label);
        // 必须用 Ordinal：同时存在 Package/package、Part No/Part NO、Date Code/DATE CODE，
        // 忽略大小写会造成重复键并让字典构造直接抛 ArgumentException。
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["订单号"] = new[] { "order", "track3", "track" },
            ["PO#"] = new[] { "po", "track3", "order", "track" },
            ["产品型号"] = new[] { "partno", "partno3", "part_no" },
            ["Part No"] = new[] { "partno3", "partno", "part_no" },
            ["Part NO"] = new[] { "partno3", "partno", "part_no" },
            ["批号"] = new[] { "batch", "batch3", "batch2" },
            ["Package"] = new[] { "package3", "package" },
            ["package"] = new[] { "package3", "package" },
            ["Date Code"] = new[] { "dcode3", "dcode2", "dcode" },
            ["DATE CODE"] = new[] { "dcode", "dcode3", "dcode2" },
            ["D/C"] = new[] { "dcode", "dcode3" },
            ["Lot No"] = new[] { "lot3", "lot2", "lot" },
            ["LOT ID"] = new[] { "lot", "lot3" },
            ["Q'TY"] = new[] { "qty" },
            ["QTY"] = new[] { "qty" },
            ["数量"] = new[] { "qty" },
            ["BIN"] = new[] { "bin", "bin3" },
            ["Bin"] = new[] { "bin", "bin3" },
            ["片号"] = new[] { "wafer" },
            ["编号"] = new[] { "serial" },
            ["Tracking NO"] = new[] { "track", "track3", "order", "po" },
            ["Track ID"] = new[] { "track", "track3", "order", "po" },
        };
        if (map.TryGetValue(k, out var keys)) return keys;
        if (LabelKeyMap.TryGetValue(k, out var single)) return new[] { single };
        foreach (var kv in LabelKeyMap)
            if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase)) return new[] { kv.Value };
        return Array.Empty<string>();
    }

    /// <summary>读不到文本包围盒时的兜底：按变量名表铺栅格（旧行为）。</summary>
    private static void BuildFallbackBody(LsdImportResult r, List<TemplateVar> variables,
        TemplateVar? barVar, TemplateVar? qrVar, (double X, double Y, double Size)? qr,
        double barBottom, List<TemplateElement> elements)
    {
        var hMode = 3.0;
        var body = variables.Where(v => v != barVar && v != qrVar).ToList();
        var qrLeft = qr.HasValue ? qr.Value.X - 1.0 : r.WidthMm;
        var band = Math.Max(20.0, Math.Min(r.WidthMm - 1.5, qrLeft) - 1.5);
        var top = barBottom > 0 ? Math.Round(barBottom + 1.6, 1) : 2.0;
        var availH = Math.Max(10.0, r.HeightMm - top - 1.5);
        var rows = Math.Max(1, body.Count);
        var pitch = Math.Min(Math.Round(hMode + 1.1, 2), Math.Round(availH / rows, 2));
        var sizeMm = Math.Max(1.2, Math.Round(Math.Min(hMode * 0.95, pitch * 0.66), 2));
        var capW = Math.Min(12.0, Math.Round(band * 0.5, 2));
        var valW = Math.Max(6.0, band - capW - 0.6);

        for (var i = 0; i < body.Count; i++)
        {
            var v = body[i];
            var y = Math.Round(top + i * pitch + (pitch - sizeMm) / 2, 2);
            elements.Add(new TemplateElement
            {
                Type = "text", X = 1.5, Y = y, W = capW, H = Math.Round(sizeMm * 1.2, 2),
                Text = (v.Short ?? v.Key) + "：", SizeMm = sizeMm, Align = "left", Valign = "middle",
            });
            elements.Add(new TemplateElement
            {
                Type = "text", X = Math.Round(1.5 + capW, 2), Y = y, W = Math.Round(valW, 2),
                H = Math.Round(sizeMm * 1.2, 2), Text = "{{" + v.Key + "}}",
                SizeMm = sizeMm, Align = "left", Valign = "middle",
            });
        }
    }

    private static bool Near(LsdBox a, LsdBox b) =>
        Math.Abs(a.X1 - b.X1) <= 3 && Math.Abs(a.Y1 - b.Y1) <= 3
        && Math.Abs(a.X2 - b.X2) <= 3 && Math.Abs(a.Y2 - b.Y2) <= 3;
}
