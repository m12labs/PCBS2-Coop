using System.Globalization;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>One material slot of the player model, from the bundle's palette (docs/playermodel.md 5.3).</summary>
internal readonly record struct PaletteSlot(int Index, string Name, Color Color, float Smoothness, bool Tint);

/// <summary>
/// Loads the remote player character from pcbs2coop-player.bundle next to the plugin DLL, once per process.
/// The bundle stays loaded for the process lifetime. Any failure is logged once and leaves <see cref="Prefab"/> null,
/// so the view falls back to the capsule.
/// </summary>
internal static class PlayerModelAssets
{
    public const string BundleFile = "pcbs2coop-player.bundle";

    private static bool _tried;
    private static AssetBundle _bundle;
    private static GameObject _prefab;

    public static IReadOnlyList<PaletteSlot> Palette { get; private set; } = Array.Empty<PaletteSlot>();

    /// <summary>The model prefab, or null when the bundle is missing or invalid. Main thread only.</summary>
    public static GameObject Prefab
    {
        get
        {
            if (!_tried) { _tried = true; Load(); }
            return _prefab;
        }
    }

    private static void Load()
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", BundleFile);
        try
        {
            if (!File.Exists(path)) { Plugin.Logger.LogWarning($"[model] {BundleFile} not found next to the plugin; using the capsule."); return; }
            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null) { Plugin.Logger.LogWarning($"[model] {BundleFile} failed to load; using the capsule."); return; }

            var prefab = LoadNow<GameObject>(_bundle, "PlayerModel");
            var palette = LoadNow<TextAsset>(_bundle, "PlayerModel.palette");
            if (prefab == null || palette == null) { Plugin.Logger.LogWarning($"[model] {BundleFile} lacks PlayerModel or PlayerModel.palette; using the capsule."); return; }

            var slots = ParsePalette(palette.text, out var error);
            var renderer = prefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (error == null && renderer == null) error = "the prefab has no SkinnedMeshRenderer";
            if (error == null && renderer.sharedMaterials.Length != slots.Count) error = $"the palette has {slots.Count} slots, the renderer {renderer.sharedMaterials.Length}";
            if (error == null && prefab.GetComponent<Animator>() == null) error = "the prefab has no Animator";
            if (error != null) { Plugin.Logger.LogWarning($"[model] {BundleFile}: {error}; using the capsule."); return; }

            // Scene changes run Resources.UnloadUnusedAssets; keep the loaded assets alive.
            prefab.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            palette.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            _prefab = prefab;
            Palette = slots;
            Plugin.Logger.LogInfo($"[model] loaded {BundleFile}: prefab '{prefab.name}', palette '{palette.name}' ({string.Join(", ", slots.Select(s => s.Tint ? s.Name + "*" : s.Name))}).");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[model] {BundleFile} failed to load ({e.GetType().Name}: {e.Message}); using the capsule.");
            _prefab = null;
        }
    }

    /// <summary>
    /// Load one asset and wait for it. Don't use <c>AssetBundle.LoadAsset</c>: its Unity 6 interop wrapper passes the
    /// name through <c>Il2CppSystem.ReadOnlySpan</c>, which the game's runtime lacks (MissingMethodException in game).
    /// <c>LoadAssetAsync</c> is a plain native call, and reading <c>asset</c> blocks until the load is done.
    /// </summary>
    private static T LoadNow<T>(AssetBundle bundle, string name) where T : Object
    {
        var request = bundle.LoadAssetAsync(name, Il2CppType.Of<T>());
        return request?.asset?.TryCast<T>();
    }

    /// <summary>Lines "index name r g b smoothness tint"; '#' starts a comment. Indices must run 0..n-1 with exactly one tint row.</summary>
    internal static List<PaletteSlot> ParsePalette(string text, out string error)
    {
        var slots = new List<PaletteSlot>();
        error = null;
        var inv = CultureInfo.InvariantCulture;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length != 7 || !int.TryParse(f[0], NumberStyles.Integer, inv, out var index) || index != slots.Count ||
                !float.TryParse(f[2], NumberStyles.Float, inv, out var r) || !float.TryParse(f[3], NumberStyles.Float, inv, out var g) ||
                !float.TryParse(f[4], NumberStyles.Float, inv, out var b) || !float.TryParse(f[5], NumberStyles.Float, inv, out var smooth) ||
                (f[6] != "0" && f[6] != "1"))
            {
                error = $"bad palette line '{line}'";
                return slots;
            }
            slots.Add(new PaletteSlot(index, f[1], new Color(r, g, b, 1f), smooth, f[6] == "1"));
        }
        if (slots.Count == 0) error = "the palette is empty";
        else if (slots.Count(s => s.Tint) != 1) error = "the palette needs exactly one tint slot";
        return slots;
    }
}
