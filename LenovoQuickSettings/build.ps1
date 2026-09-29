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

# Keep the executable's version metadata in sync with the application manifest.
[xml]$manifest = Get-Content -LiteralPath (Join-Path $project 'app.manifest') -Raw
$version = [Version]$manifest.assembly.assemblyIdentity.version
if ($version.Build -lt 0 -or $version.Revision -ne 0) {
    throw 'The manifest version must use major.minor.patch.0 format.'
}
$assemblyInfo = Join-Path $output 'AssemblyInfo.cs'
@"
using System.Reflection;
[assembly: AssemblyTitle("Lenovo Quick Settings")]
[assembly: AssemblyProduct("Lenovo Quick Settings")]
[assembly: AssemblyVersion("$version")]
[assembly: AssemblyFileVersion("$version")]
[assembly: AssemblyInformationalVersion("$($version.ToString(3))")]
"@ | Set-Content -LiteralPath $assemblyInfo -Encoding UTF8

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
    "$(Join-Path $project 'LenovoQuickSettings.cs')" `
    $assemblyInfo

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Built $(Join-Path $output 'LenovoQuickSettings.exe') (v$($version.ToString(3)))"
