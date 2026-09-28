using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
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
                var beforeUnavailableAncestor = CodexConversationSurfaceMonitor.SelectComposerSurfaceBounds(editor, host,
                    AncestorsWithUnavailableParent(editor, textWrapper, frame));
                if (beforeUnavailableAncestor != frame)
                    throw new Exception("a valid composer frame must be returned before probing unavailable ancestors at DPI " + dpi);
                if (!CodexConversationSurfaceMonitor.SelectComposerSurfaceBounds(editor, host,
                        new[] { editor, textWrapper, host }).IsEmpty)
                    throw new Exception("missing composer frame must not fall back to the editor or text wrapper at DPI " + dpi);
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
            VerifyProbeRecovery();
            VerifyBottomBorderAnchor();
            VerifyHostGeometryRaces();
        }

        private static void VerifyHostGeometryRaces()
        {
            MethodInfo notify = typeof(CodexConversationSurfaceMonitor).GetMethod("NotifyHostGeometryChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (notify == null)
                throw new Exception("host geometry notifications must invalidate in-flight composer generations");

            var originalHost = new Rectangle(100, 100, 800, 700);
            var movedHost = new Rectangle(300, 200, 800, 700);
            var resizedHost = new Rectangle(0, 0, 1200, 900);
            CodexConversationSurfaceMonitor.ProbeResult next = ProbeAt(originalHost);
            int block = 0;
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var monitor = new CodexConversationSurfaceMonitor(delegate(IntPtr handle, Rectangle bounds)
            {
                if (Thread.VolatileRead(ref block) != 0)
                {
                    entered.Set();
                    if (!release.WaitOne(2000)) throw new Exception("synthetic host transition was not released");
                }
                return next;
            }))
            {
                RunProbeAndWait(monitor, originalHost);
                AssertProbeGeometry(monitor, new IntPtr(1), originalHost, ProbeAt(originalHost),
                    "initial confirmed geometry missing");

                // UIA returns new absolute coordinates after the worker was started at the old origin.
                // The old generation must not publish them under the old origin and offset them twice.
                next = ProbeAt(movedHost);
                Interlocked.Exchange(ref block, 1);
                StartProbe(monitor, new IntPtr(1), originalHost);
                if (!entered.WaitOne(1000)) throw new Exception("move race scan did not start");
                notify.Invoke(monitor, new object[] { new IntPtr(1), movedHost });
                AssertProbeGeometry(monitor, new IntPtr(1), movedHost, ProbeAt(movedHost),
                    "a pure move must keep the confirmed cache following immediately");
                release.Set();
                WaitForProbeCompletion(monitor, false);
                AssertImmediateProbeRetry(monitor);
                FreezeProbe(monitor);
                AssertProbeGeometry(monitor, new IntPtr(1), movedHost, ProbeAt(movedHost),
                    "late absolute UIA coordinates were applied to the old origin a second time");
                Interlocked.Exchange(ref block, 0);
                RunProbeAndWait(monitor, movedHost);
                AssertProbeGeometry(monitor, new IntPtr(1), movedHost, next,
                    "fresh geometry did not recover after a rejected moved-window scan");

                // A -> B -> A must invalidate a blocked A worker even when the UI timer never saw B.
                entered.Reset(); release.Reset();
                Interlocked.Exchange(ref block, 1);
                StartProbe(monitor, new IntPtr(1), movedHost);
                if (!entered.WaitOne(1000)) throw new Exception("resize ABA scan did not start");
                notify.Invoke(monitor, new object[] { new IntPtr(1), resizedHost });
                notify.Invoke(monitor, new object[] { new IntPtr(1), movedHost });
                release.Set();
                WaitForProbeCompletion(monitor, false);
                AssertImmediateProbeRetry(monitor);
                FreezeProbe(monitor);
                AssertProbeHidden(monitor, new IntPtr(1), movedHost,
                    "an ABA resize accepted an old worker only because its final size matched");
                Interlocked.Exchange(ref block, 0);
                RunProbeAndWait(monitor, movedHost);
                AssertProbeHidden(monitor, new IntPtr(1), movedHost,
                    "the first resize observation was published without confirmation");
                RunProbeAndWait(monitor, movedHost);
                AssertProbeGeometry(monitor, new IntPtr(1), movedHost, next,
                    "matching fresh observations did not restore geometry after the ABA resize");

                entered.Reset(); release.Reset();
                Interlocked.Exchange(ref block, 1);
                StartProbe(monitor, new IntPtr(1), movedHost);
                if (!entered.WaitOne(1000)) throw new Exception("window replacement scan did not start");
                notify.Invoke(monitor, new object[] { new IntPtr(2), movedHost });
                release.Set();
                WaitForProbeCompletion(monitor, false);
                FreezeProbe(monitor);
                AssertProbeHidden(monitor, new IntPtr(2), movedHost,
                    "a worker from another window reused its predecessor's coordinates");
                Interlocked.Exchange(ref block, 0);
            }

            next = ProbeAt(originalHost);
            int failure = 0;
            using (var monitor = new CodexConversationSurfaceMonitor(delegate(IntPtr handle, Rectangle bounds)
            {
                if (Thread.VolatileRead(ref failure) == 1) throw new COMException("synthetic resize failure");
                return Thread.VolatileRead(ref failure) == 2 ? null : next;
            }))
            {
                RunProbeAndWait(monitor, originalHost);
                notify.Invoke(monitor, new object[] { new IntPtr(1), resizedHost });
                // This is the transient pre-reflow layout, translated into the new host origin.
                next = ProbeAt(new Rectangle(resizedHost.Location, originalHost.Size));
                RunProbeAndWait(monitor, resizedHost);
                AssertProbeHidden(monitor, new IntPtr(1), resizedHost,
                    "the first post-resize stale internal layout was shown");
                next = ProbeAt(resizedHost);
                RunProbeAndWait(monitor, resizedHost);
                AssertProbeHidden(monitor, new IntPtr(1), resizedHost,
                    "a changed frame was accepted without a second coherent observation");
                // Toolbar width can legitimately jitter without moving the frame itself.
                next = ProbeAt(resizedHost);
                next.SafeFooter.Width -= 2;
                RunProbeAndWait(monitor, resizedHost);
                AssertProbeGeometry(monitor, new IntPtr(1), resizedHost, next,
                    "stable composer/frame geometry must confirm despite toolbar-width jitter");

                foreach (int failedKind in new[] { 1, 2 })
                {
                    resizedHost.Width += 40;
                    next = ProbeAt(resizedHost);
                    notify.Invoke(monitor, new object[] { new IntPtr(1), resizedHost });
                    RunProbeAndWait(monitor, resizedHost);
                    AssertProbeHidden(monitor, new IntPtr(1), resizedHost, "resize candidate was shown early");
                    Interlocked.Exchange(ref failure, failedKind);
                    RunProbeAndWait(monitor, resizedHost);
                    AssertProbeHidden(monitor, new IntPtr(1), resizedHost, "failed or absent scan retained resize geometry");
                    Interlocked.Exchange(ref failure, 0);
                    RunProbeAndWait(monitor, resizedHost);
                    AssertProbeHidden(monitor, new IntPtr(1), resizedHost,
                        "a failed or absent scan failed to clear the pending confirmation");
                    RunProbeAndWait(monitor, resizedHost);
                    AssertProbeGeometry(monitor, new IntPtr(1), resizedHost, next,
                        "two coherent scans did not recover after failure cleared confirmation");
                }
            }
        }

        private static CodexConversationSurfaceMonitor.ProbeResult ProbeAt(Rectangle host)
        {
            int width = Math.Min(700, host.Width - 200);
            var composer = new Rectangle(host.Left + 100, host.Bottom - 120, width, 40);
            return new CodexConversationSurfaceMonitor.ProbeResult {
                Composer = composer,
                Surface = new Rectangle(composer.Left - 12, composer.Top - 14, width + 24, 98),
                SafeFooter = new Rectangle(composer.Left + (width - 260) / 2, composer.Bottom, 260, 44)
            };
        }

        private static void AssertProbeGeometry(CodexConversationSurfaceMonitor monitor, IntPtr window,
            Rectangle host, CodexConversationSurfaceMonitor.ProbeResult expected, string message)
        {
            Rectangle composer, surface, footer;
            if (!monitor.TryGetConversationBounds(window, host, out composer, out surface, out footer) ||
                composer != expected.Composer || surface != expected.Surface || footer != expected.SafeFooter)
                throw new Exception(message);
        }

        private static void AssertProbeHidden(CodexConversationSurfaceMonitor monitor, IntPtr window,
            Rectangle host, string message)
        {
            Rectangle composer, surface, footer;
            if (monitor.TryGetConversationBounds(window, host, out composer, out surface, out footer) ||
                !composer.IsEmpty || !surface.IsEmpty || !footer.IsEmpty) throw new Exception(message);
        }

        private static void StartProbe(CodexConversationSurfaceMonitor monitor, IntPtr window, Rectangle host)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(CodexConversationSurfaceMonitor);
            object gate = type.GetField("gate", flags).GetValue(monitor);
            lock (gate) type.GetField("nextProbeUtc", flags).SetValue(monitor, DateTime.MinValue);
            Rectangle composer, surface;
            monitor.TryGetConversationBounds(window, host, out composer, out surface);
        }

        private static void WaitForProbeCompletion(CodexConversationSurfaceMonitor monitor, bool freeze)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(CodexConversationSurfaceMonitor);
            object gate = type.GetField("gate", flags).GetValue(monitor);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 2000)
            {
                lock (gate)
                    if (!(bool)type.GetField("running", flags).GetValue(monitor))
                    {
                        if (freeze) type.GetField("nextProbeUtc", flags).SetValue(monitor, DateTime.MaxValue);
                        return;
                    }
                Thread.Sleep(1);
            }
            throw new Exception("synthetic raced scan did not complete within its bounded wait");
        }

        private static void FreezeProbe(CodexConversationSurfaceMonitor monitor)
        {
            WaitForProbeCompletion(monitor, true);
        }

        private static void AssertImmediateProbeRetry(CodexConversationSurfaceMonitor monitor)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(CodexConversationSurfaceMonitor);
            object gate = type.GetField("gate", flags).GetValue(monitor);
            lock (gate)
                if ((DateTime)type.GetField("nextProbeUtc", flags).GetValue(monitor) > DateTime.UtcNow)
                    throw new Exception("a rejected host-generation result delayed the required fresh scan");
        }

        private static void VerifyBottomBorderAnchor()
        {
            foreach (float scale in new[] { 1f, 1.125f, 1.25f, 1.5f, 1.75f, 2f })
            {
                Func<int, int> px = delegate(int value) { return (int)Math.Round(value * scale); };
                // Live geometry: the frame ends at 1022, but the usable desktop ends at 1030.
                // Pulling a 42px split strip up to 988 detaches its first row from that border.
                Rectangle editor = Rectangle.FromLTRB(px(831), px(938), px(1543), px(982));
                Rectangle frame = Rectangle.FromLTRB(px(819), px(924), px(1555), px(1022));
                Rectangle attachedFrame = Rectangle.FromLTRB(frame.Left, px(794), frame.Right, frame.Bottom);
                Rectangle footer = Rectangle.FromLTRB(px(1065), editor.Bottom, px(1309), frame.Bottom);
                Rectangle allowed = Rectangle.FromLTRB(px(263), px(84), px(1900), px(1030));
                int row = px(20), height = px(42), minimumLine = (int)Math.Ceiling(16 * scale);
                int preferredWidth = px(320), minimumWidth = px(200);
                Rectangle strip = OverlayInteraction.GetContextStripPlacementBounds(editor, frame, allowed,
                    preferredWidth, scale, false, footer, minimumWidth, minimumLine);
                if (strip.IsEmpty || strip.Height != row || strip.Bottom != frame.Bottom || !allowed.Contains(strip))
                    throw new Exception("insufficient outside space must use one safe row at the frame bottom, never lift two rows at scale " + scale);
                Rectangle attached = OverlayInteraction.GetContextStripPlacementBounds(editor, attachedFrame, allowed,
                    preferredWidth, scale, false, footer, minimumWidth, minimumLine);
                if (attached != strip)
                    throw new Exception("an attachment above the editor must not change the frame-bottom strip anchor");

                Rectangle noOutside = Rectangle.FromLTRB(allowed.Left, allowed.Top, allowed.Right, frame.Bottom);
                Rectangle insideOnly = OverlayInteraction.GetContextStripPlacementBounds(editor, frame, noOutside,
                    preferredWidth, scale, false, footer, minimumWidth, minimumLine);
                if (insideOnly != strip || !noOutside.Contains(insideOnly))
                    throw new Exception("a readable safe footer must retain its bottom-aligned single row with no external space");
                Rectangle defaultMinimum = OverlayInteraction.GetContextStripPlacementBounds(editor, frame, noOutside,
                    preferredWidth, scale, false, footer, minimumWidth);
                if (defaultMinimum != insideOnly)
                    throw new Exception("omitted font-height metadata must conservatively allow a full-height safe internal row");
                Rectangle clippedBorder = Rectangle.FromLTRB(allowed.Left, allowed.Top, allowed.Right, frame.Bottom - 1);
                if (!OverlayInteraction.GetContextStripPlacementBounds(editor, frame, clippedBorder,
                        preferredWidth, scale, false, footer, minimumWidth, minimumLine).IsEmpty)
                    throw new Exception("a clipped frame border must not be replaced with an upward-shifted strip");

                Rectangle enoughOutside = Rectangle.FromLTRB(allowed.Left, allowed.Top, allowed.Right,
                    frame.Bottom + height - row);
                Rectangle split = OverlayInteraction.GetContextStripPlacementBounds(editor, frame, enoughOutside,
                    preferredWidth, scale, false, footer, minimumWidth, minimumLine);
                if (split.IsEmpty || split.Height != height || split.Top + row != frame.Bottom || !enoughOutside.Contains(split))
                    throw new Exception("normal split rows must meet the frame bottom exactly at the first-row lower edge");
                Rectangle noGapOutside = Rectangle.FromLTRB(allowed.Left, allowed.Top, allowed.Right, frame.Bottom + row);
                Rectangle noGapSplit = OverlayInteraction.GetContextStripPlacementBounds(editor, frame, noGapOutside,
                    preferredWidth, scale, false, footer, minimumWidth, minimumLine);
                if (noGapSplit.IsEmpty || noGapSplit.Height != row * 2 || noGapSplit.Top + row != frame.Bottom ||
                    !noGapOutside.Contains(noGapSplit))
                    throw new Exception("two full rows without their decorative gap must retain the frame-bottom split anchor");
                Rectangle bothInside = OverlayInteraction.GetContextStripPlacementBounds(editor, frame, noOutside,
                    preferredWidth, scale, true, footer, minimumWidth, minimumLine);
                if (bothInside.IsEmpty || bothInside.Bottom != frame.Bottom || bothInside.Top < editor.Bottom ||
                    bothInside.Height < row * 2 || !noOutside.Contains(bothInside))
                    throw new Exception("the configured two-inside-rows layout must remain bottom anchored in its safe footer");

                Rectangle blockedFooter = new Rectangle(frame.Left + frame.Width / 2, editor.Bottom,
                    1, frame.Bottom - editor.Bottom);
                if (!OverlayInteraction.GetContextStripPlacementBounds(editor, frame, allowed,
                        preferredWidth, scale, false, blockedFooter, minimumWidth, minimumLine).IsEmpty)
                    throw new Exception("an obstructed center must not use the internal one-row fallback");
                Rectangle shortOutside = Rectangle.FromLTRB(allowed.Left, allowed.Top, allowed.Right,
                    frame.Bottom + minimumLine - 1);
                if (!OverlayInteraction.GetContextStripPlacementBounds(editor, frame, shortOutside,
                        preferredWidth, scale, false, blockedFooter, minimumWidth, minimumLine).IsEmpty)
                    throw new Exception("an external single line must not exceed its available height");
                Rectangle oneLineOutside = Rectangle.FromLTRB(allowed.Left, allowed.Top, allowed.Right,
                    frame.Bottom + minimumLine);
                Rectangle external = OverlayInteraction.GetContextStripPlacementBounds(editor, frame, oneLineOutside,
                    preferredWidth, scale, false, footer, minimumWidth, minimumLine);
                if (external.IsEmpty || external.Top != frame.Bottom || external.Height != minimumLine ||
                    !oneLineOutside.Contains(external))
                    throw new Exception("a readable external single row must take precedence over moving inside the toolbar");
                external = OverlayInteraction.GetContextStripPlacementBounds(editor, frame, oneLineOutside,
                    preferredWidth, scale, false, blockedFooter, minimumWidth, minimumLine);
                if (external.IsEmpty || external.Top != frame.Bottom || !oneLineOutside.Contains(external))
                    throw new Exception("a blocked footer must still permit an independently safe external row");
            }
        }

        private static void VerifyProbeRecovery()
        {
            int stage = 0;
            var host = new Rectangle(0, 0, 800, 700);
            var editor = new Rectangle(220, 560, 460, 40);
            var frame = new Rectangle(208, 546, 484, 98);
            var movedEditor = new Rectangle(320, 560, 360, 40);
            var blockedFooter = new Rectangle(frame.Left + frame.Width / 2,
                editor.Bottom, 1, frame.Bottom - editor.Bottom);
            using (var monitor = new CodexConversationSurfaceMonitor(delegate(IntPtr handle, Rectangle bounds)
            {
                switch (Thread.VolatileRead(ref stage))
                {
                    case 1: return null;
                    case 2: return new CodexConversationSurfaceMonitor.ProbeResult {
                        Composer = movedEditor, RetryRequired = true, Status = "surface-unavailable"
                    };
                    case 3: throw new COMException("synthetic unavailable composer");
                    case 4: return new CodexConversationSurfaceMonitor.ProbeResult {
                        Composer = editor, Surface = frame, SafeFooter = blockedFooter,
                        RetryRequired = true, Status = "toolbar-error:COMException"
                    };
                    default: return new CodexConversationSurfaceMonitor.ProbeResult {
                        Composer = editor, Surface = frame,
                        SafeFooter = new Rectangle(220, editor.Bottom, 460, frame.Bottom - editor.Bottom)
                    };
                }
            }))
            {
                Rectangle composer, surface, safeFooter;
                RunProbeAndWait(monitor, host);
                if (!monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface, out safeFooter) || surface != frame)
                    throw new Exception("recovery fixture did not establish a valid composer frame");

                Interlocked.Exchange(ref stage, 1);
                RunProbeAndWait(monitor, host);
                if (monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface, out safeFooter) ||
                    !composer.IsEmpty || !surface.IsEmpty || !safeFooter.IsEmpty || monitor.DiagnosticStatus != "composer-absent")
                    throw new Exception("a confirmed absent composer must immediately clear prior geometry");

                Interlocked.Exchange(ref stage, 0);
                RunProbeAndWait(monitor, host);
                Interlocked.Exchange(ref stage, 2);
                RunProbeAndWait(monitor, host);
                if (!monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface, out safeFooter) ||
                    composer != movedEditor || !surface.IsEmpty || !safeFooter.IsEmpty)
                    throw new Exception("a new composer without its frame must not inherit the previous frame or toolbar bounds");
                if (!OverlayInteraction.GetContextStripPlacementBounds(composer, surface, host, 360, 1, false).IsEmpty)
                    throw new Exception("an incomplete composer frame must not produce a strip position");

                Interlocked.Exchange(ref stage, 0);
                RunProbeAndWait(monitor, host);
                Interlocked.Exchange(ref stage, 3);
                RunProbeAndWait(monitor, host);
                if (monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface, out safeFooter) ||
                    !composer.IsEmpty || !surface.IsEmpty || !safeFooter.IsEmpty || monitor.DiagnosticStatus != "scan-error:COMException")
                    throw new Exception("a failed scan must report its cause without extending old geometry");
                Interlocked.Exchange(ref stage, 0);
                RunProbeAndWait(monitor, host);
                if (!monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface, out safeFooter) ||
                    composer != editor || surface != frame || monitor.DiagnosticStatus != "ready")
                    throw new Exception("composer probing must recover after a transient COM failure");

                Interlocked.Exchange(ref stage, 4);
                RunProbeAndWait(monitor, host);
                if (!monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface, out safeFooter) ||
                    surface != frame || safeFooter != blockedFooter || monitor.DiagnosticStatus != "toolbar-error:COMException")
                    throw new Exception("toolbar failure must retain only the fresh frame and explicitly block its interior");
                var allowed = OverlayInteraction.GetContextStripAllowedBounds(host, host, host, 1);
                foreach (bool bothInside in new[] { false, true })
                {
                    Rectangle strip = OverlayInteraction.GetContextStripPlacementBounds(composer, surface, allowed,
                        360, 1, bothInside, safeFooter, 280, 16);
                    if (strip.IsEmpty || strip.Top < surface.Bottom || !allowed.Contains(strip))
                        throw new Exception("unknown toolbar bounds must use only a contained external strip fallback");
                }
            }
        }

        private static void RunProbeAndWait(CodexConversationSurfaceMonitor monitor, Rectangle host)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(CodexConversationSurfaceMonitor);
            object gate = type.GetField("gate", flags).GetValue(monitor);
            FieldInfo nextProbe = type.GetField("nextProbeUtc", flags);
            FieldInfo running = type.GetField("running", flags);
            lock (gate) nextProbe.SetValue(monitor, DateTime.MinValue);
            Rectangle composer, surface;
            monitor.TryGetConversationBounds(new IntPtr(1), host, out composer, out surface);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 2000)
            {
                lock (gate)
                {
                    if (!(bool)running.GetValue(monitor))
                    {
                        // Keep assertions from scheduling another scan of the mutable fixture.
                        nextProbe.SetValue(monitor, DateTime.MaxValue);
                        return;
                    }
                }
                Thread.Sleep(1);
            }
            throw new Exception("synthetic composer scan did not complete within its bounded wait");
        }

        private static IEnumerable<Rectangle> AncestorsWithUnavailableParent(
            Rectangle editor, Rectangle textWrapper, Rectangle frame)
        {
            yield return editor;
            yield return textWrapper;
            yield return frame;
            throw new InvalidOperationException("ancestor above the composer frame is no longer available");
        }
    }
}
