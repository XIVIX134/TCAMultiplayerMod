using HarmonyLib;
using UnityEngine;
using Falcon.Game2;
using TCAMultiplayer.Player;
using TCAMultiplayer.Networking;
using TCAMultiplayer;
using System;
using System.Reflection;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Patches for FlightGame to hook into game lifecycle
    /// </summary>
    [HarmonyPatch]
    public static class FlightGamePatches
    {
        private static float _lastStateSendTime = 0f;
        private const float STATE_SEND_INTERVAL = 0.0078f; // 128Hz for smoother interpolation
        private static GameObject _cachedLocalAircraft = null;
        private static Component _cachedUniAircraft = null;
        private static bool _inFlight = false;
        
        // Cached reflection for reading aircraft state
        private static bool _reflectionInitialized = false;
        
        // Sub-component references
        private static PropertyInfo _uniPilotProperty = null;
        private static PropertyInfo _enginesProperty = null;
        private static PropertyInfo _flapsProperty = null;
        
        // UniPilot properties (fallback if FlightInput not found)
        private static PropertyInfo _pilotThrottleProperty = null;
        private static PropertyInfo _pilotPitchProperty = null;
        private static PropertyInfo _pilotRollProperty = null;
        private static PropertyInfo _pilotYawProperty = null;
        
        // UniFlaps properties  
        private static PropertyInfo _flapsDeployedProperty = null;
        
        // LandingGear from aircraft
        private static PropertyInfo _landingGearProperty = null;
        private static FieldInfo _landingGearField = null;
        private static PropertyInfo _gearIsDownProperty = null;
        
        // Engine state
        private static FieldInfo _engineAfterburnerField = null;
        private static FieldInfo _engineThrottleField = null;
        
        // FlightInput for control inputs
        private static FieldInfo _flightInputField = null;
        private static PropertyInfo _flightInputPitchProperty = null;
        private static PropertyInfo _flightInputRollProperty = null;
        private static PropertyInfo _flightInputYawProperty = null;
        private static FieldInfo _flightInputPitchField = null;
        private static FieldInfo _flightInputRollField = null;
        private static FieldInfo _flightInputYawField = null;
        private static FieldInfo _flightInputThrottleField = null;
        private static PropertyInfo _flightInputIsAfterburningProperty = null;
        private static PropertyInfo _flightInputNozzleAnalogProperty = null;
        
        // Cached objects for reading state
        private static object _cachedFirstEngine = null;
        private static object _cachedFlightInput = null;
        private static object _cachedLandingGear = null;
        
        /// <summary>
        /// Detect when we enter flight mode
        /// </summary>
        [HarmonyPatch(typeof(FlightGame), "Start")]
        [HarmonyPostfix]
        public static void FlightGame_Start_Postfix(FlightGame __instance)
        {
            try
            {
                _inFlight = true;
                _cachedLocalAircraft = null;
                _cachedUniAircraft = null;
                _reflectionInitialized = false; // Re-init on new flight
                Plugin.Log?.LogInfo("[FlightGamePatches] FlightGame started - in flight mode");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[FlightGamePatches] Start error: {ex.Message}");
            }
        }

        /// <summary>
        /// Hook into FlightGame.Update to send local player state
        /// </summary>
        [HarmonyPatch(typeof(FlightGame), "Update")]
        [HarmonyPostfix]
        public static void FlightGame_Update_Postfix(FlightGame __instance)
        {
            try
            {
                if (Plugin.Instance == null || Plugin.Instance.Network == null) return;
                if (!Plugin.Instance.Network.IsConnected) return;
                if (!_inFlight) return;
                
                // Send state at fixed interval
                if (Time.time - _lastStateSendTime >= STATE_SEND_INTERVAL)
                {
                    _lastStateSendTime = Time.time;
                    SendLocalPlayerState();
                }
                
                // Check for respawn (aircraft changed)
                CheckForRespawn();
                
                // Check radar lock state for RWR sync
                if (_cachedUniAircraft != null)
                {
                    WeaponPatches.CheckRadarLockState(_cachedUniAircraft);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[FlightGamePatches] Update error: {ex.Message}");
            }
        }
        
        // Track aircraft for respawn detection
        private static string _lastAircraftName = null;
        private static bool _wasDestroyed = false;
        
        private static void CheckForRespawn()
        {
            try
            {
                // If we had a cached aircraft but it's now null or destroyed, check for new one
                if (_cachedLocalAircraft == null || !_cachedLocalAircraft.activeInHierarchy)
                {
                    if (_lastAircraftName != null)
                    {
                        // We had an aircraft before, it's gone now
                        if (!_wasDestroyed)
                        {
                            _wasDestroyed = true;
                            Plugin.Instance?.Network?.SendAircraftDestroyedNotification();
                            Plugin.Log?.LogInfo("[FlightGamePatches] Local aircraft destroyed, sent notification");
                        }
                    }
                    
                    // Try to find new aircraft
                    _cachedLocalAircraft = null;
                    _cachedUniAircraft = null;
                    var newAircraft = FindLocalPlayerAircraft();
                    
                    if (newAircraft != null && _wasDestroyed)
                    {
                        // Respawned!
                        _wasDestroyed = false;
                        _lastAircraftName = newAircraft.name;
                        Plugin.Instance?.Network?.SendAircraftRespawnNotification(newAircraft.name);
                        Plugin.Log?.LogInfo($"[FlightGamePatches] Respawned with: {newAircraft.name}");
                        
                        // Reinitialize reflection for new aircraft
                        _reflectionInitialized = false;
                    }
                }
                else if (_lastAircraftName == null)
                {
                    // First time seeing this aircraft
                    _lastAircraftName = _cachedLocalAircraft.name;
                }
                else if (_cachedLocalAircraft.name != _lastAircraftName)
                {
                    // Aircraft changed (switched planes?)
                    _lastAircraftName = _cachedLocalAircraft.name;
                    Plugin.Instance?.Network?.SendAircraftRespawnNotification(_cachedLocalAircraft.name);
                    Plugin.Log?.LogInfo($"[FlightGamePatches] Aircraft changed to: {_cachedLocalAircraft.name}");
                    
                    // Reinitialize reflection for new aircraft
                    _reflectionInitialized = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[FlightGamePatches] CheckForRespawn error: {ex.Message}");
            }
        }

        private static void InitializeReflection(Component uniAircraft)
        {
            if (_reflectionInitialized) return;
            
            try
            {
                var aircraftType = uniAircraft.GetType();
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                
                Plugin.Log?.LogInfo($"[FlightGamePatches] Initializing reflection for {aircraftType.Name}...");
                
                // ============================================
                // DUMP ALL UniAircraft FIELDS
                // ============================================
                Plugin.Log?.LogInfo("[FlightGamePatches] UniAircraft FIELDS:");
                foreach (var field in aircraftType.GetFields(flags))
                {
                    Plugin.Log?.LogInfo($"  Field: {field.Name} ({field.FieldType.Name})");
                }
                
                // Get sub-component properties from UniAircraft
                _uniPilotProperty = aircraftType.GetProperty("UniPilot", flags);
                _enginesProperty = aircraftType.GetProperty("Engines", flags);
                _flapsProperty = aircraftType.GetProperty("Flaps", flags);
                
                // Look for landing gear on aircraft itself
                _landingGearProperty = aircraftType.GetProperty("LandingGear", flags)
                    ?? aircraftType.GetProperty("Gear", flags)
                    ?? aircraftType.GetProperty("GearSystem", flags);
                _landingGearField = aircraftType.GetField("landingGear", flags)
                    ?? aircraftType.GetField("_landingGear", flags)
                    ?? aircraftType.GetField("gear", flags)
                    ?? aircraftType.GetField("_gear", flags)
                    ?? aircraftType.GetField("LandingGear", flags);
                
                // ============================================
                // Explore UniPilot FIELDS and PROPERTIES
                // ============================================
                if (_uniPilotProperty != null)
                {
                    var uniPilot = _uniPilotProperty.GetValue(uniAircraft);
                    if (uniPilot != null)
                    {
                        var pilotType = uniPilot.GetType();
                        Plugin.Log?.LogInfo($"[FlightGamePatches] Found UniPilot: {pilotType.Name}");
                        
                        // Dump UniPilot FIELDS
                        Plugin.Log?.LogInfo("[FlightGamePatches] UniPilot FIELDS:");
                        foreach (var field in pilotType.GetFields(flags))
                        {
                            Plugin.Log?.LogInfo($"  Field: {field.Name} ({field.FieldType.Name})");
                        }
                        
                        // Get FlightInput field - this contains pitch/roll/yaw
                        _flightInputField = pilotType.GetField("FlightInput", flags);
                        if (_flightInputField != null)
                        {
                            _cachedFlightInput = _flightInputField.GetValue(uniPilot);
                            if (_cachedFlightInput != null)
                            {
                                var inputType = _cachedFlightInput.GetType();
                                Plugin.Log?.LogInfo($"[FlightGamePatches] Found FlightInput: {inputType.Name}");
                                
                                // Dump FlightInput fields and properties
                                Plugin.Log?.LogInfo("[FlightGamePatches] FlightInput FIELDS:");
                                foreach (var field in inputType.GetFields(flags))
                                {
                                    Plugin.Log?.LogInfo($"  Field: {field.Name} ({field.FieldType.Name})");
                                }
                                Plugin.Log?.LogInfo("[FlightGamePatches] FlightInput PROPERTIES:");
                                foreach (var prop in inputType.GetProperties(flags))
                                {
                                    Plugin.Log?.LogInfo($"  Prop: {prop.Name} ({prop.PropertyType.Name})");
                                }
                                
                                // Look for pitch/roll/yaw on FlightInput
                                _flightInputPitchProperty = inputType.GetProperty("Pitch", flags);
                                _flightInputRollProperty = inputType.GetProperty("Roll", flags);
                                _flightInputYawProperty = inputType.GetProperty("Yaw", flags)
                                    ?? inputType.GetProperty("Rudder", flags);
                                
                                // Look for Throttle, IsAfterburning, and NozzleAnalog on FlightInput
                                _flightInputThrottleField = inputType.GetField("Throttle", flags);
                                _flightInputIsAfterburningProperty = inputType.GetProperty("IsAfterburning", flags);
                                _flightInputNozzleAnalogProperty = inputType.GetProperty("NozzleAnalog", flags);
                                
                                _flightInputPitchField = inputType.GetField("pitch", flags)
                                    ?? inputType.GetField("Pitch", flags)
                                    ?? inputType.GetField("_pitch", flags);
                                _flightInputRollField = inputType.GetField("roll", flags)
                                    ?? inputType.GetField("Roll", flags)
                                    ?? inputType.GetField("_roll", flags);
                                _flightInputYawField = inputType.GetField("yaw", flags)
                                    ?? inputType.GetField("Yaw", flags)
                                    ?? inputType.GetField("rudder", flags)
                                    ?? inputType.GetField("Rudder", flags);
                            }
                        }
                        
                        // Also try direct properties on pilot (fallback)
                        _pilotThrottleProperty = pilotType.GetProperty("Throttle", flags);
                        _pilotPitchProperty = pilotType.GetProperty("Pitch", flags);
                        _pilotRollProperty = pilotType.GetProperty("Roll", flags);
                        _pilotYawProperty = pilotType.GetProperty("Yaw", flags);
                    }
                }
                
                // ============================================
                // Get Flaps - use AreFlapsDown (we know this exists)
                // ============================================
                if (_flapsProperty != null)
                {
                    var flaps = _flapsProperty.GetValue(uniAircraft);
                    if (flaps != null)
                    {
                        var flapsType = flaps.GetType();
                        Plugin.Log?.LogInfo($"[FlightGamePatches] Found Flaps: {flapsType.Name}");
                        
                        // Use AreFlapsDown which we know exists from log
                        _flapsDeployedProperty = flapsType.GetProperty("AreFlapsDown", flags)
                            ?? flapsType.GetProperty("IsDeployed", flags)
                            ?? flapsType.GetProperty("Deployed", flags);
                        
                        Plugin.Log?.LogInfo($"[FlightGamePatches] Flaps property: {_flapsDeployedProperty?.Name ?? "NOT FOUND"}");
                    }
                }
                
                // ============================================
                // Get Engine - use exact field names from log
                // ============================================
                if (_enginesProperty != null)
                {
                    var engines = _enginesProperty.GetValue(uniAircraft);
                    if (engines != null)
                    {
                        var enginesList = engines as System.Collections.IList;
                        if (enginesList != null && enginesList.Count > 0)
                        {
                            _cachedFirstEngine = enginesList[0];
                            var engineType = _cachedFirstEngine.GetType();
                            Plugin.Log?.LogInfo($"[FlightGamePatches] Found Engine: {engineType.Name}");
                            
                            // Use exact field names from log dump:
                            // Field: Throttle (Single)
                            // Field: Afterburner (Single)
                            _engineThrottleField = engineType.GetField("Throttle", flags);
                            _engineAfterburnerField = engineType.GetField("Afterburner", flags);
                            
                            Plugin.Log?.LogInfo($"[FlightGamePatches] Engine Throttle field: {_engineThrottleField?.Name ?? "NOT FOUND"}");
                            Plugin.Log?.LogInfo($"[FlightGamePatches] Engine Afterburner field: {_engineAfterburnerField?.Name ?? "NOT FOUND"}");
                        }
                    }
                }
                
                // ============================================
                // Try to find gear on aircraft directly
                // Use exact field/property names from log
                // ============================================
                if (_landingGearField != null)
                {
                    _cachedLandingGear = _landingGearField.GetValue(uniAircraft);
                    Plugin.Log?.LogInfo($"[FlightGamePatches] Found LandingGear field on aircraft");
                }
                else if (_landingGearProperty != null)
                {
                    _cachedLandingGear = _landingGearProperty.GetValue(uniAircraft);
                    Plugin.Log?.LogInfo($"[FlightGamePatches] Found LandingGear property on aircraft");
                }
                
                if (_cachedLandingGear != null)
                {
                    var gearType = _cachedLandingGear.GetType();
                    Plugin.Log?.LogInfo($"[FlightGamePatches] LandingGear type: {gearType.Name}");
                    
                    // Use exact property name from log: IsGearLowered
                    _gearIsDownProperty = gearType.GetProperty("IsGearLowered", flags);
                    
                    Plugin.Log?.LogInfo($"[FlightGamePatches] Gear IsGearLowered property: {_gearIsDownProperty?.Name ?? "NOT FOUND"}");
                }
                
                // ============================================
                // Summary
                // ============================================
                Plugin.Log?.LogInfo("[FlightGamePatches] ========== REFLECTION SUMMARY ==========");
                Plugin.Log?.LogInfo($"  Engine Throttle: {_engineThrottleField?.Name ?? "NOT FOUND"}");
                Plugin.Log?.LogInfo($"  Engine Afterburner: {_engineAfterburnerField?.Name ?? "NOT FOUND"}");
                Plugin.Log?.LogInfo($"  Gear IsGearLowered: {_gearIsDownProperty?.Name ?? "NOT FOUND"}");
                Plugin.Log?.LogInfo($"  Flaps AreFlapsDown: {_flapsDeployedProperty?.Name ?? "NOT FOUND"}");
                Plugin.Log?.LogInfo($"  FlightInput: {(_cachedFlightInput != null ? "FOUND" : "NOT FOUND")}");
                Plugin.Log?.LogInfo($"  FlightInput Pitch: prop={_flightInputPitchProperty?.Name ?? "null"} field={_flightInputPitchField?.Name ?? "null"}");
                Plugin.Log?.LogInfo($"  FlightInput Roll: prop={_flightInputRollProperty?.Name ?? "null"} field={_flightInputRollField?.Name ?? "null"}");
                Plugin.Log?.LogInfo($"  FlightInput Yaw: prop={_flightInputYawProperty?.Name ?? "null"} field={_flightInputYawField?.Name ?? "null"}");
                Plugin.Log?.LogInfo("[FlightGamePatches] ========================================");
                
                _reflectionInitialized = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[FlightGamePatches] Reflection init error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void SendLocalPlayerState()
        {
            try
            {
                // Find local player aircraft
                var localAircraft = FindLocalPlayerAircraft();
                if (localAircraft == null)
                {
                    if (LogHelper.IsEnabled(LogCategory.Patches) &&
                        LogHelper.ShouldLogInterval("FlightGamePatches.NoLocalAircraft", LogHelper.DefaultIntervalSeconds))
                    {
                        LogHelper.Info(LogCategory.Patches, "[FlightGamePatches] No local aircraft found for state send");
                    }
                    return;
                }
                
                // Initialize reflection if needed
                if (!_reflectionInitialized && _cachedUniAircraft != null)
                {
                    InitializeReflection(_cachedUniAircraft);
                }
                
                // Get rigidbody for velocity
                var rb = localAircraft.GetComponent<Rigidbody>();
                
                var localPos = localAircraft.transform.position;
                var rot = localAircraft.transform.rotation;
                var vel = rb != null ? rb.velocity : Vector3.zero;
                var angVel = rb != null ? rb.angularVelocity : Vector3.zero;
                
                // Convert local position to world-absolute using FloatingOrigin offset
                var absolutePos = FloatingOriginHelper.LocalToAbsolute(localPos);
                
                // Read aircraft state via reflection
                float throttle = 0f;
                bool afterburner = false;
                bool gearDown = true;
                bool flapsDown = false;
                bool isFiring = false;
                float pitch = 0f;
                float roll = 0f;
                float yaw = 0f;
                float nozzleAngle = 0f;
                
                if (_cachedUniAircraft != null)
                {
                    try
                    {
                        // Read from FlightInput (primary source for player inputs)
                        if (_cachedFlightInput != null)
                        {
                            // Throttle from FlightInput
                            if (_flightInputThrottleField != null)
                                throttle = Convert.ToSingle(_flightInputThrottleField.GetValue(_cachedFlightInput));
                            
                            // Afterburner from FlightInput.IsAfterburning (boolean property)
                            if (_flightInputIsAfterburningProperty != null)
                                afterburner = Convert.ToBoolean(_flightInputIsAfterburningProperty.GetValue(_cachedFlightInput));
                            
                            // Pitch/Roll/Yaw
                            if (_flightInputPitchProperty != null)
                                pitch = Convert.ToSingle(_flightInputPitchProperty.GetValue(_cachedFlightInput));
                            else if (_flightInputPitchField != null)
                                pitch = Convert.ToSingle(_flightInputPitchField.GetValue(_cachedFlightInput));
                            
                            if (_flightInputRollProperty != null)
                                roll = Convert.ToSingle(_flightInputRollProperty.GetValue(_cachedFlightInput));
                            else if (_flightInputRollField != null)
                                roll = Convert.ToSingle(_flightInputRollField.GetValue(_cachedFlightInput));
                            
                            if (_flightInputYawProperty != null)
                                yaw = Convert.ToSingle(_flightInputYawProperty.GetValue(_cachedFlightInput));
                            else if (_flightInputYawField != null)
                                yaw = Convert.ToSingle(_flightInputYawField.GetValue(_cachedFlightInput));
                            
                            // Nozzle angle (VTOL) - NozzleAnalog is 0-1, convert to angle
                            if (_flightInputNozzleAnalogProperty != null)
                            {
                                float nozzleAnalog = Convert.ToSingle(_flightInputNozzleAnalogProperty.GetValue(_cachedFlightInput));
                                // Get actual nozzle angle from engine if available
                                nozzleAngle = nozzleAnalog * 90f; // Assume 90 deg max for now
                            }
                        }
                        
                        // Try to get actual nozzle angle from first engine
                        if (_cachedFirstEngine != null)
                        {
                            var engineType = _cachedFirstEngine.GetType();
                            var nozzleAngleProp = engineType.GetProperty("VTOLNozzleAngle", BindingFlags.Public | BindingFlags.Instance);
                            if (nozzleAngleProp != null)
                            {
                                nozzleAngle = Convert.ToSingle(nozzleAngleProp.GetValue(_cachedFirstEngine));
                            }
                        }
                        
                        // Fallback to engine for throttle/afterburner if not found on FlightInput
                        if (throttle == 0f && _cachedFirstEngine != null && _engineThrottleField != null)
                        {
                            throttle = Convert.ToSingle(_engineThrottleField.GetValue(_cachedFirstEngine));
                        }
                        
                        if (!afterburner && _cachedFirstEngine != null && _engineAfterburnerField != null)
                        {
                            float abValue = Convert.ToSingle(_engineAfterburnerField.GetValue(_cachedFirstEngine));
                            afterburner = abValue > 0.1f;
                        }
                        
                        // Get flaps state
                        object flaps = _flapsProperty?.GetValue(_cachedUniAircraft);
                        if (flaps != null && _flapsDeployedProperty != null)
                        {
                            flapsDown = Convert.ToBoolean(_flapsDeployedProperty.GetValue(flaps));
                        }
                        
                        // Get landing gear state from cached object
                        if (_cachedLandingGear != null && _gearIsDownProperty != null)
                        {
                            gearDown = Convert.ToBoolean(_gearIsDownProperty.GetValue(_cachedLandingGear));
                        }
                        
                        // Try to detect gun firing - look for Gun2 or weapon firing state
                        isFiring = DetectGunFiring(_cachedUniAircraft);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning($"[FlightGamePatches] State read error: {ex.Message}");
                    }
                }
                
                // Build state packet with ABSOLUTE coordinates
                var state = new AircraftStatePacket
                {
                    PlayerId = GetLocalPlayerId(),
                    
                    // Use absolute world position (doubles for precision)
                    PosX = absolutePos.x,
                    PosY = absolutePos.y,
                    PosZ = absolutePos.z,
                    
                    RotX = rot.x,
                    RotY = rot.y,
                    RotZ = rot.z,
                    RotW = rot.w,
                    
                    VelX = vel.x,
                    VelY = vel.y,
                    VelZ = vel.z,
                    
                    AngVelX = angVel.x,
                    AngVelY = angVel.y,
                    AngVelZ = angVel.z,
                    
                    Throttle = throttle,
                    Pitch = pitch,
                    Roll = roll,
                    Yaw = yaw,
                    NozzleAngle = nozzleAngle,
                    
                    Timestamp = Time.time
                };
                
                // Set flags
                state.Afterburner = afterburner;
                state.GearDown = gearDown;
                state.FlapsDown = flapsDown;
                state.IsFiring = isFiring;
                
                if (LogHelper.IsEnabled(LogCategory.Patches) &&
                    LogHelper.ShouldSample("FlightGamePatches.SendLocalPlayerState", LogHelper.HighFreqSampleRate))
                {
                    LogHelper.Info(LogCategory.Patches,
                        $"[FlightGamePatches] State pos={absolutePos} vel={vel} throttle={throttle:F2} " +
                        $"ab={afterburner} gear={gearDown} flaps={flapsDown} fire={isFiring}");
                }
                
                Plugin.Instance.Network.SendAircraftState(state);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[FlightGamePatches] SendLocalPlayerState error: {ex.Message}");
            }
        }

        private static GameObject FindLocalPlayerAircraft()
        {
            // Use cached reference if still valid and active
            if (_cachedLocalAircraft != null && _cachedLocalAircraft.activeInHierarchy)
            {
                return _cachedLocalAircraft;
            }
            
            try
            {
                // Fast path: use UniAircraft.Player if available
                var playerAircraft = Falcon.UniversalAircraft.UniAircraft.Player;
                if (playerAircraft != null)
                {
                    var playerGo = playerAircraft.gameObject;
                    if (playerGo != null && playerGo.activeInHierarchy)
                    {
                        // Ignore our own remote clones
                        if (playerGo.GetComponent<TCAMultiplayer.Player.RemoteAircraftController>() == null &&
                            !playerGo.name.Contains("MP_Remote"))
                        {
                            _cachedLocalAircraft = playerGo;
                            _cachedUniAircraft = playerAircraft;
                            Plugin.Log?.LogInfo($"[FlightGamePatches] Using UniAircraft.Player as local aircraft: {_cachedLocalAircraft.name}");
                            return _cachedLocalAircraft;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[FlightGamePatches] UniAircraft.Player lookup failed: {ex.Message}");
            }
            
            try
            {
                // Try to find UniAircraft components
                var aircrafts = UnityEngine.Object.FindObjectsByType<Falcon.UniversalAircraft.UniAircraft>(FindObjectsSortMode.None);
                
                if (aircrafts != null && aircrafts.Length > 0)
                {
                    foreach (var aircraft in aircrafts)
                    {
                        var component = aircraft as Component;
                        if (component != null)
                        {
                            var go = component.gameObject;
                            
                            // CRITICAL FIX: Ignore our own remote clones!
                            // If it has a RemoteAircraftController, it is NOT the local player
                            if (go.GetComponent<TCAMultiplayer.Player.RemoteAircraftController>() != null)
                            {
                                continue;
                            }
                            
                            // Also check name as backup
                            if (go.name.Contains("MP_Remote"))
                            {
                                continue;
                            }

                            // This is likely the real player
                            _cachedLocalAircraft = go;
                            _cachedUniAircraft = component;
                            Plugin.Log?.LogInfo($"[FlightGamePatches] Found local aircraft: {_cachedLocalAircraft.name}");
                            return _cachedLocalAircraft;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[FlightGamePatches] Error finding aircraft: {ex.Message}");
            }
            
            if (LogHelper.IsEnabled(LogCategory.Patches) &&
                LogHelper.ShouldLogInterval("FlightGamePatches.FindLocalAircraft.None", LogHelper.DefaultIntervalSeconds))
            {
                LogHelper.Info(LogCategory.Patches, "[FlightGamePatches] No local aircraft found (search returned empty)");
            }
            return null;
        }

        private static ulong GetLocalPlayerId()
        {
            return Plugin.Instance.Network.IsHost ? 1UL : 2UL;
        }
        
        // Cached gun detection
        private static object _cachedWeaponInput = null;
        private static FieldInfo _weaponInputIsFiringField = null;
        private static bool _gunReflectionInitialized = false;
        
        /// <summary>
        /// Detect if the aircraft is currently firing its gun
        /// Uses WeaponInput.IsFiring from the WeaponControls field on UniAircraft
        /// </summary>
        private static bool DetectGunFiring(Component aircraft)
        {
            try
            {
                if (!_gunReflectionInitialized)
                {
                    InitializeGunReflection(aircraft);
                }
                
                // Primary method: Check WeaponInput.IsFiring
                if (_cachedWeaponInput != null && _weaponInputIsFiringField != null)
                {
                    return Convert.ToBoolean(_weaponInputIsFiringField.GetValue(_cachedWeaponInput));
                }
                
                // Fallback: Check if mouse button is pressed
                // This is less reliable but works as a backup
                if (Input.GetMouseButton(0))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Silent fail - gun detection isn't critical
                if (!_gunReflectionInitialized)
                {
                    Plugin.Log?.LogWarning($"[FlightGamePatches] Gun detection error: {ex.Message}");
                }
            }
            
            return false;
        }
        
        private static void InitializeGunReflection(Component aircraft)
        {
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var aircraftType = aircraft.GetType();
                
                // Find WeaponControls field on UniAircraft (which is a WeaponInput)
                // From log: "Field: WeaponControls (WeaponInput)"
                var weaponControlsField = aircraftType.GetField("WeaponControls", flags);
                
                if (weaponControlsField != null)
                {
                    _cachedWeaponInput = weaponControlsField.GetValue(aircraft);
                    
                    if (_cachedWeaponInput != null)
                    {
                        var weaponInputType = _cachedWeaponInput.GetType();
                        Plugin.Log?.LogInfo($"[FlightGamePatches] Found WeaponInput: {weaponInputType.Name}");
                        
                        // Get IsFiring field - it's a public bool field
                        _weaponInputIsFiringField = weaponInputType.GetField("IsFiring", flags);
                        
                        if (_weaponInputIsFiringField != null)
                        {
                            Plugin.Log?.LogInfo("[FlightGamePatches] Found WeaponInput.IsFiring field - gun detection ready!");
                        }
                        else
                        {
                            Plugin.Log?.LogWarning("[FlightGamePatches] WeaponInput.IsFiring field not found");
                            
                            // Log available fields for debugging
                            Plugin.Log?.LogInfo("[FlightGamePatches] WeaponInput fields:");
                            foreach (var field in weaponInputType.GetFields(flags))
                            {
                                Plugin.Log?.LogInfo($"  Field: {field.Name} ({field.FieldType.Name})");
                            }
                        }
                    }
                }
                else
                {
                    Plugin.Log?.LogWarning("[FlightGamePatches] WeaponControls field not found on UniAircraft");
                }
                
                _gunReflectionInitialized = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[FlightGamePatches] Gun reflection init error: {ex.Message}");
                _gunReflectionInitialized = true; // Don't retry
            }
        }
        
        /// <summary>
        /// Clear cache when leaving flight
        /// </summary>
        public static void ClearCache()
        {
            _cachedLocalAircraft = null;
            _cachedUniAircraft = null;
            _cachedFirstEngine = null;
            _cachedFlightInput = null;
            _cachedLandingGear = null;
            _cachedWeaponInput = null;
            _weaponInputIsFiringField = null;
            _gunReflectionInitialized = false;
            _inFlight = false;
            _reflectionInitialized = false;
            _lastAircraftName = null;
            _wasDestroyed = false;
            WeaponPatches.ClearState();
        }
    }
}
