using System;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    internal sealed class OverlaySettings
    {
        public string FontName = "Microsoft YaHei UI";
        public string Theme = "NeonBlue";
        public int CustomBackgroundArgb = Color.FromArgb(24, 99, 171).ToArgb();
        public int RefreshSeconds = 15;
        public bool ResetNotificationsEnabled;

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
            OverlaySettings settings = new OverlaySettings();
            if (!File.Exists(SettingsPath))
                return settings;

            try
            {
                foreach (string line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    int split = line.IndexOf('=');
                    if (split <= 0)
                        continue;
                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    int number;
                    bool enabled;
                    if (key == "FontName" && value.Length > 0) settings.FontName = value;
                    else if (key == "Theme" && value.Length > 0) settings.Theme = value;
                    else if (key == "CustomBackgroundArgb" && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) settings.CustomBackgroundArgb = number;
                    else if (key == "RefreshSeconds" && Int32.TryParse(value, out number)) settings.RefreshSeconds = Math.Max(5, Math.Min(3600, number));
                    else if (key == "ResetNotificationsEnabled" && Boolean.TryParse(value, out enabled)) settings.ResetNotificationsEnabled = enabled;
                }
            }
            catch
            {
            }
            settings.FontName = UiRendering.NormalizeFontName(settings.FontName);
            return settings;
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

        public static void Save(OverlaySettings settings)
        {
            try
            {
                settings.FontName = UiRendering.NormalizeFontName(settings.FontName);
                string temporary = SettingsPath + ".tmp";
                string[] lines = new[]
                {
                    "FontName=" + settings.FontName,
                    "Theme=" + settings.Theme,
                    "CustomBackgroundArgb=" + settings.CustomBackgroundArgb.ToString(CultureInfo.InvariantCulture),
                    "RefreshSeconds=" + settings.RefreshSeconds.ToString(CultureInfo.InvariantCulture),
                    "ResetNotificationsEnabled=" + settings.ResetNotificationsEnabled.ToString(CultureInfo.InvariantCulture)
                };
                File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
                if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                File.Move(temporary, SettingsPath);
            }
            catch
            {
            }
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly ComboBox fontCombo;
        private readonly ComboBox themeCombo;
        private readonly NumericUpDown refreshSeconds;
        private readonly CheckBox resetNotifications;
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
            ClientSize = new Size(430, 285);
            Font = UiRendering.CreateTextFont("Microsoft YaHei UI", 9f, FontStyle.Regular);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(18);
            layout.ColumnCount = 2;
            layout.RowCount = 6;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
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
            themeCombo.Items.AddRange(new object[] { "荧光蓝", "透明磨砂玻璃", "渐变橙", "渐变粉", "自定义颜色", "渐变彩字" });
            themeCombo.SelectedIndex = ThemeIndex(current.Theme);
            themeCombo.SelectedIndexChanged += delegate { colorButton.Enabled = themeCombo.SelectedIndex == 4; };

            colorButton = new Button();
            colorButton.Dock = DockStyle.Left;
            colorButton.Width = 150;
            colorButton.Text = "选择背景颜色";
            colorButton.BackColor = Color.FromArgb(255, customColor.R, customColor.G, customColor.B);
            colorButton.Enabled = themeCombo.SelectedIndex == 4;
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
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            layout.SetColumnSpan(buttons, 2);
            layout.Controls.Add(buttons, 0, 5);

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

        private static int ThemeIndex(string theme)
        {
            if (theme == "FrostedGlass") return 1;
            if (theme == "OrangeGradient") return 2;
            if (theme == "PinkGradient") return 3;
            if (theme == "Custom") return 4;
            if (theme == "RainbowText") return 5;
            return 0;
        }

        private static string ThemeName(int index)
        {
            if (index == 1) return "FrostedGlass";
            if (index == 2) return "OrangeGradient";
            if (index == 3) return "PinkGradient";
            if (index == 4) return "Custom";
            if (index == 5) return "RainbowText";
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
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
