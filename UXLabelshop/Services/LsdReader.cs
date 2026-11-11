using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace UXLabelshop.Services;

/// <summary>.lsd 里的一个图片对象（CDrawPicture）。</summary>
public record LsdPicture(double XMm, double YMm, double WMm, double HMm, int Offset, bool LikelyPbMark);

/// <summary>
/// 签赋 LabelShop .lsd 文件的定点取证读取器。
///
/// 背景（2026-11 实测 8 个样本，LabelShop Professional 2.27.4 Build 1339）：
///   .lsd 是「纯文本头 + MFC 风格二进制流」的混合容器，对象记录的形态是
///       &lt;长度前缀&gt; "CDraw" + 类名 &lt;1 字节类号&gt; &lt;4 字节版本&gt; &lt;8 字节保留&gt;
///       &lt;int32 x1&gt;&lt;int32 y1&gt;&lt;int32 x2&gt;&lt;int32 y2&gt; ...
///   包围盒单位是 0.01mm，原点在标签左上角。
///
/// Pb 无铅标志就是一个 <b>CDrawPicture 对象</b>（内嵌 LZSS 压缩位图），
/// 因此「识别标志」= 「找到类名为 CDrawPicture 的记录并读它的包围盒」，
/// 不需要解码位图——标志本身用内置矢量图 <see cref="Rendering.Symbols"/> 重绘。
/// </summary>
public static class LsdReader
{
    private static readonly byte[] PictureTag = Encoding.ASCII.GetBytes("CDrawPicture");
    private static readonly byte[] PictureTagLegacy = Encoding.ASCII.GetBytes("CDrawImage");

    /// <summary>读文件并返回全部图片对象。</summary>
    public static List<LsdPicture> FindPictures(string lsdPath) =>
        FindPictures(File.ReadAllBytes(lsdPath));

    public static List<LsdPicture> FindPictures(byte[] data)
    {
        var result = new List<LsdPicture>();
        var page = TryReadPageSize(data);           // 单位 0.01mm，未知时用 200mm 上限兜底
        var maxW = page?.W ?? 20000;
        var maxH = page?.H ?? 20000;

        foreach (var tag in new[] { PictureTag, PictureTagLegacy })
        {
            var from = 0;
            while (true)
            {
                var at = IndexOf(data, tag, from);
                if (at < 0) break;
                from = at + tag.Length;

                // 正常偏移是类名后 12 字节（类号 4 + 版本 4 + 保留 8），
                // 再按 4 字节步长容错搜索，但只接受落在页面内的包围盒。
                foreach (var delta in new[] { 12, 8, 16, 20, 24, 28 })
                {
                    var o = at + tag.Length + delta;
                    if (o + 16 > data.Length) break;
                    var x1 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o));
                    var y1 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o + 4));
                    var x2 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o + 8));
                    var y2 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o + 12));
                    if (!Plausible(x1, y1, x2, y2, maxW, maxH)) continue;

                    var w = (x2 - x1) / 100.0;
                    var h = (y2 - y1) / 100.0;
                    var ratio = w / h;
                    result.Add(new LsdPicture(
                        Math.Round(x1 / 100.0, 2), Math.Round(y1 / 100.0, 2), Math.Round(w, 2), Math.Round(h, 2),
                        at, ratio is >= 0.85 and <= 1.18 && w is >= 3.0 and <= 12.0 && Math.Abs(w - h) <= 1.0));
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>标签幅面（单位 0.01mm）。规则见 label-system-kit/tools/lsd_probe.py：第一个 "&gt;&lt;" 之后 ~500 字节内、间隔 44 字节重复出现的 int32 对。</summary>
    public static (int W, int H)? TryReadPageSize(byte[] data)
    {
        var anchor = IndexOf(data, new[] { (byte)'>', (byte)'<' }, 0);
        if (anchor < 0) return null;

        var a = Math.Max(0, anchor - 100);
        var b = Math.Min(data.Length, anchor + 500);
        var seen = new Dictionary<(int, int), List<int>>();
        for (var o = a; o < b - 8; o++)
        {
            var w = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o));
            var h = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o + 4));
            if (w is >= 2000 and <= 20000 && h is >= 1000 and <= 10000)
            {
                if (!seen.TryGetValue((w, h), out var list)) seen[(w, h)] = list = new List<int>();
                list.Add(o);
            }
        }
        foreach (var ((w, h), offs) in seen)
            foreach (var o1 in offs)
                if (offs.Any(o2 => o2 - o1 == 44))
                    return (w, h);
        return null;
    }

    /// <summary>Pb 无铅标志（近似方形、边长 3~12mm 的图片对象）。</summary>
    public static LsdPicture? FindPbMark(string lsdPath) =>
        FindPictures(lsdPath).FirstOrDefault(p => p.LikelyPbMark);

    private static bool Plausible(int x1, int y1, int x2, int y2, int maxW, int maxH) =>
        x1 >= 0 && y1 >= 0 && x1 < x2 && y1 < y2
        && x2 <= maxW + 1 && y2 <= maxH + 1     // 必须落在标签幅面内（0.01mm）
        && x2 - x1 >= 30 && y2 - y1 >= 30       // ≥0.3mm，排除噪声
        && x2 - x1 <= maxW && y2 - y1 <= maxH;

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
}
