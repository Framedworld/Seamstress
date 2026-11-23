namespace Seamstress.Core.Geometry;

/// <summary>
/// Hashes a mesh edge stored as one <see cref="long"/> (the lower vertex in the high 32 bits, the higher in the low).
/// The default hash of a long XORs its two halves, which for neighbouring vertices gives a handful of values over and
/// over: dictionaries of edges then slowed to a crawl (70-230 ms to thicken an 18,000-vertex jacket for display).
/// </summary>
public sealed class EdgeKey : IEqualityComparer<long>
{
    public static readonly EdgeKey Comparer = new();

    public static long Of(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    public bool Equals(long x, long y) => x == y;

    public int GetHashCode(long key)
    {
        unchecked
        {
            ulong mixed = (ulong)key * 0x9E3779B97F4A7C15UL;
            return (int)(mixed >> 32) ^ (int)mixed;
        }
    }
}
