using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.UIElements;

namespace ToasterPerformance;

// Two per-frame UI/scene allocators surfaced by ToasterProfiler:
//
//   SpectatorManager.Update — `spectatorPositionSpectatorMap.Values.ToList<Spectator>()` per
//     frame to index into. With ~hundreds of spectators in the stands, that's a per-frame
//     List<Spectator> alloc (~4KB avg measured = ~1.5 MB/sec of garbage).
//     → Maintain a cached List<Spectator> kept in sync with Register/Unregister.
//
//   UIMinimap.Update — `value.Query("Body", null)` inside per-player foreach. UQuery objects
//     allocate. ~7.9 MB / 30s measured.
//     → Cache the per-player "Body" VisualElement on AddPlayerBody; look up directly in Update.
public static class PatchSpectatorAndMinimap
{
    // ---- SpectatorManager.Update -----------------------------------------------------
    private static readonly AccessTools.FieldRef<SpectatorManager, Dictionary<SpectatorPosition, Spectator>> s_specMap
        = AccessTools.FieldRefAccess<SpectatorManager, Dictionary<SpectatorPosition, Spectator>>("spectatorPositionSpectatorMap");
    private static readonly AccessTools.FieldRef<SpectatorManager, int> s_updateBatch
        = AccessTools.FieldRefAccess<SpectatorManager, int>("updateBatch");

    // Use a static field for the per-frame updates field as a serialized inspector value.
    // It's private on SpectatorManager — read via reflection on first call.
    private static System.Reflection.FieldInfo s_specUpdatesPerFrameField;

    private static readonly List<Spectator> s_spectatorsCache = new(128);
    private static bool s_specDirty = true;

    [HarmonyPatch(typeof(SpectatorManager), nameof(SpectatorManager.RegisterSpectatorPosition))]
    public static class PatchSpecAdd
    {
        [HarmonyPostfix] public static void Postfix() => s_specDirty = true;
    }

    [HarmonyPatch(typeof(SpectatorManager), nameof(SpectatorManager.UnregisterSpectatorPosition))]
    public static class PatchSpecRemove
    {
        [HarmonyPostfix] public static void Postfix() => s_specDirty = true;
    }

    [HarmonyPatch(typeof(SpectatorManager), "Update")]
    public static class PatchSpecUpdate
    {
        [HarmonyPrefix]
        public static bool Prefix(SpectatorManager __instance)
        {
            var map = s_specMap(__instance);
            if (s_specDirty)
            {
                s_spectatorsCache.Clear();
                foreach (var kv in map) s_spectatorsCache.Add(kv.Value);
                s_specDirty = false;
            }
            int count = s_spectatorsCache.Count;
            if (count == 0) return false;

            if (s_specUpdatesPerFrameField == null)
                s_specUpdatesPerFrameField = AccessTools.Field(typeof(SpectatorManager), "spectatorUpdatesPerFrame");
            int updatesPerFrame = (int)s_specUpdatesPerFrameField.GetValue(__instance);

            int batch = s_updateBatch(__instance);
            for (int i = 0; i < updatesPerFrame; i++)
            {
                int idx = (batch + i) % count;
                var spec = s_spectatorsCache[idx];
                if (spec != null) spec.UpdateAnimation();
            }
            s_updateBatch(__instance) = (batch + updatesPerFrame) % count;
            return false;
        }
    }

    // ---- UIMinimap.Update ------------------------------------------------------------
    // Cache the per-player "Body" child VisualElement so we skip the per-frame Query.
    // Per-instance state keyed by VisualElement reference (the value in the Player/Puck dicts).
    private static readonly Dictionary<VisualElement, VisualElement> s_minimapBodyCache = new();

    [HarmonyPatch(typeof(UIMinimap), "Update")]
    public static class PatchMinimapUpdate
    {
        [HarmonyPrefix]
        public static bool Prefix(UIMinimap __instance)
        {
            if (ApplicationManager.IsDedicatedGameServer) return false;

            // Accumulator + rate-limit check (mirroring original)
            ref float accum = ref s_minimapAccum(__instance);
            int rate = s_minimapUpdateRate(__instance);
            accum += UnityEngine.Time.deltaTime;
            if (accum < 1f / rate) return false;
            accum = 0f;

            var team = s_minimapTeam(__instance);
            var bounds = __instance.Bounds;
            bool isBlue = team == PlayerTeam.Blue;

            var playerMap = s_minimapPlayerMap(__instance);
            foreach (var kv in playerMap)
            {
                var key = kv.Key;
                var value = kv.Value;
                if (key == null) continue;

                if (!s_minimapBodyCache.TryGetValue(value, out var bodyVe))
                {
                    bodyVe = value.Q<VisualElement>("Body");
                    if (bodyVe != null) s_minimapBodyCache[value] = bodyVe;
                }

                var pos = isBlue ? key.transform.position : -key.transform.position;
                float yaw = isBlue ? key.transform.rotation.eulerAngles.y : key.transform.rotation.eulerAngles.y + 180f;
                var minimapPos = WorldToMinimap(__instance, pos, bounds);
                value.style.translate = new Translate(-minimapPos.x, minimapPos.y);
                if (bodyVe != null) bodyVe.style.rotate = new Rotate(yaw);
            }

            var puckMap = s_minimapPuckMap(__instance);
            foreach (var kv in puckMap)
            {
                var key = kv.Key;
                var value = kv.Value;
                if (key == null) continue;
                var pos = isBlue ? key.transform.position : -key.transform.position;
                float yaw = isBlue ? key.transform.rotation.eulerAngles.y : key.transform.rotation.eulerAngles.y + 180f;
                var minimapPos = WorldToMinimap(__instance, pos, bounds);
                value.style.translate = new Translate(-minimapPos.x, minimapPos.y);
                value.style.rotate = new Rotate(yaw);
            }
            return false;
        }
    }

    // Drop the body cache when the VisualElement is removed.
    [HarmonyPatch(typeof(UIMinimap), nameof(UIMinimap.RemovePlayerBody))]
    public static class PatchMinimapRemovePlayer
    {
        [HarmonyPrefix]
        public static void Prefix(UIMinimap __instance, PlayerBody playerBody)
        {
            if (playerBody == null) return;
            var map = s_minimapPlayerMap(__instance);
            if (map.TryGetValue(playerBody, out var ve) && ve != null)
                s_minimapBodyCache.Remove(ve);
        }
    }

    // Replicates UIMinimap.WorldPositionToMinimapPosition (private).
    private static UnityEngine.Vector2 WorldToMinimap(UIMinimap mgr, UnityEngine.Vector3 pos, UnityEngine.Bounds bounds)
    {
        var content = s_minimapContent(mgr);
        var contentSize = new UnityEngine.Vector2(content.resolvedStyle.width, content.resolvedStyle.height);
        var normalized = new UnityEngine.Vector2(
            (pos.x + bounds.center.x) / bounds.size.x,
            (pos.z + bounds.center.z) / bounds.size.z);
        return new UnityEngine.Vector2(contentSize.x * normalized.x, contentSize.y * normalized.y);
    }

    // ---- Reflection accessors for UIMinimap private fields ---------------------------
    private static readonly AccessTools.FieldRef<UIMinimap, float> s_minimapAccum
        = AccessTools.FieldRefAccess<UIMinimap, float>("updateAccumulator");
    private static readonly AccessTools.FieldRef<UIMinimap, int> s_minimapUpdateRate
        = AccessTools.FieldRefAccess<UIMinimap, int>("updateRate");
    private static readonly AccessTools.FieldRef<UIMinimap, PlayerTeam> s_minimapTeam
        = AccessTools.FieldRefAccess<UIMinimap, PlayerTeam>("Team");
    private static readonly AccessTools.FieldRef<UIMinimap, Dictionary<PlayerBody, VisualElement>> s_minimapPlayerMap
        = AccessTools.FieldRefAccess<UIMinimap, Dictionary<PlayerBody, VisualElement>>("playerBodyVisualElementMap");
    private static readonly AccessTools.FieldRef<UIMinimap, Dictionary<Puck, VisualElement>> s_minimapPuckMap
        = AccessTools.FieldRefAccess<UIMinimap, Dictionary<Puck, VisualElement>>("puckVisualElementMap");
    private static readonly AccessTools.FieldRef<UIMinimap, VisualElement> s_minimapContent
        = AccessTools.FieldRefAccess<UIMinimap, VisualElement>("content");
}
