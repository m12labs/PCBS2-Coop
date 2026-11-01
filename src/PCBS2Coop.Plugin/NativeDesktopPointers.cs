using PCBS2Coop.Network;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Draws actor labels over each locally rendered PC desktop. The game's own software cursor
/// remains the local pointer; the remote label includes its pointer mark. These visuals never receive hits.</summary>
internal sealed class NativeDesktopPointers
{
    private sealed class Overlay
    {
        public long Canvas;
        public RectTransform Root;
        public TextMeshProUGUI Local, Remote;
    }

    private readonly Dictionary<NetObjectId, Overlay> _overlays = new();
    private readonly HashSet<NetObjectId> _noStyle = new();

    public void Reset()
    {
        foreach (var overlay in _overlays.Values)
            if (overlay.Root != null) Object.Destroy(overlay.Root.gameObject);
        _overlays.Clear();
    }

    public void Update(CoopController coop, MonitorCursor local, MonitorCursor remote, bool remoteFresh)
    {
        if (!coop.Session.IsJoined || !coop.WorldReady || coop.Objects.Initialized != true)
        { Reset(); return; }
        var seen = new HashSet<NetObjectId>();
        foreach (var pc in coop.Objects.Model.Pcs.Where(p => p.Location.Place == PcPlace.Bench))
        {
            var os = coop.Objects.FindBench(pc.Id)?.m_workStation?.m_virtualComputer?.m_os;
            var canvas = os?.m_desktop?.canvas?.rootCanvas; // the fractions are of the root screen (NativeDesktopInput)
            if (canvas == null) continue;
            TMP_Text style = os.m_timeDisplay;
            if (style == null || style.font == null) style = canvas.GetComponentInChildren<TMP_Text>(true);
            if (style == null || style.font == null)
            {
                if (_noStyle.Add(pc.Id)) Plugin.Logger.LogWarning($"[desktop] PC {pc.Id}: no screen font for the pointer labels.");
                continue;
            }
            seen.Add(pc.Id);
            var key = canvas.Pointer.ToInt64();
            if (!_overlays.TryGetValue(pc.Id, out var overlay) || overlay.Root == null || overlay.Canvas != key)
            {
                if (overlay?.Root != null) Object.Destroy(overlay.Root.gameObject);
                overlay = Create(canvas, style, coop.Session.Role == Role.Host ? "Host" : "Guest",
                    coop.Session.Role == Role.Host ? "Guest" : "Host");
                _overlays[pc.Id] = overlay;
            }
            overlay.Root.SetAsLastSibling();
            Place(overlay.Local, local, pc.Id);
            Place(overlay.Remote, remoteFresh ? remote : default, pc.Id);
        }
        foreach (var id in _overlays.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            if (_overlays[id].Root != null) Object.Destroy(_overlays[id].Root.gameObject);
            _overlays.Remove(id);
        }
    }

    private static Overlay Create(Canvas canvas, TMP_Text style, string localName, string remoteName)
    {
        var root = Ui.NewRect("PCBS2Coop pointers", canvas.transform);
        Ui.Stretch(root, 0, 0);
        // App windows sit on nested canvases with their own sorting; draw the labels above all of them.
        var layer = root.gameObject.AddComponent<Canvas>();
        layer.overrideSorting = true;
        layer.sortingOrder = 30000;
        var size = Mathf.Clamp(root.rect.height * .026f, 14f, 26f);
        var local = Ui.NewText("Local pointer label", root, style, localName, size, TextAlignmentOptions.MidlineLeft);
        var remote = Ui.NewText("Remote pointer", root, style, "+ " + remoteName, size, TextAlignmentOptions.MidlineLeft);
        foreach (var text in new[] { local, remote })
        {
            var rect = text.rectTransform;
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.sizeDelta = new Vector2(size * 7, size * 1.6f);
            text.outlineColor = Color.black;
            text.outlineWidth = .25f;
            text.raycastTarget = false;
            text.gameObject.SetActive(false);
        }
        // The monitor camera renders only the screen's layer; new objects start on Default.
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = canvas.gameObject.layer;
        Plugin.Logger.LogInfo($"[desktop] pointer labels added to screen '{canvas.name}' (layer {canvas.gameObject.layer}).");
        local.color = new Color(.3f, .8f, 1f);
        remote.color = new Color(1f, .75f, .25f);
        return new Overlay { Canvas = canvas.Pointer.ToInt64(), Root = root, Local = local, Remote = remote };
    }

    private static void Place(TextMeshProUGUI text, MonitorCursor pointer, NetObjectId pc)
    {
        var show = pointer.Visible && pointer.Pc == pc;
        text.gameObject.SetActive(show);
        if (!show) return;
        var root = text.rectTransform.parent.GetComponent<RectTransform>();
        var rect = root.rect;
        text.rectTransform.anchoredPosition = new Vector2(pointer.X * rect.width + 8f, pointer.Y * rect.height + 8f);
    }
}
