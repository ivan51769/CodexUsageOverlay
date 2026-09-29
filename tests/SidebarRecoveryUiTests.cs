// Read-only UI Automation against this test's own window. No user app is controlled.
using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class SidebarRecoveryUiTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Exception failure;

    [STAThread]
    private static int Main(string[] args)
    {
        Assembly app = Assembly.LoadFrom(args[0]);
        Type monitorType = app.GetType("CodexUsageOverlay.CodexThreadContextMonitor", true);
        if (args.Length > 2 && args[1] == "--scroll-benchmark")
            return ScrollBenchmark(app, monitorType, Int32.Parse(args[2]));
        try { VerifyScrollEventRetention(monitorType); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        object monitor = Activator.CreateInstance(monitorType, true);
        string fixture = Path.Combine(Path.GetTempPath(), "codex-sidebar-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string session = Path.Combine(fixture, "rollout-regression.jsonl");
        File.WriteAllText(session, "{\"timestamp\":\"2026-09-28T00:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":100000}}}}\n");
        Set(monitor, "sessionsRoot", fixture + Path.DirectorySeparatorChar);
        using (var host = new Form { Text = "Sidebar remount regression", Width = 1100, Height = 650 })
        using (var oldList = List())
        using (var newList = List())
        {
            oldList.Items.Add("Thread A");
            newList.Items.Add("Thread A");
            for (int i = 1; i < 1500; i++) newList.Items.Add("Thread " + i);
            host.Controls.Add(oldList);
            host.Shown += delegate
            {
                IntPtr handle = host.Handle, oldHandle = oldList.Handle;
                Rectangle bounds = host.Bounds;
                oldList.Hide();
                host.Controls.Add(newList);
                newList.Show();
                IntPtr newHandle = newList.Handle;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        VerifyLargeSidebarQuery(app, monitorType, host, newList, newHandle, bounds);
                        AutomationElement staleRoot = AutomationElement.FromHandle(oldHandle);
                        Call(monitor, "TrackSidebarRoot", staleRoot, handle);
                        Type descriptorType = app.GetType("CodexUsageOverlay.CodexThreadDescriptor", true);
                        object descriptor = Activator.CreateInstance(descriptorType, true);
                        Set(descriptor, "Id", "fixture");
                        Set(descriptor, "Name", "Thread A");
                        Set(descriptor, "Path", session);
                        Array threads = Array.CreateInstance(descriptorType, 1);
                        threads.SetValue(descriptor, 0);
                        Set(monitor, "threads", threads);
                        Set(monitor, "nextThreadRefreshUtc", DateTime.UtcNow.AddHours(1));
                        Set(monitor, "lastThreadSuccessUtc", DateTime.UtcNow);
                        Set(monitor, "requestedWindow", handle);
                        Set(monitor, "requestedBounds", bounds);
                        Set(monitor, "cachedWindow", handle);
                        Set(monitor, "cachedBounds", bounds);
                        object probed = Call(monitor, "Probe", handle, bounds, threads);
                        if (((IList)Get(probed, "Rows")).Count == 0) throw new Exception("Fixture did not expose a readable replacement row");
                        if (Automation.Compare((AutomationElement)Get(monitor, "sidebarRoot"), staleRoot))
                            throw new Exception("Sidebar remount leaves the position worker attached to a stale, empty root");
                        Console.WriteLine("PASS a remounted sidebar replaces the stale accessibility root");
                        Call(monitor, "Refresh", handle, bounds, 1f);
                        object snapshot = Call(monitor, "Snapshot", handle, bounds);
                        if (((IList)Get(snapshot, "Rows")).Count != 1)
                            throw new Exception("Fresh probe rows are overwritten by an empty position snapshot");
                        Console.WriteLine("PASS fresh probe rows recover an empty position snapshot");
                        object replacementRoot = Get(monitor, "sidebarRoot");
                        object geometry = Get(monitor, "sidebarTitleGeometry");
                        Array emptyRows = Array.CreateInstance(app.GetType("CodexUsageOverlay.CodexSidebarContextRow", true), 0);
                        // Deterministic completion order: an old read finishes after the
                        // replacement root and its rows have already been published.
                        if ((bool)Call(monitor, "PublishSidebarPositions", staleRoot, handle, bounds,
                            geometry, geometry, emptyRows, false))
                            throw new Exception("An obsolete root published its position result");
                        if ((bool)Call(monitor, "InvalidateUnavailableSidebar", staleRoot, handle))
                            throw new Exception("A late failure from an obsolete root cleared the replacement sidebar");
                        if (!Object.ReferenceEquals(replacementRoot, Get(monitor, "sidebarRoot")) ||
                            ((IList)Get(Call(monitor, "Snapshot", handle, bounds), "Rows")).Count != 1)
                            throw new Exception("A stale worker completion changed the replacement root or its rows");
                        if ((bool)Call(monitor, "InvalidateUnavailableSidebar", replacementRoot, IntPtr.Zero))
                            throw new Exception("A worker from a different host invalidated the current sidebar");
                        Console.WriteLine("PASS stale worker success/failure cannot overwrite a replacement sidebar");
                        if (!(bool)Call(monitor, "InvalidateUnavailableSidebar", replacementRoot, handle) ||
                            Get(monitor, "sidebarRoot") != null ||
                            ((IList)Get(Call(monitor, "Snapshot", handle, bounds), "Rows")).Count != 0)
                            throw new Exception("A current-root failure did not atomically clear its obsolete state");
                        Console.WriteLine("PASS current-root failure clears its state and allows rediscovery");
                    }
                    catch (Exception error) { failure = error; }
                    finally { host.BeginInvoke((Action)delegate { host.Close(); }); }
                });
            };
            Application.Run(host);
        }
        ((IDisposable)monitor).Dispose();
        File.Delete(session);
        Directory.Delete(fixture);
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }

    private static ListBox List()
    {
        return new ListBox { Left = 8, Top = 140, Width = 300, Height = 390,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 31, IntegralHeight = false };
    }

    private static void VerifyScrollEventRetention(Type monitorType)
    {
        object busy = Activator.CreateInstance(monitorType, true);
        try
        {
            ((System.Threading.Timer)Get(busy, "positionTimer")).Change(Timeout.Infinite, Timeout.Infinite);
            Set(busy, "positionRunning", 1);
            Call(busy, "QueuePositionRefresh");
            Thread.Sleep(80);
            if ((int)Get(busy, "positionQueued") != 1)
                throw new Exception("A scroll event arriving during a position read was dropped before its latest frame could be read");
            Set(busy, "positionRunning", 0);
            Call(busy, "RefreshPositions", new object[] { null });
            if ((int)Get(busy, "positionQueued") != 0)
                throw new Exception("The next position read did not consume the retained scroll request");
            Console.WriteLine("PASS scroll events survive an in-flight read and are consumed by the next serial refresh");
        }
        finally { Set(busy, "positionRunning", 0); ((IDisposable)busy).Dispose(); }
    }

    private static void VerifyLargeSidebarQuery(Assembly app, Type monitorType, Form host, ListBox list,
        IntPtr listHandle, Rectangle bounds)
    {
        AutomationElement root = AutomationElement.FromHandle(listHandle);
        Type signalType = app.GetType("CodexUsageOverlay.CodexContextSignal", true);
        Type rowType = app.GetType("CodexUsageOverlay.CodexSidebarContextRow", true);
        IDictionary signals = (IDictionary)Activator.CreateInstance(
            typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(string), signalType));
        IDictionary geometry = (IDictionary)Activator.CreateInstance(
            typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(string), rowType));
        MethodInfo query = monitorType.GetMethod("FindVisibleSidebarElements", All);
        if (query == null) throw new Exception("The fast sidebar path still requests all offscreen descendants");
        var watch = Stopwatch.StartNew();
        var visible = (AutomationElementCollection)query.Invoke(null, new object[] { root });
        long queryMilliseconds = watch.ElapsedMilliseconds;
        watch.Restart();
        var all = (AutomationElementCollection)monitorType.GetMethod("FindCachedElements", All).Invoke(null,
            new object[] { root, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)) });
        long fullQueryMilliseconds = watch.ElapsedMilliseconds;
        if (visible.Count == 0 || visible.Count > 24)
            throw new Exception("Position query returned " + visible.Count + " cached objects for only one visible viewport in a 1500-row list");
        System.Windows.Rect firstRaw = visible[0].Cached.BoundingRectangle;
        Rectangle first = Rectangle.FromLTRB((int)firstRaw.Left, (int)firstRaw.Top, (int)firstRaw.Right, (int)firstRaw.Bottom);
        for (int i = 0; i < 1500; i++)
        {
            object signal = Activator.CreateInstance(signalType, true);
            Set(signal, "UsedTokens", (long)(1000 + i)); Set(signal, "WindowTokens", 10000L);
            string title = i == 0 ? "Thread A" : "Thread " + i;
            signals.Add(title, signal);
            object row = Activator.CreateInstance(rowType, true);
            Set(row, "Bounds", first);
            Set(row, "TitleBounds", new Rectangle(first.Left + 8, first.Top + 3, 80, 20));
            geometry.Add(title, row);
        }
        long worst = 0;
        foreach (int top in new[] { 100, 600, 1200, 7, 0 })
        {
            host.Invoke((Action)delegate { list.TopIndex = top; });
            watch.Restart();
            object[] arguments = { root, bounds, signals, false, 1f, geometry };
            IList rows = (IList)monitorType.GetMethod("ReadSidebarPositions", All).Invoke(null, arguments);
            worst = Math.Max(worst, watch.ElapsedMilliseconds);
            if (rows.Count == 0 || (long)Get(Get(rows[0], "Signal"), "UsedTokens") != 1000 + top)
                throw new Exception("Large-list scroll reused a prior title's data or coordinates");
            Rectangle actual = (Rectangle)Get(rows[0], "Bounds");
            Rectangle title = (Rectangle)Get(rows[0], "TitleBounds");
            if (title != new Rectangle(actual.Left + 8, actual.Top + 3, 80, 20))
                throw new Exception("Large-list scroll lost title collision clearance");
        }
        Console.WriteLine("PASS 1500-row sidebar queries only " + visible.Count + " viewport objects (" + queryMilliseconds +
            "ms versus full " + all.Count + " objects/" + fullQueryMilliseconds +
            "ms); rapid jumps retain fresh title/data geometry, max read " + worst + "ms");
        VerifyBoundedCatchup(app, monitorType, root, host, bounds, signals, geometry);
    }

    private static void VerifyBoundedCatchup(Assembly app, Type monitorType, AutomationElement root, Form host,
        Rectangle bounds, IDictionary signals, IDictionary geometry)
    {
        object monitor = Activator.CreateInstance(monitorType, true);
        int frames = 0;
        try
        {
            ((System.Threading.Timer)Get(monitor, "positionTimer")).Change(Timeout.Infinite, Timeout.Infinite);
            Set(monitor, "sidebarRoot", root); Set(monitor, "sidebarWindow", host.Handle);
            Set(monitor, "requestedWindow", host.Handle); Set(monitor, "requestedBounds", bounds);
            Set(monitor, "lastRequestUtc", DateTime.UtcNow); Set(monitor, "sidebarSignals", signals);
            Set(monitor, "sidebarTitleGeometry", geometry); Set(monitor, "running", true);
            Action keepScrolling = delegate
            {
                Interlocked.Increment(ref frames);
                // Force another change notification while this read is still publishing.
                Set(monitor, "cached", Activator.CreateInstance(app.GetType("CodexUsageOverlay.CodexThreadContextSnapshot", true), true));
                Call(monitor, "QueuePositionRefresh");
            };
            EventInfo changed = monitorType.GetEvent("SidebarChanged", All);
            changed.GetAddMethod(true).Invoke(monitor, new object[] { keepScrolling });
            Call(monitor, "RefreshPositions", new object[] { null });
            Stopwatch wait = Stopwatch.StartNew();
            while ((Volatile.Read(ref frames) < 2 || (int)Get(monitor, "positionRunning") != 0) && wait.ElapsedMilliseconds < 1500)
                Thread.Sleep(5);
            Thread.Sleep(80);
            changed.GetRemoveMethod(true).Invoke(monitor, new object[] { keepScrolling });
            if (frames != 2 || (int)Get(monitor, "positionQueued") != 1)
                throw new Exception("Continuous scroll must retain one pending frame after exactly one immediate catch-up, not spin or lose it: " + frames);
            Set(monitor, "lastRequestUtc", DateTime.UtcNow); // The real host refresh renews this independently.
            Call(monitor, "RefreshPositions", new object[] { null });
            if ((int)Get(monitor, "positionQueued") != 0 ||
                ((IList)Get(Call(monitor, "Snapshot", host.Handle, bounds), "Rows")).Count == 0)
                throw new Exception("The normal timer pass failed to consume the remaining frame while the data worker was busy");
            Console.WriteLine("PASS one immediate serial catch-up; event storms retain the next timer frame without an unbounded loop or data-worker wait");
        }
        finally { ((IDisposable)monitor).Dispose(); }
    }

    private static int ScrollBenchmark(Assembly app, Type monitorType, int count)
    {
        using (var host = new Form { Text = "Owned sidebar scroll benchmark", Width = 1100, Height = 650 })
        using (var list = List())
        {
            for (int i = 0; i < count; i++) list.Items.Add("Thread " + i);
            host.Controls.Add(list);
            host.Shown += delegate
            {
                IntPtr handle = list.Handle;
                Rectangle bounds = host.Bounds;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        AutomationElement root = AutomationElement.FromHandle(handle);
                        Type signalType = app.GetType("CodexUsageOverlay.CodexContextSignal", true);
                        Type rowType = app.GetType("CodexUsageOverlay.CodexSidebarContextRow", true);
                        IDictionary signals = (IDictionary)Activator.CreateInstance(
                            typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(string), signalType));
                        IDictionary geometry = (IDictionary)Activator.CreateInstance(
                            typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(string), rowType));
                        var initial = (AutomationElementCollection)monitorType.GetMethod("FindCachedElements", All).Invoke(null,
                            new object[] { root, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem) });
                        System.Windows.Rect raw = initial[0].Cached.BoundingRectangle;
                        Rectangle first = Rectangle.FromLTRB((int)raw.Left, (int)raw.Top, (int)raw.Right, (int)raw.Bottom);
                        for (int i = 0; i < count; i++)
                        {
                            object signal = Activator.CreateInstance(signalType, true);
                            Set(signal, "UsedTokens", 1000L + i); Set(signal, "WindowTokens", 10000L);
                            signals.Add("Thread " + i, signal);
                            object row = Activator.CreateInstance(rowType, true);
                            Set(row, "Bounds", first); Set(row, "TitleBounds", new Rectangle(first.Left + 8, first.Top + 3, 80, 20));
                            geometry.Add("Thread " + i, row);
                        }
                        var samples = new System.Collections.Generic.List<long>();
                        for (int frame = 0; frame < 64; frame++)
                        {
                            int top = frame % Math.Min(count - 16, 60);
                            host.Invoke((Action)delegate { list.TopIndex = top; });
                            Stopwatch elapsed = Stopwatch.StartNew();
                            object[] query = { root, bounds, signals, false, 1f, geometry };
                            IList rows = (IList)monitorType.GetMethod("ReadSidebarPositions", All).Invoke(null, query);
                            if (frame >= 4) samples.Add(elapsed.ElapsedMilliseconds);
                            if (rows.Count == 0 || (long)Get(Get(rows[0], "Signal"), "UsedTokens") != 1000 + top)
                                throw new Exception("Continuous-scroll benchmark lost current-row identity at " + top);
                        }
                        samples.Sort();
                        Console.WriteLine("BENCH assembly=" + app.GetName().Version + "; rows=" + count + "; frames=" + samples.Count +
                            "; p50=" + samples[samples.Count / 2] + "ms; p95=" + samples[(samples.Count * 95 + 99) / 100 - 1] +
                            "ms; max=" + samples[samples.Count - 1] + "ms; identity=verified");
                    }
                    catch (Exception error) { failure = error; }
                    finally { host.BeginInvoke((Action)delegate { host.Close(); }); }
                });
            };
            Application.Run(host);
        }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }
    private static object Call(object target, string name, params object[] args)
    { return target.GetType().GetMethod(name, All).Invoke(target, args); }
    private static object Get(object target, string name)
    { return target.GetType().GetField(name, All).GetValue(target); }
    private static void Set(object target, string name, object value)
    { target.GetType().GetField(name, All).SetValue(target, value); }
}
