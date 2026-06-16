using System;
using System.Collections.Generic;
using System.Reflection;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Game
{
    /// <summary>
    /// Helper class to interface with the game's loadout system.
    /// Uses reflection to access GameDataLoadouts and related classes.
    /// </summary>
    public static class LoadoutHelper
    {
        private const string Tag = "LOADOUT-HELPER";
        private static bool _initialized = false;
        
        // Cached types
        private static Type _gameDataLoadoutsType;
        private static Type _gameDataAircraftType;
        private static Type _loadoutType;
        
        // Cached methods
        private static MethodInfo _getLoadoutNamesMethod;
        private static MethodInfo _getLoadoutMethod;
        private static MethodInfo _isLoadoutOnAircraftMethod;
        private static MethodInfo _getAircraftNamesMethod;
        private static MethodInfo _getAircraftDataMethod;
        
        // Aircraft data fields (DisplayName and Name are public fields, not properties)
        private static FieldInfo _aircraftDisplayNameField;
        private static FieldInfo _aircraftNameField;
        
        public static bool IsInitialized => _initialized;
        
        /// <summary>
        /// Initialize reflection caches. Call this once when the mod loads.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            
            try
            {
                // Find GameDataLoadouts type
                _gameDataLoadoutsType = Type.GetType("Falcon.GameDataLoadouts, Assembly-CSharp");
                if (_gameDataLoadoutsType == null)
                {
                    Log.Warning(Tag, "[LoadoutHelper] GameDataLoadouts type not found");
                    return;
                }
                
                // Find GameDataAircraft type
                _gameDataAircraftType = Type.GetType("Falcon.GameDataAircraft, Assembly-CSharp");
                if (_gameDataAircraftType == null)
                {
                    Log.Warning(Tag, "[LoadoutHelper] GameDataAircraft type not found");
                    return;
                }
                
                // Find Loadout type
                _loadoutType = Type.GetType("Falcon.Loadout, Assembly-CSharp");
                
                // Cache GameDataLoadouts methods
                _getLoadoutNamesMethod = _gameDataLoadoutsType.GetMethod("GetLoadoutNamesByAircraft", 
                    BindingFlags.Public | BindingFlags.Static);
                _getLoadoutMethod = _gameDataLoadoutsType.GetMethod("GetByAircraftAndName", 
                    BindingFlags.Public | BindingFlags.Static);
                _isLoadoutOnAircraftMethod = _gameDataLoadoutsType.GetMethod("IsLoadoutOnAircraft", 
                    BindingFlags.Public | BindingFlags.Static);
                
                // Cache GameDataAircraft methods
                // NOTE: The method is "GetListOfAllAircraft", not "GetAircraftNames"
                _getAircraftNamesMethod = _gameDataAircraftType.GetMethod("GetListOfAllAircraft", 
                    BindingFlags.Public | BindingFlags.Static);
                _getAircraftDataMethod = _gameDataAircraftType.GetMethod("GetByName", 
                    BindingFlags.Public | BindingFlags.Static);
                
                if (_getAircraftDataMethod != null)
                {
                    // Get the return type (UniAircraftData) and cache its DisplayName and Name fields
                    // NOTE: DisplayName and Name are public FIELDS, not properties
                    var returnType = _getAircraftDataMethod.ReturnType;
                    _aircraftDisplayNameField = returnType.GetField("DisplayName", 
                        BindingFlags.Public | BindingFlags.Instance);
                    _aircraftNameField = returnType.GetField("Name", 
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                }
                
                _initialized = true;
                Log.Info(Tag, "[LoadoutHelper] Initialized successfully");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"[LoadoutHelper] Initialization failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get all available aircraft names in the game.
        /// </summary>
        public static List<string> GetAircraftNames()
        {
            if (!_initialized) Initialize();
            if (_getAircraftNamesMethod == null) return new List<string>();
            
            try
            {
                var result = _getAircraftNamesMethod.Invoke(null, null);
                return result as List<string> ?? new List<string>();
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"[LoadoutHelper] GetAircraftNames failed: {ex.Message}");
                return new List<string>();
            }
        }

        public static bool IsAircraftAvailable(string aircraftName)
        {
            var aircraft = GetAircraftNames();
            if (aircraft.Count == 0)
                return !string.IsNullOrWhiteSpace(aircraftName);
            return !string.IsNullOrWhiteSpace(FindInList(aircraft, aircraftName));
        }

        public static string ResolveAvailableAircraft(string preferred, string fallback = "AV8B")
        {
            var aircraft = GetAircraftNames();
            if (aircraft.Count == 0)
                return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

            string resolved = FindInList(aircraft, preferred);
            if (!string.IsNullOrEmpty(resolved))
                return resolved;

            resolved = FindInList(aircraft, fallback);
            if (!string.IsNullOrEmpty(resolved))
                return resolved;

            return aircraft[0];
        }

        public static string ResolveLoadoutForAircraft(string aircraftName, string preferredLoadout)
        {
            var loadouts = GetLoadoutNamesForAircraft(aircraftName);
            if (loadouts.Count == 0)
                return string.IsNullOrWhiteSpace(preferredLoadout) ? "Clean" : preferredLoadout;

            string resolved = FindInList(loadouts, preferredLoadout);
            if (!string.IsNullOrEmpty(resolved))
                return resolved;

            resolved = FindInList(loadouts, "Clean");
            if (!string.IsNullOrEmpty(resolved))
                return resolved;

            return loadouts[0];
        }

        private static string FindInList(List<string> values, string value)
        {
            if (values == null || string.IsNullOrWhiteSpace(value))
                return null;

            for (int i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase))
                    return values[i];
            }

            return null;
        }
        
        /// <summary>
        /// Get the display name for an aircraft (e.g., "F-16C Viper" instead of "F16C").
        /// </summary>
        public static string GetAircraftDisplayName(string aircraftName)
        {
            if (!_initialized) Initialize();
            if (_getAircraftDataMethod == null || _aircraftDisplayNameField == null) 
                return aircraftName;
            
            try
            {
                var aircraftData = _getAircraftDataMethod.Invoke(null, new object[] { aircraftName });
                if (aircraftData != null)
                {
                    // DisplayName is a field, not a property
                    var displayName = _aircraftDisplayNameField.GetValue(aircraftData);
                    return displayName as string ?? aircraftName;
                }
            }
            catch { }
            
            return aircraftName;
        }
        
        /// <summary>
        /// Get all loadout names for a specific aircraft.
        /// </summary>
        public static List<string> GetLoadoutNamesForAircraft(string aircraftName)
        {
            if (!_initialized) Initialize();
            if (_getLoadoutNamesMethod == null) return new List<string>();
            
            try
            {
                var result = _getLoadoutNamesMethod.Invoke(null, new object[] { aircraftName });
                return result as List<string> ?? new List<string>();
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"[LoadoutHelper] GetLoadoutNamesForAircraft failed: {ex.Message}");
                return new List<string>();
            }
        }
        
        /// <summary>
        /// Check if a loadout exists for an aircraft.
        /// </summary>
        public static bool IsLoadoutOnAircraft(string aircraftName, string loadoutName)
        {
            if (!_initialized) Initialize();
            if (_isLoadoutOnAircraftMethod == null) return false;
            
            try
            {
                var result = _isLoadoutOnAircraftMethod.Invoke(null, new object[] { aircraftName, loadoutName });
                return result is bool b && b;
            }
            catch { return false; }
        }
        
        /// <summary>
        /// Get a default loadout name for an aircraft (first available, or "Clean" as fallback).
        /// </summary>
        public static string GetDefaultLoadoutForAircraft(string aircraftName)
        {
            var loadouts = GetLoadoutNamesForAircraft(aircraftName);
            if (loadouts.Count > 0)
            {
                // Prefer "Clean" if available, otherwise use first
                if (loadouts.Contains("Clean")) return "Clean";
                return loadouts[0];
            }
            return "Clean";
        }
    }
}
