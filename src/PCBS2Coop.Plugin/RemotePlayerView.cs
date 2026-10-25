using TMPro;
using UnityEngine;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>The other player: a colored capsule with a facing marker and a floating name tag.</summary>
internal sealed class RemotePlayerView
{
    private const float Height = 1.8f;
    private const float Radius = 0.3f;

    private readonly GameObject _root;
    private readonly Transform _label;
    private Vector3 _targetPos;
    private float _targetYaw;
    private bool _placed;

    private RemotePlayerView(GameObject root, Transform label)
    {
        _root = root;
        _label = label;
    }

    public static RemotePlayerView Create(string name)
    {
        var root = new GameObject("PCBS2Coop Remote " + name);
        var color = name == "Host" ? new Color(0.95f, 0.55f, 0.1f) : new Color(0.2f, 0.6f, 1f);

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        Object.Destroy(body.GetComponent<Collider>());
        body.transform.SetParent(root.transform, false);
        body.transform.localPosition = new Vector3(0, Height / 2, 0);
        body.transform.localScale = new Vector3(Radius * 2, Height / 2, Radius * 2);
        Tint(body, color);

        // Small box at eye height so you can tell which way they face.
        var visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visor.name = "Visor";
        Object.Destroy(visor.GetComponent<Collider>());
        visor.transform.SetParent(root.transform, false);
        visor.transform.localPosition = new Vector3(0, Height - 0.25f, Radius);
        visor.transform.localScale = new Vector3(0.4f, 0.12f, 0.15f);
        Tint(visor, Color.black);

        var label = CreateLabel(root.transform, name);
        Plugin.Logger.LogInfo($"Spawned remote player '{name}'.");
        return new RemotePlayerView(root, label);
    }

    public void SetTarget(in PlayerState s)
    {
        _targetPos = new Vector3(s.X, s.Y, s.Z);
        _targetYaw = s.Yaw;
        if (_placed) return;
        _placed = true;
        _root.transform.SetPositionAndRotation(_targetPos, Quaternion.Euler(0, _targetYaw, 0));
    }

    public void Update()
    {
        var t = _root.transform;
        var k = 1f - Mathf.Exp(-15f * Time.deltaTime);
        t.position = Vector3.Lerp(t.position, _targetPos, k);
        t.rotation = Quaternion.Euler(0, Mathf.LerpAngle(t.eulerAngles.y, _targetYaw, k), 0);

        var cam = Camera.main;
        if (cam != null) _label.rotation = Quaternion.LookRotation(_label.position - cam.transform.position);
    }

    public void Destroy()
    {
        if (_root != null) Object.Destroy(_root);
    }

    /// <summary>
    /// Unity's default primitive material doesn't render under HDRP, so build one from an HDRP shader.
    /// A copy of a Lit material already in the scene is preferred because its shader variants are in the build.
    /// </summary>
    private static void Tint(GameObject go, Color color)
    {
        var r = go.GetComponent<Renderer>();
        var m = MakeMaterial();
        foreach (var tex in new[] { "_BaseColorMap", "_MaskMap", "_NormalMap", "_EmissiveColorMap", "_UnlitColorMap" })
            if (m.HasProperty(tex)) m.SetTexture(tex, null);
        foreach (var prop in new[] { "_BaseColor", "_UnlitColor", "_Color" })
            if (m.HasProperty(prop)) m.SetColor(prop, color);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.3f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        r.sharedMaterial = m;
    }

    private static Material _template;

    private static Material MakeMaterial()
    {
        if (_template == null)
        {
            foreach (var mr in Object.FindObjectsOfType<MeshRenderer>())
            {
                var sm = mr.sharedMaterial;
                if (sm != null && sm.shader != null && sm.shader.name == "HDRP/Lit") { _template = sm; break; }
            }
            if (_template != null) Plugin.Logger.LogInfo($"Remote player material: copy of '{_template.name}' (HDRP/Lit).");
        }
        if (_template != null) return new Material(_template);

        var shader = Shader.Find("HDRP/Lit") ?? Shader.Find("HDRP/Unlit");
        Plugin.Logger.LogInfo($"Remote player material: new from shader '{shader?.name ?? "none"}'.");
        return shader != null ? new Material(shader) : new Material(Shader.Find("Standard"));
    }

    private static Transform CreateLabel(Transform parent, string text)
    {
        var go = new GameObject("NameTag");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0, Height + 0.3f, 0);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(400, 100);
        rt.localScale = Vector3.one * 0.0025f;

        var tmp = Ui.NewText("Text", go.transform, FindFontSource(), text, 72, TextAlignmentOptions.Center);
        Ui.Stretch(tmp.rectTransform, 0, 0);
        tmp.color = Color.white;
        tmp.outlineWidth = 0.2f;
        tmp.outlineColor = Color.black;
        return go.transform;
    }

    /// <summary>Any TMP text in the scene, used only to borrow its font asset and material.</summary>
    private static TMP_Text FindFontSource()
    {
        foreach (var t in Object.FindObjectsOfType<TextMeshProUGUI>())
            if (t.font != null) return t;
        var fallback = new GameObject("FontProbe").AddComponent<TextMeshProUGUI>();
        Object.Destroy(fallback.gameObject, 1);
        return fallback;
    }
}
