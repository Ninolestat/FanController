# Fan Controller

Minimalist fan control for Windows 10/11 (~21 MB background service), built on LibreHardwareMonitor 0.9.6 and PawnIO. Follows the Windows light/dark theme (*Settings › Personalization › Colors*).

Includes `Ite8689Fix` (ported from the Linux `it87` driver) to disable SmartFan extra vectors on Gigabyte ITE IT8689E boards so manual/curve control is not overridden by the BIOS.

## Architecture

| Process | Role | Memory lifecycle |
|---|---|---|
| `FanCtl.exe` | Control service: sensors, curves, tray icon. Pure Win32 (no UI framework), single control thread, EcoQoS. Requires administrator (PawnIO driver). | Always running |
| `FanCtl.UI.exe` | WPF configuration window (Mica, curve editor, copy/paste curves). | Only while open; closing it exits the UI process |

- **IPC**: shared memory (`Local\FanCtl.Snapshot`) + named event for config reload.
- **Config**: `%LOCALAPPDATA%\FanCtl\config.json`.
- When the UI is closed, the service updates **only** the hardware used by active curves (+ CPU for thermal protection).

## Safety

- Curve temperature sensor unavailable → 100%.
- CPU Package ≥ 95 °C → all controlled fans forced to 100%.
- Exit / logoff / crash recovery → touched fans are restored to BIOS/firmware control.
- Ramping: +20%/s up, −5%/s down; 2 °C hysteresis.

## Install

Download `FanCtl-Setup.exe` from [Releases](https://github.com/Ninolestat/FanController/releases) (self-contained `win-x64`, includes PawnIO setup).

## Build from source

```powershell
dotnet build FanCtl.sln -c Release
```

Output is written to `bin\Release\`. Run `FanCtl.exe` (UAC). `FanCtl.exe --tray` starts minimized to the tray; `FanCtl.exe --dump file.txt` dumps detected sensors.

To build the self-contained installer (requires Inno Setup 6 and `installer\PawnIO_setup.exe`):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

**Close NZXT CAM, GCC, and FanControl** while using Fan Controller so two tools do not overwrite the same fan registers.
