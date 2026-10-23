using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace CodexUsageOverlay
{
    internal static class MemoryCacheTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;

        internal static void Verify()
        {
            Type type = typeof(CodexThreadContextMonitor);
            MethodInfo prune = type.GetMethod("PruneSignalCaches", Members);
            Assert(prune != null, "session caches have no pruning path after a successful thread-list refresh");

            string testHome = Path.Combine(Path.GetTempPath(), "codex-memory-cache-" + Guid.NewGuid().ToString("N"));
            string sessions = Path.Combine(testHome, "sessions");
            Directory.CreateDirectory(sessions);
            var files = new List<string>();
            DateTimeOffset observed = DateTimeOffset.UtcNow.AddHours(-1);
            try
            {
                string primary = WriteRollout(sessions, files, "primary", observed, 10000);
                string older = WriteRollout(sessions, files, "older", observed.AddMinutes(1), 20000);
                string newer = WriteRollout(sessions, files, "newer", observed.AddMinutes(2), 30000);
                var resumed = new CodexThreadDescriptor { Id = "resumed", Name = "Resumed", Path = primary };
                // Both the primary path and every continuation must survive pruning,
                // even when the latest thread list uses another Windows spelling.
                resumed.RolloutPaths.Add(Path.Combine(sessions, ".", Path.GetFileName(older)));
                resumed.RolloutPaths.Add(@"\\?\" + newer.ToUpperInvariant());
                resumed.RolloutPaths.Add(newer);

                using (var monitor = new CodexThreadContextMonitor(testHome))
                {
                    var paths = (IDictionary)type.GetField("signalCache", Members).GetValue(monitor);
                    var threads = (IDictionary)type.GetField("threadSignalCache", Members).GetValue(monitor);
                    MethodInfo readPath = type.GetMethod("ReadSignal", Members);
                    var primarySignal = (CodexContextSignal)readPath.Invoke(monitor, new object[] { primary });
                    CodexContextSignal latest = monitor.ReadThreadSignal(resumed);
                    Assert(latest.Available && latest.UsedTokens == 30000 && paths.Count == 3,
                        "resumed fixture did not initialize three distinct canonical rollout caches");
                    object primaryCache = paths[primary], olderCache = paths[older], newerCache = paths[newer];
                    CodexRolloutTailReader primaryReader = Reader(primaryCache);
                    CodexRolloutTailReader olderReader = Reader(olderCache);
                    CodexRolloutTailReader newerReader = Reader(newerCache);

                    // Push the last token observation outside a fresh reader's 2 MiB
                    // tail. Only the retained incremental reader can keep that signal.
                    using (var writer = new StreamWriter(newer, true))
                    {
                        writer.Write("{\"type\":\"event_msg\",\"payload\":{\"type\":\"attachment\",\"data\":\"");
                        string block = new string('x', 8192);
                        for (int index = 0; index < 257; index++) writer.Write(block);
                        writer.Write("\"}}\n");
                    }
                    Assert(new FileInfo(newer).Length > 2 * 1024 * 1024 &&
                        Object.ReferenceEquals(monitor.ReadThreadSignal(resumed), latest),
                        "an attachment larger than the rollout tail erased the previously cached token observation");

                    string previousId = null, previousPath = null;
                    for (int batch = 0; batch < 24; batch++)
                    {
                        var known = new List<CodexThreadDescriptor> { resumed };
                        for (int index = 0; index < 4; index++)
                        {
                            string id = "batch-" + batch + "-" + index;
                            string path = WriteRollout(sessions, files, id, observed, 40000 + index);
                            var thread = new CodexThreadDescriptor { Id = id, Name = id, Path = path };
                            monitor.ReadThreadSignal(thread);
                            known.Add(thread);
                        }
                        prune.Invoke(monitor, new object[] { known });
                        Assert(threads.Count == known.Count && paths.Count == 7,
                            "session cache retained old batches instead of being bounded by the current thread list");
                        Assert(threads.Contains(resumed.Id) && Object.ReferenceEquals(threads[resumed.Id], latest),
                            "pruning removed or replaced an existing valid thread signal");
                        if (previousId != null)
                            Assert(!threads.Contains(previousId) && !paths.Contains(previousPath),
                                "evicted thread or rollout cache survived a successful list replacement");
                        foreach (CodexThreadDescriptor thread in known)
                            Assert(threads.Contains(thread.Id), "pruning removed a currently known thread");
                        Assert(Object.ReferenceEquals(paths[primary], primaryCache) &&
                            Object.ReferenceEquals(paths[older], olderCache) && Object.ReferenceEquals(paths[newer], newerCache),
                            "canonical path pruning discarded a primary or continuation reader");

                        Assert(Object.ReferenceEquals(monitor.ReadThreadSignal(resumed), latest),
                            "pruning caused an unchanged continuation signal to be reparsed");
                        Assert(Object.ReferenceEquals(readPath.Invoke(monitor, new object[] { primary }), primarySignal),
                            "pruning discarded the separate primary path signal");
                        Assert(primaryReader.BytesReadLastRefresh == 0 && olderReader.BytesReadLastRefresh == 0 &&
                            newerReader.BytesReadLastRefresh == 0,
                            "an unchanged retained rollout was scanned again after pruning");
                        previousId = known[1].Id;
                        previousPath = known[1].Path;
                    }

                    // Normalized aliases must share one reader, and new appended
                    // records must remain visible after many prune cycles.
                    string appended = TokenRecord(observed.AddMinutes(3), 50000);
                    File.AppendAllText(newer, appended);
                    CodexContextSignal updated = monitor.ReadThreadSignal(resumed);
                    Assert(updated.Available && updated.UsedTokens == 50000 && updated.ObservedAt == observed.AddMinutes(3),
                        "pruning broke incremental updates of a retained continuation");
                    Assert(Object.ReferenceEquals(paths[newer], newerCache),
                        "extended and normal Windows paths created separate reader caches");

                    prune.Invoke(monitor, new object[] { new List<CodexThreadDescriptor>() });
                    Assert(threads.Count == 0 && paths.Count == 0,
                        "an empty successful thread list did not release all obsolete session caches");
                    foreach (string file in files)
                        Assert(File.Exists(file) && new FileInfo(file).Length > 0,
                            "cache eviction deleted or truncated a session log");
                    Assert(File.ReadAllText(newer).EndsWith(appended, StringComparison.Ordinal),
                        "cache eviction changed an appended session record");
                }
            }
            finally
            {
                foreach (string file in files) if (File.Exists(file)) File.Delete(file);
                Directory.Delete(sessions);
                Directory.Delete(testHome);
            }
        }

        private static CodexRolloutTailReader Reader(object cached)
        {
            return (CodexRolloutTailReader)cached.GetType().GetField("Reader", Members).GetValue(cached);
        }

        private static string WriteRollout(string sessions, IList<string> files, string name,
            DateTimeOffset observed, long used)
        {
            string path = Path.Combine(sessions, "rollout-" + name + ".jsonl");
            files.Add(path);
            File.WriteAllText(path, TokenRecord(observed, used));
            return path;
        }

        private static string TokenRecord(DateTimeOffset observed, long used)
        {
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "type", "event_msg" }, { "timestamp", observed.ToString("o") },
                { "payload", new Dictionary<string, object> {
                    { "type", "token_count" }, { "info", new Dictionary<string, object> {
                        { "model_context_window", 200000 },
                        { "last_token_usage", new Dictionary<string, object> { { "total_tokens", used } } },
                        { "total_token_usage", new Dictionary<string, object> {
                            { "total_tokens", used * 10 }, { "input_tokens", used * 10 - 1000 },
                            { "cached_input_tokens", used * 10 - 2000 }, { "output_tokens", 1000 }
                        } }
                    } }
                } }
            }) + "\n";
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
