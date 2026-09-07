using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;

namespace CodexUsageOverlay
{
    internal static class ConversationProbeTests
    {
        internal static void Verify()
        {
            var overlay = new Rectangle(100, 100, 688, 450);
            var download = new Rectangle(100, 550, 688, 400);
            if (OutsideClickMonitor.IsOutside(new Point(120,120), overlay, download) ||
                OutsideClickMonitor.IsOutside(new Point(120,600), overlay, download) ||
                !OutsideClickMonitor.IsOutside(new Point(10,10), overlay, download))
                throw new Exception("outside click must exclude both expanded panels");
            var header = new Rectangle(300, 600, 400, 28);
            var work = new Rectangle(0, 0, 1920, 1080);
            var panel = new Size(688, 400);
            var above = OverlayInteraction.GetAttachedDownloadBounds(header, panel, work, true);
            var below = OverlayInteraction.GetAttachedDownloadBounds(header, panel, work, false);
            if (above.Left != 156 || above.Bottom != header.Top || below.Left != above.Left || below.Top != header.Bottom)
                throw new Exception("download panel must center on the same header and expand without offset");
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var finished = new ManualResetEvent(false))
            using (var monitor = new CodexConversationSurfaceMonitor(delegate(IntPtr handle, Rectangle bounds)
            {
                entered.Set();
                release.WaitOne(2000);
                finished.Set();
                return new CodexConversationSurfaceMonitor.ProbeResult {
                    Composer = new Rectangle(bounds.Left + 20, bounds.Top + 500, 400, 50),
                    Surface = new Rectangle(bounds.Left + 10, bounds.Top + 490, 420, 100)
                };
            }))
            {
                Rectangle composer, surface;
                var host = new Rectangle(0, 0, 800, 700);
                var watch = Stopwatch.StartNew();
                monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface);
                if (watch.ElapsedMilliseconds > 200) throw new Exception("UI caller waited for scan");
                if (!entered.WaitOne(1000)) throw new Exception("scan did not start");
                watch.Restart();
                for (int i = 0; i < 30; i++) monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface);
                if (watch.ElapsedMilliseconds > 200) throw new Exception("pending scan blocked UI");
                release.Set();
                if (!finished.WaitOne(1000)) throw new Exception("scan did not finish");
                watch.Restart();
                while (!monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface) && watch.ElapsedMilliseconds < 1000) Thread.Sleep(1);
                if (composer.IsEmpty) throw new Exception("background result was not cached");
                host.Offset(30, 40);
                if (!monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface) || composer.X != 50 || composer.Y != 540) throw new Exception("cached bounds did not follow window movement");
                if (monitor.TryGetConversationBounds(new IntPtr(2), host, out composer, out surface)) throw new Exception("cache leaked into another window");
            }
        }
    }
}
