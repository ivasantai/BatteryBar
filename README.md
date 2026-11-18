# BatteryBar

A tiny Windows app that shows the battery level of your wireless devices right on the taskbar, next to the notification area.

![BatteryBar on the taskbar](docs/screenshot.png)

## Features

- Shows each connected device as an icon plus a percentage (headphones, mouse, keyboard, gamepad, phone).
- Sits on the taskbar to the left of the system tray. You can drag it to another spot, and the position is remembered.
- Matches the light or dark taskbar theme. The level turns red at 15% and below.
- Hover to see device names. Right-click for the menu: refresh, start with Windows, reset position, exit.
- Hides itself when a full-screen app is running or the taskbar is auto-hidden.
- Updates every 30 seconds, and right away when a device connects or disconnects.
- DPI-aware (per-monitor).

## Supported devices

| Connection | How the level is read |
|---|---|
| **Bluetooth Classic** (headsets with Hands-Free profile) | `DEVPKEY_Bluetooth_Battery` device property, the same source the Windows Settings app uses |
| **Bluetooth LE** (mice, keyboards, gamepads with the GATT Battery Service) | Same as above |
| **Razer 2.4 GHz dongles** (tested with Viper Ultimate) | Razer HID feature report protocol |

Other vendors' 2.4 GHz dongles (Logitech, SteelSeries, HyperX, …) each use their own proprietary protocol and aren't supported yet.

## Requirements

- Windows 10 (1809+) or Windows 11, x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64)

## Installation

1. Download `BatteryBar.exe` from the [latest release](https://github.com/ivasantai/BatteryBar/releases/latest).
2. Run it. Nothing needs installing, and the app has no window: it only shows up on the taskbar.
3. Optional: right-click the widget and enable **Start with Windows**.

If no supported device is connected, the widget stays hidden.

## Command-line options

| Option | Description |
|---|---|
| `--list` | Writes the detected devices and raw diagnostic data to `devices.txt` next to the exe, then exits |
| `--demo` | Shows sample devices so you can check the look |

## Building from source

Requires the .NET 8 SDK.

```powershell
dotnet publish -c Release -p:PublishSingleFile=true --self-contained false -o publish
```

The output is `publish\BatteryBar.exe`.

## Notes

- Settings are stored in `%APPDATA%\BatteryBar\settings.json`.
- **Start with Windows** adds an entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- When Razer Synapse is running, it talks to the device at the same time, so a query can occasionally fail. In that case the last known level is shown for up to 2 hours, which also covers a mouse that has gone to sleep.
