using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
namespace CodexUsageOverlay
{
    internal static class NativeAnalyticsTests
    {
        internal static void Verify()
        {
            ConversationProbeTests.Verify();
            var json = new JavaScriptSerializer();
            var data = NativeUsageAnalytics.Parse((IDictionary<string, object>)json.DeserializeObject(
                "{\"summary\":{\"lifetimeTokens\":999999},\"dailyUsageBuckets\":[{\"startDate\":\"2026-09-05\",\"tokens\":0},{\"startDate\":\"2026-09-06\",\"tokens\":120}]}"));
            if (data.Total(new DateTime(2026,9,5),2) != 120) throw new Exception("period must use daily tokens");
            if (data.Total(new DateTime(2026,9,7),1).HasValue) throw new Exception("missing day must not become zero or lifetime");
            if (data.Total(new DateTime(2026,9,5),1) != 0) throw new Exception("reported zero must survive");
            if (data.Coverage(new DateTime(2026,9,5),3) != 2) throw new Exception("partial coverage is required");
            var empty = NativeUsageAnalytics.Parse((IDictionary<string, object>)json.DeserializeObject("{\"summary\":{},\"dailyUsageBuckets\":null}"));
            if (empty.Total(DateTime.Today,30).HasValue) throw new Exception("null buckets must stay unavailable");
            var duplicate = NativeUsageAnalytics.Parse((IDictionary<string, object>)json.DeserializeObject("{\"dailyUsageBuckets\":[{\"startDate\":\"2026-09-05\",\"tokens\":2},{\"startDate\":\"2026-09-05\",\"tokens\":3}]}"));
            if (duplicate.Total(new DateTime(2026,9,5),1).HasValue) throw new Exception("conflicting daily rows must not be summed");
            var report = new NativeAnalyticsReport { End = new DateTime(2026, 9, 6) };
            NativeAnalyticsService.Parse(report, 0, (IDictionary<string, object>)json.DeserializeObject("{\"units\":\"percent\",\"data\":[{\"date\":\"2026-09-06\",\"product_surface_usage_values\":{\"desktop_app\":2,\"work_desktop\":3},\"models\":[{\"model\":\"model-a\",\"credits\":2},{\"model\":\"model-a\",\"credits\":3}]},{\"date\":\"2026-09-07\",\"product_surface_usage_values\":{\"desktop_app\":999},\"models\":[]}]}"));
            if (report.Series[0].Unit != "%" || report.Series[1].Sum(new DateTime(2026,8,31),7)["model-a"] != 5) throw new Exception("native units and speed grouping must survive");
            if (report.Series[0].Daily.Count != 1 || report.Series[0].Sum(new DateTime(2026,8,31),7)["Codex"] != 2) throw new Exception("today must be excluded");
            NativeAnalyticsService.Parse(report, 1, (IDictionary<string, object>)json.DeserializeObject("{\"data\":[{\"date\":\"2026-09-06\",\"models\":[{\"model\":\"model-a\",\"turns\":7,\"credits\":999}],\"clients\":[{\"client_id\":\"CODEX_DESKTOP_APP\",\"turns\":7}]}]}"));
            if (report.Series[2].Sum(new DateTime(2026,8,31),7)["model-a"] != 7) throw new Exception("activity must use turns, not credits");
            NativeAnalyticsService.Parse(report, 2, (IDictionary<string, object>)json.DeserializeObject("{\"data\":[{\"date\":\"2026-09-06\",\"skill_usage_overviews\":[{\"display_name\":\"Other\",\"invocation_counts\":4}]}]}"));
            NativeAnalyticsService.Parse(report, 3, (IDictionary<string, object>)json.DeserializeObject("{\"data\":[]}"));
            if (report.Series[4].Sum(new DateTime(2026,8,31),7)["Other"] != 4 || report.Series[5].Daily.Count != 0) throw new Exception("native tools must preserve Other and empty responses");
        }
    }
}
