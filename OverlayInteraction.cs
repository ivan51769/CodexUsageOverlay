using System.Drawing;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal enum OverlayMouseAction
    {
        None,
        ExitApplication
    }

    internal static class OverlayInteraction
    {
        internal static Rectangle GetMainUsageBounds(int resetRadarLeft, int headerHeight)
        {
            return new Rectangle(
                10,
                0,
                System.Math.Max(40, resetRadarLeft - 14),
                System.Math.Max(1, headerHeight - 2));
        }

        internal static OverlayMouseAction DecideMouseUp(
            MouseButtons button,
            Point logicalLocation,
            Rectangle mainUsageBounds,
            bool rightDownStartedInMainUsage)
        {
            return button == MouseButtons.Right && rightDownStartedInMainUsage &&
                mainUsageBounds.Contains(logicalLocation)
                ? OverlayMouseAction.ExitApplication
                : OverlayMouseAction.None;
        }
    }
}
