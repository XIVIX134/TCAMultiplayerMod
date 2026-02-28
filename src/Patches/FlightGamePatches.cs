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
        private static GameObject _cachedLocalAircraft = null;
        private static Component _cachedUniAircraft = null;
        private static bool _inFlight = false;

        // Sequence number for packet ordering
        private static uint _stateSequenceNumber = 0;

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
        private static FieldInfo _flightInputField = null;          // UniPilot.FlightInput
        private static FieldInfo _flightControlsField = null;       // UniAircraft.FlightControls (primary for player input)
        private static PropertyInfo _flightInputPitchProperty = null;
        private static PropertyInfo _flightInputRollProperty = null;
        private static PropertyInfo _flightInputYawProperty = null;
        private static FieldInfo _flightInputPitchField = null;
        private static FieldInfo _flightInputRollField = null;
        private static FieldInfo _flightInputYawField = null;
        private static FieldInfo _flightInputThrottleField = null;
        private static PropertyInfo _flightInputIsAfterburningProperty = null;
        private static PropertyInfo _flightInputNozzleAnalogProperty = null;
        // StickAndRudder sub-object on FlightInput (alternative raw input source)
        private static FieldInfo _stickAndRudderField = null;
        private static PropertyInfo _sarPitchProperty = null;
        private static PropertyInfo _sarRollProperty = null;
        private static PropertyInfo _sarYawProperty = null;
        private static FieldInfo _sarPitchField = null;
        private static FieldInfo _sarRollField = null;
        private static FieldInfo _sarYawField = null;

        // FlightStats from aircraft
        private static PropertyInfo _flightStatsProperty = null;
        private static FieldInfo _flightStatsField = null;
        private static PropertyInfo _speedKIASProperty = null;
        private static FieldInfo _speedKIASField = null;

        // BrakeState from aircraft
        private static PropertyInfo _brakeStateProperty = null;
        private static FieldInfo _brakeStateField = null;

        // Cached objects for reading state
        private static object _cachedFirstEngine = null;
        private static object _cachedFlightInput = null;
        private static object _cachedLandingGear = null;
        private static object _cachedFlightStats = null;

        /// <summary>
        /// Public accessor for the current FlightGame instance
        /// </summary>
        public static object FlightGameInstance { get; private set; }

        /// <summary>
        /// Detect when FlightGame wakes up to capture instance
        /// </summary>
        [HarmonyPatch(typeof(FlightGame), "Awake")]
        [HarmonyPostfix]
        public static void FlightGame_Awake_Postfix(FlightGame __instance)
        {
            FlightGameInstance = __instance;
            Plugin.Log?.LogInfo("[FlightGamePatches] Captured FlightGame instance");
        }

        /// <summary>
        /// Detect when we enter flight mode
        /// </summary>
        [HarmonyPatch(typeof(FlightGame), "Start")]
        [HarmonyPostfix]
        public static void FlightGame_Start_Postfix(FlightGame __instance)
        {
            try
            {
                // Capture instance here as well since Start is guaranteed to run
                FlightGameInstance = __instance;

                _inFlight = true;
                _cachedLocalAircraft = null;
                _cachedUniAircraft = null;
                _reflectionInitialized = false; // Re-init on new flight
                Plugin.Log?.LogInfo("[FlightGamePatches] FlightGame started - in flight mode (Instance captured)");
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
                // Use GameStateMachine.IsConnected instead of Network.IsConnected
                // This supports solo mode where the host has no clients connected
                if (Plugin.Instance.GameState == null || !Plugin.Instance.GameState.IsConnected) return;
                if (!_inFlight) return;

                // Send state at fixed interval
                if (Time.time - _lastStateSendTime >= NetworkConfig.CurrentStateSendInterval)
                {
                    _lastStateSendTime = Time.time;
                    SendLocalPlayerState();
                }

                // Check for respawn (aircraft changed)
                CheckForRespawn();

                // Poll for new missile launches (replaces broken Harmony Postfix)
                WeaponPatches.PollMissileLaunches();

                // Continuous missile position sync (~10Hz, unreliable)
                WeaponPatches.PollMissilePositionSync();
                WeaponPatches.PollMissileLaunches();

                // Poll for new bomb drops (unguided munitions)
                WeaponPatches.PollBombDrops();

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

        // Timer-based death detection fallback:
        // If we had an aircraft and then FindLocalPlayerAircraft returns null for this many seconds, trigger death
        private static float _aircraftMissingTime = 0f;
        private static bool _hadAircraftBefore = false;
        private const float AIRCRAFT_MISSING_DEATH_THRESHOLD = 2.0f;

        // Death cooldown: after death is detected, wait this many seconds before searching for new aircraft
        // This prevents the respawn loop where the destroyed aircraft's GO is still alive and gets re-found
        private static float _deathTime = 0f;
        private const float RESPAWN_SEARCH_COOLDOWN = 5.0f;
        
        // Set to true once respawn UI is shown; prevents repeated CheckForRespawn logging
        private static bool _respawnScreenShown = false;

        // Cached reflection for HasBeenDestroyed check on UniAircraft
        private static PropertyInfo _hasBeenDestroyedProp = null;
        private static bool _hasBeenDestroyedPropChecked = false;

        /// <summary>
        /// Check if the UniAircraft component reports HasBeenDestroyed = true.
        /// Uses reflection since UniAircraft is not directly accessible.
        /// </summary>
        private static bool IsAircraftMarkedDestroyed(Component uniAircraft)
        {
            if (uniAircraft == null) return false;

            try
            {
                if (!_hasBeenDestroyedPropChecked)
                {
                    _hasBeenDestroyedPropChecked = true;
                    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    var aircraftType = uniAircraft.GetType();
                    _hasBeenDestroyedProp = aircraftType.GetProperty("HasBeenDestroyed", flags);
                    if (_hasBeenDestroyedProp == null)
                    {
                        // Use robust backing field resolver instead of hardcoded name
                        var field = ReflectionHelper.GetBackingField(aircraftType, "HasBeenDestroyed", flags);
                        if (field != null)
                        {
                            Plugin.Log?.LogInfo($"[FlightGamePatches] Found HasBeenDestroyed backing field: {field.Name}");
                        }
                        else
                        {
                            Plugin.Log?.LogWarning("[FlightGamePatches] HasBeenDestroyed property/field not found on UniAircraft");
                        }
                    }
                    else
                    {
                        Plugin.Log?.LogInfo("[FlightGamePatches] Found HasBeenDestroyed property on UniAircraft");
                    }
                }

                if (_hasBeenDestroyedProp != null)
                {
                    return (bool)_hasBeenDestroyedProp.GetValue(uniAircraft);
                }

                // Fallback: use robust backing field resolver
                var backingField = ReflectionHelper.GetBackingField(uniAircraft.GetType(), "HasBeenDestroyed",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (backingField != null)
                {
                    return (bool)backingField.GetValue(uniAircraft);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[FlightGamePatches] HasBeenDestroyed check error: {ex.Message}");
            }

            return false;
        }

        private static void CheckForRespawn()
        {
            try
            {
                // Determine if current aircraft is effectively dead/gone:
                // 1. Cached reference is null (object destroyed by Unity)
                // 2. GameObject is inactive
                // 3. UniAircraft.HasBeenDestroyed == true (aircraft killed but GO may linger)
                bool cachedIsNull = _cachedLocalAircraft == null;
                bool cachedIsInactive = !cachedIsNull && !_cachedLocalAircraft.activeInHierarchy;
                bool aircraftGone = cachedIsNull || cachedIsInactive;
                
                // Additional check: HasBeenDestroyed via reflection (catches case where GO is still active but aircraft is dead)
                if (!aircraftGone && _cachedUniAircraft != null)
                {
                    bool markedDestroyed = IsAircraftMarkedDestroyed(_cachedUniAircraft);
                    if (markedDestroyed)
                    {
                        if (LogHelper.ShouldLogInterval("FlightGamePatches.HasBeenDestroyedDetected", 5f))
                            Plugin.Log?.LogInfo("[FlightGamePatches] Aircraft HasBeenDestroyed=true detected (GO still active)");
                        aircraftGone = true;
                    }
                }

                // Also check UniAircraft.Player as a backup source of truth
                if (!aircraftGone)
                {
                    try
                    {
                        var playerAircraft = Falcon.UniversalAircraft.UniAircraft.Player;
                        if (playerAircraft == null && _lastAircraftName != null)
                        {
                            Plugin.Log?.LogInfo("[FlightGamePatches] UniAircraft.Player is null but we had an aircraft — treating as gone");
                            aircraftGone = true;
                        }
                    }
                    catch { }
                }

                // Diagnostic logging (throttled) — suppress once respawn screen is shown
                if (aircraftGone && !_respawnScreenShown && LogHelper.ShouldLogInterval("FlightGamePatches.CheckForRespawn.AircraftGone", 5f))
                {
                    Plugin.Log?.LogInfo($"[FlightGamePatches] CheckForRespawn: aircraftGone=true cachedNull={cachedIsNull} inactive={cachedIsInactive} lastAircraft={_lastAircraftName ?? "null"} wasDestroyed={_wasDestroyed} hadBefore={_hadAircraftBefore}");
                }

                if (aircraftGone)
                {
                    if (_lastAircraftName != null)
                    {
                        // We had an aircraft before, it's gone now
                        if (!_wasDestroyed)
                        {
                            _wasDestroyed = true;
                            _deathTime = Time.time;
                            Plugin.Log?.LogInfo($"[FlightGamePatches] Death detected! lastAircraft={_lastAircraftName} cachedNull={_cachedLocalAircraft == null}");
                            
                            // Only send destroyed notification if DamagePatches hasn't already handled it
                            if (!DamagePatches.DestructionHandled)
                            {
                                Plugin.Instance?.Network?.SendAircraftDestroyedNotification();
                                
                                // Check if an enemy recently dealt damage — give them kill credit
                                // This handles: enemy damages us → we crash shortly after
                                float timeSinceLastDamage = Time.time - DamagePatches.LastDamageTime;
                                if (DamagePatches.LastAttackerId != 0 && timeSinceLastDamage < 15f)
                                {
                                    ulong localId = Plugin.Instance?.Network?.LocalPeerId ?? 0;
                                    Plugin.Log?.LogInfo($"[FlightGamePatches] Aircraft destroyed {timeSinceLastDamage:F1}s after last damage — crediting kill to {DamagePatches.LastAttackerId}");
                                    
                                    // Send kill confirmation to give attacker credit
                                    Plugin.Instance?.Network?.SendKillConfirmation(DamagePatches.LastAttackerId, localId, DamagePatches.LastAttackerWeapon);
                                    
                                    // Record kill/death locally
                                    Game.ScoreTracker.Instance?.RecordKill(DamagePatches.LastAttackerId, localId, DamagePatches.LastAttackerWeapon);
                                }
                                else
                                {
                                    Plugin.Log?.LogInfo("[FlightGamePatches] Aircraft destroyed (no recent attacker within 15s)");
                                }
                                
                                // Reset last attacker tracking
                                DamagePatches.LastAttackerId = 0;
                                DamagePatches.LastAttackerWeapon = "";
                                DamagePatches.LastDamageTime = 0f;
                                
                                // Show respawn UI
                                Game.SpawnManager.Instance?.NotifyPlayerDied();
                                _respawnScreenShown = true;
                            }
                            else
                            {
                                Plugin.Log?.LogInfo("[FlightGamePatches] Local aircraft destroyed (already handled by DamagePatches)");
                                // SAFETY: Still trigger respawn UI in case DamagePatches missed it
                                // (RespawnScreen.Show() is idempotent — won't double-create)
                                Game.SpawnManager.Instance?.NotifyPlayerDied();
                                _respawnScreenShown = true;
                            }
                        }
                    }
                    else if (_hadAircraftBefore)
                    {
                        // Timer-based fallback: we once had an aircraft, now FindLocalPlayerAircraft returns null
                        // but _lastAircraftName was never set (edge case). Track missing time.
                        _aircraftMissingTime += Time.deltaTime;
                        if (_aircraftMissingTime >= AIRCRAFT_MISSING_DEATH_THRESHOLD && !_wasDestroyed)
                        {
                            _wasDestroyed = true;
                            _deathTime = Time.time;
                            Plugin.Log?.LogInfo("[FlightGamePatches] Death detected via timer fallback (aircraft missing >2s)");
                            
                            if (!DamagePatches.DestructionHandled)
                            {
                                Plugin.Instance?.Network?.SendAircraftDestroyedNotification();
                                
                                float timeSinceLastDamage = Time.time - DamagePatches.LastDamageTime;
                                if (DamagePatches.LastAttackerId != 0 && timeSinceLastDamage < 15f)
                                {
                                    ulong localId = Plugin.Instance?.Network?.LocalPeerId ?? 0;
                                    Plugin.Log?.LogInfo($"[FlightGamePatches] Timer-fallback death {timeSinceLastDamage:F1}s after last damage — crediting kill to {DamagePatches.LastAttackerId}");
                                    Plugin.Instance?.Network?.SendKillConfirmation(DamagePatches.LastAttackerId, localId, DamagePatches.LastAttackerWeapon);
                                    Game.ScoreTracker.Instance?.RecordKill(DamagePatches.LastAttackerId, localId, DamagePatches.LastAttackerWeapon);
                                }
                                
                                DamagePatches.LastAttackerId = 0;
                                DamagePatches.LastAttackerWeapon = "";
                                DamagePatches.LastDamageTime = 0f;
                                
                                Game.SpawnManager.Instance?.NotifyPlayerDied();
                                _respawnScreenShown = true;
                            }
                        }
                    }

                    // Clear cached references so we don't keep reading the destroyed aircraft
                    _cachedLocalAircraft = null;
                    _cachedUniAircraft = null;

                    // COOLDOWN: Don't search for new aircraft immediately after death
                    // The destroyed aircraft's GameObject can linger for seconds and gets re-found,
                    // causing a false "respawn" that resets _wasDestroyed in an infinite loop
                    if (_wasDestroyed && Time.time - _deathTime < RESPAWN_SEARCH_COOLDOWN)
                    {
                        // Still in cooldown — don't search for aircraft yet
                        return;
                    }

                    // Try to find new aircraft (for respawn detection)
                    var newAircraft = FindLocalPlayerAircraft();

                    if (newAircraft != null && _wasDestroyed)
                    {
                        // VALIDATE: Make sure this isn't the same destroyed aircraft lingering
                        if (_cachedUniAircraft != null && IsAircraftMarkedDestroyed(_cachedUniAircraft))
                        {
                            Plugin.Log?.LogInfo("[FlightGamePatches] Found aircraft but it's still marked destroyed — ignoring");
                            _cachedLocalAircraft = null;
                            _cachedUniAircraft = null;
                            return;
                        }

                        // Respawned!
                        _wasDestroyed = false;
                        _deathTime = 0f;
                        _aircraftMissingTime = 0f;
                        _respawnScreenShown = false;
                        DamagePatches.DestructionHandled = false; // Reset for next death
                        DamagePatches.LastAttackerId = 0;
                        DamagePatches.LastAttackerWeapon = "";
                        DamagePatches.LastDamageTime = 0f;
                        _hasBeenDestroyedPropChecked = false; // Reset for new aircraft type
                        _lastAircraftName = newAircraft.name;
                        
                        // Use proper aircraft type name (e.g. "AV8B") not display name (e.g. "Kestrel 1 (MRGoldberg)")
                        string respawnTypeName = GetLocalAircraftTypeName(_cachedUniAircraft);
                        if (string.IsNullOrEmpty(respawnTypeName))
                        {
                            respawnTypeName = ReflectionHelper.MapAircraftNameFromString(newAircraft.name) ?? newAircraft.name;
                        }
                        Plugin.Instance?.Network?.SendAircraftRespawnNotification(respawnTypeName);
                        Plugin.Log?.LogInfo($"[FlightGamePatches] Respawned with: {newAircraft.name} (type: {respawnTypeName})");

                        // Reinitialize reflection for new aircraft
                        _reflectionInitialized = false;
                    }
                }
                else if (_wasDestroyed && _cachedLocalAircraft != null)
                {
                    // CRITICAL FIX: We have an aircraft but _wasDestroyed is still true
                    // This happens when SendLocalPlayerState found the new aircraft before CheckForRespawn
                    // We need to detect respawn here!
                    
                    // Validate: Make sure this isn't the same destroyed aircraft lingering
                    if (_cachedUniAircraft != null && IsAircraftMarkedDestroyed(_cachedUniAircraft))
                    {
                        Plugin.Log?.LogInfo("[FlightGamePatches] Found aircraft but it's still marked destroyed — ignoring");
                        _cachedLocalAircraft = null;
                        _cachedUniAircraft = null;
                        return;
                    }

                    // Respawned!
                    _wasDestroyed = false;
                    _deathTime = 0f;
                    _aircraftMissingTime = 0f;
                    _respawnScreenShown = false;
                    DamagePatches.DestructionHandled = false; // Reset for next death
                    DamagePatches.LastAttackerId = 0;
                    DamagePatches.LastAttackerWeapon = "";
                    DamagePatches.LastDamageTime = 0f;
                    _hasBeenDestroyedPropChecked = false; // Reset for new aircraft type
                    _lastAircraftName = _cachedLocalAircraft.name;
                    _hadAircraftBefore = true;
                    
                    // Use proper aircraft type name (e.g. "AV8B") not display name (e.g. "Kestrel 1 (MRGoldberg)")
                    string respawnTypeName = GetLocalAircraftTypeName(_cachedUniAircraft);
                    if (string.IsNullOrEmpty(respawnTypeName))
                    {
                        respawnTypeName = ReflectionHelper.MapAircraftNameFromString(_cachedLocalAircraft.name) ?? _cachedLocalAircraft.name;
                    }
                    Plugin.Instance?.Network?.SendAircraftRespawnNotification(respawnTypeName);
                    Plugin.Log?.LogInfo($"[FlightGamePatches] Respawned with: {_cachedLocalAircraft.name} (type: {respawnTypeName})");

                    // Reinitialize reflection for new aircraft
                    _reflectionInitialized = false;
                }
                else if (_lastAircraftName == null)
                {
                    // First time seeing this aircraft
                    _lastAircraftName = _cachedLocalAircraft.name;
                    _hadAircraftBefore = true;
                    _aircraftMissingTime = 0f;
                    Plugin.Log?.LogInfo($"[FlightGamePatches] First aircraft detected: {_lastAircraftName}");
                }
                else if (_cachedLocalAircraft.name != _lastAircraftName)
                {
                    // Aircraft changed (switched planes?)
                    _lastAircraftName = _cachedLocalAircraft.name;
                    _hadAircraftBefore = true;
                    _aircraftMissingTime = 0f;
                    // Use proper aircraft type name, not display name
                    string changedTypeName = GetLocalAircraftTypeName(_cachedUniAircraft);
                    if (string.IsNullOrEmpty(changedTypeName))
                    {
                        changedTypeName = ReflectionHelper.MapAircraftNameFromString(_cachedLocalAircraft.name) ?? _cachedLocalAircraft.name;
                    }
                    Plugin.Instance?.Network?.SendAircraftRespawnNotification(changedTypeName);
                    Plugin.Log?.LogInfo($"[FlightGamePatches] Aircraft changed to: {_cachedLocalAircraft.name} (type: {changedTypeName})");

                    // Reinitialize reflection for new aircraft
                    _reflectionInitialized = false;
                }
                else
                {
                    // Aircraft still alive and same — reset missing timer
                    _aircraftMissingTime = 0f;
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

                // Get FlightControls field directly from UniAircraft
                // This is the aircraft-level FlightInput which reflects actual player stick inputs
                _flightControlsField = aircraftType.GetField("FlightControls", flags);
                Plugin.Log?.LogInfo($"[FlightGamePatches] UniAircraft.FlightControls field: {(_flightControlsField != null ? "FOUND" : "NOT FOUND")}");

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

                        // Get FlightInput field from pilot (secondary/fallback)
                        _flightInputField = pilotType.GetField("FlightInput", flags);

                        // Determine the FlightInput object to init properties from.
                        // Prefer UniAircraft.FlightControls (reflects player stick inputs).
                        // Fall back to UniPilot.FlightInput.
                        object initFlightInput = null;

                        if (_flightControlsField != null)
                        {
                            initFlightInput = _flightControlsField.GetValue(uniAircraft);
                            if (initFlightInput != null)
                            {
                                Plugin.Log?.LogInfo($"[FlightGamePatches] Using UniAircraft.FlightControls as primary FlightInput source");
                            }
                        }

                        if (initFlightInput == null && _flightInputField != null)
                        {
                            initFlightInput = _flightInputField.GetValue(uniPilot);
                            Plugin.Log?.LogInfo($"[FlightGamePatches] Using UniPilot.FlightInput as fallback FlightInput source");
                        }

                        // Cache the initial reference (will be RE-READ each frame in SendLocalPlayerState)
                        _cachedFlightInput = initFlightInput;

                        if (initFlightInput != null)
                        {
                            var inputType = initFlightInput.GetType();
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

                            // Explore StickAndRudder sub-object (raw stick input)
                            _stickAndRudderField = inputType.GetField("StickAndRudder", flags);
                            if (_stickAndRudderField != null)
                            {
                                var sarObj = _stickAndRudderField.GetValue(initFlightInput);
                                if (sarObj != null)
                                {
                                    var sarType = sarObj.GetType();
                                    Plugin.Log?.LogInfo($"[FlightGamePatches] Found StickAndRudder: {sarType.Name}");
                                    Plugin.Log?.LogInfo("[FlightGamePatches] StickAndRudder FIELDS:");
                                    foreach (var field in sarType.GetFields(flags))
                                    {
                                        Plugin.Log?.LogInfo($"  Field: {field.Name} ({field.FieldType.Name})");
                                    }
                                    Plugin.Log?.LogInfo("[FlightGamePatches] StickAndRudder PROPERTIES:");
                                    foreach (var prop in sarType.GetProperties(flags))
                                    {
                                        Plugin.Log?.LogInfo($"  Prop: {prop.Name} ({prop.PropertyType.Name})");
                                    }
                                    _sarPitchProperty = sarType.GetProperty("Pitch", flags);
                                    _sarRollProperty = sarType.GetProperty("Roll", flags);
                                    _sarYawProperty = sarType.GetProperty("Yaw", flags)
                                        ?? sarType.GetProperty("Rudder", flags);
                                    _sarPitchField = sarType.GetField("Pitch", flags)
                                        ?? sarType.GetField("pitch", flags);
                                    _sarRollField = sarType.GetField("Roll", flags)
                                        ?? sarType.GetField("roll", flags);
                                    _sarYawField = sarType.GetField("Yaw", flags)
                                        ?? sarType.GetField("yaw", flags)
                                        ?? sarType.GetField("Rudder", flags)
                                        ?? sarType.GetField("rudder", flags);
                                }
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

                // Try to get FlightStats
                _flightStatsProperty = aircraftType.GetProperty("FlightStats", flags);
                _flightStatsField = aircraftType.GetField("FlightStats", flags);

                if (_flightStatsProperty != null)
                {
                    _cachedFlightStats = _flightStatsProperty.GetValue(uniAircraft);
                }
                else if (_flightStatsField != null)
                {
                    _cachedFlightStats = _flightStatsField.GetValue(uniAircraft);
                }

                if (_cachedFlightStats != null)
                {
                    var flightStatsType = _cachedFlightStats.GetType();
                    _speedKIASProperty = flightStatsType.GetProperty("IndicatedAirspeedKnots", flags);
                    _speedKIASField = flightStatsType.GetField("IndicatedAirspeedKnots", flags);
                }

                // Try to get BrakeState
                _brakeStateProperty = aircraftType.GetProperty("BrakeState", flags);
                _brakeStateField = aircraftType.GetField("BrakeState", flags);

                // ============================================
                // Summary
                // ============================================
                Plugin.Log?.LogInfo("[FlightGamePatches] ========== REFLECTION SUMMARY ==========");
                Plugin.Log?.LogInfo($"  Engine Throttle: {_engineThrottleField?.Name ?? "NOT FOUND"}");
                Plugin.Log?.LogInfo($"  Engine Afterburner: {_engineAfterburnerField?.Name ?? "NOT FOUND"}");
                Plugin.Log?.LogInfo($"  Gear IsGearLowered: {_gearIsDownProperty?.Name ?? "NOT FOUND"}");
                Plugin.Log?.LogInfo($"  Flaps AreFlapsDown: {_flapsDeployedProperty?.Name ?? "NOT FOUND"}");
                Plugin.Log?.LogInfo($"  FlightControls field: {(_flightControlsField != null ? "FOUND" : "NOT FOUND")}");
                Plugin.Log?.LogInfo($"  FlightInput (pilot): {(_flightInputField != null ? "FOUND" : "NOT FOUND")}");
                Plugin.Log?.LogInfo($"  Active FlightInput: {(_cachedFlightInput != null ? "FOUND" : "NOT FOUND")}");
                Plugin.Log?.LogInfo($"  FlightInput Pitch: prop={_flightInputPitchProperty?.Name ?? "null"} field={_flightInputPitchField?.Name ?? "null"}");
                Plugin.Log?.LogInfo($"  FlightInput Roll: prop={_flightInputRollProperty?.Name ?? "null"} field={_flightInputRollField?.Name ?? "null"}");
                Plugin.Log?.LogInfo($"  FlightInput Yaw: prop={_flightInputYawProperty?.Name ?? "null"} field={_flightInputYawField?.Name ?? "null"}");
                Plugin.Log?.LogInfo($"  StickAndRudder: {(_stickAndRudderField != null ? "FOUND" : "NOT FOUND")}");
                Plugin.Log?.LogInfo($"  SAR Pitch: prop={_sarPitchProperty?.Name ?? "null"} field={_sarPitchField?.Name ?? "null"}");
                Plugin.Log?.LogInfo($"  SAR Roll: prop={_sarRollProperty?.Name ?? "null"} field={_sarRollField?.Name ?? "null"}");
                Plugin.Log?.LogInfo($"  SAR Yaw: prop={_sarYawProperty?.Name ?? "null"} field={_sarYawField?.Name ?? "null"}");
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
                bool isFlareFiring = false;
                bool isChaffFiring = false;
                bool isNavMode = true;  // Default to NavMode (gun safety on)
                bool isWeightOnWheels = true;  // Default to on ground
                float pitch = 0f;
                float roll = 0f;
                float yaw = 0f;
                float nozzleAngle = 0f;
                float speedKIAS = 0f;
                float brakeState = 0f;

                if (_cachedUniAircraft != null)
                {
                    try
                    {
                        // CRITICAL: Re-read FlightInput object each frame!
                        // The cached reference from init may be stale or point to the wrong object.
                        // Prefer UniAircraft.FlightControls (reflects actual player stick input).
                        object liveFlightInput = _cachedFlightInput;

                        if (_flightControlsField != null)
                        {
                            var freshInput = _flightControlsField.GetValue(_cachedUniAircraft);
                            if (freshInput != null) liveFlightInput = freshInput;
                        }
                        else if (_flightInputField != null)
                        {
                            // Re-read from pilot each frame in case object was replaced
                            var uniPilot = _uniPilotProperty?.GetValue(_cachedUniAircraft);
                            if (uniPilot != null)
                            {
                                var freshInput = _flightInputField.GetValue(uniPilot);
                                if (freshInput != null) liveFlightInput = freshInput;
                            }
                        }

                        // Read from FlightInput (primary source for player inputs)
                        if (liveFlightInput != null)
                        {
                            // Throttle from FlightInput
                            if (_flightInputThrottleField != null)
                                throttle = Convert.ToSingle(_flightInputThrottleField.GetValue(liveFlightInput));

                            // Afterburner from FlightInput.IsAfterburning (boolean property)
                            if (_flightInputIsAfterburningProperty != null)
                                afterburner = Convert.ToBoolean(_flightInputIsAfterburningProperty.GetValue(liveFlightInput));

                            // Pitch/Roll/Yaw — try FlightInput properties/fields first
                            if (_flightInputPitchProperty != null)
                                pitch = Convert.ToSingle(_flightInputPitchProperty.GetValue(liveFlightInput));
                            else if (_flightInputPitchField != null)
                                pitch = Convert.ToSingle(_flightInputPitchField.GetValue(liveFlightInput));

                            if (_flightInputRollProperty != null)
                                roll = Convert.ToSingle(_flightInputRollProperty.GetValue(liveFlightInput));
                            else if (_flightInputRollField != null)
                                roll = Convert.ToSingle(_flightInputRollField.GetValue(liveFlightInput));

                            if (_flightInputYawProperty != null)
                                yaw = Convert.ToSingle(_flightInputYawProperty.GetValue(liveFlightInput));
                            else if (_flightInputYawField != null)
                                yaw = Convert.ToSingle(_flightInputYawField.GetValue(liveFlightInput));

                            // If pitch/roll/yaw are STILL zero, try StickAndRudder sub-object
                            if (pitch == 0f && roll == 0f && yaw == 0f && _stickAndRudderField != null)
                            {
                                var sarObj = _stickAndRudderField.GetValue(liveFlightInput);
                                if (sarObj != null)
                                {
                                    if (_sarPitchProperty != null)
                                        pitch = Convert.ToSingle(_sarPitchProperty.GetValue(sarObj));
                                    else if (_sarPitchField != null)
                                        pitch = Convert.ToSingle(_sarPitchField.GetValue(sarObj));

                                    if (_sarRollProperty != null)
                                        roll = Convert.ToSingle(_sarRollProperty.GetValue(sarObj));
                                    else if (_sarRollField != null)
                                        roll = Convert.ToSingle(_sarRollField.GetValue(sarObj));

                                    if (_sarYawProperty != null)
                                        yaw = Convert.ToSingle(_sarYawProperty.GetValue(sarObj));
                                    else if (_sarYawField != null)
                                        yaw = Convert.ToSingle(_sarYawField.GetValue(sarObj));
                                }
                            }

                            // Nozzle angle (VTOL) - NozzleAnalog is 0-1, convert to angle
                            if (_flightInputNozzleAnalogProperty != null)
                            {
                                float nozzleAnalog = Convert.ToSingle(_flightInputNozzleAnalogProperty.GetValue(liveFlightInput));
                                // Get actual nozzle angle from engine if available
                                nozzleAngle = nozzleAnalog * 90f; // Assume 90 deg max for now
                            }
                        }

                        // Try to get actual nozzle angle from first engine
                        // VTOLNozzleAngle is a FIELD on UniEngine, not a property
                        if (_cachedFirstEngine != null)
                        {
                            var engineType = _cachedFirstEngine.GetType();
                            // Try the method first (preferred)
                            var getTrueNozzleAngle = engineType.GetMethod("GetTrueNozzleAngle", BindingFlags.Public | BindingFlags.Instance);
                            if (getTrueNozzleAngle != null)
                            {
                                nozzleAngle = Convert.ToSingle(getTrueNozzleAngle.Invoke(_cachedFirstEngine, null));
                            }
                            else
                            {
                                // Fallback to field
                                var nozzleAngleField = engineType.GetField("VTOLNozzleAngle", BindingFlags.Public | BindingFlags.Instance);
                                if (nozzleAngleField != null)
                                {
                                    nozzleAngle = Convert.ToSingle(nozzleAngleField.GetValue(_cachedFirstEngine));
                                }
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

                        // Read SpeedKIAS
                        if (_cachedFlightStats != null)
                        {
                            if (_speedKIASProperty != null)
                                speedKIAS = Convert.ToSingle(_speedKIASProperty.GetValue(_cachedFlightStats));
                            else if (_speedKIASField != null)
                                speedKIAS = Convert.ToSingle(_speedKIASField.GetValue(_cachedFlightStats));
                        }

                        // Read BrakeState
                        if (_brakeStateProperty != null)
                            brakeState = Convert.ToSingle(_brakeStateProperty.GetValue(_cachedUniAircraft));
                        else if (_brakeStateField != null)
                            brakeState = Convert.ToSingle(_brakeStateField.GetValue(_cachedUniAircraft));

                        // Try to detect gun firing and countermeasure state
                        isFiring = DetectGunFiring(_cachedUniAircraft);
                        
                        // Override: if the player has guns disabled (Master Arm off or wrong weapon selected), 
                        // verify they actually have a gun selected and ammo
                        if (isFiring && _cachedUniAircraft != null)
                        {
                            try
                            {
                                var fcProp = _cachedUniAircraft.GetType().GetProperty("FireControl", BindingFlags.Public | BindingFlags.Instance);
                                if (fcProp != null)
                                {
                                    var fc = fcProp.GetValue(_cachedUniAircraft);
                                    if (fc != null)
                                    {
                                        var fcType = fc.GetType();
                                        // Check if gun is selected weapon
                                        var gunProp = fcType.GetProperty("Gun", BindingFlags.Public | BindingFlags.Instance);
                                        var activeWeaponProp = fcType.GetMethod("GetActiveWeapon", BindingFlags.Public | BindingFlags.Instance);
                                        var masterArmProp = fcType.GetProperty("IsMasterArmOn", BindingFlags.Public | BindingFlags.Instance);
                                        
                                        if (gunProp != null && activeWeaponProp != null)
                                        {
                                            var gun = gunProp.GetValue(fc);
                                            var activeWeapon = activeWeaponProp.Invoke(fc, null);
                                            
                                            // 1. Check if Master Arm is safe
                                            if (masterArmProp != null)
                                            {
                                                bool isMasterArmOn = Convert.ToBoolean(masterArmProp.GetValue(fc));
                                                if (!isMasterArmOn)
                                                {
                                                    isFiring = false;
                                                }
                                            }

                                            // 2. Check if gun is the active weapon
                                            if (gun == null || (activeWeapon != null && !ReferenceEquals(gun, activeWeapon)))
                                            {
                                                isFiring = false;
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        isFlareFiring = DetectFlareFiring();
                        isChaffFiring = DetectChaffFiring();
                        
                        // Read IsNavMode (gun safety) from FireControl
                        isNavMode = ReadIsNavMode(_cachedUniAircraft);
                        
                        // Read IsWeightOnWheels (ground state) from LandingGear
                        isWeightOnWheels = ReadIsWeightOnWheels(_cachedUniAircraft);
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
                    SequenceNumber = ++_stateSequenceNumber,
                    AircraftType = GetLocalAircraftTypeName(_cachedUniAircraft),

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
                    SpeedKIAS = speedKIAS,
                    BrakeState = brakeState,

                    Timestamp = Time.time
                };

                // Set flags
                state.Afterburner = afterburner;
                state.GearDown = gearDown;
                state.FlapsDown = flapsDown;
                state.IsFiring = isFiring;
                state.IsFlareFiring = isFlareFiring;
                state.IsChaffFiring = isChaffFiring;
                state.IsNavMode = isNavMode;  // Sync NavMode (gun safety)
                state.IsWeightOnWheels = isWeightOnWheels;  // Sync ground state
                
                if (LogHelper.IsEnabled(LogCategory.Patches) &&
                    LogHelper.ShouldSample("FlightGamePatches.SendLocalPlayerState", LogHelper.HighFreqSampleRate))
                {
                    LogHelper.Info(LogCategory.Patches,
                        $"[FlightGamePatches] State pos={absolutePos} vel={vel} throttle={throttle:F2} " +
                        $"pitch={pitch:F2} roll={roll:F2} yaw={yaw:F2} " +
                        $"ab={afterburner} gear={gearDown} flaps={flapsDown} fire={isFiring}" +
                        $" flare={isFlareFiring} chaff={isChaffFiring} nav={isNavMode} wow={isWeightOnWheels}");
                }

                Plugin.Instance.Network.SendAircraftState(state);

                // Update collision manager with local player info
                AircraftCollisionManager.Instance?.UpdateLocalPlayerInfo(localAircraft, vel, GetLocalPlayerId());
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
                            // Skip aircraft that is already marked destroyed (GO lingers during death animation)
                            if (IsAircraftMarkedDestroyed(playerAircraft))
                            {
                                // Don't cache or return a destroyed aircraft — it will just cause re-detection spam
                                return null;
                            }

                            _cachedLocalAircraft = playerGo;
                            _cachedUniAircraft = playerAircraft;
                            if (LogHelper.ShouldLogInterval("FlightGamePatches.PlayerAircraftFound", 10f))
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

                            // Skip aircraft that is already marked destroyed
                            if (IsAircraftMarkedDestroyed(component))
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

        private static string GetLocalAircraftTypeName(Component uniAircraft)
        {
            if (uniAircraft == null) return string.Empty;

            try
            {
                // Use shared helper for Data→Name extraction
                var name = ReflectionHelper.GetAircraftNameFromData(uniAircraft);
                if (!string.IsNullOrEmpty(name)) return name;

                var go = uniAircraft.gameObject;
                if (go != null)
                {
                    var mapped = ReflectionHelper.MapAircraftNameFromString(go.name);
                    if (!string.IsNullOrEmpty(mapped)) return mapped;
                    return go.name;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[FlightGamePatches] GetLocalAircraftTypeName error: {ex.Message}");
            }

            return string.Empty;
        }

        private static ulong GetLocalPlayerId()
        {
            return Plugin.Instance.Network.LocalPeerId;
        }

        // Cached gun/countermeasure detection
        private static object _cachedWeaponInput = null;
        private static FieldInfo _weaponInputIsFiringField = null;
        private static FieldInfo _weaponInputIsFlareFiringField = null;
        private static FieldInfo _weaponInputIsChaffFiringField = null;
        private static bool _gunReflectionInitialized = false;

        // Cached direct input access
        private static object _directPlayerInput = null;
        private static FieldInfo _directWeaponInputField = null;
        private static FieldInfo _directIsFiringField = null;
        private static FieldInfo _directIsFlareFiringField = null;
        private static FieldInfo _directIsChaffFiringField = null;

        /// <summary>
        /// Detect if the aircraft is currently firing its gun
        /// Uses robust detection: check UniAircraft first, then fallback to FlightGame.PlayerInput
        /// </summary>
        private static bool DetectGunFiring(Component aircraft)
        {
            try
            {
                // Method 1: Check UniAircraft.WeaponControls (Primary)
                if (!_gunReflectionInitialized)
                {
                    InitializeGunReflection(aircraft);
                }

                // Re-read WeaponControls each frame (object reference may change)
                if (_weaponInputIsFiringField != null && _cachedUniAircraft != null)
                {
                    var weaponControlsField = _cachedUniAircraft.GetType().GetField("WeaponControls",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (weaponControlsField != null)
                    {
                        var liveWeaponInput = weaponControlsField.GetValue(_cachedUniAircraft);
                        if (liveWeaponInput != null)
                        {
                            _cachedWeaponInput = liveWeaponInput;
                        }
                    }
                }

                if (_cachedWeaponInput != null && _weaponInputIsFiringField != null)
                {
                    bool isFiring = Convert.ToBoolean(_weaponInputIsFiringField.GetValue(_cachedWeaponInput));
                    if (isFiring) return true; // If true, return immediately
                }

                // Method 2: Check FlightGame.PlayerInput (Fallback/Override)
                // This catches cases where UniAircraft.WeaponControls is stale or not updated for local player
                if (FlightGameInstance != null)
                {
                    if (_directPlayerInput == null)
                    {
                        // Init direct input reflection
                        var flightGameType = FlightGameInstance.GetType();
                        var playerInputField = flightGameType.GetField("PlayerInput", BindingFlags.Public | BindingFlags.Instance);
                        if (playerInputField != null)
                        {
                            _directPlayerInput = playerInputField.GetValue(FlightGameInstance);
                            if (_directPlayerInput != null)
                            {
                                var playerInputType = _directPlayerInput.GetType();
                                _directWeaponInputField = playerInputType.GetField("WeaponInput", BindingFlags.Public | BindingFlags.Instance);
                                if (_directWeaponInputField != null)
                                {
                                    var wiType = _directWeaponInputField.FieldType;
                                    _directIsFiringField = wiType.GetField("IsFiring", BindingFlags.Public | BindingFlags.Instance);
                                    _directIsFlareFiringField = wiType.GetField("IsFlareFiring", BindingFlags.Public | BindingFlags.Instance);
                                    _directIsChaffFiringField = wiType.GetField("IsChaffFiring", BindingFlags.Public | BindingFlags.Instance);
                                    
                                    Plugin.Log?.LogInfo("[FlightGamePatches] Direct PlayerInput reflection initialized");
                                }
                            }
                        }
                    }

                    if (_directPlayerInput != null && _directWeaponInputField != null && _directIsFiringField != null)
                    {
                        var wi = _directWeaponInputField.GetValue(_directPlayerInput);
                        if (wi != null)
                        {
                            return Convert.ToBoolean(_directIsFiringField.GetValue(wi));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_gunReflectionInitialized)
                {
                    Plugin.Log?.LogWarning($"[FlightGamePatches] Gun detection error: {ex.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// Detect if the aircraft is currently deploying flares
        /// </summary>
        private static bool DetectFlareFiring()
        {
            try
            {
                // Method 1: UniAircraft
                if (_cachedWeaponInput != null && _weaponInputIsFlareFiringField != null)
                {
                    bool firing = Convert.ToBoolean(_weaponInputIsFlareFiringField.GetValue(_cachedWeaponInput));
                    if (firing) return true;
                }
                
                // Method 2: Direct PlayerInput
                if (_directPlayerInput != null && _directWeaponInputField != null && _directIsFlareFiringField != null)
                {
                    var wi = _directWeaponInputField.GetValue(_directPlayerInput);
                    if (wi != null)
                    {
                        return Convert.ToBoolean(_directIsFlareFiringField.GetValue(wi));
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Detect if the aircraft is currently deploying chaff
        /// </summary>
        private static bool DetectChaffFiring()
        {
            try
            {
                // Method 1: UniAircraft
                if (_cachedWeaponInput != null && _weaponInputIsChaffFiringField != null)
                {
                    bool firing = Convert.ToBoolean(_weaponInputIsChaffFiringField.GetValue(_cachedWeaponInput));
                    if (firing) return true;
                }
                
                // Method 2: Direct PlayerInput
                if (_directPlayerInput != null && _directWeaponInputField != null && _directIsChaffFiringField != null)
                {
                    var wi = _directWeaponInputField.GetValue(_directPlayerInput);
                    if (wi != null)
                    {
                        return Convert.ToBoolean(_directIsChaffFiringField.GetValue(wi));
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Read IsNavMode (gun safety) from FireControl
        /// Returns true if NavMode is ON (gun safety engaged, should not fire)
        /// </summary>
        private static bool ReadIsNavMode(Component aircraft)
        {
            try
            {
                if (aircraft == null) return true; // Default to safe

                // FireControl is a FIELD, not a property - use GetField instead
                var fcField = aircraft.GetType().GetField("FireControl", BindingFlags.Public | BindingFlags.Instance);
                if (fcField == null) return true;

                var fc = fcField.GetValue(aircraft);
                if (fc == null) return true;

                // IsNavMode is a public property on FireControl
                var isNavModeProp = fc.GetType().GetProperty("IsNavMode", BindingFlags.Public | BindingFlags.Instance);
                if (isNavModeProp == null) return true;

                return Convert.ToBoolean(isNavModeProp.GetValue(fc));
            }
            catch
            {
                return true; // Default to safe on error
            }
        }

        /// <summary>
        /// Read IsWeightOnWheels from LandingGear - returns true if plane is on ground
        /// When true, gun should not fire (same as game's FireControl logic)
        /// </summary>
        private static bool ReadIsWeightOnWheels(Component aircraft)
        {
            try
            {
                if (aircraft == null) return true; // Default to on ground

                // LandingGear is a field on UniAircraft
                var lgField = aircraft.GetType().GetField("LandingGear", BindingFlags.Public | BindingFlags.Instance);
                if (lgField == null) return true;

                var lg = lgField.GetValue(aircraft);
                if (lg == null) return true;

                // IsWeightOnWheels is a property on LandingGear
                var wowProp = lg.GetType().GetProperty("IsWeightOnWheels", BindingFlags.Public | BindingFlags.Instance);
                if (wowProp == null) return true;

                return Convert.ToBoolean(wowProp.GetValue(lg));
            }
            catch
            {
                return true; // Default to on ground on error
            }
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
                        _weaponInputIsFlareFiringField = weaponInputType.GetField("IsFlareFiring", flags);
                        _weaponInputIsChaffFiringField = weaponInputType.GetField("IsChaffFiring", flags);

                        if (_weaponInputIsFiringField != null)
                        {
                            Plugin.Log?.LogInfo("[FlightGamePatches] Found WeaponInput.IsFiring field - gun detection ready!");
                        }
                        
                        if (_weaponInputIsFlareFiringField != null || _weaponInputIsChaffFiringField != null)
                        {
                            Plugin.Log?.LogInfo($"[FlightGamePatches] Countermeasure detection: Flare={_weaponInputIsFlareFiringField != null} Chaff={_weaponInputIsChaffFiringField != null}");
                        }
                        
                        if (_weaponInputIsFiringField == null)
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
            _weaponInputIsFlareFiringField = null;
            _weaponInputIsChaffFiringField = null;
            _gunReflectionInitialized = false;
            
            // Clear direct input cache
            _directPlayerInput = null;
            _directWeaponInputField = null;
            _directIsFiringField = null;
            _directIsFlareFiringField = null;
            _directIsChaffFiringField = null;
            
            _flightControlsField = null;
            _stickAndRudderField = null;
            _sarPitchProperty = null;
            _sarRollProperty = null;
            _sarYawProperty = null;
            _sarPitchField = null;
            _sarRollField = null;
            _sarYawField = null;
            _inFlight = false;
            _reflectionInitialized = false;
            _lastAircraftName = null;
            _wasDestroyed = false;
            _hadAircraftBefore = false;
            _aircraftMissingTime = 0f;
            _hasBeenDestroyedPropChecked = false;
            _hasBeenDestroyedProp = null;
            _respawnScreenShown = false;
            WeaponPatches.ClearState();
        }
    }
}
