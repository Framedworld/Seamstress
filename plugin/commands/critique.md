---
description: Critique the outfit on the avatar with critique_outfit and propose fixes.
argument-hint: "[garment or focus, optional]"
---

Critique the outfit in the current Seamstress project and propose fixes. Focus, if given: $ARGUMENTS

Use the Seamstress MCP tools and the `seamstress-garment-tailoring` skill.

1. Call `get_project_summary`. If `server.staleWarning` is set, tell the user to rebuild the server first. Note whether
   you are working live in the user's window (`liveLink`) or on a headless project.
2. Call `critique_outfit` (for the whole outfit, or the garment named in the focus).
   If the server has no `critique_outfit` tool, say that this command needs the outfit-critic tools (a newer
   Seamstress build), then do the nearest thing: `inspect_fit` plus `render_preview` with
   `views: "front,quarter,back"`, and judge the render yourself against the skill's "Verify" checklist.
3. Turn the findings into a short list of fixes, worst first. For each one give:
   - what is wrong and where (garment, piece, seam),
   - the tool call that fixes it, with its arguments (for example `reverse_seam`, `set_garment` with a larger
     `bodyOffsetMm`, `add_patch` with another `easePercent`, `set_fabric` with a higher `bend`, `pull_cloth` then
     `set_creases`),
   - and what you expect it to change.
4. Do not apply the fixes yet. Ask the user which ones to apply (all, some, or none). When they choose, apply them,
   `simulate` what changed, and call `critique_outfit` again to show what improved. Live, each fix is one undo step
   in their window.

Keep the report short: at most eight fixes, one or two lines each.
