using System.Numerics;
using Seamstress.Core;
using Seamstress.Core.Editing;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Tests;

/// <summary>The building blocks as the app uses them: through the workspace, each one undoable step.</summary>
public class BlockEditingTests
{
    private static readonly TriMesh Body = Avatar.CreateMannequin().Mesh;
    private static readonly BodyMeasurements Measured = BodyMeasurements.Measure(Body);

    private static (Workspace Workspace, Garment Jacket) Jacket(FitOptions? options = null)
    {
        var ws = new Workspace();
        var jacket = ws.AddTemplate("Fitted Jacket", Body.Bounds().Max.Y, Body, options ?? new FitOptions { Sleeves = "none" });
        return (ws, jacket);
    }

    private static PatternPiece Front(Garment g) => g.Pieces.First(p => p.Name.StartsWith("Front L", StringComparison.Ordinal));

    private static List<Vector2> Square(Vector2 centre, float size)
    {
        float h = size / 2;
        return new() { centre + new Vector2(-h, -h), centre + new Vector2(h, -h), centre + new Vector2(h, h), centre + new Vector2(-h, h) };
    }

    private static Vector2 Middle(PatternPiece p)
    {
        var lo = p.Points.Select(q => q.Position).Aggregate(Vector2.Min);
        var hi = p.Points.Select(q => q.Position).Aggregate(Vector2.Max);
        return (lo + hi) / 2;
    }

    [Fact]
    public void FitOptionsReachTheTemplate()
    {
        var (_, closed) = Jacket(new FitOptions { Sleeves = "long" });
        var (_, open) = Jacket(new FitOptions { Sleeves = "none", OpenFront = true, FrontGapCm = 12 });
        Assert.True(closed.Pieces.Count > open.Pieces.Count, "long sleeves add pieces");
        Assert.True(closed.Seams.Count > open.Seams.Count, "a closed front is sewn up the middle");
    }

    [Fact]
    public void APocketIsOneUndoStepOnItsOwnTrimLayer()
    {
        var (ws, jacket) = Jacket();
        int garments = ws.Document.Garments.Count;
        var front = Front(jacket);
        var made = ws.AddTrim(front.Id, Square(Middle(front), 12), "Pocket");

        Assert.Equal(garments + 1, ws.Document.Garments.Count);
        Assert.True(made.Garment.Layer > jacket.Layer);
        Assert.True(made.Garment.BodyOffset > jacket.BodyOffset);
        Assert.Equal(3, made.Garment.SurfaceStitches.Count);            // bottom and sides; the top is the opening
        Assert.Equal("Undo Add pocket", Lang.F("Undo {0}", ws.History.UndoName));

        ws.History.Undo();
        Assert.Equal(garments, ws.Document.Garments.Count);
    }

    [Fact]
    public void AFlapGoesOverThePocketAndHardwareIsPinned()
    {
        var (ws, jacket) = Jacket();
        var front = Front(jacket);
        var pocket = ws.AddTrim(front.Id, Square(Middle(front), 12), "Pocket");
        var flap = ws.AddTrim(front.Id, Square(Middle(front) + new Vector2(0, 6), 12), "Flap");
        Assert.True(flap.Garment.Layer > pocket.Garment.Layer);
        Assert.Single(flap.Garment.SurfaceStitches);                      // sewn along the top only

        var snap = ws.AddHardware(flap.Pieces[0].Id, Middle(flap.Pieces[0]), "Snap");
        Assert.NotEmpty(snap.Pins);
        Assert.True(ws.Document.FindMaterial(snap.MaterialId)!.Metallic > 0.5f);
    }

    [Fact]
    public void CollarAndHoodNeedANeckline()
    {
        var (ws, jacket) = Jacket();
        Assert.True(Workspace.HasNeckline(jacket));
        var collar = ws.AddCollar(jacket.Id, Measured, "Hood roll");
        Assert.True(collar.Seams.Count + collar.SurfaceStitches.Count > 0);
        var hood = ws.AddHood(jacket.Id, Measured, behindNeck: true);
        Assert.Equal(2, hood.Pieces.Count);

        // A plain rectangle has no neckline: the edit is refused and leaves nothing behind.
        var plain = ws.AddGarment("Plain");
        int before = ws.Document.Garments.Count;
        string undo = ws.History.UndoName;
        Assert.False(Workspace.HasNeckline(plain));
        Assert.Throws<ArgumentException>(() => ws.AddCollar(plain.Id, Measured, "Stand"));
        Assert.Equal(before, ws.Document.Garments.Count);
        Assert.Equal(undo, ws.History.UndoName);
    }
}
