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
            CodexContextSignal detailed = CodexContextSignal.ParseTokenCount(
                "{\"timestamp\":\"2026-09-24T06:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":114000,\"input_tokens\":90000,\"cached_input_tokens\":70000,\"output_tokens\":24000}}}}", observed);
            Assert(detailed.InputTokens == 90000 && detailed.CachedInputTokens == 70000 &&
                detailed.OutputTokens == 24000, "context card details did not use native token counts");
            Assert(!detailed.HasSessionUsage && detailed.CacheHitText == "—", "missing cumulative fields must not become fake zeroes");
            string cumulative = "{\"timestamp\":\"2026-09-24T06:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":100000},\"total_token_usage\":{\"total_tokens\":1010000,\"input_tokens\":1000000,\"cached_input_tokens\":990000,\"output_tokens\":10000}}}}";
            var session = CodexContextSignal.ParseTokenCount(cumulative, observed);
            Assert(session.HasSessionUsage && session.SessionTokens == 1010000 && session.CacheHitText == "99%",
                "session tokens or cache hit ratio incorrect");
            Assert(session.SessionInputTokens - session.SessionCachedTokens == 10000, "cache must not be added to input twice");
            var invalid = CodexContextSignal.ParseTokenCount(cumulative.Replace("990000", "1000001"), observed);
            Assert(!invalid.HasSessionUsage && invalid.CacheHitText == "—", "cache greater than input must be unavailable");
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
