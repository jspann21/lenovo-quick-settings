[CmdletBinding()]
param(
    [switch]$NoLaunch,
    [switch]$NoStartup
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $project 'deployment.ps1')
$source = Join-Path $project 'dist\LenovoQuickSettings.exe'
$installDirectory = Get-LenovoInstallDirectory
$target = Join-Path $installDirectory 'LenovoQuickSettings.exe'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lenovo Quick Settings.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Lenovo Quick Settings.lnk'

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    & (Join-Path $project 'build.ps1')
}

Stop-LenovoInstalledApp -ExecutablePath $target
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $target -Force

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoStartup) {
    Remove-LenovoStartupEntry
} else {
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name 'Lenovo Quick Settings' -Value ('"' + $target + '" --tray') -Type String
}

$shell = New-Object -ComObject WScript.Shell
foreach ($shortcutPath in @($startMenu, $desktop)) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $installDirectory
    $shortcut.Description = 'Quick control for Lenovo power, charging, and presence detection'
    $shortcut.Save()
}

Write-Host "Installed $target"
Write-Host $(if ($NoStartup) { 'Windows startup is disabled.' } else { 'Windows startup is enabled (silent tray launch).' })
Write-Host 'Start-menu and desktop shortcuts created. Right-click either shortcut and choose Pin to taskbar.'

if (-not $NoLaunch) {
    Start-Process -FilePath $target
}
