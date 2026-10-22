using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

// Uses only owned fixture windows; no browser or user application is opened.
internal static class SettingsInteractionUiTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Assembly app = Assembly.LoadFrom(args[0]);
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                using (Form panel = (Form)Activator.CreateInstance(app.GetType("CodexUsageOverlay.CodexMsixUpdatePanelForm"), true))
                {
                    Call(panel, "ScalePreviewForDpi", scale);
                    Call(panel, "ApplyHostDpiScale", scale);
                    Rectangle work = new Rectangle(-4000, -2000, 8000, 6000);
                    foreach (int width in new[] { 720, 920, 1120 })
                    foreach (bool above in new[] { false, true })
                    {
                        Rectangle header = new Rectangle(300, 120, (int)(width * scale), (int)(28 * scale));
                        Call(panel, "UpdateAnchor", header, work, above);
                        Assert(panel.Left == header.Left && panel.Width == header.Width,
                            "Download panel does not inherit the toolbar edges before showing");
                        panel.Show();
                        Call(panel, "UpdateAnchor", header, work, above);
                        Assert(panel.Left == header.Left && panel.Width == header.Width,
                            "Visible download panel resets its width after opening");
                        panel.PerformLayout();
                        Call(panel, "AssertPreviewLayout");
                        Rectangle movedHeader = header;
                        movedHeader.Offset(80, 40);
                        Call(panel, "UpdateAnchor", movedHeader, work, above);
                        Assert(panel.Left == movedHeader.Left && panel.Width == header.Width,
                            "Download panel loses its width while following the host");
                        panel.Hide();
                    }
                    Call(panel, "UpdateAnchor", new Rectangle(0, 0, 2000, 28), new Rectangle(0, 0, 800, 600), false);
                    Assert(panel.Left >= 0 && panel.Right <= 800, "Download panel exceeds the screen");
                }
                object settings = Activator.CreateInstance(app.GetType("CodexUsageOverlay.OverlaySettings"), true);
                Set(settings, "OnboardingCompleted", true);
                object service = Activator.CreateInstance(app.GetType("CodexUsageOverlay.UsageService"), true);
                try
                {
                    using (Form host = new Form { Bounds = new Rectangle(100, 100, 1800, 900) })
                    using (Form overlay = (Form)Activator.CreateInstance(app.GetType("CodexUsageOverlay.OverlayForm"), All,
                        null, new[] { service, settings }, null))
                    {
                        ((Timer)Get(overlay, "timer")).Stop();
                        Set(overlay, "dpiScale", scale);
                        Set(overlay, "displayCapsuleTexts", new[] { "PRO 200", "5H: 无限", "周: 41%", "23亿" });
                        Set(overlay, "draftSettings", Call(settings, "Clone"));
                        Set(overlay, "settingsExpanded", true);
                        overlay.Bounds = new Rectangle(300, 120, (int)(1120 * scale), (int)(548 * scale));
                        Paint(overlay);
                        Rectangle gear = (Rectangle)Get(overlay, "GearBounds");
                        Point down = PixelCenter(gear, scale);
                        Message message = Message.Create(overlay.Handle, 0x0201, (IntPtr)1,
                            (IntPtr)((down.Y << 16) | (down.X & 0xffff)));
                        overlay.GetType().GetMethod("WndProc", All).Invoke(overlay, new object[] { message });
                        Assert((bool)Get(overlay, "settingsExpanded"),
                            "Settings changed layout on mouse-down, before the click completed");
                        overlay.Width = (int)(688 * scale);
                        Paint(overlay);
                        Point release = PixelCenter((Rectangle)Get(overlay, "MsixUpdaterBounds"), scale);
                        Call(overlay, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, release.X, release.Y, 0));
                        Assert((bool)Get(overlay, "settingsExpanded") && Get(overlay, "msixUpdatePanel") == null,
                            "A settings gesture was reassigned to the download control after layout moved");
                        Assert(!overlay.Capture, "Settings gesture retained mouse capture");

                        foreach (string theme in new[] { "PinkGradient", "RainbowText", "NeonBlue", "LightCard", "FrostedGlass", "OrangeGradient", "Custom" })
                        foreach (int toolbarLogicalWidth in new[] { 420, 720, 920, 1120 })
                        {
                            Set(settings, "Theme", theme);
                            Set(overlay, "settingsExpanded", false);
                            Set(overlay, "draftSettings", null);
                            Set(overlay, "codexWindow", host.Handle);
                            overlay.Bounds = new Rectangle(300, 120, (int)(toolbarLogicalWidth * scale), (int)(28 * scale));
                            Paint(overlay);
                            gear = (Rectangle)Get(overlay, "GearBounds");
                            int oldScreenX = overlay.Left + (int)Math.Round(gear.Left * scale);
                            Call(overlay, "RememberExpandedHeaderAnchor");
                            int logicalWidth = (int)Call(overlay, "GetPreferredSettingsLogicalWidth", (int)(1600 * scale), false);
                            Assert(logicalWidth == toolbarLogicalWidth, "Settings still replaces the clicked toolbar width with a fixed width");
                            Set(overlay, "settingsExpanded", true);
                            Set(overlay, "draftSettings", Call(settings, "Clone"));
                            var workArea = new Rectangle(-4000, -1000, 8000, 4000);
                            int expandedWidth = (int)(logicalWidth * scale);
                            int left = (int)Call(overlay, "GetExpandedTitleBarLeft", 200, expandedWidth,
                                host.Left, workArea, Get(overlay, "draftSettings"));
                            overlay.Bounds = new Rectangle(left, 120, expandedWidth, (int)(548 * scale));
                            overlay.Height = (int)((int)Get(overlay, "ActiveExpandedHeight") * scale);
                            Assert(left == 300, "Settings expansion shifted the toolbar left edge");
                            Paint(overlay);
                            gear = (Rectangle)Get(overlay, "GearBounds");
                            Assert(Math.Abs(overlay.Left + (int)Math.Round(gear.Left * scale) - oldScreenX) <= 1,
                                "Settings expansion moved the gear at DPI " + scale + " theme " + theme);
                            int movedLeft = (int)Call(overlay, "GetExpandedTitleBarLeft", 280, expandedWidth,
                                host.Left + 80, workArea, Get(overlay, "draftSettings"));
                            Assert(movedLeft == left + 80, "Settings anchor did not follow the host move");
                            Rectangle news = (Rectangle)Get(overlay, "NewsSiteBounds");
                            Rectangle qrEntry = (Rectangle)Get(overlay, "WeChatQrCodeBounds");
                            Assert(qrEntry.Left <= 24 && qrEntry.Width >= 128 && qrEntry.Height >= 128 &&
                                qrEntry.Width == qrEntry.Height && news.Left > qrEntry.Right && news.Height >= 60,
                                "Footer does not have a four-row QR at the left and a two-row news button");
                            Rectangle supportEntry = (Rectangle)Get(overlay, "ProjectSupportBounds");
                            Assert(news.Bottom <= supportEntry.Top && supportEntry.Bottom <= qrEntry.Bottom + 8,
                                "News and star buttons are not stacked beside the QR");
                            Assert(!news.IntersectsWith((Rectangle)Get(overlay, "ExitBounds")) &&
                                !news.IntersectsWith((Rectangle)Get(overlay, "GuideBounds")), "News link overlaps footer actions");
                            Assert(!qrEntry.IntersectsWith((Rectangle)Get(overlay, "ExitBounds")) &&
                                !qrEntry.IntersectsWith((Rectangle)Get(overlay, "SaveBounds")) &&
                                !qrEntry.IntersectsWith((Rectangle)Get(overlay, "CancelBounds")), "QR link overlaps footer actions");
                            Assert(news.Bottom <= (int)Get(overlay, "CanvasHeight") &&
                                ((Rectangle)Get(overlay, "SaveBounds")).Bottom <= (int)Get(overlay, "CanvasHeight"),
                                "Footer links or actions extend past the settings panel");
                            Assert(!((Rectangle)Get(overlay, "PublicAccountBounds")).IntersectsWith(
                                (Rectangle)Get(overlay, "WeChatQrCodeBounds")), "QR entry covers the public account name");
                            Assert(!((Rectangle)Get(overlay, "PublicAccountBounds")).IntersectsWith(
                                (Rectangle)Get(overlay, "ProjectSupportBounds")), "Support entry covers the public account name");
                            using (Bitmap bitmap = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                            {
                                foreach (string brandLine in new[] { "PublicAccountBounds", "AuthorBounds" })
                                {
                                    Rectangle area = (Rectangle)Get(overlay, brandLine);
                                    int colored = 0;
                                    for (int y = (int)(area.Top * scale); y < (int)(area.Bottom * scale); y++)
                                    for (int x = (int)(area.Left * scale); x < (int)(area.Right * scale); x++)
                                    {
                                        Color ink = bitmap.GetPixel(x, y);
                                        if (ink.A > 180 && ink.R > ink.G * 1.3 && ink.B > ink.G * 1.3) colored++;
                                    }
                                    Assert(colored > 6, "Original rainbow branding was removed from " + brandLine + " in " + theme);
                                }
                                if (theme == "NeonBlue")
                                {
                                    Rectangle pixels = Rectangle.FromLTRB((int)(gear.Left * scale), (int)(gear.Top * scale),
                                        (int)(gear.Right * scale), (int)(gear.Bottom * scale));
                                    bool readableIcon = false;
                                    for (int y = pixels.Top; y < pixels.Bottom; y++)
                                    for (int x = pixels.Left; x < pixels.Right; x++)
                                    {
                                        Color iconPixel = bitmap.GetPixel(x, y);
                                        if (iconPixel.G > 180 && iconPixel.B > 200 && iconPixel.A > 180) readableIcon = true;
                                    }
                                    Assert(readableIcon, "Dark toolbar gear is not readable");
                                }
                                Rectangle save = (Rectangle)Get(overlay, "SaveBounds");
                                Color starFill = bitmap.GetPixel((int)((supportEntry.Left + supportEntry.Width / 2) * scale),
                                    (int)((supportEntry.Top + 4) * scale));
                                Assert(starFill.R >= 240 && starFill.G >= 210 && starFill.B < 220,
                                    "Star entry is not yellow on " + theme);
                                Color fill = (Color)app.GetType("CodexUsageOverlay.UiRendering").GetMethod("PanelPrimaryColor", All)
                                    .Invoke(null, new[] { Get(overlay, "draftSettings") });
                                Color pixel = bitmap.GetPixel((int)((save.Left + save.Width / 2) * scale), (int)((save.Top + 4) * scale));
                                Assert(pixel.R == fill.R && pixel.G == fill.G && pixel.B == fill.B,
                                    "Save action lost its primary fill on " + theme);
                                if (scale == 1f && (toolbarLogicalWidth == 420 || toolbarLogicalWidth == 920) && args.Length > 1)
                                {
                                    using (Bitmap preview = new Bitmap(bitmap.Width, bitmap.Height))
                                    using (Graphics graphics = Graphics.FromImage(preview))
                                    {
                                        graphics.Clear(Color.White); graphics.DrawImageUnscaled(bitmap, 0, 0);
                                        preview.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(args[1]),
                                            "settings-polish-" + theme + "-" + toolbarLogicalWidth + ".png"));
                                    }
                                }
                            }
                            if (scale == 1f && theme == "PinkGradient" && toolbarLogicalWidth == 920 && args.Length > 1)
                            {
                                using (Bitmap bitmap = (Bitmap)Call(overlay, "BuildRenderedBitmap"))
                                using (Bitmap preview = new Bitmap(bitmap.Width, bitmap.Height))
                                using (Graphics graphics = Graphics.FromImage(preview))
                                { graphics.Clear(Color.White); graphics.DrawImageUnscaled(bitmap, 0, 0); preview.Save(args[1]); }
                                bool qrOpened = false;
                                using (Timer closeQr = new Timer { Interval = 50 })
                                {
                                    closeQr.Tick += delegate {
                                        Form popup = Application.OpenForms["WeChatQrCodeForm"];
                                        if (popup == null) return;
                                        PictureBox picture = (PictureBox)popup.Controls.Find("WeChatQrCodeImage", true)[0];
                                        Assert(picture.Image.Size == new Size(344, 344), "QR original dimensions changed");
                                        using (Bitmap preview = new Bitmap(popup.ClientSize.Width, popup.ClientSize.Height))
                                        {
                                            popup.DrawToBitmap(preview, popup.ClientRectangle);
                                            preview.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(args[1]), "wechat-qr-preview.png"));
                                        }
                                        qrOpened = true;
                                        Call(popup, "OnKeyDown", new KeyEventArgs(Keys.Escape));
                                    };
                                    closeQr.Start();
                                    Point qrPoint = PixelCenter((Rectangle)Get(overlay, "WeChatQrCodeBounds"), scale);
                                    Call(overlay, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, qrPoint.X, qrPoint.Y, 0));
                                    closeQr.Stop();
                                }
                                Assert(qrOpened, "QR entry did not open the scannable popup");
                                bool supportOpened = false;
                                using (Timer dismissSupport = new Timer { Interval = 50 })
                                {
                                    dismissSupport.Tick += delegate {
                                        Form popup = Application.OpenForms["ProjectSupportForm"];
                                        if (popup == null) return;
                                        supportOpened = true;
                                        ((Button)popup.Controls.Find("SupportLaterButton", true)[0]).PerformClick();
                                    };
                                    dismissSupport.Start();
                                    Point supportPoint = PixelCenter((Rectangle)Get(overlay, "ProjectSupportBounds"), scale);
                                    Call(overlay, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, supportPoint.X, supportPoint.Y, 0));
                                    dismissSupport.Stop();
                                }
                                Assert(supportOpened, "Support entry did not open its manual popup");
                            }
                        }
                        Set(overlay, "settingsExpanded", false);
                        Set(overlay, "draftSettings", null);
                        overlay.Bounds = new Rectangle(100, 120, (int)(720 * scale), (int)(28 * scale));
                        Paint(overlay);
                        Point downloadPoint = PixelCenter((Rectangle)Get(overlay, "MsixUpdaterBounds"), scale);
                        Call(overlay, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, downloadPoint.X, downloadPoint.Y, 0));
                        Form downloadPanel = (Form)Get(overlay, "msixUpdatePanel");
                        Assert(downloadPanel != null && downloadPanel.Visible && downloadPanel.Width == overlay.Width &&
                            downloadPanel.Left == overlay.Left, "Download click did not open an edge-aligned panel");
                        Assert((int)Call(overlay, "GetPreferredOverlayLogicalWidth", Call(service, "Snapshot")) == 720,
                            "Open download panel did not retain the clicked toolbar width");
                        Call(downloadPanel, "OnKeyDown", new KeyEventArgs(Keys.Escape));
                        Assert(!downloadPanel.Visible, "Escape does not dismiss the download panel");
                    }
                }
                finally { ((IDisposable)service).Dispose(); }
            }
            Type overlayType = app.GetType("CodexUsageOverlay.OverlayInteraction");
            object standaloneSettings = Activator.CreateInstance(app.GetType("CodexUsageOverlay.OverlaySettings"), true);
            using (Form standalone = (Form)Activator.CreateInstance(app.GetType("CodexUsageOverlay.SettingsForm"), All,
                null, new[] { standaloneSettings }, null))
            {
                standalone.Show();
                standalone.PerformLayout();
                foreach (string name in new[] { "NewsSiteLink", "WeChatQrCodeLink", "ProjectSupportLink" })
                {
                    Control link = standalone.Controls.Find(name, true)[0];
                    Assert(standalone.RectangleToScreen(standalone.ClientRectangle).Contains(
                        link.RectangleToScreen(link.ClientRectangle)), "Standalone settings clipped " + name);
                    Assert(link.Parent.ClientRectangle.Contains(link.Bounds), "Standalone settings footer clipped " + name);
                }
                Control group = standalone.Controls.Find("FooterLinks", true)[0];
                Assert(standalone.Controls.Find("NewsSiteLink", true)[0] is Button &&
                    standalone.Controls.Find("WeChatQrCodeLink", true)[0] is PictureBox &&
                    standalone.Controls.Find("NewsSiteLink", true)[0].Height >= 60 &&
                    standalone.Controls.Find("WeChatQrCodeLink", true)[0].Height >= 128,
                    "Standalone community area does not use the enlarged QR and two-row button");
                Rectangle groupScreen = group.RectangleToScreen(group.ClientRectangle);
                Rectangle settingsScreen = standalone.RectangleToScreen(standalone.ClientRectangle);
                Assert(Math.Abs(groupScreen.Left + groupScreen.Width / 2 - settingsScreen.Left - settingsScreen.Width / 2) <= 2,
                    "Standalone settings links are not centered");
                standalone.Close();
            }
            foreach (string supportTheme in new[] { "PinkGradient", "NeonBlue" })
            foreach (float supportScale in new[] { 1f, 1.5f, 2f })
            {
                Set(standaloneSettings, "Theme", supportTheme);
                using (Form popup = (Form)overlayType.GetMethod("CreateProjectSupportForm", All).Invoke(null, new[] { standaloneSettings }))
                {
                    Button starButton = (Button)popup.Controls.Find("OpenGitHubStarButton", true)[0];
                    Button laterButton = (Button)popup.Controls.Find("SupportLaterButton", true)[0];
                    popup.AutoScaleMode = AutoScaleMode.None;
                    popup.Scale(new SizeF(supportScale, supportScale));
                    Type rendering = app.GetType("CodexUsageOverlay.UiRendering");
                    popup.Font = (Font)rendering.GetMethod("CreateTextFont", All).Invoke(null,
                        new object[] { "Microsoft YaHei UI", 9f * supportScale, FontStyle.Regular });
                    starButton.Font = (Font)rendering.GetMethod("CreateTextFont", All).Invoke(null,
                        new object[] { "Microsoft YaHei UI", 9f * supportScale, FontStyle.Bold });
                    popup.Show();
                    popup.PerformLayout();
                    Control message = popup.Controls.Find("SupportMessage", true)[0];
                    Size messageText = TextRenderer.MeasureText(message.Text, message.Font,
                        new Size(message.Width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    Assert(messageText.Height <= message.Height, "Support message is clipped at " + supportScale);
                    Assert(popup.AcceptButton == starButton && popup.CancelButton == laterButton &&
                        starButton.DialogResult == DialogResult.OK && laterButton.DialogResult == DialogResult.Cancel,
                        "Support popup lost its explicit open or dismiss actions");
                    Assert(starButton.Image != null && !starButton.Bounds.IntersectsWith(laterButton.Bounds),
                        "Support popup actions overlap or the star icon is missing");
                    Assert(starButton.BackColor.R >= 240 && starButton.BackColor.G >= 210 && starButton.BackColor.B < 220,
                        "Star popup action is not reference yellow");
                    foreach (Button button in new[] { starButton, laterButton })
                    {
                        Size text = TextRenderer.MeasureText(button.Text, button.Font);
                        Assert(text.Height + button.Padding.Vertical <= button.Height &&
                            text.Width + button.Padding.Horizontal + (button.Image == null ? 0 : button.Image.Width + 8) <= button.Width,
                            "Support action text is clipped at " + supportScale);
                    }
                    if (args.Length > 1 && supportScale == 1f)
                        using (Bitmap preview = new Bitmap(popup.Width, popup.Height))
                        {
                            popup.DrawToBitmap(preview, new Rectangle(Point.Empty, popup.Size));
                            preview.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(args[1]), "support-popup-" + supportTheme + ".png"));
                        }
                    starButton.PerformClick();
                    Assert(popup.DialogResult == DialogResult.OK, "Star button did not select the repository action");
                    popup.Close();
                }
            }
            using (System.IO.Stream qr = app.GetManifestResourceStream("CodexUsageOverlay.WeChatQr.jpg"))
            using (System.Security.Cryptography.SHA256 hash = System.Security.Cryptography.SHA256.Create())
                Assert(BitConverter.ToString(hash.ComputeHash(qr)).Replace("-", "") ==
                    "1AA2E3E135A1732A4D6A74318F3D4BB465A5A450CF44FE33CD3CE2E0065DA12A", "Embedded QR differs from the supplied original");
            Assert((bool)overlayType.GetMethod("IsAllowedExternalUrl", All).Invoke(null,
                new object[] { "https://news.blues19.com/" }), "News URL was not allowed");
            Assert((bool)overlayType.GetMethod("IsAllowedExternalUrl", All).Invoke(null,
                new object[] { "https://github.com/ivan51769/CodexUsageOverlay" }), "Project URL was not allowed");
            foreach (string invalid in new[] { "http://news.blues19.com/", "https://news.blues19.com.evil.test/",
                "https://news.blues19.com/other", "https://news.blues19.com/?redirect=other",
                "https://github.com/ivan51769/other", "https://github.com.evil.test/ivan51769/CodexUsageOverlay",
                "https://github.com/ivan51769/CodexUsageOverlay?redirect=other" })
                Assert(!(bool)overlayType.GetMethod("IsAllowedExternalUrl", All).Invoke(null,
                    new object[] { invalid }), "News URL matching was too broad");
            Console.WriteLine("PASS inherited settings/download width, fixed edges, gesture isolation, QR popup/original, host moves and news link at 100-200 percent DPI");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Paint(object overlay) { using (Bitmap bitmap = (Bitmap)Call(overlay, "BuildRenderedBitmap")) { } }
    private static Point PixelCenter(Rectangle box, float scale)
    { return new Point((int)Math.Round((box.Left + box.Width / 2) * scale), (int)Math.Round((box.Top + box.Height / 2) * scale)); }
    private static object Get(object value, string name)
    { var field = value.GetType().GetField(name, All); return field != null ? field.GetValue(value) : value.GetType().GetProperty(name, All).GetValue(value, null); }
    private static void Set(object value, string name, object field) { value.GetType().GetField(name, All).SetValue(value, field); }
    private static object Call(object value, string name, params object[] args) { return value.GetType().GetMethod(name, All).Invoke(value, args); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
