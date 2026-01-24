using HarmonyLib;
using UnityEngine;
using TCAMultiplayer.Networking;
using TCAMultiplayer.Player;
using System;
using System.Reflection;
using TCAMultiplayer;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Patches for weapon systems to sync missile launches and radar locks
    /// </summary>
    [HarmonyPatch]
    public static class WeaponPatches
    {
        // Track last known radar lock state to detect changes
        private static bool _wasLockedOnTarget = false;
        private static object _lastLockedTarget = null;
        
        // Cached reflection for radar
        private static bool _radarReflectionInitialized = false;
        private static PropertyInfo _radarLockedTargetProp = null;
        private static PropertyInfo _radarIsLockedProp = null;
        private static FieldInfo _aircraftRadarField = null;
        
        /// <summary>
        /// Patch Munition.Launch to detect missile launches
        /// </summary>
        [HarmonyPatch]
        public static class MunitionLaunchPatch
        {
            static MethodBase TargetMethod()
            {
                var munitionType = Type.GetType("Falcon.Stores.Munition, Assembly-CSharp");
                if (munitionType != null)
                {
                    return munitionType.GetMethod("Launch", BindingFlags.Public | BindingFlags.Instance);
                }
                return null;
            }

            static void Postfix(object __instance)
            {
                try
                {
                    if (Plugin.Instance == null || Plugin.Instance.Network == null) return;
                    if (!Plugin.Instance.Network.IsConnected)
                    {
                        if (LogHelper.IsEnabled(LogCategory.Weapon) &&
                            LogHelper.ShouldLogInterval("WeaponPatches.MissileLaunch.NotConnected", LogHelper.DefaultIntervalSeconds))
                        {
                            LogHelper.Info(LogCategory.Weapon, "[WeaponPatches] Missile launch ignored: not connected");
                        }
                        return;
                    }
                    
                    var munition = __instance as MonoBehaviour;
                    if (munition == null) return;
                    
                    // Check if this munition belongs to local player (not a remote clone)
                    var controller = munition.GetComponentInParent<RemoteAircraftController>();
                    if (controller != null)
                    {
                        if (LogHelper.IsEnabled(LogCategory.Weapon) &&
                            LogHelper.ShouldLogInterval("WeaponPatches.MissileLaunch.RemoteClone", LogHelper.DefaultIntervalSeconds))
                        {
                            LogHelper.Info(LogCategory.Weapon, "[WeaponPatches] Ignoring munition launch from remote clone");
                        }
                        return; // Ignore missiles from remote clones
                    }
                    
                    // Get munition info via reflection
                    var munitionType = __instance.GetType();
                    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    
                    // Get missile name
                    string missileName = munition.gameObject.name;
                    var dataField = munitionType.GetField("Data", flags);
                    if (dataField != null)
                    {
                        var munitionData = dataField.GetValue(__instance);
                        if (munitionData != null)
                        {
                            var nameField = munitionData.GetType().GetField("Name", flags);
                            if (nameField != null)
                            {
                                missileName = nameField.GetValue(munitionData) as string ?? missileName;
                            }
                        }
                    }
                    
                    // Get seeker type (IR or Radar)
                    byte seekerType = 2; // Default: unguided
                    var seekerField = munitionType.GetField("Seeker", flags);
                    if (seekerField != null)
                    {
                        var seeker = seekerField.GetValue(__instance);
                        if (seeker != null)
                        {
                            var seekerTypeField = seeker.GetType().GetField("seekerType", flags);
                            if (seekerTypeField != null)
                            {
                                var seekerTypeVal = seekerTypeField.GetValue(seeker);
                                if (seekerTypeVal != null)
                                {
                                    // SeekerType enum: Infrared=0, Radar=1
                                    seekerType = (byte)(int)seekerTypeVal;
                                    Plugin.Log?.LogInfo($"[WeaponPatches] Got seeker type from field: {seekerType}");
                                }
                            }
                            else
                            {
                                // Try SeekerType property instead
                                var seekerTypeProp = seeker.GetType().GetProperty("SeekerType", flags);
                                if (seekerTypeProp != null)
                                {
                                    var seekerTypeVal = seekerTypeProp.GetValue(seeker);
                                    if (seekerTypeVal != null)
                                    {
                                        seekerType = (byte)(int)seekerTypeVal;
                                        Plugin.Log?.LogInfo($"[WeaponPatches] Got seeker type from property: {seekerType}");
                                    }
                                }
                            }
                        }
                    }
                    
                    // FALLBACK: If reflection failed (seekerType still 2), determine from missile name
                    if (seekerType == 2)
                    {
                        string upperName = missileName.ToUpperInvariant();
                        
                        // IR missiles (heat seekers)
                        if (upperName.Contains("AIM-9") || upperName.Contains("AIM9") ||
                            upperName.Contains("SIDEWINDER") ||
                            upperName.Contains("R-73") || upperName.Contains("R73") ||
                            upperName.Contains("R-60") || upperName.Contains("R60") ||
                            upperName.Contains("MAGIC") || upperName.Contains("PYTHON") ||
                            upperName.Contains("ASRAAM") || upperName.Contains("IRIS"))
                        {
                            seekerType = 0; // IR
                            Plugin.Log?.LogInfo($"[WeaponPatches] Detected IR missile from name: {missileName}");
                        }
                        // Radar-guided missiles (semi-active or active)
                        else if (upperName.Contains("AIM-120") || upperName.Contains("AIM120") ||
                                 upperName.Contains("AMRAAM") ||
                                 upperName.Contains("AIM-7") || upperName.Contains("AIM7") ||
                                 upperName.Contains("SPARROW") ||
                                 upperName.Contains("R-27") || upperName.Contains("R27") ||
                                 upperName.Contains("R-77") || upperName.Contains("R77") ||
                                 upperName.Contains("METEOR") || upperName.Contains("MICA"))
                        {
                            seekerType = 1; // Radar
                            Plugin.Log?.LogInfo($"[WeaponPatches] Detected radar missile from name: {missileName}");
                        }
                        else
                        {
                            // Still unguided/unknown - log for debugging
                            Plugin.Log?.LogWarning($"[WeaponPatches] Could not determine seeker type for: {missileName}, defaulting to unguided");
                        }
                    }
                    
                    // Get target if any
                    ulong targetId = 0;
                    var targetField = munitionType.GetField("Target", flags) ?? munitionType.GetProperty("Target", flags)?.GetMethod?.Invoke(__instance, null) as FieldInfo;
                    // For now, assume target is the remote player
                    targetId = Plugin.Instance.Network.IsHost ? 2UL : 1UL;
                    
                    // Get launch position and direction
                    Vector3 launchPos = munition.transform.position;
                    Vector3 launchDir = munition.transform.forward;
                    Vector3d absoluteLaunchPos = FloatingOriginHelper.LocalToAbsolute(launchPos);
                    
                    Plugin.Log?.LogInfo($"[WeaponPatches] Launch pos local {launchPos} absolute {absoluteLaunchPos}");
                    
                    var packet = new MissileLaunchPacket
                    {
                        ShooterId = Plugin.Instance.Network.IsHost ? 1UL : 2UL,
                        TargetId = targetId,
                        MissileType = missileName,
                        SeekerType = seekerType,
                        LaunchPosX = (float)absoluteLaunchPos.x,
                        LaunchPosY = (float)absoluteLaunchPos.y,
                        LaunchPosZ = (float)absoluteLaunchPos.z,
                        LaunchDirX = launchDir.x,
                        LaunchDirY = launchDir.y,
                        LaunchDirZ = launchDir.z
                    };
                    
                    byte[] data = PacketSerializer.SerializeMissileLaunch(packet);
                    Plugin.Instance.Network.SendPacket(PacketType.MissileLaunch, data, reliable: true);
                    
                    Plugin.Log?.LogInfo($"[WeaponPatches] Sent missile launch: {missileName} (seeker type: {seekerType})");
                    if (LogHelper.IsEnabled(LogCategory.Weapon))
                    {
                        LogHelper.Info(LogCategory.Weapon,
                            $"[WeaponPatches] Launch packet shooter={packet.ShooterId} target={packet.TargetId} " +
                            $"pos=({packet.LaunchPosX:F1},{packet.LaunchPosY:F1},{packet.LaunchPosZ:F1}) " +
                            $"dir=({packet.LaunchDirX:F2},{packet.LaunchDirY:F2},{packet.LaunchDirZ:F2})");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"[WeaponPatches] MissileLaunch error: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Called from FlightGamePatches.Update to check for radar lock changes
        /// </summary>
        public static void CheckRadarLockState(Component uniAircraft)
        {
            try
            {
                if (Plugin.Instance == null || Plugin.Instance.Network == null) return;
                if (!Plugin.Instance.Network.IsConnected) return;
                if (uniAircraft == null) return;
                
                // Initialize reflection if needed
                if (!_radarReflectionInitialized)
                {
                    InitializeRadarReflection(uniAircraft);
                }
                
                if (_aircraftRadarField == null) return;
                
                // Get radar from aircraft
                var radar = _aircraftRadarField.GetValue(uniAircraft);
                if (radar == null) return;
                
                bool isLocked = false;
                object lockedTarget = null;
                
                if (_radarIsLockedProp != null)
                {
                    isLocked = (bool)_radarIsLockedProp.GetValue(radar);
                }
                
                if (_radarLockedTargetProp != null)
                {
                    lockedTarget = _radarLockedTargetProp.GetValue(radar);
                }
                
                // Detect lock state changes
                if (isLocked && !_wasLockedOnTarget)
                {
                    // Just got a lock - check if it's the remote player
                    if (IsRemotePlayerTarget(lockedTarget))
                    {
                        SendRadarLockPacket(true);
                        Plugin.Log?.LogInfo("[WeaponPatches] Radar locked on remote player - sent lock notification");
                    }
                }
                else if (!isLocked && _wasLockedOnTarget)
                {
                    // Lost lock
                    SendRadarLockPacket(false);
                    Plugin.Log?.LogInfo("[WeaponPatches] Radar lock lost - sent unlock notification");
                }
                else if (isLocked && lockedTarget != _lastLockedTarget)
                {
                    // Target changed
                    if (IsRemotePlayerTarget(lockedTarget))
                    {
                        SendRadarLockPacket(true);
                        Plugin.Log?.LogInfo("[WeaponPatches] Radar lock changed to remote player");
                    }
                    else if (IsRemotePlayerTarget(_lastLockedTarget))
                    {
                        // Was locked on remote, now on something else
                        SendRadarLockPacket(false);
                        Plugin.Log?.LogInfo("[WeaponPatches] Radar lock moved away from remote player");
                    }
                }
                
                _wasLockedOnTarget = isLocked;
                _lastLockedTarget = lockedTarget;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[WeaponPatches] CheckRadarLockState error: {ex.Message}");
            }
        }
        
        private static void InitializeRadarReflection(Component uniAircraft)
        {
            try
            {
                var aircraftType = uniAircraft.GetType();
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                
                // UniAircraft has a Radar property/field
                _aircraftRadarField = aircraftType.GetField("<Radar>k__BackingField", flags)
                    ?? aircraftType.GetField("Radar", flags);
                
                if (_aircraftRadarField == null)
                {
                    var radarProp = aircraftType.GetProperty("Radar", flags);
                    if (radarProp != null)
                    {
                        // Create a wrapper to use property as if it were a field
                        var radar = radarProp.GetValue(uniAircraft);
                        if (radar != null)
                        {
                            var radarType = radar.GetType();
                            _radarIsLockedProp = radarType.GetProperty("IsLocked", flags);
                            _radarLockedTargetProp = radarType.GetProperty("LockedTarget", flags);
                        }
                    }
                }
                else
                {
                    var radar = _aircraftRadarField.GetValue(uniAircraft);
                    if (radar != null)
                    {
                        var radarType = radar.GetType();
                        _radarIsLockedProp = radarType.GetProperty("IsLocked", flags);
                        _radarLockedTargetProp = radarType.GetProperty("LockedTarget", flags);
                    }
                }
                
                _radarReflectionInitialized = true;
                Plugin.Log?.LogInfo($"[WeaponPatches] Radar reflection initialized. IsLocked: {_radarIsLockedProp != null}, LockedTarget: {_radarLockedTargetProp != null}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WeaponPatches] InitializeRadarReflection error: {ex.Message}");
                _radarReflectionInitialized = true; // Don't retry
            }
        }
        
        private static bool IsRemotePlayerTarget(object target)
        {
            if (target == null) return false;
            
            try
            {
                // Target is a Falcon.Targeting.Target component
                var targetMono = target as MonoBehaviour;
                if (targetMono == null) return false;
                
                // Check if it has RemoteAircraftController (means it's the remote player's clone)
                var controller = targetMono.GetComponentInParent<RemoteAircraftController>();
                return controller != null;
            }
            catch
            {
                return false;
            }
        }
        
        private static void SendRadarLockPacket(bool isLocked)
        {
            var packet = new RadarLockPacket
            {
                LockerId = Plugin.Instance.Network.IsHost ? 1UL : 2UL,
                TargetId = Plugin.Instance.Network.IsHost ? 2UL : 1UL,
                IsLocked = isLocked,
                LockType = 0 // Radar
            };
            
            byte[] data = PacketSerializer.SerializeRadarLock(packet);
            Plugin.Instance.Network.SendPacket(
                isLocked ? PacketType.RadarLock : PacketType.RadarLockLost, 
                data, 
                reliable: true
            );
        }
        
        /// <summary>
        /// Handle received missile launch - ADD REAL MISSILE to game's threat system
        /// </summary>
        public static void HandleMissileLaunch(MissileLaunchPacket packet)
        {
            try
            {
                ulong localPlayerId = Plugin.Instance.Network.IsHost ? 1UL : 2UL;
                
                // Only process if we're the target
                if (packet.TargetId != localPlayerId) return;
                
                Plugin.Log?.LogInfo($"[WeaponPatches] INCOMING MISSILE! {packet.MissileType} (seeker: {packet.SeekerType})");
                if (LogHelper.IsEnabled(LogCategory.Weapon))
                {
                    LogHelper.Info(LogCategory.Weapon,
                        $"[WeaponPatches] Incoming packet shooter={packet.ShooterId} pos=({packet.LaunchPosX:F1}," +
                        $"{packet.LaunchPosY:F1},{packet.LaunchPosZ:F1}) dir=({packet.LaunchDirX:F2}," +
                        $"{packet.LaunchDirY:F2},{packet.LaunchDirZ:F2})");
                }
                
                // Convert position to local coordinates
                var absoluteLaunchPos = new Vector3d(packet.LaunchPosX, packet.LaunchPosY, packet.LaunchPosZ);
                Vector3 localLaunchPos = FloatingOriginHelper.AbsoluteToLocal(absoluteLaunchPos);
                
                // Add REAL missile to game's LaunchedMissiles list
                // This makes the game's ThreatWarning system detect it automatically!
                Player.RealCombatSync.AddNetworkMissileToThreatSystem(packet, localLaunchPos);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WeaponPatches] HandleMissileLaunch error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handle received radar lock - SET REAL RADAR LOCK on remote aircraft
        /// This makes the game's RWR system detect the lock automatically!
        /// </summary>
        public static void HandleRadarLock(RadarLockPacket packet)
        {
            try
            {
                ulong localPlayerId = Plugin.Instance.Network.IsHost ? 1UL : 2UL;
                
                // Only process if we're the target
                if (packet.TargetId != localPlayerId) return;
                
                if (packet.IsLocked)
                {
                    Plugin.Log?.LogInfo("[WeaponPatches] WARNING: Enemy radar has locked on!");
                }
                else
                {
                    Plugin.Log?.LogInfo("[WeaponPatches] Enemy radar lock lost");
                }
                
                // Set REAL radar lock on the remote aircraft's radar component
                // This makes the game's ThreatWarning/RWR detect the lock!
                Player.RealCombatSync.SetRemoteRadarLock(packet.LockerId, packet.IsLocked);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WeaponPatches] HandleRadarLock error: {ex.Message}");
            }
        }
        
        // NOTE: TriggerMissileWarning and TriggerRWRLock removed - 
        // Now using RealCombatSync which adds real missiles to Munition.LaunchedMissiles
        // and sets real radar locks via Radar.LockTarget(), making the game's native 
        // ThreatWarning system detect them automatically!
        
        private static Component FindLocalAircraft()
        {
            try
            {
                var aircrafts = UnityEngine.Object.FindObjectsByType<Falcon.UniversalAircraft.UniAircraft>(FindObjectsSortMode.None);
                foreach (var aircraft in aircrafts)
                {
                    if (aircraft.GetComponent<RemoteAircraftController>() == null)
                    {
                        return aircraft;
                    }
                }
            }
            catch { }
            return null;
        }
        
        /// <summary>
        /// Reset state when leaving flight
        /// </summary>
        public static void ClearState()
        {
            _wasLockedOnTarget = false;
            _lastLockedTarget = null;
            _radarReflectionInitialized = false;
        }
    }
}
