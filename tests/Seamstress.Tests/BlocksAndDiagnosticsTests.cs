using System.Numerics;
using System.Text;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Tests;

public class BlocksAndDiagnosticsTests
{
    private static PatternPiece Rect(string name, float w, float h, Vector3 pos, float ry = 0)
        => new()
        {
            Name = name,
            Points = new List<PatternPoint>
            {
                new(0, 0) { EdgeName = "bottom" }, new(w, 0) { EdgeName = "right" },
                new(w, h) { EdgeName = "top" }, new(0, h) { EdgeName = "left" },
            },
            Placement = new Placement { Position = pos, Rotation = new Vector3(0, ry, 0) },
        };

    [Fact]
    public void GarmentFileRoundTripsBodyOffsetEdgeNamesAndTileSize()
    {
        var doc = new Document();
        var garment = new Garment { Name = "Jacket", Layer = 4, BodyOffset = 7.5f };
        garment.Pieces.Add(Rect("Front", 30, 40, new Vector3(0, 1.2f, 0.25f)));
        doc.Garments.Add(garment);
        var material = new FabricMaterial { Name = "Canvas", Texture = "canvas.png", TileSizeCm = 24 };
        doc.Materials.Add(material);
        garment.Pieces[0].MaterialId = material.Id;

        var loaded = GarmentFile.Deserialize(GarmentFile.Serialize(doc));

        Assert.Equal(7.5f, loaded.Garments[0].BodyOffset, 0.001f);
        Assert.Equal(new[] { "bottom", "right", "top", "left" }, loaded.Garments[0].Pieces[0].Points.Select(p => p.EdgeName));
        Assert.Equal(2, loaded.Garments[0].Pieces[0].EdgeNamed("TOP"));
        Assert.Equal(24f, loaded.Materials[0].TileSizeCm, 0.001f);
        Assert.False(loaded.Materials[0].FitsTextureToPiece);
        Assert.Equal(1f, loaded.Materials[0].TextureScale);
    }

    [Fact]
    public void TileSizeGivesTheSameTexelSizeWhateverTheLayout()
    {
        var material = new FabricMaterial { Texture = "t.png", TileSizeCm = 20 };
        var small = new Garment();
        small.Pieces.Add(Rect("A", 40, 40, Vector3.Zero));
        var large = new Garment();
        large.Pieces.Add(Rect("B", 40, 40, Vector3.Zero));
        large.Pieces.Add(Rect("C", 300, 10, Vector3.Zero));
        large.Pieces[1].Origin = new Vector2(100, 0);

        foreach (var g in new[] { small, large })
        {
            var mesh = Mesher.Build(g).Pieces[0];
            var uv = UvLayout.For(g).MapMaterial(g.Pieces[0], mesh, material);
            float span = uv.Max(p => p.X) - uv.Min(p => p.X);
            Assert.Equal(2f, span, 0.01f);      // 40 cm of cloth = two 20 cm tiles
        }
    }

    [Fact]
    public void BodyMeasurementsFindLegsArmsAndLandmarksOnTheMannequin()
    {
        var body = Avatar.CreateMannequin();
        var m = BodyMeasurements.Measure(body.Mesh);

        Assert.InRange(m.HeightCm, 140, 210);
        Assert.InRange(m.CrotchHeightCm, m.HeightCm * 0.3f, m.HeightCm * 0.56f);
        Assert.True(m.ArmpitHeightCm > m.CrotchHeightCm + 25);
        Assert.True(m.NeckHeightCm > m.ArmpitHeightCm);
        Assert.Equal(5, m.LeftLeg.Count);
        Assert.Equal(m.LeftLeg[0].CircumferenceCm, m.RightLeg[0].CircumferenceCm, 1.5f);
        Assert.Contains(m.Torso, s => s.Name == "hips" && s.CircumferenceCm > 60);
        Assert.NotEmpty(m.ShoulderRidge);
        if (m.Arms.Count > 0)
        {
            Assert.True(m.Arms[0].LengthCm > 25);
            Assert.All(m.Arms[0].Sections, s => Assert.InRange(s.CircumferenceCm, 8, 70));
        }
    }

    [Fact]
    public void SeamReportNamesTheOpenSeamAndSuggestsReversingATwistedOne()
    {
        var doc = new Document();
        var garment = new Garment { ParticleDistance = 2 };
        var a = Rect("A", 20, 30, new Vector3(-0.11f, 1f, 0.3f));
        var b = Rect("B", 20, 30, new Vector3(0.11f, 1f, 0.3f));
        garment.Pieces.Add(a);
        garment.Pieces.Add(b);
        // A's right edge runs up, B's left edge runs down: start-to-end is the untwisted way.
        var seam = new Seam { A = new EdgeRef(a.Id, 1), B = new EdgeRef(b.Id, 3), Reversed = true };
        garment.Seams.Add(seam);
        doc.Garments.Add(garment);
        var mesh = Mesher.Build(garment);
        foreach (var pm in mesh.Pieces)
        {
            var piece = garment.FindPiece(pm.PieceId)!;
            piece.Drape = Mesher.Arrange(piece, pm).SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
            piece.DrapeKey = pm.Key;
        }

        var twisted = SeamReport.OpenSeams(doc, _ => mesh, garment);
        Assert.Single(twisted);
        Assert.Equal(seam.Id, twisted[0].Id);
        Assert.Contains("reverse_seam", twisted[0].Hint);

        seam.Reversed = false;
        var open = SeamReport.OpenSeams(doc, _ => Mesher.Build(garment), garment);
        Assert.Single(open);        // 2 cm apart side by side: open, but not twisted
        Assert.DoesNotContain("reverse_seam", open[0].Hint);
    }

    [Fact]
    public void PatchLandsInATrimGarmentAboveItsHostAndIsSewnByKind()
    {
        var doc = new Document();
        var jacket = new Garment { Name = "Jacket", Layer = 4, BodyOffset = 7, Thickness = 2.6f };
        var front = Rect("Front", 40, 50, new Vector3(0, 1.2f, 0.25f));
        jacket.Pieces.Add(front);
        doc.Garments.Add(jacket);
        var footprint = new List<Vector2> { new(10, 10), new(23, 10), new(23, 25), new(10, 25) };

        var pocket = GarmentBlocks.AddPatch(doc, Mesher.Build, front.Id, footprint, "pocket", "Pocket", new Vector2(1.05f, 1));
        var flap = GarmentBlocks.AddPatch(doc, Mesher.Build, front.Id, footprint, "flap", "Flap", Vector2.One, level: 2);

        Assert.Equal("Jacket trim", pocket.Garment.Name);
        Assert.Equal(5, pocket.Garment.Layer);
        Assert.True(pocket.Garment.BodyOffset > jacket.BodyOffset + 2.5f);
        Assert.True(flap.Garment.BodyOffset > pocket.Garment.BodyOffset);
        Assert.Equal(3, pocket.Garment.SurfaceStitches.Count);     // bottom and both sides, top left open
        var top = pocket.Pieces[0].EdgeNamed("top");
        Assert.DoesNotContain(pocket.Garment.SurfaceStitches, s => s.SourceEdge == top);
        Assert.Single(flap.Garment.SurfaceStitches);
        Assert.Equal(flap.Pieces[0].EdgeNamed("top"), flap.Garment.SurfaceStitches[0].SourceEdge);
        // The patch starts just in front of the host, not on the origin of the 2D window.
        Assert.InRange(pocket.Pieces[0].Placement.Position.Z, 0.25f, 0.29f);
        Assert.NotEqual(front.Origin, pocket.Pieces[0].Origin);
    }

    [Fact]
    public void TubeWrapsItsLimbAndSewsItsOwnSeam()
    {
        var doc = new Document();
        var garment = new Garment { Name = "Sleeves" };
        doc.Garments.Add(garment);
        var start = new Vector3(0.25f, 1.4f, 0);
        var axis = Vector3.Normalize(new Vector3(0.88f, -0.47f, 0));

        var tube = GarmentBlocks.AddTube(doc, garment, "Sleeve", start, axis, Vector3.UnitY, 50, 40, 30, splitStart: true, capCm: 3);

        Assert.Single(garment.Seams);
        Assert.True(tube.EdgeNamed("start_front") >= 0 && tube.EdgeNamed("start_back") >= 0 && tube.EdgeNamed("end") >= 0);
        var mesh = Mesher.Build(garment).Pieces[0];
        var positions = Mesher.Arrange(tube, mesh);
        // Every point of the arranged tube is about one radius from the limb's axis.
        float radius = 0.50f / (325f * MathF.PI / 180f);
        foreach (var p in positions)
        {
            var d = p - start;
            float off = (d - axis * Vector3.Dot(d, axis)).Length();
            Assert.InRange(off, radius * 0.6f, radius * 1.25f);
        }
        // The start edge's front half really is on the +Z side.
        var front = mesh.EdgeChains[tube.EdgeNamed("start_front")];
        var back = mesh.EdgeChains[tube.EdgeNamed("start_back")];
        Assert.True(front.Average(i => positions[i].Z) > back.Average(i => positions[i].Z));
    }

    [Fact]
    public void HardwareIsPinnedSoItNeverMoves()
    {
        var doc = new Document();
        var jacket = new Garment { Name = "Jacket" };
        var front = Rect("Front", 40, 50, new Vector3(0, 1.2f, 0.25f));
        jacket.Pieces.Add(front);
        doc.Garments.Add(jacket);

        var snap = GarmentBlocks.AddHardware(doc, Mesher.Build, front.Id, new Vector2(20, 25),
            GarmentBlocks.HardwareShape("snap", 1.7f, 0), "Snap", "Hardware", 2.2f, 2.5f);

        var owner = doc.GarmentOfPiece(snap.Id)!;
        var mesh = Mesher.Build(owner).Find(snap.Id)!;
        Assert.Equal(mesh.VertexCount, snap.Pins.Count);
        Assert.Equal(mesh.Key, snap.PinKey);
        Assert.InRange(snap.Placement.Position.Z, 0.25f, 0.26f);
    }

    [Fact]
    public void NewPiecesGetTheirOwnPlaceInThePatternWindow()
    {
        var doc = new Document();
        var g = new Garment();
        doc.Garments.Add(g);
        for (int i = 0; i < 4; i++)
        {
            var piece = Rect($"P{i}", 30, 40, Vector3.Zero);
            PatternLayout.Place(doc, g, piece);
            g.Pieces.Add(piece);
        }
        var boxes = g.Pieces.Select(p => (Min: p.Origin, Max: p.Origin + new Vector2(30, 40))).ToList();
        for (int i = 0; i < boxes.Count; i++)
        {
            for (int j = i + 1; j < boxes.Count; j++)
            {
                bool apart = boxes[i].Max.X <= boxes[j].Min.X || boxes[j].Max.X <= boxes[i].Min.X
                          || boxes[i].Max.Y <= boxes[j].Min.Y || boxes[j].Max.Y <= boxes[i].Min.Y;
                Assert.True(apart, $"pieces {i} and {j} overlap");
            }
        }
        Assert.Equal(4, PatternLayout.ArrangeAll(doc));
    }

    [Fact]
    public void GltfBodyIsReadWithItsNodeTransform()
    {
        // One triangle, moved 1 m up by its node.
        var positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        var bytes = new byte[positions.Length * 4];
        Buffer.BlockCopy(positions, 0, bytes, 0, bytes.Length);
        string json = "{\"asset\":{\"version\":\"2.0\"},\"scenes\":[{\"nodes\":[0]}],"
            + "\"nodes\":[{\"name\":\"Body\",\"mesh\":0,\"translation\":[0,1,0]}],"
            + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],"
            + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}],"
            + "\"bufferViews\":[{\"buffer\":0,\"byteLength\":36}],"
            + $"\"buffers\":[{{\"byteLength\":36,\"uri\":\"data:application/octet-stream;base64,{Convert.ToBase64String(bytes)}\"}}]}}";
        string path = Path.Combine(Path.GetTempPath(), $"seamstress-test-{Guid.NewGuid():N}.gltf");
        File.WriteAllText(path, json, Encoding.UTF8);
        try
        {
            var file = GltfMeshReader.Load(path);
            Assert.Single(file.Parts);
            Assert.Equal("Body", file.Parts[0].Name);
            Assert.Equal(new Vector3(0, 2, 0), file.Parts[0].Positions[2]);
            Assert.Equal(new[] { 0, 1, 2 }, file.Parts[0].Triangles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FbxReaderRejectsTextFilesWithAClearMessage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"seamstress-test-{Guid.NewGuid():N}.fbx");
        File.WriteAllText(path, "; FBX 7.4.0 project file\n");
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => FbxMeshReader.Load(path));
            Assert.Contains("binary", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
