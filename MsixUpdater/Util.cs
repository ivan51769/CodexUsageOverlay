using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Blues19.CodexInstaller
{
    public static class Fmt
    {
        public static string Size(long bytes)
        {
            if (bytes < 0) return "-";
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.00", CultureInfo.InvariantCulture) + " MB";
            return (mb / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        public static string Speed(double bytesPerSec)
        {
            if (bytesPerSec <= 0) return "-";
            return Size((long)bytesPerSec) + "/s";
        }

        public static string Duration(TimeSpan span)
        {
            if (span.TotalSeconds < 1) return "不到 1 秒";
            if (span.TotalMinutes < 1) return ((int)span.TotalSeconds) + " 秒";
            if (span.TotalHours < 1) return ((int)span.TotalMinutes) + " 分 " + span.Seconds + " 秒";
            return ((int)span.TotalHours) + " 小时 " + span.Minutes + " 分";
        }

        public static string Eta(long remaining, double bytesPerSec)
        {
            if (bytesPerSec <= 1 || remaining <= 0) return "-";
            return Duration(TimeSpan.FromSeconds(remaining / bytesPerSec));
        }

        public static string Stamp()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        public static string FileStamp()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        /// <summary>把可能来自网络的名字压成安全的 Windows 文件名。</summary>
        public static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "codex_setup.msixbundle";
            StringBuilder sb = new StringBuilder(name.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name)
            {
                bool bad = c < 32;
                if (!bad)
                {
                    foreach (char inv in invalid) { if (c == inv) { bad = true; break; } }
                }
                sb.Append(bad ? '_' : c);
            }
            string result = sb.ToString().Trim().TrimEnd('.');
            if (result.Length == 0) return "codex_setup.msixbundle";
            if (result.Length > 180) result = result.Substring(0, 180);
            return result;
        }
    }

    /// <summary>
    /// 决定运行产物落在哪里。优先放在 exe 同目录（便携、好找），
    /// 目录不可写时（Program Files、只读介质、UNC）自动回落到 LocalAppData。
    /// </summary>
    public static class Paths
    {
        private static string _baseDir;
        private static readonly object Lock = new object();

        public static string ExeDir
        {
            get
            {
                string p = Assembly.GetExecutingAssembly().Location;
                string dir = string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
                return string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir;
            }
        }

        public static string BaseDir
        {
            get
            {
                if (_baseDir != null) return _baseDir;
                lock (Lock)
                {
                    if (_baseDir != null) return _baseDir;
                    string exeDir = ExeDir;
                    _baseDir = IsWritable(exeDir) ? exeDir : FallbackDir();
                    return _baseDir;
                }
            }
        }

        public static bool UsingFallbackDir
        {
            get { return !string.Equals(BaseDir, ExeDir, StringComparison.OrdinalIgnoreCase); }
        }

        private static string FallbackDir()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
            string dir = Path.Combine(Path.Combine(root, "Blues19"), "CodexInstaller");
            try { Directory.CreateDirectory(dir); }
            catch { dir = Path.GetTempPath(); }
            return dir;
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                string probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (FileStream fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64, FileOptions.DeleteOnClose))
                {
                    fs.WriteByte(1);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string LogFile { get { return Path.Combine(BaseDir, "install.log"); } }
        public static string StatusFile { get { return Path.Combine(BaseDir, "status.json"); } }
        public static string LogsDir { get { return Path.Combine(BaseDir, "logs"); } }

        // Large MSIX packages belong where the user can immediately find them,
        // not beside Overlay's installed executable. Logs and settings continue
        // to use BaseDir so the desktop stays tidy.
        public static string PackageDir
        {
            get
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) return desktop;
                return BaseDir;
            }
        }
    }

    /// <summary>手写的极简 JSON 序列化，避免为了写状态文件去引 System.Web.Extensions。</summary>
    public static class Json
    {
        public static string Escape(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
