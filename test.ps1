$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectRoot 'tests\bin'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$uiAutomationClient = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll'
$uiAutomationTypes = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll'
$windowsBase = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll'

if (-not (Test-Path -LiteralPath $uiAutomationClient) -or
    -not (Test-Path -LiteralPath $uiAutomationTypes) -or
    -not (Test-Path -LiteralPath $windowsBase)) {
    throw 'Missing Windows UI Automation build dependency.'
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& $compiler /nologo /target:exe /optimize+ /platform:anycpu `
    "/out:$outputDir\ResetRadarTests.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Windows.Forms.dll `
    "/reference:$uiAutomationClient" `
    "/reference:$uiAutomationTypes" `
    "/reference:$windowsBase" `
    (Join-Path $projectRoot 'UiRendering.cs') `
    (Join-Path $projectRoot 'UpdateMenuVisuals.cs') `
    (Join-Path $projectRoot 'OverlayInteraction.cs') `
    (Join-Path $projectRoot 'UsageData.cs') `
    (Join-Path $projectRoot 'UsageTrustPolicy.cs') `
    (Join-Path $projectRoot 'CodexAppServerClient.cs') `
    (Join-Path $projectRoot 'GitHubReleaseUpdateService.cs') `
    (Join-Path $projectRoot 'FirstRunGuideForm.cs') `
    (Join-Path $projectRoot 'OverlaySettings.cs') `
    (Join-Path $projectRoot 'ResetRadarService.cs') `
    (Join-Path $projectRoot 'CodexConversationSurfaceMonitor.cs') `
    (Join-Path $projectRoot 'tests\RenderingCompatibilityTests.cs') `
    (Join-Path $projectRoot 'tests\UpdateMenuVisualsTests.cs') `
    (Join-Path $projectRoot 'tests\OverlayInteractionTests.cs') `
    (Join-Path $projectRoot 'tests\UsageTrustPolicyTests.cs') `
    (Join-Path $projectRoot 'tests\UsageDisplayTextTests.cs') `
    (Join-Path $projectRoot 'tests\GitHubReleaseUpdateTests.cs') `
    (Join-Path $projectRoot 'tests\OverlaySettingsTests.cs') `
    (Join-Path $projectRoot 'tests\ResetRadarTests.cs')

if ($LASTEXITCODE -ne 0) {
    throw "Test build failed with exit code $LASTEXITCODE"
}

& (Join-Path $outputDir 'ResetRadarTests.exe')
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE"
}
