using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.Editing;

/// <summary>How <see cref="PatternEasing"/> eases a garment.</summary>
public sealed class EaseOptions
{
    /// <summary>Stretch across the cloth that is fine as it is (0.02 = 2%); only more than this is eased.</summary>
    public float TargetStrain { get; set; } = 0.02f;

    /// <summary>How much of the measured correction to make in one go (0..1). Under 1, so that repeating settles.</summary>
    public float Strength { get; set; } = 0.6f;

    /// <summary>The most a row of a piece may grow in one go (0.25 = 25% wider).</summary>
    public float MaxGrowth { get; set; } = 0.25f;

    /// <summary>Also ease pieces drawn to pull in (elastic across below 1: waistbands, cuffs), which are tight on purpose.</summary>
    public bool IncludeElastic { get; set; }

    /// <summary>Only these pieces; null for every visible piece of the garment.</summary>
    public IReadOnlyCollection<string>? PieceIds { get; set; }

    /// <summary>The height of the rows the cloth is measured in, in pattern centimetres.</summary>
    public float RowCm { get; set; } = 3f;
}

/// <summary>An outline edge's length before and after easing, in centimetres.</summary>
public sealed record EdgeChange(int Edge, string? Name, float BeforeCm, float AfterCm);

/// <summary>A seam whose two edges' lengths drift apart by easing, in centimetres (A and B as the seam names them).</summary>
public sealed record SeamChange(string SeamId, string PieceA, int EdgeA, string PieceB, int EdgeB, float BeforeA, float BeforeB, float AfterA, float AfterB)
{
    public float MismatchBefore => MathF.Abs(BeforeA - BeforeB);
    public float MismatchAfter => MathF.Abs(AfterA - AfterB);
}

/// <summary>
/// One piece's easing: it is widened about the middle of each row of the pattern by a factor that varies smoothly
/// from row to row (<see cref="Rows"/>), so only where the cloth is too tight. <see cref="Map"/> and
/// <see cref="Unmap"/> take a pattern point to its eased place and back.
/// </summary>
public sealed class PieceEase
{
    public required string PieceId { get; init; }
    public required string PieceName { get; init; }
    /// <summary>Why the piece is left as it is, or null when it is eased (or has nothing to ease).</summary>
    public string? Skipped { get; init; }
    /// <summary>The most stretch across the piece measured in any row (mean of the row).</summary>
    public float MaxRowStrain { get; init; }
    /// <summary>The most width added to any row, in centimetres, and the pattern height of that row.</summary>
    public float MostAddedCm { get; init; }
    public float AtYCm { get; init; }
    /// <summary>The most stretch along the piece (mean of a column), reported but not eased: lengthening moves seams.</summary>
    public float MaxColumnStrain { get; init; }
    public float RowStart { get; init; }
    public float RowHeight { get; init; }
    /// <summary>Per row: the middle the row is widened about (pattern x) and how much wider it gets (1 = unchanged).</summary>
    public (float Centre, float Scale)[] Rows { get; init; } = Array.Empty<(float, float)>();
    public List<EdgeChange> Edges { get; } = new();

    /// <summary>The piece has set creases, which easing clears: set on the stretched cloth, they would keep it stretched.</summary>
    public bool HasCreases { get; init; }

    public bool Changes => Skipped == null && Rows.Any(r => r.Scale > 1.0005f);

    private (float Centre, float Scale) At(float y)
    {
        if (Rows.Length == 0)
            return (0, 1);
        float f = (y - RowStart) / RowHeight - 0.5f;
        if (f <= 0)
            return Rows[0];
        if (f >= Rows.Length - 1)
            return Rows[^1];
        int i = (int)f;
        float t = f - i;
        return (Rows[i].Centre + (Rows[i + 1].Centre - Rows[i].Centre) * t, Rows[i].Scale + (Rows[i + 1].Scale - Rows[i].Scale) * t);
    }

    /// <summary>Where a point of the drawn pattern goes.</summary>
    public Vector2 Map(Vector2 p)
    {
        var (centre, scale) = At(p.Y);
        return new Vector2(centre + (p.X - centre) * scale, p.Y);
    }

    /// <summary>Where a point of the eased pattern came from (only x moves, so each row is undone on its own).</summary>
    public Vector2 Unmap(Vector2 p)
    {
        var (centre, scale) = At(p.Y);
        return new Vector2(centre + (p.X - centre) / scale, p.Y);
    }
}

/// <summary>What easing a garment would do (or did).</summary>
public sealed class EasePlan
{
    public required string GarmentId { get; init; }
    public required string GarmentName { get; init; }
    public List<PieceEase> Pieces { get; } = new();
    public List<SeamChange> Seams { get; } = new();
    public int ChangedPieces => Pieces.Count(p => p.Changes);
}

/// <summary>
/// Eases a pattern where the simulated cloth is stretched too much, the way a pattern maker lets out a seam: the stretch
/// map's numbers, measured across the cloth (along the pattern's x) in rows, say how much wider each row of a piece
/// must be for the cloth to sit at <see cref="EaseOptions.TargetStrain"/>; the piece is widened by part of that about
/// the middle of the row, smoothly from row to row, so a tight knee gets room at the knee and nowhere else. The drape
/// is carried over, so simulating again starts from where the cloth was and only relaxes.
/// <para>
/// Only stretch across is eased. Stretch along a piece is reported (<see cref="PieceEase.MaxColumnStrain"/>) but left
/// alone, since lengthening a piece moves its hem and the seams at its ends. Compression is left alone too: that is
/// where cloth folds, which is not a fault. Pieces that pull in on purpose (elastic across below 1) and pieces pinned
/// all over (hardware) are skipped.
/// </para>
/// </summary>
public static class PatternEasing
{
    /// <summary>Works out the easing of a garment from its drape, without changing anything.</summary>
    public static EasePlan Plan(Garment garment, GarmentMesh mesh, EaseOptions? options = null)
    {
        options ??= new EaseOptions();
        var plan = new EasePlan { GarmentId = garment.Id, GarmentName = garment.Name };
        foreach (var pieceMesh in mesh.Pieces)
        {
            var piece = garment.FindPiece(pieceMesh.PieceId);
            if (piece == null || !Workspace.IsEffectivelyVisible(garment, piece))
                continue;
            if (options.PieceIds != null && !options.PieceIds.Contains(piece.Id))
                continue;
            plan.Pieces.Add(PlanPiece(piece, pieceMesh, options));
        }
        Measure(garment, plan);
        return plan;
    }

    private static PieceEase PlanPiece(PatternPiece piece, PieceMesh mesh, EaseOptions options)
    {
        PieceEase Skip(string why) => new() { PieceId = piece.Id, PieceName = piece.Name, Skipped = why };
        if (piece.Drape == null || piece.DrapeKey != mesh.Key || piece.Drape.Length != mesh.VertexCount * 3)
            return Skip("not simulated");
        if (!options.IncludeElastic && piece.Physics.ShrinkWeft < 0.999f)
            return Skip("elastic on purpose");
        if (piece.PinKey == mesh.Key && piece.Pins.Count >= mesh.VertexCount)
            return Skip("pinned all over");
        if (mesh.TriangleCount == 0)
            return Skip("no mesh");

        var positions = Mesher.CurrentPositions(piece, mesh);
        float sx = Math.Clamp(piece.Physics.ShrinkWeft, 0.5f, 1.5f), sy = Math.Clamp(piece.Physics.ShrinkWarp, 0.5f, 1.5f);
        float minY = mesh.Points.Min(p => p.Y), maxY = mesh.Points.Max(p => p.Y);
        float minX = mesh.Points.Min(p => p.X), maxX = mesh.Points.Max(p => p.X);
        float rowHeight = MathF.Max(options.RowCm, 0.5f);
        int rows = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / rowHeight));
        rowHeight = (maxY - minY) / rows;
        int columns = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / MathF.Max(options.RowCm, 0.5f)));
        float columnWidth = (maxX - minX) / columns;
        if (rowHeight < 1e-4f || columnWidth < 1e-4f)
            return Skip("too small");

        var across = new double[rows];
        var acrossArea = new double[rows];
        var along = new double[columns];
        var alongArea = new double[columns];
        for (int t = 0; t + 2 < mesh.Triangles.Length; t += 3)
        {
            int a = mesh.Triangles[t], b = mesh.Triangles[t + 1], c = mesh.Triangles[t + 2];
            // The deformation from the rest shape (the pattern after its shrink, in metres) to the cloth: F = Ds Dm⁻¹.
            var e1 = (mesh.Points[b] - mesh.Points[a]) * new Vector2(sx, sy) * 0.01f;
            var e2 = (mesh.Points[c] - mesh.Points[a]) * new Vector2(sx, sy) * 0.01f;
            float det = e1.X * e2.Y - e2.X * e1.Y;
            if (MathF.Abs(det) < 1e-12f)
                continue;
            var d1 = positions[b] - positions[a];
            var d2 = positions[c] - positions[a];
            // Columns of Dm⁻¹ are (e2.Y, -e1.Y)/det and (-e2.X, e1.X)/det: F's columns are where the pattern's x and y go.
            var fx = (d1 * e2.Y - d2 * e1.Y) / det;
            var fy = (d2 * e1.X - d1 * e2.X) / det;
            double area = MathF.Abs(det) * 0.5f;
            var centroid = (mesh.Points[a] + mesh.Points[b] + mesh.Points[c]) / 3f;
            int row = Math.Clamp((int)((centroid.Y - minY) / rowHeight), 0, rows - 1);
            int column = Math.Clamp((int)((centroid.X - minX) / columnWidth), 0, columns - 1);
            across[row] += (fx.Length() - 1f) * area;
            acrossArea[row] += area;
            along[column] += (fy.Length() - 1f) * area;
            alongArea[column] += area;
        }

        // Each row's width and middle, from the mesh points in it.
        var low = new float[rows];
        var high = new float[rows];
        Array.Fill(low, float.MaxValue);
        Array.Fill(high, float.MinValue);
        foreach (var p in mesh.Points)
        {
            int row = Math.Clamp((int)((p.Y - minY) / rowHeight), 0, rows - 1);
            low[row] = MathF.Min(low[row], p.X);
            high[row] = MathF.Max(high[row], p.X);
        }

        var scale = new float[rows];
        float maxRow = float.MinValue;
        for (int r = 0; r < rows; r++)
        {
            float strain = acrossArea[r] > 0 ? (float)(across[r] / acrossArea[r]) : 0f;
            maxRow = MathF.Max(maxRow, strain);
            // Rest lengths must grow by (1 + strain) / (1 + target) for the cloth to sit at the target.
            float wanted = (1f + strain) / (1f + options.TargetStrain) - 1f;
            scale[r] = 1f + Math.Clamp(wanted * options.Strength, 0f, options.MaxGrowth);
        }
        // Smooth, so the outline stays a smooth line instead of stepping from row to row; a peak is kept, not flattened.
        for (int pass = 0; pass < 2; pass++)
        {
            var copy = (float[])scale.Clone();
            for (int r = 0; r < rows; r++)
            {
                float smooth = (copy[Math.Max(r - 1, 0)] + 2 * copy[r] + copy[Math.Min(r + 1, rows - 1)]) / 4f;
                scale[r] = MathF.Max(smooth, copy[r] * 0.5f + smooth * 0.5f);
            }
        }

        var table = new (float, float)[rows];
        float mostAdded = 0, atY = 0;
        for (int r = 0; r < rows; r++)
        {
            float width = high[r] >= low[r] ? high[r] - low[r] : 0f;
            float centre = high[r] >= low[r] ? (low[r] + high[r]) * 0.5f : (minX + maxX) * 0.5f;
            table[r] = (centre, scale[r]);
            float added = width * (scale[r] - 1f);
            if (added > mostAdded)
                (mostAdded, atY) = (added, minY + (r + 0.5f) * rowHeight);
        }
        // The middle of a row is not the same from row to row; smooth it as well, so straight sides stay straight.
        for (int r = 1; r + 1 < rows; r++)
            table[r].Item1 = (table[r - 1].Item1 + 2 * table[r].Item1 + table[r + 1].Item1) / 4f;

        float maxColumn = float.MinValue;
        for (int c = 0; c < columns; c++)
            if (alongArea[c] > 0)
                maxColumn = MathF.Max(maxColumn, (float)(along[c] / alongArea[c]));

        return new PieceEase
        {
            PieceId = piece.Id, PieceName = piece.Name, MaxRowStrain = maxRow, MostAddedCm = mostAdded, AtYCm = atY,
            MaxColumnStrain = maxColumn == float.MinValue ? 0 : maxColumn, RowStart = minY, RowHeight = rowHeight, Rows = table,
            HasCreases = piece.HasCreasesFor(mesh.Key, mesh.VertexCount),
        };
    }

    /// <summary>
    /// The outline as it would be after easing: every point and curve handle moved by the piece's map, and snapped to
    /// what the project file stores (4 decimals). A drape belongs to its mesh's fingerprint, which changes with the
    /// smallest move of a point, so a point that the file would round differently would lose the drape on reopening.
    /// </summary>
    public static List<PatternPoint> EasedOutline(PatternPiece piece, PieceEase ease)
    {
        return piece.Points.Select(p =>
        {
            var moved = p.Clone();
            moved.Position = Snap(ease.Map(p.Position));
            if (p.Curve is { } curve)
                moved.Curve = Snap(ease.Map(curve));
            return moved;
        }).ToList();
    }

    private static Vector2 Snap(Vector2 v) => new(Snap(v.X), Snap(v.Y));

    // Written as the project file writes numbers, and read back as it reads them.
    private static float Snap(float v) => float.Parse((MathF.Round(v, 4) + 0f).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Fills in the edge and seam lengths before and after, on copies of the pieces.</summary>
    private static void Measure(Garment garment, EasePlan plan)
    {
        var eased = new Dictionary<string, PatternPiece>();
        foreach (var ease in plan.Pieces.Where(p => p.Changes))
        {
            var piece = garment.FindPiece(ease.PieceId)!;
            var copy = piece.Clone();
            copy.Points = EasedOutline(piece, ease);
            eased[piece.Id] = copy;
            for (int e = 0; e < piece.EdgeCount; e++)
            {
                float before = Outline.EdgeLength(piece, e), after = Outline.EdgeLength(copy, e);
                if (MathF.Abs(after - before) >= 0.05f)
                    ease.Edges.Add(new EdgeChange(e, piece.Points[e].EdgeName, before, after));
            }
        }
        foreach (var seam in garment.Seams)
        {
            if (!eased.ContainsKey(seam.A.PieceId) && !eased.ContainsKey(seam.B.PieceId))
                continue;
            var a = garment.FindPiece(seam.A.PieceId);
            var b = garment.FindPiece(seam.B.PieceId);
            if (a == null || b == null)
                continue;
            var change = new SeamChange(seam.Id, a.Name, seam.A.Edge, b.Name, seam.B.Edge,
                Outline.EdgeLength(a, seam.A.Edge), Outline.EdgeLength(b, seam.B.Edge),
                Outline.EdgeLength(eased.GetValueOrDefault(a.Id, a), seam.A.Edge), Outline.EdgeLength(eased.GetValueOrDefault(b.Id, b), seam.B.Edge));
            if (MathF.Abs(change.AfterA - change.BeforeA) >= 0.05f || MathF.Abs(change.AfterB - change.BeforeB) >= 0.05f)
                plan.Seams.Add(change);
        }
    }

    /// <summary>
    /// Eases the garment as planned, as one undo step. Every eased piece keeps its drape: each vertex of its new mesh
    /// starts where its place on the old pattern was, so the next simulation only lets the cloth relax. What belongs to
    /// a place on the eased cloth moves with it: its pins, patches sewn onto it (in any garment) and hard parts attached
    /// to it. Its set creases are cleared (<see cref="PieceEase.HasCreases"/>): they were set on the stretched cloth and
    /// would hold it stretched.
    /// </summary>
    public static void Apply(Workspace workspace, EasePlan plan, string stepName)
    {
        if (plan.ChangedPieces == 0)
            return;
        workspace.History.Execute(stepName, doc =>
        {
            var garment = doc.FindGarment(plan.GarmentId) ?? throw new InvalidOperationException("The garment is gone.");
            var before = Mesher.Build(garment);
            var old = new Dictionary<string, (PieceMesh Mesh, Vector3[] Positions, bool HadCreases, int[] Pins)>();
            foreach (var ease in plan.Pieces.Where(p => p.Changes))
            {
                var piece = garment.FindPiece(ease.PieceId);
                if (piece == null || before.Find(piece.Id) is not { } pieceMesh)
                    continue;
                old[piece.Id] = (pieceMesh, Mesher.CurrentPositions(piece, pieceMesh),
                    piece.HasCreasesFor(pieceMesh.Key, pieceMesh.VertexCount),
                    piece.PinKey == pieceMesh.Key ? piece.Pins.Where(i => i >= 0 && i < pieceMesh.VertexCount).ToArray() : Array.Empty<int>());
                piece.Points = EasedOutline(piece, ease);
            }
            var eases = plan.Pieces.Where(p => p.Changes && old.ContainsKey(p.PieceId)).ToDictionary(p => p.PieceId);
            // Patches sewn onto an eased piece, and hard parts sitting on one, keep their place on its cloth.
            foreach (var other in doc.Garments)
            {
                foreach (var stitch in other.SurfaceStitches)
                {
                    if (eases.TryGetValue(stitch.TargetPieceId, out var host))
                    {
                        stitch.TargetStart = Snap(host.Map(stitch.TargetStart));
                        stitch.TargetEnd = Snap(host.Map(stitch.TargetEnd));
                    }
                }
                foreach (var part in other.Pieces)
                {
                    if (part.Attachment is { } attached && eases.TryGetValue(attached.HostPieceId, out var host))
                        part.Attachment = new PieceAttachment { HostPieceId = attached.HostPieceId, At = Snap(host.Map(attached.At)) };
                }
            }
            var after = Mesher.Build(garment);
            foreach (var ease in plan.Pieces.Where(p => p.Changes))
            {
                var piece = garment.FindPiece(ease.PieceId);
                if (piece == null || !old.TryGetValue(piece.Id, out var was) || after.Find(piece.Id) is not { } pieceMesh)
                    continue;
                var sampler = new Workspace.SurfaceSampler(was.Mesh, was.Positions, Mesher.ComputeNormals(was.Positions, was.Mesh.Triangles));
                var drape = new float[pieceMesh.VertexCount * 3];
                for (int i = 0; i < pieceMesh.VertexCount; i++)
                {
                    var (p, _) = sampler.At(ease.Unmap(pieceMesh.Points[i]));
                    drape[3 * i] = p.X;
                    drape[3 * i + 1] = p.Y;
                    drape[3 * i + 2] = p.Z;
                }
                piece.Drape = drape;
                piece.DrapeKey = pieceMesh.Key;
                // Set creases hold the cloth at the distances it had when they were set, which here is the stretch being
                // eased out, so they would keep it stretched: they are cleared, to be set again on the relaxed cloth.
                if (was.HadCreases)
                    piece.ClearCreases();
                if (was.Pins.Length > 0)
                {
                    // Each pin goes to the new vertex nearest to where its old one is on the eased pattern.
                    var pins = new HashSet<int>();
                    foreach (int pin in was.Pins)
                    {
                        var at = ease.Map(was.Mesh.Points[pin]);
                        int nearest = 0;
                        float best = float.MaxValue;
                        for (int i = 0; i < pieceMesh.VertexCount; i++)
                        {
                            float d = Vector2.DistanceSquared(pieceMesh.Points[i], at);
                            if (d < best)
                                (best, nearest) = (d, i);
                        }
                        pins.Add(nearest);
                    }
                    piece.Pins = pins;
                    piece.PinKey = pieceMesh.Key;
                }
            }
        });
    }
}
