using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace CodexUsageOverlay
{
    internal sealed partial class OverlayForm
    {
        private NativeAnalyticsReport nativeReport = new NativeAnalyticsReport();
        private int nativeModule, nativeListPage;
        private Rectangle NativeModuleBounds(int index)
        {
            var area = AnalysisContentBounds;
            int width = (area.Width - 25) / 6;
            return new Rectangle(area.Left + index * (width + 5), area.Top + 81, width, 26);
        }
        private Rectangle NativePageBounds(bool next)
        {
            var area = AnalysisContentBounds;
            return new Rectangle(area.Right - (next ? 55 : 115), area.Bottom - 25, 52, 23);
        }
        private int NativeRowsPerPage { get { return Math.Max(1, (AnalysisContentBounds.Height - 285) / 29); } }
        private bool HandleNativeAnalyticsClick(Point point)
        {
            if (analysisPage == 2) return false;
            for (int i = 0; i < 6; i++)
                if (NativeModuleBounds(i).Contains(point)) { nativeModule = i; nativeListPage = 0; RefreshInlinePanel(); return true; }
            if (NativePageBounds(false).Contains(point)) { nativeListPage = Math.Max(0, nativeListPage - 1); RefreshInlinePanel(); return true; }
            if (NativePageBounds(true).Contains(point))
            {
                int days = analysisPage == 0 ? 7 : 30;
                int count = nativeReport.Series[nativeModule].Sum(nativeReport.End.AddDays(1-days), days).Count;
                nativeListPage = Math.Min(Math.Max(0, (count - 1) / NativeRowsPerPage), nativeListPage + 1);
                RefreshInlinePanel(); return true;
            }
            return false;
        }
        private void DrawInlineAnalysis(Graphics graphics, Color textColor, Color borderColor, OverlaySettings visualSettings)
        {
            var area = AnalysisContentBounds;
            Color ink = UiRendering.PanelColor(visualSettings, 1), edge = UiRendering.PanelColor(visualSettings, 2);
            Color card = UiRendering.PanelColor(visualSettings, 4), selected = UiRendering.PanelColor(visualSettings, 5);
            Color accent = UiRendering.PanelColor(visualSettings, 3);
            using (var title = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 12f, FontStyle.Regular))
            using (var body = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 8.5f, FontStyle.Regular))
            using (var small = UiRendering.CreateTextFont(UiRendering.PreferredFontName, 7.5f, FontStyle.Regular))
            using (var text = new SolidBrush(ink))
            using (var bar = new SolidBrush(accent))
            using (var center = UiRendering.CreateTextFormat())
            {
                center.Alignment = StringAlignment.Center; center.LineAlignment = StringAlignment.Center;
                graphics.DrawString("Codex 原生分析", title, text, area.Left + 4, area.Top);
                graphics.DrawString(analysisLoading ? "正在同步官方分析…" : "官方分析接口 · UTC 日期 · 截至 " + nativeReport.End.ToString("yyyy-MM-dd"), small, text, area.Left + 4, area.Top + 27);
                graphics.DrawString("×", title, text, AnalysisCloseBounds, center);
                string[] ranges = { "7 天", "30 天", "数据说明" };
                for (int i = 0; i < ranges.Length; i++)
                {
                    var rect = AnalysisTabBounds(i); DrawInlineBox(graphics, rect, analysisPage == i ? selected : card, edge);
                    graphics.DrawString(ranges[i], body, text, rect, center);
                }
                if (analysisPage == 2)
                {
                    string notes = "与 Codex 原生分析页相同的官方分析接口。\n\n产品 / 模型用量：保留接口单位（百分比或 credits）。\n模型 / 使用端活动：官方轮次；不是 Token 或任务数。\n技能 / 插件：官方调用次数，接口返回前 10 项及 Other。\n\n按所选 7 天 / 30 天向接口查询，均截止昨天（UTC）。\n缺失日期不补零；服务端统计可能延迟。\n\n只读取本机 Codex 登录态用于官方 HTTPS 请求。\n不保存凭据，不上传本地对话，不改写登录文件。\n这些为客户端内部接口，后续版本可能发生变化。";
                    graphics.DrawString(notes, body, text, new RectangleF(area.Left + 8, area.Top + 88, area.Width - 16, area.Height - 95));
                    return;
                }
                for (int i = 0; i < 6; i++)
                {
                    var rect = NativeModuleBounds(i); DrawInlineBox(graphics, rect, nativeModule == i ? selected : card, edge);
                    graphics.DrawString(nativeReport.Series[i].Title, small, text, rect, center);
                }
                var series = nativeReport.Series[nativeModule];
                if (analysisLoading || series.Error.Length > 0)
                {
                    graphics.DrawString(analysisLoading ? "正在读取，请稍候…" : series.Error, body, text, new RectangleF(area.Left + 12, area.Top + 130, area.Width - 24, 90));
                    return;
                }
                int days = analysisPage == 0 ? 7 : 30;
                DateTime start = nativeReport.End.AddDays(1 - days);
                var totals = series.Sum(start, days).OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToList();
                var chart = new Rectangle(area.Left, area.Top + 118, area.Width, 120);
                DrawInlineBox(graphics, chart, card, edge);
                graphics.DrawString(series.Title + " · 单位 " + series.Unit + (nativeModule >= 4 ? " · 原生 Top 10 / Other" : ""), small, text, chart.Left + 12, chart.Top + 8);
                var plot = new Rectangle(chart.Left + 16, chart.Top + 31, chart.Width - 32, 65);
                decimal max = 1;
                for (int i = 0; i < days; i++)
                { Dictionary<string, decimal> values; if (series.Daily.TryGetValue(start.AddDays(i), out values)) max = Math.Max(max, values.Values.Sum()); }
                for (int i = 0; i < days; i++)
                {
                    float slot = plot.Width / (float)days, width = Math.Min(40, Math.Max(2, slot - 5));
                    float x = plot.Left + i * slot + (slot - width) / 2;
                    Dictionary<string, decimal> values;
                    if (series.Daily.TryGetValue(start.AddDays(i), out values))
                    {
                        float height = (float)(values.Values.Sum() / max) * plot.Height;
                        if (height > 0) graphics.FillRectangle(bar, x, plot.Bottom - height, width, height);
                        else graphics.DrawString("0", small, text, x, plot.Bottom - 14);
                    }
                    else graphics.DrawString("—", small, text, x, plot.Bottom - 14);
                }
                graphics.DrawString(start.ToString("MM-dd"), small, text, plot.Left, plot.Bottom + 3);
                graphics.DrawString(nativeReport.End.ToString("MM-dd"), small, text, plot.Right - 40, plot.Bottom + 3);
                int top = area.Top + 249;
                if (totals.Count == 0) graphics.DrawString("此期间接口未返回记录，不代表零消耗。", body, text, area.Left + 10, top);
                int rowCount = NativeRowsPerPage;
                nativeListPage = Math.Min(nativeListPage, Math.Max(0, (totals.Count - 1) / rowCount));
                foreach (var item in totals.Skip(nativeListPage * rowCount).Take(rowCount))
                {
                    graphics.DrawString(item.Key, body, text, new RectangleF(area.Left + 10, top, area.Width - 150, 21));
                    var valueRect = new Rectangle(area.Right - 138, top, 132, 21);
                    graphics.DrawString(item.Value.ToString("0.##") + " " + series.Unit, body, text, valueRect, center);
                    top += 29;
                }
                graphics.DrawString("空缺不补零 · " + (nativeListPage + 1) + " / " + Math.Max(1, (totals.Count + rowCount - 1) / rowCount), small, text, area.Left + 6, area.Bottom - 21);
                foreach (bool next in new[] { false, true })
                { var rect = NativePageBounds(next); DrawInlineBox(graphics, rect, card, edge); graphics.DrawString(next ? "下一页" : "上一页", small, text, rect, center); }
            }
        }
    }
}
