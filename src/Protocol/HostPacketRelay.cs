using System;
using System.Collections.Generic;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Protocol
{
    /// <summary>
    /// Host-side packet fanout for the star topology. Clients only send to the
    /// host, so client-origin gameplay packets must be relayed to other clients.
    /// </summary>
    public sealed class HostPacketRelay : IDisposable
    {
        private static readonly HashSet<PacketType> RelayedPacketTypes = new HashSet<PacketType>
        {
            PacketType.AircraftState,
            PacketType.GunFiring,
            PacketType.GunStopped,
            PacketType.MissileLaunch,
            PacketType.RadarLock,
            PacketType.RadarLockLost,
            PacketType.BombDrop,
            PacketType.CraterSpawn,
            PacketType.BuildingDestroy,
            PacketType.MissileUpdate,
            PacketType.DamageDealt,
            PacketType.PartDestroyed,
            PacketType.AircraftChanged,
            PacketType.ExplosionSync,
            PacketType.AircraftDestructionVfx,
            PacketType.MissilePositionSync
        };

        private const string Tag = "HOST-RELAY";
        private readonly GameSession _session;
        private readonly ConnectionManager _connection;
        private readonly PacketRouter _router;
        private bool _disposed;

        public HostPacketRelay(GameSession session, ConnectionManager connection, PacketRouter router)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _router = router ?? throw new ArgumentNullException(nameof(router));

            foreach (var type in RelayedPacketTypes)
                _router.Register(type, RelayIfNeeded);
        }

        private void RelayIfNeeded(ulong fromPeerId, byte[] data)
        {
            if (_disposed || !_session.IsHost || fromPeerId == _session.LocalPeerId)
                return;
            if (data == null || data.Length == 0)
                return;

            var packetType = (PacketType)data[0];
            if (!RelayedPacketTypes.Contains(packetType))
                return;
            if (!PacketBelongsToSender(packetType, fromPeerId, data))
            {
                Log.Warning(Tag, $"Rejected spoofed {packetType} from {fromPeerId}");
                return;
            }

            if (IsReliable(packetType))
                _connection.BroadcastReliable(data, except: fromPeerId);
            else
                _connection.BroadcastUnreliable(data, except: fromPeerId);

            Log.Debug(Tag, $"Relayed {packetType} from {fromPeerId}");
        }

        private static bool IsReliable(PacketType type)
        {
            return type != PacketType.AircraftState
                && type != PacketType.MissilePositionSync;
        }

        private static bool PacketBelongsToSender(PacketType type, ulong fromPeerId, byte[] data)
        {
            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
                return false;

            switch (type)
            {
                case PacketType.AircraftState:
                    return PacketSerializer.DeserializeAircraftState(payload).PlayerId == fromPeerId;
                case PacketType.GunFiring:
                    return PacketSerializer.DeserializeWeaponFire(payload).PlayerId == fromPeerId;
                case PacketType.GunStopped:
                    return payload.Length >= sizeof(ulong) && BitConverter.ToUInt64(payload, 0) == fromPeerId;
                case PacketType.MissileLaunch:
                    return PacketSerializer.DeserializeMissileLaunch(payload).ShooterId == fromPeerId;
                case PacketType.RadarLock:
                    return PacketSerializer.DeserializeRadarLock(payload).LockerId == fromPeerId;
                case PacketType.BombDrop:
                    return PacketSerializer.DeserializeBombDrop(payload).ShooterId == fromPeerId;
                case PacketType.MissileUpdate:
                    return PacketSerializer.DeserializeMissileUpdate(payload).ShooterId == fromPeerId;
                case PacketType.DamageDealt:
                    return PacketSerializer.DeserializeDamage(payload).AttackerId == fromPeerId;
                case PacketType.AircraftChanged:
                    return PacketSerializer.DeserializeAircraftChanged(payload).PlayerId == fromPeerId;
                case PacketType.ExplosionSync:
                    return PacketSerializer.DeserializeExplosionSync(payload).ShooterId == fromPeerId;
                case PacketType.AircraftDestructionVfx:
                    return PacketSerializer.DeserializeAircraftDestructionVfx(payload).VictimId == fromPeerId;
                case PacketType.MissilePositionSync:
                    return PacketSerializer.DeserializeMissilePositionSync(payload).ShooterId == fromPeerId;
                case PacketType.CraterSpawn:
                case PacketType.BuildingDestroy:
                    return true;
                default:
                    return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var type in RelayedPacketTypes)
                _router.Unregister(type, RelayIfNeeded);
        }
    }
}
