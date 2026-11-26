using System.Diagnostics;
using System.Numerics;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.Animation;

/// <summary>
/// Plays an animation on the avatar while the cloth simulates: every solver frame it moves the timeline on, skins the
/// body, updates the moving collision field, records the cloth and keeps the numbers an agent asks for (how deep cloth
/// went into the body, how fast it moved). Before the clip starts, the body blends from the pose the garments were
/// draped in to the clip's first frame, so nothing jumps.
/// </summary>
public sealed class BodyAnimation : IBodyMotion
{
    private readonly Matrix4x4[] _clipLocals, _locals;
    private readonly Vector3[] _posed, _shown;
    private readonly object _shownGate = new();
    private long _shownVersion;
    private float _speed = 1f;

    public SkinnedBody Body { get; }
    public AnimationClip Clip { get; }

    /// <summary>What was played: a built-in id or a file path, and the clip's name.</summary>
    public string Source { get; init; } = "";

    /// <summary>Seconds spent blending from the draped pose into the clip before it starts.</summary>
    public float BlendIn { get; }

    public MovingBodyField Field { get; }
    public PointCacheRecording Recording { get; } = new();

    /// <summary>Timeline position in seconds: <see cref="BlendIn"/> plus the clip's own time.</summary>
    public float Time { get; private set; }
    public float Duration => BlendIn + Clip.Duration;
    public bool Finished => Time >= Duration - 1e-5f;

    /// <summary>Animation seconds per simulated second (0.25 plays in slow motion, 2 twice as fast).</summary>
    public float Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 0.05f, 4f);
    }

    /// <summary>When set, every frame measures how deep the simulated cloth is inside the body (costs a little).</summary>
    public bool TrackPenetration { get; set; }

    /// <summary>Depth below which cloth counts as having gone into the body, in metres.</summary>
    public float PenetrationTolerance { get; set; } = 0.002f;

    // Statistics since the last ResetStatistics.
    public int Frames { get; private set; }
    public float MaxPenetration { get; private set; }
    public int MaxPenetratingParticles { get; private set; }
    public int LastPenetratingParticles { get; private set; }
    /// <summary>
    /// The fastest particle of the fastest frame (m/s). One particle in one substep sets it, so it jumps about from
    /// frame to frame even on a still body; <see cref="PeakSpeedMedian"/> is steadier, and
    /// <see cref="PeakSpeedLastFrame"/> is what the simulate tool reports.
    /// </summary>
    public float PeakSpeed { get; private set; }
    public float PeakSpeedLastFrame { get; private set; }
    public float PeakSpeedMedian
    {
        get
        {
            if (_peaks.Count == 0)
                return 0;
            var sorted = _peaks.OrderBy(p => p).ToList();
            return sorted[sorted.Count / 2];
        }
    }
    private readonly List<float> _peaks = new();
    public float WidestSeamGap { get; private set; }
    public double FieldMilliseconds { get; private set; }
    public double FieldMillisecondsMax { get; private set; }
    public double PoseMilliseconds { get; private set; }

    private BodyAnimation(SkinnedBody body, AnimationClip clip, float blendIn, float cell)
    {
        Body = body;
        Clip = clip;
        BlendIn = MathF.Max(blendIn, 0);
        int joints = body.Skeleton.Count;
        _clipLocals = new Matrix4x4[joints];
        _locals = new Matrix4x4[joints];
        _posed = new Vector3[body.VertexCount];
        _shown = new Vector3[body.VertexCount];

        // The grid covers everywhere the body goes, sampled through the whole timeline.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int samples = Math.Max(8, (int)MathF.Ceiling(Duration * 15));
        for (int s = 0; s <= samples; s++)
        {
            PoseAt(Duration * s / samples, _posed);
            foreach (var p in _posed)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        Field = new MovingBodyField(body.Triangles, (min, max), cell);
        Recording.SetBodyTriangles(body.Triangles);
        Seek(0);
    }

    /// <param name="cell">Collision grid spacing in metres; a coarser grid updates faster.</param>
    public static BodyAnimation Create(SkinnedBody body, AnimationClip clip, float blendIn, float cell = 0.01f, string source = "")
        => new(body, clip, blendIn, cell) { Source = source };

    /// <summary>The body's vertices at a point on the timeline, in avatar space.</summary>
    public void PoseAt(float time, Vector3[] output)
    {
        var bind = Body.BindLocals;
        if (time < BlendIn)
        {
            Clip.Sample(Body.Skeleton, 0, Body.Skeleton.RestLocal, _clipLocals);
            Pose.Blend(bind, _clipLocals, Pose.Ease(time / BlendIn), _locals);
        }
        else
        {
            Clip.Sample(Body.Skeleton, time - BlendIn, Body.Skeleton.RestLocal, _locals);
        }
        Body.Pose(_locals, output);
    }

    /// <summary>Jumps to a point on the timeline: the body is there at once and still.</summary>
    public void Seek(float time)
    {
        Time = Math.Clamp(time, 0, Duration);
        PoseAt(Time, _posed);
        Field.Update(_posed, 0, jump: true);
        Publish();
    }

    public void ResetStatistics()
    {
        Frames = 0;
        MaxPenetration = 0;
        MaxPenetratingParticles = LastPenetratingParticles = 0;
        PeakSpeed = PeakSpeedLastFrame = 0;
        _peaks.Clear();
        WidestSeamGap = 0;
        FieldMilliseconds = FieldMillisecondsMax = PoseMilliseconds = 0;
    }

    // Pinned particles (hard parts, pinned cloth) ride on the skin: the triangle under each, where on it, and the
    // offset from it in the triangle's own frame.
    private ClothSolver? _ridersOf;
    private int[] _riders = Array.Empty<int>();
    private (int Triangle, float Wa, float Wb, float Wc, Vector3 Local)[] _ride = Array.Empty<(int, float, float, float, Vector3)>();
    private Vector3[] _rideTargets = Array.Empty<Vector3>();

    private static (Vector3 Point, Vector3 E1, Vector3 E2, Vector3 N) Frame(Vector3 a, Vector3 b, Vector3 c, float wa, float wb, float wc)
    {
        var e1 = b - a;
        var n = Vector3.Cross(e1, c - a);
        e1 = e1.LengthSquared() > 1e-20f ? Vector3.Normalize(e1) : Vector3.UnitX;
        n = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
        return (a * wa + b * wb + c * wc, e1, Vector3.Cross(n, e1), n);
    }

    private void BindPinned(ClothSolver solver)
    {
        _ridersOf = solver;
        _riders = solver.PinnedParticles();
        _ride = new (int, float, float, float, Vector3)[_riders.Length];
        _rideTargets = new Vector3[_riders.Length];
        var tris = Body.Triangles;
        var pos = _posed;
        Parallel.For(0, _riders.Length, k =>
        {
            var p = solver.Positions[_riders[k]];
            int best = Field.NearestTriangle(p);
            if (best < 0)
            {
                // Further out than the band: look through every triangle.
                float nearest = float.MaxValue;
                for (int t = 0; t < tris.Length / 3; t++)
                {
                    var q = SdfGrid.ClosestPointOnTriangle(p, pos[tris[3 * t]], pos[tris[3 * t + 1]], pos[tris[3 * t + 2]]);
                    float d = Vector3.DistanceSquared(p, q);
                    if (d < nearest)
                    {
                        nearest = d;
                        best = t;
                    }
                }
            }
            var a = pos[tris[3 * best]];
            var b = pos[tris[3 * best + 1]];
            var c = pos[tris[3 * best + 2]];
            SdfGrid.ClosestPointOnTriangle(p, a, b, c, out float wa, out float wb, out float wc);
            var f = Frame(a, b, c, wa, wb, wc);
            var r = p - f.Point;
            _ride[k] = (best, wa, wb, wc, new Vector3(Vector3.Dot(r, f.E1), Vector3.Dot(r, f.E2), Vector3.Dot(r, f.N)));
        });
    }

    private void MovePinned(ClothSolver solver)
    {
        if (_riders.Length == 0)
            return;
        var tris = Body.Triangles;
        for (int k = 0; k < _riders.Length; k++)
        {
            var (t, wa, wb, wc, local) = _ride[k];
            var f = Frame(_posed[tris[3 * t]], _posed[tris[3 * t + 1]], _posed[tris[3 * t + 2]], wa, wb, wc);
            _rideTargets[k] = f.Point + f.E1 * local.X + f.E2 * local.Y + f.N * local.Z;
        }
        solver.MovePinned(_riders, _rideTargets);
    }

    public void BeforeStep(ClothSolver solver)
    {
        float dt = solver.FrameDuration;
        if (!ReferenceEquals(_ridersOf, solver))
            BindPinned(solver); // where the body is now, before it moves on
        var clock = Stopwatch.StartNew();
        if (!Finished)
        {
            Time = MathF.Min(Duration, Time + Speed * dt);
            PoseAt(Time, _posed);
        }
        double posed = clock.Elapsed.TotalMilliseconds;
        Field.Update(_posed, dt);
        PoseMilliseconds += posed;
        FieldMilliseconds += Field.LastUpdateMilliseconds;
        FieldMillisecondsMax = Math.Max(FieldMillisecondsMax, Field.LastUpdateMilliseconds);
        solver.MoveBody(Field);
        MovePinned(solver);
    }

    public void AfterStep(ClothSolver solver)
    {
        Frames++;
        PeakSpeed = MathF.Max(PeakSpeed, solver.PeakSpeed);
        PeakSpeedLastFrame = solver.PeakSpeed;
        _peaks.Add(solver.PeakSpeed);
        WidestSeamGap = MathF.Max(WidestSeamGap, solver.SeamGap);
        Recording.Capture(Time, solver, _posed);
        if (TrackPenetration)
            MeasurePenetration(solver);
        Publish();
    }

    private void Publish()
    {
        lock (_shownGate)
        {
            Array.Copy(_posed, _shown, _posed.Length);
            _shownVersion++;
        }
    }

    /// <summary>The body as of the newest frame, when it changed since <paramref name="seen"/>.</summary>
    public Vector3[]? TakeBody(ref long seen)
    {
        lock (_shownGate)
        {
            if (seen == _shownVersion)
                return null;
            seen = _shownVersion;
            return (Vector3[])_shown.Clone();
        }
    }

    private void MeasurePenetration(ClothSolver solver)
    {
        var x = solver.Positions;
        int count = 0;
        float deepest = 0;
        var gate = new object();
        foreach (var cloth in solver.Pieces)
        {
            if (!cloth.Simulated)
                continue;
            Parallel.For(0, (cloth.Mesh.VertexCount + 1023) / 1024, () => (0, 0f), (chunk, state, acc) =>
            {
                int end = Math.Min(cloth.Mesh.VertexCount, (chunk + 1) * 1024);
                for (int i = chunk * 1024; i < end; i++)
                {
                    float d = Field.Sample(x[cloth.Offset + i], 1f, out _, out _);
                    if (d < -PenetrationTolerance)
                        acc.Item1++;
                    if (d < 0)
                        acc.Item2 = MathF.Max(acc.Item2, -d);
                }
                return acc;
            }, acc =>
            {
                lock (gate)
                {
                    count += acc.Item1;
                    deepest = MathF.Max(deepest, acc.Item2);
                }
            });
        }
        LastPenetratingParticles = count;
        MaxPenetratingParticles = Math.Max(MaxPenetratingParticles, count);
        MaxPenetration = MathF.Max(MaxPenetration, deepest);
    }

    /// <summary>The body as an avatar frozen at the current pose (for keeping the result once playback ends).</summary>
    public Avatar PosedAvatar(string name)
    {
        var mesh = new TriMesh { Positions = (Vector3[])_posed.Clone(), Triangles = Body.Triangles };
        return new Avatar { Name = name, Mesh = mesh, Sdf = SdfGrid.FromMesh(mesh) };
    }
}

/// <summary>The cloth and body recorded during playback, a fixed number of times per animation second.</summary>
public sealed class PointCacheRecording
{
    public sealed record Piece(string PieceId, int Offset, int Count);
    public sealed record Frame(float Time, Vector3[] Cloth, Vector3[] Body);

    /// <summary>Frames per second of animation time.</summary>
    public float FrameRate { get; set; } = 30f;

    /// <summary>At most this many frames are kept (about half a minute at 30 frames per second).</summary>
    public int MaxFrames { get; set; } = 900;

    public List<Piece> Layout { get; private set; } = new();
    public List<Frame> Frames { get; } = new();
    public int[] BodyTriangles { get; private set; } = Array.Empty<int>();

    private readonly object _gate = new();
    private float _next;
    private ClothSolver? _layoutOf;

    public float RecordedDuration
    {
        get
        {
            lock (_gate)
                return Frames.Count == 0 ? 0 : Frames[^1].Time;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Frames.Clear();
            _next = 0;
        }
    }

    /// <summary>Keeps the frames up to <paramref name="time"/>, so recording goes on from there.</summary>
    public void TruncateAfter(float time)
    {
        lock (_gate)
        {
            Frames.RemoveAll(f => f.Time > time + 1e-4f);
            _next = Frames.Count == 0 ? 0 : Frames[^1].Time + 1f / FrameRate;
        }
    }

    /// <summary>The frame nearest a time, or null when nothing is recorded.</summary>
    public Frame? At(float time)
    {
        lock (_gate)
        {
            if (Frames.Count == 0)
                return null;
            int best = 0;
            for (int i = 1; i < Frames.Count; i++)
            {
                if (MathF.Abs(Frames[i].Time - time) < MathF.Abs(Frames[best].Time - time))
                    best = i;
            }
            return Frames[best];
        }
    }

    public List<Frame> Snapshot()
    {
        lock (_gate)
            return Frames.ToList();
    }

    internal void Capture(float time, ClothSolver solver, Vector3[] body)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_layoutOf, solver))
            {
                // A new run (after a pause): the same pieces in the same places keep the recording going.
                var layout = solver.Pieces.Where(p => p.Simulated).Select(p => new Piece(p.Piece.Id, p.Offset, p.Mesh.VertexCount)).ToList();
                bool same = layout.Select(p => (p.PieceId, p.Count)).SequenceEqual(Layout.Select(p => (p.PieceId, p.Count)));
                if (!same)
                {
                    Frames.Clear();
                    _next = 0;
                }
                Layout = layout;
                _layoutOf = solver;
            }
            if (time + 1e-4f < _next || Frames.Count >= MaxFrames)
                return;
            int total = Layout.Sum(p => p.Count);
            var cloth = new Vector3[total];
            int at = 0;
            foreach (var p in Layout)
            {
                Array.Copy(solver.Positions, p.Offset, cloth, at, p.Count);
                at += p.Count;
            }
            Frames.Add(new Frame(time, cloth, (Vector3[])body.Clone()));
            _next = time + 1f / FrameRate;
            if (_next <= time)
                _next = time + 1e-3f;
        }
    }

    /// <summary>Sets the body's triangles, used when the recording is exported.</summary>
    public void SetBodyTriangles(int[] triangles) => BodyTriangles = triangles;

    /// <summary>A recorded frame's positions for one piece, as a drape array.</summary>
    public float[] DrapeOf(Frame frame, Piece piece)
    {
        int at = 0;
        foreach (var p in Layout)
        {
            if (p == piece)
                break;
            at += p.Count;
        }
        var drape = new float[piece.Count * 3];
        for (int i = 0; i < piece.Count; i++)
        {
            var v = frame.Cloth[at + i];
            drape[3 * i] = v.X;
            drape[3 * i + 1] = v.Y;
            drape[3 * i + 2] = v.Z;
        }
        return drape;
    }
}
