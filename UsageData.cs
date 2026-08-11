using System;

namespace CodexUsageOverlay
{
    internal sealed class UsageData
    {
        public string Plan = "ChatGPT";
        public bool HasPlan;
        public int? ShortRemaining;
        public bool HasShortRemaining;
        public string ShortResetText = "待刷新";
        public bool HasShortResetText;
        public int? WeeklyRemaining;
        public bool HasWeeklyRemaining;
        public string WeeklyResetText = "待刷新";
        public bool HasWeeklyResetText;
        public string RateLimitStatus = "待刷新";
        public bool HasRateLimitStatus;
        public int? AvailableResetCredits;
        public bool HasAvailableResetCredits;
        public string ProfileTokensText = String.Empty;
        public long? LifetimeTokens;
        public string Source = "缓存";
        public string LastError = String.Empty;
        public DateTime UpdatedUtc = DateTime.MinValue;

        public UsageData Clone()
        {
            return (UsageData)MemberwiseClone();
        }
    }

    internal static class UsageDataMerger
    {
        internal static bool MergeInto(UsageData target, UsageData incoming)
        {
            if (target == null)
                throw new ArgumentNullException("target");
            if (incoming == null)
                throw new ArgumentNullException("incoming");

            bool changed = false;
            if (incoming.HasPlan && !String.IsNullOrWhiteSpace(incoming.Plan) && target.Plan != incoming.Plan)
            {
                target.Plan = incoming.Plan;
                changed = true;
            }
            if (incoming.HasShortRemaining && target.ShortRemaining != incoming.ShortRemaining)
            {
                target.ShortRemaining = incoming.ShortRemaining;
                changed = true;
            }
            if (incoming.HasShortResetText && target.ShortResetText != incoming.ShortResetText)
            {
                target.ShortResetText = incoming.ShortResetText;
                changed = true;
            }
            if (incoming.HasWeeklyRemaining && target.WeeklyRemaining != incoming.WeeklyRemaining)
            {
                target.WeeklyRemaining = incoming.WeeklyRemaining;
                changed = true;
            }
            if (incoming.HasWeeklyResetText && target.WeeklyResetText != incoming.WeeklyResetText)
            {
                target.WeeklyResetText = incoming.WeeklyResetText;
                changed = true;
            }
            if (incoming.HasRateLimitStatus && target.RateLimitStatus != incoming.RateLimitStatus)
            {
                target.RateLimitStatus = incoming.RateLimitStatus;
                changed = true;
            }
            if (incoming.HasAvailableResetCredits && target.AvailableResetCredits != incoming.AvailableResetCredits)
            {
                target.AvailableResetCredits = incoming.AvailableResetCredits;
                changed = true;
            }
            if (!String.IsNullOrWhiteSpace(incoming.ProfileTokensText) &&
                incoming.ProfileTokensText != "待刷新" && target.ProfileTokensText != incoming.ProfileTokensText)
            {
                target.ProfileTokensText = incoming.ProfileTokensText;
                changed = true;
            }
            if (incoming.LifetimeTokens.HasValue && target.LifetimeTokens != incoming.LifetimeTokens)
            {
                target.LifetimeTokens = incoming.LifetimeTokens;
                changed = true;
            }
            if (!String.IsNullOrWhiteSpace(incoming.Source))
                target.Source = incoming.Source;
            target.LastError = incoming.LastError ?? String.Empty;
            return changed;
        }
    }
}
