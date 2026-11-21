# Buttery Taskbar 3

Hides the Windows taskbar until you need it, and works on current versions of Windows 11.

This goes further than the built-in auto-hide: moving the mouse to the bottom of the screen does not bring the taskbar back. To show it, press the Windows key or scroll at the bottom edge of the screen.

It's built to reduce burn-in on OLED screens, so it also has options to hide other things that tend to sit still on screen: taskbar tooltips and an idle mouse cursor.

This is a C# rewrite of [ButteryTaskbar2](https://github.com/LuisThiamNye/ButteryTaskbar2) by Luis Thiam-Nye, which stopped working on newer Windows 11 builds. The original Jai source files are still in the repo for reference.

## Features

- The taskbar stays hidden unless:
  - you are holding down the Windows key, or
  - the Start menu, search, the tray overflow or the notification centre is open.
- Scroll at the bottom edge of the primary monitor to open the Start menu (can be turned off).
- Optional `Ctrl` + `Win` + `F11` shortcut to turn hiding on or off (off by default).
- Option to keep Windows' own auto-hide turned on when Buttery Taskbar is disabled.
- Option to turn off taskbar tooltips. This works with the classic Windows 10 taskbar, including ExplorerPatcher on Windows 11.
- Option to hide the mouse cursor after 2 minutes without movement. It comes back as soon as you move the mouse. To change the delay, edit `HideCursorAfterSeconds` in the settings file.
- Tray icon (a yellow block of butter) with a settings menu. Left-click or right-click it.

Settings are saved to `%LocalAppData%\ButteryTaskbar3\config.json`.

## Download

Get the latest version from the [Releases page](../../releases). There are two builds:

- `ButteryTaskbar3.exe` is small, but needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).
- `ButteryTaskbar3-standalone.exe` runs without it, but is about 50 MB.

There's no installer. Put the exe somewhere permanent and run it.

## Running at log-in

Tick **Start at log-in** in the tray menu and approve the admin prompt. This creates a Task Scheduler entry that starts Buttery Taskbar with administrator rights when you log in. Without those rights, the Windows key detection stops working while an app running as administrator has focus.

The entry points at the exe's current location, so if you move the exe, untick and tick the option again.

## Building

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download) or newer.

```powershell
dotnet publish src -c Release -r win-x64
```

The executable ends up in `src\bin\Release\net9.0-windows\win-x64\publish\ButteryTaskbar3.exe`.

Every push to `main` is built by GitHub Actions, and the exes are attached to the run. Pushing a tag such as `v3.1.0` also creates a GitHub release with both exes.

## Quitting

Use **Quit** in the tray menu, or run:

```powershell
ButteryTaskbar3.exe --quit
```

Either way the taskbar comes back. Windows' own auto-hide is turned back off, unless "Auto-hide when disabled" is ticked. That setting starts out matching your Windows auto-hide setting from the first time you ran the app.

If the process is killed instead (Task Manager, `Stop-Process`, `taskkill /F`), it can't clean up and the taskbar stays hidden. To get it back, either:

- start Buttery Taskbar again, then quit it properly, or
- restart Windows Explorer from Task Manager, then turn off "Automatically hide the taskbar" in Settings > Personalisation > Taskbar > Taskbar behaviours.

If the mouse cursor was hidden at the time, it stays invisible too. Starting Buttery Taskbar again brings it back straight away.

## License

[Eclipse Public License 2.0](LICENSE), the same as the original project.
