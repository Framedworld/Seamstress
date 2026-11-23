using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Tests;

public class CreaseTests
{
    /// <summary>A 30 x 10 cm strip standing in the z = 0 plane, with no seams, no body and no gravity.</summary>
    private static (Garment Garment, PatternPiece Piece, PieceMesh Mesh) Strip()
    {
        var piece = new PatternPiece { Name = "Strip" };
        piece.Points.Add(new PatternPoint(0, 0));
        piece.Points.Add(new PatternPoint(30, 0));
        piece.Points.Add(new PatternPoint(30, 10));
        piece.Points.Add(new PatternPoint(0, 10));
        piece.Physics.Bend = 85;
        var garment = new Garment { Name = "Cloth", ParticleDistance = 1f };
        garment.Pieces.Add(piece);
        return (garment, piece, Mesher.Build(garment).Find(piece.Id)!);
    }

    /// <summary>The strip folded down its middle into a V: <paramref name="depth"/> is how far the ends stand out (m).</summary>
    private static float[] Folded(PatternPiece piece, PieceMesh mesh, float depth)
    {
        var flat = Mesher.Arrange(piece, mesh);
        var shape = new float[flat.Length * 3];
        for (int i = 0; i < flat.Length; i++)
        {
            shape[i * 3] = flat[i].X;
            shape[i * 3 + 1] = flat[i].Y;
            shape[i * 3 + 2] = depth * MathF.Abs(flat[i].X) / 0.15f;
        }
        return shape;
    }

    /// <summary>How far the strip's ends stand out from its middle after <paramref name="frames"/> frames.</summary>
    private static float FoldDepthAfter(Garment garment, int frames)
    {
        ClothAcceleration.Enabled = false;
        try
        {
            using var solver = ClothSolver.Create(new[] { Mesher.Build(garment) }, Array.Empty<GarmentMesh>(), null,
                new SimulationSettings { Gravity = 0, SelfCollision = false, GroundPlane = false });
            for (int i = 0; i < frames; i++)
                solver.Step();
            float zMin = solver.Positions.Min(p => p.Z), zMax = solver.Positions.Max(p => p.Z);
            return zMax - zMin;
        }
        finally
        {
            ClothAcceleration.Enabled = true;
        }
    }

    [Fact]
    public void SetCreasesBringTheFoldsBackWhereStiffClothWouldSpringFlat()
    {
        // Without creases a slightly bent strip of stiff cloth relaxes towards flat...
        var (plain, plainPiece, plainMesh) = Strip();
        plainPiece.Drape = Folded(plainPiece, plainMesh, 0.02f);
        plainPiece.DrapeKey = plainMesh.Key;
        float relaxed = FoldDepthAfter(plain, 150);
        Assert.True(relaxed < 0.02f, $"uncreased strip should flatten, fold depth {relaxed * 100:0.0} cm");

        // ...with the V-fold set as a crease it springs back into it.
        var (creased, piece, mesh) = Strip();
        piece.Drape = Folded(piece, mesh, 0.07f);
        piece.DrapeKey = mesh.Key;
        Assert.Equal(1, Creases.Set(new[] { piece }, _ => mesh, 1f));
        piece.Drape = Folded(piece, mesh, 0.02f);
        float kept = FoldDepthAfter(creased, 150);
        Assert.True(kept > 0.045f, $"creased strip should fold back, fold depth {kept * 100:0.0} cm");
    }

    [Fact]
    public void CreasesNeedADrapeAreSavedAndCanBeCleared()
    {
        var (garment, piece, mesh) = Strip();
        Assert.Equal(0, Creases.Set(new[] { piece }, _ => mesh));          // nothing draped: no folds to keep
        piece.Drape = Folded(piece, mesh, 0.05f);
        piece.DrapeKey = mesh.Key;
        Assert.Equal(1, Creases.Set(new[] { piece }, _ => mesh, 0.6f));
        Assert.NotSame(piece.Drape, piece.Crease);                         // a copy: later drapes do not change it

        var doc = new Document();
        doc.Garments.Add(garment);
        var loaded = GarmentFile.Deserialize(GarmentFile.Serialize(doc)).Garments[0].Pieces[0];
        Assert.Equal(mesh.Key, loaded.CreaseKey);
        Assert.Equal(0.6f, loaded.CreaseStrength, 0.001f);
        Assert.True(loaded.HasCreasesFor(mesh.Key, mesh.VertexCount));
        Assert.Equal(piece.Crease!.Length, loaded.Crease!.Length);

        Assert.Equal(1, Creases.Clear(new[] { loaded }));
        Assert.False(loaded.HasCreasesFor(mesh.Key, mesh.VertexCount));
        Assert.Null(loaded.Crease);
    }
}
