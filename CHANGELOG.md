# Changelog

All notable changes to TCAMP are tracked here.

## Unreleased

- Add new changes here before running `.\scripts\Release-Version.ps1`.

## v0.2.2 - 2026-06-15

- Added host/client TCAMP version checks so mismatched builds are rejected with a clear status message.
- Added Mods folder compatibility checks before clients can ready up or mutate lobby selections.
- Added one-click client mod sync from the host for safe mod data files, including chunked transfer progress and game-data reload after sync.
- Added a red confirmation warning before mod sync overwrites changed mod files or removes extra sync-safe files.
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
