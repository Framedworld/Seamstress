using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Tests;

/// <summary>Trim and hardware go where the garment they are on goes, as in Marvelous Designer.</summary>
public class AttachmentTests
{
    private static readonly TriMesh Body = Avatar.CreateMannequin().Mesh;

    private static (Workspace Workspace, Garment Jacket, PatternPiece Front) Jacket()
    {
        var ws = new Workspace();
        var jacket = ws.AddTemplate("Fitted Jacket", Body.Bounds().Max.Y, Body, new FitOptions { Sleeves = "none" });
        var front = jacket.Pieces.First(p => p.Name.StartsWith("Front L", StringComparison.Ordinal));
        return (ws, jacket, front);
    }

    private static Vector2 Middle(PatternPiece p)
    {
        var lo = p.Points.Select(q => q.Position).Aggregate(Vector2.Min);
        var hi = p.Points.Select(q => q.Position).Aggregate(Vector2.Max);
        return (lo + hi) / 2;
    }

    [Fact]
    public void TrimSewnOntoAGarmentIsSimulatedWithIt()
    {
        var (ws, jacket, front) = Jacket();
        var centre = Middle(front);
        var pocket = ws.AddTrim(front.Id, new List<Vector2> { centre + new Vector2(-6, -6), centre + new Vector2(6, -6), centre + new Vector2(6, 6), centre + new Vector2(-6, 6) }, "Pocket");
        var flap = ws.AddTrim(pocket.Pieces[0].Id, new List<Vector2>
        {
            Middle(pocket.Pieces[0]) + new Vector2(-6, 2), Middle(pocket.Pieces[0]) + new Vector2(6, 2),
            Middle(pocket.Pieces[0]) + new Vector2(6, 6), Middle(pocket.Pieces[0]) + new Vector2(-6, 6),
        }, "Flap");
        ws.SelectPiece(front.Id);

        var simulated = new SimulationController(ws, () => null).GarmentsToSimulate(false);
        Assert.Contains(simulated, g => g.Id == jacket.Id);
        Assert.Contains(simulated, g => g.Id == pocket.Garment.Id);   // sewn onto the jacket
        Assert.Contains(simulated, g => g.Id == flap.Garment.Id);     // sewn onto the pocket

        // A frozen pocket stays out, and keeps its flap out with it.
        ws.SetFrozen(pocket.Garment.Id, true);
        simulated = new SimulationController(ws, () => null).GarmentsToSimulate(false);
        Assert.DoesNotContain(simulated, g => g.Id == pocket.Garment.Id);
        Assert.DoesNotContain(simulated, g => g.Id == flap.Garment.Id);
    }

    [Fact]
    public void HardwareIsCarriedAlongWhenItsClothMoves()
    {
        var (ws, jacket, front) = Jacket();
        var snap = ws.AddHardware(front.Id, Middle(front), "Snap");
        Assert.Equal(front.Id, snap.Attachment?.HostPieceId);

        var doc = ws.Document;
        Attachments.Prepare(doc, ws.MeshFor);
        var snapMesh = ws.MeshFor(doc.GarmentOfPiece(snap.Id)!).Find(snap.Id)!;
        var before = Mesher.CurrentPositions(snap, snapMesh);

        // Move the whole front 10 cm forward and turn it a little, as a simulation might.
        var frontMesh = ws.MeshFor(jacket).Find(front.Id)!;
        var turn = Matrix4x4.CreateRotationY(0.3f);
        var moved = Mesher.CurrentPositions(front, frontMesh)
            .Select(p => Vector3.Transform(p, turn) + new Vector3(0, 0, 0.1f)).ToArray();
        front.Drape = moved.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        front.DrapeKey = frontMesh.Key;
        Assert.Equal(1, Attachments.Follow(doc, ws.MeshFor, new HashSet<string> { front.Id }));

        var after = Mesher.CurrentPositions(snap, snapMesh);
        for (int i = 0; i < before.Length; i++)
        {
            var expected = Vector3.Transform(before[i], turn) + new Vector3(0, 0, 0.1f);
            Assert.True(Vector3.Distance(expected, after[i]) < 0.003f, $"vertex {i} is {Vector3.Distance(expected, after[i]) * 1000:0.0} mm off");
        }
    }

    [Fact]
    public void AHardPartPlacedWithoutAHostIsAttachedToTheClothUnderIt()
    {
        var (ws, jacket, front) = Jacket();
        var snap = ws.AddHardware(front.Id, Middle(front), "Rivet");
        snap.Attachment = null; // as in projects made before hard parts knew their host
        Attachments.Prepare(ws.Document, ws.MeshFor);
        Assert.Equal(front.Id, snap.Attachment?.HostPieceId);
    }

    [Fact]
    public void TheAttachmentIsSaved()
    {
        var (ws, _, front) = Jacket();
        var snap = ws.AddHardware(front.Id, Middle(front), "Button");
        string path = Path.Combine(Path.GetTempPath(), $"attached-{Guid.NewGuid():N}.garment.json");
        try
        {
            GarmentFile.Save(ws.Document, path);
            var loaded = GarmentFile.Load(path).FindPiece(snap.Id)!;
            Assert.Equal(front.Id, loaded.Attachment?.HostPieceId);
            Assert.True(Vector2.Distance(snap.Attachment!.At, loaded.Attachment!.At) < 0.01f);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
