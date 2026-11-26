using System.Numerics;
using Seamstress.Core.Rigging;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.IO;

/// <summary>One named mesh read from an FBX or glTF file, already in Seamstress space: metres, Y up, facing +Z.</summary>
public sealed record MeshPart(string Name, Vector3[] Positions, int[] Triangles)
{
    /// <summary>Skin weights of each position, with joints indexing <see cref="MeshFile.Skeleton"/>; null when the mesh is not skinned.</summary>
    public VertexWeights? Skin { get; init; }
}

/// <summary>The meshes of a body file that were kept, and the names of those that were left out.</summary>
public sealed class MeshFile
{
    public List<MeshPart> Parts { get; } = new();
    public List<string> Skipped { get; } = new();

    /// <summary>The joints the kept meshes are skinned to, in the same space as the parts; null when none is.</summary>
    public Skeleton? Skeleton { get; set; }

    public TriMesh ToTriMesh(float scale = 1f)
    {
        if (Parts.Count == 0)
            throw new InvalidDataException("The file contains no mesh that can be used as a body.");
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        foreach (var part in Parts)
        {
            int offset = positions.Count;
            foreach (var p in part.Positions)
                positions.Add(p * scale);
            foreach (int index in part.Triangles)
                triangles.Add(offset + index);
        }
        return new TriMesh { Positions = positions.ToArray(), Triangles = triangles.ToArray() };
    }

    /// <summary>
    /// The skeleton and the weights of every vertex of <see cref="ToTriMesh"/>, moved by <paramref name="transform"/>
    /// (the same scale and offset given to the mesh). A part without skin follows the joint nearest to it.
    /// Null when the file has no skinned mesh.
    /// </summary>
    public AvatarRig? ToRig(Matrix4x4 transform)
    {
        if (Skeleton is not { Count: > 0 } skeleton || !Parts.Any(p => p.Skin != null))
            return null;
        int total = Parts.Sum(p => p.Positions.Length);
        var joints = new int[total * VertexWeights.MaxInfluences];
        var weights = new float[total * VertexWeights.MaxInfluences];
        int at = 0;
        foreach (var part in Parts)
        {
            if (part.Skin is { } skin && skin.VertexCount == part.Positions.Length)
            {
                Array.Copy(skin.Joints, 0, joints, at * VertexWeights.MaxInfluences, skin.Joints.Length);
                Array.Copy(skin.Weights, 0, weights, at * VertexWeights.MaxInfluences, skin.Weights.Length);
            }
            else
            {
                // Eyes, teeth and other rigid props: bind the whole part to the joint nearest its centre.
                var centre = part.Positions.Aggregate(Vector3.Zero, (s, p) => s + p) / Math.Max(1, part.Positions.Length);
                int nearest = 0;
                float best = float.MaxValue;
                for (int j = 0; j < skeleton.Count; j++)
                {
                    float d = Vector3.DistanceSquared(skeleton.Joints[j].BindWorld.Translation, centre);
                    if (d < best)
                    {
                        best = d;
                        nearest = j;
                    }
                }
                for (int v = 0; v < part.Positions.Length; v++)
                {
                    joints[(at + v) * VertexWeights.MaxInfluences] = nearest;
                    weights[(at + v) * VertexWeights.MaxInfluences] = 1;
                }
            }
            at += part.Positions.Length;
        }
        return new AvatarRig { Skeleton = skeleton.Transformed(transform), Weights = new VertexWeights(joints, weights) };
    }
}
