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
            internal bool RetryRequired;
            internal string Status = "ready";
        }
        private readonly object gate = new object();
        private readonly Func<IntPtr, Rectangle, ProbeResult> probe;
        private DateTime nextProbeUtc = DateTime.MinValue, cachedUtc;
        private IntPtr requestedWindow, cachedWindow;
        private Rectangle requestedBounds, cachedBounds;
        private ProbeResult cached;
        private ProbeResult pendingLayout;
        private long geometryGeneration;
        private bool confirmLayout;
        private bool running, disposed;
        private string probeStatus = "pending";
        private string probePhase = "pending";
        private DateTime probeStartedUtc;
        // Worker-owned element identity only. Its geometry is read afresh on every probe.
        private AutomationElement trackedComposer;
        private IntPtr trackedComposerWindow;

        internal string DiagnosticStatus
        {
            get
            {
                lock (gate)
                {
                    if (running && (cached == null || DateTime.UtcNow - cachedUtc > TimeSpan.FromSeconds(5)))
                        return "scanning-" + probePhase + ":" + (int)(DateTime.UtcNow - probeStartedUtc).TotalSeconds + "s";
                    return probeStatus;
                }
            }
        }

        internal CodexConversationSurfaceMonitor() : this(null) { }
        internal CodexConversationSurfaceMonitor(Func<IntPtr, Rectangle, ProbeResult> probe)
        {
            this.probe = probe ?? Probe;
        }

        internal void NotifyHostGeometryChanged(IntPtr windowHandle, Rectangle windowBounds)
        {
            lock (gate)
                if (!disposed) UpdateRequestedHost(windowHandle, windowBounds);
        }

        private void UpdateRequestedHost(IntPtr windowHandle, Rectangle windowBounds)
        {
            if (requestedWindow == windowHandle && requestedBounds == windowBounds) return;
            bool hadHost = requestedWindow != IntPtr.Zero && !requestedBounds.IsEmpty;
            bool reflow = hadHost && (requestedWindow != windowHandle || requestedBounds.Size != windowBounds.Size);
            geometryGeneration++;
            requestedWindow = windowHandle;
            requestedBounds = windowBounds;
            pendingLayout = null;
            if (reflow)
            {
                cached = null;
                confirmLayout = true;
                probeStatus = "layout-invalidated";
            }
            // Preserve the last verified relative layout for a pure move. Debounce
            // new UIA reads, whose absolute coordinates can straddle a drag event.
            nextProbeUtc = hadHost ? DateTime.UtcNow.AddMilliseconds(120) : DateTime.MinValue;
        }

        private static bool SameLayout(ProbeResult first, ProbeResult second)
        {
            return first != null && second != null &&
                NearBounds(first.Composer, second.Composer) && NearBounds(first.Surface, second.Surface);
        }

        private static bool NearBounds(Rectangle first, Rectangle second)
        {
            return !first.IsEmpty && !second.IsEmpty && Math.Abs(first.Left - second.Left) <= 1 &&
                Math.Abs(first.Top - second.Top) <= 1 && Math.Abs(first.Right - second.Right) <= 1 &&
                Math.Abs(first.Bottom - second.Bottom) <= 1;
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
                UpdateRequestedHost(windowHandle, windowBounds);
                DateTime now = DateTime.UtcNow;
                if (windowHandle == IntPtr.Zero || windowBounds.Width <= 0 || windowBounds.Height <= 0)
                { cached = null; return false; }
                if (!running && now >= nextProbeUtc)
                {
                    long probeGeneration = geometryGeneration;
                    running = true;
                    probePhase = "starting";
                    probeStartedUtc = now;
                    nextProbeUtc = now.AddMilliseconds(800);
                    // UI Automation may block in another process. Never execute it on the UI thread.
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        ProbeResult result = null;
                        string error = null;
                        try { result = probe(windowHandle, windowBounds); }
                        catch (Exception ex) { error = ex.GetType().Name; }
                        lock (gate)
                        {
                            running = false;
                            if (disposed) return;
                            if (probeGeneration != geometryGeneration || requestedWindow != windowHandle || requestedBounds != windowBounds)
                            {
                                nextProbeUtc = DateTime.MinValue;
                                return;
                            }
                            if (confirmLayout && error == null && result != null &&
                                !result.Composer.IsEmpty && !result.Surface.IsEmpty)
                            {
                                if (!SameLayout(pendingLayout, result))
                                {
                                    pendingLayout = result;
                                    cached = null;
                                    probeStatus = "layout-confirming";
                                    nextProbeUtc = DateTime.UtcNow.AddMilliseconds(200);
                                    return;
                                }
                                confirmLayout = false;
                            }
                            pendingLayout = null;
                            cachedWindow = windowHandle;
                            cachedBounds = windowBounds;
                            cached = result;
                            cachedUtc = DateTime.UtcNow;
                            probeStatus = error != null ? "scan-error:" + error :
                                (result == null ? "composer-absent" : result.Status);
                            // Retry an incomplete scan promptly, but never retain stale geometry
                            // across a failed scan: a split-pane change can leave host size unchanged.
                            nextProbeUtc = cachedUtc.AddMilliseconds(error != null ||
                                (result != null && result.RetryRequired) ? 200 : 800);
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
                if (!composerSurfaceBounds.IsEmpty) composerSurfaceBounds.Offset(dx, dy);
                if (!safeFooter.IsEmpty) safeFooter.Offset(dx, dy);
                return true;
            }
        }

        private void SetProbePhase(string phase)
        {
            lock (gate) { probePhase = phase; }
        }

        private ProbeResult Probe(IntPtr windowHandle, Rectangle windowBounds)
        {
            var request = new CacheRequest();
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            if (trackedComposerWindow != windowHandle) trackedComposer = null;
            if (trackedComposer != null)
            {
                SetProbePhase("tracked-editor");
                try
                {
                    // Avoid rescanning the browser and entire transcript on every tick.
                    // This is a fresh provider read, not the previous rectangle snapshot.
                    AutomationElement updated = trackedComposer.GetUpdatedCache(request);
                    Rectangle editor = ReadCachedEditorBounds(updated, windowBounds);
                    if (!editor.IsEmpty)
                    {
                        ProbeResult current = ProbeComposerFrame(updated, editor, windowBounds);
                        if (!current.Surface.IsEmpty)
                        {
                            trackedComposer = updated;
                            return current;
                        }
                    }
                }
                catch { } // A remount invalidates the identity; rediscover below.
                trackedComposer = null;
            }

            SetProbePhase("editor");
            AutomationElement root = AutomationElement.FromHandle(windowHandle);
            if (root == null) throw new ElementNotAvailableException();
            AutomationElementCollection elements;
            using (request.Activate()) elements = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            var candidates = new List<KeyValuePair<AutomationElement, Rectangle>>();
            foreach (AutomationElement element in elements)
            {
                Rectangle editor = ReadCachedEditorBounds(element, windowBounds);
                if (!editor.IsEmpty)
                    candidates.Add(new KeyValuePair<AutomationElement, Rectangle>(element, editor));
            }
            candidates.Sort(delegate(KeyValuePair<AutomationElement, Rectangle> left,
                KeyValuePair<AutomationElement, Rectangle> right)
            {
                int width = right.Value.Width.CompareTo(left.Value.Width);
                return width != 0 ? width : right.Value.Bottom.CompareTo(left.Value.Bottom);
            });
            ProbeResult incomplete = null;
            foreach (var candidate in candidates)
            {
                ProbeResult result = ProbeComposerFrame(candidate.Key, candidate.Value, windowBounds);
                if (!result.Surface.IsEmpty)
                {
                    trackedComposer = candidate.Key;
                    trackedComposerWindow = windowHandle;
                    return result;
                }
                if (incomplete == null) incomplete = result;
            }
            // A wider edit in the browser must not suppress a narrower valid composer.
            return incomplete;
        }

        private static Rectangle ReadCachedEditorBounds(AutomationElement element, Rectangle windowBounds)
        {
            var info = element.Cached;
            if (info.IsOffscreen) return Rectangle.Empty;
            System.Windows.Rect bounds = info.BoundingRectangle;
            if (bounds.IsEmpty) return Rectangle.Empty;
            Rectangle editor = Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
            return LooksLikeConversationComposer(windowBounds, editor) ? editor : Rectangle.Empty;
        }

        private ProbeResult ProbeComposerFrame(AutomationElement composerElement, Rectangle composer,
            Rectangle windowBounds)
        {
            var result = new ProbeResult { Composer = composer };
            Rectangle surface;
            AutomationElement surfaceElement = null;
            try
            {
                SetProbePhase("frame");
                surface = SelectComposerSurfaceBounds(composer, windowBounds,
                    ReadComposerAncestors(composerElement, delegate(AutomationElement element) { surfaceElement = element; }));
            }
            catch (Exception ex)
            {
                result.RetryRequired = true;
                result.Status = "surface-error:" + ex.GetType().Name;
                return result;
            }
            result.Surface = surface;
            if (surface.IsEmpty)
            {
                result.RetryRequired = true;
                result.Status = "surface-unavailable";
                return result;
            }
            try
            {
                SetProbePhase("toolbar");
                var controls = new List<Rectangle>();
                var request = new CacheRequest();
                request.Add(AutomationElement.BoundingRectangleProperty);
                request.Add(AutomationElement.IsOffscreenProperty);
                AutomationElementCollection toolbarElements;
                // Scan only the verified input frame, not chat attachments or browser panes.
                using (request.Activate()) toolbarElements = surfaceElement.FindAll(TreeScope.Descendants, new OrCondition(
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
                result.SafeFooter = OverlayInteraction.GetCenteredToolbarSpace(composer, surface, controls);
            }
            catch (Exception ex)
            {
                // Keep the freshly verified frame, but explicitly block its interior while
                // button bounds are unknown; only the external safe fallback is allowed.
                result.SafeFooter = new Rectangle(surface.Left + surface.Width / 2,
                    composer.Bottom, 1, surface.Bottom - composer.Bottom);
                result.RetryRequired = true;
                result.Status = "toolbar-error:" + ex.GetType().Name;
            }
            return result;
        }

        private static IEnumerable<Rectangle> ReadComposerAncestors(AutomationElement element,
            Action<AutomationElement> visited)
        {
            while (element != null)
            {
                if (!element.Current.IsOffscreen)
                {
                    System.Windows.Rect rawBounds = element.Current.BoundingRectangle;
                    if (!rawBounds.IsEmpty)
                    {
                        visited(element);
                        yield return Rectangle.FromLTRB(
                            (int)Math.Floor(rawBounds.Left), (int)Math.Floor(rawBounds.Top),
                            (int)Math.Ceiling(rawBounds.Right), (int)Math.Ceiling(rawBounds.Bottom));
                    }
                }
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
        }

        internal static Rectangle SelectComposerSurfaceBounds(Rectangle composer, Rectangle window,
            IEnumerable<Rectangle> candidates)
        {
            foreach (Rectangle candidate in candidates)
            {
                int footerHeight = candidate.Bottom - composer.Bottom;
                // Attachments expand the frame above the editor. Its total height must not
                // disqualify it; horizontal padding distinguishes the frame from inner text wrappers.
                if (Contains(window, candidate) && Contains(candidate, composer) &&
                    candidate.Left < composer.Left && candidate.Right > composer.Right &&
                    candidate.Width <= window.Width * 96 / 100 && candidate.Width <= composer.Width + 160 &&
                    footerHeight >= 24 && footerHeight <= 112)
                    // Ancestors arrive inside-out. Stop at the enclosing frame: continuing
                    // to unrelated upper UI nodes can throw and discard a valid current anchor.
                    return candidate;
            }
            return Rectangle.Empty;
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
