using System;
using System.Net;

namespace CodexUsageOverlay
{
    // .NET Framework does not automatically honor the proxy variables used by
    // curl/GitHub CLI. Apply them only to this updater's individual requests.
    internal static class GitHubRequestProxy
    {
        internal static void Apply(HttpWebRequest request)
        {
            Apply(request, ReadVariable);
        }

        internal static void Apply(HttpWebRequest request, Func<string, string> read)
        {
            if (request.RequestUri.IsLoopback || Bypassed(request.RequestUri, read("NO_PROXY")))
            {
                request.Proxy = null;
                return;
            }
            string setting = read(request.RequestUri.Scheme == "https" ? "HTTPS_PROXY" : "HTTP_PROXY");
            if (String.IsNullOrWhiteSpace(setting)) setting = read("ALL_PROXY");
            Uri address;
            if (String.IsNullOrWhiteSpace(setting) ||
                !Uri.TryCreate(setting.Trim(), UriKind.Absolute, out address) ||
                (address.Scheme != "http" && address.Scheme != "https") ||
                String.IsNullOrEmpty(address.Host)) return; // Preserve the existing system proxy.
            var clean = new UriBuilder(address) { UserName = "", Password = "" };
            var proxy = new WebProxy(clean.Uri, true);
            if (!String.IsNullOrEmpty(address.UserInfo))
            {
                string[] credentials = address.UserInfo.Split(new[] { ':' }, 2);
                proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(credentials[0]),
                    credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : "");
            }
            request.Proxy = proxy;
        }

        private static string ReadVariable(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (String.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable(name.ToLowerInvariant());
            if (String.IsNullOrWhiteSpace(value))
                value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            return value;
        }

        private static bool Bypassed(Uri target, string exclusions)
        {
            if (String.IsNullOrWhiteSpace(exclusions)) return false;
            foreach (string value in exclusions.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string host = value.Trim();
                if (host == "*") return true;
                int colon = host.LastIndexOf(':');
                if (colon > 0 && host.IndexOf(':') == colon)
                {
                    int port;
                    if (!Int32.TryParse(host.Substring(colon + 1), out port) || port != target.Port) continue;
                    host = host.Substring(0, colon);
                }
                host = host.TrimStart('.');
                if (host.Length > 0 && (target.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                    target.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }
    }
}
