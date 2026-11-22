"""Gus mechanic outfit, second pass: built with the block tools of the Seamstress MCP (load_avatar from FBX,
named edges, sew_named, add_tube, add_patch, add_hardware, tile-size materials).

  python build.py [stage ...]      stages: pants tee jacket trim hood accessories finish   (default: all)

Each stage saves a project under .tools/gus-outfit/work and the next one opens it, so a stage can be re-run alone.
"""
import sys, json, math, pathlib, time
sys.path.insert(0, str(pathlib.Path(__file__).parent))
from mcp import Mcp, ROOT

OUT = ROOT / "samples/gus-mechanic"
TEX = OUT / "textures"
WORK = ROOT / ".tools/gus-outfit/work"
FBX = pathlib.Path.home() / "Documents/gus.Fbx"
WORK.mkdir(parents=True, exist_ok=True)

ids = {}          # piece name -> id
garments = {}     # garment name -> id


def pts(*points):
    """(x, y, 'edge') or (x, y, cx, cy, 'edge') -> add_piece points; the name is the edge leaving that point."""
    out = []
    for p in points:
        d = {"x": p[0], "y": p[1]}
        rest = list(p[2:])
        if rest and isinstance(rest[-1], str):
            d["edge"] = rest.pop()
        if len(rest) == 2:
            d["cx"], d["cy"] = rest
        out.append(d)
    return out


def garment(m, name, pd, layer, offset=0.0, thick=None):
    garments[name] = m.create_garment(name=name, particleDistance=pd, layer=layer, bodyOffsetMm=offset, thicknessMm=thick)["garmentId"]
    return garments[name]


def piece(m, name, points, on=None, mirror=False, depth=25, curv=0.0, fabric=None, **kw):
    r = m.add_piece(name=name, points=points, drawnOn=on, mirrorX=mirror or None, depthCm=depth, curvature=curv, fabric=fabric, **kw)
    ids[name] = r["pieceId"]
    return r


def sew(m, a, ea, b, eb):
    r = m.sew_edges(pieceAId=ids[a], edgeA=ea, pieceBId=ids[b], edgeB=eb)
    if r.get("warning"):
        print(f"  ! {a}.{ea} ({r['lengthACm']}) <-> {b}.{eb} ({r['lengthBCm']}): {r['mismatchPercent']}%")


def named(m, a, b, names):
    m.sew_named(pieceAId=ids[a], pieceBId=ids[b], names=names)


def sim(m, name, frames=90, self_collision=False):
    r = m.simulate(garmentId=garments[name], frames=frames, selfCollision=self_collision)
    report(name, r)
    return r


def report(name, r):
    opened = r.get("openSeams") or []
    print(f"  sim {name}: {r['frames']}f {r['elapsedMs']:.0f}ms gap={r['seamGapMetres']*1000:.1f}mm speed={r['peakSpeedMetresPerSec']} open={len(opened)}")
    for o in opened[:4]:
        print(f"     OPEN {o['kind']} {o['pieceA']}[{o['edgeA']}] -> {o['pieceB']}: {o['gapMm']}mm  {o['hint'][:70]}")


def patch(m, name, host, **kw):
    r = m.add_patch(hostPieceId=ids[host], name=name, **kw)
    ids[name] = r["pieces"][0]["pieceId"]
    garments[r["garment"]] = r["garmentId"]
    if r.get("simulation"):
        opened = r["simulation"].get("openSeams") or []
        if opened:
            print(f"     {name}: open {[(o['pieceA'], o['gapMm']) for o in opened[:3]]}")
    return r


def tex(n):
    return str(TEX / f"{n}.png")


CANVAS = dict(density=520, stretch=92, shear=80, bend=58, damping=38, friction=60, thickness=2.4)
JERSEY = dict(preset="Jersey Knit", bend=22, thickness=1.6, friction=55, stretch=45)


# ------------------------------------------------------------------------------------------------------------------
def stage_pants(m):
    m.new_project(name="Gus mechanic")
    r = m.load_avatar(path=str(FBX))
    tape = r["landmarks"]["tape"]
    print("  body:", r["meshesUsed"], "| crotch", tape["crotchHeightCm"], "hips", tape["torso"][0]["circumferenceCm"],
          "thigh", tape["leftLeg"][0]["circumferenceCm"], "arm droop", tape["arms"][0]["droopDegrees"])
    (WORK / "tape.json").write_text(json.dumps(tape))

    garment(m, "Work pants", pd=1.0, layer=0, thick=2.4)
    front = pts((0, 8, "hem"), (22.5, 8, "out_lo"), (23, 50, "out_hi"), (31, 93, "out_hip"), (27.5, 104.5, "waist"),
                (0, 104.5, "cf"), (0, 76, 0, 65.5, "crotch"), (-6, 65, "in_hi"), (-1.5, 50, "in_lo"))
    back = pts((-1, 8, "hem"), (23.5, 8, "out_lo"), (24, 50, "out_hi"), (31.5, 93, "out_hip"), (27.5, 105.5, "waist"),
               (0, 107, "cb"), (0, 80, 0.5, 65.5, "crotch"), (-9.5, 64.5, "in_hi"), (-3, 50, "in_lo"))
    leg = {**CANVAS, "shrinkWarp": 1.06}
    piece(m, "pf_l", front, "front", depth=20, curv=100, fabric=leg)
    piece(m, "pf_r", front, "front", mirror=True, depth=20, curv=100, fabric=leg)
    piece(m, "pb_l", back, "back", depth=22, curv=100, fabric=leg)
    piece(m, "pb_r", back, "back", mirror=True, depth=22, curv=100, fabric=leg)
    band = dict(density=600, stretch=96, shear=90, bend=95, damping=40, friction=95, thickness=2.6, shrinkWeft=0.93)
    wf = pts((0, 105, "bottom"), (27.5, 105, "out"), (27.5, 109.5, "top"), (0, 109.5, "in"))
    wb = pts((0, 107.5, "bottom"), (27.5, 106, "out"), (27.5, 110.5, "top"), (0, 112, "in"))
    piece(m, "wb_fl", wf, "front", depth=21, curv=70, fabric=band)
    piece(m, "wb_fr", wf, "front", mirror=True, depth=21, curv=70, fabric=band)
    piece(m, "wb_bl", wb, "back", depth=23, curv=70, fabric=band)
    piece(m, "wb_br", wb, "back", mirror=True, depth=23, curv=70, fabric=band)
    for s in "lr":
        named(m, f"pf_{s}", f"pb_{s}", ["out_lo", "out_hi", "out_hip", "in_hi", "in_lo"])
        sew(m, f"wb_f{s}", "bottom", f"pf_{s}", "waist")
        sew(m, f"wb_b{s}", "bottom", f"pb_{s}", "waist")
        named(m, f"wb_f{s}", f"wb_b{s}", ["out"])
    named(m, "pf_l", "pf_r", ["cf", "crotch"])
    named(m, "pb_l", "pb_r", ["cb", "crotch"])
    named(m, "wb_fl", "wb_fr", ["in"])
    named(m, "wb_bl", "wb_br", ["in"])
    m.set_material(garmentId=garments["Work pants"], name="Charcoal canvas", texture=tex("canvas_charcoal"), tileCm=24, roughness=0.88)
    sim(m, "Work pants", 110)
    sim(m, "Work pants", 60, self_collision=True)      # lets the hems stack on the boots without passing through
    sim(m, "Work pants", 30)

    # Trim: knee panels sewn all round; cargo pockets and flaps centred on the outseam.
    edge_len = math.hypot(31 - 23, 93 - 50)
    lo, hi, flap_lo, flap_hi = 16.5, 37.5, 39.0, 46.5
    for s, sx in (("l", 1), ("r", -1)):
        x = 2.5 if s == "l" else -19.5
        patch(m, f"knee_{s}", f"pf_{s}", kind="patch", x=x, y=37, widthCm=17, heightCm=26, easePercent=4, bend=58, simulate=False)
        m.set_material(pieceId=ids[f"knee_{s}"], name="Knee panel", texture=tex("knee_panel"), repeat=0, roughness=0.9)
        # the mirrored piece's edge runs the other way, so distances along it are measured from the other end
        d = (lambda v: v) if s == "l" else (lambda v: edge_len - v)
        patch(m, f"cargo_{s}", f"pf_{s}", kind="pocket", acrossEdge="out_hi", fromCm=d(lo), toCm=d(hi), widthCm=19,
              easePercent=14, bend=80, simulate=False)
        patch(m, f"cargo_flap_{s}", f"pf_{s}", kind="flap", acrossEdge="out_hi", fromCm=d(flap_lo), toCm=d(flap_hi),
              widthCm=20.5, easePercent=4, simulate=False)
    sim(m, "Work pants trim", 70, self_collision=True)
    sim(m, "Work pants trim 2", 70, self_collision=True)


def stage_tee(m):
    garment(m, "Tee", pd=1.1, layer=3, offset=2.6, thick=1.6)
    front = pts((-31, 101, "hem"), (31, 101, 25.5, 113, "side_l"), (28.5, 128, 21, 133, "arm_l"), (21, 156.5, "sh_l"),
                (12, 159.5, 10, 151.5, "neck_l"), (0, 151, -10, 151.5, "neck_r"), (-12, 159.5, "sh_r"),
                (-21, 156.5, -21, 133, "arm_r"), (-28.5, 128, -25.5, 113, "side_r"))
    back = pts((-31, 101, "hem"), (31, 101, 25.5, 113, "side_l"), (28.5, 128, 21.5, 133, "arm_l"), (21, 156.5, "sh_l"),
               (12, 159.5, 8, 155, "neck_l"), (0, 155, -8, 155, "neck_r"), (-12, 159.5, "sh_r"),
               (-21, 156.5, -21.5, 133, "arm_r"), (-28.5, 128, -25.5, 113, "side_r"))
    piece(m, "tee_f", front, "front", depth=27, curv=40, fabric=JERSEY)
    piece(m, "tee_b", back, "back", depth=26, curv=40, fabric=JERSEY)
    hem = dict(preset="Jersey Knit", bend=40, thickness=1.8, friction=55, stretch=70, shrinkWeft=0.97)
    band = pts((-31, 97.5, "bottom"), (31, 97.5, "side_l"), (31, 101, "top"), (-31, 101, "side_r"))
    piece(m, "tee_hem_f", band, "front", depth=27, curv=40, fabric=hem)
    piece(m, "tee_hem_b", band, "back", depth=26, curv=40, fabric=hem)
    named(m, "tee_f", "tee_b", ["side_l", "side_r", "sh_l", "sh_r"])
    named(m, "tee_hem_f", "tee_hem_b", ["side_l", "side_r"])
    sew(m, "tee_hem_f", "top", "tee_f", "hem")
    sew(m, "tee_hem_b", "top", "tee_b", "hem")
    m.set_material(garmentId=garments["Tee"], name="Black jersey", texture=tex("jersey_black"), tileCm=16, roughness=0.92)
    sim(m, "Tee", 130)      # one pass, real friction from the start: the solver sews before friction takes hold


def stage_jacket(m):
    garment(m, "Jacket", pd=1.0, layer=4, offset=8.5, thick=2.6)
    canvas = dict(density=480, stretch=92, shear=80, bend=62, damping=40, friction=55, thickness=2.6)
    back = pts((-33, 103, "hem"), (33, 103, "side_l"), (33, 129, 27.5, 134, "arm_l"), (27, 155.5, "sh_l"),
               (11, 161, 7, 159, "neck_l"), (0, 159, -7, 159, "neck_r"), (-11, 161, "sh_r"),
               (-27, 155.5, -27.5, 134, "arm_r"), (-33, 129, "side_r"))
    piece(m, "jb", back, "back", depth=30, curv=95, fabric=canvas)
    front = pts((4, 103, "hem"), (33, 103, "side"), (33, 129, 27.5, 134, "arm"), (27, 155.5, "sh"), (11, 161, 5, 154, "neck"), (4, 146, "cf"))
    piece(m, "jf_l", front, "front", curv=45, fabric=canvas, pos3d=[0.215, 1.32, 0.29], rot3d=[0, 22, 0])
    piece(m, "jf_r", front, "front", mirror=True, curv=45, fabric=canvas, pos3d=[-0.215, 1.32, 0.29], rot3d=[0, -22, 0])
    band = dict(density=560, stretch=96, shear=90, bend=92, damping=42, friction=55, thickness=2.8)
    piece(m, "jhem_b", pts((-33, 98, "bottom"), (33, 98, "side_l"), (33, 103, "top"), (-33, 103, "side_r")), "back", depth=30, curv=95, fabric=band)
    fb = pts((4, 98, "bottom"), (33, 98, "side"), (33, 103, "top"), (4, 103, "cf"))
    piece(m, "jhem_l", fb, "front", curv=45, fabric=band, pos3d=[0.215, 1.005, 0.29], rot3d=[0, 22, 0])
    piece(m, "jhem_r", fb, "front", mirror=True, curv=45, fabric=band, pos3d=[-0.215, 1.005, 0.29], rot3d=[0, -22, 0])
    # A stay across the open front keeps the two edges parallel while it drapes; it is removed afterwards.
    piece(m, "TEMP_stay", pts((-4, 103, "bottom"), (4, 103, "l"), (4, 146, "top"), (-4, 146, "r")), "front", depth=33,
          fabric=dict(bend=80, stretch=95, thickness=2.6))
    for s in "lr":
        sew(m, f"jf_{s}", "side", "jb", f"side_{s}")
        sew(m, f"jf_{s}", "sh", "jb", f"sh_{s}")
        sew(m, f"jhem_{s}", "top", f"jf_{s}", "hem")
        sew(m, f"jhem_{s}", "side", "jhem_b", f"side_{s}")
        sew(m, "TEMP_stay", s, f"jf_{s}", "cf")
    sew(m, "jhem_b", "top", "jb", "hem")
    m.set_material(garmentId=garments["Jacket"], name="Navy canvas", texture=tex("canvas_navy"), tileCm=24, roughness=0.86)
    sim(m, "Jacket", 130)

    # Sleeves round the measured arms, sewn to both armholes; cuffs sewn to the sleeve ends.
    for s in "lr":
        limb = "left_arm" if s == "l" else "right_arm"
        r = m.add_tube(name=f"sl_{s}", limb=limb, endCircCm=44, lengthCm=22, capCm=3,
                       sewStartTo=[{"pieceId": ids[f"jf_{s}"], "edge": "arm"}, {"pieceId": ids["jb"], "edge": f"arm_{s}"}])
        ids[f"sl_{s}"] = r["pieceId"]
        m.set_fabric(pieceId=ids[f"sl_{s}"], shrinkWarp=0.92, bend=56)
        r = m.add_tube(name=f"cuff_{s}", limb=limb, offsetCm=18.5, startCircCm=44, endCircCm=43.5, lengthCm=7.5,
                       sewStartTo=[{"pieceId": ids[f"sl_{s}"], "edge": "end"}])
        if r.get("fitWarning"):
            print("  !", r["fitWarning"])
        ids[f"cuff_{s}"] = r["pieceId"]
        m.set_fabric(pieceId=ids[f"cuff_{s}"], bend=94, stretch=95, friction=99, shrinkWeft=0.97, thickness=2.8)
    sim(m, "Jacket", 150)
    # Push the sleeves up: the cuffs are held where they settled and only the sleeve cloth grows, so it has to bunch.
    for s in "lr":
        m.pin_piece(pieceId=ids[f"cuff_{s}"])
        m.set_fabric(pieceId=ids[f"sl_{s}"], shrinkWarp=1.32, shrinkWeft=1.06, bend=70)
    sim(m, "Jacket", 130)
    for s in "lr":
        m.pin_piece(pieceId=ids[f"cuff_{s}"], pinned=False)
    m.delete_piece(pieceId=ids.pop("TEMP_stay"))


def stage_trim(m):
    for s, sx in (("l", 1), ("r", -1)):
        host = f"jf_{s}"
        patch(m, f"chest_pocket_{s}", host, kind="pocket", x=11.5 if s == "l" else -24.5, y=123, widthCm=13, heightCm=15.5,
              easePercent=5, simulate=False)
        zip_pts = [(1.0, 103.5), (4.6, 103.5), (4.6, 145.5), (1.0, 145.5)] if s == "l" else [(-4.6, 103.5), (-1.0, 103.5), (-1.0, 145.5), (-4.6, 145.5)]
        patch(m, f"zip_{s}", host, points=[{"x": x, "y": y} for x, y in zip_pts], stitchEdges=[1 if s == "l" else 3],
              easePercent=0, bend=92, simulate=False)
        m.set_material(pieceId=ids[f"zip_{s}"], name=f"Zip facing {s}", texture=tex("zip_facing_mirror" if s == "l" else "zip_facing"), repeat=0, roughness=0.9)
        leaf = [(11.6, 160.6), (6.8, 153.7), (4.6, 146.2), (13.0, 140.5), (21.0, 153.5)] if s == "l" else \
               [(-11.6, 160.6), (-21.0, 153.5), (-13.0, 140.5), (-4.6, 146.2), (-6.8, 153.7)]
        patch(m, f"collar_{s}", host, points=[{"x": x, "y": y} for x, y in leaf], stitchEdges=[0, 1] if s == "l" else [3, 4],
              easePercent=0, bend=88, simulate=False)
    patch(m, "name_patch", "jf_l", kind="patch", x=12.5, y=142.5, widthCm=10, heightCm=4, easePercent=0, simulate=False)
    m.set_material(pieceId=ids["name_patch"], name="Name patch", texture=tex("patch_tan"), tileCm=10, roughness=0.85)
    sim(m, "Jacket trim", 70)
    patch(m, "chest_flap_r", "jf_r", kind="flap", x=-25.2, y=135, widthCm=14.4, heightCm=6.5, simulate=False)
    patch(m, "pocket_band_l", "jf_l", kind="patch", level=2, x=11.2, y=136, widthCm=13.6, heightCm=3.5, easePercent=0, bend=90, simulate=False)
    sim(m, "Jacket trim 2", 60)


def stage_hood(m):
    fleece = dict(density=380, stretch=70, shear=60, bend=78, damping=45, friction=70, thickness=3.0)
    garment(m, "Hood", pd=0.9, layer=7, offset=11.0, thick=4.0)
    seg = [7.8, 8.4, 10.8, 10.8, 8.4, 7.8]
    rise = [3.6, 6.5, 10.5, 12.0, 10.5, 6.5, 3.6]
    names = ["b_cl", "b_fl", "b_bl", "b_br", "b_fr", "b_cr"]
    xs = [0.0]
    for L in seg:
        xs.append(xs[-1] + L * 1.15)
    W = xs[-1]
    bottom = [(x - W / 2, 0, n) for x, n in zip(xs, names + ["end_r"])]
    top = [((x - W / 2) * 1.10, r) for x, r in zip(xs, rise)][::-1]
    piece(m, "cowl", pts(*bottom, *top), curv=250, fabric=fleece, pos3d=[0, 1.64, -0.17], rot3d=[8, 180, 0])
    for edge, host, a, b in (("b_cl", "jf_l", (4.6, 146.2), (6.8, 153.7)), ("b_fl", "jf_l", (6.8, 153.7), (11.6, 160.6)),
                             ("b_bl", "jb", (-10.6, 160.2), (0, 158.5)), ("b_br", "jb", (0, 158.5), (10.6, 160.2)),
                             ("b_fr", "jf_r", (-11.6, 160.6), (-6.8, 153.7)), ("b_cr", "jf_r", (-6.8, 153.7), (-4.6, 146.2))):
        m.sew_surface_stitch(sourcePieceId=ids["cowl"], sourceEdge=edge, targetPieceId=ids[host], x1=a[0], y1=a[1], x2=b[0], y2=b[1])
    m.set_material(garmentId=garments["Hood"], name="Hoodie fleece", texture=tex("fleece_brown"), tileCm=20, roughness=0.95)
    sim(m, "Hood", 90)
    # The hood itself: a bag cut a third larger than the patch of back it is sewn round, so it bunches like a folded hood.
    bag = [(-15, 154.5), (-13.5, 139), (-7, 131.5), (7, 131.5), (13.5, 139), (15, 154.5), (8.5, 157), (0, 155.6), (-8.5, 157)]
    patch(m, "hood", "jb", kind="patch", points=[{"x": x, "y": y} for x, y in bag], easePercent=30, level=3, garmentName="Hood bag",
          bend=45, simulate=False)
    m.set_fabric(pieceId=ids["hood"], **{**fleece, "bend": 45, "thickness": 2.4})
    m.set_material(pieceId=ids["hood"], name="Hoodie fleece", texture=tex("fleece_brown"), tileCm=20, roughness=0.95)
    sim(m, "Hood bag", 120)


def stage_accessories(m):
    garment(m, "Shop rag", pd=1.0, layer=8, offset=9.5, thick=1.6)
    rag = pts((17, 68, "hem1"), (30, 64, "hem2"), (41, 70, "side_b"), (42, 99.5, "top_b"), (27, 99.5, "tuck"), (21, 99.5, "top_f"), (15, 97, "side_f"))
    piece(m, "rag", rag, curv=30, pos3d=[0.30, 0.86, 0.14], rot3d=[0, 55, 0],
          fabric=dict(density=160, stretch=55, shear=30, bend=6, damping=30, friction=60, thickness=1.6))
    m.sew_surface_stitch(sourcePieceId=ids["rag"], sourceEdge="tuck", targetPieceId=ids["pf_l"], x1=27, y1=98.5, x2=21, y2=98.5)
    m.set_material(garmentId=garments["Shop rag"], name="Shop rag", texture=tex("rag_red"), tileCm=14, roughness=0.92)
    sim(m, "Shop rag", 110)

    hide = dict(density=750, stretch=97, shear=92, bend=93, damping=45, friction=65, thickness=2.4)
    plate = [(-28, 75), (-25, 72), (-17, 72), (-14, 75), (-14, 97), (-28, 97)]
    patch(m, "holster_plate", "pf_r", points=[{"x": x, "y": y} for x, y in plate], stitchEdges=[4], easePercent=0, level=3,
          garmentName="Holster", simulate=False)
    m.set_fabric(pieceId=ids["holster_plate"], **hide)
    m.set_material(pieceId=ids["holster_plate"], name="Holster leather", texture=tex("leather_brown"), tileCm=18, roughness=0.6)
    sim(m, "Holster", 90)
    for name, x, y, h in (("pouch_a", -27.2, 75, 13.5), ("pouch_b", -20.4, 74, 13.0)):
        patch(m, name, "holster_plate", kind="pocket", x=x, y=y, widthCm=5.8, heightCm=h, easePercent=32, level=2,
              garmentName="Holster pouches", simulate=False)
        m.set_fabric(pieceId=ids[name], **hide)
    sim(m, "Holster pouches", 140)

    # Hard parts: placed on the draped surface and pinned.
    steel = dict(color="#9aa0a6", metallic=0.78, roughness=0.26)
    m.add_hardware(hostPieceId=ids["holster_plate"], x=-17.5, y=78, shape="wrench", heightCm=21, widthCm=4.8, name="wrench",
                   garmentName="Tools", thicknessMm=4, liftMm=17, spinDegrees=-6, **steel)
    for name, x, y, col, spin in (("driver_yellow", -25.6, 83.0, "#c8891f", 5), ("driver_red", -22.8, 82.0, "#a8322b", -8)):
        m.add_hardware(hostPieceId=ids["holster_plate"], x=x, y=y, shape="handle", widthCm=2.4, heightCm=9.5, name=name,
                       garmentName="Tools", thicknessMm=4, liftMm=9, spinDegrees=spin, color=col, metallic=0.0, roughness=0.4)
    for i, (x, col, metal) in enumerate(((15.0, "#b08a3c", 0.9), (17.6, "#3a3a3d", 0.1), (20.0, "#b08a3c", 0.9))):
        m.add_hardware(hostPieceId=ids["jf_l"], x=x, y=135.5, shape="pen", widthCm=0.75, heightCm=6.5, name=f"pen_{i+1}",
                       garmentName="Tools", liftMm=7, color=col, metallic=metal, roughness=0.3)
    snaps = [("jf_r", -6.6, y) for y in (108, 117, 126, 135, 144)] + [("chest_flap_r", -18, 136.6), ("chest_pocket_l", 18, 127),
             ("cargo_flap_l", 0, 1.6), ("cargo_flap_r", 0, 1.6)]
    for i, (host, x, y) in enumerate(snaps):
        m.add_hardware(hostPieceId=ids[host], x=x, y=y, shape="snap", widthCm=1.7, name=f"snap_{i+1}")


def stage_finish(m):
    m.save_project(path=str(OUT / "Gus-mechanic.garment.json"))
    for ext in ("glb", "fbx"):
        m.export_garment(path=str(OUT / f"Gus-mechanic.{ext}"), weldSeams=True)
    m.render_preview(svgPath=str(OUT / "Gus-mechanic-pattern.svg"))
    fit = m.inspect_fit()
    print("  fit:", {k: fit[k] for k in ("garmentCount", "vertexCount", "triangleCount", "penetratedVertices", "maxPenetrationMm")})
    print("  open seams:", [(o["pieceA"], o["pieceB"], o["gapMm"]) for o in fit["openSeams"]])
    print("  server:", m.get_project_summary()["server"])


STAGES = {"pants": stage_pants, "tee": stage_tee, "jacket": stage_jacket, "trim": stage_trim, "hood": stage_hood,
          "accessories": stage_accessories, "finish": stage_finish}


def main(names):
    order = list(STAGES)
    names = names or order
    m = Mcp(log=str(WORK / "mcp.log"))
    try:
        for name in names:
            i = order.index(name)
            if i > 0 and not ids:
                m.open_project(path=str(WORK / f"{order[i-1]}.garment.json"))
                st = json.loads((WORK / f"{order[i-1]}.ids.json").read_text())
                ids.update(st["ids"]); garments.update(st["garments"])
            t = time.time()
            print(f"== {name}")
            STAGES[name](m)
            m.save_project(path=str(WORK / f"{name}.garment.json"))
            (WORK / f"{name}.ids.json").write_text(json.dumps({"ids": ids, "garments": garments}))
            m.render_preview(pngPath=str(WORK / f"{name}.png"), width=1500, height=820, views="front,quarter,back")
            print(f"   {time.time()-t:.1f}s")
    finally:
        m.close()


if __name__ == "__main__":
    main(sys.argv[1:])
