using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PdfTool
{
    // 自己写一个"单页 + 图片（可带透明蒙版 SMask）"的最小 PDF，
    // 供插入图片时使用：pdfium 没有挂 SMask 的接口，只能这样保住透明通道。
    internal static class MiniPdf
    {
        // 用"已压缩好的 PNG 扫描线"直接建一页：PNG 的 IDAT 就是 Flate + PNG 预测器格式，
        // 与 PDF 图片流完全一致，可以原样嵌入 —— 16 位、ICC 都能保住。
        public static byte[] BuildPngImagePage(byte[] idat, int w, int h, int comps, int bpc,
            byte[] icc, double pageW, double pageH)
        {
            List<byte[]> objects = new List<byte[]>();

            bool hasIcc = icc != null && icc.Length > 0;
            string cs = hasIcc ? "[/ICCBased 5 0 R]" : (comps == 1 ? "/DeviceGray" : "/DeviceRGB");
            int contentsObj = hasIcc ? 6 : 5;

            objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
            objects.Add(Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"));
            objects.Add(Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + F(pageW) + " " + F(pageH) + "] "
                + "/Resources << /XObject << /Im0 4 0 R >> >> /Contents " + contentsObj + " 0 R >>"));
            objects.Add(Stream("<< /Type /XObject /Subtype /Image /Width " + w + " /Height " + h
                + " /ColorSpace " + cs + " /BitsPerComponent " + bpc + " /Filter /FlateDecode"
                + " /DecodeParms << /Predictor 15 /Colors " + comps + " /BitsPerComponent " + bpc
                + " /Columns " + w + " >> /Length " + idat.Length + " >>", idat));
            if (hasIcc)
            {
                objects.Add(Stream("<< /N " + comps + " /Length " + icc.Length + " >>", icc));
            }

            double sc = Math.Min(pageW / w, pageH / h);
            double dw = w * sc, dh = h * sc;
            string content = "q " + F(dw) + " 0 0 " + F(dh) + " " + F((pageW - dw) / 2) + " " + F((pageH - dh) / 2) + " cm /Im0 Do Q";
            objects.Add(Stream("<< /Length " + content.Length + " >>", Ascii(content)));

            return WritePdfBytes(objects);
        }

        private static byte[] WritePdfBytes(List<byte[]> objects)
        {
            using (MemoryStream fs = new MemoryStream())
            {
                List<long> offsets = new List<long>();
                Write(fs, Ascii("%PDF-1.4\n"));
                for (int i = 0; i < objects.Count; i++)
                {
                    offsets.Add(fs.Position);
                    Write(fs, Ascii((i + 1) + " 0 obj\n"));
                    Write(fs, objects[i]);
                    Write(fs, Ascii("\nendobj\n"));
                }
                long xref = fs.Position;
                Write(fs, Ascii("xref\n0 " + (objects.Count + 1) + "\n"));
                Write(fs, Ascii("0000000000 65535 f \n"));
                for (int i = 0; i < offsets.Count; i++)
                {
                    string off = offsets[i].ToString();
                    while (off.Length < 10) off = "0" + off;
                    Write(fs, Ascii(off + " 00000 n \n"));
                }
                Write(fs, Ascii("trailer\n<< /Size " + (objects.Count + 1) + " /Root 1 0 R >>\nstartxref\n"
                    + xref + "\n%%EOF\n"));
                return fs.ToArray();
            }
        }

        public static byte[] BuildImagePage(byte[] rgb, byte[] alpha, int w, int h, double pageW, double pageH)
        {
            List<byte[]> objects = new List<byte[]>();

            byte[] imgData = Flate(rgb);
            byte[] maskData = alpha != null ? Flate(alpha) : null;

            // 1 catalog / 2 pages / 3 page / 4 image / 5 contents / 6 smask
            objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
            objects.Add(Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"));
            objects.Add(Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + F(pageW) + " " + F(pageH) + "]"
                + " /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>"));

            string imgDict = "<< /Type /XObject /Subtype /Image /Width " + w + " /Height " + h
                + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode"
                + (maskData != null ? " /SMask 6 0 R" : "") + " /Length " + imgData.Length + " >>";
            objects.Add(Stream(imgDict, imgData));

            // 等比缩放居中放到页面上
            double sc = Math.Min(pageW / w, pageH / h);
            double dw = w * sc, dh = h * sc;
            double dx = (pageW - dw) / 2.0, dy = (pageH - dh) / 2.0;
            string content = "q " + F(dw) + " 0 0 " + F(dh) + " " + F(dx) + " " + F(dy) + " cm /Im0 Do Q";
            objects.Add(Stream("<< /Length " + content.Length + " >>", Ascii(content)));

            if (maskData != null)
            {
                string maskDict = "<< /Type /XObject /Subtype /Image /Width " + w + " /Height " + h
                    + " /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode"
                    + " /Length " + maskData.Length + " >>";
                objects.Add(Stream(maskDict, maskData));
            }

            return WritePdfBytes(objects);
        }

        private static void Write(Stream s, byte[] data) { s.Write(data, 0, data.Length); }

        private static byte[] Ascii(string s)
        {
            return Encoding.ASCII.GetBytes(s);
        }

        private static string F(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static byte[] Stream(string dict, byte[] data)
        {
            byte[] head = Ascii(dict + "\nstream\n");
            byte[] tail = Ascii("\nendstream");
            byte[] all = new byte[head.Length + data.Length + tail.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(data, 0, all, head.Length, data.Length);
            Buffer.BlockCopy(tail, 0, all, head.Length + data.Length, tail.Length);
            return all;
        }

        // zlib 压缩（PDF 的 FlateDecode 要带 zlib 头 + Adler32 校验）
        private static byte[] Flate(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    ds.Write(data, 0, data.Length);
                }
                uint a = Adler32(data);
                ms.WriteByte((byte)(a >> 24));
                ms.WriteByte((byte)(a >> 16));
                ms.WriteByte((byte)(a >> 8));
                ms.WriteByte((byte)a);
                return ms.ToArray();
            }
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }
    }
}
