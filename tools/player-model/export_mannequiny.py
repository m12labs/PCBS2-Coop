"""Run in Blender with the official Mannequiny v0.2.0 .blend open."""

from collections import defaultdict
import math
from pathlib import Path
import sys

import bpy
from mathutils import Matrix


def export(target):
    source_body = bpy.data.objects["body.001"]
    source_rig = bpy.data.objects["mannequiny_rig"]
    joints = {}
    group_map = {}

    def add(name, parent, original, *weights):
        joints[name] = (parent, source_rig.data.bones[original])
        for group in weights:
            group_map[group] = name

    add("pelvis", None, "root.x", "root.x")
    add("spine_01", "pelvis", "spine_01.x", "spine_01.x")
    add("spine_02", "spine_01", "spine_02.x", "spine_02.x")
    add("neck_01", "spine_02", "neck.x", "neck.x", "c_neck_01.x")
    add("head", "neck_01", "head.x", "head.x", "c_skull_01.x", "c_skull_02.x", "c_skull_03.x")
    for side in ("l", "r"):
        def limb(name, parent, original, *weights):
            add(f"{name}.{side}", parent, f"{original}.{side}", *(f"{group}.{side}" for group in weights))

        limb("clavicle", "spine_02", "shoulder", "shoulder")
        limb("upperarm", f"clavicle.{side}", "arm", "arm_stretch", "c_arm_twist_offset")
        limb("lowerarm", f"upperarm.{side}", "forearm", "forearm_stretch", "forearm_twist")
        limb("hand", f"lowerarm.{side}", "hand", "hand")
        limb("thigh", "pelvis", "thigh", "thigh_stretch", "thigh_twist")
        limb("calf", f"thigh.{side}", "leg", "leg_stretch", "leg_twist")
        limb("foot", f"calf.{side}", "foot", "foot")
        limb("ball", f"foot.{side}", "toes_01", "toes_01")
        for finger in ("thumb", "index", "middle", "ring"):
            proximal = f"{finger}1"
            base = (proximal,) if finger == "thumb" else (f"c_{proximal}_base", proximal)
            limb(f"{finger}_01", f"hand.{side}", base[0], *base)
            limb(f"{finger}_02", f"{finger}_01.{side}", f"c_{finger}2", f"c_{finger}2")
            limb(f"{finger}_03", f"{finger}_02.{side}", f"c_{finger}3", f"c_{finger}3")

    world_vertices = [source_body.matrix_world @ vertex.co for vertex in source_body.data.vertices]
    bottom = min(vertex.z for vertex in world_vertices)
    top = max(vertex.z for vertex in world_vertices)
    scale = 1.8 / (top - bottom)
    turn = Matrix.Rotation(math.pi, 4, "Z")
    normalize = Matrix.Translation((0, 0, -bottom * scale)) @ Matrix.Scale(scale, 4) @ turn
    weights_by_vertex = []
    for vertex in source_body.data.vertices:
        weights = defaultdict(float)
        for weight in vertex.groups:
            if weight.weight > 0:
                weights[group_map[source_body.vertex_groups[weight.group].name]] += weight.weight
        if not weights:
            raise ValueError(f"Vertex {vertex.index} has no weights")
        weights_by_vertex.append(weights)

    collection = bpy.context.scene.collection
    armature = bpy.data.armatures.new("MannequinySkeleton")
    rig = bpy.data.objects.new("MannequinyRig", armature)
    collection.objects.link(rig)
    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    for name, (parent, original) in joints.items():
        bone = armature.edit_bones.new(name)
        bone.head = normalize @ source_rig.matrix_world @ original.head_local
        bone.tail = normalize @ source_rig.matrix_world @ original.tail_local
        if name == "spine_01":
            # The control rig starts its spine at the same point as the hips.
            bone.head = bone.head.lerp(bone.tail, 0.35)
        bone.align_roll(turn.to_3x3() @ source_rig.matrix_world.to_3x3() @ original.matrix_local.to_3x3().col[2])
        if parent is not None:
            bone.parent = armature.edit_bones[parent]
    bpy.ops.object.mode_set(mode="OBJECT")

    body = source_body.copy()
    body.data = source_body.data.copy()
    body.name = "Mannequiny"
    body.parent = rig
    body.matrix_parent_inverse = Matrix.Identity(4)
    body.matrix_basis = Matrix.Identity(4)
    body.animation_data_clear()
    body.modifiers.clear()
    body.constraints.clear()
    body.vertex_groups.clear()
    body.hide_viewport = False
    body.hide_render = False
    collection.objects.link(body)
    body.hide_set(False)
    if body.data.shape_keys:
        body.shape_key_clear()
    for vertex, world_point in zip(body.data.vertices, world_vertices):
        vertex.co = normalize @ world_point
    groups = {name: body.vertex_groups.new(name=name) for name in joints}
    for index, weights in enumerate(weights_by_vertex):
        total = sum(weights.values())
        for name, weight in weights.items():
            groups[name].add([index], weight / total, "REPLACE")
    skin = body.modifiers.new("MannequinySkin", "ARMATURE")
    skin.object = rig

    bpy.context.scene.unit_settings.system = "METRIC"
    bpy.context.scene.unit_settings.scale_length = 1
    body.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=str(target),
        object_types={"ARMATURE", "MESH"},
        use_selection=True,
        apply_scale_options="FBX_SCALE_UNITS",
        add_leaf_bones=False,
        bake_anim=False,
        axis_forward="-Z",
        axis_up="Y",
    )
    print(f"MANNEQUINY: {len(body.data.vertices)} vertices, {len(joints)} bones, {top-bottom:.3f} m before normalization")


if __name__ == "__main__":
    export(Path(sys.argv[sys.argv.index("--") + 1]).resolve())
