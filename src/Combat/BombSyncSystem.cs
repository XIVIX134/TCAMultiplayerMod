using System;
using System.Collections.Generic;
using UnityEngine;
using Falcon;
using Falcon.Stores;
using TCAMultiplayer.Core;
using TCAMultiplayer.Game;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.Sync;

namespace TCAMultiplayer.Combat
{
    /// <summary>Synchronizes unguided bomb drops between peers (fire-and-forget — no position sync).</summary>
    public class BombSyncSystem : IDisposable
    {
        private const string Tag = "BOMB-SYNC";
        private readonly GameSession _session;
        private readonly ConnectionManager _connection;
        private readonly PacketRouter _router;
        private readonly RemoteAircraftManager _remoteManager;
        private readonly FloatingOriginService _originService;
        private readonly RemoteMunitionSpawner _spawner;
        private readonly HashSet<int> _sentBombDrops = new HashSet<int>();
        private readonly Dictionary<int, Munition> _localBombs = new Dictionary<int, Munition>();
        private readonly Dictionary<BombKey, Munition> _remoteBombs = new Dictionary<BombKey, Munition>();
        private readonly Dictionary<BombKey, ulong> _remoteShooters = new Dictionary<BombKey, ulong>();
        private int _nextBombId = 1;
        private bool _disposed;

        public BombSyncSystem(
            GameSession session, ConnectionManager connection, PacketRouter router,
            RemoteAircraftManager remoteManager, FloatingOriginService originService)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _remoteManager = remoteManager ?? throw new ArgumentNullException(nameof(remoteManager));
            _originService = originService ?? throw new ArgumentNullException(nameof(originService));
            _spawner = new RemoteMunitionSpawner(_originService, _remoteManager);
            _router.Register(PacketType.BombDrop, HandleBombDropRaw);
            Log.Info(Tag, "Initialized");
        }

        public void Update()
        {
            if (_disposed) return;
            DetectNewDrops();
            CleanupExpiredBombs();
        }

        private void DetectNewDrops()
        {
            var launched = Munition.LaunchedMunitions;
            if (launched == null || launched.Count == 0) return;
            for (int i = 0; i < launched.Count; i++)
            {
                var munition = launched[i];
                if (munition == null || munition.HasExploded) continue;
                if (munition.HasSeeker) continue; // Guided munitions handled by MissileSyncSystem
                int unityId = munition.GetInstanceID();
                if (_sentBombDrops.Contains(unityId)) continue;
                if (_remoteBombs.ContainsValue(munition)) continue; // Already tracked as remote
                if (!IsLocalMunition(munition)) continue;
                int networkId = _nextBombId++;
                _sentBombDrops.Add(unityId);
                _localBombs[networkId] = munition;
                BroadcastDrop(munition, networkId, true, BombRemovalReason.Unknown);
                int cUid = unityId, cNid = networkId;
                var capturedBomb = munition;
                munition.OnDestroyed += () => OnLocalBombDestroyed(capturedBomb, cUid, cNid);
                Log.Info(Tag, $"Local bomb drop: netId={networkId} type={GetBombTypeName(munition)}");
            }
        }

        private bool IsLocalMunition(Munition munition)
        {
            var ownship = munition.Ownship;
            if (ownship == null) return false;
            foreach (var peerId in _remoteManager.GetAllPeerIds())
            {
                var aircraft = _remoteManager.GetAircraft(peerId);
                if (aircraft == null) continue;
                var peerTarget = aircraft.GetComponentInChildren<Falcon.Targeting.Target>();
                if (ReferenceEquals(peerTarget, ownship)) return false;
            }
            return true;
        }

        private void BroadcastDrop(Munition munition, int networkId, bool isActive, byte removalReason)
        {
            double absX = 0, absY = 0, absZ = 0;
            Vector3 vel = Vector3.zero;
            float timestamp = Time.time;
            string bombType = "Unknown";

            if (munition != null)
            {
                _originService.LocalToAbsolute(munition.GetUnityPosition(),
                    out absX, out absY, out absZ);
                // Bombs use custom physics (no Rigidbody). Use Munition's internal velocity.
                vel = munition.GetVelocity();
                bombType = GetBombTypeName(munition);
            }

            var packet = new BombDropPacket
            {
                ShooterId = _session.LocalPeerId,
                BombType = bombType,
                BombInstanceId = networkId,
                LaunchPosX = absX, LaunchPosY = absY, LaunchPosZ = absZ,
                VelX = vel.x, VelY = vel.y, VelZ = vel.z,
                Timestamp = timestamp,
                IsActive = isActive,
                RemovalReason = removalReason
            };
            var payload = PacketSerializer.SerializeBombDrop(packet);
            _connection.BroadcastReliable(PacketSerializer.Serialize(PacketType.BombDrop, payload));
        }

        private byte[] ExtractPayload(byte[] data, ulong fromPeerId)
        {
            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
                Log.Warning(Tag, $"Null BombDrop payload from peer {fromPeerId}");
            return payload;
        }

        private void HandleBombDropRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            var payload = ExtractPayload(data, from);
            if (payload == null) return;
            var pkt = PacketSerializer.DeserializeBombDrop(payload);
            if (_session.IsHost && from != _session.LocalPeerId && pkt.ShooterId != from)
            {
                Log.Warning(Tag, $"Rejected BombDrop from peer {from} for shooter {pkt.ShooterId}");
                return;
            }
            if (pkt.ShooterId != _session.LocalPeerId) HandleBombDropPacket(pkt);
        }

        private void HandleBombDropPacket(BombDropPacket packet)
        {
            var key = BombKey.FromPacket(packet);
            if (!packet.IsActive)
            {
                if (!key.HasNetworkId)
                    return;
                DestroyRemoteBomb(key, packet.RemovalReason);
                return;
            }

            if (key.HasNetworkId && _remoteBombs.ContainsKey(key))
            {
                Log.Warning(Tag, $"Duplicate bomb netId={packet.BombInstanceId} shooter={packet.ShooterId}");
                return;
            }

            if (string.IsNullOrEmpty(packet.BombType))
            {
                Log.Warning(Tag, $"Unknown bomb type '{packet.BombType}'");
                return;
            }

            var shooterClone = _remoteManager.GetAircraft(packet.ShooterId);
            var bomb = _spawner.SpawnAndConfigureBomb(packet, shooterClone);
            if (bomb == null)
            {
                Log.Warning(Tag, $"Failed to spawn remote bomb type='{packet.BombType}'");
                return;
            }

            if (key.HasNetworkId)
            {
                _remoteBombs[key] = bomb;
                _remoteShooters[key] = packet.ShooterId;
                var cKey = key;
                bomb.OnDestroyed += () => OnRemoteBombDestroyed(cKey);
            }
            Log.Info(Tag, $"Spawned remote bomb: netId={packet.BombInstanceId} type={packet.BombType} shooter={packet.ShooterId}");
        }

        private void CleanupExpiredBombs()
        {
            var expired = new List<int>();
            foreach (var kvp in _localBombs)
                if (kvp.Value == null || kvp.Value.HasExploded) expired.Add(kvp.Key);
            foreach (int id in expired) _localBombs.Remove(id);

            var remoteExpired = new List<BombKey>();
            foreach (var kvp in _remoteBombs)
                if (kvp.Value == null || kvp.Value.HasExploded) remoteExpired.Add(kvp.Key);
            foreach (var id in remoteExpired)
            {
                _remoteBombs.Remove(id);
                _remoteShooters.Remove(id);
            }
        }

        private void OnLocalBombDestroyed(Munition bomb, int unityId, int networkId)
        {
            _sentBombDrops.Remove(unityId);
            _localBombs.Remove(networkId);
            BroadcastDrop(bomb, networkId, false, GetRemovalReason(bomb));
        }

        private void OnRemoteBombDestroyed(BombKey key)
        {
            _remoteBombs.Remove(key);
            _remoteShooters.Remove(key);
        }

        /// <summary>Clean up all remote bombs for a disconnected peer.</summary>
        public void CleanupPeerBombs(ulong peerId)
        {
            var toRemove = new List<BombKey>();
            foreach (var kvp in _remoteShooters)
                if (kvp.Value == peerId) toRemove.Add(kvp.Key);
            foreach (var id in toRemove)
            {
                if (_remoteBombs.TryGetValue(id, out var bomb))
                {
                    _remoteBombs.Remove(id);
                    _remoteShooters.Remove(id);
                    if (bomb != null && !bomb.HasExploded)
                    {
                        try { UnityEngine.Object.Destroy(bomb.gameObject); }
                        catch (Exception ex)
                        {
                            Log.Warning(Tag, $"Error destroying bomb netId={id.BombInstanceId} shooter={id.ShooterId}: {ex.Message}");
                        }
                    }
                }
            }
            if (toRemove.Count > 0)
                Log.Info(Tag, $"Cleaned up {toRemove.Count} bombs for peer {peerId}");
        }

        private void DestroyRemoteBomb(BombKey key, byte reason)
        {
            if (!_remoteBombs.TryGetValue(key, out var bomb))
                return;

            _remoteBombs.Remove(key);
            _remoteShooters.Remove(key);

            if (bomb == null || bomb.HasExploded)
                return;

            try
            {
                if (reason == BombRemovalReason.SelfDestruct)
                    bomb.SelfDestruct();
                else
                    UnityEngine.Object.Destroy(bomb.gameObject);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Error destroying bomb netId={key.BombInstanceId} shooter={key.ShooterId}: {ex.Message}");
            }
        }

        private static string GetBombTypeName(Munition munition)
        {
            string name = munition.name;
            if (string.IsNullOrEmpty(name)) return "Unknown";
            int idx = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return idx >= 0 ? name.Substring(0, idx).TrimEnd() : name;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _router.Unregister(PacketType.BombDrop, HandleBombDropRaw);
            foreach (var kvp in _remoteBombs)
            {
                if (kvp.Value != null && !kvp.Value.HasExploded)
                {
                    try { UnityEngine.Object.Destroy(kvp.Value.gameObject); }
                    catch (Exception ex)
                    {
                        Log.Warning(Tag, $"Error disposing bomb netId={kvp.Key.BombInstanceId} shooter={kvp.Key.ShooterId}: {ex.Message}");
                    }
                }
            }
            _remoteBombs.Clear();
            _remoteShooters.Clear();
            _sentBombDrops.Clear();
            _localBombs.Clear();
            Log.Info(Tag, "Disposed");
        }

        private static byte GetRemovalReason(Munition bomb)
        {
            if (bomb == null)
                return BombRemovalReason.Unknown;
            if (bomb.HasExploded)
                return BombRemovalReason.Exploded;
            if (IsNativeSelfDestruct(bomb))
                return BombRemovalReason.SelfDestruct;
            return BombRemovalReason.Unknown;
        }

        private static bool IsNativeSelfDestruct(Munition bomb)
        {
            try
            {
                if (bomb?.Data?.Warhead == null)
                    return false;
                return bomb.SecondsSinceLaunch >= bomb.Data.Warhead.SelfDestruct - 0.25f;
            }
            catch
            {
                return false;
            }
        }

        private static class BombRemovalReason
        {
            public const byte Unknown = 0;
            public const byte Exploded = 1;
            public const byte SelfDestruct = 2;
        }

        private struct BombKey : IEquatable<BombKey>
        {
            public readonly ulong ShooterId;
            public readonly int BombInstanceId;
            public bool HasNetworkId => BombInstanceId != 0;

            private BombKey(ulong shooterId, int bombInstanceId)
            {
                ShooterId = shooterId;
                BombInstanceId = bombInstanceId;
            }

            public static BombKey FromPacket(BombDropPacket packet)
            {
                return new BombKey(packet.ShooterId, packet.BombInstanceId);
            }

            public bool Equals(BombKey other)
            {
                return ShooterId == other.ShooterId && BombInstanceId == other.BombInstanceId;
            }

            public override bool Equals(object obj)
            {
                return obj is BombKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((int)ShooterId * 397) ^ BombInstanceId;
                }
            }
        }
    }
}
