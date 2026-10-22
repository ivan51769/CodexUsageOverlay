using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using CodexUsageOverlay;

internal static class CodexTurnTimingTests
{
    internal static void Verify()
    {
        string home = Path.Combine(Path.GetTempPath(), "codex-ttft-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(home, "sessions") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "rollout-first.jsonl"), second = Path.Combine(root, "rollout-resumed.jsonl");
        string outside = Path.Combine(home, "rollout-outside.jsonl");
        try
        {
            File.WriteAllText(first, Event("a", "1000") + Event("b", "3000") + Event("a", "1000") +
                Event("missing", "null") + Event("negative", "-1") + Event("fraction", "1.5") +
                Event("bad-duration", "900000") + Event("aborted", "2000").Replace("task_complete", "turn_aborted") +
                Event("not-event", "2000").Replace("event_msg", "response_item"));
            File.WriteAllText(second, Event("b", "3000") + Event("c", "2000"));
            File.WriteAllText(outside, Event("outside", "8000"));
            var reader = new CodexTurnTimingReader(root);
            var thread = new CodexThreadDescriptor { Id = "one", Path = first };
            thread.RolloutPaths.Add(first); thread.RolloutPaths.Add(second); thread.RolloutPaths.Add(outside);
            var original = new CodexContextSignal { UsedTokens = 10, WindowTokens = 100 };
            CodexContextSignal result = reader.Read(thread, original);
            Check(result.FirstTokenSampleCount == 3 && result.FirstTokenAverageText == "2.0s", "valid completed turns deduped across segments");
            Check(original.FirstTokenSampleCount == 0 && result.UsedTokens == 10, "published snapshots must be immutable");
            reader.Read(thread, original);
            Check(reader.BytesReadLastRefresh == 0, "unchanged files must not be reread");
            string partial = Event("d", "4000");
            File.AppendAllText(second, partial.Substring(0, partial.Length / 2));
            Check(reader.Read(thread, original).FirstTokenSampleCount == 3, "partial record must wait for completion");
            string rest = partial.Substring(partial.Length / 2);
            File.AppendAllText(second, rest);
            result = reader.Read(thread, original);
            Check(reader.BytesReadLastRefresh == Encoding.UTF8.GetByteCount(rest) && result.FirstTokenSampleCount == 4 &&
                result.FirstTokenAverageText == "2.5s", "append must consume only new bytes and count a split record once");
            File.WriteAllText(first, Event("replacement", "7000"));
            result = reader.Read(thread, original);
            Check(result.FirstTokenSampleCount == 4 && result.FirstTokenTotalMilliseconds == 16000, "truncation must reset stale samples");
            File.WriteAllText(first, Event("replacement", "8000"));
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(2));
            Check(reader.Read(thread, original).FirstTokenTotalMilliseconds == 17000, "same-size rewrite must reset cached timing");
            var other = new CodexThreadDescriptor { Id = "two", Path = outside };
            Check(reader.Read(other, original).FirstTokenSampleCount == 0, "thread switch must not show previous timing or read outside sessions");
            other.Path = @"\\?\" + first;
            Check(reader.Read(other, original).FirstTokenAverageText == "8.0s", "extended Windows path must resolve");

            // A large attachment plus thousands of irrelevant lines must not be retained as strings.
            using (var writer = new StreamWriter(first, false, new UTF8Encoding(false)))
            {
                writer.Write("{\"type\":\"response_item\",\"payload\":\"data:image/png;base64,");
                string block = new string('x', 32768);
                for (int i = 0; i < 1024; i++) writer.Write(block);
                writer.Write("\"}\n"); writer.Write(Event("large-fixture", "2200"));
                writer.Write("{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"");
                for (int i = 0; i < 16; i++) writer.Write(block);
                writer.Write("\",\"turn_id\":\"oversized\",\"time_to_first_token_ms\":8000}}\n");
                writer.Write(Event("zero", "0"));
            }
            long before = GC.GetTotalMemory(true);
            var watch = Stopwatch.StartNew();
            result = reader.Read(other, original);
            long retained = GC.GetTotalMemory(true) - before;
            Check(result.FirstTokenSampleCount == 2 && result.FirstTokenAverageText == "1.1s", "oversized records must be skipped without losing next valid sample");
            Check(retained < 2 * 1024 * 1024, "large transcript leaked into retained timing state");
            for (int i = 0; i < 100; i++) reader.Read(other, original);
            Check(reader.BytesReadLastRefresh == 0, "idle timing refresh must perform no log reads");
            Console.WriteLine("TTFT 32 MiB attachment: retained=" + retained + " bytes; elapsed=" + watch.ElapsedMilliseconds + " ms");
        }
        finally
        {
            foreach (string file in new[] { first, second, outside }) if (File.Exists(file)) File.Delete(file);
            Directory.Delete(root); Directory.Delete(home);
        }
    }
    private static string Event(string id, string milliseconds)
    { return "{\"timestamp\":\"2026-10-22T01:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"" + id + "\",\"duration_ms\":100000,\"time_to_first_token_ms\":" + milliseconds + "}}\n"; }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
