## Issue Summary

Remote planes exhibit visible jitter/stutter during gameplay. The user reports "jitter issues on remote planes" — the remote aircraft position and rotation appear to jump or stutter rather than moving smoothly.

## Root Cause Analysis

The jitter has **three interacting root causes** in the interpolation and velocity-steering pipeline:

### 1. No Extrapolation on Buffer Underrun (PRIMARY)

**File:** `src/Networking/InterpolationBuffer.cs`, lines 274-278 (before fix)

When the render clock advances past the newest available snapshot (i.e., only a `before` snapshot exists but no `after`), the original code **held the last known position**:

```csharp
else if (before.IsValid)
{
    // Only have "before" — hold position (no extrapolation)
    rawAbsolutePosition = before.AbsolutePosition;
    rawRotation = before.Rotation;
}
```

This causes the remote plane to **freeze in place** until the next packet arrives, then **jump** to its new interpolated position. At 250 m/s, even a single missed FixedUpdate tick (20ms) = 5 meters of jump. This is the most visible source of jitter.

With `InterpolationDelay = 0.15s` and packets arriving at ~90Hz (~11ms apart), any network jitter spike >150ms causes a buffer underrun. On non-LAN connections, this happens frequently.

### 2. Velocity Clamping Prevents Catch-Up Correction

**File:** `src/Networking/RemoteAircraftManager.cs`, lines 438-444 (before fix)

```csharp
float packetSpeed = state.LastVelocity.magnitude;
float steeringSpeed = rb.velocity.magnitude;
float maxReasonableSpeed = Mathf.Max(packetSpeed * 2f, packetSpeed + 10f);
if (maxReasonableSpeed > 0.1f && steeringSpeed > maxReasonableSpeed)
{
    rb.velocity = rb.velocity * (maxReasonableSpeed / steeringSpeed);
}
```

After a buffer underrun (where the plane held position), the velocity-steering needs to catch up. The catch-up velocity is `distance_error / fixedDeltaTime`, which can be much larger than the aircraft's actual flight speed. The clamp at `packetSpeed * 2` prevents this catch-up from completing in a single step, causing the error to **persist across multiple frames** as the rigidbody incrementally corrects. This turns a single large jump into a series of smaller but still visible jerks.

### 3. Angular Velocity Clamping Too Restrictive

**File:** `src/Networking/RemoteAircraftManager.cs`, lines 462-471 (before fix)

```csharp
float packetAngSpeed = newest.AngularVelocity.magnitude;
float maxReasonableAngSpeed = Mathf.Max(packetAngSpeed * 2f, packetAngSpeed + 1f);
```

During near-level flight, `packetAngSpeed` is very small (near zero), so the cap becomes ~1 rad/s. But rotational corrections after jitter can require higher angular velocities. The tight cap causes the aircraft orientation to lag behind, creating visible "wobble" as it slowly converges.

### Supporting Factor: Interpolation Delay Was Insufficient

**File:** `src/Networking/InterpolationBuffer.cs`, line 55

`InterpolationDelay = 0.15f` (150ms) provides a buffer of ~13 snapshots at 90Hz. While adequate for LAN, any real-world network jitter or packet loss easily exhausts this buffer. Increasing to 200ms reduces buffer underruns significantly.

## Affected Files

1. `src/Networking/InterpolationBuffer.cs` — Add velocity-based extrapolation when buffer runs dry; increase default interpolation delay
2. `src/Networking/RemoteAircraftManager.cs` — Remove velocity and angular velocity clamping

## Implementation Plan

All four changes below have already been applied in commit `9189385`.

### Change 1: Add velocity-based extrapolation in InterpolationBuffer.cs (lines 274-288)

**Old code:**
```csharp
else if (before.IsValid)
{
    // Only have "before" — hold position (no extrapolation)
    rawAbsolutePosition = before.AbsolutePosition;
    rawRotation = before.Rotation;
}
```

**New code (applied):**
```csharp
else if (before.IsValid)
{
    // Extrapolate using velocity from the last known snapshot
    // This prevents freeze-then-jump jitter when the buffer runs dry
    float extrapolateTime = (float)(remoteRenderTime - before.RemoteTime);
    // Cap extrapolation to prevent runaway (max 0.25s beyond last known data)
    extrapolateTime = Mathf.Min(extrapolateTime, 0.25f);
    
    rawAbsolutePosition = new Vector3d(
        before.AbsolutePosition.x + before.Velocity.x * extrapolateTime,
        before.AbsolutePosition.y + before.Velocity.y * extrapolateTime,
        before.AbsolutePosition.z + before.Velocity.z * extrapolateTime);
    rawRotation = before.Rotation;
    isExtrapolating = true;
}
```

This continues the plane along its last known velocity vector rather than freezing, which eliminates the freeze→jump jitter pattern. The 0.25s cap prevents divergence during prolonged packet loss.

**Reviewer note:** The `extrapolateTime` is guaranteed non-negative by the snapshot search logic (line 170: `snap.RemoteTime <= remoteRenderTime`), but adding a `Mathf.Max(0f, extrapolateTime)` clamp would be defensive. Not strictly required — the search guarantees `before.RemoteTime <= remoteRenderTime`.

### Change 2: Increase default interpolation delay (line 55)

**Old:** `public float InterpolationDelay { get; set; } = 0.15f;`
**New (applied):** `public float InterpolationDelay { get; set; } = 0.20f;`

A 200ms buffer (vs 150ms) provides ~18 snapshots at 90Hz, giving more headroom for jitter without noticeably increasing perceived latency.

### Change 3: Remove velocity clamping in VelocitySteerForState (lines 436-439)

Removed the `maxReasonableSpeed` clamping block entirely, replacing with a comment explaining why no clamping is needed.

The velocity is purely a means to reach a target position by next FixedUpdate. The interpolation buffer already ensures smooth, bounded target positions. Clamping the corrective velocity only prevents the rigidbody from arriving where it should be.

### Change 4: Remove angular velocity clamping (lines 456-458)

Removed the `maxReasonableAngSpeed` clamping block, replacing with a comment.

Same reasoning as linear velocity. The angular velocity is computed to reach the target rotation in one step. Clamping causes rotational lag and wobble.

## Risks & Edge Cases

1. **Extrapolation overshoot:** If a packet arrives after extrapolation has moved the plane forward, there will be a small correction when interpolation resumes. The velocity steering corrects this in a single FixedUpdate step (unclamped velocity reaches the target exactly). The correction size is bounded by `extrapolation_time × velocity_error`, where velocity_error is the difference between the last known velocity and the actual velocity during the gap. For typical flight, this is small.

2. **Removing velocity clamping could cause large instantaneous velocities:** If the interpolation target jumps significantly (e.g., FloatingOrigin shift not caught by teleport detection), the corrective velocity could spike. However, the teleport detection system (line 319 in InterpolationBuffer.cs: `delta > 10000f` i.e. >100m) and the `justTeleported` snap path (line 419 in RemoteAircraftManager.cs) handle this case. Additionally, the smooth render clock prevents sudden target jumps from clock offset updates.

3. **Increased interpolation delay adds ~50ms of perceived latency.** For a combat game this is a trade-off, but 200ms total is still well within acceptable range for this type of flight sim. The alternative (more frequent buffer underruns) is worse.

4. **Extrapolation during sustained packet loss:** If a player experiences packet loss >450ms (200ms buffer + 250ms extrapolation cap), the plane will freeze at the extrapolated position. This is a reasonable fallback — prolonged packet loss already degrades the experience regardless. When packets resume, the render clock will drift-correct (up to 5% per frame) and catch up over ~2 seconds.

5. **Rotation not extrapolated:** During extrapolation, rotation is held static (the last known rotation). For an aircraft turning at 30°/s, 250ms of extrapolation means ~7.5° of rotational error at most. The unclamped angular velocity steering corrects this in one FixedUpdate step when interpolation resumes. Full angular velocity extrapolation (quaternion integration) was considered but rejected as overly complex for marginal benefit.

6. **No blend on extrapolation→interpolation transition:** When switching from extrapolated to interpolated positions, there could be a small position discontinuity. Since velocity clamping is removed, the velocity steering corrects this immediately (in one physics step), so no explicit blending is needed. The `WasExtrapolating` field exists in RemoteAircraftState but is unused — it could be used for blending in the future if needed.

7. **Dead code:** `SmoothedPosition`, `SmoothedRotation`, `HasSmoothedPose`, and `WasExtrapolating` in RemoteAircraftState are declared but never read or written in the active code path. They appear to be remnants of a previous smoothing approach. Not harmful, but could be cleaned up in a future pass.

## Reviewer Notes

### What I verified:
- Confirmed all four proposed changes are already applied in commit `9189385`
- Traced the data flow from packet receipt → AddSnapshot → GetInterpolatedState → VelocitySteerForState to verify velocity data is correctly propagated
- Verified the snapshot search logic guarantees `before.RemoteTime <= remoteRenderTime`, making the extrapolation time non-negative
- Checked that teleport detection (>100m in local space) still works as a safety net
- Verified that `rb.drag = 0f` and `rb.angularDrag = 0f` are enforced every FixedUpdate (line 410-411), so the unclamped velocities aren't dampened by physics
- Confirmed `rb.interpolation = RigidbodyInterpolation.Interpolate` is enforced (line 408-409), ensuring smooth rendering between physics steps
- Verified `rb.useGravity = false` (line 407) prevents gravity from interfering with velocity steering

### What I changed in this analysis:
1. **Clarified that changes are already applied** — the original analysis read as a plan, but the code already has all changes
2. **Added risk #5 (rotation not extrapolated)** — important to document why rotation extrapolation was not included
3. **Added risk #6 (no blend on transition)** — documents why explicit blending isn't needed with unclamped steering
4. **Added risk #7 (dead code)** — flags unused fields for future cleanup
5. **Expanded risk #1** — more precise about the correction mechanism when extrapolation overshoots
6. **Expanded risk #4** — added specific timing for when freeze occurs and how recovery works
7. **Verified the extrapolateTime sign safety** — added reviewer note to Change 1 confirming it's guaranteed non-negative

### Overall assessment:
The original analysis correctly identified all three root causes and the supporting factor. The implementation plan is sound and minimal — each change addresses a specific jitter source without over-engineering. The changes work together: extrapolation prevents freeze→jump, removed clamping allows immediate correction of any remaining error, and increased buffer reduces the frequency of extrapolation needed. **The plan is ready for implementation (and is in fact already implemented).**
