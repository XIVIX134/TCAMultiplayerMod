# Changelog

All notable changes to TCAMP are tracked here.

## Unreleased

- Refactored Steam P2P transport: fixed thread safety in peer tracking, replaced magic byte markers with named constants, improved lobby metadata propagation (map name now set in lobby metadata).
- Added `UpdateLobbyMap` to `SteamP2PTransport` so the lobby browser always shows the current map.
- Fixed lobby browser: version check before joining, self-join prevention, better error messages for common failure scenarios (missing host_steamid, full lobby, version mismatch).
- Added 15-second connection timeout for clients — auto-disconnects if the host doesn't respond.
- Added `OnConnectionFailed` event to `ConnectionManager` for UI notification of connection failures.
- Added `CreateTransport` factory in `PluginRunner` for cleaner transport initialization.
- Wired lobby state changes to update Steam lobby metadata automatically.
- Added TCAMM-style XML documentation to `SteamP2PTransport`, `ConnectionManager`, and `MultiplayerMenu`.
- Fixed kicked Steam clients lingering in the session until a timeout — a player removed by the host now disconnects immediately.
- Hardened the host against spoofed aircraft positions, so a client can no longer send movement updates on another player's behalf.
- Made the mod load-order check more reliable by removing a fragile dependency on game internals (no change to in-game behavior).
- Fixed a network connection being leaked when switching between Steam and Direct IP hosting in the multiplayer menu.
- Add new changes here before running `.\scripts\Release-Version.ps1`.

## v0.2.3 - 2026-06-15

- Added an optional in-game updater that checks the latest GitHub release on launch.
- Added update staging with DLL verification, backup/pending files, and a restart prompt that applies the new plugin and relaunches the game.
- Changed release packaging to publish a `TCAMP.dll` SHA256 asset instead of a zip checksum, matching the updater's DLL-based hash check.
- Added a Mods folder backup before client mod sync.
- Added the loaded TCAMP version beside the game's native bottom-left version label on the main menu.
- Improved confirmation popup sizing and text autosizing so longer warnings are not cut off.
- Added updater tests for release version comparison, GitHub asset selection, SHA256 parsing, and plugin DLL extraction from release zips.
- Bumped package, assembly, plugin, and README version metadata to `0.2.3`.

## v0.2.2 - 2026-06-15

- Added host/client TCAMP version checks so mismatched builds are rejected with a clear status message.
- Added Mods folder compatibility checks before clients can ready up or mutate lobby selections.
- Added one-click client mod sync from the host for safe mod data files, including chunked transfer progress and game-data reload after sync.
- Added a red confirmation warning before mod sync overwrites changed mod files or removes extra sync-safe files.
- Added an in-game updater that checks GitHub releases at launch, verifies SHA256 checksums, stages changed plugin files, and prompts for a restart.
- Added lobby UI states for mod verification, sync progress, sync cancellation, and blocked/unsyncable mod mismatches.
- Improved direct UDP connection diagnostics with user-facing route/status messages, RTT/traffic snapshots, and version-rejection reporting.
- Improved direct UDP reliability on VPN/TUN and unstable links with automatic local route selection, endpoint refresh handshakes, reconnect grace handling, and low-bandwidth transport presets.
- Added adaptive aircraft-state send rates that back off when reliable queues, retransmits, RTT, or receive gaps indicate poor network quality.
- Improved FlightGame scene teardown and UI rebuild handling when returning to lobby or starting a new session.
- Fixed lobby flow so verified clients announce their configured player name and selections after mod checks complete.
- Added tests for mod file manifest safety/sync behavior, packet serialization, reliability retransmit throttling, lobby verification gates, and transport reconnect behavior.
- Bumped package, assembly, plugin, and README version metadata to `0.2.2`.

## v0.2.1 - 2026-06-11

- Fixed the in-flight/menu cursor state so the pointer hides in flight and returns in menus.
- Fixed Return To Lobby so host/client sessions tear down the flight scene, clear aircraft/clones, and reopen the lobby cleanly.
- Added a reconnect grace window for direct UDP peers to prevent brief dropouts from removing players and desyncing remote damage visuals.
- Prevented late aircraft-state packets from recreating remote clones after returning to the lobby.

## v0.2 - 2026-06-11

- Synced damageable part hits and destroyed parts.
- Mirrored remote aircraft swing wings, spinners, and formation lights.
- Improved remote death/ejection VFX, including burning wrecks and pilot ejection.
- Reset player flight input on respawn to prevent stale ejection state.
- Added respawn-screen aircraft/loadout changes for the next life.
- Added multiplayer Esc menu support with host return-to-lobby and client leave-session flows.
- Bumped package metadata to `0.2.0`.

## v0.1 - 2026-06-04

- First public source release.
