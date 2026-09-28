using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal sealed class CodexTokenUsagePopup : Form
    {
        private CodexContextSignal signal = CodexContextSignal.Empty;
        private float scale = 1f;
        private bool dark;

        internal CodexTokenUsagePopup()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var value = base.CreateParams;
                value.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                return value;
            }
        }

        internal void ShowUsage(Form owner, CodexContextSignal current, string theme, Rectangle anchor, float dpiScale)
        {
            signal = current;
            scale = Math.Max(.75f, dpiScale);
            dark = theme == "NeonBlue";
            Rectangle work = Screen.FromRectangle(anchor).WorkingArea;
            Size size = new Size(Math.Min(work.Width, (int)(310 * scale)), Math.Min(work.Height, (int)(172 * scale)));
            int left = Math.Max(work.Left, Math.Min(anchor.Left + anchor.Width / 2 - size.Width / 2, work.Right - size.Width));
            int top = Math.Max(work.Top, anchor.Top - size.Height - (int)(5 * scale));
            Bounds = new Rectangle(left, top, size.Width, size.Height);
            using (GraphicsPath path = Shape(new Rectangle(0, 0, Width, Height), (int)(8 * scale)))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
            if (!Visible) Show(owner);
            Invalidate();
        }

        internal static string[] DetailValues(CodexContextSignal current)
        {
            if (!current.HasSessionUsage) return new[] { "—", "—", "—", "—", "—" };
            return new[] { Exact(current.SessionTokens), current.CacheHitText,
                Exact(current.SessionInputTokens - current.SessionCachedTokens),
                Exact(current.SessionCachedTokens), Exact(current.SessionOutputTokens) };
        }
        private static string Exact(long value) { return value.ToString("N0", CultureInfo.InvariantCulture) + " tok"; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Color background = dark ? Color.FromArgb(39, 46, 54) : Color.FromArgb(232, 234, 237);
            Color ink = dark ? Color.FromArgb(225, 229, 232) : Color.FromArgb(66, 73, 80);
            Color muted = dark ? Color.FromArgb(162, 171, 180) : Color.FromArgb(116, 123, 130);
            Color border = dark ? Color.FromArgb(65, 73, 82) : Color.FromArgb(210, 215, 220);
            graphics.Clear(background);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float fontScale = scale * 96f / Math.Max(1f, graphics.DpiY);
            int pad = (int)(15 * scale), rowHeight = (int)(24 * scale);
            using (Font font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 8.8f * fontScale, FontStyle.Regular))
            using (Font small = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 7.4f * fontScale, FontStyle.Regular))
            using (Pen line = new Pen(border, Math.Max(1f, scale)))
            using (GraphicsPath outline = Shape(new Rectangle(0, 0, Width - 1, Height - 1), (int)(8 * scale)))
            {
                graphics.DrawPath(line, outline);
                string[] labels = { "本会话 Token", "缓存命中", "未缓存输入", "缓存读取", "输出" };
                string[] values = DetailValues(signal);
                TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
                for (int i = 0; i < labels.Length; i++)
                {
                    int y = pad + i * rowHeight + (i > 0 ? (int)(9 * scale) : 0);
                    TextRenderer.DrawText(graphics, labels[i], font, new Rectangle(pad, y, (int)(100 * scale), rowHeight),
                        i == 0 ? ink : muted, flags);
                    TextRenderer.DrawText(graphics, values[i], font,
                        new Rectangle(pad + (int)(102 * scale), y, Width - 2 * pad - (int)(102 * scale), rowHeight),
                        ink, flags | TextFormatFlags.Right);
                }
                graphics.DrawLine(line, pad, pad + rowHeight + (int)(4 * scale), Width - pad, pad + rowHeight + (int)(4 * scale));
                string note = !signal.HasSessionUsage ? "本会话累计数据暂不可用" :
                    signal.IsRecent(DateTime.UtcNow) ? "本机累计记录 · 缓存已计入输入" : "上次累计记录 · 缓存已计入输入";
                TextRenderer.DrawText(graphics, note, small,
                    new Rectangle(pad, Height - (int)(22 * scale), Width - pad * 2, (int)(17 * scale)), muted, flags);
            }
        }

        private static GraphicsPath Shape(Rectangle bounds, int radius)
        {
            int d = Math.Max(2, Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2));
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
