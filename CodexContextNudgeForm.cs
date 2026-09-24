using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal sealed class CodexContextNudgeForm : Form
    {
        internal const int LogicalHeight = 52;
        internal const int LogicalGap = 1;
        internal const int LogicalWidth = 420;
        private readonly Action openDetails;
        private readonly Action dismiss;
        private CodexContextSignal signal = CodexContextSignal.Empty;
        private OverlaySettings settings = new OverlaySettings();
        private Rectangle anchoredBounds;
        private float scale = 1f;

        internal CodexContextNudgeForm(Action openDetails, Action dismiss)
        {
            this.openDetails = openDetails;
            this.dismiss = dismiss;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                return value;
            }
        }

        internal void UpdateBanner(CodexContextSignal current, OverlaySettings visualSettings,
            Rectangle bounds, float dpiScale)
        {
            bool scaleChanged = Math.Abs(scale - dpiScale) > 0.01f;
            bool changed = signal.Percent != current.Percent || signal.Level != current.Level ||
                !String.Equals(settings.Theme, visualSettings.Theme, StringComparison.Ordinal) ||
                settings.CustomBackgroundArgb != visualSettings.CustomBackgroundArgb ||
                scaleChanged;
            signal = current;
            settings = visualSettings;
            scale = Math.Max(0.5f, dpiScale);
            if (scaleChanged) UpdateRoundedRegion();
            if (anchoredBounds != bounds)
            {
                changed = true;
                anchoredBounds = bounds;
                SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, BoundsSpecified.All);
            }
            if (!Visible)
            {
                changed = true;
                Show();
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
            }
            if (changed) Invalidate();
        }

        internal void HideBanner()
        {
            if (Visible) Hide();
        }

        internal Rectangle OffsetForHostMove(int dx, int dy)
        {
            if (!Visible || anchoredBounds.IsEmpty || (dx == 0 && dy == 0)) return Rectangle.Empty;
            anchoredBounds = OverlayInteraction.OffsetBoundsForHostMove(anchoredBounds, dx, dy);
            return anchoredBounds;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (e.X >= Width - (int)(35 * scale)) dismiss();
            else openDetails();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRoundedRegion();
        }

        private void UpdateRoundedRegion()
        {
            if (Width < 2 || Height < 2) return;
            using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, Width, Height)))
            {
                Region previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }

        private GraphicsPath RoundedPath(Rectangle bounds)
        {
            int diameter = Math.Min(Math.Min(bounds.Width, bounds.Height),
                Math.Max(4, (int)Math.Round(16 * scale)));
            GraphicsPath path = new GraphicsPath();
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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.Clear(UiRendering.PanelColor(settings, 0));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color ink = UiRendering.PanelColor(settings, 1);
            Color edge = UiRendering.PanelColor(settings, 2);
            Color status = signal.Level == 2 ? Color.FromArgb(205, 74, 80) :
                signal.Level == 1 ? Color.FromArgb(185, 129, 35) : Color.FromArgb(34, 151, 107);
            string title = signal.Level == 2 ? "上下文快满了" :
                signal.Level == 1 ? "上下文正在升高" : "上下文充裕";
            string advice = signal.Level == 2 ? "建议收尾并保存关键结论，再开启新任务" :
                signal.Level == 1 ? "建议整理进展，为新任务做准备" : "可以继续工作";
            int pad = (int)(12 * scale);
            int dot = Math.Max(7, (int)(9 * scale));
            float fontScale = scale * 96f / Math.Max(1f, graphics.DpiY);
            using (Pen border = new Pen(edge))
            using (Brush statusBrush = new SolidBrush(status))
            using (Font titleFont = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 10.5f * fontScale, FontStyle.Regular))
            using (Font bodyFont = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 9f * fontScale, FontStyle.Regular))
            using (GraphicsPath outline = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1)))
            {
                TextFormatFlags format = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                graphics.DrawPath(border, outline);
                graphics.FillEllipse(statusBrush, pad, (int)(10 * scale), dot, dot);
                int left = pad + dot + (int)(8 * scale);
                TextRenderer.DrawText(graphics, title + " · " + signal.Percent + "%",
                    titleFont, new Rectangle(left, (int)(3 * scale),
                        Width - left - (int)(42 * scale), (int)(24 * scale)), status, format);
                TextRenderer.DrawText(graphics, advice + "  ·  点击查看",
                    bodyFont, new Rectangle(left, (int)(26 * scale),
                        Width - left - (int)(42 * scale), (int)(20 * scale)), ink, format);
                TextRenderer.DrawText(graphics, "×", titleFont,
                    new Rectangle(Width - (int)(30 * scale), (int)(7 * scale),
                        (int)(25 * scale), (int)(26 * scale)), ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }
    }
}
