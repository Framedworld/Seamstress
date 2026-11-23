using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Simulation;

/// <summary>Parameters controlling <see cref="GarmentRefitter.RefitGarment"/>.</summary>
public sealed class RefitOptions
{
    /// <summary>When true, scales 2D pattern outlines and Bezier curves to match transferred 3D stretch.</summary>
    public bool Grade2DPatterns { get; set; } = true;

    /// <summary>When true, runs staged XPBD morph-draping from source body to target body.</summary>
    public bool RunMorphDrape { get; set; } = true;

    /// <summary>Number of intermediate body-morph stages between source and target.</summary>
    public int MorphStages { get; set; } = 3;

    /// <summary>Solver frames simulated at each morph stage.</summary>
    public int FramesPerStage { get; set; } = 12;

    /// <summary>Additional solver frames run on the final target body to let the garment settle.</summary>
    public int SettleFrames { get; set; } = 30;

    /// <summary>XPBD substeps per frame during morph-draping.</summary>
    public int Substeps { get; set; } = 12;

    /// <summary>Whether cloth-on-cloth self-collision is enabled during refit simulation.</summary>
    public bool SelfCollision { get; set; }
}

/// <summary>Summary metrics from a <see cref="GarmentRefitter.RefitGarment"/> run.</summary>
public sealed class RefitResult
{
    public int TransferredVertices { get; set; }
    public int GradedPieces { get; set; }
    public int SimulatedFrames { get; set; }
    public string ComputeBackend { get; set; } = "CPU";
    public float AverageScaleX { get; set; } = 1f;
    public float AverageScaleY { get; set; } = 1f;
    public float AverageScaleZ { get; set; } = 1f;
    public int PenetratedVerticesAfter { get; set; }
    public float MaxPenetrationMmAfter { get; set; }
}

/// <summary>
/// Transfers draped garments between same-topology avatars, grades 2D pattern pieces to match the new body, and
/// optionally runs staged morph-drape cloth simulation so the refitted garment settles cleanly without penetration.
/// </summary>
public static class GarmentRefitter
{
    private const float MinGradeScale = 0.5f;
    private const float MaxGradeScale = 2.0f;
    private const float SolverSkinOffset = 0.002f;

    private readonly record struct VertexBinding(int TriangleIndex, float U, float V, float W, float NormalOffset, Vector3 FallbackNormal);
    private readonly record struct PieceGradeTransform(Vector2 Centroid2D, float ScaleU, float ScaleV);

    /// <summary>
    /// Binds every garment vertex to its nearest triangle on <paramref name="sourceBody"/> (triangle index,
    /// barycentric coordinates, and signed normal clearance) and reconstructs its position on
    /// <paramref name="targetBody"/> using the matching triangle plane and outward normal.
    /// </summary>
    public static Vector3[] BindAndTransfer(Vector3[] garmentPositions, TriMesh sourceBody, TriMesh targetBody)
    {
        ArgumentNullException.ThrowIfNull(garmentPositions);
        ArgumentNullException.ThrowIfNull(sourceBody);
        ArgumentNullException.ThrowIfNull(targetBody);
        ValidateSameTopology(sourceBody, targetBody);

        if (garmentPositions.Length == 0)
            return Array.Empty<Vector3>();

        var grid = new TriangleSpatialGrid(sourceBody);
        var bindings = new VertexBinding[garmentPositions.Length];
        var srcPos = sourceBody.Positions;
        var srcTris = sourceBody.Triangles;

        Parallel.For(0, garmentPositions.Length, i =>
        {
            var p = garmentPositions[i];
            var hit = grid.FindClosest(p);
            int t3 = hit.TriangleIndex * 3;
            var a0 = srcPos[srcTris[t3]];
            var b0 = srcPos[srcTris[t3 + 1]];
            var c0 = srcPos[srcTris[t3 + 2]];
            var n0 = Vector3.Cross(b0 - a0, c0 - a0);
            float n0Len = n0.Length();
            n0 = n0Len > 1e-12f ? n0 / n0Len : Vector3.UnitY;

            var diff = p - hit.ClosestPoint;
            float dist = diff.Length();
            float side = Vector3.Dot(diff, n0);
            float signedOffset = side >= 0f ? dist : -dist;
            bindings[i] = new VertexBinding(hit.TriangleIndex, hit.U, hit.V, hit.W, signedOffset, n0);
        });

        var result = new Vector3[garmentPositions.Length];
        var dstPos = targetBody.Positions;
        var dstTris = targetBody.Triangles;

        Parallel.For(0, bindings.Length, i =>
        {
            var b = bindings[i];
            int t3 = b.TriangleIndex * 3;
            var a1 = dstPos[dstTris[t3]];
            var b1 = dstPos[dstTris[t3 + 1]];
            var c1 = dstPos[dstTris[t3 + 2]];
            var q1 = a1 * b.U + b1 * b.V + c1 * b.W;
            var n1 = Vector3.Cross(b1 - a1, c1 - a1);
            float n1Len = n1.Length();
            n1 = n1Len > 1e-12f ? n1 / n1Len : b.FallbackNormal;
            result[i] = q1 + n1 * b.NormalOffset;
        });

        return result;
    }

    /// <summary>
    /// Grades each pattern piece in <paramref name="garment"/> by estimating 2D horizontal/vertical stretch ratios
    /// from <paramref name="beforePos"/> to <paramref name="afterPos"/>, scaling outline points and Bezier curves
    /// around the 2D piece centroid (clamped to [0.5, 2.0]), and moving <see cref="Placement.Position"/> to the
    /// transferred 3D centroid. Returns the number of graded pieces.
    /// </summary>
    public static int GradePieces(Garment garment, GarmentMesh mesh, Vector3[] beforePos, Vector3[] afterPos)
        => GradePiecesInternal(garment, mesh, beforePos, afterPos, out _);

    /// <summary>
    /// Full same-topology garment auto-fit pipeline: surface transfer, optional 2D pattern grading, mesh rebuild,
    /// optional staged morph-draping via <see cref="ClothSolver"/>, writing final draped positions into
    /// <see cref="PatternPiece.Drape"/> and <see cref="PatternPiece.DrapeKey"/>, and reporting post-fit penetration.
    /// </summary>
    public static RefitResult RefitGarment(
        Document doc,
        Workspace workspace,
        Garment garment,
        Avatar sourceBody,
        Avatar targetBody,
        RefitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(garment);
        ArgumentNullException.ThrowIfNull(sourceBody);
        ArgumentNullException.ThrowIfNull(targetBody);
        ValidateSameTopology(sourceBody.Mesh, targetBody.Mesh);

        options ??= new RefitOptions();

        var initialMesh = workspace.MeshFor(garment);
        var beforePos = GatherPositions(garment, initialMesh);
        if (beforePos.Length == 0)
            return new RefitResult();

        var afterPos = BindAndTransfer(beforePos, sourceBody.Mesh, targetBody.Mesh);
        var (avgX, avgY, avgZ) = EstimateWorldAxisScales(initialMesh, beforePos, afterPos);

        var result = new RefitResult
        {
            TransferredVertices = afterPos.Length,
            AverageScaleX = avgX,
            AverageScaleY = avgY,
            AverageScaleZ = avgZ,
        };

        bool trackUndo = ReferenceEquals(workspace.Document, doc);
        if (trackUndo)
            workspace.History.Begin($"Refit {garment.Name}");

        try
        {
            GarmentMesh activeMesh = initialMesh;
            Vector3[] currentPos;

            if (options.Grade2DPatterns)
            {
                result.GradedPieces = GradePiecesInternal(garment, initialMesh, beforePos, afterPos, out var gradeTransforms);
                activeMesh = workspace.MeshFor(garment);
                var reference3D = options.RunMorphDrape ? beforePos : afterPos;
                currentPos = ResampleSurfaceOntoGradedMesh(initialMesh, activeMesh, reference3D, gradeTransforms);
            }
            else
            {
                currentPos = options.RunMorphDrape ? (Vector3[])beforePos.Clone() : (Vector3[])afterPos.Clone();
            }

            if (options.RunMorphDrape)
            {
                int stages = Math.Max(1, options.MorphStages);
                int framesPerStage = Math.Max(0, options.FramesPerStage);
                int settleFrames = Math.Max(0, options.SettleFrames);

                var simSettings = doc.Simulation.Clone();
                simSettings.Substeps = Math.Clamp(options.Substeps, 1, 64);
                simSettings.SelfCollision = options.SelfCollision;

                TriMesh prevBodyMesh = sourceBody.Mesh;
                for (int stage = 1; stage <= stages; stage++)
                {
                    bool isFinalStage = stage == stages;
                    float t = (float)stage / stages;

                    TriMesh stageMesh = isFinalStage
                        ? targetBody.Mesh
                        : InterpolateMesh(sourceBody.Mesh, targetBody.Mesh, t);
                    SdfGrid stageSdf = isFinalStage
                        ? targetBody.Sdf
                        : SdfGrid.FromMesh(stageMesh);

                    currentPos = BindAndTransfer(currentPos, prevBodyMesh, stageMesh);
                    PushOutsideSdf(garment, activeMesh, currentPos, stageSdf);
                    WriteDrapeToGarment(garment, activeMesh, currentPos);

                    int frames = isFinalStage ? framesPerStage + settleFrames : framesPerStage;
                    if (frames > 0)
                    {
                        using var solver = ClothSolver.Create(
                            new[] { activeMesh },
                            Array.Empty<GarmentMesh>(),
                            stageSdf,
                            simSettings);
                        result.ComputeBackend = solver.BackendName;
                        for (int f = 0; f < frames; f++)
                            solver.Step();
                        result.SimulatedFrames += frames;

                        if (solver.Positions.Length == currentPos.Length)
                            Array.Copy(solver.Positions, currentPos, currentPos.Length);
                    }

                    prevBodyMesh = stageMesh;
                }
            }

            WriteDrapeToGarment(garment, activeMesh, currentPos);
            MeasurePenetrations(currentPos, targetBody.Sdf, out int penetrated, out float maxPenMm);
            result.PenetratedVerticesAfter = penetrated;
            result.MaxPenetrationMmAfter = maxPenMm;

            if (trackUndo)
                workspace.History.Commit();
        }
        catch
        {
            if (trackUndo)
                workspace.History.Cancel();
            throw;
        }

        return result;
    }

    private static int GradePiecesInternal(
        Garment garment,
        GarmentMesh mesh,
        Vector3[] beforePos,
        Vector3[] afterPos,
        out Dictionary<string, PieceGradeTransform> transforms)
    {
        ArgumentNullException.ThrowIfNull(garment);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(beforePos);
        ArgumentNullException.ThrowIfNull(afterPos);
        if (beforePos.Length != mesh.VertexCount || afterPos.Length != mesh.VertexCount)
        {
            throw new ArgumentException(
                $"Position array lengths ({beforePos.Length}, {afterPos.Length}) must match garment mesh vertex count ({mesh.VertexCount}).");
        }

        transforms = new Dictionary<string, PieceGradeTransform>(mesh.Pieces.Count);
        int gradedCount = 0;
        int offset = 0;

        foreach (var pieceMesh in mesh.Pieces)
        {
            int count = pieceMesh.VertexCount;
            var piece = garment.FindPiece(pieceMesh.PieceId);
            if (piece == null || count == 0 || piece.Points.Count < 3)
            {
                offset += count;
                continue;
            }

            var (scaleU, scaleV) = EstimatePiece2DStretch(pieceMesh, beforePos, afterPos, offset);
            scaleU = Math.Clamp(scaleU, MinGradeScale, MaxGradeScale);
            scaleV = Math.Clamp(scaleV, MinGradeScale, MaxGradeScale);

            var centroid2D = ComputeOutlineCentroid(piece);
            foreach (var (loop, _) in piece.Loops())
            {
                foreach (var pt in loop)
                {
                    pt.Position = new Vector2(
                        centroid2D.X + (pt.Position.X - centroid2D.X) * scaleU,
                        centroid2D.Y + (pt.Position.Y - centroid2D.Y) * scaleV);
                    if (pt.Curve is { } c)
                    {
                        pt.Curve = new Vector2(
                            centroid2D.X + (c.X - centroid2D.X) * scaleU,
                            centroid2D.Y + (c.Y - centroid2D.Y) * scaleV);
                    }
                }
            }

            var sum3D = Vector3.Zero;
            for (int i = 0; i < count; i++)
                sum3D += afterPos[offset + i];
            piece.Placement.Position = sum3D / count;
            piece.ClearPins();
            piece.ClearDrape();

            transforms[piece.Id] = new PieceGradeTransform(centroid2D, scaleU, scaleV);
            gradedCount++;
            offset += count;
        }

        return gradedCount;
    }

    private static (float ScaleU, float ScaleV) EstimatePiece2DStretch(
        PieceMesh pieceMesh,
        Vector3[] beforePos,
        Vector3[] afterPos,
        int offset)
    {
        double sumAfterU = 0, sumBeforeU = 0;
        double sumAfterV = 0, sumBeforeV = 0;
        var seenEdges = new HashSet<long>(EdgeKey.Comparer);
        var pts2 = pieceMesh.Points;
        var tris = pieceMesh.Triangles;

        for (int t = 0; t < tris.Length; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = tris[t + k];
                int b = tris[t + (k + 1) % 3];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!seenEdges.Add(key))
                    continue;

                var d2 = pts2[b] - pts2[a];
                float len2Sq = d2.LengthSquared();
                if (len2Sq < 1e-8f)
                    continue;

                float lenBefore = Vector3.Distance(beforePos[offset + a], beforePos[offset + b]);
                if (lenBefore < 1e-5f)
                    continue;

                float lenAfter = Vector3.Distance(afterPos[offset + a], afterPos[offset + b]);
                float clampedAfter = Math.Clamp(lenAfter, lenBefore * 0.25f, lenBefore * 4f);

                float wu = (d2.X * d2.X) / len2Sq;
                float wv = (d2.Y * d2.Y) / len2Sq;

                sumAfterU += wu * clampedAfter;
                sumBeforeU += wu * lenBefore;
                sumAfterV += wv * clampedAfter;
                sumBeforeV += wv * lenBefore;
            }
        }

        float scaleU = sumBeforeU > 1e-6 ? (float)(sumAfterU / sumBeforeU) : 1f;
        float scaleV = sumBeforeV > 1e-6 ? (float)(sumAfterV / sumBeforeV) : 1f;
        return (scaleU, scaleV);
    }

    private static (float ScaleX, float ScaleY, float ScaleZ) EstimateWorldAxisScales(
        GarmentMesh mesh,
        Vector3[] beforePos,
        Vector3[] afterPos)
    {
        double sumX = 0, weightX = 0;
        double sumY = 0, weightY = 0;
        double sumZ = 0, weightZ = 0;

        int offset = 0;
        foreach (var pieceMesh in mesh.Pieces)
        {
            int count = pieceMesh.VertexCount;
            if (count >= 3)
            {
                var meanB = Vector3.Zero;
                var meanA = Vector3.Zero;
                for (int i = 0; i < count; i++)
                {
                    meanB += beforePos[offset + i];
                    meanA += afterPos[offset + i];
                }
                meanB /= count;
                meanA /= count;

                double varBx = 0, varBy = 0, varBz = 0;
                double varAx = 0, varAy = 0, varAz = 0;
                for (int i = 0; i < count; i++)
                {
                    var db = beforePos[offset + i] - meanB;
                    var da = afterPos[offset + i] - meanA;
                    varBx += db.X * db.X;
                    varBy += db.Y * db.Y;
                    varBz += db.Z * db.Z;
                    varAx += da.X * da.X;
                    varAy += da.Y * da.Y;
                    varAz += da.Z * da.Z;
                }

                double rmsBx = Math.Sqrt(varBx / count);
                double rmsBy = Math.Sqrt(varBy / count);
                double rmsBz = Math.Sqrt(varBz / count);
                double rmsAx = Math.Sqrt(varAx / count);
                double rmsAy = Math.Sqrt(varAy / count);
                double rmsAz = Math.Sqrt(varAz / count);

                const double minSpan = 0.015;
                if (rmsBx > minSpan)
                {
                    sumX += (rmsAx / rmsBx) * rmsBx;
                    weightX += rmsBx;
                }
                if (rmsBy > minSpan)
                {
                    sumY += (rmsAy / rmsBy) * rmsBy;
                    weightY += rmsBy;
                }
                if (rmsBz > minSpan)
                {
                    sumZ += (rmsAz / rmsBz) * rmsBz;
                    weightZ += rmsBz;
                }
            }
            offset += count;
        }

        float sx = weightX > 0 ? (float)(sumX / weightX) : 1f;
        float sy = weightY > 0 ? (float)(sumY / weightY) : 1f;
        float sz = weightZ > 0 ? (float)(sumZ / weightZ) : 1f;
        return (sx, sy, sz);
    }

    private static Vector2 ComputeOutlineCentroid(PatternPiece piece)
    {
        if (piece.Points.Count == 0)
            return Vector2.Zero;
        var sum = Vector2.Zero;
        foreach (var pt in piece.Points)
            sum += pt.Position;
        return sum / piece.Points.Count;
    }

    private static Vector3[] ResampleSurfaceOntoGradedMesh(
        GarmentMesh sourceMesh,
        GarmentMesh gradedMesh,
        Vector3[] sourcePositions,
        Dictionary<string, PieceGradeTransform> gradeTransforms)
    {
        var sourceOffsets = new Dictionary<string, (PieceMesh Mesh, int Offset)>(sourceMesh.Pieces.Count);
        int srcOff = 0;
        foreach (var pm in sourceMesh.Pieces)
        {
            sourceOffsets[pm.PieceId] = (pm, srcOff);
            srcOff += pm.VertexCount;
        }

        var resampled = new Vector3[gradedMesh.VertexCount];
        int dstOff = 0;
        foreach (var dstPm in gradedMesh.Pieces)
        {
            if (!sourceOffsets.TryGetValue(dstPm.PieceId, out var srcInfo))
            {
                dstOff += dstPm.VertexCount;
                continue;
            }

            var srcPm = srcInfo.Mesh;
            int sOff = srcInfo.Offset;
            var transform = gradeTransforms.TryGetValue(dstPm.PieceId, out var tr)
                ? tr
                : new PieceGradeTransform(Vector2.Zero, 1f, 1f);

            var srcPts = srcPm.Points;
            var srcTris = srcPm.Triangles;
            int triCount = srcTris.Length / 3;
            var triMinX = new float[triCount];
            var triMaxX = new float[triCount];
            var triMinY = new float[triCount];
            var triMaxY = new float[triCount];
            for (int t = 0; t < triCount; t++)
            {
                var a = srcPts[srcTris[3 * t]];
                var b = srcPts[srcTris[3 * t + 1]];
                var c = srcPts[srcTris[3 * t + 2]];
                triMinX[t] = MathF.Min(a.X, MathF.Min(b.X, c.X));
                triMaxX[t] = MathF.Max(a.X, MathF.Max(b.X, c.X));
                triMinY[t] = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
                triMaxY[t] = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
            }

            float invU = 1f / MathF.Max(transform.ScaleU, 1e-4f);
            float invV = 1f / MathF.Max(transform.ScaleV, 1e-4f);
            var centroid = transform.Centroid2D;

            int localDstOff = dstOff;
            var dstPts = dstPm.Points;
            Parallel.For(0, dstPm.VertexCount, i =>
            {
                var pNew = dstPts[i];
                var pOrig = new Vector2(
                    centroid.X + (pNew.X - centroid.X) * invU,
                    centroid.Y + (pNew.Y - centroid.Y) * invV);
                var pOrig3 = new Vector3(pOrig.X, pOrig.Y, 0f);

                float bestDistSq = float.MaxValue;
                int bestA = 0, bestB = 0, bestC = 0;
                float bestWa = 1f, bestWb = 0f, bestWc = 0f;

                // Fast pass: check triangles whose 2D AABB contains pOrig.
                for (int t = 0; t < triCount; t++)
                {
                    if (pOrig.X < triMinX[t] || pOrig.X > triMaxX[t] || pOrig.Y < triMinY[t] || pOrig.Y > triMaxY[t])
                        continue;
                    int ia = srcTris[3 * t], ib = srcTris[3 * t + 1], ic = srcTris[3 * t + 2];
                    var a2 = srcPts[ia];
                    var b2 = srcPts[ib];
                    var c2 = srcPts[ic];
                    var q = SdfGrid.ClosestPointOnTriangle(
                        pOrig3,
                        new Vector3(a2.X, a2.Y, 0f),
                        new Vector3(b2.X, b2.Y, 0f),
                        new Vector3(c2.X, c2.Y, 0f),
                        out float wa, out float wb, out float wc);
                    float dSq = Vector3.DistanceSquared(pOrig3, q);
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestA = ia; bestB = ib; bestC = ic;
                        bestWa = wa; bestWb = wb; bestWc = wc;
                        if (dSq < 1e-10f)
                            break;
                    }
                }

                // Fallback for boundary points slightly outside due to Bezier re-discretization.
                if (bestDistSq > 1e-8f)
                {
                    for (int t = 0; t < triCount; t++)
                    {
                        float dx = pOrig.X < triMinX[t] ? triMinX[t] - pOrig.X : (pOrig.X > triMaxX[t] ? pOrig.X - triMaxX[t] : 0f);
                        float dy = pOrig.Y < triMinY[t] ? triMinY[t] - pOrig.Y : (pOrig.Y > triMaxY[t] ? pOrig.Y - triMaxY[t] : 0f);
                        if (dx * dx + dy * dy >= bestDistSq)
                            continue;

                        int ia = srcTris[3 * t], ib = srcTris[3 * t + 1], ic = srcTris[3 * t + 2];
                        var a2 = srcPts[ia];
                        var b2 = srcPts[ib];
                        var c2 = srcPts[ic];
                        var q = SdfGrid.ClosestPointOnTriangle(
                            pOrig3,
                            new Vector3(a2.X, a2.Y, 0f),
                            new Vector3(b2.X, b2.Y, 0f),
                            new Vector3(c2.X, c2.Y, 0f),
                            out float wa, out float wb, out float wc);
                        float dSq = Vector3.DistanceSquared(pOrig3, q);
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestA = ia; bestB = ib; bestC = ic;
                            bestWa = wa; bestWb = wb; bestWc = wc;
                        }
                    }
                }

                resampled[localDstOff + i] =
                    sourcePositions[sOff + bestA] * bestWa
                    + sourcePositions[sOff + bestB] * bestWb
                    + sourcePositions[sOff + bestC] * bestWc;
            });

            dstOff += dstPm.VertexCount;
        }

        return resampled;
    }

    private static Vector3[] GatherPositions(Garment garment, GarmentMesh mesh)
    {
        var result = new Vector3[mesh.VertexCount];
        int offset = 0;
        foreach (var pm in mesh.Pieces)
        {
            var piece = garment.FindPiece(pm.PieceId)!;
            var pos = Mesher.CurrentPositions(piece, pm);
            Array.Copy(pos, 0, result, offset, pos.Length);
            offset += pos.Length;
        }
        return result;
    }

    private static void WriteDrapeToGarment(Garment garment, GarmentMesh mesh, Vector3[] positions)
    {
        int offset = 0;
        foreach (var pm in mesh.Pieces)
        {
            var piece = garment.FindPiece(pm.PieceId);
            if (piece != null)
            {
                var drape = new float[pm.VertexCount * 3];
                for (int i = 0; i < pm.VertexCount; i++)
                {
                    var p = positions[offset + i];
                    drape[3 * i] = p.X;
                    drape[3 * i + 1] = p.Y;
                    drape[3 * i + 2] = p.Z;
                }
                piece.Drape = drape;
                piece.DrapeKey = pm.Key;
            }
            offset += pm.VertexCount;
        }
    }

    private static void PushOutsideSdf(Garment garment, GarmentMesh mesh, Vector3[] positions, SdfGrid sdf)
    {
        int offset = 0;
        foreach (var pm in mesh.Pieces)
        {
            var piece = garment.FindPiece(pm.PieceId);
            float clearance = piece != null
                ? (MathF.Max(garment.EffectiveThicknessMm(piece), 0.1f) + MathF.Max(garment.BodyOffset, 0f)) * 0.001f + SolverSkinOffset
                : 0.005f;
            for (int i = 0; i < pm.VertexCount; i++)
            {
                int idx = offset + i;
                float d = sdf.Sample(positions[idx], out var normal);
                if (d < clearance)
                    positions[idx] += normal * (clearance - d);
            }
            offset += pm.VertexCount;
        }
    }

    private static void MeasurePenetrations(Vector3[] positions, SdfGrid targetSdf, out int penetrated, out float maxPenMm)
    {
        penetrated = 0;
        maxPenMm = 0f;
        for (int i = 0; i < positions.Length; i++)
        {
            float d = targetSdf.Sample(positions[i], out _);
            if (d < 0f)
            {
                penetrated++;
                float penMm = -d * 1000f;
                if (penMm > maxPenMm)
                    maxPenMm = penMm;
            }
        }
    }

    private static TriMesh InterpolateMesh(TriMesh source, TriMesh target, float t)
    {
        int count = source.Positions.Length;
        var positions = new Vector3[count];
        for (int i = 0; i < count; i++)
            positions[i] = Vector3.Lerp(source.Positions[i], target.Positions[i], t);
        return new TriMesh { Positions = positions, Triangles = source.Triangles };
    }

    private static void ValidateSameTopology(TriMesh sourceBody, TriMesh targetBody)
    {
        if (sourceBody.Positions.Length != targetBody.Positions.Length
            || sourceBody.Triangles.Length != targetBody.Triangles.Length)
        {
            throw new ArgumentException(
                $"Source and target bodies must share identical mesh topology (source: {sourceBody.Positions.Length} verts / {sourceBody.Triangles.Length / 3} tris, "
                + $"target: {targetBody.Positions.Length} verts / {targetBody.Triangles.Length / 3} tris).");
        }
        if (sourceBody.Triangles.Length < 3)
            throw new ArgumentException("Body meshes must contain at least one triangle.");
    }

    /// <summary>Uniform spatial hash grid over mesh triangles for fast nearest-triangle queries.</summary>
    private sealed class TriangleSpatialGrid
    {
        public readonly record struct ClosestHit(int TriangleIndex, float U, float V, float W, Vector3 ClosestPoint, float Distance);

        private readonly Vector3[] _positions;
        private readonly int[] _triangles;
        private readonly Vector3[] _normals;
        private readonly Dictionary<(int, int, int), List<int>> _cells = new();
        private readonly float _cell;
        private readonly int _maxRings;

        public TriangleSpatialGrid(TriMesh mesh)
        {
            _positions = mesh.Positions;
            _triangles = mesh.Triangles;
            int triCount = _triangles.Length / 3;
            _normals = new Vector3[triCount];

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            double edgeSum = 0;
            int validCount = 0;

            for (int t = 0; t < triCount; t++)
            {
                var a = _positions[_triangles[3 * t]];
                var b = _positions[_triangles[3 * t + 1]];
                var c = _positions[_triangles[3 * t + 2]];
                var n = Vector3.Cross(b - a, c - a);
                float nl = n.Length();
                if (nl < 1e-12f)
                    continue;
                _normals[t] = n / nl;
                min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
                max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
                edgeSum += Vector3.Distance(a, b);
                validCount++;
            }

            _cell = MathF.Max((float)(edgeSum / Math.Max(1, validCount)) * 2f, 1e-4f);
            var size = max - min;
            _maxRings = (int)(MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / _cell) + 2;

            for (int t = 0; t < triCount; t++)
            {
                if (_normals[t] == Vector3.Zero)
                    continue;
                var a = _positions[_triangles[3 * t]];
                var b = _positions[_triangles[3 * t + 1]];
                var c = _positions[_triangles[3 * t + 2]];
                var lo = CellOf(Vector3.Min(a, Vector3.Min(b, c)));
                var hi = CellOf(Vector3.Max(a, Vector3.Max(b, c)));
                for (int z = lo.Z; z <= hi.Z; z++)
                {
                    for (int y = lo.Y; y <= hi.Y; y++)
                    {
                        for (int x = lo.X; x <= hi.X; x++)
                        {
                            var key = (x, y, z);
                            if (!_cells.TryGetValue(key, out var list))
                                _cells[key] = list = new List<int>();
                            list.Add(t);
                        }
                    }
                }
            }
        }

        private (int X, int Y, int Z) CellOf(Vector3 p)
            => ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell), (int)MathF.Floor(p.Z / _cell));

        public ClosestHit FindClosest(Vector3 p)
        {
            var center = CellOf(p);
            int bestTri = 0;
            float bestU = 1f, bestV = 0f, bestW = 0f;
            var bestPoint = _positions[_triangles[0]];
            float bestDist = float.MaxValue;
            float bestFace = -1f;

            for (int ring = 0; ring <= _maxRings * 2; ring++)
            {
                if (bestDist < (ring - 1) * _cell)
                    break;

                for (int dz = -ring; dz <= ring; dz++)
                {
                    for (int dy = -ring; dy <= ring; dy++)
                    {
                        for (int dx = -ring; dx <= ring; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != ring)
                                continue;
                            if (!_cells.TryGetValue((center.X + dx, center.Y + dy, center.Z + dz), out var list))
                                continue;

                            for (int i = 0; i < list.Count; i++)
                            {
                                int t = list[i];
                                int t3 = 3 * t;
                                var a = _positions[_triangles[t3]];
                                var b = _positions[_triangles[t3 + 1]];
                                var c = _positions[_triangles[t3 + 2]];
                                var q = SdfGrid.ClosestPointOnTriangle(p, a, b, c, out float u, out float v, out float w);
                                var diff = p - q;
                                float d = diff.Length();
                                if (d > bestDist + 1e-6f)
                                    continue;

                                float face = d > 1e-9f ? MathF.Abs(Vector3.Dot(diff, _normals[t])) / d : 1f;
                                if (d < bestDist - 1e-6f || face > bestFace)
                                {
                                    bestTri = t;
                                    bestU = u;
                                    bestV = v;
                                    bestW = w;
                                    bestPoint = q;
                                    bestDist = d;
                                    bestFace = face;
                                }
                            }
                        }
                    }
                }
            }

            if (bestDist == float.MaxValue)
            {
                int triCount = _triangles.Length / 3;
                for (int t = 0; t < triCount; t++)
                {
                    if (_normals[t] == Vector3.Zero)
                        continue;
                    int t3 = 3 * t;
                    var a = _positions[_triangles[t3]];
                    var b = _positions[_triangles[t3 + 1]];
                    var c = _positions[_triangles[t3 + 2]];
                    var q = SdfGrid.ClosestPointOnTriangle(p, a, b, c, out float u, out float v, out float w);
                    var diff = p - q;
                    float d = diff.Length();
                    if (d > bestDist + 1e-6f)
                        continue;
                    float face = d > 1e-9f ? MathF.Abs(Vector3.Dot(diff, _normals[t])) / d : 1f;
                    if (d < bestDist - 1e-6f || face > bestFace)
                    {
                        bestTri = t;
                        bestU = u;
                        bestV = v;
                        bestW = w;
                        bestPoint = q;
                        bestDist = d;
                        bestFace = face;
                    }
                }
            }

            return new ClosestHit(bestTri, bestU, bestV, bestW, bestPoint, bestDist);
        }
    }
}
