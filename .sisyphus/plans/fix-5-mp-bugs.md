# Fix 5 Critical Multiplayer Bugs

## TL;DR

> **Quick Summary**: Fix 5 bugs in the TCA Multiplayer mod: lobby showing only 1 player, duplicate nuclear explosions client-side, shorter gun range on remote screen, config not saving, and remote aircraft jitter/shaking.
> 
> **Deliverables**:
> - Fixed lobby player list synchronization (immediate broadcast on join)
> - Fixed duplicate explosion sync for nuclear weapons
> - Fixed bullet velocity inheritance on remote aircraft guns
> - Fixed config persistence with explicit save calls
> - Reduced remote aircraft jitter via velocity smoothing and rigidbody protection
> 
> **Estimated Effort**: Medium
> **Parallel Execution**: YES - 3 waves
> **Critical Path**: Bug #1 (lobby) → Bug #2 (explosions) → Bug #5 (jitter) — each independent but ordered by confidence/risk

---

## Context

### Original Request
User reported 5 bugs in the TCA Multiplayer mod for Tiny Combat Arena v0.14.1.4:
1. Lobby shows only 1 player (the joiner)
2. Multiple nuclear-like explosion impacts spawning client-side when plane on ground
3. Gun bullet range shorter on remote player's screen
4. Lobby config doesn't save correctly
5. Remote player's plane is super jittery/shaking

### Research Findings
- **Full codebase read**: All 50+ source files across src/Networking, src/Player, src/Patches, src/UI, src/Game
- **Decompiled game code**: Assembly-CSharp types (Falcon.* namespace) analyzed for Gun2, FireControl, UniAircraft, Explosion, Munition
- **5 parallel explore agents** confirmed root causes for each bug
- **Config file** at `BepInEx/config/com.modder.tcamultiplayer.cfg` read — shows partial persistence works

### Metis Review
**Identified Gaps** (addressed):
- Bug #2 execution order corrected: crater postfix sends FIRST (during Explode()), MunitionExplodePostfix sends SECOND (after)
- Bug #3 rb.velocity is a STEERING velocity, not actual aircraft velocity — bullets inherit wrong initial velocity
- Bug #4 may be partially phantom — BepInEx auto-saves on Value set, config file shows some values persist
- Bug #5 needs incremental approach — fix one root cause at a time, validate before adding more
- Added guardrails: no local aircraft regressions, no high-frequency Config.Save(), thread-safe dedup, O(1) FixedUpdate budget

---

## Work Objectives

### Core Objective
Fix all 5 reported multiplayer bugs to achieve stable 2-player PvP gameplay with correct lobby display, no duplicate VFX, proper gun ballistics, persistent config, and smooth remote aircraft rendering.

### Concrete Deliverables
- `src/Networking/LobbyManager.cs` — Immediate broadcast after player join
- `src/Patches/ExplosionPatches.cs` — Nuclear explosion dedup fix
- `src/Patches/UniPilotPatches.cs` — Bullet velocity correction for remote aircraft
- `src/Networking/RemoteAircraftManager.cs` — Velocity smoothing for jitter reduction
- `src/Plugin.cs` and `src/Networking/LobbyManager.cs` — Explicit Config.Save() calls
- `src/UI/MultiplayerMenu.cs` — Config save on UI changes

### Definition of Done
- [ ] Host and client both see all lobby players within 200ms of join
- [ ] Nuclear explosions spawn exactly 1 VFX on client per impact
- [ ] Remote aircraft bullets travel ≥90% of local bullet range
- [ ] All lobby settings persist across game restart
- [ ] Remote aircraft jitter reduced to imperceptible at dogfight range (<2km)

### Must Have
- All 5 bugs fixed with minimal code changes
- No regressions to local aircraft behavior (flying, shooting, damage)
- No regressions to modded weapon sync (Tiny Weapon Shop nukes)
- Victim-side damage authority model preserved

### Must NOT Have (Guardrails)
- MUST NOT modify any codepath executing for local aircraft rigidbodies
- MUST NOT call Config.Save() in high-frequency paths (FixedUpdate, packet handlers)
- MUST NOT add per-frame allocations or O(n) searches in FixedUpdate path
- MUST NOT bypass the lobby state hash-based dirty check
- MUST NOT break modded weapon VFX sync (ExplosionTriggerPostfix must remain as fallback)
- MUST NOT redesign the gun firing pipeline or interpolation architecture
- MUST NOT add new config entries or change config schema
- MUST NOT use hardcoded speed/velocity limits — all bounds MUST be data-driven from the game's own native values (packet velocity, aircraft data)
- MUST NOT edit or override the game's native physics values, aircraft parameters, or weapon data

---

## Verification Strategy

> **ZERO HUMAN INTERVENTION** — ALL verification is agent-executed. No exceptions.

### Test Decision
- **Infrastructure exists**: NO (no test framework in project)
- **Automated tests**: None — BepInEx mod cannot be unit tested in isolation
- **Framework**: N/A

### QA Policy
Every task MUST include agent-executed QA scenarios using code review and log analysis.
Evidence saved to `.sisyphus/evidence/task-{N}-{scenario-slug}.{ext}`.

- **Code changes**: Use LSP diagnostics + grep to verify correctness
- **Logic verification**: Trace execution paths through code to verify fix addresses root cause
- **Build verification**: `dotnet build` must pass

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Start Immediately — independent, high-confidence fixes):
├── Task 1: Fix lobby player list — immediate broadcast on join [quick]
├── Task 2: Fix config persistence — add explicit Config.Save() calls [quick]
└── Task 3: Validate Bug #4 — read config file, verify what actually fails [quick]

Wave 2 (After Wave 1 — moderate complexity, clear fixes):
├── Task 4: Fix duplicate nuclear explosion sync [deep]
├── Task 5: Fix bullet velocity on remote aircraft guns [deep]
└── Task 6: Validate Gun2 velocity source via decompiled code [quick]

Wave 3 (After Wave 2 — highest complexity, incremental approach):
├── Task 7: Fix remote aircraft jitter — velocity smoothing [deep]
├── Task 8: Verify FloatingOrigin blacklist effectiveness [quick]
└── Task 9: Build and verify all changes compile [quick]

Wave FINAL (After ALL tasks — verification):
├── Task F1: Plan compliance audit [deep]
├── Task F2: Code quality review + build [unspecified-high]
├── Task F3: Regression check — trace local aircraft paths [deep]
└── Task F4: Scope fidelity check [deep]
```

### Dependency Matrix

| Task | Depends On | Blocks |
|------|-----------|--------|
| 1 | — | F1-F4 |
| 2 | — | F1-F4 |
| 3 | — | 2 (may modify approach) |
| 4 | — | F1-F4 |
| 5 | 6 (validation) | F1-F4 |
| 6 | — | 5 |
| 7 | — | F1-F4 |
| 8 | — | 7 (informs approach) |
| 9 | 1-8 | F1-F4 |
| F1-F4 | 9 | — |

### Agent Dispatch Summary

- **Wave 1**: 3 tasks — T1 → `quick`, T2 → `quick`, T3 → `quick`
- **Wave 2**: 3 tasks — T4 → `deep`, T5 → `deep`, T6 → `quick`
- **Wave 3**: 3 tasks — T7 → `deep`, T8 → `quick`, T9 → `quick`
- **FINAL**: 4 tasks — F1 → `deep`, F2 → `unspecified-high`, F3 → `deep`, F4 → `deep`

---

## TODOs

- [ ] 1. Fix Lobby Player List — Immediate Broadcast on Join

  **What to do**:
  - In `src/Networking/LobbyManager.cs`, method `HandlePlayerJoined()` (around line 271):
    - After `OnLobbyStateChanged?.Invoke()` (line 275), add a call to `BroadcastLobbyState()` to force an immediate state broadcast to all clients
    - This ensures the client receives the full player list within one network round-trip (~10-50ms) instead of waiting up to 1 second for the periodic broadcast
  - Additionally, in `HandlePlayerJoined()`, reset `_lastBroadcastHash = 0` before calling `BroadcastLobbyState()` to bypass the dirty-check that might skip sending if hash hasn't changed
  - On the client side in `NetworkManager.HandleLobbyWelcome()` (line 584): `JoinLobby()` clears `_players` and adds only self — this is correct behavior because `UpdateFromLobbyState()` will repopulate when the host's forced broadcast arrives

  **Must NOT do**:
  - MUST NOT add a second broadcast mechanism — use existing `BroadcastLobbyState()`
  - MUST NOT change the periodic broadcast logic (1s interval remains as fallback)
  - MUST NOT modify `JoinLobby()` to retain stale player data

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: Single-file change, 2-3 lines of code, clear fix with high confidence
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 2, 3)
  - **Blocks**: Tasks F1-F4
  - **Blocked By**: None

  **References**:

  **Pattern References**:
  - `src/Networking/LobbyManager.cs:249-276` — `HandlePlayerJoined()` method — add broadcast call after line 275
  - `src/Networking/LobbyManager.cs:914-923` — `BroadcastLobbyState()` method with hash-based dirty check — understand the hash check to properly bypass it
  - `src/Networking/LobbyManager.cs:928-956` — `ComputeLobbyStateHash()` — the hash check to understand
  - `src/Networking/LobbyManager.cs:73` — `_lastBroadcastHash` field to reset

  **API/Type References**:
  - `src/Networking/NetworkConfig.cs:27` — `LOBBY_BROADCAST_INTERVAL = 1.0f` — the current periodic interval this fix bypasses

  **WHY Each Reference Matters**:
  - `HandlePlayerJoined()` is the exact insertion point — add broadcast AFTER the player is added and events fired
  - `BroadcastLobbyState()` has a hash-based dirty check that might skip the send — must reset `_lastBroadcastHash` to force it
  - The constructor's hash field starts at 0, meaning any fresh state will always differ and be sent

  **Acceptance Criteria**:

  **QA Scenarios (MANDATORY):**

  ```
  Scenario: Player join triggers immediate broadcast
    Tool: Bash (grep + code trace)
    Preconditions: Code changes applied to LobbyManager.cs
    Steps:
      1. Read HandlePlayerJoined() method
      2. Verify BroadcastLobbyState() call exists after OnLobbyStateChanged?.Invoke()
      3. Verify _lastBroadcastHash is reset to 0 before the broadcast call
      4. Trace BroadcastLobbyState() to confirm it will send with hash=0
    Expected Result: HandlePlayerJoined contains `_lastBroadcastHash = 0; BroadcastLobbyState();` after line 275
    Failure Indicators: No BroadcastLobbyState() call, or call before player is added to _players
    Evidence: .sisyphus/evidence/task-1-lobby-broadcast.txt

  Scenario: Periodic broadcast still works as fallback
    Tool: Bash (grep)
    Preconditions: Code changes applied
    Steps:
      1. Verify Update() method still contains the periodic broadcast logic
      2. Verify LOBBY_BROADCAST_INTERVAL unchanged at 1.0f
    Expected Result: Periodic broadcast code unchanged
    Evidence: .sisyphus/evidence/task-1-periodic-unchanged.txt
  ```

  **Commit**: YES
  - Message: `fix(lobby): broadcast state immediately on player join`
  - Files: `src/Networking/LobbyManager.cs`

- [ ] 2. Fix Config Persistence — Add Explicit Config.Save() Calls

  **What to do**:
  - In `src/UI/MultiplayerMenu.cs`, add `Plugin.Instance.Config.Save()` after each config value assignment in UI handlers:
    - `DrawMainMenu()` username change handler (after line 272)
    - `DrawHostSetup()` server name change handler (after line 290)
    - `DrawHostSetup()` port change handler (after line 294)
    - `DrawDirectConnect()` IP change handler (after line 600)
    - `DrawDirectConnect()` port change handler (after line 604)
  - In `src/Networking/LobbyManager.cs`, add `Plugin.Instance?.Config?.Save()` after config value assignments in:
    - `SetLocalAirfield()` (after line 365)
    - `SetLocalAircraft()` (after line 395)
    - `SetLocalLoadout()` (after line 412)
    - `SetSpawnSettings()` (after line 457)
    - `SetTimeOfDay()` (after line 488)
    - `SetAircraftCollisionsEnabled()` (after line 477)
  - These are all UI-triggered (user action), NOT high-frequency paths — Config.Save() is safe here

  **Must NOT do**:
  - MUST NOT call Config.Save() in FixedUpdate, Update, packet handlers, or any per-frame code
  - MUST NOT add new config entries or change config schema
  - MUST NOT change config section names or key names

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: Repetitive single-line additions across 2 files, no logic changes
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 3)
  - **Blocks**: Tasks F1-F4
  - **Blocked By**: Task 3 (validation may change approach)

  **References**:

  **Pattern References**:
  - `src/UI/MultiplayerMenu.cs:269-273` — Username change handler — add Config.Save() after line 272
  - `src/UI/MultiplayerMenu.cs:288-295` — Host setup handlers — add Config.Save() after each assignment
  - `src/Networking/LobbyManager.cs:362-374` — `SetLocalAirfield()` — add Config.Save() after line 365
  - `TinyCombatArena[Host]/BepInEx/config/com.modder.tcamultiplayer.cfg` — Config file to verify format

  **API/Type References**:
  - `Plugin.Instance.Config` is a `BepInEx.Configuration.ConfigFile` — `Save()` flushes to disk

  **WHY Each Reference Matters**:
  - MultiplayerMenu handlers are the UI entry points where user changes settings
  - LobbyManager setters are called by UI handlers and also save individual ConfigEntry values
  - The config file shows the format and confirms some values DO persist (validating the approach)

  **Acceptance Criteria**:

  **QA Scenarios (MANDATORY):**

  ```
  Scenario: Config.Save() called in all UI handlers
    Tool: Bash (grep)
    Preconditions: Code changes applied
    Steps:
      1. grep for 'Config.Save()' in MultiplayerMenu.cs — expect 5 occurrences
      2. grep for 'Config?.Save()' in LobbyManager.cs — expect 6 occurrences
    Expected Result: 11 total Config.Save() calls across the two files
    Failure Indicators: Missing Save() calls, or Save() in Update/FixedUpdate paths
    Evidence: .sisyphus/evidence/task-2-config-save-grep.txt

  Scenario: Config.Save() NOT in high-frequency paths
    Tool: Bash (grep)
    Preconditions: Code changes applied
    Steps:
      1. Search for Config.Save() in FlightGamePatches.cs, RemoteAircraftManager.cs, NetworkManager.cs
      2. Verify zero occurrences in these high-frequency files
    Expected Result: 0 occurrences in hot-path files
    Evidence: .sisyphus/evidence/task-2-config-no-hotpath.txt
  ```

  **Commit**: YES (groups with Task 3)
  - Message: `fix(config): add explicit Config.Save() for reliable persistence`
  - Files: `src/UI/MultiplayerMenu.cs`, `src/Networking/LobbyManager.cs`

- [ ] 3. Validate Config Bug — Determine What Actually Fails to Persist

  **What to do**:
  - Read the config file at `TinyCombatArena[Host]/BepInEx/config/com.modder.tcamultiplayer.cfg`
  - Compare saved values against default values in `Plugin.cs:BindLoggingConfig()` (lines 642-669)
  - Check if BepInEx auto-save is working by examining:
    - `LastAircraft = F-16CJ 52` (not default `AV8B`) → SAVING WORKS for this field
    - `SpawnType = 0` (default is `1`) → SAVING WORKS for this field
    - `AircraftCollisions = false` (default is `true`) → SAVING WORKS for this field
  - Check if the `LobbyManager` constructor (lines 81-83) loads config correctly on startup
  - Check if `CreateLobby()` (lines 127-135) correctly loads saved host settings
  - Document which settings genuinely fail to persist vs which work fine
  - If ALL settings persist correctly, the explicit Config.Save() from Task 2 is still good insurance

  **Must NOT do**:
  - MUST NOT modify any code in this validation task
  - This is read-only analysis to inform Task 2

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: Read-only analysis of config file + code tracing
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 2)
  - **Blocks**: Task 2 (may modify approach)
  - **Blocked By**: None

  **References**:

  **Pattern References**:
  - `TinyCombatArena[Host]/BepInEx/config/com.modder.tcamultiplayer.cfg` — The actual config file with saved values
  - `src/Plugin.cs:640-669` — `BindLoggingConfig()` method with all config entry definitions and defaults
  - `src/Networking/LobbyManager.cs:75-84` — Constructor loading config values
  - `src/Networking/LobbyManager.cs:118-135` — `CreateLobby()` loading host settings

  **WHY Each Reference Matters**:
  - Config file shows WHICH values persist and which don't
  - BindLoggingConfig shows default values to compare against
  - Constructor/CreateLobby shows if saved values are loaded correctly

  **Acceptance Criteria**:

  **QA Scenarios (MANDATORY):**

  ```
  Scenario: Document which config values persist
    Tool: Bash (diff analysis)
    Preconditions: Config file exists
    Steps:
      1. Read config file values
      2. Compare each value against its default in Plugin.cs
      3. List which values differ from default (= successfully saved)
      4. List which values match default (= potentially not saving)
    Expected Result: Report documenting save status of each config entry
    Evidence: .sisyphus/evidence/task-3-config-validation.txt
  ```

  **Commit**: NO (validation only)

---

- [ ] 4. Fix Duplicate Nuclear Explosion Sync

  **What to do**:
  - ROOT CAUSE: When a nuclear munition explodes, TWO explosion sync packets are sent:
    1. FIRST: During `Munition.Explode()` execution, a huge crater spawns → `WorldDestructionPatches.SpawnCraterPostfix` → `ExplosionPatches.SendExplosionSyncForNukeCrater()` sends packet
    2. SECOND: After `Munition.Explode()` returns, `MunitionExplodePostfix` sends another packet, THEN sets dedup vars
  - The dedup check in `SendExplosionSyncForNukeCrater()` (line 855-861) fails because `_lastSentExplosionLocalPos/Time` haven't been set yet (MunitionExplodePostfix hasn't run)
  
  - **FIX**: Move dedup variable updates to BEFORE the packet send in `MunitionExplodePostfix`:
    1. In `MunitionExplodePostfix()` (ExplosionPatches.cs), move lines 541-542 (`_lastSentExplosionLocalPos = impactPoint; _lastSentExplosionTime = Time.time;`) to BEFORE the `SendPacket()` call on line 538
    2. This way, when the crater postfix fires DURING Explode(), the dedup vars are not set yet → nuke crater sends first. Then when MunitionExplodePostfix fires AFTER, it sets dedup vars BEFORE sending → `SendExplosionSyncForNukeCrater` never fires for the same position
    
    WAIT — that won't work because the crater fires DURING Explode() (before the postfix). The correct fix:
    
    In `SendExplosionSyncForNukeCrater()` (line 848): ALSO set `_lastSentExplosionLocalPos/Time` AFTER sending (line 887-888 already does this). The issue is that `MunitionExplodePostfix` doesn't CHECK dedup vars — it always sends.
    
    **ACTUAL FIX**: In `MunitionExplodePostfix()`, ADD a dedup check BEFORE sending for explosionType == 2 (nuclear). Check if `Time.time - _lastSentExplosionTime < TRIGGER_DEDUP_SECONDS && (impactPoint - _lastSentExplosionLocalPos).sqrMagnitude < TRIGGER_DEDUP_DISTANCE_SQ` — if true, skip sending. This way:
    1. Crater postfix sends FIRST, sets dedup vars
    2. MunitionExplodePostfix checks dedup vars, sees recent nearby send, SKIPS
    Result: exactly 1 packet per nuclear explosion.
  
  - In `ExplosionPatches.cs:MunitionExplodePostfix()`, add dedup check around line 516 (after classifying explosionType):
    ```csharp
    // Dedup: if SendExplosionSyncForNukeCrater already sent for this position, skip
    float timeSinceLast = Time.time - _lastSentExplosionTime;
    float distSq = (impactPoint - _lastSentExplosionLocalPos).sqrMagnitude;
    if (timeSinceLast < TRIGGER_DEDUP_SECONDS && distSq < TRIGGER_DEDUP_DISTANCE_SQ)
    {
        return; // Already handled by crater/trigger postfix
    }
    ```

  **Must NOT do**:
  - MUST NOT remove `ExplosionTriggerPostfix` — it's the universal fallback for modded weapons
  - MUST NOT change `SendExplosionSyncForNukeCrater` — it correctly handles nukes that bypass Munition.Explode()
  - MUST NOT widen the dedup thresholds (2m, 0.1s) as this could suppress legitimate nearby explosions

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: Requires understanding Harmony postfix execution order and multi-path dedup logic
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 5, 6)
  - **Blocks**: Tasks F1-F4
  - **Blocked By**: None

  **References**:

  **Pattern References**:
  - `src/Patches/ExplosionPatches.cs:432-552` — `MunitionExplodePostfix()` — the SECOND sender, needs dedup check added
  - `src/Patches/ExplosionPatches.cs:848-897` — `SendExplosionSyncForNukeCrater()` — the FIRST sender (fires during Explode()), already sets dedup vars at lines 887-888
  - `src/Patches/ExplosionPatches.cs:940-1045` — `ExplosionTriggerPostfix()` — the universal fallback, already HAS dedup check at lines 953-960
  - `src/Patches/ExplosionPatches.cs:66-71` — Dedup constants: `TRIGGER_DEDUP_SECONDS = 0.1f`, `TRIGGER_DEDUP_DISTANCE_SQ = 4f`
  - `src/Patches/WorldDestructionPatches.cs` — `SpawnCraterPostfix` that calls `SendExplosionSyncForNukeCrater` for huge craters

  **WHY Each Reference Matters**:
  - `MunitionExplodePostfix` is where the duplicate originates — it's the ONLY sender without a dedup check
  - `SendExplosionSyncForNukeCrater` already has a dedup check (line 855-861) AND sets dedup vars (887-888) — it's correct
  - `ExplosionTriggerPostfix` already has a dedup check (953-960) — it's correct; copy its pattern to MunitionExplodePostfix
  - The dedup constants define the matching window — must use the SAME values

  **Acceptance Criteria**:

  **QA Scenarios (MANDATORY):**

  ```
  Scenario: MunitionExplodePostfix has dedup check
    Tool: Bash (code review)
    Preconditions: Code changes applied to ExplosionPatches.cs
    Steps:
      1. Read MunitionExplodePostfix method
      2. Verify dedup check exists BEFORE the SendPacket call
      3. Verify it uses _lastSentExplosionTime and _lastSentExplosionLocalPos
      4. Verify it uses TRIGGER_DEDUP_SECONDS and TRIGGER_DEDUP_DISTANCE_SQ constants
    Expected Result: Dedup check present before send, using same pattern as ExplosionTriggerPostfix
    Failure Indicators: No dedup check, or check after send, or different constants
    Evidence: .sisyphus/evidence/task-4-explosion-dedup.txt

  Scenario: ExplosionTriggerPostfix and SendExplosionSyncForNukeCrater unchanged
    Tool: Bash (grep)
    Preconditions: Code changes applied
    Steps:
      1. Verify ExplosionTriggerPostfix still has its dedup check
      2. Verify SendExplosionSyncForNukeCrater still sets dedup vars
    Expected Result: Both methods unchanged
    Evidence: .sisyphus/evidence/task-4-other-senders-unchanged.txt
  ```

  **Commit**: YES
  - Message: `fix(explosions): deduplicate nuclear explosion sync packets`
  - Files: `src/Patches/ExplosionPatches.cs`

- [ ] 5. Fix Bullet Velocity on Remote Aircraft Guns

  **What to do**:
  - ROOT CAUSE: In `FireControlPatches.Update_Prefix()` (UniPilotPatches.cs:247-248), when the remote aircraft's Gun2 fires:
    ```csharp
    __instance.Gun.IsFiring = shouldFire;
    __instance.Gun.Update(Time.timeAsDouble, Time.deltaTime);
    ```
    Gun2.Update() calls `FireFromNextBarrel()` which spawns bullets. The bullets inherit velocity from the aircraft's Rigidbody. But the remote aircraft's `rb.velocity` is set to a STEERING velocity (`(targetPos - rb.position) / dt`) in `VelocitySteerForState()`, NOT the actual aircraft velocity from the network packet.
  - At 250 m/s with even 0.5m position error, steering velocity differs from true velocity by up to 25 m/s — causing bullets to have wrong initial velocity and thus shorter effective range.
  
  - **FIX**: Before calling `Gun.Update()`, temporarily set `rb.velocity` to the synced aircraft velocity from the last state packet. After `Gun.Update()`, restore the steering velocity.
  - In `FireControlPatches.Update_Prefix()` (UniPilotPatches.cs), before line 248:
    1. Get the `RemoteAircraftState` from `RemoteAircraftManager` for this aircraft's peer
    2. Read `state.LastVelocity` (the actual synced velocity from the network packet)
    3. Get the Rigidbody: `var rb = root.GetComponent<Rigidbody>()`
    4. Save current steering velocity: `var savedVel = rb.velocity`
    5. Set true velocity: `rb.velocity = state.LastVelocity`
    6. Call `__instance.Gun.Update(...)`
    7. Restore steering velocity: `rb.velocity = savedVel`
  - This ensures bullets spawn with correct initial velocity matching the actual aircraft speed

  **Must NOT do**:
  - MUST NOT modify the local aircraft fast-path (lines 204-208 — `return true`)
  - MUST NOT change VelocitySteerForState() in RemoteAircraftManager
  - MUST NOT permanently set rb.velocity to packet velocity (would break interpolation)

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: Requires understanding Gun2 bullet spawning, rigidbody velocity, and ensuring no side effects
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 4, 6)
  - **Blocks**: Tasks F1-F4
  - **Blocked By**: Task 6 (validation of Gun2 velocity source)

  **References**:

  **Pattern References**:
  - `src/Patches/UniPilotPatches.cs:200-271` — `FireControlPatches.Update_Prefix()` — the exact method to modify
  - `src/Patches/UniPilotPatches.cs:247-248` — Gun firing lines where velocity swap must happen
  - `src/Networking/RemoteAircraftManager.cs:392-459` — `VelocitySteerForState()` — shows steering velocity calculation on line 430
  - `src/Networking/RemoteAircraftManager.cs:591` — `state.LastVelocity = velocity` — where synced velocity is stored

  **API/Type References**:
  - `RemoteAircraftManager.GetRemotePlayer(ulong peerId)` — returns `RemoteAircraftState` with `LastVelocity`
  - `RemoteAircraftController.PlayerId` — peer ID to look up the state
  - `Plugin.Instance.Network.RemoteAircraftManager` — access to the manager

  **WHY Each Reference Matters**:
  - `Update_Prefix` is where bullets are fired — the velocity swap must bracket the `Gun.Update()` call
  - `VelocitySteerForState` shows what `rb.velocity` is normally set to (steering, NOT true velocity)
  - `LastVelocity` is the correct source of truth for the aircraft's real speed from network packets

  **Acceptance Criteria**:

  **QA Scenarios (MANDATORY):**

  ```
  Scenario: Velocity swap brackets Gun.Update() call
    Tool: Bash (code review)
    Preconditions: Code changes applied to UniPilotPatches.cs
    Steps:
      1. Read Update_Prefix method, find the Gun.Update() call
      2. Verify rb.velocity is set to LastVelocity BEFORE Gun.Update()
      3. Verify rb.velocity is restored to saved value AFTER Gun.Update()
      4. Verify the swap only happens when shouldFire is true (optimization)
    Expected Result: Velocity swap pattern: save → set true → Gun.Update → restore
    Failure Indicators: No swap, or swap not restored, or swap in local aircraft path
    Evidence: .sisyphus/evidence/task-5-velocity-swap.txt

  Scenario: Local aircraft path unchanged
    Tool: Bash (code review)
    Preconditions: Code changes applied
    Steps:
      1. Verify lines 204-208 still return true immediately for local aircraft
      2. Verify no new code executes before the IsRemote check
    Expected Result: Local aircraft fast-path unchanged
    Evidence: .sisyphus/evidence/task-5-local-unchanged.txt
  ```

  **Commit**: YES
  - Message: `fix(guns): use synced velocity for remote aircraft bullet spawning`
  - Files: `src/Patches/UniPilotPatches.cs`

- [ ] 6. Validate Gun2 Velocity Source via Decompiled Code

  **What to do**:
  - Read decompiled Gun2 source in `Decompiled/Assembly-CSharp/Falcon/Weapons/` to confirm:
    1. Does `Gun2.Update()` → `FireFromNextBarrel()` read velocity from the parent Rigidbody?
    2. Or does it use an internal velocity field?
    3. What property/field on Rigidbody does it read? (.velocity? .GetPointVelocity()?)
  - Also check `Bullet2Manager.FireBullet()` to see if bullet initial velocity comes from:
    a. The barrel transform's forward * muzzle velocity
    b. The parent rigidbody's velocity (added to muzzle velocity)
    c. A combination of both
  - Document findings to inform Task 5's implementation
  - If bullets DON'T read from Rigidbody.velocity, Task 5's approach needs adjustment

  **Must NOT do**:
  - MUST NOT modify any code
  - This is read-only analysis

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: Read-only code analysis of decompiled files
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 4, 5)
  - **Blocks**: Task 5 (informs approach)
  - **Blocked By**: None

  **References**:

  **Pattern References**:
  - `Decompiled/Assembly-CSharp/Falcon/` — Decompiled game code directory
  - Search for `Gun2` class, `FireFromNextBarrel`, `FireBullet` methods
  - Search for `Bullet2Manager.FireBullet` to see bullet velocity initialization

  **WHY Each Reference Matters**:
  - Must confirm bullets actually inherit rigidbody velocity before implementing velocity swap
  - If bullets use a different velocity source, the fix approach changes entirely

  **Acceptance Criteria**:

  **QA Scenarios (MANDATORY):**

  ```
  Scenario: Document Gun2 bullet velocity source
    Tool: Bash (grep + read)
    Preconditions: Decompiled code available
    Steps:
      1. Find Gun2 class in decompiled code
      2. Read FireFromNextBarrel or equivalent method
      3. Trace bullet velocity initialization
      4. Document whether rb.velocity is used
    Expected Result: Report documenting exact velocity source for bullet spawning
    Evidence: .sisyphus/evidence/task-6-gun2-velocity-analysis.txt
  ```

  **Commit**: NO (validation only)

---

- [ ] 7. Fix Remote Aircraft Jitter — Data-Driven Velocity Smoothing

  **What to do**:
  - ROOT CAUSE (confirmed by logs): The velocity steering `rb.velocity = (targetPos - rb.position) / dt` amplifies ANY target position discontinuity. Even a 0.1m interpolation error at 50Hz FixedUpdate creates a 5 m/s velocity spike. The logs show FloatingOrigin blacklist IS working and rigidbody IS configured correctly, so the primary source is interpolation output discontinuities amplified by velocity steering.
  
  - **FIX 1 — Use packet velocity as a data-driven velocity bound** (NO hardcoded speeds):
    The `AircraftStatePacket` contains the aircraft's ACTUAL velocity from the game (VelX/Y/Z). This is the game's own native data — the aircraft cannot physically exceed this velocity plus some acceleration margin. Use this as a sanity check:
    In `VelocitySteerForState()`, after computing steering velocity:
    ```csharp
    // Data-driven velocity bound: use the synced packet velocity magnitude as a reference
    // The aircraft's actual speed comes from the game engine — no hardcoded limits needed
    float packetSpeed = state.LastVelocity.magnitude;
    float steeringSpeed = rb.velocity.magnitude;
    // If steering velocity exceeds 2x the actual aircraft speed + 10 (for low-speed margin),
    // scale it down — this catches position discontinuity spikes without imposing artificial limits
    float maxReasonableSpeed = Mathf.Max(packetSpeed * 2f, packetSpeed + 10f);
    if (steeringSpeed > maxReasonableSpeed && maxReasonableSpeed > 0.1f)
    {
        rb.velocity = rb.velocity * (maxReasonableSpeed / steeringSpeed);
    }
    ```
    Same for angular velocity — use the synced angular velocity as the reference:
    ```csharp
    float packetAngSpeed = state.Buffer.GetNewestSnapshot().AngularVelocity.magnitude;
    float steeringAngSpeed = rb.angularVelocity.magnitude;
    float maxReasonableAngSpeed = Mathf.Max(packetAngSpeed * 2f, packetAngSpeed + 1f);
    if (steeringAngSpeed > maxReasonableAngSpeed && maxReasonableAngSpeed > 0.01f)
    {
        rb.angularVelocity = rb.angularVelocity * (maxReasonableAngSpeed / steeringAngSpeed);
    }
    ```
    This is 100% data-driven — the bound comes from the game's own physics, not hardcoded values.
  
  - **FIX 2 — Ensure rigidbody settings are enforced every FixedUpdate**:
    The logs confirm initial setup is correct (line 394: `non-kinematic + Interpolate`). But add enforcement in VelocitySteerForState as a guard:
    ```csharp
    if (rb.interpolation != RigidbodyInterpolation.Interpolate)
        rb.interpolation = RigidbodyInterpolation.Interpolate;
    if (rb.drag != 0f) rb.drag = 0f;
    if (rb.angularDrag != 0f) rb.angularDrag = 0f;
    ```
  
  - **FIX 3 — Prevent dt amplification on catch-up frames**:
    When Unity runs multiple FixedUpdates in one frame (catch-up), `dt` is the same but the interpolation target hasn't changed, causing the same velocity to be applied multiple times. Add a guard:
    ```csharp
    // If position hasn't changed since last FixedUpdate, skip velocity update
    // This prevents redundant steering during Unity's FixedUpdate catch-up
    if (state.LastAppliedPosition == targetPos && state.HasAppliedPose)
    {
        return; // Target hasn't moved — no new steering needed
    }
    ```

  **Must NOT do**:
  - MUST NOT use hardcoded speed/velocity limits — all bounds must come from game data (packet velocity)
  - MUST NOT modify InterpolationBuffer.cs render clock or Hermite interpolation
  - MUST NOT add per-frame allocations (use magnitude, sqrMagnitude for comparisons)
  - MUST NOT change the velocity steering approach fundamentally (keep `(target - current) / dt`)
  - MUST NOT affect local aircraft rigidbodies
  - MUST NOT edit the game's native values or physics settings

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: Rigidbody physics tuning requires careful understanding of Unity's physics pipeline
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 8, 9)
  - **Blocks**: Tasks F1-F4
  - **Blocked By**: Task 8 (FloatingOrigin validation — already confirmed working by logs)

  **References**:

  **Pattern References**:
  - `src/Networking/RemoteAircraftManager.cs:392-459` — `VelocitySteerForState()` — primary method to modify
  - `src/Networking/RemoteAircraftManager.cs:406-407` — Existing isKinematic/useGravity guards
  - `src/Networking/RemoteAircraftManager.cs:430` — The velocity steering line: `rb.velocity = (targetPos - rb.position) / dt`
  - `src/Networking/RemoteAircraftManager.cs:591` — `state.LastVelocity = velocity` — the game's actual velocity from packet
  - `src/Networking/RemoteAircraftManager.cs:1200+` — `ConfigureRemoteAircraft()` — initial rigidbody setup
  - `TinyCombatArena[Host]/BepInEx/LogOutput.log:392-394` — Logs confirming FO blacklist works and rb configured correctly

  **WHY Each Reference Matters**:
  - `VelocitySteerForState` is where jitter manifests — data-driven velocity bound goes here
  - `state.LastVelocity` is the game's own velocity data — use it as the velocity reference instead of hardcoded limits
  - Logs prove FO blacklist and rb setup work, so jitter is purely from steering amplification

  **Acceptance Criteria**:

  **QA Scenarios (MANDATORY):**

  ```
  Scenario: Velocity bound uses packet data, not hardcoded values
    Tool: Bash (code review)
    Preconditions: Code changes applied to RemoteAircraftManager.cs
    Steps:
      1. Read VelocitySteerForState method
      2. Verify velocity bound references state.LastVelocity (packet data)
      3. Verify NO hardcoded speed constants (no 500f, no Mach values)
      4. Verify the bound scales with actual aircraft speed
    Expected Result: Velocity bound is data-driven from game's packet velocity
    Failure Indicators: Any hardcoded speed limit, or bound that doesn't reference packet velocity
    Evidence: .sisyphus/evidence/task-7-data-driven-velocity.txt

  Scenario: Rigidbody settings enforced in FixedUpdate
    Tool: Bash (code review + grep)
    Preconditions: Code changes applied
    Steps:
      1. grep for RigidbodyInterpolation.Interpolate in RemoteAircraftManager.cs
      2. Verify guard enforcement in VelocitySteerForState
    Expected Result: rb.interpolation, rb.drag, rb.angularDrag enforced every FixedUpdate
    Evidence: .sisyphus/evidence/task-7-rb-enforcement.txt
  ```

  **Commit**: YES
  - Message: `fix(interpolation): data-driven velocity smoothing to reduce remote aircraft jitter`

- [ ] 8. Validate FloatingOrigin Blacklist Effectiveness

  **What to do**:
  - In `RemoteAircraftManager.ConfigureRemoteAircraft()` (line 1208-1230), the code tries to blacklist remote aircraft from FloatingOrigin
  - Read the blacklist code and verify:
    1. Does the reflection successfully find the FloatingOrigin.Blacklist property/list?
    2. Is the remote aircraft actually added to the blacklist?
    3. What happens if the blacklist fails? (fallback behavior)
  - Also read `FloatingOriginHelper.cs` to understand FloatingOrigin shift mechanics
  - Check if there are any game logs indicating blacklist success/failure
  - If blacklist is NOT working, this explains micro-position-shifts causing jitter
  - If blacklist IS working, FloatingOrigin shifts are NOT a jitter source

  **Must NOT do**:
  - MUST NOT modify any code
  - This is read-only analysis

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: Read-only code analysis
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 7, 9)
  - **Blocks**: Task 7 (informs if FO is a jitter source)
  - **Blocked By**: None

  **References**:

  **Pattern References**:
  - `src/Networking/RemoteAircraftManager.cs:1208-1230` — FloatingOrigin blacklist code
  - `src/Networking/FloatingOriginHelper.cs` — Full FO helper with shift detection

  **Acceptance Criteria**:

  **QA Scenarios (MANDATORY):**

  ```
  Scenario: Document FloatingOrigin blacklist status
    Tool: Bash (code review)
    Preconditions: Source code available
    Steps:
      1. Read ConfigureRemoteAircraft blacklist code
      2. Check if reflection finds the Blacklist property
      3. Document whether blacklist is working or failing
    Expected Result: Report on blacklist effectiveness
    Evidence: .sisyphus/evidence/task-8-fo-blacklist.txt
  ```

  **Commit**: NO (validation only)

- [ ] 9. Build Verification — Compile All Changes

  **What to do**:
  - Run `dotnet build` in the project directory to verify all changes compile
  - Fix any compilation errors introduced by Tasks 1-7
  - Verify no new warnings added

  **Must NOT do**:
  - MUST NOT add functionality — only fix compilation errors from previous tasks

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: Single build command + potential minor fixes
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Sequential (after Wave 3 implementation tasks)
  - **Blocks**: Tasks F1-F4
  - **Blocked By**: Tasks 1-8

  **References**:

  **Pattern References**:
  - `src/TCAMultiplayer.csproj` — Project file for build configuration

  **Acceptance Criteria**:

  **QA Scenarios (MANDATORY):**

  ```
  Scenario: Project compiles with zero errors
    Tool: Bash
    Preconditions: All previous tasks completed
    Steps:
      1. Run `dotnet build` in project root
      2. Verify exit code 0
      3. Verify no error messages in output
    Expected Result: Build succeeded, 0 errors
    Failure Indicators: Non-zero exit code, error messages
    Evidence: .sisyphus/evidence/task-9-build-output.txt
  ```

  **Commit**: YES (if fixes needed)
  - Message: `fix(build): resolve compilation issues from bug fixes`
  - Files: Any files with compilation errors

## Final Verification Wave

- [ ] F1. **Plan Compliance Audit** — `deep`
  Read the plan end-to-end. For each "Must Have": verify implementation exists (read file, check diff). For each "Must NOT Have": search codebase for forbidden patterns — reject with file:line if found. Check evidence files exist in .sisyphus/evidence/. Compare deliverables against plan.
  Output: `Must Have [N/N] | Must NOT Have [N/N] | Tasks [N/N] | VERDICT: APPROVE/REJECT`

- [ ] F2. **Code Quality Review + Build** — `unspecified-high`
  Run `dotnet build` in the project directory. Review all changed files for: empty catches, unused imports, debug logging left in hot paths. Check AI slop: excessive comments, over-abstraction.
  Output: `Build [PASS/FAIL] | Files [N clean/N issues] | VERDICT`

- [ ] F3. **Regression Check — Local Aircraft Paths** — `deep`
  For each modified file, trace all code paths that execute for LOCAL aircraft (not remote). Verify NONE of the changes affect local aircraft behavior. Check: FlightGamePatches.SendLocalPlayerState(), DamagePatches prefixes, FireControlPatches local fast-path.
  Output: `Local paths verified [N/N] | Regressions found [CLEAN/N issues] | VERDICT`

- [ ] F4. **Scope Fidelity Check** — `deep`
  For each task: read "What to do", read actual diff. Verify 1:1 — everything in spec was built, nothing beyond spec was built. Check "Must NOT do" compliance. Flag unaccounted changes.
  Output: `Tasks [N/N compliant] | Unaccounted [CLEAN/N files] | VERDICT`

---

## Commit Strategy

- **Commit 1**: `fix(lobby): broadcast state immediately on player join` — LobbyManager.cs
- **Commit 2**: `fix(config): add explicit Config.Save() for reliable persistence` — Plugin.cs, LobbyManager.cs, MultiplayerMenu.cs
- **Commit 3**: `fix(explosions): deduplicate nuclear explosion sync packets` — ExplosionPatches.cs
- **Commit 4**: `fix(guns): use synced velocity for remote aircraft bullet spawning` — UniPilotPatches.cs, RemoteAircraftManager.cs
- **Commit 5**: `fix(interpolation): smooth velocity steering to reduce remote aircraft jitter` — RemoteAircraftManager.cs, InterpolationBuffer.cs

---

## Success Criteria

### Verification Commands
```bash
dotnet build  # Expected: Build succeeded with 0 errors
```

### Final Checklist
- [ ] All 5 bugs addressed with targeted fixes
- [ ] All "Must Have" present
- [ ] All "Must NOT Have" absent
- [ ] Build passes with 0 errors
- [ ] No local aircraft regressions identified
