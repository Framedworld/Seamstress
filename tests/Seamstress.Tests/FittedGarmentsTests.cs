using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Tests;

public class FittedGarmentsTests
{
    private static readonly BodyMeasurements Mannequin = BodyMeasurements.Measure(Avatar.CreateMannequin().Mesh);

    private static Garment Add(Document doc, string template, FitOptions? options = null)
    {
        var made = FittedGarments.Create(template, doc, Mannequin, options);
        doc.Garments.AddRange(made);
        return made[0];
    }

    [Theory]
    [InlineData("Fitted Tee")]
    [InlineData("Fitted Trousers")]
    [InlineData("Fitted Jacket")]
    [InlineData("Fitted Hoodie")]
    public void EveryTemplateIsSewnWithNamedEdgesAndMeshes(string template)
    {
        var doc = new Document();
        var made = FittedGarments.Create(template, doc, Mannequin);
        Assert.NotEmpty(made);
        foreach (var g in made)
        {
            Assert.True(g.Pieces.Count >= 2);
            Assert.True(g.Seams.Count + g.SurfaceStitches.Count >= 1);
            foreach (var seam in g.Seams)
            {
                var a = g.FindPiece(seam.A.PieceId)!;
                var b = g.FindPiece(seam.B.PieceId)!;
                Assert.InRange(seam.A.Edge, 0, a.EdgeCount - 1);
                Assert.InRange(seam.B.Edge, 0, b.EdgeCount - 1);
                // Edges that are sewn together are about the same length (a sleeve end may gather onto a cuff).
                float la = Outline.EdgeLength(a, seam.A.Edge), lb = Outline.EdgeLength(b, seam.B.Edge);
                Assert.True(MathF.Abs(la - lb) <= 0.3f * MathF.Max(la, lb), $"{a.Name}[{seam.A.Edge}] {la:0.#} cm sewn to {b.Name}[{seam.B.Edge}] {lb:0.#} cm");
            }
            var mesh = Mesher.Build(g);
            Assert.All(mesh.Pieces, pm => Assert.True(pm.TriangleCount > 0));
        }
        Assert.Contains(made[0].Pieces, p => p.Points.Any(pt => pt.EdgeName != null));
    }

    [Fact]
    public void TemplatesAreCutToTheBodyTheyAreGiven()
    {
        // A body half as wide again needs wider pieces: the same template must not come out the same size.
        var slim = Avatar.CreateMannequin().Mesh;
        var wide = new TriMesh { Positions = slim.Positions.Select(p => new Vector3(p.X * 1.35f, p.Y, p.Z * 1.35f)).ToArray(), Triangles = slim.Triangles };
        float Width(TriMesh body)
        {
            var doc = new Document();
            var g = FittedGarments.Create("Fitted Tee", doc, BodyMeasurements.Measure(body), new FitOptions { Sleeves = "none" })[0];
            var back = g.Pieces.First(p => p.EdgeNamed("neck_l") >= 0);
            var (lo, hi) = Outline.Bounds(back);
            return hi.X - lo.X;
        }
        Assert.True(Width(wide) > Width(slim) * 1.2f);
    }

    [Fact]
    public void MovingTheOutseamBackWidensTheFrontsAndKeepsTheSeamsMatched()
    {
        (float Knee, float[] Mismatch) Cut(float shift)
        {
            var g = FittedGarments.Create("Fitted Trousers", new Document(), Mannequin, new FitOptions { OutseamBackCm = shift })[0];
            var front = g.Pieces.First(p => p.Name == "Front L");
            var back = g.Pieces.First(p => p.Name == "Back L");
            var mismatch = new[] { "out_lo", "out_hi", "out_hip" }
                .Select(e => Outline.EdgeLength(front, front.EdgeNamed(e)) - Outline.EdgeLength(back, back.EdgeNamed(e))).ToArray();
            return (front.Points[front.EdgeNamed("out_hi")].Position.X, mismatch);
        }
        var (knee0, mismatch0) = Cut(0);
        var (knee5, mismatch5) = Cut(5);
        Assert.Equal(knee0 + 5, knee5, 0.05f);
        // A chosen leg opening is what the hem measures round the leg.
        var narrow = FittedGarments.Create("Fitted Trousers", new Document(), Mannequin, new FitOptions { HemGirthCm = 40 })[0];
        float hem = new[] { "Front L", "Back L" }.Select(n => narrow.Pieces.First(p => p.Name == n)).Sum(p => Outline.EdgeLength(p, p.EdgeNamed("hem")));
        Assert.Equal(40f, hem, 0.1f);
        // The seam is moved, not reshaped: front and back still match exactly as well as they did (the back's ease included).
        for (int i = 0; i < 3; i++)
            Assert.Equal(mismatch0[i], mismatch5[i], 0.05f);
    }

    [Fact]
    public void LaterGarmentsAreWornOverEarlierOnesWhereTheyOverlap()
    {
        var doc = new Document();
        var trousers = Add(doc, "Fitted Trousers");
        var tee = Add(doc, "Fitted Tee", new FitOptions { Sleeves = "none" });
        var jacket = Add(doc, "Fitted Jacket");

        Assert.Equal(0, trousers.BodyOffset);
        Assert.True(tee.Layer > trousers.Layer);
        Assert.True(tee.BodyOffset >= trousers.Thickness!.Value);            // over the waistband
        Assert.True(jacket.BodyOffset >= tee.BodyOffset + tee.Thickness!.Value);
        Assert.True(jacket.Layer > tee.Layer);

        // Pockets on the thighs do not push a jacket further out: they are nowhere near it.
        var front = trousers.Pieces.First(p => p.Name.StartsWith("Front L", StringComparison.Ordinal));
        var (lo, hi) = Outline.Bounds(front);
        var pocket = GarmentBlocks.AddPatch(doc, Mesher.Build, front.Id, new List<Vector2> { new(lo.X + 8, lo.Y + 45), new(lo.X + 20, lo.Y + 45), new(lo.X + 20, lo.Y + 60), new(lo.X + 8, lo.Y + 60) },
            "pocket", "Thigh pocket", Vector2.One);
        pocket.Garment.BodyOffset = 30;      // absurdly proud, to make the point
        var second = FittedGarments.Create("Fitted Jacket", doc, Mannequin)[0];
        Assert.True(second.BodyOffset < 25);
    }

    [Fact]
    public void HoodAndCollarAreSewnRoundTheNecklineOfATemplateGarment()
    {
        var doc = new Document();
        var hoodie = Add(doc, "Fitted Hoodie", new FitOptions { Hood = false, Sleeves = "none" });

        var hood = FittedGarments.Hood(doc, hoodie, Mannequin);
        doc.Garments.Add(hood);
        Assert.Equal(2, hood.Pieces.Count);
        Assert.Single(hood.Seams);                                           // the two panels, down the middle
        Assert.Equal(6, hood.SurfaceStitches.Count);                         // back and two front runs per side
        Assert.All(hood.SurfaceStitches, s => Assert.Contains(hoodie.Pieces, p => p.Id == s.TargetPieceId));
        Assert.True(hood.BodyOffset > hoodie.BodyOffset);

        var collar = FittedGarments.Collar(doc, hoodie, Mannequin, heightCm: 9, ease: 1.2f, frontShare: 0.4f);
        Assert.Single(collar.Pieces);
        Assert.Equal(6, collar.SurfaceStitches.Count);
        // Cut a fifth longer than the line it is sewn to, so it gathers.
        var band = collar.Pieces[0];
        float sewn = collar.SurfaceStitches.Sum(s => Vector2.Distance(s.TargetStart, s.TargetEnd));
        float cut = collar.SurfaceStitches.Sum(s => Outline.EdgeLength(band, s.SourceEdge));
        Assert.Equal(1.2f, cut / sewn, 0.03f);
        // The stitch lines are inside the pieces they are sewn to.
        foreach (var stitch in collar.SurfaceStitches.Concat(hood.SurfaceStitches))
        {
            var host = hoodie.FindPiece(stitch.TargetPieceId)!;
            var mesh = Mesher.Build(hoodie).Find(host.Id)!;
            foreach (var point in new[] { stitch.TargetStart, stitch.TargetEnd })
            {
                Assert.True(SurfaceLocator.TryLocate(mesh, Mesher.Arrange(host, mesh), point, false, out _, out _, out float off));
                Assert.True(off < 0.3f, $"stitch point {point} is {off:0.##} cm off {host.Name}");
            }
        }
    }

    [Fact]
    public void ARolledCollarIsSewnDownAlongBothEdges()
    {
        var doc = new Document();
        var hoodie = Add(doc, "Fitted Hoodie", new FitOptions { Hood = false, Sleeves = "none", OpenFront = true });
        var roll = FittedGarments.Collar(doc, hoodie, Mannequin, heightCm: 18, ease: 1.2f, frontShare: 0.6f, flare: 1f, rollReachCm: 3f);
        Assert.Equal(12, roll.SurfaceStitches.Count);
        var band = roll.Pieces[0];
        Assert.All(roll.SurfaceStitches, s => Assert.InRange(s.SourceEdge, 0, band.EdgeCount - 1));
        Assert.Equal(12, roll.SurfaceStitches.Select(s => s.SourceEdge).Distinct().Count());
        // The top edge is sewn further from the neck than the bottom, so it is cut longer.
        float bottom = roll.SurfaceStitches.Take(6).Sum(s => Outline.EdgeLength(band, s.SourceEdge));
        float top = roll.SurfaceStitches.Skip(6).Sum(s => Outline.EdgeLength(band, s.SourceEdge));
        Assert.True(top > bottom);
        foreach (var stitch in roll.SurfaceStitches)
        {
            var host = hoodie.FindPiece(stitch.TargetPieceId)!;
            var mesh = Mesher.Build(hoodie).Find(host.Id)!;
            foreach (var point in new[] { stitch.TargetStart, stitch.TargetEnd })
                Assert.True(SurfaceLocator.TryLocate(mesh, Mesher.Arrange(host, mesh), point, false, out _, out _, out float off) && off < 0.3f);
        }
    }

    [Fact]
    public void ATemplateGarmentKeepsItsDrapeThroughSaveAndLoad()
    {
        // Pattern coordinates are stored rounded; a piece that meshed differently after loading would lose its drape.
        var doc = new Document();
        var g = Add(doc, "Fitted Trousers");
        var before = Mesher.Build(g);
        foreach (var pm in before.Pieces)
        {
            var piece = g.FindPiece(pm.PieceId)!;
            piece.Drape = Mesher.Arrange(piece, pm).SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
            piece.DrapeKey = pm.Key;
        }
        var loaded = GarmentFile.Deserialize(GarmentFile.Serialize(doc));
        var after = Mesher.Build(loaded.Garments[0]);
        foreach (var pm in after.Pieces)
            Assert.Equal(pm.Key, loaded.Garments[0].FindPiece(pm.PieceId)!.DrapeKey);
    }

    [Fact]
    public void FinerMeshesGetMoreSubsteps()
    {
        Assert.Equal(1, ClothSolver.SubstepsForMesh(2f));        // no floor: the setting decides
        Assert.Equal(1, ClothSolver.SubstepsForMesh(1f));
        int at85 = ClothSolver.SubstepsForMesh(0.85f), at70 = ClothSolver.SubstepsForMesh(0.7f);
        Assert.True(at85 >= 20);
        Assert.True(at70 > at85);
        Assert.True(ClothSolver.SubstepsForMesh(0.3f) <= 64);
    }

    [Fact]
    public void TheOldAndFittedTemplatesAreAllListedAndCreatable()
    {
        foreach (string name in Templates.Names)
        {
            var doc = new Document();
            var made = Templates.CreateAll(name, doc);
            Assert.NotEmpty(made);
            Assert.All(made, g => Assert.NotEmpty(g.Pieces));
        }
    }
}
