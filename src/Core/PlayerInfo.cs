namespace TCAMultiplayer.Core
{
    /// <summary>
    /// Per-player data container. Mutable — owned and mutated only through <see cref="GameSession"/>.
    /// </summary>
    public class PlayerInfo
    {
        public ulong PeerId { get; set; }
        public string PlayerName { get; set; }
        public string SelectedAircraft { get; set; }
        public string SelectedLoadout { get; set; }
        public string SelectedAirfield { get; set; }
        public bool IsReady { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsHost { get; set; }
        public bool IsModsVerified { get; set; }
        public bool IsModSyncing { get; set; }
        public bool IsAlive { get; set; }
        public bool IsAwaitingRespawn { get; set; }
        public uint LifeId { get; set; }
        public MultiplayerTeam Team { get; set; } = MultiplayerTeam.None;

        // Score
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Assists { get; set; }
    }
}
