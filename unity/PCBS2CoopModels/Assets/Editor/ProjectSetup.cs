using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// One-time project settings from docs/playermodel.md section 3.2. Safe to run again.
/// Batch: Unity.exe -batchmode -quit -projectPath unity/PCBS2CoopModels -executeMethod ProjectSetup.Apply
/// </summary>
public static class ProjectSetup
{
    private const string HdrpAssetPath = "Assets/Settings/HDRPAsset.asset";

    [MenuItem("PCBS2Coop/Apply Project Settings")]
    public static void Apply()
    {
        PlayerSettings.colorSpace = ColorSpace.Linear; // HDRP requires linear
        EditorSettings.serializationMode = SerializationMode.ForceText;
        VersionControlSettings.mode = "Visible Meta Files";

        EnsureFolder("Assets/Settings");
        var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(HdrpAssetPath);
        if (asset == null)
        {
            asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            AssetDatabase.CreateAsset(asset, HdrpAssetPath);
        }
        GraphicsSettings.defaultRenderPipeline = asset;

        AssetDatabase.SaveAssets();
        Debug.Log($"PCBS2Coop: project settings applied (linear, force text, visible meta, HDRP asset {HdrpAssetPath}).");
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var slash = path.LastIndexOf('/');
        var parent = path.Substring(0, slash);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
    }
}
