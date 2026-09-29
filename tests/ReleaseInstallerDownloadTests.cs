using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace CodexUsageOverlay
{
    internal static class ReleaseInstallerDownloadTests
    {
        public static void ParallelRangesAreVerifiedAndReported()
        {
            foreach (int size in new[] { 1024 * 1024, 1536 * 1024, 2 * 1024 * 1024 + 37 })
            {
                byte[] bytes = Payload(size);
                using (var source = new RangeSource(bytes, "valid"))
                WithDirectory(delegate(string directory)
                {
                    var progress = new ProgressLog(directory);
                    string path = ReleaseInstallerDownload.DownloadAsset(Asset(bytes), directory, delegate(ReleaseInstallerDownload.Progress value)
                    {
                        progress.Record(value);
                        source.ObserveProgress(value);
                    }, source.Open);
                    Assert(File.ReadAllBytes(path).SequenceEqual(bytes), "range assembly changed installer bytes");
                    RequestRange[] ranges = source.Requests.Where(x => x.Start.HasValue && !x.IsProbe)
                        .OrderBy(x => x.Start.Value).ToArray();
                    int expectedConnections = Math.Min(4, size / (512 * 1024));
                    Assert(ranges.Length == expectedConnections, "unexpected parallel range count");
                    long next = 0;
                    foreach (RequestRange range in ranges)
                    {
                        Assert(range.Start == next && range.End >= range.Start, "ranges overlap or leave a hole");
                        next = range.End.Value + 1;
                    }
                    Assert(next == size && source.Peak >= 2 && source.Peak <= 4,
                        "range requests were serial, incomplete, or exceeded four active connections");
                    Assert(source.Active == 0, "successful range responses were not disposed");
                    progress.AssertSuccess(size, true);
                });
            }
        }

        public static void IgnoredRangesFallBackWithoutDuplicateProgress()
        {
            foreach (string mode in new[] { "probe200", "worker200" })
            {
                byte[] bytes = Payload(2 * 1024 * 1024 + 37);
                using (var source = new RangeSource(bytes, mode))
                WithDirectory(delegate(string directory)
                {
                    var progress = new ProgressLog(directory);
                    string path = ReleaseInstallerDownload.DownloadAsset(Asset(bytes), directory, delegate(ReleaseInstallerDownload.Progress value)
                    {
                        progress.Record(value);
                        source.ObserveProgress(value);
                    }, source.Open);
                    Assert(File.ReadAllBytes(path).SequenceEqual(bytes), mode + " did not produce the verified single-stream file");
                    Assert(source.Active == 0 && source.Peak <= 4, "fallback leaked or exceeded the connection cap");
                    if (mode == "probe200")
                        Assert(source.Peak == 1 && source.Requests.Count == 1,
                            "an ignored probe range must reuse its full 200 response as one stream");
                    else
                    {
                        Assert(source.Requests.Any(x => !x.Start.HasValue), "ignored worker range did not retry a full stream");
                        Assert(progress.Samples.Any(x => x.Phase == "downloading" && x.Connections > 1 && x.Downloaded > 0),
                            "fallback fixture never downloaded real partial bytes before retrying");
                        Assert(progress.Samples.Any(x => x.Phase == "downloading" && x.Connections == 1 &&
                            x.Downloaded == 0 && x.Percent == 0), "fallback retained discarded partial bytes as useful progress");
                    }
                    progress.AssertSuccess(bytes.Length, false);
                    Assert(progress.Samples.Any(x => x.Phase == "downloading" && x.Connections == 1),
                        "single-stream fallback was not reported honestly");
                });
            }
        }

        public static void InvalidTransfersNeverPublishReady()
        {
            foreach (string mode in new[] { "bad-probe", "wrong-start", "wrong-end", "wrong-total", "bad-length", "short", "long",
                "single-short", "single-long", "bad-sha", "bad-mz" })
            {
                byte[] expected = Payload(2 * 1024 * 1024 + 37);
                byte[] actual = (byte[])expected.Clone();
                if (mode == "bad-sha") actual[actual.Length / 2] ^= 0x7f;
                if (mode == "bad-mz") actual[0] = expected[0] = 0;
                using (var source = new RangeSource(actual, mode))
                WithDirectory(delegate(string directory)
                {
                    var progress = new ProgressLog(directory);
                    bool rejected = false;
                    try { ReleaseInstallerDownload.DownloadAsset(Asset(expected), directory, progress.Record, source.Open); }
                    catch (InvalidDataException) { rejected = true; }
                    catch (IOException) { rejected = true; }
                    Assert(rejected, mode + " was accepted");
                    Assert(source.Active == 0, mode + " left a response open");
                    Assert(Directory.GetFiles(directory).Length == 0, mode + " left a final or partial installer behind");
                    progress.AssertNotReady(expected.Length);
                });
            }
        }

        public static void FilePromotionFailureDoesNotComplete()
        {
            byte[] bytes = Payload(4097);
            using (var source = new RangeSource(bytes, "probe200"))
            WithDirectory(delegate(string directory)
            {
                var asset = Asset(bytes);
                string final = Path.Combine(directory, asset.Name);
                byte[] previous = new byte[] { 1, 2, 3 };
                File.WriteAllBytes(final, previous);
                var progress = new ProgressLog(directory);
                bool rejected = false;
                try { ReleaseInstallerDownload.DownloadAsset(asset, directory, progress.Record, source.Open); }
                catch (IOException) { rejected = true; }
                Assert(rejected && File.ReadAllBytes(final).SequenceEqual(previous), "failed promotion replaced an existing final file");
                Assert(!File.Exists(final + ".part") && source.Active == 0, "failed promotion left a partial file or response open");
                progress.AssertNotReady(bytes.Length);
            });
        }

        private sealed class ProgressSample
        {
            internal string Phase;
            internal long Downloaded, Total;
            internal double Speed;
            internal int Connections, Percent;
        }

        private sealed class ProgressLog
        {
            private readonly string directory;
            private bool prematureReady;
            internal readonly List<ProgressSample> Samples = new List<ProgressSample>();
            internal ProgressLog(string directory) { this.directory = directory; }
            internal void Record(ReleaseInstallerDownload.Progress value)
            {
                lock (Samples)
                {
                    if (value.Phase == "ready")
                        prematureReady |= !File.Exists(Path.Combine(directory, "test-installer.exe")) ||
                            File.Exists(Path.Combine(directory, "test-installer.exe.part"));
                    Samples.Add(new ProgressSample { Phase = value.Phase, Downloaded = value.DownloadedBytes,
                        Total = value.TotalBytes, Speed = value.BytesPerSecond, Connections = value.Connections,
                        Percent = value.Percent });
                }
            }
            internal void AssertSuccess(long size, bool parallel)
            {
                AssertValid(size);
                Assert(Samples.Any(x => x.Phase == "connecting") && Samples.Any(x => x.Phase == "downloading") &&
                    Samples.Any(x => x.Phase == "verifying"), "connecting/download/verification phases were not all reported");
                Assert(Samples.Count(x => x.Phase == "ready") == 1 && Samples.Last().Phase == "ready" &&
                    Samples.Last().Percent == 100 && Samples.Last().Downloaded == size,
                    "verified download did not publish exactly one final ready/100 snapshot");
                if (parallel) Assert(Samples.Any(x => x.Phase == "downloading" && x.Connections > 1),
                    "parallel connection count was not shown during downloading");
            }
            internal void AssertNotReady(long size)
            {
                AssertValid(size);
                Assert(Samples.All(x => x.Phase != "ready" && x.Percent < 100), "failure was falsely reported as complete");
            }
            private void AssertValid(long size)
            {
                Assert(!prematureReady, "ready was emitted before file promotion completed");
                long previous = 0;
                int previousConnections = 0;
                foreach (ProgressSample sample in Samples)
                {
                    bool fallbackReset = sample.Phase == "downloading" && sample.Connections == 1 &&
                        previousConnections > 1 && sample.Downloaded == 0 && sample.Percent == 0;
                    Assert((sample.Downloaded >= previous || fallbackReset) && sample.Downloaded <= size,
                        "aggregate bytes regressed outside a single-stream restart or counted overlapping retries");
                    Assert(sample.Total == size && sample.Percent >= 0 && sample.Percent <= 100,
                        "progress total/percentage did not match the verified release size");
                    Assert(sample.Phase == "ready" || sample.Percent < 100, "unverified data reached 100 percent");
                    Assert(sample.Connections >= 0 && sample.Connections <= 4 && sample.Speed >= 0 &&
                        !Double.IsNaN(sample.Speed) && !Double.IsInfinity(sample.Speed), "invalid connection count or speed");
                    previous = sample.Downloaded;
                    previousConnections = sample.Connections;
                }
            }
        }

        private sealed class RequestRange
        {
            internal long? Start, End;
            internal bool IsProbe { get { return Start == 0 && End == 0; } }
        }

        private sealed class RangeSource : IDisposable
        {
            private readonly byte[] bytes;
            private readonly string mode;
            private readonly ManualResetEvent parallelOpened = new ManualResetEvent(false);
            private readonly ManualResetEvent partialReported = new ManualResetEvent(false);
            private readonly object gate = new object();
            internal readonly List<RequestRange> Requests = new List<RequestRange>();
            internal int Active, Peak;
            internal RangeSource(byte[] bytes, string mode) { this.bytes = bytes; this.mode = mode; }
            public void Dispose() { parallelOpened.Dispose(); partialReported.Dispose(); }
            internal void ObserveProgress(ReleaseInstallerDownload.Progress value)
            {
                if (value.Phase == "downloading" && value.Connections > 1 && value.DownloadedBytes > 0)
                    partialReported.Set();
            }
            internal ReleaseInstallerDownload.Response Open(ReleaseInstallerDownload.Asset asset, long? start, long? end)
            {
                var request = new RequestRange { Start = start, End = end };
                bool single = !start.HasValue || mode == "probe200" || mode.StartsWith("single-", StringComparison.Ordinal) ||
                    (mode == "worker200" && !request.IsProbe && start.Value == bytes.Length / 4);
                long first = single ? 0 : start.Value;
                long last = single ? bytes.Length - 1 : end.Value;
                long bodyLength = last - first + 1;
                if (mode == "single-short" || (mode == "short" && !request.IsProbe)) bodyLength--;
                if (mode == "single-long" || (mode == "long" && !request.IsProbe)) bodyLength++;
                string contentRange = "bytes " + first + "-" + last + "/" + bytes.Length;
                if (request.IsProbe && mode == "bad-probe") contentRange = "bytes 0-0/*";
                if (!request.IsProbe)
                {
                    if (mode == "wrong-start") contentRange = "bytes " + (first + 1) + "-" + last + "/" + bytes.Length;
                    if (mode == "wrong-end") contentRange = "bytes " + first + "-" + (last + 1) + "/" + bytes.Length;
                    if (mode == "wrong-total") contentRange = "bytes " + first + "-" + last + "/" + (bytes.Length + 1);
                }
                lock (gate)
                {
                    Requests.Add(request);
                    Active++;
                    Peak = Math.Max(Peak, Active);
                    if (Active >= 2) parallelOpened.Set();
                }
                var stream = new PayloadStream(bytes, first, bodyLength, delegate { lock (gate) { Active--; } },
                    mode == "worker200" && start.HasValue && !request.IsProbe && !single ? 250 : 0);
                if (start.HasValue && !request.IsProbe && !single && !parallelOpened.WaitOne(5000))
                {
                    stream.Dispose();
                    throw new IOException("parallel regression: second connection never opened");
                }
                if (mode == "worker200" && start.HasValue && !request.IsProbe && single && !partialReported.WaitOne(5000))
                {
                    stream.Dispose();
                    throw new IOException("fallback fixture did not observe partial progress before a 200 response");
                }
                return new ReleaseInstallerDownload.Response { StatusCode = single ? 200 : 206,
                    ContentRange = single ? null : contentRange,
                    ContentLength = !request.IsProbe && mode == "bad-length" ? last - first : last - first + 1,
                    Body = stream };
            }
        }

        private sealed class PayloadStream : Stream
        {
            private readonly byte[] bytes;
            private readonly long offset, length;
            private readonly Action release;
            private readonly int firstReadDelay;
            private long position;
            private int disposed;
            internal PayloadStream(byte[] bytes, long offset, long length, Action release, int firstReadDelay)
            { this.bytes = bytes; this.offset = offset; this.length = length; this.release = release; this.firstReadDelay = firstReadDelay; }
            public override int Read(byte[] buffer, int destination, int count)
            {
                if (position == 0 && firstReadDelay > 0) Thread.Sleep(firstReadDelay);
                int available = (int)Math.Min(Math.Min(count, 16384), length - position);
                for (int index = 0; index < available; index++)
                {
                    long source = offset + position + index;
                    buffer[destination + index] = source < bytes.Length ? bytes[(int)source] : (byte)0;
                }
                position += available;
                return available;
            }
            protected override void Dispose(bool disposing)
            { if (Interlocked.Exchange(ref disposed, 1) == 0) release(); base.Dispose(disposing); }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return length; } }
            public override long Position { get { return position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long value, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        private static byte[] Payload(int size)
        {
            var bytes = new byte[size];
            for (int index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index * 31 + 7);
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            return bytes;
        }
        private static ReleaseInstallerDownload.Asset Asset(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
                return new ReleaseInstallerDownload.Asset { Name = "test-installer.exe", Size = bytes.Length,
                    Sha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""),
                    Url = "https://github.com/ivan51769/CodexUsageOverlay/releases/download/v99.0.0/test-installer.exe" };
        }
        private static void WithDirectory(Action<string> test)
        {
            string directory = Path.Combine(Path.GetTempPath(), "CodexUpdateTransferTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(directory); }
            finally { Directory.Delete(directory, true); }
        }
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
