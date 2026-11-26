---
name: seamstress-garment-tailoring
description: >-
  Build layered, sewn and draped outfits on a custom avatar in Seamstress through its MCP server or a Python script:
  loading and measuring the body, fitted templates, drafting patterns, sewing, layering garments, pockets and trim,
  sleeves, hoods, hardware, stiffness and shaped folds (pull_cloth, set_creases), textures and verification. Use when
  designing, fitting or polishing clothing in Seamstress.
---

# Seamstress garment tailoring

Worked example: `samples/gus-mechanic/scripts/build.py` builds Gus's mechanic outfit (canvas trousers with cargo
pockets, tee, hoodie with its hood down, open canvas jacket with pushed-up sleeves, straps, holster, hardware) in
stages that can be re-run one at a time: about 80 seconds, plus a minute for `paint.py` to paint each piece. Tool
reference: `docs/MCP.md`.

## Start

1. Call `get_project_summary` and read `server.staleWarning`. If it is set, rebuild before doing anything. The running
   server's exe is locked, so build a private copy (`~/.dotnet/dotnet.exe build src/Seamstress.Mcp -c Release -o
   <scratch>/mcp`) and point the script's `SEAMSTRESS_MCP` at it. A connected MCP session only picks up a new build
   after a restart.
2. Check `live_link` (or `liveLink` in the summary): with the Seamstress window open, the tools work on the user's
   open project, live. They watch, each call is one undo step, and `simulate` drapes in their window. Ask before
   `open_project` or `new_project` there (refused over unsaved changes). Building a whole outfit from scratch is
   better headless: script it with `scripts/mcp.py` (a stdio client, headless unless `live=True`) in stages that each
   save a project, then open the result in the window.
3. `load_avatar` takes OBJ, binary FBX or glTF/GLB directly and reports which meshes it used and skipped. Drape on the
   pose the garment will be skinned to (the file's rest pose); use `step_pose` only for a different final pose.
4. Read the `tape` block it returns: girths per leg and arm, crotch, armpit and neck heights, arm axis and droop, and
   `shoulderRidge` (the height a shoulder seam has to clear at each distance from the centre line).

## Templates first

`add_template_garment` cuts a Fitted Tee, Trousers, Jacket or Hoodie to the measured body. It returns every piece with
named edges and points, ready for patches and pulls. Each template goes one layer above what is already worn, with a
body offset just clear of it. Draft by hand only what no template covers.

- Options: `easePercent` (negative for a close under-layer), `extraLengthCm`, `particleDistance`, `sleeves` (`none`
  to add your own with `add_tube`), `hood`, `openFront` + `frontGapCm`. Trousers also take `outseamBackCm` (moves the
  side seam back so pockets on the front's outer edge sit on the side of the leg) and `hemGirthCm` (a narrow opening;
  started inside a high shoe it tangles, so keep it wider than the shoe).
- Trousers: `easePercent` about 3. Roomier than that reads as a costume, not workwear. Add `extraLengthCm` (about 9)
  when the hems should stack on the shoes.
- A hoodie under a jacket: `easePercent: -4`, `extraLengthCm: -4`, `bend` about 62, so it lies close and smooth. Then
  raise the jacket's `bodyOffsetMm` by 2-3 mm so it clears the hoodie's folds at the armholes.
- Template canvas starts at `bend` 78 and 3 mm. Raise it for stiff workwear (see Stiffness).

## Drafting by hand

- `add_piece` with `drawnOn: "front"` or `"back"`: draw in body centimetres (x towards the avatar's left, y above the
  floor) and the piece lands where it was drawn. Draw the left piece once and add the right with `mirrorX`.
- Name every edge you will sew (`edge` on the point it leaves). Use the same names on front and back
  (`side_l`, `side_r`, `sh_l`, `sh_r`) and sew them with one `sew_named` call.
- Leave `reversed` out: the direction that does not twist is worked out from where the pieces are.
- An edge sews to one other edge. To sew one long edge to several pieces (a cowl to back and fronts), add points along
  it so each stretch is its own edge.
- A back piece's own pattern x runs the other way (pattern x = -body x). This applies to everything that takes a point
  on a piece, such as `add_patch` and `pull_cloth`.

Hand-drafted fit that worked on a heavy build (Gus: chest 114, hips 120, thigh 57):
- Tee: +5% at chest, tapered at the waist with a curved side seam; spare width there bunches and pokes through the
  jacket. Neck opening clearly larger than the neck base.
- Jacket: +15% at chest, drop shoulder, shoulder seam drafted 2 cm above `shoulderRidge` plus the body offset;
  armholes ending 4-5 cm below the armpit.
- On a belly the trouser front waist settles under it, about 7 cm lower than drafted: check where it landed before
  placing the tee and jacket hems.

## Layering: body offset, not fake thickness

Keep fabric `thickness` real and give each garment worn over another a `bodyOffsetMm`: the offset of the layer below,
plus its thickness, plus a little air. Templates and `add_patch` trim set this themselves; the table is for garments
you create by hand.

| Garment | thickness | bodyOffsetMm |
|---|---|---|
| Canvas trousers | 3.2 | 0 |
| Tee (over the waistband) | 1.6 | 3.5 |
| Canvas jacket | 3.2 | 8.5-11 |
| Hood roll, cowl | 4 | 11+ |
| Hanging accessories | 1.6-3 | 9.5+ |

With offsets in place simulate with `selfCollision: false`: it is several times faster. Turn it on for:
- trim on a loose host: cargo pockets on roomy trousers get poked through by the trousers' own folds unless the
  trim's simulate has `selfCollision: true`;
- cloth that must not pass through itself, such as trouser hems stacking on boots.

Self-collision works off overlaps gradually, so a drape made without it can be simulated with it (Gus's jacket
against the whole outfit: about 30 ms a frame, settling within a second). It used to throw such cloth apart at
10+ m/s. Keep it off for pieces sewn by one edge that overhang their host (collars, facings), which crumple.

The project's own `simulation.selfCollision` is what the app uses when someone presses Simulate or pinches; the
MCP `simulate` call's flag does not change it. A garment with `"frozen": true` in the project is never simulated
and is always collided with, even with self-collision off: the app's ❄ button in Layers, for building a layer
over finished ones.

Garment-level `thicknessMm` is also what makes cloth render as a shell rather than a paper-thin sheet.

## Sewing and simulating

- One `simulate` at the fabric's real friction is enough: a piece slides freely until its own seams have closed.
- Read `openSeams` after every simulate. It names the seam, the gap and the likely cause:
  wrong way round (`reverse_seam`), blocked or too short (add ease, move the seam clear of the body), or `advice`
  saying the cloth was still moving (simulate again). A gap equal to a body part's width means the piece cannot get
  round it.
- A tube must be wider than the limb plus the room the garment keeps for the layers under it
  (`2 * pi * (bodyOffset + thickness + 2 mm)`). `add_tube` with `limb` returns `fitWarning` with the minimum.
- Open fronts: sew a temporary stay strip between the two front edges, then simulate and shape everything on that
  garment (sleeves, pulls) before you `delete_piece` the stay. Any simulation of the garment after that swings the
  fronts open. Then add trim.
- Pushed-up sleeves: `add_tube` the sleeve (`shrinkWarp: 0.92`) and a cuff sewn to its end, simulate, `pin_piece` the
  cuffs, lengthen only the sleeve, simulate, unpin. The cloth has nowhere to go but into folds. Without the pin the
  cuff slides to the wrist. Canvas: `shrinkWarp` about 1.14, `shrinkWeft` 1.06, `bend` 90+, for a few broad rolls;
  pushed harder (1.25+) it crumples into small ripples like a sweater. A soft knit takes `shrinkWarp` 1.3, `bend` 70.
- The simulation does not give the same result twice. Give loose cloth (a hanging rag) enough frames to settle every
  time rather than tuning numbers to one lucky run.

## Stiffness and shaping folds

- Canvas, denim and workwear read stiff: `bend` 84-93 (stiffness rises tenfold every ~29 points), `thickness` about
  3.2 mm, `stretch` 95+. Below about 65 they fold like a knit.
- Stiff cloth will not hold folds you push into it: it springs back towards flat. Shape it, then set creases.
  `set_creases` makes the current drape the shape bending relaxes towards (80-85% strength), saved with the project.
- The worn-canvas recipe:
  1. Drape with a softer `bend` (about 60) and extra length, so folds form.
  2. `pull_cloth` the knees down towards the shoes with `hold: true`, so the shins stack.
  3. `set_creases` at 80-85%.
  4. `pin_piece` with `pinned: false` to let go.
  5. Raise `bend` to canvas and simulate briefly. The folds stay, big and crisp.
- `pull_cloth` grabs a patch round a pattern point and moves it in steps while the garment simulates (`creasePercent`
  sets creases at the end). It refuses a point that is not on the piece (on a back piece, negate x). Pushing cloth
  towards something that stops it (a shoe, a cinched band) makes folds; pulling it away just moves the whole piece (a
  trouser leg pulled up lifts off the shoe). Drag folds on a jacket front: pull the lower edge 3-4 cm down and in.
- Creases belong to the mesh they were set on: changing a piece's outline or particle distance drops them. Set them
  last.
- For people shaping by hand in the app: the Pinch tool (G) drags cloth during a live simulation, its Grab slider
  sets how much cloth it takes. In Pull mode (the default) letting go leaves the cloth simulating; Pull & Freeze
  stops it where it was dropped and sets the pulled folds as creases, in the same undo step. Set Creases (K) does the same for the whole garment. The blocks are there too:
  Garment ▸ Add Fitted … (with fit, length, sleeves, open front), Add Collar…, Add Hood…, and the Trim tool (J)
  for pockets, flaps, patches, snaps, buttons, rivets and buckles dragged or clicked onto a piece in 2D.

## Blocks

- `add_patch` on a draped host: `kind` pocket (top open), flap (top sewn), patch (all round); footprint in the host's
  pattern centimetres. `easePercent` 4 lies nearly flat, 12-15 puffs, 30 makes a bellows or a bunched bag.
- `acrossEdge` centres it on a seam, half on each piece: cargo pockets on an outseam. Distances are along the edge
  from its start; a mirrored piece's edge runs the other way.
- `stitchEdges` sews chosen edges only: one edge for a collar leaf, a zip facing, a hanging holster plate.
- Give each layer of trim its own `level`, one above what it lies on. On stiff canvas the host's folds poke through
  level-1 trim, so put chest pockets at 2 and flaps or a name patch over them at 3.
- Hoods: `add_collar` with `rollCm` (about 4.5, `bend` 88, 4 mm fleece) for a hood pushed down into a roll round the
  neck; `add_hood` for the hood itself lying down the back (soft, `bend` about 46).
- `add_hardware` for snaps, buttons, pens, tool handles and wrenches: pinned, so a simulation does not move it by
  itself, but it is attached to the piece it is put on (`attachedTo` in the project) and carried along rigidly when
  that piece is simulated. Put it on the piece it sits on (a snap on a flap goes on the flap). Pinned hard parts
  without a host are attached to the cloth under them (within 3 cm) when a simulation starts.
- In the app, simulating or pinching a garment also simulates everything surface-stitched onto it (pockets, flaps,
  collars, recursively); `simulate` over MCP still moves only the garments it is given.
- Set a garment's material before adding trim to it: patches take their host's fabric.

## Materials

- `set_material` with `tileCm`: the real width one copy of the texture covers. The cloth then looks the same on every
  garment. Leave `color` out with a texture (it tints).
- `repeat: 0` fits one image to one piece: match the image's aspect to the piece (a 16 x 21 cm knee panel -> 640 x
  840). `scripts/paint.py` paints such images per piece: weave, tone-on-tone topstitching, wear on the edges.
- `*_normal.png`, `*_rough.png`, `*_metal.png` next to the texture are loaded automatically.
- Wear comes from fine weave, stippled grit, gentle mottling (about ±15%) and darker edges. Keep noise above about 10
  cycles per tile; lower reads as camouflage blotches. Check the palette's neutrals against the reference: a slight
  red or blue cast in dark canvas reads as plum or purple.

## Verify

1. After every simulate: `openSeams` empty, peak speed under 0.3, `penetratedVertices` 0.
   Before showing anything, `critique_outfit`: fix every critical and major problem it lists (each has a place, a
   `fix` with the tool to call and a `look` camera for `render_preview`), then call it again. It finds open seams,
   undraped pieces, cloth in the body, inner layers showing through, hems in the wrong order, trim and hardware
   floating or sunk, stretch over 8%, cloth still moving, and (as a hint) worn garments that look too clean.
   For `layer_show_through` and `hem_order`, simulate the inner garment again with `selfCollision: true` first; if it
   will not settle under the layers over it, `tuck_layers` moves just the cloth that shows back under, without
   simulating, then render the places it moved.
2. `render_preview` after every stage: `views: "front,quarter,back"` for the whole figure, then `focusPieceId` or
   `focus` + `spanCm` with `yaw`/`pitch` for close looks at the neck, a pocket, a cuff. Piece edges are drawn darker,
   so same-cloth patches and seams are visible. It needs no window and takes about a second.
3. For lighting and shell thickness, the live window:
   `Seamstress.exe <project> --gpu --view garmentclean --camera yaw,pitch,distance,tx,ty,tz --screenshot out.png --timeout 60`
   (also `garmentfrontclean`, `garmentcloseupclean`, `garmentbackclean`, `pattern`, or no `--view` for the whole
   window). It exits non-zero with a reason if it cannot capture; give the caller its own time limit as well, because
   the process has once failed to exit after writing the picture. Or render the exported `.glb` in Blender with
   `scripts/render.py`.
4. Judge light direction on the cloth, never on the avatar's skin: an imported body's normals can face inwards and
   shade mirror-wise.
5. Look for: neck and shoulders closed, no inner layer showing through an outer one (dark flecks near hems and side
   seams), hems in the intended order, trim lying flat, hardware seated rather than floating.
6. With concept art or a photo to match, `compare_to_reference` with the reference's camera: it writes the reference,
   your render and the overlaid silhouettes side by side, with silhouette overlap (`iou`), width ratios by height and
   a colour distance per region. Judge the details from the picture; use the numbers to find where to look.
