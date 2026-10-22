using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUsageOverlay
{
    // Downloads this assistant's installer, not the separate Codex MSIX package.
    internal static class ReleaseInstallerDownload
    {
        internal sealed class Asset
        {
            internal string Name, Url, Sha256;
            internal long Size;
        }

        internal sealed class Progress
        {
            internal string Phase;
            internal long DownloadedBytes, TotalBytes;
            internal double BytesPerSecond;
            internal int Connections, Percent;
        }

        // The stream seam lets tests exercise the transfer without weakening HTTP trust rules.
        internal sealed class Response : IDisposable
        {
            internal int StatusCode;
            internal string ContentRange;
            internal long ContentLength = -1;
            internal Stream Body;
            internal IDisposable Owner;

            public void Dispose()
            {
                try { if (Body != null) Body.Dispose(); }
                finally { if (Owner != null) Owner.Dispose(); }
            }
        }

        private sealed class ProgressReporter
        {
            private readonly object gate = new object();
            private readonly Action<Progress> callback;
            private readonly long total;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private long attemptBytes, transferredBytes, lastReport = -100;
            private string phase = "connecting";
            private int connections;

            internal ProgressReporter(long total, Action<Progress> callback)
            {
                this.total = total;
                this.callback = callback;
            }

            internal void SetPhase(string value, int activeConnections)
            {
                lock (gate)
                {
                    phase = value;
                    connections = activeConnections;
                    Report(true);
                }
            }

            internal void BeginAttempt(int activeConnections)
            {
                lock (gate)
                {
                    attemptBytes = 0;
                    phase = "downloading";
                    connections = activeConnections;
                    Report(true);
                }
            }

            internal void Add(int count)
            {
                lock (gate)
                {
                    attemptBytes += count;
                    transferredBytes += count;
                    Report(false);
                }
            }

            private void Report(bool force)
            {
                long now = clock.ElapsedMilliseconds;
                if (!force && now - lastReport < 100) return;
                lastReport = now;
                if (callback == null) return;
                long downloaded = Math.Min(total, attemptBytes);
                var value = new Progress
                {
                    Phase = phase,
                    DownloadedBytes = phase == "ready" ? total : downloaded,
                    TotalBytes = total,
                    BytesPerSecond = transferredBytes / Math.Max(0.001, clock.Elapsed.TotalSeconds),
                    Connections = connections,
                    Percent = phase == "ready" ? 100 : (int)Math.Min(99, downloaded * 100 / total)
                };
                // UI callbacks must neither race one another nor break the verified transfer.
                try { callback(value); }
                catch { }
            }
        }

        private sealed class RangeIgnoredException : IOException
        {
            internal RangeIgnoredException() : base("下载服务器忽略了分块请求，改用单连接下载。") { }
        }

        internal sealed class HttpFailureException : IOException
        {
            internal readonly int StatusCode;
            internal readonly string Host, Remaining, RetryAfter;
            internal HttpFailureException(int statusCode, string host, string remaining, string retryAfter)
                : base("HTTP " + statusCode.ToString(CultureInfo.InvariantCulture))
            {
                StatusCode = statusCode; Host = host; Remaining = remaining; RetryAfter = retryAfter;
            }
            internal bool IsRateLimited
            {
                get { return StatusCode == 429 || (StatusCode == 403 &&
                    (Remaining == "0" || !String.IsNullOrEmpty(RetryAfter))); }
            }
        }

        private sealed class OfficialFallbackException : IOException
        {
            internal OfficialFallbackException(Exception error)
                : base("接口限流后的官方备用下载未完成。", error) { }
        }

        private const long MaxInstallerBytes = 128 * 1024 * 1024;
        private const long MinChunkBytes = 512 * 1024;

        internal static Asset ParseAsset(string releaseUrl, string json)
        {
            GitHubReleaseUpdateSnapshot release = GitHubReleaseUpdateService.EvaluateReleaseUrl(releaseUrl);
            if (release == null || !release.UpdateAvailable)
                throw new InvalidDataException("只允许安装本项目较新的正式版本。");
            string tag = Uri.UnescapeDataString(new Uri(releaseUrl).Segments[5]);
            var serializer = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
            var data = serializer.DeserializeObject(json) as Dictionary<string, object>;
            object value;
            if (data == null || !data.TryGetValue("tag_name", out value) || !Equals(value, tag) ||
                !data.TryGetValue("draft", out value) || !Equals(value, false) ||
                !data.TryGetValue("prerelease", out value) || !Equals(value, false))
                throw new InvalidDataException("更新版本信息不匹配。");
            if (!data.TryGetValue("assets", out value) || !(value is object[]))
                throw new InvalidDataException("正式版本缺少安装包。");
            string name = "blues19-CodexUsageUpdateAssistant-Setup-" + release.LatestVersion + ".exe";
            string url = "https://github.com/ivan51769/CodexUsageOverlay/releases/download/" +
                Uri.EscapeDataString(tag) + "/" + name;
            Asset found = null;
            foreach (object item in (object[])value)
            {
                var asset = item as Dictionary<string, object>;
                if (asset == null || !asset.TryGetValue("name", out value) || !Equals(value, name)) continue;
                if (found != null) throw new InvalidDataException("存在重复安装包。");
                if (!asset.TryGetValue("browser_download_url", out value) || !Equals(value, url))
                    throw new InvalidDataException("安装包不是本项目的正式下载地址。");
                long size;
                if (!asset.TryGetValue("size", out value) ||
                    !Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out size) ||
                    size < 2 || size > MaxInstallerBytes)
                    throw new InvalidDataException("安装包大小无效。");
                string digest = asset.TryGetValue("digest", out value) ? value as string : null;
                if (digest == null || !Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z"))
                    throw new InvalidDataException("安装包缺少 SHA256 校验信息，已停止自动安装。");
                found = new Asset { Name = name, Url = url, Size = size, Sha256 = digest.Substring(7) };
            }
            if (found == null) throw new InvalidDataException("正式版本尚未上传安装包。");
            return found;
        }

        internal static bool IsTrustedRedirect(Uri uri)
        {
            return uri != null && uri.Scheme == "https" && uri.Port == 443 &&
                String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Fragment) &&
                (uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com");
        }

        private static Response GetResponse(string url, bool assetDownload, long? start, long? end)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            Uri next = new Uri(url);
            for (int hop = 0; hop < 4; hop++)
            {
                var request = (HttpWebRequest)WebRequest.Create(next);
                request.UserAgent = "CodexUsageOverlay/" + GitHubReleaseUpdateService.CurrentVersion;
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                request.AllowAutoRedirect = false;
                request.UseDefaultCredentials = false;
                request.Credentials = null;
                request.AutomaticDecompression = DecompressionMethods.None;
                request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                request.Headers[HttpRequestHeader.AcceptEncoding] = "identity";
                if (assetDownload)
                    request.ServicePoint.ConnectionLimit = Math.Max(4, request.ServicePoint.ConnectionLimit);
                if (start.HasValue) request.AddRange(start.Value, end.Value);
                HttpWebResponse response;
                try { response = (HttpWebResponse)request.GetResponse(); }
                catch (WebException ex)
                {
                    var failed = ex.Response as HttpWebResponse;
                    if (failed != null)
                    {
                        using (failed)
                            throw new HttpFailureException((int)failed.StatusCode, next.Host,
                                failed.Headers["X-RateLimit-Remaining"], failed.Headers["Retry-After"]);
                    }
                    if (ex.Response != null) ex.Response.Dispose();
                    throw;
                }
                if (response.StatusCode == HttpStatusCode.OK ||
                    (assetDownload && start.HasValue && response.StatusCode == HttpStatusCode.PartialContent))
                {
                    try
                    {
                        return new Response
                        {
                            StatusCode = (int)response.StatusCode,
                            ContentRange = response.Headers[HttpResponseHeader.ContentRange],
                            ContentLength = response.ContentLength,
                            Body = response.GetResponseStream(),
                            Owner = response
                        };
                    }
                    catch { response.Dispose(); throw; }
                }
                int code = (int)response.StatusCode;
                string location = response.Headers[HttpResponseHeader.Location];
                response.Dispose();
                Uri redirected;
                if (!assetDownload || (code != 301 && code != 302 && code != 303 && code != 307 && code != 308) ||
                    !Uri.TryCreate(next, location, out redirected) || !IsTrustedRedirect(redirected))
                    throw new InvalidDataException("更新下载地址不受信任或不可用，已停止安装。");
                next = redirected;
            }
            throw new InvalidDataException("安装包下载跳转次数过多，已停止安装。");
        }

        internal static void CopyVerified(Stream input, Stream output, Asset asset, Action<int> progress)
        {
            byte[] buffer = new byte[65536];
            long total = 0;
            int previous = -1, first = -1, second = -1;
            DateTime deadline = DateTime.UtcNow.AddMinutes(5);
            using (SHA256 hash = SHA256.Create())
            {
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("下载超时，请重试。");
                    if (total == 0) first = buffer[0];
                    if (total == 0 && count > 1) second = buffer[1];
                    else if (total == 1) second = buffer[0];
                    total += count;
                    if (total > asset.Size || total > MaxInstallerBytes)
                        throw new InvalidDataException("安装包大小与发布信息不一致。");
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                    output.Write(buffer, 0, count);
                    int percent = (int)Math.Min(99, total * 100 / asset.Size);
                    if (percent != previous && progress != null) progress(percent);
                    previous = percent;
                }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                string actual = BitConverter.ToString(hash.Hash).Replace("-", "");
                if (total != asset.Size || first != 'M' || second != 'Z' ||
                    !String.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包校验失败，未运行安装程序；请重试。");
            }
            if (progress != null) progress(100);
        }

        internal static string Download(string releaseUrl, Action<int> progress)
        {
            int previous = -1;
            return DownloadWithProgress(releaseUrl, delegate(Progress value)
            {
                if (progress != null && value.Percent != previous) progress(value.Percent);
                previous = value.Percent;
            });
        }

        internal static string DownloadWithProgress(string releaseUrl, Action<Progress> progress)
        {
            Asset asset = ResolveAsset(releaseUrl, GetResponse, ResolveLatestReleaseUrl, progress);
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexUsageOverlay", "updates", Guid.NewGuid().ToString("N"));
            return DownloadAsset(asset, directory, progress, delegate(Asset item, long? start, long? end)
            {
                return GetResponse(item.Url, true, start, end);
            });
        }

        private static string ResolveLatestReleaseUrl()
        {
            using (var service = new GitHubReleaseUpdateService())
                return service.ResolveLatestReleaseUrl();
        }

        internal static Asset ResolveAsset(string releaseUrl,
            Func<string, bool, long?, long?, Response> transport, Func<string> resolveLatest,
            Action<Progress> progress)
        {
            var release = GitHubReleaseUpdateService.EvaluateReleaseUrl(releaseUrl);
            if (release == null || !release.UpdateAvailable)
                throw new InvalidDataException("没有可安装的新版本。");
            string tag = Uri.UnescapeDataString(new Uri(releaseUrl).Segments[5]);
            if (progress != null)
            {
                try { progress(new Progress { Phase = "connecting" }); }
                catch { }
            }
            try
            {
                using (var response = transport("https://api.github.com/repos/ivan51769/CodexUsageOverlay/releases/tags/" +
                    Uri.EscapeDataString(tag), false, null, null))
                    return ParseAsset(releaseUrl, ReadText(response, 2 * 1024 * 1024));
            }
            catch (HttpFailureException error)
            {
                // Only explicit GitHub API throttling permits the alternate metadata path.
                // Authentication errors, bad digests and unpublished versions still fail closed.
                if (error.Host != "api.github.com" || !error.IsRateLimited) throw;
                if (progress != null)
                {
                    try { progress(new Progress { Phase = "fallback" }); }
                    catch { }
                }
                try { return ResolveOfficialAsset(releaseUrl, release, transport, resolveLatest); }
                catch (Exception fallbackError) { throw new OfficialFallbackException(fallbackError); }
            }
        }

        private static string ReadText(Response response, int limit)
        {
            if (response == null || response.StatusCode != 200 || response.Body == null)
                throw new InvalidDataException("更新信息响应无效。");
            using (var reader = new StreamReader(response.Body, Encoding.UTF8))
            {
                char[] text = new char[limit + 1];
                int count = reader.ReadBlock(text, 0, text.Length);
                if (count == text.Length) throw new InvalidDataException("更新信息过大。");
                return new string(text, 0, count);
            }
        }

        private static Asset ResolveOfficialAsset(string releaseUrl, GitHubReleaseUpdateSnapshot release,
            Func<string, bool, long?, long?, Response> transport, Func<string> resolveLatest)
        {
            // /releases/latest excludes draft and prerelease releases. If the selection
            // changed since the user clicked, refresh instead of silently switching versions.
            if (!String.Equals(resolveLatest(), releaseUrl, StringComparison.Ordinal))
                throw new InvalidDataException("最新正式版本已变化，请点击刷新后重新更新。");
            string tag = Uri.UnescapeDataString(new Uri(releaseUrl).Segments[5]);
            string name = "blues19-CodexUsageUpdateAssistant-Setup-" + release.LatestVersion + ".exe";
            string root = "https://github.com/ivan51769/CodexUsageOverlay/releases/download/" + Uri.EscapeDataString(tag) + "/";
            string digest = null;
            using (Response response = transport(root + "SHA256-" + Uri.EscapeDataString(tag) + ".txt", true, null, null))
            {
                string checksums = ReadText(response, 4096);
                foreach (string line in checksums.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Match match = Regex.Match(line, @"\A([0-9a-fA-F]{64}) [ *](.+)\z", RegexOptions.CultureInvariant);
                    if (!match.Success || !String.Equals(match.Groups[2].Value, name, StringComparison.Ordinal))
                        throw new InvalidDataException("官方校验文件与安装包名称不匹配，已停止安装。");
                    if (digest != null) throw new InvalidDataException("官方校验文件包含重复条目，已停止安装。");
                    digest = match.Groups[1].Value;
                }
            }
            if (digest == null) throw new InvalidDataException("官方校验文件缺少 SHA256，已停止安装。");
            var asset = new Asset { Name = name, Url = root + name, Sha256 = digest };
            using (Response response = transport(asset.Url, true, 0, 0))
            {
                if (response == null || response.Body == null) throw new InvalidDataException("安装包响应无效。");
                if (response.StatusCode == 206)
                {
                    Match range = Regex.Match(response.ContentRange ?? "", @"\Abytes 0-0/([0-9]+)\z",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!range.Success || !Int64.TryParse(range.Groups[1].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out asset.Size))
                        throw new InvalidDataException("安装包大小探测失败，已停止安装。");
                    ValidatePartialResponse(response, 0, 0, asset.Size);
                    CopyExact(response.Body, Stream.Null, 1, null, DateTime.UtcNow.AddSeconds(20), CancellationToken.None);
                }
                else if (response.StatusCode == 200) asset.Size = response.ContentLength;
                if (asset.Size < 2 || asset.Size > MaxInstallerBytes)
                    throw new InvalidDataException("安装包大小无效，已停止安装。");
            }
            return asset;
        }

        internal static string FailureMessage(Exception error)
        {
            string prefix = "";
            if (error is OfficialFallbackException)
            {
                prefix = "GitHub 接口已限流，官方备用下载也未完成：";
                error = error.InnerException;
            }
            string reason;
            var http = error as HttpFailureException;
            var web = error as WebException;
            if (http != null)
            {
                if (http.IsRateLimited) reason = "GitHub 请求已限流，请稍后重试。";
                else if (http.StatusCode == 404) reason = "官方安装包或校验文件不存在（HTTP 404）。";
                else if (http.StatusCode == 403) reason = "GitHub 拒绝访问（HTTP 403），并非已确认的限流。";
                else reason = "下载服务返回 HTTP " + http.StatusCode.ToString(CultureInfo.InvariantCulture) + "，请稍后重试。";
            }
            else if (error is InvalidDataException) reason = error.Message;
            else if (error is TimeoutException) reason = "下载超时，请检查网络或代理后重试。";
            else if (web != null)
            {
                if (web.Status == WebExceptionStatus.Timeout) reason = "下载连接超时，请检查网络或代理后重试。";
                else if (web.Status == WebExceptionStatus.NameResolutionFailure || web.Status == WebExceptionStatus.ProxyNameResolutionFailure)
                    reason = "无法解析下载服务器或代理地址，请检查网络设置。";
                else if (web.Status == WebExceptionStatus.TrustFailure || web.Status == WebExceptionStatus.SecureChannelFailure)
                    reason = "HTTPS 安全连接验证失败，已停止下载。";
                else reason = "下载连接中断或无法建立，请检查网络或代理后重试。";
            }
            else if (error is UnauthorizedAccessException) reason = "无法写入更新目录，请检查目录权限。";
            else if (error is IOException) reason = "下载文件读写失败或连接中断，请检查磁盘空间并重试。";
            else reason = "更新信息处理失败，已停止安装，请稍后重试。";
            return prefix + reason + " 当前版本没有改动。";
        }

        internal static void RecordFailure(Exception error)
        {
            // Keep only the latest failure, without signed URLs, response bodies or proxy credentials.
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-download.log"),
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + FailureMessage(error) + Environment.NewLine);
            }
            catch { }
        }

        internal static string DownloadAsset(Asset asset, string directory, Action<Progress> progress,
            Func<Asset, long?, long?, Response> openResponse)
        {
            if (asset == null || asset.Size < 2 || asset.Size > MaxInstallerBytes ||
                String.IsNullOrEmpty(asset.Name) || Path.GetFileName(asset.Name) != asset.Name ||
                asset.Sha256 == null || !Regex.IsMatch(asset.Sha256, @"\A[0-9a-fA-F]{64}\z") ||
                openResponse == null)
                throw new InvalidDataException("安装包信息无效。");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, asset.Name);
            string part = path + ".part";
            var chunks = new List<string>();
            var reporter = new ProgressReporter(asset.Size, progress);
            DateTime deadline = DateTime.UtcNow.AddMinutes(5);
            try
            {
                reporter.SetPhase("connecting", 0);
                int connections = (int)Math.Min(4, asset.Size / MinChunkBytes);
                bool completed = false;
                if (connections > 1)
                {
                    using (Response probe = openResponse(asset, 0, 0))
                    {
                        if (probe != null && probe.StatusCode == 200)
                        {
                            reporter.BeginAttempt(1);
                            DownloadSingle(probe, part, asset, reporter, deadline);
                            completed = true;
                        }
                        else
                        {
                            ValidatePartialResponse(probe, 0, 0, asset.Size);
                            CopyExact(probe.Body, Stream.Null, 1, null, deadline, CancellationToken.None);
                        }
                    }
                    if (!completed)
                    {
                        try
                        {
                            DownloadChunks(asset, part, chunks, connections, reporter, deadline, openResponse);
                            completed = true;
                        }
                        catch (RangeIgnoredException)
                        {
                            // Every worker has stopped before any single-stream retry begins.
                            foreach (string chunk in chunks) if (File.Exists(chunk)) File.Delete(chunk);
                            chunks.Clear();
                        }
                    }
                }
                if (!completed)
                {
                    reporter.BeginAttempt(1);
                    using (Response response = openResponse(asset, null, null))
                        DownloadSingle(response, part, asset, reporter, deadline);
                }
                CheckDeadline(deadline);
                reporter.SetPhase("verifying", 0);
                using (var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read))
                    CopyVerified(input, Stream.Null, asset, null);
                File.Move(part, path);
                reporter.SetPhase("ready", 0);
                return path;
            }
            finally
            {
                if (File.Exists(part)) File.Delete(part);
                foreach (string chunk in chunks) if (File.Exists(chunk)) File.Delete(chunk);
            }
        }

        private static void ValidatePartialResponse(Response response, long start, long end, long total)
        {
            if (response == null || response.StatusCode != 206 || response.Body == null)
                throw new InvalidDataException("分块下载未返回有效的 206 响应。");
            Match match = Regex.Match(response.ContentRange ?? "", @"\Abytes ([0-9]+)-([0-9]+)/([0-9]+)\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            long actualStart, actualEnd, actualTotal;
            if (!match.Success ||
                !Int64.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out actualStart) ||
                !Int64.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out actualEnd) ||
                !Int64.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out actualTotal) ||
                actualStart != start || actualEnd != end || actualTotal != total ||
                (response.ContentLength >= 0 && response.ContentLength != end - start + 1))
                throw new InvalidDataException("分块下载范围与发布信息不一致。");
        }

        private static void DownloadSingle(Response response, string part, Asset asset,
            ProgressReporter reporter, DateTime deadline)
        {
            if (response == null || response.StatusCode != 200 || response.Body == null ||
                (response.ContentLength >= 0 && response.ContentLength != asset.Size))
                throw new InvalidDataException("安装包响应大小与发布信息不一致。");
            using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                CopyExact(response.Body, output, asset.Size, reporter, deadline, CancellationToken.None);
        }

        private static void DownloadChunks(Asset asset, string part, List<string> chunks, int connections,
            ProgressReporter reporter, DateTime deadline, Func<Asset, long?, long?, Response> openResponse)
        {
            var threads = new List<Thread>();
            object errorGate = new object();
            Exception error = null;
            reporter.BeginAttempt(connections);
            using (var cancellation = new CancellationTokenSource())
            {
                long chunkSize = asset.Size / connections;
                try
                {
                    for (int i = 0; i < connections; i++)
                    {
                        long start = i * chunkSize;
                        long end = i == connections - 1 ? asset.Size - 1 : start + chunkSize - 1;
                        string chunk = part + "." + i.ToString(CultureInfo.InvariantCulture);
                        chunks.Add(chunk);
                        var worker = new Thread(delegate()
                        {
                            try
                            {
                                cancellation.Token.ThrowIfCancellationRequested();
                                CheckDeadline(deadline);
                                using (Response response = openResponse(asset, start, end))
                                {
                                    if (response != null && response.StatusCode == 200) throw new RangeIgnoredException();
                                    ValidatePartialResponse(response, start, end, asset.Size);
                                    using (var output = new FileStream(chunk, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                        CopyExact(response.Body, output, end - start + 1, reporter, deadline, cancellation.Token);
                                }
                            }
                            catch (Exception ex)
                            {
                                lock (errorGate)
                                {
                                    if (error == null || (error is RangeIgnoredException &&
                                        !(ex is RangeIgnoredException) && !(ex is OperationCanceledException))) error = ex;
                                }
                                cancellation.Cancel();
                            }
                        });
                        worker.IsBackground = true;
                        worker.Start();
                        threads.Add(worker);
                    }
                }
                catch { cancellation.Cancel(); throw; }
                finally
                {
                    // Never merge, retry, or delete files while a worker can still write to them.
                    foreach (Thread worker in threads) worker.Join();
                }
            }
            if (error != null) throw error;
            using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (string chunk in chunks)
                using (var input = new FileStream(chunk, FileMode.Open, FileAccess.Read, FileShare.Read))
                    CopyExact(input, output, input.Length, null, deadline, CancellationToken.None);
            }
        }

        private static void CopyExact(Stream input, Stream output, long expected, ProgressReporter reporter,
            DateTime deadline, CancellationToken cancellation)
        {
            byte[] buffer = new byte[65536];
            long total = 0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                CheckDeadline(deadline);
                int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, expected - total + 1));
                if (count == 0) break;
                total += count;
                if (total > expected) throw new InvalidDataException("安装包响应超过声明的大小。");
                output.Write(buffer, 0, count);
                if (reporter != null) reporter.Add(count);
            }
            if (total != expected) throw new InvalidDataException("安装包下载不完整，已停止安装。");
        }

        private static void CheckDeadline(DateTime deadline)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("下载超时，请重试。");
        }

        internal static string InstallArguments(string directory)
        {
            return "/SILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTOVERLAY=1 /DIR=\"" +
                Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + "\"";
        }
    }
}
