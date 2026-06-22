using System;
using System.Reflection;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Game
{
    public interface IActivityPresenceSink
    {
        void SetActivity(string state, string details, long? startTimestamp = null);
        void RestoreMainMenu();
    }

    /// <summary>
    /// Bridges TCAMP presence updates into the base game's Discord Rich Presence owner.
    /// Reflection keeps the mod independent of Discord GameSDK compile-time references.
    /// </summary>
    public sealed class GameDiscordActivitySink : IActivityPresenceSink
    {
        private const string Tag = "PRESENCE";

        private static bool _directActivityResolved;
        private static FieldInfo _discordField;
        private static MethodInfo _getActivityManagerMethod;
        private static Type _activityType;
        private static Type _activityAssetsType;
        private static Type _activityTimestampsType;
        private static Type _activityTypeEnumType;
        private static Type _updateActivityHandlerType;
        private static MethodInfo _updateActivityMethod;
        private static Delegate _updateActivityHandler;

        private static bool _setActivityResolved;
        private static MethodInfo _setActivityMethod;
        private static bool _restoreResolved;
        private static MethodInfo _setGameStateMethod;
        private static object _mainMenuGameState;

        public void SetActivity(string state, string details, long? startTimestamp = null)
        {
            state = state ?? string.Empty;
            details = details ?? string.Empty;

            if (TrySetActivityDirect(state, details, startTimestamp))
                return;

            // Fallback through the base helper. It always sets Start=now, so use it
            // only if the direct Discord GameSDK path is unavailable.
            var method = ResolveSetActivity();
            if (method == null)
                return;

            try
            {
                method.Invoke(null, new object[] { state, details });
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Discord activity update failed: {ex.GetBaseException().Message}");
            }
        }

        public void RestoreMainMenu()
        {
            var method = ResolveSetGameState();
            if (method == null)
            {
                SetActivity("Main Menu", string.Empty);
                return;
            }

            try
            {
                method.Invoke(null, new[] { _mainMenuGameState });
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Rich Presence restore failed: {ex.GetBaseException().Message}");
                SetActivity("Main Menu", string.Empty);
            }
        }

        private static bool TrySetActivityDirect(string state, string details, long? startTimestamp)
        {
            if (!ResolveDirectActivity())
                return false;

            object discord = _discordField?.GetValue(null);
            if (discord == null)
                return false;

            try
            {
                object manager = _getActivityManagerMethod.Invoke(discord, null);
                if (manager == null)
                    return false;

                object activity = Activator.CreateInstance(_activityType);
                _activityType.GetField("State")?.SetValue(activity, state);
                _activityType.GetField("Details")?.SetValue(activity, details);
                _activityType.GetField("Type")?.SetValue(activity, Enum.ToObject(_activityTypeEnumType, 0));

                object assets = Activator.CreateInstance(_activityAssetsType);
                _activityAssetsType.GetField("LargeImage")?.SetValue(assets, "tiny_combat_arena");
                _activityAssetsType.GetField("LargeText")?.SetValue(assets, "Tiny Combat Arena Multiplayer");
                _activityType.GetField("Assets")?.SetValue(activity, assets);

                if (startTimestamp.HasValue)
                {
                    object timestamps = Activator.CreateInstance(_activityTimestampsType);
                    _activityTimestampsType.GetField("Start")?.SetValue(timestamps, startTimestamp.Value);
                    _activityType.GetField("Timestamps")?.SetValue(activity, timestamps);
                }

                _updateActivityMethod.Invoke(manager, new[] { activity, _updateActivityHandler });
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Direct Discord activity update failed: {ex.GetBaseException().Message}");
                return false;
            }
        }

        private static bool ResolveDirectActivity()
        {
            if (_directActivityResolved)
                return _updateActivityMethod != null && _updateActivityHandler != null;

            _directActivityResolved = true;
            try
            {
                var discordStuffType = Type.GetType("Falcon.DiscordStuff, Assembly-CSharp", throwOnError: false);
                _discordField = discordStuffType?.GetField("discord", BindingFlags.NonPublic | BindingFlags.Static);
                var discordType = _discordField?.FieldType;
                _getActivityManagerMethod = discordType?.GetMethod("GetActivityManager", BindingFlags.Public | BindingFlags.Instance);
                var activityManagerType = _getActivityManagerMethod?.ReturnType;
                var firstPassAssembly = activityManagerType?.Assembly;

                _activityType = firstPassAssembly?.GetType("Discord.Activity", throwOnError: false);
                _activityAssetsType = firstPassAssembly?.GetType("Discord.ActivityAssets", throwOnError: false);
                _activityTimestampsType = firstPassAssembly?.GetType("Discord.ActivityTimestamps", throwOnError: false);
                _activityTypeEnumType = firstPassAssembly?.GetType("Discord.ActivityType", throwOnError: false);
                _updateActivityHandlerType = firstPassAssembly?.GetType("Discord.ActivityManager+UpdateActivityHandler", throwOnError: false);

                if (activityManagerType == null || _activityType == null || _updateActivityHandlerType == null)
                    return false;

                _updateActivityMethod = activityManagerType.GetMethod(
                    "UpdateActivity",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { _activityType, _updateActivityHandlerType },
                    null);

                if (_updateActivityMethod != null)
                    _updateActivityHandler = CreateUpdateActivityHandler(_updateActivityHandlerType);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Direct Discord activity hook unavailable: {ex.GetBaseException().Message}");
            }

            return _updateActivityMethod != null && _updateActivityHandler != null;
        }

        private static Delegate CreateUpdateActivityHandler(Type handlerType)
        {
            var callbackType = Type.GetType("Falcon.DiscordStuff+<>c, Assembly-CSharp", throwOnError: false);
            var callbackTarget = callbackType
                ?.GetField("<>9", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            var callbackMethod = callbackType?.GetMethod(
                "<SetActivity>b__6_0",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            if (callbackTarget == null || callbackMethod == null)
                return null;

            return Delegate.CreateDelegate(handlerType, callbackTarget, callbackMethod);
        }

        private static MethodInfo ResolveSetActivity()
        {
            if (_setActivityResolved)
                return _setActivityMethod;

            _setActivityResolved = true;
            try
            {
                var type = Type.GetType("Falcon.DiscordStuff, Assembly-CSharp", throwOnError: false);
                _setActivityMethod = type?.GetMethod(
                    "SetActivity",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(string) },
                    null);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Discord activity hook unavailable: {ex.GetBaseException().Message}");
            }

            return _setActivityMethod;
        }

        private static MethodInfo ResolveSetGameState()
        {
            if (_restoreResolved)
                return _setGameStateMethod;

            _restoreResolved = true;
            try
            {
                var gameStateType = Type.GetType("Falcon.Game2.GameState, Assembly-CSharp", throwOnError: false);
                var richPresenceType = Type.GetType("Falcon.RichPresence, Assembly-CSharp", throwOnError: false);
                if (gameStateType == null || richPresenceType == null)
                    return null;

                _mainMenuGameState = Enum.Parse(gameStateType, "MainMenu");
                _setGameStateMethod = richPresenceType.GetMethod(
                    "SetGameState",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { gameStateType },
                    null);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Rich Presence restore hook unavailable: {ex.GetBaseException().Message}");
            }

            return _setGameStateMethod;
        }
    }

    public sealed class MultiplayerRichPresence
    {
        private readonly IActivityPresenceSink _sink;
        private readonly Func<string, string> _mapDisplayNameResolver;
        private readonly Func<string, string> _aircraftDisplayNameResolver;
        private readonly Func<long> _timestampProvider;
        private string _lastState;
        private string _lastDetails;
        private long? _lastStartTimestamp;
        private long? _matchStartTimestamp;

        public MultiplayerRichPresence(
            IActivityPresenceSink sink = null,
            Func<string, string> mapDisplayNameResolver = null,
            Func<string, string> aircraftDisplayNameResolver = null,
            Func<long> timestampProvider = null)
        {
            _sink = sink ?? new GameDiscordActivitySink();
            _mapDisplayNameResolver = mapDisplayNameResolver ?? MapHelper.GetMapDisplayName;
            _aircraftDisplayNameResolver = aircraftDisplayNameResolver ?? LoadoutHelper.GetAircraftDisplayName;
            _timestampProvider = timestampProvider ?? (() => DateTimeOffset.Now.ToUnixTimeSeconds());
        }

        public void ShowForSession(GameSession session, string aircraftName = null)
        {
            if (session == null)
            {
                RestoreMainMenu();
                return;
            }

            switch (session.StateMachine.CurrentState)
            {
                case GameState.HostingLobby:
                case GameState.ClientLobby:
                    ShowLobby(session);
                    break;
                case GameState.Loading:
                    ShowLoading(session);
                    break;
                case GameState.Spawning:
                    ShowSpawning(session, aircraftName);
                    break;
                case GameState.InGame:
                    ShowInGame(session, aircraftName);
                    break;
                case GameState.Respawning:
                    ShowRespawning(session);
                    break;
                case GameState.ReturningToLobby:
                    ResetMatchTimer();
                    SetActivity("Returning to lobby", BuildLobbyDetails(session));
                    break;
                default:
                    RestoreMainMenu();
                    break;
            }
        }

        public void ShowLobby(GameSession session)
        {
            if (session == null) return;

            ResetMatchTimer();
            string state = session.IsHost ? "Hosting multiplayer lobby" : "In multiplayer lobby";
            SetActivity(state, BuildLobbyDetails(session));
        }

        public void ShowLoading(GameSession session)
        {
            if (session == null) return;

            ResetMatchTimer();
            SetActivity("Loading multiplayer flight", BuildLobbyDetails(session));
        }

        public void ShowSpawning(GameSession session, string aircraftName = null)
        {
            if (session == null) return;

            EnsureMatchTimer();
            SetActivity("Spawning into multiplayer", BuildFlightDetails(session, aircraftName, "Alive"), _matchStartTimestamp);
        }

        public void ShowInGame(GameSession session, string aircraftName = null)
        {
            if (session == null) return;

            EnsureMatchTimer();
            SetActivity("Flying multiplayer", BuildFlightDetails(session, aircraftName, GetLocalLifeState(session)), _matchStartTimestamp);
        }

        public void ShowRespawning(GameSession session)
        {
            if (session == null) return;

            EnsureMatchTimer();
            SetActivity("Respawning in multiplayer", BuildFlightDetails(session, null, "Respawning"), _matchStartTimestamp);
        }

        public void RestoreMainMenu()
        {
            ResetMatchTimer();
            _lastState = null;
            _lastDetails = null;
            _lastStartTimestamp = null;
            _sink.RestoreMainMenu();
        }

        private string BuildLobbyDetails(GameSession session)
        {
            string players = $"{session.PlayerCount}/{session.MaxPlayersTotal} pilots";
            return Truncate($"{players} | {DisplayMapName(session.MapName)} | {DisplayGameMode(session)}");
        }

        private string BuildFlightDetails(GameSession session, string aircraftName, string lifeState)
        {
            var local = session.GetLocalPlayer();
            string aircraft = DisplayAircraftName(session, aircraftName);
            string stats = local != null ? $"K/D {local.Kills}/{local.Deaths}" : "K/D 0/0";
            string players = $"{session.PlayerCount}/{session.MaxPlayersTotal}";
            string status = string.IsNullOrWhiteSpace(lifeState) ? GetLocalLifeState(session) : lifeState;
            return Truncate($"{aircraft} | {stats} | {status} | {players} pilots | {DisplayMapName(session.MapName)} | {DisplayGameMode(session)}");
        }

        private void EnsureMatchTimer()
        {
            if (!_matchStartTimestamp.HasValue)
                _matchStartTimestamp = _timestampProvider();
        }

        private void ResetMatchTimer()
        {
            _matchStartTimestamp = null;
        }

        private static string GetLocalLifeState(GameSession session)
        {
            var local = session.GetLocalPlayer();
            if (session.StateMachine.CurrentState == GameState.Respawning)
                return "Respawning";
            if (local == null)
                return "Alive";
            if (local.IsAwaitingRespawn)
                return "Respawning";
            return local.IsAlive ? "Alive" : "Down";
        }

        private string DisplayMapName(string mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName))
                mapName = MapHelper.GetDefaultMapName();

            try
            {
                string display = _mapDisplayNameResolver?.Invoke(mapName);
                return string.IsNullOrWhiteSpace(display) ? mapName : display;
            }
            catch
            {
                return mapName;
            }
        }

        private string DisplayAircraftName(GameSession session, string aircraftName)
        {
            if (string.IsNullOrWhiteSpace(aircraftName))
                aircraftName = session.GetLocalPlayer()?.SelectedAircraft;
            if (string.IsNullOrWhiteSpace(aircraftName))
                return "Selected aircraft";

            try
            {
                string display = _aircraftDisplayNameResolver?.Invoke(aircraftName);
                return string.IsNullOrWhiteSpace(display) ? aircraftName : display;
            }
            catch
            {
                return aircraftName;
            }
        }

        private static string DisplayGameMode(GameSession session)
        {
            if (session.GameMode == MultiplayerGameMode.TeamDogfight)
            {
                int teamCount = GameSession.ClampTeamCountForPlayers(session.TeamCount, session.PlayerCount);
                return $"Team Dogfight ({teamCount} teams)";
            }

            return "FFA Dogfight";
        }

        private void SetActivity(string state, string details, long? startTimestamp = null)
        {
            state = Truncate(state ?? string.Empty);
            details = Truncate(details ?? string.Empty);
            if (string.Equals(_lastState, state, StringComparison.Ordinal)
                && string.Equals(_lastDetails, details, StringComparison.Ordinal)
                && _lastStartTimestamp == startTimestamp)
            {
                return;
            }

            _lastState = state;
            _lastDetails = details;
            _lastStartTimestamp = startTimestamp;
            _sink.SetActivity(state, details, startTimestamp);
        }

        private static string Truncate(string value)
        {
            const int DiscordFieldLimit = 128;
            if (string.IsNullOrEmpty(value) || value.Length <= DiscordFieldLimit)
                return value ?? string.Empty;
            return value.Substring(0, DiscordFieldLimit);
        }
    }
}
