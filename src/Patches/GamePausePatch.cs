using System;
using Falcon;
using HarmonyLib;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Prevents the native pause from freezing the simulation in multiplayer.
    /// GamePause.PauseGame sets Time.timeScale = 0 and mutes audio — fine in
    /// singleplayer, but in a session the world must keep running while the
    /// pause menu is open. Skipping the original also keeps IsPaused false so
    /// nothing downstream thinks the game is halted.
    /// </summary>
    [HarmonyPatch]
    internal static class GamePausePatch
    {
        /// <summary>Returns true when in an active multiplayer session.</summary>
        public static Func<bool> IsMultiplayerSession;

        [HarmonyPatch(typeof(GamePause), nameof(GamePause.PauseGame), new Type[] { typeof(bool) })]
        [HarmonyPrefix]
        static bool PausePrefix()
        {
            // Skip the original entirely in multiplayer — no time freeze, no mute
            return IsMultiplayerSession?.Invoke() != true;
        }
    }
}
