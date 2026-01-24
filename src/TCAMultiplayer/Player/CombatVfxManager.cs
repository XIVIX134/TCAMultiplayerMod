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
        
        // Known effect paths from decompiled game code
        private static readonly string[] HitEffectPaths = {
            "Effects/BulletHit_Metal",
            "Effects/BulletHit_Hard",
            "Effects/BulletImpact_Metal"
        };
        
        private static readonly string[] GroundEffectPaths = {
            "Effects/BulletHit_Ground",
            "Effects/BulletHit_Soft",
            "Effects/DirtImpact"
        };
        
        private static readonly string[] WaterEffectPaths = {
            "Effects/BulletHit_Water",
            "Effects/WaterSplash"
        };
        
        private static readonly string[] AirExplosionPaths = {
            "Effects/Explosion_Air",
            "Effects/MissileExplosion_Air",
            "Effects/Explosion"
        };

        /// <summary>
        /// Try to cache effect prefabs from the game
        /// </summary>
        public static void CacheEffects()
        {
            if (_effectsCached) return;
            
            try
            {
                // Try to load effect prefabs from Resources
                foreach (string path in HitEffectPaths)
                {
                    var effect = Resources.Load<GameObject>(path);
                    if (effect != null && !_cachedEffects.ContainsKey("hit_metal"))
                    {
                        _cachedEffects["hit_metal"] = effect;
                        Plugin.Log?.LogInfo($"[CombatVfxManager] Cached effect: {path}");
                    }
                }
                
                foreach (string path in GroundEffectPaths)
                {
                    var effect = Resources.Load<GameObject>(path);
                    if (effect != null && !_cachedEffects.ContainsKey("hit_ground"))
                    {
                        _cachedEffects["hit_ground"] = effect;
                        Plugin.Log?.LogInfo($"[CombatVfxManager] Cached effect: {path}");
                    }
                }
                
                foreach (string path in WaterEffectPaths)
                {
                    var effect = Resources.Load<GameObject>(path);
                    if (effect != null && !_cachedEffects.ContainsKey("hit_water"))
                    {
                        _cachedEffects["hit_water"] = effect;
                        Plugin.Log?.LogInfo($"[CombatVfxManager] Cached effect: {path}");
                    }
                }
                
                foreach (string path in AirExplosionPaths)
                {
                    var effect = Resources.Load<GameObject>(path);
                    if (effect != null && !_cachedEffects.ContainsKey("explosion_air"))
                    {
                        _cachedEffects["explosion_air"] = effect;
                        Plugin.Log?.LogInfo($"[CombatVfxManager] Cached effect: {path}");
                    }
                }
                
                // Also try to find Bullet2Manager and use its SpawnEffect method
                TryGetBullet2Manager();
                
                _effectsCached = true;
                Plugin.Log?.LogInfo($"[CombatVfxManager] Cached {_cachedEffects.Count} effect prefabs");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[CombatVfxManager] CacheEffects error: {ex.Message}");
                _effectsCached = true; // Don't retry
            }
        }
        
        private static object _bullet2Manager = null;
        private static System.Reflection.MethodInfo _spawnEffectMethod = null;
        
        private static void TryGetBullet2Manager()
        {
            try
            {
                var managerType = Type.GetType("Falcon.Weapons.Bullet2Manager, Assembly-CSharp");
                if (managerType != null)
                {
                    var instanceProp = managerType.GetProperty("Instance", 
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (instanceProp != null)
                    {
                        _bullet2Manager = instanceProp.GetValue(null);
                        if (_bullet2Manager != null)
                        {
                            _spawnEffectMethod = managerType.GetMethod("SpawnEffect",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            Plugin.Log?.LogInfo("[CombatVfxManager] Found Bullet2Manager.SpawnEffect method");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[CombatVfxManager] TryGetBullet2Manager error: {ex.Message}");
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
            // These names need to match what Bullet2Manager.SpawnedEffects contains
            // From decompiled code, common effect pool names are:
            // "hit", "hit_metal", "hit_ground", "hit_water", "penetrate", "explode", etc.
            
            if (impactType == 1 || impactType == 2) // Missile or explosion
            {
                return "explode";
            }
            
            switch (effectType)
            {
                case 0: return "hit"; // metal/hard
                case 1: return "hit_ground"; // soft/ground
                case 2: return "hit_water"; // water
                case 3: return "explode"; // air explosion
                default: return "hit";
            }
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
        }
    }
}
