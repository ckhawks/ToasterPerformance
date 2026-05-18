using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ToasterPerfPatches;

// Several vanilla Puck event fire-sites allocate a Dictionary<string, object> before
// calling EventManager.TriggerEvent — which means the allocation happens even when
// nobody is listening. On a dedicated server, no UI subscribes to these per-tick
// gameplay events, so they fire 100s of times per second into the void.
//
// Measured (60s server capture, 2 players):
//   Event_Everyone_OnPlayerBodySpeedChanged: 20,461 fires, 3.84 MB allocated, no server listener
//   Event_Everyone_OnPlayerBodyStaminaChanged: 10,747 fires, 1.81 MB allocated, no server listener
//   Event_Everyone_OnPlayerPingChanged: 102 fires, 112 KB allocated, no server listener
//
// Patch: prefix each fire-site method, check EventManager.events for the event name,
// skip the whole method body if no listener is registered.
public static class PatchListenerlessEvents
{
    private static readonly FieldInfo s_eventsField = AccessTools.Field(typeof(EventManager), "events");

    private static bool HasListener(string eventName)
    {
        var events = (Dictionary<string, Action<Dictionary<string, object>>>)s_eventsField.GetValue(null);
        return events != null && events.ContainsKey(eventName);
    }

    [HarmonyPatch(typeof(PlayerBody), "OnSpeedChanged")]
    public static class PatchSpeedChanged
    {
        [HarmonyPrefix]
        public static bool Prefix() => HasListener("Event_Everyone_OnPlayerBodySpeedChanged");
    }

    [HarmonyPatch(typeof(PlayerBody), "OnStaminaChanged")]
    public static class PatchStaminaChanged
    {
        [HarmonyPrefix]
        public static bool Prefix() => HasListener("Event_Everyone_OnPlayerBodyStaminaChanged");
    }

    [HarmonyPatch(typeof(Player), "OnPlayerPingChanged")]
    public static class PatchPingChanged
    {
        [HarmonyPrefix]
        public static bool Prefix() => HasListener("Event_Everyone_OnPlayerPingChanged");
    }

    // Strip UIHUDController-owned listeners from the three relevant events. The HUD's
    // listeners are no-ops on dedi (they require IsLocalPlayer), so removing them lets
    // the fire-site Prefixes elide the Dictionary allocation at OnSpeedChanged et al.
    //
    // Run via a hidden MonoBehaviour driver because Plugin.OnEnable fires before the
    // UI scene Awakes; we need to scan once initial subs settle, then again periodically
    // to catch any post-scene-reload re-instantiation.
    public static void CleanupUIHUDListenersIfDedi()
    {
        if (!IsDedicatedServer()) return;
        StripUIHUDListenersFor("Event_Everyone_OnPlayerBodySpeedChanged");
        StripUIHUDListenersFor("Event_Everyone_OnPlayerBodyStaminaChanged");
    }

    private static void StripUIHUDListenersFor(string eventName)
    {
        var events = (Dictionary<string, Action<Dictionary<string, object>>>)s_eventsField.GetValue(null);
        if (events == null || !events.TryGetValue(eventName, out var combined) || combined == null) return;

        var invList = combined.GetInvocationList();
        foreach (var d in invList)
        {
            if (d.Target is UIHUDController)
            {
                EventManager.RemoveEventListener(eventName, (Action<Dictionary<string, object>>)d);
            }
        }
    }

    private static bool IsDedicatedServer()
        => UnityEngine.SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
}

// Drives the periodic cleanup. Spawned by Plugin.OnEnable on dedi.
internal class HUDListenerCleanupDriver : UnityEngine.MonoBehaviour
{
    float _nextScan;
    void Update()
    {
        if (UnityEngine.Time.unscaledTime < _nextScan) return;
        _nextScan = UnityEngine.Time.unscaledTime + 5f; // every 5s
        PatchListenerlessEvents.CleanupUIHUDListenersIfDedi();
    }
}
