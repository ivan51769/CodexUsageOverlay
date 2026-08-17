using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal enum OverlayMouseAction
    {
        None,
        OpenRunwayPage,
        ShowUpdateMenu
    }

    internal sealed class UpdateMenuState
    {
        public string CurrentVersionText;
        public string CheckUpdateText;
        public bool CanCheck;
        public string DownloadUpdateText;
        public bool CanDownload;
        public string DownloadUrl;
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

        internal static bool IsHeaderInteractive(
            Point logicalLocation,
            Rectangle resetRadarBounds,
            Rectangle gearBounds)
        {
            return resetRadarBounds.Contains(logicalLocation) ||
                gearBounds.Contains(logicalLocation);
        }

        internal static OverlayMouseAction DecideResetRadarClick(
            MouseButtons button,
            Point logicalLocation,
            Rectangle resetRadarBounds)
        {
            if (button != MouseButtons.Left || !resetRadarBounds.Contains(logicalLocation))
                return OverlayMouseAction.None;
            return OverlayMouseAction.OpenRunwayPage;
        }

        internal static OverlayMouseAction DecideGearMouseUp(
            MouseButtons button,
            Point logicalLocation,
            Rectangle gearBounds,
            bool rightDownStartedInGear)
        {
            return button == MouseButtons.Right && rightDownStartedInGear &&
                gearBounds.Contains(logicalLocation)
                ? OverlayMouseAction.ShowUpdateMenu
                : OverlayMouseAction.None;
        }

        internal static UpdateMenuState BuildUpdateMenuState(
            GitHubReleaseUpdateSnapshot update)
        {
            UpdateMenuState result = new UpdateMenuState();
            result.CurrentVersionText = "当前版本 v" + GitHubReleaseUpdateService.CurrentVersion;
            result.CheckUpdateText = update != null && update.IsChecking
                ? "正在检查…"
                : "检查更新";
            result.CanCheck = update == null || !update.IsChecking;

            bool trustedUpdate = update != null && update.UpdateAvailable &&
                GitHubReleaseUpdateService.IsAllowedReleaseUrl(update.ReleaseUrl);
            result.CanDownload = trustedUpdate;
            result.DownloadUrl = trustedUpdate ? update.ReleaseUrl : String.Empty;
            if (trustedUpdate)
                result.DownloadUpdateText = "下载更新 v" + update.LatestVersion;
            else if (update != null && update.IsChecking)
                result.DownloadUpdateText = "下载更新（检查中）";
            else if (update != null && update.LastCheckedUtc.HasValue)
                result.DownloadUpdateText = "下载更新（已是最新版）";
            else
                result.DownloadUpdateText = "下载更新（请先检查）";
            return result;
        }
    }
}
