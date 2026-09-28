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
            float scale, bool expanded)
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
            if (expanded)
                rightMostRow += (int)Math.Ceiling(106 * scale);
            Rectangle bounds = new Rectangle(hostBounds.Left, hostBounds.Top,
                OverlayInteraction.GetSidebarContextCanvasWidth(hostBounds, rightMostRow, scale),
                hostBounds.Height);
            Point cursor = Cursor.Position;
            StringBuilder key = new StringBuilder();
            key.Append(bounds.ToString()).Append(':').Append(expanded);
            foreach (CodexSidebarContextRow row in rows)
                key.Append('|').Append(row.Bounds).Append(':').Append(row.Signal.Percent)
                    .Append(':').Append(row.Signal.Level).Append(':').Append(row.Signal.UsedTokens)
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
                        DrawBadge(graphics, row, bounds, scale, expanded);
                    else if (expanded)
                        DrawBadgePart(graphics, row, bounds, scale, true, true);
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
            Rectangle window, float scale, bool expanded)
        {
            // The percentage always uses its compact anchor, font and vertical alignment.
            // Details use the gap beside it, with a transparent slot for native task status.
            DrawBadgePart(graphics, row, window, scale, false);
            if (expanded) DrawBadgePart(graphics, row, window, scale, true);
        }

        private static void DrawBadgePart(Graphics graphics, CodexSidebarContextRow row,
            Rectangle window, float scale, bool expanded, bool hideLeadingDetails = false)
        {
            int width = (int)Math.Round((expanded ? 184 : 47) * scale);
            int height = Math.Min(row.Bounds.Height - (int)Math.Round(2 * scale),
                (int)Math.Round((expanded ? 26 : 20) * scale));
            if (height < (int)Math.Round((expanded ? 20 : 14) * scale)) return;
            int left = expanded
                ? row.Bounds.Right - (int)Math.Round(78 * scale) - window.Left
                // Leave the native task activity / refresh indicator unobscured.
                : row.Bounds.Right - width - (int)Math.Round(80 * scale) - window.Left;
            int top = row.Bounds.Top + (row.Bounds.Height - height) / 2 - window.Top;
            if (left < 0 || top < 0 || left + width > window.Width || top + height > window.Height)
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
            using (Pen outline = new Pen(Color.FromArgb(224, 230, 226), Math.Max(1, scale)))
            using (Font font = UiRendering.CreateTextFont(UiRendering.PreferredFontName,
                (expanded ? 7.2f : 8.2f) * scale, FontStyle.Regular))
            using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter })
            {
                if (!expanded || !hideLeadingDetails) graphics.FillPath(background, shape);
                if (expanded)
                {
                    if (!hideLeadingDetails) graphics.DrawPath(outline, shape);
                    int trailingLeft = row.Bounds.Right - (int)Math.Round(10 * scale) - window.Left;
                    Rectangle trailing = new Rectangle(trailingLeft, top, left + width - trailingLeft, height);
                    using (GraphicsPath trailingShape = RoundedPath(trailing, (int)Math.Round(5 * scale)))
                    {
                        graphics.FillPath(background, trailingShape);
                        graphics.DrawPath(outline, trailingShape);
                    }
                    format.Alignment = StringAlignment.Near;
                    float lineHeight = height / 2f;
                    using (StringFormat compactFormat = (StringFormat)StringFormat.GenericTypographic.Clone())
                    {
                        compactFormat.LineAlignment = StringAlignment.Center;
                        compactFormat.FormatFlags |= StringFormatFlags.NoWrap;
                        compactFormat.Trimming = StringTrimming.EllipsisCharacter;
                        // Hide the in-row cells on hover, just like the percentage, so native actions remain visible.
                        if (!hideLeadingDetails)
                        {
                            graphics.DrawString("已" + BriefTokens(row.Signal.UsedTokens), font, ink,
                                new RectangleF(left + scale, top, 44 * scale, lineHeight), compactFormat);
                            graphics.DrawString("入" + BriefTokens(row.Signal.InputTokens), font, muted,
                                new RectangleF(left + scale, top + lineHeight, 44 * scale, lineHeight), compactFormat);
                        }
                        float textLeft = trailingLeft + 3 * scale;
                        graphics.DrawString("余" + BriefTokens(Math.Max(0,
                            row.Signal.WindowTokens - row.Signal.UsedTokens)), font, ink,
                            new RectangleF(textLeft, top, 110 * scale, lineHeight), compactFormat);
                        graphics.DrawString("缓" + BriefTokens(row.Signal.CachedInputTokens), font, muted,
                            new RectangleF(textLeft, top + lineHeight, 57 * scale, lineHeight), compactFormat);
                        graphics.DrawString("出" + BriefTokens(row.Signal.OutputTokens), font, muted,
                            new RectangleF(textLeft + 57 * scale, top + lineHeight, 53 * scale, lineHeight), compactFormat);
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

        internal static string BuildContextLine(CodexContextSignal signal)
        {
            return signal.Percent + "%  已" + BriefTokens(signal.UsedTokens) +
                "  余" + BriefTokens(Math.Max(0, signal.WindowTokens - signal.UsedTokens));
        }

        internal static string BuildTokenLine(CodexContextSignal signal)
        {
            return "入" + BriefTokens(signal.InputTokens) + "  缓" +
                BriefTokens(signal.CachedInputTokens) + "  出" + BriefTokens(signal.OutputTokens);
        }

        internal static string BriefTokens(long value)
        {
            value = Math.Max(0, value);
            if (value >= 1000000)
                return (value / 1000000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            // Keep the unit stable around the threshold; 999,999 must not round to 1000K.
            double thousands = value >= 1000 ? Math.Floor(value / 100d) / 10d : value / 1000d;
            return thousands.ToString(value >= 1000 ? "0.#" : "0.###", CultureInfo.InvariantCulture) + "K";
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
