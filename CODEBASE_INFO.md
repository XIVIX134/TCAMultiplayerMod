# TinyCombatArenaMP Codebase Info

Last updated: 2026-02-25

## 1) What this repository is

`TinyCombatArenaMP` is a BepInEx + Harmony multiplayer mod for Tiny Combat Arena.

- Project type: Unity runtime mod (`net472`)
- Main output: `src/bin/Release/net472/TCAMultiplayer.dll`
- Current in-repo version constant: `0.2.2` (see `src/Plugin.cs`)
- Solution: single C# project (`TCAMultiplayer.sln` -> `src/TCAMultiplayer.csproj`)

Core goals implemented in code:

- 2-player direct UDP multiplayer (with infrastructure to scale to N remote states in parts of the code)
- Lobby flow (host/join/ready/load/spawn)
- High-rate aircraft state sync
- Combat sync (guns, missiles, bombs, radar lock, damage, explosions, aircraft destruction VFX)
- Score tracking + HUD + respawn flow
- Mod compatibility handshake before play


## 2) Tech stack and dependencies

- Language/runtime: C# on `.NET Framework 4.7.2`
- Mod framework: `BepInEx`
- Patching: `Harmony`
- Async utilities: `UniTask`
- Engine APIs: UnityEngine + game types from `Assembly-CSharp`

Key referenced DLLs are in `libs/`:

- `BepInEx.dll`, `0Harmony.dll`
- `Assembly-CSharp.dll` (game code)
- Unity modules (`UnityEngine.*`, `Unity.TextMeshPro`, etc.)
- Optional Steamworks reference (`Facepunch.Steamworks.Win64.dll`) though active transport is Direct UDP


## 3) Repository structure

Top-level:

- `src/` main mod source
- `libs/` local dependency DLLs used for compile-time references
- `TinyCombatArena[Host]/`, `TinyCombatArena[Client]/` local game instances for test/deploy
- `BuildAndDeploy.bat` build and copy DLL to host/client plugin folders
- `RunMultiplayerTest.bat` launch both game instances
- `.github/workflows/build.yml` CI build + artifact/release zip

`src/` source modules:

- `Plugin.cs` mod bootstrap and frame runner
- `Networking/` transport, packet protocol, lobby/network managers, interpolation, remote aircraft lifecycle
- `Patches/` Harmony hooks into base game systems
- `Player/` remote aircraft behavior, combat sync, collision manager
- `Game/` spawn/airfield/loadout helpers + scoring
- `UI/` native Canvas/TMP menu, respawn, scoreboard, UIFactory
- `ModCompatibility/` manifest collection + host/client compatibility checks
- `Logging/` category logging + host/client log file separation

Code scale (non-generated C# only):

- 43 source files
- ~22,866 lines

Largest files:

- `src/Player/RealCombatSync.cs` (2622)
- `src/Networking/PacketTypes.cs` (1559)
- `src/Networking/RemoteAircraftManager.cs` (1514)
- `src/Patches/FlightGamePatches.cs` (1414)


## 4) Runtime architecture (high level)

Main startup path:

1. `Plugin.Awake()`
2. Creates core managers (`GameStateMachine`, `NetworkManager`, `LobbyManager`, `LanDiscovery`, `SpawnManager`, `ScoreTracker`, `ScoreboardHUD`)
3. Applies Harmony patches (automatic + manual patch entrypoints)
4. Creates persistent `PluginRunner` GameObject
5. Initializes combat/respawn/collision systems

Per-frame loop (`PluginRunner.Update`/`LateUpdate`):

- `NetworkManager.Update()`
- `LanDiscovery.Update()`
- `LobbyManager.Update()`
- `RealCombatSync.PeriodicCleanup()`
- `GameStateMachine.Update()`
- `ScoreboardHUD.Update()`
- `NetworkManager.LateUpdate()` for remote interpolation apply

Flow control is centralized through `GameStateMachine` states:

- `Disconnected`
- `HostingLobby`
- `Connecting`
- `ClientLobby`
- `Loading`
- `WaitingForPlayers`
- `Spawning`
- `InGame`
- `Respawning`


## 5) Networking model

### Transport

`src/Networking/DirectTransport.cs` implements `INetworkTransport`:

- UDP sockets + receive thread
- Simple handshake:
  - `MSG_CONNECT`
  - `MSG_CONNECT_ACK`
- Keepalive:
  - ping every 2s
  - timeout at 10s
- Reliability layer for selected packets:
  - `MSG_RELIABLE_DATA` with sequence number
  - ACK + retransmit (`0.2s` retry, max `5`)
  - dedup cache for received sequences

### NetworkManager responsibilities

`src/Networking/NetworkManager.cs`:

- Owns transport + packet routing
- Registers handlers for all packet types
- Sends/receives packets through `PacketSerializer`
- Bridges packet events into domain systems:
  - lobby (`LobbyManager`)
  - remote aircraft (`RemoteAircraftManager`)
  - damage/weapons/explosions/aircraft destruction patches
  - score updates (kill confirms)
  - mod compatibility handshake

### Protocol and packet definitions

`src/Networking/PacketTypes.cs`:

- Packet enum + all payload structs/classes
- Serializer/deserializer methods for each payload
- Packet categories include:
  - state sync (`AircraftState`)
  - combat events (missile/bomb/radar/damage/impact/collision)
  - world sync (craters/buildings/explosions/destruction VFX)
  - lobby/session packets (welcome, joined/left/ready, start/load/spawn, selections)
  - mod compatibility packets

### Sync cadence/config

`src/Networking/NetworkConfig.cs` is the central constants file:

- default port `7777`
- normal state send interval `0.0078s` (~128Hz)
- throttled interval `0.0333s` (~30Hz)
- lobby broadcast every `1s`
- interpolation buffer capacity `120`


## 6) Remote aircraft lifecycle and interpolation

`src/Networking/RemoteAircraftManager.cs` manages each peer in a `RemoteAircraftState`.

Important behavior:

- Each peer keeps:
  - interpolation buffer
  - sequence tracking
  - clock offset
  - desired aircraft type
  - respawn state
- Incoming `AircraftStatePacket`:
  - drops out-of-order packets
  - stores absolute coordinates in interpolation buffer
  - updates remote visual controller state
- Aircraft creation strategy:
  1. try native game spawn (`SpawnAircraft`)
  2. fallback clone local aircraft (`AircraftCloneConfigurer`)
  3. fallback marker + periodic retry

Interpolation:

- Implemented in `src/Networking/InterpolationBuffer.cs`
- Uses local receive timeline with interpolation delay (`0.15s`)
- Stores absolute coordinates to survive floating-origin shifts
- Converts absolute -> local only at render application
- Extrapolation intentionally minimized to reduce jitter


## 7) Floating origin handling

`src/Networking/FloatingOriginHelper.cs` abstracts game floating-origin offsets.

Used across state/damage/impact/explosion sync:

- Local -> absolute before packet send
- Absolute -> local after packet receive

`SpawnManager` also explicitly sets floating-origin reference object after respawn to avoid precision/cockpit jitter issues.


## 8) Harmony patch surface (game integration)

Core patch files and responsibilities:

- `FlightGamePatches.cs`
  - main update hook for sending local state
  - respawn/death detection + aircraft changed notifications
  - calls missile/bomb/radar polling each frame
- `DamagePatches.cs`
  - shooter-authority damage on remote clone
  - receiver-side validation and local damage application
  - impact FX packet sync
- `WeaponPatches.cs`
  - polls launched missiles/munitions for reliable detection
  - sends missile launch/update, bomb drop, radar lock packets
  - receives and forwards to `RealCombatSync`
- `ExplosionPatches.cs`
  - syncs explosion VFX/effects (including modded fallback paths)
  - includes dedup logic and crater integration
- `WorldDestructionPatches.cs`
  - crater/building destruction sync
- `AircraftDestructionPatches.cs`
  - syncs crash/destruction VFX by invoking native `DestroyAircraft` on clone
- `EnvironmentPatches.cs`
  - deterministic cloud/wind behavior during multiplayer sessions
- `MainMenuPatches.cs`
  - injects Multiplayer button into game main menu
- `UniPilotPatches.cs`
  - remote aircraft registration + remote gun control diagnostics/patching


## 9) Combat sync implementation details

`src/Player/RealCombatSync.cs` is the combat integration core:

- Remote radar setup and lock propagation for native RWR behavior
- Spawns network missiles via game data/resources and injects into native launched lists
- Handles missile target-state updates
- Spawns network bombs and tracks them in launched munitions
- Remote gun firing support:
  - works through game `FireControl`/`Gun2` when possible
  - fallback direct bullet firing path via `Bullet2Manager`
- Periodic cleanup of stale network missiles/bombs

`src/Player/RemoteAircraftController.cs` applies visual state:

- control surfaces, gear/flaps/nozzles
- afterburner/engine FX
- muzzle flashes
- countermeasure state updates
- destruction handling

`src/Player/AircraftCollisionManager.cs`:

- host-authority manual collision checks for kinematic remote aircraft
- computes collision damage, applies host-local damage, broadcasts collision packet


## 10) Lobby, spawning, and game flow

`LobbyManager`:

- player roster + selections + ready/loaded states
- host state broadcast with hash dirty-check
- host options: spawn type, time of day, aircraft collisions
- mod sync state UI flags (checking/compatible/incompatible)

`SpawnManager`:

- resolves factions and spawn points via reflection helpers
- spawns local player at airfield or position
- applies selected loadout/ammo belt logic
- raises spawn/death events for UI/game-state flow

`AirfieldHelper` + `LoadoutHelper`:

- reflection wrappers around game data for airfields, aircraft, loadouts


## 11) UI system

UI is native Unity Canvas/TMP-based (not IMGUI), except debug overlay in `PluginRunner.OnGUI`.

Key UI classes:

- `MultiplayerMenu` host/join/lobby/browse/direct-connect flows
- `RespawnScreen` death overlay with airfield/loadout selectors and respawn action
- `ScoreboardHUD` compact K/D, kill feed, TAB scoreboard
- `UIFactory` clones native game UI prefabs and provides reusable controls
- `LobbyUI.cs` shared screen enum/state declarations


## 12) Mod compatibility handshake

`ModManifestCollector`:

- collects BepInEx plugins
- collects TCA Mods folder entries + enable state
- collects custom content hashes from data directories
- serializes manifest for network transfer

Compatibility rules:

- strict: enabled game mods/content parity + required multiplayer plugin/version compatibility
- non-strict warnings: extra/missing unrelated BepInEx plugins

Host can reject and kick incompatible clients after sending reason.


## 13) Logging and diagnostics

- `LogHelper` categories + sampling/throttling config flags
- `InstanceLogger` writes host/client-separated log files under `BepInEx/TCAMultiplayer`
- Many subsystems include explicit diagnostic logs for reflection state and high-frequency systems


## 14) Build, test, and CI

### Local build

```powershell
dotnet build TCAMultiplayer.sln -c Release
```

Status verified in this workspace:

- Build succeeded
- 0 warnings, 0 errors

### Helper scripts

- `BuildAndDeploy.bat` builds and copies DLL to:
  - `TinyCombatArena[Host]\BepInEx\plugins`
  - `TinyCombatArena[Client]\BepInEx\plugins`
- `RunMultiplayerTest.bat` clears old logs and launches both game executables

### CI

`.github/workflows/build.yml`:

- builds on `windows-latest`
- uploads DLL artifact
- on `v*` tags, zips DLL and creates GitHub release

`.github/workflows/mini-swe-agent.yml`:

- triggered by `issue_comment` events on **issues** (not PRs) that contain `/mini-swe-fix`
- installs [mini-swe-agent](https://github.com/SWE-agent/mini-swe-agent) (Python package) and runs it against the issue URL
- uses Anthropic provider (`anthropic/claude-opus-4-6`); secrets: `LLM_API_KEY` → `ANTHROPIC_API_KEY`, optional `LLM_BASE_URL` → `ANTHROPIC_BASE_URL`
- commits any resulting changes to a new branch `mini-swe-fix/issue-<number>` and opens a ready-for-review PR targeting `main`; exits silently if the agent produces no changes
- requires `PAT_TOKEN` (repo-scoped personal access token) and `PAT_USERNAME` for git authorship and PR creation
- PRs are never auto-merged; all changes require human review before merging
- see the [README.md "Automated Issue Fixing" section](README.md#automated-issue-fixing-mini-swe-agent) for full usage instructions and troubleshooting


## 15) Important notes and drift observed

- Docs/UI hotkey mismatch:
  - `README.md` and some planning docs still mention F8 opening menu.
  - Current code injects a **Main Menu Multiplayer button** (`MainMenuPatches`).
  - `PluginRunner` currently uses:
    - `F7` debug panel toggle
    - `F8` bandwidth throttle toggle
- `src/bin` and `src/obj` are present in repo tree but are build artifacts, not source architecture.

