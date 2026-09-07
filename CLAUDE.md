# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is the Windows port of the macOS screensaver in `../macos`. The product is
`Hockey Fight.scr`, a WinForms application that renders with GDI+.

**Target:** Hockey Fight.scr
**Entry point:** `HockeyFight.Program`
**Framework:** net10.0-windows (WinForms)

## Build Commands

Build and install for the current user:
```powershell
.\build.ps1
```

Build only, into `dist\`:
```powershell
.\build.ps1 -Task build
```

Self-contained release into `release\`:
```powershell
.\build.ps1 -Task release
```

Uninstall, or clean build artifacts:
```powershell
.\build.ps1 -Task uninstall
.\build.ps1 -Task clean
```

Plain `dotnet build` / `dotnet run -- /w` also work for quick iteration.

## Architecture

### Screensaver lifecycle

Windows has no equivalent of Apple's `ScreenSaver` framework, so the plumbing is
explicit. `Program` parses the switch Windows passes (`/s`, `/p <hwnd>`, `/c`,
plus a development-only `/w`) and creates `ScreenSaverForm`s: one per monitor for
`/s`, or a single reparented child window for `/p`.

`ScreenSaverForm` owns the window, the 30 FPS timer and the exit-on-input rules.
`HockeyFightRenderer` owns everything the macOS `Hockey_FightView` did: sprite
loading, layout, the delta-time Euler integration and the one-second animation
tick. Keep new rendering work in the renderer and new windowing work in the form.

### Porting conventions

The renderer is deliberately a close translation of `Hockey_FightView.m` — same
constants, same structure, same method names where they map. When changing shared
behaviour, change both sides so they stay comparable.

All layout math is written in AppKit's bottom-left origin space, exactly as in the
original. `HockeyFightRenderer.Rect()` is the single point where that converts to
GDI+'s top-left origin. Do not scatter Y-flips through the drawing code.

### Things that will bite you

- **`Form.ClientSize` is not pixels.** It reports DPI-scaled logical units; on a
  150% display it is 1.5x the pixels actually painted. Use `PhysicalClientSize`
  (`GetClientRect`) for anything that lays out the scene.
- **WinForms resizes per-monitor-aware forms on `WM_DPICHANGED`.** A full-screen
  window would be sized past the edge of its own screen. `_targetBounds` pins the
  rectangle and the message is swallowed.
- **Preview reparenting must happen in `OnShown`, not `OnHandleCreated`.**
  WinForms recreates the handle while the form settles, and a recreated handle
  comes back top-level, silently undoing `SetParent`.
- **`WS_POPUP` must be cleared when reparenting.** A borderless form carries it,
  and a window cannot be both a popup and a child — leave it set and the preview
  renders blank.
- **Launching a `.scr` through the shell ignores your arguments.** The file
  type's default verb is *Install*, which runs it full screen. Use
  `Start-Process -NoNewWindow`, or run the executable directly.

### Rendering

Sprite sheets are embedded resources loaded by `Sprites.Load`, drawn with
`InterpolationMode.NearestNeighbor` to keep the pixel art crisp. The audience
rows, nets, scoreboard, clock and flags only change once a second, so they are
composited into a cached background bitmap and only the zamboni is redrawn each
frame.

## Project Structure

- `src/Program.cs` — entry point, command-line switches
- `src/ScreenSaverForm.cs` — window, timer, input, DPI and preview handling
- `src/HockeyFightRenderer.cs` — port of `Hockey_FightView.m`
- `src/Sprites.cs` — embedded sprite sheet loading
- `src/NativeMethods.cs` — Win32 interop
- `Resources/` — sprite sheets (copies of the macOS bundle's images)

## Releases

`.github/workflows/release-windows.yml` (at the repository root) runs on
`release: published`, builds a self-contained single-file `.scr`, and uploads it
to the release. It also has a `workflow_dispatch` trigger for testing changes
without cutting a release.

The version comes from the release tag, is validated against `1.2.3` / `v1.2.3`,
and is passed to `dotnet publish -p:Version=`, which lands it in the `.scr`'s
version resource.

Release builds are self-contained (~47 MB) so end users do not need the .NET
runtime. That requires `IncludeNativeLibrariesForSelfExtract=true`, without which
the single file leaves native DLLs beside it and the `.scr` stops working as soon
as it is copied somewhere on its own.
