using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal sealed class ResetRadarBannerForm : Form
    {
        public const int LogicalHeight = 48;
        public const int LogicalGap = 5;
        public const int LogicalWidth = 450;

        private readonly Action openSource;
        private readonly Action closeRequested;
        private ResetRadarData radar = new ResetRadarData();
        private OverlaySettings settings = new OverlaySettings();
        private string renderedRevision = String.Empty;
        private Rectangle renderedBounds = Rectangle.Empty;
        private float dpiScale = 1f;
        private bool hovered;
        private bool closeHovered;
        private DateTimeOffset? previewNow;

        public ResetRadarBannerForm(Action openSource, Action closeRequested)
        {
            this.openSource = openSource;
            this.closeRequested = closeRequested;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Cursor = Cursors.Hand;
            Width = 720;
            Height = LogicalHeight;
        }

        public static bool ShouldShow(ResetRadarData data)
        {
            return ResetRadarDisplay.ShouldShow(data, DateTimeOffset.Now);
        }

        public void UpdateBanner(ResetRadarData data, OverlaySettings visualSettings, Rectangle bounds, float scale)
        {
            if (!ShouldShow(data))
            {
                HideBanner();
                return;
            }

            radar = data.Clone();
            settings = visualSettings.Clone();
            dpiScale = Math.Max(0.5f, scale);
            bool boundsChanged = bounds != renderedBounds;
            if (boundsChanged)
            {
                SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, BoundsSpecified.All);
                renderedBounds = bounds;
            }

            bool scheduled = radar.Status == ResetRadarStatus.ScheduledToday ||
                radar.Status == ResetRadarStatus.ScheduledUpcoming;
            string clockRevision = scheduled && radar.EffectiveAt.HasValue
                ? DateTimeOffset.Now.ToString("yyyyMMddHHmmss")
                : String.Empty;
            string revision = radar.RevisionKey + "|" + settings.Theme + "|" +
                settings.CustomBackgroundArgb.ToString() + "|" + settings.FontName + "|" +
                dpiScale.ToString("0.###") + "|" +
                (hovered ? "hover" : "normal") + "|" + (closeHovered ? "close" : "open") + "|" + clockRevision;
            bool becameVisible = !Visible;
            if (becameVisible)
            {
                Show();
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
            }
            if (becameVisible || boundsChanged || !String.Equals(revision, renderedRevision, StringComparison.Ordinal))
            {
                RenderLayered();
                renderedRevision = revision;
            }
        }

        public void HideBanner()
        {
            hovered = false;
            closeHovered = false;
            renderedRevision = String.Empty;
            if (Visible)
                Hide();
        }

        internal void ExportPreviews(
            string outputDirectory,
            ResetRadarData previewRadar,
            OverlaySettings previewSettings,
            DateTimeOffset displayNow)
        {
            ResetRadarData originalRadar = radar;
            OverlaySettings originalSettings = settings;
            string originalRevision = renderedRevision;
            Rectangle originalBounds = renderedBounds;
            float originalDpiScale = dpiScale;
            bool originalHovered = hovered;
            bool originalCloseHovered = closeHovered;
            DateTimeOffset? originalPreviewNow = previewNow;
            Size originalSize = Size;

            Directory.CreateDirectory(outputDirectory);
            try
            {
                radar = previewRadar.Clone();
                settings = previewSettings.Clone();
                dpiScale = 1f;
                previewNow = displayNow;
                Size = new Size(LogicalWidth, LogicalHeight);

                hovered = false;
                closeHovered = false;
                using (Bitmap normal = BuildRenderedBitmap())
                    normal.Save(Path.Combine(outputDirectory, "reset-radar-banner.png"), ImageFormat.Png);

                hovered = true;
                closeHovered = true;
                using (Bitmap close = BuildRenderedBitmap())
                    close.Save(Path.Combine(outputDirectory, "reset-radar-banner-close.png"), ImageFormat.Png);
            }
            finally
            {
                radar = originalRadar;
                settings = originalSettings;
                renderedRevision = originalRevision;
                renderedBounds = originalBounds;
                dpiScale = originalDpiScale;
                hovered = originalHovered;
                closeHovered = originalCloseHovered;
                previewNow = originalPreviewNow;
                Size = originalSize;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE |
                    NativeMethods.WS_EX_LAYERED;
                return parameters;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!hovered)
            {
                hovered = true;
                renderedRevision = String.Empty;
                RenderLayered();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hovered || closeHovered)
            {
                hovered = false;
                closeHovered = false;
                renderedRevision = String.Empty;
                RenderLayered();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point logicalLocation = ToLogicalPoint(e.Location);
            bool nextCloseHovered = CloseHitBounds(LogicalCanvasWidth).Contains(logicalLocation);
            if (nextCloseHovered != closeHovered)
            {
                closeHovered = nextCloseHovered;
                renderedRevision = String.Empty;
                RenderLayered();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
                return;
            if (CloseHitBounds(LogicalCanvasWidth).Contains(ToLogicalPoint(e.Location)))
            {
                if (closeRequested != null)
                    closeRequested();
                return;
            }
            if (openSource != null)
                openSource();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WM_NCHITTEST)
            {
                message.Result = (IntPtr)NativeMethods.HTCLIENT;
                return;
            }
            base.WndProc(ref message);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Rendering is handled by UpdateLayeredWindow for per-pixel alpha.
        }

        private void RenderLayered()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0)
                return;
            using (Bitmap bitmap = BuildRenderedBitmap())
                NativeMethods.UpdateLayeredBitmap(Handle, bitmap, Left, Top);
        }

        private Bitmap BuildRenderedBitmap()
        {
            Bitmap bitmap = UiRendering.CreateLayeredBitmap(Width, Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform(dpiScale, dpiScale);

                int canvasWidth = Math.Max(1, (int)Math.Round(Width / dpiScale));
                int canvasHeight = Math.Max(1, (int)Math.Round(Height / dpiScale));
                Rectangle card = new Rectangle(1, 1, Math.Max(1, canvasWidth - 3), Math.Max(1, canvasHeight - 3));
                Color top;
                Color bottom;
                Color titleColor;
                Color detailColor;
                ResolveSurface(settings, out top, out bottom, out titleColor, out detailColor);

                Color border;
                Color dot;
                ResolveStatusColors(radar.Status, out border, out dot);
                if (hovered)
                {
                    border = Color.FromArgb(255, border.R, border.G, border.B);
                    top = Color.FromArgb(Math.Min(255, top.A + 10), top.R, top.G, top.B);
                }

                using (GraphicsPath shadowPath = RoundedRectangle(new Rectangle(0, 0, canvasWidth - 1, canvasHeight - 1), 10))
                using (Brush shadow = new SolidBrush(Color.FromArgb(35, 0, 0, 0)))
                    graphics.FillPath(shadow, shadowPath);
                using (GraphicsPath cardPath = RoundedRectangle(card, 9))
                using (LinearGradientBrush background = new LinearGradientBrush(card, top, bottom, LinearGradientMode.Vertical))
                using (Pen borderPen = new Pen(border, hovered ? 1.4f : 1f))
                {
                    graphics.FillPath(background, cardPath);
                    graphics.DrawPath(borderPen, cardPath);
                }
                using (Pen highlight = new Pen(Color.FromArgb(105, 255, 255, 255), 1f))
                    graphics.DrawLine(highlight, 10, 3, canvasWidth - 10, 3);

                using (Brush dotBrush = new SolidBrush(dot))
                using (Pen pulse = new Pen(Color.FromArgb(150, dot.R, dot.G, dot.B), 1f))
                {
                    graphics.DrawEllipse(pulse, 11, 8, 11, 11);
                    graphics.FillEllipse(dotBrush, 14, 11, 5, 5);
                }

                Rectangle titleBounds = new Rectangle(28, 4, Math.Max(20, canvasWidth - 62), 20);
                Rectangle detailBounds = new Rectangle(12, 24, Math.Max(20, canvasWidth - 24), 19);
                bool rainbowText = settings.Theme == "RainbowText";
                using (Font titleFont = CreateBannerFont(settings.FontName, 8.5f))
                using (Font detailFont = CreateBannerFont(settings.FontName, 7.8f))
                using (Brush titleBrush = CreateBannerTextBrush(titleBounds, titleColor, rainbowText))
                using (Brush detailBrush = CreateBannerTextBrush(detailBounds, detailColor, rainbowText))
                using (StringFormat titleFormat = UiRendering.CreateTextFormat())
                using (StringFormat detailFormat = UiRendering.CreateTextFormat())
                {
                    titleFormat.Alignment = StringAlignment.Near;
                    titleFormat.LineAlignment = StringAlignment.Center;
                    titleFormat.Trimming = StringTrimming.EllipsisCharacter;
                    titleFormat.FormatFlags |= StringFormatFlags.NoWrap;
                    detailFormat.Alignment = StringAlignment.Near;
                    detailFormat.LineAlignment = StringAlignment.Center;
                    detailFormat.Trimming = StringTrimming.EllipsisCharacter;
                    detailFormat.FormatFlags |= StringFormatFlags.NoWrap;

                    DateTimeOffset displayNow = previewNow ?? DateTimeOffset.Now;
                    string title = "TIBO RADAR · " +
                        ResetRadarDisplay.BuildHeadline(radar, displayNow) +
                        ResetRadarDisplay.ConfidenceSuffix(radar) + " · 非官方";
                    string detail = ResetRadarDisplay.BuildPrimaryLine(radar, displayNow);
                    graphics.DrawString(title, titleFont, titleBrush, titleBounds, titleFormat);
                    graphics.DrawString(detail, detailFont, detailBrush, detailBounds, detailFormat);
                }

                if (closeHovered)
                {
                    Rectangle closeButton = new Rectangle(Math.Max(2, canvasWidth - 25), 4, 18, 18);
                    using (GraphicsPath closePath = RoundedRectangle(closeButton, 6))
                    using (Brush closeBackground = new SolidBrush(Color.FromArgb(236, 222, 57, 67)))
                    using (Pen closePen = new Pen(Color.White, 1.8f))
                    {
                        closePen.StartCap = LineCap.Round;
                        closePen.EndCap = LineCap.Round;
                        graphics.FillPath(closeBackground, closePath);
                        graphics.DrawLine(closePen, closeButton.Left + 5, closeButton.Top + 5,
                            closeButton.Right - 5, closeButton.Bottom - 5);
                        graphics.DrawLine(closePen, closeButton.Right - 5, closeButton.Top + 5,
                            closeButton.Left + 5, closeButton.Bottom - 5);
                    }
                }
            }
            return bitmap;
        }

        private int LogicalCanvasWidth
        {
            get { return Math.Max(1, (int)Math.Round(Width / Math.Max(0.5f, dpiScale))); }
        }

        private Point ToLogicalPoint(Point physicalPoint)
        {
            float scale = Math.Max(0.5f, dpiScale);
            return new Point((int)Math.Floor(physicalPoint.X / scale), (int)Math.Floor(physicalPoint.Y / scale));
        }

        private static Rectangle CloseHitBounds(int canvasWidth)
        {
            return new Rectangle(Math.Max(0, canvasWidth - 34), 0, 34, 27);
        }

        private static void ResolveSurface(
            OverlaySettings visualSettings,
            out Color top,
            out Color bottom,
            out Color title,
            out Color detail)
        {
            if (visualSettings.Theme == "FrostedGlass")
            {
                top = Color.FromArgb(246, 248, 251, 253);
                bottom = Color.FromArgb(238, 219, 233, 241);
                title = Color.FromArgb(255, 28, 55, 78);
                detail = Color.FromArgb(235, 28, 55, 78);
            }
            else if (visualSettings.Theme == "RainbowText")
            {
                top = Color.FromArgb(246, 248, 251, 253);
                bottom = Color.FromArgb(238, 219, 233, 241);
                title = Color.FromArgb(255, 25, 105, 145);
                detail = Color.FromArgb(235, 25, 105, 145);
            }
            else if (visualSettings.Theme == "OrangeGradient")
            {
                top = Color.FromArgb(239, 250, 170, 91);
                bottom = Color.FromArgb(236, 239, 107, 119);
                title = Color.FromArgb(255, 255, 250, 235);
                detail = Color.FromArgb(238, 255, 250, 235);
            }
            else if (visualSettings.Theme == "PinkGradient")
            {
                top = Color.FromArgb(240, 248, 125, 184);
                bottom = Color.FromArgb(238, 183, 92, 207);
                title = Color.FromArgb(255, 255, 248, 253);
                detail = Color.FromArgb(238, 255, 248, 253);
            }
            else if (visualSettings.Theme == "Custom")
            {
                Color custom = Color.FromArgb(visualSettings.CustomBackgroundArgb);
                top = Color.FromArgb(238, custom.R, custom.G, custom.B);
                bottom = Color.FromArgb(232,
                    Math.Max(0, custom.R * 3 / 4),
                    Math.Max(0, custom.G * 3 / 4),
                    Math.Max(0, custom.B * 3 / 4));
                title = Color.White;
                detail = Color.FromArgb(242, 255, 255, 255);
            }
            else
            {
                top = Color.FromArgb(239, 10, 42, 61);
                bottom = Color.FromArgb(235, 10, 76, 98);
                title = Color.FromArgb(255, 132, 219, 255);
                detail = Color.FromArgb(235, 132, 219, 255);
            }
        }

        private static Font CreateBannerFont(string fontName, float size)
        {
            return UiRendering.CreateTextFont(fontName, size, FontStyle.Bold);
        }

        private static Brush CreateBannerTextBrush(RectangleF bounds, Color fallback, bool rainbowText)
        {
            if (!rainbowText)
                return new SolidBrush(fallback);

            LinearGradientBrush gradient = new LinearGradientBrush(
                bounds,
                Color.FromArgb(255, 255, 137, 47),
                Color.FromArgb(255, 70, 196, 255),
                LinearGradientMode.Horizontal);
            ColorBlend blend = new ColorBlend();
            blend.Positions = new[] { 0f, 0.34f, 0.68f, 1f };
            blend.Colors = new[]
            {
                Color.FromArgb(255, 255, 137, 47),
                Color.FromArgb(255, 255, 48, 145),
                Color.FromArgb(255, 158, 75, 255),
                Color.FromArgb(255, 70, 196, 255)
            };
            gradient.InterpolationColors = blend;
            return gradient;
        }

        private static void ResolveStatusColors(ResetRadarStatus status, out Color border, out Color dot)
        {
            if (status == ResetRadarStatus.CompletedToday)
            {
                border = Color.FromArgb(225, 77, 210, 151);
                dot = Color.FromArgb(255, 106, 244, 178);
            }
            else
            {
                border = Color.FromArgb(230, 241, 183, 58);
                dot = Color.FromArgb(255, 255, 210, 82);
            }
        }

        private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            int diameter = Math.Max(2, radius * 2);
            Rectangle arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(arc, 180, 90);
            arc.X = rectangle.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rectangle.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rectangle.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
