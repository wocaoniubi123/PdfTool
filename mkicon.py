import pymupdf, struct, io

RED = (0.80, 0.09, 0.09)
SIZES = [16, 24, 32, 48, 64, 128, 256]

def make_png(size):
    d = pymupdf.open()
    page = d.new_page(width=size, height=size)
    fontsize = size * 0.44
    tw = pymupdf.get_text_length("PDF", fontname="hebo", fontsize=fontsize)
    if tw > size * 0.80:            # 保证左右留白
        fontsize *= size * 0.80 / tw
        tw = pymupdf.get_text_length("PDF", fontname="hebo", fontsize=fontsize)
    x = (size - tw) / 2.0
    y = size / 2.0 + fontsize * 0.36
    # 白底圆角卡片（16px 下也看得清）
    page.draw_rect(pymupdf.Rect(size*0.03, size*0.03, size*0.97, size*0.97),
                   color=RED, fill=(1, 1, 1), width=max(0.8, size*0.04))
    page.insert_text((x, y), "PDF", fontname="hebo", fontsize=fontsize, color=RED)
    return page.get_pixmap(alpha=True).tobytes("png")

imgs = [(s, make_png(s)) for s in SIZES]
entries, data = b"", b""
off = 6 + 16 * len(imgs)
for s, png in imgs:
    b = 0 if s >= 256 else s
    entries += struct.pack("<BBBBHHII", b, b, 0, 0, 1, 32, len(png), off)
    off += len(png)
    data += png
ico = struct.pack("<HHH", 0, 1, len(imgs)) + entries + data
open(r"G:\ZCode\PdfTool\app.ico", "wb").write(ico)
print("app.ico 生成: %d 字节, %d 个尺寸" % (len(ico), len(imgs)))
