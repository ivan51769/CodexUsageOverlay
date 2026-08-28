using System;
using System.Collections.Generic;
using System.Globalization;

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

    internal static class UsageDisplayText
    {
        internal static string Build(
            UsageData usage,
            int availableTextWidth)
        {
            if (usage == null)
                return "Codex 用量正在载入";

            string planLabel = BuildPlanLabel(usage);
            bool isProPlan = String.Equals(planLabel, "PRO", StringComparison.OrdinalIgnoreCase);
            string shortRemaining = isProPlan
                ? "无限制"
                : FormatRemaining(usage.ShortRemaining, usage.HasShortRemaining || usage.HasShortResetText);
            string shortResetText = isProPlan
                ? String.Empty
                : FormatDisplayResetText(usage.ShortResetText);
            string weeklyRemaining = FormatRemaining(
                usage.WeeklyRemaining, usage.RateLimitStatus != "待刷新");
            string weeklyResetText = FormatDisplayResetText(usage.WeeklyResetText);
            string tokensText = String.IsNullOrWhiteSpace(usage.ProfileTokensText)
                ? "待刷新"
                : usage.ProfileTokensText;
            bool abnormalStatus = IsAbnormalRateLimitStatus(usage.RateLimitStatus);
            string statusText = FormatRateLimitStatus(usage.RateLimitStatus);

            List<string> sections = new List<string>();

            if (availableTextWidth >= 500)
            {
                sections.Add(planLabel);
                sections.Add("5H：" + shortRemaining + FormatResetSuffix(shortResetText));
                sections.Add("周：" + weeklyRemaining + FormatResetSuffix(weeklyResetText));
                if (abnormalStatus)
                    sections.Add("状态：" + statusText);
                if (usage.AvailableResetCredits.HasValue)
                    sections.Add("重置券：" + usage.AvailableResetCredits.Value.ToString(CultureInfo.InvariantCulture));
                sections.Add("累计Token：" + tokensText);
                return String.Join(" | ", sections.ToArray());
            }

            if (availableTextWidth >= 390)
            {
                sections.Add(planLabel);
                sections.Add("5H：" + shortRemaining + FormatResetSuffix(shortResetText));
                sections.Add("周：" + weeklyRemaining + FormatResetSuffix(weeklyResetText));
                if (abnormalStatus)
                    sections.Add(statusText);
                if (usage.AvailableResetCredits.HasValue)
                    sections.Add("券" + usage.AvailableResetCredits.Value.ToString(CultureInfo.InvariantCulture));
                sections.Add("Token：" + tokensText);
                return String.Join(" | ", sections.ToArray());
            }

            if (availableTextWidth < 270)
                return "Token：" + tokensText;

            sections.Add(planLabel);
            sections.Add("5H：" + shortRemaining);
            sections.Add("周：" + weeklyRemaining);
            sections.Add("Token：" + tokensText);
            return String.Join(" | ", sections.ToArray());
        }

        internal static string[] BuildCapsuleSections(UsageData usage)
        {
            if (usage == null)
                return new[] { "CHATGPT", "5H：待刷新", "周：待刷新", "待刷新" };

            string planLabel = BuildPlanLabel(usage);
            bool isProPlan = String.Equals(planLabel, "PRO", StringComparison.OrdinalIgnoreCase);
            string shortRemaining = isProPlan
                ? "无限制"
                : FormatRemaining(usage.ShortRemaining, usage.HasShortRemaining || usage.HasShortResetText);
            string shortResetText = isProPlan
                ? String.Empty
                : FormatDisplayResetText(usage.ShortResetText);
            string weeklyRemaining = FormatRemaining(
                usage.WeeklyRemaining, usage.RateLimitStatus != "待刷新");
            string weeklyResetText = FormatDisplayResetText(usage.WeeklyResetText);
            string tokensText = String.IsNullOrWhiteSpace(usage.ProfileTokensText)
                ? "待刷新"
                : usage.ProfileTokensText;

            List<string> sections = new List<string>();
            sections.Add(planLabel);
            sections.Add("5H：" + shortRemaining + FormatResetSuffix(shortResetText));
            sections.Add("周：" + weeklyRemaining + FormatResetSuffix(weeklyResetText));
            if (usage.AvailableResetCredits.HasValue)
                sections.Add("重置券：" + usage.AvailableResetCredits.Value.ToString(CultureInfo.InvariantCulture));
            sections.Add(tokensText);
            return sections.ToArray();
        }

        internal static string[] BuildComposerInsideCapsuleSections(UsageData usage)
        {
            return BuildCapsuleSections(usage);
        }

        internal static string BuildPlanLabel(UsageData usage)
        {
            return usage == null || String.IsNullOrWhiteSpace(usage.Plan)
                ? "CHATGPT"
                : usage.Plan.ToUpperInvariant();
        }

        private static string FormatRemaining(int? remaining, bool hasQuotaData)
        {
            return remaining.HasValue
                ? remaining.Value.ToString(CultureInfo.InvariantCulture) + "%"
                : (hasQuotaData ? "—" : "待刷新");
        }

        private static bool IsAbnormalRateLimitStatus(string status)
        {
            return !String.IsNullOrWhiteSpace(status) &&
                !String.Equals(status, "正常", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(status, "待刷新", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(status, "normal", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(status, "pending", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatRateLimitStatus(string status)
        {
            if (String.Equals(status, "rate_limit_reached", StringComparison.OrdinalIgnoreCase))
                return "额度已用完";
            if (String.Equals(status, "rate_limit_warning", StringComparison.OrdinalIgnoreCase))
                return "接近额度上限";
            if (String.Equals(status, "normal", StringComparison.OrdinalIgnoreCase))
                return "正常";
            if (String.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
                return "待刷新";
            if (String.IsNullOrWhiteSpace(status))
                return "待刷新";
            return status.Length <= 12 ? status : "额度状态异常";
        }

        private static string FormatDisplayResetText(string resetText)
        {
            if (String.IsNullOrWhiteSpace(resetText) || resetText == "—" || resetText == "待刷新")
                return String.Empty;
            return resetText.Replace(" ", String.Empty).Replace("重置", String.Empty);
        }

        private static string FormatResetSuffix(string resetText)
        {
            return String.IsNullOrWhiteSpace(resetText) ? String.Empty : " " + resetText;
        }
    }
}
