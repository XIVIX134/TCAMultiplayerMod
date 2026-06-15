using System;
using NUnit.Framework;
using TCAMultiplayer.Protocol;

namespace TCAMultiplayer.Tests
{
    [TestFixture]
    public class SerializationTests
    {
        // ═══════════════════════════════════════════════════════════
        //  Framing: [PacketType byte][payload bytes…]
        // ═══════════════════════════════════════════════════════════

        [Test]
        public void Frame_Roundtrip_WithPayload()
        {
            var payload = new byte[] { 0x01, 0x02, 0x03 };
            byte[] framed = PacketSerializer.Serialize(PacketType.AircraftState, payload);
            var (type, extracted) = PacketSerializer.Deserialize(framed);

            Assert.AreEqual(PacketType.AircraftState, type);
            CollectionAssert.AreEqual(payload, extracted);
        }

        [Test]
        public void Frame_Roundtrip_NullPayload()
        {
            byte[] framed = PacketSerializer.Serialize(PacketType.Ping, null);
            var (type, payload) = PacketSerializer.Deserialize(framed);

            Assert.AreEqual(PacketType.Ping, type);
            Assert.IsNull(payload);
        }

        [Test]
        public void Frame_Roundtrip_EmptyPayload()
        {
            byte[] framed = PacketSerializer.Serialize(PacketType.Pong, new byte[0]);
            var (type, payload) = PacketSerializer.Deserialize(framed);

            Assert.AreEqual(PacketType.Pong, type);
            Assert.IsNull(payload); // Length 1 (just type byte) → no payload extracted
        }

        [Test]
        public void Frame_Deserialize_EmptyData_ReturnsDefault()
        {
            var (type, payload) = PacketSerializer.Deserialize(new byte[0]);

            Assert.AreEqual((PacketType)0, type);
            Assert.IsNull(payload);
        }

        [Test]
        public void Frame_Deserialize_NullData_ReturnsDefault()
        {
            var (type, payload) = PacketSerializer.Deserialize(null);

            Assert.AreEqual((PacketType)0, type);
            Assert.IsNull(payload);
        }

        [Test]
        public void Frame_UnknownPacketType_StillDeserializes()
        {
            byte[] raw = new byte[] { 255, 0xAA, 0xBB };
            var (type, payload) = PacketSerializer.Deserialize(raw);

            Assert.AreEqual((PacketType)255, type);
            CollectionAssert.AreEqual(new byte[] { 0xAA, 0xBB }, payload);
        }

        [Test]
        public void Frame_SingleTypeByte_NoPayload()
        {
            byte[] raw = new byte[] { (byte)PacketType.Ping };
            var (type, payload) = PacketSerializer.Deserialize(raw);

            Assert.AreEqual(PacketType.Ping, type);
            Assert.IsNull(payload);
        }

        // ═══════════════════════════════════════════════════════════
        //  AircraftStatePacket — most complex packet
        // ═══════════════════════════════════════════════════════════

        [Test]
        public void AircraftState_Roundtrip()
        {
            var original = new AircraftStatePacket
            {
                PlayerId = 42,
                SequenceNumber = 12345,
                AircraftType = "F-16C",
                PosX = 1000.5, PosY = 2000.5, PosZ = 3000.5,
                RotX = 0.1f, RotY = 0.2f, RotZ = 0.3f, RotW = 0.9f,
                VelX = 100f, VelY = 0f, VelZ = 200f,
                AngVelX = 0.01f, AngVelY = -0.02f, AngVelZ = 0.03f,
                Throttle = 0.8f,
                Pitch = -0.5f,
                Roll = 0.3f,
                Yaw = 0.1f,
                NozzleAngle = 45.5f,
                SpeedKIAS = 350.0f,
                BrakeState = 0.0f,
                Flags = 0b00000101, // Afterburner + FlapsDown
                Timestamp = 1.5f
            };

            byte[] data = PacketSerializer.SerializeAircraftState(original);
            var result = PacketSerializer.DeserializeAircraftState(data);

            Assert.AreEqual(original.PlayerId, result.PlayerId);
            Assert.AreEqual(original.SequenceNumber, result.SequenceNumber);
            Assert.AreEqual(original.AircraftType, result.AircraftType);
            Assert.AreEqual(original.PosX, result.PosX, 0.001);
            Assert.AreEqual(original.PosY, result.PosY, 0.001);
            Assert.AreEqual(original.PosZ, result.PosZ, 0.001);
            Assert.AreEqual(original.RotX, result.RotX, 0.0001f);
            Assert.AreEqual(original.RotY, result.RotY, 0.0001f);
            Assert.AreEqual(original.RotZ, result.RotZ, 0.0001f);
            Assert.AreEqual(original.RotW, result.RotW, 0.0001f);
            Assert.AreEqual(original.VelX, result.VelX, 0.001f);
            Assert.AreEqual(original.VelY, result.VelY, 0.001f);
            Assert.AreEqual(original.VelZ, result.VelZ, 0.001f);
            Assert.AreEqual(original.AngVelX, result.AngVelX, 0.0001f);
            Assert.AreEqual(original.AngVelY, result.AngVelY, 0.0001f);
            Assert.AreEqual(original.AngVelZ, result.AngVelZ, 0.0001f);
            Assert.AreEqual(original.Throttle, result.Throttle, 0.001f);
            Assert.AreEqual(original.Pitch, result.Pitch, 0.001f);
            Assert.AreEqual(original.Roll, result.Roll, 0.001f);
            Assert.AreEqual(original.Yaw, result.Yaw, 0.001f);
            Assert.AreEqual(original.NozzleAngle, result.NozzleAngle, 0.001f);
            Assert.AreEqual(original.SpeedKIAS, result.SpeedKIAS, 0.001f);
            Assert.AreEqual(original.BrakeState, result.BrakeState, 0.001f);
            Assert.AreEqual(original.Flags, result.Flags);
            Assert.AreEqual(original.Timestamp, result.Timestamp, 0.001f);
        }

        [Test]
        public void AircraftState_FlagHelpers_Roundtrip()
        {
            var original = new AircraftStatePacket
            {
                PlayerId = 1,
                SequenceNumber = 1,
                AircraftType = "AV-8B",
                Flags = 0
            };
            original.Afterburner = true;
            original.GearDown = true;
            original.FlapsDown = false;
            original.IsFiring = true;
            original.IsFlareFiring = false;
            original.IsChaffFiring = false;
            original.IsNavMode = false;
            original.IsWeightOnWheels = true;

            byte[] data = PacketSerializer.SerializeAircraftState(original);
            var result = PacketSerializer.DeserializeAircraftState(data);

            Assert.IsTrue(result.Afterburner);
            Assert.IsTrue(result.GearDown);
            Assert.IsFalse(result.FlapsDown);
            Assert.IsTrue(result.IsFiring);
            Assert.IsFalse(result.IsFlareFiring);
            Assert.IsFalse(result.IsChaffFiring);
            Assert.IsFalse(result.IsNavMode);
            Assert.IsTrue(result.IsWeightOnWheels);
        }

        [Test]
        public void AircraftState_CarriesNativeControlAndCountermeasureState()
        {
            var packet = new AircraftStatePacket
            {
                Pitch = -0.25f,
                Roll = 0.5f,
                Yaw = 0.75f,
                Throttle = 0.8f,
                NozzleAngle = 45f,
                IsFiring = true,
                IsFlareFiring = true,
                IsChaffFiring = true
            };

            byte[] data = PacketSerializer.SerializeAircraftState(packet);
            var result = PacketSerializer.DeserializeAircraftState(data);

            Assert.AreEqual(packet.Pitch, result.Pitch);
            Assert.AreEqual(packet.Roll, result.Roll);
            Assert.AreEqual(packet.Yaw, result.Yaw);
            Assert.AreEqual(packet.Throttle, result.Throttle);
            Assert.AreEqual(packet.NozzleAngle, result.NozzleAngle);
            Assert.IsTrue(result.IsFiring);
            Assert.IsTrue(result.IsFlareFiring);
            Assert.IsTrue(result.IsChaffFiring);
        }

        [Test]
        public void DeprecatedMechanicPacketSlots_RemainReserved()
        {
#pragma warning disable CS0618
            Assert.AreEqual(20, (byte)PacketType.ControlInput);
            Assert.AreEqual(35, (byte)PacketType.CountermeasureDeploy);
            Assert.AreEqual(42, (byte)PacketType.ProjectileImpact);
#pragma warning restore CS0618
        }

        [Test]
        public void AircraftState_AllZeros_Roundtrip()
        {
            var original = new AircraftStatePacket
            {
                PlayerId = 0,
                SequenceNumber = 0,
                AircraftType = "",
                PosX = 0, PosY = 0, PosZ = 0,
                RotX = 0, RotY = 0, RotZ = 0, RotW = 0,
                VelX = 0, VelY = 0, VelZ = 0,
                AngVelX = 0, AngVelY = 0, AngVelZ = 0,
                Throttle = 0, Pitch = 0, Roll = 0, Yaw = 0,
                NozzleAngle = 0, SpeedKIAS = 0, BrakeState = 0,
                Flags = 0, Timestamp = 0
            };

            byte[] data = PacketSerializer.SerializeAircraftState(original);
            var result = PacketSerializer.DeserializeAircraftState(data);

            Assert.AreEqual(0UL, result.PlayerId);
            Assert.AreEqual(0u, result.SequenceNumber);
            Assert.AreEqual("", result.AircraftType);
            Assert.AreEqual(0.0, result.PosX, 0.001);
            Assert.AreEqual(0.0, result.PosY, 0.001);
            Assert.AreEqual(0.0, result.PosZ, 0.001);
            Assert.AreEqual(0, result.Flags);
            Assert.AreEqual(0f, result.Timestamp, 0.001f);
        }

        [Test]
        public void AircraftState_MaxValues_Roundtrip()
        {
            var original = new AircraftStatePacket
            {
                PlayerId = ulong.MaxValue,
                SequenceNumber = uint.MaxValue,
                AircraftType = "MaxTest",
                PosX = double.MaxValue,
                PosY = double.MaxValue,
                PosZ = double.MaxValue,
                RotX = float.MaxValue,
                RotY = float.MaxValue,
                RotZ = float.MaxValue,
                RotW = float.MaxValue,
                VelX = float.MaxValue,
                VelY = float.MaxValue,
                VelZ = float.MaxValue,
                Throttle = float.MaxValue,
                Flags = byte.MaxValue,
                Timestamp = float.MaxValue
            };

            byte[] data = PacketSerializer.SerializeAircraftState(original);
            var result = PacketSerializer.DeserializeAircraftState(data);

            Assert.AreEqual(ulong.MaxValue, result.PlayerId);
            Assert.AreEqual(uint.MaxValue, result.SequenceNumber);
            Assert.AreEqual(double.MaxValue, result.PosX);
            Assert.AreEqual(float.MaxValue, result.RotX);
            Assert.AreEqual(byte.MaxValue, result.Flags);
            Assert.AreEqual(float.MaxValue, result.Timestamp);
        }

        [Test]
        public void AircraftState_NullAircraftType_SerializesAsEmpty()
        {
            var original = new AircraftStatePacket
            {
                PlayerId = 1,
                SequenceNumber = 1,
                AircraftType = null
            };

            byte[] data = PacketSerializer.SerializeAircraftState(original);
            var result = PacketSerializer.DeserializeAircraftState(data);

            Assert.AreEqual("", result.AircraftType);
        }

        [Test]
        public void AircraftState_Truncated_ReturnsDefault()
        {
            // minBytes = 13; giving 5 triggers length check
            var result = PacketSerializer.DeserializeAircraftState(new byte[5]);
            Assert.AreEqual(0UL, result.PlayerId);
            Assert.AreEqual(0u, result.SequenceNumber);
        }

        [Test]
        public void AircraftState_NullData_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeAircraftState(null);
            Assert.AreEqual(0UL, result.PlayerId);
        }

        [Test]
        public void AircraftState_MidStreamTruncation_ReturnsDefault()
        {
            // Passes minBytes check (13) but stream runs out reading doubles
            // 13 bytes: PlayerId(8) + SeqNum(4) + empty string(1) = 13 exactly
            // Then ReadDouble for PosX fails → EndOfStreamException → default
            var result = PacketSerializer.DeserializeAircraftState(new byte[13]);
            Assert.AreEqual(0UL, result.PlayerId);
        }

        [Test]
        public void AircraftState_LegacyWithoutSpeedAndBrake_Roundtrip()
        {
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(7UL);
                w.Write(99u);
                w.Write("AV8B");
                w.Write(10.0);
                w.Write(20.0);
                w.Write(30.0);
                w.Write(0f);
                w.Write(0f);
                w.Write(0f);
                w.Write(1f);
                w.Write(100f);
                w.Write(0f);
                w.Write(200f);
                w.Write(0.1f);
                w.Write(0.2f);
                w.Write(0.3f);
                w.Write(0.75f);
                w.Write(-0.1f);
                w.Write(0.2f);
                w.Write(0.3f);
                w.Write(45f);
                w.Write((byte)0b0010_1011);
                w.Write(123.5f);

                var result = PacketSerializer.DeserializeAircraftState(ms.ToArray());

                Assert.AreEqual(7UL, result.PlayerId);
                Assert.AreEqual(99u, result.SequenceNumber);
                Assert.AreEqual(0b0010_1011, result.Flags);
                Assert.AreEqual(123.5f, result.Timestamp, 0.001f);
                Assert.AreEqual(0f, result.SpeedKIAS);
                Assert.AreEqual(0f, result.BrakeState);
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  MissileLaunchPacket — doubles + strings + backward compat
        // ═══════════════════════════════════════════════════════════

        [Test]
        public void MissileLaunch_Roundtrip()
        {
            var original = new MissileLaunchPacket
            {
                ShooterId = 100,
                TargetId = 200,
                IsTracking = true,
                MissileType = "AIM-120C",
                SeekerType = 1, // Radar
                LaunchPosX = 5000.123,
                LaunchPosY = 1000.456,
                LaunchPosZ = 7000.789,
                LaunchDirX = 0.5f,
                LaunchDirY = 0.1f,
                LaunchDirZ = 0.85f,
                MissileInstanceId = 42
            };

            byte[] data = PacketSerializer.SerializeMissileLaunch(original);
            var result = PacketSerializer.DeserializeMissileLaunch(data);

            Assert.AreEqual(original.ShooterId, result.ShooterId);
            Assert.AreEqual(original.TargetId, result.TargetId);
            Assert.AreEqual(original.IsTracking, result.IsTracking);
            Assert.AreEqual(original.MissileType, result.MissileType);
            Assert.AreEqual(original.SeekerType, result.SeekerType);
            Assert.AreEqual(original.LaunchPosX, result.LaunchPosX, 0.001);
            Assert.AreEqual(original.LaunchPosY, result.LaunchPosY, 0.001);
            Assert.AreEqual(original.LaunchPosZ, result.LaunchPosZ, 0.001);
            Assert.AreEqual(original.LaunchDirX, result.LaunchDirX, 0.0001f);
            Assert.AreEqual(original.LaunchDirY, result.LaunchDirY, 0.0001f);
            Assert.AreEqual(original.LaunchDirZ, result.LaunchDirZ, 0.0001f);
            Assert.AreEqual(original.MissileInstanceId, result.MissileInstanceId);
        }

        [Test]
        public void MissileLaunch_NullMissileType_SerializesAsUnknown()
        {
            var original = new MissileLaunchPacket
            {
                ShooterId = 1,
                TargetId = 2,
                MissileType = null
            };

            byte[] data = PacketSerializer.SerializeMissileLaunch(original);
            var result = PacketSerializer.DeserializeMissileLaunch(data);

            Assert.AreEqual("Unknown", result.MissileType);
        }

        [Test]
        public void MissileLaunch_EmptyString_Roundtrip()
        {
            var original = new MissileLaunchPacket
            {
                ShooterId = 1,
                TargetId = 2,
                MissileType = ""
            };

            byte[] data = PacketSerializer.SerializeMissileLaunch(original);
            var result = PacketSerializer.DeserializeMissileLaunch(data);

            Assert.AreEqual("", result.MissileType);
        }

        [Test]
        public void MissileLaunch_Truncated_ReturnsDefault()
        {
            // minBytes = 55
            var result = PacketSerializer.DeserializeMissileLaunch(new byte[10]);
            Assert.AreEqual(0UL, result.ShooterId);
        }

        [Test]
        public void MissileLaunch_NullData_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeMissileLaunch(null);
            Assert.AreEqual(0UL, result.ShooterId);
        }

        // ═══════════════════════════════════════════════════════════
        //  DamagePacket (PacketType.DamageDealt)
        // ═══════════════════════════════════════════════════════════

        [Test]
        public void Damage_Roundtrip()
        {
            var original = new DamagePacket
            {
                VictimId = 10,
                AttackerId = 20,
                AttackerLifeId = 3,
                DamageSequence = 77,
                Damage = 500,
                Penetration = 100,
                DamageType = 1, // missile
                HitPosX = 1234.5678,
                HitPosY = 2345.6789,
                HitPosZ = 3456.7890,
                WeaponName = "M61A1",
                HitPartName = "WingLeft"
            };

            byte[] data = PacketSerializer.SerializeDamage(original);
            var result = PacketSerializer.DeserializeDamage(data);

            Assert.AreEqual(original.VictimId, result.VictimId);
            Assert.AreEqual(original.AttackerId, result.AttackerId);
            Assert.AreEqual(original.AttackerLifeId, result.AttackerLifeId);
            Assert.AreEqual(original.DamageSequence, result.DamageSequence);
            Assert.AreEqual(original.Damage, result.Damage);
            Assert.AreEqual(original.Penetration, result.Penetration);
            Assert.AreEqual(original.DamageType, result.DamageType);
            Assert.AreEqual(original.HitPosX, result.HitPosX, 0.0001);
            Assert.AreEqual(original.HitPosY, result.HitPosY, 0.0001);
            Assert.AreEqual(original.HitPosZ, result.HitPosZ, 0.0001);
            Assert.AreEqual(original.WeaponName, result.WeaponName);
            Assert.AreEqual(original.HitPartName, result.HitPartName);
        }

        [Test]
        public void Damage_LegacyPayloadWithoutPartName_Deserializes()
        {
            var original = new DamagePacket
            {
                VictimId = 10,
                AttackerId = 20,
                AttackerLifeId = 3,
                DamageSequence = 77,
                WeaponName = "M61A1",
                HitPartName = "WingLeft"
            };
            byte[] full = PacketSerializer.SerializeDamage(original);

            // Strip the trailing part-name string (1 length byte + 8 chars)
            byte[] legacy = new byte[full.Length - 9];
            System.Array.Copy(full, legacy, legacy.Length);

            var result = PacketSerializer.DeserializeDamage(legacy);
            Assert.AreEqual(10UL, result.VictimId);
            Assert.AreEqual(3U, result.AttackerLifeId);
            Assert.AreEqual(77U, result.DamageSequence);
            Assert.IsTrue(string.IsNullOrEmpty(result.HitPartName));
        }

        [Test]
        public void PartDestroyed_Roundtrip()
        {
            var original = new PartDestroyedPacket
            {
                VictimId = 42,
                PartName = "StabilizerVertical"
            };

            byte[] data = PacketSerializer.SerializePartDestroyed(original);
            var result = PacketSerializer.DeserializePartDestroyed(data);

            Assert.AreEqual(original.VictimId, result.VictimId);
            Assert.AreEqual(original.PartName, result.PartName);
        }

        [Test]
        public void Damage_NullWeaponName_SerializesAsUnknown()
        {
            var original = new DamagePacket
            {
                VictimId = 1,
                AttackerId = 2,
                WeaponName = null
            };

            byte[] data = PacketSerializer.SerializeDamage(original);
            var result = PacketSerializer.DeserializeDamage(data);

            Assert.AreEqual("Unknown", result.WeaponName);
        }

        [Test]
        public void Damage_MaxValues_Roundtrip()
        {
            var original = new DamagePacket
            {
                VictimId = ulong.MaxValue,
                AttackerId = ulong.MaxValue,
                Damage = int.MaxValue,
                Penetration = int.MaxValue,
                DamageType = byte.MaxValue,
                HitPosX = double.MaxValue,
                HitPosY = double.MaxValue,
                HitPosZ = double.MaxValue,
                WeaponName = "TestWeapon"
            };

            byte[] data = PacketSerializer.SerializeDamage(original);
            var result = PacketSerializer.DeserializeDamage(data);

            Assert.AreEqual(ulong.MaxValue, result.VictimId);
            Assert.AreEqual(ulong.MaxValue, result.AttackerId);
            Assert.AreEqual(int.MaxValue, result.Damage);
            Assert.AreEqual(int.MaxValue, result.Penetration);
            Assert.AreEqual(byte.MaxValue, result.DamageType);
            Assert.AreEqual(double.MaxValue, result.HitPosX);
        }

        [Test]
        public void Damage_Truncated_ReturnsDefault()
        {
            // minBytes = 50
            var result = PacketSerializer.DeserializeDamage(new byte[10]);
            Assert.AreEqual(0UL, result.VictimId);
        }

        [Test]
        public void Damage_NullData_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeDamage(null);
            Assert.AreEqual(0UL, result.VictimId);
        }

        // ═══════════════════════════════════════════════════════════
        //  LobbyStatePacket — nested array of LobbyPlayerInfo
        // ═══════════════════════════════════════════════════════════

        [Test]
        public void LobbyState_Roundtrip_WithPlayers()
        {
            var original = new LobbyStatePacket
            {
                HostName = "TopGun",
                MapName = "Valley",
                SpawnType = LobbySpawnType.Runway,
                GameStarted = false,
                GameLoading = true,
                AircraftCollisionsEnabled = true,
                TimeOfDay = TimeOfDay.Evening,
                GameMode = MultiplayerGameMode.TeamDogfight,
                MaxPlayersTotal = 8,
                TeamCount = 4,
                Revision = 42,
                Players = new[]
                {
                    new LobbyPlayerInfo
                    {
                        PeerId = 1,
                        PlayerName = "Maverick",
                        SelectedAirfield = "Miramar",
                        SelectedAircraft = "F-14A",
                        SelectedLoadout = "CAP",
                        IsReady = true,
                        IsLoaded = false,
                        IsHost = true,
                        Team = MultiplayerTeam.Team1
                    },
                    new LobbyPlayerInfo
                    {
                        PeerId = 2,
                        PlayerName = "Goose",
                        SelectedAirfield = "Miramar",
                        SelectedAircraft = "F-14A",
                        SelectedLoadout = "Clean",
                        IsReady = true,
                        IsLoaded = true,
                        IsHost = false,
                        Team = MultiplayerTeam.Team4
                    }
                }
            };

            byte[] data = PacketSerializer.SerializeLobbyState(original);
            var result = PacketSerializer.DeserializeLobbyState(data);

            Assert.IsNotNull(result);
            Assert.AreEqual(original.HostName, result.HostName);
            Assert.AreEqual(original.MapName, result.MapName);
            Assert.AreEqual(original.SpawnType, result.SpawnType);
            Assert.AreEqual(original.GameStarted, result.GameStarted);
            Assert.AreEqual(original.GameLoading, result.GameLoading);
            Assert.AreEqual(original.AircraftCollisionsEnabled, result.AircraftCollisionsEnabled);
            Assert.AreEqual(original.TimeOfDay, result.TimeOfDay);
            Assert.AreEqual(original.GameMode, result.GameMode);
            Assert.AreEqual(original.MaxPlayersTotal, result.MaxPlayersTotal);
            Assert.AreEqual(original.TeamCount, result.TeamCount);
            Assert.AreEqual(original.Revision, result.Revision);

            Assert.IsNotNull(result.Players);
            Assert.AreEqual(2, result.Players.Length);

            // Player 0
            Assert.AreEqual(1UL, result.Players[0].PeerId);
            Assert.AreEqual("Maverick", result.Players[0].PlayerName);
            Assert.AreEqual("Miramar", result.Players[0].SelectedAirfield);
            Assert.AreEqual("F-14A", result.Players[0].SelectedAircraft);
            Assert.AreEqual("CAP", result.Players[0].SelectedLoadout);
            Assert.IsTrue(result.Players[0].IsReady);
            Assert.IsFalse(result.Players[0].IsLoaded);
            Assert.IsTrue(result.Players[0].IsHost);
            Assert.AreEqual(MultiplayerTeam.Team1, result.Players[0].Team);

            // Player 1
            Assert.AreEqual(2UL, result.Players[1].PeerId);
            Assert.AreEqual("Goose", result.Players[1].PlayerName);
            Assert.AreEqual("Miramar", result.Players[1].SelectedAirfield);
            Assert.AreEqual("F-14A", result.Players[1].SelectedAircraft);
            Assert.AreEqual("Clean", result.Players[1].SelectedLoadout);
            Assert.IsTrue(result.Players[1].IsReady);
            Assert.IsTrue(result.Players[1].IsLoaded);
            Assert.IsFalse(result.Players[1].IsHost);
            Assert.AreEqual(MultiplayerTeam.Team4, result.Players[1].Team);
        }

        [Test]
        public void LobbyState_EmptyPlayers_Roundtrip()
        {
            var original = new LobbyStatePacket
            {
                HostName = "Host",
                MapName = "TestMap",
                Players = new LobbyPlayerInfo[0]
            };

            byte[] data = PacketSerializer.SerializeLobbyState(original);
            var result = PacketSerializer.DeserializeLobbyState(data);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result.Players);
            Assert.AreEqual(0, result.Players.Length);
        }

        [Test]
        public void LobbyState_NullPlayers_SerializesAsZeroCount()
        {
            var original = new LobbyStatePacket
            {
                HostName = "Host",
                MapName = "TestMap",
                Players = null
            };

            byte[] data = PacketSerializer.SerializeLobbyState(original);
            var result = PacketSerializer.DeserializeLobbyState(data);

            Assert.IsNotNull(result);
            Assert.IsNotNull(result.Players);
            Assert.AreEqual(0, result.Players.Length);
        }

        [Test]
        public void LobbyState_NullStringFields_SerializeDefaults()
        {
            var original = new LobbyStatePacket
            {
                HostName = null,
                MapName = null,
                Players = new[]
                {
                    new LobbyPlayerInfo
                    {
                        PeerId = 1,
                        PlayerName = null,
                        SelectedAirfield = null,
                        SelectedAircraft = null
                    }
                }
            };

            byte[] data = PacketSerializer.SerializeLobbyState(original);
            var result = PacketSerializer.DeserializeLobbyState(data);

            Assert.IsNotNull(result);
            Assert.AreEqual("Host", result.HostName);   // null → "Host"
            Assert.AreEqual("", result.MapName);          // null → ""
            Assert.AreEqual("Player", result.Players[0].PlayerName);    // null → "Player"
            Assert.AreEqual("", result.Players[0].SelectedAirfield);    // null → ""
            Assert.AreEqual("", result.Players[0].SelectedAircraft);     // null → ""
            Assert.IsNull(result.Players[0].SelectedLoadout);
        }

        [Test]
        public void LobbyState_Truncated_ReturnsNull()
        {
            // LobbyStatePacket is a class → default is null
            // minBytes = 11
            var result = PacketSerializer.DeserializeLobbyState(new byte[3]);
            Assert.IsNull(result);
        }

        [Test]
        public void LobbyState_NullData_ReturnsNull()
        {
            var result = PacketSerializer.DeserializeLobbyState(null);
            Assert.IsNull(result);
        }

        // ═══════════════════════════════════════════════════════════
        //  ExplosionSyncPacket — two strings + mixed types
        // ═══════════════════════════════════════════════════════════

        [Test]
        public void ExplosionSync_Roundtrip()
        {
            var original = new ExplosionSyncPacket
            {
                ShooterId = 55,
                PosX = 10000.0, PosY = 500.0, PosZ = 20000.0,
                BlastRadius = 25.0f,
                ImpactDamage = 1000,
                WeaponName = "GBU-12",
                EffectPath = "Effects/Explosions/Large",
                ExplosionType = 1, // large/bomb
                ImpactSurface = 1  // ground
            };

            byte[] data = PacketSerializer.SerializeExplosionSync(original);
            var result = PacketSerializer.DeserializeExplosionSync(data);

            Assert.AreEqual(original.ShooterId, result.ShooterId);
            Assert.AreEqual(original.PosX, result.PosX, 0.001);
            Assert.AreEqual(original.PosY, result.PosY, 0.001);
            Assert.AreEqual(original.PosZ, result.PosZ, 0.001);
            Assert.AreEqual(original.BlastRadius, result.BlastRadius, 0.001f);
            Assert.AreEqual(original.ImpactDamage, result.ImpactDamage);
            Assert.AreEqual(original.WeaponName, result.WeaponName);
            Assert.AreEqual(original.EffectPath, result.EffectPath);
            Assert.AreEqual(original.ExplosionType, result.ExplosionType);
            Assert.AreEqual(original.ImpactSurface, result.ImpactSurface);
        }

        [Test]
        public void ExplosionSync_NullStrings_SerializeDefaults()
        {
            var original = new ExplosionSyncPacket
            {
                ShooterId = 1,
                WeaponName = null,
                EffectPath = null
            };

            byte[] data = PacketSerializer.SerializeExplosionSync(original);
            var result = PacketSerializer.DeserializeExplosionSync(data);

            Assert.AreEqual("Unknown", result.WeaponName); // null → "Unknown"
            Assert.AreEqual("", result.EffectPath);         // null → ""
        }

        [Test]
        public void ExplosionSync_Truncated_ReturnsDefault()
        {
            // minBytes = 44
            var result = PacketSerializer.DeserializeExplosionSync(new byte[10]);
            Assert.AreEqual(0UL, result.ShooterId);
        }

        [Test]
        public void ExplosionSync_NullData_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeExplosionSync(null);
            Assert.AreEqual(0UL, result.ShooterId);
        }

        // ═══════════════════════════════════════════════════════════
        //  LobbyPlayerJoinedPacket — simple struct
        // ═══════════════════════════════════════════════════════════

        [Test]
        public void LobbyPlayerJoined_Roundtrip()
        {
            var original = new LobbyPlayerJoinedPacket
            {
                PeerId = 999,
                PlayerName = "Viper"
            };

            byte[] data = PacketSerializer.SerializeLobbyPlayerJoined(original);
            var result = PacketSerializer.DeserializeLobbyPlayerJoined(data);

            Assert.AreEqual(original.PeerId, result.PeerId);
            Assert.AreEqual(original.PlayerName, result.PlayerName);
        }

        [Test]
        public void LobbyPlayerJoined_NullName_SerializesAsPlayer()
        {
            var original = new LobbyPlayerJoinedPacket
            {
                PeerId = 1,
                PlayerName = null
            };

            byte[] data = PacketSerializer.SerializeLobbyPlayerJoined(original);
            var result = PacketSerializer.DeserializeLobbyPlayerJoined(data);

            Assert.AreEqual("Player", result.PlayerName);
        }

        [Test]
        public void LobbyTeamSelect_Roundtrip()
        {
            var original = new LobbyTeamSelectPacket
            {
                PeerId = 77,
                Team = MultiplayerTeam.Team3
            };

            byte[] data = PacketSerializer.SerializeLobbyTeamSelect(original);
            var result = PacketSerializer.DeserializeLobbyTeamSelect(data);

            Assert.AreEqual(original.PeerId, result.PeerId);
            Assert.AreEqual(original.Team, result.Team);
        }

        [Test]
        public void LobbyPlayerJoined_Truncated_ReturnsDefault()
        {
            // minBytes = 9
            var result = PacketSerializer.DeserializeLobbyPlayerJoined(new byte[3]);
            Assert.AreEqual(0UL, result.PeerId);
        }

        [Test]
        public void LobbyPlayerJoined_NullData_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeLobbyPlayerJoined(null);
            Assert.AreEqual(0UL, result.PeerId);
        }

        // ═══════════════════════════════════════════════════════════
        //  Additional packet roundtrips (coverage)
        // ═══════════════════════════════════════════════════════════

        [Test]
        public void WeaponFire_Roundtrip()
        {
            var original = new WeaponFirePacket
            {
                PlayerId = 77,
                WeaponType = 1,  // Missile
                WeaponIndex = 3,
                TargetId = 88
            };

            byte[] data = PacketSerializer.SerializeWeaponFire(original);
            var result = PacketSerializer.DeserializeWeaponFire(data);

            Assert.AreEqual(original.PlayerId, result.PlayerId);
            Assert.AreEqual(original.WeaponType, result.WeaponType);
            Assert.AreEqual(original.WeaponIndex, result.WeaponIndex);
            Assert.AreEqual(original.TargetId, result.TargetId);
        }

        [Test]
        public void WeaponFire_Truncated_ReturnsDefault()
        {
            // minBytes = 18
            var result = PacketSerializer.DeserializeWeaponFire(new byte[5]);
            Assert.AreEqual(0UL, result.PlayerId);
        }

        [Test]
        public void RadarLock_Roundtrip()
        {
            var original = new RadarLockPacket
            {
                LockerId = 10,
                TargetId = 20,
                IsLocked = true,
                LockType = 0 // radar
            };

            byte[] data = PacketSerializer.SerializeRadarLock(original);
            var result = PacketSerializer.DeserializeRadarLock(data);

            Assert.AreEqual(original.LockerId, result.LockerId);
            Assert.AreEqual(original.TargetId, result.TargetId);
            Assert.AreEqual(original.IsLocked, result.IsLocked);
            Assert.AreEqual(original.LockType, result.LockType);
        }

        [Test]
        public void KillConfirm_Roundtrip()
        {
            var original = new KillConfirmPacket
            {
                KillerId = 100,
                VictimId = 200,
                DeathSequence = 3,
                WeaponName = "AIM-9L"
            };

            byte[] data = PacketSerializer.SerializeKillConfirm(original);
            var result = PacketSerializer.DeserializeKillConfirm(data);

            Assert.AreEqual(original.KillerId, result.KillerId);
            Assert.AreEqual(original.VictimId, result.VictimId);
            Assert.AreEqual(original.DeathSequence, result.DeathSequence);
            Assert.AreEqual(original.WeaponName, result.WeaponName);
        }

        [Test]
        public void KillConfirm_NullWeaponName_SerializesAsUnknown()
        {
            var original = new KillConfirmPacket
            {
                KillerId = 1,
                VictimId = 2,
                WeaponName = null
            };

            byte[] data = PacketSerializer.SerializeKillConfirm(original);
            var result = PacketSerializer.DeserializeKillConfirm(data);

            Assert.AreEqual("Unknown", result.WeaponName);
        }

        [Test]
        public void KillConfirm_LegacyWithoutDeathSequence_DefaultsToZero()
        {
            byte[] data;
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(1UL);
                w.Write(2UL);
                w.Write("Legacy");
                data = ms.ToArray();
            }

            var result = PacketSerializer.DeserializeKillConfirm(data);

            Assert.AreEqual(1UL, result.KillerId);
            Assert.AreEqual(2UL, result.VictimId);
            Assert.AreEqual("Legacy", result.WeaponName);
            Assert.AreEqual(0u, result.DeathSequence);
        }

        [Test]
        public void AircraftDestroyed_Roundtrip()
        {
            var original = new AircraftDestroyedPacket
            {
                VictimId = 42,
                DeathSequence = 5,
                Reason = "terrain/self"
            };

            byte[] data = PacketSerializer.SerializeAircraftDestroyed(original);
            var result = PacketSerializer.DeserializeAircraftDestroyed(data);

            Assert.AreEqual(original.VictimId, result.VictimId);
            Assert.AreEqual(original.DeathSequence, result.DeathSequence);
            Assert.AreEqual(original.Reason, result.Reason);
        }

        [Test]
        public void DeathReport_Roundtrip()
        {
            var original = new DeathReportPacket
            {
                VictimId = 2,
                KillerId = 1,
                LifeId = 9,
                WeaponName = "AIM-120B",
                Reason = "killed"
            };

            byte[] data = PacketSerializer.SerializeDeathReport(original);
            var result = PacketSerializer.DeserializeDeathReport(data);

            Assert.AreEqual(original.VictimId, result.VictimId);
            Assert.AreEqual(original.KillerId, result.KillerId);
            Assert.AreEqual(original.LifeId, result.LifeId);
            Assert.AreEqual(original.WeaponName, result.WeaponName);
            Assert.AreEqual(original.Reason, result.Reason);
        }

        [Test]
        public void ScoreEvent_Roundtrip()
        {
            var original = new ScoreEventPacket
            {
                VictimId = 2,
                KillerId = 1,
                LifeId = 9,
                WeaponName = "Fire",
                Reason = "recent-remote-damage"
            };

            byte[] data = PacketSerializer.SerializeScoreEvent(original);
            var result = PacketSerializer.DeserializeScoreEvent(data);

            Assert.AreEqual(original.VictimId, result.VictimId);
            Assert.AreEqual(original.KillerId, result.KillerId);
            Assert.AreEqual(original.LifeId, result.LifeId);
            Assert.AreEqual(original.WeaponName, result.WeaponName);
            Assert.AreEqual(original.Reason, result.Reason);
        }

        [Test]
        public void BombDrop_Roundtrip()
        {
            var original = new BombDropPacket
            {
                ShooterId = 33,
                BombType = "Mk-82",
                BombInstanceId = 12,
                LaunchPosX = 1000.0,
                LaunchPosY = 5000.0,
                LaunchPosZ = 2000.0,
                VelX = 100f,
                VelY = -50f,
                VelZ = 200f,
                Timestamp = 42.5f,
                IsActive = true,
                RemovalReason = 1
            };

            byte[] data = PacketSerializer.SerializeBombDrop(original);
            var result = PacketSerializer.DeserializeBombDrop(data);

            Assert.AreEqual(original.ShooterId, result.ShooterId);
            Assert.AreEqual(original.BombType, result.BombType);
            Assert.AreEqual(original.BombInstanceId, result.BombInstanceId);
            Assert.AreEqual(original.LaunchPosX, result.LaunchPosX, 0.001);
            Assert.AreEqual(original.LaunchPosY, result.LaunchPosY, 0.001);
            Assert.AreEqual(original.LaunchPosZ, result.LaunchPosZ, 0.001);
            Assert.AreEqual(original.VelX, result.VelX, 0.001f);
            Assert.AreEqual(original.VelY, result.VelY, 0.001f);
            Assert.AreEqual(original.VelZ, result.VelZ, 0.001f);
            Assert.AreEqual(original.Timestamp, result.Timestamp, 0.001f);
            Assert.AreEqual(original.IsActive, result.IsActive);
            Assert.AreEqual(original.RemovalReason, result.RemovalReason);
        }

        [Test]
        public void BombDrop_LegacyWithoutInstanceId_DefaultsToActive()
        {
            byte[] data;
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(33UL);
                w.Write("Mk-82");
                w.Write(1000.0);
                w.Write(5000.0);
                w.Write(2000.0);
                w.Write(100f);
                w.Write(-50f);
                w.Write(200f);
                w.Write(42.5f);
                data = ms.ToArray();
            }

            var result = PacketSerializer.DeserializeBombDrop(data);

            Assert.AreEqual(33UL, result.ShooterId);
            Assert.AreEqual("Mk-82", result.BombType);
            Assert.AreEqual(0, result.BombInstanceId);
            Assert.AreEqual(1000.0, result.LaunchPosX, 0.001);
            Assert.AreEqual(42.5f, result.Timestamp, 0.001f);
            Assert.IsTrue(result.IsActive);
            Assert.AreEqual(0, result.RemovalReason);
        }

        [Test]
        public void MissileUpdate_Roundtrip()
        {
            var original = new MissileUpdatePacket
            {
                ShooterId = 10,
                MissileInstanceId = 5,
                TargetId = 20,
                IsTracking = true
            };

            byte[] data = PacketSerializer.SerializeMissileUpdate(original);
            var result = PacketSerializer.DeserializeMissileUpdate(data);

            Assert.AreEqual(original.ShooterId, result.ShooterId);
            Assert.AreEqual(original.MissileInstanceId, result.MissileInstanceId);
            Assert.AreEqual(original.TargetId, result.TargetId);
            Assert.AreEqual(original.IsTracking, result.IsTracking);
        }

        [Test]
        public void MissilePositionSync_Roundtrip()
        {
            var original = new MissilePositionSyncPacket
            {
                MissileInstanceId = 7,
                PosX = 5000.5, PosY = 1000.5, PosZ = 8000.5,
                VelX = 300f, VelY = 10f, VelZ = -50f,
                IsActive = true,
                RemovalReason = 2,
                MotorActive = false,
                ShooterId = 99
            };

            byte[] data = PacketSerializer.SerializeMissilePositionSync(original);
            var result = PacketSerializer.DeserializeMissilePositionSync(data);

            Assert.AreEqual(original.MissileInstanceId, result.MissileInstanceId);
            Assert.AreEqual(original.PosX, result.PosX, 0.001);
            Assert.AreEqual(original.PosY, result.PosY, 0.001);
            Assert.AreEqual(original.PosZ, result.PosZ, 0.001);
            Assert.AreEqual(original.VelX, result.VelX, 0.001f);
            Assert.AreEqual(original.VelY, result.VelY, 0.001f);
            Assert.AreEqual(original.VelZ, result.VelZ, 0.001f);
            Assert.AreEqual(original.IsActive, result.IsActive);
            Assert.AreEqual(original.RemovalReason, result.RemovalReason);
            Assert.AreEqual(original.MotorActive, result.MotorActive);
            Assert.AreEqual(original.ShooterId, result.ShooterId);
        }

        [Test]
        public void MissilePositionSync_LegacyWithoutMotorActive_DefaultsToActiveState()
        {
            byte[] data;
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(7);
                w.Write(5000.5);
                w.Write(1000.5);
                w.Write(8000.5);
                w.Write(300f);
                w.Write(10f);
                w.Write(-50f);
                w.Write(true);
                w.Write((byte)2);
                data = ms.ToArray();
            }

            var result = PacketSerializer.DeserializeMissilePositionSync(data);

            Assert.AreEqual(7, result.MissileInstanceId);
            Assert.IsTrue(result.IsActive);
            Assert.AreEqual(2, result.RemovalReason);
            Assert.IsTrue(result.MotorActive);
            Assert.AreEqual(0UL, result.ShooterId);
        }

        [Test]
        public void AircraftCollision_Roundtrip()
        {
            var original = new AircraftCollisionPacket
            {
                PlayerA = 10,
                PlayerB = 20,
                PosX = 5000.0, PosY = 1000.0, PosZ = 3000.0,
                NormalX = 1f, NormalY = 0f, NormalZ = 0f,
                DamageA = 500,
                DamageB = 300,
                RelativeSpeed = 250.5f
            };

            byte[] data = PacketSerializer.SerializeAircraftCollision(original);
            var result = PacketSerializer.DeserializeAircraftCollision(data);

            Assert.AreEqual(original.PlayerA, result.PlayerA);
            Assert.AreEqual(original.PlayerB, result.PlayerB);
            Assert.AreEqual(original.PosX, result.PosX, 0.001);
            Assert.AreEqual(original.PosY, result.PosY, 0.001);
            Assert.AreEqual(original.PosZ, result.PosZ, 0.001);
            Assert.AreEqual(original.NormalX, result.NormalX, 0.001f);
            Assert.AreEqual(original.NormalY, result.NormalY, 0.001f);
            Assert.AreEqual(original.NormalZ, result.NormalZ, 0.001f);
            Assert.AreEqual(original.DamageA, result.DamageA);
            Assert.AreEqual(original.DamageB, result.DamageB);
            Assert.AreEqual(original.RelativeSpeed, result.RelativeSpeed, 0.001f);
        }

        [Test]
        public void AircraftChanged_Roundtrip()
        {
            var original = new AircraftChangedPacket
            {
                PlayerId = 42,
                AircraftType = "F-16C",
                IsAlive = true,
                LoadoutName = "Air Superiority"
            };

            byte[] data = PacketSerializer.SerializeAircraftChanged(original);
            var result = PacketSerializer.DeserializeAircraftChanged(data);

            Assert.AreEqual(original.PlayerId, result.PlayerId);
            Assert.AreEqual(original.AircraftType, result.AircraftType);
            Assert.AreEqual(original.IsAlive, result.IsAlive);
            Assert.AreEqual(original.LoadoutName, result.LoadoutName);
        }

        [Test]
        public void AircraftChanged_LegacyPayloadWithoutLoadout_Deserializes()
        {
            var original = new AircraftChangedPacket
            {
                PlayerId = 5,
                AircraftType = "AV8B",
                IsAlive = true,
                LoadoutName = "Clean"
            };
            byte[] full = PacketSerializer.SerializeAircraftChanged(original);

            // Strip the trailing loadout string (1 length byte + 5 chars)
            byte[] legacy = new byte[full.Length - 6];
            System.Array.Copy(full, legacy, legacy.Length);

            var result = PacketSerializer.DeserializeAircraftChanged(legacy);
            Assert.AreEqual(5UL, result.PlayerId);
            Assert.AreEqual("AV8B", result.AircraftType);
            Assert.IsTrue(result.IsAlive);
            Assert.IsTrue(string.IsNullOrEmpty(result.LoadoutName));
        }

        [Test]
        public void AircraftDestructionVfx_Roundtrip()
        {
            var original = new AircraftDestructionVfxPacket
            {
                VictimId = 99,
                PosX = 1000.0, PosY = 0.0, PosZ = 2000.0,
                RotX = 0f, RotY = 0.707f, RotZ = 0f, RotW = 0.707f,
                DestructionReason = 2, // GroundHard
                VelX = 150f, VelY = -12.5f, VelZ = 88f
            };

            byte[] data = PacketSerializer.SerializeAircraftDestructionVfx(original);
            var result = PacketSerializer.DeserializeAircraftDestructionVfx(data);

            Assert.AreEqual(original.VictimId, result.VictimId);
            Assert.AreEqual(original.PosX, result.PosX, 0.001);
            Assert.AreEqual(original.PosY, result.PosY, 0.001);
            Assert.AreEqual(original.PosZ, result.PosZ, 0.001);
            Assert.AreEqual(original.RotX, result.RotX, 0.0001f);
            Assert.AreEqual(original.RotY, result.RotY, 0.0001f);
            Assert.AreEqual(original.RotZ, result.RotZ, 0.0001f);
            Assert.AreEqual(original.RotW, result.RotW, 0.0001f);
            Assert.AreEqual(original.DestructionReason, result.DestructionReason);
            Assert.AreEqual(original.VelX, result.VelX, 0.0001f);
            Assert.AreEqual(original.VelY, result.VelY, 0.0001f);
            Assert.AreEqual(original.VelZ, result.VelZ, 0.0001f);
        }

        [Test]
        public void AircraftDestructionVfx_EjectedReason_Roundtrips()
        {
            var original = new AircraftDestructionVfxPacket
            {
                VictimId = 7,
                DestructionReason = AircraftDestructionVfxPacket.ReasonPilotsEjected
            };

            byte[] data = PacketSerializer.SerializeAircraftDestructionVfx(original);
            var result = PacketSerializer.DeserializeAircraftDestructionVfx(data);

            Assert.AreEqual(AircraftDestructionVfxPacket.ReasonPilotsEjected, result.DestructionReason);
        }

        [Test]
        public void AircraftDestructionVfx_LegacyPayloadWithoutVelocity_Deserializes()
        {
            // Pre-velocity payload: VictimId(8) + Pos(24) + Rot(16) + Reason(1) = 49 bytes
            var original = new AircraftDestructionVfxPacket
            {
                VictimId = 42,
                PosX = 10.0, PosY = 20.0, PosZ = 30.0,
                RotW = 1f,
                DestructionReason = 1
            };
            byte[] full = PacketSerializer.SerializeAircraftDestructionVfx(original);
            byte[] legacy = new byte[49];
            System.Array.Copy(full, legacy, 49);

            var result = PacketSerializer.DeserializeAircraftDestructionVfx(legacy);

            Assert.AreEqual(42UL, result.VictimId);
            Assert.AreEqual(1, result.DestructionReason);
            Assert.AreEqual(0f, result.VelX);
            Assert.AreEqual(0f, result.VelY);
            Assert.AreEqual(0f, result.VelZ);
        }

        [Test]
        public void CraterSpawn_Roundtrip()
        {
            var original = new CraterSpawnPacket
            {
                PosX = 9999.9, PosY = 100.0, PosZ = 5555.5,
                CraterSize = 3 // Huge
            };

            byte[] data = PacketSerializer.SerializeCraterSpawn(original);
            var result = PacketSerializer.DeserializeCraterSpawn(data);

            Assert.AreEqual(original.PosX, result.PosX, 0.001);
            Assert.AreEqual(original.PosY, result.PosY, 0.001);
            Assert.AreEqual(original.PosZ, result.PosZ, 0.001);
            Assert.AreEqual(original.CraterSize, result.CraterSize);
        }

        [Test]
        public void BuildingDestroy_Roundtrip()
        {
            var original = new BuildingDestroyPacket
            {
                BuildingInstanceId = 12345,
                PosX = 1000.0, PosY = 0.0, PosZ = 2000.0,
                ObjectName = "Hangar A",
                TargetType = 9,
                ObjectKind = 1
            };

            byte[] data = PacketSerializer.SerializeBuildingDestroy(original);
            var result = PacketSerializer.DeserializeBuildingDestroy(data);

            Assert.AreEqual(original.BuildingInstanceId, result.BuildingInstanceId);
            Assert.AreEqual(original.PosX, result.PosX, 0.001);
            Assert.AreEqual(original.PosY, result.PosY, 0.001);
            Assert.AreEqual(original.PosZ, result.PosZ, 0.001);
            Assert.AreEqual(original.ObjectName, result.ObjectName);
            Assert.AreEqual(original.TargetType, result.TargetType);
            Assert.AreEqual(original.ObjectKind, result.ObjectKind);
        }

        [Test]
        public void BuildingDestroy_LegacyWithoutHints_Roundtrip()
        {
            byte[] data;
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(12345);
                w.Write(1000.0);
                w.Write(0.0);
                w.Write(2000.0);
                data = ms.ToArray();
            }

            var result = PacketSerializer.DeserializeBuildingDestroy(data);

            Assert.AreEqual(12345, result.BuildingInstanceId);
            Assert.AreEqual(1000.0, result.PosX, 0.001);
            Assert.AreEqual(0.0, result.PosY, 0.001);
            Assert.AreEqual(2000.0, result.PosZ, 0.001);
            Assert.AreEqual("", result.ObjectName);
            Assert.AreEqual(255, result.TargetType);
            Assert.AreEqual(0, result.ObjectKind);
        }

        // ═══════════════════════════════════════════════════════════
        //  Lobby sub-packets
        // ═══════════════════════════════════════════════════════════

        [Test]
        public void LobbyPlayerLeft_Roundtrip()
        {
            var original = new LobbyPlayerLeftPacket { PeerId = 42 };

            byte[] data = PacketSerializer.SerializeLobbyPlayerLeft(original);
            var result = PacketSerializer.DeserializeLobbyPlayerLeft(data);

            Assert.AreEqual(original.PeerId, result.PeerId);
        }

        [Test]
        public void LobbyPlayerReady_Roundtrip()
        {
            var original = new LobbyPlayerReadyPacket { PeerId = 5, IsReady = true };

            byte[] data = PacketSerializer.SerializeLobbyPlayerReady(original);
            var result = PacketSerializer.DeserializeLobbyPlayerReady(data);

            Assert.AreEqual(original.PeerId, result.PeerId);
            Assert.AreEqual(original.IsReady, result.IsReady);
        }

        [Test]
        public void LobbyWelcome_Roundtrip()
        {
            var original = new LobbyWelcomePacket
            {
                AssignedPeerId = 999,
                HostName = "TopGunHost"
            };

            byte[] data = PacketSerializer.SerializeLobbyWelcome(original);
            var result = PacketSerializer.DeserializeLobbyWelcome(data);

            Assert.AreEqual(original.AssignedPeerId, result.AssignedPeerId);
            Assert.AreEqual(original.HostName, result.HostName);
        }

        [Test]
        public void LobbySpawnSettings_Roundtrip()
        {
            var original = new LobbySpawnSettingsPacket
            {
                SpawnType = LobbySpawnType.Ramp,
                MapName = "Desert"
            };

            byte[] data = PacketSerializer.SerializeLobbySpawnSettings(original);
            var result = PacketSerializer.DeserializeLobbySpawnSettings(data);

            Assert.AreEqual(original.SpawnType, result.SpawnType);
            Assert.AreEqual(original.MapName, result.MapName);
        }

        [Test]
        public void LobbyStartGame_Roundtrip()
        {
            var original = new LobbyStartGamePacket
            {
                MapName = "Valley",
                SpawnType = LobbySpawnType.Air,
                TimeOfDay = TimeOfDay.Night
            };

            byte[] data = PacketSerializer.SerializeLobbyStartGame(original);
            var result = PacketSerializer.DeserializeLobbyStartGame(data);

            Assert.AreEqual(original.MapName, result.MapName);
            Assert.AreEqual(original.SpawnType, result.SpawnType);
            Assert.AreEqual(original.TimeOfDay, result.TimeOfDay);
        }

        [Test]
        public void ModManifest_Roundtrip()
        {
            var manifestBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 };
            var original = new ModManifestPacket
            {
                PeerId = 42,
                ManifestData = manifestBytes,
                ModVersion = "0.2.1"
            };

            byte[] data = PacketSerializer.SerializeModManifest(original);
            var result = PacketSerializer.DeserializeModManifest(data);

            Assert.AreEqual(original.PeerId, result.PeerId);
            CollectionAssert.AreEqual(manifestBytes, result.ManifestData);
            Assert.AreEqual(original.ModVersion, result.ModVersion);
        }

        [Test]
        public void ModManifest_NullData_RoundtripsAsNull()
        {
            var original = new ModManifestPacket
            {
                PeerId = 1,
                ManifestData = null
            };

            byte[] data = PacketSerializer.SerializeModManifest(original);
            var result = PacketSerializer.DeserializeModManifest(data);

            Assert.AreEqual(original.PeerId, result.PeerId);
            Assert.IsNull(result.ManifestData);
            Assert.AreEqual("", result.ModVersion);
        }

        [Test]
        public void ModCompatibilityResult_Roundtrip()
        {
            var hostManifest = new byte[] { 0x10, 0x20, 0x30 };
            var original = new ModCompatibilityResultPacket
            {
                PeerId = 42,
                IsCompatible = false,
                RejectionReason = "Missing mod: CoolPlanes v2.0",
                HostModVersion = "0.2.1",
                HostManifestData = hostManifest
            };

            byte[] data = PacketSerializer.SerializeModCompatibilityResult(original);
            var result = PacketSerializer.DeserializeModCompatibilityResult(data);

            Assert.AreEqual(original.PeerId, result.PeerId);
            Assert.AreEqual(original.IsCompatible, result.IsCompatible);
            Assert.AreEqual(original.RejectionReason, result.RejectionReason);
            Assert.AreEqual(original.HostModVersion, result.HostModVersion);
            CollectionAssert.AreEqual(hostManifest, result.HostManifestData);
        }

        [Test]
        public void ModSyncRequest_Roundtrip()
        {
            var original = new ModSyncRequestPacket
            {
                PeerId = 7,
                HostManifestHash = "abcdef0123456789"
            };

            byte[] data = PacketSerializer.SerializeModSyncRequest(original);
            var result = PacketSerializer.DeserializeModSyncRequest(data);

            Assert.AreEqual(original.PeerId, result.PeerId);
            Assert.AreEqual(original.HostManifestHash, result.HostManifestHash);
        }

        [Test]
        public void ModSyncChunk_Roundtrip()
        {
            var chunk = new byte[] { 1, 2, 3, 4, 5 };
            var original = new ModSyncChunkPacket
            {
                PeerId = 9,
                TransferId = 123,
                ChunkIndex = 2,
                ChunkCount = 4,
                TotalBytes = 42,
                ChunkData = chunk
            };

            byte[] data = PacketSerializer.SerializeModSyncChunk(original);
            var result = PacketSerializer.DeserializeModSyncChunk(data);

            Assert.AreEqual(original.PeerId, result.PeerId);
            Assert.AreEqual(original.TransferId, result.TransferId);
            Assert.AreEqual(original.ChunkIndex, result.ChunkIndex);
            Assert.AreEqual(original.ChunkCount, result.ChunkCount);
            Assert.AreEqual(original.TotalBytes, result.TotalBytes);
            CollectionAssert.AreEqual(chunk, result.ChunkData);
        }

        [Test]
        public void ModManifest_LegacyPayload_DefaultsVersionToEmpty()
        {
            var manifestBytes = new byte[] { 0xAA, 0xBB };
            byte[] data;
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(42UL);
                w.Write(manifestBytes.Length);
                w.Write(manifestBytes);
                data = ms.ToArray();
            }

            var result = PacketSerializer.DeserializeModManifest(data);

            Assert.AreEqual(42UL, result.PeerId);
            CollectionAssert.AreEqual(manifestBytes, result.ManifestData);
            Assert.AreEqual("", result.ModVersion);
        }

        [Test]
        public void ModCompatibilityResult_LegacyPayload_DefaultsHostVersionToEmpty()
        {
            byte[] data;
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(42UL);
                w.Write(true);
                w.Write("");
                data = ms.ToArray();
            }

            var result = PacketSerializer.DeserializeModCompatibilityResult(data);

            Assert.AreEqual(42UL, result.PeerId);
            Assert.IsTrue(result.IsCompatible);
            Assert.AreEqual("", result.RejectionReason);
            Assert.AreEqual("", result.HostModVersion);
        }

        // ═══════════════════════════════════════════════════════════
        //  SafeRead: truncated buffers across various types
        // ═══════════════════════════════════════════════════════════

        [Test]
        public void RadarLock_Truncated_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeRadarLock(new byte[5]);
            Assert.AreEqual(0UL, result.LockerId);
        }

        [Test]
        public void MissileUpdate_Truncated_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeMissileUpdate(new byte[5]);
            Assert.AreEqual(0UL, result.ShooterId);
        }

        [Test]
        public void MissilePositionSync_Truncated_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeMissilePositionSync(new byte[10]);
            Assert.AreEqual(0, result.MissileInstanceId);
        }

        [Test]
        public void AircraftCollision_Truncated_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeAircraftCollision(new byte[10]);
            Assert.AreEqual(0UL, result.PlayerA);
        }

        [Test]
        public void BombDrop_Truncated_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeBombDrop(new byte[10]);
            Assert.AreEqual(0UL, result.ShooterId);
        }

        [Test]
        public void CraterSpawn_Truncated_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeCraterSpawn(new byte[5]);
            Assert.AreEqual(0.0, result.PosX);
        }

        [Test]
        public void BuildingDestroy_Truncated_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeBuildingDestroy(new byte[5]);
            Assert.AreEqual(0, result.BuildingInstanceId);
        }

        [Test]
        public void KillConfirm_Truncated_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeKillConfirm(new byte[5]);
            Assert.AreEqual(0UL, result.KillerId);
        }

        [Test]
        public void AircraftDestructionVfx_Truncated_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeAircraftDestructionVfx(new byte[10]);
            Assert.AreEqual(0UL, result.VictimId);
        }

        [Test]
        public void AircraftChanged_Truncated_ReturnsDefault()
        {
            var result = PacketSerializer.DeserializeAircraftChanged(new byte[3]);
            Assert.AreEqual(0UL, result.PlayerId);
        }
    }
}
