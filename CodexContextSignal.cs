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
                return new CodexContextSignal { UsedTokens = used, WindowTokens = window,
                    ObservedAt = observed, SourceWriteUtc = sourceWriteUtc };
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
