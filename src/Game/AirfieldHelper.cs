using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using TCAMultiplayer.Networking;

namespace TCAMultiplayer.Game
{
    /// <summary>
    /// Helper for interacting with game's airfield system via reflection
    /// </summary>
    public static class AirfieldHelper
    {
        // Cached reflection info
        private static Type _airfield2Type;
        private static Type _gameLogicType;
        private static Type _gameMapType;
        private static PropertyInfo _gameLogicInstanceProp;
        private static PropertyInfo _loadedMapProp;
        private static MethodInfo _getAirfieldByNameMethod;
        private static FieldInfo _displayNameField;
        private static MethodInfo _getRunwaySpawnMethod;
        private static MethodInfo _getRampSpawnMethod;
        
        private static bool _initialized = false;
        
        // Cached airfield data
        private static Dictionary<string, object> _cachedAirfields = new Dictionary<string, object>();
        private static string[] _cachedAirfieldNames = new string[0];
        private static float _lastCacheTime = 0f;
        // CACHE_DURATION now in NetworkConfig
        
        /// <summary>
        /// Initialize reflection
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                
                // Airfield2 type
                _airfield2Type = Type.GetType("Falcon.Game2.Airfield2, Assembly-CSharp");
                if (_airfield2Type != null)
                {
                    _displayNameField = _airfield2Type.GetField("DisplayName", flags);
                    _getRunwaySpawnMethod = _airfield2Type.GetMethod("GetRunwaySpawn", flags, null, new Type[0], null);
                    _getRampSpawnMethod = _airfield2Type.GetMethod("GetRampSpawn", flags, null, new Type[0], null);
                    
                    Plugin.Log?.LogInfo($"[AirfieldHelper] Airfield2 type found: DisplayName={_displayNameField != null}, GetRunwaySpawn={_getRunwaySpawnMethod != null}");
                }
                else
                {
                    Plugin.Log?.LogWarning("[AirfieldHelper] Airfield2 type not found");
                }
                
                // GameLogic type
                _gameLogicType = Type.GetType("Falcon.Game2.GameLogic, Assembly-CSharp");
                if (_gameLogicType != null)
                {
                    _gameLogicInstanceProp = _gameLogicType.GetProperty("Instance", flags) 
                        ?? _gameLogicType.GetField("Instance", flags)?.DeclaringType?.GetProperty("Instance", flags);
                    
                    // Try field if property not found
                    if (_gameLogicInstanceProp == null)
                    {
                        var instanceField = _gameLogicType.GetField("Instance", flags);
                        if (instanceField != null)
                        {
                            Plugin.Log?.LogInfo("[AirfieldHelper] GameLogic.Instance is a field, not property");
                        }
                    }
                    
                    _loadedMapProp = _gameLogicType.GetProperty("LoadedMap", flags);
                    
                    Plugin.Log?.LogInfo($"[AirfieldHelper] GameLogic type found: Instance={_gameLogicInstanceProp != null}, LoadedMap={_loadedMapProp != null}");
                }
                
                // GameMap type for GetAirfieldByMapName
                _gameMapType = Type.GetType("Falcon.Game2.GameMap, Assembly-CSharp");
                if (_gameMapType != null)
                {
                    _getAirfieldByNameMethod = _gameMapType.GetMethod("GetAirfieldByMapName", flags);
                    Plugin.Log?.LogInfo($"[AirfieldHelper] GameMap type found: GetAirfieldByMapName={_getAirfieldByNameMethod != null}");
                }
                
                _initialized = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[AirfieldHelper] Initialize error: {ex.Message}");
                _initialized = true; // Don't retry
            }
        }
        
        /// <summary>
        /// Get all airfield names from the current map
        /// </summary>
        public static string[] GetAirfieldNames()
        {
            Initialize();
            
            // Use cache if recent
            if (Time.unscaledTime - _lastCacheTime < NetworkConfig.AIRFIELD_CACHE_DURATION && _cachedAirfieldNames.Length > 0)
            {
                return _cachedAirfieldNames;
            }
            
            try
            {
                if (_airfield2Type == null) return new string[0];
                
                // Find all Airfield2 objects in scene
                var airfields = UnityEngine.Object.FindObjectsOfType(_airfield2Type) as Component[];
                if (LogHelper.ShouldLogInterval("AirfieldHelper.GetAirfieldNames", 10f))
                {
                    Plugin.Log?.LogInfo($"[AirfieldHelper] Searching for airfields of type {_airfield2Type.Name}... Found: {airfields?.Length ?? 0}");
                }
                if (airfields == null || airfields.Length == 0)
                {
                    Plugin.Log?.LogWarning("[AirfieldHelper] No airfields found in scene");
                    return new string[0];
                }
                
                var names = new List<string>();
                _cachedAirfields.Clear();
                
                foreach (var airfield in airfields)
                {
                    string name = GetAirfieldDisplayName(airfield);
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                        _cachedAirfields[name] = airfield;
                    }
                }
                
                _cachedAirfieldNames = names.ToArray();
                _lastCacheTime = Time.unscaledTime;
                
                Plugin.Log?.LogInfo($"[AirfieldHelper] Found {_cachedAirfieldNames.Length} airfields");
                
                return _cachedAirfieldNames;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[AirfieldHelper] GetAirfieldNames error: {ex.Message}");
                return new string[0];
            }
        }
        
        /// <summary>
        /// Get display name from airfield object
        /// </summary>
        private static string GetAirfieldDisplayName(object airfield)
        {
            if (airfield == null || _displayNameField == null) return null;
            
            try
            {
                string name = _displayNameField.GetValue(airfield) as string;
                
                // If DisplayName is empty, use GameObject name
                if (string.IsNullOrEmpty(name) && airfield is Component comp)
                {
                    name = comp.gameObject.name;
                }
                
                return name;
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// Get airfield by name
        /// </summary>
        public static object GetAirfield(string name)
        {
            Initialize();
            
            // Check cache
            if (_cachedAirfields.TryGetValue(name, out var cached))
            {
                return cached;
            }
            
            // Refresh cache
            GetAirfieldNames();
            
            // Try again
            if (_cachedAirfields.TryGetValue(name, out cached))
            {
                return cached;
            }
            
            // Try via GameLogic.LoadedMap.GetAirfieldByMapName
            try
            {
                var gameLogic = GetGameLogicInstance();
                if (gameLogic != null && _loadedMapProp != null && _getAirfieldByNameMethod != null)
                {
                    var loadedMap = _loadedMapProp.GetValue(gameLogic);
                    if (loadedMap != null)
                    {
                        var airfield = _getAirfieldByNameMethod.Invoke(loadedMap, new object[] { name });
                        if (airfield != null)
                        {
                            _cachedAirfields[name] = airfield;
                            return airfield;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[AirfieldHelper] GetAirfield error: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Get GameLogic.Instance
        /// </summary>
        private static object GetGameLogicInstance()
        {
            if (_gameLogicType == null) return null;
            
            try
            {
                // Try property
                if (_gameLogicInstanceProp != null)
                {
                    return _gameLogicInstanceProp.GetValue(null);
                }
                
                // Try field
                var instanceField = _gameLogicType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceField != null)
                {
                    return instanceField.GetValue(null);
                }
            }
            catch { }
            
            return null;
        }
        
        /// <summary>
        /// Get spawn position and rotation from airfield
        /// </summary>
        public static (Vector3 position, Quaternion rotation) GetSpawnPoint(string airfieldName, Networking.LobbySpawnType spawnType)
        {
            Initialize();
            
            var airfield = GetAirfield(airfieldName);
            if (airfield == null)
            {
                Plugin.Log?.LogWarning($"[AirfieldHelper] Airfield not found: {airfieldName}");
                return (Vector3.zero, Quaternion.identity);
            }
            
            return GetSpawnPoint(airfield, spawnType);
        }
        
        /// <summary>
        /// Get spawn position and rotation from airfield object
        /// </summary>
        public static (Vector3 position, Quaternion rotation) GetSpawnPoint(object airfield, Networking.LobbySpawnType spawnType)
        {
            if (airfield == null) return (Vector3.zero, Quaternion.identity);
            
            try
            {
                MethodInfo spawnMethod = null;
                
                switch (spawnType)
                {
                    case Networking.LobbySpawnType.Air:
                    case Networking.LobbySpawnType.Runway:
                        spawnMethod = _getRunwaySpawnMethod;
                        break;
                    case Networking.LobbySpawnType.Ramp:
                        spawnMethod = _getRampSpawnMethod;
                        break;
                }
                
                if (spawnMethod == null)
                {
                    Plugin.Log?.LogWarning("[AirfieldHelper] Spawn method not found");
                    
                    // Fallback to airfield transform
                    if (airfield is Component comp)
                    {
                        return (comp.transform.position, comp.transform.rotation);
                    }
                    return (Vector3.zero, Quaternion.identity);
                }
                
                // Call GetRunwaySpawn or GetRampSpawn - returns ValueTuple<Vector3, Quaternion>
                var result = spawnMethod.Invoke(airfield, null);
                
                if (result != null)
                {
                    // Handle ValueTuple<Vector3, Quaternion>
                    var tupleType = result.GetType();
                    var item1Field = tupleType.GetField("Item1");
                    var item2Field = tupleType.GetField("Item2");
                    
                    if (item1Field != null && item2Field != null)
                    {
                        Vector3 position = (Vector3)item1Field.GetValue(result);
                        Quaternion rotation = (Quaternion)item2Field.GetValue(result);
                        
                        // For Air spawn, adjust position
                        if (spawnType == Networking.LobbySpawnType.Air)
                        {
                            // 300m above, 2000m behind (same as game's logic)
                            position += Vector3.up * 300f;
                            position -= rotation * Vector3.forward * 2000f;
                        }
                        
                        return (position, rotation);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[AirfieldHelper] GetSpawnPoint error: {ex.Message}");
            }
            
            // Fallback
            if (airfield is Component comp2)
            {
                return (comp2.transform.position, comp2.transform.rotation);
            }
            
            return (Vector3.zero, Quaternion.identity);
        }
        
        /// <summary>
        /// Clear cached data
        /// </summary>
        public static void ClearCache()
        {
            _cachedAirfields.Clear();
            _cachedAirfieldNames = new string[0];
            _lastCacheTime = 0f;
        }
        
        /// <summary>
        /// Check if map is loaded
        /// </summary>
        public static bool IsMapLoaded()
        {
            Initialize();
            
            try
            {
                var gameLogic = GetGameLogicInstance();
                if (gameLogic != null && _loadedMapProp != null)
                {
                    var loadedMap = _loadedMapProp.GetValue(gameLogic);
                    return loadedMap != null;
                }
            }
            catch { }
            
            return false;
        }
        
        /// <summary>
        /// Get current map name
        /// </summary>
        public static string GetCurrentMapName()
        {
            Initialize();
            
            try
            {
                var gameLogic = GetGameLogicInstance();
                if (gameLogic != null && _loadedMapProp != null)
                {
                    var loadedMap = _loadedMapProp.GetValue(gameLogic);
                    if (loadedMap != null)
                    {
                        // Try to get MapName property
                        var mapNameProp = _gameMapType?.GetProperty("MapName", BindingFlags.Public | BindingFlags.Instance);
                        if (mapNameProp != null)
                        {
                            return mapNameProp.GetValue(loadedMap) as string;
                        }
                        
                        // Try name field
                        var nameField = _gameMapType?.GetField("name", BindingFlags.Public | BindingFlags.Instance);
                        if (nameField != null)
                        {
                            return nameField.GetValue(loadedMap) as string;
                        }
                    }
                }
            }
            catch { }
            
            return "Unknown";
        }
    }
}
