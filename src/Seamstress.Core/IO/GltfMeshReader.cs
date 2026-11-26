using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Seamstress.Core.IO;

/// <summary>
/// Reads the triangle meshes of a glTF 2.0 file (.gltf with its buffers, or .glb) in their rest pose, with each
/// node's transform applied. glTF is metres with Y up, which is what Seamstress uses. Skinned meshes keep their
/// joints and weights, and the file their skeleton (<see cref="MeshFile.Skeleton"/>).
/// </summary>
public static class GltfMeshReader
{
    private const uint GlbMagic = 0x46546C67, JsonChunk = 0x4E4F534A, BinChunk = 0x004E4942;

    public static MeshFile Load(string path, Func<string, bool>? include = null)
    {
        byte[] data = File.ReadAllBytes(path);
        byte[]? binChunk = null;
        JsonDocument json;
        if (data.Length >= 20 && BitConverter.ToUInt32(data, 0) == GlbMagic)
        {
            int at = 12;
            string? text = null;
            while (at + 8 <= data.Length)
            {
                int length = BitConverter.ToInt32(data, at);
                uint kind = BitConverter.ToUInt32(data, at + 4);
                if (length < 0 || at + 8 + length > data.Length)
                    break;
                if (kind == JsonChunk)
                    text = Encoding.UTF8.GetString(data, at + 8, length);
                else if (kind == BinChunk)
                    binChunk = data.AsSpan(at + 8, length).ToArray();
                at += 8 + length;
            }
            json = JsonDocument.Parse(text ?? throw new InvalidDataException("The GLB file has no scene description."));
        }
        else
        {
            // Text editors and some exporters put a byte-order mark in front of the JSON.
            int skip = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            json = JsonDocument.Parse(data.AsMemory(skip));
        }

        using (json)
        {
            var root = json.RootElement;
            string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            var buffers = new List<byte[]>();
            if (root.TryGetProperty("buffers", out var bufferList))
            {
                foreach (var b in bufferList.EnumerateArray())
                {
                    if (!b.TryGetProperty("uri", out var uriEl))
                    {
                        buffers.Add(binChunk ?? throw new InvalidDataException("The GLB file has no binary data."));
                        continue;
                    }
                    string uri = uriEl.GetString() ?? "";
                    int comma = uri.IndexOf(',');
                    buffers.Add(uri.StartsWith("data:", StringComparison.Ordinal) && comma > 0
                        ? Convert.FromBase64String(uri[(comma + 1)..])
                        : File.ReadAllBytes(Path.Combine(folder, Uri.UnescapeDataString(uri))));
                }
            }

            Accessor Accessor(int index)
            {
                var a = root.GetProperty("accessors")[index];
                var view = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
                int offset = (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                           + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
                int stride = view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : 0;
                return new Accessor(buffers[view.GetProperty("buffer").GetInt32()], offset, stride, a.GetProperty("count").GetInt32(),
                    a.GetProperty("componentType").GetInt32(), a.GetProperty("type").GetString() ?? "");
            }

            var file = new MeshFile();
            var visited = new HashSet<int>();
            var parentOf = new Dictionary<int, int>();
            var worldOf = new Dictionary<int, Matrix4x4>();
            var skinned = new List<int>();                 // parts whose Skin joints are still node indices
            var bindOf = new Dictionary<int, Matrix4x4>(); // joint node -> bind pose, from its inverse bind matrix
            void Visit(int nodeIndex, Matrix4x4 parent)
            {
                if (!visited.Add(nodeIndex))
                    return;
                var node = root.GetProperty("nodes")[nodeIndex];
                var world = Local(node) * parent;
                worldOf[nodeIndex] = world;
                if (node.TryGetProperty("mesh", out var meshIndex))
                {
                    var mesh = root.GetProperty("meshes")[meshIndex.GetInt32()];
                    string name = node.TryGetProperty("name", out var nn) ? nn.GetString() ?? ""
                        : mesh.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : $"mesh {meshIndex.GetInt32()}";
                    // A skinned mesh is placed by its joints; in the rest pose its vertices are already in the scene.
                    var placed = node.TryGetProperty("skin", out _) ? Matrix4x4.Identity : world;
                    if (!(include ?? FbxMeshReader.LooksLikeBody)(name))
                        file.Skipped.Add(name);
                    else if (ReadMesh(mesh, placed, Accessor, SkinJoints(node)) is { } part)
                    {
                        if (part.Skin != null)
                            skinned.Add(file.Parts.Count);
                        file.Parts.Add(part with { Name = name });
                    }
                }
                if (node.TryGetProperty("children", out var children))
                {
                    foreach (var child in children.EnumerateArray())
                    {
                        parentOf.TryAdd(child.GetInt32(), nodeIndex);
                        Visit(child.GetInt32(), world);
                    }
                }
            }

            // The joints of the node's skin as node indices, remembering each one's bind pose; null without a skin.
            int[]? SkinJoints(JsonElement node)
            {
                if (!node.TryGetProperty("skin", out var skinIndex) || !root.TryGetProperty("skins", out var skins))
                    return null;
                var skin = skins[skinIndex.GetInt32()];
                int[] joints = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
                if (skin.TryGetProperty("inverseBindMatrices", out var ibmIndex))
                {
                    var ibm = Accessor(ibmIndex.GetInt32());
                    int step = ibm.Stride > 0 ? ibm.Stride : 64;
                    for (int k = 0; k < joints.Length && k < ibm.Count; k++)
                    {
                        var v = new float[16];
                        for (int e = 0; e < 16; e++)
                            v[e] = BitConverter.ToSingle(ibm.Bytes, ibm.Offset + k * step + 4 * e);
                        var inverse = new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
                        if (Matrix4x4.Invert(inverse, out var bind))
                            bindOf.TryAdd(joints[k], bind);
                    }
                }
                return joints;
            }

            if (root.TryGetProperty("scenes", out var scenes) && scenes.GetArrayLength() > 0)
            {
                int scene = root.TryGetProperty("scene", out var s) ? s.GetInt32() : 0;
                foreach (var n in scenes[Math.Clamp(scene, 0, scenes.GetArrayLength() - 1)].GetProperty("nodes").EnumerateArray())
                    Visit(n.GetInt32(), Matrix4x4.Identity);
            }
            else if (root.TryGetProperty("nodes", out var nodes))
            {
                for (int i = 0; i < nodes.GetArrayLength(); i++)
                    Visit(i, Matrix4x4.Identity);
            }
            if (skinned.Count > 0)
            {
                string? NameOf(int i) => root.GetProperty("nodes")[i].TryGetProperty("name", out var n) ? n.GetString() : null;
                BuildSkeleton(file, skinned, parentOf, worldOf, bindOf, NameOf);
            }
            if (file.Parts.Count == 0)
            {
                throw new InvalidDataException(file.Skipped.Count > 0
                    ? $"No body mesh was found; the file only has: {string.Join(", ", file.Skipped)}."
                    : "The glTF file contains no triangle mesh.");
            }
            return file;
        }
    }

    private readonly record struct Accessor(byte[] Bytes, int Offset, int Stride, int Count, int Component, string Type);

    /// <summary>
    /// Gathers the joints the skinned parts use, with all their ancestors, into one skeleton ordered parents first,
    /// and renumbers the parts' joints (node indices until now) to it.
    /// </summary>
    private static void BuildSkeleton(MeshFile file, List<int> skinned, Dictionary<int, int> parentOf,
        Dictionary<int, Matrix4x4> worldOf, Dictionary<int, Matrix4x4> bindOf, Func<int, string?> nameOf)
    {
        var needed = new HashSet<int>();
        foreach (int p in skinned)
        {
            var used = file.Parts[p].Skin!;
            foreach (int node in used.Joints.Where((_, k) => used.Weights[k] > 0).Distinct())
            {
                int n = node;
                for (int guard = 0; guard < 1024 && needed.Add(n) && parentOf.TryGetValue(n, out int up); guard++)
                    n = up;
            }
        }
        int Depth(int n)
        {
            int d = 0;
            while (d < 1024 && parentOf.TryGetValue(n, out n))
                d++;
            return d;
        }
        var order = needed.OrderBy(Depth).ThenBy(n => n).ToList();
        var indexOf = new Dictionary<int, int>();
        for (int i = 0; i < order.Count; i++)
            indexOf[order[i]] = i;
        var joints = order.Select(n => new Rigging.Joint(
            nameOf(n) is { Length: > 0 } name ? name : $"joint{n}",
            parentOf.TryGetValue(n, out int up) && indexOf.TryGetValue(up, out int parent) ? parent : -1,
            Rigging.Skeleton.Rigid(bindOf.TryGetValue(n, out var bind) ? bind : worldOf.GetValueOrDefault(n, Matrix4x4.Identity)))).ToList();
        file.Skeleton = new Rigging.Skeleton(joints);
        foreach (int p in skinned)
        {
            var skin = file.Parts[p].Skin!;
            // Unused slots (weight 0) hold 0, which is not necessarily a joint's node.
            var remapped = skin.Joints.Select((n, k) => skin.Weights[k] > 0 ? indexOf[n] : 0).ToArray();
            file.Parts[p] = file.Parts[p] with { Skin = new Rigging.VertexWeights(remapped, skin.Weights) };
        }
    }

    /// <param name="skinJoints">The node index of each joint of the node's skin, or null for a mesh that is not skinned.</param>
    private static MeshPart? ReadMesh(JsonElement mesh, Matrix4x4 placed, Func<int, Accessor> accessor, int[]? skinJoints)
    {
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        var influences = new List<List<(int, float)>>();
        bool anyWeights = false;
        foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
        {
            if (prim.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4)
                continue;
            if (!prim.GetProperty("attributes").TryGetProperty("POSITION", out var posIndex))
                continue;
            var p = accessor(posIndex.GetInt32());
            if (p.Component != 5126 || p.Type != "VEC3")
                continue;
            int first = positions.Count;
            int step = p.Stride > 0 ? p.Stride : 12;
            for (int i = 0; i < p.Count; i++)
            {
                int o = p.Offset + i * step;
                positions.Add(Vector3.Transform(new Vector3(BitConverter.ToSingle(p.Bytes, o),
                    BitConverter.ToSingle(p.Bytes, o + 4), BitConverter.ToSingle(p.Bytes, o + 8)), placed));
            }
            if (skinJoints != null)
                anyWeights |= ReadWeights(prim.GetProperty("attributes"), p.Count, skinJoints, accessor, influences);
            if (prim.TryGetProperty("indices", out var indexAccessor))
            {
                var ix = accessor(indexAccessor.GetInt32());
                int size = ix.Component switch { 5121 => 1, 5123 => 2, _ => 4 };
                int istep = ix.Stride > 0 ? ix.Stride : size;
                for (int i = 0; i < ix.Count; i++)
                {
                    int o = ix.Offset + i * istep;
                    triangles.Add(first + (size == 1 ? ix.Bytes[o] : size == 2 ? BitConverter.ToUInt16(ix.Bytes, o) : (int)BitConverter.ToUInt32(ix.Bytes, o)));
                }
            }
            else
            {
                for (int i = 0; i + 2 < p.Count; i += 3)
                {
                    triangles.Add(first + i);
                    triangles.Add(first + i + 1);
                    triangles.Add(first + i + 2);
                }
            }
            triangles.RemoveRange(triangles.Count - triangles.Count % 3, triangles.Count % 3);
        }
        if (triangles.Count < 3)
            return null;
        var part = new MeshPart("", positions.ToArray(), triangles.ToArray());
        if (skinJoints == null || !anyWeights || skinJoints.Length == 0)
            return part;
        // Joints stay node indices until the whole skeleton is known.
        return part with { Skin = Rigging.VertexWeights.FromInfluences(influences, _ => skinJoints[0]) };
    }

    /// <summary>Appends the JOINTS_n/WEIGHTS_n influences of <paramref name="count"/> vertices, as node indices.</summary>
    private static bool ReadWeights(JsonElement attributes, int count, int[] skinJoints, Func<int, Accessor> accessor,
        List<List<(int, float)>> influences)
    {
        int first = influences.Count;
        for (int i = 0; i < count; i++)
            influences.Add(new List<(int, float)>(4));
        bool found = false;
        for (int set = 0; set < 4; set++)
        {
            if (!attributes.TryGetProperty($"JOINTS_{set}", out var ji) || !attributes.TryGetProperty($"WEIGHTS_{set}", out var wi))
                break;
            var j = accessor(ji.GetInt32());
            var w = accessor(wi.GetInt32());
            if (j.Type != "VEC4" || w.Type != "VEC4")
                continue;
            int jSize = j.Component == 5121 ? 1 : 2, wSize = w.Component switch { 5121 => 1, 5123 => 2, _ => 4 };
            int jStep = j.Stride > 0 ? j.Stride : 4 * jSize, wStep = w.Stride > 0 ? w.Stride : 4 * wSize;
            for (int v = 0; v < count && v < j.Count && v < w.Count; v++)
            {
                for (int k = 0; k < 4; k++)
                {
                    int jo = j.Offset + v * jStep + k * jSize, wo = w.Offset + v * wStep + k * wSize;
                    int joint = jSize == 1 ? j.Bytes[jo] : BitConverter.ToUInt16(j.Bytes, jo);
                    float weight = wSize switch
                    {
                        1 => w.Bytes[wo] / 255f,
                        2 => BitConverter.ToUInt16(w.Bytes, wo) / 65535f,
                        _ => BitConverter.ToSingle(w.Bytes, wo),
                    };
                    if (weight > 0 && joint < skinJoints.Length)
                        influences[first + v].Add((skinJoints[joint], weight));
                }
            }
            found = true;
        }
        return found;
    }

    /// <summary>A node's transform as a row-vector matrix (glTF stores column-major matrices, which read the same).</summary>
    private static Matrix4x4 Local(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var m) && m.GetArrayLength() == 16)
        {
            float[] v = m.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        }
        var scale = node.TryGetProperty("scale", out var s) ? new Vector3(s[0].GetSingle(), s[1].GetSingle(), s[2].GetSingle()) : Vector3.One;
        var rotation = node.TryGetProperty("rotation", out var r)
            ? new Quaternion(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle()) : Quaternion.Identity;
        var translation = node.TryGetProperty("translation", out var t) ? new Vector3(t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle()) : Vector3.Zero;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
    }
}
