using System;
using System.IO;
using System.Reflection;
using System.Text;
using CodexUsageOverlay;

internal static class CodexRolloutTailReaderTests
{
    internal static void Verify()
    {
        string home = Path.Combine(Path.GetTempPath(), "codex-tail-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(home, "sessions");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "rollout-test.jsonl");
        try
        {
            string token = "{\"timestamp\":\"2026-10-22T01:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":100000,\"last_token_usage\":{\"total_tokens\":12345}}}}\n";
            File.WriteAllText(path, token, new UTF8Encoding(false));
            var reader = new CodexRolloutTailReader(path);
            int reset = 0, records = 0;
            Action clear = delegate { reset++; };
            Action<string> inspect = delegate(string line) { records++; };
            reader.Read(clear, inspect);
            Check(reset == 1 && records == 1, "initial tail read");
            for (int i = 0; i < 100; i++) reader.Read(clear, inspect);
            Check(records == 1 && reader.BytesReadLastRefresh == 0, "idle status/context polls must not parse or allocate log lines");
            File.AppendAllText(path, "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_");
            reader.Read(clear, inspect);
            string received = null;
            File.AppendAllText(path, "complete\"}}\n");
            reader.Read(clear, delegate(string line) { received = line; });
            Check(received.Contains("\"task_complete\"") && reader.BytesReadLastRefresh == 12,
                "split append must resume at its byte offset");
            File.WriteAllText(path, "replacement\n");
            reader.Read(clear, inspect);
            Check(reset == 2, "truncation must reset cached state");
            File.WriteAllText(path, "replacemenX\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            reader.Read(clear, delegate(string line) { received = line; });
            Check(reset == 3 && received == "replacemenX", "same-size rewrite must reset cached state");

            File.WriteAllText(path, token);
            using (var monitor = new CodexThreadContextMonitor(home))
            {
                var thread = new CodexThreadDescriptor { Id = "fixture", Path = path };
                Check(monitor.ReadThreadSignal(thread).UsedTokens == 12345, "initial context must parse");
                using (var writer = new StreamWriter(path, true, new UTF8Encoding(false)))
                {
                    writer.Write("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"");
                    string block = new string('x', 32768);
                    for (int i = 0; i < 128; i++) writer.Write(block);
                    writer.Write("\",\"phase\":\"final_answer\"}}\n");
                }
                Check(monitor.ReadThreadSignal(thread).UsedTokens == 12345,
                    "an attachment larger than the old tail must not erase the last confirmed context");
            }
            // Read from the initial small record, then append a large legacy final response.
            File.WriteAllText(path, token);
            reader = new CodexRolloutTailReader(path);
            reader.Read(delegate { }, delegate { });
            string large = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"" +
                new string('x', 4 * 1024 * 1024) + "\",\"phase\":\"final_answer\"}}\n";
            File.AppendAllText(path, large);
            int length = 0;
            reader.Read(delegate { }, delegate(string line) { length = line.Length; received = line; });
            Check(length <= 32768 + 512 && received.Contains("\"phase\":\"final_answer\""), "large replies must preserve final phase with bounded memory");
            var state = CodexTaskState.Unknown;
            object[] parameters = { received, path, state, CodexContextSignal.Empty };
            typeof(CodexTaskStatusMonitor).GetMethod("InspectRecord", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, parameters);
            Check((CodexTaskState)parameters[2] == CodexTaskState.Completed, "large legacy final response must remain completed");
            parameters[0] = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}";
            typeof(CodexTaskStatusMonitor).GetMethod("InspectRecord", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, parameters);
            Check((CodexTaskState)parameters[2] == CodexTaskState.Processing, "new turn must restore active status");
            parameters[0] = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_aborted\"}}";
            typeof(CodexTaskStatusMonitor).GetMethod("InspectRecord", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, parameters);
            Check((CodexTaskState)parameters[2] == CodexTaskState.Interrupted, "abort status must remain supported");
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(folder); Directory.Delete(home); }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
