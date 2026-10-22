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
        internal const string NewsSiteUrl = "https://news.blues19.com/";
        internal const string ProjectUrl = "https://github.com/ivan51769/CodexUsageOverlay";
        internal static readonly Color StarButtonFill = Color.FromArgb(255, 242, 196);
        internal static readonly Color StarButtonInk = Color.FromArgb(133, 78, 20);
        internal static readonly Color StarButtonHover = Color.FromArgb(255, 230, 166);
        internal static readonly Color StarButtonPressed = Color.FromArgb(255, 217, 128);

        internal static void DrawStarGlyph(Graphics graphics, RectangleF bounds)
        {
            using (Pen pen = new Pen(StarButtonInk, 1.4f))
            {
                PointF[] points = new PointF[10];
                for (int index = 0; index < points.Length; index++)
                {
                    double angle = -Math.PI / 2 + index * Math.PI / 5;
                    float radius = index % 2 == 0 ? 6.5f : 3f;
                    points[index] = new PointF(bounds.Left + bounds.Width / 2 + radius * (float)Math.Cos(angle),
                        bounds.Top + bounds.Height / 2 + radius * (float)Math.Sin(angle));
                }
                graphics.DrawPolygon(pen, points);
            }
        }

        internal static Form CreateProjectSupportForm(OverlaySettings settings)
        {
            Form form = new Form {
                Name = "ProjectSupportForm", Text = "支持开源项目",
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96f, 96f),
                ClientSize = new Size(460, 148), Padding = new Padding(16),
                BackColor = UiRendering.PanelColor(settings, 0), ForeColor = UiRendering.PanelColor(settings, 1),
                Font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 9f, FontStyle.Regular) };
            TableLayoutPanel layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            Label message = new Label {
                Name = "SupportMessage", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 12),
                Text = "如果您喜欢 Codex 用量与更新助手，在 GitHub 上点星可以帮助更多开发者发现它。" };
            TableLayoutPanel actions = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            Button star = new Button {
                Name = "OpenGitHubStarButton", Text = "在 GitHub 上点星", AccessibleName = "在 GitHub 上点星",
                DialogResult = DialogResult.OK, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0),
                FlatStyle = FlatStyle.Flat, BackColor = StarButtonFill, ForeColor = StarButtonInk,
                Font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 9f, FontStyle.Bold), Cursor = Cursors.Hand };
            star.FlatAppearance.BorderColor = StarButtonFill;
            star.FlatAppearance.MouseOverBackColor = StarButtonHover;
            star.FlatAppearance.MouseDownBackColor = StarButtonPressed;
            Bitmap starIcon = new Bitmap(16, 16);
            using (Graphics graphics = Graphics.FromImage(starIcon))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                DrawStarGlyph(graphics, new RectangleF(0, 0, 16, 16));
            }
            star.Image = starIcon;
            star.ImageAlign = ContentAlignment.MiddleLeft;
            star.Padding = new Padding(12, 0, 12, 0);
            form.Disposed += delegate { starIcon.Dispose(); };
            Button later = new Button {
                Name = "SupportLaterButton", Text = "稍后", AccessibleName = "稍后",
                DialogResult = DialogResult.Cancel, Dock = DockStyle.Fill, Margin = Padding.Empty,
                FlatStyle = FlatStyle.Flat, BackColor = UiRendering.PanelColor(settings, 4), ForeColor = form.ForeColor };
            later.FlatAppearance.BorderColor = UiRendering.PanelColor(settings, 2);
            later.FlatAppearance.MouseOverBackColor = UiRendering.PanelColor(settings, 5);
            actions.Controls.Add(star, 0, 0);
            actions.Controls.Add(later, 1, 0);
            layout.Controls.Add(message, 0, 0);
            layout.Controls.Add(actions, 0, 1);
            form.Controls.Add(layout);
            form.AcceptButton = star;
            form.CancelButton = later;
            return form;
        }

        internal static void ShowProjectSupport(IWin32Window owner, OverlaySettings settings)
        {
            using (Form form = CreateProjectSupportForm(settings))
                if (form.ShowDialog(owner) == DialogResult.OK) OpenExternalUrl(ProjectUrl);
        }

        internal static Image LoadWeChatQrCode()
        {
            using (System.IO.Stream stream = typeof(OverlayInteraction).Assembly.GetManifestResourceStream("CodexUsageOverlay.WeChatQr.jpg"))
            using (Image original = Image.FromStream(stream))
                return new Bitmap(original);
        }

        internal static Form CreateWeChatQrCodeForm()
        {
            Form form = new Form {
                Text = "拾玖说跨境AI · 公众号二维码", Name = "WeChatQrCodeForm",
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96f, 96f),
                ClientSize = new Size(368, 400), BackColor = Color.White,
                Font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 9f, FontStyle.Regular) };
            form.KeyPreview = true;
            form.KeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; form.Close(); }
            };
            Image image = LoadWeChatQrCode();
            form.Disposed += delegate { image.Dispose(); };
            PictureBox picture = new PictureBox {
                Name = "WeChatQrCodeImage", Dock = DockStyle.Fill, Image = image,
                SizeMode = PictureBoxSizeMode.Zoom, Padding = new Padding(12),
                AccessibleName = "拾玖说跨境AI公众号二维码" };
            Label caption = new Label {
                Dock = DockStyle.Bottom, Height = 40, TextAlign = ContentAlignment.MiddleCenter,
                Text = "微信扫一扫，关注「拾玖说跨境AI」", ForeColor = Color.FromArgb(52, 61, 78) };
            form.Controls.Add(picture);
            form.Controls.Add(caption);
            return form;
        }

        internal static void ShowWeChatQrCode(IWin32Window owner)
        {
            using (Form form = CreateWeChatQrCodeForm()) form.ShowDialog(owner);
        }

        internal static bool IsAllowedExternalUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.IsDefaultPort || !String.IsNullOrEmpty(uri.UserInfo) ||
                !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                return false;
            bool isTiboStatus = String.Equals(uri.Host, "x.com", StringComparison.OrdinalIgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/thsottiaux/status/\d{1,30}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            bool isRunwayPage = String.Equals(uri.Host, "didcodexreset.com", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(uri.AbsolutePath, "/zh.html", StringComparison.Ordinal);
            bool isGitHubRelease = GitHubReleaseUpdateService.IsAllowedReleaseUrl(uri.AbsoluteUri);
            bool isNewsSite = String.Equals(uri.Host, "news.blues19.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath == "/";
            bool isProject = uri.AbsoluteUri == ProjectUrl || uri.AbsoluteUri == ProjectUrl + "/";
            return isTiboStatus || isRunwayPage || isGitHubRelease || isNewsSite || isProject;
        }

        internal static void OpenExternalUrl(string url)
        {
            TryOpenExternalUrl(url);
        }

        internal static bool TryOpenExternalUrl(string url)
        {
            if (!IsAllowedExternalUrl(url)) return false;
            try
            {
                System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo(new Uri(url).AbsoluteUri);
                start.UseShellExecute = true;
                System.Diagnostics.Process.Start(start);
                return true;
            }
            catch { return false; }
        }

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

        internal static Rectangle GetContextStripAllowedBounds(Rectangle workArea, Rectangle window,
            Rectangle client, float scale)
        {
            if (client.IsEmpty) return Rectangle.Empty;
            Rectangle allowed = Rectangle.Intersect(workArea, Rectangle.Intersect(window, client));
            int inset = Math.Max(1, (int)Math.Ceiling(2 * scale));
            if (allowed.Width <= inset * 2 || allowed.Height <= inset * 2) return Rectangle.Empty;
            allowed.Inflate(-inset, -inset);
            return allowed;
        }

        internal static Rectangle GetContextStripPlacementBounds(Rectangle composer, Rectangle surface,
            Rectangle workArea, int preferredWidth, float scale, bool bothInside, Rectangle safeFooter = default(Rectangle),
            int measuredMinimumWidth = 0, int singleRowMinimumHeight = 0)
        {
            if (composer.IsEmpty || surface.IsEmpty || !surface.Contains(composer) || preferredWidth <= 0) return Rectangle.Empty;
            int minimumWidth = Math.Min(preferredWidth, measuredMinimumWidth > 0
                ? measuredMinimumWidth : (int)(240 * scale));
            int row = (int)Math.Round(20 * scale);
            int height = (int)Math.Round(42 * scale);
            int footerHeight = surface.Bottom - composer.Bottom;
            // Two readable rows are required; their decorative gap is optional on short native footers.
            int availableHeight = bothInside ? footerHeight : row + Math.Max(0, workArea.Bottom - surface.Bottom);
            if (availableHeight >= row * 2) height = Math.Min(height, availableHeight);
            // Symmetric clearance protects the native toolbar at either end of the composer.
            int width = Math.Min(preferredWidth, surface.Width - (int)Math.Ceiling(436 * scale));
            if (!safeFooter.IsEmpty && surface.Contains(safeFooter))
                width = Math.Min(preferredWidth, safeFooter.Width - (int)Math.Ceiling(8 * scale));
            int insideHeight = bothInside ? height : row;
            if (width >= minimumWidth && footerHeight >= insideHeight)
            {
                // The border is the anchor. Pulling both rows upward to fit the
                // window puts the header back onto the native button baseline.
                int top = surface.Bottom - insideHeight;
                Rectangle result = new Rectangle(surface.Left + (surface.Width - width) / 2, top, width, height);
                if (top >= composer.Bottom && workArea.Contains(result)) return result;
            }
            // Never flip above the editor or cover native controls.
            int outsideWidth = Math.Min(preferredWidth, surface.Width - (int)Math.Ceiling(16 * scale));
            Rectangle below = new Rectangle(surface.Left + (surface.Width - outsideWidth) / 2,
                surface.Bottom + (int)Math.Ceiling(2 * scale), outsideWidth, height);
            if (outsideWidth >= minimumWidth && workArea.Contains(below)) return below;
            // A browser split can leave only one readable line below the frame. Keep its
            // full header centered there; the token popup still exposes all session details.
            int minimumSingleHeight = singleRowMinimumHeight > 0 ? singleRowMinimumHeight : row;
            int singleHeight = Math.Min(row, workArea.Bottom - surface.Bottom);
            if (singleHeight >= minimumSingleHeight && outsideWidth >= minimumWidth)
            {
                Rectangle single = new Rectangle(surface.Left + (surface.Width - outsideWidth) / 2,
                    surface.Bottom, outsideWidth, singleHeight);
                if (workArea.Contains(single)) return single;
            }
            // When even one line cannot fit outside, use only the measured
            // center of the native footer, still flush with its bottom border.
            if (row >= minimumSingleHeight && width >= minimumWidth && footerHeight >= row)
            {
                Rectangle single = new Rectangle(surface.Left + (surface.Width - width) / 2,
                    surface.Bottom - row, width, row);
                if (workArea.Contains(single)) return single;
            }
            return Rectangle.Empty;
        }

        internal static Rectangle GetCenteredToolbarSpace(Rectangle composer, Rectangle surface,
            System.Collections.Generic.IList<Rectangle> controls)
        {
            int center = surface.Left + surface.Width / 2;
            int radius = surface.Width / 2;
            Rectangle footer = Rectangle.FromLTRB(surface.Left, composer.Bottom, surface.Right, surface.Bottom);
            foreach (Rectangle control in controls)
            {
                if (!control.IntersectsWith(footer)) continue;
                if (control.Left <= center && control.Right >= center) radius = 0;
                else if (control.Right < center) radius = Math.Min(radius, center - control.Right);
                else
                {
                    // The model selector can have an aria-hidden status glyph immediately before it.
                    // Reserve one toolbar-height slot even when that glyph is absent from UI Automation.
                    int leadingStatusSlot = Math.Min(control.Height, footer.Height);
                    radius = Math.Min(radius, Math.Max(0, control.Left - center - leadingStatusSlot));
                }
            }
            // Width=1 is an explicit blocked center, distinct from missing probe geometry.
            return new Rectangle(center - radius, footer.Top, Math.Max(1, radius * 2), Math.Max(1, footer.Height));
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
