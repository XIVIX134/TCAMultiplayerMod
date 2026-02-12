using System;
using System.Collections.Generic;
using UnityEngine;

namespace TCAMultiplayer.Player
{
    /// <summary>
    /// Manages combat visual effects (impacts, tracers, explosions) for networked combat.
    /// Uses the game's existing VFX systems when available, with fallbacks for reliability.
    /// </summary>
    public static class CombatVfxManager
    {
        // Cached effect prefabs from the game
        private static Dictionary<string, GameObject> _cachedEffects = new Dictionary<string, GameObject>();
        private static bool _effectsCached = false;
        
        // Real FX names from game JSON (Weapons/Bullets/Aircraft.json)
        // The game's Bullet2Manager.SpawnedEffects dictionary uses these as keys.
        // Format: "HitAC", "ExplosionAC", "GrassAC", "WaterAC", "PenetrateAC", etc.
        private static readonly string[] HitEffectNames = {
            "HitAC", "HitAC_AP", "Hit", "BulletHit_Metal", "BulletHit_Hard"
        };
        
        private static readonly string[] GroundEffectNames = {
            "GrassAC", "GrassAC_AP", "Grass", "BulletHit_Ground", "DirtImpact"
        };
        
        private static readonly string[] WaterEffectNames = {
            "WaterAC", "Water", "BulletHit_Water", "WaterSplash"
        };
        
        private static readonly string[] ExplosionEffectNames = {
            "ExplosionAC", "Explosion", "Explosion_Air", "MissileExplosion_Air"
        };
        
        private static readonly string[] PenetrateEffectNames = {
            "PenetrateAC", "PenetrateAC_AP", "Penetrate"
        };

        /// <summary>
        /// Try to cache effect prefabs from the game
        /// </summary>
        public static void CacheEffects()
        {
            if (_effectsCached) return;
            
            try
            {
                // Primary: Get Bullet2Manager and its SpawnedEffects dictionary
                TryGetBullet2Manager();
                
                // Secondary: Try to extract effect prefabs from Bullet2Manager's pool
                TryCacheEffectsFromBullet2Manager();
                
                // Tertiary: Try UniAircraft VFX fields (AirExplosionPrefab, etc.)
                TryCacheEffectsFromUniAircraft();
                
                _effectsCached = true;
                Plugin.Log?.LogInfo($"[CombatVfxManager] Cached {_cachedEffects.Count} effect prefabs (Bullet2Manager={_bullet2Manager != null})");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[CombatVfxManager] CacheEffects error: {ex.Message}");
                _effectsCached = true; // Don't retry
            }
        }
        
        private static object _bullet2Manager = null;
        private static System.Reflection.MethodInfo _spawnEffectMethod = null;
        private static System.Collections.IDictionary _spawnedEffectsDict = null;
        
        private static void TryGetBullet2Manager()
        {
            try
            {
                var managerType = Type.GetType("Falcon.Weapons.Bullet2Manager, Assembly-CSharp");
                if (managerType == null) return;
                
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
                
                var instanceProp = managerType.GetProperty("Instance", flags);
                var instanceField = managerType.GetField("Instance", flags);
                
                if (instanceProp != null)
                    _bullet2Manager = instanceProp.GetValue(null);
                else if (instanceField != null)
                    _bullet2Manager = instanceField.GetValue(null);
                
                if (_bullet2Manager != null)
                {
                    _spawnEffectMethod = managerType.GetMethod("SpawnEffect", flags);
                    
                    // Try to get the SpawnedEffects dictionary for direct access to effect prefabs
                    var effectsDictField = managerType.GetField("SpawnedEffects", flags)
                        ?? managerType.GetField("_spawnedEffects", flags)
                        ?? managerType.GetField("spawnedEffects", flags);
                    if (effectsDictField != null)
                    {
                        _spawnedEffectsDict = effectsDictField.GetValue(_bullet2Manager) as System.Collections.IDictionary;
                        if (_spawnedEffectsDict != null)
                        {
                            Plugin.Log?.LogInfo($"[CombatVfxManager] Found Bullet2Manager.SpawnedEffects dictionary with {_spawnedEffectsDict.Count} entries");
                            // Log available effect names for debugging
                            foreach (var key in _spawnedEffectsDict.Keys)
                            {
                                Plugin.Log?.LogInfo($"[CombatVfxManager]   Effect: '{key}'");
                            }
                        }
                    }
                    
                    Plugin.Log?.LogInfo($"[CombatVfxManager] Found Bullet2Manager: SpawnEffect={_spawnEffectMethod != null}, SpawnedEffects={_spawnedEffectsDict != null}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[CombatVfxManager] TryGetBullet2Manager error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Try to extract effect prefabs from Bullet2Manager's SpawnedEffects dictionary
        /// </summary>
        private static void TryCacheEffectsFromBullet2Manager()
        {
            if (_spawnedEffectsDict == null) return;
            
            try
            {
                // Cache hit effects
                foreach (string name in HitEffectNames)
                {
                    if (_spawnedEffectsDict.Contains(name))
                    {
                        var pool = _spawnedEffectsDict[name];
                        var prefab = ExtractPrefabFromPool(pool);
                        if (prefab != null && !_cachedEffects.ContainsKey("hit_metal"))
                        {
                            _cachedEffects["hit_metal"] = prefab;
                            Plugin.Log?.LogInfo($"[CombatVfxManager] Cached hit effect from Bullet2Manager: {name}");
                        }
                    }
                }
                
                foreach (string name in GroundEffectNames)
                {
                    if (_spawnedEffectsDict.Contains(name))
                    {
                        var pool = _spawnedEffectsDict[name];
                        var prefab = ExtractPrefabFromPool(pool);
                        if (prefab != null && !_cachedEffects.ContainsKey("hit_ground"))
                        {
                            _cachedEffects["hit_ground"] = prefab;
                            Plugin.Log?.LogInfo($"[CombatVfxManager] Cached ground effect from Bullet2Manager: {name}");
                        }
                    }
                }
                
                foreach (string name in WaterEffectNames)
                {
                    if (_spawnedEffectsDict.Contains(name))
                    {
                        var pool = _spawnedEffectsDict[name];
                        var prefab = ExtractPrefabFromPool(pool);
                        if (prefab != null && !_cachedEffects.ContainsKey("hit_water"))
                        {
                            _cachedEffects["hit_water"] = prefab;
                            Plugin.Log?.LogInfo($"[CombatVfxManager] Cached water effect from Bullet2Manager: {name}");
                        }
                    }
                }
                
                foreach (string name in ExplosionEffectNames)
                {
                    if (_spawnedEffectsDict.Contains(name))
                    {
                        var pool = _spawnedEffectsDict[name];
                        var prefab = ExtractPrefabFromPool(pool);
                        if (prefab != null && !_cachedEffects.ContainsKey("explosion_air"))
                        {
                            _cachedEffects["explosion_air"] = prefab;
                            Plugin.Log?.LogInfo($"[CombatVfxManager] Cached explosion effect from Bullet2Manager: {name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[CombatVfxManager] TryCacheEffectsFromBullet2Manager error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Extract a prefab GameObject from a Bullet2Manager effect pool entry.
        /// Pool entries may be GameObjects directly, or wrapper objects with a Prefab field.
        /// </summary>
        private static GameObject ExtractPrefabFromPool(object pool)
        {
            if (pool == null) return null;
            
            // Direct GameObject
            if (pool is GameObject go) return go;
            
            // Component with gameObject
            if (pool is Component comp) return comp.gameObject;
            
            // Object with Prefab property/field
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            var prefabProp = pool.GetType().GetProperty("Prefab", flags);
            if (prefabProp != null)
            {
                var val = prefabProp.GetValue(pool);
                if (val is GameObject prefabGo) return prefabGo;
                if (val is Component prefabComp) return prefabComp.gameObject;
            }
            
            var prefabField = pool.GetType().GetField("Prefab", flags)
                ?? pool.GetType().GetField("prefab", flags);
            if (prefabField != null)
            {
                var val = prefabField.GetValue(pool);
                if (val is GameObject fieldGo) return fieldGo;
                if (val is Component fieldComp) return fieldComp.gameObject;
            }
            
            return null;
        }
        
        /// <summary>
        /// Try to get VFX prefabs from UniAircraft fields (AirExplosionPrefab, etc.)
        /// </summary>
        private static void TryCacheEffectsFromUniAircraft()
        {
            try
            {
                var player = Falcon.UniversalAircraft.UniAircraft.Player;
                if (player == null) return;
                
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var aircraftType = player.GetType();
                
                // Try to get explosion prefab fields
                string[] explosionFields = { "AirExplosionPrefab", "airExplosionPrefab", "<AirExplosionPrefab>k__BackingField" };
                foreach (var fieldName in explosionFields)
                {
                    var field = aircraftType.GetField(fieldName, flags);
                    if (field != null)
                    {
                        var value = field.GetValue(player);
                        GameObject prefab = null;
                        if (value is ParticleSystem ps) prefab = ps.gameObject;
                        else if (value is GameObject go2) prefab = go2;
                        else if (value is Component c) prefab = c.gameObject;
                        
                        if (prefab != null && !_cachedEffects.ContainsKey("explosion_air"))
                        {
                            _cachedEffects["explosion_air"] = prefab;
                            Plugin.Log?.LogInfo($"[CombatVfxManager] Cached AirExplosionPrefab from UniAircraft");
                        }
                        break;
                    }
                }
                
                // Try to get impact prefab fields
                string[] impactFields = { "HardFuselageImpactPrefab", "SoftFuselageImpactPrefab", "GroundImpactPrefab" };
                foreach (var fieldName in impactFields)
                {
                    var field = aircraftType.GetField(fieldName, flags);
                    if (field != null)
                    {
                        var value = field.GetValue(player);
                        GameObject prefab = null;
                        if (value is ParticleSystem ps) prefab = ps.gameObject;
                        else if (value is GameObject go2) prefab = go2;
                        else if (value is Component c) prefab = c.gameObject;
                        
                        if (prefab != null && !_cachedEffects.ContainsKey("hit_metal"))
                        {
                            _cachedEffects["hit_metal"] = prefab;
                            Plugin.Log?.LogInfo($"[CombatVfxManager] Cached {fieldName} from UniAircraft");
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[CombatVfxManager] TryCacheEffectsFromUniAircraft error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Spawn impact effect at the specified position
        /// </summary>
        /// <param name="position">Local position to spawn effect</param>
        /// <param name="direction">Impact direction (for rotation)</param>
        /// <param name="impactType">0=bullet, 1=missile, 2=explosion</param>
        /// <param name="effectType">0=metal, 1=ground, 2=water, 3=air</param>
        /// <param name="damage">Damage amount (for scaling effect)</param>
        /// <param name="weaponName">Weapon name (for effect selection)</param>
        public static void SpawnImpactEffect(Vector3 position, Vector3 direction, byte impactType, byte effectType, int damage, string weaponName)
        {
            try
            {
                CacheEffects();
                
                Quaternion rotation = direction != Vector3.zero 
                    ? Quaternion.LookRotation(direction) 
                    : Quaternion.identity;
                
                // Try to use Bullet2Manager.SpawnEffect first (most reliable)
                if (_bullet2Manager != null && _spawnEffectMethod != null)
                {
                    string effectName = GetEffectNameForBullet2Manager(impactType, effectType, weaponName);
                    if (!string.IsNullOrEmpty(effectName))
                    {
                        try
                        {
                            _spawnEffectMethod.Invoke(_bullet2Manager, new object[] { effectName, position, rotation });
                            Plugin.Log?.LogInfo($"[CombatVfxManager] Spawned effect via Bullet2Manager: {effectName}");
                            return;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log?.LogWarning($"[CombatVfxManager] Bullet2Manager.SpawnEffect failed: {ex.Message}");
                        }
                    }
                }
                
                // Fallback: use cached prefabs
                string cacheKey = GetCacheKeyForEffect(impactType, effectType);
                if (_cachedEffects.TryGetValue(cacheKey, out GameObject prefab) && prefab != null)
                {
                    var effect = UnityEngine.Object.Instantiate(prefab, position, rotation);
                    
                    // Scale effect based on damage for missiles/explosions
                    if (impactType >= 1 && damage > 50)
                    {
                        float scale = Mathf.Clamp(damage / 100f, 1f, 3f);
                        effect.transform.localScale *= scale;
                    }
                    
                    Plugin.Log?.LogInfo($"[CombatVfxManager] Spawned cached effect: {cacheKey}");
                    return;
                }
                
                // Ultimate fallback: create simple particle effect
                SpawnFallbackEffect(position, rotation, impactType, damage);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[CombatVfxManager] SpawnImpactEffect error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Spawn explosion effect at the specified position
        /// </summary>
        public static void SpawnExplosion(Vector3 position, float radius, int damage)
        {
            SpawnImpactEffect(position, Vector3.up, 2, 3, damage, "Explosion");
        }
        
        private static string GetEffectNameForBullet2Manager(byte impactType, byte effectType, string weaponName)
        {
            // Use REAL game FX names from Weapons/Bullets/Aircraft.json
            // The game's Bullet2Manager uses these keys: HitAC, ExplosionAC, GrassAC, WaterAC, PenetrateAC
            
            if (impactType == 1 || impactType == 2) // Missile or explosion
            {
                // Try real names first, then fallbacks
                foreach (string name in ExplosionEffectNames)
                {
                    if (_spawnedEffectsDict != null && _spawnedEffectsDict.Contains(name))
                        return name;
                }
                return "ExplosionAC";
            }
            
            string[] candidates;
            switch (effectType)
            {
                case 0: candidates = HitEffectNames; break;
                case 1: candidates = GroundEffectNames; break;
                case 2: candidates = WaterEffectNames; break;
                case 3: candidates = ExplosionEffectNames; break;
                default: candidates = HitEffectNames; break;
            }
            
            // Return the first name that exists in the SpawnedEffects dictionary
            if (_spawnedEffectsDict != null)
            {
                foreach (string name in candidates)
                {
                    if (_spawnedEffectsDict.Contains(name))
                        return name;
                }
            }
            
            // Return the first candidate as a guess
            return candidates.Length > 0 ? candidates[0] : "HitAC";
        }
        
        private static string GetCacheKeyForEffect(byte impactType, byte effectType)
        {
            if (impactType == 1 || impactType == 2) return "explosion_air";
            
            switch (effectType)
            {
                case 0: return "hit_metal";
                case 1: return "hit_ground";
                case 2: return "hit_water";
                case 3: return "explosion_air";
                default: return "hit_metal";
            }
        }
        
        private static void SpawnFallbackEffect(Vector3 position, Quaternion rotation, byte impactType, int damage)
        {
            try
            {
                // Create a simple particle system as fallback
                var effectGo = new GameObject("NetworkImpactEffect");
                effectGo.transform.position = position;
                effectGo.transform.rotation = rotation;
                
                var ps = effectGo.AddComponent<ParticleSystem>();
                var main = ps.main;
                var emission = ps.emission;
                var shape = ps.shape;
                
                // Configure based on impact type
                if (impactType == 1 || impactType == 2) // Explosion
                {
                    main.startLifetime = 1.5f;
                    main.startSpeed = 15f;
                    main.startSize = 0.5f;
                    main.startColor = new Color(1f, 0.5f, 0.1f, 1f);
                    main.duration = 0.3f;
                    main.loop = false;
                    
                    emission.rateOverTime = 0;
                    emission.SetBursts(new ParticleSystem.Burst[] { 
                        new ParticleSystem.Burst(0f, 50) 
                    });
                    
                    shape.shapeType = ParticleSystemShapeType.Sphere;
                    shape.radius = Mathf.Clamp(damage / 100f, 0.5f, 3f);
                }
                else // Bullet hit
                {
                    main.startLifetime = 0.5f;
                    main.startSpeed = 5f;
                    main.startSize = 0.1f;
                    main.startColor = new Color(1f, 0.8f, 0.3f, 1f);
                    main.duration = 0.1f;
                    main.loop = false;
                    
                    emission.rateOverTime = 0;
                    emission.SetBursts(new ParticleSystem.Burst[] { 
                        new ParticleSystem.Burst(0f, 10) 
                    });
                    
                    shape.shapeType = ParticleSystemShapeType.Cone;
                    shape.angle = 30f;
                    shape.radius = 0.05f;
                }
                
                ps.Play();
                
                // Cleanup after effect finishes
                UnityEngine.Object.Destroy(effectGo, main.startLifetime.constant + 0.5f);
                
                Plugin.Log?.LogInfo($"[CombatVfxManager] Spawned fallback effect at {position}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[CombatVfxManager] SpawnFallbackEffect error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clear cached effects (call when leaving flight)
        /// </summary>
        public static void ClearCache()
        {
            _cachedEffects.Clear();
            _effectsCached = false;
            _bullet2Manager = null;
            _spawnEffectMethod = null;
            _spawnedEffectsDict = null;
        }
    }
}
