## Issue Summary

Remote planes exhibit visible jitter/stutter during gameplay. The user reports "jitter issues on remote planes" — the remote aircraft position and rotation appear to jump or stutter rather than moving smoothly.

## Root Cause Analysis

The jitter has **three interacting root causes** in the interpolation and velocity-steering pipeline:

### 1. No Extrapolation on Buffer Underrun (PRIMARY)

**File:** `src/Networking/InterpolationBuffer.cs`, lines 274-278

When the render clock advances past the newest available snapshot (i.e., only a `before` snapshot exists but no `after`), the code **holds the last known position**:

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

**File:** `src/Networking/RemoteAircraftManager.cs`, lines 438-444

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

**File:** `src/Networking/RemoteAircraftManager.cs`, lines 462-471

```csharp
float packetAngSpeed = newest.AngularVelocity.magnitude;
float maxReasonableAngSpeed = Mathf.Max(packetAngSpeed * 2f, packetAngSpeed + 1f);
```

During near-level flight, `packetAngSpeed` is very small (near zero), so the cap becomes ~1 rad/s. But rotational corrections after jitter can require higher angular velocities. The tight cap causes the aircraft orientation to lag behind, creating visible "wobble" as it slowly converges.

### Supporting Factor: Interpolation Delay May Be Insufficient

**File:** `src/Networking/InterpolationBuffer.cs`, line 55

`InterpolationDelay = 0.15f` (150ms) provides a buffer of ~13 snapshots at 90Hz. While adequate for LAN, any real-world network jitter or packet loss easily exhausts this buffer. Increasing to 200ms would reduce buffer underruns significantly.

## Affected Files

1. `src/Networking/InterpolationBuffer.cs` — Add velocity-based extrapolation when buffer runs dry; increase default interpolation delay
2. `src/Networking/RemoteAircraftManager.cs` — Remove or significantly loosen the velocity and angular velocity clamping

## Implementation Plan

### Change 1: Add velocity-based extrapolation in InterpolationBuffer.cs

**Old code (lines 274-278):**
```csharp
else if (before.IsValid)
{
    // Only have "before" — hold position (no extrapolation)
    rawAbsolutePosition = before.AbsolutePosition;
    rawRotation = before.Rotation;
}
```

**New code:**
```csharp
else if (before.IsValid)
{
    // Extrapolate using velocity from the nearest snapshot
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

### Change 2: Increase default interpolation delay

**File:** `src/Networking/InterpolationBuffer.cs`, line 55

**Old:** `public float InterpolationDelay { get; set; } = 0.15f;`
**New:** `public float InterpolationDelay { get; set; } = 0.20f;`

A 200ms buffer (vs 150ms) provides ~18 snapshots at 90Hz, giving more headroom for jitter without noticeably increasing perceived latency.

### Change 3: Remove velocity clamping in VelocitySteerForState

**File:** `src/Networking/RemoteAircraftManager.cs`, lines 436-444

**Old:**
```csharp
// Data-driven velocity bound: use the synced packet velocity magnitude as reference.
// The aircraft's actual speed comes from the game engine — no hardcoded limits.
float packetSpeed = state.LastVelocity.magnitude;
float steeringSpeed = rb.velocity.magnitude;
float maxReasonableSpeed = Mathf.Max(packetSpeed * 2f, packetSpeed + 10f);
if (maxReasonableSpeed > 0.1f && steeringSpeed > maxReasonableSpeed)
{
    rb.velocity = rb.velocity * (maxReasonableSpeed / steeringSpeed);
}
```

**New:**
```csharp
// No velocity clamping — the velocity-steering value is the exact velocity needed
// to reach the interpolated target in one FixedUpdate step. Clamping it prevents
// the rigidbody from reaching the target, causing accumulated error and visible jitter.
// The interpolation buffer already ensures smooth target positions.
```

The velocity is purely a means to reach a target position by next FixedUpdate. The interpolation buffer already ensures smooth, bounded target positions. Clamping the corrective velocity only prevents the rb from arriving where it should be.

### Change 4: Remove angular velocity clamping

**File:** `src/Networking/RemoteAircraftManager.cs`, lines 461-472

**Old:**
```csharp
// Data-driven angular velocity bound using packet data
var newest = state.Buffer.GetNewestSnapshot();
if (newest.IsValid)
{
    float packetAngSpeed = newest.AngularVelocity.magnitude;
    float steeringAngSpeed = rb.angularVelocity.magnitude;
    float maxReasonableAngSpeed = Mathf.Max(packetAngSpeed * 2f, packetAngSpeed + 1f);
    if (maxReasonableAngSpeed > 0.01f && steeringAngSpeed > maxReasonableAngSpeed)
    {
        rb.angularVelocity = rb.angularVelocity * (maxReasonableAngSpeed / steeringAngSpeed);
    }
}
```

**New:**
```csharp
// No angular velocity clamping — same reasoning as linear velocity.
// The angular velocity is computed to reach the target rotation in one step.
// Clamping causes rotational lag and wobble.
```

## Risks & Edge Cases

1. **Extrapolation overshoot:** If a packet arrives after extrapolation has moved the plane forward, there may be a small correction. The 0.25s cap and the smooth render clock mitigate this. The interpolation buffer's Hermite spline also handles the transition smoothly.

2. **Removing velocity clamping could cause large instantaneous velocities:** In theory, if the interpolation target jumps significantly (e.g., FloatingOrigin shift not caught by teleport detection), the velocity could spike. However, the teleport detection system (lines 305-311 in InterpolationBuffer.cs and line 419 in RemoteAircraftManager.cs) already handles this case by snapping. Additionally, the interpolation buffer's smooth render clock prevents sudden target jumps.

3. **Increased interpolation delay adds ~50ms of perceived latency.** For a combat game this is a trade-off, but 200ms total is still well within acceptable range for this type of flight sim.

4. **Extrapolation during packet loss:** If a player experiences sustained packet loss (>250ms), the extrapolation cap kicks in and the plane will freeze after 0.25s. This is a reasonable fallback — prolonged packet loss already degrades the experience regardless.
