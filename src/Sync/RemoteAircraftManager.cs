using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Falcon.Damage;
using Falcon.UniversalAircraft;
using TCAMultiplayer.Core;
using TCAMultiplayer.Game;
using TCAMultiplayer.Protocol;

namespace TCAMultiplayer.Sync
{
    /// <summary>
    /// Per-peer state tracking for remote aircraft.
    /// </summary>
    internal class RemotePeerState
    {
        public ulong PeerId;
        public InterpolationBuffer Buffer;
        public UniAircraft Aircraft;
        public RemoteAircraftController Controller;
        public string AircraftType;
        public bool IsSpawned;
        public bool IsWaitingForRespawn;
        public uint LastSequenceNumber;
        public bool HasReceivedSequence;
    }

    /// <summary>
    /// Manages all remote players' aircraft with interpolation.
    /// Created per GameSession, disposed with it. No static state.
    /// </summary>
    public class RemoteAircraftManager : IDisposable
    {
        private const string Tag = "RemoteAircraftMgr";

        private readonly GameSession _session;
        private readonly AircraftSpawner _spawner;
        private readonly FloatingOriginService _originService;
        private readonly Dictionary<ulong, RemotePeerState> _peers
            = new Dictionary<ulong, RemotePeerState>();

        private readonly FieldInfo _destroyedByField;
        private readonly FieldInfo _criticalDamageField;
        private bool _disposed;

        public RemoteAircraftManager(GameSession session, AircraftSpawner spawner, FloatingOriginService originService)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            _originService = originService ?? throw new ArgumentNullException(nameof(originService));
            _destroyedByField = typeof(UniAircraft).GetField(
                "destroyedBy",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _criticalDamageField = typeof(UniAircraft).GetField(
                "CriticalDamage",
                BindingFlags.Instance | BindingFlags.NonPublic);
        }

        /// <summary>
        /// Handle an incoming AircraftStatePacket — feed the interpolation buffer.
        /// Creates the remote aircraft on the first packet if not yet spawned.
        /// </summary>
        public void HandleStatePacket(ulong peerId, AircraftStatePacket packet)
        {
            if (_disposed) return;

            if (!_peers.TryGetValue(peerId, out var peer))
            {
                // First packet from this peer — create tracking state
                peer = new RemotePeerState
                {
                    PeerId = peerId,
                    Buffer = CreateInterpolationBuffer(),
                    AircraftType = packet.AircraftType ?? "F-16C"
                };
                _peers[peerId] = peer;
                Log.Info(Tag, $"New remote peer {peerId} with aircraft {peer.AircraftType}");
            }

            if (peer.IsWaitingForRespawn)
            {
                // Late state packets can arrive after the owner has reported death.
                // Ignore them until the host-approved Respawned packet clears this flag.
                return;
            }

            if (!ShouldAcceptSequence(peer.HasReceivedSequence, peer.LastSequenceNumber, packet.SequenceNumber))
            {
                Log.Debug(
                    Tag,
                    $"Ignoring stale aircraft state from peer {peerId}: seq={packet.SequenceNumber}, last={peer.LastSequenceNumber}");
                return;
            }

            peer.LastSequenceNumber = packet.SequenceNumber;
            peer.HasReceivedSequence = true;

            // Convert packet fields into an InterpolationSample
            var sample = new InterpolationSample
            {
                PosX = packet.PosX,
                PosY = packet.PosY,
                PosZ = packet.PosZ,
                RotX = packet.RotX,
                RotY = packet.RotY,
                RotZ = packet.RotZ,
                RotW = packet.RotW,
                VelX = packet.VelX,
                VelY = packet.VelY,
                VelZ = packet.VelZ,
                AngVelX = packet.AngVelX,
                AngVelY = packet.AngVelY,
                AngVelZ = packet.AngVelZ,
                Throttle = packet.Throttle,
                Pitch = packet.Pitch,
                Roll = packet.Roll,
                Yaw = packet.Yaw,
                NozzleAngle = packet.NozzleAngle,
                SpeedKIAS = packet.SpeedKIAS,
                BrakeState = packet.BrakeState,
                Flags = packet.Flags,
                RemoteTimestamp = packet.Timestamp,
                LocalReceiveTime = Time.time
            };
            peer.Buffer.AddSample(sample);

            // Spawn aircraft on first valid packet if not yet spawned
            if (!peer.IsSpawned)
            {
                SpawnRemote(peer, packet);
            }
        }

        /// <summary>
        /// Spawn a remote peer's aircraft using native game APIs.
        /// </summary>
        private void SpawnRemote(RemotePeerState peer, AircraftStatePacket packet)
        {
            try
            {
                var localPos = _originService.AbsoluteToLocal(packet.PosX, packet.PosY, packet.PosZ);
                var rot = new Quaternion(packet.RotX, packet.RotY, packet.RotZ, packet.RotW);

                peer.Aircraft = _spawner.SpawnRemoteAircraft(
                    peer.PeerId, peer.AircraftType, localPos, rot);

                if (peer.Aircraft != null)
                {
                    // Attach controller and wire up interpolation
                    peer.Controller = peer.Aircraft.gameObject.AddComponent<RemoteAircraftController>();
                    peer.Controller.Initialize(peer.Buffer, _originService);
                    var player = _session.GetPlayer(peer.PeerId);
                    if (!string.IsNullOrEmpty(player?.SelectedLoadout))
                        _spawner.ApplyLoadout(peer.PeerId, player.SelectedLoadout);
                    EnsurePeerLifeStarted(peer.PeerId);
                    peer.IsWaitingForRespawn = false;
                    peer.IsSpawned = true;
                    Log.Info(Tag, $"Spawned remote aircraft for peer {peer.PeerId}: {peer.AircraftType}");
                }
                else
                {
                    Log.Error(Tag, $"Failed to spawn remote aircraft for peer {peer.PeerId}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Exception spawning remote aircraft for peer {peer.PeerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove a specific peer's aircraft (called on disconnect).
        /// </summary>
        public void RemovePeer(ulong peerId)
        {
            if (_peers.TryGetValue(peerId, out var peer))
            {
                Log.Info(Tag, $"Removing remote peer {peerId}");
                if (peer.Controller != null)
                {
                    UnityEngine.Object.Destroy(peer.Controller);
                }
                _spawner.DestroyAircraft(peerId);
                peer.Buffer?.Clear();
                _peers.Remove(peerId);
            }
        }

        /// <summary>
        /// Destroy every remote clone (return to lobby). Peers re-register
        /// automatically from their first state packet next round.
        /// </summary>
        public void RemoveAllPeers()
        {
            var peerIds = new List<ulong>(_peers.Keys);
            foreach (var peerId in peerIds)
                RemovePeer(peerId);
        }

        /// <summary>
        /// Respawn a peer's aircraft (after death/respawn cycle).
        /// Destroys old aircraft and creates new one on next state packet.
        /// </summary>
        public void RespawnPeer(ulong peerId, string newAircraftType = null)
        {
            if (_peers.TryGetValue(peerId, out var peer))
            {
                DespawnPeer(peerId, peer);
                peer.IsWaitingForRespawn = false;
                if (!string.IsNullOrEmpty(newAircraftType))
                {
                    peer.AircraftType = newAircraftType;
                }
                ResetSequenceOrdering(peer);
                Log.Info(Tag, $"Peer {peerId} marked for respawn ({peer.AircraftType})");
            }
        }

        /// <summary>
        /// Remove a dead peer's current clone while keeping tracking state so a
        /// later respawn or fresh state packet can spawn it again.
        /// </summary>
        public void MarkPeerDead(ulong peerId)
        {
            if (_peers.TryGetValue(peerId, out var peer))
            {
                DespawnPeer(peerId, peer);
                peer.IsWaitingForRespawn = true;
                Log.Info(Tag, $"Peer {peerId} marked dead");
            }
        }

        /// <summary>
        /// Play a peer's death on their remote clone. For destruction deaths this
        /// starts the native critical-damage sequence (critical HP, shed parts,
        /// burn, then Damage.OnDestroyed explodes and cleans up). For ejection
        /// deaths (<see cref="AircraftDestructionVfxPacket.ReasonPilotsEjected"/>)
        /// the clone's pilots eject with parachutes and the pilotless husk keeps
        /// flying until it crashes or the peer respawns.
        /// </summary>
        public void PlayPeerDeath(ulong peerId, Vector3 localPosition, Quaternion rotation, byte destructionReason,
            Vector3 velocity = default(Vector3))
        {
            if (!_peers.TryGetValue(peerId, out var peer))
            {
                peer = new RemotePeerState
                {
                    PeerId = peerId,
                    Buffer = CreateInterpolationBuffer(),
                    AircraftType = _session.GetPlayer(peerId)?.SelectedAircraft ?? "F-16C",
                    IsWaitingForRespawn = true
                };
                _peers[peerId] = peer;
                Log.Debug(Tag, $"Peer {peerId} death received before first state; waiting for respawn");
                return;
            }

            peer.IsWaitingForRespawn = true;
            peer.Buffer?.Clear();

            var aircraft = peer.Aircraft;
            if (aircraft == null)
            {
                peer.IsSpawned = false;
                Log.Debug(Tag, $"Peer {peerId} death received without a spawned clone");
                return;
            }

            try
            {
                if (peer.Controller != null)
                {
                    UnityEngine.Object.Destroy(peer.Controller);
                    peer.Controller = null;
                }

                aircraft.transform.SetPositionAndRotation(localPosition, rotation);
                var rb = aircraft.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.position = localPosition;
                    rb.rotation = rotation;
                    rb.velocity = velocity; // gibs and wrecks inherit this momentum
                }

                if (destructionReason == AircraftDestructionVfxPacket.ReasonPilotsEjected)
                {
                    ReturnWreckToNativePhysics(aircraft);
                    StartRemoteEjection(aircraft);
                    Log.Info(Tag, $"Started ejection sequence for peer {peerId}");
                }
                else if (destructionReason == AircraftDestructionVfxPacket.ReasonCriticalBurning)
                {
                    // Victim is in the critical burn phase: wreck burns and falls
                    // under the native flight model until it impacts.
                    ReturnWreckToNativePhysics(aircraft);
                    StartNativeCriticalDeath(aircraft);
                    Log.Info(Tag, $"Started critical burn sequence for peer {peerId}");
                }
                else
                {
                    // Victim's aircraft fully exploded (HP hit 0). Run the exact
                    // native death: DestroyAircraft() gibs the airframe with
                    // explosion force, spawns the air/ground/water explosion for
                    // the reason, and removes the object — same as on the
                    // victim's own screen.
                    SetDestroyedBy(aircraft, destructionReason);
                    aircraft.DestroyAircraft();
                    Log.Info(Tag, $"Exploded clone of peer {peerId} natively (reason={destructionReason})");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Native death failed for peer {peerId}: {ex.Message}");
                DespawnPeer(peerId, peer);
            }
        }

        /// <summary>
        /// Allow the native critical-damage burn/impact path to finish a dead
        /// remote clone locally. Normal live-clone damage is still blocked and
        /// forwarded to the owner.
        /// </summary>
        public bool ShouldAllowRemoteDeathSequenceDamage(Damageable damageable, DamageSource source)
        {
            if (damageable == null || source.IsCausedByWeapon)
                return false;

            var ownerAircraft = damageable.GetComponentInParent<UniAircraft>();
            foreach (var peer in _peers.Values)
            {
                if (peer.IsSpawned
                    && peer.IsWaitingForRespawn
                    && peer.Aircraft != null
                    && ownerAircraft == peer.Aircraft)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Get a remote peer's aircraft (null if not spawned)</summary>
        public UniAircraft GetAircraft(ulong peerId)
        {
            return _peers.TryGetValue(peerId, out var peer) ? peer.Aircraft : null;
        }

        /// <summary>Get a remote peer's interpolation buffer</summary>
        public InterpolationBuffer GetBuffer(ulong peerId)
        {
            return _peers.TryGetValue(peerId, out var peer) ? peer.Buffer : null;
        }

        /// <summary>Check if a peer has a spawned aircraft</summary>
        public bool HasPeer(ulong peerId)
        {
            return _peers.TryGetValue(peerId, out var peer) && peer.IsSpawned;
        }

        /// <summary>Get all remote peer IDs</summary>
        public IEnumerable<ulong> GetAllPeerIds() => _peers.Keys;

        /// <summary>Get the number of tracked remote peers</summary>
        public int PeerCount => _peers.Count;

        private void DespawnPeer(ulong peerId, RemotePeerState peer)
        {
            if (peer.Controller != null)
            {
                UnityEngine.Object.Destroy(peer.Controller);
                peer.Controller = null;
            }
            _spawner.DestroyAircraft(peerId);
            peer.Aircraft = null;
            peer.IsSpawned = false;
            peer.Buffer?.Clear();
        }

        private static void ResetSequenceOrdering(RemotePeerState peer)
        {
            peer.LastSequenceNumber = 0u;
            peer.HasReceivedSequence = false;
        }

        private static bool ShouldAcceptSequence(bool hasReceivedSequence, uint lastSequenceNumber, uint incomingSequenceNumber)
        {
            return !hasReceivedSequence || IsNewerSequence(incomingSequenceNumber, lastSequenceNumber);
        }

        private static bool IsNewerSequence(uint incomingSequenceNumber, uint lastSequenceNumber)
        {
            uint delta = unchecked(incomingSequenceNumber - lastSequenceNumber);
            return delta != 0u && delta < 0x80000000u;
        }

        private static InterpolationBuffer CreateInterpolationBuffer()
        {
            float delay = ModConfig.RemoteInterpolationDelaySeconds?.Value ?? 0.18f;
            if (delay < 0.03f) delay = 0.03f;
            if (delay > 0.35f) delay = 0.35f;
            return new InterpolationBuffer(capacity: 120, interpolationDelay: delay);
        }

        private void EnsurePeerLifeStarted(ulong peerId)
        {
            var player = _session.GetPlayer(peerId);
            if (player == null)
                return;

            if (player.LifeId == 0 || player.IsAwaitingRespawn || !player.IsAlive)
            {
                uint lifeId = _session.BeginPlayerLife(peerId);
                Log.Info(Tag, $"Peer {peerId} active life={lifeId}");
            }
        }

        private void SetDestroyedBy(UniAircraft aircraft, byte destructionReason)
        {
            if (aircraft == null || _destroyedByField == null)
                return;

            try
            {
                var enumType = typeof(UniAircraft.DestructionReason);
                int maxReason = Enum.GetValues(enumType).Length - 1;
                int clampedReason = Mathf.Clamp(destructionReason, 0, maxReason);
                _destroyedByField.SetValue(aircraft, (UniAircraft.DestructionReason)clampedReason);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Could not set destroyedBy: {ex.Message}");
            }
        }

        /// <summary>
        /// Hand a dead clone back to the native flight model. UniAircraft is
        /// disabled on live clones (the network controller drives them), but the
        /// controller is gone at death — re-enabling gives the wreck real
        /// aero/gravity physics so it falls and crashes exactly like a local
        /// dying aircraft. The AI pilot is bypassed and the engine cut so
        /// nothing flies it; an ejection husk glides pilotless natively.
        /// </summary>
        private static void ReturnWreckToNativePhysics(UniAircraft aircraft)
        {
            aircraft.IsPilotBypassed = true;
            aircraft.FlightControls = new Falcon.Controls.FlightInput(isEngineOn: false);
            aircraft.enabled = true;
        }

        /// <summary>
        /// Play a pilot ejection on a remote clone: canopy and pilots pop with
        /// parachutes, and the pilotless airframe keeps flying. EjectPilots marks
        /// the clone destroyed itself (no mod systems listen to clone destroyed
        /// events); the husk is removed by DespawnPeer when the peer respawns,
        /// or by the native crash when it hits the ground.
        /// </summary>
        private static void StartRemoteEjection(UniAircraft aircraft)
        {
            if (aircraft == null)
                return;

            if (aircraft.Target != null)
                aircraft.Target.IsDestroyed = true;

            aircraft.EjectPilots().Forget();
        }

        private void StartNativeCriticalDeath(UniAircraft aircraft)
        {
            if (aircraft == null || aircraft.Damage == null)
                return;

            int criticalDamage = GetCriticalDamage(aircraft);
            if (criticalDamage <= 0)
                criticalDamage = 1;

            aircraft.Damage.IsInvincible = false;
            if (aircraft.Damage.HitPoints > criticalDamage)
                aircraft.Damage.HitPoints = criticalDamage;
            if (aircraft.Damage.HitPoints <= 0)
                aircraft.Damage.HitPoints = 1;

            if (aircraft.Target != null)
            {
                aircraft.Target.IsDestroyed = true;
                aircraft.Target.IsCriticalHP = true;
            }

            TryStartCriticalDamageEffects(aircraft);
        }

        private int GetCriticalDamage(UniAircraft aircraft)
        {
            try
            {
                if (_criticalDamageField?.GetValue(aircraft) is int criticalDamage)
                    return criticalDamage;
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Could not read CriticalDamage: {ex.Message}");
            }

            return 1;
        }

        private static void TryStartCriticalDamageEffects(UniAircraft aircraft)
        {
            try
            {
                if (aircraft.DamageEffectsByHP == null || aircraft.Damage == null || aircraft.Damage.MaxHitpoints <= 0)
                    return;

                var source = new DamageSource(
                    1,
                    int.MaxValue,
                    0,
                    0,
                    null,
                    null,
                    aircraft.transform.position,
                    false,
                    "Fire");
                aircraft.DamageEffectsByHP.HandleReceivedDamage(source);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Could not start critical damage effects: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a given Damageable belongs to a remote clone.
        /// Used by DamagePatch to block local damage on remote aircraft.
        /// </summary>
        public bool IsRemoteCloneDamageable(Falcon.Damage.Damageable damageable)
        {
            if (damageable == null) return false;
            var ownerAircraft = damageable.GetComponentInParent<UniAircraft>();
            foreach (var peer in _peers.Values)
            {
                if (peer.IsSpawned && peer.Aircraft != null
                    && ownerAircraft == peer.Aircraft)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Check if a Target belongs to a remote aircraft.</summary>
        public bool IsRemoteTarget(Falcon.Targeting.Target target)
        {
            if (target == null) return false;
            var ownerAircraft = target.GetComponentInParent<UniAircraft>();
            foreach (var peer in _peers.Values)
            {
                if (peer.IsSpawned && peer.Aircraft != null
                    && ownerAircraft == peer.Aircraft)
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Log.Info(Tag, $"Disposing — destroying {_peers.Count} remote aircraft");
            foreach (var kvp in _peers)
            {
                try
                {
                    if (kvp.Value.Controller != null)
                    {
                        UnityEngine.Object.Destroy(kvp.Value.Controller);
                    }
                    _spawner.DestroyAircraft(kvp.Key);
                    kvp.Value.Buffer?.Clear();
                }
                catch (Exception ex)
                {
                    Log.Warning(Tag, $"Error cleaning up peer {kvp.Key}: {ex.Message}");
                }
            }
            _peers.Clear();
        }
    }
}
