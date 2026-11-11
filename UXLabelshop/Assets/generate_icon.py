"""生成 UX_LabelShop 程序图标（方案 C）。

用法： python Assets/generate_icon.py

产出（均在 Assets/ 目录）：
  app.ico          Windows 多尺寸图标（16/20/24/32/40/48/64/128/256，PNG 压缩）
  app-256.png      256px 预览图
  icon-preview.png 各尺寸排布预览图

设计以 96x96 为单位坐标，与 app.svg 一致；每个尺寸单独按 4 倍超采样绘制，
再 LANCZOS 缩小，避免小尺寸出现锯齿。16/24px 自动简化细节（加粗条码、
去掉折角与文字线），保证任务栏下仍可辨认。
"""

import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

OUT = Path(__file__).resolve().parent

BLUE = (43, 87, 154, 255)      # #2B579A 程序主色
FOLD = (181, 212, 244, 255)    # #B5D4F4 折角
LINE = (133, 169, 214, 255)    # #85A9D6 文字线
WHITE = (255, 255, 255, 255)

BARS_FULL = [  # (x, width)
    (27, 3), (33, 2), (38, 5), (46, 2), (51, 4), (58, 2), (63, 5),
]
BARS_MID = [
    (27, 3.5), (33.5, 2.5), (38, 5), (46, 2.5), (50.5, 4), (57.5, 2.5), (62.5, 5),
]
BARS_TINY = [
    (28, 6), (39, 5), (50, 6), (61, 5),
]

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def make_icon(size: int) -> Image.Image:
    s = 4  # 超采样倍数
    canvas = 96 * s
    im = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    p = lambda v: int(round(v * s))

    # 蓝色圆角底板
    d.rounded_rectangle([p(4), p(4), p(92), p(92)], radius=p(20), fill=BLUE)

    # 白色折角标签
    if size <= 24:
        # 极小尺寸：直矩形 + 一道斜切口，轮廓更干净
        d.polygon([(p(20), p(26)), (p(66), p(26)), (p(76), p(36)),
                   (p(76), p(74)), (p(20), p(74))], fill=WHITE)
    else:
        d.polygon([(p(20), p(26)), (p(64), p(26)), (p(76), p(38)),
                   (p(76), p(74)), (p(20), p(74))], fill=WHITE)
        d.polygon([(p(64), p(26)), (p(76), p(38)), (p(64), p(38))], fill=FOLD)

    # 条码
    if size <= 24:
        bars, top, height = BARS_TINY, 38, 26
    elif size <= 48:
        bars, top, height = BARS_MID, 40, 22
    else:
        bars, top, height = BARS_FULL, 40, 22
    for x, w in bars:
        d.rectangle([p(x), p(top), p(x + w), p(top + height)], fill=BLUE)

    # 底部文字线（仅大尺寸显示）
    if size >= 64:
        d.rounded_rectangle([p(27), p(67), p(61), p(70)], radius=p(1.5), fill=LINE)

    return im.resize((size, size), Image.LANCZOS)


def png_bytes(im: Image.Image) -> bytes:
    buf = io.BytesIO()
    im.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def build_ico(images: list[tuple[int, bytes]]) -> bytes:
    """手写 ICO 容器，每个尺寸内嵌独立 PNG（Vista 及以上支持）。"""
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = bytearray()
    payload = bytearray()
    for size, data in images:
        field = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", field, field, 0, 0, 1, 32,
                              len(data), offset)
        offset += len(data)
        payload += data
    return header + bytes(entries) + bytes(payload)


def font(size: int):
    for path in (r"C:\Windows\Fonts\segoeui.ttf", r"C:\Windows\Fonts\arial.ttf"):
        try:
            return ImageFont.truetype(path, size)
        except OSError:
            continue
    return ImageFont.load_default()


def make_preview(icons: dict[int, Image.Image]) -> Image.Image:
    w, h = 760, 470
    sheet = Image.new("RGB", (w, h), "#FFFFFF")
    d = ImageDraw.Draw(sheet)
    f_title = font(18)
    f_label = font(12)
    d.text((28, 22), "UX_LabelShop  -  icon scheme C", fill="#2C2C2A", font=f_title)
    d.text((28, 50), "16 / 24 / 32 / 48 / 64 px", fill="#5F5E5A", font=f_label)

    small = [16, 24, 32, 48, 64]
    for i, size in enumerate(small):
        cx = 90 + i * 140
        d.rounded_rectangle([cx - 55, 76, cx + 55, 186], radius=10, fill="#F5F7FA")
        im = icons[size]
        sheet.paste(im, (cx - size // 2, 130 - size // 2), im)
        d.text((cx, 198), f"{size} px", fill="#5F5E5A", font=f_label, anchor="ma")

    d.text((28, 234), "128 / 256 px", fill="#5F5E5A", font=f_label)
    for i, size in enumerate((128, 256)):
        cx = 190 + i * 300
        d.rounded_rectangle([cx - 130, 258, cx + 130, 438], radius=10, fill="#F5F7FA")
        im = icons[size]
        sheet.paste(im, (cx - size // 2, 348 - size // 2), im)
    return sheet


def main():
    icons = {size: make_icon(size) for size in SIZES}
    (OUT / "app.ico").write_bytes(
        build_ico([(size, png_bytes(icons[size])) for size in SIZES]))
    icons[256].save(OUT / "app-256.png")
    make_preview(icons).save(OUT / "icon-preview.png")
    print("generated:", ", ".join(f"{s}px" for s in SIZES))
    for name in ("app.ico", "app-256.png", "icon-preview.png"):
        print(f"  {name}: {(OUT / name).stat().st_size} bytes")


if __name__ == "__main__":
    main()
