$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectRoot 'tests\bin'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& $compiler /nologo /target:exe /optimize+ /platform:anycpu `
    "/out:$outputDir\ResetRadarTests.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Windows.Forms.dll `
    (Join-Path $projectRoot 'UiRendering.cs') `
    (Join-Path $projectRoot 'OverlayInteraction.cs') `
    (Join-Path $projectRoot 'UsageData.cs') `
    (Join-Path $projectRoot 'UsageTrustPolicy.cs') `
    (Join-Path $projectRoot 'CodexAppServerClient.cs') `
    (Join-Path $projectRoot 'GitHubReleaseUpdateService.cs') `
    (Join-Path $projectRoot 'ResetRadarService.cs') `
    (Join-Path $projectRoot 'tests\RenderingCompatibilityTests.cs') `
    (Join-Path $projectRoot 'tests\OverlayInteractionTests.cs') `
    (Join-Path $projectRoot 'tests\UsageTrustPolicyTests.cs') `
    (Join-Path $projectRoot 'tests\GitHubReleaseUpdateTests.cs') `
    (Join-Path $projectRoot 'tests\ResetRadarTests.cs')

if ($LASTEXITCODE -ne 0) {
    throw "Test build failed with exit code $LASTEXITCODE"
}

& (Join-Path $outputDir 'ResetRadarTests.exe')
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE"
}
