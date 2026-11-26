using System.Numerics;
using Seamstress.Core.IO;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.Rigging;

public sealed class SkinTransferOptions
{
    /// <summary>Rounds of smoothing over the cloth mesh. More rounds spread weights further across loose cloth.</summary>
    public int SmoothingIterations { get; set; } = 12;

    /// <summary>Cloth this close to the body (metres) mostly keeps the weights of the body under it.</summary>
    public float NearDistance { get; set; } = 0.01f;

    /// <summary>Cloth this far from the body (metres) or farther takes its weights only from the cloth around it.</summary>
    public float FarDistance { get; set; } = 0.08f;

    /// <summary>Vertices of different pieces closer than this (metres) are smoothed together even when not welded.</summary>
    public float JoinDistance { get; set; } = 0.002f;
}

/// <summary>
/// Gives garment vertices skin weights from the body they were draped on: each vertex takes the barycentric blend of
/// the weights at the nearest point of the body's surface, then the weights are smoothed over the cloth so seams and
/// loose cloth (a skirt between the legs, a hood) move as one piece instead of tearing between bones.
/// </summary>
public static class SkinTransfer
{
    public static VertexWeights Transfer(TriMesh body, VertexWeights bodyWeights, Vector3[] positions, int[] triangles,
        SkinTransferOptions? options = null, TriangleLocator? locator = null)
    {
        options ??= new SkinTransferOptions();
        if (bodyWeights.VertexCount != body.Positions.Length)
            throw new ArgumentException("The body weights must have one entry per body vertex.", nameof(bodyWeights));
        locator ??= new TriangleLocator(body.Positions, body.Triangles);
        int n = positions.Length;

        // 1. Nearest body point and its blended weights.
        var raw = new List<(int Joint, float Weight)>[n];
        var closeness = new float[n];
        Parallel.For(0, n, v =>
        {
            var hit = locator.Closest(positions[v]);
            var blend = new List<(int, float)>(12);
            foreach (var (corner, w) in new[] { (hit.A, hit.Wa), (hit.B, hit.Wb), (hit.C, hit.Wc) })
            {
                if (w <= 0)
                    continue;
                foreach (var (joint, weight) in bodyWeights.Of(corner))
                    blend.Add((joint, weight * w));
            }
            raw[v] = blend;
            float t = (hit.Distance - options.NearDistance) / MathF.Max(1e-6f, options.FarDistance - options.NearDistance);
            closeness[v] = 1 - Math.Clamp(t, 0, 1);
        });

        // 2. Dense weights over the joints this garment actually uses.
        var columns = new Dictionary<int, int>();
        foreach (var list in raw)
        {
            foreach (var (joint, _) in list)
                columns.TryAdd(joint, columns.Count);
        }
        int m = Math.Max(1, columns.Count);
        var original = new float[n * m];
        for (int v = 0; v < n; v++)
        {
            foreach (var (joint, weight) in raw[v])
                original[v * m + columns[joint]] += weight;
        }

        // 3. Smooth over the cloth's edges, holding cloth that lies on the body to the body's weights.
        var neighbours = Neighbours(positions, triangles, options.JoinDistance);
        var current = (float[])original.Clone();
        var next = new float[current.Length];
        for (int iteration = 0; iteration < options.SmoothingIterations; iteration++)
        {
            var source = current;
            var target = next;
            Parallel.For(0, n, v =>
            {
                var nb = neighbours[v];
                int row = v * m;
                if (nb.Length == 0)
                {
                    Array.Copy(source, row, target, row, m);
                    return;
                }
                // Laplacian step (half way to the neighbours' average), then pull back towards the body's weights.
                float anchor = 0.5f * closeness[v];
                float share = 0.5f / nb.Length;
                for (int c = 0; c < m; c++)
                {
                    float sum = 0;
                    foreach (int u in nb)
                        sum += source[u * m + c];
                    float smoothed = 0.5f * source[row + c] + share * sum;
                    target[row + c] = smoothed + (original[row + c] - smoothed) * anchor;
                }
            });
            (current, next) = (next, current);
        }

        // 4. The strongest four, normalised.
        var jointOf = new int[m];
        foreach (var (joint, column) in columns)
            jointOf[column] = joint;
        var joints = new int[n * VertexWeights.MaxInfluences];
        var weights = new float[n * VertexWeights.MaxInfluences];
        int fallback = columns.Count > 0 ? jointOf[0] : 0;
        Parallel.For(0, n, v =>
        {
            var influences = new List<(int, float)>(m);
            for (int c = 0; c < m; c++)
            {
                float w = current[v * m + c];
                if (w > 1e-6f)
                    influences.Add((jointOf[c], w));
            }
            VertexWeights.Pack(influences, v, joints, weights, fallback);
        });
        return new VertexWeights(joints, weights);
    }

    /// <summary>Each vertex's neighbours along triangle edges, plus vertices of other pieces lying on top of it.</summary>
    private static int[][] Neighbours(Vector3[] positions, int[] triangles, float joinDistance)
    {
        var sets = new HashSet<int>[positions.Length];
        for (int i = 0; i < sets.Length; i++)
            sets[i] = new HashSet<int>();
        void Link(int a, int b)
        {
            if (a == b)
                return;
            sets[a].Add(b);
            sets[b].Add(a);
        }
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            Link(triangles[t], triangles[t + 1]);
            Link(triangles[t + 1], triangles[t + 2]);
            Link(triangles[t + 2], triangles[t]);
        }
        if (joinDistance > 0)
        {
            var cells = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / joinDistance), (int)MathF.Floor(p.Y / joinDistance), (int)MathF.Floor(p.Z / joinDistance));
            for (int i = 0; i < positions.Length; i++)
            {
                var c = Cell(positions[i]);
                if (!cells.TryGetValue(c, out var list))
                    cells[c] = list = new List<int>();
                list.Add(i);
            }
            float limit = joinDistance * joinDistance;
            for (int i = 0; i < positions.Length; i++)
            {
                var (cx, cy, cz) = Cell(positions[i]);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (!cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                                continue;
                            foreach (int j in list)
                            {
                                if (j > i && Vector3.DistanceSquared(positions[i], positions[j]) <= limit)
                                    Link(i, j);
                            }
                        }
            }
        }
        return sets.Select(s => s.ToArray()).ToArray();
    }
}
