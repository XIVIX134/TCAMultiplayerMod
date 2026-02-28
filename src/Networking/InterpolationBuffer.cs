using UnityEngine;
using TCAMultiplayer;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Network interpolation buffer with smooth render clock.
    /// 
    /// DESIGN:
    /// - Snapshots stored in ABSOLUTE double-precision coordinates (immune to FloatingOrigin)
    /// - A smooth "render clock" advances at real-time rate with slow drift correction
    ///   (the clock offset NEVER directly affects render time frame-to-frame)
    /// - Simple linear interpolation between two bracketing snapshots
    /// - Conversion to local Unity space happens only at the end
    /// 
    /// WHY SMOOTH RENDER CLOCK:
    /// The naive approach (remoteRenderTime = Time.time - ClockOffset - delay) causes
    /// the render time to JUMP every time ClockOffset updates (~90x/sec via EMA).
    /// At 250 m/s, a 0.001s jump = 0.25m of position jitter per packet.
    /// The render clock eliminates this by advancing smoothly and converging slowly.
    /// </summary>
    public class InterpolationBuffer
    {
        public struct Snapshot
        {
            public Vector3d AbsolutePosition;
            public Quaternion Rotation;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public float LocalTime;
            public float RemoteTime;
            public bool IsValid;
            
            public static Snapshot Invalid => new Snapshot { IsValid = false };
        }
        
        private readonly Snapshot[] _buffer;
        private int _writeIndex = 0;
        private int _count = 0;
        private readonly int _capacity;
        private bool _loggedFull = false;
        
        // --- Smooth render clock ---
        // Instead of computing remoteRenderTime = Time.time - ClockOffset - delay each frame
        // (which jumps when ClockOffset updates), we maintain a smooth clock that advances
        // at real-time rate and slowly converges to the target.
        private float _renderClock;
        private bool _renderClockInitialized = false;
        private Vector3 _lastLocalPosition; // For teleport detection
        private bool _hasLastPosition = false;
        
        /// <summary>
        /// How far behind real-time to render (in seconds).
        /// </summary>
        public float InterpolationDelay { get; set; } = 0.15f;
        
        /// <summary>
        /// Clock offset between local and remote time (local - remote).
        /// Set by RemoteAircraftManager from clock sync.
        /// Used ONLY to compute the render clock TARGET, never directly for render time.
        /// </summary>
        public float ClockOffset { get; set; } = 0f;
        
        public int Count => _count;
        public bool HasData => _count >= 1;
        
        public InterpolationBuffer(int capacity = NetworkConfig.INTERPOLATION_BUFFER_CAPACITY)
        {
            _capacity = capacity;
            _buffer = new Snapshot[capacity];
        }
        
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
        }
        
        /// <summary>
        /// Get interpolated state at the current smooth render time.
        /// Returns position in LOCAL Unity space.
        /// </summary>
        public (Vector3 position, Quaternion rotation, bool isExtrapolating, bool justTeleported) GetInterpolatedState()
        {
            if (_count == 0)
            {
                return (Vector3.zero, Quaternion.identity, false, false);
            }
            
            // ======================================================================
            // SMOOTH RENDER CLOCK
            // ======================================================================
            // Target = where we SHOULD be on the remote timeline right now.
            // Render clock = where we ACTUALLY render, advancing smoothly.
            //
            // The clock advances by exactly Time.deltaTime each frame (smooth),
            // plus a small bounded correction to converge toward the target.
            // ClockOffset changes from EMA updates have ZERO frame-to-frame effect.
            // ======================================================================
            float targetRenderTime = Time.time - ClockOffset - InterpolationDelay;
            
            if (!_renderClockInitialized)
            {
                _renderClock = targetRenderTime;
                _renderClockInitialized = true;
            }
            else
            {
                // Advance at real-time rate
                _renderClock += Time.deltaTime;
                
                // Compute drift from target
                float error = targetRenderTime - _renderClock;
                
                // Large error (>0.5s) = snap (initial sync, major clock correction, respawn)
                if (Mathf.Abs(error) > 0.5f)
                {
                    _renderClock = targetRenderTime;
                }
                else
                {
                    // Gentle correction: adjust rate by up to ±5% of deltaTime.
                    // This converges over several seconds without any per-frame jitter.
                    // At 0.05 * deltaTime, a 0.1s error takes ~2 seconds to correct.
                    float maxCorrection = 0.05f * Time.deltaTime;
                    float correction = Mathf.Clamp(error, -maxCorrection, maxCorrection);
                    _renderClock += correction;
                }
            }
            
            float remoteRenderTime = _renderClock;
            
            // ======================================================================
            // FIND 4 SNAPSHOTS for cubic Hermite (s0, s1=before, s2=after, s3)
            // s1/s2 bracket render time. s0/s3 provide neighbor tangent data.
            // ======================================================================
            Snapshot s0 = Snapshot.Invalid, s1 = Snapshot.Invalid;
            Snapshot s2 = Snapshot.Invalid, s3 = Snapshot.Invalid;

            // Pass 1: find s1 (latest before) and s2 (earliest after)
            for (int i = 0; i < _count; i++)
            {
                int idx = (_writeIndex - 1 - i + _capacity) % _capacity;
                var snap = _buffer[idx];
                if (!snap.IsValid) continue;
                if (snap.RemoteTime <= remoteRenderTime)
                {
                    if (!s1.IsValid || snap.RemoteTime > s1.RemoteTime) s1 = snap;
                }
                if (snap.RemoteTime >= remoteRenderTime)
                {
                    if (!s2.IsValid || snap.RemoteTime < s2.RemoteTime) s2 = snap;
                }
            }
            // Pass 2: find s0 (before s1) and s3 (after s2)
            if (s1.IsValid || s2.IsValid)
            {
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_writeIndex - 1 - i + _capacity) % _capacity;
                    var snap = _buffer[idx];
                    if (!snap.IsValid) continue;
                    if (s1.IsValid && snap.RemoteTime < s1.RemoteTime)
                    {
                        if (!s0.IsValid || snap.RemoteTime > s0.RemoteTime) s0 = snap;
                    }
                    if (s2.IsValid && snap.RemoteTime > s2.RemoteTime)
                    {
                        if (!s3.IsValid || snap.RemoteTime < s3.RemoteTime) s3 = snap;
                    }
                }
            }

            // Alias for case handling below
            Snapshot before = s1, after = s2;

            // ======================================================================
            // INTERPOLATE — Cubic Hermite when 4 snapshots available, linear fallback
            // Hermite smooths speed at segment boundaries (the actual jitter source).
            // Tangents derived from POSITIONS (finite differences), not packet velocity.
            // ======================================================================
            Vector3d rawAbsolutePosition;
            Quaternion rawRotation;
            bool isExtrapolating = false;

            if (s1.IsValid && s2.IsValid && s1.RemoteTime != s2.RemoteTime)
            {
                double duration = s2.RemoteTime - s1.RemoteTime;
                double u = (remoteRenderTime - s1.RemoteTime) / duration;
                u = System.Math.Max(0.0, System.Math.Min(1.0, u));

                if (s0.IsValid && s3.IsValid)
                {
                    // Cubic Hermite with position-derived tangents + clamp
                    double dt01 = s2.RemoteTime - s0.RemoteTime;
                    double dt13 = s3.RemoteTime - s1.RemoteTime;

                    double v1x = 0, v1y = 0, v1z = 0;
                    if (dt01 > 0.001)
                    {
                        v1x = (s2.AbsolutePosition.x - s0.AbsolutePosition.x) / dt01;
                        v1y = (s2.AbsolutePosition.y - s0.AbsolutePosition.y) / dt01;
                        v1z = (s2.AbsolutePosition.z - s0.AbsolutePosition.z) / dt01;
                    }
                    double v2x = 0, v2y = 0, v2z = 0;
                    if (dt13 > 0.001)
                    {
                        v2x = (s3.AbsolutePosition.x - s1.AbsolutePosition.x) / dt13;
                        v2y = (s3.AbsolutePosition.y - s1.AbsolutePosition.y) / dt13;
                        v2z = (s3.AbsolutePosition.z - s1.AbsolutePosition.z) / dt13;
                    }

                    double m1x = v1x * duration, m1y = v1y * duration, m1z = v1z * duration;
                    double m2x = v2x * duration, m2y = v2y * duration, m2z = v2z * duration;

                    // Tangent clamp (k=1.25 prevents overshoot)
                    double cx = s2.AbsolutePosition.x - s1.AbsolutePosition.x;
                    double cy = s2.AbsolutePosition.y - s1.AbsolutePosition.y;
                    double cz = s2.AbsolutePosition.z - s1.AbsolutePosition.z;
                    double segLen = System.Math.Sqrt(cx*cx + cy*cy + cz*cz);
                    if (segLen > 1e-6)
                    {
                        double maxD = 1.25 * segLen;
                        double l1 = System.Math.Sqrt(m1x*m1x+m1y*m1y+m1z*m1z);
                        if (l1 > maxD) { double s = maxD/l1; m1x*=s; m1y*=s; m1z*=s; }
                        double l2 = System.Math.Sqrt(m2x*m2x+m2y*m2y+m2z*m2z);
                        if (l2 > maxD) { double s = maxD/l2; m2x*=s; m2y*=s; m2z*=s; }
                    }

                    // Hermite basis
                    double u2 = u*u, u3 = u2*u;
                    double h00 = 2*u3-3*u2+1, h10 = u3-2*u2+u, h01 = -2*u3+3*u2, h11 = u3-u2;

                    rawAbsolutePosition = new Vector3d(
                        h00*s1.AbsolutePosition.x + h10*m1x + h01*s2.AbsolutePosition.x + h11*m2x,
                        h00*s1.AbsolutePosition.y + h10*m1y + h01*s2.AbsolutePosition.y + h11*m2y,
                        h00*s1.AbsolutePosition.z + h10*m1z + h01*s2.AbsolutePosition.z + h11*m2z);
                }
                else
                {
                    // Linear fallback when <4 snapshots
                    rawAbsolutePosition = new Vector3d(
                        s1.AbsolutePosition.x + (s2.AbsolutePosition.x - s1.AbsolutePosition.x) * u,
                        s1.AbsolutePosition.y + (s2.AbsolutePosition.y - s1.AbsolutePosition.y) * u,
                        s1.AbsolutePosition.z + (s2.AbsolutePosition.z - s1.AbsolutePosition.z) * u);
                }

                rawRotation = Quaternion.Slerp(s1.Rotation, s2.Rotation, (float)u);
            }
            else if (before.IsValid)
            {
                // Only have "before" — hold position (no extrapolation)
                rawAbsolutePosition = before.AbsolutePosition;
                rawRotation = before.Rotation;
            }
            else if (after.IsValid)
            {
                rawAbsolutePosition = after.AbsolutePosition;
                rawRotation = after.Rotation;
            }
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
            
            // ======================================================================
            // CONVERT TO LOCAL SPACE
            // ======================================================================
            Vector3 localPosition = FloatingOriginHelper.AbsoluteToLocal(rawAbsolutePosition);
            
            // Teleport detection (FloatingOrigin shift causes >100m jump in local space)
            bool justTeleported = false;
            if (_hasLastPosition)
            {
                float delta = (localPosition - _lastLocalPosition).sqrMagnitude;
                if (delta > 10000f) // > 100m
                    justTeleported = true;
            }
            _lastLocalPosition = localPosition;
            _hasLastPosition = true;
            
            return (localPosition, rawRotation, isExtrapolating, justTeleported);
        }
        
        public Snapshot GetNewestSnapshot()
        {
            if (_count == 0) return Snapshot.Invalid;
            int idx = (_writeIndex - 1 + _capacity) % _capacity;
            return _buffer[idx];
        }
        
        public Snapshot GetOldestSnapshot()
        {
            if (_count == 0) return Snapshot.Invalid;
            int idx = (_writeIndex - _count + _capacity) % _capacity;
            return _buffer[idx];
        }
        
        public void Clear()
        {
            _writeIndex = 0;
            _count = 0;
            _renderClockInitialized = false;
            _hasLastPosition = false;
            _loggedFull = false;
            for (int i = 0; i < _capacity; i++)
                _buffer[i] = Snapshot.Invalid;
        }
        
        public string GetDebugInfo()
        {
            if (_count == 0) return "Empty";
            var oldest = GetOldestSnapshot();
            var newest = GetNewestSnapshot();
            float localSpan = newest.LocalTime - oldest.LocalTime;
            float remoteSpan = newest.RemoteTime - oldest.RemoteTime;
            return $"Count:{_count} LocalSpan:{localSpan:F2}s RemoteSpan:{remoteSpan:F2}s ClkOff:{ClockOffset:F3}s RenderClk:{_renderClock:F3}";
        }
    }
}
