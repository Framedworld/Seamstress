using System.Numerics;

namespace Seamstress.Core.Animation;

/// <summary>
/// A joint hierarchy. Matrices are row-vector (System.Numerics) style: a joint's world matrix is its local matrix
/// times its parent's world matrix. Parents always come before their children.
/// </summary>
public sealed class Skeleton
{
    public required string[] Names { get; init; }
    public required int[] Parents { get; init; }

    /// <summary>Each joint's local transform when nothing animates it.</summary>
    public required Matrix4x4[] RestLocal { get; init; }

    public int Count => Names.Length;

    private Dictionary<string, int>? _byName;

    /// <summary>The joint called <paramref name="name"/>, also when one of the two names carries a namespace prefix ("mixamorig:Hips").</summary>
    public int Find(string name)
    {
        if (_byName == null)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Names.Length; i++)
            {
                map.TryAdd(Names[i], i);
                map.TryAdd(Bare(Names[i]), i);
            }
            _byName = map;
        }
        return _byName.TryGetValue(name, out int index) || _byName.TryGetValue(Bare(name), out index) ? index : -1;
    }

    /// <summary>A joint name without its namespace ("mixamorig:LeftArm" and "Armature|LeftArm" both give "LeftArm").</summary>
    public static string Bare(string name)
    {
        int cut = name.LastIndexOfAny(new[] { ':', '|' });
        return cut >= 0 ? name[(cut + 1)..] : name;
    }

    /// <summary>World matrices of every joint for the given local matrices.</summary>
    public void World(Matrix4x4[] locals, Matrix4x4[] world)
    {
        for (int j = 0; j < Names.Length; j++)
            world[j] = Parents[j] >= 0 ? locals[j] * world[Parents[j]] : locals[j];
    }
}

/// <summary>
/// A body mesh bound to a skeleton for linear blend skinning: up to four joints per vertex. Bind positions are in the
/// file's scene space; <see cref="ToAvatar"/> takes skinned positions to the avatar's space (metres, Y up, standing
/// on the ground) so that the bind pose gives exactly the avatar mesh the garments were draped on.
/// </summary>
public sealed class SkinnedBody
{
    public const int Influences = 4;

    public required Skeleton Skeleton { get; init; }
    public required Vector3[] BindPositions { get; init; }
    public required int[] Triangles { get; init; }

    /// <summary><see cref="Influences"/> joint indices per vertex.</summary>
    public required int[] Joints { get; init; }

    /// <summary><see cref="Influences"/> weights per vertex, summing to one.</summary>
    public required float[] Weights { get; init; }

    /// <summary>Per joint: scene space to the joint's own space in the bind pose. Identity for joints that skin nothing.</summary>
    public required Matrix4x4[] InverseBind { get; init; }

    public Matrix4x4 ToAvatar { get; set; } = Matrix4x4.Identity;

    /// <summary>Where the skin came from: a file path, or a description of the automatic rig.</summary>
    public string Source { get; init; } = "";

    public int VertexCount => BindPositions.Length;

    private Matrix4x4[]? _bindLocals;

    /// <summary>Local matrices that put every joint where it was when the skin was bound.</summary>
    public Matrix4x4[] BindLocals => _bindLocals ??= ComputeBindLocals();

    private Matrix4x4[] ComputeBindLocals()
    {
        int n = Skeleton.Count;
        var bindWorld = new Matrix4x4[n];
        var locals = new Matrix4x4[n];
        var skins = new bool[n];
        for (int i = 0; i < Weights.Length; i++)
        {
            if (Weights[i] > 0)
                skins[Joints[i]] = true;
        }
        for (int j = 0; j < n; j++)
        {
            int parent = Skeleton.Parents[j];
            var parentWorld = parent >= 0 ? bindWorld[parent] : Matrix4x4.Identity;
            if (skins[j] && Matrix4x4.Invert(InverseBind[j], out var world))
            {
                bindWorld[j] = world;
                locals[j] = Matrix4x4.Invert(parentWorld, out var invParent) ? world * invParent : Skeleton.RestLocal[j];
            }
            else
            {
                locals[j] = Skeleton.RestLocal[j];
                bindWorld[j] = locals[j] * parentWorld;
            }
        }
        return locals;
    }

    private Matrix4x4[]? _world, _skin;

    /// <summary>Skins the body for these local joint matrices and writes avatar-space positions into <paramref name="output"/>.</summary>
    public void Pose(Matrix4x4[] locals, Vector3[] output)
    {
        int n = Skeleton.Count;
        _world ??= new Matrix4x4[n];
        _skin ??= new Matrix4x4[n];
        Skeleton.World(locals, _world);
        for (int j = 0; j < n; j++)
            _skin[j] = InverseBind[j] * _world[j] * ToAvatar;
        var skin = _skin;
        int count = BindPositions.Length;
        const int chunk = 2048;
        Parallel.For(0, (count + chunk - 1) / chunk, c =>
        {
            int end = Math.Min(count, (c + 1) * chunk);
            for (int v = c * chunk; v < end; v++)
            {
                var p = BindPositions[v];
                var sum = Vector3.Zero;
                int at = v * Influences;
                for (int k = 0; k < Influences; k++)
                {
                    float w = Weights[at + k];
                    if (w > 0)
                        sum += Vector3.Transform(p, skin[Joints[at + k]]) * w;
                }
                output[v] = sum;
            }
        });
    }

    /// <summary>
    /// Lines the bind pose up with an avatar mesh loaded from the same file: shifts <see cref="ToAvatar"/> so the two
    /// agree on average and returns the largest remaining distance (metres), or infinity when the meshes differ.
    /// </summary>
    public float FitTo(Simulation.TriMesh mesh)
    {
        if (mesh.Positions.Length != VertexCount || VertexCount == 0)
            return float.PositiveInfinity;
        var posed = new Vector3[VertexCount];
        Pose(BindLocals, posed);
        var sum = Vector3.Zero;
        for (int i = 0; i < posed.Length; i++)
            sum += mesh.Positions[i] - posed[i];
        var shift = sum / posed.Length;
        ToAvatar *= Matrix4x4.CreateTranslation(shift);
        float worst = 0;
        for (int i = 0; i < posed.Length; i++)
            worst = MathF.Max(worst, Vector3.Distance(mesh.Positions[i], posed[i] + shift));
        return worst;
    }

    /// <summary>Normalises four-per-vertex weights; a vertex with no weight goes to <paramref name="fallbackJoint"/>.</summary>
    public static void NormaliseWeights(int[] joints, float[] weights, int fallbackJoint)
    {
        for (int v = 0; v < weights.Length / Influences; v++)
        {
            int at = v * Influences;
            float sum = 0;
            for (int k = 0; k < Influences; k++)
                sum += MathF.Max(weights[at + k], 0);
            if (sum <= 1e-8f)
            {
                joints[at] = fallbackJoint;
                weights[at] = 1;
                for (int k = 1; k < Influences; k++)
                    weights[at + k] = 0;
                continue;
            }
            for (int k = 0; k < Influences; k++)
                weights[at + k] = MathF.Max(weights[at + k], 0) / sum;
        }
    }

    /// <summary>
    /// Adds one influence to a vertex's four, keeping the strongest four (used while gathering weights from a file).
    /// </summary>
    public static void AddInfluence(int[] joints, float[] weights, int vertex, int joint, float weight)
    {
        int at = vertex * Influences;
        int weakest = at;
        for (int k = 0; k < Influences; k++)
        {
            if (weights[at + k] > 0 && joints[at + k] == joint)
            {
                weights[at + k] += weight;
                return;
            }
            if (weights[at + k] < weights[weakest])
                weakest = at + k;
        }
        if (weight > weights[weakest])
        {
            joints[weakest] = joint;
            weights[weakest] = weight;
        }
    }
}

/// <summary>Something that poses a skeleton over time.</summary>
public abstract class AnimationClip
{
    public required string Name { get; init; }

    /// <summary>Length in seconds.</summary>
    public required float Duration { get; init; }

    /// <summary>
    /// Writes every joint's local matrix at time <paramref name="time"/> (seconds, clamped to the clip). Joints the clip
    /// does not animate get <paramref name="rest"/>.
    /// </summary>
    public abstract void Sample(Skeleton skeleton, float time, Matrix4x4[] rest, Matrix4x4[] locals);
}

/// <summary>Interpolation between keys, as glTF names it.</summary>
public enum KeyInterpolation { Linear, Step }

/// <summary>A clip made of translation, rotation and scale keys for named joints (glTF style).</summary>
public sealed class KeyframeClip : AnimationClip
{
    public sealed class Track
    {
        public required string Joint { get; init; }
        public float[]? TranslationTimes, RotationTimes, ScaleTimes;
        public Vector3[]? Translations, Scales;
        public Quaternion[]? Rotations;
        public KeyInterpolation TranslationMode, RotationMode, ScaleMode;
    }

    public required List<Track> Tracks { get; init; }

    private Skeleton? _boundTo;
    private int[] _jointOfTrack = Array.Empty<int>();

    public override void Sample(Skeleton skeleton, float time, Matrix4x4[] rest, Matrix4x4[] locals)
    {
        if (!ReferenceEquals(_boundTo, skeleton))
        {
            _jointOfTrack = Tracks.Select(t => skeleton.Find(t.Joint)).ToArray();
            _boundTo = skeleton;
        }
        Array.Copy(rest, locals, skeleton.Count);
        time = Math.Clamp(time, 0, Duration);
        for (int k = 0; k < Tracks.Count; k++)
        {
            int j = _jointOfTrack[k];
            if (j < 0)
                continue;
            var track = Tracks[k];
            Pose.Decompose(rest[j], out var s, out var r, out var t);
            if (track.Translations != null)
                t = SampleVector(track.TranslationTimes!, track.Translations, track.TranslationMode, time);
            if (track.Scales != null)
                s = SampleVector(track.ScaleTimes!, track.Scales, track.ScaleMode, time);
            if (track.Rotations != null)
                r = SampleRotation(track.RotationTimes!, track.Rotations, track.RotationMode, time);
            locals[j] = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        }
    }

    private static (int A, int B, float F) Find(float[] times, float time)
    {
        if (times.Length == 1 || time <= times[0])
            return (0, 0, 0);
        if (time >= times[^1])
            return (times.Length - 1, times.Length - 1, 0);
        int hi = Array.BinarySearch(times, time);
        if (hi >= 0)
            return (hi, hi, 0);
        hi = ~hi;
        int lo = hi - 1;
        float span = times[hi] - times[lo];
        return (lo, hi, span > 1e-9f ? (time - times[lo]) / span : 0);
    }

    private static Vector3 SampleVector(float[] times, Vector3[] values, KeyInterpolation mode, float time)
    {
        var (a, b, f) = Find(times, time);
        return mode == KeyInterpolation.Step ? values[a] : Vector3.Lerp(values[a], values[b], f);
    }

    private static Quaternion SampleRotation(float[] times, Quaternion[] values, KeyInterpolation mode, float time)
    {
        var (a, b, f) = Find(times, time);
        return mode == KeyInterpolation.Step ? values[a] : Quaternion.Normalize(Quaternion.Slerp(values[a], values[b], f));
    }
}

/// <summary>A clip computed from a function of time (the built-in poses).</summary>
public sealed class ProceduralClip : AnimationClip
{
    public required Action<float, Matrix4x4[], Matrix4x4[]> Evaluate { get; init; }

    public override void Sample(Skeleton skeleton, float time, Matrix4x4[] rest, Matrix4x4[] locals)
    {
        Array.Copy(rest, locals, skeleton.Count);
        Evaluate(Math.Clamp(time, 0, Duration), rest, locals);
    }
}

/// <summary>Blending of joint transforms.</summary>
public static class Pose
{
    public static void Decompose(Matrix4x4 m, out Vector3 scale, out Quaternion rotation, out Vector3 translation)
    {
        if (!Matrix4x4.Decompose(m, out scale, out rotation, out translation))
        {
            scale = Vector3.One;
            rotation = Quaternion.Identity;
            translation = m.Translation;
        }
    }

    /// <summary>Blends two local poses joint by joint: scale and translation linearly, rotation along the shorter arc.</summary>
    public static void Blend(Matrix4x4[] from, Matrix4x4[] to, float amount, Matrix4x4[] result)
    {
        for (int j = 0; j < result.Length; j++)
        {
            if (amount <= 0) { result[j] = from[j]; continue; }
            if (amount >= 1) { result[j] = to[j]; continue; }
            Decompose(from[j], out var s0, out var r0, out var t0);
            Decompose(to[j], out var s1, out var r1, out var t1);
            result[j] = Matrix4x4.CreateScale(Vector3.Lerp(s0, s1, amount))
                * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.Slerp(r0, r1, amount)))
                * Matrix4x4.CreateTranslation(Vector3.Lerp(t0, t1, amount));
        }
    }

    /// <summary>Smooth start and stop for blends (0..1 to 0..1).</summary>
    public static float Ease(float x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
}
