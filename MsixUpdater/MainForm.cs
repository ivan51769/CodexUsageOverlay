using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace Blues19.CodexInstaller
{
    public sealed class MainForm : Form
    {
        // 浅绿毛玻璃配色
        private static readonly Color EdgeLine = Color.FromArgb(227, 232, 240);   // 极窄边框
        private static readonly Color Card = Color.FromArgb(247, 249, 252);
        private static readonly Color Ink = Color.FromArgb(43, 55, 74);
        private static readonly Color Muted = Color.FromArgb(105, 120, 142);
        private static readonly Color Hair = Color.FromArgb(227, 232, 240);
        private static readonly Color Primary = Color.FromArgb(49, 104, 221);
        private static readonly Color Sage = Color.FromArgb(81, 106, 145);
        private static readonly Color Teal = Color.FromArgb(49, 104, 221);
        private static readonly Color Steel = Color.FromArgb(62, 124, 140);
        private static readonly Color Stone = Color.FromArgb(107, 127, 118);
        private static readonly Color Danger = Color.FromArgb(194, 84, 77);
        private static readonly Color Amber = Color.FromArgb(190, 120, 20);
        private static readonly Color Good = Color.FromArgb(21, 128, 61);

        // 以下都是 96 DPI 下的设计坐标，实际使用前一律经过 S() 换算
        private const int CaptionHeightDip = 42;
        private const int GripDip = 6;
        private const int PadDip = 20;

        private float _scale = 1f;

        /// <summary>设计坐标 → 实际像素。</summary>
        private int S(int dip)
        {
            return (int)Math.Round(dip * _scale);
        }

        private int CaptionHeight { get { return S(CaptionHeightDip); } }

        private static float DetectScale()
        {
            if (_scaleOverride > 0) return _scaleOverride;
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float s = g.DpiX / 96f;
                    if (s < 1f) s = 1f;
                    if (s > 3f) s = 3f;
                    return s;
                }
            }
            catch
            {
                return 1f;
            }
        }

        private readonly Logger _log;
        private readonly string _startupAction;

        private Label _stateValue, _installedValue, _latestValue, _sizeValue, _hint, _dirLabel;
        private Label _installerUpdateStatus;
        private ProgressPanel _progress;
        private RichTextBox _logBox;
        private Button _btnCheck, _btnUpdate, _btnDownload, _btnInstallLocal, _btnFolder, _btnOpenLog, _btnNetwork, _btnCancel;
        private Button _btnInstallerUpdate;
        private CheckBox _forceRedownload;

        private Rectangle _closeRect, _minRect;
        private int _hotButton = -1;     // 0=最小化 1=关闭

        private Thread _worker;
        private CancellationTokenSource _cancel;
        private volatile bool _busy;

        private PackageInfo _latest;
        private InstalledPackage _installed;
        private string _finalState = "idle";
        private string _mode = "gui";
        private string _installerUpdateUrl;
        private bool _installerUpdateCheckStarted;

        private static float _scaleOverride;

        /// <summary>排版自检用：强制按指定缩放比例构建界面。必须在构造窗口之前调用。</summary>
        public static void PresetScale(float scale) { _scaleOverride = scale; }

        /// <summary>已经构建完成时的兼容入口（重新按新比例搭一遍界面）。</summary>
        public void OverrideScale(float scale)
        {
            if (Math.Abs(scale - _scale) < 0.001f) return;
            _scaleOverride = scale;
            Controls.Clear();
            BuildUi();
        }

        public MainForm(Logger log, string startupAction)
        {
            _log = log;
            _startupAction = startupAction ?? string.Empty;
            BuildUi();
            _log.Appended += OnLogAppended;
        }

        // ---------------- 窗口外观 ----------------

        /// <summary>
        /// 保证窗口一定放得下。150% 缩放的 1366×768 笔记本上，按设计尺寸放大后会超出屏幕，
        /// 这里按可用工作区裁一刀再居中——否则底部的日志区和按钮会被挤到屏幕外面。
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                int w = Math.Min(Width, work.Width - S(24));
                int h = Math.Min(Height, work.Height - S(24));
                if (w < MinimumSize.Width || h < MinimumSize.Height)
                {
                    // 屏幕比最小尺寸还小时，让最小尺寸让步，宁可挤一点也不能超出屏幕
                    MinimumSize = new Size(Math.Min(MinimumSize.Width, w), Math.Min(MinimumSize.Height, h));
                }
                if (w != Width || h != Height) Size = new Size(w, h);
                Location = new Point(work.X + (work.Width - Width) / 2,
                                     work.Y + (work.Height - Height) / 2);
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 毛玻璃这里是自己画的，没有用 DWM 的亚克力。
            // 试过 SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)：
            // 它会把整个客户区连同 GDI 画上去的文字一起做透明合成，结果是界面糊到看不清；
            // 而且这套接口在不同 Windows 版本上表现并不一致，与「换台电脑也能正常用」相冲突。
            // 改为：浅绿渐变 + 细噪点磨砂质感 + 轻微整窗半透，观感一致且到哪台机器都一样。
            // 注意：不要用 DwmExtendFrameIntoClientArea 去要投影。无边框窗口上它会把整个
            // 客户区变成玻璃区，叠加 Opacity 之后背后的内容会透得看不清正文，实测如此。
            // 投影改用窗口类的 CS_DROPSHADOW（见 CreateParams）。
            Opacity = 0.985;
            Glass.EnableRoundedCorners(this);
        }

        private const int CsDropShadow = 0x00020000;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CsDropShadow;   // 无边框窗口的投影
                return cp;
            }
        }

        /// <summary>磨砂颗粒。生成一次后作为平铺纹理反复用，避免每帧重算。</summary>
        private static TextureBrush _frostBrush;
        private static Image _brandLogo;

        private static TextureBrush FrostBrush()
        {
            if (_frostBrush != null) return _frostBrush;

            const int size = 96;
            Bitmap noise = new Bitmap(size, size);
            Random rnd = new Random(20260726);      // 固定种子：每次运行颗粒一致，不会闪烁
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int v = rnd.Next(0, 100);
                    if (v < 62) { noise.SetPixel(x, y, Color.Transparent); continue; }
                    bool light = v % 2 == 0;
                    int alpha = 6 + (v % 9);
                    noise.SetPixel(x, y, light
                        ? Color.FromArgb(alpha, 255, 255, 255)
                        : Color.FromArgb(alpha, 90, 130, 110));
                }
            }
            _frostBrush = new TextureBrush(noise, WrapMode.Tile);
            return _frostBrush;
        }

        /// <summary>
        /// 毛玻璃底色单独放在背景层，不与标题栏、边框混在一起画。
        /// 这样透明子控件（进度条）向父窗口索要背景时，拿到的只有这层底色，
        /// 不会把标题文字一并复制进去。
        /// </summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle full = new Rectangle(0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));

            // 1) 浅绿底：顶部略深、往下收白
            using (LinearGradientBrush bg = new LinearGradientBrush(
                full, Color.FromArgb(214, 240, 226), Color.FromArgb(246, 252, 248),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(bg, full);
            }

            // 2) 左上角一团柔光，让平面看起来有厚度
            using (GraphicsPath glow = new GraphicsPath())
            {
                glow.AddEllipse(-full.Width / 3, -full.Height / 2, full.Width, full.Height);
                using (PathGradientBrush brush = new PathGradientBrush(glow))
                {
                    brush.CenterColor = Color.FromArgb(120, 255, 255, 255);
                    brush.SurroundColors = new Color[] { Color.Transparent };
                    g.FillPath(brush, glow);
                }
            }

            // 3) 细噪点：磨砂质感的关键，没有它就只是一块纯色渐变
            g.FillRectangle(FrostBrush(), full);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            DrawCaption(g);

            // 极窄边：1 像素浅绿描边，贴着窗口最外圈
            using (Pen pen = new Pen(EdgeLine, 1f))
            {
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        private void DrawCaption(Graphics g)
        {
            using (Font titleFont = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, "Codex 桌面版 MSIX 更新", titleFont,
                    new Rectangle(S(PadDip), 0, S(400), CaptionHeight), Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }

            // 标题栏底部一条极细分隔线
            using (Pen pen = new Pen(Color.FromArgb(150, EdgeLine)))
            {
                g.DrawLine(pen, 1, CaptionHeight, ClientSize.Width - 2, CaptionHeight);
            }

            DrawCaptionButton(g, _minRect, 0);
            DrawCaptionButton(g, _closeRect, 1);
        }

        private static Image BrandLogo()
        {
            if (_brandLogo != null) return _brandLogo;
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("Blues19.CodexInstaller.WeChatLogo.png"))
                {
                    if (stream == null) return null;
                    using (Image source = Image.FromStream(stream))
                        _brandLogo = new Bitmap(source);
                }
            }
            catch { }
            return _brandLogo;
        }

        private void DrawCaptionButton(Graphics g, Rectangle r, int index)
        {
            if (_hotButton == index)
            {
                using (SolidBrush b = new SolidBrush(index == 1
                    ? Color.FromArgb(232, 88, 80)
                    : Color.FromArgb(70, 122, 197, 155)))
                {
                    g.FillRectangle(b, r);
                }
            }

            Color glyph = (_hotButton == index && index == 1) ? Color.White : Ink;
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;
            int arm = S(5);
            using (Pen pen = new Pen(glyph, Math.Max(1.3f, _scale)))
            {
                if (index == 0)
                {
                    g.DrawLine(pen, cx - arm - S(1), cy, cx + arm + S(1), cy);
                }
                else
                {
                    g.DrawLine(pen, cx - arm, cy - arm, cx + arm, cy + arm);
                    g.DrawLine(pen, cx + arm, cy - arm, cx - arm, cy + arm);
                }
            }
        }

        private void LayoutCaptionButtons()
        {
            int w = S(46), h = CaptionHeight - 1;
            _closeRect = new Rectangle(ClientSize.Width - 1 - w, 1, w, h);
            _minRect = new Rectangle(_closeRect.X - w, 1, w, h);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCaptionButtons();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hot = -1;
            if (_minRect.Contains(e.Location)) hot = 0;
            else if (_closeRect.Contains(e.Location)) hot = 1;
            if (hot != _hotButton)
            {
                _hotButton = hot;
                Invalidate(new Rectangle(_minRect.X, 0, ClientSize.Width - _minRect.X, CaptionHeight));
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hotButton != -1)
            {
                _hotButton = -1;
                Invalidate(new Rectangle(_minRect.X, 0, ClientSize.Width - _minRect.X, CaptionHeight));
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_minRect.Contains(e.Location)) WindowState = FormWindowState.Minimized;
            else if (_closeRect.Contains(e.Location)) Close();
        }

        protected override void WndProc(ref Message m)
        {
            // 无边框窗口自己回答“鼠标落在哪”，拖动与缩放就还是系统原生行为
            if (m.Msg == HitTest.WM_NCHITTEST)
            {
                Point screen = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                Point p = PointToClient(screen);
                if (_minRect.Contains(p) || _closeRect.Contains(p))
                {
                    m.Result = (IntPtr)1;    // HTCLIENT：让按钮自己收到点击
                    return;
                }

                int? area = HitTest.Resolve(this, m, CaptionHeight,
                    S(GripDip), WindowState == FormWindowState.Normal);
                if (area.HasValue)
                {
                    m.Result = (IntPtr)area.Value;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        // ---------------- 界面搭建 ----------------

        private void BuildUi()
        {
            SuspendLayout();

            Text = "Codex 用量与更新助手 · Codex MSIX 更新";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(243, 251, 246);

            // 缩放全部自己算，不交给 WinForms 的 AutoScale。
            // 试过 AutoScaleMode.Font：字号按点数随 DPI 变大，控件坐标却由另一套规则缩放，
            // 两边对不齐就会出现「文字被裁掉」或「控件跑到窗口外面」。
            // 这里统一用 S() 把设计坐标乘上 DPI 比例，字号仍用点数（本身就随 DPI 走），
            // 两者刚好同步，换任何缩放比例的屏幕表现都一致。
            AutoScaleMode = AutoScaleMode.None;
            _scale = DetectScale();
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            ClientSize = new Size(S(900), S(692));
            MinimumSize = new Size(S(760), S(600));
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            try { Icon = IconFactory.Create(); }
            catch { }
            LayoutCaptionButtons();

            int pad = S(PadDip);
            int contentWidth = ClientSize.Width - pad * 2;

            int brandY = CaptionHeight + S(10);
            int brandH = S(58);
            Controls.Add(NewBrandPanel(pad, brandY, contentWidth, brandH));

            int top = brandY + brandH + S(10);
            Label sub = NewLabel("直连微软官方分发服务器获取离线安装包，无需浏览器、无需 Node.js",
                pad + S(2), top, 9F, FontStyle.Regular, Muted);
            Controls.Add(sub);

            _dirLabel = NewClippedLabel("安装包保存到桌面：" + Paths.PackageDir, pad + S(2), top + S(24),
                contentWidth - S(4), 8.5F, FontStyle.Regular, Muted);
            _dirLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_dirLabel);

            // 状态卡片。四列等宽排布，列宽由卡片宽度算出来，
            // 这样窗口变宽或系统字号变大都不会互相挤压。
            int cardY = top + S(52);
            int cardH = S(112);
            Panel card = NewCard(pad, cardY, contentWidth, cardH);
            Controls.Add(card);

            int colPad = S(18);
            int colW = (contentWidth - colPad * 2) / 4;
            string[] captions = { "当前状态", "已安装版本", "最新版本", "安装包大小" };
            Label[] values = new Label[4];
            for (int i = 0; i < 4; i++)
            {
                int cx = colPad + colW * i;
                card.Controls.Add(NewLabel(captions[i], cx, S(12), 8.5F, FontStyle.Regular, Muted));
                float size = i == 0 ? 14F : 11.5F;
                values[i] = NewClippedLabel(i == 0 ? "就绪" : (i == 1 ? "检测中…" : "—"),
                    cx, S(34), colW - S(8), size, FontStyle.Bold, Ink);
                card.Controls.Add(values[i]);
            }
            _stateValue = values[0];
            _installedValue = values[1];
            _latestValue = values[2];
            _sizeValue = values[3];

            _hint = NewClippedLabel("正在准备…", colPad, S(78), contentWidth - colPad * 2, 8.5F, FontStyle.Regular, Muted);
            _hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(_hint);

            // 进度条
            _progress = new ProgressPanel();
            _progress.Location = new Point(pad, cardY + cardH + S(12));
            _progress.Size = new Size(contentWidth, S(30));
            _progress.BackColor = Color.Transparent;
            _progress.TrackColor = Color.FromArgb(216, 238, 225);
            _progress.BarColor = Primary;
            _progress.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
            _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_progress);

            // 操作按钮：按内容宽度自适应，整行铺满卡片宽度，
            // 避免换个语言或字号后按钮被挤出窗口。
            int y = cardY + cardH + S(52);
            string[] labels = { "一键更新", "检查更新", "仅下载", "安装本地包", "打开桌面", "打开日志", "网络设置", "取消" };
            Color[] colors = { Primary, Sage, Teal, Steel, Stone, Stone, Stone, Danger };
            EventHandler[] handlers = {
                delegate { StartWork("update"); },
                delegate { StartWork("check"); },
                delegate { StartWork("download"); },
                delegate { StartWork("install-local"); },
                delegate { OpenPath(Paths.PackageDir); },
                delegate { OpenPath(_log.LogPath); },
                delegate { ShowNetworkDialog(); },
                delegate { CancelWork(); }
            };
            int gap = S(8);
            int[] widths = new int[labels.Length];
            int totalWidth = 0;
            using (Graphics g = CreateGraphics())
            {
                for (int i = 0; i < labels.Length; i++)
                {
                    widths[i] = (int)Math.Ceiling(g.MeasureString(labels[i], Font).Width) + S(28);
                    totalWidth += widths[i];
                }
            }
            // 把剩余空间按比例摊给每个按钮，正好占满一行
            int spare = contentWidth - totalWidth - gap * (labels.Length - 1);
            Button[] buttons = new Button[labels.Length];
            int x = pad;
            for (int i = 0; i < labels.Length; i++)
            {
                int extra = spare > 0 ? spare / labels.Length : 0;
                int w = Math.Max(S(56), widths[i] + extra);
                if (i == labels.Length - 1) w = Math.Max(S(56), pad + contentWidth - x);
                buttons[i] = NewButton(labels[i], x, y, w, S(36), colors[i]);
                buttons[i].Click += handlers[i];
                Controls.Add(buttons[i]);
                x += w + gap;
            }
            _btnUpdate = buttons[0]; _btnCheck = buttons[1]; _btnDownload = buttons[2];
            _btnInstallLocal = buttons[3]; _btnFolder = buttons[4]; _btnOpenLog = buttons[5];
            _btnNetwork = buttons[6]; _btnCancel = buttons[7];
            _btnCancel.Enabled = false;

            _forceRedownload = new CheckBox();
            _forceRedownload.Text = "强制重新下载（忽略本地已有安装包）";
            _forceRedownload.ForeColor = Muted;
            _forceRedownload.BackColor = Color.Transparent;
            _forceRedownload.AutoSize = true;
            _forceRedownload.FlatStyle = FlatStyle.Standard;
            _forceRedownload.Location = new Point(pad + S(2), y + S(46));
            Controls.Add(_forceRedownload);

            Label logTitle = NewLabel("运行日志", pad, y + S(72), 10F, FontStyle.Bold, Ink);
            Controls.Add(logTitle);

            _logBox = new RichTextBox();
            _logBox.Location = new Point(pad, y + S(98));
            _logBox.Size = new Size(contentWidth, Math.Max(S(80), ClientSize.Height - (y + S(98)) - pad));
            _logBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _logBox.ReadOnly = true;
            _logBox.BorderStyle = BorderStyle.None;
            _logBox.BackColor = Color.FromArgb(14, 31, 23);      // 偏绿的深色，和浅绿玻璃同一色系
            _logBox.ForeColor = Color.FromArgb(214, 232, 222);
            _logBox.Font = new Font("Microsoft YaHei UI", 9F);
            _logBox.WordWrap = true;
            _logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            _logBox.DetectUrls = false;
            _logBox.HideSelection = false;
            Controls.Add(_logBox);

            ResumeLayout(false);
            PerformLayout();
            LayoutCaptionButtons();

            FormClosing += OnFormClosing;
            Shown += OnShown;
        }

        private static Panel NewCard(int x, int y, int w, int h)
        {
            Panel p = new Panel();
            p.Location = new Point(x, y);
            p.Size = new Size(w, h);
            p.BackColor = Card;
            p.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            p.Paint += delegate (object s, PaintEventArgs e)
            {
                Rectangle r = new Rectangle(0, 0, Math.Max(1, p.Width), Math.Max(1, p.Height));
                // 卡片本身也做一点由白到浅绿的过渡，压在磨砂底上像一块贴上去的玻璃板
                using (LinearGradientBrush b = new LinearGradientBrush(
                    r, Color.FromArgb(253, 255, 254), Color.FromArgb(240, 249, 244),
                    LinearGradientMode.Vertical))
                {
                    e.Graphics.FillRectangle(b, r);
                }
                using (Pen pen = new Pen(Hair))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
                }
            };
            return p;
        }

        private Panel NewBrandPanel(int x, int y, int w, int h)
        {
            Panel p = new Panel();
            p.Location = new Point(x, y);
            p.Size = new Size(w, h);
            p.BackColor = Color.Transparent;
            p.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            p.Paint += delegate (object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, Math.Max(1, p.Width), Math.Max(1, p.Height));
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    r, Color.FromArgb(24, 42, 31), Color.FromArgb(35, 66, 47),
                    LinearGradientMode.Horizontal))
                    g.FillRectangle(bg, r);
                using (Pen edge = new Pen(Color.FromArgb(74, 145, 102)))
                    g.DrawRectangle(edge, 0, 0, p.Width - 1, p.Height - 1);

                int logoSize = S(44);
                int logoX = S(12);
                int logoY = (p.Height - logoSize) / 2;
                Image logo = BrandLogo();
                if (logo != null)
                {
                    GraphicsState state = g.Save();
                    using (GraphicsPath clip = new GraphicsPath())
                    {
                        clip.AddEllipse(logoX, logoY, logoSize, logoSize);
                        g.SetClip(clip);
                        g.DrawImage(logo, new Rectangle(logoX, logoY, logoSize, logoSize));
                    }
                    g.Restore(state);
                    using (Pen border = new Pen(Color.FromArgb(224, 177, 74), Math.Max(1.2f, _scale)))
                        g.DrawEllipse(border, logoX, logoY, logoSize, logoSize);
                }

                int textX = logoX + logoSize + S(14);
                int textWidth = Math.Max(S(180), p.Width - textX - S(358));
                using (Font nameFont = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold))
                    TextRenderer.DrawText(g, "拾玖说跨境AI", nameFont,
                        new Rectangle(textX, S(8), textWidth, S(24)),
                        Color.FromArgb(244, 224, 168),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                using (Font authorFont = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular))
                    TextRenderer.DrawText(g, "作者：拾玖Blues  ·  Blues19 开源工具", authorFont,
                        new Rectangle(textX, S(31), textWidth, S(20)),
                        Color.FromArgb(202, 225, 211),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };

            int updateButtonW = S(104);
            int updateRight = S(12);
            int updateStatusW = S(220);
            int updateGap = S(10);

            _installerUpdateStatus = NewClippedLabel(
                "MSIX 官方更新 · 下载校验",
                w - updateRight - updateButtonW - updateGap - updateStatusW,
                S(19), updateStatusW, 8.5F, FontStyle.Regular, Color.FromArgb(202, 225, 211));
            _installerUpdateStatus.TextAlign = ContentAlignment.MiddleRight;
            _installerUpdateStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            p.Controls.Add(_installerUpdateStatus);

            _btnInstallerUpdate = NewButton("查看新版",
                w - updateRight - updateButtonW, S(13), updateButtonW, S(32), Amber);
            _btnInstallerUpdate.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnInstallerUpdate.Visible = false;
            _btnInstallerUpdate.Click += delegate { OpenInstallerUpdatePage(); };
            p.Controls.Add(_btnInstallerUpdate);
            return p;
        }

        /// <summary>
        /// 固定宽度 + 省略号，适合可能很长的路径类文本。
        /// </summary>
        private static Label NewClippedLabel(string text, int x, int y, int w, float size, FontStyle style, Color color)
        {
            Label l = NewLabel(text, x, y, size, style, color);
            l.AutoSize = false;
            l.AutoEllipsis = true;
            l.Size = new Size(w, TextHeight(l.Font));
            return l;
        }

        /// <summary>
        /// 自动按内容撑开。刻意不写死高度：字号会随系统 DPI 与字体设置变化，
        /// 写死高度换台机器就会把字切掉半截。
        /// </summary>
        private static Label NewLabel(string text, int x, int y, float size, FontStyle style, Color color)
        {
            Label l = new Label();
            l.Font = new Font("Microsoft YaHei UI", size, style);
            l.Text = text;
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            return l;
        }

        private static int TextHeight(Font font)
        {
            return (int)Math.Ceiling(font.GetHeight()) + 6;
        }

        private static Button NewButton(string text, int x, int y, int w, int h, Color back)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = back;
            b.ForeColor = Color.White;
            b.Font = new Font("Microsoft YaHei UI", 9.5F);
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.18f);
            b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.05f);
            b.EnabledChanged += delegate
            {
                b.ForeColor = b.Enabled ? Color.White : Color.FromArgb(238, 246, 241);
                b.BackColor = b.Enabled ? back : Color.FromArgb(178, 199, 187);
            };
            return b;
        }

        // ---------------- 网络设置 ----------------

        private void ShowNetworkDialog()
        {
            Settings s = Settings.Current;

            using (Form dlg = new Form())
            {
                dlg.Text = "网络设置";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.ClientSize = new Size(S(470), S(276));
                dlg.BackColor = Color.FromArgb(247, 252, 249);
                dlg.Font = Font;

                Label tip = NewLabel(
                    "微软的下载 CDN（dl.delivery.mp.microsoft.com）在部分网络下连不上，\n" +
                    "而查询接口却是通的。如果下载一直超时，在这里指定代理即可。",
                    S(16), S(12), 8.5F, FontStyle.Regular, Muted);
                dlg.Controls.Add(tip);

                RadioButton rSystem = NewRadio("跟随系统代理设置（默认）", S(18), S(64));
                RadioButton rAuto = NewRadio("自动探测本地代理（扫描常见端口）", S(18), S(94));
                RadioButton rManual = NewRadio("手动指定：", S(18), S(124));
                RadioButton rNone = NewRadio("强制直连，不使用代理", S(18), S(186));
                dlg.Controls.Add(rSystem); dlg.Controls.Add(rAuto);
                dlg.Controls.Add(rManual); dlg.Controls.Add(rNone);

                TextBox box = new TextBox();
                box.Location = new Point(S(122), S(122));
                box.Size = new Size(S(196), S(26));
                box.Text = s.ManualProxy;
                dlg.Controls.Add(box);

                Label sample = NewLabel("例如 127.0.0.1:7897", S(122), S(154), 8.5F, FontStyle.Regular, Muted);
                dlg.Controls.Add(sample);

                Button detect = NewButton("探测", S(330), S(120), S(80), S(28), Sage);
                detect.Click += delegate
                {
                    System.Collections.Generic.List<string> found = Settings.DetectAllLocalProxies();
                    if (found.Count == 0)
                    {
                        MessageBox.Show(dlg, "没有在常见端口上发现本地代理。", "探测结果",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    box.Text = found[0];
                    rManual.Checked = true;
                    MessageBox.Show(dlg, "发现本地代理：" + string.Join("、", found.ToArray()) +
                        "\n已填入第一个。", "探测结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                dlg.Controls.Add(detect);

                switch (s.Mode)
                {
                    case ProxyMode.Auto: rAuto.Checked = true; break;
                    case ProxyMode.Manual: rManual.Checked = true; break;
                    case ProxyMode.None: rNone.Checked = true; break;
                    default: rSystem.Checked = true; break;
                }

                Button ok = NewButton("保存", S(254), S(226), S(96), S(32), Primary);
                ok.DialogResult = DialogResult.OK;
                dlg.Controls.Add(ok);

                Button cancel = NewButton("取消", S(358), S(226), S(96), S(32), Stone);
                cancel.DialogResult = DialogResult.Cancel;
                dlg.Controls.Add(cancel);

                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                if (rAuto.Checked) s.Mode = ProxyMode.Auto;
                else if (rManual.Checked) s.Mode = ProxyMode.Manual;
                else if (rNone.Checked) s.Mode = ProxyMode.None;
                else s.Mode = ProxyMode.System;
                s.ManualProxy = box.Text.Trim();
                s.Save();

                _log.Step("网络设置已更新：" + s.Describe());
            }
        }

        private RadioButton NewRadio(string text, int x, int y)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.Location = new Point(x, y);
            r.AutoSize = true;
            r.ForeColor = Ink;
            return r;
        }

        // ---------------- 日志渲染 ----------------

        private void OnLogAppended(LogEntry entry)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(new Action<LogEntry>(AppendLogLine), entry);
                else AppendLogLine(entry);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void AppendLogLine(LogEntry entry)
        {
            if (_logBox == null || _logBox.IsDisposed) return;

            Color color;
            switch (entry.Level)
            {
                case LogLevel.Error: color = Color.FromArgb(252, 165, 165); break;
                case LogLevel.Warn: color = Color.FromArgb(253, 224, 71); break;
                case LogLevel.Success: color = Color.FromArgb(134, 239, 172); break;
                case LogLevel.Step: color = Color.FromArgb(147, 214, 253); break;
                default: color = Color.FromArgb(200, 218, 208); break;
            }

            // 日志很长时裁掉最前面一段，避免 RichTextBox 越来越卡
            if (_logBox.TextLength > 200000)
            {
                _logBox.SelectionStart = 0;
                _logBox.SelectionLength = 80000;
                _logBox.SelectedText = string.Empty;
            }

            bool atBottom = IsScrolledToBottom();
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.SelectionColor = Color.FromArgb(118, 146, 132);
            _logBox.AppendText(entry.Time.ToString("HH:mm:ss") + "  ");
            _logBox.SelectionColor = color;
            _logBox.AppendText(entry.Message + Environment.NewLine);

            if (atBottom)
            {
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
        }

        private bool IsScrolledToBottom()
        {
            try
            {
                int firstVisible = _logBox.GetLineFromCharIndex(_logBox.GetCharIndexFromPosition(new Point(2, _logBox.Height - 4)));
                return firstVisible >= _logBox.GetLineFromCharIndex(_logBox.TextLength) - 2;
            }
            catch
            {
                return true;
            }
        }

        // ---------------- 状态与线程 ----------------

        private void UiInvoke(Action action)
        {
            if (IsDisposed || Disposing) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void SetState(string text, Color color, string hint)
        {
            UiInvoke(delegate
            {
                _stateValue.Text = text;
                _stateValue.ForeColor = color;
                if (hint != null) _hint.Text = hint;
            });
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            UiInvoke(delegate
            {
                _btnUpdate.Enabled = !busy;
                _btnCheck.Enabled = !busy;
                _btnDownload.Enabled = !busy;
                _btnInstallLocal.Enabled = !busy;
                _btnNetwork.Enabled = !busy;
                _forceRedownload.Enabled = !busy;
                _btnCancel.Enabled = busy;
                if (!busy) _progress.Indeterminate = false;
            });
        }

        private void StartWork(string action)
        {
            if (_busy) return;
            CancellationTokenSource previous = _cancel;
            _cancel = new CancellationTokenSource();
            if (previous != null) { try { previous.Dispose(); } catch { } }
            SetBusy(true);
            _mode = action;

            _worker = new Thread(delegate ()
            {
                try
                {
                    RunAction(action, _cancel.Token);
                }
                catch (OperationCanceledException)
                {
                    _finalState = "cancelled";
                    _log.Warn("操作已被用户取消。");
                    SetState("已取消", Amber, "操作已取消，可以重新发起。");
                    UiInvoke(delegate { _progress.Caption = "已取消"; });
                    WriteStatus("cancelled", "操作已取消");
                }
                catch (Exception ex)
                {
                    _finalState = "failed";
                    foreach (string line in ex.Message.Split('\n')) _log.Error(line.TrimEnd());
                    if (ex.InnerException != null) _log.Error("底层原因：" + ex.InnerException.Message);
                    SetState("失败", Danger, FirstLine(ex.Message));
                    UiInvoke(delegate
                    {
                        _progress.Indeterminate = false;
                        _progress.BarColor = Danger;
                        _progress.Caption = "失败";
                    });
                    WriteStatus("failed", ex.Message);
                }
                finally
                {
                    SetBusy(false);
                }
            });
            _worker.IsBackground = true;
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int i = text.IndexOf('\n');
            return i < 0 ? text : text.Substring(0, i).TrimEnd();
        }

        private void CancelWork()
        {
            CancellationTokenSource cts = _cancel;
            if (cts != null && !cts.IsCancellationRequested)
            {
                _log.Warn("正在取消…");
                try { cts.Cancel(); }
                catch { }
            }
        }

        private void RunAction(string action, CancellationToken cancel)
        {
            UiInvoke(delegate { _progress.BarColor = Primary; });

            switch (action)
            {
                case "check":
                    DoCheck(cancel);
                    break;
                case "download":
                    // 「仅下载」不看本机是不是已经最新——用户点它就是想要那个安装包文件，
                    // 常见用途是拿去给另一台没网的电脑装。这里只用检查来确定最新版本是哪个。
                    DoCheck(cancel);
                    DoDownload(cancel);
                    break;
                case "install-local":
                    DoInstallLocal(cancel);
                    break;
                case "update":
                default:
                    if (DoCheck(cancel))
                    {
                        string path = DoDownload(cancel);
                        DoInstall(path, cancel);
                    }
                    break;
            }
        }

        // ---------------- 具体流程 ----------------

        private bool DoCheck(CancellationToken cancel)
        {
            _finalState = "running";
            SetState("检查中", Primary, "正在读取本机已安装版本…");
            UiInvoke(delegate { _progress.Indeterminate = true; _progress.Caption = "正在查询"; });
            WriteStatus("running", "正在检查更新");

            _installed = AppxInstaller.GetInstalled(_log);
            string installedText = _installed != null ? _installed.Version.ToString() : "未安装";
            _log.Step("本机已安装版本：" + installedText);
            UiInvoke(delegate { _installedValue.Text = installedText; });

            SetState("检查中", Primary, "正在向微软官方服务器查询最新版本…");
            _latest = StoreApi.FindLatestPackage(_log, cancel);

            UiInvoke(delegate
            {
                _latestValue.Text = _latest.VersionText;
                _sizeValue.Text = _latest.SizeText;
                _progress.Indeterminate = false;
            });
            _log.Success("最新版本：" + _latest.VersionText + "（" + _latest.FileName + "，" + _latest.SizeText + "）");

            if (_installed != null && _latest.Version != null && _installed.Version >= _latest.Version)
            {
                _finalState = "success";
                SetState("已是最新", Good, "当前已安装 " + installedText + "，无需更新。");
                UiInvoke(delegate { _progress.Value = 1; _progress.Caption = "已是最新版本"; });
                _log.Success("当前已是最新版本，无需更新。");
                WriteStatus("success", "已是最新版本");
                return false;
            }

            // 检查本身已经成功了。不把状态改成 success 的话，
            // 只跑「检查更新」时归档出来的日志文件名会一直带 -running-，与 status.json 自相矛盾。
            _finalState = "success";
            SetState("有可用更新", Amber,
                _installed == null
                    ? "本机未安装 Codex，可直接点“一键更新”完成安装。"
                    : "可从 " + installedText + " 升级到 " + _latest.VersionText + "。");
            WriteStatus("success", "检测到可用版本 " + _latest.VersionText);
            return true;
        }

        private bool ReadForceRedownload()
        {
            if (IsDisposed) return false;
            try
            {
                if (InvokeRequired) return (bool)Invoke(new Func<bool>(ReadForceRedownload));
                return _forceRedownload.Checked;
            }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private string DoDownload(CancellationToken cancel)
        {
            if (_latest == null) throw new InvalidOperationException("请先执行检查更新。");

            string savePath = _latest.LocalPath(Paths.PackageDir);
            bool force = ReadForceRedownload();

            if (force && File.Exists(savePath))
            {
                _log.Warn("已勾选强制重新下载，正在删除本地旧文件。");
                Downloader.TryDelete(savePath);
            }

            if (!force && File.Exists(savePath))
            {
                FileInfo fi = new FileInfo(savePath);
                bool sizeOk = _latest.SizeBytes <= 0 || fi.Length == _latest.SizeBytes;
                if (sizeOk && Downloader.LooksLikeMsix(savePath))
                {
                    _log.Success("本地已存在完整安装包，跳过下载：" + fi.Name);
                    SetState("下载完成", Good, "已使用本地安装包：" + fi.Name);
                    UiInvoke(delegate { _progress.Value = 1; _progress.Caption = "已就绪（本地文件）"; });
                    return savePath;
                }
                _log.Warn(sizeOk
                    ? "本地文件不是有效的安装包，将重新下载。"
                    : "本地文件大小不符（" + Fmt.Size(fi.Length) + " ≠ " + _latest.SizeText + "），将重新下载。");
                Downloader.TryDelete(savePath);
            }

            Downloader.EnsurePathLength(savePath);
            Downloader.EnsureDiskSpace(Paths.PackageDir, _latest.SizeBytes, delegate (string m) { _log.Warn(m); });

            SetState("下载中", Primary, "正在下载 " + _latest.FileName);
            WriteStatus("running", "正在下载安装包", 0);
            _log.Step("开始下载：" + _latest.FileName);
            _log.Info("网络方式：" + Settings.Current.Describe());

            DateTime lastStatusWrite = DateTime.MinValue;
            Downloader downloader = new Downloader(
                delegate (string msg) { _log.Info(msg); },
                delegate (Downloader.Progress p)
                {
                    double ratio = p.Total > 0 ? (double)p.Downloaded / p.Total : 0;
                    int percent = (int)Math.Round(ratio * 100);
                    string caption = p.Total > 0
                        ? string.Format("{0}%   {1} / {2}   {3}   剩余 {4}",
                            percent, Fmt.Size(p.Downloaded), Fmt.Size(p.Total),
                            Fmt.Speed(p.SpeedBytesPerSec), Fmt.Eta(p.Total - p.Downloaded, p.SpeedBytesPerSec))
                        : string.Format("已下载 {0}   {1}", Fmt.Size(p.Downloaded), Fmt.Speed(p.SpeedBytesPerSec));

                    UiInvoke(delegate
                    {
                        _progress.Value = ratio;
                        _progress.Caption = caption;
                    });

                    if ((DateTime.UtcNow - lastStatusWrite).TotalSeconds >= 1)
                    {
                        lastStatusWrite = DateTime.UtcNow;
                        WriteStatus("running", caption, percent);
                    }
                },
                cancel);

            StoreApi.PrepareDownload(_latest, _log, cancel);

            DateTime started = DateTime.UtcNow;
            downloader.Download(_latest.Url, savePath, _latest.SizeBytes,
                _latest.DigestBase64, _latest.DigestAlgorithm);
            TimeSpan cost = DateTime.UtcNow - started;

            _log.Success("下载完成，用时 " + Fmt.Duration(cost) + "：" + savePath);
            SetState("下载完成", Good, "安装包已保存到桌面。");
            UiInvoke(delegate { _progress.Value = 1; _progress.Caption = "下载完成"; });
            WriteStatus("success", "下载完成", 100);
            _finalState = "success";
            return savePath;
        }

        private void DoInstall(string packagePath, CancellationToken cancel)
        {
            SetState("安装中", Primary, "正在调用系统部署服务安装，请稍候…");
            UiInvoke(delegate { _progress.Indeterminate = true; _progress.Caption = "正在安装"; });
            WriteStatus("running", "正在安装");
            _log.Step("开始安装：" + Path.GetFileName(packagePath));

            bool deferred = AppxInstaller.Install(packagePath, _log,
                delegate (int percent)
                {
                    UiInvoke(delegate
                    {
                        _progress.Indeterminate = false;
                        _progress.Value = percent / 100.0;
                        _progress.Caption = "正在安装 " + percent + "%";
                    });
                },
                AskInUseDecision,
                cancel);

            InstalledPackage now = AppxInstaller.GetInstalled(_log);
            string version = now != null ? now.Version.ToString() : (_latest != null ? _latest.VersionText : "");

            _finalState = "success";
            if (deferred)
            {
                _log.Success("新版本已铺设完成，完全退出 Codex 后再次打开即为新版本。");
                SetState("等待生效", Amber, "新版本已就位，完全退出 Codex 后重新打开即可生效。");
                UiInvoke(delegate
                {
                    _progress.Indeterminate = false;
                    _progress.Value = 1;
                    _progress.Caption = "已就位，待 Codex 退出后生效";
                });
                WriteStatus("success", "已铺设，等待 Codex 退出后生效", 100);
                return;
            }

            _log.Success("安装完成" + (version.Length > 0 ? "，当前版本 " + version : "") + "。安装包已保留在桌面。");
            SetState("安装完成", Good, "Codex 已更新到 " + version + "。");
            UiInvoke(delegate
            {
                _progress.Indeterminate = false;
                _progress.Value = 1;
                _progress.Caption = "安装完成";
                if (now != null) _installedValue.Text = now.Version.ToString();
            });
            WriteStatus("success", "安装完成", 100);
        }

        /// <summary>
        /// Codex 正在运行时的三选一。这是现实中最常见的安装失败原因，
        /// 与其直接报错，不如把选择权交给用户。
        /// </summary>
        private InUseDecision AskInUseDecision()
        {
            if (IsDisposed) return InUseDecision.Abort;
            try
            {
                if (InvokeRequired) return (InUseDecision)Invoke(new Func<InUseDecision>(AskInUseDecision));
            }
            catch (ObjectDisposedException) { return InUseDecision.Abort; }
            catch (InvalidOperationException) { return InUseDecision.Abort; }

            DialogResult answer = MessageBox.Show(this,
                "Codex 正在运行，系统无法直接替换它的文件。\n\n" +
                "【是】立即关闭 Codex 并完成安装（未保存的内容可能丢失）\n" +
                "【否】先装好，等你下次完全退出 Codex 后自动生效\n" +
                "【取消】放弃本次安装",
                "Codex 正在运行", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer == DialogResult.Yes) return InUseDecision.ForceShutdown;
            if (answer == DialogResult.No) return InUseDecision.DeferToNextExit;
            return InUseDecision.Abort;
        }

        private void DoInstallLocal(CancellationToken cancel)
        {
            string path = FindLocalPackage();
            if (path == null)
            {
                throw new FileNotFoundException(
                    "桌面中没有找到可用的 Codex 安装包（.msixbundle / .msix）。请先执行“仅下载”或“一键更新”。");
            }
            _log.Step("使用本地安装包：" + path + "（" + Fmt.Size(new FileInfo(path).Length) + "）");
            DoInstall(path, cancel);
        }

        /// <summary>
        /// 挑本地可安装的包。刻意不按“文件最大”来挑——下载损坏时反而可能产出超大文件。
        /// 先用文件头把明显损坏的排除掉，再按文件名里的版本号取最新。
        /// </summary>
        private string FindLocalPackage()
        {
            try
            {
                string best = null;
                Version bestVersion = null;
                DateTime bestTime = DateTime.MinValue;
                int rejected = 0;

                foreach (string file in Directory.GetFiles(Paths.PackageDir))
                {
                    string name = Path.GetFileName(file);
                    string lower = name.ToLowerInvariant();
                    if (lower.IndexOf("codex", StringComparison.Ordinal) < 0) continue;
                    if (!lower.EndsWith(".msixbundle", StringComparison.Ordinal) &&
                        !lower.EndsWith(".msix", StringComparison.Ordinal) &&
                        !lower.EndsWith(".appxbundle", StringComparison.Ordinal)) continue;

                    if (!Downloader.LooksLikeMsix(file)) { rejected++; continue; }

                    InstalledPackage parsed = AppxInstaller.ParseFullName(Path.GetFileNameWithoutExtension(name));
                    Version version = parsed != null ? parsed.Version : null;
                    DateTime time = new FileInfo(file).LastWriteTimeUtc;

                    bool better;
                    if (best == null) better = true;
                    else if (version != null && bestVersion != null) better = version > bestVersion;
                    else if (version != null) better = true;
                    else if (bestVersion != null) better = false;
                    else better = time > bestTime;

                    if (better) { best = file; bestVersion = version; bestTime = time; }
                }

                if (rejected > 0)
                    _log.Warn("目录中有 " + rejected + " 个损坏的安装包文件已被跳过。");
                return best;
            }
            catch
            {
                return null;
            }
        }

        private void WriteStatus(string state, string message)
        {
            WriteStatus(state, message, -1);
        }

        private void WriteStatus(string state, string message, int percent)
        {
            StatusFile.Write(_mode, _mode, state, message,
                _latest != null ? _latest.FileName : null,
                _latest != null ? _latest.SizeText : null,
                _latest != null ? _latest.LocalPath(Paths.PackageDir) : null,
                percent);
        }

        private void OpenPath(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Process.Start("explorer.exe", "\"" + path + "\"");
                else if (File.Exists(path))
                {
                    ProcessStartInfo psi = new ProcessStartInfo(path);
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                else
                    MessageBox.Show(this, "路径不存在：" + path, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenInstallerUpdatePage()
        {
            string url = _installerUpdateUrl;
            if (!InstallerUpdateChecker.IsTrustedReleaseUrl(url)) return;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开 GitHub Release 页面失败：" + ex.Message,
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void StartInstallerUpdateCheck()
        {
            if (_installerUpdateCheckStarted) return;
            _installerUpdateCheckStarted = true;

            UiInvoke(delegate
            {
                _installerUpdateStatus.Text = "安装器 v" + Program.AppVersion + " · 正在检查…";
            });

            Thread checker = new Thread(delegate ()
            {
                try
                {
                    InstallerReleaseInfo release = InstallerUpdateChecker.Check(CancellationToken.None);
                    if (InstallerUpdateChecker.IsNewer(release.Version, Program.AppVersion))
                    {
                        _log.Warn("安装器发现新版本：" + release.TagName +
                                  "（当前 v" + Program.AppVersion + "）");
                        ShowInstallerUpdateAvailable(release.TagName, release.ReleaseUrl);
                    }
                    else
                    {
                        _log.Info("安装器已是最新版（v" + Program.AppVersion + "）。");
                        UiInvoke(delegate
                        {
                            _installerUpdateStatus.Text = "安装器 v" + Program.AppVersion + " · 已是最新版";
                            _installerUpdateStatus.ForeColor = Color.FromArgb(202, 225, 211);
                            _btnInstallerUpdate.Visible = false;
                        });
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("安装器更新检查暂时不可用（不影响使用）：" + FirstLine(ex.Message));
                    UiInvoke(delegate
                    {
                        _installerUpdateStatus.Text = "安装器 v" + Program.AppVersion + " · 暂无法检查";
                        _installerUpdateStatus.ForeColor = Color.FromArgb(202, 225, 211);
                        _btnInstallerUpdate.Visible = false;
                    });
                }
            });
            checker.IsBackground = true;
            checker.Name = "InstallerUpdateCheck";
            checker.Start();
        }

        private void ShowInstallerUpdateAvailable(string tagName, string releaseUrl)
        {
            UiInvoke(delegate
            {
                _installerUpdateUrl = releaseUrl;
                _installerUpdateStatus.Text = "v" + Program.AppVersion + " → " + tagName + " 可更新";
                _installerUpdateStatus.ForeColor = Color.FromArgb(255, 224, 138);
                _btnInstallerUpdate.Visible = true;
                _btnInstallerUpdate.BringToFront();
            });
        }

        /// <summary>离屏排版验证用，不发网络请求。</summary>
        internal void PreviewInstallerUpdate(string tagName)
        {
            ShowInstallerUpdateAvailable(tagName,
                InstallerUpdateChecker.RepositoryUrl + "/releases/tag/" + tagName);
        }

        // ---------------- 生命周期 ----------------

        private void OnShown(object sender, EventArgs e)
        {
            // 排版自检模式：只把界面画出来，不发起任何网络或安装动作
            if (_startupAction == "none") return;

            // 每次正常启动都单独检查一次安装器自身版本；失败不会阻断 Codex 的检查与安装。
            StartInstallerUpdateCheck();

            _log.Info(string.Format("界面：缩放比 {0:0.00}，窗口 {1}×{2}，工作区 {3}×{4}",
                _scale, Width, Height,
                Screen.FromControl(this).WorkingArea.Width, Screen.FromControl(this).WorkingArea.Height));
            _log.Info("工作目录：" + Paths.BaseDir);
            if (Paths.UsingFallbackDir)
                _log.Warn("程序所在目录不可写，已自动改用用户目录存放安装包与日志。");

            string action = _startupAction.Length > 0 ? _startupAction : "check";
            StartWork(action);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_busy)
            {
                DialogResult answer = MessageBox.Show(this,
                    "任务正在进行中，确定要退出吗？\n（下载进度会保留，下次可以断点续传。）",
                    "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                CancelWork();
                // 刻意不 Join 工作线程：它可能正卡在 Invoke 里等 UI 线程处理，
                // 而 UI 线程如果在这里 Join，两边就互相等成死锁（表现为关窗口卡住几秒）。
                // 工作线程是后台线程，进程退出时会自然结束，取消信号已经发出去了。
            }
            _log.Appended -= OnLogAppended;
            _log.Archive(_mode, _finalState);
        }
    }
}
