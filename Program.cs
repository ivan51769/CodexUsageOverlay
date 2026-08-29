using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool snapshot = Array.IndexOf(args, "--snapshot") >= 0;
            bool radarSnapshot = Array.IndexOf(args, "--reset-radar-snapshot") >= 0;
            bool settingsOnly = Array.IndexOf(args, "--settings") >= 0;
            string previewOutput = null;
            const string previewPrefix = "--export-theme-previews=";
            foreach (string argument in args)
            {
                if (argument.StartsWith(previewPrefix, StringComparison.OrdinalIgnoreCase))
                    previewOutput = argument.Substring(previewPrefix.Length).Trim('"');
            }
            if (snapshot || radarSnapshot)
                NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

            if (radarSnapshot)
            {
                using (ResetRadarService radarService = new ResetRadarService())
                {
                    bool refreshed = radarService.RefreshNow();
                    ResetRadarData radar = radarService.Snapshot();
                    string[] report = new[]
                    {
                        "RadarStatus=" + radar.Status.ToString(),
                        "RadarLabel=" + radar.StatusLabel,
                        "RadarDetail=" + radar.Detail,
                        "RadarScope=" + radar.ScopeLabel,
                        "RadarEventKind=" + radar.EventKind,
                        "RadarPostId=" + radar.EvidencePostId,
                        "RadarSourceUrl=" + radar.SourceUrl,
                        "RadarConfidence=" + (radar.Confidence.HasValue ? radar.Confidence.Value.ToString("0.####", CultureInfo.InvariantCulture) : String.Empty),
                        "RadarNetworkAvailable=" + radar.NetworkAvailable.ToString(CultureInfo.InvariantCulture),
                        "RadarLastError=" + radar.LastError
                    };
                    foreach (string line in report) Console.WriteLine(line);
                    File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "reset-radar-snapshot.txt"), report, new UTF8Encoding(false));
                    return refreshed ? 0 : 1;
                }
            }

            OverlaySettings settings = OverlaySettingsStore.Load();
            using (UsageService service = new UsageService())
            {
                if (!String.IsNullOrWhiteSpace(previewOutput))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (OverlayForm form = new OverlayForm(service, settings))
                        form.ExportThemePreviews(previewOutput);
                    return 0;
                }

                if (settingsOnly)
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (SettingsForm form = new SettingsForm(settings))
                    {
                        if (form.ShowDialog() == DialogResult.OK)
                            OverlaySettingsStore.SavePreservingCompletedOnboarding(
                                form.SelectedSettings);
                    }
                    return 0;
                }

                if (snapshot)
                {
                    IntPtr window = CodexWindow.Find();
                    bool refreshed = service.RefreshNow();
                    UsageData data = service.Snapshot();
                    string[] report = new[]
                    {
                        String.Format(CultureInfo.InvariantCulture, "CodexWindow={0}", window != IntPtr.Zero ? "found" : "not-found"),
                        "DataSource=" + data.Source,
                        "Plan=" + data.Plan,
                        "ShortRemaining=" + (data.ShortRemaining.HasValue ? data.ShortRemaining.Value.ToString(CultureInfo.InvariantCulture) : "unknown"),
                        "ShortReset=" + data.ShortResetText,
                        "WeeklyRemaining=" + (data.WeeklyRemaining.HasValue ? data.WeeklyRemaining.Value.ToString(CultureInfo.InvariantCulture) : "unknown"),
                        "WeeklyReset=" + data.WeeklyResetText,
                        "RateLimitStatus=" + data.RateLimitStatus,
                        "AvailableResetCredits=" + (data.AvailableResetCredits.HasValue ? data.AvailableResetCredits.Value.ToString(CultureInfo.InvariantCulture) : "unknown"),
                        "LifetimeTokens=" + (data.LifetimeTokens.HasValue ? data.LifetimeTokens.Value.ToString(CultureInfo.InvariantCulture) : "unknown"),
                        "ProfileTokensText=" + data.ProfileTokensText,
                        "LastError=" + data.LastError
                    };
                    foreach (string line in report) Console.WriteLine(line);
                    File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "snapshot.txt"), report, new UTF8Encoding(false));
                    return refreshed ? 0 : 1;
                }

                bool created;
                using (Mutex mutex = new Mutex(true, "Local\\CodexUsageOverlay-7E2EBB20", out created))
                {
                    if (!created)
                        return 0;

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (OverlayForm overlay = new OverlayForm(service, settings))
                    {
                        Application.Run(overlay);
                    }
                }
            }
            return 0;
        }
    }

    internal sealed class UsageService : IDisposable
    {
        private readonly object sync = new object();
        private readonly string cachePath;
        private readonly CodexAppServerClient appServer = new CodexAppServerClient();
        private UsageData data;
        private DateTime lastRefreshUtc = DateTime.MinValue;
        private bool refreshRunning;

        public UsageService()
        {
            cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "usage-cache.ini");
            data = CacheStore.Load(cachePath);
        }

        public UsageData Snapshot()
        {
            lock (sync)
                return data.Clone();
        }

        public void RequestRefresh(int refreshSeconds, bool force)
        {
            refreshSeconds = Math.Max(5, Math.Min(3600, refreshSeconds));
            bool shouldStart = false;
            lock (sync)
            {
                if (!refreshRunning && (force || (DateTime.UtcNow - lastRefreshUtc).TotalSeconds >= refreshSeconds))
                {
                    refreshRunning = true;
                    lastRefreshUtc = DateTime.UtcNow;
                    shouldStart = true;
                }
            }
            if (!shouldStart)
                return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try { RefreshNow(); }
                finally
                {
                    lock (sync)
                        refreshRunning = false;
                }
            });
        }

        public bool RefreshNow()
        {
            UsageData current;
            if (appServer.TryReadUsage(out current))
            {
                current.LastError = String.Empty;
                Merge(current);
                return true;
            }
            lock (sync)
            {
                data.LastError = appServer.LastError ?? String.Empty;
            }
            return false;
        }

        private void Merge(UsageData incoming)
        {
            lock (sync)
            {
                bool changed = UsageDataMerger.MergeInto(data, incoming);
                if (changed)
                {
                    data.UpdatedUtc = DateTime.UtcNow;
                    CacheStore.Save(cachePath, data);
                }
            }
        }

        public void Dispose()
        {
            appServer.Dispose();
        }
    }

    internal sealed class OverlayForm : Form
    {
        private readonly UsageService service;
        private readonly ResetRadarService resetRadarService;
        private readonly CodexConversationSurfaceMonitor conversationSurfaceMonitor;
        private readonly System.Windows.Forms.Timer timer;
        private OverlaySettings settings;
        private IntPtr codexWindow = IntPtr.Zero;
        private readonly object hostMoveSync = new object();
        private NativeMethods.WinEventDelegate codexWindowLocationChanged;
        private IntPtr codexWindowLocationHook = IntPtr.Zero;
        private Rectangle lastCodexWindowBounds = Rectangle.Empty;
        private string displayText = "Codex 用量正在载入";
        private string[] displayCapsuleTexts = new string[0];
        private string lastRenderedText = String.Empty;
        private string lastRenderedCapsuleRevision = String.Empty;
        private Rectangle lastRenderedBounds = Rectangle.Empty;
        private bool settingsExpanded;
        private bool gearHovered;
        private bool gearPressed;
        private bool radarHovered;
        private bool radarRefreshHovered;
        private OverlaySettings draftSettings;
        private readonly string[] fontOptions;
        private readonly Image brandLogo;
        private readonly CodexTaskStatusMonitor taskStatusMonitor;
        private readonly NotifyIcon resetNotifyIcon;
        private readonly GitHubReleaseUpdateService releaseUpdateService;
        private readonly NotifyIcon releaseUpdateNotifyIcon;
        private readonly ContextMenuStrip updateMenu;
        private readonly ToolStripMenuItem currentVersionMenuItem;
        private readonly ToolStripMenuItem checkUpdateMenuItem;
        private readonly ToolStripMenuItem downloadUpdateMenuItem;
        private readonly ToolStripMenuItem exitApplicationMenuItem;
        private readonly ResetRadarBannerForm resetRadarBanner;
        private FirstRunGuideForm guideBubble;
        private CodexTaskState taskState = CodexTaskState.Unknown;
        private ResetRadarData resetRadar = new ResetRadarData();
        private string lastRadarRevision = String.Empty;
        private string lastRadarClockRevision = String.Empty;
        private string notificationSourceUrl = String.Empty;
        private string releaseUpdateUrl = String.Empty;
        private string lastReleaseUpdateRevision = String.Empty;
        private bool updateAvailable;
        private string lastPreferredWidthRevision = String.Empty;
        private int preferredOverlayLogicalWidth = 720;
        private DateTime? manualUpdateCheckRequestedUtc;
        private DateTimeOffset? resetRadarDisplayNow;
        private float dpiScale = 1f;
        private bool radarBannerDismissed;
        private string settingsRevision;
        private bool rightDownStartedInGear;
        private bool pendingAutoGuide;
        private bool replayGuideRequested;
        private bool automaticGuideSession;
        private BottomCapsuleLayout bottomCapsuleLayout;

        private const int HeaderHeight = 28;
        private const int BottomCapsuleHeight = 18;
        private const int BottomCapsuleContentHeight = 16;
        private const float BottomCapsuleTextSize = 7.2f;
        private const int ComposerInsideHeight = 28;
        private const float ComposerInsideTextSize = 7.2f;
        private const int ComposerInsideLeftReservedWidth = 124;
        private const int ComposerInsideRightReservedWidth = 218;
        private const int SettingsPanelMaximumWidth = 640;
        private const int ComposerInsideSettingsPanelMaximumWidth = 560;
        private const int ExpandedHeight = 446;
        private const string RunwayPageUrl = "https://www.codexrunway.com/zh.html";

        private sealed class BottomCapsuleLayout
        {
            internal Rectangle UsageBounds;
            internal Rectangle RadarBounds;
            internal Rectangle UpdateBounds;
            internal Rectangle RefreshBounds;
            internal Rectangle GearBounds;
        }

        public OverlayForm(UsageService service, OverlaySettings settings)
        {
            this.service = service;
            this.settings = settings;
            codexWindowLocationChanged = OnCodexWindowLocationChanged;
            pendingAutoGuide = !settings.OnboardingCompleted;
            settingsRevision = OverlaySettingsStore.GetRevision();
            fontOptions = BuildFontOptions(settings.FontName);
            brandLogo = LoadBrandLogo();
            taskStatusMonitor = new CodexTaskStatusMonitor();
            conversationSurfaceMonitor = new CodexConversationSurfaceMonitor();
            resetRadarService = new ResetRadarService();
            resetRadar = resetRadarService.Snapshot();
            resetNotifyIcon = new NotifyIcon();
            resetNotifyIcon.Icon = SystemIcons.Information;
            resetNotifyIcon.Text = "Codex · Tibo 重置雷达";
            resetNotifyIcon.BalloonTipClicked += delegate { OpenExternalUrl(notificationSourceUrl); };
            resetNotifyIcon.DoubleClick += delegate { OpenRadarSource(); };
            releaseUpdateService = new GitHubReleaseUpdateService();
            releaseUpdateNotifyIcon = new NotifyIcon();
            releaseUpdateNotifyIcon.Icon = SystemIcons.Information;
            releaseUpdateNotifyIcon.Text = "Codex Usage Overlay 更新";
            releaseUpdateNotifyIcon.BalloonTipClicked += delegate { OpenReleaseUpdate(); };
            releaseUpdateNotifyIcon.BalloonTipClosed += delegate
            {
                if (String.IsNullOrWhiteSpace(releaseUpdateUrl))
                    releaseUpdateNotifyIcon.Visible = false;
            };
            releaseUpdateNotifyIcon.DoubleClick += delegate { OpenReleaseUpdate(); };
            currentVersionMenuItem = new ToolStripMenuItem(
                "当前版本 v" + GitHubReleaseUpdateService.CurrentVersion);
            currentVersionMenuItem.Enabled = false;
            currentVersionMenuItem.Font = UiRendering.CreateTextFont(
                "Microsoft YaHei UI", 9f, FontStyle.Bold);
            checkUpdateMenuItem = new ToolStripMenuItem("检查更新");
            checkUpdateMenuItem.Font = UiRendering.CreateTextFont(
                "Microsoft YaHei UI", 9f, FontStyle.Bold);
            checkUpdateMenuItem.Click += delegate { CheckForReleaseUpdateNow(); };
            downloadUpdateMenuItem = new ToolStripMenuItem("下载更新");
            downloadUpdateMenuItem.Font = UiRendering.CreateTextFont(
                "Microsoft YaHei UI", 9f, FontStyle.Bold);
            downloadUpdateMenuItem.Click += delegate { DownloadReleaseUpdate(); };
            updateMenu = new OverlayUpdateContextMenu();
            updateMenu.ShowImageMargin = false;
            updateMenu.ShowCheckMargin = false;
            updateMenu.Items.Add(currentVersionMenuItem);
            updateMenu.Items.Add(new ToolStripSeparator());
            updateMenu.Items.Add(checkUpdateMenuItem);
            updateMenu.Items.Add(downloadUpdateMenuItem);
            exitApplicationMenuItem = new ToolStripMenuItem("退出程序");
            exitApplicationMenuItem.Font = UiRendering.CreateTextFont(
                "Microsoft YaHei UI", 9f, FontStyle.Bold);
            exitApplicationMenuItem.Click += delegate { ConfirmExitApplication(); };
            updateMenu.Items.Add(new ToolStripSeparator());
            updateMenu.Items.Add(exitApplicationMenuItem);
            resetRadarBanner = new ResetRadarBannerForm(OpenRunwayPage, DismissRadarBanner);
            ApplyNotificationVisibility();
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Width = 520;
            Height = 30;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 250;
            timer.Tick += OnTick;
            timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopTrackingCodexWindowMoves();
                timer.Dispose();
                taskStatusMonitor.Dispose();
                resetRadarService.Dispose();
                releaseUpdateService.Dispose();
                if (guideBubble != null)
                {
                    guideBubble.Dispose();
                    guideBubble = null;
                }
                resetRadarBanner.Dispose();
                resetNotifyIcon.Visible = false;
                resetNotifyIcon.Dispose();
                releaseUpdateNotifyIcon.Visible = false;
                releaseUpdateNotifyIcon.Dispose();
                updateMenu.Dispose();
                if (brandLogo != null)
                    brandLogo.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE |
                    NativeMethods.WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
        }

        private void OnTick(object sender, EventArgs e)
        {
            ReloadSettingsIfChanged();
            CheckForReleaseUpdate();
            GitHubReleaseUpdateSnapshot updateSnapshot = releaseUpdateService.Snapshot();
            bool nextUpdateAvailable = updateSnapshot.UpdateAvailable &&
                GitHubReleaseUpdateService.IsAllowedReleaseUrl(updateSnapshot.ReleaseUrl);
            bool updateAvailabilityChanged = nextUpdateAvailable != updateAvailable;
            if (updateAvailabilityChanged)
            {
                updateAvailable = nextUpdateAvailable;
                lastRenderedBounds = Rectangle.Empty;
            }
            resetRadarService.RequestRefresh(false);
            ResetRadarData latestRadar = resetRadarService.Snapshot();
            bool radarChanged = !String.Equals(latestRadar.RevisionKey, lastRadarRevision, StringComparison.Ordinal);
            if (radarChanged)
            {
                resetRadar = latestRadar;
                lastRadarRevision = latestRadar.RevisionKey;
            }
            if (settings.ResetNotificationsEnabled)
            {
                ResetRadarNotification notification;
                if (resetRadarService.TryCreateNotification(out notification))
                    ShowResetNotification(notification);
            }

            if (codexWindow == IntPtr.Zero || !NativeMethods.IsWindow(codexWindow))
                TrackCodexWindow(CodexWindow.Find());

            IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
            bool guideHasFocus = GuideSessionActive && guideBubble.Visible &&
                foregroundWindow == guideBubble.Handle;
            if (codexWindow == IntPtr.Zero || NativeMethods.IsIconic(codexWindow) ||
                !NativeMethods.IsWindowVisible(codexWindow) ||
                (!settingsExpanded && foregroundWindow != codexWindow && !guideHasFocus))
            {
                resetRadarBanner.HideBanner();
                HideGuideBubble();
                Hide();
                return;
            }

            NativeMethods.RECT hostRect;
            if (!NativeMethods.GetWindowRect(codexWindow, out hostRect))
            {
                resetRadarBanner.HideBanner();
                HideGuideBubble();
                Hide();
                return;
            }
            UpdateTrackedCodexWindowBounds(hostRect);

            NativeMethods.RECT rect = hostRect;
            NativeMethods.RECT visibleRect;
            if (NativeMethods.TryGetVisibleWindowRect(codexWindow, out visibleRect))
                rect = visibleRect;

            int windowWidth = rect.Right - rect.Left;
            float newDpiScale = NativeMethods.GetWindowDpiScale(codexWindow);
            bool dpiChanged = Math.Abs(newDpiScale - dpiScale) > 0.01f;
            dpiScale = newDpiScale;
            UsageData usage = service.Snapshot();
            OverlaySettings displaySettings = settingsExpanded && draftSettings != null
                ? draftSettings
                : settings;
            Rectangle windowBounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            Rectangle composerBounds = Rectangle.Empty;
            Rectangle composerSurfaceBounds = Rectangle.Empty;
            bool composerPosition = OverlayDisplayPositions.IsComposerPosition(
                displaySettings.DisplayPosition);
            if (composerPosition && !conversationSurfaceMonitor.TryGetConversationBounds(
                codexWindow, windowBounds, out composerBounds, out composerSurfaceBounds))
            {
                resetRadarBanner.HideBanner();
                HideGuideBubble();
                Hide();
                return;
            }
            Rectangle composerAnchorBounds = composerBounds;
            if (displaySettings.DisplayPosition == OverlayDisplayPosition.ComposerBelow)
            {
                // The editor element ends above the permission/tool row on some Codex builds.
                // Reserve that row when UI Automation cannot expose the enclosing input surface.
                composerAnchorBounds = OverlayInteraction.GetComposerBelowAnchorBounds(
                    windowBounds,
                    composerBounds,
                    composerSurfaceBounds,
                    ScalePixels(48));
            }
            int logicalHeaderHeight = GetCollapsedHeaderHeight(displaySettings);
            Rectangle composerInsideCollapsedBounds = Rectangle.Empty;
            bool expandingInsideComposer = settingsExpanded &&
                displaySettings.DisplayPosition == OverlayDisplayPosition.ComposerInside;
            if (expandingInsideComposer)
            {
                composerInsideCollapsedBounds = OverlayInteraction.GetComposerInsideOverlayBounds(
                    windowBounds,
                    composerBounds,
                    composerSurfaceBounds,
                    ScalePixels(ComposerInsideLeftReservedWidth),
                    ScalePixels(ComposerInsideRightReservedWidth),
                    ScalePixels(logicalHeaderHeight));
            }
            int availableWidth = expandingInsideComposer
                ? Math.Max(1, composerInsideCollapsedBounds.Width)
                : (composerPosition
                    ? Math.Max(1, composerAnchorBounds.Width)
                    : Math.Max(ScalePixels(240), windowWidth - ScalePixels(32)));
            int preferredOverlayWidth = settingsExpanded
                ? GetPreferredSettingsLogicalWidth(availableWidth, expandingInsideComposer)
                : GetPreferredOverlayLogicalWidth(usage);
            Screen targetScreen = Screen.FromHandle(codexWindow);
            bool titleBarCanUseScreenWidth = !settingsExpanded &&
                displaySettings.DisplayPosition == OverlayDisplayPosition.TitleBar;
            int screenAvailableWidth = Math.Max(1, targetScreen.WorkingArea.Width -
                ScalePixels(16));
            int overlayWidth = OverlayInteraction.ResolveOverlayWidth(
                titleBarCanUseScreenWidth,
                ScalePixels(preferredOverlayWidth),
                availableWidth,
                screenAvailableWidth);
            int overlayLeft = rect.Left + (windowWidth - overlayWidth) / 2;
            if (titleBarCanUseScreenWidth)
            {
                int safeScreenLeft = targetScreen.WorkingArea.Left + ScalePixels(8);
                int safeScreenRight = targetScreen.WorkingArea.Right - ScalePixels(8);
                overlayLeft = Math.Max(safeScreenLeft, Math.Min(overlayLeft,
                    safeScreenRight - overlayWidth));
            }
            int titleBarHeight = ScalePixels(36);
            int overlayHeight = ScalePixels(settingsExpanded ? ExpandedHeight : logicalHeaderHeight);
            int visibleTitleBarTop = Math.Max(rect.Top, targetScreen.Bounds.Top);
            int overlayTop = visibleTitleBarTop + (titleBarHeight - ScalePixels(HeaderHeight)) / 2;
            if (composerPosition)
            {
                if (displaySettings.DisplayPosition == OverlayDisplayPosition.ComposerInside)
                {
                    Rectangle insideOverlayBounds = settingsExpanded
                        ? composerInsideCollapsedBounds
                        : OverlayInteraction.GetComposerInsideOverlayBounds(
                            windowBounds,
                            composerBounds,
                            composerSurfaceBounds,
                            ScalePixels(ComposerInsideLeftReservedWidth),
                            ScalePixels(ComposerInsideRightReservedWidth),
                            overlayHeight);
                    overlayWidth = Math.Min(overlayWidth, insideOverlayBounds.Width);
                    overlayLeft = insideOverlayBounds.Left +
                        Math.Max(0, (insideOverlayBounds.Width - overlayWidth) / 2);
                    overlayTop = insideOverlayBounds.Top;
                }
                else
                {
                    Rectangle bottomOverlayBounds = OverlayInteraction.GetBottomOverlayBounds(
                        windowBounds, composerAnchorBounds, overlayWidth, overlayHeight);
                    overlayLeft = bottomOverlayBounds.Left;
                    overlayTop = bottomOverlayBounds.Top;
                    overlayWidth = bottomOverlayBounds.Width;
                }

                if (settingsExpanded)
                {
                    int collapsedHeight = ScalePixels(GetCollapsedHeaderHeight(displaySettings));
                    Rectangle collapsedBounds = displaySettings.DisplayPosition ==
                        OverlayDisplayPosition.ComposerInside
                        ? composerInsideCollapsedBounds
                        : OverlayInteraction.GetBottomOverlayBounds(
                            windowBounds, composerAnchorBounds, overlayWidth, collapsedHeight);
                    overlayTop = OverlayInteraction.GetExpandedPanelTopFromHeader(
                        collapsedBounds.Top,
                        collapsedHeight,
                        overlayHeight,
                        windowBounds.Top);
                }
            }
            bool showRadarBanner = !settingsExpanded && !radarBannerDismissed &&
                ResetRadarBannerForm.ShouldShow(resetRadar);
            int radarBannerHeight = ScalePixels(ResetRadarBannerForm.LogicalHeight);
            int radarBannerGap = ScalePixels(ResetRadarBannerForm.LogicalGap);
            int radarBannerWidth = Math.Min(overlayWidth, ScalePixels(ResetRadarBannerForm.LogicalWidth));
            int radarBannerLeft = overlayLeft + (overlayWidth - radarBannerWidth) / 2;
            int radarBannerTop = OverlayInteraction.GetResetRadarBannerTop(
                overlayTop,
                overlayHeight,
                radarBannerHeight,
                radarBannerGap,
                composerPosition);
            Rectangle desiredBounds = new Rectangle(overlayLeft, overlayTop, overlayWidth, overlayHeight);
            bool boundsChanged = desiredBounds != lastRenderedBounds;
            if (boundsChanged)
            {
                SetBounds(desiredBounds.X, desiredBounds.Y, desiredBounds.Width, desiredBounds.Height, BoundsSpecified.All);
                lastRenderedBounds = desiredBounds;
            }

            bool becameVisible = !Visible;
            if (!Visible)
            {
                Show();
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
            }
            Rectangle guideAnchorBounds = settingsExpanded
                ? desiredBounds
                : new Rectangle(
                overlayLeft, overlayTop, overlayWidth, ScalePixels(logicalHeaderHeight));
            UpdateGuideBubble(guideAnchorBounds, targetScreen.WorkingArea);
            showRadarBanner = showRadarBanner && !GuideSessionActive;
            if (showRadarBanner)
            {
                Rectangle bannerBounds = new Rectangle(
                    radarBannerLeft,
                    radarBannerTop,
                    radarBannerWidth,
                    radarBannerHeight);
                resetRadarBanner.UpdateBanner(resetRadar, settings, bannerBounds, dpiScale);
            }
            else
            {
                resetRadarBanner.HideBanner();
            }

            service.RequestRefresh(settings.RefreshSeconds, false);

            CodexTaskState newTaskState = taskStatusMonitor.Snapshot();
            bool taskStateChanged = newTaskState != taskState;
            taskState = newTaskState;
            int textWidth = Math.Max(40, ResetRadarBounds.Left - 14);
            displayText = UsageDisplayText.Build(usage, textWidth);
            displayCapsuleTexts = displaySettings.DisplayPosition == OverlayDisplayPosition.ComposerInside
                ? UsageDisplayText.BuildComposerInsideCapsuleSections(usage)
                : UsageDisplayText.BuildCapsuleSections(usage);
            string capsuleRevision = String.Join("\n", displayCapsuleTexts);
            bool scheduledRadar = resetRadar.Status == ResetRadarStatus.ScheduledToday ||
                resetRadar.Status == ResetRadarStatus.ScheduledUpcoming;
            string radarClockRevision = scheduledRadar && resetRadar.EffectiveAt.HasValue
                ? DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                : String.Empty;
            bool radarClockChanged = !String.Equals(
                radarClockRevision,
                lastRadarClockRevision,
                StringComparison.Ordinal);
            if (becameVisible || boundsChanged || dpiChanged || taskStateChanged || radarChanged ||
                updateAvailabilityChanged ||
                radarClockChanged || !String.Equals(displayText, lastRenderedText, StringComparison.Ordinal) ||
                !String.Equals(capsuleRevision, lastRenderedCapsuleRevision, StringComparison.Ordinal))
            {
                RenderLayered();
                lastRenderedText = displayText;
                lastRenderedCapsuleRevision = capsuleRevision;
                lastRadarClockRevision = radarClockRevision;
            }
        }

        private void TrackCodexWindow(IntPtr nextWindow)
        {
            if (nextWindow == codexWindow && codexWindowLocationHook != IntPtr.Zero)
                return;

            StopTrackingCodexWindowMoves();
            codexWindow = nextWindow;
            if (codexWindow == IntPtr.Zero || !NativeMethods.IsWindow(codexWindow))
                return;

            NativeMethods.RECT rect;
            if (NativeMethods.GetWindowRect(codexWindow, out rect))
                UpdateTrackedCodexWindowBounds(rect);

            uint processId;
            NativeMethods.GetWindowThreadProcessId(codexWindow, out processId);
            if (processId == 0)
                return;

            codexWindowLocationHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
                NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero,
                codexWindowLocationChanged,
                processId,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);
        }

        private void StopTrackingCodexWindowMoves()
        {
            if (codexWindowLocationHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(codexWindowLocationHook);
                codexWindowLocationHook = IntPtr.Zero;
            }
            lock (hostMoveSync)
                lastCodexWindowBounds = Rectangle.Empty;
        }

        private void UpdateTrackedCodexWindowBounds(NativeMethods.RECT rect)
        {
            lock (hostMoveSync)
            {
                lastCodexWindowBounds = Rectangle.FromLTRB(
                    rect.Left, rect.Top, rect.Right, rect.Bottom);
            }
        }

        private void OnCodexWindowLocationChanged(
            IntPtr hook,
            uint eventType,
            IntPtr window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (IsDisposed || eventType != NativeMethods.EVENT_OBJECT_LOCATIONCHANGE ||
                window != codexWindow || objectId != NativeMethods.OBJID_WINDOW || childId != 0)
                return;

            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(window, out rect))
                return;

            Rectangle currentBounds = Rectangle.FromLTRB(
                rect.Left, rect.Top, rect.Right, rect.Bottom);
            Rectangle movedOverlayBounds = Rectangle.Empty;
            int horizontalOffset = 0;
            int verticalOffset = 0;
            lock (hostMoveSync)
            {
                if (lastCodexWindowBounds.IsEmpty)
                {
                    lastCodexWindowBounds = currentBounds;
                    return;
                }

                horizontalOffset = currentBounds.Left - lastCodexWindowBounds.Left;
                verticalOffset = currentBounds.Top - lastCodexWindowBounds.Top;
                lastCodexWindowBounds = currentBounds;
                if ((horizontalOffset == 0 && verticalOffset == 0) ||
                    lastRenderedBounds.IsEmpty)
                    return;

                movedOverlayBounds = OverlayInteraction.OffsetBoundsForHostMove(
                    lastRenderedBounds, horizontalOffset, verticalOffset);
                lastRenderedBounds = movedOverlayBounds;
            }

            // This is intentionally a native move rather than a timer/layout pass. During
            // an interactive drag Windows raises this event for each position change, so the
            // layered overlay moves with the Codex window without leaving a trailing image.
            NativeMethods.MoveWindowWithoutActivation(Handle, movedOverlayBounds);
            Rectangle movedBannerBounds = resetRadarBanner.OffsetForHostMove(
                horizontalOffset, verticalOffset);
            if (!movedBannerBounds.IsEmpty)
                NativeMethods.MoveWindowWithoutActivation(
                    resetRadarBanner.Handle, movedBannerBounds);
            FirstRunGuideForm guide = guideBubble;
            if (guide != null && !guide.IsDisposed)
            {
                Rectangle movedGuideBounds = guide.OffsetForHostMove(
                    horizontalOffset, verticalOffset);
                if (!movedGuideBounds.IsEmpty)
                    NativeMethods.MoveWindowWithoutActivation(
                        guide.Handle, movedGuideBounds);
            }
        }

        private int GetPreferredOverlayLogicalWidth(UsageData usage)
        {
            const int defaultWidth = 720;
            OverlaySettings visualSettings = settingsExpanded && draftSettings != null
                ? draftSettings
                : settings;
            int maximumWidth = visualSettings.DisplayPosition == OverlayDisplayPosition.TitleBar
                ? 1120
                : 920;
            int chromeWidth = 218;
            if (visualSettings.DisplayPosition == OverlayDisplayPosition.TitleBar)
                chromeWidth += Math.Max(0,
                    GetResetRadarPillWidth(visualSettings, false) - 104);
            string detailedText = UsageDisplayText.Build(usage, Int32.MaxValue);
            string revision = visualSettings.FontName + "\n" +
                visualSettings.DisplayPosition.ToString() + "\n" +
                visualSettings.BottomCapsuleStyle.ToString() + "\n" +
                visualSettings.TitleBarFontSize.ToString("0.0", CultureInfo.InvariantCulture) + "\n" +
                visualSettings.ComposerInsideFontSize.ToString("0.0", CultureInfo.InvariantCulture) + "\n" +
                visualSettings.ComposerBelowFontSize.ToString("0.0", CultureInfo.InvariantCulture) + "\n" +
                detailedText;
            if (String.Equals(revision, lastPreferredWidthRevision, StringComparison.Ordinal))
                return preferredOverlayLogicalWidth;

            using (Bitmap canvas = UiRendering.CreateLayeredBitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(canvas))
            using (Font font = OverlayDisplayPositions.IsComposerPosition(visualSettings.DisplayPosition)
                ? CreateDisplayFont(visualSettings, BottomCapsuleTextSize)
                : CreateDisplayFont(visualSettings))
            using (StringFormat format = UiRendering.CreateTextFormat())
            {
                format.FormatFlags |= StringFormatFlags.NoWrap;
                float detailedWidth = 0f;
                string[] detailedLines = detailedText.Replace("\r\n", "\n").Split('\n');
                foreach (string line in detailedLines)
                    detailedWidth = Math.Max(detailedWidth,
                        graphics.MeasureString(line, font, Int32.MaxValue, format).Width);
                if (OverlayDisplayPositions.IsComposerPosition(visualSettings.DisplayPosition))
                {
                    string[] capsules = UsageDisplayText.BuildCapsuleSections(usage);
                    float capsuleWidth = 0f;
                    if (visualSettings.BottomCapsuleStyle == BottomCapsuleStyle.TextOnly)
                    {
                        capsuleWidth = graphics.MeasureString(String.Join(" | ", capsules),
                            font, Int32.MaxValue, format).Width;
                    }
                    else
                    {
                        for (int index = 0; index < capsules.Length; index++)
                        {
                            capsuleWidth += graphics.MeasureString(
                                capsules[index], font, Int32.MaxValue, format).Width + 10f;
                            if (index > 0)
                                capsuleWidth += 2f;
                        }
                    }
                    detailedWidth = Math.Max(detailedWidth, capsuleWidth);
                }
                int requiredWidth = (int)Math.Ceiling(detailedWidth) + chromeWidth;
                preferredOverlayLogicalWidth = Math.Max(defaultWidth,
                    Math.Min(maximumWidth, requiredWidth));
                lastPreferredWidthRevision = revision;
                return preferredOverlayLogicalWidth;
            }
        }

        private int GetPreferredSettingsLogicalWidth(
            int availablePhysicalWidth,
            bool compactForComposerInside)
        {
            // Keep settings readable without allowing a wide Codex composer to enlarge the panel.
            int availableLogicalWidth = Math.Max(1, UnscalePixels(availablePhysicalWidth));
            int maximumWidth = compactForComposerInside
                ? ComposerInsideSettingsPanelMaximumWidth
                : SettingsPanelMaximumWidth;
            return Math.Min(maximumWidth, Math.Max(420, availableLogicalWidth));
        }

        private int ScalePixels(int logicalPixels)
        {
            return Math.Max(1, (int)Math.Round(logicalPixels * dpiScale));
        }

        private int UnscalePixels(int physicalPixels)
        {
            return (int)Math.Round(physicalPixels / Math.Max(0.5f, dpiScale));
        }

        private int CanvasWidth { get { return UnscalePixels(Width); } }
        private int CanvasHeight { get { return UnscalePixels(Height); } }

        private bool ShowUpdateIndicator
        {
            get { return !IsComposerInsidePosition && updateAvailable && CanvasWidth >= 420; }
        }

        private bool IsComposerInsidePosition
        {
            get
            {
                OverlaySettings visualSettings = settingsExpanded && draftSettings != null
                    ? draftSettings
                    : settings;
                return visualSettings.DisplayPosition == OverlayDisplayPosition.ComposerInside;
            }
        }

        private bool IsBottomCapsulePosition
        {
            get
            {
                OverlaySettings visualSettings = settingsExpanded && draftSettings != null
                    ? draftSettings
                    : settings;
                return OverlayDisplayPositions.IsComposerPosition(visualSettings.DisplayPosition);
            }
        }

        private bool UsesEmbeddedRadar
        {
            get
            {
                OverlaySettings visualSettings = settingsExpanded && draftSettings != null
                    ? draftSettings
                    : settings;
                return visualSettings.ComposerInsideLayout == ComposerInsideLayout.TwoLines;
            }
        }

        private bool IsBottomCapsuleSettingsExpanded
        {
            get { return settingsExpanded && IsBottomCapsulePosition; }
        }

        private int HeaderTop
        {
            get
            {
                return IsBottomCapsuleSettingsExpanded
                    ? Math.Max(0, CanvasHeight - ActiveHeaderHeight)
                    : 0;
            }
        }

        private int ActiveHeaderHeight
        {
            get
            {
                OverlaySettings visualSettings = settingsExpanded && draftSettings != null
                    ? draftSettings
                    : settings;
                if (IsComposerInsidePosition)
                    return ComposerInsideHeight;
                return IsBottomCapsulePosition && !UsesTwoLineLayout(visualSettings)
                    ? BottomCapsuleHeight
                    : HeaderHeight;
            }
        }

        private int InlineSettingsOffset
        {
            get { return IsBottomCapsuleSettingsExpanded ? -36 : 0; }
        }

        private bool HideTaskStatus
        {
            get { return true; }
        }

        private static bool UsesTwoLineLayout(OverlaySettings visualSettings)
        {
            return visualSettings != null &&
                visualSettings.ComposerInsideLayout == ComposerInsideLayout.TwoLines;
        }

        private static int GetCollapsedHeaderHeight(OverlaySettings visualSettings)
        {
            if (visualSettings == null ||
                visualSettings.DisplayPosition == OverlayDisplayPosition.ComposerInside)
                return ComposerInsideHeight;
            return OverlayDisplayPositions.IsComposerPosition(visualSettings.DisplayPosition) &&
                !UsesTwoLineLayout(visualSettings)
                ? BottomCapsuleHeight
                : HeaderHeight;
        }

        private void RenderLayered()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0)
                return;

            using (Bitmap bitmap = BuildRenderedBitmap())
                NativeMethods.UpdateLayeredBitmap(Handle, bitmap, Left, Top);
        }

        private Bitmap BuildRenderedBitmap()
        {
            Bitmap bitmap = UiRendering.CreateLayeredBitmap(Width, Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform(dpiScale, dpiScale);

                int canvasWidth = CanvasWidth;
                int canvasHeight = CanvasHeight;
                Rectangle pill = new Rectangle(1, 1, canvasWidth - 3, canvasHeight - 3);
                Color shadowColor = Color.FromArgb(34, 0, 139, 255);
                Color borderColor = Color.FromArgb(105, 48, 180, 255);
                Color textColor = Color.FromArgb(255, 132, 219, 255);
                Color glowColor = Color.FromArgb(38, 0, 154, 255);
                Brush background;
                OverlaySettings visualSettings = settingsExpanded && draftSettings != null ? draftSettings : settings;
                bool rainbowText = visualSettings.Theme == "RainbowText";
                bool bottomCapsulePosition = OverlayDisplayPositions.IsComposerPosition(
                    visualSettings.DisplayPosition);
                bool capsuleLayoutPosition = bottomCapsulePosition ||
                    visualSettings.DisplayPosition == OverlayDisplayPosition.TitleBar;
                bool capsuleLayoutCollapsed = capsuleLayoutPosition && !settingsExpanded;
                bottomCapsuleLayout = capsuleLayoutPosition
                    ? BuildBottomCapsuleLayout(graphics, visualSettings)
                    : null;

                if (visualSettings.Theme == "FrostedGlass")
                {
                    shadowColor = Color.FromArgb(24, 80, 105, 130);
                    borderColor = Color.FromArgb(150, 255, 255, 255);
                    textColor = Color.FromArgb(255, 28, 55, 78);
                    glowColor = Color.FromArgb(18, 255, 255, 255);
                    background = new LinearGradientBrush(pill,
                        Color.FromArgb(205, 242, 248, 252), Color.FromArgb(155, 170, 196, 216),
                        LinearGradientMode.Vertical);
                }
                else if (visualSettings.Theme == "LightCard")
                {
                    shadowColor = Color.FromArgb(26, 69, 79, 94);
                    borderColor = Color.FromArgb(185, 211, 218, 228);
                    textColor = Color.FromArgb(255, 65, 73, 84);
                    glowColor = Color.FromArgb(14, 117, 128, 145);
                    background = new SolidBrush(Color.FromArgb(248, 252, 253, 255));
                }
                else if (visualSettings.Theme == "OrangeGradient")
                {
                    shadowColor = Color.FromArgb(42, 255, 96, 20);
                    borderColor = Color.FromArgb(155, 255, 213, 135);
                    textColor = Color.FromArgb(255, 255, 250, 235);
                    glowColor = Color.FromArgb(34, 255, 177, 70);
                    background = new LinearGradientBrush(pill,
                        Color.FromArgb(222, 255, 194, 112), Color.FromArgb(222, 255, 119, 132),
                        LinearGradientMode.Horizontal);
                }
                else if (visualSettings.Theme == "PinkGradient")
                {
                    shadowColor = Color.FromArgb(42, 255, 73, 169);
                    borderColor = Color.FromArgb(170, 255, 190, 230);
                    textColor = Color.FromArgb(255, 255, 248, 253);
                    glowColor = Color.FromArgb(42, 255, 91, 181);
                    background = new LinearGradientBrush(pill,
                        Color.FromArgb(238, 255, 119, 187), Color.FromArgb(238, 190, 86, 210),
                        LinearGradientMode.Horizontal);
                }
                else if (visualSettings.Theme == "Custom")
                {
                    Color custom = Color.FromArgb(visualSettings.CustomBackgroundArgb);
                    shadowColor = Color.FromArgb(32, custom.R, custom.G, custom.B);
                    borderColor = Color.FromArgb(135, 255, 255, 255);
                    textColor = Color.White;
                    glowColor = Color.FromArgb(24, 255, 255, 255);
                    background = new SolidBrush(Color.FromArgb(205, custom.R, custom.G, custom.B));
                }
                else if (rainbowText)
                {
                    shadowColor = Color.Transparent;
                    borderColor = Color.Transparent;
                    textColor = Color.FromArgb(255, 25, 105, 145);
                    glowColor = Color.FromArgb(82, 255, 255, 255);
                    background = null;
                }
                else
                {
                    background = new LinearGradientBrush(pill,
                        Color.FromArgb(218, 8, 31, 51), Color.FromArgb(206, 10, 61, 87),
                        LinearGradientMode.Horizontal);
                }

                if (settingsExpanded)
                {
                    Color opaqueSettingsColor;
                    if (visualSettings.Theme == "FrostedGlass")
                        opaqueSettingsColor = Color.FromArgb(255, 242, 248, 252);
                    else if (visualSettings.Theme == "LightCard")
                        opaqueSettingsColor = Color.FromArgb(255, 250, 251, 253);
                    else if (visualSettings.Theme == "OrangeGradient")
                        opaqueSettingsColor = Color.FromArgb(255, 205, 103, 77);
                    else if (visualSettings.Theme == "PinkGradient")
                        opaqueSettingsColor = Color.FromArgb(255, 173, 76, 170);
                    else if (visualSettings.Theme == "Custom")
                    {
                        Color custom = Color.FromArgb(visualSettings.CustomBackgroundArgb);
                        opaqueSettingsColor = Color.FromArgb(255, custom.R, custom.G, custom.B);
                    }
                    else if (rainbowText)
                        opaqueSettingsColor = Color.FromArgb(255, 245, 251, 255);
                    else
                        opaqueSettingsColor = Color.FromArgb(255, 9, 40, 59);

                    using (GraphicsPath opaquePath = RoundedRectangle(
                        new Rectangle(0, 0, canvasWidth - 1, canvasHeight - 1), 12))
                    using (Brush opaqueBrush = new SolidBrush(opaqueSettingsColor))
                        graphics.FillPath(opaqueBrush, opaquePath);
                }

                if (capsuleLayoutCollapsed)
                {
                    if (background != null)
                        background.Dispose();
                }
                else if (rainbowText)
                {
                    if (settingsExpanded)
                    {
                        Rectangle settingsPanel = IsBottomCapsuleSettingsExpanded
                            ? new Rectangle(1, 1, canvasWidth - 3, Math.Max(1, HeaderTop - 2))
                            : new Rectangle(1, HeaderHeight + 1,
                                canvasWidth - 3, Math.Max(1, canvasHeight - HeaderHeight - 3));
                        using (GraphicsPath panelPath = RoundedRectangle(settingsPanel, 10))
                        using (LinearGradientBrush panelBackground = new LinearGradientBrush(settingsPanel,
                            Color.FromArgb(210, 245, 251, 255), Color.FromArgb(178, 186, 220, 238),
                            LinearGradientMode.Vertical))
                        using (Pen panelBorder = new Pen(Color.FromArgb(145, 70, 181, 225), 1f))
                        {
                            graphics.FillPath(panelBackground, panelPath);
                            graphics.DrawPath(panelBorder, panelPath);
                        }
                    }
                }
                else
                {
                    using (GraphicsPath shadowPath = RoundedRectangle(new Rectangle(0, 0, canvasWidth - 1, canvasHeight - 1), 12))
                    using (Brush shadow = new SolidBrush(shadowColor))
                        graphics.FillPath(shadow, shadowPath);

                    using (GraphicsPath pillPath = RoundedRectangle(pill, 10))
                    using (background)
                    using (Pen border = new Pen(borderColor, 1f))
                    {
                        graphics.FillPath(background, pillPath);
                        graphics.DrawPath(border, pillPath);
                    }

                    using (GraphicsPath glassPath = RoundedRectangle(new Rectangle(3, 3, canvasWidth - 7, canvasHeight - 7), 8))
                    using (LinearGradientBrush glassSheen = new LinearGradientBrush(
                        new Rectangle(3, 3, Math.Max(1, canvasWidth - 7), Math.Max(1, canvasHeight - 7)),
                        Color.FromArgb(62, 255, 255, 255), Color.FromArgb(4, 255, 255, 255),
                        LinearGradientMode.Vertical))
                    using (Pen innerHighlight = new Pen(Color.FromArgb(92, 255, 255, 255), 1f))
                    {
                        graphics.FillPath(glassSheen, glassPath);
                        graphics.DrawPath(innerHighlight, glassPath);
                    }
                }

                using (Font font = CreateDisplayFont(visualSettings))
                using (StringFormat format = UiRendering.CreateTextFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    Rectangle gear = GearBounds;
                    Rectangle radar = ResetRadarBounds;
                    Rectangle status = TaskStatusBounds;
                    RectangleF box = MainUsageBounds;

                    int glowRadius = settingsExpanded ? 1 : 2;
                    if (bottomCapsulePosition ||
                        visualSettings.DisplayPosition == OverlayDisplayPosition.TitleBar)
                    {
                        DrawBottomUsageCapsules(graphics, visualSettings, textColor, rainbowText);
                    }
                    else
                    {
                        for (int x = -glowRadius; x <= glowRadius; x++)
                        {
                            for (int y = -glowRadius; y <= glowRadius; y++)
                            {
                                if (x == 0 && y == 0)
                                    continue;
                                int distance = Math.Abs(x) + Math.Abs(y);
                                int alpha = distance <= 2 ? glowColor.A : Math.Max(6, glowColor.A / 3);
                                using (Brush glow = new SolidBrush(Color.FromArgb(alpha, glowColor.R, glowColor.G, glowColor.B)))
                                    graphics.DrawString(displayText, font, glow,
                                        new RectangleF(box.X + x, box.Y + y, box.Width, box.Height), format);
                            }
                        }

                        using (Brush text = CreateDisplayTextBrush(box, textColor, rainbowText))
                            graphics.DrawString(displayText, font, text, box, format);
                    }

                    if (!radar.IsEmpty)
                        DrawResetRadar(graphics, resetRadar, visualSettings, textColor, rainbowText);
                    if (!TaskStatusBounds.IsEmpty)
                        DrawTaskStatus(graphics, taskState);

                    if (ShowUpdateIndicator)
                    {
                        Rectangle update = UpdateIndicatorBounds;
                        using (Font updateFont = CreateDisplayFont(visualSettings, 7.2f))
                        using (Brush updateBrush = new SolidBrush(Color.FromArgb(255, 46, 181, 103)))
                        using (StringFormat updateFormat = UiRendering.CreateTextFormat())
                        {
                            updateFormat.Alignment = StringAlignment.Center;
                            updateFormat.LineAlignment = StringAlignment.Center;
                            updateFormat.FormatFlags |= StringFormatFlags.NoWrap;
                            graphics.DrawString("有更新", updateFont, updateBrush, update, updateFormat);
                        }
                    }

                    Rectangle usageRefresh = UsageRefreshBounds;
                    if (!usageRefresh.IsEmpty)
                        DrawUsageRefreshButton(graphics, usageRefresh, visualSettings);

                    bool bottomTextOnly = visualSettings.BottomCapsuleStyle ==
                        BottomCapsuleStyle.TextOnly;
                    if (!gear.IsEmpty && capsuleLayoutPosition && !bottomTextOnly)
                    {
                        Color gearFillColor;
                        Color gearBorderColor;
                        GetPairedActionButtonColors(visualSettings,
                            gearHovered || gearPressed, out gearFillColor,
                            out gearBorderColor);
                        using (GraphicsPath gearPath = RoundedRectangle(GearBounds,
                            BottomCapsuleCornerRadius(visualSettings.BottomCapsuleStyle)))
                        using (Brush gearBackground = new SolidBrush(gearFillColor))
                        using (Pen gearBorder = new Pen(gearBorderColor, 1f))
                        {
                            graphics.FillPath(gearBackground, gearPath);
                            graphics.DrawPath(gearBorder, gearPath);
                        }
                    }
                    else if (!gear.IsEmpty && (gearHovered || gearPressed) && !bottomTextOnly)
                    {
                        Color gearFillColor = gearPressed
                            ? Color.FromArgb(112, textColor.R, textColor.G, textColor.B)
                            : Color.FromArgb(58, textColor.R, textColor.G, textColor.B);
                        using (GraphicsPath gearHighlightPath = RoundedRectangle(GearBounds, 7))
                        using (Brush gearHighlight = new SolidBrush(gearFillColor))
                            graphics.FillPath(gearHighlight, gearHighlightPath);
                    }

                    if (!gear.IsEmpty && !capsuleLayoutPosition)
                    {
                        using (Pen divider = new Pen(Color.FromArgb(70, textColor.R, textColor.G, textColor.B), 1f))
                            graphics.DrawLine(divider, gear.Left, 6, gear.Left, HeaderHeight - 6);
                    }
                    if (!gear.IsEmpty)
                    {
                        using (Font gearFont = new Font("Segoe MDL2 Assets",
                            capsuleLayoutPosition ? 9f : 10f, FontStyle.Regular, GraphicsUnit.Point))
                        using (StringFormat gearFormat = new StringFormat())
                        using (Brush gearBrush = CreateGearBrush(gear, visualSettings))
                        {
                            gearFormat.Alignment = StringAlignment.Center;
                            gearFormat.LineAlignment = StringAlignment.Center;
                            graphics.DrawString("\uE713", gearFont, gearBrush, gear, gearFormat);
                        }
                    }
                }

                if (settingsExpanded && draftSettings != null)
                    DrawInlineSettings(graphics, textColor, borderColor, visualSettings);
            }
            return bitmap;
        }

        public void ExportThemePreviews(string outputDirectory)
        {
            string[] themes = new[] { "NeonBlue", "FrostedGlass", "OrangeGradient", "PinkGradient", "LightCard", "Custom", "RainbowText" };
            string[] names = new[] { "neon-blue", "frosted-glass", "orange-gradient", "pink-gradient", "light-card", "custom", "rainbow-text" };
            OverlaySettings originalSettings = settings;
            OverlaySettings originalDraft = draftSettings;
            string originalText = displayText;
            string[] originalCapsuleTexts = displayCapsuleTexts;
            bool originalExpanded = settingsExpanded;
            CodexTaskState originalTaskState = taskState;
            ResetRadarData originalResetRadar = resetRadar;
            DateTimeOffset? originalResetRadarDisplayNow = resetRadarDisplayNow;
            float originalDpiScale = dpiScale;
            bool originalUpdateAvailable = updateAvailable;
            Size originalSize = Size;

            Directory.CreateDirectory(outputDirectory);
            try
            {
                displayText = "PRO | 周用量剩余：55%·8月24日11:24重置 | 重置券：2 | 累计Token：55.9亿";
                displayCapsuleTexts = new[]
                {
                    "PRO",
                    "5H：无限制",
                    "周：55% 8月24日11:24",
                    "重置券：2",
                    "55.9亿"
                };
                taskState = CodexTaskState.Completed;
                resetRadar = new ResetRadarData
                {
                    Status = ResetRadarStatus.CompletedToday,
                    StatusLabel = "今日已重置",
                    Detail = "Tibo 已宣布完成额度重置 · 8月24日 08:46",
                    ScopeLabel = "全部计划",
                    SourceUrl = "https://x.com/thsottiaux/status/2091688655828246890",
                    EvidencePostId = "2091688655828246890",
                    AnnouncedAt = new DateTimeOffset(2026, 8, 24, 8, 46, 51, TimeSpan.FromHours(8)),
                    Confidence = 0.98d,
                    NetworkAvailable = true
                };
                resetRadarDisplayNow = new DateTimeOffset(2026, 8, 24, 9, 36, 0, TimeSpan.FromHours(8));
                dpiScale = 1f;
                Width = 720;

                for (int index = 0; index < themes.Length; index++)
                {
                    settings = originalSettings.Clone();
                    settings.Theme = themes[index];

                    settingsExpanded = false;
                    draftSettings = null;
                    Height = HeaderHeight;
                    using (Bitmap collapsed = BuildRenderedBitmap())
                        collapsed.Save(Path.Combine(outputDirectory, names[index] + "-collapsed.png"), ImageFormat.Png);

                    settingsExpanded = true;
                    draftSettings = settings.Clone();
                    Height = ExpandedHeight;
                    using (Bitmap expanded = BuildRenderedBitmap())
                        expanded.Save(Path.Combine(outputDirectory, names[index] + "-expanded.png"), ImageFormat.Png);
                }

                settings = originalSettings.Clone();
                settings.Theme = "RainbowText";
                settings.DisplayPosition = OverlayDisplayPosition.ComposerInside;
                settings.ComposerInsideLayout = ComposerInsideLayout.TwoLines;
                settings.BottomCapsuleStyle = BottomCapsuleStyle.SmallRoundedRectangle;
                taskState = CodexTaskState.Processing;
                displayCapsuleTexts = new[]
                {
                    "PRO",
                    "5H：无限制",
                    "周：55% 8月24日11:24",
                    "重置券：2",
                    "55.9亿"
                };
                settingsExpanded = false;
                draftSettings = null;
                Width = 850;
                Height = ComposerInsideHeight;
                using (Bitmap bottomCapsules = BuildRenderedBitmap())
                    bottomCapsules.Save(Path.Combine(outputDirectory, "bottom-capsules-collapsed.png"), ImageFormat.Png);

                BottomCapsuleStyle[] capsuleStyles = new[]
                {
                    BottomCapsuleStyle.Rounded,
                    BottomCapsuleStyle.SmallRoundedRectangle,
                    BottomCapsuleStyle.TextOnly
                };
                string[] capsuleStyleNames = new[]
                {
                    "bottom-capsules-rounded.png",
                    "bottom-capsules-small-rounded.png",
                    "bottom-capsules-text-only.png"
                };
                for (int index = 0; index < capsuleStyles.Length; index++)
                {
                    settings.BottomCapsuleStyle = capsuleStyles[index];
                    using (Bitmap stylePreview = BuildRenderedBitmap())
                        stylePreview.Save(Path.Combine(outputDirectory, capsuleStyleNames[index]), ImageFormat.Png);
                }
                settings.BottomCapsuleStyle = BottomCapsuleStyle.SmallRoundedRectangle;

                settings.Theme = "LightCard";
                using (Bitmap lightCardBottom = BuildRenderedBitmap())
                    lightCardBottom.Save(Path.Combine(outputDirectory, "light-card-bottom-capsules.png"), ImageFormat.Png);
                settings.Theme = "RainbowText";

                settingsExpanded = true;
                draftSettings = settings.Clone();
                Height = ExpandedHeight;
                using (Bitmap bottomCapsulesExpanded = BuildRenderedBitmap())
                    bottomCapsulesExpanded.Save(Path.Combine(outputDirectory, "bottom-capsules-expanded.png"), ImageFormat.Png);

                taskState = CodexTaskState.Completed;

                // Keep a dedicated feature screenshot for the green update hint.
                settings = originalSettings.Clone();
                settings.Theme = "RainbowText";
                settingsExpanded = false;
                draftSettings = null;
                updateAvailable = true;
                Height = HeaderHeight;
                using (Bitmap updatePreview = BuildRenderedBitmap())
                    updatePreview.Save(Path.Combine(outputDirectory, "update-available.png"), ImageFormat.Png);

                OverlaySettings bannerSettings = originalSettings.Clone();
                bannerSettings.Theme = "RainbowText";
                resetRadarBanner.ExportPreviews(
                    outputDirectory,
                    resetRadar,
                    bannerSettings,
                    resetRadarDisplayNow.Value);

                OverlaySettings guideSettings = originalSettings.Clone();
                guideSettings.Theme = "NeonBlue";
                settings = guideSettings.Clone();
                settingsExpanded = false;
                draftSettings = null;
                Width = 720;
                Height = HeaderHeight;
                using (Bitmap overlay = BuildRenderedBitmap())
                using (FirstRunGuideForm guide = new FirstRunGuideForm(guideSettings))
                using (Bitmap guideBitmap = guide.ExportPreviewBitmap(
                    new Rectangle(0, 0, 720, HeaderHeight),
                    new Rectangle(0, 0, 1280, 720)))
                {
                    int guideLeft = guide.Bounds.Left;
                    int guideTop = guide.Bounds.Top;
                    int previewHeight = Math.Max(overlay.Height, guideTop + guideBitmap.Height + 4);
                    using (Bitmap guidePreview = new Bitmap(overlay.Width, previewHeight, PixelFormat.Format32bppArgb))
                    using (Graphics guideGraphics = Graphics.FromImage(guidePreview))
                    {
                        guideGraphics.Clear(Color.FromArgb(248, 250, 252));
                        guideGraphics.DrawImageUnscaled(overlay, 0, 0);
                        guideGraphics.DrawImageUnscaled(guideBitmap, guideLeft, guideTop);
                        guidePreview.Save(Path.Combine(outputDirectory, "first-run-guide.png"), ImageFormat.Png);
                    }
                }
            }
            finally
            {
                settings = originalSettings;
                draftSettings = originalDraft;
                displayText = originalText;
                displayCapsuleTexts = originalCapsuleTexts;
                settingsExpanded = originalExpanded;
                taskState = originalTaskState;
                resetRadar = originalResetRadar;
                resetRadarDisplayNow = originalResetRadarDisplayNow;
                dpiScale = originalDpiScale;
                updateAvailable = originalUpdateAvailable;
                Size = originalSize;
            }
        }

        private void DrawInlineSettings(Graphics graphics, Color textColor, Color borderColor, OverlaySettings visualSettings)
        {
            Color boxColor = Color.FromArgb(30, textColor.R, textColor.G, textColor.B);
            int separatorY = IsBottomCapsuleSettingsExpanded
                ? Math.Max(0, HeaderTop - 2)
                : HeaderHeight + 2;
            using (Pen separator = new Pen(Color.FromArgb(75, borderColor.R, borderColor.G, borderColor.B), 1f))
                graphics.DrawLine(separator, 12, separatorY, CanvasWidth - 12, separatorY);

            using (Font labelFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold, GraphicsUnit.Point))
            using (Font valueFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold, GraphicsUnit.Point))
            using (Brush textBrush = CreateDisplayTextBrush(
                new RectangleF(0, HeaderHeight, CanvasWidth, Math.Max(1, CanvasHeight - HeaderHeight)),
                textColor, visualSettings.Theme == "RainbowText"))
            using (StringFormat left = UiRendering.CreateTextFormat())
            using (StringFormat center = UiRendering.CreateTextFormat())
            {
                left.Alignment = StringAlignment.Near;
                left.LineAlignment = StringAlignment.Center;
                center.Alignment = StringAlignment.Center;
                center.LineAlignment = StringAlignment.Center;

                DrawInlineLabel(graphics, "字体", InlineRowBounds(0), labelFont, textBrush, left);
                Rectangle fontBox = InlineValueBounds(0);
                DrawInlineBox(graphics, fontBox, boxColor, borderColor);
                graphics.DrawString("‹", valueFont, textBrush,
                    new Rectangle(FontPreviousBounds.Left, FontPreviousBounds.Top - 1,
                        FontPreviousBounds.Width, FontPreviousBounds.Height), center);
                graphics.DrawString(visualSettings.FontName, valueFont, textBrush,
                    new Rectangle(fontBox.Left + 34, fontBox.Top, fontBox.Width - 68, fontBox.Height), center);
                graphics.DrawString("›", valueFont, textBrush,
                    new Rectangle(FontNextBounds.Left, FontNextBounds.Top - 1,
                        FontNextBounds.Width, FontNextBounds.Height), center);

                DrawInlineLabel(graphics, "外观", InlineRowBounds(1), labelFont, textBrush, left);
                string[] themeLabels = new[] { "荧光蓝", "磨砂", "渐变橙", "渐变粉", "轻盈白", "自定义", "彩字" };
                for (int index = 0; index < themeLabels.Length; index++)
                {
                    Rectangle theme = ThemeChoiceBounds(index);
                    bool selected = InlineThemeIndex(visualSettings.Theme) == index;
                    Color fill = selected ? Color.FromArgb(85, textColor.R, textColor.G, textColor.B) : boxColor;
                    DrawInlineBox(graphics, theme, fill, borderColor);
                    graphics.DrawString(themeLabels[index], labelFont, textBrush, theme, center);
                }

                graphics.DrawString("背景颜色", labelFont, textBrush, BackgroundLabelBounds, left);
                Rectangle colorBox = BackgroundColorBounds;
                DrawInlineBox(graphics, colorBox, boxColor, borderColor);
                Color custom = Color.FromArgb(visualSettings.CustomBackgroundArgb);
                using (Brush swatch = new SolidBrush(Color.FromArgb(255, custom.R, custom.G, custom.B)))
                    graphics.FillRectangle(swatch, new Rectangle(colorBox.Left + 8, colorBox.Top + 6, 42, colorBox.Height - 12));
                graphics.DrawString("选择颜色", labelFont, textBrush,
                    new Rectangle(colorBox.Left + 60, colorBox.Top, colorBox.Width - 68, colorBox.Height), left);

                graphics.DrawString("自动刷新", labelFont, textBrush, RefreshLabelBounds, left);
                Rectangle refreshBox = RefreshValueBounds;
                DrawInlineBox(graphics, refreshBox, boxColor, borderColor);
                int refreshStepperWidth = RefreshStepperWidth;
                graphics.DrawString("−", valueFont, textBrush,
                    new Rectangle(refreshBox.Left, refreshBox.Top - 1,
                        refreshStepperWidth, refreshBox.Height), center);
                graphics.DrawString(visualSettings.RefreshSeconds.ToString(CultureInfo.InvariantCulture) + " 秒", valueFont, textBrush,
                    new Rectangle(refreshBox.Left + refreshStepperWidth, refreshBox.Top,
                        Math.Max(1, refreshBox.Width - refreshStepperWidth * 2), refreshBox.Height), center);
                graphics.DrawString("+", valueFont, textBrush,
                    new Rectangle(refreshBox.Right - refreshStepperWidth, refreshBox.Top - 1,
                        refreshStepperWidth, refreshBox.Height), center);

                DrawInlineLabel(graphics, "显示位置", InlineRowBounds(3), labelFont, textBrush, left);
                Rectangle displayPosition = DisplayPositionBounds;
                DrawInlineBox(graphics, displayPosition, boxColor, borderColor);
                graphics.DrawString(OverlayDisplayPositions.Label(visualSettings.DisplayPosition),
                    valueFont, textBrush, displayPosition, center);

                DrawInlineLabel(graphics, "字号", InlineRowBounds(4), labelFont, textBrush, left);
                string[] fontSizeLabels = new[] { "顶", "内", "下" };
                OverlayDisplayPosition[] fontSizePositions = new[]
                {
                    OverlayDisplayPosition.TitleBar,
                    OverlayDisplayPosition.ComposerInside,
                    OverlayDisplayPosition.ComposerBelow
                };
                for (int index = 0; index < fontSizeLabels.Length; index++)
                {
                    Rectangle fontSizeBox = FontSizeControlBounds(index);
                    DrawInlineBox(graphics, fontSizeBox, boxColor, borderColor);
                    graphics.DrawString(fontSizeLabels[index], labelFont, textBrush,
                        new Rectangle(fontSizeBox.Left + 2, fontSizeBox.Top,
                            Math.Max(1, FontSizeLabelWidth - 2), fontSizeBox.Height), center);
                    graphics.DrawString("−", valueFont, textBrush,
                        new Rectangle(FontSizeMinusBounds(index).Left,
                            FontSizeMinusBounds(index).Top - 1,
                            FontSizeMinusBounds(index).Width,
                            FontSizeMinusBounds(index).Height), center);
                    graphics.DrawString(OverlayFontSizes.Get(visualSettings,
                            fontSizePositions[index]).ToString("0.0", CultureInfo.InvariantCulture),
                        valueFont, textBrush, FontSizeValueBounds(index), center);
                    graphics.DrawString("+", valueFont, textBrush,
                        new Rectangle(FontSizePlusBounds(index).Left,
                            FontSizePlusBounds(index).Top - 1,
                            FontSizePlusBounds(index).Width,
                            FontSizePlusBounds(index).Height), center);
                }

                DrawInlineLabel(graphics, "用量排版", InlineRowBounds(5), labelFont, textBrush, left);
                Rectangle composerLayout = ComposerInsideLayoutBounds;
                DrawInlineBox(graphics, composerLayout, boxColor, borderColor);
                graphics.DrawString(ComposerInsideLayouts.Label(visualSettings.ComposerInsideLayout),
                    valueFont, textBrush, composerLayout, center);

                DrawInlineLabel(graphics, "胶囊风格", InlineRowBounds(6), labelFont, textBrush, left);
                Rectangle capsuleStyle = BottomCapsuleStyleBounds;
                DrawInlineBox(graphics, capsuleStyle, boxColor, borderColor);
                graphics.DrawString(BottomCapsuleStyles.Label(visualSettings.BottomCapsuleStyle),
                    valueFont, textBrush, capsuleStyle, center);

                DrawResetRadarPanel(graphics, textColor, borderColor, visualSettings);

                if (brandLogo != null)
                {
                    GraphicsState logoState = graphics.Save();
                    using (GraphicsPath logoClip = new GraphicsPath())
                    {
                        logoClip.AddEllipse(BrandLogoBounds);
                        graphics.SetClip(logoClip, CombineMode.Intersect);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.DrawImage(brandLogo, BrandLogoBounds);
                    }
                    graphics.Restore(logoState);
                    using (Pen logoBorder = new Pen(Color.FromArgb(220, 255, 211, 92), 1.5f))
                        graphics.DrawEllipse(logoBorder, BrandLogoBounds);
                }
                RectangleF brandTextBounds = RectangleF.Union(PublicAccountBounds, AuthorBounds);
                using (Font brandFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold, GraphicsUnit.Point))
                using (Brush brandTextBrush = CreateDisplayTextBrush(brandTextBounds, textColor, true))
                {
                    graphics.DrawString("公众号：拾玖说跨境AI", brandFont, brandTextBrush, PublicAccountBounds, left);
                    graphics.DrawString("作者：拾玖Blues", brandFont, brandTextBrush, AuthorBounds, left);
                }
                string versionText = "版本 v" + GitHubReleaseUpdateService.CurrentVersion;
                using (Font versionFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold, GraphicsUnit.Point))
                {
                    int versionGradientWidth = Math.Min(VersionBounds.Width,
                        Math.Max(1, (int)Math.Ceiling(graphics.MeasureString(versionText, versionFont).Width)));
                    Rectangle versionGradientBounds = new Rectangle(
                        VersionBounds.Left, VersionBounds.Top,
                        versionGradientWidth, VersionBounds.Height);
                    using (Brush versionBrush = CreateDisplayTextBrush(versionGradientBounds, textColor, true))
                        graphics.DrawString(versionText, versionFont, versionBrush, VersionBounds, left);
                }
                DrawInlineBox(graphics, ExitBounds, Color.FromArgb(158, 225, 92, 104), Color.FromArgb(220, 255, 170, 178));
                using (Brush exitText = new SolidBrush(Color.White))
                using (StringFormat exitCenter = (StringFormat)center.Clone())
                {
                    exitCenter.FormatFlags |= StringFormatFlags.NoWrap;
                    graphics.DrawString("退出工具", valueFont, exitText, ExitBounds, exitCenter);
                }

                DrawInlineBox(graphics, GuideBounds, boxColor, borderColor);
                DrawInlineBox(graphics, CancelBounds, boxColor, borderColor);
                DrawInlineBox(graphics, SaveBounds, Color.FromArgb(85, textColor.R, textColor.G, textColor.B), borderColor);
                graphics.DrawString("使用指引", labelFont, textBrush, GuideBounds, center);
                graphics.DrawString("取消", labelFont, textBrush, CancelBounds, center);
                graphics.DrawString("保存", valueFont, textBrush, SaveBounds, center);
            }
        }

        private void DrawInlineLabel(Graphics graphics, string text, Rectangle row, Font font, Brush brush, StringFormat format, int labelLeft = 16)
        {
            int labelWidth = Math.Max(48, InlineValueBounds(0).Left - labelLeft - 6);
            graphics.DrawString(text, font, brush, new Rectangle(labelLeft, row.Top, labelWidth, row.Height), format);
        }

        private static void DrawInlineBox(Graphics graphics, Rectangle bounds, Color fillColor, Color borderColor)
        {
            using (GraphicsPath path = RoundedRectangle(bounds, 6))
            using (Brush fill = new SolidBrush(fillColor))
            using (Pen border = new Pen(Color.FromArgb(105, borderColor.R, borderColor.G, borderColor.B), 1f))
            {
                graphics.FillPath(fill, path);
                graphics.DrawPath(border, path);
            }
        }

        private void DrawResetRadarPanel(Graphics graphics, Color textColor, Color borderColor, OverlaySettings visualSettings)
        {
            Color fill;
            Color semanticBorder;
            Color dot;
            GetResetRadarColors(resetRadar.Status, out fill, out semanticBorder, out dot);
            Rectangle panel = ResetRadarPanelBounds;
            DrawInlineBox(graphics, panel, Color.FromArgb(38, fill.R, fill.G, fill.B), semanticBorder);

            using (Font titleFont = CreateDisplayFont(visualSettings, 8.5f))
            using (Font detailFont = CreateDisplayFont(visualSettings, 7.8f))
            using (Brush titleBrush = new SolidBrush(textColor))
            using (Brush detailBrush = new SolidBrush(Color.FromArgb(225, textColor.R, textColor.G, textColor.B)))
            using (Brush dotBrush = new SolidBrush(dot))
            using (StringFormat left = UiRendering.CreateTextFormat())
            using (StringFormat detailFormat = UiRendering.CreateTextFormat())
            using (StringFormat center = UiRendering.CreateTextFormat())
            {
                left.Alignment = StringAlignment.Near;
                left.LineAlignment = StringAlignment.Center;
                left.FormatFlags |= StringFormatFlags.NoWrap;
                detailFormat.Alignment = StringAlignment.Near;
                detailFormat.LineAlignment = StringAlignment.Center;
                detailFormat.Trimming = StringTrimming.EllipsisCharacter;
                detailFormat.FormatFlags |= StringFormatFlags.NoWrap;
                center.Alignment = StringAlignment.Center;
                center.LineAlignment = StringAlignment.Center;

                bool showStatusDot = ResetRadarDisplay.ShouldShowStatusDot(resetRadar);
                if (showStatusDot)
                    graphics.FillEllipse(dotBrush, panel.Left + 10, panel.Top + 10, 7, 7);
                DateTimeOffset displayNow = resetRadarDisplayNow ?? DateTimeOffset.Now;
                string title = "TIBO RADAR · " +
                    ResetRadarDisplay.BuildHeadline(resetRadar, displayNow) +
                    ResetRadarDisplay.ConfidenceSuffix(resetRadar) + " · 非官方";
                int titleLeft = panel.Left + (showStatusDot ? 23 : 10);
                int titleWidth = Math.Max(40, ResetSourceBounds.Width - (showStatusDot ? 25 : 12));
                graphics.DrawString(title, titleFont, titleBrush,
                    new Rectangle(titleLeft, panel.Top + 4, titleWidth, 20), left);
                string detail = ResetRadarDisplay.BuildPrimaryLine(
                    resetRadar,
                    displayNow);
                graphics.DrawString(detail, detailFont, detailBrush,
                    new Rectangle(panel.Left + 10, panel.Top + 23, Math.Max(40, ResetSourceBounds.Width - 12), 19), detailFormat);

                bool enabled = visualSettings.ResetNotificationsEnabled;
                Color toggleFill = enabled
                    ? Color.FromArgb(105, dot.R, dot.G, dot.B)
                    : Color.FromArgb(34, textColor.R, textColor.G, textColor.B);
                DrawInlineBox(graphics, ResetNotificationBounds, toggleFill, enabled ? semanticBorder : borderColor);
                graphics.DrawString(enabled ? "提醒  开" : "提醒  关", titleFont, titleBrush, ResetNotificationBounds, center);
            }
        }

        private Rectangle InlineRowBounds(int row)
        {
            return new Rectangle(14, 36 + InlineSettingsOffset + row * 34, CanvasWidth - 28, 27);
        }

        private Rectangle InlineValueBounds(int row)
        {
            Rectangle rowBounds = InlineRowBounds(row);
            int left = CanvasWidth < 520 ? 96 : 116;
            return new Rectangle(left, rowBounds.Top,
                Math.Max(1, CanvasWidth - left - 16), rowBounds.Height);
        }

        private Rectangle FontPreviousBounds { get { Rectangle box = InlineValueBounds(0); return new Rectangle(box.Left, box.Top, 34, box.Height); } }
        private Rectangle FontNextBounds { get { Rectangle box = InlineValueBounds(0); return new Rectangle(box.Right - 34, box.Top, 34, box.Height); } }
        private Rectangle BackgroundLabelBounds { get { return new Rectangle(16, 104 + InlineSettingsOffset, CanvasWidth < 520 ? 50 : 74, 27); } }
        private Rectangle BackgroundColorBounds
        {
            get
            {
                int left = CanvasWidth < 520 ? 70 : 92;
                int width = CanvasWidth < 520
                    ? Math.Max(76, Math.Min(132, CanvasWidth / 3 - 12))
                    : 174;
                return new Rectangle(left, 104 + InlineSettingsOffset, width, 27);
            }
        }
        private Rectangle RefreshLabelBounds
        {
            get
            {
                int left = CanvasWidth < 520 ? BackgroundColorBounds.Right + 8 : 280;
                return new Rectangle(left, 104 + InlineSettingsOffset, CanvasWidth < 520 ? 56 : 72, 27);
            }
        }
        private Rectangle RefreshValueBounds
        {
            get
            {
                int left = CanvasWidth < 520 ? RefreshLabelBounds.Right + 4 : 354;
                return new Rectangle(left, 104 + InlineSettingsOffset,
                    Math.Max(1, CanvasWidth - left - 16), 27);
            }
        }
        private int RefreshStepperWidth
        {
            get { return Math.Min(38, Math.Max(18, RefreshValueBounds.Width / 3)); }
        }
        private Rectangle RefreshMinusBounds { get { Rectangle box = RefreshValueBounds; return new Rectangle(box.Left, box.Top, RefreshStepperWidth, box.Height); } }
        private Rectangle RefreshPlusBounds { get { Rectangle box = RefreshValueBounds; return new Rectangle(box.Right - RefreshStepperWidth, box.Top, RefreshStepperWidth, box.Height); } }
        private Rectangle DisplayPositionBounds { get { return InlineValueBounds(3); } }
        private Rectangle FontSizeBounds { get { return InlineValueBounds(4); } }
        private Rectangle ComposerInsideLayoutBounds { get { return InlineValueBounds(5); } }
        private Rectangle BottomCapsuleStyleBounds { get { return InlineValueBounds(6); } }
        private Rectangle ResetRadarPanelBounds { get { return new Rectangle(16, 276 + InlineSettingsOffset, Math.Max(180, CanvasWidth - 32), 46); } }
        private Rectangle ResetNotificationBounds { get { Rectangle panel = ResetRadarPanelBounds; return new Rectangle(panel.Right - 92, panel.Top + 9, 82, 28); } }
        private Rectangle ResetSourceBounds { get { Rectangle panel = ResetRadarPanelBounds; return new Rectangle(panel.Left, panel.Top, Math.Max(80, panel.Width - 100), panel.Height); } }
        private Rectangle BrandLogoBounds { get { return new Rectangle(16, 334 + InlineSettingsOffset, 64, 64); } }
        private Rectangle PublicAccountBounds { get { return new Rectangle(90, 343 + InlineSettingsOffset, Math.Max(80, ExitBounds.Left - 98), 20); } }
        private Rectangle AuthorBounds { get { return new Rectangle(90, 365 + InlineSettingsOffset, Math.Max(80, ExitBounds.Left - 98), 20); } }
        private Rectangle VersionBounds { get { return new Rectangle(90, 385 + InlineSettingsOffset, Math.Max(80, ExitBounds.Left - 98), 17); } }
        private Rectangle GuideBounds { get { return new Rectangle(16, 408 + InlineSettingsOffset, 82, 28); } }
        private Rectangle ExitBounds { get { return new Rectangle(Math.Max(108, CanvasWidth - 212), 408 + InlineSettingsOffset, 60, 28); } }
        private Rectangle CancelBounds { get { return new Rectangle(Math.Max(176, CanvasWidth - 144), 408 + InlineSettingsOffset, 60, 28); } }
        private Rectangle SaveBounds { get { return new Rectangle(Math.Max(244, CanvasWidth - 76), 408 + InlineSettingsOffset, 60, 28); } }

        private Rectangle FontSizeControlBounds(int index)
        {
            Rectangle row = FontSizeBounds;
            const int gap = 3;
            int width = Math.Max(1, (row.Width - gap * 2) / 3);
            int left = row.Left + index * (width + gap);
            int right = index == 2 ? row.Right : left + width;
            return Rectangle.FromLTRB(left, row.Top, Math.Max(left + 1, right), row.Bottom);
        }

        private int FontSizeLabelWidth
        {
            get { return CanvasWidth < 520 ? 14 : 18; }
        }

        private int FontSizeStepperWidth
        {
            get { return CanvasWidth < 520 ? 15 : 18; }
        }

        private Rectangle FontSizeMinusBounds(int index)
        {
            Rectangle box = FontSizeControlBounds(index);
            return new Rectangle(box.Left + FontSizeLabelWidth, box.Top,
                FontSizeStepperWidth, box.Height);
        }

        private Rectangle FontSizePlusBounds(int index)
        {
            Rectangle box = FontSizeControlBounds(index);
            return new Rectangle(box.Right - FontSizeStepperWidth, box.Top,
                FontSizeStepperWidth, box.Height);
        }

        private Rectangle FontSizeValueBounds(int index)
        {
            Rectangle box = FontSizeControlBounds(index);
            Rectangle minus = FontSizeMinusBounds(index);
            Rectangle plus = FontSizePlusBounds(index);
            return Rectangle.FromLTRB(minus.Right, box.Top,
                Math.Max(minus.Right + 1, plus.Left), box.Bottom);
        }

        private Rectangle ThemeChoiceBounds(int index)
        {
            Rectangle box = InlineValueBounds(1);
            int width = box.Width / 7;
            int left = box.Left + index * width;
            int right = index == 6 ? box.Right : left + width - 3;
            return new Rectangle(left, box.Top, Math.Max(1, right - left), box.Height);
        }

        private Rectangle GearBounds
        {
            get
            {
                if (bottomCapsuleLayout != null)
                    return bottomCapsuleLayout.GearBounds;
                if (IsComposerInsidePosition)
                {
                    Rectangle usage;
                    Rectangle gear;
                    OverlayInteraction.GetComposerInsideContentBounds(
                        CanvasWidth, HeaderTop, ActiveHeaderHeight, out usage, out gear);
                    return gear;
                }
                if (IsBottomCapsulePosition)
                {
                    return new Rectangle(Math.Max(0, CanvasWidth - 26), HeaderTop + 2,
                        22, BottomCapsuleContentHeight);
                }
                int rightChrome = ShowUpdateIndicator ? 82 : 34;
                return new Rectangle(Math.Max(0, CanvasWidth - rightChrome), HeaderTop + 2, 30, HeaderHeight - 4);
            }
        }

        private Rectangle UpdateIndicatorBounds
        {
            get
            {
                if (!ShowUpdateIndicator)
                    return Rectangle.Empty;
                if (bottomCapsuleLayout != null)
                    return bottomCapsuleLayout.UpdateBounds;
                Rectangle gear = GearBounds;
                return IsBottomCapsulePosition
                    ? new Rectangle(Math.Max(0, gear.Left - 45),
                        HeaderTop + ActiveHeaderHeight - BottomCapsuleContentHeight,
                        42, BottomCapsuleContentHeight)
                    : new Rectangle(gear.Right + 3, HeaderTop + 5, 42, 18);
            }
        }

        private Rectangle TaskStatusBounds
        {
            get
            {
                if (HideTaskStatus)
                    return Rectangle.Empty;
                if (IsBottomCapsulePosition)
                {
                    Rectangle update = UpdateIndicatorBounds;
                    int right = update.IsEmpty ? GearBounds.Left - 6 : update.Left - 6;
                    return new Rectangle(Math.Max(0, right - 44),
                        HeaderTop + ActiveHeaderHeight - BottomCapsuleContentHeight,
                        44, BottomCapsuleContentHeight);
                }
                return new Rectangle(Math.Max(0, GearBounds.Left - 50), HeaderTop + 5, 44, 18);
            }
        }

        private Rectangle ResetRadarBounds
        {
            get
            {
                if (IsComposerInsidePosition || UsesEmbeddedRadar)
                    return Rectangle.Empty;
                OverlaySettings visualSettings = settingsExpanded && draftSettings != null
                    ? draftSettings
                    : settings;
                int width = visualSettings.DisplayPosition == OverlayDisplayPosition.TitleBar
                    ? GetResetRadarPillWidth(visualSettings, false)
                    : (CanvasWidth < 500 ? 22 : 104);
                if (bottomCapsuleLayout != null)
                    return bottomCapsuleLayout.RadarBounds;
                if (IsBottomCapsulePosition)
                {
                    width = CanvasWidth < 500 ? 22 : 86;
                    Rectangle task = TaskStatusBounds;
                    Rectangle update = UpdateIndicatorBounds;
                    int right = !task.IsEmpty
                        ? task.Left - 2
                        : (!update.IsEmpty ? update.Left - 2 : GearBounds.Left - 2);
                    return new Rectangle(Math.Max(0, right - width),
                        HeaderTop + ActiveHeaderHeight - BottomCapsuleContentHeight,
                        width, BottomCapsuleContentHeight);
                }
                Rectangle refresh = UsageRefreshBounds;
                return new Rectangle(Math.Max(0, refresh.Left - width - 2),
                    HeaderTop + Math.Max(0, (ActiveHeaderHeight - 18) / 2), width, 18);
            }
        }

        private Rectangle UsageRefreshBounds
        {
            get
            {
                if (bottomCapsuleLayout != null)
                    return bottomCapsuleLayout.RefreshBounds;

                Rectangle gear = GearBounds;
                Rectangle refresh;
                Rectangle pairedGear;
                OverlayInteraction.GetPairedControlBounds(gear.Right,
                    HeaderTop, ActiveHeaderHeight, gear.Width, 2,
                    out refresh, out pairedGear);
                return refresh;
            }
        }

        private Rectangle RadarRefreshBounds
        {
            get
            {
                return Rectangle.Empty;
            }
        }

        private Rectangle MainUsageBounds
        {
            get
            {
                if (bottomCapsuleLayout != null)
                    return bottomCapsuleLayout.UsageBounds;
                if (IsComposerInsidePosition)
                {
                    Rectangle usage;
                    Rectangle gear;
                    OverlayInteraction.GetComposerInsideContentBounds(
                        CanvasWidth, HeaderTop, ActiveHeaderHeight, out usage, out gear);
                    Rectangle refresh = UsageRefreshBounds;
                    return new Rectangle(usage.Left, usage.Top,
                        Math.Max(1, refresh.Left - usage.Left - 2), usage.Height);
                }
                if (UsesEmbeddedRadar)
                {
                    Rectangle refresh = UsageRefreshBounds;
                    return new Rectangle(10, HeaderTop,
                        Math.Max(40, refresh.Left - 12), ActiveHeaderHeight);
                }
                Rectangle bounds = OverlayInteraction.GetMainUsageBounds(
                    ResetRadarBounds.Left, ActiveHeaderHeight);
                return new Rectangle(bounds.Left, HeaderTop, bounds.Width, bounds.Height);
            }
        }

        private static int GetCapsuleContentHeight(OverlaySettings visualSettings)
        {
            return visualSettings != null &&
                visualSettings.DisplayPosition == OverlayDisplayPosition.TitleBar
                ? HeaderHeight - 4
                : BottomCapsuleContentHeight;
        }

        private int GetResetRadarPillWidth(
            OverlaySettings visualSettings,
            bool bottomCapsule)
        {
            int minimumWidth = bottomCapsule ? 86 : 128;
            if (visualSettings == null)
                return minimumWidth;

            string label = ResetRadarDisplay.BuildPillLabel(resetRadar,
                resetRadarDisplayNow ?? DateTimeOffset.Now);
            if (String.IsNullOrWhiteSpace(label))
                return minimumWidth;

            using (Bitmap canvas = UiRendering.CreateLayeredBitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(canvas))
            using (Font font = CreateDisplayFont(visualSettings,
                bottomCapsule ? 7.1f : 8f))
            using (StringFormat format = UiRendering.CreateTextFormat())
            {
                format.FormatFlags |= StringFormatFlags.NoWrap;
                int measuredWidth = (int)Math.Ceiling(graphics.MeasureString(
                    label, font, Int32.MaxValue, format).Width) + 10;
                return Math.Max(minimumWidth, Math.Min(220, measuredWidth));
            }
        }

        private BottomCapsuleLayout BuildBottomCapsuleLayout(
            Graphics graphics,
            OverlaySettings visualSettings)
        {
            string[] sections = displayCapsuleTexts ?? new string[0];
            if (visualSettings.DisplayPosition == OverlayDisplayPosition.ComposerInside)
            {
                Rectangle usage;
                Rectangle gear;
                OverlayInteraction.GetComposerInsideContentBounds(
                    CanvasWidth, HeaderTop, ActiveHeaderHeight, out usage, out gear);
                Rectangle refresh;
                Rectangle pairedGear;
                OverlayInteraction.GetPairedControlBounds(gear.Right,
                    HeaderTop, ActiveHeaderHeight, gear.Width, 2,
                    out refresh, out pairedGear);
                return new BottomCapsuleLayout
                {
                    UsageBounds = new Rectangle(usage.Left, usage.Top,
                        Math.Max(1, refresh.Left - usage.Left - 2), usage.Height),
                    RadarBounds = Rectangle.Empty,
                    UpdateBounds = Rectangle.Empty,
                    RefreshBounds = refresh,
                    GearBounds = pairedGear
                };
            }
            if (UsesTwoLineLayout(visualSettings))
            {
                const int twoLineControlSize = 18;
                const int twoLineControlGap = 2;
                Rectangle twoLineRefresh;
                Rectangle twoLineGear;
                OverlayInteraction.GetPairedControlBounds(CanvasWidth - 2,
                    HeaderTop, ActiveHeaderHeight, twoLineControlSize,
                    twoLineControlGap, out twoLineRefresh, out twoLineGear);
                int twoLineUpdateWidth = ShowUpdateIndicator ? 42 : 0;
                int twoLineUpdateLeft = twoLineUpdateWidth > 0
                    ? Math.Max(0, twoLineRefresh.Left - twoLineControlGap - twoLineUpdateWidth)
                    : twoLineRefresh.Left;
                int usageRight = twoLineUpdateWidth > 0
                    ? twoLineUpdateLeft - twoLineControlGap
                    : twoLineRefresh.Left - twoLineControlGap;
                BottomCapsuleLayout twoLineLayout = new BottomCapsuleLayout();
                twoLineLayout.UsageBounds = new Rectangle(0, HeaderTop,
                    Math.Max(40, usageRight), ActiveHeaderHeight);
                twoLineLayout.RadarBounds = Rectangle.Empty;
                twoLineLayout.UpdateBounds = twoLineUpdateWidth > 0
                    ? new Rectangle(twoLineUpdateLeft,
                        OverlayInteraction.GetCenteredContentTop(
                            HeaderTop, ActiveHeaderHeight, BottomCapsuleContentHeight),
                        twoLineUpdateWidth, BottomCapsuleContentHeight)
                    : Rectangle.Empty;
                twoLineLayout.RefreshBounds = twoLineRefresh;
                twoLineLayout.GearBounds = twoLineGear;
                return twoLineLayout;
            }
            const float horizontalPadding = 5f;
            const float capsuleGap = 2f;
            bool lightCardTheme = visualSettings.Theme == "LightCard";
            float usageWidth = 0f;

            using (Font font = CreateDisplayFont(visualSettings, BottomCapsuleTextSize))
            using (StringFormat textFormat = UiRendering.CreateTextFormat())
            {
                textFormat.FormatFlags |= StringFormatFlags.NoWrap;
                if (visualSettings.BottomCapsuleStyle == BottomCapsuleStyle.TextOnly)
                {
                    usageWidth = graphics.MeasureString(String.Join(" | ", sections), font,
                        Int32.MaxValue, textFormat).Width;
                }
                else
                {
                    for (int index = 0; index < sections.Length; index++)
                    {
                        usageWidth += (float)Math.Ceiling(graphics.MeasureString(
                            sections[index], font, Int32.MaxValue, textFormat).Width) +
                            horizontalPadding * 2f + (lightCardTheme ? 10f : 0f);
                        if (index > 0)
                            usageWidth += capsuleGap;
                    }
                }
            }

            int capsuleContentHeight = GetCapsuleContentHeight(visualSettings);
            int radarWidth = visualSettings.DisplayPosition == OverlayDisplayPosition.TitleBar
                ? GetResetRadarPillWidth(visualSettings, false)
                : (CanvasWidth < 500 ? 22 : 86);
            int updateWidth = ShowUpdateIndicator ? 42 : 0;
            const int controlSize = 18;
            const int controlGap = 2;
            int fixedWidth = radarWidth + controlSize * 2 + controlGap * 3;
            if (updateWidth > 0)
                fixedWidth += updateWidth + controlGap;
            int availableUsageWidth = Math.Max(40, CanvasWidth - fixedWidth - 8);
            int roundedUsageWidth = Math.Min(availableUsageWidth,
                Math.Max(40, (int)Math.Ceiling(usageWidth)));
            int groupWidth = roundedUsageWidth + fixedWidth;
            int groupLeft = OverlayInteraction.GetCenteredGroupLeft(CanvasWidth, groupWidth);
            int contentTop = OverlayInteraction.GetCenteredContentTop(
                HeaderTop, ActiveHeaderHeight, capsuleContentHeight);
            BottomCapsuleLayout layout = new BottomCapsuleLayout();
            layout.UsageBounds = new Rectangle(groupLeft, contentTop,
                roundedUsageWidth, capsuleContentHeight);

            int nextLeft = layout.UsageBounds.Right + controlGap;
            layout.RadarBounds = new Rectangle(nextLeft, contentTop,
                radarWidth, capsuleContentHeight);
            nextLeft = layout.RadarBounds.Right + controlGap;
            if (updateWidth > 0)
            {
                layout.UpdateBounds = new Rectangle(nextLeft, contentTop,
                    updateWidth, capsuleContentHeight);
                nextLeft = layout.UpdateBounds.Right + controlGap;
            }
            OverlayInteraction.GetPairedControlBounds(groupLeft + groupWidth,
                HeaderTop, ActiveHeaderHeight, controlSize, controlGap,
                out layout.RefreshBounds, out layout.GearBounds);
            return layout;
        }

        private void DrawBottomUsageCapsules(
            Graphics graphics,
            OverlaySettings visualSettings,
            Color textColor,
            bool rainbowText)
        {
            string[] sections = displayCapsuleTexts ?? new string[0];
            if (sections.Length == 0)
                return;

            Rectangle usageBounds = MainUsageBounds;
            if (visualSettings.DisplayPosition == OverlayDisplayPosition.ComposerInside ||
                UsesTwoLineLayout(visualSettings))
            {
                DrawComposerInsideUsage(graphics, visualSettings, sections, usageBounds);
                return;
            }
            int capsuleContentHeight = GetCapsuleContentHeight(visualSettings);
            float capsuleHeight = capsuleContentHeight;
            const float horizontalPadding = 5f;
            const float capsuleGap = 2f;
            bool lightCardTheme = visualSettings.Theme == "LightCard";
            const float statusDotReservation = 10f;
            bool lightSurface = visualSettings.Theme == "FrostedGlass" ||
                visualSettings.Theme == "LightCard" || rainbowText;
            Color capsuleFill;
            Color capsuleBorder;
            UiRendering.ResolveCapsuleSurfaceColors(visualSettings.Theme,
                visualSettings.CustomBackgroundArgb, out capsuleFill, out capsuleBorder);
            Color capsuleText = lightSurface
                ? Color.FromArgb(255, 58, 69, 82)
                : textColor;

            using (Font font = CreateDisplayFont(visualSettings, BottomCapsuleTextSize))
            using (StringFormat textFormat = UiRendering.CreateTextFormat())
            {
                textFormat.Alignment = StringAlignment.Near;
                textFormat.LineAlignment = StringAlignment.Center;
                textFormat.Trimming = StringTrimming.None;
                textFormat.FormatFlags |= StringFormatFlags.NoWrap;

                if (visualSettings.BottomCapsuleStyle == BottomCapsuleStyle.TextOnly)
                {
                    textFormat.Alignment = StringAlignment.Center;
                    string plainText = String.Join(" | ", sections);
                    Rectangle textBounds = new Rectangle(
                        usageBounds.Left,
                        usageBounds.Top,
                        usageBounds.Width,
                        capsuleContentHeight);
                    using (Brush plainBrush = CreateDisplayTextBrush(
                        textBounds, capsuleText, rainbowText))
                    {
                        UiRendering.DrawOpticallyCenteredText(graphics, plainText,
                            font, plainBrush, textBounds, StringAlignment.Center);
                    }
                    return;
                }

                float totalWidth = 0f;
                float[] widths = new float[sections.Length];
                for (int index = 0; index < sections.Length; index++)
                {
                    widths[index] = (float)Math.Ceiling(graphics.MeasureString(
                        sections[index], font, Int32.MaxValue, textFormat).Width) +
                        horizontalPadding * 2f + (lightCardTheme ? statusDotReservation : 0f);
                    totalWidth += widths[index];
                    if (index > 0)
                        totalWidth += capsuleGap;
                }

                float x = usageBounds.Left;
                float y = usageBounds.Top;
                for (int index = 0; index < sections.Length && x < usageBounds.Right; index++)
                {
                    float remainingWidth = usageBounds.Right - x;
                    float capsuleWidth = Math.Min(widths[index], remainingWidth);
                    if (capsuleWidth < 24f)
                        break;

                    RectangleF capsule = new RectangleF(x, y, capsuleWidth, capsuleHeight);
                    using (GraphicsPath capsulePath = RoundedRectangle(
                        Rectangle.Round(capsule),
                        BottomCapsuleCornerRadius(visualSettings.BottomCapsuleStyle)))
                    using (Brush fill = new SolidBrush(capsuleFill))
                    using (Pen border = new Pen(capsuleBorder, 1f))
                    using (Brush text = CreateDisplayTextBrush(capsule, capsuleText, rainbowText))
                    {
                        graphics.FillPath(fill, capsulePath);
                        graphics.DrawPath(border, capsulePath);
                        if (lightCardTheme)
                        {
                            using (Brush dot = new SolidBrush(LightCardCapsuleDotColor(index)))
                                graphics.FillEllipse(dot, capsule.Left + horizontalPadding,
                                    capsule.Top + (capsule.Height - 6f) / 2f, 6f, 6f);
                        }
                        RectangleF textBounds = new RectangleF(capsule.Left + horizontalPadding +
                            (lightCardTheme ? statusDotReservation : 0f), capsule.Top,
                            Math.Max(1f, capsule.Width - horizontalPadding * 2f -
                                (lightCardTheme ? statusDotReservation : 0f)),
                            capsule.Height);
                        if (capsuleWidth >= widths[index])
                        {
                            UiRendering.DrawOpticallyCenteredText(graphics,
                                sections[index], font, text, textBounds,
                                StringAlignment.Near);
                        }
                        else
                        {
                            graphics.DrawString(sections[index], font, text,
                                textBounds, textFormat);
                        }
                    }
                    x += widths[index] + capsuleGap;
                }
            }
        }

        private void DrawComposerInsideUsage(
            Graphics graphics,
            OverlaySettings visualSettings,
            string[] sections,
            Rectangle usageBounds)
        {
            if (sections == null || sections.Length == 0)
                return;

            string radarText = BuildComposerInsideRadarText();
            bool twoLines = visualSettings.ComposerInsideLayout == ComposerInsideLayout.TwoLines;
            string firstLine = twoLines
                ? JoinComposerInsideSections(sections, 0, Math.Min(3, sections.Length))
                : BuildComposerInsideOneLine(sections, radarText);
            string secondLine = twoLines
                ? JoinComposerInsideSections(sections, Math.Min(3, sections.Length), sections.Length, radarText)
                : String.Empty;
            int centerAxisY = usageBounds.Top + usageBounds.Height / 2;
            int firstRowHeight = twoLines
                ? Math.Max(1, centerAxisY - usageBounds.Top)
                : Math.Max(1, usageBounds.Height);
            bool rainbowText = visualSettings.Theme == "RainbowText";
            Color composerTextColor = UiRendering.ResolveComposerInsideTextColor(
                visualSettings.Theme, visualSettings.CustomBackgroundArgb);
            Color accentStart;
            Color accentEnd;
            UiRendering.ResolveGearColors(visualSettings.Theme,
                visualSettings.CustomBackgroundArgb, out accentStart, out accentEnd);

            bool textOnly = visualSettings.BottomCapsuleStyle == BottomCapsuleStyle.TextOnly;
            int textInset = textOnly ? 1 : 5;
            Rectangle firstBounds = new Rectangle(usageBounds.Left + textInset, usageBounds.Top,
                Math.Max(1, usageBounds.Width - textInset - 3), firstRowHeight);
            Rectangle secondBounds = new Rectangle(firstBounds.Left,
                twoLines ? centerAxisY : firstBounds.Bottom,
                firstBounds.Width, Math.Max(1, usageBounds.Bottom -
                    (twoLines ? centerAxisY : firstBounds.Bottom)));

            using (Font primaryFont = CreateDisplayFont(visualSettings,
                twoLines ? ComposerInsideTextSize : ComposerInsideTextSize + 0.25f))
            using (Font secondaryFont = CreateDisplayFont(visualSettings,
                ComposerInsideTextSize))
            using (StringFormat format = UiRendering.CreateTextFormat())
            using (Brush primaryBrush = CreateComposerInsideTextBrush(
                GetComposerInsideTextBrushBounds(graphics, firstLine, primaryFont, firstBounds),
                composerTextColor, rainbowText))
            using (Brush secondaryBrush = CreateComposerInsideTextBrush(
                GetComposerInsideTextBrushBounds(graphics, secondLine, secondaryFont, secondBounds),
                Color.FromArgb(222, composerTextColor.R, composerTextColor.G,
                    composerTextColor.B), rainbowText))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags |= StringFormatFlags.NoWrap;
                if (!textOnly)
                {
                    Rectangle firstCapsule = CreateComposerInsideCapsuleBounds(
                        graphics, firstLine, primaryFont, firstBounds, twoLines);
                    DrawComposerInsideCapsule(graphics, firstCapsule, visualSettings,
                        accentStart, rainbowText);
                    if (twoLines)
                    {
                        Rectangle secondCapsule = CreateComposerInsideCapsuleBounds(
                            graphics, secondLine, secondaryFont, secondBounds, true);
                        DrawComposerInsideCapsule(graphics, secondCapsule, visualSettings,
                            accentStart, rainbowText);
                    }
                }

                if (graphics.MeasureString(firstLine, primaryFont).Width <= firstBounds.Width)
                {
                    UiRendering.DrawOpticallyCenteredText(graphics, firstLine,
                        primaryFont, primaryBrush, firstBounds, StringAlignment.Center);
                }
                else
                {
                    graphics.DrawString(firstLine, primaryFont, primaryBrush,
                        firstBounds, format);
                }
                if (!String.IsNullOrEmpty(secondLine))
                {
                    if (graphics.MeasureString(secondLine, secondaryFont).Width <=
                        secondBounds.Width)
                    {
                        UiRendering.DrawOpticallyCenteredText(graphics, secondLine,
                            secondaryFont, secondaryBrush, secondBounds,
                            StringAlignment.Center);
                    }
                    else
                    {
                        graphics.DrawString(secondLine, secondaryFont, secondaryBrush,
                            secondBounds, format);
                    }
                }
            }
        }

        private static Rectangle GetComposerInsideTextBrushBounds(
            Graphics graphics,
            string text,
            Font font,
            Rectangle bounds)
        {
            if (String.IsNullOrWhiteSpace(text))
                return bounds;

            int width = Math.Min(bounds.Width, Math.Max(1,
                (int)Math.Ceiling(graphics.MeasureString(text, font).Width)));
            return new Rectangle(bounds.Left + Math.Max(0, (bounds.Width - width) / 2),
                bounds.Top, width, bounds.Height);
        }

        private static Rectangle CreateComposerInsideCapsuleBounds(
            Graphics graphics,
            string text,
            Font font,
            Rectangle textBounds,
            bool compactRow)
        {
            int textWidth = Math.Min(textBounds.Width, Math.Max(1,
                (int)Math.Ceiling(graphics.MeasureString(text ?? String.Empty, font).Width)));
            return OverlayInteraction.GetCompactCenteredCapsuleBounds(textBounds,
                textWidth, 7, compactRow ? 1 : 2);
        }

        private static void DrawComposerInsideCapsule(
            Graphics graphics,
            Rectangle bounds,
            OverlaySettings visualSettings,
            Color accent,
            bool rainbowText)
        {
            Color frame = rainbowText
                ? Color.FromArgb(118, 104, 75, 196)
                : Color.FromArgb(104, accent.R, accent.G, accent.B);
            Color fill = rainbowText
                ? Color.FromArgb(13, 104, 75, 196)
                : Color.FromArgb(20, accent.R, accent.G, accent.B);
            int radius = visualSettings.BottomCapsuleStyle == BottomCapsuleStyle.Rounded
                ? Math.Min(7, Math.Max(1, bounds.Height / 2))
                : Math.Min(3, Math.Max(1, bounds.Height / 2));
            using (GraphicsPath path = RoundedRectangle(bounds, radius))
            using (Brush background = new SolidBrush(fill))
            using (Pen border = new Pen(frame, 1f))
            {
                graphics.FillPath(background, path);
                graphics.DrawPath(border, path);
            }
        }

        private static Brush CreateComposerInsideTextBrush(
            Rectangle bounds,
            Color fallback,
            bool rainbowText)
        {
            if (!rainbowText)
                return new SolidBrush(fallback);

            Color[] colors = UiRendering.GetComposerInsideRainbowColors();
            LinearGradientBrush brush = new LinearGradientBrush(bounds, colors[0],
                colors[colors.Length - 1], LinearGradientMode.Horizontal);
            ColorBlend blend = new ColorBlend();
            blend.Colors = colors;
            blend.Positions = new[] { 0f, 0.34f, 0.68f, 1f };
            brush.InterpolationColors = blend;
            return brush;
        }

        private string BuildComposerInsideRadarText()
        {
            string status = resetRadar == null ? String.Empty : resetRadar.StatusLabel;
            return String.IsNullOrWhiteSpace(status) ? "待刷新" : status;
        }

        private static string BuildComposerInsideOneLine(string[] sections, string radarText)
        {
            List<string> overview = new List<string>();
            if (sections.Length > 0)
                overview.Add(sections[0]);
            if (sections.Length > 1)
                overview.Add(sections[1]);
            if (sections.Length > 2)
            {
                string weekly = sections[2];
                int separator = weekly.IndexOf(' ');
                overview.Add(separator > 0 ? weekly.Substring(0, separator) : weekly);
            }
            overview.Add(radarText);
            return String.Join(" · ", overview.ToArray());
        }

        private static string JoinComposerInsideSections(
            string[] sections,
            int start,
            int end,
            params string[] trailingSections)
        {
            List<string> values = new List<string>();
            for (int index = start; index < end && index < sections.Length; index++)
            {
                if (!String.IsNullOrWhiteSpace(sections[index]))
                    values.Add(sections[index]);
            }
            if (trailingSections != null)
            {
                for (int index = 0; index < trailingSections.Length; index++)
                {
                    if (!String.IsNullOrWhiteSpace(trailingSections[index]))
                        values.Add(trailingSections[index]);
                }
            }
            return String.Join(" · ", values.ToArray());
        }

        private static int BottomCapsuleCornerRadius(BottomCapsuleStyle style)
        {
            return style == BottomCapsuleStyle.Rounded ? 8 : 4;
        }

        private static Color LightCardCapsuleDotColor(int index)
        {
            if (index == 0) return Color.FromArgb(255, 128, 86, 241);
            if (index == 1) return Color.FromArgb(255, 58, 134, 246);
            if (index == 2) return Color.FromArgb(255, 15, 184, 133);
            return Color.FromArgb(255, 247, 164, 31);
        }

        private static void GetPairedActionButtonColors(
            OverlaySettings visualSettings,
            bool active,
            out Color fill,
            out Color border)
        {
            UiRendering.ResolveCapsuleSurfaceColors(visualSettings.Theme,
                visualSettings.CustomBackgroundArgb, out fill, out border);
            if (active)
            {
                fill = Color.FromArgb(Math.Min(255, fill.A + 17),
                    fill.R, fill.G, fill.B);
                border = Color.FromArgb(Math.Min(255, border.A + 44),
                    border.R, border.G, border.B);
            }
        }

        private void DrawUsageRefreshButton(
            Graphics graphics,
            Rectangle bounds,
            OverlaySettings visualSettings)
        {
            bool textOnly = visualSettings.BottomCapsuleStyle == BottomCapsuleStyle.TextOnly;
            if (!textOnly)
            {
                Color fill;
                Color border;
                GetPairedActionButtonColors(visualSettings, radarRefreshHovered,
                    out fill, out border);
                using (GraphicsPath path = RoundedRectangle(bounds,
                    BottomCapsuleCornerRadius(visualSettings.BottomCapsuleStyle)))
                using (Brush background = new SolidBrush(fill))
                using (Pen outline = new Pen(border, 1f))
                {
                    graphics.FillPath(background, path);
                    graphics.DrawPath(outline, path);
                }
            }

            using (Font iconFont = new Font("Segoe UI Symbol", 9f,
                FontStyle.Regular, GraphicsUnit.Point))
            using (StringFormat format = UiRendering.CreateTextFormat())
            using (Brush icon = CreateGearBrush(bounds, visualSettings))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                graphics.DrawString("↻", iconFont, icon, bounds, format);
            }
        }

        private void DrawResetRadar(
            Graphics graphics,
            ResetRadarData radar,
            OverlaySettings visualSettings,
            Color textColor,
            bool rainbowText)
        {
            Rectangle bounds = ResetRadarBounds;
            Color fill;
            Color border;
            Color dot;
            GetResetRadarColors(radar.Status, out fill, out border, out dot);

            bool bottomTextOnly = visualSettings.BottomCapsuleStyle ==
                BottomCapsuleStyle.TextOnly;
            bool useUnifiedCapsuleSurface =
                visualSettings.DisplayPosition == OverlayDisplayPosition.TitleBar ||
                OverlayDisplayPositions.IsComposerPosition(visualSettings.DisplayPosition);
            bool lightCapsuleSurface = visualSettings.Theme == "FrostedGlass" ||
                visualSettings.Theme == "LightCard" || rainbowText;
            Color neutralCapsuleFill;
            Color neutralCapsuleBorder;
            UiRendering.ResolveCapsuleSurfaceColors(visualSettings.Theme,
                visualSettings.CustomBackgroundArgb, out neutralCapsuleFill,
                out neutralCapsuleBorder);
            Color neutralCapsuleText = lightCapsuleSurface
                ? Color.FromArgb(255, 58, 69, 82)
                : textColor;
            if (useUnifiedCapsuleSurface)
            {
                fill = neutralCapsuleFill;
                border = neutralCapsuleBorder;
            }
            if (radarHovered)
                fill = Color.FromArgb(Math.Min(245, fill.A + 35), fill.R, fill.G, fill.B);

            if (!bottomTextOnly)
            {
                int radius = BottomCapsuleCornerRadius(
                    visualSettings.BottomCapsuleStyle);
                using (GraphicsPath path = RoundedRectangle(bounds, radius))
                using (Brush fillBrush = new SolidBrush(fill))
                using (Pen borderPen = new Pen(border, 1f))
                {
                    graphics.FillPath(fillBrush, path);
                    graphics.DrawPath(borderPen, path);
                }
            }

            bool showStatusDot = ResetRadarDisplay.ShouldShowStatusDot(radar);
            if (showStatusDot)
            {
                int dotSize = bounds.Width <= 24 ? 8 : 6;
                int dotLeft = bounds.Width <= 24 ? bounds.Left + (bounds.Width - dotSize) / 2 : bounds.Left + 8;
                int dotTop = bounds.Top + (bounds.Height - dotSize) / 2;
                using (Brush dotBrush = new SolidBrush(dot))
                using (Pen pulse = new Pen(Color.FromArgb(130, dot.R, dot.G, dot.B), 1f))
                {
                    graphics.DrawEllipse(pulse, dotLeft - 2, dotTop - 2, dotSize + 4, dotSize + 4);
                    graphics.FillEllipse(dotBrush, dotLeft, dotTop, dotSize, dotSize);
                }
            }

            if (bounds.Width > 24)
            {
                using (Font font = CreateDisplayFont(visualSettings,
                    IsBottomCapsulePosition ? 7.1f : 8f))
                using (Brush text = useUnifiedCapsuleSurface
                    ? CreateDisplayTextBrush(bounds, neutralCapsuleText, rainbowText)
                    : new SolidBrush(bottomTextOnly
                        ? Color.FromArgb(255, fill.R, fill.G, fill.B)
                        : Color.White))
                using (StringFormat format = UiRendering.CreateTextFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    format.Trimming = StringTrimming.None;
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    string pillLabel = ResetRadarDisplay.BuildPillLabel(
                        radar,
                        resetRadarDisplayNow ?? DateTimeOffset.Now);
                    int labelLeft = bounds.Left + (showStatusDot ? 17 : 4);
                    Rectangle labelRefresh = RadarRefreshBounds;
                    int labelRight = labelRefresh.IsEmpty ? bounds.Right - 4 : labelRefresh.Left - 1;
                    int labelWidth = labelRight - labelLeft;
                    graphics.DrawString(pillLabel, font, text,
                        new Rectangle(labelLeft, bounds.Top, Math.Max(1, labelWidth), bounds.Height), format);
                }

                Rectangle refresh = RadarRefreshBounds;
                if (!refresh.IsEmpty)
                {
                    Color refreshFill = radarRefreshHovered
                        ? Color.FromArgb(100, 255, 255, 255)
                        : Color.FromArgb(42, 255, 255, 255);
                    using (StringFormat refreshFormat = UiRendering.CreateTextFormat())
                    using (Font refreshFont = new Font("Segoe UI Symbol", 10f, FontStyle.Bold, GraphicsUnit.Point))
                    using (Brush refreshText = useUnifiedCapsuleSurface
                        ? CreateDisplayTextBrush(refresh, neutralCapsuleText, rainbowText)
                        : new SolidBrush(bottomTextOnly
                            ? Color.FromArgb(255, fill.R, fill.G, fill.B)
                            : Color.White))
                    {
                        refreshFormat.Alignment = StringAlignment.Center;
                        refreshFormat.LineAlignment = StringAlignment.Center;
                        if (!bottomTextOnly && !useUnifiedCapsuleSurface)
                        {
                            using (Brush refreshBrush = new SolidBrush(refreshFill))
                                graphics.FillRectangle(refreshBrush, refresh);
                        }
                        graphics.DrawString("↻", refreshFont, refreshText, refresh, refreshFormat);
                    }
                }
            }
        }

        private static void GetResetRadarColors(ResetRadarStatus status, out Color fill, out Color border, out Color dot)
        {
            if (status == ResetRadarStatus.CompletedToday)
            {
                fill = Color.FromArgb(205, 18, 126, 87);
                border = Color.FromArgb(235, 92, 224, 163);
                dot = Color.FromArgb(255, 120, 255, 190);
            }
            else if (status == ResetRadarStatus.ScheduledToday || status == ResetRadarStatus.ScheduledUpcoming)
            {
                fill = Color.FromArgb(210, 184, 105, 18);
                border = Color.FromArgb(240, 255, 202, 89);
                dot = Color.FromArgb(255, 255, 224, 118);
            }
            else if (status == ResetRadarStatus.Offline)
            {
                fill = Color.FromArgb(200, 126, 68, 78);
                border = Color.FromArgb(230, 239, 137, 148);
                dot = Color.FromArgb(255, 255, 166, 176);
            }
            else if (status == ResetRadarStatus.NoSignal)
            {
                fill = Color.FromArgb(190, 62, 91, 118);
                border = Color.FromArgb(225, 135, 176, 211);
                dot = Color.FromArgb(255, 163, 207, 239);
            }
            else
            {
                fill = Color.FromArgb(185, 82, 92, 104);
                border = Color.FromArgb(220, 157, 169, 181);
                dot = Color.FromArgb(255, 190, 201, 212);
            }
        }

        private void DrawTaskStatus(Graphics graphics, CodexTaskState state)
        {
            string label = "检测中";
            Color fill = Color.FromArgb(175, 92, 105, 118);
            Color border = Color.FromArgb(220, 164, 177, 190);
            if (state == CodexTaskState.Processing)
            {
                label = "处理中";
                fill = Color.FromArgb(220, 15, 126, 214);
                border = Color.FromArgb(255, 104, 210, 255);
            }
            else if (state == CodexTaskState.Completed)
            {
                label = "完成";
                fill = Color.FromArgb(220, 32, 155, 94);
                border = Color.FromArgb(255, 126, 240, 171);
            }
            else if (state == CodexTaskState.Interrupted)
            {
                label = "中断";
                fill = Color.FromArgb(225, 196, 58, 68);
                border = Color.FromArgb(255, 255, 151, 158);
            }

            using (GraphicsPath path = RoundedRectangle(TaskStatusBounds, 4))
            using (Brush background = new SolidBrush(fill))
            using (Pen outline = new Pen(border, 1f))
            using (Font statusFont = new Font("Microsoft YaHei UI", 7.25f, FontStyle.Bold, GraphicsUnit.Point))
            using (Brush statusText = new SolidBrush(Color.White))
            using (GraphicsPath textPath = new GraphicsPath())
            using (StringFormat typographic = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                graphics.FillPath(background, path);
                graphics.DrawPath(outline, path);

                float emSize = statusFont.SizeInPoints * graphics.DpiY / 72f;
                textPath.AddString(label, statusFont.FontFamily, (int)statusFont.Style,
                    emSize, PointF.Empty, typographic);
                RectangleF textBounds = textPath.GetBounds();
                using (Matrix centerText = new Matrix())
                {
                    centerText.Translate(
                        TaskStatusBounds.Left + (TaskStatusBounds.Width - textBounds.Width) / 2f - textBounds.Left,
                        TaskStatusBounds.Top + (TaskStatusBounds.Height - textBounds.Height) / 2f - textBounds.Top);
                    textPath.Transform(centerText);
                }
                graphics.FillPath(statusText, textPath);
            }
        }

        private Font CreateDisplayFont(OverlaySettings visualSettings)
        {
            return CreateDisplayFont(visualSettings, 8.5f);
        }

        private Font CreateDisplayFont(OverlaySettings visualSettings, float size)
        {
            float baseline = visualSettings.DisplayPosition == OverlayDisplayPosition.TitleBar
                ? 8.5f
                : BottomCapsuleTextSize;
            float configuredSize = OverlayFontSizes.Get(visualSettings,
                visualSettings.DisplayPosition);
            float effectiveSize = size * configuredSize / baseline;
            return UiRendering.CreateTextFont(visualSettings.FontName, effectiveSize, FontStyle.Bold);
        }

        private static Image LoadBrandLogo()
        {
            try
            {
                using (Stream stream = typeof(OverlayForm).Assembly.GetManifestResourceStream("CodexUsageOverlay.BrandLogo.png"))
                {
                    if (stream == null)
                        return null;
                    using (Image source = Image.FromStream(stream))
                        return new Bitmap(source);
                }
            }
            catch
            {
                return null;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WM_LBUTTONDOWN)
            {
                long packed = message.LParam.ToInt64();
                Point client = ToLogicalPoint(new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff))));
                if (GearBounds.Contains(client))
                {
                    gearPressed = true;
                    ToggleInlineSettings();
                    message.Result = IntPtr.Zero;
                    return;
                }
            }
            if (message.Msg == NativeMethods.WM_NCHITTEST)
            {
                long packed = message.LParam.ToInt64();
                int screenX = unchecked((short)(packed & 0xffff));
                int screenY = unchecked((short)((packed >> 16) & 0xffff));
                Point client = ToLogicalPoint(PointToClient(new Point(screenX, screenY)));
                bool interactive = OverlayInteraction.IsHeaderInteractive(
                    client, ResetRadarBounds, GearBounds) ||
                    UsageRefreshBounds.Contains(client) ||
                    (settingsExpanded &&
                        new Rectangle(0, 0, CanvasWidth, CanvasHeight).Contains(client));
                message.Result = (IntPtr)(interactive ? NativeMethods.HTCLIENT : NativeMethods.HTTRANSPARENT);
                return;
            }
            base.WndProc(ref message);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Point logicalLocation = ToLogicalPoint(e.Location);
            bool rightDownInGear = rightDownStartedInGear;
            if (e.Button == MouseButtons.Right)
                rightDownStartedInGear = false;
            if (e.Button == MouseButtons.Left && RadarRefreshBounds.Contains(logicalLocation))
            {
                RequestRadarRefresh();
                return;
            }
            if (e.Button == MouseButtons.Left && UsageRefreshBounds.Contains(logicalLocation))
            {
                RequestUsageAndRadarRefresh();
                return;
            }
            if (e.Button == MouseButtons.Left && ResetRadarBounds.Contains(logicalLocation))
            {
                if (OverlayInteraction.DecideResetRadarClick(
                    e.Button,
                    logicalLocation,
                    ResetRadarBounds) ==
                    OverlayMouseAction.OpenRunwayPage)
                {
                    OpenRunwayPage();
                    return;
                }
                return;
            }
            if (OverlayInteraction.DecideGearMouseUp(
                e.Button,
                logicalLocation,
                GearBounds,
                rightDownInGear) ==
                OverlayMouseAction.ShowUpdateMenu)
            {
                ShowUpdateMenu();
                return;
            }
            if (e.Button == MouseButtons.Left && GearBounds.Contains(logicalLocation))
            {
                gearPressed = false;
                RefreshInlinePanel();
                return;
            }
            if (e.Button != MouseButtons.Left || !settingsExpanded || draftSettings == null)
                return;

            if (ResetNotificationBounds.Contains(logicalLocation)) ToggleResetNotifications();
            else if (ResetSourceBounds.Contains(logicalLocation)) OpenRadarSource();
            else if (FontPreviousBounds.Contains(logicalLocation)) CycleFont(-1);
            else if (FontNextBounds.Contains(logicalLocation)) CycleFont(1);
            else if (BackgroundColorBounds.Contains(logicalLocation)) ChooseInlineColor();
            else if (RefreshMinusBounds.Contains(logicalLocation)) ChangeRefreshSeconds(-5);
            else if (RefreshPlusBounds.Contains(logicalLocation)) ChangeRefreshSeconds(5);
            else if (HandleFontSizeClick(logicalLocation)) return;
            else if (DisplayPositionBounds.Contains(logicalLocation)) ToggleDisplayPosition();
            else if (ComposerInsideLayoutBounds.Contains(logicalLocation)) ToggleComposerInsideLayout();
            else if (BottomCapsuleStyleBounds.Contains(logicalLocation)) ToggleBottomCapsuleStyle();
            else if (GuideBounds.Contains(logicalLocation)) ShowUsageGuide();
            else if (ExitBounds.Contains(logicalLocation)) Application.Exit();
            else if (CancelBounds.Contains(logicalLocation)) CloseInlineSettings(false);
            else if (SaveBounds.Contains(logicalLocation)) CloseInlineSettings(true);
            else
            {
                for (int index = 0; index < 7; index++)
                {
                    if (ThemeChoiceBounds(index).Contains(logicalLocation))
                    {
                        draftSettings.Theme = InlineThemeName(index);
                        RefreshInlinePanel();
                        break;
                    }
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                Point logicalLocation = ToLogicalPoint(e.Location);
                rightDownStartedInGear = GearBounds.Contains(logicalLocation);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point logicalLocation = ToLogicalPoint(e.Location);
            bool hovered = GearBounds.Contains(logicalLocation);
            bool resetHovered = ResetRadarBounds.Contains(logicalLocation) ||
                (settingsExpanded && ResetSourceBounds.Contains(logicalLocation));
            bool refreshHovered = UsageRefreshBounds.Contains(logicalLocation);
            if (hovered != gearHovered || resetHovered != radarHovered || refreshHovered != radarRefreshHovered)
            {
                gearHovered = hovered;
                radarHovered = resetHovered;
                radarRefreshHovered = refreshHovered;
                RefreshInlinePanel();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (gearHovered || gearPressed || radarHovered || radarRefreshHovered)
            {
                gearHovered = false;
                gearPressed = false;
                radarHovered = false;
                radarRefreshHovered = false;
                RefreshInlinePanel();
            }
        }

        private Point ToLogicalPoint(Point physicalPoint)
        {
            return new Point(UnscalePixels(physicalPoint.X), UnscalePixels(physicalPoint.Y));
        }

        private void ToggleInlineSettings()
        {
            if (settingsExpanded)
                CloseInlineSettings(true);
            else
            {
                int headerScreenTop = GetCurrentHeaderScreenTop();
                bool preserveBottomHeader = OverlayDisplayPositions.IsComposerPosition(
                    settings.DisplayPosition);
                draftSettings = settings.Clone();
                settingsExpanded = true;
                resetRadarBanner.HideBanner();
                RefreshInlinePanel(preserveBottomHeader ? headerScreenTop : Int32.MinValue);
            }
        }

        private void CycleFont(int direction)
        {
            if (fontOptions.Length == 0)
                return;
            int index = Array.IndexOf(fontOptions, draftSettings.FontName);
            if (index < 0) index = 0;
            index = (index + direction + fontOptions.Length) % fontOptions.Length;
            draftSettings.FontName = fontOptions[index];
            RefreshInlinePanel();
        }

        private void ChangeRefreshSeconds(int delta)
        {
            draftSettings.RefreshSeconds = Math.Max(5, Math.Min(3600, draftSettings.RefreshSeconds + delta));
            RefreshInlinePanel();
        }

        private bool HandleFontSizeClick(Point logicalLocation)
        {
            OverlayDisplayPosition[] positions = new[]
            {
                OverlayDisplayPosition.TitleBar,
                OverlayDisplayPosition.ComposerInside,
                OverlayDisplayPosition.ComposerBelow
            };
            for (int index = 0; index < positions.Length; index++)
            {
                if (FontSizeMinusBounds(index).Contains(logicalLocation))
                {
                    ChangeDisplayFontSize(positions[index], -OverlayFontSizes.Step);
                    return true;
                }
                if (FontSizePlusBounds(index).Contains(logicalLocation))
                {
                    ChangeDisplayFontSize(positions[index], OverlayFontSizes.Step);
                    return true;
                }
            }
            return false;
        }

        private void ChangeDisplayFontSize(OverlayDisplayPosition position, float delta)
        {
            float current = OverlayFontSizes.Get(draftSettings, position);
            float next = (float)(Math.Round((current + delta) * 2f) / 2f);
            OverlayFontSizes.Set(draftSettings, position, next);
            RefreshInlinePanel();
        }

        private void ToggleDisplayPosition()
        {
            int nextIndex = (OverlayDisplayPositions.Index(draftSettings.DisplayPosition) + 1) % 3;
            draftSettings.DisplayPosition = OverlayDisplayPositions.FromIndex(nextIndex);
            RefreshInlinePanel();
        }

        private void ToggleBottomCapsuleStyle()
        {
            int nextIndex = (BottomCapsuleStyles.Index(draftSettings.BottomCapsuleStyle) + 1) % 3;
            draftSettings.BottomCapsuleStyle = BottomCapsuleStyles.FromIndex(nextIndex);
            RefreshInlinePanel();
        }

        private void ToggleComposerInsideLayout()
        {
            int nextIndex = (ComposerInsideLayouts.Index(draftSettings.ComposerInsideLayout) + 1) % 2;
            draftSettings.ComposerInsideLayout = ComposerInsideLayouts.FromIndex(nextIndex);
            RefreshInlinePanel();
        }

        private void ToggleResetNotifications()
        {
            draftSettings.ResetNotificationsEnabled = !draftSettings.ResetNotificationsEnabled;
            RefreshInlinePanel();
        }

        private void ApplyNotificationVisibility()
        {
            if (resetNotifyIcon != null)
                resetNotifyIcon.Visible = settings.ResetNotificationsEnabled;
        }

        private void ShowResetNotification(ResetRadarNotification notification)
        {
            if (notification == null || resetNotifyIcon == null)
                return;
            notificationSourceUrl = notification.SourceUrl ?? String.Empty;
            resetNotifyIcon.Visible = true;
            resetNotifyIcon.ShowBalloonTip(8000, notification.Title, notification.Body, ToolTipIcon.Info);
        }

        private void CheckForReleaseUpdate()
        {
            releaseUpdateService.RequestCheck();
            GitHubReleaseUpdateSnapshot update = releaseUpdateService.Snapshot();
            bool manualCompleted = manualUpdateCheckRequestedUtc.HasValue && !update.IsChecking;
            bool manualSucceeded = manualCompleted && update.LastCheckedUtc.HasValue &&
                update.LastCheckedUtc.Value >= manualUpdateCheckRequestedUtc.Value;
            if (manualCompleted)
                manualUpdateCheckRequestedUtc = null;

            bool trustedUpdate = update.UpdateAvailable &&
                GitHubReleaseUpdateService.IsAllowedReleaseUrl(update.ReleaseUrl);
            if (trustedUpdate)
            {
                string revision = update.LatestVersion + "|" + update.ReleaseUrl;
                bool unseen = !String.Equals(
                    revision, lastReleaseUpdateRevision, StringComparison.Ordinal);
                if (unseen || manualCompleted)
                {
                    lastReleaseUpdateRevision = revision;
                    releaseUpdateUrl = update.ReleaseUrl;
                    ShowReleaseUpdateBalloon(
                        "发现 v" + update.LatestVersion + "，点击查看 GitHub Release。",
                        ToolTipIcon.Info);
                }
                return;
            }

            if (manualCompleted)
            {
                releaseUpdateUrl = String.Empty;
                ShowReleaseUpdateBalloon(
                    manualSucceeded
                        ? "当前已是最新稳定版。"
                        : "暂时无法连接 GitHub，请稍后重试。",
                    manualSucceeded ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
        }

        private void CheckForReleaseUpdateNow()
        {
            DateTime requestedUtc = DateTime.UtcNow;
            bool started = releaseUpdateService.RequestCheck(true);
            updateMenu.Close();
            if (started)
            {
                manualUpdateCheckRequestedUtc = requestedUtc;
                releaseUpdateUrl = String.Empty;
                ShowReleaseUpdateBalloon("正在检查 GitHub 稳定版更新。", ToolTipIcon.Info);
            }
            else if (releaseUpdateService.Snapshot().IsChecking)
            {
                releaseUpdateUrl = String.Empty;
                ShowReleaseUpdateBalloon("GitHub 稳定版更新正在检查中。", ToolTipIcon.Info);
            }
        }

        private void ShowUpdateMenu()
        {
            UpdateMenuState menuState = OverlayInteraction.BuildUpdateMenuState(
                releaseUpdateService.Snapshot());
            currentVersionMenuItem.Text = "CODEX USAGE OVERLAY  ·  v" +
                GitHubReleaseUpdateService.CurrentVersion;
            checkUpdateMenuItem.Text = "↻  " + menuState.CheckUpdateText;
            checkUpdateMenuItem.Enabled = menuState.CanCheck;
            downloadUpdateMenuItem.Enabled = menuState.CanDownload;
            downloadUpdateMenuItem.Text = "↓  " + menuState.DownloadUpdateText;
            exitApplicationMenuItem.Text = "×  退出程序";
            UpdateMenuVisuals.Apply(
                updateMenu,
                currentVersionMenuItem,
                checkUpdateMenuItem,
                downloadUpdateMenuItem,
                exitApplicationMenuItem,
                dpiScale);
            updateMenu.Show(Cursor.Position);
        }

        private void ConfirmExitApplication()
        {
            updateMenu.Close();
            DialogResult result = MessageBox.Show(
                "确定要退出 Codex Usage Overlay 吗？",
                "确认退出",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (result == DialogResult.Yes)
                Application.Exit();
        }

        private void DownloadReleaseUpdate()
        {
            UpdateMenuState menuState = OverlayInteraction.BuildUpdateMenuState(
                releaseUpdateService.Snapshot());
            if (!menuState.CanDownload)
                return;
            OpenExternalUrl(menuState.DownloadUrl);
            updateMenu.Close();
        }

        private void ShowReleaseUpdateBalloon(string message, ToolTipIcon icon)
        {
            releaseUpdateNotifyIcon.Visible = true;
            releaseUpdateNotifyIcon.ShowBalloonTip(
                8000, "Codex Usage Overlay", message, icon);
        }

        private void OpenReleaseUpdate()
        {
            OpenExternalUrl(releaseUpdateUrl);
            releaseUpdateNotifyIcon.Visible = false;
        }

        private void OpenRadarSource()
        {
            OpenExternalUrl(resetRadar == null ? String.Empty : resetRadar.SourceUrl);
        }

        private void OpenRunwayPage()
        {
            OpenExternalUrl(RunwayPageUrl);
        }

        private void RequestRadarRefresh()
        {
            resetRadarService.RequestRefresh(true);
            resetRadar = resetRadarService.Snapshot();
            lastRadarRevision = resetRadar.RevisionKey;
            RefreshInlinePanel();
        }

        private void RequestUsageAndRadarRefresh()
        {
            OverlaySettings activeSettings = settingsExpanded && draftSettings != null
                ? draftSettings
                : settings;
            service.RequestRefresh(activeSettings.RefreshSeconds, true);
            resetRadarService.RequestRefresh(true);
            resetRadar = resetRadarService.Snapshot();
            lastRadarRevision = resetRadar.RevisionKey;
            RefreshInlinePanel();
        }

        private void ShowUsageGuide()
        {
            resetRadarBanner.HideBanner();
            replayGuideRequested = true;
            if (GuideSessionActive)
            {
                replayGuideRequested = false;
                guideBubble.ResetToFirstPage();
                guideBubble.ApplyTheme(CurrentGuideSettings);
            }
            lastRenderedBounds = Rectangle.Empty;
        }

        private OverlaySettings CurrentGuideSettings
        {
            get
            {
                return settingsExpanded && draftSettings != null
                    ? draftSettings
                    : settings;
            }
        }

        private bool GuideSessionActive
        {
            get { return guideBubble != null && !guideBubble.IsDisposed; }
        }

        private void UpdateGuideBubble(Rectangle anchorBounds, Rectangle workingArea)
        {
            if (!GuideSessionActive && (pendingAutoGuide || replayGuideRequested))
                StartGuideSession(pendingAutoGuide);
            if (!GuideSessionActive)
                return;

            guideBubble.ApplyTheme(CurrentGuideSettings);
            guideBubble.UpdateAnchor(anchorBounds, workingArea);
            if (!guideBubble.Visible)
            {
                guideBubble.Show(this);
                guideBubble.UpdateAnchor(anchorBounds, workingArea);
            }
        }

        private void StartGuideSession(bool automatic)
        {
            pendingAutoGuide = false;
            replayGuideRequested = false;
            automaticGuideSession = automatic;
            guideBubble = new FirstRunGuideForm(CurrentGuideSettings);
            guideBubble.Dismissed += OnGuideDismissed;
            guideBubble.FormClosed += OnGuideClosed;
        }

        private void OnGuideDismissed(object sender, EventArgs e)
        {
            if (!automaticGuideSession)
                return;

            if (OverlaySettingsStore.MarkOnboardingCompleted())
            {
                settings.OnboardingCompleted = true;
                if (draftSettings != null)
                    draftSettings.OnboardingCompleted = true;
            }
            else
            {
                settings.OnboardingCompleted = false;
                MessageBox.Show(
                    "使用指引状态未能保存，下次启动时会再次显示。",
                    "Codex Usage Overlay",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void OnGuideClosed(object sender, FormClosedEventArgs e)
        {
            FirstRunGuideForm closed = sender as FirstRunGuideForm;
            if (closed != null)
            {
                closed.Dismissed -= OnGuideDismissed;
                closed.FormClosed -= OnGuideClosed;
            }
            if (ReferenceEquals(guideBubble, closed))
                guideBubble = null;
            automaticGuideSession = false;
            lastRenderedBounds = Rectangle.Empty;
        }

        private void HideGuideBubble()
        {
            if (GuideSessionActive && guideBubble.Visible)
                guideBubble.Hide();
        }

        private void DismissRadarBanner()
        {
            radarBannerDismissed = true;
            resetRadarBanner.HideBanner();
        }

        private static void OpenExternalUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.IsDefaultPort || !String.IsNullOrEmpty(uri.UserInfo) ||
                !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                return;
            bool isTiboStatus = String.Equals(uri.Host, "x.com", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(uri.AbsolutePath, @"^/thsottiaux/status/\d{1,30}$", RegexOptions.CultureInvariant);
            bool isRunwayPage = String.Equals(uri.Host, "www.codexrunway.com", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(uri.AbsolutePath, "/zh.html", StringComparison.Ordinal);
            bool isGitHubRelease = GitHubReleaseUpdateService.IsAllowedReleaseUrl(uri.AbsoluteUri);
            if (!isTiboStatus && !isRunwayPage && !isGitHubRelease)
                return;
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(uri.AbsoluteUri);
                start.UseShellExecute = true;
                Process.Start(start);
            }
            catch
            {
            }
        }

        private void ChooseInlineColor()
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = Color.FromArgb(draftSettings.CustomBackgroundArgb);
                dialog.FullOpen = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    draftSettings.CustomBackgroundArgb = Color.FromArgb(255, dialog.Color.R, dialog.Color.G, dialog.Color.B).ToArgb();
                    draftSettings.Theme = "Custom";
                    RefreshInlinePanel();
                }
            }
        }

        private void CloseInlineSettings(bool save)
        {
            int headerScreenTop = GetCurrentHeaderScreenTop();
            bool preserveBottomHeader = IsBottomCapsuleSettingsExpanded;
            if (save && draftSettings != null)
            {
                draftSettings.OnboardingCompleted = OverlaySettingsStore.MergeOnboardingCompleted(
                    draftSettings.OnboardingCompleted, settings.OnboardingCompleted);
                settings = draftSettings.Clone();
                OverlaySettingsStore.Save(settings);
                settingsRevision = OverlaySettingsStore.GetRevision();
                service.RequestRefresh(settings.RefreshSeconds, true);
                ApplyNotificationVisibility();
            }
            settingsExpanded = false;
            draftSettings = null;
            RefreshInlinePanel(preserveBottomHeader ? headerScreenTop : Int32.MinValue);
        }

        private void ReloadSettingsIfChanged()
        {
            if (settingsExpanded)
                return;
            string latestRevision = OverlaySettingsStore.GetRevision();
            if (String.Equals(latestRevision, settingsRevision, StringComparison.Ordinal))
                return;

            settings = OverlaySettingsStore.Load();
            settingsRevision = latestRevision;
            lastRenderedText = String.Empty;
            lastRenderedCapsuleRevision = String.Empty;
            lastRenderedBounds = Rectangle.Empty;
            service.RequestRefresh(settings.RefreshSeconds, true);
            ApplyNotificationVisibility();
        }

        private int GetCurrentHeaderScreenTop()
        {
            int offset = IsBottomCapsuleSettingsExpanded
                ? Height - ScalePixels(ActiveHeaderHeight)
                : 0;
            return Top + Math.Max(0, offset);
        }

        private void RefreshInlinePanel(int preservedHeaderScreenTop = Int32.MinValue)
        {
            lastRenderedText = String.Empty;
            lastRenderedCapsuleRevision = String.Empty;
            lastRenderedBounds = Rectangle.Empty;
            OverlaySettings displaySettings = settingsExpanded && draftSettings != null
                ? draftSettings
                : settings;
            int desiredHeight = ScalePixels(settingsExpanded
                ? ExpandedHeight
                : GetCollapsedHeaderHeight(displaySettings));
            if (Height != desiredHeight)
            {
                int desiredTop = Top;
                if (preservedHeaderScreenTop != Int32.MinValue &&
                    OverlayDisplayPositions.IsComposerPosition(displaySettings.DisplayPosition))
                {
                    desiredTop = OverlayInteraction.GetExpandedPanelTopFromHeader(
                        preservedHeaderScreenTop,
                        settingsExpanded
                            ? ScalePixels(GetCollapsedHeaderHeight(displaySettings))
                            : desiredHeight,
                        desiredHeight,
                        Int32.MinValue);
                }
                SetBounds(Left, desiredTop, Width, desiredHeight, BoundsSpecified.All);
            }
            int textWidth = Math.Max(40, ResetRadarBounds.Left - 14);
            UsageData usage = service.Snapshot();
            displayText = UsageDisplayText.Build(usage, textWidth);
            displayCapsuleTexts = displaySettings.DisplayPosition == OverlayDisplayPosition.ComposerInside
                ? UsageDisplayText.BuildComposerInsideCapsuleSections(usage)
                : UsageDisplayText.BuildCapsuleSections(usage);
            RenderLayered();
        }

        private static int InlineThemeIndex(string theme)
        {
            if (theme == "FrostedGlass") return 1;
            if (theme == "OrangeGradient") return 2;
            if (theme == "PinkGradient") return 3;
            if (theme == "LightCard") return 4;
            if (theme == "Custom") return 5;
            if (theme == "RainbowText") return 6;
            return 0;
        }

        private static string InlineThemeName(int index)
        {
            if (index == 1) return "FrostedGlass";
            if (index == 2) return "OrangeGradient";
            if (index == 3) return "PinkGradient";
            if (index == 4) return "LightCard";
            if (index == 5) return "Custom";
            if (index == 6) return "RainbowText";
            return "NeonBlue";
        }

        private static Brush CreateDisplayTextBrush(RectangleF bounds, Color fallback, bool rainbowText)
        {
            if (!rainbowText)
                return new SolidBrush(fallback);

            RectangleF gradientBounds = new RectangleF(bounds.X, bounds.Y, Math.Max(1f, bounds.Width), Math.Max(1f, bounds.Height));
            LinearGradientBrush gradient = new LinearGradientBrush(gradientBounds,
                Color.FromArgb(255, 255, 137, 47), Color.FromArgb(255, 70, 196, 255),
                LinearGradientMode.Horizontal);
            ColorBlend blend = new ColorBlend();
            blend.Positions = new[] { 0f, 0.34f, 0.68f, 1f };
            blend.Colors = new[]
            {
                Color.FromArgb(255, 255, 137, 47),
                Color.FromArgb(255, 255, 48, 145),
                Color.FromArgb(255, 158, 75, 255),
                Color.FromArgb(255, 70, 196, 255)
            };
            gradient.InterpolationColors = blend;
            return gradient;
        }

        private static Brush CreateGearBrush(Rectangle bounds, OverlaySettings visualSettings)
        {
            Color start;
            Color end;
            UiRendering.ResolveGearColors(
                visualSettings.Theme,
                visualSettings.CustomBackgroundArgb,
                out start,
                out end);
            return new LinearGradientBrush(bounds, start, end, LinearGradientMode.Horizontal);
        }

        private static string[] BuildFontOptions(string currentFont)
        {
            System.Collections.Generic.List<string> options = new System.Collections.Generic.List<string>();
            string[] preferred = new[]
            {
                UiRendering.NormalizeFontName(currentFont),
                "Microsoft YaHei UI", "Segoe UI", "SimSun", "Arial"
            };
            foreach (string candidate in preferred)
            {
                if (!UiRendering.IsSafeTextFontName(candidate) || options.Contains(candidate))
                    continue;
                foreach (FontFamily family in FontFamily.Families)
                {
                    if (String.Equals(family.Name, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        options.Add(family.Name);
                        break;
                    }
                }
            }
            if (options.Count == 0)
                options.Add("Microsoft YaHei UI");
            return options.ToArray();
        }

        private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rectangle.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rectangle.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rectangle.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Rendering is handled by UpdateLayeredWindow for real per-pixel alpha.
        }

    }

    internal static class CodexWindow
    {
        public static IntPtr Find()
        {
            IntPtr result = IntPtr.Zero;
            NativeMethods.EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindowText(hwnd) != "ChatGPT")
                    return true;

                uint processId;
                NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
                try
                {
                    Process process = Process.GetProcessById((int)processId);
                    if (!String.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    return true;
                }

                result = hwnd;
                return false;
            }, IntPtr.Zero);
            return result;
        }
    }

    internal static class CacheStore
    {
        public static UsageData Load(string path)
        {
            UsageData result = new UsageData();
            if (!File.Exists(path))
                return result;
            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int split = line.IndexOf('=');
                    if (split <= 0)
                        continue;
                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    int number;
                    long longNumber;
                    if (key == "Plan" && value.Length > 0) result.Plan = value;
                    else if (key == "ShortRemaining" && Int32.TryParse(value, out number)) result.ShortRemaining = number;
                    else if (key == "ShortReset" && value.Length > 0) result.ShortResetText = value;
                    else if (key == "WeeklyRemaining" && Int32.TryParse(value, out number)) result.WeeklyRemaining = number;
                    else if (key == "WeeklyReset" && value.Length > 0) result.WeeklyResetText = value;
                    else if (key == "RateLimitStatus" && value.Length > 0) result.RateLimitStatus = value;
                    else if (key == "AvailableResetCredits" && Int32.TryParse(value, out number)) result.AvailableResetCredits = number;
                    else if (key == "GeneralRemaining" && Int32.TryParse(value, out number)) result.WeeklyRemaining = number;
                    else if (key == "Reset" && value.Length > 0) result.WeeklyResetText = value;
                    else if (key == "ProfileTokensText" && value.Length > 0) result.ProfileTokensText = value;
                    else if (key == "LifetimeTokens" && Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out longNumber)) result.LifetimeTokens = longNumber;
                    else if (key == "UpdatedUtc")
                    {
                        DateTime updated;
                        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out updated)) result.UpdatedUtc = updated;
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        public static void Save(string path, UsageData data)
        {
            try
            {
                string temporary = path + ".tmp";
                string[] lines = new[]
                {
                    "Plan=" + data.Plan,
                    "ShortRemaining=" + (data.ShortRemaining.HasValue ? data.ShortRemaining.Value.ToString(CultureInfo.InvariantCulture) : String.Empty),
                    "ShortReset=" + data.ShortResetText,
                    "WeeklyRemaining=" + (data.WeeklyRemaining.HasValue ? data.WeeklyRemaining.Value.ToString(CultureInfo.InvariantCulture) : String.Empty),
                    "WeeklyReset=" + data.WeeklyResetText,
                    "RateLimitStatus=" + data.RateLimitStatus,
                    "AvailableResetCredits=" + (data.AvailableResetCredits.HasValue ? data.AvailableResetCredits.Value.ToString(CultureInfo.InvariantCulture) : String.Empty),
                    "ProfileTokensText=" + data.ProfileTokensText,
                    "LifetimeTokens=" + (data.LifetimeTokens.HasValue ? data.LifetimeTokens.Value.ToString(CultureInfo.InvariantCulture) : String.Empty),
                    "UpdatedUtc=" + data.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture)
                };
                File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch
            {
            }
        }
    }

    internal static class NativeMethods
    {
        internal const int WS_EX_TRANSPARENT = 0x20;
        internal const int WS_EX_TOOLWINDOW = 0x80;
        internal const int WS_EX_LAYERED = 0x00080000;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int SW_SHOWNOACTIVATE = 4;
        internal const int GWLP_HWNDPARENT = -8;
        internal const int WM_KEYDOWN = 0x0100;
        internal const int WM_KEYUP = 0x0101;
        internal const int WM_LBUTTONDOWN = 0x0201;
        internal const int VK_RIGHT = 0x27;
        internal const int VK_ESCAPE = 0x1B;
        internal const int ATTACH_PARENT_PROCESS = -1;
        internal const int WM_NCHITTEST = 0x0084;
        internal const int HTCLIENT = 1;
        internal const int HTTRANSPARENT = -1;
        internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        internal const int OBJID_WINDOW = 0;
        internal const uint WINEVENT_OUTOFCONTEXT = 0;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        internal delegate void WinEventDelegate(
            IntPtr hook,
            uint eventType,
            IntPtr hWnd,
            int idObject,
            int idChild,
            uint idEventThread,
            uint eventTime);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE
        {
            public int Width, Height;
            public SIZE(int width, int height) { Width = width; Height = height; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int valueSize);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr module,
            WinEventDelegate callback,
            uint processId,
            uint threadId,
            uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")]
        internal static extern bool PostMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr screenDc, ref POINT destination,
            ref SIZE size, IntPtr sourceDc, ref POINT source, int colorKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
        [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
        private static extern uint GetDpiForWindowNative(IntPtr hWnd);
        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr gdiObject);
        [DllImport("kernel32.dll")]
        internal static extern bool AttachConsole(int processId);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr newLong);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, int index, IntPtr newLong);

        internal static string GetWindowText(IntPtr hWnd)
        {
            StringBuilder text = new StringBuilder(512);
            GetWindowText(hWnd, text, text.Capacity);
            return text.ToString();
        }

        internal static float GetWindowDpiScale(IntPtr hWnd)
        {
            uint dpi = 96;
            try
            {
                dpi = GetDpiForWindowNative(hWnd);
            }
            catch
            {
                IntPtr dc = IntPtr.Zero;
                try
                {
                    dc = GetDC(hWnd);
                    if (dc != IntPtr.Zero)
                    {
                        int detectedDpi = GetDeviceCaps(dc, 88);
                        if (detectedDpi > 0)
                            dpi = (uint)detectedDpi;
                    }
                }
                catch
                {
                }
                finally
                {
                    if (dc != IntPtr.Zero)
                        ReleaseDC(hWnd, dc);
                }
            }

            if (dpi < 72 || dpi > 768)
                dpi = 96;
            return dpi / 96f;
        }

        internal static bool TryGetVisibleWindowRect(IntPtr hWnd, out RECT rect)
        {
            try
            {
                return DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect,
                    Marshal.SizeOf(typeof(RECT))) == 0;
            }
            catch
            {
                rect = new RECT();
                return false;
            }
        }

        internal static void SetOwner(IntPtr hWnd, IntPtr owner)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, GWLP_HWNDPARENT, owner);
            else SetWindowLong32(hWnd, GWLP_HWNDPARENT, owner);
        }

        internal static void MoveWindowWithoutActivation(IntPtr hWnd, Rectangle bounds)
        {
            if (hWnd == IntPtr.Zero || bounds.IsEmpty)
                return;
            SetWindowPos(hWnd, IntPtr.Zero, bounds.Left, bounds.Top,
                bounds.Width, bounds.Height,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        internal static void UpdateLayeredBitmap(IntPtr hWnd, Bitmap bitmap, int left, int top)
        {
            const int ULW_ALPHA = 2;
            const byte AC_SRC_OVER = 0;
            const byte AC_SRC_ALPHA = 1;

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memoryDc = CreateCompatibleDC(screenDc);
            IntPtr bitmapHandle = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
                previous = SelectObject(memoryDc, bitmapHandle);
                POINT destination = new POINT(left, top);
                POINT source = new POINT(0, 0);
                SIZE size = new SIZE(bitmap.Width, bitmap.Height);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;
                UpdateLayeredWindow(hWnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memoryDc, previous);
                if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

    }
}
