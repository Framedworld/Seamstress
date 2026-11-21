namespace Seamstress.Cuda;

/// <summary>
/// Partitions pairwise constraints (a[i], b[i]) into vertex-disjoint color batches so every constraint
/// in a batch can be projected in parallel on CUDA cores with zero write-write races.
/// </summary>
internal readonly struct ColoredConstraints
{
    public required int[] A { get; init; }
    public required int[] B { get; init; }
    public required float[] Rest { get; init; }
    public required float[] Compliance { get; init; }
    public required int[] ColorOffsets { get; init; }
    public int ColorCount => ColorOffsets.Length > 0 ? ColorOffsets.Length - 1 : 0;

    public static ColoredConstraints Build(int particleCount, int[] a, int[] b, float[]? rest = null, float[]? compliance = null)
    {
        int m = a.Length;
        if (m == 0)
        {
            return new ColoredConstraints
            {
                A = Array.Empty<int>(),
                B = Array.Empty<int>(),
                Rest = Array.Empty<float>(),
                Compliance = Array.Empty<float>(),
                ColorOffsets = new[] { 0 },
            };
        }

        // Greedy coloring using per-vertex bitmasks (64 colors fast-path + HashSet overflow).
        var usedMask = new ulong[particleCount];
        Dictionary<int, HashSet<int>>? overflow = null;
        var colorOf = new int[m];
        int maxColor = 0;

        for (int i = 0; i < m; i++)
        {
            int va = a[i], vb = b[i];
            ulong combined = usedMask[va] | usedMask[vb];
            int color;
            if (combined != ulong.MaxValue)
            {
                color = System.Numerics.BitOperations.TrailingZeroCount(~combined);
                ulong bit = 1UL << color;
                usedMask[va] |= bit;
                usedMask[vb] |= bit;
            }
            else
            {
                overflow ??= new Dictionary<int, HashSet<int>>();
                if (!overflow.TryGetValue(va, out var setA)) overflow[va] = setA = new HashSet<int>();
                if (!overflow.TryGetValue(vb, out var setB)) overflow[vb] = setB = new HashSet<int>();
                color = 64;
                while (setA.Contains(color) || setB.Contains(color))
                    color++;
                setA.Add(color);
                setB.Add(color);
            }
            colorOf[i] = color;
            if (color > maxColor)
                maxColor = color;
        }

        int colorCount = maxColor + 1;
        var counts = new int[colorCount];
        for (int i = 0; i < m; i++)
            counts[colorOf[i]]++;

        var offsets = new int[colorCount + 1];
        for (int c = 0; c < colorCount; c++)
            offsets[c + 1] = offsets[c] + counts[c];

        var cursor = (int[])offsets.Clone();
        var outA = new int[m];
        var outB = new int[m];
        var outRest = rest != null ? new float[m] : Array.Empty<float>();
        var outComp = compliance != null ? new float[m] : Array.Empty<float>();

        for (int i = 0; i < m; i++)
        {
            int dst = cursor[colorOf[i]]++;
            outA[dst] = a[i];
            outB[dst] = b[i];
            if (rest != null) outRest[dst] = rest[i];
            if (compliance != null) outComp[dst] = compliance[i];
        }

        return new ColoredConstraints
        {
            A = outA,
            B = outB,
            Rest = outRest,
            Compliance = outComp,
            ColorOffsets = offsets,
        };
    }
}
