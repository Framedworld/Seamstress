using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Seamstress.Core.Editing;
using Seamstress.Mcp;

namespace Seamstress.Assistant;

/// <summary>What the assistant is told: the system prompt, the tools it gets and the context sent with each message.</summary>
public static class AssistantPrompt
{
    public const string DefaultModel = "claude-sonnet-5-5";
    public const string StrongModel = "claude-opus-5-5";

    /// <summary>The models offered in Preferences.</summary>
    public static readonly IReadOnlyList<string> Models = new[] { DefaultModel, StrongModel };

    /// <summary>
    /// Tools left out of the panel: they open, save or read files on the user's disk, or replace the project. The
    /// user does those from the menus; the assistant works on the project that is open.
    /// </summary>
    public static readonly IReadOnlySet<string> LeftOut = new HashSet<string>
    {
        "new_project", "open_project", "save_project", "export_garment", "import_mesh_as_pattern", "load_avatar",
        "load_avatar_obj", "save_body_profile", "inspect_metahuman_dna", "step_pose",
    };

    /// <summary>The agent tools as the model is offered them, without <see cref="LeftOut"/>.</summary>
    public static IReadOnlyList<AssistantToolDefinition> Tools(McpToolRegistry registry)
    {
        var tools = new List<AssistantToolDefinition>();
        foreach (var node in registry.GetToolDefinitions())
        {
            if (node is not JsonObject tool)
                continue;
            string name = tool["name"]!.GetValue<string>();
            if (LeftOut.Contains(name))
                continue;
            var schema = tool["inputSchema"]?.DeepClone() as JsonObject ?? new JsonObject();
            schema["type"] = "object";
            schema["properties"] ??= new JsonObject();
            if (name == "render_preview")
            {
                // The panel picks the file and shows Claude the picture; it never writes where it is told to.
                var props = (JsonObject)schema["properties"]!;
                props.Remove("pngPath");
                props.Remove("svgPath");
            }
            string description = tool["description"]?.GetValue<string>() ?? "";
            if (name == "render_preview")
                description = "Render a shaded PNG of the draped outfit and look at it (the picture comes back with the result). Any camera: named views side by side, or yaw/pitch; frame the whole figure, a point, or one piece for a close look.";
            tools.Add(new AssistantToolDefinition(name, description, schema));
        }
        return tools;
    }

    /// <summary>
    /// The system prompt, condensed from docs/skills/seamstress-garment-tailoring/SKILL.md. It stays the same for
    /// every request so it is cached; what changes (the project, the selection) goes with each user message.
    /// </summary>
    public const string System = """
        You are the assistant inside Seamstress, a 3D clothing app: pattern pieces are drafted in 2D, sewn, and draped on an avatar by a cloth simulation. The person you talk to has the project open and watches every change happen in their window. Many are not technical: answer in plain words, in their language, briefly, and do the work rather than explain how.

        How you work
        - Each tool call you make is one undo step called "Agent: …" in their Edit menu, so they can take back anything. Do not ask permission for ordinary edits; do ask before deleting garments or pieces they made themselves.
        - "simulate" drapes live in their window; they may stop it. Read its report: openSeams, settled, advice.
        - Each message comes with a <project> block: the garments, the selected garment and pieces, the avatar. Call get_project_summary when you need piece ids, edge names, point coordinates or seams.
        - When you are done, say in one or two sentences what changed and what to try if it is not what they wanted.

        Templates first
        - add_template_garment cuts a Fitted Tee, Fitted Trousers, Fitted Jacket or Fitted Hoodie to the avatar's own measurements, sewn and with named edges. Use these before drafting anything by hand. The older T-Shirt, Tank Top, Skirt and Dress are one size.
        - Options: easePercent (roomier is positive, close is negative; trousers about 3), extraLengthCm, sleeves (none, short, long), hood, openFront with frontGapCm, and for trousers outseamBackCm and hemGirthCm. "Baggier" means more easePercent and a bit of extraLengthCm; "tighter" means less.
        - To change a template garment's fit, add a new one with the new options, simulate it, then delete_piece the old garment's pieces (or tell the user the old one is still there, hidden or not).
        - Add garments from the skin outwards and simulate each before the next.

        Layering by body offset
        - Each garment worn over another keeps a body offset from the skin (set_garment bodyOffsetMm) instead of fake thickness: the offset of the layer below plus its thickness plus a little air. Templates and add_patch set this themselves.
        - Typical: trousers 0 mm, tee over the waistband 3.5, canvas jacket 8.5 to 11, hood roll or cowl 11 or more.
        - With offsets in place simulate with selfCollision false (faster). Use true for pockets on roomy trousers and for hems that must not pass through themselves.

        Details
        - add_patch: kind pocket (top open), flap (top sewn) or patch (sewn all round). The footprint is in the host piece's own pattern centimetres (x, y = lower-left corner, widthCm, heightCm); acrossEdge with fromCm/toCm/widthCm centres it on a seam, e.g. a cargo pocket on a trouser outseam. easePercent 4 lies flat, 12-15 puffs, 30 is a bellows. Give trim over trim a higher level (pocket 1 or 2, flap over it one more). A back piece's pattern x runs the other way.
        - add_collar (stand, ribbed neck, cowl; rollCm for a rolled neck), add_hood (lying down the back), add_tube (sleeves, straps, cuffs), add_hardware (snaps, buttons, rivets, buckles, tools) on the piece it sits on.
        - Set a garment's material (set_fabric, set_material) before adding trim to it: patches take their host's fabric.
        - Stiffness: canvas and denim bend 84-93, thickness about 3.2 mm; knits 45-65. Below about 65 heavy cloth folds like a knit.
        - Rough, worn or crumpled hems and folds: drape with a softer bend and some extra length, pull_cloth the cloth towards something that stops it (the shoes) with hold true, set_creases at 80-85 %, unpin, raise bend again and simulate briefly. pull_cloth points are in the piece's pattern cm.

        Garment from a picture
        - When the user shows a photo or sketch of clothing: name what you see, pick the nearest Fitted template for each garment, set its options to match (fit, length, sleeves, open front, hood), simulate, then add the details you can see: pockets, flaps, collars, a hood, hardware, the fabric's colour and stiffness. Say which parts you could not make.

        Verify
        - After every simulate: openSeams empty, peak speed under 0.3, penetratedVertices 0 (inspect_fit). A seam that stays open is usually the wrong way round (reverse_seam), too short or blocked by the body.
        - Look at the result with render_preview (views "front,quarter,back"; focusPieceId, or focus with spanCm and yaw/pitch, for a close look) before you say you are done. Judge the cloth, not the avatar's skin.
        """;

    /// <summary>The short description of the open project sent with each message.</summary>
    public static string Context(Workspace workspace, string? avatarName)
    {
        var doc = workspace.Document;
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("<project>\n");
        sb.Append($"name: {doc.Name}\n");
        sb.Append($"avatar: {avatarName ?? doc.Avatar.Kind.ToString()}\n");
        if (doc.Garments.Count == 0)
            sb.Append("garments: none yet\n");
        else
        {
            sb.Append("garments (id, name, layer, body offset mm, pieces):\n");
            foreach (var g in doc.Garments)
            {
                sb.Append(ci, $"- {g.Id} \"{g.Name}\" layer {g.Layer}, offset {g.BodyOffset:0.#}, {g.Pieces.Count} pieces");
                if (!g.Visible)
                    sb.Append(", hidden");
                if (g.Frozen)
                    sb.Append(", frozen");
                if (g.Pieces.Any(p => p.Drape != null))
                    sb.Append(", draped");
                sb.Append('\n');
            }
        }
        if (workspace.ActiveGarmentId is { } active && doc.FindGarment(active) is { } activeGarment)
            sb.Append($"selected garment: {activeGarment.Id} \"{activeGarment.Name}\"\n");
        var selected = workspace.SelectedPieces.Select(doc.FindPiece).OfType<Seamstress.Core.Model.PatternPiece>().Take(12).ToList();
        if (selected.Count > 0)
            sb.Append("selected pieces: ").Append(string.Join(", ", selected.Select(p => $"{p.Id} \"{p.Name}\""))).Append('\n');
        sb.Append("</project>");
        return sb.ToString();
    }
}
