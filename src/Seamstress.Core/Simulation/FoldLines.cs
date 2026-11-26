using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Simulation;

/// <summary>
/// Fold lines in the simulation. The cloth's bending springs reach across each mesh edge, between the two vertices
/// facing each other over it; along a fold line those springs rest at the distance the vertices have when the cloth
/// is folded by the line's angle, and they are stiffened by its strength, so the cloth folds there and stays folded.
/// A distance alone cannot tell a mountain fold from a valley fold, so a piece that starts flat gets its fold lines
/// nudged a little to the side they fold towards before the first step.
/// </summary>
public static class FoldLines
{
    /// <summary>Folds are kept a little short of fully flat on themselves, where the bending spring would vanish.</summary>
    public const float MaxSimulatedAngle = 175f;

    public readonly record struct Fold(float Angle, float Strength, bool Valley);

    private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>The mesh edges along the piece's fold lines, with the fold each one carries (angle in radians).</summary>
    public static Dictionary<long, Fold> Edges(PatternPiece piece, PieceMesh mesh)
    {
        var result = new Dictionary<long, Fold>(EdgeKey.Comparer);
        int count = Math.Min(piece.InternalLines.Count, mesh.LineChains.Length);
        for (int l = 0; l < count; l++)
        {
            var line = piece.InternalLines[l];
            if (!line.IsFold)
                continue;
            var fold = new Fold(Math.Clamp(line.FoldAngle, 0, MaxSimulatedAngle) * MathF.PI / 180f, Math.Clamp(line.FoldStrength, 0, 1), line.Valley);
            var chain = mesh.LineChains[l];
            for (int k = 0; k + 1 < chain.Length; k++)
                result[Key(chain[k], chain[k + 1])] = fold;
        }
        return result;
    }

    /// <summary>
    /// How far apart <paramref name="c"/> and <paramref name="d"/>, on either side of the edge from <paramref name="a"/>
    /// to <paramref name="b"/>, are once the cloth is folded along that edge by <paramref name="angle"/> radians (0 is
    /// flat). Same units as the points.
    /// </summary>
    public static float FoldedDistance(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float angle)
    {
        var e = b - a;
        float length = e.Length();
        if (length < 1e-9f)
            return Vector2.Distance(c, d);
        var dir = e / length;
        var rc = c - a;
        var rd = d - a;
        float uc = Vector2.Dot(rc, dir), ud = Vector2.Dot(rd, dir);
        float vc = MathF.Abs(dir.X * rc.Y - dir.Y * rc.X), vd = MathF.Abs(dir.X * rd.Y - dir.Y * rd.X);
        float along = uc - ud;
        return MathF.Sqrt(MathF.Max(along * along + vc * vc + vd * vd + 2 * vc * vd * MathF.Cos(angle), 0));
    }

    /// <summary>Bending stiffness of a fold: at least the fabric's own, and much firmer the stronger the fold.</summary>
    public static float Stiffness(float fabricBendStiffness, float strength)
        => MathF.Max(fabricBendStiffness, MathF.Pow(10f, -1f + 3.5f * Math.Clamp(strength, 0, 1)));

    /// <summary>
    /// Gives a flat piece a first hint of the shape its folds and darts make, since from perfectly flat nothing tells
    /// the cloth which way to go. A mountain fold's ridge is nudged out of the side the piece's faces point to and a
    /// valley fold's into it; the cloth around a dart's point is domed a little outwards, the way a dart shapes cloth
    /// over the body. Pieces already draped are left as they are: their folds have chosen their side.
    /// </summary>
    public static void PreShape(Garment garment, PatternPiece piece, PieceMesh mesh, Vector3[] positions)
    {
        if (piece.Drape != null && piece.DrapeKey == mesh.Key && piece.Drape.Length == mesh.VertexCount * 3)
            return;
        // A pleat folding from flat snaps through itself hard enough to throw the garment off the body: lay it
        // already folded instead. (Its layers do not collide with each other; see PleatIds.)
        PreFoldPleats(garment, piece, mesh, positions);
        var darts = Darts(garment, piece, mesh);
        if (darts.Count == 0 && !piece.InternalLines.Any(l => l.IsFold))
            return;
        var normals = Mesher.ComputeNormals(positions, mesh.Triangles);
        float outward = piece.FlipNormals ? -1f : 1f;
        var shift = new Vector3[positions.Length];
        int count = Math.Min(piece.InternalLines.Count, mesh.LineChains.Length);
        for (int l = 0; l < count; l++)
        {
            var line = piece.InternalLines[l];
            var chain = mesh.LineChains[l];
            if (!line.IsFold || chain.Length < 2)
                continue;
            float spacing = 0;
            for (int k = 0; k + 1 < chain.Length; k++)
                spacing += Vector2.Distance(mesh.Points[chain[k]], mesh.Points[chain[k + 1]]);
            spacing = spacing / (chain.Length - 1) * 0.01f;
            float sign = (line.Valley ? -1f : 1f) * outward;
            float amount = 0.3f * spacing * MathF.Sin(Math.Clamp(line.FoldAngle, 30f, 90f) * MathF.PI / 180f);
            foreach (int v in chain)
                shift[v] += normals[v] * sign * amount;
        }
        foreach (var (apex, reach) in darts)
        {
            var centre = mesh.Points[apex];
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                float r = Vector2.Distance(mesh.Points[i], centre) / reach;
                if (r < 1)
                    shift[i] += normals[i] * outward * (0.06f * reach * 0.01f * (1 - r) * (1 - r));
            }
        }
        for (int i = 0; i < positions.Length; i++)
            positions[i] += shift[i];
    }

    /// <summary>
    /// A knife pleat: the stretch of edge from <see cref="A"/> to <see cref="B"/> folded under and tacked face to face
    /// to the stretch from <see cref="B"/> on (both local cm), so that <see cref="A"/> comes to lie on the point
    /// <see cref="Depth"/> x 2 further along.
    /// </summary>
    public readonly record struct Pleat(Vector2 A, Vector2 B, float Depth);

    /// <summary>The tacked pleats of a piece: folded seams joining two of its own edges that follow each other.</summary>
    public static List<Pleat> Pleats(Garment garment, PatternPiece piece)
    {
        var result = new List<Pleat>();
        foreach (var seam in garment.Seams)
        {
            if (!seam.Folded || seam.A.PieceId != piece.Id || seam.B.PieceId != piece.Id
                || seam.A.Edge < 0 || seam.A.Edge >= piece.EdgeCount || piece.NextPoint(seam.A.Edge) != seam.B.Edge)
                continue;
            var a = Outline.EdgeStart(piece, seam.A.Edge);
            var b = Outline.EdgeEnd(piece, seam.A.Edge);
            float depth = Vector2.Distance(a, b);
            if (depth > 1e-3f)
                result.Add(new Pleat(a, b, depth));
        }
        return result;
    }

    /// <summary>
    /// Lays a flat piece's tacked knife pleats already folded: from each
    /// pleat's fold the cloth turns back under itself for the pleat's depth, then forward again, and everything
    /// beyond moves along by twice the depth. The tacks then start closed. Only folds lying square to the edge, as
    /// the Pleat tool makes them, are folded this way.
    /// </summary>
    public static void PreFoldPleats(Garment garment, PatternPiece piece, PieceMesh mesh, Vector3[] positions)
    {
        var pleats = Pleats(garment, piece);
        if (pleats.Count == 0)
            return;
        // All pleats along one straight edge share its direction.
        var along = Vector2.Normalize(pleats[0].B - pleats[0].A);
        if (pleats.Any(p => Vector2.Dot(Vector2.Normalize(p.B - p.A), along) < 0.999f))
            return;
        float layer = MathF.Max(garment.EffectiveThicknessMm(piece), 1f) * 0.0015f;
        float inward = piece.FlipNormals ? 1f : -1f;   // the folded-under layers go behind, towards the body
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            var p = mesh.Points[i];
            float shift = 0;
            int depth = 0;
            foreach (var pleat in pleats)
            {
                float u = Vector2.Dot(p - pleat.A, along);
                if (u <= 0)
                    continue;
                if (u < pleat.Depth)
                {
                    shift -= 2 * u;           // turned back under the fold
                    depth = 1;
                }
                else if (u < 2 * pleat.Depth)
                {
                    shift -= 2 * pleat.Depth; // and forward again, under that
                    depth = 2;
                }
                else
                {
                    shift -= 2 * pleat.Depth; // beyond the pleat: moved along by what it takes in
                }
            }
            if (shift == 0 && depth == 0)
                continue;
            var (position, rotation) = Mesher.ArrangedFrame(piece, p + along * shift);
            var normal = Vector3.TransformNormal(Vector3.UnitZ, rotation);
            positions[i] = position + normal * (inward * layer * depth);
        }
    }

    /// <summary>
    /// For each particle of the solver, the pleat whose folded layers it is part of (unique across the solver), or
    /// -1. A pleat's layers lie folded against each other by design, held in order by its fold lines and its tack;
    /// cloth-to-cloth contacts between them only fought that (pushing the stack apart and working the garment down
    /// the body), so the solver leaves pairs within one pleat out.
    /// </summary>
    public static int[] PleatIds(IReadOnlyList<ClothPiece> pieces, int particleCount)
    {
        int[]? ids = null;
        for (int pi = 0; pi < pieces.Count; pi++)
        {
            var cloth = pieces[pi];
            if (!cloth.Simulated)
                continue;
            var pleats = Pleats(cloth.Source, cloth.Piece);
            for (int k = 0; k < pleats.Count; k++)
            {
                var pleat = pleats[k];
                var along = Vector2.Normalize(pleat.B - pleat.A);
                // The layers span from a pleat's depth before its fold (where the fold lies on the cloth) to twice
                // its depth after it, plus a little for the triangles along the band's edges.
                float margin = cloth.Source.EffectiveParticleDistance(cloth.Piece);
                for (int i = 0; i < cloth.Mesh.VertexCount; i++)
                {
                    float u = Vector2.Dot(cloth.Mesh.Points[i] - pleat.A, along);
                    if (u < -pleat.Depth - margin || u > 2 * pleat.Depth + margin)
                        continue;
                    ids ??= Enumerable.Repeat(-1, particleCount).ToArray();
                    ids[cloth.Offset + i] = pi * 4096 + k;
                }
            }
        }
        return ids ?? Array.Empty<int>();
    }

    /// <summary>
    /// The darts of a piece: seams joining two of its own edges that meet at a point (the dart's point), with how far
    /// the legs reach from it in centimetres.
    /// </summary>
    public static List<(int Apex, float Reach)> Darts(Garment garment, PatternPiece piece, PieceMesh mesh)
    {
        var result = new List<(int, float)>();
        foreach (var seam in garment.Seams)
        {
            if (seam.Folded || seam.Reversed || seam.A.PieceId != piece.Id || seam.B.PieceId != piece.Id)
                continue;
            int a = seam.A.Edge, b = seam.B.Edge;
            if (a < 0 || b < 0 || a >= mesh.EdgeChains.Length || b >= mesh.EdgeChains.Length)
                continue;
            // Leg A runs into the point and leg B out of it (or the other way round).
            int apex = piece.NextPoint(a) == b ? mesh.EdgeChains[a][^1] : piece.NextPoint(b) == a ? mesh.EdgeChains[b][^1] : -1;
            if (apex < 0)
                continue;
            float reach = 0.5f * (Outline.EdgeLength(piece, a) + Outline.EdgeLength(piece, b));
            result.Add((apex, MathF.Max(reach, 0.5f)));
        }
        return result;
    }
}
