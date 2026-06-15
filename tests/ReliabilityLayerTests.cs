using System.Collections.Generic;
using BepInEx.Logging;
using NUnit.Framework;
using TCAMultiplayer.Core;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.Tests.TestDoubles;
using TCAMultiplayer.Transport;

namespace TCAMultiplayer.Tests
{
    [TestFixture]
    public class ReliabilityLayerTests
    {
        [OneTimeSetUp]
        public void SetupLogging()
        {
            if (!Log.IsInitialized)
                Log.Init(new ManualLogSource("Test"));
        }

        [Test]
        public void SendReliable_LargePayload_FragmentsAndReassembles()
        {
            var network = new FakeNetworkHarness();
            var senderTransport = network.CreateHost(1);
            var receiverTransport = network.CreateClient(2);
            network.Connect(senderTransport, receiverTransport);
            senderTransport.Update();
            receiverTransport.Update();

            var config = new TransportConfig
            {
                MaxPacketSize = 512,
                RetransmitInterval = 10f,
                MaxRetransmitAttempts = 2
            };
            var sender = new ReliabilityLayer(senderTransport, config);
            var receiver = new ReliabilityLayer(receiverTransport, config);
            var received = new List<byte[]>();

            senderTransport.OnDataReceived += sender.HandleReceived;
            receiverTransport.OnDataReceived += receiver.HandleReceived;
            receiver.OnDataReady += (_, data) => received.Add(data);

            var payload = new byte[4096];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i % 251);

            sender.SendReliable(2, payload);
            for (int i = 0; i < 20; i++)
            {
                network.AdvanceFrame();
                senderTransport.Update();
                receiverTransport.Update();
            }

            Assert.AreEqual(1, received.Count);
            CollectionAssert.AreEqual(payload, received[0]);
            Assert.AreEqual(0, sender.GetPendingCount(2));
        }
    }
}
