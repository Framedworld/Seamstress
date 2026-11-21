using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;
using Seamstress.Cuda;
using Xunit;

namespace Seamstress.Tests;

public class TailoringFeaturesTests
{
    private static PatternPiece RectPiece(string name, float w, float h, Vector3 pos, float shrinkWeft = 1f)
    {
        var p = new PatternPiece
        {
            Name = name,
            Points = new List<PatternPoint>
            {
                new() { Position = new Vector2(0, 0) },
                new() { Position = new Vector2(w, 0) },
                new() { Position = new Vector2(w, h) },
                new() { Position = new Vector2(0, h) },
            },
            Placement = new Placement { Position = pos },
            Physics = FabricPhysics.FromPreset("Denim"),
        };
        p.Physics.ShrinkWeft = shrinkWeft;
        return p;
    }

    [Fact]
    public void GarmentFileRoundTripsLayerSurfaceStitchesAndShrink()
    {
        var doc = new Document { Name = "Tailoring RoundTrip" };
        var baseGarment = new Garment { Name = "Base", Layer = 1 };
        var host = RectPiece("Host", 30, 30, new Vector3(0, 1f, 0.2f));
        baseGarment.Pieces.Add(host);

        var overlay = new Garment { Name = "Pocket", Layer = 2 };
        var pocket = RectPiece("Pocket", 10, 10, new Vector3(0, 1f, 0.22f), shrinkWeft: 0.92f);
        pocket.Physics.ShrinkWarp = 1.05f;
        overlay.Pieces.Add(pocket);
        overlay.SurfaceStitches.Add(new SurfaceStitch
        {
            SourcePieceId = pocket.Id,
            SourceEdge = 0,
            TargetPieceId = host.Id,
            TargetStart = new Vector2(10, 10),
            TargetEnd = new Vector2(20, 10),
        });

        doc.Garments.Add(baseGarment);
        doc.Garments.Add(overlay);

        string json = GarmentFile.Serialize(doc);
        var loaded = GarmentFile.Deserialize(json);

        Assert.Equal(2, loaded.Garments.Count);
        Assert.Equal(1, loaded.Garments[0].Layer);
        Assert.Equal(2, loaded.Garments[1].Layer);
        Assert.Single(loaded.Garments[1].SurfaceStitches);
        var ss = loaded.Garments[1].SurfaceStitches[0];
        Assert.Equal(pocket.Id, ss.SourcePieceId);
        Assert.Equal(0, ss.SourceEdge);
        Assert.Equal(host.Id, ss.TargetPieceId);
        Assert.Equal(new Vector2(10, 10), ss.TargetStart);
        Assert.Equal(new Vector2(20, 10), ss.TargetEnd);
        Assert.Equal(0.92f, loaded.Garments[1].Pieces[0].Physics.ShrinkWeft, tolerance: 1e-3f);
        Assert.Equal(1.05f, loaded.Garments[1].Pieces[0].Physics.ShrinkWarp, tolerance: 1e-3f);
    }

    [Fact]
    public void SdfGridHandlesNanAndDegenerateTrianglesSafely()
    {
        var avatar = Avatar.CreateMannequin();
        float dNan = avatar.Sdf.Sample(new Vector3(float.NaN, 1f, 0f), out var nNan);
        Assert.Equal(float.MaxValue, dNan);
        Assert.Equal(Vector3.UnitY, nNan);

        float dInf = avatar.Sdf.Sample(new Vector3(float.PositiveInfinity, 0f, 0f), out _);
        Assert.Equal(float.MaxValue, dInf);

        var a = new Vector3(1, 2, 3);
        var cp = SdfGrid.ClosestPointOnTriangle(a, a, a, a, out float wa, out float wb, out float wc);
        Assert.False(float.IsNaN(cp.X) || float.IsNaN(cp.Y) || float.IsNaN(cp.Z));
        Assert.False(float.IsNaN(wa) || float.IsNaN(wb) || float.IsNaN(wc));
    }

    [Fact]
    public void ShrinkWeftCinchesBandWidth()
    {
        var gNormal = new Garment { Name = "Normal", ParticleDistance = 2.5f };
        gNormal.Pieces.Add(RectPiece("Band", 30, 8, new Vector3(0, 1f, 0.2f), shrinkWeft: 1.0f));
        var gShrunk = new Garment { Name = "Shrunk", ParticleDistance = 2.5f };
        gShrunk.Pieces.Add(RectPiece("Band", 30, 8, new Vector3(0, 1f, 0.2f), shrinkWeft: 0.85f));

        float SimulateWidth(Garment g)
        {
            var gm = Mesher.Build(g);
            var settings = new SimulationSettings { Gravity = 0f, GroundPlane = false, SelfCollision = false, Substeps = 10 };
            using var solver = ClothSolver.Create(new[] { gm }, Array.Empty<GarmentMesh>(), null, settings);
            for (int f = 0; f < 30; f++)
                solver.Step();
            float minX = solver.Positions.Min(p => p.X);
            float maxX = solver.Positions.Max(p => p.X);
            return maxX - minX;
        }

        float wNormal = SimulateWidth(gNormal);
        float wShrunk = SimulateWidth(gShrunk);
        Assert.True(wShrunk < wNormal * 0.92f, $"Expected shrunk width ({wShrunk:F3}m) to be < 92% of normal ({wNormal:F3}m)");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurfaceStitchAttachesPatchProudOfFrozenHostWithoutNan(bool useGpu)
    {
        if (useGpu && !CudaClothBackend.Register())
            return;

        bool prevGpu = ClothAcceleration.Enabled;
        ClothAcceleration.Enabled = useGpu;
        try
        {
            var hostGarment = new Garment { Name = "Host", ParticleDistance = 2.5f, Thickness = 3f, Layer = 1 };
            var hostPiece = RectPiece("Panel", 30, 30, new Vector3(0, 1f, 0.2f));
            hostGarment.Pieces.Add(hostPiece);

            var patchGarment = new Garment { Name = "Patch", ParticleDistance = 2.0f, Thickness = 3f, Layer = 2 };
            var patchPiece = RectPiece("Pocket", 12, 12, new Vector3(0, 1f, 0.24f));
            patchGarment.Pieces.Add(patchPiece);
            patchGarment.SurfaceStitches.Add(new SurfaceStitch
            {
                SourcePieceId = patchPiece.Id,
                SourceEdge = 0,
                TargetPieceId = hostPiece.Id,
                TargetStart = new Vector2(9, 9),
                TargetEnd = new Vector2(21, 9),
            });
            patchGarment.SurfaceStitches.Add(new SurfaceStitch
            {
                SourcePieceId = patchPiece.Id,
                SourceEdge = 1,
                TargetPieceId = hostPiece.Id,
                TargetStart = new Vector2(21, 9),
                TargetEnd = new Vector2(21, 21),
            });
            patchGarment.SurfaceStitches.Add(new SurfaceStitch
            {
                SourcePieceId = patchPiece.Id,
                SourceEdge = 3,
                TargetPieceId = hostPiece.Id,
                TargetStart = new Vector2(9, 21),
                TargetEnd = new Vector2(9, 9),
            });

            var hostMesh = Mesher.Build(hostGarment);
            var patchMesh = Mesher.Build(patchGarment);
            var settings = new SimulationSettings { Gravity = -9.81f, GroundPlane = false, SelfCollision = true, Substeps = 12 };
            using var solver = ClothSolver.Create(new[] { patchMesh }, new[] { hostMesh }, null, settings);
            for (int f = 0; f < 40; f++)
                solver.Step();

            var cp = solver.Pieces.First(p => p.Simulated);
            var draped = solver.ExtractDrape(cp);
            float minZ = float.MaxValue;
            for (int i = 0; i < draped.Length; i += 3)
            {
                Assert.False(float.IsNaN(draped[i]) || float.IsNaN(draped[i + 1]) || float.IsNaN(draped[i + 2]));
                minZ = MathF.Min(minZ, draped[i + 2]);
            }
            // Host panel sits at Z = 0.200m; Layer 2 patch must sit strictly proud (Z > 0.201m).
            Assert.True(minZ > 0.201f, $"Expected patch to sit proud of host Z=0.200m, got minZ={minZ:F4}m");
            Assert.True(solver.SeamGap < 0.01f, $"Expected surface stitches to close cleanly, got gap={solver.SeamGap:F4}m");
        }
        finally
        {
            ClothAcceleration.Enabled = prevGpu;
        }
    }
}
