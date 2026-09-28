using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace CodexUsageOverlay
{
    internal static class CodexThreadContextTests
    {
        internal static void Verify()
        {
            var threads = new List<CodexThreadDescriptor>
            {
                new CodexThreadDescriptor { Id = "11111111-1111-1111-1111-111111111111", Name = "第一条", Path = @"C:\sessions\rollout-first.jsonl" },
                new CodexThreadDescriptor { Id = "22222222-2222-2222-2222-222222222222", Name = "同名", Path = @"C:\sessions\rollout-second.jsonl" },
                new CodexThreadDescriptor { Id = "33333333-3333-3333-3333-333333333333", Name = "同名", Path = @"C:\sessions\rollout-third.jsonl" }
            };
            Assert(CodexThreadContextMonitor.FindUniqueThread(threads, "第一条") == threads[0],
                "unique title did not map to its own thread");
            Assert(CodexThreadContextMonitor.FindUniqueThread(threads, "同名") == null,
                "duplicate titles must not be assigned another thread's usage");
            Assert(CodexThreadContextMonitor.FindUniqueThread(threads, "不存在") == null,
                "missing title must remain blank");
            Assert(CodexThreadContextMonitor.IsSidebarRow(
                new Rectangle(0, 0, 1900, 1000), new Rectangle(8, 439, 262, 31)),
                "visible sidebar conversation was rejected");
            Assert(!CodexThreadContextMonitor.IsSidebarRow(
                new Rectangle(0, 0, 1900, 1000), new Rectangle(330, 439, 262, 31)),
                "main content must not be treated as a sidebar conversation");
            Assert(CodexThreadContextMonitor.IsSidebarRow(
                new Rectangle(121, 27, 1641, 960), new Rectangle(181, 194, 333, 31)),
                "current Codex navigation-rail inset rejected every conversation");
            Assert(CodexThreadContextMonitor.IsSidebarRow(
                new Rectangle(100, 50, 3282, 1920), new Rectangle(220, 384, 666, 62), 2f),
                "high-DPI navigation-rail inset rejected conversations");
            Assert(CodexThreadContextMonitor.IsThreadHeader(
                new Rectangle(121, 27, 1641, 960), new Rectangle(580, 87, 160, 19),
                System.Windows.Automation.ControlType.Text, 1f), "native text header rejected");
            Assert(CodexThreadContextMonitor.IsThreadHeader(
                new Rectangle(-8, -8, 1936, 1048), new Rectangle(459, 60, 160, 19),
                System.Windows.Automation.ControlType.Text, 1f), "maximized invisible frame rejected native header");
            Assert(!CodexThreadContextMonitor.IsThreadHeader(
                new Rectangle(-8, -8, 1936, 1048), new Rectangle(503, 82, 477, 20),
                System.Windows.Automation.ControlType.Text, 1f), "maximized body accepted as header");
            Assert(CodexThreadContextMonitor.IsThreadHeader(
                new Rectangle(-16, -16, 3872, 2096), new Rectangle(918, 120, 320, 38),
                System.Windows.Automation.ControlType.Text, 2f), "200 percent maximized header rejected");
            Assert(!CodexThreadContextMonitor.IsThreadHeader(
                new Rectangle(121, 27, 1641, 960), new Rectangle(624, 109, 477, 20),
                System.Windows.Automation.ControlType.Text, 1f), "conversation body accepted as header");
            Assert(CodexThreadContextMonitor.ProbeDelay(
                new Rectangle(0, 0, 1900, 1000), new Point(250, 500), 1f) <=
                TimeSpan.FromMilliseconds(300), "sidebar scroll did not use the fast probe rate");
            Assert(CodexThreadContextMonitor.ProbeDelay(
                new Rectangle(0, 0, 1900, 1000), new Point(800, 500), 1f) >=
                TimeSpan.FromSeconds(2), "non-sidebar probing did not stay throttled");
            var source = new Dictionary<string, object> {
                { "data", new object[] {
                    new Dictionary<string, object> {
                        { "id", "11111111-1111-1111-1111-111111111111" },
                        { "name", "第一条" }, { "path", @"C:\sessions\rollout-first.jsonl" } },
                    new Dictionary<string, object> {
                        { "id", "not-a-thread" }, { "name", "伪会话" },
                        { "path", @"C:\sessions\rollout-fake.jsonl" } }
                } }
            };
            Assert(CodexAppServerClient.ParseThreadList(source).Count == 1,
                "thread/list parser accepted an invalid thread ID");
            var resumedSource = new Dictionary<string, object> {
                { "data", new object[] {
                    ThreadEntry("11111111-1111-1111-1111-111111111111", "已恢复会话", @"C:\sessions\rollout-new.jsonl"),
                    ThreadEntry("11111111-1111-1111-1111-111111111111", "已恢复会话", @"C:\sessions\rollout-older.jsonl"),
                    ThreadEntry("11111111-1111-1111-1111-111111111111", "已恢复会话", @"C:\sessions\rollout-original.jsonl")
                } }
            };
            IList<CodexThreadDescriptor> resumed = CodexAppServerClient.ParseThreadList(resumedSource);
            Assert(resumed.Count == 1 && CodexThreadContextMonitor.FindUniqueThread(resumed, "已恢复会话") != null,
                "multiple rollout records for one stable ID must not become an ambiguous title");
            Assert(resumed[0].RolloutPaths.Count == 3,
                "merging a resumed thread must retain all of its rollout files");
            VerifyResumedThreadSignals();
            VerifyColdSidebarSignals();
        }

        private static void VerifyColdSidebarSignals()
        {
            string testHome = Path.Combine(Path.GetTempPath(), "codex-sidebar-cold-" + Guid.NewGuid().ToString("N"));
            string sessions = Path.Combine(testHome, "sessions");
            Directory.CreateDirectory(sessions);
            string first = Path.Combine(sessions, "rollout-visible.jsonl");
            string cold = Path.Combine(sessions, "rollout-cold.jsonl");
            string empty = Path.Combine(sessions, "rollout-empty.jsonl");
            string[] files = { first, cold, empty };
            DateTimeOffset observed = DateTimeOffset.UtcNow;
            try
            {
                File.WriteAllText(first, TokenRecord(observed, 10000, 100000));
                File.WriteAllText(cold, TokenRecord(observed, 20000, 200000));
                File.WriteAllText(empty, String.Empty);
                var known = new List<CodexThreadDescriptor> {
                    new CodexThreadDescriptor { Id = "visible", Name = "Visible", Path = first },
                    new CodexThreadDescriptor { Id = "cold", Name = "Cold", Path = cold },
                    new CodexThreadDescriptor { Id = "empty", Name = "Empty", Path = empty },
                    new CodexThreadDescriptor { Id = "duplicate-a", Name = "Duplicate", Path = first },
                    new CodexThreadDescriptor { Id = "duplicate-b", Name = "Duplicate", Path = cold }
                };
                using (var monitor = new CodexThreadContextMonitor(testHome))
                {
                    const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                    Type type = typeof(CodexThreadContextMonitor);
                    MethodInfo publish = type.GetMethod("PublishRefreshedSnapshotAndWarmSignals", fields);
                    Assert(publish != null, "cold sidebar rows have no background prefetch publication path");
                    type.GetField("threads", fields).SetValue(monitor, known);
                    IntPtr window = new IntPtr(1);
                    Rectangle host = new Rectangle(0, 0, 1200, 800);
                    type.GetField("requestedWindow", fields).SetValue(monitor, window);
                    type.GetField("requestedBounds", fields).SetValue(monitor, host);
                    CodexContextSignal visible = monitor.ReadThreadSignal(known[0]);
                    var cache = (IDictionary<string, CodexContextSignal>)type.GetField("threadSignalCache", fields).GetValue(monitor);
                    var firstRows = new List<CodexSidebarContextRow> {
                        new CodexSidebarContextRow { Bounds = new Rectangle(8, 150, 300, 31), Signal = visible }
                    };
                    var firstSnapshot = new CodexThreadContextSnapshot { ActiveSignal = visible, Rows = firstRows };
                    var scrolledRows = new List<CodexSidebarContextRow> {
                        new CodexSidebarContextRow { Bounds = new Rectangle(8, 181, 300, 31), Signal = visible }
                    };
                    var movedGeometry = new Dictionary<string, CodexSidebarContextRow>(StringComparer.Ordinal);
                    bool sawVisibleBeforeWarm = false;
                    Action onVisible = delegate
                    {
                        Assert(Object.ReferenceEquals(monitor.Snapshot(window, host).Rows, firstRows),
                            "visible rows must be published before cold prefetch starts");
                        Assert(!cache.ContainsKey("cold") && !cache.ContainsKey("empty"),
                            "first-screen publication waited for cold sessions");
                        sawVisibleBeforeWarm = true;
                        // The independent position worker can publish a new scroll frame during prefetch.
                        type.GetField("cached", fields).SetValue(monitor,
                            new CodexThreadContextSnapshot { ActiveSignal = visible, Rows = scrolledRows });
                        type.GetField("sidebarTitleGeometry", fields).SetValue(monitor, movedGeometry);
                    };
                    monitor.SidebarChanged += onVisible;
                    publish.Invoke(monitor, new object[] { window, host, known, firstSnapshot, null });
                    monitor.SidebarChanged -= onVisible;
                    Assert(sawVisibleBeforeWarm, "cold prefetch delayed the visible result notification");
                    var signals = (IDictionary<string, CodexContextSignal>)type.GetField("sidebarSignals", fields).GetValue(monitor);
                    Assert(signals["Cold"] != null && signals["Cold"].UsedTokens == 20000,
                        "a newly scrolled known row still needs the slow whole-window probe to obtain its signal");
                    Assert(signals["Empty"] != null && !signals["Empty"].Available,
                        "an empty session must be cached as checked, not remain a missing signal");
                    Assert(!signals.ContainsKey("Duplicate") && !cache.ContainsKey("duplicate-a") && !cache.ContainsKey("duplicate-b"),
                        "cold prefetch assigned ambiguous titles from different thread IDs");
                    Assert(Object.ReferenceEquals(monitor.Snapshot(window, host).Rows, scrolledRows) &&
                        Object.ReferenceEquals(type.GetField("sidebarTitleGeometry", fields).GetValue(monitor), movedGeometry),
                        "cold prefetch publication overwrote newer row/title geometry");

                    CodexContextSignal warmed = cache["cold"], checkedEmpty = cache["empty"];
                    File.WriteAllText(cold, TokenRecord(observed.AddMinutes(1), 40000, 400000));
                    File.WriteAllText(empty, TokenRecord(observed.AddMinutes(1), 50000, 500000));
                    publish.Invoke(monitor, new object[] { window, host, known, firstSnapshot, null });
                    Assert(Object.ReferenceEquals(cache["cold"], warmed) && Object.ReferenceEquals(cache["empty"], checkedEmpty),
                        "cold prefetch re-read an already checked signal, including an empty one");
                    Assert(monitor.ReadThreadSignal(known[2]).UsedTokens == 50000,
                        "a normal visible-row refresh must still refresh an earlier empty session");
                }
            }
            finally
            {
                foreach (string file in files) if (File.Exists(file)) File.Delete(file);
                Directory.Delete(sessions);
                Directory.Delete(testHome);
            }
        }

        private static void VerifyResumedThreadSignals()
        {
            string testHome = Path.Combine(Path.GetTempPath(), "codex-context-duplicate-" + Guid.NewGuid().ToString("N"));
            string sessions = Path.Combine(testHome, "sessions");
            Directory.CreateDirectory(sessions);
            string older = Path.Combine(sessions, "rollout-older.jsonl");
            string newer = Path.Combine(sessions, "rollout-newer.jsonl");
            string empty = Path.Combine(sessions, "rollout-empty.jsonl");
            string broken = Path.Combine(sessions, "rollout-broken.jsonl");
            string outside = Path.Combine(testHome, "rollout-outside.jsonl");
            string wrongExtension = Path.Combine(sessions, "rollout-wrong.txt");
            string wrongName = Path.Combine(sessions, "not-a-rollout.jsonl");
            string[] files = { older, newer, empty, broken, outside, wrongExtension, wrongName };
            DateTimeOffset observed = DateTimeOffset.UtcNow.AddHours(-2);
            try
            {
                File.WriteAllText(older, TokenRecord(observed.AddHours(-1), 100000, 9000000));
                File.WriteAllText(newer, TokenRecord(observed, 60000, 700000));
                File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));
                File.WriteAllText(empty, String.Empty);
                File.WriteAllText(broken, "{\"type\":\"token_count\",broken");
                File.WriteAllText(outside, TokenRecord(observed.AddHours(1), 180000, 12000000));
                File.WriteAllText(wrongExtension, TokenRecord(observed.AddHours(1), 180000, 12000000));
                File.WriteAllText(wrongName, TokenRecord(observed.AddHours(1), 180000, 12000000));
                const string firstId = "11111111-1111-1111-1111-111111111111";
                var source = new Dictionary<string, object> { { "data", new object[] {
                    ThreadEntry(firstId, "已恢复会话", empty),
                    ThreadEntry(firstId, "已恢复会话", broken),
                    ThreadEntry(firstId, "已恢复会话", older),
                    ThreadEntry(firstId, "已恢复会话", newer),
                    ThreadEntry(firstId, "已恢复会话", newer.ToUpperInvariant()),
                    ThreadEntry(firstId, "已恢复会话", outside),
                    ThreadEntry(firstId, "已恢复会话", wrongExtension),
                    ThreadEntry(firstId, "已恢复会话", wrongName)
                } } };
                IList<CodexThreadDescriptor> parsed = CodexAppServerClient.ParseThreadList(source);
                Assert(parsed.Count == 1 && parsed[0].RolloutPaths.Count == 7,
                    "resumed rollout paths must be retained once, regardless of path casing");
                using (var monitor = new CodexThreadContextMonitor(testHome))
                {
                    CodexContextSignal signal = monitor.ReadThreadSignal(parsed[0]);
                    Assert(signal.Available && signal.UsedTokens == 60000 && signal.SessionTokens == 700000 &&
                        signal.SourcePath == newer && signal.ObservedAt == observed,
                        "latest valid observation must win, not file write time, greatest usage, or a sum of snapshots");
                    Assert(!signal.IsRecent(DateTime.UtcNow),
                        "selecting a resumed snapshot must preserve its original stale timestamp");
                    var cache = (IDictionary<string, CodexContextSignal>)typeof(CodexThreadContextMonitor)
                        .GetField("threadSignalCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(monitor);
                    Assert(Object.ReferenceEquals(cache[firstId], signal),
                        "sidebar cache must use the resolved stable-ID signal, not the empty primary path");

                    File.WriteAllText(newer, String.Empty);
                    signal = monitor.ReadThreadSignal(parsed[0]);
                    Assert(signal.Available && signal.SourcePath == older && signal.UsedTokens == 100000 &&
                        signal.ObservedAt == observed.AddHours(-1),
                        "an empty newer rollout must not erase an older valid observation");
                    File.WriteAllText(newer, "{\"type\":\"token_count\",incomplete");
                    signal = monitor.ReadThreadSignal(parsed[0]);
                    Assert(signal.Available && signal.SourcePath == older,
                        "an incomplete newer rollout must not erase an older valid observation");
                    File.WriteAllText(newer, TokenRecord(observed.AddMinutes(1), 80000, 800000));
                    signal = monitor.ReadThreadSignal(parsed[0]);
                    Assert(signal.UsedTokens == 80000 && signal.SessionTokens == 800000 && signal.SourcePath == newer,
                        "a newly completed rollout must replace the cached fallback without duplicating usage");
                }
                var differentIds = new Dictionary<string, object> { { "data", new object[] {
                    ThreadEntry(firstId, "同名", older), ThreadEntry(firstId, "同名", newer),
                    ThreadEntry("22222222-2222-2222-2222-222222222222", "同名", empty)
                } } };
                parsed = CodexAppServerClient.ParseThreadList(differentIds);
                Assert(parsed.Count == 2 && CodexThreadContextMonitor.FindUniqueThread(parsed, "同名") == null,
                    "different stable IDs with the same title must remain ambiguous after rollout merging");
            }
            finally
            {
                foreach (string file in files) if (File.Exists(file)) File.Delete(file);
                Directory.Delete(sessions);
                Directory.Delete(testHome);
            }
        }

        private static string TokenRecord(DateTimeOffset observed, long used, long total)
        {
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "type", "event_msg" }, { "timestamp", observed.ToString("o") },
                { "payload", new Dictionary<string, object> {
                    { "type", "token_count" }, { "info", new Dictionary<string, object> {
                        { "model_context_window", 200000 },
                        { "last_token_usage", new Dictionary<string, object> { { "total_tokens", used } } },
                        { "total_token_usage", new Dictionary<string, object> {
                            { "total_tokens", total }, { "input_tokens", total - 1000 },
                            { "cached_input_tokens", total - 2000 }, { "output_tokens", 1000 }
                        } }
                    } }
                } }
            });
        }

        private static IDictionary<string, object> ThreadEntry(string id, string name, string path)
        {
            return new Dictionary<string, object> { { "id", id }, { "name", name }, { "path", path } };
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
