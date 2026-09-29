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
                    Set(overlay, "releaseDownloadRunning", true);
                    foreach (string phase in new[] { "connecting", "downloading", "verifying", "ready" })
                    {
                        object progress = NewProgress(progressType, phase, 73);
                        Call(overlay, "ApplyReleaseDownloadProgress", progress);
                        string expected = phase == "connecting" ? "连接中" : phase == "verifying" ? "校验中" :
                            phase == "ready" ? "启动中" : "73%";
                        if ((string)Get(overlay, "ReleaseUpdateLabel") != expected)
                            throw new Exception("Wrong release phase label: " + phase);
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
                        Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "downloading", 25));
                        using (Bitmap first = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                        {
                            Rectangle firstBounds = (Rectangle)Get(overlay, "UpdateIndicatorBounds");
                            Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "downloading", 75));
                            using (Bitmap second = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                            {
                                if (firstBounds != (Rectangle)Get(overlay, "UpdateIndicatorBounds"))
                                    throw new Exception("Download progress changes toolbar width");
                                if (SamePixels(first, second)) throw new Exception("Download progress is not painted");
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
                    Call(overlay, "ApplyReleaseDownloadProgress", NewProgress(progressType, "verifying", 99));
                    if (((string)Get(overlay, "ReleaseUpdateHint")).Contains("下载完成"))
                        throw new Exception("Verification is represented as completed installation");
                    Set(overlay, "releaseDownloadRunning", false);
                    Set(overlay, "updateAvailable", true);
                    if ((string)Get(overlay, "ReleaseUpdateLabel") != "有更新")
                        throw new Exception("Retry action retains stale progress");
                }
                finally { ((IDisposable)service).Dispose(); }
            }
            Console.WriteLine("PASS live release percentage, phases, byte/speed tooltip and stable toolbar at 100-200 percent DPI");
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
    private static object Call(object value, string name, params object[] parameters)
    { return value.GetType().GetMethod(name, All).Invoke(value, parameters); }
    private static bool SamePixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
        return true;
    }
}
