using UnityEngine;
using TCAMultiplayer;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Stores timestamped network snapshots and provides smooth interpolation
    /// between past states using Hermite spline interpolation for smooth movement.
    /// Renders at a configurable delay behind real-time to ensure we always have
    /// two states to interpolate between.
    /// </summary>
    public class InterpolationBuffer
    {
        /// <summary>
        /// A single snapshot of remote aircraft state
        /// </summary>
        public struct Snapshot
        {
            public Vector3 Position;
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
        private Vector3 _smoothedPosition;
        private Quaternion _smoothedRotation;
        private bool _smoothingInitialized = false;
        
        /// <summary>
        /// How far behind real-time to render (in seconds)
        /// Higher = smoother but more latency
        /// </summary>
        public float InterpolationDelay { get; set; } = 0.1f; // 100ms default
        
        /// <summary>
        /// Maximum time to extrapolate beyond last known state
        /// </summary>
        public float MaxExtrapolationTime { get; set; } = 0.25f; // 250ms
        
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
        /// Add a new snapshot to the buffer
        /// </summary>
        public void AddSnapshot(Vector3 position, Quaternion rotation, Vector3 velocity, 
            Vector3 angularVelocity, float remoteTimestamp)
        {
            var snapshot = new Snapshot
            {
                Position = position,
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
                _smoothedPosition = position;
                _smoothedRotation = rotation;
                _smoothingInitialized = true;
            }
        }
        
        /// <summary>
        /// Get interpolated state at the current render time (now - delay)
        /// Uses RemoteTime (sender timestamps) for interpolation bracketing to
        /// eliminate jitter from variable network arrival times.
        /// Uses Hermite spline interpolation for smooth movement curves.
        /// </summary>
        public (Vector3 position, Quaternion rotation, bool isExtrapolating) GetInterpolatedState()
        {
            if (_count == 0)
            {
                return (Vector3.zero, Quaternion.identity, false);
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
            
            Vector3 rawPosition;
            Quaternion rawRotation;
            bool isExtrapolating = false;
            
            // Case 1: We have both before and after - use Hermite interpolation
            if (before.IsValid && after.IsValid && before.RemoteTime != after.RemoteTime)
            {
                float duration = after.RemoteTime - before.RemoteTime;
                float t = (remoteRenderTime - before.RemoteTime) / duration;
                t = Mathf.Clamp01(t);
                
                // Use Hermite spline interpolation for position
                rawPosition = HermiteInterpolate(
                    before.Position, before.Velocity,
                    after.Position, after.Velocity,
                    duration, t);
                
                // Use smooth squad interpolation for rotation
                rawRotation = SmoothSlerpRotation(before.Rotation, after.Rotation, 
                    before.AngularVelocity, after.AngularVelocity,
                    duration, t);
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
                
                // Extrapolate position using velocity with damping
                // Apply slight damping to prevent runaway extrapolation
                float dampFactor = 1f - Mathf.Clamp01(timeSince / MaxExtrapolationTime) * 0.3f;
                rawPosition = before.Position + before.Velocity * timeSince * dampFactor;
                
                // Extrapolate rotation using angular velocity
                Vector3 angularDelta = before.AngularVelocity * timeSince * Mathf.Rad2Deg;
                rawRotation = before.Rotation * Quaternion.Euler(angularDelta);
            }
            // Case 3: Only have after (shouldn't happen normally)
            else if (after.IsValid)
            {
                rawPosition = after.Position;
                rawRotation = after.Rotation;
            }
            // Fallback - get newest snapshot
            else
            {
                var newest = GetNewestSnapshot();
                if (newest.IsValid)
                {
                    rawPosition = newest.Position;
                    rawRotation = newest.Rotation;
                    isExtrapolating = true;
                }
                else
                {
                    return (Vector3.zero, Quaternion.identity, false);
                }
            }
            
            // Apply additional smoothing layer to reduce micro-jitter
            // Use frame-rate independent smoothing
            float dt = Time.deltaTime;
            float posSmoothT = 1f - Mathf.Pow(1f - PositionSmoothingFactor, dt * 60f);
            float rotSmoothT = 1f - Mathf.Pow(1f - RotationSmoothingFactor, dt * 60f);
            
            _smoothedPosition = Vector3.Lerp(_smoothedPosition, rawPosition, posSmoothT);
            _smoothedRotation = Quaternion.Slerp(_smoothedRotation, rawRotation, rotSmoothT);
            
            return (_smoothedPosition, _smoothedRotation, isExtrapolating);
        }
        
        /// <summary>
        /// Hermite spline interpolation using positions and velocities
        /// Creates smooth curves that respect velocity at endpoints
        /// </summary>
        private Vector3 HermiteInterpolate(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float duration, float t)
        {
            // Scale velocities by duration for proper interpolation
            Vector3 m0 = v0 * duration;
            Vector3 m1 = v1 * duration;
            
            // Hermite basis functions
            float t2 = t * t;
            float t3 = t2 * t;
            
            float h00 = 2f * t3 - 3f * t2 + 1f;  // position at p0
            float h10 = t3 - 2f * t2 + t;         // tangent at p0
            float h01 = -2f * t3 + 3f * t2;       // position at p1
            float h11 = t3 - t2;                   // tangent at p1
            
            return h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
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
