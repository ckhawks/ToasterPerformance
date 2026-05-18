using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace ToasterPerformance;

// Patches that don't fit elsewhere:
//
//   #C UIPlayerUsernames.UsernameWorldToScreen calls Camera.main 3× per player per frame.
//      Camera.main is a tag lookup on every access. With 12 players × 3 × 60fps that's
//      2160 FindWithTag calls/sec on the client just from this UI.
//      → Cache the Camera reference once. Refresh lazily if it becomes null.
//
//   #F StickPositioner.OnGrounded calls LayerMask.NameToLayer("Ice") on every grounded
//      transition. Cache once.
//
// NOT shipped here:
//   #D Puck.FixedUpdate client gate — NetSphereCollider.radius is consumed by
//      GoalController's client-side cloth simulation, so we can't fully gate FixedUpdate
//      without breaking the goal net visuals. The CheckSphere saving alone (~50/sec per
//      puck) isn't worth the risk.
//   #E StickPositioner.FixedUpdate client gate — blade target position likely drives
//      client-side stick visuals. Needs in-game verification before shipping.
public static class PatchUIAndMisc
{
    // ---- #C: Camera.main caching for UIPlayerUsernames -------------------------------
    private static Camera s_cachedMainCamera;

    private static Camera GetMainCamera()
    {
        if (s_cachedMainCamera == null) s_cachedMainCamera = Camera.main;
        return s_cachedMainCamera;
    }

    // UsernameWorldToScreen is private — patch by name.
    [HarmonyPatch]
    public static class PatchUsernameWorldToScreen
    {
        public static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(typeof(UIPlayerUsernames), "UsernameWorldToScreen");

        [HarmonyPrefix]
        public static bool Prefix(UIPlayerUsernames __instance, VisualElement playerVisualElement, PlayerBody playerBody)
        {
            var cam = GetMainCamera();
            if (cam == null) return false;

            // Replicate the original logic, but with cached Camera.main and the yOffset / FadeThreshold
            // / FadeRange / MaximumDistance fields read via reflection (yOffset is private; the others
            // are public properties / fields).
            float yOffset = s_yOffsetField(__instance);
            float fadeThreshold = __instance.FadeThreshold;
            float maximumDistance = __instance.MaximumDistance;
            float fadeRange = __instance.FadeRange;
            VisualElement rootVisualElement = __instance.RootVisualElement;
            if (rootVisualElement == null || rootVisualElement.panel == null) return false;

            Vector3 camPos = cam.transform.position;
            Vector3 bodyPos = playerBody.transform.position;
            float distance = Vector3.Distance(camPos, bodyPos);

            Vector3 screen = cam.WorldToScreenPoint(bodyPos + Vector3.up * yOffset);
            screen.y = Screen.height - screen.y;
            Vector2 panelPos = RuntimePanelUtils.ScreenToPanel(rootVisualElement.panel, screen);

            if (screen.z < 0f)
            {
                playerVisualElement.style.display = DisplayStyle.None;
                return false;
            }

            float fadeStart = maximumDistance * fadeThreshold;
            float fadeEnd = fadeStart + fadeRange;
            float opacity = Mathf.Clamp01(Utils.Map(distance, fadeStart, fadeEnd, 1f, 0f));

            playerVisualElement.style.display = DisplayStyle.Flex;
            playerVisualElement.style.left = panelPos.x;
            playerVisualElement.style.top = panelPos.y;
            playerVisualElement.style.opacity = new StyleFloat(opacity);
            return false;
        }
    }

    private static readonly AccessTools.FieldRef<UIPlayerUsernames, float> s_yOffsetField
        = AccessTools.FieldRefAccess<UIPlayerUsernames, float>("yOffset");

    // #F (LayerMask cache on StickPositioner.OnGrounded) — REMOVED.
    // The original method re-implementation was crashing on clients (NullRef inside
    // the patched method, observed as constant errors that froze stick visuals). The
    // performance gain was tiny (one LayerMask.NameToLayer call per grounded transition),
    // not worth the risk. Reverting to vanilla.
}
