using System;
using System.Net;
using System.Text;
using System.Threading;

namespace Blues19.CodexInstaller
{
    public sealed class InstallerReleaseInfo
    {
        public string TagName;
        public Version Version;
        public string ReleaseUrl;
    }

    /// <summary>
    /// 只检查这个安装器自身的 GitHub Release。Codex 的版本检查仍由 StoreApi/Fe3Client 负责，
    /// 两条更新链路互不影响。
    /// </summary>
    public static class InstallerUpdateChecker
    {
        public const string RepositoryUrl =
            "https://github.com/ivan51769/blues19-codex-native-installer";

        private const string LatestReleaseApi =
            "https://api.github.com/repos/ivan51769/blues19-codex-native-installer/releases/latest";

        public static InstallerReleaseInfo Check(CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            HttpWebRequest req = Http.CreateGet(LatestReleaseApi);
            req.Accept = "application/vnd.github+json";
            req.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            // 这是启动时的辅助检查，不值得让主界面为它等满默认的 30 秒。
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;

            using (HttpWebResponse res = Http.GetResponse(req))
            {
                string body = Http.ReadAll(res);
                int code = (int)res.StatusCode;
                if (code < 200 || code >= 300)
                    throw new HttpStatusException(code, "GitHub Release API 返回 HTTP " + code + "。");
                return ParseLatestRelease(body);
            }
        }

        public static InstallerReleaseInfo ParseLatestRelease(string json)
        {
            string tag = ReadJsonString(json, "tag_name");
            string url = ReadJsonString(json, "html_url");
            Version version = ParseVersionTag(tag);

            if (version == null)
                throw new FormatException("GitHub 最新 Release 的版本号无法识别：" + (tag ?? "(空)"));
            if (!IsTrustedReleaseUrl(url))
                throw new FormatException("GitHub 最新 Release 返回了不受信任的页面地址。");

            InstallerReleaseInfo info = new InstallerReleaseInfo();
            info.TagName = tag;
            info.Version = version;
            info.ReleaseUrl = url;
            return info;
        }

        public static bool IsNewer(Version latest, string currentVersion)
        {
            Version current = ParseVersionTag(currentVersion);
            if (latest == null || current == null)
                throw new FormatException("安装器版本号无法比较。");
            return latest.CompareTo(current) > 0;
        }

        public static Version ParseVersionTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;

            string value = tag.Trim();
            if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V'))
                value = value.Substring(1);

            int suffix = value.IndexOfAny(new char[] { '-', '+' });
            if (suffix >= 0) value = value.Substring(0, suffix);

            string[] parts = value.Split('.');
            if (parts.Length < 1 || parts.Length > 4) return null;

            int[] numbers = new int[4];
            for (int i = 0; i < parts.Length; i++)
            {
                int number;
                if (!int.TryParse(parts[i], out number) || number < 0) return null;
                numbers[i] = number;
            }

            if (parts.Length == 1) return new Version(numbers[0], 0, 0);
            if (parts.Length == 2) return new Version(numbers[0], numbers[1], 0);
            if (parts.Length == 3) return new Version(numbers[0], numbers[1], numbers[2]);
            return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        }

        public static bool IsTrustedReleaseUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
            return uri.AbsolutePath.StartsWith(
                "/ivan51769/blues19-codex-native-installer/releases/",
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 为两个字段写一个小而完整的 JSON 字符串读取器，避免为了启动检查引入额外程序集。
        /// 支持 GitHub JSON 可能出现的常用转义和 \uXXXX。
        /// </summary>
        private static string ReadJsonString(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) throw new FormatException("GitHub 返回了空响应。");

            string token = "\"" + name + "\"";
            int start = json.IndexOf(token, StringComparison.Ordinal);
            if (start < 0) throw new FormatException("GitHub 响应缺少字段：" + name);

            int colon = json.IndexOf(':', start + token.Length);
            if (colon < 0) throw new FormatException("GitHub 响应字段格式不正确：" + name);
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"')
                throw new FormatException("GitHub 响应字段不是字符串：" + name);

            i++;
            StringBuilder value = new StringBuilder();
            while (i < json.Length)
            {
                char ch = json[i++];
                if (ch == '"') return value.ToString();
                if (ch != '\\')
                {
                    value.Append(ch);
                    continue;
                }

                if (i >= json.Length) break;
                char escaped = json[i++];
                switch (escaped)
                {
                    case '"': value.Append('"'); break;
                    case '\\': value.Append('\\'); break;
                    case '/': value.Append('/'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'u':
                        if (i + 4 > json.Length)
                            throw new FormatException("GitHub 响应包含不完整的 Unicode 转义。");
                        int code;
                        if (!int.TryParse(json.Substring(i, 4),
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out code))
                            throw new FormatException("GitHub 响应包含无效的 Unicode 转义。");
                        value.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw new FormatException("GitHub 响应包含无法识别的 JSON 转义。");
                }
            }

            throw new FormatException("GitHub 响应字段没有正确结束：" + name);
        }
    }
}
