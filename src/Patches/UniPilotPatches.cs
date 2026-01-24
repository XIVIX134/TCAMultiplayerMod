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
    public static class FireControlPatches
    {
        // Track which FireControls we've tried to initialize
        private static readonly HashSet<int> _attemptedInit = new HashSet<int>();
        private static MethodInfo _fireControlStartMethod = null;
        private static MethodInfo _fireControlAwakeMethod = null;
        private static bool _reflectionCached = false;
        
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
        }
        
        /// <summary>
        /// Patch Update to use network state for remote aircraft instead of local input
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        public static bool Update_Prefix(FireControl __instance)
        {
            // Check if this FireControl belongs to a remote aircraft
            var root = __instance.transform.root.gameObject;
            bool isRemote = RemoteAircraftRegistry.IsRemote(root) || 
                            RemoteAircraftRegistry.IsRemote(__instance.gameObject);
            
            if (isRemote)
            {
                // Get firing state from RemoteAircraftController
                var controller = root.GetComponent<RemoteAircraftController>();
                bool shouldFire = controller != null && controller.IsFiring;
                
                // Try to initialize Gun if it's null
                if (__instance.Gun == null)
                {
                    TryInitializeGun(__instance);
                }
                
                // For remote aircraft, we control the gun directly using network state
                if (__instance.Gun != null)
                {
                    __instance.Gun.IsFiring = shouldFire;
                    __instance.Gun.Update(Time.timeAsDouble, Time.deltaTime);
                    
                    // Log when firing for debugging
                    if (shouldFire && LogHelper.ShouldLogInterval("FireControlPatch.Firing", 1f))
                    {
                        Plugin.Log?.LogInfo($"[FireControlPatches] Remote gun FIRING on {root.name} via native Gun2 system");
                    }
                }
                else if (shouldFire)
                {
                    // Gun2 is null on cloned aircraft - use fallback direct bullet spawning
                    if (LogHelper.ShouldLogInterval("FireControlPatch.Fallback", 1f))
                    {
                        Plugin.Log?.LogInfo($"[FireControlPatches] Using FireBulletsDirectly fallback for {root.name}");
                    }
                    RealCombatSync.FireBulletsDirectlyPublic(root);
                }
                
                return false; // Skip original Update (which would read from local input)
            }
            
            return true; // Run normal Update for local aircraft
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
