using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            NativeMethods.EnablePerMonitorDpiAwareness();
            bool snapshot = Array.IndexOf(args, "--snapshot") >= 0;
            bool settingsOnly = Array.IndexOf(args, "--settings") >= 0;
            string previewOutput = null;
            const string previewPrefix = "--export-theme-previews=";
            foreach (string argument in args)
            {
                if (argument.StartsWith(previewPrefix, StringComparison.OrdinalIgnoreCase))
                    previewOutput = argument.Substring(previewPrefix.Length).Trim('"');
            }
            if (snapshot)
                NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
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
                            OverlaySettingsStore.Save(form.SelectedSettings);
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
                    Application.Run(new OverlayForm(service, settings));
                }
            }
            return 0;
        }
    }

    internal sealed class UsageData
    {
        public string Plan = "ChatGPT";
        public int? ShortRemaining;
        public string ShortResetText = "待刷新";
        public int? WeeklyRemaining;
        public string WeeklyResetText = "待刷新";
        public string RateLimitStatus = "待刷新";
        public int? AvailableResetCredits;
        public string ProfileTokensText = String.Empty;
        public long? LifetimeTokens;
        public string Source = "缓存";
        public string LastError = String.Empty;
        public DateTime UpdatedUtc = DateTime.MinValue;

        public UsageData Clone()
        {
            return (UsageData)MemberwiseClone();
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
            bool changed = false;
            lock (sync)
            {
                if (!String.IsNullOrWhiteSpace(incoming.Plan) && incoming.Plan != "ChatGPT" && data.Plan != incoming.Plan)
                {
                    data.Plan = incoming.Plan;
                    changed = true;
                }
                if (!String.IsNullOrWhiteSpace(incoming.RateLimitStatus) && incoming.RateLimitStatus != "待刷新")
                {
                    if (data.ShortRemaining != incoming.ShortRemaining) { data.ShortRemaining = incoming.ShortRemaining; changed = true; }
                    if (data.ShortResetText != incoming.ShortResetText) { data.ShortResetText = incoming.ShortResetText; changed = true; }
                    if (data.WeeklyRemaining != incoming.WeeklyRemaining) { data.WeeklyRemaining = incoming.WeeklyRemaining; changed = true; }
                    if (data.WeeklyResetText != incoming.WeeklyResetText) { data.WeeklyResetText = incoming.WeeklyResetText; changed = true; }
                    if (data.RateLimitStatus != incoming.RateLimitStatus) { data.RateLimitStatus = incoming.RateLimitStatus; changed = true; }
                    if (data.AvailableResetCredits != incoming.AvailableResetCredits) { data.AvailableResetCredits = incoming.AvailableResetCredits; changed = true; }
                }
                if (!String.IsNullOrWhiteSpace(incoming.ProfileTokensText) &&
                    incoming.ProfileTokensText != "待刷新" && data.ProfileTokensText != incoming.ProfileTokensText)
                {
                    data.ProfileTokensText = incoming.ProfileTokensText;
                    changed = true;
                }
                if (incoming.LifetimeTokens.HasValue && data.LifetimeTokens != incoming.LifetimeTokens)
                {
                    data.LifetimeTokens = incoming.LifetimeTokens;
                    changed = true;
                }
                if (!String.IsNullOrWhiteSpace(incoming.Source))
                    data.Source = incoming.Source;
                data.LastError = incoming.LastError ?? String.Empty;
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
        private readonly System.Windows.Forms.Timer timer;
        private OverlaySettings settings;
        private IntPtr codexWindow = IntPtr.Zero;
        private string displayText = "Codex 用量正在载入";
        private string lastRenderedText = String.Empty;
        private Rectangle lastRenderedBounds = Rectangle.Empty;
        private bool settingsExpanded;
        private bool gearHovered;
        private bool gearPressed;
        private OverlaySettings draftSettings;
        private readonly string[] fontOptions;
        private readonly Image brandLogo;
        private readonly CodexTaskStatusMonitor taskStatusMonitor;
        private CodexTaskState taskState = CodexTaskState.Unknown;
        private float dpiScale = 1f;

        private const int HeaderHeight = 28;
        private const int ExpandedHeight = 226;

        public OverlayForm(UsageService service, OverlaySettings settings)
        {
            this.service = service;
            this.settings = settings;
            fontOptions = BuildFontOptions(settings.FontName);
            brandLogo = LoadBrandLogo();
            taskStatusMonitor = new CodexTaskStatusMonitor();
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
            if (codexWindow == IntPtr.Zero || !NativeMethods.IsWindow(codexWindow))
            {
                codexWindow = CodexWindow.Find();
            }

            if (codexWindow == IntPtr.Zero || NativeMethods.IsIconic(codexWindow) ||
                !NativeMethods.IsWindowVisible(codexWindow) ||
                (!settingsExpanded && NativeMethods.GetForegroundWindow() != codexWindow))
            {
                Hide();
                return;
            }

            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(codexWindow, out rect))
            {
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
            int availableWidth = Math.Max(ScalePixels(240), windowWidth - ScalePixels(32));
            int overlayWidth = Math.Min(ScalePixels(620), availableWidth);
            int overlayLeft = rect.Left + (windowWidth - overlayWidth) / 2;
            int titleBarHeight = ScalePixels(36);
            int overlayHeight = ScalePixels(settingsExpanded ? ExpandedHeight : HeaderHeight);
            int visibleTitleBarTop = Math.Max(rect.Top, Screen.FromHandle(codexWindow).Bounds.Top);
            int overlayTop = visibleTitleBarTop + (titleBarHeight - ScalePixels(HeaderHeight)) / 2;
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

            service.RequestRefresh(settings.RefreshSeconds, false);

            UsageData usage = service.Snapshot();
            CodexTaskState newTaskState = taskStatusMonitor.Snapshot();
            bool taskStateChanged = newTaskState != taskState;
            taskState = newTaskState;
            int textWidth = Math.Max(140, TaskStatusBounds.Left - 14);
            displayText = BuildDisplayText(usage, textWidth);
            if (becameVisible || boundsChanged || dpiChanged || taskStateChanged || !String.Equals(displayText, lastRenderedText, StringComparison.Ordinal))
            {
                RenderLayered();
                lastRenderedText = displayText;
            }
        }

        private static string BuildDisplayText(UsageData usage, int availableTextWidth)
        {
            string planLabel = usage.Plan.ToUpperInvariant();
            bool hasQuotaData = usage.RateLimitStatus != "待刷新";
            string weeklyRemaining = FormatRemaining(usage.WeeklyRemaining, hasQuotaData);
            string tokensText = !String.IsNullOrWhiteSpace(usage.ProfileTokensText)
                ? usage.ProfileTokensText
                : "待刷新";

            System.Collections.Generic.List<string> sections = new System.Collections.Generic.List<string>();
            sections.Add(planLabel);
            if (availableTextWidth >= 500)
            {
                sections.Add("周用量剩余：" + weeklyRemaining + "·" + FormatResetText(usage.WeeklyResetText));
                if (IsAbnormalRateLimitStatus(usage.RateLimitStatus))
                    sections.Add("状态：" + usage.RateLimitStatus);
                if (usage.AvailableResetCredits.HasValue)
                    sections.Add("重置券：" + usage.AvailableResetCredits.Value.ToString(CultureInfo.InvariantCulture));
                sections.Add("累计Token：" + tokensText);
                return String.Join(" | ", sections.ToArray());
            }

            if (availableTextWidth >= 390)
            {
                sections.Clear();
                sections.Add(planLabel);
                sections.Add("周用量剩余：" + weeklyRemaining);
                if (usage.AvailableResetCredits.HasValue)
                    sections.Add("重置券：" + usage.AvailableResetCredits.Value.ToString(CultureInfo.InvariantCulture));
                sections.Add("累计Token：" + tokensText);
                return String.Join(" | ", sections.ToArray());
            }

            sections.Add("周用量剩余：" + weeklyRemaining);
            if (IsAbnormalRateLimitStatus(usage.RateLimitStatus))
                sections.Add(usage.RateLimitStatus);
            if (usage.AvailableResetCredits.HasValue)
                sections.Add("重置券：" + usage.AvailableResetCredits.Value.ToString(CultureInfo.InvariantCulture));
            sections.Add("累计Token：" + tokensText);
            return String.Join(" | ", sections.ToArray());
        }

        private static string FormatRemaining(int? remaining, bool hasQuotaData)
        {
            return remaining.HasValue
                ? remaining.Value.ToString(CultureInfo.InvariantCulture) + "%"
                : (hasQuotaData ? "—" : "待刷新");
        }

        private static bool IsAbnormalRateLimitStatus(string status)
        {
            return !String.IsNullOrWhiteSpace(status) && status != "正常" && status != "待刷新";
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

        private static string FormatResetText(string resetText)
        {
            if (String.IsNullOrWhiteSpace(resetText) || resetText == "—" || resetText == "待刷新")
                return resetText;
            return resetText.Replace(" ", String.Empty) + "重置";
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
            Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
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
                using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    Rectangle gear = GearBounds;
                    Rectangle status = TaskStatusBounds;
                    RectangleF box = new RectangleF(10, 1.5f, Math.Max(40, status.Left - 14), HeaderHeight - 2f);

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

                    DrawTaskStatus(graphics, taskState);

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
            float originalDpiScale = dpiScale;
            Size originalSize = Size;

            Directory.CreateDirectory(outputDirectory);
            try
            {
                displayText = "PRO | 周用量剩余：86%·7月29日09:07重置 | 重置券：0 | 累计Token：3.5亿";
                taskState = CodexTaskState.Completed;
                dpiScale = 1f;
                Width = 620;

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
            }
            finally
            {
                settings = originalSettings;
                draftSettings = originalDraft;
                displayText = originalText;
                settingsExpanded = originalExpanded;
                taskState = originalTaskState;
                dpiScale = originalDpiScale;
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
            using (StringFormat left = new StringFormat())
            using (StringFormat center = new StringFormat())
            {
                left.Alignment = StringAlignment.Near;
                left.LineAlignment = StringAlignment.Center;
                center.Alignment = StringAlignment.Center;
                center.LineAlignment = StringAlignment.Center;

                DrawInlineLabel(graphics, "字体", InlineRowBounds(0), labelFont, textBrush, left);
                Rectangle fontBox = InlineValueBounds(0);
                DrawInlineBox(graphics, fontBox, boxColor, borderColor);
                graphics.DrawString("‹", valueFont, textBrush, FontPreviousBounds, center);
                graphics.DrawString(visualSettings.FontName, valueFont, textBrush,
                    new Rectangle(fontBox.Left + 34, fontBox.Top, fontBox.Width - 68, fontBox.Height), center);
                graphics.DrawString("›", valueFont, textBrush, FontNextBounds, center);

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
                graphics.DrawString("−", valueFont, textBrush, RefreshMinusBounds, center);
                graphics.DrawString(visualSettings.RefreshSeconds.ToString(CultureInfo.InvariantCulture) + " 秒", valueFont, textBrush,
                    new Rectangle(refreshBox.Left + 42, refreshBox.Top, refreshBox.Width - 84, refreshBox.Height), center);
                graphics.DrawString("+", valueFont, textBrush, RefreshPlusBounds, center);

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
                DrawInlineBox(graphics, ExitBounds, Color.FromArgb(158, 225, 92, 104), Color.FromArgb(220, 255, 170, 178));
                using (Brush exitText = new SolidBrush(Color.White))
                using (StringFormat exitCenter = (StringFormat)center.Clone())
                {
                    exitCenter.FormatFlags |= StringFormatFlags.NoWrap;
                    graphics.DrawString("退出工具", valueFont, exitText, ExitBounds, exitCenter);
                }

                DrawInlineBox(graphics, CancelBounds, boxColor, borderColor);
                DrawInlineBox(graphics, SaveBounds, Color.FromArgb(85, textColor.R, textColor.G, textColor.B), borderColor);
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
        private Rectangle BrandLogoBounds { get { return new Rectangle(16, 140, 76, 76); } }
        private Rectangle PublicAccountBounds { get { return new Rectangle(102, 157, Math.Max(80, ExitBounds.Left - 110), 20); } }
        private Rectangle AuthorBounds { get { return new Rectangle(102, 179, Math.Max(80, ExitBounds.Left - 110), 20); } }
        private Rectangle ExitBounds { get { return new Rectangle(Math.Max(228, CanvasWidth - 208), 188, 60, 28); } }
        private Rectangle CancelBounds { get { return new Rectangle(Math.Max(296, CanvasWidth - 140), 188, 60, 28); } }
        private Rectangle SaveBounds { get { return new Rectangle(Math.Max(364, CanvasWidth - 72), 188, 60, 28); } }

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
            get { return new Rectangle(Math.Max(0, CanvasWidth - 34), 2, 30, HeaderHeight - 4); }
        }

        private Rectangle TaskStatusBounds
        {
            get { return new Rectangle(Math.Max(0, GearBounds.Left - 50), 5, 44, 18); }
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
            try
            {
                return new Font(visualSettings.FontName, 8.5f, FontStyle.Bold, GraphicsUnit.Point);
            }
            catch
            {
                return new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
            }
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
                bool interactive = GearBounds.Contains(client) ||
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
            if (e.Button == MouseButtons.Left && GearBounds.Contains(logicalLocation))
            {
                gearPressed = false;
                RefreshInlinePanel();
                return;
            }
            if (e.Button != MouseButtons.Left || !settingsExpanded || draftSettings == null)
                return;

            if (FontPreviousBounds.Contains(logicalLocation)) CycleFont(-1);
            else if (FontNextBounds.Contains(logicalLocation)) CycleFont(1);
            else if (BackgroundColorBounds.Contains(logicalLocation)) ChooseInlineColor();
            else if (RefreshMinusBounds.Contains(logicalLocation)) ChangeRefreshSeconds(-5);
            else if (RefreshPlusBounds.Contains(logicalLocation)) ChangeRefreshSeconds(5);
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

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hovered = GearBounds.Contains(ToLogicalPoint(e.Location));
            if (hovered != gearHovered)
            {
                gearHovered = hovered;
                RefreshInlinePanel();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (gearHovered || gearPressed)
            {
                gearHovered = false;
                gearPressed = false;
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
                CloseInlineSettings(false);
            else
            {
                draftSettings = settings.Clone();
                settingsExpanded = true;
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
                settings = draftSettings.Clone();
                OverlaySettingsStore.Save(settings);
                service.RequestRefresh(settings.RefreshSeconds, true);
            }
            settingsExpanded = false;
            draftSettings = null;
            RefreshInlinePanel();
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
            string[] preferred = new[] { currentFont, "Microsoft YaHei UI", "Segoe UI", "SimSun", "Arial" };
            foreach (string candidate in preferred)
            {
                if (String.IsNullOrWhiteSpace(candidate) || options.Contains(candidate))
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
        [DllImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
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

        internal static void EnablePerMonitorDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
                    return;
            }
            catch
            {
            }

            try
            {
                if (SetProcessDpiAwareness(2) == 0)
                    return;
            }
            catch
            {
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
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
