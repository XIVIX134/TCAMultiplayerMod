using System;
using System.Collections.Generic;
using TCAMultiplayer.Core;
using TCAMultiplayer.Protocol;

namespace TCAMultiplayer.Compatibility
{
    /// <summary>
    /// Performs a hash-based Mods-folder compatibility handshake and optional
    /// Mods-folder sync from host to client.
    /// </summary>
    public class ModManifestCollector : IDisposable
    {
        private const string Tag = "MOD-COMPAT";
        private const int SyncChunkSize = 960;
        private const int SyncChunksPerUpdate = 32;
        private const int MaxSyncPackageBytes = ModFileManifest.MaxSyncPackageBytes;

        private readonly GameSession _session;
        private readonly ConnectionManager _connection;
        private readonly PacketRouter _router;
        private readonly Dictionary<ulong, ModFileManifest> _clientManifests =
            new Dictionary<ulong, ModFileManifest>();
        private readonly Dictionary<uint, IncomingTransfer> _incomingTransfers =
            new Dictionary<uint, IncomingTransfer>();
        private readonly Dictionary<ulong, OutgoingTransfer> _outgoingTransfers =
            new Dictionary<ulong, OutgoingTransfer>();
        private readonly List<ulong> _finishedPeers = new List<ulong>();
        private bool _disposed;
        private uint _nextTransferId = 1;
        private ModFileManifest _hostManifestForPrompt;
        private string _hostManifestHashForPrompt;

        /// <summary>When false, skip mod file comparison and immediately accept all peers.</summary>
        public bool ModCheckingEnabled { get; set; } = true;

        public event Action OnCompatibilityAccepted;
        public event Action<ModMismatchInfo> OnCompatibilityMismatch;
        public event Action<string> OnSyncStatus;

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
            _router.Register(PacketType.ModSyncRequest, HandleSyncRequestRaw);
            _router.Register(PacketType.ModSyncChunk, HandleSyncChunkRaw);

            _session.OnPlayerJoined += OnPlayerJoined;
            Log.Info(Tag, "Initialized");
        }

        public void Update(float deltaTime)
        {
            if (_disposed || !_session.IsHost || _outgoingTransfers.Count == 0)
                return;

            _finishedPeers.Clear();
            foreach (var kvp in _outgoingTransfers)
            {
                SendOutgoingSyncBatch(kvp.Key, kvp.Value);
                if (kvp.Value.IsComplete)
                    _finishedPeers.Add(kvp.Key);
            }

            foreach (ulong peerId in _finishedPeers)
            {
                _outgoingTransfers.Remove(peerId);
            }
        }

        public void SendManifest()
        {
            var manifest = ModFileManifest.Collect();
            var packet = new ModManifestPacket
            {
                PeerId = _session.LocalPeerId,
                ManifestData = manifest.Serialize(),
                ModVersion = PluginMetadata.Version
            };

            var payload = PacketSerializer.SerializeModManifest(packet);
            var frame = PacketSerializer.Serialize(PacketType.ModManifest, payload);
            _connection.BroadcastReliable(frame);
            Log.Info(Tag, $"Sent Mods manifest: files={manifest.Files.Count}, hash={manifest.ManifestHash.Substring(0, 16)}");
        }

        public void RequestSyncFromHost()
        {
            if (_session.IsHost)
                return;
            if (_hostManifestForPrompt == null || string.IsNullOrWhiteSpace(_hostManifestHashForPrompt))
            {
                OnSyncStatus?.Invoke("No host mod manifest available to sync");
                return;
            }

            var packet = new ModSyncRequestPacket
            {
                PeerId = _session.LocalPeerId,
                HostManifestHash = _hostManifestHashForPrompt
            };
            var payload = PacketSerializer.SerializeModSyncRequest(packet);
            var frame = PacketSerializer.Serialize(PacketType.ModSyncRequest, payload);
            _connection.BroadcastReliable(frame);
            OnSyncStatus?.Invoke("Requesting mod sync from host...");
            Log.Info(Tag, $"Requested mod sync for host manifest {_hostManifestHashForPrompt.Substring(0, 16)}");
        }

        // ── Auto-verification (mod checking disabled) ──────────────────

        /// <summary>
        /// When the host has mod checking disabled, mark every new player as
        /// verified immediately without waiting for their manifest packet.
        /// Eliminates the timing window where HasUnverifiedPeers could return
        /// true between the player joining and HandleManifestReceived running.
        /// </summary>
        private void OnPlayerJoined(PlayerInfo player)
        {
            if (_disposed || !_session.IsHost || ModCheckingEnabled)
                return;

            player.IsModsVerified = true;
            Log.Info(Tag, $"Player {player.PeerId} auto-verified (mod checking disabled)");
        }

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

        private void HandleManifestReceived(ulong fromPeerId, ModManifestPacket packet)
        {
            if (!_session.IsHost)
            {
                Log.Warning(Tag, "Non-host received manifest packet, ignoring");
                return;
            }

            string clientVersion = packet.ModVersion ?? "";
            string hostVersion = PluginMetadata.Version;
            bool versionMatches = string.Equals(hostVersion, clientVersion, StringComparison.Ordinal);

            ModFileManifest clientManifest = null;
            ModFileManifest hostManifest = null;
            ModFileManifest.ModManifestDiff diff = null;
            string reason = "";

            bool isCompatible;

            if (ModCheckingEnabled)
            {
                try
                {
                    clientManifest = ModFileManifest.Deserialize(packet.ManifestData);
                    hostManifest = ModFileManifest.Collect();
                    diff = clientManifest.CompareTo(hostManifest);
                }
                catch (Exception ex)
                {
                    reason = $"Invalid client mod manifest: {ex.Message}";
                }

                isCompatible = versionMatches && diff != null && diff.IsCompatible;
                if (!versionMatches)
                {
                    string shownClientVersion = string.IsNullOrWhiteSpace(clientVersion) ? "unknown/old build" : clientVersion;
                    reason = $"TCAMP version mismatch - host {hostVersion}, client {shownClientVersion}";
                }
                else if (!isCompatible && string.IsNullOrEmpty(reason))
                {
                    reason = $"Mod files mismatch ({diff.ToSummary()}). Sync or match the host Mods folder.";
                }

                if (clientManifest != null)
                    _clientManifests[fromPeerId] = clientManifest;
            }
            else
            {
                isCompatible = versionMatches;
                if (!versionMatches)
                {
                    string shownClientVersion = string.IsNullOrWhiteSpace(clientVersion) ? "unknown/old build" : clientVersion;
                    reason = $"TCAMP version mismatch - host {hostVersion}, client {shownClientVersion}";
                }
                Log.Info(Tag, $"Mod checking disabled — peer {fromPeerId} accepted without manifest comparison");
            }

            var player = _session.GetPlayer(fromPeerId);
            if (player != null)
            {
                player.IsModsVerified = isCompatible;
                if (isCompatible)
                    player.IsModSyncing = false;
            }

            if (isCompatible)
            {
                Log.Info(Tag, $"Peer {fromPeerId} Mods manifest accepted");
            }
            else
            {
                Log.Warning(Tag, $"Peer {fromPeerId} Mods manifest rejected: {reason}");
            }

            var result = new ModCompatibilityResultPacket
            {
                PeerId = fromPeerId,
                IsCompatible = isCompatible,
                RejectionReason = reason,
                HostModVersion = hostVersion,
                HostManifestData = isCompatible ? null : hostManifest?.Serialize()
            };

            var resultPayload = PacketSerializer.SerializeModCompatibilityResult(result);
            var resultFrame = PacketSerializer.Serialize(PacketType.ModCompatibilityResult, resultPayload);
            _connection.SendReliable(fromPeerId, resultFrame);
        }

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
                reason = $"TCAMP version mismatch - host {shownHostVersion}, client {PluginMetadata.Version}";
            }

            if (isCompatible)
            {
                Log.Info(Tag, "Mods compatible with host");
                OnCompatibilityAccepted?.Invoke();
                return;
            }

            ModFileManifest hostManifest = null;
            ModFileManifest.ModManifestDiff diff = null;
            try
            {
                if (packet.HostManifestData != null && packet.HostManifestData.Length > 0)
                {
                    hostManifest = ModFileManifest.Deserialize(packet.HostManifestData);
                    diff = ModFileManifest.Collect().CompareTo(hostManifest);
                    _hostManifestForPrompt = hostManifest;
                    _hostManifestHashForPrompt = hostManifest.ManifestHash;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Could not parse host manifest for sync prompt: {ex.Message}");
            }

            Log.Warning(Tag, $"Mod incompatible: {reason}");
            OnCompatibilityMismatch?.Invoke(new ModMismatchInfo
            {
                Reason = reason,
                HostManifest = hostManifest,
                Diff = diff,
                CanSync = hostManifest != null && diff != null && diff.Unsyncable.Count == 0,
                HostManifestHash = _hostManifestHashForPrompt
            });
        }

        private void HandleSyncRequestRaw(ulong fromPeerId, byte[] data)
        {
            if (_disposed || !_session.IsHost)
                return;

            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
                return;

            var packet = PacketSerializer.DeserializeModSyncRequest(payload);
            if (packet.PeerId != fromPeerId)
            {
                Log.Warning(Tag, $"Rejected ModSyncRequest from peer {fromPeerId} for peer {packet.PeerId}");
                return;
            }

            var player = _session.GetPlayer(fromPeerId);
            if (player != null)
                player.IsModSyncing = true;

            if (!_clientManifests.TryGetValue(fromPeerId, out var clientManifest))
            {
                Log.Warning(Tag, $"Peer {fromPeerId} requested sync before sending a manifest");
                if (player != null)
                    player.IsModSyncing = false;
                return;
            }

            try
            {
                var hostManifest = ModFileManifest.Collect();
                if (!string.Equals(hostManifest.ManifestHash, packet.HostManifestHash, StringComparison.Ordinal))
                {
                    Log.Warning(Tag, $"Peer {fromPeerId} requested stale host manifest {packet.HostManifestHash}");
                    SendRejectedResult(fromPeerId, "Host mods changed. Try joining again.", hostManifest);
                    if (player != null)
                        player.IsModSyncing = false;
                    return;
                }

                var diff = clientManifest.CompareTo(hostManifest);
                if (diff.Unsyncable.Count > 0)
                {
                    SendRejectedResult(fromPeerId,
                        $"Mod files mismatch ({diff.ToSummary()}). Remove blocked files manually, then sync again.",
                        hostManifest);
                    if (player != null)
                        player.IsModSyncing = false;
                    return;
                }

                byte[] package = hostManifest.BuildSyncPackage(clientManifest);
                SendSyncPackage(fromPeerId, package);
                OnSyncStatus?.Invoke($"Sending mod sync to peer {fromPeerId}...");
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Failed to build sync package for peer {fromPeerId}: {ex.Message}");
                SendRejectedResult(fromPeerId, $"Mod sync failed on host: {ex.Message}", ModFileManifest.Collect());
                if (player != null)
                    player.IsModSyncing = false;
            }
        }

        private void SendRejectedResult(ulong peerId, string reason, ModFileManifest hostManifest)
        {
            var result = new ModCompatibilityResultPacket
            {
                PeerId = peerId,
                IsCompatible = false,
                RejectionReason = reason,
                HostModVersion = PluginMetadata.Version,
                HostManifestData = hostManifest?.Serialize()
            };
            var payload = PacketSerializer.SerializeModCompatibilityResult(result);
            _connection.SendReliable(peerId, PacketSerializer.Serialize(PacketType.ModCompatibilityResult, payload));
        }

        private void SendSyncPackage(ulong peerId, byte[] package)
        {
            if (package == null || package.Length == 0)
                throw new InvalidOperationException("Empty sync package");
            if (package.Length > MaxSyncPackageBytes)
                throw new InvalidOperationException("Sync package is too large");

            uint transferId = _nextTransferId++;
            if (_nextTransferId == 0)
                _nextTransferId = 1;

            var transfer = new OutgoingTransfer(transferId, package, SyncChunkSize);
            _outgoingTransfers[peerId] = transfer;
            SendOutgoingSyncBatch(peerId, transfer);
            Log.Info(Tag, $"Queued sync package to peer {peerId}: {package.Length} bytes in {transfer.ChunkCount} chunks");
        }

        private void SendOutgoingSyncBatch(ulong peerId, OutgoingTransfer transfer)
        {
            if (transfer == null || transfer.IsComplete)
                return;

            int sent = 0;
            while (sent < SyncChunksPerUpdate && !transfer.IsComplete)
            {
                int index = transfer.NextChunkIndex;
                int offset = index * transfer.ChunkSize;
                int length = Math.Min(transfer.ChunkSize, transfer.Package.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(transfer.Package, offset, chunk, 0, length);

                var packet = new ModSyncChunkPacket
                {
                    PeerId = peerId,
                    TransferId = transfer.TransferId,
                    ChunkIndex = index,
                    ChunkCount = transfer.ChunkCount,
                    TotalBytes = transfer.Package.Length,
                    ChunkData = chunk
                };
                var payload = PacketSerializer.SerializeModSyncChunk(packet);
                var frame = PacketSerializer.Serialize(PacketType.ModSyncChunk, payload);
                _connection.SendReliable(peerId, frame);
                transfer.NextChunkIndex++;
                sent++;
            }

            if (transfer.IsComplete)
            {
                Log.Info(Tag, $"Sent sync package to peer {peerId}: {transfer.Package.Length} bytes in {transfer.ChunkCount} chunks");
            }
        }

        private void HandleSyncChunkRaw(ulong fromPeerId, byte[] data)
        {
            if (_disposed || _session.IsHost)
                return;
            if (fromPeerId != 1)
            {
                Log.Warning(Tag, $"Rejected ModSyncChunk from non-host peer {fromPeerId}");
                return;
            }

            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
                return;

            var packet = PacketSerializer.DeserializeModSyncChunk(payload);
            if (packet.PeerId != _session.LocalPeerId)
            {
                Log.Warning(Tag, $"Rejected ModSyncChunk for peer {packet.PeerId}");
                return;
            }
            if (packet.TotalBytes <= 0 || packet.TotalBytes > MaxSyncPackageBytes
                || packet.ChunkCount <= 0 || packet.ChunkIndex < 0 || packet.ChunkIndex >= packet.ChunkCount
                || packet.ChunkData == null)
            {
                Log.Warning(Tag, "Rejected malformed ModSyncChunk");
                return;
            }

            if (!_incomingTransfers.TryGetValue(packet.TransferId, out var transfer))
            {
                transfer = new IncomingTransfer(packet.TransferId, packet.ChunkCount, packet.TotalBytes);
                _incomingTransfers[packet.TransferId] = transfer;
            }

            if (!transfer.TryAdd(packet.ChunkIndex, packet.ChunkData))
                return;

            if (transfer.ShouldReportProgress())
                OnSyncStatus?.Invoke($"Receiving mod sync {transfer.ReceivedCount}/{transfer.ChunkCount}...");
            if (!transfer.IsComplete)
                return;

            _incomingTransfers.Remove(packet.TransferId);
            byte[] package = transfer.Assemble();
            var result = ModFileManifest.ApplySyncPackage(package, _hostManifestHashForPrompt);
            if (!result.Success)
            {
                Log.Warning(Tag, $"Mod sync apply failed: {result.Message}");
                OnCompatibilityMismatch?.Invoke(new ModMismatchInfo
                {
                    Reason = "Mod sync failed: " + result.Message,
                    HostManifest = _hostManifestForPrompt,
                    Diff = _hostManifestForPrompt != null ? ModFileManifest.Collect().CompareTo(_hostManifestForPrompt) : null,
                    CanSync = _hostManifestForPrompt != null,
                    HostManifestHash = _hostManifestHashForPrompt
                });
                return;
            }

            Log.Info(Tag, result.Message);
            var reloadResult = ReloadGameDataAfterSync();
            if (!reloadResult.Success)
            {
                Log.Warning(Tag, $"Game data reload after mod sync failed: {reloadResult.Message}");
                OnSyncStatus?.Invoke(result.Message + ". Restart required: " + reloadResult.Message);
                return;
            }

            Log.Info(Tag, reloadResult.Message);
            OnSyncStatus?.Invoke(result.Message + ". Game data reloaded. Rechecking with host...");
            SendManifest();
        }

        /// <summary>Remove per-peer manifest and outgoing transfer state when a peer disconnects.</summary>
        public void CleanupPeer(ulong peerId)
        {
            _clientManifests.Remove(peerId);
            _outgoingTransfers.Remove(peerId);
        }

        private static SyncReloadResult ReloadGameDataAfterSync()
        {
            try
            {
                Falcon.GameData.LoadAllGameData(forceReload: true);
                Falcon.GameDataLoadouts.RefreshLoadoutDictionaries();
                return SyncReloadResult.Ok("Game data reloaded after mod sync");
            }
            catch (Exception ex)
            {
                return SyncReloadResult.Failed(ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _router.Unregister(PacketType.ModManifest, HandleManifestRaw);
            _router.Unregister(PacketType.ModCompatibilityResult, HandleCompatibilityResultRaw);
            _router.Unregister(PacketType.ModSyncRequest, HandleSyncRequestRaw);
            _router.Unregister(PacketType.ModSyncChunk, HandleSyncChunkRaw);
            _session.OnPlayerJoined -= OnPlayerJoined;
            _outgoingTransfers.Clear();
            _incomingTransfers.Clear();
            OnCompatibilityAccepted = null;
            OnCompatibilityMismatch = null;
            OnSyncStatus = null;
            Log.Info(Tag, "Disposed");
        }

        public sealed class ModMismatchInfo
        {
            public string Reason;
            public ModFileManifest HostManifest;
            public ModFileManifest.ModManifestDiff Diff;
            public bool CanSync;
            public string HostManifestHash;

            public string Summary
            {
                get
                {
                    if (Diff == null)
                        return Reason ?? "Mod files mismatch";
                    return $"{Reason} ({Diff.ToSummary()})";
                }
            }
        }

        private sealed class IncomingTransfer
        {
            private readonly byte[][] _chunks;
            private int _receivedCount;
            private int _lastReportedCount;

            public IncomingTransfer(uint transferId, int chunkCount, int totalBytes)
            {
                TransferId = transferId;
                ChunkCount = chunkCount;
                TotalBytes = totalBytes;
                _chunks = new byte[chunkCount][];
            }

            public uint TransferId { get; }
            public int ChunkCount { get; }
            public int TotalBytes { get; }
            public int ReceivedCount => _receivedCount;
            public bool IsComplete => _receivedCount == ChunkCount;

            public bool ShouldReportProgress()
            {
                if (_receivedCount == _lastReportedCount)
                    return false;

                int reportStep = Math.Max(1, ChunkCount / 100);
                if (_receivedCount == 1 || IsComplete || _receivedCount - _lastReportedCount >= reportStep)
                {
                    _lastReportedCount = _receivedCount;
                    return true;
                }

                return false;
            }

            public bool TryAdd(int index, byte[] data)
            {
                if (index < 0 || index >= ChunkCount || data == null)
                    return false;
                if (_chunks[index] != null)
                    return false;
                _chunks[index] = data;
                _receivedCount++;
                return true;
            }

            public byte[] Assemble()
            {
                var result = new byte[TotalBytes];
                int offset = 0;
                for (int i = 0; i < _chunks.Length; i++)
                {
                    var chunk = _chunks[i];
                    if (chunk == null)
                        throw new InvalidOperationException("Sync transfer is incomplete");
                    if (offset + chunk.Length > result.Length)
                        throw new InvalidOperationException("Sync transfer overflow");

                    Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                    offset += chunk.Length;
                }

                if (offset != result.Length)
                    throw new InvalidOperationException("Sync transfer size mismatch");

                return result;
            }
        }

        private sealed class OutgoingTransfer
        {
            public OutgoingTransfer(uint transferId, byte[] package, int chunkSize)
            {
                TransferId = transferId;
                Package = package ?? throw new ArgumentNullException(nameof(package));
                ChunkSize = chunkSize;
                ChunkCount = (package.Length + chunkSize - 1) / chunkSize;
            }

            public uint TransferId { get; }
            public byte[] Package { get; }
            public int ChunkSize { get; }
            public int ChunkCount { get; }
            public int NextChunkIndex { get; set; }
            public bool IsComplete => NextChunkIndex >= ChunkCount;
        }

        private struct SyncReloadResult
        {
            public bool Success;
            public string Message;

            public static SyncReloadResult Ok(string message)
            {
                return new SyncReloadResult { Success = true, Message = message };
            }

            public static SyncReloadResult Failed(string message)
            {
                return new SyncReloadResult
                {
                    Success = false,
                    Message = string.IsNullOrWhiteSpace(message) ? "unknown reload error" : message
                };
            }
        }
    }
}
