using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace Blues19.CodexInstaller
{
    /// <summary>
    /// 微软 Windows Update「FE3」分发服务的最小客户端。
    ///
    /// 这条链路正是 store.rg-adguard.net 在服务端做的事情，我们自己走一遍就能：
    /// 不开浏览器、不过 Cloudflare 人机验证、不依赖任何第三方站点，
    /// 直接从微软官方服务器拿到 .msix 的下载直链。
    ///
    /// 流程：
    ///   1. GetCookie               取一个匿名访问票据
    ///   2. SyncUpdates（多轮）      按分类 ID 拉更新树；服务端先给分类/探测器这类中间节点，
    ///                              需要把已见到的节点回传成 InstalledNonLeafUpdateIDs 再问一次，
    ///                              一层层剥到最后才会吐出真正的安装包（叶子节点）
    ///   3. GetExtendedUpdateInfo2  在 /secured 端点上用叶子的 UpdateID 换取带签名的下载 URL
    /// </summary>
    public static class Fe3Client
    {
        private const string Fe3Url = "https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx";
        private const string Fe3Secured = Fe3Url + "/secured";
        private const string Fe3Fallback = "https://fe3cr.delivery.mp.microsoft.com/ClientWebService/client.asmx";
        private const string SoapContentType = "application/soap+xml; charset=utf-8";
        private const string WuUserAgent = "Windows-Update-Agent/10.0.10011.16384 Client-Protocol/2.50";
        private const int MaxSyncRounds = 12;

        // 设备属性串照抄 Windows Update 客户端的形态。服务端用它做适用性判断，
        // 缺字段会直接少返回甚至不返回结果，所以这里保持完整。
        private const string DeviceAttributes =
            "E:BranchReadinessLevel=CB&amp;CurrentBranch=rs4_release&amp;OSArchitecture=AMD64" +
            "&amp;FlightRing=Retail&amp;AttrDataVer=57&amp;InstallLanguage=en-US&amp;OSUILocale=en-US" +
            "&amp;InstallationType=Client&amp;FlightingBranchName=&amp;Version_RS5=10&amp;UpgEx_RS5=Green" +
            "&amp;GStatus_RS5=2&amp;CTAC_RS5=Not%20Found&amp;IsDeviceRetailDemo=0&amp;IsFlightingEnabled=0" +
            "&amp;DefaultUserRegion=244&amp;WuClientVer=1023&amp;DataVer_RS5=1942";

        private const string CallerAttributes =
            "E:Interactive=1&amp;IsSeeker=0&amp;Acquisition=1&amp;SheddingAware=1" +
            "&amp;Id=Acquisition%3BMicrosoft.WindowsStore_8wekyb3d8bbwe";

        // ---------------- 对外接口 ----------------

        /// <summary>
        /// 拉取该分类下所有可安装的包（此时还没有下载 URL，URL 要等选定后再单独换取，
        /// 免得为几个用不到的架构白跑几次请求）。
        /// </summary>
        /// <summary>查询阶段实际用上的节点，换取直链时要沿用同一个。</summary>
        private static string _endpoint = Fe3Url;

        public static List<PackageInfo> QueryPackages(string categoryId, Logger log, CancellationToken cancel)
        {
            string endpoint = Fe3Url;
            string cookie;
            try
            {
                cookie = GetCookie(endpoint, cancel);
            }
            catch (OperationCanceledException) { throw; }   // 用户取消不该触发换节点重试
            catch (Exception ex)
            {
                log.Warn("主分发节点不可用（" + ex.Message + "），改用备用节点。");
                endpoint = Fe3Fallback;
                cookie = GetCookie(endpoint, cancel);
            }
            _endpoint = endpoint;

            Dictionary<string, LeafUpdate> leaves = new Dictionary<string, LeafUpdate>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenRevisions = new HashSet<string>(StringComparer.Ordinal);

            for (int round = 1; round <= MaxSyncRounds; round++)
            {
                cancel.ThrowIfCancellationRequested();
                int before = seenRevisions.Count;

                string response = Post(endpoint, "SyncUpdates",
                    BuildSyncUpdatesBody(cookie, categoryId, seenRevisions), cancel);

                string newCookie = ExtractNewCookie(response);
                if (!string.IsNullOrEmpty(newCookie)) cookie = newCookie;

                CollectUpdates(response, seenRevisions, leaves);

                log.Info(string.Format("第 {0} 轮同步：已知节点 {1} 个，找到安装包 {2} 个。",
                    round, seenRevisions.Count, leaves.Count));

                if (seenRevisions.Count == before) break;
            }

            List<PackageInfo> packages = new List<PackageInfo>();
            foreach (LeafUpdate leaf in leaves.Values)
            {
                PackageInfo pkg = ToPackageInfo(leaf);
                if (pkg != null) packages.Add(pkg);
            }

            if (packages.Count == 0)
                log.Warn("同步完成，但没有解析出任何安装包条目。");

            return packages;
        }

        /// <summary>为选定的包换取真正的下载 URL（有时效，用完尽快下载）。</summary>
        public static void ResolveDownloadUrl(PackageInfo pkg, Logger log, CancellationToken cancel)
        {
            if (pkg == null || string.IsNullOrEmpty(pkg.UpdateId))
                throw new InvalidOperationException("安装包信息不完整，无法换取下载链接。");

            // 沿用查询阶段选定的那个节点。之前这里写死主节点，
            // 结果主节点不可用时前面好不容易切到备用节点，到换直链这一步又切回去了。
            string response = Post(_endpoint + "/secured", "GetExtendedUpdateInfo2",
                BuildExtendedInfoBody(pkg.UpdateId, pkg.RevisionNumber), cancel);

            string fallbackUrl = null;
            foreach (Match m in Regex.Matches(response, @"<FileLocation>([\s\S]*?)</FileLocation>"))
            {
                string block = m.Groups[1].Value;
                string digest = Between(block, "<FileDigest>", "</FileDigest>");
                string url = Between(block, "<Url>", "</Url>");
                if (string.IsNullOrEmpty(url)) continue;
                url = Unescape(url);

                // 一次可能回来多个文件（安装包本体 + 块映射 cab），靠摘要认领自己那个
                if (!string.IsNullOrEmpty(pkg.DigestBase64) && digest == pkg.DigestBase64)
                {
                    pkg.Url = url;
                    return;
                }
                if (fallbackUrl == null) fallbackUrl = url;
            }

            if (fallbackUrl == null)
                throw new InvalidOperationException("微软服务器没有返回可用的下载链接，请稍后重试。");

            log.Warn("未能按摘要匹配到下载链接，改用返回的第一个链接。");
            pkg.Url = fallbackUrl;
        }

        // ---------------- SOAP 组装与收发 ----------------

        private static string Post(string endpoint, string action, string body, CancellationToken cancel)
        {
            string envelope = BuildEnvelope(action, endpoint, body);
            try
            {
                return Http.PostString(endpoint, SoapContentType, envelope, 2, cancel, WuUserAgent);
            }
            catch (HttpStatusException ex)
            {
                // SOAP 错误是把详情塞在 500 响应体里的，直接把整段 XML 甩给用户没有意义
                string reason = ExtractFaultReason(ex.Message);
                if (reason != null)
                    throw new InvalidOperationException("微软分发服务拒绝了 " + action + " 请求：" + reason, ex);
                throw;
            }
        }

        private static string ExtractFaultReason(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            if (body.IndexOf("<s:Fault>", StringComparison.Ordinal) < 0) return null;

            string text = Between(body, "<s:Text xml:lang=\"en-US\">", "</s:Text>");
            if (string.IsNullOrEmpty(text)) text = Between(body, "<s:Text", "</s:Text>");
            string code = Between(body, "<s:Value>", "</s:Value>");

            StringBuilder sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(text) ? "服务端返回错误" : text.Trim());
            if (!string.IsNullOrEmpty(code)) sb.Append("（").Append(code.Trim()).Append("）");
            return sb.ToString();
        }

        private static string BuildEnvelope(string action, string to, string body)
        {
            DateTime now = DateTime.UtcNow;
            StringBuilder sb = new StringBuilder(body.Length + 2048);
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<s:Envelope xmlns:a=\"http://www.w3.org/2005/08/addressing\" xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">");
            sb.Append("<s:Header>");
            sb.Append("<a:Action s:mustUnderstand=\"1\">http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService/").Append(action).Append("</a:Action>");
            sb.Append("<a:MessageID>urn:uuid:").Append(Guid.NewGuid().ToString("D")).Append("</a:MessageID>");
            sb.Append("<a:To s:mustUnderstand=\"1\">").Append(to).Append("</a:To>");
            sb.Append("<o:Security s:mustUnderstand=\"1\" xmlns:o=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">");
            sb.Append("<Timestamp xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">");
            sb.Append("<Created>").Append(Iso(now)).Append("</Created>");
            sb.Append("<Expires>").Append(Iso(now.AddMinutes(5))).Append("</Expires>");
            sb.Append("</Timestamp>");
            // 匿名票据：免费应用不需要真的登录，给一个空的 MSA 票据占位即可
            sb.Append("<wuws:WindowsUpdateTicketsToken wsu:id=\"ClientMSA\" ");
            sb.Append("xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\" ");
            sb.Append("xmlns:wuws=\"http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization\">");
            sb.Append("<TicketType Name=\"MSA\" Version=\"1.0\" Policy=\"MBI_SSL\"></TicketType>");
            sb.Append("</wuws:WindowsUpdateTicketsToken>");
            sb.Append("</o:Security>");
            sb.Append("</s:Header>");
            sb.Append("<s:Body>").Append(body).Append("</s:Body>");
            sb.Append("</s:Envelope>");
            return sb.ToString();
        }

        private static string GetCookie(string endpoint, CancellationToken cancel)
        {
            StringBuilder body = new StringBuilder();
            body.Append("<GetCookie xmlns=\"http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService\">");
            body.Append("<oldCookie></oldCookie>");
            body.Append("<lastChange>2015-10-21T17:01:07.1472913Z</lastChange>");
            body.Append("<currentTime>").Append(Iso(DateTime.UtcNow)).Append("</currentTime>");
            body.Append("<protocolVersion>2.50</protocolVersion>");
            body.Append("</GetCookie>");

            string response = Post(endpoint, "GetCookie", body.ToString(), cancel);
            string encrypted = Between(response, "<EncryptedData>", "</EncryptedData>");
            if (string.IsNullOrEmpty(encrypted))
                throw new InvalidOperationException("分发服务没有返回访问票据。");
            return encrypted;
        }

        private static string BuildSyncUpdatesBody(string cookie, string categoryId, HashSet<string> knownRevisions)
        {
            StringBuilder ints = new StringBuilder();
            foreach (string id in knownRevisions) ints.Append("<int>").Append(id).Append("</int>");

            StringBuilder b = new StringBuilder(ints.Length + 4096);
            b.Append("<SyncUpdates xmlns=\"http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService\">");
            b.Append("<cookie><Expiration>2044-08-02T20:34:02Z</Expiration><EncryptedData>").Append(cookie).Append("</EncryptedData></cookie>");
            b.Append("<parameters>");
            b.Append("<ExpressQuery>false</ExpressQuery>");
            b.Append("<InstalledNonLeafUpdateIDs>").Append(ints).Append("</InstalledNonLeafUpdateIDs>");
            b.Append("<OtherCachedUpdateIDs></OtherCachedUpdateIDs>");
            b.Append("<SkipSoftwareSync>false</SkipSoftwareSync>");
            b.Append("<NeedTwoGroupOutOfScopeUpdates>true</NeedTwoGroupOutOfScopeUpdates>");
            b.Append("<FilterAppCategoryIds><CategoryIdentifier><Id>").Append(categoryId).Append("</Id></CategoryIdentifier></FilterAppCategoryIds>");
            b.Append("<TreatAppCategoryIdsAsInstalled>true</TreatAppCategoryIdsAsInstalled>");
            b.Append("<AlsoPerformRegularSync>false</AlsoPerformRegularSync>");
            b.Append("<ComputerSpec/>");
            b.Append("<ExtendedUpdateInfoParameters>");
            b.Append("<XmlUpdateFragmentTypes>");
            b.Append("<XmlUpdateFragmentType>Extended</XmlUpdateFragmentType>");
            b.Append("<XmlUpdateFragmentType>LocalizedProperties</XmlUpdateFragmentType>");
            b.Append("<XmlUpdateFragmentType>Eula</XmlUpdateFragmentType>");
            b.Append("</XmlUpdateFragmentTypes>");
            b.Append("<Locales><string>en-US</string><string>en</string></Locales>");
            b.Append("</ExtendedUpdateInfoParameters>");
            b.Append("<ClientPreferredLanguages></ClientPreferredLanguages>");
            b.Append("<ProductsParameters>");
            b.Append("<SyncCurrentVersionOnly>false</SyncCurrentVersionOnly>");
            b.Append("<DeviceAttributes>").Append(DeviceAttributes).Append("</DeviceAttributes>");
            b.Append("<CallerAttributes>").Append(CallerAttributes).Append("</CallerAttributes>");
            b.Append("<Products></Products>");
            b.Append("</ProductsParameters>");
            b.Append("</parameters>");
            b.Append("</SyncUpdates>");
            return b.ToString();
        }

        private static string BuildExtendedInfoBody(string updateId, string revisionNumber)
        {
            StringBuilder b = new StringBuilder(2048);
            b.Append("<GetExtendedUpdateInfo2 xmlns=\"http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService\">");
            b.Append("<updateIDs><UpdateIdentity>");
            b.Append("<UpdateID>").Append(updateId).Append("</UpdateID>");
            b.Append("<RevisionNumber>").Append(string.IsNullOrEmpty(revisionNumber) ? "1" : revisionNumber).Append("</RevisionNumber>");
            b.Append("</UpdateIdentity></updateIDs>");
            // 只请求这 5 种片段。多要几种（Extended / LocalizedProperties / Eula /
            // Verification / InstalledVersion / LiteInstalled / SecuredFragment）服务端会直接
            // 回 HTTP 500 + s:Sender「An Error Occurred」，实测过，不是偶发。
            b.Append("<infoTypes>");
            string[] types = { "FileUrl", "FileDecryption", "EsrpDecryptionInformation",
                               "PiecesHashUrl", "BlockMapUrl" };
            foreach (string t in types) b.Append("<XmlUpdateFragmentType>").Append(t).Append("</XmlUpdateFragmentType>");
            b.Append("</infoTypes>");
            b.Append("<deviceAttributes>").Append(DeviceAttributes).Append("</deviceAttributes>");
            b.Append("</GetExtendedUpdateInfo2>");
            return b.ToString();
        }

        // ---------------- 响应解析 ----------------

        private sealed class LeafUpdate
        {
            public string UpdateId;
            public string RevisionNumber;
            public string Fragment = string.Empty;   // 主片段 + 扩展片段拼在一起
        }

        private static void CollectUpdates(string response, HashSet<string> seenRevisions,
            Dictionary<string, LeafUpdate> leaves)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(response);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("分发服务返回的数据无法解析：" + ex.Message);
            }

            // 扩展片段按修订号归档，稍后并进对应的更新条目
            Dictionary<string, StringBuilder> extended = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
            foreach (XElement ext in Descendants(doc.Root, "ExtendedUpdateInfo"))
            {
                foreach (XElement update in Descendants(ext, "Update"))
                {
                    string id = ChildValue(update, "ID");
                    string xml = ChildValue(update, "Xml");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(xml)) continue;
                    if (!extended.ContainsKey(id)) extended[id] = new StringBuilder();
                    extended[id].Append(xml).Append('\n');
                }
            }

            foreach (XElement info in Descendants(doc.Root, "UpdateInfo"))
            {
                string revisionId = ChildValue(info, "ID");
                if (string.IsNullOrEmpty(revisionId)) continue;
                seenRevisions.Add(revisionId);

                string fragment = ChildValue(info, "Xml");
                if (fragment == null) fragment = string.Empty;

                bool isLeaf = string.Equals(ChildValue(info, "IsLeaf"), "true", StringComparison.OrdinalIgnoreCase);
                if (!isLeaf) continue;

                Match identity = Regex.Match(fragment,
                    "<UpdateIdentity\\s+UpdateID=\"([^\"]+)\"\\s+RevisionNumber=\"(\\d+)\"");
                if (!identity.Success) continue;

                string all = fragment;
                StringBuilder extra;
                if (extended.TryGetValue(revisionId, out extra)) all = all + "\n" + extra;

                LeafUpdate leaf = new LeafUpdate();
                leaf.UpdateId = identity.Groups[1].Value;
                leaf.RevisionNumber = identity.Groups[2].Value;
                leaf.Fragment = all;
                leaves[leaf.UpdateId] = leaf;
            }
        }

        private static PackageInfo ToPackageInfo(LeafUpdate leaf)
        {
            string all = leaf.Fragment;

            // 没有 <Files> 的叶子是分组/清单节点，本身不含可下载内容
            if (all.IndexOf("<Files>", StringComparison.Ordinal) < 0) return null;

            string extendedProps = MatchTag(all, "ExtendedProperties");
            string appxMetadata = MatchTag(all, "AppxMetadata");

            bool isFramework = string.Equals(Attr(extendedProps, "IsAppxFramework"), "true", StringComparison.OrdinalIgnoreCase);
            if (isFramework) return null;   // 运行库依赖，不是我们要的主程序

            string moniker = Attr(appxMetadata, "PackageMoniker");

            // <AppxPackageInstallData MainPackage="true" PackageFileName="xxx.msix"/> 指明哪个才是安装包本体，
            // 同一个更新里另一个文件是 Abm_*.cab 块映射元数据，下错了装不上
            string mainFileName = null;
            Match mainData = Regex.Match(all, "<AppxPackageInstallData\\b[^>]*MainPackage=\"true\"[^>]*/?>");
            if (mainData.Success) mainFileName = Attr(mainData.Value, "PackageFileName");

            foreach (Match fileMatch in Regex.Matches(all, "<File\\s[^>]*?/?>"))
            {
                string tag = fileMatch.Value;
                string fileName = Attr(tag, "FileName");
                string patching = Attr(tag, "PatchingType");

                bool isPayload = mainFileName != null
                    ? string.Equals(fileName, mainFileName, StringComparison.OrdinalIgnoreCase)
                    : !string.Equals(patching, "DynamicMetadata", StringComparison.OrdinalIgnoreCase);
                if (!isPayload) continue;

                PackageInfo pkg = new PackageInfo();
                pkg.UpdateId = leaf.UpdateId;
                pkg.RevisionNumber = leaf.RevisionNumber;
                pkg.ServerFileName = fileName;
                pkg.DigestBase64 = Attr(tag, "Digest");
                pkg.DigestAlgorithm = Attr(tag, "DigestAlgorithm");
                // AppxMetadata 的 IsAppxBundle 属性并不可信（实测 .msixbundle 也可能标成 false），
                // 直接看服务端文件名的扩展名更靠谱
                pkg.IsBundle = fileName != null &&
                    fileName.EndsWith("bundle", StringComparison.OrdinalIgnoreCase);

                long size;
                if (long.TryParse(Attr(tag, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size))
                    pkg.SizeBytes = size;

                // InstallerSpecificIdentifier 就是 PackageFullName，拿它当落盘文件名最直观
                string identifier = Attr(tag, "InstallerSpecificIdentifier");
                if (string.IsNullOrEmpty(identifier)) identifier = moniker;
                pkg.FileName = !string.IsNullOrEmpty(identifier)
                    ? identifier + (pkg.IsBundle ? ".msixbundle" : ".msix")
                    : fileName;

                InstalledPackage parsed = AppxInstaller.ParseFullName(!string.IsNullOrEmpty(identifier) ? identifier : moniker);
                if (parsed != null)
                {
                    pkg.Version = parsed.Version;
                    pkg.Architecture = parsed.Architecture;
                }

                return pkg;
            }

            return null;
        }

        // ---------------- 小工具 ----------------

        private static IEnumerable<XElement> Descendants(XElement root, string localName)
        {
            if (root == null) yield break;
            foreach (XElement e in root.Descendants())
            {
                if (e.Name.LocalName == localName) yield return e;
            }
        }

        private static string ChildValue(XElement parent, string localName)
        {
            if (parent == null) return null;
            foreach (XElement e in parent.Elements())
            {
                if (e.Name.LocalName == localName) return e.Value;
            }
            return null;
        }

        private static string ExtractNewCookie(string response)
        {
            int idx = response.IndexOf("<NewCookie>", StringComparison.Ordinal);
            if (idx < 0) return null;
            int end = response.IndexOf("</NewCookie>", idx, StringComparison.Ordinal);
            if (end < 0) return null;
            return Between(response.Substring(idx, end - idx), "<EncryptedData>", "</EncryptedData>");
        }

        private static string Between(string text, string start, string end)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = text.IndexOf(start, StringComparison.Ordinal);
            if (i < 0) return null;
            i += start.Length;
            int j = text.IndexOf(end, i, StringComparison.Ordinal);
            if (j < 0) return null;
            return text.Substring(i, j - i);
        }

        private static string MatchTag(string text, string tagName)
        {
            Match m = Regex.Match(text, "<" + tagName + "\\b[^>]*>");
            return m.Success ? m.Value : string.Empty;
        }

        private static string Attr(string tag, string name)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            Match m = Regex.Match(tag, "\\b" + name + "=\"([^\"]*)\"");
            return m.Success ? Unescape(m.Groups[1].Value) : null;
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('&') < 0) return s;
            return s.Replace("&lt;", "<").Replace("&gt;", ">")
                    .Replace("&quot;", "\"").Replace("&apos;", "'")
                    .Replace("&amp;", "&");
        }

        private static string Iso(DateTime utc)
        {
            return utc.ToString("yyyy-MM-ddTHH:mm:ss.0000000Z", CultureInfo.InvariantCulture);
        }
    }
}
