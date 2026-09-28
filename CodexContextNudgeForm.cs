using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal sealed class CodexContextNudgeForm : Form
    {
        internal const int LogicalHeight = 126;
        internal const int LogicalGap = 1;
        internal const int LogicalWidth = 374;
        internal const int LogicalStripWidth = 520;
        internal const int LogicalStripHeight = 15;
        private readonly Action openDetails;
        private readonly Action dismiss;
        private CodexContextSignal signal = CodexContextSignal.Empty;
        private OverlaySettings settings = new OverlaySettings();
        private Rectangle anchoredBounds;
        private float scale = 1f;
        private bool closeHovered;
        private bool compact;
        private bool recent;

        private static string CompactText(CodexContextSignal current)
        {
            if (current == null || !current.Available)
                return "Codex 上下文 · 等待当前会话数据";
            return (current.IsRecent(DateTime.UtcNow) ? "Codex  " : "Codex（上次记录） ") +
                CodexSidebarContextForm.BuildContextLine(current) +
                "   " + CodexSidebarContextForm.BuildTokenLine(current);
        }

        internal static int MeasureCompactWidth(CodexContextSignal current, float dpiScale)
        {
            using (Bitmap bitmap = UiRendering.CreateLayeredBitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font font = UiRendering.CreateTextFont(UiRendering.PreferredFontName,
                7.8f * dpiScale, FontStyle.Regular))
            {
                int textWidth = TextRenderer.MeasureText(graphics, CompactText(current), font,
                    Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                    TextFormatFlags.NoPadding).Width;
                return Math.Min((int)Math.Round(LogicalStripWidth * dpiScale),
                    Math.Max((int)Math.Round(240 * dpiScale),
                        textWidth + (int)Math.Round(65 * dpiScale)));
            }
        }

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
            Rectangle bounds, float dpiScale, bool compactMode)
        {
            bool scaleChanged = Math.Abs(scale - dpiScale) > 0.01f;
            bool currentRecent = current.IsRecent(DateTime.UtcNow);
            bool changed = signal.Percent != current.Percent || signal.Level != current.Level ||
                signal.UsedTokens != current.UsedTokens || signal.WindowTokens != current.WindowTokens ||
                signal.InputTokens != current.InputTokens ||
                signal.CachedInputTokens != current.CachedInputTokens ||
                signal.OutputTokens != current.OutputTokens ||
                !String.Equals(settings.Theme, visualSettings.Theme, StringComparison.Ordinal) ||
                settings.CustomBackgroundArgb != visualSettings.CustomBackgroundArgb ||
                scaleChanged || compact != compactMode || recent != currentRecent;
            signal = current;
            recent = currentRecent;
            settings = visualSettings;
            scale = Math.Max(0.5f, dpiScale);
            compact = compactMode;
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
            if (e.X >= Width - (int)((compact ? 30 : 47) * scale) &&
                (compact || e.Y <= (int)(43 * scale))) dismiss();
            else openDetails();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hovered = e.X >= Width - (int)((compact ? 30 : 47) * scale) &&
                (compact || e.Y <= (int)(43 * scale));
            if (hovered != closeHovered) { closeHovered = hovered; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (closeHovered) { closeHovered = false; Invalidate(); }
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
                Math.Max(4, (int)Math.Round((compact ? 10 : 34) * scale)));
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
            bool dark = String.Equals(settings.Theme, "NeonBlue", StringComparison.Ordinal);
            Color background = dark ? Color.FromArgb(29, 37, 45) : Color.FromArgb(242, 243, 245);
            Color ink = dark ? Color.FromArgb(238, 240, 241) : Color.FromArgb(34, 39, 42);
            Color muted = dark ? Color.FromArgb(172, 180, 185) : Color.FromArgb(123, 129, 133);
            Color track = dark ? Color.FromArgb(76, 84, 88) : Color.FromArgb(216, 219, 221);
            graphics.Clear(background);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color status = signal.Level == 2 ? Color.FromArgb(212, 89, 86) :
                signal.Level == 1 ? Color.FromArgb(218, 158, 49) : Color.FromArgb(38, 190, 101);
            if (compact)
            {
                if (!signal.IsRecent(DateTime.UtcNow)) status = muted;
                DrawCompactBar(graphics, ink, muted, status);
                return;
            }
            string advice = signal.Level == 2 ? "建议收尾并保存关键结论，再开启新任务" :
                signal.Level == 1 ? "建议整理进展，为新任务做准备" : "可以继续工作";
            int pad = (int)Math.Round(18 * scale);
            int dot = Math.Max(8, (int)Math.Round(13 * scale));
            float fontScale = scale * 96f / Math.Max(1f, graphics.DpiY);
            using (Brush statusBrush = new SolidBrush(status))
            using (Brush trackBrush = new SolidBrush(track))
            using (Brush closeBrush = new SolidBrush(closeHovered ?
                (dark ? Color.FromArgb(88, 96, 101) : Color.FromArgb(219, 221, 223)) :
                (dark ? Color.FromArgb(59, 67, 73) : Color.FromArgb(232, 234, 236))))
            using (Font titleFont = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 11f * fontScale, FontStyle.Regular))
            using (Font bodyFont = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 9.5f * fontScale, FontStyle.Regular))
            using (Font detailFont = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 8.7f * fontScale, FontStyle.Regular))
            {
                TextFormatFlags format = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                graphics.FillEllipse(statusBrush, pad, (int)(15 * scale), dot, dot);
                int titleLeft = pad + dot + (int)(9 * scale);
                TextRenderer.DrawText(graphics, "Codex  " + signal.Percent + "%",
                    titleFont, new Rectangle(titleLeft, (int)(7 * scale),
                        Width - titleLeft - (int)(55 * scale), (int)(30 * scale)), ink, format);
                int closeSize = (int)(27 * scale);
                graphics.FillEllipse(closeBrush, Width - pad - closeSize,
                    (int)(8 * scale), closeSize, closeSize);
                TextRenderer.DrawText(graphics, "×", detailFont,
                    new Rectangle(Width - pad - closeSize, (int)(8 * scale),
                        closeSize, closeSize), muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                int progressY = (int)(43 * scale), progressHeight = Math.Max(3, (int)(4 * scale));
                int progressWidth = Width - 2 * pad;
                using (GraphicsPath path = ProgressPath(new Rectangle(pad, progressY,
                    progressWidth, progressHeight), progressHeight))
                    graphics.FillPath(trackBrush, path);
                int filled = Math.Max(progressHeight,
                    (int)Math.Round(progressWidth * signal.Percent / 100d));
                using (GraphicsPath path = ProgressPath(new Rectangle(pad, progressY,
                    Math.Min(progressWidth, filled), progressHeight), progressHeight))
                    graphics.FillPath(statusBrush, path);
                TextRenderer.DrawText(graphics, advice, bodyFont,
                    new Rectangle(pad, (int)(52 * scale), progressWidth, (int)(20 * scale)), ink, format);
                string summary = "已用 " + FormatTokens(signal.UsedTokens) + " · 剩余 " +
                    FormatTokens(Math.Max(0, signal.WindowTokens - signal.UsedTokens)) +
                    " · 窗口 " + FormatTokens(signal.WindowTokens);
                TextRenderer.DrawText(graphics, summary, detailFont,
                    new Rectangle(pad, (int)(75 * scale), progressWidth, (int)(19 * scale)), muted, format);
                string detail = signal.InputTokens + signal.CachedInputTokens + signal.OutputTokens > 0
                    ? "本轮输入 " + FormatTokens(signal.InputTokens) + " · 缓存复用 " +
                        FormatTokens(signal.CachedInputTokens) + " · 输出 " +
                        FormatTokens(signal.OutputTokens)
                    : "本机会话记录 · 点击查看上下文分析";
                TextRenderer.DrawText(graphics, detail, detailFont,
                    new Rectangle(pad, (int)(98 * scale), progressWidth, (int)(19 * scale)), muted, format);
            }
        }

        private void DrawCompactBar(Graphics graphics, Color ink, Color muted, Color status)
        {
            int pad = (int)Math.Round(10 * scale);
            int dot = Math.Max(4, (int)Math.Round(5 * scale));
            int closeWidth = (int)Math.Round(25 * scale);
            float fontScale = scale * 96f / Math.Max(1f, graphics.DpiY);
            using (Brush accent = new SolidBrush(status))
            using (Brush border = new SolidBrush(Color.FromArgb(170, muted.R, muted.G, muted.B)))
            using (Font font = UiRendering.CreateTextFont(UiRendering.PreferredFontName,
                7.8f * fontScale, FontStyle.Regular))
            {
                string line = CompactText(signal);
                int gap = (int)Math.Round(7 * scale);
                int available = Math.Max(1, Width - 2 * pad - dot - gap - closeWidth - gap);
                int textWidth = Math.Min(available, TextRenderer.MeasureText(graphics, line, font,
                    Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                    TextFormatFlags.NoPadding).Width);
                int contentWidth = dot + gap + textWidth + gap + closeWidth;
                int contentLeft = (Width - contentWidth) / 2;
                graphics.FillEllipse(accent, contentLeft, (Height - dot) / 2, dot, dot);
                int textLeft = contentLeft + dot + gap;
                TextRenderer.DrawText(graphics, line, font,
                    new Rectangle(textLeft, 0, textWidth, Height),
                    ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(graphics, "×", font,
                    new Rectangle(Width - closeWidth - pad, 0, closeWidth, Height),
                    closeHovered ? ink : muted, TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPadding);
                graphics.FillRectangle(border, 0, Height - 1, Width, 1);
            }
        }

        private static string FormatTokens(long tokens)
        {
            return tokens >= 10000
                ? (tokens / 10000d).ToString("0.#", CultureInfo.InvariantCulture) + " 万"
                : tokens.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static GraphicsPath ProgressPath(Rectangle bounds, int height)
        {
            GraphicsPath path = new GraphicsPath();
            if (bounds.Width <= height)
                path.AddEllipse(bounds);
            else
            {
                path.AddArc(bounds.Left, bounds.Top, height, height, 90, 180);
                path.AddArc(bounds.Right - height, bounds.Top, height, height, 270, 180);
                path.CloseFigure();
            }
            return path;
        }
    }
}
