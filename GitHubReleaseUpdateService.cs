using System;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodexUsageOverlay
{
    internal sealed class GitHubReleaseUpdateSnapshot
    {
        public string CurrentVersion = GitHubReleaseUpdateService.CurrentVersion;
        public string LatestVersion = String.Empty;
        public string ReleaseUrl = String.Empty;
        public bool UpdateAvailable;
        public bool IsChecking;
        public DateTime? LastCheckedUtc;

        public GitHubReleaseUpdateSnapshot Clone()
        {
            return (GitHubReleaseUpdateSnapshot)MemberwiseClone();
        }
    }

    internal sealed class GitHubReleaseUpdateService : IDisposable
    {
        public const string CurrentVersion = "1.3.3";
        public const string LatestReleaseUrl =
            "https://github.com/ivan51769/CodexUsageOverlay/releases/latest";

        private const string AllowedReleasePrefix =
            "/ivan51769/CodexUsageOverlay/releases/tag/";
        private const int RequestTimeoutMilliseconds = 10000;
        private static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromHours(24d);
        private static readonly Regex StableVersionPattern = new Regex(
            @"^(?:v)?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly object sync = new object();
        private GitHubReleaseUpdateSnapshot state = new GitHubReleaseUpdateSnapshot();
        private DateTime lastCheckAttemptUtc = DateTime.MinValue;
        private HttpWebRequest activeRequest;
        private bool checkRunning;
        private bool disposed;

        public GitHubReleaseUpdateSnapshot Snapshot()
        {
            lock (sync)
                return state.Clone();
        }

        public void RequestCheck()
        {
            RequestCheck(false);
        }

        public bool RequestCheck(bool force)
        {
            bool shouldStart = false;
            lock (sync)
            {
                DateTime nowUtc = DateTime.UtcNow;
                if (CanStartCheck(disposed, checkRunning, nowUtc,
                    lastCheckAttemptUtc, force))
                {
                    checkRunning = true;
                    lastCheckAttemptUtc = nowUtc;
                    state.IsChecking = true;
                    shouldStart = true;
                }
            }

            if (!shouldStart)
                return false;

            try
            {
                if (ThreadPool.QueueUserWorkItem(delegate { CheckLatestRelease(); }))
                    return true;
            }
            catch
            {
            }

            lock (sync)
            {
                checkRunning = false;
                state.IsChecking = false;
            }
            return false;
        }

        internal static bool CanStartCheck(
            bool isDisposed,
            bool isRunning,
            DateTime nowUtc,
            DateTime lastAttemptUtc,
            bool force)
        {
            if (isDisposed || isRunning)
                return false;
            if (force)
                return true;
            return nowUtc >= lastAttemptUtc &&
                nowUtc - lastAttemptUtc >= MinimumCheckInterval;
        }

        private void CheckLatestRelease()
        {
            try
            {
                GitHubReleaseUpdateSnapshot updated = EvaluateReleaseUrl(ResolveLatestReleaseUrl());
                if (updated == null)
                    return;
                updated.LastCheckedUtc = DateTime.UtcNow;
                lock (sync)
                {
                    if (!disposed)
                        state = updated;
                }
            }
            catch
            {
                // Update checks must never interrupt the overlay when GitHub is unavailable.
            }
            finally
            {
                lock (sync)
                {
                    activeRequest = null;
                    checkRunning = false;
                    state.IsChecking = false;
                }
            }
        }

        private string ResolveLatestReleaseUrl()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(LatestReleaseUrl);
            request.Method = "GET";
            request.Accept = "text/html";
            request.UserAgent = "CodexUsageOverlay/" + CurrentVersion;
            request.Timeout = RequestTimeoutMilliseconds;
            request.ReadWriteTimeout = RequestTimeoutMilliseconds;
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.UseDefaultCredentials = false;
            request.Credentials = null;
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache";

            lock (sync)
            {
                if (disposed)
                {
                    request.Abort();
                    throw new ObjectDisposedException("GitHubReleaseUpdateService");
                }
                activeRequest = request;
            }

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                int statusCode = (int)response.StatusCode;
                if (statusCode != 301 && statusCode != 302 && statusCode != 303 &&
                    statusCode != 307 && statusCode != 308)
                    throw new WebException("GitHub latest release returned HTTP " +
                        statusCode.ToString(CultureInfo.InvariantCulture));

                string location = response.Headers[HttpResponseHeader.Location];
                Uri destination;
                if (String.IsNullOrWhiteSpace(location) ||
                    !Uri.TryCreate(new Uri(LatestReleaseUrl), location, out destination) ||
                    !IsAllowedReleaseUrl(destination.AbsoluteUri))
                    throw new WebException("GitHub latest release returned an invalid location.");
                return destination.AbsoluteUri;
            }
        }

        internal static GitHubReleaseUpdateSnapshot EvaluateReleaseUrl(string releaseUrl)
        {
            string releaseTag;
            SemanticVersion current;
            SemanticVersion latest;
            if (!TryGetReleaseTag(releaseUrl, out releaseTag) ||
                !SemanticVersion.TryParse(CurrentVersion, out current) ||
                !SemanticVersion.TryParse(releaseTag, out latest))
                return null;

            GitHubReleaseUpdateSnapshot result = new GitHubReleaseUpdateSnapshot();
            result.LatestVersion = latest.DisplayVersion;
            result.ReleaseUrl = releaseUrl;
            result.UpdateAvailable = latest.CompareTo(current) > 0;
            return result;
        }

        internal static bool IsAllowedReleaseUrl(string value)
        {
            string releaseTag;
            SemanticVersion version;
            return TryGetReleaseTag(value, out releaseTag) &&
                SemanticVersion.TryParse(releaseTag, out version);
        }

        private static bool TryGetReleaseTag(string value, out string releaseTag)
        {
            releaseTag = String.Empty;
            if (String.IsNullOrWhiteSpace(value))
                return false;

            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                !String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Port != 443 || !String.IsNullOrEmpty(uri.UserInfo) ||
                !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                return false;

            string path = "/" + uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
            if (!path.StartsWith(AllowedReleasePrefix, StringComparison.Ordinal))
                return false;

            string escapedTag = path.Substring(AllowedReleasePrefix.Length);
            if (escapedTag.Length == 0 || escapedTag.IndexOf('/') >= 0 ||
                escapedTag.IndexOf('\\') >= 0)
                return false;

            try { releaseTag = Uri.UnescapeDataString(escapedTag); }
            catch { return false; }
            return true;
        }

        public void Dispose()
        {
            HttpWebRequest request;
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                state.IsChecking = false;
                request = activeRequest;
                activeRequest = null;
            }

            if (request != null)
            {
                try { request.Abort(); }
                catch { }
            }
        }

        private struct SemanticVersion : IComparable<SemanticVersion>
        {
            private ulong major;
            private ulong minor;
            private ulong patch;
            private string displayVersion;

            public string DisplayVersion
            {
                get { return displayVersion; }
            }

            public static bool TryParse(string value, out SemanticVersion version)
            {
                version = new SemanticVersion();
                if (String.IsNullOrEmpty(value))
                    return false;

                Match match = StableVersionPattern.Match(value);
                ulong major;
                ulong minor;
                ulong patch;
                if (!match.Success ||
                    !UInt64.TryParse(match.Groups[1].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out major) ||
                    !UInt64.TryParse(match.Groups[2].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out minor) ||
                    !UInt64.TryParse(match.Groups[3].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out patch))
                    return false;

                version.major = major;
                version.minor = minor;
                version.patch = patch;
                version.displayVersion = value.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    ? value.Substring(1)
                    : value;
                return true;
            }

            public int CompareTo(SemanticVersion other)
            {
                int result = major.CompareTo(other.major);
                if (result != 0) return result;
                result = minor.CompareTo(other.minor);
                if (result != 0) return result;
                return patch.CompareTo(other.patch);
            }
        }
    }
}
