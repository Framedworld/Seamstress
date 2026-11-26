using System.Diagnostics;
using Seamstress.Core.Animation;
using Seamstress.Core.Editing;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Mcp;

public sealed partial class GarmentSession
{
    /// <summary>What to play and how, for <see cref="PlayAnimation"/>.</summary>
    public sealed record AnimationRequest(string Animation, string? Clip = null, int? Frames = null, float Speed = 1f,
        float? BlendIn = null, int? Substeps = null, bool? SelfCollision = null, bool UseGpu = true, float CellCm = 1f,
        string? RecordPath = null, bool KeepResult = false);

    /// <summary>
    /// Plays an animation on the avatar with every visible, unfrozen garment simulating on the moving body, and
    /// reports how the cloth held up. Unless <see cref="AnimationRequest.KeepResult"/>, the drape and the avatar go
    /// back to how they were.
    /// </summary>
    public object PlayAnimation(AnimationRequest request)
    {
        var avatar = Avatar;
        var anim = AnimationLibrary.Prepare(avatar, Doc.Avatar, request.Animation, request.Clip, request.BlendIn,
            Math.Clamp(request.CellCm, 0.5f, 3f) / 100f);
        anim.Speed = request.Speed;
        anim.TrackPenetration = true;

        var simulated = Doc.Garments.Where(g => g.Visible && !g.Frozen && !g.Deactivated && g.Pieces.Count > 0).ToList();
        if (simulated.Count == 0)
            throw new InvalidOperationException("No visible, unfrozen, active garment to simulate on the moving body.");
        var obstacles = Doc.Garments.Where(g => g.Visible && !g.Deactivated && g.Pieces.Count > 0 && !simulated.Contains(g)).ToList();
        var before = simulated.SelectMany(g => g.Pieces).ToDictionary(p => p.Id, p => (p.Drape, p.DrapeKey));

        Attachments.Prepare(Doc, Workspace.MeshFor);
        var settings = Doc.Simulation.Clone();
        if (request.Substeps is { } substeps)
            settings.Substeps = Math.Clamp(substeps, 1, 64);
        if (request.SelfCollision is { } self)
            settings.SelfCollision = self;

        bool prevGpu = ClothAcceleration.Enabled;
        ClothAcceleration.Enabled = request.UseGpu;
        try
        {
            using var solver = ClothSolver.Create(simulated.Select(Workspace.MeshFor).ToList(),
                obstacles.Select(Workspace.MeshFor).ToList(), anim.Field.Grid, settings);
            int frames = request.Frames ?? (int)MathF.Ceiling(anim.Duration / (anim.Speed * solver.FrameDuration));
            frames = Math.Clamp(frames, 1, 20000);
            var stepTimes = new List<double>(frames);
            var frameTimes = new List<double>(frames);
            var total = Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                var clock = Stopwatch.StartNew();
                anim.BeforeStep(solver);
                double moved = clock.Elapsed.TotalMilliseconds;
                solver.Step();
                stepTimes.Add(clock.Elapsed.TotalMilliseconds - moved);
                anim.AfterStep(solver);
                frameTimes.Add(clock.Elapsed.TotalMilliseconds);
            }
            total.Stop();

            var movedPieces = new HashSet<string>();
            foreach (var cp in solver.Pieces.Where(p => p.Simulated))
            {
                cp.Piece.Drape = solver.ExtractDrape(cp);
                cp.Piece.DrapeKey = cp.Mesh.Key;
                movedPieces.Add(cp.Piece.Id);
            }
            Attachments.Follow(Doc, Workspace.MeshFor, movedPieces);
            var openSeams = simulated.SelectMany(g => SeamReport.OpenSeams(Doc, Workspace.MeshFor, g)).Take(12).ToList();

            object? recording = null;
            if (request.RecordPath is { } recordPath)
            {
                var written = PointCacheExporter.Write(Path.GetFullPath(recordPath), anim.Recording, Workspace);
                recording = new { path = Path.GetFullPath(recordPath), frames = written.Frames, meshes = written.Meshes,
                    seconds = MathF.Round(written.Seconds, 3), megabytes = Math.Round(written.Bytes / 1048576.0, 1),
                    format = "glTF binary, one morph target per frame with an animation of the morph weights" };
            }

            if (request.KeepResult)
            {
                SetAvatar(anim.PosedAvatar($"{avatar.Name} (posed)"));
            }
            else
            {
                foreach (var g in simulated)
                {
                    foreach (var p in g.Pieces)
                    {
                        if (before.TryGetValue(p.Id, out var was))
                            (p.Drape, p.DrapeKey) = was;
                    }
                }
                Attachments.Follow(Doc, Workspace.MeshFor, movedPieces);
            }

            static double R(double v) => Math.Round(v, 2);
            static double Percentile(List<double> v, double q) => v.Count == 0 ? 0 : v.OrderBy(x => x).ElementAt(Math.Min(v.Count - 1, (int)(v.Count * q)));
            int fieldFrames = Math.Max(1, anim.Frames);
            return new
            {
                animation = anim.Source,
                backend = solver.BackendName,
                frames,
                timelineSeconds = MathF.Round(anim.Time, 3),
                durationSeconds = MathF.Round(anim.Duration, 3),
                blendInSeconds = anim.BlendIn,
                speed = anim.Speed,
                finished = anim.Finished,
                particles = solver.ParticleCount,
                substeps = solver.EffectiveSubsteps,
                collisionGrid = new { cells = anim.Field.CellCount, cellCm = anim.Field.Cell * 100 },
                timing = new
                {
                    msPerFrame = R(frameTimes.Average()),
                    msPerFrameP95 = R(Percentile(frameTimes, 0.95)),
                    msPerFrameMax = R(frameTimes.Max()),
                    solverMsPerFrame = R(stepTimes.Average()),
                    bodyMsPerFrame = R((anim.FieldMilliseconds + anim.PoseMilliseconds) / fieldFrames),
                    collisionFieldMsPerFrame = R(anim.FieldMilliseconds / fieldFrames),
                    collisionFieldMsMax = R(anim.FieldMillisecondsMax),
                    framesPerSecond = R(frames / Math.Max(1e-3, total.Elapsed.TotalSeconds)),
                },
                // simulate reports the last frame's fastest particle; the highest of all frames is always larger, a still
                // body included, so all three are given.
                peakSpeedMetresPerSec = MathF.Round(anim.PeakSpeed, 3),
                peakSpeedMedianMetresPerSec = MathF.Round(anim.PeakSpeedMedian, 3),
                peakSpeedLastFrameMetresPerSec = MathF.Round(anim.PeakSpeedLastFrame, 3),
                widestSeamGapMetres = MathF.Round(anim.WidestSeamGap, 4),
                finalSeamGapMetres = MathF.Round(solver.SeamGap, 4),
                penetration = new
                {
                    maxDepthMm = MathF.Round(anim.MaxPenetration * 1000, 2),
                    toleranceMm = anim.PenetrationTolerance * 1000,
                    mostParticlesBeyondTolerance = anim.MaxPenetratingParticles,
                    particlesBeyondToleranceAtEnd = anim.LastPenetratingParticles,
                },
                openSeams,
                recording,
                keptResult = request.KeepResult,
                advice = anim.MaxPenetratingParticles > 0
                    ? "Cloth went into the moving body: play slower (speed 0.5), add substeps, or give layered garments more bodyOffsetMm."
                    : anim.PeakSpeedMedian > 6 ? "The cloth kept moving fast through the animation: try a lower speed or more substeps." : null,
            };
        }
        finally
        {
            ClothAcceleration.Enabled = prevGpu;
        }
    }
}
