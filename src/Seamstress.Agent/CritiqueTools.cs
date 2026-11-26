using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Seamstress.Mcp;

/// <summary>
/// The self-checking tools: <c>critique_outfit</c> lists concrete problems with the drape, <c>compare_to_reference</c>
/// renders the outfit like a reference picture and scores how close it is. Neither changes the project.
/// </summary>
public static class CritiqueTools
{
    public static JsonObject CritiqueDefinition() => new()
    {
        ["name"] = "critique_outfit",
        ["description"] = "Check your own result before showing it: a ranked list of concrete problems with the draped outfit. "
            + "Checks open seams, pieces not draped, cloth inside the body, an inner layer showing through an outer one, hems "
            + "stacked the wrong way round, trim floating off or poked through by its host, hardware floating or sunk, stretch "
            + "hotspots (edges over 8% longer than cut), cloth still moving (a short simulation probe that is thrown away), and, "
            + "as a soft hint, garments meant to look worn that are too smooth. Each problem names the garment and piece, where it "
            + "is (3D point in metres and the piece's pattern point in cm), says what is wrong and suggests a fix (a tool and its "
            + "arguments where one fits). Changes nothing.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["garmentId"] = new JsonObject { ["type"] = "string", ["description"] = "Only this garment's problems (default: the whole outfit)." },
                ["checkMotion"] = new JsonObject { ["type"] = "boolean", ["description"] = "Run the still-moving probe (default true): each garment simulated on for 24 frames, then put back." },
                ["lookWorn"] = new JsonObject { ["type"] = "boolean", ["description"] = "Whether the garments are meant to look worn (default: guessed from names, textures and set creases). false skips the too-clean hint." },
                ["maxProblems"] = new JsonObject { ["type"] = "integer", ["description"] = "Most problems to list (default 25); counts cover all of them." },
            },
        },
    };

    public static JsonObject CompareDefinition() => new()
    {
        ["name"] = "compare_to_reference",
        ["description"] = "Render the outfit with the camera of a reference picture (concept art, a photo) and write the two side by "
            + "side, with a third panel overlaying the two silhouettes. Returns simple scores: silhouette overlap (IoU, from "
            + "background segmentation of both images, also with the figures scaled to the same height), width ratios at a few "
            + "heights, and a colour-palette distance (CIELAB deltaE) per region from head to feet. Look at the side-by-side image "
            + "yourself for the details; the numbers say where to look. PNG and WebP are read directly; other formats are "
            + "converted on Windows. Changes nothing.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["referencePath"] = new JsonObject { ["type"] = "string", ["description"] = "The reference image." },
                ["outputPath"] = new JsonObject { ["type"] = "string", ["description"] = "Side-by-side PNG to write (default: next to the reference, *_compare.png)." },
                ["yaw"] = new JsonObject { ["type"] = "number", ["description"] = "Degrees round the figure, as render_preview (0 = front, 90 = the avatar's left)." },
                ["pitch"] = new JsonObject { ["type"] = "number", ["description"] = "Degrees the camera is raised (default 4)." },
                ["view"] = new JsonObject { ["type"] = "string", ["description"] = "A named view instead of yaw/pitch: front, back, side, quarter, ... (default front)." },
                ["focusPieceId"] = new JsonObject { ["type"] = "string", ["description"] = "Frame this piece, as render_preview." },
                ["focus"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["description"] = "Frame this point [x, y, z] in metres (with spanCm)." },
                ["spanCm"] = new JsonObject { ["type"] = "number", ["description"] = "Height of the framed area in cm (default: the whole figure)." },
                ["showAvatar"] = new JsonObject { ["type"] = "boolean", ["description"] = "Draw the body (default true: a reference shows the whole character)." },
                ["height"] = new JsonObject { ["type"] = "integer", ["description"] = "Height in pixels of each panel (default 640); the render takes the reference's aspect." },
            },
            ["required"] = new JsonArray("referencePath"),
        },
    };

    public static object Critique(GarmentSession session, JsonElement args)
    {
        var options = new CritiqueOptions
        {
            GarmentId = Str(args, "garmentId"),
            CheckMotion = Bool(args, "checkMotion") ?? true,
            LookWorn = Bool(args, "lookWorn"),
        };
        int max = Math.Clamp(Int(args, "maxProblems") ?? 25, 1, 500);
        var problems = OutfitCritic.Critique(session.Doc, session.Workspace.MeshFor, session.ActiveAvatar, options);
        var counts = problems.GroupBy(p => p.Type).ToDictionary(g => g.Key, g => g.Count());
        var bySeverity = problems.GroupBy(p => p.Severity).ToDictionary(g => g.Key, g => g.Count());
        string summary = problems.Count == 0
            ? "No problems found."
            : string.Join(", ", counts.OrderByDescending(c => c.Value).Select(c => $"{c.Value} {c.Key.Replace('_', ' ')}"));
        return new
        {
            ok = problems.All(p => p.Severity is "hint" or "minor"),
            summary,
            counts,
            severities = bySeverity,
            listed = Math.Min(max, problems.Count),
            total = problems.Count,
            checks = OutfitCritic.Checks.Where(c => c != OutfitCritic.Moving || options.CheckMotion),
            problems = problems.Take(max).Select((p, i) => new
            {
                rank = i + 1,
                type = p.Type,
                severity = p.Severity,
                score = p.Score,
                garment = p.Garment,
                garmentId = p.GarmentId,
                piece = p.Piece,
                pieceId = p.PieceId,
                at = V(p.At),
                patternCm = new[] { MathF.Round(p.PatternCm.X, 1), MathF.Round(p.PatternCm.Y, 1) },
                vertices = p.Vertices,
                otherGarment = p.OtherGarment,
                otherPiece = p.OtherPiece,
                description = p.Description,
                measures = p.Measures,
                fix = new { tool = p.Fix.Tool, args = p.Fix.Args, note = p.Fix.Note },
                look = new { yaw = p.LookYaw, pitch = 10, focus = V(p.At), spanCm = 35 },
            }),
            note = "Severity: critical and major spoil the outfit; minor is worth fixing; hint is a matter of taste. "
                + "patternCm is in the piece's own pattern centimetres (what pull_cloth, add_patch and locate_on_piece take). "
                + "look holds render_preview arguments for a close look at the place.",
        };
    }

    public static object CompareToReference(GarmentSession session, JsonElement args)
    {
        string reference = Str(args, "referencePath") ?? throw new ArgumentException("Missing required string 'referencePath'.");
        var view = Float(args, "yaw") is { } yaw
            ? new PreviewView("custom", yaw, Float(args, "pitch") ?? 4f)
            : PreviewRenderer.Named.TryGetValue(Str(args, "view") ?? "front", out var named) ? named
            : throw new ArgumentException($"Unknown view '{Str(args, "view")}'. Use: {string.Join(", ", PreviewRenderer.Named.Keys)}.");
        Vector3? focus = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("focus", out var f) && f.ValueKind == JsonValueKind.Array && f.GetArrayLength() == 3)
            focus = new Vector3(f[0].GetSingle(), f[1].GetSingle(), f[2].GetSingle());
        float? span = Float(args, "spanCm") is { } cm ? cm * 0.01f : null;
        if (Str(args, "focusPieceId") is { Length: > 0 } pieceId)
        {
            var piece = session.Doc.FindPiece(pieceId) ?? throw new ArgumentException($"Piece '{pieceId}' not found.");
            var mesh = session.Workspace.MeshFor(session.Doc.GarmentOfPiece(pieceId)!).Find(pieceId)!;
            var positions = Seamstress.Core.Geometry.Mesher.CurrentPositions(piece, mesh);
            var lo = positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var hi = positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            focus = (lo + hi) / 2;
            span ??= MathF.Max((hi - lo).Length() * 1.5f, 0.2f);
        }
        string output = Str(args, "outputPath")
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reference))!, Path.GetFileNameWithoutExtension(reference) + "_compare.png");
        int height = Math.Clamp(Int(args, "height") ?? 640, 128, 2048);
        return ReferenceComparer.Compare(reference, output, height, (path, w, h) =>
            session.RenderDrapePreviewPng(path, w, h, new[] { view }, focus, span, Bool(args, "showAvatar") ?? true));
    }

    private static float[] V(Vector3 v) => new[] { MathF.Round(v.X, 3), MathF.Round(v.Y, 3), MathF.Round(v.Z, 3) };

    private static string? Str(JsonElement a, string k)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool? Bool(JsonElement a, string k)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static int? Int(JsonElement a, string k)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static float? Float(JsonElement a, string k)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
}
