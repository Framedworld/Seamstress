using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Geometry;

/// <summary>
/// Makes a piece's mesh follow its internal lines: vertices are placed along each line, the interior lattice keeps
/// clear of them, and after triangulation every stretch between two neighbouring line vertices is made an edge of
/// the mesh (by flipping the edges that cross it), so the cloth can fold exactly along the line.
/// </summary>
public static class InternalLineMesher
{
    /// <summary>
    /// Adds the vertices of every internal line to <paramref name="points"/> (which holds the boundary vertices,
    /// <paramref name="boundaryCount"/> of them) and returns, for each line of the piece, its vertices in order. A
    /// line end close to the outline lands on the outline vertex nearest to it, so a fold can run edge to edge; lines
    /// or parts of lines off the cloth are left out (a line with nothing left gets an empty chain).
    /// </summary>
    public static int[][] AddLineVertices(PatternPiece piece, List<Vector2> points, int boundaryCount,
        IReadOnlyList<Vector2> outer, IReadOnlyList<List<Vector2>> holes, float spacing)
    {
        var chains = new int[piece.InternalLines.Count][];
        bool OnCloth(Vector2 p) => Outline.Contains(outer, p) && !holes.Any(h => Outline.Contains(h, p));
        float ToBoundary(Vector2 p)
        {
            float d = Outline.DistanceToPolyline(p, outer, true);
            foreach (var hole in holes)
                d = MathF.Min(d, Outline.DistanceToPolyline(p, hole, true));
            return d;
        }
        (int Index, float Distance) Nearest(Vector2 p, int count)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                float d = Vector2.DistanceSquared(points[i], p);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return (best, MathF.Sqrt(bestD));
        }

        float merge = spacing * 0.45f;
        var sampled = Crossings(piece.InternalLines.Select(l => l.Sample(spacing)).ToList(), merge);
        for (int l = 0; l < piece.InternalLines.Count; l++)
        {
            var samples = sampled[l];
            var chain = new List<int>();
            for (int k = 0; k < samples.Count; k++)
            {
                var p = samples[k];
                bool end = k == 0 || k == samples.Count - 1;
                int index;
                var (near, distance) = Nearest(p, points.Count);
                if (near >= 0 && distance < merge)
                {
                    // Another vertex is right here already: an outline vertex, or a crossing line's.
                    index = near;
                }
                else if (!OnCloth(p) || ToBoundary(p) < merge)
                {
                    // At the outline: the ends catch on it; anything else would only make slivers.
                    if (!end)
                        continue;
                    var (edgeVertex, edgeDistance) = Nearest(p, boundaryCount);
                    if (edgeVertex < 0 || edgeDistance > spacing * 1.5f)
                        continue;
                    index = edgeVertex;
                }
                else
                {
                    index = points.Count;
                    points.Add(p);
                }
                if (chain.Count == 0 || chain[^1] != index)
                    chain.Add(index);
            }
            chains[l] = chain.Count >= 2 ? chain.ToArray() : Array.Empty<int>();
        }
        return chains;
    }

    /// <summary>
    /// Where lines cross, both get a point at the crossing (so they share a vertex there instead of having edges
    /// that cross), and their other points too close to it are dropped.
    /// </summary>
    private static List<List<Vector2>> Crossings(List<List<Vector2>> lines, float merge)
    {
        var extra = lines.Select(_ => new List<(int Segment, float T, Vector2 At)>()).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            for (int j = i + 1; j < lines.Count; j++)
            {
                for (int a = 0; a + 1 < lines[i].Count; a++)
                {
                    for (int b = 0; b + 1 < lines[j].Count; b++)
                    {
                        if (Intersect(lines[i][a], lines[i][a + 1], lines[j][b], lines[j][b + 1]) is not { } hit)
                            continue;
                        extra[i].Add((a, hit.S, hit.At));
                        extra[j].Add((b, hit.T, hit.At));
                    }
                }
            }
        }
        var result = new List<List<Vector2>>(lines.Count);
        for (int l = 0; l < lines.Count; l++)
        {
            if (extra[l].Count == 0)
            {
                result.Add(lines[l]);
                continue;
            }
            var points = new List<(Vector2 At, bool Forced)>();
            for (int k = 0; k < lines[l].Count; k++)
            {
                points.Add((lines[l][k], k == 0 || k == lines[l].Count - 1));
                foreach (var crossing in extra[l].Where(e => e.Segment == k).OrderBy(e => e.T))
                    points.Add((crossing.At, true));
            }
            var forced = points.Where(p => p.Forced).Select(p => p.At).ToList();
            result.Add(points.Where(p => p.Forced || forced.All(f => Vector2.Distance(f, p.At) >= merge)).Select(p => p.At).ToList());
        }
        return result;
    }

    private static (float S, float T, Vector2 At)? Intersect(Vector2 p0, Vector2 p1, Vector2 q0, Vector2 q1)
    {
        var r = p1 - p0;
        var d = q1 - q0;
        float den = r.X * d.Y - r.Y * d.X;
        if (MathF.Abs(den) < 1e-9f)
            return null;
        var w = q0 - p0;
        float s = (w.X * d.Y - w.Y * d.X) / den;
        float t = (w.X * r.Y - w.Y * r.X) / den;
        if (s < 0 || s >= 1 || t < 0 || t >= 1)
            return null;
        return (s, t, p0 + r * s);
    }

    /// <summary>The internal lines of a piece as polylines, for keeping the lattice clear of them.</summary>
    public static List<List<Vector2>> Polylines(PatternPiece piece)
        => piece.InternalLines.Where(l => l.Points.Count >= 2).Select(l => l.Flatten()).ToList();

    /// <summary>True when <paramref name="p"/> is closer than <paramref name="clearance"/> to any of the polylines.</summary>
    public static bool Near(Vector2 p, List<List<Vector2>> polylines, float clearance)
    {
        foreach (var line in polylines)
        {
            if (Outline.DistanceToPolyline(p, line, false) < clearance)
                return true;
        }
        return false;
    }

    /// <summary>Every stretch between neighbouring vertices of the chains.</summary>
    public static List<(int A, int B)> ChainEdges(IEnumerable<int[]> chains)
    {
        var edges = new List<(int, int)>();
        foreach (var chain in chains)
        {
            for (int k = 0; k + 1 < chain.Length; k++)
            {
                if (chain[k] != chain[k + 1])
                    edges.Add((chain[k], chain[k + 1]));
            }
        }
        return edges;
    }

    private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>
    /// Makes each of <paramref name="constraints"/> an edge of the counter-clockwise triangulation by flipping the
    /// edges that cross it, one at a time, whenever the two triangles beside such an edge form a convex quad. Edges
    /// already made constraints are never flipped away again.
    /// </summary>
    public static int[] Recover(double[] xs, double[] ys, int[] triangles, IReadOnlyList<(int A, int B)> constraints)
    {
        if (constraints.Count == 0)
            return triangles;
        var t = (int[])triangles.Clone();
        int count = t.Length / 3;
        var owners = new Dictionary<long, List<int>>();
        void Own(int tri)
        {
            for (int k = 0; k < 3; k++)
            {
                long key = Key(t[3 * tri + k], t[3 * tri + (k + 1) % 3]);
                if (!owners.TryGetValue(key, out var list))
                    owners[key] = list = new List<int>(2);
                list.Add(tri);
            }
        }
        void Disown(int tri)
        {
            for (int k = 0; k < 3; k++)
            {
                if (owners.TryGetValue(Key(t[3 * tri + k], t[3 * tri + (k + 1) % 3]), out var list))
                    list.Remove(tri);
            }
        }
        for (int i = 0; i < count; i++)
            Own(i);

        double Orient(int a, int b, int c) => (xs[b] - xs[a]) * (ys[c] - ys[a]) - (ys[b] - ys[a]) * (xs[c] - xs[a]);
        bool Crosses(int a, int b, int u, int v)
            => Orient(a, b, u) * Orient(a, b, v) < 0 && Orient(u, v, a) * Orient(u, v, b) < 0;
        bool Present(int a, int b) => owners.TryGetValue(Key(a, b), out var list) && list.Count > 0;

        var locked = new HashSet<long>();
        foreach (var (a, b) in constraints)
        {
            if (a == b)
                continue;
            double minX = Math.Min(xs[a], xs[b]), maxX = Math.Max(xs[a], xs[b]);
            double minY = Math.Min(ys[a], ys[b]), maxY = Math.Max(ys[a], ys[b]);
            for (int guard = 0; guard < 256 && !Present(a, b); guard++)
            {
                bool flipped = false;
                for (int tri = 0; tri < count && !flipped; tri++)
                {
                    for (int k = 0; k < 3 && !flipped; k++)
                    {
                        int u = t[3 * tri + k], v = t[3 * tri + (k + 1) % 3];
                        if (u == a || u == b || v == a || v == b)
                            continue;
                        if (Math.Max(xs[u], xs[v]) < minX || Math.Min(xs[u], xs[v]) > maxX
                            || Math.Max(ys[u], ys[v]) < minY || Math.Min(ys[u], ys[v]) > maxY)
                            continue;
                        long key = Key(u, v);
                        if (locked.Contains(key) || !Crosses(a, b, u, v) || owners[key].Count != 2)
                            continue;
                        int other = owners[key][0] == tri ? owners[key][1] : owners[key][0];
                        // This triangle is (u, v, c); the neighbour holds the edge the other way round, (v, u, d).
                        int c = t[3 * tri + (k + 2) % 3];
                        int d = -1;
                        for (int j = 0; j < 3; j++)
                        {
                            int w = t[3 * other + j];
                            if (w != u && w != v)
                                d = w;
                        }
                        if (d < 0 || !Crosses(c, d, u, v))
                            continue;   // not a convex quad: flipping would fold a triangle over
                        Disown(tri);
                        Disown(other);
                        t[3 * tri] = u; t[3 * tri + 1] = d; t[3 * tri + 2] = c;
                        t[3 * other] = d; t[3 * other + 1] = v; t[3 * other + 2] = c;
                        Own(tri);
                        Own(other);
                        flipped = true;
                    }
                }
                if (!flipped)
                    break;
            }
            if (Present(a, b))
                locked.Add(Key(a, b));
        }
        return t;
    }
}
