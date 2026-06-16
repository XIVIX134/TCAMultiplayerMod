using System;
using System.Collections.Generic;
using UnityEngine;
using TCAMultiplayer.Core;
using TCAMultiplayer.Protocol;
using WireSpawnType = TCAMultiplayer.Protocol.LobbySpawnType;
using WireTimeOfDay = TCAMultiplayer.Protocol.TimeOfDay;
using WireGameMode = TCAMultiplayer.Protocol.MultiplayerGameMode;
using WireTeam = TCAMultiplayer.Protocol.MultiplayerTeam;

namespace TCAMultiplayer.Game
{
    public class LobbyManager : IDisposable
    {
        private const string Tag = "LOBBY";
        private const float LobbyBroadcastInterval = 1.0f;
        private readonly GameSession _session;
        private readonly ConnectionManager _connection;
        private readonly PacketRouter _router;
        private float _lastBroadcastTime;
        private int _lastStateHash;
        private uint _stateRevision;
        private uint _lastAppliedStateRevision;
        private bool _disposed;
        internal static Func<float> TimeProvider = () => Time.time;

        public event Action OnLobbyStateChanged;
        public event Action<string> OnGameStarting;

        /// <summary>
        /// True while we sit in the lobby but the host's game is already
        /// running (late join). Cleared by the next lobby-state broadcast
        /// after the host returns to the lobby.
        /// </summary>
        public bool HostGameInProgress { get; private set; }
        public event Action OnAllPlayersLoaded;

        public LobbyManager(GameSession session, ConnectionManager connection, PacketRouter router)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _router.Register(PacketType.LobbyState, HandleLobbyStateRaw);
            _router.Register(PacketType.LobbyPlayerJoined, HandlePlayerJoinedRaw);
            _router.Register(PacketType.LobbyPlayerLeft, HandlePlayerLeftRaw);
            _router.Register(PacketType.LobbyPlayerReady, HandlePlayerReadyRaw);
            _router.Register(PacketType.LobbyAirfieldSelect, HandleAirfieldSelectRaw);
            _router.Register(PacketType.LobbySpawnSettings, HandleSpawnSettingsRaw);
            _router.Register(PacketType.LobbyStartGame, HandleStartGameRaw);
            _router.Register(PacketType.LobbyLoadingComplete, HandleLoadingCompleteRaw);
            _router.Register(PacketType.LobbySpawnPlayers, HandleSpawnPlayersRaw);
            _router.Register(PacketType.LobbyRespawnRequest, HandleRespawnRequestRaw);
            _router.Register(PacketType.LobbyWelcome, HandleWelcomeRaw);
            _router.Register(PacketType.LobbyReturnToLobby, HandleReturnToLobbyRaw);
            _router.Register(PacketType.LobbyTeamSelect, HandleTeamSelectRaw);
            _router.Register(PacketType.AircraftSelect, HandleAircraftSelectRaw);
            _router.Register(PacketType.LoadoutSelect, HandleLoadoutSelectRaw);
            _session.OnPlayerLeft += HandleSessionPlayerLeft;
            Log.Info(Tag, "Initialized");
        }

        public void Update()
        {
            if (_disposed || !_session.IsHost) return;
            // Only broadcast lobby state while actually in lobby
            var state = _session.StateMachine.CurrentState;
            if (state != GameState.HostingLobby && state != GameState.ClientLobby) return;
            float now = GetTime();
            if (now - _lastBroadcastTime < LobbyBroadcastInterval) return;
            _lastBroadcastTime = now;
            int hash = ComputeStateHash();
            if (hash == _lastStateHash) return;
            _lastStateHash = hash;
            BroadcastLobbyState();
        }

        public void SetSpawnType(Core.LobbySpawnType spawnType)
        {
            if (!CanHostChangeLobby(nameof(SetSpawnType))) return;
            if (_session.SpawnType == spawnType) return;
            _session.SpawnType = spawnType;
            ClearNonHostReadyForSettingsChange();
            SendSpawnSettings();
            BroadcastLobbyState();
            OnLobbyStateChanged?.Invoke();
            Log.Info(Tag, $"Spawn type: {spawnType}");
        }

        public void SetMap(string mapName)
        {
            if (!CanHostChangeLobby(nameof(SetMap))) return;
            if (string.IsNullOrEmpty(mapName)) return;
            if (string.Equals(_session.MapName, mapName, StringComparison.Ordinal)) return;
            _session.MapName = mapName;
            ClearNonHostReadyForSettingsChange();
            var defaultAirfield = MapHelper.GetDefaultAirfieldName(mapName);
            foreach (var player in _session.Players.Values)
            {
                if (string.IsNullOrEmpty(player.SelectedAirfield)
                    || !MapHelper.IsAirfieldOnMap(mapName, player.SelectedAirfield))
                {
                    player.SelectedAirfield = defaultAirfield;
                    if (player.PeerId == _session.LocalPeerId)
                        PersistAirfield(defaultAirfield);
                }
            }
            SendSpawnSettings();
            BroadcastLobbyState();
            OnLobbyStateChanged?.Invoke();
            Log.Info(Tag, $"Map: {mapName}");
        }

        public void SetTimeOfDay(TimeOfDaySetting time)
        {
            if (!CanHostChangeLobby(nameof(SetTimeOfDay))) return;
            if (_session.TimeOfDay == time) return;
            _session.TimeOfDay = time;
            ClearNonHostReadyForSettingsChange();
            BroadcastLobbyState();
            OnLobbyStateChanged?.Invoke();
        }

        public void SetCollisions(bool enabled)
        {
            if (!CanHostChangeLobby(nameof(SetCollisions))) return;
            if (_session.AircraftCollisionsEnabled == enabled) return;
            _session.AircraftCollisionsEnabled = enabled;
            ClearNonHostReadyForSettingsChange();
            BroadcastLobbyState();
            OnLobbyStateChanged?.Invoke();
        }

        public void SetMaxPlayersTotal(int maxPlayersTotal)
        {
            if (!CanHostChangeLobby(nameof(SetMaxPlayersTotal))) return;
            _session.MaxPlayersTotal = GameSession.ClampMaxPlayersTotal(maxPlayersTotal);
            _connection.Config.MaxConnections = Math.Max(0, _session.MaxPlayersTotal - 1);
            ClampTeamStateForCurrentPlayers();
            BroadcastLobbyState();
            OnLobbyStateChanged?.Invoke();
            Log.Info(Tag, $"Max players: {_session.MaxPlayersTotal}");
        }

        public void SetGameMode(Core.MultiplayerGameMode mode)
        {
            if (!CanHostChangeLobby(nameof(SetGameMode))) return;
            if (_session.GameMode == mode) return;
            _session.GameMode = mode;
            ClearNonHostReadyForSettingsChange();
            if (mode == Core.MultiplayerGameMode.FreeForAllDogfight)
            {
                foreach (var p in _session.Players.Values)
                    p.Team = Core.MultiplayerTeam.None;
            }
            else
            {
                ClampTeamStateForCurrentPlayers();
            }
            BroadcastLobbyState();
            OnLobbyStateChanged?.Invoke();
            Log.Info(Tag, $"Game mode: {_session.GameMode}");
        }

        public void SetTeamCount(int teamCount)
        {
            if (!CanHostChangeLobby(nameof(SetTeamCount))) return;
            int clampedTeamCount = GameSession.ClampTeamCountForPlayers(teamCount, _session.PlayerCount);
            if (_session.TeamCount == clampedTeamCount) return;
            _session.TeamCount = clampedTeamCount;
            ClearNonHostReadyForSettingsChange();
            ClampTeamStateForCurrentPlayers();
            BroadcastLobbyState();
            OnLobbyStateChanged?.Invoke();
            Log.Info(Tag, $"Team count: {_session.TeamCount}");
        }

        public void SetLocalTeam(Core.MultiplayerTeam team)
        {
            SetTeam(_session.LocalPeerId, team);
        }

        public void SetTeam(ulong peerId, Core.MultiplayerTeam team)
        {
            if (!CanChangeLobbySelection(nameof(SetTeam))) return;
            if (!_session.IsHost && peerId != _session.LocalPeerId)
            {
                Log.Warning(Tag, $"Ignored SetTeam for non-local peer {peerId}");
                return;
            }

            var player = _session.GetPlayer(peerId);
            if (player == null) return;
            team = _session.GameMode == Core.MultiplayerGameMode.TeamDogfight
                ? GameSession.ClampTeam(team, _session.TeamCount)
                : Core.MultiplayerTeam.None;
            bool changed = player.Team != team;
            if (changed)
                ClearReadyForSelectionChange(player, notifyIfLocal: true);
            player.Team = team;
            Send(PacketType.LobbyTeamSelect, PacketSerializer.SerializeLobbyTeamSelect(
                new LobbyTeamSelectPacket { PeerId = peerId, Team = ToWire(team) }));
            if (_session.IsHost) BroadcastLobbyState();
            OnLobbyStateChanged?.Invoke();
            Log.Info(Tag, $"Team: peer={peerId} team={team}");
        }

        public void StartGame()
        {
            if (!CanHostChangeLobby(nameof(StartGame))) return;
            foreach (var p in _session.Players.Values)
                if (!p.IsHost && !p.IsModsVerified)
                { Log.Warning(Tag, $"Cannot start: {p.PlayerName} is still verifying mods"); return; }
            foreach (var p in _session.Players.Values)
                if (!p.IsReady && !p.IsHost)
                { Log.Warning(Tag, $"Cannot start: {p.PlayerName} not ready"); return; }
            if (_session.GameMode == Core.MultiplayerGameMode.TeamDogfight)
            {
                foreach (var p in _session.Players.Values)
                    if (p.Team == Core.MultiplayerTeam.None)
                    { Log.Warning(Tag, $"Cannot start: {p.PlayerName} has no team"); return; }
            }
            if (!_session.StateMachine.TryTransition(GameState.Loading))
            { Log.Warning(Tag, "Cannot transition to Loading"); return; }
            Send(PacketType.LobbyStartGame, PacketSerializer.SerializeLobbyStartGame(new LobbyStartGamePacket
            {
                MapName = _session.MapName ?? "ActionIsland",
                SpawnType = ToWire(_session.SpawnType),
                TimeOfDay = ToWire(_session.TimeOfDay)
            }));
            foreach (var p in _session.Players.Values) p.IsLoaded = false;
            OnGameStarting?.Invoke(_session.MapName);
            Log.Info(Tag, $"Game starting: {_session.MapName}");
        }

        public void ReturnToLobby()
        {
            if (!_session.IsHost) return;
            if (!CanReturnToLobby(nameof(ReturnToLobby))) return;
            _connection.BroadcastReliable(PacketSerializer.Serialize(PacketType.LobbyReturnToLobby, null));
            ResetPlayersForLobby();
            if (!IsInLobbyState())
                _session.StateMachine.TryTransition(GameState.ReturningToLobby);
            if (_session.StateMachine.CurrentState != GameState.HostingLobby)
                _session.StateMachine.TryTransition(GameState.HostingLobby);
            BroadcastLobbyState();
            OnLobbyStateChanged?.Invoke();
            Log.Info(Tag, "Returned to lobby");
        }

        public void PublishLobbyState()
        {
            if (_disposed) return;
            if (_session.IsHost && IsInLobbyState())
                BroadcastLobbyState();
            OnLobbyStateChanged?.Invoke();
        }

        public void SetAircraft(string name)
        {
            // Allowed in lobby and during gameplay (the respawn screen lets a
            // dead player pick a different aircraft for their next life)
            if (!CanChangeAircraftSelection(nameof(SetAircraft))) return;
            var local = _session.GetLocalPlayer(); if (local == null) return;
            name = LoadoutHelper.ResolveAvailableAircraft(name);
            bool changed = !string.Equals(local.SelectedAircraft, name, StringComparison.Ordinal);
            local.SelectedAircraft = name;
            PersistAircraft(name);
            string resolvedLoadout = LoadoutHelper.ResolveLoadoutForAircraft(name, local.SelectedLoadout);
            bool loadoutChanged = !string.Equals(local.SelectedLoadout, resolvedLoadout, StringComparison.Ordinal);
            if ((changed || loadoutChanged) && IsInLobbyState())
                ClearReadyForSelectionChange(local, notifyIfLocal: true);
            if (loadoutChanged)
            {
                local.SelectedLoadout = resolvedLoadout;
                PersistLoadout(resolvedLoadout);
            }
            Send(PacketType.AircraftSelect, PacketSerializer.SerializeLobbyAircraftSelect(
                new LobbyAircraftSelectPacket { PeerId = _session.LocalPeerId, AircraftName = name }));
            if (loadoutChanged)
            {
                Send(PacketType.LoadoutSelect, PacketSerializer.SerializeLobbyLoadoutSelect(
                    new LobbyLoadoutSelectPacket { PeerId = _session.LocalPeerId, LoadoutName = resolvedLoadout }));
            }
            OnLobbyStateChanged?.Invoke();
        }

        public void SetAirfield(string name)
        {
            if (!CanChangeLobbySelection(nameof(SetAirfield))) return;
            var local = _session.GetLocalPlayer(); if (local == null) return;
            bool changed = !string.Equals(local.SelectedAirfield, name, StringComparison.Ordinal);
            if (changed)
                ClearReadyForSelectionChange(local, notifyIfLocal: true);
            local.SelectedAirfield = name;
            PersistAirfield(name);
            Send(PacketType.LobbyAirfieldSelect, PacketSerializer.SerializeLobbyAirfieldSelect(
                new LobbyAirfieldSelectPacket { PeerId = _session.LocalPeerId, AirfieldName = name }));
            OnLobbyStateChanged?.Invoke();
        }

        public void SetLoadout(string name)
        {
            if (!CanChangeAircraftSelection(nameof(SetLoadout))) return;
            var local = _session.GetLocalPlayer(); if (local == null) return;
            name = LoadoutHelper.ResolveLoadoutForAircraft(local.SelectedAircraft, name);
            bool changed = !string.Equals(local.SelectedLoadout, name, StringComparison.Ordinal);
            if (changed && IsInLobbyState())
                ClearReadyForSelectionChange(local, notifyIfLocal: true);
            local.SelectedLoadout = name;
            PersistLoadout(name);
            Send(PacketType.LoadoutSelect, PacketSerializer.SerializeLobbyLoadoutSelect(
                new LobbyLoadoutSelectPacket { PeerId = _session.LocalPeerId, LoadoutName = name }));
            OnLobbyStateChanged?.Invoke();
        }

        public void ToggleReady()
        {
            if (!CanChangeLobbySelection(nameof(ToggleReady))) return;
            var local = _session.GetLocalPlayer(); if (local == null) return;
            local.IsReady = !local.IsReady;
            Send(PacketType.LobbyPlayerReady, PacketSerializer.SerializeLobbyPlayerReady(
                new LobbyPlayerReadyPacket { PeerId = _session.LocalPeerId, IsReady = local.IsReady }));
            OnLobbyStateChanged?.Invoke();
            Log.Info(Tag, $"Ready: {local.IsReady}");
        }

        public void SendLoadingComplete()
        {
            if (!IsCurrentState(GameState.Loading))
            {
                Log.Warning(Tag, $"Ignored {nameof(SendLoadingComplete)} while state={_session.StateMachine.CurrentState}");
                return;
            }

            var local = _session.GetLocalPlayer();
            if (local != null) local.IsLoaded = true;
            Send(PacketType.LobbyLoadingComplete, PacketSerializer.SerializeLobbyLoadingComplete(
                new LobbyLoadingCompletePacket { PeerId = _session.LocalPeerId }));
            if (_session.IsHost) CheckAllLoaded();
        }

        public void RequestRespawn()
        {
            if (!IsGameplayState())
            {
                Log.Warning(Tag, $"Ignored {nameof(RequestRespawn)} while state={_session.StateMachine.CurrentState}");
                return;
            }

            Send(PacketType.LobbyRespawnRequest, PacketSerializer.SerializeLobbyRespawnRequest(
                new LobbyRespawnRequestPacket { PeerId = _session.LocalPeerId }));
        }

        public void AnnounceLocalPlayer()
        {
            if (_session.IsHost || !IsCurrentState(GameState.ClientLobby))
                return;

            var local = _session.GetLocalPlayer();
            if (local == null)
                return;

            string playerName = GetConfiguredPlayerName(local.PlayerName);
            local.PlayerName = playerName;
            var joinPkt = new LobbyPlayerJoinedPacket
            {
                PeerId = local.PeerId,
                PlayerName = playerName
            };
            Send(PacketType.LobbyPlayerJoined, PacketSerializer.SerializeLobbyPlayerJoined(joinPkt));
            Send(PacketType.AircraftSelect, PacketSerializer.SerializeLobbyAircraftSelect(
                new LobbyAircraftSelectPacket { PeerId = local.PeerId, AircraftName = local.SelectedAircraft ?? "" }));
            Send(PacketType.LoadoutSelect, PacketSerializer.SerializeLobbyLoadoutSelect(
                new LobbyLoadoutSelectPacket { PeerId = local.PeerId, LoadoutName = local.SelectedLoadout ?? "" }));
            Send(PacketType.LobbyAirfieldSelect, PacketSerializer.SerializeLobbyAirfieldSelect(
                new LobbyAirfieldSelectPacket { PeerId = local.PeerId, AirfieldName = local.SelectedAirfield ?? "" }));

            Log.Info(Tag, $"Announced verified local player {joinPkt.PlayerName} ({local.PeerId})");
        }

        private void HandleLobbyStateRaw(ulong from, byte[] data)
        {
            if (_disposed || _session.IsHost) return;
            if (!IsFromHost(from, "LobbyState")) return;
            if (!IsCurrentState(GameState.ClientLobby))
            {
                Log.Warning(Tag, $"Ignored LobbyState from peer {from} while state={_session.StateMachine.CurrentState}");
                return;
            }

            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null) return;
            var pkt = PacketSerializer.DeserializeLobbyState(payload);
            if (pkt == null) return;
            if (!ShouldApplyLobbyRevision(pkt.Revision))
            {
                Log.Debug(Tag, $"Ignored stale LobbyState revision {pkt.Revision} (current {_lastAppliedStateRevision})");
                return;
            }

            // Late join: the host's game may already be running — surface that
            // to the lobby UI so the waiting client knows why nothing happens.
            HostGameInProgress = pkt.GameStarted || pkt.GameLoading;

            _session.HostName = pkt.HostName;
            _session.MapName = pkt.MapName;
            _session.SpawnType = FromWire(pkt.SpawnType);
            _session.AircraftCollisionsEnabled = pkt.AircraftCollisionsEnabled;
            _session.TimeOfDay = FromWire(pkt.TimeOfDay);
            _session.GameMode = FromWire(pkt.GameMode);
            _session.MaxPlayersTotal = GameSession.ClampMaxPlayersTotal(pkt.MaxPlayersTotal);
            int incomingPlayerCount = pkt.Players?.Length ?? _session.PlayerCount;
            _session.TeamCount = GameSession.ClampTeamCountForPlayers(pkt.TeamCount, incomingPlayerCount);
            var receivedIds = new HashSet<ulong>();
            if (pkt.Players != null)
                foreach (var wp in pkt.Players)
                {
                    receivedIds.Add(wp.PeerId);
                    string playerName = wp.PlayerName;
                    if (wp.PeerId == _session.LocalPeerId)
                    {
                        var existingLocal = _session.GetLocalPlayer();
                        playerName = GetConfiguredPlayerName(existingLocal?.PlayerName ?? wp.PlayerName);
                    }

                    var pi = _session.AddPlayer(wp.PeerId, playerName);
                    bool preserveLocalSelections = wp.PeerId == _session.LocalPeerId
                        && HasLocalSelectionChanged(
                            pi, wp.SelectedAircraft, wp.SelectedAirfield, wp.SelectedLoadout);

                    if (!preserveLocalSelections)
                    {
                        pi.SelectedAircraft = wp.SelectedAircraft;
                        pi.SelectedAirfield = wp.SelectedAirfield;
                        pi.SelectedLoadout = string.IsNullOrEmpty(wp.SelectedLoadout)
                            ? null
                            : wp.SelectedLoadout;
                    }

                    pi.IsReady = wp.IsReady; pi.IsLoaded = wp.IsLoaded; pi.IsHost = wp.IsHost;
                    pi.Team = GameSession.ClampTeam(FromWire(wp.Team), _session.TeamCount);
                    if (wp.IsHost)
                    {
                        pi.IsModsVerified = true;
                        pi.IsModSyncing = false;
                    }
                    else if (wp.HasModCompatibilityState)
                    {
                        pi.IsModsVerified = wp.IsModsVerified;
                        pi.IsModSyncing = wp.IsModSyncing;
                    }
                }
            foreach (var id in _session.Players.Keys)
                if (!receivedIds.Contains(id)) _session.RemovePlayer(id);
            _session.TeamCount = GameSession.ClampTeamCountForPlayers(_session.TeamCount, _session.PlayerCount);
            ClampTeamStateForCurrentPlayers();
            OnLobbyStateChanged?.Invoke();
        }
        private void HandlePlayerJoinedRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            // The host also accepts joins while a game is running: the late
            // joiner is registered in the roster and waits in the lobby until
            // the host returns to it (no mid-game spawn yet).
            bool lateJoinAsHost = _session.IsHost && IsGameplayState();
            if (!lateJoinAsHost && !CanApplyLobbyMutation(from, "LobbyPlayerJoined")) return;
            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbyPlayerJoined(p);
            if (!CanPeerMutatePlayer(from, pkt.PeerId, "LobbyPlayerJoined")) return;
            _session.AddPlayer(pkt.PeerId, pkt.PlayerName);
            ClampTeamStateForCurrentPlayers();
            OnLobbyStateChanged?.Invoke();
            if (_session.IsHost) BroadcastLobbyState();
            Log.Info(Tag, $"Player joined: {pkt.PlayerName} ({pkt.PeerId}){(lateJoinAsHost ? " (waiting in lobby — game in progress)" : "")}");
        }
        private void HandlePlayerLeftRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            if (!CanApplyLobbyMutation(from, "LobbyPlayerLeft")) return;
            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbyPlayerLeft(p);
            if (!CanPeerMutatePlayer(from, pkt.PeerId, "LobbyPlayerLeft")) return;
            if (_session.GetPlayer(pkt.PeerId) == null) return;
            _session.RemovePlayer(pkt.PeerId);
        }
        private void HandlePlayerReadyRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            if (!IsPeerVerifiedForHost(from, "LobbyPlayerReady")) return;
            if (!CanApplyLobbyMutation(from, "LobbyPlayerReady")) return;
            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbyPlayerReady(p);
            if (!CanPeerMutatePlayer(from, pkt.PeerId, "LobbyPlayerReady")) return;
            var pi = _session.GetPlayer(pkt.PeerId);
            if (pi != null) pi.IsReady = pkt.IsReady;
            OnLobbyStateChanged?.Invoke();
            if (_session.IsHost)
            {
                BroadcastLobbyState();
                CheckAllReady();
            }
        }
        private void HandleTeamSelectRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            if (!IsPeerVerifiedForHost(from, "LobbyTeamSelect")) return;
            if (!CanApplyLobbyMutation(from, "LobbyTeamSelect")) return;
            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbyTeamSelect(p);
            if (!CanPeerMutatePlayer(from, pkt.PeerId, "LobbyTeamSelect")) return;
            var pi = _session.GetPlayer(pkt.PeerId);
            if (pi != null)
            {
                pi.Team = _session.GameMode == Core.MultiplayerGameMode.TeamDogfight
                    ? GameSession.ClampTeam(FromWire(pkt.Team), _session.TeamCount)
                    : Core.MultiplayerTeam.None;
            }
            OnLobbyStateChanged?.Invoke();
            if (_session.IsHost) BroadcastLobbyState();
        }
        private void HandleAirfieldSelectRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            if (!IsPeerVerifiedForHost(from, "LobbyAirfieldSelect")) return;
            if (!CanApplyLobbyMutation(from, "LobbyAirfieldSelect")) return;
            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbyAirfieldSelect(p);
            if (!CanPeerMutatePlayer(from, pkt.PeerId, "LobbyAirfieldSelect")) return;
            var pi = _session.GetPlayer(pkt.PeerId);
            if (pi != null) pi.SelectedAirfield = pkt.AirfieldName;
            OnLobbyStateChanged?.Invoke();
            if (_session.IsHost) BroadcastLobbyState();
        }
        private void HandleAircraftSelectRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            if (!IsPeerVerifiedForHost(from, "AircraftSelect")) return;
            if (!CanApplySelectionMutation(from, "AircraftSelect")) return;
            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbyAircraftSelect(p);
            if (!CanPeerMutatePlayer(from, pkt.PeerId, "AircraftSelect")) return;
            var pi = _session.GetPlayer(pkt.PeerId);
            if (pi != null) pi.SelectedAircraft = pkt.AircraftName;
            OnLobbyStateChanged?.Invoke();
            // Mid-game, peers learn the new selection from the Respawned packet
            if (_session.IsHost && IsCurrentState(GameState.HostingLobby)) BroadcastLobbyState();
        }
        private void HandleLoadoutSelectRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            if (!IsPeerVerifiedForHost(from, "LoadoutSelect")) return;
            if (!CanApplySelectionMutation(from, "LoadoutSelect")) return;
            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbyLoadoutSelect(p);
            if (!CanPeerMutatePlayer(from, pkt.PeerId, "LoadoutSelect")) return;
            var pi = _session.GetPlayer(pkt.PeerId);
            if (pi != null) pi.SelectedLoadout = pkt.LoadoutName;
            OnLobbyStateChanged?.Invoke();
            if (_session.IsHost && IsCurrentState(GameState.HostingLobby)) BroadcastLobbyState();
        }
        private void HandleSpawnSettingsRaw(ulong from, byte[] data)
        {
            if (_disposed || _session.IsHost) return;
            if (!IsFromHost(from, "LobbySpawnSettings")) return;
            if (!IsCurrentState(GameState.ClientLobby))
            {
                Log.Warning(Tag, $"Ignored LobbySpawnSettings from peer {from} while state={_session.StateMachine.CurrentState}");
                return;
            }

            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbySpawnSettings(p);
            _session.SpawnType = FromWire(pkt.SpawnType);
            _session.MapName = pkt.MapName;
            EnsureLocalAirfieldForCurrentMap();
            OnLobbyStateChanged?.Invoke();
        }
        private void HandleStartGameRaw(ulong from, byte[] data)
        {
            if (_disposed || _session.IsHost) return;
            if (!IsFromHost(from, "LobbyStartGame")) return;
            if (!IsCurrentState(GameState.ClientLobby))
            {
                Log.Warning(Tag, $"Ignored LobbyStartGame from peer {from} while state={_session.StateMachine.CurrentState}");
                return;
            }

            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbyStartGame(p);
            _session.MapName = pkt.MapName;
            _session.SpawnType = FromWire(pkt.SpawnType);
            _session.TimeOfDay = FromWire(pkt.TimeOfDay);
            EnsureLocalAirfieldForCurrentMap();
            foreach (var pl in _session.Players.Values) pl.IsLoaded = false;
            if (!_session.StateMachine.TryTransition(GameState.Loading))
            {
                Log.Warning(Tag, $"Cannot transition to Loading from {_session.StateMachine.CurrentState}");
                return;
            }
            OnGameStarting?.Invoke(pkt.MapName);
            Log.Info(Tag, $"Game starting: {pkt.MapName}");
        }
        private void HandleLoadingCompleteRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            if (!IsCurrentState(GameState.Loading))
            {
                Log.Warning(Tag, $"Ignored LobbyLoadingComplete from peer {from} while state={_session.StateMachine.CurrentState}");
                return;
            }

            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbyLoadingComplete(p);
            if (!CanPeerMutatePlayer(from, pkt.PeerId, "LobbyLoadingComplete")) return;
            var pi = _session.GetPlayer(pkt.PeerId);
            if (pi != null) pi.IsLoaded = true;
            if (_session.IsHost) CheckAllLoaded();
        }
        private void HandleSpawnPlayersRaw(ulong from, byte[] data)
        {
            if (_disposed) return;
            if (_session.IsHost && from != _session.LocalPeerId)
            {
                Log.Warning(Tag, $"Rejected LobbySpawnPlayers from peer {from}; host controls spawning");
                return;
            }
            if (!_session.IsHost && !IsFromHost(from, "LobbySpawnPlayers")) return;
            if (!IsCurrentState(GameState.Loading))
            {
                Log.Warning(Tag, $"Ignored LobbySpawnPlayers from peer {from} while state={_session.StateMachine.CurrentState}");
                return;
            }
            if (!_session.StateMachine.TryTransition(GameState.Spawning))
            {
                Log.Warning(Tag, $"Cannot transition to Spawning from {_session.StateMachine.CurrentState}");
                return;
            }
            OnAllPlayersLoaded?.Invoke();
        }
        private void HandleRespawnRequestRaw(ulong from, byte[] data)
        {
            if (_disposed || !_session.IsHost) return;
            if (!IsGameplayState())
            {
                Log.Warning(Tag, $"Ignored LobbyRespawnRequest from peer {from} while state={_session.StateMachine.CurrentState}");
                return;
            }

            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbyRespawnRequest(p);
            if (!CanPeerMutatePlayer(from, pkt.PeerId, "LobbyRespawnRequest")) return;
            Log.Info(Tag, $"Respawn request from peer {pkt.PeerId}");
        }
        private void HandleWelcomeRaw(ulong from, byte[] data)
        {
            if (_disposed || _session.IsHost) return;
            if (!IsFromHost(from, "LobbyWelcome")) return;
            if (!IsCurrentState(GameState.ClientLobby))
            {
                Log.Warning(Tag, $"Ignored LobbyWelcome from peer {from} while state={_session.StateMachine.CurrentState}");
                return;
            }

            var (_, p) = PacketSerializer.Deserialize(data); if (p == null) return;
            var pkt = PacketSerializer.DeserializeLobbyWelcome(p);
            _session.LocalPeerId = pkt.AssignedPeerId;
            _session.HostName = pkt.HostName;

            // Add self to local roster with real username
            var username = GetConfiguredPlayerName(_session.GetLocalPlayer()?.PlayerName);
            _session.AddPlayer(pkt.AssignedPeerId, username);
            InitializeLocalSelectionDefaults();

            // Tell the host our real name (otherwise host only knows us as "Peer_N")
            var joinPkt = new LobbyPlayerJoinedPacket
            {
                PeerId = pkt.AssignedPeerId,
                PlayerName = username
            };
            Send(PacketType.LobbyPlayerJoined, PacketSerializer.SerializeLobbyPlayerJoined(joinPkt));

            Log.Info(Tag, $"Welcome: peer ID {pkt.AssignedPeerId}, host: {pkt.HostName}, name: {username}");
            OnLobbyStateChanged?.Invoke();
        }
        private void HandleReturnToLobbyRaw(ulong from, byte[] data)
        {
            if (_disposed || _session.IsHost) return;
            if (!IsFromHost(from, "LobbyReturnToLobby")) return;
            if (!CanReturnToLobby("LobbyReturnToLobby")) return;
            ResetPlayersForLobby();
            if (!IsInLobbyState())
                _session.StateMachine.TryTransition(GameState.ReturningToLobby);
            if (_session.StateMachine.CurrentState != GameState.ClientLobby)
                _session.StateMachine.TryTransition(GameState.ClientLobby);
            OnLobbyStateChanged?.Invoke();
        }
        private void Send(PacketType type, byte[] payload)
        {
            _connection.BroadcastReliable(PacketSerializer.Serialize(type, payload));
        }

        private bool IsCurrentState(GameState state)
        {
            return _session.StateMachine.CurrentState == state;
        }

        private bool IsInLobbyState()
        {
            var state = _session.StateMachine.CurrentState;
            return state == GameState.HostingLobby || state == GameState.ClientLobby;
        }

        private bool IsGameplayState()
        {
            var state = _session.StateMachine.CurrentState;
            return state == GameState.Spawning
                || state == GameState.InGame
                || state == GameState.Respawning;
        }

        private static string GetConfiguredPlayerName(string fallback = null)
        {
            string configuredName = ModConfig.Username?.Value;
            if (!string.IsNullOrWhiteSpace(configuredName))
                return configuredName.Trim();
            if (!string.IsNullOrWhiteSpace(fallback))
                return fallback.Trim();
            return "Player";
        }

        private bool CanHostChangeLobby(string actionName)
        {
            if (!_session.IsHost)
                return false;
            if (IsCurrentState(GameState.HostingLobby))
                return true;

            Log.Warning(Tag, $"Ignored {actionName} while state={_session.StateMachine.CurrentState}");
            return false;
        }

        private bool CanChangeLobbySelection(string actionName)
        {
            if (IsInLobbyState())
                return true;

            Log.Warning(Tag, $"Ignored {actionName} while state={_session.StateMachine.CurrentState}");
            return false;
        }

        /// <summary>
        /// Aircraft/loadout selection is also legal during gameplay — it only
        /// changes what the player's NEXT life spawns with (respawn screen).
        /// </summary>
        private bool CanChangeAircraftSelection(string actionName)
        {
            if (IsInLobbyState() || IsGameplayState())
                return true;

            Log.Warning(Tag, $"Ignored {actionName} while state={_session.StateMachine.CurrentState}");
            return false;
        }

        /// <summary>
        /// Receive-side gate for aircraft/loadout selection packets — accepted
        /// in lobby and during gameplay (selection for the sender's next life).
        /// </summary>
        private bool CanApplySelectionMutation(ulong fromPeerId, string packetName)
        {
            if (_session.IsHost)
            {
                if (IsCurrentState(GameState.HostingLobby) || IsGameplayState())
                    return true;

                Log.Warning(Tag, $"Ignored {packetName} from peer {fromPeerId} while state={_session.StateMachine.CurrentState}");
                return false;
            }

            if (!IsFromHost(fromPeerId, packetName))
                return false;
            if (IsCurrentState(GameState.ClientLobby) || IsGameplayState())
                return true;

            Log.Warning(Tag, $"Ignored {packetName} from host while state={_session.StateMachine.CurrentState}");
            return false;
        }

        private bool CanApplyLobbyMutation(ulong fromPeerId, string packetName)
        {
            if (_session.IsHost)
            {
                if (IsCurrentState(GameState.HostingLobby))
                    return true;

                Log.Warning(Tag, $"Ignored {packetName} from peer {fromPeerId} while state={_session.StateMachine.CurrentState}");
                return false;
            }

            if (!IsFromHost(fromPeerId, packetName))
                return false;
            if (IsCurrentState(GameState.ClientLobby))
                return true;

            Log.Warning(Tag, $"Ignored {packetName} from host while state={_session.StateMachine.CurrentState}");
            return false;
        }

        private bool CanReturnToLobby(string packetName)
        {
            if (IsCurrentState(GameState.ReturningToLobby))
                return true;
            if (IsInLobbyState())
                return true;
            if (IsCurrentState(GameState.Loading) || IsGameplayState())
                return true;

            Log.Warning(Tag, $"Ignored {packetName} while state={_session.StateMachine.CurrentState}");
            return false;
        }

        private void HandleSessionPlayerLeft(PlayerInfo player)
        {
            if (_disposed || player == null)
                return;

            ClampTeamStateForCurrentPlayers();
            OnLobbyStateChanged?.Invoke();

            if (_session.IsHost && IsInLobbyState())
                BroadcastLobbyState();
            if (_session.IsHost && IsCurrentState(GameState.Loading))
                CheckAllLoaded();

            Log.Info(Tag, $"Player left: {player.PlayerName} ({player.PeerId})");
        }

        private bool IsFromHost(ulong fromPeerId, string packetName)
        {
            if (fromPeerId == 1)
                return true;

            Log.Warning(Tag, $"Rejected {packetName} from non-host peer {fromPeerId}");
            return false;
        }

        private bool ShouldApplyLobbyRevision(uint revision)
        {
            if (revision == 0)
                return true;

            if (revision <= _lastAppliedStateRevision)
                return false;

            _lastAppliedStateRevision = revision;
            return true;
        }

        private void ClearReadyForSelectionChange(PlayerInfo player, bool notifyIfLocal)
        {
            if (player == null || !player.IsReady)
                return;

            player.IsReady = false;
            if (notifyIfLocal && player.PeerId == _session.LocalPeerId)
            {
                Send(PacketType.LobbyPlayerReady, PacketSerializer.SerializeLobbyPlayerReady(
                    new LobbyPlayerReadyPacket { PeerId = player.PeerId, IsReady = false }));
            }
        }

        private void ClearNonHostReadyForSettingsChange()
        {
            foreach (var player in _session.Players.Values)
            {
                if (player.IsHost)
                    continue;
                ClearReadyForSelectionChange(player, notifyIfLocal: false);
            }
        }

        private void InitializeLocalSelectionDefaults()
        {
            var local = _session.GetLocalPlayer();
            if (local == null) return;

            bool changed = false;
            if (string.IsNullOrWhiteSpace(local.SelectedAircraft))
            {
                string aircraft = ModConfig.LastAircraft?.Value;
                local.SelectedAircraft = LoadoutHelper.ResolveAvailableAircraft(aircraft);
                changed = true;
            }
            else if (!LoadoutHelper.IsAircraftAvailable(local.SelectedAircraft))
            {
                local.SelectedAircraft = LoadoutHelper.ResolveAvailableAircraft(local.SelectedAircraft);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(local.SelectedLoadout))
            {
                string loadout = ModConfig.LastLoadout?.Value;
                if (string.IsNullOrWhiteSpace(loadout))
                    loadout = LoadoutHelper.GetDefaultLoadoutForAircraft(local.SelectedAircraft);
                local.SelectedLoadout = LoadoutHelper.ResolveLoadoutForAircraft(local.SelectedAircraft, loadout);
                changed = true;
            }
            else
            {
                string loadout = LoadoutHelper.ResolveLoadoutForAircraft(local.SelectedAircraft, local.SelectedLoadout);
                if (!string.Equals(local.SelectedLoadout, loadout, StringComparison.Ordinal))
                {
                    local.SelectedLoadout = loadout;
                    changed = true;
                }
            }

            if (changed)
            {
                PersistAircraft(local.SelectedAircraft);
                PersistLoadout(local.SelectedLoadout);
            }

            string mapName = _session.MapName ?? MapHelper.GetDefaultMapName();
            if (string.IsNullOrWhiteSpace(local.SelectedAirfield)
                || !MapHelper.IsAirfieldOnMap(mapName, local.SelectedAirfield))
            {
                string airfield = ModConfig.LastAirfield?.Value;
                if (string.IsNullOrWhiteSpace(airfield) || !MapHelper.IsAirfieldOnMap(mapName, airfield))
                    airfield = MapHelper.GetDefaultAirfieldName(mapName);
                local.SelectedAirfield = airfield;
                changed = true;
            }

            if (!changed)
                return;

            Send(PacketType.AircraftSelect, PacketSerializer.SerializeLobbyAircraftSelect(
                new LobbyAircraftSelectPacket { PeerId = _session.LocalPeerId, AircraftName = local.SelectedAircraft }));
            Send(PacketType.LoadoutSelect, PacketSerializer.SerializeLobbyLoadoutSelect(
                new LobbyLoadoutSelectPacket { PeerId = _session.LocalPeerId, LoadoutName = local.SelectedLoadout }));
            Send(PacketType.LobbyAirfieldSelect, PacketSerializer.SerializeLobbyAirfieldSelect(
                new LobbyAirfieldSelectPacket { PeerId = _session.LocalPeerId, AirfieldName = local.SelectedAirfield }));
        }

        private bool CanPeerMutatePlayer(ulong fromPeerId, ulong targetPeerId, string packetName)
        {
            if (!_session.IsHost)
                return true;
            if (fromPeerId == _session.LocalPeerId)
                return true;
            if (targetPeerId == fromPeerId)
                return true;

            Log.Warning(Tag, $"Rejected {packetName} from peer {fromPeerId} for peer {targetPeerId}");
            return false;
        }

        private bool IsPeerVerifiedForHost(ulong fromPeerId, string packetName)
        {
            if (!_session.IsHost)
                return true;
            if (fromPeerId == _session.LocalPeerId)
                return true;

            var player = _session.GetPlayer(fromPeerId);
            if (player?.IsModsVerified == true)
                return true;

            Log.Warning(Tag, $"Rejected {packetName} from unverified peer {fromPeerId}");
            return false;
        }

        private void SendSpawnSettings()
        {
            Send(PacketType.LobbySpawnSettings, PacketSerializer.SerializeLobbySpawnSettings(
                new LobbySpawnSettingsPacket { SpawnType = ToWire(_session.SpawnType), MapName = _session.MapName }));
        }

        private void EnsureLocalAirfieldForCurrentMap()
        {
            var local = _session.GetLocalPlayer();
            if (local == null) return;
            string mapName = _session.MapName ?? MapHelper.GetDefaultMapName();
            if (!string.IsNullOrEmpty(local.SelectedAirfield)
                && MapHelper.IsAirfieldOnMap(mapName, local.SelectedAirfield))
                return;

            local.SelectedAirfield = MapHelper.GetDefaultAirfieldName(mapName);
            if (!string.IsNullOrEmpty(local.SelectedAirfield))
            {
                PersistAirfield(local.SelectedAirfield);
                Send(PacketType.LobbyAirfieldSelect, PacketSerializer.SerializeLobbyAirfieldSelect(
                    new LobbyAirfieldSelectPacket { PeerId = _session.LocalPeerId, AirfieldName = local.SelectedAirfield }));
            }
        }

        private static bool HasLocalSelectionChanged(
            PlayerInfo local,
            string incomingAircraft,
            string incomingAirfield,
            string incomingLoadout)
        {
            if (local == null) return false;

            return IsDifferentNonEmpty(local.SelectedAircraft, incomingAircraft)
                || IsDifferentNonEmpty(local.SelectedAirfield, incomingAirfield)
                || IsDifferentNonEmpty(local.SelectedLoadout, incomingLoadout);
        }

        private static bool IsDifferentNonEmpty(string current, string incoming)
        {
            return !string.IsNullOrEmpty(current)
                && !string.Equals(current, incoming, StringComparison.Ordinal);
        }

        private static void PersistAircraft(string name)
        {
            if (string.IsNullOrEmpty(name) || ModConfig.LastAircraft == null) return;
            ModConfig.LastAircraft.Value = name;
            ModConfig.Save();
        }

        private static void PersistAirfield(string name)
        {
            if (string.IsNullOrEmpty(name) || ModConfig.LastAirfield == null) return;
            ModConfig.LastAirfield.Value = name;
            ModConfig.Save();
        }

        private static void PersistLoadout(string name)
        {
            if (string.IsNullOrEmpty(name) || ModConfig.LastLoadout == null) return;
            ModConfig.LastLoadout.Value = name;
            ModConfig.Save();
        }
        private void BroadcastLobbyState()
        {
            if (!_session.IsHost) return;

            var players = _session.Players;
            var wire = new LobbyPlayerInfo[players.Count];
            int i = 0;
            foreach (var p in players.Values)
                wire[i++] = new LobbyPlayerInfo
                {
                    PeerId = p.PeerId, PlayerName = p.PlayerName ?? "Player",
                    SelectedAircraft = p.SelectedAircraft ?? "", SelectedAirfield = p.SelectedAirfield ?? "",
                    SelectedLoadout = p.SelectedLoadout ?? "",
                    IsReady = p.IsReady, IsLoaded = p.IsLoaded, IsHost = p.IsHost,
                    Team = ToWire(p.Team),
                    IsModsVerified = p.IsModsVerified,
                    IsModSyncing = p.IsModSyncing,
                    HasModCompatibilityState = true
                };
            var state = _session.StateMachine.CurrentState;
            var pkt = new LobbyStatePacket
            {
                HostName = _session.HostName ?? "Host", MapName = _session.MapName ?? "ActionIsland",
                SpawnType = ToWire(_session.SpawnType),
                GameStarted = state == GameState.InGame || state == GameState.Spawning,
                GameLoading = state == GameState.Loading,
                AircraftCollisionsEnabled = _session.AircraftCollisionsEnabled,
                TimeOfDay = ToWire(_session.TimeOfDay),
                GameMode = ToWire(_session.GameMode),
                MaxPlayersTotal = _session.MaxPlayersTotal,
                TeamCount = _session.TeamCount,
                Revision = ++_stateRevision,
                Players = wire
            };
            var payload = PacketSerializer.SerializeLobbyState(pkt);
            if (payload != null)
                _connection.BroadcastReliable(PacketSerializer.Serialize(PacketType.LobbyState, payload));
        }

        private void CheckAllReady()
        {
            foreach (var p in _session.Players.Values)
                if (!p.IsHost && !p.IsReady) return;
            Log.Info(Tag, "All players ready");
        }

        private void ClampTeamStateForCurrentPlayers()
        {
            _session.TeamCount = GameSession.ClampTeamCountForPlayers(_session.TeamCount, _session.PlayerCount);
            if (_session.GameMode != Core.MultiplayerGameMode.TeamDogfight)
            {
                foreach (var p in _session.Players.Values)
                    p.Team = Core.MultiplayerTeam.None;
                return;
            }

            if (_session.PlayerCount <= 2)
            {
                foreach (var p in _session.Players.Values)
                    p.Team = p.IsHost ? Core.MultiplayerTeam.Team1 : Core.MultiplayerTeam.Team2;
                return;
            }

            foreach (var p in _session.Players.Values)
                p.Team = GameSession.ClampTeam(p.Team, _session.TeamCount);
        }

        private void CheckAllLoaded()
        {
            foreach (var p in _session.Players.Values)
                if (!p.IsLoaded) return;
            Send(PacketType.LobbySpawnPlayers, PacketSerializer.SerializeLobbySpawnPlayers(
                new LobbySpawnPlayersPacket { Timestamp = GetTime() }));
            _session.StateMachine.TryTransition(GameState.Spawning);
            OnAllPlayersLoaded?.Invoke();
            Log.Info(Tag, "All loaded — triggering spawn");
        }

        private static float GetTime()
        {
            try
            {
                return TimeProvider?.Invoke() ?? 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private void ResetPlayersForLobby()
        {
            foreach (var p in _session.Players.Values)
            { p.IsReady = false; p.IsLoaded = false; }
        }

        private int ComputeStateHash()
        {
            int hash = 17;
            hash = hash * 31 + (_session.MapName?.GetHashCode() ?? 0);
            hash = hash * 31 + (int)_session.SpawnType;
            hash = hash * 31 + (int)_session.TimeOfDay;
            hash = hash * 31 + (int)_session.GameMode;
            hash = hash * 31 + _session.MaxPlayersTotal;
            hash = hash * 31 + _session.TeamCount;
            hash = hash * 31 + (_session.AircraftCollisionsEnabled ? 1 : 0);
            foreach (var p in _session.Players.Values)
            {
                hash = hash * 31 + (p.PlayerName?.GetHashCode() ?? 0);
                hash = hash * 31 + (p.SelectedAircraft?.GetHashCode() ?? 0);
                hash = hash * 31 + (p.SelectedAirfield?.GetHashCode() ?? 0);
                hash = hash * 31 + (p.SelectedLoadout?.GetHashCode() ?? 0);
                hash = hash * 31 + (int)p.Team;
                hash = hash * 31 + (p.IsReady ? 1 : 0) + (p.IsLoaded ? 2 : 0);
                hash = hash * 31 + (p.IsModsVerified ? 1 : 0) + (p.IsModSyncing ? 2 : 0);
            }
            return hash;
        }

        private static WireSpawnType ToWire(Core.LobbySpawnType s) => (WireSpawnType)(int)s;
        private static Core.LobbySpawnType FromWire(WireSpawnType s) => (Core.LobbySpawnType)(int)s;
        private static WireTimeOfDay ToWire(TimeOfDaySetting t) => (WireTimeOfDay)(int)t;
        private static TimeOfDaySetting FromWire(WireTimeOfDay t) => (TimeOfDaySetting)(int)t;
        private static WireGameMode ToWire(Core.MultiplayerGameMode m) => (WireGameMode)(int)m;
        private static Core.MultiplayerGameMode FromWire(WireGameMode m) => (Core.MultiplayerGameMode)(int)m;
        private static WireTeam ToWire(Core.MultiplayerTeam t) => (WireTeam)(int)t;
        private static Core.MultiplayerTeam FromWire(WireTeam t) => (Core.MultiplayerTeam)(int)t;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.OnPlayerLeft -= HandleSessionPlayerLeft;
            var unregTypes = new[] {
                PacketType.LobbyState, PacketType.LobbyPlayerJoined, PacketType.LobbyPlayerLeft,
                PacketType.LobbyPlayerReady, PacketType.LobbyAirfieldSelect, PacketType.LobbySpawnSettings,
                PacketType.LobbyStartGame, PacketType.LobbyLoadingComplete, PacketType.LobbySpawnPlayers,
                PacketType.LobbyRespawnRequest, PacketType.LobbyWelcome, PacketType.LobbyReturnToLobby,
                PacketType.LobbyTeamSelect, PacketType.AircraftSelect, PacketType.LoadoutSelect
            };
            foreach (var t in unregTypes) _router.Unregister(t);
            OnLobbyStateChanged = null;
            OnGameStarting = null;
            OnAllPlayersLoaded = null;
            Log.Info(Tag, "Disposed");
        }
    }
}
