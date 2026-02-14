using System;
using System.Reflection;
using UnityEngine;
using TCAMultiplayer.Networking;

namespace TCAMultiplayer.Game
{
    /// <summary>
    /// Manages synchronized player spawning using the game's native systems.
    /// Simplified: Uses SpawnPlayerAtPosition directly (SpawnPlayerAtAirfield crashes with dummy factions).
    /// </summary>
    public class SpawnManager
    {
        public static SpawnManager Instance { get; private set; }

        // Cached reflection info
        private static Type _flightGameType;
        private static Type _startLocationType;
        private static Type _jFactionType;
        private static Type _gameDataFactionsType;
        
        // MapData reflection for proper faction lookup
        private static Type _gameDataMapsType;
        private static Type _mapDataType;
        private static MethodInfo _getMapByNameMethod;
        private static MethodInfo _getPrimaryBlueFactionMethod;

        private static PropertyInfo _flightGameInstanceProp;
        private static FieldInfo _flightGameInstanceField;
        private static MethodInfo _spawnPlayerAtPositionMethod;
        private static MethodInfo _startFlightMethod;
        private static MethodInfo _getFactionByNameMethod;

        private static bool _initialized = false;

        // State
        public bool IsSpawned { get; private set; }
        public string SpawnedAirfield { get; private set; }
        public LobbySpawnType SpawnedType { get; private set; }

        // Events
        public event Action OnSpawnComplete;
        public event Action OnSpawnFailed;
        public event Action OnPlayerDied;

        public SpawnManager()
        {
            Instance = this;
        }

        /// <summary>
        /// Initialize reflection (called automatically on first spawn).
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

                _flightGameType = Type.GetType("Falcon.Game2.FlightGame, Assembly-CSharp");
                if (_flightGameType != null)
                {
                    _flightGameInstanceProp = _flightGameType.GetProperty("Instance", flags);
                    _flightGameInstanceField = _flightGameType.GetField("Instance", flags);
                    _spawnPlayerAtPositionMethod = _flightGameType.GetMethod("SpawnPlayerAtPosition", flags);
                    _startFlightMethod = _flightGameType.GetMethod("StartFlight", flags);

                    Plugin.Log?.LogInfo($"[SpawnManager] FlightGame found: SpawnPlayerAtPosition={_spawnPlayerAtPositionMethod != null}");
                }

                _startLocationType = Type.GetType("Falcon.Game2.InstantAction.StartLocation, Assembly-CSharp");
                _jFactionType = Type.GetType("Falcon.Factions.JFaction, Assembly-CSharp");

                _gameDataFactionsType = Type.GetType("Falcon.GameDataFactions, Assembly-CSharp");
                if (_gameDataFactionsType != null)
                {
                    _getFactionByNameMethod = _gameDataFactionsType.GetMethod("GetByName", flags);
                }
                
                // MapData reflection for faction lookup via map
                _gameDataMapsType = Type.GetType("Falcon.GameDataMaps, Assembly-CSharp");
                if (_gameDataMapsType != null)
                {
                    _getMapByNameMethod = _gameDataMapsType.GetMethod("GetByName", flags);
                    Plugin.Log?.LogInfo($"[SpawnManager] GameDataMaps found: GetByName={_getMapByNameMethod != null}");
                }
                
                _mapDataType = Type.GetType("Falcon.Game2.MapData, Assembly-CSharp");
                if (_mapDataType != null)
                {
                    _getPrimaryBlueFactionMethod = _mapDataType.GetMethod("GetPrimaryBlueFaction", flags);
                    Plugin.Log?.LogInfo($"[SpawnManager] MapData found: GetPrimaryBlueFaction={_getPrimaryBlueFactionMethod != null}");
                }
                
                Plugin.Log?.LogInfo("[SpawnManager] MapData reflection initialized");

                _initialized = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[SpawnManager] Initialize error: {ex.Message}");
                _initialized = true;
            }
        }

        /// <summary>
        /// Get FlightGame.Instance through multiple fallback methods.
        /// </summary>
        private static object GetFlightGameInstance()
        {
            // 1. Try the instance captured by our patch (fastest)
            if (Patches.FlightGamePatches.FlightGameInstance != null)
                return Patches.FlightGamePatches.FlightGameInstance;

            // 2. Try the static Instance property/field via reflection
            try
            {
                if (_flightGameInstanceProp != null)
                {
                    var val = _flightGameInstanceProp.GetValue(null);
                    if (val != null) return val;
                }
                if (_flightGameInstanceField != null)
                {
                    var val = _flightGameInstanceField.GetValue(null);
                    if (val != null) return val;
                }
            }
            catch { }

            // 3. Last resort: Find it in the scene
            if (_flightGameType != null)
            {
                try
                {
                    var objects = UnityEngine.Object.FindObjectsOfType(_flightGameType, true) as UnityEngine.Object[];
                    if (objects != null && objects.Length > 0)
                    {
                        Plugin.Log?.LogInfo("[SpawnManager] Found FlightGame via FindObjectsOfType");
                        return objects[0];
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Get a faction by name.
        /// </summary>
        private static object GetFaction(string factionName)
        {
            if (_getFactionByNameMethod == null) return null;

            try
            {
                return _getFactionByNameMethod.Invoke(null, new object[] { factionName });
            }
            catch { }
            return null;
        }
        
        /// <summary>
        /// Get faction from the current map using MapData.GetPrimaryBlueFaction().
        /// This is the canonical way the game gets a valid faction for spawning.
        /// </summary>
        private static object GetMapFaction(string mapName)
        {
            if (_getMapByNameMethod == null || _getPrimaryBlueFactionMethod == null)
            {
                Plugin.Log?.LogWarning("[SpawnManager] MapData reflection not available");
                return null;
            }
            
            try
            {
                var mapData = _getMapByNameMethod.Invoke(null, new object[] { mapName });
                if (mapData == null)
                {
                    Plugin.Log?.LogWarning($"[SpawnManager] Map not found: {mapName}");
                    return null;
                }
                
                var faction = _getPrimaryBlueFactionMethod.Invoke(mapData, null);
                if (faction != null)
                {
                    Plugin.Log?.LogInfo($"[SpawnManager] Using map faction: {faction}");
                    return faction;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[SpawnManager] GetMapFaction error: {ex.Message}");
            }
            
            return null;
        }

        /// <summary>
        /// Get any available faction (fallback when specific faction not found).
        /// First tries MapData.GetPrimaryBlueFaction, then falls back to hardcoded names.
        /// </summary>
        private static object GetAnyFaction(string mapName = "ActionIsland")
        {
            // Try map-specific faction first (the canonical approach)
            var mapFaction = GetMapFaction(mapName);
            if (mapFaction != null) return mapFaction;
            
            Plugin.Log?.LogWarning("[SpawnManager] MapData faction lookup failed, trying fallback names...");
            
            // Fallback: Try common faction names
            string[] names = { "Blue", "US", "USA", "NATO", "USMC", "Player", "Ally" };
            foreach (var name in names)
            {
                var f = GetFaction(name);
                if (f != null)
                {
                    Plugin.Log?.LogInfo($"[SpawnManager] Using fallback faction: {name}");
                    return f;
                }
            }

            // Last resort: Try to find any JFaction in resources
            if (_jFactionType != null)
            {
                var factions = Resources.FindObjectsOfTypeAll(_jFactionType);
                if (factions != null && factions.Length > 0)
                {
                    Plugin.Log?.LogWarning($"[SpawnManager] Using resource fallback faction: {factions[0]}");
                    return factions[0];
                }
            }

            Plugin.Log?.LogError("[SpawnManager] No faction found - spawn will fail!");
            return null;
        }

        /// <summary>
        /// Destroy any existing player aircraft to prevent duplicates.
        /// </summary>
        private void DestroyExistingPlayer()
        {
            try
            {
                var flightGame = GetFlightGameInstance();
                if (flightGame == null) return;

                var prop = _flightGameType?.GetProperty("PlayerAircraft");
                if (prop == null) return;

                var aircraft = prop.GetValue(flightGame);
                if (aircraft != null)
                {
                    Plugin.Log?.LogWarning("[SpawnManager] Destroying existing player aircraft");
                    var component = aircraft as Component;
                    if (component != null)
                        UnityEngine.Object.Destroy(component.gameObject);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[SpawnManager] DestroyExistingPlayer error: {ex.Message}");
            }
        }

        /// <summary>
        /// Spawn player at an airfield.
        /// </summary>
        public bool SpawnPlayerAtAirfield(string airfieldName, string aircraftName, LobbySpawnType spawnType, string loadoutName = null)
        {
            Initialize();
            DestroyExistingPlayer();

            // Get spawn position from airfield
            var (position, rotation) = AirfieldHelper.GetSpawnPoint(airfieldName, spawnType);

            if (position == Vector3.zero)
            {
                Plugin.Log?.LogError($"[SpawnManager] Could not get spawn point for airfield: {airfieldName}");
                OnSpawnFailed?.Invoke();
                return false;
            }

            SpawnedAirfield = airfieldName;
            return SpawnPlayerAtPosition(position, rotation, aircraftName ?? "AV8B", spawnType, loadoutName);
        }

        /// <summary>
        /// Spawn player at a specific position.
        /// </summary>
        public bool SpawnPlayerAtPosition(Vector3 position, Quaternion rotation, string aircraftName, LobbySpawnType spawnType, string loadoutName = null)
        {
            Initialize();

            if (_spawnPlayerAtPositionMethod == null)
            {
                Plugin.Log?.LogError("[SpawnManager] SpawnPlayerAtPosition method not available");
                OnSpawnFailed?.Invoke();
                return false;
            }

            // Get loadout from lobby if not specified
            if (string.IsNullOrEmpty(loadoutName))
            {
                loadoutName = Plugin.Instance?.Lobby?.LocalSelectedLoadout ?? "Clean";
            }

            try
            {
                var flightGame = GetFlightGameInstance();
                if (flightGame == null)
                {
                    Plugin.Log?.LogError("[SpawnManager] FlightGame.Instance is null - cannot spawn");
                    OnSpawnFailed?.Invoke();
                    return false;
                }

                var mapName = Plugin.Instance?.Lobby?.MapName ?? "ActionIsland";
                var faction = GetAnyFaction(mapName);
                if (faction == null)
                {
                    Plugin.Log?.LogError("[SpawnManager] No faction found - spawn may fail");
                }

                bool isGrounded = spawnType != LobbySpawnType.Air;

                Plugin.Log?.LogInfo($"[SpawnManager] Spawning {aircraftName} at ({position.x:F0}, {position.y:F0}, {position.z:F0}) with loadout: {loadoutName}");

                _spawnPlayerAtPositionMethod.Invoke(flightGame, new object[]
                {
                    aircraftName,
                    position,
                    rotation,
                    loadoutName, // Use selected loadout
                    "Mixed",    // Ammo belt
                    isGrounded,
                    faction,
                    true,       // Is player
                    1           // Skin index
                });

                IsSpawned = true;
                SpawnedType = spawnType;

                // Initialize flight mode
                InitializeFlightMode(flightGame, spawnType);

                Plugin.Log?.LogInfo("[SpawnManager] Spawn complete!");
                OnSpawnComplete?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[SpawnManager] Spawn error: {ex.Message}");
                OnSpawnFailed?.Invoke();
                return false;
            }
        }

        /// <summary>
        /// Initialize the game's flight mode after spawning.
        /// </summary>
        private void InitializeFlightMode(object flightGame, LobbySpawnType spawnType)
        {
            if (_startFlightMethod == null) return;

            try
            {
                // FlightType.Freeflight = 1
                object mode = 1;

                // StartLocation enum
                object startLocation = 0;
                if (_startLocationType != null)
                {
                    startLocation = Enum.ToObject(_startLocationType, (int)spawnType);
                }

                Plugin.Log?.LogInfo("[SpawnManager] Calling StartFlight(Freeflight)...");
                _startFlightMethod.Invoke(flightGame, new object[] { mode, startLocation });
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[SpawnManager] StartFlight failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Respawn the player at their previous airfield.
        /// </summary>
        public bool Respawn()
        {
            if (string.IsNullOrEmpty(SpawnedAirfield))
            {
                Plugin.Log?.LogWarning("[SpawnManager] No spawn airfield recorded - using default");
                SpawnedAirfield = "DefaultAirfield";
            }

            IsSpawned = false;
            return SpawnPlayerAtAirfield(SpawnedAirfield, "AV8B", SpawnedType);
        }

        /// <summary>
        /// Notify that the player has died.
        /// </summary>
        public void NotifyPlayerDied()
        {
            IsSpawned = false;
            Plugin.Log?.LogInfo("[SpawnManager] Player died");
            OnPlayerDied?.Invoke();
        }

        /// <summary>
        /// Check if FlightGame is ready for spawning.
        /// </summary>
        public bool IsFlightReady()
        {
            Initialize();
            return GetFlightGameInstance() != null;
        }

        /// <summary>
        /// Reset spawn state.
        /// </summary>
        public void Reset()
        {
            IsSpawned = false;
            SpawnedAirfield = null;
        }
    }
}
