// ============================================================================
//  NATIVE UI ONLY -- ALL UI IN THIS MOD MUST USE CANVAS + TEXTMESHPRO
// ============================================================================
//  DO NOT use Unity IMGUI (OnGUI / GUILayout / GUI.Box / GUIStyle / etc.)
//  for ANY user-facing UI in this project.
//
//  Use UIFactory (Canvas buttons, text, input fields) and follow the
//  patterns established in:
//    - MultiplayerMenu.cs   (lobby / host / join / browse)
//    - RespawnScreen.cs     (death overlay + respawn)
//    - ScoreboardHUD.cs     (kill feed, K/D counter, TAB scoreboard)
//
//  The only acceptable use of OnGUI is the tiny debug overlay in
//  PluginRunner -- and even that should be migrated eventually.
// ============================================================================

namespace TCAMultiplayer.UI
{
    /// <summary>
    /// Screen states shared across native UI components
    /// (MultiplayerMenu, RespawnScreen, etc.)
    /// </summary>
    public enum LobbyScreen
    {
        None,
        MainMenu,
        HostSetup,
        Browse,
        DirectConnect,
        Lobby,
        Loading,
        InGame,
        Respawn
    }
}
