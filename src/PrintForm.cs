using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Windows.Forms;

namespace PdfTool
{
    // 打印窗口：左边设置、右边所见即所得预览（打印按钮下一版接）
    // 单位统一用 1/100 英寸（和打印 API 一致），显示时按纸张比例换算
    internal sealed class PrintForm : Form
    {
        private readonly PdfJob _job;
        private readonly object _lock;
        private readonly int _startPage;   // 主界面当前页（"当前页"范围的默认值）
        private int _index;

        private ComboBox _cbPrinter, _cbColor, _cbPaper, _cbLayout;
        private RadioButton _rbRangeAll, _rbRangeCur, _rbRangeRange;
        private RadioButton _rbOddAll, _rbOdd, _rbEven;
        private TextBox _txtRange;
        private NumericUpDown _numCopies;
        private RadioButton _rbPortrait, _rbLandscape;
        private RadioButton _rbFitMargin, _rbActual, _rbCustom;
        private NumericUpDown _numScale;
        private CheckBox _chkCenter, _chkAutoRotate;

        private PreviewCanvas _canvas;
        private TextBox _txtPage;
        private Label _lblTotal;
        private Label _lblInfo;

        private string _printerName = "";
        private double _paperW = 827, _paperH = 1169;      // A4，1/100 英寸
        private double _marginL = 25, _marginT = 25;       // 打印机硬边距

        private readonly List<int> _pages = new List<int>();      // 筛选后的页序（0 基）
        private readonly List<int[]> _sheets = new List<int[]>(); // 每张纸放哪些页

        public PrintForm(MainForm owner, PdfJob job, object renderLock, int startPage)
        {
            _job = job;
            _lock = renderLock;
            _startPage = startPage;
            Text = "打印";
            Font = owner.Font;
            BackColor = Color.FromArgb(240, 240, 240);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1000, 720);
            MinimumSize = new Size(880, 620);
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Left) { Go(_index - 1); e.Handled = true; }
                else if (e.KeyCode == Keys.Right) { Go(_index + 1); e.Handled = true; }
            };

            BuildUi();
            LoadPrinters();
            BuildPaperList();
            UpdateLayoutFromPaper();
            BuildSheetList();
            Go(0);
        }

        // ---------------- 界面 ----------------

        private GroupBox Group(string title, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = title;
            g.SetBounds(x, y, w, h);
            g.BackColor = Color.FromArgb(248, 248, 248);
            return g;
        }

        private static Label Lbl(string t, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = t;
            l.SetBounds(x, y, w, 20);
            l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }

        private static Button NavBtn(string text, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = w;
            b.Height = 30;
            b.Margin = new Padding(0, 0, 4, 0);
            return b;
        }

        private void BuildUi()
        {
            Panel left = new Panel();
            left.Dock = DockStyle.Left;
            left.Width = 320;
            left.AutoScroll = true;
            left.BackColor = Color.FromArgb(240, 240, 240);
            left.Padding = new Padding(10, 8, 6, 8);
            Controls.Add(left);

            int y = 6;

            GroupBox g1 = Group("打印机选择", 6, y, 296, 62); y += 68;
            _cbPrinter = new ComboBox();
            _cbPrinter.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbPrinter.SetBounds(12, 24, 272, 24);
            _cbPrinter.SelectedIndexChanged += delegate { OnPrinterChanged(); };
            g1.Controls.Add(_cbPrinter);
            left.Controls.Add(g1);

            GroupBox g2 = Group("份数 / 颜色", 6, y, 296, 58); y += 64;
            g2.Controls.Add(Lbl("份数", 12, 22, 34));
            _numCopies = new NumericUpDown();
            _numCopies.Minimum = 1; _numCopies.Maximum = 999; _numCopies.Value = 1;
            _numCopies.SetBounds(48, 20, 60, 24);
            g2.Controls.Add(_numCopies);
            g2.Controls.Add(Lbl("颜色", 124, 22, 34));
            _cbColor = new ComboBox();
            _cbColor.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbColor.Items.AddRange(new object[] { "彩色打印", "黑白打印" });
            _cbColor.SelectedIndex = 0;
            _cbColor.SetBounds(162, 20, 122, 24);
            _cbColor.SelectedIndexChanged += delegate { if (_canvas != null) _canvas.Invalidate(); };
            g2.Controls.Add(_cbColor);
            left.Controls.Add(g2);

            GroupBox g3 = Group("页面内容和范围选择", 6, y, 296, 92); y += 98;
            _rbRangeAll = new RadioButton();
            _rbRangeAll.Text = "所有页面"; _rbRangeAll.SetBounds(12, 20, 88, 22);
            _rbRangeAll.CheckedChanged += delegate { if (_rbRangeAll.Checked) { SyncRangeUi(); BuildSheetList(); } };
            g3.Controls.Add(_rbRangeAll);
            _rbRangeCur = new RadioButton();
            _rbRangeCur.Text = "当前页"; _rbRangeCur.Checked = true;      // 默认当前页
            _rbRangeCur.SetBounds(104, 20, 76, 22);
            _rbRangeCur.CheckedChanged += delegate { if (_rbRangeCur.Checked) { SyncRangeUi(); BuildSheetList(); } };
            g3.Controls.Add(_rbRangeCur);
            _rbRangeRange = new RadioButton();
            _rbRangeRange.Text = "页码范围";
            _rbRangeRange.SetBounds(184, 20, 90, 22);
            _rbRangeRange.CheckedChanged += delegate { if (_rbRangeRange.Checked) { SyncRangeUi(); BuildSheetList(); } };
            g3.Controls.Add(_rbRangeRange);
            _txtRange = new TextBox();
            _txtRange.SetBounds(12, 50, 130, 24);
            _txtRange.Text = "1-" + Math.Max(1, _job.PageCount);
            _txtRange.Enabled = false;
            _txtRange.TextChanged += delegate { BuildSheetList(); };
            g3.Controls.Add(_txtRange);
            g3.Controls.Add(Lbl("如 1-5,8,10-12", 148, 52, 140));
            left.Controls.Add(g3);

            GroupBox g4 = Group("奇偶页面", 6, y, 296, 58); y += 64;
            _rbOddAll = new RadioButton();
            _rbOddAll.Text = "所有页面"; _rbOddAll.Checked = true;
            _rbOddAll.SetBounds(12, 20, 88, 22);
            _rbOddAll.CheckedChanged += delegate { if (_rbOddAll.Checked) BuildSheetList(); };
            g4.Controls.Add(_rbOddAll);
            _rbOdd = new RadioButton();
            _rbOdd.Text = "仅奇数页"; _rbOdd.SetBounds(104, 20, 80, 22);
            _rbOdd.CheckedChanged += delegate { if (_rbOdd.Checked) BuildSheetList(); };
            g4.Controls.Add(_rbOdd);
            _rbEven = new RadioButton();
            _rbEven.Text = "仅偶数页"; _rbEven.SetBounds(188, 20, 80, 22);
            _rbEven.CheckedChanged += delegate { if (_rbEven.Checked) BuildSheetList(); };
            g4.Controls.Add(_rbEven);
            left.Controls.Add(g4);

            GroupBox g5 = Group("纸张大小和方向", 6, y, 296, 86); y += 92;
            _cbPaper = new ComboBox();
            _cbPaper.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbPaper.SetBounds(12, 20, 272, 24);
            _cbPaper.SelectedIndexChanged += delegate { UpdateLayoutFromPaper(); BuildSheetList(); };
            g5.Controls.Add(_cbPaper);
            _rbPortrait = new RadioButton();
            _rbPortrait.Text = "纵向"; _rbPortrait.Checked = true;
            _rbPortrait.SetBounds(40, 52, 70, 22);
            _rbPortrait.CheckedChanged += delegate { if (_rbPortrait.Checked) { UpdateLayoutFromPaper(); BuildSheetList(); } };
            g5.Controls.Add(_rbPortrait);
            _rbLandscape = new RadioButton();
            _rbLandscape.Text = "横向";
            _rbLandscape.SetBounds(170, 52, 70, 22);
            _rbLandscape.CheckedChanged += delegate { if (_rbLandscape.Checked) { UpdateLayoutFromPaper(); BuildSheetList(); } };
            g5.Controls.Add(_rbLandscape);
            left.Controls.Add(g5);

            GroupBox g6 = Group("打印方式 / 缩放", 6, y, 296, 128); y += 134;
            g6.Controls.Add(Lbl("打印方式", 12, 20, 60));
            _cbLayout = new ComboBox();
            _cbLayout.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbLayout.Items.AddRange(new object[] { "页面大小", "一张两页", "一张四页" });
            _cbLayout.SelectedIndex = 0;
            _cbLayout.SetBounds(76, 18, 208, 24);
            _cbLayout.SelectedIndexChanged += delegate { BuildSheetList(); };
            g6.Controls.Add(_cbLayout);
            _rbFitMargin = new RadioButton();
            _rbFitMargin.Text = "适合打印边距"; _rbFitMargin.Checked = true;
            _rbFitMargin.SetBounds(12, 50, 130, 22);
            _rbFitMargin.CheckedChanged += delegate { SyncScaleUi(); if (_canvas != null) _canvas.Invalidate(); };
            g6.Controls.Add(_rbFitMargin);
            _rbActual = new RadioButton();
            _rbActual.Text = "实际大小"; _rbActual.SetBounds(12, 74, 100, 22);
            _rbActual.CheckedChanged += delegate { SyncScaleUi(); if (_canvas != null) _canvas.Invalidate(); };
            g6.Controls.Add(_rbActual);
            _rbCustom = new RadioButton();
            _rbCustom.Text = "自定义比例"; _rbCustom.SetBounds(12, 98, 100, 22);
            _rbCustom.CheckedChanged += delegate { SyncScaleUi(); if (_canvas != null) _canvas.Invalidate(); };
            g6.Controls.Add(_rbCustom);
            _numScale = new NumericUpDown();
            _numScale.Minimum = 10; _numScale.Maximum = 400; _numScale.Value = 100;
            _numScale.SetBounds(116, 97, 60, 24);
            _numScale.ValueChanged += delegate { if (_canvas != null) _canvas.Invalidate(); };
            g6.Controls.Add(_numScale);
            g6.Controls.Add(Lbl("%", 180, 99, 20));
            left.Controls.Add(g6);

            GroupBox g7 = Group("页面设置", 6, y, 296, 58); y += 64;
            _chkCenter = new CheckBox();
            _chkCenter.Text = "自动居中"; _chkCenter.Checked = true;
            _chkCenter.SetBounds(12, 20, 100, 22);
            _chkCenter.CheckedChanged += delegate { if (_canvas != null) _canvas.Invalidate(); };
            g7.Controls.Add(_chkCenter);
            _chkAutoRotate = new CheckBox();
            _chkAutoRotate.Text = "自动旋转"; _chkAutoRotate.SetBounds(140, 20, 100, 22);
            _chkAutoRotate.CheckedChanged += delegate { if (_canvas != null) _canvas.Invalidate(); };
            g7.Controls.Add(_chkAutoRotate);
            left.Controls.Add(g7);

            Panel right = new Panel();
            right.Dock = DockStyle.Fill;
            right.BackColor = Color.FromArgb(240, 240, 240);
            Controls.Add(right);
            right.BringToFront();

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 46;
            bottom.BackColor = Color.FromArgb(240, 240, 240);
            right.Controls.Add(bottom);

            _lblInfo = new Label();
            _lblInfo.SetBounds(12, 13, 260, 22);
            _lblInfo.ForeColor = Color.FromArgb(90, 90, 90);
            bottom.Controls.Add(_lblInfo);

            // 翻页控件放进自动排布的面板（靠右），窗口再小也不会被挤掉
            FlowLayoutPanel nav = new FlowLayoutPanel();
            nav.Dock = DockStyle.Right;
            nav.AutoSize = true;
            nav.WrapContents = false;
            nav.Padding = new Padding(0, 8, 8, 0);
            nav.BackColor = Color.FromArgb(240, 240, 240);
            bottom.Controls.Add(nav);

            Button bFirst = NavBtn("首页", 60);
            bFirst.Click += delegate { Go(0); };
            nav.Controls.Add(bFirst);
            Button bPrev = NavBtn("上一页", 68);
            bPrev.Click += delegate { Go(_index - 1); };
            nav.Controls.Add(bPrev);

            _txtPage = new TextBox();
            _txtPage.Width = 52;
            _txtPage.Margin = new Padding(6, 10, 4, 0);
            _txtPage.TextAlign = HorizontalAlignment.Center;
            _txtPage.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    int n;
                    if (int.TryParse(_txtPage.Text.Trim(), out n)) Go(n - 1);
                    else UpdatePageBox();
                }
            };
            nav.Controls.Add(_txtPage);

            _lblTotal = new Label();
            _lblTotal.AutoSize = true;
            _lblTotal.Margin = new Padding(0, 14, 8, 0);
            nav.Controls.Add(_lblTotal);

            Button bNext = NavBtn("下一页", 68);
            bNext.Click += delegate { Go(_index + 1); };
            nav.Controls.Add(bNext);
            Button bLast = NavBtn("尾页", 60);
            bLast.Click += delegate { Go(_sheets.Count - 1); };
            nav.Controls.Add(bLast);

            Button btnClose = new Button();
            btnClose.Text = "关闭";
            btnClose.Width = 80;
            btnClose.Height = 30;
            btnClose.Margin = new Padding(10, 0, 0, 0);
            btnClose.Click += delegate { Close(); };
            nav.Controls.Add(btnClose);

            _canvas = new PreviewCanvas();
            _canvas.Dock = DockStyle.Fill;
            _canvas.BackColor = Color.FromArgb(200, 200, 200);
            _canvas.Paint += CanvasPaint;
            right.Controls.Add(_canvas);
            _canvas.BringToFront();
            bottom.BringToFront();

            SyncRangeUi();
            SyncScaleUi();
        }

        // ---------------- 打印机 / 纸张 ----------------

        private void LoadPrinters()
        {
            try
            {
                foreach (string n in PrinterSettings.InstalledPrinters) _cbPrinter.Items.Add(n);
                PrinterSettings ps = new PrinterSettings();
                string def = ps.PrinterName;
                if (_cbPrinter.Items.Count > 0)
                {
                    int i = 0;
                    for (int k = 0; k < _cbPrinter.Items.Count; k++)
                        if ((string)_cbPrinter.Items[k] == def) { i = k; break; }
                    _cbPrinter.SelectedIndex = i;
                    _printerName = (string)_cbPrinter.Items[i];
                }
            }
            catch { }
            if (_cbPrinter.Items.Count == 0)
            {
                _cbPrinter.Items.Add("（没有检测到打印机，仅预览）");
                _cbPrinter.SelectedIndex = 0;
            }
        }

        private void BuildPaperList()
        {
            _cbPaper.Items.Clear();
            List<PaperItem> sizes = new List<PaperItem>();
            try
            {
                if (_printerName.Length > 0)
                {
                    PrinterSettings ps = new PrinterSettings();
                    ps.PrinterName = _printerName;
                    foreach (PaperSize s in ps.PaperSizes)
                    {
                        if (s.Width < 100 || s.Height < 100) continue;   // 驱动里的怪尺寸跳过
                        sizes.Add(new PaperItem(s));
                    }
                }
            }
            catch { }
            if (sizes.Count == 0)
            {
                sizes.Add(new PaperItem(new PaperSize("A4", 827, 1169)));
                sizes.Add(new PaperItem(new PaperSize("A3", 1169, 1654)));
                sizes.Add(new PaperItem(new PaperSize("A5", 583, 827)));
                sizes.Add(new PaperItem(new PaperSize("Letter", 850, 1100)));
            }
            foreach (PaperItem s in sizes) _cbPaper.Items.Add(s);
            int pick = 0;
            for (int i = 0; i < _cbPaper.Items.Count; i++)
            {
                PaperItem it = (PaperItem)_cbPaper.Items[i];
                if (it.Size.PaperName == "A4" || it.Size.Kind == PaperKind.A4) { pick = i; break; }
            }
            _cbPaper.SelectedIndex = pick;
        }

        private void OnPrinterChanged()
        {
            if (_cbPrinter.SelectedItem != null) _printerName = (string)_cbPrinter.SelectedItem;
            BuildPaperList();
            UpdateLayoutFromPaper();
            BuildSheetList();
        }

        private void UpdateLayoutFromPaper()
        {
            PaperItem pi = _cbPaper.SelectedItem as PaperItem;
            double w = pi != null ? pi.Size.Width : 827;
            double h = pi != null ? pi.Size.Height : 1169;
            if (w < 100) w = 827;
            if (h < 100) h = 1169;
            if (_rbLandscape.Checked) { double t = w; w = h; h = t; }
            _paperW = w; _paperH = h;

            _marginL = 25; _marginT = 25;
            try
            {
                if (_printerName.Length > 0)
                {
                    PrinterSettings p = new PrinterSettings();
                    p.PrinterName = _printerName;
                    RectangleF a = p.DefaultPageSettings.PrintableArea;
                    if (a.X > 0) _marginL = a.X;
                    if (a.Y > 0) _marginT = a.Y;
                }
            }
            catch { }
        }

        private void SyncRangeUi()
        {
            _txtRange.Enabled = _rbRangeRange.Checked;
        }

        private void SyncScaleUi()
        {
            _numScale.Enabled = _rbCustom.Checked;
        }

        // ---------------- 页 / 张 ----------------

        private void BuildSheetList()
        {
            _pages.Clear();
            int total = _job.PageCount;
            int from = 0, to = total - 1;
            if (_rbRangeCur.Checked) from = to = Math.Min(Math.Max(_startPage, 0), total - 1);
            else if (_rbRangeRange.Checked)
            {
                from = -1; to = -1;
                foreach (string part in _txtRange.Text.Split(','))
                {
                    string p = part.Trim();
                    if (p.Length == 0) continue;
                    int dash = p.IndexOf('-');
                    int a, b;
                    if (dash > 0 && int.TryParse(p.Substring(0, dash), out a) && int.TryParse(p.Substring(dash + 1), out b)) { }
                    else if (int.TryParse(p, out a)) b = a;
                    else continue;
                    if (a < 1) a = 1;
                    if (b > total) b = total;
                    if (a > b) continue;
                    if (from < 0 || a - 1 < from) from = a - 1;
                    if (b - 1 > to) to = b - 1;
                }
                if (from < 0) { from = 0; to = -1; }
            }
            for (int i = from; i <= to && i < total; i++)
            {
                if (i < 0) continue;
                bool odd = ((i + 1) % 2) == 1;
                if (_rbOdd.Checked && !odd) continue;
                if (_rbEven.Checked && odd) continue;
                _pages.Add(i);
            }
            _sheets.Clear();
            int per = _cbLayout.SelectedIndex == 0 ? 1 : (_cbLayout.SelectedIndex == 1 ? 2 : 4);
            for (int i = 0; i < _pages.Count; i += per)
            {
                int n = Math.Min(per, _pages.Count - i);
                int[] arr = new int[n];
                for (int k = 0; k < n; k++) arr[k] = _pages[i + k];
                _sheets.Add(arr);
            }
            if (_index >= _sheets.Count) _index = Math.Max(0, _sheets.Count - 1);
            UpdatePageBox();
            if (_canvas != null) _canvas.Invalidate();
        }

        private void Go(int sheet)
        {
            if (_sheets.Count == 0) { _index = 0; UpdatePageBox(); if (_canvas != null) _canvas.Invalidate(); return; }
            if (sheet < 0) sheet = 0;
            if (sheet > _sheets.Count - 1) sheet = _sheets.Count - 1;
            _index = sheet;
            UpdatePageBox();
            if (_canvas != null) _canvas.Invalidate();
        }

        private void UpdatePageBox()
        {
            _txtPage.Text = (_sheets.Count == 0 ? 0 : _index + 1).ToString();
            _lblTotal.Text = "/ " + _sheets.Count;
            _lblInfo.Text = "预览：第 " + (_sheets.Count == 0 ? 0 : _index + 1) + " 张 / 共 " + _sheets.Count + " 张"
                + (_pages.Count != _job.PageCount ? "（已筛选）" : "");
        }

        // ---------------- 绘制 ----------------

        // 当前张上每个内容的落点（1/100 英寸，相对纸左上角）
        private List<RectangleF> ContentRects()
        {
            List<RectangleF> list = new List<RectangleF>();
            if (_sheets.Count == 0) return list;
            int[] sheet = _sheets[_index];
            double pw = _paperW - _marginL * 2, ph = _paperH - _marginT * 2;
            if (pw < 10) pw = _paperW - 50;
            if (ph < 10) ph = _paperH - 50;
            int cols = sheet.Length == 1 ? 1 : (sheet.Length == 2 ? (pw >= ph ? 2 : 1) : 2);
            int rows = (int)Math.Ceiling(sheet.Length / (double)cols);
            if (rows < 1) rows = 1;
            double cellW = pw / cols, cellH = ph / rows;
            for (int i = 0; i < sheet.Length; i++)
            {
                int r = i / cols, c = i % cols;
                double x0 = _marginL + c * cellW, y0 = _marginT + r * cellH;

                double srcW, srcH;
                lock (_lock) { _job.PageSizeIn100(sheet[i], out srcW, out srcH); }
                if (srcW < 1 || srcH < 1) { srcW = 595; srcH = 842; }   // 兜底 A4

                double sc;
                if (_rbActual.Checked) sc = 100.0 / 72.0;                       // 实际大小（1:1）
                else if (_rbCustom.Checked) sc = (double)_numScale.Value / 100.0 * (100.0 / 72.0);
                else sc = Math.Min(cellW / srcW, cellH / srcH);                  // 适合打印边距
                if (sc <= 0 || double.IsNaN(sc) || double.IsInfinity(sc)) sc = 1;

                double w = srcW * sc, h = srcH * sc;
                bool rotate = _chkAutoRotate.Checked && (w > cellW || h > cellH) && (h <= cellW && w <= cellH);
                if (rotate) { double t = w; w = h; h = t; }
                double x = x0, y = y0;
                if (_chkCenter.Checked)
                {
                    x = x0 + (cellW - w) / 2;
                    y = y0 + (cellH - h) / 2;
                }
                list.Add(new RectangleF((float)x, (float)y, (float)w, (float)h));
            }
            return list;
        }

        private void CanvasPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_canvas.BackColor);
            if (_sheets.Count == 0)
            {
                g.DrawString("没有可预览的页面", Font, Brushes.DimGray, 20, 20);
                return;
            }
            int cw = _canvas.ClientSize.Width - 32, ch = _canvas.ClientSize.Height - 32;
            if (cw < 40 || ch < 40) return;
            float s = Math.Min((float)(cw / _paperW), (float)(ch / _paperH));
            if (s <= 0) return;
            float pw = (float)(_paperW * s), ph = (float)(_paperH * s);
            float px = 16 + (cw - pw) / 2, py = 16 + (ch - ph) / 2;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
                g.FillRectangle(shadow, px + 3, py + 3, pw, ph);
            g.FillRectangle(Brushes.White, px, py, pw, ph);
            g.DrawRectangle(Pens.Gray, px, py, pw, ph);

            float mx = (float)(_marginL * s), my = (float)(_marginT * s);
            using (Pen dash = new Pen(Color.FromArgb(214, 214, 214)))
            {
                dash.DashStyle = DashStyle.Dot;
                g.DrawRectangle(dash, px + mx, py + my, pw - mx * 2, ph - my * 2);
            }

            int[] sheet = _sheets[_index];
            List<RectangleF> rects = ContentRects();
            bool gray = _cbColor.SelectedIndex == 1;
            for (int i = 0; i < sheet.Length && i < rects.Count; i++)
            {
                RectangleF r = rects[i];
                float dx = px + r.X * s, dy = py + r.Y * s, dw = r.Width * s, dh = r.Height * s;
                if (dw < 2 || dh < 2) continue;
                Bitmap bmp = null;
                try
                {
                    lock (_lock) { bmp = _job.RenderPage(sheet[i], (int)Math.Round(dw), (int)Math.Round(dh), false); }
                    if (gray) bmp = ToGray(bmp);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(bmp, dx, dy, dw, dh);
                    g.DrawRectangle(Pens.Silver, dx, dy, dw, dh);
                }
                catch { }
                finally { if (bmp != null) bmp.Dispose(); }
            }
        }

        private static Bitmap ToGray(Bitmap src)
        {
            Bitmap dst = new Bitmap(src.Width, src.Height);
            using (Graphics g = Graphics.FromImage(dst))
            using (ImageAttributes ia = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix(new float[][]
                {
                    new float[] { 0.299f, 0.299f, 0.299f, 0, 0 },
                    new float[] { 0.587f, 0.587f, 0.587f, 0, 0 },
                    new float[] { 0.114f, 0.114f, 0.114f, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 },
                    new float[] { 0, 0, 0, 0, 1 }
                });
                ia.SetColorMatrix(cm);
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            src.Dispose();
            return dst;
        }

        // 下拉里显示 "A4  (210 x 297 mm)" 这种
        private sealed class PaperItem
        {
            public readonly PaperSize Size;
            public PaperItem(PaperSize s) { Size = s; }
            public override string ToString()
            {
                string name = Size.PaperName;
                if (string.IsNullOrEmpty(name)) name = "自定义";
                int wmm = (int)Math.Round(Size.Width / 100.0 * 25.4);
                int hmm = (int)Math.Round(Size.Height / 100.0 * 25.4);
                return name + "  (" + wmm + " x " + hmm + " mm)";
            }
        }

        // 自己画，不闪
        private sealed class PreviewCanvas : Panel
        {
            public PreviewCanvas() { DoubleBuffered = true; ResizeRedraw = true; }
        }
    }
}
