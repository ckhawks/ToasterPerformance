using System.Collections.Generic;
using HarmonyLib;

namespace ToasterPerfPatches;

// Patches for NetworkObjectCollisionRecorder + GetPlayerPuck (#5, #6, #G):
//
//   #6 NetworkObjectCollisions getter does `Buffer.AsNativeArray().ToList()` per access.
//      A per-frame reader allocates a fresh List<NetworkObjectCollision> each call.
//      → Cache the list per-instance, invalidate in OnBufferChanged.
//
//   #5 PuckManager.GetPlayerPuck does `.LastOrDefault<NetworkObjectCollision>()` on top of
//      #6's allocation, allocating a LINQ enumerator on top of the fresh List.
//      → Direct NativeArray index access against Buffer.
//
//   #G (optional, not shipped here) — a player→puck dict updated on collision events would
//      remove the chain entirely. Skip until profiling proves it matters; #5+#6 already
//      kill the per-call allocations.
public static class PatchCollisionRecorder
{
    // ---- Per-instance cache for NetworkObjectCollisions getter (#6) ------------------
    // Keyed by recorder instance. We use a plain Dictionary keyed by reference rather than
    // ConditionalWeakTable so we can also iterate for cleanup if needed; entries are removed
    // on OnNetworkDespawn.
    private sealed class CacheEntry
    {
        public readonly List<NetworkObjectCollision> List = new(8);
        public bool Dirty = true;
    }

    private static readonly Dictionary<NetworkObjectCollisionRecorder, CacheEntry> s_cache = new();

    private static CacheEntry GetOrCreate(NetworkObjectCollisionRecorder recorder)
    {
        if (!s_cache.TryGetValue(recorder, out var entry))
        {
            entry = new CacheEntry();
            s_cache[recorder] = entry;
        }
        return entry;
    }

    [HarmonyPatch(typeof(NetworkObjectCollisionRecorder), nameof(NetworkObjectCollisionRecorder.NetworkObjectCollisions), MethodType.Getter)]
    public static class PatchGetter
    {
        [HarmonyPrefix]
        public static bool Prefix(NetworkObjectCollisionRecorder __instance, ref List<NetworkObjectCollision> __result)
        {
            var entry = GetOrCreate(__instance);
            if (entry.Dirty)
            {
                entry.List.Clear();
                if (__instance.Buffer != null)
                {
                    var arr = __instance.Buffer.AsNativeArray();
                    for (int i = 0; i < arr.Length; i++) entry.List.Add(arr[i]);
                }
                entry.Dirty = false;
            }
            __result = entry.List;
            return false;
        }
    }

    // OnBufferChanged is private — find it by name.
    [HarmonyPatch]
    public static class PatchOnBufferChanged
    {
        public static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(typeof(NetworkObjectCollisionRecorder), "OnBufferChanged");

        [HarmonyPostfix]
        public static void Postfix(NetworkObjectCollisionRecorder __instance)
        {
            if (s_cache.TryGetValue(__instance, out var entry))
                entry.Dirty = true;
        }
    }

    [HarmonyPatch(typeof(NetworkObjectCollisionRecorder), nameof(NetworkObjectCollisionRecorder.OnNetworkDespawn))]
    public static class PatchDespawn
    {
        [HarmonyPostfix]
        public static void Postfix(NetworkObjectCollisionRecorder __instance)
        {
            s_cache.Remove(__instance);
        }
    }

    // ---- GetPlayerPuck rewrite (#5) --------------------------------------------------
    [HarmonyPatch(typeof(PuckManager), nameof(PuckManager.GetPlayerPuck))]
    public static class PatchGetPlayerPuck
    {
        [HarmonyPrefix]
        public static bool Prefix(ulong clientId, ref Puck __result)
        {
            var player = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(clientId);
            if (player == null || !player) { __result = null; return false; }
            if (player.Stick == null || !player.Stick) { __result = null; return false; }

            var recorder = player.Stick.NetworkObjectCollisionRecorder;
            if (recorder == null || recorder.Buffer == null) { __result = null; return false; }

            var arr = recorder.Buffer.AsNativeArray();
            if (arr.Length == 0) { __result = null; return false; }

            var lastRef = arr[arr.Length - 1].NetworkObjectReference;
            __result = NetworkingUtils.GetPuckFromNetworkObjectReference(lastRef);
            return false;
        }
    }
}
