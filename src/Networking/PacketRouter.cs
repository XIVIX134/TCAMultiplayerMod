using System;
using System.Collections.Generic;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Routes incoming packets to registered handlers.
    /// Replaces the giant switch statement in NetworkManager with a cleaner pattern.
    /// </summary>
    public class PacketRouter
    {
        private readonly Dictionary<PacketType, Action<ulong, byte[]>> _handlers = new Dictionary<PacketType, Action<ulong, byte[]>>();

        /// <summary>
        /// Register a handler for a specific packet type.
        /// </summary>
        public void RegisterHandler(PacketType type, Action<ulong, byte[]> handler)
        {
            if (handler == null)
            {
                Plugin.Log.LogWarning($"[PacketRouter] Attempted to register null handler for {type}");
                return;
            }

            if (_handlers.ContainsKey(type))
            {
                Plugin.Log.LogWarning($"[PacketRouter] Overwriting handler for {type}");
            }

            _handlers[type] = handler;
        }

        /// <summary>
        /// Unregister a handler for a packet type.
        /// </summary>
        public void UnregisterHandler(PacketType type)
        {
            _handlers.Remove(type);
        }

        /// <summary>
        /// Route a packet to its registered handler.
        /// </summary>
        /// <returns>True if a handler was found and invoked, false otherwise.</returns>
        public bool ProcessPacket(ulong peerId, PacketType type, byte[] payload)
        {
            if (_handlers.TryGetValue(type, out var handler))
            {
                try
                {
                    handler(peerId, payload);
                    return true;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[PacketRouter] Handler error for {type}: {ex.Message}");
                    return false;
                }
            }

            // Log unhandled packet types (rate limited)
            if (LogHelper.IsEnabled(LogCategory.Network) &&
                LogHelper.ShouldLogInterval($"PacketRouter.Unhandled.{type}", LogHelper.DefaultIntervalSeconds))
            {
                LogHelper.Info(LogCategory.Network, $"[PacketRouter] No handler for packet type: {type}");
            }

            return false;
        }

        /// <summary>
        /// Check if a handler is registered for a packet type.
        /// </summary>
        public bool HasHandler(PacketType type)
        {
            return _handlers.ContainsKey(type);
        }

        /// <summary>
        /// Get the number of registered handlers.
        /// </summary>
        public int HandlerCount => _handlers.Count;

        /// <summary>
        /// Clear all registered handlers.
        /// </summary>
        public void ClearHandlers()
        {
            _handlers.Clear();
        }
    }
}
