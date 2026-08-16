using System;

namespace CodexUsageOverlay
{
    internal static class GitHubReleaseUpdateTests
    {
        public static void NewerStableReleaseIsDetected()
        {
            GitHubReleaseUpdateSnapshot result = GitHubReleaseUpdateService.EvaluateReleaseUrl(
                "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/v1.4.0");
            Assert(result != null && result.UpdateAvailable, "new release was not detected");
            Assert(result.LatestVersion == "1.4.0", result == null ? "missing result" : result.LatestVersion);
        }

        public static void PrereleaseIsIgnored()
        {
            GitHubReleaseUpdateSnapshot result = GitHubReleaseUpdateService.EvaluateReleaseUrl(
                "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/v1.4.0-beta.1");
            Assert(result == null, "prerelease was accepted");
        }

        public static void ForeignReleaseUrlIsRejected()
        {
            GitHubReleaseUpdateSnapshot result = GitHubReleaseUpdateService.EvaluateReleaseUrl(
                "https://example.com/ivan51769/CodexUsageOverlay/releases/tag/v1.4.0");
            Assert(result == null, "foreign release URL was accepted");
        }

        public static void CurrentReleaseDoesNotPrompt()
        {
            GitHubReleaseUpdateSnapshot result = GitHubReleaseUpdateService.EvaluateReleaseUrl(
                "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/v" +
                    GitHubReleaseUpdateService.CurrentVersion);
            Assert(result != null && !result.UpdateAvailable, "current release prompted an update");
        }

        public static void ReleaseUrlAllowlistIsStrict()
        {
            Assert(GitHubReleaseUpdateService.IsAllowedReleaseUrl(
                "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/v1.4.0"),
                "valid release URL was rejected");
            Assert(!GitHubReleaseUpdateService.IsAllowedReleaseUrl(
                "https://github.com/ivan51769/CodexUsageOverlay/releases/latest"),
                "unversioned release URL was accepted");
            Assert(!GitHubReleaseUpdateService.IsAllowedReleaseUrl(
                "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/v1.4.0?download=1"),
                "release URL with query was accepted");
        }

        public static void ManualCheckBypassesOnlyTimeThrottle()
        {
            DateTime now = new DateTime(2026, 8, 12, 8, 0, 0, DateTimeKind.Utc);
            DateTime recent = now.AddMinutes(-5);
            Assert(!GitHubReleaseUpdateService.CanStartCheck(
                false, false, now, recent, false),
                "automatic check bypassed the 24-hour throttle");
            Assert(GitHubReleaseUpdateService.CanStartCheck(
                false, false, now, recent, true),
                "manual check did not bypass the time throttle");
            Assert(!GitHubReleaseUpdateService.CanStartCheck(
                false, true, now, recent, true),
                "manual check bypassed an active request");
            Assert(!GitHubReleaseUpdateService.CanStartCheck(
                true, false, now, recent, true),
                "manual check bypassed disposal");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
