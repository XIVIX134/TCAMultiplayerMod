using System;
using System.Collections.Generic;
using BepInEx.Logging;
using NUnit.Framework;
using TCAMultiplayer.Core;
using TCAMultiplayer.Game;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.Transport;

namespace TCAMultiplayer.Tests
{
    [TestFixture]
    public class LobbyManagerTests
    {
        [OneTimeSetUp]
        public void SetUpLogging()
        {
            if (!Log.IsInitialized)
                Log.Init(new ManualLogSource("Test"));
            LobbyManager.TimeProvider = () => 0f;
        }

        [Test]
        public void NewPlayerInfo_DoesNotPreselectCleanLoadout()
        {
            using (var session = new GameSession(isHost: true))
            {
                session.LocalPeerId = 1;

                var player = session.AddPlayer(1, "Host");

                Assert.IsNull(player.SelectedLoadout);
            }
        }

        [Test]
        public void ClientLobbyStateMerge_CopiesRemoteLoadout()
        {
            using (var session = new GameSession(isHost: false))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 2;
                session.StateMachine.TryTransition(GameState.ClientLobby);
                var router = new PacketRouter();
                using (new LobbyManager(session, connection, router))
                {
                    var state = new LobbyStatePacket
                    {
                        HostName = "TCA Server",
                        MapName = "ActionIsland",
                        SpawnType = Protocol.LobbySpawnType.Air,
                        TimeOfDay = Protocol.TimeOfDay.Evening,
                        GameMode = Protocol.MultiplayerGameMode.TeamDogfight,
                        MaxPlayersTotal = 8,
                        TeamCount = 2,
                        Players = new[]
                        {
                            new LobbyPlayerInfo
                            {
                                PeerId = 1,
                                PlayerName = "Host",
                                SelectedAircraft = "F16A",
                                SelectedAirfield = "ToramaruAirfield",
                                SelectedLoadout = "Dogfight",
                                IsHost = true,
                                Team = Protocol.MultiplayerTeam.Team1
                            },
                            new LobbyPlayerInfo
                            {
                                PeerId = 2,
                                PlayerName = "Client",
                                SelectedAircraft = "MiG-29G",
                                SelectedAirfield = "Expeditionary",
                                SelectedLoadout = "Dogfight",
                                Team = Protocol.MultiplayerTeam.Team2
                            }
                        }
                    };

                    var frame = PacketSerializer.Serialize(
                        PacketType.LobbyState,
                        PacketSerializer.SerializeLobbyState(state));

                    router.Route(1, frame);
                }

                var host = session.GetPlayer(1);
                Assert.IsNotNull(host);
                Assert.AreEqual("Dogfight", host.SelectedLoadout);
                Assert.AreEqual(Core.MultiplayerGameMode.TeamDogfight, session.GameMode);
                Assert.AreEqual(8, session.MaxPlayersTotal);
                Assert.AreEqual(2, session.TeamCount);
                Assert.AreEqual(Core.MultiplayerTeam.Team1, host.Team);
                Assert.AreEqual(Core.MultiplayerTeam.Team2, session.GetPlayer(2)?.Team);
            }
        }

        [Test]
        public void ClientLobbyStateMerge_DoesNotTreatMissingLocalLoadoutAsClean()
        {
            using (var session = new GameSession(isHost: false))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 2;
                session.StateMachine.TryTransition(GameState.ClientLobby);
                session.AddPlayer(2, "Client");

                var router = new PacketRouter();
                using (new LobbyManager(session, connection, router))
                {
                    var state = new LobbyStatePacket
                    {
                        HostName = "TCA Server",
                        MapName = "ActionIsland",
                        Players = new[]
                        {
                            new LobbyPlayerInfo
                            {
                                PeerId = 2,
                                PlayerName = "Client",
                                SelectedAircraft = "AV8B",
                                SelectedAirfield = "ToramaruAirfield",
                                SelectedLoadout = null
                            }
                        }
                    };

                    var frame = PacketSerializer.Serialize(
                        PacketType.LobbyState,
                        PacketSerializer.SerializeLobbyState(state));

                    router.Route(1, frame);
                }

                Assert.IsNull(session.GetLocalPlayer()?.SelectedLoadout);
            }
        }

        [Test]
        public void HostLobbyTeamSelect_RejectsPeerSpoofingAnotherPlayer()
        {
            using (var session = new GameSession(isHost: true))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 1;
                session.StateMachine.TryTransition(GameState.HostingLobby);
                session.GameMode = Core.MultiplayerGameMode.TeamDogfight;
                session.TeamCount = 4;
                session.AddPlayer(1, "Host");
                var second = session.AddPlayer(2, "Two");
                var third = session.AddPlayer(3, "Three");
                second.IsModsVerified = true;
                third.IsModsVerified = true;
                var router = new PacketRouter();

                using (new LobbyManager(session, connection, router))
                {
                    var spoofed = PacketSerializer.Serialize(
                        PacketType.LobbyTeamSelect,
                        PacketSerializer.SerializeLobbyTeamSelect(new LobbyTeamSelectPacket
                        {
                            PeerId = 3,
                            Team = Protocol.MultiplayerTeam.Team4
                        }));
                    router.Route(2, spoofed);

                    Assert.AreEqual(Core.MultiplayerTeam.None, third.Team);

                    var legitimate = PacketSerializer.Serialize(
                        PacketType.LobbyTeamSelect,
                        PacketSerializer.SerializeLobbyTeamSelect(new LobbyTeamSelectPacket
                        {
                            PeerId = 2,
                            Team = Protocol.MultiplayerTeam.Team2
                        }));
                    router.Route(2, legitimate);

                    Assert.AreEqual(Core.MultiplayerTeam.Team2, second.Team);
                    Assert.AreEqual(Core.MultiplayerTeam.None, third.Team);
                }
            }
        }

        [Test]
        public void HostTeamDogfight_WithTwoPlayers_AutoAssignsTwoTeams()
        {
            using (var session = new GameSession(isHost: true))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 1;
                session.StateMachine.TryTransition(GameState.HostingLobby);
                var host = session.AddPlayer(1, "Host");
                var peer = session.AddPlayer(2, "Peer");
                session.TeamCount = 4;

                var router = new PacketRouter();
                using (var lobby = new LobbyManager(session, connection, router))
                {
                    lobby.SetGameMode(Core.MultiplayerGameMode.TeamDogfight);

                    Assert.AreEqual(2, session.TeamCount);
                    Assert.AreEqual(Core.MultiplayerTeam.Team1, host.Team);
                    Assert.AreEqual(Core.MultiplayerTeam.Team2, peer.Team);
                }
            }
        }

        [Test]
        public void ClientLobbyStateMerge_PreservesTeamThreeWhenHostUsesThreeTeams()
        {
            using (var session = new GameSession(isHost: false))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 3;
                session.StateMachine.TryTransition(GameState.ClientLobby);
                var router = new PacketRouter();
                using (new LobbyManager(session, connection, router))
                {
                    var state = new LobbyStatePacket
                    {
                        HostName = "TCA Server",
                        MapName = "ActionIsland",
                        GameMode = Protocol.MultiplayerGameMode.TeamDogfight,
                        MaxPlayersTotal = 8,
                        TeamCount = 3,
                        Players = new[]
                        {
                            new LobbyPlayerInfo { PeerId = 1, PlayerName = "Host", IsHost = true, Team = Protocol.MultiplayerTeam.Team1 },
                            new LobbyPlayerInfo { PeerId = 2, PlayerName = "Two", Team = Protocol.MultiplayerTeam.Team2 },
                            new LobbyPlayerInfo { PeerId = 3, PlayerName = "Three", Team = Protocol.MultiplayerTeam.Team3 }
                        }
                    };

                    var frame = PacketSerializer.Serialize(
                        PacketType.LobbyState,
                        PacketSerializer.SerializeLobbyState(state));

                    router.Route(1, frame);
                }

                Assert.AreEqual(3, session.TeamCount);
                Assert.AreEqual(Core.MultiplayerTeam.Team3, session.GetPlayer(3)?.Team);
            }
        }

        [Test]
        public void ClientLobbyStateMerge_CopiesModVerificationState()
        {
            using (var session = new GameSession(isHost: false))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 2;
                session.StateMachine.TryTransition(GameState.ClientLobby);
                var router = new PacketRouter();
                using (new LobbyManager(session, connection, router))
                {
                    router.Route(1, LobbyStateFrame(new LobbyStatePacket
                    {
                        HostName = "TCA Server",
                        MapName = "ActionIsland",
                        Players = new[]
                        {
                            new LobbyPlayerInfo
                            {
                                PeerId = 1,
                                PlayerName = "Host",
                                IsHost = true,
                                IsModsVerified = true,
                                HasModCompatibilityState = true
                            },
                            new LobbyPlayerInfo
                            {
                                PeerId = 2,
                                PlayerName = "Client",
                                IsModsVerified = true,
                                IsModSyncing = false,
                                HasModCompatibilityState = true
                            },
                            new LobbyPlayerInfo
                            {
                                PeerId = 3,
                                PlayerName = "Syncing",
                                IsModsVerified = false,
                                IsModSyncing = true,
                                HasModCompatibilityState = true
                            }
                        }
                    }));
                }

                Assert.AreEqual(true, session.GetPlayer(1)?.IsModsVerified);
                Assert.AreEqual(true, session.GetPlayer(2)?.IsModsVerified);
                Assert.AreEqual(false, session.GetPlayer(2)?.IsModSyncing);
                Assert.AreEqual(false, session.GetPlayer(3)?.IsModsVerified);
                Assert.AreEqual(true, session.GetPlayer(3)?.IsModSyncing);
            }
        }

        [Test]
        public void ClientLobbyStateMerge_IgnoresOlderRevision()
        {
            using (var session = new GameSession(isHost: false))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 2;
                session.StateMachine.TryTransition(GameState.ClientLobby);
                var router = new PacketRouter();
                using (new LobbyManager(session, connection, router))
                {
                    router.Route(1, LobbyStateFrame(new LobbyStatePacket
                    {
                        HostName = "TCA Server",
                        MapName = "ActionIsland",
                        Revision = 2,
                        Players = new[]
                        {
                            new LobbyPlayerInfo { PeerId = 1, PlayerName = "Host", IsHost = true },
                            new LobbyPlayerInfo { PeerId = 2, PlayerName = "Client", SelectedAircraft = "AV8B" }
                        }
                    }));

                    router.Route(1, LobbyStateFrame(new LobbyStatePacket
                    {
                        HostName = "TCA Server",
                        MapName = "StaleMap",
                        Revision = 1,
                        Players = new[]
                        {
                            new LobbyPlayerInfo { PeerId = 1, PlayerName = "Host", IsHost = true },
                            new LobbyPlayerInfo { PeerId = 2, PlayerName = "Client", SelectedAircraft = "F16A" }
                        }
                    }));
                }

                Assert.AreEqual("ActionIsland", session.MapName);
                Assert.AreEqual("AV8B", session.GetPlayer(2)?.SelectedAircraft);
            }
        }

        [Test]
        public void ClientWelcome_InitializesAndAnnouncesLocalSelectionDefaults()
        {
            using (var session = new GameSession(isHost: false))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                transport.Connect("127.0.0.1", 7777);
                session.LocalPeerId = 0;
                session.StateMachine.TryTransition(GameState.ClientLobby);
                var router = new PacketRouter();
                using (new LobbyManager(session, connection, router))
                {
                    router.Route(1, Frame(PacketType.LobbyWelcome,
                        PacketSerializer.SerializeLobbyWelcome(new LobbyWelcomePacket
                        {
                            AssignedPeerId = 2,
                            HostName = "TCA Server"
                        })));
                }

                var local = session.GetLocalPlayer();
                Assert.IsNotNull(local);
                Assert.AreEqual(2UL, session.LocalPeerId);
                Assert.IsFalse(string.IsNullOrEmpty(local.SelectedAircraft));
                Assert.IsFalse(string.IsNullOrEmpty(local.SelectedLoadout));

                var sentTypes = transport.SentReliablePacketTypes;
                Assert.Contains(PacketType.AircraftSelect, sentTypes);
                Assert.Contains(PacketType.LoadoutSelect, sentTypes);
                Assert.Contains(PacketType.LobbyAirfieldSelect, sentTypes);
                Assert.Contains(PacketType.LobbyPlayerJoined, sentTypes);
            }
        }

        [Test]
        public void HostAcceptsJoinNameBeforeModVerificationButBlocksSelection()
        {
            using (var session = new GameSession(isHost: true))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 1;
                session.StateMachine.TryTransition(GameState.HostingLobby);
                session.AddPlayer(1, "Host");
                var peer = session.AddPlayer(2, "Peer_2");
                peer.IsModsVerified = false;

                var router = new PacketRouter();
                using (new LobbyManager(session, connection, router))
                {
                    router.Route(2, Frame(PacketType.LobbyPlayerJoined,
                        PacketSerializer.SerializeLobbyPlayerJoined(new LobbyPlayerJoinedPacket
                        {
                            PeerId = 2,
                            PlayerName = "ClientName"
                        })));

                    router.Route(2, Frame(PacketType.AircraftSelect,
                        PacketSerializer.SerializeLobbyAircraftSelect(new LobbyAircraftSelectPacket
                        {
                            PeerId = 2,
                            AircraftName = "F16A"
                        })));
                }

                var updatedPeer = session.GetPlayer(2);
                Assert.IsNotNull(updatedPeer);
                Assert.AreEqual("ClientName", updatedPeer.PlayerName);
                Assert.IsFalse(updatedPeer.IsModsVerified);
                Assert.AreNotEqual("F16A", updatedPeer.SelectedAircraft);
            }
        }

        [Test]
        public void ClientLobbyState_PreservesLocalNameOverHostPlaceholder()
        {
            using (var session = new GameSession(isHost: false))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 2;
                session.StateMachine.TryTransition(GameState.ClientLobby);
                session.AddPlayer(2, "ClientName");

                var router = new PacketRouter();
                using (new LobbyManager(session, connection, router))
                {
                    router.Route(1, LobbyStateFrame(new LobbyStatePacket
                    {
                        HostName = "TCA Server",
                        MapName = "ActionIsland",
                        Revision = 1,
                        Players = new[]
                        {
                            new LobbyPlayerInfo { PeerId = 1, PlayerName = "Host", IsHost = true },
                            new LobbyPlayerInfo { PeerId = 2, PlayerName = "Peer_2" }
                        }
                    }));
                }

                Assert.AreEqual("ClientName", session.GetPlayer(2)?.PlayerName);
            }
        }

        [Test]
        public void ClientLobby_IgnoresLobbyStateFromNonHostPeer()
        {
            using (var session = new GameSession(isHost: false))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 2;
                session.StateMachine.TryTransition(GameState.ClientLobby);
                var router = new PacketRouter();
                using (new LobbyManager(session, connection, router))
                {
                    router.Route(3, LobbyStateFrame(new LobbyStatePacket
                    {
                        HostName = "Spoof",
                        MapName = "BadMap",
                        Revision = 1,
                        Players = new[]
                        {
                            new LobbyPlayerInfo { PeerId = 3, PlayerName = "Spoofer" }
                        }
                    }));
                }

                Assert.AreNotEqual("Spoof", session.HostName);
                Assert.AreEqual(0, session.PlayerCount);
            }
        }

        [Test]
        public void ClientLoading_IgnoresStaleLobbyState()
        {
            using (var session = new GameSession(isHost: false))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 2;
                session.StateMachine.TryTransition(GameState.ClientLobby);
                session.StateMachine.TryTransition(GameState.Loading);
                var router = new PacketRouter();
                using (new LobbyManager(session, connection, router))
                {
                    router.Route(1, LobbyStateFrame(new LobbyStatePacket
                    {
                        HostName = "TCA Server",
                        MapName = "LateLobby",
                        Revision = 1,
                        Players = new[]
                        {
                            new LobbyPlayerInfo { PeerId = 1, PlayerName = "Host", IsHost = true },
                            new LobbyPlayerInfo { PeerId = 2, PlayerName = "Client" }
                        }
                    }));
                }

                Assert.AreEqual(GameState.Loading, session.StateMachine.CurrentState);
                Assert.AreNotEqual("LateLobby", session.MapName);
                Assert.AreEqual(0, session.PlayerCount);
            }
        }

        [Test]
        public void HostLoading_IgnoresLateReadyPacket()
        {
            using (var session = new GameSession(isHost: true))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 1;
                session.StateMachine.TryTransition(GameState.HostingLobby);
                session.StateMachine.TryTransition(GameState.Loading);
                var peer = session.AddPlayer(2, "Peer");
                var router = new PacketRouter();
                using (new LobbyManager(session, connection, router))
                {
                    router.Route(2, Frame(PacketType.LobbyPlayerReady,
                        PacketSerializer.SerializeLobbyPlayerReady(new LobbyPlayerReadyPacket
                        {
                            PeerId = 2,
                            IsReady = true
                        })));
                }

                Assert.IsFalse(peer.IsReady);
            }
        }

        [Test]
        public void ClientSpawnPlayers_RequiresHostAndLoadingState()
        {
            using (var session = new GameSession(isHost: false))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 2;
                session.StateMachine.TryTransition(GameState.ClientLobby);
                var router = new PacketRouter();
                int allLoadedCount = 0;

                using (var lobby = new LobbyManager(session, connection, router))
                {
                    lobby.OnAllPlayersLoaded += () => allLoadedCount++;

                    router.Route(1, Frame(PacketType.LobbySpawnPlayers,
                        PacketSerializer.SerializeLobbySpawnPlayers(new LobbySpawnPlayersPacket { Timestamp = 1f })));
                    Assert.AreEqual(GameState.ClientLobby, session.StateMachine.CurrentState);

                    session.StateMachine.TryTransition(GameState.Loading);
                    router.Route(3, Frame(PacketType.LobbySpawnPlayers,
                        PacketSerializer.SerializeLobbySpawnPlayers(new LobbySpawnPlayersPacket { Timestamp = 2f })));
                    Assert.AreEqual(GameState.Loading, session.StateMachine.CurrentState);

                    router.Route(1, Frame(PacketType.LobbySpawnPlayers,
                        PacketSerializer.SerializeLobbySpawnPlayers(new LobbySpawnPlayersPacket { Timestamp = 3f })));
                }

                Assert.AreEqual(GameState.Spawning, session.StateMachine.CurrentState);
                Assert.AreEqual(1, allLoadedCount);
            }
        }

        [Test]
        public void LobbySelectionChange_ClearsLocalReady()
        {
            using (var session = new GameSession(isHost: false))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 2;
                session.StateMachine.TryTransition(GameState.ClientLobby);
                var local = session.AddPlayer(2, "Client");
                local.SelectedAircraft = "AV8B";
                local.IsReady = true;

                var router = new PacketRouter();
                using (var lobby = new LobbyManager(session, connection, router))
                {
                    lobby.SetAircraft("F16A");
                }

                Assert.IsFalse(local.IsReady);
                Assert.AreEqual("F16A", local.SelectedAircraft);
            }
        }

        [Test]
        public void HostSettingsChange_ClearsClientReady()
        {
            using (var session = new GameSession(isHost: true))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 1;
                session.StateMachine.TryTransition(GameState.HostingLobby);
                session.AddPlayer(1, "Host");
                var peer = session.AddPlayer(2, "Peer");
                peer.IsReady = true;

                var router = new PacketRouter();
                using (var lobby = new LobbyManager(session, connection, router))
                {
                    lobby.SetTimeOfDay(TimeOfDaySetting.Evening);
                }

                Assert.IsFalse(peer.IsReady);
            }
        }

        [Test]
        public void HostPeerDisconnect_RefreshesLobbyAndBroadcastsState()
        {
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                connection.HostSession("Host", 7777);
                var session = connection.Session;
                int changedCount = 0;

                using (var lobby = new LobbyManager(session, connection, connection.Router))
                {
                    lobby.OnLobbyStateChanged += () => changedCount++;

                    transport.EmitPeerConnected(2);
                    transport.EmitPeerConnected(3);
                    transport.SentReliablePacketTypes.Clear();

                    transport.EmitPeerDisconnected(2);
                }

                Assert.IsNull(session.GetPlayer(2));
                Assert.IsNotNull(session.GetPlayer(3));
                Assert.AreEqual(2, session.PlayerCount);
                Assert.GreaterOrEqual(changedCount, 1);
                Assert.Contains(PacketType.LobbyState, transport.SentReliablePacketTypes);
            }
        }

        [Test]
        public void HostLoading_RechecksAllLoadedWhenPeerDisconnects()
        {
            using (var session = new GameSession(isHost: true))
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                session.LocalPeerId = 1;
                session.StateMachine.TryTransition(GameState.HostingLobby);
                var host = session.AddPlayer(1, "Host");
                var loadedPeer = session.AddPlayer(2, "Loaded");
                var missingPeer = session.AddPlayer(3, "Missing");
                host.IsLoaded = true;
                loadedPeer.IsLoaded = true;
                missingPeer.IsLoaded = false;
                session.StateMachine.TryTransition(GameState.Loading);

                var router = new PacketRouter();
                int allLoadedCount = 0;
                using (var lobby = new LobbyManager(session, connection, router))
                {
                    lobby.OnAllPlayersLoaded += () => allLoadedCount++;
                    session.RemovePlayer(3);
                }

                Assert.AreEqual(GameState.Spawning, session.StateMachine.CurrentState);
                Assert.AreEqual(1, allLoadedCount);
            }
        }

        [Test]
        public void ClientConnection_HostDisconnect_EndsSessionAndStopsTransport()
        {
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                connection.JoinSession("127.0.0.1", 7777);
                transport.EmitPeerConnected(1);

                Assert.IsTrue(connection.HasSession);
                Assert.AreEqual(GameState.ClientLobby, connection.Session.StateMachine.CurrentState);

                transport.EmitPeerDisconnected(1);

                Assert.IsFalse(connection.HasSession);
                Assert.IsFalse(transport.IsConnected);
                Assert.AreEqual(1, transport.DisconnectCalls);
            }
        }

        [Test]
        public void ClientReconnect_AllowsHostReliableSequencesToRestart()
        {
            using (var transport = new FakeTransport())
            using (var connection = new ConnectionManager(transport))
            {
                byte[] packet = ReliableFrame(0, Frame(
                    PacketType.LobbyWelcome,
                    PacketSerializer.SerializeLobbyWelcome(new LobbyWelcomePacket
                    {
                        AssignedPeerId = 2,
                        HostName = "Host"
                    })));
                int delivered = 0;

                connection.JoinSession("127.0.0.1", 7777);
                transport.EmitPeerConnected(1);
                connection.Router.Register(PacketType.LobbyWelcome, (_, __) => delivered++);
                transport.EmitDataReceived(1, packet);
                Assert.AreEqual(1, delivered);

                connection.Disconnect();
                connection.JoinSession("127.0.0.1", 7777);
                transport.EmitPeerConnected(1);
                connection.Router.Register(PacketType.LobbyWelcome, (_, __) => delivered++);
                transport.EmitDataReceived(1, packet);

                Assert.AreEqual(2, delivered);
            }
        }

        private static byte[] LobbyStateFrame(LobbyStatePacket state)
        {
            return Frame(PacketType.LobbyState, PacketSerializer.SerializeLobbyState(state));
        }

        private static byte[] Frame(PacketType type, byte[] payload)
        {
            return PacketSerializer.Serialize(type, payload);
        }

        private static byte[] ReliableFrame(uint sequence, byte[] payload)
        {
            var framed = new byte[payload.Length + 5];
            framed[0] = 0xFE;
            framed[1] = (byte)sequence;
            framed[2] = (byte)(sequence >> 8);
            framed[3] = (byte)(sequence >> 16);
            framed[4] = (byte)(sequence >> 24);
            System.Buffer.BlockCopy(payload, 0, framed, 5, payload.Length);
            return framed;
        }

        private sealed class FakeTransport : ITransport
        {
            public bool IsHost { get; private set; }
            public bool IsConnected { get; private set; }
            public ulong LocalPeerId { get; private set; }
            private readonly List<ulong> _connectedPeers = new List<ulong>();
            public IReadOnlyCollection<ulong> ConnectedPeers => _connectedPeers;
            public int DisconnectCalls { get; private set; }
            public List<PacketType> SentReliablePacketTypes { get; } = new List<PacketType>();

            private Action<ulong> _onPeerConnected;
            private Action<ulong> _onPeerDisconnected;
            private Action<ulong, byte[]> _onDataReceived;

            public event Action<ulong, byte[]> OnDataReceived
            {
                add { _onDataReceived += value; }
                remove { _onDataReceived -= value; }
            }
            public event Action<ulong> OnPeerConnected
            {
                add { _onPeerConnected += value; }
                remove { _onPeerConnected -= value; }
            }
            public event Action<ulong> OnPeerDisconnected
            {
                add { _onPeerDisconnected += value; }
                remove { _onPeerDisconnected -= value; }
            }

            public void EmitPeerConnected(ulong peerId)
            {
                if (!_connectedPeers.Contains(peerId))
                    _connectedPeers.Add(peerId);
                _onPeerConnected?.Invoke(peerId);
            }

            public void EmitPeerDisconnected(ulong peerId)
            {
                _connectedPeers.Remove(peerId);
                _onPeerDisconnected?.Invoke(peerId);
            }

            public void EmitDataReceived(ulong peerId, byte[] data)
            {
                _onDataReceived?.Invoke(peerId, data);
            }

            public void StartHost(int port)
            {
                IsHost = true;
                IsConnected = true;
                LocalPeerId = 1;
                _connectedPeers.Clear();
            }

            public void Connect(string address, int port)
            {
                IsHost = false;
                IsConnected = true;
                LocalPeerId = 2;
                _connectedPeers.Clear();
                _connectedPeers.Add(1);
            }

            public void Disconnect()
            {
                DisconnectCalls++;
                IsConnected = false;
                LocalPeerId = 0;
                _connectedPeers.Clear();
            }

            public void DisconnectPeer(ulong peerId)
            {
                _connectedPeers.Remove(peerId);
                if (!IsHost && peerId == 1)
                    Disconnect();
            }

            public void Send(ulong peerId, byte[] data, bool reliable)
            {
                if (data == null || data.Length == 0)
                    return;
                if (data[0] == 0xFF)
                    return;

                if (data[0] == 0xFE && data.Length > 5)
                    SentReliablePacketTypes.Add((PacketType)data[5]);
                else
                    SentReliablePacketTypes.Add((PacketType)data[0]);
            }

            public void Broadcast(byte[] data, bool reliable, ulong? except = null)
            {
            }

            public void Update()
            {
            }

            public void Dispose()
            {
                Disconnect();
            }
        }
    }
}
