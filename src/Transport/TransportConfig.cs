namespace TCAMultiplayer.Transport
{
    /// <summary>
    /// Configuration for transport layer.
    /// </summary>
    public class TransportConfig
    {
        /// <summary>Default port for direct UDP connections</summary>
        public int Port { get; set; } = 7777;

        /// <summary>Seconds between keepalive pings</summary>
        public float KeepaliveInterval { get; set; } = 2.0f;

        /// <summary>Seconds before a peer is considered timed out</summary>
        public float TimeoutSeconds { get; set; } = 10.0f;

        /// <summary>
        /// Seconds after a timeout during which the connection is kept alive and
        /// actively retried before the peer is finally reported as disconnected.
        /// 0 disables the grace window (timeout disconnects immediately).
        /// </summary>
        public float ReconnectGraceSeconds { get; set; } = 30.0f;

        /// <summary>Maximum retransmit attempts for reliable packets</summary>
        public int MaxRetransmitAttempts { get; set; } = 50;

        /// <summary>Seconds between retransmit attempts</summary>
        public float RetransmitInterval { get; set; } = 0.2f;

        /// <summary>Maximum packet size in bytes</summary>
        public int MaxPacketSize { get; set; } = 1400; // Below MTU to avoid fragmentation

        /// <summary>Maximum number of simultaneous client connections (0 = unlimited)</summary>
        public int MaxConnections { get; set; } = 7;
    }
}
