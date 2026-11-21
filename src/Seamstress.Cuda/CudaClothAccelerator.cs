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
    private Vector3[]? _syncPos, _syncPrev; // CPU copies for the self-collision callback, made on first use

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
        _thickness = acc.Allocate1D(data.Thickness);
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
    }

    public void DownloadPositions(Vector3[] positions) => Download(_pos, positions);

    public void DownloadVelocities(Vector3[] velocities) => Download(_vel, velocities);

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

    public float StepFrame(in ClothStepParams p, Action<Vector3[], Vector3[], bool>? onSelfCollisionSubstep = null)
    {
        if (p.Sewing.Length > 0)
            _sewing.CopyFromCPU(p.Sewing);
        _openStitch.CopyFromCPU(p.OpenStitch);

        float h = p.SubstepDt;
        float invH = 1f / h;
        float invH2 = invH * invH;
        float closing = SeamClosingSpeed * h;
        int ground = p.GroundPlane ? 1 : 0;

        bool cpuContacts = p.SelfCollision && onSelfCollisionSubstep != null;
        if (cpuContacts)
        {
            _syncPos ??= new Vector3[_n];
            _syncPrev ??= new Vector3[_n];
        }

        for (int step = 0; step < p.Substeps; step++)
        {
            _rt.IntegrateKernel(_n, _pos.View, _prev.View, _vel.View, _invMass.View, _drag.View,
                _pieceOf.View, _garmentOfPiece.View, _sewing.View, p.GravityY, h);

            SolveSeamsGpu(closing, assist: true);
            SolveDistancesGpu(invH2);
            SolveSeamHingesGpu(invH2);
            SolveSeamsGpu(closing, assist: false);

            if (cpuContacts)
            {
                // Cloth-on-cloth contacts are still solved on the CPU. Friction there is measured against where
                // each particle was at the start of this substep, so those positions go along as well.
                _rt.Accelerator.Synchronize();
                Download(_pos, _syncPos!);
                Download(_prev, _syncPrev!);
                onSelfCollisionSubstep!(_syncPos!, _syncPrev!, step % p.SearchEvery == 0);
                Upload(_pos, _syncPos!);
            }

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
