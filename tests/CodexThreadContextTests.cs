using System;
using System.Collections.Generic;
using System.Drawing;

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
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
