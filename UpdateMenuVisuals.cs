using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal sealed class UpdateMenuPalette
    {
        public Color Surface;
        public Color SurfaceAlt;
        public Color Border;
        public Color Text;
        public Color MutedText;
        public Color Accent;
        public Color Hover;
        public Color Danger;
        public Color DangerHover;
    }

    internal static class UpdateMenuVisuals
    {
        internal const string HeaderTag = "update-menu-header";
        internal const string DangerTag = "update-menu-danger";

        internal static UpdateMenuPalette CreateRainbowPalette()
        {
            return CreatePalette("RainbowText", Color.FromArgb(24, 99, 171).ToArgb());
        }

        internal static UpdateMenuPalette CreatePalette(string theme, int customBackgroundArgb)
        {
            OverlaySettings settings = new OverlaySettings { Theme = theme, CustomBackgroundArgb = customBackgroundArgb };
            bool dark = theme == "NeonBlue" || (theme == "Custom" && UiRendering.PanelColor(settings, 1) == Color.White);
            return new UpdateMenuPalette
            {
                Surface = UiRendering.PanelColor(settings, 0),
                SurfaceAlt = UiRendering.PanelColor(settings, 4),
                Border = UiRendering.PanelColor(settings, 2),
                Text = UiRendering.PanelColor(settings, 1),
                MutedText = UiRendering.PanelColor(settings, 1),
                Accent = UiRendering.PanelColor(settings, 3),
                Hover = UiRendering.PanelColor(settings, 5),
                Danger = dark ? Color.FromArgb(237, 137, 149) : Color.FromArgb(176, 57, 74),
                DangerHover = dark ? Color.FromArgb(82, 40, 53) : Color.FromArgb(255, 237, 240)
            };
        }

        internal static void Apply(
            ContextMenuStrip menu,
            ToolStripMenuItem versionItem,
            ToolStripMenuItem checkItem,
            ToolStripMenuItem downloadItem,
            ToolStripMenuItem exitItem,
            string theme,
            int customBackgroundArgb,
            float scale)
        {
            int width = Scale(228, scale);
            int horizontalPadding = Scale(6, scale);
            int itemWidth = width - horizontalPadding * 2;
            // The menu shares the selected companion-panel theme.
            UpdateMenuPalette palette = CreatePalette(theme, customBackgroundArgb);
            menu.AutoSize = false;
            menu.LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
            menu.Padding = new Padding(horizontalPadding, Scale(4, scale), horizontalPadding, Scale(5, scale));
            menu.MinimumSize = Size.Empty;
            menu.Size = new Size(width, Scale(142, scale));
            menu.MinimumSize = menu.Size;
            menu.BackColor = palette.Surface;
            menu.ForeColor = palette.Text;
            menu.Renderer = new OverlayUpdateMenuRenderer(palette);

            ConfigureItem(versionItem, itemWidth, Scale(26, scale), Scale(10, scale));
            ConfigureItem(checkItem, itemWidth, Scale(29, scale), Scale(10, scale));
            ConfigureItem(downloadItem, itemWidth, Scale(29, scale), Scale(10, scale));
            ConfigureItem(exitItem, itemWidth, Scale(29, scale), Scale(10, scale));
            versionItem.Tag = HeaderTag;
            exitItem.Tag = DangerTag;

            for (int index = 0; index < menu.Items.Count; index++)
            {
                ToolStripSeparator separator = menu.Items[index] as ToolStripSeparator;
                if (separator != null)
                {
                    separator.AutoSize = false;
                    separator.Size = new Size(itemWidth, Scale(6, scale));
                    separator.Margin = Padding.Empty;
                }
            }
        }

        internal static double ContrastRatio(Color first, Color second)
        {
            double light = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
            double dark = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
            return (light + 0.05d) / (dark + 0.05d);
        }

        private static void ConfigureItem(ToolStripItem item, int width, int height, int leftPadding)
        {
            item.AutoSize = false;
            item.Size = new Size(width, height);
            item.Margin = Padding.Empty;
            item.Padding = new Padding(leftPadding, 0, Scale(7, width / 252f), 0);
            item.TextAlign = ContentAlignment.MiddleLeft;
        }

        private static int Scale(int value, float scale)
        {
            return Math.Max(1, (int)Math.Round(value * Math.Max(0.75f, scale)));
        }

        private static Color Opaque(Color color)
        {
            return Color.FromArgb(255, color.R, color.G, color.B);
        }

        private static Color Blend(Color first, Color second, float secondWeight)
        {
            float weight = Math.Max(0f, Math.Min(1f, secondWeight));
            return Color.FromArgb(255,
                (int)Math.Round(first.R + (second.R - first.R) * weight),
                (int)Math.Round(first.G + (second.G - first.G) * weight),
                (int)Math.Round(first.B + (second.B - first.B) * weight));
        }

        private static double RelativeLuminance(Color color)
        {
            return 0.2126d * Linear(color.R) + 0.7152d * Linear(color.G) + 0.0722d * Linear(color.B);
        }

        private static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.03928d ? value / 12.92d : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }
    }

    internal sealed class OverlayUpdateContextMenu : ContextMenuStrip
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRoundedRegion();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ApplyRoundedRegion();
        }

        private void ApplyRoundedRegion()
        {
            if (Width <= 0 || Height <= 0)
                return;
            using (GraphicsPath path = OverlayUpdateMenuRenderer.RoundedRectangle(
                new Rectangle(0, 0, Width, Height), Math.Max(8, Width / 24)))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null)
                    old.Dispose();
            }
        }
    }

    internal sealed class OverlayUpdateMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly UpdateMenuPalette palette;

        internal OverlayUpdateMenuRenderer(UpdateMenuPalette palette)
        {
            this.palette = palette;
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Rectangle bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;
            using (LinearGradientBrush background = new LinearGradientBrush(
                bounds, palette.SurfaceAlt, palette.Surface, LinearGradientMode.Vertical))
                e.Graphics.FillRectangle(background, bounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle bounds = new Rectangle(0, 0,
                Math.Max(1, e.ToolStrip.Width - 1), Math.Max(1, e.ToolStrip.Height - 1));
            using (GraphicsPath path = RoundedRectangle(bounds, Math.Max(8, e.ToolStrip.Width / 24)))
            using (Pen border = new Pen(palette.Border, 1f))
                e.Graphics.DrawPath(border, path);
            Rectangle rail = new Rectangle(2, 13, 3, Math.Max(1, e.ToolStrip.Height - 26));
            using (LinearGradientBrush accent = new LinearGradientBrush(
                rail, palette.Accent, palette.Danger, LinearGradientMode.Vertical))
                e.Graphics.FillRectangle(accent, rail);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled || IsHeader(e.Item))
                return;
            Rectangle bounds = new Rectangle(3, 2,
                Math.Max(1, e.Item.Width - 6), Math.Max(1, e.Item.Height - 4));
            using (GraphicsPath path = RoundedRectangle(bounds, Math.Max(5, e.Item.Height / 4)))
            using (Brush hover = new SolidBrush(IsDanger(e.Item) ? palette.DangerHover : palette.Hover))
                e.Graphics.FillPath(hover, path);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen separator = new Pen(Color.FromArgb(120, palette.Border), 1f))
                e.Graphics.DrawLine(separator, 13, y, Math.Max(13, e.Item.Width - 10), y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (IsHeader(e.Item))
                e.TextColor = palette.Accent;
            else if (IsDanger(e.Item))
                e.TextColor = palette.Danger;
            else if (!e.Item.Enabled)
                e.TextColor = palette.MutedText;
            else
                e.TextColor = palette.Text;
            base.OnRenderItemText(e);
        }

        internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Max(2, radius * 2);
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

        private static bool IsHeader(ToolStripItem item)
        {
            return String.Equals(item.Tag as string, UpdateMenuVisuals.HeaderTag, StringComparison.Ordinal);
        }

        private static bool IsDanger(ToolStripItem item)
        {
            return String.Equals(item.Tag as string, UpdateMenuVisuals.DangerTag, StringComparison.Ordinal);
        }
    }
}
