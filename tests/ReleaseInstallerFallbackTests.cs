using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUsageOverlay
{
    internal static class ReleaseInstallerFallbackTests
    {
        public static void RateLimitUsesVerifiedOfficialAssets()
        {
            foreach (int code in new[] { 403, 429 })
            foreach (bool rangeIgnored in new[] { false, true })
            {
                var source = new Source { Code = code, RangeIgnored = rangeIgnored };
                bool fallbackShown = false;
                var asset = ReleaseInstallerDownload.ResolveAsset(source.ReleaseUrl, source.Open, source.Latest,
                    delegate(ReleaseInstallerDownload.Progress p) { fallbackShown |= p.Phase == "fallback"; });
                Assert(source.ApiCalls == 1 && source.LatestCalls == 1 && source.ChecksumCalls == 1 &&
                    source.Probes == 1 && fallbackShown, "rate-limit fallback must not retry the API or skip stable-release verification");
                Assert(asset.Url == source.AssetUrl && asset.Name == source.Name && asset.Size == source.Bytes.Length &&
                    asset.Sha256.Equals(source.Digest, StringComparison.OrdinalIgnoreCase), "fallback asset lost its trusted identity");
                using (var output = new MemoryStream())
                    ReleaseInstallerDownload.CopyVerified(new MemoryStream(source.Bytes), output, asset, null);
                byte[] corrupt = (byte[])source.Bytes.Clone();
                corrupt[2] ^= 1;
                Reject(delegate { ReleaseInstallerDownload.CopyVerified(new MemoryStream(corrupt), new MemoryStream(), asset, null); });
            }
            var secondary = new Source { Remaining = "12", RetryAfter = "60" };
            ReleaseInstallerDownload.ResolveAsset(secondary.ReleaseUrl, secondary.Open, secondary.Latest, null);
            Assert(secondary.ChecksumCalls == 1, "explicit secondary rate limit did not use the fallback");
        }

        public static void UnsafeFallbackMetadataStopsInstallation()
        {
            foreach (string mode in new[] { "wrong-latest", "foreign-latest", "wrong-name", "traversal-name", "duplicate",
                "bad-digest", "oversized-checksum", "missing-checksum", "unknown-size", "oversized-asset", "wrong-range" })
            {
                var source = new Source { Mode = mode };
                Reject(delegate { ReleaseInstallerDownload.ResolveAsset(source.ReleaseUrl, source.Open, source.Latest, null); });
                Assert(source.ApiCalls == 1 && source.LatestCalls == 1, mode + " retried a rate-limited API or skipped latest verification");
                if (mode.EndsWith("latest", StringComparison.Ordinal))
                    Assert(source.ChecksumCalls == 0 && source.Probes == 0, mode + " accessed assets before stable-release verification");
                else Assert(source.ChecksumCalls == 1, mode + " did not check the official checksum file");
            }
            var foreign = new Source();
            Reject(delegate { ReleaseInstallerDownload.ResolveAsset("https://example.com/releases/tag/v99.0.0",
                foreign.Open, foreign.Latest, null); });
            Assert(foreign.ApiCalls == 0 && foreign.LatestCalls == 0, "foreign release initiated network access");
            foreach (string tag in new[] { "v" + GitHubReleaseUpdateService.CurrentVersion, "v0.0.1", "v99.0.0-rc1" })
            {
                var blocked = new Source();
                Reject(delegate { ReleaseInstallerDownload.ResolveAsset("https://github.com/ivan51769/CodexUsageOverlay/releases/tag/" + tag,
                    blocked.Open, blocked.Latest, null); });
                Assert(blocked.ApiCalls == 0 && blocked.LatestCalls == 0, "non-update tag initiated network access");
            }
        }

        public static void OtherErrorsDoNotUseFallback()
        {
            foreach (string mode in new[] { "denied", "not-found", "timeout", "invalid-metadata", "prerelease", "missing-api-digest" })
            {
                var source = new Source { Mode = mode };
                bool rejected = false;
                try { ReleaseInstallerDownload.ResolveAsset(source.ReleaseUrl, source.Open, source.Latest, null); }
                catch (Exception) { rejected = true; }
                Assert(rejected && source.LatestCalls == 0 && source.ChecksumCalls == 0,
                    mode + " weakened API metadata validation or retried a non-rate-limit error");
            }
            var valid = new Source { Mode = "api-success" };
            var asset = ReleaseInstallerDownload.ResolveAsset(valid.ReleaseUrl, valid.Open, valid.Latest, null);
            Assert(asset.Sha256 == valid.Digest && valid.LatestCalls == 0 && valid.ChecksumCalls == 0,
                "successful API metadata unnecessarily used the fallback");
        }

        public static void FailureReasonsAreSpecificAndRedacted()
        {
            Exception[] errors = {
                new ReleaseInstallerDownload.HttpFailureException(429, "api.github.com", null, "60"),
                new ReleaseInstallerDownload.HttpFailureException(403, "api.github.com", "0", null),
                new ReleaseInstallerDownload.HttpFailureException(403, "api.github.com", "12", null),
                new ReleaseInstallerDownload.HttpFailureException(404, "github.com", null, null),
                new ReleaseInstallerDownload.HttpFailureException(502, "github.com", null, null),
                new WebException("https://secret:password@proxy.test/?signature=private", WebExceptionStatus.Timeout),
                new WebException("private proxy", WebExceptionStatus.NameResolutionFailure),
                new WebException("private proxy", WebExceptionStatus.TrustFailure),
                new TimeoutException("private transport"), new UnauthorizedAccessException("private directory"),
                new InvalidDataException("安装包校验失败，未运行安装程序；请重试。") };
            string[] reasons = { "已限流", "已限流", "拒绝访问", "404", "502", "超时", "解析", "安全连接", "超时", "权限", "校验失败" };
            for (int index = 0; index < errors.Length; index++)
            {
                string message = ReleaseInstallerDownload.FailureMessage(errors[index]);
                Assert(message.Contains(reasons[index]) && message.Contains("当前版本没有改动"), "failure hides its actual category");
                Assert(!message.Contains("private") && !message.Contains("password") && !message.Contains("signature"), "failure exposes private transport details");
            }
            var source = new Source { Mode = "missing-checksum" };
            try { ReleaseInstallerDownload.ResolveAsset(source.ReleaseUrl, source.Open, source.Latest, null); }
            catch (IOException error)
            {
                string message = ReleaseInstallerDownload.FailureMessage(error);
                Assert(message.Contains("接口已限流") && message.Contains("404"), "fallback failure lost the original rate-limit context");
                return;
            }
            throw new Exception("missing official checksum was accepted");
        }

        private sealed class Source
        {
            internal readonly byte[] Bytes = { 77, 90, 3, 8, 4, 9 };
            internal readonly string Version, ReleaseUrl, AssetUrl, Name, Digest, ChecksumUrl;
            internal string Mode = "valid", Remaining = "0", RetryAfter;
            internal int Code = 403, ApiCalls, LatestCalls, ChecksumCalls, Probes;
            internal bool RangeIgnored;
            internal Source()
            {
                Version current = new Version(GitHubReleaseUpdateService.CurrentVersion);
                Version = new Version(current.Major, current.Minor, current.Build + 1).ToString();
                ReleaseUrl = "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/v" + Version;
                Name = "blues19-CodexUsageUpdateAssistant-Setup-" + Version + ".exe";
                string root = "https://github.com/ivan51769/CodexUsageOverlay/releases/download/v" + Version + "/";
                AssetUrl = root + Name;
                ChecksumUrl = root + "SHA256-v" + Version + ".txt";
                using (SHA256 sha = SHA256.Create()) Digest = BitConverter.ToString(sha.ComputeHash(Bytes)).Replace("-", "");
            }
            internal string Latest()
            {
                LatestCalls++;
                if (Mode == "wrong-latest") return ReleaseUrl + "0";
                if (Mode == "foreign-latest") return "https://example.com/releases/tag/v" + Version;
                return ReleaseUrl;
            }
            internal ReleaseInstallerDownload.Response Open(string url, bool assetDownload, long? start, long? end)
            {
                if (url == "https://api.github.com/repos/ivan51769/CodexUsageOverlay/releases/tags/v" + Version)
                {
                    ApiCalls++;
                    Assert(!assetDownload && !start.HasValue && !end.HasValue, "API was treated as an asset request");
                    if (Mode == "timeout") throw new WebException("private proxy detail", WebExceptionStatus.Timeout);
                    if (Mode == "denied" || Mode == "not-found")
                        throw new ReleaseInstallerDownload.HttpFailureException(Mode == "denied" ? 403 : 404, "api.github.com", "12", null);
                    if (Mode == "invalid-metadata") return Text("{}");
                    if (Mode == "api-success" || Mode == "prerelease" || Mode == "missing-api-digest")
                        return Text(new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                            { "tag_name", "v" + Version }, { "draft", false }, { "prerelease", Mode == "prerelease" },
                            { "assets", new object[] { new Dictionary<string, object> {
                                { "name", Name }, { "browser_download_url", AssetUrl }, { "size", Bytes.Length },
                                { "digest", Mode == "missing-api-digest" ? null : "sha256:" + Digest } } } }
                        }));
                    throw new ReleaseInstallerDownload.HttpFailureException(Code, "api.github.com", Remaining, RetryAfter);
                }
                Assert(assetDownload, "fallback asset did not use trusted release redirects");
                if (url == ChecksumUrl)
                {
                    ChecksumCalls++;
                    Assert(!start.HasValue && !end.HasValue, "checksum request uses a partial range");
                    if (Mode == "missing-checksum") throw new ReleaseInstallerDownload.HttpFailureException(404, "github.com", null, null);
                    string line = Digest + "  " + Name;
                    if (Mode == "wrong-name") line = Digest + "  another.exe";
                    if (Mode == "traversal-name") line = Digest + "  ../" + Name;
                    if (Mode == "duplicate") line += "\n" + line;
                    if (Mode == "bad-digest") line = "not-a-hash  " + Name;
                    if (Mode == "oversized-checksum") line += new string('x', 65536);
                    return Text("\uFEFF" + line + "\r\n");
                }
                Assert(url == AssetUrl && start == 0 && end == 0, "fallback contacted an unexpected asset/range");
                Probes++;
                bool full = RangeIgnored || Mode == "unknown-size";
                return new ReleaseInstallerDownload.Response {
                    StatusCode = full ? 200 : 206,
                    ContentLength = Mode == "unknown-size" ? -1 : full ? Bytes.Length : 1,
                    ContentRange = "bytes " + (Mode == "wrong-range" ? "1-1/" : "0-0/") +
                        (Mode == "oversized-asset" ? 134217729 : Bytes.Length),
                    Body = new MemoryStream(full ? Bytes : new[] { Bytes[0] }) };
            }
            private static ReleaseInstallerDownload.Response Text(string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                return new ReleaseInstallerDownload.Response { StatusCode = 200, ContentLength = bytes.Length,
                    Body = new MemoryStream(bytes) };
            }
        }
        private static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            catch (IOException) { return; }
            throw new InvalidOperationException("unverified fallback was accepted");
        }
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
