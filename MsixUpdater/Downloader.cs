using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace Blues19.CodexInstaller
{
    /// <summary>
    /// 多线程分块下载器，支持断点续传、单块重试、整包完整性校验。
    /// 语法保持 C# 5 兼容，以便用 Windows 内置的 csc.exe 直接编译。
    /// </summary>
    public sealed class Downloader
    {
        public const int DefaultThreads = 8;

        // 分块小于该值时不值得开多线程，直接单线程下载
        private const long MinBytesPerThread = 4L * 1024 * 1024;
        private const int BufferSize = 128 * 1024;
        // 探测阶段用较短的超时：链路被墙时要尽快失败并给出提示，而不是干等 30 秒
        private const int ProbeTimeoutMs = 12000;
        private const int ConnectTimeoutMs = 30000;
        private const int IdleTimeoutMs = 45000;
        private const int MaxRetriesPerChunk = 8;         // 连续无进展多少次就放弃
        private const int MinTotalAttemptsPerChunk = 64;  // 总次数天花板的下限
        private const int MaxTotalAttemptsPerChunk = 4096;// 总次数天花板的上限

        /// <summary>
        /// 「即使一直有进展也要有个天花板」的那个天花板，必须随分块大小放大。
        /// 写死成一个小常数（试过 60）会误伤真实场景：链路不稳时每次请求只传回几十 KB
        /// 但每次都有进展，一个 88MB 的分块光正常续传就要几百次请求，
        /// 于是下载会在 90% 上下以「已重试 61 次，其中连续 0 次无进展」失败——
        /// 而这恰恰是这个下载器存在的意义所在的网络环境。
        /// 真正防死循环的是 idleAttempts（连续零进展计数），这里只是最后的保险。
        /// </summary>
        private static int TotalAttemptCap(long chunkLength)
        {
            long byPayload = chunkLength / (64 * 1024);   // 假定每次请求至少能传回 64KB
            long cap = MinTotalAttemptsPerChunk + byPayload;
            if (cap < MinTotalAttemptsPerChunk) cap = MinTotalAttemptsPerChunk;
            if (cap > MaxTotalAttemptsPerChunk) cap = MaxTotalAttemptsPerChunk;
            return (int)cap;
        }

        public sealed class Progress
        {
            public long Downloaded;
            public long Total;
            public double SpeedBytesPerSec;
            public int ActiveThreads;
            public bool Resumed;
        }

        private readonly Action<string> _log;
        private readonly Action<Progress> _onProgress;
        private readonly CancellationToken _cancel;

        private long _downloaded;
        private long _total;
        private int _activeThreads;
        private bool _resumed;
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private long _baselineBytes;   // 本次启动前已存在的分块字节，计算速度时要扣掉
        private int _lastReportTick;
        private string _hostHeader;    // 探测阶段选定的 Host 头写法，后续所有请求沿用

        public Downloader(Action<string> log, Action<Progress> onProgress, CancellationToken cancel)
        {
            _log = log ?? delegate { };
            _onProgress = onProgress ?? delegate { };
            _cancel = cancel;
        }

        public sealed class RemoteInfo
        {
            public long Size;
            public bool AcceptRanges;
            public string FinalUrl;
        }

        /// <summary>
        /// 探测远端文件大小与是否支持 Range 分块。
        /// </summary>
        public RemoteInfo Probe(string url)
        {
            return Probe(url, null);
        }

        public RemoteInfo Probe(string url, string hostHeader)
        {
            HttpWebRequest req = Http.CreateGet(url, hostHeader);
            req.AddRange(0, 0);
            req.Timeout = ProbeTimeoutMs;
            req.ReadWriteTimeout = ProbeTimeoutMs;

            using (HttpWebResponse res = Http.GetResponse(req))
            {
                int code = (int)res.StatusCode;
                if (code < 200 || code >= 300)
                {
                    // 403 通常意味着直链签名已过期或被篡改
                    Drain(res);
                    throw new HttpStatusException(code, "下载服务器返回 HTTP " + code +
                        (code == 403 ? "（下载直链已失效，请重新检查更新）" : ""));
                }

                RemoteInfo info = new RemoteInfo();
                info.FinalUrl = res.ResponseUri != null ? res.ResponseUri.AbsoluteUri : url;

                if (res.StatusCode == HttpStatusCode.PartialContent)
                {
                    string contentRange = res.Headers["Content-Range"];
                    long totalFromRange = ParseContentRangeTotal(contentRange);
                    if (totalFromRange > 0)
                    {
                        info.Size = totalFromRange;
                        info.AcceptRanges = true;
                        Drain(res);
                        return info;
                    }
                }

                // 服务端忽略了 Range（返回 200 全量），只能单线程下载。
                // 这里绝对不能 Drain：那会把整整 700MB 的包读完再丢掉，
                // 等于探测阶段就白下一遍。直接 Abort 掐断连接。
                info.Size = res.ContentLength > 0 ? res.ContentLength : 0;
                info.AcceptRanges = false;
                try { req.Abort(); }
                catch { }
                return info;
            }
        }

        /// <summary>
        /// 微软返回的下载直链是 http 的，而 dl.delivery.mp.microsoft.com 这类 CDN 域名
        /// 在部分网络下会被拦掉（查询用的 API 域名却是通的）。这里依次尝试
        /// 原始 http 链接和它的 https 变体，全都不通时给出能照着做的提示。
        /// </summary>
        private sealed class Route
        {
            public string Url;
            public string HostHeader;    // null = 用 URL 里的域名
            public string Label;
        }

        private RemoteInfo ProbeWithFallback(ref string url)
        {
            string host = null;
            try { host = new Uri(url).Host; }
            catch { }

            string httpsUrl = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "https://" + url.Substring(7) : null;
            string dottedHost = string.IsNullOrEmpty(host) ? null : host + ".";

            List<Route> routes = new List<Route>();
            routes.Add(NewRoute(url, null, "原始直链"));
            if (httpsUrl != null) routes.Add(NewRoute(httpsUrl, null, "HTTPS"));
            // 末尾点写法：部分网络设备按 Host 头精确匹配来拦微软下载域名，
            // "host." 是合法的绝对域名写法，能绕过这种字符串匹配而 CDN 照常响应
            if (dottedHost != null) routes.Add(NewRoute(url, dottedHost, "绝对域名写法"));
            if (dottedHost != null && httpsUrl != null) routes.Add(NewRoute(httpsUrl, dottedHost, "HTTPS + 绝对域名写法"));

            Exception last = null;
            for (int i = 0; i < routes.Count; i++)
            {
                _cancel.ThrowIfCancellationRequested();
                Route route = routes[i];
                try
                {
                    // 探测成功就采用这条路，哪怕服务端没报大小、也不支持分块——
                    // 那种情况交给上层退回单线程下载，而不是当作「连不上」报错。
                    RemoteInfo info = Probe(route.Url, route.HostHeader);
                    url = route.Url;
                    _hostHeader = route.HostHeader;
                    if (i > 0) _log("已改用「" + route.Label + "」连接下载服务器。");
                    return info;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    if (i + 1 < routes.Count)
                        _log("「" + route.Label + "」不通（" + ex.Message + "），换下一种方式…");
                }
            }

            throw new IOException(
                "连不上微软的下载服务器 " + (host ?? "") + "。\n" +
                "查询接口是通的，说明只有下载 CDN 这一段被网络拦住了——这在国内网络下很常见。\n" +
                "已经依次试过 HTTP / HTTPS / 绝对域名写法，都不通。\n" +
                "请开启你的代理或加速工具后重试，或点“网络设置”把代理地址填进去（支持自动探测本地代理）。\n" +
                "底层错误：" + (last != null ? last.Message : "未知"), last);
        }

        private static Route NewRoute(string url, string hostHeader, string label)
        {
            Route r = new Route();
            r.Url = url;
            r.HostHeader = hostHeader;
            r.Label = label;
            return r;
        }

        private static void Drain(HttpWebResponse res)
        {
            try
            {
                using (Stream s = res.GetResponseStream())
                {
                    if (s != null)
                    {
                        byte[] buf = new byte[1024];
                        while (s.Read(buf, 0, buf.Length) > 0) { }
                    }
                }
            }
            catch { }
        }

        internal static long ParseContentRangeTotal(string contentRange)
        {
            if (string.IsNullOrEmpty(contentRange)) return 0;
            int slash = contentRange.LastIndexOf('/');
            if (slash < 0 || slash == contentRange.Length - 1) return 0;
            string tail = contentRange.Substring(slash + 1).Trim();
            if (tail == "*") return 0;
            long value;
            return long.TryParse(tail, out value) ? value : 0;
        }

        /// <summary>
        /// 下载到 destPath。expectedSize &gt; 0 时做严格大小校验；
        /// digestBase64 非空时按 digestAlgorithm（SHA1/SHA256）校验内容摘要。
        /// </summary>
        public void Download(string url, string destPath, long expectedSize,
            string digestBase64, string digestAlgorithm)
        {
            RemoteInfo info = ProbeWithFallback(ref url);

            // 分块的范围计算必须按服务端实际提供的大小来算，否则会去请求不存在的字节区间；
            // 但最终校验要同时对上「服务端大小」和「微软清单登记的大小」——
            // 只信服务端自报的大小，被截断或被替换的响应就能蒙混过关。
            long total = info.Size > 0 ? info.Size : expectedSize;
            if (expectedSize > 0 && info.Size > 0 && info.Size != expectedSize)
            {
                _log(string.Format("警告：服务端报出的大小 {0} 与官方清单登记的 {1} 不一致，下载后会按清单校验。",
                    Fmt.Size(info.Size), Fmt.Size(expectedSize)));
            }

            if (total <= 0)
            {
                _log("服务端未提供文件大小，改用单线程下载。");
                DownloadSingle(url, destPath, 0);
            }
            else if (!info.AcceptRanges)
            {
                _log("服务端不支持分块下载，改用单线程下载。");
                DownloadSingle(url, destPath, total);
            }
            else
            {
                DownloadChunked(url, destPath, total);
            }

            try
            {
                VerifyResult(destPath, total, expectedSize, digestBase64, digestAlgorithm);
            }
            catch
            {
                // 校验没过的文件必须立刻删掉。留在原地的话，下次运行时
                // MainForm 只看大小和文件头就会把它当成「本地已有完整安装包」直接拿去安装。
                _log("校验未通过，已删除损坏的文件。");
                TryDelete(destPath);
                throw;
            }
        }

        /// <summary>
        /// transferSize 是服务端声明的大小，manifestSize 是微软清单登记的大小。
        /// 两个都要对上：只对服务端那个，被中间环节替换掉的响应可以自圆其说。
        /// </summary>
        private void VerifyResult(string destPath, long transferSize, long manifestSize,
            string digestBase64, string digestAlgorithm)
        {
            FileInfo fi = new FileInfo(destPath);
            if (!fi.Exists) throw new IOException("下载结束但文件不存在：" + destPath);

            // 大小必须完全相等。只卡下限的话，服务端忽略 Range 导致每个分块都拿到整包时，
            // 合并出来的文件会是正常大小的 N 倍，却照样“校验通过”。
            if (transferSize > 0 && fi.Length != transferSize)
            {
                throw new IOException(string.Format(
                    "下载文件大小不符：期望 {0}，实际 {1}。文件可能损坏，请重试。",
                    Fmt.Size(transferSize), Fmt.Size(fi.Length)));
            }

            if (manifestSize > 0 && fi.Length != manifestSize)
            {
                throw new IOException(string.Format(
                    "下载到的文件大小（{0}）与微软官方清单登记的大小（{1}）不一致，" +
                    "说明拿到的不是预期的安装包。请重试，或检查网络中是否有代理改写了响应。",
                    Fmt.Size(fi.Length), Fmt.Size(manifestSize)));
            }

            if (!LooksLikeMsix(destPath))
            {
                throw new IOException(
                    "下载到的文件不是有效的安装包（缺少 MSIX/ZIP 文件头），" +
                    "多半是网络中途返回了错误页面。请重试，或检查是否需要配置代理。");
            }

            if (!string.IsNullOrEmpty(digestBase64))
            {
                string algorithm = string.IsNullOrEmpty(digestAlgorithm) ? "SHA1" : digestAlgorithm.ToUpperInvariant();
                _log("正在校验安装包完整性（" + algorithm + "）…");
                string actual = ComputeDigestBase64(destPath, algorithm);
                if (actual == null)
                {
                    _log("不认识的摘要算法 " + algorithm + "，跳过内容校验。");
                }
                else if (!string.Equals(actual, digestBase64, StringComparison.Ordinal))
                {
                    throw new IOException("安装包内容校验失败，文件在传输中被损坏。期望 "
                        + digestBase64 + "，实际 " + actual + "。请重试下载。");
                }
                else
                {
                    _log("完整性校验通过。");
                }
            }
        }

        /// <summary>按微软清单里登记的算法计算 Base64 摘要；不认识的算法返回 null。</summary>
        public static string ComputeDigestBase64(string path, string algorithm)
        {
            HashAlgorithm hash;
            switch ((algorithm ?? "SHA1").ToUpperInvariant())
            {
                case "SHA1": hash = SHA1.Create(); break;
                case "SHA256": hash = SHA256.Create(); break;
                case "SHA512": hash = SHA512.Create(); break;
                default: return null;
            }

            using (hash)
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            {
                return Convert.ToBase64String(hash.ComputeHash(fs));
            }
        }

        /// <summary>
        /// msix/msixbundle 本质是 zip，头四个字节必然是 PK\x03\x04。
        /// 这一步几乎不花时间，却能把“把 HTML 错误页当安装包存下来”这类问题当场拦住。
        /// </summary>
        public static bool LooksLikeMsix(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < 4) return false;
                    byte[] head = new byte[4];
                    int read = 0;
                    while (read < 4)
                    {
                        int n = fs.Read(head, read, 4 - read);
                        if (n <= 0) return false;
                        read += n;
                    }
                    return head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 下载前确认磁盘装得下。除了包本身，分块文件与合并中间文件会同时存在，
        /// 峰值大约要 2.2 倍包体积；700MB 的包意味着需要 1.6GB 左右空余。
        /// </summary>
        /// <summary>
        /// 分块文件会在包名后再加 ".part0"~".part15"、".parts"、".merging"，
        /// 目标路径已经接近 260 字符时这些临时文件会创建失败，而失败点在下载中途、
        /// 表现得像网络错误一直重试。提前拦住并给出能照做的提示。
        /// </summary>
        public static void EnsurePathLength(string destPath)
        {
            if (string.IsNullOrEmpty(destPath)) return;
            const int suffixRoom = 10;   // ".merging" / ".part15" 里最长的那个
            if (destPath.Length + suffixRoom <= 259) return;

            throw new PathTooLongException(
                "工作目录层级太深：加上分块文件后缀会超出 Windows 的 260 字符路径上限。\n" +
                "当前目标路径长度 " + destPath.Length + " 字符。\n" +
                "请把本工具放到层级更浅的目录（例如 D:\\CodexInstaller）后重试。");
        }

        public static void EnsureDiskSpace(string targetDir, long packageBytes, Action<string> log)
        {
            if (packageBytes <= 0) return;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(targetDir));
                if (string.IsNullOrEmpty(root)) return;
                DriveInfo drive = new DriveInfo(root);
                if (!drive.IsReady) return;

                long needed = (long)(packageBytes * 2.2);
                if (drive.AvailableFreeSpace >= needed) return;

                throw new IOException(string.Format(
                    "磁盘空间不足：{0} 可用 {1}，下载并安装大约需要 {2}（安装包 {3} + 分块与合并的临时空间）。请清理后重试。",
                    root, Fmt.Size(drive.AvailableFreeSpace), Fmt.Size(needed), Fmt.Size(packageBytes)));
            }
            catch (IOException) { throw; }
            catch (Exception ex)
            {
                if (log != null) log("无法检测磁盘剩余空间（" + ex.Message + "），继续执行。");
            }
        }

        // ---------------- 单线程 ----------------

        private void DownloadSingle(string url, string destPath, long total)
        {
            _total = total;
            _downloaded = 0;
            _baselineBytes = 0;
            _activeThreads = 1;

            string tmp = destPath + ".partial";
            TryDelete(tmp);

            HttpWebRequest req = Http.CreateGet(url, _hostHeader);
            req.Timeout = ConnectTimeoutMs;
            req.ReadWriteTimeout = IdleTimeoutMs;

            using (HttpWebResponse res = Http.GetResponse(req))
            {
                if (res.StatusCode != HttpStatusCode.OK && res.StatusCode != HttpStatusCode.PartialContent)
                    throw new IOException("下载失败，HTTP " + (int)res.StatusCode);

                if (_total <= 0 && res.ContentLength > 0) _total = res.ContentLength;

                using (Stream input = res.GetResponseStream())
                using (FileStream output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
                {
                    byte[] buffer = new byte[BufferSize];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        _cancel.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        AddProgress(read);
                    }
                    output.Flush();
                }
            }

            _activeThreads = 0;
            ReplaceFile(tmp, destPath);
            ReportProgress(true);
        }

        // ---------------- 多线程分块 ----------------

        private sealed class Chunk
        {
            public int Index;
            public long Start;
            public long End;        // 含
            public string PartPath;
            public long Length { get { return End - Start + 1; } }
        }

        private void DownloadChunked(string url, string destPath, long total)
        {
            int threads = Settings.Current.Threads;
            if (threads < 1) threads = DefaultThreads;
            if (threads > 16) threads = 16;
            while (threads > 1 && total / threads < MinBytesPerThread) threads--;

            _total = total;
            _downloaded = 0;

            List<Chunk> chunks = new List<Chunk>();
            long chunkSize = (total + threads - 1) / threads;
            for (int i = 0; i < threads; i++)
            {
                long start = i * chunkSize;
                if (start >= total) break;
                Chunk c = new Chunk();
                c.Index = i;
                c.Start = start;
                c.End = Math.Min(total - 1, start + chunkSize - 1);
                c.PartPath = destPath + ".part" + i;
                chunks.Add(c);
            }

            // 断点续传前先核对分块归属：只按文件大小判断“已下载多少”是危险的——
            // 上次下的是旧版本、或者换了下载源，残留的 .partN 大小可能刚好合法，
            // 于是一个字节都不重下就合出一个内容完全过期的包，大小校验还会通过。
            // 这里用一份 .parts 清单把分块和 URL/总大小/线程数绑定起来，对不上就全部作废。
            if (!PartManifest.Matches(destPath, url, total, chunks.Count))
            {
                if (PartManifest.AnyPartExists(destPath))
                    _log("检测到上次遗留的分块与本次下载不匹配（版本或来源已变），已全部丢弃重新下载。");
                PartManifest.DiscardAll(destPath);
            }
            PartManifest.Write(destPath, url, total, chunks.Count);

            // 清掉上一次残留但本次用不到的分块文件（线程数变化时会出现）
            CleanStaleParts(destPath, chunks.Count);

            // 统计已有进度：超长的分块直接截断，避免污染合并结果
            long existing = 0;
            foreach (Chunk c in chunks)
            {
                long have = SafePartLength(c.PartPath, c.Length);
                existing += have;
            }
            _downloaded = existing;
            _baselineBytes = existing;
            _resumed = existing > 0;
            if (_resumed)
            {
                _log(string.Format("发现上次未完成的下载，已完成 {0} / {1}，从断点继续。",
                    Fmt.Size(existing), Fmt.Size(total)));
            }

            _log(string.Format("文件大小 {0}，启用 {1} 线程分块下载。", Fmt.Size(total), chunks.Count));
            ReportProgress(true);

            Exception firstError = null;
            object errLock = new object();
            using (CountdownEvent done = new CountdownEvent(chunks.Count))
            {
                foreach (Chunk chunk in chunks)
                {
                    Chunk captured = chunk;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Interlocked.Increment(ref _activeThreads);
                        try
                        {
                            DownloadChunk(url, captured);
                        }
                        catch (Exception ex)
                        {
                            lock (errLock) { if (firstError == null) firstError = ex; }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _activeThreads);
                            done.Signal();
                        }
                    });
                }

                // 等待期间持续刷新进度
                while (!done.Wait(300))
                {
                    ReportProgress(true);
                }
            }
            ReportProgress(true);

            if (firstError != null) throw firstError;
            _cancel.ThrowIfCancellationRequested();

            _log("所有分块下载完成，正在合并 ...");
            MergeChunks(chunks, destPath, total);
            _log("合并完成。");
        }

        private void DownloadChunk(string url, Chunk chunk)
        {
            long have = SafePartLength(chunk.PartPath, chunk.Length);
            int idleAttempts = 0;    // 连续「毫无进展」的次数，用来退避和放弃
            int totalAttempts = 0;   // 总尝试次数，兜底防死循环
            int totalCap = TotalAttemptCap(chunk.Length);

            while (have < chunk.Length)
            {
                _cancel.ThrowIfCancellationRequested();
                long before = have;
                long from = chunk.Start + have;
                Exception error = null;

                try
                {
                    HttpWebRequest req = Http.CreateGet(url, _hostHeader);
                    req.AddRange(from, chunk.End);
                    req.Timeout = ConnectTimeoutMs;
                    req.ReadWriteTimeout = IdleTimeoutMs;

                    // 把取消信号接到请求上。只在两次 Read 之间检查标志是不够的：
                    // 连接卡住时 Read 会一直阻塞到 45 秒读超时，用户点了取消要等半分钟才有反应。
                    // Abort 能立刻把阻塞中的 Read 打断。
                    using (CancellationTokenRegistration reg = _cancel.Register(delegate
                    {
                        try { req.Abort(); }
                        catch { }
                    }))
                    using (HttpWebResponse res = Http.GetResponse(req))
                    {
                        if (res.StatusCode != HttpStatusCode.PartialContent)
                        {
                            // 200 表示服务端忽略了 Range，继续追加会写坏数据
                            throw new IOException("分块请求未返回 206，实际 HTTP " + (int)res.StatusCode);
                        }

                        using (Stream input = res.GetResponseStream())
                        using (FileStream output = new FileStream(chunk.PartPath,
                            have > 0 ? FileMode.Append : FileMode.Create,
                            FileAccess.Write, FileShare.None, BufferSize))
                        {
                            byte[] buffer = new byte[BufferSize];
                            int read;
                            while (have < chunk.Length && (read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                _cancel.ThrowIfCancellationRequested();
                                int usable = (int)Math.Min(read, chunk.Length - have);
                                output.Write(buffer, 0, usable);
                                have += usable;
                                AddProgress(usable);
                            }
                            output.Flush();
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 被上面的 Abort 打断时，底层抛的是 WebException，要翻译回取消
                    if (_cancel.IsCancellationRequested) throw new OperationCanceledException();
                    error = ex;
                }

                // 以磁盘上真实落地的字节数为准，避免重试时进度虚高
                long confirmed = SafePartLength(chunk.PartPath, chunk.Length);
                Interlocked.Add(ref _downloaded, confirmed - have);
                have = confirmed;
                if (have >= chunk.Length) break;

                // 注意：没有异常也可能没下完——服务端提前关闭连接时 Read 直接返回 0。
                // 这种「安静的提前结束」如果不当成失败处理，外层循环会立刻再发一次请求，
                // 变成没有退避、没有上限的空转，每秒几千次打爆服务端。
                if (error == null)
                    error = new IOException("服务端提前结束了传输（还差 " + Fmt.Size(chunk.Length - have) + "）");

                totalAttempts++;
                if (have > before) idleAttempts = 0;    // 有实质进展，退避计数归零
                else idleAttempts++;

                if (idleAttempts > MaxRetriesPerChunk || totalAttempts > totalCap)
                {
                    throw new IOException(string.Format("分块 {0} 传输失败（已重试 {1} 次，其中连续 {2} 次无进展）：{3}",
                        chunk.Index, totalAttempts, idleAttempts, error.Message), error);
                }

                int delay = Math.Min(8000, 500 * (1 << Math.Min(Math.Max(idleAttempts, 1) - 1, 4)));
                _log(string.Format("分块 {0} 中断（{1}），{2}ms 后从 {3} 续传（第 {4} 次重试）。",
                    chunk.Index, error.Message, delay, Fmt.Size(have), totalAttempts));
                if (_cancel.WaitHandle.WaitOne(delay)) _cancel.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// 读取分块已下载字节；超过应有长度时截断，返回真实可用长度。
        /// </summary>
        private static long SafePartLength(string partPath, long maxLength)
        {
            try
            {
                FileInfo fi = new FileInfo(partPath);
                if (!fi.Exists) return 0;
                if (fi.Length <= maxLength) return fi.Length;

                using (FileStream fs = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    fs.SetLength(maxLength);
                }
                return maxLength;
            }
            catch
            {
                return 0;
            }
        }

        private static void CleanStaleParts(string destPath, int keepCount)
        {
            try
            {
                string dir = Path.GetDirectoryName(destPath);
                string name = Path.GetFileName(destPath);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                foreach (string file in Directory.GetFiles(dir, name + ".part*"))
                {
                    string suffix = Path.GetFileName(file).Substring(name.Length + 5);
                    int idx;
                    if (int.TryParse(suffix, out idx) && idx >= keepCount) TryDelete(file);
                }
            }
            catch { }
        }

        private void MergeChunks(List<Chunk> chunks, string destPath, long total)
        {
            string tmp = destPath + ".merging";
            TryDelete(tmp);

            using (FileStream output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                byte[] buffer = new byte[BufferSize];
                foreach (Chunk c in chunks)
                {
                    using (FileStream input = new FileStream(c.PartPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
                    {
                        if (input.Length != c.Length)
                        {
                            throw new IOException(string.Format(
                                "分块 {0} 长度异常（{1} != {2}），已中止合并以免生成损坏文件。",
                                c.Index, input.Length, c.Length));
                        }
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            _cancel.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                        }
                    }
                }
                output.Flush(true);
                if (output.Length != total)
                    throw new IOException("合并结果长度异常：" + output.Length + " != " + total);
            }

            ReplaceFile(tmp, destPath);
            foreach (Chunk c in chunks) TryDelete(c.PartPath);
            PartManifest.Delete(destPath);
        }

        private static void ReplaceFile(string tmp, string destPath)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (File.Exists(destPath)) File.Delete(destPath);
                    File.Move(tmp, destPath);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(300);
                }
            }
            // 最后一次让异常抛出去，附带可读信息
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private void AddProgress(int bytes)
        {
            Interlocked.Add(ref _downloaded, bytes);
            ReportProgress(false);
        }

        /// <summary>
        /// 分块归属清单。断点续传只看 .partN 的大小是不够的——必须确认这些分块
        /// 确实来自同一个 URL、同样的总大小、同样的切分方式，否则一律丢弃重下。
        /// </summary>
        internal static class PartManifest
        {
            private const string Version = "v1";

            private static string PathFor(string destPath) { return destPath + ".parts"; }

            private static string Fingerprint(string url, long total, int threads)
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(StableKey(url)));
                    return Version + "|" + BitConverter.ToString(hash).Replace("-", string.Empty)
                           + "|" + total + "|" + threads;
                }
            }

            /// <summary>
            /// 只取 URL 的主机名与路径来做指纹，丢掉查询串。
            /// 微软给的直链带时效签名（P1 过期时间、P4 签名），每次换取都不一样；
            /// 如果把整条 URL 算进指纹，重启后指纹必然对不上，断点续传就永远失效、
            /// 每次都要从头重下 700MB。路径里的文件 GUID 才是真正标识这个包的东西。
            /// 协议（http/https）同样忽略，因为连接方式可能在重试时改变。
            /// </summary>
            private static string StableKey(string url)
            {
                if (string.IsNullOrEmpty(url)) return string.Empty;
                try
                {
                    Uri uri = new Uri(url);
                    return uri.Host.TrimEnd('.').ToLowerInvariant() + uri.AbsolutePath;
                }
                catch
                {
                    int q = url.IndexOf('?');
                    return q > 0 ? url.Substring(0, q) : url;
                }
            }

            public static bool Matches(string destPath, string url, long total, int threads)
            {
                try
                {
                    string path = PathFor(destPath);
                    if (!File.Exists(path)) return !AnyPartExists(destPath);
                    return File.ReadAllText(path).Trim() == Fingerprint(url, total, threads);
                }
                catch
                {
                    return false;
                }
            }

            public static void Write(string destPath, string url, long total, int threads)
            {
                try { File.WriteAllText(PathFor(destPath), Fingerprint(url, total, threads)); }
                catch { }
            }

            public static bool AnyPartExists(string destPath)
            {
                try
                {
                    string dir = Path.GetDirectoryName(destPath);
                    if (string.IsNullOrEmpty(dir)) dir = ".";
                    return Directory.GetFiles(dir, Path.GetFileName(destPath) + ".part*").Length > 0;
                }
                catch
                {
                    return false;
                }
            }

            public static void DiscardAll(string destPath)
            {
                try
                {
                    string dir = Path.GetDirectoryName(destPath);
                    if (string.IsNullOrEmpty(dir)) dir = ".";
                    foreach (string file in Directory.GetFiles(dir, Path.GetFileName(destPath) + ".part*"))
                        TryDelete(file);
                }
                catch { }
                Delete(destPath);
            }

            public static void Delete(string destPath)
            {
                TryDelete(PathFor(destPath));
            }
        }

        private void ReportProgress(bool force)
        {
            int tick = Environment.TickCount;
            if (!force)
            {
                int last = _lastReportTick;
                if (unchecked(tick - last) < 200) return;
                if (Interlocked.CompareExchange(ref _lastReportTick, tick, last) != last) return;
            }
            else
            {
                _lastReportTick = tick;
            }

            Progress p = new Progress();
            p.Downloaded = Interlocked.Read(ref _downloaded);
            p.Total = _total;
            p.ActiveThreads = _activeThreads;
            p.Resumed = _resumed;

            double seconds = (DateTime.UtcNow - _startedAt).TotalSeconds;
            long thisRun = p.Downloaded - _baselineBytes;
            p.SpeedBytesPerSec = seconds > 0.3 && thisRun > 0 ? thisRun / seconds : 0;

            try { _onProgress(p); }
            catch { }
        }
    }
}
