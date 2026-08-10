using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CodexUsageOverlay
{
    internal static class RenderingCompatibilityTests
    {
        public static void LayeredBitmapUsesLogicalDpi()
        {
            using (Bitmap bitmap = UiRendering.CreateLayeredBitmap(450, 48))
            {
                Assert(Math.Abs(bitmap.VerticalResolution - UiRendering.LogicalDpi) < 0.01f,
                    "unexpected bitmap DPI " + bitmap.VerticalResolution);
            }
        }

        public static void UnsafeFontFallsBackToTextFont()
        {
            using (Font font = UiRendering.CreateTextFont("Segoe MDL2 Assets", 8.5f, FontStyle.Bold))
            using (Font emoji = UiRendering.CreateTextFont("Segoe UI Emoji", 8.5f, FontStyle.Bold))
            using (Font mathSymbols = UiRendering.CreateTextFont("MT Extra", 8.5f, FontStyle.Bold))
            using (Font missing = UiRendering.CreateTextFont("Definitely Missing Text Font 2026", 8.5f, FontStyle.Bold))
            {
                Assert(UiRendering.IsSafeTextFontName(font.FontFamily.Name),
                    "unsafe font remained active: " + font.FontFamily.Name);
                Assert(!String.Equals(font.FontFamily.Name, "Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase),
                    "symbol font was not replaced");
                Assert(String.Equals(font.FontFamily.Name, missing.FontFamily.Name, StringComparison.OrdinalIgnoreCase),
                    "unsafe and missing fonts did not use the same fallback");
                Assert(String.Equals(emoji.FontFamily.Name, missing.FontFamily.Name, StringComparison.OrdinalIgnoreCase),
                    "emoji font did not use the text fallback");
                Assert(String.Equals(mathSymbols.FontFamily.Name, missing.FontFamily.Name, StringComparison.OrdinalIgnoreCase),
                    "math symbol font did not use the text fallback");
                Assert(!UiRendering.IsSafeTextFontName("MT Extra"),
                    "math symbol font was exposed as a text font");
            }
        }

        public static void TextRendersAtMixedDpiScale()
        {
            RectangleF baseline = RenderVisibleBounds(1f);
            float[] scales = { 1.25f, 1.5f, 2f };
            foreach (float scale in scales)
            {
                RectangleF visible = RenderVisibleBounds(scale);
                Assert(visible.Width > 0 && visible.Height > 0,
                    "text rendered blank at " + scale + "x");
                Assert(Math.Abs(visible.Width / scale - baseline.Width) <= 2.5f,
                    "text width was scaled twice at " + scale + "x");
                Assert(Math.Abs(visible.Height / scale - baseline.Height) <= 2.5f,
                    "text height was scaled twice at " + scale + "x");
            }

            using (StringFormat format = UiRendering.CreateTextFormat())
            {
                StringFormatFlags forbidden = StringFormatFlags.FitBlackBox |
                    StringFormatFlags.LineLimit | StringFormatFlags.NoClip;
                Assert((format.FormatFlags & forbidden) == 0,
                    "single-line format contains unsafe typographic flags");
            }
        }

        private static RectangleF RenderVisibleBounds(float scale)
        {
            using (Bitmap bitmap = UiRendering.CreateLayeredBitmap(
                (int)Math.Round(450 * scale),
                (int)Math.Round(48 * scale)))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font font = UiRendering.CreateTextFont("Segoe MDL2 Assets", 8.5f, FontStyle.Bold))
            using (Brush brush = new SolidBrush(Color.White))
            using (StringFormat format = UiRendering.CreateTextFormat())
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.ScaleTransform(scale, scale);
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags |= StringFormatFlags.NoWrap;
                graphics.DrawString("TIBO RADAR · 预计今日15:00后有重置", font, brush,
                    new RectangleF(28, 4, 360, 20), format);
                return GetVisibleBounds(bitmap);
            }
        }

        private static RectangleF GetVisibleBounds(Bitmap bitmap)
        {
            int left = bitmap.Width;
            int top = bitmap.Height;
            int right = -1;
            int bottom = -1;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A != 0)
                    {
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }
            if (right < left || bottom < top)
                return RectangleF.Empty;
            return new RectangleF(left, top, right - left + 1, bottom - top + 1);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
