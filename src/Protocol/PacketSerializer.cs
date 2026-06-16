using System;
using System.IO;
using UnityEngine;

namespace TCAMultiplayer.Protocol
{
    /// <summary>
    /// Serialization utilities for network packets.
    /// Every Deserialize method validates input length, catches truncation,
    /// and returns default (never throws) for malformed data.
    /// </summary>
    public static class PacketSerializer
    {
        private const int MaxPlayerCount = 8;
        private const int MaxManifestBytes = 1024 * 1024; // 1 MB
        private const int MaxModSyncChunkBytes = 64 * 1024;
        private const string Tag = "[PacketSerializer]";

        /// <summary>
        /// Safe wrapper around Debug.LogWarning that gracefully degrades
        /// when Unity runtime is not available (e.g. unit test environment).
        /// </summary>
        private static void LogWarning(string message)
        {
            try
            {
                Debug.LogWarning(message);
            }
            catch (TypeLoadException) { /* Unity runtime unavailable (test env) */ }
            catch (MissingMethodException) { /* Unity runtime unavailable (test env) */ }
        }

        // ═══════════════════════════════════════════════════════════
        //  Framing: [PacketType byte][payload bytes…]
        // ═══════════════════════════════════════════════════════════

        public static byte[] Serialize(PacketType type, byte[] payload = null)
        {
            int length = 1 + (payload?.Length ?? 0);
            byte[] result = new byte[length];
            result[0] = (byte)type;

            if (payload != null && payload.Length > 0)
            {
                Array.Copy(payload, 0, result, 1, payload.Length);
            }

            return result;
        }

        public static (PacketType type, byte[] payload) Deserialize(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                LogWarning($"{Tag} Empty packet data");
                return ((PacketType)0, null);
            }

            PacketType type = (PacketType)data[0];
            byte[] payload = null;

            if (data.Length > 1)
            {
                payload = new byte[data.Length - 1];
                Array.Copy(data, 1, payload, 0, payload.Length);
            }

            return (type, payload);
        }

        // ═══════════════════════════════════════════════════════════
        //  Safe deserialization helper
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Wraps deserialization in length check + try/catch.
        /// Returns default(T) on failure (null for classes, zero-init for structs).
        /// </summary>
        private static T SafeRead<T>(byte[] data, int minBytes, string name, Func<BinaryReader, T> read)
        {
            if (data == null || data.Length < minBytes)
            {
                LogWarning($"{Tag} Truncated {name}: expected >= {minBytes} bytes, got {data?.Length ?? 0}");
                return default;
            }
            try
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    return read(reader);
                }
            }
            catch (EndOfStreamException)
            {
                LogWarning($"{Tag} Malformed {name}: unexpected end of stream ({data.Length} bytes)");
                return default;
            }
            catch (Exception ex)
            {
                LogWarning($"{Tag} Failed to deserialize {name}: {ex.Message}");
                return default;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  AircraftState (10) — high-frequency, most critical
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeAircraftState(AircraftStatePacket state)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(state.PlayerId);
                w.Write(state.SequenceNumber);
                w.Write(state.AircraftType ?? string.Empty);
                w.Write(state.PosX);
                w.Write(state.PosY);
                w.Write(state.PosZ);
                w.Write(state.RotX);
                w.Write(state.RotY);
                w.Write(state.RotZ);
                w.Write(state.RotW);
                w.Write(state.VelX);
                w.Write(state.VelY);
                w.Write(state.VelZ);
                w.Write(state.AngVelX);
                w.Write(state.AngVelY);
                w.Write(state.AngVelZ);
                w.Write(state.Throttle);
                w.Write(state.Pitch);
                w.Write(state.Roll);
                w.Write(state.Yaw);
                w.Write(state.NozzleAngle);
                w.Write(state.SpeedKIAS);
                w.Write(state.BrakeState);
                w.Write(state.Flags);
                w.Write(state.Timestamp);
                return ms.ToArray();
            }
        }

        public static AircraftStatePacket DeserializeAircraftState(byte[] data)
        {
            // Min: PlayerId(8) + Seq(4) + string(1+) = 13
            return SafeRead(data, 13, nameof(AircraftStatePacket), r =>
            {
                var p = new AircraftStatePacket
                {
                    PlayerId = r.ReadUInt64(),
                    SequenceNumber = r.ReadUInt32(),
                    AircraftType = r.ReadString(),
                    PosX = r.ReadDouble(),
                    PosY = r.ReadDouble(),
                    PosZ = r.ReadDouble(),
                    RotX = r.ReadSingle(),
                    RotY = r.ReadSingle(),
                    RotZ = r.ReadSingle(),
                    RotW = r.ReadSingle(),
                    VelX = r.ReadSingle(),
                    VelY = r.ReadSingle(),
                    VelZ = r.ReadSingle(),
                    AngVelX = r.ReadSingle(),
                    AngVelY = r.ReadSingle(),
                    AngVelZ = r.ReadSingle(),
                    Throttle = r.ReadSingle(),
                    Pitch = r.ReadSingle(),
                    Roll = r.ReadSingle(),
                    Yaw = r.ReadSingle(),
                    NozzleAngle = r.ReadSingle(),
                };

                var ms = r.BaseStream;
                long remaining = ms.Length - ms.Position;
                if (remaining >= 13)
                {
                    p.SpeedKIAS = r.ReadSingle();
                    p.BrakeState = r.ReadSingle();
                    p.Flags = r.ReadByte();
                    p.Timestamp = r.ReadSingle();
                }
                else if (remaining >= 5)
                {
                    p.Flags = r.ReadByte();
                    p.Timestamp = r.ReadSingle();
                }
                else
                {
                    throw new EndOfStreamException("AircraftState missing flags/timestamp");
                }
                return p;
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  WeaponFire (30)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeWeaponFire(WeaponFirePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PlayerId);
                w.Write(packet.WeaponType);
                w.Write(packet.WeaponIndex);
                w.Write(packet.TargetId);
                return ms.ToArray();
            }
        }

        public static WeaponFirePacket DeserializeWeaponFire(byte[] data)
        {
            // PlayerId(8) + WeaponType(1) + WeaponIndex(1) + TargetId(8) = 18
            return SafeRead(data, 18, nameof(WeaponFirePacket), r => new WeaponFirePacket
            {
                PlayerId = r.ReadUInt64(),
                WeaponType = r.ReadByte(),
                WeaponIndex = r.ReadByte(),
                TargetId = r.ReadUInt64()
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  Damage (40)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeDamage(DamagePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.VictimId);
                w.Write(packet.AttackerId);
                w.Write(packet.Damage);
                w.Write(packet.Penetration);
                w.Write(packet.DamageType);
                w.Write(packet.HitPosX);
                w.Write(packet.HitPosY);
                w.Write(packet.HitPosZ);
                w.Write(packet.WeaponName ?? "Unknown");
                w.Write(packet.AttackerLifeId);
                w.Write(packet.DamageSequence);
                w.Write(packet.HitPartName ?? "");
                return ms.ToArray();
            }
        }

        public static DamagePacket DeserializeDamage(byte[] data)
        {
            // VictimId(8) + AttackerId(8) + Damage(4) + Pen(4) + DmgType(1) + HitPos(24) + string(1+) = 50
            return SafeRead(data, 50, nameof(DamagePacket), r =>
            {
                var packet = new DamagePacket
                {
                    VictimId = r.ReadUInt64(),
                    AttackerId = r.ReadUInt64(),
                    Damage = r.ReadInt32(),
                    Penetration = r.ReadInt32(),
                    DamageType = r.ReadByte(),
                    HitPosX = r.ReadDouble(),
                    HitPosY = r.ReadDouble(),
                    HitPosZ = r.ReadDouble(),
                    WeaponName = r.ReadString()
                };

                // Backward compat: event identity added after the variable-length weapon name.
                if (r.BaseStream.Position + sizeof(uint) * 2 <= r.BaseStream.Length)
                {
                    packet.AttackerLifeId = r.ReadUInt32();
                    packet.DamageSequence = r.ReadUInt32();
                }

                // Backward compat: hit part name added after event identity.
                if (r.BaseStream.Position < r.BaseStream.Length)
                    packet.HitPartName = r.ReadString();

                return packet;
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  PartDestroyed (81)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializePartDestroyed(PartDestroyedPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.VictimId);
                w.Write(packet.PartName ?? "");
                return ms.ToArray();
            }
        }

        public static PartDestroyedPacket DeserializePartDestroyed(byte[] data)
        {
            // VictimId(8) + string(1+) = 9
            return SafeRead(data, 9, nameof(PartDestroyedPacket), r => new PartDestroyedPacket
            {
                VictimId = r.ReadUInt64(),
                PartName = r.ReadString()
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  MissileLaunch (32)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeMissileLaunch(MissileLaunchPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.ShooterId);
                w.Write(packet.TargetId);
                w.Write(packet.IsTracking);
                w.Write(packet.MissileType ?? "Unknown");
                w.Write(packet.SeekerType);
                w.Write(packet.LaunchPosX);
                w.Write(packet.LaunchPosY);
                w.Write(packet.LaunchPosZ);
                w.Write(packet.LaunchDirX);
                w.Write(packet.LaunchDirY);
                w.Write(packet.LaunchDirZ);
                w.Write(packet.MissileInstanceId);
                return ms.ToArray();
            }
        }

        public static MissileLaunchPacket DeserializeMissileLaunch(byte[] data)
        {
            // ShooterId(8) + TargetId(8) + IsTracking(1) + string(1+) + Seeker(1) + Pos(24) + Dir(12) = 55
            return SafeRead(data, 55, nameof(MissileLaunchPacket), r =>
            {
                var p = new MissileLaunchPacket
                {
                    ShooterId = r.ReadUInt64(),
                    TargetId = r.ReadUInt64(),
                    IsTracking = r.ReadBoolean(),
                    MissileType = r.ReadString(),
                    SeekerType = r.ReadByte(),
                    LaunchPosX = r.ReadDouble(),
                    LaunchPosY = r.ReadDouble(),
                    LaunchPosZ = r.ReadDouble(),
                    LaunchDirX = r.ReadSingle(),
                    LaunchDirY = r.ReadSingle(),
                    LaunchDirZ = r.ReadSingle()
                };

                // Backward compat: MissileInstanceId added later
                if (r.BaseStream.Position < r.BaseStream.Length)
                {
                    p.MissileInstanceId = r.ReadInt32();
                }

                return p;
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  MissileUpdate (39)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeMissileUpdate(MissileUpdatePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.ShooterId);
                w.Write(packet.MissileInstanceId);
                w.Write(packet.TargetId);
                w.Write(packet.IsTracking);
                return ms.ToArray();
            }
        }

        public static MissileUpdatePacket DeserializeMissileUpdate(byte[] data)
        {
            // ShooterId(8) + InstanceId(4) + TargetId(8) + IsTracking(1) = 21
            return SafeRead(data, 21, nameof(MissileUpdatePacket), r => new MissileUpdatePacket
            {
                ShooterId = r.ReadUInt64(),
                MissileInstanceId = r.ReadInt32(),
                TargetId = r.ReadUInt64(),
                IsTracking = r.ReadBoolean()
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  MissilePositionSync (80)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeMissilePositionSync(MissilePositionSyncPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.MissileInstanceId);
                w.Write(packet.PosX);
                w.Write(packet.PosY);
                w.Write(packet.PosZ);
                w.Write(packet.VelX);
                w.Write(packet.VelY);
                w.Write(packet.VelZ);
                w.Write(packet.IsActive);
                w.Write(packet.RemovalReason);
                w.Write(packet.MotorActive);
                w.Write(packet.ShooterId);
                return ms.ToArray();
            }
        }

        public static MissilePositionSyncPacket DeserializeMissilePositionSync(byte[] data)
        {
            // InstanceId(4) + Pos(24) + Vel(12) + IsActive(1) + optional RemovalReason(1) + optional MotorActive(1)
            return SafeRead(data, 41, nameof(MissilePositionSyncPacket), r =>
            {
                var p = new MissilePositionSyncPacket
                {
                    MissileInstanceId = r.ReadInt32(),
                    PosX = r.ReadDouble(),
                    PosY = r.ReadDouble(),
                    PosZ = r.ReadDouble(),
                    VelX = r.ReadSingle(),
                    VelY = r.ReadSingle(),
                    VelZ = r.ReadSingle(),
                    IsActive = r.ReadBoolean()
                };
                if (r.BaseStream.Position < r.BaseStream.Length)
                    p.RemovalReason = r.ReadByte();
                p.MotorActive = r.BaseStream.Position < r.BaseStream.Length
                    ? r.ReadBoolean()
                    : p.IsActive;
                if (r.BaseStream.Position + sizeof(ulong) <= r.BaseStream.Length)
                    p.ShooterId = r.ReadUInt64();
                return p;
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  RadarLock (33)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeRadarLock(RadarLockPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.LockerId);
                w.Write(packet.TargetId);
                w.Write(packet.IsLocked);
                w.Write(packet.LockType);
                return ms.ToArray();
            }
        }

        public static RadarLockPacket DeserializeRadarLock(byte[] data)
        {
            // LockerId(8) + TargetId(8) + IsLocked(1) + LockType(1) = 18
            return SafeRead(data, 18, nameof(RadarLockPacket), r => new RadarLockPacket
            {
                LockerId = r.ReadUInt64(),
                TargetId = r.ReadUInt64(),
                IsLocked = r.ReadBoolean(),
                LockType = r.ReadByte()
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  BombDrop (36)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeBombDrop(BombDropPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.ShooterId);
                w.Write(packet.BombType ?? "Unknown");
                w.Write(packet.BombInstanceId);
                w.Write(packet.LaunchPosX);
                w.Write(packet.LaunchPosY);
                w.Write(packet.LaunchPosZ);
                w.Write(packet.VelX);
                w.Write(packet.VelY);
                w.Write(packet.VelZ);
                w.Write(packet.Timestamp);
                w.Write(packet.IsActive);
                w.Write(packet.RemovalReason);
                return ms.ToArray();
            }
        }

        public static BombDropPacket DeserializeBombDrop(byte[] data)
        {
            // ShooterId(8) + string(1+) + optional InstanceId(4) + Pos(24) + Vel(12) + Time(4)
            // + optional IsActive(1) + RemovalReason(1). Legacy packets omit the appended fields.
            return SafeRead(data, 49, nameof(BombDropPacket), r =>
            {
                var p = new BombDropPacket
                {
                    ShooterId = r.ReadUInt64(),
                    BombType = r.ReadString(),
                    IsActive = true
                };

                long remaining = r.BaseStream.Length - r.BaseStream.Position;
                if (remaining >= 44)
                    p.BombInstanceId = r.ReadInt32();

                p.LaunchPosX = r.ReadDouble();
                p.LaunchPosY = r.ReadDouble();
                p.LaunchPosZ = r.ReadDouble();
                p.VelX = r.ReadSingle();
                p.VelY = r.ReadSingle();
                p.VelZ = r.ReadSingle();
                p.Timestamp = r.ReadSingle();

                if (r.BaseStream.Position < r.BaseStream.Length)
                    p.IsActive = r.ReadBoolean();
                if (r.BaseStream.Position < r.BaseStream.Length)
                    p.RemovalReason = r.ReadByte();

                return p;
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  CraterSpawn (37)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeCraterSpawn(CraterSpawnPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PosX);
                w.Write(packet.PosY);
                w.Write(packet.PosZ);
                w.Write(packet.CraterSize);
                return ms.ToArray();
            }
        }

        public static CraterSpawnPacket DeserializeCraterSpawn(byte[] data)
        {
            // Pos(24) + CraterSize(1) = 25
            return SafeRead(data, 25, nameof(CraterSpawnPacket), r => new CraterSpawnPacket
            {
                PosX = r.ReadDouble(),
                PosY = r.ReadDouble(),
                PosZ = r.ReadDouble(),
                CraterSize = r.ReadByte()
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  BuildingDestroy (38)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeBuildingDestroy(BuildingDestroyPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.BuildingInstanceId);
                w.Write(packet.PosX);
                w.Write(packet.PosY);
                w.Write(packet.PosZ);
                w.Write(packet.ObjectName ?? "");
                w.Write(packet.TargetType);
                w.Write(packet.ObjectKind);
                return ms.ToArray();
            }
        }

        public static BuildingDestroyPacket DeserializeBuildingDestroy(byte[] data)
        {
            // InstanceId(4) + Pos(24) + optional ObjectName + TargetType + ObjectKind
            return SafeRead(data, 28, nameof(BuildingDestroyPacket), r =>
            {
                var packet = new BuildingDestroyPacket
                {
                    BuildingInstanceId = r.ReadInt32(),
                    PosX = r.ReadDouble(),
                    PosY = r.ReadDouble(),
                    PosZ = r.ReadDouble(),
                    ObjectName = "",
                    TargetType = 255,
                    ObjectKind = 0
                };

                if (r.BaseStream.Position < r.BaseStream.Length)
                    packet.ObjectName = r.ReadString();
                if (r.BaseStream.Position < r.BaseStream.Length)
                    packet.TargetType = r.ReadByte();
                if (r.BaseStream.Position < r.BaseStream.Length)
                    packet.ObjectKind = r.ReadByte();

                return packet;
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  AircraftChanged (52)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeAircraftChanged(AircraftChangedPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PlayerId);
                w.Write(packet.AircraftType ?? "Unknown");
                w.Write(packet.IsAlive);
                w.Write(packet.LoadoutName ?? "");
                return ms.ToArray();
            }
        }

        public static AircraftChangedPacket DeserializeAircraftChanged(byte[] data)
        {
            // PlayerId(8) + string(1+) + IsAlive(1) = 10
            return SafeRead(data, 10, nameof(AircraftChangedPacket), r =>
            {
                var packet = new AircraftChangedPacket
                {
                    PlayerId = r.ReadUInt64(),
                    AircraftType = r.ReadString(),
                    IsAlive = r.ReadBoolean()
                };

                // Backward compat: loadout added after IsAlive
                if (r.BaseStream.Position < r.BaseStream.Length)
                    packet.LoadoutName = r.ReadString();

                return packet;
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  KillConfirm (54)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeKillConfirm(KillConfirmPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.KillerId);
                w.Write(packet.VictimId);
                w.Write(packet.WeaponName ?? "Unknown");
                w.Write(packet.DeathSequence);
                return ms.ToArray();
            }
        }

        public static KillConfirmPacket DeserializeKillConfirm(byte[] data)
        {
            // KillerId(8) + VictimId(8) + string(1+) = 17
            return SafeRead(data, 17, nameof(KillConfirmPacket), r =>
            {
                var packet = new KillConfirmPacket
                {
                    KillerId = r.ReadUInt64(),
                    VictimId = r.ReadUInt64(),
                    WeaponName = r.ReadString()
                };

                if (r.BaseStream.Position < r.BaseStream.Length)
                    packet.DeathSequence = r.ReadUInt32();

                return packet;
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  AircraftDestroyed (41)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeAircraftDestroyed(AircraftDestroyedPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.VictimId);
                w.Write(packet.DeathSequence);
                w.Write(packet.Reason ?? "Unknown");
                return ms.ToArray();
            }
        }

        public static AircraftDestroyedPacket DeserializeAircraftDestroyed(byte[] data)
        {
            // VictimId(8) + DeathSequence(4) + string(1+) = 13
            return SafeRead(data, 13, nameof(AircraftDestroyedPacket), r => new AircraftDestroyedPacket
            {
                VictimId = r.ReadUInt64(),
                DeathSequence = r.ReadUInt32(),
                Reason = r.ReadString()
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  DeathReport (57) / ScoreEvent (58)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeDeathReport(DeathReportPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.VictimId);
                w.Write(packet.KillerId);
                w.Write(packet.LifeId);
                w.Write(packet.WeaponName ?? "Unknown");
                w.Write(packet.Reason ?? "");
                return ms.ToArray();
            }
        }

        public static DeathReportPacket DeserializeDeathReport(byte[] data)
        {
            // VictimId(8) + KillerId(8) + LifeId(4) + string(1+) + string(1+) = 22
            return SafeRead(data, 22, nameof(DeathReportPacket), r => new DeathReportPacket
            {
                VictimId = r.ReadUInt64(),
                KillerId = r.ReadUInt64(),
                LifeId = r.ReadUInt32(),
                WeaponName = r.ReadString(),
                Reason = r.ReadString()
            });
        }

        public static byte[] SerializeScoreEvent(ScoreEventPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.VictimId);
                w.Write(packet.KillerId);
                w.Write(packet.LifeId);
                w.Write(packet.WeaponName ?? "Unknown");
                w.Write(packet.Reason ?? "");
                return ms.ToArray();
            }
        }

        public static ScoreEventPacket DeserializeScoreEvent(byte[] data)
        {
            // VictimId(8) + KillerId(8) + LifeId(4) + string(1+) + string(1+) = 22
            return SafeRead(data, 22, nameof(ScoreEventPacket), r => new ScoreEventPacket
            {
                VictimId = r.ReadUInt64(),
                KillerId = r.ReadUInt64(),
                LifeId = r.ReadUInt32(),
                WeaponName = r.ReadString(),
                Reason = r.ReadString()
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  AircraftCollision (43)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeAircraftCollision(AircraftCollisionPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PlayerA);
                w.Write(packet.PlayerB);
                w.Write(packet.PosX);
                w.Write(packet.PosY);
                w.Write(packet.PosZ);
                w.Write(packet.NormalX);
                w.Write(packet.NormalY);
                w.Write(packet.NormalZ);
                w.Write(packet.DamageA);
                w.Write(packet.DamageB);
                w.Write(packet.RelativeSpeed);
                return ms.ToArray();
            }
        }

        public static AircraftCollisionPacket DeserializeAircraftCollision(byte[] data)
        {
            // A(8) + B(8) + Pos(24) + Normal(12) + DmgA(4) + DmgB(4) + Speed(4) = 64
            return SafeRead(data, 64, nameof(AircraftCollisionPacket), r => new AircraftCollisionPacket
            {
                PlayerA = r.ReadUInt64(),
                PlayerB = r.ReadUInt64(),
                PosX = r.ReadDouble(),
                PosY = r.ReadDouble(),
                PosZ = r.ReadDouble(),
                NormalX = r.ReadSingle(),
                NormalY = r.ReadSingle(),
                NormalZ = r.ReadSingle(),
                DamageA = r.ReadInt32(),
                DamageB = r.ReadInt32(),
                RelativeSpeed = r.ReadSingle()
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  ExplosionSync (78)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeExplosionSync(ExplosionSyncPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.ShooterId);
                w.Write(packet.PosX);
                w.Write(packet.PosY);
                w.Write(packet.PosZ);
                w.Write(packet.BlastRadius);
                w.Write(packet.ImpactDamage);
                w.Write(packet.WeaponName ?? "Unknown");
                w.Write(packet.EffectPath ?? "");
                w.Write(packet.ExplosionType);
                w.Write(packet.ImpactSurface);
                return ms.ToArray();
            }
        }

        public static ExplosionSyncPacket DeserializeExplosionSync(byte[] data)
        {
            // Shooter(8) + Pos(24) + Radius(4) + Dmg(4) + str(1+) + str(1+) + Type(1) + Surface(1) = 44
            return SafeRead(data, 44, nameof(ExplosionSyncPacket), r => new ExplosionSyncPacket
            {
                ShooterId = r.ReadUInt64(),
                PosX = r.ReadDouble(),
                PosY = r.ReadDouble(),
                PosZ = r.ReadDouble(),
                BlastRadius = r.ReadSingle(),
                ImpactDamage = r.ReadInt32(),
                WeaponName = r.ReadString(),
                EffectPath = r.ReadString(),
                ExplosionType = r.ReadByte(),
                ImpactSurface = r.ReadByte()
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  AircraftDestructionVfx (79)
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeAircraftDestructionVfx(AircraftDestructionVfxPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.VictimId);
                w.Write(packet.PosX);
                w.Write(packet.PosY);
                w.Write(packet.PosZ);
                w.Write(packet.RotX);
                w.Write(packet.RotY);
                w.Write(packet.RotZ);
                w.Write(packet.RotW);
                w.Write(packet.DestructionReason);
                w.Write(packet.VelX);
                w.Write(packet.VelY);
                w.Write(packet.VelZ);
                return ms.ToArray();
            }
        }

        public static AircraftDestructionVfxPacket DeserializeAircraftDestructionVfx(byte[] data)
        {
            // VictimId(8) + Pos(24) + Rot(16) + Reason(1) = 49
            return SafeRead(data, 49, nameof(AircraftDestructionVfxPacket), r =>
            {
                var packet = new AircraftDestructionVfxPacket
                {
                    VictimId = r.ReadUInt64(),
                    PosX = r.ReadDouble(),
                    PosY = r.ReadDouble(),
                    PosZ = r.ReadDouble(),
                    RotX = r.ReadSingle(),
                    RotY = r.ReadSingle(),
                    RotZ = r.ReadSingle(),
                    RotW = r.ReadSingle(),
                    DestructionReason = r.ReadByte()
                };

                // Backward compat: death velocity added later
                if (r.BaseStream.Position + sizeof(float) * 3 <= r.BaseStream.Length)
                {
                    packet.VelX = r.ReadSingle();
                    packet.VelY = r.ReadSingle();
                    packet.VelZ = r.ReadSingle();
                }

                return packet;
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  Lobby Packets
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeLobbyState(LobbyStatePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.HostName ?? "Host");
                w.Write(packet.MapName ?? "");
                w.Write((byte)packet.SpawnType);
                w.Write(packet.GameStarted);
                w.Write(packet.GameLoading);
                w.Write(packet.AircraftCollisionsEnabled);
                w.Write((byte)packet.TimeOfDay);

                int playerCount = packet.Players?.Length ?? 0;
                w.Write(playerCount);

                if (packet.Players != null)
                {
                    foreach (var player in packet.Players)
                    {
                        w.Write(player.PeerId);
                        w.Write(player.PlayerName ?? "Player");
                        w.Write(player.SelectedAirfield ?? "");
                        w.Write(player.SelectedAircraft ?? "");
                        w.Write(player.IsReady);
                        w.Write(player.IsLoaded);
                        w.Write(player.IsHost);
                    }

                    // Appended after the legacy roster fields so older builds can ignore it.
                    foreach (var player in packet.Players)
                    {
                        w.Write(player.SelectedLoadout ?? "");
                    }

                    foreach (var player in packet.Players)
                    {
                        w.Write((byte)player.Team);
                    }
                }

                w.Write((byte)packet.GameMode);
                w.Write(packet.MaxPlayersTotal);
                w.Write(packet.TeamCount);
                w.Write(packet.Revision);

                // Player mod verification state (appended after Revision for backward compat)
                w.Write(playerCount);
                if (packet.Players != null)
                {
                    foreach (var player in packet.Players)
                    {
                        w.Write(player.IsModsVerified);
                        w.Write(player.IsModSyncing);
                    }
                }

                return ms.ToArray();
            }
        }

        public static LobbyStatePacket DeserializeLobbyState(byte[] data)
        {
            // Minimum: str(1) + str(1) + SpawnType(1) + bools(3) + TimeOfDay(1) + count(4) = 11
            return SafeRead(data, 11, nameof(LobbyStatePacket), r =>
            {
                var packet = new LobbyStatePacket
                {
                    HostName = r.ReadString(),
                    MapName = r.ReadString(),
                    SpawnType = (LobbySpawnType)r.ReadByte(),
                    GameStarted = r.ReadBoolean(),
                    GameLoading = r.ReadBoolean(),
                    AircraftCollisionsEnabled = r.ReadBoolean(),
                    TimeOfDay = (TimeOfDay)r.ReadByte()
                };

                int playerCount = r.ReadInt32();
                if (playerCount < 0 || playerCount > MaxPlayerCount)
                {
                    LogWarning($"{Tag} Invalid player count in LobbyState: {playerCount}");
                    packet.Players = Array.Empty<LobbyPlayerInfo>();
                    return packet;
                }

                packet.Players = new LobbyPlayerInfo[playerCount];
                for (int i = 0; i < playerCount; i++)
                {
                    packet.Players[i] = new LobbyPlayerInfo
                    {
                        PeerId = r.ReadUInt64(),
                        PlayerName = r.ReadString(),
                        SelectedAirfield = r.ReadString(),
                        SelectedAircraft = r.ReadString(),
                        IsReady = r.ReadBoolean(),
                        IsLoaded = r.ReadBoolean(),
                        IsHost = r.ReadBoolean()
                    };
                }

                // Loadouts were added after the legacy player array. If absent, keep defaults.
                for (int i = 0; i < playerCount && r.BaseStream.Position < r.BaseStream.Length; i++)
                {
                    string loadout = r.ReadString();
                    packet.Players[i].SelectedLoadout = string.IsNullOrEmpty(loadout) ? null : loadout;
                }

                for (int i = 0; i < playerCount && r.BaseStream.Position < r.BaseStream.Length; i++)
                {
                    packet.Players[i].Team = (MultiplayerTeam)r.ReadByte();
                }

                if (r.BaseStream.Position < r.BaseStream.Length)
                    packet.GameMode = (MultiplayerGameMode)r.ReadByte();
                if (r.BaseStream.Position + sizeof(int) <= r.BaseStream.Length)
                    packet.MaxPlayersTotal = r.ReadInt32();
                if (r.BaseStream.Position + sizeof(int) <= r.BaseStream.Length)
                    packet.TeamCount = r.ReadInt32();
                if (r.BaseStream.Position + sizeof(uint) <= r.BaseStream.Length)
                    packet.Revision = r.ReadUInt32();

                // Mod verification state (appended — old builds read Revision and ignore trailing bytes)
                bool hasModVerification = r.BaseStream.Position < r.BaseStream.Length;
                if (hasModVerification)
                {
                    int vPlayerCount = r.ReadInt32();
                    for (int i = 0; i < vPlayerCount && i < playerCount; i++)
                    {
                        if (r.BaseStream.Position + 2 <= r.BaseStream.Length)
                        {
                            packet.Players[i].IsModsVerified = r.ReadBoolean();
                            packet.Players[i].IsModSyncing = r.ReadBoolean();
                        }
                    }
                }

                return packet;
            });
        }

        public static byte[] SerializeLobbyPlayerJoined(LobbyPlayerJoinedPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                w.Write(packet.PlayerName ?? "Player");
                return ms.ToArray();
            }
        }

        public static LobbyPlayerJoinedPacket DeserializeLobbyPlayerJoined(byte[] data)
        {
            // PeerId(8) + str(1+) = 9
            return SafeRead(data, 9, nameof(LobbyPlayerJoinedPacket), r => new LobbyPlayerJoinedPacket
            {
                PeerId = r.ReadUInt64(),
                PlayerName = r.ReadString()
            });
        }

        public static byte[] SerializeLobbyPlayerLeft(LobbyPlayerLeftPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                return ms.ToArray();
            }
        }

        public static LobbyPlayerLeftPacket DeserializeLobbyPlayerLeft(byte[] data)
        {
            // PeerId(8) = 8
            return SafeRead(data, 8, nameof(LobbyPlayerLeftPacket), r => new LobbyPlayerLeftPacket
            {
                PeerId = r.ReadUInt64()
            });
        }

        public static byte[] SerializeLobbyPlayerReady(LobbyPlayerReadyPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                w.Write(packet.IsReady);
                return ms.ToArray();
            }
        }

        public static LobbyPlayerReadyPacket DeserializeLobbyPlayerReady(byte[] data)
        {
            // PeerId(8) + IsReady(1) = 9
            return SafeRead(data, 9, nameof(LobbyPlayerReadyPacket), r => new LobbyPlayerReadyPacket
            {
                PeerId = r.ReadUInt64(),
                IsReady = r.ReadBoolean()
            });
        }

        public static byte[] SerializeLobbyTeamSelect(LobbyTeamSelectPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                w.Write((byte)packet.Team);
                return ms.ToArray();
            }
        }

        public static LobbyTeamSelectPacket DeserializeLobbyTeamSelect(byte[] data)
        {
            // PeerId(8) + Team(1) = 9
            return SafeRead(data, 9, nameof(LobbyTeamSelectPacket), r => new LobbyTeamSelectPacket
            {
                PeerId = r.ReadUInt64(),
                Team = (MultiplayerTeam)r.ReadByte()
            });
        }

        public static byte[] SerializeLobbyAirfieldSelect(LobbyAirfieldSelectPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                w.Write(packet.AirfieldName ?? "");
                return ms.ToArray();
            }
        }

        public static LobbyAirfieldSelectPacket DeserializeLobbyAirfieldSelect(byte[] data)
        {
            // PeerId(8) + str(1+) = 9
            return SafeRead(data, 9, nameof(LobbyAirfieldSelectPacket), r => new LobbyAirfieldSelectPacket
            {
                PeerId = r.ReadUInt64(),
                AirfieldName = r.ReadString()
            });
        }

        public static byte[] SerializeLobbyAircraftSelect(LobbyAircraftSelectPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                w.Write(packet.AircraftName ?? "");
                return ms.ToArray();
            }
        }

        public static LobbyAircraftSelectPacket DeserializeLobbyAircraftSelect(byte[] data)
        {
            // PeerId(8) + str(1+) = 9
            return SafeRead(data, 9, nameof(LobbyAircraftSelectPacket), r => new LobbyAircraftSelectPacket
            {
                PeerId = r.ReadUInt64(),
                AircraftName = r.ReadString()
            });
        }

        public static byte[] SerializeLobbyLoadoutSelect(LobbyLoadoutSelectPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                w.Write(packet.LoadoutName ?? "");
                return ms.ToArray();
            }
        }

        public static LobbyLoadoutSelectPacket DeserializeLobbyLoadoutSelect(byte[] data)
        {
            // PeerId(8) + str(1+) = 9
            return SafeRead(data, 9, nameof(LobbyLoadoutSelectPacket), r => new LobbyLoadoutSelectPacket
            {
                PeerId = r.ReadUInt64(),
                LoadoutName = r.ReadString()
            });
        }

        public static byte[] SerializeLobbySpawnSettings(LobbySpawnSettingsPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)packet.SpawnType);
                w.Write(packet.MapName ?? "");
                return ms.ToArray();
            }
        }

        public static LobbySpawnSettingsPacket DeserializeLobbySpawnSettings(byte[] data)
        {
            // SpawnType(1) + str(1+) = 2
            return SafeRead(data, 2, nameof(LobbySpawnSettingsPacket), r => new LobbySpawnSettingsPacket
            {
                SpawnType = (LobbySpawnType)r.ReadByte(),
                MapName = r.ReadString()
            });
        }

        public static byte[] SerializeLobbyStartGame(LobbyStartGamePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.MapName ?? "");
                w.Write((byte)packet.SpawnType);
                w.Write((byte)packet.TimeOfDay);
                return ms.ToArray();
            }
        }

        public static LobbyStartGamePacket DeserializeLobbyStartGame(byte[] data)
        {
            // Backward compatible: str(1+) + SpawnType(1) + optional TimeOfDay(1)
            return SafeRead(data, 2, nameof(LobbyStartGamePacket), r => new LobbyStartGamePacket
            {
                MapName = r.ReadString(),
                SpawnType = (LobbySpawnType)r.ReadByte(),
                TimeOfDay = r.BaseStream.Position < r.BaseStream.Length
                    ? (TimeOfDay)r.ReadByte()
                    : TimeOfDay.Morning
            });
        }

        public static byte[] SerializeLobbyLoadingComplete(LobbyLoadingCompletePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                return ms.ToArray();
            }
        }

        public static LobbyLoadingCompletePacket DeserializeLobbyLoadingComplete(byte[] data)
        {
            // PeerId(8) = 8
            return SafeRead(data, 8, nameof(LobbyLoadingCompletePacket), r => new LobbyLoadingCompletePacket
            {
                PeerId = r.ReadUInt64()
            });
        }

        public static byte[] SerializeLobbySpawnPlayers(LobbySpawnPlayersPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.Timestamp);
                return ms.ToArray();
            }
        }

        public static LobbySpawnPlayersPacket DeserializeLobbySpawnPlayers(byte[] data)
        {
            // Timestamp(4) = 4
            return SafeRead(data, 4, nameof(LobbySpawnPlayersPacket), r => new LobbySpawnPlayersPacket
            {
                Timestamp = r.ReadSingle()
            });
        }

        public static byte[] SerializeLobbyRespawnRequest(LobbyRespawnRequestPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                return ms.ToArray();
            }
        }

        public static LobbyRespawnRequestPacket DeserializeLobbyRespawnRequest(byte[] data)
        {
            // PeerId(8) = 8
            return SafeRead(data, 8, nameof(LobbyRespawnRequestPacket), r => new LobbyRespawnRequestPacket
            {
                PeerId = r.ReadUInt64()
            });
        }

        public static byte[] SerializeLobbyWelcome(LobbyWelcomePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.AssignedPeerId);
                w.Write(packet.HostName ?? "Host");
                return ms.ToArray();
            }
        }

        public static LobbyWelcomePacket DeserializeLobbyWelcome(byte[] data)
        {
            // AssignedPeerId(8) + str(1+) = 9
            return SafeRead(data, 9, nameof(LobbyWelcomePacket), r => new LobbyWelcomePacket
            {
                AssignedPeerId = r.ReadUInt64(),
                HostName = r.ReadString()
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  Mod Compatibility Packets
        // ═══════════════════════════════════════════════════════════

        public static byte[] SerializeModManifest(ModManifestPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                w.Write(packet.ManifestData?.Length ?? 0);
                if (packet.ManifestData != null && packet.ManifestData.Length > 0)
                {
                    w.Write(packet.ManifestData);
                }
                w.Write(packet.ModVersion ?? "");
                return ms.ToArray();
            }
        }

        public static ModManifestPacket DeserializeModManifest(byte[] data)
        {
            // PeerId(8) + length(4) = 12
            return SafeRead(data, 12, nameof(ModManifestPacket), r =>
            {
                var packet = new ModManifestPacket
                {
                    PeerId = r.ReadUInt64()
                };

                int length = r.ReadInt32();
                if (length < 0 || length > MaxManifestBytes)
                {
                    LogWarning($"{Tag} Invalid manifest length: {length}");
                    return packet;
                }

                if (length > 0)
                {
                    packet.ManifestData = r.ReadBytes(length);
                }

                packet.ModVersion = ReadOptionalString(r);

                return packet;
            });
        }

        public static byte[] SerializeModCompatibilityResult(ModCompatibilityResultPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                w.Write(packet.IsCompatible);
                w.Write(packet.RejectionReason ?? "");
                w.Write(packet.HostModVersion ?? "");
                w.Write(packet.HostManifestData?.Length ?? 0);
                if (packet.HostManifestData != null && packet.HostManifestData.Length > 0)
                    w.Write(packet.HostManifestData);
                return ms.ToArray();
            }
        }

        public static ModCompatibilityResultPacket DeserializeModCompatibilityResult(byte[] data)
        {
            // PeerId(8) + IsCompatible(1) + str(1+) = 10
            return SafeRead(data, 10, nameof(ModCompatibilityResultPacket), r =>
            {
                var packet = new ModCompatibilityResultPacket
                {
                    PeerId = r.ReadUInt64(),
                    IsCompatible = r.ReadBoolean(),
                    RejectionReason = r.ReadString()
                };

                packet.HostModVersion = ReadOptionalString(r);
                if (r.BaseStream.Position + sizeof(int) <= r.BaseStream.Length)
                {
                    int length = r.ReadInt32();
                    if (length < 0 || length > MaxManifestBytes)
                    {
                        LogWarning($"{Tag} Invalid host manifest length: {length}");
                        return packet;
                    }

                    if (length > 0)
                        packet.HostManifestData = r.ReadBytes(length);
                }
                return packet;
            });
        }

        public static byte[] SerializeModSyncRequest(ModSyncRequestPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                w.Write(packet.HostManifestHash ?? "");
                return ms.ToArray();
            }
        }

        public static ModSyncRequestPacket DeserializeModSyncRequest(byte[] data)
        {
            // PeerId(8) + string(1+) = 9
            return SafeRead(data, 9, nameof(ModSyncRequestPacket), r => new ModSyncRequestPacket
            {
                PeerId = r.ReadUInt64(),
                HostManifestHash = r.ReadString()
            });
        }

        public static byte[] SerializeModSyncChunk(ModSyncChunkPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packet.PeerId);
                w.Write(packet.TransferId);
                w.Write(packet.ChunkIndex);
                w.Write(packet.ChunkCount);
                w.Write(packet.TotalBytes);
                w.Write(packet.ChunkData?.Length ?? 0);
                if (packet.ChunkData != null && packet.ChunkData.Length > 0)
                    w.Write(packet.ChunkData);
                return ms.ToArray();
            }
        }

        public static ModSyncChunkPacket DeserializeModSyncChunk(byte[] data)
        {
            // PeerId(8) + transfer(4) + index(4) + count(4) + total(4) + len(4) = 28
            return SafeRead(data, 28, nameof(ModSyncChunkPacket), r =>
            {
                var packet = new ModSyncChunkPacket
                {
                    PeerId = r.ReadUInt64(),
                    TransferId = r.ReadUInt32(),
                    ChunkIndex = r.ReadInt32(),
                    ChunkCount = r.ReadInt32(),
                    TotalBytes = r.ReadInt32()
                };

                int length = r.ReadInt32();
                if (length < 0 || length > MaxModSyncChunkBytes)
                {
                    LogWarning($"{Tag} Invalid mod sync chunk length: {length}");
                    return packet;
                }

                if (length > 0)
                    packet.ChunkData = r.ReadBytes(length);

                return packet;
            });
        }

        private static string ReadOptionalString(BinaryReader r)
        {
            if (r == null || r.BaseStream.Position >= r.BaseStream.Length)
                return "";

            try
            {
                return r.ReadString() ?? "";
            }
            catch (EndOfStreamException)
            {
                return "";
            }
            catch (IOException)
            {
                return "";
            }
        }
    }
}
