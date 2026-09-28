using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

        private const long MaxInstallerBytes = 128 * 1024 * 1024;

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

        private static HttpWebResponse GetResponse(string url, bool assetDownload)
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
                request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                var response = (HttpWebResponse)request.GetResponse();
                if (response.StatusCode == HttpStatusCode.OK) return response;
                int code = (int)response.StatusCode;
                string location = response.Headers[HttpResponseHeader.Location];
                response.Dispose();
                Uri redirected;
                if (!assetDownload || (code != 301 && code != 302 && code != 303 && code != 307 && code != 308) ||
                    !Uri.TryCreate(next, location, out redirected) || !IsTrustedRedirect(redirected))
                    throw new WebException("更新下载地址不受信任或不可用。");
                next = redirected;
            }
            throw new WebException("安装包下载跳转次数过多。");
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
                    if (DateTime.UtcNow > deadline) throw new IOException("下载超时，请重试。");
                    if (total == 0) first = buffer[0];
                    if (total == 0 && count > 1) second = buffer[1];
                    else if (total == 1) second = buffer[0];
                    total += count;
                    if (total > asset.Size || total > MaxInstallerBytes)
                        throw new InvalidDataException("安装包大小与发布信息不一致。");
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                    output.Write(buffer, 0, count);
                    int percent = (int)(total * 100 / asset.Size);
                    if (percent != previous && progress != null) progress(percent);
                    previous = percent;
                }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                string actual = BitConverter.ToString(hash.Hash).Replace("-", "");
                if (total != asset.Size || first != 'M' || second != 'Z' ||
                    !String.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包校验失败，未运行安装程序；请重试。");
            }
        }

        internal static string Download(string releaseUrl, Action<int> progress)
        {
            var release = GitHubReleaseUpdateService.EvaluateReleaseUrl(releaseUrl);
            if (release == null || !release.UpdateAvailable)
                throw new InvalidDataException("没有可安装的新版本。");
            string tag = Uri.UnescapeDataString(new Uri(releaseUrl).Segments[5]);
            string json;
            using (var response = GetResponse("https://api.github.com/repos/ivan51769/CodexUsageOverlay/releases/tags/" +
                Uri.EscapeDataString(tag), false))
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                char[] text = new char[2 * 1024 * 1024 + 1];
                int count = reader.ReadBlock(text, 0, text.Length);
                if (count == text.Length) throw new InvalidDataException("更新信息过大。");
                json = new string(text, 0, count);
            }
            Asset asset = ParseAsset(releaseUrl, json);
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexUsageOverlay", "updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, asset.Name);
            string part = path + ".part";
            try
            {
                using (var response = GetResponse(asset.Url, true))
                using (Stream input = response.GetResponseStream())
                using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    CopyVerified(input, output, asset, progress);
                File.Move(part, path);
                return path;
            }
            finally
            {
                if (File.Exists(part)) File.Delete(part);
            }
        }

        internal static string InstallArguments(string directory)
        {
            return "/SILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTOVERLAY=1 /DIR=\"" +
                Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + "\"";
        }
    }
}
