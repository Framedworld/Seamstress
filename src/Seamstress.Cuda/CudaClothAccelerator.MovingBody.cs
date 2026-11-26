using System.Numerics;
using System.Runtime.CompilerServices;
using ILGPU;
using ILGPU.Runtime;
using Seamstress.Core.Animation;
using Seamstress.Core.Simulation;

namespace Seamstress.Cuda;

/// <summary>Per-substep settings of the moving-body collision kernel.</summary>
public readonly struct MovingBodyParams
{
    public readonly SdfParams Sdf;
    /// <summary>Where in the frame this substep ends: 0 is the previous frame's field, 1 the current one.</summary>
    public readonly float Blend;
    /// <summary>Substep length in seconds, to turn the body's velocity into its movement this substep.</summary>
    public readonly float H;
    public readonly int Ground;

    public MovingBodyParams(SdfParams sdf, float blend, float h, int ground)
    {
        Sdf = sdf;
        Blend = blend;
        H = h;
        Ground = ground;
    }
}

internal static class MovingBodyKernels
{
    /// <summary>
    /// Body collision against a moving body: the field is blended between the previous and the current frame, and
    /// friction works on the cloth's movement relative to the skin, so cloth resting on a limb moves with it.
    /// </summary>
    public static void SolveMovingBody(
        Index1D i,
        ArrayView<Float3> pos,
        ArrayView<Float3> prev,
        ArrayView<float> invMass,
        ArrayView<float> thickness,
        ArrayView<float> friction,
        ArrayView<byte> openStitch,
        ArrayView<float> sdfPrev,
        ArrayView<float> sdfNext,
        ArrayView<Float3> velocity,
        MovingBodyParams m)
    {
        if (invMass[i] == 0f)
            return;
        var p = pos[i];
        float thick = thickness[i];
        var sdf = m.Sdf;
        float gx = (p.X - sdf.Origin.X) / sdf.Cell;
        float gy = (p.Y - sdf.Origin.Y) / sdf.Cell;
        float gz = (p.Z - sdf.Origin.Z) / sdf.Cell;
        if (sdf.HasSdf != 0 && gx >= 0f && gy >= 0f && gz >= 0f && gx < sdf.Nx - 1 && gy < sdf.Ny - 1 && gz < sdf.Nz - 1)
        {
            int ix = (int)gx, iy = (int)gy, iz = (int)gz;
            float fx = gx - ix, fy = gy - iy, fz = gz - iz;
            int sy = sdf.Nx, sz = sdf.Nx * sdf.Ny;
            int b = (iz * sdf.Ny + iy) * sdf.Nx + ix;
            float t = m.Blend;
            float c000 = sdfPrev[b] + (sdfNext[b] - sdfPrev[b]) * t;
            float c100 = sdfPrev[b + 1] + (sdfNext[b + 1] - sdfPrev[b + 1]) * t;
            float c010 = sdfPrev[b + sy] + (sdfNext[b + sy] - sdfPrev[b + sy]) * t;
            float c110 = sdfPrev[b + sy + 1] + (sdfNext[b + sy + 1] - sdfPrev[b + sy + 1]) * t;
            float c001 = sdfPrev[b + sz] + (sdfNext[b + sz] - sdfPrev[b + sz]) * t;
            float c101 = sdfPrev[b + sz + 1] + (sdfNext[b + sz + 1] - sdfPrev[b + sz + 1]) * t;
            float c011 = sdfPrev[b + sz + sy] + (sdfNext[b + sz + sy] - sdfPrev[b + sz + sy]) * t;
            float c111 = sdfPrev[b + sz + sy + 1] + (sdfNext[b + sz + sy + 1] - sdfPrev[b + sz + sy + 1]) * t;

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
                    var v00 = Lerp(velocity[b], velocity[b + 1], fx);
                    var v10 = Lerp(velocity[b + sy], velocity[b + sy + 1], fx);
                    var v01 = Lerp(velocity[b + sz], velocity[b + sz + 1], fx);
                    var v11 = Lerp(velocity[b + sz + sy], velocity[b + sz + sy + 1], fx);
                    var body = Lerp(Lerp(v00, v10, fy), Lerp(v01, v11, fy), fz);
                    var move = p - prev[i] - body * m.H;
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

        if (m.Ground != 0 && p.Y < thick)
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

    private static Float3 Lerp(Float3 a, Float3 b, float t) => a + (b - a) * t;
}

public sealed partial class CudaClothAccelerator : IMovingBodyAccelerator
{
    private static Action<Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ArrayView<byte>, ArrayView<float>, ArrayView<float>, ArrayView<Float3>, MovingBodyParams>? _movingBodyKernel;
    private static Accelerator? _movingBodyKernelOwner;

    private MemoryBuffer1D<float, Stride1D.Dense>? _bodyPrev;
    private MemoryBuffer1D<Float3, Stride1D.Dense>? _bodyVelocity;
    private long _bodyVersion = -1;

    public void SetMovingBody(MovingBodyField field)
    {
        if (field.CellCount != _sdfData.Length)
            throw new InvalidOperationException("The moving body's grid is not the one this solver was created with.");
        var acc = _rt.Accelerator;
        int count = field.CellCount;
        bool full = _bodyPrev == null || field.Version != _bodyVersion + 1;
        _bodyPrev ??= acc.Allocate1D<float>(count);
        _bodyVelocity ??= acc.Allocate1D<Float3>(count);
        if (full)
        {
            _bodyPrev.View.CopyFromCPU(ref field.Previous[0], count);
            _sdfData.View.CopyFromCPU(ref field.Distances[0], count);
            _bodyVelocity.View.CopyFromCPU(ref Unsafe.As<Vector3, Float3>(ref field.Velocity[0]), count);
        }
        else
        {
            // The field the cloth ended the last frame against is where this frame starts.
            _sdfData.View.CopyTo(_bodyPrev.View);
            if (field.DirtyZ1 >= field.DirtyZ0)
            {
                int slab = field.Grid.Nx * field.Grid.Ny;
                int start = field.DirtyZ0 * slab;
                int length = (field.DirtyZ1 - field.DirtyZ0 + 1) * slab;
                _sdfData.View.SubView(start, length).CopyFromCPU(ref field.Distances[start], length);
                _bodyVelocity.View.SubView(start, length).CopyFromCPU(ref Unsafe.As<Vector3, Float3>(ref field.Velocity[start]), length);
            }
        }
        _bodyVersion = field.Version;
    }

    /// <summary>The body collision of one substep while the body moves; false when no moving body is set.</summary>
    private bool SolveMovingBodyGpu(float blend, float h, int ground)
    {
        if (_bodyPrev == null || _bodyVelocity == null)
            return false;
        var acc = _rt.Accelerator;
        if (_movingBodyKernel == null || !ReferenceEquals(_movingBodyKernelOwner, acc))
        {
            _movingBodyKernel = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<Float3>, ArrayView<Float3>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>, ArrayView<byte>, ArrayView<float>, ArrayView<float>, ArrayView<Float3>,
                MovingBodyParams>(MovingBodyKernels.SolveMovingBody);
            _movingBodyKernelOwner = acc;
        }
        _movingBodyKernel(_n, _pos.View, _prev.View, _invMass.View, _thickness.View, _friction.View, _openStitch.View,
            _bodyPrev.View, _sdfData.View, _bodyVelocity.View, new MovingBodyParams(_sdfParams, blend, h, ground));
        return true;
    }

    private void DisposeMovingBody()
    {
        _bodyPrev?.Dispose();
        _bodyVelocity?.Dispose();
        _bodyPrev = null;
        _bodyVelocity = null;
    }
}
