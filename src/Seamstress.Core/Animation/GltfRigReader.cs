using System.Numerics;
using System.Text;
using System.Text.Json;
using Seamstress.Core.IO;

namespace Seamstress.Core.Animation;

/// <summary>What a rigged file holds: the skinned body (when it has a mesh) and its animation clips.</summary>
public sealed class RigFile
{
    public SkinnedBody? Body { get; init; }
    public Skeleton? Skeleton { get; init; }
    public List<AnimationClip> Clips { get; } = new();

    /// <summary>Reads a glTF/GLB or binary FBX file. <paramref name="scale"/> multiplies the file's own units, as for avatars.</summary>
    public static RigFile Load(string path, float scale = 1f, Func<string, bool>? includeMesh = null)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".glb" or ".gltf" => GltfRigReader.Load(path, scale, includeMesh),
            ".fbx" => FbxRigReader.Load(path, scale, includeMesh),
            _ => throw new InvalidDataException($"Animations are read from FBX or glTF/GLB files, not '{ext}'."),
        };
    }

    /// <summary>Translation that stands a mesh on the ground plane, centred in X, exactly as avatars are loaded.</summary>
    internal static Vector3 GroundOffset(IEnumerable<Vector3> positions)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return new Vector3(-(min.X + max.X) / 2, -min.Y, 0);
    }
}

/// <summary>
/// Reads a glTF 2.0 file's node hierarchy, skins and animations. The meshes are gathered in the same order as
/// <see cref="GltfMeshReader"/> does, so the skinned vertices line up one to one with the avatar loaded from the file.
/// </summary>
public static class GltfRigReader
{
    private const uint GlbMagic = 0x46546C67, JsonChunk = 0x4E4F534A, BinChunk = 0x004E4942;

    private readonly record struct View(byte[] Bytes, int Offset, int Stride, int Count, int Component, string Type, bool Normalized);

    public static RigFile Load(string path, float scale = 1f, Func<string, bool>? includeMesh = null)
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

            View Accessor(int index)
            {
                var a = root.GetProperty("accessors")[index];
                var view = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
                int offset = (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                           + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
                int stride = view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : 0;
                return new View(buffers[view.GetProperty("buffer").GetInt32()], offset, stride, a.GetProperty("count").GetInt32(),
                    a.GetProperty("componentType").GetInt32(), a.GetProperty("type").GetString() ?? "",
                    a.TryGetProperty("normalized", out var nm) && nm.GetBoolean());
            }

            // ---- the node hierarchy as a skeleton (parents first)
            var nodes = root.TryGetProperty("nodes", out var nodeList) ? nodeList.EnumerateArray().ToList() : new List<JsonElement>();
            int nodeCount = nodes.Count;
            var parentOf = Enumerable.Repeat(-1, nodeCount).ToArray();
            for (int i = 0; i < nodeCount; i++)
            {
                if (nodes[i].TryGetProperty("children", out var children))
                {
                    foreach (var c in children.EnumerateArray())
                    {
                        int child = c.GetInt32();
                        if (child >= 0 && child < nodeCount && parentOf[child] < 0 && child != i)
                            parentOf[child] = i;
                    }
                }
            }
            var order = new List<int>(nodeCount);
            var placed = new bool[nodeCount];
            void Place(int n, int depth)
            {
                if (placed[n] || depth > 512)
                    return;
                if (parentOf[n] >= 0 && !placed[parentOf[n]])
                    Place(parentOf[n], depth + 1);
                if (placed[n])
                    return;
                placed[n] = true;
                order.Add(n);
            }
            for (int i = 0; i < nodeCount; i++)
                Place(i, 0);
            var jointOfNode = new int[nodeCount];
            for (int k = 0; k < order.Count; k++)
                jointOfNode[order[k]] = k;
            var names = new string[nodeCount];
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k < order.Count; k++)
            {
                var node = nodes[order[k]];
                string name = node.TryGetProperty("name", out var nn) && !string.IsNullOrEmpty(nn.GetString()) ? nn.GetString()! : $"node {order[k]}";
                string unique = name;
                for (int n = 2; !used.Add(unique); n++)
                    unique = $"{name} #{n}";
                names[k] = unique;
            }
            var skeleton = new Skeleton
            {
                Names = names,
                Parents = order.Select(n => parentOf[n] >= 0 ? jointOfNode[parentOf[n]] : -1).ToArray(),
                RestLocal = order.Select(n => Local(nodes[n])).ToArray(),
            };
            var restWorld = new Matrix4x4[nodeCount];
            skeleton.World(skeleton.RestLocal, restWorld);

            // ---- the meshes, in GltfMeshReader's order. inverseBind is by joint, inverseBindSet by node.
            var inverseBind = Enumerable.Repeat(Matrix4x4.Identity, nodeCount).ToArray();
            var inverseBindSet = new bool[nodeCount];
            var bind = new List<Vector3>();
            var tris = new List<int>();
            var joints = new List<int>();
            var weights = new List<float>();
            bool anySkin = false;
            var visited = new HashSet<int>();
            void Visit(int nodeIndex)
            {
                if (nodeIndex < 0 || nodeIndex >= nodeCount || !visited.Add(nodeIndex))
                    return;
                var node = nodes[nodeIndex];
                if (node.TryGetProperty("mesh", out var meshIndex))
                {
                    var mesh = root.GetProperty("meshes")[meshIndex.GetInt32()];
                    string name = node.TryGetProperty("name", out var nn) ? nn.GetString() ?? ""
                        : mesh.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : $"mesh {meshIndex.GetInt32()}";
                    if ((includeMesh ?? FbxMeshReader.LooksLikeBody)(name))
                    {
                        int[]? skinJoints = null;
                        if (node.TryGetProperty("skin", out var skinIndex))
                        {
                            var skin = root.GetProperty("skins")[skinIndex.GetInt32()];
                            skinJoints = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
                            Matrix4x4[]? ibm = skin.TryGetProperty("inverseBindMatrices", out var ibmIndex)
                                ? ReadMatrices(Accessor(ibmIndex.GetInt32())) : null;
                            for (int k = 0; k < skinJoints.Length; k++)
                            {
                                int jn = skinJoints[k];
                                if (jn < 0 || jn >= nodeCount || inverseBindSet[jn])
                                    continue;
                                inverseBindSet[jn] = true;
                                inverseBind[jointOfNode[jn]] = ibm != null && k < ibm.Length ? ibm[k]
                                    : Matrix4x4.Invert(restWorld[jointOfNode[jn]], out var inv) ? inv : Matrix4x4.Identity;
                            }
                            anySkin = true;
                        }
                        ReadMesh(mesh, nodeIndex, skinJoints);
                    }
                }
                if (node.TryGetProperty("children", out var children))
                {
                    foreach (var child in children.EnumerateArray())
                        Visit(child.GetInt32());
                }
            }

            void ReadMesh(JsonElement mesh, int nodeIndex, int[]? skinJoints)
            {
                int self = jointOfNode[nodeIndex];
                var world = restWorld[self];
                if (skinJoints == null && !inverseBindSet[nodeIndex])
                {
                    // A mesh without a skin rides on its own node: bound rigidly in its rest place.
                    inverseBindSet[nodeIndex] = true;
                    inverseBind[self] = Matrix4x4.Invert(world, out var inv) ? inv : Matrix4x4.Identity;
                }
                var partPositions = new List<Vector3>();
                var partTris = new List<int>();
                foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
                {
                    if (prim.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4)
                        continue;
                    var attributes = prim.GetProperty("attributes");
                    if (!attributes.TryGetProperty("POSITION", out var posIndex))
                        continue;
                    var p = Accessor(posIndex.GetInt32());
                    if (p.Component != 5126 || p.Type != "VEC3")
                        continue;
                    int first = partPositions.Count;
                    int step = p.Stride > 0 ? p.Stride : 12;
                    for (int i = 0; i < p.Count; i++)
                    {
                        int o = p.Offset + i * step;
                        var v = new Vector3(BitConverter.ToSingle(p.Bytes, o), BitConverter.ToSingle(p.Bytes, o + 4), BitConverter.ToSingle(p.Bytes, o + 8));
                        partPositions.Add(skinJoints != null ? v : Vector3.Transform(v, world));
                    }
                    int[]? jointData = null;
                    float[]? weightData = null;
                    if (skinJoints != null && attributes.TryGetProperty("JOINTS_0", out var jIndex) && attributes.TryGetProperty("WEIGHTS_0", out var wIndex))
                    {
                        jointData = ReadComponents(Accessor(jIndex.GetInt32()), 4, out _);
                        weightData = ReadFloats(Accessor(wIndex.GetInt32()), 4);
                    }
                    for (int i = 0; i < p.Count; i++)
                    {
                        for (int k = 0; k < SkinnedBody.Influences; k++)
                        {
                            int joint = self;
                            float w = k == 0 ? 1 : 0;
                            if (jointData != null && weightData != null && i * 4 + k < jointData.Length && i * 4 + k < weightData.Length)
                            {
                                int index = jointData[i * 4 + k];
                                w = weightData[i * 4 + k];
                                joint = index >= 0 && index < skinJoints!.Length && skinJoints[index] < nodeCount ? jointOfNode[skinJoints[index]] : self;
                            }
                            joints.Add(joint);
                            weights.Add(w);
                        }
                    }
                    if (prim.TryGetProperty("indices", out var indexAccessor))
                    {
                        var ix = ReadComponents(Accessor(indexAccessor.GetInt32()), 1, out _);
                        foreach (int index in ix)
                            partTris.Add(first + index);
                    }
                    else
                    {
                        for (int i = 0; i + 2 < p.Count; i += 3)
                        {
                            partTris.Add(first + i);
                            partTris.Add(first + i + 1);
                            partTris.Add(first + i + 2);
                        }
                    }
                    partTris.RemoveRange(partTris.Count - partTris.Count % 3, partTris.Count % 3);
                }
                if (partTris.Count < 3)
                {
                    // GltfMeshReader leaves such a part out; so do the weights gathered for it.
                    int extra = partPositions.Count * SkinnedBody.Influences;
                    joints.RemoveRange(joints.Count - extra, extra);
                    weights.RemoveRange(weights.Count - extra, extra);
                    return;
                }
                int offset = bind.Count;
                bind.AddRange(partPositions);
                tris.AddRange(partTris.Select(t => t + offset));
            }

            if (root.TryGetProperty("scenes", out var scenes) && scenes.GetArrayLength() > 0)
            {
                int scene = root.TryGetProperty("scene", out var s) ? s.GetInt32() : 0;
                foreach (var n in scenes[Math.Clamp(scene, 0, scenes.GetArrayLength() - 1)].GetProperty("nodes").EnumerateArray())
                    Visit(n.GetInt32());
            }
            else
            {
                for (int i = 0; i < nodeCount; i++)
                    Visit(i);
            }

            SkinnedBody? body = null;
            if (bind.Count > 0)
            {
                var jointArray = joints.ToArray();
                var weightArray = weights.ToArray();
                SkinnedBody.NormaliseWeights(jointArray, weightArray, 0);
                var toScaled = Matrix4x4.CreateScale(scale);
                var offset = RigFile.GroundOffset(bind.Select(p => Vector3.Transform(p, toScaled)));
                body = new SkinnedBody
                {
                    Skeleton = skeleton,
                    BindPositions = bind.ToArray(),
                    Triangles = tris.ToArray(),
                    Joints = jointArray,
                    Weights = weightArray,
                    InverseBind = inverseBind,
                    ToAvatar = toScaled * Matrix4x4.CreateTranslation(offset),
                    Source = path + (anySkin ? "" : " (no skin: meshes ride on their nodes)"),
                };
            }

            var file = new RigFile { Body = body, Skeleton = skeleton };
            if (root.TryGetProperty("animations", out var animations))
            {
                int index = 0;
                foreach (var anim in animations.EnumerateArray())
                {
                    var clip = ReadAnimation(anim, index++, Accessor, nodeCount, jointOfNode, names);
                    if (clip != null)
                        file.Clips.Add(clip);
                }
            }
            return file;
        }
    }

    private static KeyframeClip? ReadAnimation(JsonElement anim, int index, Func<int, View> accessor, int nodeCount, int[] jointOfNode, string[] names)
    {
        if (!anim.TryGetProperty("channels", out var channels) || !anim.TryGetProperty("samplers", out var samplers))
            return null;
        var tracks = new Dictionary<int, KeyframeClip.Track>();
        float duration = 0;
        foreach (var ch in channels.EnumerateArray())
        {
            var target = ch.GetProperty("target");
            if (!target.TryGetProperty("node", out var nodeEl))
                continue;
            int node = nodeEl.GetInt32();
            if (node < 0 || node >= nodeCount)
                continue;
            string pathName = target.GetProperty("path").GetString() ?? "";
            if (pathName is not ("translation" or "rotation" or "scale"))
                continue;
            var sampler = samplers[ch.GetProperty("sampler").GetInt32()];
            string interp = sampler.TryGetProperty("interpolation", out var ie) ? ie.GetString() ?? "LINEAR" : "LINEAR";
            var times = ReadFloats(accessor(sampler.GetProperty("input").GetInt32()), 1);
            if (times.Length == 0)
                continue;
            int width = pathName == "rotation" ? 4 : 3;
            var values = ReadFloats(accessor(sampler.GetProperty("output").GetInt32()), width);
            bool cubic = interp == "CUBICSPLINE";
            int keys = times.Length;
            // Cubic splines store in-tangent, value, out-tangent per key: only the values are used, joined linearly.
            int stride = cubic ? 3 : 1;
            if (values.Length < keys * width * stride)
                keys = values.Length / (width * stride);
            if (keys == 0)
                continue;
            duration = MathF.Max(duration, times[keys - 1]);
            int joint = jointOfNode[node];
            if (!tracks.TryGetValue(joint, out var track))
                tracks[joint] = track = new KeyframeClip.Track { Joint = names[joint] };
            var mode = interp == "STEP" ? KeyInterpolation.Step : KeyInterpolation.Linear;
            int Base(int k) => (k * stride + (cubic ? 1 : 0)) * width;
            var t = times.Take(keys).ToArray();
            if (pathName == "rotation")
            {
                track.RotationTimes = t;
                track.RotationMode = mode;
                track.Rotations = Enumerable.Range(0, keys).Select(k => Quaternion.Normalize(new Quaternion(
                    values[Base(k)], values[Base(k) + 1], values[Base(k) + 2], values[Base(k) + 3]))).ToArray();
            }
            else
            {
                var v = Enumerable.Range(0, keys).Select(k => new Vector3(values[Base(k)], values[Base(k) + 1], values[Base(k) + 2])).ToArray();
                if (pathName == "translation")
                {
                    track.TranslationTimes = t;
                    track.TranslationMode = mode;
                    track.Translations = v;
                }
                else
                {
                    track.ScaleTimes = t;
                    track.ScaleMode = mode;
                    track.Scales = v;
                }
            }
        }
        if (tracks.Count == 0)
            return null;
        string name = anim.TryGetProperty("name", out var n) && !string.IsNullOrEmpty(n.GetString()) ? n.GetString()! : $"Animation {index + 1}";
        return new KeyframeClip { Name = name, Duration = MathF.Max(duration, 1f / 60f), Tracks = tracks.Values.ToList() };
    }

    private static Matrix4x4[] ReadMatrices(View v)
    {
        var f = ReadFloats(v, 16);
        var result = new Matrix4x4[f.Length / 16];
        for (int i = 0; i < result.Length; i++)
        {
            int o = i * 16;
            result[i] = new Matrix4x4(f[o], f[o + 1], f[o + 2], f[o + 3], f[o + 4], f[o + 5], f[o + 6], f[o + 7],
                f[o + 8], f[o + 9], f[o + 10], f[o + 11], f[o + 12], f[o + 13], f[o + 14], f[o + 15]);
        }
        return result;
    }

    private static int ComponentSize(int component) => component switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };

    /// <summary>Integer components (unsigned byte, short or int) of an accessor, <paramref name="width"/> per element.</summary>
    private static int[] ReadComponents(View v, int width, out int componentSize)
    {
        componentSize = ComponentSize(v.Component);
        int step = v.Stride > 0 ? v.Stride : componentSize * width;
        var result = new int[v.Count * width];
        for (int i = 0; i < v.Count; i++)
        {
            for (int k = 0; k < width; k++)
            {
                int o = v.Offset + i * step + k * componentSize;
                result[i * width + k] = componentSize switch
                {
                    1 => v.Bytes[o],
                    2 => BitConverter.ToUInt16(v.Bytes, o),
                    _ => (int)BitConverter.ToUInt32(v.Bytes, o),
                };
            }
        }
        return result;
    }

    /// <summary>Float components of an accessor, with normalised integer types turned into 0..1.</summary>
    private static float[] ReadFloats(View v, int width)
    {
        int size = ComponentSize(v.Component);
        int step = v.Stride > 0 ? v.Stride : size * width;
        var result = new float[v.Count * width];
        for (int i = 0; i < v.Count; i++)
        {
            for (int k = 0; k < width; k++)
            {
                int o = v.Offset + i * step + k * size;
                result[i * width + k] = v.Component switch
                {
                    5126 => BitConverter.ToSingle(v.Bytes, o),
                    5121 => v.Bytes[o] / 255f,
                    5123 => BitConverter.ToUInt16(v.Bytes, o) / 65535f,
                    5120 => MathF.Max((sbyte)v.Bytes[o] / 127f, -1f),
                    5122 => MathF.Max(BitConverter.ToInt16(v.Bytes, o) / 32767f, -1f),
                    _ => BitConverter.ToUInt32(v.Bytes, o),
                };
            }
        }
        return result;
    }

    /// <summary>A node's transform as a row-vector matrix (glTF stores column-major matrices, which read the same).</summary>
    internal static Matrix4x4 Local(JsonElement node)
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
