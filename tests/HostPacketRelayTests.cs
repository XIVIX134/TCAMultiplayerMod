using BepInEx.Logging;
using NUnit.Framework;
using TCAMultiplayer.Core;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.Tests.TestDoubles;
using TCAMultiplayer.Transport;

namespace TCAMultiplayer.Tests
{
    [TestFixture]
    public class HostPacketRelayTests
    {
        [OneTimeSetUp]
        public void SetUpLogging()
        {
            if (!Log.IsInitialized)
                Log.Init(new ManualLogSource("Test"));
        }

        [Test]
        public void HostRelay_RelaysClientAircraftState_ToOtherClientsOnly()
        {
            var network = new FakeNetworkHarness();
            var hostTransport = network.CreateHost(1);
            var firstClientTransport = network.CreateClient(2);
            var secondClientTransport = network.CreateClient(3);

            using (var hostConnection = new ConnectionManager(hostTransport, new TransportConfig { MaxConnections = 7 }))
            using (var firstClientConnection = new ConnectionManager(firstClientTransport))
            using (var secondClientConnection = new ConnectionManager(secondClientTransport))
            {
                hostConnection.HostSession("Host", 7777);
                firstClientConnection.JoinSession("127.0.0.1", 7777);
                secondClientConnection.JoinSession("127.0.0.1", 7777);
                Pump(network, hostConnection, firstClientConnection, secondClientConnection);

                using (new HostPacketRelay(hostConnection.Session, hostConnection, hostConnection.Router))
                {
                    int firstClientReceives = 0;
                    AircraftStatePacket relayed = default;
                    secondClientConnection.Router.Register(PacketType.AircraftState, (_, raw) =>
                    {
                        var (_, payload) = PacketSerializer.Deserialize(raw);
                        relayed = PacketSerializer.DeserializeAircraftState(payload);
                    });
                    firstClientConnection.Router.Register(PacketType.AircraftState, (_, __) => firstClientReceives++);

                    var state = new AircraftStatePacket
                    {
                        PlayerId = 2,
                        SequenceNumber = 42,
                        AircraftType = "AV8B",
                        RotW = 1f,
                        Timestamp = 12.5f
                    };
                    var frame = PacketSerializer.Serialize(
                        PacketType.AircraftState,
                        PacketSerializer.SerializeAircraftState(state));

                    firstClientConnection.BroadcastUnreliable(frame);
                    Pump(network, hostConnection, firstClientConnection, secondClientConnection);

                    Assert.AreEqual(0, firstClientReceives);
                    Assert.AreEqual(2UL, relayed.PlayerId);
                    Assert.AreEqual(42u, relayed.SequenceNumber);
                    Assert.AreEqual("AV8B", relayed.AircraftType);
                    Assert.AreEqual(12.5f, relayed.Timestamp, 0.001f);
                }
            }
        }

        [Test]
        public void HostRelay_AcceptsClientAircraftState_RegardlessOfPlayerId()
        {
            // PlayerId identity check is skipped for AircraftState because Steam transport
            // uses SteamIDs (ulong) for PlayerId while transport peer IDs are 1,2,3...
            var network = new FakeNetworkHarness();
            var hostTransport = network.CreateHost(1);
            var firstClientTransport = network.CreateClient(2);
            var secondClientTransport = network.CreateClient(3);

            using (var hostConnection = new ConnectionManager(hostTransport, new TransportConfig { MaxConnections = 7 }))
            using (var firstClientConnection = new ConnectionManager(firstClientTransport))
            using (var secondClientConnection = new ConnectionManager(secondClientTransport))
            {
                hostConnection.HostSession("Host", 7777);
                firstClientConnection.JoinSession("127.0.0.1", 7777);
                secondClientConnection.JoinSession("127.0.0.1", 7777);
                Pump(network, hostConnection, firstClientConnection, secondClientConnection);

                using (new HostPacketRelay(hostConnection.Session, hostConnection, hostConnection.Router))
                {
                    int relayedCount = 0;
                    secondClientConnection.Router.Register(PacketType.AircraftState, (_, __) => relayedCount++);

                    var state = new AircraftStatePacket
                    {
                        PlayerId = 3,  // Differs from sender peer ID (2) — but check is skipped
                        SequenceNumber = 42,
                        AircraftType = "AV8B",
                        RotW = 1f,
                        Timestamp = 12.5f
                    };
                    var frame = PacketSerializer.Serialize(
                        PacketType.AircraftState,
                        PacketSerializer.SerializeAircraftState(state));

                    firstClientConnection.BroadcastUnreliable(frame);
                    Pump(network, hostConnection, firstClientConnection, secondClientConnection);

                    Assert.AreEqual(1, relayedCount);
                }
            }
        }

        [Test]
        public void HostRelay_FansOutAircraftState_FromOneClientToSixOtherPeers()
        {
            var network = new FakeNetworkHarness();
            var hostTransport = network.CreateHost(1);
            var clientTransports = new FakeNetworkTransport[7];
            var clientConnections = new ConnectionManager[7];

            using (var hostConnection = new ConnectionManager(hostTransport, new TransportConfig { MaxConnections = 7 }))
            {
                try
                {
                    hostConnection.HostSession("Host", 7777);

                    for (int i = 0; i < clientConnections.Length; i++)
                    {
                        clientTransports[i] = network.CreateClient((ulong)(i + 2));
                        clientConnections[i] = new ConnectionManager(clientTransports[i]);
                        clientConnections[i].JoinSession("127.0.0.1", 7777);
                    }

                    Pump(network, hostConnection, clientConnections);

                    using (new HostPacketRelay(hostConnection.Session, hostConnection, hostConnection.Router))
                    {
                        var receiveCounts = new int[7];
                        for (int i = 0; i < clientConnections.Length; i++)
                        {
                            int index = i;
                            clientConnections[i].Router.Register(PacketType.AircraftState, (_, __) => receiveCounts[index]++);
                        }

                        var state = new AircraftStatePacket
                        {
                            PlayerId = 2,
                            SequenceNumber = 99,
                            AircraftType = "AV8B",
                            RotW = 1f
                        };
                        var frame = PacketSerializer.Serialize(
                            PacketType.AircraftState,
                            PacketSerializer.SerializeAircraftState(state));

                        clientConnections[0].BroadcastUnreliable(frame);
                        Pump(network, hostConnection, clientConnections);

                        Assert.AreEqual(0, receiveCounts[0], "Sender should not receive its own relayed state.");
                        for (int i = 1; i < receiveCounts.Length; i++)
                            Assert.AreEqual(1, receiveCounts[i], $"Peer {i + 2} should receive relayed state.");
                    }
                }
                finally
                {
                    foreach (var connection in clientConnections)
                        connection?.Dispose();
                }
            }
        }

        private static void Pump(
            FakeNetworkHarness network,
            ConnectionManager hostConnection,
            ConnectionManager firstClientConnection,
            ConnectionManager secondClientConnection)
        {
            network.AdvanceFrame();
            hostConnection.Update(1f / 60f);
            firstClientConnection.Update(1f / 60f);
            secondClientConnection.Update(1f / 60f);
        }

        private static void Pump(
            FakeNetworkHarness network,
            ConnectionManager hostConnection,
            ConnectionManager[] clientConnections)
        {
            network.AdvanceFrame();
            hostConnection.Update(1f / 60f);
            foreach (var connection in clientConnections)
                connection.Update(1f / 60f);
        }
    }
}
