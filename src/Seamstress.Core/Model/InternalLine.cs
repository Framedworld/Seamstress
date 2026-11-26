using System.Numerics;

namespace Seamstress.Core.Model;

/// <summary>
/// A line drawn inside a pattern piece, in the piece's local centimetres. The cloth mesh always has edges along it,
/// so it can carry a fold: a pressed crease, a turned-back cuff, a lapel roll line or the folds of a pleat. With no
/// fold angle it is only a line the mesh follows (a placement line, or where a seam will be marked).
/// </summary>
public sealed class InternalLine
{
    /// <summary>Largest fold angle in degrees: the cloth folded right back onto itself.</summary>
    public const float MaxFoldAngle = 180f;

    /// <summary>How firmly a fold line holds its angle unless told otherwise, 0 to 1.</summary>
    public const float DefaultFoldStrength = 0.7f;

    public string Id { get; set; } = Document.NewId();

    /// <summary>Optional name ("roll line", "pleat 2 fold"...).</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The points the line goes through, at least two. <see cref="PatternPoint.Curve"/> bends the stretch from a point
    /// to the next one, as on an outline; the last point's curve is ignored, since the line is open.
    /// </summary>
    public List<PatternPoint> Points { get; set; } = new();

    /// <summary>
    /// How far the cloth folds along the line, in degrees: 0 leaves it flat (a plain line), 90 is a right angle and
    /// 180 folds the cloth right back onto itself, like a pressed pleat.
    /// </summary>
    public float FoldAngle { get; set; }

    /// <summary>
    /// Which way it folds. A mountain fold (false) makes a ridge on the outside of the garment, the side the piece's
    /// faces point to; a valley fold (true) makes a groove there, the ridge pointing in.
    /// </summary>
    public bool Valley { get; set; }

    /// <summary>How firmly the fold holds its angle, 0 (as stiff as the fabric anyway) to 1 (pressed hard).</summary>
    public float FoldStrength { get; set; } = DefaultFoldStrength;

    /// <summary>
    /// Another line of the same piece this one is stitched to, from both their starts for <see cref="TackLength"/>
    /// centimetres: a pleat stitched down from the waist, its fold sewn onto the line it lies on. Null for none.
    /// </summary>
    public string? TackLineId { get; set; }

    /// <summary>How far down from their starts this line and <see cref="TackLineId"/> are stitched together (cm).</summary>
    public float TackLength { get; set; }

    public bool IsFold => FoldAngle > 0.5f;

    public InternalLine Clone() => new()
    {
        Id = Id,
        Name = Name,
        Points = Points.Select(p => p.Clone()).ToList(),
        FoldAngle = FoldAngle,
        Valley = Valley,
        FoldStrength = FoldStrength,
        TackLineId = TackLineId,
        TackLength = TackLength,
    };

    /// <summary>A straight line from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static InternalLine Straight(Vector2 a, Vector2 b, float foldAngle = 0, bool valley = false, float strength = DefaultFoldStrength) => new()
    {
        Points = { new PatternPoint { Position = a }, new PatternPoint { Position = b } },
        FoldAngle = Math.Clamp(foldAngle, 0, MaxFoldAngle),
        Valley = valley,
        FoldStrength = Math.Clamp(strength, 0, 1),
    };

    /// <summary>Where the stretch starting at point <paramref name="segment"/> is at <paramref name="t"/> (0-1).</summary>
    public Vector2 SegmentPoint(int segment, float t)
    {
        var a = Points[segment].Position;
        var b = Points[segment + 1].Position;
        if (Points[segment].Curve is not { } c)
            return Vector2.Lerp(a, b, t);
        float u = 1 - t;
        return u * u * a + 2 * u * t * c + t * t * b;
    }

    /// <summary>The line as a polyline, each curved stretch split into short straight pieces.</summary>
    public List<Vector2> Flatten(int curveSteps = 16)
    {
        var result = new List<Vector2>();
        if (Points.Count == 0)
            return result;
        result.Add(Points[0].Position);
        for (int s = 0; s + 1 < Points.Count; s++)
        {
            int steps = Points[s].Curve.HasValue ? curveSteps : 1;
            for (int k = 1; k <= steps; k++)
                result.Add(SegmentPoint(s, k / (float)steps));
        }
        return result;
    }

    public float Length()
    {
        var line = Flatten();
        float length = 0;
        for (int i = 1; i < line.Count; i++)
            length += Vector2.Distance(line[i - 1], line[i]);
        return length;
    }

    /// <summary>
    /// Points along the line no further apart than <paramref name="spacing"/>, both ends and every corner included,
    /// spread evenly along each stretch.
    /// </summary>
    public List<Vector2> Sample(float spacing)
    {
        var result = new List<Vector2>();
        if (Points.Count < 2)
            return result;
        spacing = MathF.Max(spacing, 1e-3f);
        result.Add(Points[0].Position);
        for (int s = 0; s + 1 < Points.Count; s++)
        {
            // Measure the stretch on a fine polyline, then step along it evenly by arc length.
            var fine = new List<Vector2> { Points[s].Position };
            int steps = Points[s].Curve.HasValue ? 32 : 1;
            for (int k = 1; k <= steps; k++)
                fine.Add(SegmentPoint(s, k / (float)steps));
            var cumulative = new float[fine.Count];
            for (int k = 1; k < fine.Count; k++)
                cumulative[k] = cumulative[k - 1] + Vector2.Distance(fine[k - 1], fine[k]);
            float length = cumulative[^1];
            int n = Math.Max(1, (int)MathF.Ceiling(length / spacing - 1e-3f));
            int j = 1;
            for (int k = 1; k < n; k++)
            {
                float target = length * k / n;
                while (j < fine.Count - 1 && cumulative[j] < target)
                    j++;
                float span = cumulative[j] - cumulative[j - 1];
                float f = span > 1e-9f ? (target - cumulative[j - 1]) / span : 0;
                result.Add(Vector2.Lerp(fine[j - 1], fine[j], f));
            }
            result.Add(Points[s + 1].Position);
        }
        return result;
    }
}
