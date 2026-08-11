using System;
using System.Globalization;
using CodexUsageOverlay;

internal static class ResetRadarTests
{
    private static int failures;

    private static int Main()
    {
        Run("completed reset is today", CompletedResetIsToday);
        Run("future schedule today is pending", FutureScheduleTodayIsPending);
        Run("expired exact schedule is not today", ExpiredExactScheduleIsNotToday);
        Run("scheduled date range crosses Shanghai local day", DateRangeCrossesShanghaiLocalDay);
        Run("Pacific date range honors daylight saving transition", DateRangeHonorsDaylightSavingTransition);
        Run("exactly thirty hours is still fresh", ExactlyThirtyHoursIsFresh);
        Run("over thirty hours is offline", OverThirtyHoursIsOffline);
        Run("future feed timestamp is rejected", FutureFeedTimestampIsRejected);
        Run("bare local timestamp is rejected", BareTimestampIsRejected);
        Run("wrong source host is rejected", WrongSourceHostIsRejected);
        Run("confidence and countdown are displayed", ConfidenceAndCountdownAreDisplayed);
        Run("completed banner expires at local midnight", CompletedBannerExpiresAtLocalMidnight);
        Run("cached radar is not shown as live", CachedRadarIsNotShownAsLive);
        Run("scheduled headline uses reset time", ScheduledHeadlineUsesResetTime);
        Run("completed reset overrides active schedule", CompletedResetOverridesActiveSchedule);
        Run("completed schedule stays cleared after local midnight", CompletedScheduleStaysClearedAfterLocalMidnight);
        Run("layered bitmap uses logical DPI", RenderingCompatibilityTests.LayeredBitmapUsesLogicalDpi);
        Run("unsafe font falls back to text font", RenderingCompatibilityTests.UnsafeFontFallsBackToTextFont);
        Run("text renders at mixed DPI scale", RenderingCompatibilityTests.TextRendersAtMixedDpiScale);
        Run("right click main usage requests exit", OverlayInteractionTests.RightClickMainUsageRequestsExit);
        Run("other mouse buttons do not request exit", OverlayInteractionTests.OtherButtonsDoNotRequestExit);
        Run("right click outside main usage does not request exit", OverlayInteractionTests.RightClickOutsideMainUsageDoesNotRequestExit);
        Run("right drag from another region does not request exit", OverlayInteractionTests.RightDragFromOtherRegionDoesNotRequestExit);
        Run("account and quota are required for trusted usage", UsageTrustPolicyTests.AccountAndQuotaAreRequiredForTrustedSnapshot);
        Run("ChatGPT nullable email and real Free window are accepted", UsageTrustPolicyTests.ChatgptWithNullEmailAndFreeWindowIsAccepted);
        Run("non-ChatGPT identity is rejected", UsageTrustPolicyTests.NonChatgptIdentityIsRejected);
        Run("quota plan overrides account plan", UsageTrustPolicyTests.QuotaPlanOverridesAccountPlan);
        Run("valid window plan overrides account fallback", UsageTrustPolicyTests.ValidWindowPlanOverridesAccountFallback);
        Run("invalid window plan does not leak", UsageTrustPolicyTests.InvalidWindowPlanDoesNotLeakIntoSnapshot);
        Run("quota window requires duration", UsageTrustPolicyTests.WindowWithoutDurationIsRejected);
        Run("weekly partial response preserves cached fields", UsageTrustPolicyTests.PartialWeeklyWindowPreservesCachedFields);
        Run("explicit zero reset credits override cache", UsageTrustPolicyTests.ExplicitZeroCreditsOverridesCachedCount);
        Run("valid Pro snapshot repairs cached Free", UsageTrustPolicyTests.ValidProSnapshotRepairsCachedFree);
        Run("rejected usage snapshot leaves cache untouched", UsageTrustPolicyTests.RejectedSnapshotLeavesCacheUntouched);
        Run("newer stable GitHub release is detected", GitHubReleaseUpdateTests.NewerStableReleaseIsDetected);
        Run("GitHub prerelease is ignored", GitHubReleaseUpdateTests.PrereleaseIsIgnored);
        Run("foreign GitHub release URL is rejected", GitHubReleaseUpdateTests.ForeignReleaseUrlIsRejected);
        Run("current GitHub release does not prompt", GitHubReleaseUpdateTests.CurrentReleaseDoesNotPrompt);
        Run("GitHub release URL allowlist is strict", GitHubReleaseUpdateTests.ReleaseUrlAllowlistIsStrict);

        Console.WriteLine(failures == 0 ? "All reset radar tests passed." : failures + " reset radar test(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void CompletedResetIsToday()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-28T12:30:00Z");
        ResetRadarData data = Parse(Feed(
            "2026-07-28T12:30:00Z",
            "2026-07-28T12:30:00Z",
            CompletedEvent("2026-07-28T12:00:00Z", "1001")), now);
        Assert(data.Status == ResetRadarStatus.CompletedToday, data.Status.ToString());
        Assert(data.SourceUrl.EndsWith("/1001", StringComparison.Ordinal), data.SourceUrl);
    }

    private static void FutureScheduleTodayIsPending()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-28T12:00:00Z");
        ResetRadarData data = Parse(Feed(
            "2026-07-28T12:00:00Z",
            "2026-07-28T12:00:00Z",
            ScheduledEvent("2026-07-28T11:00:00Z", "2026-07-28T13:00:00Z", "1002")), now);
        Assert(data.Status == ResetRadarStatus.ScheduledToday, data.Status.ToString());
    }

    private static void ConfidenceAndCountdownAreDisplayed()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-10T02:02:27Z");
        ResetRadarData data = Parse(Feed(
            "2026-08-10T02:02:27Z",
            "2026-08-10T02:02:27Z",
            ScheduledEvent("2026-08-08T20:34:50Z", "2026-08-10T07:00:00Z", "1007")), now);
        Assert(data.Confidence.HasValue && Math.Abs(data.Confidence.Value - 0.95d) < 0.0001d,
            data.Confidence.HasValue ? data.Confidence.Value.ToString() : "missing confidence");
        Assert(ResetRadarDisplay.ConfidenceSuffix(data) == " · 置信度 95%",
            ResetRadarDisplay.ConfidenceSuffix(data));
        CultureInfo culture = CultureInfo.GetCultureInfo("zh-CN");
        string localStart = data.EffectiveAt.Value.ToLocalTime().ToString("M月d日 HH:mm", culture);
        string localEnd = data.EffectiveUntil.Value.ToLocalTime().ToString("M月d日 HH:mm", culture);
        string expected = "计划重置：" + localStart + "—" + localEnd +
            " · 4小时57分33秒后—28小时56分33秒后";
        Assert(ResetRadarDisplay.BuildPrimaryLine(data, now) ==
            expected,
            ResetRadarDisplay.BuildPrimaryLine(data, now));
    }

    private static void CompletedBannerExpiresAtLocalMidnight()
    {
        DateTimeOffset localOccurrence = new DateTimeOffset(
            new DateTime(2026, 8, 10, 23, 59, 30),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 10, 23, 59, 30)));
        ResetRadarData data = new ResetRadarData
        {
            Status = ResetRadarStatus.CompletedToday,
            AnnouncedAt = localOccurrence,
            SourceUrl = "https://x.com/thsottiaux/status/1008",
            NetworkAvailable = true
        };
        Assert(ResetRadarDisplay.ShouldShow(data, localOccurrence), "completion hidden before midnight");
        Assert(!ResetRadarDisplay.ShouldShow(data, localOccurrence.AddMinutes(1)),
            "completion remained visible after midnight");
    }

    private static void CachedRadarIsNotShownAsLive()
    {
        ResetRadarData data = new ResetRadarData
        {
            Status = ResetRadarStatus.ScheduledToday,
            EffectiveAt = DateTimeOffset.Now.AddHours(1),
            EffectiveUntil = DateTimeOffset.Now.AddHours(2),
            SourceUrl = "https://x.com/thsottiaux/status/1009",
            NetworkAvailable = false,
            IsFromCache = true
        };
        Assert(!ResetRadarDisplay.ShouldShow(data, DateTimeOffset.Now), "cached event was shown as live");
    }

    private static void ScheduledHeadlineUsesResetTime()
    {
        DateTime localNowValue = new DateTime(2026, 8, 10, 10, 2, 27, DateTimeKind.Unspecified);
        DateTime localStartValue = new DateTime(2026, 8, 10, 15, 0, 0, DateTimeKind.Unspecified);
        DateTimeOffset localNow = new DateTimeOffset(
            localNowValue,
            TimeZoneInfo.Local.GetUtcOffset(localNowValue));
        DateTimeOffset localStart = new DateTimeOffset(
            localStartValue,
            TimeZoneInfo.Local.GetUtcOffset(localStartValue));
        ResetRadarData data = new ResetRadarData
        {
            Status = ResetRadarStatus.ScheduledToday,
            StatusLabel = "今日有预告",
            EffectiveAt = localStart
        };
        Assert(ResetRadarDisplay.BuildHeadline(data, localNow) == "预计今日15:00后有重置",
            ResetRadarDisplay.BuildHeadline(data, localNow));
        Assert(ResetRadarDisplay.BuildPillLabel(data, localNow) == "15:00后重置",
            ResetRadarDisplay.BuildPillLabel(data, localNow));
        data.EffectiveUntil = localStart.AddHours(24);
        DateTimeOffset activeNow = localStart.AddMinutes(1);
        Assert(ResetRadarDisplay.BuildHeadline(data, activeNow) == "重置时段已开始",
            ResetRadarDisplay.BuildHeadline(data, activeNow));
        Assert(ResetRadarDisplay.BuildPillLabel(data, activeNow) == "重置进行中",
            ResetRadarDisplay.BuildPillLabel(data, activeNow));
    }

    private static void CompletedResetOverridesActiveSchedule()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        string events = ScheduledEvent("2026-08-08T20:34:50Z", "2026-08-10T07:00:00Z", "1010") +
            "," + CompletedEvent("2026-08-10T12:00:00Z", "1011");
        ResetRadarData data = Parse(Feed(
            "2026-08-10T12:00:00Z",
            "2026-08-10T12:00:00Z",
            events), now);
        Assert(data.Status == ResetRadarStatus.CompletedToday, data.Status.ToString());
        Assert(data.EvidencePostId == "1011", data.EvidencePostId);
    }

    private static void CompletedScheduleStaysClearedAfterLocalMidnight()
    {
        DateTimeOffset scheduleStart = DateTimeOffset.Parse("2026-08-10T07:00:00Z");
        DateTime localMidnightValue = scheduleStart.ToLocalTime().Date.AddDays(1);
        DateTimeOffset localMidnight = new DateTimeOffset(
            localMidnightValue,
            TimeZoneInfo.Local.GetUtcOffset(localMidnightValue));
        DateTimeOffset completedAt = localMidnight.AddMinutes(-1);
        DateTimeOffset now = localMidnight.AddMinutes(1);
        string events = ScheduledEvent(
            "2026-08-08T20:34:50Z",
            scheduleStart.ToString("o", CultureInfo.InvariantCulture),
            "1012") + "," + CompletedEvent(
                completedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                "1013");
        ResetRadarData data = Parse(Feed(
            now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            events), now);
        Assert(data.Status == ResetRadarStatus.NoSignal, data.Status.ToString());
    }

    private static void ExpiredExactScheduleIsNotToday()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-28T12:00:00Z");
        ResetRadarData data = Parse(Feed(
            "2026-07-28T12:00:00Z",
            "2026-07-28T12:00:00Z",
            ScheduledEvent("2026-07-28T10:00:00Z", "2026-07-28T11:00:00Z", "1003")), now);
        Assert(data.Status == ResetRadarStatus.NoSignal, data.Status.ToString());
    }

    private static void DateRangeCrossesShanghaiLocalDay()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-10T16:00:00Z");
        ResetRadarData data = Parse(Feed(
            "2026-08-10T16:00:00Z",
            "2026-08-10T16:00:00Z",
            ScheduledEvent("2026-08-08T20:34:50Z", "2026-08-10T07:00:00Z", "1004")), now);
        Assert(data.Status == ResetRadarStatus.ScheduledToday, data.Status.ToString());
        Assert(data.EffectiveUntil.HasValue && data.EffectiveUntil.Value == DateTimeOffset.Parse("2026-08-11T06:59:00Z"),
            data.EffectiveUntil.HasValue ? data.EffectiveUntil.Value.ToString("o") : "missing end");
    }

    private static void ExactlyThirtyHoursIsFresh()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-28T12:00:00Z");
        ResetRadarData data = Parse(Feed(
            "2026-07-28T12:00:00Z",
            "2026-07-27T06:00:00Z",
            String.Empty), now);
        Assert(data.Status == ResetRadarStatus.NoSignal, data.Status.ToString());
    }

    private static void DateRangeHonorsDaylightSavingTransition()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-03-08T09:00:00Z");
        ResetRadarData data = Parse(Feed(
            "2026-03-08T09:00:00Z",
            "2026-03-08T09:00:00Z",
            ScheduledEvent("2026-03-07T20:00:00Z", "2026-03-08T08:00:00Z", "1006")), now);
        Assert(data.EffectiveUntil.HasValue && data.EffectiveUntil.Value == DateTimeOffset.Parse("2026-03-09T06:59:00Z"),
            data.EffectiveUntil.HasValue ? data.EffectiveUntil.Value.ToString("o") : "missing end");
    }

    private static void OverThirtyHoursIsOffline()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-28T12:00:01Z");
        ResetRadarData data = Parse(Feed(
            "2026-07-28T12:00:01Z",
            "2026-07-27T06:00:00Z",
            String.Empty), now);
        Assert(data.Status == ResetRadarStatus.Offline, data.Status.ToString());
    }

    private static void BareTimestampIsRejected()
    {
        ResetRadarData data;
        string error;
        string json = Feed("2026-07-28T12:00:00", "2026-07-28T12:00:00Z", String.Empty);
        bool parsed = ResetRadarParser.TryParse(json, DateTimeOffset.Parse("2026-07-28T12:00:00Z"), out data, out error);
        Assert(!parsed, "unexpectedly parsed");
    }

    private static void FutureFeedTimestampIsRejected()
    {
        ResetRadarData data;
        string error;
        string json = Feed("2026-07-28T12:20:01Z", "2026-07-28T12:00:00Z", String.Empty);
        bool parsed = ResetRadarParser.TryParse(json, DateTimeOffset.Parse("2026-07-28T12:00:00Z"), out data, out error);
        Assert(!parsed, "unexpectedly parsed");
    }

    private static void WrongSourceHostIsRejected()
    {
        ResetRadarData data;
        string error;
        string json = Feed(
            "2026-07-28T12:00:00Z",
            "2026-07-28T12:00:00Z",
            CompletedEvent("2026-07-28T11:00:00Z", "1005")).Replace("https://x.com/", "https://example.com/");
        bool parsed = ResetRadarParser.TryParse(json, DateTimeOffset.Parse("2026-07-28T12:00:00Z"), out data, out error);
        Assert(!parsed, "unexpectedly parsed");
    }

    private static ResetRadarData Parse(string json, DateTimeOffset now)
    {
        ResetRadarData data;
        string error;
        if (!ResetRadarParser.TryParse(json, now, out data, out error))
            throw new InvalidOperationException(error);
        return data;
    }

    private static string Feed(string generatedAt, string lastSuccessfulCheckAt, string events)
    {
        return "{\"schemaVersion\":1," +
            "\"generatedAt\":\"" + generatedAt + "\"," +
            "\"lastSuccessfulCheckAt\":\"" + lastSuccessfulCheckAt + "\"," +
            "\"monitor\":{\"status\":\"ok\",\"errorCode\":null}," +
            "\"events\":[" + events + "]}";
    }

    private static string CompletedEvent(string announcedAt, string postId)
    {
        return Event("reset_completed", announcedAt, null, postId,
            "Explicit Codex quota reset announcement.");
    }

    private static string ScheduledEvent(string announcedAt, string effectiveAt, string postId)
    {
        return Event("reset_scheduled", announcedAt, effectiveAt, postId,
            "Explicit Codex quota reset schedule.");
    }

    private static string Event(string kind, string announcedAt, string effectiveAt, string postId, string rationale)
    {
        string effective = effectiveAt == null ? "null" : "\"" + effectiveAt + "\"";
        return "{\"kind\":\"" + kind + "\"," +
            "\"announcedAt\":\"" + announcedAt + "\"," +
            "\"effectiveAt\":" + effective + "," +
            "\"scope\":{\"plans\":[\"all\"],\"windows\":[\"weekly\"]}," +
            "\"source\":{\"handle\":\"thsottiaux\",\"postId\":\"" + postId + "\"," +
            "\"url\":\"https://x.com/thsottiaux/status/" + postId + "\"}," +
            "\"confidence\":0.95,\"rationale\":\"" + rationale + "\"," +
            "\"text\":\"Test reset announcement.\"}";
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("FAIL " + name + ": " + ex.Message);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
