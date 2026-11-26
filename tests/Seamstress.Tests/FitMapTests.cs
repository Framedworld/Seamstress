using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;
using Xunit;

namespace Seamstress.Tests;

/// <summary>The fit maps' numbers (FitMaps) on small meshes made by hand: strain, the gap to a body, pressure, colours, summaries.</summary>
public class FitMapTests
{
    /// <summary>A grid of (n+1)² vertices, <paramref name="cell"/> cm apart, as a piece mesh.</summary>
    private static PieceMesh Grid(int n, float cell = 2f)
    {
        var points = new List<Vector2>();
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
                points.Add(new Vector2(x * cell, y * cell));
        var triangles = new List<int>();
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int a = y * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
                triangles.AddRange(new[] { a, b, d, a, d, c });
            }
        }
        return new PieceMesh
        {
            PieceId = "p", Points = points.ToArray(), Triangles = triangles.ToArray(), EdgeChains = Array.Empty<int[]>(),
            BoundaryCount = 0, Key = "grid" + n,
        };
    }

    /// <summary>The pattern laid flat in 3D (metres) and scaled by (sx, sy).</summary>
    private static Vector3[] Laid(PieceMesh mesh, float sx = 1f, float sy = 1f)
        => mesh.Points.Select(p => new Vector3(p.X * 0.01f * sx, p.Y * 0.01f * sy, 0)).ToArray();

    private static float[] StrainOf(PieceMesh mesh, Vector3[] positions, float weft = 1f, float warp = 1f)
    {
        var strain = new float[positions.Length];
        FitMaps.Strain(FitMaps.Edges(mesh, weft, warp), positions, strain);
        return strain;
    }

    [Fact]
    public void EdgesAreUniqueWithPatternRestLengths()
    {
        var edges = FitMaps.Edges(Grid(1, 10f));
        // Two triangles of a square: four sides and one diagonal.
        Assert.Equal(5, edges.Rest.Length);
        Assert.Equal(4, edges.Rest.Count(r => MathF.Abs(r - 0.1f) < 1e-6f));
        Assert.Single(edges.Rest, r => MathF.Abs(r - 0.1f * MathF.Sqrt(2)) < 1e-6f);
    }

    [Fact]
    public void ClothAtItsPatternSizeIsAtRest()
    {
        var mesh = Grid(6);
        Assert.All(StrainOf(mesh, Laid(mesh)), s => Assert.InRange(s, -1e-5f, 1e-5f));
    }

    [Fact]
    public void UniformStretchAndCompressionGiveTheirStrain()
    {
        var mesh = Grid(6);
        Assert.All(StrainOf(mesh, Laid(mesh, 1.1f, 1.1f)), s => Assert.InRange(s, 0.0999f, 0.1001f));
        Assert.All(StrainOf(mesh, Laid(mesh, 0.95f, 0.95f)), s => Assert.InRange(s, -0.0501f, -0.0499f));
    }

    [Fact]
    public void StretchingOneWayStrainsTheEdgesAlongIt()
    {
        var mesh = Grid(1, 10f);
        // 20% longer across: the two horizontal sides +20%, the vertical ones 0, the diagonal sqrt(1.44 + 1) / sqrt(2) - 1.
        var strain = StrainOf(mesh, Laid(mesh, 1.2f, 1f));
        float diagonal = MathF.Sqrt(1.44f + 1f) / MathF.Sqrt(2f) - 1f;
        // Vertex 0 has the bottom side, the left side and the diagonal.
        Assert.Equal((0.2f + 0f + diagonal) / 3f, strain[0], 1e-4f);
        // Vertex 1 has the bottom and the right side only.
        Assert.Equal(0.1f, strain[1], 1e-4f);
    }

    [Fact]
    public void ShrinkFactorsShortenTheRestLengths()
    {
        var mesh = Grid(4);
        // Elastic weft at 0.9: cloth laid at its drawn size is stretched across by 1/0.9.
        var strain = StrainOf(mesh, Laid(mesh), weft: 0.9f);
        Assert.All(strain, s => Assert.True(s > 0.01f));
        var relaxed = StrainOf(mesh, Laid(mesh, 0.9f, 1f), weft: 0.9f);
        Assert.All(relaxed, s => Assert.InRange(s, -1e-5f, 1e-5f));
    }

    [Fact]
    public void ALocalPullShowsWhereItIs()
    {
        var mesh = Grid(8);
        var positions = Laid(mesh);
        int centre = 4 * 9 + 4;
        positions[centre] += new Vector3(0, 0, 0.012f);   // pulled 1.2 cm out of the plane
        var strain = StrainOf(mesh, positions);
        int worst = Array.IndexOf(strain, strain.Max());
        Assert.Equal(centre, worst);
        Assert.True(strain[0] == 0f, "a corner far from the pull stays at rest");
    }

    [Fact]
    public void ContactDistanceIsThicknessRoomAndSkin()
    {
        Assert.Equal(0.001f + 0.004f + FitMaps.SkinOffsetMetres, FitMaps.ContactDistance(1f, 4f), 1e-6f);
        var garment = new Garment { BodyOffset = 3f, Thickness = 2f };
        var piece = new PatternPiece();
        Assert.Equal(0.002f + 0.003f + FitMaps.SkinOffsetMetres, FitMaps.ContactDistance(garment, piece), 1e-6f);
    }

    /// <summary>A ball of 10 cm radius at the origin, as the solver sees a body.</summary>
    private static SdfGrid Ball() => SdfGrid.FromFunction(p => p.Length() - 0.1f, new Vector3(-0.2f), new Vector3(0.2f), 0.004f);

    /// <summary>An upright column of 10 cm radius round the Y axis.</summary>
    private static SdfGrid Column() => SdfGrid.FromFunction(p => MathF.Sqrt(p.X * p.X + p.Z * p.Z) - 0.1f, new Vector3(-0.2f), new Vector3(0.2f), 0.004f);

    [Fact]
    public void GapIsTheAirBeyondTheContactDistance()
    {
        var body = Ball();
        float contact = FitMaps.ContactDistance(1f, 0f);
        var points = new[]
        {
            new Vector3(0.1f + contact, 0, 0),          // touching
            new Vector3(0, 0.1f + contact + 0.02f, 0),  // 2 cm off
            new Vector3(0, 0, 0.1f + contact - 0.003f), // pushed 3 mm in
            new Vector3(5f, 0, 0),                      // far outside the field
        };
        var gap = new float[points.Length];
        FitMaps.Gap(body, points, contact, gap);
        Assert.InRange(gap[0], -0.0005f, 0.0005f);
        Assert.InRange(FitMaps.DistanceCm(gap[1]), 1.95f, 2.05f);
        Assert.InRange(gap[2], -0.0035f, -0.0025f);
        Assert.Equal(0f, FitMaps.DistanceCm(gap[2]));
        Assert.True(float.IsPositiveInfinity(gap[3]));
        FitMaps.Gap(null, points, contact, gap);
        Assert.All(gap, g => Assert.True(float.IsPositiveInfinity(g)));
    }

    [Fact]
    public void PressureGrowsWithContactAndTension()
    {
        Assert.Equal(0f, FitMaps.Pressure(0.02f, 0.2f));                // 2 cm off: free, however stretched
        Assert.Equal(0.25f, FitMaps.Pressure(0f, 0f), 1e-4f);               // touching, slack
        Assert.Equal(1f, FitMaps.Pressure(0f, 0.1f), 1e-4f);                // touching and stretched 10%
        Assert.Equal(1f, FitMaps.Pressure(0f, 0.3f), 1e-4f);                // capped
        Assert.Equal(0.625f, FitMaps.Pressure(0f, 0.05f), 1e-4f);           // half way to tight
        Assert.Equal(0.125f, FitMaps.Pressure(FitMaps.ContactBandMetres / 2, 0f), 1e-4f); // fading out over the contact band
        Assert.Equal(1f, FitMaps.Pressure(-FitMaps.ContactBandMetres, 0f), 1e-4f);       // squeezed inside its contact distance
        Assert.Equal(0f, FitMaps.Pressure(float.PositiveInfinity, 0.1f));
    }

    [Fact]
    public void PressureOnABodyIsHighestWhereTheClothIsTautAndTouching()
    {
        // A sheet wrapped round a column: its lower half touching it, stretched 10% on the left and slack on the right;
        // its upper half standing 3 cm off.
        var body = Column();
        float contact = FitMaps.ContactDistance(1f, 0f);
        var mesh = Grid(10, 2f);
        var positions = new Vector3[mesh.VertexCount];
        for (int i = 0; i < positions.Length; i++)
        {
            var p = mesh.Points[i];
            float radius = p.Y < 10f ? 0.1f + contact : 0.1f + contact + 0.03f;
            float stretch = p.X < 10f ? 1.1f : 1f;
            float angle = p.X * 0.01f * stretch / radius;
            positions[i] = new Vector3(MathF.Cos(angle) * radius, (p.Y - 10f) * 0.01f, MathF.Sin(angle) * radius);
        }
        var values = new float[positions.Length];
        var scratch = new float[positions.Length];
        FitMaps.Values(FitMapKind.Pressure, FitMaps.Edges(mesh), positions, body, contact, values, scratch);
        float Mean(Func<Vector2, bool> where) => Enumerable.Range(0, positions.Length).Where(i => where(mesh.Points[i])).Average(i => values[i]);
        float tautTouching = Mean(p => p.Y < 8 && p.X < 8);
        float slackTouching = Mean(p => p.Y < 8 && p.X > 12);
        float off = Mean(p => p.Y > 12);
        Assert.True(tautTouching > 0.6f, $"taut and touching: {tautTouching}");
        Assert.InRange(slackTouching, 0.15f, 0.4f);
        Assert.True(off < 0.01f, $"standing off: {off}");

        FitMaps.Values(FitMapKind.Distance, FitMaps.Edges(mesh), positions, body, contact, values, scratch);
        Assert.InRange(Mean(p => p.Y > 12), 2.8f, 3.2f);
        Assert.InRange(Mean(p => p.Y < 8), 0f, 0.1f);
    }

    [Fact]
    public void ColoursRunFromBlueThroughGreenToRed()
    {
        var rest = FitMaps.Colour(FitMapKind.Stretch, 0f);
        var stretched = FitMaps.Colour(FitMapKind.Stretch, 0.1f);
        var compressed = FitMaps.Colour(FitMapKind.Stretch, -0.1f);
        Assert.True(rest.Y > rest.X && rest.Y > rest.Z, "at rest is green");
        Assert.True(stretched.X > stretched.Y && stretched.X > stretched.Z, "10% stretched is red");
        Assert.Equal(stretched, FitMaps.Colour(FitMapKind.Stretch, 0.5f));
        Assert.True(compressed.Z > compressed.X && compressed.Z > compressed.Y, "10% compressed is blue");
        var touching = FitMaps.Colour(FitMapKind.Distance, 0f);
        var loose = FitMaps.Colour(FitMapKind.Distance, 8f);
        Assert.True(touching.X > touching.Z && loose.Z > loose.X);
        Assert.Equal(loose, FitMaps.Colour(FitMapKind.Distance, float.PositiveInfinity));
        var free = FitMaps.Colour(FitMapKind.Pressure, 0f);
        var tight = FitMaps.Colour(FitMapKind.Pressure, 1f);
        Assert.True(tight.X > 0.5f && tight.X > free.X);
        foreach (var kind in new[] { FitMapKind.Stretch, FitMapKind.Pressure, FitMapKind.Distance })
            Assert.Equal(5, FitMaps.LegendColours(kind).Length);
    }

    [Theory]
    [InlineData("stretch", FitMapKind.Stretch)]
    [InlineData("Stress", FitMapKind.Pressure)]
    [InlineData("fit", FitMapKind.Distance)]
    [InlineData("distance", FitMapKind.Distance)]
    [InlineData("nonsense", FitMapKind.Off)]
    [InlineData(null, FitMapKind.Off)]
    public void MapNamesParse(string? text, FitMapKind kind)
    {
        Assert.Equal(kind, FitMaps.Parse(text));
        if (kind != FitMapKind.Off)
            Assert.Equal(kind, FitMaps.Parse(FitMaps.Name(kind)));
    }

    [Fact]
    public void SummaryFindsTheStretchedPlaceOfAGarment()
    {
        var piece = new PatternPiece { Name = "Panel" };
        piece.Points.Add(new PatternPoint(0, 0));
        piece.Points.Add(new PatternPoint(30, 0));
        piece.Points.Add(new PatternPoint(30, 30));
        piece.Points.Add(new PatternPoint(0, 30));
        var garment = new Garment { Name = "Test", ParticleDistance = 3 };
        garment.Pieces.Add(piece);
        var doc = new Document();
        doc.Garments.Add(garment);
        var mesh = Mesher.Build(garment);
        var pm = mesh.Pieces[0];
        // Laid flat, with the right-hand third stretched 15% across.
        var positions = pm.Points.Select(p => new Vector3((p.X <= 20 ? p.X : 20 + (p.X - 20) * 1.15f) * 0.01f, p.Y * 0.01f, 0)).ToArray();
        piece.Drape = positions.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        piece.DrapeKey = pm.Key;

        var summary = Assert.Single(FitMaps.Summarize(doc, _ => mesh, null, FitMapKind.Stretch));
        Assert.Equal("Test", summary.GarmentName);
        Assert.InRange(summary.Max, 0.1f, 0.16f);
        Assert.True(summary.ShareOver > 0.1f && summary.ShareOver < 0.6f, $"share over 5%: {summary.ShareOver}");
        Assert.NotEmpty(summary.Worst);
        Assert.All(summary.Worst.Take(1), w => Assert.True(w.PatternCm.X >= 20f, $"worst at {w.PatternCm}"));
        // Worst places are spread out, not one cluster.
        for (int i = 0; i < summary.Worst.Count; i++)
            for (int j = i + 1; j < summary.Worst.Count; j++)
                Assert.True(Vector3.Distance(summary.Worst[i].Position, summary.Worst[j].Position) >= 0.08f);

        // Without a body there is no distance to measure: everything counts as loose, with nothing to average.
        var distance = Assert.Single(FitMaps.Summarize(doc, _ => mesh, null, FitMapKind.Distance));
        Assert.Equal(1f, distance.ShareOver);
        Assert.Empty(distance.Worst);
        Assert.Empty(FitMaps.Summarize(doc, _ => mesh, null, FitMapKind.Off));
    }

    [Fact]
    public void BodyRegionsNameHeightSideAndFace()
    {
        var body = new TriMesh
        {
            Positions = new[] { new Vector3(-0.25f, 0, -0.15f), new Vector3(0.25f, 1.7f, 0.15f), new Vector3(0, 0.8f, 0) },
            Triangles = new[] { 0, 1, 2 },
        };
        var regions = new FitMaps.BodyRegions(body);
        Assert.Equal("front, left, chest", regions.Describe(new Vector3(0.1f, 1.2f, 0.1f)));
        Assert.Equal("back, right, hips", regions.Describe(new Vector3(-0.1f, 0.88f, -0.1f)));
        Assert.Equal("front, centre, waist", regions.Describe(new Vector3(0f, 1.05f, 0.1f)));
    }

    [Fact]
    public void ShellSourcesPointBackAtTheSheet()
    {
        var mesh = Grid(2);
        var positions = Laid(mesh);
        var normals = positions.Select(_ => Vector3.UnitZ).ToArray();
        var uvs = mesh.Points;
        var shell = Mesher.BuildShell(positions, normals, uvs, mesh.Triangles, 0.002f);
        Assert.Equal(shell.Positions.Length, shell.Sources.Length);
        for (int i = 0; i < shell.Positions.Length; i++)
        {
            int s = shell.Sources[i];
            // Each shell vertex lies within half the thickness of the sheet vertex it came from.
            if (shell.Positions[i] != Vector3.Zero)
                Assert.True(Vector3.Distance(shell.Positions[i], positions[s]) <= 0.0011f, $"vertex {i} from {s}");
        }
    }
}
