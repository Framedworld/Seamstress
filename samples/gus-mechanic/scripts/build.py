"""Gus mechanic outfit, built to match the concept art (samples/gus-service-jacket/Gus-concept.png).

Everything is made with the Seamstress MCP: the garments come from the fitted templates (cut to Gus's own
measurements), details from the block tools. Gus stands in the concept's A-pose.

  python build.py [stage ...]     stages: pants tee hoodie jacket trim hood accessories finish   (default: all)

Each stage saves a project under .tools/gus-outfit/work3 and the next one opens it, so a stage can be re-run alone.
"""
import sys, json, math, pathlib, time
sys.path.insert(0, str(pathlib.Path(__file__).parent))
from mcp import Mcp, ROOT
import paint

OUT = ROOT / "samples/gus-mechanic"
TEX = OUT / "textures"
WORK = ROOT / ".tools/gus-outfit/work3"
BODY = ROOT / "samples/gus_v2_poses/gus_15.obj"       # Gus in the concept's A-pose
WORK.mkdir(parents=True, exist_ok=True)

ids = {}          # piece name -> id
garments = {}     # garment name -> id
points = {}       # piece name -> {edge name: (x, y)}  the point each named edge starts at, in the piece's pattern cm


def tex(n):
    return str(TEX / f"{n}.png")


def remember(g):
    garments[g["name"]] = g["garmentId"]
    for p in g["pieces"]:
        ids[p["name"]] = p["id"]
        points[p["name"]] = {q["edge"]: (q["x"], q["y"]) for q in p["points"] if q.get("edge")}


def template(m, name, key, **kw):
    r = m.add_template_garment(template=name, **kw)
    for g in r["garments"]:
        remember(g)
    garments[key] = r["garmentId"]
    return r


def report(name, r):
    opened = r.get("openSeams") or []
    print(f"  sim {name}: {r['frames']}f {r['elapsedMs']:.0f}ms gap={r['seamGapMetres']*1000:.1f}mm speed={r['peakSpeedMetresPerSec']} open={len(opened)}")
    for o in opened[:4]:
        print(f"     OPEN {o['kind']} {o['pieceA']}[{o['edgeA']}] -> {o['pieceB']}: {o['gapMm']}mm  {o['hint'][:70]}")


def sim(m, key, frames=120, self_collision=False):
    r = m.simulate(garmentId=garments[key], frames=frames, selfCollision=self_collision)
    report(key, r)
    return r


def patch(m, name, host, **kw):
    kw.setdefault("simulate", False)
    r = m.add_patch(hostPieceId=ids[host], name=name, **kw)
    ids[name] = r["pieces"][0]["pieceId"]
    garments[r["garment"]] = r["garmentId"]
    return r


def poly(*pts):
    return [{"x": x, "y": y} for x, y in pts]


def stay(m, garment_key, left, right, gap, y0, y1, depth):
    """A strip sewn across an open front while it drapes, so the two edges hang parallel. Removed afterwards."""
    h = gap / 2
    r = m.add_piece(garmentId=garments[garment_key], name=f"TEMP stay {garment_key}", drawnOn="front", depthCm=depth,
                    points=[{"x": -h, "y": y0, "edge": "bottom"}, {"x": h, "y": y0, "edge": "l"}, {"x": h, "y": y1, "edge": "top"}, {"x": -h, "y": y1, "edge": "r"}],
                    fabric=dict(bend=80, stretch=95, thickness=2.4))
    m.sew_edges(pieceAId=r["pieceId"], edgeA="l", pieceBId=ids[left], edgeB="cf")
    m.sew_edges(pieceAId=r["pieceId"], edgeA="r", pieceBId=ids[right], edgeB="cf")
    return r["pieceId"]


# ------------------------------------------------------------------------------------------------------------------
def stage_pants(m):
    m.new_project(name="Gus mechanic")
    r = m.load_avatar(path=str(BODY))
    tape = r["landmarks"]["tape"]
    (WORK / "tape.json").write_text(json.dumps(tape))
    print("  arms droop", tape["arms"][0]["droopDegrees"], "| crotch", tape["crotchHeightCm"], "| hips", tape["torso"][0]["circumferenceCm"])

    # Fitted heavy workwear trousers: snug through seat/thighs (easePercent=1, NOT loose!), extraLengthCm=8 for broad shin stacks.
    template(m, "Fitted Trousers", "pants", particleDistance=0.85, extraLengthCm=8, easePercent=1,
             outseamBackCm=6)          # side seam on the side of the leg, so the cargo pockets and holster sit there
    # Soft pre-drape (bend=58, modest shrinkWarp=1.05 so crotch stays high & fitted, not drop-crotch!)
    for n in ("Front L", "Front R", "Back L", "Back R"):
        m.set_fabric(pieceId=ids[n], shrinkWarp=1.05, bend=58, stretch=97, shear=86, thickness=3.4, density=580, friction=96)
    m.set_material(garmentId=garments["pants"], name="Charcoal canvas", texture=tex("canvas_charcoal"), tileCm=24, roughness=0.9)
    sim(m, "pants", 160)
    shape_pants(m)

    # Named points are where each edge starts on the left piece; the right piece is its mirror image (x negated).
    p = points["Front L"]
    knee_out, knee_in, hip = p["out_hi"], p["in_lo"], p["out_hip"]
    knee_x = (knee_in[0] + knee_out[0]) / 2                                   # middle of the leg at the knee
    out_at = lambda y: knee_out[0] + (hip[0] - knee_out[0]) * (y - knee_out[1]) / (hip[1] - knee_out[1])   # outseam x at height y
    for s, sx, front in (("l", 1, "Front L"), ("r", -1, "Front R")):
        # double-front knee panel: crisp heavy-canvas reinforcement over the kneecap/upper shin
        patch(m, f"knee_{s}", front, kind="patch", x=sx * knee_x - 8, y=knee_out[1] - 13, widthCm=16, heightCm=21, easePercent=2, bend=84)
        # 3D bellows cargo pocket on the outer mid-thigh (easePercent=9 -> clean stable 3D box with selfCollision=True!)
        y0 = knee_out[1] + 6.5
        w, h = 16.5, 20.0
        xo = out_at(y0) - 0.5
        x = xo - w if s == "l" else -xo
        patch(m, f"cargo_{s}", front, kind="patch", x=x, y=y0, widthCm=w, heightCm=h, easePercent=9, bend=88)
        # crisp overhanging cargo flap (level 2, over the bellows bulge)
        patch(m, f"cargo_flap_{s}", front, kind="patch", level=2, x=x - 0.8, y=y0 + h - 5.5, widthCm=w + 1.6, heightCm=7.2, easePercent=6, bend=94)
    garments["pants trim"] = garments[next(k for k in garments if k.endswith("trim"))]
    garments["pants flaps"] = garments[next(k for k in garments if k.endswith("trim 2"))]
    sim(m, "pants trim", 90, self_collision=True)      # against the trousers under it, not just the body: folds poke through otherwise
    sim(m, "pants flaps", 80, self_collision=True)


def shape_pants(m):
    """Worn-in heavy duck canvas shaped by multi-zone cloth pulling:
    1) Radiating diagonal crotch/upper-thigh whiskers
    2) Crisp knee break right above the knee panel
    3) Broad alternating 3-tier S-fold stacks climbing up the lower shin (not a crumpled ankle ring)
    Locked with set_creases(88%), unpinned, then stiffened to 14 oz canvas (bend=90)."""
    p = points["Front L"]
    knee_x = (p["in_lo"][0] + p["out_hi"][0]) / 2
    knee_in_x = p["in_lo"][0]
    knee_y = p["out_hi"][1]
    crotch_y = p["crotch"][1]
    for sx, front, back in ((1, "Front L", "Back L"), (-1, "Front R", "Back R")):
        # 1. Broad lower-shin alternating S-stacks pushed toward the boot tops
        m.pull_cloth(pieceId=ids[front], x=sx * (knee_x + 1.5), y=knee_y - 14, radiusCm=11, offsetCm=[sx * 1.6, -7.8, 2.6], steps=6, framesPerStep=12, settleFrames=0, hold=True)
        m.pull_cloth(pieceId=ids[front], x=sx * (knee_x - 1.5), y=knee_y - 25, radiusCm=9,  offsetCm=[-sx * 1.5, -5.5, 2.6], steps=5, framesPerStep=12, settleFrames=0, hold=True)
        # 2. Strong heavy-canvas break right across/above the knee
        m.pull_cloth(pieceId=ids[front], x=sx * knee_x, y=knee_y + 3, radiusCm=10, offsetCm=[sx * 1.8, -6.0, 2.0], steps=5, framesPerStep=12, settleFrames=0, hold=True)
        # 3. Diagonal radiating crotch/upper-thigh tension whiskers (inner thigh gently up/in, outer thigh slightly down)
        m.pull_cloth(pieceId=ids[front], x=sx * (knee_in_x + 4.5), y=crotch_y - 6, radiusCm=8, offsetCm=[-sx * 2.2, 2.8, 1.0], steps=4, framesPerStep=10, settleFrames=0, hold=True)
        # 4. Back knee/calf counter-stack (back pieces run the other way: pattern x = -body x)
        m.pull_cloth(pieceId=ids[back], x=-sx * knee_x, y=knee_y - 6,  radiusCm=10, offsetCm=[0, -6.0, -1.8], steps=6, framesPerStep=12, settleFrames=0, hold=True)
        m.pull_cloth(pieceId=ids[back], x=-sx * knee_x, y=knee_y - 20, radiusCm=9,  offsetCm=[0, -4.5, -1.5], steps=5, framesPerStep=12, settleFrames=0, hold=True)
    r = m.set_creases(garmentId=garments["pants"], strengthPercent=88)
    print(f"  creases set in {r['creasedPieces']} pieces")
    for n in ("Front L", "Front R", "Back L", "Back R"):
        m.pin_piece(pieceId=ids[n], pinned=False)
        m.set_fabric(pieceId=ids[n], bend=90, stretch=98, shear=90, shrinkWarp=1.05, thickness=3.4)
    sim(m, "pants", 70)


def stage_tee(m):
    template(m, "Fitted Tee", "tee", sleeves="none")
    m.set_material(garmentId=garments["tee"], name="Black jersey", texture=tex("jersey_black"), tileCm=16, roughness=0.93)
    sim(m, "tee", 130)


def stage_hoodie(m):
    r = template(m, "Fitted Hoodie", "hoodie", sleeves="none", hood=False, openFront=True, frontGapCm=8, easePercent=-4, extraLengthCm=-4)
    m.set_fabric(garmentId=r["garmentId"], bend=62)          # close and smooth: nothing to fold up under the jacket
    g = r["garments"][0]
    front_l = next(p["name"] for p in g["pieces"] if p["name"].startswith("Front L"))
    front_r = next(p["name"] for p in g["pieces"] if p["name"].startswith("Front R"))
    ids["hoodie_fl"], ids["hoodie_fr"] = ids[front_l], ids[front_r]
    points["hoodie_fl"], points["hoodie_fr"] = points[front_l], points[front_r]
    p = points["hoodie_fl"]
    tmp = stay(m, "hoodie", "hoodie_fl", "hoodie_fr", 8, p["hem"][1], p["cf"][1], 30)
    m.set_material(garmentId=garments["hoodie"], name="Hoodie fleece", texture=tex("fleece_charcoal"), tileCm=20, roughness=0.96)
    m.set_material(pieceId=ids["hoodie_fl"], name="Hoodie front L", texture=tex("hoodie_front"), repeat=0, roughness=0.95)
    m.set_material(pieceId=ids["hoodie_fr"], name="Hoodie front R", texture=tex("hoodie_front_mirror"), repeat=0, roughness=0.95)
    sim(m, "hoodie", 140)
    m.delete_piece(pieceId=tmp)


def stage_jacket(m):
    r = template(m, "Fitted Jacket", "jacket", sleeves="none", openFront=True, frontGapCm=12, particleDistance=0.85, extraLengthCm=5.5)
    g = r["garments"][0]
    names = {key: next(p["name"] for p in g["pieces"] if p["name"].startswith(prefix)) for key, prefix in
             (("jb", "Back"), ("jf_l", "Front L"), ("jf_r", "Front R"), ("jhem_b", "Back hem"), ("jhem_l", "Front hem L"), ("jhem_r", "Front hem R"))}
    names["jb"] = next(p["name"] for p in g["pieces"] if p["name"].startswith("Back") and "hem" not in p["name"])
    for key, n in names.items():
        ids[key], points[key] = ids[n], points[n]
    # Soft pre-drape for torso folds before gentle pulling & setting creases, then stiffen to heavy 14 oz duck canvas (bend=89)!
    for key in ("jb", "jf_l", "jf_r"):
        m.set_fabric(pieceId=ids[key], bend=68, stretch=97, shear=88, thickness=3.4, density=560, shrinkWarp=1.03)
    for key in ("jhem_b", "jhem_l", "jhem_r"):
        m.set_fabric(pieceId=ids[key], bend=95, shrinkWeft=0.92, friction=92, thickness=3.6)   # stiff cinched band sitting at the beltline
    p = points["jf_l"]
    tmp = stay(m, "jacket", "jf_l", "jf_r", 12, p["hem"][1], p["cf"][1], 33)
    m.set_material(garmentId=garments["jacket"], name="Navy canvas", texture=tex("canvas_navy"), tileCm=24, roughness=0.88)
    m.set_garment(garmentId=garments["jacket"], bodyOffsetMm=g["bodyOffsetMm"] + 2.5)     # clear of the hoodie's folds at the armholes
    sim(m, "jacket", 140)

    # Sleeves: 46cm circumference clears 34.2cm arm + 11.3cm layer room (open=0!), then cuff grips snugly at shrinkWeft=0.91!
    for s in "lr":
        limb = "left_arm" if s == "l" else "right_arm"
        r = m.add_tube(name=f"sl_{s}", limb=limb, endCircCm=46, lengthCm=22, capCm=3,
                       sewStartTo=[{"pieceId": ids[f"jf_{s}"], "edge": "arm"}, {"pieceId": ids["jb"], "edge": f"arm_{s}"}])
        ids[f"sl_{s}"] = r["pieceId"]
        m.set_fabric(pieceId=ids[f"sl_{s}"], shrinkWarp=0.92, bend=74, stretch=97, thickness=3.4)
        r = m.add_tube(name=f"cuff_{s}", limb=limb, offsetCm=18.5, startCircCm=46, endCircCm=46, lengthCm=7.5,
                       sewStartTo=[{"pieceId": ids[f"sl_{s}"], "edge": "end"}])
        if r.get("fitWarning"):
            print("  !", r["fitWarning"])
        ids[f"cuff_{s}"] = r["pieceId"]
        m.set_fabric(pieceId=ids[f"cuff_{s}"], bend=97, stretch=96, friction=99, shrinkWeft=0.91, thickness=4.0)
    sim(m, "jacket", 150)

    # Pin cuffs, expand sleeve length for broad stiff rolls, AND shape both sleeves + torso fronts with pull_cloth + set_creases!
    for s in "lr":
        m.pin_piece(pieceId=ids[f"cuff_{s}"])
        m.set_fabric(pieceId=ids[f"sl_{s}"], shrinkWarp=1.15, shrinkWeft=1.04, bend=84, stretch=98, shear=86, thickness=3.6)
    sim(m, "jacket", 130)

    # Shape big stiff diagonal rolls into both sleeves by pushing mid-sleeve cloth down toward the pinned cuff
    for s, sx in (("l", 1), ("r", -1)):
        m.pull_cloth(pieceId=ids[f"sl_{s}"], x=23, y=12, radiusCm=10, offsetCm=[sx * 2.0, -3.5, 1.2], steps=5, framesPerStep=12, settleFrames=0, hold=True)

    # Gentle broad diagonal tension pull on jacket fronts (clean sweeping heavy-canvas creases, ZERO rolled lip over waistband!)
    p = points["jf_l"]
    cf, hem_y, under_y = abs(p["hem"][0]), p["hem"][1], p["arm"][1]
    for key, sx in (("jf_l", 1), ("jf_r", -1)):
        m.pull_cloth(pieceId=ids[key], x=sx * (cf + 9), y=hem_y + 11, radiusCm=13, offsetCm=[-sx * 1.8, -2.2, 0.8], steps=4, framesPerStep=12, settleFrames=0, hold=True)
    # Subtle back fullness above back waistband
    m.pull_cloth(pieceId=ids["jb"], x=0, y=hem_y + 11, radiusCm=16, offsetCm=[0, -1.8, -0.6], steps=4, framesPerStep=12, settleFrames=0, hold=True)

    r_cr = m.set_creases(garmentId=garments["jacket"], strengthPercent=88)
    print(f"  jacket creases set in {r_cr['creasedPieces']} pieces")
    for k in ("jb", "jf_l", "jf_r", "sl_l", "sl_r", "cuff_l", "cuff_r"):
        m.pin_piece(pieceId=ids[k], pinned=False)
    for key in ("jb", "jf_l", "jf_r"):
        m.set_fabric(pieceId=ids[key], bend=89, stretch=98, shear=90, thickness=3.4, density=560)
    for s in "lr":
        m.set_fabric(pieceId=ids[f"sl_{s}"], bend=93, stretch=98, shear=90, thickness=3.6)
    sim(m, "jacket", 60)
    m.delete_piece(pieceId=tmp)


def stage_trim(m):
    p = points["jf_l"]          # the right front is the mirror image of the left
    cf, hem_y, under_y = abs(p["hem"][0]), p["hem"][1], p["arm"][1]
    neck, cf_top = p["neck"], p["cf"]
    for s, sx in (("l", 1), ("r", -1)):
        host = f"jf_{s}"
        top = under_y + 9.5
        px = cf + 4.5
        x = px if s == "l" else -(px + 13.0)
        patch(m, f"chest_pocket_{s}", host, kind="patch", level=2, x=x, y=top - 14.5, widthCm=12.8, heightCm=14.5, easePercent=4, bend=92)
        # diagonal welt of the hand pocket
        w = [(cf + 5.0, hem_y + 7.5), (cf + 7.2, hem_y + 6.3), (cf + 14.2, hem_y + 18.3), (cf + 12.0, hem_y + 19.5)]
        if s == "r":
            w = [(-x_, y_) for x_, y_ in reversed(w)]
        patch(m, f"welt_{s}", host, kind="patch", points=poly(*w), easePercent=0, bend=92)
        # Crisp wide chore-jacket spread collar leaf (broad flat workwear lapel, NOT a pointy hanging triangle!)
        mid = (0.5 * abs(neck[0]) + 0.5 * cf, 0.5 * neck[1] + 0.5 * cf_top[1])
        leaf = [
            (abs(neck[0]) + 0.4, neck[1] - 0.6),
            (mid[0] + 0.4, mid[1] - 0.4),
            (cf + 0.5, cf_top[1] - 0.2),
            (cf + 4.8, cf_top[1] - 4.4),
            (abs(neck[0]) + 6.4, neck[1] - 5.0),
        ]
        if s == "l":
            patch(m, f"collar_{s}", host, points=poly(*leaf), level=2, easePercent=0, bend=94)
        else:
            mirrored = [(-leaf[0][0], leaf[0][1])] + [(-x_, y_) for x_, y_ in reversed(leaf[1:])]
            patch(m, f"collar_{s}", host, points=poly(*mirrored), level=2, easePercent=0, bend=94)
    p = points["jf_l"]
    cf, under_y = abs(p["hem"][0]), p["arm"][1]
    top = under_y + 9.5
    # turned against the slope of the chest so it reads level from the front
    c, sn = math.cos(math.radians(11)), math.sin(math.radians(11))
    mx, my = cf + 11.5, top + 5.75
    corners = [(mx + dx * c - dy * sn, my + dx * sn + dy * c) for dx, dy in ((-4.5, -1.75), (4.5, -1.75), (4.5, 1.75), (-4.5, 1.75))]
    # Welts lie on the jacket (level 1); pockets, collar points and the name patch one layer out (level 2), clear of
    # the jacket's own folds, which would otherwise poke through them; the flap over its pocket (level 3).
    garments["jacket trim"] = garments[next(k for k in garments if "Jacket" in k and k.endswith("trim"))]
    garments["jacket pockets"] = garments[next(k for k in garments if "Jacket" in k and k.endswith("trim 2"))]
    sim(m, "jacket trim", 60)
    sim(m, "jacket pockets", 80)
    patch(m, "name_patch", "jf_l", points=poly(*corners), level=3, easePercent=0, bend=96)
    patch(m, "chest_flap_r", "jf_r", kind="patch", level=3, x=-(cf + 5.0 + 13.0 + 0.6), y=top - 5.0, widthCm=14.2, heightCm=5.6, easePercent=2, bend=94)
    garments["jacket flaps"] = garments[next(k for k in garments if "Jacket" in k and k.endswith("trim 3"))]
    sim(m, "jacket flaps", 70)


def stage_hood(m):
    fleece = dict(density=420, stretch=65, shear=50, damping=45, friction=80, thickness=3.2)
    # The hood pushed down: a thick fleece roll lying round the neck, over the jacket collar, running into the fronts...
    r = m.add_collar(garmentId=garments["hoodie"], heightCm=24, easePercent=14, frontPercent=75, flarePercent=0, rollCm=4.5, simulate=False)
    garments["cowl"] = r["garmentId"]
    ids["cowl"] = r["pieces"][0]["pieceId"]
    m.set_fabric(pieceId=ids["cowl"], bend=88, **{**fleece, "thickness": 4.0})
    m.set_material(garmentId=garments["cowl"], name="Hoodie fleece", texture=tex("fleece_charcoal"), tileCm=20, roughness=0.96)
    sim(m, "cowl", 130)
    # ...and the hood itself lying down the back.
    r = m.add_hood(garmentId=garments["hoodie"], lengthCm=30, widthCm=24, easePercent=15, frontPercent=20, simulate=False)
    garments["hood"] = r["garmentId"]
    for p in r["pieces"]:
        m.set_fabric(pieceId=p["pieceId"], bend=46, **fleece)
    m.set_material(garmentId=garments["hood"], name="Hoodie fleece", texture=tex("fleece_charcoal"), tileCm=20, roughness=0.96)
    sim(m, "hood", 170)


def stage_accessories(m):
    pl = points["Front L"]
    waist_y = pl["waist"][1]
    wx = pl["waist"][0]
    brass = dict(color="#7a5c2e", metallic=0.88, roughness=0.42)
    bronze = dict(color="#3a2e21", metallic=0.7, roughness=0.55)

    # Webbing straps hanging from the waist: two at the right hip carry the holster, one at the left holds the rag.
    web = dict(density=600, stretch=98, shear=92, bend=90, damping=40, friction=90, thickness=2.0)
    straps = [("strap_rag", "Front L", wx - 16.5), ("strap_h1", "Front R", -(wx - 8.0)), ("strap_h2", "Front R", -(wx - 17.5))]
    for name, host, xc in straps:
        patch(m, name, host, kind="patch", level=5, x=xc - 1.6, y=waist_y - 14.0, widthCm=3.2, heightCm=13.6, easePercent=0, bend=92,
              garmentName="Straps")
        m.set_fabric(pieceId=ids[name], **web)
    garments["straps"] = garments["Straps"]

    # The rag: a short, soft square bunched into a tuck under its strap.
    g = m.create_garment(name="Shop rag", particleDistance=0.8, layer=20, bodyOffsetMm=10.5, thicknessMm=1.6)
    garments["rag"] = g["garmentId"]
    at = m.locate_on_piece(pieceId=ids["Front L"], xCm=wx - 16.5, yCm=waist_y - 14)["positionMetres"]
    rag = [{"x": 0, "y": 0, "edge": "hem1"}, {"x": 11, "y": -3, "edge": "hem2"}, {"x": 21, "y": 1, "edge": "side_b"},
           {"x": 23, "y": 21, "edge": "tuck"}, {"x": 1, "y": 21, "edge": "side_f"}]
    r = m.add_piece(garmentId=g["garmentId"], name="rag", points=rag, curvature=40, pos3d=[at[0] + 0.03, at[1] - 0.03, at[2] + 0.03], rot3d=[0, 40, 0],
                    fabric=dict(density=160, stretch=55, shear=30, bend=8, damping=30, friction=70, thickness=1.6))
    ids["rag"] = r["pieceId"]
    # 22 cm of hem gathered into 9 cm across the strap
    m.sew_surface_stitch(sourcePieceId=ids["rag"], sourceEdge="tuck", targetPieceId=ids["Front L"], x1=wx - 12.0, y1=waist_y - 8, x2=wx - 21.0, y2=waist_y - 8)
    m.set_material(garmentId=g["garmentId"], name="Shop rag", texture=tex("rag_red"), tileCm=14, roughness=0.94)
    sim(m, "rag", 220)        # left short it is still swinging, and hangs flat

    # Holster: a leather plate hung from the two right-hip straps, below the waist, with two deep pouches.
    hide = dict(density=750, stretch=97, shear=92, bend=93, damping=45, friction=65, thickness=2.6)
    x0, x1, top = -(wx - 5.5), -(wx - 20.5), waist_y - 12
    plate = [(x0, top - 21), (x0 + 3, top - 24), (x1 - 3, top - 24), (x1, top - 21), (x1, top), (x0, top)]
    patch(m, "holster_plate", "Front R", points=poly(*plate), stitchEdges=[4], easePercent=0, level=4, garmentName="Holster")
    m.set_fabric(pieceId=ids["holster_plate"], **hide)
    m.set_material(pieceId=ids["holster_plate"], name="Holster leather", texture=tex("leather_brown"), tileCm=18, roughness=0.62)
    garments["holster"] = garments["Holster"]
    sim(m, "holster", 100)
    for name, x, y, h in (("pouch_a", x0 + 0.6, top - 21.0, 14.0), ("pouch_b", x0 + 7.6, top - 21.4, 13.5)):
        patch(m, name, "holster_plate", kind="pocket", x=x, y=y, widthCm=6.4, heightCm=h, easePercent=48, level=2, garmentName="Holster pouches")
        m.set_fabric(pieceId=ids[name], **hide)
    garments["pouches"] = garments["Holster pouches"]
    sim(m, "pouches", 150)
    sim(m, "straps", 60)

    # Brass buckles on the straps, belt loops round the waistband, a button at the front.
    for name, host, xc in straps:
        m.add_hardware(hostPieceId=ids[host], x=xc, y=waist_y - 10.5, shape="buckle", widthCm=3.8, heightCm=2.6, name=f"buckle_{name}",
                       garmentName="Tools", thicknessMm=3, liftMm=5, **brass)
    for band in ("Waistband front L", "Waistband front R", "Waistband back L", "Waistband back R"):
        bp = points[band]                      # bottom and in start at the centre, out and top at the side (x signed as cut)
        for i, f in enumerate((0.42, 0.88) if "front" in band else (0.3, 0.75)):
            lo_y = bp["bottom"][1] + f * (bp["out"][1] - bp["bottom"][1])
            hi_y = bp["in"][1] + f * (bp["top"][1] - bp["in"][1])
            key = f"loop_{band[10:].replace(' ', '_')}_{i}"
            patch(m, key, band, kind="patch", level=2, x=f * bp["out"][0] - 0.65, y=lo_y + 0.35, widthCm=1.3, heightCm=hi_y - lo_y - 0.7,
                  easePercent=0, bend=92, garmentName="Belt loops")
    garments["loops"] = garments["Belt loops"]
    sim(m, "loops", 40)
    bp = points["Waistband front L"]
    m.add_hardware(hostPieceId=ids["Waistband front L"], x=1.6, y=bp["out"][1] + 2.2, shape="button", widthCm=1.5, name="fly_button",
                   garmentName="Tools", thicknessMm=3, liftMm=2, **bronze)

    steel = dict(color="#8e9399", metallic=0.82, roughness=0.3)
    m.add_hardware(hostPieceId=ids["holster_plate"], x=x0 + 10.5, y=top - 17, shape="wrench", heightCm=20, widthCm=4.8, name="wrench",
                   garmentName="Tools", thicknessMm=6, liftMm=18, spinDegrees=-6, **steel)
    m.add_hardware(hostPieceId=ids["holster_plate"], x=x0 + 8.6, y=top - 15.5, shape="wrench", heightCm=17, widthCm=4.2, name="wrench_2",
                   garmentName="Tools", thicknessMm=6, liftMm=12, spinDegrees=7, **steel)
    for name, dx, dy, col, spin in (("driver_yellow", 2.6, -12.5, "#9a6a1a", 5), ("driver_red", 5.4, -13.5, "#7c2a24", -8)):
        m.add_hardware(hostPieceId=ids["holster_plate"], x=x0 + dx, y=top + dy, shape="handle", widthCm=2.4, heightCm=9.5, name=name,
                       garmentName="Tools", thicknessMm=4, liftMm=10, spinDegrees=spin, color=col, metallic=0.0, roughness=0.4)
    p = points["jf_l"]
    cf, under_y = abs(p["hem"][0]), p["arm"][1]
    for i, (dx, col, metal) in enumerate(((8.0, "#a07c36", 0.9), (10.4, "#3a3a3d", 0.1), (12.6, "#a07c36", 0.9))):
        m.add_hardware(hostPieceId=ids["jf_l"], x=cf + dx, y=under_y + 6.0, shape="pen", widthCm=0.75, heightCm=6.5, name=f"pen_{i+1}",
                       garmentName="Tools", liftMm=7, color=col, metallic=metal, roughness=0.3)
    hem_y, cf_top = p["hem"][1], p["cf"][1]
    snaps = [("jf_r", -(cf + 2.6), hem_y + 4 + i * (cf_top - hem_y - 7) / 4) for i in range(5)]
    ko, hp = pl["out_hi"], pl["out_hip"]
    cy = ko[1] + 7.0
    cxm = ko[0] + (hp[0] - ko[0]) * (cy - ko[1]) / (hp[1] - ko[1]) - 0.5 - 17.0 / 2       # middle of the cargo flap, as in stage_pants
    snaps += [("chest_flap_r", -(cf + 5.0 + 6.5), under_y + 7.8),
              ("cargo_flap_l", cxm, cy + 21.0 - 5.5 + 2.4), ("cargo_flap_r", -cxm, cy + 21.0 - 5.5 + 2.4),
              ("jhem_r", -(cf + 2.6), hem_y - 2.5)]
    for i, (host, x, y) in enumerate(snaps):
        m.add_hardware(hostPieceId=ids[host], x=x, y=y, shape="snap", widthCm=1.2, name=f"snap_{i+1}", thicknessMm=3, **bronze)


def stage_paint(m):
    """One painted texture per piece: weave, topstitching and worn edges along its own outline, dust where it collects."""
    tmp = WORK / "prepaint.garment.json"
    m.save_project(path=str(tmp))
    project = json.loads(tmp.read_text(encoding="utf-8"))
    by_id = {p["id"]: p for g in project["garments"] for p in g["pieces"]}
    out = TEX / "painted"
    pl = points["Front L"]
    knee_y, hem_y = pl["out_hi"][1], pl["hem"][1]
    waist_y, crotch_y = pl["cf"][1], pl["crotch"][1]
    jhem = points["jf_l"]["hem"][1]
    legs = [(knee_y - 2, 10, 0.5), (knee_y + 20, 10, 0.4), (hem_y + 6, 7, 0.6)]
    under_jacket = [(waist_y + 2, 5, 0.35)]                  # the jacket hem's shadow on the trouser tops
    spec = {}
    # No topstitching up the centre front: a 15 cm fly, J-stitched on the left front.
    fly = [(3.6, waist_y - 0.5), (3.6, crotch_y + 3.5), (3.0, crotch_y + 1.4), (1.6, crotch_y + 0.4), (0.4, crotch_y)]
    spec["Front L"] = dict(weave="charcoal", dust=0.2, dust_bands=legs, shade_bands=under_jacket,
                           no_stitch=[(-0.5, crotch_y - 4, 2.2, waist_y + 2)], lines=[fly])
    spec["Front R"] = dict(weave="charcoal", dust=0.2, dust_bands=legs, shade_bands=under_jacket,
                           no_stitch=[(-2.2, crotch_y - 4, 0.5, waist_y + 2)])
    for n in ("Back L", "Back R"):
        spec[n] = dict(weave="charcoal", dust=0.2, dust_bands=[(hem_y + 6, 7, 0.5)])
    for n in ("Waistband front L", "Waistband front R", "Waistband back L", "Waistband back R"):
        spec[n] = dict(weave="charcoal", dust=0.25, stitch=(0.45,))
    for n in ("knee_l", "knee_r"):
        spec[n] = dict(weave="knee", dust=0.7, wash=0.25, dust_bands=[(knee_y - 3, 9, 0.8)], edge_dark=0.5, stitch=(0.45, 0.9), edge_dust=0.2)
    for n in ("cargo_l", "cargo_r"):
        spec[n] = dict(weave="charcoal", dust=0.55, edge_dark=0.6, shade_top=0.6, lift=0.13, stitch=(0.45, 0.9), edge_dust=0.5)
    for n in ("cargo_flap_l", "cargo_flap_r"):
        spec[n] = dict(weave="charcoal", dust=0.55, edge_dark=0.6, lift=0.18, stitch=(0.45, 0.9), edge_dust=0.5)
    for n in ("jb", "jf_l", "jf_r"):
        spec[n] = dict(weave="navy", dust=0.3, dust_bands=[(jhem + 9, 11, 0.55)], stitch=(0.55, 1.0))
    for n in ("jhem_b", "jhem_l", "jhem_r", "cuff_l", "cuff_r"):
        spec[n] = dict(weave="navy", dust=0.6, stitch=(0.45, 0.9), edge_dust=0.6)
    for n in ("sl_l", "sl_r"):
        spec[n] = dict(weave="navy", dust=0.45)
    for n in ("chest_pocket_l", "chest_pocket_r"):
        spec[n] = dict(weave="navy", dust=0.5, edge_dark=0.7, shade_top=0.45, lift=0.14, edge_dust=0.4)
    for n in ("chest_flap_r", "pocket_band_l", "collar_l", "collar_r"):
        spec[n] = dict(weave="navy", dust=0.45, edge_dark=0.65, lift=0.15, edge_dust=0.4)
    for n in ("welt_l", "welt_r"):
        spec[n] = dict(weave="navy", dust=0.3, edge_dark=0.6, stitch=(0.4,))
    for n in ("holster_plate", "pouch_a", "pouch_b"):
        spec[n] = dict(weave="leather", dust=0.6, wash=0.2, edge_dark=0.5, stitch=(0.45,), thread=(0.45, 0.38, 0.28))
    for n in ("strap_rag", "strap_h1", "strap_h2"):
        spec[n] = dict(weave="webbing", dust=0.5, edge_dark=0.35, stitch=(0.3,))
    spec["name_patch"] = dict(weave="patch", dust=0.03, wash=0.0, edge_dark=0.25, stitch=(0.3,), thread=(0.3, 0.3, 0.3))
    spec["rag"] = dict(weave="rag", dust=1.0, wash=0.6, edge_dark=0.2, stitch=())
    for n in [k for k in ids if k.startswith("loop_")]:
        spec[n] = dict(weave="charcoal", dust=0.4, edge_dark=0.4, stitch=())
    # Whole garments: the tee, the hoodie and its hood.
    zips = {ids["hoodie_fl"]: points["hoodie_fl"]["cf"][0], ids["hoodie_fr"]: points["hoodie_fr"]["cf"][0]}
    for key, weave in (("tee", "jersey"), ("hoodie", "fleece"), ("cowl", "fleece"), ("hood", "fleece")):
        g = next(g for g in project["garments"] if g["id"] == garments[key])
        for i, piece in enumerate(g["pieces"]):
            name = f"{key}_{i}"
            ids[name] = piece["id"]
            spec[name] = dict(weave=weave, dust=0.2, edge_dark=0.35, stitch=(0.5,), zip_x=zips.get(piece["id"]),
                              shade_bands=[(jhem + 6, 6, 0.25)] if key == "tee" else ())
    for i, (name, kw) in enumerate(spec.items()):
        if name not in ids or ids[name] not in by_id:
            print("  (no piece", name + ")")
            continue
        path = paint.paint(by_id[ids[name]], out, name.replace(" ", "_"), seed=100 + 7 * i, **kw)
        m.set_material(pieceId=ids[name], name=name, texture=path, repeat=0, roughness=0.6 if kw["weave"] == "leather" else 0.92)
    print("  painted", len(spec), "pieces")


def stage_finish(m):
    m.save_project(path=str(OUT / "Gus-mechanic.garment.json"))
    for ext in ("glb", "fbx"):
        m.export_garment(path=str(OUT / f"Gus-mechanic.{ext}"), weldSeams=True)
    m.render_preview(svgPath=str(OUT / "Gus-mechanic-pattern.svg"))
    fit = m.inspect_fit()
    print("  fit:", {k: fit[k] for k in ("garmentCount", "vertexCount", "triangleCount", "penetratedVertices", "maxPenetrationMm")})
    print("  open seams:", [(o["pieceA"], o["pieceB"], o["gapMm"]) for o in fit["openSeams"]])


STAGES = {"pants": stage_pants, "tee": stage_tee, "hoodie": stage_hoodie, "jacket": stage_jacket, "trim": stage_trim,
          "hood": stage_hood, "accessories": stage_accessories, "paint": stage_paint, "finish": stage_finish}


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
                ids.update(st["ids"]); garments.update(st["garments"]); points.update(st["points"])
            t = time.time()
            print(f"== {name}")
            STAGES[name](m)
            m.save_project(path=str(WORK / f"{name}.garment.json"))
            (WORK / f"{name}.ids.json").write_text(json.dumps({"ids": ids, "garments": garments, "points": points}))
            m.render_preview(pngPath=str(WORK / f"{name}.png"), width=1500, height=820, views="front,quarter,back")
            print(f"   {time.time()-t:.1f}s")
    finally:
        m.close()


if __name__ == "__main__":
    main(sys.argv[1:])
