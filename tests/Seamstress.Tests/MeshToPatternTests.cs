using System.Globalization;
using System.Numerics;
using System.Text;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Tests;

public class MeshToPatternTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Builds a curved two-panel open tube/tunic OBJ centered vertically around <paramref name="centerY"/> (metres),
    /// with welded 3D side-seam vertices shared between Front (+Z) and Back (-Z) panels.
    /// </summary>
    private static (string Obj, double ExpectedHalfAreaCm2, float ExpectedArcWidthCm, float ExpectedHeightCm) BuildTwoPanelTubeObj(
        float radiusM,
        float heightM,
        float centerY,
        int halfCols,
        int rows,
        bool includeUvs,
        bool includeGroups,
        bool mirrorBackUv = false)
    {
        var sb = new StringBuilder();
        int totalCols = halfCols * 2;
        float y0 = centerY - heightM * 0.5f;

        // Shared 3D ring vertices around the full cylinder (welded along both side seams: col 0 and col halfCols).
        // Angle runs from 0 (+X, right seam) -> +pi/2 (+Z, front center) -> pi (-X, left seam) -> 3pi/2 (-Z, back center).
        var verts = new Vector3[rows + 1, totalCols];
        for (int r = 0; r <= rows; r++)
        {
            float y = y0 + heightM * r / rows;
            for (int c = 0; c < totalCols; c++)
            {
                float theta = 2f * MathF.PI * c / totalCols;
                // Place front panel (+Z) from theta = 0..pi (actually +X -> +Z -> -X with outward normal +radial).
                // Wait: x = -R*cos(theta) goes -R (theta=0) -> 0 (pi/2) -> +R (pi), and z = R*sin(theta) > 0 for front!
                float x = -radiusM * MathF.Cos(theta);
                float z = radiusM * MathF.Sin(theta);
                verts[r, c] = new Vector3(x, y, z);
                sb.Append(Inv, $"v {x:0.######} {y:0.######} {z:0.######}\n");
            }
        }

        // Compute exact 3D area of one half of the discretized cylinder in cm^2.
        double halfAreaM2 = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < halfCols; c++)
            {
                var p00 = verts[r, c];
                var p10 = verts[r, c + 1];
                var p11 = verts[r + 1, c + 1];
                var p01 = verts[r + 1, c];
                halfAreaM2 += 0.5 * Vector3.Cross(p10 - p00, p11 - p00).Length();
                halfAreaM2 += 0.5 * Vector3.Cross(p11 - p00, p01 - p00).Length();
            }
        }

        int uvCols = halfCols + 1;
        int uvRows = rows + 1;
        int frontUvBase = 1;
        int backUvBase = 1 + uvCols * uvRows;

        // Exact chordal arc width of one half of the discretized cylinder in cm.
        float chordStepCm = Vector3.Distance(verts[0, 0], verts[0, 1]) * 100f;
        float expectedWidthCm = chordStepCm * halfCols;
        float expectedHeightCm = heightM * 100f;

        if (includeUvs)
        {
            // Conformal UV islands proportional to arc width & height, placed side by side in [0, 1]^2.
            float uvWidth = 0.40f;
            float uvHeight = uvWidth * (expectedHeightCm / MathF.Max(expectedWidthCm, 1e-3f));
            // Front UV island: u in [0.05, 0.45]
            for (int r = 0; r <= rows; r++)
            {
                float v = 0.05f + uvHeight * r / rows;
                for (int c = 0; c <= halfCols; c++)
                {
                    float u = 0.05f + uvWidth * c / halfCols;
                    sb.Append(Inv, $"vt {u:0.######} {v:0.######}\n");
                }
            }
            // Back UV island: u in [0.55, 0.95]
            for (int r = 0; r <= rows; r++)
            {
                float v = 0.05f + uvHeight * r / rows;
                for (int c = 0; c <= halfCols; c++)
                {
                    float frac = mirrorBackUv ? 1f - (float)c / halfCols : (float)c / halfCols;
                    float u = 0.55f + uvWidth * frac;
                    sb.Append(Inv, $"vt {u:0.######} {v:0.######}\n");
                }
            }
        }

        int VIndex(int r, int c) => r * totalCols + (c % totalCols) + 1;
        int FrontVt(int r, int c) => frontUvBase + r * uvCols + c;
        int BackVt(int r, int c) => backUvBase + r * uvCols + c;

        // Front faces (cols 0 .. halfCols)
        if (includeGroups)
            sb.Append("g Front\n");
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < halfCols; c++)
            {
                int v00 = VIndex(r, c);
                int v10 = VIndex(r, c + 1);
                int v11 = VIndex(r + 1, c + 1);
                int v01 = VIndex(r + 1, c);
                if (includeUvs)
                {
                    int t00 = FrontVt(r, c);
                    int t10 = FrontVt(r, c + 1);
                    int t11 = FrontVt(r + 1, c + 1);
                    int t01 = FrontVt(r + 1, c);
                    sb.Append($"f {v00}/{t00} {v10}/{t10} {v11}/{t11} {v01}/{t01}\n");
                }
                else
                {
                    sb.Append($"f {v00} {v10} {v11} {v01}\n");
                }
            }
        }

        // Back faces (cols halfCols .. totalCols)
        if (includeGroups)
            sb.Append("g Back\n");
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < halfCols; c++)
            {
                int gc = halfCols + c;
                int v00 = VIndex(r, gc);
                int v10 = VIndex(r, gc + 1);
                int v11 = VIndex(r + 1, gc + 1);
                int v01 = VIndex(r + 1, gc);
                if (includeUvs)
                {
                    int t00 = BackVt(r, c);
                    int t10 = BackVt(r, c + 1);
                    int t11 = BackVt(r + 1, c + 1);
                    int t01 = BackVt(r + 1, c);
                    sb.Append($"f {v00}/{t00} {v10}/{t10} {v11}/{t11} {v01}/{t01}\n");
                }
                else
                {
                    sb.Append($"f {v00} {v10} {v11} {v01}\n");
                }
            }
        }

        return (sb.ToString(), halfAreaM2 * 10000.0, expectedWidthCm, expectedHeightCm);
    }

    [Fact]
    public void UvMappedTwoPanelTube_ExtractsMetricPiecesAutoSewsAndSimulatesCleanly()
    {
        var (objText, expectedAreaCm2, _, _) = BuildTwoPanelTubeObj(
            radiusM: 0.16f,
            heightM: 0.56f,
            centerY: 1.15f,
            halfCols: 8,
            rows: 6,
            includeUvs: true,
            includeGroups: false);

        var doc = new Document();
        var options = new MeshImportOptions
        {
            Scale = 1f,
            UseUvs = true,
            AutoSew = true,
            ParticleDistanceCm = 3.0f,
            GarmentName = "UV Tunic",
        };

        var result = MeshToPatternImporter.ImportFromText(objText, "tunic", doc, options);

        Assert.True(result.UsedUvIslands);
        Assert.Equal(2, result.ExtractedPieceCount);
        Assert.Equal(2, result.Garment.Pieces.Count);
        Assert.Equal(2, result.AutoSeamCount);
        Assert.Equal(2, result.Garment.Seams.Count);
        Assert.Equal("UV Tunic", result.Garment.Name);
        Assert.True(result.SourceVertexCount > 0);
        Assert.Equal(8 * 6 * 2 * 2, result.SourceTriangleCount);

        // Verify each piece has valid CCW simple outline and metric area within 5% of 3D area.
        foreach (var piece in result.Garment.Pieces)
        {
            var poly = Outline.Flatten(piece);
            Assert.True(Outline.SignedArea(poly) > 0, $"Piece '{piece.Name}' should be CCW.");
            Assert.True(Outline.IsSimple(poly), $"Piece '{piece.Name}' outline should not self-intersect.");

            float areaCm2 = Outline.Area(piece);
            double relErr = Math.Abs(areaCm2 - expectedAreaCm2) / expectedAreaCm2;
            Assert.True(relErr < 0.05, $"Piece '{piece.Name}' area {areaCm2:0.0} cm² differs from 3D {expectedAreaCm2:0.0} cm² by {relErr:P1}.");
        }

        // Verify front & back 3D placements sit on opposite sides of Z = 0 near Y = 1.15 m.
        var sortedByZ = result.Garment.Pieces.OrderBy(p => p.Placement.Position.Z).ToList();
        Assert.True(sortedByZ[0].Placement.Position.Z < -0.05f, "Back panel should be placed at -Z.");
        Assert.True(sortedByZ[1].Placement.Position.Z > 0.05f, "Front panel should be placed at +Z.");
        Assert.InRange(sortedByZ[0].Placement.Position.Y, 1.10f, 1.20f);
        Assert.InRange(sortedByZ[1].Placement.Position.Y, 1.10f, 1.20f);

        // Verify auto-detected seams connect the two pieces with low length mismatch and anti-parallel orientation.
        foreach (var seam in result.Garment.Seams)
        {
            var pieceA = result.Garment.FindPiece(seam.A.PieceId)!;
            var pieceB = result.Garment.FindPiece(seam.B.PieceId)!;
            Assert.NotEqual(pieceA.Id, pieceB.Id);
            Assert.False(seam.Reversed, "Manifold outward-facing panels should sew start-to-end (Reversed = false).");

            float lenA = Outline.EdgeLength(pieceA, seam.A.Edge);
            float lenB = Outline.EdgeLength(pieceB, seam.B.Edge);
            float mismatch = MathF.Abs(lenA - lenB) / MathF.Max(lenA, lenB);
            Assert.True(mismatch < 0.05f, $"Seam edge lengths {lenA:0.0} cm vs {lenB:0.0} cm mismatch by {mismatch:P1}.");
        }

        // Verify Mesher.Build + ClothSolver.Create + 10 steps runs cleanly without NaN or Infinity.
        doc.Garments.Add(result.Garment);
        var garmentMesh = Mesher.Build(result.Garment);
        Assert.Equal(2, garmentMesh.Pieces.Count);
        Assert.True(garmentMesh.Stitches.Count > 0);
        Assert.True(garmentMesh.TriangleCount > 0);

        using var solver = ClothSolver.Create(
            new[] { garmentMesh },
            Array.Empty<GarmentMesh>(),
            collider: null,
            doc.Simulation);

        for (int step = 0; step < 10; step++)
            solver.Step();

        Assert.True(solver.ParticleCount > 0);
        foreach (var p in solver.Positions)
        {
            Assert.False(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z), "ClothSolver produced NaN.");
            Assert.False(float.IsInfinity(p.X) || float.IsInfinity(p.Y) || float.IsInfinity(p.Z), "ClothSolver produced Infinity.");
        }
    }

    [Fact]
    public void UnUvdPolygroups_FlattensViaPcaWithAccurateCmDimensionsAndAutoSews()
    {
        var (objText, expectedAreaCm2, expectedWidthCm, expectedHeightCm) = BuildTwoPanelTubeObj(
            radiusM: 0.18f,
            heightM: 0.50f,
            centerY: 1.10f,
            halfCols: 8,
            rows: 5,
            includeUvs: false,
            includeGroups: true);

        var doc = new Document();
        var options = new MeshImportOptions
        {
            Scale = 1f,
            UseUvs = true, // No UVs exist in OBJ -> must fall back cleanly to 3D PCA flattening.
            SplitByGroups = true,
            AutoSew = true,
        };

        var result = MeshToPatternImporter.ImportFromText(objText, "PolygroupTop", doc, options);

        Assert.False(result.UsedUvIslands);
        Assert.Equal(2, result.ExtractedPieceCount);
        Assert.Equal(2, result.AutoSeamCount);

        var names = result.Garment.Pieces.Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Back", "Front" }, names);

        foreach (var piece in result.Garment.Pieces)
        {
            var poly = Outline.Flatten(piece);
            Assert.True(Outline.SignedArea(poly) > 0, $"Piece '{piece.Name}' should be CCW.");
            Assert.True(Outline.IsSimple(poly), $"Piece '{piece.Name}' should be simple.");

            var (min, max) = Outline.Bounds(piece);
            float widthCm = max.X - min.X;
            float heightCm = max.Y - min.Y;
            float areaCm2 = Outline.Area(piece);

            Assert.True(MathF.Abs(widthCm - expectedWidthCm) / expectedWidthCm < 0.08f,
                $"Piece '{piece.Name}' width {widthCm:0.0} cm vs expected {expectedWidthCm:0.0} cm.");
            Assert.True(MathF.Abs(heightCm - expectedHeightCm) / expectedHeightCm < 0.05f,
                $"Piece '{piece.Name}' height {heightCm:0.0} cm vs expected {expectedHeightCm:0.0} cm.");
            Assert.True(Math.Abs(areaCm2 - expectedAreaCm2) / expectedAreaCm2 < 0.05,
                $"Piece '{piece.Name}' area {areaCm2:0.0} cm² vs expected {expectedAreaCm2:0.0} cm².");
        }

        // Verify both side seams auto-sewed with matching lengths (~50 cm each).
        foreach (var seam in result.Garment.Seams)
        {
            var pieceA = result.Garment.FindPiece(seam.A.PieceId)!;
            var pieceB = result.Garment.FindPiece(seam.B.PieceId)!;
            Assert.NotEqual(pieceA.Id, pieceB.Id);
            Assert.False(seam.Reversed);
            float lenA = Outline.EdgeLength(pieceA, seam.A.Edge);
            float lenB = Outline.EdgeLength(pieceB, seam.B.Edge);
            Assert.InRange(lenA, expectedHeightCm * 0.92f, expectedHeightCm * 1.08f);
            Assert.InRange(lenB, expectedHeightCm * 0.92f, expectedHeightCm * 1.08f);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScaleAndZUpToYUp_ConvertsCmZUpObjToMetrePlacementAndCmPattern(bool useUvs)
    {
        // Author a 40 cm x 60 cm rectangular front panel in cm-scale Z-up coordinates (Unreal / ZBrush Z-up style):
        // X in [-20, +20] cm (width = 40 cm)
        // Z in [90, 150] cm  (vertical height = 60 cm, center = 120 cm = 1.20 m)
        // Y = -18 cm         (under (x, y, z) -> (x, z, -y), maps to +0.18 m forward in +Z!)
        // Facing -Y in Z-up => outward normal maps to +Z in Y-up.
        const string objText = """
            o FrontPanel
            v -20 -18 90
            v  20 -18 90
            v  20 -18 150
            v -20 -18 150
            vt 0 0
            vt 0.4 0
            vt 0.4 0.6
            vt 0 0.6
            f 1/1 2/2 3/3 4/4
            """;

        var doc = new Document();
        var options = new MeshImportOptions
        {
            Scale = 0.01f,
            ZUpToYUp = true,
            UseUvs = useUvs,
            AutoSew = true,
        };

        var result = MeshToPatternImporter.ImportFromText(objText, "ZUpPanel", doc, options);

        Assert.Equal(useUvs, result.UsedUvIslands);
        Assert.Equal(1, result.ExtractedPieceCount);
        Assert.Equal(0, result.AutoSeamCount);

        var piece = result.Garment.Pieces[0];
        Assert.Equal("FrontPanel", piece.Name);

        // 2D pattern should be 40 cm wide x 60 cm tall (area = 2400 cm²).
        var (min, max) = Outline.Bounds(piece);
        Assert.InRange(max.X - min.X, 39.5f, 40.5f);
        Assert.InRange(max.Y - min.Y, 59.5f, 60.5f);
        Assert.InRange(Outline.Area(piece), 2380f, 2420f);

        // 3D placement should be in metres Y-up: Position = (0, 1.20, +0.18).
        Assert.InRange(piece.Placement.Position.X, -0.005f, 0.005f);
        Assert.InRange(piece.Placement.Position.Y, 1.195f, 1.205f);
        Assert.InRange(piece.Placement.Position.Z, 0.175f, 0.185f);

        // Arranged mesh vertices in 3D should lie in the Z = +0.18 m plane spanning X in [-0.20, 0.20], Y in [0.90, 1.50].
        var mesh = Mesher.Build(result.Garment);
        var arranged = Mesher.Arrange(piece, mesh.Pieces[0]);
        float minX = arranged.Min(p => p.X), maxX = arranged.Max(p => p.X);
        float minY = arranged.Min(p => p.Y), maxY = arranged.Max(p => p.Y);
        float minZ = arranged.Min(p => p.Z), maxZ = arranged.Max(p => p.Z);
        Assert.InRange(minX, -0.205f, -0.195f);
        Assert.InRange(maxX, 0.195f, 0.205f);
        Assert.InRange(minY, 0.895f, 0.905f);
        Assert.InRange(maxY, 1.495f, 1.505f);
        Assert.InRange(minZ, 0.175f, 0.185f);
        Assert.InRange(maxZ, 0.175f, 0.185f);
    }

    [Fact]
    public void SplitVertexSeamWithinTolerance_WeldsAndAutoSewsAcrossDistinctVertexIndices()
    {
        // Two flat panels meeting at X = 0 with distinct v indices separated by 0.2 mm (within 1 mm weld tolerance).
        const string objText = """
            g LeftPanel
            v -0.25 1.00 0.15
            v  0.00 1.00 0.15
            v  0.00 1.40 0.15
            v -0.25 1.40 0.15
            f 1 2 3 4

            g RightPanel
            v 0.0002 1.00 0.15
            v 0.2500 1.00 0.15
            v 0.2500 1.40 0.15
            v 0.0002 1.40 0.15
            f 5 6 7 8
            """;

        var doc = new Document();
        var result = MeshToPatternImporter.ImportFromText(objText, "SplitSeam", doc);

        Assert.Equal(2, result.ExtractedPieceCount);
        Assert.Equal(1, result.AutoSeamCount);
        Assert.Single(result.Garment.Seams);
        Assert.False(result.Garment.Seams[0].Reversed);
    }

    [Fact]
    public void MirroredUvIsland_FlipsToCcwAndPreservesAccurateSeamOrientation()
    {
        var (objText, expectedAreaCm2, _, _) = BuildTwoPanelTubeObj(
            radiusM: 0.15f,
            heightM: 0.50f,
            centerY: 1.15f,
            halfCols: 6,
            rows: 4,
            includeUvs: true,
            includeGroups: false,
            mirrorBackUv: true);

        var doc = new Document();
        var result = MeshToPatternImporter.ImportFromText(objText, "MirroredUv", doc);

        Assert.True(result.UsedUvIslands);
        Assert.Equal(2, result.ExtractedPieceCount);
        Assert.Equal(2, result.AutoSeamCount);

        foreach (var piece in result.Garment.Pieces)
        {
            Assert.True(Outline.SignedArea(Outline.Flatten(piece)) > 0, "Mirrored UV island must still produce CCW outline.");
            Assert.True(Math.Abs(Outline.Area(piece) - expectedAreaCm2) / expectedAreaCm2 < 0.05);
        }

        // Flipping U on the mirrored island restores right-handed CCW winding, so seams remain anti-parallel (Reversed = false).
        foreach (var seam in result.Garment.Seams)
            Assert.False(seam.Reversed);
    }

    [Fact]
    public void PanelWithHole_ExtractsCcwOuterAndCwHoleLoopAndMeshesCleanly()
    {
        // A 40 cm x 40 cm flat plate with a 10 cm x 10 cm square hole in the center (8 surrounding quads).
        // Coordinates in metres: outer [-0.20, 0.20]^2, inner [-0.05, 0.05]^2.
        float[] xs = { -0.20f, -0.05f, 0.05f, 0.20f };
        float[] ys = { 0.80f, 0.95f, 1.05f, 1.20f };
        var sb = new StringBuilder();
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                sb.Append(Inv, $"v {xs[c]:0.####} {ys[r]:0.####} 0.15\n");
                sb.Append(Inv, $"vt {(xs[c] + 0.20f) / 0.40f:0.####} {(ys[r] - 0.80f) / 0.40f:0.####}\n");
            }
        }
        int Idx(int r, int c) => r * 4 + c + 1;
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                if (r == 1 && c == 1)
                    continue; // center hole
                int a = Idx(r, c), b = Idx(r, c + 1), d = Idx(r + 1, c + 1), e = Idx(r + 1, c);
                sb.Append($"f {a}/{a} {b}/{b} {d}/{d} {e}/{e}\n");
            }
        }

        var doc = new Document();
        var result = MeshToPatternImporter.ImportFromText(sb.ToString(), "PlateWithHole", doc);

        Assert.Equal(1, result.ExtractedPieceCount);
        var piece = result.Garment.Pieces[0];
        Assert.Single(piece.Holes);
        Assert.True(Outline.SignedArea(Outline.Flatten(piece)) > 0, "Outer loop must be CCW.");
        Assert.True(Outline.SignedArea(Outline.FlattenHoles(piece)[0]) < 0, "Hole loop must be CW.");

        // Expected area = 40*40 - 10*10 = 1500 cm².
        Assert.InRange(Outline.Area(piece), 1480f, 1520f);

        var mesh = Mesher.Build(result.Garment);
        Assert.True(mesh.TriangleCount > 0);
    }

    [Fact]
    public void ImportFromFile_HandlesNegativeIndicesAndNonOverlappingLayout()
    {
        // Two separate panels in a temporary OBJ file using negative face indices and v//vn syntax.
        const string objContent = """
            # Test OBJ with negative indices and comments
            g Left
            v -0.30 1.00 0.10
            v -0.05 1.00 0.10
            v -0.05 1.40 0.10
            v -0.30 1.40 0.10
            vn 0 0 1
            f -4//-1 -3//-1 -2//-1 -1//-1

            g Right
            v 0.05 1.00 0.10
            v 0.30 1.00 0.10
            v 0.30 1.40 0.10
            v 0.05 1.40 0.10
            f -4//-1 -3//-1 -2//-1 -1//-1
            """;

        string tempPath = Path.Combine(Path.GetTempPath(), $"seamstress_mesh_import_{Guid.NewGuid():N}.obj");
        try
        {
            File.WriteAllText(tempPath, objContent);
            var doc = new Document();
            var result = MeshToPatternImporter.ImportFromFile(tempPath, doc, new MeshImportOptions { AutoSew = false });

            Assert.Equal(2, result.ExtractedPieceCount);
            Assert.Equal(0, result.AutoSeamCount);

            // Verify 2D bounding boxes in pattern space do not overlap.
            var p0 = result.Garment.Pieces[0];
            var p1 = result.Garment.Pieces[1];
            var (min0, max0) = Outline.Bounds(p0);
            var (min1, max1) = Outline.Bounds(p1);
            min0 += p0.Origin; max0 += p0.Origin;
            min1 += p1.Origin; max1 += p1.Origin;

            bool separated = max0.X <= min1.X || max1.X <= min0.X || max0.Y <= min1.Y || max1.Y <= min0.Y;
            Assert.True(separated, "Extracted pieces should be laid out without overlapping in 2D pattern space.");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
