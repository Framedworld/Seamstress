using System.Numerics;
using System.Text.Json;
using Seamstress.Mcp;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Tests;

public class FoldsDartsPleatsTests
{
    private static PatternPiece Rectangle(float width, float height, string name = "Piece")
    {
        var piece = new PatternPiece { Name = name };
        piece.Points.Add(new PatternPoint(0, 0));
        piece.Points.Add(new PatternPoint(width, 0));
        piece.Points.Add(new PatternPoint(width, height));
        piece.Points.Add(new PatternPoint(0, height));
        return piece;
    }

    private static Garment GarmentOf(PatternPiece piece, float particleDistance = 1.5f)
    {
        var garment = new Garment { Name = "Cloth", ParticleDistance = particleDistance };
        garment.Pieces.Add(piece);
        return garment;
    }

    private static HashSet<long> MeshEdges(PieceMesh mesh)
    {
        var edges = new HashSet<long>();
        for (int t = 0; t < mesh.Triangles.Length; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = mesh.Triangles[t + k], b = mesh.Triangles[t + (k + 1) % 3];
                edges.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a);
            }
        }
        return edges;
    }

    /// <summary>Runs a garment with no body, no gravity and no collisions; returns the final positions of its single piece.</summary>
    private static Vector3[] Simulate(Garment garment, int frames, float gravity = 0)
    {
        ClothAcceleration.Enabled = false;
        try
        {
            using var solver = ClothSolver.Create(new[] { Mesher.Build(garment) }, Array.Empty<GarmentMesh>(), null,
                new SimulationSettings { Gravity = gravity, SelfCollision = false, GroundPlane = false });
            for (int i = 0; i < frames; i++)
                solver.Step();
            return solver.Positions.Take(Mesher.Build(garment).Pieces[0].VertexCount).ToArray();
        }
        finally
        {
            ClothAcceleration.Enabled = true;
        }
    }

    [Fact]
    public void TheMeshHasEdgesAlongEveryInternalLine()
    {
        var piece = Rectangle(40, 30);
        // Straight across from edge to edge, a curve inside the piece, and a line crossing the first.
        piece.InternalLines.Add(InternalLine.Straight(new Vector2(13.3f, 0), new Vector2(13.3f, 30)));
        var curve = new InternalLine();
        curve.Points.Add(new PatternPoint(20, 5, 35, 15));
        curve.Points.Add(new PatternPoint(22, 26));
        piece.InternalLines.Add(curve);
        piece.InternalLines.Add(InternalLine.Straight(new Vector2(4, 21.7f), new Vector2(31, 18.2f)));
        var garment = GarmentOf(piece);
        var mesh = Mesher.Build(garment).Pieces[0];

        Assert.Equal(3, mesh.LineChains.Length);
        var edges = MeshEdges(mesh);
        for (int l = 0; l < 3; l++)
        {
            var chain = mesh.LineChains[l];
            Assert.True(chain.Length >= 5, $"line {l} has only {chain.Length} vertices");
            var polyline = piece.InternalLines[l].Flatten();
            for (int k = 0; k < chain.Length; k++)
            {
                // Every vertex of the chain lies on the line (within the snap at a crossing or at the outline)...
                float off = Outline.DistanceToPolyline(mesh.Points[chain[k]], polyline, false);
                Assert.True(off < 0.75f, $"line {l} vertex {k} is {off:0.00} cm off the line");
                // ...and neighbours on it are joined by a mesh edge, so the cloth can fold exactly there.
                if (k + 1 < chain.Length)
                {
                    int a = chain[k], b = chain[k + 1];
                    Assert.Contains(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a, edges);
                }
            }
        }
        // The line from edge to edge starts and ends on the outline, on boundary vertices.
        Assert.True(mesh.LineChains[0][0] < mesh.BoundaryCount);
        Assert.True(mesh.LineChains[0][^1] < mesh.BoundaryCount);
        // Without lines the mesh is laid out differently: the lines changed it.
        Assert.NotEqual(Mesher.Build(GarmentOf(Rectangle(40, 30))).Pieces[0].Key, mesh.Key);
        // No triangle is turned over.
        for (int t = 0; t < mesh.Triangles.Length; t += 3)
        {
            var ab = mesh.Points[mesh.Triangles[t + 1]] - mesh.Points[mesh.Triangles[t]];
            var ac = mesh.Points[mesh.Triangles[t + 2]] - mesh.Points[mesh.Triangles[t]];
            Assert.True(ab.X * ac.Y - ab.Y * ac.X > 0);
        }
    }

    [Fact]
    public void InternalLinesAndFoldedSeamsAreSaved()
    {
        var piece = Rectangle(40, 30);
        var curve = new InternalLine { Name = "roll line", FoldAngle = 120, Valley = true, FoldStrength = 0.4f };
        curve.Points.Add(new PatternPoint(5, 5, 20, 2));
        curve.Points.Add(new PatternPoint(35, 25));
        piece.InternalLines.Add(curve);
        var garment = GarmentOf(piece);
        PatternFolds.AddPleats(garment, piece, 2, 0.1f, 0.9f, 2, 3);
        var doc = new Document();
        doc.Garments.Add(garment);

        var loaded = GarmentFile.Deserialize(GarmentFile.Serialize(doc)).Garments[0];
        var lines = loaded.Pieces[0].InternalLines;
        // The roll line, and for each of the two pleats its fold, the fold under it and the line it is stitched to.
        Assert.Equal(7, lines.Count);
        Assert.Equal("roll line", lines[0].Name);
        Assert.Equal(120, lines[0].FoldAngle, 0.01f);
        Assert.True(lines[0].Valley);
        Assert.Equal(0.4f, lines[0].FoldStrength, 0.001f);
        Assert.Equal(new Vector2(20, 2), lines[0].Points[0].Curve);
        Assert.Equal(piece.PointCount, loaded.Pieces[0].PointCount);
        Assert.Equal(2, loaded.Seams.Count(s => s.Folded));
        var stitched = lines.Where(l => l.TackLineId != null).ToList();
        Assert.Equal(2, stitched.Count);
        Assert.All(stitched, l => Assert.Contains(lines, o => o.Id == l.TackLineId));
        Assert.Equal(PatternFolds.DefaultPleatStitchDown, stitched[0].TackLength, 0.01f);
        // The clone used for undo keeps them too.
        Assert.Equal(7, doc.Clone().Garments[0].Pieces[0].InternalLines.Count);
    }

    /// <summary>A 30 x 12 cm strip with a fold line across its middle, from the bottom edge to the top.</summary>
    private static Garment FoldedStrip(float angle, bool valley)
    {
        var piece = Rectangle(30, 12, "Strip");
        piece.Physics.Bend = 20;
        if (angle > 0)
            piece.InternalLines.Add(InternalLine.Straight(new Vector2(15, 0), new Vector2(15, 12), angle, valley, 1f));
        return GarmentOf(piece, 1f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFoldLineFoldsTheClothAtTheLineTheWayItIsTold(bool valley)
    {
        var flat = Simulate(FoldedStrip(0, false), 200);
        Assert.True(flat.Max(p => p.Z) - flat.Min(p => p.Z) < 0.002f, "a strip without a fold stays flat");

        var garment = FoldedStrip(90, valley);
        var mesh = Mesher.Build(garment).Pieces[0];
        var positions = Simulate(garment, 300);
        // The two halves meet at the fold at about the angle asked for: the ends are now about 2 x 15 x sin 45 apart
        // instead of 30 cm.
        var left = Enumerable.Range(0, mesh.VertexCount).Where(i => mesh.Points[i].X < 1).ToList();
        var right = Enumerable.Range(0, mesh.VertexCount).Where(i => mesh.Points[i].X > 29).ToList();
        var leftEnd = left.Aggregate(Vector3.Zero, (s, i) => s + positions[i]) / left.Count;
        var rightEnd = right.Aggregate(Vector3.Zero, (s, i) => s + positions[i]) / right.Count;
        float span = Vector3.Distance(leftEnd, rightEnd);
        Assert.InRange(span, 0.17f, 0.26f);

        // A mountain fold's ridge stands out of the side the piece faces (+Z here), a valley fold's into it.
        var chain = mesh.LineChains[0];
        float ridge = chain.Average(i => positions[i].Z);
        float ends = (leftEnd.Z + rightEnd.Z) / 2;
        if (valley)
            Assert.True(ridge < ends - 0.03f, $"valley fold should dip: ridge {ridge:0.000} ends {ends:0.000}");
        else
            Assert.True(ridge > ends + 0.03f, $"mountain fold should stand out: ridge {ridge:0.000} ends {ends:0.000}");

        // The fold is at the line: nothing stands further out (or in) than the line itself.
        float extreme = valley ? positions.Min(p => p.Z) : positions.Max(p => p.Z);
        Assert.True(MathF.Abs(extreme - ridge) < 0.01f, $"the fold is not at the line: ridge {ridge:0.000}, extreme {extreme:0.000}");
    }

    [Fact]
    public void ADartShapesAFlatPieceIntoACone()
    {
        // A wool-like panel: soft enough to shape, firm enough to hold the cone with no body inside it.
        var piece = Rectangle(30, 30, "Panel");
        piece.Physics.Bend = 55;
        var garment = GarmentOf(piece, 1.5f);
        var flat = Simulate(garment, 10);
        Assert.True(flat.Max(p => p.Z) - flat.Min(p => p.Z) < 1e-4f);

        var result = PatternFolds.AddDart(garment, piece, 0, new Vector2(10, 0), new Vector2(20, 0), new Vector2(15, 16));
        Assert.Equal(7, piece.Points.Count);
        Assert.Single(garment.Seams);
        Assert.Equal(new Vector2(15, 16), piece.Points[2].Position);
        Assert.Equal(result.LegA, garment.Seams[0].A.Edge);
        Assert.Equal(result.LegB, garment.Seams[0].B.Edge);

        var gm = Mesher.Build(garment);
        var positions = Simulate(garment, 400);
        // The legs are sewn shut...
        float gap = gm.Stitches.Max(s => Vector3.Distance(positions[s.VertexA], positions[s.VertexB]));
        Assert.True(gap < 0.006f, $"dart left open by {gap * 100:0.0} cm");
        // ...so the cloth rises into a cone around the apex: measured along the cloth's average facing (a cone's
        // axis), the apex stands well out from the far edges, on the side the piece faces.
        var mesh = gm.Pieces[0];
        int apex = mesh.EdgeChains[result.LegA][^1];
        var axis = Vector3.Zero;
        for (int t = 0; t < mesh.Triangles.Length; t += 3)
        {
            var p0 = positions[mesh.Triangles[t]];
            axis += Vector3.Cross(positions[mesh.Triangles[t + 1]] - p0, positions[mesh.Triangles[t + 2]] - p0);
        }
        axis = Vector3.Normalize(axis);
        var rim = new[] { 4, 5, 6 }.SelectMany(e => mesh.EdgeChains[e]).Select(v => positions[v]).ToList();
        var rimCentre = rim.Aggregate(Vector3.Zero, (a, b) => a + b) / rim.Count;
        float height = Vector3.Dot(positions[apex] - rimCentre, axis);
        Assert.True(height > 0.03f, $"cone only {height * 100:0.0} cm high");
    }

    [Fact]
    public void PleatsFoldAndTakeUpTheirDepth()
    {
        // A 60 x 40 cm panel with four 4 cm knife pleats across its top edge (edge 2 runs right to left).
        var piece = Rectangle(60, 40, "Skirt");
        piece.Physics.Bend = 25;
        var garment = GarmentOf(piece, 1.5f);
        var result = PatternFolds.AddPleats(garment, piece, 2, 0.05f, 0.95f, 4, 4);
        Assert.Equal(4f, result.Depth, 0.01f);
        Assert.Equal(8, result.Lines.Count);
        Assert.Equal(4, result.Tacks.Count);
        Assert.Equal(4 + 12, piece.Points.Count);
        Assert.Equal(4, result.Lines.Count(l => l.Valley));
        // Each fold runs from the waist all the way to the hem.
        Assert.All(result.Lines, l => Assert.Equal(40f, l.Length(), 0.05f));

        Assert.Equal(4, result.Placements.Count);
        Assert.Equal(PatternFolds.DefaultPleatStitchDown, result.StitchDown, 0.01f);
        var gm = Mesher.Build(garment);
        var mesh = gm.Pieces[0];
        // Besides each tack along the edge, every fold is stitched to its placement line down to 8 cm.
        int edgeTacks = result.Tacks.Sum(t => mesh.EdgeChains[t.A.Edge].Length - 1);
        Assert.True(gm.Stitches.Count(s => s.Folded) >= edgeTacks + 4 * 4, $"{gm.Stitches.Count(s => s.Folded)} folded stitches");
        var positions = Simulate(garment, 500);
        float gap = gm.Stitches.Max(s => Vector3.Distance(positions[s.VertexA], positions[s.VertexB]));
        Assert.True(gap < 0.01f, $"pleat tack left open by {gap * 100:0.0} cm");

        // All the way down, the panel is narrower by about what the pleats fold under (4 x 2 x 4 = 32 cm).
        foreach (float y in new[] { 30f, 10f })
        {
            var row = Enumerable.Range(0, mesh.VertexCount).Where(i => MathF.Abs(mesh.Points[i].Y - y) < 1f).ToList();
            float min = row.Min(i => positions[i].X), max = row.Max(i => positions[i].X);
            Assert.True(max - min < 0.60f - 0.20f, $"at {y} cm the panel is still {(max - min) * 100:0.0} cm wide");
        }
        // And the folds are pressed: across each fold line the cloth turns right back.
        var bent = 0;
        foreach (var foldLine in result.Lines)
        {
            var chain = mesh.LineChains[piece.InternalLines.IndexOf(foldLine)];
            int mid = chain[chain.Length / 2];
            // Neighbours a couple of centimetres either side of the fold, across it.
            var p = mesh.Points[mid];
            int Near(float dx) => Enumerable.Range(0, mesh.VertexCount).OrderBy(i => Vector2.Distance(mesh.Points[i], p + new Vector2(dx, 0))).First();
            var a = positions[Near(-1.8f)] - positions[mid];
            var b = positions[Near(1.8f)] - positions[mid];
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)) * 180 / MathF.PI;
            if (angle < 90)
                bent++;
        }
        Assert.True(bent >= 6, $"only {bent} of 8 pleat folds are folded past a right angle");
    }

    [Fact]
    public void ADartOnASewnEdgeRenumbersTheOtherSeams()
    {
        var front = Rectangle(30, 30, "Front");
        var back = Rectangle(30, 30, "Back");
        var garment = GarmentOf(front);
        garment.Pieces.Add(back);
        garment.Seams.Add(new Seam { A = new EdgeRef(front.Id, 1), B = new EdgeRef(back.Id, 3) });  // side seam
        garment.Seams.Add(new Seam { A = new EdgeRef(front.Id, 0), B = new EdgeRef(back.Id, 0) });  // hem, will be split
        var result = PatternFolds.AddDart(garment, front, 0, new Vector2(10, 0), new Vector2(20, 0), new Vector2(15, 12));
        Assert.Equal(1, result.RemovedSeams);
        Assert.Equal(2, garment.Seams.Count);
        // The side seam followed its edge, now three further on.
        Assert.Contains(garment.Seams, s => s.A.Matches(front.Id, 4) && s.B.Matches(back.Id, 3));
        Assert.Equal(new Vector2(30, 0), Outline.EdgeStart(front, 4));

        Assert.Throws<ArgumentException>(() => PatternFolds.AddDart(garment, front, 2, new Vector2(25, 30), new Vector2(5, 30), new Vector2(15, 50)));
    }

    [Fact]
    public void AgentsCanDrawFoldLinesDartsAndPleats()
    {
        var session = new GarmentSession();
        var tools = new McpToolRegistry(session);
        foreach (string name in new[] { "add_internal_line", "set_fold_line", "delete_internal_line", "add_dart", "add_pleats" })
            Assert.Contains(tools.GetToolDefinitions(), t => t!["name"]!.GetValue<string>() == name);
        object Call(string name, string json)
        {
            using var args = JsonDocument.Parse(json);
            return tools.Dispatch(name, args.RootElement);
        }
        Call("add_template_garment", "{\"template\":\"Skirt\"}");
        var front = session.Doc.Garments[0].Pieces[0];
        string id = front.Id;
        // The skirt front: 0 hem (0,0)-(70,0), 1 side, 2 waist (55,50)-(15,50), 3 side.
        Call("add_internal_line", $"{{\"pieceId\":\"{id}\",\"points\":[{{\"x\":35,\"y\":0}},{{\"x\":35,\"y\":50}}],\"foldAngle\":120,\"name\":\"press\"}}");
        Assert.Equal(120, front.InternalLines.Single().FoldAngle, 0.01f);
        Call("set_fold_line", $"{{\"pieceId\":\"{id}\",\"lineId\":\"press\",\"valley\":true,\"strengthPercent\":40}}");
        Assert.True(front.InternalLines[0].Valley);
        Assert.Equal(0.4f, front.InternalLines[0].FoldStrength, 0.001f);
        Call("delete_internal_line", $"{{\"pieceId\":\"{id}\",\"lineId\":\"press\"}}");
        Assert.Empty(front.InternalLines);

        Call("add_dart", $"{{\"pieceId\":\"{id}\",\"edge\":2,\"widthCm\":3,\"lengthCm\":12}}");
        Assert.Equal(7, front.Points.Count);
        Assert.Contains(session.Doc.Garments[0].Seams, s => s.A.PieceId == id && s.B.PieceId == id && !s.Folded);
        // The side seam on edge 3 followed its edge to 6.
        Assert.Contains(session.Doc.Garments[0].Seams, s => s.A.Matches(id, 6) || s.B.Matches(id, 6));

        Call("add_pleats", $"{{\"pieceId\":\"{id}\",\"edge\":0,\"count\":3,\"depthCm\":2}}");
        Assert.Equal(9, front.InternalLines.Count); // fold, under and placement line for each pleat
        Assert.Equal(3, session.Doc.Garments[0].Seams.Count(s => s.Folded));
    }

    private static readonly Lazy<Avatar> Mannequin = new(Avatar.CreateMannequin);

    /// <summary>
    /// A wool skirt drafted on the mannequin through the agent tools: front and back panels, their waists
    /// 2 x <paramref name="waistHalfCm"/> wide with five 2.5 cm knife pleats (stitched down, the default), sewn at
    /// the sides. The pleats take the waist in by 25 cm.
    /// </summary>
    private static Document PleatedSkirt(float waistHalfCm)
    {
        var session = new GarmentSession();
        var tools = new McpToolRegistry(session);
        void Call(string name, object args)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(args));
            tools.Dispatch(name, json.RootElement);
        }
        Call("new_project", new { name = "Pleated skirt" });
        Call("create_garment", new { name = "Pleated skirt", particleDistance = 1.2 });
        string garmentId = session.Doc.Garments[0].Id;
        string Panel(string side)
        {
            var points = new object[]
            {
                new { x = -45, y = 47, edge = "hem" }, new { x = 45, y = 47, edge = "side_l" }, new { x = 36, y = 85, edge = "hip_l" },
                new { x = waistHalfCm, y = 102, edge = "waist" }, new { x = -waistHalfCm, y = 102, edge = "hip_r" }, new { x = -36, y = 85, edge = "side_r" },
            };
            Call("add_piece", new { garmentId, name = "Skirt " + side, points, drawnOn = side, depthCm = 16, preset = "Wool" });
            string id = session.Doc.Garments[0].Pieces[^1].Id;
            Call("add_pleats", new { pieceId = id, edge = "waist", count = 5, depthCm = 2.5, fromCm = 3, toCm = 2 * waistHalfCm - 3 });
            return id;
        }
        string front = Panel("front"), back = Panel("back");
        Call("sew_named", new { pieceAId = front, pieceBId = back, names = new[] { "side_l", "side_r", "hip_l", "hip_r" } });
        Call("set_fabric", new { garmentId, preset = "Wool", stretch = 94, shear = 70, bend = 45 });
        return session.Doc;
    }

    /// <summary>Drapes the skirt from flat on the mannequin: the highest point of the cloth after 200 frames, and the widest seam gap.</summary>
    private static (float Top, float Gap) Drape(Document doc, bool selfCollision, bool gpu)
    {
        var garment = doc.Garments[0];
        var mesh = Mesher.Build(garment);
        var settings = doc.Simulation.Clone();
        settings.SelfCollision = selfCollision;
        bool before = ClothAcceleration.Enabled;
        ClothAcceleration.Enabled = gpu;
        try
        {
            using var solver = ClothSolver.Create(new[] { mesh }, Array.Empty<GarmentMesh>(), Mannequin.Value.Sdf, settings);
            for (int f = 0; f < 200; f++)
                solver.Step();
            return (solver.Positions.Take(mesh.VertexCount).Max(p => p.Y), solver.SeamGap);
        }
        finally
        {
            ClothAcceleration.Enabled = before;
        }
    }

    // The mannequin's waist is at 1.04 m and its hips at 0.87 m; a skirt that slips past them ends on the floor.
    // Skirts like these used to: a pleat tacked only along the edge opens below it, so that one row of stitches held
    // the whole skirt and stretched over the hips, and with cloth collision the pleat's own layers pushed each other
    // apart. Whether one fell depended on where its pleats happened to lie, so several waists are tried.
    [Theory]
    [InlineData(27f, true)]
    [InlineData(27f, false)]
    [InlineData(28.5f, true)]
    [InlineData(28.5f, false)]
    [InlineData(30f, true)]
    [InlineData(30f, false)]
    public void APleatedSkirtStaysAtTheWaistWithOrWithoutClothCollision(float waistHalfCm, bool selfCollision)
    {
        var (top, gap) = Drape(PleatedSkirt(waistHalfCm), selfCollision, gpu: false);
        Assert.True(gap < 0.01f, $"seams left {gap * 100:0.0} cm open");
        Assert.True(top > 0.95f, $"the skirt slid down to {top:0.00} m");
    }

    [Fact]
    public void APleatedSkirtStaysAtTheWaistOnTheGpuWithClothCollision()
    {
        if (!Seamstress.Cuda.CudaClothBackend.Register())
            return; // no CUDA device here
        foreach (float waist in new[] { 27f, 28.5f, 30f })
        {
            var (top, gap) = Drape(PleatedSkirt(waist), true, gpu: true);
            Assert.True(gap < 0.01f, $"waist {waist}: seams left {gap * 100:0.0} cm open");
            Assert.True(top > 0.95f, $"waist {waist}: the skirt slid down to {top:0.00} m");
        }
    }
}
