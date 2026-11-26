using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Tests;

/// <summary>Deactivating garments (left out of the simulation) and Draft/Final quality (a coarse working mesh).</summary>
public class DeactivateAndQualityTests
{
    private static PatternPiece Sheet(string name, float width, float height, float y = 0.6f)
    {
        var piece = new PatternPiece { Name = name };
        piece.Points.Add(new PatternPoint(0, 0));
        piece.Points.Add(new PatternPoint(width, 0));
        piece.Points.Add(new PatternPoint(width, height));
        piece.Points.Add(new PatternPoint(0, height));
        piece.Placement = new Placement { Position = new Vector3(0, y, 0), Rotation = new Vector3(-90, 0, 0) };
        return piece;
    }

    // ---------------------------------------------------------------- deactivate

    [Fact]
    public void ClothFallsThroughADeactivatedGarmentWhichTheSolverNeverSees()
    {
        // Frozen and with cloth-to-cloth collision on, the table would catch the cloth; deactivated it is not there.
        var table = new Garment { Name = "Table", ParticleDistance = 3, Frozen = true, Deactivated = true };
        table.Pieces.Add(Sheet("Table", 80, 80, 0.60f));
        var cloth = new Garment { Name = "Cloth", ParticleDistance = 2, Layer = 1 };
        cloth.Pieces.Add(Sheet("Cloth", 30, 30, 0.66f));
        ClothAcceleration.Enabled = false;
        try
        {
            var clothMesh = Mesher.Build(cloth);
            using var solver = ClothSolver.Create(new[] { clothMesh }, new[] { Mesher.Build(table) }, null,
                new SimulationSettings { Substeps = 12, SelfCollision = true, GroundPlane = false });
            Assert.DoesNotContain(solver.Pieces, p => p.Source == table);
            Assert.Equal(clothMesh.VertexCount, solver.ParticleCount);
            for (int i = 0; i < 40; i++)
                solver.Step();
            var piece = solver.Pieces.Single();
            float lowest = Enumerable.Range(0, piece.Mesh.VertexCount).Min(i => solver.Positions[piece.Offset + i].Y);
            Assert.True(lowest < 0.58f, $"the cloth should pass through the deactivated table, but stopped at {lowest:0.000} m");

            // Even handed to the solver as a garment to simulate, a deactivated garment is left out.
            using var none = ClothSolver.Create(new[] { Mesher.Build(table) }, Array.Empty<GarmentMesh>(), null, new SimulationSettings());
            Assert.Empty(none.Pieces);
        }
        finally
        {
            ClothAcceleration.Enabled = true;
        }
    }

    [Fact]
    public void ADeactivatedGarmentIsLeftOutOfRunsSavedAndUndoable()
    {
        var ws = new Workspace();
        var skirt = ws.AddTemplate("Skirt");
        var tee = ws.AddTemplate("T-Shirt");
        var controller = new SimulationController(ws, () => null);
        Assert.Contains(controller.GarmentsToSimulate(true), g => g.Id == skirt.Id);

        ws.SetDeactivated(skirt.Id, true);
        Assert.True(ws.Document.FindGarment(skirt.Id)!.Deactivated);
        Assert.Equal("Deactivate garment", ws.History.UndoName);
        Assert.DoesNotContain(controller.GarmentsToSimulate(true), g => g.Id == skirt.Id);
        Assert.Contains(controller.GarmentsToSimulate(true), g => g.Id == tee.Id);
        ws.SetActiveGarment(skirt.Id);
        Assert.Contains("deactivated", controller.Start(false) ?? "");
        Assert.False(controller.IsRunning);

        string path = Path.Combine(Path.GetTempPath(), $"deactivated-{Guid.NewGuid():N}.garment.json");
        try
        {
            GarmentFile.Save(ws.Document, path);
            var loaded = GarmentFile.Load(path);
            Assert.True(loaded.FindGarment(skirt.Id)!.Deactivated);
            Assert.False(loaded.FindGarment(tee.Id)!.Deactivated);
        }
        finally
        {
            File.Delete(path);
        }

        ws.History.Undo();
        Assert.False(ws.Document.FindGarment(skirt.Id)!.Deactivated);
        Assert.Contains(controller.GarmentsToSimulate(true), g => g.Id == skirt.Id);
        ws.History.Redo();
        Assert.True(ws.Document.FindGarment(skirt.Id)!.Deactivated);
        Assert.True(ws.Document.Clone().FindGarment(skirt.Id)!.Deactivated);
    }

    // ---------------------------------------------------------------- draft and final

    /// <summary>A smooth made-up drape: the sheet bent into a wave, so interpolation errors are easy to bound.</summary>
    private static Vector3 Wave(Vector2 cm) => new(cm.X / 100f, 1f + 0.05f * MathF.Sin(cm.X / 8f), cm.Y / 100f);

    private static (Workspace Ws, Garment Garment, PatternPiece Piece) DrapedSheet()
    {
        var garment = new Garment { Name = "Sheet", ParticleDistance = 0.8f };
        var piece = Sheet("Sheet", 40, 30);
        garment.Pieces.Add(piece);
        var doc = new Document();
        doc.Garments.Add(garment);
        var ws = new Workspace(doc);
        var mesh = ws.MeshFor(garment).Find(piece.Id)!;
        piece.Drape = mesh.Points.SelectMany(p => { var v = Wave(p); return new[] { v.X, v.Y, v.Z }; }).ToArray();
        piece.DrapeKey = mesh.Key;
        // Pins in one corner, and creases at the drape.
        piece.Pins = Enumerable.Range(0, mesh.VertexCount).Where(i => mesh.Points[i].X < 5 && mesh.Points[i].Y < 5).ToHashSet();
        piece.PinKey = mesh.Key;
        Creases.Set(new[] { piece }, p => mesh, 0.6f);
        return (ws, garment, piece);
    }

    private static float WorstError(Workspace ws, Garment garment, string pieceId)
    {
        var piece = ws.Document.FindPiece(pieceId)!;
        var mesh = ws.MeshFor(ws.Document.FindGarment(garment.Id)!).Find(pieceId)!;
        Assert.Equal(mesh.Key, piece.DrapeKey);
        var positions = Mesher.CurrentPositions(piece, mesh);
        return Enumerable.Range(0, mesh.VertexCount).Max(i => Vector3.Distance(positions[i], Wave(mesh.Points[i])));
    }

    [Fact]
    public void DraftAndFinalCarryTheDrapePinsAndCreasesThroughThePattern()
    {
        var (ws, garment, piece) = DrapedSheet();
        string id = piece.Id;
        var final = ws.MeshFor(garment).Find(id)!;
        var originalDrape = piece.Drape!;
        var originalPins = piece.Pins.ToHashSet();

        var toDraft = ws.SetQuality(SimulationQuality.Draft);
        Assert.Equal(SimulationQuality.Draft, ws.Quality);
        Assert.Equal(new QualityChange(1, 1, 1, 1), toDraft);
        var draftPiece = ws.Document.FindPiece(id)!;
        var draft = ws.MeshFor(ws.Document.Garments[0]).Find(id)!;
        Assert.True(draft.VertexCount * 4 < final.VertexCount, $"draft {draft.VertexCount} vertices, final {final.VertexCount}");
        Assert.True(WorstError(ws, garment, id) < 0.001f, "the draft drape follows the final one");
        Assert.Equal(draft.Key, draftPiece.PinKey);
        Assert.NotEmpty(draftPiece.Pins);
        Assert.All(draftPiece.Pins, i => Assert.True(draft.Points[i].X < 5 + 2.1f && draft.Points[i].Y < 5 + 2.1f));
        Assert.True(draftPiece.HasCreasesFor(draft.Key, draft.VertexCount));
        Assert.Equal(0.6f, draftPiece.CreaseStrength);
        var draftDrape = draftPiece.Drape!;

        ws.SetQuality(SimulationQuality.Final);
        Assert.Equal(SimulationQuality.Final, ws.Quality);
        var refined = ws.Document.FindPiece(id)!;
        Assert.Equal(final.Key, ws.MeshFor(ws.Document.Garments[0]).Find(id)!.Key);
        Assert.True(WorstError(ws, garment, id) < 0.003f, "refining keeps the shape to within a few millimetres");
        Assert.True(originalPins.IsSubsetOf(refined.Pins), "every pin survives the round trip");
        Assert.All(refined.Pins, i => Assert.True(final.Points[i].X < 5 + 2.1f && final.Points[i].Y < 5 + 2.1f));
        Assert.True(refined.HasCreasesFor(final.Key, final.VertexCount));

        // Each switch is one undo step and gives back exactly what was there.
        Assert.Equal("Refine to Final quality", ws.History.UndoName);
        ws.History.Undo();
        Assert.Equal(SimulationQuality.Draft, ws.Quality);
        Assert.Equal(draftDrape, ws.Document.FindPiece(id)!.Drape);
        Assert.Equal(draft.Key, ws.MeshFor(ws.Document.Garments[0]).Find(id)!.Key);
        Assert.Equal("Switch to Draft quality", ws.History.UndoName);
        ws.History.Undo();
        Assert.Equal(SimulationQuality.Final, ws.Quality);
        var back = ws.Document.FindPiece(id)!;
        Assert.Equal(originalDrape, back.Drape);
        Assert.Equal(final.Key, back.DrapeKey);
        Assert.True(originalPins.SetEquals(back.Pins));
        Assert.Equal(final.Key, back.PinKey);
        ws.History.Redo();
        Assert.Equal(SimulationQuality.Draft, ws.Quality);
        Assert.Equal(draftDrape, ws.Document.FindPiece(id)!.Drape);
    }

    [Fact]
    public void AProjectSavedAtDraftOpensAtDraftWithItsDrape()
    {
        var (ws, garment, piece) = DrapedSheet();
        ws.SetQuality(SimulationQuality.Draft);
        ws.SetDraftParticleDistance(3);
        Assert.True(WorstError(ws, garment, piece.Id) < 0.002f, "changing the draft distance carries the shape too");
        string path = Path.Combine(Path.GetTempPath(), $"draft-{Guid.NewGuid():N}.garment.json");
        try
        {
            GarmentFile.Save(ws.Document, path);
            var reopened = new Workspace(GarmentFile.Load(path));
            Assert.Equal(SimulationQuality.Draft, reopened.Quality);
            Assert.Equal(3f, reopened.Document.Simulation.DraftParticleDistance);
            Assert.True(WorstError(reopened, garment, piece.Id) < 0.002f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HardPartsKeepTheirDetailAtDraft()
    {
        var garment = new Garment { Name = "Hardware", ParticleDistance = 0.6f };
        var snap = Sheet("Snap", 3, 3);
        var cloth = Sheet("Patch", 10, 10);
        garment.Pieces.Add(snap);
        garment.Pieces.Add(cloth);
        var doc = new Document();
        doc.Garments.Add(garment);
        var ws = new Workspace(doc);
        var snapMesh = ws.MeshFor(garment).Find(snap.Id)!;
        var clothMesh = ws.MeshFor(garment).Find(cloth.Id)!;
        snap.Pins = Enumerable.Range(0, snapMesh.VertexCount).ToHashSet();
        snap.PinKey = snapMesh.Key;

        ws.SetQuality(SimulationQuality.Draft);
        var draft = ws.MeshFor(ws.Document.Garments[0]);
        Assert.Equal(snapMesh.Key, draft.Find(snap.Id)!.Key);
        Assert.True(Attachments.IsHardPart(ws.Document.FindPiece(snap.Id)!, draft.Find(snap.Id)));
        Assert.True(draft.Find(cloth.Id)!.VertexCount * 4 < clothMesh.VertexCount);
    }

    [Fact]
    public void DraftNeedsNoExtraSubstepsForFineMeshes()
    {
        var garment = new Garment { Name = "Fine", ParticleDistance = 0.85f };
        garment.Pieces.Add(Sheet("Sheet", 20, 20));
        ClothAcceleration.Enabled = false;
        try
        {
            var final = new SimulationSettings { Substeps = 12 };
            using (var solver = ClothSolver.Create(new[] { Mesher.Build(garment) }, Array.Empty<GarmentMesh>(), null, final))
                Assert.True(solver.EffectiveSubsteps > 12);
            var draft = new SimulationSettings { Substeps = 12, Quality = SimulationQuality.Draft };
            using (var solver = ClothSolver.Create(new[] { Mesher.Build(garment, draft.CoarsestParticleDistance) }, Array.Empty<GarmentMesh>(), null, draft))
                Assert.Equal(12, solver.EffectiveSubsteps);
        }
        finally
        {
            ClothAcceleration.Enabled = true;
        }
    }
}
