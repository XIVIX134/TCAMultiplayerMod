namespace TCAMultiplayer.Protocol
{
    /// <summary>
    /// Packet types for multiplayer communication.
    /// Byte values are stable — do not renumber existing entries.
    /// </summary>
    public enum PacketType : byte
    {
        // ── State Sync (high frequency, unreliable) ──────────────────
        AircraftState = 10,

        // ── Deprecated/reserved input sync ───────────────────────────
        // Native-style control sync is carried by AircraftState.FlightInput fields.
        [System.Obsolete("Reserved protocol slot. Flight controls are synced through AircraftStatePacket.")]
        ControlInput = 20,

        // ── Events (on occurrence, reliable) ─────────────────────────
        GunFiring = 30,
        GunStopped = 31,
        MissileLaunch = 32,
        RadarLock = 33,             // Sent when locking onto enemy (for RWR)
        RadarLockLost = 34,         // Sent when lock is broken
        [System.Obsolete("Reserved protocol slot. Flare/chaff state is synced through AircraftStatePacket flags and applied through native CountermeasureLauncher.Update.")]
        CountermeasureDeploy = 35,
        BombDrop = 36,              // Bomb/unguided munition released

        // ── World destruction sync (reliable) ────────────────────────
        CraterSpawn = 37,           // Crater spawned at location
        BuildingDestroy = 38,       // Building destroyed
        MissileUpdate = 39,         // Mid-flight missile state update

        // ── Damage (reliable) ────────────────────────────────────────
        DamageDealt = 40,
        AircraftDestroyed = 41,
        [System.Obsolete("Reserved protocol slot. Gun/projectile impacts are produced by native Gun2/Bullet2 on remote clones.")]
        ProjectileImpact = 42,
        AircraftCollision = 43,     // Aircraft-to-aircraft collision (host authority)

        // ── Game State ───────────────────────────────────────────────
        RequestRespawn = 50,
        Respawned = 51,
        AircraftChanged = 52,       // Sent when local player changes aircraft
        Victory = 53,
        KillConfirm = 54,           // Sent by victim to confirm a kill (scoreboard sync)
        LoadoutSelect = 55,         // Player selected loadout
        AircraftSelect = 56,        // Player selected aircraft (lobby only)
        DeathReport = 57,           // Victim -> host native death report
        ScoreEvent = 58,            // Host -> all authoritative score update

        // ── Lobby System (synchronized multiplayer sessions) ─────────
        LobbyState = 60,            // Host broadcasts full lobby state
        LobbyPlayerJoined = 61,     // New player joined lobby
        LobbyPlayerLeft = 62,       // Player left lobby
        LobbyPlayerReady = 63,      // Player ready toggle
        LobbyAirfieldSelect = 64,   // Player selected airfield
        LobbySpawnSettings = 65,    // Host spawn settings (Air/Runway/Ramp)
        LobbyStartGame = 66,        // Host starts game — begin loading
        LobbyLoadingComplete = 67,  // Player finished loading map
        LobbySpawnPlayers = 68,     // Trigger synchronized spawn
        LobbyRespawnRequest = 69,   // Player requests respawn after death
        LobbyWelcome = 70,          // Host welcomes client with assigned PeerID
        LobbyReturnToLobby = 71,    // Host forces all players back to lobby
        LobbyTeamSelect = 72,       // Player selected multiplayer team

        // ── Mod Compatibility (sent during handshake) ────────────────
        ModManifest = 75,            // Client sends mod manifest to host
        ModCompatibilityResult = 76, // Host responds with compatibility result

        // ── Explosion sync (reliable) ────────────────────────────────
        ExplosionSync = 78,          // Explosion VFX/effect sync

        // ── Aircraft destruction VFX (reliable) ──────────────────────
        AircraftDestructionVfx = 79, // Aircraft explosion VFX sync

        // ── Missile position sync (unreliable, high frequency) ───────
        MissilePositionSync = 80,    // Continuous missile position/velocity correction

        // ── Part damage sync (reliable) ──────────────────────────────
        PartDestroyed = 81,          // Victim broadcasts a damageable part breaking off

        // ── Utility ──────────────────────────────────────────────────
        Ping = 100,
        Pong = 101,
        Chat = 102,
    }

    /// <summary>
    /// Spawn location types for lobby system.
    /// </summary>
    public enum LobbySpawnType : byte
    {
        Air = 0,
        Runway = 1,
        Ramp = 2
    }

    /// <summary>
    /// Time-of-day setting for the mission.
    /// Wire-format mirror of the game's TimeOfDay enum — kept game-agnostic.
    /// </summary>
    public enum TimeOfDay : byte
    {
        Dawn = 0,
        Morning = 1,
        Noon = 2,
        Afternoon = 3,
        Evening = 4,
        Night = 5
    }

    public enum MultiplayerGameMode : byte
    {
        FreeForAllDogfight = 0,
        TeamDogfight = 1
    }

    public enum MultiplayerTeam : byte
    {
        None = 0,
        Team1 = 1,
        Team2 = 2,
        Team3 = 3,
        Team4 = 4
    }
}
