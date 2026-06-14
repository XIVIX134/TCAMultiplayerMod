using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Bootstrap;
using TCAMultiplayer.Core;
using TCAMultiplayer.Protocol;

namespace TCAMultiplayer.Compatibility
{
    /// <summary>
    /// Collects installed BepInEx plugin info, generates manifest hashes,
    /// and handles mod compatibility checking during the connection handshake.
    /// </summary>
    public class ModManifestCollector : IDisposable
    {
        private const string Tag = "MOD-COMPAT";
        private readonly GameSession _session;
        private readonly ConnectionManager _connection;
        private readonly PacketRouter _router;
        private bool _disposed;

        /// <summary>Fired when host sends compatibility result. Args: (isCompatible, reason).</summary>
        public event Action<bool, string> OnCompatibilityResult;

        public ModManifestCollector(
            GameSession session,
            ConnectionManager connection,
            PacketRouter router)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _router = router ?? throw new ArgumentNullException(nameof(router));

            _router.Register(PacketType.ModManifest, HandleManifestRaw);
            _router.Register(PacketType.ModCompatibilityResult, HandleCompatibilityResultRaw);
            Log.Info(Tag, "Initialized");
        }

        // ── Manifest Collection ─────────────────────────────────────────

        /// <summary>
        /// Gather installed BepInEx plugin GUIDs and versions.
        /// Returns a deterministically sorted list of "GUID@Version" strings.
        /// </summary>
        public List<string> CollectManifest()
        {
            var plugins = new List<string>();
            try
            {
                var pluginInfos = Chainloader.PluginInfos;
                if (pluginInfos == null)
                {
                    Log.Warning(Tag, "Chainloader.PluginInfos is null");
                    return plugins;
                }

                foreach (var kvp in pluginInfos)
                {
                    try
                    {
                        var info = kvp.Value;
                        if (info?.Metadata == null) continue;
                        string guid = info.Metadata.GUID ?? "unknown";
                        string version = info.Metadata.Version?.ToString() ?? "0.0.0";
                        plugins.Add($"{guid}@{version}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(Tag, $"Error reading plugin '{kvp.Key}': {ex.Message}");
                    }
                }

                plugins.Sort(StringComparer.Ordinal);
                Log.Info(Tag, $"Collected {plugins.Count} plugins");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Error collecting manifest: {ex.Message}");
            }
            return plugins;
        }

        /// <summary>SHA256 hash (first 16 hex chars) of the sorted plugin list.</summary>
        public string GetManifestHash()
        {
            var plugins = CollectManifest();
            var combined = string.Join(";", plugins);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
            }
        }
        // ── Outbound: client sends manifest to host ─────────────────────

        /// <summary>Client sends its manifest hash to the host during handshake.</summary>
        public void SendManifest()
        {
            var hash = GetManifestHash();
            var manifestBytes = Encoding.UTF8.GetBytes(hash);

            var packet = new ModManifestPacket
            {
                PeerId = _session.LocalPeerId,
                ManifestData = manifestBytes,
                ModVersion = PluginMetadata.Version
            };

            var payload = PacketSerializer.SerializeModManifest(packet);
            var frame = PacketSerializer.Serialize(PacketType.ModManifest, payload);
            _connection.BroadcastReliable(frame);
            Log.Info(Tag, $"Sent manifest hash: {hash}");
        }
        // ── Inbound: host receives manifest from client ─────────────────

        private void HandleManifestRaw(ulong fromPeerId, byte[] data)
        {
            if (_disposed) return;
            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
            {
                Log.Warning(Tag, $"Null ModManifest payload from peer {fromPeerId}");
                return;
            }
            var packet = PacketSerializer.DeserializeModManifest(payload);
            HandleManifestReceived(fromPeerId, packet);
        }

        /// <summary>Host compares client hash with own hash and sends result.</summary>
        private void HandleManifestReceived(ulong fromPeerId, ModManifestPacket packet)
        {
            if (!_session.IsHost)
            {
                Log.Warning(Tag, "Non-host received manifest packet, ignoring");
                return;
            }

            string clientHash = packet.ManifestData != null
                ? Encoding.UTF8.GetString(packet.ManifestData)
                : "";
            string hostHash = GetManifestHash();
            string clientVersion = packet.ModVersion ?? "";
            string hostVersion = PluginMetadata.Version;
            bool versionMatches = string.Equals(hostVersion, clientVersion, StringComparison.Ordinal);
            bool hashMatches = string.Equals(hostHash, clientHash, StringComparison.Ordinal);
            bool isCompatible = versionMatches && hashMatches;
            string reason = "";
            if (!versionMatches)
            {
                string shownClientVersion = string.IsNullOrWhiteSpace(clientVersion) ? "unknown/old build" : clientVersion;
                reason = $"TCAMP version mismatch — host {hostVersion}, client {shownClientVersion}";
            }
            else if (!hashMatches)
            {
                reason = $"Mod mismatch — host hash: {hostHash}, client hash: {clientHash}";
            }

            Log.Info(Tag,
                $"Peer {fromPeerId}: compatible={isCompatible} " +
                $"(hostVersion={hostVersion}, clientVersion={clientVersion}, host={hostHash}, client={clientHash})");

            var result = new ModCompatibilityResultPacket
            {
                PeerId = fromPeerId,
                IsCompatible = isCompatible,
                RejectionReason = reason,
                HostModVersion = hostVersion
            };

            var resultPayload = PacketSerializer.SerializeModCompatibilityResult(result);
            var resultFrame = PacketSerializer.Serialize(PacketType.ModCompatibilityResult, resultPayload);
            _connection.SendReliable(fromPeerId, resultFrame);
        }

        // ── Inbound: client receives compatibility result ────────────────

        private void HandleCompatibilityResultRaw(ulong fromPeerId, byte[] data)
        {
            if (_disposed) return;
            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
            {
                Log.Warning(Tag, $"Null ModCompatibilityResult payload from peer {fromPeerId}");
                return;
            }
            var packet = PacketSerializer.DeserializeModCompatibilityResult(payload);
            HandleCompatibilityResult(packet);
        }

        private void HandleCompatibilityResult(ModCompatibilityResultPacket packet)
        {
            bool isCompatible = packet.IsCompatible;
            string reason = packet.RejectionReason ?? "";
            string hostVersion = packet.HostModVersion ?? "";
            if (isCompatible && !string.Equals(hostVersion, PluginMetadata.Version, StringComparison.Ordinal))
            {
                string shownHostVersion = string.IsNullOrWhiteSpace(hostVersion) ? "unknown/old build" : hostVersion;
                isCompatible = false;
                reason = $"TCAMP version mismatch — host {shownHostVersion}, client {PluginMetadata.Version}";
            }

            if (isCompatible)
            {
                Log.Info(Tag, "Mods compatible with host");
            }
            else
            {
                Log.Warning(Tag, $"Mod incompatible: {reason}");
            }

            OnCompatibilityResult?.Invoke(isCompatible, reason);
        }

        // ── Dispose ─────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _router.Unregister(PacketType.ModManifest, HandleManifestRaw);
            _router.Unregister(PacketType.ModCompatibilityResult, HandleCompatibilityResultRaw);
            OnCompatibilityResult = null;
            Log.Info(Tag, "Disposed");
        }
    }
}
