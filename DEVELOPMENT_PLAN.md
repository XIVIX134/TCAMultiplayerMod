# TCA Multiplayer Mod - Development Plan

## Project Overview

**Project Name:** TCAMultiplayer  
**Target Game:** Tiny Combat Arena v0.14.1.4  
**Game Engine:** Unity (IL2CPP)  
**Mod Framework:** BepInEx 5.4.23 + Harmony  
**Goal:** 2-player PvP dogfighting with Direct UDP networking  
**Start Date:** January 2025  
**Current Status:** Alpha - Core functionality working, bugs being fixed  

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Project Structure](#project-structure)
3. [Completed Features](#completed-features)
4. [Recent Bug Fixes](#recent-bug-fixes)
5. [Known Issues](#known-issues)
6. [Roadmap](#roadmap)
7. [Technical Deep Dive](#technical-deep-dive)
8. [Development Workflow](#development-workflow)
9. [Testing Procedures](#testing-procedures)
10. [Build & Deployment](#build--deployment)
11. [Troubleshooting Guide](#troubleshooting-guide)

---

## Architecture Overview

### High-Level Design

```
┌─────────────────────────────────────────────────────────────────┐
│                        PLAYER 1 (Host)                          │
│  ┌─────────────┐    ┌──────────────┐    ┌───────────────────┐  │
│  │ Local       │───▶│ FlightGame   │───▶│ NetworkManager    │  │
│  │ Aircraft    │    │ Patches      │    │ (UDP Send)        │  │
│  └─────────────┘    └──────────────┘    └─────────┬─────────┘  │
│                                                    │            │
│  ┌─────────────┐    ┌──────────────┐              │            │
│  │ Remote      │◀───│ RemoteAircraft│◀─────────────┘            │
│  │ Clone       │    │ Controller   │   (State packets)         │
│  └─────────────┘    └──────────────┘                           │
└─────────────────────────────────────────────────────────────────┘
                              │
                              │ UDP (Port 7777)
                              │ 128 Hz state updates
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                        PLAYER 2 (Client)                        │
│  ┌─────────────┐    ┌──────────────┐    ┌───────────────────┐  │
│  │ Remote      │◀───│ RemoteAircraft│◀───│ NetworkManager    │  │
│  │ Clone       │    │ Controller   │    │ (UDP Receive)     │  │
│  └─────────────┘    └──────────────┘    └─────────┬─────────┘  │
│                                                    │            │
│  ┌─────────────┐    ┌──────────────┐              │            │
│  │ Local       │───▶│ FlightGame   │───▶──────────┘            │
│  │ Aircraft    │    │ Patches      │   (State packets)         │
│  └─────────────┘    └──────────────┘                           │
└─────────────────────────────────────────────────────────────────┘
```

### Design Principles

1. **Shooter Authority:** The player who fires calculates hits and sends damage to the victim
2. **State Synchronization:** Position/rotation synced at 128Hz with interpolation
3. **Visual Cloning:** Remote players are displayed using cloned local aircraft GameObjects
4. **Floating Origin Handling:** Absolute world coordinates converted to local space
5. **Minimal Latency:** UDP for state updates, reliable packets only for critical events

---

## Project Structure

```
D:\Tiny.Combat.Arena.v0.14.1.4\TCAMultiplayer\
├── TCAMultiplayer.sln              # Visual Studio solution
├── DEVELOPMENT_PLAN.md             # This file
├── libs\                           # Reference DLLs from game
│   ├── Assembly-CSharp.dll
│   ├── UnityEngine.dll
│   ├── UnityEngine.CoreModule.dll
│   └── ...
└── src\TCAMultiplayer\
    ├── TCAMultiplayer.csproj       # Project file
    ├── Plugin.cs                   # Main entry point + F8 UI toggle
    │
    ├── Networking\
    │   ├── NetworkManager.cs       # Core network logic + aircraft cloning
    │   ├── InterpolationBuffer.cs  # Hermite spline interpolation (30 samples)
    │   ├── PacketTypes.cs          # Packet definitions + serialization
    │   ├── DirectTransport.cs      # Raw UDP socket implementation
    │   ├── FloatingOriginHelper.cs # Coordinate system conversion
    │   └── MarkerComponents.cs     # Debug visual markers
    │
    ├── Player\
    │   ├── RemoteAircraftController.cs  # Controls cloned aircraft visuals
    │   └── RemotePlayer.cs              # Remote player data container
    │
    └── Patches\
        ├── FlightGamePatches.cs    # Reads & sends local aircraft state
        ├── DamagePatches.cs        # Damage sync (shooter authority)
        └── WeaponPatches.cs        # Missile launch & radar lock sync
```

---

## Completed Features

### Phase 1: Core Networking (COMPLETE)
- [x] BepInEx plugin structure
- [x] Direct UDP transport layer
- [x] Packet serialization/deserialization
- [x] Host/Client connection flow
- [x] 128Hz state update rate
- [x] Connection UI (F8 to toggle)

### Phase 2: Aircraft Synchronization (COMPLETE)
- [x] Aircraft cloning system
- [x] Position/rotation sync with interpolation
- [x] Hermite spline interpolation buffer (30 samples)
- [x] Floating origin coordinate handling
- [x] Extrapolation for packet loss

### Phase 3: Visual State Sync (COMPLETE)
- [x] Landing gear animation
- [x] Flaps position
- [x] Control surfaces (ailerons, elevators, rudder)
- [x] Nozzle angle (for VTOL aircraft like AV-8B)
- [x] Afterburner effects
- [x] Muzzle flash effects
- [x] Throttle-based engine visuals

### Phase 4: Combat Systems (COMPLETE)
- [x] Gun damage synchronization (shooter authority)
- [x] Missile launch detection and notification
- [x] Radar lock/unlock synchronization
- [x] Target registration in TargetManagement.AllTargets
- [x] RCS curve configuration for radar detection
- [x] IR signature configuration for heat seekers
- [x] Damageable component setup

### Phase 5: Death & Respawn (IN PROGRESS)
- [x] Aircraft destroyed notification
- [x] IsDestroyed flag to prevent multiple triggers
- [x] Collider disabling on death
- [x] Respawn packet handling
- [x] Timeout-based recreation (30s fallback)
- [ ] Explosion visual effects
- [ ] Respawn position sync
- [ ] Score tracking

---

## Recent Bug Fixes

### Session: January 23, 2025

#### Bug 1: Multiple "Remote aircraft destroyed" Messages
**Symptom:** Log showed multiple destroy calls and damage after destruction  
**Root Cause:** No guard against repeated `OnDestroyed()` calls  
**Fix:** Added `IsDestroyed` property to `RemoteAircraftController`
```csharp
public bool IsDestroyed { get; private set; } = false;

public void OnDestroyed()
{
    if (IsDestroyed) return; // Guard
    IsDestroyed = true;
    // ... disable renderers and colliders
}
```

#### Bug 2: Damage Sent to Destroyed Aircraft
**Symptom:** Bullets kept hitting destroyed aircraft clone  
**Root Cause:** No check for destroyed state in damage patch  
**Fix:** Added check in `DamagePatches.ApplyDamageFromImpact_Prefix`
```csharp
if (controller.IsDestroyed)
{
    Plugin.Log?.LogInfo("[DamagePatches] Remote aircraft already destroyed, ignoring damage");
    return false;
}
```

#### Bug 3: Premature Re-cloning After Destroy
**Symptom:** New clone created immediately from state packets  
**Root Cause:** `HandleRemoteAircraftState` created aircraft whenever `_remoteAircraftObject` was null  
**Fix:** Added respawn flag check and timeout mechanism
```csharp
if (_remoteAircraftNeedsRespawn)
{
    // Only allow recreation after Respawned packet OR 30s timeout
    if (Time.time - _remoteDestroyedTime > RESPAWN_TIMEOUT)
    {
        _remoteAircraftNeedsRespawn = false;
        CleanupRemoteAircraft();
    }
    else
    {
        return; // Wait for respawn packet
    }
}
```

#### Bug 4: Wrong Seeker Type for Missiles
**Symptom:** AIM-9L logged as seeker type 2 (unguided) instead of 0 (IR)  
**Root Cause:** Reflection to get `seekerType` field was failing  
**Fix:** Added name-based fallback detection
```csharp
if (seekerType == 2) // Reflection failed
{
    string upperName = missileName.ToUpperInvariant();
    if (upperName.Contains("AIM-9") || upperName.Contains("SIDEWINDER"))
        seekerType = 0; // IR
    else if (upperName.Contains("AIM-120") || upperName.Contains("AMRAAM"))
        seekerType = 1; // Radar
}
```

---

## Known Issues

### High Priority
| Issue | Description | Status |
|-------|-------------|--------|
| No explosion VFX | Destroyed aircraft just disappears | **Fixed** (CombatVfxManager) |
| No visual missiles | Victim doesn't see incoming missiles | **In Progress** (RealCombatSync) |
| RWR not functional | Radar lock notification only logs, no audio/visual | **In Progress** (ThreatWarning integration) |
| Seeker configuration | Cloned missiles may lack proper Seeker for warnings | **Fixed** (CreateSeeker method) |

### Medium Priority
| Issue | Description | Status |
|-------|-------------|--------|
| Canopy visibility | Some aircraft canopies may not render correctly | Monitoring |
| LOD forcing | Remote aircraft forced to LOD0, may impact performance | Monitoring |
| No bullet tracers | Victim doesn't see incoming tracers | **Fixed** (FireControl patch fires real bullets) |
| Missile immediate explosion | Network missiles exploding too early | **Fixed** (MIN_FLIGHT_TIME added) |

### Low Priority
| Issue | Description | Status |
|-------|-------------|--------|
| Deprecation warnings | Using obsolete `FindObjectOfType` | Tech debt |
| Unused field warning | `_lastPingTime` in DirectTransport | Tech debt |

---

## Roadmap

### Completed (Real Combat Sync)

#### Native Gun System Integration
**Status:** ✅ Completed

**Implementation:**
- Patched `FireControl.Update()` to allow multiplayer control of `Gun2.IsFiring`
- `RemoteAircraftRegistry` tracks which aircraft are remote
- When remote aircraft fires, the game's actual `Gun2` system fires real bullets
- Same bullet physics, damage, and visual effects as local player

**Key Files:**
- `UniPilotPatches.cs` - `FireControlPatches.Update_Prefix()`
- `RealCombatSync.cs` - `SetRemoteGunFiring()`
- `RemoteAircraftController.cs` - Registers with `RemoteAircraftRegistry`

#### Missile Threat Warning Integration
**Status:** 🔄 In Progress

**Implementation:**
- Missiles added to `Munition.LaunchedMissiles` for `ThreatWarning.Refresh()` detection
- `NetworkMissileController` manages missile movement/lifetime
- Seeker configured with `IsTracking=true`, `IsSpoofed=false`, proper `Target`
- Falls back to `GameDataStores.SpawnStore()` or creating Seeker if missile lacks one

**Key Files:**
- `RealCombatSync.cs` - `AddNetworkMissileToThreatSystem()`, `CreateSeeker()`
- `WeaponPatches.cs` - `HandleMissileLaunch()` calls RealCombatSync

### Next Up (Priority Order)

#### 1. Test & Validate Real Combat Sync
**Objective:** Verify bullets fire from remote aircraft and missiles trigger warnings.

**Test Plan:**
1. Host fires gun -> Client sees tracers and takes damage
2. Host launches missile -> Client RWR detects, warning sounds play
3. Verify missile visible and tracking until impact/timeout

#### 2. Radar Lock RWR Integration
**Objective:** Remote player's radar lock triggers real RWR spike on victim.

**Implementation Plan:**
1. When host locks target, send RadarLockPacket
2. Client adds remote aircraft's Radar to `Radar.ActiveRadars`
3. Set `Radar.LockedTarget` to local player's Target
4. Game's `ThreatWarning.Refresh()` auto-detects the lock

**Key Insight:** `ThreatWarning.IsRadarVisible()` checks `Radar.ActiveRadars` list

**Files to modify:**
- `RealCombatSync.cs` - `SetRemoteRadarLock()` to manipulate `Radar.ActiveRadars`

#### 3. Explosion Effects on Death
**Objective:** Show explosion VFX when remote aircraft is destroyed.

**Implementation Plan:**
1. Find explosion prefabs in game assets
2. Instantiate at remote aircraft position
3. Play sound effects
4. Add debris/smoke particles

**Files to modify:**
- `RemoteAircraftController.cs` - `OnDestroyed()`

#### 4. Score Tracking
**Objective:** Track kills/deaths for each player.

**Implementation Plan:**
1. Add score fields to NetworkManager
2. Increment on kill confirmation
3. Display in UI
4. Sync scores between players

**New files:**
- `ScoreManager.cs`
- Update `Plugin.cs` UI

### Future Phases

#### Phase 6: Polish & UX
- [ ] In-game HUD for multiplayer status
- [ ] Connection quality indicator
- [ ] Ping display
- [ ] Player names/callsigns
- [ ] Kill feed notifications

#### Phase 7: Extended Features
- [ ] More than 2 players
- [ ] Team modes
- [ ] Spectator mode
- [ ] Replay system

#### Phase 8: Infrastructure
- [ ] Dedicated server support
- [ ] NAT punch-through
- [ ] Matchmaking server
- [ ] Anti-cheat basics

---

## Technical Deep Dive

### Packet Types

```csharp
public enum PacketType : byte
{
    // Connection
    Ping = 1,
    Pong = 2,
    
    // State sync (unreliable, high frequency)
    AircraftState = 10,      // Position, rotation, controls, effects
    
    // Weapons (reliable)
    MissileLaunch = 32,      // Missile fired at target
    RadarLock = 33,          // Radar locked on target
    RadarLockLost = 34,      // Radar lock broken
    
    // Damage (reliable)
    DamageDealt = 40,        // Hit confirmed, send damage
    AircraftDestroyed = 41,  // Aircraft killed
    
    // Lifecycle (reliable)
    Respawned = 51,          // Player respawned
    AircraftChanged = 52,    // Player changed aircraft type
}
```

### AircraftStatePacket Structure (88 bytes)

```csharp
public struct AircraftStatePacket
{
    // Position (absolute world coords) - 24 bytes
    public double PosX, PosY, PosZ;
    
    // Rotation (quaternion) - 16 bytes
    public float RotX, RotY, RotZ, RotW;
    
    // Velocity - 12 bytes
    public float VelX, VelY, VelZ;
    
    // Angular velocity - 12 bytes
    public float AngVelX, AngVelY, AngVelZ;
    
    // Control inputs - 20 bytes
    public float Throttle, Pitch, Roll, Yaw, NozzleAngle;
    
    // Flags (packed byte) - 1 byte
    public bool Afterburner, GearDown, FlapsDown, IsFiring;
    
    // Timing - 4 bytes
    public float Timestamp;
}
```

### Interpolation System

The `InterpolationBuffer` uses Hermite spline interpolation for smooth movement:

1. **Buffer Size:** 30 snapshots (~234ms at 128Hz)
2. **Interpolation Delay:** 2-3 packets behind real-time
3. **Hermite Spline:** Uses position + velocity for smooth curves
4. **Extrapolation:** Linear extrapolation when buffer underruns
5. **Jitter Handling:** Adaptive delay based on packet timing

```
Timeline:
[T-30] [T-29] ... [T-3] [T-2] [T-1] [T-0 (latest)]
                    ↑
              Interpolation point (slight delay)
```

### Floating Origin System

TCA uses a floating origin to handle large world coordinates:

```csharp
// Convert absolute world position to local (relative to origin)
public static Vector3 AbsoluteToLocal(Vector3d absolutePos)
{
    Vector3d origin = GetCurrentOrigin();
    return new Vector3(
        (float)(absolutePos.x - origin.x),
        (float)(absolutePos.y - origin.y),
        (float)(absolutePos.z - origin.z)
    );
}

// Convert local position back to absolute
public static Vector3d LocalToAbsolute(Vector3 localPos)
{
    Vector3d origin = GetCurrentOrigin();
    return new Vector3d(
        origin.x + localPos.x,
        origin.y + localPos.y,
        origin.z + localPos.z
    );
}
```

### Aircraft Cloning Process

1. **Find Source:** Locate local player's `UniAircraft` component
2. **Clone GameObject:** `GameObject.Instantiate(sourceAircraft)`
3. **Disable Physics:** Set Rigidbody to kinematic
4. **Disable Gameplay:** Disable pilot, input, weapon, flight components
5. **Keep Combat:** Enable Target, Damageable, Signature components
6. **Configure Targeting:** Set faction to "Enemy", coalition to Red
7. **Configure Signatures:** Populate RCS and IR curves for detection
8. **Add Controller:** Attach `RemoteAircraftController` component
9. **Register Target:** Re-enable Target component to trigger registration

---

## Development Workflow

### My Approach

1. **Read First:** Always read existing code before modifying
2. **Understand Context:** Trace through call chains to understand flow
3. **Small Incremental Changes:** One bug fix or feature at a time
4. **Build Frequently:** Compile after each change to catch errors early
5. **Log Liberally:** Add logging to understand runtime behavior
6. **Test Systematically:** Verify each fix before moving to next

### Typical Bug Fix Process

```
1. Read log file to understand symptoms
   └─> BepInEx/LogOutput.log

2. Identify relevant code files
   └─> Use grep to find related code

3. Read the files thoroughly
   └─> Understand data flow and state

4. Identify root cause
   └─> Trace through logic

5. Design fix
   └─> Consider edge cases

6. Implement fix
   └─> Small, targeted changes

7. Build and verify
   └─> dotnet build

8. Document changes
   └─> Update this plan
```

### Decompilation Workflow

When investigating game internals:

```bash
# Decompile specific class
"D:\Tiny.Combat.Arena.v0.14.1.4\Dnspy\dnSpy.Console.exe" \
    -t "ClassName" \
    "D:\Tiny.Combat.Arena.v0.14.1.4\Arena_Data\Managed\Assembly-CSharp.dll"

# Common classes to investigate:
# - Falcon.Targeting.Target
# - Falcon.Targeting.TargetManagement
# - Falcon.Damage.Damageable
# - Falcon.Stores.Munition
# - Falcon.UniversalAircraft.UniAircraft
# - Falcon.Sensors.Radar
# - Falcon.Effects.EngineFX
```

---

## Testing Procedures

### Local Testing Setup

1. **Two Game Instances:** Run two copies of TCA
2. **Host:** Press F8 -> Start Host (port 7777)
3. **Client:** Press F8 -> Enter "127.0.0.1" -> Connect

### Test Checklist

#### Connection Tests
- [ ] Host starts successfully
- [ ] Client connects successfully
- [ ] Disconnect/reconnect works
- [ ] Connection persists through flight changes

#### Synchronization Tests
- [ ] Remote aircraft appears at correct position
- [ ] Movement is smooth (no jitter/teleporting)
- [ ] Rotation syncs correctly
- [ ] Gear animation syncs
- [ ] Flaps sync
- [ ] Afterburner effect syncs
- [ ] Control surfaces move

#### Combat Tests
- [ ] Guns hit remote aircraft
- [ ] Damage is applied to victim
- [ ] Victim's HP decreases
- [ ] Missiles can lock remote aircraft
- [ ] Missiles track and hit
- [ ] Missile warning appears for victim
- [ ] Radar lock notification works
- [ ] Aircraft destruction syncs
- [ ] No damage after destruction

#### Respawn Tests
- [ ] Destroyed aircraft hides correctly
- [ ] No premature re-cloning
- [ ] Respawn packet recreates aircraft
- [ ] 30-second timeout fallback works

### Log Analysis

Key log prefixes to monitor:
```
[NetworkManager]           - Core networking events
[RemoteAircraftController] - Clone state changes
[DamagePatches]            - Hit detection and damage
[WeaponPatches]            - Missile/radar events
[FlightGamePatches]        - State sending
```

---

## Build & Deployment

### Build Command

```bash
cd "D:\Tiny.Combat.Arena.v0.14.1.4\TCAMultiplayer"
dotnet build src/TCAMultiplayer/TCAMultiplayer.csproj --configuration Release
```

### Output Location

```
D:\Tiny.Combat.Arena.v0.14.1.4\BepInEx\plugins\TCAMultiplayer\TCAMultiplayer.dll
```

### Dependencies

The project references these DLLs from `libs/`:
- `0Harmony.dll` (Harmony patching)
- `BepInEx.dll` (Plugin framework)
- `Assembly-CSharp.dll` (Game code)
- `UnityEngine.*.dll` (Unity engine)

### Deployment Steps

1. Build the project
2. DLL automatically copies to BepInEx plugins folder
3. Start game - mod loads automatically
4. Press F8 to access multiplayer UI

---

## Troubleshooting Guide

### "Remote aircraft not appearing"

1. Check connection status in F8 menu
2. Verify both players are in flight
3. Check log for `CreateRemoteAircraft` messages
4. Verify `TryCloneRealAircraft` succeeds

### "Remote aircraft teleporting"

1. Check network latency
2. Verify interpolation buffer is filling
3. Look for `IsExtrapolating: true` in logs
4. May indicate packet loss

### "Can't lock remote aircraft"

1. Verify Target component is enabled
2. Check Faction is set to "Enemy"
3. Verify RCSCurve has points
4. Check TargetManagement.AllTargets registration

### "Damage not applying"

1. Check Damageable component is enabled
2. Verify damage packet is being sent
3. Check victim is receiving packet
4. Verify local aircraft is found

### "Multiple destroy messages"

1. Should be fixed - verify `IsDestroyed` check works
2. Check logs for guard message
3. Verify colliders are disabled

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1.0 | Jan 2025 | Initial networking, position sync |
| 0.2.0 | Jan 2025 | Aircraft cloning, visual state sync |
| 0.3.0 | Jan 2025 | Combat systems, damage sync |
| 0.3.1 | Jan 23, 2025 | Bug fixes: destroy handling, seeker detection |

---

## Contact & Resources

- **Game:** [Tiny Combat Arena on Steam](https://store.steampowered.com/app/1347550/Tiny_Combat_Arena/)
- **BepInEx:** [GitHub](https://github.com/BepInEx/BepInEx)
- **Harmony:** [GitHub](https://github.com/pardeike/Harmony)

---

*Last Updated: January 23, 2025*
