# Changelog

All notable changes to TCAMP are tracked here.

## Unreleased

- Add new changes here before running `.\scripts\Release-Version.ps1`.

## v0.3.2 - 2026-06-23

- Added multiplayer Discord Rich Presence updates for lobbies, loading, gameplay, downed, and respawn states, including map/aircraft display names and a stable match timer.
- Improved damage sync so critical-hit settings, hit collider paths, and weapon categories are preserved when remote damage is serialized and applied.
- Fixed remote damage mirroring so non-local victim clones can show replicated hits without being destroyed by another player's damage packet.
- Improved multiplayer menu sizing for host setup and confirmation dialogs so longer labels and warning text fit more reliably.
- Improved Direct IP hosting and joining so the default port is automatic, custom ports live behind an advanced control, pasted `address:port` values are handled, and timeout messages explain the default/custom port path.

## v0.3.1 - 2026-06-20

- Improved Direct IP routing so hosts listen on all IPv4 adapters while clients dynamically choose the correct local route for LAN, VPN/tunnel, or public IP joins.
- Fixed host relay validation for radar lock-lost packets so client unlock events can reach other peers.
- Tightened lobby packet handler cleanup so disposing the lobby only removes its own router handlers.

## v0.3.0 - 2026-06-17

Thanks to @1w7g0 for the Steam Integration PR that provided the Steam multiplayer transport, lobby browser, and related hosting/joining improvements in this release.

### Added

- **Steam multiplayer** — host and join games over Steam with no port forwarding (NAT traversal runs through Steam's relay network), plus a built-in lobby browser that lists open TCAMP sessions by server name, map, and player count for one-click joining.
- Choice of Steam or Direct IP when hosting or joining, selectable in the multiplayer menu.
- Steam host options: lobby visibility (Public or Friends Only) and a toggle to require clients to run matching mods.

### Changed

- Aircraft and loadout pickers now reflect host-enabled availability in the lobby and respawn screen, so players can't select options the host hasn't enabled.
- Mod sync now covers Steam Workshop mods — the host's enabled Workshop mods are included in the compatibility check and sync, and extra Workshop mods on a client are disabled to match the host.
- Joins now time out after 15 seconds instead of hanging on an unresponsive host.
- More reliable aircraft spawning, with airfield/player-slot-based spawn positions and clearer handling of spawn failures.

### Internal

- Mod load-order handling no longer depends on game internals, making it more robust and unit-testable (no change to in-game behavior).
- Added developer documentation and tests across the new transport, selection, and mod-sync code.

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
