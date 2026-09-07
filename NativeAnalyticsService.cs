using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageOverlay
{
    internal sealed class NativeAnalyticsSeries
    {
        internal string Title, Unit, Error = "", Freshness = "";
        internal readonly SortedDictionary<DateTime, Dictionary<string, decimal>> Daily = new SortedDictionary<DateTime, Dictionary<string, decimal>>();
        internal Dictionary<string, decimal> Sum(DateTime start, int days)
        {
            var result = new Dictionary<string, decimal>();
            foreach (var day in Daily)
                if (day.Key >= start && day.Key < start.AddDays(days))
                    foreach (var item in day.Value)
                    { decimal value; result.TryGetValue(item.Key, out value); result[item.Key] = value + item.Value; }
            return result;
        }
    }

    internal sealed class NativeAnalyticsReport
    {
        internal DateTime End = DateTime.UtcNow.Date.AddDays(-1);
        internal NativeAnalyticsSeries[] Series = {
            new NativeAnalyticsSeries { Title = "产品用量" },
            new NativeAnalyticsSeries { Title = "模型用量" },
            new NativeAnalyticsSeries { Title = "模型活动", Unit = "轮次" },
            new NativeAnalyticsSeries { Title = "使用端", Unit = "轮次" },
            new NativeAnalyticsSeries { Title = "技能", Unit = "调用次数" },
            new NativeAnalyticsSeries { Title = "插件", Unit = "调用次数" }
        };
    }

    internal static class NativeAnalyticsService
    {
        internal static readonly string[] Routes = { "usage/daily-token-usage-breakdown", "analytics/daily-workspace-usage-counts", "analytics/daily-skill-usage-metrics", "analytics/daily-plugin-usage-metrics" };
        internal static object Get(IDictionary<string, object> obj, string key) { object value; return obj != null && obj.TryGetValue(key, out value) ? value : null; }
        private static string Text(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture); }
        private static IEnumerable Items(object value) { return value is string ? new object[0] : value as IEnumerable ?? new object[0]; }
        private static void Add(Dictionary<string, decimal> values, string name, object raw)
        {
            decimal number, current;
            if (String.IsNullOrWhiteSpace(name) || raw == null || !Decimal.TryParse(Text(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || number < 0) return;
            values.TryGetValue(name, out current); values[name] = checked(current + number);
        }
        internal static void Parse(NativeAnalyticsReport report, int route, IDictionary<string, object> root)
        {
            if (!(Get(root, "data") is object[])) throw new FormatException();
            int first = route == 0 ? 0 : route == 1 ? 2 : route + 2;
            int count = route < 2 ? 2 : 1;
            for (int i = first; i < first + count; i++)
            {
                var series = report.Series[i];
                series.Freshness = Text(Get(root, "data_freshness_ts"));
                if (route == 0)
                {
                    string unit = Text(Get(root, "units"));
                    if (unit != "percent" && unit != "credits") throw new FormatException();
                    series.Unit = unit == "percent" ? "%" : "credits";
                }
                foreach (object raw in Items(Get(root, "data")))
                {
                    var row = raw as IDictionary<string, object>;
                    DateTime date;
                    if (!DateTime.TryParseExact(Text(Get(row, "date")), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) throw new FormatException();
                    if (date > report.End || date < report.End.AddDays(-29)) continue;
                    if (series.Daily.ContainsKey(date)) throw new FormatException();
                    var values = new Dictionary<string, decimal>();
                    if (i == 0)
                    {
                        var products = Get(row, "product_surface_usage_values") as IDictionary<string, object>;
                        if (products == null) throw new FormatException();
                        foreach (var pair in products) Add(values, pair.Key.StartsWith("work_", StringComparison.Ordinal) ? "工作" : "Codex", pair.Value);
                    }
                    else
                    {
                        string array = i == 1 || i == 2 ? "models" : i == 3 ? "clients" : i == 4 ? "skill_usage_overviews" : "plugin_usage_overviews";
                        if (!(Get(row, array) is object[])) throw new FormatException();
                        foreach (object item in Items(Get(row, array)))
                        {
                            var entry = item as IDictionary<string, object>;
                            string name = Text(Get(entry, i <= 2 ? "model" : i == 3 ? "client_id" : "display_name"));
                            if (name.Length == 0 && i >= 4) name = Text(Get(entry, i == 4 ? "skill_name" : "plugin_name"));
                            Add(values, name, Get(entry, i == 1 ? "credits" : i <= 3 ? "turns" : "invocation_counts"));
                        }
                    }
                    series.Daily.Add(date, values);
                }
            }
        }

        internal static NativeAnalyticsReport Read()
        {
            return ReadPeriod(30);
        }
        internal static NativeAnalyticsReport ReadPeriod(int days)
        {
            if (days != 7 && days != 30) throw new ArgumentOutOfRangeException("days");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var report = new NativeAnalyticsReport();
            string token, account;
            try
            {
                string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (String.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(home, "auth.json"))) as IDictionary<string, object>;
                var auth = Get(root, "tokens") as IDictionary<string, object>;
                token = Text(Get(auth, "access_token")); account = Text(Get(auth, "account_id"));
                if (token.Length == 0 || account.Length == 0) throw new InvalidOperationException();
            }
            catch { foreach (var series in report.Series) series.Error = "未找到 Codex 登录态，请先登录 Codex"; return report; }
            // Credentials are read only, never logged, persisted, refreshed, or sent to redirects.
            Parallel.For(0, Routes.Length, delegate(int route)
            {
                string error = "";
                try
                {
                    string query = "?start_date=" + report.End.AddDays(1 - days).ToString("yyyy-MM-dd") + "&end_date=" + report.End.ToString("yyyy-MM-dd") + "&group_by=day";
                    if (route > 0) query += "&workspace_user=true";
                    if (route == 2) query += "&top_skill_limit=10";
                    if (route == 3) query += "&top_plugin_limit=10";
                    var request = (HttpWebRequest)WebRequest.Create("https://chatgpt.com/backend-api/wham/" + Routes[route] + query);
                    request.AllowAutoRedirect = false; request.Timeout = 20000; request.ReadWriteTimeout = 20000;
                    request.UserAgent = "codex-cli"; request.Accept = "application/json";
                    request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                    request.Headers["ChatGPT-Account-Id"] = account;
                    string proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
                    Uri proxyUri;
                    if (Uri.TryCreate(proxy, UriKind.Absolute, out proxyUri) && (proxyUri.Scheme == "http" || proxyUri.Scheme == "https")) request.Proxy = new WebProxy(proxyUri);
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if (response.StatusCode != HttpStatusCode.OK) throw new FormatException();
                        using (var reader = new StreamReader(response.GetResponseStream()))
                        {
                            var json = new JavaScriptSerializer { MaxJsonLength = 4000000 };
                            Parse(report, route, json.DeserializeObject(reader.ReadToEnd()) as IDictionary<string, object>);
                        }
                    }
                }
                catch (WebException ex)
                {
                    var response = ex.Response as HttpWebResponse;
                    error = response == null ? "连接失败，请检查网络/代理后刷新" : response.StatusCode == HttpStatusCode.Unauthorized ? "登录已过期，请在 Codex 重新登录" : "官方接口暂不可用（HTTP " + (int)response.StatusCode + "）";
                    if (response != null) response.Dispose();
                }
                catch { error = "原生数据格式不可用，请更新助手或稍后刷新"; }
                if (error.Length > 0)
                {
                    int first = route == 0 ? 0 : route == 1 ? 2 : route + 2;
                    for (int i = first; i < first + (route < 2 ? 2 : 1); i++) { report.Series[i].Daily.Clear(); report.Series[i].Error = error; }
                }
            });
            return report;
        }
    }
}
