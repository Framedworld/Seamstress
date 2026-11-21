using System.Numerics;

namespace Seamstress.Core.Simulation;

// Cloth-against-cloth collision: every particle is kept on its side of every nearby triangle, at a distance of
// the two fabrics' thickness. Covers a garment folding onto itself and garments layered over each other.
public sealed partial class ClothSolver
{
    private const float ContactMargin = 0.02f;
    private const float ContactGap = 0.0015f;

    private struct Contact
    {
        public int Particle;
        public int Triangle;
        public float Side;
    }

    private int[] _tris = Array.Empty<int>();
    private int[] _weld = Array.Empty<int>();
    private int[]?[] _near = Array.Empty<int[]?>();
    private Contact[] _contacts = Array.Empty<Contact>();
    private int _contactCount;
    private Dictionary<long, float> _sides = new();
    private Dictionary<long, float> _sidesNext = new();
    private int[] _bucketStart = Array.Empty<int>();
    private int[] _bucketFill = Array.Empty<int>();
    private int[] _bucketItems = Array.Empty<int>();
    private int[] _triStamp = Array.Empty<int>();
    private int _triStampEpoch;
    private int[] _triPiece = Array.Empty<int>();
    private Vector2[] _triRest = Array.Empty<Vector2>();
    private Vector3[] _triMin = Array.Empty<Vector3>();
    private Vector3[] _triMax = Array.Empty<Vector3>();
    private Vector2[] _rest2D = Array.Empty<Vector2>();
    private int[][] _workerStamps = Array.Empty<int[]>();
    private int[] _workerEpochs = Array.Empty<int>();
    private List<Contact>[] _workerContacts = Array.Empty<List<Contact>>();

    // Free boundary (hem) edges for edge-edge collision so two hanging hems do not slip past each other.
    private int[] _hemA = Array.Empty<int>();
    private int[] _hemB = Array.Empty<int>();
    private int[] _hemPiece = Array.Empty<int>();
    private Vector2[] _hemRest = Array.Empty<Vector2>();
    private (int E1, int E2)[] _hemContacts = Array.Empty<(int, int)>();
    private int _hemContactCount;
    private GpuPtContact[] _gpuPtContacts = Array.Empty<GpuPtContact>();
    private GpuHemContact[] _gpuHemContacts = Array.Empty<GpuHemContact>();

    // Within one piece, cloth closer than this in the flat pattern (cm) is held apart by the fabric itself.
    private const float SamePieceRestDistance = 5f;

    /// <summary>Number of particle-triangle pairs currently being watched.</summary>
    public int ContactCount => _contactCount;

    /// <summary>
    /// Records which particles are neighbours in the sewn-together surface. A particle never collides with triangles
    /// within two edges of it: those are held apart by the cloth itself.
    /// </summary>
    private void BuildContactTopology(int[] triangles, List<(int A, int B)> welds)
    {
        _tris = triangles;
        int triCount = triangles.Length / 3;
        _triStamp = new int[triCount];
        _triMin = new Vector3[triCount];
        _triMax = new Vector3[triCount];
        int n = Positions.Length;
        _rest2D = new Vector2[n];
        foreach (var piece in Pieces)
            piece.Mesh.Points.CopyTo(_rest2D, piece.Offset);
        _triPiece = new int[triangles.Length / 3];
        _triRest = new Vector2[triangles.Length / 3];
        for (int t = 0; t < _triPiece.Length; t++)
        {
            _triPiece[t] = _pieceOf[triangles[3 * t]];
            _triRest[t] = (_rest2D[triangles[3 * t]] + _rest2D[triangles[3 * t + 1]] + _rest2D[triangles[3 * t + 2]]) / 3f;
        }
        _weld = new int[n];
        for (int i = 0; i < n; i++)
            _weld[i] = i;
        int Find(int a)
        {
            while (_weld[a] != a)
                a = _weld[a] = _weld[_weld[a]];
            return a;
        }
        foreach (var (a, b) in welds)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
                _weld[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
        for (int i = 0; i < n; i++)
            _weld[i] = Find(i);

        var ring = new HashSet<int>[n];
        void Link(int a, int b)
        {
            (ring[a] ??= new HashSet<int>()).Add(b);
            (ring[b] ??= new HashSet<int>()).Add(a);
        }
        for (int t = 0; t < triangles.Length; t += 3)
        {
            int a = _weld[triangles[t]], b = _weld[triangles[t + 1]], c = _weld[triangles[t + 2]];
            Link(a, b);
            Link(b, c);
            Link(c, a);
        }
        _near = new int[]?[n];
        for (int i = 0; i < n; i++)
        {
            if (ring[i] == null)
                continue;
            var two = new HashSet<int>(ring[i]) { i };
            foreach (int neighbour in ring[i])
                two.UnionWith(ring[neighbour]);
            var sorted = two.ToArray();
            Array.Sort(sorted);
            _near[i] = sorted;
        }

        // Collect free boundary (hem) edges: edges belonging to a single triangle and not stitched on both ends.
        var edgeCounts = new Dictionary<long, (int A, int B, int Count)>();
        for (int t = 0; t < triangles.Length; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                edgeCounts[key] = edgeCounts.TryGetValue(key, out var e) ? (e.A, e.B, e.Count + 1) : (a, b, 1);
            }
        }
        var hemA = new List<int>();
        var hemB = new List<int>();
        var hemPiece = new List<int>();
        var hemRest = new List<Vector2>();
        foreach (var (_, (a, b, count)) in edgeCounts)
        {
            if (count != 1 || (_stitched[a] && _stitched[b]))
                continue;
            hemA.Add(a);
            hemB.Add(b);
            hemPiece.Add(_pieceOf[a]);
            hemRest.Add((_rest2D[a] + _rest2D[b]) * 0.5f);
        }
        _hemA = hemA.ToArray();
        _hemB = hemB.ToArray();
        _hemPiece = hemPiece.ToArray();
        _hemRest = hemRest.ToArray();
    }

    private bool AreNeighbours(int particle, int triangle)
    {
        var near = _near[_weld[particle]];
        if (near == null)
            return false;
        for (int k = 0; k < 3; k++)
        {
            if (Array.BinarySearch(near, _weld[_tris[3 * triangle + k]]) >= 0)
                return true;
        }
        return false;
    }

    private static int Bucket(int x, int y, int z, int mask)
        => ((x * 73856093) ^ (y * 19349663) ^ (z * 83492791)) & mask;

    /// <summary>Finds the particle-triangle pairs close enough to touch before the next search.</summary>
    private void FindContacts()
    {
        _contactCount = 0;
        var x = Positions;
        int triCount = _tris.Length / 3;
        if (triCount == 0)
            return;

        const float margin = ContactMargin;
        var marginVec = new Vector3(margin);
        float cell = MathF.Max(0.04f, margin);
        float inv = 1f / cell;
        int size = 1024;
        while (size < triCount * 4)
            size <<= 1;
        int mask = size - 1;
        if (_bucketStart.Length != size + 1)
        {
            _bucketStart = new int[size + 1];
            _bucketFill = new int[size];
        }
        Array.Clear(_bucketStart);

        // Two passes over the triangles' padded bounding boxes: count (caching AABBs), then fill.
        for (int pass = 0; pass < 2; pass++)
        {
            int total = 0;
            for (int t = 0; t < triCount; t++)
            {
                Vector3 minP, maxP;
                if (pass == 0)
                {
                    var a = x[_tris[3 * t]];
                    var b = x[_tris[3 * t + 1]];
                    var c = x[_tris[3 * t + 2]];
                    minP = Vector3.Min(a, Vector3.Min(b, c)) - marginVec;
                    maxP = Vector3.Max(a, Vector3.Max(b, c)) + marginVec;
                    _triMin[t] = minP;
                    _triMax[t] = maxP;
                }
                else
                {
                    minP = _triMin[t];
                    maxP = _triMax[t];
                }
                var lo = minP * inv;
                var hi = maxP * inv;
                int x0 = (int)MathF.Floor(lo.X), x1 = (int)MathF.Floor(hi.X);
                int y0 = (int)MathF.Floor(lo.Y), y1 = (int)MathF.Floor(hi.Y);
                int z0 = (int)MathF.Floor(lo.Z), z1 = (int)MathF.Floor(hi.Z);
                // A triangle stretched across the scene (mid-sewing) would flood the grid; it cannot be in contact anyway.
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 400)
                    continue;
                for (int iz = z0; iz <= z1; iz++)
                {
                    for (int iy = y0; iy <= y1; iy++)
                    {
                        for (int ix = x0; ix <= x1; ix++)
                        {
                            int bucket = Bucket(ix, iy, iz, mask);
                            if (pass == 0)
                                _bucketStart[bucket + 1]++;
                            else
                                _bucketItems[_bucketStart[bucket] + _bucketFill[bucket]++] = t;
                            total++;
                        }
                    }
                }
            }
            if (pass == 0)
            {
                for (int i = 0; i < size; i++)
                    _bucketStart[i + 1] += _bucketStart[i];
                if (_bucketItems.Length < total)
                    _bucketItems = new int[total + total / 2];
                Array.Clear(_bucketFill);
            }
        }

        _sidesNext.Clear();
        float reach = margin * margin;
        const float samePieceSq = SamePieceRestDistance * SamePieceRestDistance;

        if (x.Length < 512)
        {
            if (_triStampEpoch > int.MaxValue - x.Length - 2)
            {
                Array.Clear(_triStamp);
                _triStampEpoch = 0;
            }
            int baseEpoch = _triStampEpoch;
            _triStampEpoch += x.Length + 1;
            QueryParticleRange(0, x.Length, inv, mask, triCount, reach, samePieceSq, _triStamp, baseEpoch, null);
        }
        else
        {
            int workers = Math.Clamp(x.Length / 256, 2, Environment.ProcessorCount);
            if (_workerStamps.Length != workers)
            {
                _workerStamps = new int[workers][];
                _workerEpochs = new int[workers];
                _workerContacts = new List<Contact>[workers];
                for (int w = 0; w < workers; w++)
                {
                    _workerStamps[w] = new int[triCount];
                    _workerContacts[w] = new List<Contact>(256);
                }
            }
            int chunk = (x.Length + workers - 1) / workers;
            Parallel.For(0, workers, w =>
            {
                int start = w * chunk;
                int end = Math.Min(start + chunk, x.Length);
                var list = _workerContacts[w];
                list.Clear();
                if (start >= end)
                    return;
                var stamp = _workerStamps[w];
                int span = end - start + 1;
                if (_workerEpochs[w] > int.MaxValue - span - 1)
                {
                    Array.Clear(stamp);
                    _workerEpochs[w] = 0;
                }
                int baseEpoch = _workerEpochs[w];
                _workerEpochs[w] += span;
                QueryParticleRange(start, end, inv, mask, triCount, reach, samePieceSq, stamp, baseEpoch, list);
            });

            for (int w = 0; w < workers; w++)
            {
                var list = _workerContacts[w];
                int count = list.Count;
                if (count == 0)
                    continue;
                if (_contactCount + count > _contacts.Length)
                    Array.Resize(ref _contacts, Math.Max(1024, Math.Max(_contacts.Length * 2, _contactCount + count)));
                for (int i = 0; i < count; i++)
                {
                    var c = list[i];
                    _contacts[_contactCount++] = c;
                    _sidesNext[(long)c.Particle * triCount + c.Triangle] = c.Side;
                }
            }
        }

        (_sides, _sidesNext) = (_sidesNext, _sides);
        FindHemContacts(margin);
    }

    private void QueryParticleRange(
        int start, int end, float inv, int mask, int triCount, float reach, float samePieceSq,
        int[] stamp, int baseEpoch, List<Contact>? output)
    {
        var x = Positions;
        var bucketStart = _bucketStart;
        var bucketItems = _bucketItems;
        var triMin = _triMin;
        var triMax = _triMax;
        var triPiece = _triPiece;
        var triRest = _triRest;
        var rest2D = _rest2D;
        var pieceOf = _pieceOf;
        var tris = _tris;
        var invMass = _invMass;
        var sides = _sides;

        for (int p = start; p < end; p++)
        {
            int tag = baseEpoch + (p - start) + 1;
            var pos = x[p];
            int pPiece = pieceOf[p];
            var pRest = rest2D[p];
            float mp = invMass[p];
            int bucket = Bucket((int)MathF.Floor(pos.X * inv), (int)MathF.Floor(pos.Y * inv), (int)MathF.Floor(pos.Z * inv), mask);
            int kEnd = bucketStart[bucket + 1];
            for (int k = bucketStart[bucket]; k < kEnd; k++)
            {
                int t = bucketItems[k];
                if (stamp[t] == tag)
                    continue;
                stamp[t] = tag;

                var lo = triMin[t];
                var hi = triMax[t];
                if (pos.X < lo.X || pos.X > hi.X || pos.Y < lo.Y || pos.Y > hi.Y || pos.Z < lo.Z || pos.Z > hi.Z)
                    continue;

                if (triPiece[t] == pPiece && Vector2.DistanceSquared(triRest[t], pRest) < samePieceSq)
                    continue;

                int t3 = 3 * t;
                int ia = tris[t3], ib = tris[t3 + 1], ic = tris[t3 + 2];
                if (mp == 0f && invMass[ia] == 0f && invMass[ib] == 0f && invMass[ic] == 0f)
                    continue;

                var xa = x[ia];
                var xb = x[ib];
                var xc = x[ic];
                var q = SdfGrid.ClosestPointOnTriangle(pos, xa, xb, xc);
                var r = pos - q;
                float distance2 = r.LengthSquared();
                if (distance2 > reach)
                    continue;
                var normal = Vector3.Cross(xb - xa, xc - xa);
                float area = normal.Length();
                if (area < 1e-12f)
                    continue;
                // Only a particle that is above or below the triangle has a meaningful side. One that is merely
                // beside it (cloth continuing past the triangle's edge) is left alone until it moves over it.
                float height = Vector3.Dot(r, normal) / area;
                if (height * height < 0.5f * distance2)
                    continue;
                if (AreNeighbours(p, t))
                    continue;

                // Remember which face of the triangle the particle is on; it has to stay there for as long as the
                // pair remains close, even if it gets pushed through for a moment.
                long key = (long)p * triCount + t;
                if (!sides.TryGetValue(key, out float side))
                    side = height >= 0f ? 1f : -1f;
                var contact = new Contact { Particle = p, Triangle = t, Side = side };
                if (output != null)
                {
                    output.Add(contact);
                }
                else
                {
                    _sidesNext[key] = side;
                    if (_contactCount == _contacts.Length)
                        Array.Resize(ref _contacts, Math.Max(1024, _contacts.Length * 2));
                    _contacts[_contactCount++] = contact;
                }
            }
        }
    }

    private void FindHemContacts(float margin)
    {
        _hemContactCount = 0;
        int m = _hemA.Length;
        if (m < 2)
            return;
        var x = Positions;
        float reachSq = margin * margin;
        float samePieceSq = SamePieceRestDistance * SamePieceRestDistance;
        for (int i = 0; i < m; i++)
        {
            int a1 = _hemA[i], b1 = _hemB[i];
            var p1 = x[a1];
            var q1 = x[b1];
            var lo1 = Vector3.Min(p1, q1) - new Vector3(margin);
            var hi1 = Vector3.Max(p1, q1) + new Vector3(margin);
            int piece1 = _hemPiece[i];
            for (int j = i + 1; j < m; j++)
            {
                int a2 = _hemA[j], b2 = _hemB[j];
                if (_invMass[a1] == 0 && _invMass[b1] == 0 && _invMass[a2] == 0 && _invMass[b2] == 0)
                    continue;
                if (piece1 == _hemPiece[j] && Vector2.DistanceSquared(_hemRest[i], _hemRest[j]) < samePieceSq)
                    continue;
                if (_weld[a1] == _weld[a2] || _weld[a1] == _weld[b2] || _weld[b1] == _weld[a2] || _weld[b1] == _weld[b2])
                    continue;
                var p2 = x[a2];
                var q2 = x[b2];
                var lo2 = Vector3.Min(p2, q2);
                var hi2 = Vector3.Max(p2, q2);
                if (hi1.X < lo2.X || lo1.X > hi2.X || hi1.Y < lo2.Y || lo1.Y > hi2.Y || hi1.Z < lo2.Z || lo1.Z > hi2.Z)
                    continue;
                ClosestPointsOnSegments(p1, q1, p2, q2, out _, out _, out var c1, out var c2);
                if (Vector3.DistanceSquared(c1, c2) > reachSq)
                    continue;
                if (_hemContactCount == _hemContacts.Length)
                    Array.Resize(ref _hemContacts, Math.Max(128, _hemContacts.Length * 2));
                _hemContacts[_hemContactCount++] = (i, j);
            }
        }
    }

    private static void ClosestPointsOnSegments(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
        out float s, out float t, out Vector3 c1, out Vector3 c2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);
        if (a <= 1e-12f && e <= 1e-12f)
        {
            s = t = 0f;
            c1 = p1; c2 = p2;
            return;
        }
        if (a <= 1e-12f)
        {
            s = 0f;
            t = Math.Clamp(f / e, 0f, 1f);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-12f)
            {
                t = 0f;
                s = Math.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom > 1e-12f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    /// <summary>Packs the current broadphase contacts into compact blittable structs for GPU upload.</summary>
    private ContactBroadphaseResult PackGpuContacts()
    {
        if (_gpuPtContacts.Length < _contactCount)
            _gpuPtContacts = new GpuPtContact[Math.Max(1024, _contactCount + _contactCount / 2)];
        for (int i = 0; i < _contactCount; i++)
        {
            ref var c = ref _contacts[i];
            int t3 = 3 * c.Triangle;
            _gpuPtContacts[i] = new GpuPtContact
            {
                Particle = c.Particle,
                Ia = _tris[t3],
                Ib = _tris[t3 + 1],
                Ic = _tris[t3 + 2],
                Side = c.Side,
            };
        }

        if (_gpuHemContacts.Length < _hemContactCount)
            _gpuHemContacts = new GpuHemContact[Math.Max(128, _hemContactCount + _hemContactCount / 2)];
        for (int k = 0; k < _hemContactCount; k++)
        {
            var (e1, e2) = _hemContacts[k];
            _gpuHemContacts[k] = new GpuHemContact
            {
                A1 = _hemA[e1],
                B1 = _hemB[e1],
                A2 = _hemA[e2],
                B2 = _hemB[e2],
            };
        }

        return new ContactBroadphaseResult
        {
            PtContacts = _gpuPtContacts,
            PtCount = _contactCount,
            HemContacts = _gpuHemContacts,
            HemCount = _hemContactCount,
        };
    }

    /// <summary>Every substep: pushes particles and triangles apart where they are closer than the fabrics allow.</summary>
    private void SolveContacts()
    {
        var x = Positions;
        for (int i = 0; i < _contactCount; i++)
        {
            ref var contact = ref _contacts[i];
            int p = contact.Particle;
            int ia = _tris[3 * contact.Triangle], ib = _tris[3 * contact.Triangle + 1], ic = _tris[3 * contact.Triangle + 2];
            var normal = Vector3.Cross(x[ib] - x[ia], x[ic] - x[ia]);
            float area = normal.Length();
            if (area < 1e-12f)
                continue;
            normal *= contact.Side / area;

            var q = SdfGrid.ClosestPointOnTriangle(x[p], x[ia], x[ib], x[ic], out float wa, out float wb, out float wc);
            var r = x[p] - q;
            float height = Vector3.Dot(r, normal);
            float separation = 0.5f * (_clothThickness[p] + (_clothThickness[ia] + _clothThickness[ib] + _clothThickness[ic]) / 3f) + ContactGap;
            // Only when the particle is over the triangle itself; beside it, the neighbouring triangle is in charge.
            if ((r - normal * height).LengthSquared() > separation * separation)
                continue;
            // A deep overlap is worked off over several substeps rather than thrown out in one.
            float depth = MathF.Min(separation - height, separation);
            if (depth <= 0)
                continue;

            float mp = _invMass[p], ma = _invMass[ia], mb = _invMass[ib], mc = _invMass[ic];
            float w = mp + wa * wa * ma + wb * wb * mb + wc * wc * mc;
            if (w == 0)
                continue;
            var push = normal * (depth / w);
            x[p] += push * mp;
            x[ia] -= push * (wa * ma);
            x[ib] -= push * (wb * mb);
            x[ic] -= push * (wc * mc);

            // Friction between the two fabrics: cancel the sliding between the particle and the spot it touches,
            // completely if it is small compared with how hard they press together.
            var slide = (x[p] - _previous[p])
                      - ((x[ia] - _previous[ia]) * wa + (x[ib] - _previous[ib]) * wb + (x[ic] - _previous[ic]) * wc);
            slide -= normal * Vector3.Dot(slide, normal);
            float sliding = slide.Length();
            if (sliding < 1e-9f)
                continue;
            float grip = 0.5f * (_friction[p] + (_friction[ia] + _friction[ib] + _friction[ic]) / 3f) * depth;
            var cancel = slide * ((sliding <= grip ? 1f : grip / sliding) / w);
            x[p] -= cancel * mp;
            x[ia] += cancel * (wa * ma);
            x[ib] += cancel * (wb * mb);
            x[ic] += cancel * (wc * mc);
        }

        // Resolve hem edge-edge contacts so free hems keep fabric thickness clearance.
        for (int k = 0; k < _hemContactCount; k++)
        {
            var (e1, e2) = _hemContacts[k];
            int a1 = _hemA[e1], b1 = _hemB[e1], a2 = _hemA[e2], b2 = _hemB[e2];
            ClosestPointsOnSegments(x[a1], x[b1], x[a2], x[b2], out float s, out float t, out var c1, out var c2);
            var diff = c1 - c2;
            float dist = diff.Length();
            float sep = 0.25f * (_clothThickness[a1] + _clothThickness[b1] + _clothThickness[a2] + _clothThickness[b2]);
            if (dist >= sep || dist < 1e-9f)
                continue;
            var normal = diff / dist;
            float depth = MathF.Min(sep - dist, sep * 0.5f);
            float w1a = 1f - s, w1b = s, w2a = 1f - t, w2b = t;
            float m1a = _invMass[a1], m1b = _invMass[b1], m2a = _invMass[a2], m2b = _invMass[b2];
            float w = w1a * w1a * m1a + w1b * w1b * m1b + w2a * w2a * m2a + w2b * w2b * m2b;
            if (w == 0f)
                continue;
            var push = normal * (depth / w);
            x[a1] += push * (w1a * m1a);
            x[b1] += push * (w1b * m1b);
            x[a2] -= push * (w2a * m2a);
            x[b2] -= push * (w2b * m2b);
        }
    }
}
