using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal sealed class FirstRunGuideForm : Form
    {
        private sealed class GuidePage
        {
            public readonly string Title;
            public readonly string Body;
            public readonly string Tip;

            public GuidePage(string title, string body, string tip)
            {
                Title = title;
                Body = body;
                Tip = tip;
            }
        }

        private static readonly GuidePage[] Pages =
        {
            new GuidePage(
                "看懂主用量条",
                "主条会显示套餐、周用量剩余、重置时间、重置券、累计 Token 和任务状态。额度数据来自本机 Codex。",
                "主用量正文只负责展示信息，不再绑定退出操作。"),
            new GuidePage(
                "查看 Tibo 重置预告",
                "点击雷达状态块可打开 Codex Runway 中文状态页；点击雷达右侧的 ↻ 可立即重新获取最新状态。",
                "临时网络失败会显示“网络重试中”，数据超过 30 小时未更新才会显示“雷达离线”。"),
            new GuidePage(
                "左键设置，右键更新",
                "左键齿轮可切换主题、字体、刷新频率和提醒；右键齿轮可查看版本、检查或下载更新，也可以安全退出程序。",
                "退出前会再次确认；下载更新不会静默安装。"),
            new GuidePage(
                "以后随时可以再看",
                "完成后，这份指引不会再次自动出现。需要回顾时，打开设置并点击“使用指引”即可。",
                "现在可以开始使用 Codex Usage Overlay 了。")
        };

        private const int LogicalWidth = 430;
        private const int LogicalHeight = 252;
        private const int ArrowHeight = 10;
        private const int LogicalGap = 4;
        private const int CornerRadius = 12;

        private readonly Panel contentPanel;
        private readonly Label sectionLabel;
        private readonly Label stepLabel;
        private readonly Label titleLabel;
        private readonly Label bodyLabel;
        private readonly Panel tipPanel;
        private readonly Label tipLabel;
        private readonly Button closeButton;
        private readonly Button skipButton;
        private readonly Button previousButton;
        private readonly Button nextButton;
        private Color bodyColor;
        private Color borderColor;
        private Color primaryTextColor;
        private Color secondaryTextColor;
        private Color accentColor;
        private Color actionTextColor;
        private Color tipColor;
        private bool arrowOnTop = true;
        private bool dismissedRaised;
        private int pageIndex;
        private float layoutScale = 1f;

        public event EventHandler Dismissed;

        internal Rectangle OffsetForHostMove(int horizontalOffset, int verticalOffset)
        {
            if ((horizontalOffset == 0 && verticalOffset == 0) || IsDisposed ||
                !IsHandleCreated)
                return Rectangle.Empty;

            return OverlayInteraction.OffsetBoundsForHostMove(
                Bounds, horizontalOffset, verticalOffset);
        }

        public FirstRunGuideForm()
            : this(new OverlaySettings())
        {
        }

        public FirstRunGuideForm(OverlaySettings settings)
        {
            Text = "Codex Usage Overlay 使用指引";
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(LogicalWidth, LogicalHeight);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            KeyPreview = true;
            DoubleBuffered = true;
            Font = UiRendering.CreateTextFont(
                "Microsoft YaHei UI", 9f, FontStyle.Regular);

            contentPanel = new Panel();
            contentPanel.Location = new Point(2, ArrowHeight + 2);
            contentPanel.Size = new Size(LogicalWidth - 4, LogicalHeight - ArrowHeight - 4);
            Controls.Add(contentPanel);

            sectionLabel = new Label();
            sectionLabel.Location = new Point(20, 15);
            sectionLabel.Size = new Size(112, 22);
            sectionLabel.Text = "●  使用指引";
            sectionLabel.TextAlign = ContentAlignment.MiddleLeft;
            sectionLabel.Font = UiRendering.CreateTextFont(
                "Microsoft YaHei UI", 9f, FontStyle.Bold);
            contentPanel.Controls.Add(sectionLabel);

            stepLabel = new Label();
            stepLabel.Location = new Point(326, 15);
            stepLabel.Size = new Size(58, 22);
            stepLabel.TextAlign = ContentAlignment.MiddleRight;
            stepLabel.Font = UiRendering.CreateTextFont(
                "Segoe UI", 9f, FontStyle.Bold);
            contentPanel.Controls.Add(stepLabel);

            closeButton = new Button();
            closeButton.Location = new Point(391, 11);
            closeButton.Size = new Size(25, 25);
            closeButton.Text = "×";
            closeButton.FlatStyle = FlatStyle.Flat;
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.Font = UiRendering.CreateTextFont(
                "Segoe UI", 11f, FontStyle.Bold);
            closeButton.TabStop = false;
            closeButton.Click += delegate { DismissGuide(); };
            contentPanel.Controls.Add(closeButton);

            titleLabel = new Label();
            titleLabel.Location = new Point(20, 44);
            titleLabel.Size = new Size(390, 30);
            titleLabel.Font = UiRendering.CreateTextFont(
                "Microsoft YaHei UI", 13.5f, FontStyle.Bold);
            titleLabel.TextAlign = ContentAlignment.MiddleLeft;
            contentPanel.Controls.Add(titleLabel);

            bodyLabel = new Label();
            bodyLabel.Location = new Point(20, 78);
            bodyLabel.Size = new Size(390, 50);
            bodyLabel.Font = UiRendering.CreateTextFont(
                "Microsoft YaHei UI", 9.5f, FontStyle.Regular);
            contentPanel.Controls.Add(bodyLabel);

            tipPanel = new Panel();
            tipPanel.Location = new Point(20, 134);
            tipPanel.Size = new Size(390, 42);
            contentPanel.Controls.Add(tipPanel);

            tipLabel = new Label();
            tipLabel.Location = new Point(12, 3);
            tipLabel.Size = new Size(366, 36);
            tipLabel.TextAlign = ContentAlignment.MiddleLeft;
            tipLabel.Font = UiRendering.CreateTextFont(
                "Microsoft YaHei UI", 8.8f, FontStyle.Bold);
            tipPanel.Controls.Add(tipLabel);

            skipButton = CreateFooterButton("跳过", new Point(20, 190), new Size(66, 30));
            skipButton.Click += delegate { DismissGuide(); };
            contentPanel.Controls.Add(skipButton);

            previousButton = CreateFooterButton("上一步", new Point(256, 190), new Size(72, 30));
            previousButton.Click += delegate
            {
                if (pageIndex > 0)
                {
                    pageIndex--;
                    RenderPage();
                }
            };
            contentPanel.Controls.Add(previousButton);

            nextButton = CreateFooterButton("下一步", new Point(336, 190), new Size(74, 30));
            nextButton.Click += delegate
            {
                if (pageIndex >= Pages.Length - 1)
                {
                    DismissGuide();
                    return;
                }
                pageIndex++;
                RenderPage();
            };
            contentPanel.Controls.Add(nextButton);

            AcceptButton = nextButton;
            CancelButton = skipButton;
            ApplyTheme(settings);
            RenderPage();
            ApplyBubbleRegion();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return parameters;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle body = BubbleBodyBounds;
            using (GraphicsPath rounded = RoundedRectangle(body, ScaleValue(CornerRadius)))
            using (Brush fill = new SolidBrush(bodyColor))
            using (Pen border = new Pen(borderColor, Math.Max(1f, DeviceDpi / 96f)))
            {
                e.Graphics.FillPath(fill, rounded);
                e.Graphics.DrawPath(border, rounded);
            }

            Point[] arrow = ArrowPoints;
            using (Brush fill = new SolidBrush(bodyColor))
            using (Pen border = new Pen(borderColor, Math.Max(1f, DeviceDpi / 96f)))
            {
                e.Graphics.FillPolygon(fill, arrow);
                e.Graphics.DrawLines(border, arrow);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
                RaiseDismissed();
            base.OnFormClosing(e);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ApplyBubbleRegion();
        }

        public void ResetToFirstPage()
        {
            pageIndex = 0;
            RenderPage();
        }

        public void ApplyTheme(OverlaySettings settings)
        {
            string theme = settings == null ? "NeonBlue" : settings.Theme;
            if (theme == "FrostedGlass")
            {
                bodyColor = Color.FromArgb(242, 248, 252);
                borderColor = Color.FromArgb(108, 155, 188);
                primaryTextColor = Color.FromArgb(27, 52, 73);
                secondaryTextColor = Color.FromArgb(73, 101, 122);
                accentColor = Color.FromArgb(35, 143, 199);
                actionTextColor = Color.White;
                tipColor = Color.FromArgb(222, 238, 247);
            }
            else if (theme == "LightCard")
            {
                bodyColor = Color.FromArgb(250, 252, 254);
                primaryTextColor = Color.FromArgb(54, 64, 76);
                secondaryTextColor = Color.FromArgb(101, 112, 125);
                accentColor = Color.FromArgb(121, 83, 239);
                borderColor = Color.FromArgb(214, 221, 230);
                tipColor = Color.FromArgb(244, 247, 251);
            }
            else if (theme == "OrangeGradient")
            {
                bodyColor = Color.FromArgb(179, 72, 82);
                borderColor = Color.FromArgb(255, 212, 132);
                primaryTextColor = Color.FromArgb(255, 252, 241);
                secondaryTextColor = Color.FromArgb(255, 232, 218);
                accentColor = Color.FromArgb(246, 159, 51);
                actionTextColor = Color.FromArgb(80, 35, 14);
                tipColor = Color.FromArgb(151, 55, 68);
            }
            else if (theme == "PinkGradient")
            {
                bodyColor = Color.FromArgb(145, 66, 166);
                borderColor = Color.FromArgb(255, 187, 228);
                primaryTextColor = Color.FromArgb(255, 250, 254);
                secondaryTextColor = Color.FromArgb(245, 218, 247);
                accentColor = Color.FromArgb(237, 103, 180);
                actionTextColor = Color.FromArgb(67, 20, 61);
                tipColor = Color.FromArgb(122, 50, 144);
            }
            else if (theme == "RainbowText")
            {
                bodyColor = Color.FromArgb(243, 249, 252);
                borderColor = Color.FromArgb(61, 172, 211);
                primaryTextColor = Color.FromArgb(24, 86, 123);
                secondaryTextColor = Color.FromArgb(57, 112, 143);
                accentColor = Color.FromArgb(104, 95, 201);
                actionTextColor = Color.White;
                tipColor = Color.FromArgb(226, 241, 248);
            }
            else if (theme == "Custom" && settings != null)
            {
                Color custom = Color.FromArgb(settings.CustomBackgroundArgb);
                bodyColor = Color.FromArgb(255, custom.R, custom.G, custom.B);
                bool dark = (custom.R * 299 + custom.G * 587 + custom.B * 114) < 150000;
                primaryTextColor = dark ? Color.White : Color.FromArgb(24, 45, 61);
                secondaryTextColor = dark
                    ? Color.FromArgb(225, 238, 247)
                    : Color.FromArgb(69, 88, 103);
                borderColor = dark ? Color.FromArgb(160, 222, 245) : Color.FromArgb(77, 120, 147);
                accentColor = dark ? Color.FromArgb(49, 177, 234) : Color.FromArgb(24, 126, 183);
                actionTextColor = Color.White;
                tipColor = dark ? Darken(custom, 22) : Lighten(custom, 24);
            }
            else
            {
                bodyColor = Color.FromArgb(9, 40, 59);
                borderColor = Color.FromArgb(48, 180, 255);
                primaryTextColor = Color.White;
                secondaryTextColor = Color.FromArgb(189, 219, 235);
                accentColor = Color.FromArgb(28, 151, 219);
                actionTextColor = Color.White;
                tipColor = Color.FromArgb(14, 59, 80);
            }

            BackColor = bodyColor;
            contentPanel.BackColor = bodyColor;
            sectionLabel.ForeColor = theme == "OrangeGradient" || theme == "PinkGradient"
                ? primaryTextColor
                : accentColor;
            stepLabel.ForeColor = secondaryTextColor;
            titleLabel.ForeColor = primaryTextColor;
            bodyLabel.ForeColor = secondaryTextColor;
            tipPanel.BackColor = tipColor;
            tipLabel.ForeColor = primaryTextColor;
            closeButton.BackColor = bodyColor;
            closeButton.ForeColor = secondaryTextColor;
            StyleSecondaryButton(skipButton);
            StyleSecondaryButton(previousButton);
            nextButton.BackColor = accentColor;
            nextButton.ForeColor = actionTextColor;
            nextButton.FlatAppearance.BorderColor = accentColor;
            Invalidate(true);
        }

        public Bitmap ExportPreviewBitmap(Rectangle anchorBounds, Rectangle workingArea)
        {
            UpdateAnchor(anchorBounds, workingArea);
            if (!IsHandleCreated)
                CreateControl();
            PerformLayout();
            bool wasVisible = Visible;
            Point previewLocation = Location;
            if (!wasVisible)
            {
                Location = new Point(-2000, -2000);
                Show();
                Application.DoEvents();
            }
            Bitmap preview = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                PixelFormat.Format32bppArgb);
            DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));

            // WinForms may omit child controls when a borderless form is rendered
            // off-screen. Paint the content panel once more so the preview keeps
            // the same labels and buttons that users see at runtime.
            using (Bitmap content = new Bitmap(Math.Max(1, contentPanel.Width),
                Math.Max(1, contentPanel.Height), PixelFormat.Format32bppArgb))
            {
                contentPanel.DrawToBitmap(content, new Rectangle(Point.Empty, content.Size));
                using (Graphics graphics = Graphics.FromImage(preview))
                {
                    graphics.DrawImageUnscaled(content, contentPanel.Left, contentPanel.Top);
                    RenderPreviewControls(graphics, contentPanel, contentPanel.Left, contentPanel.Top);
                }
            }
            if (!wasVisible)
            {
                Hide();
                Location = previewLocation;
            }
            return preview;
        }

        private static void RenderPreviewControls(
            Graphics graphics, Control parent, int offsetX, int offsetY)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible || child.Width <= 0 || child.Height <= 0)
                    continue;

                int left = offsetX + child.Left;
                int top = offsetY + child.Top;
                Rectangle bounds = new Rectangle(left, top, child.Width, child.Height);
                Label label = child as Label;
                Button button = child as Button;
                Panel panel = child as Panel;

                if (panel != null && panel.BackColor.A > 0)
                {
                    using (Brush fill = new SolidBrush(panel.BackColor))
                        graphics.FillRectangle(fill, bounds);
                }

                if (label != null)
                    DrawPreviewText(graphics, label.Text, label.Font, label.ForeColor,
                        bounds, label.TextAlign, label.AutoEllipsis);
                else if (button != null)
                {
                    using (Brush fill = new SolidBrush(button.BackColor))
                        graphics.FillRectangle(fill, bounds);
                    if (button.FlatAppearance.BorderSize > 0)
                    {
                        using (Pen border = new Pen(button.FlatAppearance.BorderColor,
                            button.FlatAppearance.BorderSize))
                            graphics.DrawRectangle(border, bounds.Left, bounds.Top,
                                bounds.Width - 1, bounds.Height - 1);
                    }
                    DrawPreviewText(graphics, button.Text, button.Font, button.ForeColor,
                        bounds, ContentAlignment.MiddleCenter, false);
                }

                if (child.HasChildren)
                    RenderPreviewControls(graphics, child, left, top);
            }
        }

        private static void DrawPreviewText(
            Graphics graphics, string text, Font font, Color color, Rectangle bounds,
            ContentAlignment alignment, bool ellipsis)
        {
            if (string.IsNullOrEmpty(text))
                return;
            using (Brush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = alignment == ContentAlignment.TopLeft ||
                    alignment == ContentAlignment.MiddleLeft ||
                    alignment == ContentAlignment.BottomLeft
                    ? StringAlignment.Near
                    : alignment == ContentAlignment.TopRight ||
                        alignment == ContentAlignment.MiddleRight ||
                        alignment == ContentAlignment.BottomRight
                        ? StringAlignment.Far
                        : StringAlignment.Center;
                format.LineAlignment = alignment == ContentAlignment.TopLeft ||
                    alignment == ContentAlignment.TopCenter ||
                    alignment == ContentAlignment.TopRight
                    ? StringAlignment.Near
                    : alignment == ContentAlignment.BottomLeft ||
                        alignment == ContentAlignment.BottomCenter ||
                        alignment == ContentAlignment.BottomRight
                        ? StringAlignment.Far
                        : StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.LineLimit;
                if (ellipsis)
                    format.Trimming = StringTrimming.EllipsisCharacter;
                graphics.DrawString(text, font, brush, bounds, format);
            }
        }

        public void UpdateAnchor(Rectangle anchorBounds, Rectangle workingArea)
        {
            FitToWorkingArea(workingArea);
            bool nextArrowOnTop;
            Rectangle nextBounds = CalculateBubbleBounds(
                anchorBounds, Size, workingArea, ScaleValue(LogicalGap), out nextArrowOnTop);
            if (arrowOnTop != nextArrowOnTop)
            {
                arrowOnTop = nextArrowOnTop;
                contentPanel.Top = arrowOnTop
                    ? ScaleValue(ArrowHeight + 2)
                    : ScaleValue(2);
                ApplyBubbleRegion();
            }
            if (Bounds != nextBounds)
                Bounds = nextBounds;
        }

        internal static Rectangle CalculateBubbleBounds(
            Rectangle anchorBounds,
            Size bubbleSize,
            Rectangle workingArea,
            int gap,
            out bool arrowOnTop)
        {
            int left = anchorBounds.Left + (anchorBounds.Width - bubbleSize.Width) / 2;
            int below = anchorBounds.Bottom + gap;
            int above = anchorBounds.Top - gap - bubbleSize.Height;
            int belowSpace = workingArea.Bottom - below;
            int aboveSpace = anchorBounds.Top - workingArea.Top - gap;
            arrowOnTop = belowSpace >= bubbleSize.Height || belowSpace >= aboveSpace;
            int top = arrowOnTop ? below : above;

            int maxLeft = Math.Max(workingArea.Left, workingArea.Right - bubbleSize.Width);
            int maxTop = Math.Max(workingArea.Top, workingArea.Bottom - bubbleSize.Height);
            left = Math.Max(workingArea.Left, Math.Min(maxLeft, left));
            top = Math.Max(workingArea.Top, Math.Min(maxTop, top));
            return new Rectangle(left, top, bubbleSize.Width, bubbleSize.Height);
        }

        private Rectangle BubbleBodyBounds
        {
            get
            {
                int arrow = ScaleValue(ArrowHeight);
                return arrowOnTop
                    ? new Rectangle(1, arrow, Math.Max(1, ClientSize.Width - 2),
                        Math.Max(1, ClientSize.Height - arrow - 1))
                    : new Rectangle(1, 1, Math.Max(1, ClientSize.Width - 2),
                        Math.Max(1, ClientSize.Height - arrow - 1));
            }
        }

        private Point[] ArrowPoints
        {
            get
            {
                int half = ScaleValue(9);
                int arrow = ScaleValue(ArrowHeight);
                int center = ClientSize.Width / 2;
                if (arrowOnTop)
                {
                    return new[]
                    {
                        new Point(center - half, arrow + 1),
                        new Point(center, 1),
                        new Point(center + half, arrow + 1)
                    };
                }
                int bottom = ClientSize.Height - 1;
                return new[]
                {
                    new Point(center - half, bottom - arrow),
                    new Point(center, bottom),
                    new Point(center + half, bottom - arrow)
                };
            }
        }

        private Button CreateFooterButton(string text, Point location, Size size)
        {
            Button button = new Button();
            button.Location = location;
            button.Size = size;
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.Font = UiRendering.CreateTextFont(
                "Microsoft YaHei UI", 9f, FontStyle.Bold);
            return button;
        }

        private void StyleSecondaryButton(Button button)
        {
            button.BackColor = bodyColor;
            button.ForeColor = secondaryTextColor;
            button.FlatAppearance.BorderColor = borderColor;
        }

        private void RenderPage()
        {
            GuidePage page = Pages[pageIndex];
            stepLabel.Text = (pageIndex + 1).ToString() + " / " + Pages.Length.ToString();
            titleLabel.Text = page.Title;
            bodyLabel.Text = page.Body;
            tipLabel.Text = "小提示  " + page.Tip;
            previousButton.Visible = pageIndex > 0;
            nextButton.Text = pageIndex == Pages.Length - 1 ? "开始使用" : "下一步";
        }

        private void DismissGuide()
        {
            RaiseDismissed();
            Close();
        }

        private void RaiseDismissed()
        {
            if (dismissedRaised)
                return;
            dismissedRaised = true;
            EventHandler handler = Dismissed;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private void FitToWorkingArea(Rectangle workingArea)
        {
            if (workingArea.Width <= 0 || workingArea.Height <= 0 || Width <= 0 || Height <= 0)
                return;

            int margin = Math.Max(ScaleValue(8), 8);
            float widthRatio = (workingArea.Width - margin * 2) / (float)Width;
            float heightRatio = (workingArea.Height - margin * 2) / (float)Height;
            float factor = Math.Min(1f, Math.Min(widthRatio, heightRatio));
            if (factor >= 0.999f)
                return;

            layoutScale *= factor;
            Scale(new SizeF(factor, factor));
            ApplyBubbleRegion();
        }

        private int ScaleValue(int logical)
        {
            return Math.Max(1, (int)Math.Round(logical * DeviceDpi / 96d * layoutScale));
        }

        private void ApplyBubbleRegion()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
                return;
            using (GraphicsPath rounded = RoundedRectangle(
                BubbleBodyBounds, ScaleValue(CornerRadius)))
            using (Region region = new Region(rounded))
            using (GraphicsPath arrow = new GraphicsPath())
            {
                arrow.AddPolygon(ArrowPoints);
                region.Union(arrow);
                Region old = Region;
                Region = region.Clone();
                if (old != null)
                    old.Dispose();
            }
            Invalidate();
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Max(2, Math.Min(radius * 2,
                Math.Min(bounds.Width, bounds.Height)));
            Rectangle arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color Darken(Color color, int amount)
        {
            return Color.FromArgb(
                Math.Max(0, color.R - amount),
                Math.Max(0, color.G - amount),
                Math.Max(0, color.B - amount));
        }

        private static Color Lighten(Color color, int amount)
        {
            return Color.FromArgb(
                Math.Min(255, color.R + amount),
                Math.Min(255, color.G + amount),
                Math.Min(255, color.B + amount));
        }
    }
}
