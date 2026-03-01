# TCAMultiplayerMod

## Project Description

TCAMultiplayerMod is a BepInEx + Harmony multiplayer mod for **Tiny Combat Arena** (v0.14.1.4), a Unity-based combat flight game. It enables 2-player PvP dogfighting over direct UDP networking with 128 Hz state synchronization. The mod handles aircraft state sync (position, rotation, control surfaces, gear, flaps, afterburners), combat systems (guns, missiles, bombs, radar locks, damage), a lobby/matchmaking flow, score tracking, respawn logic, and a mod-compatibility handshake between host and client.

## File Structure

- **`src/`** — All C# source code (~43 files, ~23k lines), organized into:
  - `Plugin.cs` — Mod entry point and frame runner (BepInEx plugin bootstrap)
  - `Networking/` — UDP transport (`DirectTransport`), packet protocol (`PacketTypes`), lobby/network managers, interpolation buffer, remote aircraft lifecycle, floating-origin helpers
  - `Patches/` — Harmony patches hooking into game systems (flight state, damage, weapons, explosions, world destruction, main menu, environment)
  - `Player/` — Remote aircraft behavior (`RemoteAircraftController`), combat sync (`RealCombatSync`), collision manager
  - `Game/` — Spawn, airfield, loadout helpers, and score tracking
  - `UI/` — Native Unity Canvas/TMP-based multiplayer menu, respawn screen, scoreboard HUD
  - `ModCompatibility/` — Manifest collection and host/client compatibility checks
  - `Logging/` — Category-based logging with host/client log file separation
- **`libs/`** — Compile-time reference DLLs (BepInEx, Harmony, Unity engine modules, game `Assembly-CSharp.dll`)
- **`TCAMultiplayer.sln`** — Visual Studio solution (single project targeting .NET Framework 4.7.2)
- **`BuildAndDeploy.bat`** — Builds and copies the DLL to local host/client BepInEx plugin folders
- **`RunMultiplayerTest.bat`** — Clears logs and launches two game instances for local testing
- **`.github/workflows/build.yml`** — CI: builds on Windows, uploads DLL artifact, creates GitHub releases on `v*` tags

## Building and Testing

**Build:**
```bash
dotnet build TCAMultiplayer.sln -c Release
```
Output DLL: `src/bin/Release/net472/TCAMultiplayer.dll`

**Local testing** requires two running copies of Tiny Combat Arena with BepInEx installed. Place the built DLL in each instance's `BepInEx/plugins/` folder. Use `BuildAndDeploy.bat` to automate this, and `RunMultiplayerTest.bat` to launch both instances. The default network port is `7777`.

There is no automated test suite — testing is done manually by running two game instances (host + client) and verifying sync, combat, and UI behavior. See `DEVELOPMENT_PLAN.md` for a detailed test checklist.

## Key Things for New Developers

- The networking model uses **shooter-authority** hit detection over raw UDP with a simple reliability layer for important packets (weapons, damage, lobby events).
- The game uses a **floating origin** system for large-world coordinates; all network positions are transmitted as absolute doubles and converted to/from local space using `FloatingOriginHelper`.
- Remote aircraft are created by cloning the local player's aircraft GameObject, disabling physics/AI, and attaching a `RemoteAircraftController` for visual state application.
- Game flow is managed by `GameStateMachine` with states: Disconnected → HostingLobby/Connecting → ClientLobby → Loading → WaitingForPlayers → Spawning → InGame → Respawning.
- `CODEBASE_INFO.md` and `DEVELOPMENT_PLAN.md` contain extensive architecture documentation, known issues, and the development roadmap.
- The CI workflow (`.github/workflows/build.yml`) runs on `windows-latest` with .NET 6 SDK.
