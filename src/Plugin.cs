using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Exception = System.Exception;

namespace ToasterPerfPatches;

public class Plugin : IPuckPlugin
{
    public static string MOD_NAME = "ToasterPerfPatches";
    public static string MOD_VERSION = "0.1.0";
    public static string MOD_GUID = "pw.stellaric.toaster.perfpatches";

    static readonly Harmony harmony = new Harmony(MOD_GUID);

    public bool OnEnable()
    {
        Plugin.Log($"Enabling {MOD_VERSION}...");
        try
        {
            if (IsDedicatedServer())
                Plugin.Log("Environment: dedicated server.");
            else
                Plugin.Log("Environment: client.");

            Plugin.Log("Patching methods...");
            harmony.PatchAll();
            Plugin.Log("All patched!");

            // Dedi-only: periodically strip the no-op UIHUDController listeners that
            // get added during scene init (before our Harmony patches can intercept Awake).
            if (IsDedicatedServer())
            {
                var go = new GameObject("ToasterPerfPatches_HUDListenerCleanup");
                Object.DontDestroyOnLoad(go);
                go.AddComponent<HUDListenerCleanupDriver>();
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError($"Failed to Enable: {e}");
            return false;
        }
    }

    public bool OnDisable()
    {
        try
        {
            Plugin.Log("Disabling...");
            harmony.UnpatchSelf();
            Plugin.Log("Disabled.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError($"Failed to disable: {e}");
            return false;
        }
    }

    public static bool IsDedicatedServer()
    {
        return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
    }

    public static void Log(string message) => Debug.Log($"[{MOD_NAME}] {message}");
    public static void LogError(string message) => Debug.LogError($"[{MOD_NAME}] {message}");
    public static void LogWarning(string message) => Debug.LogWarning($"[{MOD_NAME}] {message}");
}
