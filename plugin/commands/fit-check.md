---
description: Check the fit of the outfit (inspect_fit plus critique_outfit) and give a short report.
argument-hint: "[garment, optional]"
---

Check the fit of the outfit in the current Seamstress project. Garment, if given: $ARGUMENTS

Use the Seamstress MCP tools. This is a read-only check: change nothing unless the user asks afterwards.

1. `get_project_summary`: the garments, and whether the server is stale (`server.staleWarning`; if so, say the
   server needs a rebuild first).
2. `inspect_fit` (for the named garment, or with no `garmentId` for everything): open seams, seam length mismatches,
   penetrated vertices, the bounding box.
3. `critique_outfit` for the same scope. If the server has no such tool, say that part needs the outfit-critic tools
   (a newer Seamstress build) and continue with `inspect_fit` alone.
4. `render_preview` with `views: "front,quarter,back"` to a scratch PNG, and look at it.

Report in this shape, and nothing longer:

```
Fit check: <outfit or garment>     <OK | needs work>
Seams      <n open>  <worst: seam, gap cm, likely cause>
Body       <n penetrated vertices>  <where>
Layers     <any inner layer showing through, and where>
Critique   <the two or three most important points from critique_outfit>
Next       <the one fix to make first, as a tool call>
```

Then show the render.
