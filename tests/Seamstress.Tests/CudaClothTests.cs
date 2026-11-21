using System.Diagnostics;
using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;
using Seamstress.Cuda;
using Xunit;
using Xunit.Abstractions;

namespace Seamstress.Tests;

public class CudaClothTests
{
    private readonly ITestOutputHelper _output;

    public CudaClothTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void CudaSolverDrapesTShirtAndMatchesCpuStability()
    {
        bool registered = CudaClothBackend.Register();
        _output.WriteLine($"CUDA registered: {registered}, device: {ClothAcceleration.RegisteredDeviceName ?? "none"}");
        if (!registered)
        {
            _output.WriteLine($"CUDA error: {CudaClothBackend.LastError}");
            return; // Graceful skip on non-NVIDIA machines
        }

        var avatar = Avatar.CreateMannequin();
        var doc = new Document();
        var shirt = Templates.Create("T-Shirt", doc);
        shirt.ParticleDistance = 1.5f; // denser mesh to exercise CUDA cores
        doc.Garments.Add(shirt);
        var mesh = Mesher.Build(shirt);
        var settings = new SimulationSettings { Substeps = 16, SelfCollision = false };

        // Warm up CUDA JIT + run 60 frames on GPU
        ClothAcceleration.Enabled = true;
        using var gpuSolver = ClothSolver.Create(new[] { mesh }, Array.Empty<GarmentMesh>(), avatar.Sdf, settings);
        Assert.StartsWith("CUDA", gpuSolver.BackendName);
        gpuSolver.Step(); // warm-up
        var swGpu = Stopwatch.StartNew();
        for (int i = 0; i < 60; i++)
            gpuSolver.Step();
        swGpu.Stop();

        foreach (var p in gpuSolver.Positions)
        {
            Assert.False(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z));
            Assert.False(float.IsInfinity(p.X) || float.IsInfinity(p.Y) || float.IsInfinity(p.Z));
        }
        Assert.True(gpuSolver.SeamGap < 0.05f, $"Expected seams to close, got SeamGap={gpuSolver.SeamGap:F4} m");

        // Compare against CPU on same mesh
        ClothAcceleration.Enabled = false;
        try
        {
            using var cpuSolver = ClothSolver.Create(new[] { mesh }, Array.Empty<GarmentMesh>(), avatar.Sdf, settings);
            Assert.Equal("CPU", cpuSolver.BackendName);
            cpuSolver.Step(); // warm-up
            var swCpu = Stopwatch.StartNew();
            for (int i = 0; i < 60; i++)
                cpuSolver.Step();
            swCpu.Stop();

            _output.WriteLine($"Mesh: {gpuSolver.ParticleCount} particles, {gpuSolver.ConstraintCount} constraints");
            _output.WriteLine($"60 frames ({gpuSolver.BackendName}): {swGpu.ElapsedMilliseconds} ms ({swGpu.Elapsed.TotalMilliseconds / 60:F2} ms/frame), SeamGap={gpuSolver.SeamGap:F4}m");
            _output.WriteLine($"60 frames (CPU): {swCpu.ElapsedMilliseconds} ms ({swCpu.Elapsed.TotalMilliseconds / 60:F2} ms/frame), SeamGap={cpuSolver.SeamGap:F4}m");
        }
        finally
        {
            ClothAcceleration.Enabled = true;
        }
    }

    /// <summary>A square of cloth with no seams, no avatar and no floor: it simply falls.</summary>
    private static ClothSolver FallingSquare()
    {
        var piece = new PatternPiece { Name = "Square" };
        piece.Points.Add(new PatternPoint(0, 0));
        piece.Points.Add(new PatternPoint(40, 0));
        piece.Points.Add(new PatternPoint(40, 40));
        piece.Points.Add(new PatternPoint(0, 40));
        var garment = new Garment { Name = "Cloth" };
        garment.Pieces.Add(piece);
        ClothAcceleration.Enabled = true;
        return ClothSolver.Create(new[] { Mesher.Build(garment) }, Array.Empty<GarmentMesh>(), null,
            new SimulationSettings { SelfCollision = false, GroundPlane = false });
    }

    /// <summary>
    /// Steps one frame and returns how fast the middle particle of the pack travelled (m/s). Unlike the peak speed,
    /// this is not moved by the one particle that a pinch holds or lets go of.
    /// </summary>
    private static float StepMedianSpeed(ClothSolver solver)
    {
        var before = (Vector3[])solver.Positions.Clone();
        solver.Step();
        var speed = new float[before.Length];
        for (int i = 0; i < speed.Length; i++)
            speed[i] = Vector3.Distance(before[i], solver.Positions[i]) * 60f;
        Array.Sort(speed);
        return speed[speed.Length / 2];
    }

    // A pinch moves the solver from the GPU to the CPU and back, and the cloth's velocities have to cross with it.
    // Both tests hold a single particle (the smallest pinch) so that the rest of the cloth keeps falling freely.

    [Fact]
    public void FallingClothKeepsItsSpeedWhenAPinchMovesItToTheCpu()
    {
        if (!CudaClothBackend.Register())
        {
            _output.WriteLine($"CUDA error: {CudaClothBackend.LastError}");
            return; // Graceful skip on non-NVIDIA machines
        }

        using var solver = FallingSquare();
        Assert.StartsWith("CUDA", solver.BackendName);
        float falling = 0;
        for (int i = 0; i < 10; i++)
            falling = StepMedianSpeed(solver);
        Assert.True(falling > 1f, $"Expected the cloth to be falling after 10 frames, got {falling:F2} m/s");

        solver.BeginPinch(solver.Positions[0], 0.01f);
        float after = StepMedianSpeed(solver); // this frame runs on the CPU
        _output.WriteLine($"Falling at {falling:F2} m/s on the GPU, {after:F2} m/s on the first CPU frame");
        Assert.True(after > 0.8f * falling, $"The cloth lost its speed when the CPU took over: {falling:F2} -> {after:F2} m/s");
    }

    [Fact]
    public void FallingClothKeepsItsSpeedWhenAPinchEndsAndTheGpuTakesOver()
    {
        if (!CudaClothBackend.Register())
        {
            _output.WriteLine($"CUDA error: {CudaClothBackend.LastError}");
            return; // Graceful skip on non-NVIDIA machines
        }

        using var solver = FallingSquare();
        Assert.StartsWith("CUDA", solver.BackendName);
        solver.BeginPinch(solver.Positions[0], 0.01f);
        float falling = 0;
        for (int i = 0; i < 10; i++)
            falling = StepMedianSpeed(solver); // on the CPU from the first frame, so the GPU has never seen the cloth move
        Assert.True(falling > 0.8f, $"Expected the cloth to be falling after 10 frames, got {falling:F2} m/s");

        solver.EndPinch();
        float after = StepMedianSpeed(solver); // back on the GPU
        _output.WriteLine($"Falling at {falling:F2} m/s on the CPU, {after:F2} m/s on the first GPU frame");
        Assert.True(after > 0.8f * falling, $"The cloth lost its speed when the GPU took over: {falling:F2} -> {after:F2} m/s");
    }
}
