using System;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal enum OverlayDisplayPosition
    {
        TitleBar,
        ComposerInside,
        ComposerBelow,
        BottomCapsules = ComposerInside
    }

    internal static class OverlayDisplayPositions
    {
        internal static bool IsComposerPosition(OverlayDisplayPosition position)
        {
            return position == OverlayDisplayPosition.ComposerInside ||
                position == OverlayDisplayPosition.ComposerBelow;
        }

        internal static int Index(OverlayDisplayPosition position)
        {
            if (position == OverlayDisplayPosition.ComposerInside)
                return 1;
            if (position == OverlayDisplayPosition.ComposerBelow)
                return 2;
            return 0;
        }

        internal static OverlayDisplayPosition FromIndex(int index)
        {
            if (index == 1)
                return OverlayDisplayPosition.ComposerInside;
            if (index == 2)
                return OverlayDisplayPosition.ComposerBelow;
            return OverlayDisplayPosition.TitleBar;
        }

        internal static string Label(OverlayDisplayPosition position)
        {
            if (position == OverlayDisplayPosition.ComposerInside)
                return "聊天对话框内";
            if (position == OverlayDisplayPosition.ComposerBelow)
                return "聊天对话框下面";
            return "顶部任务栏";
        }
    }

    internal enum BottomCapsuleStyle
    {
        Rounded,
        SmallRoundedRectangle,
        TextOnly
    }

    internal static class BottomCapsuleStyles
    {
        internal static string Label(BottomCapsuleStyle style)
        {
            if (style == BottomCapsuleStyle.Rounded)
                return "圆角";
            if (style == BottomCapsuleStyle.TextOnly)
                return "无胶囊";
            return "小圆角矩形";
        }

        internal static int Index(BottomCapsuleStyle style)
        {
            if (style == BottomCapsuleStyle.Rounded)
                return 0;
            if (style == BottomCapsuleStyle.TextOnly)
                return 2;
            return 1;
        }

        internal static BottomCapsuleStyle FromIndex(int index)
        {
            if (index == 0)
                return BottomCapsuleStyle.Rounded;
            if (index == 2)
                return BottomCapsuleStyle.TextOnly;
            return BottomCapsuleStyle.SmallRoundedRectangle;
        }
    }

    internal enum ComposerInsideLayout
    {
        OneLine,
        TwoLines
    }

    internal static class ComposerInsideLayouts
    {
        internal static string Label(ComposerInsideLayout layout)
        {
            return layout == ComposerInsideLayout.OneLine ? "一行概览" : "两行详情";
        }

        internal static int Index(ComposerInsideLayout layout)
        {
            return layout == ComposerInsideLayout.OneLine ? 0 : 1;
        }

        internal static ComposerInsideLayout FromIndex(int index)
        {
            return index == 0 ? ComposerInsideLayout.OneLine : ComposerInsideLayout.TwoLines;
        }
    }

    internal static class OverlayFontSizes
    {
        public const float Minimum = 6f;
        public const float DefaultTitleBar = 12f;
        public const float DefaultComposer = 7.2f;
        public const float TitleBarMaximum = 18f;
        public const float Step = 0.5f;

        public static float Clamp(float value, float fallback)
        {
            if (Single.IsNaN(value) || Single.IsInfinity(value))
                return fallback;
            return Math.Max(Minimum, Math.Min(TitleBarMaximum, value));
        }

        public static float ClampForPosition(
            OverlayDisplayPosition position,
            float value,
            float fallback)
        {
            float maximum = position == OverlayDisplayPosition.TitleBar
                ? TitleBarMaximum
                : 9f;
            if (Single.IsNaN(value) || Single.IsInfinity(value))
                return fallback;
            return Math.Max(Minimum, Math.Min(maximum, value));
        }

        public static float Get(OverlaySettings settings, OverlayDisplayPosition position)
        {
            if (position == OverlayDisplayPosition.ComposerInside)
                return settings.ComposerInsideFontSize;
            if (position == OverlayDisplayPosition.ComposerBelow)
                return settings.ComposerBelowFontSize;
            return settings.TitleBarFontSize;
        }

        public static void Set(OverlaySettings settings, OverlayDisplayPosition position, float value)
        {
            if (position == OverlayDisplayPosition.ComposerInside)
                settings.ComposerInsideFontSize = ClampForPosition(position, value, DefaultComposer);
            else if (position == OverlayDisplayPosition.ComposerBelow)
                settings.ComposerBelowFontSize = ClampForPosition(position, value, DefaultComposer);
            else
                settings.TitleBarFontSize = ClampForPosition(position, value, DefaultTitleBar);
        }
    }

    internal sealed class OverlaySettings
    {
        public string FontName = "Microsoft YaHei UI";
        public string Theme = "NeonBlue";
        public int CustomBackgroundArgb = Color.FromArgb(24, 99, 171).ToArgb();
        public int RefreshSeconds = 15;
        public bool ResetNotificationsEnabled;
        public OverlayDisplayPosition DisplayPosition = OverlayDisplayPosition.TitleBar;
        public float TitleBarFontSize = OverlayFontSizes.DefaultTitleBar;
        public float ComposerInsideFontSize = OverlayFontSizes.DefaultComposer;
        public float ComposerBelowFontSize = OverlayFontSizes.DefaultComposer;
        public BottomCapsuleStyle BottomCapsuleStyle = BottomCapsuleStyle.SmallRoundedRectangle;
        public ComposerInsideLayout ComposerInsideLayout = ComposerInsideLayout.TwoLines;
        public bool OnboardingCompleted;

        public OverlaySettings Clone()
        {
            return (OverlaySettings)MemberwiseClone();
        }
    }

    internal static class OverlaySettingsStore
    {
        private static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"); }
        }

        public static OverlaySettings Load()
        {
            return LoadFromPath(SettingsPath);
        }

        internal static OverlaySettings LoadFromPath(string path)
        {
            OverlaySettings settings = new OverlaySettings();
            if (!File.Exists(path))
                return settings;

            bool onboardingSettingFound = false;
            bool onboardingCompleted = false;
            bool titleBarFontSizeFound = false;
            bool fontSizeSettingsVersionFound = false;
            int fontSizeSettingsVersion = 0;
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
                    float fontSize;
                    bool enabled;
                    if (key == "FontName" && value.Length > 0) settings.FontName = value;
                    else if (key == "Theme" && value.Length > 0) settings.Theme = value;
                    else if (key == "CustomBackgroundArgb" && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) settings.CustomBackgroundArgb = number;
                    else if (key == "RefreshSeconds" && Int32.TryParse(value, out number)) settings.RefreshSeconds = Math.Max(5, Math.Min(3600, number));
                    else if (key == "ResetNotificationsEnabled" && Boolean.TryParse(value, out enabled)) settings.ResetNotificationsEnabled = enabled;
                    else if (key == "DisplayPosition")
                    {
                        OverlayDisplayPosition position;
                        if (Enum.TryParse<OverlayDisplayPosition>(value, true, out position))
                            settings.DisplayPosition = position;
                    }
                    else if (key == "TitleBarFontSize" &&
                        Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                            out fontSize))
                    {
                        titleBarFontSizeFound = true;
                        settings.TitleBarFontSize = OverlayFontSizes.ClampForPosition(
                            OverlayDisplayPosition.TitleBar, fontSize,
                            OverlayFontSizes.DefaultTitleBar);
                    }
                    else if (key == "FontSizeSettingsVersion" &&
                        Int32.TryParse(value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out number))
                    {
                        fontSizeSettingsVersionFound = true;
                        fontSizeSettingsVersion = number;
                    }
                    else if (key == "ComposerInsideFontSize" &&
                        Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                            out fontSize))
                    {
                        settings.ComposerInsideFontSize = OverlayFontSizes.ClampForPosition(
                            OverlayDisplayPosition.ComposerInside, fontSize,
                            OverlayFontSizes.DefaultComposer);
                    }
                    else if (key == "ComposerBelowFontSize" &&
                        Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                            out fontSize))
                    {
                        settings.ComposerBelowFontSize = OverlayFontSizes.ClampForPosition(
                            OverlayDisplayPosition.ComposerBelow, fontSize,
                            OverlayFontSizes.DefaultComposer);
                    }
                    else if (key == "BottomCapsuleStyle")
                    {
                        BottomCapsuleStyle style;
                        if (Enum.TryParse<BottomCapsuleStyle>(value, true, out style))
                            settings.BottomCapsuleStyle = style;
                    }
                    else if (key == "ComposerInsideLayout")
                    {
                        ComposerInsideLayout layout;
                        if (Enum.TryParse<ComposerInsideLayout>(value, true, out layout))
                            settings.ComposerInsideLayout = layout;
                    }
                    else if (key == "OnboardingCompleted" && Boolean.TryParse(value, out enabled))
                    {
                        onboardingSettingFound = true;
                        onboardingCompleted = enabled;
                    }
                }
            }
            catch
            {
            }
            settings.OnboardingCompleted = ResolveOnboardingCompleted(
                true, onboardingSettingFound, onboardingCompleted);
            if (!fontSizeSettingsVersionFound && titleBarFontSizeFound &&
                Math.Abs(settings.TitleBarFontSize - 8.5f) < 0.01f)
                settings.TitleBarFontSize = OverlayFontSizes.DefaultTitleBar;
            settings.FontName = UiRendering.NormalizeFontName(settings.FontName);
            return settings;
        }

        internal static bool ResolveOnboardingCompleted(
            bool settingsFileExists,
            bool settingFound,
            bool storedValue)
        {
            if (!settingsFileExists)
                return false;
            return settingFound ? storedValue : true;
        }

        internal static bool MergeOnboardingCompleted(bool draftValue, bool currentValue)
        {
            return draftValue || currentValue;
        }

        public static string GetRevision()
        {
            try
            {
                FileInfo file = new FileInfo(SettingsPath);
                if (!file.Exists)
                    return String.Empty;
                return file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" +
                    file.Length.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                return String.Empty;
            }
        }

        public static bool Save(OverlaySettings settings)
        {
            return SaveToPath(settings, SettingsPath);
        }

        public static bool SavePreservingCompletedOnboarding(OverlaySettings settings)
        {
            return SavePreservingCompletedOnboardingToPath(settings, SettingsPath);
        }

        internal static bool SavePreservingCompletedOnboardingToPath(
            OverlaySettings settings, string path)
        {
            OverlaySettings latest = LoadFromPath(path);
            settings.OnboardingCompleted = MergeOnboardingCompleted(
                settings.OnboardingCompleted, latest.OnboardingCompleted);
            return SaveToPath(settings, path);
        }

        public static bool MarkOnboardingCompleted()
        {
            OverlaySettings latest = Load();
            latest.OnboardingCompleted = true;
            return Save(latest);
        }

        internal static bool SaveToPath(OverlaySettings settings, string path)
        {
            string temporary = path + ".tmp";
            try
            {
                settings.FontName = UiRendering.NormalizeFontName(settings.FontName);
                string[] lines = new[]
                {
                    "FontName=" + settings.FontName,
                    "Theme=" + settings.Theme,
                    "CustomBackgroundArgb=" + settings.CustomBackgroundArgb.ToString(CultureInfo.InvariantCulture),
                    "RefreshSeconds=" + settings.RefreshSeconds.ToString(CultureInfo.InvariantCulture),
                    "ResetNotificationsEnabled=" + settings.ResetNotificationsEnabled.ToString(CultureInfo.InvariantCulture),
                    "DisplayPosition=" + settings.DisplayPosition.ToString(),
                    "FontSizeSettingsVersion=2",
                    "TitleBarFontSize=" + OverlayFontSizes.ClampForPosition(
                        OverlayDisplayPosition.TitleBar, settings.TitleBarFontSize,
                        OverlayFontSizes.DefaultTitleBar).ToString(
                            "0.0", CultureInfo.InvariantCulture),
                    "ComposerInsideFontSize=" + OverlayFontSizes.ClampForPosition(
                        OverlayDisplayPosition.ComposerInside, settings.ComposerInsideFontSize,
                        OverlayFontSizes.DefaultComposer).ToString("0.0", CultureInfo.InvariantCulture),
                    "ComposerBelowFontSize=" + OverlayFontSizes.ClampForPosition(
                        OverlayDisplayPosition.ComposerBelow, settings.ComposerBelowFontSize,
                        OverlayFontSizes.DefaultComposer).ToString("0.0", CultureInfo.InvariantCulture),
                    "BottomCapsuleStyle=" + settings.BottomCapsuleStyle.ToString(),
                    "ComposerInsideLayout=" + settings.ComposerInsideLayout.ToString(),
                    "OnboardingCompleted=" + settings.OnboardingCompleted.ToString(CultureInfo.InvariantCulture)
                };
                File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
                return true;
            }
            catch
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch
                {
                }
                return false;
            }
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly ComboBox fontCombo;
        private readonly ComboBox themeCombo;
        private readonly NumericUpDown refreshSeconds;
        private readonly CheckBox resetNotifications;
        private readonly ComboBox displayPositionCombo;
        private readonly NumericUpDown titleBarFontSize;
        private readonly NumericUpDown composerInsideFontSize;
        private readonly NumericUpDown composerBelowFontSize;
        private readonly ComboBox bottomCapsuleStyleCombo;
        private readonly ComboBox composerInsideLayoutCombo;
        private readonly Button colorButton;
        private Color customColor;

        public OverlaySettings SelectedSettings { get; private set; }

        public SettingsForm(OverlaySettings current)
        {
            SelectedSettings = current.Clone();
            customColor = Color.FromArgb(current.CustomBackgroundArgb);

            Text = "Codex 用量显示设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(430, 510);
            Font = UiRendering.CreateTextFont("Microsoft YaHei UI", 9f, FontStyle.Regular);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(18);
            layout.ColumnCount = 2;
            layout.RowCount = 12;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            fontCombo = new ComboBox();
            fontCombo.Dock = DockStyle.Fill;
            fontCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            using (InstalledFontCollection fonts = new InstalledFontCollection())
            {
                foreach (FontFamily family in fonts.Families)
                {
                    if (UiRendering.IsSafeTextFontName(family.Name))
                        fontCombo.Items.Add(family.Name);
                }
            }
            int fontIndex = fontCombo.FindStringExact(current.FontName);
            if (fontIndex < 0)
                fontIndex = fontCombo.FindStringExact("Microsoft YaHei UI");
            if (fontIndex < 0 && fontCombo.Items.Count > 0)
                fontIndex = 0;
            fontCombo.SelectedIndex = fontIndex;

            themeCombo = new ComboBox();
            themeCombo.Dock = DockStyle.Fill;
            themeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            themeCombo.Items.AddRange(new object[] { "荧光蓝", "透明磨砂玻璃", "渐变橙", "渐变粉", "轻盈白", "自定义颜色", "渐变彩字" });
            themeCombo.SelectedIndex = ThemeIndex(current.Theme);
            themeCombo.SelectedIndexChanged += delegate { colorButton.Enabled = themeCombo.SelectedIndex == 5; };

            colorButton = new Button();
            colorButton.Dock = DockStyle.Left;
            colorButton.Width = 150;
            colorButton.Text = "选择背景颜色";
            colorButton.BackColor = Color.FromArgb(255, customColor.R, customColor.G, customColor.B);
            colorButton.Enabled = themeCombo.SelectedIndex == 5;
            colorButton.Click += ChooseColor;

            refreshSeconds = new NumericUpDown();
            refreshSeconds.Minimum = 5;
            refreshSeconds.Maximum = 3600;
            refreshSeconds.Value = Math.Max(5, Math.Min(3600, current.RefreshSeconds));
            refreshSeconds.Width = 110;
            refreshSeconds.ThousandsSeparator = true;

            resetNotifications = new CheckBox();
            resetNotifications.Dock = DockStyle.Fill;
            resetNotifications.Text = "检测到新公告时显示 Windows 通知";
            resetNotifications.Checked = current.ResetNotificationsEnabled;

            displayPositionCombo = new ComboBox();
            displayPositionCombo.Dock = DockStyle.Fill;
            displayPositionCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            displayPositionCombo.Items.AddRange(new object[] { "顶部任务栏", "聊天对话框内", "聊天对话框下面" });
            displayPositionCombo.SelectedIndex = OverlayDisplayPositions.Index(current.DisplayPosition);

            titleBarFontSize = CreateFontSizeSelector(
                OverlayDisplayPosition.TitleBar, current.TitleBarFontSize);
            composerInsideFontSize = CreateFontSizeSelector(
                OverlayDisplayPosition.ComposerInside, current.ComposerInsideFontSize);
            composerBelowFontSize = CreateFontSizeSelector(
                OverlayDisplayPosition.ComposerBelow, current.ComposerBelowFontSize);

            composerInsideLayoutCombo = new ComboBox();
            composerInsideLayoutCombo.Dock = DockStyle.Fill;
            composerInsideLayoutCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            composerInsideLayoutCombo.Items.AddRange(new object[] { "一行概览", "两行详情" });
            composerInsideLayoutCombo.SelectedIndex = ComposerInsideLayouts.Index(
                current.ComposerInsideLayout);

            bottomCapsuleStyleCombo = new ComboBox();
            bottomCapsuleStyleCombo.Dock = DockStyle.Fill;
            bottomCapsuleStyleCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            bottomCapsuleStyleCombo.Items.AddRange(new object[] { "圆角", "小圆角矩形", "无胶囊" });
            bottomCapsuleStyleCombo.SelectedIndex = BottomCapsuleStyles.Index(current.BottomCapsuleStyle);

            layout.Controls.Add(CreateLabel("字体"), 0, 0);
            layout.Controls.Add(fontCombo, 1, 0);
            layout.Controls.Add(CreateLabel("外观预设"), 0, 1);
            layout.Controls.Add(themeCombo, 1, 1);
            layout.Controls.Add(CreateLabel("背景颜色"), 0, 2);
            layout.Controls.Add(colorButton, 1, 2);
            layout.Controls.Add(CreateLabel("自动刷新（秒）"), 0, 3);
            layout.Controls.Add(refreshSeconds, 1, 3);
            layout.Controls.Add(CreateLabel("重置雷达提醒"), 0, 4);
            layout.Controls.Add(resetNotifications, 1, 4);
            layout.Controls.Add(CreateLabel("显示位置"), 0, 5);
            layout.Controls.Add(displayPositionCombo, 1, 5);
            layout.Controls.Add(CreateLabel("顶部字号"), 0, 6);
            layout.Controls.Add(titleBarFontSize, 1, 6);
            layout.Controls.Add(CreateLabel("对话框内字号"), 0, 7);
            layout.Controls.Add(composerInsideFontSize, 1, 7);
            layout.Controls.Add(CreateLabel("对话框下字号"), 0, 8);
            layout.Controls.Add(composerBelowFontSize, 1, 8);
            layout.Controls.Add(CreateLabel("用量排版"), 0, 9);
            layout.Controls.Add(composerInsideLayoutCombo, 1, 9);
            layout.Controls.Add(CreateLabel("胶囊风格"), 0, 10);
            layout.Controls.Add(bottomCapsuleStyleCombo, 1, 10);
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Fill;
            Button save = new Button();
            save.Text = "保存";
            save.Width = 86;
            save.Click += SaveAndClose;
            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Width = 86;
            cancel.DialogResult = DialogResult.Cancel;
            Button guide = new Button();
            guide.Text = "使用指引";
            guide.Width = 86;
            guide.Click += delegate
            {
                using (FirstRunGuideForm form = new FirstRunGuideForm(SelectedSettings))
                {
                    form.Shown += delegate
                    {
                        Rectangle anchor = guide.RectangleToScreen(guide.ClientRectangle);
                        form.UpdateAnchor(anchor, Screen.FromControl(this).WorkingArea);
                    };
                    form.ShowDialog(this);
                }
            };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(guide);
            layout.SetColumnSpan(buttons, 2);
            layout.Controls.Add(buttons, 0, 11);

            AcceptButton = save;
            CancelButton = cancel;
        }

        private static Label CreateLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static NumericUpDown CreateFontSizeSelector(
            OverlayDisplayPosition position,
            float value)
        {
            NumericUpDown selector = new NumericUpDown();
            selector.Minimum = (decimal)OverlayFontSizes.Minimum;
            selector.Maximum = position == OverlayDisplayPosition.TitleBar
                ? (decimal)OverlayFontSizes.TitleBarMaximum
                : 9M;
            selector.DecimalPlaces = 1;
            selector.Increment = (decimal)OverlayFontSizes.Step;
            selector.Value = (decimal)OverlayFontSizes.ClampForPosition(position, value,
                position == OverlayDisplayPosition.TitleBar
                    ? OverlayFontSizes.DefaultTitleBar
                    : OverlayFontSizes.DefaultComposer);
            selector.Width = 110;
            return selector;
        }

        private static int ThemeIndex(string theme)
        {
            if (theme == "FrostedGlass") return 1;
            if (theme == "OrangeGradient") return 2;
            if (theme == "PinkGradient") return 3;
            if (theme == "LightCard") return 4;
            if (theme == "Custom") return 5;
            if (theme == "RainbowText") return 6;
            return 0;
        }

        private static string ThemeName(int index)
        {
            if (index == 1) return "FrostedGlass";
            if (index == 2) return "OrangeGradient";
            if (index == 3) return "PinkGradient";
            if (index == 4) return "LightCard";
            if (index == 5) return "Custom";
            if (index == 6) return "RainbowText";
            return "NeonBlue";
        }

        private void ChooseColor(object sender, EventArgs e)
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = customColor;
                dialog.FullOpen = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    customColor = dialog.Color;
                    colorButton.BackColor = dialog.Color;
                }
            }
        }

        private void SaveAndClose(object sender, EventArgs e)
        {
            SelectedSettings.FontName = UiRendering.NormalizeFontName(
                fontCombo.SelectedItem == null ? "Microsoft YaHei UI" : fontCombo.SelectedItem.ToString());
            SelectedSettings.Theme = ThemeName(themeCombo.SelectedIndex);
            SelectedSettings.CustomBackgroundArgb = Color.FromArgb(255, customColor.R, customColor.G, customColor.B).ToArgb();
            SelectedSettings.RefreshSeconds = Decimal.ToInt32(refreshSeconds.Value);
            SelectedSettings.ResetNotificationsEnabled = resetNotifications.Checked;
            SelectedSettings.DisplayPosition = OverlayDisplayPositions.FromIndex(
                displayPositionCombo.SelectedIndex);
            SelectedSettings.TitleBarFontSize = OverlayFontSizes.ClampForPosition(
                OverlayDisplayPosition.TitleBar, (float)titleBarFontSize.Value,
                OverlayFontSizes.DefaultTitleBar);
            SelectedSettings.ComposerInsideFontSize = OverlayFontSizes.ClampForPosition(
                OverlayDisplayPosition.ComposerInside, (float)composerInsideFontSize.Value,
                OverlayFontSizes.DefaultComposer);
            SelectedSettings.ComposerBelowFontSize = OverlayFontSizes.ClampForPosition(
                OverlayDisplayPosition.ComposerBelow, (float)composerBelowFontSize.Value,
                OverlayFontSizes.DefaultComposer);
            SelectedSettings.BottomCapsuleStyle = BottomCapsuleStyles.FromIndex(
                bottomCapsuleStyleCombo.SelectedIndex);
            SelectedSettings.ComposerInsideLayout = ComposerInsideLayouts.FromIndex(
                composerInsideLayoutCombo.SelectedIndex);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
