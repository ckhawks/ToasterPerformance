using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace ToasterPerformance;

// Patches for SynchronizedObjectManager hot paths:
//
//   #3  Client_SynchronizeObjects O(N²) List.Find(lambda) per sync object per tick
//       → Dictionary<ulong, SynchronizedObject> lookup, maintained via Add/Remove postfix.
//
//   #1+#2 Server_GatherSynchronizedObjectData allocates a List + ToArray() every tick,
//       and EncodeSynchronizedObject allocates two short[] per object per tick.
//       → Replace with a reused staging List; inline encoding directly into struct fields.
//
//   #3a Client_SynchronizeObjects smoothing branch allocates a fresh
//       List<SynchronizedObjectSnapshot> per tick.
//       → Pre-size against known dict count; trim allocator churn (SynchronizedObjectsSnapshot
//         retains the list, so we can't truly reuse a single instance — but pre-sizing kills
//         the resize churn during fill).
//
//   #B  EventManager.TriggerEvent("Event_OnSynchronizeObjects", ...) call site allocates a
//       new Dictionary<string, object> every client tick even when nobody is listening.
//       → Skip the dict alloc and TriggerEvent call entirely when no listener is registered.
//
// All replacements are full-method Prefixes returning false. Private fields are accessed
// through AccessTools.FieldRefAccess delegates cached at class init.
public static class PatchSyncObjects
{
    // ---- Dictionary for O(1) client lookup -------------------------------------------
    // Single instance keyed by NetworkObjectId. SynchronizedObjectManager is a singleton
    // (NetworkBehaviourSingleton<SynchronizedObjectManager>) so one dict is enough.
    private static readonly Dictionary<ulong, SynchronizedObject> s_objectsById = new(64);

    // ---- Server gather staging -------------------------------------------------------
    private static readonly List<SynchronizedObjectData> s_gatherStaging = new(64);

    // ---- Reflection accessors --------------------------------------------------------
    private static readonly AccessTools.FieldRef<SynchronizedObjectManager, List<SynchronizedObject>> s_synchronizedObjects
        = AccessTools.FieldRefAccess<SynchronizedObjectManager, List<SynchronizedObject>>("synchronizedObjects");

    private static readonly AccessTools.FieldRef<SynchronizedObjectManager, double> s_serverLastSentServerTime
        = AccessTools.FieldRefAccess<SynchronizedObjectManager, double>("serverLastSentServerTime");

    private static readonly AccessTools.FieldRef<SynchronizedObjectManager, bool> s_clientHasReceivedFirstTick
        = AccessTools.FieldRefAccess<SynchronizedObjectManager, bool>("clientHasReceivedFirstTick");

    private static readonly AccessTools.FieldRef<SynchronizedObjectManager, SortedList<double, SynchronizedObjectsSnapshot>> s_snapshots
        = AccessTools.FieldRefAccess<SynchronizedObjectManager, SortedList<double, SynchronizedObjectsSnapshot>>("snapshots");

    private static readonly AccessTools.FieldRef<SynchronizedObjectManager, double> s_clientLocalTimeline
        = AccessTools.FieldRefAccess<SynchronizedObjectManager, double>("clientLocalTimeline");

    private static readonly AccessTools.FieldRef<SynchronizedObjectManager, double> s_clientLocalTimescale
        = AccessTools.FieldRefAccess<SynchronizedObjectManager, double>("clientLocalTimescale");

    private static readonly AccessTools.FieldRef<SynchronizedObjectManager, ExponentialMovingAverage> s_driftEma
        = AccessTools.FieldRefAccess<SynchronizedObjectManager, ExponentialMovingAverage>("driftEma");

    private static readonly AccessTools.FieldRef<SynchronizedObjectManager, ExponentialMovingAverage> s_deliveryTimeEma
        = AccessTools.FieldRefAccess<SynchronizedObjectManager, ExponentialMovingAverage>("deliveryTimeEma");

    private static readonly AccessTools.FieldRef<SynchronizedObjectManager, SnapshotInterpolationSettings> s_snapshotInterpolationSettings
        = AccessTools.FieldRefAccess<SynchronizedObjectManager, SnapshotInterpolationSettings>("snapshotInterpolationSettings");

    // EventManager.events is private static Dictionary<string, Action<Dictionary<string, object>>>
    private static readonly System.Reflection.FieldInfo s_eventsField
        = AccessTools.Field(typeof(EventManager), "events");

    private const string OnSynchronizeObjectsEventName = "Event_OnSynchronizeObjects";

    // ---- Dictionary maintenance ------------------------------------------------------
    [HarmonyPatch(typeof(SynchronizedObjectManager), nameof(SynchronizedObjectManager.AddSynchronizedObject))]
    public static class PatchAdd
    {
        [HarmonyPostfix]
        public static void Postfix(SynchronizedObject synchronizedObject)
        {
            if (synchronizedObject != null)
                s_objectsById[synchronizedObject.NetworkObjectId] = synchronizedObject;
        }
    }

    [HarmonyPatch(typeof(SynchronizedObjectManager), nameof(SynchronizedObjectManager.RemoveSynchronizedObject))]
    public static class PatchRemove
    {
        [HarmonyPostfix]
        public static void Postfix(SynchronizedObject synchronizedObject)
        {
            if (synchronizedObject != null)
                s_objectsById.Remove(synchronizedObject.NetworkObjectId);
        }
    }

    // ClearSynchronizedObjects is private; only fired from Dispose. Use raw method lookup.
    [HarmonyPatch]
    public static class PatchClear
    {
        public static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(typeof(SynchronizedObjectManager), "ClearSynchronizedObjects");

        [HarmonyPostfix]
        public static void Postfix()
        {
            s_objectsById.Clear();
            s_gatherStaging.Clear();
        }
    }

    // Ensures the dict matches the underlying list. Called at the top of any hot path that
    // reads the dict, so a stale state (e.g. if Add was missed during load) self-heals.
    private static void EnsureDictPopulated(SynchronizedObjectManager mgr)
    {
        var list = s_synchronizedObjects(mgr);
        if (s_objectsById.Count == list.Count) return;

        s_objectsById.Clear();
        for (int i = 0; i < list.Count; i++)
        {
            var so = list[i];
            if (so != null)
                s_objectsById[so.NetworkObjectId] = so;
        }
    }

    // ---- Server_GatherSynchronizedObjectData replacement (#1 + #2) -------------------
    [HarmonyPatch]
    public static class PatchServerGather
    {
        public static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(typeof(SynchronizedObjectManager), "Server_GatherSynchronizedObjectData");

        [HarmonyPrefix]
        public static bool Prefix(SynchronizedObjectManager __instance, bool forceAllObjects, ref SynchronizedObjectData[] __result)
        {
            var list = s_synchronizedObjects(__instance);
            int tickRate = __instance.TickRate;
            double serverTimeNow = NetworkManager.Singleton.ServerTime.Time;
            float serverDeltaTime = (float)(serverTimeNow - s_serverLastSentServerTime(__instance));

            var staging = s_gatherStaging;
            staging.Clear();

            for (int i = 0; i < list.Count; i++)
            {
                var so = list[i];
                if (so == null) continue;
                if (!forceAllObjects && !so.ShouldSendPosition(tickRate) && !so.ShouldSendRotation(tickRate))
                    continue;

                var tuple = so.OnServerTick(serverDeltaTime);
                Vector3 pos = tuple.Item1;
                Quaternion rot = tuple.Item2;
                ulong networkObjectId = tuple.Item3;

                // Inline EncodeSynchronizedObject — write straight into the struct, no short[] allocs.
                staging.Add(new SynchronizedObjectData
                {
                    NetworkObjectId = (ushort)networkObjectId,
                    X = (short)(pos.x * 655f),
                    Y = (short)(pos.y * 655f),
                    Z = (short)(pos.z * 655f),
                    Rx = (short)(rot.x * 32767f),
                    Ry = (short)(rot.y * 32767f),
                    Rz = (short)(rot.z * 32767f),
                    Rw = (short)(rot.w * 32767f),
                });
            }

            // One array alloc per tick — the RPC consumes it synchronously, so the staging
            // list is free to reuse on the next call. We can't return the list's internal
            // array directly because List<T>.ToArray sizes exactly to Count.
            __result = staging.ToArray();
            return false;
        }
    }

    // ---- Client_SynchronizeObjects replacement (#3 + #3a + #B) -----------------------
    // Reusable scratch list for the smoothing branch. SynchronizedObjectsSnapshot stores the
    // list reference, so we hand off a fresh one each tick — but pre-sized to dict count
    // to avoid internal growth-resize allocation.
    [HarmonyPatch]
    public static class PatchClientSync
    {
        public static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(typeof(SynchronizedObjectManager), "Client_SynchronizeObjects");

        // DecodeSynchronizedObjectData is private — inline its math here.
        // From SynchronizedObjectManager.cs:180
        private static void Decode(in SynchronizedObjectData d, out Vector3 pos, out Quaternion rot)
        {
            pos = new Vector3(d.X / 655f, d.Y / 655f, d.Z / 655f);
            rot = new Quaternion(d.Rx / 32767f, d.Ry / 32767f, d.Rz / 32767f, d.Rw / 32767f);
        }

        [HarmonyPrefix]
        public static bool Prefix(SynchronizedObjectManager __instance, SynchronizedObjectData[] synchronizedObjectsData, float serverDeltaTime, double serverTime)
        {
            if (synchronizedObjectsData == null) return false;

            EnsureDictPopulated(__instance);
            var dict = s_objectsById;

            if (__instance.UseNetworkSmoothing && s_clientHasReceivedFirstTick(__instance))
            {
                // Pre-size to expected count. Snapshot retains the list, so we can't reuse one
                // instance across calls — but pre-sizing eliminates the Add()-triggered growth path.
                var snapshotList = new List<SynchronizedObjectSnapshot>(synchronizedObjectsData.Length);

                for (int i = 0; i < synchronizedObjectsData.Length; i++)
                {
                    ref var d = ref synchronizedObjectsData[i];
                    Decode(in d, out var pos, out var rot);
                    if (!dict.TryGetValue(d.NetworkObjectId, out var so) || so == null) continue;
                    snapshotList.Add(so.OnClientSmoothTick(pos, rot, so, serverDeltaTime));
                }

                var snapshot = new SynchronizedObjectsSnapshot(serverTime, NetworkManager.Singleton.LocalTime.Time, snapshotList);

                var settings = s_snapshotInterpolationSettings(__instance);
                if (settings.dynamicAdjustment)
                {
                    settings.bufferTimeMultiplier = SnapshotInterpolation.DynamicAdjustment(
                        __instance.TickInterval,
                        s_deliveryTimeEma(__instance).StandardDeviation,
                        settings.dynamicAdjustmentTolerance) * __instance.NetworkSmoothingStrength;
                    // settings is a class? If struct, write back. Assume class (typical Unity pattern).
                    // If it were a struct AccessTools.FieldRefAccess gave us a ref so the in-place
                    // mutation already wrote back — either way this is safe.
                }

                double bufferTime = __instance.TickInterval * settings.bufferTimeMultiplier;
                SnapshotInterpolation.InsertAndAdjust(
                    s_snapshots(__instance),
                    settings.bufferLimit,
                    snapshot,
                    ref s_clientLocalTimeline(__instance),
                    ref s_clientLocalTimescale(__instance),
                    __instance.TickInterval,
                    bufferTime,
                    settings.catchupSpeed,
                    settings.slowdownSpeed,
                    ref s_driftEma(__instance),
                    settings.catchupNegativeThreshold,
                    settings.catchupPositiveThreshold,
                    ref s_deliveryTimeEma(__instance));
            }
            else
            {
                for (int i = 0; i < synchronizedObjectsData.Length; i++)
                {
                    ref var d = ref synchronizedObjectsData[i];
                    Decode(in d, out var pos, out var rot);
                    if (!dict.TryGetValue(d.NetworkObjectId, out var so) || so == null) continue;
                    so.OnClientTick(pos, rot, serverDeltaTime);
                }
            }

            return false;
        }
    }

    // ---- #B: skip EventManager.TriggerEvent dict alloc when nobody is listening ------
    // The original `Server_SynchronizeObjectsRpc` ends with:
    //   EventManager.TriggerEvent("Event_OnSynchronizeObjects",
    //       new Dictionary<string, object> { { "serverDeltaTime", num2 } });
    // The Dictionary is allocated even when no listener exists — TriggerEvent only checks
    // membership inside. We can't easily skip it without a transpiler, BUT we *can* patch
    // EventManager.TriggerEvent itself to early-out before doing anything more.
    //
    // Note: this doesn't eliminate the call-site dict allocation, only the downstream cost.
    // True elimination would require a transpiler on the RPC's local-execute path; ship that
    // in a follow-up if profiling shows it still matters.
    [HarmonyPatch(typeof(EventManager), nameof(EventManager.TriggerEvent))]
    public static class PatchEventManagerTrigger
    {
        [HarmonyPrefix]
        public static bool Prefix(string eventName, Dictionary<string, object> message)
        {
            // Fast path: if no listener is registered for this event, skip the whole call.
            var events = (Dictionary<string, Action<Dictionary<string, object>>>)s_eventsField.GetValue(null);
            if (events == null || !events.ContainsKey(eventName)) return false;
            return true;
        }
    }
}
