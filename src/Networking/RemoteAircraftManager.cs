using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using TCAMultiplayer.Player;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Consolidated state for a remote aircraft. Replaces 17 scattered fields in NetworkManager.
    /// </summary>
    public class RemoteAircraftState
    {
        public ulong PeerId { get; set; }
        public GameObject Aircraft { get; set; }
        public RemoteAircraftController Controller { get; set; }
        public InterpolationBuffer Buffer { get; }
        public int ExternalWriteCount { get; set; } = 0;
        public int FixedUpdateCount { get; set; } = 0;
        public Vector3 SmoothedPosition { get; set; }
        public Quaternion SmoothedRotation { get; set; } = Quaternion.identity;
        public bool HasSmoothedPose { get; set; } = false;

        public string DesiredAircraftType { get; set; }

        // Sequence number tracking for packet ordering
        public uint LastSequenceNumber { get; set; } = 0;

        // Clock synchronization
        // ClockOffset = Local time - Remote time (positive means local clock is ahead)
        // Used in InterpolationBuffer: remoteRenderTime = Time.time - ClockOffset - delay
        public float ClockOffset { get; set; } = 0f;
        public bool ClockOffsetInitialized { get; set; } = false;

        // Asymmetric EMA clock offset tracking.
        // Fast-down (tau=0.1s): quickly adapts when genuine faster path appears,
        //   keeps render point close to minimum latency = maximum buffer.
        // Slow-up (tau=3s): filters stutter-induced high offsets that would
        //   push render point backward and waste buffer.
        private const float CLOCK_TAU_DOWN = 0.1f;
        private const float CLOCK_TAU_UP = 3.0f;

        public void RecordClockSample(float instantOffset, float localTime)
        {
            if (!ClockOffsetInitialized)
            {
                ClockOffset = instantOffset;
                ClockOffsetInitialized = true;
                return;
            }

            float dt = Mathf.Max(Time.deltaTime, 0.001f);
            float tau = (instantOffset < ClockOffset) ? CLOCK_TAU_DOWN : CLOCK_TAU_UP;
            float alpha = 1f - Mathf.Exp(-dt / tau);
            ClockOffset = Mathf.Lerp(ClockOffset, instantOffset, alpha);
        }

        /// <summary>
        /// Reset clock sync state (call on respawn/destroy to re-acquire offset).
        /// </summary>
        public void ResetClock()
        {
            ClockOffset = 0f;
            ClockOffsetInitialized = false;
        }

        // Display state (what we actually render)
        public Vector3 DisplayPosition { get; set; } = Vector3.zero;
        public Quaternion DisplayRotation { get; set; } = Quaternion.identity;
        public Vector3 LastVelocity { get; set; } = Vector3.zero;

        // Interpolation state
        public float LastUpdateTime { get; set; }
        public bool IsExtrapolating { get; set; }
        public bool WasExtrapolating { get; set; }
        public bool HasAppliedPose { get; set; }
        public Vector3 LastAppliedPosition { get; set; } = Vector3.zero;
        public Quaternion LastAppliedRotation { get; set; } = Quaternion.identity;

        // Clone state
        public bool UsingRealAircraft { get; set; }
        public bool NeedsCloneRetry { get; set; }
        public float LastCloneRetryTime { get; set; }

        // Respawn state
        public bool NeedsRespawn { get; set; }
        public float DestroyedTime { get; set; }

        public RemoteAircraftState(int bufferCapacity = NetworkConfig.INTERPOLATION_BUFFER_CAPACITY)
        {
            Buffer = new InterpolationBuffer(bufferCapacity);
        }

        public bool HasAircraft => Aircraft != null;
        public float TimeSinceUpdate => Time.time - LastUpdateTime;
    }

    /// <summary>
    /// Manages remote aircraft lifecycle, interpolation, and visual updates.
    /// Supports multiple remote players (N-player multiplayer).
    /// Extracted from NetworkManager to isolate remote player handling.
    /// </summary>
    public class RemoteAircraftManager
    {
        // Constants now in NetworkConfig

        // Native spawning reflection
        private static Type _gameDataAircraftType;
        private static MethodInfo _getAircraftByNameMethod;
        private static MethodInfo _spawnAircraftMethod;
        private static bool _spawnAircraftHasName;
        private static Type _pilotSkillType;
        private static bool _nativeSpawnInitialized = false;

        // Multi-player: Dictionary of all remote player states
        private readonly Dictionary<ulong, RemoteAircraftState> _remotePlayers = new Dictionary<ulong, RemoteAircraftState>();
        private readonly object _playersLock = new object();
        private Vector3 _localPlayerPosition = Vector3.zero;

        // Public properties for external access - returns first remote player for backward compatibility
        public Vector3 LastRemotePosition
        {
            get
            {
                lock (_playersLock)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        return kvp.Value.DisplayPosition;
                    }
                }
                return Vector3.zero;
            }
        }

        public Vector3 LocalPlayerPosition => _localPlayerPosition;

        public bool HasRemoteAircraft
        {
            get
            {
                lock (_playersLock)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        if (kvp.Value.HasAircraft) return true;
                    }
                }
                return false;
            }
        }

        public float TimeSinceRemoteUpdate
        {
            get
            {
                lock (_playersLock)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        return kvp.Value.TimeSinceUpdate;
                    }
                }
                return float.MaxValue;
            }
        }

        public float DistanceToRemote
        {
            get
            {
                lock (_playersLock)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        return Vector3.Distance(_localPlayerPosition, kvp.Value.DisplayPosition);
                    }
                }
                return float.MaxValue;
            }
        }

        public bool IsExtrapolating
        {
            get
            {
                lock (_playersLock)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        return kvp.Value.IsExtrapolating;
                    }
                }
                return false;
            }
        }

        public string BufferStatus
        {
            get
            {
                lock (_playersLock)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        return kvp.Value.Buffer?.GetDebugInfo() ?? "N/A";
                    }
                }
                return "No players";
            }
        }

        public bool UsingRealAircraft
        {
            get
            {
                lock (_playersLock)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        return kvp.Value.UsingRealAircraft;
                    }
                }
                return false;
            }
        }

        public Vector3 RemoteVelocity
        {
            get
            {
                lock (_playersLock)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        return kvp.Value.LastVelocity;
                    }
                }
                return Vector3.zero;
            }
        }

        public float ExtrapolationTime
        {
            get
            {
                lock (_playersLock)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        return kvp.Value.IsExtrapolating ? kvp.Value.TimeSinceUpdate : 0f;
                    }
                }
                return 0f;
            }
        }

        public ulong RemotePeerId
        {
            get
            {
                lock (_playersLock)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        return kvp.Key;
                    }
                }
                return 0;
            }
        }

        // First controller for backward compatibility
        public RemoteAircraftController Controller
        {
            get
            {
                lock (_playersLock)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        return kvp.Value.Controller;
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Get all remote player states (for iteration)
        /// </summary>
        public IEnumerable<RemoteAircraftState> GetAllRemotePlayers()
        {
            lock (_playersLock)
            {
                return new List<RemoteAircraftState>(_remotePlayers.Values);
            }
        }

        /// <summary>
        /// Get remote player state by peer ID
        /// </summary>
        public RemoteAircraftState GetRemotePlayer(ulong peerId)
        {
            lock (_playersLock)
            {
                if (_remotePlayers.TryGetValue(peerId, out var state))
                {
                    return state;
                }
            }
            return null;
        }

        /// <summary>
        /// Get the number of connected remote players
        /// </summary>
        public int RemotePlayerCount
        {
            get
            {
                lock (_playersLock)
                {
                    return _remotePlayers.Count;
                }
            }
        }

        private RemoteAircraftState GetOrCreateState(ulong peerId)
        {
            lock (_playersLock)
            {
                if (!_remotePlayers.TryGetValue(peerId, out var state))
                {
                    state = new RemoteAircraftState();
                    state.PeerId = peerId;
                    _remotePlayers[peerId] = state;
                    Plugin.Log.LogInfo($"[RemoteAircraftManager] Created state for peer {peerId}");
                }
                return state;
            }
        }

        /// <summary>
        /// Called every frame to update interpolation and retry cloning if needed.
        /// </summary>
        public void Update()
        {
            try { var cam = Camera.main; if (cam != null) _localPlayerPosition = cam.transform.position; } catch { }
            TryRetryAllClones();
        }

        /// <summary>
        /// LateUpdate: no position writes. Unity's RigidbodyInterpolation.Interpolate
        /// handles smooth rendering between FixedUpdate positions automatically.
        /// </summary>
        public void LateUpdate()
        {
            // Nothing — Unity interpolation renders the rigidbody smoothly.
            // We only store display state for other systems that read it.
            foreach (var kvp in _remotePlayers)
            {
                var state = kvp.Value;
                if (state.Aircraft == null) continue;
                try
                {
                    if (!state.Aircraft) continue;
                    // Cache the Unity-interpolated position for other systems (HUD, radar, etc.)
                    state.DisplayPosition = state.Aircraft.transform.position;
                    state.DisplayRotation = state.Aircraft.transform.rotation;
                }
                catch { }
            }
        }

        /// <summary>
        /// FixedUpdate: VELOCITY-STEERING.
        /// Compute interpolated target position, then set rb.velocity so the rigidbody
        /// moves there over the next physics step. Unity's built-in Rigidbody interpolation
        /// smoothly renders between physics steps at any display framerate.
        /// This is the SAME rendering pipeline the local player uses.
        /// </summary>
        public void FixedUpdate()
        {
            foreach (var kvp in _remotePlayers)
            {
                var state = kvp.Value;
                VelocitySteerForState(state);
            }
        }

        private void VelocitySteerForState(RemoteAircraftState state)
        {
            if (state.Aircraft == null) return;
            if (!state.Buffer.HasData) return;

            try
            {
                if (!state.Aircraft) { state.Aircraft = null; state.Controller = null; return; }

                var rb = state.Aircraft.GetComponent<Rigidbody>();
                if (rb == null) return;

                // Enforce non-kinematic + no gravity every physics step
                // (in case any game system resets these)
                if (rb.isKinematic) rb.isKinematic = false;
                if (rb.useGravity) rb.useGravity = false;
                if (rb.interpolation != RigidbodyInterpolation.Interpolate)
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                if (rb.drag != 0f) rb.drag = 0f;
                if (rb.angularDrag != 0f) rb.angularDrag = 0f;

                var (targetPos, targetRot, isExtrapolating, justTeleported) = state.Buffer.GetInterpolatedState();

                state.IsExtrapolating = isExtrapolating;

                float dt = Time.fixedDeltaTime;

                if (justTeleported || !state.HasAppliedPose)
                {
                    // Snap on teleport (FloatingOrigin shift) or first frame
                    rb.position = targetPos;
                    rb.rotation = targetRot;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    state.Aircraft.transform.position = targetPos;
                    state.Aircraft.transform.rotation = targetRot;
                }
                else
                {
                    // VELOCITY STEERING: set velocity so rigidbody arrives at target
                    // over the next FixedUpdate step. Unity physics integrates this,
                    // and RigidbodyInterpolation.Interpolate smoothly renders between steps.
                    rb.velocity = (targetPos - rb.position) / dt;

                    // Data-driven velocity bound: use the synced packet velocity magnitude as reference.
                    // The aircraft's actual speed comes from the game engine — no hardcoded limits.
                    float packetSpeed = state.LastVelocity.magnitude;
                    float steeringSpeed = rb.velocity.magnitude;
                    float maxReasonableSpeed = Mathf.Max(packetSpeed * 2f, packetSpeed + 10f);
                    if (maxReasonableSpeed > 0.1f && steeringSpeed > maxReasonableSpeed)
                    {
                        rb.velocity = rb.velocity * (maxReasonableSpeed / steeringSpeed);
                    }

                    // Angular velocity steering: compute the shortest rotation and convert to angular velocity
                    Quaternion deltaRot = targetRot * Quaternion.Inverse(rb.rotation);
                    // Ensure shortest path
                    if (deltaRot.w < 0f)
                    {
                        deltaRot.x = -deltaRot.x;
                        deltaRot.y = -deltaRot.y;
                        deltaRot.z = -deltaRot.z;
                        deltaRot.w = -deltaRot.w;
                    }
                    deltaRot.ToAngleAxis(out float angle, out Vector3 axis);
                    if (angle > 0.001f && axis.sqrMagnitude > 0.001f)
                    {
                        rb.angularVelocity = axis.normalized * (angle * Mathf.Deg2Rad / dt);

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
                    }
                    else
                    {
                        rb.angularVelocity = Vector3.zero;
                    }
                }

                state.HasAppliedPose = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[RemoteAircraftManager] VelocitySteer error: {ex.Message}");
            }
        }

        private void TryRetryAllClones()
        {
            List<RemoteAircraftState> states;
            lock (_playersLock)
            {
                states = new List<RemoteAircraftState>(_remotePlayers.Values);
            }

            foreach (var state in states)
            {
                TryRetryCloneForState(state);
            }
        }

        private void TryRetryCloneForState(RemoteAircraftState state)
        {
            if (!state.NeedsCloneRetry || state.Aircraft == null) return;
            if (state.UsingRealAircraft) return;

            // Safety: check if Unity has destroyed the GameObject externally
            if (!state.Aircraft)
            {
                state.Aircraft = null;
                state.Controller = null;
                state.NeedsCloneRetry = true;
                return;
            }

            if (Time.time - state.LastCloneRetryTime < NetworkConfig.CLONE_RETRY_INTERVAL) return;
            state.LastCloneRetryTime = Time.time;

            try
            {
                var sourceAircraft = FindLocalAircraftForCloning();
                if (sourceAircraft == null) return;

                Plugin.Log.LogInfo($"[RemoteAircraftManager] Local aircraft now available, upgrading peer {state.PeerId} to real aircraft...");

                Vector3 savedPos = state.Aircraft.transform.position;
                Quaternion savedRot = state.Aircraft.transform.rotation;

                var clone = AircraftCloneConfigurer.CloneAircraft(sourceAircraft, state.PeerId);
                if (clone != null)
                {
                    GameObject.Destroy(state.Aircraft);

                    state.Aircraft = clone;
                    state.Aircraft.transform.position = savedPos;
                    state.Aircraft.transform.rotation = savedRot;
                    // ScreenSpaceMarker removed - no red dot needed

                    state.Controller = clone.GetComponent<RemoteAircraftController>();
                    if (state.Controller != null)
                    {
                        state.Controller.PlayerId = state.PeerId;
                    }
                    state.UsingRealAircraft = true;
                    state.NeedsCloneRetry = false;

                    Plugin.Log.LogInfo($"[RemoteAircraftManager] Successfully upgraded peer {state.PeerId} to real aircraft!");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] Clone retry failed for peer {state.PeerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle incoming aircraft state packet.
        /// </summary>
        public void HandleStatePacket(ulong peerId, AircraftStatePacket packet)
        {
            try
            {
                var state = GetOrCreateState(peerId);

                // Check sequence number to drop out-of-order packets
                // Allow for wraparound: if difference is very large, assume wraparound
                uint seqDiff = packet.SequenceNumber - state.LastSequenceNumber;
                if (state.LastSequenceNumber > 0 && seqDiff > 0 && seqDiff < 0x80000000)
                {
                    // Valid new packet
                }
                else if (state.LastSequenceNumber > 0 && packet.SequenceNumber != 0 && packet.SequenceNumber <= state.LastSequenceNumber)
                {
                    // Out of order - drop unless it's very old (wraparound case)
                    if (state.LastSequenceNumber - packet.SequenceNumber < 1000)
                    {
                        LogHelper.InfoSample(LogCategory.Network,
                            $"[RemoteAircraftManager] Dropping out-of-order packet: seq={packet.SequenceNumber} lastSeq={state.LastSequenceNumber}",
                            $"RemoteAircraftManager.OutOfOrder.{peerId}", LogHelper.PacketSampleRate);
                        return;
                    }
                }
                state.LastSequenceNumber = packet.SequenceNumber;
                state.LastUpdateTime = Time.time;

                // Clock offset: use sliding-window minimum.
                // The minimum over a recent time window represents the fastest
                // packet (least network delay) and gives a stable estimate of
                // true_clock_diff + base_latency.  Old all-time-min + slow-drift
                // was vulnerable to a single fast packet permanently biasing the
                // render point forward, causing buffer underruns and jitter.
                float instantOffset = Time.time - packet.Timestamp;
                state.RecordClockSample(instantOffset, Time.time);

                // Propagate stable clock offset to interpolation buffer
                state.Buffer.ClockOffset = state.ClockOffset;

                if (!string.IsNullOrEmpty(packet.AircraftType) && packet.AircraftType != state.DesiredAircraftType)
                {
                    state.DesiredAircraftType = packet.AircraftType;
                    LogHelper.Info(LogCategory.Network,
                        $"[RemoteAircraftManager] Peer {peerId} DesiredAircraftType updated: '{packet.AircraftType}'");
                }

                LogHelper.InfoSample(LogCategory.Packets,
                    $"[RemoteAircraftManager] State packet peer={peerId} seq={packet.SequenceNumber} type='{packet.AircraftType}' pos=({packet.PosX:F1},{packet.PosY:F1},{packet.PosZ:F1}) vel=({packet.VelX:F1},{packet.VelY:F1},{packet.VelZ:F1}) ts={packet.Timestamp:F2}",
                    "RemoteAircraftManager.StatePacket", LogHelper.HighFreqSampleRate);

                // Incoming packets use ABSOLUTE (floating-origin independent) coordinates.
                // Store absolute positions in the buffer; conversion to local happens at render time.
                // This prevents FloatingOrigin shifts from invalidating stored snapshots.
                var absolutePos = new Vector3d(packet.PosX, packet.PosY, packet.PosZ);
                Vector3 localPos = FloatingOriginHelper.AbsoluteToLocal(absolutePos);
                Quaternion rotation = new Quaternion(packet.RotX, packet.RotY, packet.RotZ, packet.RotW);
                Vector3 velocity = new Vector3(packet.VelX, packet.VelY, packet.VelZ);
                Vector3 angularVelocity = new Vector3(packet.AngVelX, packet.AngVelY, packet.AngVelZ);

                state.LastVelocity = velocity;
                state.Buffer.AddSnapshot(absolutePos, rotation, velocity, angularVelocity, packet.Timestamp);

                // Don't create aircraft if waiting for respawn
                if (state.NeedsRespawn)
                {
                    if (LogHelper.IsEnabled(LogCategory.Network) &&
                        LogHelper.ShouldLogInterval($"RemoteAircraftManager.NeedsRespawn.{peerId}", LogHelper.DefaultIntervalSeconds))
                    {
                        LogHelper.Info(LogCategory.Network,
                            $"[RemoteAircraftManager] Peer {peerId} waiting for respawn, ignoring states for {Time.time - state.DestroyedTime:F1}s");
                    }

                    // Timeout - allow recreation
                    if (Time.time - state.DestroyedTime > NetworkConfig.RESPAWN_TIMEOUT)
                    {
                        Plugin.Log.LogInfo($"[RemoteAircraftManager] Peer {peerId} respawn timeout, allowing recreation");
                        state.NeedsRespawn = false;
                        CleanupState(state);
                        state.Buffer.Clear();
                    }
                    else
                    {
                        state.DisplayPosition = localPos;
                        state.DisplayRotation = rotation;
                        return;
                    }
                }

                if (state.Aircraft == null)
                {
                    state.DisplayPosition = localPos;
                    state.DisplayRotation = rotation;

                    CreateRemoteAircraftForState(state, peerId);
                }

                if (LogHelper.IsEnabled(LogCategory.Network) &&
                    LogHelper.ShouldLogInterval($"RemoteAircraftManager.State.{peerId}", LogHelper.DefaultIntervalSeconds))
                {
                    LogHelper.Info(LogCategory.Network,
                        $"[RemoteAircraftManager] Peer {peerId} state pos={localPos} vel={velocity} ts={packet.Timestamp:F2}");
                }

                // Update visual state on controller
                if (state.Controller != null && !state.Controller.IsDestroyed)
                {
                    state.Controller.UpdateFromState(packet);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[RemoteAircraftManager] HandleStatePacket error for peer {peerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle remote aircraft destroyed notification.
        /// Spawns explosion, despawns the aircraft, and marks the state for respawn.
        /// </summary>
        public void HandleDestroyed(ulong peerId)
        {
            try
            {
                var state = GetOrCreateState(peerId);

                if (state.NeedsRespawn)
                {
                    Plugin.Log.LogInfo($"[RemoteAircraftManager] Peer {peerId} already marked as destroyed, ignoring duplicate");
                    return;
                }

                if (state.Controller != null)
                {
                    // This will spawn explosion VFX and schedule Destroy(gameObject, 2.0f)
                    state.Controller.OnDestroyed();
                }

                state.NeedsRespawn = true;
                state.DestroyedTime = Time.time;
                
                // Null out the Aircraft/Controller references immediately so we stop
                // trying to interpolate a dying aircraft. The actual GameObject will
                // self-destruct after the explosion delay.
                state.Aircraft = null;
                state.Controller = null;
                state.Buffer.Clear();
                state.UsingRealAircraft = false;
                state.ResetClock();

                Plugin.Log.LogInfo($"[RemoteAircraftManager] Peer {peerId} destroyed — aircraft despawned, waiting for respawn");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[RemoteAircraftManager] HandleDestroyed error for peer {peerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle remote aircraft respawn notification.
        /// </summary>
        public void HandleRespawn(ulong peerId, AircraftChangedPacket packet)
        {
            try
            {
                Plugin.Log.LogInfo($"[RemoteAircraftManager] Peer {peerId} respawned with: {packet.AircraftType}");

                var state = GetOrCreateState(peerId);

                if (!string.IsNullOrEmpty(packet.AircraftType))
                {
                    state.DesiredAircraftType = packet.AircraftType;
                    LogHelper.Info(LogCategory.Network,
                        $"[RemoteAircraftManager] Peer {peerId} DesiredAircraftType set from respawn: '{packet.AircraftType}'");
                }

                CleanupState(state);
                state.Buffer.Clear();
                state.NeedsRespawn = false;
                state.DestroyedTime = 0f;
                state.ResetClock();  // Reset clock offset for new aircraft
                state.LastSequenceNumber = 0;  // Reset sequence number for new aircraft

                Plugin.Log.LogInfo($"[RemoteAircraftManager] Peer {peerId} ready for new aircraft on next state packet");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[RemoteAircraftManager] HandleRespawn error for peer {peerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle peer disconnection.
        /// </summary>
        public void HandlePeerDisconnected(ulong peerId)
        {
            RemoteAircraftState state = null;
            lock (_playersLock)
            {
                if (_remotePlayers.TryGetValue(peerId, out state))
                {
                    _remotePlayers.Remove(peerId);
                }
            }

            if (state != null)
            {
                LogHelper.Info(LogCategory.Network, $"[RemoteAircraftManager] Peer {peerId} disconnected; cleaning up remote aircraft");
                CleanupState(state);
            }
        }

        /// <summary>
        /// Set the remote peer ID (called when peer connects).
        /// </summary>
        public void SetRemotePeer(ulong peerId)
        {
            GetOrCreateState(peerId);
            LogHelper.Info(LogCategory.Network, $"[RemoteAircraftManager] SetRemotePeer={peerId}");
        }

        /// <summary>
        /// Initialize reflection for native aircraft spawning
        /// </summary>
        private static void InitializeNativeSpawning()
        {
            if (_nativeSpawnInitialized) return;
            _nativeSpawnInitialized = true;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

                _gameDataAircraftType = Type.GetType("Falcon.GameDataAircraft, Assembly-CSharp");
                _pilotSkillType = Type.GetType("Falcon.UniversalAircraft.PilotSkill, Assembly-CSharp");

                if (_gameDataAircraftType != null)
                {
                    foreach (var method in _gameDataAircraftType.GetMethods(flags))
                    {
                        if (method.Name != "SpawnAircraft") continue;

                        var parameters = method.GetParameters();
                        if (parameters.Length == 7)
                        {
                            _spawnAircraftMethod = method;
                            _spawnAircraftHasName = true;
                            break;
                        }

                        if (parameters.Length == 6)
                        {
                            _spawnAircraftMethod = method;
                            _spawnAircraftHasName = false;
                        }
                    }

                    _getAircraftByNameMethod = _gameDataAircraftType.GetMethod("GetByName", flags);
                    Plugin.Log.LogInfo($"[RemoteAircraftManager] SpawnAircraft found: {_spawnAircraftMethod != null}");
                    Plugin.Log.LogInfo($"[RemoteAircraftManager] GetAircraftByName found: {_getAircraftByNameMethod != null}");
                }

                LogHelper.Info(LogCategory.Reflection,
                    $"[RemoteAircraftManager] Native spawn init: GameDataAircraft={_gameDataAircraftType != null}, PilotSkillType={_pilotSkillType != null}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[RemoteAircraftManager] Native spawn init error: {ex.Message}");
            }
        }

        private void CreateRemoteAircraftForState(RemoteAircraftState state, ulong peerId)
        {
            try
            {
                Plugin.Log.LogInfo($"[RemoteAircraftManager] Creating remote aircraft for peer {peerId}...");
                state.PeerId = peerId;

                InitializeNativeSpawning();

                string preferredAirfield = GetRemoteSelectedAirfield(peerId);

                // Try native spawning first - this creates a proper aircraft without cockpit binding
                GameObject nativeAircraft = TryNativeSpawnForState(state, peerId, preferredAirfield);

                if (nativeAircraft != null)
                {
                    state.Aircraft = nativeAircraft;
                    state.UsingRealAircraft = true;
                    state.NeedsCloneRetry = false;

                    // Add our remote controller
                    state.Controller = state.Aircraft.GetComponent<RemoteAircraftController>();
                    if (state.Controller == null)
                    {
                        state.Controller = state.Aircraft.AddComponent<RemoteAircraftController>();
                        state.Controller.PlayerId = peerId;
                        state.Controller.Initialize();
                    }
                    else
                    {
                        state.Controller.PlayerId = peerId;
                    }

                    Plugin.Log.LogInfo($"[RemoteAircraftManager] Created remote aircraft for peer {peerId} via native spawn!");
                }
                else
                {
                    // Fallback to cloning if native spawn fails
                    var sourceAircraft = FindLocalAircraftForCloning();
                    if (sourceAircraft != null)
                    {
                        state.Aircraft = AircraftCloneConfigurer.CloneAircraft(sourceAircraft, peerId);
                    }

                    if (state.Aircraft != null)
                    {
                        // CRITICAL: Configure the clone the same way as native spawn.
                        // Without this, UniAircraft stays enabled, rigidbodies stay
                        // non-kinematic, and the game's physics fights our interpolation
                        // every frame — causing the 1-10m jitter.
                        ConfigureRemoteAircraft(state.Aircraft);

                        state.UsingRealAircraft = true;
                        state.NeedsCloneRetry = false;
                        state.Controller = state.Aircraft.GetComponent<RemoteAircraftController>();
                        if (state.Controller != null)
                        {
                            state.Controller.PlayerId = peerId;
                        }
                        Plugin.Log.LogInfo($"[RemoteAircraftManager] Cloned real aircraft for peer {peerId} (fallback)!");
                    }
                    else
                    {
                        state.UsingRealAircraft = false;
                        state.NeedsCloneRetry = true;
                        state.Aircraft = CreateFallbackMarker(peerId);
                        Plugin.Log.LogInfo($"[RemoteAircraftManager] Using fallback marker for peer {peerId} (will retry)");
                    }
                }

                state.Aircraft.transform.position = state.DisplayPosition;

                // ScreenSpaceMarker removed - no red dot needed on remote aircraft

                Plugin.Log.LogInfo($"[RemoteAircraftManager] Peer {peerId} created at {state.DisplayPosition}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[RemoteAircraftManager] Create failed for peer {peerId}: {ex}");
            }
        }

        /// <summary>
        /// Try to spawn remote aircraft using the game's native spawning system
        /// </summary>
        private GameObject TryNativeSpawnForState(RemoteAircraftState state, ulong peerId, string airfieldName)
        {
            try
            {
                // Get map faction for proper spawning
                var mapName = Plugin.Instance?.Lobby?.MapName ?? "ActionIsland";
                object faction = GetMapFaction(mapName);
                if (faction == null)
                {
                    Plugin.Log.LogWarning("[RemoteAircraftManager] Map faction not available - cannot spawn remote aircraft");
                    return null;
                }
                LogHelper.Info(LogCategory.Reflection,
                    $"[RemoteAircraftManager] Faction type={faction.GetType().FullName} value={faction}");

                // Determine aircraft type - prefer network desired, then lobby selection, then local fallback
                string desiredType = state.DesiredAircraftType;
                string lobbySelection = GetRemoteSelectedAircraft(peerId);
                string localSelection = GetLocalPlayerAircraftName();
                string aircraftName = ResolveAircraftType(desiredType, lobbySelection, localSelection);
                LogHelper.Info(LogCategory.Network,
                    $"[RemoteAircraftManager] Aircraft selection: desired='{desiredType ?? ""}' lobby='{lobbySelection ?? ""}' local='{localSelection ?? ""}' resolved='{aircraftName}'");

                Plugin.Log.LogInfo($"[RemoteAircraftManager] Attempting native spawn of {aircraftName} for peer {peerId}");

                if (_spawnAircraftMethod == null)
                {
                    Plugin.Log.LogWarning("[RemoteAircraftManager] SpawnAircraft method not available - falling back");
                    return null;
                }

                object skill = GetPilotSkill("Ace");
                if (skill == null)
                {
                    Plugin.Log.LogWarning("[RemoteAircraftManager] PilotSkill not available - cannot spawn remote aircraft");
                    return null;
                }

                var spawnType = Plugin.Instance?.Lobby?.SpawnType ?? LobbySpawnType.Runway;
                bool isAirstart = spawnType == LobbySpawnType.Air;

                LogHelper.Info(LogCategory.Reflection,
                    $"[RemoteAircraftManager] Spawn inputs: map={mapName}, airfield='{airfieldName}', spawnType={spawnType}, pos={state.DisplayPosition}, rot={state.DisplayRotation}");
                LogHelper.Info(LogCategory.Reflection,
                    $"[RemoteAircraftManager] Spawn method={( _spawnAircraftMethod != null ? _spawnAircraftMethod.ToString() : "null" )} hasName={_spawnAircraftHasName}");

                Vector3 spawnPosition = state.DisplayPosition;
                Quaternion spawnRotation = state.DisplayRotation;

                if (!string.IsNullOrEmpty(airfieldName))
                {
                    var (position, rotation) = Game.AirfieldHelper.GetSpawnPoint(airfieldName, spawnType);
                    if (position != Vector3.zero)
                    {
                        spawnPosition = position;
                        spawnRotation = rotation;
                    }
                    LogHelper.Info(LogCategory.Reflection,
                        $"[RemoteAircraftManager] Airfield spawn: name='{airfieldName}' pos={position} rot={rotation}");
                }

                if (spawnType != LobbySpawnType.Air)
                {
                    float terrainHeight = GetTerrainHeightAtPosition(spawnPosition);
                    if (!float.IsNaN(terrainHeight) && !float.IsInfinity(terrainHeight))
                    {
                        spawnPosition.y = terrainHeight;
                    }
                    LogHelper.Info(LogCategory.Reflection,
                        $"[RemoteAircraftManager] Terrain height adjust: height={terrainHeight:F2} finalY={spawnPosition.y:F2}");
                }

                string playerName = GetRemotePlayerName(peerId);
                object[] args = _spawnAircraftHasName
                    ? new object[] { playerName, aircraftName, faction, skill, spawnPosition, spawnRotation, isAirstart }
                    : new object[] { aircraftName, faction, skill, spawnPosition, spawnRotation, isAirstart };

                LogHelper.Info(LogCategory.Reflection,
                    $"[RemoteAircraftManager] Spawn args: name={( _spawnAircraftHasName ? playerName : "(none)" )}, type={aircraftName}, skill={skill}, airstart={isAirstart}, spawnPos={spawnPosition}");

                var spawnResult = _spawnAircraftMethod.Invoke(null, args);
                var component = spawnResult as Component;
                if (component == null)
                {
                    Plugin.Log.LogWarning("[RemoteAircraftManager] SpawnAircraft returned null or non-component");
                    return null;
                }

                var go = component.gameObject;
                go.name = playerName;

                // Get the remote player's selected loadout
                string loadoutName = GetRemoteSelectedLoadout(peerId) ?? "Clean";
                ConfigureSpawnedAircraft(component, loadoutName, "Mixed");
                ConfigureRemoteAircraft(go);

                LogHelper.Info(LogCategory.Reflection,
                    $"[RemoteAircraftManager] Native spawn components: UniAircraft={component.GetType().FullName}, hasRigidbody={go.GetComponent<Rigidbody>() != null}");

                Plugin.Log.LogInfo($"[RemoteAircraftManager] Native spawn succeeded: {go.name}");
                return go;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] Native spawn failed: {ex.Message}");
                return null;
        }
        }

        /// <summary>
        /// Try to spawn by loading the aircraft prefab directly
        /// </summary>
        private GameObject TryPrefabSpawn(RemoteAircraftState state, string aircraftName, ulong peerId)
        {
            try
            {
                // Try to get aircraft data
                if (_getAircraftByNameMethod != null)
                {
                    var aircraftData = _getAircraftByNameMethod.Invoke(null, new object[] { aircraftName });
                    if (aircraftData != null)
                    {
                        // Get the prefab from aircraft data
                        var prefabProp = aircraftData.GetType().GetProperty("Prefab") ??
                                         aircraftData.GetType().GetProperty("AircraftPrefab");
                        if (prefabProp != null)
                        {
                            var prefab = prefabProp.GetValue(aircraftData) as GameObject;
                            if (prefab != null)
                            {
                                var instance = GameObject.Instantiate(prefab, state.DisplayPosition, state.DisplayRotation);
                                instance.name = GetRemotePlayerName(peerId);

                                // Configure for remote control
                                ConfigureRemoteAircraft(instance);

                                Plugin.Log.LogInfo($"[RemoteAircraftManager] Prefab spawn succeeded: {instance.name}");
                                return instance;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] Prefab spawn failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Get ENEMY (Red) faction from the current map.
        /// Remote aircraft must be on the opposing team so the game's 
        /// damage system, IFF, and targeting treat them as hostile.
        /// Falls back to Blue faction if Red is not available.
        /// </summary>
        private static object GetMapFaction(string mapName)
        {
            try
            {
                var gameDataMapsType = Type.GetType("Falcon.GameDataMaps, Assembly-CSharp");
                var mapDataType = Type.GetType("Falcon.Game2.MapData, Assembly-CSharp");

                if (gameDataMapsType != null && mapDataType != null)
                {
                    var getByName = gameDataMapsType.GetMethod("GetByName", BindingFlags.Public | BindingFlags.Static);
                    
                    if (getByName != null)
                    {
                        var mapData = getByName.Invoke(null, new object[] { mapName });
                        if (mapData != null)
                        {
                            // Try RED faction first (enemy team) — this makes remote aircraft hostile
                            var getRedFaction = mapDataType.GetMethod("GetPrimaryRedFaction", BindingFlags.Public | BindingFlags.Instance);
                            if (getRedFaction != null)
                            {
                                var redFaction = getRedFaction.Invoke(mapData, null);
                                if (redFaction != null)
                                {
                                    Plugin.Log?.LogInfo($"[RemoteAircraftManager] Using RED faction for remote aircraft: {redFaction}");
                                    return redFaction;
                                }
                            }
                            
                            // Try alternative names for enemy faction
                            foreach (string methodName in new[] { "GetPrimaryOpforFaction", "GetRedFaction", "GetEnemyFaction" })
                            {
                                var altMethod = mapDataType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                                if (altMethod != null)
                                {
                                    var altFaction = altMethod.Invoke(mapData, null);
                                    if (altFaction != null)
                                    {
                                        Plugin.Log?.LogInfo($"[RemoteAircraftManager] Using enemy faction via {methodName}: {altFaction}");
                                        return altFaction;
                                    }
                                }
                            }
                            
                            // Log all available methods on MapData for debugging
                            Plugin.Log?.LogWarning("[RemoteAircraftManager] Could not find Red faction method. Available MapData methods:");
                            foreach (var method in mapDataType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                            {
                                if (method.Name.Contains("Faction") || method.Name.Contains("faction"))
                                {
                                    Plugin.Log?.LogInfo($"  MapData method: {method.Name} -> {method.ReturnType.Name}");
                                }
                            }
                            
                            // Fallback to Blue faction (same team — not ideal but at least spawns)
                            var getBlueFaction = mapDataType.GetMethod("GetPrimaryBlueFaction", BindingFlags.Public | BindingFlags.Instance);
                            if (getBlueFaction != null)
                            {
                                Plugin.Log?.LogWarning("[RemoteAircraftManager] Falling back to BLUE faction — remote will be friendly!");
                                return getBlueFaction.Invoke(mapData, null);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[RemoteAircraftManager] GetMapFaction error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Get the local player's aircraft name
        /// </summary>
        private string GetLocalPlayerAircraftName()
        {
            try
            {
                var player = Falcon.UniversalAircraft.UniAircraft.Player;
                if (player != null)
                {
                    // Use shared helper for Data→Name extraction
                    var name = ReflectionHelper.GetAircraftNameFromData(player);
                    if (!string.IsNullOrEmpty(name)) return name;

                    // Fallback: parse from GameObject name
                    string goName = player.gameObject.name;
                    var mapped = ReflectionHelper.MapAircraftNameFromString(goName);
                    if (!string.IsNullOrEmpty(mapped)) return mapped;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] Could not get aircraft name: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Disable AI pilot on spawned aircraft so we control it
        /// </summary>
        private void DisableAIPilot(GameObject aircraft)
        {
            try
            {
                var uniAircraftType = Type.GetType("Falcon.UniversalAircraft.UniAircraft, Assembly-CSharp");
                if (uniAircraftType != null)
                {
                    var uniAircraft = aircraft.GetComponentInChildren(uniAircraftType) as Component;
                    if (uniAircraft != null)
                    {
                        if (TrySetPilotBypass(uniAircraft, true))
                        {
                            Plugin.Log.LogInfo("[RemoteAircraftManager] Set IsPilotBypassed=true");
                        }
                    }
                }

                // Find and disable UniPilot or AI components
                var uniPilotType = Type.GetType("Falcon.UniversalAircraft.UniPilot, Assembly-CSharp");
                if (uniPilotType != null)
                {
                    var pilot = aircraft.GetComponentInChildren(uniPilotType) as MonoBehaviour;
                    if (pilot != null)
                    {
                        pilot.enabled = false;
                        Plugin.Log.LogInfo("[RemoteAircraftManager] Disabled UniPilot");
                    }
                }

                // Also disable any AI behavior components
                string[] aiComponentNames = { "AIPilot", "AIController", "PilotAI", "FlightAI" };
                foreach (var name in aiComponentNames)
                {
                    var aiType = Type.GetType($"Falcon.{name}, Assembly-CSharp");
                    if (aiType != null)
                    {
                        var ai = aircraft.GetComponentInChildren(aiType) as MonoBehaviour;
                        if (ai != null)
                        {
                            ai.enabled = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] DisableAIPilot error: {ex.Message}");
            }
        }

        /// <summary>
        /// Configure a prefab-spawned aircraft for remote control
        /// </summary>
        private void ConfigureRemoteAircraft(GameObject aircraft)
        {
            Plugin.Log.LogWarning($"[RemoteAircraftManager] ConfigureRemoteAircraft ENTER for {aircraft?.name}");
            try
            {
                // Disable player-specific components
                DisableAIPilot(aircraft);

                // CRITICAL: Blacklist from FloatingOrigin so it never moves this object.
                // Our interpolation converts absolute→local each frame using the current
                // TotalOffset, so we don't need the origin shift to touch the transform.
                // Without this, FixedUpdate shifts the aircraft to a wrong position, and
                // any system that reads it between FixedUpdate and our LateUpdate sees garbage.
                try
                {
                    // Try property first (Blacklist is a static property, not a field)
                    var blacklistProp = typeof(Falcon.World.FloatingOrigin)
                        .GetProperty("Blacklist", BindingFlags.Public | BindingFlags.Static);
                    object listObj = null;
                    if (blacklistProp != null)
                    {
                        listObj = blacklistProp.GetValue(null);
                    }
                    else
                    {
                        // Fallback: try as field
                        var blacklistField = typeof(Falcon.World.FloatingOrigin)
                            .GetField("Blacklist", BindingFlags.Public | BindingFlags.Static);
                        if (blacklistField != null)
                            listObj = blacklistField.GetValue(null);
                    }

                    var list = listObj as System.Collections.IList;
                    if (list != null && !list.Contains(aircraft))
                    {
                        list.Add(aircraft);
                        Plugin.Log.LogInfo($"[RemoteAircraftManager] Added remote aircraft to FloatingOrigin.Blacklist (prop={blacklistProp != null})");
                    }
                    else if (list == null)
                    {
                        Plugin.Log.LogWarning("[RemoteAircraftManager] FloatingOrigin.Blacklist not found as property or field!");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[RemoteAircraftManager] FloatingOrigin blacklist failed: {ex.Message}");
                }

                // CRITICAL: Disable UniAircraft component to prevent its FixedUpdate from
                // resetting isKinematic=false every physics frame. The remote aircraft is
                // driven purely by network interpolation, not the game's physics simulation.
                var uniAircraft = aircraft.GetComponent<Falcon.UniversalAircraft.UniAircraft>();
                if (uniAircraft != null)
                {
                    uniAircraft.enabled = false;
                    Plugin.Log.LogInfo("[RemoteAircraftManager] Disabled UniAircraft component (prevents physics FixedUpdate)");

                    // FIX: Prevent collision-triggered VFX and damage on remote aircraft.
                    // Unity's OnCollisionEnter/OnCollisionStay fire even on disabled MonoBehaviours.
                    // The remote clone's collisions with terrain trigger HandleFuselageImpact which:
                    //  1. Spawns impact VFX (HardFuselageImpactPrefab etc.) → "nuclear explosion" visuals
                    //  2. Applies damage → HP reaches 0 → DestroyAircraft() → spawns GroundImpactPrefab
                    //     AND SpawnCrater which triggers explosion sync packets back to the client
                    // Fix: null all VFX prefabs AND make Damage invulnerable AND disable colliders.
                    try
                    {
                        var uniType = uniAircraft.GetType();
                        
                        // 1. Null out all impact/explosion VFX prefab fields
                        string[] vfxFieldNames = {
                            "HardFuselageImpactPrefab", "SoftFuselageImpactPrefab",
                            "WaterFuselageImpactPrefab", "GroundImpactPrefab",
                            "AirExplosionPrefab", "WaterImpactPrefab"
                        };
                        int nulled = 0;
                        foreach (var fieldName in vfxFieldNames)
                        {
                            var field = uniType.GetField(fieldName,
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (field != null)
                            {
                                field.SetValue(uniAircraft, null);
                                nulled++;
                            }
                        }
                        Plugin.Log.LogInfo($"[RemoteAircraftManager] Nulled {nulled} impact/explosion VFX prefabs on remote aircraft");
                        
                        // 2. Make Damage component invulnerable so collision damage doesn't destroy the clone
                        var damageProp = uniType.GetProperty("Damage",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (damageProp != null)
                        {
                            var damageable = damageProp.GetValue(uniAircraft);
                            if (damageable != null)
                            {
                                // Set MaxHitpoints very high so it never dies from collision damage
                                var maxHpProp = damageable.GetType().GetProperty("MaxHitpoints",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                if (maxHpProp != null && maxHpProp.CanWrite)
                                    maxHpProp.SetValue(damageable, 999999);
                                else
                                {
                                    var maxHpField = damageable.GetType().GetField("MaxHitpoints",
                                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                    if (maxHpField != null)
                                        maxHpField.SetValue(damageable, 999999);
                                }
                                Plugin.Log.LogInfo("[RemoteAircraftManager] Set Damage.MaxHitpoints=999999 on remote aircraft");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"[RemoteAircraftManager] Failed to configure remote aircraft safety: {ex.Message}");
                    }
                }

                // VELOCITY-STEERING: Configure the ROOT rigidbody as non-kinematic with
                // Unity interpolation. We'll set rb.velocity each FixedUpdate to steer it
                // toward the interpolated target. Unity's physics integration moves it,
                // and RigidbodyInterpolation.Interpolate smoothly renders between physics steps.
                // This is the SAME rendering pipeline the local player uses.
                //
                // Child rigidbodies (e.g. landing gear, weapons) stay kinematic.
                var rootRb = aircraft.GetComponent<Rigidbody>();
                if (rootRb != null)
                {
                    rootRb.isKinematic = false;
                    rootRb.useGravity = false;
                    rootRb.drag = 0f;
                    rootRb.angularDrag = 0f;
                    rootRb.interpolation = RigidbodyInterpolation.Interpolate;
                    rootRb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    // Disable collision response: remote aircraft doesn't need to physically collide
                    // with terrain. OnCollisionEnter/Stay still fire on disabled MonoBehaviours and cause
                    // damage + VFX. Using detectCollisions=false prevents ALL collision callbacks while
                    // keeping colliders active for raycast hit detection (bullets, missiles).
                    rootRb.detectCollisions = false;
                    rootRb.constraints = RigidbodyConstraints.None;
                    rootRb.velocity = Vector3.zero;
                    rootRb.angularVelocity = Vector3.zero;
                    Plugin.Log.LogInfo($"[RemoteAircraftManager] Root Rigidbody: non-kinematic + Interpolate (velocity-steering mode)");
                }
                else
                {
                    Plugin.Log.LogWarning("[RemoteAircraftManager] No root Rigidbody found!");
                }

                // Child rigidbodies stay kinematic (weapons, gear, etc.)
                var allRbs = aircraft.GetComponentsInChildren<Rigidbody>(true);
                int childCount = 0;
                foreach (var rb in allRbs)
                {
                    if (rb == null || rb == rootRb) continue;
                    rb.isKinematic = true;
                    rb.useGravity = false;
                    rb.interpolation = RigidbodyInterpolation.None;
                    childCount++;
                }
                if (childCount > 0)
                    Plugin.Log.LogInfo($"[RemoteAircraftManager] Set {childCount} child Rigidbodies to kinematic");
                else
                {
                    Plugin.Log.LogWarning("[RemoteAircraftManager] No Rigidbody found on remote aircraft");
                }

                // Prevent animation/root-motion systems from writing transform deltas.
                var animators = aircraft.GetComponentsInChildren<Animator>(true);
                foreach (var animator in animators)
                {
                    if (animator == null) continue;
                    animator.applyRootMotion = false;
                    animator.updateMode = AnimatorUpdateMode.Normal;
                }
                if (animators.Length > 0)
                {
                    Plugin.Log.LogInfo($"[RemoteAircraftManager] Configured {animators.Length} Animator components (root motion disabled)");
                }

                // Disable cockpit camera
                var cockpitCams = aircraft.GetComponentsInChildren<Camera>(true);
                foreach (var cam in cockpitCams)
                {
                    if (cam.gameObject.name.ToLower().Contains("cockpit") ||
                        cam.gameObject.name.ToLower().Contains("interior"))
                    {
                        cam.enabled = false;
                    }
                }

                // Disable audio listeners
                var listeners = aircraft.GetComponentsInChildren<AudioListener>(true);
                foreach (var listener in listeners)
                {
                    listener.enabled = false;
                }

            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] ConfigureRemoteAircraft error: {ex.Message}");
            }
        }

        private static object GetPilotSkill(string skillName)
        {
            if (_pilotSkillType == null) return null;

            try
            {
                LogHelper.Info(LogCategory.Reflection,
                    $"[RemoteAircraftManager] PilotSkill type={_pilotSkillType.FullName} requested='{skillName}'");

                if (Enum.IsDefined(_pilotSkillType, skillName))
                {
                    var parsed = Enum.Parse(_pilotSkillType, skillName);
                    LogHelper.Info(LogCategory.Reflection, $"[RemoteAircraftManager] PilotSkill parsed={parsed}");
                    return parsed;
                }

                Array values = Enum.GetValues(_pilotSkillType);
                if (values.Length > 0)
                {
                    LogHelper.Info(LogCategory.Reflection,
                        $"[RemoteAircraftManager] PilotSkill fallback={values.GetValue(0)} (count={values.Length})");
                    return values.GetValue(0);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] GetPilotSkill error: {ex.Message}");
            }

            return null;
        }

        private static string GetRemotePlayerName(ulong peerId)
        {
            var lobby = Plugin.Instance?.Lobby;
            if (lobby == null) return $"Player {peerId}";

            if (lobby.Players != null && lobby.Players.TryGetValue(peerId, out var info))
            {
                if (!string.IsNullOrEmpty(info?.PlayerName)) return info.PlayerName;
            }

            return $"Player {peerId}";
        }

        private static string GetRemoteSelectedAircraft(ulong peerId)
        {
            var lobby = Plugin.Instance?.Lobby;
            if (lobby == null) return null;

            if (lobby.Players != null && lobby.Players.TryGetValue(peerId, out var info))
            {
                if (!string.IsNullOrEmpty(info?.SelectedAircraft)) return info.SelectedAircraft;
            }

            return null;
        }

        private static string GetRemoteSelectedLoadout(ulong peerId)
        {
            var lobby = Plugin.Instance?.Lobby;
            if (lobby == null) return null;

            if (lobby.Players != null && lobby.Players.TryGetValue(peerId, out var info))
            {
                if (!string.IsNullOrEmpty(info?.SelectedLoadout)) return info.SelectedLoadout;
            }

            return null;
        }

        private static string NormalizeAircraftType(string aircraftType)
        {
            if (string.IsNullOrWhiteSpace(aircraftType)) return null;

            string normalized = aircraftType.Trim();

            int parenIndex = normalized.IndexOf('(');
            if (parenIndex > 0)
            {
                normalized = normalized.Substring(0, parenIndex).Trim();
            }

            // Normalize common variants
            if (normalized.Equals("F-16", StringComparison.OrdinalIgnoreCase)) return "F16C";
            if (normalized.Equals("F-18", StringComparison.OrdinalIgnoreCase)) return "F18C";
            if (normalized.Equals("F/A-18", StringComparison.OrdinalIgnoreCase)) return "F18C";
            if (normalized.Equals("F-15", StringComparison.OrdinalIgnoreCase)) return "F15C";
            if (normalized.Equals("AV-8B", StringComparison.OrdinalIgnoreCase)) return "AV8B";
            if (normalized.Equals("Harrier", StringComparison.OrdinalIgnoreCase)) return "AV8B";

            return normalized;
        }

        private string ResolveAircraftType(string desiredType, string lobbySelection, string localSelection)
        {
            string[] candidates =
            {
                desiredType,
                lobbySelection,
                localSelection,
                NormalizeAircraftType(desiredType),
                NormalizeAircraftType(lobbySelection),
                NormalizeAircraftType(localSelection),
                "AV8B"
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                bool valid = IsAircraftTypeValid(candidate);
                if (LogHelper.IsEnabled(LogCategory.Reflection))
                {
                    LogHelper.Info(LogCategory.Reflection,
                        $"[RemoteAircraftManager] AircraftType validation: '{candidate}' valid={valid}");
                }

                if (valid)
                {
                    return candidate;
                }
            }

            return "AV8B";
        }

        private bool IsAircraftTypeValid(string aircraftType)
        {
            if (string.IsNullOrEmpty(aircraftType)) return false;
            if (_getAircraftByNameMethod == null) return true;

            try
            {
                var data = _getAircraftByNameMethod.Invoke(null, new object[] { aircraftType });
                return data != null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] AircraftType validation error for '{aircraftType}': {ex.Message}");
                return false;
            }
        }


        private static bool TrySetPilotBypass(Component uniAircraft, bool bypass)
        {
            if (uniAircraft == null) return false;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var type = uniAircraft.GetType();

                var bypassProp = type.GetProperty("IsPilotBypassed", flags);
                if (bypassProp != null && bypassProp.CanWrite)
                {
                    bypassProp.SetValue(uniAircraft, bypass);
                    return true;
                }

                var bypassField = type.GetField("IsPilotBypassed", flags)
                    ?? type.GetField("isPilotBypassed", flags)
                    ?? type.GetField("<IsPilotBypassed>k__BackingField", flags);

                if (bypassField != null)
                {
                    bypassField.SetValue(uniAircraft, bypass);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] TrySetPilotBypass error: {ex.Message}");
            }

            return false;
        }

        private static void ConfigureSpawnedAircraft(Component uniAircraft, string loadout, string ammoBelt)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var type = uniAircraft.GetType();

                var storesProp = type.GetProperty("Stores", flags);
                var stores = storesProp?.GetValue(uniAircraft);
                if (stores != null)
                {
                    var loadLoadout = stores.GetType().GetMethod("LoadLoadout", flags);
                    var loadAmmo = stores.GetType().GetMethod("LoadAmmoBelt", flags);
                    loadLoadout?.Invoke(stores, new object[] { loadout });
                    loadAmmo?.Invoke(stores, new object[] { ammoBelt });
                }

                var fireControlProp = type.GetProperty("FireControl", flags);
                var fireControl = fireControlProp?.GetValue(uniAircraft);
                if (fireControl != null)
                {
                    var switchCombat = fireControl.GetType().GetMethod("SwitchToCombatMode", flags);
                    switchCombat?.Invoke(fireControl, null);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] ConfigureSpawnedAircraft error: {ex.Message}");
            }
        }

        private GameObject FindLocalAircraftForCloning()
        {
            try
            {
                var aircraftType = Type.GetType("Falcon.UniversalAircraft.UniAircraft, Assembly-CSharp");
                if (aircraftType != null)
                {
                    var aircrafts = GameObject.FindObjectsByType(aircraftType, FindObjectsSortMode.None) as Component[];
                    if (aircrafts != null && aircrafts.Length > 0)
                    {
                        foreach (var aircraft in aircrafts)
                        {
                            // Don't clone a remote aircraft!
                            if (aircraft.gameObject.GetComponent<RemoteAircraftController>() != null) continue;
                            if (aircraft.gameObject.name.Contains("MP_Remote")) continue;
                            return aircraft.gameObject;
                        }
                    }
                }

                // Fallback: search by name
                var allObjects = GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None);
                foreach (var t in allObjects)
                {
                    string name = t.gameObject.name.ToLower();
                    if (name.Contains("mp_remote")) continue;

                    if (name.Contains("aircraft") || name.Contains("plane") || name.Contains("jet") ||
                        name.Contains("f-16") || name.Contains("f16") || name.Contains("falcon"))
                    {
                        if (t.GetComponent<Rigidbody>() != null)
                        {
                            Plugin.Log.LogInfo($"[RemoteAircraftManager] Found aircraft by name: {t.gameObject.name}");
                            return t.gameObject;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[RemoteAircraftManager] Error finding aircraft: {ex.Message}");
            }

            return null;
        }

        private GameObject CreateFallbackMarker(ulong peerId)
        {
            var marker = new GameObject($"MP_RemoteMarker_{peerId}");

            CreateLineRendererMarker(marker);
            CreateTrailMarker(marker);
            CreatePrimitiveMarker(marker);
            marker.AddComponent<RemoteMarkerRenderer>();

            try
            {
                var lightObj = new GameObject("RemotePlayerLight");
                lightObj.transform.SetParent(marker.transform);
                lightObj.transform.localPosition = Vector3.zero;
                var light = lightObj.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = Color.red;
                light.intensity = 100f;
                light.range = 1000f;
            }
            catch { }

            return marker;
        }

        private static string GetRemoteSelectedAirfield(ulong peerId)
        {
            var lobby = Plugin.Instance?.Lobby;
            if (lobby == null) return null;

            if (lobby.Players != null && lobby.Players.TryGetValue(peerId, out var info))
            {
                if (!string.IsNullOrEmpty(info?.SelectedAirfield)) return info.SelectedAirfield;
            }

            return null;
        }

        private static float GetTerrainHeightAtPosition(Vector3 position)
        {
            try
            {
                var terrainTools = Type.GetType("Falcon.Utilities.TerrainTools, Assembly-CSharp");
                if (terrainTools == null) return position.y;

                var method = terrainTools.GetMethod("GetTerrainHeightAtPosition", BindingFlags.Public | BindingFlags.Static);
                if (method == null) return position.y;

                var result = method.Invoke(null, new object[] { position });
                if (result is float height)
                {
                    return height;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] GetTerrainHeightAtPosition error: {ex.Message}");
            }

            return position.y;
        }

        private void CreateLineRendererMarker(GameObject parent)
        {
            try
            {
                var lineObj = new GameObject("LineMarker");
                lineObj.transform.SetParent(parent.transform);
                lineObj.transform.localPosition = Vector3.zero;

                var lr = lineObj.AddComponent<LineRenderer>();
                lr.useWorldSpace = false;
                lr.startWidth = 5f;
                lr.endWidth = 5f;
                lr.positionCount = 5;

                float size = 20f;
                lr.SetPosition(0, new Vector3(0, size, 0));
                lr.SetPosition(1, new Vector3(size, 0, 0));
                lr.SetPosition(2, new Vector3(0, -size, 0));
                lr.SetPosition(3, new Vector3(-size, 0, 0));
                lr.SetPosition(4, new Vector3(0, size, 0));

                lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.startColor = Color.red;
                lr.endColor = Color.yellow;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] LineRenderer failed: {ex.Message}");
            }
        }

        private void CreateTrailMarker(GameObject parent)
        {
            try
            {
                var trailObj = new GameObject("TrailMarker");
                trailObj.transform.SetParent(parent.transform);
                trailObj.transform.localPosition = Vector3.zero;

                var trail = trailObj.AddComponent<TrailRenderer>();
                trail.time = 2f;
                trail.startWidth = 10f;
                trail.endWidth = 2f;
                trail.material = new Material(Shader.Find("Sprites/Default"));
                trail.startColor = Color.red;
                trail.endColor = new Color(1, 0, 0, 0);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] TrailRenderer failed: {ex.Message}");
            }
        }

        private void CreatePrimitiveMarker(GameObject parent)
        {
            try
            {
                Shader foundShader = null;

                var allRenderers = GameObject.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
                foreach (var r in allRenderers)
                {
                    if (r.material != null && r.material.shader != null && r.isVisible)
                    {
                        foundShader = r.material.shader;
                        break;
                    }
                }

                if (foundShader == null)
                {
                    foreach (var r in allRenderers)
                    {
                        if (r.material?.shader != null)
                        {
                            foundShader = r.material.shader;
                            break;
                        }
                    }
                }

                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "PrimitiveMarker";
                sphere.transform.SetParent(parent.transform);
                sphere.transform.localPosition = Vector3.zero;
                sphere.transform.localScale = new Vector3(30f, 30f, 30f);

                var col = sphere.GetComponent<Collider>();
                if (col != null) GameObject.Destroy(col);

                var renderer = sphere.GetComponent<Renderer>();
                if (renderer != null && foundShader != null)
                {
                    var mat = new Material(foundShader);
                    mat.color = Color.red;

                    if (mat.HasProperty("_BaseColor"))
                        mat.SetColor("_BaseColor", Color.red);
                    if (mat.HasProperty("_Color"))
                        mat.SetColor("_Color", Color.red);
                    if (mat.HasProperty("_EmissionColor"))
                    {
                        mat.EnableKeyword("_EMISSION");
                        mat.SetColor("_EmissionColor", Color.red * 2f);
                    }

                    renderer.material = mat;
                }

                sphere.layer = 0;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftManager] Primitive failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Clean up a specific remote aircraft state.
        /// </summary>
        private void CleanupState(RemoteAircraftState state)
        {
            if (state == null) return;

            if (state.Aircraft != null)
            {
                Plugin.Log.LogInfo($"[RemoteAircraftManager] Cleaning up remote aircraft for peer {state.PeerId}");

                try
                {
                    var prop = typeof(Falcon.World.FloatingOrigin)
                        .GetProperty("Blacklist", BindingFlags.Public | BindingFlags.Static);
                    object listObj = null;
                    if (prop != null) listObj = prop.GetValue(null);
                    else
                    {
                        var field = typeof(Falcon.World.FloatingOrigin)
                            .GetField("Blacklist", BindingFlags.Public | BindingFlags.Static);
                        if (field != null) listObj = field.GetValue(null);
                    }
                    (listObj as System.Collections.IList)?.Remove(state.Aircraft);
                }
                catch { }

                GameObject.Destroy(state.Aircraft);
                state.Aircraft = null;
            }

            state.Controller = null;
            state.Buffer.Clear();
            state.UsingRealAircraft = false;
            state.NeedsCloneRetry = false;
        }

        /// <summary>
        /// Clean up all remote aircraft resources.
        /// </summary>
        public void Cleanup()
        {
            List<RemoteAircraftState> states;
            lock (_playersLock)
            {
                states = new List<RemoteAircraftState>(_remotePlayers.Values);
                _remotePlayers.Clear();
            }

            foreach (var state in states)
            {
                CleanupState(state);
            }

            Plugin.Log.LogInfo("[RemoteAircraftManager] Cleaned up all remote aircraft");
        }
    }
}
