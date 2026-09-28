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
            foreach (int dpi in new[] { 100, 125, 150, 175, 200 })
            {
                Func<int, int> px = delegate(int value) { return value * dpi / 100; };
                var host = new Rectangle(0, 0, px(1400), px(1000));
                var editor = new Rectangle(px(320), px(880), px(700), px(40));
                var textWrapper = new Rectangle(editor.Left, editor.Top, editor.Width, px(80));
                var frame = new Rectangle(px(308), px(866), px(724), px(98));
                var attachedFrame = Rectangle.FromLTRB(frame.Left, px(650), frame.Right, frame.Bottom);
                var withAttachment = CodexConversationSurfaceMonitor.SelectComposerSurfaceBounds(editor, host,
                    new[] { editor, textWrapper, attachedFrame, host });
                var withoutAttachment = CodexConversationSurfaceMonitor.SelectComposerSurfaceBounds(editor, host,
                    new[] { editor, textWrapper, frame, host });
                if (withAttachment != attachedFrame || withoutAttachment != frame)
                    throw new Exception("composer border must survive attachment height and exclude the inner text wrapper at DPI " + dpi);
                Rectangle client = new Rectangle(px(8), px(30), host.Width - px(16), px(950));
                Rectangle allowed = OverlayInteraction.GetContextStripAllowedBounds(host, host, client, dpi / 100f);
                if (allowed.Bottom != client.Bottom - (int)Math.Ceiling(2 * dpi / 100f) || !client.Contains(allowed))
                    throw new Exception("context strip must exclude non-client borders and retain a bottom safety inset");
                if (!OverlayInteraction.GetContextStripAllowedBounds(host, host, Rectangle.Empty, dpi / 100f).IsEmpty)
                    throw new Exception("missing client bounds must not use an unsafe window frame fallback");
                Rectangle plain = OverlayInteraction.GetContextStripPlacementBounds(editor, withoutAttachment, allowed, px(360), dpi / 100f, false);
                Rectangle attached = OverlayInteraction.GetContextStripPlacementBounds(editor, withAttachment, allowed, px(360), dpi / 100f, false);
                if (plain.IsEmpty || attached != plain || !allowed.Contains(attached))
                    throw new Exception("attaching an image must not move the bottom border anchor");
            }
            var overlay = new Rectangle(100, 100, 688, 450);
            var download = new Rectangle(100, 550, 688, 400);
            if (OutsideClickMonitor.IsOutside(new Point(120,120), overlay, download) ||
                OutsideClickMonitor.IsOutside(new Point(120,600), overlay, download) ||
                !OutsideClickMonitor.IsOutside(new Point(10,10), overlay, download))
                throw new Exception("outside click must exclude both expanded panels");
            var header = new Rectangle(300, 600, 400, 28);
            var work = new Rectangle(0, 0, 1920, 1080);
            var splitWindow = new Rectangle(-8, -8, 1936, 1048);
            if (!CodexConversationSurfaceMonitor.LooksLikeConversationComposer(
                    splitWindow, new Rectangle(416, 932, 442, 44)) ||
                CodexConversationSurfaceMonitor.LooksLikeConversationComposer(
                    splitWindow, new Rectangle(416, 932, 250, 44)))
                throw new Exception("split-pane conversation composer width was misclassified");
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
                    Surface = new Rectangle(bounds.Left + 10, bounds.Top + 490, 420, 100),
                    SafeFooter = new Rectangle(bounds.Left + 100, bounds.Top + 550, 180, 40)
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
                Rectangle safeFooter;
                if (!monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface, out safeFooter) ||
                    safeFooter != new Rectangle(130, 590, 180, 40)) throw new Exception("toolbar safe space did not follow host movement");
                if (monitor.TryGetConversationBounds(new IntPtr(2), host, out composer, out surface)) throw new Exception("cache leaked into another window");
            }
        }
    }
}
