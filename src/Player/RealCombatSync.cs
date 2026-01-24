using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using TCAMultiplayer.Networking;

namespace TCAMultiplayer.Player
{
    /// <summary>
    /// Syncs REAL game combat systems for multiplayer:
    /// - Radar lock for RWR warnings
    /// - Missiles added to Munition.LaunchedMissiles for threat warnings
    /// - Real bullets fired via Bullet2Manager for visible tracers
    /// </summary>
    public static class RealCombatSync
    {
        // Cached reflection
        private static bool _initialized = false;
        
        // Radar system
        private static Type _radarType;
        private static FieldInfo _radarActiveRadarsField;
        private static FieldInfo _radarLockedTargetField;
        private static MethodInfo _radarLockTargetMethod;
        private static MethodInfo _radarUnlockMethod;
        private static MethodInfo _radarActivateMethod;
        private static PropertyInfo _radarIsActiveProp;
        
        // Munition system
        private static Type _munitionType;
        private static FieldInfo _launchedMissilesField;
        private static FieldInfo _munitionTargetField;
        private static FieldInfo _munitionHasExplodedField;
        private static FieldInfo _munitionSeekerField;
        private static PropertyInfo _munitionSeekerSignatureProp;
        
        // Seeker system
        private static Type _seekerType;
        private static FieldInfo _seekerIsTrackingField;
        private static FieldInfo _seekerIsSpoofedField;
        private static FieldInfo _seekerTargetField;
        
        // Bullet system
        private static Type _bullet2ManagerType;
        private static PropertyInfo _bullet2ManagerInstanceProp;
        private static MethodInfo _bullet2ManagerFireBulletMethod;
        private static Type _bulletDataType;
        
        // Target system
        private static Type _targetType;
        
        // Tracked network missiles (for cleanup)
        private static List<object> _networkMissiles = new List<object>();
        
        // Remote aircraft radar (for lock sync)
        private static Dictionary<ulong, object> _remoteRadars = new Dictionary<ulong, object>();
        
        public static void Initialize()
        {
            if (_initialized) return;
            
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                
                // Radar
                _radarType = Type.GetType("Falcon.Targeting.Radar, Assembly-CSharp");
                if (_radarType != null)
                {
                    _radarActiveRadarsField = _radarType.GetField("ActiveRadars", flags);
                    _radarLockedTargetField = _radarType.GetField("<LockedTarget>k__BackingField", flags);
                    _radarLockTargetMethod = _radarType.GetMethod("LockTarget", flags);
                    _radarUnlockMethod = _radarType.GetMethod("UnlockTarget", flags);
                    _radarActivateMethod = _radarType.GetMethod("ActivateRadar", flags);
                    _radarIsActiveProp = _radarType.GetProperty("IsActive", flags);
                    Plugin.Log?.LogInfo($"[RealCombatSync] Radar type found, ActiveRadars={_radarActiveRadarsField != null}, LockTarget={_radarLockTargetMethod != null}");
                }
                
                // Munition
                _munitionType = Type.GetType("Falcon.Stores.Munition, Assembly-CSharp");
                if (_munitionType != null)
                {
                    _launchedMissilesField = _munitionType.GetField("LaunchedMissiles", flags);
                    _munitionTargetField = _munitionType.GetField("Target", flags);
                    _munitionHasExplodedField = _munitionType.GetField("<HasExploded>k__BackingField", flags);
                    _munitionSeekerField = _munitionType.GetField("seeker", flags);
                    _munitionSeekerSignatureProp = _munitionType.GetProperty("SeekerSignature", flags);
                    Plugin.Log?.LogInfo($"[RealCombatSync] Munition type found, LaunchedMissiles={_launchedMissilesField != null}");
                }
                
                // Seeker
                _seekerType = Type.GetType("Falcon.Stores.Seeker, Assembly-CSharp");
                if (_seekerType != null)
                {
                    _seekerIsTrackingField = _seekerType.GetField("<IsTracking>k__BackingField", flags);
                    _seekerIsSpoofedField = _seekerType.GetField("<IsSpoofed>k__BackingField", flags);
                    _seekerTargetField = _seekerType.GetField("<Target>k__BackingField", flags);
                    Plugin.Log?.LogInfo($"[RealCombatSync] Seeker type found, IsTracking={_seekerIsTrackingField != null}");
                }
                
                // Bullet2Manager
                _bullet2ManagerType = Type.GetType("Falcon.Weapons.Bullet2Manager, Assembly-CSharp");
                if (_bullet2ManagerType != null)
                {
                    _bullet2ManagerInstanceProp = _bullet2ManagerType.GetProperty("Instance", flags);
                    _bullet2ManagerInstanceField = _bullet2ManagerType.GetField("Instance", flags);
                    _bullet2ManagerFireBulletMethod = _bullet2ManagerType.GetMethod("FireBullet", flags);
                    Plugin.Log?.LogInfo($"[RealCombatSync] Bullet2Manager type found, InstanceProp={_bullet2ManagerInstanceProp != null}, InstanceField={_bullet2ManagerInstanceField != null}");
                }
                
                // BulletData
                _bulletDataType = Type.GetType("Falcon.Weapons.BulletData, Assembly-CSharp");
                
                // Target
                _targetType = Type.GetType("Falcon.Targeting.Target, Assembly-CSharp");
                
                _initialized = true;
                Plugin.Log?.LogInfo("[RealCombatSync] Initialization complete");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[RealCombatSync] Initialize error: {ex.Message}");
                _initialized = true; // Don't retry
            }
        }
        
        #region Radar Lock Sync
        
        /// <summary>
        /// Set up the remote aircraft's radar for threat detection
        /// </summary>
        public static void SetupRemoteRadar(GameObject remoteAircraft, ulong playerId)
        {
            Initialize();
            if (_radarType == null) return;
            
            try
            {
                // Find the Radar component on the remote aircraft
                var radar = FindRadarOnAircraft(remoteAircraft);
                if (radar == null)
                {
                    Plugin.Log?.LogWarning("[RealCombatSync] No radar found on remote aircraft");
                    return;
                }
                
                // Activate the radar and add to ActiveRadars
                if (_radarActivateMethod != null)
                {
                    _radarActivateMethod.Invoke(radar, null);
                    Plugin.Log?.LogInfo($"[RealCombatSync] Activated remote radar for player {playerId}");
                }
                
                // Store for later lock updates
                _remoteRadars[playerId] = radar;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[RealCombatSync] SetupRemoteRadar error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update radar lock state when we receive a RadarLock packet
        /// This makes the local player's RWR detect the lock
        /// </summary>
        public static void SetRemoteRadarLock(ulong attackerId, bool isLocked)
        {
            Initialize();
            if (_radarType == null) return;
            
            try
            {
                // Get the remote player's radar
                if (!_remoteRadars.TryGetValue(attackerId, out object radar) || radar == null)
                {
                    Plugin.Log?.LogWarning($"[RealCombatSync] No radar found for attacker {attackerId}");
                    return;
                }
                
                if (isLocked)
                {
                    // Find local player's Target
                    var localTarget = FindLocalPlayerTarget();
                    if (localTarget == null)
                    {
                        Plugin.Log?.LogWarning("[RealCombatSync] No local player target found for lock");
                        return;
                    }
                    
                    // Lock onto local player
                    if (_radarLockTargetMethod != null)
                    {
                        _radarLockTargetMethod.Invoke(radar, new object[] { localTarget, false }); // false = don't check radar constraints
                        Plugin.Log?.LogInfo($"[RealCombatSync] Remote radar {attackerId} locked onto local player - RWR should detect!");
                    }
                }
                else
                {
                    // Unlock
                    if (_radarUnlockMethod != null)
                    {
                        _radarUnlockMethod.Invoke(radar, null);
                        Plugin.Log?.LogInfo($"[RealCombatSync] Remote radar {attackerId} unlocked");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[RealCombatSync] SetRemoteRadarLock error: {ex.Message}");
            }
        }
        
        private static object FindRadarOnAircraft(GameObject aircraft)
        {
            if (_radarType == null) return null;
            
            // First try to find Radar directly
            var allComponents = aircraft.GetComponentsInChildren<Component>(true);
            foreach (var comp in allComponents)
            {
                if (comp != null && comp.GetType().Name == "Radar")
                {
                    return comp;
                }
            }
            
            // Try to get from UniAircraft
            var uniAircraftType = Type.GetType("Falcon.UniversalAircraft.UniAircraft, Assembly-CSharp");
            if (uniAircraftType != null)
            {
                var uniAircraft = aircraft.GetComponent(uniAircraftType);
                if (uniAircraft != null)
                {
                    var radarProp = uniAircraftType.GetProperty("Radar", 
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (radarProp != null)
                    {
                        return radarProp.GetValue(uniAircraft);
                    }
                }
            }
            
            return null;
        }
        
        #endregion
        
        #region Missile Threat Sync
        
        // GameDataStores for spawning real missiles
        private static Type _gameDataStoresType;
        private static MethodInfo _spawnStoreMethod;
        private static MethodInfo _hasStoreMethod;
        
        // GuidanceProperties for creating Seeker
        private static Type _guidancePropertiesType;
        private static ConstructorInfo _seekerConstructor;
        
        /// <summary>
        /// Add a network missile to the game's LaunchedMissiles list
        /// Uses GameDataStores.SpawnStore() to create a REAL missile with proper Seeker
        /// </summary>
        public static void AddNetworkMissileToThreatSystem(MissileLaunchPacket packet, Vector3 localLaunchPos)
        {
            Initialize();
            if (_munitionType == null || _launchedMissilesField == null) return;
            
            try
            {
                // Find local target (may be null if player died - that's OK, we still show the missile)
                var localTarget = FindLocalPlayerTarget();
                if (localTarget == null)
                {
                    Plugin.Log?.LogInfo("[RealCombatSync] No local target - missile will fly straight (unguided)");
                }
                
                // Get the LaunchedMissiles list
                var launchedMissiles = _launchedMissilesField.GetValue(null) as System.Collections.IList;
                if (launchedMissiles == null)
                {
                    Plugin.Log?.LogWarning("[RealCombatSync] LaunchedMissiles list is null");
                    return;
                }
                
                // Try to spawn a real missile from GameDataStores
                GameObject missileObj = SpawnMissileFromGameData(packet.MissileType);
                
                if (missileObj == null)
                {
                    // Fallback: load from Resources
                    missileObj = LoadMissileFromResources(packet.MissileType);
                }
                
                if (missileObj == null)
                {
                    Plugin.Log?.LogWarning($"[RealCombatSync] Could not create missile for {packet.MissileType}");
                    return;
                }
                
                missileObj.name = $"MP_NetworkMissile_{packet.MissileType}";
                missileObj.transform.position = localLaunchPos;
                missileObj.transform.forward = new Vector3(packet.LaunchDirX, packet.LaunchDirY, packet.LaunchDirZ);
                
                // CRITICAL: Make sure missile is active and visible!
                missileObj.SetActive(true);
                
                // Reset scale (pylon missiles may be scaled down)
                missileObj.transform.localScale = Vector3.one;
                
                // Detach from any parent (in case cloned from pylon)
                missileObj.transform.SetParent(null);
                
                // Enable all renderers and reset their layers
                int rendererCount = 0;
                foreach (var renderer in missileObj.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.enabled = true;
                    renderer.gameObject.layer = 0; // Default layer
                    rendererCount++;
                }
                
                // Enable all particle systems (smoke trails, flames)
                int particleCount = 0;
                foreach (var ps in missileObj.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ps.gameObject.SetActive(true);
                    ps.gameObject.layer = 0;
                    // Clear and restart the particle system
                    ps.Clear();
                    ps.Play();
                    particleCount++;
                }
                
                // Enable all child GameObjects (some missiles have nested visual objects)
                foreach (Transform child in missileObj.GetComponentsInChildren<Transform>(true))
                {
                    child.gameObject.SetActive(true);
                    child.gameObject.layer = 0;
                }
                
                // Log the missile's actual world position for debugging
                Plugin.Log?.LogInfo($"[RealCombatSync] Missile has {rendererCount} renderers, {particleCount} particle systems, scale={missileObj.transform.localScale}");
                
                // Get the Munition component
                var munition = missileObj.GetComponent(_munitionType);
                if (munition == null)
                {
                    Plugin.Log?.LogWarning("[RealCombatSync] No Munition component on spawned missile");
                    UnityEngine.Object.Destroy(missileObj);
                    return;
                }
                
                // Set the target to local player BEFORE Launch
                if (_munitionTargetField != null)
                {
                    _munitionTargetField.SetValue(munition, localTarget);
                }
                
                // Create and configure seeker ONLY for guided missiles AND if we have a target
                // SeekerType: 0 = IR, 1 = Radar, 2 = Unguided
                if ((packet.SeekerType == 0 || packet.SeekerType == 1) && localTarget != null)
                {
                    ConfigureOrCreateMissileSeeker(munition, localTarget, packet.SeekerType);
                }
                else
                {
                    Plugin.Log?.LogInfo($"[RealCombatSync] Missile type {packet.SeekerType}, target={localTarget != null} - flying unguided");
                }
                
                // CRITICAL: Call Munition.Launch() to properly initialize the missile!
                // This starts the motor effects (flames/smoke trails) and sets IsLaunched = true
                var launchDir = new Vector3(packet.LaunchDirX, packet.LaunchDirY, packet.LaunchDirZ);
                Vector3 inheritedVelocity = launchDir * 300f; // Approximate missile launch velocity
                
                var launchMethod = _munitionType.GetMethod("Launch", BindingFlags.Public | BindingFlags.Instance);
                if (launchMethod != null)
                {
                    try
                    {
                        // Launch() will add to LaunchedMissiles, so don't add manually
                        launchMethod.Invoke(munition, new object[] { inheritedVelocity });
                        Plugin.Log?.LogInfo("[RealCombatSync] Called Munition.Launch() - motor effects should start!");
                    }
                    catch (Exception launchEx)
                    {
                        Plugin.Log?.LogWarning($"[RealCombatSync] Launch() failed: {launchEx.Message}");
                        // Fallback: manually add to LaunchedMissiles
                        launchedMissiles.Add(munition);
                    }
                }
                else
                {
                    // Fallback: manually add to LaunchedMissiles
                    launchedMissiles.Add(munition);
                    Plugin.Log?.LogWarning("[RealCombatSync] Launch method not found, added manually");
                }
                
                _networkMissiles.Add(munition);
                
                // Disable the Munition component so it doesn't move on its own
                // We'll control movement via NetworkMissileController instead
                var munitionBehaviour = munition as Behaviour;
                if (munitionBehaviour != null)
                {
                    munitionBehaviour.enabled = false;
                    Plugin.Log?.LogInfo("[RealCombatSync] Disabled Munition component - NetworkMissileController will handle movement");
                }
                
                // Add a controller to handle movement and cleanup
                var controller = missileObj.AddComponent<NetworkMissileController>();
                controller.Initialize(localTarget as Component, packet);
                
                Plugin.Log?.LogInfo($"[RealCombatSync] Created missile at {localLaunchPos}, heading {launchDir}, renderers={rendererCount}");
                Plugin.Log?.LogInfo($"[RealCombatSync] Missile launched with motor effects - ThreatWarning should detect!");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[RealCombatSync] AddNetworkMissile error: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Spawn a missile using GameDataStores.SpawnStore()
        /// </summary>
        private static GameObject SpawnMissileFromGameData(string missileType)
        {
            try
            {
                if (_gameDataStoresType == null)
                {
                    _gameDataStoresType = Type.GetType("Falcon.Stores.GameDataStores, Assembly-CSharp");
                }
                if (_gameDataStoresType == null) 
                {
                    Plugin.Log?.LogWarning("[RealCombatSync] GameDataStores type not found");
                    return null;
                }
                
                if (_spawnStoreMethod == null)
                {
                    _spawnStoreMethod = _gameDataStoresType.GetMethod("SpawnStore", 
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                }
                if (_hasStoreMethod == null)
                {
                    _hasStoreMethod = _gameDataStoresType.GetMethod("HasStore", 
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                }
                
                if (_hasStoreMethod == null || _spawnStoreMethod == null)
                {
                    Plugin.Log?.LogWarning("[RealCombatSync] SpawnStore/HasStore methods not found");
                    return null;
                }
                
                // Build list of names to try - ACTUAL GAME NAMES (no hyphens!)
                var namesToTry = new List<string>
                {
                    missileType,           // Original from packet
                    // ACTUAL game store names (from StreamingAssets/Data/Stores/Missiles)
                    "AIM9L", "AIM9C",       // Sidewinders
                    "AIM82B",               // AIM-9 variant
                    "AIM7E", "AIM7F",       // Sparrows
                    "R60",                  // Russian IR
                    "R3S", "R3R",           // Russian older
                    "R23T", "R23R",         // Russian medium range
                    "R24T", "R24R",         // Russian longer range
                    // AGMs (less likely but included)
                    "AGM65D", "AGM64C"
                };
                
                foreach (var name in namesToTry)
                {
                    try
                    {
                        bool hasStore = (bool)_hasStoreMethod.Invoke(null, new object[] { name });
                        if (hasStore)
                        {
                            var store = _spawnStoreMethod.Invoke(null, new object[] { name });
                            if (store is Component comp)
                            {
                                Plugin.Log?.LogInfo($"[RealCombatSync] Spawned missile from GameDataStores: {name} (requested: {missileType})");
                                return comp.gameObject;
                            }
                        }
                    }
                    catch { /* Continue trying other names */ }
                }
                
                Plugin.Log?.LogWarning($"[RealCombatSync] No missile found in GameDataStores for: {missileType} (tried {namesToTry.Count} variants)");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[RealCombatSync] SpawnMissileFromGameData error: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Load missile from Resources as fallback
        /// </summary>
        private static GameObject LoadMissileFromResources(string missileType)
        {
            try
            {
                // FIRST: Try cloning from scene - these have proper visual effects already set up!
                var sceneClone = CloneMunitionFromScene(missileType);
                if (sceneClone != null)
                {
                    return sceneClone;
                }
                
                // FALLBACK: Try to load from Resources - but these are basic prefabs
                var pathsToTry = new List<string>
                {
                    $"Stores/{missileType}",
                    $"Weapons/Missiles/AIM9/{missileType}",
                    "Weapons/Missiles/AIM9/AIM9L",
                    // The game uses "Stores/_Munition" as base prefab (last resort)
                    "Stores/_Munition",
                };
                
                foreach (var path in pathsToTry)
                {
                    var prefab = Resources.Load<GameObject>(path);
                    if (prefab != null)
                    {
                        var instance = UnityEngine.Object.Instantiate(prefab);
                        Plugin.Log?.LogInfo($"[RealCombatSync] Loaded missile from Resources: {path}");
                        return instance;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[RealCombatSync] LoadMissileFromResources error: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Clone a missile from an existing scene object
        /// </summary>
        private static GameObject CloneMunitionFromScene(string missileType)
        {
            if (_munitionType == null) return null;
            
            try
            {
                // BEST: Try to clone from LaunchedMissiles (these have active VFX)
                if (_launchedMissilesField != null)
                {
                    var launchedMissiles = _launchedMissilesField.GetValue(null) as System.Collections.IList;
                    if (launchedMissiles != null && launchedMissiles.Count > 0)
                    {
                        foreach (var missile in launchedMissiles)
                        {
                            var comp = missile as Component;
                            if (comp == null) continue;
                            if (comp.gameObject.name.StartsWith("MP_")) continue;
                            if (_networkMissiles.Contains(missile)) continue;
                            
                            // This is an active missile with effects - clone it!
                            var instance = UnityEngine.Object.Instantiate(comp.gameObject);
                            Plugin.Log?.LogInfo($"[RealCombatSync] Cloned ACTIVE missile with VFX: {comp.gameObject.name}");
                            return instance;
                        }
                    }
                }
                
                // Look for loaded missiles in game (StoresManagement has loaded munitions)
                var munitions = Resources.FindObjectsOfTypeAll(_munitionType);
                Plugin.Log?.LogInfo($"[RealCombatSync] Found {munitions.Length} munitions in scene/resources");
                
                // First pass: find a munition on an aircraft (has proper setup)
                Component bestMatch = null;
                int bestRendererCount = 0;
                
                foreach (var obj in munitions)
                {
                    var comp = obj as Component;
                    if (comp == null) continue;
                    
                    // Skip our own network missiles
                    if (comp.gameObject.name.StartsWith("MP_NetworkMissile")) continue;
                    if (comp.gameObject.name.StartsWith("MP_")) continue;
                    
                    // Skip inactive/destroyed
                    if (comp.gameObject == null) continue;
                    
                    // Count renderers (more is better - means it has model/effects)
                    int rendererCount = comp.GetComponentsInChildren<Renderer>(true).Length;
                    
                    string name = comp.gameObject.name.ToLower();
                    
                    // Prefer missiles with matching names
                    if (name.Contains(missileType.ToLower()) && rendererCount > bestRendererCount)
                    {
                        bestMatch = comp;
                        bestRendererCount = rendererCount;
                    }
                    // Or any missile-like name
                    else if (rendererCount > bestRendererCount && 
                            (name.Contains("aim") || name.Contains("r60") || name.Contains("sidewinder") || name.Contains("missile")))
                    {
                        bestMatch = comp;
                        bestRendererCount = rendererCount;
                    }
                }
                
                if (bestMatch != null)
                {
                    var instance = UnityEngine.Object.Instantiate(bestMatch.gameObject);
                    Plugin.Log?.LogInfo($"[RealCombatSync] Cloned best match missile: {bestMatch.gameObject.name} ({bestRendererCount} renderers)");
                    return instance;
                }
                
                // Fallback: clone any munition with renderers
                foreach (var obj in munitions)
                {
                    var comp = obj as Component;
                    if (comp == null) continue;
                    if (comp.gameObject.name.StartsWith("MP_")) continue;
                    
                    int rendererCount = comp.GetComponentsInChildren<Renderer>(true).Length;
                    if (rendererCount > 1)
                    {
                        var instance = UnityEngine.Object.Instantiate(comp.gameObject);
                        Plugin.Log?.LogInfo($"[RealCombatSync] Cloned fallback munition: {comp.gameObject.name} ({rendererCount} renderers)");
                        return instance;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[RealCombatSync] CloneMunitionFromScene error: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Configure existing seeker or create a new one if needed
        /// </summary>
        private static void ConfigureOrCreateMissileSeeker(object munition, object target, byte seekerType)
        {
            try
            {
                if (_munitionSeekerField == null || _seekerType == null)
                {
                    Plugin.Log?.LogWarning("[RealCombatSync] Seeker reflection not initialized");
                    return;
                }
                
                var seeker = _munitionSeekerField.GetValue(munition);
                
                // If no seeker exists, try to create one
                if (seeker == null)
                {
                    seeker = CreateSeeker(munition, seekerType);
                    if (seeker != null)
                    {
                        _munitionSeekerField.SetValue(munition, seeker);
                        Plugin.Log?.LogInfo("[RealCombatSync] Created new Seeker for missile");
                    }
                    else
                    {
                        Plugin.Log?.LogWarning("[RealCombatSync] Could not create Seeker - missile won't trigger warnings");
                        return;
                    }
                }
                
                // Configure seeker
                if (_seekerTargetField != null)
                {
                    _seekerTargetField.SetValue(seeker, target);
                }
                
                if (_seekerIsTrackingField != null)
                {
                    _seekerIsTrackingField.SetValue(seeker, true);
                }
                
                if (_seekerIsSpoofedField != null)
                {
                    _seekerIsSpoofedField.SetValue(seeker, false);
                }
                
                Plugin.Log?.LogInfo("[RealCombatSync] Configured missile seeker: IsTracking=true, IsSpoofed=false");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[RealCombatSync] ConfigureOrCreateMissileSeeker error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Create a new Seeker instance for a missile that doesn't have one
        /// </summary>
        private static object CreateSeeker(object munition, byte seekerType)
        {
            try
            {
                // Get GuidanceProperties type
                if (_guidancePropertiesType == null)
                {
                    _guidancePropertiesType = Type.GetType("Falcon.Stores.GuidanceProperties, Assembly-CSharp");
                }
                
                if (_guidancePropertiesType == null || _seekerType == null)
                {
                    return null;
                }
                
                // Get Seeker constructor: Seeker(GuidanceProperties, Transform)
                if (_seekerConstructor == null)
                {
                    _seekerConstructor = _seekerType.GetConstructor(new[] { _guidancePropertiesType, typeof(Transform) });
                }
                
                if (_seekerConstructor == null)
                {
                    Plugin.Log?.LogWarning("[RealCombatSync] Could not find Seeker constructor");
                    return null;
                }
                
                // Create GuidanceProperties
                var guidanceProps = Activator.CreateInstance(_guidancePropertiesType);
                
                // Set GuideType based on seeker type
                var guideTypeField = _guidancePropertiesType.GetField("GuideType", BindingFlags.Public | BindingFlags.Instance);
                if (guideTypeField != null)
                {
                    // GuidanceType: 0=None, 1=Infrared, 2=ActiveRadar, 3=SemiActiveRadar, etc.
                    guideTypeField.SetValue(guidanceProps, (int)seekerType);
                }
                
                // Set reasonable defaults
                SetFieldIfExists(_guidancePropertiesType, guidanceProps, "SeekerFOV", 90f);
                SetFieldIfExists(_guidancePropertiesType, guidanceProps, "MaxRange", 10000f);
                SetFieldIfExists(_guidancePropertiesType, guidanceProps, "EffectiveRange", 5000f);
                
                // Get munition's transform
                var munitionComp = munition as Component;
                Transform transform = munitionComp?.transform;
                
                // Create the Seeker
                var seeker = _seekerConstructor.Invoke(new object[] { guidanceProps, transform });
                
                return seeker;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[RealCombatSync] CreateSeeker error: {ex.Message}");
                return null;
            }
        }
        
        private static void SetFieldIfExists(Type type, object obj, string fieldName, object value)
        {
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(obj, value);
            }
        }
        
        /// <summary>
        /// Remove a network missile (when it explodes or times out)
        /// </summary>
        public static void RemoveNetworkMissile(object munition)
        {
            try
            {
                if (_launchedMissilesField == null) return;
                
                var launchedMissiles = _launchedMissilesField.GetValue(null) as System.Collections.IList;
                if (launchedMissiles != null && launchedMissiles.Contains(munition))
                {
                    launchedMissiles.Remove(munition);
                    _networkMissiles.Remove(munition);
                    Plugin.Log?.LogInfo("[RealCombatSync] Removed network missile from LaunchedMissiles");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[RealCombatSync] RemoveNetworkMissile error: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Bullet Sync - Using Game's Gun2 System
        
        // FireControl reflection
        private static Type _fireControlType;
        private static FieldInfo _fireControlGunField;
        
        // Gun2 reflection (Gun2 is a plain C# class, NOT a MonoBehaviour!)
        private static Type _gun2Type;
        private static FieldInfo _gun2IsFiringField; // IsFiring is a public FIELD, not property
        private static MethodInfo _gun2UpdateMethod;
        
        /// <summary>
        /// Set the firing state on a remote aircraft's Gun2
        /// This uses the REAL game gun system - same as AI enemies use
        /// The FireControl.Update patch will read this state and fire bullets
        /// </summary>
        public static void SetRemoteGunFiring(GameObject remoteAircraft, bool isFiring)
        {
            Initialize();
            
            try
            {
                // Gun2 is NOT a MonoBehaviour - it's a plain C# class held in FireControl.Gun field
                // We MUST get it via FireControl, not GetComponent!
                if (_fireControlType == null)
                {
                    _fireControlType = Type.GetType("Falcon.Vehicles.FireControl, Assembly-CSharp");
                }
                if (_fireControlType == null)
                {
                    if (isFiring && LogHelper.ShouldLogInterval("NoFireControlType", 10f))
                        Plugin.Log?.LogWarning("[RealCombatSync] FireControl type not found");
                    return;
                }
                
                // FireControl IS a MonoBehaviour - find it on the aircraft
                var fireControlComp = remoteAircraft.GetComponentInChildren(_fireControlType, true) as Component;
                if (fireControlComp == null)
                {
                    if (isFiring && LogHelper.ShouldLogInterval("NoFireControl", 5f))
                        Plugin.Log?.LogWarning($"[RealCombatSync] No FireControl on {remoteAircraft.name}");
                    return;
                }
                
                // Get the Gun2 object from FireControl.Gun field
                if (_fireControlGunField == null)
                {
                    _fireControlGunField = _fireControlType.GetField("Gun", 
                        BindingFlags.Public | BindingFlags.Instance);
                }
                if (_fireControlGunField == null)
                {
                    if (isFiring && LogHelper.ShouldLogInterval("NoGunField", 10f))
                        Plugin.Log?.LogWarning("[RealCombatSync] Gun field not found on FireControl");
                    return;
                }
                
                var gun = _fireControlGunField.GetValue(fireControlComp);
                if (gun == null)
                {
                    // Gun2 is a plain C# class that needs to be constructed
                    // On cloned aircraft, FireControl.Gun is null because Gun2 was never created
                    // FALLBACK: Fire bullets directly via Bullet2Manager
                    if (isFiring)
                    {
                        FireBulletsDirectly(remoteAircraft);
                    }
                    return;
                }
                
                // Gun2 has a public field "IsFiring" (not property!) and method "Update(double, float)"
                if (_gun2Type == null)
                {
                    _gun2Type = gun.GetType();
                }
                if (_gun2IsFiringField == null)
                {
                    _gun2IsFiringField = _gun2Type.GetField("IsFiring", BindingFlags.Public | BindingFlags.Instance);
                }
                if (_gun2UpdateMethod == null)
                {
                    _gun2UpdateMethod = _gun2Type.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance);
                }
                
                // Set IsFiring and call Update to fire bullets
                if (_gun2IsFiringField != null)
                {
                    _gun2IsFiringField.SetValue(gun, isFiring);
                }
                if (isFiring && _gun2UpdateMethod != null)
                {
                    _gun2UpdateMethod.Invoke(gun, new object[] { Time.timeAsDouble, Time.deltaTime });
                }
                
                // Also register with RemoteAircraftRegistry for FireControlPatches
                Patches.RemoteAircraftRegistry.SetRemoteGunFiring(gun, isFiring);
                
                // Log state changes
                if (isFiring && LogHelper.ShouldLogInterval("RemoteGunFiring", 2f))
                {
                    Plugin.Log?.LogInfo($"[RealCombatSync] Remote gun FIRING on {remoteAircraft.name}");
                }
            }
            catch (Exception ex)
            {
                // Only log occasionally to avoid spam
                if (LogHelper.ShouldLogInterval("RealCombatSync.SetRemoteGunFiring", 5f))
                {
                    Plugin.Log?.LogWarning($"[RealCombatSync] SetRemoteGunFiring error: {ex.Message}");
                }
            }
        }
        
        // Direct bullet firing state (cached to reduce per-frame reflection overhead)
        private static object _cachedBulletData;
        private static object _cachedBullet2ManagerInstance;
        private static FieldInfo _bullet2ManagerInstanceField;
        private static double _lastBulletFireTime;
        private const double BULLET_FIRE_INTERVAL = 0.1; // 10 rounds per second (reduced from 20 to prevent lag)
        
        // Cache for remote firing helpers
        private static readonly Dictionary<int, Component> _cachedRemoteTargets = new Dictionary<int, Component>();
        private static readonly Dictionary<int, Transform> _cachedRemoteFirePoints = new Dictionary<int, Transform>();
        
        // FireBullet overload resolution
        private static bool _fireBulletMethodResolved = false;
        private static FireBulletArgKind[] _fireBulletArgMap;
        private static bool _loggedFireBulletSignatures = false;
        
        private enum FireBulletArgKind
        {
            Target,
            BulletData,
            WeaponName,
            Position,
            Velocity,
            Direction,
            Rotation,
            FirePoint,
            RemoteAircraft,
            RemoteRigidbody,
            Gun,
            Barrel,
            MuzzleVelocity,
            FloatZero,
            DoubleZero,
            BoolTrue,
            BoolFalse,
            EnumDefault,
            EmptyEnumerable,
            Null
        }
        
        private static object TryGetBullet2ManagerInstance()
        {
            if (_cachedBullet2ManagerInstance != null) return _cachedBullet2ManagerInstance;
            
            if (_bullet2ManagerType == null)
            {
                _bullet2ManagerType = Type.GetType("Falcon.Weapons.Bullet2Manager, Assembly-CSharp");
            }
            if (_bullet2ManagerType == null) return null;
            
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            if (_bullet2ManagerInstanceProp == null)
            {
                _bullet2ManagerInstanceProp = _bullet2ManagerType.GetProperty("Instance", flags);
            }
            if (_bullet2ManagerInstanceField == null)
            {
                _bullet2ManagerInstanceField = _bullet2ManagerType.GetField("Instance", flags);
            }
            
            if (_bullet2ManagerInstanceProp != null)
            {
                _cachedBullet2ManagerInstance = _bullet2ManagerInstanceProp.GetValue(null);
            }
            else if (_bullet2ManagerInstanceField != null)
            {
                _cachedBullet2ManagerInstance = _bullet2ManagerInstanceField.GetValue(null);
            }
            
            if (_cachedBullet2ManagerInstance != null)
            {
                Plugin.Log?.LogInfo("[RealCombatSync] Cached Bullet2Manager instance");
            }
            
            return _cachedBullet2ManagerInstance;
        }
        
        private static bool ResolveFireBulletMethod()
        {
            if (_fireBulletMethodResolved) return _bullet2ManagerFireBulletMethod != null;
            _fireBulletMethodResolved = true;
            
            if (_bullet2ManagerType == null) return false;
            
            var methods = _bullet2ManagerType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            foreach (var method in methods)
            {
                if (!string.Equals(method.Name, "FireBullet", StringComparison.Ordinal)) continue;
                
                if (TryBuildFireBulletArgMap(method, out var argMap))
                {
                    _bullet2ManagerFireBulletMethod = method;
                    _fireBulletArgMap = argMap;
                    LogFireBulletSignature(method, argMap);
                    return true;
                }
            }
            
            LogAvailableFireBulletSignatures(methods);
            return false;
        }
        
        private static bool TryBuildFireBulletArgMap(MethodInfo method, out FireBulletArgKind[] argMap)
        {
            var parameters = method.GetParameters();
            argMap = new FireBulletArgKind[parameters.Length];
            int vector3Count = 0;
            
            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var paramType = param.ParameterType;
                var name = param.Name?.ToLowerInvariant() ?? string.Empty;
                
                if (_targetType != null && paramType.IsAssignableFrom(_targetType))
                {
                    argMap[i] = FireBulletArgKind.Target;
                }
                else if (_bulletDataType != null && paramType.IsAssignableFrom(_bulletDataType))
                {
                    argMap[i] = FireBulletArgKind.BulletData;
                }
                else if (paramType == typeof(string))
                {
                    argMap[i] = FireBulletArgKind.WeaponName;
                }
                else if (TryGetEnumerableElementType(paramType, out _))
                {
                    argMap[i] = FireBulletArgKind.EmptyEnumerable;
                }
                else if (paramType == typeof(Vector3))
                {
                    if (name.Contains("vel") || name.Contains("speed"))
                    {
                        argMap[i] = FireBulletArgKind.Velocity;
                    }
                    else if (name.Contains("dir") || name.Contains("forward"))
                    {
                        argMap[i] = FireBulletArgKind.Direction;
                    }
                    else
                    {
                        argMap[i] = vector3Count == 0 ? FireBulletArgKind.Position : FireBulletArgKind.Velocity;
                    }
                    vector3Count++;
                }
                else if (paramType == typeof(Quaternion))
                {
                    argMap[i] = FireBulletArgKind.Rotation;
                }
                else if (paramType == typeof(Transform))
                {
                    argMap[i] = FireBulletArgKind.FirePoint;
                }
                else if (paramType == typeof(GameObject))
                {
                    argMap[i] = FireBulletArgKind.RemoteAircraft;
                }
                else if (paramType == typeof(Rigidbody))
                {
                    argMap[i] = FireBulletArgKind.RemoteRigidbody;
                }
                else if (paramType == typeof(float))
                {
                    if (name.Contains("vel") || name.Contains("speed") || name.Contains("muzzle"))
                        argMap[i] = FireBulletArgKind.MuzzleVelocity;
                    else
                        argMap[i] = FireBulletArgKind.FloatZero;
                }
                else if (paramType == typeof(double))
                {
                    if (name.Contains("vel") || name.Contains("speed") || name.Contains("muzzle"))
                        argMap[i] = FireBulletArgKind.MuzzleVelocity;
                    else
                        argMap[i] = FireBulletArgKind.DoubleZero;
                }
                else if (paramType == typeof(bool))
                {
                    argMap[i] = FireBulletArgKind.BoolFalse;
                }
                else if (paramType.IsEnum)
                {
                    argMap[i] = FireBulletArgKind.EnumDefault;
                }
                else if (paramType.Name.IndexOf("Gun", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    argMap[i] = FireBulletArgKind.Gun;
                }
                else if (paramType.Name.IndexOf("Barrel", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    argMap[i] = FireBulletArgKind.Barrel;
                }
                else if (typeof(Delegate).IsAssignableFrom(paramType))
                {
                    argMap[i] = FireBulletArgKind.Null;
                }
                else if (!paramType.IsValueType)
                {
                    argMap[i] = FireBulletArgKind.Null;
                }
                else
                {
                    return false; // Unhandled value type
                }
            }
            
            return true;
        }
        
        private static object[] BuildFireBulletArgs(
            GameObject remoteAircraft,
            Component target,
            object bulletData,
            Transform firePoint,
            Vector3 firePos,
            Vector3 fireDir,
            Vector3 bulletVelocity,
            Rigidbody rb,
            object gun,
            object barrel,
            float muzzleVelocity)
        {
            var parameters = _bullet2ManagerFireBulletMethod.GetParameters();
            var args = new object[parameters.Length];
            
            for (int i = 0; i < parameters.Length; i++)
            {
                switch (_fireBulletArgMap[i])
                {
                    case FireBulletArgKind.Target:
                        args[i] = target;
                        break;
                    case FireBulletArgKind.BulletData:
                        args[i] = bulletData;
                        break;
                    case FireBulletArgKind.WeaponName:
                        args[i] = "Remote Gun";
                        break;
                    case FireBulletArgKind.Position:
                        args[i] = firePos;
                        break;
                    case FireBulletArgKind.Velocity:
                        args[i] = bulletVelocity;
                        break;
                    case FireBulletArgKind.Direction:
                        args[i] = fireDir;
                        break;
                    case FireBulletArgKind.Rotation:
                        args[i] = firePoint != null ? firePoint.rotation : remoteAircraft.transform.rotation;
                        break;
                    case FireBulletArgKind.FirePoint:
                        args[i] = firePoint;
                        break;
                    case FireBulletArgKind.RemoteAircraft:
                        args[i] = remoteAircraft;
                        break;
                    case FireBulletArgKind.RemoteRigidbody:
                        args[i] = rb;
                        break;
                    case FireBulletArgKind.Gun:
                        args[i] = gun;
                        break;
                    case FireBulletArgKind.Barrel:
                        args[i] = barrel;
                        break;
                    case FireBulletArgKind.MuzzleVelocity:
                        args[i] = parameters[i].ParameterType == typeof(double)
                            ? (object)(double)muzzleVelocity
                            : muzzleVelocity;
                        break;
                    case FireBulletArgKind.FloatZero:
                        args[i] = 0f;
                        break;
                    case FireBulletArgKind.DoubleZero:
                        args[i] = 0d;
                        break;
                    case FireBulletArgKind.BoolTrue:
                        args[i] = true;
                        break;
                    case FireBulletArgKind.BoolFalse:
                        args[i] = false;
                        break;
                    case FireBulletArgKind.EnumDefault:
                        args[i] = Enum.ToObject(parameters[i].ParameterType, 0);
                        break;
                    case FireBulletArgKind.EmptyEnumerable:
                        if (TryGetEnumerableElementType(parameters[i].ParameterType, out var elementType) && elementType != null)
                        {
                            args[i] = Array.CreateInstance(elementType, 0);
                        }
                        else
                        {
                            args[i] = Array.Empty<object>();
                        }
                        break;
                    case FireBulletArgKind.Null:
                        args[i] = null;
                        break;
                }
            }
            
            return args;
        }
        
        private static bool TryGetEnumerableElementType(Type paramType, out Type elementType)
        {
            elementType = null;
            if (paramType == null || paramType == typeof(string)) return false;
            
            if (paramType.IsArray)
            {
                elementType = paramType.GetElementType();
                return elementType != null;
            }
            
            if (!paramType.IsInterface) return false;
            
            if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = paramType.GetGenericArguments()[0];
                return true;
            }
            
            foreach (var iface in paramType.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    elementType = iface.GetGenericArguments()[0];
                    return true;
                }
            }
            
            return false;
        }
        
        private static Component GetRemoteTarget(GameObject remoteAircraft)
        {
            if (remoteAircraft == null || _targetType == null) return null;
            
            int id = remoteAircraft.GetInstanceID();
            if (_cachedRemoteTargets.TryGetValue(id, out var cached) && cached != null)
            {
                return cached;
            }
            
            Component target = null;
            var targets = remoteAircraft.GetComponentsInChildren(_targetType, true);
            if (targets != null && targets.Length > 0)
            {
                target = targets[0] as Component;
            }
            
            if (target != null)
            {
                _cachedRemoteTargets[id] = target;
            }
            
            return target;
        }
        
        private static Transform GetRemoteFirePoint(GameObject remoteAircraft)
        {
            if (remoteAircraft == null) return null;
            
            int id = remoteAircraft.GetInstanceID();
            if (_cachedRemoteFirePoints.TryGetValue(id, out var cached) && cached != null)
            {
                return cached;
            }
            
            Transform firePoint = null;
            
            // Try common gun hierarchy
            var gunTransform = remoteAircraft.transform.Find("Gun");
            if (gunTransform != null)
            {
                firePoint = gunTransform.Find("FirePoint") ?? gunTransform.Find("Muzzle") ?? gunTransform;
            }
            
            // Fallback: search for any muzzle/firepoint in children
            if (firePoint == null)
            {
                var allTransforms = remoteAircraft.GetComponentsInChildren<Transform>(true);
                foreach (var t in allTransforms)
                {
                    var n = t.name.ToLowerInvariant();
                    if (n.Contains("muzzle") || n.Contains("firepoint") || n.Contains("gun"))
                    {
                        firePoint = t;
                        break;
                    }
                }
            }
            
            firePoint = firePoint ?? remoteAircraft.transform;
            _cachedRemoteFirePoints[id] = firePoint;
            return firePoint;
        }
        
        private static object TryGetBarrelFromGun(object gun)
        {
            if (gun == null) return null;
            
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var gunType = gun.GetType();
            
            foreach (var field in gunType.GetFields(flags))
            {
                if (!field.Name.ToLowerInvariant().Contains("barrel")) continue;
                var value = field.GetValue(gun);
                var barrel = TryGetFirstEnumerableValue(value);
                return barrel ?? value;
            }
            
            foreach (var prop in gunType.GetProperties(flags))
            {
                if (!prop.Name.ToLowerInvariant().Contains("barrel")) continue;
                var value = prop.GetValue(gun, null);
                var barrel = TryGetFirstEnumerableValue(value);
                return barrel ?? value;
            }
            
            return null;
        }
        
        private static object TryGetFirstEnumerableValue(object value)
        {
            if (value == null) return null;
            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    return item;
                }
            }
            return null;
        }
        
        private static void LogFireBulletSignature(MethodInfo method, FireBulletArgKind[] argMap)
        {
            if (_loggedFireBulletSignatures) return;
            
            var parameters = method.GetParameters();
            var parts = new List<string>();
            for (int i = 0; i < parameters.Length; i++)
            {
                parts.Add($"{parameters[i].ParameterType.Name} {parameters[i].Name}=>{argMap[i]}");
            }
            
            Plugin.Log?.LogInfo($"[RealCombatSync] Using FireBullet overload: {string.Join(", ", parts)}");
            _loggedFireBulletSignatures = true;
        }
        
        private static void LogAvailableFireBulletSignatures(MethodInfo[] methods)
        {
            if (_loggedFireBulletSignatures) return;
            
            foreach (var method in methods)
            {
                if (!string.Equals(method.Name, "FireBullet", StringComparison.Ordinal)) continue;
                
                var parameters = method.GetParameters();
                var parts = new List<string>();
                foreach (var param in parameters)
                {
                    parts.Add($"{param.ParameterType.Name} {param.Name}");
                }
                
                Plugin.Log?.LogWarning($"[RealCombatSync] FireBullet overload: {string.Join(", ", parts)}");
            }
            
            _loggedFireBulletSignatures = true;
        }
        
        /// <summary>
        /// Public wrapper to fire bullets directly - called from FireControlPatches when Gun2 is null
        /// </summary>
        public static void FireBulletsDirectlyPublic(GameObject remoteAircraft)
        {
            FireBulletsDirectly(remoteAircraft);
        }
        
        /// <summary>
        /// Fire bullets directly using Bullet2Manager when Gun2 is null
        /// </summary>
        private static void FireBulletsDirectly(GameObject remoteAircraft)
        {
            try
            {
                // Throttle bullet firing - check FIRST to skip all work
                double currentTime = Time.timeAsDouble;
                if (currentTime - _lastBulletFireTime < BULLET_FIRE_INTERVAL)
                    return;
                _lastBulletFireTime = currentTime;
                
                if (remoteAircraft == null) return;
                
                // Cache Bullet2Manager instance (only look up once)
                var bulletManager = TryGetBullet2ManagerInstance();
                if (bulletManager == null) return;
                
                // Cache BulletData (only look up once)
                if (_cachedBulletData == null)
                {
                    var gameDataBulletsType = Type.GetType("Falcon.GameDataBullets, Assembly-CSharp");
                    if (gameDataBulletsType != null)
                    {
                        var getBulletByName = gameDataBulletsType.GetMethod("GetByName", 
                            BindingFlags.Public | BindingFlags.Static);
                        if (getBulletByName != null)
                        {
                            // Use actual game bullet names from Weapons/Bullets/Aircraft.json
                            string[] bulletNames = { "20mm M61", "25mm GAU12 HE", "23mm Gsh23", "25mm GAU12 AP" };
                            foreach (var name in bulletNames)
                            {
                                _cachedBulletData = getBulletByName.Invoke(null, new object[] { name });
                                if (_cachedBulletData != null)
                                {
                                    Plugin.Log?.LogInfo($"[RealCombatSync] Cached bullet data: {name}");
                                    break;
                                }
                            }
                        }
                    }
                }
                
                if (_cachedBulletData == null) return;
                
                // Ensure type caches for overload resolution
                if (_bulletDataType == null)
                {
                    _bulletDataType = Type.GetType("Falcon.Weapons.BulletData, Assembly-CSharp");
                }
                if (_targetType == null)
                {
                    _targetType = Type.GetType("Falcon.Targeting.Target, Assembly-CSharp");
                }
                
                // Resolve FireBullet overload and argument mapping
                if (!ResolveFireBulletMethod()) return;
                
                // Get Target component from aircraft (needed for bullet ownership)
                var target = GetRemoteTarget(remoteAircraft);
                if (target == null)
                {
                    target = FindLocalPlayerTarget() as Component;
                    if (target == null)
                    {
                        if (LogHelper.ShouldLogInterval("FireBullets.NoTarget", 5f))
                            Plugin.Log?.LogWarning("[RealCombatSync] FireBulletsDirectly: no Target found");
                        return;
                    }
                    
                    if (LogHelper.ShouldLogInterval("FireBullets.TargetFallback", 10f))
                        Plugin.Log?.LogInfo("[RealCombatSync] FireBulletsDirectly: using local Target fallback");
                }
                
                // Find gun muzzle position
                Transform firePoint = GetRemoteFirePoint(remoteAircraft) ?? remoteAircraft.transform;
                
                // Calculate bullet velocity
                Vector3 firePos = firePoint.position;
                Vector3 fireDir = firePoint.forward;
                float muzzleVelocity = 1000f;
                
                // Get aircraft velocity
                var rb = remoteAircraft.GetComponent<Rigidbody>();
                Vector3 bulletVelocity = fireDir * muzzleVelocity + (rb != null ? rb.velocity : Vector3.zero);
                
                // Optional gun/barrel references if required by overload
                var gun = GetRemoteGun(remoteAircraft);
                var barrel = TryGetBarrelFromGun(gun);
                
                // Fire the bullet!
                var args = BuildFireBulletArgs(remoteAircraft, target, _cachedBulletData, firePoint, firePos, fireDir, bulletVelocity, rb, gun, barrel, muzzleVelocity);
                _bullet2ManagerFireBulletMethod.Invoke(bulletManager, args);
                
                if (LogHelper.ShouldLogInterval("BulletFired", 2f))
                {
                    Plugin.Log?.LogInfo($"[RealCombatSync] Fired bullet from {remoteAircraft.name}");
                }
            }
            catch (Exception ex)
            {
                if (LogHelper.ShouldLogInterval("FireBulletsError", 5f))
                {
                    var root = ex is TargetInvocationException tie && tie.InnerException != null
                        ? tie.InnerException
                        : ex;
                    Plugin.Log?.LogWarning($"[RealCombatSync] FireBulletsDirectly error: {root.GetType().Name}: {root.Message}");
                }
            }
        }
        
        /// <summary>
        /// Get the Gun2 component from a remote aircraft for direct access
        /// </summary>
        public static object GetRemoteGun(GameObject remoteAircraft)
        {
            Initialize();
            
            try
            {
                if (_fireControlType == null)
                {
                    _fireControlType = Type.GetType("Falcon.Vehicles.FireControl, Assembly-CSharp");
                }
                if (_fireControlType == null) return null;
                
                var fireControl = remoteAircraft.GetComponentInChildren(_fireControlType);
                if (fireControl == null) return null;
                
                if (_fireControlGunField == null)
                {
                    _fireControlGunField = _fireControlType.GetField("Gun", 
                        BindingFlags.Public | BindingFlags.Instance);
                }
                if (_fireControlGunField == null) return null;
                
                return _fireControlGunField.GetValue(fireControl);
            }
            catch
            {
                return null;
            }
        }
        
        #endregion
        
        #region Helpers
        
        private static object FindLocalPlayerTarget()
        {
            if (_targetType == null) return null;
            
            try
            {
                // Fast path: use UniAircraft.Player if available
                var playerAircraft = Falcon.UniversalAircraft.UniAircraft.Player;
                if (playerAircraft != null)
                {
                    // Skip remote clones just in case
                    if (playerAircraft.GetComponent<RemoteAircraftController>() == null)
                    {
                        var target = playerAircraft.GetComponentInChildren(_targetType);
                        if (target != null)
                        {
                            return target;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[RealCombatSync] UniAircraft.Player lookup failed: {ex.Message}");
            }
            
            try
            {
                var aircrafts = UnityEngine.Object.FindObjectsByType<Falcon.UniversalAircraft.UniAircraft>(FindObjectsSortMode.None);
                foreach (var aircraft in aircrafts)
                {
                    // Skip remote aircraft
                    if (aircraft.GetComponent<RemoteAircraftController>() != null) continue;
                    
                    // Get Target component
                    var target = aircraft.GetComponentInChildren(_targetType);
                    if (target != null)
                    {
                        return target;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[RealCombatSync] FindLocalPlayerTarget error: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Clean up all network combat objects
        /// </summary>
        public static void Cleanup()
        {
            try
            {
                // Remove all network missiles from LaunchedMissiles
                if (_launchedMissilesField != null)
                {
                    var launchedMissiles = _launchedMissilesField.GetValue(null) as System.Collections.IList;
                    if (launchedMissiles != null)
                    {
                        foreach (var missile in _networkMissiles)
                        {
                            if (launchedMissiles.Contains(missile))
                            {
                                launchedMissiles.Remove(missile);
                            }
                        }
                    }
                }
                
                _networkMissiles.Clear();
                _remoteRadars.Clear();
                _cachedRemoteTargets.Clear();
                _cachedRemoteFirePoints.Clear();
                _fireBulletMethodResolved = false;
                _fireBulletArgMap = null;
                _loggedFireBulletSignatures = false;
                
                Plugin.Log?.LogInfo("[RealCombatSync] Cleanup complete");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[RealCombatSync] Cleanup error: {ex.Message}");
            }
        }
        
        #endregion
    }
    
    /// <summary>
    /// Controller for network missiles - handles movement and cleanup
    /// Does NOT immediately explode - maintains missile tracking for threat warnings
    /// </summary>
    public class NetworkMissileController : MonoBehaviour
    {
        private Component _target;
        private MissileLaunchPacket _packet;
        private float _spawnTime;
        private float _speed = 350f;       // Typical missile speed
        private float _turnRate = 45f;     // Degrees per second
        private float _boostTime = 0f;     // Track motor burn
        
        private const float LIFETIME = 30f;           // Max lifetime
        private const float HIT_DISTANCE = 25f;       // Proximity for hit
        private const float MIN_FLIGHT_TIME = 2.0f;   // Minimum time before hit check
        private const float BOOST_SPEED = 600f;       // Speed during boost
        
        private bool _hasLoggedTracking = false;
        
        public void Initialize(Component target, MissileLaunchPacket packet)
        {
            _target = target;
            _packet = packet;
            _spawnTime = Time.time;
            
            // Set initial speed based on seeker type (IR missiles accelerate faster)
            _speed = packet.SeekerType == 0 ? 400f : 300f;
            
            // Disable physics on the missile - we control movement
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.detectCollisions = false;
            }
            
            // Disable colliders - we handle collision manually
            foreach (var col in GetComponentsInChildren<Collider>())
            {
                col.enabled = false;
            }
            
            Plugin.Log?.LogInfo($"[NetworkMissileController] Initialized missile targeting {target?.name ?? "null"}, speed={_speed}");
        }
        
        private void Update()
        {
            float flightTime = Time.time - _spawnTime;
            
            // Check timeout
            if (flightTime > LIFETIME)
            {
                Plugin.Log?.LogInfo("[NetworkMissileController] Missile timed out");
                Explode();
                return;
            }
            
            // Update speed (boost phase for first 3 seconds)
            if (flightTime < 3f)
            {
                _speed = Mathf.Lerp(200f, BOOST_SPEED, flightTime / 3f);
            }
            else
            {
                // Gradual slowdown after boost
                _speed = Mathf.Lerp(BOOST_SPEED, 300f, (flightTime - 3f) / 10f);
            }
            
            // Move toward target
            if (_target != null)
            {
                Vector3 toTarget = _target.transform.position - transform.position;
                float distance = toTarget.magnitude;
                
                // Log tracking status periodically
                if (!_hasLoggedTracking && flightTime > 0.5f)
                {
                    _hasLoggedTracking = true;
                    Plugin.Log?.LogInfo($"[NetworkMissileController] Tracking target at distance {distance:F0}m, pos={transform.position}");
                }
                
                // Log position every second for debugging
                if (flightTime > 1f && LogHelper.ShouldLogInterval("NetworkMissile.Position", 1f))
                {
                    Plugin.Log?.LogInfo($"[NetworkMissileController] Missile pos={transform.position}, dist={distance:F0}m, speed={_speed:F0}m/s");
                }
                
                // Only check for hit after minimum flight time to prevent immediate explosion
                if (flightTime > MIN_FLIGHT_TIME && distance < HIT_DISTANCE)
                {
                    Plugin.Log?.LogInfo($"[NetworkMissileController] Hit target at distance {distance:F1}m");
                    Explode();
                    return;
                }
                
                // Turn toward target with proportional navigation
                if (distance > 1f)
                {
                    Vector3 targetDir = toTarget.normalized;
                    
                    // Lead the target slightly based on velocity
                    var targetRb = _target.GetComponent<Rigidbody>();
                    if (targetRb != null)
                    {
                        float timeToIntercept = distance / _speed;
                        Vector3 leadPos = _target.transform.position + targetRb.velocity * timeToIntercept * 0.5f;
                        targetDir = (leadPos - transform.position).normalized;
                    }
                    
                    // Smooth turn toward target
                    Vector3 newForward = Vector3.RotateTowards(
                        transform.forward, 
                        targetDir, 
                        _turnRate * Mathf.Deg2Rad * Time.deltaTime, 
                        0f);
                    transform.forward = newForward;
                }
            }
            else
            {
                // Lost target - fly straight and self-destruct after a bit
                if (flightTime > 5f)
                {
                    Plugin.Log?.LogInfo("[NetworkMissileController] Lost target, self-destructing");
                    Explode();
                    return;
                }
            }
            
            // Move forward
            transform.position += transform.forward * _speed * Time.deltaTime;
        }
        
        private void Explode()
        {
            // Remove from LaunchedMissiles FIRST (prevents ThreatWarning from seeing destroyed missile)
            var munition = GetComponent("Munition");
            if (munition != null)
            {
                RealCombatSync.RemoveNetworkMissile(munition);
            }
            
            // Spawn explosion effect at current position
            CombatVfxManager.SpawnExplosion(transform.position, 15f, 150);
            
            Plugin.Log?.LogInfo("[NetworkMissileController] Missile exploded");
            
            Destroy(gameObject);
        }
        
        private void OnDestroy()
        {
            // Ensure removal from LaunchedMissiles list
            var munition = GetComponent("Munition");
            if (munition != null)
            {
                RealCombatSync.RemoveNetworkMissile(munition);
            }
        }
    }
}
