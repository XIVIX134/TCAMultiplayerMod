using UnityEngine;
using TCAMultiplayer;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Stores timestamped network snapshots and provides smooth interpolation
    /// between past states using Hermite spline interpolation for smooth movement.
    /// Renders at a configurable delay behind real-time to ensure we always have
    /// two states to interpolate between.
    /// 
    /// IMPORTANT: Positions are stored in ABSOLUTE (double-precision) world coordinates
    /// to be immune to FloatingOrigin shifts. Conversion to local Unity space happens
    /// only at render time in GetInterpolatedState().
    /// </summary>
    public class InterpolationBuffer
    {
        /// <summary>
        /// A single snapshot of remote aircraft state
        /// </summary>
        public struct Snapshot
        {
            public Vector3d AbsolutePosition;   // Stored in absolute world coords (immune to FloatingOrigin)
            public Quaternion Rotation;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public float LocalTime;      // Time.time when received
            public float RemoteTime;     // Timestamp from sender
            public bool IsValid;
            
            public static Snapshot Invalid => new Snapshot { IsValid = false };
        }
        
        private readonly Snapshot[] _buffer;
        private int _writeIndex = 0;
        private int _count = 0;
        private readonly int _capacity;
        private bool _loggedFull = false;
        
        // Smoothing state - applies additional smoothing on top of interpolation
        // Stored in LOCAL space (recomputed each frame from absolute)
        private Vector3 _smoothedPosition;
        private Quaternion _smoothedRotation;
        private bool _smoothingInitialized = false;
        
        /// <summary>
        /// How far behind real-time to render (in seconds)
        /// Higher = smoother but more latency
        /// </summary>
        public float InterpolationDelay { get; set; } = 0.15f; 
        
        /// <summary>
        /// Maximum time to extrapolate beyond last known state
        /// </summary>
        public float MaxExtrapolationTime { get; set; } = 0.5f;
        
        /// <summary>
        /// Smoothing factor for additional position smoothing (0-1, lower = smoother)
        /// </summary>
        public float PositionSmoothingFactor { get; set; } = 0.25f;
        
        /// <summary>
        /// Smoothing factor for additional rotation smoothing (0-1, lower = smoother)
        /// </summary>
        public float RotationSmoothingFactor { get; set; } = 0.3f;
        
        /// <summary>
        /// Clock offset between local and remote time (local - remote).
        /// Set by RemoteAircraftManager from clock sync.
        /// Used to convert local time to remote time domain for interpolation.
        /// </summary>
        public float ClockOffset { get; set; } = 0f;
        
        /// <summary>
        /// Number of snapshots currently in buffer
        /// </summary>
        public int Count => _count;
        
        /// <summary>
        /// Whether we have enough data to interpolate
        /// </summary>
        public bool HasData => _count >= 1;
        
        public InterpolationBuffer(int capacity = NetworkConfig.INTERPOLATION_BUFFER_CAPACITY)
        {
            _capacity = capacity;
            _buffer = new Snapshot[capacity];
        }
        
        /// <summary>
        /// Add a new snapshot to the buffer.
        /// Position must be in ABSOLUTE world coordinates (not local Unity space).
        /// </summary>
        public void AddSnapshot(Vector3d absolutePosition, Quaternion rotation, Vector3 velocity, 
            Vector3 angularVelocity, float remoteTimestamp)
        {
            var snapshot = new Snapshot
            {
                AbsolutePosition = absolutePosition,
                Rotation = rotation,
                Velocity = velocity,
                AngularVelocity = angularVelocity,
                LocalTime = Time.time,
                RemoteTime = remoteTimestamp,
                IsValid = true
            };
            
            _buffer[_writeIndex] = snapshot;
            _writeIndex = (_writeIndex + 1) % _capacity;
            _count = Mathf.Min(_count + 1, _capacity);
            
            if (_count == 1)
            {
                LogHelper.Info(LogCategory.Interpolation, "[InterpolationBuffer] First snapshot received");
            }
            
            if (!_loggedFull && _count == _capacity)
            {
                _loggedFull = true;
                LogHelper.Info(LogCategory.Interpolation, $"[InterpolationBuffer] Buffer full ({_capacity}), overwriting oldest");
            }
            
            // Initialize smoothing if this is the first snapshot
            if (!_smoothingInitialized)
            {
                _smoothedPosition = FloatingOriginHelper.AbsoluteToLocal(absolutePosition);
                _smoothedRotation = rotation;
                _smoothingInitialized = true;
            }
        }
        
        /// <summary>
        /// Get interpolated state at the current render time (now - delay).
        /// Returns position in LOCAL Unity space (converted from absolute at render time).
        /// Uses RemoteTime (sender timestamps) for interpolation bracketing to
        /// eliminate jitter from variable network arrival times.
        /// Uses Hermite spline interpolation for smooth movement curves.
        /// </summary>
        public (Vector3 position, Quaternion rotation, bool isExtrapolating, bool justTeleported) GetInterpolatedState()
        {
            if (_count == 0)
            {
                return (Vector3.zero, Quaternion.identity, false, false);
            }
            
            // Convert current local time to remote time domain using clock offset
            // ClockOffset = local - remote, so remoteNow = localNow - ClockOffset
            float remoteRenderTime = Time.time - ClockOffset - InterpolationDelay;
            
            // Find the two snapshots surrounding remoteRenderTime using RemoteTime
            Snapshot before = Snapshot.Invalid;
            Snapshot after = Snapshot.Invalid;
            
            // Search through buffer for bracketing snapshots (using RemoteTime)
            for (int i = 0; i < _count; i++)
            {
                int idx = (_writeIndex - 1 - i + _capacity) % _capacity;
                var snap = _buffer[idx];
                
                if (!snap.IsValid) continue;
                
                if (snap.RemoteTime <= remoteRenderTime)
                {
                    if (!before.IsValid || snap.RemoteTime > before.RemoteTime)
                    {
                        before = snap;
                    }
                }
                
                if (snap.RemoteTime >= remoteRenderTime)
                {
                    if (!after.IsValid || snap.RemoteTime < after.RemoteTime)
                    {
                        after = snap;
                    }
                }
            }
            
            // Interpolation is done in ABSOLUTE space (doubles) then converted to local at the end.
            // This prevents FloatingOrigin shifts from causing position jumps.
            Vector3d rawAbsolutePosition;
            Quaternion rawRotation;
            bool isExtrapolating = false;
            
            // Case 1: We have both before and after - use Linear interpolation
            if (before.IsValid && after.IsValid && before.RemoteTime != after.RemoteTime)
            {
                float duration = after.RemoteTime - before.RemoteTime;
                float t = (remoteRenderTime - before.RemoteTime) / duration;
                t = Mathf.Clamp01(t);
                
                // Use precise linear interpolation for position (in absolute space)
                // Hermite spline is too sensitive to noisy physics velocity
                rawAbsolutePosition = new Vector3d(
                    before.AbsolutePosition.x + (after.AbsolutePosition.x - before.AbsolutePosition.x) * t,
                    before.AbsolutePosition.y + (after.AbsolutePosition.y - before.AbsolutePosition.y) * t,
                    before.AbsolutePosition.z + (after.AbsolutePosition.z - before.AbsolutePosition.z) * t
                );
                
                // Use standard Slerp for rotation
                rawRotation = Quaternion.Slerp(before.Rotation, after.Rotation, t);
            }
            // Case 2: Only have before (most common when delay is working) - extrapolate
            else if (before.IsValid)
            {
                float timeSince = remoteRenderTime - before.RemoteTime;
                isExtrapolating = timeSince > 0.01f;
                
                // Limit extrapolation time
                if (timeSince > MaxExtrapolationTime)
                {
                    timeSince = MaxExtrapolationTime;
                }
                
                // Extrapolate position using velocity with damping (in absolute space)
                float dampFactor = 1f - Mathf.Clamp01(timeSince / MaxExtrapolationTime) * 0.3f;
                rawAbsolutePosition = new Vector3d(
                    before.AbsolutePosition.x + before.Velocity.x * timeSince * dampFactor,
                    before.AbsolutePosition.y + before.Velocity.y * timeSince * dampFactor,
                    before.AbsolutePosition.z + before.Velocity.z * timeSince * dampFactor);
                
                // Extrapolate rotation using angular velocity
                Vector3 angularDelta = before.AngularVelocity * timeSince * Mathf.Rad2Deg;
                rawRotation = before.Rotation * Quaternion.Euler(angularDelta);
            }
            // Case 3: Only have after (shouldn't happen normally)
            else if (after.IsValid)
            {
                rawAbsolutePosition = after.AbsolutePosition;
                rawRotation = after.Rotation;
            }
            // Fallback - get newest snapshot
            else
            {
                var newest = GetNewestSnapshot();
                if (newest.IsValid)
                {
                    rawAbsolutePosition = newest.AbsolutePosition;
                    rawRotation = newest.Rotation;
                    isExtrapolating = true;
                }
                else
                {
                    return (Vector3.zero, Quaternion.identity, false, false);
                }
            }
            
            // Convert absolute position to local Unity space using CURRENT FloatingOrigin offset.
            // This is the key fix: all snapshots store absolute coords, and we convert here
            // using the latest offset, so FloatingOrigin shifts don't cause jumps.
            Vector3 rawLocalPosition = FloatingOriginHelper.AbsoluteToLocal(rawAbsolutePosition);
            
            bool justTeleported = false;

            // Detect FloatingOrigin shift: if the raw local position jumped far from the
            // previous frame's position, the origin shifted. 
            float posDelta = (rawLocalPosition - _smoothedPosition).sqrMagnitude;
            if (posDelta > 10000f) // > 100m means FloatingOrigin shifted
            {
                justTeleported = true;
            }
            
            // WE MUST NOT apply secondary EMA smoothing (Lerp) to fast-moving objects.
            // A plane moving at 200m/s will permanently lag 10+ meters behind the true interpolation
            // if we Lerp its absolute position, and variable framerates will cause it to jitter violently!
            // Hermite/Linear interpolation already gives us perfectly smooth continuous coordinates.
            _smoothedPosition = rawLocalPosition;
            _smoothedRotation = rawRotation;
            
            return (_smoothedPosition, _smoothedRotation, isExtrapolating, justTeleported);
        }
        
        /// <summary>
        /// Hermite spline interpolation using absolute positions (doubles) and velocities.
        /// Creates smooth curves that respect velocity at endpoints.
        /// </summary>
        private Vector3d HermiteInterpolateAbsolute(Vector3d p0, Vector3 v0, Vector3d p1, Vector3 v1, float duration, float t)
        {
            // Scale velocities by duration for proper interpolation
            double m0x = v0.x * duration, m0y = v0.y * duration, m0z = v0.z * duration;
            double m1x = v1.x * duration, m1y = v1.y * duration, m1z = v1.z * duration;
            
            // Hermite basis functions
            double t2 = t * (double)t;
            double t3 = t2 * t;
            
            double h00 = 2.0 * t3 - 3.0 * t2 + 1.0;  // position at p0
            double h10 = t3 - 2.0 * t2 + t;            // tangent at p0
            double h01 = -2.0 * t3 + 3.0 * t2;         // position at p1
            double h11 = t3 - t2;                        // tangent at p1
            
            return new Vector3d(
                h00 * p0.x + h10 * m0x + h01 * p1.x + h11 * m1x,
                h00 * p0.y + h10 * m0y + h01 * p1.y + h11 * m1y,
                h00 * p0.z + h10 * m0z + h01 * p1.z + h11 * m1z);
        }
        
        /// <summary>
        /// Smooth rotation interpolation that considers angular velocity
        /// </summary>
        private Quaternion SmoothSlerpRotation(Quaternion q0, Quaternion q1, 
            Vector3 angVel0, Vector3 angVel1, float duration, float t)
        {
            // For small angular velocities, just use slerp
            if (angVel0.sqrMagnitude < 0.01f && angVel1.sqrMagnitude < 0.01f)
            {
                return Quaternion.Slerp(q0, q1, SmoothStep(t));
            }
            
            // Use smoothstep for eased interpolation
            float smoothT = SmoothStep(t);
            return Quaternion.Slerp(q0, q1, smoothT);
        }
        
        /// <summary>
        /// Smoothstep function for eased transitions
        /// </summary>
        private float SmoothStep(float t)
        {
            // Hermite smoothstep: 3t² - 2t³
            return t * t * (3f - 2f * t);
        }
        
        /// <summary>
        /// Get the most recent snapshot
        /// </summary>
        public Snapshot GetNewestSnapshot()
        {
            if (_count == 0) return Snapshot.Invalid;
            
            int idx = (_writeIndex - 1 + _capacity) % _capacity;
            return _buffer[idx];
        }
        
        /// <summary>
        /// Get the oldest snapshot in buffer
        /// </summary>
        public Snapshot GetOldestSnapshot()
        {
            if (_count == 0) return Snapshot.Invalid;
            
            int idx = (_writeIndex - _count + _capacity) % _capacity;
            return _buffer[idx];
        }
        
        /// <summary>
        /// Clear all snapshots
        /// </summary>
        public void Clear()
        {
            _writeIndex = 0;
            _count = 0;
            _smoothingInitialized = false;
            _loggedFull = false;
            for (int i = 0; i < _capacity; i++)
            {
                _buffer[i] = Snapshot.Invalid;
            }
        }
        
        /// <summary>
        /// Get debug info about buffer state
        /// </summary>
        public string GetDebugInfo()
        {
            if (_count == 0) return "Empty";
            
            var oldest = GetOldestSnapshot();
            var newest = GetNewestSnapshot();
            float localSpan = newest.LocalTime - oldest.LocalTime;
            float remoteSpan = newest.RemoteTime - oldest.RemoteTime;
            
            return $"Count:{_count} LocalSpan:{localSpan:F2}s RemoteSpan:{remoteSpan:F2}s ClkOff:{ClockOffset:F3}s";
        }
    }
}
