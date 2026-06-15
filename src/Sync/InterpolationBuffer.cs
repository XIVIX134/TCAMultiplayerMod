using System;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Sync
{
    public enum InterpolationMode
    {
        Empty,
        SingleExtrapolate,
        Interpolate,
        Extrapolate,
        Hold,
        Fallback
    }

    public struct InterpolationBufferStats
    {
        public int Count;
        public float RemoteSpan;
        public float LocalSpan;
        public float OldestRemoteTime;
        public float NewestRemoteTime;
        public float OldestLocalReceiveTime;
        public float NewestLocalReceiveTime;
        public float MaxRemoteGap;
        public float MaxLocalGap;
    }

    /// <summary>
    /// Network state sample stored in absolute (double-precision) coordinates.
    /// Conversion to local Unity space happens at render time via FloatingOriginService.
    /// </summary>
    public struct InterpolationSample
    {
        public double PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public float VelX, VelY, VelZ;
        public float AngVelX, AngVelY, AngVelZ;
        public float Throttle, Pitch, Roll, Yaw;
        public float NozzleAngle, SpeedKIAS, BrakeState;
        public byte Flags;
        public float RemoteTimestamp;
        public float LocalReceiveTime;
    }

    /// <summary>
    /// Interpolation buffer with snapshot interpolation and a smooth render clock.
    ///
    /// DESIGN:
    /// - Samples stored in ABSOLUTE double-precision coordinates (immune to FloatingOrigin).
    /// - Sampled from FixedUpdate with Time.fixedTime: TCA renders its world on the physics
    ///   timeline (cameras + native aircraft only move on physics steps), so the remote pose
    ///   must step on that same timeline or it appears to oscillate relative to the camera.
    /// - Linear position interpolation between bracketing samples. Fast aircraft expose
    ///   tiny snapshot noise, and cubic curves can turn that into visible micro-wobble.
    /// - Slerp for quaternion rotation.
    /// - Conversion to local Unity space happens ONLY at the caller (FloatingOriginService).
    /// - Pre-allocated circular buffer — zero GC allocations on the hot path.
    ///
    /// RENDER CLOCK:
    /// The clock follows the rate at which remote data actually ARRIVES, via a PI controller
    /// on the buffered lead (newest remote timestamp - render clock). Sender and receiver
    /// game clocks routinely run at different rates — a background-throttled instance runs
    /// its whole game (and therefore its send timestamps) in slow motion — so any scheme
    /// that models a fixed clock offset will starve or overrun the buffer. Tracking the
    /// data rate keeps the lead pinned at the interpolation delay regardless:
    /// - Integral term: learns sustained rate mismatch (throttling, slow-motion physics,
    ///   clock drift) so steady-state error is zero.
    /// - Proportional term: recovers transient lead changes, active only outside a deadband
    ///   that hides per-frame packet-burst sawtooth (inside it the clock advances at exactly
    ///   the local rate → perfectly smooth motion).
    /// </summary>
    public class InterpolationBuffer
    {
        private const float MaxExtrapolationSeconds = 0.10f;
        private const float LeadErrorDeadbandSeconds = 0.040f;
        private const float ProportionalGain = 1.0f;
        private const float MaxProportionalRate = 0.10f;
        private const float IntegralGain = 0.5f;
        private const float MaxIntegralRate = 0.30f;
        private const float SnapErrorSeconds = 1.0f;
        private const float OffsetWindowSeconds = 3f;

        private readonly InterpolationSample[] _buffer; // circular buffer
        private int _head;
        private int _count;
        private readonly int _capacity;
        private float _interpolationDelay;

        // Clock offset tracking (sliding-window minimum, two-bucket technique).
        // Diagnostic only — the render clock no longer depends on it.
        private float _clockOffset;
        private bool _clockInitialized;
        private float _offsetWindowMinA;
        private float _offsetWindowMinB;
        private float _offsetWindowStartTime;

        private int _droppedOutOfOrderSamples;
        private int _droppedInvalidSamples;

        // Smooth render clock (PI rate controller on buffered lead)
        private float _renderClock;
        private bool _renderClockInitialized;
        private float _lastLocalTime;
        private float _rateIntegral;

        // Last known state for empty-buffer edge case
        private InterpolationSample _lastKnownSample;
        private bool _hasLastKnownSample;

        // Warning throttle (one-shot logs, not per-frame)
        private bool _loggedEmpty;
        private bool _loggedFull;

        public int Count => _count;
        public float ClockOffset => _clockOffset;
        public InterpolationMode LastMode { get; private set; }
        public float LastRenderTime { get; private set; }
        public float LastExtrapolationSeconds { get; private set; }
        public float LastTargetRenderTime { get; private set; }
        public float LastRenderClockDelta { get; private set; }
        public float LastRenderClockError { get; private set; }
        public float LastRenderClockCorrection { get; private set; }
        public float LastRenderClockRate { get; private set; } = 1f;
        public bool LastRenderClockLargeError { get; private set; }
        public bool LastRenderClockClampedBackward { get; private set; }
        public float LastEffectiveInterpolationDelay => _interpolationDelay;
        public float LastBufferedLeadSeconds { get; private set; }
        public int DroppedOutOfOrderSamples => _droppedOutOfOrderSamples;
        public int DroppedInvalidSamples => _droppedInvalidSamples;

        public float InterpolationDelay
        {
            get => _interpolationDelay;
            set => _interpolationDelay = value;
        }

        public InterpolationBuffer(int capacity = 120, float interpolationDelay = 0.20f)
        {
            _capacity = Math.Max(4, capacity);
            _interpolationDelay = interpolationDelay;
            _buffer = new InterpolationSample[_capacity];
        }

        // ════════════════════════════════════════════════════════════════
        //  Public API
        // ════════════════════════════════════════════════════════════════

        public void AddSample(InterpolationSample sample)
        {
            if (!IsValidSample(sample))
            {
                _droppedInvalidSamples++;
                return;
            }

            if (_count > 0)
            {
                int newestIdx = (_head - 1 + _capacity) % _capacity;
                float newestRemoteTime = _buffer[newestIdx].RemoteTimestamp;
                if (sample.RemoteTimestamp <= newestRemoteTime + 0.00001f)
                {
                    _droppedOutOfOrderSamples++;
                    return;
                }

            }

            // Update clock-offset diagnostic (not used by the render clock)
            UpdateClockOffset(sample.LocalReceiveTime, sample.RemoteTimestamp);

            // Write to circular buffer
            _buffer[_head] = sample;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;

            // Track last known for empty-buffer fallback
            _lastKnownSample = sample;
            _hasLastKnownSample = true;
            _loggedEmpty = false;

            if (!_loggedFull && _count == _capacity)
            {
                _loggedFull = true;
                Log.Debug("INTERP", $"Buffer full ({_capacity}), overwriting oldest");
            }
        }

        /// <summary>
        /// Get the interpolated state at the given local time.
        /// Returns an InterpolationSample in ABSOLUTE coordinates — caller converts to local space.
        /// Zero-allocation on the normal hot path.
        /// </summary>
        public InterpolationSample GetInterpolatedState(float localTime)
        {
            // ── Edge case: empty buffer ──
            if (_count == 0)
            {
                LastMode = InterpolationMode.Empty;
                LastExtrapolationSeconds = 0f;
                if (!_loggedEmpty)
                {
                    _loggedEmpty = true;
                    Log.Warning("INTERP", "Buffer empty, returning last known or identity state");
                }
                if (_hasLastKnownSample) return _lastKnownSample;
                // Return identity state (RotW=1 for valid quaternion)
                var empty = default(InterpolationSample);
                empty.RotW = 1f;
                return empty;
            }

            // ── Smooth render clock ──
            float remoteRenderTime = ComputeRenderTime(localTime);
            LastRenderTime = remoteRenderTime;
            LastExtrapolationSeconds = 0f;

            // ── Edge case: single sample — extrapolate from velocity ──
            if (_count == 1)
            {
                int idx = (_head - 1 + _capacity) % _capacity;
                LastMode = InterpolationMode.SingleExtrapolate;
                LastExtrapolationSeconds = Math.Max(0f, Math.Min(remoteRenderTime - _buffer[idx].RemoteTimestamp, MaxExtrapolationSeconds));
                return ExtrapolateFromSingle(ref _buffer[idx], remoteRenderTime);
            }

            // ── Find bracketing samples ──
            // s0, s1(before), s2(after), s3 — indices into _buffer, -1 if not found
            int i0 = -1, i1 = -1, i2 = -1, i3 = -1;
            FindBracketingSamples(remoteRenderTime, ref i0, ref i1, ref i2, ref i3);

            // ── Interpolate ──
            if (i1 >= 0 && i2 >= 0)
            {
                float duration = _buffer[i2].RemoteTimestamp - _buffer[i1].RemoteTimestamp;

                // Division-by-zero protection (near-identical timestamps)
                if (duration < 0.0001f)
                    return _buffer[i2];

                double u = (remoteRenderTime - _buffer[i1].RemoteTimestamp) / duration;
                u = Math.Max(0.0, Math.Min(1.0, u));

                LastMode = InterpolationMode.Interpolate;
                return LinearInterpolate(i1, i2, u);
            }
            else if (i1 >= 0)
            {
                // Render time is just newer than the newest received sample.
                // Short, clamped extrapolation avoids visible hold-then-jump stutter
                // during normal packet jitter; long gaps still hold to avoid runaway.
                LastMode = InterpolationMode.Extrapolate;
                LastExtrapolationSeconds = Math.Max(0f, Math.Min(remoteRenderTime - _buffer[i1].RemoteTimestamp, MaxExtrapolationSeconds));
                return ExtrapolateFromSample(ref _buffer[i1], remoteRenderTime);
            }
            else if (i2 >= 0)
            {
                // Only have "after" — hold position
                LastMode = InterpolationMode.Hold;
                return _buffer[i2];
            }

            // Fallback: newest sample
            LastMode = InterpolationMode.Fallback;
            return _buffer[(_head - 1 + _capacity) % _capacity];
        }

        public void Clear()
        {
            _head = 0;
            _count = 0;
            _clockInitialized = false;
            _offsetWindowMinA = 0f;
            _offsetWindowMinB = 0f;
            _offsetWindowStartTime = 0f;
            _renderClockInitialized = false;
            _hasLastKnownSample = false;
            _loggedEmpty = false;
            _loggedFull = false;
            _lastLocalTime = 0f;
            _rateIntegral = 0f;
            LastTargetRenderTime = 0f;
            LastRenderClockDelta = 0f;
            LastRenderClockError = 0f;
            LastRenderClockCorrection = 0f;
            LastRenderClockRate = 1f;
            LastRenderClockLargeError = false;
            LastRenderClockClampedBackward = false;
            LastBufferedLeadSeconds = 0f;
            _droppedOutOfOrderSamples = 0;
            _droppedInvalidSamples = 0;
            // No need to zero the array — _count=0 means nothing is read
        }

        public string GetDebugInfo()
        {
            if (_count == 0) return "Empty";
            int oldIdx = (_head - _count + _capacity) % _capacity;
            int newIdx = (_head - 1 + _capacity) % _capacity;
            float remoteSpan = _buffer[newIdx].RemoteTimestamp - _buffer[oldIdx].RemoteTimestamp;
            float localSpan = _buffer[newIdx].LocalReceiveTime - _buffer[oldIdx].LocalReceiveTime;
            return $"Count:{_count} RemoteSpan:{remoteSpan:F2}s LocalSpan:{localSpan:F2}s " +
                   $"Delay:{LastEffectiveInterpolationDelay:F3}s Lead:{LastBufferedLeadSeconds:F3}s " +
                   $"ClkOff:{_clockOffset:F3}s RenderClk:{_renderClock:F3}";
        }

        public bool TryGetSampleStats(out int count, out float remoteSpan, out float localSpan)
        {
            count = _count;
            remoteSpan = 0f;
            localSpan = 0f;
            if (_count == 0)
                return false;

            int oldIdx = (_head - _count + _capacity) % _capacity;
            int newIdx = (_head - 1 + _capacity) % _capacity;
            remoteSpan = _buffer[newIdx].RemoteTimestamp - _buffer[oldIdx].RemoteTimestamp;
            localSpan = _buffer[newIdx].LocalReceiveTime - _buffer[oldIdx].LocalReceiveTime;
            return true;
        }

        public bool TryGetDetailedStats(out InterpolationBufferStats stats)
        {
            stats = default(InterpolationBufferStats);
            stats.Count = _count;
            if (_count == 0)
                return false;

            int oldIdx = (_head - _count + _capacity) % _capacity;
            int newIdx = (_head - 1 + _capacity) % _capacity;
            stats.OldestRemoteTime = _buffer[oldIdx].RemoteTimestamp;
            stats.NewestRemoteTime = _buffer[newIdx].RemoteTimestamp;
            stats.OldestLocalReceiveTime = _buffer[oldIdx].LocalReceiveTime;
            stats.NewestLocalReceiveTime = _buffer[newIdx].LocalReceiveTime;
            stats.RemoteSpan = stats.NewestRemoteTime - stats.OldestRemoteTime;
            stats.LocalSpan = stats.NewestLocalReceiveTime - stats.OldestLocalReceiveTime;

            float previousRemote = stats.OldestRemoteTime;
            float previousLocal = stats.OldestLocalReceiveTime;
            for (int i = 1; i < _count; i++)
            {
                int idx = (oldIdx + i) % _capacity;
                float remoteGap = _buffer[idx].RemoteTimestamp - previousRemote;
                float localGap = _buffer[idx].LocalReceiveTime - previousLocal;
                if (remoteGap > stats.MaxRemoteGap) stats.MaxRemoteGap = remoteGap;
                if (localGap > stats.MaxLocalGap) stats.MaxLocalGap = localGap;
                previousRemote = _buffer[idx].RemoteTimestamp;
                previousLocal = _buffer[idx].LocalReceiveTime;
            }

            return true;
        }

        // ════════════════════════════════════════════════════════════════
        //  Sample validation and clock offset
        // ════════════════════════════════════════════════════════════════

        private static bool IsValidSample(InterpolationSample sample)
        {
            return IsFinite(sample.PosX) && IsFinite(sample.PosY) && IsFinite(sample.PosZ)
                && IsFinite(sample.RotX) && IsFinite(sample.RotY) && IsFinite(sample.RotZ) && IsFinite(sample.RotW)
                && IsFinite(sample.VelX) && IsFinite(sample.VelY) && IsFinite(sample.VelZ)
                && IsFinite(sample.AngVelX) && IsFinite(sample.AngVelY) && IsFinite(sample.AngVelZ)
                && IsFinite(sample.Throttle) && IsFinite(sample.Pitch) && IsFinite(sample.Roll) && IsFinite(sample.Yaw)
                && IsFinite(sample.NozzleAngle) && IsFinite(sample.SpeedKIAS) && IsFinite(sample.BrakeState)
                && IsFinite(sample.RemoteTimestamp) && IsFinite(sample.LocalReceiveTime);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void UpdateClockOffset(float localReceiveTime, float remoteTimestamp)
        {
            float offset = localReceiveTime - remoteTimestamp;

            if (!_clockInitialized)
            {
                _clockOffset = offset;
                _clockInitialized = true;
                _offsetWindowMinA = offset;
                _offsetWindowMinB = offset;
                _offsetWindowStartTime = localReceiveTime;
                return;
            }

            // Two-bucket sliding minimum: minA covers the current window, minB the previous
            // one, so the reported minimum always spans 1-2 windows of history. The minimum
            // tracks the fastest packet (least queuing + least frame-quantization noise) and
            // is stable frame-to-frame; per-packet noise never moves it.
            if (localReceiveTime - _offsetWindowStartTime > OffsetWindowSeconds)
            {
                _offsetWindowMinB = _offsetWindowMinA;
                _offsetWindowMinA = offset;
                _offsetWindowStartTime = localReceiveTime;
            }
            else if (offset < _offsetWindowMinA)
            {
                _offsetWindowMinA = offset;
            }

            _clockOffset = Math.Min(_offsetWindowMinA, _offsetWindowMinB);
        }

        // ════════════════════════════════════════════════════════════════
        //  Smooth render clock
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Advances the smooth render clock and returns the remote-timeline render time.
        /// PI rate controller: the clock advances at the local time rate scaled to track
        /// the rate at which remote data arrives, keeping the buffered lead pinned at the
        /// interpolation delay even when the sender's game clock runs slower or faster
        /// than ours (background throttling, slow-motion physics, clock drift).
        /// </summary>
        private float ComputeRenderTime(float localTime)
        {
            float newestRemoteTime = GetNewestRemoteTime();
            float targetRenderTime = newestRemoteTime - _interpolationDelay;
            LastTargetRenderTime = targetRenderTime;
            LastRenderClockDelta = 0f;
            LastRenderClockError = 0f;
            LastRenderClockCorrection = 0f;
            LastRenderClockRate = 1f;
            LastRenderClockLargeError = false;
            LastRenderClockClampedBackward = false;

            if (!_renderClockInitialized)
            {
                float offsetRenderTime = localTime - _clockOffset - _interpolationDelay;
                bool slightInitialUnderrun = offsetRenderTime > newestRemoteTime
                    && offsetRenderTime - newestRemoteTime <= MaxExtrapolationSeconds;

                _renderClock = slightInitialUnderrun ? offsetRenderTime : targetRenderTime;
                _renderClockInitialized = true;
                _lastLocalTime = localTime;
                _rateIntegral = 0f;
                LastBufferedLeadSeconds = newestRemoteTime - _renderClock;
                return _renderClock;
            }

            // Compute step delta from local time progression
            float deltaTime = localTime - _lastLocalTime;
            _lastLocalTime = localTime;

            // Time going backwards recovery
            if (deltaTime < 0f)
            {
                // Unity time should not run backwards in normal play, but if it
                // does, never rewind the remote render timeline. Rewinding a jet
                // is perceived as forward/back oscillation along velocity.
                LastBufferedLeadSeconds = newestRemoteTime - _renderClock;
                return _renderClock;
            }

            float previousRenderClock = _renderClock;
            float error = targetRenderTime - _renderClock;
            LastRenderClockError = error;
            LastRenderClockLargeError = Math.Abs(error) > SnapErrorSeconds;

            // Hard resync only for huge errors (level-load stall, long disconnect).
            // Forward only — never rewind the timeline.
            if (LastRenderClockLargeError)
            {
                if (error > 0f)
                {
                    _renderClock = targetRenderTime;
                    LastRenderClockCorrection = error;
                }
                _rateIntegral = 0f;
                LastBufferedLeadSeconds = newestRemoteTime - _renderClock;
                return _renderClock;
            }

            // Integral: learns sustained data-rate mismatch → zero steady-state error.
            // Always active; inside the deadband the error is tiny and self-cancelling.
            _rateIntegral += error * IntegralGain * deltaTime;
            _rateIntegral = Math.Max(-MaxIntegralRate, Math.Min(MaxIntegralRate, _rateIntegral));

            // Proportional: recovers transient lead changes. Only the excess beyond the
            // deadband contributes — the deadband hides the per-frame packet-burst
            // sawtooth so normal operation runs at exactly rate 1.0 (perfectly smooth).
            float proportional = 0f;
            if (Math.Abs(error) > LeadErrorDeadbandSeconds)
            {
                float excess = error > 0f
                    ? error - LeadErrorDeadbandSeconds
                    : error + LeadErrorDeadbandSeconds;
                proportional = Math.Max(-MaxProportionalRate,
                    Math.Min(MaxProportionalRate, excess * ProportionalGain));
            }

            float rate = 1f + _rateIntegral + proportional;
            if (rate < 0f) rate = 0f;
            _renderClock += deltaTime * rate;
            LastRenderClockRate = rate;
            LastRenderClockDelta = deltaTime * rate;
            LastRenderClockCorrection = deltaTime * (rate - 1f);

            if (_renderClock < previousRenderClock)
            {
                _renderClock = previousRenderClock;
                LastRenderClockClampedBackward = true;
            }

            LastBufferedLeadSeconds = newestRemoteTime - _renderClock;
            return _renderClock;
        }

        private float GetNewestRemoteTime()
        {
            if (_count == 0)
                return _renderClock;

            int newIdx = (_head - 1 + _capacity) % _capacity;
            return _buffer[newIdx].RemoteTimestamp;
        }

        // ════════════════════════════════════════════════════════════════
        //  Sample search (zero-allocation, two-pass scan)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Find samples bracketing renderTime:
        ///   s0 → s1(before) → [renderTime] → s2(after) → s3
        /// s1/s2 bracket the render time. s0/s3 are kept for diagnostics/future curve work.
        /// All indices are into _buffer; -1 means not found.
        /// </summary>
        private void FindBracketingSamples(float renderTime, ref int i0, ref int i1, ref int i2, ref int i3)
        {
            float bestBeforeTime = float.MinValue;
            float bestAfterTime = float.MaxValue;

            // Pass 1: find s1 (latest sample at or before renderTime)
            //         and s2 (earliest sample at or after renderTime)
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - 1 - i + _capacity) % _capacity;
                float t = _buffer[idx].RemoteTimestamp;

                if (t <= renderTime && t > bestBeforeTime)
                {
                    bestBeforeTime = t;
                    i1 = idx;
                }
                if (t >= renderTime && t < bestAfterTime)
                {
                    bestAfterTime = t;
                    i2 = idx;
                }
            }

            // Pass 2: find s0 (latest sample before s1)
            //         and s3 (earliest sample after s2)
            if (i1 < 0 && i2 < 0) return;

            float bestS0Time = float.MinValue;
            float bestS3Time = float.MaxValue;

            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - 1 - i + _capacity) % _capacity;
                float t = _buffer[idx].RemoteTimestamp;

                if (i1 >= 0 && t < bestBeforeTime && t > bestS0Time)
                {
                    bestS0Time = t;
                    i0 = idx;
                }
                if (i2 >= 0 && t > bestAfterTime && t < bestS3Time)
                {
                    bestS3Time = t;
                    i3 = idx;
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Linear interpolation fallback (< 4 samples available)
        // ════════════════════════════════════════════════════════════════

        private InterpolationSample LinearInterpolate(int i1, int i2, double u)
        {
            ref var s1 = ref _buffer[i1];
            ref var s2 = ref _buffer[i2];
            float t = (float)u;

            InterpolationSample result;

            // Position: linear
            result.PosX = s1.PosX + (s2.PosX - s1.PosX) * u;
            result.PosY = s1.PosY + (s2.PosY - s1.PosY) * u;
            result.PosZ = s1.PosZ + (s2.PosZ - s1.PosZ) * u;

            // Rotation: slerp
            SlerpRotation(ref s1, ref s2, t,
                out result.RotX, out result.RotY, out result.RotZ, out result.RotW);

            // Scalar fields: linear lerp
            result.VelX = s1.VelX + (s2.VelX - s1.VelX) * t;
            result.VelY = s1.VelY + (s2.VelY - s1.VelY) * t;
            result.VelZ = s1.VelZ + (s2.VelZ - s1.VelZ) * t;
            result.AngVelX = s1.AngVelX + (s2.AngVelX - s1.AngVelX) * t;
            result.AngVelY = s1.AngVelY + (s2.AngVelY - s1.AngVelY) * t;
            result.AngVelZ = s1.AngVelZ + (s2.AngVelZ - s1.AngVelZ) * t;
            result.Throttle = s1.Throttle + (s2.Throttle - s1.Throttle) * t;
            result.Pitch = s1.Pitch + (s2.Pitch - s1.Pitch) * t;
            result.Roll = s1.Roll + (s2.Roll - s1.Roll) * t;
            result.Yaw = s1.Yaw + (s2.Yaw - s1.Yaw) * t;
            result.NozzleAngle = s1.NozzleAngle + (s2.NozzleAngle - s1.NozzleAngle) * t;
            result.SpeedKIAS = s1.SpeedKIAS + (s2.SpeedKIAS - s1.SpeedKIAS) * t;
            result.BrakeState = s1.BrakeState + (s2.BrakeState - s1.BrakeState) * t;

            result.Flags = t < 0.5f ? s1.Flags : s2.Flags;
            result.RemoteTimestamp = s1.RemoteTimestamp + (s2.RemoteTimestamp - s1.RemoteTimestamp) * t;
            result.LocalReceiveTime = s2.LocalReceiveTime;

            return result;
        }

        // ════════════════════════════════════════════════════════════════
        //  Quaternion slerp (manual, zero-allocation)
        // ════════════════════════════════════════════════════════════════

        private static void SlerpRotation(
            ref InterpolationSample a, ref InterpolationSample b, float t,
            out float rx, out float ry, out float rz, out float rw)
        {
            float dot = a.RotX * b.RotX + a.RotY * b.RotY + a.RotZ * b.RotZ + a.RotW * b.RotW;

            // Ensure shortest path
            float bx = b.RotX, by = b.RotY, bz = b.RotZ, bw = b.RotW;
            if (dot < 0f)
            {
                dot = -dot;
                bx = -bx; by = -by; bz = -bz; bw = -bw;
            }

            float s0, s1;
            if (dot > 0.9995f)
            {
                // Nearly identical — normalized lerp (avoids sin(~0) instability)
                s0 = 1f - t;
                s1 = t;
            }
            else
            {
                float theta = (float)Math.Acos(dot);
                float sinTheta = (float)Math.Sin(theta);
                s0 = (float)Math.Sin((1f - t) * theta) / sinTheta;
                s1 = (float)Math.Sin(t * theta) / sinTheta;
            }

            rx = s0 * a.RotX + s1 * bx;
            ry = s0 * a.RotY + s1 * by;
            rz = s0 * a.RotZ + s1 * bz;
            rw = s0 * a.RotW + s1 * bw;

            // Normalize (safety against accumulated drift)
            float mag = (float)Math.Sqrt(rx * rx + ry * ry + rz * rz + rw * rw);
            if (mag > 0.00001f)
            {
                float inv = 1f / mag;
                rx *= inv; ry *= inv; rz *= inv; rw *= inv;
            }
            else
            {
                // Degenerate — return identity
                rx = 0f; ry = 0f; rz = 0f; rw = 1f;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Single-sample extrapolation
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extrapolate from a single sample using its velocity.
        /// Clamped tightly to prevent runaway during packet loss.
        /// </summary>
        private static InterpolationSample ExtrapolateFromSingle(ref InterpolationSample sample, float renderTime)
        {
            return ExtrapolateFromSample(ref sample, renderTime);
        }

        private static InterpolationSample ExtrapolateFromSample(ref InterpolationSample sample, float renderTime)
        {
            float dt = renderTime - sample.RemoteTimestamp;
            dt = Math.Max(0f, Math.Min(dt, MaxExtrapolationSeconds));

            InterpolationSample result = sample;
            result.PosX += sample.VelX * dt;
            result.PosY += sample.VelY * dt;
            result.PosZ += sample.VelZ * dt;
            return result;
        }
    }
}
