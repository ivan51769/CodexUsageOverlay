using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace CodexUsageOverlay
{
    internal enum CodexTaskState
    {
        Unknown,
        Processing,
        Completed,
        Interrupted
    }

    internal sealed class CodexTaskStatusMonitor : IDisposable
    {
        private const int TailBytes = 2 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly Dictionary<string, bool> rootRolloutCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly string sessionsRoot;
        private readonly Timer timer;
        private FileSystemWatcher watcher;
        private string candidatePath;
        private DateTime lastDiscoveryUtc = DateTime.MinValue;
        private CodexTaskState state = CodexTaskState.Unknown;
        private int refreshRunning;

        public CodexTaskStatusMonitor()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            sessionsRoot = Path.Combine(profile, ".codex", "sessions");
            StartWatcher();
            timer = new Timer(Refresh, null, 0, 1000);
        }

        public CodexTaskState Snapshot()
        {
            lock (gate)
                return state;
        }

        private void StartWatcher()
        {
            try
            {
                if (!Directory.Exists(sessionsRoot))
                    return;
                watcher = new FileSystemWatcher(sessionsRoot, "rollout-*.jsonl");
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.Changed += OnRolloutChanged;
                watcher.Created += OnRolloutChanged;
                watcher.Renamed += OnRolloutRenamed;
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                if (watcher != null)
                {
                    watcher.Dispose();
                    watcher = null;
                }
            }
        }

        private void OnRolloutChanged(object sender, FileSystemEventArgs eventArgs)
        {
            if (!IsRootRollout(eventArgs.FullPath))
                return;
            lock (gate)
                candidatePath = eventArgs.FullPath;
        }

        private void OnRolloutRenamed(object sender, RenamedEventArgs eventArgs)
        {
            if (!IsRootRollout(eventArgs.FullPath))
                return;
            lock (gate)
                candidatePath = eventArgs.FullPath;
        }

        private void Refresh(object ignored)
        {
            if (Interlocked.Exchange(ref refreshRunning, 1) != 0)
                return;
            try
            {
                string path;
                lock (gate)
                    path = candidatePath;

                if (String.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                    (DateTime.UtcNow - lastDiscoveryUtc).TotalSeconds >= 30)
                {
                    string discovered = DiscoverLatestRollout();
                    if (!String.IsNullOrWhiteSpace(discovered))
                    {
                        path = discovered;
                        lock (gate)
                            candidatePath = discovered;
                    }
                    lastDiscoveryUtc = DateTime.UtcNow;
                }

                CodexTaskState detected = InspectRolloutTail(path);
                lock (gate)
                {
                    if (detected != CodexTaskState.Unknown || state == CodexTaskState.Unknown)
                        state = detected;
                }
            }
            catch
            {
                // Keep the last valid state when a rollout is momentarily locked or incomplete.
            }
            finally
            {
                Interlocked.Exchange(ref refreshRunning, 0);
            }
        }

        private string DiscoverLatestRollout()
        {
            if (!Directory.Exists(sessionsRoot))
                return null;

            string newestPath = null;
            DateTime newestWriteUtc = DateTime.MinValue;
            try
            {
                foreach (string path in Directory.EnumerateFiles(sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories))
                {
                    if (!IsRootRollout(path))
                        continue;
                    DateTime writeUtc;
                    try { writeUtc = File.GetLastWriteTimeUtc(path); }
                    catch { continue; }
                    if (writeUtc <= newestWriteUtc)
                        continue;
                    newestWriteUtc = writeUtc;
                    newestPath = path;
                }
            }
            catch
            {
            }
            return newestPath;
        }

        private bool IsRootRollout(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            lock (gate)
            {
                bool cached;
                if (rootRolloutCache.TryGetValue(path, out cached))
                    return cached;
            }

            bool isRoot = false;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 4096))
                {
                    string sessionMeta = reader.ReadLine() ?? String.Empty;
                    bool hasParentThread = sessionMeta.IndexOf("\"parent_thread_id\":\"", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isSubAgent = sessionMeta.IndexOf("\"source\":{\"subagent\"", StringComparison.OrdinalIgnoreCase) >= 0;
                    isRoot = !hasParentThread && !isSubAgent;
                }
            }
            catch
            {
                return false;
            }

            lock (gate)
                rootRolloutCache[path] = isRoot;
            return isRoot;
        }

        private static CodexTaskState InspectRolloutTail(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return CodexTaskState.Unknown;

            CodexTaskState detected = CodexTaskState.Unknown;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, stream.Length - TailBytes);
                stream.Seek(start, SeekOrigin.Begin);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 4096))
                {
                    if (start > 0)
                        reader.ReadLine();

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (IsUserMessage(line))
                            detected = CodexTaskState.Processing;
                        else if (IsFinalAssistantMessage(line))
                            detected = CodexTaskState.Completed;
                        else if (IsTurnInterrupted(line))
                            detected = CodexTaskState.Interrupted;
                        else if (IsActiveWorkEvent(line))
                            detected = CodexTaskState.Processing;
                    }
                }
            }
            return detected;
        }

        private static bool IsUserMessage(string line)
        {
            bool eventUserMessage = line.IndexOf("\"type\":\"event_msg\"", StringComparison.Ordinal) >= 0 &&
                line.IndexOf("\"type\":\"user_message\"", StringComparison.Ordinal) >= 0;
            bool responseUserMessage = line.IndexOf("\"type\":\"response_item\"", StringComparison.Ordinal) >= 0 &&
                line.IndexOf("\"type\":\"message\"", StringComparison.Ordinal) >= 0 &&
                line.IndexOf("\"role\":\"user\"", StringComparison.Ordinal) >= 0;
            bool compactedUserMessage = line.IndexOf("\"type\":\"compacted\"", StringComparison.Ordinal) >= 0 &&
                line.IndexOf("\"role\":\"user\"", StringComparison.Ordinal) >= 0;
            return eventUserMessage || responseUserMessage || compactedUserMessage;
        }

        private static bool IsActiveWorkEvent(string line)
        {
            if (line.IndexOf("\"type\":\"response_item\"", StringComparison.Ordinal) < 0)
                return false;

            return line.IndexOf("\"role\":\"assistant\"", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("\"type\":\"custom_tool_call\"", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("\"type\":\"custom_tool_call_output\"", StringComparison.Ordinal) >= 0;
        }

        private static bool IsFinalAssistantMessage(string line)
        {
            return line.IndexOf("\"type\":\"response_item\"", StringComparison.Ordinal) >= 0 &&
                line.IndexOf("\"type\":\"message\"", StringComparison.Ordinal) >= 0 &&
                line.IndexOf("\"role\":\"assistant\"", StringComparison.Ordinal) >= 0 &&
                (line.IndexOf("\"channel\":\"final\"", StringComparison.Ordinal) >= 0 ||
                 line.IndexOf("\"channel\":\"final_answer\"", StringComparison.Ordinal) >= 0 ||
                 line.IndexOf("\"phase\":\"final_answer\"", StringComparison.Ordinal) >= 0);
        }

        private static bool IsTurnInterrupted(string line)
        {
            return line.IndexOf("\"type\":\"event_msg\"", StringComparison.Ordinal) >= 0 &&
                line.IndexOf("\"type\":\"turn_aborted\"", StringComparison.Ordinal) >= 0;
        }

        public void Dispose()
        {
            timer.Dispose();
            if (watcher != null)
                watcher.Dispose();
        }
    }
}
