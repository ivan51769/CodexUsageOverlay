using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUsageOverlay
{
    internal enum ResetRadarStatus
    {
        Loading,
        Offline,
        NoSignal,
        CompletedToday,
        ScheduledToday,
        ScheduledUpcoming
    }

    internal sealed class ResetRadarData
    {
        public ResetRadarStatus Status = ResetRadarStatus.Loading;
        public string StatusLabel = "雷达载入中";
        public string Detail = "正在检查 Tibo 的公开重置公告";
        public string ScopeLabel = String.Empty;
        public string EventKind = String.Empty;
        public string EvidencePostId = String.Empty;
        public string SourceUrl = String.Empty;
        public DateTimeOffset? AnnouncedAt;
        public DateTimeOffset? EffectiveAt;
        public DateTimeOffset? EffectiveUntil;
        public DateTimeOffset? LastSuccessfulCheckAt;
        public double? Confidence;
        public DateTimeOffset FetchedAt;
        public bool NetworkAvailable;
        public bool IsFromCache;
        public bool RefreshPending;
        public string LastError = String.Empty;

        public ResetRadarData Clone()
        {
            return (ResetRadarData)MemberwiseClone();
        }

        public string RevisionKey
        {
            get
            {
                return String.Join("|", new[]
                {
                    Status.ToString(), StatusLabel, Detail, ScopeLabel, EventKind,
                    EvidencePostId, SourceUrl, NetworkAvailable ? "1" : "0",
                    IsFromCache ? "1" : "0", RefreshPending ? "1" : "0", LastError,
                    Confidence.HasValue ? Confidence.Value.ToString("0.####", CultureInfo.InvariantCulture) : String.Empty,
                    FetchedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                });
            }
        }
    }

    internal static class ResetRadarDisplay
    {
        public static bool ShouldShowStatusDot(ResetRadarData data)
        {
            return data == null || (data.Status != ResetRadarStatus.CompletedToday &&
                data.Status != ResetRadarStatus.NoSignal);
        }

        public static bool ShouldShow(ResetRadarData data, DateTimeOffset now)
        {
            if (data == null || !data.NetworkAvailable || data.IsFromCache ||
                String.IsNullOrWhiteSpace(data.SourceUrl))
                return false;

            if (data.Status == ResetRadarStatus.CompletedToday)
            {
                DateTimeOffset? completedAt = data.EffectiveAt ?? data.AnnouncedAt;
                return completedAt.HasValue &&
                    completedAt.Value.ToLocalTime().Date == now.ToLocalTime().Date;
            }

            bool scheduled = data.Status == ResetRadarStatus.ScheduledToday ||
                data.Status == ResetRadarStatus.ScheduledUpcoming;
            if (!scheduled)
                return false;
            return !data.EffectiveUntil.HasValue || data.EffectiveUntil.Value > now;
        }

        public static string ConfidenceSuffix(ResetRadarData data)
        {
            if (data == null || !data.Confidence.HasValue)
                return String.Empty;
            int percent = (int)Math.Round(data.Confidence.Value * 100d, MidpointRounding.AwayFromZero);
            return " · 置信度 " + percent.ToString(CultureInfo.InvariantCulture) + "%";
        }

        public static string BuildHeadline(ResetRadarData data, DateTimeOffset now)
        {
            if (data == null)
                return String.Empty;

            bool scheduled = data.Status == ResetRadarStatus.ScheduledToday ||
                data.Status == ResetRadarStatus.ScheduledUpcoming;
            string headline;
            if (!scheduled || !data.EffectiveAt.HasValue)
            {
                headline = data.StatusLabel;
            }
            else
            {
                DateTimeOffset localStart = data.EffectiveAt.Value.ToLocalTime();
                DateTime localToday = now.ToLocalTime().Date;
                if (now >= data.EffectiveAt.Value &&
                    (!data.EffectiveUntil.HasValue || now < data.EffectiveUntil.Value))
                    headline = "重置时段已开始";
                else if (localStart.Date == localToday)
                    headline = "预计今日" + FormatLocalTime(localStart) + "后有重置";
                else if (localStart.Date == localToday.AddDays(1))
                    headline = "预计明日" + FormatLocalTime(localStart) + "后有重置";
                else
                    headline = "预计" + FormatLocalDateTime(localStart) + "后有重置";
            }
            return data.RefreshPending && data.Status != ResetRadarStatus.Offline
                ? headline + " · 网络重试中"
                : headline;
        }

        public static string BuildPillLabel(ResetRadarData data, DateTimeOffset now)
        {
            if (data == null)
                return String.Empty;
            bool scheduled = data.Status == ResetRadarStatus.ScheduledToday ||
                data.Status == ResetRadarStatus.ScheduledUpcoming;
            if (scheduled && data.EffectiveAt.HasValue)
            {
                if (now >= data.EffectiveAt.Value &&
                    (!data.EffectiveUntil.HasValue || now < data.EffectiveUntil.Value))
                    return "重置进行中";
                DateTimeOffset localStart = data.EffectiveAt.Value.ToLocalTime();
                DateTime localToday = now.ToLocalTime().Date;
                if (localStart.Date == localToday)
                    return FormatLocalTime(localStart) + "后重置";
                if (localStart.Date == localToday.AddDays(1))
                    return "明日" + FormatLocalTime(localStart) + "重置";
                return localStart.ToString("M/d", CultureInfo.InvariantCulture) + "重置";
            }
            return data.StatusLabel;
        }

        public static string BuildPrimaryLine(ResetRadarData data, DateTimeOffset now)
        {
            if (data == null)
                return String.Empty;

            if (data.Status == ResetRadarStatus.CompletedToday)
            {
                DateTimeOffset? completedAt = data.EffectiveAt ?? data.AnnouncedAt;
                return completedAt.HasValue
                    ? "今日已重置：" + FormatLocalDateTime(completedAt.Value)
                    : data.Detail;
            }

            bool scheduled = data.Status == ResetRadarStatus.ScheduledToday ||
                data.Status == ResetRadarStatus.ScheduledUpcoming;
            if (!scheduled || !data.EffectiveAt.HasValue)
                return data.Detail;

            DateTimeOffset start = data.EffectiveAt.Value;
            DateTimeOffset? end = data.EffectiveUntil;
            string schedule = "计划重置：" + FormatLocalDateTime(start);
            if (end.HasValue && end.Value > start)
                schedule += "—" + FormatLocalDateTime(end.Value);

            if (now < start)
            {
                string countdown = FormatCountdown(start - now) + "后";
                if (end.HasValue && end.Value > start)
                    countdown += "—" + FormatCountdown(end.Value - now) + "后";
                return schedule + " · " + countdown;
            }

            if (end.HasValue && end.Value > now)
                return schedule + " · 已开始—还剩" + FormatCountdown(end.Value - now);

            return schedule + " · 计划窗口已结束";
        }

        internal static string FormatCountdown(TimeSpan remaining)
        {
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            int hours = (int)Math.Floor(remaining.TotalHours);
            return hours.ToString(CultureInfo.InvariantCulture) + "小时" +
                remaining.Minutes.ToString(CultureInfo.InvariantCulture) + "分" +
                remaining.Seconds.ToString(CultureInfo.InvariantCulture) + "秒";
        }

        private static string FormatLocalDateTime(DateTimeOffset value)
        {
            return value.ToLocalTime().ToString("M月d日 HH:mm", CultureInfo.GetCultureInfo("zh-CN"));
        }

        private static string FormatLocalTime(DateTimeOffset value)
        {
            return value.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class ResetRadarNotification
    {
        public string Title;
        public string Body;
        public string SourceUrl;
    }

    internal sealed class ResetRadarService : IDisposable
    {
        public const string FeedUrl = "https://www.codexrunway.com/api/status.json";
        public const string SiteUrl = "https://www.codexrunway.com/";

        private const int RefreshMinutes = 10;
        private const int RetrySeconds = 60;
        private const int MaxPayloadCharacters = 262144;
        private readonly object sync = new object();
        private readonly string cachePath;
        private readonly string statePath;
        private ResetRadarData data;
        private DateTime lastRefreshAttemptUtc = DateTime.MinValue;
        private int refreshDelaySeconds = RefreshMinutes * 60;
        private bool refreshRunning;
        private bool disposed;
        private readonly List<string> notifiedPostIds;

        public ResetRadarService()
        {
            cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "reset-radar-cache.json");
            statePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "reset-radar-state.ini");
            data = LoadCachedData(cachePath);
            notifiedPostIds = LoadNotifiedPostIds(statePath);
        }

        public ResetRadarData Snapshot()
        {
            lock (sync)
                return data.Clone();
        }

        public void RequestRefresh(bool force)
        {
            bool shouldStart = false;
            lock (sync)
            {
                if (!disposed && !refreshRunning &&
                    (force || (DateTime.UtcNow - lastRefreshAttemptUtc).TotalSeconds >= refreshDelaySeconds))
                {
                    refreshRunning = true;
                    lastRefreshAttemptUtc = DateTime.UtcNow;
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
            try
            {
                string payload = DownloadPayload();
                ResetRadarData incoming;
                string error;
                if (!ResetRadarParser.TryParse(payload, DateTimeOffset.Now, out incoming, out error))
                    throw new InvalidDataException(error);

                incoming.IsFromCache = false;
                incoming.RefreshPending = false;
                SaveTextAtomically(cachePath, payload);
                lock (sync)
                {
                    data = incoming;
                    refreshDelaySeconds = RefreshMinutes * 60;
                }
                return true;
            }
            catch (Exception ex)
            {
                lock (sync)
                {
                    data = ResetRadarParser.WithNetworkFailure(data, ex.Message, DateTimeOffset.Now);
                    refreshDelaySeconds = RetrySeconds;
                }
                return false;
            }
        }

        public bool TryCreateNotification(out ResetRadarNotification notification)
        {
            notification = null;
            ResetRadarData current;
            lock (sync)
            {
                current = data.Clone();
                bool relevant = current.Status == ResetRadarStatus.CompletedToday ||
                    current.Status == ResetRadarStatus.ScheduledToday ||
                    current.Status == ResetRadarStatus.ScheduledUpcoming;
                if (!current.NetworkAvailable || current.IsFromCache || !relevant ||
                    String.IsNullOrWhiteSpace(current.EvidencePostId) ||
                    notifiedPostIds.Contains(current.EvidencePostId))
                    return false;

                notifiedPostIds.Add(current.EvidencePostId);
                while (notifiedPostIds.Count > 20)
                    notifiedPostIds.RemoveAt(0);
                SaveNotifiedPostIds(statePath, notifiedPostIds);
            }

            string body;
            if (current.Status == ResetRadarStatus.CompletedToday)
                body = "Tibo 已宣布完成 Codex 额度重置。点击查看原帖。";
            else if (current.Status == ResetRadarStatus.ScheduledToday)
                body = "Tibo 已预告今天重置 Codex 额度。点击查看原帖。";
            else
                body = "Tibo 发布了新的 Codex 额度重置预告。点击查看原帖。";

            notification = new ResetRadarNotification
            {
                Title = "Codex · Tibo 重置雷达",
                Body = body,
                SourceUrl = current.SourceUrl
            };
            return true;
        }

        private static ResetRadarData LoadCachedData(string path)
        {
            ResetRadarData initial = new ResetRadarData();
            if (!File.Exists(path))
                return initial;
            try
            {
                string payload = File.ReadAllText(path, Encoding.UTF8);
                ResetRadarData cached;
                string error;
                if (ResetRadarParser.TryParse(payload, DateTimeOffset.Now, out cached, out error))
                {
                    cached.NetworkAvailable = false;
                    cached.IsFromCache = true;
                    return cached;
                }
            }
            catch
            {
            }
            return initial;
        }

        private static string DownloadPayload()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(FeedUrl);
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = "CodexUsageOverlay/1.3.50";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.UseDefaultCredentials = false;
            request.Credentials = null;
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache";

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new WebException("重置雷达返回 HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
                if (response.ContentLength > MaxPayloadCharacters)
                    throw new InvalidDataException("重置雷达数据过大");
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    StringBuilder builder = new StringBuilder();
                    char[] buffer = new char[4096];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        builder.Append(buffer, 0, read);
                        if (builder.Length > MaxPayloadCharacters)
                            throw new InvalidDataException("重置雷达数据过大");
                    }
                    return builder.ToString();
                }
            }
        }

        private static void SaveTextAtomically(string path, string content)
        {
            try
            {
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch
            {
            }
        }

        private static List<string> LoadNotifiedPostIds(string path)
        {
            List<string> result = new List<string>();
            try
            {
                if (!File.Exists(path)) return result;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string prefix = line.StartsWith("NotifiedPostId=", StringComparison.Ordinal)
                        ? "NotifiedPostId="
                        : (line.StartsWith("LastNotifiedPostId=", StringComparison.Ordinal)
                            ? "LastNotifiedPostId="
                            : String.Empty);
                    if (prefix.Length == 0)
                        continue;
                    string value = line.Substring(prefix.Length).Trim();
                    if (value.Length <= 30 && IsDigits(value) && !result.Contains(value))
                        result.Add(value);
                }
            }
            catch
            {
            }
            while (result.Count > 20)
                result.RemoveAt(0);
            return result;
        }

        private static void SaveNotifiedPostIds(string path, List<string> postIds)
        {
            StringBuilder content = new StringBuilder();
            foreach (string postId in postIds)
                content.Append("NotifiedPostId=").Append(postId).Append(Environment.NewLine);
            SaveTextAtomically(path, content.ToString());
        }

        private static bool IsDigits(string value)
        {
            if (String.IsNullOrEmpty(value)) return false;
            for (int index = 0; index < value.Length; index++)
                if (!Char.IsDigit(value[index])) return false;
            return true;
        }

        public void Dispose()
        {
            lock (sync)
                disposed = true;
        }
    }

    internal static class ResetRadarParser
    {
        private static readonly Regex TimestampPattern = new Regex(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$",
            RegexOptions.CultureInvariant);

        public static bool TryParse(string json, DateTimeOffset now, out ResetRadarData result, out string error)
        {
            result = new ResetRadarData();
            error = String.Empty;
            try
            {
                if (String.IsNullOrWhiteSpace(json))
                    throw new InvalidDataException("重置雷达返回空数据");

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 262144;
                ResetFeed feed = serializer.Deserialize<ResetFeed>(json);
                ValidateFeed(feed);

                DateTimeOffset generatedAt = ParseTimestamp(feed.generatedAt, "generatedAt");
                DateTimeOffset? lastSuccessful = String.IsNullOrWhiteSpace(feed.lastSuccessfulCheckAt)
                    ? (DateTimeOffset?)null
                    : ParseTimestamp(feed.lastSuccessfulCheckAt, "lastSuccessfulCheckAt");
                DateTimeOffset nowUtc = now.ToUniversalTime();
                DateTimeOffset generatedUtc = generatedAt.ToUniversalTime();
                if (generatedUtc > nowUtc.AddMinutes(10))
                    throw new InvalidDataException("generatedAt 超出允许的时钟偏差");
                if (lastSuccessful.HasValue &&
                    lastSuccessful.Value.ToUniversalTime() > generatedUtc.AddMinutes(10))
                    throw new InvalidDataException("lastSuccessfulCheckAt 晚于数据生成时间");

                List<ParsedResetEvent> events = new List<ParsedResetEvent>();
                foreach (ResetFeedEvent item in feed.events)
                    events.Add(ParseEvent(item));

                bool fresh = feed.monitor.status == "ok" &&
                    HasFreshCheck(lastSuccessful, now);
                result = BuildResult(events, fresh, lastSuccessful, now);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                result = new ResetRadarData
                {
                    Status = ResetRadarStatus.Offline,
                    StatusLabel = "雷达离线",
                    Detail = "非官方重置数据无法验证",
                    LastError = ex.Message,
                    FetchedAt = now
                };
                return false;
            }
        }

        public static ResetRadarData WithNetworkFailure(ResetRadarData previous, string error, DateTimeOffset now)
        {
            ResetRadarData failed = previous == null ? new ResetRadarData() : previous.Clone();
            string previousLabel = failed.StatusLabel;
            bool hasFreshPrevious = HasFreshCheck(failed.LastSuccessfulCheckAt, now) &&
                failed.Status != ResetRadarStatus.Loading &&
                failed.Status != ResetRadarStatus.Offline;
            if (hasFreshPrevious)
            {
                failed.Detail = "网络重试中；上次成功数据仍在有效期内";
                failed.RefreshPending = true;
            }
            else
            {
                failed.Status = ResetRadarStatus.Offline;
                failed.StatusLabel = "雷达离线";
                failed.Detail = String.IsNullOrWhiteSpace(failed.EvidencePostId)
                    ? "无法连接非官方重置数据源"
                    : "连接失败；上次结果：" + previousLabel;
                failed.RefreshPending = false;
            }
            failed.NetworkAvailable = false;
            failed.IsFromCache = hasFreshPrevious || !String.IsNullOrWhiteSpace(failed.EvidencePostId);
            failed.LastError = String.IsNullOrWhiteSpace(error) ? "网络请求失败" : error;
            failed.FetchedAt = now;
            return failed;
        }

        private static bool HasFreshCheck(DateTimeOffset? lastSuccessful, DateTimeOffset now)
        {
            if (!lastSuccessful.HasValue)
                return false;
            TimeSpan age = now.ToUniversalTime() - lastSuccessful.Value.ToUniversalTime();
            return age.TotalMinutes >= -10d && age.TotalHours <= 30d;
        }

        private static ResetRadarData BuildResult(
            List<ParsedResetEvent> events,
            bool fresh,
            DateTimeOffset? lastSuccessful,
            DateTimeOffset now)
        {
            ResetRadarData result = new ResetRadarData();
            result.FetchedAt = now;
            result.LastSuccessfulCheckAt = lastSuccessful;

            ParsedResetEvent nextToday = FindNextScheduled(events, now, true);
            ParsedResetEvent latestCompletedToday = FindLatestCompletedToday(events, now);
            ParsedResetEvent nextUpcoming = FindNextScheduled(events, now, false);
            ParsedResetEvent evidence = null;

            if (!fresh)
            {
                result.Status = ResetRadarStatus.Offline;
                result.StatusLabel = "雷达离线";
                result.Detail = lastSuccessful.HasValue
                    ? "非官方数据已超过 30 小时未更新"
                    : "非官方数据源当前不可用";
                evidence = LatestEvent(events);
            }
            else if (nextToday != null)
            {
                result.Status = ResetRadarStatus.ScheduledToday;
                result.StatusLabel = "今日有预告";
                result.Detail = FormatScheduleDetail(nextToday);
                evidence = nextToday;
            }
            else if (latestCompletedToday != null)
            {
                result.Status = ResetRadarStatus.CompletedToday;
                result.StatusLabel = "今日已重置";
                result.Detail = "Tibo 已宣布完成额度重置 · " + FormatLocalTime(latestCompletedToday.OccurrenceAt.Value);
                evidence = latestCompletedToday;
            }
            else if (nextUpcoming != null)
            {
                result.Status = ResetRadarStatus.ScheduledUpcoming;
                result.StatusLabel = "重置已预告";
                result.Detail = FormatScheduleDetail(nextUpcoming);
                evidence = nextUpcoming;
            }
            else
            {
                result.Status = ResetRadarStatus.NoSignal;
                result.StatusLabel = "暂无重置信号";
                result.Detail = "今天没有明确的全局重置信号";
                evidence = FindSameDayCommentary(events, now) ?? LatestEvent(events);
            }

            if (evidence != null)
            {
                result.EventKind = evidence.Kind;
                result.EvidencePostId = evidence.PostId;
                result.SourceUrl = evidence.SourceUrl;
                result.AnnouncedAt = evidence.AnnouncedAt;
                result.EffectiveAt = evidence.EffectiveAt;
                result.EffectiveUntil = evidence.EffectiveUntil;
                result.Confidence = evidence.Confidence;
                result.ScopeLabel = FormatScope(evidence);
            }
            result.NetworkAvailable = fresh;
            result.IsFromCache = false;
            result.LastError = fresh ? String.Empty : "数据过期或监测器降级";
            return result;
        }

        private static void ValidateFeed(ResetFeed feed)
        {
            if (feed == null) throw new InvalidDataException("无法解析重置雷达数据");
            if (feed.schemaVersion != 1) throw new InvalidDataException("不支持的重置雷达数据版本");
            ParseTimestamp(feed.generatedAt, "generatedAt");
            if (!String.IsNullOrWhiteSpace(feed.lastSuccessfulCheckAt))
                ParseTimestamp(feed.lastSuccessfulCheckAt, "lastSuccessfulCheckAt");
            if (feed.monitor == null) throw new InvalidDataException("缺少 monitor");
            if (feed.monitor.status == "ok")
            {
                if (!String.IsNullOrEmpty(feed.monitor.errorCode))
                    throw new InvalidDataException("正常监测状态不能包含错误码");
            }
            else if (feed.monitor.status == "degraded")
            {
                string[] accepted = { "configuration_error", "request_failed", "invalid_response", "uncited_source" };
                if (Array.IndexOf(accepted, feed.monitor.errorCode) < 0)
                    throw new InvalidDataException("监测器错误码无效");
            }
            else
            {
                throw new InvalidDataException("监测器状态无效");
            }
            if (feed.events == null) throw new InvalidDataException("缺少 events");
            if (feed.events.Length > 50) throw new InvalidDataException("重置事件数量过多");
        }

        private static ParsedResetEvent ParseEvent(ResetFeedEvent item)
        {
            if (item == null) throw new InvalidDataException("重置事件为空");
            string[] kinds = { "reset_completed", "reset_scheduled", "banked_reset", "limit_increase", "uncertain" };
            if (Array.IndexOf(kinds, item.kind) < 0) throw new InvalidDataException("重置事件类型无效");
            if (Double.IsNaN(item.confidence) || Double.IsInfinity(item.confidence) || item.confidence < 0d || item.confidence > 1d)
                throw new InvalidDataException("重置事件置信度无效");
            if (String.IsNullOrWhiteSpace(item.text)) throw new InvalidDataException("重置事件缺少原帖文本");
            if (!RationaleMatches(item.kind, item.rationale)) throw new InvalidDataException("重置事件解释与类型不匹配");
            if (item.scope == null || item.scope.plans == null || item.scope.windows == null)
                throw new InvalidDataException("重置事件缺少适用范围");
            if (item.source == null) throw new InvalidDataException("重置事件缺少来源");
            ValidateSource(item.source);

            DateTimeOffset announcedAt = ParseTimestamp(item.announcedAt, "announcedAt");
            DateTimeOffset? effectiveAt = String.IsNullOrWhiteSpace(item.effectiveAt)
                ? (DateTimeOffset?)null
                : ParseTimestamp(item.effectiveAt, "effectiveAt");
            if (item.kind == "reset_scheduled" && !effectiveAt.HasValue)
                throw new InvalidDataException("重置预告缺少生效时间");
            if ((item.kind == "banked_reset" || item.kind == "limit_increase" || item.kind == "uncertain") && effectiveAt.HasValue)
                throw new InvalidDataException("该重置事件不能包含生效时间");

            ParsedResetEvent parsed = new ParsedResetEvent
            {
                Kind = item.kind,
                AnnouncedAt = announcedAt,
                EffectiveAt = effectiveAt,
                OccurrenceAt = item.kind == "reset_completed" ? (effectiveAt ?? announcedAt) : (DateTimeOffset?)null,
                PostId = item.source.postId,
                SourceUrl = item.source.url,
                Confidence = item.confidence,
                Plans = item.scope.plans,
                Windows = item.scope.windows
            };
            if (item.kind == "reset_scheduled")
                ResolveScheduleWindow(parsed);
            return parsed;
        }

        private static void ValidateSource(ResetFeedSource source)
        {
            string origin = String.IsNullOrWhiteSpace(source.origin)
                ? "x"
                : source.origin.Trim().ToLowerInvariant();
            if (origin == "operator")
            {
                if (!String.IsNullOrWhiteSpace(source.handle) || !String.IsNullOrWhiteSpace(source.url) ||
                    String.IsNullOrWhiteSpace(source.postId) ||
                    !Regex.IsMatch(source.postId, "^op_[A-Za-z0-9_-]{8,64}$", RegexOptions.CultureInvariant))
                    throw new InvalidDataException("操作员来源格式无效");
                return;
            }
            if (origin != "x")
                throw new InvalidDataException("重置事件来源类型无效");
            if (source.handle != "thsottiaux") throw new InvalidDataException("重置事件来源账号无效");
            if (String.IsNullOrEmpty(source.postId) || source.postId.Length > 30 || !AllDigits(source.postId))
                throw new InvalidDataException("重置事件来源编号无效");
            Uri url;
            if (!Uri.TryCreate(source.url, UriKind.Absolute, out url) ||
                url.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(url.Host, "x.com", StringComparison.OrdinalIgnoreCase) ||
                !String.IsNullOrEmpty(url.Query) || !String.IsNullOrEmpty(url.Fragment) ||
                url.AbsolutePath != "/thsottiaux/status/" + source.postId)
                throw new InvalidDataException("重置事件来源链接无效");
        }

        private static bool RationaleMatches(string kind, string rationale)
        {
            if (kind == "reset_completed") return
                rationale == "Explicit Codex quota reset announcement." ||
                rationale == "Explicit Codex reset-bank credit announcement." ||
                rationale == "Operator-confirmed Codex quota reset without an X announcement.";
            if (kind == "reset_scheduled") return rationale == "Explicit Codex quota reset schedule.";
            if (kind == "banked_reset") return rationale == "Banked reset announcement; not a completed reset.";
            if (kind == "limit_increase") return rationale == "Quota limit increase announcement; not a reset.";
            return rationale == "Not a clear reset signal." ||
                rationale == "Relevant announcement could not be classified safely.";
        }

        private static DateTimeOffset ParseTimestamp(string value, string field)
        {
            DateTimeOffset parsed;
            if (String.IsNullOrWhiteSpace(value) || !TimestampPattern.IsMatch(value) ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
                throw new InvalidDataException(field + " 必须是带时区的 RFC3339 时间");
            return parsed;
        }

        private static void ResolveScheduleWindow(ParsedResetEvent item)
        {
            DateTimeOffset start = item.EffectiveAt.Value;
            TimeZoneInfo pacific;
            try { pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
            catch { pacific = TimeZoneInfo.CreateCustomTimeZone("PacificFallback", TimeSpan.FromHours(-8), "Pacific", "Pacific"); }
            DateTimeOffset pacificTime = TimeZoneInfo.ConvertTime(start, pacific);
            bool dateOnly = pacificTime.Hour == 0 && pacificTime.Minute == 0 && pacificTime.Second == 0;
            if (dateOnly)
            {
                DateTime nextLocalMidnight = DateTime.SpecifyKind(pacificTime.Date.AddDays(1), DateTimeKind.Unspecified);
                TimeSpan nextOffset = pacific.GetUtcOffset(nextLocalMidnight);
                item.EffectiveUntil = new DateTimeOffset(nextLocalMidnight, nextOffset).ToUniversalTime().AddMinutes(-1);
            }
            else
            {
                item.EffectiveUntil = start;
            }
            item.IsDateRange = dateOnly;
        }

        private static ParsedResetEvent FindLatestCompletedToday(List<ParsedResetEvent> events, DateTimeOffset now)
        {
            ParsedResetEvent best = null;
            foreach (ParsedResetEvent item in events)
            {
                if (item.Kind != "reset_completed" || !item.OccurrenceAt.HasValue || item.OccurrenceAt.Value > now)
                    continue;
                if (!SameLocalDay(item.OccurrenceAt.Value, now))
                    continue;
                if (best == null || item.AnnouncedAt > best.AnnouncedAt)
                    best = item;
            }
            return best;
        }

        private static ParsedResetEvent FindNextScheduled(List<ParsedResetEvent> events, DateTimeOffset now, bool todayOnly)
        {
            ParsedResetEvent best = null;
            foreach (ParsedResetEvent item in events)
            {
                if (item.Kind != "reset_scheduled" || !item.EffectiveAt.HasValue || !item.EffectiveUntil.HasValue)
                    continue;
                if (item.EffectiveUntil.Value <= now)
                    continue;
                if (ScheduleHasCompleted(item, events, now))
                    continue;
                if (todayOnly && !IntersectsLocalDay(item.EffectiveAt.Value, item.EffectiveUntil.Value, now))
                    continue;
                if (best == null || item.EffectiveAt.Value < best.EffectiveAt.Value)
                    best = item;
            }
            return best;
        }

        private static bool ScheduleHasCompleted(
            ParsedResetEvent schedule,
            List<ParsedResetEvent> events,
            DateTimeOffset now)
        {
            foreach (ParsedResetEvent item in events)
            {
                if (item.Kind != "reset_completed" || !item.OccurrenceAt.HasValue)
                    continue;
                DateTimeOffset occurrence = item.OccurrenceAt.Value;
                if (occurrence <= now && occurrence >= schedule.EffectiveAt.Value &&
                    occurrence <= schedule.EffectiveUntil.Value)
                    return true;
            }
            return false;
        }

        private static ParsedResetEvent FindSameDayCommentary(List<ParsedResetEvent> events, DateTimeOffset now)
        {
            ParsedResetEvent best = null;
            foreach (ParsedResetEvent item in events)
            {
                bool commentary = item.Kind == "uncertain" || item.Kind == "banked_reset" || item.Kind == "limit_increase";
                if (!commentary || !SameLocalDay(item.AnnouncedAt, now))
                    continue;
                if (best == null || item.AnnouncedAt > best.AnnouncedAt)
                    best = item;
            }
            return best;
        }

        private static ParsedResetEvent LatestEvent(List<ParsedResetEvent> events)
        {
            ParsedResetEvent best = null;
            foreach (ParsedResetEvent item in events)
                if (best == null || item.AnnouncedAt > best.AnnouncedAt) best = item;
            return best;
        }

        private static bool SameLocalDay(DateTimeOffset value, DateTimeOffset now)
        {
            return value.ToLocalTime().Date == now.ToLocalTime().Date;
        }

        private static bool IntersectsLocalDay(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
        {
            DateTime localDayStart = now.ToLocalTime().Date;
            DateTime localDayEnd = localDayStart.AddDays(1);
            DateTime localStart = start.ToLocalTime().DateTime;
            DateTime localEnd = end.ToLocalTime().DateTime;
            return localEnd >= localDayStart && localStart < localDayEnd;
        }

        private static string FormatScheduleDetail(ParsedResetEvent item)
        {
            if (item.IsDateRange)
                return "Tibo 已预告重置 · 预计 " + FormatLocalDateTime(item.EffectiveAt.Value) + "—" +
                    FormatLocalDateTime(item.EffectiveUntil.Value) + "（本地时间）";
            return "Tibo 已预告重置 · 预计 " + FormatLocalDateTime(item.EffectiveAt.Value) + "（本地时间）";
        }

        private static string FormatLocalDateTime(DateTimeOffset value)
        {
            return value.ToLocalTime().ToString("M月d日 HH:mm", CultureInfo.GetCultureInfo("zh-CN"));
        }

        private static string FormatLocalTime(DateTimeOffset value)
        {
            return value.ToLocalTime().ToString("M月d日 HH:mm", CultureInfo.GetCultureInfo("zh-CN"));
        }

        private static string FormatScope(ParsedResetEvent item)
        {
            List<string> parts = new List<string>();
            if (Array.IndexOf(item.Plans, "all") >= 0) parts.Add("全部计划");
            else
            {
                if (Array.IndexOf(item.Plans, "plus") >= 0) parts.Add("Plus");
                if (Array.IndexOf(item.Plans, "pro") >= 0) parts.Add("Pro");
                if (Array.IndexOf(item.Plans, "team") >= 0) parts.Add("Team");
                if (Array.IndexOf(item.Plans, "business") >= 0) parts.Add("Business");
                if (Array.IndexOf(item.Plans, "enterprise") >= 0) parts.Add("Enterprise");
            }
            if (Array.IndexOf(item.Windows, "weekly") >= 0) parts.Add("周额度");
            if (Array.IndexOf(item.Windows, "five_hour") >= 0) parts.Add("5 小时额度");
            return String.Join(" · ", parts.ToArray());
        }

        private static bool AllDigits(string value)
        {
            for (int index = 0; index < value.Length; index++)
                if (!Char.IsDigit(value[index])) return false;
            return true;
        }

        private sealed class ParsedResetEvent
        {
            public string Kind;
            public DateTimeOffset AnnouncedAt;
            public DateTimeOffset? EffectiveAt;
            public DateTimeOffset? EffectiveUntil;
            public DateTimeOffset? OccurrenceAt;
            public bool IsDateRange;
            public string PostId;
            public string SourceUrl;
            public double Confidence;
            public string[] Plans;
            public string[] Windows;
        }

        private sealed class ResetFeed
        {
            public int schemaVersion { get; set; }
            public string generatedAt { get; set; }
            public string lastSuccessfulCheckAt { get; set; }
            public ResetFeedMonitor monitor { get; set; }
            public ResetFeedEvent[] events { get; set; }
        }

        private sealed class ResetFeedMonitor
        {
            public string status { get; set; }
            public string errorCode { get; set; }
        }

        private sealed class ResetFeedEvent
        {
            public string kind { get; set; }
            public string announcedAt { get; set; }
            public string effectiveAt { get; set; }
            public ResetFeedScope scope { get; set; }
            public ResetFeedSource source { get; set; }
            public double confidence { get; set; }
            public string rationale { get; set; }
            public string text { get; set; }
        }

        private sealed class ResetFeedScope
        {
            public string[] plans { get; set; }
            public string[] windows { get; set; }
        }

        private sealed class ResetFeedSource
        {
            public string origin { get; set; }
            public string handle { get; set; }
            public string postId { get; set; }
            public string url { get; set; }
        }
    }
}
