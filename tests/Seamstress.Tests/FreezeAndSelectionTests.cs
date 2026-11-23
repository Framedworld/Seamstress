using System.Numerics;
using Seamstress.Core;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Tests;

/// <summary>Freezing a layer to build on, box-selecting pieces in 2D, and keeping the shape a pinch pulled.</summary>
public class FreezeAndSelectionTests
{
    private static PatternPiece HorizontalSheet(string name, float size, float height)
    {
        var piece = new PatternPiece { Name = name };
        piece.Points.Add(new PatternPoint(0, 0));
        piece.Points.Add(new PatternPoint(size, 0));
        piece.Points.Add(new PatternPoint(size, size));
        piece.Points.Add(new PatternPoint(0, size));
        piece.Placement = new Placement { Position = new Vector3(0, height, 0), Rotation = new Vector3(-90, 0, 0) };
        return piece;
    }

    /// <summary>The lowest the cloth gets after falling onto a table it may or may not be able to pass through.</summary>
    private static float LowestAfterFalling(bool tableFrozen)
    {
        var table = new Garment { Name = "Table", ParticleDistance = 3, Frozen = tableFrozen };
        table.Pieces.Add(HorizontalSheet("Table", 80, 0.60f));
        var cloth = new Garment { Name = "Cloth", ParticleDistance = 2, Layer = 1 };
        cloth.Pieces.Add(HorizontalSheet("Cloth", 30, 0.66f));
        ClothAcceleration.Enabled = false;
        try
        {
            // Self-collision off: only freezing makes the table something to land on.
            using var solver = ClothSolver.Create(new[] { Mesher.Build(cloth) }, new[] { Mesher.Build(table) }, null,
                new SimulationSettings { Substeps = 12, SelfCollision = false, GroundPlane = false });
            for (int i = 0; i < 40; i++)
                solver.Step();
            var piece = solver.Pieces.First(p => p.Simulated);
            float lowest = float.MaxValue;
            for (int i = 0; i < piece.Mesh.VertexCount; i++)
                lowest = MathF.Min(lowest, solver.Positions[piece.Offset + i].Y);
            return lowest;
        }
        finally
        {
            ClothAcceleration.Enabled = true;
        }
    }

    [Fact]
    public void ClothLandsOnAFrozenGarmentEvenWithoutSelfCollision()
    {
        Assert.True(LowestAfterFalling(tableFrozen: false) < 0.58f, "an ordinary garment is not collided with when self-collision is off");
        float lowest = LowestAfterFalling(tableFrozen: true);
        Assert.InRange(lowest, 0.598f, 0.62f);
    }

    [Fact]
    public void AFrozenGarmentIsNeverSimulatedAndIsSaved()
    {
        var ws = new Workspace();
        var skirt = ws.AddTemplate("Skirt");
        var controller = new SimulationController(ws, () => null);
        Assert.Contains(controller.GarmentsToSimulate(true), g => g.Id == skirt.Id);

        ws.SetFrozen(skirt.Id, true);
        Assert.True(ws.Document.FindGarment(skirt.Id)!.Frozen);
        Assert.DoesNotContain(controller.GarmentsToSimulate(true), g => g.Id == skirt.Id);
        Assert.Contains("frozen", controller.Start(false) ?? "");

        string path = Path.Combine(Path.GetTempPath(), $"frozen-{Guid.NewGuid():N}.garment.json");
        try
        {
            GarmentFile.Save(ws.Document, path);
            Assert.True(GarmentFile.Load(path).FindGarment(skirt.Id)!.Frozen);
        }
        finally
        {
            File.Delete(path);
        }

        ws.History.Undo();
        Assert.False(ws.Document.FindGarment(skirt.Id)!.Frozen);
    }

    [Fact]
    public void DraggingABoxOverEmptySpaceSelectsThePiecesItTouches()
    {
        var ws = new Workspace();
        var shirt = ws.AddTemplate("T-Shirt");
        var editor = new PatternEditor(ws);
        var pieces = shirt.Pieces.ToList();
        Assert.True(pieces.Count >= 2);

        // Bounds of every piece in pattern centimetres.
        (Vector2 Lo, Vector2 Hi) Bounds(PatternPiece p)
        {
            var pts = p.Points.Select(q => q.Position + p.Origin).ToList();
            return (pts.Aggregate(Vector2.Min), pts.Aggregate(Vector2.Max));
        }
        var all = pieces.Select(Bounds).ToList();
        var lo = all.Select(b => b.Lo).Aggregate(Vector2.Min) - new Vector2(5);
        var hi = all.Select(b => b.Hi).Aggregate(Vector2.Max) + new Vector2(5);

        void Drag(Vector2 from, Vector2 to, bool additive = false)
        {
            editor.PointerDown(from, 0.5f, additive);
            editor.PointerMove((from + to) / 2, 0.5f);
            editor.PointerMove(to, 0.5f);
            editor.PointerUp(to);
        }

        // A box round everything, started in empty space, takes every piece.
        Drag(lo, hi);
        Assert.Equal(pieces.Count, ws.SelectedPieces.Count);

        // A box that only clips the corner of one piece takes just that one.
        var first = Bounds(pieces[0]);
        var outside = new Vector2(first.Lo.X - 3, first.Hi.Y + 3);
        Drag(outside, first.Lo + (first.Hi - first.Lo) * 0.9f);
        Assert.Contains(pieces[0].Id, ws.SelectedPieces);
        Assert.True(ws.SelectedPieces.Count < pieces.Count);

        // A click in empty space clears the selection; Ctrl adds a box to what is selected.
        editor.PointerDown(lo, 0.5f, false);
        editor.PointerUp(lo);
        Assert.Empty(ws.SelectedPieces);
        Drag(outside, first.Lo + (first.Hi - first.Lo) * 0.9f);
        int one = ws.SelectedPieces.Count;
        Drag(lo, hi, additive: true);
        Assert.Equal(pieces.Count, ws.SelectedPieces.Count);
        Assert.True(one < pieces.Count);
    }

    [Fact]
    public void KeepingAPulledShapeIsPartOfThePinchUndoStep()
    {
        var ws = new Workspace();
        var skirt = ws.AddTemplate("Skirt");
        var piece = skirt.Pieces[0];
        var mesh = ws.MeshFor(skirt).Find(piece.Id)!;

        // A "pinch": a recorded change to the drape, as a simulation run commits it.
        ws.History.Begin(Lang.T("Pinch cloth"));
        var flat = Mesher.Arrange(piece, mesh);
        var pulled = new float[flat.Length * 3];
        for (int i = 0; i < flat.Length; i++)
        {
            pulled[3 * i] = flat[i].X;
            pulled[3 * i + 1] = flat[i].Y;
            pulled[3 * i + 2] = 0.05f * MathF.Sin(flat[i].X * 20f);
        }
        ws.Document.FindPiece(piece.Id)!.Drape = pulled;
        ws.Document.FindPiece(piece.Id)!.DrapeKey = mesh.Key;
        ws.History.Commit();

        Assert.True(ws.KeepPulledShape(skirt.Id) > 0);
        Assert.True(ws.Document.FindPiece(piece.Id)!.HasCreasesFor(mesh.Key, mesh.VertexCount));
        Assert.Equal(Lang.T("Pinch cloth"), ws.History.UndoName);

        // One undo takes back both the pull and the creases.
        ws.History.Undo();
        var back = ws.Document.FindPiece(piece.Id)!;
        Assert.Null(back.Drape);
        Assert.False(back.HasCreasesFor(mesh.Key, mesh.VertexCount));
    }
}
