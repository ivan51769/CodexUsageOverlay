$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$automationRoot = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL'
foreach ($testName in @('SidebarContextUiTests', 'SidebarRecoveryUiTests')) {
    $output = Join-Path $projectRoot "tests\bin\$testName.exe"
    New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
    & $compiler /nologo /target:exe "/out:$output" /reference:System.dll /reference:System.Core.dll `
        /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
        "/reference:$automationRoot\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll" `
        "/reference:$automationRoot\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll" `
        "/reference:$automationRoot\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll" `
        (Join-Path $projectRoot "tests\$testName.cs")
    if ($LASTEXITCODE -ne 0) { throw "$testName build failed." }
    & $output (Join-Path $projectRoot 'bin\CodexUsageOverlay.exe') `
        (Join-Path $projectRoot 'bin\sidebar-refined-dpi.png')
    if ($LASTEXITCODE -ne 0) { throw "$testName failed." }
}
