using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;

namespace Seamstress.Core.Animation;

/// <summary>
/// Writes a recorded animation as a binary glTF (.glb) point cache: one mesh per garment and one for the body, each
/// with a morph target per recorded frame and an animation of the morph weights that steps from frame to frame
/// (linear in between). Blender imports it as shape keys with their animation; three.js, Godot, Unity (glTFast) and
/// Unreal read the same file.
/// </summary>
public static class PointCacheExporter
{
    public sealed record Result(int Frames, int Meshes, long Bytes, float Seconds);

    public static Result Write(string path, PointCacheRecording recording, Workspace workspace, bool includeBody = true, bool includeNormals = true)
    {
        var frames = recording.Snapshot();
        if (frames.Count == 0)
            throw new InvalidOperationException("Nothing has been recorded yet: play the animation first.");
        var doc = workspace.Document;
        var meshes = new List<(string Name, int[] Triangles, Vector2[]? Uvs, Func<RecordingFrame, Vector3[]> Positions, string Color)>();

        // Garments: the pieces of each garment, joined.
        int at = 0;
        var byGarment = new List<(string Garment, List<(PointCacheRecording.Piece Piece, int Start, PieceMesh Mesh, Vector2[] Uv)> Pieces, string Color)>();
        foreach (var p in recording.Layout)
        {
            var garment = doc.GarmentOfPiece(p.PieceId);
            var piece = garment?.FindPiece(p.PieceId);
            var mesh = garment == null ? null : workspace.MeshFor(garment).Find(p.PieceId);
            if (garment != null && piece != null && mesh != null && mesh.VertexCount == p.Count)
            {
                var material = doc.FindMaterial(piece.MaterialId) ?? IO.ExportScene.DefaultMaterial;
                var uv = UvLayout.For(garment).MapMaterial(piece, mesh, material);
                var entry = byGarment.FirstOrDefault(g => g.Garment == garment.Name);
                if (entry.Pieces == null)
                {
                    entry = (garment.Name, new(), material.Color);
                    byGarment.Add(entry);
                }
                entry.Pieces.Add((p, at, mesh, uv));
            }
            at += p.Count;
        }
        foreach (var (name, pieces, color) in byGarment)
        {
            var tris = new List<int>();
            var uvs = new List<Vector2>();
            int offset = 0;
            foreach (var (_, _, mesh, uv) in pieces)
            {
                tris.AddRange(mesh.Triangles.Select(t => t + offset));
                uvs.AddRange(uv);
                offset += mesh.VertexCount;
            }
            var parts = pieces.Select(p => (p.Start, p.Piece.Count)).ToList();
            int total = offset;
            meshes.Add((name, tris.ToArray(), uvs.ToArray(), f =>
            {
                var result = new Vector3[total];
                int k = 0;
                foreach (var (start, count) in parts)
                {
                    Array.Copy(f.Cloth, start, result, k, count);
                    k += count;
                }
                return result;
            }, color));
        }
        if (includeBody && recording.BodyTriangles.Length > 0)
            meshes.Add(("Avatar", recording.BodyTriangles, null, f => f.Body, "#b9afa6"));

        var wrapped = frames.Select(f => new RecordingFrame(f.Time, f.Cloth, f.Body)).ToList();
        long bytes = WriteGlb(path, meshes, wrapped, includeNormals);
        return new Result(frames.Count, meshes.Count, bytes, frames[^1].Time - frames[0].Time);
    }

    public sealed record RecordingFrame(float Time, Vector3[] Cloth, Vector3[] Body);

    private static long WriteGlb(string path, List<(string Name, int[] Triangles, Vector2[]? Uvs, Func<RecordingFrame, Vector3[]> Positions, string Color)> meshes,
        List<RecordingFrame> frames, bool includeNormals)
    {
        var bin = new MemoryStream();
        var views = new JsonArray();
        var accessors = new JsonArray();
        var jsonMeshes = new JsonArray();
        var nodes = new JsonArray();
        var materials = new JsonArray();
        var sceneNodes = new JsonArray();

        int View(ReadOnlySpan<byte> data, int? target)
        {
            while (bin.Length % 4 != 0)
                bin.WriteByte(0);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = data.Length };
            if (target != null)
                view["target"] = target.Value;
            bin.Write(data);
            views.Add(view);
            return views.Count - 1;
        }
        int Vec3(Vector3[] v, bool bounds)
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(v.AsSpan());
            var a = new JsonObject { ["bufferView"] = View(bytes, 34962), ["componentType"] = 5126, ["count"] = v.Length, ["type"] = "VEC3" };
            if (bounds)
            {
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var p in v)
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                if (v.Length == 0)
                    min = max = Vector3.Zero;
                a["min"] = new JsonArray(min.X, min.Y, min.Z);
                a["max"] = new JsonArray(max.X, max.Y, max.Z);
            }
            accessors.Add(a);
            return accessors.Count - 1;
        }
        int Floats(float[] values, string type, bool bounds)
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan());
            int width = type == "SCALAR" ? 1 : 2;
            var a = new JsonObject { ["bufferView"] = View(bytes, null), ["componentType"] = 5126, ["count"] = values.Length / width, ["type"] = type };
            if (bounds)
            {
                a["min"] = new JsonArray(values.Min());
                a["max"] = new JsonArray(values.Max());
            }
            accessors.Add(a);
            return accessors.Count - 1;
        }

        int count = frames.Count;
        foreach (var (name, triangles, uvs, positionsOf, color) in meshes)
        {
            var basePos = positionsOf(frames[0]);
            var baseNormals = includeNormals ? Mesher.ComputeNormals(basePos, triangles) : null;
            var attributes = new JsonObject { ["POSITION"] = Vec3(basePos, true) };
            if (baseNormals != null)
                attributes["NORMAL"] = Vec3(baseNormals, false);
            if (uvs != null && uvs.Length == basePos.Length)
            {
                var flat = new float[uvs.Length * 2];
                for (int i = 0; i < uvs.Length; i++)
                {
                    flat[2 * i] = uvs[i].X;
                    flat[2 * i + 1] = uvs[i].Y;
                }
                attributes["TEXCOORD_0"] = Floats(flat, "VEC2", false);
            }
            var indexBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(triangles.AsSpan());
            accessors.Add(new JsonObject { ["bufferView"] = View(indexBytes, 34963), ["componentType"] = 5125, ["count"] = triangles.Length, ["type"] = "SCALAR" });
            int indices = accessors.Count - 1;

            var targets = new JsonArray();
            var names = new JsonArray();
            var delta = new Vector3[basePos.Length];
            for (int f = 0; f < count; f++)
            {
                var pos = positionsOf(frames[f]);
                for (int i = 0; i < delta.Length; i++)
                    delta[i] = pos[i] - basePos[i];
                var target = new JsonObject { ["POSITION"] = Vec3(delta, true) };
                if (baseNormals != null)
                {
                    var normals = Mesher.ComputeNormals(pos, triangles);
                    for (int i = 0; i < normals.Length; i++)
                        normals[i] -= baseNormals[i];
                    target["NORMAL"] = Vec3(normals, false);
                }
                targets.Add(target);
                names.Add($"frame_{f:0000}");
            }

            materials.Add(new JsonObject
            {
                ["name"] = name,
                ["doubleSided"] = true,
                ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = Color(color), ["metallicFactor"] = 0, ["roughnessFactor"] = 0.8 },
            });
            jsonMeshes.Add(new JsonObject
            {
                ["name"] = name,
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = attributes,
                    ["indices"] = indices,
                    ["material"] = materials.Count - 1,
                    ["targets"] = targets,
                }),
                ["weights"] = new JsonArray(Enumerable.Range(0, count).Select(_ => (JsonNode)0).ToArray()),
                ["extras"] = new JsonObject { ["targetNames"] = names },
            });
            nodes.Add(new JsonObject { ["name"] = name, ["mesh"] = jsonMeshes.Count - 1 });
            sceneNodes.Add(nodes.Count - 1);
        }

        // One weight curve shared by every mesh: frame k is fully on at its own time and off at the neighbours'.
        var times = frames.Select(f => f.Time - frames[0].Time).ToArray();
        var weights = new float[count * count];
        for (int f = 0; f < count; f++)
            weights[f * count + f] = 1;
        int input = Floats(times, "SCALAR", true);
        int output = Floats(weights, "SCALAR", false);
        var channels = new JsonArray();
        for (int n = 0; n < nodes.Count; n++)
            channels.Add(new JsonObject { ["sampler"] = 0, ["target"] = new JsonObject { ["node"] = n, ["path"] = "weights" } });

        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Seamstress point cache" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = sceneNodes }),
            ["nodes"] = nodes,
            ["meshes"] = jsonMeshes,
            ["materials"] = materials,
            ["accessors"] = accessors,
            ["bufferViews"] = views,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
            ["animations"] = new JsonArray(new JsonObject
            {
                ["name"] = "Recording",
                ["samplers"] = new JsonArray(new JsonObject { ["input"] = input, ["output"] = output, ["interpolation"] = "LINEAR" }),
                ["channels"] = channels,
            }),
        };
        while (bin.Length % 4 != 0)
            bin.WriteByte(0);
        root["buffers"]![0]!["byteLength"] = bin.Length;
        byte[] jsonBytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        int jsonPadded = (jsonBytes.Length + 3) & ~3;
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder != null)
            Directory.CreateDirectory(folder);
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        long total = 12 + 8 + jsonPadded + 8 + bin.Length;
        w.Write(0x46546C67u);
        w.Write(2u);
        w.Write((uint)total);
        w.Write((uint)jsonPadded);
        w.Write(0x4E4F534Au);
        w.Write(jsonBytes);
        for (int i = jsonBytes.Length; i < jsonPadded; i++)
            w.Write((byte)' ');
        w.Write((uint)bin.Length);
        w.Write(0x004E4942u);
        bin.Position = 0;
        bin.CopyTo(file);
        return total;
    }

    private static JsonArray Color(string hex)
    {
        hex = hex.TrimStart('#');
        float C(int at) => hex.Length >= at + 2 && int.TryParse(hex.Substring(at, 2), System.Globalization.NumberStyles.HexNumber, null, out int v) ? v / 255f : 0.7f;
        return new JsonArray(C(0), C(2), C(4), 1f);
    }
}

/// <summary>Reads back a point cache written by <see cref="PointCacheExporter"/>: every mesh's positions per frame.</summary>
public static class PointCacheReader
{
    public sealed record CachedMesh(string Name, int[] Triangles, List<Vector3[]> Frames);
    public sealed record Cache(float[] Times, List<CachedMesh> Meshes);

    public static Cache Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != 0x46546C67)
            throw new InvalidDataException("Not a GLB file.");
        int jsonLength = BitConverter.ToInt32(data, 12);
        using var json = JsonDocument.Parse(data.AsMemory(20, jsonLength));
        int binStart = 20 + jsonLength + 8;
        var root = json.RootElement;
        (int Offset, int Count) Acc(int index)
        {
            var a = root.GetProperty("accessors")[index];
            var v = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
            return (binStart + v.GetProperty("byteOffset").GetInt32() + (a.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0), a.GetProperty("count").GetInt32());
        }
        Vector3[] Vec3(int index)
        {
            var (offset, count) = Acc(index);
            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
                result[i] = new Vector3(BitConverter.ToSingle(data, offset + 12 * i), BitConverter.ToSingle(data, offset + 12 * i + 4), BitConverter.ToSingle(data, offset + 12 * i + 8));
            return result;
        }
        var meshes = new List<CachedMesh>();
        foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
        {
            var prim = mesh.GetProperty("primitives")[0];
            var basePos = Vec3(prim.GetProperty("attributes").GetProperty("POSITION").GetInt32());
            var (io, ic) = Acc(prim.GetProperty("indices").GetInt32());
            var tris = new int[ic];
            for (int i = 0; i < ic; i++)
                tris[i] = BitConverter.ToInt32(data, io + 4 * i);
            var frames = new List<Vector3[]>();
            foreach (var target in prim.GetProperty("targets").EnumerateArray())
            {
                var d = Vec3(target.GetProperty("POSITION").GetInt32());
                frames.Add(basePos.Select((p, i) => p + d[i]).ToArray());
            }
            meshes.Add(new CachedMesh(mesh.GetProperty("name").GetString() ?? "", tris, frames));
        }
        var sampler = root.GetProperty("animations")[0].GetProperty("samplers")[0];
        var (to, tc) = Acc(sampler.GetProperty("input").GetInt32());
        var times = new float[tc];
        for (int i = 0; i < tc; i++)
            times[i] = BitConverter.ToSingle(data, to + 4 * i);
        return new Cache(times, meshes);
    }
}
