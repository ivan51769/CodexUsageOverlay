// Exercises only this test's own UI; no user application is automated.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class SidebarMemoryUiTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance;
    private static Exception failure;

    [STAThread]
    private static int Main(string[] args)
    {
        Assembly app = Assembly.LoadFrom(args[0]);
        bool measureOnly = args.Length > 1 && args[1] == "--measure";
        Type monitor = app.GetType("CodexUsageOverlay.CodexThreadContextMonitor", true);
        using (var host = new Form { Text = "Sidebar resource regression", Width = 1000,
            Height = 700, ShowInTaskbar = false })
        using (var list = new ListBox { Left = 10, Top = 100, Width = 300, Height = 450 })
        {
            for (int i = 0; i < 50; i++) list.Items.Add("Memory fixture " + (i == 1 ? 0 : i));
            host.Controls.Add(list);
            host.Shown += delegate
            {
                IntPtr handle = list.Handle;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        AutomationElement root = AutomationElement.FromHandle(handle);
                        MethodInfo query = monitor.GetMethod("FindVisibleSidebarElements", All);
                        var elements = (AutomationElementCollection)query.Invoke(null, new object[] { root });
                        if (elements.Count == 0) throw new Exception("Missing visible fixture rows");
                        bool cachedOnly = false;
                        try { var ignored = elements[0].Current.Name; }
                        catch (InvalidOperationException) { cachedOnly = true; }
                        Console.WriteLine("CachedOnly=" + cachedOnly);
                        if (!measureOnly && !cachedOnly)
                            throw new Exception("Position snapshots retain native UI references on every polling frame");
                        if (!measureOnly)
                        {
                            int matched = 0;
                            foreach (AutomationElement element in elements)
                            {
                                if (element.Cached.Name != "Memory fixture 0") continue;
                                System.Windows.Rect raw = element.Cached.BoundingRectangle;
                                Rectangle bounds = Rectangle.FromLTRB((int)Math.Floor(raw.Left), (int)Math.Floor(raw.Top),
                                    (int)Math.Ceiling(raw.Right), (int)Math.Ceiling(raw.Bottom));
                                var live = (AutomationElement)monitor.GetMethod("FindLiveRow", All).Invoke(null,
                                    new object[] { root, element.Cached.Name, bounds });
                                if (live == null || live.Current.BoundingRectangle != raw)
                                    throw new Exception("Duplicate row names resolved to a different row or disappeared");
                                matched++;
                            }
                            if (matched != 2) throw new Exception("Duplicate row fixture unavailable");
                            Console.WriteLine("PASS duplicate names retain their own row geometry");
                        }
                        for (int i = 0; i < 100; i++) ReadFrame(query, root);
                        Collect();
                        long before = PrivateBytes();
                        for (int batch = 0; batch < 3; batch++)
                        {
                            for (int i = 0; i < 1000; i++) ReadFrame(query, root);
                            Console.WriteLine("Batch=" + batch + "; PrivateMiB=" + PrivateBytes() / 1048576);
                            Collect();
                            Console.WriteLine("AfterGcMiB=" + PrivateBytes() / 1048576);
                        }
                        long growth = PrivateBytes() - before;
                        if (growth > 48L * 1024 * 1024)
                            throw new Exception("Retained sidebar memory grew by " + growth + " bytes");
                        Console.WriteLine("PASS 3000 sidebar scans; retained growth bytes=" + growth);
                    }
                    catch (Exception error) { failure = error; }
                    finally { host.BeginInvoke((Action)delegate { host.Close(); }); }
                });
            };
            Application.Run(host);
        }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        if (!measureOnly)
        {
            try { VerifyRenderFailure(app); }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        return 0;
    }

    private static void ReadFrame(MethodInfo query, AutomationElement root)
    {
        var elements = (AutomationElementCollection)query.Invoke(null, new object[] { root });
        foreach (AutomationElement element in elements)
            if (element.Cached.Name == null) throw new Exception("Cached name unavailable");
    }

    private static void VerifyRenderFailure(Assembly app)
    {
        Type formType = app.GetType("CodexUsageOverlay.CodexSidebarContextForm", true);
        Type rowType = app.GetType("CodexUsageOverlay.CodexSidebarContextRow", true);
        Type signalType = app.GetType("CodexUsageOverlay.CodexContextSignal", true);
        object signal = Activator.CreateInstance(signalType, true);
        signalType.GetField("UsedTokens", All).SetValue(signal, 1000L);
        signalType.GetField("WindowTokens", All).SetValue(signal, 10000L);
        object row = Activator.CreateInstance(rowType, true);
        rowType.GetField("Bounds", All).SetValue(row, new Rectangle(10, 100, 400, 40));
        rowType.GetField("TitleBounds", All).SetValue(row, new Rectangle(20, 105, 80, 25));
        rowType.GetField("Signal", All).SetValue(row, signal);
        Array rows = Array.CreateInstance(rowType, 1);
        rows.SetValue(row, 0);
        using (var form = (Form)Activator.CreateInstance(formType, true))
        {
            string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sidebar-render-errors.log");
            if (File.Exists(log)) File.Delete(log);
            // GDI+ rejects this impossible allocation before committing memory.
            formType.GetMethod("UpdateBadges", All).Invoke(form,
                new object[] { rows, new Rectangle(0, 0, 1000, Int32.MaxValue), 1f, 0 });
            if (form.Visible) throw new Exception("Failed bitmap must hide the sidebar overlay");
            if (!File.Exists(log) || !File.ReadAllText(log).Contains("CreateLayeredBitmap"))
                throw new Exception("Rendering failure must preserve exception and allocation details");
            formType.GetMethod("UpdateBadges", All).Invoke(form,
                new object[] { rows, new Rectangle(0, 0, 1000, 700), 1f, 0 });
            if (form.Visible) throw new Exception("Failed allocation must back off before another attempt");
            Thread.Sleep(1100);
            formType.GetMethod("UpdateBadges", All).Invoke(form,
                new object[] { rows, new Rectangle(0, 0, 1000, 700), 1f, 0 });
            if (!form.Visible) throw new Exception("Sidebar did not recover after a failed allocation");
            Console.WriteLine("PASS bitmap failure logged and hidden; next valid frame recovers");
        }
    }

    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static long PrivateBytes()
    {
        using (Process process = Process.GetCurrentProcess()) return process.PrivateMemorySize64;
    }
}
