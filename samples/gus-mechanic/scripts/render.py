"""Blender check render of an exported outfit over the avatar.
blender -b -P render.py -- <garment.glb> <avatar.obj> <out-prefix> [view,view,...] [samples]
Views: front, back, side, q (three-quarter), chest, legs, hip
"""
import bpy, sys, math
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
glb, avatar, prefix = argv[0], argv[1], argv[2]
views = (argv[3] if len(argv) > 3 else "front,back,side").split(",")
samples = int(argv[4]) if len(argv) > 4 else 24

bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene
sc.render.engine = 'CYCLES'
sc.cycles.samples = samples
sc.cycles.use_denoising = True
try:
    pref = bpy.context.preferences.addons['cycles'].preferences
    pref.compute_device_type = 'OPTIX'
    pref.get_devices()
    for dev in pref.devices:
        dev.use = dev.type == 'OPTIX'
    sc.cycles.device = 'GPU'
except Exception as e:
    print('device', e)

world = bpy.data.worlds.new('studio')
sc.world = world
world.use_nodes = True
bg = world.node_tree.nodes['Background']
bg.inputs[0].default_value = (0.75, 0.77, 0.80, 1)
bg.inputs[1].default_value = 0.35

bpy.ops.import_scene.gltf(filepath=glb)
for o in list(sc.objects):
    if o.type == 'MESH':
        for p in o.data.polygons:
            p.use_smooth = True
bpy.ops.wm.obj_import(filepath=avatar, forward_axis='NEGATIVE_Z', up_axis='Y')
skin = bpy.data.materials.new('skin')
skin.use_nodes = True
b = skin.node_tree.nodes.get('Principled BSDF')
b.inputs['Base Color'].default_value = (0.55, 0.42, 0.36, 1)
b.inputs['Roughness'].default_value = 0.6
for o in bpy.context.selected_objects:
    if o.type == 'MESH':
        o.data.materials.clear()
        o.data.materials.append(skin)
        for p in o.data.polygons:
            p.use_smooth = True


def aim(obj, target):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat('-Z', 'Y').to_euler()


def lamp(name, pos, color, power, size):
    d = bpy.data.lights.new(name, 'AREA')
    d.energy = power; d.shape = 'DISK'; d.size = size; d.color = color
    o = bpy.data.objects.new(name, d)
    sc.collection.objects.link(o)
    o.location = pos
    aim(o, (0, 0, 1.1))
    return o


# Blender is Z-up: Seamstress (x, y, z) -> Blender (x, -z, y)
key = lamp('key', (-2.0, -3.0, 3.2), (1, 0.95, 0.88), 420, 2.5)
fill = lamp('fill', (2.4, -2.2, 1.6), (0.8, 0.88, 1), 200, 2.5)
rim = lamp('rim', (1.2, 2.6, 2.6), (1, 0.92, 0.8), 380, 1.5)
rim2 = lamp('rim2', (-1.6, 2.4, 1.8), (0.8, 0.88, 1), 220, 2.0)

cd = bpy.data.cameras.new('cam')
cam = bpy.data.objects.new('cam', cd)
sc.collection.objects.link(cam)
sc.camera = cam
cd.type = 'ORTHO'
sc.render.film_transparent = False
sc.view_settings.view_transform = 'Standard'

# name: (camera position, target, ortho span, width, height)
VIEWS = {
    'front': ((0, -4, 1.0), (0, 0, 0.93), 1.95, 1150, 1300),
    'back': ((0, 4, 1.0), (0, 0, 0.93), 1.95, 1150, 1300),
    'side': ((4, 0, 1.0), (0, 0, 0.93), 1.95, 800, 1300),
    'q': ((2.4, -3.2, 1.3), (0, 0, 0.93), 1.95, 1150, 1300),
    'chest': ((0.5, -4, 1.45), (0, 0, 1.30), 0.95, 1300, 1050),
    'chestback': ((-0.5, 4, 1.45), (0, 0, 1.30), 0.95, 1300, 1050),
    'legs': ((0.5, -4, 0.7), (0, 0, 0.55), 1.15, 1100, 1250),
    'hip': ((-0.8, -4, 1.05), (0, 0, 0.92), 0.8, 1300, 1050),
    'arm': ((2.0, -3.4, 1.6), (0.42, 0, 1.33), 0.7, 1200, 1000),
}
for v in views:
    pos, target, span, w, h = VIEWS[v]
    cam.location = pos
    aim(cam, target)
    cd.ortho_scale = span
    sc.render.resolution_x, sc.render.resolution_y = w, h
    sc.render.filepath = f"{prefix}-{v}.png"
    bpy.ops.render.render(write_still=True)
    print('saved', sc.render.filepath)
