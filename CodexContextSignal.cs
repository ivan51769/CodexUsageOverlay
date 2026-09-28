using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace CodexUsageOverlay
{
    internal sealed class CodexContextSignal
    {
        internal static readonly CodexContextSignal Empty = new CodexContextSignal();
        internal long UsedTokens;
        internal long WindowTokens;
        internal long InputTokens;
        internal long CachedInputTokens;
        internal long OutputTokens;
        internal bool HasSessionUsage;
        internal long SessionTokens, SessionInputTokens, SessionCachedTokens, SessionOutputTokens;
        internal double? CacheHitPercent
        {
            get { return HasSessionUsage && SessionInputTokens > 0
                ? (double?)(100d * SessionCachedTokens / SessionInputTokens) : null; }
        }
        internal string CacheHitText
        {
            get { return CacheHitPercent.HasValue ? Math.Round(CacheHitPercent.Value, 0, MidpointRounding.AwayFromZero)
                .ToString("0", CultureInfo.InvariantCulture) + "%" : "—"; }
        }
        internal DateTimeOffset ObservedAt;
        internal DateTime SourceWriteUtc;
        internal string SourcePath = String.Empty;

        internal bool Available { get { return WindowTokens > 0 && UsedTokens > 0; } }
        internal int Percent { get { return Available ? (int)Math.Min(100,
            Math.Floor(100d * UsedTokens / WindowTokens)) : 0; } }
        internal int Level
        {
            get
            {
                if (!Available) return -1;
                double rawPercent = 100d * UsedTokens / WindowTokens;
                return rawPercent >= 85d ? 2 : rawPercent >= 70d ? 1 : 0;
            }
        }
        internal bool IsRecent(DateTime nowUtc)
        {
            DateTime observedUtc = ObservedAt.UtcDateTime;
            return Available && observedUtc > nowUtc.AddMinutes(-15) && observedUtc <= nowUtc.AddMinutes(2);
        }

        internal static CodexContextSignal ParseTokenCount(string line, DateTime sourceWriteUtc)
        {
            try
            {
                var root = new JavaScriptSerializer().DeserializeObject(line) as IDictionary<string, object>;
                if (Text(root, "type") != "event_msg") return Empty;
                var payload = Object(root, "payload");
                if (Text(payload, "type") != "token_count") return Empty;
                var info = Object(payload, "info");
                var last = Object(info, "last_token_usage");
                long used, window;
                if (!Number(last, "total_tokens", out used) || !Number(info, "model_context_window", out window) ||
                    used <= 0 || window <= 0 || used > window * 100L)
                    return Empty;
                DateTimeOffset observed;
                if (!DateTimeOffset.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out observed))
                    return Empty;
                long input, cached, output;
                Number(last, "input_tokens", out input);
                Number(last, "cached_input_tokens", out cached);
                Number(last, "output_tokens", out output);
                var total = Object(info, "total_token_usage");
                long sessionTokens, sessionInput, sessionCached, sessionOutput;
                bool hasTotal = Number(total, "total_tokens", out sessionTokens);
                bool hasInput = Number(total, "input_tokens", out sessionInput);
                bool hasCached = Number(total, "cached_input_tokens", out sessionCached);
                bool hasOutput = Number(total, "output_tokens", out sessionOutput);
                bool validSession = hasTotal && hasInput && hasCached && hasOutput && sessionTokens >= 0 &&
                    sessionInput >= 0 && sessionCached >= 0 && sessionCached <= sessionInput && sessionOutput >= 0;
                return new CodexContextSignal { UsedTokens = used, WindowTokens = window,
                    InputTokens = Math.Max(0, input), CachedInputTokens = Math.Max(0, cached),
                    OutputTokens = Math.Max(0, output), ObservedAt = observed,
                    HasSessionUsage = validSession, SessionTokens = sessionTokens,
                    SessionInputTokens = sessionInput, SessionCachedTokens = sessionCached,
                    SessionOutputTokens = sessionOutput,
                    SourceWriteUtc = sourceWriteUtc };
            }
            catch { return Empty; }
        }

        private static IDictionary<string, object> Object(IDictionary<string, object> root, string key)
        {
            object value;
            return root != null && root.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
        }
        private static string Text(IDictionary<string, object> root, string key)
        {
            object value;
            return root != null && root.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) : String.Empty;
        }
        private static bool Number(IDictionary<string, object> root, string key, out long number)
        {
            return Int64.TryParse(Text(root, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
        }
    }
}
