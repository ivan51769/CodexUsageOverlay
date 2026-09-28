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

        internal static int ResolveOverlayWidth(
            bool titleBarCanUseScreenWidth,
            int preferredWidth,
            int hostAvailableWidth,
            int screenAvailableWidth)
        {
            int maximumWidth = titleBarCanUseScreenWidth
                ? screenAvailableWidth
                : hostAvailableWidth;
            return System.Math.Min(System.Math.Max(1, preferredWidth),
                System.Math.Max(1, maximumWidth));
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
            // The Codex permission row uses an optical rather than a purely
            // geometric center. Nudge the overlay down so its text shares the
            // same baseline as “完全访问” and the model controls.
            int baselineNudge = Math.Max(1, Math.Min(3, height / 14));
            top += baselineNudge;
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
            // Match Codex's composer toolbar hit target. The three overlay
            // actions use this same 22px square rather than the former tiny
            // 16px controls.
            int gearSize = Math.Min(22, Math.Max(14, height - 6));
            // The composer text has an optical baseline nudge; keep the three
            // utility controls on that same baseline instead of geometric center.
            int controlTop = headerTop + Math.Max(0, (height - gearSize) / 2) + 2;
            controlTop = Math.Min(headerTop + Math.Max(0, height - gearSize), controlTop);
            gearBounds = new Rectangle(
                Math.Max(0, canvasWidth - gearSize - 2),
                controlTop,
                gearSize,
                gearSize);
            usageBounds = new Rectangle(0, headerTop,
                Math.Max(1, gearBounds.Left - 2), height);
        }

        internal static Rectangle GetAttachedDownloadBounds(Rectangle header, Size panel, Rectangle work, bool above)
        {
            int left = header.Left + (header.Width - panel.Width) / 2;
            int top = above ? header.Top - panel.Height : header.Bottom;
            left = Math.Max(work.Left, Math.Min(left, work.Right - panel.Width));
            top = Math.Max(work.Top, Math.Min(top, work.Bottom - panel.Height));
            return new Rectangle(left, top, panel.Width, panel.Height);
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

        internal static int GetContextNudgeTop(Rectangle overlay, Rectangle workArea,
            int bannerHeight, int gap, bool composerPosition)
        {
            int below = overlay.Bottom + gap;
            int above = overlay.Top - bannerHeight - gap;
            if (composerPosition && above >= workArea.Top) return above;
            if (below + bannerHeight <= workArea.Bottom) return below;
            return above;
        }

        internal static Rectangle GetContextNudgeBounds(Rectangle overlay, Rectangle workArea,
            int bannerWidth, int bannerHeight, int gap, bool composerPosition)
        {
            int width = Math.Min(overlay.Width, Math.Min(bannerWidth, workArea.Width));
            int centeredLeft = overlay.Left + (overlay.Width - width) / 2;
            int left = Math.Max(workArea.Left,
                Math.Min(centeredLeft, workArea.Right - width));
            int top = GetContextNudgeTop(overlay, workArea, bannerHeight, gap,
                composerPosition);
            top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - bannerHeight));
            return new Rectangle(left, top, width, bannerHeight);
        }

        internal static Rectangle GetContextStripBounds(Rectangle composer, Rectangle surface,
            Rectangle workArea, int preferredWidth, int height)
        {
            if (composer.IsEmpty || surface.IsEmpty || height <= 0 ||
                !surface.Contains(composer))
                return Rectangle.Empty;
            int width = Math.Min(preferredWidth, surface.Width - 24);
            if (width < 240) return Rectangle.Empty;
            int top = surface.Bottom + 1;
            if (top + height > workArea.Bottom)
            {
                int footerHeight = surface.Bottom - composer.Bottom;
                top = footerHeight >= height + 30
                    ? surface.Bottom - height - 2
                    : surface.Top - height - 1;
            }
            Rectangle strip = new Rectangle(surface.Left + (surface.Width - width) / 2,
                top, width, height);
            return workArea.Contains(strip) ? strip : Rectangle.Empty;
        }

        internal static Rectangle GetContextStripPlacementBounds(Rectangle composer, Rectangle surface,
            Rectangle workArea, int preferredWidth, float scale, bool bothInside)
        {
            if (composer.IsEmpty || surface.IsEmpty || !surface.Contains(composer)) return Rectangle.Empty;
            int row = (int)Math.Round(20 * scale);
            int height = (int)Math.Round(42 * scale);
            // Symmetric clearance protects the native toolbar at either end of the composer.
            int width = Math.Min(preferredWidth, surface.Width - (int)Math.Ceiling(436 * scale));
            int insideHeight = bothInside ? height : row;
            if (width >= (int)(240 * scale) && surface.Bottom - composer.Bottom >= insideHeight)
            {
                int top = surface.Bottom - insideHeight;
                if (!bothInside && top + height > workArea.Bottom && surface.Bottom - composer.Bottom >= height)
                    top = surface.Bottom - height;
                Rectangle result = new Rectangle(surface.Left + (surface.Width - width) / 2, top, width, height);
                if (workArea.Contains(result)) return result;
            }
            // Narrow panes or shallow footers cannot fit the strip without hiding native controls.
            return GetContextStripBounds(composer, surface, workArea, preferredWidth, height);
        }

        internal static int GetSidebarContextCanvasWidth(Rectangle host, int rightMostRow,
            float scale)
        {
            int minimum = (int)Math.Ceiling(320 * Math.Max(.75f, scale));
            int required = rightMostRow - host.Left + (int)Math.Ceiling(8 * scale);
            return Math.Min(host.Width, Math.Max(minimum, required));
        }

        internal static bool IsHeaderInteractive(
            Point logicalLocation,
            Rectangle resetRadarBounds,
            Rectangle gearBounds)
        {
            return resetRadarBounds.Contains(logicalLocation) ||
                gearBounds.Contains(logicalLocation);
        }

        internal static Rectangle GetContextToggleBounds(Rectangle downloadBounds)
        {
            return downloadBounds.IsEmpty ? Rectangle.Empty : new Rectangle(
                downloadBounds.Right + 2, downloadBounds.Top, downloadBounds.Width, downloadBounds.Height);
        }

        internal static bool ShouldShowContextStrip(bool enabled, bool panelExpanded,
            Rectangle stripBounds, Rectangle overlayBounds)
        {
            return enabled && !panelExpanded && !stripBounds.IsEmpty &&
                !stripBounds.IntersectsWith(overlayBounds);
        }

        internal static Rectangle GetSidebarExpandBounds(Rectangle contextBounds)
        {
            return GetContextToggleBounds(contextBounds);
        }

        internal static bool IsActionControlHit(
            Point logicalLocation,
            Rectangle refreshBounds,
            Rectangle gearBounds,
            Rectangle analysisBounds,
            Rectangle downloadBounds,
            Rectangle contextBounds = default(Rectangle),
            Rectangle sidebarExpandBounds = default(Rectangle))
        {
            return refreshBounds.Contains(logicalLocation) ||
                gearBounds.Contains(logicalLocation) ||
                analysisBounds.Contains(logicalLocation) ||
                downloadBounds.Contains(logicalLocation) || contextBounds.Contains(logicalLocation) ||
                sidebarExpandBounds.Contains(logicalLocation);
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
                result.DownloadUpdateText = "一键更新 v" + update.LatestVersion;
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
