using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal sealed class CodexSidebarContextForm : Form
    {
        private Rectangle anchoredBounds;
        private string revision = String.Empty;
        private string lastDiagnostic;
        private DateTime lastDiagnosticUtc;

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
                WriteDiagnostic("rows=" + (rows == null ? 0 : rows.Count) + "; host=" + hostBounds + "; reason=no-rows-or-host");
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
            int sharedAnchor = GetSharedBadgeAnchorRight(rows, scale);
            int drawable = 0, hovered = 0, missingTitle = 0, collision = 0;
            StringBuilder geometry = new StringBuilder();
            foreach (CodexSidebarContextRow row in rows)
            {
                Rectangle badge = GetAlignedBadgeBounds(row.Bounds, row.TitleBounds, scale, 0, sharedAnchor);
                if (row.Bounds.Contains(cursor)) hovered++;
                else if (row.TitleBounds.IsEmpty) missingTitle++;
                else if (badge.IsEmpty || !bounds.Contains(badge)) collision++;
                else drawable++;
                if (geometry.Length < 600)
                    geometry.Append(" row=").Append(row.Bounds).Append(" title=").Append(row.TitleBounds).Append(" badge=").Append(badge);
            }
            WriteDiagnostic("rows=" + rows.Count + "; drawable=" + drawable + "; hovered=" + hovered +
                "; missingTitle=" + missingTitle + "; collision=" + collision + "; anchor=" + sharedAnchor +
                "; canvas=" + bounds + "; scale=" + scale + "; geometry=" + geometry);
            StringBuilder key = new StringBuilder();
            key.Append(bounds.ToString()).Append(':').Append(stage);
            foreach (CodexSidebarContextRow row in rows)
                key.Append('|').Append(row.Bounds).Append(':').Append(row.Signal.Percent)
                    .Append(':').Append(row.TitleBounds)
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
                        DrawBadgeAtAnchor(graphics, row, bounds, scale, stage, sharedAnchor);
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

        private void WriteDiagnostic(string value)
        {
            TimeSpan elapsed = DateTime.UtcNow - lastDiagnosticUtc;
            if (elapsed < TimeSpan.FromSeconds(1) ||
                (value == lastDiagnostic && elapsed < TimeSpan.FromMinutes(1))) return;
            lastDiagnostic = value;
            lastDiagnosticUtc = DateTime.UtcNow;
            try
            {
                // Bounded geometry only: no session names, contents, paths or credentials.
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sidebar-layout.log"),
                    DateTime.UtcNow.ToString("o") + " " + value + Environment.NewLine);
            }
            catch { }
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
            DrawBadgeAtAnchor(graphics, row, window, scale, stage, GetBadgeAnchorRight(row.Bounds, scale));
        }

        private static void DrawBadgeAtAnchor(Graphics graphics, CodexSidebarContextRow row,
            Rectangle window, float scale, int stage, int anchor)
        {
            // The percentage always uses its compact anchor, font and vertical alignment.
            // All details stay inside the row; its right edge is reserved for native status.
            DrawBadgePart(graphics, row, window, scale, false, false, true, anchor);
            if (stage > 0) DrawBadgePart(graphics, row, window, scale, true, false, stage == 2, anchor);
        }

        private static void DrawBadgePart(Graphics graphics, CodexSidebarContextRow row,
            Rectangle window, float scale, bool expanded, bool hideLeadingDetails = false, bool showTokens = true, int anchor = 0)
        {
            if (hideLeadingDetails) return; // Hover gives the entire native row its actions back.
            if (anchor == 0) anchor = GetBadgeAnchorRight(row.Bounds, scale);
            Rectangle first = GetAlignedBadgeBounds(row.Bounds, row.TitleBounds, scale, expanded ? 1 : 0, anchor);
            Rectangle second = GetAlignedBadgeBounds(row.Bounds, row.TitleBounds, scale, 2, anchor);
            if (first.IsEmpty || !window.Contains(first)) return;
            showTokens = showTokens && !second.IsEmpty && window.Contains(second);
            int left = first.Left - window.Left, top = first.Top - window.Top;
            int width = first.Width, height = first.Height;
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
                    int trailingLeft = second.Left - window.Left;
                    Rectangle trailing = new Rectangle(trailingLeft, top, second.Width, height);
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

        internal static Rectangle GetBadgeBounds(Rectangle row, Rectangle title, float scale, int part)
        {
            return GetAlignedBadgeBounds(row, title, scale, part, GetBadgeAnchorRight(row, scale));
        }

        internal static int GetSharedBadgeAnchorRight(IList<CodexSidebarContextRow> rows, float scale)
        {
            int left = Int32.MaxValue, right = Int32.MaxValue;
            foreach (CodexSidebarContextRow row in rows)
            {
                left = Math.Min(left, row.Bounds.Left);
                right = Math.Min(right, row.Bounds.Right);
            }
            if (rows.Count == 0) return 0;
            int groupLeft = Math.Min(right, left + (int)Math.Round(400 * scale)) - (int)Math.Round(177 * scale);
            int lastPercentageLeft = right - (int)Math.Round(79 * scale);
            foreach (CodexSidebarContextRow row in rows)
            {
                if (row.TitleBounds.IsEmpty || !row.Bounds.Contains(row.TitleBounds)) continue;
                int afterTitle = row.TitleBounds.Right + (int)Math.Round(8 * scale);
                if (afterTitle <= lastPercentageLeft) groupLeft = Math.Max(groupLeft, afterTitle);
            }
            return groupLeft + (int)Math.Round(177 * scale);
        }

        internal static Rectangle GetAlignedBadgeBounds(Rectangle row, Rectangle title, float scale, int part, int anchor)
        {
            if (title.IsEmpty || !row.Contains(title)) return Rectangle.Empty;
            int groupLeft = anchor - (int)Math.Round(177 * scale);
            int left = groupLeft + (int)Math.Round((part == 0 ? 0 : part == 1 ? 51 : 100) * scale);
            int width = (int)Math.Round((part == 0 ? 47 : 45) * scale);
            int height = Math.Min(row.Height - (int)Math.Round(2 * scale), (int)Math.Round((part == 0 ? 20 : 26) * scale));
            if (left < title.Right + (int)Math.Round(8 * scale) ||
                height < (int)(14 * scale) || left + width > row.Right - (int)Math.Round(32 * scale))
                return Rectangle.Empty;
            return new Rectangle(left, row.Top + (row.Height - height) / 2, width, height);
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
