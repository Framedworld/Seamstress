using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Editing;

/// <summary>
/// Internal lines, fold lines, darts and pleats, as edits on a garment's pattern. These work on the document handed
/// to them (the workspace wraps each one in an undo step).
/// </summary>
public static class PatternFolds
{
    public const float DefaultPleatFoldAngle = 170f;
    public const float DefaultPleatStrength = 0.8f;

    /// <summary>
    /// How far down from the edge each pleat is stitched shut unless told otherwise (cm). Below a tack along the edge
    /// alone a pleat opens under the slightest pull, so the edge's single row of stitches carried the whole garment
    /// and stretched until a pleated skirt slid off the hips; stitched down, a band of cloth carries it.
    /// </summary>
    public const float DefaultPleatStitchDown = 8f;
    public const int MaxPleats = 40;

    public sealed record DartResult(Seam Seam, int LegA, int LegB, int RemovedSeams);

    /// <summary>
    /// <see cref="Lines"/> are the fold lines (each pleat's mountain fold, then its valley fold); <see cref="Placements"/>
    /// are the lines each fold is stitched down onto, when the pleats are stitched down.
    /// </summary>
    public sealed record PleatResult(List<InternalLine> Lines, List<Seam> Tacks, float Depth, int RemovedSeams, List<InternalLine> Placements, float StitchDown);

    /// <summary>True when a local point is on the cloth: inside the outline and in none of the holes.</summary>
    public static bool OnCloth(PatternPiece piece, Vector2 local)
        => Outline.Contains(piece, local) && !Outline.FlattenHoles(piece).Any(h => Outline.Contains(h, local));

    /// <summary>Where along an edge (0 at its start, 1 at its end, by length) the point nearest to <paramref name="local"/> is.</summary>
    public static float EdgeFraction(PatternPiece piece, int edge, Vector2 local)
    {
        var line = Outline.FlattenEdge(piece, edge);
        float total = 0, best = float.MaxValue, bestAt = 0;
        for (int i = 1; i < line.Count; i++)
        {
            float d = Outline.DistanceToSegment(local, line[i - 1], line[i], out float t);
            float length = Vector2.Distance(line[i - 1], line[i]);
            if (d < best)
            {
                best = d;
                bestAt = total + t * length;
            }
            total += length;
        }
        return total > 1e-9f ? Math.Clamp(bestAt / total, 0, 1) : 0;
    }

    /// <summary>The point a fraction of the way along an edge, by length.</summary>
    public static Vector2 PointAlong(PatternPiece piece, int edge, float fraction)
    {
        var line = Outline.FlattenEdge(piece, edge);
        float total = 0;
        for (int i = 1; i < line.Count; i++)
            total += Vector2.Distance(line[i - 1], line[i]);
        float target = Math.Clamp(fraction, 0, 1) * total;
        for (int i = 1; i < line.Count; i++)
        {
            float length = Vector2.Distance(line[i - 1], line[i]);
            if (target <= length || i == line.Count - 1)
                return Vector2.Lerp(line[i - 1], line[i], length > 1e-9f ? Math.Clamp(target / length, 0, 1) : 0);
            target -= length;
        }
        return line[^1];
    }

    /// <summary>The direction into the cloth from an edge, at a fraction along it: the cloth is on the left of every edge.</summary>
    public static Vector2 Inward(PatternPiece piece, int edge, float fraction)
    {
        var a = PointAlong(piece, edge, Math.Max(0, fraction - 0.01f));
        var b = PointAlong(piece, edge, Math.Min(1, fraction + 0.01f));
        var d = b - a;
        if (d.LengthSquared() < 1e-12f)
            d = Outline.EdgeEnd(piece, edge) - Outline.EdgeStart(piece, edge);
        var n = new Vector2(-d.Y, d.X);
        return n.LengthSquared() > 1e-12f ? Vector2.Normalize(n) : Vector2.UnitY;
    }

    /// <summary>
    /// Splits an edge at the given fractions of its length (each strictly between 0 and 1), keeping its curve.
    /// Returns the numbers of the new points, in order along the edge. Seams and surface stitches on later edges are
    /// renumbered; a seam on the split edge itself is taken off (it would only cover the first part), and so is a
    /// surface stitch from it. Returns how many were taken off through <paramref name="removed"/>.
    /// </summary>
    public static int[] SplitEdge(Garment garment, PatternPiece piece, int edge, IReadOnlyList<float> fractions, out int removed)
    {
        var sorted = fractions.Where(f => f > 1e-4f && f < 1 - 1e-4f).OrderBy(f => f).ToList();
        var (loop, start) = piece.LoopOf(edge);
        int at = edge - start;
        var end = loop[(at + 1) % loop.Count].Position;
        float done = 0;
        var created = new List<int>();
        foreach (float f in sorted)
        {
            if (f - done < 1e-4f)
                continue;
            float relative = (f - done) / (1 - done);
            var current = loop[at];
            var (first, middle, second) = Inset.SplitEdge(current.Position, current.Curve, end, relative);
            current.Curve = first;
            loop.Insert(at + 1, new PatternPoint { Position = middle, Curve = second });
            at++;
            created.Add(start + at);
            done = f;
        }
        removed = 0;
        int n = created.Count;
        if (n == 0)
            return Array.Empty<int>();
        removed += garment.Seams.RemoveAll(s => s.A.Matches(piece.Id, edge) || s.B.Matches(piece.Id, edge));
        removed += garment.SurfaceStitches.RemoveAll(s => s.SourcePieceId == piece.Id && s.SourceEdge == edge);
        foreach (var seam in garment.Seams)
        {
            if (seam.A.PieceId == piece.Id && seam.A.Edge > edge)
                seam.A.Edge += n;
            if (seam.B.PieceId == piece.Id && seam.B.Edge > edge)
                seam.B.Edge += n;
        }
        foreach (var stitch in garment.SurfaceStitches)
        {
            if (stitch.SourcePieceId == piece.Id && stitch.SourceEdge > edge)
                stitch.SourceEdge += n;
        }
        return created.ToArray();
    }

    /// <summary>
    /// Adds an internal line through <paramref name="points"/> (local centimetres, at least two). A fold angle above 0
    /// makes it a fold line. Throws <see cref="ArgumentException"/> when the line is not on the piece.
    /// </summary>
    public static InternalLine AddLine(PatternPiece piece, IReadOnlyList<PatternPoint> points, float foldAngle = 0, bool valley = false,
        float strength = InternalLine.DefaultFoldStrength)
    {
        if (points.Count < 2)
            throw new ArgumentException(Lang.T("A line needs at least two points."));
        var line = new InternalLine
        {
            Points = points.Select(p => p.Clone()).ToList(),
            FoldAngle = Math.Clamp(float.IsFinite(foldAngle) ? foldAngle : 0, 0, InternalLine.MaxFoldAngle),
            Valley = valley,
            FoldStrength = Math.Clamp(float.IsFinite(strength) ? strength : InternalLine.DefaultFoldStrength, 0, 1),
        };
        line.Points[^1].Curve = null;
        if (line.Length() < 0.1f)
            throw new ArgumentException(Lang.T("The line is too short."));
        // Some of it has to be on the cloth (it may end on the outline, or just past it).
        var flat = line.Flatten();
        bool onCloth = false;
        for (int i = 0; i < flat.Count && !onCloth; i++)
        {
            var mid = i + 1 < flat.Count ? (flat[i] + flat[i + 1]) / 2 : flat[i];
            onCloth = OnCloth(piece, mid) || OnCloth(piece, flat[i]);
        }
        if (!onCloth)
            throw new ArgumentException(Lang.T("The line has to be drawn on the piece."));
        piece.InternalLines.Add(line);
        return line;
    }

    /// <summary>
    /// Adds a dart: the wedge from <paramref name="legA"/> to <paramref name="apex"/> to <paramref name="legB"/> is
    /// cut out of the piece (both leg ends on <paramref name="edge"/>), and its two legs are sewn together, so the
    /// cloth takes a three-dimensional shape around the apex.
    /// </summary>
    public static DartResult AddDart(Garment garment, PatternPiece piece, int edge, Vector2 legA, Vector2 legB, Vector2 apex)
    {
        if (edge < 0 || edge >= piece.EdgeCount)
            throw new ArgumentException(Lang.T("No such edge."));
        if (!OnCloth(piece, apex))
            throw new ArgumentException(Lang.T("The point of the dart has to be inside the piece."));
        float fa = EdgeFraction(piece, edge, legA), fb = EdgeFraction(piece, edge, legB);
        if (fa > fb)
            (fa, fb) = (fb, fa);
        fa = Math.Clamp(fa, 0.002f, 0.998f);
        fb = Math.Clamp(fb, 0.002f, 0.998f);
        float length = Outline.EdgeLength(piece, edge);
        if ((fb - fa) * length < 0.2f)
            throw new ArgumentException(Lang.T("The dart's legs are too close together; click two different points on the edge."));
        var a = PointAlong(piece, edge, fa);
        var b = PointAlong(piece, edge, fb);
        if (Vector2.Distance(apex, (a + b) / 2) < 0.5f)
            throw new ArgumentException(Lang.T("The dart is too short."));

        // Try the new outline before changing anything: the legs must not cross the rest of it.
        var trial = piece.Clone();
        var trialGarment = new Garment { Pieces = { trial } };
        InsertDart(trialGarment, trial, edge, fa, fb, apex, out _);
        var outline = Outline.Flatten(trial);
        if (!Outline.IsSimple(outline) || Outline.SignedArea(outline) <= 0
            || Outline.FlattenHoles(trial).Any(h => Outline.Crosses(outline, h)))
            throw new ArgumentException(Lang.T("The dart would cross the piece's outline; put its point further inside."));

        var (legEdgeA, legEdgeB) = InsertDart(garment, piece, edge, fa, fb, apex, out int removed);
        var seam = new Seam { A = new EdgeRef(piece.Id, legEdgeA), B = new EdgeRef(piece.Id, legEdgeB) };
        garment.Seams.Add(seam);
        piece.ClearDrape();
        return new DartResult(seam, legEdgeA, legEdgeB, removed);
    }

    private static (int LegA, int LegB) InsertDart(Garment garment, PatternPiece piece, int edge, float fa, float fb, Vector2 apex, out int removed)
    {
        var made = SplitEdge(garment, piece, edge, new[] { fa, fb }, out removed);
        int legStart = made[0];
        var (loop, start) = piece.LoopOf(legStart);
        // The stretch between the leg ends is cut out: straight legs to the apex instead.
        loop[legStart - start].Curve = null;
        loop.Insert(legStart - start + 1, new PatternPoint { Position = apex });
        foreach (var seam in garment.Seams)
        {
            if (seam.A.PieceId == piece.Id && seam.A.Edge > legStart)
                seam.A.Edge++;
            if (seam.B.PieceId == piece.Id && seam.B.Edge > legStart)
                seam.B.Edge++;
        }
        foreach (var stitch in garment.SurfaceStitches)
        {
            if (stitch.SourcePieceId == piece.Id && stitch.SourceEdge > legStart)
                stitch.SourceEdge++;
        }
        return (legStart, legStart + 1);
    }

    /// <summary>
    /// Adds knife pleats along an edge, between <paramref name="from"/> and <paramref name="to"/> (fractions of the
    /// edge's length): <paramref name="count"/> pleats, each folding <paramref name="depth"/> centimetres of cloth
    /// under. Each pleat is a mountain fold line and a valley fold line running from the edge across the piece; with
    /// <paramref name="tack"/> its top is sewn shut, so the fold sits on the cloth beside it, and with
    /// <paramref name="stitchDown"/> the fold is stitched onto the cloth that far down from the edge. The depth is reduced
    /// when the pleats would not fit; the result gives the depth used.
    /// </summary>
    public static PleatResult AddPleats(Garment garment, PatternPiece piece, int edge, float from, float to, int count, float depth,
        float foldAngle = DefaultPleatFoldAngle, float strength = DefaultPleatStrength, bool tack = true, float stitchDown = DefaultPleatStitchDown)
    {
        if (edge < 0 || edge >= piece.EdgeCount)
            throw new ArgumentException(Lang.T("No such edge."));
        if (count < 1 || count > MaxPleats)
            throw new ArgumentException(Lang.F("Choose between 1 and {0} pleats.", MaxPleats));
        if (from > to)
            (from, to) = (to, from);
        from = Math.Clamp(from, 0, 1);
        to = Math.Clamp(to, 0, 1);
        float length = Outline.EdgeLength(piece, edge);
        float span = (to - from) * length;
        float pitch = span / count;
        depth = MathF.Min(depth, pitch * 0.45f);
        if (!float.IsFinite(depth) || depth < 0.2f)
            throw new ArgumentException(Lang.T("The pleats do not fit: make the stretch longer, or have fewer of them."));

        // Each pleat sits in the middle of its share of the stretch: its fold, where it turns back, and where the
        // fold comes to rest.
        var marks = new List<(float Fold, float Under, float Settle)>();
        for (int i = 0; i < count; i++)
        {
            float s = from * length + i * pitch + (pitch - 2 * depth) / 2;
            marks.Add((s / length, (s + depth) / length, (s + 2 * depth) / length));
        }
        // The folds run across the piece from the edge, square to it, as the edge is now.
        var folds = marks.Select(m => (Fold: Across(piece, edge, m.Fold), Under: Across(piece, edge, m.Under), Settle: Across(piece, edge, m.Settle))).ToList();
        if (folds.Any(f => f.Fold == null || f.Under == null || f.Settle == null))
            throw new ArgumentException(Lang.T("The pleats have to run across the piece from the edge."));

        var made = SplitEdge(garment, piece, edge, marks.SelectMany(m => new[] { m.Fold, m.Under, m.Settle }).ToList(), out int removed);
        if (made.Length != 3 * count)
            throw new ArgumentException(Lang.T("The pleats are too close together."));
        var lines = new List<InternalLine>();
        var placements = new List<InternalLine>();
        var tacks = new List<Seam>();
        stitchDown = tack && float.IsFinite(stitchDown) ? Math.Max(0, stitchDown) : 0;
        for (int i = 0; i < count; i++)
        {
            var fold = InternalLine.Straight(folds[i].Fold!.Value.A, folds[i].Fold!.Value.B, foldAngle, false, strength);
            fold.Name = Lang.F("Pleat {0} fold", i + 1);
            var under = InternalLine.Straight(folds[i].Under!.Value.A, folds[i].Under!.Value.B, foldAngle, true, strength);
            under.Name = Lang.F("Pleat {0} under", i + 1);
            piece.InternalLines.Add(fold);
            piece.InternalLines.Add(under);
            lines.Add(fold);
            lines.Add(under);
            if (tack)
            {
                // The stretch of edge folded under is sewn face to face with the stretch that comes back over it.
                var seam = new Seam { A = new EdgeRef(piece.Id, made[3 * i]), B = new EdgeRef(piece.Id, made[3 * i + 1]), Folded = true };
                garment.Seams.Add(seam);
                tacks.Add(seam);
            }
            if (stitchDown > 0)
            {
                // Stitched down: the fold is sewn onto the line where it comes to rest, from the edge down.
                var settle = InternalLine.Straight(folds[i].Settle!.Value.A, folds[i].Settle!.Value.B);
                settle.Name = Lang.F("Pleat {0} placement", i + 1);
                fold.TackLineId = settle.Id;
                fold.TackLength = MathF.Min(stitchDown, 0.5f * Math.Min(fold.Length(), settle.Length()));
                piece.InternalLines.Add(settle);
                placements.Add(settle);
            }
        }
        piece.ClearDrape();
        return new PleatResult(lines, tacks, depth, removed, placements, placements.Count > 0 ? lines[0].TackLength : 0);
    }

    /// <summary>The line from the edge at a fraction along it straight into the piece, to where it leaves the cloth.</summary>
    private static (Vector2 A, Vector2 B)? Across(PatternPiece piece, int edge, float fraction)
    {
        var start = PointAlong(piece, edge, fraction);
        var dir = Inward(piece, edge, fraction);
        float? nearest = null;
        foreach (var (loop, loopStart) in piece.Loops())
        {
            for (int e = loopStart; e < loopStart + loop.Count; e++)
            {
                var poly = Outline.FlattenEdge(piece, e);
                for (int k = 1; k < poly.Count; k++)
                {
                    if (RayHit(start, dir, poly[k - 1], poly[k]) is { } t && t > 0.05f && (nearest == null || t < nearest))
                        nearest = t;
                }
            }
        }
        return nearest is { } distance ? (start, start + dir * distance) : null;
    }

    private static float? RayHit(Vector2 origin, Vector2 dir, Vector2 a, Vector2 b)
    {
        var e = b - a;
        float den = dir.X * e.Y - dir.Y * e.X;
        if (MathF.Abs(den) < 1e-9f)
            return null;
        var w = a - origin;
        float t = (w.X * e.Y - w.Y * e.X) / den;
        float u = (w.X * dir.Y - w.Y * dir.X) / den;
        return u >= -1e-5f && u <= 1 + 1e-5f ? t : null;
    }

    /// <summary>Moves every internal line of a piece the way its outline was moved.</summary>
    public static void TransformLines(PatternPiece piece, Func<Vector2, Vector2> map)
    {
        foreach (var line in piece.InternalLines)
        {
            foreach (var point in line.Points)
            {
                point.Position = map(point.Position);
                if (point.Curve is { } c)
                    point.Curve = map(c);
            }
        }
    }

    /// <summary>The internal line of a piece nearest to <paramref name="local"/>, if one is within <paramref name="tolerance"/>.</summary>
    public static InternalLine? HitLine(PatternPiece piece, Vector2 local, float tolerance)
    {
        InternalLine? best = null;
        float bestD = tolerance;
        foreach (var line in piece.InternalLines)
        {
            float d = Outline.DistanceToPolyline(local, line.Flatten(), false);
            if (d <= bestD)
            {
                bestD = d;
                best = line;
            }
        }
        return best;
    }
}
