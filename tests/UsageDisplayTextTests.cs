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
        Assert(text.Contains("余0%·8/20 11:38"), text);
    }

    public static void WideLayoutKeepsDetailedLabels()
    {
        string text = UsageDisplayText.Build(SampleUsage(), 800);

        Assert(text.Contains("周用量剩余：0%·8月20日11:38重置"), text);
        Assert(text.Contains("状态：额度已用完"), text);
        Assert(text.Contains("累计Token：55.9亿"), text);
    }

    public static void NarrowLayoutPrioritizesToken()
    {
        string text = UsageDisplayText.Build(SampleUsage(), 250);

        Assert(text == "Token：55.9亿", text);
    }

    private static UsageData SampleUsage()
    {
        return new UsageData
        {
            Plan = "plus",
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
