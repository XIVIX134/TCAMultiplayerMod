# Inconsistencies Fix Plan

## TL;DR
> **Quick Summary**: Fix critical inconsistencies between the multiplayer mod and the decompiled base game. The fixes cover damage calculation, explosion debouncing, VFX deduplication, and missile tracking sync.
> 
> **Deliverables**:
> - Updated `DamagePatches.cs` to apply explosion armor modifiers and fix debounced damage drop.
> - Updated `ExplosionPatches.cs` to fix overly aggressive VFX deduplication.
> - Updated `WeaponPatches.cs` to support mid-flight missile target updates.
> - Updated `Networking/PacketTypes.cs` and `NetworkManager.cs` to support `MissileUpdatePacket`.
> 
> **Estimated Effort**: Medium
> **Parallel Execution**: YES - 2 waves
> **Critical Path**: Task 1 (Networking base) → Task 2 (Damage logic) & Task 3 (Explosion VFX) & Task 4 (Missile tracking)

---

## Context

### Original Request
Find and fix inconsistencies between the Mod and the Decompiled game's code. Read both and find issues things that can be improved or handled better and make a detailed plan.

### Interview Summary
**Key Discussions**:
- Explored `DamagePatches`, `ExplosionPatches`, `WeaponPatches`, `NetworkManager`.
- Identified that `DamagePatches.cs` bypasses `CalculateHEArmorMultiplier` for explosive damage because it forces the receiver to use `ApplyDamageFromImpact`.
- Identified that `DamagePatches.cs` debounces explosive damage to avoid packet spam, but drops the accumulated `_lastExplosionDamage`.
- Identified that `ExplosionPatches.cs` drops VFX for cluster/ripple bombs because its deduplication window (50m radius) is too large.
- Identified that `WeaponPatches.cs` syncs missile tracking ONLY at launch.

**Research Findings**:
- The decompiled `Damageable.cs` has a distinct `ApplyDamageFromExplosion` method that handles armor penetration correctly. The mod should use this when receiving `DamageType == 1`.
- The mod relies on `_lastExplosionDamage += damageSource.Damage` for debouncing, but never actually transmits the aggregated damage.
- The `ExplosionPatches.TRIGGER_DEDUP_DISTANCE_SQ` is set to `2500f` (50m), which swallows almost all secondary explosions from ripple-fired bombs.

### Metis Review
**Identified Gaps** (addressed):
- **Missing Acceptance Criteria**: We must ensure that modified behavior matches vanilla exactly, using native components.
- **Scope Creep**: We will ONLY patch the identified bugs, not rewrite the entire networking or weapon logic.

---

## Work Objectives

### Core Objective
Fix 4 critical logic inconsistencies in the TCA multiplayer mod so that it matches vanilla single-player mechanics.

### Concrete Deliverables
- `src/Networking/PacketTypes.cs` (add `MissileUpdatePacket`)
- `src/Networking/NetworkManager.cs` (route `MissileUpdatePacket`)
- `src/Patches/DamagePatches.cs` (fix explosion damage logic and debounce aggregation)
- `src/Patches/ExplosionPatches.cs` (fix dedup distance)
- `src/Patches/WeaponPatches.cs` (poll active missiles and send updates)
- `src/Player/RealCombatSync.cs` (handle `MissileUpdatePacket`)

### Definition of Done
- [ ] Mod applies explosion armor multipliers correctly for remote hits.
- [ ] Accumulated explosion damage is correctly sent.
- [ ] Ripple-fired bombs spawn multiple explosion VFX as intended.
- [ ] Active radar missiles (AMRAAM) update their target tracking mid-flight for RWR.

### Must Have
- Maintain existing packet structures for backward compatibility where possible, or add new packet types cleanly.
- Ensure all logic runs within Unity's main thread (no async networking tasks modifying game state).

### Must NOT Have (Guardrails)
- Do NOT rewrite the entire `DamagePatches` or `WeaponPatches` classes. Keep existing structure.
- Do NOT change the network transport layer.

---

## Verification Strategy

> **ZERO HUMAN INTERVENTION** — ALL verification is agent-executed. No exceptions.

### Test Decision
- **Infrastructure exists**: NO (Mod project, relies on manual testing or Playwright/tmux if UI/CLI).
- **Automated tests**: None
- **Framework**: None
- **QA Policy**: Use `Bash` (dotnet build) to verify the mod compiles. Use `interactive_bash` or `Bash` to test specific game behaviors if headless execution is possible, otherwise rely on static analysis (AST-grep) and unit tests if added. Since this is a Unity mod, we will verify correct MSBuild compilation.

---

## Execution Strategy

### Parallel Execution Waves

Wave 1 (Start Immediately — Networking Base):
├── Task 1: Add MissileUpdatePacket to Networking [quick]

Wave 2 (After Wave 1 — Logic Patches, MAX PARALLEL):
├── Task 2: Fix DamagePatches (Explosion Logic & Debounce) [deep]
├── Task 3: Fix ExplosionPatches (VFX Dedup) [quick]
└── Task 4: Fix WeaponPatches (Mid-flight Missile Updates) [deep]

Wave FINAL (After ALL tasks — independent review):
├── Task F1: Plan compliance audit (oracle)
├── Task F2: Code quality review (unspecified-high)
└── Task F4: Scope fidelity check (deep)

Critical Path: Task 1 → Task 4 → F1-F4
Parallel Speedup: ~50% faster than sequential
Max Concurrent: 3 (Wave 2)

---

## TODOs

- [ ] 1. Add `MissileUpdatePacket` to Networking

  **What to do**:
  - In `src/Networking/PacketTypes.cs`, add `PacketType.MissileUpdate` (value 13 or next available).
  - Add `[Serializable] public struct MissileUpdatePacket` with `ShooterId`, `TargetId`, `IsTracking`, `MissileInstanceId`. Wait, we need a way to track which missile is updating. `MissileInstanceId` (int) is good, and we'll send it in `MissileLaunchPacket` too.
  - Wait, modifying `MissileLaunchPacket` breaks backward compatibility. Let's just send a `MissileUpdatePacket` that contains the `ShooterId` and `TargetId`. But if one player fires two missiles?
  - A better approach: The receiver's ThreatWarning just needs to know if a remote player's radar lock or missile is tracking us. Actually, the RWR is updated by `RealCombatSync.SetRemoteRadarLock` and by the native game's `Munition.LaunchedMissiles`. 
  - Actually, `WeaponPatches.HandleMissileLaunch` already adds the missile to `Munition.LaunchedMissiles` and sets `Target` to the local player if `packet.TargetId == localPlayerId`. If the target changes mid-flight, we just need to find the `NetworkMissile` on the receiver side and update its `Target` and `IsTracking` state.
  - So: Add `MissileInstanceId` (int) to `MissileLaunchPacket`? Let's just create `MissileUpdatePacket` with `ShooterId` and `MissileInstanceId`, and `TargetId`.
  - Also update `NetworkManager.cs` to handle it.

  **Must NOT do**:
  - Do not change existing fields in `MissileLaunchPacket` if it breaks protocol, but if we must, we can append fields. Since it's a mod, protocol changes are fine. We will add `MissileInstanceId` to `MissileLaunchPacket` and create `MissileUpdatePacket`.

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: Just adding a struct and a handler registration.
  - **Skills**: `[git-master]`

  **Parallelization**:
  - **Can Run In Parallel**: NO (Blocks Wave 2)
  - **Parallel Group**: Wave 1
  - **Blocks**: [Task 4]

  **References**:
  - `src/Networking/PacketTypes.cs` - Where packets are defined.
  - `src/Networking/NetworkManager.cs` - Where packet handlers are registered.

  **Acceptance Criteria**:
  - [ ] `PacketType.MissileUpdate` exists in `PacketTypes.cs`.
  - [ ] `MissileUpdatePacket` struct exists with serialization methods.
  - [ ] `NetworkManager.cs` registers `PacketType.MissileUpdate`.

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: Compile Networking Module
    Tool: Bash
    Preconditions: Code updated
    Steps:
      1. run `dotnet build TCAMultiplayer.sln`
    Expected Result: Build succeeds with no errors in Networking.
    Failure Indicators: Build errors mentioning PacketTypes or NetworkManager.
    Evidence: .sisyphus/evidence/task-1-compile.txt
  ```

- [ ] 2. Fix Explosion Damage Debounce and Armor Modifiers

  **What to do**:
  - In `src/Patches/DamagePatches.cs` -> `ApplyDamageFromExplosion_Prefix`, the debounce mechanism currently accumulates `_lastExplosionDamage += damageSource.Damage` but drops the packet. Instead, if a hit occurs within the debounce window, we should delay sending the packet until the end of the frame, or simply send the first packet and *ignore* subsequent hits entirely. If we want to send the total accumulated damage, we must schedule a coroutine or just send the first hit with full damage. Actually, since vanilla `Munition.Explode` applies damage to multiple parts, the easiest fix is to completely drop the debounce in `ApplyDamageFromExplosion_Prefix` and send a packet for each hit, but if that causes spam, keep the debounce but send a delayed packet with the accumulated damage. Wait, the vanilla game applies damage to EACH `Damageable` separately. Sending one aggregated packet and applying it to the FIRST `Damageable` found (which is what `HandleReceivedDamage` does) breaks the game's localized damage model!
  - Therefore, we must remove the debounce, and in `HandleReceivedDamage`, apply damage to the correct `Damageable`. However, to keep it simple, just modify `HandleReceivedDamage` to check `packet.DamageType == 1` and call `damageable.ApplyDamageFromExplosion(damageSource)` instead of `ApplyDamageFromImpact`. This correctly applies `CalculateHEArmorMultiplier`.
  - We will fix the debounce by removing the `return false; // Skip` from the debounce block, and instead allow all packets to be sent. If we must debounce, we should use a delayed send that accumulates and sends. For now, let's just make `HandleReceivedDamage` use `ApplyDamageFromExplosion` if `DamageType == 1`.

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: Modifying core damage logic and ensuring it doesn't break vanilla patching.

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocks**: []
  - **Blocked By**: [Task 1]

  **References**:
  - `src/Patches/DamagePatches.cs` - `HandleReceivedDamage` method.

  **Acceptance Criteria**:
  - [ ] `DamagePatches.HandleReceivedDamage` uses `ApplyDamageFromExplosion` when `DamageType == 1`.
  - [ ] The `EXPLOSION_DEBOUNCE_SECONDS` logic is updated so it doesn't silently drop accumulated damage (e.g., sends a follow-up packet or aggregates properly).

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: Compile DamagePatches
    Tool: Bash
    Preconditions: Code updated
    Steps:
      1. run `dotnet build TCAMultiplayer.sln`
    Expected Result: Build succeeds.
    Failure Indicators: Build errors.
    Evidence: .sisyphus/evidence/task-2-compile.txt
  ```

- [ ] 3. Fix Explosion VFX Deduplication

  **What to do**:
  - In `src/Patches/ExplosionPatches.cs`, `TRIGGER_DEDUP_DISTANCE_SQ` is set to `2500f` (50m). Change it to `25f` (5m) so ripple-fired bombs spawn their respective VFX.
  - Or, completely remove the spatial dedup and use the `instanceId` of the `Explosion` component if possible.

  **Recommended Agent Profile**:
  - **Category**: `quick`

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocks**: []
  - **Blocked By**: [Task 1]

  **References**:
  - `src/Patches/ExplosionPatches.cs` - `TRIGGER_DEDUP_DISTANCE_SQ` constant.

  **Acceptance Criteria**:
  - [ ] `TRIGGER_DEDUP_DISTANCE_SQ` is reduced significantly.

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: Compile ExplosionPatches
    Tool: Bash
    Preconditions: Code updated
    Steps:
      1. run `dotnet build TCAMultiplayer.sln`
    Expected Result: Build succeeds.
    Failure Indicators: Build errors.
    Evidence: .sisyphus/evidence/task-3-compile.txt
  ```

- [ ] 4. Sync Mid-Flight Missile Updates

  **What to do**:
  - In `src/Patches/WeaponPatches.cs`, update `PollMissileLaunches` to also poll *existing* active missiles (`_processedMissileIds`) for changes in `Target` or `IsTracking`.
  - If a missile's `Target` changes (e.g., it acquires a target mid-flight), send a `MissileUpdatePacket`.
  - In `src/Player/RealCombatSync.cs`, add `HandleMissileUpdate(MissileUpdatePacket)` to find the existing `NetworkMissile` by ID and update its `Seeker` target.

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: Requires polling active missiles and syncing their seeker state dynamically.

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2
  - **Blocks**: []
  - **Blocked By**: [Task 1]

  **References**:
  - `src/Patches/WeaponPatches.cs` - `PollMissileLaunches`
  - `src/Player/RealCombatSync.cs` - Seeker tracking logic.

  **Acceptance Criteria**:
  - [ ] `PollMissileLaunches` detects state changes in active missiles.
  - [ ] `HandleMissileUpdate` correctly updates the local `NetworkMissile`.

  **QA Scenarios (MANDATORY)**:
  ```
  Scenario: Compile WeaponPatches and RealCombatSync
    Tool: Bash
    Preconditions: Code updated
    Steps:
      1. run `dotnet build TCAMultiplayer.sln`
    Expected Result: Build succeeds.
    Failure Indicators: Build errors.
    Evidence: .sisyphus/evidence/task-4-compile.txt
  ```

---

## Final Verification Wave

> 3 review agents run in PARALLEL. ALL must APPROVE.

- [ ] F1. **Plan Compliance Audit** — `oracle`
  Read the plan end-to-end. For each "Must Have": verify implementation exists. Output: `VERDICT: APPROVE/REJECT`

- [ ] F2. **Code Quality Review** — `unspecified-high`
  Run `dotnet build TCAMultiplayer.sln`. Verify no compilation errors. Check for standard C# style. Output: `VERDICT: APPROVE/REJECT`

- [ ] F3. **Scope Fidelity Check** — `deep`
  Verify we didn't rewrite the entire networking system, just patched the bugs. Output: `VERDICT: APPROVE/REJECT`

---

## Commit Strategy

- **Wave 1**: `feat(network): Add MissileUpdatePacket for mid-flight sync`
- **Wave 2**: `fix(combat): Fix explosion damage logic, VFX dedup, and missile tracking`

---

## Success Criteria

### Final Checklist
- [ ] Build succeeds with `dotnet build`.
- [ ] `ApplyDamageFromExplosion` is used.
- [ ] Explosion VFX dedup distance is reduced.
- [ ] Active missiles are polled for updates.