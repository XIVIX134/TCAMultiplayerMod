using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private static int _nextPort = 27801;

        [OneTimeSetUp]
        public void SetUpLogging()
        {
            if (!Log.IsInitialized)
                Log.Init(new ManualLogSource("Test"));
        }

        private static TransportConfig MakeConfig() => new TransportConfig
        {
            KeepaliveInterval = 0.05f,
            TimeoutSeconds = 0.4f,
            ReconnectGraceSeconds = 1.6f,
        };

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
    }
}
