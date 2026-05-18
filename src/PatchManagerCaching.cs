using System.Collections.Generic;
using HarmonyLib;

namespace ToasterPerformance;

// Patches for PlayerManager and PuckManager (#4):
//
//   PlayerManager.GetPlayers(bool) runs RemoveAll(lambda) + (optional) Where(lambda).ToList()
//   on every call. Every helper (GetPlayerByClientId, GetPlayersByTeam, etc.) funnels through
//   it. Mirror for PuckManager.GetPucks. Both are called by gameplay logic, UI, spawn paths,
//   replay, vote manager — anywhere a player or puck list is needed.
//
//   Approach: maintain cached lists with a dirty flag. Postfix AddPlayer/RemovePlayer (and
//   AddPuck/RemovePuck) to mark dirty. On read, if dirty: rebuild — scanning the underlying
//   list once, copying live entries to the cache, batch-removing dead entries from the
//   underlying list (preserves the original RemoveAll contract).
//
//   We also do a cheap pre-validation on each read: if any cached entry has gone null or
//   !IsSpawned since the last rebuild (which can happen if a NetworkObject is destroyed
//   without RemovePlayer being called), mark dirty and rebuild. Cost: O(N) per call, same
//   as the original — but skips the Where+ToList+lambda allocations.
//
//   We do NOT patch the LINQ helpers (GetPlayersByPhase, etc.) — they're allocation-heavy
//   too but each is its own contract; patching them is a follow-up. Their upstream cost
//   (the GetPlayers call inside) drops from "RemoveAll + Where + ToList" to "validate cache
//   + return reference", which alone is a substantial win.
public static class PatchManagerCaching
{
    // ---- PlayerManager state ---------------------------------------------------------
    private static readonly AccessTools.FieldRef<PlayerManager, List<Player>> s_playersField
        = AccessTools.FieldRefAccess<PlayerManager, List<Player>>("players");

    // Not readonly — RebuildPlayerCache swaps these references rather than mutating the
    // lists in place. This protects any in-flight foreach over a previously-returned cache
    // list from a ConcurrentModification when a cascading event (e.g. NetworkObject.Despawn
    // → Event_Everyone_OnPlayerRemoved → some listener calls GetPlayers again) triggers a
    // rebuild during iteration. The old enumerator keeps reading its (now-orphaned) list
    // unharmed. See ReplayPlayer.Server_DisposeReplayObjects for the canonical caller.
    private static List<Player> s_cachedPlayersAll = new(16);
    private static List<Player> s_cachedPlayersNoReplay = new(16);
    private static List<Player> s_cachedReplayPlayers = new(4);
    private static bool s_playersDirty = true;

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.AddPlayer))]
    public static class PatchAddPlayer
    {
        [HarmonyPostfix] public static void Postfix() => s_playersDirty = true;
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.RemovePlayer))]
    public static class PatchRemovePlayer
    {
        [HarmonyPostfix] public static void Postfix() => s_playersDirty = true;
    }

    private static bool ValidatePlayerCache()
    {
        // Returns true if cache is usable; false if any entry has gone bad (caller should rebuild).
        // We only need to scan one cached list — the others rebuild together.
        var cache = s_cachedPlayersAll;
        for (int i = 0; i < cache.Count; i++)
        {
            var p = cache[i];
            if (p == null || p.NetworkObject == null || !p.NetworkObject.IsSpawned)
                return false;
        }
        return true;
    }

    private static void RebuildPlayerCache(PlayerManager mgr)
    {
        var underlying = s_playersField(mgr);

        // Build fresh lists so any in-flight enumerator on the previous cache survives.
        var all      = new List<Player>(underlying.Count);
        var noReplay = new List<Player>(underlying.Count);
        var replay   = new List<Player>(4);

        // Single forward scan: copy live entries to caches, batch-remove dead from underlying.
        int writeIndex = 0;
        for (int i = 0; i < underlying.Count; i++)
        {
            var p = underlying[i];
            if (p == null || p.NetworkObject == null || !p.NetworkObject.IsSpawned) continue;

            underlying[writeIndex++] = p;
            all.Add(p);
            if (p.IsReplay.Value) replay.Add(p);
            else noReplay.Add(p);
        }
        // Trim removed-tail entries from underlying.
        if (writeIndex < underlying.Count)
            underlying.RemoveRange(writeIndex, underlying.Count - writeIndex);

        s_cachedPlayersAll = all;
        s_cachedPlayersNoReplay = noReplay;
        s_cachedReplayPlayers = replay;
        s_playersDirty = false;
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.GetPlayers))]
    public static class PatchGetPlayers
    {
        [HarmonyPrefix]
        public static bool Prefix(PlayerManager __instance, bool includeReplay, ref List<Player> __result)
        {
            if (!s_playersDirty && !ValidatePlayerCache()) s_playersDirty = true;
            if (s_playersDirty) RebuildPlayerCache(__instance);

            // Original returns the underlying list directly when includeReplay is true;
            // callers may mutate it. Returning our cached reference preserves that semantics
            // for read access, but mutations against the returned list would desync from
            // the underlying. None of the in-repo callers mutate the GetPlayers return.
            __result = includeReplay ? s_cachedPlayersAll : s_cachedPlayersNoReplay;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.GetReplayPlayers))]
    public static class PatchGetReplayPlayers
    {
        [HarmonyPrefix]
        public static bool Prefix(PlayerManager __instance, ref List<Player> __result)
        {
            if (!s_playersDirty && !ValidatePlayerCache()) s_playersDirty = true;
            if (s_playersDirty) RebuildPlayerCache(__instance);

            __result = s_cachedReplayPlayers;
            return false;
        }
    }

    // ---- PuckManager state -----------------------------------------------------------
    private static readonly AccessTools.FieldRef<PuckManager, List<Puck>> s_pucksField
        = AccessTools.FieldRefAccess<PuckManager, List<Puck>>("pucks");

    // Not readonly — see PlayerManager comment above. Same rationale: swap references on
    // rebuild instead of mutating in place, so foreachs that trigger cascading despawns
    // don't blow up on ConcurrentModification.
    private static List<Puck> s_cachedPucksAll = new(8);
    private static List<Puck> s_cachedPucksNoReplay = new(8);
    private static List<Puck> s_cachedReplayPucks = new(4);
    private static bool s_pucksDirty = true;

    [HarmonyPatch(typeof(PuckManager), nameof(PuckManager.AddPuck))]
    public static class PatchAddPuck
    {
        [HarmonyPostfix] public static void Postfix() => s_pucksDirty = true;
    }

    [HarmonyPatch(typeof(PuckManager), nameof(PuckManager.RemovePuck))]
    public static class PatchRemovePuck
    {
        [HarmonyPostfix] public static void Postfix() => s_pucksDirty = true;
    }

    private static bool ValidatePuckCache()
    {
        var cache = s_cachedPucksAll;
        for (int i = 0; i < cache.Count; i++)
        {
            var p = cache[i];
            // PuckManager.GetPucks doesn't filter on IsSpawned — only checks for null via implicit Unity bool.
            if (p == null) return false;
        }
        return true;
    }

    private static void RebuildPuckCache(PuckManager mgr)
    {
        var underlying = s_pucksField(mgr);

        var all      = new List<Puck>(underlying.Count);
        var noReplay = new List<Puck>(underlying.Count);
        var replay   = new List<Puck>(4);

        int writeIndex = 0;
        for (int i = 0; i < underlying.Count; i++)
        {
            var p = underlying[i];
            if (p == null) continue;
            underlying[writeIndex++] = p;
            all.Add(p);
            if (p.IsReplay.Value) replay.Add(p);
            else noReplay.Add(p);
        }
        if (writeIndex < underlying.Count)
            underlying.RemoveRange(writeIndex, underlying.Count - writeIndex);

        s_cachedPucksAll = all;
        s_cachedPucksNoReplay = noReplay;
        s_cachedReplayPucks = replay;

        s_pucksDirty = false;
    }

    [HarmonyPatch(typeof(PuckManager), nameof(PuckManager.GetPucks))]
    public static class PatchGetPucks
    {
        [HarmonyPrefix]
        public static bool Prefix(PuckManager __instance, bool includeReplay, ref List<Puck> __result)
        {
            if (!s_pucksDirty && !ValidatePuckCache()) s_pucksDirty = true;
            if (s_pucksDirty) RebuildPuckCache(__instance);

            __result = includeReplay ? s_cachedPucksAll : s_cachedPucksNoReplay;
            return false;
        }
    }

    [HarmonyPatch(typeof(PuckManager), nameof(PuckManager.GetReplayPucks))]
    public static class PatchGetReplayPucks
    {
        [HarmonyPrefix]
        public static bool Prefix(PuckManager __instance, ref List<Puck> __result)
        {
            if (!s_pucksDirty && !ValidatePuckCache()) s_pucksDirty = true;
            if (s_pucksDirty) RebuildPuckCache(__instance);

            __result = s_cachedReplayPucks;
            return false;
        }
    }
}
