using System.Numerics;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.Animation;

/// <summary>
/// A small rig fitted to any avatar from its own measurements: a root and one joint per shoulder, with the arm's
/// vertices weighted to it. Enough for the built-in poses (raising and lowering the arms), with no file needed.
/// </summary>
public sealed class AutoRig
{
    public required SkinnedBody Body { get; init; }

    /// <summary>Joint index per side: [0] left (+X), [1] right; -1 where no arm was found.</summary>
    public required int[] Shoulder { get; init; }

    /// <summary>How far each arm hangs below horizontal in the rest pose, degrees.</summary>
    public required float[] DroopDegrees { get; init; }

    public static AutoRig Build(TriMesh mesh)
    {
        var m = BodyMeasurements.Measure(mesh);
        var names = new List<string> { "Root" };
        var parents = new List<int> { -1 };
        var rest = new List<Matrix4x4> { Matrix4x4.Identity };
        int n = mesh.Positions.Length;
        var joints = new int[n * SkinnedBody.Influences];
        var weights = new float[n * SkinnedBody.Influences];
        for (int v = 0; v < n; v++)
            weights[v * SkinnedBody.Influences] = 1;
        var shoulder = new[] { -1, -1 };
        var droop = new[] { 0f, 0f };

        foreach (var arm in m.Arms)
        {
            int side = arm.Side == "left" ? 0 : 1;
            float sign = side == 0 ? 1 : -1;
            var axis = Vector3.Normalize(arm.Axis);
            float length = MathF.Max(arm.LengthCm / 100f, 0.2f);
            // The measured shoulder is the first girth outside the torso; the joint sits a little further in.
            var pivot = arm.Shoulder - axis * (0.12f * length);
            int joint = names.Count;
            names.Add(side == 0 ? "LeftShoulder" : "RightShoulder");
            parents.Add(0);
            rest.Add(Matrix4x4.CreateTranslation(pivot));
            shoulder[side] = joint;
            droop[side] = arm.DroopDegrees;

            // Arm girth along its length, from the tape sections, to tell arm vertices from the torso beside them.
            var radii = arm.Sections
                .Select(s => (T: Vector3.Dot(s.Centre - pivot, axis), R: s.CircumferenceCm / 100f / MathF.Tau))
                .OrderBy(s => s.T).ToList();
            if (radii.Count == 0)
                continue;
            float wristT = radii[^1].T;
            float Radius(float t)
            {
                if (t <= radii[0].T)
                    return radii[0].R;
                if (t >= wristT)
                    return radii[^1].R * 1.8f; // the hand is flatter and wider than the wrist
                for (int i = 0; i + 1 < radii.Count; i++)
                {
                    if (t <= radii[i + 1].T)
                    {
                        float f = (t - radii[i].T) / MathF.Max(radii[i + 1].T - radii[i].T, 1e-6f);
                        return radii[i].R + (radii[i + 1].R - radii[i].R) * f;
                    }
                }
                return radii[^1].R;
            }

            for (int v = 0; v < n; v++)
            {
                var p = mesh.Positions[v];
                if (p.X * sign <= 0)
                    continue;
                var d = p - pivot;
                float t = Vector3.Dot(d, axis);
                float r = (d - axis * t).Length();
                float along = Smooth(-0.02f, 0.06f, t);
                if (along <= 0)
                    continue;
                float girth = Radius(MathF.Max(t, 0));
                float within = 1 - Smooth(1.45f * girth, 2.1f * girth, r);
                float w = along * within;
                if (w <= 0)
                    continue;
                int at = v * SkinnedBody.Influences;
                joints[at] = 0;
                weights[at] = 1 - w;
                joints[at + 1] = joint;
                weights[at + 1] = w;
            }
        }

        // The two arms move together, from one rest angle.
        if (shoulder[0] >= 0 && shoulder[1] >= 0)
            droop[0] = droop[1] = (droop[0] + droop[1]) / 2;

        var skeleton = new Skeleton { Names = names.ToArray(), Parents = parents.ToArray(), RestLocal = rest.ToArray() };
        var world = new Matrix4x4[skeleton.Count];
        skeleton.World(skeleton.RestLocal, world);
        var body = new SkinnedBody
        {
            Skeleton = skeleton,
            BindPositions = (Vector3[])mesh.Positions.Clone(),
            Triangles = mesh.Triangles,
            Joints = joints,
            Weights = weights,
            InverseBind = world.Select(w => Matrix4x4.Invert(w, out var inv) ? inv : Matrix4x4.Identity).ToArray(),
            Source = "automatic shoulder rig",
        };
        return new AutoRig { Body = body, Shoulder = shoulder, DroopDegrees = droop };
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Locals with both arms at <paramref name="belowHorizontal"/> degrees below horizontal (negative raises them above).</summary>
    public void ArmsAt(float belowHorizontal, Matrix4x4[] rest, Matrix4x4[] locals)
    {
        for (int side = 0; side < 2; side++)
        {
            int j = Shoulder[side];
            if (j < 0)
                continue;
            float sign = side == 0 ? 1 : -1;
            float raise = (DroopDegrees[side] - belowHorizontal) * MathF.PI / 180f;
            locals[j] = Matrix4x4.CreateRotationZ(sign * raise) * rest[j];
        }
    }
}

/// <summary>The animations that come with Seamstress and need no file.</summary>
public static class BuiltInAnimations
{
    public const string TPoseToAPose = "builtin:t-pose-to-a-pose";
    public const string ArmRaise = "builtin:arm-raise";

    public static readonly string[] Ids = { TPoseToAPose, ArmRaise };

    /// <summary>English display name, translated where it is shown.</summary>
    public static string DisplayName(string id) => id switch
    {
        TPoseToAPose => "T-pose to A-pose",
        ArmRaise => "Arm raise",
        _ => id,
    };

    public static bool IsBuiltIn(string id) => Ids.Contains(id);

    /// <summary>
    /// The clip for a built-in animation on this rig. The T-to-A clip starts from the rest pose,
    /// lifts the arms level (the T-pose), holds, then lowers them to 45° (the A-pose); the arm raise lifts both arms
    /// above level and brings them back to rest.
    /// </summary>
    public static AnimationClip Create(string id, AutoRig rig)
    {
        float restDroop = rig.DroopDegrees.Where((_, i) => rig.Shoulder[i] >= 0).DefaultIfEmpty(45f).Average();
        // Each segment: (duration, arm angle below horizontal at its end); the arms start at their rest angle.
        (float Seconds, float Angle)[] keys = id switch
        {
            TPoseToAPose => new[] { (1.0f, 0f), (0.3f, 0f), (1.5f, 45f), (0.4f, 45f) },
            ArmRaise => new[] { (1.4f, -25f), (0.5f, -25f), (1.4f, restDroop), (0.4f, restDroop) },
            _ => throw new ArgumentException($"Unknown built-in animation '{id}'."),
        };
        float duration = keys.Sum(k => k.Seconds);
        return new ProceduralClip
        {
            Name = DisplayName(id),
            Duration = duration,
            Evaluate = (time, rest, locals) =>
            {
                float angle = restDroop, start = 0;
                foreach (var (seconds, target) in keys)
                {
                    if (time <= start + seconds)
                    {
                        angle += (target - angle) * Pose.Ease((time - start) / seconds);
                        break;
                    }
                    angle = target;
                    start += seconds;
                }
                rig.ArmsAt(angle, rest, locals);
            },
        };
    }
}
