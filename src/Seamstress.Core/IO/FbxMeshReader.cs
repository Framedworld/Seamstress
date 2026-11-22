using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Seamstress.Core.IO;

/// <summary>
/// Reads the meshes of a binary FBX file (7.x, as written by Maya, Blender, Character Creator, Unreal...) in their
/// rest pose, for use as a body: each mesh's own node transform, the file's up axis and its unit are applied, so the
/// result is in metres with Y up. Skinning, materials and animation are ignored.
/// </summary>
public static class FbxMeshReader
{
    /// <summary>Meshes that are not part of the surface cloth rests on; left out unless asked for by name.</summary>
    private static readonly string[] NotBody =
    {
        "eyelash", "tearline", "occlusion", "cornea", "teeth", "tongue", "hair", "brow", "beard", "mustache", "stubble",
    };

    public static bool LooksLikeBody(string name)
        => !NotBody.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private sealed class Node
    {
        public string Name = "";
        public List<object> Props = new();
        public List<Node> Children = new();
        public Node? Child(string name) => Children.FirstOrDefault(c => c.Name == name);
    }

    public static MeshFile Load(string path, Func<string, bool>? include = null)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 27 || Encoding.ASCII.GetString(data, 0, 18) != "Kaydara FBX Binary")
        {
            throw new InvalidDataException(data.Length > 5 && Encoding.ASCII.GetString(data, 0, 5) == "; FBX"
                ? "This is a text (ASCII) FBX file. Export it as binary FBX, or as OBJ or glTF."
                : "This is not an FBX file.");
        }
        uint version = BitConverter.ToUInt32(data, 23);
        bool wide = version >= 7500;
        var top = new List<Node>();
        long pos = 27;
        while (pos < data.Length - (wide ? 25 : 13))
        {
            var node = ReadNode(data, ref pos, wide, null);
            if (node == null)
                break;
            if (Wanted(null, node.Name))
                top.Add(node);
        }

        // Scene conventions.
        int upAxis = 1;
        double unit = 1;
        if (top.FirstOrDefault(n => n.Name == "GlobalSettings")?.Child("Properties70") is { } settings)
        {
            foreach (var p in settings.Children)
            {
                if (p.Props.Count < 5 || p.Props[0] is not string key)
                    continue;
                if (key == "UpAxis")
                    upAxis = Convert.ToInt32(p.Props[4]);
                else if (key == "UnitScaleFactor")
                    unit = Convert.ToDouble(p.Props[4]);
            }
        }

        var geometry = new Dictionary<long, Node>();
        var models = new Dictionary<long, (string Name, Matrix4x4 Local)>();
        foreach (var obj in top.FirstOrDefault(n => n.Name == "Objects")?.Children ?? new())
        {
            if (obj.Props.Count < 3 || obj.Props[0] is not long id)
                continue;
            if (obj.Name == "Geometry" && obj.Props[2] as string == "Mesh")
                geometry[id] = obj;
            else if (obj.Name == "Model")
                models[id] = (CleanName(obj.Props[1] as string ?? ""), LocalTransform(obj));
        }
        var parent = new Dictionary<long, long>();
        var modelOfGeometry = new Dictionary<long, long>();
        foreach (var c in top.FirstOrDefault(n => n.Name == "Connections")?.Children ?? new())
        {
            if (c.Props.Count < 3 || c.Props[0] as string != "OO" || c.Props[1] is not long child || c.Props[2] is not long owner)
                continue;
            if (geometry.ContainsKey(child) && models.ContainsKey(owner))
                modelOfGeometry[child] = owner;
            else if (models.ContainsKey(child) && models.ContainsKey(owner))
                parent[child] = owner;
        }

        Matrix4x4 World(long model)
        {
            var m = Matrix4x4.Identity;
            for (int guard = 0; guard < 256 && models.TryGetValue(model, out var entry); guard++)
            {
                m *= entry.Local;
                if (!parent.TryGetValue(model, out model))
                    break;
            }
            return m;
        }

        float metres = (float)(unit * 0.01);
        var file = new MeshFile();
        foreach (var (id, node) in geometry)
        {
            string name = modelOfGeometry.TryGetValue(id, out long owner) ? models[owner].Name : CleanName(node.Props[1] as string ?? "");
            if (!(include ?? LooksLikeBody)(name))
            {
                file.Skipped.Add(name);
                continue;
            }
            if (node.Child("Vertices")?.Props.FirstOrDefault() is not double[] verts
                || node.Child("PolygonVertexIndex")?.Props.FirstOrDefault() is not int[] polygons || verts.Length < 9)
                continue;
            var world = modelOfGeometry.ContainsKey(id) ? World(owner) : Matrix4x4.Identity;
            var positions = new Vector3[verts.Length / 3];
            for (int i = 0; i < positions.Length; i++)
            {
                var p = Vector3.Transform(new Vector3((float)verts[3 * i], (float)verts[3 * i + 1], (float)verts[3 * i + 2]), world);
                p = upAxis switch
                {
                    2 => new Vector3(p.X, p.Z, -p.Y),   // Z up
                    0 => new Vector3(-p.Y, p.X, p.Z),   // X up
                    _ => p,
                };
                positions[i] = p * metres;
            }
            var triangles = new List<int>(polygons.Length);
            var polygon = new List<int>(8);
            foreach (int raw in polygons)
            {
                bool last = raw < 0;                    // the last corner of each polygon is stored as ~index
                int index = last ? ~raw : raw;
                if (index < positions.Length)
                    polygon.Add(index);
                if (!last)
                    continue;
                for (int k = 1; k + 1 < polygon.Count; k++)
                {
                    triangles.Add(polygon[0]);
                    triangles.Add(polygon[k]);
                    triangles.Add(polygon[k + 1]);
                }
                polygon.Clear();
            }
            if (triangles.Count >= 3)
                file.Parts.Add(new MeshPart(name, positions, triangles.ToArray()));
        }
        if (file.Parts.Count == 0)
            throw new InvalidDataException(file.Skipped.Count > 0
                ? $"No body mesh was found; the file only has: {string.Join(", ", file.Skipped)}."
                : "The FBX file contains no mesh.");
        return file;
    }

    private static string CleanName(string name)
    {
        int cut = name.IndexOf('\0');
        if (cut >= 0)
            name = name[..cut];
        int colon = name.LastIndexOf("::", StringComparison.Ordinal);
        return colon >= 0 ? name[(colon + 2)..] : name;
    }

    /// <summary>The node's translation, pre-rotation, rotation and scale as a row-vector matrix (pivots are ignored).</summary>
    private static Matrix4x4 LocalTransform(Node model)
    {
        Vector3 t = Vector3.Zero, r = Vector3.Zero, pre = Vector3.Zero, s = Vector3.One;
        foreach (var p in model.Child("Properties70")?.Children ?? new())
        {
            if (p.Props.Count < 7 || p.Props[0] is not string key)
                continue;
            var v = new Vector3((float)Convert.ToDouble(p.Props[4]), (float)Convert.ToDouble(p.Props[5]), (float)Convert.ToDouble(p.Props[6]));
            switch (key)
            {
                case "Lcl Translation": t = v; break;
                case "Lcl Rotation": r = v; break;
                case "PreRotation": pre = v; break;
                case "Lcl Scaling": s = v; break;
            }
        }
        static Matrix4x4 Euler(Vector3 degrees)
        {
            const float rad = MathF.PI / 180f;
            return Matrix4x4.CreateRotationX(degrees.X * rad) * Matrix4x4.CreateRotationY(degrees.Y * rad) * Matrix4x4.CreateRotationZ(degrees.Z * rad);
        }
        return Matrix4x4.CreateScale(s) * Euler(r) * Euler(pre) * Matrix4x4.CreateTranslation(t);
    }

    /// <summary>Only the parts that describe meshes and where they are get decoded; everything else is stepped over.</summary>
    private static bool Wanted(string? parent, string name) => parent switch
    {
        null => name is "GlobalSettings" or "Objects" or "Connections",
        "Objects" => name is "Geometry" or "Model",
        "Geometry" => name is "Vertices" or "PolygonVertexIndex",
        "Model" or "GlobalSettings" => name == "Properties70",
        "Properties70" => name == "P",
        "Connections" => name == "C",
        _ => false,
    };

    private static Node? ReadNode(byte[] data, ref long pos, bool wide, string? parent)
    {
        long end, count;
        if (wide)
        {
            end = BitConverter.ToInt64(data, (int)pos);
            count = BitConverter.ToInt64(data, (int)pos + 8);
            pos += 24;
        }
        else
        {
            end = BitConverter.ToUInt32(data, (int)pos);
            count = BitConverter.ToUInt32(data, (int)pos + 4);
            pos += 12;
        }
        int nameLength = data[pos++];
        if (end == 0)
            return null;
        if (end > data.Length || end < pos)
            throw new InvalidDataException("The FBX file is damaged or cut short.");
        var node = new Node { Name = Encoding.ASCII.GetString(data, (int)pos, nameLength) };
        pos += nameLength;
        if (!Wanted(parent, node.Name))
        {
            pos = end;
            return node;
        }
        bool arrays = node.Name is "Vertices" or "PolygonVertexIndex";
        for (long i = 0; i < count; i++)
            node.Props.Add(ReadProperty(data, ref pos, arrays));
        while (pos < end)
        {
            var child = ReadNode(data, ref pos, wide, node.Name);
            if (child == null)
                break;
            if (Wanted(node.Name, child.Name))
                node.Children.Add(child);
        }
        pos = end;
        return node;
    }

    private static object ReadProperty(byte[] data, ref long pos, bool decodeArrays)
    {
        char type = (char)data[pos++];
        int at = (int)pos;
        switch (type)
        {
            case 'Y': pos += 2; return (long)BitConverter.ToInt16(data, at);
            case 'C': pos += 1; return data[at] != 0;
            case 'I': pos += 4; return BitConverter.ToInt32(data, at);
            case 'F': pos += 4; return (double)BitConverter.ToSingle(data, at);
            case 'D': pos += 8; return BitConverter.ToDouble(data, at);
            case 'L': pos += 8; return BitConverter.ToInt64(data, at);
            case 'S':
            case 'R':
            {
                int length = BitConverter.ToInt32(data, at);
                pos += 4 + length;
                return type == 'S' ? Encoding.UTF8.GetString(data, at + 4, length) : (object)length;
            }
            case 'f': case 'd': case 'l': case 'i': case 'b':
            {
                int length = BitConverter.ToInt32(data, at);
                int encoding = BitConverter.ToInt32(data, at + 4);
                int stored = BitConverter.ToInt32(data, at + 8);
                pos += 12 + stored;
                if (!decodeArrays)
                    return length;
                int size = type is 'd' or 'l' ? 8 : type == 'b' ? 1 : 4;
                byte[] raw = new byte[length * size];
                if (encoding == 1)
                {
                    using var z = new ZLibStream(new MemoryStream(data, at + 12, stored), CompressionMode.Decompress);
                    z.ReadExactly(raw);
                }
                else
                {
                    Buffer.BlockCopy(data, at + 12, raw, 0, raw.Length);
                }
                if (type == 'd')
                {
                    var values = new double[length];
                    Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
                    return values;
                }
                if (type == 'f')
                {
                    var values = new double[length];
                    for (int i = 0; i < length; i++)
                        values[i] = BitConverter.ToSingle(raw, 4 * i);
                    return values;
                }
                if (type == 'i')
                {
                    var values = new int[length];
                    Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
                    return values;
                }
                return length;
            }
            default:
                throw new InvalidDataException($"Unknown FBX property type '{type}'.");
        }
    }
}
