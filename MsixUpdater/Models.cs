using System;
using System.IO;

namespace Blues19.CodexInstaller
{
    /// <summary>Codex 在 Microsoft Store 上的标识，集中放这里方便日后改产品。</summary>
    public static class CodexProduct
    {
        public const string StoreProductId = "9PLM9XGG6VKS";
        public const string PackageName = "OpenAI.Codex";
        public const string PublisherId = "2p2nqsd0c76g0";
        public const string PackageFamilyName = PackageName + "_" + PublisherId;

        /// <summary>接口不可用时兜底用的分类 ID（来自 DisplayCatalog 的 WuCategoryId）。</summary>
        public const string FallbackWuCategoryId = "fdf7dba1-a7bc-4592-ad8e-04aa3b974675";

        public const string StorePageUrl =
            "https://apps.microsoft.com/detail/9plm9xgg6vks";
    }

    /// <summary>一个可下载的安装包候选。</summary>
    public sealed class PackageInfo
    {
        /// <summary>落盘用的友好名，形如 OpenAI.Codex_26.721.4979.0_x64__2p2nqsd0c76g0.msix</summary>
        public string FileName;
        /// <summary>服务端的真实文件名（GUID.msix），只用于按摘要认领下载链接</summary>
        public string ServerFileName;
        public string Url;
        public long SizeBytes;
        public Version Version;
        public string Architecture;
        /// <summary>清单里登记的内容摘要，微软目前给的是 SHA1（Base64）</summary>
        public string DigestBase64;
        public string DigestAlgorithm;
        public string UpdateId;
        public string RevisionNumber;
        public bool IsBundle;

        public string VersionText
        {
            get { return Version != null ? Version.ToString() : "未知"; }
        }

        public string SizeText
        {
            get { return SizeBytes > 0 ? Fmt.Size(SizeBytes) : "未知"; }
        }

        public string LocalPath(string baseDir)
        {
            return Path.Combine(baseDir, Fmt.SafeFileName(FileName));
        }

        public override string ToString()
        {
            return FileName + " (" + SizeText + ")";
        }
    }
}
