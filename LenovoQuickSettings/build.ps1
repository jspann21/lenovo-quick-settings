[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = Join-Path $project 'dist'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $compiler)) {
    throw ".NET Framework C# compiler not found at $compiler"
}

New-Item -ItemType Directory -Path $output -Force | Out-Null

& $compiler `
    /nologo `
    /target:winexe `
    /platform:x64 `
    /optimize+ `
    /win32manifest:"$(Join-Path $project 'app.manifest')" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /out:"$(Join-Path $output 'LenovoQuickSettings.exe')" `
    "$(Join-Path $project 'LenovoQuickSettings.cs')"

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Built $(Join-Path $output 'LenovoQuickSettings.exe')"
