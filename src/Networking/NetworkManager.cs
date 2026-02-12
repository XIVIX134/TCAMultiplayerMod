using System;
using UnityEngine;
using TCAMultiplayer.Game;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Manages network transport and coordinates packet routing.
    /// Refactored to delegate to specialized managers for specific concerns.
    /// </summary>
    public class NetworkManager
    {
        public INetworkTransport Transport { get; private set; }
        public string CurrentTransportName => Transport?.Name ?? "None";
        public bool IsConnected => Transport?.IsConnected ?? false;
        public bool IsHost => GameStateMachine.Instance?.IsHost ?? false;

        // Local peer ID (assigned by host on connect)
        public ulong LocalPeerId { get; set; }

        // Remote peer ID (for 2-player mode; future: track multiple peers)
        public ulong RemotePeerId => LocalPeerId == 1UL ? 2UL : 1UL;

        /// <summary>
        /// Get the local player's ID. Use this instead of hardcoding IsHost ? 1UL : 2UL
        /// </summary>
        public ulong GetLocalPlayerId() => LocalPeerId != 0 ? LocalPeerId : (IsHost ? 1UL : 2UL);

        /// <summary>
        /// Get the remote player's ID. Use this instead of hardcoding IsHost ? 2UL : 1UL
        /// </summary>
        public ulong GetRemotePlayerId() => LocalPeerId != 0 ? RemotePeerId : (IsHost ? 2UL : 1UL);

        // Statistics
        public int PacketsSent { get; private set; }
        public int PacketsReceived { get; private set; }
        public int AircraftStatesSent { get; private set; }
        public int AircraftStatesReceived { get; private set; }

        // Events
        public event Action<ulong> OnPeerConnected;
        public event Action<ulong> OnPeerDisconnected;
        public event Action<ulong, PacketType, byte[]> OnPacketReceived;

        // Specialized managers
        private readonly PacketRouter _router = new PacketRouter();
        private readonly RemoteAircraftManager _remoteAircraft = new RemoteAircraftManager();

        // Public access to remote aircraft state
        public Vector3 LastRemotePosition => _remoteAircraft.LastRemotePosition;
        public Vector3 LocalPlayerPosition => _remoteAircraft.LocalPlayerPosition;
        public bool HasRemoteAircraft => _remoteAircraft.HasRemoteAircraft;
        public float TimeSinceRemoteUpdate => _remoteAircraft.TimeSinceRemoteUpdate;
        public float DistanceToRemote => _remoteAircraft.DistanceToRemote;
        public bool IsExtrapolating => _remoteAircraft.IsExtrapolating;
        public string BufferStatus => _remoteAircraft.BufferStatus;
        public bool UsingRealAircraft => _remoteAircraft.UsingRealAircraft;
        public Vector3 RemoteVelocity => _remoteAircraft.RemoteVelocity;
        public float ExtrapolationTime => _remoteAircraft.ExtrapolationTime;
        public Player.RemoteAircraftController RemoteController => _remoteAircraft.Controller;

        public NetworkManager()
        {
            LocalPeerId = 0;
            SetTransport(new DirectTransport());
            RegisterPacketHandlers();
        }

        private void RegisterPacketHandlers()
        {
            // Aircraft state
            _router.RegisterHandler(PacketType.AircraftState, HandleAircraftState);
            _router.RegisterHandler(PacketType.AircraftDestroyed, HandleAircraftDestroyed);
            _router.RegisterHandler(PacketType.Respawned, HandleAircraftRespawn);
            _router.RegisterHandler(PacketType.AircraftChanged, HandleAircraftRespawn);

            // Combat
            _router.RegisterHandler(PacketType.DamageDealt, HandleDamageDealt);
            _router.RegisterHandler(PacketType.MissileLaunch, HandleMissileLaunch);
            _router.RegisterHandler(PacketType.RadarLock, HandleRadarLock);
            _router.RegisterHandler(PacketType.RadarLockLost, HandleRadarLock);
            _router.RegisterHandler(PacketType.ProjectileImpact, HandleProjectileImpact);
            _router.RegisterHandler(PacketType.KillConfirm, HandleKillConfirm);

            // Lobby packets - routed to LobbyManager
            _router.RegisterHandler(PacketType.LobbyState, HandleLobbyState);
            _router.RegisterHandler(PacketType.LobbyWelcome, HandleLobbyWelcome);
            _router.RegisterHandler(PacketType.LobbyPlayerJoined, HandleLobbyPlayerJoined);
            _router.RegisterHandler(PacketType.LobbyPlayerLeft, HandleLobbyPlayerLeft);
            _router.RegisterHandler(PacketType.LobbyPlayerReady, HandleLobbyPlayerReady);
            _router.RegisterHandler(PacketType.LobbyAirfieldSelect, HandleLobbyAirfieldSelect);
            _router.RegisterHandler(PacketType.LobbySpawnSettings, HandleLobbySpawnSettings);
            _router.RegisterHandler(PacketType.LobbyStartGame, HandleLobbyStartGame);
            _router.RegisterHandler(PacketType.LobbyLoadingComplete, HandleLobbyLoadingComplete);
            _router.RegisterHandler(PacketType.LobbySpawnPlayers, HandleLobbySpawnPlayers);
            _router.RegisterHandler(PacketType.LobbyRespawnRequest, HandleLobbyRespawnRequest);
        }

        #region Transport Management

        public void SetTransport(INetworkTransport transport)
        {
            if (Transport != null)
            {
                Transport.OnPeerConnected -= HandlePeerConnected;
                Transport.OnPeerDisconnected -= HandlePeerDisconnected;
                Transport.OnDataReceived -= HandleDataReceived;
                Transport.Shutdown();
            }

            Transport = transport;

            if (Transport != null)
            {
                Transport.OnPeerConnected += HandlePeerConnected;
                Transport.OnPeerDisconnected += HandlePeerDisconnected;
                Transport.OnDataReceived += HandleDataReceived;
            }

            LocalPeerId = 0;
            Plugin.Log.LogInfo($"[NetworkManager] Transport set to {CurrentTransportName}");
        }

        public void StartHost(int port)
        {
            LocalPeerId = 1;
            Transport?.StartHost(port);
        }

        public void StartClient(string address, int port) => Transport?.Connect(address, port);
        public void Connect(string address, int port) => Transport?.Connect(address, port);

        public void Disconnect()
        {
            _remoteAircraft.Cleanup();
            Transport?.Disconnect();
        }

        public void Update()
        {
            Transport?.Update();
            _remoteAircraft.Update();
        }

        public void Shutdown()
        {
            _remoteAircraft.Cleanup();
            Transport?.Shutdown();
        }

        #endregion

        #region Packet Sending

        public void SendPacket(PacketType type, byte[] data = null, bool reliable = true)
        {
            if (Transport == null || !Transport.IsConnected)
            {
                if (LogHelper.IsEnabled(LogCategory.Network) &&
                    LogHelper.ShouldLogInterval("NetworkManager.SendPacket.NotConnected", 10f))
                {
                    LogHelper.Info(LogCategory.Network, "[NetworkManager] SendPacket dropped: not connected (host may be waiting for clients)");
                }
                return;
            }

            if (LogHelper.IsEnabled(LogCategory.Packets) &&
                LogHelper.ShouldSample("NetworkManager.SendPacket", LogHelper.PacketSampleRate))
            {
                int payloadLen = data?.Length ?? 0;
                LogHelper.Info(LogCategory.Packets, $"[NetworkManager] Send {type} bytes={payloadLen + 1} reliable={reliable}");
            }

            byte[] packet = PacketSerializer.Serialize(type, data);
            Transport.Send(packet, reliable);
            PacketsSent++;
        }

        public void SendAircraftState(AircraftStatePacket state)
        {
            byte[] data = PacketSerializer.SerializeAircraftState(state);
            SendPacket(PacketType.AircraftState, data, reliable: false);
            AircraftStatesSent++;

            if (LogHelper.IsEnabled(LogCategory.Network) &&
                LogHelper.ShouldSample("NetworkManager.SendAircraftState", LogHelper.HighFreqSampleRate))
            {
                LogHelper.Info(LogCategory.Network,
                    $"[NetworkManager] Sent state t={state.Timestamp:F2} pos=({state.PosX:F1},{state.PosY:F1},{state.PosZ:F1}) " +
                    $"vel=({state.VelX:F1},{state.VelY:F1},{state.VelZ:F1}) flags=AB:{state.Afterburner} Gear:{state.GearDown} " +
                    $"Flaps:{state.FlapsDown} Fire:{state.IsFiring}");
            }
        }

        public void SendAircraftRespawnNotification(string aircraftType)
        {
            try
            {
                var packet = new AircraftChangedPacket
                {
                    PlayerId = GetLocalPlayerId(),
                    AircraftType = aircraftType,
                    IsAlive = true
                };

                byte[] data = PacketSerializer.SerializeAircraftChanged(packet);
                SendPacket(PacketType.Respawned, data, reliable: true);
                Plugin.Log.LogInfo($"[NetworkManager] Sent respawn notification: {aircraftType}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] SendAircraftRespawnNotification error: {ex.Message}");
            }
        }

        public void SendAircraftDestroyedNotification()
        {
            try
            {
                SendPacket(PacketType.AircraftDestroyed, null, reliable: true);
                Plugin.Log.LogInfo("[NetworkManager] Sent aircraft destroyed notification");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] SendAircraftDestroyedNotification error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send kill confirmation packet to all peers for scoreboard sync.
        /// Called by the victim when they are destroyed.
        /// </summary>
        public void SendKillConfirmation(ulong killerId, ulong victimId, string weaponName)
        {
            try
            {
                var packet = new KillConfirmPacket
                {
                    KillerId = killerId,
                    VictimId = victimId,
                    WeaponName = weaponName ?? "Unknown"
                };

                byte[] data = PacketSerializer.SerializeKillConfirm(packet);
                SendPacket(PacketType.KillConfirm, data, reliable: true);
                Plugin.Log.LogInfo($"[NetworkManager] Sent kill confirmation: {killerId} killed {victimId} with {weaponName}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] SendKillConfirmation error: {ex.Message}");
            }
        }

        #endregion

        #region Transport Event Handlers

        private void HandlePeerConnected(ulong peerId)
        {
            Plugin.Log.LogInfo($"[NetworkManager] Peer {peerId} connected!");
            _remoteAircraft.SetRemotePeer(peerId);
            OnPeerConnected?.Invoke(peerId);
            
            // Register both players in ScoreTracker with best-known names.
            // Names will be updated again when lobby player-joined packets arrive.
            var lobby = Plugin.Instance?.Lobby;
            string localName = lobby?.LocalPlayerName ?? lobby?.HostName ?? $"Player {LocalPeerId}";
            string remoteName = $"Player {peerId}";
            if (LocalPeerId != 0)
                ScoreTracker.Instance?.RegisterPlayer(LocalPeerId, localName);
            ScoreTracker.Instance?.RegisterPlayer(peerId, remoteName);
            if (lobby != null)
            {
                if (IsHost)
                {
                    var welcomePacket = new LobbyWelcomePacket
                    {
                        AssignedPeerId = peerId,
                        HostName = lobby.HostName
                    };
                    SendPacket(PacketType.LobbyWelcome, PacketSerializer.SerializeLobbyWelcome(welcomePacket), true);
                }
            }
        }

        private void HandlePeerDisconnected(ulong peerId)
        {
            Plugin.Log.LogInfo($"[NetworkManager] Peer {peerId} disconnected");
            _remoteAircraft.HandlePeerDisconnected(peerId);
            OnPeerDisconnected?.Invoke(peerId);
            Plugin.Instance?.Lobby?.HandlePlayerLeft(peerId);

            // Graceful disconnect: transition GameStateMachine to Disconnected
            // so the game state is properly reset regardless of current phase
            var gsm = GameStateMachine.Instance;
            if (gsm != null && gsm.CurrentState != GameState.Disconnected)
            {
                Plugin.Log.LogInfo($"[NetworkManager] Peer disconnected while in state {gsm.CurrentState} — transitioning to Disconnected");
                gsm.Disconnect();
            }

            // Stop LAN discovery broadcasting/listening
            Plugin.Instance?.Discovery?.StopBroadcasting();
            Plugin.Instance?.Discovery?.StopListening();
        }

        private void HandleDataReceived(ulong peerId, byte[] data)
        {
            PacketsReceived++;

            try
            {
                var (packetType, payload) = PacketSerializer.Deserialize(data);

                if (LogHelper.IsEnabled(LogCategory.Packets) &&
                    LogHelper.ShouldSample("NetworkManager.HandleDataReceived", LogHelper.PacketSampleRate))
                {
                    LogHelper.Info(LogCategory.Packets,
                        $"[NetworkManager] Recv {packetType} from {peerId} bytes={(payload?.Length ?? 0) + 1}");
                }

                OnPacketReceived?.Invoke(peerId, packetType, payload);
                _router.ProcessPacket(peerId, packetType, payload);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NetworkManager] Packet error: {ex.Message}");
            }
        }

        #endregion

        #region Packet Handlers

        private void HandleAircraftState(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            AircraftStatesReceived++;
            var state = PacketSerializer.DeserializeAircraftState(payload);
            _remoteAircraft.HandleStatePacket(peerId, state);
        }

        private void HandleAircraftDestroyed(ulong peerId, byte[] payload)
        {
            Plugin.Log.LogInfo("[NetworkManager] Remote aircraft destroyed!");
            _remoteAircraft.HandleDestroyed(peerId);
        }

        private void HandleAircraftRespawn(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeAircraftChanged(payload);
            Plugin.Log.LogInfo($"[NetworkManager] Remote aircraft respawned: {packet.AircraftType}");
            _remoteAircraft.HandleRespawn(peerId, packet);
        }

        private void HandleDamageDealt(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeDamage(payload);
            Plugin.Log.LogInfo($"[NetworkManager] Received damage: {packet.Damage}");
            Patches.DamagePatches.HandleReceivedDamage(packet);
        }

        private void HandleMissileLaunch(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeMissileLaunch(payload);
            Plugin.Log.LogInfo($"[NetworkManager] Received missile launch: {packet.MissileType}");
            Patches.WeaponPatches.HandleMissileLaunch(packet);
        }

        private void HandleRadarLock(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeRadarLock(payload);
            Plugin.Log.LogInfo($"[NetworkManager] Received radar lock: {packet.IsLocked}");
            Patches.WeaponPatches.HandleRadarLock(packet);
        }

        private void HandleKillConfirm(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeKillConfirm(payload);
            Plugin.Log.LogInfo($"[NetworkManager] Kill confirmed: {packet.KillerId} killed {packet.VictimId} with {packet.WeaponName}");
            
            // Record the kill in the scoreboard
            Game.ScoreTracker.Instance?.RecordKill(packet.KillerId, packet.VictimId, packet.WeaponName);
        }

        private void HandleProjectileImpact(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeProjectileImpact(payload);
            Plugin.Log.LogInfo($"[NetworkManager] Received impact: {packet.WeaponName} at ({packet.ImpactPosX:F1},{packet.ImpactPosY:F1},{packet.ImpactPosZ:F1})");
            Patches.DamagePatches.HandleReceivedImpact(packet);
        }

        #endregion

        #region Lobby Packet Handlers

        private void HandleLobbyWelcome(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeLobbyWelcome(payload);
            Plugin.Log.LogInfo($"[NetworkManager] Welcome from host. Assigned ID: {packet.AssignedPeerId}");

            // Keep NetworkManager identity in sync
            LocalPeerId = packet.AssignedPeerId;

            // Ensure GameStateMachine transitions to ClientLobby so loading/spawning flow is valid
            try
            {
                var gsm = GameStateMachine.Instance;
                if (gsm != null)
                {
                    Plugin.Log.LogInfo($"[NetworkManager] GameStateMachine current state: {gsm.CurrentState}");

                    // Some UI paths call Network.StartClient without calling GameState.StartConnecting first.
                    // If we're still Disconnected, move into Connecting so OnConnected can succeed.
                    if (gsm.CurrentState == GameState.Disconnected)
                    {
                        Plugin.Log.LogInfo("[NetworkManager] Client in Disconnected state, forcing transition to Connecting");
                        bool transitioned = gsm.TransitionTo(GameState.Connecting);
                        if (!transitioned)
                        {
                            Plugin.Log.LogError("[NetworkManager] Failed to transition from Disconnected to Connecting!");
                        }
                    }

                    // Now try to complete the connection (Connecting -> ClientLobby)
                    if (gsm.CurrentState == GameState.Connecting)
                    {
                        bool connected = gsm.OnConnected(packet.AssignedPeerId);
                        if (connected)
                        {
                            Plugin.Log.LogInfo($"[NetworkManager] Successfully transitioned to ClientLobby, state: {gsm.CurrentState}");
                        }
                        else
                        {
                            Plugin.Log.LogError($"[NetworkManager] OnConnected failed! Current state: {gsm.CurrentState}");
                        }
                    }
                    else if (gsm.CurrentState != GameState.ClientLobby)
                    {
                        // Already in ClientLobby is OK, anything else is unexpected
                        Plugin.Log.LogWarning($"[NetworkManager] Unexpected state after connection attempt: {gsm.CurrentState}");
                    }
                }
                else
                {
                    Plugin.Log.LogError("[NetworkManager] GameStateMachine.Instance is null! Cannot track connection state.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[NetworkManager] GameStateMachine sync failed in LobbyWelcome: {ex.Message} | StackTrace: {ex.StackTrace}");
            }

            var lobby = Plugin.Instance?.Lobby;
            if (lobby != null)
            {
                lobby.JoinLobby(LocalPeerId);

                // Register players in ScoreTracker now that we have our real PeerId and names
                ScoreTracker.Instance?.RegisterPlayer(LocalPeerId, lobby.LocalPlayerName);
                // Host peer is always 1
                ScoreTracker.Instance?.RegisterPlayer(1UL, packet.HostName ?? "Host");

                var joinPacket = new LobbyPlayerJoinedPacket
                {
                    PeerId = LocalPeerId,
                    PlayerName = lobby.LocalPlayerName
                };
                SendPacket(PacketType.LobbyPlayerJoined, PacketSerializer.SerializeLobbyPlayerJoined(joinPacket), true);
                SendPacket(PacketType.LobbyAirfieldSelect, PacketSerializer.SerializeLobbyAirfieldSelect(
                    new LobbyAirfieldSelectPacket { PeerId = LocalPeerId, AirfieldName = lobby.LocalSelectedAirfield }), true);
            }
        }

        private void HandleLobbyState(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeLobbyState(payload);
            Plugin.Instance?.Lobby?.UpdateFromLobbyState(packet);
        }

        private void HandleLobbyPlayerJoined(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeLobbyPlayerJoined(payload);
            Plugin.Instance?.Lobby?.HandlePlayerJoined(packet.PeerId, packet.PlayerName);
            
            // Update ScoreTracker with the real player name now that we know it
            ScoreTracker.Instance?.RegisterPlayer(packet.PeerId, packet.PlayerName);
        }

        private void HandleLobbyPlayerLeft(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeLobbyPlayerLeft(payload);
            Plugin.Instance?.Lobby?.HandlePlayerLeft(packet.PeerId);
        }

        private void HandleLobbyPlayerReady(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeLobbyPlayerReady(payload);
            Plugin.Instance?.Lobby?.HandlePlayerReady(packet.PeerId, packet.IsReady);
        }

        private void HandleLobbyAirfieldSelect(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeLobbyAirfieldSelect(payload);
            Plugin.Instance?.Lobby?.HandlePlayerAirfieldSelect(packet.PeerId, packet.AirfieldName);
        }

        private void HandleLobbySpawnSettings(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeLobbySpawnSettings(payload);
            Plugin.Instance?.Lobby?.SetSpawnSettings(packet.SpawnType, packet.MapName);
        }

        private void HandleLobbyStartGame(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeLobbyStartGame(payload);
            Plugin.Log?.LogInfo($"[NetworkManager] Start game: {packet.MapName}");
            Plugin.Instance?.Lobby?.HandleGameStart(packet.MapName, packet.SpawnType);
        }

        private void HandleLobbyLoadingComplete(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeLobbyLoadingComplete(payload);
            Plugin.Instance?.Lobby?.HandlePlayerLoaded(packet.PeerId);
        }

        private void HandleLobbySpawnPlayers(ulong peerId, byte[] payload)
        {
            Plugin.Log?.LogInfo("[NetworkManager] Spawn players signal");
            Plugin.Instance?.Lobby?.HandleSpawnPlayers();
        }

        private void HandleLobbyRespawnRequest(ulong peerId, byte[] payload)
        {
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeLobbyRespawnRequest(payload);
            Plugin.Log?.LogInfo($"[NetworkManager] Respawn request from {packet.PeerId}");
            
            // Call HandleRespawn to reset the NeedsRespawn flag and allow aircraft recreation
            // AircraftType will be set from the next state packet
            var respawnPacket = new AircraftChangedPacket { AircraftType = "" };
            _remoteAircraft.HandleRespawn(packet.PeerId, respawnPacket);
        }

        #endregion
    }
}
