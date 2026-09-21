using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PdfTool
{
    // 自绘小图标：不依赖外部图片文件，任何 DPI 都清晰
    internal static class Glyphs
    {
        // 打印机图标（上出纸口 / 机身 + 指示灯 / 下托盘），参考系统图标风格
        // 设计基准 16x16，按 size 等比放大
        public static Bitmap Printer(int size, Color color)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 16f, size / 16f);
                using (SolidBrush br = new SolidBrush(color))
                {
                    // 上面那张纸（出纸口）
                    FillRound(g, br, 3.6f, 0.9f, 8.8f, 4.4f, 0.8f);
                    // 机身
                    FillRound(g, br, 0.8f, 4.8f, 14.4f, 6.4f, 1.0f);
                    // 下托盘（U 形）
                    FillRound(g, br, 3.1f, 10.4f, 1.3f, 4.4f, 0.4f);     // 左腿
                    FillRound(g, br, 11.6f, 10.4f, 1.3f, 4.4f, 0.4f);    // 右腿
                    FillRound(g, br, 3.1f, 13.5f, 9.8f, 1.3f, 0.4f);     // 底
                }
                // 指示灯（挖空）
                g.CompositingMode = CompositingMode.SourceCopy;
                using (SolidBrush hole = new SolidBrush(Color.Transparent))
                    g.FillRectangle(hole, 2.3f, 6.3f, 1.5f, 1.5f);
                g.CompositingMode = CompositingMode.SourceOver;
            }
            return bmp;
        }

        // 翻页箭头（16x16 基准）：实心三角，left=true 指左（上一页）
        public static Bitmap Arrow(int size, Color color, bool left)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 16f, size / 16f);
                using (SolidBrush br = new SolidBrush(color))
                {
                    PointF[] pts = left
                        ? new PointF[] { new PointF(10.5f, 3f), new PointF(10.5f, 13f), new PointF(4.5f, 8f) }
                        : new PointF[] { new PointF(5.5f, 3f), new PointF(5.5f, 13f), new PointF(11.5f, 8f) };
                    g.FillPolygon(br, pts);
                }
            }
            return bmp;
        }

        private static void FillRound(Graphics g, Brush br, float x, float y, float w, float h, float r)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                float d = r * 2;
                p.AddArc(x, y, d, d, 180, 90);
                p.AddArc(x + w - d, y, d, d, 270, 90);
                p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                p.AddArc(x, y + h - d, d, d, 90, 90);
                p.CloseFigure();
                g.FillPath(br, p);
            }
        }

        // 供 Form.Icon 用
        public static Icon PrinterIcon(int size, Color color)
        {
            using (Bitmap b = Printer(size, color))
            {
                IntPtr h = b.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { NativeDestroyIcon(h); }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        private static void NativeDestroyIcon(IntPtr h) { try { DestroyIcon(h); } catch { } }
    }
}
