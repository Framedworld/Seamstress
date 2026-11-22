using System.Numerics;
using System.Text;

namespace Seamstress.App;

/// <summary>
/// Writes Qt Quick 3D ".mesh" files (mesh format version 7). The 3D viewport loads geometry from these files, which
/// lets C# hand whole vertex buffers to the GPU renderer without marshalling every vertex through QML.
/// </summary>
internal static class QtMesh
{
    private const uint MeshFileId = 3365961549;
    private const uint MultiFileId = 555777497;
    private const uint Float32 = 10, UInt32 = 5;
    private const uint DrawLines = 4, DrawTriangles = 7;
    private const uint CounterClockwise = 2;

    public static void WriteTriangles(string path, Vector3[] positions, Vector3[] normals, Vector2[]? uvs, int[] indices)
    {
        bool hasUv = uvs != null;
        int stride = hasUv ? 56 : 24;
        var vertices = new byte[positions.Length * stride];
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(vertices.AsSpan());
        int floats = stride / 4;

        Vector3[]? tangents = null;
        Vector3[]? binormals = null;
        if (hasUv)
        {
            tangents = new Vector3[positions.Length];
            binormals = new Vector3[positions.Length];
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                var e1 = positions[i1] - positions[i0];
                var e2 = positions[i2] - positions[i0];
                var duv1 = uvs![i1] - uvs[i0];
                var duv2 = uvs[i2] - uvs[i0];
                float det = duv1.X * duv2.Y - duv1.Y * duv2.X;
                if (MathF.Abs(det) < 1e-10f)
                    continue;
                float inv = 1f / det;
                var tan = (e1 * duv2.Y - e2 * duv1.Y) * inv;
                var bin = (e2 * duv1.X - e1 * duv2.X) * inv;
                tangents[i0] += tan; tangents[i1] += tan; tangents[i2] += tan;
                binormals[i0] += bin; binormals[i1] += bin; binormals[i2] += bin;
            }
        }

        for (int i = 0; i < positions.Length; i++)
        {
            int o = i * floats;
            span[o] = positions[i].X; span[o + 1] = positions[i].Y; span[o + 2] = positions[i].Z;
            span[o + 3] = normals[i].X; span[o + 4] = normals[i].Y; span[o + 5] = normals[i].Z;
            if (hasUv)
            {
                span[o + 6] = uvs![i].X;
                span[o + 7] = uvs[i].Y;
                var n = normals[i];
                var t = tangents![i];
                t -= n * Vector3.Dot(n, t);
                float tLen = t.Length();
                if (tLen > 1e-6f)
                    t /= tLen;
                else
                    t = MathF.Abs(n.Y) < 0.99f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitY, n)) : Vector3.UnitX;
                var b = Vector3.Cross(n, t);
                if (Vector3.Dot(b, binormals![i]) < 0f)
                    b = -b;
                span[o + 8] = t.X; span[o + 9] = t.Y; span[o + 10] = t.Z;
                span[o + 11] = b.X; span[o + 12] = b.Y; span[o + 13] = b.Z;
            }
        }
        var entries = new List<(string, uint, uint)> { ("attr_pos", 3, 0), ("attr_norm", 3, 12) };
        if (hasUv)
        {
            entries.Add(("attr_uv0", 2, 24));
            entries.Add(("attr_textan", 3, 32));
            entries.Add(("attr_binormal", 3, 44));
        }
        Write(path, vertices, stride, entries, positions, indices, DrawTriangles);
    }

    public static void WriteLines(string path, Vector3[] positions)
    {
        var vertices = new byte[positions.Length * 12];
        System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, byte>(positions.AsSpan()).CopyTo(vertices);
        var indices = new int[positions.Length];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = i;
        Write(path, vertices, 12, new List<(string, uint, uint)> { ("attr_pos", 3, 0) }, positions, indices, DrawLines);
    }

    private static void Write(string path, byte[] vertexData, int stride, List<(string Name, uint Components, uint Offset)> entries,
        Vector3[] positions, int[] indices, uint drawMode)
    {
        using var ms = new MemoryStream(vertexData.Length + indices.Length * 4 + 512);
        using var w = new BinaryWriter(ms);
        w.Write(MeshFileId);
        w.Write((ushort)7);
        w.Write((ushort)0);
        w.Write(0u); // size, patched below
        long start = ms.Position;

        // The format pads each section to a 4-byte boundary and, as Qt's own writer does, adds a full 4 bytes when
        // the section already ends on one.
        uint counter = 0;
        void Pad(uint advance)
        {
            counter += advance;
            uint amount = 4 - counter % 4;
            counter += amount;
            for (int i = 0; i < amount; i++)
                w.Write((byte)0);
        }

        w.Write(0u);                         // morph target entries
        w.Write((uint)entries.Count);
        w.Write((uint)stride);
        w.Write(0u);                         // morph target data size
        w.Write((uint)vertexData.Length);
        w.Write(UInt32);
        w.Write(0u);
        w.Write((uint)(indices.Length * 4));
        w.Write(0u);                         // morph target count
        w.Write(1u);                         // subsets
        w.Write(0u);
        w.Write(0u);
        w.Write(drawMode);
        w.Write(CounterClockwise);
        counter += 56;

        foreach (var entry in entries)
        {
            w.Write(0u);
            w.Write(Float32);
            w.Write(entry.Components);
            w.Write(entry.Offset);
        }
        Pad((uint)entries.Count * 16);
        foreach (var entry in entries)
        {
            var name = Encoding.ASCII.GetBytes(entry.Name);
            w.Write((uint)name.Length + 1);
            w.Write(name);
            w.Write((byte)0);
            Pad(4 + (uint)name.Length + 1);
        }

        w.Write(vertexData);
        Pad((uint)vertexData.Length);
        w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices.AsSpan()));
        Pad((uint)indices.Length * 4);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        if (positions.Length == 0)
            min = max = Vector3.Zero;
        w.Write((uint)indices.Length);
        w.Write(0u);
        w.Write(min.X); w.Write(min.Y); w.Write(min.Z);
        w.Write(max.X); w.Write(max.Y); w.Write(max.Z);
        w.Write(0u);
        w.Write(1u);                         // subset name length including terminator
        w.Write(0u);                         // lightmap width
        w.Write(0u);                         // lightmap height
        w.Write(0u);                         // level-of-detail count
        Pad(52);
        w.Write((ushort)0);                  // empty UTF-16 subset name
        Pad(2);
        Pad(0);                              // level-of-detail entries
        Pad((uint)entries.Count * 16);       // morph target entries (Qt advances by the vertex entry size here)

        long end = ms.Position;
        ms.Position = 8;
        w.Write((uint)(end - start));
        ms.Position = end;
        w.Write(0ul);                        // offset of mesh 1
        w.Write(1u);                         // mesh id
        w.Write(0u);
        w.Write(MultiFileId);
        w.Write(1u);
        w.Write((uint)end);
        w.Write(1u);
        w.Flush();
        File.WriteAllBytes(path, ms.ToArray());
    }
}
