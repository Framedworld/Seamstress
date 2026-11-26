using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Xunit;

namespace Seamstress.Tests;

/// <summary>Easing a pattern where its drape is stretched (PatternEasing), on flat pieces draped by hand.</summary>
public class PatternEasingTests
{
    private static PatternPiece Square(string name, float x0 = 0, float y0 = 0, float size = 30)
    {
        var piece = new PatternPiece { Name = name };
        piece.Points.Add(new PatternPoint(x0, y0));
        piece.Points.Add(new PatternPoint(x0 + size, y0));
        piece.Points.Add(new PatternPoint(x0 + size, y0 + size));
        piece.Points.Add(new PatternPoint(x0, y0 + size));
        return piece;
    }

    /// <summary>Drapes a piece flat, its rows below <paramref name="belowY"/> stretched across by <paramref name="stretch"/> about x = 15.</summary>
    private static void DrapeStretched(PatternPiece piece, PieceMesh mesh, float stretch, float belowY = 10, float centre = 15)
    {
        var positions = mesh.Points.Select(p =>
        {
            float x = p.Y < belowY ? centre + (p.X - centre) * stretch : p.X;
            return new Vector3(x * 0.01f, p.Y * 0.01f, 0);
        }).ToArray();
        piece.Drape = positions.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        piece.DrapeKey = mesh.Key;
    }

    private static (Workspace Ws, Garment Garment, PatternPiece Piece) Setup(float stretch = 1.15f)
    {
        var ws = new Workspace(new Document());
        var garment = new Garment { Name = "Skirt", ParticleDistance = 1.5f };
        var piece = Square("Panel");
        garment.Pieces.Add(piece);
        ws.Document.Garments.Add(garment);
        DrapeStretched(piece, ws.MeshFor(garment).Pieces[0], stretch);
        return (ws, garment, piece);
    }

    [Fact]
    public void OnlyTheStretchedRowsAreWidened()
    {
        var (ws, garment, _) = Setup();
        var plan = PatternEasing.Plan(garment, ws.MeshFor(garment));
        var ease = Assert.Single(plan.Pieces);
        Assert.True(ease.Changes);
        Assert.InRange(ease.MaxRowStrain, 0.12f, 0.16f);
        // 60% of (1.15 / 1.02 - 1) of 30 cm: about 2.3 cm at the stretched bottom.
        Assert.InRange(ease.MostAddedCm, 1.5f, 2.8f);
        Assert.True(ease.AtYCm < 10f, $"most added at y = {ease.AtYCm}");
        Assert.Equal(new Vector2(0, 28), ease.Map(new Vector2(0, 28)));
        Assert.True(ease.Map(new Vector2(0, 2)).X < -0.5f, "the bottom left corner moves out");
        Assert.True(ease.Map(new Vector2(30, 2)).X > 30.5f, "and the bottom right one");
        // Nothing is stretched along the piece but the shear where the stretched rows meet the others.
        Assert.InRange(plan.Pieces[0].MaxColumnStrain, -0.01f, 0.06f);
    }

    [Fact]
    public void MapAndUnmapUndoEachOther()
    {
        var (ws, garment, _) = Setup();
        var ease = PatternEasing.Plan(garment, ws.MeshFor(garment)).Pieces[0];
        foreach (var p in new[] { new Vector2(3, 1), new Vector2(29, 9.5f), new Vector2(12, 17), new Vector2(0, 30) })
        {
            var back = ease.Unmap(ease.Map(p));
            Assert.True(Vector2.Distance(back, p) < 1e-4f, $"{p} came back as {back}");
        }
    }

    [Fact]
    public void ClothAtTheTargetIsLeftAlone()
    {
        var (ws, garment, _) = Setup(stretch: 1.01f);
        var plan = PatternEasing.Plan(garment, ws.MeshFor(garment));
        Assert.False(plan.Pieces[0].Changes);
        Assert.Equal(0, plan.ChangedPieces);
    }

    [Fact]
    public void ElasticUndrapedAndHardwarePiecesAreSkipped()
    {
        var (ws, garment, piece) = Setup();
        piece.Physics.ShrinkWeft = 0.9f;
        Assert.Equal("elastic on purpose", PatternEasing.Plan(garment, ws.MeshFor(garment)).Pieces[0].Skipped);
        Assert.Null(PatternEasing.Plan(garment, ws.MeshFor(garment), new EaseOptions { IncludeElastic = true }).Pieces[0].Skipped);
        piece.Physics.ShrinkWeft = 1f;
        var mesh = ws.MeshFor(garment).Pieces[0];
        piece.PinKey = mesh.Key;
        piece.Pins = Enumerable.Range(0, mesh.VertexCount).ToHashSet();
        Assert.Equal("pinned all over", PatternEasing.Plan(garment, ws.MeshFor(garment)).Pieces[0].Skipped);
        piece.Pins.Clear();
        piece.Drape = null;
        Assert.Equal("not simulated", PatternEasing.Plan(garment, ws.MeshFor(garment)).Pieces[0].Skipped);
    }

    [Fact]
    public void ApplyingKeepsTheDrapeAndOneEasingFullyRelievesIt()
    {
        var (ws, garment, _) = Setup();
        float widthBefore = Outline.EdgeLength(garment.Pieces[0], 0);
        var plan = PatternEasing.Plan(garment, ws.MeshFor(garment), new EaseOptions { Strength = 1f });
        PatternEasing.Apply(ws, plan, "Ease");

        var eased = ws.Document.FindGarment(garment.Id)!;
        var piece = eased.Pieces[0];
        var mesh = ws.MeshFor(eased).Pieces[0];
        Assert.True(Outline.EdgeLength(piece, 0) > widthBefore + 3f, "the bottom edge grew");
        Assert.Equal(30f, Outline.EdgeLength(piece, 2), 0.05f);
        Assert.Equal(mesh.Key, piece.DrapeKey);
        // The cloth is still where it was: the drape spans the same stretched width.
        var drape = Mesher.CurrentPositions(piece, mesh);
        Assert.InRange(drape.Max(p => p.X) - drape.Min(p => p.X), 0.3f * 1.15f - 0.01f, 0.3f * 1.15f + 0.01f);
        // Measured again on the eased pattern, the same drape is now at about the target.
        var again = PatternEasing.Plan(eased, ws.MeshFor(eased)).Pieces[0];
        Assert.InRange(again.MaxRowStrain, -0.01f, 0.045f);

        Assert.True(ws.History.Undo());
        Assert.Equal(widthBefore, Outline.EdgeLength(ws.Document.FindGarment(garment.Id)!.Pieces[0], 0), 0.01f);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.6f)]
    [InlineData(0.737f)]
    [InlineData(1f)]
    public void AnEasedPieceIsStillDrapedAfterSavingAndOpening(float strength)
    {
        // A trouser-leg sized piece with a fine mesh and odd numbers, so the eased outline is anything but round: a
        // drape belongs to its mesh's fingerprint, which the file's rounding of an unsnapped point would change.
        var ws = new Workspace(new Document());
        var garment = new Garment { Name = "Leg", ParticleDistance = 1f };
        var piece = Square("Front", 0, 0, 0);
        piece.Points.Clear();
        piece.Points.Add(new PatternPoint(0, 0));
        piece.Points.Add(new PatternPoint(41.3f, 0));
        piece.Points.Add(new PatternPoint(44.7f, 97.1f));
        piece.Points.Add(new PatternPoint(-2.9f, 97.1f));
        garment.Pieces.Add(piece);
        ws.Document.Garments.Add(garment);
        DrapeStretched(piece, ws.MeshFor(garment).Pieces[0], 1.1373f, belowY: 55f, centre: 20.6f);
        var plan = PatternEasing.Plan(garment, ws.MeshFor(garment), new EaseOptions { Strength = strength });
        Assert.True(plan.Pieces[0].Changes);
        PatternEasing.Apply(ws, plan, "Ease");
        var reopened = GarmentFile.Deserialize(GarmentFile.Serialize(ws.Document));
        var g = reopened.Garments.Single();
        Assert.Equal(Mesher.Build(g).Pieces[0].Key, g.Pieces[0].DrapeKey);
    }

    [Fact]
    public void PatchesAndPinsMoveWithTheEasedClothAndCreasesAreCleared()
    {
        var (ws, garment, piece) = Setup();
        var mesh = ws.MeshFor(garment).Pieces[0];
        // A pin at the bottom right corner, the folds the cloth has now set, and a patch sewn across the stretched rows.
        int corner = Enumerable.Range(0, mesh.VertexCount).OrderBy(i => Vector2.Distance(mesh.Points[i], new Vector2(30, 0))).First();
        piece.Pins = new HashSet<int> { corner };
        piece.PinKey = mesh.Key;
        piece.Crease = (float[])piece.Drape!.Clone();
        piece.CreaseKey = mesh.Key;
        piece.CreaseStrength = 0.8f;
        var trim = new Garment { Name = "Trim" };
        var patch = Square("Patch", 0, 0, 6);
        trim.Pieces.Add(patch);
        trim.SurfaceStitches.Add(new SurfaceStitch { SourcePieceId = patch.Id, SourceEdge = 0, TargetPieceId = piece.Id,
            TargetStart = new Vector2(3, 4), TargetEnd = new Vector2(27, 4) });
        ws.Document.Garments.Add(trim);

        var plan = PatternEasing.Plan(garment, ws.MeshFor(garment), new EaseOptions { PieceIds = new[] { piece.Id } });
        PatternEasing.Apply(ws, plan, "Ease");

        var eased = ws.Document.FindGarment(garment.Id)!.Pieces[0];
        var easedMesh = ws.MeshFor(ws.Document.FindGarment(garment.Id)!).Pieces[0];
        Assert.Equal(easedMesh.Key, eased.PinKey);
        int pinned = Assert.Single(eased.Pins);
        Assert.True(easedMesh.Points[pinned].X > 30.5f, $"the pin followed its corner out to {easedMesh.Points[pinned]}");
        // The creases were set on the stretched cloth and would hold it so: they are cleared, and the plan said so.
        Assert.True(plan.Pieces[0].HasCreases);
        Assert.Null(eased.Crease);
        var stitch = ws.Document.FindGarment(trim.Id)!.SurfaceStitches.Single();
        var ease = plan.Pieces[0];
        Assert.True(stitch.TargetStart.X < 2.9f && stitch.TargetEnd.X > 27.1f, $"the patch's line widened with the cloth: {stitch.TargetStart} {stitch.TargetEnd}");
        Assert.True(Vector2.Distance(stitch.TargetStart, ease.Map(new Vector2(3, 4))) < 1e-3f);
    }

    [Fact]
    public void SeamsWhoseEdgesChangeAreReported()
    {
        var ws = new Workspace(new Document());
        var garment = new Garment { Name = "Two", ParticleDistance = 1.5f };
        var lower = Square("Lower");
        var upper = Square("Upper", 40, 0);
        garment.Pieces.Add(lower);
        garment.Pieces.Add(upper);
        // The lower piece's bottom edge (0) is sewn to the upper piece's top edge (2).
        garment.Seams.Add(new Seam { A = new EdgeRef(lower.Id, 0), B = new EdgeRef(upper.Id, 2) });
        ws.Document.Garments.Add(garment);
        var mesh = ws.MeshFor(garment);
        DrapeStretched(lower, mesh.Find(lower.Id)!, 1.15f);
        DrapeStretched(upper, mesh.Find(upper.Id)!, 1f);
        var plan = PatternEasing.Plan(garment, mesh);
        Assert.Equal(1, plan.ChangedPieces);
        var seam = Assert.Single(plan.Seams);
        Assert.True(seam.AfterA > seam.BeforeA + 1f && MathF.Abs(seam.AfterB - seam.BeforeB) < 0.01f);
        Assert.True(seam.MismatchAfter > seam.MismatchBefore);
        Assert.Contains(plan.Pieces.Single(p => p.PieceId == lower.Id).Edges, e => e.Edge == 0 && e.AfterCm > e.BeforeCm);
    }
}
