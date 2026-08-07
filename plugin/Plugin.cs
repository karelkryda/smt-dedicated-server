using System.Collections;
using System.IO;
using System.Net;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutoHost;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
[BepInProcess("Supermarket Together.exe")]
public class Plugin : BaseUnityPlugin
{
    internal new static ManualLogSource Logger = null!;
    internal static Plugin Instance = null!;

    internal static ConfigEntry<string> SaveFile = null!;
    internal static ConfigEntry<int> Layout = null!;
    internal static ConfigEntry<int> GameMode = null!;
    internal static ConfigEntry<bool> AutoEndDay = null!;
    internal static ConfigEntry<bool> UseAutosave = null!;
    internal static ConfigEntry<bool> GrantAllPermissions = null!;
    internal static ConfigEntry<string> LobbyIdFile = null!;
    internal static ConfigEntry<int> AutosaveMinutes = null!;
    internal static ConfigEntry<int> TargetFrameRate = null!;
    internal static ConfigEntry<string> DiscordWebhookUrl = null!;

    private static Harmony harmony = null!;
    private bool hasTriggeredAutoHost;

    private void Awake()
    {
        Instance = this;
        Logger = base.Logger;

        SaveFile = Config.Bind("Server", "SaveFile", "StoreFile0.es3",
            "Save file to load (e.g. StoreFile0.es3, StoreFile1.es3).");
        Layout = Config.Bind("Server", "Layout", 0,
            "Map layout index. 0 = Classic, 3 = Plaza.");
        GameMode = Config.Bind("Server", "GameMode", 1,
            "Lobby type. 1 = Friends Only, 2 = Public.");
        AutoEndDay = Config.Bind("Server", "AutoEndDay", true,
            "Automatically continue past the day-end stats screen.");
        UseAutosave = Config.Bind("Server", "UseAutosave", true,
            "Resume from autosave if one exists for this save file.");
        GrantAllPermissions = Config.Bind("Server", "GrantAllPermissions", true,
            "Grant all permissions (build, restock, etc.) to joining players.");
        LobbyIdFile = Config.Bind("Server", "LobbyIdFile", "lobby_id.txt",
            "File name for lobby ID output. Written to the game's save directory.");
        AutosaveMinutes = Config.Bind("Server", "AutosaveMinutes", 5,
            "Autosave interval in minutes. Ensures periodic saves even if the game's setting is disabled.");
        TargetFrameRate = Config.Bind("Server", "TargetFrameRate", 60,
            "Target frame rate. Higher = smoother NPCs but more CPU. 30-60 recommended.");
        DiscordWebhookUrl = Config.Bind("Server", "DiscordWebhookUrl", "",
            "Discord webhook URL. If set, posts lobby ID when server is ready.");

        harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PluginInfo.Guid);
        SceneManager.sceneLoaded += OnSceneLoaded;
        Logger.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} loaded!");
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        harmony?.UnpatchSelf();
    }

    private float shutdownCheckTimer;

    private void Update()
    {
        // Check for shutdown save trigger every 2 seconds
        shutdownCheckTimer += Time.unscaledDeltaTime;
        if (shutdownCheckTimer < 2f)
            return;
        shutdownCheckTimer = 0f;

        var triggerPath = Path.Combine(Application.persistentDataPath, ".save_and_quit");
        if (!File.Exists(triggerPath))
            return;

        File.Delete(triggerPath);
        Logger.LogInfo("AutoHost: Shutdown signal received!");

        if (NetworkServer.active && GameData.Instance != null)
        {
            GameData.Instance.SaveFromQuitButton();
            Logger.LogInfo("AutoHost: Save-and-quit initiated.");
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == "A_Intro" && !hasTriggeredAutoHost)
        {
            hasTriggeredAutoHost = true;
            StartCoroutine(AutoHostCoroutine());
        }
    }

    /// <summary>
    /// Sets up FSM variables and triggers the game's own scene transition.
    /// The GameInitialization FSM in B_Main handles all loading.
    /// </summary>
    private IEnumerator AutoHostCoroutine()
    {
        Logger.LogInfo("AutoHost: Waiting for game systems...");

        // Wait for Steam
        while (!SteamManager.Initialized)
            yield return null;

        // Wait for PlayMaker globals
        while (FsmVariables.GlobalVariables == null)
            yield return null;

        // Wait for MasterOBJ to exist (DontDestroyOnLoad object that drives scene transitions)
        GameObject? masterOBJ = null;
        while (masterOBJ == null)
        {
            masterOBJ = FsmVariables.GlobalVariables.FindFsmGameObject("MasterOBJ")?.Value;
            yield return null;
        }

        Logger.LogInfo("AutoHost: Systems ready. Configuring game...");

        // Set the FSM global variables that the GameInitialization FSM reads
        FsmVariables.GlobalVariables.GetFsmString("CurrentFilename").Value = SaveFile.Value;

        // Determine whether to load from autosave (pick whichever is newer)
        bool loadFromAutosave = false;
        if (UseAutosave.Value)
        {
            var mainPath = Path.Combine(Application.persistentDataPath, SaveFile.Value);
            var autoPath = Path.Combine(Application.persistentDataPath, "Autosaves", "Autosave001.es3");
            if (File.Exists(autoPath) && File.Exists(mainPath))
            {
                var mainTime = File.GetLastWriteTimeUtc(mainPath);
                var autoTime = File.GetLastWriteTimeUtc(autoPath);
                loadFromAutosave = autoTime > mainTime;
                Logger.LogInfo($"AutoHost: Main save: {mainTime:HH:mm:ss}, Autosave: {autoTime:HH:mm:ss} -> {(loadFromAutosave ? "using autosave" : "using main save")}");
            }
            else if (File.Exists(autoPath))
            {
                loadFromAutosave = true;
            }
        }
        FsmVariables.GlobalVariables.GetFsmBool("LoadingFromAutosave").Value = loadFromAutosave;
        FsmVariables.GlobalVariables.GetFsmInt("GameMode").Value = GameMode.Value;

        // Set layout
        var layoutVar = FsmVariables.GlobalVariables.FindFsmInt("LayoutIndex");
        if (layoutVar != null)
            layoutVar.Value = Layout.Value;

        var layoutFactorVar = FsmVariables.GlobalVariables.FindFsmInt("LayoutFactor");
        if (layoutFactorVar != null)
            layoutFactorVar.Value = Layout.Value;

        Logger.LogInfo($"AutoHost: FSM vars set - file={SaveFile.Value}, autosave={loadFromAutosave}, mode={GameMode.Value}, layout={Layout.Value}");

        // Find the SceneTransition FSM on MasterOBJ and trigger scene load
        PlayMakerFSM? sceneTransitionFSM = null;
        foreach (var fsm in masterOBJ.GetComponents<PlayMakerFSM>())
        {
            if (fsm.FsmName == "SceneTransition")
            {
                sceneTransitionFSM = fsm;
                break;
            }
        }

        if (sceneTransitionFSM == null)
        {
            Logger.LogError("AutoHost: SceneTransition FSM not found on MasterOBJ!");
            yield break;
        }

        // Trigger the scene transition (the game's own flow)
        Logger.LogInfo("AutoHost: Triggering scene transition to B_Main...");
        sceneTransitionFSM.SendEvent("Send_Data");

        // Wait for B_Main to load
        while (SceneManager.GetActiveScene().name != "B_Main")
            yield return null;

        Logger.LogInfo("AutoHost: B_Main loaded. GameInitialization FSM is handling load...");

        // Wait for the game to fully initialize (lobby created = NetworkServer active)
        while (!NetworkServer.active)
            yield return null;

        Logger.LogInfo("AutoHost: Server active!");

        // Wait for GameData to be populated
        while (GameData.Instance == null || GameData.Instance.gameDay <= 0)
            yield return null;

        Logger.LogInfo($"AutoHost: Game fully loaded - day={GameData.Instance.gameDay}, funds={GameData.Instance.gameFunds}");

        // If we loaded from main save, restore employee priorities from autosave
        if (!loadFromAutosave)
        {
            yield return StartCoroutine(RestoreEmployeePrioritiesFromAutosave());
        }

        // Apply our server settings
        if (AutoEndDay.Value)
        {
            GameData.Instance.automaticallyEndDay = true;
            Logger.LogInfo("AutoHost: automaticallyEndDay = true");
        }

        // Reduce quality to save memory (server doesn't need graphics quality)
        Application.targetFrameRate = TargetFrameRate.Value;
        QualitySettings.SetQualityLevel(0, true);
        QualitySettings.shadowDistance = 0;
        QualitySettings.shadows = ShadowQuality.Disable;
        QualitySettings.globalTextureMipmapLimit = 4;
        QualitySettings.vSyncCount = 0;
        QualitySettings.antiAliasing = 0;
        QualitySettings.softParticles = false;
        QualitySettings.realtimeReflectionProbes = false;
        Logger.LogInfo("AutoHost: Graphics minimized for server mode.");

        // Ensure autosave is active
        if (AutosaveMinutes.Value > 0)
        {
            GameData.Instance.StopCoroutine("AutosaveControl");
            GameData.Instance.autosaveFactor = AutosaveMinutes.Value;
            GameData.Instance.nextAutosaveTime = GameData.Instance.dayTimeCounter + (AutosaveMinutes.Value * 60f);
            GameData.Instance.StartCoroutine("AutosaveControl");
            Logger.LogInfo($"AutoHost: Autosave set to every {AutosaveMinutes.Value} minutes.");
        }

        if (GameCanvas.Instance != null)
        {
            if (GrantAllPermissions.Value)
            {
                GameCanvas.Instance.automaticallyRemoveP = false;
                Logger.LogInfo("AutoHost: Permissions granted to all joining players.");
            }
            else
            {
                GameCanvas.Instance.automaticallyRemoveP = true;
                Logger.LogInfo("AutoHost: Permissions restricted for joining players.");
            }
        }

        // Write lobby ID (wait for it since callback may fire after NetworkServer.active)
        Logger.LogInfo("AutoHost: Waiting for lobby ID...");
        float lobbyWait = 0f;
        while ((SteamLobby.Instance == null || SteamLobby.Instance.CurrentLobbyID == 0) && lobbyWait < 30f)
        {
            lobbyWait += Time.deltaTime;
            yield return null;
        }

        if (SteamLobby.Instance != null && SteamLobby.Instance.CurrentLobbyID != 0)
        {
            var lobbyId = SteamLobby.Instance.CurrentLobbyID.ToString();
            Logger.LogInfo($"AutoHost: Lobby ID = {lobbyId}");

            if (!string.IsNullOrEmpty(LobbyIdFile.Value))
            {
                try
                {
                    var path = Path.IsPathRooted(LobbyIdFile.Value)
                        ? LobbyIdFile.Value
                        : Path.Combine(Application.persistentDataPath, LobbyIdFile.Value);
                    File.WriteAllText(path, lobbyId);
                    Logger.LogInfo($"AutoHost: Lobby ID written to {path}");
                }
                catch (System.Exception ex)
                {
                    Logger.LogError($"AutoHost: Failed to write lobby ID: {ex.Message}");
                }
            }

            NotifyDiscord(lobbyId);
        }
        else
        {
            Logger.LogWarning($"AutoHost: Lobby ID not available after {lobbyWait:F1}s wait.");
        }

        Logger.LogInfo("AutoHost: Server is ready! Players can join.");
    }

    /// <summary>
    /// When loading from the main save, the game skips LoadingFromAutosave() which means
    /// employee priorities (task assignments) are lost. This restores them from the autosave
    /// file if available.
    /// </summary>
    private IEnumerator RestoreEmployeePrioritiesFromAutosave()
    {
        // Wait for NPC_Manager to be available and employees to be spawned
        float timeout = 30f;
        float elapsed = 0f;
        while ((NPC_Manager.Instance == null || !NPC_Manager.Instance.initialEmployeesSpawnIsFinished) && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (NPC_Manager.Instance == null)
        {
            Logger.LogWarning("AutoHost: NPC_Manager not available, skipping priority restore.");
            yield break;
        }

        var autosavePath = Path.Combine(Application.persistentDataPath, "Autosaves", "Autosave001.es3");
        if (!File.Exists(autosavePath))
        {
            Logger.LogInfo("AutoHost: No autosave file found, skipping priority restore.");
            yield break;
        }

        try
        {
            var settings = new ES3Settings(ES3.EncryptionType.AES, "g#asojrtg@omos)^yq");
            var cacheSettings = new ES3Settings(autosavePath, ES3.Location.Cache);
            ES3.CacheFile(autosavePath, settings);

            if (ES3.KeyExists("autosaveEmployeePriorities", autosavePath, cacheSettings))
            {
                var priorities = ES3.Load<int[]>("autosaveEmployeePriorities", autosavePath, cacheSettings);
                NPC_Manager.Instance.CmdLoadPrioritiesLayout(priorities);
                Logger.LogInfo($"AutoHost: Restored employee priorities from autosave ({priorities.Length} entries).");
            }
            else
            {
                Logger.LogInfo("AutoHost: No employee priorities in autosave, skipping.");
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"AutoHost: Failed to restore employee priorities: {ex.Message}");
        }
    }

    /// <summary>
    /// Posts lobby ID to a Discord webhook if configured.
    /// </summary>
    private static void NotifyDiscord(string lobbyId)
    {
        if (string.IsNullOrEmpty(DiscordWebhookUrl.Value))
            return;

        try
        {
            var payload = "{\"content\":\"" +
                $"**Supermarket Together** server is ready!\\nLobby ID: `{lobbyId}`\\n" +
                $"Day: {GameData.Instance.gameDay} | Funds: {GameData.Instance.gameFunds:F0}" +
                "\"}";

            using var client = new WebClient();
            client.Headers[HttpRequestHeader.ContentType] = "application/json";
            client.UploadString(DiscordWebhookUrl.Value, payload);
            Logger.LogInfo("AutoHost: Discord notification sent.");
        }
        catch (System.Exception ex)
        {
            Logger.LogWarning($"AutoHost: Discord webhook failed: {ex.Message}");
        }
    }
}

internal static class PluginInfo
{
    public const string Guid = "com.karelkryda.autohost";
    public const string Name = "AutoHost";
    public const string Version = "1.0.0";
}
