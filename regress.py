"""PDF 工具全功能复测：造各类测试文件 -> 跑命令行 -> 外部验证 -> 汇总"""
import os, io, zlib, base64, struct, subprocess, glob, shutil
import pymupdf
from PIL import Image

ROOT = r'G:\ZCode\PdfTool'
EXE = os.path.join(ROOT, 'PdfTool.exe')
T = os.path.join(ROOT, '_regress')
shutil.rmtree(T, ignore_errors=True)
os.makedirs(T, exist_ok=True)
results = []


def run(args):
    p = subprocess.run([EXE] + args, cwd=ROOT, capture_output=True)
    return p.stdout.decode('gbk', errors='replace') + p.stderr.decode('gbk', errors='replace')


def log(name, ok, detail=''):
    results.append((name, ok, detail))
    print('%-28s %s  %s' % (name, 'OK ' if ok else 'FAIL', detail))


# ---------- 造各类测试 PDF ----------
def build_pdf(path, img_dict, img_data, w=100, h=50):
    objs = [b'<< /Type /Catalog /Pages 2 0 R >>',
            b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
            ('<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>' % (w, h)).encode(),
            img_dict.encode() + b' /Length ' + str(len(img_data)).encode() + b' >>\nstream\n' + img_data + b'\nendstream']
    content = ('q %d 0 0 %d 0 0 cm /Im0 Do Q' % (w, h)).encode()
    objs.append(b'<< /Length ' + str(len(content)).encode() + b' >>\nstream\n' + content + b'\nendstream')
    out = io.BytesIO(); out.write(b'%PDF-1.4\n'); offs = []
    for i, o in enumerate(objs):
        offs.append(out.tell()); out.write(str(i+1).encode() + b' 0 obj\n' + o + b'\nendobj\n')
    x = out.tell()
    out.write(b'xref\n0 ' + str(len(objs)+1).encode() + b'\n0000000000 65535 f \n')
    for o in offs: out.write(('%010d 00000 n \n' % o).encode())
    out.write(b'trailer\n<< /Size ' + str(len(objs)+1).encode() + b' /Root 1 0 R >>\nstartxref\n' + str(x).encode() + b'\n%%EOF\n')
    open(path, 'wb').write(out.getvalue())


W, H = 64, 32
rgb = bytearray()
for y in range(H):
    for x in range(W):
        rgb += bytes([(x*4) % 256, (y*8) % 256, 128])
rgb = bytes(rgb)


def expect(x, y):
    return bytes([(x*4) % 256, (y*8) % 256, 128])


# 1) ASCII85 + Flate
fl = zlib.compress(rgb)
build_pdf(os.path.join(T, 't1_ascii85.pdf'),
          '<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter [/ASCII85Decode /FlateDecode]' % (W, H),
          b'<~' + base64.a85encode(fl) + b'~>')

# 2) CCITT G4（全白）
bits = '1' * H; bits += '0' * ((-len(bits)) % 8)
g4 = bytes(int(bits[i:i+8], 2) for i in range(0, len(bits), 8))
build_pdf(os.path.join(T, 't2_ccitt.pdf'),
          '<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns %d /Rows %d >>' % (W, H, W, H),
          g4)

# 3) JBIG2
build_pdf(os.path.join(T, 't3_jbig2.pdf'),
          '<< /Type /XObject /Subtype /Image /Width 32 /Height 32 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode',
          bytes(range(64)))

# 4) RunLength
rle = bytearray()
for i in range(0, len(rgb), 128):
    ch = rgb[i:i+128]; rle.append(len(ch)-1); rle.extend(ch)
rle.append(128)
build_pdf(os.path.join(T, 't4_rle.pdf'),
          '<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /RunLengthDecode' % (W, H),
          bytes(rle))

# 5) 真实 LZW（Pillow 生成）
im = Image.new('RGB', (W, H)); px = im.load()
for y in range(H):
    for x in range(W):
        px[x, y] = ((x*4) % 256, (y*8) % 256, 128)
im.save(os.path.join(T, '_lzw.tif'), compression='tiff_lzw')
tb = open(os.path.join(T, '_lzw.tif'), 'rb').read()
off = struct.unpack('<I', tb[4:8])[0]
cnt = struct.unpack('<H', tb[off:off+2])[0]
tags = {}
for i in range(cnt):
    tag, typ, n, val = struct.unpack('<HHII', tb[off+2+i*12:off+2+(i+1)*12])
    tags[tag] = val
lzw = tb[tags[273]:tags[273]+tags[279]]
build_pdf(os.path.join(T, 't5_lzw.pdf'),
          '<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /LZWDecode' % (W, H),
          lzw)

# 6) SMask 透明
mask = bytearray()
for y in range(H):
    for x in range(W):
        mask.append(255 if ((x-W/2)**2 + (y-H/2)**2) < (W/3)**2 else 0)
objs = [b'<< /Type /Catalog /Pages 2 0 R >>',
        b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
        ('<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 6 0 R >>' % (W, H)).encode(),
        ('<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /SMask 5 0 R /Length %d >>' % (W, H, len(zlib.compress(rgb)))).encode() + b'\nstream\n' + zlib.compress(rgb) + b'\nendstream',
        ('<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length %d >>' % (W, H, len(zlib.compress(bytes(mask))))).encode() + b'\nstream\n' + zlib.compress(bytes(mask)) + b'\nendstream',
        b'<< /Length 25 >>\nstream\nq 64 0 0 32 0 0 cm /Im0 Do Q\nendstream']
o = io.BytesIO(); o.write(b'%PDF-1.4\n'); offs = []
for i, x in enumerate(objs):
    offs.append(o.tell()); o.write(str(i+1).encode()+b' 0 obj\n'+x+b'\nendobj\n')
xx = o.tell(); o.write(b'xref\n0 '+str(len(objs)+1).encode()+b'\n0000000000 65535 f \n')
for f in offs: o.write(('%010d 00000 n \n' % f).encode())
o.write(b'trailer\n<< /Size '+str(len(objs)+1).encode()+b' /Root 1 0 R >>\nstartxref\n'+str(xx).encode()+b'\n%%EOF\n')
open(os.path.join(T, 't6_smask.pdf'), 'wb').write(o.getvalue())

# 7) 对象流结构（用 PyMuPDF 生成）
src = r'C:\Users\Administrator\Desktop\25秋53天天练一上册.pdf'
if os.path.exists(src):
    d = pymupdf.open(src)
    d.save(os.path.join(T, 't7_objstm.pdf'), use_objstms=1, deflate=True, garbage=3)

# ---------- 跑提取并验证 ----------
def extract(pdf, page=1, tag=''):
    out = os.path.join(T, 'out_' + tag)
    os.makedirs(out, exist_ok=True)
    return run(['--extract', pdf, str(page), out]), out


print('===== 提取图片（各滤镜）=====')
for tag, pdf, expect_pixels in (('t1_ascii85', 't1_ascii85.pdf', True), ('t4_rle', 't4_rle.pdf', True),
                                ('t5_lzw', 't5_lzw.pdf', True)):
    txt, out = extract(os.path.join(T, pdf), 1, tag)
    fs = [f for f in glob.glob(os.path.join(out, '*')) if f.lower().endswith('.png')]
    ok = False
    if fs:
        pix = pymupdf.Pixmap(fs[0]); n = pix.n
        ok = all(pix.samples[(y*W+x)*n:(y*W+x)*n+3] == expect(x, y) for x in (0, 1, 30) for y in (0, 1, 20))
    log(tag + ' 像素正确', ok, os.path.basename(fs[0]) if fs else '无输出')

# CCITT -> TIFF
txt, out = extract(os.path.join(T, 't2_ccitt.pdf'), 1, 't2')
fs = glob.glob(os.path.join(out, '*.tif'))
ok = False
if fs:
    px2 = pymupdf.open(fs[0])[0].get_pixmap()
    ok = set(px2.samples[0::px2.n]) == {255}
log('t2_ccitt -> TIFF 全白', ok, os.path.basename(fs[0]) if fs else '无输出')

# JBIG2 原样
txt, out = extract(os.path.join(T, 't3_jbig2.pdf'), 1, 't3')
fs = glob.glob(os.path.join(out, '*.jb2'))
ok = bool(fs) and open(fs[0], 'rb').read() == bytes(range(64))
log('t3_jbig2 字节一致', ok, os.path.basename(fs[0]) if fs else '无输出')

# SMask -> 带 alpha PNG
txt, out = extract(os.path.join(T, 't6_smask.pdf'), 1, 't6')
fs = glob.glob(os.path.join(out, '*.png'))
ok = False; det = ''
if fs:
    pix = pymupdf.Pixmap(fs[0]); n = pix.n
    if n == 4:
        a_c = pix.samples[(H//2*W + W//2)*n + 3]
        a_k = pix.samples[(1*W + 1)*n + 3]
        ok = (a_c == 255 and a_k == 0)
        det = '中心alpha=%d 角落alpha=%d' % (a_c, a_k)
log('t6_smask 透明保留', ok, det or '无输出')

# 对象流
if os.path.exists(os.path.join(T, 't7_objstm.pdf')):
    txt, out = extract(os.path.join(T, 't7_objstm.pdf'), 1, 't7')
    fs = [f for f in glob.glob(os.path.join(out, '*'))]
    ok = bool(fs)
    det = os.path.basename(fs[0]) if fs else '无输出'
    if fs and fs[0].lower().endswith(('.jpg', '.jp2', '.j2k')):
        b = open(fs[0], 'rb').read()
        ok = b in open(os.path.join(T, 't7_objstm.pdf'), 'rb').read()
        det += '（与源文件逐字节一致=%s）' % ok
    log('t7_objstm 对象流提取', ok, det)

# 主文件：JPEG/JP2 逐字节
if os.path.exists(src):
    shutil.copy(src, os.path.join(T, 'main.pdf'))
    txt, out = extract(os.path.join(T, 'main.pdf'), 1, 'main')
    fs = glob.glob(os.path.join(out, '*'))
    doc = pymupdf.open(os.path.join(T, 'main.pdf'))
    refs = [doc.xref_stream_raw(x[0]) for x in doc[0].get_images(full=True)]
    ok = bool(fs) and any(open(f, 'rb').read() in refs for f in fs)
    log('主文件第1页 原样导出', ok, '%d 个文件' % len(fs))

print()
print('===== 保存 / 拆分 =====')
if os.path.exists(src):
    txt = run(['--selftest', os.path.join(T, 'main.pdf')])
    log('自检（增删页+插图）', '自检通过' in txt, txt.strip().splitlines()[-1][:60] if txt.strip() else '')
    txt = run(['--split', os.path.join(T, 'main.pdf')])
    log('拆分奇偶页', '校验通过' in txt, '')
    odd = os.path.join(T, 'main_奇数页.pdf')
    if os.path.exists(odd):
        s = pymupdf.open(os.path.join(T, 'main.pdf')); o = pymupdf.open(odd)
        ok = bad = 0
        for k in range(4):
            sh = [s.xref_stream_raw(x[0]) for x in s[2*k].get_images(full=True)]
            dh = [o.xref_stream_raw(x[0]) for x in o[k].get_images(full=True)]
            for h in sh:
                ok += 1 if h in dh else 0
                bad += 0 if h in dh else 1
        log('拆分后图片流逐字节', bad == 0 and ok > 0, '%d 张一致 / %d 张不同' % (ok, bad))

print()
print('===== 汇总 =====')
fails = [r for r in results if not r[1]]
print('通过 %d / %d' % (len(results) - len(fails), len(results)))
for r in fails:
    print('  失败:', r[0], r[2])
