using System;
using System.Collections.Generic;
using System.IO;
using CodexUsageOverlay;

internal static class ResetCreditExpiryTests
{
    private static readonly DateTime Now = new DateTime(2030, 10, 4, 4, 0, 0, DateTimeKind.Utc);
    public static void Verify()
    {
        var usage = Parse(3, new object[] {
            Credit("used", "consumed", Now.AddMinutes(10)),
            Credit("later", "available", Now.AddDays(3)),
            Credit("first", "available", Now.AddHours(2)),
            Credit("first", "available", Now.AddHours(2)),
            Credit("same", "available", Now.AddHours(2)),
            Obj("id", "bad", "status", "available", "expiresAt", Int64.MaxValue)
        });
        Assert(usage.ResetCreditsExpireUtc == Now.AddHours(2) && usage.ResetCreditsExpiringCount == 2,
            "must select and count the earliest available credits without duplicate IDs");
        Assert(ResetCreditExpiry.BuildLabel(usage, Now).Contains("最早2时到期"), "missing countdown");
        Assert(ResetCreditExpiry.BuildLabel(usage, Now.AddMinutes(1)).Contains("1时59分"), "countdown frozen");
        Assert(ResetCreditExpiry.FormatRemaining(TimeSpan.FromSeconds(10)) == "不足1分", "subminute rounding");
        Assert(ResetCreditExpiry.FormatRemaining(TimeSpan.FromHours(26)) == "1天2时", "day/hour format");
        Assert(ResetCreditExpiry.BuildLabel(usage, Now.AddHours(2)).Contains("已到期"), "negative countdown");
        var cleared = Parse(0, new object[] { Credit("old", "available", Now.AddHours(2)) });
        UsageDataMerger.MergeInto(usage, cleared);
        Assert(usage.AvailableResetCredits == 0 && !usage.ResetCreditsExpireUtc.HasValue,
            "zero credits retained an expiry");
        usage = Parse(1, new object[] { Credit("first", "available", Now.AddHours(2)) });
        UsageDataMerger.MergeInto(usage, Parse(1, null));
        Assert(!usage.ResetCreditsExpireUtc.HasValue, "missing expiry details retained an obsolete reminder");

        string statePath = Path.Combine(Path.GetTempPath(), "codex-reset-expiry-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            var reminders = new ResetCreditExpiryReminder(statePath);
            ResetRadarNotification notification;
            usage = Parse(1, new object[] { Credit("first", "available", Now.AddHours(25)) });
            Fresh(usage, Now);
            Assert(!reminders.TryCreateNotification(usage, Now, out notification), "early reminder");
            Fresh(usage, Now.AddHours(1));
            Assert(reminders.TryCreateNotification(usage, Now.AddHours(1), out notification), "24h boundary missing");
            Assert(notification.Body.Contains("1张") && notification.Body.Contains("到期"), "reminder details missing");
            Assert(!reminders.TryCreateNotification(usage, Now.AddHours(1), out notification), "repeated reminder");
            reminders = new ResetCreditExpiryReminder(statePath);
            Assert(!reminders.TryCreateNotification(usage, Now.AddHours(1), out notification), "restart repeated reminder");
            Fresh(usage, Now.AddHours(24));
            Assert(reminders.TryCreateNotification(usage, Now.AddHours(24), out notification), "1h boundary missing");
            Fresh(usage, Now.AddHours(25));
            Assert(!reminders.TryCreateNotification(usage, Now.AddHours(25), out notification), "expired credit notified");

            usage = Parse(1, new object[] { Credit("new", "available", Now.AddHours(4)) });
            Fresh(usage, Now);
            usage.Source = "缓存";
            Assert(!reminders.TryCreateNotification(usage, Now, out notification), "cached data notified");
            Fresh(usage, Now.AddMinutes(-6));
            Assert(!reminders.TryCreateNotification(usage, Now, out notification), "stale data notified");
            Fresh(usage, Now); usage.LastError = "offline";
            Assert(!reminders.TryCreateNotification(usage, Now, out notification), "failed refresh notified");
            Fresh(usage, Now);
            Assert(reminders.TryCreateNotification(usage, Now, out notification), "new expiry was suppressed");
        }
        finally { if (File.Exists(statePath)) File.Delete(statePath); }
    }
    private static void Fresh(UsageData usage, DateTime now)
    { usage.UpdatedUtc = now; usage.Source = "Codex CLI app-server"; usage.LastError = String.Empty; }
    private static UsageData Parse(int count, object[] credits)
    {
        var reset = Obj("availableCount", count);
        if (credits != null) reset["credits"] = credits;
        var result = Obj("rateLimits", Obj("planType", "pro", "primary",
            Obj("windowDurationMins", 300, "usedPercent", 10)), "rateLimitResetCredits", reset);
        var usage = new UsageData(); string plan;
        Assert(CodexAppServerClient.ParseRateLimits(result, usage, out plan), "quota parse failed");
        return usage;
    }
    private static Dictionary<string, object> Credit(string id, string status, DateTime expiry)
    { return Obj("id", id, "status", status, "expiresAt", (long)(expiry - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds); }
    private static Dictionary<string, object> Obj(params object[] fields)
    {
        var result = new Dictionary<string, object>();
        for (int i = 0; i < fields.Length; i += 2) result[(string)fields[i]] = fields[i + 1];
        return result;
    }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
