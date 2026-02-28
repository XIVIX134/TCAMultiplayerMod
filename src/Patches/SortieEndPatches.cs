using System;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Falcon.Game2.UI;
using HarmonyLib;
using UnityEngine.UI;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Makes End Sortie host-only and synchronizes return-to-lobby for all peers.
    /// </summary>
    [HarmonyPatch]
    public static class SortieEndPatches
    {
        private static readonly FieldInfo PauseFinishMissionButtonField =
            AccessTools.Field(typeof(PauseMenu), "FinishMissionButton");

        private static readonly FieldInfo RearmEndSortieButtonField =
            AccessTools.Field(typeof(RearmRefuelDialog), "EndSortieButton");

        [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.ShowPauseMenu))]
        [HarmonyPostfix]
        public static void PauseMenu_ShowPauseMenu_Postfix(PauseMenu __instance, ref UniTask<PauseMenu.Result> __result)
        {
            __result = HandlePauseMenuResult(__instance, __result);
        }

        [HarmonyPatch(typeof(RearmRefuelDialog), nameof(RearmRefuelDialog.RunLoadoutSelector))]
        [HarmonyPostfix]
        public static void RearmRefuelDialog_RunLoadoutSelector_Postfix(RearmRefuelDialog __instance, ref UniTask<RearmRefuelDialog.RearmResult> __result)
        {
            __result = HandleRearmResult(__instance, __result);
        }

        private static async UniTask<PauseMenu.Result> HandlePauseMenuResult(PauseMenu menu, UniTask<PauseMenu.Result> originalTask)
        {
            bool intercept = ShouldInterceptSortieEnd();
            bool isHost = Plugin.Instance?.GameState?.IsHost ?? false;

            if (intercept)
            {
                SetButtonVisible(PauseFinishMissionButtonField, menu, isHost);
            }

            PauseMenu.Result result = await originalTask;

            if (!intercept || result != PauseMenu.Result.FinishMission)
            {
                return result;
            }

            if (!isHost)
            {
                Plugin.Log?.LogInfo("[SortieEndPatches] Client attempted pause-menu End Sortie; forcing Resume.");
                return PauseMenu.Result.Resume;
            }

            Plugin.Instance?.RequestReturnToLobbyAsHost("PauseMenu");
            return PauseMenu.Result.Resume;
        }

        private static async UniTask<RearmRefuelDialog.RearmResult> HandleRearmResult(
            RearmRefuelDialog dialog,
            UniTask<RearmRefuelDialog.RearmResult> originalTask)
        {
            bool intercept = ShouldInterceptSortieEnd();
            bool isHost = Plugin.Instance?.GameState?.IsHost ?? false;

            if (intercept)
            {
                SetButtonVisible(RearmEndSortieButtonField, dialog, isHost);
            }

            RearmRefuelDialog.RearmResult result = await originalTask;

            if (!intercept || result.Action != RearmRefuelDialog.Action.EndSortie)
            {
                return result;
            }

            if (!isHost)
            {
                Plugin.Log?.LogInfo("[SortieEndPatches] Client attempted rearm End Sortie; forcing Resume.");
                result.Action = RearmRefuelDialog.Action.Resume;
                return result;
            }

            Plugin.Instance?.RequestReturnToLobbyAsHost("RearmRefuel");
            result.Action = RearmRefuelDialog.Action.Resume;
            return result;
        }

        private static bool ShouldInterceptSortieEnd()
        {
            var gameState = Plugin.Instance?.GameState;
            return gameState != null && gameState.IsConnected && gameState.IsInGame;
        }

        private static void SetButtonVisible(FieldInfo buttonField, object target, bool visible)
        {
            try
            {
                if (!(buttonField?.GetValue(target) is Button button) || button.gameObject == null)
                {
                    return;
                }

                button.gameObject.SetActive(visible);
                button.interactable = visible;
            }
            catch (Exception ex)
            {
                // Non-critical (UI hierarchy can change while dialog initializes).
                if (LogHelper.ShouldLogInterval("SortieEndPatches.SetButtonVisible", 5f))
                {
                    Plugin.Log?.LogInfo($"[SortieEndPatches] End Sortie button visibility update skipped: {ex.Message}");
                }
            }
        }
    }
}
