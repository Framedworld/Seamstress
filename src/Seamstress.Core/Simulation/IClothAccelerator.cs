using System.Numerics;

namespace Seamstress.Core.Simulation;

/// <summary>Snapshot of solver topology and per-particle parameters handed to an accelerator at creation time.</summary>
public sealed class ClothSolverData
{
    public required Vector3[] Positions { get; init; }
    public required float[] InvMass { get; init; }
    public required float[] Thickness { get; init; }
    public required float[] Friction { get; init; }
    public required float[] Drag { get; init; }
    public required float[] ClothThickness { get; init; }
    public required bool[] Stitched { get; init; }
    public required int[] PieceOf { get; init; }
    public required int[] GarmentOfPiece { get; init; }
    public required int PieceCount { get; init; }
    public required int GarmentCount { get; init; }

    public required int[] Ca { get; init; }
    public required int[] Cb { get; init; }
    public required float[] Rest { get; init; }
    public required float[] Compliance { get; init; }

    public required int[] Ha { get; init; }
    public required int[] Hb { get; init; }
    public required float[] HRest { get; init; }
    public required float[] HCompliance { get; init; }

    public required int[] Sa { get; init; }
    public required int[] Sb { get; init; }

    public required SdfGrid? Collider { get; init; }
}

/// <summary>Per-frame parameters passed into an accelerated solver step.</summary>
public readonly struct ClothStepParams
{
    public required int Substeps { get; init; }
    public required float SubstepDt { get; init; }
    public required float GravityY { get; init; }
    public required bool GroundPlane { get; init; }
    public required bool SelfCollision { get; init; }
    public required int SearchEvery { get; init; }
    public required float[] Sewing { get; init; }
    public required byte[] OpenStitch { get; init; }
}

/// <summary>Compact point-triangle self-collision contact pair for GPU resolution (must be public for ILGPU).</summary>
public struct GpuPtContact
{
    public int Particle;
    public int Ia;
    public int Ib;
    public int Ic;
    public float Side;
}

/// <summary>Compact hem edge-edge self-collision contact pair for GPU resolution (must be public for ILGPU).</summary>
public struct GpuHemContact
{
    public int A1;
    public int B1;
    public int A2;
    public int B2;
}

/// <summary>Compact contact lists produced by a CPU broadphase refresh for GPU contact kernels.</summary>
public readonly struct ContactBroadphaseResult
{
    public required GpuPtContact[] PtContacts { get; init; }
    public required int PtCount { get; init; }
    public required GpuHemContact[] HemContacts { get; init; }
    public required int HemCount { get; init; }
}

/// <summary>Optional GPU/SIMD backend that accelerates <see cref="ClothSolver"/> substeps.</summary>
public interface IClothAccelerator : IDisposable
{
    /// <summary>Human-readable backend name (e.g. "CUDA (NVIDIA GeForce RTX 4090)").</summary>
    string Name { get; }

    /// <summary>
    /// Hands the simulation back to the accelerator after the CPU solver stepped it (e.g. during a pinch):
    /// uploads the CPU positions and velocities.
    /// </summary>
    void UploadState(Vector3[] positions, Vector3[] velocities);

    /// <summary>Downloads current particle positions from the accelerator back into <paramref name="positions"/>.</summary>
    void DownloadPositions(Vector3[] positions);

    /// <summary>
    /// Downloads current particle velocities into <paramref name="velocities"/>. The accelerator owns the velocities
    /// while it is stepping, so the CPU solver needs them before it takes over.
    /// </summary>
    void DownloadVelocities(Vector3[] velocities);

    /// <summary>
    /// Runs one full display frame (all substeps) on the accelerator.
    /// Returns the peak particle speed (m/s) after the final substep.
    /// </summary>
    /// <param name="onBroadphase">
    /// Called only on broadphase substeps (<c>step % parameters.SearchEvery == 0</c>) when self-collision is on,
    /// receiving current particle positions and returning compact point-triangle and hem contact arrays to upload
    /// for GPU-side contact resolution across this and subsequent substeps.
    /// </param>
    float StepFrame(in ClothStepParams parameters, Func<Vector3[], ContactBroadphaseResult>? onBroadphase = null);
}

/// <summary>Global registration point for an optional GPU accelerator (e.g. Seamstress.Cuda).</summary>
public static class ClothAcceleration
{
    /// <summary>Set to false to force the reference CPU solver even when a GPU accelerator is registered.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Factory that tries to create a GPU accelerator for a solver, returning null if unavailable.</summary>
    public static Func<ClothSolverData, IClothAccelerator?>? Factory { get; set; }

    /// <summary>Name of the registered accelerator device, if any.</summary>
    public static string? RegisteredDeviceName { get; set; }
}
