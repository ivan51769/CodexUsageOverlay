using System;
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
        private readonly System.Windows.Forms.Timer timer;
        private OverlaySettings settings;
        private IntPtr codexWindow = IntPtr.Zero;
        private string displayText = "Codex 用量正在载入";
        private string lastRenderedText = String.Empty;
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

        private const int HeaderHeight = 28;
        private const int ExpandedHeight = 310;
        private const string RunwayPageUrl = "https://www.codexrunway.com/zh.html";

        public OverlayForm(UsageService service, OverlaySettings settings)
        {
            this.service = service;
            this.settings = settings;
            pendingAutoGuide = !settings.OnboardingCompleted;
            settingsRevision = OverlaySettingsStore.GetRevision();
            fontOptions = BuildFontOptions(settings.FontName);
            brandLogo = LoadBrandLogo();
            taskStatusMonitor = new CodexTaskStatusMonitor();
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
            {
                codexWindow = CodexWindow.Find();
            }

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

            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(codexWindow, out rect))
            {
                resetRadarBanner.HideBanner();
                HideGuideBubble();
                Hide();
                return;
            }

            NativeMethods.RECT visibleRect;
            if (NativeMethods.TryGetVisibleWindowRect(codexWindow, out visibleRect))
                rect = visibleRect;

            int windowWidth = rect.Right - rect.Left;
            float newDpiScale = NativeMethods.GetWindowDpiScale(codexWindow);
            bool dpiChanged = Math.Abs(newDpiScale - dpiScale) > 0.01f;
            dpiScale = newDpiScale;
            UsageData usage = service.Snapshot();
            int availableWidth = Math.Max(ScalePixels(240), windowWidth - ScalePixels(32));
            int preferredOverlayWidth = GetPreferredOverlayLogicalWidth(usage);
            int overlayWidth = Math.Min(ScalePixels(preferredOverlayWidth), availableWidth);
            int overlayLeft = rect.Left + (windowWidth - overlayWidth) / 2;
            int titleBarHeight = ScalePixels(36);
            int overlayHeight = ScalePixels(settingsExpanded ? ExpandedHeight : HeaderHeight);
            Screen targetScreen = Screen.FromHandle(codexWindow);
            int visibleTitleBarTop = Math.Max(rect.Top, targetScreen.Bounds.Top);
            int overlayTop = visibleTitleBarTop + (titleBarHeight - ScalePixels(HeaderHeight)) / 2;
            bool showRadarBanner = !settingsExpanded && !radarBannerDismissed &&
                ResetRadarBannerForm.ShouldShow(resetRadar);
            int radarBannerHeight = ScalePixels(ResetRadarBannerForm.LogicalHeight);
            int radarBannerGap = ScalePixels(ResetRadarBannerForm.LogicalGap);
            int radarBannerWidth = Math.Min(overlayWidth, ScalePixels(ResetRadarBannerForm.LogicalWidth));
            int radarBannerLeft = overlayLeft + (overlayWidth - radarBannerWidth) / 2;
            int radarBannerTop = overlayTop - radarBannerHeight - radarBannerGap;
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
                    overlayLeft, overlayTop, overlayWidth, ScalePixels(HeaderHeight));
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
                radarClockChanged || !String.Equals(displayText, lastRenderedText, StringComparison.Ordinal))
            {
                RenderLayered();
                lastRenderedText = displayText;
                lastRadarClockRevision = radarClockRevision;
            }
        }

        private int GetPreferredOverlayLogicalWidth(UsageData usage)
        {
            const int defaultWidth = 720;
            const int maximumWidth = 920;
            const int chromeWidth = 218;
            string detailedText = UsageDisplayText.Build(usage, Int32.MaxValue);
            OverlaySettings visualSettings = settingsExpanded && draftSettings != null
                ? draftSettings
                : settings;
            string revision = visualSettings.FontName + "\n" + detailedText;
            if (String.Equals(revision, lastPreferredWidthRevision, StringComparison.Ordinal))
                return preferredOverlayLogicalWidth;

            using (Bitmap canvas = UiRendering.CreateLayeredBitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(canvas))
            using (Font font = CreateDisplayFont(visualSettings))
            using (StringFormat format = UiRendering.CreateTextFormat())
            {
                format.FormatFlags |= StringFormatFlags.NoWrap;
                int requiredWidth = (int)Math.Ceiling(
                    graphics.MeasureString(detailedText, font, Int32.MaxValue, format).Width) + chromeWidth;
                preferredOverlayLogicalWidth = Math.Max(defaultWidth,
                    Math.Min(maximumWidth, requiredWidth));
                lastPreferredWidthRevision = revision;
                return preferredOverlayLogicalWidth;
            }
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
            get { return updateAvailable && CanvasWidth >= 420; }
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

                if (rainbowText)
                {
                    if (settingsExpanded)
                    {
                        Rectangle settingsPanel = new Rectangle(1, HeaderHeight + 1,
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
                    for (int x = -glowRadius; x <= glowRadius; x++)
                    {
                        for (int y = -glowRadius; y <= glowRadius; y++)
                        {
                            if (x == 0 && y == 0)
                                continue;
                            int distance = Math.Abs(x) + Math.Abs(y);
                            int alpha = distance <= 2 ? glowColor.A : Math.Max(6, glowColor.A / 3);
                            using (Brush glow = new SolidBrush(Color.FromArgb(alpha, glowColor.R, glowColor.G, glowColor.B)))
                                graphics.DrawString(displayText, font, glow, new RectangleF(box.X + x, box.Y + y, box.Width, box.Height), format);
                        }
                    }

                    using (Brush text = CreateDisplayTextBrush(box, textColor, rainbowText))
                        graphics.DrawString(displayText, font, text, box, format);

                    DrawResetRadar(graphics, resetRadar, visualSettings);
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

                    if (gearHovered || gearPressed)
                    {
                        Color gearFillColor = gearPressed
                            ? Color.FromArgb(112, textColor.R, textColor.G, textColor.B)
                            : Color.FromArgb(58, textColor.R, textColor.G, textColor.B);
                        using (GraphicsPath gearHighlightPath = RoundedRectangle(GearBounds, 7))
                        using (Brush gearHighlight = new SolidBrush(gearFillColor))
                            graphics.FillPath(gearHighlight, gearHighlightPath);
                    }

                    using (Pen divider = new Pen(Color.FromArgb(70, textColor.R, textColor.G, textColor.B), 1f))
                        graphics.DrawLine(divider, gear.Left, 6, gear.Left, HeaderHeight - 6);
                    using (Font gearFont = new Font("Segoe MDL2 Assets", 10f, FontStyle.Regular, GraphicsUnit.Point))
                    using (Brush gearBrush = new SolidBrush(textColor))
                    using (StringFormat gearFormat = new StringFormat())
                    {
                        gearFormat.Alignment = StringAlignment.Center;
                        gearFormat.LineAlignment = StringAlignment.Center;
                        graphics.DrawString("\uE713", gearFont, gearBrush, gear, gearFormat);
                    }
                }

                if (settingsExpanded && draftSettings != null)
                    DrawInlineSettings(graphics, textColor, borderColor, visualSettings);
            }
            return bitmap;
        }

        public void ExportThemePreviews(string outputDirectory)
        {
            string[] themes = new[] { "NeonBlue", "FrostedGlass", "OrangeGradient", "PinkGradient", "RainbowText" };
            string[] names = new[] { "neon-blue", "frosted-glass", "orange-gradient", "pink-gradient", "rainbow-text" };
            OverlaySettings originalSettings = settings;
            OverlaySettings originalDraft = draftSettings;
            string originalText = displayText;
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
            using (Pen separator = new Pen(Color.FromArgb(75, borderColor.R, borderColor.G, borderColor.B), 1f))
                graphics.DrawLine(separator, 12, HeaderHeight + 2, CanvasWidth - 12, HeaderHeight + 2);

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
                string[] themeLabels = new[] { "荧光蓝", "磨砂", "渐变橙", "渐变粉", "自定义", "彩字" };
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
                graphics.DrawString("−", valueFont, textBrush,
                    new Rectangle(RefreshMinusBounds.Left, RefreshMinusBounds.Top - 1,
                        RefreshMinusBounds.Width, RefreshMinusBounds.Height), center);
                graphics.DrawString(visualSettings.RefreshSeconds.ToString(CultureInfo.InvariantCulture) + " 秒", valueFont, textBrush,
                    new Rectangle(refreshBox.Left + 42, refreshBox.Top, refreshBox.Width - 84, refreshBox.Height), center);
                graphics.DrawString("+", valueFont, textBrush,
                    new Rectangle(RefreshPlusBounds.Left, RefreshPlusBounds.Top - 1,
                        RefreshPlusBounds.Width, RefreshPlusBounds.Height), center);

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

        private static void DrawInlineLabel(Graphics graphics, string text, Rectangle row, Font font, Brush brush, StringFormat format, int labelLeft = 16)
        {
            int labelWidth = labelLeft >= 100 ? 76 : 94;
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

                graphics.FillEllipse(dotBrush, panel.Left + 10, panel.Top + 10, 7, 7);
                DateTimeOffset displayNow = resetRadarDisplayNow ?? DateTimeOffset.Now;
                string title = "TIBO RADAR · " +
                    ResetRadarDisplay.BuildHeadline(resetRadar, displayNow) +
                    ResetRadarDisplay.ConfidenceSuffix(resetRadar) + " · 非官方";
                graphics.DrawString(title, titleFont, titleBrush,
                    new Rectangle(panel.Left + 23, panel.Top + 4, Math.Max(40, ResetSourceBounds.Width - 25), 20), left);
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
            return new Rectangle(14, 36 + row * 34, CanvasWidth - 28, 27);
        }

        private Rectangle InlineValueBounds(int row)
        {
            Rectangle rowBounds = InlineRowBounds(row);
            return new Rectangle(116, rowBounds.Top, Math.Max(100, CanvasWidth - 132), rowBounds.Height);
        }

        private Rectangle FontPreviousBounds { get { Rectangle box = InlineValueBounds(0); return new Rectangle(box.Left, box.Top, 34, box.Height); } }
        private Rectangle FontNextBounds { get { Rectangle box = InlineValueBounds(0); return new Rectangle(box.Right - 34, box.Top, 34, box.Height); } }
        private Rectangle BackgroundLabelBounds { get { return new Rectangle(16, 104, 74, 27); } }
        private Rectangle BackgroundColorBounds { get { return new Rectangle(92, 104, 174, 27); } }
        private Rectangle RefreshLabelBounds { get { return new Rectangle(280, 104, 72, 27); } }
        private Rectangle RefreshValueBounds { get { return new Rectangle(354, 104, Math.Max(100, CanvasWidth - 370), 27); } }
        private Rectangle RefreshMinusBounds { get { Rectangle box = RefreshValueBounds; return new Rectangle(box.Left, box.Top, 38, box.Height); } }
        private Rectangle RefreshPlusBounds { get { Rectangle box = RefreshValueBounds; return new Rectangle(box.Right - 38, box.Top, 38, box.Height); } }
        private Rectangle ResetRadarPanelBounds { get { return new Rectangle(16, 140, Math.Max(180, CanvasWidth - 32), 46); } }
        private Rectangle ResetNotificationBounds { get { Rectangle panel = ResetRadarPanelBounds; return new Rectangle(panel.Right - 92, panel.Top + 9, 82, 28); } }
        private Rectangle ResetSourceBounds { get { Rectangle panel = ResetRadarPanelBounds; return new Rectangle(panel.Left, panel.Top, Math.Max(80, panel.Width - 100), panel.Height); } }
        private Rectangle BrandLogoBounds { get { return new Rectangle(16, 198, 64, 64); } }
        private Rectangle PublicAccountBounds { get { return new Rectangle(90, 207, Math.Max(80, ExitBounds.Left - 98), 20); } }
        private Rectangle AuthorBounds { get { return new Rectangle(90, 229, Math.Max(80, ExitBounds.Left - 98), 20); } }
        private Rectangle VersionBounds { get { return new Rectangle(90, 249, Math.Max(80, ExitBounds.Left - 98), 17); } }
        private Rectangle GuideBounds { get { return new Rectangle(16, 272, 82, 28); } }
        private Rectangle ExitBounds { get { return new Rectangle(Math.Max(228, CanvasWidth - 208), 272, 60, 28); } }
        private Rectangle CancelBounds { get { return new Rectangle(Math.Max(296, CanvasWidth - 140), 272, 60, 28); } }
        private Rectangle SaveBounds { get { return new Rectangle(Math.Max(364, CanvasWidth - 72), 272, 60, 28); } }

        private Rectangle ThemeChoiceBounds(int index)
        {
            Rectangle box = InlineValueBounds(1);
            int width = box.Width / 6;
            int left = box.Left + index * width;
            int right = index == 5 ? box.Right : left + width - 3;
            return new Rectangle(left, box.Top, Math.Max(1, right - left), box.Height);
        }

        private Rectangle GearBounds
        {
            get
            {
                int rightChrome = ShowUpdateIndicator ? 82 : 34;
                return new Rectangle(Math.Max(0, CanvasWidth - rightChrome), 2, 30, HeaderHeight - 4);
            }
        }

        private Rectangle UpdateIndicatorBounds
        {
            get
            {
                if (!ShowUpdateIndicator)
                    return Rectangle.Empty;
                Rectangle gear = GearBounds;
                return new Rectangle(gear.Right + 3, 5, 42, 18);
            }
        }

        private Rectangle TaskStatusBounds
        {
            get { return new Rectangle(Math.Max(0, GearBounds.Left - 50), 5, 44, 18); }
        }

        private Rectangle ResetRadarBounds
        {
            get
            {
                int width = CanvasWidth < 500 ? 22 : 104;
                return new Rectangle(Math.Max(0, TaskStatusBounds.Left - width - 6), 5, width, 18);
            }
        }

        private Rectangle RadarRefreshBounds
        {
            get
            {
                Rectangle radar = ResetRadarBounds;
                if (radar.Width <= 24)
                    return Rectangle.Empty;
                return new Rectangle(radar.Right - 20, radar.Top, 18, radar.Height);
            }
        }

        private Rectangle MainUsageBounds
        {
            get { return OverlayInteraction.GetMainUsageBounds(ResetRadarBounds.Left, HeaderHeight); }
        }

        private void DrawResetRadar(Graphics graphics, ResetRadarData radar, OverlaySettings visualSettings)
        {
            Rectangle bounds = ResetRadarBounds;
            Color fill;
            Color border;
            Color dot;
            GetResetRadarColors(radar.Status, out fill, out border, out dot);
            if (radarHovered)
                fill = Color.FromArgb(Math.Min(245, fill.A + 35), fill.R, fill.G, fill.B);

            using (GraphicsPath path = RoundedRectangle(bounds, 8))
            using (Brush fillBrush = new SolidBrush(fill))
            using (Pen borderPen = new Pen(border, 1f))
            {
                graphics.FillPath(fillBrush, path);
                graphics.DrawPath(borderPen, path);
            }

            int dotSize = bounds.Width <= 24 ? 8 : 6;
            int dotLeft = bounds.Width <= 24 ? bounds.Left + (bounds.Width - dotSize) / 2 : bounds.Left + 8;
            int dotTop = bounds.Top + (bounds.Height - dotSize) / 2;
            using (Brush dotBrush = new SolidBrush(dot))
            using (Pen pulse = new Pen(Color.FromArgb(130, dot.R, dot.G, dot.B), 1f))
            {
                graphics.DrawEllipse(pulse, dotLeft - 2, dotTop - 2, dotSize + 4, dotSize + 4);
                graphics.FillEllipse(dotBrush, dotLeft, dotTop, dotSize, dotSize);
            }

            if (bounds.Width > 24)
            {
                using (Font font = CreateDisplayFont(visualSettings, 8f))
                using (Brush text = new SolidBrush(Color.White))
                using (StringFormat format = UiRendering.CreateTextFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    string pillLabel = ResetRadarDisplay.BuildPillLabel(
                        radar,
                        resetRadarDisplayNow ?? DateTimeOffset.Now);
                    graphics.DrawString(pillLabel, font, text,
                        new Rectangle(bounds.Left + 17, bounds.Top, bounds.Width - 40, bounds.Height), format);
                }

                Rectangle refresh = RadarRefreshBounds;
                if (!refresh.IsEmpty)
                {
                    Color refreshFill = radarRefreshHovered
                        ? Color.FromArgb(100, 255, 255, 255)
                        : Color.FromArgb(42, 255, 255, 255);
                    using (Brush refreshBrush = new SolidBrush(refreshFill))
                    using (StringFormat refreshFormat = UiRendering.CreateTextFormat())
                    using (Font refreshFont = new Font("Segoe UI Symbol", 10f, FontStyle.Bold, GraphicsUnit.Point))
                    using (Brush refreshText = new SolidBrush(Color.White))
                    {
                        refreshFormat.Alignment = StringAlignment.Center;
                        refreshFormat.LineAlignment = StringAlignment.Center;
                        graphics.FillRectangle(refreshBrush, refresh);
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
            return UiRendering.CreateTextFont(visualSettings.FontName, size, FontStyle.Bold);
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
                    (settingsExpanded && client.Y >= HeaderHeight &&
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
            else if (GuideBounds.Contains(logicalLocation)) ShowUsageGuide();
            else if (ExitBounds.Contains(logicalLocation)) Application.Exit();
            else if (CancelBounds.Contains(logicalLocation)) CloseInlineSettings(false);
            else if (SaveBounds.Contains(logicalLocation)) CloseInlineSettings(true);
            else
            {
                for (int index = 0; index < 6; index++)
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
            bool refreshHovered = RadarRefreshBounds.Contains(logicalLocation);
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
                draftSettings = settings.Clone();
                settingsExpanded = true;
                resetRadarBanner.HideBanner();
                RefreshInlinePanel();
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
            RefreshInlinePanel();
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
            lastRenderedBounds = Rectangle.Empty;
            service.RequestRefresh(settings.RefreshSeconds, true);
            ApplyNotificationVisibility();
        }

        private void RefreshInlinePanel()
        {
            lastRenderedText = String.Empty;
            lastRenderedBounds = Rectangle.Empty;
            int desiredHeight = ScalePixels(settingsExpanded ? ExpandedHeight : HeaderHeight);
            if (Height != desiredHeight)
                SetBounds(Left, Top, Width, desiredHeight, BoundsSpecified.Height);
            RenderLayered();
        }

        private static int InlineThemeIndex(string theme)
        {
            if (theme == "FrostedGlass") return 1;
            if (theme == "OrangeGradient") return 2;
            if (theme == "PinkGradient") return 3;
            if (theme == "Custom") return 4;
            if (theme == "RainbowText") return 5;
            return 0;
        }

        private static string InlineThemeName(int index)
        {
            if (index == 1) return "FrostedGlass";
            if (index == 2) return "OrangeGradient";
            if (index == 3) return "PinkGradient";
            if (index == 4) return "Custom";
            if (index == 5) return "RainbowText";
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
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

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
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int command);
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
