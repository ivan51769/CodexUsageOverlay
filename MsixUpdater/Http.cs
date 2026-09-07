using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Blues19.CodexInstaller
{
    /// <summary>
    /// 统一的 HTTP 出口：集中处理 TLS、代理、超时、UA 与非 2xx 响应，
    /// 让上层拿到 HttpWebResponse 后自己判断状态码，而不是被 WebException 打断。
    /// </summary>
    public static class Http
    {
        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        private const SecurityProtocolType Ssl3 = (SecurityProtocolType)48;
        private const SecurityProtocolType Tls10 = (SecurityProtocolType)192;
        private const SecurityProtocolType Tls11 = (SecurityProtocolType)768;
        private const SecurityProtocolType Tls12 = (SecurityProtocolType)3072;

        static Http()
        {
            // 配合 AssemblyInfo.cs 里的 TargetFrameworkAttribute，默认值应当是 SystemDefault(0)，
            // 也就是把协议选择交给操作系统——这样 Win11 上能协商到 TLS 1.3，老系统也不会挑到不支持的版本。
            // 只有当默认值被降级成了含 SSL3 的老组合时才动手纠正，并且不硬写 TLS 1.3
            //（老系统上给 SecurityProtocol 赋一个未定义的值会抛 NotSupportedException）。
            try
            {
                SecurityProtocolType current = ServicePointManager.SecurityProtocol;
                bool legacy = current == 0 ? false : (current & Tls12) == 0 || (current & Ssl3) != 0;
                if (legacy)
                {
                    try
                    {
                        ServicePointManager.SecurityProtocol = Tls12 | Tls11 | Tls10;
                    }
                    catch (NotSupportedException)
                    {
                        try { ServicePointManager.SecurityProtocol = Tls12; }
                        catch { }
                    }
                }
            }
            catch { }

            ServicePointManager.DefaultConnectionLimit = 64;
            ServicePointManager.Expect100Continue = false;
            try { ServicePointManager.UseNagleAlgorithm = false; } catch { }
        }

        /// <summary>诊断用：把当前生效的 TLS 设置读出来写进日志。</summary>
        public static string ProtocolSummary()
        {
            try
            {
                SecurityProtocolType p = ServicePointManager.SecurityProtocol;
                return p == 0 ? "SystemDefault（由系统协商）" : p.ToString();
            }
            catch
            {
                return "未知";
            }
        }

        /// <summary>触发静态构造，确保 TLS 设置在任何请求之前生效。</summary>
        public static void Init() { }

        private static void ApplyCommon(HttpWebRequest req)
        {
            req.UserAgent = UserAgent;
            req.KeepAlive = true;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 10;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            try
            {
                // 配置里指定了代理就用指定的，否则走系统代理并带上默认凭据（兼容公司内网）
                IWebProxy proxy = Settings.Current.BuildProxy();
                if (proxy == null) proxy = WebRequest.DefaultWebProxy;
                if (proxy != null)
                {
                    try { proxy.Credentials = CredentialCache.DefaultCredentials; }
                    catch { }
                    req.Proxy = proxy;
                }
            }
            catch { }
        }

        public static HttpWebRequest CreateGet(string url)
        {
            return CreateGet(url, null);
        }

        /// <summary>
        /// hostOverride 用于改写 Host 请求头。存在的意义：有些网络设备是按 Host 头
        /// 精确匹配来拦截微软下载 CDN 的，给域名加一个末尾点（合法的绝对域名写法）
        /// 就能绕过这种字符串匹配，而 CDN 侧照常路由。
        /// </summary>
        public static HttpWebRequest CreateGet(string url, string hostOverride)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            ApplyCommon(req);
            if (!string.IsNullOrEmpty(hostOverride))
            {
                try { req.Host = hostOverride; }
                catch { }
            }
            return req;
        }

        public static HttpWebRequest CreatePost(string url, string contentType)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = contentType;
            ApplyCommon(req);
            return req;
        }

        /// <summary>
        /// 发起请求并返回响应；4xx/5xx 不抛异常，而是把响应交回调用方判断。
        /// </summary>
        public static HttpWebResponse GetResponse(HttpWebRequest req)
        {
            try
            {
                return (HttpWebResponse)req.GetResponse();
            }
            catch (WebException ex)
            {
                HttpWebResponse res = ex.Response as HttpWebResponse;
                if (res != null) return res;
                throw;
            }
        }

        public static string ReadAll(HttpWebResponse res)
        {
            using (Stream s = res.GetResponseStream())
            {
                if (s == null) return string.Empty;
                Encoding enc = Encoding.UTF8;
                try
                {
                    if (!string.IsNullOrEmpty(res.CharacterSet))
                        enc = Encoding.GetEncoding(res.CharacterSet);
                }
                catch { }
                using (StreamReader reader = new StreamReader(s, enc))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        /// <summary>GET 文本，带重试。失败时抛出携带状态码的异常。</summary>
        public static string GetString(string url, int retries, CancellationToken cancel)
        {
            return WithRetry(retries, cancel, delegate
            {
                HttpWebRequest req = CreateGet(url);
                req.Accept = "application/json, text/plain, */*";
                using (HttpWebResponse res = GetResponse(req))
                {
                    string body = ReadAll(res);
                    EnsureSuccess(res, body, url);
                    return body;
                }
            });
        }

        /// <summary>POST 文本，带重试。userAgent 为空时用默认浏览器 UA。</summary>
        public static string PostString(string url, string contentType, string payload, int retries,
            CancellationToken cancel, string userAgent)
        {
            return WithRetry(retries, cancel, delegate
            {
                HttpWebRequest req = CreatePost(url, contentType);
                if (!string.IsNullOrEmpty(userAgent)) req.UserAgent = userAgent;
                byte[] data = new UTF8Encoding(false).GetBytes(payload);
                req.ContentLength = data.Length;
                using (Stream reqStream = req.GetRequestStream())
                {
                    reqStream.Write(data, 0, data.Length);
                }
                using (HttpWebResponse res = GetResponse(req))
                {
                    string body = ReadAll(res);
                    EnsureSuccess(res, body, url);
                    return body;
                }
            });
        }

        private static void EnsureSuccess(HttpWebResponse res, string body, string url)
        {
            int code = (int)res.StatusCode;
            if (code >= 200 && code < 300) return;

            string snippet = body ?? string.Empty;
            if (snippet.Length > 400) snippet = snippet.Substring(0, 400) + " ...";
            throw new HttpStatusException(code, string.Format("请求 {0} 返回 HTTP {1}。{2}",
                Shorten(url), code, snippet));
        }

        private static string Shorten(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length <= 80) return url;
            return url.Substring(0, 77) + "...";
        }

        private static string WithRetry(int retries, CancellationToken cancel, Func<string> action)
        {
            Exception last = null;
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    return action();
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpStatusException ex)
                {
                    // 4xx（除 408/429）重试没有意义，直接失败
                    if (ex.StatusCode < 500 && ex.StatusCode != 408 && ex.StatusCode != 429) throw;
                    last = ex;
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                if (attempt < retries)
                {
                    int delay = Math.Min(6000, 600 * (1 << Math.Min(attempt, 3)));
                    if (cancel.WaitHandle.WaitOne(delay)) cancel.ThrowIfCancellationRequested();
                }
            }
            throw last ?? new IOException("请求失败");
        }
    }

    public sealed class HttpStatusException : Exception
    {
        public readonly int StatusCode;
        public HttpStatusException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
