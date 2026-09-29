using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CodexUsageOverlay
{
    internal static class ReleaseInstallerHttpTests
    {
        public static void ProductionTransportSendsConcurrentRanges()
        {
            byte[] bytes = new byte[2 * 1024 * 1024 + 37];
            for (int index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index * 29 + 11);
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            string directory = Path.Combine(Path.GetTempPath(), "CodexUpdateHttpTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using (var server = new RangeServer(bytes))
                {
                    bool refused = false;
                    try { ReleaseInstallerDownload.DownloadWithProgress(server.Url, null); }
                    catch (InvalidDataException) { refused = true; }
                    Assert(refused && server.Requests == 0, "public installer entry point accepted loopback instead of the release allowlist");

                    // Only the private transport is routed to this fixture; the public URL allowlist is unchanged.
                    MethodInfo getResponse = typeof(ReleaseInstallerDownload).GetMethod("GetResponse", BindingFlags.NonPublic | BindingFlags.Static);
                    Assert(getResponse != null, "production HTTP transport seam is missing");
                    string digest;
                    using (SHA256 hash = SHA256.Create()) digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
                    var asset = new ReleaseInstallerDownload.Asset { Name = "loopback-installer.exe", Size = bytes.Length,
                        Sha256 = digest, Url = server.Url };
                    string path = ReleaseInstallerDownload.DownloadAsset(asset, directory, null,
                        delegate(ReleaseInstallerDownload.Asset item, long? start, long? end)
                        {
                            try { return (ReleaseInstallerDownload.Response)getResponse.Invoke(null, new object[] { item.Url, true, start, end }); }
                            catch (TargetInvocationException error) { throw error.InnerException; }
                        });
                    Assert(File.ReadAllBytes(path).SequenceEqual(bytes), "real HTTP range assembly changed the installer");
                    server.Finish();
                    Assert(server.Failure == null, "HTTP fixture failed: " + server.Failure);
                    Assert(server.Requests == 5 && server.Probes == 1 && server.Ranges == 4,
                        "HTTP transport did not send the probe plus four exact Range requests");
                    Assert(server.PeakRanges >= 2 && server.PeakRanges <= 4,
                        "real HTTP connections were serial or exceeded four workers");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private sealed class RangeServer : IDisposable
        {
            private readonly byte[] bytes;
            private readonly TcpListener listener;
            private readonly Thread accept;
            private readonly object gate = new object();
            private readonly List<Thread> workers = new List<Thread>();
            private readonly ManualResetEvent twoRanges = new ManualResetEvent(false);
            private volatile bool stopping;
            private int activeRanges;
            internal int Requests, Probes, Ranges, PeakRanges;
            internal Exception Failure;
            internal string Url;

            internal RangeServer(byte[] bytes)
            {
                this.bytes = bytes;
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/asset";
                accept = new Thread(Accept) { IsBackground = true };
                accept.Start();
            }
            private void Accept()
            {
                try
                {
                    while (!stopping)
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        var worker = new Thread(delegate() { Serve(client); }) { IsBackground = true };
                        lock (gate) workers.Add(worker);
                        worker.Start();
                    }
                }
                catch (SocketException error) { if (!stopping) Remember(error); }
                catch (ObjectDisposedException error) { if (!stopping) Remember(error); }
            }
            private void Serve(TcpClient client)
            {
                bool countedRange = false;
                try
                {
                    using (client)
                    using (NetworkStream stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        stream.ReadTimeout = stream.WriteTimeout = 5000;
                        string request = reader.ReadLine();
                        Assert(request != null && request.StartsWith("GET ", StringComparison.Ordinal), "unexpected HTTP request");
                        string range = null, line;
                        int lines = 0;
                        while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                        {
                            Assert(++lines <= 64, "unexpectedly large fixture request headers");
                            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line.Substring(6).Trim();
                        }
                        Assert(range != null && range.StartsWith("bytes=", StringComparison.Ordinal), "production request omitted Range");
                        string[] parts = range.Substring(6).Split('-');
                        long start = Int64.Parse(parts[0]), end = Int64.Parse(parts[1]);
                        Assert(start >= 0 && end >= start && end < bytes.Length, "production request sent invalid range");
                        bool probe = start == 0 && end == 0;
                        lock (gate)
                        {
                            Requests++;
                            if (probe) Probes++;
                            else
                            {
                                Ranges++;
                                countedRange = true;
                                activeRanges++;
                                PeakRanges = Math.Max(PeakRanges, activeRanges);
                                if (activeRanges >= 2) twoRanges.Set();
                            }
                        }
                        string headers = "HTTP/1.1 206 Partial Content\r\nConnection: close\r\nContent-Type: application/octet-stream\r\n" +
                            "Content-Length: " + (end - start + 1) + "\r\nContent-Range: bytes " + start + "-" + end + "/" + bytes.Length + "\r\n\r\n";
                        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
                        stream.Write(headerBytes, 0, headerBytes.Length);
                        if (!probe) Assert(twoRanges.WaitOne(5000), "second HTTP range never reached the server");
                        for (long position = start; position <= end; position += 16384)
                            stream.Write(bytes, (int)position, (int)Math.Min(16384, end - position + 1));
                    }
                }
                catch (Exception error) { Remember(error); }
                finally { if (countedRange) lock (gate) activeRanges--; }
            }
            private void Remember(Exception error) { lock (gate) if (Failure == null) Failure = error; }
            internal void Finish()
            {
                if (stopping) return;
                stopping = true;
                listener.Stop();
                Assert(accept.Join(5000), "fixture listener did not stop");
                Thread[] current;
                lock (gate) current = workers.ToArray();
                foreach (Thread worker in current) Assert(worker.Join(5000), "fixture HTTP worker did not stop");
            }
            public void Dispose() { try { Finish(); } finally { twoRanges.Dispose(); } }
        }

        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
