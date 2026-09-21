using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PdfTool
{
    // 一页的来源：原 PDF 的某一页（SrcIndex>=0），或插入的一张图片（Image!=null）
    internal sealed class PageRef
    {
        public int SrcIndex = -1;
        public byte[] Image;
        public string ImgExt;   // 原图片文件扩展名（提取原图时用）
        public bool Jpeg;
        public int PxW, PxH;
        public float Width, Height; // 图片页的页面尺寸（点）
        public byte[] Pixels;      // 图片页的 RGB 像素（仅当原图带透明通道时保留）
        public byte[] Alpha;       // 图片页的透明度通道（0-255）
        public byte[] PngIdat;     // PNG 原样嵌入用的扫描线数据
        public int PngComps;       // 1=Gray 3=RGB
        public int PngBpc;         // 每通道位数（1/2/4/8/16）
        public byte[] PngIcc;      // 内嵌 ICC 色彩描述

        public bool IsImage { get { return Image != null; } }

        public PageRef Clone()
        {
            PageRef c = new PageRef();
            c.SrcIndex = SrcIndex;
            c.Image = Image;
            c.ImgExt = ImgExt;
            c.Jpeg = Jpeg;
            c.PxW = PxW;
            c.PxH = PxH;
            c.Width = Width;
            c.Height = Height;
            c.Pixels = Pixels;
            c.Alpha = Alpha;
            c.PngIdat = PngIdat;
            c.PngComps = PngComps;
            c.PngBpc = PngBpc;
            c.PngIcc = PngIcc;
            return c;
        }
    }

    internal sealed class PdfJob : IDisposable
    {
        private byte[] _bytes;
        private GCHandle _pin;
        private IntPtr _doc;
        private PageRef _cacheRef;
        private RawPdf _rawPdf;
        private bool _rawTried;
        private Bitmap _cacheBmp;

        public string SourcePath { get; private set; }
        public List<PageRef> Pages { get; private set; }
        private string _outDir;    // 新建（无源文件）时的输出目录/文件名
        private string _outName;

        private PdfJob() { }

        public int PageCount { get { return Pages.Count; } }

        public string Dir
        {
            get { return SourcePath == null ? _outDir : Path.GetDirectoryName(SourcePath); }
        }
        public string BaseName
        {
            get { return SourcePath == null ? _outName : Path.GetFileNameWithoutExtension(SourcePath); }
        }
        public string OddPath { get { return Path.Combine(Dir, BaseName + "_奇数页.pdf"); } }
        public string EvenPath { get { return Path.Combine(Dir, BaseName + "_偶数页.pdf"); } }
        public string EditedPath
        {
            get
            {
                // 新建的空白 PDF 直接存成 <名字>.pdf；打开的 PDF 存成 <名字>_已修改.pdf
                string name = SourcePath == null ? BaseName + ".pdf" : BaseName + "_已修改.pdf";
                return Path.Combine(Dir, name);
            }
        }

        // 新建空白 PDF（没有源文件），输出目录/文件名由调用方指定
        public static PdfJob CreateNew(string outDir, string outName)
        {
            PdfJob j = new PdfJob();
            j.SourcePath = null;
            j._outDir = outDir;
            j._outName = outName;
            j.Pages = new List<PageRef>();
            return j;
        }

        public static PdfJob Load(string path)
        {
            if (!File.Exists(path)) throw new Exception("找不到文件：" + path);
            PdfJob j = new PdfJob();
            j.SourcePath = Path.GetFullPath(path);
            j._bytes = File.ReadAllBytes(path);
            j._pin = GCHandle.Alloc(j._bytes, GCHandleType.Pinned);
            j._doc = Pdfium.FPDF_LoadMemDocument(j._pin.AddrOfPinnedObject(), j._bytes.Length, null);
            if (j._doc == IntPtr.Zero)
            {
                uint err = Pdfium.FPDF_GetLastError();
                j._pin.Free();
                throw new Exception("打不开这个 PDF（错误码 " + err + "），可能已加密或文件损坏。");
            }
            int n = Pdfium.FPDF_GetPageCount(j._doc);
            if (n <= 0) throw new Exception("这个 PDF 没有任何页面。");
            j.Pages = new List<PageRef>();
            for (int i = 0; i < n; i++)
            {
                PageRef p = new PageRef();
                p.SrcIndex = i;
                j.Pages.Add(p);
            }
            return j;
        }

        // 把一张图片做成"图片页"：页面尺寸照当前页（点），内容居中按比例缩放
        public static PageRef MakeImagePage(string file, SizeF target)
        {
            byte[] bytes = File.ReadAllBytes(file);
            int w, h;
            byte[] pix = null;
            byte[] alpha = null;
            using (MemoryStream ms = new MemoryStream(bytes))
            using (Image img = Image.FromStream(ms))
            {
                w = img.Width;
                h = img.Height;
                if (Image.IsAlphaPixelFormat(img.PixelFormat))
                {
                    // 原图带透明通道：把 RGB 和 alpha 分别存下来，插入时用 SMask 保留透明
                    using (Bitmap b32 = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(b32))
                        {
                            g.Clear(Color.Transparent);
                            g.DrawImage(img, new Rectangle(0, 0, w, h));
                        }
                        byte[] rgb = new byte[w * h * 3];
                        byte[] al = new byte[w * h];
                        bool anyTransparent = false;
                        BitmapData bd = b32.LockBits(new Rectangle(0, 0, w, h),
                            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        try
                        {
                            byte[] row = new byte[bd.Stride];
                            for (int y = 0; y < h; y++)
                            {
                                Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, bd.Stride);
                                for (int x = 0; x < w; x++)
                                {
                                    int so = x * 4, o = (y * w + x);
                                    rgb[o * 3] = row[so + 2];
                                    rgb[o * 3 + 1] = row[so + 1];
                                    rgb[o * 3 + 2] = row[so];
                                    al[o] = row[so + 3];
                                    if (row[so + 3] != 255) anyTransparent = true;
                                }
                            }
                        }
                        finally
                        {
                            b32.UnlockBits(bd);
                        }
                        if (anyTransparent) { pix = rgb; alpha = al; }
                    }
                }
            }
            if (w <= 0 || h <= 0) throw new Exception("图片尺寸读不出来。");
            // PNG 能原样嵌入时，用它自己解析出的参数覆盖（16 位/ICC 不被 GDI+ 降级）
            PngInfo pi = PngInfo.Parse(bytes);
            if (pi != null && pi.Lossless)
            {
                w = pi.Width;
                h = pi.Height;
            }
            string ext = Path.GetExtension(file).ToLowerInvariant();
            PageRef p = new PageRef();
            p.SrcIndex = -1;
            p.Image = bytes;
            p.ImgExt = ext;
            p.Jpeg = (ext == ".jpg" || ext == ".jpeg");
            p.PxW = w;
            p.PxH = h;
            p.Pixels = pix;
            p.Alpha = alpha;
            if (pi != null && pi.Lossless)
            {
                p.PngIdat = pi.Idat;
                p.PngComps = pi.Comps;
                p.PngBpc = pi.Bpc;
                p.PngIcc = pi.Icc;
            }
            p.Width = target.Width > 1f ? target.Width : 595f;
            p.Height = target.Height > 1f ? target.Height : 842f;
            return p;
        }

        public SizeF GetPageSizePoints(PageRef p)
        {
            if (p.IsImage) return new SizeF(p.Width, p.Height);
            IntPtr page = Pdfium.FPDF_LoadPage(_doc, p.SrcIndex);
            if (page == IntPtr.Zero) return new SizeF(595f, 842f); // A4 兜底
            SizeF s = new SizeF(Pdfium.FPDF_GetPageWidthF(page), Pdfium.FPDF_GetPageHeightF(page));
            Pdfium.FPDF_ClosePage(page);
            return s;
        }

        // 把第 index 页渲成不超过 maxWidth x maxHeight 的位图
        // 页面物理尺寸（单位 1/100 英寸）：插入的图片页看 PageRef，PDF 页问 pdfium。
        // 注意 PageRef.Width/Height 对"从 PDF 打开的页"是 0，别再直接用它们做排版。
        public void PageSizeIn100(int index, out double w, out double h)
        {
            PageRef p = Pages[index];
            if (p.IsImage && p.Width > 0 && p.Height > 0)
            {
                w = p.Width * 100.0 / 72.0;    // 这里存的是 PDF 点，换算成 1/100 英寸
                h = p.Height * 100.0 / 72.0;
                return;
            }
            IntPtr page = Pdfium.FPDF_LoadPage(_doc, p.SrcIndex);
            if (page == IntPtr.Zero) { w = 595; h = 842; return; }   // 兜底 A4
            try
            {
                w = Pdfium.FPDF_GetPageWidthF(page) * 100.0 / 72.0;
                h = Pdfium.FPDF_GetPageHeightF(page) * 100.0 / 72.0;
                if (w < 1 || h < 1) { w = 595; h = 842; }
            }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
        public Bitmap RenderPage(int index, int maxWidth, int maxHeight)
        {
            return RenderPage(index, maxWidth, maxHeight, false);
        }

        // fast=true：翻页过程中用（跳过超采样，渲染快 3~4 倍，停下后调用方再补高清）
        public Bitmap RenderPage(int index, int maxWidth, int maxHeight, bool fast)
        {
            if (index < 0 || index >= Pages.Count) throw new Exception("没有这一页。");
            PageRef pref = Pages[index];
            if (pref.IsImage) return RenderImagePage(pref, maxWidth, maxHeight, fast);

            IntPtr page = Pdfium.FPDF_LoadPage(_doc, pref.SrcIndex);
            if (page == IntPtr.Zero) throw new Exception("读取第 " + (index + 1) + " 页失败");
            try
            {
                float pw = Pdfium.FPDF_GetPageWidthF(page);
                float ph = Pdfium.FPDF_GetPageHeightF(page);
                return Render(page, pw, ph, maxWidth, maxHeight, null, fast);
            }
            finally
            {
                Pdfium.FPDF_ClosePage(page);
            }
        }

        private Bitmap RenderImagePage(PageRef pref, int maxWidth, int maxHeight, bool fast)
        {
            return Render(IntPtr.Zero, pref.Width, pref.Height, maxWidth, maxHeight, pref, fast);
        }

        private Bitmap Render(IntPtr page, float pw, float ph, int maxWidth, int maxHeight, PageRef imgRef, bool fast)
        {
            if (pw < 1f) pw = 1f;
            if (ph < 1f) ph = 1f;
            // 目标显示尺寸：按窗口可视区等比装下
            double scale = Math.Min(maxWidth / (double)pw, maxHeight / (double)ph);
            if (scale <= 0) scale = 1.0;
            if (scale > 8.0) scale = 8.0;
            int dw = Math.Max(1, (int)Math.Round(pw * scale));
            int dh = Math.Max(1, (int)Math.Round(ph * scale));

            if (imgRef != null)
            {
                // 图片页：直接按目标尺寸高质量缩放一次（没有二次损失）
                Bitmap only = new Bitmap(dw, dh, PixelFormat.Format32bppRgb);
                try
                {
                    using (Graphics g = Graphics.FromImage(only))
                    {
                        g.Clear(Color.White);
                        Image src = DecodedImage(imgRef); // 缓存持有，不能在这里释放
                        double s = Math.Min(dw / (double)imgRef.PxW, dh / (double)imgRef.PxH);
                        int iw = Math.Max(1, (int)Math.Round(imgRef.PxW * s));
                        int ih = Math.Max(1, (int)Math.Round(imgRef.PxH * s));
                        // 快速滚动用双线性（快得多）；停手后 _idleTimer 会补一张高质量
                        g.InterpolationMode = fast ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = fast ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
                        g.DrawImage(src, (dw - iw) / 2, (dh - ih) / 2, iw, ih);
                        using (Pen pen = new Pen(Color.FromArgb(190, 190, 190)))
                        {
                            g.DrawRectangle(pen, 0, 0, dw - 1, dh - 1);
                        }
                    }
                }
                catch
                {
                    only.Dispose();
                    throw;
                }
                return only;
            }

            // PDF 页：先按整数倍超采样渲染，再面积平均缩回目标尺寸
            // （面积平均比 GDI+ 的双三次锐利，和看图软件的重采样一致）
            int ss = fast ? 1 : 2;
            long limit = 12L * 1000 * 1000; // 超采样后像素上限，避免吃爆内存
            while (ss > 1 && (long)dw * ss * dh * ss > limit) ss--;

            Bitmap big = RenderRaw(page, dw * ss, dh * ss);
            if (ss == 1) return big;

            Bitmap shrunk = DownscaleBox(big, ss, dw, dh);
            big.Dispose();
            return shrunk;
        }

        // 整数倍面积平均缩放（ss 倍 -> 1 倍），每块 ss*ss 个像素求平均
        private static Bitmap DownscaleBox(Bitmap src, int ss, int dw, int dh)
        {
            Bitmap dst = new Bitmap(dw, dh, PixelFormat.Format32bppRgb);
            BitmapData sd = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                BitmapData dd = dst.LockBits(new Rectangle(0, 0, dw, dh),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    int nn = ss * ss;
                    byte[] inRows = new byte[sd.Stride * ss];
                    byte[] outRow = new byte[dd.Stride];
                    for (int y = 0; y < dh; y++)
                    {
                        for (int j = 0; j < ss; j++)
                        {
                            Marshal.Copy(IntPtr.Add(sd.Scan0, (y * ss + j) * sd.Stride),
                                inRows, j * sd.Stride, sd.Stride);
                        }
                        for (int x = 0; x < dw; x++)
                        {
                            int b = 0, g = 0, r = 0;
                            for (int j = 0; j < ss; j++)
                            {
                                int rowOff = j * sd.Stride + x * ss * 4;
                                for (int i = 0; i < ss; i++)
                                {
                                    int o = rowOff + i * 4;
                                    b += inRows[o];
                                    g += inRows[o + 1];
                                    r += inRows[o + 2];
                                }
                            }
                            int p = x * 4;
                            outRow[p] = (byte)(b / nn);
                            outRow[p + 1] = (byte)(g / nn);
                            outRow[p + 2] = (byte)(r / nn);
                            outRow[p + 3] = 255;
                        }
                        Marshal.Copy(outRow, 0, IntPtr.Add(dd.Scan0, y * dd.Stride), dw * 4);
                    }
                }
                finally
                {
                    dst.UnlockBits(dd);
                }
            }
            catch
            {
                dst.Dispose();
                throw;
            }
            finally
            {
                src.UnlockBits(sd);
            }
            return dst;
        }

        // 用 PDFium 把页面渲成 w x h 的位图
        private Bitmap RenderRaw(IntPtr page, int w, int h)
        {
            Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            IntPtr bmp = IntPtr.Zero;
            try
            {
                bmp = Pdfium.FPDFBitmap_CreateEx(w, h, Pdfium.BitmapBgra, IntPtr.Zero, 0);
                if (bmp == IntPtr.Zero) throw new Exception("创建渲染缓冲失败");
                Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
                Pdfium.FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, 0, Pdfium.FlagAnnot | Pdfium.FlagLcdText);
                // 逐行把 pdfium 缓冲拷进 GDI+ 位图（不让 GDI+ 引用外部内存）
                BitmapData data = result.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    IntPtr src = Pdfium.FPDFBitmap_GetBuffer(bmp);
                    int stride = Pdfium.FPDFBitmap_GetStride(bmp);
                    byte[] row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(IntPtr.Add(src, y * stride), row, 0, w * 4);
                        Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), w * 4);
                    }
                }
                finally
                {
                    result.UnlockBits(data);
                }
            }
            catch
            {
                result.Dispose();
                throw;
            }
            finally
            {
                if (bmp != IntPtr.Zero) Pdfium.FPDFBitmap_Destroy(bmp);
            }
            return result;
        }

        // 图片解码只做一次，翻页/缩放时不重复解码
        private Bitmap DecodedImage(PageRef p)
        {
            if (_cacheRef == p && _cacheBmp != null) return _cacheBmp;
            if (_cacheBmp != null)
            {
                _cacheBmp.Dispose();
                _cacheBmp = null;
                _cacheRef = null;
            }
            using (MemoryStream ms = new MemoryStream(p.Image))
            using (Image src = Image.FromStream(ms))
            {
                _cacheBmp = new Bitmap(src);
                _cacheRef = p;
                return _cacheBmp;
            }
        }

        // 把第 index 页里的图片提取到 outDir；返回提取张数，文件名写进 saved
        public int ExtractPageImages(int index, string outDir, string prefix, List<string> saved)
        {
            if (index < 0 || index >= Pages.Count) throw new Exception("没有这一页。");
            PageRef pref = Pages[index];

            if (pref.IsImage) // 我们自己插入的图片页：原样导出原图
            {
                string ext = string.IsNullOrEmpty(pref.ImgExt) ? (pref.Jpeg ? ".jpg" : ".png") : pref.ImgExt;
                string path = Util.AutoName(Path.Combine(outDir, prefix + "_原图" + ext));
                File.WriteAllBytes(path, pref.Image);
                saved.Add(Path.GetFileName(path));
                return 1;
            }

            // 优先：自己解析 PDF，按原始字节导出（无损）
            try
            {
                if (!_rawTried && SourcePath != null)
                {
                    _rawTried = true;
                    _rawPdf = RawPdf.Open(SourcePath);
                }
                if (_rawPdf != null)
                {
                    List<RawImage> list = _rawPdf.PageImages(index);
                    if (list.Count > 0)
                    {
                        List<string> tmp = new List<string>();
                        bool all = true;
                        for (int k = 0; k < list.Count; k++)
                        {
                            if (!RawExport.Save(list[k], Path.Combine(outDir, prefix + "_图" + (k + 1)), tmp))
                            {
                                all = false;
                                break;
                            }
                        }
                        if (all)
                        {
                            saved.AddRange(tmp);
                            return list.Count;
                        }
                        foreach (string f in tmp) // 有导不出的就整页退回，避免半成品
                        {
                            try { File.Delete(Path.Combine(outDir, f)); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
            }

            // 优先：自己解析 PDF，按原始字节导出（无损）
            try
            {
                if (!_rawTried && SourcePath != null)
                {
                    _rawTried = true;
                    _rawPdf = RawPdf.Open(SourcePath);
                }
                if (_rawPdf != null)
                {
                    List<RawImage> list = _rawPdf.PageImages(index);
                    if (list.Count > 0)
                    {
                        List<string> tmp = new List<string>();
                        bool all = true;
                        for (int k = 0; k < list.Count; k++)
                        {
                            if (!RawExport.Save(list[k], Path.Combine(outDir, prefix + "_图" + (k + 1)), tmp))
                            {
                                all = false;
                                break;
                            }
                        }
                        if (all)
                        {
                            saved.AddRange(tmp);
                            return list.Count;
                        }
                        foreach (string f in tmp) // 有导不出的就整页退回，避免半成品
                        {
                            try { File.Delete(Path.Combine(outDir, f)); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
            }

            IntPtr page = Pdfium.FPDF_LoadPage(_doc, pref.SrcIndex);
            if (page == IntPtr.Zero) throw new Exception("读取第 " + (index + 1) + " 页失败");
            try
            {
                int n = Pdfium.FPDFPage_CountObjects(page);
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    IntPtr obj = Pdfium.FPDFPage_GetObject(page, i);
                    if (obj == IntPtr.Zero) continue;
                    if (Pdfium.FPDFPageObj_GetType(obj) != Pdfium.PageObjImage) continue;
                    string baseName = Path.Combine(outDir, prefix + "_图" + (count + 1));
                    if (SaveImageObject(page, obj, baseName, saved)) count++;
                }
                return count;
            }
            finally
            {
                Pdfium.FPDF_ClosePage(page);
            }
        }

        // 导出单个图片对象：
        // 1) 先试"原始字节"导出（取 PDF 里存的原样数据），但必须文件头合法才落盘
        // 2) 否则按解码像素全分辨率存 PNG
        private static bool SaveImageObject(IntPtr page, IntPtr obj, string pathBase, List<string> saved)
        {
            if (SaveRawIfValid(obj, pathBase, saved)) return true;
            if (SaveDecodedImage(obj, pathBase, saved)) return true;

            IntPtr bmp = Pdfium.FPDFImageObj_GetBitmap(obj);
            if (bmp == IntPtr.Zero) return false;
            try
            {
                int w = Pdfium.FPDFBitmap_GetWidth(bmp);
                int h = Pdfium.FPDFBitmap_GetHeight(bmp);
                if (w <= 0 || h <= 0) return false;
                using (Bitmap img = BitmapFromPdfium(bmp, w, h))
                {
                    string p = Util.AutoName(pathBase + ".png");
                    img.Save(p, ImageFormat.Png);
                    saved.Add(Path.GetFileName(p));
                }
                return true;
            }
            finally
            {
                Pdfium.FPDFBitmap_Destroy(bmp);
            }
        }

        // 原样导出 PDF 里存的图片字节：只有文件头认得出来才算成功（防止坏数据落盘）
        private static bool SaveRawIfValid(IntPtr obj, string pathBase, List<string> saved)
        {
            string wantExt = RawImageExt(obj);
            if (wantExt == null) return false;

            long guess = 64L * 1024 * 1024;
            Pdfium.FPDF_IMAGEOBJ_METADATA meta = new Pdfium.FPDF_IMAGEOBJ_METADATA();
            if (Pdfium.FPDFImageObj_GetImageMetadata(obj, IntPtr.Zero, ref meta) != 0
                && meta.width > 0 && meta.height > 0)
            {
                long est = (long)meta.width * meta.height * 4 + 65536;
                if (est < guess) guess = est;
            }
            IntPtr buf = Marshal.AllocHGlobal((int)guess);
            try
            {
                uint got = Pdfium.FPDFImageObj_GetImageDataRaw(obj, buf, (uint)guess);
                if (got < 8 || got > guess) return false;
                byte[] data = new byte[got];
                Marshal.Copy(buf, data, 0, (int)got);
                string ext = MagicExt(data, wantExt);
                if (ext == null) return false;   // 头部不认，交给后面的解码路径
                string p = Util.AutoName(pathBase + ext);
                File.WriteAllBytes(p, data);
                saved.Add(Path.GetFileName(p));
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // 按文件头判断真实格式；only=期望的扩展名（滤镜说的），匹配才认
        private static string MagicExt(byte[] d, string only)
        {
            string ext = null;
            if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) ext = ".jpg";
            else if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) ext = ".png";
            else if (d[0] == 0x00 && d[1] == 0x00 && d[2] == 0x00 && d[3] == 0x0C
                     && d[4] == 0x6A && d[5] == 0x50) ext = ".jp2";
            else if (d[0] == 0xFF && d[1] == 0x4F && d[2] == 0xFF && d[3] == 0x51) ext = ".j2k";
            else if (d[0] == 0x42 && d[1] == 0x4D) ext = ".bmp";
            else if (d[0] == 0x49 && d[1] == 0x49 && d[2] == 0x2A) ext = ".tif";
            else if (d[0] == 0x4D && d[1] == 0x4D && d[2] == 0x00) ext = ".tif";
            else if (d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) ext = ".gif";
            if (ext == null) return null;
            if (only != null && ext != only)
            {
                // 允许等价替换：jp2/j2k 互认
                if (!((only == ".jp2" && ext == ".j2k") || (only == ".j2k" && ext == ".jp2"))) return null;
            }
            return ext;
        }

        // 图片在 PDF 里用的滤镜（决定能不能原样保存）；多重滤镜不冒险
        private static string RawImageExt(IntPtr obj)
        {
            if (Pdfium.FPDFImageObj_GetImageFilterCount(obj) != 1) return null;
            IntPtr buf = Marshal.AllocHGlobal(64);
            try
            {
                uint got = Pdfium.FPDFImageObj_GetImageFilter(obj, 0, buf, 64);
                if (got == 0) return null;
                string f = Marshal.PtrToStringAnsi(buf);
                if (string.IsNullOrEmpty(f)) return null;
                if (f.IndexOf("DCTDecode") >= 0) return ".jpg";
                if (f.IndexOf("JPXDecode") >= 0) return ".jp2";
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // 取"解码后的像素"（灰度/RGB/CMYK）存成 PNG
        private static bool SaveDecodedImage(IntPtr obj, string pathBase, List<string> saved)
        {
            Pdfium.FPDF_IMAGEOBJ_METADATA meta = new Pdfium.FPDF_IMAGEOBJ_METADATA();
            if (Pdfium.FPDFImageObj_GetImageMetadata(obj, IntPtr.Zero, ref meta) == 0) return false;
            if (meta.width == 0 || meta.height == 0) return false;
            int ch = meta.colorspace == Pdfium.CsGray ? 1
                : (meta.colorspace == Pdfium.CsRgb ? 3
                : (meta.colorspace == Pdfium.CsCmyk ? 4 : 0));
            if (ch == 0) return false;
            long need = (long)meta.width * meta.height * ch;
            if (need <= 0 || need > 256L * 1024 * 1024) return false;

            IntPtr buf = Marshal.AllocHGlobal((int)need);
            try
            {
                uint got = Pdfium.FPDFImageObj_GetImageDataDecoded(obj, buf, (uint)need);
                if (got == 0 || got > need) return false;
                using (Bitmap b = FromRawPixels(buf, (int)meta.width, (int)meta.height, ch))
                {
                    string p = Util.AutoName(pathBase + ".png");
                    b.Save(p, ImageFormat.Png);
                    saved.Add(Path.GetFileName(p));
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // 原始像素（灰度/RGB/CMYK）-> GDI+ 位图
        private static Bitmap FromRawPixels(IntPtr buf, int w, int h, int ch)
        {
            Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            BitmapData d = result.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                byte[] inRow = new byte[w * ch];
                byte[] outRow = new byte[d.Stride];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(buf, y * w * ch), inRow, 0, w * ch);
                    for (int x = 0; x < w; x++)
                    {
                        int si = x * ch, di = x * 4;
                        if (ch == 1)
                        {
                            outRow[di] = inRow[si];
                            outRow[di + 1] = inRow[si];
                            outRow[di + 2] = inRow[si];
                        }
                        else if (ch == 3)
                        {
                            outRow[di] = inRow[si + 2];
                            outRow[di + 1] = inRow[si + 1];
                            outRow[di + 2] = inRow[si];
                        }
                        else
                        {
                            int c = inRow[si], m = inRow[si + 1], y2 = inRow[si + 2], k = inRow[si + 3];
                            outRow[di] = (byte)(255 - Math.Min(255, y2 + k));
                            outRow[di + 1] = (byte)(255 - Math.Min(255, m + k));
                            outRow[di + 2] = (byte)(255 - Math.Min(255, c + k));
                        }
                        outRow[di + 3] = 255;
                    }
                    Marshal.Copy(outRow, 0, IntPtr.Add(d.Scan0, y * d.Stride), w * 4);
                }
            }
            finally
            {
                result.UnlockBits(d);
            }
            return result;
        }

        // pdfium 位图（含灰度/BGR/BGRx/BGRA）-> GDI+ 位图
        private static Bitmap BitmapFromPdfium(IntPtr bmp, int w, int h)
        {
            int fmt = Pdfium.FPDFBitmap_GetFormat(bmp);
            int bpp = fmt == 1 ? 1 : (fmt == 2 ? 3 : 4);
            Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            BitmapData d = result.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                IntPtr src = Pdfium.FPDFBitmap_GetBuffer(bmp);
                int stride = Pdfium.FPDFBitmap_GetStride(bmp);
                byte[] inRow = new byte[stride];
                byte[] outRow = new byte[d.Stride];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(src, y * stride), inRow, 0, stride);
                    for (int x = 0; x < w; x++)
                    {
                        int si = x * bpp, di = x * 4;
                        if (bpp == 1)
                        {
                            outRow[di] = inRow[si];
                            outRow[di + 1] = inRow[si];
                            outRow[di + 2] = inRow[si];
                        }
                        else
                        {
                            outRow[di] = inRow[si];
                            outRow[di + 1] = inRow[si + 1];
                            outRow[di + 2] = inRow[si + 2];
                        }
                        outRow[di + 3] = 255;
                    }
                    Marshal.Copy(outRow, 0, IntPtr.Add(d.Scan0, y * d.Stride), w * 4);
                }
            }
            finally
            {
                result.UnlockBits(d);
            }
            return result;
        }

        // 按给定页序重建一份 PDF 写到 outPath（先写 .tmp 再替换，避免写坏已有文件）
        // 保存：有源文件时在原文档副本上增删移页（保住书签/属性/表单）；空白新文档则从零构建
        public void SavePages(string outPath, IList<PageRef> pages)
        {
            if (pages.Count == 0) throw new Exception("没有页面可保存。");
            if (SourcePath != null && _doc != IntPtr.Zero) { SaveInPlace(outPath, pages); return; }
            SaveFresh(outPath, pages);
        }

        // 在原文档副本上：删掉不要的页、复制重复的页、调整顺序、插入图片页
        private void SaveInPlace(string outPath, IList<PageRef> pages)
        {
            string tmp = outPath + ".tmp";
            IntPtr work = Pdfium.FPDF_LoadMemDocument(_pin.AddrOfPinnedObject(), _bytes.Length, null);
            if (work == IntPtr.Zero) throw new Exception("无法创建工作副本。");
            IntPtr src = Pdfium.FPDF_LoadMemDocument(_pin.AddrOfPinnedObject(), _bytes.Length, null);
            try
            {
                int n = Pdfium.FPDF_GetPageCount(work);
                int[] used = new int[n];
                foreach (PageRef p in pages)
                    if (!p.IsImage && p.SrcIndex >= 0 && p.SrcIndex < n) used[p.SrcIndex]++;

                for (int i = n - 1; i >= 0; i--)
                    if (used[i] == 0) Pdfium.FPDFPage_Delete(work, i);

                int[] newIdx = new int[n];
                for (int i = 0; i < n; i++) newIdx[i] = -1;
                int c0 = 0;
                for (int i = 0; i < n; i++) if (used[i] > 0) newIdx[i] = c0++;

                int[] identity = new int[pages.Count];
                int[] copySeq = new int[n];
                for (int i = 0; i < pages.Count; i++)
                {
                    PageRef p = pages[i];
                    if (p.IsImage) { identity[i] = -1; continue; }
                    int si = p.SrcIndex;
                    if (copySeq[si] == 0) identity[i] = newIdx[si];
                    else
                    {
                        int cnt = Pdfium.FPDF_GetPageCount(work);
                        if (Pdfium.FPDF_ImportPages(work, src, (si + 1).ToString(), cnt) == 0)
                            throw new Exception("复制第 " + (si + 1) + " 页失败");
                        identity[i] = cnt;
                    }
                    copySeq[si]++;
                }

                List<int> cur = new List<int>();
                for (int i = 0; i < Pdfium.FPDF_GetPageCount(work); i++) cur.Add(i);
                int pos = 0;
                for (int i = 0; i < pages.Count; i++)
                {
                    if (identity[i] < 0) continue;
                    int from = cur.IndexOf(identity[i]);
                    if (from < 0) throw new Exception("页序状态异常。");
                    if (from != pos)
                    {
                        int[] one = new int[] { from };
                        if (Pdfium.FPDF_MovePages(work, one, 1, pos) == 0)
                            throw new Exception("调整页序失败。");
                        int v = cur[from];
                        cur.RemoveAt(from);
                        cur.Insert(pos, v);
                    }
                    pos++;
                }

                for (int i = 0; i < pages.Count; i++)
                {
                    PageRef p = pages[i];
                    if (!p.IsImage) continue;
                    if (p.PngIdat != null)
                    {
                        string mpg = MiniPdf.BuildPngImagePage(p.PngIdat, p.PxW, p.PxH, p.PngComps, p.PngBpc,
                            p.PngIcc, p.Width, p.Height);
                        try { ImportOnePage(work, mpg, i); }
                        finally { try { File.Delete(mpg); } catch { } }
                        continue;
                    }
                    if (p.Alpha != null && p.Pixels != null)
                    {
                        string mp = MiniPdf.BuildImagePage(p.Pixels, p.Alpha, p.PxW, p.PxH, p.Width, p.Height);
                        try
                        {
                            byte[] mb = File.ReadAllBytes(mp);
                            GCHandle pin2 = GCHandle.Alloc(mb, GCHandleType.Pinned);
                            try
                            {
                                IntPtr mini = Pdfium.FPDF_LoadMemDocument(pin2.AddrOfPinnedObject(), mb.Length, null);
                                if (mini == IntPtr.Zero) throw new Exception("透明图片页生成失败");
                                try
                                {
                                    if (Pdfium.FPDF_ImportPages(work, mini, "1", i) == 0)
                                        throw new Exception("导入透明图片页失败");
                                }
                                finally { Pdfium.FPDF_CloseDocument(mini); }
                            }
                            finally { pin2.Free(); }
                        }
                        finally
                        {
                            try { File.Delete(mp); } catch { }
                        }
                        continue;
                    }
                    IntPtr np = Pdfium.FPDFPage_New(work, i, p.Width, p.Height);
                    if (np == IntPtr.Zero) throw new Exception("创建图片页失败（第 " + (i + 1) + " 页）");
                    try { AddImageToPage(work, np, p); }
                    finally { Pdfium.FPDF_ClosePage(np); }
                }

                WriteDoc(work, tmp);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
            finally
            {
                if (src != IntPtr.Zero) Pdfium.FPDF_CloseDocument(src);
                Pdfium.FPDF_CloseDocument(work);
            }
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(tmp, outPath);
        }

        // 从零构建（新建的空白 PDF 用）：先写 .tmp 再替换，避免写坏已有文件
        private void SaveFresh(string outPath, IList<PageRef> pages)
        {
            if (pages.Count == 0) throw new Exception("没有页面可保存。");
            string tmp = outPath + ".tmp";
            IntPtr dest = Pdfium.FPDF_CreateNewDocument();
            if (dest == IntPtr.Zero) throw new Exception("无法创建新 PDF。");
            try
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    PageRef p = pages[i];
                    if (p.IsImage && p.PngIdat != null)
                    {
                        // PNG 原样嵌入（16 位/ICC 保真）
                        string mpg = MiniPdf.BuildPngImagePage(p.PngIdat, p.PxW, p.PxH, p.PngComps, p.PngBpc,
                            p.PngIcc, p.Width, p.Height);
                        try { ImportOnePage(dest, mpg, i); }
                        finally { try { File.Delete(mpg); } catch { } }
                    }
                    else if (p.IsImage && p.Alpha != null && p.Pixels != null)
                    {
                        // 带透明通道：用自写的小 PDF（含 SMask）导入，保住透明
                        string mp = MiniPdf.BuildImagePage(p.Pixels, p.Alpha, p.PxW, p.PxH, p.Width, p.Height);
                        try
                        {
                            byte[] mb = File.ReadAllBytes(mp);
                            GCHandle pin2 = GCHandle.Alloc(mb, GCHandleType.Pinned);
                            try
                            {
                                IntPtr mini = Pdfium.FPDF_LoadMemDocument(pin2.AddrOfPinnedObject(), mb.Length, null);
                                if (mini == IntPtr.Zero) throw new Exception("透明图片页生成失败");
                                try
                                {
                                    if (Pdfium.FPDF_ImportPages(dest, mini, "1", i) == 0)
                                        throw new Exception("导入透明图片页失败");
                                }
                                finally { Pdfium.FPDF_CloseDocument(mini); }
                            }
                            finally { pin2.Free(); }
                        }
                        finally
                        {
                            try { File.Delete(mp); } catch { }
                        }
                    }
                    else if (p.IsImage)
                    {
                        IntPtr ip = Pdfium.FPDFPage_New(dest, i, p.Width, p.Height);
                        if (ip == IntPtr.Zero) throw new Exception("创建图片页失败（第 " + (i + 1) + " 页）");
                        try
                        {
                            AddImageToPage(dest, ip, p);
                        }
                        finally
                        {
                            Pdfium.FPDF_ClosePage(ip);
                        }
                    }
                    else
                    {
                        string range = (p.SrcIndex + 1).ToString();
                        if (Pdfium.FPDF_ImportPages(dest, _doc, range, i) == 0)
                            throw new Exception("复制原第 " + (p.SrcIndex + 1) + " 页失败");
                    }
                }
                WriteDoc(dest, tmp);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { }
                throw;
            }
            finally
            {
                Pdfium.FPDF_CloseDocument(dest);
            }
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(tmp, outPath);
        }

        // 把外部小 PDF 的第 1 页导入到 dest 的第 index 位（用于 PNG 原样嵌入）
        private static void ImportOnePage(IntPtr dest, string pdfPath, int index)
        {
            byte[] mb = File.ReadAllBytes(pdfPath);
            GCHandle pin2 = GCHandle.Alloc(mb, GCHandleType.Pinned);
            try
            {
                IntPtr mini = Pdfium.FPDF_LoadMemDocument(pin2.AddrOfPinnedObject(), mb.Length, null);
                if (mini == IntPtr.Zero) throw new Exception("原样嵌入的临时 PDF 打不开");
                try
                {
                    if (Pdfium.FPDF_ImportPages(dest, mini, "1", index) == 0)
                        throw new Exception("原样嵌入导入失败");
                }
                finally { Pdfium.FPDF_CloseDocument(mini); }
            }
            finally { pin2.Free(); }
        }
        private static void AddImageToPage(IntPtr doc, IntPtr page, PageRef p)
        {
            IntPtr obj = Pdfium.FPDFPageObj_NewImageObj(doc);
            if (obj == IntPtr.Zero) throw new Exception("创建图片对象失败。");
            IntPtr[] arr = new IntPtr[] { page };
            bool ok = false;
            if (p.Jpeg) ok = LoadJpeg(obj, arr, p.Image) != 0;
            if (!ok) ok = SetBitmap(obj, arr, p.Image, p.PxW, p.PxH) != 0;
            if (!ok) throw new Exception("读不了这张图片（格式不支持或文件损坏）。");

            double scale = Math.Min(p.Width / (double)p.PxW, p.Height / (double)p.PxH);
            double dw = p.PxW * scale;
            double dh = p.PxH * scale;
            Pdfium.FPDFPageObj_Transform(obj, dw, 0, 0, dh, (p.Width - dw) / 2.0, (p.Height - dh) / 2.0);
            if (Pdfium.FPDFPage_InsertObject(page, obj) == 0) throw new Exception("插入图片失败。");
            if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new Exception("生成页面内容失败。");
        }

        // JPEG 原样嵌入（不重编码，体积小）；失败返回 0，调用方回退到位图方案
        private static int LoadJpeg(IntPtr obj, IntPtr[] pages, byte[] bytes)
        {
            GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            Pdfium.GetBlockDelegate cb = delegate(IntPtr param, uint position, IntPtr buffer, uint size)
            {
                if ((long)position + size > bytes.Length) return 0;
                Marshal.Copy(bytes, (int)position, buffer, (int)size);
                return 1;
            };
            try
            {
                Pdfium.FPDF_FILEACCESS acc = new Pdfium.FPDF_FILEACCESS();
                acc.FileLen = (uint)bytes.Length;
                acc.GetBlock = Marshal.GetFunctionPointerForDelegate(cb);
                acc.Param = IntPtr.Zero;
                int r = Pdfium.FPDFImageObj_LoadJpegFileInline(pages, 1, obj, ref acc);
                GC.KeepAlive(cb);
                return r;
            }
            finally
            {
                pin.Free();
            }
        }

        // 其余格式：用 GDI+ 解码成 BGRx 位图交给 pdfium
        private static int SetBitmap(IntPtr obj, IntPtr[] pages, byte[] bytes, int pxW, int pxH)
        {
            if (pxW <= 0 || pxH <= 0) return 0;
            IntPtr buf = Marshal.AllocHGlobal(pxW * pxH * 4);
            IntPtr bmp = IntPtr.Zero;
            try
            {
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Image src = Image.FromStream(ms))
                using (Bitmap canvas = new Bitmap(pxW, pxH, PixelFormat.Format32bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.White); // 透明区域铺白底
                        g.DrawImage(src, new Rectangle(0, 0, pxW, pxH));
                    }
                    BitmapData d = canvas.LockBits(new Rectangle(0, 0, pxW, pxH),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        byte[] row = new byte[pxW * 4];
                        for (int y = 0; y < pxH; y++)
                        {
                            Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), row, 0, pxW * 4);
                            Marshal.Copy(row, 0, IntPtr.Add(buf, y * pxW * 4), pxW * 4);
                        }
                    }
                    finally
                    {
                        canvas.UnlockBits(d);
                    }
                }
                bmp = Pdfium.FPDFBitmap_CreateEx(pxW, pxH, Pdfium.BitmapBgrx, buf, pxW * 4);
                if (bmp == IntPtr.Zero) return 0;
                return Pdfium.FPDFImageObj_SetBitmap(pages, 1, obj, bmp);
            }
            finally
            {
                if (bmp != IntPtr.Zero) Pdfium.FPDFBitmap_Destroy(bmp);
                Marshal.FreeHGlobal(buf);
            }
        }

        private static void WriteDoc(IntPtr doc, string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Pdfium.WriteBlockDelegate cb = delegate(IntPtr self, IntPtr data, uint size)
                {
                    try
                    {
                        byte[] block = new byte[(int)size];
                        Marshal.Copy(data, block, 0, (int)size);
                        fs.Write(block, 0, (int)size);
                        return 1;
                    }
                    catch
                    {
                        return 0;
                    }
                };
                Pdfium.FPDF_FILEWRITE fw = new Pdfium.FPDF_FILEWRITE();
                fw.version = 1;
                fw.WriteBlock = Marshal.GetFunctionPointerForDelegate(cb);
                int ok = Pdfium.FPDF_SaveAsCopy(doc, ref fw, 0);
                GC.KeepAlive(cb);
                if (ok == 0) throw new Exception("写入文件失败：" + path);
            }
        }

        public void Dispose()
        {
            if (_cacheBmp != null)
            {
                _cacheBmp.Dispose();
                _cacheBmp = null;
                _cacheRef = null;
            }
            if (_doc != IntPtr.Zero)
            {
                Pdfium.FPDF_CloseDocument(_doc);
                _doc = IntPtr.Zero;
            }
            if (_pin.IsAllocated) _pin.Free();
            _bytes = null;
        }
    }

    internal static class Util
    {
        // 同名就自动编号 xxx(2).pdf，绝不覆盖
        public static string AutoName(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 10000; i++)
            {
                string cand = Path.Combine(dir, name + "(" + i + ")" + ext);
                if (!File.Exists(cand)) return cand;
            }
            throw new Exception("同名文件太多，请先清理：" + path);
        }

        public static List<PageRef> OddOf(IList<PageRef> pages)
        {
            List<PageRef> r = new List<PageRef>();
            for (int i = 0; i < pages.Count; i += 2) r.Add(pages[i]);
            return r;
        }

        public static List<PageRef> EvenOf(IList<PageRef> pages)
        {
            List<PageRef> r = new List<PageRef>();
            for (int i = 1; i < pages.Count; i += 2) r.Add(pages[i]);
            return r;
        }
    }
}
