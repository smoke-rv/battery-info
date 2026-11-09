# Battery Info

[Українською](README.uk.md)

A tiny Windows tray app that shows the battery level of your wireless devices — one tray icon per device, with the percentage right on the icon. No vendor software, no digging through Settings or tray submenus.

![Icon styles](docs/icons.png)

*Icon styles (shown 4x): mouse, headphones, keyboard, generic device. Green = OK, orange ≤ 30%, red ≤ 15%, blue = charging, gray = stale data.*

## What it supports

Nothing is hard-coded to specific devices — it discovers what is connected:

- **Bluetooth (classic and BLE)** — headphones, mice, keyboards, gamepads, etc. It reads the value Windows itself shows in *Settings → Bluetooth*. If Windows doesn't show a battery level for a device, this app can't see it either.
- **Logitech devices on a receiver** (Bolt / Unifying / Lightspeed) — via the HID++ 2.0 protocol, all paired devices that report a battery.

The device type (headphones / mouse / keyboard / other) is detected automatically and decides the icon shape.

## Behavior

- Connect a device and its icon appears on its own (refreshes on device changes and every 60 s). Disconnect — the icon disappears.
- Notifications at 15% and 5%.
- Right-click an icon: list of detected devices (checkbox = show its icon, remembered between runs), *Refresh now*, *Run at Windows startup*, *Exit*.

## Install

Download `BatteryInfo.exe` from the [Releases](../../releases) page and run it. That's it — it needs only .NET Framework 4 (built into Windows 10/11), x64. The file is not code-signed, so SmartScreen may warn: *More info → Run anyway*.

Windows 11 hides new tray icons in the `^` overflow — drag them onto the taskbar once and it will remember.

## Build

```powershell
.\build.ps1
```

Only the C# compiler that ships with Windows (`csc.exe`, .NET Framework 4) is required. Output: `BatteryInfo.exe`.

The version lives in `src/AssemblyInfo.cs`. The exe icon is `assets/app.ico` (regenerate: `BatteryInfo.exe --icon assets\app.ico`).

Diagnostics: `BatteryInfo.exe --dump "C:\path\dump.txt"` writes the detected devices to a file.

## Limitations

- Devices that don't expose their battery to Windows (some speakers, gamepads, AirPods) aren't shown — they use proprietary protocols.
- Non-Logitech devices on their own USB receivers (Razer, SteelSeries, ...) aren't supported yet.

## License

[MIT](LICENSE)
