---
description: Compare the outfit with a reference image (compare_to_reference) and judge how close it is.
argument-hint: <reference image path>
---

Compare the outfit in the current Seamstress project with this reference image: $ARGUMENTS

If no image path was given, ask for one and stop. If the file does not exist, say so and stop.

1. `get_project_summary` (stop if `server.staleWarning` asks for a rebuild).
2. Look at the reference image yourself first: the garments, their lengths and fit, the layering order, the colours
   and fabrics, the trim, the folds, and the camera angle it was taken from.
3. Call `compare_to_reference` with the image path. Match the camera to the reference where the tool lets you
   (a front, quarter or back view, or a `yaw` and `pitch`).
   If the server has no `compare_to_reference` tool, say that this command needs the outfit-critic tools (a newer
   Seamstress build), then render the closest view with `render_preview` and compare the two pictures yourself.
4. Give a judgement:
   - **Score**: how close the outfit is overall, 1 to 10, in one line.
   - **Matches**: what already reads like the reference (two or three points).
   - **Differences**, worst first: silhouette and length, fit and ease, layering, folds and stiffness, colour and
     texture, trim and hardware. For each one, the tool call that would close the gap.
5. Ask whether to apply the fixes. If yes, apply them, simulate what changed, and run `compare_to_reference` again
   to show the new score.

Judge the cloth, not the body or the background: the avatar and the lighting are not part of the outfit.
