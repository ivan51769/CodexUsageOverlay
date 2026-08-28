using System;
using CodexUsageOverlay;

internal static class UsageDisplayTextTests
{
    public static void LongRateLimitStatusIsLocalizedAndTokenIsKept()
    {
        UsageData usage = SampleUsage();
        string text = UsageDisplayText.Build(usage, 480);

        Assert(!text.Contains("rate_limit_reached"), text);
        Assert(text.Contains("额度已用完"), text);
        Assert(text.Contains("Token：55.9亿"), text);
        Assert(text.Contains("周：0% 8月20日11:38"), text);
        Assert(text.Contains("5H：88% 14:05"), text);
    }

    public static void WideLayoutKeepsDetailedLabels()
    {
        string text = UsageDisplayText.Build(SampleUsage(), 800);

        Assert(text.Contains("周：0% 8月20日11:38"), text);
        Assert(text.Contains("5H：88% 14:05"), text);
        Assert(text.Contains("状态：额度已用完"), text);
        Assert(text.Contains("重置券：0"), text);
        Assert(text.Contains("累计Token：55.9亿"), text);
    }

    public static void NarrowLayoutPrioritizesToken()
    {
        string text = UsageDisplayText.Build(SampleUsage(), 250);

        Assert(text == "Token：55.9亿", text);
    }

    public static void PlusLayoutIncludesFiveHourQuota()
    {
        UsageData usage = new UsageData
        {
            Plan = "plus",
            ShortRemaining = 62,
            HasShortRemaining = true,
            ShortResetText = "15:01",
            HasShortResetText = true,
            WeeklyRemaining = 94,
            WeeklyResetText = "9月4日 10:01",
            AvailableResetCredits = 1,
            ProfileTokensText = "19.5亿"
        };

        Assert(UsageDisplayText.Build(usage, 800) ==
            "PLUS | 5H：62% 15:01 | 周：94% 9月4日10:01 | 重置券：1 | 累计Token：19.5亿",
            UsageDisplayText.Build(usage, 800));
    }

    public static void MissingQuotaWindowsDegradeGracefully()
    {
        UsageData weeklyOnly = new UsageData
        {
            Plan = "pro",
            WeeklyRemaining = 94,
            WeeklyResetText = "9月4日 10:01",
            ProfileTokensText = "19.5亿"
        };
        string weeklyOnlyText = UsageDisplayText.Build(weeklyOnly, 800);
        Assert(weeklyOnlyText.Contains("5H：无限制"), weeklyOnlyText);
        Assert(weeklyOnlyText.Contains("周：94% 9月4日10:01"), weeklyOnlyText);

        UsageData shortOnly = new UsageData
        {
            Plan = "plus",
            ShortRemaining = 62,
            HasShortRemaining = true,
            ShortResetText = "15:01",
            HasShortResetText = true,
            ProfileTokensText = "19.5亿"
        };
        string shortOnlyText = UsageDisplayText.Build(shortOnly, 800);
        Assert(shortOnlyText.Contains("5H：62% 15:01"), shortOnlyText);
        Assert(shortOnlyText.Contains("周：待刷新"), shortOnlyText);
    }

    public static void ZeroShortQuotaIsDisplayedBeforeWeeklyQuota()
    {
        UsageData usage = new UsageData
        {
            Plan = "plus",
            ShortRemaining = 0,
            HasShortRemaining = true,
            ShortResetText = "15:01",
            HasShortResetText = true,
            WeeklyRemaining = 94,
            WeeklyResetText = "9月4日 10:01",
            ProfileTokensText = "19.5亿"
        };

        string text = UsageDisplayText.Build(usage, 800);
        Assert(text.Contains("5H：0% 15:01"), text);
        Assert(text.IndexOf("5H：", StringComparison.Ordinal) <
            text.IndexOf("周：", StringComparison.Ordinal), text);
    }

    public static void ProPlanShowsUnlimitedFiveHourQuota()
    {
        UsageData usage = new UsageData
        {
            Plan = "pro",
            ShortRemaining = 62,
            HasShortRemaining = true,
            ShortResetText = "15:01",
            HasShortResetText = true,
            WeeklyRemaining = 94,
            WeeklyResetText = "9月4日 10:01",
            ProfileTokensText = "19.5亿"
        };

        string text = UsageDisplayText.Build(usage, 800);
        Assert(text.Contains("PRO | 5H：无限制 | 周：94% 9月4日10:01"), text);
        Assert(!text.Contains("15:01"), text);
    }

    public static void CapsuleSectionsKeepFieldOrderAndUseTokenValueOnly()
    {
        UsageData usage = new UsageData
        {
            Plan = "plus",
            ShortRemaining = 62,
            HasShortRemaining = true,
            ShortResetText = "15:01",
            HasShortResetText = true,
            WeeklyRemaining = 94,
            WeeklyResetText = "9月4日 10:01",
            AvailableResetCredits = 1,
            ProfileTokensText = "19.5亿"
        };

        string[] sections = UsageDisplayText.BuildCapsuleSections(usage);
        Assert(String.Join(" | ", sections) ==
            "PLUS | 5H：62% 15:01 | 周：94% 9月4日10:01 | 重置券：1 | 19.5亿",
            String.Join(" | ", sections));
    }

    public static void ComposerInsideCapsulesIncludePlan()
    {
        UsageData usage = new UsageData
        {
            Plan = "pro",
            WeeklyRemaining = 94,
            WeeklyResetText = "9月4日 10:01",
            AvailableResetCredits = 1,
            ProfileTokensText = "19.5亿"
        };

        string[] sections = UsageDisplayText.BuildComposerInsideCapsuleSections(usage);
        Assert(String.Join(" | ", sections) ==
            "PRO | 5H：无限制 | 周：94% 9月4日10:01 | 重置券：1 | 19.5亿",
            String.Join(" | ", sections));
        Assert(String.Join(" | ", sections).Contains("PRO"),
            "composer inside did not display the plan label");
    }

    private static UsageData SampleUsage()
    {
        return new UsageData
        {
            Plan = "plus",
            ShortRemaining = 88,
            HasShortRemaining = true,
            ShortResetText = "14:05",
            HasShortResetText = true,
            WeeklyRemaining = 0,
            WeeklyResetText = "8月20日 11:38",
            RateLimitStatus = "rate_limit_reached",
            AvailableResetCredits = 0,
            ProfileTokensText = "55.9亿"
        };
    }

    private static void Assert(bool condition, string value)
    {
        if (!condition)
            throw new InvalidOperationException(value);
    }
}
