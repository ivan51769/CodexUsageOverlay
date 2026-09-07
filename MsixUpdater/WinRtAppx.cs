using System;
using System.Threading;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace Blues19.CodexInstaller
{
    /// <summary>
    /// 直接调用系统的 WinRT 部署接口（Windows.Management.Deployment.PackageManager）。
    ///
    /// 相比 powershell.exe + Add-AppxPackage：
    ///   · 快一个数量级（约 170ms vs 约 2200ms，省掉起 PowerShell 进程的开销）
    ///   · 拿得到真正的 HRESULT，而不是被 CLR 包装成 0x80131500 的通用 COMException
    ///   · 有安装进度回调，700MB 的包不至于让界面看起来卡死
    ///   · 不依赖 PowerShell 是否存在、是否被执行策略或杀软拦截
    ///
    /// 本类里的所有类型都来自 WinMetadata，万一某台机器上取不到（极老的系统），
    /// 类型加载失败只会发生在调用这里的那一刻，所以调用方一律用 try/catch 包住并回落到
    /// PowerShell 方案。切勿把这些类型泄漏到其它类的方法签名里。
    /// </summary>
    internal static class WinRtAppx
    {
        internal enum InUseChoice { Abort, ForceShutdown, DeferToNextExit }

        internal sealed class DeployOutcome
        {
            public bool Success;
            public uint HResult;
            public string ErrorText;
            public string ActivityId;
            public bool Deferred;
        }

        /// <summary>返回该包族当前已注册的最高版本；没装返回 null。</summary>
        internal static InstalledPackage FindInstalled(string packageFamilyName)
        {
            PackageManager pm = new PackageManager();
            InstalledPackage best = null;

            // 空字符串代表“当前用户”，这条路径不需要管理员权限。
            foreach (Windows.ApplicationModel.Package p in pm.FindPackagesForUser(string.Empty, packageFamilyName))
            {
                Windows.ApplicationModel.PackageVersion v = p.Id.Version;
                InstalledPackage item = new InstalledPackage();
                item.FullName = p.Id.FullName;
                item.Name = p.Id.Name;
                item.Version = new Version(v.Major, v.Minor, v.Build, v.Revision);
                item.Architecture = p.Id.Architecture.ToString().ToLowerInvariant();
                if (best == null || item.Version > best.Version) best = item;
            }
            return best;
        }

        /// <summary>
        /// 常规安装。forceShutdown=true 会强制关闭正在运行的 Codex 再替换文件。
        /// 注意签名里刻意不出现任何 WinRT 类型，避免调用方的方法体在加载时就依赖 WinMetadata。
        /// </summary>
        internal static DeployOutcome Install(string packagePath, bool forceShutdown,
            Action<int> onProgress, CancellationToken cancel)
        {
            PackageManager pm = new PackageManager();
            Uri uri = new Uri(packagePath);
            DeploymentOptions options = forceShutdown
                ? DeploymentOptions.ForceApplicationShutdown
                : DeploymentOptions.None;
            IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> op =
                pm.AddPackageAsync(uri, null, options);
            return Await(op, onProgress, cancel);
        }

        /// <summary>
        /// 延迟注册：包先铺到磁盘，等用户下次完全退出 Codex 时系统自动切到新版本。
        /// 专门用来化解 0x80073D02（应用正在运行）而又不强杀用户进程。
        /// </summary>
        internal static DeployOutcome InstallDeferred(string packagePath,
            Action<int> onProgress, CancellationToken cancel)
        {
            PackageManager pm = new PackageManager();
            Uri uri = new Uri(packagePath);
            AddPackageOptions options = new AddPackageOptions();
            options.DeferRegistrationWhenPackagesAreInUse = true;
            IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> op =
                pm.AddPackageByUriAsync(uri, options);
            DeployOutcome outcome = Await(op, onProgress, cancel);
            outcome.Deferred = outcome.Success;
            return outcome;
        }

        private static DeployOutcome Await(IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> op,
            Action<int> onProgress, CancellationToken cancel)
        {
            DeploymentResult result = null;
            Exception failure = null;
            using (ManualResetEventSlim done = new ManualResetEventSlim(false))
            {
                op.Progress = delegate (IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> sender,
                                        DeploymentProgress progress)
                {
                    if (onProgress == null) return;
                    try { onProgress((int)progress.percentage); }
                    catch { }
                };

                op.Completed = delegate (IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> sender,
                                         AsyncStatus status)
                {
                    try { result = sender.GetResults(); }
                    catch (Exception ex) { failure = ex; }
                    done.Set();
                };

                // 分片等待，这样取消请求能及时把部署操作停掉
                while (!done.Wait(200))
                {
                    if (cancel.IsCancellationRequested)
                    {
                        try { op.Cancel(); }
                        catch { }
                        done.Wait(15000);
                        throw new OperationCanceledException();
                    }
                }
            }

            if (failure != null) throw failure;

            DeployOutcome outcome = new DeployOutcome();
            if (result == null)
            {
                outcome.Success = false;
                outcome.ErrorText = "部署接口没有返回结果。";
                return outcome;
            }

            // 关键：部署成功时 ExtendedErrorCode 是 null，不是一个 HResult 为 0 的异常对象。
            // 直接点 .HResult 会在「安装成功」这条路径上抛 NullReferenceException，
            // 于是成功也被当成失败、白白退回 PowerShell 慢速通道。
            Exception error = result.ExtendedErrorCode;
            outcome.HResult = error != null ? unchecked((uint)error.HResult) : 0;
            outcome.Success = outcome.HResult == 0;
            outcome.ErrorText = result.ErrorText;
            try { outcome.ActivityId = result.ActivityId.ToString(); }
            catch { }
            return outcome;
        }

    }
}
