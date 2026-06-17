namespace TCAMultiplayer.Protocol
{
    // ═══════════════════════════════════════════════════════════════
    //  Lobby packets (60-79)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Lobby state packet — full state broadcast from host.
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
        public MultiplayerGameMode GameMode = MultiplayerGameMode.FreeForAllDogfight;
        public int MaxPlayersTotal = 8;
        public int TeamCount = 2;
        public uint Revision;
        public LobbyPlayerInfo[] Players;
    }

    /// <summary>
    /// Player info within lobby.
    /// </summary>
    public class LobbyPlayerInfo
    {
        public ulong PeerId;
        public string PlayerName;
        public string SelectedAirfield;
        public string SelectedAircraft;
        public string SelectedLoadout;
        public bool IsReady;
        public bool IsLoaded;
        public bool IsHost;
        /// <summary>True after the player's mod manifest has been verified.</summary>
        public bool IsModsVerified;
        /// <summary>True while mod files are being synced from the host.</summary>
        public bool IsModSyncing;
        public MultiplayerTeam Team;

        /// <summary>
        /// True when the sender included mod compatibility fields in this
        /// lobby state. Older peers omit them, so receivers should preserve
        /// their local compatibility state instead of treating omitted fields
        /// as false.
        /// </summary>
        public bool HasModCompatibilityState;
    }

    /// <summary>
    /// Player joined lobby packet.
    /// </summary>
    public struct LobbyPlayerJoinedPacket
    {
        public ulong PeerId;
        public string PlayerName;
    }

    /// <summary>
    /// Player left lobby packet.
    /// </summary>
    public struct LobbyPlayerLeftPacket
    {
        public ulong PeerId;
    }

    /// <summary>
    /// Player ready state packet.
    /// </summary>
    public struct LobbyPlayerReadyPacket
    {
        public ulong PeerId;
        public bool IsReady;
    }

    /// <summary>
    /// Player team selection packet.
    /// </summary>
    public struct LobbyTeamSelectPacket
    {
        public ulong PeerId;
        public MultiplayerTeam Team;
    }

    /// <summary>
    /// Airfield selection packet.
    /// </summary>
    public struct LobbyAirfieldSelectPacket
    {
        public ulong PeerId;
        public string AirfieldName;
    }

    /// <summary>
    /// Aircraft selection packet.
    /// </summary>
    public struct LobbyAircraftSelectPacket
    {
        public ulong PeerId;
        public string AircraftName;
    }

    /// <summary>
    /// Loadout selection packet.
    /// </summary>
    public struct LobbyLoadoutSelectPacket
    {
        public ulong PeerId;
        public string LoadoutName;
    }

    /// <summary>
    /// Spawn settings packet (host only).
    /// </summary>
    public struct LobbySpawnSettingsPacket
    {
        public LobbySpawnType SpawnType;
        public string MapName;
    }

    /// <summary>
    /// Start game packet (host only).
    /// </summary>
    public struct LobbyStartGamePacket
    {
        public string MapName;
        public LobbySpawnType SpawnType;
        public TimeOfDay TimeOfDay;
    }

    /// <summary>
    /// Loading complete packet.
    /// </summary>
    public struct LobbyLoadingCompletePacket
    {
        public ulong PeerId;
    }

    /// <summary>
    /// Spawn players packet (host sends when all loaded).
    /// </summary>
    public struct LobbySpawnPlayersPacket
    {
        public float Timestamp;
    }

    /// <summary>
    /// Respawn request packet.
    /// </summary>
    public struct LobbyRespawnRequestPacket
    {
        public ulong PeerId;
    }

    /// <summary>
    /// Welcome packet — host assigns PeerID to joining client.
    /// </summary>
    public struct LobbyWelcomePacket
    {
        public ulong AssignedPeerId;
        public string HostName;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Mod compatibility packets (75-76)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Mod manifest packet — sent by client during handshake.
    /// </summary>
    public struct ModManifestPacket
    {
        public ulong PeerId;
        public byte[] ManifestData;
        public string ModVersion;
    }

    /// <summary>
    /// Mod compatibility result packet — sent by host after checking.
    /// </summary>
    public struct ModCompatibilityResultPacket
    {
        public ulong PeerId;
        public bool IsCompatible;
        public string RejectionReason;
        public string HostModVersion;
        public byte[] HostManifestData;
    }

    /// <summary>
    /// Mod sync request — sent by client after user accepts overwriting local mods.
    /// </summary>
    public struct ModSyncRequestPacket
    {
        public ulong PeerId;
        public string HostManifestHash;
    }

    /// <summary>
    /// Chunk of a host mod sync package. Package data is reassembled by TransferId.
    /// </summary>
    public struct ModSyncChunkPacket
    {
        public ulong PeerId;
        public uint TransferId;
        public int ChunkIndex;
        public int ChunkCount;
        public int TotalBytes;
        public byte[] ChunkData;
    }

    // ═══════════════════════════════════════════════════════════════
    //  State sync (10)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Aircraft state for network synchronization.
    /// High-frequency, most critical packet.
    /// </summary>
    public struct AircraftStatePacket
    {
        public ulong PlayerId;

        /// <summary>Sequence number for ordering (drop out-of-order packets).</summary>
        public uint SequenceNumber;

        /// <summary>Aircraft type for initial spawn (may be empty).</summary>
        public string AircraftType;

        // Position (world space — doubles for precision with FloatingOrigin)
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

        // Physics and Aerodynamics
        public float SpeedKIAS;    // Indicated Airspeed in Knots
        public float BrakeState;   // 0-1 Airbrake/Wheel brake

        /// <summary>
        /// Packed boolean flags:
        /// bit 0 = Afterburner, bit 1 = GearDown, bit 2 = FlapsDown,
        /// bit 3 = IsFiring, bit 4 = IsFlareFiring, bit 5 = IsChaffFiring,
        /// bit 6 = IsNavMode, bit 7 = IsWeightOnWheels.
        /// </summary>
        public byte Flags;

        /// <summary>Timestamp for interpolation.</summary>
        public float Timestamp;

        // ── Flag helpers ─────────────────────────────────────────

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

        /// <summary>NavMode (gun safety) — when true, gun should not fire.</summary>
        public bool IsNavMode
        {
            get => (Flags & 0x40) != 0;
            set => Flags = (byte)(value ? Flags | 0x40 : Flags & ~0x40);
        }

        /// <summary>Weight on wheels — when true, plane is on ground.</summary>
        public bool IsWeightOnWheels
        {
            get => (Flags & 0x80) != 0;
            set => Flags = (byte)(value ? Flags | 0x80 : Flags & ~0x80);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Event packets (30-39)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Weapon fire event packet.
    /// </summary>
    public struct WeaponFirePacket
    {
        public ulong PlayerId;
        public byte WeaponType;     // 0 = Gun, 1 = Missile
        public byte WeaponIndex;    // Which hardpoint
        public ulong TargetId;      // For missiles with lock
    }

    /// <summary>
    /// Missile launch packet — sent when a missile is fired at target.
    /// Uses absolute (double-precision) coordinates for floating-origin sync.
    /// </summary>
    public struct MissileLaunchPacket
    {
        public ulong ShooterId;
        public ulong TargetId;
        public bool IsTracking;
        public string MissileType;  // e.g. "AIM-9L", "AIM-120"
        public byte SeekerType;     // 0 = IR, 1 = Radar, 2 = Unguided
        public double LaunchPosX;
        public double LaunchPosY;
        public double LaunchPosZ;
        public float LaunchDirX;
        public float LaunchDirY;
        public float LaunchDirZ;
        public int MissileInstanceId;
    }

    /// <summary>
    /// Mid-flight missile update packet.
    /// </summary>
    public struct MissileUpdatePacket
    {
        public ulong ShooterId;
        public int MissileInstanceId;
        public ulong TargetId;
        public bool IsTracking;
    }

    /// <summary>
    /// Continuous missile position/velocity sync packet.
    /// Sent by the attacker at ~10Hz so the receiver can correct trajectory divergence.
    /// Uses absolute coordinates for floating-origin safety.
    /// </summary>
    public struct MissilePositionSyncPacket
    {
        public ulong ShooterId;
        public int MissileInstanceId;
        public double PosX;
        public double PosY;
        public double PosZ;
        public float VelX;
        public float VelY;
        public float VelZ;
        public bool IsActive;       // False = missile expired/exploded
        public byte RemovalReason;  // 0=unknown/legacy, 1=exploded, 2=native self-destruct
        public bool MotorActive;    // True while any native motor stage is still burning
    }

    /// <summary>
    /// Radar lock packet — sent when locking/unlocking target (for RWR).
    /// </summary>
    public struct RadarLockPacket
    {
        public ulong LockerId;
        public ulong TargetId;
        public bool IsLocked;       // True = locked, False = lost lock
        public byte LockType;       // 0 = radar, 1 = IR
    }

    /// <summary>
    /// Bomb drop packet — sent when an unguided munition is released.
    /// Uses absolute (double-precision) coordinates for floating-origin sync.
    /// </summary>
    public struct BombDropPacket
    {
        public ulong ShooterId;
        public string BombType;     // Display name from StoreData (e.g. "Mk-82", "GBU-12")
        public int BombInstanceId;
        public double LaunchPosX;
        public double LaunchPosY;
        public double LaunchPosZ;
        public float VelX;
        public float VelY;
        public float VelZ;
        public float Timestamp;
        public bool IsActive;       // False = owner reports native destroy/explosion
        public byte RemovalReason;  // 0=unknown/legacy, 1=exploded/destroyed, 2=native self-destruct
    }

    // ═══════════════════════════════════════════════════════════════
    //  World destruction sync (37-38)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Crater spawn packet — syncs crater creation between clients.
    /// </summary>
    public struct CraterSpawnPacket
    {
        public double PosX;
        public double PosY;
        public double PosZ;
        public byte CraterSize;     // 0=Small, 1=Medium, 2=Large, 3=Huge, 4=Aircraft
    }

    /// <summary>
    /// Building destroy packet — syncs building destruction between clients.
    /// </summary>
    public struct BuildingDestroyPacket
    {
        public int BuildingInstanceId;
        public double PosX;
        public double PosY;
        public double PosZ;
        public string ObjectName;
        public byte TargetType;     // Falcon.Targeting.TargetType byte; 255 = unknown
        public byte ObjectKind;     // 0=unknown, 1=Building, 2=Vehicle, 3=Damageable
    }

    // ═══════════════════════════════════════════════════════════════
    //  Damage packets (40-43)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Damage event packet — sent when attacker hits victim.
    /// Uses absolute (double-precision) coordinates for floating-origin sync.
    /// </summary>
    public struct DamagePacket
    {
        public ulong VictimId;
        public ulong AttackerId;
        public uint AttackerLifeId;
        public uint DamageSequence;
        public int Damage;
        public int Penetration;
        public byte DamageType;     // 0 = bullet, 1 = missile, 2 = explosion
        public double HitPosX;
        public double HitPosY;
        public double HitPosZ;
        public string WeaponName;
        public string HitPartName;  // damageable part hit on the shooter's clone
                                    // (backward-compat trailing field; may be empty)
    }

    /// <summary>
    /// Part destruction packet — broadcast by the victim when one of their
    /// damageable parts (wing, stabilizer, engine cowl, ...) breaks off, so
    /// every peer can shear the same part off their clone.
    /// </summary>
    public struct PartDestroyedPacket
    {
        public ulong VictimId;
        public string PartName;
    }

    /// <summary>
    /// Kill confirmation packet — sent by the destroyed player to confirm who killed them.
    /// Both sides use this to update their scoreboard.
    /// </summary>
    public struct KillConfirmPacket
    {
        public ulong KillerId;
        public ulong VictimId;
        public uint DeathSequence;
        public string WeaponName;
    }

    /// <summary>
    /// Death confirmation packet for uncredited deaths (terrain, water, self).
    /// Both sides use this to keep death counters and remote despawn state in sync.
    /// </summary>
    public struct AircraftDestroyedPacket
    {
        public ulong VictimId;
        public uint DeathSequence;
        public string Reason;
    }

    /// <summary>
    /// Native victim-authoritative death report. Sent by the aircraft owner to the host.
    /// The host validates/dedupes it and rebroadcasts a ScoreEvent.
    /// </summary>
    public struct DeathReportPacket
    {
        public ulong VictimId;
        public ulong KillerId;
        public uint LifeId;
        public string WeaponName;
        public string Reason;
    }

    /// <summary>
    /// Host-authoritative score event. Clients update scoreboards only from this packet.
    /// </summary>
    public struct ScoreEventPacket
    {
        public ulong VictimId;
        public ulong KillerId;
        public uint LifeId;
        public string WeaponName;
        public string Reason;
    }

    /// <summary>
    /// Aircraft collision packet — sent by host when two aircraft collide.
    /// Uses host-authority model for collision detection and damage calculation.
    /// </summary>
    public struct AircraftCollisionPacket
    {
        public ulong PlayerA;
        public ulong PlayerB;

        // Collision position (absolute coordinates)
        public double PosX;
        public double PosY;
        public double PosZ;

        // Collision normal (direction from A to B)
        public float NormalX;
        public float NormalY;
        public float NormalZ;

        public int DamageA;
        public int DamageB;
        public float RelativeSpeed;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Game state packets (50-56)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Aircraft changed packet — sent when respawning or changing aircraft.
    /// </summary>
    public struct AircraftChangedPacket
    {
        public ulong PlayerId;
        public string AircraftType; // e.g. "F-16", "AV-8B"
        public bool IsAlive;
        public string LoadoutName;  // backward-compat trailing field (may be empty)
    }

    // ═══════════════════════════════════════════════════════════════
    //  Explosion / VFX sync (78-79)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Explosion sync packet — sent when a munition explodes locally
    /// so the remote player sees the explosion VFX and blast effects.
    /// </summary>
    public struct ExplosionSyncPacket
    {
        public ulong ShooterId;
        public double PosX;
        public double PosY;
        public double PosZ;
        public float BlastRadius;
        public int ImpactDamage;
        public string WeaponName;
        public string EffectPath;       // Asset path for explosion effect prefab
        public byte ExplosionType;      // 0 = standard, 1 = large/bomb, 2 = nuke/massive
        public byte ImpactSurface;      // 0 = air, 1 = ground/terrain, 2 = water
    }

    /// <summary>
    /// Aircraft destruction VFX packet — sent when a player's aircraft is destroyed
    /// so the remote player sees the explosion/impact visual effect.
    /// Sent by the victim; received by the shooter who has the remote clone.
    /// </summary>
    public struct AircraftDestructionVfxPacket
    {
        /// <summary>
        /// DestructionReason value meaning the pilots ejected. The native
        /// UniAircraft.DestructionReason enum tops out at Water = 3; receivers
        /// that predate this value clamp it back into the native range.
        /// </summary>
        public const byte ReasonPilotsEjected = 4;

        /// <summary>
        /// DestructionReason value meaning the victim's aircraft entered the
        /// critical burn phase but has not exploded yet (native HP not at 0):
        /// clones should burn and fall, not detonate. Reasons 0-3 mean the
        /// aircraft fully exploded with that native reason.
        /// </summary>
        public const byte ReasonCriticalBurning = 5;

        public ulong VictimId;
        public double PosX;
        public double PosY;
        public double PosZ;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
        public byte DestructionReason;  // 0 = Air, 1 = GroundSoft, 2 = GroundHard, 3 = Water, 4 = PilotsEjected
        public float VelX;              // world-space velocity at death so the wreck/husk
        public float VelY;              // keeps moving on remote screens
        public float VelZ;              // (backward-compatible trailing fields)
    }
}
