using System;
using System.Collections.Generic;

namespace CodexUsageOverlay
{
    internal static class UsageTrustPolicyTests
    {
        public static void AccountAndQuotaAreRequiredForTrustedSnapshot()
        {
            Assert(UsageTrustPolicy.HasVerifiedSnapshot(true, true),
                "authenticated account with quota was rejected");
            Assert(!UsageTrustPolicy.HasVerifiedSnapshot(true, false),
                "account-only snapshot was trusted");
            Assert(!UsageTrustPolicy.HasVerifiedSnapshot(false, true),
                "quota-only snapshot was trusted");
            Assert(!UsageTrustPolicy.HasVerifiedSnapshot(false, false),
                "empty snapshot was trusted");
        }

        public static void ChatgptWithNullEmailAndFreeWindowIsAccepted()
        {
            UsageData usage = new UsageData();
            bool accepted = CodexAppServerClient.TryParseTrustedSnapshot(
                Account("chatgpt", "free", null),
                RateLimits("free", Window(300, 12d, 1786406400L), null,
                    true, null, true, 0L), usage);

            Assert(accepted, "ChatGPT account with nullable email was rejected");
            Assert(usage.HasPlan && usage.Plan == "Free", "real Free plan was not accepted");
            Assert(usage.HasShortRemaining && usage.ShortRemaining == 88,
                "short quota was not parsed");
            Assert(usage.HasRateLimitStatus && usage.RateLimitStatus == "正常",
                "explicit null status was not treated as normal");
            Assert(usage.HasAvailableResetCredits && usage.AvailableResetCredits == 0,
                "explicit zero reset credits were not preserved");
        }

        public static void NonChatgptIdentityIsRejected()
        {
            UsageData apiKeyUsage = new UsageData();
            bool apiKeyAccepted = CodexAppServerClient.TryParseTrustedSnapshot(
                Account("apiKey", null, null),
                RateLimits("free", Window(300, 10d, 1786406400L), null,
                    false, null, false, 0L), apiKeyUsage);
            Assert(!apiKeyAccepted, "API-key identity was trusted as a ChatGPT subscription");

            UsageData anonymousUsage = new UsageData();
            bool anonymousAccepted = CodexAppServerClient.TryParseTrustedSnapshot(
                ObjectOf("account", null),
                RateLimits("free", Window(300, 10d, 1786406400L), null,
                    false, null, false, 0L), anonymousUsage);
            Assert(!anonymousAccepted, "anonymous quota response was trusted");
        }

        public static void QuotaPlanOverridesAccountPlan()
        {
            UsageData usage = new UsageData();
            bool accepted = CodexAppServerClient.TryParseTrustedSnapshot(
                Account("chatgpt", "free", "user@example.com"),
                RateLimits("pro", Window(300, 25d, 1786406400L), null,
                    false, null, false, 0L), usage);

            Assert(accepted, "valid snapshot was rejected");
            Assert(usage.HasPlan && usage.Plan == "Pro",
                "quota-side Pro plan did not override stale account-side Free plan");
        }

        public static void ValidWindowPlanOverridesAccountFallback()
        {
            IDictionary<string, object> weekly = Window(10080, 25d, 1786406400L);
            weekly["planType"] = "pro";
            UsageData usage = new UsageData();
            bool accepted = CodexAppServerClient.TryParseTrustedSnapshot(
                Account("chatgpt", "free", null),
                RateLimits(null, null, weekly, false, null, false, 0L), usage);

            Assert(accepted, "valid nested quota plan snapshot was rejected");
            Assert(usage.HasPlan && usage.Plan == "Pro",
                "valid window-side Pro plan did not override stale account-side Free plan");
        }

        public static void InvalidWindowPlanDoesNotLeakIntoSnapshot()
        {
            IDictionary<string, object> invalidShort = ObjectOf(
                "usedPercent", 0,
                "planType", "pro");
            UsageData usage = new UsageData();
            bool accepted = CodexAppServerClient.TryParseTrustedSnapshot(
                Account("chatgpt", "free", null),
                RateLimits(null, invalidShort, Window(10080, 25d, null),
                    false, null, false, 0L), usage);

            Assert(accepted, "valid weekly window was rejected");
            Assert(usage.HasPlan && usage.Plan == "Free",
                "plan from invalid quota window leaked into trusted snapshot");
        }

        public static void WindowWithoutDurationIsRejected()
        {
            UsageData usage = new UsageData();
            IDictionary<string, object> partialWindow = ObjectOf(
                "usedPercent", 0,
                "resetsAt", 1786406400L);
            bool accepted = CodexAppServerClient.TryParseTrustedSnapshot(
                Account("chatgpt", "free", null),
                RateLimits("free", partialWindow, null,
                    false, null, false, 0L), usage);

            Assert(!accepted, "partial window without duration was trusted");
            Assert(!usage.HasPlan, "rejected snapshot still supplied a plan update");
        }

        public static void PartialWeeklyWindowPreservesCachedFields()
        {
            UsageData cached = CachedProUsage();
            UsageData incoming = new UsageData();
            bool accepted = CodexAppServerClient.TryParseTrustedSnapshot(
                Account("chatgpt", "pro", null),
                RateLimits("pro", Window(10080, 42d, null), null,
                    false, null, false, 0L), incoming);

            Assert(accepted, "weekly-only snapshot was rejected");
            Assert(UsageDataMerger.MergeInto(cached, incoming), "weekly update did not change cache");
            Assert(cached.Plan == "Pro", "trusted plan was lost");
            Assert(cached.WeeklyRemaining == 58, "weekly remaining was not updated");
            Assert(cached.WeeklyResetText == "旧周重置", "missing weekly reset cleared cache");
            Assert(cached.ShortRemaining == 71 && cached.ShortResetText == "旧短重置",
                "weekly-only response cleared short quota cache");
            Assert(cached.RateLimitStatus == "正常", "missing status cleared cache");
            Assert(cached.AvailableResetCredits == 2, "missing reset credits cleared cache");
        }

        public static void ExplicitZeroCreditsOverridesCachedCount()
        {
            UsageData cached = CachedProUsage();
            UsageData incoming = new UsageData();
            bool accepted = CodexAppServerClient.TryParseTrustedSnapshot(
                Account("chatgpt", "pro", null),
                RateLimits("pro", Window(300, 20d, null), null,
                    false, null, true, 0L), incoming);

            Assert(accepted, "valid reset-credit snapshot was rejected");
            UsageDataMerger.MergeInto(cached, incoming);
            Assert(cached.AvailableResetCredits == 0,
                "explicit zero reset credits did not replace cached count");
        }

        public static void ValidProSnapshotRepairsCachedFree()
        {
            UsageData cached = CachedProUsage();
            cached.Plan = "Free";
            UsageData incoming = new UsageData();
            bool accepted = CodexAppServerClient.TryParseTrustedSnapshot(
                Account("chatgpt", "free", null),
                RateLimits("pro", Window(300, 20d, null), null,
                    false, null, false, 0L), incoming);

            Assert(accepted, "valid Pro snapshot was rejected");
            UsageDataMerger.MergeInto(cached, incoming);
            Assert(cached.Plan == "Pro", "trusted Pro snapshot did not repair cached Free plan");
        }

        public static void RejectedSnapshotLeavesCacheUntouched()
        {
            UsageData cached = CachedProUsage();
            UsageData incoming = new UsageData();
            bool accepted = CodexAppServerClient.TryParseTrustedSnapshot(
                Account("chatgpt", "free", null),
                RateLimits("free", ObjectOf("usedPercent", 0), null,
                    false, null, false, 0L), incoming);
            if (accepted)
                UsageDataMerger.MergeInto(cached, incoming);

            Assert(!accepted, "invalid snapshot was accepted");
            Assert(cached.Plan == "Pro", "invalid snapshot changed cached plan");
            Assert(cached.ShortRemaining == 71 && cached.WeeklyRemaining == 44,
                "invalid snapshot changed cached quota");
            Assert(cached.AvailableResetCredits == 2,
                "invalid snapshot changed cached reset credits");

            cached.Plan = "Free";
            incoming = new UsageData();
            accepted = CodexAppServerClient.TryParseTrustedSnapshot(
                Account("chatgpt", "free", null),
                RateLimits("free", ObjectOf("usedPercent", 0), null,
                    false, null, false, 0L), incoming);
            if (accepted)
                UsageDataMerger.MergeInto(cached, incoming);
            Assert(!accepted && cached.Plan == "Free",
                "rejected snapshot changed an existing Free cache entry");
        }

        private static UsageData CachedProUsage()
        {
            return new UsageData
            {
                Plan = "Pro",
                ShortRemaining = 71,
                ShortResetText = "旧短重置",
                WeeklyRemaining = 44,
                WeeklyResetText = "旧周重置",
                RateLimitStatus = "正常",
                AvailableResetCredits = 2
            };
        }

        private static IDictionary<string, object> Account(string type, string plan, object email)
        {
            IDictionary<string, object> account = ObjectOf("type", type);
            if (plan != null)
                account["planType"] = plan;
            if (String.Equals(type, "chatgpt", StringComparison.OrdinalIgnoreCase))
                account["email"] = email;
            return ObjectOf("account", account);
        }

        private static IDictionary<string, object> Window(long durationMinutes, double usedPercent,
            long? resetsAt)
        {
            IDictionary<string, object> window = ObjectOf(
                "windowDurationMins", durationMinutes,
                "usedPercent", usedPercent);
            if (resetsAt.HasValue)
                window["resetsAt"] = resetsAt.Value;
            return window;
        }

        private static IDictionary<string, object> RateLimits(string plan,
            IDictionary<string, object> primary, IDictionary<string, object> secondary,
            bool includeStatus, object status, bool includeCredits, long credits)
        {
            IDictionary<string, object> limits = ObjectOf("planType", plan);
            if (primary != null)
                limits["primary"] = primary;
            if (secondary != null)
                limits["secondary"] = secondary;
            if (includeStatus)
                limits["rateLimitReachedType"] = status;

            IDictionary<string, object> result = ObjectOf("rateLimits", limits);
            if (includeCredits)
                result["rateLimitResetCredits"] = ObjectOf("availableCount", credits);
            return result;
        }

        private static IDictionary<string, object> ObjectOf(params object[] values)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            for (int index = 0; index < values.Length; index += 2)
                result[(string)values[index]] = values[index + 1];
            return result;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
