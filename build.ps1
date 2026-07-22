$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectRoot 'bin'
$logoPath = Join-Path $projectRoot 'installer-assets\brand-logo.png'
$iconPath = Join-Path $projectRoot 'installer-assets\app-icon.ico'
$defaultCachePath = Join-Path $projectRoot 'installer-assets\usage-cache.ini'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw "Missing .NET Framework compiler: $compiler"
}
if (-not (Test-Path -LiteralPath $logoPath) -or
    -not (Test-Path -LiteralPath $iconPath) -or
    -not (Test-Path -LiteralPath $defaultCachePath)) {
    throw 'Missing installer asset.'
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& $compiler /nologo /target:winexe /optimize+ /platform:anycpu `
    "/out:$outputDir\CodexUsageOverlay.exe" `
    "/win32icon:$iconPath" `
    "/resource:$logoPath,CodexUsageOverlay.BrandLogo.png" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Windows.Forms.dll `
    (Join-Path $projectRoot 'Program.cs') `
    (Join-Path $projectRoot 'OverlaySettings.cs') `
    (Join-Path $projectRoot 'CodexTaskStatusMonitor.cs') `
    (Join-Path $projectRoot 'CodexAppServerClient.cs')

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath $defaultCachePath -Destination (Join-Path $outputDir 'usage-cache.ini') -Force
Get-Item -LiteralPath (Join-Path $outputDir 'CodexUsageOverlay.exe')
