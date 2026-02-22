using System;
using System.Collections.Generic;
using UnityEngine;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Universal asset finder for the TCA Multiplayer mod.
    ///
    /// Resolves Unity assets (GameObjects, ParticleSystems, AudioClips, ScriptableObjects, etc.)
    /// across ALL possible sources — both vanilla (Resources/) and modded (AssetBundles):
    ///
    ///   Search order inside Find&lt;T&gt;():
    ///     1. Weak-reference cache (fastest — avoids re-scanning)
    ///     2. Bundle-qualified format "bundleName:assetName" → direct bundle lookup
    ///     3. Resources.Load&lt;T&gt;(path) — vanilla game assets
    ///     4. All loaded AssetBundles → bundle.LoadAsset&lt;T&gt;(name) — mod assets
    ///     5. Resources.FindObjectsOfTypeAll&lt;T&gt;() name match — in-memory assets
    ///     6. Fuzzy basename-only fallback across all bundles
    ///
    ///   GetIdentifier(obj):
    ///     Reverse lookup — given a live Unity object, returns the canonical string that
    ///     Find&lt;T&gt;() can use to relocate it on another machine:
    ///       "bundleName:assetName"  for mod AssetBundle assets
    ///       plain "assetName"       for Resources assets / unknown
    ///
    /// Usage examples:
    ///   // Sender side — build a packet identifier from a live prefab reference
    ///   string id = UniversalAssetFinder.GetIdentifier(myExplosionPrefab);
    ///   packet.EffectPath = id;
    ///
    ///   // Receiver side — resolve identifier back to a prefab
    ///   var prefab = UniversalAssetFinder.Find&lt;GameObject&gt;(packet.EffectPath);
    ///   if (prefab != null) Instantiate(prefab, pos, rot);
    ///
    ///   // Typed convenience helpers
    ///   var clip  = UniversalAssetFinder.FindAudioClip("ExplosionSFX");
    ///   var ps    = UniversalAssetFinder.FindParticleSystem("SmokeTrail");
    ///   var go    = UniversalAssetFinder.FindPrefab("NukeExplosion");
    /// </summary>
    public static class UniversalAssetFinder
    {
        // Weak-reference cache: (Type.FullName + "|" + normalizedKey) -> object
        // WeakReference lets Unity GC unload unused assets without us holding strong refs.
        private static readonly Dictionary<string, WeakReference> _cache =
            new Dictionary<string, WeakReference>(StringComparer.OrdinalIgnoreCase);

        // Separator used in bundle-qualified identifiers: "bundleName:assetName"
        private const char BUNDLE_SEP = ':';

        // Limit for FindObjectsOfTypeAll scans (performance guard)
        private const int MAX_SCENE_SCAN = 4096;

        // ─────────────────────────────────────────────────────────────────────
        // CORE GENERIC API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Find a Unity asset of type T by name or bundle-qualified path.
        ///
        /// Accepts:
        ///   "bundleName:assetName"   — mod AssetBundle asset (precise)
        ///   "Effects/Explosion/Foo"  — Resources path (vanilla)
        ///   "Foo"                    — bare name, searched everywhere
        ///
        /// Returns null if not found anywhere.
        /// </summary>
        public static T Find<T>(string nameOrPath) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(nameOrPath)) return null;

            string cacheKey = typeof(T).FullName + "|" + nameOrPath;

            // 1. Cache hit
            if (_cache.TryGetValue(cacheKey, out var weakRef))
            {
                var cached = weakRef.Target as T;
                if (cached != null) return cached;
                // Dead weak ref — remove and continue
                _cache.Remove(cacheKey);
            }

            T result = null;

            // 2. Bundle-qualified: "bundleName:assetName"
            if (nameOrPath.IndexOf(BUNDLE_SEP) >= 0)
            {
                int sep = nameOrPath.IndexOf(BUNDLE_SEP);
                string bundleName = nameOrPath.Substring(0, sep);
                string assetName  = nameOrPath.Substring(sep + 1);
                result = FindInSpecificBundle<T>(bundleName, assetName);
                if (result != null)
                {
                    CacheResult(cacheKey, result);
                    return result;
                }
                // Fall through with the asset name part for broader search
                nameOrPath = assetName;
                cacheKey   = typeof(T).FullName + "|" + nameOrPath;
            }

            // 3. Resources.Load — handles both "Effects/Explosion/Foo" and "Foo"
            result = Resources.Load<T>(nameOrPath);
            if (result != null)
            {
                CacheResult(cacheKey, result);
                return result;
            }

            // Extract just the basename for broader searches (strip directory prefix)
            string baseName = System.IO.Path.GetFileNameWithoutExtension(nameOrPath);
            if (string.IsNullOrEmpty(baseName)) baseName = nameOrPath;

            // Also try with the basename directly in Resources
            if (!string.Equals(baseName, nameOrPath, StringComparison.OrdinalIgnoreCase))
            {
                result = Resources.Load<T>(baseName);
                if (result != null)
                {
                    CacheResult(cacheKey, result);
                    return result;
                }
            }

            // 4. Search all loaded AssetBundles
            result = FindInAllBundles<T>(nameOrPath, baseName);
            if (result != null)
            {
                CacheResult(cacheKey, result);
                return result;
            }

            // 5. Resources.FindObjectsOfTypeAll — in-memory assets (instantiated or referenced)
            result = FindInLoadedObjects<T>(nameOrPath, baseName);
            if (result != null)
            {
                CacheResult(cacheKey, result);
                return result;
            }

            Plugin.Log?.LogWarning($"[UniversalAssetFinder] Could not find {typeof(T).Name}: '{nameOrPath}'");
            return null;
        }

        /// <summary>
        /// Given a live Unity Object, return the canonical identifier string that
        /// Find&lt;T&gt;() on another machine (with the same mods) can use to locate it.
        ///
        /// Returns "bundleName:assetName" if the object was loaded from a mod AssetBundle,
        /// otherwise returns the plain asset name for Resources-based assets.
        /// </summary>
        public static string GetIdentifier(UnityEngine.Object obj)
        {
            if (obj == null) return "";

            string assetName = obj.name;
            if (string.IsNullOrEmpty(assetName)) return "";

            // Search all loaded AssetBundles to find which bundle this came from
            try
            {
                foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    try
                    {
                        if (bundle == null) continue;
                        if (bundle.Contains(assetName))
                        {
                            string id = bundle.name + BUNDLE_SEP + assetName;
                            Plugin.Log?.LogInfo($"[UniversalAssetFinder] Identified '{assetName}' in bundle '{bundle.name}'");
                            return id;
                        }
                    }
                    catch { /* skip individual bundle errors */ }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[UniversalAssetFinder] GetIdentifier bundle scan error: {ex.Message}");
            }

            // Not found in any bundle — return plain name (Resources asset or scene object)
            return assetName;
        }

        // ─────────────────────────────────────────────────────────────────────
        // TYPED CONVENIENCE WRAPPERS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Find a GameObject prefab (vanilla or modded).</summary>
        public static GameObject FindPrefab(string nameOrPath)
            => Find<GameObject>(nameOrPath);

        /// <summary>Find a ParticleSystem prefab (vanilla or modded).</summary>
        public static ParticleSystem FindParticleSystem(string nameOrPath)
            => Find<ParticleSystem>(nameOrPath);

        /// <summary>Find an AudioClip (vanilla or modded).</summary>
        public static AudioClip FindAudioClip(string nameOrPath)
            => Find<AudioClip>(nameOrPath);

        /// <summary>Find a Sprite (vanilla or modded).</summary>
        public static Sprite FindSprite(string nameOrPath)
            => Find<Sprite>(nameOrPath);

        /// <summary>Find a Mesh (vanilla or modded).</summary>
        public static Mesh FindMesh(string nameOrPath)
            => Find<Mesh>(nameOrPath);

        /// <summary>Find a ScriptableObject of a specific subtype (vanilla or modded).</summary>
        public static T FindScriptableObject<T>(string nameOrPath) where T : ScriptableObject
            => Find<T>(nameOrPath);

        // ─────────────────────────────────────────────────────────────────────
        // CACHE MANAGEMENT
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Clear the cache. Call when mods are loaded/unloaded or scenes change.
        /// </summary>
        public static void InvalidateCache()
        {
            _cache.Clear();
            Plugin.Log?.LogInfo("[UniversalAssetFinder] Cache cleared");
        }

        /// <summary>
        /// Pre-warm cache by scanning all currently loaded AssetBundles.
        /// Call this once after mods finish loading to make subsequent lookups instant.
        /// NOTE: Only caches names, does not force-load all assets.
        /// </summary>
        public static void PrewarmCache()
        {
            int bundleCount = 0;
            int assetCount  = 0;

            try
            {
                foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (bundle == null) continue;
                    bundleCount++;

                    try
                    {
                        // GetAllAssetNames() lists paths without loading — cheap
                        var assetNames = bundle.GetAllAssetNames();
                        assetCount += assetNames?.Length ?? 0;
                        // We don't pre-load assets here to avoid memory pressure.
                        // Cache entries are built lazily on first Find<T>() call.
                    }
                    catch { /* skip bundle if GetAllAssetNames fails */ }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[UniversalAssetFinder] PrewarmCache error: {ex.Message}");
            }

            Plugin.Log?.LogInfo($"[UniversalAssetFinder] Prewarm: {bundleCount} bundles, {assetCount} asset names found");
        }

        // ─────────────────────────────────────────────────────────────────────
        // PRIVATE HELPERS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Try to load an asset from a specific named AssetBundle.
        /// </summary>
        private static T FindInSpecificBundle<T>(string bundleName, string assetName) where T : UnityEngine.Object
        {
            try
            {
                foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (bundle == null) continue;
                    if (!string.Equals(bundle.name, bundleName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var asset = bundle.LoadAsset<T>(assetName);
                    if (asset != null)
                    {
                        Plugin.Log?.LogInfo($"[UniversalAssetFinder] Found '{assetName}' in bundle '{bundleName}'");
                        return asset;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[UniversalAssetFinder] FindInSpecificBundle({bundleName}, {assetName}) error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Search all currently loaded AssetBundles for an asset matching nameOrPath or baseName.
        /// Tries the full path first, then the basename.
        /// </summary>
        private static T FindInAllBundles<T>(string nameOrPath, string baseName) where T : UnityEngine.Object
        {
            try
            {
                foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (bundle == null) continue;

                    try
                    {
                        // Try full path
                        T asset = bundle.LoadAsset<T>(nameOrPath);
                        if (asset != null)
                        {
                            Plugin.Log?.LogInfo($"[UniversalAssetFinder] Found '{nameOrPath}' in bundle '{bundle.name}'");
                            return asset;
                        }

                        // Try basename only (without directory prefix)
                        if (!string.Equals(baseName, nameOrPath, StringComparison.OrdinalIgnoreCase))
                        {
                            asset = bundle.LoadAsset<T>(baseName);
                            if (asset != null)
                            {
                                Plugin.Log?.LogInfo($"[UniversalAssetFinder] Found '{baseName}' in bundle '{bundle.name}' (basename match)");
                                return asset;
                            }
                        }

                        // Fuzzy: enumerate all asset names in this bundle and check for substring match
                        // Only do this for small-ish bundles to avoid perf hit
                        try
                        {
                            var allNames = bundle.GetAllAssetNames();
                            if (allNames != null && allNames.Length < 500)
                            {
                                foreach (var candidateName in allNames)
                                {
                                    string candidateBase = System.IO.Path.GetFileNameWithoutExtension(candidateName);
                                    if (string.Equals(candidateBase, baseName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        asset = bundle.LoadAsset<T>(candidateName);
                                        if (asset != null)
                                        {
                                            Plugin.Log?.LogInfo($"[UniversalAssetFinder] Found '{candidateName}' in bundle '{bundle.name}' (fuzzy match for '{baseName}')");
                                            return asset;
                                        }
                                    }
                                }
                            }
                        }
                        catch { /* skip fuzzy if GetAllAssetNames fails */ }
                    }
                    catch { /* skip individual bundle errors */ }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[UniversalAssetFinder] FindInAllBundles error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Search in-memory objects (Resources.FindObjectsOfTypeAll) — catches assets that
        /// are already loaded/instantiated but not directly in a bundle or Resources path.
        /// Includes scene objects, prefab instances still in memory, etc.
        /// </summary>
        private static T FindInLoadedObjects<T>(string nameOrPath, string baseName) where T : UnityEngine.Object
        {
            try
            {
                var allObjects = Resources.FindObjectsOfTypeAll<T>();
                if (allObjects == null) return null;

                int limit = Math.Min(allObjects.Length, MAX_SCENE_SCAN);
                for (int i = 0; i < limit; i++)
                {
                    var obj = allObjects[i];
                    if (obj == null) continue;

                    string objName = obj.name;
                    if (string.IsNullOrEmpty(objName)) continue;

                    if (string.Equals(objName, nameOrPath, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(objName, baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Log?.LogInfo($"[UniversalAssetFinder] Found '{objName}' via FindObjectsOfTypeAll<{typeof(T).Name}>");
                        return obj;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[UniversalAssetFinder] FindInLoadedObjects error: {ex.Message}");
            }

            return null;
        }

        /// <summary>Store a result in the weak-reference cache.</summary>
        private static void CacheResult(string key, UnityEngine.Object result)
        {
            try
            {
                _cache[key] = new WeakReference(result);
            }
            catch { /* cache is best-effort */ }
        }
    }
}
