using HarmonyLib;
using UnityEngine;
using Falcon.World;
using TCAMultiplayer.Networking;
using System;
using System.Reflection;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Patches for syncing environmental effects like clouds and wind
    /// </summary>
    [HarmonyPatch]
    public static class EnvironmentPatches
    {
        [HarmonyPatch(typeof(CloudMeshes), "InitializeClouds")]
        [HarmonyPrefix]
        public static void InitializeClouds_Prefix()
        {
            try
            {
                // Only sync clouds if we are in a multiplayer game
                if (Plugin.Instance?.Lobby != null && (Plugin.Instance.Lobby.GameStarted || Plugin.Instance.Lobby.GameLoading))
                {
                    Plugin.Log?.LogInfo("[EnvironmentPatches] Intercepted CloudMeshes.InitializeClouds. Applying deterministic random seed.");
                    
                    // Generate a deterministic seed based on map name and time of day
                    string mapName = Plugin.Instance.Lobby.MapName ?? "DefaultMap";
                    int timeOfDay = (int)(Plugin.Instance.Lobby.SelectedTimeOfDay);
                    int seed = mapName.GetHashCode() ^ (timeOfDay * 1337);

                    // Set the random seed so that UnityEngine.Random inside InitializeClouds generates the exact same clouds for all players
                    UnityEngine.Random.InitState(seed);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[EnvironmentPatches] Error in InitializeClouds prefix: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(Falcon.World.Environment), "UpdateWindVector")]
        [HarmonyPrefix]
        public static bool UpdateWindVector_Prefix(Falcon.World.Environment __instance, Component ___GlobalWind)
        {
            try
            {
                // In multiplayer, sync the wind using TimeOfDaySeconds instead of Time.time
                // This ensures wind (and therefore cloud drift) is identical on all clients
                if (Plugin.Instance?.Lobby != null && (Plugin.Instance.Lobby.GameStarted || Plugin.Instance.Lobby.GameLoading))
                {
                    // Use TimeOfDaySeconds which is deterministic and synced
                    float syncedTime = (float)__instance.TODTimespan.TotalSeconds;

                    float num = __instance.WindSpeedMS + Mathf.PerlinNoise(0f, syncedTime) * __instance.WindTurbulence;
                    float num2 = __instance.WindHeading + 180f + Falcon.Utilities.Perlin.Noise(syncedTime, 50f) * (__instance.WindSpeedMS / 4f);
                    num2 += Falcon.Utilities.Perlin.Noise(syncedTime, 200f) * (__instance.WindTurbulence / 2f);
                    float num3 = Falcon.Utilities.Perlin.Noise(syncedTime, 100f) * Mathf.Sqrt(__instance.WindSpeedMS + __instance.WindTurbulence);
                    
                    ___GlobalWind.transform.rotation = Quaternion.AngleAxis(num2, Vector3.up) * Quaternion.AngleAxis(num3, Vector3.right);
                    
                    // Set windMain and windTurbulence via reflection since WindZone is in a separate unreferenced module
                    var windZoneType = ___GlobalWind.GetType();
                    var windMainProp = windZoneType.GetProperty("windMain");
                    if (windMainProp != null) windMainProp.SetValue(___GlobalWind, __instance.WindSpeedMS, null);
                    
                    var windTurbulenceProp = windZoneType.GetProperty("windTurbulence");
                    if (windTurbulenceProp != null) windTurbulenceProp.SetValue(___GlobalWind, __instance.WindTurbulence, null);
                    
                    // Set WindVelocityMS (it's an auto-property with a private setter)
                    var windVelProp = typeof(Falcon.World.Environment).GetProperty("WindVelocityMS", BindingFlags.Public | BindingFlags.Instance);
                    if (windVelProp != null)
                    {
                        windVelProp.SetValue(__instance, ___GlobalWind.transform.forward * num, null);
                    }

                    return false; // Skip original method
                }
                return true; // Run original method in singleplayer
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[EnvironmentPatches] UpdateWindVector error: {ex.Message}");
                return true;
            }
        }
    }
}