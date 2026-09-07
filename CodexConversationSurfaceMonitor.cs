using System;
using System.Drawing;
using System.Threading;
using System.Windows.Automation;

namespace CodexUsageOverlay
{
    internal sealed class CodexConversationSurfaceMonitor : IDisposable
    {
        internal sealed class ProbeResult
        {
            internal Rectangle Composer, Surface;
        }
        private readonly object gate = new object();
        private readonly Func<IntPtr, Rectangle, ProbeResult> probe;
        private DateTime nextProbeUtc = DateTime.MinValue, cachedUtc;
        private IntPtr requestedWindow, cachedWindow;
        private Rectangle requestedBounds, cachedBounds;
        private ProbeResult cached;
        private bool running, disposed;

        internal CodexConversationSurfaceMonitor() : this(Probe) { }
        internal CodexConversationSurfaceMonitor(Func<IntPtr, Rectangle, ProbeResult> probe)
        {
            this.probe = probe;
        }
        public void Dispose()
        {
            lock (gate) { disposed = true; cached = null; }
        }

        internal bool IsConversationInputVisible(IntPtr windowHandle, Rectangle windowBounds)
        {
            Rectangle composerBounds;
            return TryGetConversationInputBounds(windowHandle, windowBounds, out composerBounds);
        }

        internal bool TryGetConversationInputBounds(
            IntPtr windowHandle,
            Rectangle windowBounds,
            out Rectangle composerBounds)
        {
            Rectangle composerSurfaceBounds;
            return TryGetConversationBounds(windowHandle, windowBounds, out composerBounds,
                out composerSurfaceBounds);
        }

        internal bool TryGetConversationBounds(
            IntPtr windowHandle, Rectangle windowBounds,
            out Rectangle composerBounds, out Rectangle composerSurfaceBounds)
        {
            lock (gate)
            {
                composerBounds = Rectangle.Empty;
                composerSurfaceBounds = Rectangle.Empty;
                if (disposed) return false;
                requestedWindow = windowHandle;
                requestedBounds = windowBounds;
                DateTime now = DateTime.UtcNow;
                if (windowHandle == IntPtr.Zero || windowBounds.Width <= 0 || windowBounds.Height <= 0)
                { cached = null; return false; }
                if (!running && (now >= nextProbeUtc || cachedWindow != windowHandle || cachedBounds.Size != windowBounds.Size))
                {
                    running = true;
                    nextProbeUtc = now.AddMilliseconds(800);
                    // UI Automation may block in another process. Never execute it on the UI thread.
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        ProbeResult result = null;
                        try { result = probe(windowHandle, windowBounds); }
                        catch { }
                        lock (gate)
                        {
                            running = false;
                            if (disposed || requestedWindow != windowHandle || requestedBounds.Size != windowBounds.Size) return;
                            cachedWindow = windowHandle;
                            cachedBounds = windowBounds;
                            cached = result;
                            cachedUtc = DateTime.UtcNow;
                            nextProbeUtc = cachedUtc.AddMilliseconds(800);
                        }
                    });
                }
                if (cached == null || cached.Composer.IsEmpty || cachedWindow != windowHandle ||
                    cachedBounds.Size != windowBounds.Size || now - cachedUtc > TimeSpan.FromSeconds(5))
                    return false;
                composerBounds = cached.Composer;
                composerSurfaceBounds = cached.Surface;
                int dx = windowBounds.Left - cachedBounds.Left, dy = windowBounds.Top - cachedBounds.Top;
                composerBounds.Offset(dx, dy);
                composerSurfaceBounds.Offset(dx, dy);
                return true;
            }
        }

        private static ProbeResult Probe(IntPtr windowHandle, Rectangle windowBounds)
        {
            Rectangle composer = Rectangle.Empty, surface = Rectangle.Empty;
            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                if (root == null)
                    return null;

                Condition editCondition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty, ControlType.Edit);
                AutomationElementCollection elements = root.FindAll(
                    TreeScope.Descendants, editCondition);
                AutomationElement composerElement = null;
                foreach (AutomationElement element in elements)
                {
                    if (element.Current.IsOffscreen)
                        continue;
                    System.Windows.Rect bounds = element.Current.BoundingRectangle;
                    Rectangle candidate = Rectangle.FromLTRB(
                        (int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                        (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
                    if (LooksLikeConversationComposer(windowBounds, candidate))
                    {
                        if (composer.IsEmpty || candidate.Width > composer.Width ||
                            (candidate.Width == composer.Width &&
                                candidate.Bottom > composer.Bottom))
                        {
                            composer = candidate;
                            composerElement = element;
                        }
                    }
                }

                if (!composer.IsEmpty)
                {
                    surface = FindComposerSurfaceBounds(
                        composerElement, composer, windowBounds);
                    if (surface.IsEmpty)
                        surface = composer;
                    return new ProbeResult { Composer = composer, Surface = surface };
                }
            }
            catch
            {
            }
            return null;
        }

        private static Rectangle FindComposerSurfaceBounds(
            AutomationElement composerElement,
            Rectangle composerBounds,
            Rectangle windowBounds)
        {
            try
            {
                Rectangle best = Rectangle.Empty;
                AutomationElement element = composerElement;
                while (element != null)
                {
                    if (element.Current.IsOffscreen)
                    {
                        element = TreeWalker.ControlViewWalker.GetParent(element);
                        continue;
                    }
                    System.Windows.Rect rawBounds = element.Current.BoundingRectangle;
                    Rectangle candidate = Rectangle.FromLTRB(
                        (int)Math.Floor(rawBounds.Left), (int)Math.Floor(rawBounds.Top),
                        (int)Math.Ceiling(rawBounds.Right), (int)Math.Ceiling(rawBounds.Bottom));
                    int footerHeight = candidate.Bottom - composerBounds.Bottom;
                    if (Contains(candidate, composerBounds) &&
                        candidate.Width <= windowBounds.Width * 96 / 100 &&
                        candidate.Height <= composerBounds.Height + 120 &&
                        candidate.Width <= composerBounds.Width + 160 &&
                        footerHeight >= 24 && footerHeight <= 112 &&
                        (best.IsEmpty || candidate.Width * candidate.Height < best.Width * best.Height))
                        best = candidate;
                    element = TreeWalker.ControlViewWalker.GetParent(element);
                }
                return best;
            }
            catch
            {
                return Rectangle.Empty;
            }
        }

        private static bool Contains(Rectangle container, Rectangle content)
        {
            return container.Left <= content.Left && container.Top <= content.Top &&
                container.Right >= content.Right && container.Bottom >= content.Bottom;
        }

        internal static bool LooksLikeConversationComposer(
            Rectangle windowBounds,
            Rectangle candidateBounds)
        {
            if (windowBounds.Width < 1 || windowBounds.Height < 1 ||
                candidateBounds.Width < Math.Max(220, windowBounds.Width * 28 / 100) ||
                candidateBounds.Height < 24)
                return false;

            int lowerHalfTop = windowBounds.Top + windowBounds.Height * 45 / 100;
            return candidateBounds.Top >= lowerHalfTop &&
                candidateBounds.Left >= windowBounds.Left - 8 &&
                candidateBounds.Right <= windowBounds.Right + 8 &&
                candidateBounds.Bottom <= windowBounds.Bottom + 8;
        }
    }
}
