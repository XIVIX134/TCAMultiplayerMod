using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TCAMultiplayer.Networking;
using TCAMultiplayer.Player;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Patches for syncing explosion VFX between players.
    ///
    /// ROOT CAUSE OF BUG:
    ///   Munition.Explode() instantiates VFX prefabs (fireball, smoke, crater) locally.
    ///   The old HandleExplosionSync() called Explosion.Trigger() which does DAMAGE ONLY
    ///   (OverlapSphere + AddExplosionForce) — it never spawns any visual prefabs.
    ///
    /// FIX:
    ///   1. Sender (MunitionExplodePostfix): read private impactLayer/impactMaterial fields
    ///      to classify surface as Air/Ground/Water → include as ImpactSurface in packet.
    ///   2. Receiver (HandleExplosionSync): call SpawnNativeExplosionVfx() which uses
    ///      Resources.Load with known game prefab paths (Effects/Explosion/ExplosionXxxMedium)
    ///      instead of Explosion.Trigger(). Ground impacts also spawn craters.
    ///
    /// Uses manual Harmony patching (same pattern as WorldDestructionPatches)
    /// to avoid type resolution issues.
    /// </summary>
    public static class ExplosionPatches
    {
        private static bool _patchesApplied = false;
        private static Type _munitionType;
        private static Type _explosionType;
        private static Type _damageSourceType;

        // Reflection for reading munition data
        private static PropertyInfo _munitionDataProp;
        private static FieldInfo _munitionOwnshipField;
        private static PropertyInfo _storeDataDisplayNameProp;
        private static PropertyInfo _storeDataNameProp;
        private static FieldInfo _storeDataBlastRadiusField;
        private static PropertyInfo _storeDataBlastRadiusProp;
        private static FieldInfo _storeDataImpactDamageField;
        private static PropertyInfo _storeDataImpactDamageProp;
        private static FieldInfo _storeDataEffectField;
        private static PropertyInfo _storeDataEffectProp;

        // Reflection for reading impact surface from Munition (private fields)
        private static FieldInfo _munitionImpactLayerField;
        private static FieldInfo _munitionImpactMaterialField;

        // WorldCraters2 reflection for spawning craters on received ground explosions
        private static Type _worldCratersType;
        private static PropertyInfo _worldCratersInstanceProp;
        private static FieldInfo _worldCratersInstanceField;
        private static MethodInfo _worldCratersSpawnCraterMethod;
        private static Type _craterSizeType;

        // Guard to prevent echo loops (same pattern as WorldDestructionPatches)
        private static bool _isProcessingNetworkEvent = false;

        // Debounce: prevent duplicate explosion packets for the same munition
        private static int _lastExplodedInstanceId = 0;
        private static float _lastExplodeTime = 0f;
        private const float EXPLODE_DEBOUNCE_SECONDS = 0.1f;

        // Cross-patch dedup: track last explosion position/time sent by EITHER patch
        // so Explosion.Trigger() postfix can skip when MunitionExplodePostfix already handled it
        private static Vector3 _lastSentExplosionLocalPos = Vector3.zero;
        private static float _lastSentExplosionTime = 0f;
        private const float TRIGGER_DEDUP_SECONDS = 0.1f;     // FIX E2: tighter window to avoid deduping legitimate nearby explosions
        private const float TRIGGER_DEDUP_DISTANCE_SQ = 4f;    // FIX E2: 2m squared (was 5m) — only dedup truly same-source explosions

        // Reflection for Explosion component fields (radius, damage, optional effect)
        private static FieldInfo _explosionRadiusField;
        private static FieldInfo _explosionDamageField;
        private static FieldInfo _explosionEffectField;

        // Explosion VFX paths read from WeaponEffectProperties at runtime.
        // Defaults match the game's own defaults (see decompiled WeaponEffectProperties.cs).
        // These are populated via reflection in ApplyPatches() so they track game/mod changes.
        private static string PATH_EXPLOSION_AIR_MEDIUM   = "Effects/Explosion/ExplosionAirMedium";
        private static string PATH_EXPLOSION_AIR_LARGE    = "Effects/Explosion/ExplosionAirLarge";
        private static string PATH_EXPLOSION_GROUND_MEDIUM = "Effects/Explosion/ExplosionGroundMedium";
        private static string PATH_EXPLOSION_GROUND_LARGE  = "Effects/Explosion/ExplosionGroundLarge";
        private static string PATH_EXPLOSION_WATER_MEDIUM  = "Effects/Explosion/ExplosionWaterMedium";
        private static string PATH_EXPLOSION_WATER_LARGE   = "Effects/Explosion/ExplosionWaterLarge";

        // ImpactSurface byte values
        private const byte SURFACE_AIR    = 0;
        private const byte SURFACE_GROUND = 1;
        private const byte SURFACE_WATER  = 2;

        /// <summary>
        /// Apply Harmony patches manually with proper type resolution.
        /// Called from Plugin.Awake() after Harmony is initialized.
        /// </summary>
        public static void ApplyPatches(Harmony harmony)
        {
            if (_patchesApplied) return;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

                // Get Munition type
                _munitionType = ReflectionHelper.GetGameType("Falcon.Stores.Munition");
                if (_munitionType == null)
                {
                    Plugin.Log?.LogWarning("[ExplosionPatches] Munition type not found - explosion sync disabled");
                    return;
                }

                // Get Explosion and DamageSource types (kept for reference, no longer used to trigger)
                _explosionType = ReflectionHelper.GetGameType("Falcon.Damage.Explosion");
                _damageSourceType = ReflectionHelper.GetGameType("Falcon.Damage.DamageSource");

                // Initialize munition data reflection
                _munitionDataProp = _munitionType.GetProperty("Data", flags);
                _munitionOwnshipField = _munitionType.GetField("Ownship", flags);

                // Read private impact surface fields from Munition
                // impactLayer: set to the layer of whatever was hit (-1 = nothing hit = air detonation)
                _munitionImpactLayerField = _munitionType.GetField("impactLayer", flags)
                    ?? _munitionType.GetField("_impactLayer", flags)
                    ?? _munitionType.GetField("m_impactLayer", flags);

                // impactMaterial: PhysicMaterial of the hit surface (null = air)
                _munitionImpactMaterialField = _munitionType.GetField("impactMaterial", flags)
                    ?? _munitionType.GetField("_impactMaterial", flags)
                    ?? _munitionType.GetField("m_impactMaterial", flags);

                Plugin.Log?.LogInfo($"[ExplosionPatches] Munition private fields: " +
                    $"impactLayer={_munitionImpactLayerField != null}, " +
                    $"impactMaterial={_munitionImpactMaterialField != null}");

                // Resolve WorldCraters2 for spawning craters on received ground explosions
                InitializeWorldCratersReflection(flags);

                // Read default explosion VFX paths from WeaponEffectProperties (data-driven)
                InitializeExplosionVfxPaths(flags);
                InitializeWorldCratersReflection(flags);

                // Patch Munition.Explode(Vector3, bool, bool)
                var explodeMethod = _munitionType.GetMethod("Explode", flags,
                    null, new Type[] { typeof(Vector3), typeof(bool), typeof(bool) }, null);

                if (explodeMethod != null)
                {
                    var postfix = typeof(ExplosionPatches).GetMethod("MunitionExplodePostfix",
                        BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(explodeMethod, postfix: new HarmonyMethod(postfix));
                    Plugin.Log?.LogInfo("[ExplosionPatches] Patched Munition.Explode(Vector3, bool, bool)");
                }
                else
                {
                    // Try fallback with any signature
                    explodeMethod = _munitionType.GetMethod("Explode", flags);
                    if (explodeMethod != null)
                    {
                        var postfix = typeof(ExplosionPatches).GetMethod("MunitionExplodePostfix",
                            BindingFlags.Public | BindingFlags.Static);
                        harmony.Patch(explodeMethod, postfix: new HarmonyMethod(postfix));
                        Plugin.Log?.LogInfo($"[ExplosionPatches] Patched Munition.Explode (fallback)");
                    }
                    else
                    {
                        Plugin.Log?.LogWarning("[ExplosionPatches] Munition.Explode method not found!");
                    }
                }

                // Also patch Explosion.Trigger() as a UNIVERSAL FALLBACK for modded weapons
                // (e.g. nukes) that bypass Munition.Explode() entirely.
                // Dedup logic inside ExplosionTriggerPostfix prevents double-sending for
                // vanilla weapons where BOTH methods fire.
                if (_explosionType != null)
                {
                    InitializeExplosionReflection(flags);

                    // Try the no-param overload first (most common form)
                    var triggerMethod = _explosionType.GetMethod("Trigger", flags,
                        null, Type.EmptyTypes, null);
                    if (triggerMethod == null)
                        triggerMethod = _explosionType.GetMethod("Trigger", flags);

                    if (triggerMethod != null)
                    {
                        var triggerPostfix = typeof(ExplosionPatches).GetMethod(
                            "ExplosionTriggerPostfix", BindingFlags.Public | BindingFlags.Static);
                        harmony.Patch(triggerMethod, postfix: new HarmonyMethod(triggerPostfix));
                        Plugin.Log?.LogInfo("[ExplosionPatches] Patched Explosion.Trigger() as universal VFX fallback");
                    }
                    else
                    {
                        Plugin.Log?.LogWarning("[ExplosionPatches] Explosion.Trigger() not found - modded weapon VFX sync may be limited");
                    }
                }

                _patchesApplied = true;
                Plugin.Log?.LogInfo("[ExplosionPatches] Explosion sync patches applied successfully");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[ExplosionPatches] ApplyPatches error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void InitializeWorldCratersReflection(BindingFlags flags)
        {
            try
            {
                _worldCratersType = ReflectionHelper.GetGameType("Falcon.World.WorldCraters2");
                if (_worldCratersType == null) return;

                _worldCratersInstanceProp = _worldCratersType.GetProperty("Instance", flags);
                _worldCratersInstanceField = _worldCratersType.GetField("Instance", flags);
                _worldCratersSpawnCraterMethod = _worldCratersType.GetMethod("SpawnCrater", flags);
                _craterSizeType = ReflectionHelper.GetGameType("Falcon.World.CraterSize");

                Plugin.Log?.LogInfo($"[ExplosionPatches] WorldCraters2 reflection: " +
                    $"Instance={_worldCratersInstanceProp != null || _worldCratersInstanceField != null}, " +
                    $"SpawnCrater={_worldCratersSpawnCraterMethod != null}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ExplosionPatches] WorldCraters2 reflection init failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Read default explosion VFX paths from WeaponEffectProperties.
        /// Creates a default instance and reads its Air/Ground/Water fields.
        /// This ensures paths track game updates and mod overrides rather than being hardcoded.
        /// Falls back to the compiled-in defaults if reflection fails.
        /// </summary>
        private static void InitializeExplosionVfxPaths(BindingFlags flags)
        {
            try
            {
                var wepType = ReflectionHelper.GetGameType("Falcon.Stores.WeaponEffectProperties");
                if (wepType == null) return;

                // Create a default instance to read the game's default field values
                var defaults = Activator.CreateInstance(wepType);
                if (defaults == null) return;

                string ReadStringField(string fieldName)
                {
                    var field = wepType.GetField(fieldName, flags);
                    return field?.GetValue(defaults) as string;
                }

                // Read default paths — these are the same fields used in Munition.Explode()
                string air = ReadStringField("Air");
                string ground = ReadStringField("Ground");
                string water = ReadStringField("Water");

                if (!string.IsNullOrEmpty(air))
                {
                    PATH_EXPLOSION_AIR_MEDIUM = air;
                    // Derive "Large" variant by replacing "Medium" if present
                    PATH_EXPLOSION_AIR_LARGE = air.Replace("Medium", "Large");
                }
                if (!string.IsNullOrEmpty(ground))
                {
                    PATH_EXPLOSION_GROUND_MEDIUM = ground;
                    PATH_EXPLOSION_GROUND_LARGE = ground.Replace("Medium", "Large");
                }
                if (!string.IsNullOrEmpty(water))
                {
                    PATH_EXPLOSION_WATER_MEDIUM = water;
                    PATH_EXPLOSION_WATER_LARGE = water.Replace("Medium", "Large");
                }

                Plugin.Log?.LogInfo($"[ExplosionPatches] VFX paths from game data: Air='{PATH_EXPLOSION_AIR_MEDIUM}', Ground='{PATH_EXPLOSION_GROUND_MEDIUM}', Water='{PATH_EXPLOSION_WATER_MEDIUM}'");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ExplosionPatches] InitializeExplosionVfxPaths error (using defaults): {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize reflection for Explosion component fields (radius, damage, optional VFX prefab).
        /// Called once from ApplyPatches().
        /// </summary>
        private static void InitializeExplosionReflection(BindingFlags flags)
        {
            if (_explosionType == null) return;
            try
            {
                // radius — blast radius used for explosion damage falloff
                _explosionRadiusField = _explosionType.GetField("radius", flags)
                    ?? _explosionType.GetField("Radius", flags)
                    ?? _explosionType.GetField("_radius", flags)
                    ?? _explosionType.GetField("blastRadius", flags);

                // damage — base damage of the explosion
                _explosionDamageField = _explosionType.GetField("damage", flags)
                    ?? _explosionType.GetField("Damage", flags)
                    ?? _explosionType.GetField("_damage", flags)
                    ?? _explosionType.GetField("impactDamage", flags);

                // effect — optional VFX prefab stored on the Explosion component (for modded explosions)
                _explosionEffectField = _explosionType.GetField("effect", flags)
                    ?? _explosionType.GetField("Effect", flags)
                    ?? _explosionType.GetField("explosionEffect", flags)
                    ?? _explosionType.GetField("ExplosionEffect", flags)
                    ?? _explosionType.GetField("vfxPrefab", flags);

                Plugin.Log?.LogInfo($"[ExplosionPatches] Explosion reflection: " +
                    $"radius={_explosionRadiusField?.Name ?? "null"}, " +
                    $"damage={_explosionDamageField?.Name ?? "null"}, " +
                    $"effect={_explosionEffectField?.Name ?? "null"}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ExplosionPatches] InitializeExplosionReflection error: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize StoreData reflection lazily on first explosion.
        /// </summary>
        private static void InitializeStoreDataReflection(object storeData)
        {
            if (storeData == null) return;
            if (_storeDataDisplayNameProp != null || _storeDataNameProp != null) return;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var dataType = storeData.GetType();

                _storeDataDisplayNameProp = dataType.GetProperty("DisplayName", flags);
                _storeDataNameProp = dataType.GetProperty("Name", flags);

                _storeDataBlastRadiusProp = dataType.GetProperty("BlastRadius", flags);
                _storeDataBlastRadiusField = dataType.GetField("BlastRadius", flags)
                    ?? dataType.GetField("blastRadius", flags)
                    ?? dataType.GetField("_blastRadius", flags);

                _storeDataImpactDamageProp = dataType.GetProperty("ImpactDamage", flags);
                _storeDataImpactDamageField = dataType.GetField("ImpactDamage", flags)
                    ?? dataType.GetField("impactDamage", flags)
                    ?? dataType.GetField("_impactDamage", flags);

                _storeDataEffectProp = dataType.GetProperty("Effect", flags)
                    ?? dataType.GetProperty("ExplosionEffect", flags)
                    ?? dataType.GetProperty("EffectPrefab", flags);
                _storeDataEffectField = dataType.GetField("Effect", flags)
                    ?? dataType.GetField("effect", flags)
                    ?? dataType.GetField("ExplosionEffect", flags)
                    ?? dataType.GetField("explosionEffect", flags);

                Plugin.Log?.LogInfo($"[ExplosionPatches] StoreData reflection initialized for {dataType.Name}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ExplosionPatches] StoreData reflection init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Determine ImpactSurface from the Munition's private impactLayer and impactMaterial.
        /// Returns SURFACE_AIR (0), SURFACE_GROUND (1), or SURFACE_WATER (2).
        /// </summary>
        private static byte DetermineImpactSurface(object munitionInstance, bool isAirburst)
        {
            // Airburst = explicitly mid-air
            if (isAirburst) return SURFACE_AIR;

            // Try impactMaterial first — most reliable indicator
            if (_munitionImpactMaterialField != null)
            {
                try
                {
                    var mat = _munitionImpactMaterialField.GetValue(munitionInstance) as PhysicMaterial;
                    if (mat != null)
                    {
                        string matName = mat.name.ToLowerInvariant();
                        if (matName.Contains("water") || matName.Contains("ocean") || matName.Contains("sea"))
                            return SURFACE_WATER;
                        if (matName.Contains("air"))
                            return SURFACE_AIR;
                        // Any other material = ground (terrain, concrete, sand, etc.)
                        return SURFACE_GROUND;
                    }
                    // null material = nothing was hit = air detonation (e.g. proximity fuse)
                }
                catch { /* ignore */ }
            }

            // Try impactLayer as fallback
            if (_munitionImpactLayerField != null)
            {
                try
                {
                    var layerObj = _munitionImpactLayerField.GetValue(munitionInstance);
                    if (layerObj != null)
                    {
                        int layer = Convert.ToInt32(layerObj);
                        if (layer >= 0)
                        {
                            string layerName = LayerMask.LayerToName(layer).ToLowerInvariant();
                            Plugin.Log?.LogInfo($"[ExplosionPatches] impactLayer={layer} name='{layerName}'");

                            if (layerName.Contains("water"))
                                return SURFACE_WATER;
                            if (layerName.Contains("terrain") || layerName.Contains("ground") ||
                                layerName.Contains("default") || layerName.Contains("building") ||
                                layerName.Contains("static"))
                                return SURFACE_GROUND;
                            // -1 = no impact layer set = air detonation
                            if (layer == -1)
                                return SURFACE_AIR;
                            // Any positive layer we hit something solid = ground by default
                            return SURFACE_GROUND;
                        }
                    }
                }
                catch { /* ignore */ }
            }

            // Default: if we can't determine, assume air (missiles that hit aircraft)
            return SURFACE_AIR;
        }

        /// <summary>
        /// Postfix on Munition.Explode to send explosion data to remote player.
        /// Fires AFTER the explosion happens locally.
        /// </summary>
        public static void MunitionExplodePostfix(object __instance, Vector3 impactPoint, bool inflictDamage, bool isAirburst)
        {
            if (_isProcessingNetworkEvent) return;
            if (Plugin.Instance?.Network == null || !Plugin.Instance.Network.IsConnected) return;

            try
            {
                var munition = __instance as MonoBehaviour;
                if (munition == null) return;

                // Debounce duplicate Explode calls on same munition
                int instanceId = munition.GetInstanceID();
                if (instanceId == _lastExplodedInstanceId &&
                    Time.time - _lastExplodeTime < EXPLODE_DEBOUNCE_SECONDS)
                {
                    return;
                }
                _lastExplodedInstanceId = instanceId;
                _lastExplodeTime = Time.time;

                // Skip network munitions (MP_ prefix)
                if (munition.gameObject.name.StartsWith("MP_")) return;

                // Skip munitions belonging to remote aircraft or AI/NPC aircraft.
                // Only sync explosions from the LOCAL player's own weapons.
                if (_munitionOwnshipField != null)
                {
                    var ownship = _munitionOwnshipField.GetValue(__instance);
                    if (ownship != null)
                    {
                        var ownshipComp = ownship as Component;
                        if (ownshipComp != null)
                        {
                            // Skip remote aircraft munitions
                            var remoteCtrl = ownshipComp.GetComponentInParent<RemoteAircraftController>();
                            if (remoteCtrl != null) return;

                            // Skip AI/NPC munitions — only sync the local player's own weapons.
                            // Without this, ALL game AI explosions (SAMs, enemy aircraft bombs, etc.)
                            // get synced as explosion packets, causing huge VFX on the other player's screen.
                            var localPlayer = Falcon.UniversalAircraft.UniAircraft.Player;
                            if (localPlayer != null)
                            {
                                var munitionAircraft = ownshipComp.GetComponentInParent<Falcon.UniversalAircraft.UniAircraft>();
                                if (munitionAircraft != localPlayer) return;
                            }
                        }
                    }
                    else
                    {
                        // No ownship = orphaned munition (SAM, ground weapon, etc.) — skip
                        return;
                    }
                }

                // Gather explosion parameters
                string weaponName = munition.gameObject.name;
                float blastRadius = 50f;
                int impactDamage = 500;
                string effectPath = "";

                if (_munitionDataProp != null)
                {
                    var data = _munitionDataProp.GetValue(__instance);
                    if (data != null)
                    {
                        InitializeStoreDataReflection(data);

                        if (_storeDataDisplayNameProp != null)
                            weaponName = _storeDataDisplayNameProp.GetValue(data) as string ?? weaponName;
                        else if (_storeDataNameProp != null)
                            weaponName = _storeDataNameProp.GetValue(data) as string ?? weaponName;

                        if (_storeDataBlastRadiusProp != null)
                            blastRadius = Convert.ToSingle(_storeDataBlastRadiusProp.GetValue(data));
                        else if (_storeDataBlastRadiusField != null)
                            blastRadius = Convert.ToSingle(_storeDataBlastRadiusField.GetValue(data));

                        if (_storeDataImpactDamageProp != null)
                            impactDamage = Convert.ToInt32(_storeDataImpactDamageProp.GetValue(data));
                        else if (_storeDataImpactDamageField != null)
                            impactDamage = Convert.ToInt32(_storeDataImpactDamageField.GetValue(data));

                        if (_storeDataEffectProp != null)
                            effectPath = ExtractEffectPath(_storeDataEffectProp.GetValue(data));
                        else if (_storeDataEffectField != null)
                            effectPath = ExtractEffectPath(_storeDataEffectField.GetValue(data));
                    }
                }

                // Classify explosion type by blast radius
                byte explosionType;
                if (blastRadius > 500f)
                    explosionType = 2; // Nuke
                else if (blastRadius > 100f)
                    explosionType = 1; // Large bomb
                else
                    explosionType = 0; // Standard

                // DEDUP: Skip if SendExplosionSyncForNukeCrater already sent for this position
                // (crater postfix fires DURING Explode() before this postfix runs)
                float timeSinceLastSend = Time.time - _lastSentExplosionTime;
                float dedupDistSq = (impactPoint - _lastSentExplosionLocalPos).sqrMagnitude;
                if (timeSinceLastSend < TRIGGER_DEDUP_SECONDS && dedupDistSq < TRIGGER_DEDUP_DISTANCE_SQ)
                {
                    Plugin.Log?.LogInfo($"[ExplosionPatches] MunitionExplodePostfix: dedup skip " +
                        $"(NukeCrater handled it {timeSinceLastSend*1000:F0}ms ago, dist={Mathf.Sqrt(dedupDistSq):F1}m)");
                    return;
                }

                // Determine impact surface (air / ground / water)
                byte impactSurface = DetermineImpactSurface(__instance, isAirburst);

                Plugin.Log?.LogInfo($"[ExplosionPatches] Sending explosion: {weaponName} surface={impactSurface} " +
                    $"isAirburst={isAirburst} radius={blastRadius} type={explosionType}");

                // Convert to absolute coordinates
                var absolutePos = FloatingOriginHelper.LocalToAbsolute(impactPoint);

                var packet = new ExplosionSyncPacket
                {
                    ShooterId = Plugin.Instance.Network.LocalPeerId,
                    PosX = absolutePos.x,
                    PosY = absolutePos.y,
                    PosZ = absolutePos.z,
                    BlastRadius = blastRadius,
                    ImpactDamage = impactDamage,
                    WeaponName = weaponName,
                    EffectPath = effectPath,
                    ExplosionType = explosionType,
                    ImpactSurface = impactSurface
                };

                byte[] packetData = PacketSerializer.SerializeExplosionSync(packet);
                Plugin.Instance.Network.SendPacket(PacketType.ExplosionSync, packetData, reliable: true);

                // Record this send so ExplosionTriggerPostfix can dedup and skip
                _lastSentExplosionLocalPos = impactPoint;
                _lastSentExplosionTime = Time.time;

                Plugin.Log?.LogInfo($"[ExplosionPatches] Sent explosion (Munition): {weaponName} at " +
                    $"({absolutePos.x:F0},{absolutePos.y:F0},{absolutePos.z:F0}) " +
                    $"surface={impactSurface} radius={blastRadius:F0} type={explosionType}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[ExplosionPatches] MunitionExplodePostfix error: {ex.Message}");
            }
        }

        private static string ExtractEffectPath(object effectValue)
        {
            if (effectValue == null) return "";
            if (effectValue is string s) return s;

            UnityEngine.Object unityObj = null;
            try
            {
                if (effectValue is GameObject go)         unityObj = go;
                else if (effectValue is Component comp)   unityObj = comp.gameObject;
                else
                {
                    string str = effectValue.ToString();
                    if (!string.IsNullOrEmpty(str) && str != effectValue.GetType().FullName)
                        return str;
                }
            }
            catch { }

            if (unityObj == null) return "";

            // Use UniversalAssetFinder to get the bundle-qualified identifier when possible
            // ("bundleName:assetName" for mod assets, plain name for vanilla)
            return Networking.UniversalAssetFinder.GetIdentifier(unityObj);
        }

        /// <summary>
        /// Handle received explosion sync packet — spawn VFX at the explosion position.
        ///
        /// FIXED: now uses Resources.Load with known game prefab paths instead of
        /// Explosion.Trigger() which only does damage physics, not visual effects.
        /// </summary>
        public static void HandleExplosionSync(ExplosionSyncPacket packet)
        {
            try
            {
                var absolutePos = new Vector3d(packet.PosX, packet.PosY, packet.PosZ);
                Vector3 localPos = FloatingOriginHelper.AbsoluteToLocal(absolutePos);

                Plugin.Log?.LogInfo($"[ExplosionPatches] Received explosion: {packet.WeaponName} " +
                    $"surface={packet.ImpactSurface} type={packet.ExplosionType} " +
                    $"at ({localPos.x:F0},{localPos.y:F0},{localPos.z:F0}) radius={packet.BlastRadius:F0}");

                _isProcessingNetworkEvent = true;
                try
                {
                    bool spawned = false;

                    // PRIMARY: If the weapon has a custom effect path (modded or vanilla named effect),
                    // try that FIRST via UniversalAssetFinder — covers both mod AssetBundles and Resources.
                    // Modded weapons like nukes, special bombs etc. must use THEIR OWN effect, not a
                    // generic vanilla explosion.
                    if (!string.IsNullOrEmpty(packet.EffectPath))
                    {
                        spawned = TrySpawnModdedExplosion(localPos, packet);
                    }

                    // SECONDARY: Vanilla game prefabs via known resource paths.
                    // Used when no custom EffectPath is set (vanilla weapons) or modded lookup failed.
                    if (!spawned)
                    {
                        spawned = SpawnNativeExplosionVfx(localPos, packet);
                    }

                    // FALLBACK: CombatVfxManager (always produces a visual, regardless of asset availability)
                    if (!spawned)
                    {
                        SpawnFallbackExplosion(localPos, packet);
                    }

                    // For ground explosions: also spawn a crater (same logic as Munition.Explode)
                    if (packet.ImpactSurface == SURFACE_GROUND)
                    {
                        TrySpawnCraterForExplosion(localPos, packet);
                    }
                }
                finally
                {
                    _isProcessingNetworkEvent = false;
                }
            }
            catch (Exception ex)
            {
                _isProcessingNetworkEvent = false;
                Plugin.Log?.LogError($"[ExplosionPatches] HandleExplosionSync error: {ex.Message}");
            }
        }

        /// <summary>
        /// Spawn explosion VFX using the game's native prefab resource paths.
        /// This replicates what Munition.Explode() does: load the correct prefab and instantiate it.
        ///
        /// WeaponEffectProperties defaults:
        ///   Air    = "Effects/Explosion/ExplosionAirMedium"
        ///   Ground = "Effects/Explosion/ExplosionGroundMedium"
        ///   Water  = "Effects/Explosion/ExplosionWaterMedium"
        ///
        /// Returns true if a prefab was successfully instantiated.
        /// </summary>
        private static bool SpawnNativeExplosionVfx(Vector3 position, ExplosionSyncPacket packet)
        {
            try
            {
                bool isLarge = packet.ExplosionType >= 1;
                GameObject prefab = null;

                switch (packet.ImpactSurface)
                {
                    case SURFACE_WATER:
                        if (isLarge)
                            prefab = Resources.Load<GameObject>(PATH_EXPLOSION_WATER_LARGE);
                        if (prefab == null)
                            prefab = Resources.Load<GameObject>(PATH_EXPLOSION_WATER_MEDIUM);
                        break;

                    case SURFACE_GROUND:
                        if (isLarge)
                            prefab = Resources.Load<GameObject>(PATH_EXPLOSION_GROUND_LARGE);
                        if (prefab == null)
                            prefab = Resources.Load<GameObject>(PATH_EXPLOSION_GROUND_MEDIUM);
                        break;

                    default: // SURFACE_AIR — mid-air detonation (missile hit, airburst bomb)
                        if (isLarge)
                            prefab = Resources.Load<GameObject>(PATH_EXPLOSION_AIR_LARGE);
                        if (prefab == null)
                            prefab = Resources.Load<GameObject>(PATH_EXPLOSION_AIR_MEDIUM);
                        break;
                }

                if (prefab != null)
                {
                    var instance = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);

                    // Scale for massive explosions (nukes)
                    if (packet.ExplosionType == 2)
                    {
                        float scale = Mathf.Clamp(packet.BlastRadius / 300f, 1f, 8f);
                        instance.transform.localScale *= scale;
                    }

                    float lifetime = packet.ExplosionType == 2 ? 30f : 12f;
                    UnityEngine.Object.Destroy(instance, lifetime);

                    Plugin.Log?.LogInfo($"[ExplosionPatches] Spawned native VFX: {prefab.name} " +
                        $"surface={packet.ImpactSurface} type={packet.ExplosionType} at {position}");
                    return true;
                }

                Plugin.Log?.LogWarning($"[ExplosionPatches] Native prefab not found for surface={packet.ImpactSurface} large={isLarge}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ExplosionPatches] SpawnNativeExplosionVfx failed: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Try to spawn a crater at the ground explosion position.
        /// Mirrors the logic in Munition.Explode() → WorldCraters2.Instance.SpawnCrater().
        /// Guard: _isProcessingNetworkEvent prevents WorldDestructionPatches from re-broadcasting.
        /// </summary>
        private static void TrySpawnCraterForExplosion(Vector3 position, ExplosionSyncPacket packet)
        {
            if (_worldCratersType == null || _worldCratersSpawnCraterMethod == null) return;

            try
            {
                // Get WorldCraters2.Instance
                object instance = null;
                if (_worldCratersInstanceProp != null)
                    instance = _worldCratersInstanceProp.GetValue(null);
                else if (_worldCratersInstanceField != null)
                    instance = _worldCratersInstanceField.GetValue(null);

                if (instance == null) return;

                // Choose crater size based on blast radius
                // CraterSize enum: Small=0, Medium=1, Large=2, Huge=3, Aircraft=4
                object craterSize = null;
                if (_craterSizeType != null)
                {
                    if (packet.ExplosionType == 2)         // Nuke
                        craterSize = Enum.ToObject(_craterSizeType, 3); // Huge
                    else if (packet.ExplosionType == 1 || packet.BlastRadius > 50f) // Large bomb
                        craterSize = Enum.ToObject(_craterSizeType, 2); // Large
                    else
                        craterSize = Enum.ToObject(_craterSizeType, 1); // Medium
                }
                else
                {
                    craterSize = 2; // Large as raw int fallback
                }

                // SpawnCrater(Vector3 position, CraterSize size, bool forceGroundClamp)
                // Guard: tell WorldDestructionPatches this is a network-driven call so its
                // SpawnCraterPostfix does NOT echo a CraterSpawn packet back to the HOST.
                WorldDestructionPatches.SetProcessingFlag(true);
                try
                {
                    _worldCratersSpawnCraterMethod.Invoke(instance, new object[] { position, craterSize, false });
                }
                finally
                {
                    WorldDestructionPatches.SetProcessingFlag(false);
                }
                Plugin.Log?.LogInfo($"[ExplosionPatches] Spawned crater at {position} size={craterSize}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ExplosionPatches] TrySpawnCraterForExplosion failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Spawn the weapon's custom explosion effect using UniversalAssetFinder.
        ///
        /// Handles all sources:
        ///   - Mod AssetBundle assets   ("bundleName:assetName" format)
        ///   - Vanilla Resources paths  ("Effects/Explosion/Foo")
        ///   - Bare names searched across all loaded bundles and memory
        ///
        /// Called FIRST when EffectPath is present so modded weapons always use their own
        /// visual (e.g. nuke mushroom cloud, custom fire effect) rather than the generic
        /// vanilla ExplosionGroundLarge/AirMedium etc.
        /// </summary>
        private static bool TrySpawnModdedExplosion(Vector3 position, ExplosionSyncPacket packet)
        {
            try
            {
                if (string.IsNullOrEmpty(packet.EffectPath)) return false;

                // UniversalAssetFinder handles: cache → bundle-qualified → Resources → all bundles → in-memory scan
                var prefab = UniversalAssetFinder.Find<GameObject>(packet.EffectPath);

                // Also try as ParticleSystem if GameObject lookup failed (some mods store PS prefabs directly)
                ParticleSystem psPrefab = null;
                if (prefab == null)
                    psPrefab = UniversalAssetFinder.Find<ParticleSystem>(packet.EffectPath);

                if (prefab != null)
                {
                    var instance = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
                    instance.SetActive(true);

                    if (packet.ExplosionType == 2)
                    {
                        float scale = Mathf.Clamp(packet.BlastRadius / 500f, 1f, 10f);
                        instance.transform.localScale *= scale;
                    }

                    float lifetime = packet.ExplosionType == 2 ? 30f : 10f;
                    UnityEngine.Object.Destroy(instance, lifetime);

                    Plugin.Log?.LogInfo($"[ExplosionPatches] Spawned modded explosion (GO): {packet.EffectPath} at {position}");
                    return true;
                }

                if (psPrefab != null)
                {
                    var instance = UnityEngine.Object.Instantiate(psPrefab, position, Quaternion.identity);
                    instance.gameObject.SetActive(true);

                    if (packet.ExplosionType == 2)
                    {
                        float scale = Mathf.Clamp(packet.BlastRadius / 500f, 1f, 10f);
                        instance.transform.localScale *= scale;
                    }

                    float lifetime = packet.ExplosionType == 2 ? 30f : 10f;
                    UnityEngine.Object.Destroy(instance.gameObject, lifetime);

                    Plugin.Log?.LogInfo($"[ExplosionPatches] Spawned modded explosion (PS): {packet.EffectPath} at {position}");
                    return true;
                }

                Plugin.Log?.LogWarning($"[ExplosionPatches] Modded effect not found via UniversalAssetFinder: '{packet.EffectPath}'");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ExplosionPatches] TrySpawnModdedExplosion failed: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Send an explosion sync packet triggered by a Huge crater spawn.
        /// Used for modded weapons (e.g. nukes) that bypass both Munition.Explode()
        /// and Explosion.Trigger(), but DO call WorldCraters2.SpawnCrater with size=Huge.
        /// Called from WorldDestructionPatches.SpawnCraterPostfix.
        /// </summary>
        public static void SendExplosionSyncForNukeCrater(Vector3 localPosition)
        {
            if (_isProcessingNetworkEvent) return;
            if (Plugin.Instance?.Network == null || !Plugin.Instance.Network.IsConnected) return;
            try
            {
                // Dedup: if MunitionExplodePostfix or ExplosionTriggerPostfix already sent nearby, skip
                float timeSinceLastSend = Time.time - _lastSentExplosionTime;
                float distSq = (localPosition - _lastSentExplosionLocalPos).sqrMagnitude;
                if (timeSinceLastSend < TRIGGER_DEDUP_SECONDS && distSq < TRIGGER_DEDUP_DISTANCE_SQ)
                {
                    Plugin.Log?.LogInfo($"[ExplosionPatches] SendExplosionSyncForNukeCrater: dedup skip ({timeSinceLastSend * 1000:F0}ms ago)");
                    return;
                }

                var absolutePos = FloatingOriginHelper.LocalToAbsolute(localPosition);
                // Use the effect path cached when the bomb was dropped (WeaponPatches reads
                // StoreData.Effects.Ground at launch time).  Falls back to "" if the bomb
                // was not recognised as a nuke (rare), in which case CLIENT falls through to
                // SpawnNativeExplosionVfx with the scaled-up generic ground explosion.
                string effectPath = WeaponPatches.LastNukeGroundEffectPath ?? "";

                var packet = new ExplosionSyncPacket
                {
                    ShooterId    = Plugin.Instance.Network.LocalPeerId,
                    PosX         = absolutePos.x,
                    PosY         = absolutePos.y,
                    PosZ         = absolutePos.z,
                    BlastRadius  = 1500f,
                    ImpactDamage = 50000,
                    WeaponName   = "Nuke",
                    EffectPath   = effectPath,
                    ExplosionType = 2,       // nuke / massive
                    ImpactSurface = SURFACE_GROUND
                };

                byte[] packetData = PacketSerializer.SerializeExplosionSync(packet);
                Plugin.Instance.Network.SendPacket(PacketType.ExplosionSync, packetData, reliable: true);

                _lastSentExplosionLocalPos = localPosition;
                _lastSentExplosionTime = Time.time;

                Plugin.Log?.LogInfo($"[ExplosionPatches] Sent nuke explosion (via huge crater) at " +
                    $"({absolutePos.x:F0},{absolutePos.y:F0},{absolutePos.z:F0})");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[ExplosionPatches] SendExplosionSyncForNukeCrater error: {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback: spawn explosion using CombatVfxManager.
        /// Used when native prefabs can't be loaded (modded game, wrong paths, etc.).
        /// </summary>
        private static void SpawnFallbackExplosion(Vector3 position, ExplosionSyncPacket packet)
        {
            try
            {
                int scaledDamage = packet.ImpactDamage;

                if (packet.ExplosionType == 2)
                    scaledDamage = Math.Max(scaledDamage, 10000);
                else if (packet.ExplosionType == 1)
                    scaledDamage = Math.Max(scaledDamage, 1000);

                CombatVfxManager.SpawnExplosion(position, packet.BlastRadius, scaledDamage);
                Plugin.Log?.LogInfo($"[ExplosionPatches] Spawned fallback explosion: {packet.WeaponName} at {position}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[ExplosionPatches] SpawnFallbackExplosion error: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // UNIVERSAL FALLBACK: Explosion.Trigger() postfix
        //
        // Catches ALL explosions — including modded ones (e.g. nuke from Tiny Weapon Shop)
        // that bypass Munition.Explode() entirely.  Fires AFTER Explosion.Trigger() runs.
        //
        // For vanilla weapons, MunitionExplodePostfix runs first and records the position.
        // This postfix then sees a recent nearby send and deduplicates (skips) itself.
        // For modded weapons that skip Munition.Explode(), only this postfix fires → synced.
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Postfix on Explosion.Trigger() — universal VFX sync fallback for ALL explosions.
        ///
        /// Dedup: if MunitionExplodePostfix already sent a packet within TRIGGER_DEDUP_SECONDS
        /// and within sqrt(TRIGGER_DEDUP_DISTANCE_SQ) meters, skip to avoid double-sending.
        /// </summary>
        public static void ExplosionTriggerPostfix(object __instance)
        {
            if (_isProcessingNetworkEvent) return;
            if (Plugin.Instance?.Network == null || !Plugin.Instance.Network.IsConnected) return;

            try
            {
                var explosion = __instance as MonoBehaviour;
                if (explosion == null) return;

                Vector3 position = explosion.transform.position;

                // DEDUP: Skip if MunitionExplodePostfix already sent for this explosion location
                float timeSinceLastSend = Time.time - _lastSentExplosionTime;
                float distSq = (position - _lastSentExplosionLocalPos).sqrMagnitude;
                if (timeSinceLastSend < TRIGGER_DEDUP_SECONDS && distSq < TRIGGER_DEDUP_DISTANCE_SQ)
                {
                    Plugin.Log?.LogInfo($"[ExplosionPatches] ExplosionTriggerPostfix: dedup skip " +
                        $"(MunitionExplode handled it {timeSinceLastSend*1000:F0}ms ago, dist={Mathf.Sqrt(distSq):F1}m)");
                    return;
                }

                // Skip if this Explosion component is on or inside a remote clone
                var remoteCtrl = explosion.GetComponentInParent<RemoteAircraftController>();
                if (remoteCtrl != null) return;

                // Also skip if this Explosion is a child of any MP_-named object
                var topParent = explosion.transform.root;
                if (topParent != null && topParent.name.StartsWith("MP_")) return;

                // Read blast radius from the Explosion component
                float blastRadius = 50f;
                if (_explosionRadiusField != null)
                {
                    try { blastRadius = Convert.ToSingle(_explosionRadiusField.GetValue(__instance)); }
                    catch { /* use default */ }
                }

                // Read damage from the Explosion component
                int impactDamage = 500;
                if (_explosionDamageField != null)
                {
                    try { impactDamage = Convert.ToInt32(_explosionDamageField.GetValue(__instance)); }
                    catch { /* use default */ }
                }

                // Read optional effect prefab from the Explosion component (modded weapons may set this)
                string effectPath = "";
                if (_explosionEffectField != null)
                {
                    try
                    {
                        var effectVal = _explosionEffectField.GetValue(__instance);
                        if (effectVal != null)
                            effectPath = ExtractEffectPath(effectVal);
                    }
                    catch { /* use empty */ }
                }

                // Classify explosion type from blast radius
                byte explosionType;
                if (blastRadius > 500f)
                    explosionType = 2; // Nuke / massive
                else if (blastRadius > 100f)
                    explosionType = 1; // Large bomb
                else
                    explosionType = 0; // Standard

                // Determine impact surface via raycast (we don't have impactLayer here)
                byte impactSurface = DetermineImpactSurfaceFromPosition(position);

                // Weapon name: use the GameObject name of the Explosion component's owner,
                // fall back to the Explosion's own name
                string weaponName = explosion.gameObject.name;
                // Try to get parent's name for better context (often "Bomb_RN28(Clone)" etc.)
                if (explosion.transform.parent != null)
                {
                    string parentName = explosion.transform.parent.gameObject.name;
                    if (!string.IsNullOrEmpty(parentName) && !parentName.StartsWith("MP_"))
                        weaponName = parentName;
                }

                Plugin.Log?.LogInfo($"[ExplosionPatches] ExplosionTriggerPostfix: modded/uncaught explosion " +
                    $"'{weaponName}' pos={position} radius={blastRadius} surface={impactSurface} type={explosionType}" +
                    (string.IsNullOrEmpty(effectPath) ? "" : $" effect='{effectPath}'"));

                var absolutePos = FloatingOriginHelper.LocalToAbsolute(position);

                var packet = new ExplosionSyncPacket
                {
                    ShooterId = Plugin.Instance.Network.LocalPeerId,
                    PosX = absolutePos.x,
                    PosY = absolutePos.y,
                    PosZ = absolutePos.z,
                    BlastRadius = blastRadius,
                    ImpactDamage = impactDamage,
                    WeaponName = weaponName,
                    EffectPath = effectPath,
                    ExplosionType = explosionType,
                    ImpactSurface = impactSurface
                };

                byte[] packetData = PacketSerializer.SerializeExplosionSync(packet);
                Plugin.Instance.Network.SendPacket(PacketType.ExplosionSync, packetData, reliable: true);

                // Record this send for future dedup
                _lastSentExplosionLocalPos = position;
                _lastSentExplosionTime = Time.time;

                Plugin.Log?.LogInfo($"[ExplosionPatches] Sent explosion (Trigger fallback): '{weaponName}' at " +
                    $"({absolutePos.x:F0},{absolutePos.y:F0},{absolutePos.z:F0}) " +
                    $"surface={impactSurface} radius={blastRadius:F0} type={explosionType}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[ExplosionPatches] ExplosionTriggerPostfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Determine impact surface type from a world position using a downward Physics.Raycast.
        /// Used by ExplosionTriggerPostfix when we don't have the Munition's impactLayer/impactMaterial.
        ///
        /// Strategy:
        ///   1. Cast a short ray upward from just below position (catches explosions AT ground level)
        ///   2. Fall through to a longer downward ray from above position
        ///   3. Check hit collider's physics material name and layer name
        ///   4. If no ground hit found: check if position.y is below sea level (≈ 0) → water
        ///   5. Default: air (missile detonated in flight)
        /// </summary>
        private static byte DetermineImpactSurfaceFromPosition(Vector3 position)
        {
            const float RAY_DOWN_DIST = 15f;  // Ray downward to find ground near explosion
            const float RAY_START_UP = 5f;    // Start slightly above to avoid being inside terrain

            try
            {
                Vector3 rayStart = position + Vector3.up * RAY_START_UP;

                if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, RAY_DOWN_DIST + RAY_START_UP))
                {
                    // Check physics material of the hit surface
                    if (hit.collider.sharedMaterial != null)
                    {
                        string matName = hit.collider.sharedMaterial.name.ToLowerInvariant();
                        if (matName.Contains("water") || matName.Contains("ocean") || matName.Contains("sea"))
                            return SURFACE_WATER;
                        if (!matName.Contains("air"))
                            return SURFACE_GROUND; // any solid material → ground
                    }

                    // Check layer
                    string layerName = LayerMask.LayerToName(hit.collider.gameObject.layer).ToLowerInvariant();
                    if (layerName.Contains("water"))
                        return SURFACE_WATER;
                    if (layerName.Contains("terrain") || layerName.Contains("ground") ||
                        layerName.Contains("default") || layerName.Contains("static"))
                        return SURFACE_GROUND;

                    // Something was hit at close range — assume ground
                    return SURFACE_GROUND;
                }

                // No ground hit within RAY_DOWN_DIST — check altitude as a water indicator
                // Sea level in TCA is approximately y ≤ 2 units
                if (position.y <= 2f)
                    return SURFACE_WATER;

                // High altitude → missile detonated in air (proximity or aircraft hit)
                return SURFACE_AIR;
            }
            catch
            {
                // Physics errors are non-fatal — default to air
                return SURFACE_AIR;
            }
        }
    }
}
