# WatchAlong

Puts a Kosmi room on a screen inside FFXIV, joining as a real Kosmi guest via a
sandboxed out-of-process browser, so playback stays perfectly synced with the
rest of the room. See `WatchAlong-Design-Doc.md` for the full design.

## Commands

| Command | Effect |
|---|---|
| `/wa` | Toggle the main viewer/control window |
| `/wa view` | Open the main window |
| `/wa join <kosmi url or WA1: invite>` | Join a room, or accept an invite (with confirmation) |
| `/wa settings` | Open Settings (identity, volume/mute, join/leave, invite, screen placement) |
| `/wa invite` | Copy a shareable invite for your current room and screen |
| `/wa sync <WA1P: message>` | Apply a shared screen position (with confirmation) |
| `/wa place` | Open the screen placement window |
| `/wa depth` | Toggle quad vs. depth-tested screen rendering |
| `/wa vol <0-150>` | Set volume |
| `/wa mute` | Toggle mute |
| `/wa snapshot` | Save a debug screenshot + DOM outline |

The main window's gear icon (or `/wa settings`) opens Settings, and Dalamud's
own plugin-installer gear/click-to-open buttons are wired up as well.

## Project layout

- `WatchAlong/` — the Dalamud plugin itself (UI, commands, world-screen rendering).
- `WatchAlong.Renderer/` — the out-of-process CEF browser that hosts the Kosmi page, communicating with the plugin over shared memory + a named pipe.
- `WatchAlong.Shared/` — code shared between the two processes (IPC messages, the Kosmi room codec, screen anchors, invite codec) with no Dalamud/game dependency, so it's unit-testable on its own.
- `tests/WatchAlong.Tests/` — the unit test suite for `WatchAlong.Shared` (and a few plugin-process integration checks).

## Building

1. Open `WatchAlong.slnx` in your C# editor of choice (Visual Studio or JetBrains Rider), or build from the CLI:
   ```
   dotnet build WatchAlong.slnx --configuration Release
   ```
2. The plugin DLL lands at `WatchAlong/bin/x64/Release/WatchAlong.dll` (or `Debug`).

### Prerequisites

* XIVLauncher, FINAL FANTASY XIV, and Dalamud installed, with the game run at least once through Dalamud.
* A .NET 10 SDK installed and available (`DALAMUD_HOME` if Dalamud's dev directory isn't in its default location).

## Running it in-game

1. `/xlsettings` → `Experimental` → add the full path to `WatchAlong.dll` under Dev Plugin Locations.
2. `/xlplugins` → `Dev Tools > Installed Dev Plugins` → enable `WatchAlong`.
3. `/wa` opens the main window.

## Installing from this repo

Add this repository's `repo.json` as a custom plugin repository in Dalamud
(`/xlsettings` → `Experimental` → Custom Plugin Repositories) to install
release builds without a dev environment.

## License and credits

WatchAlong is licensed under the **GNU Affero General Public License v3.0** (see
`LICENSE.md`), because it ports and adapts code from:

- [`Sebane1/XivMediaPlayer`](https://github.com/Sebane1/XivMediaPlayer) (AGPL-3.0) —
  `Direct3D11VideoTexture`, world-screen rendering, spatial-audio math, BGM
  ducking, Wine detection, and CEF loader workarounds.
- [`Styr1x/Browsingway`](https://github.com/Styr1x/Browsingway) — the
  out-of-process CEF renderer pattern (separate exe, shared texture/IPC,
  parent-PID watchdog).

Ported files carry an SPDX header identifying their origin and license.
