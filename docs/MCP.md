# Seamstress MCP server

`src/Seamstress.Mcp` is a stdio [Model Context Protocol](https://modelcontextprotocol.io) server that lets an assistant
(or a script) draft, sew, drape and export garments without the desktop window. It uses the same core and the same
CUDA solver as the app, and reads and writes the same `*.garment.json` projects.

```
dotnet build src/Seamstress.Mcp -c Release
```

Register `src/Seamstress.Mcp/bin/Release/net8.0/Seamstress.Mcp.exe` as a stdio server (see `.mcp.json`).
`get_project_summary` reports when the server was built and warns when the source next to it is newer: a server that
was never rebuilt silently lacks the newest tools. Rebuild and restart it when you see that warning.

For a complete worked example see `samples/gus-mechanic/scripts/build.py` (a 14-garment outfit built in about ten
seconds) and the tailoring guide in `docs/skills/seamstress-garment-tailoring/SKILL.md`.

## Working in the open window (live link)

When the Seamstress window is open, the server works on the project the user has open there instead of a project of
its own. The user watches every change appear in the 2D and 3D views, and can undo it: each tool call is one undo
step named `Agent: <tool>`.

- **Where it works is settled at the first tool call:** live when a window is open, headless otherwise. It does not
  change on its own after that. `live_link` reports which (`action: "status"`), connects to a window opened later
  (`"connect"`) or switches to a headless project (`"headless"`). `get_project_summary` shows the window under
  `liveLink`.
- **`simulate` runs live:** the window's own simulation runs for the frames asked for, so the user sees the cloth
  drape, and the reply is the usual report. Everything sewn onto the garment simulates with it, as when the user
  simulates. If the user stops it or edits meanwhile, the reply says so.
- **The user's work is safe:** `open_project` and `new_project` are refused while the window has unsaved changes;
  ask the user to save first. A tool that fails leaves nothing behind.
- **Scripts:** `samples/gus-mechanic/scripts/mcp.py` builds headless (it sets `SEAMSTRESS_LIVE=0`); pass `live=True`
  to work in the window. Any client can set `SEAMSTRESS_LIVE=0` for a project of its own.
- **How it connects:** the window listens on a port on this computer only (127.0.0.1) and writes the port and a
  random key to `%LOCALAPPDATA%\Seamstress\live-link.json`, which only the user's own processes can read. Calls
  without the key get no answer. Automated runs of the app (`--smoke`, `--screenshot`) do not listen.
- **Builds:** the tool code lives in `src/Seamstress.Agent`, shared by this server and the window. Building the app
  or the tests does not touch this server's folder, so they build while an agent is connected; this server itself
  still has to be stopped (or built elsewhere with `-o`) to be rebuilt.

## The short version

1. `load_avatar` a body (OBJ, binary FBX, glTF/GLB). The reply has tape measurements: girths per limb, crotch, armpit
   and neck heights, arm axes and the shoulder ridge.
2. `create_garment` for each layer. Give a garment worn over others a `bodyOffsetMm` (room for what is underneath)
   rather than a large fabric thickness.
3. `add_piece` with `drawnOn: "front"` or `"back"`: draw in body centimetres (x = cm towards the avatar's left, y = cm
   above the floor) and the piece is placed where it was drawn. Name the edges with `edge` on the points.
4. `sew_named` front to back by edge name; `sew_edges` for the rest. The direction that does not twist is chosen for you.
5. `simulate`. Read `openSeams`: each entry names the seam, how far apart it is and why.
6. `add_tube` for sleeves and cuffs, `add_patch` for pockets, flaps and patches (also across a seam), `add_hardware`
   for snaps, buttons, pens and tools.
7. `set_material` with `tileCm` (texture size in real centimetres), `render_preview` to look, `export_garment`.

## Tools

| Task | Tools |
|---|---|
| Project | `new_project`, `open_project`, `save_project`, `get_project_summary`, `export_garment` (.glb .gltf .obj .fbx .svg), `live_link` (work in the open window or headless) |
| Body | `load_avatar`, `get_avatar_landmarks`, `list_body_profiles`, `save_body_profile`, `load_body_profile`, `step_pose`, `refit_garment_to_body`, `inspect_metahuman_dna` |
| Garments | `create_garment`, `set_garment` (name, layer, body offset, mesh density, thickness, visibility), `add_template_garment` (fitted tee, trousers, jacket and hoodie cut to the body; trousers take `outseamBackCm` and `hemGirthCm`) |
| Pieces | `add_piece`, `add_rectangle_piece`, `mirror_piece`, `cut_piece`, `inset_band`, `delete_piece`, `arrange_piece_3d`, `layout_pattern`, `import_mesh_as_pattern` |
| Sewing | `sew_edges`, `sew_named`, `auto_sew`, `sew_surface_stitch`, `reverse_seam`, `delete_seam` |
| Blocks | `add_patch` (patch, pocket, flap; on a piece or centred on a seam), `add_tube` (sleeve, cuff, leg), `add_collar` (stand, gathered cowl, or with `rollCm` a padded roll such as a pushed-down hood), `add_hood`, `add_hardware` (snap, button, rivet, bar, buckle, pen, handle, wrench, custom) |
| Fabric and look | `set_fabric`, `set_material`, `pin_piece`, `set_creases` |
| Simulation | `simulate`, `pull_cloth`, `reset_drape`, `inspect_fit`, `locate_on_piece` |
| Looking | `render_preview` (shaded and textured PNG from any angle, framed on the figure, a point or one piece; 2D pattern SVG) |
| Checking | `critique_outfit` (ranked list of concrete problems, each with a place and a suggested fix), `compare_to_reference` (render with a reference picture's camera, side by side, with silhouette and colour scores) |

Edges can be given by number or by name everywhere. A back piece is turned round, so its own pattern x runs the other
way (pattern x = -body x) when you refer to points on it, for example in `add_patch`.

## Checking your own work

Before showing a result, check it yourself:

- **`critique_outfit`** (optional `garmentId`, `checkMotion`, `lookWorn`, `maxProblems`) returns the problems worst
  first. Each one has a `type`, a `severity` (critical, major, minor, hint), the garment and piece, `at` (3D point, m),
  `patternCm` (the same place on the piece's own pattern: what `pull_cloth` and `locate_on_piece` take), a
  description, a `fix` (a tool and its arguments where one fits, and a note) and `look` (`render_preview` arguments
  for a close look). It changes nothing. The checks:

  | type | finds |
  |---|---|
  | `open_seam` | seams and surface stitches still apart (as `openSeams`) |
  | `not_draped` | pieces never simulated, still flat where they were arranged |
  | `cloth_in_body` | cloth inside the avatar |
  | `layer_show_through` | an inner layer outside the outer garment where that garment covers it |
  | `hem_order` | the hem or opening of an inner layer lying over the garment worn over it |
  | `trim_floating`, `trim_sunk` | a patch standing off its host, or its host's folds poking through it |
  | `hardware_floating`, `hardware_sunk` | hard parts in the air over their cloth, or sunk into it |
  | `stretch_hotspot` | cloth stretched more than 8% over its cut size |
  | `still_moving` | cloth that moves on when simulated: each garment, with its trim, is simulated 24 frames and put back |
  | `looks_too_clean` | a soft hint: a garment meant to look worn (guessed from names, textures and set creases, or `lookWorn`) that is mostly smooth |

  Fix critical and major problems before showing the outfit; `checkMotion: false` skips the probe when time matters
  (it takes most of the few seconds a large outfit needs).
- **`compare_to_reference`** (`referencePath`; `yaw`/`pitch` or `view`, `focus`/`spanCm` or `focusPieceId` like
  `render_preview`) renders the outfit with the reference's aspect and writes `*_compare.png`: reference, render,
  and the two silhouettes overlaid with the figures scaled to the same height. It returns `iou` (above 0.85 is
  close, below 0.7 the outline differs a lot), width ratios at nine heights, and per region (head, shoulders and
  chest, waist and hips, legs, feet) the two palettes and their CIELAB distance (`deltaE`: under 10 close, over 25
  clearly different). Look at the picture yourself: the numbers say where. A reference with a transparent or plain
  background segments well; a dark figure on a dark backdrop is reported as low contrast, and its silhouette numbers
  are not reliable.

## Things that go wrong, and what the server tells you

- **A seam will not close.** `simulate` and `inspect_fit` list it under `openSeams` with a hint: sewn the wrong way
  round (use `reverse_seam`), blocked by the body or cut too short (add ease or move the seam line), or simply not
  finished (`advice` says to simulate again).
- **A cuff or sleeve is too narrow.** `add_tube` with `limb` compares the tube with the measured arm plus the room the
  garment keeps for the layers under it and returns `fitWarning` with the smallest circumference that can close.
- **A patch floats or sinks.** It belongs in a trim garment one layer out from its host with a little more body offset;
  `add_patch` sets that up. Do not raise its fabric thickness to push it out.
- **Stiff cloth springs back flat.** The solver bends cloth back towards its flat pattern. Shape the folds
  (`pull_cloth`, a softer first pass, a pose), then `set_creases`: the bending rests at the folded shape from then on,
  so canvas keeps its folds the way worn canvas does. `pull_cloth` with `creasePercent` does both. Saved with the
  project; `set_creases` with `clear` forgets them. In the app: the Pinch tool (G) drags cloth during a live
  simulation, its Grab slider sets how much it takes hold of, and Set Creases (K) keeps the result.
- **A texture looks huge or tiny.** Use `tileCm`. `repeat` counts repeats across the garment's whole 2D layout, so the
  same number looks different on every garment. The colour multiplies the texture: leave it out to see the texture as
  it is.
- **A layer shows through the one over it.** The inner garment is larger than the outer one where they overlap, or
  the outer garment's `bodyOffsetMm` is too small for the folds underneath.
