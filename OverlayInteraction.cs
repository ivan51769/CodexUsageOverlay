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

        internal static int GetCenteredGroupLeft(int containerWidth, int groupWidth)
        {
            return System.Math.Max(0, (containerWidth - groupWidth) / 2);
        }

        internal static int GetCenteredContentTop(
            int headerTop,
            int headerHeight,
            int contentHeight)
        {
            return headerTop + System.Math.Max(0,
                (System.Math.Max(1, headerHeight) - System.Math.Max(1, contentHeight)) / 2);
        }

        internal static void GetPairedControlBounds(
            int right,
            int headerTop,
            int headerHeight,
            int size,
            int gap,
            out Rectangle refreshBounds,
            out Rectangle gearBounds)
        {
            int controlSize = System.Math.Max(1, size);
            int controlGap = System.Math.Max(0, gap);
            int top = GetCenteredContentTop(headerTop, headerHeight, controlSize);
            gearBounds = new Rectangle(System.Math.Max(0, right - controlSize), top,
                controlSize, controlSize);
            refreshBounds = new Rectangle(System.Math.Max(0, gearBounds.Left - controlGap - controlSize),
                top, controlSize, controlSize);
        }

        internal static Rectangle GetCompactCenteredCapsuleBounds(
            Rectangle railBounds,
            int textWidth,
            int horizontalPadding,
            int verticalInset)
        {
            int width = System.Math.Min(railBounds.Width, System.Math.Max(1,
                textWidth + System.Math.Max(0, horizontalPadding) * 2));
            int inset = System.Math.Max(0, verticalInset);
            return new Rectangle(railBounds.Left + System.Math.Max(0,
                    (railBounds.Width - width) / 2),
                railBounds.Top + inset,
                width,
                System.Math.Max(1, railBounds.Height - inset * 2));
        }

        internal static Rectangle OffsetBoundsForHostMove(
            Rectangle bounds,
            int horizontalOffset,
            int verticalOffset)
        {
            return new Rectangle(
                bounds.Left + horizontalOffset,
                bounds.Top + verticalOffset,
                bounds.Width,
                bounds.Height);
        }

        internal static Rectangle GetBottomOverlayBounds(
            Rectangle hostBounds,
            Rectangle composerBounds,
            int requestedWidth,
            int overlayHeight)
        {
            int width = System.Math.Max(1, System.Math.Min(requestedWidth, composerBounds.Width));
            int height = System.Math.Max(1, overlayHeight);
            int left = composerBounds.Left + (composerBounds.Width - width) / 2;
            int top = composerBounds.Bottom;
            if (top + height > hostBounds.Bottom)
                top = hostBounds.Bottom - height;
            top = System.Math.Max(hostBounds.Top, top);
            return new Rectangle(left, top, width, height);
        }

        internal static Rectangle GetComposerBelowAnchorBounds(
            Rectangle hostBounds,
            Rectangle composerInputBounds,
            Rectangle composerSurfaceBounds,
            int footerHeight)
        {
            Rectangle surface = composerSurfaceBounds.IsEmpty
                ? composerInputBounds
                : composerSurfaceBounds;
            int fallbackBottom = System.Math.Min(hostBounds.Bottom,
                composerInputBounds.Bottom + System.Math.Max(0, footerHeight));
            int bottom = System.Math.Max(surface.Bottom, fallbackBottom);
            return Rectangle.FromLTRB(surface.Left, surface.Top, surface.Right, bottom);
        }

        internal static Rectangle GetComposerInsideOverlayBounds(
            Rectangle hostBounds,
            Rectangle composerInputBounds,
            Rectangle composerSurfaceBounds,
            int leftReservedWidth,
            int rightReservedWidth,
            int overlayHeight)
        {
            Rectangle surface = composerSurfaceBounds.IsEmpty
                ? composerInputBounds
                : composerSurfaceBounds;
            int leftInset = Math.Min(Math.Max(24, leftReservedWidth),
                Math.Max(24, surface.Width / 3));
            int rightInset = Math.Min(Math.Max(24, rightReservedWidth),
                Math.Max(24, surface.Width / 3));
            if (surface.Width - leftInset - rightInset < 140)
            {
                leftInset = Math.Max(16, surface.Width / 8);
                rightInset = Math.Max(16, surface.Width / 5);
            }

            int left = surface.Left + leftInset;
            int right = Math.Max(left + 1, surface.Right - rightInset);
            int height = Math.Max(1, overlayHeight);
            int toolbarTop = Math.Max(surface.Top, composerInputBounds.Bottom);
            int toolbarHeight = Math.Max(0, surface.Bottom - toolbarTop);
            int top = toolbarHeight >= height
                ? toolbarTop + (toolbarHeight - height) / 2
                : toolbarTop;
            if (top + height > hostBounds.Bottom)
                top = Math.Max(hostBounds.Top, hostBounds.Bottom - height);
            return Rectangle.FromLTRB(left, top, right, top + height);
        }

        internal static void GetComposerInsideContentBounds(
            int canvasWidth,
            int headerTop,
            int headerHeight,
            out Rectangle usageBounds,
            out Rectangle gearBounds)
        {
            int height = Math.Max(1, headerHeight);
            int gearSize = Math.Min(16, Math.Max(12, height - 8));
            gearBounds = new Rectangle(
                Math.Max(0, canvasWidth - gearSize - 2),
                headerTop + Math.Max(0, (height - gearSize) / 2),
                gearSize,
                gearSize);
            usageBounds = new Rectangle(0, headerTop,
                Math.Max(1, gearBounds.Left - 2), height);
        }

        internal static int GetExpandedPanelTopFromHeader(
            int collapsedHeaderTop,
            int collapsedHeight,
            int expandedHeight,
            int hostTop)
        {
            return Math.Max(hostTop,
                collapsedHeaderTop - Math.Max(0, expandedHeight - collapsedHeight));
        }

        internal static int GetResetRadarBannerTop(
            int overlayTop,
            int overlayHeight,
            int bannerHeight,
            int gap,
            bool openDownward)
        {
            return openDownward
                ? overlayTop + overlayHeight + gap
                : overlayTop - bannerHeight - gap;
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
