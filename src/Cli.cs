using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PdfTool
{
    // 命令行模式（测试/批处理用；双击 exe 走图形界面）
    internal static class Cli
    {
        internal static int Split(string path)
        {
            using (PdfJob job = PdfJob.Load(path))
            {
                List<PageRef> odd = Util.OddOf(job.Pages);
                List<PageRef> even = Util.EvenOf(job.Pages);
                List<string> hashes = new List<string>();
                for (int i = 0; i < job.Pages.Count; i++) hashes.Add(HashPage(job, i));

                string p1 = Util.AutoName(job.OddPath);
                job.SavePages(p1, odd);
                Console.WriteLine("奇数页 " + odd.Count + " 页 -> " + p1);
                Verify(job, p1, hashes, 0);

                if (even.Count > 0)
                {
                    string p2 = Util.AutoName(job.EvenPath);
                    job.SavePages(p2, even);
                    Console.WriteLine("偶数页 " + even.Count + " 页 -> " + p2);
                    Verify(job, p2, hashes, 1);
                }
            }
            return 0;
        }

        // 逐页比对：输出第 k 页必须和原第 (start + 2k) 页渲染一致
        private static void Verify(PdfJob src, string outPath, List<string> hashes, int start)
        {
            using (PdfJob outDoc = PdfJob.Load(outPath))
            {
                int expect = (hashes.Count - start + 1) / 2;
                if (outDoc.PageCount != expect)
                    throw new Exception("页数不符：" + outPath + " 期望 " + expect + " 实际 " + outDoc.PageCount);
                for (int k = 0; k < outDoc.PageCount; k++)
                    Same(HashPage(outDoc, k), hashes[start + 2 * k], "第 " + (k + 1) + " 页");
                Console.WriteLine("  校验通过：逐页内容一致");
            }
        }

        // 渲染导出一张图（调试/对比用）：--rendump 文件.pdf 页码(1起) 宽 输出.png
        internal static int RenderDump(string path, string pageNo, string width, string outPath)
        {
            using (PdfJob job = PdfJob.Load(path))
            {
                int idx = int.Parse(pageNo) - 1;
                if (idx < 0 || idx >= job.PageCount) throw new Exception("页码越界：" + pageNo);
                int w = int.Parse(width);
                SizeF pt = job.GetPageSizePoints(job.Pages[idx]);
                int h = (int)Math.Round(w * (pt.Height / (double)pt.Width));
                using (Bitmap b = job.RenderPage(idx, w, h))
                {
                    b.Save(outPath, ImageFormat.Png);
                }
                Console.WriteLine("已导出 " + outPath + "（" + w + "x" + h + "）");
            }
            return 0;
        }

        // 提取某页里的图片：--extract 文件.pdf 页码 输出目录
        internal static int Extract(string path, string pageNo, string outDir)
        {
            using (PdfJob job = PdfJob.Load(path))
            {
                int idx = int.Parse(pageNo) - 1;
                if (idx < 0 || idx >= job.PageCount) throw new Exception("页码越界：" + pageNo);
                List<string> saved = new List<string>();
                string prefix = job.BaseName + "_第" + pageNo + "页";
                int n = job.ExtractPageImages(idx, outDir, prefix, saved);
                Console.WriteLine("提取 " + n + " 张图片到 " + outDir);
                foreach (string f in saved) Console.WriteLine("  " + f);
            }
            return 0;
        }

        // --lzwtest 压缩数据文件 参考像素文件：用外部样本验证 LZW 解码器
        internal static int LzwTest(string lzwPath, string refPath)
        {
            byte[] lzw = File.ReadAllBytes(lzwPath);
            byte[] want = File.ReadAllBytes(refPath);
            bool[] conventions = new bool[] { false, true };
            foreach (bool early in conventions)
            {
                byte[] got = RawExport.Lzw(lzw, early);
                if (got == null) { Console.WriteLine("EarlyChange=" + early + ": 解码失败"); continue; }
                int diff = 0;
                int n = Math.Min(got.Length, want.Length);
                for (int i = 0; i < n; i++) if (got[i] != want[i]) diff++;
                diff += Math.Abs(got.Length - want.Length);
                Console.WriteLine("EarlyChange=" + early + ": 解出 " + got.Length + " 字节（期望 " + want.Length
                    + "），不同 " + diff + " 字节" + (diff == 0 ? " -> 完全一致" : ""));
            }
            return 0;
        }
        // 诊断自解析：--rawinfo 文件.pdf 页码
        internal static int RawInfo(string path, string pageNo)
        {
            RawPdf r = RawPdf.Open(path);
            if (r == null) { Console.WriteLine("自解析失败（对象太少）"); return 1; }
            int idx = int.Parse(pageNo) - 1;
            Console.WriteLine(r.DebugScan());
            Console.WriteLine("自解析页面数: " + r.PageCount);
            Console.WriteLine("页面概况: " + r.DebugPage(idx));
            List<RawImage> list = r.PageImages(idx);
            Console.WriteLine("第 " + pageNo + " 页找到 " + list.Count + " 张图");
            foreach (RawImage im in list)
            {
                Console.WriteLine("  对象" + im.ObjNum + " " + im.Filter + (im.MultiFilter ? "(多重)" : "")
                    + " " + im.Width + "x" + im.Height + " bits=" + im.Bits + " cs=" + im.ColorSpace
                    + " 原始字节=" + (im.Raw == null ? 0 : im.Raw.Length)
                    + " 预测器=" + (im.HasPredictor ? im.Predictor.ToString() : "-"));
                List<string> saved = new List<string>();
                bool ok = RawExport.Save(im, Path.Combine(Path.GetDirectoryName(path), "_probe_" + im.ObjNum), saved);
                Console.WriteLine("      原样导出: " + (ok ? "成功" : "失败"));
            }
            return 0;
        }

        // 自检：删第 2 页 + 复制第 1 页 + 插入一张图片页 -> 另存 -> 回读逐页比对
        internal static int SelfTest(string path)
        {
            using (PdfJob job = PdfJob.Load(path))
            {
                int n = job.PageCount;
                List<string> hashes = new List<string>();
                for (int i = 0; i < n; i++) hashes.Add(HashPage(job, i));

                job.Pages.RemoveAt(1);                        // 删原第 2 页
                job.Pages.Insert(1, job.Pages[0].Clone());    // 第 1 页复制一份

                string imgPng = MakeTestImage(Path.GetDirectoryName(Path.GetFullPath(path)), "png");
                string imgJpg = MakeTestImage(Path.GetDirectoryName(Path.GetFullPath(path)), "jpg");
                string imgAlpha = MakeAlphaPng(Path.GetDirectoryName(Path.GetFullPath(path)));
                string img16 = MakePng16(Path.GetDirectoryName(Path.GetFullPath(path)));
                try
                {
                    SizeF target = job.GetPageSizePoints(job.Pages[0]);
                    job.Pages.Insert(2, PdfJob.MakeImagePage(imgPng, target)); // PNG 通道
                    job.Pages.Insert(3, PdfJob.MakeImagePage(imgJpg, target)); // JPEG 通道

                    job.Pages.Add(PdfJob.MakeImagePage(imgAlpha, target));      // 带透明通道
                    job.Pages.Add(PdfJob.MakeImagePage(img16, target));         // 16 位 PNG

                    string outPath = Util.AutoName(job.EditedPath);
                    job.SavePages(outPath, job.Pages);
                    Console.WriteLine("已保存：" + outPath);

                    using (PdfJob chk = PdfJob.Load(outPath))
                    {
                        if (chk.PageCount != n + 4)
                            throw new Exception("页数不对：期望 " + (n + 4) + "，实际 " + chk.PageCount);
                        Same(HashPage(chk, 0), hashes[0], "第1页");
                        Same(HashPage(chk, 1), hashes[0], "第2页(副本)");
                        CheckImagePage(chk, 2, "PNG");
                        CheckImagePage(chk, 3, "JPG");
                        for (int k = 4; k < chk.PageCount - 2; k++)
                            Same(HashPage(chk, k), hashes[k - 2], "第 " + (k + 1) + " 页");
                        Console.WriteLine("自检通过：" + n + " 页 -> " + chk.PageCount
                            + " 页（含 4 图片页），逐页内容一致");
                    }
                }
                finally
                {
                    try { File.Delete(imgPng); } catch { }
                    try { File.Delete(imgJpg); } catch { }
                    try { File.Delete(imgAlpha); } catch { }
                    try { File.Delete(img16); } catch { }
                }
            }
            return 0;
        }

        // 造一张 16 位/通道的 PNG（测试插入是否保住位深）
        private static string MakePng16(string dir)
        {
            string p = Path.Combine(dir, "_selftest_png16.png");
            int S = 64;
            using (Bitmap b = new Bitmap(S, S, PixelFormat.Format48bppRgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.FromArgb(0, 128, 255));
                }
                b.Save(p, ImageFormat.Png);
            }
            return p;
        }
        // 造一张带透明通道的测试图（圆形，四周透明）

        private static string MakeAlphaPng(string dir)

        {

            string p = Path.Combine(dir, "_selftest_alpha.png");

            int S = 200;

            using (Bitmap b = new Bitmap(S, S, PixelFormat.Format32bppArgb))

            {

                using (Graphics g = Graphics.FromImage(b))

                {

                    g.Clear(Color.Transparent);

                    using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 220, 20, 20)))

                        g.FillEllipse(br, 20, 20, S - 40, S - 40);

                }

                b.Save(p, ImageFormat.Png);

            }

            return p;

        }

        // 造一张测试图：A4 比例，左半边黑、右半边白
        private static string MakeTestImage(string dir, string ext)
        {
            string p = Path.Combine(dir, "_selftest_img." + ext);
            using (Bitmap b = new Bitmap(595, 842))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.White);
                g.FillRectangle(Brushes.Black, 0, 0, 297, 842);
                if (ext == "jpg") b.Save(p, ImageFormat.Jpeg);
                else b.Save(p, ImageFormat.Png);
            }
            return p;
        }

        // 渲染成小图算指纹："宽x高:权重和:像素数"
        private static string HashPage(PdfJob j, int idx)
        {
            using (Bitmap b = j.RenderPage(idx, 48, 48))
            {
                BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    long sum = 0;
                    int cnt = 0;
                    byte[] row = new byte[d.Stride];
                    for (int y = 0; y < b.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), row, 0, d.Stride);
                        for (int x = 0; x < b.Width * 4; x += 4)
                        {
                            sum += row[x] + row[x + 1] * 3L + row[x + 2] * 7L;
                            cnt++;
                        }
                    }
                    return b.Width + "x" + b.Height + ":" + sum + ":" + cnt;
                }
                finally
                {
                    b.UnlockBits(d);
                }
            }
        }

        private static void Same(string a, string b, string label)
        {
            string[] pa = a.Split(':');
            string[] pb = b.Split(':');
            if (pa[0] != pb[0]) throw new Exception(label + " 尺寸不符：" + a + " vs " + b);
            long sa = long.Parse(pa[1]);
            long sb = long.Parse(pb[1]);
            long tol = Math.Max(2000, sa / 200); // 0.5% 容差
            if (Math.Abs(sa - sb) > tol)
                throw new Exception(label + " 内容不符：" + sa + " vs " + sb + "（超出容差 " + tol + "）");
        }

        // 图片页检查：左半边应为黑、右半边应为白
        private static void CheckImagePage(PdfJob doc, int idx, string tag)
        {
            using (Bitmap b = doc.RenderPage(idx, 100, 140))
            {
                BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int left = Brightness(d, b.Width / 4, b.Height / 2);
                    int right = Brightness(d, b.Width * 3 / 4, b.Height / 2);
                    if (left > 200)
                        throw new Exception("图片页左半边应为黑色，实际亮度 " + left);
                    if (right < 600)
                        throw new Exception("图片页右半边应为白色，实际亮度 " + right);
                }
                finally
                {
                    b.UnlockBits(d);
                }
            }
            Console.WriteLine("  第 " + (idx + 1) + " 页确认为插入的图片（" + tag + "，左黑右白）");
        }

        private static int Brightness(BitmapData d, int x, int y)
        {
            byte[] px = new byte[4];
            Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride + x * 4), px, 0, 4);
            return px[0] + px[1] + px[2];
        }
    }
}
