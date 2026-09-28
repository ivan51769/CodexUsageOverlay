// Desktop regression: real UI Automation row geometry, with the data worker held busy.
// Run after build.ps1 via test-sidebar-ui.ps1. No user application is controlled.
using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class SidebarContextUiTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly app;
    private static Exception failure;
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);

    [STAThread]
    private static int Main(string[] args)
    {
        app = Assembly.LoadFrom(args[0]);
        try
        {
            VerifyContextStripStates();
            VerifyRefreshRequestsReleaseCheck();
            VerifyTitleClearance();
            VerifyAlignedSidebar();
            VerifyNarrowContextHeader();
            VerifyMeasuredFooterMinimum();
            VerifyBrowserSplitFallback();
            VerifyHostStack();
            VerifyTokenPopupAndRender(args[1]);
            VerifyContextToggleLayout();
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        var monitorType = app.GetType("CodexUsageOverlay.CodexThreadContextMonitor", true);
        object monitor = Activator.CreateInstance(monitorType, true);
        using (var host = new Form { Text = "Sidebar follow regression", Width = 1200,
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
                        IList initialRows = WaitFor(monitor, handle, hostBounds, 1000);
                        Type rowType = app.GetType("CodexUsageOverlay.CodexSidebarContextRow", true);
                        var geometry = (IDictionary)Activator.CreateInstance(
                            typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(string), rowType));
                        Rectangle initialBounds = (Rectangle)Get(initialRows[0], "Bounds");
                        for (int i = 0; i < 40; i++)
                        {
                            object row = Activator.CreateInstance(rowType, true);
                            Set(row, "Bounds", initialBounds);
                            Set(row, "TitleBounds", new Rectangle(initialBounds.Left + 10, initialBounds.Top + 3, 80, 20));
                            geometry.Add("Thread " + i, row);
                        }
                        Set(monitor, "sidebarTitleGeometry", geometry);
                        long worst = 0;
                        foreach (int top in new[] { 6, 12, 2, 9, 0 })
                        {
                            host.Invoke((Action)delegate { list.TopIndex = top; });
                            var elapsed = Stopwatch.StartNew();
                            var rows = WaitFor(monitor, handle, hostBounds, 1000 + top, true);
                            worst = Math.Max(worst, elapsed.ElapsedMilliseconds);
                            if (elapsed.ElapsedMilliseconds > 750)
                                throw new Exception("Sidebar followed too late: " + elapsed.ElapsedMilliseconds + "ms");
                            Rectangle first = (Rectangle)Get(rows[0], "Bounds");
                            Rectangle title = (Rectangle)Get(rows[0], "TitleBounds");
                            if (title != new Rectangle(first.Left + 10, first.Top + 3, 80, 20))
                                throw new Exception("Cached title clearance did not follow the scrolled row");
                            if (first.Top < hostBounds.Top + 140 || first.Top > hostBounds.Top + 200)
                                throw new Exception("Scrolled row kept its previous coordinate");
                        }
                        Console.WriteLine("PASS real sidebar scroll follows while data worker is blocked; max " + worst + "ms");
                        foreach (int width in new[] { 500, 330, 510 })
                        {
                            host.Invoke((Action)delegate { list.Width = width; });
                            var elapsed = Stopwatch.StartNew();
                            bool refreshed = false;
                            while (elapsed.ElapsedMilliseconds < 750)
                            {
                                var latest = (IDictionary)Get(monitor, "sidebarTitleGeometry");
                                var rows = (IList)Get(Call(monitor, "Snapshot", handle, hostBounds), "Rows");
                                if (rows.Count > 0 && latest.Contains("Thread 0"))
                                {
                                    Rectangle rowBounds = (Rectangle)Get(rows[0], "Bounds");
                                    Rectangle measured = (Rectangle)Get(latest["Thread 0"], "Bounds");
                                    if (rowBounds.Width > width - 30 && rowBounds.Width <= width && measured.Size == rowBounds.Size)
                                    { refreshed = true; break; }
                                }
                                Thread.Sleep(10);
                            }
                            if (!refreshed) throw new Exception("Resize waited for the blocked data worker to remeasure titles");
                        }
                        Console.WriteLine("PASS sidebar width changes remeasure independently of the data worker");
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

    private static IList WaitFor(object monitor, IntPtr handle, Rectangle bounds, long firstTokens, bool requireTitle = false)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 3000)
        {
            var rows = (IList)Get(Call(monitor, "Snapshot", handle, bounds), "Rows");
            if (rows.Count > 0 && (long)Get(Get(rows[0], "Signal"), "UsedTokens") == firstTokens &&
                (!requireTitle || !((Rectangle)Get(rows[0], "TitleBounds")).IsEmpty)) return rows;
            Thread.Sleep(10);
        }
        var last = (IList)Get(Call(monitor, "Snapshot", handle, bounds), "Rows");
        foreach (object row in last)
            Console.WriteLine("observed " + Get(Get(row, "Signal"), "UsedTokens") + " " + Get(row, "Bounds"));
        throw new Exception("Sidebar positions did not update for row " + firstTokens);
    }

    private static void VerifyHostStack()
    {
        Type native = app.GetType("CodexUsageOverlay.NativeMethods", true);
        using (var host = new Form())
        using (var other = new Form())
        using (var overlay = new Form())
        {
            // Own test handles only; no activation, mouse input or user application control.
            IntPtr hostHandle = host.Handle, otherHandle = other.Handle, overlayHandle = overlay.Handle;
            object[] clientArgs = { hostHandle, Rectangle.Empty };
            if (!(bool)native.GetMethod("TryGetClientScreenBounds", All).Invoke(null, clientArgs) ||
                (Rectangle)clientArgs[1] != host.RectangleToScreen(host.ClientRectangle))
                throw new Exception("Native client boundary differs from the actual form content area");
            native.GetMethod("PlaceAboveHost", All).Invoke(null, new object[] { overlayHandle, hostHandle });
            if (GetWindow(hostHandle, 3) != overlayHandle || !host.IsHandleCreated || overlay.TopMost)
                throw new Exception("Inactive-host overlay is not directly above its host in the non-topmost band");
            host.Dispose();
            if (!overlay.IsHandleCreated) throw new Exception("Closing the host destroyed the independent companion");
        }
        Console.WriteLine("PASS inactive host z-order without activation or permanent topmost ownership");
    }

    private static void VerifyTitleClearance()
    {
        Type form = app.GetType("CodexUsageOverlay.CodexSidebarContextForm", true);
        MethodInfo layout = form.GetMethod("GetBadgeBounds", All);
        if (layout == null) throw new Exception("Missing title-aware sidebar layout");
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
        foreach (int width in new[] { 250, 350, 500, 700 })
        foreach (int titleWidth in new[] { 90, 180, 260, 410 })
        {
            Rectangle row = new Rectangle(20, 20, (int)(width * scale), (int)(32 * scale));
            Rectangle title = new Rectangle(row.Left + 10, row.Top + 2,
                Math.Min((int)(titleWidth * scale), row.Width - 10), row.Height - 4);
            Rectangle previous = Rectangle.Empty;
            for (int part = 0; part < 3; part++)
            {
                Rectangle badge = (Rectangle)layout.Invoke(null, new object[] { row, title, scale, part });
                if (badge.IsEmpty) continue;
                if (badge.Left < title.Right + (int)Math.Round(8 * scale) ||
                    badge.Right > row.Right - (int)Math.Round(32 * scale) || badge.IntersectsWith(previous))
                    throw new Exception("Sidebar metrics cover a title/native status or escape the sidebar");
                previous = badge;
            }
            Rectangle unknown = (Rectangle)layout.Invoke(null, new object[] { row, Rectangle.Empty, scale, 0 });
            if (!unknown.IsEmpty) throw new Exception("Unknown title geometry must not be guessed");
        }
        Console.WriteLine("PASS title-aware sidebar clearance across narrow/wide rows and 100-200 percent DPI");
    }

    private static void VerifyAlignedSidebar()
    {
        Type form = app.GetType("CodexUsageOverlay.CodexSidebarContextForm", true);
        Type rowType = app.GetType("CodexUsageOverlay.CodexSidebarContextRow", true);
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
        {
            Array rows = Array.CreateInstance(rowType, 3);
            for (int i = 0; i < 3; i++)
            {
                object row = Activator.CreateInstance(rowType, true);
                Set(row, "Bounds", Rectangle.FromLTRB((int)((20 + i * 16) * scale), (int)((20 + i * 40) * scale),
                    (int)(500 * scale), (int)((52 + i * 40) * scale)));
                Rectangle bounds = (Rectangle)Get(row, "Bounds");
                Set(row, "TitleBounds", new Rectangle(bounds.Left + 2, bounds.Top + 2,
                    (int)((110 + i * 70) * scale), bounds.Height - 4));
                rows.SetValue(row, i);
            }
            int anchor = (int)form.GetMethod("GetSharedBadgeAnchorRight", All).Invoke(null, new object[] { rows, scale });
            int column = -1;
            for (int i = 0; i < 3; i++)
            {
                Rectangle row = (Rectangle)Get(rows.GetValue(i), "Bounds");
                Rectangle title = (Rectangle)Get(rows.GetValue(i), "TitleBounds");
                Rectangle badge = (Rectangle)form.GetMethod("GetAlignedBadgeBounds", All).Invoke(null, new object[] { row, title, scale, 0, anchor });
                if (badge.IsEmpty || (column >= 0 && badge.Left != column)) throw new Exception("Nested sidebar percentages are not one vertical column");
                column = badge.Left;
                title.Width = row.Width - 40;
                Rectangle collision = (Rectangle)form.GetMethod("GetAlignedBadgeBounds", All).Invoke(null, new object[] { row, title, scale, 0, anchor });
                if (!collision.IsEmpty) throw new Exception("Long title must not push a percentage sideways or be covered");
            }
        }
        Console.WriteLine("PASS sidebar percentages share one column across nested rows and DPI scales");
    }

    private static void VerifyRefreshRequestsReleaseCheck()
    {
        // Offline wiring guard: do not click the user's overlay or perform network requests in this test.
        Type overlay = app.GetType("CodexUsageOverlay.OverlayForm", true);
        Type updates = app.GetType("CodexUsageOverlay.GitHubReleaseUpdateService", true);
        int token = updates.GetMethod("RequestCheck", All, null, new[] { typeof(bool) }, null).MetadataToken;
        byte[] body = overlay.GetMethod("RequestUsageAndRadarRefresh", All).GetMethodBody().GetILAsByteArray();
        for (int i = 1; i + 4 < body.Length; i++)
            if (body[i - 1] == 0x17 && (body[i] == 0x28 || body[i] == 0x6f) && BitConverter.ToInt32(body, i + 1) == token)
            {
                Console.WriteLine("PASS toolbar refresh also requests a forced, non-overlapping release check");
                return;
            }
        throw new Exception("Toolbar refresh is not wired to RequestCheck(true)");
    }

    private static void VerifyNarrowContextHeader()
    {
        Type form = app.GetType("CodexUsageOverlay.CodexContextNudgeForm", true);
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
        foreach (int width in new[] { 234, 262 })
        foreach (string context in new[] { "上下文 57%", "上下文 57%（上次记录）", "上下文 100%（上次记录）" })
        using (var bitmap = new Bitmap((int)(width * scale), (int)(42 * scale)))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            string token = "716M tok · 缓存命中 97%";
            object layout = form.GetMethod("LayoutCompactHeader", All).Invoke(null,
                new object[] { graphics, bitmap.Width, scale, context, token });
            using (Font font = new Font("Microsoft YaHei UI", (float)Get(layout, "FontSize")))
            {
                Rectangle c = (Rectangle)Get(layout, "Context"), t = (Rectangle)Get(layout, "Token");
                bool icons = (bool)Get(layout, "Icons");
                string renderedContext = (string)Get(layout, "ContextText");
                if (!renderedContext.Contains(context.Contains("100%") ? "100%" : "57%") ||
                    (context.Contains("上次") && !renderedContext.Contains("上次")))
                    throw new Exception("Compact header lost percentage or stale-data indication");
                TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                if (TextRenderer.MeasureText(graphics, renderedContext, font, Size.Empty, flags).Width > c.Width - (icons ? (int)(18 * scale) : 0) ||
                    TextRenderer.MeasureText(graphics, token, font, Size.Empty, flags).Width > t.Width - (icons ? (int)Math.Round(22 * scale) : 0) ||
                    c.IntersectsWith(t) || t.Right > bitmap.Width)
                    throw new Exception("Narrow context header clips a percentage or token at scale " + scale + ": " + context);
            }
        }
        Console.WriteLine("PASS complete context percentage and token text in 234-262px safe footer at 100-200 percent DPI");
    }

    private static void VerifyMeasuredFooterMinimum()
    {
        Type form = app.GetType("CodexUsageOverlay.CodexContextNudgeForm", true);
        Type interaction = app.GetType("CodexUsageOverlay.OverlayInteraction", true);
        object signal = Activator.CreateInstance(app.GetType("CodexUsageOverlay.CodexContextSignal"), true);
        Set(signal, "UsedTokens", 150000L); Set(signal, "WindowTokens", 258000L);
        Set(signal, "HasSessionUsage", true); Set(signal, "SessionTokens", 716000000L);
        Set(signal, "SessionInputTokens", 713400000L); Set(signal, "SessionCachedTokens", 692400000L);
        Set(signal, "SessionOutputTokens", 2600000L);
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
        {
            Func<int, int> px = n => (int)Math.Round(n * scale);
            int preferred = (int)form.GetMethod("MeasureCompactWidth", All).Invoke(null, new[] { signal, (object)scale });
            MethodInfo minimumMethod = form.GetMethod("MeasureCompactMinimumWidth", All);
            int minimum = minimumMethod == null ? px(240) : (int)minimumMethod.Invoke(null, new[] { signal, (object)scale });
            using (var bitmap = new Bitmap(minimum, px(42)))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                string context = (string)form.GetMethod("CompactText", All).Invoke(null, new[] { signal });
                string token = (string)form.GetMethod("TokenSummary", All).Invoke(null, new[] { signal });
                object layout = form.GetMethod("LayoutCompactHeader", All).Invoke(null,
                    new object[] { graphics, minimum, scale, context, token });
                using (Font font = new Font("Microsoft YaHei UI", (float)Get(layout, "FontSize")))
                {
                    TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                    if (TextRenderer.MeasureText(graphics, (string)Get(layout, "ContextText"), font, Size.Empty, flags).Width > ((Rectangle)Get(layout, "Context")).Width ||
                        TextRenderer.MeasureText(graphics, token, font, Size.Empty, flags).Width > ((Rectangle)Get(layout, "Token")).Width)
                        throw new Exception("Exact measured minimum clips text at DPI " + scale);
                }
            }
            Rectangle composer = new Rectangle(px(656), px(883), px(712), px(44));
            Rectangle surface = new Rectangle(px(644), px(869), px(736), px(98));
            Rectangle footer = new Rectangle(surface.Left + (surface.Width - px(242)) / 2,
                composer.Bottom, px(242), surface.Bottom - composer.Bottom);
            Rectangle allowed = new Rectangle(px(123), px(29), px(1637), px(956));
            MethodInfo placement = interaction.GetMethod("GetContextStripPlacementBounds", All);
            object[] args = { composer, surface, allowed, preferred, scale, false, footer };
            if (placement.GetParameters().Length == 8) args = new object[] { composer, surface, allowed, preferred, scale, false, footer, minimum };
            if (placement.GetParameters().Length == 9) args = new object[] { composer, surface, allowed, preferred, scale, false, footer, minimum, 0 };
            Rectangle strip = (Rectangle)placement.Invoke(null, args);
            if (strip.IsEmpty || !allowed.Contains(strip) || strip.Left < footer.Left || strip.Right > footer.Right)
                throw new Exception("242px live footer must fit measured text instead of disappearing at fixed 240px threshold, DPI " + scale +
                    "; preferred=" + preferred + "; minimum=" + minimum + "; strip=" + strip + "; footer=" + footer);
            footer = new Rectangle(surface.Left + (surface.Width - px(10)) / 2, composer.Bottom, px(10), surface.Bottom - composer.Bottom);
            Rectangle blocked = (Rectangle)placement.Invoke(null,
                new object[] { composer, surface, allowed, preferred, scale, false, footer, minimum, 0 });
            if (!blocked.IsEmpty) throw new Exception("An actually blocked center must not cover native controls");
        }
        Console.WriteLine("PASS live 242px footer uses actual text minimum and remains within native controls");
    }

    private static void VerifyBrowserSplitFallback()
    {
        Type stripType = app.GetType("CodexUsageOverlay.CodexContextNudgeForm", true);
        Type interaction = app.GetType("CodexUsageOverlay.OverlayInteraction", true);
        object signal = Activator.CreateInstance(app.GetType("CodexUsageOverlay.CodexContextSignal"), true);
        Set(signal, "UsedTokens", 114000L); Set(signal, "WindowTokens", 200000L);
        Set(signal, "ObservedAt", DateTimeOffset.UtcNow); Set(signal, "HasSessionUsage", true);
        Set(signal, "SessionTokens", 145414156L); Set(signal, "SessionInputTokens", 144588335L);
        Set(signal, "SessionCachedTokens", 142586240L); Set(signal, "SessionOutputTokens", 825821L);
        object visual = Activator.CreateInstance(app.GetType("CodexUsageOverlay.OverlaySettings"), true);
        MethodInfo placement = interaction.GetMethod("GetContextStripPlacementBounds", All);
        MethodInfo rowMeasure = stripType.GetMethod("MeasureCompactSingleRowHeight", All);
        foreach (float scale in new[] { 1f, 1.125f, 1.25f, 1.5f, 1.75f, 2f })
        {
            Func<int, int> px = n => (int)Math.Round(n * scale);
            Rectangle composer = Rectangle.FromLTRB(px(479), px(928), px(1067), px(972));
            Rectangle surface = Rectangle.FromLTRB(px(467), px(914), px(1079), px(1012));
            Rectangle footer = Rectangle.FromLTRB(px(715), px(972), px(831), px(1012));
            Rectangle allowed = Rectangle.FromLTRB(px(2), px(2), px(1918), px(1028));
            int preferred = (int)stripType.GetMethod("MeasureCompactWidth", All).Invoke(null, new[] { signal, (object)scale });
            int minimum = (int)stripType.GetMethod("MeasureCompactMinimumWidth", All).Invoke(null, new[] { signal, (object)scale });
            int minHeight = rowMeasure == null ? px(16) : (int)rowMeasure.Invoke(null, new object[] { scale });
            object[] args = { composer, surface, allowed, preferred, scale, false, footer, minimum };
            if (placement.GetParameters().Length == 9)
                args = new object[] { composer, surface, allowed, preferred, scale, false, footer, minimum, minHeight };
            Rectangle strip = (Rectangle)placement.Invoke(null, args);
            if (allowed.Bottom - surface.Bottom < minHeight)
            {
                if (!strip.IsEmpty) throw new Exception("Fractional DPI must not force text into insufficient height");
                // Font hinting at custom DPI can need an extra pixel. Give the render
                // fixture exactly its measured height after checking that unsafe case.
                allowed.Height = surface.Bottom + minHeight - allowed.Top;
                args[2] = allowed;
                strip = (Rectangle)placement.Invoke(null, args);
            }
            if (strip.IsEmpty || strip.Height < minHeight || strip.Height >= px(40) || strip.Top != surface.Bottom ||
                !allowed.Contains(strip) || strip.IntersectsWith(footer) || Math.Abs(strip.Left + strip.Width / 2 - (surface.Left + surface.Width / 2)) > 1)
                throw new Exception("Browser split must show a centered readable single row below the frame at DPI " + scale);
            using (Form form = (Form)Activator.CreateInstance(stripType, All, null,
                new object[] { (Action)delegate { }, (Action)delegate { } }, null))
            {
                Set(form, "signal", signal); Set(form, "settings", visual); Set(form, "compact", true); Set(form, "scale", scale);
                form.Size = strip.Size;
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, form.ClientRectangle);
                    Rectangle token = (Rectangle)Get(form, "tokenCapsuleBounds");
                    if (!form.ClientRectangle.Contains(token) || token.Width < px(100))
                        throw new Exception("Single-row token hover area is clipped");
                    bitmap.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(app.Location), "context-strip-browser-" + (int)(scale * 100) + ".png"));
                }
                if (form.Region.IsVisible(form.Width / 2, -1) || form.Region.IsVisible(form.Width / 2, form.Height))
                    throw new Exception("Single-row native region escapes its bounds");
                IntPtr region = CreateRectRgn(0, 0, 0, 0);
                try
                {
                    GetWindowRgn(form.Handle, region);
                    if (!PtInRegion(region, form.Width / 2, form.Height - 1) || PtInRegion(region, form.Width / 2, -1))
                        throw new Exception("Single-row native window region is invalid");
                    form.Height = 2 * px(20);
                    if ((bool)stripType.GetProperty("SingleCompactRow", All).GetValue(form, null))
                        throw new Exception("Two individually rounded rows were misclassified as single-row mode");
                    GetWindowRgn(form.Handle, region);
                    if (!PtInRegion(region, form.Width / 2, px(30))) throw new Exception("Restored second row is clipped");
                    form.Height = strip.Height;
                    GetWindowRgn(form.Handle, region);
                    if (PtInRegion(region, form.Width / 2, px(30))) throw new Exception("Old second row leaked into single-row mode");
                    form.Height = px(42);
                    GetWindowRgn(form.Handle, region);
                    if (!PtInRegion(region, form.Width / 2, px(30))) throw new Exception("Second restore lost its lower native region");
                    form.Height = strip.Height;
                }
                finally { DeleteObject(region); }
                // Exercise hover only against this test's own non-activating form.
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.Show();
                Rectangle hover = (Rectangle)Get(form, "tokenCapsuleBounds");
                Call(form, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, hover.Left + hover.Width / 2, hover.Top + hover.Height / 2, 0));
                Form popup = (Form)Get(form, "tokenPopup");
                if (popup == null || !popup.Visible) throw new Exception("Single-row hover no longer opens token details");
                string[] detail = (string[])popup.GetType().GetMethod("DetailValues", All).Invoke(null, new[] { signal });
                if (detail.Length != 5 || detail[2] != "2,002,095 tok" || detail[3] != "142,586,240 tok" || detail[4] != "825,821 tok")
                    throw new Exception("Single-row mode lost input/cache/output details");
                Call(form, "HideBanner");
            }
            Rectangle tooShort = Rectangle.FromLTRB(allowed.Left, allowed.Top, allowed.Right, surface.Bottom + minHeight - 1);
            args[2] = tooShort;
            if (!((Rectangle)placement.Invoke(null, args)).IsEmpty)
                throw new Exception("A row that cannot fit readable text must not spill out of Codex");
            args[2] = allowed;
            args[6] = new Rectangle(surface.Left + (surface.Width - px(350)) / 2, composer.Bottom, px(350), surface.Bottom - composer.Bottom);
            Rectangle widthRestored = (Rectangle)placement.Invoke(null, args);
            if (widthRestored.Height >= 2 * px(20) || widthRestored.Top != surface.Bottom)
                throw new Exception("More footer width must not pull two rows above the border when outside height is still short");
            args[2] = Rectangle.FromLTRB(allowed.Left, allowed.Top, allowed.Right, surface.Bottom + px(22));
            Rectangle restored = (Rectangle)placement.Invoke(null, args);
            if (restored.Height < 2 * px(20) || restored.Top + px(20) != surface.Bottom)
                throw new Exception("Restoring width and height must restore two rows on the same bottom border");
        }
        Console.WriteLine("PASS browser split single row stays centered and bounded; full layout restores at 100-200 percent DPI");
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
                Set(row, "TitleBounds", new Rectangle(10, top, (int)(100 * scale), (int)(24 * scale)));
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
        object formatSample = Activator.CreateInstance(signal.GetType(), true);
        Set(formatSample, "HasSessionUsage", true);
        Set(formatSample, "SessionInputTokens", 145800000L);
        Set(formatSample, "SessionCachedTokens", 143000000L);
        Set(formatSample, "SessionOutputTokens", 858000L);
        string summary = (string)stripType.GetMethod("SessionSummary", All).Invoke(null, new[] { formatSample });
        if (summary != "本会话 输入 2.8M · 缓存 143M · 输出 858K")
            throw new Exception("Session summary must match the requested input/cache/output format: " + summary);
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
                if (!strip.Region.IsVisible(width / 2, (int)Math.Round(21 * scale)))
                    throw new Exception("Tighter transparent text must not be clipped by the old row-separation hole");
                IntPtr nativeRegion = CreateRectRgn(0, 0, 0, 0);
                try
                {
                    GetWindowRgn(strip.Handle, nativeRegion);
                    if (!PtInRegion(nativeRegion, width / 2, (int)(30 * scale)))
                        throw new Exception("Native window clips the second strip row");
                }
                finally { DeleteObject(nativeRegion); }
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
                        if (token.Width - (int)Math.Round(22 * scale) < textWidth)
                            throw new Exception("Token summary truncates its cache hit percentage");
                    }
                    bitmap.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output), "context-strip-" + (int)(scale * 100) + ".png"));
                }
                using (var bitmap = new Bitmap(popup.Width, popup.Height))
                {
                    popup.DrawToBitmap(bitmap, popup.ClientRectangle);
                    bitmap.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output), "context-popup-" + (int)(scale * 100) + ".png"));
                }
                strip.Height = (int)Math.Round(20 * scale) * 2;
                using (var bitmap = new Bitmap(strip.Width, strip.Height))
                {
                    strip.DrawToBitmap(bitmap, strip.ClientRectangle);
                    if (!strip.Region.IsVisible(width / 2, strip.Height - (int)(10 * scale)))
                        throw new Exception("Short native footer clips the second row");
                    bitmap.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output), "context-strip-short-" + (int)(scale * 100) + ".png"));
                }
                Set(signal, "ObservedAt", DateTimeOffset.UtcNow.AddHours(-2));
                strip.Width = (int)(234 * scale);
                using (var bitmap = new Bitmap(strip.Width, strip.Height))
                {
                    strip.DrawToBitmap(bitmap, strip.ClientRectangle);
                    bitmap.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output), "context-strip-narrow-" + (int)(scale * 100) + ".png"));
                }
                Set(signal, "ObservedAt", DateTimeOffset.UtcNow);
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
            Set(row, "TitleBounds", new Rectangle(10, 12, (int)(100 * scale), (int)(24 * scale)));
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
                int secondBoxLeft = ((Rectangle)form.GetMethod("GetBadgeBounds", All).Invoke(null,
                    new object[] { rowBounds, Get(row, "TitleBounds"), scale, 2 })).Left;
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
                int expectedLeft = ((Rectangle)form.GetMethod("GetBadgeBounds", All).Invoke(null,
                    new object[] { rowBounds, Get(row, "TitleBounds"), scale, 0 })).Left;
                int centerY = rowBounds.Top + rowBounds.Height / 2;
                if (compact.GetPixel(expectedLeft, centerY).A == 0 ||
                    compact.GetPixel(expectedLeft - 1, centerY).A != 0)
                    throw new Exception("Percentage did not move to its tighter left anchor at scale " + scale);
                int detailsLeft = ((Rectangle)form.GetMethod("GetBadgeBounds", All).Invoke(null,
                    new object[] { rowBounds, Get(row, "TitleBounds"), scale, 1 })).Left;
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
                        graphics, row, new Rectangle(0, 0, 1200, 100), scale, true, true, true, 0 });
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
        if ((bool)Get(settings, "ShowContextMenuButton")) throw new Exception("Context menu shortcut must default hidden");
        if ((bool)Get(settings, "ShowAnalysisButton")) throw new Exception("Analysis shortcut must default hidden");
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
                        foreach (string theme in new[] { "PinkGradient", "RainbowText", "NeonBlue", "NativeCodex" })
                        foreach (bool showAnalysis in new[] { false, true })
                        foreach (bool showContext in new[] { false, true })
                        {
                            Set(settings, "ShowAnalysisButton", showAnalysis);
                            Set(settings, "ShowContextMenuButton", showContext);
                            Set(settings, "DisplayPosition", Enum.Parse(positionType, "TitleBar"));
                            Set(settings, "ComposerInsideLayout", Enum.Parse(app.GetType("CodexUsageOverlay.ComposerInsideLayout"), layout));
                            Set(settings, "Theme", theme);
                            Set(overlay, "dpiScale", scale);
                            overlay.Size = new Size((int)(720 * scale), (int)(28 * scale));
                            using (var rendered = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                            {
                                Rectangle download = (Rectangle)overlay.GetType().GetProperty("MsixUpdaterBounds", All).GetValue(overlay, null);
                                Rectangle analysis = (Rectangle)overlay.GetType().GetProperty("AnalysisBounds", All).GetValue(overlay, null);
                                Rectangle gear = (Rectangle)overlay.GetType().GetProperty("GearBounds", All).GetValue(overlay, null);
                                Rectangle toggle = (Rectangle)overlay.GetType().GetProperty("ContextToggleBounds", All).GetValue(overlay, null);
                                Rectangle enhanced = (Rectangle)overlay.GetType().GetProperty("SidebarExpandBounds", All).GetValue(overlay, null);
                                if (showAnalysis ? analysis.Left != gear.Right + 2 || analysis.Top != download.Top ||
                                        analysis.Size != download.Size || download.Left != analysis.Right + 2 :
                                    !analysis.IsEmpty || download.Left != gear.Right + 2)
                                    throw new Exception("Analysis visibility left a gap or ghost hit target at " + scale + "/" + layout + "/" + theme);
                                if (showContext ? toggle.Left != download.Right + 2 || toggle.Top != download.Top ||
                                        toggle.Size != download.Size || toggle.Right > 720 : !toggle.IsEmpty)
                                    throw new Exception("Context toggle moved, overlapped or clipped at " + scale + "/" + layout + "/" + theme);
                                Rectangle preceding = showContext ? toggle : download;
                                if (enhanced.Left != preceding.Right + 2 || enhanced.Top != download.Top ||
                                    enhanced.Size != download.Size || enhanced.Right > 720)
                                    throw new Exception("Enhanced-mode shortcut moved, overlapped or clipped at " + scale + "/" + layout + "/" + theme);
                            }
                        }
                Set(settings, "ShowContextMenuButton", false);
                Set(settings, "ShowAnalysisButton", false);
                using (var hidden = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                {
                    Rectangle download = (Rectangle)overlay.GetType().GetProperty("MsixUpdaterBounds", All).GetValue(overlay, null);
                    Rectangle toggle = (Rectangle)overlay.GetType().GetProperty("ContextToggleBounds", All).GetValue(overlay, null);
                    Rectangle enhanced = (Rectangle)overlay.GetType().GetProperty("SidebarExpandBounds", All).GetValue(overlay, null);
                    if (!toggle.IsEmpty || enhanced.Left != download.Right + 2 || !(bool)Get(settings, "ContextStripEnabled"))
                        throw new Exception("Hidden shortcut left an empty slot, hid enhanced mode, or disabled the bottom strip");
                }
                Set(settings, "SidebarContextExpanded", false);
                object radarData = Activator.CreateInstance(app.GetType("CodexUsageOverlay.ResetRadarData"), true);
                Set(radarData, "Status", Enum.Parse(app.GetType("CodexUsageOverlay.ResetRadarStatus"), "Offline"));
                Set(overlay, "resetRadar", radarData);
                Set(settings, "Theme", "RainbowText");
                Set(settings, "ComposerInsideLayout", Enum.Parse(app.GetType("CodexUsageOverlay.ComposerInsideLayout"), "OneLine"));
                Set(overlay, "dpiScale", 1f);
                overlay.Size = new Size(900, 28);
                int radarWidth = (int)Call(overlay, "GetResetRadarPillWidth", settings, false);
                if (radarWidth >= 128) throw new Exception("Radar kept its old empty indicator padding");
                using (var rendered = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                    rendered.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(app.Location), "radar-text-only.png"));
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
                foreach (string position in new[] { "ComposerInside", "ComposerBelow" })
                {
                    Set(settings, "DisplayPosition", Enum.Parse(positionType, position));
                    using (var rendered = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                        if (((Rectangle)overlay.GetType().GetProperty("AnalysisBounds", All).GetValue(overlay, null)).IsEmpty)
                            throw new Exception("The top analysis preference changed the existing composer shortcut");
                }
                Set(settings, "DisplayPosition", Enum.Parse(positionType, "TitleBar"));
                Set(settings, "Theme", "NativeCodex");
                Set(overlay, "settingsExpanded", true);
                Set(overlay, "draftSettings", settings);
                Set(overlay, "dpiScale", 1f);
                overlay.Size = new Size(720, 548);
                using (var rendered = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                    rendered.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(app.Location), "context-settings.png"));
                Rectangle placement = (Rectangle)Call(overlay, "InlineRowBounds", 9);
                Rectangle radar = (Rectangle)overlay.GetType().GetProperty("ResetRadarPanelBounds", All).GetValue(overlay, null);
                Rectangle save = (Rectangle)overlay.GetType().GetProperty("SaveBounds", All).GetValue(overlay, null);
                if (placement.IntersectsWith(radar) || save.Bottom > 548)
                    throw new Exception("New placement setting overlaps the footer");
                Rectangle analysisChoice = (Rectangle)Call(overlay, "InlineChoiceBounds", 9, 0, 2);
                Rectangle contextChoice = (Rectangle)Call(overlay, "InlineChoiceBounds", 9, 1, 2);
                Call(overlay, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1,
                    analysisChoice.Left + analysisChoice.Width / 2, analysisChoice.Top + analysisChoice.Height / 2, 0));
                if (!(bool)Get(settings, "ShowAnalysisButton") || (bool)Get(settings, "ShowContextMenuButton"))
                    throw new Exception("Analysis setting did not toggle independently of context");
                Call(overlay, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1,
                    contextChoice.Left + contextChoice.Width / 2, contextChoice.Top + contextChoice.Height / 2, 0));
                if (!(bool)Get(settings, "ShowAnalysisButton") || !(bool)Get(settings, "ShowContextMenuButton"))
                    throw new Exception("Context setting changed the analysis preference");
                Call(overlay, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1,
                    analysisChoice.Left + analysisChoice.Width / 2, analysisChoice.Top + analysisChoice.Height / 2, 0));
                if ((bool)Get(settings, "ShowAnalysisButton") || !(bool)Get(settings, "ShowContextMenuButton"))
                    throw new Exception("Analysis setting could not hide its shortcut independently");
            }
            finally { ((IDisposable)service).Dispose(); }
        }
        using (var nativeSettings = (Form)Activator.CreateInstance(app.GetType("CodexUsageOverlay.SettingsForm", true),
            All, null, new[] { settings }, null))
        {
            var analysis = (CheckBox)Get(nativeSettings, "showAnalysisButton");
            var context = (CheckBox)Get(nativeSettings, "showContextMenuButton");
            if (analysis.Checked || !context.Checked || analysis.Parent != context.Parent)
                throw new Exception("Native top-shortcut settings did not share a row or read independent values");
            analysis.Checked = true;
            context.Checked = false;
            Call(nativeSettings, "SaveAndClose", null, EventArgs.Empty);
            object saved = nativeSettings.GetType().GetProperty("SelectedSettings", All).GetValue(nativeSettings, null);
            if (!(bool)Get(saved, "ShowAnalysisButton") || (bool)Get(saved, "ShowContextMenuButton"))
                throw new Exception("Native settings did not save independent top shortcut values");
        }
        Console.WriteLine("PASS optional analysis/context shortcuts have no hidden slots at 100-200 percent DPI; settings and enhanced-mode feedback remain independent");
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
