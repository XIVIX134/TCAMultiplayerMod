using System;
using System.Collections.Generic;

namespace TCAMultiplayer.Core
{
    /// <summary>
    /// THE single source of all mutable session state.
    /// Created when joining/hosting. Disposed when disconnecting.
    /// NO STATIC FIELDS allowed — verified by grep.
    /// </summary>
    public class GameSession : IDisposable
    {
        private readonly object _lock = new object();
        private readonly Dictionary<ulong, PlayerInfo> _players = new Dictionary<ulong, PlayerInfo>();
        private bool _disposed;

        /// <summary>State machine governing the session lifecycle.</summary>
        public GameStateMachine StateMachine { get; }

        /// <summary>Whether the local peer is the host of this session.</summary>
        public bool IsHost { get; }

        /// <summary>Network peer ID assigned to the local player.</summary>
        public ulong LocalPeerId { get; set; }

        /// <summary>Thread-safe read-only snapshot of the current player roster.</summary>
        public IReadOnlyDictionary<ulong, PlayerInfo> Players
        {
            get
            {
                lock (_lock)
                {
                    return new Dictionary<ulong, PlayerInfo>(_players);
                }
            }
        }

        // ── Lobby settings ──────────────────────────────────────────────
        public string MapName { get; set; } = "ActionIsland";
        public string HostName { get; set; } = "Host";
        public LobbySpawnType SpawnType { get; set; } = LobbySpawnType.Runway;
        public TimeOfDaySetting TimeOfDay { get; set; } = TimeOfDaySetting.Morning;
        public bool AircraftCollisionsEnabled { get; set; } = true;
        public MultiplayerGameMode GameMode { get; set; } = MultiplayerGameMode.FreeForAllDogfight;
        public int MaxPlayersTotal { get; set; } = 8;
        public int TeamCount { get; set; } = 2;

        // ── Events ──────────────────────────────────────────────────────
        public event Action<PlayerInfo> OnPlayerJoined;
        public event Action<PlayerInfo> OnPlayerLeft;
        public event Action<GameState, GameState> OnStateChanged;

        // ── Constructor ─────────────────────────────────────────────────

        public GameSession(bool isHost)
        {
            IsHost = isHost;
            StateMachine = new GameStateMachine();
            StateMachine.OnStateChanged += HandleStateChanged;
        }

        // ── Player roster ───────────────────────────────────────────────

        /// <summary>
        /// Add a player to the session. Returns the new <see cref="PlayerInfo"/>,
        /// or the existing one if the peer is already registered.
        /// </summary>
        public PlayerInfo AddPlayer(ulong peerId, string name)
        {
            ThrowIfDisposed();

            lock (_lock)
            {
                if (_players.TryGetValue(peerId, out var existing))
                {
                    existing.PlayerName = name;
                    return existing;
                }

                var info = new PlayerInfo
                {
                    PeerId = peerId,
                    PlayerName = name,
                    IsHost = (peerId == LocalPeerId && IsHost)
                };
                info.IsModsVerified = info.IsHost;

                _players[peerId] = info;
                OnPlayerJoined?.Invoke(info);
                return info;
            }
        }

        /// <summary>Remove a player from the session.</summary>
        public void RemovePlayer(ulong peerId)
        {
            ThrowIfDisposed();

            PlayerInfo removed;
            lock (_lock)
            {
                if (!_players.TryGetValue(peerId, out removed))
                    return;

                _players.Remove(peerId);
            }

            OnPlayerLeft?.Invoke(removed);
        }

        /// <summary>Get a player by peer ID, or null if not found.</summary>
        public PlayerInfo GetPlayer(ulong peerId)
        {
            lock (_lock)
            {
                return _players.TryGetValue(peerId, out var info) ? info : null;
            }
        }

        /// <summary>Get the local player's info, or null if not yet registered.</summary>
        public PlayerInfo GetLocalPlayer()
        {
            return GetPlayer(LocalPeerId);
        }

        public bool IsTeamMode => GameMode == MultiplayerGameMode.TeamDogfight;

        public static int ClampMaxPlayersTotal(int value)
        {
            if (value < 1) return 1;
            if (value > 8) return 8;
            return value;
        }

        public static int ClampTeamCount(int value)
        {
            if (value < 2) return 2;
            if (value > 4) return 4;
            return value;
        }

        public static int ClampTeamCountForPlayers(int value, int playerCount)
        {
            int maxTeams = Math.Min(4, Math.Max(2, playerCount));
            if (value < 2) return 2;
            if (value > maxTeams) return maxTeams;
            return value;
        }

        public static MultiplayerTeam ClampTeam(MultiplayerTeam team, int teamCount)
        {
            if (team == MultiplayerTeam.None)
                return MultiplayerTeam.None;

            int maxTeam = ClampTeamCount(teamCount);
            int numericTeam = (int)team;
            return numericTeam >= 1 && numericTeam <= maxTeam
                ? team
                : MultiplayerTeam.None;
        }

        public bool ArePlayersOnSameTeam(ulong firstPeerId, ulong secondPeerId)
        {
            if (!IsTeamMode || firstPeerId == secondPeerId)
                return false;

            var first = GetPlayer(firstPeerId);
            var second = GetPlayer(secondPeerId);
            return first != null
                && second != null
                && first.Team != MultiplayerTeam.None
                && first.Team == second.Team;
        }

        /// <summary>
        /// Start a new aircraft life for a player after native spawn/respawn.
        /// Mirrors the game's one-active-PlayerAircraft lifecycle.
        /// </summary>
        public uint BeginPlayerLife(ulong peerId)
        {
            ThrowIfDisposed();

            lock (_lock)
            {
                if (!_players.TryGetValue(peerId, out var player))
                    return 0;

                unchecked
                {
                    player.LifeId++;
                    if (player.LifeId == 0)
                        player.LifeId = 1;
                }

                player.IsAlive = true;
                player.IsAwaitingRespawn = false;
                return player.LifeId;
            }
        }

        /// <summary>Mark the active aircraft life dead until a host-approved respawn starts the next one.</summary>
        public uint EndPlayerLife(ulong peerId)
        {
            ThrowIfDisposed();

            lock (_lock)
            {
                if (!_players.TryGetValue(peerId, out var player))
                    return 0;

                if (player.LifeId == 0)
                    player.LifeId = 1;

                player.IsAlive = false;
                player.IsAwaitingRespawn = true;
                return player.LifeId;
            }
        }

        /// <summary>Returns true when incoming gameplay for a peer's life should still be accepted.</summary>
        public bool IsCurrentLiveLife(ulong peerId, uint lifeId)
        {
            lock (_lock)
            {
                if (!_players.TryGetValue(peerId, out var player))
                    return false;

                return player.IsAlive
                    && !player.IsAwaitingRespawn
                    && player.LifeId != 0
                    && player.LifeId == lifeId;
            }
        }

        /// <summary>Current number of players in the session.</summary>
        public int PlayerCount
        {
            get
            {
                lock (_lock)
                {
                    return _players.Count;
                }
            }
        }

        // ── Dispose ─────────────────────────────────────────────────────

        /// <summary>
        /// Clear ALL state and unsubscribe ALL events.
        /// This is THE ONLY cleanup path — no scattered Reset() methods.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Reset state machine (fires final Disconnected event if needed)
            StateMachine.OnStateChanged -= HandleStateChanged;
            StateMachine.Reset();

            // Clear player roster
            lock (_lock)
            {
                _players.Clear();
            }

            // Null out all event subscribers to prevent leaks
            OnPlayerJoined = null;
            OnPlayerLeft = null;
            OnStateChanged = null;
        }

        // ── Private helpers ─────────────────────────────────────────────

        private void HandleStateChanged(GameState oldState, GameState newState)
        {
            OnStateChanged?.Invoke(oldState, newState);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GameSession));
        }
    }
}
