using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace CodexUsageOverlay
{
    internal sealed class CodexAppServerClient : IDisposable
    {
        private const int RequestTimeoutMilliseconds = 8000;
        private readonly object gate = new object();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private Process process;
        private BlockingCollection<string> outputLines;
        private int nextRequestId = 1;
        private bool initialized;

        public string LastError { get; private set; }

        public bool TryReadUsage(out UsageData usage)
        {
            lock (gate)
            {
                usage = new UsageData();
                LastError = String.Empty;
                try
                {
                    if (!EnsureStarted())
                        return false;

                    IDictionary<string, object> account = SendRequest("account/read",
                        ObjectOf("refreshToken", false));
                    IDictionary<string, object> rateLimits = SendRequest("account/rateLimits/read", null);
                    if (!TryParseTrustedSnapshot(account, rateLimits, usage))
                    {
                        if (String.IsNullOrWhiteSpace(LastError))
                            LastError = "Codex app-server 没有返回可验证的登录账户与额度窗口";
                        return false;
                    }

                    IDictionary<string, object> tokenUsage = SendRequest("account/usage/read", null);
                    if (tokenUsage != null)
                        ParseTokenUsage(tokenUsage, usage);

                    usage.Source = "Codex CLI app-server";
                    usage.UpdatedUtc = DateTime.UtcNow;
                    return true;
                }
                catch (Exception exception)
                {
                    LastError = exception.Message;
                    ResetProcess();
                    return false;
                }
            }
        }

        private bool EnsureStarted()
        {
            if (process != null && !process.HasExited && initialized)
                return true;

            ResetProcess();
            outputLines = new BlockingCollection<string>();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = ResolveCodexExecutable();
            startInfo.Arguments = "app-server";
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            process = new Process();
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
            {
                if (eventArgs.Data == null)
                    return;
                BlockingCollection<string> queue = outputLines;
                if (queue == null)
                    return;
                try { queue.Add(eventArgs.Data); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            };
            process.ErrorDataReceived += delegate { };
            process.Exited += delegate
            {
                BlockingCollection<string> queue = outputLines;
                if (queue == null)
                    return;
                try { queue.CompleteAdding(); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            };

            try
            {
                if (!process.Start())
                {
                    LastError = "无法启动 Codex CLI";
                    ResetProcess();
                    return false;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception exception)
            {
                LastError = "无法启动 Codex CLI: " + exception.Message;
                ResetProcess();
                return false;
            }

            Dictionary<string, object> clientInfo = new Dictionary<string, object>();
            clientInfo["name"] = "codex_usage_overlay";
            clientInfo["title"] = "Codex Usage Overlay";
            clientInfo["version"] = "1.3.2";
            Dictionary<string, object> initializeParams = new Dictionary<string, object>();
            initializeParams["clientInfo"] = clientInfo;

            IDictionary<string, object> initialize = SendRequest("initialize", initializeParams);
            if (initialize == null)
            {
                ResetProcess();
                return false;
            }
            SendNotification("initialized", new Dictionary<string, object>());
            initialized = true;
            return true;
        }

        private IDictionary<string, object> SendRequest(string method, object parameters)
        {
            if (process == null || process.HasExited)
            {
                LastError = "Codex app-server 已退出";
                return null;
            }

            int requestId = nextRequestId++;
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["method"] = method;
            request["id"] = requestId;
            if (parameters != null)
                request["params"] = parameters;
            WriteMessage(request);

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(RequestTimeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                int wait = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                string line;
                if (!outputLines.TryTake(out line, wait))
                    break;
                if (String.IsNullOrWhiteSpace(line))
                    continue;

                IDictionary<string, object> message;
                try
                {
                    message = json.DeserializeObject(line) as IDictionary<string, object>;
                }
                catch
                {
                    continue;
                }
                if (message == null || !MatchesId(message, requestId))
                    continue;

                object errorValue;
                if (message.TryGetValue("error", out errorValue) && errorValue != null)
                {
                    IDictionary<string, object> error = AsObject(errorValue);
                    LastError = error != null ? ReadString(error, "message") : "Codex app-server 请求失败";
                    return null;
                }

                object resultValue;
                if (!message.TryGetValue("result", out resultValue))
                {
                    LastError = "Codex app-server 返回缺少 result";
                    return null;
                }
                return AsObject(resultValue) ?? new Dictionary<string, object>();
            }

            LastError = method + " 请求超时";
            ResetProcess();
            return null;
        }

        private void SendNotification(string method, object parameters)
        {
            Dictionary<string, object> notification = new Dictionary<string, object>();
            notification["method"] = method;
            notification["params"] = parameters;
            WriteMessage(notification);
        }

        private void WriteMessage(IDictionary<string, object> message)
        {
            process.StandardInput.WriteLine(json.Serialize(message));
            process.StandardInput.Flush();
        }

        internal static bool TryParseTrustedSnapshot(IDictionary<string, object> accountResult,
            IDictionary<string, object> rateLimitsResult, UsageData usage)
        {
            if (usage == null)
                throw new ArgumentNullException("usage");

            string accountPlan = null;
            bool accountFound = accountResult != null && ParseAccount(accountResult, out accountPlan);
            string quotaPlan = null;
            bool quotaWindowFound = rateLimitsResult != null &&
                ParseRateLimits(rateLimitsResult, usage, out quotaPlan);
            if (!UsageTrustPolicy.HasVerifiedSnapshot(accountFound, quotaWindowFound))
                return false;

            string trustedPlan = UsageTrustPolicy.SelectTrustedPlan(quotaPlan, accountPlan);
            if (UsageTrustPolicy.IsUsablePlan(trustedPlan))
            {
                usage.Plan = NormalizePlan(trustedPlan);
                usage.HasPlan = true;
            }
            return true;
        }

        internal static bool ParseAccount(IDictionary<string, object> result, out string accountPlan)
        {
            accountPlan = null;
            object accountValue;
            if (!result.TryGetValue("account", out accountValue))
                return false;
            IDictionary<string, object> account = AsObject(accountValue);
            if (account == null)
                return false;
            accountPlan = ReadString(account, "planType");
            return UsageTrustPolicy.HasAuthenticatedAccount(ReadString(account, "type"));
        }

        internal static bool ParseRateLimits(IDictionary<string, object> result, UsageData usage,
            out string quotaPlan)
        {
            quotaPlan = null;
            object limitsValue;
            if (!result.TryGetValue("rateLimits", out limitsValue))
                return false;
            IDictionary<string, object> limits = AsObject(limitsValue);
            if (limits == null)
                return false;

            bool found = false;
            IDictionary<string, object> primary = ReadObject(limits, "primary");
            IDictionary<string, object> secondary = ReadObject(limits, "secondary");
            string primaryPlan;
            string secondaryPlan;
            found |= ParseQuotaWindow(primary, usage, out primaryPlan);
            found |= ParseQuotaWindow(secondary, usage, out secondaryPlan);
            if (!found)
                return false;

            quotaPlan = ReadString(limits, "planType");
            if (!UsageTrustPolicy.IsUsablePlan(quotaPlan))
                quotaPlan = UsageTrustPolicy.SelectTrustedPlan(secondaryPlan, primaryPlan);

            object reachedValue;
            if (limits.TryGetValue("rateLimitReachedType", out reachedValue))
            {
                usage.RateLimitStatus = NormalizeRateLimitStatus(reachedValue == null ? null :
                    Convert.ToString(reachedValue, CultureInfo.InvariantCulture));
                usage.HasRateLimitStatus = true;
            }

            IDictionary<string, object> resetCredits = ReadObject(result, "rateLimitResetCredits");
            long availableCount;
            if (resetCredits != null && TryReadLong(resetCredits, "availableCount", out availableCount) && availableCount >= 0)
            {
                usage.AvailableResetCredits = (int)Math.Min(Int32.MaxValue, availableCount);
                usage.HasAvailableResetCredits = true;
            }

            return true;
        }

        internal static bool ParseQuotaWindow(IDictionary<string, object> window, UsageData usage,
            out string windowPlan)
        {
            windowPlan = null;
            if (window == null)
                return false;

            long durationMinutes;
            bool hasDuration = TryReadLong(window, "windowDurationMins", out durationMinutes);
            double usedPercent;
            bool hasUsedPercent = TryReadDouble(window, "usedPercent", out usedPercent);
            long resetsAt;
            bool hasResetAt = TryReadLong(window, "resetsAt", out resetsAt);
            if (!hasDuration || !UsageTrustPolicy.IsRealQuotaWindow(durationMinutes,
                hasUsedPercent, usedPercent, hasResetAt, resetsAt))
                return false;

            windowPlan = ReadString(window, "planType");
            bool shortWindow = durationMinutes < 1440;
            if (hasUsedPercent && !Double.IsNaN(usedPercent) &&
                !Double.IsInfinity(usedPercent) && usedPercent >= 0d)
            {
                int remaining = (int)Math.Floor(100d - usedPercent + 0.0001d);
                remaining = Math.Max(0, Math.Min(100, remaining));
                if (shortWindow)
                {
                    usage.ShortRemaining = remaining;
                    usage.HasShortRemaining = true;
                }
                else
                {
                    usage.WeeklyRemaining = remaining;
                    usage.HasWeeklyRemaining = true;
                }
            }

            if (hasResetAt && resetsAt > 0)
            {
                DateTime resetLocal = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(resetsAt).ToLocalTime();
                string resetText = shortWindow && resetLocal.Date == DateTime.Now.Date
                    ? resetLocal.ToString("HH:mm", CultureInfo.CurrentCulture)
                    : resetLocal.ToString("M月d日 HH:mm", CultureInfo.CurrentCulture);
                if (shortWindow)
                {
                    usage.ShortResetText = resetText;
                    usage.HasShortResetText = true;
                }
                else
                {
                    usage.WeeklyResetText = resetText;
                    usage.HasWeeklyResetText = true;
                }
            }
            return true;
        }

        private static string NormalizeRateLimitStatus(string reachedType)
        {
            if (String.IsNullOrWhiteSpace(reachedType) ||
                String.Equals(reachedType, "none", StringComparison.OrdinalIgnoreCase))
                return "正常";

            string normalized = reachedType.Trim().ToLowerInvariant();
            if (normalized.Contains("primary")) return "短期受限";
            if (normalized.Contains("secondary")) return "周额度受限";
            if (normalized.Contains("credit")) return "积分不足";
            return reachedType.Trim();
        }

        private static bool ParseTokenUsage(IDictionary<string, object> result, UsageData usage)
        {
            IDictionary<string, object> summary = ReadObject(result, "summary");
            long lifetimeTokens;
            if (summary == null || !TryReadLong(summary, "lifetimeTokens", out lifetimeTokens) || lifetimeTokens < 0)
                return false;
            usage.LifetimeTokens = lifetimeTokens;
            usage.ProfileTokensText = FormatLifetimeTokens(lifetimeTokens);
            return true;
        }

        internal static string FormatLifetimeTokens(long value)
        {
            if (value >= 100000000L)
                return (value / 100000000d).ToString("0.#", CultureInfo.InvariantCulture) + "亿";
            if (value >= 10000L)
                return (value / 10000d).ToString("0.#", CultureInfo.InvariantCulture) + "万";
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string NormalizePlan(string plan)
        {
            if (String.Equals(plan, "pro", StringComparison.OrdinalIgnoreCase)) return "Pro";
            if (String.Equals(plan, "plus", StringComparison.OrdinalIgnoreCase)) return "Plus";
            if (String.Equals(plan, "free", StringComparison.OrdinalIgnoreCase)) return "Free";
            if (String.Equals(plan, "team", StringComparison.OrdinalIgnoreCase)) return "Team";
            return plan.Trim();
        }

        private static Dictionary<string, object> ObjectOf(string key, object value)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result[key] = value;
            return result;
        }

        private static IDictionary<string, object> AsObject(object value)
        {
            return value as IDictionary<string, object>;
        }

        private static IDictionary<string, object> ReadObject(IDictionary<string, object> source, string key)
        {
            if (source == null)
                return null;
            object value;
            return source.TryGetValue(key, out value) ? AsObject(value) : null;
        }

        private static string ReadString(IDictionary<string, object> source, string key)
        {
            if (source == null)
                return null;
            object value;
            return source.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static bool HasNumber(IDictionary<string, object> source, string key)
        {
            double ignored;
            return TryReadDouble(source, key, out ignored);
        }

        private static bool TryReadDouble(IDictionary<string, object> source, string key, out double value)
        {
            value = 0;
            if (source == null)
                return false;
            object raw;
            if (!source.TryGetValue(key, out raw) || raw == null)
                return false;
            return Double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadLong(IDictionary<string, object> source, string key, out long value)
        {
            value = 0;
            if (source == null)
                return false;
            object raw;
            if (!source.TryGetValue(key, out raw) || raw == null)
                return false;
            if (raw is long) { value = (long)raw; return true; }
            if (raw is int) { value = (int)raw; return true; }
            return Int64.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool MatchesId(IDictionary<string, object> message, int requestId)
        {
            object id;
            if (!message.TryGetValue("id", out id) || id == null)
                return false;
            int parsed;
            return Int32.TryParse(Convert.ToString(id, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed == requestId;
        }

        private static string ResolveCodexExecutable()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
            if (!String.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            string besideOverlay = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "codex.exe");
            if (File.Exists(besideOverlay))
                return besideOverlay;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] npmCandidates = new[]
            {
                Path.Combine(appData, "npm", "node_modules", "@openai", "codex", "node_modules",
                    "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe"),
                Path.Combine(appData, "npm", "node_modules", "@openai", "codex", "node_modules",
                    "@openai", "codex-win32-arm64", "vendor", "aarch64-pc-windows-msvc", "bin", "codex.exe")
            };
            foreach (string npmCandidate in npmCandidates)
            {
                if (File.Exists(npmCandidate))
                    return npmCandidate;
            }

            string desktopCliRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(desktopCliRoot))
                {
                    string[] versions = Directory.GetDirectories(desktopCliRoot);
                    Array.Sort(versions, StringComparer.OrdinalIgnoreCase);
                    for (int index = versions.Length - 1; index >= 0; index--)
                    {
                        string candidate = Path.Combine(versions[index], "codex.exe");
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
            catch { }

            string path = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            foreach (string folder in path.Split(Path.PathSeparator))
            {
                string cleanFolder = folder.Trim().Trim('"');
                if (cleanFolder.Length == 0)
                    continue;
                try
                {
                    string candidate = Path.Combine(cleanFolder, "codex.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }

            foreach (Process chatGpt in Process.GetProcessesByName("ChatGPT"))
            {
                try
                {
                    string appFolder = Path.GetDirectoryName(chatGpt.MainModule.FileName);
                    string candidate = Path.Combine(appFolder, "resources", "codex.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
                finally { chatGpt.Dispose(); }
            }
            return "codex.exe";
        }

        private void ResetProcess()
        {
            initialized = false;
            Process current = process;
            process = null;
            if (current != null)
            {
                try { current.StandardInput.Close(); }
                catch { }
                try
                {
                    if (!current.HasExited)
                    {
                        current.Kill();
                        current.WaitForExit(1000);
                    }
                }
                catch { }
                current.Dispose();
            }
            if (outputLines != null)
            {
                try { outputLines.CompleteAdding(); }
                catch { }
                outputLines.Dispose();
                outputLines = null;
            }
        }

        public void Dispose()
        {
            lock (gate)
                ResetProcess();
        }
    }
}
