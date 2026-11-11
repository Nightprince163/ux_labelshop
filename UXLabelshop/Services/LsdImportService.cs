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
    /// <summary>标签打印常见字段的视觉行序（LabelShop TIA/UX 系列实测）。</summary>
    private static readonly string[] LabelVisualOrder =
    {
        "订单号", "产品型号", "批号", "LOT ID", "片号", "编号",
        "数量", "DATE CODE", "D/C", "BIN",
        "PO#", "Package", "Part NO", "Part No", "lot no", "Lot No", "Track ID",
        "型号", "型号3", "型号4", "批次", "批次号", "L/N", "DC", "Date Code",
    };

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
    /// <summary>
    /// 静态标签按「视觉行序」返回。.lsd 里对象按创建顺序序列化，和标签上从上到下的
    /// 顺序不一致；而「文字↔包围盒」的对应关系在压缩块里读不到，因此用标签打印的
    /// 常见字段顺序做稳定重排（仅当命中 ≥3 个已知字段才启用，避免打乱其它字段体系）。
    /// </summary>
    public List<string> StaticLabels
    {
        get
        {
            var list = ConstTexts.Where(t => t.IsLabel).Select(t => t.Text).ToList();
            int Rank(string s)
            {
                var k = s.Trim().TrimEnd(':', '：').Trim();
                for (var i = 0; i < LabelVisualOrder.Length; i++)
                    if (string.Equals(LabelVisualOrder[i], k, StringComparison.OrdinalIgnoreCase)) return i;
                return int.MaxValue;
            }
            if (list.Count(s => Rank(s) != int.MaxValue) < 3) return list;
            var known = list.Where(s => Rank(s) != int.MaxValue).OrderBy(Rank).ToList();
            var unknown = list.Where(s => Rank(s) == int.MaxValue).ToList();
            return known.Concat(unknown).ToList();
        }
    }
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
    /// 明文段：长度前缀(1 字节) + GBK 字符串。每个文本对象都存着自己的显示文字
    /// （静态标签，如「订单号:」）或上次输入的样例值（如「WX-251219-03」）。
    /// 需跳过压缩编码块、文件头、字体名、打印机名和 LabelShop 固定的输入提示串。
    /// </summary>
    private static List<LsdConstText> ReadConstTexts(byte[] data, string? title, string? printer)
    {
        var result = new List<LsdConstText>();
        var skip = EncodedRanges(data);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 200; i + 2 < data.Length; i++)
        {
            var len = data[i];
            if (len is < 2 or > 40 || i + 1 + len > data.Length) continue;
            if (InRanges(i, skip)) continue;
            var body = Gbk.GetString(data, i + 1, len);
            if (body.Contains('\ufffd') || body.Any(c => c < 32)) continue;
            var t = body.Trim();
            if (t.Length < 2 || IsNoise(t, title, printer)) continue;

            var isLabel = t.EndsWith(":", StringComparison.Ordinal) || t.EndsWith("：", StringComparison.Ordinal);
            if (!isLabel && !LooksLikeSample(t)) continue;
            if (!seen.Add(t)) continue;
            result.Add(new LsdConstText(t, i, isLabel));
            i += len;
        }
        return result;
    }

    /// <summary>压缩编码块（自定义 85 字符集，含 QX … &gt;&lt;）范围，块内字符不作文本解析。</summary>
    private static List<(int A, int B)> EncodedRanges(byte[] data)
    {
        var ranges = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i <= data.Length; i++)
        {
            var ok = i < data.Length && data[i] is >= 0x21 and <= 0x7a;
            if (ok) { if (start < 0) start = i; }
            else if (start >= 0)
            {
                if (i - start >= 16)
                {
                    for (var k = start; k + 1 < i; k++)
                    {
                        if (data[k] != 'Q' || data[k + 1] != 'X') continue;
                        ranges.Add((Math.Max(0, start - 4), Math.Min(data.Length, i + 4)));
                        break;
                    }
                }
                start = -1;
            }
        }
        return ranges;
    }

    private static bool InRanges(int at, List<(int A, int B)> ranges) =>
        ranges.Any(r => r.A <= at && at < r.B);

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

    private static bool LooksLikeSample(string t) =>
        t.Length is >= 2 and <= 40
        && t.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '/' or '+' or '#' or ':' or '：')
        && t.Any(char.IsLetterOrDigit);

    /// <summary>用样例值的形态猜它属于哪个变量（只影响预览 Sample，不影响模板结构）。</summary>
    private static string? GuessSampleKey(string sample)
    {
        var s = sample.Trim();
        if (Regex.IsMatch(s, @"^WX[-_]", RegexOptions.IgnoreCase) ||
            (s.Length >= 10 && s.Count(c => c == '-') >= 2)) return "order";      // WX-251219-03
        if (Regex.IsMatch(s, @"^[A-Za-z]\d{4,7}\.\d+$")) return "lot";             // N41474.1
        if (Regex.IsMatch(s, @"^\d{4}$")) return "dcode";                           // 2640
        if (Regex.IsMatch(s, @"^\d{4,7}$")) return "qty";                           // 10208
        if (Regex.IsMatch(s, @"^[A-Za-z0-9]{6,10}$") &&
            s.Any(char.IsUpper) && s.Any(char.IsLower) && s.Any(char.IsDigit)) return "batch"; // C2Fx2X8
        if (Regex.IsMatch(s, @"^[A-Z]{2,4}\d{3,6}[A-Za-z]?$")) return "partno";    // UX2091
        return null;
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
        var sampleByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in r.ConstTexts.Where(t => !t.IsLabel))
        {
            var k = GuessSampleKey(t.Text);
            if (k != null) sampleByKey.TryAdd(k, t.Text);
        }

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
                // 有 .lsd 样例值就用真实值预览；否则必须是 ASCII 占位符（Code128 编不了中文）
                Sample = sampleByKey.TryGetValue(key, out var s) ? s : $"[{key}]",
                SortNo = i,
            });
        }

        var elements = BuildElements(r, variables);
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

    private static List<TemplateElement> BuildElements(LsdImportResult r, List<TemplateVar> variables)
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
                if (Math.Abs(w - h) <= 0.3 && w >= 8)
                {
                    if (qr == null || w > qr.Value.Size) qr = (x1, y1, w);
                }
                else texts.Add(b);
                continue;
            }
            if (hint == "变量表") continue;

            // 无提示 → 几何推断
            if (Math.Abs(w - h) <= 0.3 && w >= 8)
            {
                if (qr == null || w > qr.Value.Size) qr = (x1, y1, w);
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

        var elements = new List<TemplateElement>();

        // 一维码：内容优先绑订单号，其次追踪号，再退到第一个变量
        var barVar = variables.FirstOrDefault(v => v.Key == "order")
                     ?? variables.FirstOrDefault(v => v.Key.StartsWith("track", StringComparison.OrdinalIgnoreCase))
                     ?? variables.FirstOrDefault();
        var qrVar = variables.FirstOrDefault(v => v.Key.StartsWith("qr", StringComparison.OrdinalIgnoreCase));
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

        // ---------- 正文：用 .lsd 的真实文本包围盒按行 1:1 重建 ----------
        // 旧实现把 texts 整包丢弃，改按变量名表铺栅格，导致行数变多、字号被压小、
        // 标签与值被拆成两列。这里改为：同 y 的框合成一行，位置/宽高/字号全部取实测值，
        // 内容用 .lsd 明文段读出的静态标签并绑定对应变量。
        if (texts.Count > 0)
        {
            var left = texts.Min(b => b.X1) / 100.0;
            var rows = texts
                .GroupBy(b => Math.Round(b.Y1 / 100.0, 1))
                .OrderBy(g => g.Key)
                .Select(g => g.OrderBy(b => b.X1).ToList())
                .ToList();

            var labels = r.StaticLabels;
            var labelIdx = 0;
            var fallback = variables.Where(v => v != barVar && v != qrVar).ToList();
            var fallbackIdx = 0;
            var font = r.Fonts.FirstOrDefault(f => f.Any(c => c > 0x2E80)) ?? r.Fonts.FirstOrDefault();

            // 行距：相邻行 y 的中位数，用于补齐末尾缺失包围盒的文字行
            var ys = rows.Select(g => g.Min(b => b.Y1) / 100.0).ToList();
            var gaps = ys.Zip(ys.Skip(1), (a, b) => b - a).Where(g => g > 0.5).OrderBy(g => g).ToList();
            var pitch = gaps.Count > 0 ? gaps[gaps.Count / 2] : 4.0;
            var rowH = rows[0].Max(b => b.Y2) / 100.0 - rows[0].Min(b => b.Y1) / 100.0;

            void AddRow(string text, double y, double w, double h)
            {
                var sizeMm = Math.Clamp(Math.Round(h * 0.9, 2), 1.2, 12.0);
                elements.Add(new TemplateElement
                {
                    Type = "text", X = Math.Round(left, 2), Y = Math.Round(y, 2),
                    W = Math.Round(w, 2), H = Math.Round(h, 2),
                    Text = text, Font = font, Bold = true,
                    SizeMm = sizeMm, Align = "left", Valign = "middle",
                });
            }

            string? NextText()
            {
                if (labelIdx < labels.Count)
                {
                    var label = labels[labelIdx++];
                    var key = LabelKey(label);
                    var v = key != null ? variables.FirstOrDefault(x => x.Key == key) : null;
                    return v != null ? label + "{{" + v.Key + "}}" : label;
                }
                if (fallbackIdx < fallback.Count)
                {
                    var v = fallback[fallbackIdx++];
                    return (v.Short ?? v.Label ?? v.Key) + "：{{" + v.Key + "}}";
                }
                return null;
            }

            // 所有正文行用同一个 W（左边界 → 二维码左侧）：
            // .lsd 里有些行只记录了「值」的小框、没记录标签框，若按各自实测宽度给 W，
            // 渲染器会把短行自适应缩小，出现图1 那种「字号忽大忽小」。统一宽度后字号一致。
            var rightLimit = qr.HasValue
                ? Math.Min(width - 1.5, qr.Value.X - 1.0)
                : width - 1.5;
            var rowW = Math.Max(6.0, rightLimit - left);
            // 补出来的行以标签纸下沿为硬边界；边框底缘允许略微越过（末行文字本就贴着框底）
            var paperBottom = height - 0.3;

            // 行数以静态标签为准：.lsd 常漏记末尾 1~2 行的颜色字记录（如「BIN:」）。
            // 多余行不追加到框外，而是把全部行均匀铺在实测首末行区间内 —— 版面仍基于
            // 实测坐标，字号统一，且末行能留在边框内。
            var firstY = ys[0];
            var lastY = ys[^1];
            var target = Math.Clamp(labels.Count, rows.Count, rows.Count + 2);
            var step = target > 1 ? (lastY - firstY) / (target - 1) : pitch;
            var lineH = Math.Min(rowH, Math.Max(1.2, step * 0.95));

            for (var i = 0; i < target; i++)
            {
                var y = firstY + i * step;
                if (y + lineH > paperBottom) break;
                var text = NextText();
                if (text == null) break;
                AddRow(text, y, rowW, lineH);
            }
        }
        else if (barcode.HasValue)
        {
            // 极少数样本读不到文本包围盒：退回旧栅格，保证仍生成可用模板
            BuildFallbackBody(r, variables, barVar, qrVar, qr, barBottom, elements);
        }

        // 二维码
        if (qr.HasValue && qr.Value.Size > 0)
        {
            var qs = qr.Value.Size;
            var qx = Math.Min(Math.Max(1.5, qr.Value.X), Math.Max(1.5, width - qs - 1.5));
            var qy = Math.Min(Math.Max(1.5, qr.Value.Y), Math.Max(1.5, height - qs - 1.0));
            elements.Add(new TemplateElement
            {
                Type = "qrcode", X = Math.Round(qx, 2), Y = Math.Round(qy, 2),
                W = Math.Round(qs, 2), H = Math.Round(qs, 2), SizeMm = Math.Round(qs, 2),
                Text = qrVar != null ? "{{" + qrVar.Key + "}}" : "[qr]",
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

    private static string? LabelKey(string label)
    {
        var k = label.Trim().TrimEnd(':', '：').Trim();
        if (LabelKeyMap.TryGetValue(k, out var key)) return key;
        foreach (var kv in LabelKeyMap)
            if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
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
