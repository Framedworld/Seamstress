using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Mcp;

/// <summary>A suggested fix: the tool to call and its arguments where one fits, and what to do in words.</summary>
public sealed record CritiqueFix(string? Tool, Dictionary<string, object?>? Args, string Note);

/// <summary>One concrete problem found by <see cref="OutfitCritic"/>, located on a piece in 3D and on its pattern.</summary>
public sealed record CritiqueProblem
{
    public required string Type { get; init; }
    public required string Severity { get; init; }

    /// <summary>0-100: how much the problem spoils the outfit; the list is ranked by it.</summary>
    public required float Score { get; init; }
    public required string Garment { get; init; }
    public required string GarmentId { get; init; }
    public required string Piece { get; init; }
    public required string PieceId { get; init; }

    /// <summary>World position in metres.</summary>
    public required Vector3 At { get; init; }

    /// <summary>The same place on the piece's own pattern, in local centimetres (what pull_cloth and add_patch take).</summary>
    public required Vector2 PatternCm { get; init; }
    public required int Vertices { get; init; }
    public required string Description { get; init; }
    public required CritiqueFix Fix { get; init; }
    public string? OtherGarment { get; init; }
    public string? OtherPiece { get; init; }
    public Dictionary<string, float> Measures { get; init; } = new();

    /// <summary>A render_preview yaw (degrees) that looks at the problem from outside the figure.</summary>
    public float LookYaw { get; init; }
}

public sealed class CritiqueOptions
{
    /// <summary>Only this garment's problems (others still count as what it lies on or under).</summary>
    public string? GarmentId { get; set; }

    /// <summary>Run a short simulation probe to find cloth that is still moving.</summary>
    public bool CheckMotion { get; set; } = true;

    /// <summary>Frames the motion probe runs; motion is measured over all but the first few.</summary>
    public int MotionFrames { get; set; } = 24;

    /// <summary>Whether the garments are meant to look worn (true/false), or null to guess from names and materials.</summary>
    public bool? LookWorn { get; set; }

    public float StretchLimit { get; set; } = 0.08f;
}

/// <summary>
/// Looks over a draped outfit the way a garment artist would before showing it, and lists concrete problems: open
/// seams, cloth inside the body, inner layers showing through outer ones, hems stacked the wrong way, trim and
/// hardware floating or sunk, overstretched cloth, cloth still moving, and (as a soft hint) cloth that looks too clean
/// for a worn garment.
/// </summary>
public static class OutfitCritic
{
    public const string OpenSeam = "open_seam";
    public const string NotDraped = "not_draped";
    public const string BodyPenetration = "cloth_in_body";
    public const string ShowThrough = "layer_show_through";
    public const string HemOrder = "hem_order";
    public const string TrimFloating = "trim_floating";
    public const string TrimSunk = "trim_sunk";
    public const string HardwareFloating = "hardware_floating";
    public const string HardwareSunk = "hardware_sunk";
    public const string Stretch = "stretch_hotspot";
    public const string Moving = "still_moving";
    public const string TooClean = "looks_too_clean";

    public static readonly string[] Checks =
    {
        OpenSeam, NotDraped, BodyPenetration, ShowThrough, HemOrder, TrimFloating, TrimSunk, HardwareFloating,
        HardwareSunk, Stretch, Moving, TooClean,
    };

    private static readonly string[] WornWords =
    {
        "worn", "wear", "distress", "grime", "grimy", "dirt", "dirty", "aged", "weather", "used", "rag", "mechanic",
        "work", "painted", "faded", "stain", "patina", "vintage", "rugged", "battered",
    };

    /// <summary>Everything a check needs to know about one piece as it is now.</summary>
    internal sealed class PieceData
    {
        public required Garment Garment { get; init; }
        public required PatternPiece Piece { get; init; }
        public required PieceMesh Mesh { get; init; }
        public required Vector3[] X { get; init; }
        public required bool Draped { get; init; }
        public required bool Hard { get; init; }
        public required bool Trim { get; init; }
        public required bool[] FreeEdge { get; init; }
        public required bool[] Sewn { get; init; }
        private int[][]? _neighbours;
        private int[][]? _vertexTriangles;
        private Vector3[]? _normals;

        public float ParticleCm => Garment.EffectiveParticleDistance(Piece);
        public Vector3[] Normals => _normals ??= Mesher.ComputeNormals(X, Mesh.Triangles);

        public int[][] Neighbours => _neighbours ??= BuildNeighbours();
        public int[][] VertexTriangles => _vertexTriangles ??= BuildVertexTriangles();

        private int[][] BuildNeighbours()
        {
            var sets = new HashSet<int>[X.Length];
            for (int i = 0; i < sets.Length; i++)
                sets[i] = new HashSet<int>();
            var t = Mesh.Triangles;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                int a = t[k], b = t[k + 1], c = t[k + 2];
                sets[a].Add(b); sets[a].Add(c);
                sets[b].Add(a); sets[b].Add(c);
                sets[c].Add(a); sets[c].Add(b);
            }
            return sets.Select(s => s.ToArray()).ToArray();
        }

        private int[][] BuildVertexTriangles()
        {
            var lists = new List<int>[X.Length];
            for (int i = 0; i < lists.Length; i++)
                lists[i] = new List<int>(6);
            var t = Mesh.Triangles;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                lists[t[k]].Add(k);
                lists[t[k + 1]].Add(k);
                lists[t[k + 2]].Add(k);
            }
            return lists.Select(l => l.ToArray()).ToArray();
        }

        /// <summary>True for a vertex on, or one ring in from, an edge that is not sewn: a hem or an opening.</summary>
        public bool NearFreeEdge(int v)
        {
            if (FreeEdge[v])
                return true;
            foreach (int n in Neighbours[v])
                if (FreeEdge[n])
                    return true;
            return false;
        }
    }

    internal sealed class Scene
    {
        public required Document Doc { get; init; }
        public required Avatar? Avatar { get; init; }
        public required List<PieceData> Pieces { get; init; }
        public required Vector3 Axis { get; init; }

        public IEnumerable<PieceData> Of(Garment g) => Pieces.Where(p => p.Garment == g);

        /// <summary>
        /// <paramref name="n"/> turned to face away from the body at <paramref name="at"/>: the avatar's own outward
        /// direction there, or away from the outfit's vertical centre line when there is no avatar.
        /// </summary>
        public Vector3 Outward(Vector3 at, Vector3 n)
        {
            var away = Vector3.Zero;
            float d = Avatar != null ? Avatar.Sdf.Sample(at, out away) : float.MaxValue;
            if (d == float.MaxValue || !float.IsFinite(away.X))
            {
                away = at - Axis;
                away.Y = 0;
                if (away.LengthSquared() < 1e-10f)
                    return n;
            }
            return Vector3.Dot(n, away) < 0 ? -n : n;
        }
    }

    public static List<CritiqueProblem> Critique(Document doc, Func<Garment, GarmentMesh> meshFor, Avatar? avatar,
        CritiqueOptions? options = null)
    {
        options ??= new CritiqueOptions();
        var scene = BuildScene(doc, meshFor, avatar);
        var focus = options.GarmentId == null ? null
            : doc.FindGarment(options.GarmentId) ?? throw new ArgumentException($"Garment '{options.GarmentId}' not found.");
        bool Mine(Garment g) => focus == null || g == focus;

        var problems = new List<CritiqueProblem>();
        problems.AddRange(OpenSeams(scene, meshFor, Mine));
        problems.AddRange(NotDrapedPieces(scene, Mine));
        problems.AddRange(BodyPenetrations(scene, Mine));
        problems.AddRange(Layering(scene, Mine));
        problems.AddRange(TrimAndHardware(scene, Mine));
        problems.AddRange(Stretched(scene, Mine, options.StretchLimit));
        if (options.CheckMotion)
            problems.AddRange(StillMoving(scene, meshFor, focus, options.MotionFrames));
        problems.AddRange(TooCleanHints(scene, Mine, options.LookWorn));
        problems.Sort((a, b) => b.Score.CompareTo(a.Score));
        return problems.Select(p =>
        {
            var d = p.At - scene.Axis;
            float yaw = d.X * d.X + d.Z * d.Z > 1e-6f ? MathF.Atan2(d.X, d.Z) * 180f / MathF.PI : 0f;
            return p with { LookYaw = MathF.Round(yaw / 5f) * 5f };
        }).ToList();
    }

    internal static Scene BuildScene(Document doc, Func<Garment, GarmentMesh> meshFor, Avatar? avatar)
    {
        var sewnEdges = new HashSet<(string, int)>();
        var trimPieces = new HashSet<string>();
        foreach (var g in doc.Garments)
        {
            foreach (var s in g.Seams)
            {
                sewnEdges.Add((s.A.PieceId, s.A.Edge));
                sewnEdges.Add((s.B.PieceId, s.B.Edge));
            }
            foreach (var s in g.SurfaceStitches)
            {
                sewnEdges.Add((s.SourcePieceId, s.SourceEdge));
                trimPieces.Add(s.SourcePieceId);
            }
        }

        var pieces = new List<PieceData>();
        var sum = Vector3.Zero;
        int count = 0;
        foreach (var g in doc.Garments)
        {
            if (!g.Visible || g.Pieces.Count == 0)
                continue;
            var gm = meshFor(g);
            foreach (var mesh in gm.Pieces)
            {
                var piece = g.FindPiece(mesh.PieceId);
                if (piece == null || !piece.Visible)
                    continue;
                var x = Mesher.CurrentPositions(piece, mesh);
                bool draped = piece.Drape != null && piece.DrapeKey == mesh.Key && piece.Drape.Length == mesh.VertexCount * 3;
                var free = new bool[x.Length];
                var sewn = new bool[x.Length];
                for (int e = 0; e < mesh.EdgeChains.Length; e++)
                {
                    bool isSewn = sewnEdges.Contains((piece.Id, e));
                    foreach (int v in mesh.EdgeChains[e])
                    {
                        if (isSewn) sewn[v] = true;
                        else free[v] = true;
                    }
                }
                pieces.Add(new PieceData
                {
                    Garment = g,
                    Piece = piece,
                    Mesh = mesh,
                    X = x,
                    Draped = draped,
                    Hard = Attachments.IsHardPart(piece, mesh),
                    Trim = trimPieces.Contains(piece.Id),
                    FreeEdge = free,
                    Sewn = sewn,
                });
                foreach (var p in x)
                {
                    sum += p;
                    count++;
                }
            }
        }
        var axis = count > 0 ? sum / count : Vector3.Zero;
        return new Scene { Doc = doc, Avatar = avatar, Pieces = pieces, Axis = axis };
    }

    // ---- Open seams ----------------------------------------------------------------------------------------------

    internal static IEnumerable<CritiqueProblem> OpenSeams(Scene scene, Func<Garment, GarmentMesh> meshFor, Func<Garment, bool> mine)
    {
        foreach (var g in scene.Doc.Garments.Where(g => g.Visible && mine(g)))
        {
            foreach (var issue in SeamReport.OpenSeams(scene.Doc, meshFor, g))
            {
                string? pieceId = null;
                int edge = issue.EdgeA;
                var seam = g.Seams.FirstOrDefault(s => s.Id == issue.Id);
                var stitch = seam == null ? g.SurfaceStitches.FirstOrDefault(s => s.Id == issue.Id) : null;
                pieceId = seam?.A.PieceId ?? stitch?.SourcePieceId;
                var pd = scene.Pieces.FirstOrDefault(p => p.Piece.Id == pieceId);
                if (pd == null || edge < 0 || edge >= pd.Mesh.EdgeChains.Length)
                    continue;
                var chain = pd.Mesh.EdgeChains[edge];
                int mid = chain[chain.Length / 2];
                float score = Math.Clamp(45f + issue.GapMm * 0.8f, 45f, 98f);
                bool reverse = issue.Hint.Contains("reverse_seam", StringComparison.Ordinal);
                var fix = reverse
                    ? new CritiqueFix("reverse_seam", new() { ["seamId"] = issue.Id }, "Reverse the seam, then simulate again.")
                    : issue.Kind == "surfaceStitch"
                        ? new CritiqueFix("simulate", new() { ["garmentId"] = g.Id, ["frames"] = 90 }, issue.Hint)
                        : new CritiqueFix("simulate", new() { ["garmentId"] = g.Id, ["frames"] = 120 },
                            "Simulate longer first; if it stays open: " + issue.Hint);
                yield return Make(OpenSeam, score, pd, mid,
                    issue.Kind == "seam"
                        ? $"The seam joining {issue.PieceA} to {issue.PieceB} is {issue.GapMm:0} mm open. {issue.Hint}"
                        : $"{issue.PieceA} is {issue.GapMm:0} mm off its stitch line on {issue.PieceB}. {issue.Hint}",
                    fix, new() { ["gapMm"] = issue.GapMm, ["edge"] = edge }, otherPiece: issue.PieceB);
            }
        }
    }

    // ---- Pieces never draped -------------------------------------------------------------------------------------

    internal static IEnumerable<CritiqueProblem> NotDrapedPieces(Scene scene, Func<Garment, bool> mine)
    {
        foreach (var group in scene.Pieces.Where(p => mine(p.Garment) && !p.Hard && !p.Draped).GroupBy(p => p.Garment))
        {
            var first = group.First();
            var names = group.Select(p => p.Piece.Name).ToList();
            yield return Make(NotDraped, 60, first, Centre(first),
                $"{(names.Count == 1 ? names[0] + " has" : $"{names.Count} pieces ({string.Join(", ", names.Take(4))}{(names.Count > 4 ? ", ..." : "")}) have")} not been draped yet: they are still flat where they were arranged.",
                new CritiqueFix("simulate", new() { ["garmentId"] = group.Key.Id, ["frames"] = 120 }, "Simulate the garment."),
                new() { ["pieces"] = names.Count });
        }
    }

    // ---- Cloth inside the avatar ---------------------------------------------------------------------------------

    internal static IEnumerable<CritiqueProblem> BodyPenetrations(Scene scene, Func<Garment, bool> mine)
    {
        if (scene.Avatar == null)
            yield break;
        const float tolerance = 0.001f;
        foreach (var pd in scene.Pieces.Where(p => mine(p.Garment) && p.Draped && !p.Hard))
        {
            var depth = new float[pd.X.Length];
            var flagged = new List<int>();
            for (int i = 0; i < pd.X.Length; i++)
            {
                float d = scene.Avatar.Sdf.Sample(pd.X[i], out _);
                if (d < -tolerance && d != float.MaxValue)
                {
                    depth[i] = -d * 1000f;
                    flagged.Add(i);
                }
            }
            foreach (var cluster in Clusters(pd, flagged).Take(3))
            {
                int worst = cluster.MaxBy(v => depth[v]);
                float deepest = depth[worst];
                float score = Math.Clamp(30f + deepest * 3f + MathF.Log2(cluster.Count + 1) * 4f, 30f, 92f);
                yield return Make(BodyPenetration, score, pd, worst,
                    $"{cluster.Count} vertices of {pd.Piece.Name} are inside the body, up to {deepest:0.#} mm deep.",
                    new CritiqueFix("simulate", new() { ["garmentId"] = pd.Garment.Id, ["frames"] = 90 },
                        "Simulate again; if it stays inside, the piece is too tight here (add ease) or the layer over it "
                        + "presses it in (give that layer more bodyOffsetMm)."),
                    new() { ["depthMm"] = Round(deepest) }, count: cluster.Count);
            }
        }
    }

    // ---- Inner layers showing through, hems stacked the wrong way ------------------------------------------------

    /// <summary>A garment that is a surface of its own rather than trim on another or hard parts.</summary>
    private static bool IsShell(Scene scene, Garment g)
    {
        var pieces = scene.Of(g).ToList();
        return pieces.Count > 0 && pieces.Any(p => !p.Hard && !p.Trim);
    }

    private static int CompareLayer(Garment a, Garment b)
    {
        int c = a.Layer.CompareTo(b.Layer);
        return c != 0 ? c : a.BodyOffset.CompareTo(b.BodyOffset);
    }

    /// <summary>
    /// How far (m) a point of an inner layer is outside the outer layer's surface found next to it, or null when that
    /// surface does not cover it. With an avatar the order is the distance from the skin, compared only where the
    /// outer cloth faces the same way as the body under the point (so a sleeve's underside does not count as covering
    /// the side of the torso); without one, the side of the surface the point is on.
    /// </summary>
    private static float? Outside(Scene scene, Vector3 v, SpatialHash.Hit hit)
    {
        var off = v - hit.Point;
        if (scene.Avatar != null)
        {
            float dv = scene.Avatar.Sdf.Sample(v, out var bodyNormal);
            float dc = scene.Avatar.Sdf.Sample(hit.Point, out _);
            if (dv != float.MaxValue && dc != float.MaxValue)
            {
                if (MathF.Abs(Vector3.Dot(hit.Normal, bodyNormal)) < 0.5f)
                    return null;
                // Beside the outer cloth's edge rather than under it: mostly sideways from the surface.
                float along = MathF.Abs(Vector3.Dot(off, hit.Normal));
                if (along < 0.5f * off.Length())
                    return null;
                return dv - dc;
            }
        }
        var n = scene.Outward(hit.Point, hit.Normal);
        float s = Vector3.Dot(off, n);
        return s >= 0.6f * off.Length() ? s : null;
    }

    internal static IEnumerable<CritiqueProblem> Layering(Scene scene, Func<Garment, bool> mine)
    {
        var garments = scene.Doc.Garments.Where(g => g.Visible && scene.Of(g).Any(p => p.Draped && !p.Hard)).ToList();
        foreach (var outer in garments.Where(g => IsShell(scene, g)))
        {
            var outerPieces = scene.Of(outer).Where(p => p.Draped && !p.Hard).ToList();
            var hash = SpatialHash.Build(outerPieces, 0.03f);
            var (oMin, oMax) = Bounds(outerPieces);
            float shown = outer.ShownThicknessMm * 0.0005f;
            foreach (var inner in garments.Where(g => g != outer && CompareLayer(g, outer) < 0 && (mine(g) || mine(outer))))
            {
                foreach (var pd in scene.Of(inner).Where(p => p.Draped && !p.Hard))
                {
                    var hemFlags = new List<int>();
                    var showFlags = new List<int>();
                    var height = new float[pd.X.Length];
                    var over = new (PieceData Piece, int Vertex)[pd.X.Length];
                    for (int i = 0; i < pd.X.Length; i++)
                    {
                        var v = pd.X[i];
                        if (v.X < oMin.X - 0.03f || v.Y < oMin.Y - 0.03f || v.Z < oMin.Z - 0.03f
                            || v.X > oMax.X + 0.03f || v.Y > oMax.Y + 0.03f || v.Z > oMax.Z + 0.03f)
                            continue;
                        if (hash.Closest(v, 0.03f) is not { } hit)
                            continue;
                        if (Outside(scene, v, hit) is not { } s || s <= 0.001f + shown)
                            continue;
                        height[i] = s * 1000f;
                        over[i] = (hit.Piece, hit.Vertex);
                        (pd.NearFreeEdge(i) ? hemFlags : showFlags).Add(i);
                    }
                    foreach (var cluster in Clusters(pd, hemFlags).Where(c => c.Count >= 3).Take(2))
                    {
                        int worst = cluster.MaxBy(v => height[v]);
                        var other = over[worst].Piece;
                        float score = Math.Clamp(35f + height[worst] * 2f + MathF.Log2(cluster.Count + 1) * 4f, 35f, 85f);
                        yield return Make(HemOrder, score, pd, worst,
                            $"The edge of {inner.Name} ({pd.Piece.Name}) lies on the outside of {outer.Name} ({other.Piece.Name}) "
                            + $"over {cluster.Count} vertices, up to {height[worst]:0.#} mm out: the hems are stacked the wrong way round "
                            + $"({outer.Name} is worn over it).",
                            new CritiqueFix("pull_cloth", new()
                            {
                                ["pieceId"] = pd.Piece.Id,
                                ["x"] = Round(pd.Mesh.Points[worst].X),
                                ["y"] = Round(pd.Mesh.Points[worst].Y),
                                ["radiusCm"] = 6,
                                ["outCm"] = -2,
                            }, $"Push the edge under {outer.Name} (or shorten {inner.Name} so it ends above it), then simulate "
                               + $"{outer.Name} with selfCollision: true so the order holds."),
                            new() { ["outMm"] = Round(height[worst]) }, outer.Name, other.Piece.Name, cluster.Count);
                    }
                    foreach (var cluster in Clusters(pd, showFlags).Where(c => c.Count >= 3).Take(2))
                    {
                        int worst = cluster.MaxBy(v => height[v]);
                        var other = over[worst].Piece;
                        float score = Math.Clamp(30f + height[worst] * 2f + MathF.Log2(cluster.Count + 1) * 5f, 30f, 88f);
                        yield return Make(ShowThrough, score, pd, worst,
                            $"{inner.Name} ({pd.Piece.Name}) shows through {outer.Name} ({other.Piece.Name}): {cluster.Count} vertices "
                            + $"are outside it, up to {height[worst]:0.#} mm.",
                            new CritiqueFix("set_garment", new() { ["garmentId"] = outer.Id, ["bodyOffsetMm"] = Round(outer.BodyOffset + height[worst] + 2f) },
                                $"Give {outer.Name} more room over what is under it, then simulate it with selfCollision: true. "
                                + $"If {inner.Name} is simply larger than {outer.Name} here, take it in instead."),
                            new() { ["outMm"] = Round(height[worst]) }, outer.Name, other.Piece.Name, cluster.Count);
                    }
                }
            }
        }
    }

    // ---- Trim and hardware ---------------------------------------------------------------------------------------

    internal static IEnumerable<CritiqueProblem> TrimAndHardware(Scene scene, Func<Garment, bool> mine)
    {
        var byId = scene.Pieces.ToDictionary(p => p.Piece.Id);
        // Patches: sewn onto a host by their edges, they should lie on it, neither standing off nor poked through.
        var hosts = new Dictionary<string, List<PieceData>>();
        foreach (var g in scene.Doc.Garments)
            foreach (var s in g.SurfaceStitches)
                if (byId.TryGetValue(s.TargetPieceId, out var target) && target.Draped)
                {
                    if (!hosts.TryGetValue(s.SourcePieceId, out var list))
                        hosts[s.SourcePieceId] = list = new();
                    if (!list.Contains(target))
                        list.Add(target);
                }
        foreach (var pd in scene.Pieces.Where(p => p.Trim && !p.Hard && p.Draped && mine(p.Garment)))
        {
            if (!hosts.TryGetValue(pd.Piece.Id, out var hostList))
                continue;
            var host = hostList[0];
            var hash = SpatialHash.Build(hostList, 0.03f);
            var hitPiece = new PieceData?[pd.X.Length];
            var h = new float[pd.X.Length];
            var sunk = new List<int>();
            var interior = new List<float>();
            float ts = pd.Garment.EffectiveThicknessMm(pd.Piece) * 0.001f, th = host.Garment.EffectiveThicknessMm(host.Piece) * 0.001f;
            for (int i = 0; i < pd.X.Length; i++)
            {
                if (hash.Closest(pd.X[i], 0.08f) is not { } hit)
                {
                    h[i] = float.NaN;
                    continue;
                }
                var n = scene.Outward(hit.Point, hit.Normal);
                h[i] = Vector3.Dot(pd.X[i] - hit.Point, n);
                hitPiece[i] = hit.Piece;
                if (h[i] < -0.001f)
                    sunk.Add(i);
                if (!pd.FreeEdge[i] && !pd.Sewn[i])
                    interior.Add(h[i]);
            }
            if (sunk.Count > 0 && sunk.Count >= 0.04f * pd.X.Length)
            {
                int worst = sunk.MinBy(v => h[v]);
                host = hitPiece[worst] ?? host;
                float share = sunk.Count / (float)pd.X.Length;
                float score = Math.Clamp(30f + share * 120f + -h[worst] * 1000f * 2f, 30f, 80f);
                yield return Make(TrimSunk, score, pd, worst,
                    $"{host.Piece.Name} pokes through {pd.Piece.Name} over {share * 100:0}% of it, up to {-h[worst] * 1000:0.#} mm.",
                    new CritiqueFix("simulate", new() { ["garmentId"] = pd.Garment.Id, ["frames"] = 60, ["selfCollision"] = true },
                        $"Simulate the trim with self-collision on so {host.Piece.Name}'s folds stay under it; if that is not enough, "
                        + $"give {pd.Garment.Name} a little more bodyOffsetMm."),
                    new() { ["sharePercent"] = Round(share * 100), ["depthMm"] = Round(-h[worst] * 1000) }, host.Garment.Name, host.Piece.Name, sunk.Count);
            }
            if (interior.Count >= 3)
            {
                interior.Sort();
                float median = interior[interior.Count / 2];
                float expected = 0.5f * (ts + th) + MathF.Max(0, pd.Garment.BodyOffset - host.Garment.BodyOffset) * 0.001f;
                float gap = median - expected;
                if (gap > 0.012f)
                {
                    int worst = Enumerable.Range(0, pd.X.Length).Where(i => !float.IsNaN(h[i])).MaxBy(i => h[i]);
                    float score = Math.Clamp(25f + gap * 1000f * 1.5f, 25f, 75f);
                    yield return Make(TrimFloating, score, pd, worst,
                        $"{pd.Piece.Name} stands {gap * 1000:0} mm off {host.Piece.Name} in its middle instead of lying on it.",
                        new CritiqueFix("simulate", new() { ["garmentId"] = pd.Garment.Id, ["frames"] = 90 },
                            $"Simulate {pd.Garment.Name} after {host.Garment.Name} has settled; if it still stands off, its bodyOffsetMm "
                            + "is too large or its fabric too stiff for the curve under it."),
                        new() { ["gapMm"] = Round(gap * 1000) }, host.Garment.Name, host.Piece.Name);
                }
            }
        }

        // Hard parts: seated on the cloth they belong to.
        var cloth = scene.Pieces.Where(p => !p.Hard && p.Draped).ToList();
        SpatialHash? clothHash = null;
        foreach (var pd in scene.Pieces.Where(p => p.Hard && mine(p.Garment)))
        {
            if (pd.X.Length == 0)
                continue;
            var centre = pd.X.Aggregate(Vector3.Zero, (a, b) => a + b) / pd.X.Length;
            PieceData? host = null;
            if (pd.Piece.Attachment is { } att)
                byId.TryGetValue(att.HostPieceId, out host);
            SpatialHash hash;
            if (host != null)
                hash = SpatialHash.Build(new[] { host }, 0.03f);
            else
            {
                hash = clothHash ??= SpatialHash.Build(cloth.Where(c => c.Garment != pd.Garment).ToList(), 0.03f);
                host = hash.Closest(centre, 0.04f)?.Piece;
            }
            float partHalf = pd.Garment.ShownThicknessMm * 0.0005f;
            if (host == null || hash.Closest(centre, 0.06f) is not { } below)
            {
                yield return Make(HardwareFloating, 55, pd, Nearest(pd, centre),
                    $"{pd.Piece.Name} is not on any cloth: nothing is within 4 cm of it.",
                    new CritiqueFix(null, null, "Delete it and place it again with add_hardware on the piece it belongs to."),
                    new());
                continue;
            }
            float hostHalf = host.Garment.EffectiveThicknessMm(host.Piece) * 0.0005f;
            var heights = new float[pd.X.Length];
            int hidden = 0;
            for (int i = 0; i < pd.X.Length; i++)
            {
                var hit = hash.Closest(pd.X[i], 0.08f) ?? below;
                var n = scene.Outward(hit.Point, hit.Normal);
                heights[i] = Vector3.Dot(pd.X[i] - hit.Point, n);
                if (heights[i] + partHalf < hostHalf)
                    hidden++;
            }
            float min = heights.Min(), mean = heights.Average(), extent = heights.Max() - min;
            float airGap = min - partHalf - hostHalf;
            bool flat = extent < 0.006f;
            if (airGap > 0.005f)
            {
                int worst = Array.IndexOf(heights, min);
                float score = Math.Clamp(30f + airGap * 1000f * 2.5f, 30f, 80f);
                yield return Make(HardwareFloating, score, pd, worst,
                    $"{pd.Piece.Name} floats {airGap * 1000:0.#} mm above {host.Piece.Name} instead of sitting on it.",
                    new CritiqueFix(null, null,
                        $"Re-seat it: delete it and add_hardware again on {host.Piece.Name} at the pattern point given (patternCm is on "
                        + $"the part; locate it on {host.Piece.Name} with locate_on_piece), after {host.Garment.Name} has been simulated."),
                    new() { ["gapMm"] = Round(airGap * 1000) }, host.Garment.Name, host.Piece.Name);
            }
            else if ((flat && mean < 0.0005f) || hidden >= 0.9f * pd.X.Length)
            {
                int worst = Array.IndexOf(heights, min);
                float score = Math.Clamp(35f + -min * 1000f * 2f, 35f, 80f);
                yield return Make(HardwareSunk, score, pd, worst,
                    $"{pd.Piece.Name} is sunk into {host.Piece.Name}: its middle is {MathF.Max(0, -mean) * 1000:0.#} mm under the cloth's surface"
                    + (hidden >= 0.9f * pd.X.Length ? " and it is hidden." : "."),
                    new CritiqueFix(null, null,
                        $"Re-seat it after {host.Garment.Name} has settled: delete it and add_hardware again on {host.Piece.Name} "
                        + "(its default liftMm sets it just clear of the cloth)."),
                    new() { ["depthMm"] = Round(MathF.Max(0, -mean) * 1000) }, host.Garment.Name, host.Piece.Name);
            }
        }
    }

    // ---- Stretch hotspots ----------------------------------------------------------------------------------------

    internal static IEnumerable<CritiqueProblem> Stretched(Scene scene, Func<Garment, bool> mine, float limit)
    {
        foreach (var pd in scene.Pieces.Where(p => mine(p.Garment) && p.Draped && !p.Hard))
        {
            float sx = Math.Clamp(pd.Piece.Physics.ShrinkWeft, 0.5f, 1.5f), sy = Math.Clamp(pd.Piece.Physics.ShrinkWarp, 0.5f, 1.5f);
            // Stretch along the threads: each triangle's deformation from the cut pattern (where the pattern's x and y
            // directions went), averaged over the triangles round each vertex, then the longer of the two thread
            // directions. Averaging the deformation cancels the few percent of long-short alternation single edges of a
            // solved mesh have; shear is left out, since cloth shears freely without growing longer along its threads.
            var weft = new Vector3[pd.X.Length];
            var warp = new Vector3[pd.X.Length];
            var count = new int[pd.X.Length];
            var pins = pd.Piece.PinKey == pd.Mesh.Key ? pd.Piece.Pins : null;
            var t = pd.Mesh.Triangles;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                int a = t[k], b = t[k + 1], c = t[k + 2];
                if (pins != null && pins.Contains(a) && pins.Contains(b) && pins.Contains(c))
                    continue;
                var e1 = (pd.Mesh.Points[b] - pd.Mesh.Points[a]) * new Vector2(sx, sy) * 0.01f;
                var e2 = (pd.Mesh.Points[c] - pd.Mesh.Points[a]) * new Vector2(sx, sy) * 0.01f;
                float det = e1.X * e2.Y - e1.Y * e2.X;
                if (MathF.Abs(det) < 1e-10f)
                    continue;
                var f1 = pd.X[b] - pd.X[a];
                var f2 = pd.X[c] - pd.X[a];
                var fx = (f1 * e2.Y - f2 * e1.Y) / det;
                var fy = (f2 * e1.X - f1 * e2.X) / det;
                foreach (int v in new[] { a, b, c })
                {
                    weft[v] += fx;
                    warp[v] += fy;
                    count[v]++;
                }
            }
            var strain = new float[pd.X.Length];
            for (int i = 0; i < strain.Length; i++)
                strain[i] = count[i] > 0 ? MathF.Max(weft[i].Length(), warp[i].Length()) / count[i] - 1f : 0f;
            var flagged = Enumerable.Range(0, strain.Length).Where(i => strain[i] > limit).ToList();
            int minimum = Math.Max(4, pd.X.Length / 100);
            foreach (var cluster in Clusters(pd, flagged).Where(c => c.Count >= minimum).Take(1))
            {
                int worst = cluster.MaxBy(v => strain[v]);
                float mean = cluster.Average(v => strain[v]);
                var lo = cluster.Select(v => pd.Mesh.Points[v]).Aggregate(Vector2.Min);
                var hi = cluster.Select(v => pd.Mesh.Points[v]).Aggregate(Vector2.Max);
                float across = MathF.Max(hi.X - lo.X, hi.Y - lo.Y) + pd.ParticleCm;
                float extraCm = mean * across;
                bool atSeam = cluster.Any(v => pd.Sewn[v]);
                // Judged by the area's mean: a single point pinched at a seam can read several times that.
                float score = Math.Clamp(15f + (mean - limit) * 300f + MathF.Min(10f, MathF.Log2(cluster.Count + 1) * 1.5f), 15f, 85f);
                yield return Make(Stretch, score, pd, worst,
                    $"{pd.Piece.Name} is stretched {mean * 100:0}% over its cut size across {cluster.Count} vertices here "
                    + $"(up to {strain[worst] * 100:0}% at the worst point){(atSeam ? ", pulling at a seam" : "")}.",
                    new CritiqueFix(null, null,
                        $"The cloth is too small here: give the piece about {MathF.Max(0.5f, extraCm):0.#} cm more across this area "
                        + "(redraft it with more ease, or a larger easePercent on the template)"
                        + (atSeam ? ", or check the seam's two edges are the same length (inspect_fit lists mismatches)." : ".")),
                    new() { ["strainPercent"] = Round(strain[worst] * 100), ["meanStrainPercent"] = Round(mean * 100) }, count: cluster.Count);
            }
        }
    }

    // ---- Still moving --------------------------------------------------------------------------------------------

    internal static IEnumerable<CritiqueProblem> StillMoving(Scene scene, Func<Garment, GarmentMesh> meshFor, Garment? focus, int frames)
    {
        frames = Math.Clamp(frames, 8, 240);
        // The first frames of a fresh solver jolt (velocities start at zero, contacts are taken up): measure after them.
        int skip = frames / 2;
        var speed = new Dictionary<string, float[]>();
        // Each garment is probed the way the app simulates it: with what is sewn onto it, everything else held still.
        foreach (var group in SimulationGroups(scene, focus))
        {
            var obstacles = scene.Doc.Garments.Where(g => g.Visible && g.Pieces.Count > 0 && !group.Contains(g)).ToList();
            var start = new Dictionary<string, Vector3[]>();
            using var solver = ClothSolver.Create(group.Select(meshFor).ToList(), obstacles.Select(meshFor).ToList(),
                scene.Avatar?.Sdf, scene.Doc.Simulation.Clone());
            for (int f = 0; f < frames; f++)
            {
                solver.Step();
                if (f != skip - 1 && f != frames - 1)
                    continue;
                foreach (var cp in solver.Pieces.Where(c => c.Simulated))
                {
                    var now = new Vector3[cp.Mesh.VertexCount];
                    Array.Copy(solver.Positions, cp.Offset, now, 0, now.Length);
                    if (f == skip - 1)
                        start[cp.Piece.Id] = now;
                    else if (start.TryGetValue(cp.Piece.Id, out var was))
                    {
                        float seconds = (frames - skip) / 60f;
                        speed[cp.Piece.Id] = now.Select((p, i) => Vector3.Distance(p, was[i]) / seconds).ToArray();
                    }
                }
            }
        }

        // A settled drape still jitters by a few cm/s in places; moving cloth is faster and covers an area.
        const float limit = 0.06f;
        foreach (var pd in scene.Pieces)
        {
            if (!speed.TryGetValue(pd.Piece.Id, out var v) || pd.Hard)
                continue;
            var flagged = Enumerable.Range(0, v.Length).Where(i => v[i] > limit).ToList();
            int minimum = Math.Max(6, v.Length / 50);
            foreach (var cluster in Clusters(pd, flagged).Where(c => c.Count >= minimum).Take(1))
            {
                int worst = cluster.MaxBy(i => v[i]);
                float score = Math.Clamp(15f + (v[worst] - limit) * 120f + MathF.Min(10f, MathF.Log2(cluster.Count + 1) * 1.5f), 15f, 85f);
                yield return Make(Moving, score, pd, worst,
                    $"{pd.Piece.Name} is still moving: {cluster.Count} vertices at up to {v[worst] * 100:0} cm/s when simulated on "
                    + "from where it is now. It has not settled, so what is shown is not its final shape.",
                    new CritiqueFix("simulate", new() { ["garmentId"] = pd.Garment.Id, ["frames"] = 120 },
                        "Simulate until it settles (peakSpeed under 0.08 m/s). If it never does, it is being pushed by a layer "
                        + "it overlaps or a seam that cannot close: look at the other problems at the same place."),
                    new() { ["speedCmPerSec"] = Round(v[worst] * 100) }, count: cluster.Count);
            }
        }
    }

    /// <summary>
    /// Garments that move together when one is simulated: each draped, unfrozen garment with the trim sewn onto it
    /// (and onto that trim), as the app's Simulate takes them.
    /// </summary>
    internal static List<List<Garment>> SimulationGroups(Scene scene, Garment? focus)
    {
        bool Movable(Garment g) => g.Visible && !g.Frozen && g.Pieces.Count > 0 && scene.Of(g).Any(p => p.Draped && !p.Hard);
        var movable = scene.Doc.Garments.Where(Movable).ToList();
        Garment? HostOf(Garment g) => g.SurfaceStitches
            .Select(s => scene.Doc.GarmentOfPiece(s.TargetPieceId))
            .FirstOrDefault(h => h != null && h != g && movable.Contains(h));
        Garment Root(Garment g)
        {
            var seen = new HashSet<Garment>();
            while (HostOf(g) is { } host && seen.Add(g))
                g = host;
            return g;
        }
        var groups = movable.GroupBy(Root).Select(grp => grp.ToList()).ToList();
        return focus == null ? groups : groups.Where(grp => grp.Contains(focus)).ToList();
    }

    // ---- Too clean for a worn garment ----------------------------------------------------------------------------

    internal static bool LooksMeantWorn(Document doc, Garment g)
    {
        if (g.Pieces.Any(p => p.CreaseStrength > 0 && p.Crease != null))
            return true;
        var words = new List<string> { doc.Name, g.Name };
        foreach (var p in g.Pieces)
        {
            words.Add(p.Physics.Preset);
            if (doc.FindMaterial(p.MaterialId) is { } m)
            {
                words.Add(m.Name);
                if (m.Texture != null)
                    words.Add(Path.GetFileNameWithoutExtension(m.Texture));
            }
        }
        return words.Any(w => WornWords.Any(k => w?.Contains(k, StringComparison.OrdinalIgnoreCase) == true));
    }

    /// <summary>
    /// How much fine relief each vertex has: the angle between its normal and the average normal of the cloth round
    /// it. Smooth cloth, curved or flat, scores near 0; wrinkles and creases score several degrees.
    /// </summary>
    internal static float[] Relief(PieceData pd, int smoothing = 4)
    {
        var n = pd.Normals;
        var smooth = (Vector3[])n.Clone();
        var next = new Vector3[n.Length];
        for (int it = 0; it < smoothing; it++)
        {
            for (int i = 0; i < n.Length; i++)
            {
                var s = smooth[i];
                foreach (int j in pd.Neighbours[i])
                    s += smooth[j];
                next[i] = s;
            }
            for (int i = 0; i < n.Length; i++)
            {
                float len = next[i].Length();
                smooth[i] = len > 1e-9f ? next[i] / len : n[i];
            }
        }
        var relief = new float[n.Length];
        for (int i = 0; i < n.Length; i++)
            relief[i] = MathF.Acos(Math.Clamp(Vector3.Dot(n[i], smooth[i]), -1f, 1f)) * 180f / MathF.PI;
        return relief;
    }

    internal static IEnumerable<CritiqueProblem> TooCleanHints(Scene scene, Func<Garment, bool> mine, bool? lookWorn)
    {
        if (lookWorn == false)
            yield break;
        const float cellCm = 8f, flatDegrees = 1.5f, creaseDegrees = 6f;
        foreach (var pd in scene.Pieces.Where(p => mine(p.Garment) && p.Draped && !p.Hard))
        {
            if (lookWorn == null && !LooksMeantWorn(scene.Doc, pd.Garment))
                continue;
            float areaCm2 = MathF.Abs(Outline.SignedArea(pd.Piece.Points.Select(q => q.Position).ToList()));
            if (areaCm2 < 300f)
                continue;
            var relief = Relief(pd);
            var cells = new Dictionary<(int, int), (float Sum, int Count, int Vertex)>();
            int creased = 0;
            for (int i = 0; i < relief.Length; i++)
            {
                var p = pd.Mesh.Points[i];
                var key = ((int)MathF.Floor(p.X / cellCm), (int)MathF.Floor(p.Y / cellCm));
                cells.TryGetValue(key, out var c);
                cells[key] = (c.Sum + relief[i], c.Count + 1, c.Count == 0 ? i : c.Vertex);
                if (relief[i] > creaseDegrees)
                    creased++;
            }
            var full = cells.Where(c => c.Value.Count >= 6).ToList();
            if (full.Count < 4)
                continue;
            var flatCells = full.Where(c => c.Value.Sum / c.Value.Count < flatDegrees).Select(c => c.Key).ToHashSet();
            float flatShare = flatCells.Count / (float)full.Count;
            float creaseShare = creased / (float)relief.Length;
            if (flatShare < 0.45f || creaseShare > 0.12f)
                continue;
            // The largest connected flat area is where to add wear.
            var seen = new HashSet<(int, int)>();
            List<(int, int)> best = new();
            foreach (var start in flatCells)
            {
                if (!seen.Add(start))
                    continue;
                var group = new List<(int, int)> { start };
                for (int k = 0; k < group.Count; k++)
                {
                    var (cx, cy) = group[k];
                    foreach (var nb in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                        if (flatCells.Contains(nb) && seen.Add(nb))
                            group.Add(nb);
                }
                if (group.Count > best.Count)
                    best = group;
            }
            var mid = new Vector2((float)best.Average(c => c.Item1) + 0.5f, (float)best.Average(c => c.Item2) + 0.5f) * cellCm;
            int vertex = Enumerable.Range(0, pd.X.Length).MinBy(i => Vector2.DistanceSquared(pd.Mesh.Points[i], mid));
            bool textured = pd.Piece.MaterialId != null && scene.Doc.FindMaterial(pd.Piece.MaterialId)?.Texture is { Length: > 0 };
            float score = Math.Clamp(4f + (flatShare - 0.45f) * 20f + (textured ? 0f : 4f), 3f, 14f);
            yield return Make(TooClean, score, pd, vertex,
                $"{pd.Piece.Name} looks too clean for a worn garment: {flatShare * 100:0}% of it is smooth, with creases on only "
                + $"{creaseShare * 100:0.#}% (the largest smooth area is about {best.Count * cellCm * cellCm:0} cm²)"
                + (textured ? "." : ", and it has a plain colour with no texture."),
                new CritiqueFix("pull_cloth", new()
                {
                    ["pieceId"] = pd.Piece.Id,
                    ["x"] = Round(pd.Mesh.Points[vertex].X),
                    ["y"] = Round(pd.Mesh.Points[vertex].Y),
                    ["radiusCm"] = 6,
                    ["offsetCm"] = new[] { 0f, -2.5f, 0f },
                    ["creasePercent"] = 60,
                }, "A soft hint. Drag a few folds in (bunching at elbows, knees, the waist) and set creases so they stay"
                   + (textured ? "." : "; a worn texture (tileCm) helps as much.")),
                new() { ["smoothPercent"] = Round(flatShare * 100), ["creasedPercent"] = Round(creaseShare * 100) });
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private static CritiqueProblem Make(string type, float score, PieceData pd, int vertex, string description, CritiqueFix fix,
        Dictionary<string, float> measures, string? otherGarment = null, string? otherPiece = null, int count = 1) => new()
    {
        Type = type,
        Severity = score >= 70 ? "critical" : score >= 40 ? "major" : score >= 15 ? "minor" : "hint",
        Score = Round(score),
        Garment = pd.Garment.Name,
        GarmentId = pd.Garment.Id,
        Piece = pd.Piece.Name,
        PieceId = pd.Piece.Id,
        At = pd.X.Length > 0 ? pd.X[Math.Clamp(vertex, 0, pd.X.Length - 1)] : pd.Piece.Placement.Position,
        PatternCm = pd.Mesh.Points.Length > 0 ? pd.Mesh.Points[Math.Clamp(vertex, 0, pd.Mesh.Points.Length - 1)] : Vector2.Zero,
        Vertices = count,
        Description = description,
        Fix = fix,
        OtherGarment = otherGarment,
        OtherPiece = otherPiece,
        Measures = measures,
    };

    private static float Round(float v) => MathF.Round(v, 1);

    private static int Centre(PieceData pd)
    {
        if (pd.X.Length == 0)
            return 0;
        var c = pd.X.Aggregate(Vector3.Zero, (a, b) => a + b) / pd.X.Length;
        return Nearest(pd, c);
    }

    private static int Nearest(PieceData pd, Vector3 p)
        => pd.X.Length == 0 ? 0 : Enumerable.Range(0, pd.X.Length).MinBy(i => Vector3.DistanceSquared(pd.X[i], p));

    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<PieceData> pieces)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var pd in pieces)
            foreach (var p in pd.X)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        return (min, max);
    }

    /// <summary>
    /// The flagged vertices of a piece in groups that lie together on the pattern (within three particle distances),
    /// largest first.
    /// </summary>
    internal static List<List<int>> Clusters(PieceData pd, List<int> flagged)
    {
        if (flagged.Count == 0)
            return new();
        float r = pd.ParticleCm * 3f;
        var parent = new int[flagged.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;
        int Find(int a)
        {
            while (parent[a] != a)
                a = parent[a] = parent[parent[a]];
            return a;
        }
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < flagged.Count; i++)
        {
            var p = pd.Mesh.Points[flagged[i]];
            var key = ((int)MathF.Floor(p.X / r), (int)MathF.Floor(p.Y / r));
            if (!grid.TryGetValue(key, out var list))
                grid[key] = list = new List<int>();
            list.Add(i);
        }
        foreach (var ((gx, gy), list) in grid)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!grid.TryGetValue((gx + dx, gy + dy), out var other))
                        continue;
                    foreach (int a in list)
                    {
                        foreach (int b in other)
                        {
                            if (a < b && Vector2.DistanceSquared(pd.Mesh.Points[flagged[a]], pd.Mesh.Points[flagged[b]]) <= r * r)
                            {
                                int ra = Find(a), rb = Find(b);
                                if (ra != rb)
                                    parent[rb] = ra;
                            }
                        }
                    }
                }
            }
        }
        return Enumerable.Range(0, flagged.Count).GroupBy(Find)
            .Select(g => g.Select(i => flagged[i]).ToList())
            .OrderByDescending(g => g.Count)
            .ToList();
    }

    /// <summary>Nearest-surface queries against a set of pieces: closest point, its triangle normal and nearest vertex.</summary>
    internal sealed class SpatialHash
    {
        private readonly Dictionary<(int, int, int), List<(PieceData Piece, int Vertex)>> _cells = new();
        private readonly float _cell;

        private SpatialHash(float cell) => _cell = cell;

        public static SpatialHash Build(IReadOnlyList<PieceData> pieces, float cell)
        {
            var hash = new SpatialHash(cell);
            foreach (var pd in pieces)
            {
                for (int i = 0; i < pd.X.Length; i++)
                {
                    var key = hash.Key(pd.X[i]);
                    if (!hash._cells.TryGetValue(key, out var list))
                        hash._cells[key] = list = new();
                    list.Add((pd, i));
                }
            }
            return hash;
        }

        private (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell), (int)MathF.Floor(p.Z / _cell));

        public readonly record struct Hit(PieceData Piece, int Vertex, Vector3 Point, Vector3 Normal, float Distance);

        /// <summary>The closest point of the surface within <paramref name="reach"/> metres, looked for round the nearest vertex.</summary>
        public Hit? Closest(Vector3 p, float reach)
        {
            int span = Math.Max(1, (int)MathF.Ceiling(reach / _cell));
            var (kx, ky, kz) = Key(p);
            PieceData? bestPiece = null;
            int bestVertex = -1;
            float best = reach * reach;
            for (int dx = -span; dx <= span; dx++)
                for (int dy = -span; dy <= span; dy++)
                    for (int dz = -span; dz <= span; dz++)
                    {
                        if (!_cells.TryGetValue((kx + dx, ky + dy, kz + dz), out var list))
                            continue;
                        foreach (var (pd, v) in list)
                        {
                            float d = Vector3.DistanceSquared(pd.X[v], p);
                            if (d < best)
                            {
                                best = d;
                                bestPiece = pd;
                                bestVertex = v;
                            }
                        }
                    }
            if (bestPiece == null)
                return null;
            var x = bestPiece.X;
            var t = bestPiece.Mesh.Triangles;
            Vector3 point = x[bestVertex], normal = bestPiece.Normals[bestVertex];
            float bestD = float.MaxValue;
            foreach (int k in bestPiece.VertexTriangles[bestVertex])
            {
                var a = x[t[k]];
                var b = x[t[k + 1]];
                var c = x[t[k + 2]];
                var q = SdfGrid.ClosestPointOnTriangle(p, a, b, c);
                float d = Vector3.DistanceSquared(q, p);
                if (d < bestD)
                {
                    var n = Vector3.Cross(b - a, c - a);
                    if (n.LengthSquared() < 1e-18f)
                        continue;
                    bestD = d;
                    point = q;
                    normal = Vector3.Normalize(n);
                }
            }
            return new Hit(bestPiece, bestVertex, point, normal, MathF.Sqrt(MathF.Min(bestD, best)));
        }
    }
}
