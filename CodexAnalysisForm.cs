using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CodexUsageOverlay
{
    // Reads only local rollout metadata. Conversation bodies are never parsed,
    // retained, displayed, or sent anywhere by this form.
    internal sealed class CodexAnalysisForm : Form
    {
        private const int LogicalWidth = 640;
        private readonly UsageData usage;
        private readonly CodexTaskState taskState;
        private readonly OverlaySettings settings;
        private readonly Panel contentHost;
        private readonly Label sourceNote;
        private readonly Button[] tabButtons = new Button[4];
        private readonly Panel[] pages = new Panel[4];
        private readonly Label[] taskValues = new Label[3];
        private readonly Label[] toolValues = new Label[3];
        private readonly Label[] modelValues = new Label[3];
        private readonly ActivityChart[] charts = new ActivityChart[3];
        private readonly FlowLayoutPanel[] modelLists = new FlowLayoutPanel[3];
        private Color surface;
        private Color card;
        private Color ink;
        private Color muted;
        private Color line;
        private Color accent;
        private Color accentSoft;
        private CodexAnalyticsSnapshot snapshot;
        private int selectedPage;

        internal CodexAnalysisForm(UsageData currentUsage, CodexTaskState currentTaskState,
            OverlaySettings currentSettings)
            : this(currentUsage, currentTaskState, currentSettings, null)
        {
        }

        internal CodexAnalysisForm(UsageData currentUsage, CodexTaskState currentTaskState,
            OverlaySettings currentSettings, CodexAnalyticsSnapshot previewSnapshot)
        {
            usage = currentUsage == null ? new UsageData() : currentUsage.Clone();
            taskState = currentTaskState;
            settings = currentSettings == null ? new OverlaySettings() : currentSettings.Clone();
            snapshot = previewSnapshot;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 9f, FontStyle.Regular);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(LogicalWidth, 596);
            MinimumSize = Size;
            Text = "Codex 分析";
            DoubleBuffered = true;
            ResolveTheme();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(22, 18, 22, 18);
            root.BackColor = surface;
            root.ColumnCount = 1;
            root.ColumnStyles.Clear();
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildTabs(), 0, 1);
            sourceNote = CreateLabel("正在读取本机工作记录…", 8.5f, muted);
            sourceNote.Margin = new Padding(0, 9, 0, 8);
            root.Controls.Add(sourceNote, 0, 2);

            contentHost = new Panel();
            contentHost.Dock = DockStyle.Fill;
            contentHost.BackColor = surface;
            root.Controls.Add(contentHost, 0, 3);
            BuildPages();
            SelectPage(0);
            ApplySnapshot(snapshot ?? CodexAnalyticsSnapshot.Empty());
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (snapshot == null)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    CodexAnalyticsSnapshot result = CodexActivityAnalyzer.ReadLastDays(30);
                    if (IsDisposed || !IsHandleCreated) return;
                    try { BeginInvoke(new MethodInvoker(delegate { ApplySnapshot(result); })); }
                    catch (InvalidOperationException) { }
                });
            }
        }

        private Control BuildHeader()
        {
            TableLayoutPanel header = new TableLayoutPanel();
            header.AutoSize = true;
            header.Dock = DockStyle.Fill;
            header.ColumnCount = 2;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            FlowLayoutPanel copy = new FlowLayoutPanel();
            copy.AutoSize = true;
            copy.FlowDirection = FlowDirection.TopDown;
            copy.WrapContents = false;
            copy.Margin = Padding.Empty;
            Label title = CreateLabel("分析", 18f, ink);
            Label subtitle = CreateLabel("查看本机 Codex 的工作节奏与模型记录", 9f, muted);
            title.Margin = new Padding(0, 0, 0, 3);
            subtitle.Margin = Padding.Empty;
            copy.Controls.Add(title);
            copy.Controls.Add(subtitle);
            header.Controls.Add(copy, 0, 0);

            Button close = new Button();
            close.Text = "×";
            close.Size = new Size(30, 30);
            close.Margin = new Padding(8, 0, 0, 0);
            close.FlatStyle = FlatStyle.Flat;
            close.FlatAppearance.BorderSize = 0;
            close.FlatAppearance.MouseOverBackColor = Blend(surface, ink, 15);
            close.FlatAppearance.MouseDownBackColor = Blend(surface, ink, 28);
            close.BackColor = surface;
            close.ForeColor = muted;
            close.Font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 15f, FontStyle.Regular);
            close.Click += delegate { Close(); };
            header.Controls.Add(close, 1, 0);
            return header;
        }

        private Control BuildTabs()
        {
            FlowLayoutPanel tabs = new FlowLayoutPanel();
            tabs.AutoSize = true;
            tabs.Dock = DockStyle.Fill;
            tabs.Margin = new Padding(0, 16, 0, 0);
            tabs.Padding = Padding.Empty;
            tabs.WrapContents = false;
            string[] names = new[] { "今日", "7 天", "30 天", "数据说明" };
            for (int index = 0; index < names.Length; index++)
            {
                int target = index;
                Button button = new Button();
                button.Text = names[index];
                button.AutoSize = false;
                button.Size = new Size(index == 3 ? 78 : 58, 30);
                button.Margin = new Padding(index == 0 ? 0 : 6, 0, 0, 0);
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 1;
                button.Font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 9f, FontStyle.Regular);
                button.Click += delegate { SelectPage(target); };
                tabButtons[index] = button;
                tabs.Controls.Add(button);
            }
            return tabs;
        }

        private void BuildPages()
        {
            pages[0] = BuildActivityPage("使用记录", "今天的任务与工具活动", 0);
            pages[1] = BuildActivityPage("产品活动", "最近 7 天的本机工作记录", 1);
            pages[2] = BuildActivityPage("产品活动", "最近 30 天的本机工作记录", 2);
            pages[3] = BuildDataNotePage();
            for (int index = 0; index < pages.Length; index++)
            {
                pages[index].Dock = DockStyle.Fill;
                contentHost.Controls.Add(pages[index]);
            }
        }

        private Panel BuildActivityPage(string title, string subtitle, int periodIndex)
        {
            TableLayoutPanel page = new TableLayoutPanel();
            page.Dock = DockStyle.Fill;
            page.BackColor = surface;
            page.ColumnCount = 1;
            page.ColumnStyles.Clear();
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            page.RowCount = 4;
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 84f));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 112f));

            FlowLayoutPanel heading = new FlowLayoutPanel();
            heading.AutoSize = true;
            heading.FlowDirection = FlowDirection.TopDown;
            heading.WrapContents = false;
            heading.Margin = new Padding(0, 0, 0, 8);
            Label name = CreateLabel(title, 11f, ink);
            Label detail = CreateLabel(subtitle, 8.5f, muted);
            name.Margin = new Padding(0, 0, 0, 2);
            detail.Margin = Padding.Empty;
            heading.Controls.Add(name);
            heading.Controls.Add(detail);
            page.Controls.Add(heading, 0, 0);

            Panel metrics = CreateCard();
            metrics.Dock = DockStyle.Fill;
            metrics.Margin = new Padding(0, 0, 0, 10);
            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(16, 13, 16, 12);
            grid.ColumnCount = 3;
            grid.RowCount = 2;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int column = 0; column < 3; column++)
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            AddMetric(grid, 0, "任务", out taskValues[periodIndex]);
            AddMetric(grid, 1, "工具调用", out toolValues[periodIndex]);
            AddMetric(grid, 2, periodIndex == 0 ? "累计 Token" : "模型记录", out modelValues[periodIndex]);
            metrics.Controls.Add(grid);
            page.Controls.Add(metrics, 0, 1);

            ActivityChart chart = new ActivityChart();
            chart.Dock = DockStyle.Fill;
            chart.Margin = new Padding(0, 0, 0, 10);
            chart.ApplyTheme(card, ink, muted, line, accent);
            charts[periodIndex] = chart;
            page.Controls.Add(chart, 0, 2);

            Panel models = CreateCard();
            models.Dock = DockStyle.Fill;
            models.Margin = Padding.Empty;
            TableLayoutPanel modelRoot = new TableLayoutPanel();
            modelRoot.Dock = DockStyle.Fill;
            modelRoot.Padding = new Padding(16, 12, 16, 10);
            modelRoot.ColumnCount = 1;
            modelRoot.ColumnStyles.Clear();
            modelRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            modelRoot.RowCount = 2;
            modelRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            modelRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Label modelHeading = CreateLabel("模型记录", 10f, ink);
            modelHeading.Margin = new Padding(0, 0, 0, 7);
            modelRoot.Controls.Add(modelHeading, 0, 0);
            FlowLayoutPanel list = new FlowLayoutPanel();
            list.Dock = DockStyle.Fill;
            list.AutoScroll = true;
            list.FlowDirection = FlowDirection.TopDown;
            list.WrapContents = false;
            list.Margin = Padding.Empty;
            modelLists[periodIndex] = list;
            modelRoot.Controls.Add(list, 0, 1);
            models.Controls.Add(modelRoot);
            page.Controls.Add(models, 0, 3);

            Panel holder = new Panel();
            holder.Dock = DockStyle.Fill;
            holder.BackColor = surface;
            holder.Controls.Add(page);
            return holder;
        }

        private Panel BuildDataNotePage()
        {
            Panel page = new Panel();
            page.Dock = DockStyle.Fill;
            page.BackColor = surface;
            Panel cardPanel = CreateCard();
            cardPanel.Dock = DockStyle.Top;
            cardPanel.Height = 300;
            TableLayoutPanel copy = new TableLayoutPanel();
            copy.Dock = DockStyle.Fill;
            copy.Padding = new Padding(18, 16, 18, 14);
            copy.ColumnCount = 1;
            copy.ColumnStyles.Clear();
            copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            copy.RowCount = 5;
            for (int row = 0; row < 5; row++) copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            AddNote(copy, "本机读取", "按时间窗口扫描 Codex 会话记录中的时间、事件类型与模型字段。", 0);
            AddNote(copy, "统计口径", "任务为用户发起的工作；工具调用只计工具请求，不计工具输出。", 1);
            AddNote(copy, "模型记录", "按会话去重，表示本机记录到的模型配置次数，不当作 Token 消耗。", 2);
            AddNote(copy, "隐私边界", "不会解析、保存、显示或上传你的对话正文；离线数据不会离开本机。", 3);
            Label quota = CreateLabel("当前额度来自 Codex app-server：" + PresentQuotaLine(), 8.5f, muted);
            quota.Margin = new Padding(0, 8, 0, 0);
            copy.Controls.Add(quota, 0, 4);
            cardPanel.Controls.Add(copy);
            page.Controls.Add(cardPanel);
            return page;
        }

        private void AddNote(TableLayoutPanel parent, string heading, string content, int row)
        {
            FlowLayoutPanel note = new FlowLayoutPanel();
            note.AutoSize = true;
            note.FlowDirection = FlowDirection.TopDown;
            note.WrapContents = false;
            note.Margin = new Padding(0, row == 0 ? 0 : 11, 0, 0);
            Label title = CreateLabel(heading, 9.5f, ink);
            Label detail = CreateLabel(content, 8.5f, muted);
            detail.MaximumSize = new Size(550, 0);
            title.Margin = new Padding(0, 0, 0, 2);
            detail.Margin = Padding.Empty;
            note.Controls.Add(title);
            note.Controls.Add(detail);
            parent.Controls.Add(note, 0, row);
        }

        private void AddMetric(TableLayoutPanel grid, int column, string label, out Label value)
        {
            Label heading = CreateLabel(label, 8.5f, muted);
            heading.Margin = Padding.Empty;
            value = CreateLabel("—", 18f, ink);
            value.Margin = new Padding(0, 2, 0, 0);
            grid.Controls.Add(heading, column, 0);
            grid.Controls.Add(value, column, 1);
        }

        private void ApplySnapshot(CodexAnalyticsSnapshot value)
        {
            snapshot = value ?? CodexAnalyticsSnapshot.Empty();
            ApplyPeriod(0, snapshot.Today);
            ApplyPeriod(1, snapshot.Week);
            ApplyPeriod(2, snapshot.Month);
            if (snapshot.ScannedFiles == 0)
                sourceNote.Text = "尚未找到可统计的本机会话记录。";
            else
                sourceNote.Text = "已读取 " + snapshot.ScannedFiles.ToString(CultureInfo.InvariantCulture) +
                    " 个本机会话记录；仅统计元数据，不读取对话正文。" +
                    (snapshot.SkippedFiles > 0 ? " 跳过 " + snapshot.SkippedFiles.ToString(CultureInfo.InvariantCulture) + " 个暂不可读文件。" : String.Empty);
        }

        private void ApplyPeriod(int index, CodexAnalyticsPeriod period)
        {
            taskValues[index].Text = period.Tasks.ToString(CultureInfo.InvariantCulture);
            toolValues[index].Text = period.ToolCalls.ToString(CultureInfo.InvariantCulture);
            modelValues[index].Text = index == 0
                ? PresentLifetimeTokens()
                : period.ModelSessions.ToString(CultureInfo.InvariantCulture);
            charts[index].SetData(period, index == 0 ? "今日时段" : "每日活动", index == 0);
            FlowLayoutPanel list = modelLists[index];
            list.SuspendLayout();
            list.Controls.Clear();
            if (period.Models.Count == 0)
            {
                Label empty = CreateLabel("此时间范围内还没有模型记录。", 8.5f, muted);
                empty.Margin = Padding.Empty;
                list.Controls.Add(empty);
            }
            else
            {
                int count = Math.Min(3, period.Models.Count);
                for (int item = 0; item < count; item++) list.Controls.Add(CreateModelRow(period.Models[item]));
            }
            list.ResumeLayout();
        }

        private Control CreateModelRow(CodexModelActivity model)
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.AutoSize = false;
            row.Width = 550;
            row.Height = 22;
            row.ColumnCount = 2;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Margin = new Padding(0, 0, 0, 2);
            Label name = CreateLabel(model.Name, 8.5f, ink);
            name.AutoEllipsis = true;
            name.Dock = DockStyle.Fill;
            name.TextAlign = ContentAlignment.MiddleLeft;
            Label count = CreateLabel(model.Sessions.ToString(CultureInfo.InvariantCulture) + " 个会话", 8.5f, muted);
            count.TextAlign = ContentAlignment.MiddleRight;
            count.Dock = DockStyle.Fill;
            row.Controls.Add(name, 0, 0);
            row.Controls.Add(count, 1, 0);
            return row;
        }

        private void SelectPage(int index)
        {
            selectedPage = Math.Max(0, Math.Min(pages.Length - 1, index));
            for (int item = 0; item < pages.Length; item++)
            {
                bool selected = item == selectedPage;
                pages[item].Visible = selected;
                Button button = tabButtons[item];
                button.BackColor = selected ? accentSoft : surface;
                button.ForeColor = selected ? accent : muted;
                button.FlatAppearance.BorderColor = selected ? accent : line;
                button.FlatAppearance.MouseOverBackColor = selected ? accentSoft : Blend(surface, ink, 10);
                button.FlatAppearance.MouseDownBackColor = selected ? Blend(accentSoft, accent, 22) : Blend(surface, ink, 20);
            }
        }

        private Panel CreateCard()
        {
            Panel panel = new Panel();
            panel.BackColor = card;
            panel.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen border = new Pen(line))
                    e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, panel.Width - 1), Math.Max(0, panel.Height - 1));
            };
            return panel;
        }

        private Label CreateLabel(string text, float size, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = UiRendering.CreateTextFont(UiRendering.PreferredFontName, size, FontStyle.Regular);
            label.ForeColor = color;
            label.AutoSize = true;
            label.BackColor = Color.Transparent;
            return label;
        }

        private void ResolveTheme()
        {
            surface = UiRendering.ResolveExpandedSettingsSurfaceColor(settings);
            ink = UiRendering.ResolveExpandedSettingsInkColor(settings);
            Color start;
            Color end;
            UiRendering.ResolveGearColors(settings.Theme, settings.CustomBackgroundArgb, out start, out end);
            bool light = surface.GetBrightness() >= 0.55f;
            accent = String.Equals(settings.Theme, "PinkGradient", StringComparison.Ordinal)
                ? Color.FromArgb(63, 76, 92) : start;
            muted = light ? Color.FromArgb(100, 108, 119) : Blend(ink, surface, 96);
            line = light ? Color.FromArgb(218, 224, 230) : Blend(surface, Color.White, 46);
            card = light ? Blend(surface, Color.White, 72) : Blend(surface, Color.White, 16);
            accentSoft = light ? Blend(surface, accent, 28) : Blend(surface, accent, 55);
            BackColor = surface;
            ForeColor = ink;
        }

        private static Color Blend(Color baseColor, Color overlay, int opacity)
        {
            int alpha = Math.Max(0, Math.Min(255, opacity));
            return Color.FromArgb(255,
                (baseColor.R * (255 - alpha) + overlay.R * alpha) / 255,
                (baseColor.G * (255 - alpha) + overlay.G * alpha) / 255,
                (baseColor.B * (255 - alpha) + overlay.B * alpha) / 255);
        }

        private string PresentQuotaLine()
        {
            string shortQuota = usage.ShortRemaining.HasValue ? usage.ShortRemaining.Value.ToString(CultureInfo.InvariantCulture) + "%" : "待刷新";
            string weekQuota = usage.WeeklyRemaining.HasValue ? usage.WeeklyRemaining.Value.ToString(CultureInfo.InvariantCulture) + "%" : "待刷新";
            return "5 小时 " + shortQuota + " · 周 " + weekQuota + " · 当前任务 " + PresentTaskState();
        }

        private string PresentLifetimeTokens()
        {
            return usage.LifetimeTokens.HasValue
                ? CodexAppServerClient.FormatLifetimeTokens(usage.LifetimeTokens.Value)
                : "待刷新";
        }

        private string PresentTaskState()
        {
            if (taskState == CodexTaskState.Processing) return "进行中";
            if (taskState == CodexTaskState.Completed) return "已完成";
            if (taskState == CodexTaskState.Interrupted) return "已中断";
            return "未检测到";
        }
    }

    internal sealed class ActivityChart : Panel
    {
        private int[] values = new int[0];
        private string title = String.Empty;
        private bool hourly;
        private Color card;
        private Color ink;
        private Color muted;
        private Color line;
        private Color accent;

        internal ActivityChart()
        {
            DoubleBuffered = true;
            MinimumSize = new Size(0, 190);
        }

        internal void ApplyTheme(Color currentCard, Color currentInk, Color currentMuted, Color currentLine, Color currentAccent)
        {
            card = currentCard;
            ink = currentInk;
            muted = currentMuted;
            line = currentLine;
            accent = currentAccent;
            BackColor = card;
        }

        internal void SetData(CodexAnalyticsPeriod period, string currentTitle, bool isHourly)
        {
            title = currentTitle;
            hourly = isHourly;
            values = isHourly ? period.HourlyActivities : period.DailyActivities;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen border = new Pen(line)) e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            using (Font heading = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 10f, FontStyle.Regular))
            using (Font helper = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 8.5f, FontStyle.Regular))
            using (SolidBrush inkBrush = new SolidBrush(ink))
            using (SolidBrush mutedBrush = new SolidBrush(muted))
            {
                e.Graphics.DrawString(title, heading, inkBrush, 16, 14);
                e.Graphics.DrawString(hourly ? "按小时统计任务与工具调用" : "按日期统计任务与工具调用", helper, mutedBrush, 16, 36);
                Rectangle chart = new Rectangle(18, 66, Math.Max(50, Width - 36), Math.Max(56, Height - 98));
                for (int row = 0; row < 4; row++)
                {
                    int y = chart.Top + chart.Height * row / 3;
                    using (Pen grid = new Pen(line)) e.Graphics.DrawLine(grid, chart.Left, y, chart.Right, y);
                }
                int max = 1;
                for (int item = 0; item < values.Length; item++) max = Math.Max(max, values[item]);
                if (values.Length > 0)
                {
                    float gap = values.Length > 12 ? 2f : 7f;
                    float slot = chart.Width / (float)values.Length;
                    float barWidth = Math.Max(2f, slot - gap);
                    using (SolidBrush bars = new SolidBrush(accent))
                    {
                        for (int item = 0; item < values.Length; item++)
                        {
                            float height = (chart.Height - 3) * values[item] / max;
                            RectangleF bar = new RectangleF(chart.Left + item * slot + (slot - barWidth) / 2f, chart.Bottom - height, barWidth, height);
                            e.Graphics.FillRectangle(bars, bar);
                        }
                    }
                }
                string[] labels = hourly ? new[] { "0", "6", "12", "18", "23" } : DayLabels(values.Length);
                for (int item = 0; item < labels.Length; item++)
                {
                    float x = chart.Left + (chart.Width - 10) * item / Math.Max(1, labels.Length - 1);
                    e.Graphics.DrawString(labels[item], helper, mutedBrush, x, chart.Bottom + 7);
                }
            }
        }

        private static string[] DayLabels(int days)
        {
            if (days <= 7) return new[] { "1", "2", "3", "4", "5", "6", "7" };
            return new[] { "1", "7", "14", "21", "30" };
        }
    }

    internal sealed class CodexAnalyticsSnapshot
    {
        internal CodexAnalyticsPeriod Today;
        internal CodexAnalyticsPeriod Week;
        internal CodexAnalyticsPeriod Month;
        internal int ScannedFiles;
        internal int SkippedFiles;

        internal static CodexAnalyticsSnapshot Empty()
        {
            DateTime now = DateTime.Now;
            return new CodexAnalyticsSnapshot
            {
                Today = new CodexAnalyticsPeriod(now.Date, 1),
                Week = new CodexAnalyticsPeriod(now.Date.AddDays(-6), 7),
                Month = new CodexAnalyticsPeriod(now.Date.AddDays(-29), 30)
            };
        }

        internal static CodexAnalyticsSnapshot Preview()
        {
            CodexAnalyticsSnapshot result = Empty();
            DateTime now = DateTime.Now;
            for (int hour = 8; hour < 19; hour++) result.Today.AddActivity(now.Date.AddHours(hour), hour % 3 == 0, true);
            for (int day = 0; day < 7; day++) result.Week.AddActivity(now.Date.AddDays(-day).AddHours(10), true, day % 2 == 0);
            for (int day = 0; day < 30; day++) result.Month.AddActivity(now.Date.AddDays(-day).AddHours(11), day % 2 == 0, day % 3 == 0);
            result.Today.AddModel("gpt-5.6-sol");
            result.Week.AddModel("gpt-5.6-sol");
            result.Week.AddModel("gpt-5.6-luna");
            result.Month.AddModel("gpt-5.6-sol");
            result.Month.AddModel("gpt-5.6-luna");
            result.ScannedFiles = 12;
            return result;
        }
    }

    internal sealed class CodexAnalyticsPeriod
    {
        private readonly DateTime start;
        private readonly int dayCount;
        private readonly Dictionary<string, int> modelSessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal int Tasks;
        internal int ToolCalls;
        internal readonly int[] HourlyActivities = new int[24];
        internal readonly int[] DailyActivities;

        internal CodexAnalyticsPeriod(DateTime periodStart, int periodDays)
        {
            start = periodStart.Date;
            dayCount = Math.Max(1, periodDays);
            DailyActivities = new int[dayCount];
        }

        internal int ModelSessions
        {
            get { int total = 0; foreach (KeyValuePair<string, int> item in modelSessions) total += item.Value; return total; }
        }

        internal List<CodexModelActivity> Models
        {
            get
            {
                List<CodexModelActivity> models = new List<CodexModelActivity>();
                foreach (KeyValuePair<string, int> item in modelSessions) models.Add(new CodexModelActivity(item.Key, item.Value));
                models.Sort(delegate(CodexModelActivity left, CodexModelActivity right)
                {
                    int comparison = right.Sessions.CompareTo(left.Sessions);
                    return comparison != 0 ? comparison : String.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
                });
                return models;
            }
        }

        internal bool Contains(DateTime localTime) { return localTime >= start && localTime < start.AddDays(dayCount); }

        internal void AddActivity(DateTime localTime, bool isTask, bool isTool)
        {
            if (!Contains(localTime)) return;
            if (isTask) Tasks++;
            if (isTool) ToolCalls++;
            if (!isTask && !isTool) return;
            HourlyActivities[localTime.Hour]++;
            int day = (int)(localTime.Date - start).TotalDays;
            if (day >= 0 && day < DailyActivities.Length) DailyActivities[day]++;
        }

        internal void AddModel(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return;
            int existing;
            modelSessions.TryGetValue(name, out existing);
            modelSessions[name] = existing + 1;
        }
    }

    internal sealed class CodexModelActivity
    {
        internal readonly string Name;
        internal readonly int Sessions;
        internal CodexModelActivity(string name, int sessions) { Name = name; Sessions = sessions; }
    }

    internal static class CodexActivityAnalyzer
    {
        private static readonly Regex TimestampPattern = new Regex("\\\"timestamp\\\"\\s*:\\s*\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ModelPattern = new Regex("\\\"model\\\"\\s*:\\s*\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static CodexAnalyticsSnapshot ReadLastDays(int days)
        {
            int range = Math.Max(1, days);
            CodexAnalyticsSnapshot result = CodexAnalyticsSnapshot.Empty();
            DateTime now = DateTime.Now;
            DateTime monthStart = now.Date.AddDays(-(range - 1));
            result.Today = new CodexAnalyticsPeriod(now.Date, 1);
            result.Week = new CodexAnalyticsPeriod(now.Date.AddDays(-6), 7);
            result.Month = new CodexAnalyticsPeriod(monthStart, range);
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            if (!Directory.Exists(root)) return result;
            string[] files;
            try { files = Directory.GetFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories); }
            catch { return result; }
            foreach (string path in files)
            {
                try
                {
                    if (File.GetLastWriteTime(path) < monthStart) continue;
                    ReadFile(path, result);
                    result.ScannedFiles++;
                }
                catch { result.SkippedFiles++; }
            }
            return result;
        }

        private static void ReadFile(string path, CodexAnalyticsSnapshot result)
        {
            HashSet<string> todayModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> weekModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> monthModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 4096))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    DateTime localTime;
                    if (!TryGetLocalTimestamp(line, out localTime)) continue;
                    bool task = IsUserTask(line);
                    bool tool = IsToolCall(line);
                    result.Today.AddActivity(localTime, task, tool);
                    result.Week.AddActivity(localTime, task, tool);
                    result.Month.AddActivity(localTime, task, tool);
                    Match model = ModelPattern.Match(line);
                    if (!model.Success) continue;
                    string value = model.Groups["value"].Value.Trim();
                    if (result.Today.Contains(localTime) && todayModels.Add(value)) result.Today.AddModel(value);
                    if (result.Week.Contains(localTime) && weekModels.Add(value)) result.Week.AddModel(value);
                    if (result.Month.Contains(localTime) && monthModels.Add(value)) result.Month.AddModel(value);
                }
            }
        }

        private static bool TryGetLocalTimestamp(string line, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            Match match = TimestampPattern.Match(line);
            if (!match.Success) return false;
            DateTimeOffset parsed;
            if (!DateTimeOffset.TryParse(match.Groups["value"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)) return false;
            localTime = parsed.LocalDateTime;
            return true;
        }

        private static bool IsUserTask(string line)
        {
            bool eventUser = line.IndexOf("\\\"type\\\":\\\"event_msg\\\"", StringComparison.Ordinal) >= 0 && line.IndexOf("\\\"type\\\":\\\"user_message\\\"", StringComparison.Ordinal) >= 0;
            bool responseUser = line.IndexOf("\\\"type\\\":\\\"response_item\\\"", StringComparison.Ordinal) >= 0 && line.IndexOf("\\\"type\\\":\\\"message\\\"", StringComparison.Ordinal) >= 0 && line.IndexOf("\\\"role\\\":\\\"user\\\"", StringComparison.Ordinal) >= 0;
            return eventUser || responseUser;
        }

        private static bool IsToolCall(string line) { return line.IndexOf("\\\"type\\\":\\\"custom_tool_call\\\"", StringComparison.Ordinal) >= 0; }
    }
}
