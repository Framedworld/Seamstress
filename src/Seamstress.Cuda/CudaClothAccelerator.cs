using System.Numerics;
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using Seamstress.Core;
using Seamstress.Core.Simulation;

namespace Seamstress.Cuda;

/// <summary>Blittable 3D float vector for CUDA kernels.</summary>
public struct Float3
{
    public float X, Y, Z;

    public Float3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public Float3(Vector3 v) { X = v.X; Y = v.Y; Z = v.Z; }

    public Vector3 ToVector3() => new(X, Y, Z);
    public float LengthSquared() => X * X + Y * Y + Z * Z;
    public float Length() => XMath.Sqrt(X * X + Y * Y + Z * Z);

    public static Float3 operator +(Float3 a, Float3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Float3 operator -(Float3 a, Float3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Float3 operator *(Float3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static float Dot(Float3 a, Float3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Float3 Cross(Float3 a, Float3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);
}

public readonly struct SdfParams
{
    public readonly int HasSdf;
    public readonly Float3 Origin;
    public readonly float Cell;
    public readonly int Nx, Ny, Nz;

    public SdfParams(SdfGrid? sdf)
    {
        if (sdf == null)
        {
            HasSdf = 0;
            Origin = default;
            Cell = 1f;
            Nx = Ny = Nz = 0;
        }
        else
        {
            HasSdf = 1;
            Origin = new Float3(sdf.Origin);
            Cell = sdf.Cell;
            Nx = sdf.Nx;
            Ny = sdf.Ny;
            Nz = sdf.Nz;
        }
    }
}

/// <summary>Process-wide CUDA context, accelerator, and compiled PTX kernels.</summary>
internal sealed class CudaRuntime
{
    private static readonly object Gate = new();
    private static CudaRuntime? _instance;
    private static bool _probed;

    public Context Context { get; }
    public Accelerator Accelerator { get; }
    public string DeviceName { get; }

    public Action<Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<Float3>,
        ArrayView<float>, ArrayView<float>, ArrayView<int>, ArrayView<int>, ArrayView<float>,
        float, float> IntegrateKernel { get; }

    public Action<Index1D, int, ArrayView<Float3>, ArrayView<float>,
        ArrayView<int>, ArrayView<int>, ArrayView<float>, ArrayView<float>, float> SolveDistanceKernel { get; }

    public Action<Index1D, int, ArrayView<Float3>, ArrayView<float>,
        ArrayView<int>, ArrayView<int>, ArrayView<float>, ArrayView<float>, float> SolveSeamHingeKernel { get; }

    public Action<Index1D, int, ArrayView<Float3>, ArrayView<Float3>, ArrayView<float>,
        ArrayView<int>, ArrayView<int>, ArrayView<int>,
        ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>,
        float, int> SolveSeamKernel { get; }

    public Action<Index1D, ArrayView<Float3>, ArrayView<float>, ArrayView<byte>,
        ArrayView<int>, ArrayView<int>, ArrayView<float>,
        ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>> ApplyPieceShiftKernel { get; }

    public Action<Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<float>,
        ArrayView<float>, ArrayView<float>, ArrayView<byte>,
        ArrayView<float>, SdfParams, int> SolveBodyKernel { get; }

    public Action<Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<Float3>,
        ArrayView<float>, ArrayView<float>, float> UpdateVelocityKernel { get; }

    public Action<Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<float>,
        ArrayView<float>, ArrayView<float>, ArrayView<GpuPtContact>,
        ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>> SolvePointTriangleContactsKernel { get; }

    public Action<Index1D, ArrayView<Float3>, ArrayView<float>, ArrayView<float>,
        ArrayView<GpuHemContact>,
        ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>> SolveHemContactsKernel { get; }

    public Action<Index1D, ArrayView<Float3>, ArrayView<float>,
        ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>> ApplyContactDeltasKernel { get; }

    public Action<Index1D, ArrayView<Float3>, ArrayView<int>, ArrayView<float>, ArrayView<Float3>, Float3> ApplyPinchKernel { get; }

    public Action<Index1D, ArrayView<Float3>, ArrayView<int>> StopParticlesKernel { get; }

    public Action<Index1D, ArrayView<float>, ArrayView<int>, ArrayView<float>> ScatterKernel { get; }

    private CudaRuntime(Context context, Accelerator accelerator)
    {
        Context = context;
        Accelerator = accelerator;
        DeviceName = $"CUDA ({accelerator.Name})";

        IntegrateKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<Float3>,
            ArrayView<float>, ArrayView<float>, ArrayView<int>, ArrayView<int>, ArrayView<float>,
            float, float>(Kernels.Integrate);

        SolveDistanceKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, int, ArrayView<Float3>, ArrayView<float>,
            ArrayView<int>, ArrayView<int>, ArrayView<float>, ArrayView<float>, float>(Kernels.SolveDistanceColor);

        SolveSeamHingeKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, int, ArrayView<Float3>, ArrayView<float>,
            ArrayView<int>, ArrayView<int>, ArrayView<float>, ArrayView<float>, float>(Kernels.SolveSeamHingeColor);

        SolveSeamKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, int, ArrayView<Float3>, ArrayView<Float3>, ArrayView<float>,
            ArrayView<int>, ArrayView<int>, ArrayView<int>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>,
            float, int>(Kernels.SolveSeamColor);

        ApplyPieceShiftKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<Float3>, ArrayView<float>, ArrayView<byte>,
            ArrayView<int>, ArrayView<int>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>(Kernels.ApplyPieceShift);

        SolveBodyKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<byte>,
            ArrayView<float>, SdfParams, int>(Kernels.SolveBody);

        UpdateVelocityKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<Float3>,
            ArrayView<float>, ArrayView<float>, float>(Kernels.UpdateVelocity);

        SolvePointTriangleContactsKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<GpuPtContact>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>(Kernels.SolvePointTriangleContacts);

        SolveHemContactsKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<Float3>, ArrayView<float>, ArrayView<float>,
            ArrayView<GpuHemContact>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>(Kernels.SolveHemContacts);

        ApplyContactDeltasKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<Float3>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>(Kernels.ApplyContactDeltas);

        ApplyPinchKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<Float3>, ArrayView<int>, ArrayView<float>, ArrayView<Float3>, Float3>(Kernels.ApplyPinch);

        StopParticlesKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<Float3>, ArrayView<int>>(Kernels.StopParticles);

        ScatterKernel = accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ArrayView<float>, ArrayView<int>, ArrayView<float>>(Kernels.Scatter);
    }

    public static string? LastError { get; private set; }

    public static CudaRuntime? TryGet()
    {
        lock (Gate)
        {
            if (_probed)
                return _instance;
            _probed = true;
            try
            {
                var ctx = Context.Create(b => b.Cuda().EnableAlgorithms());
                var device = ctx.GetCudaDevice(0);
                var acc = device.CreateCudaAccelerator(ctx);
                _instance = new CudaRuntime(ctx, acc);
                AppLog.Write($"CUDA cloth solver initialized on {acc.Name} ({acc.NumMultiprocessors} SMs, {acc.MemorySize / (1024 * 1024)} MiB VRAM).");
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                AppLog.Write($"CUDA unavailable ({ex.Message}); using CPU cloth solver.");
                _instance = null;
            }
            return _instance;
        }
    }

    private static class Kernels
    {
        private const float MaxSpeed = 8f;
        private const float ContactGap = 0.0022f;
        private const float ContactStep = 0.0015f;   // as ClothSolver.ContactStep
        private const float UntangleStep = 0.0003f;  // as ClothSolver.UntangleStep

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static Float3 ClosestPointOnTriangle(
            Float3 p, Float3 a, Float3 b, Float3 c,
            out float wa, out float wb, out float wc)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            float d1 = Float3.Dot(ab, ap), d2 = Float3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { wa = 1f; wb = 0f; wc = 0f; return a; }

            var bp = p - b;
            float d3 = Float3.Dot(ab, bp), d4 = Float3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { wa = 0f; wb = 1f; wc = 0f; return b; }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float denomEdge = d1 - d3;
                float v = denomEdge > 1e-12f ? d1 / denomEdge : 0f;
                wa = 1f - v; wb = v; wc = 0f;
                return a + ab * v;
            }

            var cp = p - c;
            float d5 = Float3.Dot(ab, cp), d6 = Float3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { wa = 0f; wb = 0f; wc = 1f; return c; }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float denomEdge = d2 - d6;
                float w = denomEdge > 1e-12f ? d2 / denomEdge : 0f;
                wa = 1f - w; wb = 0f; wc = w;
                return a + ac * w;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float denomEdge = (d4 - d3) + (d5 - d6);
                float w = denomEdge > 1e-12f ? (d4 - d3) / denomEdge : 0f;
                wa = 0f; wb = 1f - w; wc = w;
                return b + (c - b) * w;
            }

            float sum = va + vb + vc;
            if (sum <= 1e-12f) { wa = 1f; wb = 0f; wc = 0f; return a; }
            float denom = 1f / sum;
            float bv = vb * denom, bw = vc * denom;
            wa = 1f - bv - bw; wb = bv; wc = bw;
            return a + ab * bv + ac * bw;
        }

        private static void ClosestPointsOnSegments(
            Float3 p1, Float3 q1, Float3 p2, Float3 q2,
            out float s, out float t, out Float3 c1, out Float3 c2)
        {
            var d1 = q1 - p1;
            var d2 = q2 - p2;
            var r = p1 - p2;
            float a = Float3.Dot(d1, d1);
            float e = Float3.Dot(d2, d2);
            float f = Float3.Dot(d2, r);
            if (a <= 1e-12f && e <= 1e-12f)
            {
                s = t = 0f;
                c1 = p1; c2 = p2;
                return;
            }
            if (a <= 1e-12f)
            {
                s = 0f;
                t = Clamp01(f / e);
            }
            else
            {
                float c = Float3.Dot(d1, r);
                if (e <= 1e-12f)
                {
                    t = 0f;
                    s = Clamp01(-c / a);
                }
                else
                {
                    float b = Float3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom > 1e-12f ? Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Clamp01((b - c) / a); }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }

        private static void AccumulateDelta(
            int idx, Float3 delta,
            ArrayView<float> deltaX, ArrayView<float> deltaY, ArrayView<float> deltaZ,
            ArrayView<int> contactHits)
        {
            Atomic.Add(ref deltaX[idx], delta.X);
            Atomic.Add(ref deltaY[idx], delta.Y);
            Atomic.Add(ref deltaZ[idx], delta.Z);
            Atomic.Add(ref contactHits[idx], 1);
        }

        public static void Integrate(
            Index1D i,
            ArrayView<Float3> pos,
            ArrayView<Float3> prev,
            ArrayView<Float3> vel,
            ArrayView<float> invMass,
            ArrayView<float> drag,
            ArrayView<int> pieceOf,
            ArrayView<int> garmentOfPiece,
            ArrayView<float> sewingPerGarment,
            float gravityY,
            float h)
        {
            if (invMass[i] == 0f)
                return;
            float sewing = sewingPerGarment[garmentOfPiece[pieceOf[i]]];
            float damp = XMath.Max(0f, 1f - (drag[i] + 6f * sewing) * h);
            var v = vel[i];
            v.Y += gravityY * ((1f - sewing) * h);
            v = v * damp;
            float speed = v.Length();
            if (speed > MaxSpeed)
                v = v * (MaxSpeed / speed);
            var p = pos[i];
            prev[i] = p;
            pos[i] = p + v * h;
        }

        public static void SolveDistanceColor(
            Index1D idx,
            int offset,
            ArrayView<Float3> pos,
            ArrayView<float> invMass,
            ArrayView<int> ca,
            ArrayView<int> cb,
            ArrayView<float> rest,
            ArrayView<float> compliance,
            float invH2)
        {
            int c = offset + idx;
            int a = ca[c], b = cb[c];
            float wa = invMass[a], wb = invMass[b];
            float w = wa + wb;
            if (w == 0f)
                return;
            var d = pos[b] - pos[a];
            float length = d.Length();
            if (length < 1e-9f)
                return;
            float lambda = (length - rest[c]) / (w + compliance[c] * invH2);
            var corr = d * (lambda / length);
            pos[a] = pos[a] + corr * wa;
            pos[b] = pos[b] - corr * wb;
        }

        public static void SolveSeamHingeColor(
            Index1D idx,
            int offset,
            ArrayView<Float3> pos,
            ArrayView<float> invMass,
            ArrayView<int> ha,
            ArrayView<int> hb,
            ArrayView<float> hrest,
            ArrayView<float> hcompliance,
            float invH2)
        {
            int c = offset + idx;
            int a = ha[c], b = hb[c];
            float wa = invMass[a], wb = invMass[b];
            float w = wa + wb;
            var d = pos[b] - pos[a];
            float length = d.Length();
            float target = hrest[c];
            if (w == 0f || length < 1e-9f || length > target * 1.5f)
                return;
            float lambda = (length - target) / (w + hcompliance[c] * invH2);
            var corr = d * (lambda / length);
            pos[a] = pos[a] + corr * wa;
            pos[b] = pos[b] - corr * wb;
        }

        public static void SolveSeamColor(
            Index1D idx,
            int offset,
            ArrayView<Float3> pos,
            ArrayView<Float3> prev,
            ArrayView<float> invMass,
            ArrayView<int> sa,
            ArrayView<int> sb,
            ArrayView<int> pieceOf,
            ArrayView<float> shiftX,
            ArrayView<float> shiftY,
            ArrayView<float> shiftZ,
            ArrayView<int> stitchCount,
            float closing,
            int assist)
        {
            int c = offset + idx;
            int a = sa[c], b = sb[c];
            float wa = invMass[a], wb = invMass[b];
            float w = wa + wb;
            if (w == 0f)
                return;
            var d = pos[b] - pos[a];
            float length = d.Length();
            if (length < 1e-9f)
                return;
            float prevDist = (prev[b] - prev[a]).Length();
            float target = XMath.Max(0f, prevDist - closing);
            if (length <= target)
                return;
            var corr = d * ((length - target) / (length * w));
            var da = corr * wa;
            var db = corr * wb;
            pos[a] = pos[a] + da;
            pos[b] = pos[b] - db;
            if (assist != 0)
            {
                int pa = pieceOf[a];
                int pb = pieceOf[b];
                Atomic.Add(ref shiftX[pa], da.X);
                Atomic.Add(ref shiftY[pa], da.Y);
                Atomic.Add(ref shiftZ[pa], da.Z);
                Atomic.Add(ref stitchCount[pa], 1);
                Atomic.Add(ref shiftX[pb], -db.X);
                Atomic.Add(ref shiftY[pb], -db.Y);
                Atomic.Add(ref shiftZ[pb], -db.Z);
                Atomic.Add(ref stitchCount[pb], 1);
            }
        }

        public static void ApplyPieceShift(
            Index1D i,
            ArrayView<Float3> pos,
            ArrayView<float> invMass,
            ArrayView<byte> stitched,
            ArrayView<int> pieceOf,
            ArrayView<int> garmentOfPiece,
            ArrayView<float> sewingPerGarment,
            ArrayView<float> shiftX,
            ArrayView<float> shiftY,
            ArrayView<float> shiftZ,
            ArrayView<int> stitchCount)
        {
            int piece = pieceOf[i];
            float sewing = sewingPerGarment[garmentOfPiece[piece]];
            int count = stitchCount[piece];
            if (sewing == 0f || invMass[i] == 0f || stitched[i] != 0 || count == 0)
                return;
            float scale = sewing / count;
            var p = pos[i];
            pos[i] = new Float3(
                p.X + shiftX[piece] * scale,
                p.Y + shiftY[piece] * scale,
                p.Z + shiftZ[piece] * scale);
        }

        public static void SolveBody(
            Index1D i,
            ArrayView<Float3> pos,
            ArrayView<Float3> prev,
            ArrayView<float> invMass,
            ArrayView<float> thickness,
            ArrayView<float> friction,
            ArrayView<byte> openStitch,
            ArrayView<float> sdfData,
            SdfParams sdf,
            int groundPlane)
        {
            if (invMass[i] == 0f)
                return;
            var p = pos[i];
            float thick = thickness[i];

            if (sdf.HasSdf != 0)
            {
                float gx = (p.X - sdf.Origin.X) / sdf.Cell;
                float gy = (p.Y - sdf.Origin.Y) / sdf.Cell;
                float gz = (p.Z - sdf.Origin.Z) / sdf.Cell;
                if (gx >= 0f && gy >= 0f && gz >= 0f && gx < sdf.Nx - 1 && gy < sdf.Ny - 1 && gz < sdf.Nz - 1)
                {
                    int ix = (int)gx, iy = (int)gy, iz = (int)gz;
                    float fx = gx - ix, fy = gy - iy, fz = gz - iz;
                    int sy = sdf.Nx, sz = sdf.Nx * sdf.Ny;
                    int baseIdx = (iz * sdf.Ny + iy) * sdf.Nx + ix;
                    float c000 = sdfData[baseIdx], c100 = sdfData[baseIdx + 1];
                    float c010 = sdfData[baseIdx + sy], c110 = sdfData[baseIdx + sy + 1];
                    float c001 = sdfData[baseIdx + sz], c101 = sdfData[baseIdx + sz + 1];
                    float c011 = sdfData[baseIdx + sz + sy], c111 = sdfData[baseIdx + sz + sy + 1];

                    float x00 = c000 + (c100 - c000) * fx;
                    float x10 = c010 + (c110 - c010) * fx;
                    float x01 = c001 + (c101 - c001) * fx;
                    float x11 = c011 + (c111 - c011) * fx;
                    float y0 = x00 + (x10 - x00) * fy;
                    float y1 = x01 + (x11 - x01) * fy;
                    float dist = y0 + (y1 - y0) * fz;

                    float depth = thick - dist;
                    if (depth > 0f)
                    {
                        float dgx = ((c100 - c000) * (1f - fy) + (c110 - c010) * fy) * (1f - fz)
                                  + ((c101 - c001) * (1f - fy) + (c111 - c011) * fy) * fz;
                        float dgy = (x10 - x00) * (1f - fz) + (x11 - x01) * fz;
                        float dgz = y1 - y0;
                        var grad = new Float3(dgx, dgy, dgz);
                        float gl = grad.Length();
                        var normal = gl > 1e-9f ? grad * (1f / gl) : new Float3(0f, 1f, 0f);
                        p = p + normal * depth;
                        if (openStitch[i] == 0)
                        {
                            var move = p - prev[i];
                            var tangent = move - normal * Float3.Dot(move, normal);
                            float slide = tangent.Length();
                            if (slide >= 1e-9f)
                            {
                                float grip = friction[i] * depth;
                                p = p - (slide <= grip ? tangent : tangent * (grip / slide));
                            }
                        }
                    }
                }
            }

            if (groundPlane != 0 && p.Y < thick)
            {
                float depth = thick - p.Y;
                p.Y = thick;
                if (openStitch[i] == 0)
                {
                    var move = p - prev[i];
                    var tangent = new Float3(move.X, 0f, move.Z);
                    float slide = tangent.Length();
                    if (slide >= 1e-9f)
                    {
                        float grip = friction[i] * depth;
                        p = p - (slide <= grip ? tangent : tangent * (grip / slide));
                    }
                }
            }

            pos[i] = p;
        }

        public static void UpdateVelocity(
            Index1D i,
            ArrayView<Float3> pos,
            ArrayView<Float3> prev,
            ArrayView<Float3> vel,
            ArrayView<float> invMass,
            ArrayView<float> speedSq,
            float invH)
        {
            if (invMass[i] == 0f)
            {
                speedSq[i] = 0f;
                return;
            }
            var v = (pos[i] - prev[i]) * invH;
            vel[i] = v;
            speedSq[i] = v.LengthSquared();
        }

        public static void SolvePointTriangleContacts(
            Index1D k,
            ArrayView<Float3> pos,
            ArrayView<Float3> prev,
            ArrayView<float> invMass,
            ArrayView<float> clothThick,
            ArrayView<float> friction,
            ArrayView<GpuPtContact> contacts,
            ArrayView<float> deltaX,
            ArrayView<float> deltaY,
            ArrayView<float> deltaZ,
            ArrayView<int> contactHits)
        {
            var c = contacts[k];
            int p = c.Particle;
            int ia = c.Ia, ib = c.Ib, ic = c.Ic;
            var xp = pos[p];
            var xa = pos[ia];
            var xb = pos[ib];
            var xc = pos[ic];

            var normal = Float3.Cross(xb - xa, xc - xa);
            float area = normal.Length();
            if (area < 1e-12f)
                return;
            normal = normal * (c.Side / area);

            var q = ClosestPointOnTriangle(xp, xa, xb, xc, out float wa, out float wb, out float wc);
            var r = xp - q;
            float height = Float3.Dot(r, normal);
            float separation = 0.5f * (clothThick[p] + (clothThick[ia] + clothThick[ib] + clothThick[ic]) / 3f) + ContactGap;
            var beside = r - normal * height;
            if (beside.LengthSquared() > separation * separation)
                return;
            // Worked off over several substeps, as in ClothSolver.SolveContacts.
            float depth = XMath.Min(separation - height, height < 0f ? UntangleStep : ContactStep);
            if (depth <= 0f)
                return;

            float mp = invMass[p], ma = invMass[ia], mb = invMass[ib], mc = invMass[ic];
            float w = mp + wa * wa * ma + wb * wb * mb + wc * wc * mc;
            if (w == 0f)
                return;

            var push = normal * (depth / w);
            var corr = push;

            // Coulomb fabric-on-fabric tangent friction (normal push projects out of tangent slide).
            var slide = (xp - prev[p])
                      - ((xa - prev[ia]) * wa + (xb - prev[ib]) * wb + (xc - prev[ic]) * wc);
            slide = slide - normal * Float3.Dot(slide, normal);
            float sliding = slide.Length();
            if (sliding >= 1e-9f)
            {
                float grip = 0.5f * (friction[p] + (friction[ia] + friction[ib] + friction[ic]) / 3f) * depth;
                var cancel = slide * ((sliding <= grip ? 1f : grip / sliding) / w);
                corr = push - cancel;
            }

            if (mp > 0f)
                AccumulateDelta(p, corr * mp, deltaX, deltaY, deltaZ, contactHits);
            if (wa > 0f && ma > 0f)
                AccumulateDelta(ia, corr * (-wa * ma), deltaX, deltaY, deltaZ, contactHits);
            if (wb > 0f && mb > 0f)
                AccumulateDelta(ib, corr * (-wb * mb), deltaX, deltaY, deltaZ, contactHits);
            if (wc > 0f && mc > 0f)
                AccumulateDelta(ic, corr * (-wc * mc), deltaX, deltaY, deltaZ, contactHits);
        }

        public static void SolveHemContacts(
            Index1D k,
            ArrayView<Float3> pos,
            ArrayView<float> invMass,
            ArrayView<float> clothThick,
            ArrayView<GpuHemContact> hemContacts,
            ArrayView<float> deltaX,
            ArrayView<float> deltaY,
            ArrayView<float> deltaZ,
            ArrayView<int> contactHits)
        {
            var c = hemContacts[k];
            int a1 = c.A1, b1 = c.B1, a2 = c.A2, b2 = c.B2;
            ClosestPointsOnSegments(pos[a1], pos[b1], pos[a2], pos[b2], out float s, out float t, out var c1, out var c2);
            var diff = c1 - c2;
            float dist = diff.Length();
            float sep = 0.25f * (clothThick[a1] + clothThick[b1] + clothThick[a2] + clothThick[b2]);
            if (dist >= sep || dist < 1e-9f)
                return;
            var normal = diff * (1f / dist);
            float depth = XMath.Min(sep - dist, sep * 0.5f);
            float w1a = 1f - s, w1b = s, w2a = 1f - t, w2b = t;
            float m1a = invMass[a1], m1b = invMass[b1], m2a = invMass[a2], m2b = invMass[b2];
            float w = w1a * w1a * m1a + w1b * w1b * m1b + w2a * w2a * m2a + w2b * w2b * m2b;
            if (w == 0f)
                return;
            var push = normal * (depth / w);
            if (w1a > 0f && m1a > 0f)
                AccumulateDelta(a1, push * (w1a * m1a), deltaX, deltaY, deltaZ, contactHits);
            if (w1b > 0f && m1b > 0f)
                AccumulateDelta(b1, push * (w1b * m1b), deltaX, deltaY, deltaZ, contactHits);
            if (w2a > 0f && m2a > 0f)
                AccumulateDelta(a2, push * (-w2a * m2a), deltaX, deltaY, deltaZ, contactHits);
            if (w2b > 0f && m2b > 0f)
                AccumulateDelta(b2, push * (-w2b * m2b), deltaX, deltaY, deltaZ, contactHits);
        }

        /// <summary>Draws the held cloth towards the hand, as ClothSolver.ApplyPinch does.</summary>
        public static void ApplyPinch(
            Index1D k,
            ArrayView<Float3> pos,
            ArrayView<int> pinched,
            ArrayView<float> strength,
            ArrayView<Float3> offsets,
            Float3 hand)
        {
            int i = pinched[k];
            var x = pos[i];
            pos[i] = x + (hand + offsets[k] - x) * strength[k];
        }

        /// <summary>Writes values at the listed indices.</summary>
        public static void Scatter(Index1D k, ArrayView<float> target, ArrayView<int> indices, ArrayView<float> values)
        {
            target[indices[k]] = values[k];
        }

        /// <summary>Stops the listed particles (the cloth a pinch lets go of).</summary>
        public static void StopParticles(Index1D k, ArrayView<Float3> vel, ArrayView<int> particles)
        {
            vel[particles[k]] = new Float3(0f, 0f, 0f);
        }

        public static void ApplyContactDeltas(
            Index1D i,
            ArrayView<Float3> pos,
            ArrayView<float> invMass,
            ArrayView<float> deltaX,
            ArrayView<float> deltaY,
            ArrayView<float> deltaZ,
            ArrayView<int> contactHits)
        {
            int hits = contactHits[i];
            if (hits <= 0)
                return;
            if (invMass[i] != 0f)
            {
                float inv = 1f / hits;
                var p = pos[i];
                pos[i] = new Float3(
                    p.X + deltaX[i] * inv,
                    p.Y + deltaY[i] * inv,
                    p.Z + deltaZ[i] * inv);
            }
            deltaX[i] = 0f;
            deltaY[i] = 0f;
            deltaZ[i] = 0f;
            contactHits[i] = 0;
        }
    }
}

/// <summary>CUDA-accelerated XPBD cloth solver backend using ILGPU.</summary>
public sealed class CudaClothAccelerator : IClothAccelerator
{
    private const float SeamClosingSpeed = 0.8f;

    private readonly CudaRuntime _rt;
    private readonly int _n;
    private readonly int _pieceCount;

    private readonly MemoryBuffer1D<Float3, Stride1D.Dense> _pos;
    private readonly MemoryBuffer1D<Float3, Stride1D.Dense> _prev;
    private readonly MemoryBuffer1D<Float3, Stride1D.Dense> _vel;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _invMass;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _thickness;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _clothThickness;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _friction;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _drag;
    private readonly MemoryBuffer1D<byte, Stride1D.Dense> _stitched;
    private readonly MemoryBuffer1D<byte, Stride1D.Dense> _openStitch;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _pieceOf;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _garmentOfPiece;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _sewing;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _speedSq;

    private readonly MemoryBuffer1D<float, Stride1D.Dense> _shiftX;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _shiftY;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _shiftZ;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _stitchCount;

    private readonly MemoryBuffer1D<float, Stride1D.Dense> _contactDeltaX;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _contactDeltaY;
    private readonly MemoryBuffer1D<float, Stride1D.Dense> _contactDeltaZ;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _contactHits;
    private MemoryBuffer1D<GpuPtContact, Stride1D.Dense>? _ptContacts;
    private int _ptCapacity;
    private int _ptCount;
    private MemoryBuffer1D<GpuHemContact, Stride1D.Dense>? _hemContacts;
    private int _hemCapacity;
    private int _hemCount;
    private MemoryBuffer1D<int, Stride1D.Dense>? _pinched;
    private MemoryBuffer1D<float, Stride1D.Dense>? _pinchStrength;
    private MemoryBuffer1D<Float3, Stride1D.Dense>? _pinchOffset;
    private int _pinchCount;
    private readonly float[] _hostInvMass;
    private MemoryBuffer1D<int, Stride1D.Dense>? _pinchCore;
    private MemoryBuffer1D<float, Stride1D.Dense>? _pinchCoreMass;

    private readonly ColoredConstraints _distColors;
    private readonly MemoryBuffer1D<int, Stride1D.Dense>? _ca, _cb;
    private readonly MemoryBuffer1D<float, Stride1D.Dense>? _rest, _compliance;

    private readonly ColoredConstraints _hingeColors;
    private readonly MemoryBuffer1D<int, Stride1D.Dense>? _ha, _hb;
    private readonly MemoryBuffer1D<float, Stride1D.Dense>? _hrest, _hcompliance;

    private readonly ColoredConstraints _seamColors;
    private readonly MemoryBuffer1D<int, Stride1D.Dense>? _sa, _sb;

    private readonly MemoryBuffer1D<float, Stride1D.Dense> _sdfData;
    private readonly SdfParams _sdfParams;

    private readonly Float3[] _hostPos;
    private readonly float[] _hostSpeedSq;
    private Vector3[]? _syncPos; // CPU copy used only on broadphase refresh substeps
    private bool _hostPosValid;

    public string Name => _rt.DeviceName;

    private CudaClothAccelerator(CudaRuntime rt, ClothSolverData data)
    {
        _rt = rt;
        _n = data.Positions.Length;
        _pieceCount = Math.Max(1, data.PieceCount);
        var acc = rt.Accelerator;

        _hostPos = new Float3[_n];
        for (int i = 0; i < _n; i++)
            _hostPos[i] = new Float3(data.Positions[i]);
        _hostSpeedSq = new float[_n];

        _pos = acc.Allocate1D(_hostPos);
        _prev = acc.Allocate1D(_hostPos);
        _vel = acc.Allocate1D<Float3>(_n);
        _vel.MemSetToZero();

        _invMass = acc.Allocate1D(data.InvMass);
        _hostInvMass = (float[])data.InvMass.Clone();
        _thickness = acc.Allocate1D(data.Thickness);
        _clothThickness = acc.Allocate1D(data.ClothThickness);
        _friction = acc.Allocate1D(data.Friction);
        _drag = acc.Allocate1D(data.Drag);

        var stitchedBytes = new byte[_n];
        for (int i = 0; i < _n; i++)
            stitchedBytes[i] = data.Stitched[i] ? (byte)1 : (byte)0;
        _stitched = acc.Allocate1D(stitchedBytes);
        _openStitch = acc.Allocate1D<byte>(_n);
        _pieceOf = acc.Allocate1D(data.PieceOf);
        _garmentOfPiece = acc.Allocate1D(data.GarmentOfPiece.Length > 0 ? data.GarmentOfPiece : new[] { 0 });
        _sewing = acc.Allocate1D<float>(Math.Max(1, data.GarmentCount));
        _speedSq = acc.Allocate1D<float>(_n);

        _shiftX = acc.Allocate1D<float>(_pieceCount);
        _shiftY = acc.Allocate1D<float>(_pieceCount);
        _shiftZ = acc.Allocate1D<float>(_pieceCount);
        _stitchCount = acc.Allocate1D<int>(_pieceCount);

        _contactDeltaX = acc.Allocate1D<float>(_n);
        _contactDeltaY = acc.Allocate1D<float>(_n);
        _contactDeltaZ = acc.Allocate1D<float>(_n);
        _contactHits = acc.Allocate1D<int>(_n);
        _contactDeltaX.MemSetToZero();
        _contactDeltaY.MemSetToZero();
        _contactDeltaZ.MemSetToZero();
        _contactHits.MemSetToZero();
        _hostPosValid = true;

        _distColors = ColoredConstraints.Build(_n, data.Ca, data.Cb, data.Rest, data.Compliance);
        if (_distColors.A.Length > 0)
        {
            _ca = acc.Allocate1D(_distColors.A);
            _cb = acc.Allocate1D(_distColors.B);
            _rest = acc.Allocate1D(_distColors.Rest);
            _compliance = acc.Allocate1D(_distColors.Compliance);
        }

        _hingeColors = ColoredConstraints.Build(_n, data.Ha, data.Hb, data.HRest, data.HCompliance);
        if (_hingeColors.A.Length > 0)
        {
            _ha = acc.Allocate1D(_hingeColors.A);
            _hb = acc.Allocate1D(_hingeColors.B);
            _hrest = acc.Allocate1D(_hingeColors.Rest);
            _hcompliance = acc.Allocate1D(_hingeColors.Compliance);
        }

        _seamColors = ColoredConstraints.Build(_n, data.Sa, data.Sb);
        if (_seamColors.A.Length > 0)
        {
            _sa = acc.Allocate1D(_seamColors.A);
            _sb = acc.Allocate1D(_seamColors.B);
        }

        _sdfParams = new SdfParams(data.Collider);
        _sdfData = data.Collider != null
            ? acc.Allocate1D(data.Collider.RawValues)
            : acc.Allocate1D(new float[1]);
    }

    public static IClothAccelerator? TryCreate(ClothSolverData data)
    {
        var rt = CudaRuntime.TryGet();
        if (rt == null || data.Positions.Length == 0)
            return null;
        return new CudaClothAccelerator(rt, data);
    }

    public void UploadState(Vector3[] positions, Vector3[] velocities)
    {
        Upload(_pos, positions);
        Upload(_vel, velocities);
        _hostPosValid = false;
    }

    public void SetPinch(int[] particles, float[] strength, Vector3[] offsets, float coreStrength)
    {
        if (_pinched != null && _pinchCount > 0)
            _rt.StopParticlesKernel(_pinchCount, _vel.View, _pinched.View);
        // The middle of the patch that was held gets its mass back; the middle of the new one is held rigidly.
        if (_pinchCore != null && _pinchCoreMass != null)
            _rt.ScatterKernel((int)_pinchCore.Length, _invMass.View, _pinchCore.View, _pinchCoreMass.View);
        // The kernels above must finish with the old buffers before they are freed.
        _rt.Accelerator.Synchronize();
        _pinchCore?.Dispose();
        _pinchCoreMass?.Dispose();
        _pinchCore = null;
        _pinchCoreMass = null;
        var core = Enumerable.Range(0, particles.Length).Where(k => strength[k] >= coreStrength).Select(k => particles[k]).ToArray();
        if (core.Length > 0)
        {
            _pinchCore = _rt.Accelerator.Allocate1D(core);
            _pinchCoreMass = _rt.Accelerator.Allocate1D(core.Select(i => _hostInvMass[i]).ToArray());
            using var zero = _rt.Accelerator.Allocate1D(new float[core.Length]);
            _rt.ScatterKernel(core.Length, _invMass.View, _pinchCore.View, zero.View);
            _rt.Accelerator.Synchronize();
        }
        _pinched?.Dispose();
        _pinchStrength?.Dispose();
        _pinchOffset?.Dispose();
        _pinched = null;
        _pinchStrength = null;
        _pinchOffset = null;
        _pinchCount = particles.Length;
        if (_pinchCount == 0)
            return;
        var acc = _rt.Accelerator;
        _pinched = acc.Allocate1D(particles);
        _pinchStrength = acc.Allocate1D(strength);
        _pinchOffset = acc.Allocate1D(offsets.Select(o => new Float3(o)).ToArray());
    }

    public void DownloadPositions(Vector3[] positions)
    {
        Download(_pos, positions);
        _hostPosValid = true;
    }

    public void DownloadVelocities(Vector3[] velocities)
    {
        Download(_vel, velocities);
        _hostPosValid = false;
    }

    private void Upload(MemoryBuffer1D<Float3, Stride1D.Dense> buffer, Vector3[] source)
    {
        for (int i = 0; i < _n; i++)
            _hostPos[i] = new Float3(source[i]);
        buffer.CopyFromCPU(_hostPos);
    }

    private void Download(MemoryBuffer1D<Float3, Stride1D.Dense> buffer, Vector3[] target)
    {
        buffer.CopyToCPU(_hostPos);
        for (int i = 0; i < _n; i++)
            target[i] = _hostPos[i].ToVector3();
    }

    public float StepFrame(in ClothStepParams p, Func<Vector3[], ContactBroadphaseResult>? onBroadphase = null)
    {
        if (p.Sewing.Length > 0)
            _sewing.CopyFromCPU(p.Sewing);
        _openStitch.CopyFromCPU(p.OpenStitch);

        float h = p.SubstepDt;
        float invH = 1f / h;
        float invH2 = invH * invH;
        float closing = SeamClosingSpeed * h;
        int ground = p.GroundPlane ? 1 : 0;

        bool gpuContacts = p.SelfCollision && onBroadphase != null;
        if (gpuContacts)
            _syncPos ??= new Vector3[_n];

        for (int step = 0; step < p.Substeps; step++)
        {
            if (gpuContacts && step % p.SearchEvery == 0)
            {
                if (_hostPosValid)
                {
                    for (int i = 0; i < _n; i++)
                        _syncPos![i] = _hostPos[i].ToVector3();
                }
                else
                {
                    _rt.Accelerator.Synchronize();
                    Download(_pos, _syncPos!);
                }
                var result = onBroadphase!(_syncPos!);
                UploadContacts(result);
            }

            _hostPosValid = false;

            _rt.IntegrateKernel(_n, _pos.View, _prev.View, _vel.View, _invMass.View, _drag.View,
                _pieceOf.View, _garmentOfPiece.View, _sewing.View, p.GravityY, h);

            if (_pinchCount > 0 && p.PinchHands is { } hands && step < hands.Length)
                _rt.ApplyPinchKernel(_pinchCount, _pos.View, _pinched!.View, _pinchStrength!.View, _pinchOffset!.View,
                    new Float3(hands[step]));

            SolveSeamsGpu(closing, assist: true);
            SolveDistancesGpu(invH2);
            SolveSeamHingesGpu(invH2);
            SolveSeamsGpu(closing, assist: false);

            if (gpuContacts)
                SolveContactsGpu();

            _rt.SolveBodyKernel(_n, _pos.View, _prev.View, _invMass.View, _thickness.View, _friction.View,
                _openStitch.View, _sdfData.View, _sdfParams, ground);

            _rt.UpdateVelocityKernel(_n, _pos.View, _prev.View, _vel.View, _invMass.View, _speedSq.View, invH);
        }

        _rt.Accelerator.Synchronize();
        _speedSq.CopyToCPU(_hostSpeedSq);
        float peakSq = 0f;
        for (int i = 0; i < _n; i++)
        {
            if (_hostSpeedSq[i] > peakSq)
                peakSq = _hostSpeedSq[i];
        }
        return MathF.Sqrt(peakSq);
    }

    private void UploadContacts(in ContactBroadphaseResult result)
    {
        _ptCount = result.PtCount;
        if (_ptCount > 0)
        {
            if (_ptContacts == null || _ptCapacity < _ptCount)
            {
                _ptContacts?.Dispose();
                _ptCapacity = Math.Max(1024, _ptCount + _ptCount / 2);
                _ptContacts = _rt.Accelerator.Allocate1D<GpuPtContact>(_ptCapacity);
            }
            _ptContacts.View.SubView(0, _ptCount).CopyFromCPU(ref result.PtContacts[0], _ptCount);
        }

        _hemCount = result.HemCount;
        if (_hemCount > 0)
        {
            if (_hemContacts == null || _hemCapacity < _hemCount)
            {
                _hemContacts?.Dispose();
                _hemCapacity = Math.Max(128, _hemCount + _hemCount / 2);
                _hemContacts = _rt.Accelerator.Allocate1D<GpuHemContact>(_hemCapacity);
            }
            _hemContacts.View.SubView(0, _hemCount).CopyFromCPU(ref result.HemContacts[0], _hemCount);
        }
    }

    private void SolveContactsGpu()
    {
        if (_ptCount == 0 && _hemCount == 0)
            return;

        if (_ptCount > 0 && _ptContacts != null)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                _rt.SolvePointTriangleContactsKernel(_ptCount, _pos.View, _prev.View, _invMass.View,
                    _clothThickness.View, _friction.View, _ptContacts.View,
                    _contactDeltaX.View, _contactDeltaY.View, _contactDeltaZ.View, _contactHits.View);
                _rt.ApplyContactDeltasKernel(_n, _pos.View, _invMass.View,
                    _contactDeltaX.View, _contactDeltaY.View, _contactDeltaZ.View, _contactHits.View);
            }
        }

        if (_hemCount > 0 && _hemContacts != null)
        {
            _rt.SolveHemContactsKernel(_hemCount, _pos.View, _invMass.View,
                _clothThickness.View, _hemContacts.View,
                _contactDeltaX.View, _contactDeltaY.View, _contactDeltaZ.View, _contactHits.View);
            _rt.ApplyContactDeltasKernel(_n, _pos.View, _invMass.View,
                _contactDeltaX.View, _contactDeltaY.View, _contactDeltaZ.View, _contactHits.View);
        }
    }

    private void SolveDistancesGpu(float invH2)
    {
        if (_ca == null || _cb == null || _rest == null || _compliance == null)
            return;
        var offsets = _distColors.ColorOffsets;
        for (int c = 0; c < _distColors.ColorCount; c++)
        {
            int start = offsets[c];
            int count = offsets[c + 1] - start;
            if (count > 0)
                _rt.SolveDistanceKernel(count, start, _pos.View, _invMass.View, _ca.View, _cb.View, _rest.View, _compliance.View, invH2);
        }
    }

    private void SolveSeamHingesGpu(float invH2)
    {
        if (_ha == null || _hb == null || _hrest == null || _hcompliance == null)
            return;
        var offsets = _hingeColors.ColorOffsets;
        for (int c = 0; c < _hingeColors.ColorCount; c++)
        {
            int start = offsets[c];
            int count = offsets[c + 1] - start;
            if (count > 0)
                _rt.SolveSeamHingeKernel(count, start, _pos.View, _invMass.View, _ha.View, _hb.View, _hrest.View, _hcompliance.View, invH2);
        }
    }

    private void SolveSeamsGpu(float closing, bool assist)
    {
        if (_sa == null || _sb == null)
            return;
        if (assist)
        {
            _shiftX.MemSetToZero();
            _shiftY.MemSetToZero();
            _shiftZ.MemSetToZero();
            _stitchCount.MemSetToZero();
        }
        var offsets = _seamColors.ColorOffsets;
        int assistFlag = assist ? 1 : 0;
        for (int c = 0; c < _seamColors.ColorCount; c++)
        {
            int start = offsets[c];
            int count = offsets[c + 1] - start;
            if (count > 0)
                _rt.SolveSeamKernel(count, start, _pos.View, _prev.View, _invMass.View, _sa.View, _sb.View,
                    _pieceOf.View, _shiftX.View, _shiftY.View, _shiftZ.View, _stitchCount.View, closing, assistFlag);
        }
        if (assist)
        {
            _rt.ApplyPieceShiftKernel(_n, _pos.View, _invMass.View, _stitched.View, _pieceOf.View,
                _garmentOfPiece.View, _sewing.View, _shiftX.View, _shiftY.View, _shiftZ.View, _stitchCount.View);
        }
    }

    public void Dispose()
    {
        _pos.Dispose();
        _prev.Dispose();
        _vel.Dispose();
        _invMass.Dispose();
        _thickness.Dispose();
        _clothThickness.Dispose();
        _friction.Dispose();
        _drag.Dispose();
        _stitched.Dispose();
        _openStitch.Dispose();
        _pieceOf.Dispose();
        _garmentOfPiece.Dispose();
        _sewing.Dispose();
        _speedSq.Dispose();
        _shiftX.Dispose();
        _shiftY.Dispose();
        _shiftZ.Dispose();
        _stitchCount.Dispose();
        _contactDeltaX.Dispose();
        _contactDeltaY.Dispose();
        _contactDeltaZ.Dispose();
        _contactHits.Dispose();
        _ptContacts?.Dispose();
        _hemContacts?.Dispose();
        _pinched?.Dispose();
        _pinchStrength?.Dispose();
        _pinchOffset?.Dispose();
        _pinchCore?.Dispose();
        _pinchCoreMass?.Dispose();
        _ca?.Dispose();
        _cb?.Dispose();
        _rest?.Dispose();
        _compliance?.Dispose();
        _ha?.Dispose();
        _hb?.Dispose();
        _hrest?.Dispose();
        _hcompliance?.Dispose();
        _sa?.Dispose();
        _sb?.Dispose();
        _sdfData.Dispose();
    }
}

/// <summary>Registers the CUDA cloth backend with <see cref="ClothAcceleration"/>.</summary>
public static class CudaClothBackend
{
    public static string? LastError => CudaRuntime.LastError;

    /// <summary>Attempts to initialize CUDA and register it as the default cloth solver accelerator.</summary>
    public static bool Register()
    {
        var rt = CudaRuntime.TryGet();
        if (rt == null)
            return false;
        ClothAcceleration.Factory = CudaClothAccelerator.TryCreate;
        ClothAcceleration.RegisteredDeviceName = rt.DeviceName;
        return true;
    }
}
