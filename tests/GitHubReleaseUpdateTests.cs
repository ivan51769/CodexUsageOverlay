using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace CodexUsageOverlay
{
    internal static class GitHubReleaseUpdateTests
    {
        public static void InstallerDownloadIsVerified()
        {
            Version current = new Version(GitHubReleaseUpdateService.CurrentVersion);
            string version = new Version(current.Major, current.Minor, current.Build + 1).ToString();
            string releaseUrl = "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/v" + version;
            byte[] bytes = new byte[] { 77, 90, 3, 8, 4, 9 };
            string digest;
            using (SHA256 sha = SHA256.Create())
                digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            string name = "blues19-CodexUsageUpdateAssistant-Setup-" + version + ".exe";
            var asset = new Dictionary<string, object> {
                { "name", name }, { "size", bytes.Length }, { "digest", "sha256:" + digest },
                { "browser_download_url", "https://github.com/ivan51769/CodexUsageOverlay/releases/download/v" + version + "/" + name }
            };
            var metadata = new Dictionary<string, object> {
                { "tag_name", "v" + version }, { "draft", false }, { "prerelease", false },
                { "assets", new object[] { asset } }
            };
            var serializer = new JavaScriptSerializer();
            var parsed = ReleaseInstallerDownload.ParseAsset(releaseUrl, serializer.Serialize(metadata));
            int percent = -1;
            using (var output = new MemoryStream())
            {
                ReleaseInstallerDownload.CopyVerified(new MemoryStream(bytes), output, parsed, delegate(int p) { percent = p; });
                Assert(output.Length == bytes.Length && percent == 100, "verified download did not complete");
            }
            Reject(delegate { ReleaseInstallerDownload.CopyVerified(new MemoryStream(new byte[] { 77, 90 }), new MemoryStream(), parsed, null); });
            Reject(delegate { ReleaseInstallerDownload.CopyVerified(new MemoryStream(new byte[] { 77, 90, 0, 0, 0, 0 }), new MemoryStream(), parsed, null); });
            Reject(delegate { ReleaseInstallerDownload.CopyVerified(new MemoryStream(new byte[20]), new MemoryStream(), parsed, null); });
            asset["digest"] = null;
            Reject(delegate { ReleaseInstallerDownload.ParseAsset(releaseUrl, serializer.Serialize(metadata)); });
            asset["digest"] = "sha256:" + digest;
            asset["browser_download_url"] = "https://example.com/" + name;
            Reject(delegate { ReleaseInstallerDownload.ParseAsset(releaseUrl, serializer.Serialize(metadata)); });
            asset["browser_download_url"] = parsed.Url;
            metadata["prerelease"] = true;
            Reject(delegate { ReleaseInstallerDownload.ParseAsset(releaseUrl, serializer.Serialize(metadata)); });
            metadata["prerelease"] = false;
            metadata["assets"] = new object[] { asset, asset };
            Reject(delegate { ReleaseInstallerDownload.ParseAsset(releaseUrl, serializer.Serialize(metadata)); });
            metadata["assets"] = new object[] { asset };
            metadata["tag_name"] = "v0.0.1";
            Reject(delegate { ReleaseInstallerDownload.ParseAsset(releaseUrl, serializer.Serialize(metadata)); });
            Reject(delegate { ReleaseInstallerDownload.ParseAsset(
                "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/v" + current, serializer.Serialize(metadata)); });
            Assert(ReleaseInstallerDownload.IsTrustedRedirect(new Uri("https://release-assets.githubusercontent.com/file?signature=example")), "asset host rejected");
            foreach (string bad in new[] { "http://release-assets.githubusercontent.com/file", "https://release-assets.githubusercontent.com.evil.test/file",
                "https://user@release-assets.githubusercontent.com/file", "https://release-assets.githubusercontent.com:444/file", "https://example.com/file" })
                Assert(!ReleaseInstallerDownload.IsTrustedRedirect(new Uri(bad)), "unsafe redirect accepted");
            string args = ReleaseInstallerDownload.InstallArguments(@"C:\Program Files\Codex Usage Overlay\");
            Assert(args.Contains("/RESTARTOVERLAY=1") && args.Contains("/DIR=\"C:\\Program Files\\Codex Usage Overlay\""), "install/restart path not quoted correctly");
        }

        private static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("unsafe installer accepted");
        }

        public static void NewerStableReleaseIsDetected()
        {
            Version current = new Version(GitHubReleaseUpdateService.CurrentVersion);
            string nextVersion = new Version(current.Major, current.Minor, current.Build + 1).ToString();
            GitHubReleaseUpdateSnapshot result = GitHubReleaseUpdateService.EvaluateReleaseUrl(
                "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/v" + nextVersion);
            Assert(result != null && result.UpdateAvailable, "new release was not detected");
            Assert(result.LatestVersion == nextVersion, result == null ? "missing result" : result.LatestVersion);
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
            DateTime recent = now.AddSeconds(-30);
            Assert(!GitHubReleaseUpdateService.CanStartCheck(
                false, false, now, recent, false),
                "automatic check bypassed the one-minute throttle");
            Assert(!GitHubReleaseUpdateService.CanStartCheck(false, false, now, now.AddSeconds(-59), false),
                "automatic check started before sixty seconds");
            Assert(GitHubReleaseUpdateService.CanStartCheck(false, false, now, now.AddMinutes(-1), false),
                "automatic check did not start at sixty seconds");
            Assert(GitHubReleaseUpdateService.CanStartCheck(false, false, now, DateTime.MinValue, false),
                "initial update check was delayed");
            Assert(!GitHubReleaseUpdateService.CanStartCheck(false, false, now, now.AddSeconds(1), false),
                "clock rollback caused repeated automatic checks");
            Assert(!GitHubReleaseUpdateService.CanStartCheck(false, true, now, now.AddMinutes(-2), false),
                "automatic check overlapped an active request");
            Assert(!GitHubReleaseUpdateService.CanStartCheck(true, false, now, now.AddMinutes(-2), false),
                "automatic check bypassed disposal");
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
