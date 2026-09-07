using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CodexUsageOverlay
{
    internal static class UiRendering
    {
        public const float LogicalDpi = 96f;
        // On this Windows installation the Microsoft YaHei family is exposed as
        // "Microsoft YaHei UI"; using the registered name prevents a fallback.
        public const string PreferredFontName = "Microsoft YaHei UI";

        // Design handoff: opaque companion surfaces, shared by every panel.
        public static Color PanelColor(OverlaySettings settings, int role)
        {
            string key = settings == null ? "RainbowText" : settings.Theme;
            string[] colors;
            switch (key)
            {
                case "NeonBlue": colors = new[] { "#101E32", "#D2E8FA", "#2B435F", "#278BD5", "#162840", "#193E5D" }; break;
                case "FrostedGlass": colors = new[] { "#F0F5FA", "#374A60", "#D3DFEC", "#547FA6", "#E7EFF7", "#DCE9F6" }; break;
                case "OrangeGradient": colors = new[] { "#FFF8F1", "#704831", "#F2DDC9", "#C77432", "#FFF0E1", "#FAE3CD" }; break;
                case "PinkGradient": colors = new[] { "#FAFAFA", "#5A5F67", "#E0E1E4", "#646B75", "#F3F3F3", "#EAECEF" }; break;
                case "LightCard": colors = new[] { "#FFFFFF", "#4C5364", "#ECECF3", "#7772BA", "#F9F9FC", "#F0EEFB" }; break;
                case "Custom":
                    Color custom = Color.FromArgb(settings.CustomBackgroundArgb);
                    custom = Color.FromArgb(255, custom.R, custom.G, custom.B);
                    bool dark = custom.R * .299 + custom.G * .587 + custom.B * .114 < 150;
                    if (role == 0) return custom;
                    if (role == 1) return dark ? Color.White : Color.FromArgb(35, 45, 59);
                    if (role == 3) return dark ? Color.FromArgb(123, 190, 255) : Color.FromArgb(36, 77, 159);
                    int blend = role == 5 ? 45 : role == 2 ? 65 : 20;
                    int target = dark ? 255 : 0;
                    return Color.FromArgb(255, (custom.R * (255-blend) + target*blend)/255,
                        (custom.G * (255-blend) + target*blend)/255, (custom.B * (255-blend) + target*blend)/255);
                default: colors = new[] { "#FFFFFF", "#2B374A", "#E3E8F0", "#3168DD", "#F7F9FC", "#EDF3FF" }; break;
            }
            return ColorTranslator.FromHtml(colors[Math.Max(0, Math.Min(5, role))]);
        }

        private static readonly string[] FallbackFontNames =
        {
            PreferredFontName,
            "Microsoft YaHei UI",
            "Microsoft Sans Serif"
        };

        private static readonly string[] SafeTextFontNames =
        {
            "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "SimSun", "NSimSun",
            "DengXian", "FangSong", "KaiTi", "Arial", "Tahoma", "Calibri", "Microsoft Sans Serif",
            "Microsoft JhengHei UI", "Microsoft JhengHei", "Yu Gothic UI", "Meiryo UI",
            "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC"
        };

        public static Bitmap CreateLayeredBitmap(int width, int height)
        {
            Bitmap bitmap = new Bitmap(
                Math.Max(1, width),
                Math.Max(1, height),
                PixelFormat.Format32bppPArgb);
            bitmap.SetResolution(LogicalDpi, LogicalDpi);
            return bitmap;
        }

        public static Font CreateTextFont(string requestedName, float size, FontStyle style)
        {
            List<string> candidates = new List<string>();
            // The overlay intentionally uses one family everywhere. This keeps the
            // layered drawing, WinForms controls and embedded updater visually aligned.
            candidates.Add(PreferredFontName);
            foreach (string fallback in FallbackFontNames)
            {
                if (!ContainsIgnoreCase(candidates, fallback))
                    candidates.Add(fallback);
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    Font font = new Font(candidate, size, style, GraphicsUnit.Point);
                    if (IsSafeTextFontName(font.FontFamily.Name) &&
                        String.Equals(font.FontFamily.Name, candidate, StringComparison.OrdinalIgnoreCase))
                        return font;
                    font.Dispose();
                }
                catch
                {
                }
            }

            return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
        }

        public static string NormalizeFontName(string requestedName)
        {
            using (Font font = CreateTextFont(requestedName, 9f, FontStyle.Regular))
                return font.FontFamily.Name;
        }

        public static StringFormat CreateTextFormat()
        {
            StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags &= ~(StringFormatFlags.FitBlackBox |
                StringFormatFlags.LineLimit | StringFormatFlags.NoClip);
            return format;
        }

        public static float CalculateCenteredTextTranslationY(
            float inkTop,
            float inkHeight,
            Rectangle bounds)
        {
            return bounds.Top + (bounds.Height - inkHeight) / 2f - inkTop;
        }

        public static void DrawOpticallyCenteredText(
            Graphics graphics,
            string text,
            Font font,
            Brush brush,
            RectangleF bounds,
            StringAlignment alignment)
        {
            DrawOpticallyCenteredText(graphics, text, font, brush, bounds,
                alignment, 0f);
        }

        public static void DrawOpticallyCenteredText(
            Graphics graphics,
            string text,
            Font font,
            Brush brush,
            RectangleF bounds,
            StringAlignment alignment,
            float verticalOffset)
        {
            if (String.IsNullOrWhiteSpace(text))
                return;

            using (StringFormat format = CreateTextFormat())
            using (GraphicsPath path = new GraphicsPath())
            {
                format.Alignment = alignment;
                format.LineAlignment = StringAlignment.Near;
                format.FormatFlags |= StringFormatFlags.NoWrap;
                float emSize = font.SizeInPoints * graphics.DpiY / 72f;
                path.AddString(text, font.FontFamily, (int)font.Style, emSize,
                    bounds, format);
                RectangleF inkBounds = path.GetBounds();
                if (inkBounds.IsEmpty)
                    return;

                using (Matrix translate = new Matrix())
                {
                    translate.Translate(0f, bounds.Top +
                        (bounds.Height - inkBounds.Height) / 2f - inkBounds.Top +
                        verticalOffset);
                    path.Transform(translate);
                }
                graphics.FillPath(brush, path);
            }
        }

        public static bool IsCjkTextCharacter(char value)
        {
            return (value >= '\u2E80' && value <= '\u2EFF') ||
                (value >= '\u3000' && value <= '\u303F') ||
                (value >= '\u3400' && value <= '\u4DBF') ||
                (value >= '\u4E00' && value <= '\u9FFF') ||
                (value >= '\uF900' && value <= '\uFAFF');
        }

        public static void ResolveGearColors(
            string theme,
            int customBackgroundArgb,
            out Color start,
            out Color end)
        {
            if (String.Equals(theme, "FrostedGlass", StringComparison.Ordinal))
            {
                start = Color.FromArgb(255, 69, 132, 168);
                end = Color.FromArgb(255, 104, 169, 198);
            }
            else if (String.Equals(theme, "OrangeGradient", StringComparison.Ordinal))
            {
                start = Color.FromArgb(255, 246, 126, 35);
                end = Color.FromArgb(255, 255, 82, 93);
            }
            else if (String.Equals(theme, "PinkGradient", StringComparison.Ordinal))
            {
                // Retained key for existing settings. Its visual identity is now the
                // restrained native Codex surface rather than a pink gradient.
                start = Color.FromArgb(255, 67, 91, 125);
                end = Color.FromArgb(255, 110, 125, 147);
            }
            else if (String.Equals(theme, "LightCard", StringComparison.Ordinal))
            {
                start = Color.FromArgb(255, 102, 92, 242);
                end = Color.FromArgb(255, 183, 93, 246);
            }
            else if (String.Equals(theme, "Custom", StringComparison.Ordinal))
            {
                Color custom = Color.FromArgb(customBackgroundArgb);
                start = Color.FromArgb(255,
                    Math.Min(255, custom.R + 46),
                    Math.Min(255, custom.G + 46),
                    Math.Min(255, custom.B + 46));
                end = Color.FromArgb(255,
                    Math.Max(0, custom.R * 3 / 5),
                    Math.Max(0, custom.G * 3 / 5),
                    Math.Max(0, custom.B * 3 / 5));
            }
            else if (String.Equals(theme, "RainbowText", StringComparison.Ordinal))
            {
                start = Color.FromArgb(255, 255, 137, 47);
                end = Color.FromArgb(255, 70, 196, 255);
            }
            else
            {
                start = Color.FromArgb(255, 52, 195, 255);
                end = Color.FromArgb(255, 107, 111, 255);
            }
        }

        // The expanded settings panel and the updater are companion surfaces.
        // Keeping their core palette here prevents either window from drifting
        // when an appearance is adjusted.
        public static Color ResolveExpandedSettingsSurfaceColor(OverlaySettings settings)
        {
            return PanelColor(settings, 0);
        }

        public static Color ResolveExpandedSettingsInkColor(OverlaySettings settings)
        {
            return PanelColor(settings, 1);
        }

        public static void ResolveCapsuleSurfaceColors(
            string theme,
            out Color fill,
            out Color border)
        {
            if (String.Equals(theme, "NeonBlue", StringComparison.Ordinal))
            {
                fill = Color.FromArgb(228, 8, 61, 92);
                border = Color.FromArgb(215, 70, 196, 247);
                return;
            }
            if (String.Equals(theme, "OrangeGradient", StringComparison.Ordinal))
            {
                fill = Color.FromArgb(228, 178, 82, 36);
                border = Color.FromArgb(220, 255, 213, 135);
                return;
            }
            if (String.Equals(theme, "PinkGradient", StringComparison.Ordinal))
            {
                fill = Color.FromArgb(244, 255, 255, 255);
                border = Color.FromArgb(230, 218, 224, 232);
                return;
            }
            bool lightSurface = String.Equals(theme, "FrostedGlass",
                StringComparison.Ordinal) ||
                String.Equals(theme, "LightCard", StringComparison.Ordinal) ||
                String.Equals(theme, "RainbowText", StringComparison.Ordinal);
            fill = lightSurface
                ? Color.FromArgb(238, 255, 255, 255)
                : Color.FromArgb(155, 255, 255, 255);
            border = lightSurface
                ? Color.FromArgb(150, 188, 198, 209)
                : Color.FromArgb(180, 255, 255, 255);
        }

        public static void ResolveCapsuleSurfaceColors(
            string theme,
            int customBackgroundArgb,
            out Color fill,
            out Color border)
        {
            if (String.Equals(theme, "Custom", StringComparison.Ordinal))
            {
                Color custom = Color.FromArgb(customBackgroundArgb);
                fill = Color.FromArgb(226,
                    Math.Max(0, custom.R * 3 / 5),
                    Math.Max(0, custom.G * 3 / 5),
                    Math.Max(0, custom.B * 3 / 5));
                border = Color.FromArgb(220,
                    Math.Min(255, custom.R + 74),
                    Math.Min(255, custom.G + 74),
                    Math.Min(255, custom.B + 74));
                return;
            }
            ResolveCapsuleSurfaceColors(theme, out fill, out border);
        }

        // The conversation composer has a light surface and no overlay card behind the text.
        // Reuse the active theme accent, but darken it until it remains readable on that surface.
        public static Color ResolveComposerInsideTextColor(
            string theme,
            int customBackgroundArgb)
        {
            Color start;
            Color end;
            ResolveGearColors(theme, customBackgroundArgb, out start, out end);
            return DarkenForLightSurface(start);
        }

        // Text-only mode intentionally has no card behind it, so use the same
        // high-contrast ink as the light composer surface.
        public static Color ResolveTextOnlyInkColor(
            string theme,
            int customBackgroundArgb)
        {
            return ResolveComposerInsideTextColor(theme, customBackgroundArgb);
        }

        public static Color[] GetComposerInsideRainbowColors()
        {
            // These are deliberately darker than the standard RainbowText palette:
            // the composer has a white surface and needs reliable text contrast.
            return new[]
            {
                Color.FromArgb(255, 180, 65, 12),
                Color.FromArgb(255, 165, 28, 105),
                Color.FromArgb(255, 104, 45, 180),
                Color.FromArgb(255, 20, 101, 160)
            };
        }

        private static Color DarkenForLightSurface(Color source)
        {
            int red = source.R;
            int green = source.G;
            int blue = source.B;
            while (RelativeLuminance(red, green, blue) > 0.18d)
            {
                red = Math.Max(0, (int)Math.Round(red * 0.88d));
                green = Math.Max(0, (int)Math.Round(green * 0.88d));
                blue = Math.Max(0, (int)Math.Round(blue * 0.88d));
            }
            return Color.FromArgb(255, red, green, blue);
        }

        private static double RelativeLuminance(int red, int green, int blue)
        {
            return 0.2126d * Linearize(red / 255d) +
                0.7152d * Linearize(green / 255d) +
                0.0722d * Linearize(blue / 255d);
        }

        private static double Linearize(double channel)
        {
            return channel <= 0.04045d
                ? channel / 12.92d
                : Math.Pow((channel + 0.055d) / 1.055d, 2.4d);
        }

        public static bool IsSafeTextFontName(string fontName)
        {
            if (String.IsNullOrWhiteSpace(fontName))
                return false;
            string value = fontName.Trim();
            foreach (string safeName in SafeTextFontNames)
            {
                if (String.Equals(value, safeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool ContainsIgnoreCase(List<string> values, string candidate)
        {
            foreach (string value in values)
            {
                if (String.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
