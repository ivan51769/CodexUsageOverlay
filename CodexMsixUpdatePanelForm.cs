using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Blues19.CodexInstaller;

namespace CodexUsageOverlay
{
    // A compact, anchored view of the embedded MSIX updater. The full updater
    // remains available for proxy and offline-package workflows, while the
    // common check/download/install path stays beside the overlay.
    internal sealed class CodexMsixUpdatePanelForm : Form
    {
        private readonly Logger log;
        private readonly Label stateLabel;
        private readonly Label installedLabel;
        private readonly Label latestLabel;
        private readonly Label detailLabel;
        private readonly ProgressBar progress;
        private readonly Button checkButton;
        private readonly Button updateButton;
        private readonly Button downloadButton;
        private readonly Button bitwardenButton;
        private readonly Button installButton;
        private readonly Button moreButton;
        private readonly Button closeButton;
        private readonly TextBox logBox;
        private readonly TableLayoutPanel rootLayout;
        private readonly TableLayoutPanel actionLayout;
        private readonly Button[] actionButtons;
        private readonly Func<CancellationToken, string> downloadBitwarden;
        private readonly Func<string, CancellationToken, bool> installBitwarden;
        private bool reflowingActions;
        private readonly ToolTip detailToolTip;
        private CancellationTokenSource cancel;
        private Thread worker;
        private PackageInfo latest;
        private string packagePath;
        private bool busy;
        private Color surfaceTop = Color.FromArgb(252, 253, 255);
        private Color surfaceBottom = Color.FromArgb(236, 242, 250);
        private Color accent = Color.FromArgb(96, 104, 211);
        private Color ink = Color.FromArgb(52, 61, 78);
        private Color secondaryInk = Color.FromArgb(73, 86, 105);
        private Color controlFill = Color.White;
        private Color controlBorder = Color.FromArgb(216, 224, 235);
        private Color selectedFill = Color.FromArgb(233, 240, 252);
        private float hostDpiScale = 1f;
        private string appliedThemeKey = String.Empty;
        // Keep the updater at the same logical canvas as the inline settings panel.
        private const int SettingsPanelLogicalWidth = 688;
        private const int SettingsPanelLogicalHeight = 446;

        internal CodexMsixUpdatePanelForm() : this(null, null) { }

        internal CodexMsixUpdatePanelForm(Func<CancellationToken, string> downloadBitwarden,
            Func<string, CancellationToken, bool> installBitwarden)
        {
            this.downloadBitwarden = downloadBitwarden ?? new Func<CancellationToken, string>(DownloadBitwardenPackage);
            this.installBitwarden = installBitwarden ?? new Func<string, CancellationToken, bool>(InstallBitwardenPackage);
            log = new Logger(true);
            log.Appended += OnLogAppended;
            // Let WinForms apply the Per-Monitor-V2 DPI factor once. Applying a
            // second host ratio here made text larger without reflowing the old
            // fixed-coordinate controls on some Windows installations.
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 9f, FontStyle.Regular);
            ClientSize = new Size(SettingsPanelLogicalWidth, SettingsPanelLogicalHeight);
            MinimumSize = Size.Empty;
            DoubleBuffered = true;
            KeyPreview = true;
            Padding = new Padding(16, 14, 16, 14);

            TableLayoutPanel root = new TableLayoutPanel();
            rootLayout = root;
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.Margin = Padding.Empty;
            root.Padding = Padding.Empty;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 6f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(root);

            TableLayoutPanel header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.AutoSize = true;
            header.ColumnCount = 2;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            FlowLayoutPanel heading = new FlowLayoutPanel();
            heading.AutoSize = true;
            heading.FlowDirection = FlowDirection.TopDown;
            heading.WrapContents = false;
            heading.Dock = DockStyle.Fill;
            heading.Margin = Padding.Empty;
            Label title = CreateLabel("Codex 桌面版更新", 11f, FontStyle.Bold, true);
            Label subtitle = CreateLabel("微软官方 MSIX · 安装包默认保存到桌面", 8.5f, FontStyle.Regular, true);
            subtitle.Name = "UpdaterSubtitle";
            title.Margin = new Padding(0, 0, 0, 2);
            subtitle.Margin = Padding.Empty;
            heading.Controls.Add(title);
            heading.Controls.Add(subtitle);
            header.Controls.Add(heading, 0, 0);

            closeButton = CreateCloseButton();
            closeButton.AutoSize = false;
            closeButton.Size = new Size(30, 30);
            closeButton.MinimumSize = closeButton.Size;
            closeButton.Margin = new Padding(10, 0, 0, 0);
            closeButton.Click += delegate { Hide(); };
            header.Controls.Add(closeButton, 1, 0);
            root.Controls.Add(header, 0, 0);

            TableLayoutPanel status = new TableLayoutPanel();
            status.AutoSize = true;
            status.Dock = DockStyle.Fill;
            status.ColumnCount = 3;
            status.Margin = new Padding(0, 10, 0, 0);
            status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            stateLabel = CreateLabel("准备就绪", 9.5f, FontStyle.Bold, true);
            installedLabel = CreateLabel("本机：待检查", 9f, FontStyle.Regular, false);
            latestLabel = CreateLabel("最新：待检查", 9f, FontStyle.Regular, false);
            stateLabel.Margin = new Padding(0, 0, 12, 0);
            installedLabel.Margin = new Padding(0, 0, 10, 0);
            latestLabel.Margin = Padding.Empty;
            status.Controls.Add(stateLabel, 0, 0);
            status.Controls.Add(installedLabel, 1, 0);
            status.Controls.Add(latestLabel, 2, 0);
            root.Controls.Add(status, 0, 1);

            detailLabel = CreateLabel("点击“检查版本”后获取官方最新包。", 9f, FontStyle.Regular, false);
            detailLabel.Dock = DockStyle.Fill;
            detailLabel.Margin = new Padding(0, 6, 0, 0);
            detailToolTip = new ToolTip();
            detailToolTip.SetToolTip(detailLabel, detailLabel.Text);
            root.Controls.Add(detailLabel, 0, 2);

            progress = new ProgressBar();
            progress.Dock = DockStyle.Fill;
            progress.Margin = new Padding(0, 6, 0, 0);
            progress.Style = ProgressBarStyle.Continuous;
            progress.AccessibleName = "下载进度";
            root.Controls.Add(progress, 0, 3);

            // Keep explicit cells so enlarged fonts cannot wrap just one label.
            // Narrow panels use two complete rows instead of clipping Bitwarden.
            TableLayoutPanel actions = new TableLayoutPanel();
            actionLayout = actions;
            actions.AutoSize = false;
            actions.Dock = DockStyle.Fill;
            actions.Margin = new Padding(0, 11, 0, 0);
            actions.Padding = Padding.Empty;
            actions.RowCount = 1;
            actions.ColumnCount = 6;
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            for (int index = 0; index < 6; index++)
                actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6));
            checkButton = CreateButton("检查版本", false);
            updateButton = CreateButton("一键更新", true);
            downloadButton = CreateButton("仅下载", false);
            bitwardenButton = CreateButton("安装 Bitwarden", false);
            bitwardenButton.AccessibleDescription = "从微软商店下载并校验后自动安装 Bitwarden";
            detailToolTip.SetToolTip(bitwardenButton, bitwardenButton.AccessibleDescription);
            installButton = CreateButton("安装桌面包", false);
            moreButton = CreateButton("更多选项", false);
            checkButton.Click += delegate { StartWork(CheckForUpdate); };
            updateButton.Click += delegate { StartWork(UpdateCodex); };
            downloadButton.Click += delegate { StartWork(DownloadPackage); };
            bitwardenButton.Click += delegate { StartWork(DownloadAndInstallBitwarden); };
            installButton.Click += delegate { StartWork(InstallPackage); };
            moreButton.Click += delegate { EmbeddedUpdaterHost.Show(this); };
            actions.Controls.Add(checkButton, 0, 0);
            actions.Controls.Add(updateButton, 1, 0);
            actions.Controls.Add(downloadButton, 2, 0);
            actions.Controls.Add(bitwardenButton, 3, 0);
            actions.Controls.Add(installButton, 4, 0);
            actions.Controls.Add(moreButton, 5, 0);
            actionButtons = new[] { checkButton, updateButton, downloadButton, bitwardenButton, installButton, moreButton };
            root.Controls.Add(actions, 0, 4);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BorderStyle = BorderStyle.None;
            logBox.Dock = DockStyle.Fill;
            logBox.MinimumSize = new Size(0, 64);
            logBox.Margin = new Padding(0, 12, 0, 0);
            logBox.Font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 8.5f, FontStyle.Regular);
            logBox.AccessibleName = "更新日志";
            root.Controls.Add(logBox, 0, 5);
            ReflowTextRows();
            log.Info("下载工具已展开。安装包默认保存到桌面。");
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= 0x00020000;
                return parameters;
            }
        }

        internal void ApplyTheme(OverlaySettings settings)
        {
            if (settings == null) return;
            string themeKey = (settings.Theme ?? String.Empty) + "|" +
                settings.CustomBackgroundArgb.ToString();
            if (String.Equals(themeKey, appliedThemeKey, StringComparison.Ordinal))
                return;
            appliedThemeKey = themeKey;
            // Use the same semantic roles as settings, including readable action ink.
            accent = UiRendering.PanelPrimaryColor(settings);
            secondaryInk = UiRendering.PanelSecondaryInkColor(settings);
            controlFill = UiRendering.PanelColor(settings, 4);
            controlBorder = UiRendering.PanelColor(settings, 2);
            selectedFill = UiRendering.PanelColor(settings, 5);
            surfaceTop = UiRendering.ResolveExpandedSettingsSurfaceColor(settings);
            surfaceBottom = surfaceTop;
            ink = UiRendering.ResolveExpandedSettingsInkColor(settings);
            BackColor = surfaceTop;
            ForeColor = ink;
            ApplySurfaceTheme(this);
            logBox.BackColor = controlFill;
            logBox.ForeColor = ink;
            ApplyButtonTheme(checkButton, false);
            ApplyButtonTheme(updateButton, true);
            ApplyButtonTheme(downloadButton, false);
            ApplyButtonTheme(bitwardenButton, false);
            ApplyButtonTheme(installButton, false);
            ApplyButtonTheme(moreButton, false);
            ApplyCloseButtonTheme();
            Invalidate();
        }

        private bool IsLightSurface
        {
            get { return surfaceTop.GetBrightness() >= 0.55f; }
        }

        private void ApplySurfaceTheme(Control control)
        {
            if (control == null) return;
            TableLayoutPanel table = control as TableLayoutPanel;
            FlowLayoutPanel flow = control as FlowLayoutPanel;
            if (table != null || flow != null)
                control.BackColor = surfaceTop;

            Label label = control as Label;
            if (label != null)
                label.ForeColor = ReferenceEquals(label, detailLabel) || label.Name == "UpdaterSubtitle"
                    ? secondaryInk : ink;

            foreach (Control child in control.Controls)
                ApplySurfaceTheme(child);
        }

        internal void UpdateAnchor(Rectangle overlayBounds, Rectangle workingArea, bool openAbove)
        {
            // Inherit both horizontal edges from the toolbar, before and after Show().
            // Do not snap back to the standalone preview width on the next tick.
            int preferredWidth = Math.Max(1, overlayBounds.Width);
            int preferredHeight = Visible
                ? (int)Math.Round(SettingsPanelLogicalHeight * hostDpiScale) : Height;
            int fittedWidth = Math.Min(preferredWidth, Math.Max(1, workingArea.Width - 8));
            int fittedHeight = Math.Min(preferredHeight, Math.Max(1, workingArea.Height - 8));
            if (Width != fittedWidth || Height != fittedHeight)
                Size = new Size(fittedWidth, fittedHeight);
            Rectangle target = OverlayInteraction.GetAttachedDownloadBounds(overlayBounds, Size, workingArea, openAbove);
            int left = target.Left;
            int top = target.Top;
            if (Left != left || Top != top)
                SetBounds(left, top, Width, Height, BoundsSpecified.Location);
        }

        internal void ApplyHostDpiScale(float scale)
        {
            // Retained for the overlay call site. Per-Monitor-V2 autoscaling owns
            // both bounds and fonts now; manually scaling here would double-scale
            // the text and reintroduce the clipped high-DPI layout.
            hostDpiScale = Math.Max(0.75f, Math.Min(3f, scale));
            ReflowTextRows();
        }

        internal void ScalePreviewForDpi(float scale)
        {
            // Offline preview only: simulate WinForms' control scaling without
            // changing the user's display settings.
            AutoScaleMode = AutoScaleMode.None;
            Scale(new SizeF(scale, scale));
            ScalePreviewFonts(this, scale);
            ClientSize = new Size((int)Math.Round(SettingsPanelLogicalWidth * scale),
                (int)Math.Round(SettingsPanelLogicalHeight * scale));
            ReflowTextRows();
        }

        private static void ScalePreviewFonts(Control control, float scale)
        {
            Font original = control.Font;
            control.Font = UiRendering.CreateTextFont(original.FontFamily.Name, original.SizeInPoints * scale,
                original.Style);
            foreach (Control child in control.Controls)
                ScalePreviewFonts(child, scale);
        }

        internal void ShowFailurePreview()
        {
            stateLabel.Text = "失败";
            installedLabel.Text = "本机：26.917.6896.0";
            latestLabel.Text = "最新：26.917.8451.0";
            detailLabel.Text = "连不上微软的下载服务器 dl.delivery.mp.microsoft.com。";
            detailToolTip.SetToolTip(detailLabel, detailLabel.Text);
            logBox.Text = "经依次尝试过 HTTP / HTTPS / 绝对域名写法，都不通。请开启你的代理或加速工具后重试，或点“网络设置”把代理地址填进去。";
        }

        internal void AssertPreviewLayout()
        {
            Button[] actions = { checkButton, updateButton, downloadButton,
                bitwardenButton, installButton, moreButton };
            foreach (Button button in actions)
            {
                Size textSize = TextRenderer.MeasureText(button.Text, button.Font);
                if (button.Height < button.Font.Height + button.Padding.Vertical + 4 ||
                    button.Width < textSize.Width + button.Padding.Horizontal)
                    throw new InvalidOperationException("DPI 布局裁切了按钮：" + button.Text);
            }
            if (logBox.Top < checkButton.Parent.Bottom)
                throw new InvalidOperationException("DPI 布局让日志区覆盖按钮。");
        }

        private void ReflowTextRows()
        {
            if (rootLayout == null || detailLabel == null || actionButtons == null || reflowingActions) return;
            reflowingActions = true;
            try
            {
                // TableLayoutPanel's absolute rows are pixels, not font-relative.
                // Recompute them after WinForms applies the monitor's DPI/font scale.
                rootLayout.RowStyles[2].Height = Math.Max(48, detailLabel.Font.Height * 3 + 10);
                int minimumCell = 0;
                foreach (Button button in actionButtons)
                    minimumCell = Math.Max(minimumCell, TextRenderer.MeasureText(button.Text, button.Font).Width + button.Padding.Horizontal + 4);
                int columns = ClientSize.Width - Padding.Horizontal >= minimumCell * 6 ? 6 : 3;
                int rows = actionButtons.Length / columns;
                if (actionLayout.ColumnCount != columns)
                {
                    actionLayout.SuspendLayout();
                    actionLayout.Controls.Clear();
                    actionLayout.ColumnStyles.Clear();
                    actionLayout.RowStyles.Clear();
                    actionLayout.ColumnCount = columns;
                    actionLayout.RowCount = rows;
                    for (int column = 0; column < columns; column++)
                        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
                    for (int row = 0; row < rows; row++)
                        actionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
                    for (int index = 0; index < actionButtons.Length; index++)
                        actionLayout.Controls.Add(actionButtons[index], index % columns, index / columns);
                    actionLayout.ResumeLayout();
                }
                int actionHeight = checkButton.Font.Height + checkButton.Padding.Vertical + 10;
                rootLayout.RowStyles[4].Height = Math.Max(44,
                    actionLayout.Margin.Top + actionHeight * rows);
            }
            finally { reflowingActions = false; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ReflowTextRows();
        }

        internal Rectangle OffsetForHostMove(int horizontalOffset, int verticalOffset)
        {
            return new Rectangle(Left + horizontalOffset, Top + verticalOffset, Width, Height);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle bounds = ClientRectangle;
            using (LinearGradientBrush background = new LinearGradientBrush(bounds,
                surfaceTop, surfaceBottom, LinearGradientMode.Vertical))
                e.Graphics.FillRectangle(background, bounds);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath border = RoundedRectangle(new Rectangle(0, 0,
                Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 12))
            using (Pen pen = new Pen(controlBorder, 1f))
                e.Graphics.DrawPath(pen, border);
            base.OnPaint(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (cancel != null) cancel.Cancel();
                log.Appended -= OnLogAppended;
                if (detailToolTip != null) detailToolTip.Dispose();
            }
            base.Dispose(disposing);
        }

        private void StartWork(Action<CancellationToken> action)
        {
            if (busy) return;
            if (cancel != null) cancel.Dispose();
            cancel = new CancellationTokenSource();
            SetBusy(true);
            worker = new Thread(delegate ()
            {
                try
                {
                    action(cancel.Token);
                }
                catch (OperationCanceledException)
                {
                    SetStatus("已取消", "操作已取消。", 0, false);
                    log.Warn("下载操作已取消。");
                }
                catch (Exception ex)
                {
                    SetStatus("失败", FirstLine(ex.Message), 0, false);
                    log.Error(ex.Message);
                }
                finally
                {
                    SetBusy(false);
                }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private void CheckForUpdate(CancellationToken token)
        {
            SetStatus("检查中", "正在读取本机与微软官方最新版本…", 0, true);
            InstalledPackage installed = AppxInstaller.GetInstalled(log);
            latest = StoreApi.FindLatestPackage(log, token);
            string installedText = installed == null ? "本机：未安装" : "本机：" + installed.Version;
            Ui(delegate
            {
                installedLabel.Text = installedText;
                latestLabel.Text = "最新：" + latest.VersionText;
            });
            bool current = installed != null && latest.Version != null && installed.Version >= latest.Version;
            SetStatus(current ? "已是最新" : "可更新", current
                ? "无需下载。" : "可更新到 " + latest.VersionText + "（" + latest.SizeText + "）。", 0, false);
            log.Success(current ? "Codex 已是最新版本。" : "已找到官方 MSIX 更新包：" + latest.FileName);
        }

        private void UpdateCodex(CancellationToken token)
        {
            CheckForUpdate(token);
            InstalledPackage installed = AppxInstaller.GetInstalled(log);
            if (installed != null && latest != null && installed.Version >= latest.Version) return;
            DownloadPackage(token);
            InstallPackage(token);
        }

        private void DownloadPackage(CancellationToken token)
        {
            EnsureLatest(token);
            StoreApi.PrepareDownload(latest, log, token);
            packagePath = latest.LocalPath(Paths.PackageDir);
            if (File.Exists(packagePath) && Downloader.LooksLikeMsix(packagePath) &&
                (latest.SizeBytes <= 0 || new FileInfo(packagePath).Length == latest.SizeBytes))
            {
                SetStatus("已下载", "桌面已有完整安装包。", 100, false);
                log.Success("复用桌面已有安装包：" + packagePath);
                return;
            }

            Downloader.EnsureDiskSpace(Paths.PackageDir, latest.SizeBytes,
                delegate (string message) { log.Warn(message); });
            SetStatus("下载中", "正在下载到桌面：" + latest.FileName, 0, false);
            Downloader downloader = new Downloader(
                delegate (string message) { log.Info(message); },
                delegate (Downloader.Progress progressInfo)
                {
                    int percent = progressInfo.Total <= 0 ? 0 : (int)Math.Round(
                        progressInfo.Downloaded * 100d / progressInfo.Total);
                    SetStatus("下载中", percent + "% · " + Fmt.Size(progressInfo.Downloaded) +
                        " / " + Fmt.Size(progressInfo.Total), percent, false);
                }, token);
            downloader.Download(latest.Url, packagePath, latest.SizeBytes,
                latest.DigestBase64, latest.DigestAlgorithm);
            SetStatus("已下载", "安装包已保存到桌面。", 100, false);
            log.Success("下载完成并已通过摘要校验：" + packagePath);
        }

        private void DownloadAndInstallBitwarden(CancellationToken token)
        {
            string destination = downloadBitwarden(token);
            token.ThrowIfCancellationRequested();
            SetStatus("安装 Bitwarden", "下载校验已完成，正在调用 Windows 安装服务…", 0, true);
            bool deferred = installBitwarden(destination, token);
            SetStatus(deferred ? "Bitwarden 待切换" : "Bitwarden 已安装", deferred
                ? "新版本已就绪，退出 Bitwarden 后会自动生效。"
                : "Bitwarden 安装完成。", 100, false);
        }

        private string DownloadBitwardenPackage(CancellationToken token)
        {
            SetStatus("Bitwarden", "正在查询 Bitwarden 官方商店安装包…", 0, true);
            PackageInfo package = StoreApi.FindLatestBitwardenPackage(log, token);
            if (String.IsNullOrWhiteSpace(package.DigestBase64) ||
                !(String.Equals(package.DigestAlgorithm, "SHA1", StringComparison.OrdinalIgnoreCase) ||
                  String.Equals(package.DigestAlgorithm, "SHA256", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("官方安装包缺少可验证的摘要，已停止自动安装。");
            log.Info("Bitwarden：" + package.FileName + "（" + package.SizeText + "）。");
            StoreApi.PrepareDownload(package, log, token);
            string destination = package.LocalPath(Paths.PackageDir);
            Downloader.EnsureDiskSpace(Paths.PackageDir, package.SizeBytes,
                delegate(string message) { log.Warn(message); });
            SetStatus("下载 Bitwarden", "正在下载到桌面：" + package.FileName, 0, false);
            var downloader = new Downloader(delegate(string message) { log.Info(message); },
                delegate(Downloader.Progress current)
                {
                    int percent = current.Total <= 0 ? 0 : (int)Math.Round(current.Downloaded * 100d / current.Total);
                    SetStatus("下载 Bitwarden", percent + "% · " + Fmt.Size(current.Downloaded) +
                        " / " + Fmt.Size(current.Total), percent, false);
                }, token);
            downloader.Download(package.Url, destination, package.SizeBytes, package.DigestBase64, package.DigestAlgorithm);
            log.Success("Bitwarden 已下载并通过摘要校验：" + destination);
            return destination;
        }

        private bool InstallBitwardenPackage(string destination, CancellationToken token)
        {
            return AppxInstaller.Install(destination, log,
                delegate(int percent) { SetStatus("安装 Bitwarden", "正在安装 " + percent + "%", percent, false); },
                delegate { return InUseDecision.DeferToNextExit; }, token, "Bitwarden");
        }

        private void InstallPackage(CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
                packagePath = FindDesktopPackage();
            if (String.IsNullOrWhiteSpace(packagePath))
                throw new FileNotFoundException("桌面没有可安装的 Codex MSIX 包，请先选择“仅下载”。");

            SetStatus("安装中", "正在调用 Windows MSIX 部署服务…", 0, true);
            bool deferred = AppxInstaller.Install(packagePath, log,
                delegate (int percent) { SetStatus("安装中", "正在安装 " + percent + "%", percent, false); },
                AskInUseDecision, token);
            SetStatus(deferred ? "等待生效" : "安装完成", deferred
                ? "完全退出 Codex 后会自动切换到新版本。"
                : "Codex 已完成更新。", 100, false);
        }

        private void EnsureLatest(CancellationToken token)
        {
            if (latest == null) CheckForUpdate(token);
            if (latest == null) throw new InvalidOperationException("未能获取最新安装包信息。");
        }

        private string FindDesktopPackage()
        {
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string file in Directory.GetFiles(Paths.PackageDir, "OpenAI.Codex*"))
            {
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if ((extension == ".msix" || extension == ".msixbundle") &&
                    Downloader.LooksLikeMsix(file) && File.GetLastWriteTimeUtc(file) > bestTime)
                {
                    best = file;
                    bestTime = File.GetLastWriteTimeUtc(file);
                }
            }
            return best;
        }

        private InUseDecision AskInUseDecision()
        {
            DialogResult answer = (DialogResult)Invoke(new Func<DialogResult>(delegate
            {
                return MessageBox.Show(this,
                    "Codex 正在运行。\n\n【是】立即关闭 Codex 并安装（未保存内容可能丢失）\n" +
                    "【否】下载完成后等待下次完全退出 Codex 再生效\n【取消】放弃本次安装",
                    "Codex 正在运行", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
            }));
            if (answer == DialogResult.Yes) return InUseDecision.ForceShutdown;
            return answer == DialogResult.No ? InUseDecision.DeferToNextExit : InUseDecision.Abort;
        }

        private void SetBusy(bool value)
        {
            busy = value;
            Ui(delegate
            {
                checkButton.Enabled = !value;
                updateButton.Enabled = !value;
                downloadButton.Enabled = !value;
                bitwardenButton.Enabled = !value;
                installButton.Enabled = !value;
                moreButton.Enabled = !value;
            });
        }

        private void SetStatus(string state, string detail, int percent, bool indeterminate)
        {
            Ui(delegate
            {
                stateLabel.Text = state;
                detailLabel.Text = detail;
                detailToolTip.SetToolTip(detailLabel, detail);
                progress.Style = indeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
                if (!indeterminate) progress.Value = Math.Max(progress.Minimum,
                    Math.Min(progress.Maximum, percent));
            });
        }

        private void OnLogAppended(LogEntry entry)
        {
            Ui(delegate
            {
                string line = "[" + entry.Time.ToString("HH:mm:ss") + "] " + entry.Message;
                logBox.AppendText(line + Environment.NewLine);
            });
        }

        private void ApplyButtonTheme(Button button, bool primary)
        {
            if (button == null) return;
            button.BackColor = primary ? accent : controlFill;
            button.ForeColor = primary ? Color.White : ink;
            button.FlatAppearance.BorderColor = primary ? accent : controlBorder;
            button.FlatAppearance.MouseOverBackColor = primary ? BlendSurface(accent, Color.Black, 24) : selectedFill;
            button.FlatAppearance.MouseDownBackColor = primary ? BlendSurface(accent, Color.Black, 40) : selectedFill;
        }

        private void ApplyCloseButtonTheme()
        {
            closeButton.BackColor = surfaceTop;
            closeButton.ForeColor = ink;
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.FlatAppearance.BorderColor = surfaceTop;
            closeButton.FlatAppearance.MouseOverBackColor = IsLightSurface
                ? Color.FromArgb(255, 235, 239, 243)
                : BlendSurface(surfaceTop, Color.White, 36);
            closeButton.FlatAppearance.MouseDownBackColor = IsLightSurface
                ? Color.FromArgb(255, 221, 228, 235)
                : BlendSurface(surfaceTop, Color.White, 64);
        }

        private static Color BlendSurface(Color surface, Color overlay, int overlayAlpha)
        {
            int alpha = Math.Max(0, Math.Min(255, overlayAlpha));
            int inverse = 255 - alpha;
            return Color.FromArgb(255,
                (surface.R * inverse + overlay.R * alpha) / 255,
                (surface.G * inverse + overlay.G * alpha) / 255,
                (surface.B * inverse + overlay.B * alpha) / 255);
        }

        private Label CreateLabel(string text, float size, FontStyle style, bool autoSize)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, size, style);
            label.BackColor = Color.Transparent;
            label.AutoSize = autoSize;
            label.AutoEllipsis = !autoSize;
            if (!autoSize)
            {
                label.Dock = DockStyle.Fill;
                label.MinimumSize = new Size(0, 22);
            }
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private Button CreateButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 8.5f, FontStyle.Regular);
            button.AutoSize = false;
            button.MinimumSize = Size.Empty;
            button.Padding = new Padding(3, 3, 3, 3);
            button.Margin = Padding.Empty;
            button.Dock = DockStyle.Fill;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.Cursor = Cursors.Hand;
            button.AccessibleName = text;
            ApplyButtonTheme(button, primary);
            return button;
        }

        private Button CreateCloseButton()
        {
            CodexIconButton button = new CodexIconButton();
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.Cursor = Cursors.Hand;
            button.TabStop = true;
            button.AccessibleName = "关闭更新面板";
            return button;
        }

        private void Ui(Action action)
        {
            if (IsDisposed || Disposing) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static string FirstLine(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "未知错误。";
            int newline = text.IndexOf('\n');
            return newline < 0 ? text : text.Substring(0, newline).Trim();
        }

        private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Max(1, radius * 2);
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
    }

    internal sealed class CodexIconButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int radius = Math.Max(4, Math.Min(7, Math.Min(Width, Height) / 4));
            int centerX = Width / 2;
            int centerY = Height / 2;
            using (Pen pen = new Pen(ForeColor, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                e.Graphics.DrawLine(pen, centerX - radius, centerY - radius,
                    centerX + radius, centerY + radius);
                e.Graphics.DrawLine(pen, centerX + radius, centerY - radius,
                    centerX - radius, centerY + radius);
            }
        }
    }
}
