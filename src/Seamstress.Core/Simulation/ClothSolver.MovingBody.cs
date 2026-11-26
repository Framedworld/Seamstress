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
}

public sealed partial class ClothSolver
{
    private MovingBodyField? _movingBody;
    private long _movingBodyVersion = -1;
    private float _bodyBlend = 1f, _bodyH = FrameTime;

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
