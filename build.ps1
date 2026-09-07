$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectRoot 'bin'
$appVersion = '1.4.30'
$distributionExeName = "blues19-CodexUsageUpdateAssistant-v$appVersion.exe"
$logoPath = Join-Path $projectRoot 'installer-assets\brand-logo.png'
$iconPath = Join-Path $projectRoot 'installer-assets\app-icon.ico'
$defaultCachePath = Join-Path $projectRoot 'installer-assets\usage-cache.ini'
$manifestPath = Join-Path $projectRoot 'app.manifest'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$uiAutomationClient = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll'
$uiAutomationTypes = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll'
$windowsBase = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll'
$frameworkDir = Split-Path -Parent $compiler
$winMetadataDir = Join-Path $env:WINDIR 'System32\WinMetadata'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw "Missing .NET Framework compiler: $compiler"
}
if (-not (Test-Path -LiteralPath $logoPath) -or
    -not (Test-Path -LiteralPath $iconPath) -or
    -not (Test-Path -LiteralPath $defaultCachePath) -or
    -not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Missing installer asset.'
}
if (-not (Test-Path -LiteralPath $uiAutomationClient) -or
    -not (Test-Path -LiteralPath $uiAutomationTypes) -or
    -not (Test-Path -LiteralPath $windowsBase)) {
    throw 'Missing Windows UI Automation build dependency.'
}
foreach ($required in @(
    (Join-Path $frameworkDir 'System.Runtime.dll'),
    (Join-Path $frameworkDir 'System.Runtime.WindowsRuntime.dll'),
    (Join-Path $winMetadataDir 'Windows.Management.winmd'),
    (Join-Path $winMetadataDir 'Windows.Foundation.winmd'),
    (Join-Path $winMetadataDir 'Windows.ApplicationModel.winmd'),
    (Join-Path $winMetadataDir 'Windows.Storage.winmd'),
    (Join-Path $winMetadataDir 'Windows.System.winmd')
)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Missing Windows MSIX build dependency: $required"
    }
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& $compiler /nologo /target:winexe /optimize+ /platform:anycpu `
    "/out:$outputDir\CodexUsageOverlay.exe" `
    "/win32icon:$iconPath" `
    "/win32manifest:$manifestPath" `
    "/resource:$logoPath,CodexUsageOverlay.BrandLogo.png" `
    "/resource:$logoPath,Blues19.CodexInstaller.WeChatLogo.png" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Xml.dll `
    /reference:System.Xml.Linq.dll `
    "/reference:$frameworkDir\System.Runtime.dll" `
    "/reference:$frameworkDir\System.Runtime.WindowsRuntime.dll" `
    "/reference:$winMetadataDir\Windows.Management.winmd" `
    "/reference:$winMetadataDir\Windows.Foundation.winmd" `
    "/reference:$winMetadataDir\Windows.ApplicationModel.winmd" `
    "/reference:$winMetadataDir\Windows.Storage.winmd" `
    "/reference:$winMetadataDir\Windows.System.winmd" `
    "/reference:$uiAutomationClient" `
    "/reference:$uiAutomationTypes" `
    "/reference:$windowsBase" `
    (Join-Path $projectRoot 'AssemblyInfo.cs') `
    (Join-Path $projectRoot 'UiRendering.cs') `
    (Join-Path $projectRoot 'UpdateMenuVisuals.cs') `
    (Join-Path $projectRoot 'OverlayInteraction.cs') `
    (Join-Path $projectRoot 'OutsideClickMonitor.cs') `
    (Join-Path $projectRoot 'UsageData.cs') `
    (Join-Path $projectRoot 'NativeAnalyticsService.cs') `
    (Join-Path $projectRoot 'NativeAnalyticsView.cs') `
    (Join-Path $projectRoot 'UsageTrustPolicy.cs') `
    (Join-Path $projectRoot 'GitHubReleaseUpdateService.cs') `
    (Join-Path $projectRoot 'CodexAnalysisForm.cs') `
    (Join-Path $projectRoot 'CodexMsixUpdatePanelForm.cs') `
    (Join-Path $projectRoot 'MsixUpdater\EmbeddedUpdaterHost.cs') `
    (Join-Path $projectRoot 'MsixUpdater\AppxInstaller.cs') `
    (Join-Path $projectRoot 'MsixUpdater\Downloader.cs') `
    (Join-Path $projectRoot 'MsixUpdater\Fe3Client.cs') `
    (Join-Path $projectRoot 'MsixUpdater\Glass.cs') `
    (Join-Path $projectRoot 'MsixUpdater\Http.cs') `
    (Join-Path $projectRoot 'MsixUpdater\IconFactory.cs') `
    (Join-Path $projectRoot 'MsixUpdater\InstallerUpdateChecker.cs') `
    (Join-Path $projectRoot 'MsixUpdater\Logger.cs') `
    (Join-Path $projectRoot 'MsixUpdater\MainForm.cs') `
    (Join-Path $projectRoot 'MsixUpdater\Models.cs') `
    (Join-Path $projectRoot 'MsixUpdater\ProgressPanel.cs') `
    (Join-Path $projectRoot 'MsixUpdater\Settings.cs') `
    (Join-Path $projectRoot 'MsixUpdater\StoreApi.cs') `
    (Join-Path $projectRoot 'MsixUpdater\Util.cs') `
    (Join-Path $projectRoot 'MsixUpdater\WinRtAppx.cs') `
    (Join-Path $projectRoot 'FirstRunGuideForm.cs') `
    (Join-Path $projectRoot 'Program.cs') `
    (Join-Path $projectRoot 'OverlaySettings.cs') `
    (Join-Path $projectRoot 'ResetRadarService.cs') `
    (Join-Path $projectRoot 'ResetRadarBannerForm.cs') `
    (Join-Path $projectRoot 'CodexConversationSurfaceMonitor.cs') `
    (Join-Path $projectRoot 'CodexTaskStatusMonitor.cs') `
    (Join-Path $projectRoot 'CodexAppServerClient.cs')

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath $defaultCachePath -Destination (Join-Path $outputDir 'usage-cache.ini') -Force
Copy-Item -LiteralPath (Join-Path $outputDir 'CodexUsageOverlay.exe') -Destination (Join-Path $outputDir $distributionExeName) -Force
Get-Item -LiteralPath (Join-Path $outputDir 'CodexUsageOverlay.exe')
Get-Item -LiteralPath (Join-Path $outputDir $distributionExeName)
