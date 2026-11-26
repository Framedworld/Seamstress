using System.Numerics;

namespace Seamstress.Core.Rigging;

/// <summary>One bone of a <see cref="Skeleton"/>.</summary>
/// <param name="Name">The joint's name in the source file, which game engines match animations by.</param>
/// <param name="Parent">Index of the parent joint, or -1 for a root. Always lower than the joint's own index.</param>
/// <param name="BindWorld">Where the joint is in the bind pose: a rotation and translation (no scale), row-vector
/// convention, in the space of the mesh it deforms (for an avatar: metres, Y up, standing on the ground).</param>
public sealed record Joint(string Name, int Parent, Matrix4x4 BindWorld);

/// <summary>The joint hierarchy of a rigged body, in its bind pose.</summary>
public sealed class Skeleton
{
    public Skeleton(IReadOnlyList<Joint> joints)
    {
        for (int i = 0; i < joints.Count; i++)
        {
            if (joints[i].Parent >= i || joints[i].Parent < -1)
                throw new ArgumentException($"Joint '{joints[i].Name}' must come after its parent.", nameof(joints));
        }
        Joints = joints;
    }

    public IReadOnlyList<Joint> Joints { get; }

    public int Count => Joints.Count;

    public int IndexOf(string name)
    {
        for (int i = 0; i < Joints.Count; i++)
        {
            if (Joints[i].Name == name)
                return i;
        }
        return -1;
    }

    /// <summary>The joint's bind transform relative to its parent (its own bind transform for a root).</summary>
    public Matrix4x4 BindLocal(int joint)
    {
        var j = Joints[joint];
        if (j.Parent < 0)
            return j.BindWorld;
        Matrix4x4.Invert(Joints[j.Parent].BindWorld, out var parentInverse);
        return j.BindWorld * parentInverse;
    }

    /// <summary>The matrix that takes a bind-pose point into the joint's own space (glTF's inverse bind matrix).</summary>
    public Matrix4x4 InverseBind(int joint)
    {
        Matrix4x4.Invert(Joints[joint].BindWorld, out var inverse);
        return inverse;
    }

    /// <summary>
    /// World transforms of every joint after replacing some local transforms, e.g. rotating the elbow. Joints not in
    /// <paramref name="localOverrides"/> keep their bind-pose local transform.
    /// </summary>
    public Matrix4x4[] Pose(IReadOnlyDictionary<int, Matrix4x4> localOverrides)
    {
        var world = new Matrix4x4[Count];
        for (int i = 0; i < Count; i++)
        {
            var local = localOverrides.TryGetValue(i, out var o) ? o : BindLocal(i);
            world[i] = Joints[i].Parent < 0 ? local : local * world[Joints[i].Parent];
        }
        return world;
    }

    /// <summary>
    /// Moves the skeleton by <paramref name="transform"/> (e.g. from file units to Seamstress space). Scale is
    /// dropped from the result so every joint stays a pure rotation and translation; positions still follow it.
    /// </summary>
    public Skeleton Transformed(Matrix4x4 transform)
        => new(Joints.Select(j => j with { BindWorld = Rigid(j.BindWorld * transform) }).ToList());

    /// <summary>The rotation and translation of <paramref name="m"/>, with any scale or shear removed.</summary>
    public static Matrix4x4 Rigid(Matrix4x4 m)
    {
        var translation = m.Translation;
        // Orthonormalise the rows (Gram-Schmidt), then make it right-handed: mirrored axes become a rotation.
        var x = new Vector3(m.M11, m.M12, m.M13);
        var y = new Vector3(m.M21, m.M22, m.M23);
        x = x.LengthSquared() > 1e-20f ? Vector3.Normalize(x) : Vector3.UnitX;
        y -= x * Vector3.Dot(x, y);
        y = y.LengthSquared() > 1e-20f ? Vector3.Normalize(y) : Perpendicular(x);
        var z = Vector3.Cross(x, y);
        return new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            translation.X, translation.Y, translation.Z, 1);
    }

    private static Vector3 Perpendicular(Vector3 v)
        => Vector3.Normalize(Vector3.Cross(v, MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
}

/// <summary>Up to four joint influences per vertex, with weights that sum to one.</summary>
public sealed class VertexWeights
{
    public const int MaxInfluences = 4;

    /// <param name="joints">Four joint indices per vertex; unused slots are 0 with weight 0.</param>
    /// <param name="weights">Four weights per vertex, parallel to <paramref name="joints"/>.</param>
    public VertexWeights(int[] joints, float[] weights)
    {
        if (joints.Length != weights.Length || joints.Length % MaxInfluences != 0)
            throw new ArgumentException("Joints and weights need four entries per vertex.");
        Joints = joints;
        Weights = weights;
    }

    public int[] Joints { get; }
    public float[] Weights { get; }
    public int VertexCount => Joints.Length / MaxInfluences;

    /// <summary>
    /// Builds weights from any number of (joint, weight) influences per vertex: the strongest four are kept and
    /// normalised. A vertex with no influence at all is bound fully to <paramref name="fallbackJoint"/>.
    /// </summary>
    public static VertexWeights FromInfluences(IReadOnlyList<IEnumerable<(int Joint, float Weight)>> perVertex, Func<int, int>? fallbackJoint = null)
    {
        var joints = new int[perVertex.Count * MaxInfluences];
        var weights = new float[perVertex.Count * MaxInfluences];
        for (int v = 0; v < perVertex.Count; v++)
            Pack(perVertex[v], v, joints, weights, fallbackJoint?.Invoke(v) ?? 0);
        return new VertexWeights(joints, weights);
    }

    /// <summary>Writes the strongest four of <paramref name="influences"/>, normalised, into slot <paramref name="vertex"/>.</summary>
    internal static void Pack(IEnumerable<(int Joint, float Weight)> influences, int vertex, int[] joints, float[] weights, int fallbackJoint)
    {
        // Merge repeats of the same joint before choosing the strongest.
        var merged = new Dictionary<int, float>();
        foreach (var (joint, weight) in influences)
        {
            if (weight > 0 && float.IsFinite(weight))
                merged[joint] = merged.GetValueOrDefault(joint) + weight;
        }
        var top = merged.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(MaxInfluences).ToList();
        float sum = top.Sum(kv => kv.Value);
        int at = vertex * MaxInfluences;
        for (int k = 0; k < MaxInfluences; k++)
        {
            joints[at + k] = 0;
            weights[at + k] = 0;
        }
        if (sum <= 1e-12f)
        {
            joints[at] = fallbackJoint;
            weights[at] = 1;
            return;
        }
        for (int k = 0; k < top.Count; k++)
        {
            joints[at + k] = top[k].Key;
            weights[at + k] = top[k].Value / sum;
        }
    }

    public IEnumerable<(int Joint, float Weight)> Of(int vertex)
    {
        for (int k = 0; k < MaxInfluences; k++)
        {
            float w = Weights[vertex * MaxInfluences + k];
            if (w > 0)
                yield return (Joints[vertex * MaxInfluences + k], w);
        }
    }

    /// <summary>The same weights for a subset or reordering of the vertices.</summary>
    public VertexWeights Select(IReadOnlyList<int> vertices)
    {
        var joints = new int[vertices.Count * MaxInfluences];
        var weights = new float[vertices.Count * MaxInfluences];
        for (int i = 0; i < vertices.Count; i++)
        {
            Array.Copy(Joints, vertices[i] * MaxInfluences, joints, i * MaxInfluences, MaxInfluences);
            Array.Copy(Weights, vertices[i] * MaxInfluences, weights, i * MaxInfluences, MaxInfluences);
        }
        return new VertexWeights(joints, weights);
    }
}

/// <summary>The skeleton of a rigged avatar and the skin weights of each of its mesh vertices.</summary>
public sealed class AvatarRig
{
    public required Skeleton Skeleton { get; init; }

    /// <summary>One entry per vertex of the avatar's mesh, in the same order.</summary>
    public required VertexWeights Weights { get; init; }
}

/// <summary>Linear blend skinning, as game engines do it.</summary>
public static class Skinning
{
    /// <summary>Moves bind-pose <paramref name="positions"/> to where the posed joints carry them.</summary>
    /// <param name="poseWorld">World transform of every joint, e.g. from <see cref="Skeleton.Pose"/>.</param>
    public static Vector3[] Deform(Vector3[] positions, VertexWeights weights, Skeleton skeleton, Matrix4x4[] poseWorld)
    {
        var skin = new Matrix4x4[skeleton.Count];
        for (int j = 0; j < skin.Length; j++)
            skin[j] = skeleton.InverseBind(j) * poseWorld[j];
        var result = new Vector3[positions.Length];
        for (int v = 0; v < positions.Length; v++)
        {
            var p = Vector3.Zero;
            foreach (var (joint, weight) in weights.Of(v))
                p += Vector3.Transform(positions[v], skin[joint]) * weight;
            result[v] = p;
        }
        return result;
    }
}
