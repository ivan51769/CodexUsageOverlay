using System;
using System.Drawing;
using System.Windows.Automation;

namespace CodexUsageOverlay
{
    internal sealed class CodexConversationSurfaceMonitor
    {
        private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(800);
        private DateTime lastProbeUtc = DateTime.MinValue;
        private IntPtr lastWindow = IntPtr.Zero;
        private Rectangle lastWindowBounds = Rectangle.Empty;
        private Rectangle lastComposerBounds = Rectangle.Empty;
        private Rectangle lastComposerSurfaceBounds = Rectangle.Empty;
        private bool lastResult;

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
            IntPtr windowHandle,
            Rectangle windowBounds,
            out Rectangle composerBounds,
            out Rectangle composerSurfaceBounds)
        {
            DateTime now = DateTime.UtcNow;
            if (windowHandle == lastWindow && windowBounds == lastWindowBounds &&
                now - lastProbeUtc < ProbeInterval)
            {
                composerBounds = lastComposerBounds;
                composerSurfaceBounds = lastComposerSurfaceBounds;
                return lastResult;
            }

            lastWindow = windowHandle;
            lastWindowBounds = windowBounds;
            lastProbeUtc = now;
            lastResult = false;
            lastComposerBounds = Rectangle.Empty;
            lastComposerSurfaceBounds = Rectangle.Empty;
            composerBounds = Rectangle.Empty;
            composerSurfaceBounds = Rectangle.Empty;
            if (windowHandle == IntPtr.Zero || windowBounds.Width <= 0 || windowBounds.Height <= 0)
                return false;

            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                if (root == null)
                    return false;

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
                        if (lastComposerBounds.IsEmpty || candidate.Width > lastComposerBounds.Width ||
                            (candidate.Width == lastComposerBounds.Width &&
                                candidate.Bottom > lastComposerBounds.Bottom))
                        {
                            lastComposerBounds = candidate;
                            composerElement = element;
                        }
                    }
                }

                if (!lastComposerBounds.IsEmpty)
                {
                    lastComposerSurfaceBounds = FindComposerSurfaceBounds(
                        composerElement, lastComposerBounds, windowBounds);
                    if (lastComposerSurfaceBounds.IsEmpty)
                        lastComposerSurfaceBounds = lastComposerBounds;
                    lastResult = true;
                    composerBounds = lastComposerBounds;
                    composerSurfaceBounds = lastComposerSurfaceBounds;
                    return true;
                }
            }
            catch
            {
            }
            return false;
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
