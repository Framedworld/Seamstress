using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Simulation;

/// <summary>A seam or surface stitch whose two sides are not together, with a plain-language guess at why.</summary>
public sealed record SeamIssue(
    string Kind, string Id, string PieceA, int EdgeA, string PieceB, int EdgeB, float GapMm, float LengthCm, string Hint);

/// <summary>Finds a point given in a piece's 2D pattern coordinates on its current 3D surface.</summary>
public static class SurfaceLocator
{
    /// <summary>
    /// The 3D position and outward normal of pattern point <paramref name="point"/> (cm). A point outside the outline
    /// snaps to the nearest point of the piece; <paramref name="offCm"/> says how far it was moved.
    /// </summary>
    public static bool TryLocate(PieceMesh mesh, Vector3[] positions, Vector2 point, bool flipNormals,
        out Vector3 position, out Vector3 normal, out float offCm)
        => TryLocate(mesh, positions, point, flipNormals, out position, out normal, out offCm, out _);

    /// <summary>As above; <paramref name="snapped"/> is the pattern point that was actually used.</summary>
    public static bool TryLocate(PieceMesh mesh, Vector3[] positions, Vector2 point, bool flipNormals,
        out Vector3 position, out Vector3 normal, out float offCm, out Vector2 snapped)
    {
        position = default;
        normal = Vector3.UnitZ;
        offCm = 0;
        snapped = point;
        var tris = mesh.Triangles;
        var pts = mesh.Points;
        if (tris.Length < 3 || positions.Length != pts.Length)
            return false;
        var q = new Vector3(point.X, point.Y, 0f);
        int best = -1;
        float bestD2 = float.MaxValue, wa = 1, wb = 0, wc = 0;
        for (int t = 0; t < tris.Length; t += 3)
        {
            var a = pts[tris[t]];
            var b = pts[tris[t + 1]];
            var c = pts[tris[t + 2]];
            var closest = SdfGrid.ClosestPointOnTriangle(q, new Vector3(a, 0), new Vector3(b, 0), new Vector3(c, 0),
                out float ua, out float ub, out float uc);
            float d2 = Vector3.DistanceSquared(q, closest);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = t;
                wa = ua; wb = ub; wc = uc;
                if (d2 < 1e-10f)
                    break;
            }
        }
        if (best < 0)
            return false;
        int ia = tris[best], ib = tris[best + 1], ic = tris[best + 2];
        position = positions[ia] * wa + positions[ib] * wb + positions[ic] * wc;
        snapped = pts[ia] * wa + pts[ib] * wb + pts[ic] * wc;
        var n = Vector3.Cross(positions[ib] - positions[ia], positions[ic] - positions[ia]);
        // Pattern triangles are counter-clockwise; a degenerate 3D triangle keeps the default normal.
        if (n.LengthSquared() > 1e-18f)
            normal = Vector3.Normalize(n) * (flipNormals ? -1 : 1);
        float area = (pts[ib].X - pts[ia].X) * (pts[ic].Y - pts[ia].Y) - (pts[ib].Y - pts[ia].Y) * (pts[ic].X - pts[ia].X);
        if (area < 0)
            normal = -normal;
        offCm = MathF.Sqrt(bestD2);
        return true;
    }
}

public static class SeamReport
{
    /// <summary>
    /// Every seam and surface stitch of <paramref name="garment"/> that is further apart than
    /// <paramref name="thresholdMm"/>, widest first. Nothing is reported for a garment that has not been draped.
    /// </summary>
    public static List<SeamIssue> OpenSeams(Document document, Func<Garment, GarmentMesh> meshFor, Garment garment,
        float thresholdMm = 5f)
    {
        var issues = new List<SeamIssue>();
        var cache = new Dictionary<string, (PatternPiece Piece, PieceMesh Mesh, Vector3[] Positions, Garment Owner)>();
        (PatternPiece Piece, PieceMesh Mesh, Vector3[] Positions, Garment Owner)? Resolve(string pieceId)
        {
            if (cache.TryGetValue(pieceId, out var hit))
                return hit;
            var owner = document.GarmentOfPiece(pieceId);
            var piece = owner?.FindPiece(pieceId);
            var mesh = owner == null ? null : meshFor(owner).Find(pieceId);
            if (owner == null || piece == null || mesh == null)
                return null;
            var entry = (piece, mesh, Mesher.CurrentPositions(piece, mesh), owner);
            cache[pieceId] = entry;
            return entry;
        }

        foreach (var seam in garment.Seams)
        {
            if (Resolve(seam.A.PieceId) is not { } a || Resolve(seam.B.PieceId) is not { } b)
                continue;
            if (seam.A.Edge >= a.Mesh.EdgeChains.Length || seam.B.Edge >= b.Mesh.EdgeChains.Length)
                continue;
            if (a.Piece.Drape == null && b.Piece.Drape == null)
                continue;
            var chainA = a.Mesh.EdgeChains[seam.A.Edge];
            var chainB = b.Mesh.EdgeChains[seam.B.Edge];
            int n = Math.Min(chainA.Length, chainB.Length);
            if (n == 0)
                continue;
            float gap = 0, flipped = 0;
            for (int k = 0; k < n; k++)
            {
                var pa = a.Positions[chainA[k]];
                var sewn = b.Positions[seam.Reversed ? chainB[k] : chainB[chainB.Length - 1 - k]];
                var other = b.Positions[seam.Reversed ? chainB[chainB.Length - 1 - k] : chainB[k]];
                gap = MathF.Max(gap, Vector3.Distance(pa, sewn));
                flipped = MathF.Max(flipped, Vector3.Distance(pa, other));
            }
            float gapMm = gap * 1000f;
            if (gapMm <= thresholdMm)
                continue;
            float lengthCm = Outline.EdgeLength(a.Piece, seam.A.Edge);
            string hint = flipped < gap * 0.5f
                ? "Sewn the wrong way round: the edges meet end to end when reversed. Use reverse_seam."
                : gapMm > lengthCm * 10f * 3f
                    ? "The two edges are far apart: check the pieces are arranged on the same side of the body."
                    : "Blocked: the body is between these edges, or the pieces are too short or too narrow to reach "
                      + "each other here. Add ease, move the seam line clear of the body, or arrange the pieces closer.";
            issues.Add(new SeamIssue("seam", seam.Id, a.Piece.Name, seam.A.Edge, b.Piece.Name, seam.B.Edge,
                MathF.Round(gapMm, 1), MathF.Round(lengthCm, 1), hint));
        }

        foreach (var stitch in garment.SurfaceStitches)
        {
            if (Resolve(stitch.SourcePieceId) is not { } src || Resolve(stitch.TargetPieceId) is not { } host)
                continue;
            if (stitch.SourceEdge < 0 || stitch.SourceEdge >= src.Mesh.EdgeChains.Length || src.Piece.Drape == null)
                continue;
            var chain = src.Mesh.EdgeChains[stitch.SourceEdge];
            float gap = 0, off = 0;
            for (int k = 0; k < chain.Length; k++)
            {
                float t = chain.Length > 1 ? k / (float)(chain.Length - 1) : 0.5f;
                var target = Vector2.Lerp(stitch.TargetStart, stitch.TargetEnd, t);
                if (!SurfaceLocator.TryLocate(host.Mesh, host.Positions, target, host.Piece.FlipNormals, out var at, out _, out float offCm))
                    continue;
                gap = MathF.Max(gap, Vector3.Distance(src.Positions[chain[k]], at));
                off = MathF.Max(off, offCm);
            }
            // A patch rides a few millimetres proud of its host by design (both thicknesses plus a little air).
            float ts = src.Owner.EffectiveThicknessMm(src.Piece), th = host.Owner.EffectiveThicknessMm(host.Piece);
            // In a hollow of the body (the corner of neck and shoulder) the room a garment keeps is measured round
            // the corner, so a band sewn there stands further off than on a flat part: allow for that.
            float allowed = thresholdMm + 3f + MathF.Max(0.5f * (ts + th) + 1.8f, 2.5f * ((src.Owner.BodyOffset + ts) - (host.Owner.BodyOffset + th)));
            float gapMm = gap * 1000f;
            if (gapMm <= allowed && off < 0.5f)
                continue;
            string hint = off >= 0.5f
                ? $"The stitch line leaves the host piece by {off:0.#} cm: keep it inside the host's outline."
                : "The patch edge is not on its stitch line: simulate the patch's garment after its host has been draped.";
            issues.Add(new SeamIssue("surfaceStitch", stitch.Id, src.Piece.Name, stitch.SourceEdge, host.Piece.Name, -1,
                MathF.Round(gapMm, 1), MathF.Round(Outline.EdgeLength(src.Piece, stitch.SourceEdge), 1), hint));
        }

        issues.Sort((x, y) => y.GapMm.CompareTo(x.GapMm));
        return issues;
    }
}
