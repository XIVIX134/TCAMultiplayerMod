# TCAMP

TCAMP (Tiny Combat Arena Multiplayer) is a BepInEx/Harmony mod for Tiny Combat
Arena that adds local direct-UDP multiplayer sessions. The current focus is
peer-hosted dogfighting: lobby flow, aircraft spawning, remote aircraft
interpolation, combat event sync, score tracking, respawns, damageable part
sync, multiplayer pause/leave flow, and mid-session loadout changes.

This repository contains the mod source only. It does not redistribute Tiny
Combat Arena, Unity, BepInEx, Harmony, or other third-party/game assemblies.

## Current Status

This is a work-in-progress mod. It is playable enough for local host/client
testing. The current public source release is `v0.2`.

## Requirements

- Windows
- Tiny Combat Arena v0.14.1.4
- BepInEx installed in the game
- .NET SDK capable of building `net472` projects
- Visual Studio 2022 or Build Tools with .NET Framework 4.7.2 targeting pack

## Local Reference Assemblies

The project needs compile-time DLL references from your own game and BepInEx
install. These DLLs live in `libs/` locally and are ignored by git.

Populate them with:

```powershell
.\scripts\CopyReferenceAssemblies.ps1 -GamePath "C:\Program Files (x86)\Steam\steamapps\common\Tiny Combat Arena"
```

If BepInEx is not installed under the game folder, pass its root folder:

```powershell
.\scripts\CopyReferenceAssemblies.ps1 `
  -GamePath "C:\Program Files (x86)\Steam\steamapps\common\Tiny Combat Arena" `
  -BepInExPath "C:\path\to\game-or-bepinex-root"
```

Do not commit files from `libs/`.

## Build

```powershell
dotnet restore .\TCAMP.sln
dotnet build .\TCAMP.sln -c Release
```

The built plugin is:

```text
src\bin\Release\net472\TCAMP.dll
```

Copy it into the game's `BepInEx\plugins` folder.

## Tests

```powershell
dotnet test .\TCAMP.sln -c Release
```

The tests still require the local reference assemblies because the mod project
references game/runtime types.

## Repository Checks

Before publishing or opening a pull request, run:

```powershell
.\scripts\AssertPublishable.ps1
```

This catches accidentally tracked DLLs, logs, local game copies, decompiler
output, and build artifacts.

## Project Layout

- `src/Core` - session state, config, logging, and connection orchestration
- `src/Transport` - UDP and Steam transport abstractions
- `src/Protocol` - packet types, serialization, routing, reliability helpers
- `src/Game` - lobby, spawning, score, map/loadout/airfield helpers
- `src/Sync` - local state reads, interpolation, remote aircraft management
- `src/Combat` - gun, missile, bomb, radar, damage, explosion, collision sync
- `src/Patches` - Harmony patches into the base game
- `src/UI` - multiplayer menu, scoreboard, respawn UI
- `tests` - NUnit protocol/session/unit tests
- `scripts` - repo setup and publication checks

## License

No open-source license has been selected yet. Choose and add a license before
publishing this repository publicly.
