# Agent 一键安装部署提示词

把下面整段提示词复制给 Codex、Claude Code 或其他能够操作 Windows 的 Agent，即可让它完成环境检查、下载安装、覆盖更新和验证。

```text
请在这台 Windows 电脑上安装或更新 Codex Usage Overlay，并完成实际验证。

项目地址：
https://github.com/ivan51769/CodexUsageOverlay

最新版安装包：
https://github.com/ivan51769/CodexUsageOverlay/raw/refs/heads/main/dist/blues19-CodexUsageOverlay-Setup-1.3.3.exe

SHA-256 校验文件：
https://github.com/ivan51769/CodexUsageOverlay/raw/refs/heads/main/dist/SHA256SUMS.txt

目标：
1. 检查当前系统是否为 Windows 10/11，记录系统架构和当前用户，不要修改系统安全策略。
2. 检查 Codex 桌面应用是否已安装；再检查是否能找到 codex.exe 或 codex.cmd。
3. 使用 HTTPS 将安装包和 SHA256SUMS.txt 下载到一个新建的、范围明确的临时目录。
4. 用 Get-FileHash 计算安装包 SHA-256，并与 SHA256SUMS.txt 中同名文件的值比较；不一致时立即停止，删除本次损坏下载并报告，不要继续运行。
5. 使用安装包完成当前用户安装。安装包不需要管理员权限；支持覆盖更新。静默安装参数使用：
   /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS
6. 预期安装路径：
   %LOCALAPPDATA%\Programs\Codex Usage Overlay\CodexUsageOverlay.exe
7. 安装后启动 CodexUsageOverlay.exe，确认进程存在且 Responding=True。
8. 确认安装程序已创建桌面快捷方式和当前用户启动目录中的自动启动快捷方式。
9. 如果 Codex 当前没有打开，悬浮条隐藏属于正常行为；打开并聚焦 Codex 后再确认顶部悬浮条出现。
10. 运行安装目录中的 CodexUsageOverlay.exe --snapshot 做只读检查。只报告 CodexWindow、DataSource 和是否存在错误，不要在回复里泄露套餐额度、累计 Token、邮箱或其他账户数据。

环境与故障处理：
- 程序要求 Windows 10/11 和系统自带的 .NET Framework 4.x。
- 程序会优先寻找 Codex 桌面应用自带的 codex.exe，也支持 npm 全局安装的官方 Codex CLI。
- 如果桌面应用和 Codex CLI 都不存在，但系统已有 Node.js/npm，可安装官方 CLI：
  npm.cmd install --global @openai/codex
- 如果 Codex 尚未登录，运行：
  codex.cmd login --device-auth
  登录和设备授权必须交给用户本人完成；不要读取、回显或代填密码、令牌、设备码、验证码。
- 如需指定 CLI，使用当前用户环境变量 CODEX_CLI_PATH 指向真实 codex.exe；不要把凭据写入环境变量。
- 不要读取或修改 ~/.codex/auth.json，不要上传 ~/.codex/sessions，不要扫描或输出对话正文。
- 不要关闭或修改 Codex 本体。覆盖安装时只允许安装程序关闭旧版 CodexUsageOverlay.exe。
- 不要使用 git reset、递归删除用户目录或放宽 PowerShell/系统安全策略。

验收标准：
- 安装文件存在于预期路径；
- CodexUsageOverlay 进程正常响应；
- 桌面快捷方式和开机启动快捷方式存在；
- 聚焦 Codex 后悬浮条出现；
- 能读取 Codex 本地接口，或明确报告仍需用户完成 Codex 登录；
- 最终给出已验证结果、安装版本、安装路径和任何未完成项，不要只说“安装成功”。
```

## 手动安装命令参考

以下 PowerShell 片段适合人工审阅后执行。设备登录仍需用户本人完成。

```powershell
$downloadDir = Join-Path $env:TEMP "CodexUsageOverlay-Install"
New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null

$installer = Join-Path $downloadDir "blues19-CodexUsageOverlay-Setup-1.3.3.exe"
$checksums = Join-Path $downloadDir "SHA256SUMS.txt"

Invoke-WebRequest -UseBasicParsing `
  -Uri "https://github.com/ivan51769/CodexUsageOverlay/raw/refs/heads/main/dist/blues19-CodexUsageOverlay-Setup-1.3.3.exe" `
  -OutFile $installer
Invoke-WebRequest -UseBasicParsing `
  -Uri "https://github.com/ivan51769/CodexUsageOverlay/raw/refs/heads/main/dist/SHA256SUMS.txt" `
  -OutFile $checksums

$expected = ((Get-Content $checksums | Where-Object {
  $_ -match "blues19-CodexUsageOverlay-Setup-1.3.3.exe$"
}) -split "\s+")[0].ToLowerInvariant()
$actual = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) {
  throw "SHA-256 校验失败，已停止安装。"
}

$install = Start-Process -FilePath $installer `
  -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS" `
  -Wait -PassThru
if ($install.ExitCode -notin 0, 3010) {
  throw "安装程序返回错误码 $($install.ExitCode)"
}

$app = Join-Path $env:LOCALAPPDATA "Programs\Codex Usage Overlay\CodexUsageOverlay.exe"
if (-not (Test-Path -LiteralPath $app)) {
  throw "未在预期路径找到程序：$app"
}
Start-Process -FilePath $app
Write-Host "如主用量条旁出现首次使用指引气泡，请让用户完成或跳过后再验收悬浮条。"
Start-Sleep -Seconds 3
Get-Process -Name CodexUsageOverlay | Select-Object Id, Responding, Path
```
