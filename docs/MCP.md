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
| Project | `new_project`, `open_project`, `save_project`, `get_project_summary`, `export_garment` (.glb .gltf .obj .fbx .svg) |
| Body | `load_avatar`, `get_avatar_landmarks`, `list_body_profiles`, `save_body_profile`, `load_body_profile`, `step_pose`, `refit_garment_to_body`, `inspect_metahuman_dna` |
| Garments | `create_garment`, `set_garment` (name, layer, body offset, mesh density, thickness, visibility), `add_template_garment` |
| Pieces | `add_piece`, `add_rectangle_piece`, `mirror_piece`, `cut_piece`, `inset_band`, `delete_piece`, `arrange_piece_3d`, `layout_pattern`, `import_mesh_as_pattern` |
| Sewing | `sew_edges`, `sew_named`, `auto_sew`, `sew_surface_stitch`, `reverse_seam`, `delete_seam` |
| Blocks | `add_patch` (patch, pocket, flap; on a piece or centred on a seam), `add_tube` (sleeve, cuff, leg), `add_hardware` (snap, button, rivet, bar, buckle, pen, handle, wrench, custom) |
| Fabric and look | `set_fabric`, `set_material`, `pin_piece` |
| Simulation | `simulate`, `reset_drape`, `inspect_fit`, `locate_on_piece` |
| Looking | `render_preview` (shaded and textured PNG from any angle, framed on the figure, a point or one piece; 2D pattern SVG) |

Edges can be given by number or by name everywhere. A back piece is turned round, so its own pattern x runs the other
way (pattern x = -body x) when you refer to points on it, for example in `add_patch`.

## Things that go wrong, and what the server tells you

- **A seam will not close.** `simulate` and `inspect_fit` list it under `openSeams` with a hint: sewn the wrong way
  round (use `reverse_seam`), blocked by the body or cut too short (add ease or move the seam line), or simply not
  finished (`advice` says to simulate again).
- **A cuff or sleeve is too narrow.** `add_tube` with `limb` compares the tube with the measured arm plus the room the
  garment keeps for the layers under it and returns `fitWarning` with the smallest circumference that can close.
- **A patch floats or sinks.** It belongs in a trim garment one layer out from its host with a little more body offset;
  `add_patch` sets that up. Do not raise its fabric thickness to push it out.
- **A texture looks huge or tiny.** Use `tileCm`. `repeat` counts repeats across the garment's whole 2D layout, so the
  same number looks different on every garment. The colour multiplies the texture: leave it out to see the texture as
  it is.
- **A layer shows through the one over it.** The inner garment is larger than the outer one where they overlap, or
  the outer garment's `bodyOffsetMm` is too small for the folds underneath.
