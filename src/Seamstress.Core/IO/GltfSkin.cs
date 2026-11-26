using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Seamstress.Core.Rigging;

namespace Seamstress.Core.IO;

/// <summary>The skinning parts of a glTF file: joint nodes, the skin with its inverse bind matrices, and the
/// JOINTS_0 / WEIGHTS_0 vertex attributes.</summary>
internal static class GltfSkin
{
    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Adds JOINTS_0 and WEIGHTS_0 accessors for glTF vertices made from the given positions.</summary>
    /// <returns>The attribute entries to append inside a primitive's "attributes" object.</returns>
    public static string VertexAttributes(VertexWeights skin, IReadOnlyList<int> sourcePositions, Func<byte[], int?, int> addView, List<string> accessors)
    {
        int count = sourcePositions.Count;
        var joints = new ushort[count * 4];
        var weights = new float[count * 4];
        for (int v = 0; v < count; v++)
        {
            int at = sourcePositions[v] * VertexWeights.MaxInfluences;
            for (int k = 0; k < 4; k++)
            {
                weights[v * 4 + k] = skin.Weights[at + k];
                joints[v * 4 + k] = skin.Weights[at + k] > 0 ? (ushort)skin.Joints[at + k] : (ushort)0;
            }
        }
        int jointAccessor = accessors.Count;
        accessors.Add($"{{\"bufferView\":{addView(MemoryMarshal.AsBytes(joints.AsSpan()).ToArray(), 34962)},\"componentType\":5123,\"count\":{count},\"type\":\"VEC4\"}}");
        accessors.Add($"{{\"bufferView\":{addView(MemoryMarshal.AsBytes(weights.AsSpan()).ToArray(), 34962)},\"componentType\":5126,\"count\":{count},\"type\":\"VEC4\"}}");
        return $",\"JOINTS_0\":{jointAccessor},\"WEIGHTS_0\":{jointAccessor + 1}";
    }

    /// <summary>
    /// One node per joint (numbered from <paramref name="firstNode"/>, in skeleton order) holding its bind-pose local
    /// transform, and the skin that lists them with their inverse bind matrices.
    /// </summary>
    public static (List<string> Nodes, string Skin, List<int> Roots) Skeleton(Skeleton skeleton, int firstNode,
        Func<byte[], int?, int> addView, List<string> accessors)
    {
        var children = new List<int>[skeleton.Count];
        for (int j = 0; j < skeleton.Count; j++)
            children[j] = new List<int>();
        var roots = new List<int>();
        for (int j = 0; j < skeleton.Count; j++)
        {
            int parent = skeleton.Joints[j].Parent;
            if (parent < 0)
                roots.Add(firstNode + j);
            else
                children[parent].Add(firstNode + j);
        }

        var nodes = new List<string>();
        var inverseBind = new float[skeleton.Count * 16];
        for (int j = 0; j < skeleton.Count; j++)
        {
            Matrix4x4.Decompose(skeleton.BindLocal(j), out _, out var r, out var t);
            r = Quaternion.Normalize(r);
            string kids = children[j].Count > 0 ? $",\"children\":[{string.Join(",", children[j])}]" : "";
            nodes.Add($"{{\"name\":{JsonSerializer.Serialize(skeleton.Joints[j].Name)},\"translation\":[{F(t.X)},{F(t.Y)},{F(t.Z)}],"
                + $"\"rotation\":[{F(r.X)},{F(r.Y)},{F(r.Z)},{F(r.W)}]{kids}}}");
            // glTF matrices are column-major for column vectors, which is the row-vector matrix row by row.
            var m = skeleton.InverseBind(j);
            float[] values =
            {
                m.M11, m.M12, m.M13, 0, m.M21, m.M22, m.M23, 0, m.M31, m.M32, m.M33, 0, m.M41, m.M42, m.M43, 1,
            };
            values.CopyTo(inverseBind, j * 16);
        }
        int ibm = accessors.Count;
        accessors.Add($"{{\"bufferView\":{addView(MemoryMarshal.AsBytes(inverseBind.AsSpan()).ToArray(), null)},\"componentType\":5126,\"count\":{skeleton.Count},\"type\":\"MAT4\"}}");
        string joints = string.Join(",", Enumerable.Range(firstNode, skeleton.Count));
        // "skeleton" names the common root, which only exists when there is one root joint.
        string root = roots.Count == 1 ? $",\"skeleton\":{roots[0]}" : "";
        string skin = $"{{\"name\":\"Armature\",\"inverseBindMatrices\":{ibm}{root},\"joints\":[{joints}]}}";
        return (nodes, skin, roots);
    }
}
