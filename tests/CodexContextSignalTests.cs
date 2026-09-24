using System;

namespace CodexUsageOverlay
{
    internal static class CodexContextSignalTests
    {
        internal static void Verify()
        {
            DateTime observed = new DateTime(2026, 9, 24, 6, 0, 0, DateTimeKind.Utc);
            Check(100000, 200000, observed, 50, 0);
            Check(145000, 200000, observed, 72, 1);
            Check(139000, 200000, observed, 69, 0);
            Check(169000, 200000, observed, 84, 1);
            Check(190000, 200000, observed, 95, 2);
            CodexContextSignal unrelated = CodexContextSignal.ParseTokenCount(
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"private\"}}", observed);
            Assert(!unrelated.Available, "message content was treated as usage");
            CodexContextSignal missing = CodexContextSignal.ParseTokenCount(
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null}}", observed);
            Assert(!missing.Available, "missing usage was treated as zero or green");
        }

        private static void Check(long used, long window, DateTime observed, int percent, int level)
        {
            string line = "{\"timestamp\":\"2026-09-24T06:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":" +
                window + ",\"last_token_usage\":{\"total_tokens\":" + used + "}}}}";
            CodexContextSignal signal = CodexContextSignal.ParseTokenCount(line, observed);
            Assert(signal.Available && signal.Percent == percent && signal.Level == level, "incorrect context level");
            Assert(signal.IsRecent(observed.AddMinutes(14)), "fresh signal was hidden");
            Assert(!signal.IsRecent(observed.AddMinutes(16)), "stale signal stayed visible");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
