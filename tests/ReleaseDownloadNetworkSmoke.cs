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
            if (args.Length != 5) throw new ArgumentException("Expected URL, filename, size, SHA256, output directory");
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
