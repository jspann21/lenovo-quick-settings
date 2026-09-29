[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $project 'deployment.ps1')
# Validate the resolved target before any recursive removal.
$installDirectory = Get-LenovoInstallDirectory
$target = Join-Path $installDirectory 'LenovoQuickSettings.exe'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lenovo Quick Settings.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Lenovo Quick Settings.lnk'

Stop-LenovoInstalledApp -ExecutablePath $target
Remove-LenovoStartupEntry
Remove-LenovoShortcut -Path $startMenu
Remove-LenovoShortcut -Path $desktop
if (Test-Path -LiteralPath $installDirectory) {
    Remove-Item -LiteralPath $installDirectory -Recurse -Force -ErrorAction Stop
}

Write-Host 'Lenovo Quick Settings was removed.'
