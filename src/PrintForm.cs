using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
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
        private CheckBox _chkDuplex;
        private RadioButton _rbDuplexLong, _rbDuplexShort;
        private Label _lblDuplexHint;
        private bool _canDuplex;

        private PreviewCanvas _canvas;
        private TextBox _txtPage;
        private Label _lblTotal;
        private Label _lblInfo;

        private string _printerName = "";
        private bool _refreshing;
        private bool _syncingPrinter;
        private double _paperW = 827, _paperH = 1169;      // A4，1/100 英寸
        private double _marginL = 25, _marginT = 25;       // 打印机硬边距（后台查到后覆盖）

        private readonly List<int> _pages = new List<int>();      // 筛选后的页序（0 基）
        private readonly List<int[]> _sheets = new List<int[]>(); // 每张纸放哪些页

        public PrintForm(MainForm owner, PdfJob job, object renderLock, int startPage)
        {
            _job = job;
            _lock = renderLock;
            _startPage = startPage;
            Text = "打印";
            try { Icon = Glyphs.PrinterIcon(32, Color.FromArgb(96, 96, 96)); } catch { }
            Font = owner.Font;
            BackColor = Color.FromArgb(240, 240, 240);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(930, 690);
            Height = 732;                      // 默认外框高度（用户按屏幕工作区定的）
            MinimumSize = new Size(860, 600);
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Left) { Go(_index - 1); e.Handled = true; }
                else if (e.KeyCode == Keys.Right) { Go(_index + 1); e.Handled = true; }
            };

            BuildUi();
            UseFallbackPaper();
            BuildSheetList();
            Go(0);
            RefreshPrinterInfo();    // 打印机/纸张信息后台查（离线打印机查询会卡好几秒）
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
            left.Width = 340;
            left.AutoScroll = true;
            left.BackColor = Color.FromArgb(240, 240, 240);
            left.Padding = new Padding(10, 8, 6, 8);
            Controls.Add(left);

            // 左下角按钮条（打印 / 取消）—— Dock=Bottom，不会被 AutoScroll 卷走
            Panel leftBottom = new Panel();
            leftBottom.Dock = DockStyle.Bottom;
            leftBottom.Height = 54;
            leftBottom.BackColor = Color.FromArgb(240, 240, 240);
            left.Controls.Add(leftBottom);

            Button btnPrint = new Button();
            btnPrint.Text = "打印";
            btnPrint.SetBounds(14, 10, 96, 34);
            btnPrint.FlatStyle = FlatStyle.Flat;
            btnPrint.FlatAppearance.BorderSize = 0;
            btnPrint.BackColor = Color.FromArgb(219, 68, 68);   // 整块红
            btnPrint.ForeColor = Color.White;
            btnPrint.Click += delegate { DoPrint(); };
            leftBottom.Controls.Add(btnPrint);

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.SetBounds(122, 10, 96, 34);
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.FlatAppearance.BorderSize = 1;
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(160, 160, 160);
            btnCancel.BackColor = Color.White;
            btnCancel.Click += delegate { Close(); };
            leftBottom.Controls.Add(btnCancel);

            int y = 6;

            GroupBox g1 = Group("打印机选择", 6, y, 316, 62); y += 68;
            _cbPrinter = new ComboBox();
            _cbPrinter.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbPrinter.SetBounds(12, 24, 208, 24);
            Button btnProp = new Button();
            btnProp.Text = "属性";
            btnProp.SetBounds(226, 24, 78, 24);
            btnProp.Click += delegate { ShowPrinterProperties(); };
            g1.Controls.Add(btnProp);
            _cbPrinter.SelectedIndexChanged += delegate { OnPrinterChanged(); };
            g1.Controls.Add(_cbPrinter);
            left.Controls.Add(g1);

            GroupBox g2 = Group("份数 / 颜色", 6, y, 316, 58); y += 64;
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
            _cbColor.SetBounds(162, 20, 142, 24);
            _cbColor.SelectedIndexChanged += delegate { if (_canvas != null) _canvas.Invalidate(); };
            g2.Controls.Add(_cbColor);
            left.Controls.Add(g2);

            GroupBox g3 = Group("页面内容和范围选择", 6, y, 316, 92); y += 98;
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

            GroupBox g4 = Group("奇偶页面", 6, y, 316, 58); y += 64;
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

            GroupBox g5 = Group("纸张大小和方向", 6, y, 316, 86); y += 92;
            _cbPaper = new ComboBox();
            _cbPaper.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbPaper.SetBounds(12, 20, 292, 24);
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

            GroupBox g6 = Group("打印方式 / 缩放", 6, y, 316, 128); y += 134;
            g6.Controls.Add(Lbl("打印方式", 12, 20, 60));
            _cbLayout = new ComboBox();
            _cbLayout.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbLayout.Items.AddRange(new object[] { "页面大小", "一张两页", "一张四页" });
            _cbLayout.SelectedIndex = 0;
            _cbLayout.SetBounds(76, 18, 228, 24);
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

            GroupBox g8 = Group("双面打印", 6, y, 316, 74); y += 80;
            _chkDuplex = new CheckBox();
            _chkDuplex.Text = "自动双面打印";
            _chkDuplex.SetBounds(12, 22, 116, 22);
            _chkDuplex.CheckedChanged += delegate { SyncDuplexUi(); };
            g8.Controls.Add(_chkDuplex);
            _rbDuplexLong = new RadioButton();
            _rbDuplexLong.Text = "长边翻转"; _rbDuplexLong.Checked = true;
            _rbDuplexLong.SetBounds(134, 22, 90, 22);
            g8.Controls.Add(_rbDuplexLong);
            _rbDuplexShort = new RadioButton();
            _rbDuplexShort.Text = "短边翻转";
            _rbDuplexShort.SetBounds(228, 22, 86, 22);
            g8.Controls.Add(_rbDuplexShort);
            _lblDuplexHint = Lbl("", 12, 48, 292);
            _lblDuplexHint.ForeColor = Color.FromArgb(190, 90, 60);
            g8.Controls.Add(_lblDuplexHint);
            left.Controls.Add(g8);

            GroupBox g7 = Group("页面设置", 6, y, 316, 58); y += 64;
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

            TableLayoutPanel right = new TableLayoutPanel();
            right.Dock = DockStyle.Fill;
            right.BackColor = Color.FromArgb(240, 240, 240);
            right.ColumnCount = 1;
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            right.RowCount = 2;
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 预览画布
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));   // 底栏
            Controls.Add(right);
            // 关键：Fill 的右区必须排到集合最前（停靠从后往前处理），否则它会占满整窗、
            // 纸就会按"整窗宽度"居中而看起来偏左（上面被左侧设置面板压住）
            Controls.SetChildIndex(right, 0);

            // 两列布局：左=提示信息（太长省略号），右=翻页按钮组（整块保证不被压）
            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.Height = 46;
            bottom.BackColor = Color.FromArgb(240, 240, 240);
            bottom.ColumnCount = 2;
            bottom.RowCount = 1;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            right.Controls.Add(bottom, 0, 1);

            _lblInfo = new Label();
            _lblInfo.Dock = DockStyle.Fill;
            _lblInfo.AutoEllipsis = false;     // AutoEllipsis 会把文字画到顶部，跟按钮不在一条中线
            _lblInfo.Margin = new Padding(0);
            _lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            _lblInfo.Padding = new Padding(12, 0, 0, 0);
            _lblInfo.ForeColor = Color.FromArgb(90, 90, 90);
            bottom.Controls.Add(_lblInfo);

            FlowLayoutPanel nav = new FlowLayoutPanel();
            nav.Anchor = AnchorStyles.None;      // 在单元格里垂直居中（原来是 Top，按钮就顶在上面）
            nav.AutoSize = true;
            nav.WrapContents = false;
            nav.Padding = new Padding(0, 0, 8, 0);
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
            _txtPage.Height = 26;
            _txtPage.Margin = new Padding(6, 2, 4, 0);   // 和按钮同一中心线
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
            _lblTotal.Margin = new Padding(0, 8, 8, 0);   // 同上，跟按钮平行
            nav.Controls.Add(_lblTotal);

            Button bNext = NavBtn("下一页", 68);
            bNext.Click += delegate { Go(_index + 1); };
            nav.Controls.Add(bNext);
            Button bLast = NavBtn("尾页", 60);
            bLast.Click += delegate { Go(_sheets.Count - 1); };
            nav.Controls.Add(bLast);

            // 停靠顺序很重要：Fill 的控件必须**最后**加，否则它占满整块、边缘面板只能压在上面
            // （之前这里调了 BringToFront，把顺序打乱，预览画布被底栏压住 46px —— 纸的底边就是这么丢的）
            _canvas = new PreviewCanvas();
            _canvas.Dock = DockStyle.Fill;
            _canvas.BackColor = Color.FromArgb(200, 200, 200);
            _canvas.Paint += CanvasPaint;
            right.Controls.Remove(_canvas);
            right.Controls.Add(_canvas, 0, 0);

            SyncRangeUi();
            SyncScaleUi();
            SyncDuplexUi();
        }

        // ---------------- 打印机 / 纸张（全部在后台线程查） ----------------

        private void UseFallbackPaper()
        {
            _cbPaper.Items.Clear();
            _cbPaper.Items.Add(new PaperItem(new PaperSize("A4", 827, 1169)));
            _cbPaper.Items.Add(new PaperItem(new PaperSize("A3", 1169, 1654)));
            _cbPaper.Items.Add(new PaperItem(new PaperSize("A5", 583, 827)));
            _cbPaper.Items.Add(new PaperItem(new PaperSize("Letter", 850, 1100)));
            _cbPaper.SelectedIndex = 0;
        }

        // 打印机列表 / 该打印机的纸张 / 硬边距：都在后台线程查，查完回 UI 线程填
        private void RefreshPrinterInfo()
        {
            if (_refreshing) return;
            _refreshing = true;
            string want = _printerName;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object st)
            {
                List<string> printers = new List<string>();
                List<PaperItem> papers = new List<PaperItem>();
                double ml = 25, mt = 25;
                bool canDuplex = false;
                string use = want;
                try { foreach (string s in PrinterSettings.InstalledPrinters) printers.Add(s); } catch { }
                try
                {
                    PrinterSettings ps = new PrinterSettings();
                    if (use.Length == 0) use = ps.PrinterName;
                    if (use.Length > 0)
                    {
                        ps.PrinterName = use;
                        try
                        {
                            foreach (PaperSize s in ps.PaperSizes)
                                if (s.Width >= 100 && s.Height >= 100) papers.Add(new PaperItem(s));
                        }
                        catch { }
                        try
                        {
                            RectangleF a = ps.DefaultPageSettings.PrintableArea;
                            if (a.X > 0) ml = a.X;
                            if (a.Y > 0) mt = a.Y;
                            try { canDuplex = ps.CanDuplex; } catch { }
                        }
                        catch { }
                    }
                }
                catch { }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _refreshing = false;
                        _canDuplex = canDuplex;
                        ApplyPrinterInfo(printers, papers, use, ml, mt);
                        SyncDuplexUi();
                    });
                }
                catch { _refreshing = false; }
            });
        }

        private void ApplyPrinterInfo(List<string> printers, List<PaperItem> papers, string use, double ml, double mt)
        {
            if (printers.Count == 0) printers.Add("（没有检测到打印机，仅预览）");
            _cbPrinter.BeginUpdate();
            _cbPrinter.Items.Clear();
            foreach (string s in printers) _cbPrinter.Items.Add(s);
            _cbPrinter.EndUpdate();
            int pick = 0;
            for (int i = 0; i < _cbPrinter.Items.Count; i++)
                if ((string)_cbPrinter.Items[i] == use) { pick = i; break; }
            _syncingPrinter = true;
            _cbPrinter.SelectedIndex = pick;
            _syncingPrinter = false;
            _printerName = use.Length > 0 ? use : (string)_cbPrinter.Items[0];
            _marginL = ml; _marginT = mt;

            if (papers.Count > 0)
            {
                PaperSize keep = null;
                PaperItem pi = _cbPaper.SelectedItem as PaperItem;
                if (pi != null) keep = pi.Size;
                _cbPaper.BeginUpdate();
                _cbPaper.Items.Clear();
                foreach (PaperItem s in papers) _cbPaper.Items.Add(s);
                _cbPaper.EndUpdate();
                int p2 = 0;
                if (keep != null)
                    for (int i = 0; i < _cbPaper.Items.Count; i++)
                        if (((PaperItem)_cbPaper.Items[i]).Size.PaperName == keep.PaperName) { p2 = i; break; }
                _cbPaper.SelectedIndex = p2;
            }
            UpdateLayoutFromPaper();
            BuildSheetList();
        }

        private void OnPrinterChanged()
        {
            if (_syncingPrinter) return;
            if (_cbPrinter.SelectedItem != null) _printerName = (string)_cbPrinter.SelectedItem;
            UpdateLayoutFromPaper();   // 先用缓存边距
            BuildSheetList();
            RefreshPrinterInfo();      // 再后台拿这台打印机的纸张/边距
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
            // 硬边距由后台 RefreshPrinterInfo() 查好放 _marginL/_marginT，这里不碰打印机
        }

        // ★双面打印：当前只保存参数（等接了真打印按钮，用 DuplexSetting 设 PrinterSettings.Duplex）
        internal Duplex DuplexSetting
        {
            get
            {
                if (_chkDuplex == null || !_chkDuplex.Checked) return Duplex.Simplex;
                return _rbDuplexShort.Checked ? Duplex.Horizontal : Duplex.Vertical;
            }
        }

        private void SyncDuplexUi()
        {
            bool on = _chkDuplex.Checked;
            _rbDuplexLong.Enabled = on;
            _rbDuplexShort.Enabled = on;
            if (on && !_canDuplex) _lblDuplexHint.Text = "当前打印机不支持自动双面（先记着设置）";
            else if (on) _lblDuplexHint.Text = "";
            else _lblDuplexHint.Text = "不勾选 = 单面打印";
        }

        // ---- 打印机属性（调用打印驱动的配置对话框）----

        [System.Runtime.InteropServices.DllImport("winspool.drv", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool OpenPrinter(string name, out IntPtr h, IntPtr def);

        [System.Runtime.InteropServices.DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr h);

        [System.Runtime.InteropServices.DllImport("winspool.drv", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int DocumentProperties(IntPtr hwnd, IntPtr hPrinter, string device, IntPtr outBuf, IntPtr inBuf, int mode);

        private void ShowPrinterProperties()
        {
            if (_printerName == null || _printerName.Length == 0) return;
            IntPtr hp;
            if (!OpenPrinter(_printerName, out hp, IntPtr.Zero))
            {
                MessageBox.Show(this, "打不开这台打印机（可能离线）。\r\n可以在 Windows 的「设备和打印机」里改它的属性。",
                    "属性", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                int need = DocumentProperties(IntPtr.Zero, hp, _printerName, IntPtr.Zero, IntPtr.Zero, 0);
                if (need <= 0)
                {
                    MessageBox.Show(this, "读不到这台打印机的配置。", "属性", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                IntPtr dm = System.Runtime.InteropServices.Marshal.AllocHGlobal(need);
                try
                {
                    if (DocumentProperties(IntPtr.Zero, hp, _printerName, dm, IntPtr.Zero, 2) < 0)   // DM_OUT_BUFFER
                    {
                        MessageBox.Show(this, "读不到这台打印机的配置。", "属性", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    Point keepPos = Location;   // Deli 这类驱动会挪动/缩放属主窗口 —— 弹之前先记住位置和尺寸
                    Size keepSize = Size;
                    int r = DocumentProperties(Handle, hp, _printerName, dm, dm, 4 | 2);   // DM_IN_PROMPT | DM_OUT_BUFFER
                    // 回来立刻还原一次
                    if (Location != keepPos || Size != keepSize) { Location = keepPos; Size = keepSize; }
                    // 个别驱动是对话框关闭之后才挪 → 400ms 再兜一次
                    Timer fixPos = new Timer();
                    fixPos.Interval = 400;
                    fixPos.Tick += delegate(object s2, EventArgs e2)
                    {
                        fixPos.Stop();
                        fixPos.Dispose();
                        if (Location != keepPos || Size != keepSize) { Location = keepPos; Size = keepSize; }
                    };
                    fixPos.Start();
                    if (r < 0)
                        MessageBox.Show(this, "驱动拒绝了属性对话框。", "属性", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else
                        _lblInfo.Text = "打印机属性已更新（下次点打印时生效）";
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(dm); }
            }
            finally { ClosePrinter(hp); }
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
            _lblInfo.Text = (_sheets.Count == 0 ? 0 : _index + 1) + "/" + _sheets.Count + " 张";
        }

        // ---------------- 绘制 ----------------

        // 一张纸上的每个格：Cell=格子（可打印范围），Content=内容落点（1/100 英寸，相对纸左上角）
        private struct SheetItem
        {
            public RectangleF Cell;
            public RectangleF Content;
        }

        private List<SheetItem> ContentRects(int sheetIndex)
        {
            List<SheetItem> list = new List<SheetItem>();
            if (sheetIndex < 0 || sheetIndex >= _sheets.Count) return list;
            int[] sheet = _sheets[sheetIndex];
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
                if (srcW < 1 || srcH < 1) { srcW = 595; srcH = 842; }   // 兜底 A4（点）

                double sc;
                if (_rbActual.Checked) sc = 1.0;                                // 实际大小（1:1）
                else if (_rbCustom.Checked) sc = (double)_numScale.Value / 100.0;
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
                SheetItem it = new SheetItem();
                it.Cell = new RectangleF((float)x0, (float)y0, (float)cellW, (float)cellH);
                it.Content = new RectangleF((float)x, (float)y, (float)w, (float)h);
                list.Add(it);
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
            List<SheetItem> items = ContentRects(_index);
            bool gray = _cbColor.SelectedIndex == 1;
            for (int i = 0; i < sheet.Length && i < items.Count; i++)
            {
                SheetItem it = items[i];
                RectangleF r = it.Content;
                float dx = px + r.X * s, dy = py + r.Y * s, dw = r.Width * s, dh = r.Height * s;
                if (dw < 2 || dh < 2) continue;
                // 只在"可打印范围（格子）"里画：实际大小超出纸面的部分会被裁掉，和真打印一致
                RectangleF cell = new RectangleF(px + it.Cell.X * s, py + it.Cell.Y * s, it.Cell.Width * s, it.Cell.Height * s);
                Region oldClip = g.Clip;
                g.SetClip(cell, CombineMode.Intersect);
                Bitmap bmp = null;
                try
                {
                    lock (_lock) { bmp = _job.RenderPage(sheet[i], (int)Math.Round(dw), (int)Math.Round(dh), false); }
                    if (gray) bmp = ToGray(bmp);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(bmp, dx, dy, dw, dh);
                }
                finally { if (bmp != null) bmp.Dispose(); }
                g.Clip = oldClip;
                g.DrawRectangle(Pens.Silver, dx, dy, dw, dh);
            }
        }


        // ---------------- 真打印 ----------------

        // 把当前设置送进打印机：排版和预览共用 ContentRects()，保证所见即所得
        private void DoPrint()
        {
            if (_sheets.Count == 0)
            {
                MessageBox.Show(this, "没有可打印的页面。", "打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                using (PrintDocument doc = new PrintDocument())
                {
                    if (_printerName.Length > 0)
                    {
                        try { doc.PrinterSettings.PrinterName = _printerName; } catch { }
                    }
                    doc.DocumentName = _job.BaseName;
                    PaperItem pi = _cbPaper.SelectedItem as PaperItem;
                    if (pi != null) { try { doc.DefaultPageSettings.PaperSize = pi.Size; } catch { } }
                    doc.DefaultPageSettings.Landscape = _rbLandscape.Checked;
                    try { doc.PrinterSettings.Copies = (short)_numCopies.Value; } catch { }
                    try { doc.PrinterSettings.Duplex = DuplexSetting; } catch { }   // 不支持会自动退化单面

                    int sheetIdx = 0;
                    doc.PrintPage += delegate(object s, PrintPageEventArgs e)
                    {
                        if (sheetIdx >= _sheets.Count) { e.HasMorePages = false; return; }
                        Graphics g = e.Graphics;
                        g.PageUnit = GraphicsUnit.Display;   // 1/100 英寸，和 ContentRects 单位一致
                        int[] sheet = _sheets[sheetIdx];
                        List<SheetItem> items = ContentRects(sheetIdx);
                        bool gray = _cbColor.SelectedIndex == 1;
                        for (int i = 0; i < sheet.Length && i < items.Count; i++)
                        {
                            SheetItem it = items[i];
                            // 相对"可打印区左上角"定位：减去硬边距，并裁到格子内
                            float x0 = (float)_marginL, y0 = (float)_marginT;
                            float dx = it.Content.X - x0, dy = it.Content.Y - y0;
                            float dw = it.Content.Width, dh = it.Content.Height;
                            if (dw < 1 || dh < 1) continue;
                            Region old = g.Clip;
                            g.SetClip(new RectangleF(it.Cell.X - x0, it.Cell.Y - y0, it.Cell.Width, it.Cell.Height));
                            Bitmap bmp = null;
                            try
                            {
                                int pxW = Math.Max(1, (int)Math.Round(dw / 100.0 * g.DpiX));
                                int pxH = Math.Max(1, (int)Math.Round(dh / 100.0 * g.DpiY));
                                lock (_lock) { bmp = _job.RenderPage(sheet[i], pxW, pxH, false); }
                                if (gray) bmp = ToGray(bmp);
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.DrawImage(bmp, dx, dy, dw, dh);
                            }
                            finally
                            {
                                if (bmp != null) bmp.Dispose();
                                g.Clip = old;
                            }
                        }
                        sheetIdx++;
                        e.HasMorePages = sheetIdx < _sheets.Count;
                    };
                    doc.Print();
                }
                // 发送成功就直接关掉预览窗口（失败走 catch，窗口留着看错误）
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打印失败：" + ex.Message, "出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
