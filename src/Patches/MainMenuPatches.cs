using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Falcon.Game2;
using TCAMultiplayer.UI;
using Cysharp.Threading.Tasks;

namespace TCAMultiplayer.Patches
{
    [HarmonyPatch(typeof(MainMenu))]
    public static class MainMenuPatches
    {
        private static GameObject _multiplayerButtonGo;

        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        public static void Awake_Postfix(MainMenu __instance, Button ___ArenaButton)
        {
            if (___ArenaButton == null)
            {
                Plugin.Log?.LogWarning("[MainMenuPatches] ArenaButton not found, cannot inject Multiplayer button");
                return;
            }

            UIFactory.Initialize(__instance);

            if (_multiplayerButtonGo != null) return;

            Plugin.Log?.LogInfo("[MainMenuPatches] Injecting Multiplayer button into Main Menu...");

            try
            {
                _multiplayerButtonGo = Object.Instantiate(___ArenaButton.gameObject, ___ArenaButton.transform.parent);
                _multiplayerButtonGo.name = "MultiplayerButton";

                int arenaIndex = ___ArenaButton.transform.GetSiblingIndex();
                _multiplayerButtonGo.transform.SetSiblingIndex(arenaIndex + 1);

                var text = _multiplayerButtonGo.GetComponentInChildren<TextMeshProUGUI>();
                if (text != null)
                {
                    text.text = "MULTIPLAYER";
                }

                var button = _multiplayerButtonGo.GetComponent<Button>();
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() =>
                {
                    Plugin.Log?.LogInfo("[MainMenuPatches] Multiplayer button clicked!");
                    HandleMultiplayerClicked(__instance);
                });
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogError($"[MainMenuPatches] Failed to inject button: {ex.Message}");
            }
        }

        private static void HandleMultiplayerClicked(MainMenu menu)
        {
            MultiplayerMenu.CreateAndRun().Forget();
            menu.ShowMainMenuUI(false);
        }
    }
}
