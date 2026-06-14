using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BepInEx.Logging;
using NUnit.Framework;
using TCAMultiplayer.Core;
using TCAMultiplayer.Transport;

namespace TCAMultiplayer.Tests
{
    /// <summary>
    /// Loopback UDP tests for the reconnect grace window in DirectUdpTransport.
    /// A peer that goes silent past TimeoutSeconds must NOT be reported as
    /// disconnected until ReconnectGraceSeconds also elapses; if traffic resumes
    /// inside the window the connection survives. An explicit DISCONNECT packet
    /// still takes effect immediately.
    ///
    /// These tests use real sockets and wall-clock time with wide margins
    /// (grace window 3x the silence we inject), so they run serially.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class TransportReconnectTests
    {
        private const byte PktData = 0x00;
        private const byte PktConnect = 0x01;
        private const byte PktConnectAck = 0x02;
        private const byte PktDataV2 = 0x06;
        private static readonly byte[] ConnectMagic = { 0x54, 0x43, 0x41, 0x4D };

        private static int _nextPort = 27801;

        [OneTimeSetUp]
        public void SetUpLogging()
        {
            if (!Log.IsInitialized)
                Log.Init(new ManualLogSource("Test"));
        }

        private static TransportConfig MakeConfig(ulong clientToken = 0) => new TransportConfig
        {
            KeepaliveInterval = 0.05f,
            TimeoutSeconds = 0.4f,
            ReconnectGraceSeconds = 1.6f,
            EndpointRefreshInterval = 0f,
            ClientToken = clientToken,
        };

        private static TransportConfig MakeVersionConfig(string modVersion, ulong clientToken = 0)
        {
            var config = MakeConfig(clientToken);
            config.ModVersion = modVersion;
            return config;
        }

        /// <summary>Pump update loops for a fixed duration. Pass pumpB=false to simulate peer B going silent.</summary>
        private static void Pump(DirectUdpTransport a, DirectUdpTransport b, double seconds, bool pumpB = true)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                a.Update();
                if (pumpB) b.Update();
                Thread.Sleep(10);
            }
        }

        private static bool PumpUntil(Func<bool> condition, DirectUdpTransport a, DirectUdpTransport b, double timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                a.Update();
                b.Update();
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }

        private static bool PumpUntil(Func<bool> condition, DirectUdpTransport transport, double timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                transport.Update();
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }

        private static bool PumpUntil(Func<bool> condition, ConnectionManager a, ConnectionManager b, double timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                a.Update(0.01f);
                b.Update(0.01f);
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }

        private static TransportConfig MakeQuietConfig() => new TransportConfig
        {
            KeepaliveInterval = 30f,
            TimeoutSeconds = 30f,
            ReconnectGraceSeconds = 30f,
            EndpointRefreshInterval = 0f,
        };

        private static byte[] BuildLegacyConnect()
        {
            var packet = new byte[1 + ConnectMagic.Length];
            packet[0] = PktConnect;
            Buffer.BlockCopy(ConnectMagic, 0, packet, 1, ConnectMagic.Length);
            return packet;
        }

        private static byte[] BuildLegacyConnectAck(ulong assignedPeerId)
        {
            var packet = new byte[9];
            packet[0] = PktConnectAck;
            WriteUInt64LE(packet, 1, assignedPeerId);
            return packet;
        }

        private static byte[] ReceiveUdp(UdpClient socket, string description)
        {
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                return socket.Receive(ref endpoint);
            }
            catch (SocketException ex)
            {
                Assert.Fail($"{description}: {ex.SocketErrorCode}");
                return null;
            }
        }

        private static ulong ReadUInt64LE(byte[] buf, int offset)
        {
            return (ulong)buf[offset]
                 | ((ulong)buf[offset + 1] << 8)
                 | ((ulong)buf[offset + 2] << 16)
                 | ((ulong)buf[offset + 3] << 24)
                 | ((ulong)buf[offset + 4] << 32)
                 | ((ulong)buf[offset + 5] << 40)
                 | ((ulong)buf[offset + 6] << 48)
                 | ((ulong)buf[offset + 7] << 56);
        }

        private static void WriteUInt64LE(byte[] buf, int offset, ulong value)
        {
            buf[offset]     = (byte)(value);
            buf[offset + 1] = (byte)(value >> 8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
            buf[offset + 4] = (byte)(value >> 32);
            buf[offset + 5] = (byte)(value >> 40);
            buf[offset + 6] = (byte)(value >> 48);
            buf[offset + 7] = (byte)(value >> 56);
        }

        [Test]
        public void HostSilence_WithinGrace_ClientStaysConnectedAndRecovers()
        {
            int port = _nextPort++;
            var host = new DirectUdpTransport(MakeConfig());
            var client = new DirectUdpTransport(MakeConfig());
            var clientDisconnects = new List<ulong>();
            client.OnPeerDisconnected += id => clientDisconnects.Add(id);

            try
            {
                host.StartHost(port);
                client.Connect("127.0.0.1", port);
                Assert.IsTrue(PumpUntil(() => client.IsConnected, host, client, 5.0), "client failed to connect");

                // Host goes silent: past TimeoutSeconds (0.4s) but inside the grace window
                Pump(client, host, 0.7, pumpB: false);
                Assert.IsTrue(client.IsConnected, "client gave up during the grace window");
                Assert.IsEmpty(clientDisconnects, "client fired disconnect during the grace window");

                // Host resumes — connection must recover with no disconnect event
                Pump(client, host, 0.5);
                Assert.IsTrue(client.IsConnected, "client did not recover after host resumed");
                Assert.IsEmpty(clientDisconnects, "client disconnected despite recovery");

                // And stays healthy well past the original silence
                Pump(client, host, 0.6);
                Assert.IsEmpty(clientDisconnects, "client disconnected after recovery");
            }
            finally
            {
                client.Dispose();
                host.Dispose();
            }
        }

        [Test]
        public void HostSilence_PastGrace_ClientFinallyDisconnects()
        {
            int port = _nextPort++;
            var host = new DirectUdpTransport(MakeConfig());
            var client = new DirectUdpTransport(MakeConfig());
            var clientDisconnects = new List<ulong>();
            client.OnPeerDisconnected += id => clientDisconnects.Add(id);

            try
            {
                host.StartHost(port);
                client.Connect("127.0.0.1", port);
                Assert.IsTrue(PumpUntil(() => client.IsConnected, host, client, 5.0), "client failed to connect");

                // Host stays silent past TimeoutSeconds + ReconnectGraceSeconds (2.0s total)
                Pump(client, host, 2.6, pumpB: false);

                Assert.IsFalse(client.IsConnected, "client still connected after grace expired");
                CollectionAssert.Contains(clientDisconnects, 1UL, "client never reported the host as disconnected");
            }
            finally
            {
                client.Dispose();
                host.Dispose();
            }
        }

        [Test]
        public void ExplicitHostDisconnect_BypassesGrace()
        {
            int port = _nextPort++;
            var host = new DirectUdpTransport(MakeConfig());
            var client = new DirectUdpTransport(MakeConfig());
            var clientDisconnects = new List<ulong>();
            client.OnPeerDisconnected += id => clientDisconnects.Add(id);

            try
            {
                host.StartHost(port);
                client.Connect("127.0.0.1", port);
                Assert.IsTrue(PumpUntil(() => client.IsConnected, host, client, 5.0), "client failed to connect");

                // Host shuts down cleanly — sends DISCONNECT, no grace applies
                host.Disconnect();

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 1.0 && clientDisconnects.Count == 0)
                {
                    client.Update();
                    Thread.Sleep(10);
                }

                CollectionAssert.Contains(clientDisconnects, 1UL, "explicit DISCONNECT was not honored immediately");
                Assert.IsFalse(client.IsConnected);
            }
            finally
            {
                client.Dispose();
                host.Dispose();
            }
        }

        [Test]
        public void ClientSilence_WithinGrace_HostKeepsPeerRegistered()
        {
            int port = _nextPort++;
            var host = new DirectUdpTransport(MakeConfig());
            var client = new DirectUdpTransport(MakeConfig());
            var hostDisconnects = new List<ulong>();
            host.OnPeerDisconnected += id => hostDisconnects.Add(id);

            try
            {
                host.StartHost(port);
                client.Connect("127.0.0.1", port);
                Assert.IsTrue(PumpUntil(() => client.IsConnected, host, client, 5.0), "client failed to connect");

                // Client goes silent past TimeoutSeconds but inside the grace window
                Pump(host, client, 0.7, pumpB: false);
                Assert.IsEmpty(hostDisconnects, "host dropped the client during the grace window");
                Assert.AreEqual(1, host.ConnectedPeers.Count, "host removed the client during the grace window");

                // Client resumes — roster must be unchanged, no disconnect event
                Pump(host, client, 0.5);
                Assert.IsEmpty(hostDisconnects, "host disconnected the client despite recovery");
                Assert.AreEqual(1, host.ConnectedPeers.Count);
            }
            finally
            {
                client.Dispose();
                host.Dispose();
            }
        }

        [Test]
        public void SameClientToken_FromNewUdpEndpoint_ReusesPeerId()
        {
            int port = _nextPort++;
            const ulong token = 0x123456789ABCDEF0UL;
            var host = new DirectUdpTransport(MakeConfig());
            var firstClient = new DirectUdpTransport(MakeConfig(token));
            var migratedClient = new DirectUdpTransport(MakeConfig(token));
            var hostConnectedPeers = new List<ulong>();
            var received = new List<(ulong PeerId, byte[] Data)>();
            host.OnPeerConnected += id => hostConnectedPeers.Add(id);
            host.OnDataReceived += (id, data) => received.Add((id, data));

            try
            {
                host.StartHost(port);
                firstClient.Connect("127.0.0.1", port);
                Assert.IsTrue(PumpUntil(() => firstClient.IsConnected, host, firstClient, 5.0),
                    "first client failed to connect");
                Assert.AreEqual(2UL, firstClient.LocalPeerId);
                Assert.AreEqual(1, host.ConnectedPeers.Count);
                CollectionAssert.AreEqual(new[] { 2UL }, hostConnectedPeers);

                migratedClient.Connect("127.0.0.1", port);
                Assert.IsTrue(PumpUntil(() => migratedClient.IsConnected, host, migratedClient, 5.0),
                    "migrated client failed to connect");
                Assert.AreEqual(2UL, migratedClient.LocalPeerId,
                    "host assigned a new peer ID instead of recognizing the client token");
                Assert.AreEqual(1, host.ConnectedPeers.Count,
                    "host kept a ghost peer after endpoint migration");
                CollectionAssert.AreEqual(new[] { 2UL }, hostConnectedPeers,
                    "endpoint migration should not fire a second peer-connected event");

                var payload = new byte[] { 42, 24 };
                migratedClient.Send(1, payload, reliable: false);
                Assert.IsTrue(PumpUntil(() => received.Count > 0, host, migratedClient, 2.0),
                    "host did not receive data from migrated endpoint");
                Assert.AreEqual(2UL, received[0].PeerId);
                CollectionAssert.AreEqual(payload, received[0].Data);
            }
            finally
            {
                migratedClient.Dispose();
                firstClient.Dispose();
                host.Dispose();
            }
        }

        [Test]
        public void Connect_WithMatchingModVersion_CompletesHandshake()
        {
            int port = _nextPort++;
            var host = new DirectUdpTransport(MakeVersionConfig("0.2.1"));
            var client = new DirectUdpTransport(MakeVersionConfig("0.2.1"));

            try
            {
                host.StartHost(port);
                client.Connect("127.0.0.1", port);

                Assert.IsTrue(PumpUntil(() => client.IsConnected, host, client, 5.0),
                    "client failed to connect with matching mod version");
                Assert.AreEqual(2UL, client.LocalPeerId);
                Assert.AreEqual(1, host.ConnectedPeers.Count);
            }
            finally
            {
                client.Dispose();
                host.Dispose();
            }
        }

        [Test]
        public void Connect_WithMismatchedModVersion_IsRejectedBeforePeerJoins()
        {
            int port = _nextPort++;
            var host = new DirectUdpTransport(MakeVersionConfig("0.2.1"));
            var client = new DirectUdpTransport(MakeVersionConfig("0.3.0"));
            var clientStatuses = new List<string>();
            var hostConnectedPeers = new List<ulong>();
            client.OnStatusChanged += message => clientStatuses.Add(message);
            host.OnPeerConnected += id => hostConnectedPeers.Add(id);

            try
            {
                host.StartHost(port);
                client.Connect("127.0.0.1", port);

                Assert.IsTrue(PumpUntil(
                        () => clientStatuses.Exists(s => s.Contains("TCAMP version mismatch")),
                        host,
                        client,
                        5.0),
                    "client did not receive a version mismatch rejection");

                Assert.IsFalse(client.IsConnected, "client should not be connected after version rejection");
                Assert.AreEqual(0, host.ConnectedPeers.Count, "host accepted a mismatched-version peer");
                CollectionAssert.IsEmpty(hostConnectedPeers, "host fired peer-connected for a rejected client");
                Assert.IsTrue(clientStatuses.Exists(s => s.Contains("host 0.2.1, client 0.3.0")));
            }
            finally
            {
                client.Dispose();
                host.Dispose();
            }
        }

        [Test]
        public void JoinSession_WithMismatchedModVersion_PreservesRejectionStatusForUi()
        {
            int port = _nextPort++;
            var hostTransport = new DirectUdpTransport(MakeVersionConfig("0.2.1"));
            var clientTransport = new DirectUdpTransport(MakeVersionConfig("0.3.0"));

            using (var host = new ConnectionManager(hostTransport, MakeVersionConfig("0.2.1")))
            using (var client = new ConnectionManager(clientTransport, MakeVersionConfig("0.3.0")))
            {
                host.HostSession("Host", port);
                client.JoinSession("127.0.0.1", port);

                Assert.IsTrue(PumpUntil(
                        () => client.StatusMessage.Contains("TCAMP version mismatch"),
                        host,
                        client,
                        5.0),
                    "client UI status did not keep the version mismatch rejection");

                Assert.IsFalse(client.IsConnected, "client should not be connected after version rejection");
                Assert.IsFalse(client.HasSession, "client session should be torn down after version rejection");
                Assert.IsTrue(client.StatusMessage.Contains("host 0.2.1, client 0.3.0"));
                Assert.AreEqual(0, host.ConnectedPeers.Count, "host accepted a mismatched-version peer");
            }
        }

        [Test]
        public void LegacyClientHandshake_HostRespondsAndSendsLegacyDataFrames()
        {
            int port = _nextPort++;
            var host = new DirectUdpTransport(MakeQuietConfig());
            using (var legacyClient = new UdpClient(0))
            {
                legacyClient.Client.ReceiveTimeout = 2000;
                legacyClient.Connect(IPAddress.Loopback, port);

                try
                {
                    host.StartHost(port);
                    byte[] connect = BuildLegacyConnect();
                    legacyClient.Send(connect, connect.Length);

                    Assert.IsTrue(PumpUntil(() => host.ConnectedPeers.Count == 1, host, 2.0),
                        "host did not accept legacy CONNECT");

                    byte[] ack = ReceiveUdp(legacyClient, "legacy client did not receive CONNECT_ACK");
                    Assert.AreEqual(9, ack.Length, "legacy CONNECT_ACK must stay 9 bytes");
                    Assert.AreEqual(PktConnectAck, ack[0]);
                    Assert.AreEqual(2UL, ReadUInt64LE(ack, 1));

                    var payload = new byte[] { 7, 8, 9 };
                    host.Send(2, payload, reliable: false);

                    byte[] data = ReceiveUdp(legacyClient, "legacy client did not receive host data");
                    Assert.AreEqual(PktData, data[0], "host sent v2 data to a legacy client");
                    Assert.AreNotEqual(PktDataV2, data[0]);
                    Assert.AreEqual(2 + payload.Length, data.Length);
                    CollectionAssert.AreEqual(payload, new ArraySegment<byte>(data, 2, payload.Length));
                }
                finally
                {
                    host.Dispose();
                }
            }
        }

        [Test]
        public void LegacyHostAck_ClientFallsBackToLegacyDataFrames()
        {
            int port = _nextPort++;
            var client = new DirectUdpTransport(MakeQuietConfig());
            using (var legacyHost = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)))
            {
                legacyHost.Client.ReceiveTimeout = 2000;

                try
                {
                    client.Connect("127.0.0.1", port);

                    var clientEndpoint = new IPEndPoint(IPAddress.Any, 0);
                    byte[] connect = legacyHost.Receive(ref clientEndpoint);
                    Assert.AreEqual(PktConnect, connect[0]);

                    byte[] ack = BuildLegacyConnectAck(2UL);
                    legacyHost.Send(ack, ack.Length, clientEndpoint);

                    Assert.IsTrue(PumpUntil(() => client.IsConnected, client, 2.0),
                        "client did not accept legacy CONNECT_ACK");
                    Assert.AreEqual(2UL, client.LocalPeerId);

                    var payload = new byte[] { 10, 11, 12 };
                    client.Send(1, payload, reliable: false);

                    byte[] data = ReceiveUdp(legacyHost, "legacy host did not receive client data");
                    Assert.AreEqual(PktData, data[0], "client sent v2 data after a legacy CONNECT_ACK");
                    Assert.AreNotEqual(PktDataV2, data[0]);
                    Assert.AreEqual(2 + payload.Length, data.Length);
                    CollectionAssert.AreEqual(payload, new ArraySegment<byte>(data, 2, payload.Length));
                }
                finally
                {
                    client.Dispose();
                }
            }
        }

        [Test]
        public void LocalBindAddress_AllowsBindingToSelectedAdapterIp()
        {
            int port = _nextPort++;
            var host = new DirectUdpTransport(new TransportConfig
            {
                LocalBindAddress = "127.0.0.1",
                EndpointRefreshInterval = 0f,
            });
            var client = new DirectUdpTransport(new TransportConfig
            {
                LocalBindAddress = "127.0.0.1",
                EndpointRefreshInterval = 0f,
            });

            try
            {
                host.StartHost(port);
                client.Connect("127.0.0.1", port);
                Assert.IsTrue(PumpUntil(() => client.IsConnected, host, client, 5.0),
                    "client failed to connect over explicitly bound loopback adapter");
            }
            finally
            {
                client.Dispose();
                host.Dispose();
            }
        }

        [Test]
        public void AutoRoute_LoopbackConnect_UsesLoopbackAndReportsQuality()
        {
            int port = _nextPort++;
            var host = new DirectUdpTransport(MakeConfig());
            var client = new DirectUdpTransport(MakeConfig());
            var statuses = new List<string>();
            client.OnStatusChanged += message => statuses.Add(message);

            try
            {
                host.StartHost(port);
                client.Connect("127.0.0.1", port);
                Assert.IsTrue(PumpUntil(() => client.IsConnected, host, client, 5.0),
                    "client failed to connect over automatically selected loopback route");

                Pump(host, client, 0.2);
                var quality = client.GetNetworkQuality();

                Assert.IsTrue(quality.LocalEndpoint.StartsWith("127.0.0.1:", StringComparison.Ordinal),
                    "client did not bind to loopback for a loopback target");
                Assert.AreEqual("loopback route", quality.RouteDescription);
                Assert.AreEqual(1, quality.ConnectedPeerCount);
                Assert.IsTrue(quality.SmoothedRttMs >= 0f, "quality snapshot did not expose RTT");
                Assert.IsTrue(statuses.Exists(s => s.StartsWith("Connected to host at 127.0.0.1:", StringComparison.Ordinal)),
                    "client did not emit a clear connected status");
            }
            finally
            {
                client.Dispose();
                host.Dispose();
            }
        }
    }
}
