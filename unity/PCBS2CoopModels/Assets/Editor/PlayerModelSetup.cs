using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Builds everything under Assets/PlayerModel from the Shyr base character and clips of Quaternius's Universal
/// Animation Library (docs/playermodel.md section 4). Safe to run again; then run PCBS2Coop/Build Player Bundle.
/// Batch: Unity.exe -batchmode -quit -projectPath unity/PCBS2CoopModels -executeMethod PlayerModelSetup.Rebuild
/// </summary>
public static class PlayerModelSetup
{
    private const string CharacterFbx = "Assets/ThirdParty/Shyr/BaseCharacter/HumanCharacter.fbx";
    /// <summary>The library's 24 MB FBXs are git-ignored; only the clips extracted from them are committed.</summary>
    private const string UalFbx = "Assets/ThirdParty/Quaternius/UniversalAnimationLibrary/UAL1_Standard.fbx";
    /// <summary>The same clips with root motion: read once for each clip's true speed.</summary>
    private const string UalRootMotionFbx = "Assets/ThirdParty/Quaternius/UniversalAnimationLibrary/UAL1_Standard_RM.fbx";
    /// <summary>Each clip's root speed in Unity's normalized humanoid units (meters ÷ human scale per second).</summary>
    private const string UalSpeedsFile = ClipDir + "/UAL.speeds.txt";
    private const string Dir = "Assets/PlayerModel";
    private const string MaterialDir = Dir + "/Materials";
    private const string ClipDir = Dir + "/Clips";

    /// <summary>Library clip → our name. Locomotion is blended by ground speed in this order.</summary>
    private static readonly (string Source, string Name)[] UalClips =
        { ("Idle_Loop", "Idle"), ("Walk_Loop", "Walk"), ("Jog_Fwd_Loop", "Jog"), ("Sprint_Loop", "Sprint") };

    /// <summary>The character is 1.734 m tall at scale 1; the capsule it replaces is 1.8 m.</summary>
    private const float ImportScale = 1.8f / 1.734f;

    /// <summary>Where the hands hold the carried PC, in model space (+Z forward), and the box center that goes with it.</summary>
    private static readonly Vector3 CarryHandLeft = new(-0.17f, 0.95f, 0.32f);
    private static readonly Vector3 CarryHandRight = new(0.17f, 0.95f, 0.32f);
    private static readonly Vector3 CarryAnchor = new(0f, 1.15f, 0.46f);

    /// <summary>
    /// The character's T-pose arms point this many degrees forward of straight out. It has no collarbones, so the
    /// library's shoulder motion is lost and the arms otherwise hang about 30 degrees too far back and out; measured
    /// against the library's own figure over idle, walk, jog and sprint, 30 halves the arm error (mean 30 to 16 degrees).
    /// </summary>
    private const float ArmRestForward = 30f;

    /// <summary>Humanoid mapping of the character (its Blender bone names). It faces +Z; left is -X.</summary>
    private static readonly Dictionary<string, string> CharacterBones = new()
    {
        { "Hips", "Body" }, { "Spine", "Torso" }, { "Chest", "Chest" }, { "Neck", "Neck" }, { "Head", "Head" },
        { "LeftUpperArm", "Upper Arm L" }, { "LeftLowerArm", "Lower Arm L" }, { "LeftHand", "Hand L" },
        { "RightUpperArm", "Upper Arm R" }, { "RightLowerArm", "Lower Arm R" }, { "RightHand", "Hand R" },
        { "LeftUpperLeg", "Upper Leg L" }, { "LeftLowerLeg", "Lower Leg L" }, { "LeftFoot", "Heel L" }, { "LeftToes", "Feet L" },
        { "RightUpperLeg", "Upper Leg R" }, { "RightLowerLeg", "Lower Leg R" }, { "RightFoot", "Heel R" }, { "RightToes", "Feet R" },
    };

    [MenuItem("PCBS2Coop/Rebuild Player Model")]
    public static void Rebuild()
    {
        EnsureFolder(Dir);
        EnsureFolder(MaterialDir);
        ConfigureCharacterImport();
        var clips = UalClips.Select(c => AssetDatabase.LoadAssetAtPath<AnimationClip>($"{ClipDir}/UAL.{c.Name}.anim")
                                         ?? throw Fail($"missing {ClipDir}/UAL.{c.Name}.anim (run PCBS2Coop/Extract Animation Clips)")).ToArray();
        var idle = clips[0];

        var model = BuildModel(out var avatar);
        try
        {
            var carry = BakeCarryPose(model, avatar, idle);
            // The carry layer holds the arms still relative to the chest, while walk and run move the chest. Hang the
            // box anchor on the chest bone, placed while the model is in the carry pose, so it stays in the hands.
            var anchor = new GameObject("CarryAnchor").transform;
            anchor.SetPositionAndRotation(model.transform.TransformPoint(CarryAnchor), model.transform.rotation);
            anchor.SetParent(model.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Chest), true); // keeps world scale 1 under the 100x armature
            // Humanoid root motion scales with the avatar, so each clip covers (normalized speed × human scale) m/s here.
            var humanScale = model.GetComponent<Animator>().humanScale;
            var normalized = ReadUalSpeeds();
            var speeds = UalClips.Select((c, i) => i == 0 ? 0f : normalized[c.Name] * humanScale).ToArray(); // idle sits at 0
            for (var i = 1; i < speeds.Length; i++)
                if (speeds[i] < 0.3f || speeds[i] <= speeds[i - 1]) throw Fail("implausible clip speeds: " + SpeedList(speeds));

            var mask = BuildCarryMask();
            var controller = BuildController(clips, speeds, carry, mask);

            ResetToBindPose(model);
            var animator = model.GetComponent<Animator>();
            animator.avatar = avatar;
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            animator.updateMode = AnimatorUpdateMode.Normal;
            PrefabUtility.SaveAsPrefabAsset(model, Dir + "/PlayerModel.prefab");

            File.WriteAllText(Path.Combine(ProjectDir, Dir, "PlayerModel.speeds.txt"),
                "# Ground speed of each clip on this character (m/s): library root speed times the avatar human scale. The Speed thresholds.\n" +
                string.Concat(UalClips.Select((c, i) => string.Format(CultureInfo.InvariantCulture, "{0} {1:0.###}\n", c.Name, speeds[i]))));
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            // The loaded prefab keeps a stale (null) controller reference after the controller is rebuilt in place.
            AssetDatabase.ImportAsset(Dir + "/PlayerModel.controller", ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(Dir + "/PlayerModel.prefab", ImportAssetOptions.ForceUpdate);
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/PlayerModel.prefab").GetComponent<Animator>();
            if (saved.runtimeAnimatorController == null || saved.avatar == null) throw Fail("the saved prefab lost its controller or avatar");
            Debug.Log($"PCBS2Coop: player model rebuilt ({SpeedList(speeds)}, height {Height(model):F2} m).");
        }
        finally
        {
            Object.DestroyImmediate(model);
        }
    }

    // ---------------- imports ----------------

    private static void ConfigureCharacterImport()
    {
        var imp = (ModelImporter)AssetImporter.GetAtPath(CharacterFbx);
        imp.globalScale = ImportScale;
        imp.useFileUnits = true;
        imp.importBlendShapes = false;
        imp.importCameras = false;
        imp.importLights = false;
        imp.importVisibility = false;
        imp.isReadable = true; // the two body meshes are merged into PlayerModel.mesh.asset, which is what ships
        imp.animationType = ModelImporterAnimationType.Generic; // the humanoid avatar is built from the cleaned prefab
        imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        imp.importAnimation = false; // its own Blender clips aren't used
        imp.SaveAndReimport();
    }

    /// <summary>
    /// Import the Universal Animation Library FBX as Humanoid (Unity's automatic mapping; its rest pose is a T-pose)
    /// and copy the clips we use into Assets/PlayerModel/Clips as standalone humanoid .anim files.
    /// Needs the library's UAL1_Standard.fbx (the file without root motion) at <see cref="UalFbx"/>.
    /// </summary>
    [MenuItem("PCBS2Coop/Extract Animation Clips")]
    public static void ExtractClips()
    {
        var imp = (ModelImporter)AssetImporter.GetAtPath(UalFbx)
                  ?? throw Fail($"missing {UalFbx}: copy Unity/UAL1_Standard.fbx from the Universal Animation Library (Standard) there");
        imp.materialImportMode = ModelImporterMaterialImportMode.None;
        imp.importBlendShapes = false;
        imp.importCameras = false;
        imp.importLights = false;
        imp.bakeAxisConversion = true; // the library's Unity setup step 1
        imp.animationType = ModelImporterAnimationType.Human;
        imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        imp.humanDescription = new HumanDescription(); // empty: Unity maps the rig automatically from its rest pose
        imp.SaveAndReimport();

        // The library's rest pose is already a T-pose, so Unity's automatic mapping and skeleton are used as they are.
        if (!imp.humanDescription.human.Any(h => h.humanName == "Hips")) throw Fail("Unity couldn't map the library's rig to Humanoid");

        var defaults = imp.defaultClipAnimations;
        imp.clipAnimations = UalClips.Select(c =>
        {
            var clip = defaults.FirstOrDefault(d => d.name.EndsWith("|" + c.Source)) ?? throw Fail("the library has no clip " + c.Source);
            clip.name = c.Name;
            clip.loopTime = true;
            clip.loopPose = false;
            // Root settings from the library's Unity setup guide. "Original" orientation would carry the library
            // file's root rotation over and turn our character around.
            clip.lockRootRotation = true;        // bake rotation into the pose…
            clip.keepOriginalOrientation = false; // …based upon body orientation
            clip.lockRootHeightY = true;         // bake height as authored
            clip.keepOriginalPositionY = true;
            clip.heightFromFeet = false;
            clip.lockRootPositionXZ = false;     // XZ goes to root motion, which the plugin never applies
            clip.keepOriginalPositionXZ = false; // based upon center of mass
            return clip;
        }).ToArray();
        imp.SaveAndReimport();

        var avatar = AssetDatabase.LoadAllAssetsAtPath(UalFbx).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman) throw Fail("the library's humanoid avatar is invalid (see the import warnings)");
        EnsureFolder(ClipDir);
        var imported = AssetDatabase.LoadAllAssetsAtPath(UalFbx).OfType<AnimationClip>().ToList();
        foreach (var c in UalClips)
        {
            var source = imported.First(x => x.name == c.Name);
            var copy = Object.Instantiate(source);
            AnimationUtility.SetAnimationClipSettings(copy, AnimationUtility.GetAnimationClipSettings(source));
            SaveAsset(copy, $"{ClipDir}/UAL.{c.Name}.anim");
        }
        WriteUalSpeeds(imp.humanDescription);
        AssetDatabase.SaveAssets();
        Debug.Log($"PCBS2Coop: extracted {UalClips.Length} clips from the Universal Animation Library into {ClipDir}.");
    }

    /// <summary>
    /// The in-place clips carry no root motion, so their speed comes from the library's root-motion twin: the
    /// horizontal distance of its root curve (RootT, normalized by human scale) over one cycle.
    /// </summary>
    private static void WriteUalSpeeds(HumanDescription mapping)
    {
        var imp = (ModelImporter)AssetImporter.GetAtPath(UalRootMotionFbx)
                  ?? throw Fail($"missing {UalRootMotionFbx}: copy Unity/UAL1_Standard_RM.fbx from the library there (only its speeds are used)");
        imp.materialImportMode = ModelImporterMaterialImportMode.None;
        imp.importBlendShapes = false;
        imp.importCameras = false;
        imp.importLights = false;
        imp.bakeAxisConversion = true;
        imp.animationType = ModelImporterAnimationType.Human;
        imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        imp.humanDescription = mapping; // same rig as the in-place file
        imp.clipAnimations = imp.defaultClipAnimations.Where(d => UalClips.Any(c => d.name.EndsWith("|" + c.Source))).Select(d =>
        {
            d.lockRootRotation = true; // same root settings as the clips we play
            d.keepOriginalOrientation = false;
            d.lockRootHeightY = true;
            d.keepOriginalPositionY = true;
            d.lockRootPositionXZ = false; // keep the forward motion in the root curve
            d.keepOriginalPositionXZ = false;
            return d;
        }).ToArray();
        imp.SaveAndReimport();

        var clips = AssetDatabase.LoadAllAssetsAtPath(UalRootMotionFbx).OfType<AnimationClip>().ToList();
        var lines = new List<string> { "# Root speed per clip in normalized humanoid units, from UAL1_Standard_RM.fbx. Times the avatar's human scale = m/s." };
        foreach (var c in UalClips)
        {
            var clip = clips.FirstOrDefault(x => x.name.EndsWith("|" + c.Source)) ?? throw Fail("the root-motion file has no clip " + c.Source);
            float Delta(string prop)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), prop));
                return curve == null ? 0f : curve.Evaluate(clip.length) - curve.Evaluate(0);
            }
            var speed = new Vector2(Delta("RootT.x"), Delta("RootT.z")).magnitude / clip.length;
            lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:0.####}", c.Name, Mathf.Max(0, speed)));
        }
        File.WriteAllText(Path.Combine(ProjectDir, UalSpeedsFile), string.Join("\n", lines) + "\n");
        AssetDatabase.ImportAsset(UalSpeedsFile);
    }

    private static Dictionary<string, float> ReadUalSpeeds()
    {
        var file = Path.Combine(ProjectDir, UalSpeedsFile);
        if (!File.Exists(file)) throw Fail($"missing {UalSpeedsFile} (run PCBS2Coop/Extract Animation Clips)");
        return File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#"))
            .Select(l => l.Split(' ')).ToDictionary(f => f[0], f => float.Parse(f[1], CultureInfo.InvariantCulture));
    }

    private static string SpeedList(float[] speeds) =>
        string.Join(", ", UalClips.Select((c, i) => string.Format(CultureInfo.InvariantCulture, "{0} {1:F2} m/s", c.Name.ToLowerInvariant(), speeds[i])));

    // ---------------- model, mesh, avatar ----------------

    private static GameObject BuildModel(out Avatar avatar)
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterFbx);
        var model = (GameObject)PrefabUtility.InstantiatePrefab(src);
        PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        model.name = "PlayerModel";

        var body = model.transform.Find("BaseCorpo") ?? throw Fail("BaseCorpo not found");
        var torso = body.Find("Torso") ?? throw Fail("BaseCorpo/Torso not found");
        var bodyRenderer = body.GetComponent<SkinnedMeshRenderer>();
        var mesh = MergeBodyMeshes(bodyRenderer, torso.GetComponent<SkinnedMeshRenderer>());
        mesh = SaveAsset(mesh, Dir + "/PlayerModel.mesh.asset");
        Object.DestroyImmediate(torso.gameObject);

        // Drop the test weapon, horns, teeth, ears and Blender bone-widget meshes.
        foreach (var child in model.transform.Cast<Transform>().ToList())
            if (child.name != "Armature" && child != body) Object.DestroyImmediate(child.gameObject);

        body.name = "Mannequin";
        var skinColor = bodyRenderer.sharedMaterial != null && bodyRenderer.sharedMaterial.HasProperty("_BaseColor")
            ? bodyRenderer.sharedMaterial.GetColor("_BaseColor") : new Color(0.906f, 0.681f, 0.436f);
        bodyRenderer.sharedMesh = mesh;
        bodyRenderer.sharedMaterials = new[] { FlatMaterial("Skin", skinColor, 0.3f), FlatMaterial("Shirt", new Color(0.8f, 0.8f, 0.8f), 0.25f) };
        bodyRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        bodyRenderer.updateWhenOffscreen = false;

        var names = model.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (names.Count > 0) throw Fail("duplicate transform names break the avatar: " + string.Join(", ", names));

        var tpose = Object.Instantiate(model);
        tpose.name = model.name;
        try
        {
            PoseT(tpose.transform, CharacterBones);
            avatar = AvatarBuilder.BuildHumanAvatar(tpose, Describe(tpose.transform, CharacterBones));
        }
        finally { Object.DestroyImmediate(tpose); }
        if (!avatar.isValid || !avatar.isHuman) throw Fail("the character's humanoid avatar is invalid");
        avatar.name = "PlayerModelAvatar";
        avatar = SaveAsset(avatar, Dir + "/PlayerModel.avatar.asset");
        model.GetComponent<Animator>().avatar = avatar;
        return model;
    }

    /// <summary>One mesh, two submeshes: 0 = the body (Skin), 1 = the torso piece (Shirt, the tinted slot).</summary>
    private static Mesh MergeBodyMeshes(SkinnedMeshRenderer body, SkinnedMeshRenderer torso)
    {
        var a = body.sharedMesh;
        var b = torso.sharedMesh;
        var boneIndex = body.bones.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
        var remap = torso.bones.Select(t => boneIndex.TryGetValue(t, out var i) ? i : throw Fail("torso bone not in the body's bones: " + t.name)).ToArray();
        // Skinning ignores the renderers' transforms: vertices live in bind space. With matching bindposes the
        // torso's vertices are already in the body's space and are copied as they are.
        for (var j = 0; j < remap.Length; j++)
            if (!Approximately(b.bindposes[j], a.bindposes[remap[j]])) throw Fail($"torso and body bindposes differ for bone {torso.bones[j].name}");

        var vertices = a.vertices.Concat(b.vertices).ToArray();
        var normals = a.normals.Concat(b.normals).ToArray();
        var weights = a.boneWeights.Concat(b.boneWeights.Select(w => new BoneWeight
        {
            boneIndex0 = remap[w.boneIndex0], weight0 = w.weight0, boneIndex1 = remap[w.boneIndex1], weight1 = w.weight1,
            boneIndex2 = remap[w.boneIndex2], weight2 = w.weight2, boneIndex3 = remap[w.boneIndex3], weight3 = w.weight3,
        })).ToArray();

        var mesh = new Mesh { name = "PlayerModel" };
        mesh.indexFormat = vertices.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.normals = normals;
        if (a.uv.Length == a.vertexCount && b.uv.Length == b.vertexCount) mesh.uv = a.uv.Concat(b.uv).ToArray();
        mesh.boneWeights = weights;
        mesh.bindposes = a.bindposes;
        mesh.subMeshCount = 2;
        mesh.SetTriangles(Enumerable.Range(0, a.subMeshCount).SelectMany(s => a.GetTriangles(s)).ToArray(), 0);
        mesh.SetTriangles(Enumerable.Range(0, b.subMeshCount).SelectMany(s => b.GetTriangles(s)).Select(i => i + a.vertexCount).ToArray(), 1);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    /// <summary>Aim each limb segment along the T-pose directions: spine up, arms out to the sides, legs down.</summary>
    private static void PoseT(Transform root, Dictionary<string, string> map)
    {
        Transform Bone(string human) => map.TryGetValue(human, out var n) ? FindDeep(root, n) : null;
        void Chain(Vector3 dir, params string[] humans)
        {
            var bones = humans.Select(Bone).Where(t => t != null).ToList();
            for (var i = 0; i + 1 < bones.Count; i++)
            {
                var current = bones[i + 1].position - bones[i].position;
                if (current.sqrMagnitude > 1e-10f) bones[i].rotation = Quaternion.FromToRotation(current, dir) * bones[i].rotation;
            }
        }
        Chain(Vector3.up, "Spine", "Chest", "UpperChest", "Neck", "Head");
        // Collarbones keep their rest angle; only the arm segments are aimed, swung forward by ArmRestForward.
        var forward = Mathf.Sin(ArmRestForward * Mathf.Deg2Rad);
        var side = Mathf.Cos(ArmRestForward * Mathf.Deg2Rad);
        Chain(new Vector3(-side, 0, forward), "LeftUpperArm", "LeftLowerArm", "LeftHand");
        Chain(new Vector3(side, 0, forward), "RightUpperArm", "RightLowerArm", "RightHand");
        Chain(Vector3.down, "LeftUpperLeg", "LeftLowerLeg", "LeftFoot");
        Chain(Vector3.down, "RightUpperLeg", "RightLowerLeg", "RightFoot");
    }

    private static HumanDescription Describe(Transform tposed, Dictionary<string, string> map)
    {
        foreach (var human in map.Keys)
            if (!HumanTrait.BoneName.Contains(human)) throw Fail("unknown humanoid bone " + human);
        return new HumanDescription
        {
            human = map.Select(kv => new HumanBone { humanName = kv.Key, boneName = kv.Value, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
            skeleton = tposed.GetComponentsInChildren<Transform>(true)
                .Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
            upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
            armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0f, hasTranslationDoF = false,
        };
    }

    // ---------------- clips ----------------

    /// <summary>
    /// Arms reaching forward to hold the box, on the idle's first frame. Arm, hand and torso muscles are written, so while
    /// carrying the upper body keeps the idle posture and the box doesn't swing with a jog's twist; legs and hips still move.
    /// </summary>
    private static AnimationClip BakeCarryPose(GameObject model, Avatar avatar, AnimationClip idle)
    {
        ResetToBindPose(model);
        idle.SampleAnimation(model, 0);
        var animator = model.GetComponent<Animator>();
        Transform B(HumanBodyBones b) => animator.GetBoneTransform(b);
        var root = model.transform;
        Reach(B(HumanBodyBones.LeftUpperArm), B(HumanBodyBones.LeftLowerArm), B(HumanBodyBones.LeftHand), root.TransformPoint(CarryHandLeft), new Vector3(-0.6f, -1f, -0.3f));
        Reach(B(HumanBodyBones.RightUpperArm), B(HumanBodyBones.RightLowerArm), B(HumanBodyBones.RightHand), root.TransformPoint(CarryHandRight), new Vector3(0.6f, -1f, -0.3f));

        var handler = new HumanPoseHandler(avatar, model.transform);
        var pose = new HumanPose();
        handler.GetHumanPose(ref pose);
        handler.Dispose();
        var muscles = new AnimationCurve[HumanTrait.MuscleCount];
        for (var i = 0; i < muscles.Length; i++)
            if (IsCarryMuscle(HumanTrait.MuscleName[i])) muscles[i] = new AnimationCurve(new Keyframe(0, pose.muscles[i]), new Keyframe(1, pose.muscles[i]));
        return SaveClip(BuildHumanClip("Carry", muscles, null), Dir + "/PlayerModel.Carry.anim", true);
    }

    private static bool IsCarryMuscle(string name) =>
        name.StartsWith("Spine") || name.StartsWith("Chest") || name.StartsWith("UpperChest") ||
        (name.StartsWith("Left") || name.StartsWith("Right")) && (name.Contains("Shoulder") || name.Contains("Arm") || name.Contains("Forearm") || name.Contains("Hand"));

    /// <summary>Two-bone IK: put the hand at the target with the elbow toward the pole direction.</summary>
    private static void Reach(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 pole)
    {
        var a = (lower.position - upper.position).magnitude;
        var b = (hand.position - lower.position).magnitude;
        var s = upper.position;
        var dir = (target - s).normalized;
        var d = Mathf.Clamp((target - s).magnitude, 0.01f, (a + b) * 0.98f);
        var cos = Mathf.Clamp((a * a + d * d - b * b) / (2 * a * d), -1f, 1f);
        var perp = Vector3.ProjectOnPlane(pole, dir).normalized;
        var elbow = s + dir * (a * cos) + perp * (a * Mathf.Sqrt(1 - cos * cos));
        upper.rotation = Quaternion.FromToRotation(lower.position - s, elbow - s) * upper.rotation;
        lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, s + dir * d - lower.position) * lower.rotation;
    }

    private static AnimationClip BuildHumanClip(string name, AnimationCurve[] muscles, AnimationCurve[] root)
    {
        var clip = new AnimationClip { name = name };
        for (var i = 0; i < muscles.Length; i++)
        {
            if (muscles[i] == null) continue;
            var muscle = HumanTrait.MuscleName[i];
            if (new[] { "Thumb", "Index", "Middle", "Ring", "Little" }.Any(muscle.Contains)) continue; // no finger bones mapped
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), muscle), muscles[i]);
        }
        if (root != null)
        {
            string[] props = { "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
            for (var i = 0; i < 7; i++) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), props[i]), root[i]);
        }
        return clip;
    }

    private static AnimationClip SaveClip(AnimationClip clip, string path, bool loop)
    {
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = loop;
        settings.loopBlendOrientation = true;
        settings.loopBlendPositionY = true;
        settings.loopBlendPositionXZ = true;
        settings.keepOriginalOrientation = true;
        settings.keepOriginalPositionY = true; // the baked body height (feet-based needs foot goal curves these clips lack)
        settings.heightFromFeet = false;
        settings.keepOriginalPositionXZ = true;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
        return SaveAsset(clip, path);
    }

    // ---------------- mask, controller ----------------

    private static AvatarMask BuildCarryMask()
    {
        var mask = new AvatarMask { name = "PlayerModel.CarryMask" };
        for (var i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
        {
            var part = (AvatarMaskBodyPart)i;
            mask.SetHumanoidBodyPartActive(part, part is AvatarMaskBodyPart.Body or AvatarMaskBodyPart.LeftArm or AvatarMaskBodyPart.RightArm or AvatarMaskBodyPart.LeftFingers or AvatarMaskBodyPart.RightFingers);
        }
        return SaveAsset(mask, Dir + "/PlayerModel.CarryMask.mask");
    }

    /// <summary>Base layer: locomotion clips blended on Speed (m/s) at their own ground speeds. Carry layer: arms only.</summary>
    private static AnimatorController BuildController(AnimationClip[] locomotion, float[] speeds, AnimationClip carry, AvatarMask mask)
    {
        // Rebuild the existing controller in place: deleting and recreating it changes its GUID and can leave the
        // prefab pointing at nothing.
        var path = Dir + "/PlayerModel.controller";
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(path) ?? AnimatorController.CreateAnimatorControllerAtPath(path);
        ctrl.layers = Array.Empty<AnimatorControllerLayer>();
        ctrl.parameters = Array.Empty<AnimatorControllerParameter>();
        foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path))
            if (sub != null && sub != ctrl) Object.DestroyImmediate(sub, true); // old state machines, states, blend trees
        ctrl.AddLayer("Base Layer");
        ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Carrying", AnimatorControllerParameterType.Bool);

        var loco = ctrl.CreateBlendTreeInController("Locomotion", out var tree, 0);
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Speed";
        tree.useAutomaticThresholds = false;
        for (var i = 0; i < locomotion.Length; i++) tree.AddChild(locomotion[i], speeds[i]);
        ctrl.layers[0].stateMachine.defaultState = loco;

        ctrl.AddLayer("Carry");
        var layers = ctrl.layers;
        layers[1].defaultWeight = 1f;
        layers[1].blendingMode = AnimatorLayerBlendingMode.Override;
        layers[1].avatarMask = mask;
        layers[1].iKPass = false;
        ctrl.layers = layers;
        var sm = ctrl.layers[1].stateMachine;
        var empty = sm.AddState("Empty");
        var hold = sm.AddState("Carry");
        hold.motion = carry;
        sm.defaultState = empty;
        var on = empty.AddTransition(hold); on.hasExitTime = false; on.duration = 0.15f; on.AddCondition(AnimatorConditionMode.If, 0, "Carrying");
        var off = hold.AddTransition(empty); off.hasExitTime = false; off.duration = 0.15f; off.AddCondition(AnimatorConditionMode.IfNot, 0, "Carrying");
        EditorUtility.SetDirty(ctrl);
        return ctrl;
    }

    // ---------------- helpers ----------------

    private static string ProjectDir => Path.GetDirectoryName(Application.dataPath);

    private static void ResetToBindPose(GameObject model)
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterFbx);
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
        {
            if (t == model.transform) continue;
            var s = FindDeep(src.transform, t.name == "Mannequin" ? "BaseCorpo" : t.name);
            if (s == null) continue;
            t.localPosition = s.localPosition;
            t.localRotation = s.localRotation;
            t.localScale = s.localScale;
        }
    }

    private static float Height(GameObject model)
    {
        var r = model.GetComponentInChildren<SkinnedMeshRenderer>();
        var m = new Mesh();
        r.BakeMesh(m, false); // unscaled bake + position/rotation only gives world units for this FBX's 100x renderer scale
        var top = m.vertices.Select(v => (r.transform.rotation * v + r.transform.position).y).DefaultIfEmpty(0).Max();
        Object.DestroyImmediate(m);
        return top - model.transform.position.y;
    }

    private static Material FlatMaterial(string name, Color color, float smoothness)
    {
        var path = $"{MaterialDir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("HDRP/Lit") ?? throw Fail("HDRP/Lit shader not found")) { name = name };
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", smoothness);
        m.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(m);
        return m;
    }

    /// <summary>Overwrite an asset in place so references to it (and its GUID) survive a rebuild.</summary>
    private static T SaveAsset<T>(T obj, string path) where T : Object
    {
        obj.name = Path.GetFileNameWithoutExtension(path); // Unity expects the main object to carry the file's name
        var existing = AssetDatabase.LoadAssetAtPath<T>(path);
        if (existing == null)
        {
            AssetDatabase.CreateAsset(obj, path);
            return obj;
        }
        var name = obj.name;
        EditorUtility.CopySerialized(obj, existing);
        existing.name = name;
        EditorUtility.SetDirty(existing);
        return existing;
    }

    private static bool Approximately(Matrix4x4 x, Matrix4x4 y)
    {
        for (var i = 0; i < 16; i++) if (Mathf.Abs(x[i] - y[i]) > 1e-4f) return false;
        return true;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform c in root)
        {
            var r = FindDeep(c, name);
            if (r != null) return r;
        }
        return null;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var slash = path.LastIndexOf('/');
        EnsureFolder(path.Substring(0, slash));
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }

    private static Exception Fail(string message)
    {
        Debug.LogError("PCBS2Coop player model: " + message);
        return new InvalidOperationException(message);
    }
}
