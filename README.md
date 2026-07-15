<p align="center">
  <img src="docs/icon.svg" width="160" alt="Lenovo Quick Settings icon">
</p>

<h1 align="center">Lenovo Quick Settings</h1>

<p align="center">
  A small, borderless Windows tray app for quick access to the Lenovo hardware settings on a Lenovo Slim 7 14ILL10 (83MC).
</p>

<p align="center">
  <img src="docs/screenshot.png" width="432" alt="Lenovo Quick Settings control panel">
</p>

The app calls the same installed Lenovo controller agents used by Vantage and verifies each change by reading the hardware state back. It does not substitute Windows power-overlay settings or write guessed registry values.

## Controls

- Adaptive power mode (Auto)
- Battery saver
- Performance
- Conservation mode (start charging at 75%, stop at 80%)
- Zero-touch login (wake and use Windows Hello when you approach)
- Zero-touch lock (lock automatically when you walk away)
- Optional silent notification-tray startup with Windows

The panel never creates a taskbar button. Closing or minimizing it hides it in the notification tray; left-click the tray icon to reopen the panel, or right-click it for direct controls.

## Download a GitHub Actions build

1. Open the repository's **Actions** tab.
2. Select **Build Windows executable**.
3. Open a successful workflow run.
4. Download the **LenovoQuickSettings-windows-x64** artifact.
5. Extract `LenovoQuickSettings.exe` from the downloaded ZIP.

The executable is a personal, unsigned Windows utility, so Windows may display a SmartScreen warning after downloading it.

## Build locally

Run:

```powershell
.\LenovoQuickSettings\build.ps1
```

The build uses the .NET Framework C# compiler included with Windows and writes:

```text
LenovoQuickSettings\dist\LenovoQuickSettings.exe
```

No Lenovo binaries are copied into the repository or bundled into the executable. Lenovo's installed Vantage add-ins are discovered at runtime.

## Install

After building, run:

```powershell
.\LenovoQuickSettings\install.ps1
```

This installs the executable under `%LOCALAPPDATA%\LenovoQuickSettings`, enables silent tray startup, and creates Desktop and Start-menu shortcuts. Use `-NoStartup` to install without enabling startup, or `-NoLaunch` to avoid launching immediately.

To remove the installed app, shortcuts, and startup entry:

```powershell
.\LenovoQuickSettings\uninstall.ps1
```

## Hardware mapping

| UI control | Lenovo controller value |
|---|---|
| Adaptive power mode | `ItsAuto` |
| Battery saver | `MmcCool` |
| Performance | `MmcPerformance` |
| Conservation enabled | battery charge mode `Storage` |
| Conservation disabled | battery charge mode `Normal` |
| Zero-touch login | human-presence `ApproachEnabled` |
| Zero-touch lock | human-presence `PresenceLeaveEnabled` |

## Requirements and scope

This project intentionally targets one computer rather than providing broad Lenovo model compatibility. It expects Lenovo Vantage's `IdeaNotebookAddin` and `SmartInteractAddin` under `%PROGRAMDATA%\Lenovo\Vantage\Addins` and discovers the newest complete version of each add-in at startup.

The GitHub-hosted runner can compile the executable, but it cannot run hardware integration tests because Lenovo's drivers and add-ins are not present there.
