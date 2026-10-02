using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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
        internal const int LogicalStripHeight = 42;
        private readonly Action openDetails;
        private readonly Action dismiss;
        private CodexContextSignal signal = CodexContextSignal.Empty;
        private OverlaySettings settings = new OverlaySettings();
        private Rectangle anchoredBounds;
        private float scale = 1f;
        private bool closeHovered;
        private bool compact;
        private bool recent;
        private Rectangle tokenCapsuleBounds;
        private bool tokenHovered;
        private CodexTokenUsagePopup tokenPopup;
        private bool SingleCompactRow { get { return compact && Height < 2 * (int)Math.Round(20 * scale); } }
        private int CompactHeaderOffset
        {
            get { return (int)Math.Round((Height <= 2 * (int)Math.Round(20 * scale) ? 5 : 6) * scale); }
        }

        private static string CompactText(CodexContextSignal current)
        {
            if (current == null || !current.Available)
                return "Codex 上下文 · 等待当前会话数据";
            return "上下文 " + current.Percent + "%" +
                (current.IsRecent(DateTime.UtcNow) ? "" : "（上次记录）");
        }

        private static string TokenSummary(CodexContextSignal current)
        {
            return (current.HasSessionUsage ? CodexSidebarContextForm.BriefTokens(current.SessionTokens) : "—") +
                " tok · 缓存命中 " + current.CacheHitText;
        }

        private static string SessionSummary(CodexContextSignal current)
        {
            if (!current.HasSessionUsage) return "本会话累计数据暂不可用";
            return "本会话 输入 " + SummaryTokens(current.SessionInputTokens - current.SessionCachedTokens) +
                " · 缓存 " + SummaryTokens(current.SessionCachedTokens) +
                " · 输出 " + SummaryTokens(current.SessionOutputTokens);
        }

        private static string SummaryTokens(long value)
        {
            // Keep small million counts useful (2.8M); leave sidebar integer formatting unchanged.
            if (value >= 999500 && value < 10000000)
                return Math.Round(value / 1000000d, 1, MidpointRounding.AwayFromZero)
                    .ToString("0.#", CultureInfo.InvariantCulture) + "M";
            return CodexSidebarContextForm.BriefTokens(value);
        }

        internal static int MeasureCompactWidth(CodexContextSignal current, float dpiScale)
        {
            using (Bitmap bitmap = UiRendering.CreateLayeredBitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font font = UiRendering.CreateTextFont(UiRendering.PreferredFontName,
                8.2f * dpiScale, FontStyle.Regular))
            {
                int textWidth = TextRenderer.MeasureText(graphics, CompactText(current), font,
                    Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                    TextFormatFlags.NoPadding).Width;
                textWidth += TextRenderer.MeasureText(graphics, TokenSummary(current), font,
                    Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                    TextFormatFlags.NoPadding).Width;
                using (Font detail = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 7.8f * dpiScale, FontStyle.Regular))
                {
                    int detailWidth = TextRenderer.MeasureText(graphics, SessionSummary(current), detail,
                        Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
                    return Math.Min((int)Math.Round(LogicalStripWidth * dpiScale),
                        Math.Max(detailWidth + (int)Math.Round(8 * dpiScale),
                            textWidth + (int)Math.Round(54 * dpiScale)));
                }
            }
        }

        private static string NarrowContextText(string text)
        {
            // Preserve the percentage and stale-data indication when only decorations/wording need shortening.
            return text.Replace("（上次记录）", "（上次）").Replace("Codex 上下文 · 等待当前会话数据", "上下文 · 等待数据");
        }

        internal static int MeasureCompactMinimumWidth(CodexContextSignal current, float dpiScale)
        {
            using (Bitmap bitmap = UiRendering.CreateLayeredBitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font font = UiRendering.CreateTextFont(UiRendering.PreferredFontName,
                7.2f * dpiScale * 96f / graphics.DpiY, FontStyle.Regular))
            using (Font detail = UiRendering.CreateTextFont(UiRendering.PreferredFontName,
                7.8f * dpiScale * 96f / graphics.DpiY, FontStyle.Regular))
            {
                TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                int header = TextRenderer.MeasureText(graphics, NarrowContextText(CompactText(current)), font, Size.Empty, flags).Width +
                    TextRenderer.MeasureText(graphics, TokenSummary(current), font, Size.Empty, flags).Width;
                int summary = TextRenderer.MeasureText(graphics, SessionSummary(current), detail, Size.Empty, flags).Width;
                int headerPadding = (int)Math.Round(4 * dpiScale) + 2 * (int)Math.Round(2 * dpiScale);
                int summaryPadding = 2 * (int)(4 * dpiScale);
                return Math.Max(header + headerPadding, summary + summaryPadding);
            }
        }

        internal static int MeasureCompactSingleRowHeight(float dpiScale)
        {
            using (Bitmap bitmap = UiRendering.CreateLayeredBitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font font = UiRendering.CreateTextFont(UiRendering.PreferredFontName,
                8.2f * dpiScale * 96f / graphics.DpiY, FontStyle.Regular))
                return TextRenderer.MeasureText(graphics, "上下文 100%（上次记录）· 716M tok · 缓存命中 100%", font,
                    Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
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
                if (compact) value.ExStyle |= NativeMethods.WS_EX_LAYERED;
                return value;
            }
        }

        internal void UpdateBanner(CodexContextSignal current, OverlaySettings visualSettings,
            Rectangle bounds, float dpiScale, bool compactMode)
        {
            bool scaleChanged = Math.Abs(scale - dpiScale) > 0.01f;
            bool modeChanged = compact != compactMode;
            bool currentRecent = current.IsRecent(DateTime.UtcNow);
            bool changed = signal.Percent != current.Percent || signal.Level != current.Level ||
                signal.UsedTokens != current.UsedTokens || signal.WindowTokens != current.WindowTokens ||
                signal.InputTokens != current.InputTokens ||
                signal.CachedInputTokens != current.CachedInputTokens ||
                signal.OutputTokens != current.OutputTokens ||
                signal.HasSessionUsage != current.HasSessionUsage || signal.SessionTokens != current.SessionTokens ||
                signal.SessionInputTokens != current.SessionInputTokens || signal.SessionCachedTokens != current.SessionCachedTokens ||
                signal.SessionOutputTokens != current.SessionOutputTokens ||
                !String.Equals(settings.Theme, visualSettings.Theme, StringComparison.Ordinal) ||
                settings.CustomBackgroundArgb != visualSettings.CustomBackgroundArgb ||
                scaleChanged || compact != compactMode || recent != currentRecent;
            signal = current;
            recent = currentRecent;
            settings = visualSettings;
            scale = Math.Max(0.5f, dpiScale);
            compact = compactMode;
            if (modeChanged)
            {
                HideTokenPopup();
                if (IsHandleCreated) RecreateHandle();
            }
            if (scaleChanged || modeChanged) UpdateRoundedRegion();
            if (anchoredBounds != bounds || Bounds != bounds)
            {
                changed = true;
                anchoredBounds = bounds;
                SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, BoundsSpecified.All);
            }
            if (compact && (changed || !Visible)) RenderCompactBitmap();
            if (!Visible)
            {
                changed = true;
                Show();
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
            }
            if (changed) Invalidate();
            if (tokenPopup != null && tokenPopup.Visible) ShowTokenPopup();
        }

        internal void HideBanner()
        {
            HideTokenPopup();
            if (Visible) Hide();
        }

        internal Rectangle OffsetForHostMove(int dx, int dy)
        {
            if (!Visible || anchoredBounds.IsEmpty || (dx == 0 && dy == 0)) return Rectangle.Empty;
            anchoredBounds = OverlayInteraction.OffsetBoundsForHostMove(anchoredBounds, dx, dy);
            HideTokenPopup();
            return anchoredBounds;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (!compact && e.X >= Width - (int)(47 * scale) &&
                e.Y <= (int)(43 * scale)) dismiss();
            else openDetails();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hovered = !compact && e.X >= Width - (int)(47 * scale) &&
                e.Y <= (int)(43 * scale);
            if (hovered != closeHovered) { closeHovered = hovered; Invalidate(); }
            bool overToken = compact && tokenCapsuleBounds.Contains(e.Location);
            if (overToken != tokenHovered)
            {
                tokenHovered = overToken;
                if (overToken) ShowTokenPopup(); else HideTokenPopup();
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (closeHovered) { closeHovered = false; Invalidate(); }
            tokenHovered = false;
            HideTokenPopup();
            Invalidate();
        }

        private void ShowTokenPopup()
        {
            if (tokenCapsuleBounds.IsEmpty || !Visible) return;
            if (tokenPopup == null) tokenPopup = new CodexTokenUsagePopup();
            tokenPopup.ShowUsage(this, signal, settings.Theme, RectangleToScreen(tokenCapsuleBounds), scale);
        }

        private void HideTokenPopup()
        {
            if (tokenPopup != null) tokenPopup.Hide();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && tokenPopup != null) tokenPopup.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRoundedRegion();
            if (compact && IsHandleCreated && Visible) RenderCompactBitmap();
        }

        private Bitmap BuildCompactBitmap()
        {
            Bitmap bitmap = UiRendering.CreateLayeredBitmap(Width, Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                bool dark = settings.Theme == "NeonBlue";
                Color muted = dark ? Color.FromArgb(172, 180, 185) : Color.FromArgb(123, 129, 133);
                DrawCompactBar(graphics, muted, muted, muted);
            }
            return bitmap;
        }

        private void RenderCompactBitmap()
        {
            using (Bitmap bitmap = BuildCompactBitmap())
                NativeMethods.UpdateLayeredBitmap(Handle, bitmap, Left, Top);
        }

        private void UpdateRoundedRegion()
        {
            if (Width < 2 || Height < 2) return;
            using (GraphicsPath path = RoundedPath(new Rectangle(0,
                compact && !SingleCompactRow ? CompactHeaderOffset : 0, Width,
                compact && !SingleCompactRow ? (int)Math.Round(20 * scale) : Height)))
            {
                Region previous = Region;
                Region next = new Region(path);
                if (compact && !SingleCompactRow)
                    using (GraphicsPath second = RoundedPath(new Rectangle(0, Height - (int)Math.Round(20 * scale),
                        Width, (int)Math.Round(20 * scale)))) next.Union(second);
                Region = next;
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
            if (compact)
            {
                // Also support off-screen snapshots without introducing an opaque background.
                using (Bitmap bitmap = BuildCompactBitmap())
                {
                    CompositingMode previous = graphics.CompositingMode;
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImageUnscaled(bitmap, 0, 0);
                    graphics.CompositingMode = previous;
                }
                return;
            }
            bool dark = String.Equals(settings.Theme, "NeonBlue", StringComparison.Ordinal);
            Color background = dark ? Color.FromArgb(29, 37, 45) : Color.FromArgb(242, 243, 245);
            Color ink = dark ? Color.FromArgb(238, 240, 241) : Color.FromArgb(34, 39, 42);
            Color muted = dark ? Color.FromArgb(172, 180, 185) : Color.FromArgb(123, 129, 133);
            Color track = dark ? Color.FromArgb(76, 84, 88) : Color.FromArgb(216, 219, 221);
            graphics.Clear(background);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color status = signal.Level == 2 ? Color.FromArgb(212, 89, 86) :
                signal.Level == 1 ? Color.FromArgb(218, 158, 49) : Color.FromArgb(38, 190, 101);
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

        private sealed class CompactHeaderLayout
        {
            internal Rectangle Context, Token;
            internal float FontSize;
            internal bool Icons;
            internal string ContextText;
        }

        private static CompactHeaderLayout LayoutCompactHeader(Graphics graphics, int width,
            float scale, string context, string token)
        {
            float fontScale = scale * 96f / Math.Max(1f, graphics.DpiY);
            TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            for (int step = 0; ; step++)
            {
                float points = Math.Max(7.2f, 8.2f - step * .2f);
                using (Font font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, points * fontScale, FontStyle.Regular))
                {
                    int contextWidth = TextRenderer.MeasureText(graphics, context, font, Size.Empty, flags).Width;
                    int tokenWidth = TextRenderer.MeasureText(graphics, token, font, Size.Empty, flags).Width;
                    bool icons = step == 0 && contextWidth + tokenWidth + (int)Math.Round(54 * scale) <= width;
                    int gap = (int)Math.Round(4 * scale);
                    int padding = (int)Math.Round((icons ? 4 : 2) * scale);
                    if (icons) { contextWidth += (int)Math.Round(20 * scale); tokenWidth += (int)Math.Round(22 * scale); }
                    if (contextWidth + tokenWidth + gap + padding * 2 > width && step < 5) continue;
                    if (contextWidth + tokenWidth + gap + padding * 2 > width)
                    {
                        context = NarrowContextText(context);
                        contextWidth = TextRenderer.MeasureText(graphics, context, font, Size.Empty, flags).Width;
                    }
                    // Context gets its complete width first; never give it the leftover space after Token.
                    contextWidth = Math.Min(contextWidth, Math.Max(0, width - padding * 2));
                    tokenWidth = Math.Min(tokenWidth, Math.Max(0, width - padding * 2 - gap - contextWidth));
                    int left = (width - contextWidth - tokenWidth - gap) / 2;
                    int height = (int)Math.Round(20 * scale);
                    return new CompactHeaderLayout {
                        Context = new Rectangle(left, 1, contextWidth, height),
                        Token = new Rectangle(left + contextWidth + gap, 1, tokenWidth, height),
                        FontSize = points * fontScale, Icons = icons, ContextText = context
                    };
                }
            }
        }

        private void DrawCompactBar(Graphics graphics, Color ink, Color muted, Color status)
        {
            float fontScale = scale * 96f / Math.Max(1f, graphics.DpiY);
            string context = CompactText(signal), token = TokenSummary(signal);
            CompactHeaderLayout layout = LayoutCompactHeader(graphics, Width, scale, context, token);
            using (Font font = UiRendering.CreateTextFont(UiRendering.PreferredFontName,
                layout.FontSize, FontStyle.Regular))
            using (Font detail = UiRendering.CreateTextFont(UiRendering.PreferredFontName,
                7.8f * fontScale, FontStyle.Regular))
            {
                int reserve = (int)(4 * scale), rowHeight = SingleCompactRow ? Height : (int)Math.Round(20 * scale);
                Rectangle contextBounds = layout.Context;
                tokenCapsuleBounds = layout.Token;
                if (SingleCompactRow)
                {
                    contextBounds.Y = tokenCapsuleBounds.Y = 0;
                    contextBounds.Height = tokenCapsuleBounds.Height = rowHeight;
                }
                else
                {
                    // Tighten the visual rows without moving the anchored window or
                    // changing the space required to choose single/two-row placement.
                    contextBounds.Y += CompactHeaderOffset;
                    tokenCapsuleBounds.Y = contextBounds.Y;
                }
                bool dark = settings.Theme == "NeonBlue";
                // Zero-alpha pixels pass through mouse input. Keep just the existing header
                // hit areas at 1/255 alpha so hovering between glyphs remains continuous.
                using (Brush hitArea = new SolidBrush(Color.FromArgb(1, 128, 128, 128)))
                {
                    graphics.FillRectangle(hitArea, contextBounds);
                    graphics.FillRectangle(hitArea, tokenCapsuleBounds);
                }
                Color firstInk = dark ? muted : Color.FromArgb(104, 111, 118);
                Rectangle tokenText = tokenCapsuleBounds;
                if (layout.Icons)
                {
                    float iconCenter = contextBounds.Top + contextBounds.Height / 2f;
                    DrawUsageIcon(graphics, contextBounds.Left + (int)(4 * scale), iconCenter, scale, firstInk, false);
                    DrawUsageIcon(graphics, tokenCapsuleBounds.Left + (int)(5 * scale), iconCenter, scale, firstInk, true);
                    contextBounds.X += (int)(18 * scale); contextBounds.Width -= (int)(18 * scale);
                    tokenText.X += (int)(20 * scale); tokenText.Width -= (int)Math.Round(22 * scale);
                }
                using (Brush firstBrush = new SolidBrush(firstInk))
                using (Brush detailBrush = new SolidBrush(muted))
                {
                    // GDI text rendering does not preserve alpha on a layered bitmap.
                    DrawCompactText(graphics, layout.ContextText, font, firstBrush,
                        contextBounds, StringAlignment.Near);
                    DrawCompactText(graphics, token, font, firstBrush,
                        tokenText, StringAlignment.Near);
                    if (!SingleCompactRow)
                    {
                        int detailHeight = rowHeight - (int)Math.Ceiling(5 * scale);
                        DrawCompactText(graphics, SessionSummary(signal), detail, detailBrush,
                            new Rectangle(reserve, Height - detailHeight, Width - reserve * 2, detailHeight), StringAlignment.Center);
                    }
                }
            }
        }

        private static void DrawCompactText(Graphics graphics, string text, Font font, Brush brush,
            RectangleF bounds, StringAlignment alignment)
        {
            using (StringFormat format = UiRendering.CreateTextFormat())
            using (GraphicsPath glyphs = new GraphicsPath())
            {
                format.Alignment = alignment;
                format.LineAlignment = StringAlignment.Near;
                format.FormatFlags |= StringFormatFlags.NoWrap;
                glyphs.AddString(text, font.FontFamily, (int)font.Style,
                    font.SizeInPoints * graphics.DpiY / 72f, bounds, format);
                RectangleF ink = glyphs.GetBounds();
                if (ink.IsEmpty) return;
                bounds.Y = (float)Math.Round(bounds.Y + (bounds.Height - ink.Height) / 2f - ink.Top + bounds.Y);
                // Small outline-filled glyphs lose font hinting. Grayscale grid fitting
                // keeps the transparent layer sharp without ClearType color fringes.
                TextRenderingHint previous = graphics.TextRenderingHint;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString(text, font, brush, bounds, format);
                graphics.TextRenderingHint = previous;
            }
        }

        internal static void DrawUsageIcon(Graphics graphics, float x, float y, float scale, Color color, bool database)
        {
            using (Pen pen = new Pen(color, Math.Max(1f, scale)))
            {
                if (database)
                {
                    graphics.DrawEllipse(pen, x, y - 6 * scale, 10 * scale, 4 * scale);
                    graphics.DrawLine(pen, x, y - 4 * scale, x, y + 5 * scale);
                    graphics.DrawLine(pen, x + 10 * scale, y - 4 * scale, x + 10 * scale, y + 5 * scale);
                    graphics.DrawArc(pen, x, y - 1 * scale, 10 * scale, 4 * scale, 0, 180);
                    graphics.DrawArc(pen, x, y + 3 * scale, 10 * scale, 4 * scale, 0, 180);
                }
                else
                {
                    graphics.DrawArc(pen, x, y - 5 * scale, 11 * scale, 11 * scale, 150, 240);
                    graphics.DrawLine(pen, x + 5.5f * scale, y + scale, x + 9 * scale, y - 3 * scale);
                }
            }
        }

        private static string FormatTokens(long tokens)
        {
            return tokens >= 10000
                ? Math.Round(tokens / 10000d, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " 万"
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
