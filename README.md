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
testing. The current public source release is `v0.3.0`.

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

## Release

Run releases from a local machine that already has `libs/` populated and the
GitHub CLI authenticated:

```powershell
.\scripts\Release-Version.ps1 -Version v0.3 -Message "Release v0.3"
```

The release helper bumps `src\TCAMP.csproj`, `src\Core\PluginMetadata.cs`, and
this README, stages all non-ignored changes, promotes the `CHANGELOG.md`
`Unreleased` section into the new version, builds `TCAMP.dll`, creates
`release\TCAMP-v0.3-plugin.zip`, commits, tags, pushes `main` and the tag, and
creates the GitHub release with the changelog notes, zip, and TCAMP.dll SHA256
checksum attached.

Before running it, replace the placeholder under `CHANGELOG.md` `Unreleased`
with the release notes for the next version.

Use this helper instead of manually pushing a release tag. GitHub-hosted runners
do not have the local Tiny Combat Arena, Unity, or BepInEx reference DLLs needed
to build the plugin, so the workflow only validates repository hygiene on tag
pushes.

Useful options:

- `-StagedOnly` commits only your already staged changes plus the version bump.
- `-Closes 39,40` adds `Fixes #39` and `Fixes #40` to the release commit.
- `-RunTests` runs `dotnet test .\TCAMP.sln -c Release` before committing.
- `-NoPush` creates the local commit, tag, and zip without pushing or uploading.

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
