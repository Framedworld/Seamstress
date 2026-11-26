using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Mcp;

// Internal lines, fold lines, darts and pleats for agents.
public sealed partial class McpToolRegistry
{
    private static readonly string[] FoldToolNames = { "add_internal_line", "set_fold_line", "delete_internal_line", "add_dart", "add_pleats" };

    private static JsonArray WithFoldTools(JsonArray tools)
    {
        tools.Add(Tool("add_internal_line", "Draw a line inside a pattern piece. The cloth mesh always has edges along it. With a foldAngle it is a fold line: the cloth folds there and stays folded, for a pressed crease, a turned-back cuff or a lapel roll line. Ends within about a mesh edge of the outline catch on it, so a fold can run edge to edge. Simulate afterwards to see it fold.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["pieceId"] = Prop("string", "The piece to draw on."),
                ["points"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Two or more points in the piece's local cm (as in add_piece): [{x, y, cx?, cy?}], (cx, cy) bending the stretch to the next point.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["x"] = Prop("number", "X in cm"),
                            ["y"] = Prop("number", "Y in cm"),
                            ["cx"] = Prop("number", "Optional curve control X in cm"),
                            ["cy"] = Prop("number", "Optional curve control Y in cm"),
                        },
                        ["required"] = new JsonArray("x", "y"),
                    },
                },
                ["foldAngle"] = Prop("number", "Degrees the cloth folds along the line: 0 (default) is a plain line, 90 a right angle, 180 folded right back onto itself."),
                ["valley"] = Prop("boolean", "Fold as a valley (a groove on the outside of the garment, the side the piece faces) instead of a mountain (a ridge). Default false."),
                ["strengthPercent"] = Prop("number", "How firmly the fold holds its angle, 0-100 (default 70)."),
                ["name"] = Prop("string", "Optional name, e.g. 'roll line'."),
            },
            ["required"] = new JsonArray("pieceId", "points"),
        }));
        tools.Add(Tool("set_fold_line", "Change an internal line's fold: its angle, direction or strength. A foldAngle of 0 makes it a plain line again.", Props(
            ("pieceId", "string", "The piece the line is on."),
            ("lineId", "string", "Line ID (from add_internal_line or add_pleats), or its name."),
            ("foldAngle", "number", "Degrees, 0-180."),
            ("valley", "boolean", "True for a valley fold, false for a mountain fold."),
            ("strengthPercent", "number", "0-100.")), "pieceId", "lineId"));
        tools.Add(Tool("delete_internal_line", "Remove an internal line or fold line from a piece.", Props(
            ("pieceId", "string", "The piece the line is on."),
            ("lineId", "string", "Line ID, or its name.")), "pieceId", "lineId"));
        tools.Add(Tool("add_dart", "Cut a dart into a piece: a wedge from two points on an edge to a point inside the piece is removed, and its two legs are sewn together, so the flat cloth takes shape over the bust, waist, hips or shoulder blades. Give the dart by where it sits on the edge (atCm, widthCm) and either how far it reaches in (lengthCm, square to the edge) or its point (apexX, apexY). The edge is split: edges after it are renumbered (+3) and a seam on that edge is taken off (sew the new edges again). Returns the two leg edges.", Props(
            ("pieceId", "string", "The piece to dart."),
            ("edge", "edge", "Edge the dart opens on: its 0-based number or its name."),
            ("atCm", "number", "Where the middle of the dart is, in cm along the edge from its start (default: the middle of the edge)."),
            ("widthCm", "number", "How much the dart takes in at the edge, between its legs (default 3)."),
            ("lengthCm", "number", "How far the dart reaches into the piece, square to the edge (default 10)."),
            ("apexX", "number", "Instead of lengthCm: the dart's point, local X in cm."),
            ("apexY", "number", "Instead of lengthCm: the dart's point, local Y in cm.")), "pieceId", "edge"));
        tools.Add(Tool("add_pleats", "Pleat a stretch of an edge with knife pleats: for each pleat a mountain fold line and a valley fold line run from the edge straight across the piece, the top of the pleat is tacked (the edge folded under is sewn face to face to the edge it lies on) and the fold is stitched down onto the cloth for stitchDownCm, so the edge comes in by count x 2 x depthCm. For a pleated skirt, pleat the waist edge of each panel before sewing the sides; stitched down, the pleats hold the skirt up without a waistband. The edge is split: edges after it are renumbered and a seam on that edge is taken off. Returns the fold line IDs and the depth used (reduced when the pleats would not fit).", Props(
            ("pieceId", "string", "The piece to pleat."),
            ("edge", "edge", "Edge to pleat along: its 0-based number or its name."),
            ("count", "integer", "How many pleats (default 4)."),
            ("depthCm", "number", "How much cloth each pleat folds under, in cm (default 3)."),
            ("fromCm", "number", "Where the pleated stretch starts, in cm along the edge from its start (default 0)."),
            ("toCm", "number", "Where it ends (default: the end of the edge)."),
            ("foldAngle", "number", "How far each fold turns, in degrees (default 170: pressed nearly flat)."),
            ("strengthPercent", "number", "How firmly the folds hold, 0-100 (default 80)."),
            ("tack", "boolean", "Sew the top of each pleat shut (default true)."),
            ("stitchDownCm", "number", "Stitch each pleat's fold down onto the cloth this far from the edge (default 8; 0 = tacked at the edge only). Below the stitching the pleats open. Stitched down, the pleats carry a skirt's weight; tacked at the edge only, a pleated skirt needs a waistband.")), "pieceId", "edge"));
        return tools;
    }

    private bool IsFoldTool(string name) => FoldToolNames.Contains(name);

    private object DispatchFolds(string name, JsonElement args) => name switch
    {
        "add_internal_line" => AddInternalLine(args),
        "set_fold_line" => SetFoldLine(args),
        "delete_internal_line" => DeleteInternalLine(args),
        "add_dart" => AddDartTool(args),
        "add_pleats" => AddPleatsTool(args),
        _ => throw new ArgumentException($"Unknown tool '{name}'."),
    };

    private PatternPiece PieceArg(JsonElement args)
    {
        string pieceId = ReqString(args, "pieceId");
        return _session.Doc.FindPiece(pieceId) ?? throw new ArgumentException($"Piece '{pieceId}' not found.");
    }

    private static InternalLine LineArg(PatternPiece piece, JsonElement args)
    {
        string id = ReqString(args, "lineId");
        return piece.InternalLines.FirstOrDefault(l => l.Id == id)
            ?? piece.InternalLines.FirstOrDefault(l => string.Equals(l.Name, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Piece '{piece.Name}' has no internal line '{id}'. Its lines: {string.Join(", ", piece.InternalLines.Select(l => l.Name ?? l.Id))}.");
    }

    private static object Describe(InternalLine line) => new
    {
        id = line.Id,
        name = line.Name,
        foldAngle = MathF.Round(line.FoldAngle, 1),
        valley = line.Valley,
        strengthPercent = MathF.Round(line.FoldStrength * 100),
        lengthCm = MathF.Round(line.Length(), 2),
    };

    private object AddInternalLine(JsonElement args)
    {
        var piece = PieceArg(args);
        if (!args.TryGetProperty("points", out var pts) || pts.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Missing required array 'points'.");
        var points = new List<PatternPoint>();
        foreach (var p in pts.EnumerateArray())
        {
            var point = new PatternPoint(ReqFloat(p, "x"), ReqFloat(p, "y"));
            if (TryOptFloat(p, "cx") is { } cx && TryOptFloat(p, "cy") is { } cy)
                point.Curve = new Vector2(cx, cy);
            points.Add(point);
        }
        var line = _session.Workspace.AddInternalLine(piece.Id, points, OptFloat(args, "foldAngle", 0), OptBool(args, "valley", false),
            Math.Clamp(OptFloat(args, "strengthPercent", InternalLine.DefaultFoldStrength * 100), 0, 100) / 100f);
        if (OptString(args, "name") is { Length: > 0 } name)
            _session.Doc.FindPiece(piece.Id)!.InternalLines.First(l => l.Id == line.Id).Name = name;
        var saved = _session.Doc.FindPiece(piece.Id)!.InternalLines.First(l => l.Id == line.Id);
        var mesh = _session.Workspace.MeshFor(_session.Doc.GarmentOfPiece(piece.Id)!).Find(piece.Id);
        int index = _session.Doc.FindPiece(piece.Id)!.InternalLines.IndexOf(saved);
        int vertices = mesh != null && index < mesh.LineChains.Length ? mesh.LineChains[index].Length : 0;
        return new
        {
            ok = true,
            line = Describe(saved),
            meshVertices = vertices,
            note = vertices < 2 ? "No part of the line is on the cloth, so the mesh does not follow it." : null,
        };
    }

    private object SetFoldLine(JsonElement args)
    {
        var piece = PieceArg(args);
        var line = LineArg(piece, args);
        float? strength = TryOptFloat(args, "strengthPercent") is { } s ? Math.Clamp(s, 0, 100) / 100f : null;
        bool? valley = args.TryGetProperty("valley", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        _session.Workspace.SetInternalLineFold(piece.Id, line.Id, TryOptFloat(args, "foldAngle"), valley, strength);
        return new { ok = true, line = Describe(_session.Doc.FindPiece(piece.Id)!.InternalLines.First(l => l.Id == line.Id)) };
    }

    private object DeleteInternalLine(JsonElement args)
    {
        var piece = PieceArg(args);
        var line = LineArg(piece, args);
        _session.Workspace.DeleteInternalLine(piece.Id, line.Id);
        return new { ok = true, deleted = line.Id, remaining = _session.Doc.FindPiece(piece.Id)!.InternalLines.Count };
    }

    private object AddDartTool(JsonElement args)
    {
        var piece = PieceArg(args);
        int edge = ResolveEdge(piece, args, "edge");
        float length = Outline.EdgeLength(piece, edge);
        float at = Math.Clamp(OptFloat(args, "atCm", length / 2), 0, length);
        float width = MathF.Max(OptFloat(args, "widthCm", 3), 0.2f);
        float fa = (at - width / 2) / length, fb = (at + width / 2) / length;
        if (fa <= 0 || fb >= 1)
            throw new ArgumentException($"A {width:0.#} cm dart at {at:0.#} cm does not fit on the {length:0.#} cm edge.");
        var legA = PatternFolds.PointAlong(piece, edge, fa);
        var legB = PatternFolds.PointAlong(piece, edge, fb);
        Vector2 apex = TryOptFloat(args, "apexX") is { } ax && TryOptFloat(args, "apexY") is { } ay
            ? new Vector2(ax, ay)
            : PatternFolds.PointAlong(piece, edge, at / length) + PatternFolds.Inward(piece, edge, at / length) * OptFloat(args, "lengthCm", 10);
        var result = _session.Workspace.AddDart(piece.Id, edge, legA, legB, apex);
        return new
        {
            ok = true,
            legEdges = new[] { result.LegA, result.LegB },
            seamId = result.Seam.Id,
            apex = new[] { MathF.Round(apex.X, 2), MathF.Round(apex.Y, 2) },
            edgeCount = _session.Doc.FindPiece(piece.Id)!.EdgeCount,
            removedSeams = result.RemovedSeams,
            note = result.RemovedSeams > 0 ? "The seam on the darted edge was taken off: sew its parts again." : null,
        };
    }

    private object AddPleatsTool(JsonElement args)
    {
        var piece = PieceArg(args);
        int edge = ResolveEdge(piece, args, "edge");
        float length = Outline.EdgeLength(piece, edge);
        float from = Math.Clamp(OptFloat(args, "fromCm", 0), 0, length) / length;
        float to = Math.Clamp(OptFloat(args, "toCm", length), 0, length) / length;
        var result = _session.Workspace.AddPleats(piece.Id, edge, from, to, OptInt(args, "count", PatternEditor.DefaultPleatCount),
            OptFloat(args, "depthCm", PatternEditor.DefaultPleatDepth), OptFloat(args, "foldAngle", PatternFolds.DefaultPleatFoldAngle),
            Math.Clamp(OptFloat(args, "strengthPercent", PatternFolds.DefaultPleatStrength * 100), 0, 100) / 100f, OptBool(args, "tack", true),
            OptFloat(args, "stitchDownCm", PatternFolds.DefaultPleatStitchDown));
        return new
        {
            ok = true,
            depthCm = MathF.Round(result.Depth, 2),
            takenInCm = MathF.Round(result.Lines.Count * result.Depth, 2),
            lines = result.Lines.Select(Describe).ToList(),
            stitchedDownCm = MathF.Round(result.StitchDown, 2),
            placementLines = result.Placements.Select(l => l.Id).ToList(),
            tackSeams = result.Tacks.Select(t => t.Id).ToList(),
            edgeCount = _session.Doc.FindPiece(piece.Id)!.EdgeCount,
            removedSeams = result.RemovedSeams,
            note = result.RemovedSeams > 0 ? "The seam on the pleated edge was taken off: sew its parts again." : null,
        };
    }
}
