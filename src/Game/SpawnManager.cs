using System;
using System.Reflection;
using UnityEngine;
using TCAMultiplayer.Networking;

namespace TCAMultiplayer.Game
{
    /// <summary>
    /// Manages synchronized player spawning using the game's native systems
    /// </summary>
    public class SpawnManager
    {
        // Singleton
        public static SpawnManager Instance { get; private set; }
        
        // Cached reflection info
        private static Type _flightGameType;
        private static Type _gameLogicType;
        private static Type _playerSpawnParamsType;
        private static Type _startLocationType;
        private static Type _jFactionType;
        private static Type _gameDataFactionsType;
        
        private static PropertyInfo _flightGameInstanceProp;
        private static FieldInfo _flightGameInstanceField;
        private static MethodInfo _spawnPlayerAtAirfieldMethod;
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
        public event Action OnPlayerDied;
        
        public SpawnManager()
        {
            Instance = this;
        }
        
        /// <summary>
        /// Initialize reflection
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                
                // FlightGame type
                _flightGameType = Type.GetType("Falcon.Game2.FlightGame, Assembly-CSharp");
                if (_flightGameType != null)
                {
                    _flightGameInstanceProp = _flightGameType.GetProperty("Instance", flags);
                    _flightGameInstanceField = _flightGameType.GetField("Instance", flags);
                    _spawnPlayerAtAirfieldMethod = _flightGameType.GetMethod("SpawnPlayerAtAirfield", flags);
                    _spawnPlayerAtPositionMethod = _flightGameType.GetMethod("SpawnPlayerAtPosition", flags);
                    _startFlightMethod = _flightGameType.GetMethod("StartFlight", flags);
                    
                    Plugin.Log?.LogInfo($"[SpawnManager] FlightGame found: SpawnPlayerAtAirfield={_spawnPlayerAtAirfieldMethod != null}");
                }
                
                // GameLogic type
                _gameLogicType = Type.GetType("Falcon.Game2.GameLogic, Assembly-CSharp");
                
                // PlayerSpawnParams type
                _playerSpawnParamsType = Type.GetType("Falcon.Game2.PlayerSpawnParams, Assembly-CSharp");
                if (_playerSpawnParamsType != null)
                {
                    Plugin.Log?.LogInfo("[SpawnManager] PlayerSpawnParams type found");
                }
                
                // StartLocation enum
                _startLocationType = Type.GetType("Falcon.Game2.InstantAction.StartLocation, Assembly-CSharp");
                if (_startLocationType != null)
                {
                    Plugin.Log?.LogInfo("[SpawnManager] StartLocation type found");
                }
                
                // JFaction type
                _jFactionType = Type.GetType("Falcon.Factions.JFaction, Assembly-CSharp");
                
                // GameDataFactions for getting factions
                _gameDataFactionsType = Type.GetType("Falcon.Database.GameDataFactions, Assembly-CSharp");
                if (_gameDataFactionsType != null)
                {
                    _getFactionByNameMethod = _gameDataFactionsType.GetMethod("GetByName", flags);
                    Plugin.Log?.LogInfo($"[SpawnManager] GameDataFactions found: GetByName={_getFactionByNameMethod != null}");
                }
                
                _initialized = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[SpawnManager] Initialize error: {ex.Message}");
                _initialized = true;
            }
        }
        
        /// <summary>
        /// Get FlightGame.Instance
        /// </summary>
        private static object GetFlightGameInstance()
        {
            try
            {
                if (_flightGameInstanceProp != null)
                {
                    return _flightGameInstanceProp.GetValue(null);
                }
                if (_flightGameInstanceField != null)
                {
                    return _flightGameInstanceField.GetValue(null);
                }
            }
            catch { }
            return null;
        }
        
        /// <summary>
        /// Get a faction by name
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
        /// Spawn the local player at an airfield using the game's native system
        /// </summary>
        public bool SpawnPlayerAtAirfield(string airfieldName, string aircraftName, LobbySpawnType spawnType, string factionName = "USA")
        {
            Initialize();
            
            if (_spawnPlayerAtAirfieldMethod == null || _playerSpawnParamsType == null)
            {
                Plugin.Log?.LogError("[SpawnManager] Cannot spawn - reflection not available");
                return SpawnPlayerFallback(airfieldName, spawnType);
            }
            
            try
            {
                // Get FlightGame instance
                var flightGame = GetFlightGameInstance();
                if (flightGame == null)
                {
                    Plugin.Log?.LogError("[SpawnManager] FlightGame.Instance is null");
                    return SpawnPlayerFallback(airfieldName, spawnType);
                }
                
                // Create PlayerSpawnParams
                var spawnParams = Activator.CreateInstance(_playerSpawnParamsType);
                
                // Set fields
                SetField(spawnParams, "AircraftName", aircraftName ?? "AV8B");
                SetField(spawnParams, "LoadoutName", "Clean");
                SetField(spawnParams, "AmmoBeltName", "Mixed");
                SetField(spawnParams, "Faction", factionName);
                SetField(spawnParams, "Airfield", airfieldName);
                
                // Set StartLocation enum
                if (_startLocationType != null)
                {
                    object startLocation = Enum.ToObject(_startLocationType, (int)spawnType);
                    SetField(spawnParams, "StartLocation", startLocation);
                }
                
                // Get faction
                var faction = GetFaction(factionName);
                if (faction == null)
                {
                    Plugin.Log?.LogWarning($"[SpawnManager] Faction not found: {factionName}, using null");
                }
                
                // Call SpawnPlayerAtAirfield(PlayerSpawnParams, JFaction, bool inNavMode)
                _spawnPlayerAtAirfieldMethod.Invoke(flightGame, new object[] { spawnParams, faction, true });
                
                // Mark as spawned
                IsSpawned = true;
                SpawnedAirfield = airfieldName;
                SpawnedType = spawnType;
                
                Plugin.Log?.LogInfo($"[SpawnManager] Spawned player at {airfieldName} ({spawnType}) in {aircraftName}");
                
                OnSpawnComplete?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[SpawnManager] SpawnPlayerAtAirfield error: {ex.Message}");
                return SpawnPlayerFallback(airfieldName, spawnType);
            }
        }
        
        /// <summary>
        /// Fallback spawn using direct position
        /// </summary>
        private bool SpawnPlayerFallback(string airfieldName, LobbySpawnType spawnType)
        {
            Plugin.Log?.LogInfo("[SpawnManager] Using fallback spawn method");
            
            // Get spawn point from AirfieldHelper
            var (position, rotation) = AirfieldHelper.GetSpawnPoint(airfieldName, spawnType);
            
            if (position == Vector3.zero)
            {
                Plugin.Log?.LogError("[SpawnManager] Could not get spawn point");
                return false;
            }
            
            // Try direct position spawn
            return SpawnPlayerAtPosition(position, rotation, "AV8B", spawnType);
        }
        
        /// <summary>
        /// Spawn player at a specific position
        /// </summary>
        public bool SpawnPlayerAtPosition(Vector3 position, Quaternion rotation, string aircraftName, LobbySpawnType spawnType)
        {
            Initialize();
            
            if (_spawnPlayerAtPositionMethod == null)
            {
                Plugin.Log?.LogError("[SpawnManager] SpawnPlayerAtPosition method not available");
                return false;
            }
            
            try
            {
                var flightGame = GetFlightGameInstance();
                if (flightGame == null)
                {
                    Plugin.Log?.LogError("[SpawnManager] FlightGame.Instance is null");
                    return false;
                }
                
                var faction = GetFaction("USA");
                bool isGrounded = spawnType != LobbySpawnType.Air;
                
                // SpawnPlayerAtPosition(string aircraftName, Vector3 position, Quaternion rotation, 
                //                       string loadout, string ammoBelt, bool isGrounded, 
                //                       JFaction faction, bool inNavMode, int count)
                _spawnPlayerAtPositionMethod.Invoke(flightGame, new object[]
                {
                    aircraftName,
                    position,
                    rotation,
                    "Clean",
                    "Mixed",
                    isGrounded,
                    faction,
                    true,
                    1
                });
                
                IsSpawned = true;
                SpawnedType = spawnType;
                
                Plugin.Log?.LogInfo($"[SpawnManager] Spawned player at position ({position.x:F0}, {position.y:F0}, {position.z:F0})");
                
                OnSpawnComplete?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[SpawnManager] SpawnPlayerAtPosition error: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Helper to set field value via reflection
        /// </summary>
        private static void SetField(object obj, string fieldName, object value)
        {
            try
            {
                var field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    field.SetValue(obj, value);
                }
            }
            catch { }
        }
        
        /// <summary>
        /// Respawn the player at their original airfield
        /// </summary>
        public bool Respawn()
        {
            if (string.IsNullOrEmpty(SpawnedAirfield))
            {
                Plugin.Log?.LogWarning("[SpawnManager] No spawn airfield recorded");
                return false;
            }
            
            IsSpawned = false;
            
            // Re-use the stored airfield and spawn type
            return SpawnPlayerAtAirfield(SpawnedAirfield, "AV8B", SpawnedType);
        }
        
        /// <summary>
        /// Notify that the player has died
        /// </summary>
        public void NotifyPlayerDied()
        {
            IsSpawned = false;
            Plugin.Log?.LogInfo("[SpawnManager] Player died");
            OnPlayerDied?.Invoke();
        }
        
        /// <summary>
        /// Check if the game's flight systems are ready for spawning
        /// </summary>
        public bool IsFlightReady()
        {
            Initialize();
            
            var flightGame = GetFlightGameInstance();
            return flightGame != null;
        }
        
        /// <summary>
        /// Reset state
        /// </summary>
        public void Reset()
        {
            IsSpawned = false;
            SpawnedAirfield = null;
        }
    }
}
