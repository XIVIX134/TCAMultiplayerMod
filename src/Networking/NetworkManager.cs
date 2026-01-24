using System;
using System.Reflection;
using UnityEngine;
using TCAMultiplayer.Player;
using TCAMultiplayer;

namespace TCAMultiplayer.Networking
{
    public class NetworkManager
    {
        public INetworkTransport Transport { get; private set; }
        public string CurrentTransportName => Transport?.Name ?? "None";
        public bool IsConnected => Transport?.IsConnected ?? false;
        public bool IsHost => Transport?.IsHost ?? false;
        
        // Local peer ID (generated on startup)
        public ulong LocalPeerId { get; private set; }
        
        public int PacketsSent { get; private set; }
        public int PacketsReceived { get; private set; }
        public int AircraftStatesSent { get; private set; }
        public int AircraftStatesReceived { get; private set; }
        
        public event Action<ulong> OnPeerConnected;
        public event Action<ulong> OnPeerDisconnected;
        public event Action<ulong, PacketType, byte[]> OnPacketReceived;
        
        // Remote player tracking
        private ulong _remotePeerId = 0;
        private GameObject _remoteAircraftObject = null;
        private RemoteAircraftController _remoteController = null;
        private float _lastRemoteUpdateTime = 0f;
        private Vector3 _localPlayerPosition = Vector3.zero;
        
        // Interpolation buffer for smooth movement
        private InterpolationBuffer _interpolationBuffer = new InterpolationBuffer(30);
        
        // Display state (what we actually render)
        private Vector3 _displayPosition = Vector3.zero;
        private Quaternion _displayRotation = Quaternion.identity;
        private bool _isExtrapolating = false;
        private bool _wasExtrapolating = false;
        
        // Track if we're using a real aircraft clone or fallback marker
        private bool _usingRealAircraft = false;
        
        // Retry cloning when local aircraft becomes available
        private bool _needsAircraftCloneRetry = false;
        private float _lastCloneRetryTime = 0f;
        private const float CLONE_RETRY_INTERVAL = 1.0f; // Retry every 1 second
        
        // Last known velocity (for display in UI)
        private Vector3 _lastVelocity = Vector3.zero;
        
        // Debug info
        public Vector3 LastRemotePosition => _displayPosition;
        public Vector3 LocalPlayerPosition => _localPlayerPosition;
        public bool HasRemoteAircraft => _remoteAircraftObject != null;
        public float TimeSinceRemoteUpdate => Time.time - _lastRemoteUpdateTime;
        public float DistanceToRemote => Vector3.Distance(_localPlayerPosition, _displayPosition);
        public bool IsExtrapolating => _isExtrapolating;
        public string BufferStatus => _interpolationBuffer?.GetDebugInfo() ?? "N/A";
        public bool UsingRealAircraft => _usingRealAircraft;
        public Vector3 RemoteVelocity => _lastVelocity;
        public float ExtrapolationTime => _isExtrapolating ? TimeSinceRemoteUpdate : 0f;

        public NetworkManager()
        {
            LocalPeerId = 0;
            
            SetTransport(new DirectTransport());
        }

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
            
            Plugin.Log.LogInfo($"NetworkManager: Transport set to {CurrentTransportName}");
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
            CleanupRemoteAircraft();
            Transport?.Disconnect();
        }

        public void Update()
        {
            Transport?.Update();
            UpdateLocalPlayerPosition();
            UpdateRemoteAircraftInterpolation();
            TryRetryAircraftClone();
        }

        public void Shutdown()
        {
            CleanupRemoteAircraft();
            Transport?.Shutdown();
        }

        private void UpdateLocalPlayerPosition()
        {
            try
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    _localPlayerPosition = cam.transform.position;
                }
            }
            catch { }
        }

        /// <summary>
        /// Updates remote aircraft position using interpolation buffer
        /// </summary>
        private void UpdateRemoteAircraftInterpolation()
        {
            if (_remoteAircraftObject == null) return;
            if (!_interpolationBuffer.HasData) return;
            
            try
            {
                // Get interpolated state from buffer
                var (position, rotation, isExtrapolating) = _interpolationBuffer.GetInterpolatedState();
                
                _displayPosition = position;
                _displayRotation = rotation;
                _isExtrapolating = isExtrapolating;
                
                if (_isExtrapolating != _wasExtrapolating)
                {
                    LogHelper.Info(LogCategory.Interpolation,
                        $"[NetworkManager] Extrapolation {(_isExtrapolating ? "started" : "ended")} " +
                        $"after {TimeSinceRemoteUpdate:F2}s (buffer: {BufferStatus})");
                }
                _wasExtrapolating = _isExtrapolating;
                
                if (LogHelper.IsEnabled(LogCategory.Interpolation) &&
                    LogHelper.ShouldLogInterval("NetworkManager.InterpolationStatus", LogHelper.DefaultIntervalSeconds))
                {
                    LogHelper.Info(LogCategory.Interpolation,
                        $"[NetworkManager] Interp buffer={BufferStatus} extrapolating={_isExtrapolating} " +
                        $"timeSinceUpdate={TimeSinceRemoteUpdate:F2}s");
                }
                
                // Apply to object
                _remoteAircraftObject.transform.position = _displayPosition;
                _remoteAircraftObject.transform.rotation = _displayRotation;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] Interpolation error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Periodically retry cloning real aircraft if we're using fallback marker
        /// </summary>
        private void TryRetryAircraftClone()
        {
            // Only retry if we need to and have a remote object
            if (!_needsAircraftCloneRetry || _remoteAircraftObject == null) return;
            if (_usingRealAircraft) return; // Already using real aircraft
            
            // Rate limit retries
            if (Time.time - _lastCloneRetryTime < CLONE_RETRY_INTERVAL) return;
            _lastCloneRetryTime = Time.time;
            
            try
            {
                // Try to find a local aircraft now
                var sourceAircraft = FindLocalAircraftForCloning();
                if (sourceAircraft == null) return; // Still no aircraft, try again later
                
                Plugin.Log.LogInfo("[NetworkManager] Local aircraft now available, attempting to upgrade remote marker to real aircraft...");
                
                // Save current position/rotation
                Vector3 savedPos = _remoteAircraftObject.transform.position;
                Quaternion savedRot = _remoteAircraftObject.transform.rotation;
                
                // Try to clone
                var clone = TryCloneRealAircraft(_remotePeerId);
                if (clone != null)
                {
                    // Destroy old marker
                    GameObject.Destroy(_remoteAircraftObject);
                    
                    // Use new clone
                    _remoteAircraftObject = clone;
                    _remoteAircraftObject.transform.position = savedPos;
                    _remoteAircraftObject.transform.rotation = savedRot;
                    
                    // Add screen-space marker
                    _remoteAircraftObject.AddComponent<ScreenSpaceMarker>();
                    
                    _usingRealAircraft = true;
                    _needsAircraftCloneRetry = false;
                    
                    Plugin.Log.LogInfo("[NetworkManager] Successfully upgraded to real aircraft clone!");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NetworkManager] Clone retry failed: {ex.Message}");
            }
        }

        public void SendPacket(PacketType type, byte[] data = null, bool reliable = true)
        {
            if (Transport == null || !Transport.IsConnected)
            {
                if (LogHelper.IsEnabled(LogCategory.Network) &&
                    LogHelper.ShouldLogInterval("NetworkManager.SendPacket.NotConnected", LogHelper.DefaultIntervalSeconds))
                {
                    LogHelper.Info(LogCategory.Network, "[NetworkManager] SendPacket dropped: not connected");
                }
                return;
            }
            
            if (LogHelper.IsEnabled(LogCategory.Packets) &&
                LogHelper.ShouldSample("NetworkManager.SendPacket", LogHelper.PacketSampleRate))
            {
                int payloadLen = data?.Length ?? 0;
                LogHelper.Info(LogCategory.Packets,
                    $"[NetworkManager] Send {type} bytes={payloadLen + 1} reliable={reliable}");
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

        private void HandlePeerConnected(ulong peerId)
        {
            Plugin.Log.LogInfo($"NetworkManager: Peer {peerId} connected!");
            _remotePeerId = peerId;
            OnPeerConnected?.Invoke(peerId);
            
            // Handle lobby connection
            var lobby = Plugin.Instance?.Lobby;
            if (lobby != null)
            {
                if (IsHost)
                {
                    // Host: Send Welcome packet with assigned PeerID (which is the Transport ID)
                    var welcomePacket = new LobbyWelcomePacket
                    {
                        AssignedPeerId = peerId,
                        HostName = lobby.HostName
                    };
                    SendPacket(PacketType.LobbyWelcome, PacketSerializer.SerializeLobbyWelcome(welcomePacket), true);
                    
                    // Note: We don't add the player to the lobby yet.
                    // We wait for them to confirm receipt with a LobbyPlayerJoined packet.
                }
                else
                {
                    if (Plugin.Instance?.State != null)
                    {
                        Plugin.Instance.State.ConnectionStatus = ConnectionStatus.Connected;
                    }
                }
            }
        }

        private void HandleLobbyWelcome(LobbyWelcomePacket packet)
        {
            Plugin.Log.LogInfo($"[NetworkManager] Received Welcome from host. Assigned ID: {packet.AssignedPeerId}");
            
            // Update local ID to match what the host assigned
            LocalPeerId = packet.AssignedPeerId;
            
            var lobby = Plugin.Instance?.Lobby;
            if (lobby != null)
            {
                // Now join the lobby locally with the correct ID
                lobby.JoinLobby(LocalPeerId);
                
                // Send our player info back to host
                var joinPacket = new LobbyPlayerJoinedPacket 
                { 
                    PeerId = LocalPeerId, 
                    PlayerName = lobby.LocalPlayerName 
                };
                SendPacket(PacketType.LobbyPlayerJoined, PacketSerializer.SerializeLobbyPlayerJoined(joinPacket), true);
            }
                SendPacket(PacketType.LobbyAirfieldSelect, PacketSerializer.SerializeLobbyAirfieldSelect(new LobbyAirfieldSelectPacket { PeerId = LocalPeerId, AirfieldName = lobby.LocalSelectedAirfield }), true);
        }

        private void HandlePeerDisconnected(ulong peerId)
        {
            Plugin.Log.LogInfo($"NetworkManager: Peer {peerId} disconnected");
            _remotePeerId = 0;
            CleanupRemoteAircraft();
            OnPeerDisconnected?.Invoke(peerId);
            
            // Handle lobby disconnection
            Plugin.Instance?.Lobby?.HandlePlayerLeft(peerId);
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
                ProcessPacket(peerId, packetType, payload);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"NetworkManager: Packet error: {ex.Message}");
            }
        }

        private void ProcessPacket(ulong peerId, PacketType type, byte[] payload)
        {
            switch (type)
            {
                case PacketType.AircraftState:
                    if (payload != null)
                    {
                        AircraftStatesReceived++;
                        var state = PacketSerializer.DeserializeAircraftState(payload);
                        HandleRemoteAircraftState(peerId, state);
                    }
                    break;
                    
                case PacketType.DamageDealt:
                    if (payload != null)
                    {
                        var damagePacket = PacketSerializer.DeserializeDamage(payload);
                        Plugin.Log.LogInfo($"[NetworkManager] Received damage packet: {damagePacket.Damage} damage");
                        Patches.DamagePatches.HandleReceivedDamage(damagePacket);
                    }
                    break;
                    
                case PacketType.AircraftDestroyed:
                    Plugin.Log.LogInfo($"[NetworkManager] Remote aircraft destroyed!");
                    HandleRemoteAircraftDestroyed(peerId);
                    break;
                    
                case PacketType.MissileLaunch:
                    if (payload != null)
                    {
                        var missilePacket = PacketSerializer.DeserializeMissileLaunch(payload);
                        Plugin.Log.LogInfo($"[NetworkManager] Received missile launch: {missilePacket.MissileType}");
                        Patches.WeaponPatches.HandleMissileLaunch(missilePacket);
                    }
                    break;
                    
                case PacketType.RadarLock:
                case PacketType.RadarLockLost:
                    if (payload != null)
                    {
                        var lockPacket = PacketSerializer.DeserializeRadarLock(payload);
                        Plugin.Log.LogInfo($"[NetworkManager] Received radar lock change: {lockPacket.IsLocked}");
                        Patches.WeaponPatches.HandleRadarLock(lockPacket);
                    }
                    break;
                    
                case PacketType.AircraftChanged:
                case PacketType.Respawned:
                    if (payload != null)
                    {
                        var changedPacket = PacketSerializer.DeserializeAircraftChanged(payload);
                        Plugin.Log.LogInfo($"[NetworkManager] Remote aircraft changed/respawned: {changedPacket.AircraftType}");
                        HandleRemoteAircraftRespawn(peerId, changedPacket);
                    }
                    break;
                
                case PacketType.ProjectileImpact:
                    if (payload != null)
                    {
                        var impactPacket = PacketSerializer.DeserializeProjectileImpact(payload);
                        Plugin.Log.LogInfo($"[NetworkManager] Received impact FX packet: {impactPacket.WeaponName} at ({impactPacket.ImpactPosX:F1},{impactPacket.ImpactPosY:F1},{impactPacket.ImpactPosZ:F1})");
                        Patches.DamagePatches.HandleReceivedImpact(impactPacket);
                    }
                    break;
                
                #region Lobby Packets
                
                case PacketType.LobbyState:
                    if (payload != null)
                    {
                        var lobbyState = PacketSerializer.DeserializeLobbyState(payload);
                        Plugin.Instance?.Lobby?.UpdateFromLobbyState(lobbyState);
                    }
                    break;
                    
                case PacketType.LobbyPlayerJoined:
                    if (payload != null)
                    {
                        var joined = PacketSerializer.DeserializeLobbyPlayerJoined(payload);
                        Plugin.Instance?.Lobby?.HandlePlayerJoined(joined.PeerId, joined.PlayerName);
                    }
                    break;
                    
                case PacketType.LobbyPlayerLeft:
                    if (payload != null)
                    {
                        var left = PacketSerializer.DeserializeLobbyPlayerLeft(payload);
                        Plugin.Instance?.Lobby?.HandlePlayerLeft(left.PeerId);
                    }
                    break;
                    
                case PacketType.LobbyPlayerReady:
                    if (payload != null)
                    {
                        var ready = PacketSerializer.DeserializeLobbyPlayerReady(payload);
                        Plugin.Instance?.Lobby?.HandlePlayerReady(ready.PeerId, ready.IsReady);
                    }
                    break;
                    
                case PacketType.LobbyAirfieldSelect:
                    if (payload != null)
                    {
                        var airfield = PacketSerializer.DeserializeLobbyAirfieldSelect(payload);
                        Plugin.Instance?.Lobby?.HandlePlayerAirfieldSelect(airfield.PeerId, airfield.AirfieldName);
                    }
                    break;
                    
                case PacketType.LobbySpawnSettings:
                    if (payload != null)
                    {
                        var settings = PacketSerializer.DeserializeLobbySpawnSettings(payload);
                        Plugin.Instance?.Lobby?.SetSpawnSettings(settings.SpawnType, settings.MapName);
                    }
                    break;
                    
                case PacketType.LobbyStartGame:
                    if (payload != null)
                    {
                        var startGame = PacketSerializer.DeserializeLobbyStartGame(payload);
                        Plugin.Log?.LogInfo($"[NetworkManager] Received start game: {startGame.MapName}");
                        Plugin.Instance?.Lobby?.HandleGameStart(startGame.MapName, startGame.SpawnType);
                    }
                    break;
                    
                case PacketType.LobbyLoadingComplete:
                    if (payload != null)
                    {
                        var loaded = PacketSerializer.DeserializeLobbyLoadingComplete(payload);
                        Plugin.Instance?.Lobby?.HandlePlayerLoaded(loaded.PeerId);
                    }
                    break;
                    
                case PacketType.LobbySpawnPlayers:
                    Plugin.Log?.LogInfo("[NetworkManager] Received spawn players signal");
                    Plugin.Instance?.Lobby?.HandleSpawnPlayers();
                    break;
                    
                case PacketType.LobbyRespawnRequest:
                    if (payload != null)
                    {
                        var respawn = PacketSerializer.DeserializeLobbyRespawnRequest(payload);
                        Plugin.Log?.LogInfo($"[NetworkManager] Player {respawn.PeerId} requesting respawn");
                        // Host can validate and allow respawn
                    }
                    break;

                case PacketType.LobbyWelcome:
                    if (payload != null)
                    {
                        var welcome = PacketSerializer.DeserializeLobbyWelcome(payload);
                        HandleLobbyWelcome(welcome);
                    }
                    break;
                    
                #endregion
                    
                default:
                    if (LogHelper.IsEnabled(LogCategory.Network) &&
                        LogHelper.ShouldLogInterval("NetworkManager.UnhandledPacket", LogHelper.DefaultIntervalSeconds))
                    {
                        LogHelper.Info(LogCategory.Network, $"[NetworkManager] Unhandled packet type: {type}");
                    }
                    break;
            }
        }

        private void HandleRemoteAircraftState(ulong peerId, AircraftStatePacket state)
        {
            try
            {
                _lastRemoteUpdateTime = Time.time;
                
                // State contains ABSOLUTE world coordinates
                // Convert to local coordinates using our FloatingOrigin offset
                var absolutePos = new Vector3d(state.PosX, state.PosY, state.PosZ);
                Vector3 localPos = FloatingOriginHelper.AbsoluteToLocal(absolutePos);
                Quaternion rotation = new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW);
                Vector3 velocity = new Vector3(state.VelX, state.VelY, state.VelZ);
                Vector3 angularVelocity = new Vector3(state.AngVelX, state.AngVelY, state.AngVelZ);
                
                // Store velocity for UI display
                _lastVelocity = velocity;
                
                // Add to interpolation buffer
                _interpolationBuffer.AddSnapshot(localPos, rotation, velocity, angularVelocity, state.Timestamp);
                
                // CRITICAL FIX: Don't create a new aircraft if we're waiting for respawn
                // Only the Respawned packet should trigger recreation
                if (_remoteAircraftNeedsRespawn)
                {
                    if (LogHelper.IsEnabled(LogCategory.Network) &&
                        LogHelper.ShouldLogInterval("NetworkManager.RemoteNeedsRespawn", LogHelper.DefaultIntervalSeconds))
                    {
                        LogHelper.Info(LogCategory.Network,
                            $"[NetworkManager] Remote needs respawn, ignoring states for {Time.time - _remoteDestroyedTime:F1}s");
                    }
                    
                    // Check for timeout - allow recreation if we've waited too long
                    if (Time.time - _remoteDestroyedTime > RESPAWN_TIMEOUT)
                    {
                        Plugin.Log.LogInfo("[NetworkManager] Respawn timeout reached, allowing recreation from state packets");
                        _remoteAircraftNeedsRespawn = false;
                        CleanupRemoteAircraft();
                        _interpolationBuffer.Clear();
                    }
                    else
                    {
                        // Just update position for when they do respawn, but don't create
                        _displayPosition = localPos;
                        _displayRotation = rotation;
                        return;
                    }
                }
                
                if (_remoteAircraftObject == null)
                {
                    CreateRemoteAircraft(peerId);
                    // Initialize display position
                    _displayPosition = localPos;
                    _displayRotation = rotation;
                }
                
                if (LogHelper.IsEnabled(LogCategory.Network) &&
                    LogHelper.ShouldLogInterval("NetworkManager.RemoteState", LogHelper.DefaultIntervalSeconds))
                {
                    LogHelper.Info(LogCategory.Network,
                        $"[NetworkManager] Remote state pos={localPos} vel={velocity} ts={state.Timestamp:F2} buffer={BufferStatus}");
                }
                
                // Update visual state on controller (gear, flaps, afterburner, control surfaces)
                if (_remoteController != null && !_remoteController.IsDestroyed)
                {
                    _remoteController.UpdateFromState(state);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] HandleRemoteAircraftState error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle remote aircraft being destroyed - hide/destroy the clone
        /// </summary>
        private void HandleRemoteAircraftDestroyed(ulong peerId)
        {
            try
            {
                // Skip if already marked as needing respawn
                if (_remoteAircraftNeedsRespawn)
                {
                    Plugin.Log.LogInfo("[NetworkManager] Remote aircraft already marked as destroyed, ignoring duplicate");
                    return;
                }
                
                if (_remoteController != null)
                {
                    _remoteController.OnDestroyed();
                }
                
                // Mark as needing respawn and record time
                _remoteAircraftNeedsRespawn = true;
                _remoteDestroyedTime = Time.time;
                
                Plugin.Log.LogInfo("[NetworkManager] Remote aircraft marked as destroyed, waiting for respawn packet");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] HandleRemoteAircraftDestroyed error: {ex.Message}");
            }
        }
        
        // Track if remote needs respawn
        private bool _remoteAircraftNeedsRespawn = false;
        private float _remoteDestroyedTime = 0f;
        private const float RESPAWN_TIMEOUT = 30f; // Allow recreation after 30 seconds if no respawn packet

        /// <summary>
        /// Handle remote aircraft respawning - recreate the clone
        /// </summary>
        private void HandleRemoteAircraftRespawn(ulong peerId, AircraftChangedPacket packet)
        {
            try
            {
                Plugin.Log.LogInfo($"[NetworkManager] Remote player respawned with: {packet.AircraftType}");
                
                // Clean up old clone completely
                CleanupRemoteAircraft();
                
                // Reset interpolation buffer
                _interpolationBuffer.Clear();
                
                // Clear respawn flag - next state packet will create new aircraft
                _remoteAircraftNeedsRespawn = false;
                _remoteDestroyedTime = 0f;
                
                Plugin.Log.LogInfo("[NetworkManager] Ready for new aircraft creation on next state packet");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] HandleRemoteAircraftRespawn error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send notification that local aircraft has respawned
        /// Call this from FlightGamePatches when detecting respawn
        /// </summary>
        public void SendAircraftRespawnNotification(string aircraftType)
        {
            try
            {
                var packet = new AircraftChangedPacket
                {
                    PlayerId = IsHost ? 1UL : 2UL,
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

        /// <summary>
        /// Send notification that local aircraft was destroyed
        /// </summary>
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
        
        #region Lobby Packet Sending
        
        /// <summary>
        /// Send lobby state broadcast (host only)
        /// </summary>
        public void SendLobbyState(LobbyStatePacket packet)
        {
            try
            {
                var data = PacketSerializer.SerializeLobbyState(packet);
                SendPacket(PacketType.LobbyState, data, reliable: true);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[NetworkManager] SendLobbyState error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send player ready state
        /// </summary>
        public void SendLobbyPlayerReady(bool isReady)
        {
            try
            {
                var packet = new LobbyPlayerReadyPacket { PeerId = LocalPeerId, IsReady = isReady };
                var data = PacketSerializer.SerializeLobbyPlayerReady(packet);
                SendPacket(PacketType.LobbyPlayerReady, data, reliable: true);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[NetworkManager] SendLobbyPlayerReady error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send airfield selection
        /// </summary>
        public void SendLobbyAirfieldSelect(string airfieldName)
        {
            try
            {
                var packet = new LobbyAirfieldSelectPacket { PeerId = LocalPeerId, AirfieldName = airfieldName };
                var data = PacketSerializer.SerializeLobbyAirfieldSelect(packet);
                SendPacket(PacketType.LobbyAirfieldSelect, data, reliable: true);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[NetworkManager] SendLobbyAirfieldSelect error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send spawn settings (host only)
        /// </summary>
        public void SendLobbySpawnSettings(LobbySpawnType spawnType, string mapName)
        {
            try
            {
                var packet = new LobbySpawnSettingsPacket { SpawnType = spawnType, MapName = mapName ?? "ActionIsland" };
                var data = PacketSerializer.SerializeLobbySpawnSettings(packet);
                SendPacket(PacketType.LobbySpawnSettings, data, reliable: true);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[NetworkManager] SendLobbySpawnSettings error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send start game (host only)
        /// </summary>
        public void SendLobbyStartGame(string mapName, LobbySpawnType spawnType)
        {
            try
            {
                var packet = new LobbyStartGamePacket { MapName = mapName ?? "ActionIsland", SpawnType = spawnType };
                var data = PacketSerializer.SerializeLobbyStartGame(packet);
                SendPacket(PacketType.LobbyStartGame, data, reliable: true);
                Plugin.Log?.LogInfo($"[NetworkManager] Sent lobby start game: {mapName}, {spawnType}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[NetworkManager] SendLobbyStartGame error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send loading complete notification
        /// </summary>
        public void SendLobbyLoadingComplete()
        {
            try
            {
                var packet = new LobbyLoadingCompletePacket { PeerId = LocalPeerId };
                var data = PacketSerializer.SerializeLobbyLoadingComplete(packet);
                SendPacket(PacketType.LobbyLoadingComplete, data, reliable: true);
                Plugin.Log?.LogInfo("[NetworkManager] Sent loading complete");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[NetworkManager] SendLobbyLoadingComplete error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send spawn players signal (host only)
        /// </summary>
        public void SendLobbySpawnPlayers()
        {
            try
            {
                var packet = new LobbySpawnPlayersPacket { Timestamp = Time.time };
                var data = PacketSerializer.SerializeLobbySpawnPlayers(packet);
                SendPacket(PacketType.LobbySpawnPlayers, data, reliable: true);
                Plugin.Log?.LogInfo("[NetworkManager] Sent spawn players signal");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[NetworkManager] SendLobbySpawnPlayers error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send respawn request
        /// </summary>
        public void SendLobbyRespawnRequest()
        {
            try
            {
                var packet = new LobbyRespawnRequestPacket { PeerId = LocalPeerId };
                var data = PacketSerializer.SerializeLobbyRespawnRequest(packet);
                SendPacket(PacketType.LobbyRespawnRequest, data, reliable: true);
                Plugin.Log?.LogInfo("[NetworkManager] Sent respawn request");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[NetworkManager] SendLobbyRespawnRequest error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Broadcast lobby state to all clients (host calls this periodically)
        /// </summary>
        public void BroadcastLobbyState()
        {
            var lobby = Plugin.Instance?.Lobby;
            if (lobby == null || !lobby.IsHost) return;
            
            SendLobbyState(lobby.GetLobbyStatePacket());
        }
        
        #endregion

        private void CreateRemoteAircraft(ulong peerId)
        {
            try
            {
                Plugin.Log.LogInfo($"[NetworkManager] Creating remote aircraft for peer {peerId}...");
                
                // First, try to clone a real aircraft from the scene
                _remoteAircraftObject = TryCloneRealAircraft(peerId);
                
                if (_remoteAircraftObject != null)
                {
                    _usingRealAircraft = true;
                    _needsAircraftCloneRetry = false;
                    Plugin.Log.LogInfo("[NetworkManager] Successfully cloned real aircraft!");
                }
                else
                {
                    // Fallback to marker placeholder
                    _usingRealAircraft = false;
                    _needsAircraftCloneRetry = true; // Will retry when local aircraft becomes available
                    _remoteAircraftObject = CreateFallbackMarker(peerId);
                    Plugin.Log.LogInfo("[NetworkManager] Using fallback marker (aircraft clone failed - will retry)");
                }
                
                _remoteAircraftObject.transform.position = _displayPosition;
                
                // Always add screen-space marker for visibility at distance
                _remoteAircraftObject.AddComponent<ScreenSpaceMarker>();
                
                Plugin.Log.LogInfo($"[NetworkManager] Remote aircraft created at {_displayPosition}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] Failed to create remote aircraft: {ex}");
            }
        }
        
        /// <summary>
        /// Attempt to clone a real aircraft from the scene
        /// </summary>
        private GameObject TryCloneRealAircraft(ulong peerId)
        {
            try
            {
                // Find the player's aircraft (UniAircraft component)
                var sourceAircraft = FindLocalAircraftForCloning();
                if (sourceAircraft == null)
                {
                    Plugin.Log.LogWarning("[NetworkManager] No source aircraft found to clone");
                    return null;
                }
                
                Plugin.Log.LogInfo($"[NetworkManager] Cloning aircraft from: {sourceAircraft.name}");
                
                // Check if source has Gun2 before cloning (using reflection since FireControl is in game assembly)
                var fireControlType = Type.GetType("Falcon.Weapons.FireControl, Assembly-CSharp");
                if (fireControlType != null)
                {
                    var sourceFireControl = sourceAircraft.GetComponentInChildren(fireControlType);
                    if (sourceFireControl != null)
                    {
                        var gunField = fireControlType.GetField("Gun", BindingFlags.Public | BindingFlags.Instance);
                        var gunValue = gunField?.GetValue(sourceFireControl);
                        Plugin.Log.LogInfo($"[NetworkManager] Source FireControl found, Gun={gunValue != null}");
                        if (gunValue != null)
                        {
                            Plugin.Log.LogInfo($"[NetworkManager] Source Gun type: {gunValue.GetType().FullName}");
                        }
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[NetworkManager] Source aircraft has no FireControl!");
                    }
                }
                
                // Clone the entire GameObject hierarchy
                var clone = GameObject.Instantiate(sourceAircraft);
                clone.name = $"MP_RemoteAircraft_{peerId}";
                
                // Check if clone has Gun2 immediately after cloning
                if (fireControlType != null)
                {
                    var cloneFireControl = clone.GetComponentInChildren(fireControlType);
                    if (cloneFireControl != null)
                    {
                        var gunField = fireControlType.GetField("Gun", BindingFlags.Public | BindingFlags.Instance);
                        var gunValue = gunField?.GetValue(cloneFireControl);
                        Plugin.Log.LogInfo($"[NetworkManager] Clone FireControl found, Gun={gunValue != null}");
                    }
                }
                
                // Disable physics and gameplay components
                DisableGameplayComponents(clone);
                
                // Add our controller and cache it
                var controller = clone.AddComponent<RemoteAircraftController>();
                controller.PlayerId = peerId;
                controller.Initialize();
                _remoteController = controller;
                
                return clone;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] Aircraft clone failed: {ex}");
                return null;
            }
        }
        
        /// <summary>
        /// Find the local player's aircraft to use as a clone source
        /// </summary>
        private GameObject FindLocalAircraftForCloning()
        {
            try
            {
                // Try to find UniAircraft components
                var aircraftType = Type.GetType("Falcon.UniversalAircraft.UniAircraft, Assembly-CSharp");
                if (aircraftType != null)
                {
                    var aircrafts = GameObject.FindObjectsByType(aircraftType, FindObjectsSortMode.None) as Component[];
                    if (aircrafts != null && aircrafts.Length > 0)
                    {
                        foreach (var aircraft in aircrafts)
                        {
                            // CRITICAL: Don't clone a remote aircraft!
                            if (aircraft.gameObject.GetComponent<RemoteAircraftController>() != null) continue;
                            if (aircraft.gameObject.name.Contains("MP_Remote")) continue;

                            // Return the first valid local aircraft
                            return aircraft.gameObject;
                        }
                    }
                }
                
                // Fallback: search by name patterns
                var allObjects = GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None);
                foreach (var t in allObjects)
                {
                    string name = t.gameObject.name.ToLower();
                    
                    // Skip remote objects
                    if (name.Contains("mp_remote")) continue;
                    
                    if (name.Contains("aircraft") || name.Contains("plane") || name.Contains("jet") || 
                        name.Contains("f-16") || name.Contains("f16") || name.Contains("falcon"))
                    {
                        // Check if it has a Rigidbody (sign of a physical aircraft)
                        if (t.GetComponent<Rigidbody>() != null)
                        {
                            Plugin.Log.LogInfo($"[NetworkManager] Found aircraft by name: {t.gameObject.name}");
                            return t.gameObject;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] Error finding aircraft: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Disable physics, AI, and input components on cloned aircraft
        /// </summary>
        private void DisableGameplayComponents(GameObject aircraft)
        {
            try
            {
                // Diagnostic: Log all child GameObjects to find canopy
                LogAircraftHierarchy(aircraft.transform, 0);
                
                // Disable Rigidbody (make kinematic)
                var rb = aircraft.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.useGravity = false;
                    Plugin.Log.LogInfo("[NetworkManager] Disabled Rigidbody physics");
                }
                
                // Re-enable some colliders for missile/gun hit detection
                // Only keep the main body collider, disable wheel/gear colliders
                var colliders = aircraft.GetComponentsInChildren<Collider>(true);
                int enabledColliders = 0;
                foreach (var col in colliders)
                {
                    string colName = col.gameObject.name.ToLower();
                    // Keep body/fuselage colliders for hit detection, disable gear/wheel colliders
                    if (colName.Contains("wheel") || colName.Contains("gear") || colName.Contains("tire"))
                    {
                        col.enabled = false;
                    }
                    else
                    {
                        col.enabled = true;
                        enabledColliders++;
                    }
                }
                Plugin.Log.LogInfo($"[NetworkManager] Enabled {enabledColliders}/{colliders.Length} colliders for hit detection");
                
                // Disable known gameplay components by type name
                // NOTE: We keep Target and Damageable ENABLED for combat!
                // NOTE: We keep FireControl ENABLED so Gun2 gets initialized!
                //       Our FireControlPatches.Update_Prefix handles remote aircraft firing
                string[] componentsToDisable = new string[]
                {
                    "UniPilot", "FlightInput", "WeaponInput",
                    "UniAircraftDamage", "UniFlight",
                    "StickAndRudder", "VehicleLauncher"
                };
                
                // Components to keep enabled for combat
                // FireControl is kept so Gun2 gets initialized - our patch handles remote firing
                string[] componentsToKeep = new string[]
                {
                    "Target", "Damageable", "Signature", "FireControl"
                };
                
                var allComponents = aircraft.GetComponentsInChildren<MonoBehaviour>(true);
                int disabledCount = 0;
                
                foreach (var comp in allComponents)
                {
                    if (comp == null) continue;
                    
                    string typeName = comp.GetType().Name;
                    
                    // Skip components we want to keep for combat
                    bool shouldKeep = false;
                    foreach (var toKeep in componentsToKeep)
                    {
                        if (typeName.Contains(toKeep))
                        {
                            shouldKeep = true;
                            break;
                        }
                    }
                    if (shouldKeep) continue;
                    
                    // Disable if it's in our list
                    foreach (var toDisable in componentsToDisable)
                    {
                        if (typeName.Contains(toDisable))
                        {
                            comp.enabled = false;
                            disabledCount++;
                            break;
                        }
                    }
                    
                    // Also disable anything that looks like AI or input
                    if (typeName.Contains("AI") || typeName.Contains("Input") || 
                        typeName.Contains("Control") || typeName.Contains("Pilot"))
                    {
                        comp.enabled = false;
                        disabledCount++;
                    }
                }
                
                Plugin.Log.LogInfo($"[NetworkManager] Disabled {disabledCount} gameplay components");
                
                // Configure targeting system for remote aircraft
                ConfigureTargetingForRemoteAircraft(aircraft);
                
                // Ensure canopy-related objects are visible
                EnsureCanopyVisible(aircraft);
                
                // Keep enabled: Renderers, AudioSources, ParticleSystems, Animators
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] Error disabling components: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Configure Target component so remote aircraft can be locked and targeted by missiles/radar
        /// CRITICAL: Must set Faction string for TargetManagement.RegisterTarget() to work!
        /// </summary>
        private void ConfigureTargetingForRemoteAircraft(GameObject aircraft)
        {
            try
            {
                // Find Target component (Falcon.Targeting.Target)
                var targetType = Type.GetType("Falcon.Targeting.Target, Assembly-CSharp");
                if (targetType == null)
                {
                    Plugin.Log.LogWarning("[NetworkManager] Target type not found - targeting won't work");
                    return;
                }
                
                var target = aircraft.GetComponentInChildren(targetType) as MonoBehaviour;
                if (target == null)
                {
                    Plugin.Log.LogWarning("[NetworkManager] No Target component found on aircraft - targeting won't work");
                    return;
                }
                
                Plugin.Log.LogInfo($"[NetworkManager] Found Target component on: {target.gameObject.name}");
                
                // Get reflection info for Target fields
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                
                // Get Coalition enum type
                var coalitionEnumType = Type.GetType("Falcon.Factions.Coalition, Assembly-CSharp");
                if (coalitionEnumType == null)
                {
                    Plugin.Log.LogWarning("[NetworkManager] Coalition enum type not found");
                    return;
                }
                
                // Coalition.Red = 1 (enemy)
                object redCoalition = Enum.ToObject(coalitionEnumType, 1);
                
                // Set TargetType to Fighter (for aircraft)
                // TargetType enum: Fighter=0, Attacker=1, Bomber=2, Rotary=3, etc.
                var targetTypeField = targetType.GetField("TargetType", flags);
                if (targetTypeField != null)
                {
                    var targetTypeEnumType = targetTypeField.FieldType;
                    object fighterType = Enum.ToObject(targetTypeEnumType, 0); // Fighter = 0
                    targetTypeField.SetValue(target, fighterType);
                    Plugin.Log.LogInfo("[NetworkManager] Set TargetType to Fighter");
                }
                
                // CRITICAL: Set Faction string - without this, TargetManagement.RegisterTarget returns immediately!
                // The Faction property has a private setter, so we need to use the backing field
                var factionBackingField = targetType.GetField("<Faction>k__BackingField", flags);
                if (factionBackingField != null)
                {
                    factionBackingField.SetValue(target, "Enemy"); // Must be non-empty!
                    Plugin.Log.LogInfo("[NetworkManager] Set Faction to 'Enemy'");
                }
                else
                {
                    // Try direct field
                    var factionField = targetType.GetField("Faction", flags);
                    if (factionField != null)
                    {
                        factionField.SetValue(target, "Enemy");
                        Plugin.Log.LogInfo("[NetworkManager] Set Faction field to 'Enemy'");
                    }
                }
                
                // Set DefaultCoalition to RED (enemy) - this is used on Awake
                var defaultCoalitionField = targetType.GetField("DefaultCoalition", flags);
                if (defaultCoalitionField != null)
                {
                    defaultCoalitionField.SetValue(target, redCoalition);
                    Plugin.Log.LogInfo("[NetworkManager] Set DefaultCoalition to Red");
                }
                
                // Set Coalition property directly via backing field
                var coalitionBackingField = targetType.GetField("<Coalition>k__BackingField", flags);
                if (coalitionBackingField != null)
                {
                    coalitionBackingField.SetValue(target, redCoalition);
                    Plugin.Log.LogInfo("[NetworkManager] Set Coalition to Red (enemy)");
                }
                
                // Set IsDestroyed = false, IsCriticalHP = false
                var isDestroyedField = targetType.GetField("IsDestroyed", flags);
                var isCriticalField = targetType.GetField("IsCriticalHP", flags);
                if (isDestroyedField != null) isDestroyedField.SetValue(target, false);
                if (isCriticalField != null) isCriticalField.SetValue(target, false);
                
                // Configure Signature for IR/Radar detection
                // CRITICAL: Must populate RCSCurve.Points for radar to detect!
                var signatureField = targetType.GetField("Signature", flags);
                if (signatureField != null)
                {
                    var signature = signatureField.GetValue(target);
                    if (signature != null)
                    {
                        var sigType = signature.GetType();
                        
                        // Set engine running for IR signature
                        var isEngineRunningField = sigType.GetField("IsEngineRunning", flags);
                        if (isEngineRunningField != null)
                        {
                            isEngineRunningField.SetValue(signature, true);
                        }
                        
                        // Set throttle for IR signature  
                        var throttleField = sigType.GetField("EngineThrottle", flags);
                        if (throttleField != null)
                        {
                            throttleField.SetValue(signature, 0.8f);
                        }
                        
                        // CRITICAL FIX: Configure RCSCurve for radar detection
                        // Radar.IsDetectable() calls Signature.GetRadar(angle) which samples RCSCurve
                        // If RCSCurve.Points is empty, GetRadar returns 0 and target is invisible!
                        var rcsCurveProp = sigType.GetProperty("RCSCurve", flags);
                        if (rcsCurveProp != null)
                        {
                            var rcsCurve = rcsCurveProp.GetValue(signature);
                            if (rcsCurve != null)
                            {
                                var curveType = rcsCurve.GetType();
                                // InterpolatedCurve.Points is a PUBLIC field
                                var pointsField = curveType.GetField("Points", BindingFlags.Public | BindingFlags.Instance);
                                if (pointsField != null)
                                {
                                    var points = pointsField.GetValue(rcsCurve) as System.Collections.IList;
                                    if (points != null)
                                    {
                                        int originalCount = points.Count;
                                        
                                        // Clear any existing points and add new ones for fighter aircraft
                                        // These give good radar visibility from all angles
                                        points.Clear();
                                        
                                        // Points are (angle, RCS value) - angle from behind (0=tail, 180=nose)
                                        // Fighter typical values: nose ~3-5 sqm, side ~8-15 sqm, tail ~5-8 sqm
                                        var tupleType = typeof(ValueTuple<float, float>);
                                        points.Add(Activator.CreateInstance(tupleType, 0f, 5f));    // Tail-on
                                        points.Add(Activator.CreateInstance(tupleType, 45f, 8f));   // Rear quarter
                                        points.Add(Activator.CreateInstance(tupleType, 90f, 12f));  // Beam (side)
                                        points.Add(Activator.CreateInstance(tupleType, 135f, 8f));  // Front quarter
                                        points.Add(Activator.CreateInstance(tupleType, 180f, 5f));  // Nose-on
                                        
                                        Plugin.Log.LogInfo($"[NetworkManager] Configured RCSCurve with {points.Count} points (was {originalCount}) for radar detection");
                                    }
                                }
                                else
                                {
                                    Plugin.Log.LogWarning("[NetworkManager] RCSCurve.Points field not found");
                                }
                            }
                        }
                        else
                        {
                            Plugin.Log.LogWarning("[NetworkManager] Signature.RCSCurve property not found");
                        }
                        
                        // Also configure IRCurve for IR missiles
                        var irCurveProp = sigType.GetProperty("IRCurve", flags);
                        if (irCurveProp != null)
                        {
                            var irCurve = irCurveProp.GetValue(signature);
                            if (irCurve != null)
                            {
                                var curveType = irCurve.GetType();
                                var pointsField = curveType.GetField("Points", BindingFlags.Public | BindingFlags.Instance);
                                if (pointsField != null)
                                {
                                    var points = pointsField.GetValue(irCurve) as System.Collections.IList;
                                    if (points != null && points.Count == 0)
                                    {
                                        // Add IR signature points - higher from behind due to exhaust
                                        var tupleType = typeof(ValueTuple<float, float>);
                                        points.Add(Activator.CreateInstance(tupleType, 0f, 10f));   // Tail-on (hot exhaust)
                                        points.Add(Activator.CreateInstance(tupleType, 45f, 6f));   // Rear quarter
                                        points.Add(Activator.CreateInstance(tupleType, 90f, 3f));   // Beam
                                        points.Add(Activator.CreateInstance(tupleType, 135f, 2f));  // Front quarter
                                        points.Add(Activator.CreateInstance(tupleType, 180f, 1f));  // Nose-on
                                        
                                        Plugin.Log.LogInfo($"[NetworkManager] Configured IRCurve with {points.Count} points for IR detection");
                                    }
                                }
                            }
                        }
                        
                        Plugin.Log.LogInfo("[NetworkManager] Configured Signature for IR/Radar detection");
                    }
                }
                
                // CRITICAL: Force re-registration with TargetManagement
                // Disable and re-enable to trigger OnEnable which calls RegisterTarget
                // This MUST happen AFTER setting Faction, otherwise RegisterTarget will fail
                target.enabled = false;
                target.enabled = true;
                
                // Verify registration worked by checking if target is in the static lists
                var targetManagementType = Type.GetType("Falcon.Targeting.TargetManagement, Assembly-CSharp");
                if (targetManagementType != null)
                {
                    var allTargetsField = targetManagementType.GetField("AllTargets", BindingFlags.Public | BindingFlags.Static);
                    if (allTargetsField != null)
                    {
                        var allTargets = allTargetsField.GetValue(null) as System.Collections.IList;
                        if (allTargets != null)
                        {
                            bool isRegistered = allTargets.Contains(target);
                            Plugin.Log.LogInfo($"[NetworkManager] Target registered in TargetManagement.AllTargets: {isRegistered} (count: {allTargets.Count})");
                        }
                    }
                }
                
                Plugin.Log.LogInfo("[NetworkManager] Target component configured for combat - aircraft should now be lockable");
                
                // Configure Viewable component for radar/map display
                ConfigureViewableForRemoteAircraft(aircraft);
                
                // Configure Damageable component
                ConfigureDamageableForRemoteAircraft(aircraft);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] ConfigureTargeting error: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Configure Damageable component so remote aircraft can take damage
        /// </summary>
        private void ConfigureDamageableForRemoteAircraft(GameObject aircraft)
        {
            try
            {
                var damageableType = Type.GetType("Falcon.Damage.Damageable, Assembly-CSharp");
                if (damageableType == null)
                {
                    Plugin.Log.LogWarning("[NetworkManager] Damageable type not found");
                    return;
                }
                
                var damageable = aircraft.GetComponentInChildren(damageableType) as MonoBehaviour;
                if (damageable == null)
                {
                    Plugin.Log.LogWarning("[NetworkManager] No Damageable component found on aircraft");
                    return;
                }
                
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                
                // Ensure Damageable is enabled
                damageable.enabled = true;
                
                // Set IsInvincible to false
                var invincibleField = damageableType.GetField("IsInvincible", flags);
                if (invincibleField != null)
                {
                    invincibleField.SetValue(damageable, false);
                }
                
                // Set DifficultyMultiplier to 1.0
                var difficultyField = damageableType.GetField("DifficultyMultiplier", flags);
                if (difficultyField != null)
                {
                    difficultyField.SetValue(damageable, 1.0f);
                }
                
                // Ensure IsDestroyed is false
                var isDestroyedProp = damageableType.GetProperty("IsDestroyed", flags);
                if (isDestroyedProp != null && isDestroyedProp.CanWrite)
                {
                    isDestroyedProp.SetValue(damageable, false);
                }
                else
                {
                    var isDestroyedBackingField = damageableType.GetField("<IsDestroyed>k__BackingField", flags);
                    if (isDestroyedBackingField != null)
                    {
                        isDestroyedBackingField.SetValue(damageable, false);
                    }
                }
                
                // Ensure hitpoints are set
                var maxHPField = damageableType.GetField("maxHP", flags);
                var hitPointsProp = damageableType.GetProperty("HitPoints", flags);
                if (maxHPField != null)
                {
                    int maxHP = (int)maxHPField.GetValue(damageable);
                    if (maxHP <= 0) maxHP = 100;
                    if (hitPointsProp != null)
                    {
                        hitPointsProp.SetValue(damageable, maxHP);
                    }
                    Plugin.Log.LogInfo($"[NetworkManager] Damageable configured with {maxHP} HP");
                }
                
                Plugin.Log.LogInfo("[NetworkManager] Damageable component configured for combat");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] ConfigureDamageable error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Configure Viewable component so remote aircraft appears on radar and map
        /// </summary>
        private void ConfigureViewableForRemoteAircraft(GameObject aircraft)
        {
            try
            {
                // Find Viewable component (Falcon.Viewable)
                var viewableType = Type.GetType("Falcon.Viewable, Assembly-CSharp");
                if (viewableType == null)
                {
                    Plugin.Log.LogWarning("[NetworkManager] Viewable type not found - radar/map won't work");
                    return;
                }
                
                var viewable = aircraft.GetComponentInChildren(viewableType) as MonoBehaviour;
                if (viewable == null)
                {
                    Plugin.Log.LogWarning("[NetworkManager] No Viewable component found on aircraft - radar/map won't work");
                    return;
                }
                
                Plugin.Log.LogInfo($"[NetworkManager] Found Viewable component on: {viewable.gameObject.name}");
                
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                
                // Set viewType to Aircraft (0) for radar display
                // ViewType enum: Aircraft=0, Munition=1, Vehicle=2, Ship=3, Structure=4, Pilot=5
                var viewTypeField = viewableType.GetField("viewType", flags);
                if (viewTypeField != null)
                {
                    viewTypeField.SetValue(viewable, 0); // ViewType.Aircraft
                    Plugin.Log.LogInfo("[NetworkManager] Set Viewable viewType to Aircraft");
                }
                
                // Set AutoPopulateStats to true so it updates position/velocity from rigidbody
                var autoPopulateField = viewableType.GetField("AutoPopulateStats", flags);
                if (autoPopulateField != null)
                {
                    autoPopulateField.SetValue(viewable, true);
                    Plugin.Log.LogInfo("[NetworkManager] Set Viewable AutoPopulateStats to true");
                }
                
                // Ensure Viewable is enabled
                viewable.enabled = true;
                
                // Force re-registration with ViewType lists by toggling enabled
                viewable.enabled = false;
                viewable.enabled = true;
                
                Plugin.Log.LogInfo("[NetworkManager] Viewable component configured - aircraft should appear on radar/map");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] ConfigureViewable error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Log aircraft hierarchy to find canopy and other parts
        /// </summary>
        private void LogAircraftHierarchy(Transform parent, int depth)
        {
            if (depth > 6) return; // Increased depth to find nested canopy
            
            string indent = new string(' ', depth * 2);
            var renderer = parent.GetComponent<Renderer>();
            var meshFilter = parent.GetComponent<MeshFilter>();
            bool hasVisual = renderer != null || meshFilter != null;
            bool isActive = parent.gameObject.activeSelf;
            
            string name = parent.name.ToLower();
            bool isCanopyRelated = name.Contains("canopy") || name.Contains("glass") || 
                                   name.Contains("cockpit") || name.Contains("window") ||
                                   name.Contains("bubble") || name.Contains("hood") ||
                                   name.Contains("hud") || name.Contains("transparency");
            
            // Log all objects at depth 0-2, and canopy-related or visual objects deeper
            if (depth <= 2 || hasVisual || isCanopyRelated)
            {
                string status = isActive ? "ACTIVE" : "INACTIVE";
                string visual = hasVisual ? "[VISUAL]" : "";
                string canopy = isCanopyRelated ? "[CANOPY?]" : "";
                Plugin.Log.LogInfo($"[Hierarchy]{indent}{parent.name} {status} {visual} {canopy}");
                
                // Log renderer details for canopy-related items
                if (isCanopyRelated && renderer != null)
                {
                    Plugin.Log.LogInfo($"[Hierarchy]{indent}  -> Renderer enabled: {renderer.enabled}, material: {renderer.material?.name}");
                }
            }
            
            foreach (Transform child in parent)
            {
                LogAircraftHierarchy(child, depth + 1);
            }
        }
        
        /// <summary>
        /// Ensure canopy and related visual elements are visible on cloned aircraft
        /// </summary>
        private void EnsureCanopyVisible(GameObject aircraft)
        {
            try
            {
                var allTransforms = aircraft.GetComponentsInChildren<Transform>(true);
                int canopyFixCount = 0;
                
                foreach (var t in allTransforms)
                {
                    string name = t.name.ToLower();
                    
                    // Check for canopy-related names (expanded list)
                    bool isCanopyRelated = name.Contains("canopy") || name.Contains("glass") || 
                                           name.Contains("cockpit") || name.Contains("window") ||
                                           name.Contains("bubble") || name.Contains("hood") ||
                                           name.Contains("transparency") || name.Contains("windshield") ||
                                           name.Contains("hud") || name.Contains("visor");
                    
                    if (isCanopyRelated)
                    {
                        Plugin.Log.LogInfo($"[NetworkManager] Found canopy-related object: {t.name}, active={t.gameObject.activeSelf}");
                        
                        // Ensure GameObject is active
                        if (!t.gameObject.activeSelf)
                        {
                            t.gameObject.SetActive(true);
                            Plugin.Log.LogInfo($"[NetworkManager] Activated canopy object: {t.name}");
                            canopyFixCount++;
                        }
                        
                        // Ensure all parent objects are active too
                        Transform parent = t.parent;
                        while (parent != null && parent != aircraft.transform)
                        {
                            if (!parent.gameObject.activeSelf)
                            {
                                parent.gameObject.SetActive(true);
                                Plugin.Log.LogInfo($"[NetworkManager] Activated canopy parent: {parent.name}");
                                canopyFixCount++;
                            }
                            parent = parent.parent;
                        }
                        
                        // Ensure renderer is enabled
                        var renderer = t.GetComponent<Renderer>();
                        if (renderer != null)
                        {
                            if (!renderer.enabled)
                            {
                                renderer.enabled = true;
                                Plugin.Log.LogInfo($"[NetworkManager] Enabled canopy renderer: {t.name}");
                                canopyFixCount++;
                            }
                            
                            // Log material info
                            Plugin.Log.LogInfo($"[NetworkManager] Canopy material: {renderer.material?.name}, shader: {renderer.material?.shader?.name}");
                        }
                        
                        // Check all renderers in children too
                        var childRenderers = t.GetComponentsInChildren<Renderer>(true);
                        foreach (var r in childRenderers)
                        {
                            if (r != null && !r.enabled)
                            {
                                r.enabled = true;
                                Plugin.Log.LogInfo($"[NetworkManager] Enabled canopy child renderer: {r.name}");
                                canopyFixCount++;
                            }
                        }
                    }
                }
                
                // Also check LODGroup - force LOD0 (highest detail)
                var lodGroup = aircraft.GetComponent<LODGroup>();
                if (lodGroup != null)
                {
                    Plugin.Log.LogInfo($"[NetworkManager] Found LODGroup, forcing LOD0");
                    lodGroup.ForceLOD(0);
                }
                
                // Check child LOD groups too
                var childLodGroups = aircraft.GetComponentsInChildren<LODGroup>(true);
                foreach (var lod in childLodGroups)
                {
                    if (lod != null)
                    {
                        lod.ForceLOD(0);
                        Plugin.Log.LogInfo($"[NetworkManager] Forced LOD0 on child: {lod.gameObject.name}");
                    }
                }
                
                if (canopyFixCount > 0)
                {
                    Plugin.Log.LogInfo($"[NetworkManager] Fixed {canopyFixCount} canopy visibility issues");
                }
                else
                {
                    Plugin.Log.LogInfo("[NetworkManager] No canopy visibility issues found (or no canopy detected by name)");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NetworkManager] EnsureCanopyVisible error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Create fallback marker when aircraft cloning fails
        /// </summary>
        private GameObject CreateFallbackMarker(ulong peerId)
        {
            var marker = new GameObject($"MP_RemoteMarker_{peerId}");
            
            // Add visual markers
            CreateLineRendererMarker(marker);
            CreateTrailMarker(marker);
            CreatePrimitivesWithGameMaterial(marker);
            marker.AddComponent<RemoteMarkerRenderer>();
            
            // Add light
            try
            {
                var lightObj = new GameObject("RemotePlayerLight");
                lightObj.transform.SetParent(marker.transform);
                lightObj.transform.localPosition = Vector3.zero;
                var light = lightObj.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = Color.red;
                light.intensity = 100f;
                light.range = 1000f;
            }
            catch { }
            
            return marker;
        }

        private void CreateLineRendererMarker(GameObject parent)
        {
            try
            {
                // Create a cross pattern with LineRenderer
                var lineObj = new GameObject("LineMarker");
                lineObj.transform.SetParent(parent.transform);
                lineObj.transform.localPosition = Vector3.zero;
                
                var lr = lineObj.AddComponent<LineRenderer>();
                lr.useWorldSpace = false;
                lr.startWidth = 5f;
                lr.endWidth = 5f;
                lr.positionCount = 5;
                
                // Create a diamond shape
                float size = 20f;
                lr.SetPosition(0, new Vector3(0, size, 0));
                lr.SetPosition(1, new Vector3(size, 0, 0));
                lr.SetPosition(2, new Vector3(0, -size, 0));
                lr.SetPosition(3, new Vector3(-size, 0, 0));
                lr.SetPosition(4, new Vector3(0, size, 0));
                
                // Use sprite material which renders in URP
                lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.startColor = Color.red;
                lr.endColor = Color.yellow;
                
                Plugin.Log.LogInfo("[NetworkManager] Created LineRenderer marker");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NetworkManager] LineRenderer failed: {ex.Message}");
            }
        }
        
        private void CreateTrailMarker(GameObject parent)
        {
            try
            {
                var trailObj = new GameObject("TrailMarker");
                trailObj.transform.SetParent(parent.transform);
                trailObj.transform.localPosition = Vector3.zero;
                
                var trail = trailObj.AddComponent<TrailRenderer>();
                trail.time = 2f;
                trail.startWidth = 10f;
                trail.endWidth = 2f;
                trail.material = new Material(Shader.Find("Sprites/Default"));
                trail.startColor = Color.red;
                trail.endColor = new Color(1, 0, 0, 0);
                
                Plugin.Log.LogInfo("[NetworkManager] Created TrailRenderer marker");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NetworkManager] TrailRenderer failed: {ex.Message}");
            }
        }

        private void CreatePrimitivesWithGameMaterial(GameObject parent)
        {
            try
            {
                // Find ANY material that's currently visible in the scene
                Material foundMaterial = null;
                Shader foundShader = null;
                
                var allRenderers = GameObject.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
                foreach (var r in allRenderers)
                {
                    if (r.material != null && r.material.shader != null && r.isVisible)
                    {
                        foundMaterial = r.material;
                        foundShader = r.material.shader;
                        Plugin.Log.LogInfo($"[NetworkManager] Found visible material from: {r.gameObject.name}, shader: {foundShader.name}");
                        break;
                    }
                }
                
                if (foundShader == null)
                {
                    // Just find any material
                    foreach (var r in allRenderers)
                    {
                        if (r.material != null && r.material.shader != null)
                        {
                            foundShader = r.material.shader;
                            Plugin.Log.LogInfo($"[NetworkManager] Found shader: {foundShader.name} from {r.gameObject.name}");
                            break;
                        }
                    }
                }
                
                // Create sphere with found material
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "PrimitiveMarker";
                sphere.transform.SetParent(parent.transform);
                sphere.transform.localPosition = Vector3.zero;
                sphere.transform.localScale = new Vector3(30f, 30f, 30f);
                
                var col = sphere.GetComponent<Collider>();
                if (col != null) GameObject.Destroy(col);
                
                var renderer = sphere.GetComponent<Renderer>();
                if (renderer != null && foundShader != null)
                {
                    var mat = new Material(foundShader);
                    mat.color = Color.red;
                    
                    // Try to set common color properties
                    if (mat.HasProperty("_BaseColor"))
                        mat.SetColor("_BaseColor", Color.red);
                    if (mat.HasProperty("_Color"))
                        mat.SetColor("_Color", Color.red);
                    if (mat.HasProperty("_EmissionColor"))
                    {
                        mat.EnableKeyword("_EMISSION");
                        mat.SetColor("_EmissionColor", Color.red * 2f);
                    }
                    
                    renderer.material = mat;
                    Plugin.Log.LogInfo($"[NetworkManager] Created primitive with shader: {foundShader.name}");
                }
                
                // Make sure layer is visible
                sphere.layer = 0;  // Default layer
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NetworkManager] Primitive creation failed: {ex.Message}");
            }
        }

        private void CleanupRemoteAircraft()
        {
            if (_remoteAircraftObject != null)
            {
                Plugin.Log.LogInfo("Cleaning up remote aircraft");
                GameObject.Destroy(_remoteAircraftObject);
                _remoteAircraftObject = null;
            }
            
            _remoteController = null;
            _interpolationBuffer.Clear();
            _usingRealAircraft = false;
            _needsAircraftCloneRetry = false;
        }
    }
}
