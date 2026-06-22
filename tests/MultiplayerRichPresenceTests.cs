using NUnit.Framework;
using TCAMultiplayer.Core;
using TCAMultiplayer.Game;

namespace TCAMultiplayer.Tests
{
    [TestFixture]
    public class MultiplayerRichPresenceTests
    {
        [Test]
        public void ShowLobby_HostIncludesMultiplayerLobbySessionDetails()
        {
            using (var session = CreateSession(isHost: true))
            {
                session.MapName = "ActionIsland";
                session.MaxPlayersTotal = 8;
                session.GameMode = MultiplayerGameMode.TeamDogfight;
                session.TeamCount = 2;
                session.AddPlayer(2, "Wingman");

                var sink = new FakePresenceSink();
                var presence = CreatePresence(sink);

                presence.ShowLobby(session);

                Assert.AreEqual("Hosting multiplayer lobby", sink.State);
                Assert.AreEqual("2/8 pilots | Action Island | Team Dogfight (2 teams)", sink.Details);
                Assert.IsNull(sink.StartTimestamp);
                Assert.AreEqual(1, sink.SetActivityCalls);
                Assert.AreEqual(0, sink.RestoreCalls);
            }
        }

        [Test]
        public void ShowInGame_UsesFriendlyAircraftAndMapNames()
        {
            using (var session = CreateSession(isHost: false))
            {
                session.MapName = "DesertArena";
                session.GameMode = MultiplayerGameMode.FreeForAllDogfight;
                var local = session.GetLocalPlayer();
                local.SelectedAircraft = "F16C";
                local.Kills = 3;
                local.Deaths = 1;
                session.StateMachine.TryTransition(GameState.Loading);
                session.StateMachine.TryTransition(GameState.Spawning);
                session.BeginPlayerLife(session.LocalPeerId);
                session.StateMachine.TryTransition(GameState.InGame);

                var sink = new FakePresenceSink();
                var presence = CreatePresence(sink, () => 12345);

                presence.ShowForSession(session);

                Assert.AreEqual("Flying multiplayer", sink.State);
                Assert.AreEqual("F-16C Viper | K/D 3/1 | Alive | 1/8 pilots | Desert Arena | FFA Dogfight", sink.Details);
                Assert.AreEqual(12345, sink.StartTimestamp);
            }
        }

        [Test]
        public void ShowForSession_UsesDownStateBeforeRespawn()
        {
            using (var session = CreateSession(isHost: false))
            {
                var local = session.GetLocalPlayer();
                local.SelectedAircraft = "F16C";
                session.StateMachine.TryTransition(GameState.Loading);
                session.StateMachine.TryTransition(GameState.Spawning);
                session.BeginPlayerLife(session.LocalPeerId);
                local.IsAlive = false;
                session.StateMachine.TryTransition(GameState.InGame);

                var sink = new FakePresenceSink();
                var presence = CreatePresence(sink, () => 22222);

                presence.ShowForSession(session);

                Assert.AreEqual("Flying multiplayer", sink.State);
                Assert.AreEqual("F-16C Viper | K/D 0/0 | Down | 1/8 pilots | Action Island | FFA Dogfight", sink.Details);
                Assert.AreEqual(22222, sink.StartTimestamp);
            }
        }

        [Test]
        public void ShowForSession_UpdatesRespawnStateAndKeepsMatchTimer()
        {
            using (var session = CreateSession(isHost: false))
            {
                var local = session.GetLocalPlayer();
                local.SelectedAircraft = "F16C";
                local.Kills = 1;
                local.Deaths = 2;
                session.StateMachine.TryTransition(GameState.Loading);
                session.StateMachine.TryTransition(GameState.Spawning);
                session.BeginPlayerLife(session.LocalPeerId);
                session.StateMachine.TryTransition(GameState.InGame);
                var sink = new FakePresenceSink();
                var clock = 1000L;
                var presence = CreatePresence(sink, () => clock);

                presence.ShowForSession(session);
                clock = 2000L;
                session.StateMachine.TryTransition(GameState.Respawning);
                local.IsAwaitingRespawn = true;

                presence.ShowForSession(session);

                Assert.AreEqual("Respawning in multiplayer", sink.State);
                Assert.AreEqual("F-16C Viper | K/D 1/2 | Respawning | 1/8 pilots | Action Island | FFA Dogfight", sink.Details);
                Assert.AreEqual(1000, sink.StartTimestamp);
            }
        }

        [Test]
        public void ShowLobby_SuppressesDuplicateActivityUpdates()
        {
            using (var session = CreateSession(isHost: true))
            {
                var sink = new FakePresenceSink();
                var presence = CreatePresence(sink);

                presence.ShowLobby(session);
                presence.ShowLobby(session);

                Assert.AreEqual(1, sink.SetActivityCalls);
            }
        }

        [Test]
        public void ShowLobby_ResetsTimerAfterGameplay()
        {
            using (var session = CreateSession(isHost: true))
            {
                session.GetLocalPlayer().SelectedAircraft = "F16C";
                session.StateMachine.TryTransition(GameState.Loading);
                session.StateMachine.TryTransition(GameState.Spawning);
                session.BeginPlayerLife(session.LocalPeerId);
                session.StateMachine.TryTransition(GameState.InGame);

                var sink = new FakePresenceSink();
                var presence = CreatePresence(sink, () => 33333);
                presence.ShowForSession(session);

                session.StateMachine.TryTransition(GameState.ReturningToLobby);
                session.StateMachine.TryTransition(GameState.HostingLobby);
                presence.ShowForSession(session);

                Assert.AreEqual("Hosting multiplayer lobby", sink.State);
                Assert.AreEqual("1/8 pilots | Action Island | FFA Dogfight", sink.Details);
                Assert.IsNull(sink.StartTimestamp);
            }
        }

        [Test]
        public void ShowForSession_NullRestoresMainMenuPresence()
        {
            var sink = new FakePresenceSink();
            var presence = CreatePresence(sink);

            presence.ShowForSession(null);

            Assert.AreEqual(1, sink.RestoreCalls);
            Assert.AreEqual(0, sink.SetActivityCalls);
        }

        private static GameSession CreateSession(bool isHost)
        {
            var session = new GameSession(isHost);
            session.LocalPeerId = isHost ? 1UL : 2UL;
            session.MaxPlayersTotal = 8;
            session.MapName = "ActionIsland";
            session.StateMachine.TryTransition(isHost ? GameState.HostingLobby : GameState.ClientLobby);
            session.AddPlayer(session.LocalPeerId, isHost ? "Host" : "Client");
            return session;
        }

        private static MultiplayerRichPresence CreatePresence(FakePresenceSink sink, System.Func<long> timestampProvider = null)
        {
            return new MultiplayerRichPresence(
                sink,
                mapName => mapName == "ActionIsland" ? "Action Island" : "Desert Arena",
                aircraftName => aircraftName == "F16C" ? "F-16C Viper" : aircraftName,
                timestampProvider);
        }

        private sealed class FakePresenceSink : IActivityPresenceSink
        {
            public string State { get; private set; }
            public string Details { get; private set; }
            public long? StartTimestamp { get; private set; }
            public int SetActivityCalls { get; private set; }
            public int RestoreCalls { get; private set; }

            public void SetActivity(string state, string details, long? startTimestamp = null)
            {
                State = state;
                Details = details;
                StartTimestamp = startTimestamp;
                SetActivityCalls++;
            }

            public void RestoreMainMenu()
            {
                RestoreCalls++;
            }
        }
    }
}
