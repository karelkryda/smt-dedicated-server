using HarmonyLib;
using UnityEngine;

namespace AutoHost;

/// <summary>
/// Prevents GameCanvas.Update from crashing when Camera.main is null (headless mode).
/// </summary>
[HarmonyPatch(typeof(GameCanvas), "Update")]
internal static class GameCanvasUpdatePatch
{
    static bool Prefix()
    {
        return Camera.main != null;
    }
}

/// <summary>
/// Logs when the game autosaves.
/// </summary>
[HarmonyPatch(typeof(GameData), "SaveFromQuitButton")]
internal static class SaveFromQuitPatch
{
    static void Postfix()
    {
        Plugin.Logger.LogInfo("AutoHost: Save triggered (SaveFromQuitButton).");
    }
}

/// <summary>
/// Logs when autosave starts.
/// </summary>
[HarmonyPatch(typeof(NetworkSpawner), "SaveProps")]
internal static class SavePropsPatch
{
    static void Prefix(bool autosave)
    {
        Plugin.Logger.LogInfo($"AutoHost: SaveProps(autosave={autosave}) - game is saving.");
    }
}
