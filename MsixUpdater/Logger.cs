using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Blues19.CodexInstaller
{
    public enum LogLevel { Info, Step, Warn, Error, Success }

    public sealed class LogEntry
    {
        public DateTime Time;
        public LogLevel Level;
        public string Message;
    }

    /// <summary>
    /// 日志：内存环形缓冲（供界面渲染）+ install.log 落盘 + 退出时归档到 logs\。
    /// 所有写盘失败都被吞掉——日志坏了不该让主流程崩。
    /// </summary>
    public sealed class Logger
    {
        private const int MaxLogArchives = 100;
        private const int MaxMemoryLines = 2000;

        private readonly object _lock = new object();
        private readonly List<LogEntry> _buffer = new List<LogEntry>();
        private readonly string _logPath;
        private bool _archived;

        public event Action<LogEntry> Appended;

        public Logger(bool truncate)
        {
            _logPath = Paths.LogFile;
            if (truncate)
            {
                try { File.WriteAllText(_logPath, string.Empty, new UTF8Encoding(true)); }
                catch { }
            }
        }

        public string LogPath { get { return _logPath; } }

        public void Info(string message) { Write(LogLevel.Info, message); }
        public void Step(string message) { Write(LogLevel.Step, message); }
        public void Warn(string message) { Write(LogLevel.Warn, message); }
        public void Error(string message) { Write(LogLevel.Error, message); }
        public void Success(string message) { Write(LogLevel.Success, message); }

        public void Write(LogLevel level, string message)
        {
            if (message == null) message = string.Empty;
            LogEntry entry = new LogEntry();
            entry.Time = DateTime.Now;
            entry.Level = level;
            entry.Message = message;

            lock (_lock)
            {
                _buffer.Add(entry);
                if (_buffer.Count > MaxMemoryLines) _buffer.RemoveRange(0, _buffer.Count - MaxMemoryLines);
                try
                {
                    File.AppendAllText(_logPath,
                        "[" + entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + Tag(level) + " " + message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch { }
            }

            Action<LogEntry> handler = Appended;
            if (handler != null)
            {
                try { handler(entry); }
                catch { }
            }
        }

        private static string Tag(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Step: return "STEP   ";
                case LogLevel.Warn: return "WARN   ";
                case LogLevel.Error: return "ERROR  ";
                case LogLevel.Success: return "SUCCESS";
                default: return "INFO   ";
            }
        }

        public LogEntry[] Snapshot()
        {
            lock (_lock) { return _buffer.ToArray(); }
        }

        /// <summary>退出时把本次日志完整归档，供事后排查；只归档一次。</summary>
        public void Archive(string mode, string state)
        {
            lock (_lock)
            {
                if (_archived) return;
                _archived = true;
                try
                {
                    if (!File.Exists(_logPath) || new FileInfo(_logPath).Length == 0) return;
                    Directory.CreateDirectory(Paths.LogsDir);
                    string name = "install-" + Fmt.FileStamp() + "-" + Safe(mode) + "-" + Safe(state) + ".log";
                    File.Copy(_logPath, Path.Combine(Paths.LogsDir, name), true);
                    Prune();
                }
                catch { }
            }
        }

        private static string Safe(string s)
        {
            return string.IsNullOrEmpty(s) ? "unknown" : Fmt.SafeFileName(s);
        }

        private static void Prune()
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(Paths.LogsDir);
                FileInfo[] files = dir.GetFiles("install-*.log");
                if (files.Length <= MaxLogArchives) return;
                Array.Sort(files, delegate (FileInfo a, FileInfo b)
                {
                    return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
                });
                for (int i = MaxLogArchives; i < files.Length; i++)
                {
                    try { files[i].Delete(); }
                    catch { }
                }
            }
            catch { }
        }
    }

    /// <summary>写 status.json，保持与旧版本一致的字段，方便外部脚本继续读取。</summary>
    public static class StatusFile
    {
        private static readonly object Lock = new object();

        public static void Write(string mode, string step, string state, string message,
            string packageName, string packageSize, string savePath, int percent)
        {
            lock (Lock)
            {
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("{\n");
                    sb.Append("  \"appDir\": ").Append(Json.Escape(Paths.BaseDir)).Append(",\n");
                    sb.Append("  \"mode\": ").Append(Json.Escape(mode)).Append(",\n");
                    sb.Append("  \"step\": ").Append(Json.Escape(step)).Append(",\n");
                    sb.Append("  \"state\": ").Append(Json.Escape(state)).Append(",\n");
                    sb.Append("  \"updatedAt\": ").Append(Json.Escape(Fmt.Stamp())).Append(",\n");
                    sb.Append("  \"message\": ").Append(Json.Escape(message ?? string.Empty)).Append(",\n");
                    sb.Append("  \"progress\": ").Append(percent).Append(",\n");
                    sb.Append("  \"packageName\": ").Append(Json.Escape(packageName ?? string.Empty)).Append(",\n");
                    sb.Append("  \"packageSize\": ").Append(Json.Escape(packageSize ?? string.Empty)).Append(",\n");
                    sb.Append("  \"savePath\": ").Append(Json.Escape(savePath ?? string.Empty)).Append("\n");
                    sb.Append("}\n");

                    string tmp = Paths.StatusFile + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    if (File.Exists(Paths.StatusFile)) File.Delete(Paths.StatusFile);
                    File.Move(tmp, Paths.StatusFile);
                }
                catch { }
            }
        }
    }
}
