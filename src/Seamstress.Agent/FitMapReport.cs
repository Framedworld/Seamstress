using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Mcp;

/// <summary>
/// What the <c>fit_map</c> tool reports: the numbers behind the 3D view's fit maps (<see cref="FitMaps"/>) for each
/// garment, with its worst places named on the body and on the pattern, so an agent can judge a fit without a picture.
/// </summary>
public static class FitMapReport
{
    /// <summary>One map, or all three when <paramref name="kind"/> is <see cref="FitMapKind.Off"/>.</summary>
    public static object Build(Document doc, Func<Garment, GarmentMesh> meshFor, Avatar? avatar, FitMapKind kind, string? garmentId,
        int worst = 5)
    {
        var kinds = kind == FitMapKind.Off ? new[] { FitMapKind.Stretch, FitMapKind.Pressure, FitMapKind.Distance } : new[] { kind };
        var maps = new Dictionary<string, object>();
        foreach (var k in kinds)
            maps[FitMaps.Name(k)] = FitMaps.Summarize(doc, meshFor, avatar, k, garmentId, worst).Select(GarmentJson).ToList();
        return new
        {
            hasAvatar = avatar != null,
            maps,
            legend = new
            {
                stretch = "Mean strain of each vertex's edges against the pattern (after the fabric's shrink): 0.10 = 10% longer. "
                    + "Over: share above +5%; under: share compressed by more than 5%.",
                pressure = "0 = not touching, 0.25 = touching but slack, 1 = touching and stretched 10% or more (or squeezed inside its "
                    + "contact distance). Over: share above 0.6 (tight); under: share not touching.",
                distance = "Centimetres of air between the cloth and the body, beyond the room the garment keeps (its thickness and room "
                    + "underneath). Over: share looser than 2 cm; under: share closer than 0.5 cm (touching).",
                worst = "The highest values, at least 8 cm apart (on the stretch map, strongly compressed places rank too). "
                    + "patternCm is in the piece's local pattern space, as locate_on_piece takes it; position is in metres.",
            },
            note = avatar == null ? "No avatar: the pressure and distance maps have nothing to measure against." : null,
        };
    }

    private static object GarmentJson(GarmentFitSummary s) => new
    {
        garmentId = s.GarmentId,
        garment = s.GarmentName,
        vertices = s.Vertices,
        min = Round(s.Min),
        max = Round(s.Max),
        mean = Round(s.Mean),
        overShare = MathF.Round(s.ShareOver, 3),
        underShare = MathF.Round(s.ShareUnder, 3),
        worst = s.Worst.Select(w => new
        {
            pieceId = w.PieceId,
            piece = w.PieceName,
            value = Round(w.Value),
            region = w.Region,
            position = new[] { MathF.Round(w.Position.X, 3), MathF.Round(w.Position.Y, 3), MathF.Round(w.Position.Z, 3) },
            patternCm = new[] { MathF.Round(w.PatternCm.X, 1), MathF.Round(w.PatternCm.Y, 1) },
        }).ToList(),
    };

    private static float Round(float v) => float.IsFinite(v) ? MathF.Round(v, 4) : -1f;
}
