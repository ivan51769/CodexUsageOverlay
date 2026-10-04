using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

internal static class ReleaseDownloadUiTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Assembly app = Assembly.LoadFrom(args[0]);
            Type overlayType = app.GetType("CodexUsageOverlay.OverlayForm", true);
            if (overlayType.GetProperty("ReleaseUpdateLabel", All) == null)
                throw new Exception("Top update indicator does not expose live download progress");
            Type progressType = app.GetType("CodexUsageOverlay.ReleaseInstallerDownload+Progress", true);
            Type settingsType = app.GetType("CodexUsageOverlay.OverlaySettings", true);
            object settings = Activator.CreateInstance(settingsType, true);
            Set(settings, "OnboardingCompleted", true);
            object service = Activator.CreateInstance(app.GetType("CodexUsageOverlay.UsageService", true), true);
            using (Form overlay = (Form)Activator.CreateInstance(overlayType, All, null,
                new[] { service, settings }, null))
            {
                try
                {
                    Set(overlay, "displayCapsuleTexts", new[] { "PRO", "5H: 无限", "周: 76%", "12.3M" });
                    Set(overlay, "updateAvailable", true);
                    if ((string)Get(overlay, "ReleaseUpdateLabel") != "有更新")
                        throw new Exception("Idle release action changed");
                    Timer animationTimer = (Timer)GetField(overlay, "releaseProgressAnimationTimer");
                    if (animationTimer.Enabled || !((Rectangle)Get(overlay, "ReleaseProgressBarBounds")).IsEmpty)
                        throw new Exception("Idle toolbar retains a progress bar or animation");
                    overlay.Size = new Size(720, 28);
                    Point toolbarLocation = overlay.Location;
                    Set(overlay, "releaseDownloadRunning", true);
                    foreach (string phase in new[] { "connecting", "downloading", "verifying", "ready" })
                    {
                        object progress = NewProgress(progressType, phase, 73);
                        Call(overlay, "ApplyReleaseDownloadProgress", progress);
                        string expected = phase == "connecting" ? "连接中" : phase == "verifying" ? "校验中" :
                            phase == "ready" ? "启动中" : "73%";
                        if ((string)Get(overlay, "ReleaseUpdateLabel") != expected)
                            throw new Exception("Wrong release phase label: " + phase);
                        bool shouldAnimate = phase != "ready";
                        if (animationTimer.Enabled != shouldAnimate)
                            throw new Exception("Wrong release phase animation state: " + phase);
                        if (overlay.Height != 32 || overlay.Location != toolbarLocation ||
                            (int)Get(overlay, "ActiveHeaderHeight") != 28)
                            throw new Exception("Taller progress bar moved toolbar content instead of extending the bottom edge");
                    }
                    // Update discovery may change while an already-authorized transfer runs.
                    Set(overlay, "updateAvailable", false);
                    if (!(bool)Get(overlay, "ShowUpdateIndicator"))
                        throw new Exception("In-flight update progress disappears with discovery state");
                    foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
                    foreach (string theme in new[] { "NativeCodex", "RainbowText", "NeonBlue", "PinkGradient" })
                    {
                        Set(settings, "Theme", theme);
                        Set(overlay, "dpiScale", scale);
                        overlay.Size = new Size((int)(720 * scale), (int)(32 * scale));
                        Size toolbarSize = overlay.Size;
                        Set(overlay, "releaseProgressAnimationFrame", 0);
                        Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "downloading", 25));
                        using (Bitmap first = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                        {
                            Rectangle firstBounds = (Rectangle)Get(overlay, "UpdateIndicatorBounds");
                            Rectangle bar = AssertProgressBarBounds(overlay);
                            AssertFillWidth(first, bar, scale, 25);
                            Set(overlay, "releaseProgressAnimationFrame", 24);
                            using (Bitmap animated = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                                AssertAnimationClipped(first, animated, bar, scale, 25);
                            Set(overlay, "releaseProgressAnimationFrame", 0);
                            Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "downloading", 75));
                            using (Bitmap second = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                            {
                                if (overlay.Size != toolbarSize)
                                    throw new Exception("Bottom progress bar changes toolbar size");
                                if (firstBounds != (Rectangle)Get(overlay, "UpdateIndicatorBounds"))
                                    throw new Exception("Download progress changes toolbar width");
                                if (SamePixels(first, second)) throw new Exception("Download progress is not painted");
                                AssertFillWidth(second, bar, scale, 75);
                                if (scale == 1f && theme == "NativeCodex" && args.Length > 1)
                                    using (Bitmap preview = new Bitmap(second.Width, second.Height))
                                    using (Graphics graphics = Graphics.FromImage(preview))
                                    {
                                        graphics.Clear(Color.FromArgb(244, 248, 250));
                                        graphics.DrawImage(second, 0, 0);
                                        preview.Save(args[1]);
                                    }
                            }
                        }
                        string hint = (string)Get(overlay, "ReleaseUpdateHint");
                        if (!hint.Contains("75%") || !hint.Contains("4 线程") || !hint.Contains("MB") || !hint.Contains("/s"))
                            throw new Exception("Download tooltip lacks bytes, speed or parallelism");
                    }
                    Set(settings, "Theme", "NativeCodex");
                    foreach (float scale in new[] { 1f, 2f })
                    {
                        Set(overlay, "dpiScale", scale);
                        overlay.Size = new Size((int)(320 * scale), (int)(32 * scale));
                        Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "downloading", 75));
                        using (Bitmap narrow = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                        {
                            if (!(bool)Get(overlay, "ShowUpdateIndicator"))
                                throw new Exception("Narrow toolbar hides the live download percentage");
                            Rectangle update = (Rectangle)Get(overlay, "UpdateIndicatorBounds");
                            if (update.IsEmpty || !new Rectangle(0, 0, (int)Get(overlay, "CanvasWidth"),
                                (int)Get(overlay, "CanvasHeight")).Contains(update))
                                throw new Exception("Narrow toolbar percentage escapes the canvas");
                            AssertFillWidth(narrow, AssertProgressBarBounds(overlay), scale, 75);
                        }
                    }
                    Set(overlay, "dpiScale", 1f);
                    overlay.Size = new Size(720, 548);
                    Set(overlay, "settingsExpanded", true);
                    Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "downloading", 75));
                    if (animationTimer.Interval != 120)
                        throw new Exception("Expanded settings animation is not throttled");
                    int expandedFrame = (int)GetField(overlay, "releaseProgressAnimationFrame");
                    Tick(animationTimer);
                    if ((int)GetField(overlay, "releaseProgressAnimationFrame") != (expandedFrame + 3) % 80)
                        throw new Exception("Expanded animation throttle changes the shimmer speed");
                    Rectangle expandedBar = AssertProgressBarBounds(overlay);
                    if (expandedBar.Top >= 28)
                        throw new Exception("Progress bar moved to the settings panel bottom");
                    Set(overlay, "settingsExpanded", false);
                    overlay.Size = new Size(720, 32);
                    Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "downloading", 75));
                    if (animationTimer.Interval != 40)
                        throw new Exception("Collapsed toolbar did not restore smooth animation");
                    object unknownProgress = NewProgress(progressType, "downloading", 73);
                    Set(unknownProgress, "TotalBytes", 0L);
                    Call(overlay, "ApplyReleaseDownloadProgress", unknownProgress);
                    Set(overlay, "releaseProgressAnimationFrame", 0);
                    if ((string)Get(overlay, "ReleaseUpdateLabel") != "下载中")
                        throw new Exception("Unknown download size fabricates a percentage");
                    using (Bitmap unknown = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                        AssertFillWidth(unknown, AssertProgressBarBounds(overlay), 1f, 0);
                    Set(overlay, "releaseProgressAnimationFrame", 24);
                    using (Bitmap unknownActive = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                    {
                        Rectangle bar = AssertProgressBarBounds(overlay);
                        int lastFilled;
                        int activeWidth = CountPurpleColumns(unknownActive, bar, out lastFilled);
                        if (activeWidth <= 0 || activeWidth > (int)Math.Ceiling(bar.Width * 0.2) + 2)
                            throw new Exception("Unknown download size paints reported progress instead of an activity segment");
                    }
                    Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "verifying", 99));
                    if (((string)Get(overlay, "ReleaseUpdateHint")).Contains("下载完成"))
                        throw new Exception("Verification is represented as completed installation");
                    Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "ready", 100));
                    using (Bitmap ready = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                        AssertFillWidth(ready, AssertProgressBarBounds(overlay), 1f, 100);
                    Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "installing", 100));
                    if (animationTimer.Enabled || !((Rectangle)Get(overlay, "ReleaseProgressBarBounds")).IsEmpty)
                        throw new Exception("Installer handoff retains progress animation");
                    if (overlay.Height != 28)
                        throw new Exception("Installer handoff retains the extra progress height");
                    Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "downloading", 50));
                    int beforeTick = (int)GetField(overlay, "releaseProgressAnimationFrame");
                    Tick(animationTimer);
                    if ((int)GetField(overlay, "releaseProgressAnimationFrame") != (beforeTick + 1) % 80)
                        throw new Exception("Collapsed animation timer does not advance by one frame");
                    Call(overlay, "StopReleaseProgressAnimation");
                    if (animationTimer.Enabled)
                        throw new Exception("Download stop retains animation timer");
                    Type positionType = app.GetType("CodexUsageOverlay.OverlayDisplayPosition", true);
                    foreach (string position in new[] { "ComposerInside", "ComposerBelow" })
                    {
                        Set(settings, "DisplayPosition", Enum.Parse(positionType, position));
                        if (!((Rectangle)Get(overlay, "ReleaseProgressBarBounds")).IsEmpty)
                            throw new Exception("Toolbar progress bar leaks into composer: " + position);
                    }
                    Set(settings, "DisplayPosition", Enum.Parse(positionType, "TitleBar"));
                    Set(overlay, "releaseDownloadRunning", false);
                    Set(overlay, "updateAvailable", true);
                    if ((string)Get(overlay, "ReleaseUpdateLabel") != "有更新")
                        throw new Exception("Retry action retains stale progress");
                    Call(overlay, "UpdateReleaseProgressAnimation");
                    if (overlay.Height != 28)
                        throw new Exception("Stopped update retains the extra progress height");
                    animationTimer.Start();
                    Tick(animationTimer);
                    if (animationTimer.Enabled || !((Rectangle)Get(overlay, "ReleaseProgressBarBounds")).IsEmpty)
                        throw new Exception("Stopped update retains animation or visible progress");
                    Set(overlay, "releaseDownloadRunning", true);
                    Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "downloading", 50));
                    overlay.Dispose();
                    if (animationTimer.Enabled)
                        throw new Exception("Disposed overlay retains the animation timer");
                }
                finally { ((IDisposable)service).Dispose(); }
            }
            Console.WriteLine("PASS purple toolbar-bottom percentage, clipped shimmer, timer lifecycle, phases, byte/speed tooltip and stable toolbar at 100-200 percent DPI");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
    }

    private static object NewProgress(Type type, string phase, int percent)
    {
        object value = Activator.CreateInstance(type, true);
        Set(value, "Phase", phase); Set(value, "Percent", percent);
        Set(value, "DownloadedBytes", 3145728L * percent / 100); Set(value, "TotalBytes", 3145728L);
        Set(value, "BytesPerSecond", 262144d); Set(value, "Connections", 4);
        return value;
    }
    private static void Set(object value, string name, object field)
    { value.GetType().GetField(name, All).SetValue(value, field); }
    private static object Get(object value, string name)
    { return value.GetType().GetProperty(name, All).GetValue(value, null); }
    private static object GetField(object value, string name)
    { return value.GetType().GetField(name, All).GetValue(value); }
    private static object Call(object value, string name, params object[] parameters)
    { return value.GetType().GetMethod(name, All).Invoke(value, parameters); }
    private static void Tick(Timer timer)
    { typeof(Timer).GetMethod("OnTick", All).Invoke(timer, new object[] { EventArgs.Empty }); }

    private static Rectangle AssertProgressBarBounds(object overlay)
    {
        Rectangle actual = (Rectangle)Get(overlay, "ReleaseProgressBarBounds");
        Rectangle expected = new Rectangle(2, (int)Get(overlay, "HeaderTop") +
            (int)Get(overlay, "ActiveHeaderHeight") - 5, (int)Get(overlay, "CanvasWidth") - 4, 8);
        if (actual != expected)
            throw new Exception("Progress bar is not full-width at the toolbar bottom: " + actual);
        if (!new Rectangle(0, 0, (int)Get(overlay, "CanvasWidth"),
            (int)Get(overlay, "CanvasHeight")).Contains(actual))
            throw new Exception("Taller progress bar is clipped by the toolbar canvas");
        return actual;
    }

    private static Rectangle PhysicalBounds(Rectangle logical, float scale)
    {
        int left = (int)Math.Round(logical.Left * scale);
        int top = (int)Math.Round(logical.Top * scale);
        return Rectangle.FromLTRB(left, top, (int)Math.Round(logical.Right * scale),
            (int)Math.Round(logical.Bottom * scale));
    }

    private static bool IsPurpleFill(Color value)
    {
        return value.A >= 150 && value.R >= 100 && value.B >= 100 &&
            value.R - value.G >= 35 && value.B - value.G >= 35;
    }

    private static void AssertFillWidth(Bitmap bitmap, Rectangle logical, float scale, int percent)
    {
        Rectangle bar = PhysicalBounds(logical, scale);
        int lastFilled;
        int filledColumns = CountPurpleColumns(bitmap, bar, out lastFilled);
        int expectedWidth = (int)Math.Round(bar.Width * percent / 100d);
        int tolerance = Math.Max(2, (int)Math.Ceiling(2 * scale));
        if (Math.Abs(filledColumns - expectedWidth) > tolerance ||
            (percent > 0 && Math.Abs(lastFilled + 1 - bar.Left - expectedWidth) > tolerance))
            throw new Exception("Purple fill does not represent the real percentage: " + percent +
                "% at scale " + scale + ", painted " + filledColumns + " / " + bar.Width);
    }

    private static int CountPurpleColumns(Bitmap bitmap, Rectangle bar, out int lastFilled)
    {
        int filledColumns = 0;
        lastFilled = bar.Left - 1;
        for (int x = bar.Left; x < bar.Right; x++)
        {
            bool filled = false;
            for (int y = bar.Top + 1; y < bar.Bottom - 1; y++)
                filled |= IsPurpleFill(bitmap.GetPixel(x, y));
            if (filled) { filledColumns++; lastFilled = x; }
        }
        return filledColumns;
    }

    private static void AssertAnimationClipped(Bitmap first, Bitmap second, Rectangle logical,
        float scale, int percent)
    {
        Rectangle fill = PhysicalBounds(logical, scale);
        fill.Width = (int)Math.Round(fill.Width * percent / 100d);
        fill.Inflate((int)Math.Ceiling(scale), (int)Math.Ceiling(scale));
        int changes = 0;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
            {
                if (first.GetPixel(x, y) == second.GetPixel(x, y)) continue;
                if (!fill.Contains(x, y))
                    throw new Exception("Animated shimmer escaped the downloaded fill");
                changes++;
            }
        if (changes < Math.Max(4, (int)(4 * scale)))
            throw new Exception("Same percentage has no visible shimmer animation");
    }
    private static bool SamePixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
        return true;
    }
}
