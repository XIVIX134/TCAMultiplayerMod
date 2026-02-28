using HarmonyLib;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;
using TCAMultiplayer.Player;
using TCAMultiplayer;
using Falcon.Vehicles;
using Falcon.Weapons;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Tracks which GameObjects are remote player aircraft
    /// </summary>
    public static class RemoteAircraftRegistry
    {
        private static readonly HashSet<int> _remoteAircraftIds = new HashSet<int>();
        
        // Track remote guns that are firing
        private static readonly HashSet<object> _remoteFiringGuns = new HashSet<object>();
        
        public static void RegisterRemote(GameObject go)
        {
            if (go != null)
            {
                _remoteAircraftIds.Add(go.GetInstanceID());
                LogHelper.Info(LogCategory.Player, $"[RemoteAircraftRegistry] Registered remote aircraft: {go.name}");
            }
        }
        
        public static void UnregisterRemote(GameObject go)
        {
            if (go != null)
            {
                _remoteAircraftIds.Remove(go.GetInstanceID());
                LogHelper.Info(LogCategory.Player, $"[RemoteAircraftRegistry] Unregistered remote aircraft: {go.name}");
            }
        }
        
        public static bool IsRemote(GameObject go)
        {
            return go != null && _remoteAircraftIds.Contains(go.GetInstanceID());
        }
        
        public static void Clear()
        {
            _remoteAircraftIds.Clear();
            _remoteFiringGuns.Clear();
            LogHelper.Info(LogCategory.Player, "[RemoteAircraftRegistry] Cleared remote aircraft registry");
        }
        
        public static void SetRemoteGunFiring(object gun, bool isFiring)
        {
            if (gun == null) return;
            
            if (isFiring)
                _remoteFiringGuns.Add(gun);
            else
                _remoteFiringGuns.Remove(gun);
        }
        
        public static bool IsRemoteGunFiring(object gun)
        {
            return gun != null && _remoteFiringGuns.Contains(gun);
        }
    }

    /// <summary>
    /// Patches for FireControl to properly handle remote aircraft gun firing
    /// The game normally reads input from WeaponInput - we bypass this for remote aircraft
    /// and use the network-synced firing state instead
    /// </summary>
    [HarmonyPatch(typeof(FireControl))]
    [HarmonyPatch("Update")]
    public static class FireControlPatches
    {
        static FireControlPatches()
        {
            Plugin.Log?.LogInfo("[FireControlPatches] PATCH CLASS LOADED - FireControl.Update will be patched");
        }
        // Track which FireControls we've tried to initialize
        private static readonly HashSet<int> _attemptedInit = new HashSet<int>();
        private static MethodInfo _fireControlStartMethod = null;
        private static MethodInfo _fireControlAwakeMethod = null;
        private static bool _reflectionCached = false;
        
        // Track previous firing state per instance to log only on state changes
        private static readonly Dictionary<int, bool> _prevFiringState = new Dictionary<int, bool>();
        // Track which instances we've already logged ammo diagnostics for (avoid spam)
        private static readonly HashSet<int> _loggedAmmoConsume = new HashSet<int>();
        private static readonly HashSet<int> _loggedNoAmmoConsume = new HashSet<int>();
        
        // Reflection: Target.Velocity has a private setter — we need reflection to override it
        // for correct bullet velocity on remote aircraft
        private static PropertyInfo _targetVelocityProp;
        private static bool _targetVelocityPropInitialized;
        
        /// <summary>
        /// Try to force FireControl to initialize its Gun2 if it's null
        /// </summary>
        private static void TryInitializeGun(FireControl fireControl)
        {
            int instanceId = fireControl.GetInstanceID();
            if (_attemptedInit.Contains(instanceId)) return;
            _attemptedInit.Add(instanceId);
            
            Plugin.Log?.LogInfo($"[FireControlPatches] Attempting to initialize Gun2 on {fireControl.gameObject.name}...");
            
            // Cache reflection
            if (!_reflectionCached)
            {
                _reflectionCached = true;
                var fcType = typeof(FireControl);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                
                _fireControlAwakeMethod = fcType.GetMethod("Awake", flags);
                _fireControlStartMethod = fcType.GetMethod("Start", flags);
                
                // Also look for Init methods
                var initMethod = fcType.GetMethod("Init", flags) ?? fcType.GetMethod("Initialize", flags);
                if (initMethod != null)
                {
                    Plugin.Log?.LogInfo($"[FireControlPatches] Found Init method: {initMethod.Name}");
                }
                
                Plugin.Log?.LogInfo($"[FireControlPatches] Reflection: Awake={_fireControlAwakeMethod != null}, Start={_fireControlStartMethod != null}");
            }
            
            // Try calling Awake via reflection
            if (_fireControlAwakeMethod != null)
            {
                try
                {
                    Plugin.Log?.LogInfo("[FireControlPatches] Calling Awake via reflection...");
                    _fireControlAwakeMethod.Invoke(fireControl, null);
                    
                    if (fireControl.Gun != null)
                    {
                        Plugin.Log?.LogInfo("[FireControlPatches] Gun2 created after Awake!");
                        return;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning($"[FireControlPatches] Awake call failed: {e.Message}");
                }
            }
            
            // Try calling Start via reflection
            if (_fireControlStartMethod != null)
            {
                try
                {
                    Plugin.Log?.LogInfo("[FireControlPatches] Calling Start via reflection...");
                    _fireControlStartMethod.Invoke(fireControl, null);
                    
                    if (fireControl.Gun != null)
                    {
                        Plugin.Log?.LogInfo("[FireControlPatches] Gun2 created after Start!");
                        return;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning($"[FireControlPatches] Start call failed: {e.Message}");
                }
            }
            
            // Try SendMessage as last resort
            try
            {
                Plugin.Log?.LogInfo("[FireControlPatches] Trying SendMessage('Start')...");
                fireControl.SendMessage("Start", SendMessageOptions.DontRequireReceiver);
                
                if (fireControl.Gun != null)
                {
                    Plugin.Log?.LogInfo("[FireControlPatches] Gun2 created after SendMessage!");
                    return;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[FireControlPatches] SendMessage failed: {e.Message}");
            }
            
            Plugin.Log?.LogWarning($"[FireControlPatches] Could not initialize Gun2 on {fireControl.gameObject.name}");
        }
        
        public static void ClearCache()
        {
            _attemptedInit.Clear();
            _prevFiringState.Clear();
            _loggedAmmoConsume.Clear();
            _loggedNoAmmoConsume.Clear();
        }
        
        /// <summary>
        /// Patch Update to use network state for remote aircraft instead of local input.
        /// CRITICAL: For local aircraft, return true IMMEDIATELY with zero side effects.
        /// Any diagnostic logging for local aircraft is done in the postfix instead.
        /// </summary>
        [HarmonyPrefix]
        public static bool Update_Prefix(FireControl __instance)
        {
            // FAST PATH: Check if remote. If NOT remote, return true immediately.
            // This ensures the original FireControl.Update() runs completely unmodified for local aircraft.
            var root = __instance.transform.root.gameObject;
            if (!RemoteAircraftRegistry.IsRemote(root) && !RemoteAircraftRegistry.IsRemote(__instance.gameObject))
            {
                return true; // Let original method run for local aircraft — no interference
            }
            
            // === REMOTE AIRCRAFT HANDLING ===
            // Get firing state from RemoteAircraftController
            var controller = root.GetComponent<RemoteAircraftController>();
            
            bool isNavMode = controller != null && controller.IsNavMode;
            bool isOnGround = controller != null && controller.IsWeightOnWheels;
            bool shouldFire = controller != null && controller.IsFiring && !isNavMode && !isOnGround;
            
            // Log only on state changes to avoid per-frame spam
            int instanceId = __instance.GetInstanceID();
            bool prevFiring = _prevFiringState.ContainsKey(instanceId) && _prevFiringState[instanceId];
            if (shouldFire != prevFiring)
            {
                _prevFiringState[instanceId] = shouldFire;
                if (shouldFire)
                {
                    Plugin.Log?.LogInfo($"[FireControlPatches] Remote gun FIRING on {root.name} (Gun2={(__instance.Gun != null ? "yes" : "null")})");
                    if (__instance.Gun != null)
                    {
                        var gun = __instance.Gun;
                        Plugin.Log?.LogInfo($"[FireControlPatches] Gun2 DIAG: Ammo={gun.Ammo}, UseAmmo={gun.UseAmmo}, HasAmmo={gun.HasAmmo()}, Barrels={gun.Barrels?.Count ?? -1}, IsBurst={gun.IsBurstFireGun()}, IsReady={gun.IsReadyToFire(Time.timeAsDouble)}");
                    }
                }
                else
                    Plugin.Log?.LogInfo($"[FireControlPatches] Remote gun STOPPED on {root.name}");
            }
            
            // Try to initialize Gun if it's null
            if (__instance.Gun == null)
            {
                TryInitializeGun(__instance);
            }
            
            // For remote aircraft, we control the gun directly using network state
            if (__instance.Gun != null)
            {
                int ammoBefore = __instance.Gun.Ammo;
                __instance.Gun.IsFiring = shouldFire;

                // FIX: Override Target.Velocity with the actual synced velocity from network
                // packets BEFORE Gun.Update(). Gun2.FireBullet() reads OwnTarget.Velocity to
                // inherit aircraft speed into bullet velocity. Without this fix, bullets inherit
                // the velocity-steering value (used for interpolation) instead of the aircraft's
                // actual speed, causing shorter effective range on remote screens.
                Vector3 savedVelocity = Vector3.zero;
                bool didOverrideVelocity = false;
                if (shouldFire && __instance.Gun.OwnTarget != null)
                {
                    try
                    {
                        // Lazy-init the reflection for Target.Velocity (private setter)
                        if (!_targetVelocityPropInitialized)
                        {
                            _targetVelocityPropInitialized = true;
                            _targetVelocityProp = __instance.Gun.OwnTarget.GetType()
                                .GetProperty("Velocity", BindingFlags.Public | BindingFlags.Instance);
                            Plugin.Log?.LogInfo($"[FireControlPatches] Target.Velocity reflection: {(_targetVelocityProp != null ? "found" : "NOT FOUND")}");
                        }

                        if (_targetVelocityProp != null && _targetVelocityProp.GetSetMethod(true) != null)
                        {
                            // Get the actual velocity from the network state packet
                            var remoteManager = Plugin.Instance?.Network?.RemoteAircraftManager;
                            if (remoteManager != null && controller != null)
                            {
                                var state = remoteManager.GetRemotePlayer(controller.PlayerId);
                                if (state != null)
                                {
                                    savedVelocity = __instance.Gun.OwnTarget.Velocity;
                                    _targetVelocityProp.SetValue(__instance.Gun.OwnTarget, state.LastVelocity);
                                    didOverrideVelocity = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning($"[FireControlPatches] Target.Velocity override failed: {ex.Message}");
                    }
                }

                __instance.Gun.Update(Time.timeAsDouble, Time.deltaTime);

                // Restore original velocity after Gun.Update() so other systems aren't affected
                if (didOverrideVelocity && _targetVelocityProp != null)
                {
                    try { _targetVelocityProp.SetValue(__instance.Gun.OwnTarget, savedVelocity); }
                    catch { /* best effort restore */ }
                }
                
                if (shouldFire && ammoBefore != __instance.Gun.Ammo)
                {
                    if (!_loggedAmmoConsume.Contains(instanceId))
                    {
                        _loggedAmmoConsume.Add(instanceId);
                        Plugin.Log?.LogInfo($"[FireControlPatches] Gun2 CONFIRMED FIRING bullets! Ammo: {ammoBefore} -> {__instance.Gun.Ammo}");
                    }
                }
                else if (shouldFire && ammoBefore == __instance.Gun.Ammo && !_loggedNoAmmoConsume.Contains(instanceId))
                {
                    _loggedNoAmmoConsume.Add(instanceId);
                    Plugin.Log?.LogInfo($"[FireControlPatches] Gun2 NOT consuming ammo despite IsFiring=true. Ammo={ammoBefore}, HasAmmo={__instance.Gun.HasAmmo()}, IsReady={__instance.Gun.IsReadyToFire(Time.timeAsDouble)}");
                }
            }
            else if (shouldFire)
            {
                Plugin.Log?.LogInfo($"[FireControlPatches] Gun2 null, using fallback for {root.name}");
                RealCombatSync.FireBulletsDirectlyPublic(root);
            }
            
            return false; // Skip original Update for remote aircraft
        }

        /// <summary>
        /// Harmony POSTFIX on FireControl.Update — runs AFTER the original method.
        /// For local aircraft, logs diagnostic info about gun firing.
        /// </summary>
        private static readonly Dictionary<int, int> _prevAmmo = new Dictionary<int, int>();
        private static int _postfixLogCount = 0;
        private const int MAX_POSTFIX_LOGS = 30;
        
        [HarmonyPostfix]
        public static void Update_Postfix(FireControl __instance)
        {
            // Only track local aircraft
            var root = __instance.transform.root.gameObject;
            if (RemoteAircraftRegistry.IsRemote(root) || RemoteAircraftRegistry.IsRemote(__instance.gameObject))
                return;
            
            if (__instance.Gun == null) return;
            
            int instanceId = __instance.GetInstanceID();
            int currentAmmo = __instance.Gun.Ammo;
            
            if (_prevAmmo.ContainsKey(instanceId))
            {
                int prevAmmo = _prevAmmo[instanceId];
                if (prevAmmo != currentAmmo && _postfixLogCount < MAX_POSTFIX_LOGS)
                {
                    _postfixLogCount++;
                    Plugin.Log?.LogInfo($"[FireControlPatches] POSTFIX: Ammo changed {prevAmmo} -> {currentAmmo} (bullets fired!)");
                }
            }
            
            _prevAmmo[instanceId] = currentAmmo;
        }
        
        /// <summary>
        /// Harmony FINALIZER on FireControl.Update to catch swallowed exceptions.
        /// Unity silently catches NullReferenceException in Update loops, so we need
        /// this to see when the gun pipeline crashes.
        /// </summary>
        [HarmonyFinalizer]
        public static Exception Update_Finalizer(Exception __exception, FireControl __instance)
        {
            if (__exception != null)
            {
                var root = __instance.transform.root.gameObject;
                bool isRemote = RemoteAircraftRegistry.IsRemote(root) || RemoteAircraftRegistry.IsRemote(__instance.gameObject);
                Plugin.Log?.LogError($"[FireControlPatches] EXCEPTION in FireControl.Update ({(isRemote ? "REMOTE" : "LOCAL")} {root.name}): {__exception}");
            }
            return __exception; // Let Unity handle it normally
        }

        /// <summary>
        /// Clear all static state between game sessions.
        /// </summary>
        public static void ClearState()
        {
            _attemptedInit.Clear();
            _prevFiringState.Clear();
            _loggedAmmoConsume.Clear();
            _loggedNoAmmoConsume.Clear();
            _fireControlStartMethod = null;
            _fireControlAwakeMethod = null;
            _reflectionCached = false;
    }

    /// <summary>
    /// Diagnostic patch on Bullet2Manager.FireBullet to detect:
    /// 1. If FireBullet is even being called for non-AV8B planes
    /// 2. If BulletData is null (from AmmoBelt.GetNextBullet())
    /// 3. If Bullet2Manager is not initialized
    /// 4. If any exception occurs inside FireBullet
    /// </summary>
    [HarmonyPatch(typeof(Bullet2Manager))]
    [HarmonyPatch("FireBullet")]
    public static class Bullet2ManagerDiagnostics
    {
        static Bullet2ManagerDiagnostics()
        {
            Plugin.Log?.LogInfo("[Bullet2ManagerDiagnostics] PATCH CLASS LOADED - Bullet2Manager.FireBullet will be patched");
        }
        
        private static int _logCount = 0;
        private const int MAX_LOGS = 50;
        
        public static void ClearCache()
        {
            _logCount = 0;
        }
        
        [HarmonyPrefix]
        public static void FireBullet_Prefix(BulletData data, string gunDisplayName, bool ___IsInitialized)
        {
            if (_logCount < MAX_LOGS)
            {
                _logCount++;
                string dataInfo = "NULL";
                if (data != null)
                {
                    dataInfo = $"Type={data.Type}, TTL={data.TimeToLive}, Name={data.Name}";
                }
                Plugin.Log?.LogInfo($"[Bullet2Mgr] FireBullet called #{_logCount}: gun='{gunDisplayName}', data=[{dataInfo}], initialized={___IsInitialized}");
            }
        }
        
        [HarmonyFinalizer]
        public static Exception FireBullet_Finalizer(Exception __exception, BulletData data, string gunDisplayName)
        {
            if (__exception != null)
            {
                string dataInfo = data != null ? $"Type={data.Type}" : "NULL";
                Plugin.Log?.LogError($"[Bullet2Mgr] EXCEPTION in FireBullet: gun='{gunDisplayName}', data=[{dataInfo}]: {__exception}");
            }
            return __exception;
        }
    }

    /// <summary>
    /// Diagnostic patch on Gun2.Update to catch exceptions thrown during the gun firing pipeline.
    /// This catches NullRef from FireFromNextBarrel -> FireBullet chain.
    /// </summary>
    [HarmonyPatch(typeof(Gun2), "Update", new Type[] { typeof(double), typeof(float) })]
    public static class Gun2UpdateDiagnostics
    {
        static Gun2UpdateDiagnostics()
        {
            Plugin.Log?.LogInfo("[Gun2UpdateDiagnostics] PATCH CLASS LOADED - Gun2.Update will be patched");
        }
        
        private static int _exceptionCount = 0;
        private const int MAX_EXCEPTION_LOGS = 20;
        private static int _prefixCallCount = 0;
        private const int MAX_PREFIX_LOGS = 10;
        
        [HarmonyPrefix]
        public static void Update_Prefix(Gun2 __instance)
        {
            if (__instance.IsFiring && _prefixCallCount < MAX_PREFIX_LOGS)
            {
                _prefixCallCount++;
                Plugin.Log?.LogInfo($"[Gun2Diag] Update FIRING #{_prefixCallCount}: Ammo={__instance.Ammo}, HasAmmo={__instance.HasAmmo()}, " +
                    $"IsReady={__instance.IsReadyToFire(Time.timeAsDouble)}, Barrels={__instance.Barrels?.Count ?? -1}");
            }
        }
        
        [HarmonyFinalizer]
        public static Exception Update_Finalizer(Exception __exception, Gun2 __instance)
        {
            if (__exception != null && _exceptionCount < MAX_EXCEPTION_LOGS)
            {
                _exceptionCount++;
                string gunName = __instance.GunData != null ? __instance.GunData.GetDisplayName() : "NULL_GUNDATA";
                string bulletName = __instance.GunData != null ? __instance.GunData.Bullet : "N/A";
                
                // Check AmmoBelt state
                string ammoBeltInfo = "NULL";
                if (__instance.AmmoBelt != null)
                {
                    try
                    {
                        var nextBullet = __instance.AmmoBelt.PeekNextBullet();
                        ammoBeltInfo = nextBullet != null ? $"NextBullet={nextBullet.Name},Type={nextBullet.Type}" : "PeekNextBullet=NULL";
                    }
                    catch (Exception peekEx)
                    {
                        ammoBeltInfo = $"PeekFailed: {peekEx.Message}";
                    }
                }
                
                Plugin.Log?.LogError($"[Gun2Diag] EXCEPTION in Gun2.Update: gun='{gunName}', bullet='{bulletName}', ammo={__instance.Ammo}, " +
                    $"ammoBelt=[{ammoBeltInfo}], isFiring={__instance.IsFiring}, barrels={__instance.Barrels?.Count ?? -1}: {__exception}");
            }
            return __exception;
        }
        }
    }

    /// <summary>
    /// Patches for UniAircraft to identify remote vs local
    /// </summary>
    [HarmonyPatch]
    public static class UniAircraftPatches
    {
        // Placeholder for additional patches if needed
    }
}
