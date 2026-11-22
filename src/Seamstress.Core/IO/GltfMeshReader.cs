using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Seamstress.Core.IO;

/// <summary>
/// Reads the triangle meshes of a glTF 2.0 file (.gltf with its buffers, or .glb) in their rest pose, with each
/// node's transform applied. glTF is metres with Y up, which is what Seamstress uses.
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

            (byte[] Bytes, int Offset, int Stride, int Count, int Component, string Type) Accessor(int index)
            {
                var a = root.GetProperty("accessors")[index];
                var view = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
                int offset = (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                           + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
                int stride = view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : 0;
                return (buffers[view.GetProperty("buffer").GetInt32()], offset, stride, a.GetProperty("count").GetInt32(),
                    a.GetProperty("componentType").GetInt32(), a.GetProperty("type").GetString() ?? "");
            }

            var file = new MeshFile();
            var visited = new HashSet<int>();
            void Visit(int nodeIndex, Matrix4x4 parent)
            {
                if (!visited.Add(nodeIndex))
                    return;
                var node = root.GetProperty("nodes")[nodeIndex];
                var world = Local(node) * parent;
                if (node.TryGetProperty("mesh", out var meshIndex))
                {
                    var mesh = root.GetProperty("meshes")[meshIndex.GetInt32()];
                    string name = node.TryGetProperty("name", out var nn) ? nn.GetString() ?? ""
                        : mesh.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : $"mesh {meshIndex.GetInt32()}";
                    // A skinned mesh is placed by its joints; in the rest pose its vertices are already in the scene.
                    var placed = node.TryGetProperty("skin", out _) ? Matrix4x4.Identity : world;
                    if (!(include ?? FbxMeshReader.LooksLikeBody)(name))
                        file.Skipped.Add(name);
                    else if (ReadMesh(mesh, placed, Accessor) is { } part)
                        file.Parts.Add(part with { Name = name });
                }
                if (node.TryGetProperty("children", out var children))
                {
                    foreach (var child in children.EnumerateArray())
                        Visit(child.GetInt32(), world);
                }
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
            if (file.Parts.Count == 0)
            {
                throw new InvalidDataException(file.Skipped.Count > 0
                    ? $"No body mesh was found; the file only has: {string.Join(", ", file.Skipped)}."
                    : "The glTF file contains no triangle mesh.");
            }
            return file;
        }
    }

    private static MeshPart? ReadMesh(JsonElement mesh, Matrix4x4 placed,
        Func<int, (byte[] Bytes, int Offset, int Stride, int Count, int Component, string Type)> accessor)
    {
        var positions = new List<Vector3>();
        var triangles = new List<int>();
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
        return triangles.Count >= 3 ? new MeshPart("", positions.ToArray(), triangles.ToArray()) : null;
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
