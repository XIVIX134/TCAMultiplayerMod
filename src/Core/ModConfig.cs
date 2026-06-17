using System;
using BepInEx.Configuration;

namespace TCAMultiplayer.Core
{
    /// <summary>Which network transport the mod uses. Selected in the multiplayer menu.</summary>
    public enum TransportType
    {
        /// <summary>Direct UDP via IP:port (DirectUdpTransport).</summary>
        DirectIP,
        /// <summary>Steam P2P via Steam lobbies (SteamP2PTransport).</summary>
        SteamLobby
    }

    /// <summary>
    /// All mod-wide configuration entries.
    /// Call <see cref="Bind"/> once from the plugin entry point, passing its ConfigFile.
    /// Does NOT depend on Plugin.Instance.
    /// </summary>
    public static class ModConfig
    {
        private static ConfigFile _config;

        // ── Transport ───────────────────────────────────────────────────
        public static ConfigEntry<string> TransportMode { get; private set; }

        // ── Player ──────────────────────────────────────────────────────
        public static ConfigEntry<string> Username { get; private set; }
        public static ConfigEntry<string> LastIP { get; private set; }
        public static ConfigEntry<string> LastPort { get; private set; }
        public static ConfigEntry<bool> VpnMode { get; private set; }
        public static ConfigEntry<string> LocalBindAddress { get; private set; }
        public static ConfigEntry<bool> LowBandwidthMode { get; private set; }
        public static ConfigEntry<string> LastAircraft { get; private set; }
        public static ConfigEntry<string> LastLoadout { get; private set; }
        public static ConfigEntry<string> LastAirfield { get; private set; }

        // ── Host ────────────────────────────────────────────────────────
        public static ConfigEntry<string> HostServerName { get; private set; }
        public static ConfigEntry<string> HostPort { get; private set; }
        public static ConfigEntry<int> HostSpawnType { get; private set; }
        public static ConfigEntry<int> HostTimeOfDay { get; private set; }
        public static ConfigEntry<bool> HostAircraftCollisions { get; private set; }
        public static ConfigEntry<int> HostMaxPlayersTotal { get; private set; }
        public static ConfigEntry<int> HostGameMode { get; private set; }
        public static ConfigEntry<int> HostTeamCount { get; private set; }
        public static ConfigEntry<string> HostSteamLobbyType { get; private set; }
        public static ConfigEntry<bool> HostCheckMods { get; private set; }

        // ── Logging verbosity ───────────────────────────────────────────
        public static ConfigEntry<bool> VerboseAll { get; private set; }
        public static ConfigEntry<bool> VerboseNetworking { get; private set; }
        public static ConfigEntry<bool> VerboseTransport { get; private set; }
        public static ConfigEntry<bool> VerbosePackets { get; private set; }
        public static ConfigEntry<bool> VerbosePatches { get; private set; }
        public static ConfigEntry<bool> VerbosePlayer { get; private set; }
        public static ConfigEntry<bool> VerboseDamage { get; private set; }
        public static ConfigEntry<bool> VerboseWeapons { get; private set; }
        public static ConfigEntry<bool> VerboseInterpolation { get; private set; }
        public static ConfigEntry<bool> VerboseReflection { get; private set; }

        // ── Logging rates ───────────────────────────────────────────────
        public static ConfigEntry<float> LogIntervalSeconds { get; private set; }
        public static ConfigEntry<int> PacketLogSampleRate { get; private set; }
        public static ConfigEntry<int> HighFreqLogSampleRate { get; private set; }

        // ── Aircraft synchronization ───────────────────────────────────
        public static ConfigEntry<float> StateSendRateHz { get; private set; }
        public static ConfigEntry<float> RemoteInterpolationDelaySeconds { get; private set; }
        public static ConfigEntry<bool> DisableRemoteSmartScaling { get; private set; }
        public static ConfigEntry<bool> RemoteVelocityAwareSmoothingEnabled { get; private set; }
        public static ConfigEntry<float> RemoteVelocityAwareSmoothingTimeSeconds { get; private set; }
        public static ConfigEntry<bool> RemoteVisualChildSmoothingEnabled { get; private set; }
        public static ConfigEntry<float> RemoteVisualSmoothingMaxOffsetMeters { get; private set; }

        // ── Testing diagnostics ────────────────────────────────────────
        public static ConfigEntry<bool> RemoteMotionDebugEnabled { get; private set; }
        public static ConfigEntry<string> RemoteMotionDebugFlagFile { get; private set; }
        public static ConfigEntry<float> RemoteMotionDebugLogIntervalSeconds { get; private set; }
        public static ConfigEntry<float> RemoteMotionDebugDrawScale { get; private set; }

        // ── Updater ────────────────────────────────────────────────────
        public static ConfigEntry<bool> CheckForUpdatesOnLaunch { get; private set; }
        public static ConfigEntry<string> UpdateApiUrl { get; private set; }

        /// <summary>
        /// Bind all config entries to the given <paramref name="config"/> file.
        /// Call once from Plugin.Awake().
        /// </summary>
        public static void Bind(ConfigFile config)
        {
            _config = config;

            // Transport
            TransportMode = config.Bind("Network", "TransportMode", "DirectIP", "Transport type: DirectIP or SteamLobby");

            // Player
            Username        = config.Bind("Player", "Username", "Player", "Your multiplayer username.");
            LastIP          = config.Bind("Network", "LastIP", "127.0.0.1", "Last connected IP address.");
            LastPort        = config.Bind("Network", "LastPort", "7777", "Last connected port.");
            VpnMode = config.Bind("Network", "VpnMode", true,
                "Deprecated: routing is automatic. Leave enabled unless debugging an adapter issue.");
            LocalBindAddress = config.Bind("Network", "LocalBindAddress", "",
                "Advanced: optional local IPv4 address to bind multiplayer UDP to. Leave blank for automatic route selection.");
            LowBandwidthMode = config.Bind("Network", "LowBandwidthMode", false,
                "Advanced: force reduced aircraft update traffic and longer waits. Normally automatic quality detection handles this.");
            LastAircraft    = config.Bind("Player", "LastAircraft", "AV8B", "Last selected aircraft ID.");
            LastLoadout     = config.Bind("Player", "LastLoadout", "Clean", "Last selected loadout name.");
            LastAirfield    = config.Bind("Player", "LastAirfield", "", "Last selected airfield name.");

            // Host
            HostServerName          = config.Bind("Host", "ServerName", "TCA Server", "Your server's name.");
            HostPort                = config.Bind("Host", "Port", "7777", "Port to host on.");
            HostSpawnType           = config.Bind("Host", "SpawnType", 1, "0=Air, 1=Runway, 2=Ramp");
            HostTimeOfDay           = config.Bind("Host", "TimeOfDay", 1, "0=Dawn, 1=Morning, 2=Noon, 3=Afternoon, 4=Evening, 5=Night");
            HostAircraftCollisions  = config.Bind("Host", "AircraftCollisions", true, "Enable aircraft collisions.");
            HostMaxPlayersTotal     = config.Bind("Host", "MaxPlayersTotal", 8, "Maximum players including host. Recommended: 8 (host + 7 peers).");
            HostGameMode            = config.Bind("Host", "GameMode", 0, "0=Free-for-all dogfight, 1=Team dogfight");
            HostTeamCount           = config.Bind("Host", "TeamCount", 2, "Number of teams for Team Dogfight. Valid range: 2-4.");
            HostSteamLobbyType      = config.Bind("Host", "SteamLobbyType", "Public", "Steam lobby visibility: Public or FriendsOnly");
            HostCheckMods            = config.Bind("Host", "CheckMods", true, "Verify clients have matching mods before they can ready up.");

            // Logging verbosity
            VerboseAll            = config.Bind("Logging", "VerboseAll", true, "Enable verbose logs across the mod.");
            VerboseNetworking     = config.Bind("Logging", "VerboseNetworking", true, "Extra logs for network state handling.");
            VerboseTransport      = config.Bind("Logging", "VerboseTransport", true, "Extra logs for transport send/receive.");
            VerbosePackets        = config.Bind("Logging", "VerbosePackets", true, "Extra logs for packet send/receive details.");
            VerbosePatches        = config.Bind("Logging", "VerbosePatches", true, "Extra logs for Harmony patch flows.");
            VerbosePlayer         = config.Bind("Logging", "VerbosePlayer", true, "Extra logs for remote player visuals.");
            VerboseDamage         = config.Bind("Logging", "VerboseDamage", true, "Extra logs for damage flows.");
            VerboseWeapons        = config.Bind("Logging", "VerboseWeapons", true, "Extra logs for weapon/missile flows.");
            VerboseInterpolation  = config.Bind("Logging", "VerboseInterpolation", true, "Extra logs for interpolation/extrapolation.");
            VerboseReflection     = config.Bind("Logging", "VerboseReflection", true, "Extra logs for reflection scans.");

            // Logging rates
            LogIntervalSeconds    = config.Bind("Logging", "LogIntervalSeconds", 1.0f, "Throttle interval (seconds) for periodic logs.");
            PacketLogSampleRate   = config.Bind("Logging", "PacketLogSampleRate", 60, "Log every Nth packet send/receive.");
            HighFreqLogSampleRate = config.Bind("Logging", "HighFreqLogSampleRate", 30, "Log every Nth high-frequency update.");

            // Aircraft synchronization
            StateSendRateHz = config.Bind("Synchronization", "StateSendRateHz", 60f,
                "Aircraft state packets sent per second. Higher values reduce remote motion jitter at the cost of bandwidth.");
            RemoteInterpolationDelaySeconds = config.Bind("Synchronization", "RemoteInterpolationDelaySeconds", 0.18f,
                "Seconds remote aircraft render behind incoming packets. Increase if remote planes stutter on uneven networks.");
            DisableRemoteSmartScaling = config.Bind("Synchronization", "DisableRemoteSmartScaling", true,
                "Disable native SmartScaling on remote aircraft. Remote clones are network puppets; native scaling can make visuals appear to surge forward/back.");
            RemoteVelocityAwareSmoothingEnabled = config.Bind("Synchronization", "RemoteVelocityAwareSmoothingEnabled", true,
                "Smooth remote presentation by predicting with velocity and damping only the residual correction.");
            RemoteVelocityAwareSmoothingTimeSeconds = config.Bind("Synchronization", "RemoteVelocityAwareSmoothingTimeSeconds", 0.12f,
                "Correction time for velocity-aware remote smoothing. Lower is tighter; higher is smoother but more delayed.");
            RemoteVisualChildSmoothingEnabled = config.Bind("Synchronization", "RemoteVisualChildSmoothingEnabled", false,
                "Apply the smoothed presentation pose to safe visual-only child transforms while keeping the remote root authoritative.");
            RemoteVisualSmoothingMaxOffsetMeters = config.Bind("Synchronization", "RemoteVisualSmoothingMaxOffsetMeters", 2f,
                "Maximum distance visual smoothing may lag from the authoritative remote root before clamping.");

            // Testing diagnostics
            RemoteMotionDebugEnabled = config.Bind("Testing", "RemoteMotionDebugEnabled", false,
                "Draw in-game 3D remote-motion diagnostics and emit detailed motion logs. Testing only.");
            RemoteMotionDebugFlagFile = config.Bind("Testing", "RemoteMotionDebugFlagFile", "TCAMP.remote-motion-debug.flag",
                "Create/delete this file next to the BepInEx config to toggle remote-motion debug drawing while the game is running.");
            RemoteMotionDebugLogIntervalSeconds = config.Bind("Testing", "RemoteMotionDebugLogIntervalSeconds", 0.25f,
                "Seconds between detailed remote-motion debug log lines while debug is enabled.");
            RemoteMotionDebugDrawScale = config.Bind("Testing", "RemoteMotionDebugDrawScale", 0.20f,
                "Scale applied to velocity/debug vectors drawn in world space.");

            // Updater
            CheckForUpdatesOnLaunch = config.Bind("Updater", "CheckForUpdatesOnLaunch", true,
                "Check GitHub for a newer TCAMP release when the game launches.");
            UpdateApiUrl = config.Bind("Updater", "UpdateApiUrl",
                "https://api.github.com/repos/XIVIX134/TCAMultiplayerMod/releases/latest",
                "GitHub latest-release API endpoint used by the in-game updater.");
        }

        /// <summary>Parse the TransportMode config string into a <see cref="TransportType"/> enum.</summary>
        public static TransportType GetTransportType()
        {
            string raw = TransportMode?.Value ?? "DirectIP";
            if (string.Equals(raw, "SteamLobby", StringComparison.OrdinalIgnoreCase))
                return TransportType.SteamLobby;
            return TransportType.DirectIP;
        }

        public static string ConfigDirectory
        {
            get
            {
                try
                {
                    string path = _config?.ConfigFilePath;
                    return string.IsNullOrEmpty(path)
                        ? null
                        : System.IO.Path.GetDirectoryName(path);
                }
                catch
                {
                    return null;
                }
            }
        }

        public static void Save()
        {
            try
            {
                _config?.Save();
            }
            catch
            {
                // Config save failures are non-fatal; selection state still lives in memory.
            }
        }
    }
}
