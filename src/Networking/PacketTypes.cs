using System;
using System.IO;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Packet types for multiplayer communication
    /// </summary>
    public enum PacketType : byte
    {
        // Connection
        PlayerJoin = 1,
        PlayerLeave = 2,
        PlayerReady = 3,
        GameStart = 4,
        
        // State Sync (high frequency, unreliable)
        AircraftState = 10,
        
        // Input Sync
        ControlInput = 20,
        
        // Events (on occurrence, reliable)
        GunFiring = 30,
        GunStopped = 31,
        MissileLaunch = 32,
        RadarLock = 33,         // Sent when locking onto enemy (for RWR)
        RadarLockLost = 34,     // Sent when lock is broken
        
        // Damage (reliable)
        DamageDealt = 40,
        AircraftDestroyed = 41,
        ProjectileImpact = 42,  // Sent when projectile impacts for FX sync
        
        // Game State
        RequestRespawn = 50,
        Respawned = 51,
        AircraftChanged = 52,   // Sent when local player changes aircraft (respawn, new plane)
        Victory = 53,
        
        // Utility
        Ping = 100,
        Pong = 101,
        Chat = 102,
        
        // Lobby System (for synchronized multiplayer sessions)
        LobbyState = 60,           // Host broadcasts full lobby state
        LobbyPlayerJoined = 61,    // New player joined lobby
        LobbyPlayerLeft = 62,      // Player left lobby
        LobbyPlayerReady = 63,     // Player ready toggle
        LobbyAirfieldSelect = 64,  // Player selected airfield
        LobbySpawnSettings = 65,   // Host spawn settings (Air/Runway/Ramp)
        LobbyStartGame = 66,       // Host starts game - begin loading
        LobbyLoadingComplete = 67, // Player finished loading map
        LobbySpawnPlayers = 68,    // Trigger synchronized spawn
        LobbyRespawnRequest = 69,  // Player requests respawn after death
        LobbyWelcome = 70          // Host welcomes client with assigned PeerID
    }
    
    /// <summary>
    /// Spawn location types for lobby system
    /// </summary>
    public enum LobbySpawnType : byte
    {
        Air = 0,
        Runway = 1,
        Ramp = 2
    }
    
    /// <summary>
    /// Lobby state packet - full state broadcast from host
    /// </summary>
    public class LobbyStatePacket
    {
        public string HostName;
        public string MapName;
        public LobbySpawnType SpawnType;
        public bool GameStarted;
        public bool GameLoading;
        public LobbyPlayerInfo[] Players;
    }
    
    /// <summary>
    /// Player info within lobby
    /// </summary>
    public class LobbyPlayerInfo
    {
        public ulong PeerId;
        public string PlayerName;
        public string SelectedAirfield;
        public string SelectedAircraft;
        public bool IsReady;
        public bool IsLoaded;
        public bool IsHost;
    }
    
    /// <summary>
    /// Player joined lobby packet
    /// </summary>
    public struct LobbyPlayerJoinedPacket
    {
        public ulong PeerId;
        public string PlayerName;
    }
    
    /// <summary>
    /// Player left lobby packet
    /// </summary>
    public struct LobbyPlayerLeftPacket
    {
        public ulong PeerId;
    }
    
    /// <summary>
    /// Player ready state packet
    /// </summary>
    public struct LobbyPlayerReadyPacket
    {
        public ulong PeerId;
        public bool IsReady;
    }
    
    /// <summary>
    /// Airfield selection packet
    /// </summary>
    public struct LobbyAirfieldSelectPacket
    {
        public ulong PeerId;
        public string AirfieldName;
    }
    
    /// <summary>
    /// Aircraft selection packet
    /// </summary>
    public struct LobbyAircraftSelectPacket
    {
        public ulong PeerId;
        public string AircraftName;
    }
    
    /// <summary>
    /// Spawn settings packet (host only)
    /// </summary>
    public struct LobbySpawnSettingsPacket
    {
        public LobbySpawnType SpawnType;
        public string MapName;
    }
    
    /// <summary>
    /// Start game packet (host only)
    /// </summary>
    public struct LobbyStartGamePacket
    {
        public string MapName;
        public LobbySpawnType SpawnType;
    }
    
    /// <summary>
    /// Loading complete packet
    /// </summary>
    public struct LobbyLoadingCompletePacket
    {
        public ulong PeerId;
    }
    
    /// <summary>
    /// Spawn players packet (host sends when all loaded)
    /// </summary>
    public struct LobbySpawnPlayersPacket
    {
        public float Timestamp;
    }
    
    /// <summary>
    /// Respawn request packet
    /// </summary>
    public struct LobbyRespawnRequestPacket
    {
        public ulong PeerId;
    }

    public struct LobbyWelcomePacket
    {
        public ulong AssignedPeerId;
        public string HostName;
    }

    /// <summary>
    /// Aircraft state for network synchronization
    /// </summary>
    public struct AircraftStatePacket
    {
        public ulong PlayerId;
        
        // Position (world space - doubles for precision with FloatingOrigin)
        public double PosX;
        public double PosY;
        public double PosZ;
        
        // Rotation (quaternion)
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
        
        // Velocity
        public float VelX;
        public float VelY;
        public float VelZ;
        
        // Angular velocity
        public float AngVelX;
        public float AngVelY;
        public float AngVelZ;
        
        // Control state
        public float Throttle;     // 0-1
        public float Pitch;        // -1 to 1
        public float Roll;         // -1 to 1
        public float Yaw;          // -1 to 1
        public float NozzleAngle;  // VTOL nozzle angle in degrees
        
        // Flags: Afterburner, GearDown, FlapsDown, Firing, etc.
        public byte Flags;
        
        // Timestamp for interpolation
        public float Timestamp;
        
        // Flag helpers
        public bool Afterburner
        {
            get => (Flags & 0x01) != 0;
            set => Flags = (byte)(value ? Flags | 0x01 : Flags & ~0x01);
        }
        
        public bool GearDown
        {
            get => (Flags & 0x02) != 0;
            set => Flags = (byte)(value ? Flags | 0x02 : Flags & ~0x02);
        }
        
        public bool FlapsDown
        {
            get => (Flags & 0x04) != 0;
            set => Flags = (byte)(value ? Flags | 0x04 : Flags & ~0x04);
        }
        
        public bool IsFiring
        {
            get => (Flags & 0x08) != 0;
            set => Flags = (byte)(value ? Flags | 0x08 : Flags & ~0x08);
        }
    }

    /// <summary>
    /// Weapon fire event packet
    /// </summary>
    public struct WeaponFirePacket
    {
        public ulong PlayerId;
        public byte WeaponType;    // 0 = Gun, 1 = Missile
        public byte WeaponIndex;   // Which hardpoint
        public ulong TargetId;     // For missiles with lock
    }

    /// <summary>
    /// Damage event packet - sent when attacker hits victim
    /// Uses absolute (double-precision) coordinates for floating-origin sync
    /// </summary>
    public struct DamagePacket
    {
        public ulong VictimId;      // Player who was hit
        public ulong AttackerId;    // Player who dealt the damage
        public int Damage;          // Amount of damage
        public int Penetration;     // Weapon penetration value
        public byte DamageType;     // 0 = bullet, 1 = missile, 2 = explosion
        public double HitPosX;      // Hit position (ABSOLUTE coordinates)
        public double HitPosY;
        public double HitPosZ;
        public string WeaponName;   // Weapon that caused damage
    }

    /// <summary>
    /// Missile launch packet - sent when a missile is fired at target
    /// </summary>
    public struct MissileLaunchPacket
    {
        public ulong ShooterId;     // Player who launched
        public ulong TargetId;      // Target player (if locked)
        public string MissileType;  // e.g. "AIM-9L", "AIM-120"
        public byte SeekerType;     // 0 = IR, 1 = Radar, 2 = Unguided
        public float LaunchPosX;    // Launch position
        public float LaunchPosY;
        public float LaunchPosZ;
        public float LaunchDirX;    // Launch direction
        public float LaunchDirY;
        public float LaunchDirZ;
    }

    /// <summary>
    /// Radar lock packet - sent when locking/unlocking target (for RWR)
    /// </summary>
    public struct RadarLockPacket
    {
        public ulong LockerId;      // Player doing the locking
        public ulong TargetId;      // Player being locked
        public bool IsLocked;       // True = locked, False = lost lock
        public byte LockType;       // 0 = radar, 1 = IR
    }

    /// <summary>
    /// Aircraft changed packet - sent when respawning or changing aircraft
    /// </summary>
    public struct AircraftChangedPacket
    {
        public ulong PlayerId;
        public string AircraftType; // e.g. "F-16", "AV-8B"
        public bool IsAlive;        // True = alive, False = destroyed
    }

    /// <summary>
    /// Projectile impact packet - sent when projectile hits for VFX sync
    /// Uses absolute (double-precision) coordinates for position
    /// </summary>
    public struct ProjectileImpactPacket
    {
        public ulong AttackerId;    // Player who fired
        public ulong VictimId;      // Player who was hit (0 if terrain/other)
        public byte ImpactType;     // 0 = bullet, 1 = missile, 2 = explosion
        public byte EffectType;     // 0 = metal/hard, 1 = ground/soft, 2 = water, 3 = air
        
        // Impact position (absolute coordinates for floating-origin sync)
        public double ImpactPosX;
        public double ImpactPosY;
        public double ImpactPosZ;
        
        // Impact direction (normalized)
        public float ImpactDirX;
        public float ImpactDirY;
        public float ImpactDirZ;
        
        public int Damage;          // For scaling explosion size
        public string WeaponName;   // For effect selection
    }

    /// <summary>
    /// Serialization utilities for network packets
    /// </summary>
    public static class PacketSerializer
    {
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

        public static (PacketType, byte[]) Deserialize(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Empty packet data");
            
            PacketType type = (PacketType)data[0];
            byte[] payload = null;
            
            if (data.Length > 1)
            {
                payload = new byte[data.Length - 1];
                Array.Copy(data, 1, payload, 0, payload.Length);
            }
            
            return (type, payload);
        }

        public static byte[] SerializeAircraftState(AircraftStatePacket state)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(state.PlayerId);
                
                writer.Write(state.PosX);
                writer.Write(state.PosY);
                writer.Write(state.PosZ);
                
                writer.Write(state.RotX);
                writer.Write(state.RotY);
                writer.Write(state.RotZ);
                writer.Write(state.RotW);
                
                writer.Write(state.VelX);
                writer.Write(state.VelY);
                writer.Write(state.VelZ);
                
                writer.Write(state.AngVelX);
                writer.Write(state.AngVelY);
                writer.Write(state.AngVelZ);
                
                writer.Write(state.Throttle);
                writer.Write(state.Pitch);
                writer.Write(state.Roll);
                writer.Write(state.Yaw);
                writer.Write(state.NozzleAngle);
                
                writer.Write(state.Flags);
                writer.Write(state.Timestamp);
                
                return ms.ToArray();
            }
        }

        public static AircraftStatePacket DeserializeAircraftState(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new AircraftStatePacket
                {
                    PlayerId = reader.ReadUInt64(),
                    
                    PosX = reader.ReadDouble(),
                    PosY = reader.ReadDouble(),
                    PosZ = reader.ReadDouble(),
                    
                    RotX = reader.ReadSingle(),
                    RotY = reader.ReadSingle(),
                    RotZ = reader.ReadSingle(),
                    RotW = reader.ReadSingle(),
                    
                    VelX = reader.ReadSingle(),
                    VelY = reader.ReadSingle(),
                    VelZ = reader.ReadSingle(),
                    
                    AngVelX = reader.ReadSingle(),
                    AngVelY = reader.ReadSingle(),
                    AngVelZ = reader.ReadSingle(),
                    
                    Throttle = reader.ReadSingle(),
                    Pitch = reader.ReadSingle(),
                    Roll = reader.ReadSingle(),
                    Yaw = reader.ReadSingle(),
                    NozzleAngle = reader.ReadSingle(),
                    
                    Flags = reader.ReadByte(),
                    Timestamp = reader.ReadSingle()
                };
            }
        }

        public static byte[] SerializeWeaponFire(WeaponFirePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PlayerId);
                writer.Write(packet.WeaponType);
                writer.Write(packet.WeaponIndex);
                writer.Write(packet.TargetId);
                return ms.ToArray();
            }
        }

        public static WeaponFirePacket DeserializeWeaponFire(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new WeaponFirePacket
                {
                    PlayerId = reader.ReadUInt64(),
                    WeaponType = reader.ReadByte(),
                    WeaponIndex = reader.ReadByte(),
                    TargetId = reader.ReadUInt64()
                };
            }
        }

        public static byte[] SerializeDamage(DamagePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.VictimId);
                writer.Write(packet.AttackerId);
                writer.Write(packet.Damage);
                writer.Write(packet.Penetration);
                writer.Write(packet.DamageType);
                // Use double precision for absolute coordinates
                writer.Write(packet.HitPosX);
                writer.Write(packet.HitPosY);
                writer.Write(packet.HitPosZ);
                writer.Write(packet.WeaponName ?? "Unknown");
                return ms.ToArray();
            }
        }

        public static DamagePacket DeserializeDamage(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new DamagePacket
                {
                    VictimId = reader.ReadUInt64(),
                    AttackerId = reader.ReadUInt64(),
                    Damage = reader.ReadInt32(),
                    Penetration = reader.ReadInt32(),
                    DamageType = reader.ReadByte(),
                    // Read double precision for absolute coordinates
                    HitPosX = reader.ReadDouble(),
                    HitPosY = reader.ReadDouble(),
                    HitPosZ = reader.ReadDouble(),
                    WeaponName = reader.ReadString()
                };
            }
        }

        public static byte[] SerializeMissileLaunch(MissileLaunchPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.ShooterId);
                writer.Write(packet.TargetId);
                writer.Write(packet.MissileType ?? "Unknown");
                writer.Write(packet.SeekerType);
                writer.Write(packet.LaunchPosX);
                writer.Write(packet.LaunchPosY);
                writer.Write(packet.LaunchPosZ);
                writer.Write(packet.LaunchDirX);
                writer.Write(packet.LaunchDirY);
                writer.Write(packet.LaunchDirZ);
                return ms.ToArray();
            }
        }

        public static MissileLaunchPacket DeserializeMissileLaunch(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new MissileLaunchPacket
                {
                    ShooterId = reader.ReadUInt64(),
                    TargetId = reader.ReadUInt64(),
                    MissileType = reader.ReadString(),
                    SeekerType = reader.ReadByte(),
                    LaunchPosX = reader.ReadSingle(),
                    LaunchPosY = reader.ReadSingle(),
                    LaunchPosZ = reader.ReadSingle(),
                    LaunchDirX = reader.ReadSingle(),
                    LaunchDirY = reader.ReadSingle(),
                    LaunchDirZ = reader.ReadSingle()
                };
            }
        }

        public static byte[] SerializeRadarLock(RadarLockPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.LockerId);
                writer.Write(packet.TargetId);
                writer.Write(packet.IsLocked);
                writer.Write(packet.LockType);
                return ms.ToArray();
            }
        }

        public static RadarLockPacket DeserializeRadarLock(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new RadarLockPacket
                {
                    LockerId = reader.ReadUInt64(),
                    TargetId = reader.ReadUInt64(),
                    IsLocked = reader.ReadBoolean(),
                    LockType = reader.ReadByte()
                };
            }
        }

        public static byte[] SerializeAircraftChanged(AircraftChangedPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PlayerId);
                writer.Write(packet.AircraftType ?? "Unknown");
                writer.Write(packet.IsAlive);
                return ms.ToArray();
            }
        }

        public static AircraftChangedPacket DeserializeAircraftChanged(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new AircraftChangedPacket
                {
                    PlayerId = reader.ReadUInt64(),
                    AircraftType = reader.ReadString(),
                    IsAlive = reader.ReadBoolean()
                };
            }
        }

        public static byte[] SerializeProjectileImpact(ProjectileImpactPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.AttackerId);
                writer.Write(packet.VictimId);
                writer.Write(packet.ImpactType);
                writer.Write(packet.EffectType);
                writer.Write(packet.ImpactPosX);
                writer.Write(packet.ImpactPosY);
                writer.Write(packet.ImpactPosZ);
                writer.Write(packet.ImpactDirX);
                writer.Write(packet.ImpactDirY);
                writer.Write(packet.ImpactDirZ);
                writer.Write(packet.Damage);
                writer.Write(packet.WeaponName ?? "Unknown");
                return ms.ToArray();
            }
        }

        public static ProjectileImpactPacket DeserializeProjectileImpact(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new ProjectileImpactPacket
                {
                    AttackerId = reader.ReadUInt64(),
                    VictimId = reader.ReadUInt64(),
                    ImpactType = reader.ReadByte(),
                    EffectType = reader.ReadByte(),
                    ImpactPosX = reader.ReadDouble(),
                    ImpactPosY = reader.ReadDouble(),
                    ImpactPosZ = reader.ReadDouble(),
                    ImpactDirX = reader.ReadSingle(),
                    ImpactDirY = reader.ReadSingle(),
                    ImpactDirZ = reader.ReadSingle(),
                    Damage = reader.ReadInt32(),
                    WeaponName = reader.ReadString()
                };
            }
        }
        
        #region Lobby Packet Serialization
        
        public static byte[] SerializeLobbyState(LobbyStatePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.HostName ?? "Host");
                writer.Write(packet.MapName ?? "");
                writer.Write((byte)packet.SpawnType);
                writer.Write(packet.GameStarted);
                writer.Write(packet.GameLoading);
                
                int playerCount = packet.Players?.Length ?? 0;
                writer.Write(playerCount);
                
                if (packet.Players != null)
                {
                    foreach (var player in packet.Players)
                    {
                        writer.Write(player.PeerId);
                        writer.Write(player.PlayerName ?? "Player");
                        writer.Write(player.SelectedAirfield ?? "");
                        writer.Write(player.SelectedAircraft ?? "AV8B");
                        writer.Write(player.IsReady);
                        writer.Write(player.IsLoaded);
                        writer.Write(player.IsHost);
                    }
                }
                
                return ms.ToArray();
            }
        }
        
        public static LobbyStatePacket DeserializeLobbyState(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                var packet = new LobbyStatePacket
                {
                    HostName = reader.ReadString(),
                    MapName = reader.ReadString(),
                    SpawnType = (LobbySpawnType)reader.ReadByte(),
                    GameStarted = reader.ReadBoolean(),
                    GameLoading = reader.ReadBoolean()
                };
                
                int playerCount = reader.ReadInt32();
                packet.Players = new LobbyPlayerInfo[playerCount];
                
                for (int i = 0; i < playerCount; i++)
                {
                    packet.Players[i] = new LobbyPlayerInfo
                    {
                        PeerId = reader.ReadUInt64(),
                        PlayerName = reader.ReadString(),
                        SelectedAirfield = reader.ReadString(),
                        SelectedAircraft = reader.ReadString(),
                        IsReady = reader.ReadBoolean(),
                        IsLoaded = reader.ReadBoolean(),
                        IsHost = reader.ReadBoolean()
                    };
                }
                
                return packet;
            }
        }
        
        public static byte[] SerializeLobbyPlayerJoined(LobbyPlayerJoinedPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PeerId);
                writer.Write(packet.PlayerName ?? "Player");
                return ms.ToArray();
            }
        }
        
        public static LobbyPlayerJoinedPacket DeserializeLobbyPlayerJoined(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbyPlayerJoinedPacket
                {
                    PeerId = reader.ReadUInt64(),
                    PlayerName = reader.ReadString()
                };
            }
        }
        
        public static byte[] SerializeLobbyPlayerLeft(LobbyPlayerLeftPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PeerId);
                return ms.ToArray();
            }
        }
        
        public static LobbyPlayerLeftPacket DeserializeLobbyPlayerLeft(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbyPlayerLeftPacket
                {
                    PeerId = reader.ReadUInt64()
                };
            }
        }
        
        public static byte[] SerializeLobbyPlayerReady(LobbyPlayerReadyPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PeerId);
                writer.Write(packet.IsReady);
                return ms.ToArray();
            }
        }
        
        public static LobbyPlayerReadyPacket DeserializeLobbyPlayerReady(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbyPlayerReadyPacket
                {
                    PeerId = reader.ReadUInt64(),
                    IsReady = reader.ReadBoolean()
                };
            }
        }
        
        public static byte[] SerializeLobbyAirfieldSelect(LobbyAirfieldSelectPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PeerId);
                writer.Write(packet.AirfieldName ?? "");
                return ms.ToArray();
            }
        }
        
        public static LobbyAirfieldSelectPacket DeserializeLobbyAirfieldSelect(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbyAirfieldSelectPacket
                {
                    PeerId = reader.ReadUInt64(),
                    AirfieldName = reader.ReadString()
                };
            }
        }
        
        public static byte[] SerializeLobbySpawnSettings(LobbySpawnSettingsPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write((byte)packet.SpawnType);
                writer.Write(packet.MapName ?? "");
                return ms.ToArray();
            }
        }
        
        public static LobbySpawnSettingsPacket DeserializeLobbySpawnSettings(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbySpawnSettingsPacket
                {
                    SpawnType = (LobbySpawnType)reader.ReadByte(),
                    MapName = reader.ReadString()
                };
            }
        }
        
        public static byte[] SerializeLobbyStartGame(LobbyStartGamePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.MapName ?? "");
                writer.Write((byte)packet.SpawnType);
                return ms.ToArray();
            }
        }
        
        public static LobbyStartGamePacket DeserializeLobbyStartGame(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbyStartGamePacket
                {
                    MapName = reader.ReadString(),
                    SpawnType = (LobbySpawnType)reader.ReadByte()
                };
            }
        }
        
        public static byte[] SerializeLobbyLoadingComplete(LobbyLoadingCompletePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PeerId);
                return ms.ToArray();
            }
        }
        
        public static LobbyLoadingCompletePacket DeserializeLobbyLoadingComplete(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbyLoadingCompletePacket
                {
                    PeerId = reader.ReadUInt64()
                };
            }
        }
        
        public static byte[] SerializeLobbySpawnPlayers(LobbySpawnPlayersPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.Timestamp);
                return ms.ToArray();
            }
        }
        
        public static LobbySpawnPlayersPacket DeserializeLobbySpawnPlayers(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbySpawnPlayersPacket
                {
                    Timestamp = reader.ReadSingle()
                };
            }
        }
        
        public static byte[] SerializeLobbyRespawnRequest(LobbyRespawnRequestPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PeerId);
                return ms.ToArray();
            }
        }
        
        public static LobbyRespawnRequestPacket DeserializeLobbyRespawnRequest(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbyRespawnRequestPacket
                {
                    PeerId = reader.ReadUInt64()
                };
            }
        }

        public static byte[] SerializeLobbyWelcome(LobbyWelcomePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.AssignedPeerId);
                writer.Write(packet.HostName ?? "Host");
                return ms.ToArray();
            }
        }

        public static LobbyWelcomePacket DeserializeLobbyWelcome(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbyWelcomePacket
                {
                    AssignedPeerId = reader.ReadUInt64(),
                    HostName = reader.ReadString()
                };
            }
        }
        
        #endregion
    }
}
