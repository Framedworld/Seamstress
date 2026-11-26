using System.Numerics;
using Seamstress.Core.Animation;

namespace Seamstress.Core.Simulation;

/// <summary>Drives a body that moves while the cloth simulates (an animation): called around every solver frame.</summary>
public interface IBodyMotion
{
    /// <summary>Before the frame: move the body and hand its new collision field to the solver.</summary>
    void BeforeStep(ClothSolver solver);

    /// <summary>After the frame, while the runner publishes it: record or show where the body and cloth are.</summary>
    void AfterStep(ClothSolver solver);
}

/// <summary>An accelerator that can collide the cloth with a moving body (<see cref="MovingBodyField"/>).</summary>
public interface IMovingBodyAccelerator
{
    /// <summary>
    /// Takes the field for the next frame. The accelerator keeps the previous one, blends between the two across the
    /// frame's substeps and lets the body's velocity drive friction.
    /// </summary>
    void SetMovingBody(MovingBodyField field);

    /// <summary>
    /// Moves pinned particles (which the solver never moves itself) from <paramref name="from"/> to
    /// <paramref name="to"/> over the next frame's substeps, so they ride on the moving body.
    /// </summary>
    void MovePinned(int[] particles, Vector3[] from, Vector3[] to);
}

public sealed partial class ClothSolver
{
    private MovingBodyField? _movingBody;
    private long _movingBodyVersion = -1;
    private float _bodyBlend = 1f, _bodyH = FrameTime;
    private int[] _riders = Array.Empty<int>();
    private Vector3[] _rideFrom = Array.Empty<Vector3>(), _rideTo = Array.Empty<Vector3>();

    /// <summary>Simulated seconds one <see cref="Step"/> covers.</summary>
    public float FrameDuration => FrameTime * Math.Clamp(Settings.TimeScale, 0.05f, 4f);

    /// <summary>The moving body the cloth collides with, if any (the solver was then created with its grid).</summary>
    public MovingBodyField? MovingBody => _movingBody;

    /// <summary>
    /// Collides the cloth with a body that moves: call once per frame, after updating the field and before
    /// <see cref="Step"/>. The solver must have been created with <paramref name="field"/>'s grid as its collider.
    /// </summary>
    public void MoveBody(MovingBodyField field)
    {
        if (!ReferenceEquals(Collider, field.Grid))
            throw new InvalidOperationException("Create the solver with the moving body's grid as its collider.");
        _movingBody = field;
        if (field.Version == _movingBodyVersion)
            return;
        _movingBodyVersion = field.Version;
        if (_accelerator is IMovingBodyAccelerator moving)
        {
            moving.SetMovingBody(field);
        }
        else if (_accelerator != null)
        {
            // This accelerator cannot follow a moving body: the CPU solver takes over from where it was.
            _accelerator.DownloadPositions(Positions);
            _accelerator.DownloadVelocities(_velocity);
            _accelerator.Dispose();
            _accelerator = null;
            AppLog.Write("The cloth accelerator cannot collide with a moving body; simulating on the CPU.");
        }
    }

    /// <summary>The pinned particles of the simulated pieces: hard parts and pinned cloth, which the solver holds still.</summary>
    public int[] PinnedParticles()
    {
        var pinned = new List<int>();
        foreach (var cloth in Pieces)
        {
            if (!cloth.Simulated)
                continue;
            for (int k = 0; k < cloth.Mesh.VertexCount; k++)
            {
                if (_invMass[cloth.Offset + k] == 0)
                    pinned.Add(cloth.Offset + k);
            }
        }
        return pinned.ToArray();
    }

    /// <summary>
    /// Carries pinned particles to <paramref name="targets"/> over the next <see cref="Step"/>, a substep at a time.
    /// A moving body calls it every frame so that pins and hard parts move with it instead of standing in its way.
    /// </summary>
    public void MovePinned(int[] particles, Vector3[] targets)
    {
        if (_rideFrom.Length != particles.Length)
        {
            _rideFrom = new Vector3[particles.Length];
            _rideTo = new Vector3[particles.Length];
        }
        _riders = particles;
        for (int k = 0; k < particles.Length; k++)
        {
            _rideFrom[k] = Positions[particles[k]];
            _rideTo[k] = targets[k];
        }
        if (_accelerator is IMovingBodyAccelerator moving)
            moving.MovePinned(particles, _rideFrom, _rideTo);
    }

    /// <summary>The CPU side of <see cref="MovePinned"/>: where the pinned particles are during this substep.</summary>
    private void RidePinned(int step, int substeps)
    {
        if (_riders.Length == 0)
            return;
        float t0 = step / (float)substeps, t1 = (step + 1f) / substeps;
        for (int k = 0; k < _riders.Length; k++)
        {
            int i = _riders[k];
            _previous[i] = Vector3.Lerp(_rideFrom[k], _rideTo[k], t0);
            Positions[i] = Vector3.Lerp(_rideFrom[k], _rideTo[k], t1);
        }
    }

    /// <summary>The CPU body collision against a moving body: blended field, and friction relative to the skin.</summary>
    private void SolveMovingBody()
    {
        var x = Positions;
        var field = _movingBody!;
        bool ground = Settings.GroundPlane;
        for (int i = 0; i < x.Length; i++)
        {
            if (_invMass[i] == 0)
                continue;
            float distance = field.Sample(x[i], _bodyBlend, out var normal, out var bodyVelocity);
            float depth = _thickness[i] - distance;
            if (depth > 0)
            {
                x[i] += normal * depth;
                ApplyMovingFriction(i, normal, depth, bodyVelocity * _bodyH);
            }
            if (ground && x[i].Y < _thickness[i])
            {
                float below = _thickness[i] - x[i].Y;
                x[i].Y = _thickness[i];
                ApplyFriction(i, Vector3.UnitY, below);
            }
        }
    }

    private void ApplyMovingFriction(int i, Vector3 normal, float depth, Vector3 bodyMove)
    {
        if (_openStitch[i])
            return;
        // Friction acts on how the cloth slides over the skin, not over the world: cloth resting on a moving arm is
        // carried with it.
        var move = Positions[i] - _previous[i] - bodyMove;
        var tangent = move - normal * Vector3.Dot(move, normal);
        float slide = tangent.Length();
        if (slide < 1e-9f)
            return;
        float grip = _friction[i] * depth;
        Positions[i] -= slide <= grip ? tangent : tangent * (grip / slide);
    }
}
