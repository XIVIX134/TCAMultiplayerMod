namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Centralized network configuration constants.
    /// Single source of truth for all networking parameters.
    /// </summary>
    public static class NetworkConfig
    {
        // Connection
        public const int DEFAULT_PORT = 7777;
        public const string DEFAULT_PORT_STRING = "7777";

        // State sync
        public const float STATE_SEND_INTERVAL = 0.0078f; // ~128Hz for smooth interpolation
        public const float STATE_SEND_INTERVAL_THROTTLED = 0.0333f; // ~30Hz for low-bandwidth mode

        // Bandwidth throttling (runtime-togglable)
        public static bool IsThrottled { get; set; } = false;

        /// <summary>
        /// Returns the active state send interval based on throttle mode.
        /// </summary>
        public static float CurrentStateSendInterval =>
            IsThrottled ? STATE_SEND_INTERVAL_THROTTLED : STATE_SEND_INTERVAL;

        // Lobby
        public const float LOBBY_BROADCAST_INTERVAL = 1.0f; // Host broadcasts lobby state every 1s

        // Keepalive
        public const float KEEPALIVE_PING_INTERVAL = 2.0f; // Send ping every 2s
        public const float KEEPALIVE_TIMEOUT = 10.0f; // Disconnect after 10s without pong

        // Reliable delivery
        public const float RELIABLE_RETRY_TIMEOUT = 0.2f; // 200ms before retransmit
        public const int RELIABLE_MAX_RETRIES = 5;
        public const int MAX_RECEIVED_SEQUENCES = 1000; // Dedup buffer size

        // Interpolation
        public const int INTERPOLATION_BUFFER_CAPACITY = 120; // ~1 second at 128Hz

        // Remote aircraft
        public const float CLONE_RETRY_INTERVAL = 1.0f;
        public const float RESPAWN_TIMEOUT = 30f;

        // Airfield cache
        public const float AIRFIELD_CACHE_DURATION = 30f;
    }
}
