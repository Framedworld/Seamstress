using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.Editing;

/// <summary>Where new pieces go in the 2D pattern window, so they do not pile up on the origin.</summary>
public static class PatternLayout
{
    private const float Gap = 6f, RowWidth = 300f, GarmentGap = 20f;

    private static (Vector2 Min, Vector2 Max)? Extent(IEnumerable<PatternPiece> pieces)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        bool any = false;
        foreach (var p in pieces)
        {
            if (p.Points.Count < 3)
                continue;
            var (lo, hi) = Outline.Bounds(p);
            min = Vector2.Min(min, lo + p.Origin);
            max = Vector2.Max(max, hi + p.Origin);
            any = true;
        }
        return any ? (min, max) : null;
    }

    /// <summary>
    /// Gives <paramref name="piece"/> (not yet in <paramref name="garment"/>) a free place: to the right of the
    /// garment's last piece, on a new row when the row is full, and below every other garment when it is the first.
    /// </summary>
    public static void Place(Document document, Garment garment, PatternPiece piece)
    {
        var (lo, hi) = Outline.Bounds(piece);
        Vector2 topLeft;
        if (garment.Pieces.Count > 0 && Extent(garment.Pieces) is { } own)
        {
            var last = garment.Pieces[^1];
            var (llo, lhi) = Outline.Bounds(last);
            float x = lhi.X + last.Origin.X + Gap;
            float rowTop = lhi.Y + last.Origin.Y;
            topLeft = x + (hi.X - lo.X) - own.Min.X > RowWidth
                ? new Vector2(own.Min.X, own.Min.Y - Gap - 2)
                : new Vector2(x, rowTop);
        }
        else
        {
            topLeft = Extent(document.Garments.Where(g => g != garment).SelectMany(g => g.Pieces)) is { } others
                ? new Vector2(others.Min.X, others.Min.Y - GarmentGap)
                : Vector2.Zero;
        }
        piece.Origin = new Vector2(topLeft.X - lo.X, topLeft.Y - hi.Y);
    }

    /// <summary>Lays every garment out again in rows, one garment under the other. Returns the pieces moved.</summary>
    public static int ArrangeAll(Document document)
    {
        int moved = 0;
        float top = 0;
        foreach (var garment in document.Garments)
        {
            float x = 0, rowHeight = 0, rowTop = top, bottom = top;
            foreach (var piece in garment.Pieces)
            {
                if (piece.Points.Count < 3)
                    continue;
                var (lo, hi) = Outline.Bounds(piece);
                float w = hi.X - lo.X, h = hi.Y - lo.Y;
                if (x > 0 && x + w > RowWidth)
                {
                    x = 0;
                    rowTop -= rowHeight + Gap + 2;
                    rowHeight = 0;
                }
                piece.Origin = new Vector2(x - lo.X, rowTop - hi.Y);
                x += w + Gap;
                rowHeight = MathF.Max(rowHeight, h);
                bottom = MathF.Min(bottom, rowTop - h);
                moved++;
            }
            if (garment.Pieces.Count > 0)
                top = bottom - GarmentGap;
        }
        return moved;
    }
}

/// <summary>What a block made, so the caller can report it and simulate the right garment.</summary>
public sealed record BlockResult(Garment Garment, List<PatternPiece> Pieces, string Note);

/// <summary>
/// Ready-made parts added in one step: a patch, pocket or flap sewn onto a draped piece (also across a seam), a
/// sleeve or cuff tube round a limb, and hard parts such as snaps. Each lands in a garment with the right layer and
/// body offset for sitting on top of its host, so it does not have to be worked out by hand.
/// </summary>
public static class GarmentBlocks
{
    /// <summary>
    /// Pattern coordinates made by blocks and templates sit on a 0.01 cm grid: a project file stores them rounded, and
    /// a piece that meshes even slightly differently after loading would lose its drape.
    /// </summary>
    public static float Snap(float cm) => MathF.Round(cm, 2);

    /// <summary>Seamstress Euler angles for a rotation taking the piece's x, y and z axes to these world directions.</summary>
    public static Vector3 EulerFromAxes(Vector3 x, Vector3 y, Vector3 z)
        => Workspace.ToEuler(new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1));

    private static float ThicknessOf(Garment g)
        => g.Thickness ?? (g.Pieces.Count > 0 ? g.Pieces.Max(p => p.Physics.Thickness) : 2.5f);

    /// <summary>
    /// The garment that holds trim sewn onto <paramref name="host"/>: one layer further out per
    /// <paramref name="level"/> (1 = lying on the host, 2 = over that trim, such as a flap over a pocket).
    /// </summary>
    public static Garment TrimGarment(Document document, Garment host, int level, string? name = null)
    {
        level = Math.Clamp(level, 1, 4);
        name ??= level == 1 ? $"{host.Name} trim" : $"{host.Name} trim {level}";
        var found = document.Garments.FirstOrDefault(g => g.Name == name);
        if (found != null)
            return found;
        float thickness = MathF.Min(ThicknessOf(host), 2.4f);
        var garment = new Garment
        {
            Name = name,
            Layer = host.Layer + level,
            BodyOffset = host.BodyOffset + ThicknessOf(host) + 0.4f + (level - 1) * (thickness + 0.4f),
            ParticleDistance = MathF.Min(host.ParticleDistance, 0.9f),
            Thickness = host.Thickness is null ? null : thickness,
        };
        document.Garments.Add(garment);
        return garment;
    }

    private static PatternPiece NewPiece(Document document, Garment garment, string name, List<PatternPoint> points,
        PatternPiece? like)
    {
        var piece = new PatternPiece { Name = document.UniqueName(name), Points = points };
        if (like != null)
        {
            piece.Physics = like.Physics.Clone();
            piece.Physics.ShrinkWeft = piece.Physics.ShrinkWarp = 1f;
            piece.Physics.Thickness = MathF.Min(piece.Physics.Thickness, 2.4f);
            piece.MaterialId = like.MaterialId;
        }
        PatternLayout.Place(document, garment, piece);
        garment.Pieces.Add(piece);
        return piece;
    }

    private static (PatternPiece Piece, PieceMesh Mesh, Vector3[] Positions, Garment Garment) Host(Document document,
        Func<Garment, GarmentMesh> meshFor, string pieceId)
    {
        var garment = document.GarmentOfPiece(pieceId) ?? throw new ArgumentException($"Piece '{pieceId}' was not found.");
        var piece = garment.FindPiece(pieceId)!;
        var mesh = meshFor(garment).Find(pieceId) ?? throw new ArgumentException($"Piece '{piece.Name}' has no mesh.");
        return (piece, mesh, Mesher.CurrentPositions(piece, mesh), garment);
    }

    private static int[] EdgesFor(string kind, IReadOnlyList<PatternPoint> points)
    {
        int n = points.Count;
        // The top edge is the one whose middle is highest.
        int top = Enumerable.Range(0, n).MaxBy(i => points[i].Position.Y + points[(i + 1) % n].Position.Y);
        return kind.ToLowerInvariant() switch
        {
            "flap" => new[] { top },
            "pocket" => Enumerable.Range(0, n).Where(i => i != top).ToArray(),
            "free" => Array.Empty<int>(),
            _ => Enumerable.Range(0, n).ToArray(),
        };
    }

    /// <summary>
    /// Sews a patch onto the draped <paramref name="hostPieceId"/>. <paramref name="footprint"/> is where it sits, in
    /// the host's own pattern centimetres, counter-clockwise. <paramref name="kind"/>: "patch" sews every edge,
    /// "pocket" leaves the top open, "flap" sews only the top, "free" sews <paramref name="stitchEdges"/>.
    /// The patch is cut <paramref name="ease"/> times larger than its footprint so it can puff or hang.
    /// </summary>
    public static BlockResult AddPatch(Document document, Func<Garment, GarmentMesh> meshFor, string hostPieceId,
        IReadOnlyList<Vector2> footprint, string kind, string name, Vector2 ease, int level = 1,
        IReadOnlyList<int>? stitchEdges = null, IReadOnlyList<string?>? edgeNames = null, float liftCm = 2f,
        string? garmentName = null)
    {
        if (footprint.Count < 3)
            throw new ArgumentException("A patch needs at least 3 footprint points.");
        var host = Host(document, meshFor, hostPieceId);
        var fp = footprint.ToList();
        float area = 0;
        for (int i = 0; i < fp.Count; i++)
            area += fp[i].X * fp[(i + 1) % fp.Count].Y - fp[(i + 1) % fp.Count].X * fp[i].Y;
        if (area < 0)
            fp.Reverse();
        var centre = fp.Aggregate(Vector2.Zero, (a, b) => a + b) / fp.Count;
        string[] rectNames = { "bottom", "right", "top", "left" };
        var points = new List<PatternPoint>();
        for (int i = 0; i < fp.Count; i++)
        {
            var cut = centre + (fp[i] - centre) * ease;
            points.Add(new PatternPoint(Snap(cut.X), Snap(cut.Y))
            {
                EdgeName = edgeNames != null && i < edgeNames.Count ? edgeNames[i] : fp.Count == 4 && area >= 0 ? rectNames[i] : null,
            });
        }
        if (fp.Count == 4 && edgeNames == null)
        {
            // Name the edges of any four-sided patch by where they are, whatever point it was started from.
            int top = Enumerable.Range(0, 4).MaxBy(i => fp[i].Y + fp[(i + 1) % 4].Y);
            string[] order = { "top", "left", "bottom", "right" };
            for (int k = 0; k < 4; k++)
                points[(top + k) % 4].EdgeName = order[k];
        }

        var garment = TrimGarment(document, host.Garment, level, garmentName);
        var piece = NewPiece(document, garment, name, points, host.Piece);
        if (kind.Equals("flap", StringComparison.OrdinalIgnoreCase))
            piece.Physics.Bend = MathF.Max(piece.Physics.Bend, 85f);

        if (!SurfaceLocator.TryLocate(host.Mesh, host.Positions, centre, host.Piece.FlipNormals, out var at, out var normal, out _, out var snapped))
            throw new ArgumentException($"'{host.Piece.Name}' has no surface to sew onto.");
        float yaw = MathF.Atan2(normal.X, normal.Z);
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        piece.Placement.Position = at + normal * (liftCm * 0.01f) + right * ((centre.X - snapped.X) * 0.01f)
            + Vector3.UnitY * ((centre.Y - snapped.Y) * 0.01f);
        piece.Placement.Rotation = new Vector3(0, yaw * 180f / MathF.PI, 0);

        foreach (int e in stitchEdges ?? EdgesFor(kind, points))
        {
            if (e < 0 || e >= fp.Count)
                continue;
            garment.SurfaceStitches.Add(new SurfaceStitch
            {
                SourcePieceId = piece.Id, SourceEdge = e, TargetPieceId = host.Piece.Id,
                TargetStart = fp[e], TargetEnd = fp[(e + 1) % fp.Count],
            });
        }
        return new BlockResult(garment, new List<PatternPiece> { piece },
            host.Piece.Drape == null ? $"'{host.Piece.Name}' is not draped yet: simulate its garment first, then '{garment.Name}'." : $"Simulate '{garment.Name}' to settle it.");
    }

    private static (Vector2 Point, Vector2 Tangent) AlongEdge(PatternPiece piece, int edge, float distanceCm)
    {
        var line = Outline.FlattenEdge(piece, edge);
        float left = MathF.Max(distanceCm, 0);
        for (int i = 0; i + 1 < line.Count; i++)
        {
            float step = Vector2.Distance(line[i], line[i + 1]);
            if (left <= step || i + 2 == line.Count)
            {
                var t = step > 1e-6f ? (line[i + 1] - line[i]) / step : Vector2.UnitY;
                return (line[i] + t * MathF.Min(left, step), t);
            }
            left -= step;
        }
        return (line[0], Vector2.UnitY);
    }

    /// <summary>
    /// A patch, pocket or flap centred on a seam: half of it on each of the two pieces the seam joins (a cargo pocket
    /// on a trouser outseam). <paramref name="fromCm"/> and <paramref name="toCm"/> are distances along
    /// <paramref name="edge"/> of <paramref name="hostPieceId"/> from the start of that edge.
    /// </summary>
    public static BlockResult AddSeamPatch(Document document, Func<Garment, GarmentMesh> meshFor, string hostPieceId,
        int edge, float fromCm, float toCm, float widthCm, string kind, string name, float ease = 1.12f, int level = 1,
        float liftCm = 3f, string? garmentName = null)
    {
        var a = Host(document, meshFor, hostPieceId);
        var seam = a.Garment.Seams.FirstOrDefault(s => s.A.Matches(hostPieceId, edge) || s.B.Matches(hostPieceId, edge))
            ?? throw new ArgumentException($"Edge {edge} of '{a.Piece.Name}' is not sewn to another piece, so there is no seam to centre on.");
        var other = seam.A.Matches(hostPieceId, edge) ? seam.B : seam.A;
        var b = Host(document, meshFor, other.PieceId);
        float lengthA = Outline.EdgeLength(a.Piece, edge), lengthB = Outline.EdgeLength(b.Piece, other.Edge);
        if (toCm < fromCm)
            (fromCm, toCm) = (toCm, fromCm);
        fromCm = Math.Clamp(fromCm, 0, lengthA);
        toCm = Math.Clamp(toCm, 0, lengthA);
        if (toCm - fromCm < 1f)
            throw new ArgumentException("The patch must be at least 1 cm tall along the seam.");
        float half = widthCm / 2;
        float PartnerDistance(float d) => seam.Reversed ? d * lengthB / lengthA : lengthB - d * lengthB / lengthA;

        // Corner points on each host: on the seam and half a width in from it, at both ends of the stretch.
        (Vector2 Edge, Vector2 Inner) Corner(PatternPiece piece, int e, float d)
        {
            var (p, t) = AlongEdge(piece, e, d);
            return (p + new Vector2(-t.Y, t.X) * 0.4f, p + new Vector2(-t.Y, t.X) * half);   // cloth is on the left of a CCW edge
        }
        var a0 = Corner(a.Piece, edge, fromCm);
        var a1 = Corner(a.Piece, edge, toCm);
        var b0 = Corner(b.Piece, other.Edge, PartnerDistance(fromCm));
        var b1 = Corner(b.Piece, other.Edge, PartnerDistance(toCm));

        Vector3 At(in (PatternPiece Piece, PieceMesh Mesh, Vector3[] Positions, Garment Garment) h, Vector2 p, out Vector3 n)
        {
            SurfaceLocator.TryLocate(h.Mesh, h.Positions, p, h.Piece.FlipNormals, out var pos, out n, out _);
            return pos;
        }
        var low = At(a, a0.Edge, out var n0);
        var high = At(a, a1.Edge, out var n1);
        At(b, b0.Edge, out var n2);
        if (high.Y < low.Y)
        {
            // The edge runs downwards on the body: swap ends so the patch's bottom is the lower one.
            (a0, a1, b0, b1) = (a1, a0, b1, b0);
            (low, high) = (high, low);
        }
        var up = Vector3.Normalize(high - low);
        var normal = n0 + n1 + n2;
        normal -= up * Vector3.Dot(normal, up);
        normal = normal.LengthSquared() > 1e-10f ? Vector3.Normalize(normal) : Vector3.UnitZ;
        var across = Vector3.Cross(up, normal);
        var aInner = At(a, (a0.Inner + a1.Inner) / 2, out _);
        bool aIsNegative = Vector3.Dot(aInner - (low + high) / 2, across) < 0;
        var neg = aIsNegative ? (Host: a, C0: a0, C1: a1) : (Host: b, C0: b0, C1: b1);
        var pos = aIsNegative ? (Host: b, C0: b0, C1: b1) : (Host: a, C0: a0, C1: a1);

        float h = Snap(Vector3.Distance(low, high) * 100f);
        float w = Snap(widthCm * ease / 2);
        var points = new List<PatternPoint>
        {
            new(-w, 0) { EdgeName = "bottom_a" }, new(0, 0) { EdgeName = "bottom_b" }, new(w, 0) { EdgeName = "side_b" },
            new(w, h) { EdgeName = "top_b" }, new(0, h) { EdgeName = "top_a" }, new(-w, h) { EdgeName = "side_a" },
        };
        var garment = TrimGarment(document, a.Garment, level, garmentName);
        var piece = NewPiece(document, garment, name, points, a.Piece);
        bool flap = kind.Equals("flap", StringComparison.OrdinalIgnoreCase);
        if (flap)
            piece.Physics.Bend = MathF.Max(piece.Physics.Bend, 85f);
        piece.Placement.Position = (low + high) / 2 + normal * (liftCm * 0.01f);
        piece.Placement.Rotation = EulerFromAxes(across, up, normal);
        piece.Placement.Curvature = 50;

        void Stitch(int e, string hostId, Vector2 from, Vector2 to) => garment.SurfaceStitches.Add(new SurfaceStitch
        {
            SourcePieceId = piece.Id, SourceEdge = e, TargetPieceId = hostId, TargetStart = from, TargetEnd = to,
        });
        bool all = kind.Equals("patch", StringComparison.OrdinalIgnoreCase);
        if (!flap)
        {
            Stitch(0, neg.Host.Piece.Id, neg.C0.Inner, neg.C0.Edge);
            Stitch(1, pos.Host.Piece.Id, pos.C0.Edge, pos.C0.Inner);
            Stitch(2, pos.Host.Piece.Id, pos.C0.Inner, pos.C1.Inner);
            Stitch(5, neg.Host.Piece.Id, neg.C1.Inner, neg.C0.Inner);
        }
        if (flap || all)
        {
            Stitch(3, pos.Host.Piece.Id, pos.C1.Inner, pos.C1.Edge);
            Stitch(4, neg.Host.Piece.Id, neg.C1.Edge, neg.C1.Inner);
        }
        return new BlockResult(garment, new List<PatternPiece> { piece }, $"Simulate '{garment.Name}' to settle it.");
    }

    /// <summary>
    /// A tube round a limb: a sleeve, a trouser leg, a cuff. The pattern is a trapezoid <paramref name="startCircCm"/>
    /// wide at <paramref name="start"/> and <paramref name="endCircCm"/> wide at the far end; its own seam runs along
    /// the side away from <paramref name="up"/> and is sewn. With <paramref name="splitStart"/> the start edge is two
    /// edges that meet on top (to sew to a front and a back armhole); <paramref name="capCm"/> raises that meeting
    /// point into a shallow sleeve cap.
    /// </summary>
    public static PatternPiece AddTube(Document document, Garment garment, string name, Vector3 start, Vector3 axis,
        Vector3 up, float startCircCm, float endCircCm, float lengthCm, bool splitStart, float capCm = 0,
        float arcDegrees = 325f, PatternPiece? like = null)
    {
        var a = Vector3.Normalize(axis);
        var u = up - a * Vector3.Dot(up, a);
        u = u.LengthSquared() > 1e-8f ? Vector3.Normalize(u) : Vector3.Normalize(Vector3.Cross(a, Vector3.UnitZ));
        var x = Vector3.Cross(a, u);
        float ha = Snap(startCircCm / 2), hb = Snap(endCircCm / 2);
        lengthCm = Snap(lengthCm);
        capCm = Snap(capCm);
        // The two halves of the start edge are named after the way they face on the body.
        bool frontBack = MathF.Abs(x.Z) >= MathF.Abs(x.X);
        string positive = frontBack ? (x.Z > 0 ? "start_front" : "start_back") : (x.X > 0 ? "start_left" : "start_right");
        string negative = frontBack ? (x.Z > 0 ? "start_back" : "start_front") : (x.X > 0 ? "start_right" : "start_left");
        var points = new List<PatternPoint>();
        if (splitStart)
        {
            points.Add(capCm > 0 ? new PatternPoint(-ha, 0, -ha / 2, -capCm) : new PatternPoint(-ha, 0));
            points[^1].EdgeName = negative;
            points.Add(capCm > 0 ? new PatternPoint(0, -capCm, ha / 2, -capCm) : new PatternPoint(0, 0));
            points[^1].EdgeName = positive;
        }
        else
        {
            points.Add(new PatternPoint(-ha, 0) { EdgeName = "start" });
        }
        points.Add(new PatternPoint(ha, 0) { EdgeName = "seam_b" });
        points.Add(new PatternPoint(hb, lengthCm) { EdgeName = "end" });
        points.Add(new PatternPoint(-hb, lengthCm) { EdgeName = "seam_a" });

        var piece = NewPiece(document, garment, name, points, like);
        var (lo, hi) = Outline.Bounds(piece);
        float radius = (hi.X - lo.X) * 0.01f / (arcDegrees * MathF.PI / 180f);
        piece.Placement.Position = start + a * ((lo.Y + hi.Y) / 2 * 0.01f) + u * radius;
        piece.Placement.Rotation = EulerFromAxes(x, a, u);
        piece.Placement.Curvature = arcDegrees;
        garment.Seams.Add(new Seam { A = new EdgeRef(piece.Id, piece.EdgeNamed("seam_b")), B = new EdgeRef(piece.Id, piece.EdgeNamed("seam_a")) });
        return piece;
    }

    /// <summary>
    /// A hard part set on the draped surface of <paramref name="hostPieceId"/> at pattern point <paramref name="at"/>:
    /// every vertex is pinned, so it keeps its shape and place even when its garment is simulated. With
    /// <paramref name="rollDegrees"/> the outline is rolled into a cylinder (a pen, a tool handle).
    /// </summary>
    public static PatternPiece AddHardware(Document document, Func<Garment, GarmentMesh> meshFor, string hostPieceId,
        Vector2 at, IReadOnlyList<Vector2> outline, string name, string garmentName, float thicknessMm, float liftMm,
        float spinDegrees = 0, float rollDegrees = 0, bool followTilt = true)
    {
        var host = Host(document, meshFor, hostPieceId);
        if (!SurfaceLocator.TryLocate(host.Mesh, host.Positions, at, host.Piece.FlipNormals, out var position, out var normal, out _))
            throw new ArgumentException($"'{host.Piece.Name}' has no surface to set hardware on.");
        var garment = document.Garments.FirstOrDefault(g => g.Name == garmentName);
        if (garment == null)
        {
            garment = new Garment { Name = garmentName, Layer = 90, ParticleDistance = 0.6f, Thickness = thicknessMm };
            document.Garments.Add(garment);
        }
        var points = outline.Select(p => new PatternPoint(MathF.Round(p.X, 3), MathF.Round(p.Y, 3))).ToList();
        float area = 0;
        for (int i = 0; i < points.Count; i++)
            area += points[i].Position.X * points[(i + 1) % points.Count].Position.Y - points[(i + 1) % points.Count].Position.X * points[i].Position.Y;
        if (area < 0)
            points.Reverse();
        var piece = NewPiece(document, garment, name, points, null);
        piece.Physics = FabricPhysics.FromPreset("Leather");
        var (lo, hi) = Outline.Bounds(piece);
        float radius = rollDegrees != 0 ? (hi.X - lo.X) * 0.01f / (MathF.Abs(rollDegrees) * MathF.PI / 180f) : 0;
        float yaw = MathF.Atan2(normal.X, normal.Z) * 180f / MathF.PI;
        float pitch = followTilt ? -MathF.Asin(Math.Clamp(normal.Y, -1, 1)) * 180f / MathF.PI : 0;
        piece.Placement.Rotation = new Vector3(pitch, yaw, spinDegrees);
        piece.Placement.Curvature = rollDegrees;
        // The outline's own origin goes on the chosen point: the middle of a snap, the bottom of a pen or a tool.
        var middle = (lo + hi) / 2;
        var toMiddle = Vector3.TransformNormal(new Vector3(middle.X, middle.Y, 0) * 0.01f, Workspace.EulerMatrix(piece.Placement.Rotation));
        piece.Placement.Position = position + normal * (liftMm * 0.001f + radius) + toMiddle;
        // Pin every vertex: a simulation leaves it exactly where it was put.
        if (meshFor(garment).Find(piece.Id) is { } mesh)
        {
            piece.Pins = Enumerable.Range(0, mesh.VertexCount).ToHashSet();
            piece.PinKey = mesh.Key;
        }
        return piece;
    }

    /// <summary>An untextured material for hard parts with this colour and finish: reused when one already exists.</summary>
    public static FabricMaterial HardMaterial(Document document, string color, float metallic, float roughness)
    {
        var mat = document.Materials.FirstOrDefault(m => m.Texture == null && string.Equals(m.Color, color, StringComparison.OrdinalIgnoreCase)
            && MathF.Abs(m.Metallic - metallic) < 0.01f && MathF.Abs(m.Roughness - roughness) < 0.01f);
        if (mat == null)
        {
            mat = new FabricMaterial { Name = document.UniqueName(metallic > 0.5f ? "Metal" : "Hard part"), Color = color, Metallic = metallic, Roughness = roughness };
            document.Materials.Add(mat);
        }
        return mat;
    }

    /// <summary>Outline of a ready-made hard part, centred on the origin, in centimetres.</summary>
    public static List<Vector2> HardwareShape(string shape, float widthCm, float heightCm)
    {
        switch (shape.ToLowerInvariant())
        {
            case "snap":
            case "button":
            case "disc":
            case "rivet":
            {
                var pts = new List<Vector2>();
                for (int i = 0; i < 12; i++)
                    pts.Add(new Vector2(MathF.Cos(i * MathF.PI / 6), MathF.Sin(i * MathF.PI / 6)) * (widthCm / 2));
                return pts;
            }
            case "buckle":
            case "bar":
            case "plate":
            case "cylinder":
            case "pen":
            case "handle":
                return new List<Vector2> { new(-widthCm / 2, 0), new(widthCm / 2, 0), new(widthCm / 2, heightCm), new(-widthCm / 2, heightCm) };
            case "wrench":
            {
                float s = heightCm / 20.8f, k = widthCm / 4.8f;
                return new (float X, float Y)[]
                {
                    (-0.9f, 0), (0.9f, 0), (1.0f, 13.5f), (2.4f, 15.5f), (2.4f, 19.5f), (1.3f, 20.8f), (1.1f, 17.6f),
                    (-1.1f, 17.6f), (-1.3f, 20.8f), (-2.4f, 19.5f), (-2.4f, 15.5f), (-1.0f, 13.5f),
                }.Select(p => new Vector2(p.X * k, p.Y * s)).ToList();
            }
            default:
                throw new ArgumentException($"Unknown hardware shape '{shape}'. Use snap, button, rivet, bar, buckle, plate, pen, handle, cylinder or wrench, or give 'points'.");
        }
    }
}
