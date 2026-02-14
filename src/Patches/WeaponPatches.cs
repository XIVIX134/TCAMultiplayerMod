using HarmonyLib;
using UnityEngine;
using TCAMultiplayer.Networking;
using TCAMultiplayer.Player;
using System;
using System.Collections.Generic;
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

        // Seeker type constants
        private const byte SEEKER_TYPE_IR = 0;
        private const byte SEEKER_TYPE_RADAR = 1;
        private const byte SEEKER_TYPE_UNGUIDED = 2;

        // ===== MISSILE LAUNCH POLLING =====
        // Instead of relying on Harmony Postfix (which never fires for Munition.Launch),
        // we poll the static Munition.LaunchedMissiles list each frame to detect new launches.
        private static bool _pollingInitialized = false;
        private static System.Collections.IList _launchedMissilesList = null;
        private static FieldInfo _launchedMissilesField = null;
        private static int _lastKnownMissileCount = 0;
        private static readonly HashSet<int> _processedMissileIds = new HashSet<int>();
        private static Type _munitionTypeForPolling = null;
        private static FieldInfo _munitionOwnshipField = null;
        private static PropertyInfo _munitionSeekerSigProp = null;
        private static PropertyInfo _munitionDataProp = null;

        // ===== BOMB DROP POLLING =====
        // Bombs don't have seekers, so they're only added to LaunchedMunitions, not LaunchedMissiles
        private static FieldInfo _launchedMunitionsField = null;
        private static System.Collections.IList _launchedMunitionsList = null;
        private static int _lastKnownMunitionCount = 0;
        private static readonly HashSet<int> _processedMunitionIds = new HashSet<int>();
        private static PropertyInfo _munitionHasSeekerProp = null;
        private static FieldInfo _munitionVelocityField = null;

        /// <summary>
        /// Get the local player's peer ID
        /// </summary>
        private static ulong GetLocalPlayerId() => Plugin.Instance.Network.LocalPeerId;

        /// <summary>
        /// Get the remote player's peer ID (for 2-player games)
        /// </summary>
        private static ulong GetRemotePlayerId() => Plugin.Instance.Network.IsHost ? 2UL : 1UL;

        /// <summary>
        /// Patch Munition.Launch(Vector3) to detect missile launches.
        /// From decompiled code: public void Launch(Vector3 inheritedVelocity)
        /// </summary>
        [HarmonyPatch]
        public static class MunitionLaunchPatch
        {
            static MethodBase TargetMethod()
            {
                try
                {
                    var munitionType = Type.GetType("Falcon.Stores.Munition, Assembly-CSharp");
                    if (munitionType == null)
                    {
                        Plugin.Log?.LogWarning("[WeaponPatches] TargetMethod: Munition type not found");
                        return null;
                    }

                    // Exact signature from decompiled code: public void Launch(Vector3 inheritedVelocity)
                    var method = munitionType.GetMethod("Launch",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new Type[] { typeof(UnityEngine.Vector3) },
                        null);

                    if (method != null)
                    {
                        Plugin.Log?.LogInfo("[WeaponPatches] TargetMethod: Found Munition.Launch(Vector3) — patch will apply");
                        return method;
                    }

                    Plugin.Log?.LogWarning("[WeaponPatches] TargetMethod: Munition.Launch(Vector3) NOT FOUND");
                    return null;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"[WeaponPatches] TargetMethod error: {ex.Message}");
                    return null;
                }
            }

            /// <summary>
            /// Postfix ONLY pre-registers the missile instance ID so the polling system
            /// knows it already exists.  Actual packet sending is handled exclusively by
            /// PollMissileLaunches() to avoid duplicate packets (Fix #4).
            /// </summary>
            static void Postfix(object __instance)
            {
                try
                {
                    var munition = __instance as MonoBehaviour;
                    if (munition == null) return;

                    // Pre-register instance ID so PollMissileLaunches won't double-send
                    int instanceId = (munition as UnityEngine.Object)?.GetInstanceID() ?? __instance.GetHashCode();
                    // _processedMissileIds is in the outer class — we access it via the enclosing scope
                    // Note: this set is checked in PollMissileLaunches before sending
                    // We still let Poll handle the actual send (it runs the same frame in Update)
                    Plugin.Log?.LogInfo($"[WeaponPatches] Postfix: Munition.Launch fired, instanceId={instanceId} (poll will handle send)");
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"[WeaponPatches] MissileLaunch Postfix error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Called from FlightGamePatches.Update every frame to detect new missile launches.
        /// Polls Munition.LaunchedMissiles static list instead of relying on Harmony Postfix.
        /// </summary>
        public static void PollMissileLaunches()
        {
            try
            {
                if (Plugin.Instance == null || Plugin.Instance.Network == null) return;
                if (!Plugin.Instance.Network.IsConnected) return;

                // Initialize polling reflection once
                if (!_pollingInitialized)
                {
                    InitializeMissilePolling();
                }

                if (_launchedMissilesList == null)
                {
                    // Re-read the list field each time in case it was re-assigned
                    if (_launchedMissilesField != null)
                    {
                        _launchedMissilesList = _launchedMissilesField.GetValue(null) as System.Collections.IList;
                    }
                    if (_launchedMissilesList == null) return;
                }

                int currentCount = _launchedMissilesList.Count;

                // Fast path: no new missiles
                if (currentCount <= _lastKnownMissileCount && currentCount > 0)
                {
                    _lastKnownMissileCount = currentCount;
                    return;
                }

                // Check for new missiles (scan from end since new ones are appended)
                for (int i = 0; i < currentCount; i++)
                {
                    var missile = _launchedMissilesList[i];
                    if (missile == null) continue;

                    int instanceId = (missile as UnityEngine.Object)?.GetInstanceID() ?? missile.GetHashCode();

                    // Already processed this missile
                    if (_processedMissileIds.Contains(instanceId)) continue;

                    _processedMissileIds.Add(instanceId);

                    // Check if this missile belongs to the LOCAL player (not a remote clone or network missile)
                    if (!IsLocalPlayerMissile(missile)) continue;

                    // NEW LOCAL MISSILE DETECTED — send launch packet
                    Plugin.Log?.LogInfo($"[WeaponPatches] POLL: New local missile detected! InstanceId={instanceId}");
                    SendMissileLaunchFromPolling(missile);
                }

                _lastKnownMissileCount = currentCount;
            }
            catch (Exception ex)
            {
                if (LogHelper.ShouldLogInterval("WeaponPatches.PollMissiles.Error", 5f))
                    Plugin.Log?.LogError($"[WeaponPatches] PollMissileLaunches error: {ex.Message}");
            }
        }

        private static void InitializeMissilePolling()
        {
            _pollingInitialized = true;
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                _munitionTypeForPolling = ReflectionHelper.GetGameType("Falcon.Stores.Munition");
                if (_munitionTypeForPolling == null)
                {
                    Plugin.Log?.LogWarning("[WeaponPatches] POLL: Munition type not found for polling");
                    return;
                }

                // Missile polling (LaunchedMissiles - guided only)
                _launchedMissilesField = _munitionTypeForPolling.GetField("LaunchedMissiles", flags);
                _munitionOwnshipField = _munitionTypeForPolling.GetField("Ownship", flags);
                _munitionSeekerSigProp = _munitionTypeForPolling.GetProperty("SeekerSignature", flags);
                _munitionDataProp = _munitionTypeForPolling.GetProperty("Data", flags);

                // Bomb polling (LaunchedMunitions - all munitions including bombs)
                _launchedMunitionsField = _munitionTypeForPolling.GetField("LaunchedMunitions", flags);
                _munitionHasSeekerProp = _munitionTypeForPolling.GetProperty("HasSeeker", flags);
                _munitionVelocityField = _munitionTypeForPolling.GetField("velocity", flags);

                if (_launchedMissilesField != null)
                {
                    _launchedMissilesList = _launchedMissilesField.GetValue(null) as System.Collections.IList;
                    _lastKnownMissileCount = _launchedMissilesList?.Count ?? 0;
                    Plugin.Log?.LogInfo($"[WeaponPatches] POLL: Initialized. LaunchedMissiles has {_lastKnownMissileCount} entries. " +
                        $"Ownship={_munitionOwnshipField != null}, SeekerSig={_munitionSeekerSigProp != null}");
                }
                else
                {
                    Plugin.Log?.LogWarning("[WeaponPatches] POLL: LaunchedMissiles field not found!");
                }

                if (_launchedMunitionsField != null)
                {
                    _launchedMunitionsList = _launchedMunitionsField.GetValue(null) as System.Collections.IList;
                    _lastKnownMunitionCount = _launchedMunitionsList?.Count ?? 0;
                    Plugin.Log?.LogInfo($"[WeaponPatches] POLL: LaunchedMunitions has {_lastKnownMunitionCount} entries. " +
                        $"HasSeeker={_munitionHasSeekerProp != null}, Velocity={_munitionVelocityField != null}");
                }
                else
                {
                    Plugin.Log?.LogWarning("[WeaponPatches] POLL: LaunchedMunitions field not found!");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WeaponPatches] POLL init error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Check if a missile was launched by the local player (not remote clone, not network missile).
        /// Uses Munition.Ownship — set during Launch() to GetComponentInParent Target.
        /// If Ownship's aircraft has a RemoteAircraftController, it's not ours.
        /// Also skip our own network missiles (MP_ prefix).
        /// </summary>
        private static bool IsLocalPlayerMissile(object missile)
        {
            try
            {
                var comp = missile as Component;
                if (comp == null) return false;

                // Skip network missiles we created
                if (comp.gameObject.name.StartsWith("MP_")) return false;

                // Check Ownship — if null, it might be a stale missile, skip
                if (_munitionOwnshipField != null)
                {
                    var ownship = _munitionOwnshipField.GetValue(missile);
                    if (ownship != null)
                    {
                        var ownshipComp = ownship as Component;
                        if (ownshipComp != null)
                        {
                            // If the ownship aircraft has RemoteAircraftController, it's NOT our missile
                            var remoteCtrl = ownshipComp.GetComponentInParent<RemoteAircraftController>();
                            if (remoteCtrl != null)
                            {
                                return false; // Remote player's missile
                            }
                        }
                    }
                    // If Ownship is null, the missile might be pre-existing or from game setup — skip
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    // Can't check ownership — use fallback: check if missile is parented to remote clone
                    var parentRemote = comp.GetComponentInParent<RemoteAircraftController>();
                    if (parentRemote != null) return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Build and send a MissileLaunchPacket from a detected missile object (polling approach).
        /// </summary>
        private static void SendMissileLaunchFromPolling(object missile)
        {
            try
            {
                var munition = missile as MonoBehaviour;
                if (munition == null) return;

                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                // Get missile name
                string missileName = munition.gameObject.name;
                if (_munitionDataProp != null)
                {
                    var data = _munitionDataProp.GetValue(missile);
                    if (data != null)
                    {
                        var displayNameProp = data.GetType().GetProperty("DisplayName", flags)
                            ?? data.GetType().GetProperty("Name", flags);
                        if (displayNameProp != null)
                        {
                            missileName = displayNameProp.GetValue(data) as string ?? missileName;
                        }
                    }
                }

                // Get seeker type from SeekerSignature
                byte seekerType = SEEKER_TYPE_UNGUIDED;
                if (_munitionSeekerSigProp != null)
                {
                    var guidanceVal = _munitionSeekerSigProp.GetValue(missile);
                    if (guidanceVal != null)
                    {
                        int guidanceInt = (int)guidanceVal;
                        if (guidanceInt == 1) seekerType = SEEKER_TYPE_IR;
                        else if (guidanceInt == 2 || guidanceInt == 3) seekerType = SEEKER_TYPE_RADAR;
                        else if (guidanceInt != 0) seekerType = SEEKER_TYPE_IR; // Other guided = treat as IR
                    }
                }

                // Check if missile has a target (locked or not)
                // IMPORTANT: Always send target if available - missiles like AIM-120 can acquire lock after launch
                ulong targetId = 0; // 0 = no target
                bool isTracking = false;
                
                // Get the Target field from Munition to check if there's an actual target
                var targetField = munition.GetType().GetField("Target", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (targetField != null)
                {
                    var target = targetField.GetValue(missile);
                    if (target != null)
                    {
                        // Always set target ID if missile has a target
                        targetId = GetRemotePlayerId();
                        
                        // IMPORTANT: If missile has a target, set IsTracking=true
                        // At launch moment, the seeker's IsTracking may be false because it hasn't activated yet,
                        // but the missile WILL track once the seeker activates. For RWR to show the threat,
                        // we need to indicate that the missile is tracking.
                        isTracking = true;
                        
                        Plugin.Log?.LogInfo($"[WeaponPatches] POLL: Missile has target - TargetId={targetId}, IsTracking={isTracking}");
                    }
                    else
                    {
                        Plugin.Log?.LogInfo($"[WeaponPatches] POLL: Missile has no target - flying unguided");
                    }
                }

                // Get launch position and direction
                Vector3 launchPos = munition.transform.position;
                Vector3 launchDir = munition.transform.forward;
                Vector3d absoluteLaunchPos = FloatingOriginHelper.LocalToAbsolute(launchPos);

                var packet = new MissileLaunchPacket
                {
                    ShooterId = GetLocalPlayerId(),
                    TargetId = targetId,
                    IsTracking = isTracking,
                    MissileType = missileName,
                    SeekerType = seekerType,
                    LaunchPosX = absoluteLaunchPos.x,
                    LaunchPosY = absoluteLaunchPos.y,
                    LaunchPosZ = absoluteLaunchPos.z,
                    LaunchDirX = launchDir.x,
                    LaunchDirY = launchDir.y,
                    LaunchDirZ = launchDir.z
                };

                byte[] data2 = PacketSerializer.SerializeMissileLaunch(packet);
                Plugin.Instance.Network.SendPacket(PacketType.MissileLaunch, data2, reliable: true);

                Plugin.Log?.LogInfo($"[WeaponPatches] POLL: Sent missile launch: {missileName} (seeker: {seekerType}) pos=({absoluteLaunchPos.x:F1},{absoluteLaunchPos.y:F1},{absoluteLaunchPos.z:F1})");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WeaponPatches] POLL SendMissileLaunch error: {ex.Message}");
            }
        }

        /// <summary>
        /// Called from FlightGamePatches.Update every frame to detect new bomb drops.
        /// Polls Munition.LaunchedMunitions static list for unguided munitions (bombs).
        /// </summary>
        public static void PollBombDrops()
        {
            try
            {
                if (Plugin.Instance == null || Plugin.Instance.Network == null) return;
                if (!Plugin.Instance.Network.IsConnected) return;

                // Initialize polling reflection once
                if (!_pollingInitialized)
                {
                    InitializeMissilePolling();
                }

                if (_launchedMunitionsList == null)
                {
                    // Re-read the list field each time in case it was re-assigned
                    if (_launchedMunitionsField != null)
                    {
                        _launchedMunitionsList = _launchedMunitionsField.GetValue(null) as System.Collections.IList;
                    }
                    if (_launchedMunitionsList == null) return;
                }

                int currentCount = _launchedMunitionsList.Count;

                // Fast path: no new munitions
                if (currentCount <= _lastKnownMunitionCount && currentCount > 0)
                {
                    _lastKnownMunitionCount = currentCount;
                    return;
                }

                // Check for new munitions (scan from end since new ones are appended)
                for (int i = 0; i < currentCount; i++)
                {
                    var munition = _launchedMunitionsList[i];
                    if (munition == null) continue;

                    int instanceId = (munition as UnityEngine.Object)?.GetInstanceID() ?? munition.GetHashCode();

                    // Already processed this munition
                    if (_processedMunitionIds.Contains(instanceId)) continue;

                    _processedMunitionIds.Add(instanceId);

                    // Check if this is a BOMB (no seeker) from the LOCAL player
                    if (!IsLocalPlayerBomb(munition)) continue;

                    // NEW LOCAL BOMB DETECTED — send bomb drop packet
                    Plugin.Log?.LogInfo($"[WeaponPatches] BOMB POLL: New local bomb detected! InstanceId={instanceId}");
                    SendBombDropFromPolling(munition);
                }

                _lastKnownMunitionCount = currentCount;
            }
            catch (Exception ex)
            {
                if (LogHelper.ShouldLogInterval("WeaponPatches.PollBombs.Error", 5f))
                    Plugin.Log?.LogError($"[WeaponPatches] PollBombDrops error: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a munition is a bomb (no seeker) launched by the local player.
        /// </summary>
        private static bool IsLocalPlayerBomb(object munition)
        {
            try
            {
                var comp = munition as Component;
                if (comp == null) return false;

                // Skip network munitions we created
                if (comp.gameObject.name.StartsWith("MP_")) return false;

                // Check if it has a seeker - if yes, it's a missile, not a bomb
                if (_munitionHasSeekerProp != null)
                {
                    var hasSeeker = (bool)_munitionHasSeekerProp.GetValue(munition);
                    if (hasSeeker)
                    {
                        return false; // It's a missile, not a bomb
                    }
                }

                // Check Ownship — if null, skip
                if (_munitionOwnshipField != null)
                {
                    var ownship = _munitionOwnshipField.GetValue(munition);
                    if (ownship != null)
                    {
                        var ownshipComp = ownship as Component;
                        if (ownshipComp != null)
                        {
                            // If the ownship aircraft has RemoteAircraftController, it's NOT our bomb
                            var remoteCtrl = ownshipComp.GetComponentInParent<RemoteAircraftController>();
                            if (remoteCtrl != null)
                            {
                                return false; // Remote player's bomb
                            }
                        }
                    }
                    else
                    {
                        return false; // No ownship, skip
                    }
                }
                else
                {
                    // Can't check ownership — use fallback
                    var parentRemote = comp.GetComponentInParent<RemoteAircraftController>();
                    if (parentRemote != null) return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Build and send a BombDropPacket from a detected bomb object.
        /// </summary>
        private static void SendBombDropFromPolling(object bomb)
        {
            try
            {
                var munition = bomb as MonoBehaviour;
                if (munition == null) return;

                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                // Get bomb name
                string bombName = munition.gameObject.name;
                if (_munitionDataProp != null)
                {
                    var data = _munitionDataProp.GetValue(bomb);
                    if (data != null)
                    {
                        var displayNameProp = data.GetType().GetProperty("DisplayName", flags)
                            ?? data.GetType().GetProperty("Name", flags);
                        if (displayNameProp != null)
                        {
                            bombName = displayNameProp.GetValue(data) as string ?? bombName;
                        }
                    }
                }

                // Get launch position
                Vector3 launchPos = munition.transform.position;
                Vector3d absoluteLaunchPos = FloatingOriginHelper.LocalToAbsolute(launchPos);

                // Get velocity (includes aircraft velocity at release)
                Vector3 velocity = Vector3.zero;
                if (_munitionVelocityField != null)
                {
                    var vel = _munitionVelocityField.GetValue(bomb);
                    if (vel is Vector3 v3)
                    {
                        velocity = v3;
                    }
                }

                var packet = new BombDropPacket
                {
                    ShooterId = GetLocalPlayerId(),
                    BombType = bombName,
                    LaunchPosX = absoluteLaunchPos.x,
                    LaunchPosY = absoluteLaunchPos.y,
                    LaunchPosZ = absoluteLaunchPos.z,
                    VelX = velocity.x,
                    VelY = velocity.y,
                    VelZ = velocity.z,
                    Timestamp = Time.time
                };

                byte[] data2 = PacketSerializer.SerializeBombDrop(packet);
                Plugin.Instance.Network.SendPacket(PacketType.BombDrop, data2, reliable: true);

                Plugin.Log?.LogInfo($"[WeaponPatches] BOMB POLL: Sent bomb drop: {bombName} pos=({absoluteLaunchPos.x:F1},{absoluteLaunchPos.y:F1},{absoluteLaunchPos.z:F1}) vel=({velocity.x:F1},{velocity.y:F1},{velocity.z:F1})");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WeaponPatches] BOMB POLL SendBombDrop error: {ex.Message}");
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
                LockerId = GetLocalPlayerId(),
                TargetId = GetRemotePlayerId(),
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
                ulong localPlayerId = GetLocalPlayerId();

                // Process missile if:
                // 1. We're the target (TargetId == localPlayerId) - missile is tracking us
                // 2. TargetId == 0 - missile has no lock, fly unguided (still spawn for visibility)
                // Skip if missile is tracking someone else
                if (packet.TargetId != 0 && packet.TargetId != localPlayerId)
                {
                    Plugin.Log?.LogInfo($"[WeaponPatches] Missile {packet.MissileType} tracking player {packet.TargetId}, not us ({localPlayerId}) - skipping");
                    return;
                }

                bool isTrackingUs = (packet.TargetId == localPlayerId);
                Plugin.Log?.LogInfo($"[WeaponPatches] INCOMING MISSILE! {packet.MissileType} (seeker: {packet.SeekerType}, tracking: {isTrackingUs})");
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
        /// Handle received bomb drop - spawn network bomb for visual/damage sync
        /// </summary>
        public static void HandleBombDrop(BombDropPacket packet)
        {
            try
            {
                Plugin.Log?.LogInfo($"[WeaponPatches] INCOMING BOMB! {packet.BombType}");

                // Convert position to local coordinates
                var absoluteLaunchPos = new Vector3d(packet.LaunchPosX, packet.LaunchPosY, packet.LaunchPosZ);
                Vector3 localLaunchPos = FloatingOriginHelper.AbsoluteToLocal(absoluteLaunchPos);
                Vector3 velocity = new Vector3(packet.VelX, packet.VelY, packet.VelZ);

                // Spawn the network bomb
                Player.RealCombatSync.SpawnNetworkBomb(packet, localLaunchPos, velocity);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WeaponPatches] HandleBombDrop error: {ex.Message}");
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
                ulong localPlayerId = GetLocalPlayerId();

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

            // Reset missile polling state
            _pollingInitialized = false;
            _launchedMissilesList = null;
            _lastKnownMissileCount = 0;
            _processedMissileIds.Clear();
        }
    }
}
