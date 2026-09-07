using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Blues19.CodexInstaller
{
    public enum ProxyMode { System, Auto, Manual, None }

    /// <summary>
    /// 一个极简的 key=value 配置文件。
    /// 之所以需要它：微软的下载 CDN（dl.delivery.mp.microsoft.com）在部分网络环境下
    /// 连不上，而查询用的 API 域名却是通的——这时用户需要一个地方把代理填进去。
    /// </summary>
    public sealed class Settings
    {
        // 常见本地代理端口，按流行度排序，用于“自动探测”
        private static readonly int[] CommonProxyPorts =
            { 7897, 7890, 10809, 10808, 1080, 8889, 8080, 2080, 33210 };

        public ProxyMode Mode = ProxyMode.System;
        public string ManualProxy = string.Empty;
        public int Threads = Downloader.DefaultThreads;

        private static readonly object Lock = new object();
        private static Settings _current;

        public static Settings Current
        {
            get
            {
                if (_current != null) return _current;
                lock (Lock)
                {
                    if (_current == null) _current = Load();
                    return _current;
                }
            }
        }

        private static string FilePath { get { return Path.Combine(Paths.BaseDir, "settings.ini"); } }

        private static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();

                    if (key == "proxymode")
                    {
                        switch (value.ToLowerInvariant())
                        {
                            case "auto": s.Mode = ProxyMode.Auto; break;
                            case "manual": s.Mode = ProxyMode.Manual; break;
                            case "none": s.Mode = ProxyMode.None; break;
                            default: s.Mode = ProxyMode.System; break;
                        }
                    }
                    else if (key == "proxy") s.ManualProxy = value;
                    else if (key == "threads")
                    {
                        int n;
                        if (int.TryParse(value, out n) && n >= 1 && n <= 16) s.Threads = n;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            lock (Lock)
            {
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("# Codex 原生安装器配置");
                    sb.AppendLine("# proxymode: system(跟随系统) | auto(自动探测本地代理) | manual(手动指定) | none(直连)");
                    sb.AppendLine("proxymode=" + Mode.ToString().ToLowerInvariant());
                    sb.AppendLine("proxy=" + (ManualProxy ?? string.Empty));
                    sb.AppendLine("threads=" + Threads);
                    File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(true));
                }
                catch { }
                _current = this;
            }
        }

        /// <summary>把当前配置翻译成 .NET 能用的代理对象；null 表示走系统默认。</summary>
        public IWebProxy BuildProxy()
        {
            switch (Mode)
            {
                case ProxyMode.None:
                    return new WebProxy();          // 空代理 = 强制直连
                case ProxyMode.Manual:
                    return MakeProxy(ManualProxy);
                case ProxyMode.Auto:
                    string found = DetectLocalProxy();
                    return found != null ? MakeProxy(found) : null;
                default:
                    return null;
            }
        }

        public string Describe()
        {
            switch (Mode)
            {
                case ProxyMode.None: return "强制直连（不使用代理）";
                case ProxyMode.Manual: return "手动代理 " + (string.IsNullOrEmpty(ManualProxy) ? "(未填写)" : ManualProxy);
                case ProxyMode.Auto:
                    string found = DetectLocalProxy();
                    return found != null ? "自动探测到本地代理 " + found : "自动探测未发现本地代理，按直连处理";
                default: return "跟随系统代理设置";
            }
        }

        internal static IWebProxy MakeProxy(string hostPort)
        {
            if (string.IsNullOrEmpty(hostPort)) return null;
            string value = hostPort.Trim();
            if (value.IndexOf("://", StringComparison.Ordinal) < 0) value = "http://" + value;
            try
            {
                WebProxy proxy = new WebProxy(new Uri(value));
                proxy.BypassProxyOnLocal = true;
                return proxy;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 扫一遍常见的本地代理端口。只做 TCP 连接测试，每个端口最多等 250ms，
        /// 全部扫完也就 2 秒出头，且绝大多数情况第一个就命中。
        /// </summary>
        public static string DetectLocalProxy()
        {
            foreach (int port in CommonProxyPorts)
            {
                if (IsPortOpen("127.0.0.1", port, 250)) return "127.0.0.1:" + port;
            }
            return null;
        }

        public static List<string> DetectAllLocalProxies()
        {
            List<string> found = new List<string>();
            foreach (int port in CommonProxyPorts)
            {
                if (IsPortOpen("127.0.0.1", port, 250)) found.Add("127.0.0.1:" + port);
            }
            return found;
        }

        private static bool IsPortOpen(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(host, port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(timeoutMs, false);
                    if (!ok) return false;
                    client.EndConnect(ar);
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
