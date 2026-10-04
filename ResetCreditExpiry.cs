using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodexUsageOverlay
{
    internal static class ResetCreditExpiry
    {
        internal static string BuildLabel(UsageData usage, DateTime utcNow, bool compact = false)
        {
            if (usage == null || !usage.AvailableResetCredits.HasValue) return String.Empty;
            string label = (compact ? "券" : "重置券：") + usage.AvailableResetCredits.Value.ToString(CultureInfo.InvariantCulture);
            if (usage.AvailableResetCredits <= 0 || !usage.ResetCreditsExpireUtc.HasValue) return label;
            TimeSpan remaining = usage.ResetCreditsExpireUtc.Value - utcNow;
            return label + (remaining <= TimeSpan.Zero ? " · 最近已到期" :
                " · 最早" + FormatRemaining(remaining) + "到期");
        }

        internal static string FormatRemaining(TimeSpan remaining)
        {
            if (remaining.TotalSeconds < 60) return "不足1分";
            if (remaining.TotalDays >= 1)
                return remaining.Days.ToString(CultureInfo.InvariantCulture) + "天" +
                    (remaining.Hours > 0 ? remaining.Hours.ToString(CultureInfo.InvariantCulture) + "时" : String.Empty);
            int minutes = (int)Math.Ceiling(remaining.TotalMinutes);
            return (minutes >= 60 ? (minutes / 60).ToString(CultureInfo.InvariantCulture) + "时" : String.Empty) +
                (minutes % 60 > 0 ? (minutes % 60).ToString(CultureInfo.InvariantCulture) + "分" : String.Empty);
        }
    }

    internal sealed class ResetCreditExpiryReminder
    {
        private readonly string path;
        private readonly List<string> notified = new List<string>();

        internal ResetCreditExpiryReminder(string statePath)
        {
            path = statePath;
            try
            {
                if (!String.IsNullOrEmpty(path) && File.Exists(path))
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                        if (line.Length > 0 && line.Length < 40) Remember(line);
            }
            catch { } // Keep in-memory de-duplication when the local state cannot be read.
        }

        internal bool TryCreateNotification(UsageData usage, DateTime utcNow, out ResetRadarNotification notification)
        {
            notification = null;
            if (usage == null || usage.AvailableResetCredits.GetValueOrDefault() <= 0 ||
                !usage.ResetCreditsExpireUtc.HasValue || usage.ResetCreditsExpiringCount <= 0 ||
                usage.Source != "Codex CLI app-server" || !String.IsNullOrEmpty(usage.LastError) ||
                usage.UpdatedUtc > utcNow || utcNow - usage.UpdatedUtc > TimeSpan.FromMinutes(5)) return false;
            DateTime expiry = usage.ResetCreditsExpireUtc.Value;
            TimeSpan remaining = expiry - utcNow;
            if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromHours(24)) return false;
            int stage = remaining <= TimeSpan.FromHours(1) ? 1 : 24;
            string prefix = expiry.Ticks.ToString(CultureInfo.InvariantCulture) + ":";
            string key = prefix + stage.ToString(CultureInfo.InvariantCulture);
            if (notified.Contains(key)) return false;
            int count = Math.Min(usage.ResetCreditsExpiringCount, usage.AvailableResetCredits.Value);
            notification = new ResetRadarNotification {
                Title = "重置券即将过期",
                Body = count.ToString(CultureInfo.InvariantCulture) + "张重置券剩余" +
                    ResetCreditExpiry.FormatRemaining(remaining) + "，将于" +
                    expiry.ToLocalTime().ToString("M月d日 HH:mm", CultureInfo.InvariantCulture) +
                    "到期（本机时间）。请在 Codex 中及时使用。",
                SourceUrl = String.Empty
            };
            Remember(key);
            if (stage == 1) Remember(prefix + "24");
            try
            {
                if (!String.IsNullOrEmpty(path))
                {
                    string temporary = path + ".tmp";
                    File.WriteAllLines(temporary, notified.ToArray(), new UTF8Encoding(false));
                    File.Copy(temporary, path, true);
                    File.Delete(temporary);
                }
            }
            catch { } // The current process still suppresses duplicates if storage is unavailable.
            return true;
        }

        private void Remember(string key)
        {
            if (!notified.Contains(key)) notified.Add(key);
            while (notified.Count > 64) notified.RemoveAt(0);
        }
    }
}
