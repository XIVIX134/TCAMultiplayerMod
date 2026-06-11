using System;
using UnityEngine;
using Falcon.UniversalAircraft;
using TCAMultiplayer.Core;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.Sync;

namespace TCAMultiplayer.Game
{
    /// <summary>
    /// Handles the death → respawn screen → respawn cycle for N-player sessions.
    /// Registers packet handlers for RequestRespawn(50) and Respawned(51).
    /// Created per GameSession, disposed with it. No static state.
    /// </summary>
    public class RespawnManager : IDisposable
    {
        private const string Tag = "RESPAWN";
        private const float RespawnCooldown = 5.0f;

        private readonly GameSession _session;
        private readonly ConnectionManager _connection;
        private readonly PacketRouter _router;
        private readonly RemoteAircraftManager _remoteManager;

        private float _deathTime;
        private bool _awaitingRespawn;
        private bool _disposed;

        /// <summary>Raised when the local player's aircraft is destroyed.</summary>
        public event Action OnDeathDetected;

        /// <summary>Raised when respawn cooldown elapses (UI shows respawn button).</summary>
        public event Action OnRespawnReady;

        /// <summary>Raised when any peer (local or remote) completes respawn. Args: (peerId, aircraftType).</summary>
        public event Action<ulong, string> OnPeerRespawned;

        // ── Constructor ─────────────────────────────────────────────

        public RespawnManager(
            GameSession session,
            ConnectionManager connection,
            PacketRouter router,
            RemoteAircraftManager remoteManager)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _remoteManager = remoteManager ?? throw new ArgumentNullException(nameof(remoteManager));

            _router.Register(PacketType.RequestRespawn, HandleRespawnRequestRaw);
            _router.Register(PacketType.Respawned, HandleRespawnedRaw);
            Log.Info(Tag, "Initialized");
        }

        // ── Death Detection ─────────────────────────────────────────

        /// <summary>
        /// Called when the local player's aircraft is destroyed
        /// (via GameEventBridge.OnAircraftDestroyed).
        /// Transitions to Respawning state and starts cooldown timer.
        /// </summary>
        public void HandleLocalDeath(UniAircraft aircraft)
        {
            if (_disposed) return;

            var currentState = _session.StateMachine.CurrentState;
            if (currentState != GameState.InGame)
            {
                Log.Warning(Tag, $"Death detected in state {currentState}, ignoring");
                return;
            }

            if (!_session.StateMachine.TryTransition(GameState.Respawning))
            {
                Log.Error(Tag, "Failed to transition InGame → Respawning");
                return;
            }

            var localPlayer = _session.GetLocalPlayer();
            uint lifeId = localPlayer != null ? _session.EndPlayerLife(localPlayer.PeerId) : 0;

            _deathTime = Time.time;
            _awaitingRespawn = true;

            Log.Info(Tag, $"Local death detected, life={lifeId}, cooldown {RespawnCooldown}s");
            OnDeathDetected?.Invoke();
        }

        // ── Cooldown ────────────────────────────────────────────────

        /// <summary>
        /// Call each frame. Checks respawn cooldown and fires
        /// <see cref="OnRespawnReady"/> when the player may request respawn.
        /// </summary>
        public void Update()
        {
            if (_disposed) return;
            if (!_awaitingRespawn) return;

            if (Time.time - _deathTime >= RespawnCooldown)
            {
                _awaitingRespawn = false;
                Log.Info(Tag, "Respawn cooldown elapsed");
                OnRespawnReady?.Invoke();
            }
        }

        // ── Outbound: Client sends RequestRespawn to host ───────────

        /// <summary>
        /// Send a respawn request to the host. Called by UI after cooldown.
        /// Host processes locally; clients send over the network.
        /// </summary>
        public void RequestRespawn()
        {
            if (_disposed) return;

            if (_session.StateMachine.CurrentState != GameState.Respawning)
            {
                Log.Warning(Tag, "RequestRespawn called outside Respawning state, ignoring");
                return;
            }

            if (_session.IsHost)
            {
                // Host processes own respawn locally — no network round-trip
                HandleRespawnRequest(_session.LocalPeerId);
            }
            else
            {
                var packet = new LobbyRespawnRequestPacket
                {
                    PeerId = _session.LocalPeerId
                };

                var payload = PacketSerializer.SerializeLobbyRespawnRequest(packet);
                var frame = PacketSerializer.Serialize(PacketType.RequestRespawn, payload);
                _connection.BroadcastReliable(frame);
                Log.Info(Tag, "Sent respawn request to host");
            }
        }

        // ── Inbound: Host receives RequestRespawn ───────────────────

        private void HandleRespawnRequestRaw(ulong fromPeerId, byte[] data)
        {
            if (_disposed) return;

            if (!_session.IsHost)
            {
                Log.Warning(Tag, $"Non-host received RequestRespawn from {fromPeerId}, ignoring");
                return;
            }

            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
            {
                Log.Warning(Tag, $"Null RequestRespawn payload from peer {fromPeerId}");
                return;
            }

            var packet = PacketSerializer.DeserializeLobbyRespawnRequest(payload);
            if (packet.PeerId != fromPeerId && fromPeerId != _session.LocalPeerId)
            {
                Log.Warning(Tag, $"Respawn request peer mismatch from {fromPeerId}: peer={packet.PeerId}");
                return;
            }
            HandleRespawnRequest(packet.PeerId);
        }

        /// <summary>
        /// Host validates a respawn request, then broadcasts Respawned(51)
        /// to all connected peers using AircraftChangedPacket format.
        /// </summary>
        private void HandleRespawnRequest(ulong peerId)
        {
            var player = _session.GetPlayer(peerId);
            if (player == null)
            {
                Log.Warning(Tag, $"Respawn request from unknown peer {peerId}");
                return;
            }
            if (player.IsAlive || !player.IsAwaitingRespawn)
            {
                Log.Warning(Tag, $"Respawn request from live/non-dead peer {peerId}, ignoring");
                return;
            }

            string aircraftType = player.SelectedAircraft ?? "F-16C";
            string loadoutName = player.SelectedLoadout ?? "";
            Log.Info(Tag, $"Host approving respawn: peer {peerId} ({aircraftType} / {loadoutName}) oldLife={player.LifeId}");

            // Broadcast Respawned(51) to all peers
            var respawnedPacket = new AircraftChangedPacket
            {
                PlayerId = peerId,
                AircraftType = aircraftType,
                IsAlive = true,
                LoadoutName = loadoutName
            };

            var payload = PacketSerializer.SerializeAircraftChanged(respawnedPacket);
            var frame = PacketSerializer.Serialize(PacketType.Respawned, payload);
            _connection.BroadcastReliable(frame);

            // Host processes the respawn locally as well
            HandleRespawned(peerId, aircraftType, loadoutName);
        }

        // ── Inbound: All peers receive Respawned ────────────────────

        private void HandleRespawnedRaw(ulong fromPeerId, byte[] data)
        {
            if (_disposed) return;

            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
            {
                Log.Warning(Tag, $"Null Respawned payload from peer {fromPeerId}");
                return;
            }

            var packet = PacketSerializer.DeserializeAircraftChanged(payload);
            if (_session.IsHost && fromPeerId != _session.LocalPeerId)
            {
                Log.Warning(Tag, $"Client sent Respawned from {fromPeerId}, ignoring");
                return;
            }
            HandleRespawned(packet.PlayerId, packet.AircraftType, packet.LoadoutName);
        }

        /// <summary>
        /// Process a Respawned notification for any peer.
        /// Remote peers: clean up old clone via RemoteAircraftManager.
        /// Local peer: transition back through Spawning → InGame.
        /// </summary>
        private void HandleRespawned(ulong peerId, string aircraftType, string loadoutName = null)
        {
            Log.Info(Tag, $"Peer {peerId} respawned with {aircraftType}");

            var player = _session.GetPlayer(peerId);
            uint lifeId = player != null ? _session.BeginPlayerLife(peerId) : 0;

            // Adopt the respawn selection so every peer's player info matches
            // (clone spawning reads SelectedAircraft/SelectedLoadout from here)
            if (player != null)
            {
                if (!string.IsNullOrEmpty(aircraftType))
                    player.SelectedAircraft = aircraftType;
                if (!string.IsNullOrEmpty(loadoutName))
                    player.SelectedLoadout = loadoutName;
            }

            if (peerId == _session.LocalPeerId)
            {
                HandleLocalRespawn(aircraftType);
            }
            else
            {
                HandleRemoteRespawn(peerId, aircraftType);
            }

            Log.Info(Tag, $"Peer {peerId} active life={lifeId}");
            OnPeerRespawned?.Invoke(peerId, aircraftType);
        }

        // ── Local respawn ───────────────────────────────────────────

        private void HandleLocalRespawn(string aircraftType)
        {
            // Respawning → Spawning
            if (!_session.StateMachine.TryTransition(GameState.Spawning))
            {
                Log.Error(Tag, "Failed to transition Respawning → Spawning");
                return;
            }

            Log.Info(Tag, $"Local respawn: entering Spawning ({aircraftType})");

            // Broadcast AircraftChanged(52) so all peers know our aircraft
            BroadcastAircraftChanged(aircraftType);

            // Spawning → InGame
            if (!_session.StateMachine.TryTransition(GameState.InGame))
            {
                Log.Warning(Tag, "Spawning → InGame transition deferred (may complete externally)");
            }
        }

        private void BroadcastAircraftChanged(string aircraftType)
        {
            var changedPacket = new AircraftChangedPacket
            {
                PlayerId = _session.LocalPeerId,
                AircraftType = aircraftType,
                IsAlive = true
            };

            var payload = PacketSerializer.SerializeAircraftChanged(changedPacket);
            var frame = PacketSerializer.Serialize(PacketType.AircraftChanged, payload);
            _connection.BroadcastReliable(frame);
        }

        // ── Remote respawn ──────────────────────────────────────────

        private void HandleRemoteRespawn(ulong peerId, string aircraftType)
        {
            _remoteManager.RespawnPeer(peerId, aircraftType);
            Log.Info(Tag, $"Remote peer {peerId} marked for respawn ({aircraftType})");
        }

        // ── Dispose ─────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _router.Unregister(PacketType.RequestRespawn, HandleRespawnRequestRaw);
            _router.Unregister(PacketType.Respawned, HandleRespawnedRaw);

            _awaitingRespawn = false;

            OnDeathDetected = null;
            OnRespawnReady = null;
            OnPeerRespawned = null;

            Log.Info(Tag, "Disposed");
        }
    }
}
