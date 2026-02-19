using System;
using System.IO;
using Falcon.World;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Packet types for multiplayer communication
    /// </summary>
    public enum PacketType : byte
    {
        // Legacy connection packet types (superseded by Lobby packets 60-70)
        // Kept for backward compatibility but not used in current protocol
        [Obsolete("Use LobbyPlayerJoined (61) instead")]
        PlayerJoin = 1,
        [Obsolete("Use LobbyPlayerLeft (62) instead")]
        PlayerLeave = 2,
        [Obsolete("Use LobbyPlayerReady (63) instead")]
        PlayerReady = 3,
        [Obsolete("Use LobbyStartGame (66) instead")]
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
        CountermeasureDeploy = 35, // Flares/chaff deployed
        BombDrop = 36,          // Bomb/unguided munition released
        
        // World destruction sync (reliable)
        CraterSpawn = 37,       // Crater spawned at location
        BuildingDestroy = 38,   // Building destroyed
        MissileUpdate = 39,     // Mid-flight missile state update
        
        // Damage (reliable)
        DamageDealt = 40,
        AircraftDestroyed = 41,
        ProjectileImpact = 42,  // Sent when projectile impacts for FX sync
        AircraftCollision = 43, // Aircraft-to-aircraft collision (host authority)
        
        // Game State
        RequestRespawn = 50,
        Respawned = 51,
        AircraftChanged = 52,   // Sent when local player changes aircraft (respawn, new plane)
        Victory = 53,
        KillConfirm = 54,       // Sent by victim to confirm a kill (for scoreboard sync)
        LoadoutSelect = 55,     // Player selected loadout
        AircraftSelect = 56,    // Player selected aircraft (lobby only)
        
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
        LobbyWelcome = 70,         // Host welcomes client with assigned PeerID
        
        // Mod Compatibility (sent during handshake)
        ModManifest = 75,          // Client sends mod manifest to host
        ModCompatibilityResult = 76, // Host responds with compatibility check result
        
        // Explosion sync (reliable)
        ExplosionSync = 78,         // Explosion VFX/effect sync between players

        // Aircraft destruction VFX (reliable)
        AircraftDestructionVfx = 79 // Aircraft explosion VFX sync between players
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
        public bool AircraftCollisionsEnabled = true;
        public TimeOfDay TimeOfDay = TimeOfDay.Morning;
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
        public string SelectedLoadout = "Clean";  // Default loadout
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
    /// Loadout selection packet
    /// </summary>
    public struct LobbyLoadoutSelectPacket
    {
        public ulong PeerId;
        public string LoadoutName;
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
    /// Mod manifest packet - sent by client during handshake
    /// </summary>
    public struct ModManifestPacket
    {
        public ulong PeerId;
        public byte[] ManifestData; // Serialized ModManifest
    }

    /// <summary>
    /// Mod compatibility result packet - sent by host after checking
    /// </summary>
    public struct ModCompatibilityResultPacket
    {
        public ulong PeerId;
        public bool IsCompatible;
        public string RejectionReason; // If not compatible, explains why
    }

    /// <summary>
    /// Countermeasure deployment packet (flares/chaff)
    /// </summary>
    public struct CountermeasurePacket
    {
        public ulong PlayerId;
        public byte Type;       // 0 = flares, 1 = chaff, 2 = both
    }

    /// <summary>
    /// Aircraft state for network synchronization
    /// </summary>
    public struct AircraftStatePacket
    {
        public ulong PlayerId;

        // Sequence number for ordering (to drop out-of-order packets)
        public uint SequenceNumber;

        // Aircraft type for initial spawn (optional, may be empty)
        public string AircraftType;
        
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
        
        public bool IsFlareFiring
        {
            get => (Flags & 0x10) != 0;
            set => Flags = (byte)(value ? Flags | 0x10 : Flags & ~0x10);
        }
        
        public bool IsChaffFiring
        {
            get => (Flags & 0x20) != 0;
            set => Flags = (byte)(value ? Flags | 0x20 : Flags & ~0x20);
        }
        
        /// <summary>
        /// NavMode (gun safety) - when true, gun should not fire
        /// </summary>
        public bool IsNavMode
        {
            get => (Flags & 0x40) != 0;
            set => Flags = (byte)(value ? Flags | 0x40 : Flags & ~0x40);
        }
        
        /// <summary>
        /// Weight on wheels - when true, plane is on ground and gun should not fire
        /// </summary>
        public bool IsWeightOnWheels
        {
            get => (Flags & 0x80) != 0;
            set => Flags = (byte)(value ? Flags | 0x80 : Flags & ~0x80);
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
    /// Uses absolute (double-precision) coordinates for floating-origin sync
    /// </summary>
    public struct MissileLaunchPacket
    {
        public ulong ShooterId;     // Player who launched
        public ulong TargetId;      // Target player (if locked)
        public bool IsTracking;     // Whether missile is actively tracking target
        public string MissileType;  // e.g. "AIM-9L", "AIM-120"
        public byte SeekerType;     // 0 = IR, 1 = Radar, 2 = Unguided
        public double LaunchPosX;   // Launch position (ABSOLUTE coordinates)
        public double LaunchPosY;
        public double LaunchPosZ;
        public float LaunchDirX;    // Launch direction
        public float LaunchDirY;
        public float LaunchDirZ;
        public int MissileInstanceId; // Instance ID of the missile to track it mid-flight
    }

    /// <summary>
    /// Mid-flight missile update packet
    /// </summary>
    public struct MissileUpdatePacket
    {
        public ulong ShooterId;
        public int MissileInstanceId;
        public ulong TargetId;
        public bool IsTracking;
    }

    /// <summary>
    /// Bomb drop packet - sent when an unguided munition is released
    /// Uses absolute (double-precision) coordinates for floating-origin sync
    /// </summary>
    public struct BombDropPacket
    {
        public ulong ShooterId;     // Player who dropped the bomb
        public string BombType;     // Display name from StoreData (e.g. "Mk-82", "GBU-12")
        public double LaunchPosX;   // Launch position (ABSOLUTE coordinates)
        public double LaunchPosY;
        public double LaunchPosZ;
        public float VelX;          // Initial velocity (includes aircraft velocity)
        public float VelY;
        public float VelZ;
        public float Timestamp;     // Time of release for sync
    }

    /// <summary>
    /// Crater spawn packet - syncs crater creation between clients
    /// </summary>
    public struct CraterSpawnPacket
    {
        public double PosX;         // Absolute world position
        public double PosY;
        public double PosZ;
        public byte CraterSize;     // 0=Small, 1=Medium, 2=Large, 3=Huge, 4=Aircraft
    }

    /// <summary>
    /// Building destroy packet - syncs building destruction between clients
    /// </summary>
    public struct BuildingDestroyPacket
    {
        public int BuildingInstanceId;  // Unity InstanceID of the building
        public double PosX;             // Absolute position for verification
        public double PosY;
        public double PosZ;
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
    /// Kill confirmation packet - sent by the destroyed player to confirm who killed them.
    /// Both sides use this to update their scoreboard.
    /// </summary>
    public struct KillConfirmPacket
    {
        public ulong KillerId;      // Player who got the kill
        public ulong VictimId;      // Player who was destroyed
        public string WeaponName;   // Weapon that dealt the final blow
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
    /// Aircraft collision packet - sent by host when two aircraft collide.
    /// Uses host-authority model for collision detection and damage calculation.
    /// </summary>
    public struct AircraftCollisionPacket
    {
        public ulong PlayerA;           // First player ID
        public ulong PlayerB;           // Second player ID
        
        // Collision position (absolute coordinates for floating-origin sync)
        public double PosX;
        public double PosY;
        public double PosZ;
        
        // Collision normal (direction from A to B)
        public float NormalX;
        public float NormalY;
        public float NormalZ;
        
        public int DamageA;             // Damage to player A
        public int DamageB;             // Damage to player B
        public float RelativeSpeed;     // Relative speed at impact (for effects)
    }

    /// <summary>
    /// Explosion sync packet - sent when a munition explodes locally
    /// so the remote player sees the explosion VFX and blast effects
    /// </summary>
    public struct ExplosionSyncPacket
    {
        public ulong ShooterId;         // Player whose munition exploded
        public double PosX;             // Explosion position (absolute coordinates)
        public double PosY;
        public double PosZ;
        public float BlastRadius;       // Explosion blast radius (meters)
        public int ImpactDamage;        // Explosion impact damage value
        public string WeaponName;       // Weapon/store name (e.g. "Mk-82", "B61-4")
        public string EffectPath;       // Asset path for explosion effect prefab (for modded effects)
        public byte ExplosionType;      // 0 = standard, 1 = large/bomb, 2 = nuke/massive
        public byte ImpactSurface;      // 0 = air (mid-air), 1 = ground/terrain, 2 = water
    }

    /// <summary>
    /// Aircraft destruction VFX packet - sent when a player's aircraft is destroyed
    /// so the remote player sees the explosion/impact visual effect.
    /// Sent by the victim; received by the shooter who has the remote clone.
    /// </summary>
    public struct AircraftDestructionVfxPacket
    {
        public ulong VictimId;          // Player whose aircraft was destroyed
        public double PosX;             // Destruction position (absolute coordinates)
        public double PosY;
        public double PosZ;
        public float RotX;              // Aircraft rotation at time of destruction
        public float RotY;
        public float RotZ;
        public float RotW;
        public byte DestructionReason;  // 0 = Air (mid-air), 1 = GroundSoft, 2 = GroundHard, 3 = Water
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
                writer.Write(state.SequenceNumber);

                writer.Write(state.AircraftType ?? string.Empty);
                
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
                    SequenceNumber = reader.ReadUInt32(),

                    AircraftType = reader.ReadString(),
                    
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

        public static byte[] SerializeCountermeasure(CountermeasurePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PlayerId);
                writer.Write(packet.Type);
                return ms.ToArray();
            }
        }

        public static CountermeasurePacket DeserializeCountermeasure(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new CountermeasurePacket
                {
                    PlayerId = reader.ReadUInt64(),
                    Type = reader.ReadByte()
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
                writer.Write(packet.IsTracking);
                writer.Write(packet.MissileType ?? "Unknown");
                writer.Write(packet.SeekerType);
                writer.Write(packet.LaunchPosX);
                writer.Write(packet.LaunchPosY);
                writer.Write(packet.LaunchPosZ);
                writer.Write(packet.LaunchDirX);
                writer.Write(packet.LaunchDirY);
                writer.Write(packet.LaunchDirZ);
                writer.Write(packet.MissileInstanceId);
                return ms.ToArray();
            }
        }

        public static MissileLaunchPacket DeserializeMissileLaunch(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                var packet = new MissileLaunchPacket
                {
                    ShooterId = reader.ReadUInt64(),
                    TargetId = reader.ReadUInt64(),
                    IsTracking = reader.ReadBoolean(),
                    MissileType = reader.ReadString(),
                    SeekerType = reader.ReadByte(),
                    LaunchPosX = reader.ReadDouble(),
                    LaunchPosY = reader.ReadDouble(),
                    LaunchPosZ = reader.ReadDouble(),
                    LaunchDirX = reader.ReadSingle(),
                    LaunchDirY = reader.ReadSingle(),
                    LaunchDirZ = reader.ReadSingle()
                };
                
                // Backward compatibility for missing InstanceId
                if (ms.Position < ms.Length)
                {
                    packet.MissileInstanceId = reader.ReadInt32();
                }
                
                return packet;
            }
        }

        public static byte[] SerializeMissileUpdate(MissileUpdatePacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.ShooterId);
                writer.Write(packet.MissileInstanceId);
                writer.Write(packet.TargetId);
                writer.Write(packet.IsTracking);
                return ms.ToArray();
            }
        }

        public static MissileUpdatePacket DeserializeMissileUpdate(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new MissileUpdatePacket
                {
                    ShooterId = reader.ReadUInt64(),
                    MissileInstanceId = reader.ReadInt32(),
                    TargetId = reader.ReadUInt64(),
                    IsTracking = reader.ReadBoolean()
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

        public static byte[] SerializeBombDrop(BombDropPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.ShooterId);
                writer.Write(packet.BombType ?? "Unknown");
                writer.Write(packet.LaunchPosX);
                writer.Write(packet.LaunchPosY);
                writer.Write(packet.LaunchPosZ);
                writer.Write(packet.VelX);
                writer.Write(packet.VelY);
                writer.Write(packet.VelZ);
                writer.Write(packet.Timestamp);
                return ms.ToArray();
            }
        }

        public static BombDropPacket DeserializeBombDrop(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new BombDropPacket
                {
                    ShooterId = reader.ReadUInt64(),
                    BombType = reader.ReadString(),
                    LaunchPosX = reader.ReadDouble(),
                    LaunchPosY = reader.ReadDouble(),
                    LaunchPosZ = reader.ReadDouble(),
                    VelX = reader.ReadSingle(),
                    VelY = reader.ReadSingle(),
                    VelZ = reader.ReadSingle(),
                    Timestamp = reader.ReadSingle()
                };
            }
        }

        public static byte[] SerializeCraterSpawn(CraterSpawnPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PosX);
                writer.Write(packet.PosY);
                writer.Write(packet.PosZ);
                writer.Write(packet.CraterSize);
                return ms.ToArray();
            }
        }

        public static CraterSpawnPacket DeserializeCraterSpawn(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new CraterSpawnPacket
                {
                    PosX = reader.ReadDouble(),
                    PosY = reader.ReadDouble(),
                    PosZ = reader.ReadDouble(),
                    CraterSize = reader.ReadByte()
                };
            }
        }

        public static byte[] SerializeBuildingDestroy(BuildingDestroyPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.BuildingInstanceId);
                writer.Write(packet.PosX);
                writer.Write(packet.PosY);
                writer.Write(packet.PosZ);
                return ms.ToArray();
            }
        }

        public static BuildingDestroyPacket DeserializeBuildingDestroy(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new BuildingDestroyPacket
                {
                    BuildingInstanceId = reader.ReadInt32(),
                    PosX = reader.ReadDouble(),
                    PosY = reader.ReadDouble(),
                    PosZ = reader.ReadDouble()
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

        public static byte[] SerializeKillConfirm(KillConfirmPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.KillerId);
                writer.Write(packet.VictimId);
                writer.Write(packet.WeaponName ?? "Unknown");
                return ms.ToArray();
            }
        }

        public static KillConfirmPacket DeserializeKillConfirm(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new KillConfirmPacket
                {
                    KillerId = reader.ReadUInt64(),
                    VictimId = reader.ReadUInt64(),
                    WeaponName = reader.ReadString()
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

        public static byte[] SerializeAircraftCollision(AircraftCollisionPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PlayerA);
                writer.Write(packet.PlayerB);
                writer.Write(packet.PosX);
                writer.Write(packet.PosY);
                writer.Write(packet.PosZ);
                writer.Write(packet.NormalX);
                writer.Write(packet.NormalY);
                writer.Write(packet.NormalZ);
                writer.Write(packet.DamageA);
                writer.Write(packet.DamageB);
                writer.Write(packet.RelativeSpeed);
                return ms.ToArray();
            }
        }

        public static AircraftCollisionPacket DeserializeAircraftCollision(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new AircraftCollisionPacket
                {
                    PlayerA = reader.ReadUInt64(),
                    PlayerB = reader.ReadUInt64(),
                    PosX = reader.ReadDouble(),
                    PosY = reader.ReadDouble(),
                    PosZ = reader.ReadDouble(),
                    NormalX = reader.ReadSingle(),
                    NormalY = reader.ReadSingle(),
                    NormalZ = reader.ReadSingle(),
                    DamageA = reader.ReadInt32(),
                    DamageB = reader.ReadInt32(),
                    RelativeSpeed = reader.ReadSingle()
                };
            }
        }
        
        public static byte[] SerializeExplosionSync(ExplosionSyncPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.ShooterId);
                writer.Write(packet.PosX);
                writer.Write(packet.PosY);
                writer.Write(packet.PosZ);
                writer.Write(packet.BlastRadius);
                writer.Write(packet.ImpactDamage);
                writer.Write(packet.WeaponName ?? "Unknown");
                writer.Write(packet.EffectPath ?? "");
                writer.Write(packet.ExplosionType);
                writer.Write(packet.ImpactSurface);
                return ms.ToArray();
            }
        }

        public static ExplosionSyncPacket DeserializeExplosionSync(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new ExplosionSyncPacket
                {
                    ShooterId = reader.ReadUInt64(),
                    PosX = reader.ReadDouble(),
                    PosY = reader.ReadDouble(),
                    PosZ = reader.ReadDouble(),
                    BlastRadius = reader.ReadSingle(),
                    ImpactDamage = reader.ReadInt32(),
                    WeaponName = reader.ReadString(),
                    EffectPath = reader.ReadString(),
                    ExplosionType = reader.ReadByte(),
                    ImpactSurface = reader.ReadByte()
                };
            }
        }

        public static byte[] SerializeAircraftDestructionVfx(AircraftDestructionVfxPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.VictimId);
                writer.Write(packet.PosX);
                writer.Write(packet.PosY);
                writer.Write(packet.PosZ);
                writer.Write(packet.RotX);
                writer.Write(packet.RotY);
                writer.Write(packet.RotZ);
                writer.Write(packet.RotW);
                writer.Write(packet.DestructionReason);
                return ms.ToArray();
            }
        }

        public static AircraftDestructionVfxPacket DeserializeAircraftDestructionVfx(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new AircraftDestructionVfxPacket
                {
                    VictimId = reader.ReadUInt64(),
                    PosX = reader.ReadDouble(),
                    PosY = reader.ReadDouble(),
                    PosZ = reader.ReadDouble(),
                    RotX = reader.ReadSingle(),
                    RotY = reader.ReadSingle(),
                    RotZ = reader.ReadSingle(),
                    RotW = reader.ReadSingle(),
                    DestructionReason = reader.ReadByte()
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
                writer.Write(packet.AircraftCollisionsEnabled);
                writer.Write((byte)packet.TimeOfDay);
                
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
                    GameLoading = reader.ReadBoolean(),
                    AircraftCollisionsEnabled = reader.ReadBoolean(),
                    TimeOfDay = (TimeOfDay)reader.ReadByte()
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
        
        public static byte[] SerializeLobbyAircraftSelect(LobbyAircraftSelectPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PeerId);
                writer.Write(packet.AircraftName ?? "");
                return ms.ToArray();
            }
        }
        
        public static LobbyAircraftSelectPacket DeserializeLobbyAircraftSelect(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbyAircraftSelectPacket
                {
                    PeerId = reader.ReadUInt64(),
                    AircraftName = reader.ReadString()
                };
            }
        }
        
        public static byte[] SerializeLobbyLoadoutSelect(LobbyLoadoutSelectPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PeerId);
                writer.Write(packet.LoadoutName ?? "");
                return ms.ToArray();
            }
        }
        
        public static LobbyLoadoutSelectPacket DeserializeLobbyLoadoutSelect(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new LobbyLoadoutSelectPacket
                {
                    PeerId = reader.ReadUInt64(),
                    LoadoutName = reader.ReadString()
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

        public static byte[] SerializeModManifest(ModManifestPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PeerId);
                writer.Write(packet.ManifestData?.Length ?? 0);
                if (packet.ManifestData != null && packet.ManifestData.Length > 0)
                {
                    writer.Write(packet.ManifestData);
                }
                return ms.ToArray();
            }
        }

        public static ModManifestPacket DeserializeModManifest(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                var packet = new ModManifestPacket
                {
                    PeerId = reader.ReadUInt64()
                };
                int length = reader.ReadInt32();
                if (length > 0)
                {
                    packet.ManifestData = reader.ReadBytes(length);
                }
                return packet;
            }
        }

        public static byte[] SerializeModCompatibilityResult(ModCompatibilityResultPacket packet)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(packet.PeerId);
                writer.Write(packet.IsCompatible);
                writer.Write(packet.RejectionReason ?? "");
                return ms.ToArray();
            }
        }

        public static ModCompatibilityResultPacket DeserializeModCompatibilityResult(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return new ModCompatibilityResultPacket
                {
                    PeerId = reader.ReadUInt64(),
                    IsCompatible = reader.ReadBoolean(),
                    RejectionReason = reader.ReadString()
                };
            }
        }
        
        #endregion
    }
}
