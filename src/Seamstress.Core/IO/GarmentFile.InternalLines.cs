using System.Text.Json;
using Seamstress.Core.Model;

namespace Seamstress.Core.IO;

// Internal lines (fold lines, pleat folds, plain lines the mesh follows) in the garment file:
//   "internalLines": [ { "id": "...", "points": [ { "at": [x, y] }, ... ], "foldAngle": 170, "valley": true,
//                        "foldStrength": 0.7, "name": "...", "tackTo": "<line id>", "tackLength": 8 } ]
// Points are written like outline points; "curve" bends the stretch to the next point.
public static partial class GarmentFile
{
    private static void WriteInternalLines(Utf8JsonWriter w, IReadOnlyList<InternalLine> lines, Func<PatternPoint, string> point)
    {
        w.WritePropertyName("internalLines");
        w.WriteRawValue(Lines(lines.Select(line =>
        {
            var parts = new List<string> { $"\"id\": {Quote(line.Id)}" };
            if (!string.IsNullOrEmpty(line.Name))
                parts.Add($"\"name\": {Quote(line.Name)}");
            parts.Add($"\"points\": [{string.Join(", ", line.Points.Select(point))}]");
            if (line.IsFold)
            {
                parts.Add($"\"foldAngle\": {N(line.FoldAngle)}");
                if (line.Valley)
                    parts.Add("\"valley\": true");
                parts.Add($"\"foldStrength\": {N(line.FoldStrength)}");
            }
            if (line.TackLineId != null && line.TackLength > 0)
            {
                parts.Add($"\"tackTo\": {Quote(line.TackLineId)}");
                parts.Add($"\"tackLength\": {N(line.TackLength)}");
            }
            return "{ " + string.Join(", ", parts) + " }";
        }), 6));
    }

    private static void ReadInternalLines(JsonElement p, PatternPiece piece, Func<JsonElement, PatternPoint> readPoint)
    {
        foreach (var l in Items(p, "internalLines"))
        {
            var line = new InternalLine();
            line.Id = Str(l, "id") ?? line.Id;
            line.Name = Str(l, "name");
            foreach (var pt in Items(l, "points"))
                line.Points.Add(readPoint(pt));
            // A line needs two points; one with fewer has nothing for the mesh to follow.
            if (line.Points.Count < 2)
                continue;
            line.FoldAngle = Math.Clamp(Finite(Num(l, "foldAngle", 0), 0), 0, InternalLine.MaxFoldAngle);
            line.Valley = Bool(l, "valley", false);
            line.FoldStrength = Math.Clamp(Finite(Num(l, "foldStrength", InternalLine.DefaultFoldStrength), InternalLine.DefaultFoldStrength), 0, 1);
            line.TackLineId = Str(l, "tackTo");
            line.TackLength = Math.Max(0, Finite(Num(l, "tackLength", 0), 0));
            piece.InternalLines.Add(line);
        }
    }
}
