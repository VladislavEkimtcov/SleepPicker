# SleepPicker

The four dropdowns from **Settings → System → Power & sleep**, as a tray menu.

![SleepPicker's tray menu, open above the notification area with a timeout submenu showing](docs/screenshot.png)

Changing a screen-off or sleep timeout normally means Start → Settings → System → Power &
sleep → scroll → dropdown. SleepPicker puts the same four settings two clicks from
anywhere: click the moon in the notification area, pick a time.

## What it does

- **All four timeouts, in one menu.** Screen off and sleep, on battery and plugged in.
  Each row shows its current value inline, so all four are readable without opening
  anything, and each offers the choices Windows itself offers — 1 to 45 minutes, 1 to 5
  hours, and Never.
- **Always the live values.** The menu is rebuilt from the *active* power scheme every
  time it opens, so changes made by the Settings app, by `powercfg`, or by switching power
  plans are already there. A timeout that is not one of the presets — say 7 minutes, set
  by something else — is shown at the top, ticked, rather than leaving nothing selected.
- **A moon that is also the battery gauge.** Described below.
- **One battery indicator instead of two.** Windows' own battery meter can be switched off
  from the same menu, leaving the moon as the only one. Windows keeps that setting out of
  a user's own reach, so this is the one thing here that needs an administrator's approval,
  and the taskbar only reads it at startup, so Explorer has to restart. SleepPicker says
  both of those in a dialog and does nothing at all unless you agree; on a desktop the row
  is hidden, like the moon row. While it is on, Windows greys out its own **Power** toggle
  in Settings → Taskbar, so unticking here is the way back.
- **Start with Windows.** A checkbox, and nothing more than a per-user `Run` entry.
- **Nothing to install, nothing left behind.** One 100 KB executable, no runtime, no
  configuration file, and at most three registry values under HKCU. Nothing is elevated
  except the one optional change above, and only at the moment you ask for it.

Either mouse button opens the menu. Launching SleepPicker while it is already running
opens the menu rather than adding a second icon.

## The moon

The tray icon *is* the charge: a full moon at 100%, waning through gibbous and half to a
thin crescent, and to nothing when the battery is flat. Hovering it reports the figure
exactly.

![Every phase the tray icon can draw, 0% to 100%: at 48px, then at tray size waning, then waxing](docs/moon-phases.png)

Which way the charge is going is drawn the way the sky draws it. A waning moon is lit on
one limb and a waxing moon on the other, so the icon mirrors itself when the machine goes
on mains — the same phase, running the other way.

| On battery — waning | On mains — waxing |
| --- | --- |
| ![The tray icon at 35% on battery, lit on its lower left](docs/tray-battery.png) | ![The same 35% on mains, lit on its lower right](docs/tray-mains.png) |

Both moons are drawn together whenever the charge moves, so the cable going in swaps a
picture that is already in hand and the icon turns round as it happens rather than at some
point in the next minute. Otherwise it redraws once a minute, which no battery moves
faster than.

Two details the arithmetic alone gets wrong. Below 20% the unlit limb fades in as a ring —
real moons do this, lit by earthshine, and it means a flat battery leaves something in the
tray to click on instead of an empty slot that reads as a program that has died. And the
dark rim takes at most a quarter of the crescent's width rather than a fixed width, since
at 16px a crescent is thinner than one pixel below about 15% charge and a fixed rim would
swallow the gold whole.

The phases are drawn, not shipped: 101 levels at every size the shell can ask for would
have added more to the executable than the rest of the program weighs, and still had
nothing to show at a scaling factor nobody thought to bake. Two arcs are exact at any
size. `tools\MakeMoonPhases.py` renders the same geometry to the sheet above, so the
series can be looked at rather than only reasoned about:

```cmd
python tools\MakeMoonPhases.py
```

Untick **Dynamic tray icon** and the fixed artwork comes back. On a desktop the row is
hidden, along with the two "on battery" rows — exactly as the Settings page hides them
there. Battery presence is re-checked every time the menu opens, so an undocked tablet or
a removed battery is handled without a restart.

## Install

1. Download `SleepPicker.exe` from the
   [latest release](https://github.com/VladislavEkimtcov/SleepPicker/releases/latest).
2. Put it anywhere — it is one file and writes nothing beside itself.
3. Run it. A gold crescent appears in the notification area.
4. Optionally tick **Start with Windows**.

To uninstall: untick **Start with Windows**, choose **Exit**, delete the file.

> Windows hides new notification-area icons by default. If the moon does not appear, click
> the `^` arrow next to the clock and drag it onto the taskbar.

## Design constraints

SleepPicker targets locked-down industrial Windows — **Windows 10/11 IoT Enterprise LTSC**
and similar images — where a developer machine's assumptions do not hold. The rules the
code is written to, and which changes should keep to:

- **.NET Framework 4.8 + WinForms.** In-box on every supported image; nothing to deploy.
- **C# 5 only.** The in-box `csc.exe` is the C# 5 compiler: no string interpolation, no
  `?.`, no `nameof`, no expression-bodied members, no pattern matching, no tuples.
- **Legacy, non-SDK-style `.csproj`.** `<Project Sdk="…">` requires the .NET SDK, which is
  not present.
- **No NuGet packages**, ever — there is no restore.
- **One self-contained `.exe`.** Framework references are marked `Private=False` so
  nothing is copied beside it.
- **Never require elevation.** The manifest requests `asInvoker`, and everything the tray
  menu does works without admin rights — changing power timeouts does not need them. The
  single exception is hiding Windows' battery meter, which lives in the part of the
  registry Windows keeps read-only for the user; that one write is handed to a second copy
  of the executable started with the `runas` verb, so the prompt appears only when that row
  is ticked, and never at startup.
- **Autostart through the per-user Run key**, not a service or scheduled task.
- **Write nothing outside the user profile**, and as little as possible inside it: turning
  **Dynamic tray icon** back on deletes its value, and the key with it.

## Build from source

```cmd
build.cmd
```

That is the entire toolchain requirement. `build.cmd` uses the MSBuild that ships inside
Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe`), so it builds on a
machine with no .NET SDK, no Visual Studio, no NuGet and no package manager. Output is
`bin\SleepPicker.exe`. `warning MSB3644` is expected and harmless: with no targeting packs
installed, MSBuild resolves the framework references from the GAC instead.

The embedded icon is built from `assets\SleepPicker.png`, a crescent drawn on white. To
regenerate it after redrawing:

```cmd
powershell.exe -ExecutionPolicy Bypass -File tools\MakeIcon.ps1
```

## How it works

Timeouts are read and written through the Win32 power API in `powrprof.dll` —
`PowerGetActiveScheme`, `PowerRead{AC,DC}ValueIndex`, `PowerWrite{AC,DC}ValueIndex` —
rather than by driving `powercfg.exe`, whose output is localised and would have to be
screen-scraped. A write is followed by `PowerSetActiveScheme`, without which the new value
sits in the scheme without taking effect. The settings are the standard ones:
`SUB_VIDEO`/`VIDEOIDLE` and `SUB_SLEEP`/`STANDBYIDLE`.

Whether there is a battery, how much is left in it, and whether the machine is on mains
all come from one `GetSystemPowerStatus` call — one call, so the charge and the power
source cannot disagree and draw a moon that was never true. It reports 255 for a charge it
does not know, which is what virtual machines and some docks give back; that falls back to
the fixed icon rather than drawing 255% of a moon.

```
src/
  Program.cs         entry point and single-instance guard
  TrayApp.cs         the notification icon and its menu
  PowerSettings.cs   powrprof.dll interop
  PowerTarget.cs     one setting on one power source
  MoonIcon.cs        draws the moon at a given phase, waning or waxing
  Settings.cs        the dynamic-icon preference, under HKCU
  AutoStart.cs       the Run-key checkbox
  BatteryMeter.cs    hides Windows' own battery icon, and restarts the shell to show it
  SingleInstance.cs  mutex plus "show the menu" signal
tools/
  MakeIcon.ps1       regenerates the .ico from assets/SleepPicker.png
  MakeMoonPhases.py  renders docs/moon-phases.png from MoonIcon.cs's geometry
bin/SleepPicker.exe  the build, committed so it can just be downloaded
```
