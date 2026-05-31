# Pedal Nudge Joy-Con for Windows

A native Windows desktop app that uses a paired Nintendo Joy-Con as a motion sensor. When pedaling motion stops for the configured idle time, the app pulses Joy-Con HD Rumble. It stops nudging once motion resumes.

This version does not use Chrome, WebHID, or a browser permission prompt. It is a .NET 8 Windows Forms app that talks to the Joy-Con as a HID device through JoyCon.NET and HidSharp.

## Requirements

- Windows 10 or Windows 11
- A Nintendo Joy-Con paired in Windows Bluetooth settings
- .NET 8 SDK for building/running from source

## Pair the Joy-Con

1. Open **Windows Settings > Bluetooth & devices**.
2. Choose **Add device > Bluetooth**.
3. Hold the small Joy-Con sync button until the LEDs start chasing.
4. Select the Joy-Con when it appears.

## Run from source

From PowerShell in this folder:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" restore .\PedalNudge.Windows.csproj --source https://api.nuget.org/v3/index.json
& "C:\Program Files\dotnet\dotnet.exe" run --project .\PedalNudge.Windows.csproj
```

## Build a standalone EXE

From PowerShell in this folder:

```powershell
.\publish-win-x64.ps1
```

The publish script now prefers the 64-bit .NET CLI at `C:\Program Files\dotnet\dotnet.exe` and restores packages directly from nuget.org.

The output will be under:

```text
bin\Release\net8.0-windows\win-x64\publish\PedalNudge.Windows.exe
```

## How to use

1. Pair the Joy-Con in Windows first.
2. Launch the app.
3. Click **Refresh** if the device list is empty.
4. Select the Joy-Con and click **Connect**.
5. Mount or strap the Joy-Con to the pedal, crank arm, shoe, or ankle.
6. Hold still and click **Calibrate while still**.
7. Click **Start watcher**.


## Saved settings

Tuning values are saved automatically to:

```text
%APPDATA%\PedalNudge\settings.json
```

The app loads this file on startup and also saves again when you change any tuning value, click **Test rumble**, click **Start watcher**, or close the app. The **Open settings/log folder** button opens the folder containing the JSON file and the app log.

Changing tuning values while the watcher is already running now live-applies the new values to the running watcher.

## Shutdown and log file

When you close the app window, it now saves settings, stops the watcher, sends a best-effort rumble-off command, disconnects the Joy-Con, and then closes. Shutdown cleanup has a timeout so a stuck Bluetooth/HID call should not keep the window open forever.

The app writes a log file here:

```text
%APPDATA%\PedalNudge\pedal-nudge.log
```

If the app crashes or gets stuck, send this log file along with what you clicked immediately before the problem happened.

## Tuning tips

- **Idle seconds**: How long motion can stay below threshold before rumble begins.
- **Motion threshold**: Higher means the app requires stronger pedaling movement before it counts as motion.
- **Rumble on ms**: Length of each nudge pulse.
- **Rumble off ms**: Quiet gap between pulses. Increase this if rumble is being mistaken for motion.
- **Rumble amplitude**: 0.0 to 1.0. Start around 0.45 to 0.60.

The app uses short rumble pulses, not continuous rumble. This leaves quiet windows where it can detect real resumed motion without the rumble motor polluting the IMU reading.

## Troubleshooting

- **No device found**: Pair the Joy-Con in Windows Bluetooth settings, then click Refresh. If it still does not show up, remove it from Windows Bluetooth devices and pair it again.
- **Connect fails**: Close Steam, BetterJoy, emulators, controller mappers, or any app that might already own the HID device.
- **Motion never registers**: Click Calibrate while still, then pedal and watch the motion meter. Lower the motion threshold if needed.
- **Rumble stops too easily**: Increase the motion threshold or rumble-off gap.
- **Rumble never stops**: Lower the motion threshold or mount the Joy-Con somewhere with stronger rotation, like the crank arm or shoe.

## Third-party dependency note

This project references JoyCon.NET 1.0.1, which is licensed under GPL-3.0, and HidSharp. This source is intended as a personal-use starter app. Review the third-party licenses before redistributing binaries or modified versions.

## Hardware test status

The source is structured for Windows and JoyCon.NET, but it was generated in an environment without a Windows desktop runtime or a physical Joy-Con, so I could not perform an end-to-end hardware test here.
