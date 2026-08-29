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

        public static void BannerInkUsesTrueVerticalCenter()
        {
            Rectangle bounds = new Rectangle(12, 24, 400, 19);
            float translation = UiRendering.CalculateCenteredTextTranslationY(
                3f, 9f, bounds);
            float centeredTop = 3f + translation;
            Assert(Math.Abs(centeredTop - (bounds.Top + (bounds.Height - 9f) / 2f)) < 0.01f,
                "banner ink was not vertically centered in its row");
        }

        public static void MixedScriptsUseOpticalTextRuns()
        {
            Assert(UiRendering.IsCjkTextCharacter('中'),
                "Chinese characters were not recognized for optical centering");
            Assert(!UiRendering.IsCjkTextCharacter('T') &&
                !UiRendering.IsCjkTextCharacter('8'),
                "Latin or numeric characters were incorrectly treated as CJK");
        }

        public static void GearAccentFollowsTheme()
        {
            Color blueStart;
            Color blueEnd;
            Color orangeStart;
            Color orangeEnd;
            UiRendering.ResolveGearColors("NeonBlue", Color.Black.ToArgb(), out blueStart, out blueEnd);
            UiRendering.ResolveGearColors("OrangeGradient", Color.Black.ToArgb(), out orangeStart, out orangeEnd);
            Assert(blueStart.ToArgb() != orangeStart.ToArgb() &&
                blueEnd.ToArgb() != orangeEnd.ToArgb(),
                "gear accent did not change with the active theme");
        }

        public static void CapsuleSurfaceStaysConsistentAcrossThemes()
        {
            Color rainbowFill;
            Color rainbowBorder;
            Color neonFill;
            Color neonBorder;
            UiRendering.ResolveCapsuleSurfaceColors("RainbowText",
                out rainbowFill, out rainbowBorder);
            UiRendering.ResolveCapsuleSurfaceColors("NeonBlue",
                out neonFill, out neonBorder);

            Assert(rainbowFill.R == 255 && rainbowFill.G == 255 && rainbowFill.B == 255,
                "rainbow capsule surface is not the shared light surface");
            Assert(rainbowBorder.A > 0 && neonBorder.A > 0 && neonFill.A > 0,
                "capsule surface did not return visible shared borders");
        }

        public static void ComposerInsideInkRemainsReadableOnLightSurface()
        {
            Color orange = UiRendering.ResolveComposerInsideTextColor(
                "OrangeGradient", Color.Black.ToArgb());
            Color pink = UiRendering.ResolveComposerInsideTextColor(
                "PinkGradient", Color.Black.ToArgb());
            Color custom = UiRendering.ResolveComposerInsideTextColor(
                "Custom", Color.White.ToArgb());
            Assert(ContrastAgainstWhite(orange) >= 4.5d,
                "orange composer text is too light for a white input surface");
            Assert(ContrastAgainstWhite(pink) >= 4.5d,
                "pink composer text is too light for a white input surface");
            Assert(ContrastAgainstWhite(custom) >= 4.5d,
                "custom composer text is too light for a white input surface");
        }

        public static void ComposerInsideRainbowInkIsDistinctAndReadable()
        {
            Color[] colors = UiRendering.GetComposerInsideRainbowColors();
            Assert(colors != null && colors.Length >= 4,
                "composer rainbow palette does not contain enough colors");
            Assert(colors[0].ToArgb() != colors[1].ToArgb() &&
                colors[1].ToArgb() != colors[2].ToArgb() &&
                colors[2].ToArgb() != colors[3].ToArgb(),
                "composer rainbow palette collapsed to a single color");
            foreach (Color color in colors)
            {
                Assert(ContrastAgainstWhite(color) >= 4.5d,
                    "composer rainbow text is too light for a white input surface");
            }
        }

        public static void PlanLabelUsesOpticalVerticalCenter()
        {
            Rectangle bounds = new Rectangle(0, 0, 80, 16);
            using (Bitmap bitmap = UiRendering.CreateLayeredBitmap(80, 24))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font font = UiRendering.CreateTextFont("Microsoft YaHei UI", 7.2f,
                FontStyle.Bold))
            using (Brush brush = new SolidBrush(Color.Black))
            {
                graphics.Clear(Color.Transparent);
                UiRendering.DrawOpticallyCenteredText(graphics, "PRO", font, brush,
                    bounds, StringAlignment.Center);
                RectangleF ink = GetVisibleBounds(bitmap);
                Assert(!ink.IsEmpty, "optically centered plan label did not render");
                float expectedTop = bounds.Top + (bounds.Height - ink.Height) / 2f;
                Assert(Math.Abs(ink.Top - expectedTop) <= 1f,
                    "plan label ink was not vertically centered");
            }
        }

        private static double ContrastAgainstWhite(Color color)
        {
            return 1.05d / (RelativeLuminance(color) + 0.05d);
        }

        private static double RelativeLuminance(Color color)
        {
            return 0.2126d * Linearize(color.R / 255d) +
                0.7152d * Linearize(color.G / 255d) +
                0.0722d * Linearize(color.B / 255d);
        }

        private static double Linearize(double channel)
        {
            return channel <= 0.04045d
                ? channel / 12.92d
                : Math.Pow((channel + 0.055d) / 1.055d, 2.4d);
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
