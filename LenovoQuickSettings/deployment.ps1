# Shared install/uninstall helpers. Dot-source this file; it makes no changes itself.

function Get-LenovoInstallDirectory {
    # IsPathRooted also accepts drive-relative C:folder and root-relative \folder.
    $fullyQualifiedPath = '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+\\[^\\/]+(?:\\|$))'
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -or $env:LOCALAPPDATA -notmatch $fullyQualifiedPath) {
        throw 'LOCALAPPDATA must name an absolute directory.'
    }

    $appDataDirectory = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\')
    $directory = [IO.Path]::GetFullPath((Join-Path $appDataDirectory 'LenovoQuickSettings'))
    if ((Split-Path -Leaf $directory) -ne 'LenovoQuickSettings' -or
        (Split-Path -Parent $directory).TrimEnd('\') -ne $appDataDirectory) {
        throw "Unsafe install directory: $directory"
    }

    if (Test-Path -LiteralPath $directory) {
        $item = Get-Item -LiteralPath $directory -Force -ErrorAction Stop
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "The install directory must be a regular directory: $directory"
        }
    }
    return $directory
}

function Stop-LenovoInstalledApp {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    $targetPath = [IO.Path]::GetFullPath($ExecutablePath)
    $processes = @(Get-Process -Name 'LenovoQuickSettings' -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        try {
            if ($process.Path -and [string]::Equals(
                [IO.Path]::GetFullPath($process.Path), $targetPath, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -InputObject $process -Force -ErrorAction Stop
                if (-not $process.WaitForExit(5000)) {
                    throw "The installed app did not exit: $targetPath"
                }
            }
        } finally {
            $process.Dispose()
        }
    }
}

function Remove-LenovoStartupEntry {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path -LiteralPath $runKey) {
        $key = Get-Item -LiteralPath $runKey -ErrorAction Stop
        try {
            if ($key.GetValueNames() -contains 'Lenovo Quick Settings') {
                Remove-ItemProperty -LiteralPath $runKey -Name 'Lenovo Quick Settings' -ErrorAction Stop
            }
        } finally {
            $key.Dispose()
        }
    }
}

function Remove-LenovoShortcut {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
}
