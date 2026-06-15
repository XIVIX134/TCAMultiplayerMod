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
- Add new changes here before running `.\scripts\Release-Version.ps1`.

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
