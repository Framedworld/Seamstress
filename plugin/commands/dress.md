---
description: Dress the avatar in an outfit from a description. Fitted templates first, then trim, then verify and show a render.
argument-hint: <outfit description>
---

Dress the avatar in Seamstress in this outfit: $ARGUMENTS

If no description was given, ask for one in a single short question and stop.

Use the Seamstress MCP tools and follow the `seamstress-garment-tailoring` skill (load it now if it is not already
loaded). Work like this:

1. **Where you are working.** Call `get_project_summary`. If `server.staleWarning` is set, tell the user the server
   needs a rebuild (see the plugin README) and stop. Read `liveLink`, or call `live_link` with `action: "status"`.
   - Live (the Seamstress window is open): the user watches every change and each tool call is one undo step. Build
     on what is open; if the project has unsaved changes, never call `new_project` or `open_project`. If the outfit
     needs a clean project, ask the user first.
   - Headless: start with `new_project` unless the summary already shows an avatar the user wants dressed.
2. **The body.** If no avatar is loaded, ask which body to use (a file path, or `list_body_profiles`) before loading
   one. Read the `tape` block from `load_avatar` or `get_avatar_landmarks`: girths, crotch, armpit and neck heights,
   `shoulderRidge`.
3. **Plan the layers** from the skin outwards, and say the plan in two or three lines: which garment each item is,
   which `add_template_garment` template covers it (Fitted Trousers, Fitted Tee, Fitted Hoodie, Fitted Jacket) and
   what is left for hand drafting or trim. Pick ease, length, sleeves, hood and open front from the description, using
   the skill's numbers (trousers ease about 3, a hoodie under a jacket ease -4, and so on).
4. **Templates first.** Add each template garment in order, inner to outer, and `simulate` each one before the next.
   After every simulate read `openSeams`, the peak speed and `penetratedVertices`, and fix what they report before
   moving on (the skill's "Sewing and simulating" section says how).
5. **Fabric and look.** `set_fabric` (canvas and denim stiff, knits soft) and `set_material` with colours from the
   description, before adding trim, since patches take their host's fabric.
6. **Then trim.** Pockets, flaps and patches with `add_patch`, sleeves and cuffs with `add_tube`, collars and hoods with
   `add_collar` / `add_hood`, buttons, snaps and the like with `add_hardware`. Each layer of trim gets its own
   `level`. Simulate the trim and check `openSeams` again.
7. **Verify.** `inspect_fit` for the whole outfit: no open seams, no penetrated vertices. Then `render_preview` with
   `views: "front,quarter,back"` to a PNG in a scratch or output folder, and a close look (`focusPieceId`, or `focus`
   with `spanCm`) at anything doubtful: the neck, a pocket, a cuff. Look at the pictures yourself and fix what reads
   wrong (an inner layer showing through, trim floating, hems out of order) before you finish.
8. **Show it.** Show the user the final render (the PNG path, and the image itself where the client can display it).
   Headless: `save_project` to a path the user can open in the window, and say where. Live: say the work is in their
   open window and can be undone step by step; do not save over their file unless they ask.

Finish with a short summary: the garments, anything you could not do from the description, and the numbers that
matter (open seams, penetrations). Keep it under ten lines.
