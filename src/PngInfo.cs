using System;
using System.Collections.Generic;
using System.IO;

namespace PdfTool
{
    // 解析 PNG 头信息：为了让"插入 PNG"能原样嵌入（不走 GDI+ 的 8 位转换）。
    // 只认非隔行的灰度/RGB（含 16 位）与 ICC；调色板/带 alpha 的仍走原路径。
    internal sealed class PngInfo
    {
        public int Width;
        public int Height;
        public int Bpc = 8;
        public int Comps = 3;
        public bool Lossless;          // 能否原样嵌入
        public byte[] Idat;            // 拼接后的 IDAT 数据（= Flate + PNG 预测器的扫描线）
        public byte[] Icc;             // iCCP 里的 ICC 描述
        public byte[] Palette;         // PLTE（调色板 PNG 用）

        public static PngInfo Parse(byte[] b)
        {
            if (b == null || b.Length < 8) return null;
            if (!(b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)) return null;
            PngInfo p = new PngInfo();
            int pos = 8;
            MemoryStream idat = new MemoryStream();
            bool sawIhdr = false;
            while (pos + 8 <= b.Length)
            {
                int len = (b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3];
                if (len < 0 || pos + 12 + len > b.Length) break;
                string type = "" + (char)b[pos + 4] + (char)b[pos + 5] + (char)b[pos + 6] + (char)b[pos + 7];
                int data = pos + 8;
                if (type == "IHDR")
                {
                    p.Width = (b[data] << 24) | (b[data + 1] << 16) | (b[data + 2] << 8) | b[data + 3];
                    p.Height = (b[data + 4] << 24) | (b[data + 5] << 16) | (b[data + 6] << 8) | b[data + 7];
                    p.Bpc = b[data + 8];
                    int colorType = b[data + 9];
                    int interlace = b[data + 12];
                    sawIhdr = true;
                    if (interlace != 0) return null;                     // 隔行：交给原路径
                    if (colorType == 0) { p.Comps = 1; }
                    else if (colorType == 2) { p.Comps = 3; }
                    else
                    {
                        // 调色板 / 带 alpha / 灰度+alpha：走原路径（GDI+），只记录调色板备用
                        p.Comps = 0;
                    }
                    if (p.Bpc != 1 && p.Bpc != 2 && p.Bpc != 4 && p.Bpc != 8 && p.Bpc != 16) return null;
                }
                else if (type == "PLTE")
                {
                    p.Palette = new byte[len];
                    Array.Copy(b, data, p.Palette, 0, len);
                }
                else if (type == "IDAT")
                {
                    idat.Write(b, data, len);
                }
                else if (type == "iCCP")
                {
                    // iCCP: 名字\0 压缩方法 压缩后的 ICC
                    int z = data;
                    while (z < data + len && b[z] != 0) z++;
                    z++;
                    if (z < data + len && b[z] == 0) z++;
                    byte[] comp = new byte[data + len - z];
                    Array.Copy(b, z, comp, 0, comp.Length);
                    p.Icc = RawExport.Inflate(comp);
                }
                else if (type == "IEND")
                {
                    break;
                }
                pos = data + len + 4;
            }
            if (!sawIhdr) return null;
            p.Idat = idat.ToArray();
            // 只有"灰度/RGB + 非隔行"才走原样嵌入；16 位/ICC 是主要收益
            p.Lossless = p.Comps != 0 && p.Idat.Length > 0 && p.Width > 0 && p.Height > 0;
            return p;
        }
    }
}
