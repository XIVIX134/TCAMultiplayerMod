using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Falcon;
using Falcon.Stores;
using Falcon.Targeting;
using Falcon.UniversalAircraft;
using TCAMultiplayer.Core;
using TCAMultiplayer.Game;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.Sync;

namespace TCAMultiplayer.Combat
{
    /// <summary>Synchronizes missile launches, tracking, and positions between peers at ~10Hz.</summary>
    public class MissileSyncSystem : IDisposable
    {
        private const string Tag = "MISSILE-SYNC";
        private const float PositionSyncInterval = 0.1f; // 10Hz
        private const float NativeMissileTimeoutCushion = 5f;
        private const float HardCorrectionDistance = 50f;
        private readonly GameSession _session;
        private readonly ConnectionManager _connection;
        private readonly PacketRouter _router;
        private readonly RemoteAircraftManager _remoteManager;
        private readonly FloatingOriginService _originService;
        private readonly RemoteMunitionSpawner _spawner;
        private readonly HashSet<int> _sentMissileLaunches = new HashSet<int>();
        private readonly Dictionary<MissileNetworkKey, Munition> _remoteMissiles = new Dictionary<MissileNetworkKey, Munition>();
        private readonly Dictionary<MissileNetworkKey, Munition> _localMissiles = new Dictionary<MissileNetworkKey, Munition>();
        private readonly Dictionary<MissileNetworkKey, float> _remoteMissileLastUpdate = new Dictionary<MissileNetworkKey, float>();
        private readonly Dictionary<MissileNetworkKey, ulong> _lastSentTargetIds = new Dictionary<MissileNetworkKey, ulong>();
        private readonly Dictionary<MissileNetworkKey, bool> _lastSentTrackingStates = new Dictionary<MissileNetworkKey, bool>();
        private readonly Dictionary<MissileNetworkKey, bool> _lastRemoteMotorStates = new Dictionary<MissileNetworkKey, bool>();
        private int _nextMissileId = 1;
        private float _lastPositionSyncTime;
        private bool _disposed;
        public MissileSyncSystem(
            GameSession session, ConnectionManager connection, PacketRouter router,
            RemoteAircraftManager remoteManager, FloatingOriginService originService)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _remoteManager = remoteManager ?? throw new ArgumentNullException(nameof(remoteManager));
            _originService = originService ?? throw new ArgumentNullException(nameof(originService));
            _spawner = new RemoteMunitionSpawner(_originService, _remoteManager, ResolveTarget);
            _router.Register(PacketType.MissileLaunch, HandleMissileLaunchRaw);
            _router.Register(PacketType.MissileUpdate, HandleMissileUpdateRaw);
            _router.Register(PacketType.MissilePositionSync, HandleMissilePositionSyncRaw);
            Log.Info(Tag, "Initialized");
        }

        public void Update()
        {
            if (_disposed) return;
            DetectNewLaunches();
            SendTrackingUpdates();
            SendPositionUpdates();
            CleanupExpiredMissiles();
            CleanupStaleMissiles();
        }

        private void DetectNewLaunches()
        {
            var launched = Munition.LaunchedMissiles;
            if (launched == null || launched.Count == 0) return;
            for (int i = 0; i < launched.Count; i++)
            {
                var missile = launched[i];
                if (missile == null || missile.HasExploded) continue;
                int unityId = missile.GetInstanceID();
                if (_sentMissileLaunches.Contains(unityId)) continue;
                if (_remoteMissiles.ContainsValue(missile)) continue; // Already tracked as remote
                if (!IsLocalMissile(missile)) continue;
                if (!missile.HasSeeker) continue; // Leave unguided munitions to BombSyncSystem
                int networkId = _nextMissileId++;
                var networkKey = MissileNetworkKey.Local(_session.LocalPeerId, networkId);
                _sentMissileLaunches.Add(unityId);
                _localMissiles[networkKey] = missile;
                ulong targetId = ResolveMissileTargetPeerId(missile);
                BroadcastLaunch(missile, networkId);
                _lastSentTargetIds[networkKey] = targetId;
                _lastSentTrackingStates[networkKey] = targetId != 0 && missile.HasSeeker && missile.Seeker.IsTracking;
                int cUid = unityId;
                var capturedKey = networkKey;
                var capturedMissile = missile;
                missile.OnDestroyed += () => OnLocalMissileDestroyed(capturedMissile, cUid, capturedKey);
                Log.Info(Tag, $"Local launch: netId={networkId} type={GetMissileTypeName(missile)} " +
                              $"targetId={targetId} missileTarget={missile.Target?.name ?? "null"} " +
                              $"seekerTarget={missile.Seeker?.Target?.name ?? "null"}");
            }
        }

        private bool IsLocalMissile(Munition missile)
        {
            var ownship = missile.Ownship;
            if (ownship == null) return false;
            foreach (var peerId in _remoteManager.GetAllPeerIds())
            {
                var aircraft = _remoteManager.GetAircraft(peerId);
                if (aircraft == null) continue;
                var peerTarget = aircraft.GetComponentInChildren<Target>();
                if (ReferenceEquals(peerTarget, ownship)) return false;
            }
            return true;
        }

        private void BroadcastLaunch(Munition missile, int networkId)
        {
            _originService.LocalToAbsolute(missile.GetUnityPosition(),
                out double absX, out double absY, out double absZ);
            var dir = missile.transform.forward;
            ulong targetId = ResolveMissileTargetPeerId(missile);
            var packet = new MissileLaunchPacket
            {
                ShooterId = _session.LocalPeerId,
                TargetId = targetId,
                IsTracking = targetId != 0 && missile.HasSeeker && missile.Seeker.IsTracking,
                MissileType = GetMissileTypeName(missile),
                SeekerType = GetSeekerTypeByte(missile),
                LaunchPosX = absX, LaunchPosY = absY, LaunchPosZ = absZ,
                LaunchDirX = dir.x, LaunchDirY = dir.y, LaunchDirZ = dir.z,
                MissileInstanceId = networkId
            };
            var payload = PacketSerializer.SerializeMissileLaunch(packet);
            _connection.BroadcastReliable(PacketSerializer.Serialize(PacketType.MissileLaunch, payload));
        }

        private void SendPositionUpdates()
        {
            if (Time.time - _lastPositionSyncTime < PositionSyncInterval) return;
            _lastPositionSyncTime = Time.time;
            var keysSnapshot = new List<MissileNetworkKey>(_localMissiles.Keys);
            foreach (var key in keysSnapshot)
            {
                if (!_localMissiles.TryGetValue(key, out var missile)) continue;
                if (missile == null || missile.HasExploded)
                {
                    BroadcastInactive(key.MissileInstanceId, MissileRemovalReason.Exploded);
                    RemoveLocalMissile(key);
                    continue;
                }
                _originService.LocalToAbsolute(missile.GetUnityPosition(),
                    out double ax, out double ay, out double az);
                // Missiles use custom physics (no Rigidbody). Read internal velocity.
                Vector3 vel = missile.GetVelocity();
                var packet = new MissilePositionSyncPacket
                {
                    ShooterId = key.ShooterId,
                    MissileInstanceId = key.MissileInstanceId,
                    PosX = ax, PosY = ay, PosZ = az,
                    VelX = vel.x, VelY = vel.y, VelZ = vel.z,
                    IsActive = true,
                    MotorActive = IsMissileMotorActive(missile)
                };
                var payload = PacketSerializer.SerializeMissilePositionSync(packet);
                _connection.BroadcastUnreliable(
                    PacketSerializer.Serialize(PacketType.MissilePositionSync, payload));
            }
        }

        private void SendTrackingUpdates()
        {
            var keysSnapshot = new List<MissileNetworkKey>(_localMissiles.Keys);
            foreach (var key in keysSnapshot)
            {
                if (!_localMissiles.TryGetValue(key, out var missile)) continue;
                if (missile == null || missile.HasExploded) continue;

                ulong targetId = ResolveMissileTargetPeerId(missile);
                bool isTracking = targetId != 0 && missile.HasSeeker && missile.Seeker.IsTracking;
                bool targetChanged = !_lastSentTargetIds.TryGetValue(key, out ulong lastTarget)
                    || lastTarget != targetId;
                bool trackingChanged = !_lastSentTrackingStates.TryGetValue(key, out bool lastTracking)
                    || lastTracking != isTracking;
                if (!targetChanged && !trackingChanged) continue;

                _lastSentTargetIds[key] = targetId;
                _lastSentTrackingStates[key] = isTracking;

                var packet = new MissileUpdatePacket
                {
                    ShooterId = _session.LocalPeerId,
                    MissileInstanceId = key.MissileInstanceId,
                    TargetId = targetId,
                    IsTracking = isTracking
                };
                var payload = PacketSerializer.SerializeMissileUpdate(packet);
                _connection.BroadcastReliable(
                    PacketSerializer.Serialize(PacketType.MissileUpdate, payload));
                Log.Debug(Tag, $"Missile update: key={key} target={targetId} tracking={isTracking}");
            }
        }

        private void BroadcastInactive(int networkId, byte reason)
        {
            var packet = new MissilePositionSyncPacket
            {
                ShooterId = _session.LocalPeerId,
                MissileInstanceId = networkId,
                IsActive = false,
                RemovalReason = reason,
                MotorActive = false
            };
            var payload = PacketSerializer.SerializeMissilePositionSync(packet);
            _connection.BroadcastReliable(
                PacketSerializer.Serialize(PacketType.MissilePositionSync, payload));
        }

        private byte[] ExtractPayload(byte[] data, string name, ulong fromPeerId)
        {
            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
                Log.Warning(Tag, $"Null {name} payload from peer {fromPeerId}");
            return payload;
        }

        private void HandleMissileLaunchRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            var payload = ExtractPayload(data, "MissileLaunch", from);
            if (payload == null) return;
            var pkt = PacketSerializer.DeserializeMissileLaunch(payload);
            if (_session.IsHost && from != _session.LocalPeerId && pkt.ShooterId != from)
            {
                Log.Warning(Tag, $"Rejected MissileLaunch from peer {from} for shooter {pkt.ShooterId}");
                return;
            }
            if (pkt.ShooterId != _session.LocalPeerId) HandleMissileLaunchPacket(pkt);
        }

        private void HandleMissileUpdateRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            var payload = ExtractPayload(data, "MissileUpdate", from);
            if (payload == null) return;
            var pkt = PacketSerializer.DeserializeMissileUpdate(payload);
            if (_session.IsHost && from != _session.LocalPeerId && pkt.ShooterId != from)
            {
                Log.Warning(Tag, $"Rejected MissileUpdate from peer {from} for shooter {pkt.ShooterId}");
                return;
            }
            if (pkt.ShooterId != _session.LocalPeerId) HandleMissileUpdatePacket(pkt);
        }

        private void HandleMissilePositionSyncRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            var payload = ExtractPayload(data, "MissilePositionSync", from);
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeMissilePositionSync(payload);
            ulong shooterId = packet.ShooterId != 0 ? packet.ShooterId : from;
            if (_session.IsHost && from != _session.LocalPeerId && shooterId != from)
            {
                Log.Warning(Tag, $"Rejected MissilePositionSync from peer {from} for shooter {shooterId}");
                return;
            }
            HandleMissilePositionSyncPacket(shooterId, packet);
        }

        private void HandleMissileLaunchPacket(MissileLaunchPacket packet)
        {
            var key = MissileNetworkKey.Remote(packet.ShooterId, packet.MissileInstanceId);
            if (_remoteMissiles.ContainsKey(key))
            {
                Log.Warning(Tag, $"Duplicate missile key={key}");
                return;
            }
            if (string.IsNullOrEmpty(packet.MissileType))
            {
                Log.Warning(Tag, $"Unknown missile type '{packet.MissileType}'");
                return;
            }
            var shooterClone = _remoteManager.GetAircraft(packet.ShooterId);
            var missile = _spawner.SpawnAndConfigureMissile(packet, shooterClone);
            if (missile == null)
            {
                Log.Warning(Tag, $"Failed to spawn remote missile key={key} type={packet.MissileType}");
                return;
            }

            _remoteMissiles[key] = missile;
            _remoteMissileLastUpdate[key] = Time.time;
            _lastRemoteMotorStates[key] = true;
            missile.OnDestroyed += () => OnRemoteMissileDestroyed(key);
            RefreshLocalThreatWarningIfTargetingLocal(packet.TargetId);
            Log.Info(Tag, $"Spawned remote missile: key={key} type={packet.MissileType} " +
                          $"shooter={packet.ShooterId} target={packet.TargetId} " +
                          $"hasSeeker={missile.HasSeeker}");
        }

        private void HandleMissileUpdatePacket(MissileUpdatePacket packet)
        {
            var key = MissileNetworkKey.Remote(packet.ShooterId, packet.MissileInstanceId);
            if (!_remoteMissiles.TryGetValue(key, out var missile)) return;
            if (missile == null || missile.HasExploded)
            {
                RemoveRemoteMissile(key);
                return;
            }
            if (packet.TargetId != 0)
            {
                Target newTarget = ResolveTarget(packet.TargetId);
                if (newTarget != null)
                {
                    missile.Target = newTarget;
                    if (missile.HasSeeker)
                        missile.Seeker.AssignTarget(newTarget);
                    RefreshLocalThreatWarningIfTargetingLocal(packet.TargetId);
                }
            }
        }

        private void HandleMissilePositionSyncPacket(ulong shooterId, MissilePositionSyncPacket packet)
        {
            var key = MissileNetworkKey.Remote(shooterId, packet.MissileInstanceId);
            if (!_remoteMissiles.TryGetValue(key, out var missile)) return;
            if (!packet.IsActive)
            {
                DestroyRemoteMissile(key, missile, packet.RemovalReason);
                return;
            }
            if (missile == null || missile.HasExploded)
            {
                RemoveRemoteMissile(key);
                return;
            }

            // Remote missiles run native Munition.FixedUpdate for motor, guidance, lifetime,
            // and threat-warning behavior. Network position is only a coarse correction.
            var targetPos = _originService.AbsoluteToLocal(packet.PosX, packet.PosY, packet.PosZ);
            bool nativeRunning = missile.enabled;
            float sqrError = (missile.transform.position - targetPos).sqrMagnitude;
            if (!nativeRunning || sqrError > HardCorrectionDistance * HardCorrectionDistance)
                missile.transform.position = targetPos;

            Vector3 vel = new Vector3(packet.VelX, packet.VelY, packet.VelZ);
            if ((!nativeRunning || sqrError > HardCorrectionDistance * HardCorrectionDistance) && vel.sqrMagnitude > 1f)
                missile.transform.rotation = Quaternion.LookRotation(vel.normalized);

            ApplyRemoteMotorState(key, missile, packet.MotorActive);
            _remoteMissileLastUpdate[key] = Time.time;
        }

        private void CleanupExpiredMissiles()
        {
            var expired = new List<MissileNetworkKey>();
            foreach (var kvp in _localMissiles)
                if (kvp.Value == null || kvp.Value.HasExploded) expired.Add(kvp.Key);
            foreach (var key in expired) RemoveLocalMissile(key);
            expired.Clear();
            foreach (var kvp in _remoteMissiles)
                if (kvp.Value == null || kvp.Value.HasExploded) expired.Add(kvp.Key);
            foreach (var key in expired) RemoveRemoteMissile(key);
        }

        private void CleanupStaleMissiles()
        {
            float now = Time.time;
            var staleKeys = new List<MissileNetworkKey>();
            foreach (var kvp in _remoteMissileLastUpdate)
            {
                if (!_remoteMissiles.TryGetValue(kvp.Key, out var missile) || missile == null)
                    continue;
                if (now - kvp.Value > GetNativeStaleTimeout(missile))
                    staleKeys.Add(kvp.Key);
            }
            foreach (var key in staleKeys)
            {
                if (_remoteMissiles.TryGetValue(key, out var missile))
                {
                    if (missile != null && missile.gameObject != null)
                        UnityEngine.Object.Destroy(missile.gameObject);
                    RemoveRemoteMissile(key);
                    Log.Debug(Tag, $"Cleaned up stale remote missile key={key}");
                }
                _remoteMissileLastUpdate.Remove(key);
            }
        }

        private void OnLocalMissileDestroyed(Munition missile, int unityId, MissileNetworkKey key)
        {
            _sentMissileLaunches.Remove(unityId);
            RemoveLocalMissile(key);
            BroadcastInactive(key.MissileInstanceId, GetRemovalReason(missile));
        }

        private void OnRemoteMissileDestroyed(MissileNetworkKey key)
        {
            RemoveRemoteMissile(key);
        }

        private void DestroyRemoteMissile(MissileNetworkKey key, Munition missile, byte reason = MissileRemovalReason.Unknown)
        {
            RemoveRemoteMissile(key);
            if (missile != null && !missile.HasExploded)
            {
                try
                {
                    if (reason == MissileRemovalReason.SelfDestruct)
                        missile.SelfDestruct();
                    else
                        UnityEngine.Object.Destroy(missile.gameObject);
                }
                catch (Exception ex)
                {
                    Log.Warning(Tag, $"Error destroying missile key={key}: {ex.Message}");
                }
            }
        }

        /// <summary>Clean up all remote missiles for a disconnected peer.</summary>
        public void CleanupPeerMissiles(ulong peerId)
        {
            var toRemove = new List<MissileNetworkKey>();
            foreach (var key in _remoteMissiles.Keys)
                if (key.ShooterId == peerId) toRemove.Add(key);
            foreach (var key in _remoteMissileLastUpdate.Keys)
                if (key.ShooterId == peerId && !toRemove.Contains(key)) toRemove.Add(key);
            foreach (var key in toRemove)
            {
                if (_remoteMissiles.TryGetValue(key, out var m)) DestroyRemoteMissile(key, m);
                else
                    RemoveRemoteMissile(key);
            }
            if (toRemove.Count > 0)
                Log.Info(Tag, $"Cleaned up {toRemove.Count} missiles for peer {peerId}");
        }

        private void RemoveLocalMissile(MissileNetworkKey key)
        {
            _localMissiles.Remove(key);
            _lastSentTargetIds.Remove(key);
            _lastSentTrackingStates.Remove(key);
        }

        private void RemoveRemoteMissile(MissileNetworkKey key)
        {
            _remoteMissiles.Remove(key);
            _remoteMissileLastUpdate.Remove(key);
            _lastRemoteMotorStates.Remove(key);
        }

        private ulong ResolvePeerId(Target target)
        {
            if (target == null) return 0;
            var ownerAircraft = target.GetComponentInParent<UniAircraft>();
            foreach (var peerId in _remoteManager.GetAllPeerIds())
            {
                var aircraft = _remoteManager.GetAircraft(peerId);
                if (aircraft == null) continue;
                if (ownerAircraft == aircraft) return peerId;
            }
            return 0;
        }

        private ulong ResolveMissileTargetPeerId(Munition missile)
        {
            if (missile == null) return 0;

            ulong targetId = ResolvePeerId(missile.Target);
            if (targetId != 0) return targetId;

            if (missile.HasSeeker && missile.Seeker != null)
                return ResolvePeerId(missile.Seeker.Target);

            return 0;
        }

        private void RefreshLocalThreatWarningIfTargetingLocal(ulong targetId)
        {
            if (targetId != _session.LocalPeerId)
                return;

            try
            {
                var localAircraft = FindLocalPlayerAircraft();
                if (localAircraft?.ThreatWarning == null)
                    return;

                localAircraft.ThreatWarning.IsActive = true;
                localAircraft.ThreatWarning.Refresh();
            }
            catch (Exception ex)
            {
                Log.Debug(Tag, $"Threat warning refresh failed: {ex.Message}");
            }
        }

        private Target ResolveTarget(ulong peerId)
        {
            if (peerId == 0) return null;
            if (peerId == _session.LocalPeerId) return FindLocalPlayerTarget();
            var aircraft = _remoteManager.GetAircraft(peerId);
            return aircraft != null ? aircraft.GetComponentInChildren<Target>() : null;
        }

        private Target FindLocalPlayerTarget()
        {
            var playerAircraft = FindLocalPlayerAircraft();
            if (playerAircraft != null)
            {
                var playerTarget = playerAircraft.GetComponentInChildren<Target>();
                if (playerTarget != null) return playerTarget;
            }

            return null;
        }

        private UniAircraft FindLocalPlayerAircraft()
        {
            if (UniAircraft.Player != null && !IsRemoteClone(UniAircraft.Player))
                return UniAircraft.Player;

            var allAircraft = UnityEngine.Object.FindObjectsByType<UniAircraft>(FindObjectsSortMode.None);
            foreach (var ac in allAircraft)
            {
                if (IsRemoteClone(ac)) continue;
                if (ac.GetComponentInChildren<Target>() != null) return ac;
            }
            return null;
        }

        private bool IsRemoteClone(UniAircraft aircraft)
        {
            foreach (var peerId in _remoteManager.GetAllPeerIds())
                if (_remoteManager.GetAircraft(peerId) == aircraft) return true;
            return false;
        }

        private static byte GetRemovalReason(Munition missile)
        {
            if (missile == null)
                return MissileRemovalReason.Unknown;
            if (missile.HasExploded)
                return MissileRemovalReason.Exploded;
            if (IsNativeSelfDestruct(missile))
                return MissileRemovalReason.SelfDestruct;
            return MissileRemovalReason.Unknown;
        }

        private void ApplyRemoteMotorState(MissileNetworkKey key, Munition missile, bool motorActive)
        {
            if (missile == null)
                return;

            if (_lastRemoteMotorStates.TryGetValue(key, out bool lastMotorActive)
                && lastMotorActive == motorActive)
            {
                return;
            }

            _lastRemoteMotorStates[key] = motorActive;
            if (motorActive)
                return;

            StopMissileMotors(missile);
            Log.Debug(Tag, $"Remote missile motor burnout key={key}");
        }

        private static bool IsMissileMotorActive(Munition missile)
        {
            if (!MotorReflection.IsAvailable)
                return EstimateMotorActiveFromData(missile);

            try
            {
                var motors = GetMissileMotors(missile);
                if (motors == null)
                    return EstimateMotorActiveFromData(missile);

                foreach (var motor in motors)
                {
                    if (motor == null)
                        continue;

                    if ((bool)MotorReflection.IsFiringProperty.GetValue(motor, null))
                        return true;
                    if ((bool)MotorReflection.IsPrimedProperty.GetValue(motor, null))
                        return true;
                }
            }
            catch
            {
                return EstimateMotorActiveFromData(missile);
            }

            return false;
        }

        private static void StopMissileMotors(Munition missile)
        {
            try
            {
                var motors = GetMissileMotors(missile);
                if (motors == null)
                    return;

                foreach (var motor in motors)
                {
                    try
                    {
                        MotorReflection.ForceBurnedOut(motor);
                        MotorReflection.DestroyMethod.Invoke(motor, null);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static System.Collections.IEnumerable GetMissileMotors(Munition missile)
        {
            if (missile == null || MotorReflection.MotorsField == null)
                return null;

            return MotorReflection.MotorsField.GetValue(missile) as System.Collections.IEnumerable;
        }

        private static bool EstimateMotorActiveFromData(Munition missile)
        {
            try
            {
                if (missile?.Data?.MotorStages == null)
                    return false;

                foreach (var stage in missile.Data.MotorStages)
                {
                    if (stage == null)
                        continue;
                    if (missile.SecondsSinceLaunch <= stage.Delay + stage.Duration)
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool IsNativeSelfDestruct(Munition missile)
        {
            try
            {
                if (missile?.Data?.Warhead == null)
                    return false;
                return missile.SecondsSinceLaunch >= missile.Data.Warhead.SelfDestruct - 0.25f;
            }
            catch
            {
                return false;
            }
        }

        private static float GetNativeStaleTimeout(Munition missile)
        {
            try
            {
                if (missile?.Data?.Warhead == null)
                    return 30f + NativeMissileTimeoutCushion;
                return missile.Data.Warhead.SelfDestruct + NativeMissileTimeoutCushion;
            }
            catch
            {
                return 30f + NativeMissileTimeoutCushion;
            }
        }

        private static string GetMissileTypeName(Munition missile)
        {
            string name = missile.name;
            if (string.IsNullOrEmpty(name)) return "Unknown";
            int idx = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return idx >= 0 ? name.Substring(0, idx).TrimEnd() : name;
        }

        private static byte GetSeekerTypeByte(Munition missile)
        {
            if (!missile.HasSeeker) return 2;
            var guidance = missile.SeekerSignature;
            if (guidance == GuidanceType.Infrared) return 0;
            if (guidance == GuidanceType.ActiveRadar) return 1;
            return 2;
        }

        private static class MissileRemovalReason
        {
            public const byte Unknown = 0;
            public const byte Exploded = 1;
            public const byte SelfDestruct = 2;
        }

        private static class MotorReflection
        {
            public static readonly FieldInfo MotorsField =
                typeof(Munition).GetField("Motors", BindingFlags.Instance | BindingFlags.NonPublic);

            private static readonly Type MotorType = Type.GetType("Falcon.Stores.Motor, Assembly-CSharp");

            public static readonly PropertyInfo IsPrimedProperty =
                MotorType?.GetProperty("IsPrimed", BindingFlags.Instance | BindingFlags.Public);

            public static readonly PropertyInfo IsFiringProperty =
                MotorType?.GetProperty("IsFiring", BindingFlags.Instance | BindingFlags.Public);

            public static readonly MethodInfo DestroyMethod =
                MotorType?.GetMethod("Destroy", BindingFlags.Instance | BindingFlags.Public);

            private static readonly BindingFlags InstanceFields =
                BindingFlags.Instance | BindingFlags.NonPublic;

            private static readonly FieldInfo ThrustField =
                MotorType?.GetField("<Thrust>k__BackingField", InstanceFields);

            private static readonly FieldInfo IsPrimedField =
                MotorType?.GetField("<IsPrimed>k__BackingField", InstanceFields);

            private static readonly FieldInfo IsBurnedOutField =
                MotorType?.GetField("<IsBurnedOut>k__BackingField", InstanceFields);

            public static bool IsAvailable =>
                MotorsField != null
                && IsPrimedProperty != null
                && IsFiringProperty != null
                && DestroyMethod != null;

            public static void ForceBurnedOut(object motor)
            {
                if (motor == null)
                    return;

                ThrustField?.SetValue(motor, 0f);
                IsPrimedField?.SetValue(motor, false);
                IsBurnedOutField?.SetValue(motor, true);
            }
        }

        private struct MissileNetworkKey : IEquatable<MissileNetworkKey>
        {
            public readonly ulong ShooterId;
            public readonly int MissileInstanceId;

            private MissileNetworkKey(ulong shooterId, int missileInstanceId)
            {
                ShooterId = shooterId;
                MissileInstanceId = missileInstanceId;
            }

            public static MissileNetworkKey Local(ulong shooterId, int missileInstanceId)
            {
                return new MissileNetworkKey(shooterId, missileInstanceId);
            }

            public static MissileNetworkKey Remote(ulong shooterId, int missileInstanceId)
            {
                return new MissileNetworkKey(shooterId, missileInstanceId);
            }

            public bool Equals(MissileNetworkKey other)
            {
                return ShooterId == other.ShooterId && MissileInstanceId == other.MissileInstanceId;
            }

            public override bool Equals(object obj)
            {
                return obj is MissileNetworkKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((int)ShooterId * 397) ^ (int)(ShooterId >> 32) ^ MissileInstanceId;
                }
            }

            public override string ToString()
            {
                return $"{ShooterId}:{MissileInstanceId}";
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _router.Unregister(PacketType.MissileLaunch, HandleMissileLaunchRaw);
            _router.Unregister(PacketType.MissileUpdate, HandleMissileUpdateRaw);
            _router.Unregister(PacketType.MissilePositionSync, HandleMissilePositionSyncRaw);
            foreach (var kvp in _remoteMissiles)
            {
                if (kvp.Value != null && !kvp.Value.HasExploded)
                {
                    try { UnityEngine.Object.Destroy(kvp.Value.gameObject); }
                    catch (Exception ex)
                    {
                        Log.Warning(Tag, $"Error disposing missile netId={kvp.Key}: {ex.Message}");
                    }
                }
            }
            _remoteMissiles.Clear();
            _remoteMissileLastUpdate.Clear();
            _localMissiles.Clear();
            _lastSentTargetIds.Clear();
            _lastSentTrackingStates.Clear();
            _lastRemoteMotorStates.Clear();
            _sentMissileLaunches.Clear();
            Log.Info(Tag, "Disposed");
        }
    }
}
