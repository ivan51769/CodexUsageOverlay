$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectRoot 'tests\bin'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& $compiler /nologo /target:exe /optimize+ /platform:anycpu `
    "/out:$outputDir\ResetRadarTests.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Web.Extensions.dll `
    (Join-Path $projectRoot 'ResetRadarService.cs') `
    (Join-Path $projectRoot 'tests\ResetRadarTests.cs')

if ($LASTEXITCODE -ne 0) {
    throw "Test build failed with exit code $LASTEXITCODE"
}

& (Join-Path $outputDir 'ResetRadarTests.exe')
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE"
}
