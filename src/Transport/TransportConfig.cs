namespace TCAMultiplayer.Transport
{
    /// <summary>
    /// Configuration for transport layer.
    /// </summary>
    public class TransportConfig
    {
        /// <summary>Default port for direct UDP connections</summary>
        public int Port { get; set; } = 7777;

        /// <summary>
        /// Optional local IP address to bind the UDP socket to. Leave blank to
        /// let the OS choose. Set this to a VPN adapter IP (for example a
        /// Radmin 26.x address) when another TUN/VPN adapter steals routes.
        /// </summary>
        public string LocalBindAddress { get; set; } = "";

        /// <summary>
        /// Automatically choose the best local route for the remote address.
        /// This keeps Radmin/Hamachi/Tailscale-style direct VPN play out of the
        /// user's hands while still allowing LocalBindAddress as an override.
        /// </summary>
        public bool AutoVpnBind { get; set; } = true;

        /// <summary>
        /// Stable per-client token sent in UDP handshakes. 0 means the
        /// transport will generate a random token at construction time.
        /// </summary>
        public ulong ClientToken { get; set; } = 0;

        /// <summary>
        /// TCAMP mod version required for peers in this transport session. Leave
        /// blank to skip the transport-level version gate.
        /// </summary>
        public string ModVersion { get; set; } = "";

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

        /// <summary>
        /// Seconds between connected-client endpoint refresh handshakes. This
        /// lets hosts recover when VPN/TUN/NAT paths change the client's UDP
        /// source endpoint mid-session. 0 disables proactive refresh.
        /// </summary>
        public float EndpointRefreshInterval { get; set; } = 5.0f;

        /// <summary>Maximum retransmit attempts for reliable packets</summary>
        public int MaxRetransmitAttempts { get; set; } = 120;

        /// <summary>Seconds between retransmit attempts</summary>
        public float RetransmitInterval { get; set; } = 0.25f;

        /// <summary>
        /// Maximum reliable retransmits allowed in one Update. Movement state
        /// is unreliable and should not be crowded out by lobby/mod retries.
        /// 0 disables the cap.
        /// </summary>
        public int MaxReliableRetransmitsPerUpdate { get; set; } = 8;

        /// <summary>Maximum packet size in bytes</summary>
        public int MaxPacketSize { get; set; } = 1400; // Below MTU to avoid fragmentation

        /// <summary>Maximum number of simultaneous client connections (0 = unlimited)</summary>
        public int MaxConnections { get; set; } = 7;
    }
}
