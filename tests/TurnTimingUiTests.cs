using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class TurnTimingUiTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Assembly app = Assembly.LoadFrom(args[0]);
            Type signalType = app.GetType("CodexUsageOverlay.CodexContextSignal", true);
            FieldInfo count = signalType.GetField("FirstTokenSampleCount", All);
            if (count == null) throw new Exception("Conversation strip has no measured first-token average");
            object signal = Activator.CreateInstance(signalType, true);
            Set(signal, "UsedTokens", 57000L); Set(signal, "WindowTokens", 100000L);
            Set(signal, "ObservedAt", DateTimeOffset.UtcNow); Set(signal, "HasSessionUsage", true);
            Set(signal, "SessionTokens", 145414156L); Set(signal, "SessionInputTokens", 144588335L);
            Set(signal, "SessionCachedTokens", 142586240L); Set(signal, "SessionOutputTokens", 825821L);
            Set(signal, "FirstTokenSampleCount", 3); Set(signal, "FirstTokenTotalMilliseconds", 6600d);
            Type formType = app.GetType("CodexUsageOverlay.CodexContextNudgeForm", true);
            Type popupType = app.GetType("CodexUsageOverlay.CodexTokenUsagePopup", true);
            string header = (string)CallStatic(formType, "TokenSummary", signal);
            if (!header.Contains("首 Token 均值 2.2s")) throw new Exception("Measured average missing from visible strip");
            string[] values = (string[])CallStatic(popupType, "DetailValues", signal);
            if (values.Length != 6 || !values[5].Contains("2.2s") || !values[5].Contains("3 轮"))
                throw new Exception("Hover details must disclose the measured sample count");
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
            foreach (string theme in new[] { "RainbowText", "NeonBlue" })
            {
                object settings = Activator.CreateInstance(app.GetType("CodexUsageOverlay.OverlaySettings", true), true);
                Set(settings, "Theme", theme);
                int minimum = (int)CallStatic(formType, "MeasureCompactMinimumWidth", signal, scale);
                int preferred = (int)CallStatic(formType, "MeasureCompactWidth", signal, scale);
                if (preferred < minimum) throw new Exception("Preferred width clips the TTFT header");
                using (var form = (Form)Activator.CreateInstance(formType, All, null,
                    new object[] { (Action)delegate { }, (Action)delegate { } }, null))
                foreach (int width in new[] { minimum, preferred })
                foreach (int height in new[] { (int)Math.Round(42 * scale),
                    (int)CallStatic(formType, "MeasureCompactSingleRowHeight", scale) })
                {
                    Call(form, "UpdateBanner", signal, settings, new Rectangle(30, 30, width, height), scale, true);
                    using (Bitmap bitmap = (Bitmap)Call(form, "BuildCompactBitmap"))
                    {
                        Rectangle token = (Rectangle)formType.GetField("tokenCapsuleBounds", All).GetValue(form);
                        if (!form.ClientRectangle.Contains(token)) throw new Exception("TTFT hover surface escaped strip");
                        if (scale == 1f && theme == "RainbowText" && width == preferred && height == 42)
                            bitmap.Save(Path.Combine(Path.GetDirectoryName(args[0]), "context-ttft-preview.png"));
                    }
                    using (Graphics g = form.CreateGraphics())
                    {
                        string context = (string)CallStatic(formType, "CompactText", signal);
                        object layout = CallStatic(formType, "LayoutCompactHeader", g, width, scale, context, header);
                        Type layoutType = layout.GetType();
                        Rectangle area = (Rectangle)layoutType.GetField("Token", All).GetValue(layout);
                        bool icons = (bool)layoutType.GetField("Icons", All).GetValue(layout);
                        float points = (float)layoutType.GetField("FontSize", All).GetValue(layout);
                        string displayed = (string)layoutType.GetField("TokenText", All).GetValue(layout);
                        if (width == preferred && !displayed.Contains("2.2s")) throw new Exception("Roomy strip lost TTFT");
                        using (var font = new Font("Microsoft YaHei UI", points))
                        {
                            int measured = TextRenderer.MeasureText(g, displayed, font, Size.Empty,
                                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
                            if (measured > area.Width - (icons ? (int)Math.Round(22 * scale) : 0))
                                throw new Exception("TTFT/cache text clipped at " + scale + " scale, width " + width);
                        }
                    }
                }
            }
            Console.WriteLine("PASS TTFT value, sample count, single/two-row layout and 100-200 percent DPI");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Set(object value, string name, object data) { value.GetType().GetField(name, All).SetValue(value, data); }
    private static object Call(object value, string name, params object[] args) { return value.GetType().GetMethod(name, All).Invoke(value, args); }
    private static object CallStatic(Type type, string name, params object[] args) { return type.GetMethod(name, All).Invoke(null, args); }
}
