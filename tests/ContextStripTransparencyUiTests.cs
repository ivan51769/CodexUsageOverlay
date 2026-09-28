// Exercises only this test's companion windows and generated bitmaps; no user app is controlled.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class ContextStripTransparencyUiTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Layered = 0x00080000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private static Assembly app;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            app = Assembly.LoadFrom(args[0]);
            bool skipNativeHit = Array.IndexOf(args, "--skip-native-hit") >= 0;
            if (skipNativeHit) Console.WriteLine("SKIP native WindowFromPoint assertion: desktop is locked/unavailable; pixel, region and hover checks remain enabled");
            Type formType = app.GetType("CodexUsageOverlay.CodexContextNudgeForm", true);
            if (args.Length > 1 && args[1] == "--font-probe")
            {
                SaveFontComparison(formType, Path.Combine(Path.GetDirectoryName(args[0]), "context-font-comparison.png"));
                return 0;
            }
            VerifyCompleteCompactText(formType);
            object signal = New("CodexUsageOverlay.CodexContextSignal");
            Set(signal, "UsedTokens", 114000L); Set(signal, "WindowTokens", 200000L);
            Set(signal, "ObservedAt", DateTimeOffset.UtcNow); Set(signal, "HasSessionUsage", true);
            Set(signal, "SessionTokens", 145414156L); Set(signal, "SessionInputTokens", 144588335L);
            Set(signal, "SessionCachedTokens", 142586240L); Set(signal, "SessionOutputTokens", 825821L);
            using (Form form = (Form)Activator.CreateInstance(formType, All, null,
                new object[] { (Action)delegate { }, (Action)delegate { } }, null))
            {
                foreach (float scale in new[] { 1f, 1.125f, 1.25f, 1.5f, 1.75f, 2f })
                foreach (string theme in new[] { "RainbowText", "NeonBlue" })
                {
                    object settings = Settings(theme);
                    int width = (int)CallStatic(formType, "MeasureCompactWidth", signal, scale);
                    int singleHeight = (int)CallStatic(formType, "MeasureCompactSingleRowHeight", scale);
                    int doubleHeight = 2 * (int)Math.Round(20 * scale);
                    foreach (int height in new[] { (int)Math.Round(42 * scale), singleHeight, doubleHeight })
                    {
                        Rectangle bounds = new Rectangle(120, 150, width, height);
                        Call(form, "UpdateBanner", signal, settings, bounds, scale, true);
                        Application.DoEvents();
                        Assert(form.Bounds == bounds, "transparency changed the requested placement");
                        Assert((GetWindowLong(form.Handle, -20) & Layered) != 0,
                            "compact strip is not a real layered window");
                        using (Bitmap normal = (Bitmap)Call(form, "BuildCompactBitmap"))
                        {
                            VerifyTransparent(normal, height >= doubleHeight, scale);
                            if (height >= doubleHeight) VerifyCompactSpacing(normal, form, scale);
                            Rectangle token = (Rectangle)Get(form, "tokenCapsuleBounds");
                            Assert(!token.IsEmpty && form.ClientRectangle.Contains(token), "token hover area escaped the rendered row");
                            Assert(CountAlpha(normal, token, 1, 1) > 0,
                                "token text gaps have no minimally visible hit surface");
                            Point hit = FindHitPixel(normal, token);
                            if (!skipNativeHit)
                            {
                                // Raise only this owned test window, without changing focus or the cursor.
                                Call(form, "HideTokenPopup");
                                SetWindowPos(form.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
                                Assert(WindowFromPoint(form.PointToScreen(hit)) == form.Handle,
                                    "transparent token text gaps pass native hit testing through the strip");
                            }
                            // Send only to this test-owned window; do not move the user's cursor.
                            SendMessage(form.Handle, 0x0200, IntPtr.Zero, new IntPtr((hit.Y << 16) | (hit.X & 0xffff)));
                            using (Bitmap hovered = (Bitmap)Call(form, "BuildCompactBitmap"))
                            {
                                VerifyTransparent(hovered, height >= doubleHeight, scale);
                                AssertBlankPixelsRemainTransparent(normal, hovered);
                            }
                            Form popup = Get(form, "tokenPopup") as Form;
                            Assert(popup != null && popup.Visible, "token hover no longer opens usage details");
                            SendMessage(form.Handle, 0x02A3, IntPtr.Zero, IntPtr.Zero);
                            Assert(!popup.Visible, "token popup remained visible after leaving the strip");
                        }
                    }
                }
                Console.WriteLine("PASS transparent single/double rows, hover details and geometry at 100-200 percent DPI");

                object light = Settings("RainbowText");
                Call(form, "UpdateBanner", signal, light, new Rectangle(120, 150, 374, 126), 1f, false);
                Application.DoEvents();
                Assert((GetWindowLong(form.Handle, -20) & Layered) == 0,
                    "full context card retained the compact-only layered style");
                using (Bitmap card = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(card, form.ClientRectangle);
                    Assert(card.GetPixel(card.Width / 2, card.Height / 2).A == 255,
                        "full context card unexpectedly became transparent");
                }
                int normalWidth = (int)CallStatic(formType, "MeasureCompactWidth", signal, 1f);
                Call(form, "UpdateBanner", signal, light, new Rectangle(120, 150, normalWidth, 42), 1f, true);
                Application.DoEvents();
                Assert((GetWindowLong(form.Handle, -20) & Layered) != 0,
                    "compact mode did not restore layered rendering after the full card");
                using (Bitmap restored = (Bitmap)Call(form, "BuildCompactBitmap")) VerifyTransparent(restored, true, 1f);
                Console.WriteLine("PASS one form switches compact/full/compact without leaving stale window styles");
                SavePreview(form, signal, Path.Combine(Path.GetDirectoryName(args[0]), "context-strip-transparent-preview.png"));
                SaveFontComparison(formType, Path.Combine(Path.GetDirectoryName(args[0]), "context-font-comparison.png"));
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void VerifyTransparent(Bitmap bitmap, bool twoRows, float scale)
    {
        Assert(bitmap.PixelFormat == PixelFormat.Format32bppPArgb || bitmap.PixelFormat == PixelFormat.Format32bppArgb,
            "compact rendering did not produce an alpha-capable bitmap");
        Assert(bitmap.GetPixel(0, 0).A <= 1 && bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).A <= 1,
            "compact background corners are still opaque");
        Rectangle area = new Rectangle(Point.Empty, bitmap.Size);
        int blank = CountAlpha(bitmap, area, 0, 1);
        int ink = CountAlpha(bitmap, area, 101, 255);
        Assert(blank > bitmap.Width * bitmap.Height / 2, "visible strip background remains behind the text");
        Assert(ink > 40 * scale, "transparent rendering lost the text and icons");
        if (twoRows)
        {
            int row = (int)Math.Round(20 * scale);
            Assert(CountAlpha(bitmap, new Rectangle(0, bitmap.Height - row, bitmap.Width, row), 101, 255) > 20 * scale,
                "restoring two rows failed to restore the session summary");
        }
    }

    private static int CountAlpha(Bitmap bitmap, Rectangle area, int min, int max)
    {
        int count = 0;
        area.Intersect(new Rectangle(Point.Empty, bitmap.Size));
        for (int y = area.Top; y < area.Bottom; y++)
        for (int x = area.Left; x < area.Right; x++)
        {
            int alpha = bitmap.GetPixel(x, y).A;
            if (alpha >= min && alpha <= max) count++;
        }
        return count;
    }

    private static void VerifyCompactSpacing(Bitmap bitmap, Form form, float scale)
    {
        List<Rectangle> bands = new List<Rectangle>();
        int start = -1;
        for (int y = 0; y <= bitmap.Height; y++)
        {
            bool ink = false;
            if (y < bitmap.Height)
                for (int x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).A > 16)
                    {
                        ink = true;
                        Assert(form.Region == null || form.Region.IsVisible(x, y),
                            "tight spacing clipped text or icons against the native window region");
                    }
            if (ink && start < 0) start = y;
            if (!ink && start >= 0)
            {
                bands.Add(new Rectangle(0, start, bitmap.Width, y - start));
                start = -1;
            }
        }
        Assert(bands.Count == 2, "compact rows overlap or have clipped glyph strokes");
        int gap = bands[1].Top - bands[0].Bottom;
        int bottom = bitmap.Height - bands[1].Bottom;
        Console.WriteLine("Spacing at " + scale + ", height " + bitmap.Height + ": gap=" + gap + ", bottom=" + bottom);
        Assert(gap >= Math.Max(1, (int)Math.Round(2 * scale)) && gap <= (int)Math.Round(6 * scale),
            "two-row visual gap must be compact without merging the rows");
        Assert(bottom >= 1 && bottom <= (int)Math.Round(3 * scale),
            "bottom visual padding should be compact without clipping the summary");
    }

    private static Point FindHitPixel(Bitmap bitmap, Rectangle area)
    {
        for (int y = area.Top + 1; y < area.Bottom - 1; y++)
        for (int x = area.Left + 1; x < area.Right - 1; x++)
            if (bitmap.GetPixel(x, y).A == 1) return new Point(x, y);
        throw new Exception("No transparent token hit point found");
    }

    private static void AssertBlankPixelsRemainTransparent(Bitmap normal, Bitmap hovered)
    {
        Assert(normal.Size == hovered.Size, "hover changed strip dimensions");
        for (int y = 0; y < normal.Height; y++)
        for (int x = 0; x < normal.Width; x++)
            if (normal.GetPixel(x, y).A <= 1)
                Assert(hovered.GetPixel(x, y).A <= 1, "hover restored a visible token capsule background");
    }

    private static void SavePreview(Form form, object signal, string path)
    {
        const int width = 800, band = 110;
        using (Bitmap preview = new Bitmap(width, band * 4))
        using (Graphics graphics = Graphics.FromImage(preview))
        {
            int index = 0;
            foreach (string theme in new[] { "RainbowText", "NeonBlue" })
            foreach (bool single in new[] { false, true })
            {
                object settings = Settings(theme);
                int stripWidth = (int)CallStatic(form.GetType(), "MeasureCompactWidth", signal, 1.5f);
                int stripHeight = single ? (int)CallStatic(form.GetType(), "MeasureCompactSingleRowHeight", 1.5f) : 63;
                Call(form, "UpdateBanner", signal, settings, new Rectangle(120, 150, stripWidth, stripHeight), 1.5f, true);
                using (Bitmap strip = (Bitmap)Call(form, "BuildCompactBitmap"))
                using (Brush background = new SolidBrush(theme == "NeonBlue" ? Color.FromArgb(20, 24, 29) : Color.FromArgb(250, 250, 250)))
                using (Brush label = new SolidBrush(theme == "NeonBlue" ? Color.Silver : Color.DimGray))
                {
                    graphics.FillRectangle(background, 0, index * band, width, band);
                    graphics.DrawString((theme == "NeonBlue" ? "Dark" : "Light") + (single ? " / single row" : " / two rows"),
                        SystemFonts.MessageBoxFont, label, 12, index * band + 8);
                    graphics.DrawImageUnscaled(strip, (width - strip.Width) / 2, index * band + 35);
                }
                index++;
            }
            preview.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("Preview: " + path);
    }

    private static void SaveFontComparison(Type formType, string path)
    {
        using (Bitmap preview = new Bitmap(1120, 480))
        using (Graphics output = Graphics.FromImage(preview))
        {
            output.Clear(Color.FromArgb(250, 250, 250));
            int band = 0;
            double outlineAlpha = 0;
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
            foreach (bool hinted in new[] { false, true })
            using (Bitmap text = new Bitmap(1000, 56, PixelFormat.Format32bppPArgb))
            using (Graphics graphics = Graphics.FromImage(text))
            using (Font font = new Font("Microsoft YaHei UI", 8.2f * scale))
            using (Brush brush = new SolidBrush(Color.FromArgb(104, 111, 118)))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                string value = "上下文 100%（上次记录）  716M tok · 缓存命中 100%  本会话 输入 2.8M · 缓存 143M · 输出 858K";
                RectangleF bounds = new RectangleF(2, 4, text.Width - 4, 40);
                if (!hinted)
                {
                    Type rendering = app.GetType("CodexUsageOverlay.UiRendering", true);
                    rendering.GetMethod("DrawOpticallyCenteredText", All, null,
                        new[] { typeof(Graphics), typeof(string), typeof(Font), typeof(Brush), typeof(RectangleF), typeof(StringAlignment) }, null)
                        .Invoke(null, new object[] { graphics, value, font, brush, bounds, StringAlignment.Near });
                }
                else CallStatic(formType, "DrawCompactText", graphics, value, font, brush, bounds, StringAlignment.Near);
                int sum = 0, inkCount = 0, strong = 0;
                for (int y = 0; y < text.Height; y++)
                for (int x = 0; x < text.Width; x++)
                {
                    Color pixel = text.GetPixel(x, y);
                    int alpha = pixel.A;
                    if (alpha > 16) { inkCount++; sum += alpha; if (alpha >= 220) strong++; }
                    if (hinted && alpha > 64)
                        Assert(Math.Abs(pixel.R - 104) <= 4 && Math.Abs(pixel.G - 111) <= 4 && Math.Abs(pixel.B - 118) <= 4,
                            "transparent text introduced colored subpixel fringes");
                }
                double averageAlpha = sum / (double)Math.Max(1, inkCount);
                if (!hinted) outlineAlpha = averageAlpha;
                else
                {
                    Assert(averageAlpha >= outlineAlpha + 5, "hinted small text lost its sharper stroke coverage");
                    Assert(inkCount > 1000 * scale && inkCount < text.Width * text.Height / 3,
                        "hinted text disappeared or introduced a background");
                }
                Console.WriteLine("Font " + scale + (hinted ? " hinted" : " outline") + ": ink=" + inkCount +
                    ", averageAlpha=" + averageAlpha.ToString("0.0") + ", solid=" + strong);
                output.DrawString(scale + (hinted ? " / hinted" : " / outline"), SystemFonts.MessageBoxFont, Brushes.DimGray, 2, band * 60 + 2);
                output.DrawImageUnscaled(text, 100, band * 60);
                band++;
            }
            preview.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("Font comparison: " + path);
    }

    private static void VerifyCompleteCompactText(Type formType)
    {
        foreach (float scale in new[] { 1f, 1.125f, 1.25f, 1.5f, 1.75f, 2f })
        foreach (float size in new[] { 7.2f, 8.2f })
        foreach (string value in new[] { "上下文 100%（上次记录）", "716M tok · 缓存命中 100%" })
        using (Font font = new Font("Microsoft YaHei UI", size * scale))
        {
            Size measured = TextRenderer.MeasureText(value, font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            using (Bitmap fitted = new Bitmap(measured.Width + 4, measured.Height + 4, PixelFormat.Format32bppPArgb))
            using (Bitmap roomy = new Bitmap(measured.Width + 100, measured.Height + 4, PixelFormat.Format32bppPArgb))
            {
                foreach (Bitmap bitmap in new[] { fitted, roomy })
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                        CallStatic(formType, "DrawCompactText", graphics, value, font, Brushes.White,
                            new RectangleF(2, 2, bitmap.Width - 4, measured.Height), StringAlignment.Near);
                Assert(CountAlpha(fitted, new Rectangle(Point.Empty, fitted.Size), 17, 255) ==
                    CountAlpha(roomy, new Rectangle(Point.Empty, roomy.Size), 17, 255),
                    "hinted compact text clipped a percentage or cache-hit value at " + scale + " DPI scale");
            }
        }
        Console.WriteLine("PASS hinted narrow text preserves complete context/cache percentages at 100-200 percent DPI");
    }

    private static object Settings(string theme)
    { object settings = New("CodexUsageOverlay.OverlaySettings"); Set(settings, "Theme", theme); return settings; }
    private static object New(string type) { return Activator.CreateInstance(app.GetType(type, true), true); }
    private static object Call(object target, string name, params object[] args)
    { return target.GetType().GetMethod(name, All).Invoke(target, args); }
    private static object CallStatic(Type type, string name, params object[] args)
    { return type.GetMethod(name, All).Invoke(null, args); }
    private static object Get(object target, string name) { return target.GetType().GetField(name, All).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, All).SetValue(target, value); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
