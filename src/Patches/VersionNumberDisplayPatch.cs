using System;
using Falcon.UI;
using HarmonyLib;
using TCAMultiplayer.Core;
using UnityEngine;
using UnityEngine.UI;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Extends the game's native bottom-left version label with the loaded TCAMP version.
    /// </summary>
    [HarmonyPatch(typeof(VersionNumberDisplay))]
    internal static class VersionNumberDisplayPatch
    {
        private const string ModVersionPrefix = "TCAMP";

        [HarmonyPostfix]
        [HarmonyPatch("Start")]
        private static void StartPostfix(Text ___versionText)
        {
            try
            {
                if (___versionText == null)
                {
                    Log.Warning("MENU", "VersionNumberDisplay.versionText not found; cannot inject TCAMP version");
                    return;
                }

                string gameVersion = ___versionText.text ?? string.Empty;
                if (gameVersion.IndexOf(ModVersionPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                    return;

                ___versionText.horizontalOverflow = HorizontalWrapMode.Overflow;
                ___versionText.verticalOverflow = VerticalWrapMode.Overflow;

                var rect = ___versionText.rectTransform;
                if (rect != null && rect.sizeDelta.x > 0f && rect.sizeDelta.x < 300f)
                    rect.sizeDelta = new Vector2(300f, rect.sizeDelta.y);

                ___versionText.text = $"{gameVersion.TrimEnd()}  {ModVersionPrefix} {PluginMetadata.Version}";
                Log.Info("MENU", $"Injected TCAMP version into native version label: {PluginMetadata.Version}");
            }
            catch (Exception ex)
            {
                Log.Warning("MENU", $"Failed to inject TCAMP version label: {ex.Message}");
            }
        }
    }
}
