using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace PdfTool
{
    // 把 RawPdf 找到的图片对象按"原样字节"导出
    internal static class RawExport
    {
        public static bool Save(RawImage img, string pathBase, List<string> saved)
        {
            if (img == null || img.Raw == null || img.Raw.Length < 1) return false;
            if (img.MultiFilter) return false;

            // 1) 依次解开前置解码器（ASCII85 / ASCIIHex / RunLength / LZW / Flate）
            for (int i = 0; i < img.PreFilters.Count; i++)
            {
                string pf = img.PreFilters[i];
                byte[] dec;
                if (pf == "ASCII85Decode") dec = Ascii85(img.Raw);
                else if (pf == "ASCIIHexDecode") dec = AsciiHex(img.Raw);
                else if (pf == "RunLengthDecode") dec = RunLength(img.Raw);
                else if (pf == "LZWDecode")
                {
                    string pm = (i < img.PreParms.Count) ? img.PreParms[i] : null;
                    dec = Lzw(img.Raw, !HasFlag(pm, "EarlyChange", "0"));
                }
                else if (pf == "FlateDecode") dec = Inflate(img.Raw);
                else return false;
                if (dec == null) return false;
                img.Raw = dec;
                // 前置 Flate/LZW 可能带预测器
                string parm = (i < img.PreParms.Count) ? img.PreParms[i] : null;
                if (parm != null && (pf == "FlateDecode" || pf == "LZWDecode"))
                {
                    byte[] pp = PredictorFrom(parm, img.Raw);
                    if (pp == null) return false;
                    img.Raw = pp;
                }
            }

            // 2) 有透明蒙版：合成带 alpha 的 PNG，保证透明不丢
            if (img.Mask != null) return SaveWithMask(img, pathBase, saved);

            // 3) 按图片编码导出
            if (img.Filter == "DCTDecode")
            {
                if (!(img.Raw[0] == 0xFF && img.Raw[1] == 0xD8)) return false;
                Write(pathBase + ".jpg", img.Raw, saved);
                return true;
            }
            if (img.Filter == "JPXDecode")
            {
                string ext = (img.Raw[0] == 0xFF && img.Raw[1] == 0x4F) ? ".j2k" : ".jp2";
                Write(pathBase + ext, img.Raw, saved);
                return true;
            }
            if (img.Filter == "JBIG2Decode")
            {
                Write(pathBase + ".jb2", img.Raw, saved);
                return true;
            }
            if (img.Filter == "CCITTFaxDecode")
            {
                byte[] tif = CcittTiff(img);
                if (tif == null) return false;
                Write(pathBase + ".tif", tif, saved);
                return true;
            }
            if (img.Filter == "FlateDecode" || img.Filter == "LZWDecode" || img.Filter == "RunLengthDecode" || img.Filter == null)
            {
                byte[] pixels = img.Raw;
                if (img.Filter == "FlateDecode" || img.Filter == null)
                {
                    pixels = Inflate(img.Raw);
                    if (pixels == null) return false;
                }
                else if (img.Filter == "LZWDecode")
                {
                    pixels = Lzw(img.Raw, true);   // EarlyChange 默认 1
                    if (pixels == null) return false;
                }
                else if (img.Filter == "RunLengthDecode")
                {
                    pixels = RunLength(img.Raw);
                    if (pixels == null) return false;
                }
                if (img.HasPredictor && img.Predictor >= 2) pixels = UndoPredictor(pixels, img);
                if (pixels == null) return false;
                using (Bitmap b = ToBitmap(pixels, img))
                {
                    if (b == null) return false;
                    string p = Util.AutoName(pathBase + ".png");
                    b.Save(p, ImageFormat.Png);
                    saved.Add(Path.GetFileName(p));
                }
                return true;
            }
            return false;
        }

        // 把图片解码成位图（用于合成蒙版）
        private static Bitmap DecodeToBitmap(RawImage img, byte[] data)
        {
            if (img.Filter == "DCTDecode")
            {
                try
                {
                    using (MemoryStream ms = new MemoryStream(data))
                    using (Image im = Image.FromStream(ms))
                    {
                        return new Bitmap(im);
                    }
                }
                catch { return null; }
            }
            if (img.Filter == "FlateDecode" || img.Filter == "LZWDecode" || img.Filter == "RunLengthDecode")
            {
                byte[] px = data;
                if (img.HasPredictor && img.Predictor >= 2) px = UndoPredictor(px, img);
                if (px == null) return null;
                return ToBitmap(px, img);
            }
            return null;
        }

        // 合成：基图 + 蒙版(灰度当 alpha) -> 带透明通道的 PNG
        private static bool SaveWithMask(RawImage img, string pathBase, List<string> saved)
        {
            byte[] baseData = img.Filter == "FlateDecode" || img.Filter == null ? Inflate(img.Raw) : img.Raw;
            if (baseData == null) return false;
            byte[] maskData = img.Mask.Filter == "FlateDecode" || img.Mask.Filter == null
                ? Inflate(img.Mask.Raw) : img.Mask.Raw;
            if (maskData == null) return false;

            using (Bitmap bmp = DecodeToBitmap(img, baseData))
            using (Bitmap msk = DecodeToBitmap(img.Mask, maskData))
            {
                if (bmp == null || msk == null) return false;
                int w = bmp.Width, h = bmp.Height;
                if (msk.Width != w || msk.Height != h) return false;
                Bitmap outp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                try
                {
                    BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    BitmapData md = msk.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    BitmapData od = outp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        byte[] br = new byte[bd.Stride];
                        byte[] mr = new byte[md.Stride];
                        byte[] orow = new byte[od.Stride];
                        for (int y = 0; y < h; y++)
                        {
                            Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), br, 0, bd.Stride);
                            Marshal.Copy(IntPtr.Add(md.Scan0, y * md.Stride), mr, 0, md.Stride);
                            for (int x = 0; x < w; x++)
                            {
                                int so = x * 4, dof = x * 4;
                                orow[dof] = br[so];
                                orow[dof + 1] = br[so + 1];
                                orow[dof + 2] = br[so + 2];
                                orow[dof + 3] = mr[so + 1];   // 蒙版是灰度，取 G 通道
                            }
                            Marshal.Copy(orow, 0, IntPtr.Add(od.Scan0, y * od.Stride), w * 4);
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(bd);
                        msk.UnlockBits(md);
                        outp.UnlockBits(od);
                    }
                    string p = Util.AutoName(pathBase + ".png");
                    outp.Save(p, ImageFormat.Png);
                    saved.Add(Path.GetFileName(p));
                }
                catch
                {
                    outp.Dispose();
                    return false;
                }
                outp.Dispose();
                return true;
            }
        }

        private static bool HasFlag(string dictText, string key, string val)
        {
            if (dictText == null) return false;
            return dictText.IndexOf("/" + key + " " + val) >= 0;
        }

        private static int IntFrom(string dictText, string key, int dflt)
        {
            if (dictText == null) return dflt;
            int i = dictText.IndexOf("/" + key);
            if (i < 0) return dflt;
            int j = i + key.Length + 1;
            while (j < dictText.Length && (dictText[j] == (char)32 || dictText[j] == (char)9)) j++;
            int s = j;
            if (j < dictText.Length && dictText[j] == (char)45) j++;
            while (j < dictText.Length && dictText[j] >= (char)48 && dictText[j] <= (char)57) j++;
            int v;
            return int.TryParse(dictText.Substring(s, j - s), out v) ? v : dflt;
        }

        // 前置解码器的预测器（用 DecodeParms 文本）
        private static byte[] PredictorFrom(string parm, byte[] data)
        {
            int pr = IntFrom(parm, "Predictor", 1);
            if (pr < 2) return data;
            RawImage tmp = new RawImage();
            tmp.Predictor = pr;
            tmp.HasPredictor = true;
            tmp.Colors = IntFrom(parm, "Colors", 1);
            tmp.Bits = IntFrom(parm, "BitsPerComponent", 8);
            tmp.Columns = IntFrom(parm, "Columns", 0);
            tmp.Width = tmp.Columns;
            int rowLen = (tmp.Colors * tmp.Bits * tmp.Columns + 7) / 8;
            if (rowLen <= 0) return data;
            tmp.Height = (pr == 2) ? data.Length / rowLen : data.Length / (rowLen + 1);
            if (tmp.Height <= 0) return data;
            byte[] outp = UndoPredictor(data, tmp);
            return outp != null ? outp : data;
        }

        // RunLength 解码
        internal static byte[] RunLength(byte[] data)
        {
            List<byte> outp = new List<byte>();
            int i = 0;
            while (i < data.Length)
            {
                int l = data[i++];
                if (l == 128) break;
                if (l < 128)
                {
                    for (int k = 0; k <= l && i < data.Length; k++) outp.Add(data[i++]);
                }
                else if (i < data.Length)
                {
                    byte b = data[i++];
                    for (int k = 0; k < 257 - l; k++) outp.Add(b);
                }
            }
            return outp.ToArray();
        }

        // LZW 解码（PDF/TIFF 风格：MSB first，9~12 位，Clear=256 EOD=257）
        internal static byte[] Lzw(byte[] data, bool earlyChange)
        {
            List<byte> outp = new List<byte>();
            List<byte[]> table = new List<byte[]>();
            for (int i = 0; i < 256; i++) table.Add(new byte[] { (byte)i });
            table.Add(null);   // 256 clear
            table.Add(null);   // 257 eod
            int codeBits = 9;
            long acc = 0;
            int accBits = 0;
            int pos = 0;
            byte[] prev = null;
            while (true)
            {
                while (accBits < codeBits && pos < data.Length)
                {
                    acc = (acc << 8) | data[pos++];
                    accBits += 8;
                }
                if (accBits < codeBits) break;
                int code = (int)((acc >> (accBits - codeBits)) & ((1 << codeBits) - 1));
                accBits -= codeBits;
                if (code == 256)
                {
                    table.RemoveRange(258, table.Count - 258);
                    codeBits = 9;
                    prev = null;
                    continue;
                }
                if (code == 257) break;
                byte[] entry;
                if (code < table.Count && table[code] != null) entry = table[code];
                else if (prev != null)
                {
                    entry = new byte[prev.Length + 1];
                    Array.Copy(prev, entry, prev.Length);
                    entry[prev.Length] = prev[0];
                }
                else return null;
                outp.AddRange(entry);
                if (prev != null && table.Count < 4096)
                {
                    byte[] ne = new byte[prev.Length + 1];
                    Array.Copy(prev, ne, prev.Length);
                    ne[prev.Length] = entry[0];
                    table.Add(ne);
                }
                prev = entry;
                int limit = table.Count + (earlyChange ? 1 : 0);
                codeBits = limit >= 2048 ? 12 : (limit >= 1024 ? 11 : (limit >= 512 ? 10 : 9));
            }
            return outp.ToArray();
        }
        private static void Write(string path, byte[] data, List<string> saved)
        {
            string p = Util.AutoName(path);
            File.WriteAllBytes(p, data);
            saved.Add(Path.GetFileName(p));
        }


        // 行数未知时按数据长度推断（对象流/xref 流用）
        internal static byte[] UndoPredictorAuto(byte[] data, RawImage img)
        {
            int colors = img.Colors > 0 ? img.Colors : 1;
            int bits = img.Bits > 0 ? img.Bits : 8;
            int columns = img.Columns > 0 ? img.Columns : img.Width;
            if (columns <= 0) return null;
            int rowLen = (colors * bits * columns + 7) / 8;
            if (rowLen <= 0) return null;
            RawImage tmp = new RawImage();
            tmp.Predictor = img.Predictor;
            tmp.Colors = colors;
            tmp.Bits = bits;
            tmp.Columns = columns;
            tmp.Width = columns;
            tmp.Height = (tmp.Predictor == 2) ? data.Length / rowLen : data.Length / (rowLen + 1);
            if (tmp.Height <= 0) return null;
            return UndoPredictor(data, tmp);
        }

        // ASCII85 解码
        private static byte[] Ascii85(byte[] data)
        {
            List<byte> outp = new List<byte>();
            int i = 0;
            while (i < data.Length && data[i] != 60) i++;
            if (i + 1 < data.Length && data[i + 1] == 126) i += 2;
            else i += 1;
            uint tuple = 0;
            int count = 0;
            for (; i < data.Length; i++)
            {
                byte c = data[i];
                if (c == 126) break;
                if (c == 32 || c == 9 || c == 10 || c == 13 || c == 12 || c == 0) continue;
                if (c == 122 && count == 0)
                {
                    for (int k = 0; k < 4; k++) outp.Add(0);
                    continue;
                }
                if (c < 33 || c > 117) return null;
                tuple = tuple * 85 + (uint)(c - 33);
                count++;
                if (count == 5)
                {
                    for (int k = 3; k >= 0; k--) outp.Add((byte)(tuple >> (8 * k)));
                    tuple = 0;
                    count = 0;
                }
            }
            if (count > 0)
            {
                for (int k = count; k < 5; k++) tuple = tuple * 85 + 84;
                for (int k = 3; k >= 4 - count; k--) outp.Add((byte)(tuple >> (8 * k)));
            }
            return outp.ToArray();
        }

        // ASCIIHex 解码
        private static byte[] AsciiHex(byte[] data)
        {
            List<byte> outp = new List<byte>();
            int hi = -1;
            for (int i = 0; i < data.Length; i++)
            {
                byte c = data[i];
                if (c == 62) break;
                int v = -1;
                if (c >= 48 && c <= 57) v = c - 48;
                else if (c >= 65 && c <= 70) v = c - 55;
                else if (c >= 97 && c <= 102) v = c - 87;
                if (v < 0) continue;
                if (hi < 0) hi = v;
                else { outp.Add((byte)((hi << 4) | v)); hi = -1; }
            }
            if (hi >= 0) outp.Add((byte)(hi << 4));
            return outp.ToArray();
        }

        // CCITT 传真数据 -> 单条 TIFF 容器（无损，Windows 照片可直接打开）
        private static byte[] CcittTiff(RawImage img)
        {
            if (img.CcittByteAlign) return null;
            int w = img.Width > 0 ? img.Width : img.Columns;
            int h = img.Height;
            if (w <= 0 || h <= 0) return null;
            bool g4 = img.CcittK < 0;
            MemoryStream ms = new MemoryStream();
            ms.Write(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, 0, 4);
            ms.Write(new byte[] { 8, 0, 0, 0 }, 0, 4);
            List<byte[]> entries = new List<byte[]>();
            entries.Add(TiffEntry(256, 3, 1, (uint)w));
            entries.Add(TiffEntry(257, 3, 1, (uint)h));
            entries.Add(TiffEntry(258, 3, 1, 1));
            entries.Add(TiffEntry(259, 3, 1, g4 ? 4u : 3u));
            entries.Add(TiffEntry(262, 3, 1, img.CcittBlackIs1 ? 1u : 0u));
            entries.Add(TiffEntry(266, 3, 1, 1));
            int stripPos = entries.Count;
            entries.Add(TiffEntry(273, 4, 1, 0));
            entries.Add(TiffEntry(278, 3, 1, (uint)h));
            entries.Add(TiffEntry(279, 4, 1, (uint)img.Raw.Length));
            if (!g4) entries.Add(TiffEntry(292, 4, 1, 0));
            ms.Write(BitConverter.GetBytes((ushort)entries.Count), 0, 2);
            for (int i = 0; i < entries.Count; i++) ms.Write(entries[i], 0, entries[i].Length);
            ms.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);
            long dataOffset = ms.Position;
            ms.Write(img.Raw, 0, img.Raw.Length);
            byte[] buf = ms.ToArray();
            byte[] offBytes = BitConverter.GetBytes((uint)dataOffset);
            Buffer.BlockCopy(offBytes, 0, buf, 8 + 2 + stripPos * 12 + 8, 4);
            return buf;
        }

        private static byte[] TiffEntry(int tag, int type, int count, uint value)
        {
            byte[] e = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)tag), 0, e, 0, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)type), 0, e, 2, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)count), 0, e, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, e, 8, 4);
            return e;
        }

        // zlib 解压（跳过 2 字节头）
        internal static byte[] Inflate(byte[] data)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(data, 2, data.Length - 2))
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (MemoryStream outMs = new MemoryStream())
                {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = ds.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n);
                    return outMs.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        // 还原 Predictor（TIFF=2 / PNG=10~15）
        internal static byte[] UndoPredictor(byte[] data, RawImage img)
        {
            int colors = img.Colors > 0 ? img.Colors : 1;
            int bits = img.Bits > 0 ? img.Bits : 8;
            int columns = img.Columns > 0 ? img.Columns : img.Width;
            if (columns <= 0) return null;
            int rowLen = (colors * bits * columns + 7) / 8;
            int bpp = Math.Max(1, (colors * bits + 7) / 8);
            int rows = img.Height > 0 ? img.Height : (rowLen > 0 ? data.Length / rowLen : 0);
            if (rowLen <= 0 || rows <= 0) return null;

            byte[] outBuf = new byte[rows * rowLen];
            if (img.Predictor == 2)
            {
                // TIFF：逐列按分量累加
                for (int r = 0; r < rows; r++)
                {
                    int ro = r * rowLen;
                    for (int i = 0; i < rowLen; i++)
                    {
                        int cur = data[ro + i];
                        int left = (i >= bpp) ? outBuf[ro + i - bpp] : 0;
                        outBuf[ro + i] = (byte)((cur + left) & 0xFF);
                    }
                }
                return outBuf;
            }

            // PNG 预测：每行开头一个 filter 字节
            int src = 0;
            for (int r = 0; r < rows; r++)
            {
                if (src >= data.Length) return null;
                int ft = data[src++];
                int ro = r * rowLen;
                int po = (r > 0) ? ro - rowLen : -1;
                for (int i = 0; i < rowLen; i++)
                {
                    if (src >= data.Length) return null;
                    int raw = data[src++];
                    int left = (i >= bpp) ? outBuf[ro + i - bpp] : 0;
                    int up = (po >= 0) ? outBuf[po + i] : 0;
                    int ul = (po >= 0 && i >= bpp) ? outBuf[po + i - bpp] : 0;
                    int val;
                    switch (ft)
                    {
                        case 0: val = raw; break;
                        case 1: val = raw + left; break;
                        case 2: val = raw + up; break;
                        case 3: val = raw + ((left + up) >> 1); break;
                        case 4: val = raw + Paeth(left, up, ul); break;
                        default: return null;
                    }
                    outBuf[ro + i] = (byte)(val & 0xFF);
                }
            }
            return outBuf;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        // 像素数据 -> 位图（支持 1/2/4/8 位，灰度/RGB/CMYK）
        private static Bitmap ToBitmap(byte[] px, RawImage img)
        {
            int w = img.Width, h = img.Height, bits = img.Bits > 0 ? img.Bits : 8;
            if (w <= 0 || h <= 0 || bits == 16) return null;
            int comps = img.Comps;
            if (comps == 0) return null; // 颜色空间认不出，交给调用方退回
            if (img.Palette != null) comps = 1;

            int rowBytes = (comps * bits * w + 7) / 8;
            if (px.Length < rowBytes * h) return null;

            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                byte[] row = new byte[d.Stride];
                int maxVal = (1 << bits) - 1;
                for (int y = 0; y < h; y++)
                {
                    int ro = y * rowBytes;
                    for (int x = 0; x < w; x++)
                    {
                        int r = 0, g = 0, bl = 0;
                        if (img.Palette != null)
                        {
                            int idx = GetComponent(px, ro, x, 0, bits, 1);
                            int bc = img.BaseComps > 0 ? img.BaseComps : 3;
                            int maxIdx = img.Palette.Length / bc - 1;
                            if (idx > maxIdx) idx = maxIdx;
                            if (idx < 0) idx = 0;
                            int off = idx * bc;
                            if (bc == 1)
                            {
                                r = g = bl = img.Palette[off];
                            }
                            else if (bc == 3)
                            {
                                r = img.Palette[off];
                                g = img.Palette[off + 1];
                                bl = img.Palette[off + 2];
                            }
                            else
                            {
                                int c0 = img.Palette[off], m0 = img.Palette[off + 1];
                                int y0 = img.Palette[off + 2], k0 = img.Palette[off + 3];
                                r = 255 - Math.Min(255, c0 + k0);
                                g = 255 - Math.Min(255, m0 + k0);
                                bl = 255 - Math.Min(255, y0 + k0);
                            }
                        }
                        else if (comps == 1)
                        {
                            int v = GetComponent(px, ro, x, 0, bits, comps);
                            if (img.Invert) v = maxVal - v;
                            int gray = bits == 8 ? v : v * 255 / (maxVal == 0 ? 1 : maxVal);
                            r = g = bl = gray;
                        }
                        else if (comps == 3)
                        {
                            r = GetComponent(px, ro, x, 0, bits, comps);
                            g = GetComponent(px, ro, x, 1, bits, comps);
                            bl = GetComponent(px, ro, x, 2, bits, comps);
                        }
                        else
                        {
                            int c = GetComponent(px, ro, x, 0, bits, comps);
                            int m = GetComponent(px, ro, x, 1, bits, comps);
                            int yy = GetComponent(px, ro, x, 2, bits, comps);
                            int k = GetComponent(px, ro, x, 3, bits, comps);
                            r = 255 - Math.Min(255, c + k);
                            g = 255 - Math.Min(255, m + k);
                            bl = 255 - Math.Min(255, yy + k);
                        }
                        int di = x * 4;
                        row[di] = (byte)bl;
                        row[di + 1] = (byte)g;
                        row[di + 2] = (byte)r;
                        row[di + 3] = 255;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(d.Scan0, y * d.Stride), w * 4);
                }
            }
            finally
            {
                b.UnlockBits(d);
            }
            return b;
        }

        private static int GetComponent(byte[] px, int rowOffset, int x, int comp, int bits, int comps)
        {
            if (bits == 8) return px[rowOffset + x * comps + comp];
            int perByte = 8 / bits;
            int idx = rowOffset + (x * comps + comp) / perByte;
            if (idx >= px.Length) return 0;
            int shift = 8 - bits * (((x * comps + comp) % perByte) + 1);
            int mask = (1 << bits) - 1;
            return (px[idx] >> shift) & mask;
        }
    }
}
