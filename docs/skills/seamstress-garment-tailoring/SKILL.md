---
name: seamstress-garment-tailoring
description: >-
  Build layered, sewn and draped outfits on a custom avatar in Seamstress through its MCP server or a Python script:
  loading and measuring the body, drafting patterns, sewing, layering garments, pockets and trim, sleeves, hardware,
  textures and verification. Use when designing, fitting or polishing clothing in Seamstress.
---

# Seamstress garment tailoring

Worked example: `samples/gus-mechanic/scripts/build.py` builds a 14-garment mechanic outfit on Gus in about ten
seconds, in stages that can be re-run one at a time. Tool reference: `docs/MCP.md`.

## Start

1. Call `get_project_summary` and read `server.staleWarning`. If it is set, rebuild
   (`dotnet build src/Seamstress.Mcp -c Release`, SDK in `~/.dotnet`) and restart the server before doing anything.
2. For more than a handful of pieces, script it with `scripts/mcp.py` (a stdio client) and build in stages that each
   save a project.
3. `load_avatar` takes OBJ, binary FBX or glTF/GLB directly and reports which meshes it used and skipped. Drape on the
   pose the garment will be skinned to (the file's rest pose); use `step_pose` only for a different final pose.
4. Read the `tape` block it returns before drafting: girths per leg and arm, crotch, armpit and neck heights, arm axis
   and droop, and `shoulderRidge` (the height a shoulder seam has to clear at each distance from the centre line).

## Drafting

- `add_piece` with `drawnOn: "front"` or `"back"`: draw in body centimetres (x towards the avatar's left, y above the
  floor) and the piece lands where it was drawn. Draw the left piece once and add the right with `mirrorX`.
- Name every edge you will sew (`edge` on the point it leaves). Use the same names on front and back
  (`side_l`, `side_r`, `sh_l`, `sh_r`) and sew them with one `sew_named` call.
- Leave `reversed` out: the direction that does not twist is worked out from where the pieces are.
- An edge sews to one other edge. To sew one long edge to several pieces (a cowl to back and fronts), add points along
  it so each stretch is its own edge.
- A back piece's own pattern x runs the other way (pattern x = -body x) when you later place something on it.

Fit numbers that worked on a heavy build (Gus: chest 114, hips 120, thigh 57):
- Tee: +5% at chest, tapered at the waist with a curved side seam; spare width there bunches and pokes through the
  jacket. Neck opening clearly larger than the neck base.
- Jacket: +15% at chest, drop shoulder, shoulder seam drafted 2 cm above `shoulderRidge` plus the body offset;
  armholes ending 4-5 cm below the armpit.
- Pants: hips +7%, thigh +20%, knee +12%; front rise 2 cm shorter than the back. On a belly the front waist settles
  under it, about 7 cm lower than drafted: check where it landed before placing the tee and jacket hems.

## Layering: body offset, not fake thickness

Keep fabric `thickness` real (1.5-3 mm) and give each garment worn over another a `bodyOffsetMm`: the offset of the
layer below, plus its thickness, plus a little air.

| Garment | thickness | bodyOffsetMm |
|---|---|---|
| Pants | 2.4 | 0 |
| Tee (over the waistband) | 1.6 | 2.6 |
| Jacket | 2.6 | 8.5 |
| Hood, cowl | 4 | 11 |
| Hanging accessories | 1.6-3 | 9.5 |

Trim garments made by `add_patch` get their layer and offset automatically. With offsets in place simulate with
`selfCollision: false`: it is 50-100 times faster and smoother. `selfCollision: true` is for cloth that must not pass
through itself (trouser hems stacking on boots, patches sewn all round in the middle of a host); follow it with a
short pass without it. It crumpled pieces sewn by one edge that overhang their host (collars, facings).

Garment-level `thicknessMm` is also what makes cloth render as a shell rather than a paper-thin sheet.

## Sewing and simulating

- One `simulate` at the fabric's real friction is enough: a piece slides freely until its own seams have closed.
- Read `openSeams` after every simulate. It names the seam, the gap and the likely cause:
  wrong way round (`reverse_seam`), blocked or too short (add ease, move the seam clear of the body), or `advice`
  saying the cloth was still moving (simulate again). A gap equal to a body part's width means the piece cannot get
  round it.
- A tube must be wider than the limb plus the room the garment keeps for the layers under it
  (`2 * pi * (bodyOffset + thickness + 2 mm)`). `add_tube` with `limb` returns `fitWarning` with the minimum.
- Open fronts: sew a temporary stay strip between the two front edges, simulate, `delete_piece` it before adding trim.
- Pushed-up sleeves: `add_tube` the sleeve (`shrinkWarp: 0.92`) and a cuff sewn to its end, simulate, `pin_piece` the
  cuffs, raise only the sleeve to `shrinkWarp: 1.3, bend: 70`, simulate, unpin. The cloth has nowhere to go but into
  folds. Without the pin the cuff slides to the wrist.

## Stiffness and shaping folds

- Canvas, denim and workwear read stiff: `bend` 80-90 (stiffness rises tenfold every ~29 points), `thickness` about
  3 mm, `stretch` 95+. Below about 65 they fold like a knit. Pushing stiff sleeves up hard (`shrinkWarp` 1.25+) crumples
  them into small ripples; about 1.14 gives a few broad rolls.
- Folds you want and the simulation will not make on its own: shape, then set creases. The worn-canvas recipe:
  drape with a softer bend (about 60) and extra length so folds form, `pull_cloth` the knees down towards the shoes
  with `hold: true` so the shins stack, `set_creases` (80-85%), `pin_piece` false to let go, raise `bend` to canvas,
  simulate briefly. The folds stay, big and crisp.
- `pull_cloth` grabs a patch round a pattern point and moves it in steps while the garment simulates. Pushing cloth
  towards something that stops it (a shoe, a cinched band) makes folds; pulling it away just moves the whole piece.
  Do it on an open front while its temporary stay is still sewn: simulated without the stay the fronts swing open.
- Trousers: `outseamBackCm` moves the side seam back so pockets at the front's outer edge sit on the side of the
  leg; `hemGirthCm` narrows the opening (a narrow hem started inside a high shoe tangles: keep it wider than the shoe).

## Blocks

- `add_patch` on a draped host: `kind` pocket (top open), flap (top sewn), patch (all round); footprint in the host's
  pattern centimetres. `easePercent` 4 lies nearly flat, 12-15 puffs, 30 makes a bellows or a bunched bag (a folded
  hood is a 30% patch on the upper back).
- `acrossEdge` centres it on a seam, half on each piece: cargo pockets on an outseam. Distances are along the edge
  from its start; a mirrored piece's edge runs the other way.
- `stitchEdges` sews chosen edges only: one edge for a collar leaf, a zip facing, a hanging holster plate.
- A flap is `level: 2` so it lies over the pocket under it.
- `add_hardware` for snaps, buttons, pens, tool handles and wrenches: pinned, so later simulations do not move it.
  Put it on the piece it sits on (a snap on a flap goes on the flap).
- Set a garment's material before adding trim to it: patches take their host's fabric.

## Materials

- `set_material` with `tileCm`: the real width one copy of the texture covers. The cloth then looks the same on every
  garment. Leave `color` out with a texture (it tints).
- `repeat: 0` fits one image to one piece: match the image's aspect to the piece (a 17 x 26 cm knee panel -> 640 x 960).
- `*_normal.png`, `*_rough.png`, `*_metal.png` next to the texture are loaded automatically.
- Wear comes from fine weave, stippled grit and darker edges. Keep noise above about 10 cycles per tile; lower reads as
  camouflage blotches.

## Verify

1. After every simulate: `openSeams` empty, peak speed under 0.3, `penetratedVertices` 0.
2. `render_preview` after every stage: `views: "front,quarter,back"` for the whole figure, then `focusPieceId` or
   `focus` + `spanCm` with `yaw`/`pitch` for close looks at the neck, a pocket, a cuff. Piece edges are drawn darker,
   so same-cloth patches and seams are visible. It needs no window and takes about a second.
3. For lighting and shell thickness, the live window:
   `Seamstress.exe <project> --gpu --view garmentclean --camera yaw,pitch,distance,tx,ty,tz --screenshot out.png --timeout 60`
   (also `garmentfrontclean`, `garmentcloseupclean`, `garmentbackclean`, `pattern`, or no `--view` for the whole
   window). It exits non-zero with a reason if it cannot capture; give the caller its own time limit as well, because
   the process has once failed to exit after writing the picture. Or render the exported `.glb` in Blender with
   `scripts/render.py`.
4. Look for: neck and shoulders closed, no inner layer showing through an outer one (dark flecks near hems and side
   seams), hems in the intended order, trim lying flat, hardware seated rather than floating.
