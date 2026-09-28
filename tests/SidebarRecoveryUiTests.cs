// Read-only UI Automation against this test's own window. No user app is controlled.
using System;
using System.Collections;
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
    private static object Call(object target, string name, params object[] args)
    { return target.GetType().GetMethod(name, All).Invoke(target, args); }
    private static object Get(object target, string name)
    { return target.GetType().GetField(name, All).GetValue(target); }
    private static void Set(object target, string name, object value)
    { target.GetType().GetField(name, All).SetValue(target, value); }
}
