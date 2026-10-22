// Opt-in network validation only. Downloads and verifies an explicitly supplied
// published asset; never launches an installer or changes the running program.
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using CodexUsageOverlay;

internal static class ReleaseDownloadNetworkSmoke
{
    private static int active, peak, ranges;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 5 && !(args.Length == 6 && args[5] == "--official-fallback"))
                throw new ArgumentException("Expected URL, filename, size, SHA256, output directory, optional --official-fallback");
            Uri uri = new Uri(args[0]);
            if (uri.Scheme != "https" || uri.Host != "github.com" || uri.Port != 443 ||
                !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment) ||
                !uri.AbsolutePath.StartsWith("/ivan51769/CodexUsageOverlay/releases/download/", StringComparison.Ordinal))
                throw new ArgumentException("Only the project's published GitHub assets may be smoke-tested");
            var asset = new ReleaseInstallerDownload.Asset { Url = args[0], Name = args[1],
                Size = Int64.Parse(args[2], CultureInfo.InvariantCulture), Sha256 = args[3] };
            var method = typeof(ReleaseInstallerDownload).GetMethod("GetResponse", BindingFlags.NonPublic | BindingFlags.Static);
            var transport = (Func<string, bool, long?, long?, ReleaseInstallerDownload.Response>)Delegate.CreateDelegate(
                typeof(Func<string, bool, long?, long?, ReleaseInstallerDownload.Response>), method);
            if (args.Length == 6)
            {
                string tag = Uri.UnescapeDataString(uri.Segments[5].TrimEnd('/'));
                string releaseUrl = "https://github.com/ivan51769/CodexUsageOverlay/releases/tag/" + Uri.EscapeDataString(tag);
                var release = GitHubReleaseUpdateService.EvaluateReleaseUrl(releaseUrl);
                if (release == null) throw new ArgumentException("Invalid stable release tag");
                try
                {
                    using (var response = transport("https://api.github.com/repos/ivan51769/CodexUsageOverlay/releases/tags/" +
                        Uri.EscapeDataString(tag), false, null, null))
                        Console.WriteLine("API available; explicitly exercising the official fallback route");
                }
                catch (ReleaseInstallerDownload.HttpFailureException error)
                {
                    if (!error.IsRateLimited) throw;
                    Console.WriteLine("API rate limited; HTTP={0}; remaining={1}", error.StatusCode, error.Remaining);
                }
                // The explicit smoke asset may be older than this test build. Invoke only the
                // metadata routine: the application's newer-version guard remains unchanged,
                // and this executable never starts an installer.
                MethodInfo fallback = typeof(ReleaseInstallerDownload).GetMethod("ResolveOfficialAsset", BindingFlags.NonPublic | BindingFlags.Static);
                using (var service = new GitHubReleaseUpdateService())
                {
                    var resolved = (ReleaseInstallerDownload.Asset)fallback.Invoke(null, new object[] {
                        releaseUrl, release, transport, (Func<string>)service.ResolveLatestReleaseUrl });
                    if (resolved.Name != asset.Name || resolved.Url != asset.Url || resolved.Size != asset.Size ||
                        !resolved.Sha256.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Official checksum and probed size differ from the independently supplied release digest");
                    asset = resolved;
                }
                Console.WriteLine("PASS official latest-release check, checksum file and installer size probe");
            }
            int lastPercent = -10;
            string lastPhase = null;
            string path = ReleaseInstallerDownload.DownloadAsset(asset, args[4], delegate(ReleaseInstallerDownload.Progress p)
            {
                if (p.Phase != lastPhase || p.Percent >= lastPercent + 10)
                {
                    Console.WriteLine("phase={0}; percent={1}; connections={2}; bytes={3}/{4}",
                        p.Phase, p.Percent, p.Connections, p.DownloadedBytes, p.TotalBytes);
                    lastPhase = p.Phase; lastPercent = p.Percent;
                }
            }, delegate(ReleaseInstallerDownload.Asset value, long? start, long? end)
            {
                int now = Interlocked.Increment(ref active), previous;
                do { previous = peak; if (previous >= now) break; }
                while (Interlocked.CompareExchange(ref peak, now, previous) != previous);
                try
                {
                    var response = transport(value.Url, true, start, end);
                    if (response.StatusCode == 206) Interlocked.Increment(ref ranges);
                    return response;
                }
                finally { Interlocked.Decrement(ref active); }
            });
            Console.WriteLine("PASS verified asset; partialResponses={0}; overlappingRequests={1}; bytes={2}; installerNotExecuted=True",
                ranges, peak, new FileInfo(path).Length);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    }
}
