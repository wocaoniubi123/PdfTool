using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PdfTool
{
    internal sealed class MainForm : Form
    {
        private PdfJob _job;
        private int _index;
        private bool _dirty;
        private bool _rendering;   // 已不再使用（渲染改后台），保留占位
        private readonly object _renderLock = new object();  // pdfium 调用串行化
        private int _renderSeq;                                // 渲染请求序号
        private bool _selAllOnMouseUp;
        private Point _boxMouseDown;    // 页码框里按下鼠标的位置（区分点一下 vs 拖动选字）
        private Bitmap _preview;

        private PreviewBox _view;
        private Label _hint;
        private TextBox _txtPage;
        private Label _lblTotal;
        private Label _lblStatus;
        private Button _btnFirst;
        private Button _btnLast;
        private ContextMenuStrip _menu;
        private Timer _resizeTimer;
        private Timer _idleTimer;
        private bool _fastScroll;
        private int _shownSeq;                                       // 已经画到屏幕上的渲染序号（判断"这一页画完了没"）
        private readonly Queue<int> _wheelQueue = new Queue<int>();  // 待翻的步子（每项 ±1），一页一页走
        private Timer _wheelTimer;
        private int _lastStepMsg;       // 最近一次滚轮/方向键的时刻（判断"手停了没"）
        private readonly string _initialPath;
        private CheckBox _chkDesktop;   // 输出位置：桌面
        private CheckBox _chkLocal;     // 输出位置：源文件目录
        private bool _syncingMode;

        private const string HintOpenDoc = "把 PDF 文件拖到这里\r\n\r\n（在图上点右键：删页 / 复制 / 上移下移 / 插入图片）";
        private const string HintNewDoc = "新 PDF（保存到桌面）\r\n\r\n把图片拖进来，或在图上点右键 → 插入图片";

        public MainForm(string initialPath)
        {
            _initialPath = initialPath;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            BuildUi();
            Application.AddMessageFilter(new UiFilter(this));
        }

        // ---------------- 界面 ----------------

        private void BuildUi()
        {
            Text = "PDF 页面工具";
            Width = 1020;
            Height = 780;
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            KeyPreview = true;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(250, 250, 250);

            // 工具栏：左（文件）/ 中（浏览，居中）/ 右（工具）
            TableLayoutPanel bar = new TableLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = 58;
            bar.Padding = new Padding(12, 12, 12, 10);
            bar.ColumnCount = 3;
            bar.RowCount = 1;
            bar.BackColor = Color.FromArgb(250, 250, 250);
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            FlowLayoutPanel fileGroup = NewGroup();
            Button btnOpen = MakeButton("打开 PDF");
            btnOpen.Click += delegate { OpenDialog(); };
            fileGroup.Controls.Add(btnOpen);
            Button btnNew = MakeButton("新建PDF（桌面）");
            btnNew.Click += delegate { NewPdf(true); };
            fileGroup.Controls.Add(btnNew);
            Button btnSave = MakeButton("保存修改");
            btnSave.Click += delegate { SaveEdited(); };
            fileGroup.Controls.Add(btnSave);

            _chkDesktop = MakeModeBox("存到桌面");
            _chkDesktop.Margin = new Padding(8, 0, 0, 0);   // 两个勾选框凑成一组，右边距交给下面那个
            _chkDesktop.CheckedChanged += delegate { if (!_syncingMode) SetSaveMode(_chkDesktop.Checked); };
            fileGroup.Controls.Add(_chkDesktop);

            _chkLocal = MakeModeBox("存到源文件目录");
            _chkLocal.Margin = new Padding(2, 0, 12, 0);     // 组内只留 2px，不贴住
            _chkLocal.CheckedChanged += delegate { if (!_syncingMode) SetSaveMode(!_chkLocal.Checked); };
            fileGroup.Controls.Add(_chkLocal);

            FlowLayoutPanel navGroup = NewGroup();
            navGroup.Anchor = AnchorStyles.None; // 这一组在剩余空间里居中
            _btnFirst = MakeButton("首页");
            _btnFirst.Click += delegate { Go(0); };
            navGroup.Controls.Add(_btnFirst);

            _txtPage = new TextBox();
            _txtPage.Width = 54;
            _txtPage.TextAlign = HorizontalAlignment.Center;
            _txtPage.Margin = new Padding(0, 4, 6, 4);
            _txtPage.KeyDown += OnPageBoxKey;
            _txtPage.KeyPress += OnPageBoxPress;
            // 点进输入框就全选（鼠标松开后再选，否则会被落光标清掉），直接输入覆盖旧页码
            _txtPage.Enter += delegate
            {
                _selAllOnMouseUp = true;
                _wheelQueue.Clear();          // 开始输页码，先停下排队的翻页
                _wheelTimer.Stop();
            };
            // 点一下（没有拖动）就整段选中：接着敲数字直接替换。
            // 注意 Enter 事件只在"拿到焦点"时发一次，第二次点框时不会发，所以这里补上。
            _txtPage.MouseDown += delegate(object s, MouseEventArgs e) { _boxMouseDown = e.Location; };
            _txtPage.MouseUp += delegate(object s, MouseEventArgs e)
            {
                bool noDrag = Math.Abs(e.Location.X - _boxMouseDown.X) < 4 && Math.Abs(e.Location.Y - _boxMouseDown.Y) < 4;
                if (_selAllOnMouseUp || noDrag)
                {
                    _selAllOnMouseUp = false;
                    _txtPage.SelectAll();
                }
            };
            navGroup.Controls.Add(_txtPage);

            _lblTotal = new Label();
            _lblTotal.Text = "/ 0";
            _lblTotal.AutoSize = true;          // 按内容自适应，别把「尾页」推远
            _lblTotal.MinimumSize = new Size(34, 30);
            _lblTotal.TextAlign = ContentAlignment.MiddleLeft;
            _lblTotal.Margin = new Padding(0, 0, 6, 0);
            navGroup.Controls.Add(_lblTotal);

            _btnLast = MakeButton("尾页");
            _btnLast.Click += delegate { Go(_job == null ? 0 : _job.PageCount - 1); };
            navGroup.Controls.Add(_btnLast);

            FlowLayoutPanel toolGroup = NewGroup();
            Button btnSplit = MakeButton("拆分奇数/偶数页");
            btnSplit.Margin = new Padding(0);

            btnSplit.Click += delegate { SplitOddEven(); };
            toolGroup.Controls.Add(btnSplit);

            bar.Controls.Add(fileGroup, 0, 0);
            bar.Controls.Add(navGroup, 1, 0);
            bar.Controls.Add(toolGroup, 2, 0);

            _lblStatus = new Label();
            _lblStatus.Dock = DockStyle.Bottom;
            _lblStatus.Height = 26;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.Padding = new Padding(12, 0, 0, 0);
            _lblStatus.BackColor = Color.FromArgb(250, 250, 250);
            _lblStatus.Text = "把 PDF 拖进来打开；也可以点「新建PDF（桌面）」后把图片拖进来。";

            _view = new PreviewBox();
            _view.Dock = DockStyle.Fill;
            _view.BackColor = Color.FromArgb(236, 236, 236);
            _view.SizeMode = PictureBoxSizeMode.Normal; // 用 OnViewPaint 按 1:1 画，不做二次缩放
            _view.AllowDrop = true;
            _view.DragEnter += OnDragEnter;
            _view.DragDrop += OnDragDrop;

            _hint = new Label();
            _hint.Dock = DockStyle.Fill;
            _hint.TextAlign = ContentAlignment.MiddleCenter;
            _hint.ForeColor = Color.DimGray;
            _hint.BackColor = Color.Transparent;
            _hint.Text = HintOpenDoc;
            _hint.AllowDrop = true;
            _hint.DragEnter += OnDragEnter;
            _hint.DragDrop += OnDragDrop;
            _view.Paint += OnViewPaint;
            _view.Controls.Add(_hint);

            _menu = new ContextMenuStrip();
            _menu.Items.Add("删除本页", null, delegate { DeleteCurrent(); });
            _menu.Items.Add("复制本页", null, delegate { DuplicateCurrent(); });
            _menu.Items.Add("上移本页", null, delegate { MoveCurrent(-1); });
            _menu.Items.Add("下移本页", null, delegate { MoveCurrent(1); });
            _menu.Items.Add("在上面插入图片", null, delegate { InsertImageDialog(true); });
            _menu.Items.Add("在下面插入图片", null, delegate { InsertImageDialog(false); });
            _menu.Items.Add("提取图片到桌面", null, delegate { ExtractImages(); });
            _menu.Opened += delegate { MenuEnabled(); };
            _view.ContextMenuStrip = _menu;
            _hint.ContextMenuStrip = _menu;

            Controls.Add(_view);
            Controls.Add(bar);
            Controls.Add(_lblStatus);

            _idleTimer = new Timer();
            _idleTimer.Interval = 220;
            _idleTimer.Tick += delegate
            {
                _idleTimer.Stop();
                if (_fastScroll) { _fastScroll = false; Render(); } // 停下后补一张高清
            };

            _wheelTimer = new Timer();
            _wheelTimer.Interval = 15;   // 一步 15ms；实际节奏由"上一页画完没"决定（保每页都看得见）
            _wheelTimer.Tick += PumpWheelTick;

            _resizeTimer = new Timer();
            _resizeTimer.Interval = 200;
            _resizeTimer.Tick += delegate { _resizeTimer.Stop(); Render(); };
            _view.Resize += delegate
            {
                if (_job == null || _job.PageCount == 0) return;
                _resizeTimer.Stop();
                _resizeTimer.Start();
            };

            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            Settings.Load();
            SetSaveMode(Settings.SaveToDesktop);

            Shown += delegate { if (_initialPath != null) OpenFile(_initialPath); };
        }

        private static FlowLayoutPanel NewGroup()
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.AutoSize = true;
            p.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            p.FlowDirection = FlowDirection.LeftToRight;
            p.WrapContents = false;
            p.Margin = new Padding(0);
            p.Anchor = AnchorStyles.Left;
            return p;
        }

        // 输出位置勾选框：高度与按钮一致（30），宽度按文字自适应；字体沿窗体（雅黑 9pt）
        private static CheckBox MakeModeBox(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.MinimumSize = new Size(0, 30);   // 高度对齐按钮，宽度仍由文字决定
            c.TextAlign = ContentAlignment.MiddleLeft;
            c.Margin = new Padding(8, 0, 12, 0);
            return c;
        }

        // 按钮宽度按文字长度自适应（文字两侧各留 16px），高度统一 30
        private Button MakeButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.Font = Font;                              // 与窗体同字体（雅黑 9pt），宽度按它量
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(8, 0, 8, 0);
            b.MinimumSize = new Size(0, 30);
            b.Margin = new Padding(0, 0, 8, 0);
            return b;
        }

        // 1:1 绘制预览（不做任何插值缩放，保证屏幕像素和渲染结果一致）
        private void OnViewPaint(object sender, PaintEventArgs e)
        {
            if (_preview == null) return;
            int x = (_view.ClientSize.Width - _preview.Width) / 2;
            int y = (_view.ClientSize.Height - _preview.Height) / 2;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            e.Graphics.DrawImageUnscaled(_preview, x, y);
        }

        private sealed class PreviewBox : PictureBox
        {
            public PreviewBox() { DoubleBuffered = true; }
        }

        // 全局消息过滤器：滚轮翻页 + 点空白处把焦点从页码框收回窗体
        // （预览区/标签这类控件在 WinForms 里不能接收焦点，点它们默认不会移走焦点）
        private sealed class UiFilter : IMessageFilter
        {
            private readonly MainForm _form;
            public UiFilter(MainForm form) { _form = form; }
            public bool PreFilterMessage(ref Message m)
            {
                if (_form._menu.Visible) return false;

                if (m.Msg == 0x0201) // WM_LBUTTONDOWN
                {
                    Control clicked = Control.FromHandle(m.HWnd);
                    // 点到"压根不能有焦点"的地方（预览区、标签、面板空白）→ 焦点还给窗体，方向键/滚轮恢复正常
                    if (clicked != null && (clicked == _form || !clicked.CanSelect) && _form._txtPage.Focused)
                        _form.ActiveControl = null;
                    return false;
                }

                if (m.Msg != 0x020A) return false; // WM_MOUSEWHEEL
                // 只要求滚轮事件是发给本窗口（或其子控件）的，不要求它必须是"活动窗口"，
                // 否则切到别的窗口再回来滚就没反应。
                Control target = Control.FromHandle(m.HWnd);
                if (target == null || !IsMine(target)) return false;
                int delta = (short)((long)m.WParam >> 16);
                return _form.WheelNav(delta);
            }

            private bool IsMine(Control c)
            {
                while (c != null)
                {
                    if (c == _form) return true;
                    c = c.Parent;
                }
                return false;
            }
        }

        internal bool WheelNav(int delta)
        {
            if (_job == null || _job.PageCount == 0) return false;
            // 系统会把连续滚动合并成一条消息（delta 可能是 ±240、±360），按倍数入队
            int steps = Math.Abs(delta) / 120;
            if (steps < 1) steps = 1;
            int dir = delta > 0 ? -1 : 1;
            for (int i = 0; i < steps; i++) StepWheel(dir);
            return true;
        }

        // 排队翻一页（滚轮/方向键都用它）：一页一页走，保证每页都渲染出来
        private void StepWheel(int dir)
        {
            if (_job == null || _job.PageCount == 0) return;
            if (_wheelQueue.Count < 60) _wheelQueue.Enqueue(dir);
            _lastStepMsg = Environment.TickCount;   // 记下"手还在动"
            if (!_wheelTimer.Enabled) _wheelTimer.Start();
        }

        // 每 25ms 试走一步；上一页还没画到屏幕上就等着（这样快滚也是连续实时预览，不跳图）
        private void PumpWheelTick(object sender, EventArgs e)
        {
            if (_job == null || _job.PageCount == 0 || _wheelQueue.Count == 0)
            {
                _wheelQueue.Clear();
                _wheelTimer.Stop();
                return;
            }
            if (_shownSeq != _renderSeq) return;   // 上一页还在画，等它显示出来

            // 手已经停了（90ms 没新消息）但队列还排着 ≥3 步：别让它再自己滚几秒，
            // 直接一步落到"该到的页"（中途该显示的页在滚动过程里已经显示过了）
            if (_wheelQueue.Count >= 3 && Environment.TickCount - _lastStepMsg > 90)
            {
                int jump = 0;
                foreach (int s in _wheelQueue) jump += s;
                _wheelQueue.Clear();
                int t2 = _index + jump;
                if (t2 < 0) t2 = 0;
                if (t2 > _job.PageCount - 1) t2 = _job.PageCount - 1;
                if (t2 != _index) { Navigate(t2, true); return; }
            }

            int target = _index + _wheelQueue.Dequeue();
            if (target < 0 || target > _job.PageCount - 1)   // 到首/尾了，剩下的步子作废
            {
                _wheelQueue.Clear();
                _wheelTimer.Stop();
                return;
            }
            Navigate(target, true);
        }

        private void MenuEnabled()
        {
            bool hasPage = _job != null && _job.PageCount > 0;
            _menu.Items[0].Enabled = hasPage;   // 删除本页
            _menu.Items[1].Enabled = hasPage;   // 复制本页
            _menu.Items[2].Enabled = hasPage;   // 上移本页
            _menu.Items[3].Enabled = hasPage;   // 下移本页
            _menu.Items[4].Enabled = _job != null;
            _menu.Items[5].Enabled = _job != null;
            _menu.Items[6].Enabled = hasPage;
        }

        // ---------------- 拖拽 ----------------

        private static bool IsImageFile(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            return e == ".jpg" || e == ".jpeg" || e == ".png" || e == ".bmp"
                || e == ".gif" || e == ".tif" || e == ".tiff";
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0 &&
                    (files[0].ToLowerInvariant().EndsWith(".pdf") || IsImageFile(files[0])))
                {
                    e.Effect = DragDropEffects.Copy;
                    return;
                }
            }
            e.Effect = DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;
            List<string> images = new List<string>();
            string pdf = null;
            foreach (string f in files)
            {
                if (f.ToLowerInvariant().EndsWith(".pdf")) { if (pdf == null) pdf = f; }
                else if (IsImageFile(f)) images.Add(f);
            }
            if (images.Count > 0) InsertImageFiles(images, true); // 拖进来的图插在当前页之前
            else if (pdf != null) OpenFile(pdf);
            Activate();  // 拖完把焦点收回程序，接着就能用方向键/滚轮
            Focus();
        }

        // ---------------- 键盘 ----------------

        // 方向键翻页（在页码输入框里时不抢键）
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_job != null && _job.PageCount > 0 && !_txtPage.Focused)
            {
                if (keyData == Keys.Left) { StepWheel(-1); return true; }
                if (keyData == Keys.Right) { StepWheel(1); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnPageBoxPress(object sender, KeyPressEventArgs e)
        {
            if (!char.IsDigit(e.KeyChar) && e.KeyChar != '\b' && e.KeyChar != '\r')
            {
                e.Handled = true;
                return;
            }
            // 边输边跳：数字合法就直接翻页（不用回车）；这一位字符不再自动插入（文本已在这里写好）
            if (char.IsDigit(e.KeyChar) && JumpTypedPage(e.KeyChar)) e.Handled = true;
        }

        // 页码框里敲一位数字：算出"这一位之后"的页码，合法就立刻跳过去
        private bool JumpTypedPage(char c)
        {
            if (_job == null || _job.PageCount == 0) return false;
            string cur = _txtPage.Text;
            int s = _txtPage.SelectionStart, len = _txtPage.SelectionLength;
            if (s > cur.Length) s = cur.Length;
            if (s + len > cur.Length) len = cur.Length - s;
            string next = cur.Substring(0, s) + c + cur.Substring(s + len);
            int n;
            if (!int.TryParse(next, out n)) return false;
            if (n < 1 || n > _job.PageCount) return false;   // 越界就等继续输入或回车
            _wheelQueue.Clear();                             // 手动输入优先，停掉排队的翻页
            _wheelTimer.Stop();
            Go(n - 1);
            _txtPage.Text = (_index + 1).ToString();
            _txtPage.SelectionStart = _txtPage.Text.Length;  // 光标放末尾，接着敲下一位数字
            return true;
        }

        private void OnPageBoxKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (_job == null || _job.PageCount == 0) return;
                int n;
                if (int.TryParse(_txtPage.Text.Trim(), out n))
                {
                    if (n < 1) n = 1;
                    if (n > _job.PageCount) n = _job.PageCount;
                    Go(n - 1);
                    _lblStatus.Text = "已跳到第 " + (_index + 1) + " 页";
                    _txtPage.Text = (_index + 1).ToString();
                    _txtPage.SelectAll(); // 连续跳页时直接输新数字即可
                }
                else
                {
                    UpdateUi();
                }
            }
            else if (e.KeyCode == Keys.Escape)   // Esc = 放弃输入，焦点还给窗体
            {
                e.SuppressKeyPress = true;
                UpdateUi();
                ActiveControl = null;
            }
        }

        // ---------------- 打开 / 新建 ----------------

        private void OpenDialog()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "PDF 文件|*.pdf|所有文件|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK) OpenFile(dlg.FileName);
            }
        }

        private void OpenFile(string path)
        {
            PdfJob nj;
            try
            {
                nj = PdfJob.Load(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "打不开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            lock (_renderLock) { if (_job != null) _job.Dispose(); }
            _job = nj;
            _index = 0;
            _dirty = false;
            _hint.Text = HintOpenDoc;
            _lblStatus.Text = Path.GetFileName(path) + "（共 " + _job.PageCount + " 页）";
            UpdateUi();
            Render();
        }

        // 新建空白 PDF：保存目标是桌面；把图片拖进来即可拼页面
        private void NewPdf(bool talk)
        {
            lock (_renderLock) { if (_job != null) _job.Dispose(); }
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            _job = PdfJob.CreateNew(desktop, "新PDF");
            _index = 0;
            _dirty = true;   // 还没有对应文件，视为未保存
            _hint.Text = HintNewDoc;
            _lblStatus.Text = talk
                ? "已新建空白 PDF（保存到桌面）——把图片拖进来，再点「保存修改」"
                : "已新建空白 PDF：把图片拖进来，再点「保存修改」";
            UpdateUi();
            Render();
        }

        // ---------------- 浏览 ----------------

        private void Go(int i)
        {
            Navigate(i, false);
        }

        // fast=true：滚轮/方向键连续翻页时用低延迟渲染，停手后由 _idleTimer 补高清
        private void Navigate(int i, bool fast)
        {
            if (_job == null || _job.PageCount == 0) return;
            if (i < 0) i = 0;
            if (i > _job.PageCount - 1) i = _job.PageCount - 1;
            if (i == _index) return;
            _index = i;
            _fastScroll = fast;
            UpdateUi();
            Render();
            if (fast)
            {
                _idleTimer.Stop();
                _idleTimer.Start();
            }
        }

        private void UpdateUi()
        {
            if (_job == null)
            {
                if (!_txtPage.Focused) _txtPage.Text = "";
                _lblTotal.Text = "/ 0";
                Text = "PDF 页面工具";
                _btnFirst.Enabled = false;
                _btnLast.Enabled = false;
                _txtPage.Enabled = false;
                return;
            }
            if (_job.PageCount == 0)
            {
                if (!_txtPage.Focused) _txtPage.Text = "";
                _lblTotal.Text = "/ 0";
                Text = "PDF 页面工具 - 新 PDF（保存到桌面）" + (_dirty ? " *" : "");
                _btnFirst.Enabled = false;
                _btnLast.Enabled = false;
                _txtPage.Enabled = false;
                return;
            }
            _txtPage.Enabled = true;
            // 页码框永远跟着当前页走（之前"框有焦点就不更新"，导致点过框再滚轮数字不动）；
            // 只有内容真的变了才写，避免打断正在输入的内容；变了就全选，方便直接覆盖。
            string pageText = (_index + 1).ToString();
            if (_txtPage.Text != pageText)
            {
                _txtPage.Text = pageText;
                if (_txtPage.Focused) _txtPage.SelectAll();
            }
            _lblTotal.Text = "/ " + _job.PageCount;
            string name = _job.SourcePath == null ? "新 PDF（保存到桌面）" : Path.GetFileName(_job.SourcePath);
            Text = "PDF 页面工具 - " + name + (_dirty ? " *" : "");
            _btnFirst.Enabled = _index > 0;
            _btnLast.Enabled = _index < _job.PageCount - 1;
        }

        // 渲染放到后台线程：pdfium 调用用 _renderLock 串行化，结果回 UI 线程换图。
        // 这样"快速滚动 + 停手补高清"都不会再阻塞界面（之前会偶尔卡一下）。
        private void Render()
        {
            if (_job == null || _job.PageCount == 0)
            {
                if (_preview != null) { _preview.Dispose(); _preview = null; }
                _view.Invalidate();
                _hint.Visible = true;
                return;
            }
            int seq = ++_renderSeq;
            int idx = _index;
            bool fast = _fastScroll;
            int w = _view.ClientSize.Width - 2; // 尽量按显示区 1:1 渲染，避免二次缩放
            int h = _view.ClientSize.Height - 2;
            if (w < 60) w = 60;
            if (h < 60) h = 60;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                Bitmap bmp = null;
                try
                {
                    lock (_renderLock)
                    {
                        if (seq != _renderSeq) return;          // 已有更新的请求，别浪费
                        bmp = _job.RenderPage(idx, w, h, fast);
                    }
                }
                catch
                {
                    return;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (seq != _renderSeq) { bmp.Dispose(); return; }
                        _shownSeq = seq;   // 这一版已经上屏，滚轮队列可以走下一步了
                        Bitmap old = _preview;
                        _preview = bmp;
                        _view.Invalidate(); // 交给 OnViewPaint 按 1:1 居中绘制
                        _hint.Visible = false;
                        if (old != null && !object.ReferenceEquals(old, bmp)) old.Dispose();
                    });
                }
                catch
                {
                    bmp.Dispose();   // 窗口已关
                }
            });
        }

        // ---------------- 增删改序 ----------------

        private void DeleteCurrent()
        {
            if (_job == null || _job.PageCount == 0) return;
            if (_job.PageCount <= 1)
            {
                _lblStatus.Text = "至少保留一页，删不了。";
                return;
            }
            _job.Pages.RemoveAt(_index);
            if (_index > _job.PageCount - 1) _index = _job.PageCount - 1;
            _dirty = true;
            _lblStatus.Text = "已删除一页（改动在内存里，点「保存修改」才写盘）";
            UpdateUi();
            Render();
        }

        private void DuplicateCurrent()
        {
            if (_job == null || _job.PageCount == 0) return;
            _job.Pages.Insert(_index + 1, _job.Pages[_index].Clone());
            _index = _index + 1;
            _dirty = true;
            _lblStatus.Text = "已复制本页";
            UpdateUi();
            Render();
        }

        // 整页换位置（不重编码，只调顺序）
        private void MoveCurrent(int dir)
        {
            if (_job == null || _job.PageCount < 2) return;
            int to = _index + dir;
            if (to < 0) { _lblStatus.Text = "已经是第一页了。"; return; }
            if (to > _job.PageCount - 1) { _lblStatus.Text = "已经是最后一页了。"; return; }
            PageRef p = _job.Pages[_index];
            _job.Pages.RemoveAt(_index);
            _job.Pages.Insert(to, p);
            _index = to;
            _dirty = true;
            _lblStatus.Text = dir < 0 ? "已上移一页" : "已下移一页";
            UpdateUi();
            Render();
        }

        // 右键菜单：选图片插入
        private void InsertImageDialog(bool above)
        {
            if (_job == null) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要插入的图片";
                dlg.Multiselect = true;
                dlg.Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                InsertImageFiles(new List<string>(dlg.FileNames), above);
            }
        }

        // 批量插入图片；above=true 插到当前页之前（保持顺序），否则插到当前页之后
        private void InsertImageFiles(List<string> paths, bool above)
        {
            if (paths == null || paths.Count == 0) return;
            if (_job == null) NewPdf(false);
            int ok = 0;
            if (above)
            {
                // 往前插要倒着来，否则顺序会反
                for (int i = paths.Count - 1; i >= 0; i--)
                {
                    if (TryInsertImage(paths[i], true)) ok++;
                }
            }
            else
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (TryInsertImage(paths[i], false)) ok++;
                }
            }
            if (ok > 0)
            {
                _lblStatus.Text = "已插入 " + ok + " 张图片（" + (above ? "在当前页前面" : "在当前页后面")
                    + "，点「保存修改」写盘）";
                UpdateUi();
                Render();
            }
        }

        private bool TryInsertImage(string file, bool above)
        {
            try
            {
                InsertOneImage(file, above);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "插不了这张图片：\n" + Path.GetFileName(file) + "\n" + ex.Message,
                    "出错", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void InsertOneImage(string file, bool above)
        {
            SizeF target = _job.PageCount > 0
                ? _job.GetPageSizePoints(_job.Pages[_index])
                : new SizeF(595f, 842f); // 空白新文档按 A4
            PageRef p = PdfJob.MakeImagePage(file, target);
            int at = _job.PageCount == 0 ? 0 : (above ? _index : _index + 1);
            _job.Pages.Insert(at, p);
            _index = at;
            _dirty = true;
        }

        // 把当前页里的图片提取到桌面
        private void ExtractImages()
        {
            if (_job == null || _job.PageCount == 0) return;
            try
            {
                string desktop = OutDir();
                string prefix = _job.BaseName + "_第" + (_index + 1) + "页";
                List<string> saved = new List<string>();
                int n;
                lock (_renderLock) { n = _job.ExtractPageImages(_index, desktop, prefix, saved); }
                if (n == 0)
                {
                    _lblStatus.Text = "这一页没有可提取的图片。";
                    return;
                }
                _lblStatus.Text = "已提取 " + n + " 张图片到" + DestName() + "。";
                MessageBox.Show(this, "已提取 " + n + " 张图片到" + DestName() + "：\n" + string.Join("\n", saved.ToArray()),
                    "提取完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "提取失败：" + ex.Message, "出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // 当前输出位置的说法：和勾选框文案保持一致（桌面 / 源文件目录）
        private static string DestName()
        {
            return Settings.SaveToDesktop ? "桌面" : "源文件目录";
        }

        // 输出位置：桌面 / 源文件目录（勾选框互斥，写进 ini 持久化）
        private void SetSaveMode(bool desktop)
        {
            _syncingMode = true;
            _chkDesktop.Checked = desktop;
            _chkLocal.Checked = !desktop;
            _syncingMode = false;
            Settings.SaveToDesktop = desktop;
            Settings.Save();
            _menu.Items[6].Text = desktop ? "提取图片到桌面" : "提取图片到源文件目录";
            _lblStatus.Text = "输出位置：" + DestName();
        }

        private string OutDir()
        {
            if (Settings.SaveToDesktop) return Settings.DesktopDir;
            return _job == null ? Settings.DesktopDir : _job.Dir;   // 新建的空白 PDF 没有"本地"，仍存桌面
        }

        private string OutPath(string suffix)
        {
            return Path.Combine(OutDir(), _job.BaseName + suffix);
        }

        // ---------------- 保存 / 拆分 ----------------

        // 输出路径撞名时问一次：覆盖 / +1；点 X 或按 Esc = 取消保存（返回 null）
        private string ResolvePath(string path)
        {
            if (!File.Exists(path)) return path;
            DialogResult r = AskOverwrite(path);
            if (r == DialogResult.Yes) return path;              // 覆盖
            if (r == DialogResult.No) return Util.AutoName(path); // +1
            return null;                                          // 取消：不写任何文件
        }

        // 撞名对话框：左"覆盖"、右"+1"；回车=覆盖，**Esc 或右上角 X = 取消（不保存）**
        private DialogResult AskOverwrite(string path)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "同名文件";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.Font = Font;
                dlg.ClientSize = new Size(470, 168);

                Label lbl = new Label();
                lbl.AutoSize = false;
                lbl.SetBounds(16, 14, 438, 96);
                lbl.TextAlign = ContentAlignment.TopLeft;
                lbl.Text = "已存在：\n" + path + "\n\n覆盖它，还是存成带序号的新文件？";
                dlg.Controls.Add(lbl);

                Button ok = new Button();
                ok.Text = "覆盖";
                ok.SetBounds(206, 118, 96, 32);
                ok.DialogResult = DialogResult.Yes;
                dlg.Controls.Add(ok);

                Button plus = new Button();
                plus.Text = "+1";
                plus.SetBounds(310, 118, 96, 32);
                plus.DialogResult = DialogResult.No;
                dlg.Controls.Add(plus);

                dlg.AcceptButton = ok;     // 回车 = 覆盖
                // 注意：不设 CancelButton —— 否则 Esc 会被当成"点 +1"；X 和 Esc 都必须是"取消保存"
                dlg.KeyPreview = true;
                dlg.KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Escape) { dlg.DialogResult = DialogResult.Abort; dlg.Close(); }
                };
                dlg.FormClosing += delegate(object s, FormClosingEventArgs e)
                {
                    if (dlg.DialogResult == DialogResult.None) dlg.DialogResult = DialogResult.Abort;   // 点 X
                };
                return dlg.ShowDialog(this);
            }
        }

        private void SaveEdited()
        {
            if (_job == null) return;
            if (_job.PageCount == 0)
            {
                _lblStatus.Text = "还没有内容可保存：先把图片拖进来。";
                return;
            }
            try
            {
                // 新建的空白 PDF：直接存 <名字>.pdf（不带 _已修改）；打开的 PDF：<名字>_已修改.pdf
                // 两者撞名都弹"覆盖 / +1"对话框
                string p = ResolvePath(OutPath(_job.SourcePath == null ? ".pdf" : "_已修改.pdf"));
                if (p == null)   // 用户点了 X / Esc：不保存，直接返回
                {
                    _lblStatus.Text = "已取消保存（没有写任何文件）。";
                    return;
                }
                lock (_renderLock) { _job.SavePages(p, _job.Pages); }
                _dirty = false;
                UpdateUi();
                _lblStatus.Text = "已保存：" + p;
                MessageBox.Show(this, "已保存：\n" + p, "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SplitOddEven()
        {
            if (_job == null) return;
            if (_job.PageCount == 0)
            {
                _lblStatus.Text = "还没有内容可拆分。";
                return;
            }
            try
            {
                List<PageRef> odd = Util.OddOf(_job.Pages);
                List<PageRef> even = Util.EvenOf(_job.Pages);
                // 先把两个路径都问清楚再写盘：中途点 X 取消，不会只写出半套
                string p1 = ResolvePath(OutPath("_奇数页.pdf"));
                if (p1 == null) { _lblStatus.Text = "已取消拆分（没有写任何文件）。"; return; }
                string p2 = null;
                if (even.Count > 0)
                {
                    p2 = ResolvePath(OutPath("_偶数页.pdf"));
                    if (p2 == null) { _lblStatus.Text = "已取消拆分（没有写任何文件）。"; return; }
                }
                lock (_renderLock) { _job.SavePages(p1, odd); }
                string msg = "已生成：\n" + p1 + "\n（" + odd.Count + " 页）";
                if (p2 != null)
                {
                    lock (_renderLock) { _job.SavePages(p2, even); }
                    msg += "\n\n" + p2 + "\n（" + even.Count + " 页）";
                }
                _lblStatus.Text = "拆分完成，输出在" + DestName() + "。";
                MessageBox.Show(this, msg, "拆分完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "拆分失败：" + ex.Message, "出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_preview != null) { _preview.Dispose(); _preview = null; }
            base.OnFormClosed(e);
        }
    }
}
