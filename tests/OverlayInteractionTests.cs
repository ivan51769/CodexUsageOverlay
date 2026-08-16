using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal static class OverlayInteractionTests
    {
        public static void RightClickMainUsageRequestsExit()
        {
            Rectangle bounds = OverlayInteraction.GetMainUsageBounds(500, 28);
            Point center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

            Assert(OverlayInteraction.DecideMouseUp(MouseButtons.Right, center, bounds, true) ==
                OverlayMouseAction.ExitApplication, "right click did not request exit");
        }

        public static void OtherButtonsDoNotRequestExit()
        {
            Rectangle bounds = OverlayInteraction.GetMainUsageBounds(500, 28);
            Point center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

            Assert(OverlayInteraction.DecideMouseUp(MouseButtons.Left, center, bounds, true) ==
                OverlayMouseAction.None, "left click requested exit");
            Assert(OverlayInteraction.DecideMouseUp(MouseButtons.Middle, center, bounds, true) ==
                OverlayMouseAction.None, "middle click requested exit");
        }

        public static void RightClickOutsideMainUsageDoesNotRequestExit()
        {
            Rectangle bounds = OverlayInteraction.GetMainUsageBounds(500, 28);
            Point[] outside =
            {
                new Point(bounds.Left - 1, bounds.Top + 1),
                new Point(bounds.Right, bounds.Top + 1),
                new Point(bounds.Left + 1, bounds.Bottom),
                new Point(bounds.Right + 8, bounds.Top + bounds.Height / 2),
                new Point(bounds.Left + bounds.Width / 2, bounds.Bottom + 8)
            };

            foreach (Point point in outside)
            {
                Assert(OverlayInteraction.DecideMouseUp(MouseButtons.Right, point, bounds, true) ==
                    OverlayMouseAction.None, "outside right click requested exit at " + point);
            }
        }

        public static void RightDragFromOtherRegionDoesNotRequestExit()
        {
            Rectangle bounds = OverlayInteraction.GetMainUsageBounds(500, 28);
            Point center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

            Assert(OverlayInteraction.DecideMouseUp(MouseButtons.Right, center, bounds, false) ==
                OverlayMouseAction.None, "right drag from another region requested exit");
        }

        public static void RadarStatusClickOpensRunway()
        {
            Rectangle bounds = new Rectangle(100, 2, 104, 24);
            Point center = new Point(bounds.Left + bounds.Width / 2,
                bounds.Top + bounds.Height / 2);
            ResetRadarStatus[] statuses =
            {
                ResetRadarStatus.Loading,
                ResetRadarStatus.Offline,
                ResetRadarStatus.NoSignal,
                ResetRadarStatus.ScheduledToday,
                ResetRadarStatus.ScheduledUpcoming,
                ResetRadarStatus.CompletedToday
            };
            foreach (ResetRadarStatus status in statuses)
            {
                Assert(OverlayInteraction.DecideResetRadarClick(
                    MouseButtons.Left, center, bounds) ==
                    OverlayMouseAction.OpenRunwayPage,
                    status + " click did not open Runway");
            }
            Assert(OverlayInteraction.DecideResetRadarClick(
                MouseButtons.Right, center, bounds) ==
                OverlayMouseAction.None, "right click unexpectedly opened Runway");
            Assert(OverlayInteraction.DecideResetRadarClick(
                MouseButtons.Left, new Point(bounds.Right, center.Y), bounds) ==
                OverlayMouseAction.None,
                "outside click unexpectedly opened Runway");
        }

        public static void RightClickGearShowsUpdateMenu()
        {
            Rectangle bounds = new Rectangle(500, 0, 30, 28);
            Point center = new Point(bounds.Left + bounds.Width / 2,
                bounds.Top + bounds.Height / 2);
            Assert(OverlayInteraction.DecideGearMouseUp(
                MouseButtons.Right, center, bounds, true) ==
                OverlayMouseAction.ShowUpdateMenu, "right click gear did not show update menu");
            Assert(OverlayInteraction.DecideGearMouseUp(
                MouseButtons.Left, center, bounds, true) ==
                OverlayMouseAction.None, "left click gear requested update menu");
            Assert(OverlayInteraction.DecideGearMouseUp(
                MouseButtons.Right, center, bounds, false) ==
                OverlayMouseAction.None, "right drag from another region requested update menu");
            Assert(OverlayInteraction.DecideGearMouseUp(
                MouseButtons.Right, new Point(bounds.Right, center.Y), bounds, true) ==
                OverlayMouseAction.None, "right click outside gear requested update menu");
        }

        public static void UpdateMenuReflectsReleaseState()
        {
            UpdateMenuState initial = OverlayInteraction.BuildUpdateMenuState(
                new GitHubReleaseUpdateSnapshot());
            Assert(initial.CurrentVersionText == "当前版本 v" +
                GitHubReleaseUpdateService.CurrentVersion, initial.CurrentVersionText);
            Assert(initial.CanCheck, "initial check action was disabled");
            Assert(!initial.CanDownload && initial.DownloadUrl == String.Empty,
                "initial download action was enabled");

            GitHubReleaseUpdateSnapshot checking = new GitHubReleaseUpdateSnapshot();
            checking.IsChecking = true;
            UpdateMenuState checkingState = OverlayInteraction.BuildUpdateMenuState(checking);
            Assert(!checkingState.CanCheck && checkingState.CheckUpdateText == "正在检查…",
                "checking state was not reflected");
            Assert(!checkingState.CanDownload, "checking state enabled download");

            GitHubReleaseUpdateSnapshot available =
                GitHubReleaseUpdateService.EvaluateReleaseUrl(
                    "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/v1.4.0");
            UpdateMenuState availableState = OverlayInteraction.BuildUpdateMenuState(available);
            Assert(availableState.CanDownload, "trusted update did not enable download");
            Assert(availableState.DownloadUrl == available.ReleaseUrl,
                "trusted update URL was not retained");

            available.ReleaseUrl += "?download=1";
            UpdateMenuState unsafeState = OverlayInteraction.BuildUpdateMenuState(available);
            Assert(!unsafeState.CanDownload && unsafeState.DownloadUrl == String.Empty,
                "unsafe update URL enabled download");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
