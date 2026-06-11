# Changelog

All notable changes to TCAMP are tracked here.

## Unreleased

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
