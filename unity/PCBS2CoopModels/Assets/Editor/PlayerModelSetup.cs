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
/// Builds the remote avatar from GDQuest's Mannequiny and clips of Quaternius's Universal
/// Animation Library (docs/playermodel.md section 4). Safe to run again; then run PCBS2Coop/Build Player Bundle.
/// Batch: Unity.exe -batchmode -quit -projectPath unity/PCBS2CoopModels -executeMethod PlayerModelSetup.Rebuild
/// </summary>
public static class PlayerModelSetup
{
    private const string CharacterFbx = "Assets/ThirdParty/GDQuest/Mannequiny.fbx";
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

    private static readonly Vector3 CarryBoxSize = new(0.40f, 0.36f, 0.60f);

    /// <summary>Humanoid mapping for the neutral export, which faces +Z with its left arm on -X.</summary>
    private static Dictionary<string, string> CharacterBones()
    {
        var mapping = new Dictionary<string, string>
        {
            { "Hips", "pelvis" }, { "Spine", "spine_01" }, { "Chest", "spine_02" }, { "Neck", "neck_01" }, { "Head", "head" },
        };
        var limbs = new (string Human, string Bone)[]
        {
            ("Shoulder", "clavicle"), ("UpperArm", "upperarm"), ("LowerArm", "lowerarm"), ("Hand", "hand"),
            ("UpperLeg", "thigh"), ("LowerLeg", "calf"), ("Foot", "foot"), ("Toes", "ball"),
        };
        var segments = new[] { "Proximal", "Intermediate", "Distal" };
        foreach (var (side, suffix) in new[] { ("Left", ".l"), ("Right", ".r") })
        {
            foreach (var (human, bone) in limbs) mapping.Add(side + human, bone + suffix);
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring" })
                for (var i = 0; i < segments.Length; i++)
                    mapping.Add($"{side} {finger} {segments[i]}", $"{finger.ToLowerInvariant()}_0{i + 1}{suffix}");
        }
        return mapping;
    }

    [MenuItem("PCBS2Coop/Rebuild Player Model")]
    public static void Rebuild()
    {
        EnsureFolder(Dir);
        EnsureFolder(MaterialDir);
        ConfigureCharacterImport();
        var clips = UalClips.Select(c => AssetDatabase.LoadAssetAtPath<AnimationClip>($"{ClipDir}/UAL.{c.Name}.anim")
                                         ?? throw Fail($"missing {ClipDir}/UAL.{c.Name}.anim (run PCBS2Coop/Extract Animation Clips)")).ToArray();
        var sourceIdle = clips[0];

        var model = BuildModel(out var avatar);
        try
        {
            var animator = model.GetComponent<Animator>();
            using var poses = new HumanPoseHandler(avatar, model.transform);
            var bind = new HumanPose();
            poses.GetHumanPose(ref bind);
            clips[0] = BakeIdlePose(model, animator, poses, sourceIdle, bind.muscles);
            var carry = BakeCarryPose(model, animator, poses, clips[0], bind.muscles);
            var workbench = BakeWorkPose(model, animator, poses, clips[0], bind.muscles, WorkPose.Workbench);
            var toolTable = BakeWorkPose(model, animator, poses, clips[0], bind.muscles, WorkPose.ToolTable);
            var monitor = BakeWorkPose(model, animator, poses, clips[0], bind.muscles, WorkPose.Monitor);
            // Humanoid root motion scales with the avatar, so each clip covers (normalized speed × human scale) m/s here.
            var humanScale = animator.humanScale;
            var normalized = ReadUalSpeeds();
            var speeds = UalClips.Select((c, i) => i == 0 ? 0f : normalized[c.Name] * humanScale).ToArray(); // idle sits at 0
            for (var i = 1; i < speeds.Length; i++)
                if (speeds[i] < 0.3f || speeds[i] <= speeds[i - 1]) throw Fail("implausible clip speeds: " + SpeedList(speeds));

            var mask = BuildUpperBodyMask();
            var controller = BuildController(clips, speeds, carry, workbench, toolTable, monitor, mask);

            ResetToBindPose(model);
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
        var imp = AssetImporter.GetAtPath(CharacterFbx) as ModelImporter
                  ?? throw Fail($"missing {CharacterFbx}");
        imp.globalScale = 1f;
        imp.useFileUnits = true;
        imp.bakeAxisConversion = true;
        imp.importBlendShapes = false;
        imp.importCameras = false;
        imp.importLights = false;
        imp.importVisibility = false;
        imp.animationType = ModelImporterAnimationType.Generic; // build the Humanoid Avatar with an explicit bone map
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
        if (src == null) throw Fail($"could not import {CharacterFbx}");
        var model = Object.Instantiate(src);
        model.name = "PlayerModel";
        try
        {
            var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length != 1) throw Fail($"expected one Mannequiny renderer, found {renderers.Length}");
            var renderer = renderers[0];
            renderer.sharedMesh = SaveAsset(Object.Instantiate(renderer.sharedMesh), Dir + "/PlayerModel.mesh.asset");
            renderer.sharedMaterials = renderer.sharedMaterials.Select(m => m.name switch
            {
                "Azul" => FlatMaterial("Azul", new Color(0.035f, 0.235f, 0.515f), 0.60f),
                "Negro" => FlatMaterial("Negro", new Color(0.053f, 0.051f, 0.054f), 0.70f),
                "Blanco" => FlatMaterial("Blanco", Color.white, 0.50f),
                _ => throw Fail($"unexpected Mannequiny material: {m.name}"),
            }).ToArray();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.updateWhenOffscreen = false;

            var names = model.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (names.Count > 0) throw Fail("duplicate transform names break the avatar: " + string.Join(", ", names));

            avatar = AvatarBuilder.BuildHumanAvatar(model, Describe(model.transform, CharacterBones()));
            if (!avatar.isValid || !avatar.isHuman) throw Fail("the character's humanoid avatar is invalid");
            avatar = SaveAsset(avatar, Dir + "/PlayerModel.avatar.asset");
            var animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.avatar = avatar;
            if (animator.GetBoneTransform(HumanBodyBones.LeftHand).position.x >= animator.GetBoneTransform(HumanBodyBones.RightHand).position.x ||
                animator.GetBoneTransform(HumanBodyBones.LeftToes).position.z <= animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.z)
                throw Fail("Mannequiny must face +Z in its neutral pose");
            return model;
        }
        catch
        {
            Object.DestroyImmediate(model);
            throw;
        }
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

    private enum WorkPose { Free, Workbench, ToolTable, Monitor }

    private static AnimationClip BakeIdlePose(GameObject model, Animator animator, HumanPoseHandler poses, AnimationClip source, float[] bind)
    {
        ResetToBindPose(model);
        source.SampleAnimation(model, 0f);
        PlaceHand(animator, true, new Vector3(-0.245f, 1.00f, 0.065f), new Vector3(0f, -1f, 0.18f), Vector3.right);
        PlaceHand(animator, false, new Vector3(0.245f, 1.00f, 0.065f), new Vector3(0f, -1f, 0.18f), Vector3.left);
        var pose = CapturePose(poses, bind, 0.32f);
        var idle = Object.Instantiate(source);
        for (var i = 0; i < HumanTrait.MuscleCount; i++)
        {
            var muscle = HumanTrait.MuscleName[i];
            if (IsArmMuscle(muscle)) WriteMuscle(idle, muscle, AnimationCurve.Constant(0f, source.length, pose.muscles[i]));
        }
        return SaveAsset(idle, Dir + "/PlayerModel.Idle.anim");
    }

    private static AnimationClip BakeCarryPose(GameObject model, Animator animator, HumanPoseHandler poses, AnimationClip idle, float[] bind)
    {
        ResetToBindPose(model);
        idle.SampleAnimation(model, 0f);
        PlaceHand(animator, true, new Vector3(-0.158f, 0.94f, 0.35f), Vector3.forward, Vector3.up);
        PlaceHand(animator, false, new Vector3(0.158f, 0.94f, 0.35f), Vector3.forward, Vector3.up);
        var pose = CapturePose(poses, bind, 0.19f);
        poses.SetHumanPose(ref pose);

        // Fit the prop to the resulting Humanoid pose, which can differ from the original IK target.
        // A palm midpoint is about halfway from the wrist to the base of the middle finger.
        Vector3 Palm(HumanBodyBones wrist, HumanBodyBones middle) =>
            Vector3.Lerp(animator.GetBoneTransform(wrist).position, animator.GetBoneTransform(middle).position, 0.55f);
        var leftPalm = Palm(HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal);
        var rightPalm = Palm(HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal);
        var right = (rightPalm - leftPalm).normalized;
        var forward = Vector3.Cross(right, model.transform.up).normalized;
        var up = Vector3.Cross(forward, right);
        var anchor = new GameObject("CarryAnchor").transform;
        anchor.SetPositionAndRotation(
            (leftPalm + rightPalm) * 0.5f + up * (CarryBoxSize.y * 0.5f + 0.018f) + forward * 0.16f,
            Quaternion.LookRotation(forward, up));
        anchor.SetParent(animator.GetBoneTransform(HumanBodyBones.Chest), true);
        var volume = new GameObject("CarryVolume").transform;
        volume.SetParent(anchor, false);
        volume.localScale = CarryBoxSize;

        var carry = new AnimationClip();
        for (var i = 0; i < HumanTrait.MuscleCount; i++)
        {
            var muscle = HumanTrait.MuscleName[i];
            if (IsArmMuscle(muscle) || IsTorsoMuscle(muscle))
                WriteMuscle(carry, muscle, AnimationCurve.Constant(0f, 1f, pose.muscles[i]));
        }
        return SaveClip(carry, Dir + "/PlayerModel.Carry.anim");
    }

    private static AnimationClip BakeWorkPose(GameObject model, Animator animator, HumanPoseHandler poses, AnimationClip idle, float[] bind, WorkPose work)
    {
        var clip = new AnimationClip();
        var curves = new AnimationCurve[HumanTrait.MuscleCount];
        for (var frame = 0; frame <= 4; frame++)
        {
            ResetToBindPose(model);
            idle.SampleAnimation(model, 0f);
            var sway = Mathf.Sin(frame * Mathf.PI * 0.5f);
            var (lean, look, braceHeight, reachHeight, reach, fineMotion) = work switch
            {
                WorkPose.ToolTable => (21f, 23f, 0.97f, 1.065f, 0.52f, 0.032f),
                WorkPose.Monitor => (6f, -3f, 1.03f, 1.05f, 0.37f, 0.012f),
                _ => (15f, 17f, 1.01f, 1.12f, 0.46f, 0.020f),
            };
            var axis = model.transform.right;
            var spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            spine.rotation = Quaternion.AngleAxis(lean + sway * 1.5f, axis) * spine.rotation;
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            head.rotation = Quaternion.AngleAxis(look, axis) * head.rotation;
            PlaceHand(animator, true, new Vector3(-0.19f, braceHeight, reach - 0.065f), Vector3.forward, Vector3.down);
            PlaceHand(animator, false, new Vector3(0.17f + sway * fineMotion, reachHeight + sway * fineMotion * 0.3f, reach + sway * fineMotion),
                Vector3.forward, Vector3.down);
            var pose = CapturePose(poses, bind, work == WorkPose.ToolTable ? 0.52f : 0.36f);
            for (var i = 0; i < curves.Length; i++)
            {
                var muscle = HumanTrait.MuscleName[i];
                if (!IsArmMuscle(muscle) && !IsTorsoMuscle(muscle) && !muscle.StartsWith("Head") && !muscle.StartsWith("Neck")) continue;
                curves[i] ??= new AnimationCurve();
                curves[i].AddKey(new Keyframe(frame, pose.muscles[i]));
            }
        }
        for (var i = 0; i < curves.Length; i++)
            if (curves[i] != null) WriteMuscle(clip, HumanTrait.MuscleName[i], curves[i]);
        return SaveClip(clip, $"{Dir}/PlayerModel.{work}.anim");
    }

    private static HumanPose CapturePose(HumanPoseHandler poses, float[] bind, float fingerCurl)
    {
        var pose = new HumanPose();
        poses.GetHumanPose(ref pose);
        for (var i = 0; i < pose.muscles.Length; i++)
        {
            var muscle = HumanTrait.MuscleName[i];
            if (!IsFingerMuscle(muscle)) continue;
            var curl = muscle.Contains("Stretched") && !muscle.Contains("Thumb") ? fingerCurl : 0f;
            pose.muscles[i] = Mathf.Clamp(bind[i] - curl, -1f, 1f);
        }
        return pose;
    }

    private static bool IsFingerMuscle(string name) => name.Contains("Thumb") || name.Contains("Index") ||
        name.Contains("Middle") || name.Contains("Ring");

    private static bool IsArmMuscle(string name) =>
        (name.StartsWith("Left") || name.StartsWith("Right")) &&
        (name.Contains("Shoulder") || name.Contains("Arm") || name.Contains("Forearm") || name.Contains("Hand") || IsFingerMuscle(name));

    private static bool IsTorsoMuscle(string name) =>
        name.StartsWith("Spine") || name.StartsWith("Chest") || name.StartsWith("UpperChest");

    private static void WriteMuscle(AnimationClip clip, string muscle, AnimationCurve curve)
    {
        if (IsFingerMuscle(muscle))
        {
            // Unity's serialized finger bindings are e.g. LeftHand.Index.1 Stretched.
            var words = muscle.Split(new[] { ' ' }, 3);
            muscle = $"{words[0]}Hand.{words[1]}.{words[2]}";
        }
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), muscle), curve);
    }

    private static void PlaceHand(Animator animator, bool left, Vector3 target, Vector3 fingers, Vector3 palm)
    {
        var upper = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        var lower = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        var wrist = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
        var middle = animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
        var thumb = animator.GetBoneTransform(left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal);
        var root = animator.transform;
        Reach(upper, lower, wrist, root.TransformPoint(target), root.TransformDirection(new Vector3(left ? -0.7f : 0.7f, -0.4f, -1f)));

        Vector3 PalmNormal() => Vector3.Cross(middle.position - wrist.position, thumb.position - wrist.position).normalized * (left ? -1f : 1f);
        var wantPalm = root.TransformDirection(palm);
        var forearm = (wrist.position - lower.position).normalized;
        // Put most of the twist into the forearm before aiming the wrist.
        var twist = Vector3.SignedAngle(Vector3.ProjectOnPlane(PalmNormal(), forearm), Vector3.ProjectOnPlane(wantPalm, forearm), forearm);
        lower.rotation = Quaternion.AngleAxis(twist, forearm) * lower.rotation;
        var current = Quaternion.LookRotation(middle.position - wrist.position, PalmNormal());
        var desired = Quaternion.LookRotation(root.TransformDirection(fingers), wantPalm);
        wrist.rotation = desired * Quaternion.Inverse(current) * wrist.rotation;
    }

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

    private static AnimationClip SaveClip(AnimationClip clip, string path)
    {
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = true;
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

    private static AvatarMask BuildUpperBodyMask()
    {
        var mask = new AvatarMask { name = "PlayerModel.CarryMask" };
        for (var i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
        {
            var part = (AvatarMaskBodyPart)i;
            mask.SetHumanoidBodyPartActive(part, part is AvatarMaskBodyPart.Body or AvatarMaskBodyPart.Head or
                AvatarMaskBodyPart.LeftArm or AvatarMaskBodyPart.RightArm or AvatarMaskBodyPart.LeftFingers or AvatarMaskBodyPart.RightFingers);
        }
        return SaveAsset(mask, Dir + "/PlayerModel.CarryMask.mask");
    }

    /// <summary>In-place locomotion and one masked upper-body layer. Carry takes priority over work.</summary>
    private static AnimatorController BuildController(AnimationClip[] locomotion, float[] speeds, AnimationClip carry,
        AnimationClip workbench, AnimationClip toolTable, AnimationClip monitor, AvatarMask mask)
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
        ctrl.AddParameter("WorkPose", AnimatorControllerParameterType.Int);

        var loco = ctrl.CreateBlendTreeInController("Locomotion", out var tree, 0);
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Speed";
        tree.useAutomaticThresholds = false;
        for (var i = 0; i < locomotion.Length; i++) tree.AddChild(locomotion[i], speeds[i]);
        ctrl.layers[0].stateMachine.defaultState = loco;

        ctrl.AddLayer("Upper Body");
        var layers = ctrl.layers;
        layers[1].defaultWeight = 1f;
        layers[1].blendingMode = AnimatorLayerBlendingMode.Override;
        layers[1].avatarMask = mask;
        layers[1].iKPass = false;
        ctrl.layers = layers;
        var sm = ctrl.layers[1].stateMachine;
        sm.defaultState = AddPose("Free", null, WorkPose.Free);
        AddPose("Carry", carry, null);
        AddPose("Workbench", workbench, WorkPose.Workbench);
        AddPose("ToolTable", toolTable, WorkPose.ToolTable);
        AddPose("Monitor", monitor, WorkPose.Monitor);
        EditorUtility.SetDirty(ctrl);
        return ctrl;

        AnimatorState AddPose(string name, Motion motion, WorkPose? work)
        {
            var state = sm.AddState(name);
            state.motion = motion;
            var transition = sm.AddAnyStateTransition(state);
            transition.canTransitionToSelf = false;
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0.18f;
            transition.AddCondition(work.HasValue ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If, 0f, "Carrying");
            if (work is { } pose) transition.AddCondition(AnimatorConditionMode.Equals, (int)pose, "WorkPose");
            return state;
        }
    }

    // ---------------- helpers ----------------

    private static string ProjectDir => Path.GetDirectoryName(Application.dataPath);

    private static void ResetToBindPose(GameObject model)
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterFbx);
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
        {
            if (t == model.transform) continue;
            var s = FindDeep(src.transform, t.name);
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
        r.BakeMesh(m, true);
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
