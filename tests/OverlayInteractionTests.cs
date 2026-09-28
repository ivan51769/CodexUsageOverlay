using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal static class OverlayInteractionTests
    {
        public static void MainUsageIsNotInteractive()
        {
            Rectangle usage = OverlayInteraction.GetMainUsageBounds(390, 28);
            Rectangle radar = new Rectangle(396, 5, 104, 18);
            Rectangle gear = new Rectangle(556, 2, 30, 24);
            Point usageCenter = new Point(usage.Left + usage.Width / 2,
                usage.Top + usage.Height / 2);
            Assert(!OverlayInteraction.IsHeaderInteractive(usageCenter, radar, gear),
                "main usage still owns mouse input");
            Assert(OverlayInteraction.IsHeaderInteractive(
                new Point(radar.Left + 2, radar.Top + 2), radar, gear),
                "radar stopped receiving input");
            Assert(OverlayInteraction.IsHeaderInteractive(
                new Point(gear.Left + 2, gear.Top + 2), radar, gear),
                "gear stopped receiving input");
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

            Version current = new Version(GitHubReleaseUpdateService.CurrentVersion);
            string nextVersion = new Version(current.Major, current.Minor, current.Build + 1).ToString();
            GitHubReleaseUpdateSnapshot available =
                GitHubReleaseUpdateService.EvaluateReleaseUrl(
                    "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/v" + nextVersion);
            UpdateMenuState availableState = OverlayInteraction.BuildUpdateMenuState(available);
            Assert(availableState.CanDownload, "trusted update did not enable download");
            Assert(availableState.DownloadUrl == available.ReleaseUrl,
                "trusted update URL was not retained");

            available.ReleaseUrl += "?download=1";
            UpdateMenuState unsafeState = OverlayInteraction.BuildUpdateMenuState(available);
            Assert(!unsafeState.CanDownload && unsafeState.DownloadUrl == String.Empty,
                "unsafe update URL enabled download");
        }

        public static void ConversationComposerDetectionIgnoresSettingsFields()
        {
            Rectangle codexWindow = new Rectangle(100, 80, 1200, 800);
            Rectangle conversationComposer = new Rectangle(230, 650, 940, 110);
            Rectangle settingsField = new Rectangle(760, 190, 310, 34);
            Rectangle narrowLowerField = new Rectangle(1060, 690, 110, 32);

            Assert(CodexConversationSurfaceMonitor.LooksLikeConversationComposer(
                codexWindow, conversationComposer),
                "conversation composer was not detected");
            Assert(!CodexConversationSurfaceMonitor.LooksLikeConversationComposer(
                codexWindow, settingsField),
                "settings field was mistaken for a conversation composer");
            Assert(!CodexConversationSurfaceMonitor.LooksLikeConversationComposer(
                codexWindow, narrowLowerField),
                "narrow lower field was mistaken for a conversation composer");
        }

        public static void BottomOverlayFollowsComposerCenter()
        {
            Rectangle host = new Rectangle(100, 80, 1200, 800);
            Rectangle composer = new Rectangle(220, 650, 960, 110);
            Rectangle initial = OverlayInteraction.GetBottomOverlayBounds(
                host, composer, 720, 18);
            Assert(initial == new Rectangle(340, 760, 720, 18),
                "bottom overlay was not centered below the composer");

            Rectangle resizedHost = new Rectangle(100, 80, 960, 640);
            Rectangle resizedComposer = new Rectangle(200, 530, 760, 90);
            Rectangle resized = OverlayInteraction.GetBottomOverlayBounds(
                resizedHost, resizedComposer, 720, 18);
            Assert(resized == new Rectangle(220, 620, 720, 18),
                "bottom overlay did not follow the resized composer");
        }

        public static void ComposerBelowReservesInputFooter()
        {
            Rectangle host = new Rectangle(100, 80, 1200, 800);
            Rectangle editor = new Rectangle(220, 650, 960, 70);
            Rectangle anchor = OverlayInteraction.GetComposerBelowAnchorBounds(
                host, editor, editor, 48);
            Rectangle overlay = OverlayInteraction.GetBottomOverlayBounds(
                host, anchor, 720, 18);

            Assert(anchor.Bottom == 768,
                "composer below did not reserve the input tool row");
            Assert(overlay == new Rectangle(340, 768, 720, 18),
                "composer below did not attach below the full input surface");

            Rectangle detectedSurface = new Rectangle(200, 630, 1000, 150);
            Rectangle detectedAnchor = OverlayInteraction.GetComposerBelowAnchorBounds(
                host, editor, detectedSurface, 48);
            Assert(detectedAnchor.Bottom == 780,
                "composer below did not prefer the detected full input surface");
        }

        public static void ComposerInsideUsesToolbarSafeZone()
        {
            Rectangle host = new Rectangle(0, 0, 1200, 800);
            Rectangle editor = new Rectangle(80, 650, 840, 70);
            Rectangle inputSurface = new Rectangle(50, 600, 900, 120);
            Rectangle overlay = OverlayInteraction.GetComposerInsideOverlayBounds(
                host, editor, inputSurface, 124, 218, 28);

            Assert(overlay == new Rectangle(174, 722, 558, 28),
                "composer inside overlay did not stay between the access and model controls");

            Rectangle toolbarSurface = new Rectangle(50, 600, 900, 150);
            Rectangle toolbarCentered = OverlayInteraction.GetComposerInsideOverlayBounds(
                host, editor, toolbarSurface, 124, 218, 28);
            Assert(toolbarCentered == new Rectangle(174, 723, 558, 28),
                "composer inside overlay did not use the toolbar center line");
        }

        public static void ComposerInsideKeepsASettingsGearWithoutCoveringUsage()
        {
            Rectangle usage;
            Rectangle gear;
            OverlayInteraction.GetComposerInsideContentBounds(320, 0, 28,
                out usage, out gear);

            Assert(gear == new Rectangle(296, 5, 22, 22),
                "composer inside settings gear moved outside its reserved edge");
            Assert(usage.Right < gear.Left,
                "composer inside usage text can cover the settings gear");
        }

        public static void OneLineCapsulesUseACenteredGroupAndTrueVerticalCenter()
        {
            Assert(OverlayInteraction.GetCenteredGroupLeft(900, 360) == 270,
                "one-line capsule group was not horizontally centered");
            Assert(OverlayInteraction.GetCenteredContentTop(0, 28, 16) == 6,
                "title-bar capsules were not vertically centered");
            Assert(OverlayInteraction.GetCenteredContentTop(80, 18, 16) == 81,
                "bottom capsules were not vertically centered");
        }

        public static void TitleBarWidthKeepsAllUsageFieldsVisible()
        {
            Assert(OverlayInteraction.ResolveOverlayWidth(true, 742, 606, 1904) == 742,
                "title-bar layout narrowed the usage fields to the host window width");
            Assert(OverlayInteraction.ResolveOverlayWidth(false, 742, 606, 1904) == 606,
                "composer layout ignored its host-safe width");
            Assert(OverlayInteraction.ResolveOverlayWidth(true, 2200, 606, 1904) == 1904,
                "title-bar layout exceeded the screen-safe width");
        }

        public static void RefreshAndGearUseSymmetricPairedControls()
        {
            Rectangle refresh;
            Rectangle gear;
            OverlayInteraction.GetPairedControlBounds(398, 0, 28, 22, 2,
                out refresh, out gear);
            Assert(refresh.Size == gear.Size && refresh.Size == new Size(22, 22),
                "refresh and gear controls do not have the same size");
            Assert(refresh.Top == gear.Top && refresh.Right + 2 == gear.Left,
                "refresh and gear controls are not a symmetric pair");
            Assert(refresh.Top == 3 && gear.Top == 3,
                "paired controls are not vertically centered");
        }

        public static void AllUtilityControlsHaveImmediateHitAreas()
        {
            Rectangle refresh = new Rectangle(100, 3, 22, 22);
            Rectangle gear = new Rectangle(124, 3, 22, 22);
            Rectangle analysis = new Rectangle(148, 3, 22, 22);
            Rectangle download = new Rectangle(172, 3, 22, 22);
            Rectangle context = OverlayInteraction.GetContextToggleBounds(download);
            Assert(context == new Rectangle(196, 3, 22, 22),
                "context toggle must sit immediately to the right of download");
            Rectangle enhanced = OverlayInteraction.GetSidebarExpandBounds(context);
            Assert(enhanced == new Rectangle(220, 3, 22, 22),
                "enhanced-mode shortcut must sit immediately to the right of context visibility");
            Assert(OverlayInteraction.IsActionControlHit(new Point(231, 14),
                refresh, gear, analysis, download, context, enhanced),
                "enhanced-mode shortcut did not receive pointer input");
            Assert(!OverlayInteraction.IsActionControlHit(new Point(243, 14),
                refresh, gear, analysis, download, context, enhanced),
                "pointer outside enhanced mode incorrectly hit a control");
            Rectangle strip = new Rectangle(300, 940, 420, 15);
            Rectangle header = new Rectangle(100, 10, 800, 28);
            Assert(OverlayInteraction.ShouldShowContextStrip(true, false, strip, header),
                "enabling context must show the strip regardless of sample freshness");
            Assert(!OverlayInteraction.ShouldShowContextStrip(false, false, strip, header),
                "closing context must keep it hidden");
            Assert(!OverlayInteraction.ShouldShowContextStrip(true, true, strip, header) &&
                !OverlayInteraction.ShouldShowContextStrip(true, false, Rectangle.Empty, header) &&
                !OverlayInteraction.ShouldShowContextStrip(true, false, strip, strip),
                "context strip must still respect panel, composer and collision guards");
            Assert(OverlayInteraction.IsActionControlHit(new Point(207, 14),
                refresh, gear, analysis, download, context), "context toggle must receive pointer input");
            Assert(OverlayInteraction.IsActionControlHit(new Point(111, 14),
                refresh, gear, analysis, download), "refresh button did not receive pointer input");
            Assert(OverlayInteraction.IsActionControlHit(new Point(135, 14),
                refresh, gear, analysis, download), "settings button did not receive pointer input");
            Assert(OverlayInteraction.IsActionControlHit(new Point(159, 14),
                refresh, gear, analysis, download), "analysis button did not receive pointer input");
            Assert(OverlayInteraction.IsActionControlHit(new Point(183, 14),
                refresh, gear, analysis, download), "download button did not receive pointer input");
            Assert(!OverlayInteraction.IsActionControlHit(new Point(195, 14),
                refresh, gear, analysis, download), "outside pointer incorrectly hit a utility control");
        }

        public static void TwoLineCapsulesFitTheirTextInsteadOfTheWholeRail()
        {
            Rectangle rail = new Rectangle(0, 0, 700, 14);
            Rectangle capsule = OverlayInteraction.GetCompactCenteredCapsuleBounds(
                rail, 180, 7, 1);
            Assert(capsule == new Rectangle(253, 1, 194, 12),
                "two-line capsule did not fit its text");
            Assert(capsule.Width < rail.Width && capsule.Left + capsule.Width / 2 ==
                rail.Left + rail.Width / 2,
                "two-line capsule was not compact and centered");
        }

        public static void OverlayFollowsTheHostMoveWithoutWaitingForALayoutPass()
        {
            Rectangle overlay = new Rectangle(340, 760, 720, 18);
            Rectangle moved = OverlayInteraction.OffsetBoundsForHostMove(overlay, 137, -84);
            Assert(moved == new Rectangle(477, 676, 720, 18),
                "overlay did not keep the host window's exact drag offset");
        }

        public static void ExpandedPanelKeepsBottomHeaderInPlace()
        {
            int expandedTop = OverlayInteraction.GetExpandedPanelTopFromHeader(
                720, 18, 378, 80);
            Assert(expandedTop == 360,
                "expanded bottom panel did not keep its header in place");
            int clampedTop = OverlayInteraction.GetExpandedPanelTopFromHeader(
                220, 28, 378, 80);
            Assert(clampedTop == 80,
                "expanded panel was not clamped at the host top");
        }

        public static void ResetRadarBannerFollowsDisplayPosition()
        {
            int aboveTop = OverlayInteraction.GetResetRadarBannerTop(
                180, 18, 48, 5, false);
            int belowTop = OverlayInteraction.GetResetRadarBannerTop(
                180, 18, 48, 5, true);

            Assert(aboveTop == 127,
                "title bar radar banner did not open upward");
            Assert(belowTop == 203,
                "composer radar banner did not open downward");
        }

        public static void ContextNudgeOpensBelowTitleBar()
        {
            Rectangle work = new Rectangle(0, 0, 1920, 1080);
            Rectangle title = new Rectangle(30, 4, 800, 28);
            int titleTop = OverlayInteraction.GetContextNudgeTop(title, work, 126, 1, false);
            Assert(titleTop == title.Bottom + 1, "context nudge did not open below title bar");
            Rectangle composer = new Rectangle(30, 1000, 800, 28);
            int composerTop = OverlayInteraction.GetContextNudgeTop(composer, work, 126, 1, true);
            Assert(composerTop == composer.Top - 127, "composer context nudge escaped screen");

            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
            {
                int bannerWidth = (int)Math.Round(374 * scale);
                int bannerHeight = (int)Math.Round(126 * scale);
                int gap = (int)Math.Round(scale);
                Rectangle scaledTitle = new Rectangle(30, 4,
                    (int)Math.Round(800 * scale), (int)Math.Round(28 * scale));
                Rectangle fitted = OverlayInteraction.GetContextNudgeBounds(
                    scaledTitle, work, bannerWidth, bannerHeight, gap, false);
                int centeredLeft = scaledTitle.Left + (scaledTitle.Width - bannerWidth) / 2;
                Assert(fitted.Width == bannerWidth && fitted.Left == centeredLeft &&
                    fitted.Top == scaledTitle.Bottom + gap && work.Contains(fitted),
                    "context nudge is clipped or off center at " + scale + "x");
            }

            Rectangle nearEdge = OverlayInteraction.GetContextNudgeBounds(
                new Rectangle(1800, 1030, 500, 28), work, 374, 126, 1, false);
            Assert(work.Contains(nearEdge), "context nudge escaped the work area at a screen edge");

            Rectangle composerEdit = new Rectangle(140, 800, 900, 100);
            Rectangle composerSurface = new Rectangle(130, 790, 920, 160);
            Rectangle strip = OverlayInteraction.GetContextStripBounds(composerEdit,
                composerSurface, work, 520, 15);
            Assert(strip.Width == 520 && strip.Height == 15 &&
                strip.Top == composerSurface.Bottom + 1 && work.Contains(strip),
                "compact context strip did not sit directly below the composer");
            Rectangle nearBottomComposer = new Rectangle(140, 932, 900, 44);
            Rectangle nearBottomSurface = new Rectangle(130, 918, 920, 98);
            Rectangle nearBottomStrip = OverlayInteraction.GetContextStripBounds(
                nearBottomComposer, nearBottomSurface,
                new Rectangle(0, 0, 1920, 1032), 520, 15);
            Assert(nearBottomStrip.Top == 1017 && nearBottomStrip.Bottom == 1032,
                "context strip disappeared near the bottom screen edge");
            Rectangle splitPaneStrip = OverlayInteraction.GetContextStripBounds(
                new Rectangle(416, 932, 442, 44), new Rectangle(404, 918, 466, 98),
                new Rectangle(0, 0, 1920, 1032), 520, 15);
            Assert(splitPaneStrip.Width == 442 && splitPaneStrip.Top == 1017,
                "context strip did not fit a narrow split-pane composer");
            Assert(splitPaneStrip.Left + splitPaneStrip.Width / 2 == 404 + 466 / 2,
                "context strip is not centered on the conversation composer");
            int sidebarCanvasWidth = OverlayInteraction.GetSidebarContextCanvasWidth(
                new Rectangle(-8, -8, 1936, 1048), 369, 1f);
            Assert(sidebarCanvasWidth >= 385,
                "sidebar badges were clipped by the old fixed 320px canvas");
            int extendedSidebarCanvas = OverlayInteraction.GetSidebarContextCanvasWidth(
                new Rectangle(-8, -8, 1936, 1048), 369 + 170, 1f);
            Assert(extendedSidebarCanvas >= 555,
                "expanded sidebar badges cannot extend past the sidebar edge");
            Rectangle fallback = OverlayInteraction.GetContextStripBounds(
                nearBottomComposer, nearBottomSurface,
                new Rectangle(0, 0, 1920, 1020), 520, 15);
            Assert(fallback.Bottom == nearBottomSurface.Top - 1,
                "context strip must remain visible without covering the native toolbar");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
