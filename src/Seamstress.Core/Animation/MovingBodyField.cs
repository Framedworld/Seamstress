using System.Diagnostics;
using System.Numerics;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.Animation;

/// <summary>
/// The collision field of a body that moves: a signed distance grid kept up to date frame by frame from the skinned
/// mesh, together with the field of the frame before (the solver blends between the two across substeps, so the body
/// moves smoothly instead of jumping once a frame) and the body's surface velocity in every cell near the skin (so
/// friction carries the cloth along). Only the region that moved is rebuilt: raising an arm touches the arm, not the
/// legs. Distances are exact in a narrow band round the skin and a flat inside/outside value beyond it, which is all a
/// collision needs; the grid covers the space the body sweeps over the whole animation, so its size never changes.
/// </summary>
public sealed class MovingBodyField
{
    /// <summary>Cells either side of the skin with exact distances.</summary>
    public const int Band = 3;

    private readonly int[] _triangles;
    private readonly int _nx, _ny, _nz;
    private readonly float _cell, _far;
    private readonly Vector3 _origin;

    private readonly float[] _unsigned;
    private readonly int[] _nearest;      // triangle nearest to the cell (band cells only)
    private readonly Vector2[] _bary;     // weights of the nearest point's second and third corners
    private readonly bool[] _inside;
    private readonly float[] _facing;     // how squarely the nearest triangle faces the cell, used to settle ties
    private readonly int[] _flood;        // cells reached from outside in the current flood carry the current mark
    private readonly int[] _queue;
    private int _floodMark;
    private Vector3[]? _last;
    private (int X0, int Y0, int Z0, int X1, int Y1, int Z1)? _lastRegion;
    private readonly List<int>[] _slices;

    /// <summary>The field at the end of the current frame; <see cref="SdfGrid.RawValues"/> is <see cref="Distances"/>.</summary>
    public SdfGrid Grid { get; }
    public float[] Distances => Grid.RawValues;

    /// <summary>The field at the end of the previous frame (the start of the current one).</summary>
    public float[] Previous { get; }

    /// <summary>The skin's velocity in metres per second in every band cell; zero elsewhere.</summary>
    public Vector3[] Velocity { get; }

    /// <summary>Z slices [first, last] rewritten by the last update (inclusive); empty when nothing moved.</summary>
    public int DirtyZ0 { get; private set; }
    public int DirtyZ1 { get; private set; } = -1;

    /// <summary>Counts every update; the solver uses it to tell a new frame from one it has seen.</summary>
    public long Version { get; private set; }

    /// <summary>Milliseconds the last update took, and how much of the grid it rewrote (0..1).</summary>
    public double LastUpdateMilliseconds { get; private set; }
    public float LastUpdateFraction { get; private set; }

    public int CellCount => Distances.Length;

    /// <summary>The box of cells the last update rewrote (inclusive), or null when nothing moved.</summary>
    public (int X0, int Y0, int Z0, int X1, int Y1, int Z1)? LastRegion { get; private set; }
    public float Cell => _cell;

    /// <param name="bounds">Space the body occupies over the whole animation.</param>
    public MovingBodyField(int[] triangles, (Vector3 Min, Vector3 Max) bounds, float cell = 0.01f)
    {
        _triangles = triangles;
        _cell = cell;
        _far = cell * (Band + 1);
        float pad = _far + 0.02f;
        var min = bounds.Min - new Vector3(pad);
        var max = bounds.Max + new Vector3(pad);
        _nx = (int)MathF.Ceiling((max.X - min.X) / cell) + 1;
        _ny = (int)MathF.Ceiling((max.Y - min.Y) / cell) + 1;
        _nz = (int)MathF.Ceiling((max.Z - min.Z) / cell) + 1;
        _origin = min;
        Grid = SdfGrid.Empty(min, cell, _nx, _ny, _nz);
        int count = _nx * _ny * _nz;
        Previous = new float[count];
        Velocity = new Vector3[count];
        _unsigned = new float[count];
        _nearest = new int[count];
        _bary = new Vector2[count];
        _inside = new bool[count];
        _facing = new float[count];
        _flood = new int[count];
        _queue = new int[count];
        _slices = new List<int>[_nz];
        for (int z = 0; z < _nz; z++)
            _slices[z] = new List<int>();
    }

    private int Index(int x, int y, int z) => (z * _ny + y) * _nx + x;

    /// <summary>
    /// Moves the body to <paramref name="positions"/>, <paramref name="seconds"/> after the previous call. With
    /// <paramref name="jump"/> (the first frame, or after seeking) the whole field is rebuilt and the body is still.
    /// </summary>
    public void Update(Vector3[] positions, float seconds, bool jump = false)
    {
        var clock = Stopwatch.StartNew();
        jump |= _last == null || _last.Length != positions.Length;
        Array.Copy(Distances, Previous, Distances.Length);
        (int X0, int Y0, int Z0, int X1, int Y1, int Z1)? region;
        if (jump)
        {
            region = (0, 0, 0, _nx - 1, _ny - 1, _nz - 1);
        }
        else
        {
            region = MovedRegion(positions);
            // The region that moved last frame is rewritten too, so its velocities go back to zero once it stops.
            if (_lastRegion is { } before)
                region = region is { } now ? Union(now, before) : before;
        }

        LastRegion = region;
        if (region is { } r)
        {
            Rebuild(r, positions, jump ? null : _last, seconds);
            DirtyZ0 = r.Z0;
            DirtyZ1 = r.Z1;
            LastUpdateFraction = (float)((long)(r.X1 - r.X0 + 1) * (r.Y1 - r.Y0 + 1) * (r.Z1 - r.Z0 + 1)) / Distances.Length;
        }
        else
        {
            DirtyZ0 = 0;
            DirtyZ1 = -1;
            LastUpdateFraction = 0;
        }
        _lastRegion = jump ? null : MovedRegion(positions, onlyMoved: true);
        _last ??= new Vector3[positions.Length];
        if (_last.Length != positions.Length)
            _last = new Vector3[positions.Length];
        Array.Copy(positions, _last, positions.Length);
        if (jump)
            Array.Copy(Distances, Previous, Distances.Length);
        Version++;
        LastUpdateMilliseconds = clock.Elapsed.TotalMilliseconds;
    }

    private static (int, int, int, int, int, int) Union((int X0, int Y0, int Z0, int X1, int Y1, int Z1) a, (int X0, int Y0, int Z0, int X1, int Y1, int Z1) b)
        => (Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Min(a.Z0, b.Z0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1), Math.Max(a.Z1, b.Z1));

    private (int X0, int Y0, int Z0, int X1, int Y1, int Z1)? _pendingMoved;

    /// <summary>Grid box round every triangle that has a corner that moved since the last update, old and new place.</summary>
    private (int X0, int Y0, int Z0, int X1, int Y1, int Z1)? MovedRegion(Vector3[] positions, bool onlyMoved = false)
    {
        if (onlyMoved)
            return _pendingMoved;
        var last = _last!;
        const float still = 1e-5f;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        var moved = new bool[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            if (Vector3.DistanceSquared(positions[i], last[i]) > still * still)
            {
                moved[i] = true;
                any = true;
            }
        }
        if (!any)
        {
            _pendingMoved = null;
            return null;
        }
        for (int t = 0; t < _triangles.Length; t += 3)
        {
            int a = _triangles[t], b = _triangles[t + 1], c = _triangles[t + 2];
            if (!moved[a] && !moved[b] && !moved[c])
                continue;
            foreach (int v in new[] { a, b, c })
            {
                min = Vector3.Min(min, Vector3.Min(positions[v], last[v]));
                max = Vector3.Max(max, Vector3.Max(positions[v], last[v]));
            }
        }
        var box = ToCells(min - new Vector3(_far), max + new Vector3(_far));
        _pendingMoved = box;
        return box;
    }

    private (int, int, int, int, int, int) ToCells(Vector3 min, Vector3 max)
    {
        int Lo(float v, float o, int n) => Math.Clamp((int)MathF.Floor((v - o) / _cell), 0, n - 1);
        int Hi(float v, float o, int n) => Math.Clamp((int)MathF.Ceiling((v - o) / _cell), 0, n - 1);
        return (Lo(min.X, _origin.X, _nx), Lo(min.Y, _origin.Y, _ny), Lo(min.Z, _origin.Z, _nz),
                Hi(max.X, _origin.X, _nx), Hi(max.Y, _origin.Y, _ny), Hi(max.Z, _origin.Z, _nz));
    }

    private void Rebuild((int X0, int Y0, int Z0, int X1, int Y1, int Z1) r, Vector3[] pos, Vector3[]? last, float seconds)
    {
        var d = Distances;
        for (int z = r.Z0; z <= r.Z1; z++)
            _slices[z].Clear();

        // Triangles whose band reaches into the region, binned by the Z slices they touch.
        int band = Band;
        for (int t = 0; t < _triangles.Length / 3; t++)
        {
            var a = pos[_triangles[3 * t]];
            var b = pos[_triangles[3 * t + 1]];
            var c = pos[_triangles[3 * t + 2]];
            var lo = Vector3.Min(a, Vector3.Min(b, c));
            var hi = Vector3.Max(a, Vector3.Max(b, c));
            int x0 = (int)MathF.Floor((lo.X - _origin.X) / _cell) - band, x1 = (int)MathF.Ceiling((hi.X - _origin.X) / _cell) + band;
            int y0 = (int)MathF.Floor((lo.Y - _origin.Y) / _cell) - band, y1 = (int)MathF.Ceiling((hi.Y - _origin.Y) / _cell) + band;
            if (x1 < r.X0 || x0 > r.X1 || y1 < r.Y0 || y0 > r.Y1)
                continue;
            int z0 = Math.Max(r.Z0, (int)MathF.Floor((lo.Z - _origin.Z) / _cell) - band);
            int z1 = Math.Min(r.Z1, (int)MathF.Ceiling((hi.Z - _origin.Z) / _cell) + band);
            for (int z = z0; z <= z1; z++)
                _slices[z].Add(t);
        }

        float invDt = seconds > 1e-6f ? 1f / seconds : 0f;
        Parallel.For(r.Z0, r.Z1 + 1, z =>
        {
            for (int y = r.Y0; y <= r.Y1; y++)
            {
                int row = Index(0, y, z);
                for (int x = r.X0; x <= r.X1; x++)
                {
                    _unsigned[row + x] = float.MaxValue;
                    _nearest[row + x] = -1;
                    _facing[row + x] = 0;
                    Velocity[row + x] = Vector3.Zero;
                }
            }
            foreach (int t in _slices[z])
            {
                int ia = _triangles[3 * t], ib = _triangles[3 * t + 1], ic = _triangles[3 * t + 2];
                var a = pos[ia];
                var b = pos[ib];
                var c = pos[ic];
                var normal = Vector3.Cross(b - a, c - a);
                float nl = normal.Length();
                if (nl < 1e-14f)
                    continue;
                normal /= nl;
                var tmin = Vector3.Min(a, Vector3.Min(b, c));
                var tmax = Vector3.Max(a, Vector3.Max(b, c));
                int x0 = Math.Max(r.X0, (int)MathF.Floor((tmin.X - _origin.X) / _cell) - band);
                int x1 = Math.Min(r.X1, (int)MathF.Ceiling((tmax.X - _origin.X) / _cell) + band);
                int y0 = Math.Max(r.Y0, (int)MathF.Floor((tmin.Y - _origin.Y) / _cell) - band);
                int y1 = Math.Min(r.Y1, (int)MathF.Ceiling((tmax.Y - _origin.Y) / _cell) + band);
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        var p = _origin + new Vector3(x, y, z) * _cell;
                        var q = SdfGrid.ClosestPointOnTriangle(p, a, b, c, out _, out float wb, out float wc);
                        var diff = p - q;
                        float dist = diff.Length();
                        if (dist > _far)
                            continue;
                        int i = Index(x, y, z);
                        float side = Vector3.Dot(diff, normal);
                        float face = dist > 1e-9f ? MathF.Abs(side) / dist : 1f;
                        float best = _unsigned[i];
                        // Where several triangles are equally near (edges, corners), the one facing the cell most decides the side.
                        if (dist < best - 1e-6f || (dist < best + 1e-6f && face > _facing[i]))
                        {
                            _unsigned[i] = dist;
                            _inside[i] = side < 0;
                            _nearest[i] = t;
                            _bary[i] = new Vector2(wb, wc);
                            _facing[i] = face;
                        }
                    }
                }
            }

            // Band cells: exact distance, side from the nearest triangle, and the skin's velocity.
            for (int y = r.Y0; y <= r.Y1; y++)
            {
                int row = Index(0, y, z);
                for (int x = r.X0; x <= r.X1; x++)
                {
                    int i = row + x;
                    if (_nearest[i] < 0)
                        continue;
                    d[i] = _inside[i] ? -_unsigned[i] : _unsigned[i];
                    if (last != null && invDt > 0)
                    {
                        int t = _nearest[i];
                        int ia = _triangles[3 * t], ib = _triangles[3 * t + 1], ic = _triangles[3 * t + 2];
                        var w = _bary[i];
                        float wa = 1 - w.X - w.Y;
                        Velocity[i] = ((pos[ia] - last[ia]) * wa + (pos[ib] - last[ib]) * w.X + (pos[ic] - last[ic]) * w.Y) * invDt;
                    }
                }
            }
        });

        // Cells beyond the band: outside where the space outside the body reaches them without crossing the band,
        // inside otherwise, as for the still avatar's field. The flood starts from the region's faces that border
        // flooded outside space (the cells next to them did not change) and from the edge of the grid.
        int queued = 0, head = 0;
        float plateau = _far * 0.9999f;
        void Seed(int x, int y, int z, int ox, int oy, int oz)
        {
            int i = Index(x, y, z);
            if (_nearest[i] >= 0 || _flood[i] == _floodMark)
                return;
            // Only flood-filled outside space counts, not a band cell's own side: near a self-intersecting skin
            // (between the thighs, an armpit) the nearest triangle can face the wrong way.
            bool exterior = ox < 0 || oy < 0 || oz < 0 || ox >= _nx || oy >= _ny || oz >= _nz || d[Index(ox, oy, oz)] >= plateau;
            if (!exterior)
                return;
            _flood[i] = _floodMark;
            _queue[queued++] = i;
        }
        _floodMark++;
        if (_floodMark == int.MaxValue)
        {
            Array.Clear(_flood);
            _floodMark = 1;
        }
        for (int z = r.Z0; z <= r.Z1; z++)
        {
            for (int y = r.Y0; y <= r.Y1; y++)
            {
                Seed(r.X0, y, z, r.X0 - 1, y, z);
                Seed(r.X1, y, z, r.X1 + 1, y, z);
            }
            for (int x = r.X0; x <= r.X1; x++)
            {
                Seed(x, r.Y0, z, x, r.Y0 - 1, z);
                Seed(x, r.Y1, z, x, r.Y1 + 1, z);
            }
        }
        for (int y = r.Y0; y <= r.Y1; y++)
        {
            for (int x = r.X0; x <= r.X1; x++)
            {
                Seed(x, y, r.Z0, x, y, r.Z0 - 1);
                Seed(x, y, r.Z1, x, y, r.Z1 + 1);
            }
        }
        int sy = _nx, sz = _nx * _ny;
        while (head < queued)
        {
            int i = _queue[head++];
            int x = i % _nx, y = i / _nx % _ny, z = i / sz;
            void Visit(int j)
            {
                if (_nearest[j] >= 0 || _flood[j] == _floodMark)
                    return;
                _flood[j] = _floodMark;
                _queue[queued++] = j;
            }
            if (x > r.X0) Visit(i - 1);
            if (x < r.X1) Visit(i + 1);
            if (y > r.Y0) Visit(i - sy);
            if (y < r.Y1) Visit(i + sy);
            if (z > r.Z0) Visit(i - sz);
            if (z < r.Z1) Visit(i + sz);
        }
        int mark = _floodMark;
        Parallel.For(r.Z0, r.Z1 + 1, z =>
        {
            for (int y = r.Y0; y <= r.Y1; y++)
            {
                int row = Index(0, y, z);
                for (int x = r.X0; x <= r.X1; x++)
                {
                    int i = row + x;
                    if (_nearest[i] < 0)
                        d[i] = _flood[i] == mark ? _far : -_far;
                }
            }
        });
    }

    /// <summary>
    /// The field blended between the previous frame (<paramref name="blend"/> 0) and the current one (1) at a point,
    /// with its outward direction and the body's velocity there. Far outside the grid it reports "no contact".
    /// </summary>
    public float Sample(Vector3 p, float blend, out Vector3 normal, out Vector3 velocity)
    {
        var g = (p - _origin) / _cell;
        if (!(g.X >= 0 && g.Y >= 0 && g.Z >= 0 && g.X < _nx - 1 && g.Y < _ny - 1 && g.Z < _nz - 1))
        {
            normal = Vector3.UnitY;
            velocity = Vector3.Zero;
            return float.MaxValue;
        }
        int x = (int)g.X, y = (int)g.Y, z = (int)g.Z;
        float fx = g.X - x, fy = g.Y - y, fz = g.Z - z;
        int i = Index(x, y, z);
        int sy = _nx, sz = _nx * _ny;
        var d1 = Distances;
        var d0 = Previous;
        float C(int k) => d0[k] + (d1[k] - d0[k]) * blend;
        float c000 = C(i), c100 = C(i + 1), c010 = C(i + sy), c110 = C(i + sy + 1);
        float c001 = C(i + sz), c101 = C(i + sz + 1), c011 = C(i + sz + sy), c111 = C(i + sz + sy + 1);

        float x00 = c000 + (c100 - c000) * fx, x10 = c010 + (c110 - c010) * fx;
        float x01 = c001 + (c101 - c001) * fx, x11 = c011 + (c111 - c011) * fx;
        float y0 = x00 + (x10 - x00) * fy, y1 = x01 + (x11 - x01) * fy;
        float value = y0 + (y1 - y0) * fz;

        float gx = ((c100 - c000) * (1 - fy) + (c110 - c010) * fy) * (1 - fz) + ((c101 - c001) * (1 - fy) + (c111 - c011) * fy) * fz;
        float gy = (x10 - x00) * (1 - fz) + (x11 - x01) * fz;
        float gz = y1 - y0;
        var grad = new Vector3(gx, gy, gz);
        float len = grad.Length();
        normal = len > 1e-9f ? grad / len : Vector3.UnitY;

        var v = Velocity;
        var vx00 = Vector3.Lerp(v[i], v[i + 1], fx);
        var vx10 = Vector3.Lerp(v[i + sy], v[i + sy + 1], fx);
        var vx01 = Vector3.Lerp(v[i + sz], v[i + sz + 1], fx);
        var vx11 = Vector3.Lerp(v[i + sz + sy], v[i + sz + sy + 1], fx);
        velocity = Vector3.Lerp(Vector3.Lerp(vx00, vx10, fy), Vector3.Lerp(vx01, vx11, fy), fz);
        return value;
    }
}
