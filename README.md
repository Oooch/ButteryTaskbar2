# Buttery Taskbar 3

Hides the Windows taskbar until you need it, and works on current versions of Windows 11.

This goes further than the built-in auto-hide: moving the mouse to the bottom of the screen does not bring the taskbar back. To show it, press the Windows key or scroll at the bottom edge of the screen.

This is a C# rewrite of [ButteryTaskbar2](https://github.com/LuisThiamNye/ButteryTaskbar2) by Luis Thiam-Nye, which stopped working on newer Windows 11 builds. The original Jai source files are still in the repo for reference.

## Features

- The taskbar stays hidden unless:
  - you are holding down the Windows key, or
  - the Start menu, search, Task View, the tray overflow or the notification centre is open.
- Scroll at the bottom edge of the primary monitor to open the Start menu (can be turned off).
- Optional `Ctrl` + `Win` + `F11` shortcut to turn hiding on or off (off by default).
- Option to keep Windows' own auto-hide turned on when Buttery Taskbar is disabled.
- Tray icon (a yellow block of butter) with a settings menu. Left-click or right-click it.

Settings are saved to `%LocalAppData%\ButteryTaskbar3\config.json`.

## Building

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download) or newer.

```powershell
dotnet publish src -c Release -r win-x64
```

The executable ends up in `src\bin\Release\net9.0-windows\win-x64\publish\ButteryTaskbar3.exe`. It needs the .NET 9 Desktop Runtime installed to run.

## Running at log-in

Create a Task Scheduler entry that runs `ButteryTaskbar3.exe` at log-on. In its properties:

- **General** tab: tick "Run with highest privileges". Without this, the Windows key detection stops working while an app running as administrator has focus.
- **Conditions** tab: on laptops, untick "Start the task only if the computer is on AC power".
- **Settings** tab: untick "Stop the task if it runs longer than".

## Quitting

Use **Quit** in the tray menu. This puts the taskbar back the way it was.

If the process is killed instead (Task Manager, `Stop-Process`), it has no chance to clean up and the taskbar stays hidden. To get it back, either:

- start Buttery Taskbar again and choose **Quit** from the tray menu, or
- restart Windows Explorer from Task Manager, then turn off "Automatically hide the taskbar" in Settings > Personalisation > Taskbar > Taskbar behaviours.

## License

[Eclipse Public License 2.0](LICENSE), the same as the original project.
