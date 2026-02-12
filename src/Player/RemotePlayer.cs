using System.Collections.Generic;
using UnityEngine;
using TCAMultiplayer.Networking;
using TCAMultiplayer;

namespace TCAMultiplayer.Player
{
    /// <summary>
    /// Represents a remote player in the multiplayer session
    /// </summary>
    public class RemotePlayer
    {
        public ulong PlayerId { get; private set; }
        public string PlayerName { get; set; }
        public GameObject Aircraft { get; set; }
        public RemoteAircraftController Controller { get; set; }
        
        // State buffer for interpolation
        private readonly Queue<AircraftStatePacket> _stateBuffer = new Queue<AircraftStatePacket>();
        private const int MAX_BUFFER_SIZE = 10;
        
        // Current interpolated state
        public Vector3 Position { get; private set; }
        public Quaternion Rotation { get; private set; }
        public Vector3 Velocity { get; private set; }
        
        // Control state
        public float Throttle { get; private set; }
        public bool Afterburner { get; private set; }
        public bool GearDown { get; private set; }
        public bool IsFiring { get; private set; }
        public bool IsFlareFiring { get; private set; }
        public bool IsChaffFiring { get; private set; }

        public RemotePlayer(ulong playerId)
        {
            PlayerId = playerId;
            PlayerName = $"Player_{playerId}";
        }

        /// <summary>
        /// Add a new state update to the buffer
        /// </summary>
        public void AddStateUpdate(AircraftStatePacket state)
        {
            lock (_stateBuffer)
            {
                _stateBuffer.Enqueue(state);
                
                // Keep buffer size limited
                bool trimmed = false;
                while (_stateBuffer.Count > MAX_BUFFER_SIZE)
                {
                    _stateBuffer.Dequeue();
                    trimmed = true;
                }
                
                if (trimmed && LogHelper.IsEnabled(LogCategory.Player) &&
                    LogHelper.ShouldLogInterval("RemotePlayer.BufferTrim", LogHelper.DefaultIntervalSeconds))
                {
                    LogHelper.Info(LogCategory.Player,
                        $"[RemotePlayer] Trimmed state buffer for player {PlayerId} (size>{MAX_BUFFER_SIZE})");
                }
            }
            
            // Update current state (for now, just use latest - TODO: interpolation)
            Position = new Vector3((float)state.PosX, (float)state.PosY, (float)state.PosZ);
            Rotation = new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW);
            Velocity = new Vector3(state.VelX, state.VelY, state.VelZ);
            
            Throttle = state.Throttle;
            Afterburner = state.Afterburner;
            GearDown = state.GearDown;
            IsFiring = state.IsFiring;
            IsFlareFiring = state.IsFlareFiring;
            IsChaffFiring = state.IsChaffFiring;
        }

        /// <summary>
        /// Get interpolated state at the given time
        /// </summary>
        public void GetInterpolatedState(float time, out Vector3 position, out Quaternion rotation, out Vector3 velocity)
        {
            // TODO: Proper interpolation between buffered states
            // For now, just return the latest
            position = Position;
            rotation = Rotation;
            velocity = Velocity;
        }
    }

    /// <summary>
    /// Manages all remote players
    /// </summary>
    public static class RemotePlayerManager
    {
        private static readonly Dictionary<ulong, RemotePlayer> _remotePlayers = new Dictionary<ulong, RemotePlayer>();
        
        public static IReadOnlyDictionary<ulong, RemotePlayer> RemotePlayers => _remotePlayers;

        public static RemotePlayer GetOrCreatePlayer(ulong playerId)
        {
            if (!_remotePlayers.TryGetValue(playerId, out var player))
            {
                player = new RemotePlayer(playerId);
                _remotePlayers[playerId] = player;
                Plugin.Log.LogInfo($"RemotePlayerManager: Created remote player {playerId}");
            }
            return player;
        }

        public static void RemovePlayer(ulong playerId)
        {
            if (_remotePlayers.TryGetValue(playerId, out var player))
            {
                // Destroy aircraft if it exists
                if (player.Aircraft != null)
                {
                    Object.Destroy(player.Aircraft);
                }
                
                _remotePlayers.Remove(playerId);
                Plugin.Log.LogInfo($"RemotePlayerManager: Removed remote player {playerId}");
            }
        }

        public static void Clear()
        {
            foreach (var player in _remotePlayers.Values)
            {
                if (player.Aircraft != null)
                {
                    Object.Destroy(player.Aircraft);
                }
            }
            _remotePlayers.Clear();
        }

        public static void UpdateRemotePlayer(ulong playerId, AircraftStatePacket state)
        {
            var player = GetOrCreatePlayer(playerId);
            player.AddStateUpdate(state);
            
            // Note: Position is now handled by NetworkManager's interpolation system
            // Visual state updates (afterburner, gear, control surfaces, etc.)
            if (player.Controller != null)
            {
                player.Controller.UpdateFromState(state);
            }
        }
    }
}
