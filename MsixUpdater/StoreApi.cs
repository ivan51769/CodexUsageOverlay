using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace Blues19.CodexInstaller
{
    /// <summary>
    /// 微软商店侧的查询入口：先从 DisplayCatalog 拿到 Windows Update 分类 ID，
    /// 再让 Fe3Client 用这个 ID 去官方分发服务器换取真正的下载直链。
    /// </summary>
    public static class StoreApi
    {
        private const string DisplayCatalogUrl =
            "https://displaycatalog.mp.microsoft.com/v7.0/products/{0}?market={1}&languages=zh-cn,en-us&fieldsTemplate=Details";

        /// <summary>
        /// 查最新可用安装包。注意下载直链是带时效签名的，所以只在选定之后才去换取，
        /// 既省掉几次无用请求，也避免链接在挑选期间过期。
        /// </summary>
        public static PackageInfo FindLatestPackage(Logger log, CancellationToken cancel)
        {
            string categoryId = ResolveCategoryId(log, cancel);

            log.Step("正在向微软分发服务器查询安装包清单…");
            List<PackageInfo> candidates = Fe3Client.QueryPackages(categoryId, log, cancel);

            if (candidates.Count == 0)
                throw new InvalidOperationException("微软服务器没有返回任何 Codex 安装包，请稍后重试。");

            List<PackageInfo> usable = FilterByArchitecture(candidates, log);
            if (usable.Count == 0)
                throw new InvalidOperationException("没有找到适配本机架构（" + LocalArchitecture() + "）的 Codex 安装包。");

            foreach (PackageInfo p in usable)
                log.Info("  候选：" + p.FileName + "（" + p.SizeText + "，" + (p.Architecture ?? "未知架构") + "）");

            PackageInfo best = PickBest(usable, log);
            if (best == null)
                throw new InvalidOperationException("没有找到适配本机架构的 Codex 安装包。");

            return best;
        }

        /// <summary>
        /// 只保留本机能装的架构。非 ARM 机器上直接把 arm64 包剔除，
        /// 免得它进入候选列表、更别说去换取它的下载直链。
        /// </summary>
        internal static List<PackageInfo> FilterByArchitecture(List<PackageInfo> candidates, Logger log)
        {
            string local = LocalArchitecture();
            bool isArm = local == "arm64";

            List<PackageInfo> usable = new List<PackageInfo>();
            List<string> dropped = new List<string>();

            foreach (PackageInfo p in candidates)
            {
                string arch = string.IsNullOrEmpty(p.Architecture) ? string.Empty : p.Architecture.ToLowerInvariant();

                // 注意：bundle 不能无条件放行。bundle 的包名里同样带架构标识，
                // 一个 arm64 的 bundle 在 x64 机器上照样装不上。
                // 只有真正没有架构信息或标为 neutral 的包才是「哪都能装」。
                bool ok;
                if (arch.Length == 0 || arch == "neutral") ok = true;
                else if (arch == local) ok = true;
                else if (isArm && (arch == "x64" || arch == "x86")) ok = true;   // ARM64 可以模拟运行 x64/x86
                else if (local == "x64" && arch == "x86") ok = true;            // x64 可以装 x86 包
                else ok = false;

                if (ok) usable.Add(p);
                else dropped.Add(p.FileName + "（" + arch + "）");
            }

            if (dropped.Count > 0 && log != null)
                log.Info("已排除与本机架构（" + local + "）不符的安装包：" + string.Join("、", dropped.ToArray()));

            return usable;
        }

        internal static string LocalArchitecture()
        {
            if (IsArm64()) return "arm64";
            return Environment.Is64BitOperatingSystem ? "x64" : "x86";
        }

        /// <summary>换取下载直链。链接有时效，下载前临门一脚再取。</summary>
        public static void PrepareDownload(PackageInfo pkg, Logger log, CancellationToken cancel)
        {
            log.Step("正在获取下载直链…");
            Fe3Client.ResolveDownloadUrl(pkg, log, cancel);
            log.Info("已取得直链：" + ShortenUrl(pkg.Url));
        }

        private static string ShortenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "(空)";
            int q = url.IndexOf('?');
            return q > 0 ? url.Substring(0, q) + "?…" : url;
        }

        private static string ResolveCategoryId(Logger log, CancellationToken cancel)
        {
            string[] markets = new string[] { "US", "CN" };
            foreach (string market in markets)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    string url = string.Format(DisplayCatalogUrl, CodexProduct.StoreProductId, market);
                    string json = Http.GetString(url, 2, cancel);
                    Match m = Regex.Match(json, "\"WuCategoryId\"\\s*:\\s*\"([0-9a-fA-F-]{36})\"");
                    if (m.Success)
                    {
                        string id = m.Groups[1].Value;
                        log.Info("已从商店目录取得分类 ID：" + id);
                        return id;
                    }
                    log.Warn("商店目录响应中没有 WuCategoryId（market=" + market + "）。");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.Warn("查询商店目录失败（market=" + market + "）：" + ex.Message);
                }
            }

            log.Warn("改用内置的分类 ID 继续：" + CodexProduct.FallbackWuCategoryId);
            return CodexProduct.FallbackWuCategoryId;
        }

        /// <summary>
        /// 从（已按架构过滤过的）候选里挑最合适的一个：版本最新优先。
        /// 架构过滤统一由 FilterByArchitecture 负责，这里不再重复一套规则——
        /// 两处规则分别演化的话迟早会不一致。
        /// </summary>
        internal static PackageInfo PickBest(List<PackageInfo> candidates, Logger log)
        {
            List<PackageInfo> usable = new List<PackageInfo>(candidates);

            usable.Sort(delegate (PackageInfo a, PackageInfo b)
            {
                // 版本优先。bundle 只在同版本时用作加分项——
                // 反过来（bundle 优先于版本）会让一个老版本的 bundle 压过最新的单架构包。
                int byVersion = CompareVersion(b.Version, a.Version);
                if (byVersion != 0) return byVersion;

                // 同版本时 bundle 更稳：它自带全部架构
                int byBundle = b.IsBundle.CompareTo(a.IsBundle);
                if (byBundle != 0) return byBundle;

                // 同版本同类型时，架构完全匹配的排前面
                string local = LocalArchitecture();
                bool aExact = string.Equals(a.Architecture, local, StringComparison.OrdinalIgnoreCase);
                bool bExact = string.Equals(b.Architecture, local, StringComparison.OrdinalIgnoreCase);
                if (aExact != bExact) return bExact.CompareTo(aExact);

                return b.SizeBytes.CompareTo(a.SizeBytes);
            });

            if (log != null && usable.Count > 1)
            {
                log.Info("共 " + usable.Count + " 个候选包，已选择：" + usable[0].FileName);
            }
            return usable.Count > 0 ? usable[0] : null;
        }

        private static int CompareVersion(Version a, Version b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            return a.CompareTo(b);
        }

        private static bool IsArm64()
        {
            try
            {
                string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
                string archW = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
                string combined = ((arch ?? "") + " " + (archW ?? "")).ToUpperInvariant();
                return combined.IndexOf("ARM", StringComparison.Ordinal) >= 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
