using System;
using System.Collections.Generic;
using System.Linq;
using TCAMultiplayer.Transport;

namespace TCAMultiplayer.Tests.TestDoubles
{
    /// <summary>
    /// Deterministic clock for frame-driven networking tests.
    /// Native gameplay advances visible state from Update/FixedUpdate, so this clock only
    /// moves when tests explicitly advance it.
    /// </summary>
    public sealed class FakeNetworkClock
    {
        public double TimeSeconds { get; private set; }
        public int FrameCount { get; private set; }

        public void Advance(double deltaSeconds)
        {
            if (deltaSeconds < 0.0)
                throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "Time cannot move backwards.");

            TimeSeconds += deltaSeconds;
        }

        public void AdvanceFrame(double deltaSeconds = 1.0 / 60.0)
        {
            Advance(deltaSeconds);
            FrameCount++;
        }

        public void AdvanceFixedStep(double fixedDeltaSeconds = 0.02)
        {
            Advance(fixedDeltaSeconds);
            FrameCount++;
        }
    }

    public sealed class PacketChaosSettings
    {
        public int Seed { get; set; } = 12345;
        public double LossRate { get; set; }
        public double DuplicateRate { get; set; }
        public double MinimumLatencySeconds { get; set; }
        public double MaximumLatencySeconds { get; set; }
        public double ReorderWindowSeconds { get; set; }

        /// <summary>
        /// Optional deterministic per-packet override. Useful for tests that need exact
        /// loss/duplication/delay expectations without relying on random thresholds.
        /// </summary>
        public Func<FakePacketInfo, PacketChaosDecision> DecidePacket { get; set; }
    }

    public sealed class FakePacketInfo
    {
        public ulong SourcePeerId { get; internal set; }
        public ulong TargetPeerId { get; internal set; }
        public byte[] Data { get; internal set; }
        public bool Reliable { get; internal set; }
        public long SendOrdinal { get; internal set; }
        public int CopyIndex { get; internal set; }
        public double SendTimeSeconds { get; internal set; }
    }

    public sealed class PacketChaosDecision
    {
        public bool Drop { get; set; }
        public int ExtraCopies { get; set; }
        public double? DelaySeconds { get; set; }
    }

    public sealed class FakeNetworkHarness
    {
        private readonly PacketChaosSettings _chaos;
        private readonly Random _random;
        private readonly Dictionary<ulong, FakeNetworkTransport> _transports =
            new Dictionary<ulong, FakeNetworkTransport>();
        private readonly List<ScheduledPacket> _scheduledPackets = new List<ScheduledPacket>();
        private readonly Dictionary<int, FakeNetworkTransport> _hostsByPort =
            new Dictionary<int, FakeNetworkTransport>();

        private ulong _nextClientPeerId = 2;
        private long _nextSendOrdinal;
        private long _nextDeliveryOrdinal;

        public FakeNetworkHarness(PacketChaosSettings chaos = null)
        {
            _chaos = chaos ?? new PacketChaosSettings();
            _random = new Random(_chaos.Seed);
            Clock = new FakeNetworkClock();
        }

        public FakeNetworkClock Clock { get; }
        public IReadOnlyCollection<FakeNetworkTransport> Transports => _transports.Values.ToList();

        public FakeNetworkTransport CreateTransport(ulong localPeerId, bool isHost = false)
        {
            if (_transports.ContainsKey(localPeerId))
                throw new InvalidOperationException($"Peer {localPeerId} already exists.");

            var transport = new FakeNetworkTransport(this, localPeerId, isHost);
            _transports.Add(localPeerId, transport);
            return transport;
        }

        public FakeNetworkTransport CreateHost(ulong localPeerId = 1)
        {
            return CreateTransport(localPeerId, isHost: true);
        }

        public FakeNetworkTransport CreateClient(ulong localPeerId = 0)
        {
            if (localPeerId == 0)
                localPeerId = _nextClientPeerId++;

            if (localPeerId >= _nextClientPeerId)
                _nextClientPeerId = localPeerId + 1;

            return CreateTransport(localPeerId, isHost: false);
        }

        public void Connect(FakeNetworkTransport first, FakeNetworkTransport second)
        {
            if (first == null) throw new ArgumentNullException(nameof(first));
            if (second == null) throw new ArgumentNullException(nameof(second));

            first.MarkConnectedTo(second.LocalPeerId);
            second.MarkConnectedTo(first.LocalPeerId);
        }

        public void Advance(double deltaSeconds)
        {
            Clock.Advance(deltaSeconds);
        }

        public void AdvanceFrame(double deltaSeconds = 1.0 / 60.0)
        {
            Clock.AdvanceFrame(deltaSeconds);
        }

        public void UpdateAll()
        {
            foreach (var transport in _transports.Values.OrderBy(t => t.LocalPeerId))
                transport.Update();
        }

        internal void RegisterHost(FakeNetworkTransport host, int port)
        {
            _hostsByPort[port] = host;
        }

        internal void ConnectToHost(FakeNetworkTransport client, int port)
        {
            if (!_hostsByPort.TryGetValue(port, out var host))
                throw new InvalidOperationException($"No fake host is listening on port {port}.");

            Connect(host, client);
        }

        internal void ScheduleSend(ulong sourcePeerId, ulong targetPeerId, byte[] data, bool reliable)
        {
            if (data == null || data.Length == 0)
                return;

            if (!_transports.ContainsKey(targetPeerId))
                return;

            long sendOrdinal = _nextSendOrdinal++;
            var baseInfo = new FakePacketInfo
            {
                SourcePeerId = sourcePeerId,
                TargetPeerId = targetPeerId,
                Data = CopyBytes(data),
                Reliable = reliable,
                SendOrdinal = sendOrdinal,
                CopyIndex = 0,
                SendTimeSeconds = Clock.TimeSeconds
            };

            PacketChaosDecision decision = _chaos.DecidePacket?.Invoke(baseInfo);
            if (decision?.Drop == true || ShouldHappen(_chaos.LossRate))
                return;

            int extraCopies = Math.Max(0, decision?.ExtraCopies ?? 0);
            if (ShouldHappen(_chaos.DuplicateRate))
                extraCopies++;

            int totalCopies = 1 + extraCopies;
            for (int copyIndex = 0; copyIndex < totalCopies; copyIndex++)
            {
                var copyInfo = new FakePacketInfo
                {
                    SourcePeerId = sourcePeerId,
                    TargetPeerId = targetPeerId,
                    Data = CopyBytes(data),
                    Reliable = reliable,
                    SendOrdinal = sendOrdinal,
                    CopyIndex = copyIndex,
                    SendTimeSeconds = Clock.TimeSeconds
                };

                double delay = decision?.DelaySeconds ?? PickLatencySeconds();
                if (_chaos.ReorderWindowSeconds > 0.0)
                    delay += _random.NextDouble() * _chaos.ReorderWindowSeconds;

                _scheduledPackets.Add(new ScheduledPacket
                {
                    SourcePeerId = sourcePeerId,
                    TargetPeerId = targetPeerId,
                    Data = CopyBytes(copyInfo.Data),
                    Reliable = reliable,
                    DeliverAtSeconds = Clock.TimeSeconds + Math.Max(0.0, delay),
                    DeliveryOrdinal = _nextDeliveryOrdinal++
                });
            }
        }

        internal void DeliverDuePackets()
        {
            if (_scheduledPackets.Count == 0)
                return;

            var due = _scheduledPackets
                .Where(packet => packet.DeliverAtSeconds <= Clock.TimeSeconds)
                .OrderBy(packet => packet.DeliverAtSeconds)
                .ThenBy(packet => packet.DeliveryOrdinal)
                .ToList();

            if (due.Count == 0)
                return;

            foreach (var packet in due)
                _scheduledPackets.Remove(packet);

            foreach (var packet in due)
            {
                if (_transports.TryGetValue(packet.TargetPeerId, out var target))
                    target.EnqueueData(packet.SourcePeerId, CopyBytes(packet.Data));
            }
        }

        private double PickLatencySeconds()
        {
            double min = Math.Max(0.0, _chaos.MinimumLatencySeconds);
            double max = Math.Max(min, _chaos.MaximumLatencySeconds);
            if (max <= min)
                return min;

            return min + (_random.NextDouble() * (max - min));
        }

        private bool ShouldHappen(double probability)
        {
            if (probability <= 0.0)
                return false;
            if (probability >= 1.0)
                return true;

            return _random.NextDouble() < probability;
        }

        private static byte[] CopyBytes(byte[] data)
        {
            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            return copy;
        }

        private sealed class ScheduledPacket
        {
            public ulong SourcePeerId;
            public ulong TargetPeerId;
            public byte[] Data;
            public bool Reliable;
            public double DeliverAtSeconds;
            public long DeliveryOrdinal;
        }
    }

    public sealed class FakeNetworkTransport : ITransport
    {
        private readonly FakeNetworkHarness _network;
        private readonly Queue<TransportEvent> _eventQueue = new Queue<TransportEvent>();
        private readonly HashSet<ulong> _connectedPeers = new HashSet<ulong>();
        private bool _disposed;

        internal FakeNetworkTransport(FakeNetworkHarness network, ulong localPeerId, bool isHost)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
            LocalPeerId = localPeerId;
            IsHost = isHost;
            IsConnected = isHost;
        }

        public event Action<ulong, byte[]> OnDataReceived;
        public event Action<ulong> OnPeerConnected;
        public event Action<ulong> OnPeerDisconnected;

        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public ulong LocalPeerId { get; private set; }
        public IReadOnlyCollection<ulong> ConnectedPeers => _connectedPeers.ToList();

        public void StartHost(int port)
        {
            ThrowIfDisposed();
            IsHost = true;
            IsConnected = true;
            if (LocalPeerId == 0)
                LocalPeerId = 1;

            _network.RegisterHost(this, port);
        }

        public void Connect(string address, int port)
        {
            ThrowIfDisposed();
            IsHost = false;
            IsConnected = true;
            _network.ConnectToHost(this, port);
        }

        public void Disconnect()
        {
            if (_disposed || !IsConnected)
                return;

            foreach (ulong peerId in _connectedPeers.ToList())
                _eventQueue.Enqueue(TransportEvent.PeerDisconnected(peerId));

            _connectedPeers.Clear();
            IsConnected = false;
        }

        public void DisconnectPeer(ulong peerId)
        {
            ThrowIfDisposed();
            if (peerId == 0)
                return;

            if (_connectedPeers.Remove(peerId))
                _eventQueue.Enqueue(TransportEvent.PeerDisconnected(peerId));

            if (!IsHost && peerId == 1)
                IsConnected = false;
        }

        public void Send(ulong peerId, byte[] data, bool reliable)
        {
            ThrowIfDisposed();
            if (!IsConnected || !_connectedPeers.Contains(peerId))
                return;

            _network.ScheduleSend(LocalPeerId, peerId, data, reliable);
        }

        public void Broadcast(byte[] data, bool reliable, ulong? except = null)
        {
            ThrowIfDisposed();
            if (!IsConnected)
                return;

            foreach (ulong peerId in _connectedPeers.ToList())
            {
                if (except.HasValue && except.Value == peerId)
                    continue;

                _network.ScheduleSend(LocalPeerId, peerId, data, reliable);
            }
        }

        public void Update()
        {
            ThrowIfDisposed();

            _network.DeliverDuePackets();

            while (_eventQueue.Count > 0)
            {
                TransportEvent transportEvent = _eventQueue.Dequeue();
                switch (transportEvent.Type)
                {
                    case TransportEventType.DataReceived:
                        OnDataReceived?.Invoke(transportEvent.PeerId, transportEvent.Data);
                        break;
                    case TransportEventType.PeerConnected:
                        OnPeerConnected?.Invoke(transportEvent.PeerId);
                        break;
                    case TransportEventType.PeerDisconnected:
                        OnPeerDisconnected?.Invoke(transportEvent.PeerId);
                        break;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Disconnect();
            _eventQueue.Clear();
            _disposed = true;
        }

        internal void MarkConnectedTo(ulong peerId)
        {
            if (_connectedPeers.Add(peerId))
                _eventQueue.Enqueue(TransportEvent.PeerConnected(peerId));

            IsConnected = true;
        }

        internal void EnqueueData(ulong peerId, byte[] data)
        {
            if (!_connectedPeers.Contains(peerId))
                return;

            _eventQueue.Enqueue(TransportEvent.DataReceived(peerId, data));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FakeNetworkTransport));
        }
    }
}
