using System;
using System.Collections.Generic;
using System.Threading;
using TCAMultiplayer.Core;
using TCAMultiplayer.Transport;

namespace TCAMultiplayer.Protocol
{
    /// <summary>
    /// Adds reliability on top of <see cref="ITransport"/> for reliable packets.
    /// Unreliable packets pass through unchanged.
    ///
    /// Reliable packet flow:
    ///   Send:    assign sequence number → store in pending → send via transport
    ///   Receive: check sequence → if new, deliver + send ACK → if duplicate, re-ACK
    ///   Update:  check pending for timeout → retransmit if needed
    ///
    /// Internal framing (prepended to reliable data):
    ///   0xFE = RELIABLE_DATA  [seq:uint32 LE][payload…]
    ///   0xFF = ACK             [seq:uint32 LE]
    /// </summary>
    public class ReliabilityLayer
    {
        private const string Tag = "REL";

        // Internal packet type markers (never collide with PacketType enum which is 0–102)
        private const byte RELIABLE_DATA = 0xFE;
        private const byte ACK          = 0xFF;
        private const byte RELIABLE_FRAGMENT = 0xFD;

        // ACK packet is always: marker(1) + seq(4) = 5 bytes
        private const int AckPacketSize = 5;
        // Reliable header: marker(1) + seq(4) = 5 bytes before payload
        private const int ReliableHeaderSize = 5;
        // Fragment payload header: marker(1) + transferId(4) + index(4) + count(4) + totalBytes(4)
        private const int FragmentHeaderSize = 17;
        private const int TransportEnvelopeBytes = 16;
        private const int MinFragmentChunkBytes = 128;
        private const int MaxFragmentCount = 8192;
        private const int MaxFragmentedPayloadBytes = 128 * 1024 * 1024;

        // Dedup window: sequences within [highestReceived - WindowSize + 1, highestReceived] are tracked
        private const int DedupWindowSize = 8192;

        private readonly ITransport _transport;
        private readonly TransportConfig _config;

        // ── Per-peer send state ───────────────────────────────────────
        private readonly Dictionary<ulong, uint> _sendSequences = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, uint> _sendTransferIds = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, Dictionary<uint, PendingPacket>> _pendingByPeer =
            new Dictionary<ulong, Dictionary<uint, PendingPacket>>();

        // ── Per-peer receive state ────────────────────────────────────
        private readonly Dictionary<ulong, ReceiveState> _receiveStates = new Dictionary<ulong, ReceiveState>();
        private readonly Dictionary<ulong, Dictionary<uint, FragmentTransfer>> _incomingFragments =
            new Dictionary<ulong, Dictionary<uint, FragmentTransfer>>();

        private long _totalReliableSent;
        private long _totalReliableAcked;
        private long _totalReliableRetransmitted;
        private long _totalReliableDropped;

        /// <summary>
        /// Fired when processed data is ready for the application layer.
        /// Args: (peerId, data) where data is the original payload (no reliability framing).
        /// </summary>
        public event Action<ulong, byte[]> OnDataReady;

        public ReliabilityLayer(ITransport transport, TransportConfig config)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        // ═══════════════════════════════════════════════════════════════
        //  Send API
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Send data reliably to a specific peer (will retransmit until ACKed).</summary>
        public void SendReliable(ulong peerId, byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                Log.Warning(Tag, "SendReliable called with null/empty data");
                return;
            }

            int maxReliablePayloadBytes = GetMaxReliablePayloadBytes();
            if (data.Length > maxReliablePayloadBytes)
            {
                SendReliableFragmented(peerId, data, maxReliablePayloadBytes);
                return;
            }

            SendReliableFrame(peerId, data);
        }

        private void SendReliableFrame(ulong peerId, byte[] data)
        {
            uint seq = NextSendSequence(peerId);
            byte[] framed = FrameReliable(seq, data);

            // Store for retransmit
            if (!_pendingByPeer.TryGetValue(peerId, out var pending))
            {
                pending = new Dictionary<uint, PendingPacket>();
                _pendingByPeer[peerId] = pending;
            }

            pending[seq] = new PendingPacket
            {
                PeerId = peerId,
                SequenceNumber = seq,
                FramedData = framed,
                RawData = data,
                TimeSinceSend = 0f,
                RetryCount = 0
            };

            Interlocked.Increment(ref _totalReliableSent);
            _transport.Send(peerId, framed, false); // send raw over UDP, we handle reliability
        }

        private void SendReliableFragmented(ulong peerId, byte[] data, int maxReliablePayloadBytes)
        {
            int chunkSize = Math.Max(MinFragmentChunkBytes, maxReliablePayloadBytes - FragmentHeaderSize);
            int fragmentCount = (data.Length + chunkSize - 1) / chunkSize;
            if (fragmentCount > MaxFragmentCount)
            {
                Log.Warning(Tag, $"Reliable payload to peer {peerId} is too large to fragment: " +
                                 $"{data.Length} bytes in {fragmentCount} fragments");
                return;
            }

            uint transferId = NextTransferId(peerId);
            Log.Info(Tag, $"Fragmenting reliable payload to peer {peerId}: " +
                          $"{data.Length} bytes in {fragmentCount} fragments");

            for (int index = 0; index < fragmentCount; index++)
            {
                int offset = index * chunkSize;
                int length = Math.Min(chunkSize, data.Length - offset);
                var fragment = new byte[FragmentHeaderSize + length];
                fragment[0] = RELIABLE_FRAGMENT;
                WriteUInt32LE(fragment, 1, transferId);
                WriteInt32LE(fragment, 5, index);
                WriteInt32LE(fragment, 9, fragmentCount);
                WriteInt32LE(fragment, 13, data.Length);
                Buffer.BlockCopy(data, offset, fragment, FragmentHeaderSize, length);

                SendReliableFrame(peerId, fragment);
            }
        }

        /// <summary>Send data unreliably to a specific peer (pass-through).</summary>
        public void SendUnreliable(ulong peerId, byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                Log.Warning(Tag, "SendUnreliable called with null/empty data");
                return;
            }

            _transport.Send(peerId, data, false);
        }

        /// <summary>Broadcast data reliably to all connected peers.</summary>
        public void BroadcastReliable(byte[] data, ulong? except = null)
        {
            if (data == null || data.Length == 0)
            {
                Log.Warning(Tag, "BroadcastReliable called with null/empty data");
                return;
            }

            foreach (var peerId in _transport.ConnectedPeers)
            {
                if (except.HasValue && peerId == except.Value)
                    continue;
                SendReliable(peerId, data);
            }
        }

        /// <summary>Broadcast data unreliably to all connected peers.</summary>
        public void BroadcastUnreliable(byte[] data, ulong? except = null)
        {
            if (data == null || data.Length == 0)
            {
                Log.Warning(Tag, "BroadcastUnreliable called with null/empty data");
                return;
            }

            _transport.Broadcast(data, false, except);
        }

        // ═══════════════════════════════════════════════════════════════
        //  Receive path
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Called when the transport delivers raw data.
        /// Inspects the first byte — if it's a reliability marker (0xFE/0xFF),
        /// process it; otherwise forward to application as-is (unreliable passthrough).
        /// </summary>
        public void HandleReceived(ulong peerId, byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            byte marker = data[0];

            switch (marker)
            {
                case RELIABLE_DATA:
                    HandleReliableData(peerId, data);
                    break;

                case ACK:
                    HandleAck(peerId, data);
                    break;

                default:
                    // Not a reliability-layer packet — pass through to application
                    OnDataReady?.Invoke(peerId, data);
                    break;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Update (called every frame)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Called every frame. Checks pending reliable packets for timeout and retransmits.
        /// </summary>
        public void Update(float deltaTime)
        {
            List<(ulong peerId, uint seq)> toRemove = null;
            int retransmitsThisUpdate = 0;
            int retransmitBudget = _config.MaxReliableRetransmitsPerUpdate;

            foreach (var kvp in _pendingByPeer)
            {
                ulong peerId = kvp.Key;
                var pending = kvp.Value;

                foreach (var pkvp in pending)
                {
                    var packet = pkvp.Value;
                    packet.TimeSinceSend += deltaTime;

                    if (packet.TimeSinceSend < _config.RetransmitInterval)
                        continue;

                    if (packet.RetryCount >= _config.MaxRetransmitAttempts)
                    {
                        Log.Warning(Tag,
                            $"Reliable packet seq={packet.SequenceNumber} to peer {peerId} " +
                            $"dropped after {_config.MaxRetransmitAttempts} retries. " +
                            "If the peer appears connected but never ACKs, check UDP route/firewall/NAT.");

                        if (toRemove == null)
                            toRemove = new List<(ulong, uint)>();
                        toRemove.Add((peerId, pkvp.Key));
                        Interlocked.Increment(ref _totalReliableDropped);
                    }
                    else
                    {
                        if (retransmitBudget > 0 && retransmitsThisUpdate >= retransmitBudget)
                            continue;

                        packet.RetryCount++;
                        packet.TimeSinceSend = 0f;
                        retransmitsThisUpdate++;
                        Interlocked.Increment(ref _totalReliableRetransmitted);
                        _transport.Send(peerId, packet.FramedData, false);

                        if (packet.RetryCount == 5 || packet.RetryCount % 25 == 0)
                        {
                            Log.Warning(Tag,
                                $"Reliable packet seq={packet.SequenceNumber} to peer {peerId} still unacked " +
                                $"after {packet.RetryCount}/{_config.MaxRetransmitAttempts} attempts");
                        }
                        else
                        {
                            Log.Debug(Tag,
                                $"Retransmit seq={packet.SequenceNumber} to peer {peerId}, " +
                                $"attempt {packet.RetryCount}/{_config.MaxRetransmitAttempts}");
                        }
                    }
                }
            }

            if (toRemove != null)
            {
                foreach (var (peerId, seq) in toRemove)
                {
                    if (_pendingByPeer.TryGetValue(peerId, out var pending))
                    {
                        pending.Remove(seq);
                    }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Peer cleanup
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Remove all state for a disconnected peer.
        /// </summary>
        public void RemovePeer(ulong peerId)
        {
            _sendSequences.Remove(peerId);
            _sendTransferIds.Remove(peerId);
            _pendingByPeer.Remove(peerId);
            _receiveStates.Remove(peerId);
            _incomingFragments.Remove(peerId);
        }

        /// <summary>
        /// Remove all reliability state across peers for a fresh session boundary.
        /// </summary>
        public void Clear()
        {
            _sendSequences.Clear();
            _sendTransferIds.Clear();
            _pendingByPeer.Clear();
            _receiveStates.Clear();
            _incomingFragments.Clear();
        }

        /// <summary>Number of reliable packets awaiting ACK for a peer.</summary>
        public int GetPendingCount(ulong peerId)
        {
            return _pendingByPeer.TryGetValue(peerId, out var pending) ? pending.Count : 0;
        }

        public int GetTotalPendingCount()
        {
            int total = 0;
            foreach (var pending in _pendingByPeer.Values)
                total += pending.Count;
            return total;
        }

        public ReliabilityStats GetStats()
        {
            return new ReliabilityStats
            {
                PendingCount = GetTotalPendingCount(),
                ReliableSent = Interlocked.Read(ref _totalReliableSent),
                ReliableAcked = Interlocked.Read(ref _totalReliableAcked),
                ReliableRetransmitted = Interlocked.Read(ref _totalReliableRetransmitted),
                ReliableDropped = Interlocked.Read(ref _totalReliableDropped)
            };
        }

        // ═══════════════════════════════════════════════════════════════
        //  Internals
        // ═══════════════════════════════════════════════════════════════

        private void HandleReliableData(ulong peerId, byte[] data)
        {
            // Minimum: marker(1) + seq(4) + at least 1 byte payload
            if (data.Length < ReliableHeaderSize + 1)
            {
                Log.Warning(Tag, $"Truncated reliable packet from peer {peerId}: {data.Length} bytes");
                return;
            }

            uint seq = BitConverter.ToUInt32(data, 1);

            // Always ACK (even duplicates, in case our previous ACK was lost)
            SendAck(peerId, seq);

            // Dedup check
            if (!_receiveStates.TryGetValue(peerId, out var state))
            {
                state = new ReceiveState();
                _receiveStates[peerId] = state;
            }

            if (state.IsDuplicate(seq))
            {
                Log.Debug(Tag, $"Duplicate reliable seq={seq} from peer {peerId}, re-ACKed");
                return;
            }

            state.MarkReceived(seq);

            // Extract payload (strip marker + seq header)
            byte[] payload = new byte[data.Length - ReliableHeaderSize];
            Array.Copy(data, ReliableHeaderSize, payload, 0, payload.Length);

            if (payload.Length > 0 && payload[0] == RELIABLE_FRAGMENT)
            {
                HandleReliableFragment(peerId, payload);
                return;
            }

            OnDataReady?.Invoke(peerId, payload);
        }

        private void HandleReliableFragment(ulong peerId, byte[] data)
        {
            if (data.Length < FragmentHeaderSize)
            {
                Log.Warning(Tag, $"Truncated reliable fragment from peer {peerId}: {data.Length} bytes");
                return;
            }

            uint transferId = ReadUInt32LE(data, 1);
            int chunkIndex = ReadInt32LE(data, 5);
            int chunkCount = ReadInt32LE(data, 9);
            int totalBytes = ReadInt32LE(data, 13);

            if (transferId == 0
                || chunkIndex < 0
                || chunkCount <= 0
                || chunkCount > MaxFragmentCount
                || chunkIndex >= chunkCount
                || totalBytes <= 0
                || totalBytes > MaxFragmentedPayloadBytes)
            {
                Log.Warning(Tag, $"Rejected malformed reliable fragment from peer {peerId}");
                return;
            }

            int chunkBytes = data.Length - FragmentHeaderSize;
            if (!_incomingFragments.TryGetValue(peerId, out var transfers))
            {
                transfers = new Dictionary<uint, FragmentTransfer>();
                _incomingFragments[peerId] = transfers;
            }

            if (!transfers.TryGetValue(transferId, out var transfer))
            {
                transfer = new FragmentTransfer(transferId, chunkCount, totalBytes);
                transfers[transferId] = transfer;
                Log.Info(Tag, $"Receiving fragmented reliable payload from peer {peerId}: " +
                              $"{totalBytes} bytes in {chunkCount} fragments");
            }

            if (!transfer.TryAdd(chunkIndex, data, FragmentHeaderSize, chunkBytes))
                return;

            if (!transfer.IsComplete)
                return;

            transfers.Remove(transferId);
            OnDataReady?.Invoke(peerId, transfer.Assemble());
        }

        private void HandleAck(ulong peerId, byte[] data)
        {
            if (data.Length < AckPacketSize)
            {
                Log.Warning(Tag, $"Truncated ACK from peer {peerId}: {data.Length} bytes");
                return;
            }

            uint seq = BitConverter.ToUInt32(data, 1);

            if (_pendingByPeer.TryGetValue(peerId, out var pending))
            {
                if (pending.Remove(seq))
                {
                    Interlocked.Increment(ref _totalReliableAcked);
                    Log.Debug(Tag, $"ACK received seq={seq} from peer {peerId}");
                }
            }
        }

        private void SendAck(ulong peerId, uint seq)
        {
            byte[] ack = new byte[AckPacketSize];
            ack[0] = ACK;
            WriteUInt32LE(ack, 1, seq);
            _transport.Send(peerId, ack, false); // ACKs are always unreliable
        }

        private uint NextSendSequence(ulong peerId)
        {
            if (!_sendSequences.TryGetValue(peerId, out uint seq))
            {
                seq = 0;
            }

            _sendSequences[peerId] = seq + 1; // uint wraps naturally from uint.MaxValue → 0
            return seq;
        }

        private uint NextTransferId(ulong peerId)
        {
            if (!_sendTransferIds.TryGetValue(peerId, out uint id))
                id = 1;

            _sendTransferIds[peerId] = id == uint.MaxValue ? 1 : id + 1;
            return id;
        }

        private int GetMaxReliablePayloadBytes()
        {
            int maxPacketSize = _config.MaxPacketSize > 0 ? _config.MaxPacketSize : 1400;
            return Math.Max(MinFragmentChunkBytes, maxPacketSize - TransportEnvelopeBytes - ReliableHeaderSize);
        }

        private static byte[] FrameReliable(uint seq, byte[] payload)
        {
            byte[] framed = new byte[ReliableHeaderSize + payload.Length];
            framed[0] = RELIABLE_DATA;
            WriteUInt32LE(framed, 1, seq);
            Array.Copy(payload, 0, framed, ReliableHeaderSize, payload.Length);
            return framed;
        }

        private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
        {
            buffer[offset]     = (byte)(value);
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt32LE(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        private static void WriteInt32LE(byte[] buffer, int offset, int value)
        {
            WriteUInt32LE(buffer, offset, unchecked((uint)value));
        }

        private static int ReadInt32LE(byte[] buffer, int offset)
        {
            return unchecked((int)ReadUInt32LE(buffer, offset));
        }

        // ═══════════════════════════════════════════════════════════════
        //  Internal types
        // ═══════════════════════════════════════════════════════════════

        private class PendingPacket
        {
            public ulong PeerId;
            public uint SequenceNumber;
            public byte[] FramedData;   // Full wire packet (marker + seq + payload)
            public byte[] RawData;      // Original payload (for diagnostics)
            public float TimeSinceSend;
            public int RetryCount;
        }

        private sealed class FragmentTransfer
        {
            private readonly byte[][] _chunks;
            private int _receivedCount;

            public FragmentTransfer(uint transferId, int chunkCount, int totalBytes)
            {
                TransferId = transferId;
                ChunkCount = chunkCount;
                TotalBytes = totalBytes;
                _chunks = new byte[chunkCount][];
            }

            public uint TransferId { get; }
            public int ChunkCount { get; }
            public int TotalBytes { get; }
            public bool IsComplete => _receivedCount == ChunkCount;

            public bool TryAdd(int index, byte[] source, int offset, int length)
            {
                if (index < 0 || index >= ChunkCount || source == null || length < 0)
                    return false;
                if (_chunks[index] != null)
                    return false;

                var chunk = new byte[length];
                if (length > 0)
                    Buffer.BlockCopy(source, offset, chunk, 0, length);
                _chunks[index] = chunk;
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
                        throw new InvalidOperationException("Reliable fragment transfer is incomplete");
                    if (offset + chunk.Length > result.Length)
                        throw new InvalidOperationException("Reliable fragment transfer overflow");

                    Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                    offset += chunk.Length;
                }

                if (offset != result.Length)
                    throw new InvalidOperationException("Reliable fragment transfer size mismatch");

                return result;
            }
        }

        public struct ReliabilityStats
        {
            public int PendingCount;
            public long ReliableSent;
            public long ReliableAcked;
            public long ReliableRetransmitted;
            public long ReliableDropped;
        }

        /// <summary>
        /// Tracks received sequence numbers for a single peer.
        /// Uses a sliding window to bound memory usage.
        ///
        /// Window logic:
        ///   - Track highest received sequence
        ///   - Keep a HashSet of sequences within [highest - WindowSize + 1, highest]
        ///   - Sequences below the window are assumed already received (stale)
        ///   - Sequences above the window advance it
        ///
        /// Wraparound: uses signed distance (int cast of uint difference)
        /// to correctly handle uint overflow at the 32-bit boundary.
        /// </summary>
        private class ReceiveState
        {
            private uint _highestSeq;
            private bool _initialized;
            private readonly HashSet<uint> _receivedSet = new HashSet<uint>();

            /// <summary>
            /// Signed distance from <paramref name="a"/> to <paramref name="b"/>.
            /// Positive when b is "ahead" of a, negative when behind.
            /// Handles uint wraparound correctly (e.g. a=0xFFFFFFFF, b=0x00000000 → +1).
            /// </summary>
            private static int SequenceDistance(uint a, uint b)
            {
                return (int)(b - a);
            }

            /// <summary>
            /// Returns true if this sequence has already been processed
            /// or is too old (below the sliding window).
            /// </summary>
            public bool IsDuplicate(uint seq)
            {
                if (!_initialized)
                    return false;

                int dist = SequenceDistance(_highestSeq, seq);

                // seq is far behind the window — treat as already received
                if (dist < -(DedupWindowSize - 1))
                    return true;

                // Within window — check if we've seen it
                return _receivedSet.Contains(seq);
            }

            /// <summary>
            /// Mark a sequence as received and advance the window if needed.
            /// </summary>
            public void MarkReceived(uint seq)
            {
                if (!_initialized)
                {
                    _highestSeq = seq;
                    _initialized = true;
                    _receivedSet.Add(seq);
                    return;
                }

                int dist = SequenceDistance(_highestSeq, seq);

                if (dist > 0)
                {
                    // New highest — advance window
                    _highestSeq = seq;
                    PruneWindow();
                }

                _receivedSet.Add(seq);
            }

            /// <summary>
            /// Remove entries that have fallen outside the dedup window.
            /// </summary>
            private void PruneWindow()
            {
                if (_receivedSet.Count <= DedupWindowSize)
                    return;

                // Remove sequences that are below the window floor
                var toRemove = new List<uint>();
                foreach (uint s in _receivedSet)
                {
                    int dist = SequenceDistance(_highestSeq, s);
                    if (dist < -(DedupWindowSize - 1))
                    {
                        toRemove.Add(s);
                    }
                }

                foreach (uint s in toRemove)
                {
                    _receivedSet.Remove(s);
                }
            }
        }
    }
}
