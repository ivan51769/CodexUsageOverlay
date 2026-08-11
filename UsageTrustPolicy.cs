using System;

namespace CodexUsageOverlay
{
    internal static class UsageTrustPolicy
    {
        internal static bool HasAuthenticatedAccount(string accountType)
        {
            return String.Equals(accountType, "chatgpt", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasVerifiedSnapshot(bool accountFound, bool quotaWindowFound)
        {
            return accountFound && quotaWindowFound;
        }

        internal static bool IsUsablePlan(string planType)
        {
            return !String.IsNullOrWhiteSpace(planType) &&
                !String.Equals(planType.Trim(), "unknown", StringComparison.OrdinalIgnoreCase);
        }

        internal static string SelectTrustedPlan(string quotaPlan, string accountPlan)
        {
            if (IsUsablePlan(quotaPlan))
                return quotaPlan;
            return IsUsablePlan(accountPlan) ? accountPlan : null;
        }

        internal static bool IsRealQuotaWindow(long durationMinutes, bool hasUsedPercent,
            double usedPercent, bool hasResetAt, long resetAt)
        {
            if (durationMinutes <= 0)
                return false;

            bool usablePercent = hasUsedPercent && !Double.IsNaN(usedPercent) &&
                !Double.IsInfinity(usedPercent) && usedPercent >= 0d;
            bool usableReset = hasResetAt && resetAt > 0;
            return usablePercent || usableReset;
        }
    }
}
