using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Builds the player model bundle described in docs/playermodel.md section 5.5 and copies it into the plugin.
/// Batch: Unity.exe -batchmode -quit -projectPath unity/PCBS2CoopModels -executeMethod BuildPlayerBundle.Build
/// </summary>
public static class BuildPlayerBundle
{
    private const string PrefabPath = "Assets/PlayerModel/PlayerModel.prefab";
    /// <summary>One line: the name of the material slot that gets the per-player color.</summary>
    private const string TintSlotPath = "Assets/PlayerModel/PlayerModel.tint.txt";
    private const string TempDir = "Assets/PlayerModel/_Build";
    private const string BundleName = "pcbs2coop-player.bundle";

    private static string ProjectDir => Path.GetDirectoryName(Application.dataPath);
    private static string OutDir => Path.Combine(ProjectDir, "Build", "Bundles");
    private static string PluginTarget => Path.GetFullPath(Path.Combine(ProjectDir, "..", "..", "src", "PCBS2Coop.Plugin", "Assets", BundleName));

    [MenuItem("PCBS2Coop/Build Player Bundle")]
    public static void Build()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) throw Fail($"missing {PrefabPath}");

        var tintName = ReadTintSlot();
        var renderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (renderers.Length != 1) throw Fail($"expected exactly one SkinnedMeshRenderer, found {renderers.Length}");
        var animator = prefab.GetComponent<Animator>();
        if (animator == null) throw Fail("the prefab root needs an Animator");
        if (animator.runtimeAnimatorController == null || animator.avatar == null) throw Fail("the prefab's Animator needs its controller and avatar (run PCBS2Coop/Rebuild Player Model)");

        var palette = WritePalette(renderers[0].sharedMaterials, tintName);

        // A plain copy (no prefab link) with the materials removed, so no shader is pulled into the bundle.
        if (!AssetDatabase.IsValidFolder(TempDir)) AssetDatabase.CreateFolder("Assets/PlayerModel", "_Build");
        var copy = Object.Instantiate(prefab);
        copy.name = "PlayerModel";
        try
        {
            if (copy.GetComponentsInChildren<MonoBehaviour>(true).Length > 0) throw Fail("the prefab has MonoBehaviours; the game can't load them");
            foreach (var c in copy.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) r.sharedMaterials = new Material[r.sharedMaterials.Length];
            PrefabUtility.SaveAsPrefabAsset(copy, TempDir + "/PlayerModel.prefab");
        }
        finally { Object.DestroyImmediate(copy); }

        File.WriteAllText(Path.Combine(ProjectDir, TempDir, "PlayerModel.palette.txt"), palette, new UTF8Encoding(false));
        AssetDatabase.ImportAsset(TempDir + "/PlayerModel.palette.txt");

        try
        {
            Directory.CreateDirectory(OutDir);
            var build = new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = new[] { TempDir + "/PlayerModel.prefab", TempDir + "/PlayerModel.palette.txt" },
                addressableNames = new[] { "PlayerModel", "PlayerModel.palette" },
            };
            var manifest = BuildPipeline.BuildAssetBundles(OutDir, new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
            if (manifest == null) throw Fail("BuildAssetBundles failed (see the log above)");

            var built = Path.Combine(OutDir, BundleName);
            Directory.CreateDirectory(Path.GetDirectoryName(PluginTarget));
            File.Copy(built, PluginTarget, true);
            Debug.Log($"PCBS2Coop: built {BundleName} ({new FileInfo(built).Length / 1024} KB), copied to {PluginTarget}\n{palette}");
        }
        finally
        {
            AssetDatabase.DeleteAsset(TempDir);
        }
    }

    private static string ReadTintSlot()
    {
        var file = Path.Combine(ProjectDir, TintSlotPath);
        if (!File.Exists(file)) throw Fail($"missing {TintSlotPath} (one line: the material slot name to tint)");
        foreach (var line in File.ReadAllLines(file))
        {
            var t = line.Trim();
            if (t.Length > 0 && !t.StartsWith("#")) return t;
        }
        throw Fail($"{TintSlotPath} is empty");
    }

    /// <summary>One line per material slot: index, name, r g b (as stored in the material, so Material.SetColor reproduces it), smoothness, tint flag.</summary>
    private static string WritePalette(Material[] materials, string tintName)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("# slot name r g b smoothness tint\n");
        var tinted = 0;
        for (var i = 0; i < materials.Length; i++)
        {
            var m = materials[i];
            if (m == null) throw Fail($"material slot {i} is empty");
            var color = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
            var smooth = m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness") : m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness") : 0.3f;
            foreach (var tex in new[] { "_BaseColorMap", "_MainTex" })
                if (m.HasProperty(tex) && m.GetTexture(tex) != null)
                    Debug.LogWarning($"PCBS2Coop: slot {i} '{m.name}' has a texture ({tex}); only its color goes in the palette.");
            var name = m.name.Replace(' ', '_');
            var tint = string.Equals(m.name, tintName, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            tinted += tint;
            sb.AppendLine(string.Format(inv, "{0} {1} {2:0.###} {3:0.###} {4:0.###} {5:0.###} {6}", i, name, color.r, color.g, color.b, smooth, tint));
        }
        if (tinted != 1) throw Fail($"tint slot '{tintName}' matched {tinted} material slots; it must match exactly one");
        return sb.ToString();
    }

    private static Exception Fail(string message)
    {
        Debug.LogError("PCBS2Coop bundle build: " + message);
        return new InvalidOperationException(message);
    }
}
