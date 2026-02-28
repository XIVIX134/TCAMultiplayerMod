# Draft: TCA Multiplayer Overhaul

## Key Discovery: Most Reflection Is UNNECESSARY

The game's API surface is ~80% PUBLIC. Of 859 reflection calls across 32 files, the vast majority can be replaced with direct type access since Assembly-CSharp.dll is already a compile-time reference.

### Verified Public APIs (no reflection needed):
- `GameDataAircraft.SpawnAircraft()` — Aircraft spawning
- `GameDataAircraft.GetByName()` — Aircraft data lookup
- `GameDataStores.SpawnStore()` / `HasStore()` — Store/missile spawning
- `GameDataBullets.GetByName()` — Bullet data lookup
- `GameDataGuns.GetByName()` — Gun data lookup
- `GameDataLoadouts` — Loadout management
- `Target` — Full public: Position, Velocity, Faction, Coalition, IsDestroyed, SetFaction()
- `TargetManagement` — Full public: AllTargets, RegisterTarget(), UnregisterTarget(), OnTargetAdded/Removed events
- `Radar` — Public: ActiveRadars (static list), LockedTarget, LockTarget(), UnlockTarget(), IsActive, OwnTarget
- `ThreatWarning` — Public: Threats, Missiles, Refresh(), IsMissileAThreat() (static)
- `Munition` — Public: LaunchedMissiles (static list), HasExploded, Target, Seeker
- `Seeker` — Public: IsTracking, IsSpoofed, Target
- `Damageable` — Public: HitPoints, IsDestroyed, MaxHitpoints, ApplyDamageFromImpact(), ApplyDamageFromExplosion()
- `DamageSource` — Public struct, full constructor
- `Explosion` — Public: Trigger() (static), OnExplosion event
- `FloatingOrigin` — Public: TotalOffset (static), Instance, OnOriginShiftStart/Finished events, Blacklist
- `Bullet2Manager` — Public: Instance (static), FireBullet()
- `Gun2` — Public fields: IsFiring, GunData, AmmoBelt, Barrels, OwnTarget, Ammo
- `UniAircraft` — Public: HasBeenDestroyed, Radar, ThreatWarning, Engines, Fuel, Flaps, Countermeasures, FlightDamage, OnAircraftDestroyed event
- `UniPilot` — Public property on UniAircraft

### What DOES still need reflection:
- Private backing fields for auto-properties (e.g., `HasBeenDestroyed` setter is private)
- Internal flight model values (control surface angles, stick inputs from FlightInput)
- Some private fields on Munition for seeker configuration
- Private pool internals for VFX caching

## Current Mod Problems Summary
1. **RealCombatSync.cs** — 3,292-line god class with 171 reflection calls
2. **FlightGamePatches.cs** — 1,580-line per-frame reflection monster
3. **Plugin.cs** — God object coupling everything
4. **859 reflection calls across 32 files** — ~700 unnecessary
5. **100+ static mutable fields** — Not reset between sessions
6. **Thread safety violations** — UDP socket shared between threads
7. **50 catch blocks** — Most swallow exceptions silently
8. **No tests** — Zero test infrastructure

## What's Currently Synced (and working)
- Aircraft state (pos, rot, vel, controls, flags) at ~128Hz
- Missile launches, radar locks, gun firing
- Damage (shooter authority), explosions, destruction VFX
- World destruction (craters, buildings)
- Lobby system with mod compatibility
- Respawn flow with scoreboard

## What's Missing
- Fuel state, ammo counts
- Partial damage (component-level, not just binary alive/dead)
- Proper N-player support (code supports it, UI doesn't)
- Steam P2P networking (NAT traversal)
- Mixed aircraft types in same session (partially working)

## Architectural Recommendation: Clean Rewrite

### Why rewrite vs refactor:
- 700+ unnecessary reflection calls need to be replaced with direct access — this touches every file
- God classes need complete restructuring — can't incrementally split
- Static state management needs fundamental redesign
- The WHAT to sync is correct; the HOW is wrong in every file

### Proposed architecture:
1. **Direct type access layer** — Replace reflection with `using Falcon.*` imports
2. **Proper component-based design** — Small, focused classes with clear responsibilities
3. **Event-driven sync** — Use game events instead of polling where possible
4. **Clean state management** — Single GameSession object, no static mutable state
5. **Transport abstraction** — Support both Direct UDP and Steam P2P
6. **Proper cleanup** — IDisposable pattern, session lifecycle management

## Research Findings
- FloatingOrigin has public events (OnOriginShiftStart/Finished) — can hook directly
- Explosion has public OnExplosion event — can hook for sync instead of Harmony patches
- TargetManagement has OnTargetAdded/OnTargetRemoved events — can track targets
- UniAircraft has OnAircraftDestroyed event — can hook for destruction
- Native game spawning (GameDataAircraft.SpawnAircraft) is the clean path for remote aircraft

## Open Questions
- Transport: Keep raw UDP + add Steam P2P? Or use LiteNetLib?
- Player count: Design for 2 initially, or architect for N from start?
- Scope: Include partial damage model? Or just clean up what exists?
- Testing: Add test infrastructure? TDD approach?
