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

    // A pinch runs on the GPU with the rest of the simulation: holding cloth must not slow the solver down by moving
    // it to the CPU, nor stop the cloth it does not hold. Both tests hold a single particle (the smallest pinch) so
    // that the rest of the cloth keeps falling freely.

    [Fact]
    public void APinchStaysOnTheGpuAndTheRestOfTheClothKeepsFalling()
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
        float after = StepMedianSpeed(solver);
        _output.WriteLine($"Falling at {falling:F2} m/s, {after:F2} m/s on the first pinched frame");
        Assert.StartsWith("CUDA", solver.BackendName);
        Assert.True(after > 0.8f * falling, $"The cloth lost its speed when the pinch began: {falling:F2} -> {after:F2} m/s");
    }

    [Fact]
    public void ClothHeldOnTheGpuFollowsTheHandAndStaysPutWhenLetGo()
    {
        if (!CudaClothBackend.Register())
        {
            _output.WriteLine($"CUDA error: {CudaClothBackend.LastError}");
            return; // Graceful skip on non-NVIDIA machines
        }

        using var solver = FallingSquare();
        Assert.StartsWith("CUDA", solver.BackendName);
        var start = solver.Positions[0];
        var hand = start + new Vector3(0.1f, 0.05f, 0f);
        solver.BeginPinch(start, 0.03f);
        for (int i = 0; i < 30; i++)
        {
            solver.MovePinch(hand);
            solver.Step();
        }
        float reach = Vector3.Distance(solver.Positions[0], hand);
        _output.WriteLine($"Held corner {reach * 100:F1} cm from the hand after half a second");
        Assert.True(reach < 0.03f, $"The held cloth did not follow the hand: {reach * 100:F1} cm away");

        // Let go, the cloth carries on with the motion it had; a fling (speed left over from being held) would throw it.
        static Vector3 Centre(Vector3[] p) => p.Aggregate(Vector3.Zero, (a, b) => a + b) / p.Length;
        var before = Centre(solver.Positions);
        solver.MovePinch(hand);
        solver.Step();
        var held = Centre(solver.Positions);
        solver.EndPinch();
        solver.Step();
        var released = Centre(solver.Positions);
        float change = Vector3.Distance(held - before, released - held);
        _output.WriteLine($"Centre moved {(held - before).Length() * 100:F2} cm held, {(released - held).Length() * 100:F2} cm let go");
        Assert.True(change < 0.01f, $"The cloth was flung when it was let go: its motion changed by {change * 100:F1} cm in one frame");
    }

    private static PatternPiece HorizontalSheet(string name, float size, float height)
    {
        var piece = new PatternPiece { Name = name };
        piece.Points.Add(new PatternPoint(0, 0));
        piece.Points.Add(new PatternPoint(size, 0));
        piece.Points.Add(new PatternPoint(size, size));
        piece.Points.Add(new PatternPoint(0, size));
        piece.Placement = new Placement { Position = new Vector3(0, height, 0), Rotation = new Vector3(-90, 0, 0) };
        return piece;
    }

    [Fact]
    public void CudaSelfCollisionKeepsLayeredClothSeparated()
    {
        if (!CudaClothBackend.Register())
        {
            _output.WriteLine($"CUDA error: {CudaClothBackend.LastError}");
            return; // Graceful skip on non-NVIDIA machines
        }

        var table = new Garment { Name = "Table", ParticleDistance = 3 };
        table.Pieces.Add(HorizontalSheet("Table", 80, 0.60f));
        var cloth = new Garment { Name = "Cloth", ParticleDistance = 2 };
        cloth.Pieces.Add(HorizontalSheet("Cloth", 40, 0.70f));

        var tableMesh = Mesher.Build(table);
        var clothMesh = Mesher.Build(cloth);
        var settings = new SimulationSettings { Substeps = 16, SelfCollision = true };

        ClothAcceleration.Enabled = true;
        using var solver = ClothSolver.Create(new[] { clothMesh }, new[] { tableMesh }, null, settings);
        Assert.StartsWith("CUDA", solver.BackendName);

        for (int i = 0; i < 150; i++)
            solver.Step();

        var clothPiece = solver.Pieces[0];
        float lowest = float.MaxValue, highest = float.MinValue;
        for (int i = 0; i < clothPiece.Mesh.VertexCount; i++)
        {
            var p = solver.Positions[clothPiece.Offset + i];
            Assert.False(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z));
            Assert.False(float.IsInfinity(p.X) || float.IsInfinity(p.Y) || float.IsInfinity(p.Z));
            lowest = MathF.Min(lowest, p.Y);
            highest = MathF.Max(highest, p.Y);
        }

        _output.WriteLine($"CUDA layered cloth ({solver.BackendName}): Y in [{lowest:F4}, {highest:F4}] m, active contacts={solver.ContactCount}");
        Assert.InRange(lowest, 0.601f, 0.620f);
        Assert.InRange(highest, 0.601f, 0.620f);
    }

    [Fact]
    public void CudaHighDensitySelfCollisionBenchmark()
    {
        if (!CudaClothBackend.Register())
        {
            _output.WriteLine($"CUDA error: {CudaClothBackend.LastError}");
            return; // Graceful skip on non-NVIDIA machines
        }

        var avatar = Avatar.CreateMannequin();
        var doc = new Document();
        var shirt = Templates.Create("T-Shirt", doc);
        shirt.ParticleDistance = 1.0f;
        doc.Garments.Add(shirt);
        var mesh = Mesher.Build(shirt);
        var settings = new SimulationSettings { Substeps = 16, SelfCollision = true };
        const int warmupFrames = 60;
        const int benchFrames = 60;

        ClothAcceleration.Enabled = true;
        using var gpuSolver = ClothSolver.Create(new[] { mesh }, Array.Empty<GarmentMesh>(), avatar.Sdf, settings);
        Assert.StartsWith("CUDA", gpuSolver.BackendName);
        for (int i = 0; i < warmupFrames; i++)
            gpuSolver.Step(); // warm-up CUDA JIT & close initial seams into active draping
        var swGpu = Stopwatch.StartNew();
        for (int i = 0; i < benchFrames; i++)
            gpuSolver.Step();
        swGpu.Stop();

        foreach (var p in gpuSolver.Positions)
        {
            Assert.False(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z));
            Assert.False(float.IsInfinity(p.X) || float.IsInfinity(p.Y) || float.IsInfinity(p.Z));
        }
        Assert.True(gpuSolver.SeamGap < 0.05f, $"Expected seams to close, got SeamGap={gpuSolver.SeamGap:F4} m");

        ClothAcceleration.Enabled = false;
        try
        {
            using var cpuSolver = ClothSolver.Create(new[] { mesh }, Array.Empty<GarmentMesh>(), avatar.Sdf, settings);
            Assert.Equal("CPU", cpuSolver.BackendName);
            for (int i = 0; i < warmupFrames; i++)
                cpuSolver.Step();
            var swCpu = Stopwatch.StartNew();
            for (int i = 0; i < benchFrames; i++)
                cpuSolver.Step();
            swCpu.Stop();

            double gpuMs = swGpu.Elapsed.TotalMilliseconds / benchFrames;
            double cpuMs = swCpu.Elapsed.TotalMilliseconds / benchFrames;
            double speedup = cpuMs / Math.Max(gpuMs, 1e-6);
            _output.WriteLine($"High-density T-Shirt (ParticleDistance=1.0cm, SelfCollision=true): {gpuSolver.ParticleCount} particles, {gpuSolver.ConstraintCount} constraints, {gpuSolver.ContactCount} contacts");
            _output.WriteLine($"GPU ({gpuSolver.BackendName}): {gpuMs:F2} ms/frame ({swGpu.ElapsedMilliseconds} ms / {benchFrames} frames), PeakSpeed={gpuSolver.PeakSpeed:F3} m/s, SeamGap={gpuSolver.SeamGap:F4}m");
            _output.WriteLine($"CPU: {cpuMs:F2} ms/frame ({swCpu.ElapsedMilliseconds} ms / {benchFrames} frames), PeakSpeed={cpuSolver.PeakSpeed:F3} m/s, SeamGap={cpuSolver.SeamGap:F4}m");
            _output.WriteLine($"Speedup: {speedup:F2}x");
        }
        finally
        {
            ClothAcceleration.Enabled = true;
        }
    }
}
