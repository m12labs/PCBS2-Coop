using TMPro;
using UnityEngine;
using PCBS2Coop.Network;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>
/// The other player: the animated character from the player model bundle (docs/playermodel.md), or a colored
/// capsule with a facing marker when the bundle can't load; plus a floating name tag and the carried-PC box.
/// </summary>
internal sealed class RemotePlayerView
{
    private const float Height = 1.8f;
    private const float Radius = 0.3f;
    /// <summary>Faster than this between two frames is a teleport (spawn, respawn), not movement.</summary>
    private const float TeleportSpeed = 10f;

    private static readonly int SpeedParam = Animator.StringToHash("Speed");
    private static readonly int CarryingParam = Animator.StringToHash("Carrying");

    /// <summary>The floor is searched from this far above the received feet position…</summary>
    private const float FloorProbeUp = 0.5f;
    /// <summary>…down to this far below it. Further away, the received height is kept.</summary>
    private const float FloorProbeDown = 0.6f;
    private static bool s_floorGapLogged;

    private readonly GameObject _root;
    private readonly Transform _visual;
    private readonly Transform _label;
    private readonly Animator _animator;
    private readonly Transform _carryAnchor;
    private Vector3 _targetPos;
    private float _targetYaw;
    private bool _placed;
    private Vector3 _lastPos;
    private float _speed;
    private float _floorOffset;
    private bool _floorOffsetSet;

    private RemotePlayerView(GameObject root, Transform visual, Transform label, Animator animator, Transform carryAnchor)
    {
        _root = root;
        _visual = visual;
        _label = label;
        _animator = animator;
        _carryAnchor = carryAnchor;
    }

    /// <summary>"model" for the animated character, "capsule" for the fallback.</summary>
    public string BodyLook => _animator != null ? "model" : "capsule";
    /// <summary>Smoothed horizontal speed of the view in m/s (drives the animation).</summary>
    public float GroundSpeed => _speed;

    public static RemotePlayerView Create(string name)
    {
        var root = new GameObject("PCBS2Coop Remote " + name);
        var color = name == "Host" ? new Color(0.95f, 0.55f, 0.1f) : new Color(0.2f, 0.6f, 1f);

        // Everything visible hangs under Visual, which Update lowers onto the floor (see SnapToFloor).
        var visual = new GameObject("Visual").transform;
        visual.SetParent(root.transform, false);

        Animator animator = null;
        Transform anchor = null;
        try { animator = BuildModel(visual, color, out anchor); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[model] building the character failed ({e.GetType().Name}: {e.Message}); using the capsule."); }
        if (animator == null) BuildCapsule(visual, color);

        var label = CreateLabel(visual, name);
        Plugin.Logger.LogInfo($"Spawned remote player '{name}' ({(animator != null ? "model" : "capsule")}).");
        return new RemotePlayerView(root, visual, label, animator, anchor);
    }

    /// <summary>Instantiate the bundle's character and give each material slot a copy of the scene's HDRP/Lit with the palette color.</summary>
    private static Animator BuildModel(Transform parent, Color tint, out Transform anchor)
    {
        anchor = null;
        var prefab = PlayerModelAssets.Prefab;
        if (prefab == null) return null;
        var model = Object.Instantiate(prefab, parent);
        try
        {
            model.name = "Model";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            foreach (var c in model.GetComponentsInChildren<Collider>(true)) Object.Destroy(c);

            var palette = PlayerModelAssets.Palette;
            var materials = new Material[palette.Count];
            foreach (var slot in palette) materials[slot.Index] = Paint(MakeMaterial(), slot.Tint ? tint : slot.Color, slot.Smoothness);
            model.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMaterials = materials;

            var animator = model.GetComponent<Animator>();
            animator.applyRootMotion = false;
            // The anchor hangs on the chest bone, so the box follows the upper body while walking and running.
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                if (t.name == "CarryAnchor") { anchor = t; break; }
            return animator;
        }
        catch
        {
            Object.Destroy(model);
            throw;
        }
    }

    private static void BuildCapsule(Transform parent, Color color)
    {
        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        Object.Destroy(body.GetComponent<Collider>());
        body.transform.SetParent(parent, false);
        body.transform.localPosition = new Vector3(0, Height / 2, 0);
        body.transform.localScale = new Vector3(Radius * 2, Height / 2, Radius * 2);
        Tint(body, color);

        // Small box at eye height so you can tell which way they face.
        var visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visor.name = "Visor";
        Object.Destroy(visor.GetComponent<Collider>());
        visor.transform.SetParent(parent, false);
        visor.transform.localPosition = new Vector3(0, Height - 0.25f, Radius);
        visor.transform.localScale = new Vector3(0.4f, 0.12f, 0.15f);
        Tint(visor, Color.black);
    }

    public void SetTarget(in PlayerState s)
    {
        _targetPos = new Vector3(s.X, s.Y, s.Z);
        _targetYaw = s.Yaw;
        if (_placed) return;
        _placed = true;
        _root.transform.SetPositionAndRotation(_targetPos, Quaternion.Euler(0, _targetYaw, 0));
        _lastPos = _targetPos;
    }

    public void Update()
    {
        var t = _root.transform;
        var dt = Time.deltaTime;
        var k = 1f - Mathf.Exp(-15f * dt);
        t.position = Vector3.Lerp(t.position, _targetPos, k);
        t.rotation = Quaternion.Euler(0, Mathf.LerpAngle(t.eulerAngles.y, _targetYaw, k), 0);
        SnapToFloor(t.position, k);
        if (_animator != null) Animate(t.position, dt);

        var cam = Camera.main;
        if (cam != null) _label.rotation = Quaternion.LookRotation(_label.position - cam.transform.position);
    }

    /// <summary>
    /// The received feet position is the bottom of the other player's CharacterController, which hovers above the
    /// floor by its skin width. Put the visible body on the floor below it (local look only; nothing is sent).
    /// </summary>
    private void SnapToFloor(Vector3 feet, float k)
    {
        float? floor = null;
        var origin = feet + Vector3.up * FloorProbeUp;
        var local = LocalPlayer.Controller;
        foreach (var hit in Physics.RaycastAll(origin, Vector3.down, FloorProbeUp + FloorProbeDown, ~0, QueryTriggerInteraction.Ignore))
        {
            if (local != null && hit.collider == local) continue; // standing in the local player
            var gap = hit.point.y - feet.y;
            if (gap > 0.05f) continue; // something above the feet (a desk edge, a carried box), not the floor
            if (floor == null || gap > floor) floor = gap; // the highest surface at or below the feet
        }
        var target = floor ?? 0f;
        if (!s_floorGapLogged && floor != null)
        {
            s_floorGapLogged = true;
            Plugin.Logger.LogInfo($"[model] floor is {-target:F3} m below the received feet position; the body is lowered onto it.");
        }
        _floorOffset = _floorOffsetSet ? Mathf.Lerp(_floorOffset, target, k) : target;
        _floorOffsetSet = true;
        _visual.localPosition = new Vector3(0, _floorOffset, 0);
    }

    /// <summary>
    /// Speed comes from the smoothed positions already received (nothing new goes over the network). The Speed
    /// parameter is ground speed in m/s; the controller's thresholds are each clip's own speed (docs/playermodel.md 5.1).
    /// </summary>
    private void Animate(Vector3 pos, float dt)
    {
        var moved = pos - _lastPos;
        moved.y = 0;
        _lastPos = pos;
        if (dt <= 0) return;
        var raw = moved.magnitude / dt;
        if (raw > TeleportSpeed) raw = 0;
        _speed = Mathf.Lerp(_speed, raw, 1f - Mathf.Exp(-8f * dt));
        _animator.SetFloat(SpeedParam, _speed);
    }

    public Vector3 Position => _root != null ? _root.transform.position : Vector3.zero;

    public void Destroy()
    {
        if (_root != null) Object.Destroy(_root);
    }

    // ---------------- carried PC: a cardboard box in front of the capsule ----------------

    private const float BoxSize = 0.6f; // longest side, roughly a mid tower case
    private GameObject _box;
    public bool CarryShown => _box != null && _box.activeSelf;
    /// <summary>"package" when the game's own cardboard package meshes were used, "cube" for the generated fallback.</summary>
    public string CarryLook { get; private set; } = "none";

    /// <summary>Show or hide the box. Derived from the accepted facts; it has no colliders and no game components.</summary>
    public void SetCarrying(bool carrying)
    {
        if (_animator != null) _animator.SetBool(CarryingParam, carrying);
        if (!carrying) { if (_box != null) _box.SetActive(false); return; }
        if (_box == null)
        {
            _box = new GameObject("CarriedPC");
            // The model's CarryAnchor is the box center resting on its hands; the capsule holds it in front.
            _box.transform.SetParent(_carryAnchor != null ? _carryAnchor : _visual, false);
            _box.transform.localPosition = _carryAnchor != null ? Vector3.zero : new Vector3(0, 1.05f, Radius + BoxSize * 0.5f);
            try { CarryLook = BuildPackage(_box.transform) ? "package" : "cube"; }
            catch (Exception e) { Plugin.Logger.LogWarning("[view] package meshes unavailable for the carried box: " + e.Message); CarryLook = "cube"; }
            if (CarryLook == "cube") BuildCube(_box.transform);
            Plugin.Logger.LogInfo($"[view] carried-PC box ready ({CarryLook}).");
        }
        _box.SetActive(true);
    }

    /// <summary>Copy the visible meshes of the workshop's cardboard package prefab (no Package component, no colliders).</summary>
    private static bool BuildPackage(Transform parent)
    {
        var prefab = Object.FindObjectOfType<WorkshopController>()?.m_packagePrefab;
        if (prefab == null) return false;
        var root = prefab.transform;
        var holder = new GameObject("Package").transform;
        holder.SetParent(parent, false);
        var bounds = new Bounds(); var any = false;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            if (filter.sharedMesh == null || renderer == null || !renderer.enabled || !ActiveChain(filter.transform, root)) continue;
            var rel = root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            var part = new GameObject(filter.name);
            part.transform.SetParent(holder, false);
            part.transform.localPosition = rel.GetColumn(3);
            part.transform.localRotation = rel.rotation;
            part.transform.localScale = rel.lossyScale;
            part.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            part.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
            var b = filter.sharedMesh.bounds;
            foreach (var corner in Corners(b))
            {
                var p = rel.MultiplyPoint3x4(corner);
                if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p);
            }
        }
        if (!any || bounds.size.magnitude < 1e-4f) { Object.Destroy(holder.gameObject); return false; }
        var scale = BoxSize / Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        holder.localScale = Vector3.one * scale;
        holder.localPosition = -bounds.center * scale;
        return true;
    }

    private static bool ActiveChain(Transform t, Transform root)
    {
        for (; t != null; t = t.parent) { if (!t.gameObject.activeSelf) return false; if (t == root) return true; }
        return true;
    }

    private static IEnumerable<Vector3> Corners(Bounds b)
    {
        for (var i = 0; i < 8; i++)
            yield return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
    }

    /// <summary>Fallback: a case-shaped cube with a generated cardboard texture (brown fibres and a packing-tape band).</summary>
    private static void BuildCube(Transform parent)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Cardboard";
        Object.Destroy(cube.GetComponent<Collider>());
        cube.transform.SetParent(parent, false);
        cube.transform.localScale = new Vector3(BoxSize * 0.42f, BoxSize * 0.8f, BoxSize);
        var m = MakeMaterial();
        foreach (var tex in new[] { "_MaskMap", "_NormalMap", "_EmissiveColorMap" }) if (m.HasProperty(tex)) m.SetTexture(tex, null);
        var cardboard = CardboardTexture();
        foreach (var tex in new[] { "_BaseColorMap", "_UnlitColorMap", "_MainTex" }) if (m.HasProperty(tex)) m.SetTexture(tex, cardboard);
        foreach (var prop in new[] { "_BaseColor", "_UnlitColor", "_Color" }) if (m.HasProperty(prop)) m.SetColor(prop, Color.white);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.15f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        cube.GetComponent<Renderer>().sharedMaterial = m;
    }

    private static Texture2D _cardboard;
    private static Texture2D CardboardTexture()
    {
        if (_cardboard != null) return _cardboard;
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "PCBS2Coop Cardboard", wrapMode = TextureWrapMode.Repeat };
        var random = new System.Random(20261025);
        var board = new Color(0.60f, 0.45f, 0.29f); var tape = new Color(0.72f, 0.60f, 0.42f);
        for (var y = 0; y < size; y++)
        {
            var fibre = 0.97f + 0.03f * Mathf.Sin(y * 0.9f); // faint corrugation lines
            for (var x = 0; x < size; x++)
            {
                var grain = 0.92f + 0.08f * (float)random.NextDouble();
                var band = Mathf.Abs(x - size / 2) < 11;
                var c = (band ? tape : board) * (band ? 0.97f + 0.03f * (float)random.NextDouble() : grain * fibre);
                if (Mathf.Abs(x - size / 2) == 11) c *= 0.85f; // tape edge
                c.a = 1;
                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply(true);
        Object.DontDestroyOnLoad(tex);
        return _cardboard = tex;
    }

    /// <summary>
    /// Unity's default primitive material doesn't render under HDRP, so build one from an HDRP shader.
    /// A copy of a Lit material already in the scene is preferred because its shader variants are in the build.
    /// </summary>
    private static void Tint(GameObject go, Color color) => go.GetComponent<Renderer>().sharedMaterial = Paint(MakeMaterial(), color, 0.3f);

    /// <summary>A flat color: textures cleared, the base color set, not metallic.</summary>
    private static Material Paint(Material m, Color color, float smoothness)
    {
        foreach (var tex in new[] { "_BaseColorMap", "_MaskMap", "_NormalMap", "_EmissiveColorMap", "_UnlitColorMap" })
            if (m.HasProperty(tex)) m.SetTexture(tex, null);
        foreach (var prop in new[] { "_BaseColor", "_UnlitColor", "_Color" })
            if (m.HasProperty(prop)) m.SetColor(prop, color);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        return m;
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
