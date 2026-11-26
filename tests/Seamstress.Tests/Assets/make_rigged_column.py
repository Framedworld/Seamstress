# Makes rigged-column.glb and rigged-column.fbx, the rigged test bodies RiggingTests reads, with Blender's own
# exporters, so the readers are tested on files Seamstress did not write.
#   blender -b --factory-startup -P make_rigged_column.py
# A closed column 1.6 m tall and 15 cm in radius (33 rings of 24), standing on the ground, skinned to a chain of three
# bones Hips (0.5 m), Spine (1.0 m) and Chest (1.3 m) with weights blending linearly between the bone heights.
import math, os
import bpy

HEIGHTS = (0.5, 1.0, 1.3)
NAMES = ("Hips", "Spine", "Chest")
RINGS, SEGMENTS, RADIUS, TOP = 33, 24, 0.15, 1.6

bpy.ops.wm.read_factory_settings(use_empty=True)

verts, faces = [], []
for r in range(RINGS):
    z = TOP * r / (RINGS - 1)
    for s in range(SEGMENTS):
        a = 2 * math.pi * s / SEGMENTS
        verts.append((RADIUS * math.cos(a), RADIUS * math.sin(a), z))
for r in range(RINGS - 1):
    for s in range(SEGMENTS):
        a, b = r * SEGMENTS + s, r * SEGMENTS + (s + 1) % SEGMENTS
        faces.append((a, b, b + SEGMENTS, a + SEGMENTS))
faces.append(tuple(reversed(range(SEGMENTS))))
faces.append(tuple(range((RINGS - 1) * SEGMENTS, RINGS * SEGMENTS)))
mesh = bpy.data.meshes.new("Column")
mesh.from_pydata(verts, [], faces)
body = bpy.data.objects.new("Column", mesh)
bpy.context.scene.collection.objects.link(body)

arm_data = bpy.data.armatures.new("Armature")
arm = bpy.data.objects.new("Armature", arm_data)
bpy.context.scene.collection.objects.link(arm)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode="EDIT")
tails = HEIGHTS[1:] + (TOP,)
parent = None
for name, head, tail in zip(NAMES, HEIGHTS, tails):
    bone = arm_data.edit_bones.new(name)
    bone.head = (0, 0, head)
    bone.tail = (0, 0, tail)
    if parent:
        bone.parent = parent
        bone.use_connect = True
    parent = bone
bpy.ops.object.mode_set(mode="OBJECT")


def weights(z):
    if z <= HEIGHTS[0]:
        return {0: 1.0}
    for j in range(len(HEIGHTS) - 1):
        if z <= HEIGHTS[j + 1]:
            t = (z - HEIGHTS[j]) / (HEIGHTS[j + 1] - HEIGHTS[j])
            return {j: 1 - t, j + 1: t}
    return {len(HEIGHTS) - 1: 1.0}


groups = [body.vertex_groups.new(name=n) for n in NAMES]
for v in mesh.vertices:
    for j, w in weights(v.co.z).items():
        if w > 0:
            groups[j].add([v.index], w, "REPLACE")
body.parent = arm
body.modifiers.new("Armature", "ARMATURE").object = arm

here = os.path.dirname(os.path.abspath(__file__))
bpy.ops.export_scene.gltf(filepath=os.path.join(here, "rigged-column.glb"), export_format="GLB")
bpy.ops.export_scene.fbx(filepath=os.path.join(here, "rigged-column.fbx"), add_leaf_bones=True)
