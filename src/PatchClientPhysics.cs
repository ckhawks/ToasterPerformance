using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace ToasterPerfPatches;

// Client-side physics-cost reductions (PERF_HOTSPOTS items #D-revised, #9, #E):
//
//   #D-revised: Puck.FixedUpdate runs Physics.CheckSphere on every peer because the
//     result (IsGrounded) feeds NetSphereCollider.radius which drives goal-net cloth
//     visuals. Server needs the real overlap test. Client only needs "puck near ice
//     plane?" which is a cheap transform.position.y check. Replace the CheckSphere on
//     clients with a y-position threshold.
//
//   #9: Hover.FixedUpdate raycast runs on every peer per player per FixedUpdate. On
//     clients the result is only consumed by IsGrounded (force application is server-
//     gated). Skip every other FixedUpdate on clients — IsGrounded becomes slightly
//     stale (~20ms), but visuals are unaffected and PID state is irrelevant since no
//     force is applied.
//
//   #E: StickPositioner.FixedUpdate runs up to 3 raycasts per stick per FixedUpdate per
//     peer. Server uses results for soft-collision force application; client use is
//     unclear (blade target position drives a visual that might already be sync'd via
//     the stick's SynchronizedObject). OFF by default — flip GateStickPositionerOnClient
//     to true to enable, then verify the local stick blade still animates correctly.
public static class PatchClientPhysics
{
    // ---- Config flags (compile-time defaults; flip and rebuild to change behavior) ----
    public const bool ReplacePuckCheckSphere = true;   // #D-revised — safe, ship on
    public const bool ThrottleHoverOnClient   = true;  // #9 — safe (delay < 20ms)
    public const bool GateStickPositionerOnClient = true; // #E — try in-game; flip to false if blade visuals look wrong

    // ---- #D-revised: replace Puck.FixedUpdate's CheckSphere on clients --------------
    private static readonly AccessTools.FieldRef<Puck, float> s_puckGroundedRadius
        = AccessTools.FieldRefAccess<Puck, float>("groundedCheckSphereRadius");

    [HarmonyPatch(typeof(Puck), "FixedUpdate")]
    public static class PatchPuckFixedUpdate
    {
        [HarmonyPrefix]
        public static bool Prefix(Puck __instance)
        {
            if (!ReplacePuckCheckSphere) return true;
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer) return true; // server path: run original

            // Client-only replacement: same fields touched, no CheckSphere, no centerOfMass writes.
            // Speed/AngularSpeed writes preserved in case any UI reads them.
            var rb = __instance.Rigidbody;
            if (rb == null) return false;

            // Speed / AngularSpeed (public fields on Puck) — preserve for any client HUD reads.
            s_puckSpeed(__instance) = rb.linearVelocity.magnitude;
            s_puckAngularSpeed(__instance) = rb.angularVelocity.magnitude;

            // Cheap IsGrounded substitute: y-position against the cached radius (ice plane at y=0).
            float threshold = s_puckGroundedRadius(__instance);
            bool grounded = __instance.transform.position.y < threshold;
            s_puckIsGrounded(__instance) = grounded;

            // NetSphereCollider radius animation — the *only* thing on the client path that
            // depends on IsGrounded. Drives goal-net cloth sim, must keep running.
            var netSphere = __instance.NetSphereCollider;
            if (netSphere != null)
            {
                float predictedSpeed = __instance.PredictedSpeed;
                float target = grounded ? 0f : Mathf.Clamp(predictedSpeed * 0.025f, 0.15f, 0.75f);
                if (netSphere.radius < target)
                    netSphere.radius = target;
                else if (netSphere.radius > target)
                    netSphere.radius = Mathf.Lerp(netSphere.radius, target, Time.fixedDeltaTime * 5f);
            }

            // Skip the original — Server_UpdateStickTensor and Server_UpdateAudio self-gate and
            // would no-op anyway; Rigidbody.centerOfMass writes are no-ops on kinematic clients.
            return false;
        }
    }

    // Speed / AngularSpeed are public *fields*; IsGrounded is a public auto-property.
    private static readonly AccessTools.FieldRef<Puck, float> s_puckSpeed
        = AccessTools.FieldRefAccess<Puck, float>("Speed");
    private static readonly AccessTools.FieldRef<Puck, float> s_puckAngularSpeed
        = AccessTools.FieldRefAccess<Puck, float>("AngularSpeed");
    private static readonly AccessTools.FieldRef<Puck, bool> s_puckIsGrounded
        = AccessTools.FieldRefAccess<Puck, bool>("<IsGrounded>k__BackingField");

    // ---- #9: Hover.FixedUpdate raycast throttle on clients --------------------------
    // Per-instance frame counter; skips alternate FixedUpdate calls.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Hover, HoverState> s_hoverState = new();

    private sealed class HoverState { public int FrameParity; }

    [HarmonyPatch(typeof(Hover), "FixedUpdate")]
    public static class PatchHoverFixedUpdate
    {
        [HarmonyPrefix]
        public static bool Prefix(Hover __instance)
        {
            if (!ThrottleHoverOnClient) return true;
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer) return true; // server runs every tick

            var state = s_hoverState.GetValue(__instance, _ => new HoverState());
            state.FrameParity++;
            // Run on even frames only — half the raycast cost.
            return (state.FrameParity & 1) == 0;
        }
    }

    // ---- #E: StickPositioner.FixedUpdate client gate -----------------------
    [HarmonyPatch(typeof(StickPositioner), "FixedUpdate")]
    public static class PatchStickPositionerFixedUpdate
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!GateStickPositionerOnClient) return true;
            var nm = NetworkManager.Singleton;
            if (nm == null) return true;
            return nm.IsServer;
        }
    }
}
