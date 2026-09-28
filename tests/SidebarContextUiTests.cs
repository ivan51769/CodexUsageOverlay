// Desktop regression: real UI Automation row geometry, with the data worker held busy.
// Run after build.ps1 via test-sidebar-ui.ps1. No user application is controlled.
using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class SidebarContextUiTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly app;
    private static Exception failure;

    [STAThread]
    private static int Main(string[] args)
    {
        app = Assembly.LoadFrom(args[0]);
        try
        {
            VerifyContextStripStates();
            VerifyTokenPopupAndRender(args[1]);
            VerifyContextToggleLayout();
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        var monitorType = app.GetType("CodexUsageOverlay.CodexThreadContextMonitor", true);
        object monitor = Activator.CreateInstance(monitorType, true);
        using (var host = new Form { Text = "Sidebar follow regression", Width = 920,
            Height = 680, StartPosition = FormStartPosition.CenterScreen })
        using (var list = new ListBox { Left = 8, Top = 140, Width = 380, Height = 403,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 31, IntegralHeight = false })
        using (var pulse = new System.Windows.Forms.Timer { Interval = 50 })
        {
            for (int i = 0; i < 40; i++) list.Items.Add("Thread " + i);
            host.Controls.Add(list);
            list.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                e.DrawBackground();
                e.Graphics.DrawString(list.Items[e.Index].ToString(), list.Font, Brushes.Black, e.Bounds);
            };
            host.Shown += delegate
            {
                IntPtr handle = host.Handle, listHandle = list.Handle;
                Rectangle hostBounds = host.Bounds;
                Set(monitor, "running", true); // Simulate slow app-server / log parsing.
                Set(monitor, "sidebarWindow", handle);
                pulse.Tick += delegate { Call(monitor, "Request", handle, host.Bounds, 1f); };
                pulse.Start();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        var signalType = app.GetType("CodexUsageOverlay.CodexContextSignal", true);
                        var signals = (IDictionary)Activator.CreateInstance(
                            typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(string), signalType));
                        for (int i = 0; i < 40; i++)
                        {
                            object signal = Activator.CreateInstance(signalType, true);
                            Set(signal, "UsedTokens", (long)(1000 + i));
                            Set(signal, "WindowTokens", 10000L);
                            signals.Add("Thread " + i, signal);
                        }
                        Set(monitor, "sidebarSignals", signals);
                        Call(monitor, "TrackSidebarRoot", AutomationElement.FromHandle(listHandle), handle);
                        WaitFor(monitor, handle, hostBounds, 1000);
                        long worst = 0;
                        foreach (int top in new[] { 6, 12, 2, 9, 0 })
                        {
                            host.Invoke((Action)delegate { list.TopIndex = top; });
                            var elapsed = Stopwatch.StartNew();
                            var rows = WaitFor(monitor, handle, hostBounds, 1000 + top);
                            worst = Math.Max(worst, elapsed.ElapsedMilliseconds);
                            if (elapsed.ElapsedMilliseconds > 750)
                                throw new Exception("Sidebar followed too late: " + elapsed.ElapsedMilliseconds + "ms");
                            Rectangle first = (Rectangle)Get(rows[0], "Bounds");
                            if (first.Top < hostBounds.Top + 140 || first.Top > hostBounds.Top + 200)
                                throw new Exception("Scrolled row kept its previous coordinate");
                        }
                        Console.WriteLine("PASS real sidebar scroll follows while data worker is blocked; max " + worst + "ms");
                        VerifyFormatAndRender(args[1]);
                    }
                    catch (Exception error) { failure = error; }
                    finally { host.BeginInvoke((Action)delegate { host.Close(); }); }
                });
            };
            Application.Run(host);
            ((IDisposable)monitor).Dispose();
        }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }

    private static IList WaitFor(object monitor, IntPtr handle, Rectangle bounds, long firstTokens)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 3000)
        {
            var rows = (IList)Get(Call(monitor, "Snapshot", handle, bounds), "Rows");
            if (rows.Count > 0 && (long)Get(Get(rows[0], "Signal"), "UsedTokens") == firstTokens) return rows;
            Thread.Sleep(10);
        }
        var last = (IList)Get(Call(monitor, "Snapshot", handle, bounds), "Rows");
        foreach (object row in last)
            Console.WriteLine("observed " + Get(Get(row, "Signal"), "UsedTokens") + " " + Get(row, "Bounds"));
        throw new Exception("Sidebar positions did not update for row " + firstTokens);
    }

    private static void VerifyFormatAndRender(string output)
    {
        Type form = app.GetType("CodexUsageOverlay.CodexSidebarContextForm", true);
        object sample = Activator.CreateInstance(app.GetType("CodexUsageOverlay.CodexContextSignal"), true);
        Set(sample, "UsedTokens", 100000L);
        Set(sample, "WindowTokens", 253000L);
        Set(sample, "InputTokens", 99500L);
        Set(sample, "CachedInputTokens", 94000L);
        Set(sample, "OutputTokens", 517L);
        string[] lines = (string[])form.GetMethod("BuildSidebarDetailLines", All).Invoke(null, new[] { sample });
        if (String.Join("|", lines) != "已100K|余153K|入100K|缓94K")
            throw new Exception("Sidebar boxes must group used/remaining then input/cache only");
        Set(sample, "UsedTokens", 254000L);
        lines = (string[])form.GetMethod("BuildSidebarDetailLines", All).Invoke(null, new[] { sample });
        if (lines[1] != "余0K") throw new Exception("Remaining context must not be negative");
        VerifyExpansionAnchor(form);
        foreach (var pair in new[] { new object[] { 347L, "0K" }, new object[] { 500L, "1K" }, new object[] { 1000L, "1K" },
            new object[] { 999499L, "999K" }, new object[] { 999500L, "1M" }, new object[] { 1000000L, "1M" },
            new object[] { 1500000L, "2M" }, new object[] { 1989000L, "2M" } })
            if ((string)form.GetMethod("BriefTokens", All).Invoke(null, new[] { pair[0] }) != (string)pair[1])
                throw new Exception("Token unit threshold failed");
        using (var bitmap = new Bitmap(1200, 330))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            bitmap.SetResolution(96, 96);
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                int top = (int)((scale - 1) * 240) + 10;
                object row = Activator.CreateInstance(app.GetType("CodexUsageOverlay.CodexSidebarContextRow"), true);
                object signal = Activator.CreateInstance(app.GetType("CodexUsageOverlay.CodexContextSignal"), true);
                Set(signal, "UsedTokens", 101452L); Set(signal, "WindowTokens", 2000000L);
                Set(signal, "InputTokens", 100823L); Set(signal, "CachedInputTokens", 98117L);
                Set(signal, "OutputTokens", 629L);
                Set(row, "Signal", signal);
                Set(row, "Bounds", new Rectangle(0, top, (int)(393 * scale), (int)(31 * scale)));
                form.GetMethod("DrawBadge", All).Invoke(null, new object[] {
                    graphics, row, new Rectangle(0, 0, 1200, 330), scale, 2 });
                using (var statusPen = new Pen(Color.Gray, scale))
                    graphics.DrawArc(statusPen, (393 - 22) * scale,
                        top + (31 * scale - 8 * scale) / 2, 8 * scale, 8 * scale, 25, 295);
            }
            bitmap.Save(output);
        }
        Console.WriteLine("PASS K/M thresholds; sidebar previews rendered at 100/125/150/200 percent");
    }

    private static void VerifyTokenPopupAndRender(string output)
    {
        object signal = Activator.CreateInstance(app.GetType("CodexUsageOverlay.CodexContextSignal"), true);
        Set(signal, "UsedTokens", 114000L); Set(signal, "WindowTokens", 258400L);
        Set(signal, "ObservedAt", DateTimeOffset.UtcNow);
        Set(signal, "HasSessionUsage", true);
        Set(signal, "SessionTokens", 145414156L); Set(signal, "SessionInputTokens", 144588335L);
        Set(signal, "SessionCachedTokens", 142586240L); Set(signal, "SessionOutputTokens", 825821L);
        Type popupType = app.GetType("CodexUsageOverlay.CodexTokenUsagePopup", true);
        string[] values = (string[])popupType.GetMethod("DetailValues", All).Invoke(null, new[] { signal });
        if (String.Join("|", values) != "145,414,156 tok|99%|2,002,095 tok|142,586,240 tok|825,821 tok")
            throw new Exception("Popup must use session totals and subtract cached input only once");
        Type stripType = app.GetType("CodexUsageOverlay.CodexContextNudgeForm", true);
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
        {
            bool dismissed = false;
            using (Form strip = (Form)Activator.CreateInstance(stripType, All, null,
                new object[] { (Action)delegate { }, (Action)delegate { dismissed = true; } }, null))
            using (Form popup = (Form)Activator.CreateInstance(popupType, true))
            {
                Set(strip, "signal", signal); Set(strip, "compact", true); Set(strip, "scale", scale);
                object visual = Activator.CreateInstance(app.GetType("CodexUsageOverlay.OverlaySettings"), true);
                Set(visual, "Theme", "NativeCodex"); Set(strip, "settings", visual);
                Set(popup, "signal", signal); Set(popup, "scale", scale);
                int width = (int)stripType.GetMethod("MeasureCompactWidth", All).Invoke(null, new[] { signal, (object)scale });
                if (width >= 350 * scale) throw new Exception("Compact strip still has excessive side padding");
                strip.Size = new Size(width, (int)Math.Round(42 * scale));
                Call(strip, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, width - 4, 5, 0));
                if (dismissed) throw new Exception("Compact strip retained its invisible close hit target");
                if (strip.Region.IsVisible(width / 2, (int)Math.Round(21 * scale)))
                    throw new Exception("Two rows must leave their separation transparent");
                popup.Size = new Size((int)(310 * scale), (int)(172 * scale));
                using (var bitmap = new Bitmap(strip.Width, strip.Height))
                {
                    strip.DrawToBitmap(bitmap, strip.ClientRectangle);
                    Rectangle token = (Rectangle)Get(strip, "tokenCapsuleBounds");
                    if (!strip.ClientRectangle.Contains(token) || token.Width < 140 * scale)
                        throw new Exception("Token hover target clipped at DPI " + scale);
                    using (Graphics measure = Graphics.FromImage(bitmap))
                    using (Font font = new Font("Microsoft YaHei UI", 8.2f * scale))
                    {
                        int textWidth = TextRenderer.MeasureText(measure, "145M tok · 缓存命中 99%", font,
                            Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                        if (token.Width - (int)(28 * scale) < textWidth)
                            throw new Exception("Token summary truncates its cache hit percentage");
                    }
                    bitmap.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output), "context-strip-" + (int)(scale * 100) + ".png"));
                }
                using (var bitmap = new Bitmap(popup.Width, popup.Height))
                {
                    popup.DrawToBitmap(bitmap, popup.ClientRectangle);
                    bitmap.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output), "context-popup-" + (int)(scale * 100) + ".png"));
                }
            }
        }
        Console.WriteLine("PASS cumulative token popup arithmetic; two-line previews at 100-200 percent DPI");
    }

    private static void VerifyExpansionAnchor(Type form)
    {
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            object row = Activator.CreateInstance(app.GetType("CodexUsageOverlay.CodexSidebarContextRow"), true);
            object signal = Activator.CreateInstance(app.GetType("CodexUsageOverlay.CodexContextSignal"), true);
            Set(signal, "UsedTokens", 64000L); Set(signal, "WindowTokens", 100000L);
            Set(row, "Signal", signal);
            Rectangle rowBounds = new Rectangle(0, 10, (int)(393 * scale), (int)(31 * scale));
            Set(row, "Bounds", rowBounds);
            using (var compact = new Bitmap(1200, 100))
            using (var expanded = new Bitmap(1200, 100))
            using (var firstOnly = new Bitmap(1200, 100))
            {
                foreach (bool details in new[] { false, true })
                    using (var graphics = Graphics.FromImage(details ? expanded : compact))
                    {
                        graphics.Clear(Color.Transparent);
                        form.GetMethod("DrawBadge", All).Invoke(null, new object[] {
                            graphics, row, new Rectangle(0, 0, 1200, 100), scale, details ? 2 : 0 });
                    }
                using (var graphics = Graphics.FromImage(firstOnly))
                {
                    graphics.Clear(Color.Transparent);
                    form.GetMethod("DrawBadge", All).Invoke(null, new object[] {
                        graphics, row, new Rectangle(0, 0, 1200, 100), scale, 1 });
                }
                int anchorRight = (int)form.GetMethod("GetBadgeAnchorRight", All).Invoke(null, new object[] { rowBounds, scale });
                int wideAnchor = (int)form.GetMethod("GetBadgeAnchorRight", All).Invoke(null,
                    new object[] { new Rectangle(rowBounds.Left, rowBounds.Top, (int)(600 * scale), rowBounds.Height), scale });
                if (wideAnchor != rowBounds.Left + (int)Math.Round(400 * scale))
                    throw new Exception("Wide sidebar did not keep metrics gathered on the left");
                int secondBoxLeft = anchorRight - (int)Math.Round(77 * scale);
                for (int x = 0; x < 1200; x++)
                    for (int y = 0; y < 100; y++)
                    {
                        if (x < secondBoxLeft - 2 && firstOnly.GetPixel(x, y) != expanded.GetPixel(x, y))
                            throw new Exception("First-stage box or percentage differs from second stage");
                        if (x > secondBoxLeft && firstOnly.GetPixel(x, y).A != 0)
                            throw new Exception("First stage leaked the input/cache/output box");
                    }
                int painted = 0;
                // The percentage stays fixed while details fill the gap on its right.
                for (int x = 0; x < anchorRight - (int)Math.Round(130 * scale); x++)
                    for (int y = 0; y < 100; y++)
                    {
                        Color before = compact.GetPixel(x, y), after = expanded.GetPixel(x, y);
                        if (before.A > 0) painted++;
                        if (before.ToArgb() != after.ToArgb())
                            throw new Exception("Expanding moved or restyled the percentage at scale " + scale);
                    }
                if (painted == 0) throw new Exception("Percentage missing in both modes");
                int expectedLeft = anchorRight - (int)Math.Round(47 * scale) -
                    (int)Math.Round(130 * scale);
                int centerY = rowBounds.Top + rowBounds.Height / 2;
                if (compact.GetPixel(expectedLeft, centerY).A == 0 ||
                    compact.GetPixel(expectedLeft - 1, centerY).A != 0)
                    throw new Exception("Percentage did not move to its tighter left anchor at scale " + scale);
                int detailsLeft = anchorRight - (int)Math.Round(126 * scale);
                if (expanded.GetPixel(detailsLeft, centerY).A == 0)
                    throw new Exception("Expanded content still starts beyond the native status instead of beside the percentage");
                for (int x = rowBounds.Right - (int)(30 * scale); x < 1200; x++)
                    for (int y = rowBounds.Top; y < rowBounds.Bottom; y++)
                        if (expanded.GetPixel(x, y).A > 0)
                            throw new Exception("Expanded details cover the native status or main conversation");
                using (var hovered = new Bitmap(1200, 100))
                using (var graphics = Graphics.FromImage(hovered))
                {
                    graphics.Clear(Color.Transparent);
                    form.GetMethod("DrawBadgePart", All).Invoke(null, new object[] {
                        graphics, row, new Rectangle(0, 0, 1200, 100), scale, true, true, true });
                    for (int x = 0; x < rowBounds.Right - (int)(12 * scale); x++)
                        for (int y = rowBounds.Top; y < rowBounds.Bottom; y++)
                            if (hovered.GetPixel(x, y).A > 0)
                                throw new Exception("Hovered details cover native row actions");
                }
                using (var graphics = Graphics.FromImage(expanded))
                using (var font = new Font("Microsoft YaHei UI", 7.2f * scale))
                using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    foreach (string text in new[] { "已999K", "余999M" })
                        if (graphics.MeasureString(text, font, 1000, format).Width > 44 * scale)
                            throw new Exception("Leading expanded value would truncate at scale " + scale);
                }
            }
        }
        Console.WriteLine("PASS percentage pixels stay identical across expansion; native status remains clear at 100-200 percent");
    }

    private static void VerifyContextStripStates()
    {
        Type form = app.GetType("CodexUsageOverlay.CodexContextNudgeForm", true);
        object signal = Activator.CreateInstance(app.GetType("CodexUsageOverlay.CodexContextSignal"), true);
        MethodInfo text = form.GetMethod("CompactText", All);
        string waiting = (string)text.Invoke(null, new[] { signal });
        if (!waiting.Contains("等待当前会话数据") || waiting.Contains("0%"))
            throw new Exception("Missing context must show an honest waiting state, not a fake zero");
        Set(signal, "UsedTokens", 64000L); Set(signal, "WindowTokens", 100000L);
        Set(signal, "ObservedAt", DateTimeOffset.UtcNow.AddMinutes(-20));
        string stale = (string)text.Invoke(null, new[] { signal });
        if (!stale.Contains("上次记录") || !stale.Contains("64%"))
            throw new Exception("Older context should stay readable and be labelled as a previous sample");
        Set(signal, "ObservedAt", DateTimeOffset.UtcNow);
        if (((string)text.Invoke(null, new[] { signal })).Contains("上次记录"))
            throw new Exception("Fresh context retained the old-record label");
        Console.WriteLine("PASS context strip distinguishes fresh, previous and unavailable samples");
    }

    private static void VerifyContextToggleLayout()
    {
        if (app.GetType("CodexUsageOverlay.OverlayForm", true).GetProperty("SidebarExpandBounds", All) == null)
            throw new Exception("Missing enhanced-mode shortcut to the right of context visibility");
        Type settingsType = app.GetType("CodexUsageOverlay.OverlaySettings", true);
        Type positionType = app.GetType("CodexUsageOverlay.OverlayDisplayPosition", true);
        object settings = Activator.CreateInstance(settingsType, true);
        Set(settings, "OnboardingCompleted", true);
        object service = Activator.CreateInstance(app.GetType("CodexUsageOverlay.UsageService", true), true);
        using (var overlay = (Form)Activator.CreateInstance(app.GetType("CodexUsageOverlay.OverlayForm", true),
            All, null, new[] { service, settings }, null))
        {
            try
            {
                Set(overlay, "displayCapsuleTexts", new[] { "PRO", "5H: 无限", "周: 76%", "12.3M" });
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
                    foreach (string layout in new[] { "OneLine", "TwoLines" })
                        foreach (string theme in new[] { "PinkGradient", "RainbowText", "NeonBlue" })
                        {
                            Set(settings, "DisplayPosition", Enum.Parse(positionType, "TitleBar"));
                            Set(settings, "ComposerInsideLayout", Enum.Parse(app.GetType("CodexUsageOverlay.ComposerInsideLayout"), layout));
                            Set(settings, "Theme", theme);
                            Set(overlay, "dpiScale", scale);
                            overlay.Size = new Size((int)(720 * scale), (int)(28 * scale));
                            using (var rendered = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                            {
                                Rectangle download = (Rectangle)overlay.GetType().GetProperty("MsixUpdaterBounds", All).GetValue(overlay, null);
                                Rectangle toggle = (Rectangle)overlay.GetType().GetProperty("ContextToggleBounds", All).GetValue(overlay, null);
                                Rectangle enhanced = (Rectangle)overlay.GetType().GetProperty("SidebarExpandBounds", All).GetValue(overlay, null);
                                if (toggle.Left != download.Right + 2 || toggle.Top != download.Top ||
                                    toggle.Size != download.Size || toggle.Right > 720)
                                    throw new Exception("Context toggle moved, overlapped or clipped at " + scale + "/" + layout + "/" + theme);
                                if (enhanced.Left != toggle.Right + 2 || enhanced.Top != toggle.Top ||
                                    enhanced.Size != toggle.Size || enhanced.Right > 720)
                                    throw new Exception("Enhanced-mode shortcut moved, overlapped or clipped at " + scale + "/" + layout + "/" + theme);
                            }
                        }
                Set(settings, "SidebarContextExpanded", false);
                using (var disabled = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                {
                    string offHint = (string)overlay.GetType().GetProperty("SidebarExpandHint", All).GetValue(overlay, null);
                    if (!offHint.Contains("开启增强模式")) throw new Exception("Disabled mode has the wrong hover hint");
                    Set(settings, "SidebarContextExpanded", true);
                    using (var enabled = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                    {
                        Set(settings, "SidebarContextStage", 1);
                        using (var firstStage = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                        {
                            if (SamePixels(firstStage, enabled)) throw new Exception("The two stages need distinct indicators");
                            string firstHint = (string)overlay.GetType().GetProperty("SidebarExpandHint", All).GetValue(overlay, null);
                            if (!firstHint.Contains("第二框")) throw new Exception("First stage must describe the next stage");
                        }
                        Set(settings, "SidebarContextStage", 2);
                        if (SamePixels(disabled, enabled)) throw new Exception("Enhanced mode has no visible enabled indicator");
                        string onHint = (string)overlay.GetType().GetProperty("SidebarExpandHint", All).GetValue(overlay, null);
                        if (!onHint.Contains("关闭增强模式")) throw new Exception("Enabled mode has the wrong hover hint");
                        Set(overlay, "sidebarExpandHovered", true);
                        using (var hovered = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                        {
                            if (SamePixels(enabled, hovered)) throw new Exception("Enhanced mode has no hover feedback");
                            Set(overlay, "sidebarExpandPressed", true);
                            using (var pressed = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                                if (SamePixels(hovered, pressed)) throw new Exception("Enhanced mode has no pressed feedback");
                        }
                    }
                }
                Set(settings, "DisplayPosition", Enum.Parse(positionType, "ComposerInside"));
                if (!( (Rectangle)overlay.GetType().GetProperty("ContextToggleBounds", All).GetValue(overlay, null)).IsEmpty)
                    throw new Exception("The top-only toggle squeezed the composer toolbar");
                if (!((Rectangle)overlay.GetType().GetProperty("SidebarExpandBounds", All).GetValue(overlay, null)).IsEmpty)
                    throw new Exception("The enhanced-mode shortcut squeezed the composer toolbar");
                Set(settings, "DisplayPosition", Enum.Parse(positionType, "TitleBar"));
                Set(settings, "Theme", "NativeCodex");
                Set(overlay, "settingsExpanded", true);
                Set(overlay, "draftSettings", settings);
                Set(overlay, "dpiScale", 1f);
                overlay.Size = new Size(720, 514);
                using (var rendered = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                    rendered.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(app.Location), "context-settings.png"));
                Rectangle placement = (Rectangle)Call(overlay, "InlineRowBounds", 8);
                Rectangle radar = (Rectangle)overlay.GetType().GetProperty("ResetRadarPanelBounds", All).GetValue(overlay, null);
                Rectangle save = (Rectangle)overlay.GetType().GetProperty("SaveBounds", All).GetValue(overlay, null);
                if (placement.IntersectsWith(radar) || save.Bottom > 514)
                    throw new Exception("New placement setting overlaps the footer");
            }
            finally { ((IDisposable)service).Dispose(); }
        }
        Console.WriteLine("PASS context and enhanced shortcuts align at 100-200 percent DPI; enabled, hover and pressed feedback differ");
    }

    private static bool SamePixels(Bitmap left, Bitmap right)
    {
        if (left.Size != right.Size) return false;
        for (int y = 0; y < left.Height; y++)
            for (int x = 0; x < left.Width; x++)
                if (left.GetPixel(x, y) != right.GetPixel(x, y)) return false;
        return true;
    }

    private static object Get(object value, string name) { return value.GetType().GetField(name, All).GetValue(value); }
    private static void Set(object value, string name, object data)
    {
        var field = value.GetType().GetField(name, All);
        if (field != null) field.SetValue(value, data);
        else value.GetType().GetProperty(name, All).SetValue(value, data, null);
    }
    private static object Call(object value, string name, params object[] args)
    { return value.GetType().GetMethod(name, All).Invoke(value, args); }
}
