using UnityEngine;

namespace PCBS2Coop;

/// <summary>Maps one actor's normalized desktop point to this game's OS canvas and raycaster.
/// It does not touch InputModule's single local cursor or EventSystem selection.</summary>
internal static class NativeDesktopRaycast
{
    internal static bool TryScreenPoint(OS os, float x, float y, out Vector2 screen)
    {
        screen = default;
        if (os == null || !float.IsFinite(x) || !float.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
            return false;
        var image = os.m_desktop;
        var canvas = image?.canvas;
        var rect = image?.rectTransform;
        if (canvas == null || rect == null || rect.rect.width <= 0 || rect.rect.height <= 0)
            return false;
        var r = rect.rect;
        var world = rect.TransformPoint(new Vector3(r.xMin + x * r.width, r.yMin + y * r.height));
        screen = RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, world);
        return float.IsFinite(screen.x) && float.IsFinite(screen.y);
    }

    internal static GameObject TargetAt(OS os, float x, float y, out Vector2 screen)
    {
        screen = default;
        var caster = os?.GetRaycaster();
        if (caster == null || !TryScreenPoint(os, x, y, out screen)) return null;
        var hits = UnityExt.Raycast(caster, screen);
        return hits != null && hits.Count > 0 ? hits[0].gameObject : null;
    }
}
