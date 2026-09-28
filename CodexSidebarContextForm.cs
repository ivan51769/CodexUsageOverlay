using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal sealed class CodexSidebarContextForm : Form
    {
        private Rectangle anchoredBounds;
        private string revision = String.Empty;

        internal CodexSidebarContextForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE |
                    NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED;
                return value;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WM_NCHITTEST)
            {
                message.Result = (IntPtr)NativeMethods.HTTRANSPARENT;
                return;
            }
            base.WndProc(ref message);
        }

        internal void UpdateBadges(IList<CodexSidebarContextRow> rows, Rectangle hostBounds,
            float scale, int stage)
        {
            if (rows == null || rows.Count == 0 || hostBounds.IsEmpty)
            {
                HideBadges();
                return;
            }
            scale = Math.Max(.75f, scale);
            int rightMostRow = hostBounds.Left;
            foreach (CodexSidebarContextRow row in rows)
                rightMostRow = Math.Max(rightMostRow, row.Bounds.Right);
            Rectangle bounds = new Rectangle(hostBounds.Left, hostBounds.Top,
                OverlayInteraction.GetSidebarContextCanvasWidth(hostBounds, rightMostRow, scale),
                hostBounds.Height);
            Point cursor = Cursor.Position;
            StringBuilder key = new StringBuilder();
            key.Append(bounds.ToString()).Append(':').Append(stage);
            foreach (CodexSidebarContextRow row in rows)
                key.Append('|').Append(row.Bounds).Append(':').Append(row.Signal.Percent)
                    .Append(':').Append(row.Signal.Level).Append(':').Append(row.Signal.UsedTokens)
                    .Append(':').Append(row.Signal.WindowTokens)
                    .Append(':').Append(row.Signal.InputTokens).Append(':').Append(row.Signal.CachedInputTokens)
                    .Append(':').Append(row.Signal.OutputTokens).Append(':').Append(row.Bounds.Contains(cursor));
            if (revision == key.ToString() && Visible) return;
            revision = key.ToString();
            anchoredBounds = bounds;
            SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, BoundsSpecified.All);
            using (Bitmap bitmap = UiRendering.CreateLayeredBitmap(bounds.Width, bounds.Height))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                foreach (CodexSidebarContextRow row in rows)
                {
                    if (!row.Bounds.Contains(cursor))
                        DrawBadge(graphics, row, bounds, scale, stage);
                }
                NativeMethods.UpdateLayeredBitmap(Handle, bitmap, bounds.Left, bounds.Top);
            }
            if (!Visible)
            {
                Show();
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
            }
        }

        internal void HideBadges()
        {
            if (Visible) Hide();
            revision = String.Empty;
        }

        internal Rectangle OffsetForHostMove(int dx, int dy)
        {
            if (!Visible || anchoredBounds.IsEmpty || (dx == 0 && dy == 0)) return Rectangle.Empty;
            anchoredBounds.Offset(dx, dy);
            revision = String.Empty;
            return anchoredBounds;
        }

        private static void DrawBadge(Graphics graphics, CodexSidebarContextRow row,
            Rectangle window, float scale, int stage)
        {
            // The percentage always uses its compact anchor, font and vertical alignment.
            // All details stay inside the row; its right edge is reserved for native status.
            DrawBadgePart(graphics, row, window, scale, false);
            if (stage > 0) DrawBadgePart(graphics, row, window, scale, true, false, stage == 2);
        }

        private static void DrawBadgePart(Graphics graphics, CodexSidebarContextRow row,
            Rectangle window, float scale, bool expanded, bool hideLeadingDetails = false, bool showTokens = true)
        {
            if (hideLeadingDetails) return; // Hover gives the entire native row its actions back.
            int anchorRight = GetBadgeAnchorRight(row.Bounds, scale);
            int width = (int)Math.Round((expanded ? 94 : 47) * scale);
            int height = Math.Min(row.Bounds.Height - (int)Math.Round(2 * scale),
                (int)Math.Round((expanded ? 26 : 20) * scale));
            if (height < (int)Math.Round((expanded ? 20 : 14) * scale)) return;
            int left = expanded
                ? anchorRight - (int)Math.Round(126 * scale) - window.Left
                // Leave the native task activity / refresh indicator unobscured.
                : anchorRight - width - (int)Math.Round(130 * scale) - window.Left;
            int top = row.Bounds.Top + (row.Bounds.Height - height) / 2 - window.Top;
            int visibleWidth = expanded && !showTokens ? (int)Math.Round(45 * scale) : width;
            if (left < 0 || top < 0 || left + visibleWidth > window.Width || top + height > window.Height)
                return;
            Color accent = row.Signal.Level == 2 ? Color.FromArgb(207, 79, 80) :
                row.Signal.Level == 1 ? Color.FromArgb(190, 134, 44) : Color.FromArgb(42, 185, 102);
            Color fill = Color.FromArgb(246, 248, 247);
            Rectangle badge = new Rectangle(left, top, width, height);
            if (expanded) badge.Width = (int)Math.Round(45 * scale);
            using (GraphicsPath shape = RoundedPath(badge, (int)Math.Round(5 * scale)))
            using (Brush background = new SolidBrush(fill))
            using (Brush dot = new SolidBrush(accent))
            using (Brush ink = new SolidBrush(Color.FromArgb(46, 53, 52)))
            using (Brush muted = new SolidBrush(Color.FromArgb(104, 113, 109)))
            using (Font font = UiRendering.CreateTextFont(UiRendering.PreferredFontName,
                (expanded ? 7.2f : 8.2f) * scale, FontStyle.Regular))
            using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter })
            {
                if (!expanded || !hideLeadingDetails) graphics.FillPath(background, shape);
                if (expanded)
                {
                    int trailingLeft = anchorRight - (int)Math.Round(77 * scale) - window.Left;
                    Rectangle trailing = new Rectangle(trailingLeft, top, left + width - trailingLeft, height);
                    if (showTokens)
                    using (GraphicsPath trailingShape = RoundedPath(trailing, (int)Math.Round(5 * scale)))
                    {
                        graphics.FillPath(background, trailingShape);
                    }
                    format.Alignment = StringAlignment.Near;
                    float lineHeight = height / 2f;
                    string[] lines = BuildSidebarDetailLines(row.Signal);
                    using (StringFormat compactFormat = (StringFormat)StringFormat.GenericTypographic.Clone())
                    {
                        compactFormat.LineAlignment = StringAlignment.Center;
                        compactFormat.FormatFlags |= StringFormatFlags.NoWrap;
                        compactFormat.Trimming = StringTrimming.EllipsisCharacter;
                        // Hide the in-row cells on hover, just like the percentage, so native actions remain visible.
                        if (!hideLeadingDetails)
                        {
                            graphics.DrawString(lines[0], font, ink,
                                new RectangleF(left + scale, top, 44 * scale, lineHeight), compactFormat);
                            graphics.DrawString(lines[1], font, muted,
                                new RectangleF(left + scale, top + lineHeight, 44 * scale, lineHeight), compactFormat);
                        }
                        if (!showTokens) return;
                        float textLeft = trailingLeft + scale;
                        graphics.DrawString(lines[2], font, ink,
                            new RectangleF(textLeft, top, 44 * scale, lineHeight), compactFormat);
                        graphics.DrawString(lines[3], font, muted,
                            new RectangleF(textLeft, top + lineHeight, 44 * scale, lineHeight), compactFormat);
                    }
                }
                else
                {
                    int dotSize = Math.Max(4, (int)Math.Round(5 * scale));
                    graphics.FillEllipse(dot, left + (int)Math.Round(5 * scale),
                        top + (height - dotSize) / 2, dotSize, dotSize);
                    graphics.DrawString(row.Signal.Percent + "%", font, ink,
                        new RectangleF(left + (int)Math.Round(10 * scale), top,
                            width - (int)Math.Round(10 * scale), height), format);
                }
            }
        }

        internal static int GetBadgeAnchorRight(Rectangle row, float scale)
        {
            // A wide sidebar must not drag the metrics into the conversation or leave a huge gap.
            return Math.Min(row.Right, row.Left + (int)Math.Round(400 * scale));
        }

        internal static string[] BuildSidebarDetailLines(CodexContextSignal signal)
        {
            return new[] { "已" + BriefTokens(signal.UsedTokens),
                "余" + BriefTokens(Math.Max(0, signal.WindowTokens - signal.UsedTokens)),
                "入" + BriefTokens(signal.InputTokens), "缓" + BriefTokens(signal.CachedInputTokens) };
        }

        internal static string BuildContextLine(CodexContextSignal signal)
        {
            return signal.Percent + "%  已" + BriefTokens(signal.UsedTokens) +
                "  余" + BriefTokens(Math.Max(0, signal.WindowTokens - signal.UsedTokens));
        }

        internal static string BuildTokenLine(CodexContextSignal signal)
        {
            return "入" + BriefTokens(signal.InputTokens) + "  缓" +
                BriefTokens(signal.CachedInputTokens);
        }

        internal static string BriefTokens(long value)
        {
            value = Math.Max(0, value);
            double thousands = Math.Round(value / 1000d, 0, MidpointRounding.AwayFromZero);
            if (thousands >= 1000)
                return Math.Round(value / 1000000d, 0, MidpointRounding.AwayFromZero)
                    .ToString("0", CultureInfo.InvariantCulture) + "M";
            return thousands.ToString("0", CultureInfo.InvariantCulture) + "K";
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            int diameter = Math.Max(2, Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2));
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
    }
}
