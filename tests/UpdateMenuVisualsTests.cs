using System;
using System.Drawing;

namespace CodexUsageOverlay
{
    internal static class UpdateMenuVisualsTests
    {
        public static void UpdateMenuUsesReadableRainbowPalette()
        {
            UpdateMenuPalette palette = UpdateMenuVisuals.CreateRainbowPalette();
            double textContrast = UpdateMenuVisuals.ContrastRatio(palette.Text, palette.Surface);
            double accentContrast = UpdateMenuVisuals.ContrastRatio(palette.Accent, palette.Surface);
            double dangerContrast = UpdateMenuVisuals.ContrastRatio(palette.Danger, palette.Surface);
            Assert(textContrast >= 4.5d, "text contrast was " + textContrast.ToString("0.00"));
            Assert(accentContrast >= 4.5d, "accent contrast was " + accentContrast.ToString("0.00"));
            Assert(dangerContrast >= 4.5d, "exit contrast was " + dangerContrast.ToString("0.00"));
        }

        public static void RainbowMenuSeparatesUpdateAndExitActions()
        {
            UpdateMenuPalette palette = UpdateMenuVisuals.CreateRainbowPalette();
            Assert(palette.Accent.ToArgb() != palette.Danger.ToArgb(),
                "exit action reused the update accent");
            Assert(palette.Hover.ToArgb() != palette.DangerHover.ToArgb(),
                "exit action reused the normal hover color");
        }

        public static void UpdateMenuPaletteFollowsSelectedTheme()
        {
            UpdateMenuPalette native = UpdateMenuVisuals.CreatePalette(
                "PinkGradient", Color.Black.ToArgb());
            UpdateMenuPalette neon = UpdateMenuVisuals.CreatePalette(
                "NeonBlue", Color.Black.ToArgb());
            Assert(native.Surface.ToArgb() != neon.Surface.ToArgb(),
                "native and neon menus reused the same surface");
            Assert(UpdateMenuVisuals.ContrastRatio(native.Text, native.Surface) >= 4.5d,
                "native menu text is not readable");
            Assert(UpdateMenuVisuals.ContrastRatio(neon.Text, neon.Surface) >= 4.5d,
                "neon menu text is not readable");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
