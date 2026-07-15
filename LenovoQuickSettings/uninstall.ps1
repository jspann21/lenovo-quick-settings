[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installDirectory = Join-Path $env:LOCALAPPDATA 'LenovoQuickSettings'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lenovo Quick Settings.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Lenovo Quick Settings.lnk'

Get-Process -Name 'LenovoQuickSettings' -ErrorAction SilentlyContinue | Stop-Process
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Lenovo Quick Settings' -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $startMenu -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $desktop -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $installDirectory -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'Lenovo Quick Settings was removed.'
