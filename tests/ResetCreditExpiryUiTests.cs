using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class ResetCreditExpiryUiTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [STAThread]
    private static int Main(string[] args)
    {
        string cachePath = Path.Combine(Path.GetTempPath(), "codex-expiry-ui-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            Assembly app = Assembly.LoadFrom(args[0]);
            object usage = Activator.CreateInstance(app.GetType("CodexUsageOverlay.UsageData"), true);
            DateTime expiry = DateTime.UtcNow.AddHours(2).AddMinutes(37);
            Set(usage, "Plan", "Pro"); Set(usage, "AvailableResetCredits", 3);
            Set(usage, "ResetCreditsExpireUtc", expiry); Set(usage, "ResetCreditsExpiringCount", 1);
            Set(usage, "WeeklyRemaining", 83); Set(usage, "RateLimitStatus", "正常");
            Set(usage, "WeeklyResetText", "10月5日 12:00"); Set(usage, "ProfileTokensText", "229.3亿");
            Type cache = app.GetType("CodexUsageOverlay.CacheStore");
            cache.GetMethod("Save", All).Invoke(null, new[] { cachePath, usage });
            object restored = cache.GetMethod("Load", All).Invoke(null, new object[] { cachePath });
            if (!expiry.Equals(Get(restored, "ResetCreditsExpireUtc")) || (int)Get(restored, "ResetCreditsExpiringCount") != 1)
                throw new Exception("Expiry cache round-trip failed");
            Set(usage, "ResetCreditsExpireUtc", null); Set(usage, "ResetCreditsExpiringCount", 0);
            cache.GetMethod("Save", All).Invoke(null, new[] { cachePath, usage });
            if (Get(cache.GetMethod("Load", All).Invoke(null, new object[] { cachePath }), "ResetCreditsExpireUtc") != null)
                throw new Exception("Cleared expiry reappeared from cache");
            string[] sections = (string[])app.GetType("CodexUsageOverlay.UsageDisplayText").GetMethod("BuildCapsuleSections", All)
                .Invoke(null, new object[] { restored });
            if (!String.Join("|", sections).Contains("重置券：3 · 最早2时37分到期"))
                throw new Exception("Expiry countdown is not connected to the live toolbar text");
            object settings = Activator.CreateInstance(app.GetType("CodexUsageOverlay.OverlaySettings"), true);
            Set(settings, "OnboardingCompleted", true);
            object service = Activator.CreateInstance(app.GetType("CodexUsageOverlay.UsageService"), true);
            try
            {
                using (Form form = (Form)Activator.CreateInstance(app.GetType("CodexUsageOverlay.OverlayForm"), All, null,
                    new[] { service, settings }, null))
                {
                    ((Timer)Get(form, "timer")).Stop();
                    Set(form, "displayCapsuleTexts", sections);
                    foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
                    {
                        Set(form, "dpiScale", scale);
                        form.Size = new Size((int)(900 * scale), (int)(28 * scale));
                        using (Bitmap bitmap = (Bitmap)form.GetType().GetMethod("BuildRenderedBitmap", All).Invoke(form, null))
                        {
                            if (bitmap.Size != form.Size) throw new Exception("Countdown changed the toolbar geometry");
                            if (scale == 1.5f && args.Length > 1)
                                using (var preview = new Bitmap(bitmap.Width, bitmap.Height))
                                using (var graphics = Graphics.FromImage(preview))
                                { graphics.Clear(Color.FromArgb(246, 250, 245)); graphics.DrawImageUnscaled(bitmap, 0, 0); preview.Save(args[1]); }
                        }
                    }
                }
            }
            finally { ((IDisposable)service).Dispose(); }
            Console.WriteLine("PASS expiry cache round-trip, clearing and toolbar rendering at 100-200 percent DPI");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (File.Exists(cachePath)) File.Delete(cachePath); }
    }
    private static void Set(object obj, string field, object value) { obj.GetType().GetField(field, All).SetValue(obj, value); }
    private static object Get(object obj, string field) { return obj.GetType().GetField(field, All).GetValue(obj); }
}
