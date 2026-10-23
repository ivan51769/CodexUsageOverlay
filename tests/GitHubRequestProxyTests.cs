using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CodexUsageOverlay;

internal static class GitHubRequestProxyTests
{
    internal static void Verify()
    {
        var variables = new Dictionary<string, string> {
            { "HTTPS_PROXY", "http://127.0.0.1:17897" },
            { "HTTP_PROXY", "http://127.0.0.1:17898" },
            { "ALL_PROXY", "http://127.0.0.1:17899" } };
        Func<string, string> read = delegate(string name) { string value; return variables.TryGetValue(name, out value) ? value : null; };
        foreach (string host in new[] { "github.com", "api.github.com", "release-assets.githubusercontent.com" })
        {
            var request = (HttpWebRequest)WebRequest.Create("https://" + host + "/asset");
            GitHubRequestProxy.Apply(request, read);
            Assert(request.Proxy.GetProxy(request.RequestUri).Port == 17897, "HTTPS updater request ignored the environment proxy");
        }
        variables.Remove("HTTPS_PROXY");
        var fallback = (HttpWebRequest)WebRequest.Create("https://github.com/asset");
        GitHubRequestProxy.Apply(fallback, read);
        Assert(fallback.Proxy.GetProxy(fallback.RequestUri).Port == 17899, "ALL_PROXY fallback was ignored");
        variables["NO_PROXY"] = "localhost,.github.com:443";
        GitHubRequestProxy.Apply(fallback, read);
        Assert(fallback.Proxy == null, "NO_PROXY domain/port was ignored");
        variables["NO_PROXY"] = "github.com:80,notgithub.com";
        GitHubRequestProxy.Apply(fallback, read);
        Assert(fallback.Proxy != null, "NO_PROXY matched an unrelated host or port");
        var local = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:1/fixture");
        GitHubRequestProxy.Apply(local, read);
        Assert(local.Proxy == null, "Loopback fixture was sent through the external proxy");
        VerifyHttpProxyTransport();
    }

    private static void VerifyHttpProxyTransport()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string proxy = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception failure = null;
        var server = new Thread(delegate()
        {
            try
            {
                using (TcpClient client = listener.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    stream.ReadTimeout = 3000;
                    Assert(reader.ReadLine().StartsWith("GET http://updater-proxy-fixture.invalid/asset ", StringComparison.Ordinal),
                        "The proxy did not receive the absolute updater URL");
                    while (!String.IsNullOrEmpty(reader.ReadLine())) { }
                    byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 2\r\n\r\nMZ");
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        server.Start();
        try
        {
            var request = (HttpWebRequest)WebRequest.Create("http://updater-proxy-fixture.invalid/asset");
            request.Timeout = 3000;
            GitHubRequestProxy.Apply(request, delegate(string name) { return name == "HTTP_PROXY" ? proxy : null; });
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                Assert(reader.ReadToEnd() == "MZ", "Configured proxy did not deliver the update bytes");
        }
        finally { listener.Stop(); Assert(server.Join(4000), "Proxy fixture did not exit"); }
        Assert(failure == null, "Proxy fixture failed: " + failure);
    }

    private static void Assert(bool valid, string message) { if (!valid) throw new Exception(message); }
}
