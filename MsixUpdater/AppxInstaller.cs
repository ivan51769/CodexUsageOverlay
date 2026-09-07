using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Blues19.CodexInstaller
{
    public sealed class InstalledPackage
    {
        public string FullName;
        public string Name;
        public Version Version;
        public string Architecture;

        public override string ToString()
        {
            return Name + " " + (Version != null ? Version.ToString() : "?") + " (" + Architecture + ")";
        }
    }

    /// <summary>当 Codex 正在运行、系统拒绝替换文件时，让调用方决定怎么办。</summary>
    public enum InUseDecision { Abort, ForceShutdown, DeferToNextExit }

    public static class AppxInstaller
    {
        private const string RepositoryKey =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        // ---------------- 版本检测 ----------------

        /// <summary>
        /// 三级回退读取已安装版本：
        ///   1. WinRT PackageManager —— 权威、约 170ms，只返回真正已注册的包
        ///   2. 注册表 Repository    —— 约 5ms，但会把残留的旧条目一起报出来，只能取最大值
        ///   3. powershell.exe       —— 约 2s，最后的兜底
        /// </summary>
        public static InstalledPackage GetInstalled(Logger log)
        {
            try
            {
                InstalledPackage pkg = WinRtAppx.FindInstalled(CodexProduct.PackageFamilyName);
                if (pkg != null) return pkg;
                // WinRT 通道能用且明确回答“没装”，就不必再查注册表了
                return null;
            }
            catch (Exception ex)
            {
                if (log != null) log.Warn("系统部署接口不可用（" + ex.GetType().Name + ": " + ex.Message + "），改用注册表检测。");
            }

            try
            {
                InstalledPackage pkg = FromRegistry();
                if (pkg != null) return pkg;
            }
            catch (Exception ex)
            {
                if (log != null) log.Warn("读取注册表失败：" + ex.Message);
            }

            try
            {
                return FromPowerShell();
            }
            catch (Exception ex)
            {
                if (log != null) log.Warn("读取已安装版本失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 注册表里同一个包族会同时留着已卸载/已过期的暂存条目（实测 289 条里只有 147 条是真的注册着的），
        /// 所以这里只能取版本号最大的那条，且仅作为 WinRT 不可用时的降级手段。
        /// </summary>
        private static InstalledPackage FromRegistry()
        {
            InstalledPackage best = null;
            string suffix = "__" + CodexProduct.PublisherId;

            foreach (RegistryKey root in EnumRoots())
            {
                if (root == null) continue;
                try
                {
                    using (RegistryKey key = root.OpenSubKey(RepositoryKey))
                    {
                        if (key == null) continue;
                        foreach (string sub in key.GetSubKeyNames())
                        {
                            if (!sub.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                            InstalledPackage pkg = ParseFullName(sub);
                            if (pkg == null) continue;
                            if (!string.Equals(pkg.Name, CodexProduct.PackageName, StringComparison.OrdinalIgnoreCase)) continue;
                            if (best == null || pkg.Version > best.Version) best = pkg;
                        }
                    }
                }
                catch { }
                finally
                {
                    if (!ReferenceEquals(root, Registry.CurrentUser))
                    {
                        try { root.Close(); }
                        catch { }
                    }
                }
            }
            return best;
        }

        private static IEnumerable<RegistryKey> EnumRoots()
        {
            yield return Registry.CurrentUser;
            RegistryKey machine = null;
            try { machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }
            catch { }
            if (machine != null) yield return machine;
        }

        /// <summary>
        /// 解析 PackageFullName：Name_Version_Arch_ResourceId__PublisherId。
        /// ResourceId 通常为空，所以发布者前面是两个下划线，publisher 落在索引 4 而不是 3。
        /// </summary>
        internal static InstalledPackage ParseFullName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            string[] parts = fullName.Split('_');
            if (parts.Length < 5) return null;

            Version version;
            if (!Version.TryParse(parts[1], out version)) return null;

            InstalledPackage pkg = new InstalledPackage();
            pkg.FullName = fullName;
            pkg.Name = parts[0];
            pkg.Version = version;
            pkg.Architecture = parts[2];
            return pkg;
        }

        private static InstalledPackage FromPowerShell()
        {
            string script =
                "$p = Get-AppxPackage -Name $env:CODEX_PKG_NAME | Sort-Object Version -Descending | Select-Object -First 1; " +
                "if ($p) { Write-Output $p.PackageFullName }";
            Dictionary<string, string> env = new Dictionary<string, string>();
            env["CODEX_PKG_NAME"] = CodexProduct.PackageName;

            ProcessResult result = RunPowerShell(script, env, 60000, null, CancellationToken.None);
            foreach (string line in result.StdOut.Split('\n'))
            {
                InstalledPackage pkg = ParseFullName(line.Trim());
                if (pkg != null) return pkg;
            }
            return null;
        }

        // ---------------- 安装 ----------------

        /// <summary>
        /// 安装 msix/msixbundle。onInUse 在遇到“应用正在运行”时被调用，由界面询问用户怎么处理。
        /// 返回 true 表示已延迟到下次退出 Codex 时生效。
        /// </summary>
        public static bool Install(string packagePath, Logger log, Action<int> onProgress,
            Func<InUseDecision> onInUse, CancellationToken cancel)
        {
            if (!File.Exists(packagePath))
                throw new FileNotFoundException("安装包不存在：" + packagePath);

            CheckPolicy(log);

            bool winrtUsable = true;
            try
            {
                return InstallViaWinRt(packagePath, log, onProgress, onInUse, cancel);
            }
            catch (OperationCanceledException) { throw; }
            catch (AppxDeployException) { throw; }
            catch (Exception ex)
            {
                winrtUsable = false;
                log.Warn("系统部署接口调用失败（" + ex.GetType().Name + "：" + ex.Message + "），改用 PowerShell 安装。");
            }

            if (!winrtUsable)
            {
                InstallViaPowerShell(packagePath, log, cancel);
            }
            return false;
        }

        private static bool InstallViaWinRt(string packagePath, Logger log, Action<int> onProgress,
            Func<InUseDecision> onInUse, CancellationToken cancel)
        {
            log.Info("正在通过系统部署接口安装…");
            WinRtAppx.DeployOutcome outcome = WinRtAppx.Install(packagePath, false, onProgress, cancel);

            if (outcome.Success)
            {
                log.Success("系统部署接口报告安装成功。");
                return false;
            }

            // 0x80073D02：Codex 正在运行，系统不能替换正在使用的文件。这是现实中最常见的失败。
            if (outcome.HResult == 0x80073D02 || outcome.HResult == 0x80073D19)
            {
                log.Warn("Codex 正在运行，系统无法直接替换文件。");
                InUseDecision decision = onInUse != null ? onInUse() : InUseDecision.Abort;

                if (decision == InUseDecision.ForceShutdown)
                {
                    log.Step("正在关闭 Codex 并重新安装…");
                    WinRtAppx.DeployOutcome retry = WinRtAppx.Install(packagePath, true, onProgress, cancel);
                    if (retry.Success)
                    {
                        log.Success("已关闭正在运行的 Codex 并完成安装。");
                        return false;
                    }
                    throw Explain(retry);
                }

                if (decision == InUseDecision.DeferToNextExit)
                {
                    log.Step("正在铺设新版本，等待 Codex 下次退出后自动生效…");
                    WinRtAppx.DeployOutcome deferred = WinRtAppx.InstallDeferred(packagePath, onProgress, cancel);
                    if (deferred.Success)
                    {
                        log.Success("新版本已就位。完全退出 Codex 后再次打开即为新版本。");
                        return true;
                    }
                    throw Explain(deferred);
                }

                throw new OperationCanceledException();
            }

            throw Explain(outcome);
        }

        private static AppxDeployException Explain(WinRtAppx.DeployOutcome outcome)
        {
            string hr = "0x" + outcome.HResult.ToString("X8");
            StringBuilder sb = new StringBuilder();
            sb.Append("安装失败（").Append(hr).Append("）。");

            string hint = ExplainHResult(outcome.HResult);
            if (hint.Length > 0) sb.Append(hint);

            if (!string.IsNullOrEmpty(outcome.ErrorText))
                sb.Append(" 系统原文：").Append(outcome.ErrorText.Trim());

            AppxDeployException ex = new AppxDeployException(sb.ToString());
            ex.HResult2 = outcome.HResult;
            ex.ActivityId = outcome.ActivityId;
            return ex;
        }

        private static void InstallViaPowerShell(string packagePath, Logger log, CancellationToken cancel)
        {
            // 路径经环境变量传入，PowerShell 按变量取值而不是拼进命令串，
            // 这样路径里出现引号、分号、反引号也不会被当成命令执行。
            string script =
                "$ErrorActionPreference='Stop'; " +
                "try { Add-AppxPackage -Path $env:CODEX_PKG_PATH -ErrorAction Stop; Write-Output 'CODEX_OK' } " +
                "catch { Write-Output ('CODEX_ERR:' + $_.Exception.Message); exit 1 }";

            Dictionary<string, string> env = new Dictionary<string, string>();
            env["CODEX_PKG_PATH"] = packagePath;

            ProcessResult result = RunPowerShell(script, env, 30 * 60 * 1000,
                delegate (string line) { log.Info("  " + line); }, cancel);

            string combined = result.StdOut + "\n" + result.StdErr;
            if (result.ExitCode == 0 && combined.IndexOf("CODEX_OK", StringComparison.Ordinal) >= 0)
            {
                log.Success("PowerShell 报告安装成功。");
                return;
            }

            // PowerShell 抛出的 Exception.HResult 恒为 0x80131500（通用 COM 异常），
            // 真正的部署错误码只出现在消息文本里，只能正则捞出来。
            uint hr = 0;
            Match m = Regex.Match(combined, @"HRESULT[:\s]+0x([0-9A-Fa-f]{8})");
            if (m.Success) hr = Convert.ToUInt32(m.Groups[1].Value, 16);

            StringBuilder sb = new StringBuilder("安装失败");
            if (hr != 0) sb.Append("（0x").Append(hr.ToString("X8")).Append("）");
            sb.Append("。");
            string hint = ExplainHResult(hr);
            if (hint.Length > 0) sb.Append(hint);

            string detail = FirstErrorLine(combined);
            if (detail.Length > 0) sb.Append(" 系统原文：").Append(detail);

            AppxDeployException ex = new AppxDeployException(sb.ToString());
            ex.HResult2 = hr;
            throw ex;
        }

        private static string FirstErrorLine(string text)
        {
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("CODEX_ERR:", StringComparison.Ordinal))
                    return line.Substring(10).Trim();
            }
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > 0 && line != "CODEX_OK") return line;
            }
            return string.Empty;
        }

        /// <summary>把 AppX 部署错误码翻译成用户能照着做的处理建议。</summary>
        internal static string ExplainHResult(uint hr)
        {
            switch (hr)
            {
                case 0x80073CF0:
                    return "安装包打不开，通常是文件没下完或已损坏。请勾选“强制重新下载”后重试。";
                case 0x80073CF3:
                    return "依赖校验没过：系统版本可能低于 Windows 10 2004（内部版本 19041）。";
                case 0x80073CF6:
                    return "系统的应用数据库出现异常，重启电脑后再试。";
                case 0x80073CF8:
                    return "暂存会话被占用，通常是 Windows 更新正在同时安装东西。稍后重试即可。";
                case 0x80073CF9:
                    return "注册阶段失败，多见于用户配置文件权限异常或磁盘空间不足。重启后重试。";
                case 0x80073CFA:
                    return "取消注册旧版本时失败。可先手动卸载 Codex 再安装。";
                case 0x80073CFB:
                    return "系统里已有完全相同的版本，无需重复安装。";
                case 0x80073CFD:
                    return "安装包与当前系统架构或系统版本不匹配。";
                case 0x80073D01:
                case 0x80073D02:
                case 0x80073D19:
                    return "Codex 正在运行，请完全退出（含托盘图标）后重试。";
                case 0x80073D06:
                    return "系统里已安装了更高版本，拒绝降级安装。";
                case 0x80073D0A:
                    return "磁盘空间不足，无法完成安装。";
                case 0x8007000D:
                    return "安装包内容无效，多半是下载不完整。请重新下载。";
                case 0x80070005:
                    return "权限不足。请确认安装包放在本地磁盘（不要放在网络盘或受保护目录）。";
                case 0x80070032:
                    return "当前系统不支持该操作。";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// 只在“确实被策略禁掉”时提醒。Windows 10 1803 之后，安装签名正常的 MSIX
        /// 默认就是允许的，注册表里没有这些值属于正常状态，不能当成异常吓唬用户。
        /// </summary>
        private static void CheckPolicy(Logger log)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Appx"))
                {
                    if (key != null)
                    {
                        object allow = key.GetValue("AllowAllTrustedApps");
                        if (allow is int && (int)allow == 0)
                            log.Warn("组策略已禁止安装非商店应用（AllowAllTrustedApps=0），安装很可能失败。请联系管理员放开该策略。");

                        object blockNonAdmin = key.GetValue("BlockNonAdminUserInstall");
                        if (blockNonAdmin is int && (int)blockNonAdmin == 1)
                            log.Warn("组策略禁止非管理员安装应用，请用管理员账号运行本工具。");
                    }
                }

                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\AppXSvc"))
                {
                    if (key == null)
                    {
                        log.Warn("系统中未找到 AppX 部署服务（AppXSvc）。");
                    }
                    else
                    {
                        object start = key.GetValue("Start");
                        if (start is int && (int)start == 4)
                            log.Warn("AppX 部署服务（AppXSvc）已被禁用，安装必定失败。请用管理员终端执行：sc config AppXSvc start= demand");
                    }
                }
            }
            catch { }
        }

        // ---------------- PowerShell 调用 ----------------

        public sealed class ProcessResult
        {
            public int ExitCode;
            public string StdOut = string.Empty;
            public string StdErr = string.Empty;
            public bool TimedOut;
        }

        public static ProcessResult RunPowerShell(string script, Dictionary<string, string> env,
            int timeoutMs, Action<string> onLine, CancellationToken cancel)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + QuoteArg(script);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Paths.BaseDir;

            if (env != null)
            {
                foreach (KeyValuePair<string, string> kv in env)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            }

            ProcessResult result = new ProcessResult();
            StringBuilder outBuf = new StringBuilder();
            StringBuilder errBuf = new StringBuilder();

            using (Process proc = new Process())
            {
                proc.StartInfo = psi;
                proc.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (outBuf) outBuf.Append(e.Data).Append('\n');
                    if (onLine != null && e.Data.Trim().Length > 0)
                    {
                        try { onLine(e.Data.Trim()); }
                        catch { }
                    }
                };
                proc.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (errBuf) errBuf.Append(e.Data).Append('\n');
                    if (onLine != null && e.Data.Trim().Length > 0)
                    {
                        try { onLine(e.Data.Trim()); }
                        catch { }
                    }
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                int waited = 0;
                const int slice = 200;
                while (!proc.WaitForExit(slice))
                {
                    waited += slice;
                    if (cancel.IsCancellationRequested)
                    {
                        TryKill(proc);
                        throw new OperationCanceledException();
                    }
                    if (timeoutMs > 0 && waited >= timeoutMs)
                    {
                        TryKill(proc);
                        result.TimedOut = true;
                        break;
                    }
                }
                // 无参重载会等异步读取管道排空，但进程被强杀后管道可能永远不闭合，
                // 那样会把工作线程永久挂住。给它一个上限。
                if (!proc.WaitForExit(10000)) TryKill(proc);

                result.ExitCode = result.TimedOut ? -1 : proc.ExitCode;
            }

            lock (outBuf) result.StdOut = outBuf.ToString();
            lock (errBuf) result.StdErr = errBuf.ToString();
            if (result.TimedOut) result.StdErr += "\n操作超时，已终止 PowerShell 进程。";
            return result;
        }

        private static void TryKill(Process proc)
        {
            try { if (!proc.HasExited) proc.Kill(); }
            catch { }
        }

        /// <summary>按 CreateProcess 的转义规则把整段脚本包成一个参数。</summary>
        internal static string QuoteArg(string value)
        {
            if (value == null) value = string.Empty;
            StringBuilder sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    backslashes = 0;
                    sb.Append('"');
                    continue;
                }
                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }

    public sealed class AppxDeployException : Exception
    {
        public uint HResult2;
        public string ActivityId;
        public AppxDeployException(string message) : base(message) { }
    }
}
