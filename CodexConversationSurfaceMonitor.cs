using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Automation;

namespace CodexUsageOverlay
{
    internal sealed class CodexConversationSurfaceMonitor : IDisposable
    {
        internal sealed class ProbeResult
        {
            internal Rectangle Composer, Surface, SafeFooter;
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
            Rectangle safeFooter;
            return TryGetConversationBounds(windowHandle, windowBounds, out composerBounds, out composerSurfaceBounds, out safeFooter);
        }

        internal bool TryGetConversationBounds(
            IntPtr windowHandle, Rectangle windowBounds,
            out Rectangle composerBounds, out Rectangle composerSurfaceBounds, out Rectangle safeFooter)
        {
            lock (gate)
            {
                composerBounds = Rectangle.Empty;
                composerSurfaceBounds = Rectangle.Empty;
                safeFooter = Rectangle.Empty;
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
                safeFooter = cached.SafeFooter;
                int dx = windowBounds.Left - cachedBounds.Left, dy = windowBounds.Top - cachedBounds.Top;
                composerBounds.Offset(dx, dy);
                composerSurfaceBounds.Offset(dx, dy);
                if (!safeFooter.IsEmpty) safeFooter.Offset(dx, dy);
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
                    // An unknown outer border is not the text editor's bottom edge.
                    Rectangle safeFooter = Rectangle.Empty;
                    if (surface.Bottom > composer.Bottom)
                    {
                        var controls = new List<Rectangle>();
                        var request = new CacheRequest();
                        request.Add(AutomationElement.BoundingRectangleProperty);
                        request.Add(AutomationElement.IsOffscreenProperty);
                        AutomationElementCollection toolbarElements;
                        using (request.Activate()) toolbarElements = root.FindAll(TreeScope.Descendants, new OrCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar)));
                        foreach (AutomationElement element in toolbarElements)
                        {
                            var info = element.Cached;
                            if (info.IsOffscreen) continue;
                            System.Windows.Rect raw = info.BoundingRectangle;
                            if (raw.IsEmpty) continue;
                            controls.Add(Rectangle.FromLTRB((int)Math.Floor(raw.Left), (int)Math.Floor(raw.Top),
                                (int)Math.Ceiling(raw.Right), (int)Math.Ceiling(raw.Bottom)));
                        }
                        safeFooter = OverlayInteraction.GetCenteredToolbarSpace(composer, surface, controls);
                    }
                    return new ProbeResult { Composer = composer, Surface = surface, SafeFooter = safeFooter };
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
                var candidates = new List<Rectangle>();
                AutomationElement element = composerElement;
                while (element != null)
                {
                    if (element.Current.IsOffscreen)
                    {
                        element = TreeWalker.RawViewWalker.GetParent(element);
                        continue;
                    }
                    System.Windows.Rect rawBounds = element.Current.BoundingRectangle;
                    Rectangle candidate = Rectangle.FromLTRB(
                        (int)Math.Floor(rawBounds.Left), (int)Math.Floor(rawBounds.Top),
                        (int)Math.Ceiling(rawBounds.Right), (int)Math.Ceiling(rawBounds.Bottom));
                    candidates.Add(candidate);
                    element = TreeWalker.RawViewWalker.GetParent(element);
                }
                return SelectComposerSurfaceBounds(composerBounds, windowBounds, candidates);
            }
            catch
            {
                return Rectangle.Empty;
            }
        }

        internal static Rectangle SelectComposerSurfaceBounds(Rectangle composer, Rectangle window,
            IList<Rectangle> candidates)
        {
            Rectangle best = Rectangle.Empty;
            foreach (Rectangle candidate in candidates)
            {
                int footerHeight = candidate.Bottom - composer.Bottom;
                // Attachments expand the frame above the editor. Its total height must not
                // disqualify it; horizontal padding distinguishes the frame from inner text wrappers.
                if (Contains(window, candidate) && Contains(candidate, composer) &&
                    candidate.Left < composer.Left && candidate.Right > composer.Right &&
                    candidate.Width <= window.Width * 96 / 100 && candidate.Width <= composer.Width + 160 &&
                    footerHeight >= 24 && footerHeight <= 112 &&
                    (best.IsEmpty || candidate.Width * candidate.Height < best.Width * best.Height))
                    best = candidate;
            }
            return best;
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
                candidateBounds.Width < Math.Max(220, windowBounds.Width * 20 / 100) ||
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
