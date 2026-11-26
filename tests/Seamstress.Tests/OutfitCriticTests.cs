using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;
using Seamstress.Mcp;

namespace Seamstress.Tests;

/// <summary>
/// critique_outfit: each check finds the problem built into a small synthetic garment, and stays quiet on the same
/// garment without it. The "body" is an upright cylinder of radius 15 cm round the Y axis.
/// </summary>
public class OutfitCriticTests
{
    private const float BodyRadius = 0.15f;

    private sealed class Scene
    {
        public Document Doc { get; } = new();
        private readonly Dictionary<string, GarmentMesh> _meshes = new();
        public Avatar? Avatar { get; set; } = CylinderBody();

        public GarmentMesh MeshFor(Garment g)
        {
            if (!_meshes.TryGetValue(g.Id, out var mesh))
                _meshes[g.Id] = mesh = Mesher.Build(g);
            return mesh;
        }

        public Garment Garment(string name, int layer, float bodyOffset = 0)
        {
            var g = new Garment { Name = name, Layer = layer, BodyOffset = bodyOffset, ParticleDistance = 1f };
            Doc.Garments.Add(g);
            return g;
        }

        public List<CritiqueProblem> Critique(bool motion = false, bool? worn = false)
            => OutfitCritic.Critique(Doc, MeshFor, Avatar, new CritiqueOptions { CheckMotion = motion, LookWorn = worn });
    }

    private static Avatar CylinderBody() => new()
    {
        Name = "cylinder",
        Mesh = new TriMesh { Positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, Triangles = new[] { 0, 1, 2 } },
        Sdf = SdfGrid.FromFunction(p => MathF.Sqrt(p.X * p.X + p.Z * p.Z) - BodyRadius,
            new Vector3(-0.5f, -0.1f, -0.5f), new Vector3(0.5f, 2.1f, 0.5f), 0.01f),
    };

    private static PatternPiece Rect(Garment g, string name, float w, float h)
    {
        var piece = new PatternPiece
        {
            Name = name,
            Points = new() { new PatternPoint(0, 0), new PatternPoint(w, 0), new PatternPoint(w, h), new PatternPoint(0, h) },
        };
        g.Pieces.Add(piece);
        return piece;
    }

    /// <summary>Drapes a piece by mapping each of its pattern points (cm) to a 3D position.</summary>
    private static void Drape(Scene s, Garment g, PatternPiece piece, Func<Vector2, Vector3> at)
    {
        var mesh = s.MeshFor(g).Find(piece.Id)!;
        piece.Drape = mesh.Points.SelectMany(p => { var q = at(p); return new[] { q.X, q.Y, q.Z }; }).ToArray();
        piece.DrapeKey = mesh.Key;
    }

    /// <summary>A pattern point wrapped round the body at a radius: u runs round the front, v up from y0.</summary>
    private static Vector3 Wrap(Vector2 p, float radius, float y0 = 1f, float u0 = 0f)
    {
        float angle = (p.X - u0) * 0.01f / radius;
        return new Vector3(radius * MathF.Sin(angle), y0 + p.Y * 0.01f, radius * MathF.Cos(angle));
    }

    private static Vector3 Flat(Vector2 p, float dx = 0, float z = 0.4f) => new(dx + p.X * 0.01f, 1f + p.Y * 0.01f, z);

    // ---- Open seams ----------------------------------------------------------------------------------------------

    private static Scene TwoPanels(float gapCm)
    {
        var s = new Scene { Avatar = null };
        var g = s.Garment("Skirt", 0);
        var a = Rect(g, "Left panel", 20, 30);
        var b = Rect(g, "Right panel", 20, 30);
        g.Seams.Add(new Seam { A = new EdgeRef(a.Id, 1), B = new EdgeRef(b.Id, 3) });
        Drape(s, g, a, p => Flat(p));
        Drape(s, g, b, p => Flat(p, 0.20f + gapCm * 0.01f));
        return s;
    }

    [Fact]
    public void AnOpenSeamIsFoundWithItsGapAndPlace()
    {
        var problem = Assert.Single(TwoPanels(3f).Critique(), p => p.Type == OutfitCritic.OpenSeam);
        Assert.InRange(problem.Measures["gapMm"], 25f, 35f);
        Assert.Equal("Left panel", problem.Piece);
        Assert.InRange(problem.PatternCm.X, 19.9f, 20.1f);           // on the sewn edge
        Assert.NotNull(problem.Fix.Tool);
        Assert.Empty(TwoPanels(0f).Critique().Where(p => p.Type == OutfitCritic.OpenSeam));
    }

    [Fact]
    public void APieceNeverDrapedIsReported()
    {
        var s = TwoPanels(0f);
        var g = s.Doc.Garments[0];
        g.Pieces[1].ClearDrape();
        var problem = Assert.Single(s.Critique(), p => p.Type == OutfitCritic.NotDraped);
        Assert.Equal("simulate", problem.Fix.Tool);
    }

    // ---- Cloth inside the body -----------------------------------------------------------------------------------

    private static Scene Tube(float radius)
    {
        var s = new Scene();
        var g = s.Garment("Tee", 0);
        var p = Rect(g, "Front", 30, 25);
        Drape(s, g, p, q => Wrap(q, radius, u0: 15));
        return s;
    }

    [Fact]
    public void ClothInsideTheBodyIsFound()
    {
        var problem = Assert.Single(Tube(0.135f).Critique(), p => p.Type == OutfitCritic.BodyPenetration);
        Assert.InRange(problem.Measures["depthMm"], 12f, 18f);
        Assert.True(problem.Vertices > 100);
        Assert.Empty(Tube(0.17f).Critique());
    }

    // ---- Layers --------------------------------------------------------------------------------------------------

    /// <summary>A tee under a jacket; <paramref name="inner"/> gives the tee's radius at each of its pattern points.</summary>
    private static Scene Layers(Func<Vector2, float> inner)
    {
        var s = new Scene();
        var tee = s.Garment("Tee", 0);
        var jacket = s.Garment("Jacket", 1, bodyOffset: 8);
        var t = Rect(tee, "Tee front", 30, 30);
        var j = Rect(jacket, "Jacket front", 40, 40);
        Drape(s, tee, t, q => Wrap(q, inner(q), y0: 1f, u0: 15));
        Drape(s, jacket, j, q => Wrap(q, 0.19f, y0: 0.95f, u0: 20));
        return s;
    }

    [Fact]
    public void AnInnerLayerShowingThroughIsFound()
    {
        // The tee bulges 1 cm out through the jacket in its middle.
        var problems = Layers(q => Vector2.Distance(q, new Vector2(15, 15)) < 4f ? 0.20f : 0.17f).Critique();
        var problem = Assert.Single(problems, p => p.Type == OutfitCritic.ShowThrough);
        Assert.Equal("Tee", problem.Garment);
        Assert.Equal("Jacket", problem.OtherGarment);
        Assert.InRange(problem.Measures["outMm"], 7f, 12f);
        Assert.InRange(Vector2.Distance(problem.PatternCm, new Vector2(15, 15)), 0f, 4f);
        Assert.Equal("set_garment", problem.Fix.Tool);
        Assert.DoesNotContain(problems, p => p.Type == OutfitCritic.HemOrder);

        Assert.Empty(Layers(_ => 0.17f).Critique());
    }

    [Fact]
    public void AHemStackedTheWrongWayRoundIsFound()
    {
        // The tee's hem flips out over the jacket that covers it.
        var problems = Layers(q => q.Y < 1.2f && q.X > 5 && q.X < 25 ? 0.21f : 0.17f).Critique();
        var problem = Assert.Single(problems, p => p.Type == OutfitCritic.HemOrder);
        Assert.Equal("Tee", problem.Garment);
        Assert.InRange(problem.PatternCm.Y, 0f, 1.2f);
        Assert.DoesNotContain(problems, p => p.Type == OutfitCritic.ShowThrough);
    }

    // ---- Trim ----------------------------------------------------------------------------------------------------

    /// <summary>A pocket stitched round its edges onto a tube; <paramref name="lift"/> is its height off it per point.</summary>
    private static Scene Pocket(Func<Vector2, bool, float> lift)
    {
        var s = new Scene();
        var trousers = s.Garment("Trousers", 0);
        var host = Rect(trousers, "Front", 40, 30);
        host.Physics.Thickness = 2;
        Drape(s, trousers, host, q => Wrap(q, 0.17f, u0: 20));
        var trim = s.Garment("Trousers trim", 1, bodyOffset: 3);
        var pocket = Rect(trim, "Pocket", 12, 12);
        pocket.Physics.Thickness = 2;
        var corner = new Vector2(14, 9);
        var outline = new[] { new Vector2(0, 0), new Vector2(12, 0), new Vector2(12, 12), new Vector2(0, 12) };
        for (int e = 0; e < 4; e++)
        {
            trim.SurfaceStitches.Add(new SurfaceStitch
            {
                SourcePieceId = pocket.Id, SourceEdge = e, TargetPieceId = host.Id,
                TargetStart = corner + outline[e], TargetEnd = corner + outline[(e + 1) % 4],
            });
        }
        bool Edge(Vector2 q) => q.X < 0.01f || q.Y < 0.01f || q.X > 11.99f || q.Y > 11.99f;
        Drape(s, trim, pocket, q => Wrap(q + corner, 0.17f + lift(q, Edge(q)), u0: 20));
        return s;
    }

    [Fact]
    public void TrimStandingOffItsHostIsFound()
    {
        var problems = Pocket((_, edge) => edge ? 0.003f : 0.025f).Critique();
        var problem = Assert.Single(problems, p => p.Type == OutfitCritic.TrimFloating);
        Assert.Equal("Pocket", problem.Piece);
        Assert.InRange(problem.Measures["gapMm"], 15f, 25f);
        Assert.Empty(Pocket((_, _) => 0.0035f).Critique());
    }

    [Fact]
    public void TrimPokedThroughByItsHostIsFound()
    {
        var problem = Assert.Single(Pocket((_, _) => -0.005f).Critique(), p => p.Type == OutfitCritic.TrimSunk);
        Assert.Equal("Front", problem.OtherPiece);
        Assert.True(problem.Measures["sharePercent"] > 90);
    }

    // ---- Hardware ------------------------------------------------------------------------------------------------

    private static Scene Snap(float liftMm)
    {
        var s = new Scene();
        var jacket = s.Garment("Jacket", 0);
        var host = Rect(jacket, "Front", 30, 30);
        host.Physics.Thickness = 3;
        Drape(s, jacket, host, q => Wrap(q, 0.17f, u0: 15));
        var hardware = s.Garment("Hardware", 90);
        hardware.Thickness = 3;
        var snap = Rect(hardware, "Snap", 2, 2);
        var mesh = s.MeshFor(hardware).Find(snap.Id)!;
        snap.Pins = Enumerable.Range(0, mesh.VertexCount).ToHashSet();
        snap.PinKey = mesh.Key;
        snap.Attachment = new PieceAttachment { HostPieceId = host.Id, At = new Vector2(15, 15) };
        Drape(s, hardware, snap, q => Wrap(q + new Vector2(14, 14), 0.17f + liftMm * 0.001f, u0: 15));
        return s;
    }

    [Fact]
    public void HardwareFloatingOffItsClothIsFound()
    {
        var problem = Assert.Single(Snap(15f).Critique(), p => p.Type == OutfitCritic.HardwareFloating);
        Assert.Equal("Snap", problem.Piece);
        Assert.InRange(problem.Measures["gapMm"], 10f, 13f);
        Assert.Empty(Snap(3f).Critique());
    }

    [Fact]
    public void HardwareSunkIntoItsClothIsFound()
    {
        var problem = Assert.Single(Snap(-3f).Critique(), p => p.Type == OutfitCritic.HardwareSunk);
        Assert.Equal("Front", problem.OtherPiece);
    }

    // ---- Stretch -------------------------------------------------------------------------------------------------

    private static Scene Panel(float stretch)
    {
        var s = new Scene { Avatar = null };
        var g = s.Garment("Skirt", 0);
        var p = Rect(g, "Panel", 30, 30);
        // Stretched across the middle third only.
        Drape(s, g, p, q =>
        {
            float x = q.X < 10 ? q.X : q.X < 20 ? 10 + (q.X - 10) * (1 + stretch) : 10 + 10 * (1 + stretch) + (q.X - 20);
            return Flat(new Vector2(x, q.Y));
        });
        return s;
    }

    [Fact]
    public void AStretchHotspotIsFound()
    {
        var problem = Assert.Single(Panel(0.15f).Critique(), p => p.Type == OutfitCritic.Stretch);
        Assert.InRange(problem.Measures["strainPercent"], 12f, 16f);
        Assert.InRange(problem.PatternCm.X, 9f, 21f);
        Assert.Empty(Panel(0.04f).Critique());
    }

    // ---- Motion --------------------------------------------------------------------------------------------------

    [Fact]
    public void ClothStillMovingIsFound()
    {
        // A panel left in the air with nothing holding it falls when simulated on.
        var s = new Scene { Avatar = null };
        var g = s.Garment("Rag", 0);
        var p = Rect(g, "Rag", 20, 20);
        Drape(s, g, p, q => Flat(q, z: 0.4f) + new Vector3(0, 0.5f, 0));
        var problem = Assert.Single(s.Critique(motion: true), x => x.Type == OutfitCritic.Moving);
        Assert.True(problem.Measures["speedCmPerSec"] > 20);
        Assert.NotNull(p.Drape);                              // the probe leaves the drape as it was
        Assert.InRange(p.Drape![1], 1.499f, 1.501f);

        // The same panel frozen in place is not simulated, so it is not moving.
        g.Frozen = true;
        Assert.DoesNotContain(s.Critique(motion: true), x => x.Type == OutfitCritic.Moving);
    }

    // ---- Too clean -----------------------------------------------------------------------------------------------

    private static Scene WornJacket(float wrinkleMm)
    {
        var s = new Scene();
        var g = s.Garment("Worn work jacket", 0);
        var p = Rect(g, "Back", 40, 40);
        // Folds 3 cm apart running round the body, as a worn jacket bunches.
        Drape(s, g, p, q => Wrap(q, 0.18f + wrinkleMm * 0.001f * MathF.Sin(q.Y * 2 * MathF.PI / 3f), u0: 20));
        return s;
    }

    [Fact]
    public void AGarmentMeantToLookWornThatIsTooCleanGetsAHint()
    {
        var problem = Assert.Single(WornJacket(0f).Critique(worn: null), p => p.Type == OutfitCritic.TooClean);
        Assert.Equal("hint", problem.Severity);
        Assert.Equal("pull_cloth", problem.Fix.Tool);
        Assert.DoesNotContain(WornJacket(4f).Critique(worn: null), p => p.Type == OutfitCritic.TooClean);
        Assert.Empty(WornJacket(0f).Critique(worn: false));
    }

    [Fact]
    public void ACleanGarmentNotMeantToLookWornGetsNoHint()
    {
        var s = WornJacket(0f);
        s.Doc.Garments[0].Name = "Evening dress";
        Assert.Empty(s.Critique(worn: null));
        Assert.Single(s.Critique(worn: true), p => p.Type == OutfitCritic.TooClean);
    }

    // ---- Ranking and the tool ------------------------------------------------------------------------------------

    [Fact]
    public void ProblemsAreRankedWorstFirst()
    {
        // Cloth deep in the body, and a seam left 30 cm open.
        var s = new Scene();
        var g = s.Garment("Tee", 0);
        var front = Rect(g, "Front", 30, 25);
        var extra = Rect(g, "Loose", 10, 10);
        g.Seams.Add(new Seam { A = new EdgeRef(front.Id, 0), B = new EdgeRef(extra.Id, 2) });
        Drape(s, g, front, q => Wrap(q, 0.12f, u0: 15));
        Drape(s, g, extra, q => Wrap(q, 0.2f, y0: 0.6f, u0: 5));
        var problems = s.Critique();
        Assert.True(problems.Count >= 2);
        for (int i = 1; i < problems.Count; i++)
            Assert.True(problems[i - 1].Score >= problems[i].Score);
    }

    [Fact]
    public void TheToolIsRegisteredAndReportsProblems()
    {
        var session = new GarmentSession();
        var tools = new McpToolRegistry(session);
        var names = tools.GetToolDefinitions().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("critique_outfit", names);
        Assert.Contains("compare_to_reference", names);

        var g = session.Workspace.AddTemplate("Fitted Tee", session.Avatar.Mesh.Bounds().Max.Y, session.Avatar.Mesh);
        var json = System.Text.Json.JsonSerializer.Serialize(
            tools.Dispatch("critique_outfit", System.Text.Json.JsonDocument.Parse("{\"checkMotion\": false}").RootElement));
        Assert.Contains("not_draped", json);     // the tee has not been simulated
        Assert.Contains(g.Id, json);
    }
}
