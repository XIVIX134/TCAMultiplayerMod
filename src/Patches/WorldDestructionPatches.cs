using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TCAMultiplayer.Networking;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Patches for syncing world destruction (craters, buildings) between clients.
    /// Uses manual Harmony patching to avoid type resolution issues with string-based type names.
    /// </summary>
    public static class WorldDestructionPatches
    {
        private static bool _patchesApplied = false;
        private static Type _worldCratersType;
        private static Type _craterSizeType;
        private static Type _buildingType;

        // Building.IsDestroyed property — used to skip already-destroyed buildings in the search
        private static PropertyInfo _buildingIsDestroyedProp;

        // CraterSize enum values
        private const byte CRATER_SIZE_SMALL = 0;
        private const byte CRATER_SIZE_MEDIUM = 1;
        private const byte CRATER_SIZE_LARGE = 2;
        private const byte CRATER_SIZE_HUGE = 3;
        private const byte CRATER_SIZE_AIRCRAFT = 4;

        // Track if we're processing a network event (to avoid echo)
        private static bool _isProcessingNetworkEvent = false;

        /// <summary>
        /// Allow sibling patches (e.g. ExplosionPatches.TrySpawnCraterForExplosion) to
        /// set the processing flag so that SpawnCraterPostfix treats their SpawnCrater
        /// calls as network events and does NOT echo a CraterSpawn packet back.
        /// </summary>
        public static void SetProcessingFlag(bool value) { _isProcessingNetworkEvent = value; }

        /// <summary>
        /// Apply Harmony patches manually with proper type resolution.
        /// This is called from Plugin.Awake() after Harmony is initialized.
        /// </summary>
        public static void ApplyPatches(Harmony harmony)
        {
            if (_patchesApplied) return;

            try
            {
                // Get types from Assembly-CSharp (the game's assembly)
                _worldCratersType = ReflectionHelper.GetGameType("Falcon.World.WorldCraters2");
                _craterSizeType = ReflectionHelper.GetGameType("Falcon.World.CraterSize");
                _buildingType = ReflectionHelper.GetGameType("Falcon.Buildings.Building");

                if (_worldCratersType == null)
                {
                    Plugin.Log?.LogWarning("[WorldDestruction] WorldCraters2 type not found - crater sync disabled");
                }
                else
                {
                    Plugin.Log?.LogInfo($"[WorldDestruction] WorldCraters2 type found");
                    
                    // Patch WorldCraters2.SpawnCrater
                    var spawnCraterMethod = _worldCratersType.GetMethod("SpawnCrater", new[] { typeof(Vector3), _craterSizeType, typeof(bool) });
                    if (spawnCraterMethod != null)
                    {
                        var postfix = typeof(WorldDestructionPatches).GetMethod("SpawnCraterPostfix", BindingFlags.Public | BindingFlags.Static);
                        harmony.Patch(spawnCraterMethod, postfix: new HarmonyMethod(postfix));
                        Plugin.Log?.LogInfo("[WorldDestruction] Patched WorldCraters2.SpawnCrater");
                    }
                    else
                    {
                        Plugin.Log?.LogWarning("[WorldDestruction] SpawnCrater method not found");
                    }
                }

                if (_buildingType == null)
                {
                    Plugin.Log?.LogWarning("[WorldDestruction] Building type not found - building sync disabled");
                }
                else
                {
                    Plugin.Log?.LogInfo($"[WorldDestruction] Building type found");

                    // Cache IsDestroyed property for skipping already-destroyed buildings in HandleBuildingDestroy
                    _buildingIsDestroyedProp = _buildingType.GetProperty("IsDestroyed",
                        BindingFlags.Public | BindingFlags.Instance);
                    Plugin.Log?.LogInfo($"[WorldDestruction] Building.IsDestroyed property: {(_buildingIsDestroyedProp != null ? "found" : "NOT FOUND")}");

                    // Patch Building.DestroyBuilding
                    var destroyBuildingMethod = _buildingType.GetMethod("DestroyBuilding", BindingFlags.Public | BindingFlags.Instance);
                    if (destroyBuildingMethod != null)
                    {
                        var postfix = typeof(WorldDestructionPatches).GetMethod("DestroyBuildingPostfix", BindingFlags.Public | BindingFlags.Static);
                        harmony.Patch(destroyBuildingMethod, postfix: new HarmonyMethod(postfix));
                        Plugin.Log?.LogInfo("[WorldDestruction] Patched Building.DestroyBuilding");
                    }
                    else
                    {
                        Plugin.Log?.LogWarning("[WorldDestruction] DestroyBuilding method not found");
                    }
                }

                _patchesApplied = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WorldDestruction] ApplyPatches error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        #region Crater Sync

        /// <summary>
        /// Postfix on WorldCraters2.SpawnCrater to sync craters to other clients.
        /// </summary>
        public static void SpawnCraterPostfix(Vector3 position, object size, bool forceGroundClamp)
        {
            if (_isProcessingNetworkEvent) return; // Don't echo back
            if (Plugin.Instance?.Network == null || !Plugin.Instance.Network.IsConnected) return;

            try
            {
                // Convert CraterSize enum to byte
                byte craterSizeByte = Convert.ToByte(size);

                // Convert to absolute coordinates
                var absolutePos = FloatingOriginHelper.LocalToAbsolute(position);

                var packet = new CraterSpawnPacket
                {
                    PosX = absolutePos.x,
                    PosY = absolutePos.y,
                    PosZ = absolutePos.z,
                    CraterSize = craterSizeByte
                };

                byte[] data = PacketSerializer.SerializeCraterSpawn(packet);
                Plugin.Instance.Network.SendPacket(PacketType.CraterSpawn, data, reliable: true);

                Plugin.Log?.LogInfo($"[WorldDestruction] Sent crater spawn at ({absolutePos.x:F1}, {absolutePos.y:F1}, {absolutePos.z:F1}) size={craterSizeByte}");

                // For huge craters (nukes from modded weapons), also send an explosion VFX packet.
                // The nuke bypasses Munition.Explode() and Explosion.Trigger() entirely, so this
                // is the only reliable interception point for its visual effect.
                if (craterSizeByte >= CRATER_SIZE_HUGE)
                {
                    ExplosionPatches.SendExplosionSyncForNukeCrater(position);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WorldDestruction] SpawnCraterPostfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle received crater spawn packet.
        /// </summary>
        public static void HandleCraterSpawn(CraterSpawnPacket packet)
        {
            try
            {
                if (_worldCratersType == null || _craterSizeType == null)
                {
                    Plugin.Log?.LogWarning("[WorldDestruction] Cannot spawn crater - types not initialized");
                    return;
                }

                // Convert to local coordinates
                var absolutePos = new Vector3d(packet.PosX, packet.PosY, packet.PosZ);
                Vector3 localPos = FloatingOriginHelper.AbsoluteToLocal(absolutePos);

                // Spawn crater locally
                _isProcessingNetworkEvent = true;
                try
                {
                    // Call WorldCraters2.Instance.SpawnCrater(position, size, false)
                    var instanceField = _worldCratersType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (instanceField != null)
                    {
                        var instance = instanceField.GetValue(null);
                        if (instance != null)
                        {
                            var spawnMethod = _worldCratersType.GetMethod("SpawnCrater", new[] { typeof(Vector3), _craterSizeType, typeof(bool) });
                            if (spawnMethod != null)
                            {
                                // Convert byte to CraterSize enum
                                object craterSize = Enum.ToObject(_craterSizeType, packet.CraterSize);
                                spawnMethod.Invoke(instance, new object[] { localPos, craterSize, false });
                                Plugin.Log?.LogInfo($"[WorldDestruction] Spawned network crater at ({localPos.x:F1}, {localPos.y:F1}, {localPos.z:F1}) size={packet.CraterSize}");
                            }
                        }
                    }
                }
                finally
                {
                    _isProcessingNetworkEvent = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WorldDestruction] HandleCraterSpawn error: {ex.Message}");
            }
        }

        #endregion

        #region Building Sync

        /// <summary>
        /// Postfix on Building.DestroyBuilding to sync building destruction to other clients.
        /// </summary>
        public static void DestroyBuildingPostfix(MonoBehaviour __instance)
        {
            if (_isProcessingNetworkEvent) return; // Don't echo back
            if (Plugin.Instance?.Network == null || !Plugin.Instance.Network.IsConnected) return;

            try
            {
                // Get building position
                Vector3 localPos = __instance.transform.position;
                var absolutePos = FloatingOriginHelper.LocalToAbsolute(localPos);

                var packet = new BuildingDestroyPacket
                {
                    BuildingInstanceId = __instance.GetInstanceID(),
                    PosX = absolutePos.x,
                    PosY = absolutePos.y,
                    PosZ = absolutePos.z
                };

                byte[] data = PacketSerializer.SerializeBuildingDestroy(packet);
                Plugin.Instance.Network.SendPacket(PacketType.BuildingDestroy, data, reliable: true);

                Plugin.Log?.LogInfo($"[WorldDestruction] Sent building destroy at ({absolutePos.x:F1}, {absolutePos.y:F1}, {absolutePos.z:F1})");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WorldDestruction] DestroyBuildingPostfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle received building destroy packet.
        /// </summary>
        public static void HandleBuildingDestroy(BuildingDestroyPacket packet)
        {
            try
            {
                if (_buildingType == null)
                {
                    Plugin.Log?.LogWarning("[WorldDestruction] Cannot destroy building - type not initialized");
                    return;
                }

                // Convert to local coordinates
                var absolutePos = new Vector3d(packet.PosX, packet.PosY, packet.PosZ);
                Vector3 localPos = FloatingOriginHelper.AbsoluteToLocal(absolutePos);

                // Find and destroy the building
                _isProcessingNetworkEvent = true;
                try
                {
                // Find building by position (since InstanceID won't match across clients).
                    // Skip already-destroyed buildings so we always match the correct live one,
                    // even when multiple buildings explode in quick succession near each other.
                    var buildings = UnityEngine.Object.FindObjectsOfType(_buildingType);
                    MonoBehaviour closestBuilding = null;
                    float closestDist = 100f; // Max 100m distance — increased from 50m for robustness

                    foreach (var building in buildings)
                    {
                        var mb = building as MonoBehaviour;
                        if (mb == null) continue;

                        // Skip buildings that are already destroyed — they are no longer valid targets.
                        // Without this check, an already-destroyed building that happens to be the
                        // closest match would "absorb" the packet while the intended building is missed.
                        if (_buildingIsDestroyedProp != null)
                        {
                            try
                            {
                                bool alreadyDestroyed = (bool)_buildingIsDestroyedProp.GetValue(mb);
                                if (alreadyDestroyed) continue;
                            }
                            catch { /* if reflection fails, include the building anyway */ }
                        }

                        float dist = Vector3.Distance(mb.transform.position, localPos);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            closestBuilding = mb;
                        }
                    }

                    if (closestBuilding != null)
                    {
                        // Call DestroyBuilding method
                        var destroyMethod = _buildingType.GetMethod("DestroyBuilding", BindingFlags.Public | BindingFlags.Instance);
                        if (destroyMethod != null)
                        {
                            destroyMethod.Invoke(closestBuilding, null);
                            Plugin.Log?.LogInfo($"[WorldDestruction] Destroyed network building at ({localPos.x:F1}, {localPos.y:F1}, {localPos.z:F1})");
                        }
                    }
                    else
                    {
                        Plugin.Log?.LogInfo($"[WorldDestruction] No building found near ({localPos.x:F1}, {localPos.y:F1}, {localPos.z:F1})");
                    }
                }
                finally
                {
                    _isProcessingNetworkEvent = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[WorldDestruction] HandleBuildingDestroy error: {ex.Message}");
            }
        }

        #endregion
    }
}
