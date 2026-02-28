# TCA Multiplayer — Full Clean-Slate Rewrite

## TL;DR

> **Quick Summary**: Complete rewrite of the TCA Multiplayer mod, replacing ~700 unnecessary reflection calls with direct game API access, restructuring god classes into focused modules, adding Steam P2P networking alongside Direct UDP, and architecting for N-player from day one.
> 
> **Deliverables**:
> - New `TCAMultiplayer.dll` with clean architecture
> - Unit tests for serialization, interpolation, state machine
> - Steam P2P + Direct UDP dual transport
> - N-player capable lobby and game flow
> - Zero static mutable state (session-scoped design)
> 
> **Estimated Effort**: XL (full rewrite of ~23k lines → ~12-15k clean lines)
> **Parallel Execution**: YES — 8 waves
> **Critical Path**: T1 (verify APIs) → T3 (transport) → T7 (UDP impl) → T18 (state reader) → T24 (combat) → T30 (game flow) → T36 (plugin) → F1-F4

---

## Context

### Original Request
Full clean-slate rewrite of the TCA Multiplayer mod. The current 44-file, 23k-line codebase suffers from: a 3,292-line god class (RealCombatSync.cs), 859 unnecessary reflection calls, 100+ static mutable fields leaking between sessions, thread safety violations, and no tests. The game's API is ~80% public — most reflection is unnecessary. Game events exist that can replace polling patches.

### Interview Summary
**Key Discussions**:
- Transport: Steam P2P (Facepunch.Steamworks) + Direct UDP fallback
- Player count: N-player from day one (architecture), tested with 2 initially
- Scope: Full clean-slate rewrite, preserving proven concepts
- Testing: Unit tests for serialization, interpolation, state machine
- Architecture: Direct type access, event-driven where possible, no static mutable state

**Research Findings**:
- GameDataAircraft.SpawnAircraft() is public static — native aircraft creation
- Target auto-registers via OnEnable → TargetManagement.RegisterTarget()
- FloatingOrigin.TotalOffset is public static Vector3 (NOT Vector3d — precision implication)
- FloatingOrigin.OnOriginShiftFinished is public static Action<Vector3> (delegate field, not C# event — defensive subscription needed)
- Explosion.OnExplosion is public static event (NEEDS VERIFICATION against DLL)
- DamageSource is a public struct with public constructor (NEEDS VERIFICATION)
- Bullet2Manager.Instance.FireBullet() takes (Target, BulletData, string, Vector3, Vector3, IEnumerable<Collider>, IEnumerable<Rigidbody>) — VERIFIED from decompiled code

### Metis Review
**Identified Gaps** (addressed):
- Transport interface must be redesigned for N-player (Send needs peerId parameter) → Addressed in T3
- Authority model for N>2 damage → Per-shooter authority (each shooter is authoritative for their own shots, host arbitrates kill credit ties)
- FloatingOrigin is Vector3 not Vector3d → Track own double-precision cumulative offset via event subscription
- 5 unverified APIs → Verification task T1 before any implementation
- Session state lifecycle → Single SessionState class, IDisposable pattern, no statics
- Harmony patch inventory → Documented below
- Scope creep risks → Locked down in Guardrails section

---

## Work Objectives

### Core Objective
Replace the entire TCA Multiplayer mod codebase with a clean, well-architected implementation that eliminates unnecessary reflection, supports N-player, uses Steam P2P networking, and is structured for maintainability.

### Concrete Deliverables
- `src/` directory with new clean architecture (est. 30-40 focused source files)
- Unit test project for serialization, interpolation, state machine
- Working 2-player dogfight (spawn → fly → lock → fire → kill → respawn → lobby)
- Steam P2P lobby/connection with Direct UDP fallback
- Build pipeline producing TCAMultiplayer.dll

### Definition of Done
- [ ] `dotnet build TCAMultiplayer.sln -c Release` → 0 errors, 0 warnings
- [ ] `dotnet test` → All unit tests pass
- [ ] Two game instances can: host lobby → join → ready → start → spawn → fly → fire guns → launch missile → kill → respawn → return to lobby → start new game
- [ ] Zero static mutable fields outside of session-scoped instances
- [ ] No `Type.GetType("Falcon.` strings in codebase (all direct type access)

### Must Have
- Direct type access to public game APIs (no unnecessary reflection)
- Session-scoped state management (single SessionState/GameSession object)
- Thread-safe transport layer (ConcurrentQueue for cross-thread messaging)
- Steam P2P transport with relay fallback
- Direct UDP transport for LAN/debug
- N-player capable transport, lobby, and state management
- Harmony patches ONLY where game events don't exist
- InterpolationBuffer with proper edge case handling
- Packet validation (bounds checking, null checks on deserialization)
- Unit tests for serialization roundtrips, interpolation edge cases, state machine transitions

### Must NOT Have (Guardrails)
- NO static mutable fields — all state in session-scoped instances
- NO unnecessary reflection — if a type is public, import it directly
- NO god classes — max ~400 lines per file, single responsibility
- NO polling where events exist — use Explosion.OnExplosion, OnOriginShiftFinished, etc.
- NO UI rewrite — port existing Canvas+TMP UI as-is (fix only if broken)
- NO reconnection/rejoin — out of scope for v1
- NO voice chat — out of scope
- NO host migration — session dies if host disconnects (stated explicitly)
- NO late join — lobby requires all players before game starts
- NO N-player TESTING beyond 2 — build architecture, test with 2
- NO `catch (Exception) { }` — every catch must log AND handle or propagate
- NO `using` old mod namespaces — clean break from old code
- NO ModManifestCollector rewrite — port existing implementation as-is

---

## Verification Strategy

> **ZERO HUMAN INTERVENTION** — ALL verification is agent-executed.

### Test Decision
- **Infrastructure exists**: NO (creating new)
- **Automated tests**: YES (tests-after for core logic)
- **Framework**: `dotnet test` with xUnit or NUnit (compatible with net472)
- **Scope**: Serialization roundtrips, interpolation math, state machine transitions

### QA Policy
Every task includes agent-executed QA scenarios.
Evidence saved to `.sisyphus/evidence/task-{N}-{scenario-slug}.{ext}`.

- **Build verification**: `dotnet build TCAMultiplayer.sln -c Release`
- **Unit tests**: `dotnet test`
- **Integration**: Launch game instances, verify behavior via Playwright/tmux

---

## Harmony Patch Inventory

| Current Patch File | Patches | Decision | Replacement |
|---|---|---|---|
| `FlightGamePatches.cs` | FlightGame.Awake/Start/Update | **KEEP (simplified)** | Still need Update hook for sending state. But reading uses direct API, not reflection. |
| `DamagePatches.cs` | Damageable.ApplyDamageFromImpact/Explosion | **KEEP** | Need to intercept damage on remote clones. Direct API for applying damage. |
| `WeaponPatches.cs` | Munition polling | **REPLACE** | Use Munition.LaunchedMissiles direct access + frame polling (no Harmony needed for detection). Keep MissileLaunch send logic. |
| `ExplosionPatches.cs` | Munition.Explode postfix, explosion trigger | **REPLACE WITH EVENT** | Subscribe to Explosion.OnExplosion (if verified) or keep minimal Harmony patch. |
| `MainMenuPatches.cs` | MainMenu button injection | **KEEP** | Only way to inject Multiplayer button into game menu. |
| `SortieEndPatches.cs` | PauseMenu/RearmRefuel button hooks | **KEEP** | Need to intercept sortie-end to return to MP lobby. |
| `WorldDestructionPatches.cs` | WorldCraters2, Building destruction | **KEEP (simplified)** | Need destruction sync. Direct API for crater spawning. |
| `AircraftDestructionPatches.cs` | UniAircraft.DestroyAircraft postfix | **REPLACE WITH EVENT** | Subscribe to UniAircraft.OnAircraftDestroyed event. |
| `EnvironmentPatches.cs` | Environment patches for determinism | **KEEP** | Need deterministic cloud/wind. |
| `UniPilotPatches.cs` | FireControl.Update prefix | **KEEP (simplified)** | Need to control remote gun firing. |

### Reflection Whitelist (what STAYS as reflection)
- `HasBeenDestroyed` setter (private set) — need to detect destruction state on clones
- `FlightInput` internal fields (pitch/roll/yaw stick positions) — no public API
- `UniPilot.FlightInput` property chain for reading control inputs
- Engine afterburner/throttle internal state
- `Seeker` backing fields for `IsTracking`/`IsSpoofed` configuration on spawned missiles
- `StickAndRudder` fields for precise control surface reading
- Estimated: ~50-80 reflection calls total (down from 859)

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Foundation — scaffolding, types, verification):
├── T1: API verification against game DLL [deep]
├── T2: Project scaffolding & namespace structure [quick]
├── T3: Transport abstraction interface (N-player) [deep]
├── T4: Packet protocol types & serialization [unspecified-high]
├── T5: Session state container & game state machine [unspecified-high]
├── T6: Unit test infrastructure setup [quick]
└── T7: Logging & config system [quick]

Wave 2 (Transport — networking core):
├── T8: Direct UDP transport (N-player, thread-safe) (depends: T3) [deep]
├── T9: Steam P2P transport (depends: T3) [deep]
├── T10: Reliability layer & packet router (depends: T3, T4) [unspecified-high]
├── T11: Connection/handshake/keepalive protocol (depends: T8, T5) [unspecified-high]
└── T12: Serialization unit tests (depends: T4, T6) [quick]

Wave 3 (Game Integration — hooking into TCA):
├── T13: Floating origin service (depends: T1) [quick]
├── T14: Aircraft spawner & clone configurer (depends: T1, T5) [deep]
├── T15: Game event hooks (depends: T1) [unspecified-high]
├── T16: Harmony patches — only needed ones (depends: T1) [unspecified-high]
└── T17: State machine unit tests (depends: T5, T6) [quick]

Wave 4 (Aircraft Sync — state replication):
├── T18: Local aircraft state reader (depends: T13, T16) [deep]
├── T19: Remote aircraft controller (depends: T14) [deep]
├── T20: Interpolation buffer (depends: T13) [deep]
├── T21: Remote aircraft manager — N-player (depends: T19, T20, T10) [deep]
└── T22: Interpolation unit tests (depends: T20, T6) [quick]

Wave 5 (Combat Sync — weapons & damage):
├── T23: Radar/RWR sync (depends: T1, T21) [deep]
├── T24: Missile sync — launch, tracking, position (depends: T1, T21) [deep]
├── T25: Gun/bullet sync (depends: T1, T21) [deep]
├── T26: Damage sync (depends: T1, T21) [deep]
├── T27: Explosion & world destruction sync (depends: T15, T21) [unspecified-high]
└── T28: Aircraft collision manager (depends: T21) [unspecified-high]

Wave 6 (Game Flow — lobby, spawn, respawn):
├── T29: Lobby manager — N-player (depends: T11, T5) [deep]
├── T30: Spawn manager (depends: T14, T29) [unspecified-high]
├── T31: Respawn flow (depends: T30, T5) [unspecified-high]
├── T32: Score tracking (depends: T26) [quick]
└── T33: Mod compatibility — port existing (depends: T29) [quick]

Wave 7 (Integration — plugin, UI, build):
├── T34: Plugin entry point — clean bootstrap (depends: T29, T21, all combat) [deep]
├── T35: Multiplayer menu — port existing (depends: T29) [visual-engineering]
├── T36: Scoreboard/HUD — port existing (depends: T32) [visual-engineering]
├── T37: Respawn screen — port existing (depends: T31) [visual-engineering]
├── T38: Build & deploy pipeline (depends: T34) [quick]
└── T39: End-to-end integration test (depends: ALL) [deep]

Wave FINAL (Verification — 4 parallel):
├── F1: Plan compliance audit (oracle)
├── F2: Code quality review (unspecified-high)
├── F3: Real QA — full dogfight scenario (unspecified-high)
└── F4: Scope fidelity check (deep)

Critical Path: T1 → T3 → T8 → T11 → T18 → T21 → T26 → T29 → T34 → T39 → F1-F4
Parallel Speedup: ~65% faster than sequential
Max Concurrent: 7 (Wave 1)
```

### Dependency Matrix

| Task | Depends On | Blocks |
|------|-----------|--------|
| T1 | — | T13-T16, T18, T23-T28 |
| T2 | — | All implementation tasks |
| T3 | — | T8-T11 |
| T4 | — | T10, T12 |
| T5 | — | T11, T14, T17, T29 |
| T6 | — | T12, T17, T22 |
| T7 | — | T34 |
| T8 | T3 | T11 |
| T9 | T3 | T34 |
| T10 | T3, T4 | T21 |
| T11 | T8, T5 | T29 |
| T12 | T4, T6 | — |
| T13 | T1 | T18, T20 |
| T14 | T1, T5 | T19, T30 |
| T15 | T1 | T27 |
| T16 | T1 | T18 |
| T17 | T5, T6 | — |
| T18 | T13, T16 | T21 |
| T19 | T14 | T21 |
| T20 | T13 | T21, T22 |
| T21 | T19, T20, T10 | T23-T28 |
| T22 | T20, T6 | — |
| T23-T28 | T1, T21 | T34 |
| T29 | T11, T5 | T30, T33, T35 |
| T30 | T14, T29 | T31 |
| T31 | T30, T5 | T37 |
| T32 | T26 | T36 |
| T33 | T29 | T34 |
| T34 | T29, T21, combat tasks | T38, T39 |
| T35-T37 | T29, T31, T32 | T39 |
| T38 | T34 | T39 |
| T39 | ALL | F1-F4 |

### Agent Dispatch Summary

- **Wave 1**: 7 tasks — T1 `deep`, T2 `quick`, T3 `deep`, T4 `unspecified-high`, T5 `unspecified-high`, T6 `quick`, T7 `quick`
- **Wave 2**: 5 tasks — T8 `deep`, T9 `deep`, T10 `unspecified-high`, T11 `unspecified-high`, T12 `quick`
- **Wave 3**: 5 tasks — T13 `quick`, T14 `deep`, T15 `unspecified-high`, T16 `unspecified-high`, T17 `quick`
- **Wave 4**: 5 tasks — T18 `deep`, T19 `deep`, T20 `deep`, T21 `deep`, T22 `quick`
- **Wave 5**: 6 tasks — T23-T28 mix of `deep` and `unspecified-high`
- **Wave 6**: 5 tasks — T29 `deep`, T30-T33 mix
- **Wave 7**: 6 tasks — T34 `deep`, T35-T37 `visual-engineering`, T38 `quick`, T39 `deep`
- **FINAL**: 4 tasks — F1 `oracle`, F2 `unspecified-high`, F3 `unspecified-high`, F4 `deep`

---

## TODOs

### Wave 1: Foundation

- [ ] 1. **API Verification Against Game DLL**

  **What to do**:
  - For each game type the mod will access directly (GameDataAircraft, Target, TargetManagement, Radar, ThreatWarning, Munition, Seeker, Damageable, DamageSource, Explosion, Bullet2Manager, Gun2, FloatingOrigin, UniAircraft, UniPilot, GameDataStores, GameDataBullets, GameDataGuns, GameDataLoadouts, GunData, BulletData, AmmoBelt), verify accessibility by writing a small test class that imports and compiles against them
  - Create `src/Verification/ApiVerification.cs` that does `using Falcon.Targeting; using Falcon.Weapons; using Falcon.Damage; using Falcon.World;` etc. and accesses each confirmed-public member
  - Build and confirm 0 errors. For any that fail, document in a `REFLECTION_REQUIRED.md` with exact reason
  - Specifically verify: `Explosion.OnExplosion` event, `DamageSource` constructor, `Bullet2Manager.FireBullet()` signature, `UniAircraft.OnAircraftDestroyed` event
  - Verify `FloatingOrigin.OnOriginShiftFinished` is a delegate field (not event) and test `+=` subscription

  **Must NOT do**:
  - Do not implement any game logic — this is verification only
  - Do not modify game DLLs

  **Recommended Agent Profile**:
  - **Category**: `deep`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1
  - **Blocks**: T13-T16, T18, T23-T28
  - **Blocked By**: None

  **References**:
  - `Decompiled/Assembly-CSharp/Falcon/GameDataAircraft.cs` — SpawnAircraft() signature
  - `Decompiled/Assembly-CSharp/Falcon/Targeting/Target.cs` — Public fields and properties
  - `Decompiled/Assembly-CSharp/Falcon/Damage/DamageSource.cs` — Constructor signature
  - `Decompiled/Assembly-CSharp/Falcon/Damage/Explosion.cs` — OnExplosion event, Trigger() method
  - `Decompiled/Assembly-CSharp/Falcon/Weapons/Bullet2Manager.cs` — FireBullet() signature
  - `Decompiled/Assembly-CSharp/Falcon/World/FloatingOrigin.cs` — TotalOffset, events, Blacklist
  - `Decompiled/Assembly-CSharp/Falcon/UniversalAircraft/UniAircraft.cs` — Public properties
  - `libs/Assembly-CSharp.dll` — Actual game DLL to compile against

  **Acceptance Criteria**:
  - [ ] `dotnet build` succeeds with ApiVerification.cs included
  - [ ] REFLECTION_REQUIRED.md lists every API that requires reflection with justification
  - [ ] All Metis-flagged unverified APIs have confirmed status (public/private/internal)

  **QA Scenarios**:
  ```
  Scenario: All game APIs compile with direct access
    Tool: Bash
    Steps:
      1. Run `dotnet build TCAMultiplayer.sln -c Release`
      2. Check for 0 errors in output
    Expected Result: Build succeeded with 0 errors
    Evidence: .sisyphus/evidence/task-1-api-verification-build.txt

  Scenario: Reflection whitelist is exhaustive
    Tool: Bash
    Steps:
      1. Run `grep -rn "GetType\|GetField\|GetMethod\|GetProperty\|Invoke\|GetValue\|SetValue" src/ --include="*.cs"`
      2. Compare each match against REFLECTION_REQUIRED.md
    Expected Result: Every reflection call in code has a corresponding entry in whitelist
    Evidence: .sisyphus/evidence/task-1-reflection-audit.txt
  ```

  **Commit**: YES — `chore(verify): verify game API accessibility and document reflection whitelist`

- [ ] 2. **Project Scaffolding & Namespace Structure**

  **What to do**:
  - Create new directory structure under `src/`:
    - `src/Core/` — Session state, game state machine, config, logging
    - `src/Transport/` — ITransport interface, DirectUdp, SteamP2P
    - `src/Protocol/` — Packet types, serialization, router
    - `src/Game/` — Aircraft spawner, spawn manager, score tracker, airfield helper
    - `src/Sync/` — Aircraft state reader/writer, remote aircraft manager, interpolation
    - `src/Combat/` — Radar sync, missile sync, gun sync, damage sync, explosion sync
    - `src/Patches/` — Only Harmony patches that are actually needed
    - `src/UI/` — Menu, HUD, respawn screen (ported as-is)
    - `src/Compatibility/` — Mod manifest collector (ported as-is)
  - Move/archive old source to a `src_old/` directory or git branch
  - Update TCAMultiplayer.csproj with new file structure
  - Ensure `dotnet build` still works (empty files with correct namespaces)

  **Must NOT do**:
  - Do not implement any logic — just create file skeletons with namespaces
  - Do not delete old code (archive it)

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: [`git-master`]

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1
  - **Blocks**: All implementation tasks
  - **Blocked By**: None

  **Acceptance Criteria**:
  - [ ] `dotnet build` succeeds with new project structure
  - [ ] All namespace directories created with placeholder files
  - [ ] Old source archived (branch or directory)

  **QA Scenarios**:
  ```
  Scenario: New project structure compiles
    Tool: Bash
    Steps:
      1. Run `dotnet build TCAMultiplayer.sln -c Release`
    Expected Result: Build succeeded, 0 errors
    Evidence: .sisyphus/evidence/task-2-scaffold-build.txt
  ```

  **Commit**: YES — `refactor(scaffold): create new project structure for multiplayer rewrite`

- [ ] 3. **Transport Abstraction Interface (N-Player)**

  **What to do**:
  - Design and implement `ITransport` interface supporting N-player:
    ```csharp
    public interface ITransport : IDisposable
    {
        event Action<ulong, byte[]> OnDataReceived;  // peerId, data
        event Action<ulong> OnPeerConnected;
        event Action<ulong> OnPeerDisconnected;
        
        bool IsHost { get; }
        ulong LocalPeerId { get; }
        IReadOnlyCollection<ulong> ConnectedPeers { get; }
        
        void StartHost(int port);
        void Connect(string address, int port);
        void Disconnect();
        
        void Send(ulong peerId, byte[] data, bool reliable);
        void Broadcast(byte[] data, bool reliable, ulong? except = null);
        
        void Update();  // Called from main thread to drain receive queue
    }
    ```
  - Define `TransportConfig` for settings (port, timeout, keepalive interval)
  - Document threading model: receive thread → ConcurrentQueue → main thread drains in Update()

  **Must NOT do**:
  - Do not implement concrete transports yet (just the interface)
  - Do not add game-specific logic

  **Recommended Agent Profile**:
  - **Category**: `deep`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1
  - **Blocks**: T8, T9, T10, T11
  - **Blocked By**: None

  **References**:
  - `src/Networking/INetworkTransport.cs` — Current interface (2-player only, shows what's needed)
  - `src/Networking/DirectTransport.cs` — Current implementation (shows thread safety issues to avoid)
  - `src/Networking/NetworkConfig.cs` — Current config (port, intervals)

  **Acceptance Criteria**:
  - [ ] ITransport.cs compiles with N-player API surface
  - [ ] Threading model documented in XML comments
  - [ ] No static mutable state in interface or config

  **QA Scenarios**:
  ```
  Scenario: Interface compiles and has correct API surface
    Tool: Bash
    Steps:
      1. `dotnet build`
      2. Verify ITransport has Send(ulong, byte[], bool) and Broadcast methods
    Expected Result: Build succeeds, interface has N-player methods
    Evidence: .sisyphus/evidence/task-3-transport-interface.txt
  ```

  **Commit**: YES (groups with T2) — `feat(transport): define N-player transport abstraction interface`

- [ ] 4. **Packet Protocol Types & Serialization**

  **What to do**:
  - Reimplement packet types from current PacketTypes.cs but with:
    - Input validation on all deserialize methods (bounds checking, null checks)
    - Remove [Obsolete] packet types (PlayerJoin=1, PlayerLeave=2, PlayerReady=3, GameStart=4)
    - Proper BinaryReader/Writer with length-prefixed strings
    - Static Serialize/Deserialize methods per packet type
  - Keep the proven packet layout (same fields, same byte order)
  - Add `PacketSerializer` with type-safe generic methods
  - Add `PacketValidator` that checks packet integrity before deserialization

  **Must NOT do**:
  - Do not change the packet protocol semantics (keep what works)
  - Do not add new packet types yet

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1
  - **Blocks**: T10, T12
  - **Blocked By**: None

  **References**:
  - `src/Networking/PacketTypes.cs:1-1681` — Current packet definitions (keep concepts, fix implementation)

  **Acceptance Criteria**:
  - [ ] All non-obsolete packet types reimplemented with validation
  - [ ] Every Deserialize method validates buffer length before reading
  - [ ] Every string deserialization is length-prefixed and null-safe
  - [ ] `dotnet build` succeeds

  **QA Scenarios**:
  ```
  Scenario: Malformed packet doesn't crash
    Tool: Bash (unit test)
    Steps:
      1. Write unit test that passes truncated byte[] to each Deserialize method
      2. Verify no unhandled exception — returns error or default
    Expected Result: All deserialize methods handle truncated input gracefully
    Evidence: .sisyphus/evidence/task-4-malformed-packets.txt
  ```

  **Commit**: YES — `feat(protocol): reimplement packet types with input validation`

- [ ] 5. **Session State Container & Game State Machine**

  **What to do**:
  - Create `GameSession` class — THE single source of all mutable session state:
    - IsHost, LocalPeerId, ConnectedPeers
    - CurrentState (enum: Disconnected, Lobby, Loading, Spawning, InGame, Respawning)
    - Player roster (Dictionary<ulong, PlayerInfo>)
    - Lobby settings (map, spawn type, time of day, collisions)
    - Score data
  - Implement as IDisposable — Dispose() is the ONLY cleanup path
  - Implement state machine with explicit valid transitions and transition events
  - NO static fields — GameSession is created per-session and disposed on disconnect

  **Must NOT do**:
  - No static mutable state
  - No direct dependency on Plugin class

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1
  - **Blocks**: T11, T14, T17, T29
  - **Blocked By**: None

  **References**:
  - `src/Networking/GameStateMachine.cs` — Current state machine (good concepts, needs cleanup)
  - `src/Player/RemotePlayer.cs` — Current player data container
  - `src/Networking/LobbyManager.cs` — Current lobby state (scattered, needs consolidation)

  **Acceptance Criteria**:
  - [ ] GameSession class has zero static mutable fields
  - [ ] State machine validates transitions (invalid transitions throw or return false)
  - [ ] Dispose() cleans up ALL session state
  - [ ] `dotnet build` succeeds

  **QA Scenarios**:
  ```
  Scenario: Invalid state transitions are rejected
    Tool: Bash (unit test)
    Steps:
      1. Create GameSession
      2. Try transition Disconnected → InGame (invalid)
      3. Verify returns false / throws
    Expected Result: Invalid transition rejected
    Evidence: .sisyphus/evidence/task-5-state-machine.txt
  ```

  **Commit**: YES — `feat(core): implement session state container and game state machine`

- [ ] 6. **Unit Test Infrastructure Setup**

  **What to do**:
  - Create test project (TCAMultiplayer.Tests.csproj) using xUnit or NUnit
  - Configure for net472 to match main project
  - Add reference to main project
  - Write one smoke test that verifies test infrastructure works
  - Set up `dotnet test` command in build pipeline

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1
  - **Blocks**: T12, T17, T22
  - **Blocked By**: None

  **Acceptance Criteria**:
  - [ ] `dotnet test` runs and passes smoke test
  - [ ] Test project references main project

  **QA Scenarios**:
  ```
  Scenario: Test infrastructure works
    Tool: Bash
    Steps: 1. `dotnet test` 2. Check output for "1 passed"
    Expected Result: 1 test passed
    Evidence: .sisyphus/evidence/task-6-test-infra.txt
  ```

  **Commit**: YES (groups with T2) — `chore(test): set up unit test infrastructure`

- [ ] 7. **Logging & Config System**

  **What to do**:
  - Port logging from current `LogHelper.cs` and `InstanceLogger.cs`
  - Create `ModConfig` class using BepInEx ConfigEntry (port from current Plugin.cs BindLoggingConfig)
  - Ensure logging does NOT depend on Plugin.Instance (accept ILogger interface)
  - Host/client log file separation

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1
  - **Blocks**: T34
  - **Blocked By**: None

  **References**:
  - `src/Logging/LogHelper.cs` — Current logging (port concepts)
  - `src/Logging/InstanceLogger.cs` — Current file logging
  - `src/Plugin.cs:640-669` — Current config binding

  **Acceptance Criteria**:
  - [ ] Logging works without Plugin.Instance dependency
  - [ ] Config entries for all current settings

  **Commit**: YES (groups with T2)

---

## Final Verification Wave

- [ ] F1. **Plan Compliance Audit** — `oracle`
  Read the plan end-to-end. For each "Must Have": verify implementation exists. For each "Must NOT Have": search codebase for forbidden patterns. Check evidence files exist. Compare deliverables against plan.
  Output: `Must Have [N/N] | Must NOT Have [N/N] | Tasks [N/N] | VERDICT: APPROVE/REJECT`

- [ ] F2. **Code Quality Review** — `unspecified-high`
  Run `dotnet build -c Release` + `dotnet test`. Review all files for: `Type.GetType("Falcon.` (forbidden), `static.*=` without session scope (forbidden), `catch { }` empty catches (forbidden), god classes >400 lines, unused imports. Check reflection whitelist compliance.
  Output: `Build [PASS/FAIL] | Tests [N pass/N fail] | Reflection [N direct/N whitelisted/N forbidden] | VERDICT`

- [ ] F3. **Real Manual QA** — `unspecified-high` (+ `playwright` skill)
  Start from clean state. Full dogfight scenario: Host lobby → Client join → Both ready → Start → Spawn → Fly → Lock radar → Fire missile → Kill → Respawn → Return to lobby → Start new game → Verify zero state leaks. Save evidence to `.sisyphus/evidence/final-qa/`.
  Output: `Scenarios [N/N pass] | Integration [N/N] | State Leaks [CLEAN/N issues] | VERDICT`

- [ ] F4. **Scope Fidelity Check** — `deep`
  For each task: read "What to do", read actual diff. Verify 1:1 match. Check "Must NOT do" compliance (no UI rewrite, no reconnection, no voice chat, no static mutable fields). Flag unaccounted changes.
  Output: `Tasks [N/N compliant] | Guardrails [N/N respected] | VERDICT`

---

## Commit Strategy

- **Wave 1**: `feat(core): scaffold project structure with transport abstraction and packet protocol`
- **Wave 2**: `feat(transport): implement Direct UDP and Steam P2P transports with reliability layer`
- **Wave 3**: `feat(game): integrate with TCA game APIs via direct access and event hooks`
- **Wave 4**: `feat(sync): implement aircraft state sync with interpolation for N-player`
- **Wave 5**: `feat(combat): implement combat sync — radar, missiles, guns, damage, explosions`
- **Wave 6**: `feat(flow): implement lobby, spawn, respawn, scoring game flow`
- **Wave 7**: `feat(plugin): integrate all systems, port UI, build pipeline`
- **Final**: `chore(verify): verification evidence and final QA`

---

## Success Criteria

### Verification Commands
```bash
dotnet build TCAMultiplayer.sln -c Release  # Expected: Build succeeded, 0 errors, 0 warnings
dotnet test  # Expected: All tests pass
grep -r "Type.GetType(\"Falcon\." src/ --include="*.cs"  # Expected: 0 matches
grep -r "static.*= new\|static.*= null\|static.*= 0\|static.*= false" src/ --include="*.cs" | grep -v "const\|readonly"  # Expected: 0 matches (no mutable statics)
```

### Final Checklist
- [ ] All "Must Have" present
- [ ] All "Must NOT Have" absent
- [ ] All unit tests pass
- [ ] Full dogfight scenario completes without errors
- [ ] Return to lobby + new game: zero state leaks
- [ ] Reflection usage ≤ 80 calls total (down from 859)
- [ ] No file exceeds 400 lines
- [ ] Every catch block logs AND handles/propagates
