# TCA Multiplayer Mod

A multiplayer mod for [Tiny Combat Arena](https://store.steampowered.com/app/1347550/Tiny_Combat_Arena/) (v0.14.1.4) that enables 2-player PvP dogfighting with direct UDP networking.

## Features

- **Direct UDP Networking:** Fast, low-latency state synchronization (128 Hz).
- **Shooter Authority:** Reliable hit detection.
- **Visual Synchronization:** Syncs aircraft position, rotation, control surfaces, gear, flaps, afterburners, and muzzle flashes.
- **Combat Systems:** Functional missiles, radar locks, and damage synchronization.

## Installation
1. Download the latest release.
2. Extract `TCAMultiplayer.dll` into your game's `BepInEx/plugins` folder.
3. Launch the game.
4. Click the **Multiplayer** button on the main menu to get started.

## Development
See [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) for detailed architecture and roadmap.

### Build Instructions
1. Copy the required game DLLs to the [`libs/`](libs/) folder.
2. Open [`TCAMultiplayer.sln`](TCAMultiplayer.sln) in Visual Studio.
3. Build for Release.

## License
[License Information Here]
