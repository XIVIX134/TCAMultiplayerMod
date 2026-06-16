using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Game
{
    /// <summary>
    /// Helper for interacting with game's airfield system via reflection
    /// </summary>
    public static class AirfieldHelper
    {
        private const string Tag = "AIRFIELD-HELPER";

        // Cached reflection info
        private static Type _airfield2Type;
        private static Type _gameLogicType;
        private static Type _gameMapType;
        private static PropertyInfo _gameLogicInstanceProp;
        private static PropertyInfo _loadedMapProp;
        private static MethodInfo _getAirfieldByNameMethod;
        private static FieldInfo _displayNameField;
        private static FieldInfo _runwaysField;
        private static FieldInfo _rampSpawnsField;
        private static MethodInfo _getRunwaySpawnMethod;
        private static MethodInfo _getRunwaySpawnIndexedMethod;
        private static MethodInfo _getRampSpawnMethod;
        private static MethodInfo _getRampSpawnTransformMethod;
        private static MethodInfo _getMultipleLineUpPointsMethod;
        
        private static bool _initialized = false;
        
        // Cached airfield data
        private static Dictionary<string, object> _cachedAirfields = new Dictionary<string, object>();
        private static string[] _cachedAirfieldNames = new string[0];
        private static string[] _lastKnownAirfieldNames = new string[0];
        private static float _lastCacheTime = 0f;
        // Cache duration hardcoded (was NetworkConfig.AIRFIELD_CACHE_DURATION)
        
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
                    _runwaysField = _airfield2Type.GetField("Runways", flags);
                    _rampSpawnsField = _airfield2Type.GetField("RampSpawns", flags);
                    _getRunwaySpawnMethod = _airfield2Type.GetMethod("GetRunwaySpawn", flags, null, new Type[0], null);
                    _getRunwaySpawnIndexedMethod = _airfield2Type.GetMethod("GetRunwaySpawn", flags, null, new[] { typeof(int) }, null);
                    _getRampSpawnMethod = _airfield2Type.GetMethod("GetRampSpawn", flags, null, new Type[0], null);
                    _getRampSpawnTransformMethod = _airfield2Type.GetMethod("GetRampSpawnTransform", flags, null, new[] { typeof(int) }, null);

                    var runwayType = Type.GetType("Falcon.Game2.Airfields.Runway, Assembly-CSharp");
                    _getMultipleLineUpPointsMethod = runwayType?.GetMethod("GetMultipleLineUpPoints", flags, null, new[] { typeof(int) }, null);
                    
                    Log.Info(Tag, $"[AirfieldHelper] Airfield2 type found: DisplayName={_displayNameField != null}, GetRunwaySpawn={_getRunwaySpawnMethod != null}, GetRunwaySpawn(int)={_getRunwaySpawnIndexedMethod != null}, RampSpawns={_rampSpawnsField != null}");
                }
                else
                {
                    Log.Warning(Tag, "[AirfieldHelper] Airfield2 type not found");
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
                            Log.Info(Tag, "[AirfieldHelper] GameLogic.Instance is a field, not property");
                        }
                    }
                    
                    _loadedMapProp = _gameLogicType.GetProperty("LoadedMap", flags);
                    
                    Log.Info(Tag, $"[AirfieldHelper] GameLogic type found: Instance={_gameLogicInstanceProp != null}, LoadedMap={_loadedMapProp != null}");
                }
                
                // GameMap type for GetAirfieldByMapName
                _gameMapType = Type.GetType("Falcon.Game2.GameMap, Assembly-CSharp");
                if (_gameMapType != null)
                {
                    _getAirfieldByNameMethod = _gameMapType.GetMethod("GetAirfieldByMapName", flags);
                    Log.Info(Tag, $"[AirfieldHelper] GameMap type found: GetAirfieldByMapName={_getAirfieldByNameMethod != null}");
                }
                
                _initialized = true;
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"[AirfieldHelper] Initialize error: {ex.Message}");
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
            if (Time.unscaledTime - _lastCacheTime < 5f && _cachedAirfieldNames.Length > 0)
            {
                return _cachedAirfieldNames;
            }
            
            try
            {
                if (_airfield2Type == null) return new string[0];
                
                // Find all Airfield2 objects in scene
                var airfields = FindAirfieldComponents(includeInactive: true);
                Log.Info(Tag, $"[AirfieldHelper] Searching for airfields of type {_airfield2Type.Name}... Found: {airfields?.Length ?? 0}");
                if (airfields == null || airfields.Length == 0)
                {
                    _cachedAirfields.Clear();
                    if (_lastKnownAirfieldNames.Length > 0)
                    {
                        _cachedAirfieldNames = (string[])_lastKnownAirfieldNames.Clone();
                        _lastCacheTime = Time.unscaledTime;
                        Log.Info(Tag, $"[AirfieldHelper] No live airfields found; using {_cachedAirfieldNames.Length} cached lobby airfields");
                        return _cachedAirfieldNames;
                    }

                    var lobbyFallbackNames = GetLobbyAirfieldFallbacks();
                    if (lobbyFallbackNames.Length > 0)
                    {
                        _cachedAirfieldNames = (string[])lobbyFallbackNames.Clone();
                        _lastKnownAirfieldNames = (string[])lobbyFallbackNames.Clone();
                        _lastCacheTime = Time.unscaledTime;
                        Log.Info(Tag, $"[AirfieldHelper] No live airfields found; using {_cachedAirfieldNames.Length} lobby-selected airfield(s)");
                        return _cachedAirfieldNames;
                    }

                    Log.Warning(Tag, "[AirfieldHelper] No airfields found in scene");
                    return new string[0];
                }
                
                var names = new List<string>();
                _cachedAirfields.Clear();
                var selectedAirfields = new Dictionary<string, Component>(StringComparer.OrdinalIgnoreCase);
                int duplicateNameCount = 0;

                foreach (var airfield in airfields)
                {
                    string name = GetAirfieldDisplayName(airfield);
                    if (string.IsNullOrEmpty(name)) continue;

                    if (selectedAirfields.TryGetValue(name, out var existing))
                    {
                        duplicateNameCount++;
                        // Prefer the newest-loaded scene instance when duplicate airfield names exist.
                        // This avoids stale spawn points when duplicate map scenes were left loaded.
                        if (ShouldPreferAirfieldCandidate(existing, airfield))
                        {
                            selectedAirfields[name] = airfield;
                        }
                        continue;
                    }

                    selectedAirfields[name] = airfield;
                    names.Add(name);
                }

                foreach (var entry in selectedAirfields)
                {
                    _cachedAirfields[entry.Key] = entry.Value;
                }
                
                _cachedAirfieldNames = names.ToArray();
                if (_cachedAirfieldNames.Length > 0)
                {
                    _lastKnownAirfieldNames = (string[])_cachedAirfieldNames.Clone();
                }
                _lastCacheTime = Time.unscaledTime;
                
                Log.Info(Tag, $"[AirfieldHelper] Found {_cachedAirfieldNames.Length} airfields");
                if (duplicateNameCount > 0)
                {
                    Log.Warning(Tag, $"[AirfieldHelper] Detected {duplicateNameCount} duplicate airfield object(s); using newest scene instances");
                }
                
                return _cachedAirfieldNames;
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"[AirfieldHelper] GetAirfieldNames error: {ex.Message}");
                return new string[0];
            }
        }

        private static Component[] FindAirfieldComponents(bool includeInactive)
        {
            if (_airfield2Type == null) return null;

            try
            {
                // includeInactive=true is important during async scene load/spawn phases.
                var inactiveMode = includeInactive
                    ? FindObjectsInactive.Include
                    : FindObjectsInactive.Exclude;
                var results = UnityEngine.Object.FindObjectsByType(
                    _airfield2Type,
                    inactiveMode,
                    FindObjectsSortMode.None);
                return ToComponents(results);
            }
            catch
            {
                var results = UnityEngine.Object.FindObjectsByType(
                    _airfield2Type,
                    FindObjectsSortMode.None);
                return ToComponents(results);
            }
        }

        private static Component[] ToComponents(UnityEngine.Object[] objects)
        {
            if (objects == null || objects.Length == 0)
                return new Component[0];

            var components = new List<Component>(objects.Length);
            foreach (var obj in objects)
            {
                if (obj is Component component && component != null)
                    components.Add(component);
            }
            return components.ToArray();
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

        private static bool ShouldPreferAirfieldCandidate(Component existing, Component candidate)
        {
            if (candidate == null) return false;
            if (existing == null) return true;

            try
            {
                int existingHandle = existing.gameObject.scene.handle;
                int candidateHandle = candidate.gameObject.scene.handle;
                if (candidateHandle != existingHandle)
                {
                    return candidateHandle > existingHandle;
                }
            }
            catch
            {
                // Ignore and keep existing on comparison failure.
            }

            return false;
        }

        private static string[] GetLobbyAirfieldFallbacks()
        {
            // Plugin.Instance pattern not available; return empty array
            return new string[0];
        }
        
        /// <summary>
        /// Get airfield by name
        /// </summary>
        public static object GetAirfield(string name)
        {
            Initialize();
            if (string.IsNullOrWhiteSpace(name)) return null;
            
            // Check cache
            if (_cachedAirfields.TryGetValue(name, out var cached))
            {
                if (IsUnityObjectAlive(cached))
                {
                    return cached;
                }

                _cachedAirfields.Remove(name);
            }
            
            // Refresh cache
            GetAirfieldNames();
            
            // Try again
            if (_cachedAirfields.TryGetValue(name, out cached))
            {
                if (IsUnityObjectAlive(cached))
                {
                    return cached;
                }

                _cachedAirfields.Remove(name);
            }

            // Normalize-based cache match (handles variants like "Toramaru Airfield" vs "ToramaruAirfield")
            string normalizedRequested = NormalizeAirfieldName(name);
            foreach (var kvp in _cachedAirfields)
            {
                if (!IsUnityObjectAlive(kvp.Value)) continue;
                if (NormalizeAirfieldName(kvp.Key) == normalizedRequested)
                {
                    _cachedAirfields[name] = kvp.Value;
                    return kvp.Value;
                }
            }

            // Direct scene scan fallback (independent from GameLogic map reflection).
            try
            {
                var airfields = FindAirfieldComponents(includeInactive: true);
                if (airfields != null)
                {
                    Component firstAlive = null;
                    foreach (var airfieldComp in airfields)
                    {
                        if (airfieldComp == null || airfieldComp.gameObject == null) continue;
                        if (firstAlive == null) firstAlive = airfieldComp;

                        string displayName = GetAirfieldDisplayName(airfieldComp);
                        string objectName = airfieldComp.gameObject.name;
                        bool isMatch =
                            string.Equals(displayName, name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(objectName, name, StringComparison.OrdinalIgnoreCase) ||
                            NormalizeAirfieldName(displayName) == normalizedRequested ||
                            NormalizeAirfieldName(objectName) == normalizedRequested;

                        if (!isMatch) continue;

                        _cachedAirfields[name] = airfieldComp;
                        if (!string.IsNullOrEmpty(displayName))
                        {
                            _cachedAirfields[displayName] = airfieldComp;
                        }
                        if (!string.IsNullOrEmpty(objectName))
                        {
                            _cachedAirfields[objectName] = airfieldComp;
                        }
                        return airfieldComp;
                    }

                    // As last resort, return first available airfield instead of hard-failing spawn.
                    if (firstAlive != null)
                    {
                        string fallbackName = GetAirfieldDisplayName(firstAlive) ?? firstAlive.gameObject.name;
                        Log.Warning(Tag, $"[AirfieldHelper] Airfield '{name}' not found, falling back to '{fallbackName}'");
                        _cachedAirfields[name] = firstAlive;
                        return firstAlive;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"[AirfieldHelper] Direct airfield scan error: {ex.Message}");
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
                Log.Warning(Tag, $"[AirfieldHelper] GetAirfield error: {ex.Message}");
            }
            
            return null;
        }

        private static string NormalizeAirfieldName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var chars = new List<char>(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsWhiteSpace(c) || c == '_' || c == '-') continue;
                chars.Add(char.ToLowerInvariant(c));
            }
            return new string(chars.ToArray());
        }

        private static bool IsUnityObjectAlive(object value)
        {
            if (value == null) return false;
            if (value is UnityEngine.Object unityObj)
            {
                return unityObj != null;
            }
            return true;
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
        public static (Vector3 position, Quaternion rotation) GetSpawnPoint(string airfieldName, Core.LobbySpawnType spawnType)
        {
            return TryGetSpawnPoint(airfieldName, spawnType, out var position, out var rotation)
                ? (position, rotation)
                : (Vector3.zero, Quaternion.identity);
        }

        public static bool TryGetSpawnPoint(
            string airfieldName,
            Core.LobbySpawnType spawnType,
            out Vector3 position,
            out Quaternion rotation)
        {
            return TryGetSpawnPoint(airfieldName, spawnType, 0, 1, out position, out rotation);
        }

        public static bool TryGetSpawnPoint(
            string airfieldName,
            Core.LobbySpawnType spawnType,
            int spawnSlot,
            int spawnCount,
            out Vector3 position,
            out Quaternion rotation)
        {
            Initialize();

            var airfield = GetAirfield(airfieldName);
            if (airfield == null)
            {
                Log.Warning(Tag, $"[AirfieldHelper] Airfield not found: {airfieldName}");
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }

            return TryGetSpawnPoint(airfield, spawnType, spawnSlot, spawnCount, out position, out rotation);
        }
        
        /// <summary>
        /// Get spawn position and rotation from airfield object
        /// </summary>
        public static (Vector3 position, Quaternion rotation) GetSpawnPoint(object airfield, Core.LobbySpawnType spawnType)
        {
            return TryGetSpawnPoint(airfield, spawnType, out var position, out var rotation)
                ? (position, rotation)
                : (Vector3.zero, Quaternion.identity);
        }

        public static bool TryGetSpawnPoint(
            object airfield,
            Core.LobbySpawnType spawnType,
            out Vector3 position,
            out Quaternion rotation)
        {
            return TryGetSpawnPoint(airfield, spawnType, 0, 1, out position, out rotation);
        }

        public static bool TryGetSpawnPoint(
            object airfield,
            Core.LobbySpawnType spawnType,
            int spawnSlot,
            int spawnCount,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (airfield == null) return false;
            
            try
            {
                spawnSlot = Mathf.Max(0, spawnSlot);
                spawnCount = Mathf.Max(1, spawnCount);

                if (spawnType == Core.LobbySpawnType.Ramp)
                {
                    if (TryGetRampSpawnPoint(airfield, spawnSlot, out position, out rotation))
                        return true;

                    Log.Warning(Tag, $"[AirfieldHelper] Ramp slot {spawnSlot} unavailable; falling back to runway line-up");
                }

                if (TryGetRunwaySpawnPoint(airfield, spawnSlot, spawnCount, out position, out rotation))
                {
                    if (spawnType == Core.LobbySpawnType.InAir)
                    {
                        position += Vector3.up * 300f;
                        position -= rotation * Vector3.forward * 2000f;
                    }

                    return true;
                }

                MethodInfo spawnMethod = spawnType == Core.LobbySpawnType.Ramp
                    ? _getRampSpawnMethod
                    : _getRunwaySpawnMethod;
                if (TryInvokeSpawnTuple(spawnMethod, airfield, null, out position, out rotation))
                {
                    if (spawnType == Core.LobbySpawnType.InAir)
                    {
                        position += Vector3.up * 300f;
                        position -= rotation * Vector3.forward * 2000f;
                    }

                    return true;
                }

                Log.Warning(Tag, "[AirfieldHelper] Spawn method not found");

                // Air starts can tolerate a generic transform fallback; ground starts must use native slots.
                if (spawnType == Core.LobbySpawnType.InAir && airfield is Component comp)
                {
                    position = comp.transform.position;
                    rotation = comp.transform.rotation;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"[AirfieldHelper] GetSpawnPoint error: {ex.Message}");
            }
            
            // Fallback only for air starts. Ground/ramp callers should fail instead of spawning off surface.
            if (spawnType == Core.LobbySpawnType.InAir && airfield is Component comp2 && comp2 != null)
            {
                try
                {
                    if (comp2.gameObject != null)
                    {
                        position = comp2.transform.position;
                        rotation = comp2.transform.rotation;
                        return true;
                    }
                }
                catch
                {
                    // Airfield component was destroyed; return zero so caller can handle gracefully.
                }
            }
            
            return false;
        }

        private static bool TryGetRunwaySpawnPoint(
            object airfield,
            int spawnSlot,
            int spawnCount,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (TryGetRunwayLineUpPoint(airfield, spawnSlot, spawnCount, out position, out rotation))
                return true;

            int runwayCount = GetListCount(_runwaysField?.GetValue(airfield));
            int runwayIndex = runwayCount > 0 ? spawnSlot % runwayCount : spawnSlot;
            if (TryInvokeSpawnTuple(_getRunwaySpawnIndexedMethod, airfield, new object[] { runwayIndex }, out position, out rotation))
                return true;

            return TryInvokeSpawnTuple(_getRunwaySpawnMethod, airfield, null, out position, out rotation);
        }

        private static bool TryGetRunwayLineUpPoint(
            object airfield,
            int spawnSlot,
            int spawnCount,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (spawnCount <= 1)
                return false;
            if (_runwaysField == null || _getMultipleLineUpPointsMethod == null)
                return false;

            var runways = _runwaysField.GetValue(airfield) as IList;
            if (runways == null || runways.Count == 0)
                return false;

            int runwayIndex = spawnSlot % runways.Count;
            int localSlot = spawnSlot / runways.Count;
            int localCount = Mathf.Max(localSlot + 1, Mathf.CeilToInt(spawnCount / (float)runways.Count));
            object runway = runways[runwayIndex];
            if (runway == null)
                return false;

            try
            {
                var result = _getMultipleLineUpPointsMethod.Invoke(runway, new object[] { localCount });
                if (!(result is Vector3[] points) || localSlot >= points.Length)
                    return false;

                position = points[localSlot];
                if (runway is Component comp && comp != null)
                {
                    rotation = comp.transform.rotation;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"[AirfieldHelper] Runway line-up spawn failed: {ex.Message}");
            }

            return false;
        }

        private static bool TryGetRampSpawnPoint(
            object airfield,
            int spawnSlot,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            var rampSpawns = _rampSpawnsField?.GetValue(airfield) as IList;
            int rampCount = GetListCount(rampSpawns);
            if (rampCount <= 0 || spawnSlot >= rampCount)
                return false;

            if (TryInvokeSpawnTuple(_getRampSpawnTransformMethod, airfield, new object[] { spawnSlot }, out position, out rotation))
                return true;

            if (rampSpawns[spawnSlot] is Transform spawnTransform && spawnTransform != null)
            {
                position = spawnTransform.position;
                rotation = spawnTransform.rotation;
                return true;
            }

            return false;
        }

        private static bool TryInvokeSpawnTuple(
            MethodInfo method,
            object target,
            object[] args,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (method == null || target == null)
                return false;

            object result;
            try
            {
                result = method.Invoke(target, args);
                if (result == null)
                    return false;
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"[AirfieldHelper] Native spawn method '{method.Name}' failed: {ex.Message}");
                return false;
            }

            var tupleType = result.GetType();
            var item1Field = tupleType.GetField("Item1");
            var item2Field = tupleType.GetField("Item2");
            if (item1Field == null || item2Field == null)
                return false;

            position = (Vector3)item1Field.GetValue(result);
            rotation = (Quaternion)item2Field.GetValue(result);
            return true;
        }

        private static int GetListCount(object value)
        {
            return value is IList list ? list.Count : 0;
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
