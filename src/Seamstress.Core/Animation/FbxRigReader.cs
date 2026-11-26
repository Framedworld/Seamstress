using System.IO.Compression;
using System.Numerics;
using System.Text;
using Seamstress.Core.IO;

namespace Seamstress.Core.Animation;

/// <summary>
/// Reads the skeleton, skin clusters and animation stacks of a binary FBX file (7.x). Meshes are gathered in the same
/// order as <see cref="FbxMeshReader"/> does, so skinned vertices line up with the avatar loaded from the same file.
/// Pivots and rotation offsets are ignored, as they are for the rest pose; rotation order and pre/post rotation are used.
/// </summary>
public static class FbxRigReader
{
    private const double TicksPerSecond = 46186158000.0;

    private sealed class Node
    {
        public string Name = "";
        public List<object> Props = new();
        public List<Node> Children = new();
        public Node? Child(string name) => Children.FirstOrDefault(c => c.Name == name);
        public IEnumerable<Node> P70 => Child("Properties70")?.Children ?? Enumerable.Empty<Node>();
    }

    private static readonly HashSet<string> ArrayNodes = new()
    {
        "Vertices", "PolygonVertexIndex", "Indexes", "Weights", "Transform", "TransformLink", "KeyTime", "KeyValueFloat",
    };

    private sealed class Model
    {
        public long Id;
        public string Name = "";
        public Vector3 T, R, S = Vector3.One, Pre, Post;
        public int RotationOrder;
        public Matrix4x4 Local => Compose(T, R, S, Pre, Post, RotationOrder);
    }

    public static RigFile Load(string path, float scale = 1f, Func<string, bool>? includeMesh = null)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 27 || Encoding.ASCII.GetString(data, 0, 18) != "Kaydara FBX Binary")
            throw new InvalidDataException(data.Length > 5 && Encoding.ASCII.GetString(data, 0, 5) == "; FBX"
                ? "This is a text (ASCII) FBX file. Export it as binary FBX, or as glTF."
                : "This is not an FBX file.");
        uint version = BitConverter.ToUInt32(data, 23);
        bool wide = version >= 7500;
        var top = new List<Node>();
        long pos = 27;
        while (pos < data.Length - (wide ? 25 : 13))
        {
            var node = ReadNode(data, ref pos, wide, 0);
            if (node == null)
                break;
            if (node.Name is "GlobalSettings" or "Objects" or "Connections")
                top.Add(node);
        }

        int upAxis = 1;
        double unit = 1;
        foreach (var p in top.FirstOrDefault(n => n.Name == "GlobalSettings")?.P70 ?? Enumerable.Empty<Node>())
        {
            if (p.Props.Count < 5 || p.Props[0] is not string key)
                continue;
            if (key == "UpAxis")
                upAxis = Convert.ToInt32(p.Props[4]);
            else if (key == "UnitScaleFactor")
                unit = Convert.ToDouble(p.Props[4]);
        }

        var geometry = new Dictionary<long, Node>();
        var models = new Dictionary<long, Model>();
        var modelOrder = new List<long>();
        var clusters = new Dictionary<long, Node>();
        var skins = new HashSet<long>();
        var stacks = new Dictionary<long, Node>();
        var layers = new HashSet<long>();
        var curveNodes = new Dictionary<long, Node>();
        var curves = new Dictionary<long, Node>();
        foreach (var obj in top.FirstOrDefault(n => n.Name == "Objects")?.Children ?? new())
        {
            if (obj.Props.Count < 3 || obj.Props[0] is not long id)
                continue;
            string kind = obj.Props[2] as string ?? "";
            switch (obj.Name)
            {
                case "Geometry" when kind == "Mesh": geometry[id] = obj; break;
                case "Model": models[id] = ReadModel(id, obj); modelOrder.Add(id); break;
                case "Deformer" when kind == "Cluster": clusters[id] = obj; break;
                case "Deformer" when kind == "Skin": skins.Add(id); break;
                case "AnimationStack": stacks[id] = obj; break;
                case "AnimationLayer": layers.Add(id); break;
                case "AnimationCurveNode": curveNodes[id] = obj; break;
                case "AnimationCurve": curves[id] = obj; break;
            }
        }

        var parent = new Dictionary<long, long>();
        var modelOfGeometry = new Dictionary<long, long>();
        var skinOfGeometry = new Dictionary<long, long>();
        var clustersOfSkin = new Dictionary<long, List<long>>();
        var boneOfCluster = new Dictionary<long, long>();
        var layersOfStack = new Dictionary<long, List<long>>();
        var curveNodesOfLayer = new Dictionary<long, List<long>>();
        var curveNodeTarget = new Dictionary<long, (long Model, string Property)>();
        var curvesOfNode = new Dictionary<long, List<(long Curve, string Channel)>>();
        foreach (var c in top.FirstOrDefault(n => n.Name == "Connections")?.Children ?? new())
        {
            if (c.Props.Count < 3 || c.Props[0] is not string type || c.Props[1] is not long child || c.Props[2] is not long owner)
                continue;
            string property = c.Props.Count > 3 ? c.Props[3] as string ?? "" : "";
            if (geometry.ContainsKey(child) && models.ContainsKey(owner))
                modelOfGeometry[child] = owner;
            else if (models.ContainsKey(child) && models.ContainsKey(owner))
                parent[child] = owner;
            else if (skins.Contains(child) && geometry.ContainsKey(owner))
                skinOfGeometry[owner] = child;
            else if (clusters.ContainsKey(child) && skins.Contains(owner))
                Add(clustersOfSkin, owner, child);
            else if (models.ContainsKey(child) && clusters.ContainsKey(owner))
                boneOfCluster[owner] = child;
            else if (layers.Contains(child) && stacks.ContainsKey(owner))
                Add(layersOfStack, owner, child);
            else if (curveNodes.ContainsKey(child) && layers.Contains(owner))
                Add(curveNodesOfLayer, owner, child);
            else if (curveNodes.ContainsKey(child) && models.ContainsKey(owner) && type == "OP")
                curveNodeTarget[child] = (owner, property);
            else if (curves.ContainsKey(child) && curveNodes.ContainsKey(owner) && type == "OP")
            {
                if (!curvesOfNode.TryGetValue(owner, out var list))
                    curvesOfNode[owner] = list = new();
                list.Add((child, property));
            }
        }

        // ---- every model as a joint, parents first
        var order = new List<long>();
        var placed = new HashSet<long>();
        void Place(long id, int depth)
        {
            if (placed.Contains(id) || depth > 512)
                return;
            if (parent.TryGetValue(id, out long p) && models.ContainsKey(p) && !placed.Contains(p))
                Place(p, depth + 1);
            if (placed.Add(id))
                order.Add(id);
        }
        foreach (long id in modelOrder)
            Place(id, 0);
        var jointOf = new Dictionary<long, int>();
        for (int k = 0; k < order.Count; k++)
            jointOf[order[k]] = k;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new string[order.Count];
        for (int k = 0; k < order.Count; k++)
        {
            string name = models[order[k]].Name;
            string unique = name;
            for (int n = 2; !used.Add(unique); n++)
                unique = $"{name} #{n}";
            names[k] = unique;
        }
        var skeleton = new Skeleton
        {
            Names = names,
            Parents = order.Select(id => parent.TryGetValue(id, out long p) && jointOf.TryGetValue(p, out int pj) ? pj : -1).ToArray(),
            RestLocal = order.Select(id => models[id].Local).ToArray(),
        };
        var restWorld = new Matrix4x4[order.Count];
        skeleton.World(skeleton.RestLocal, restWorld);

        // ---- meshes in FbxMeshReader's order, with their skin weights
        var inverseBind = Enumerable.Repeat(Matrix4x4.Identity, order.Count).ToArray();
        var inverseBindSet = new bool[order.Count];
        var bind = new List<Vector3>();
        var tris = new List<int>();
        var joints = new List<int>();
        var weights = new List<float>();
        bool anySkin = false;
        foreach (var (id, node) in geometry)
        {
            long owner = modelOfGeometry.TryGetValue(id, out long o) ? o : 0;
            string name = models.TryGetValue(owner, out var m) ? m.Name : CleanName(node.Props[1] as string ?? "");
            if (!(includeMesh ?? FbxMeshReader.LooksLikeBody)(name))
                continue;
            if (node.Child("Vertices")?.Props.FirstOrDefault() is not double[] verts
                || node.Child("PolygonVertexIndex")?.Props.FirstOrDefault() is not int[] polygons || verts.Length < 9)
                continue;
            int count = verts.Length / 3;
            var partTris = new List<int>(polygons.Length);
            var polygon = new List<int>(8);
            foreach (int raw in polygons)
            {
                bool last = raw < 0;
                int index = last ? ~raw : raw;
                if (index < count)
                    polygon.Add(index);
                if (!last)
                    continue;
                for (int k = 1; k + 1 < polygon.Count; k++)
                {
                    partTris.Add(polygon[0]);
                    partTris.Add(polygon[k]);
                    partTris.Add(polygon[k + 1]);
                }
                polygon.Clear();
            }
            if (partTris.Count < 3)
                continue;

            int self = jointOf.TryGetValue(owner, out int sj) ? sj : -1;
            var meshWorld = self >= 0 ? restWorld[self] : Matrix4x4.Identity;
            var partJoints = new int[count * SkinnedBody.Influences];
            var partWeights = new float[count * SkinnedBody.Influences];
            Matrix4x4? clusterTransform = null;
            if (skinOfGeometry.TryGetValue(id, out long skin) && clustersOfSkin.TryGetValue(skin, out var clusterIds))
            {
                foreach (long cid in clusterIds)
                {
                    var cluster = clusters[cid];
                    if (!boneOfCluster.TryGetValue(cid, out long bone) || !jointOf.TryGetValue(bone, out int bj))
                        continue;
                    if (cluster.Child("Transform")?.Props.FirstOrDefault() is double[] tm && tm.Length == 16)
                        clusterTransform ??= ToMatrix(tm);
                    if (!inverseBindSet[bj] && cluster.Child("TransformLink")?.Props.FirstOrDefault() is double[] link && link.Length == 16
                        && Matrix4x4.Invert(ToMatrix(link), out var inv))
                    {
                        inverseBind[bj] = inv;
                        inverseBindSet[bj] = true;
                    }
                    if (cluster.Child("Indexes")?.Props.FirstOrDefault() is not int[] indexes
                        || cluster.Child("Weights")?.Props.FirstOrDefault() is not double[] w)
                        continue;
                    for (int k = 0; k < indexes.Length && k < w.Length; k++)
                    {
                        if (indexes[k] >= 0 && indexes[k] < count && w[k] > 0)
                            SkinnedBody.AddInfluence(partJoints, partWeights, indexes[k], bj, (float)w[k]);
                    }
                    anySkin = true;
                }
            }
            if (self >= 0 && !inverseBindSet[self] && Matrix4x4.Invert(meshWorld, out var invSelf))
            {
                // Vertices no cluster reaches ride on the mesh's own node.
                inverseBind[self] = invSelf;
                inverseBindSet[self] = true;
            }
            var placeMatrix = clusterTransform ?? meshWorld;
            int first = bind.Count;
            for (int i = 0; i < count; i++)
                bind.Add(Vector3.Transform(new Vector3((float)verts[3 * i], (float)verts[3 * i + 1], (float)verts[3 * i + 2]), placeMatrix));
            int fallback = Math.Max(self, 0);
            SkinnedBody.NormaliseWeights(partJoints, partWeights, fallback);
            joints.AddRange(partJoints);
            weights.AddRange(partWeights);
            tris.AddRange(partTris.Select(t => t + first));
        }

        var axis = upAxis switch
        {
            2 => new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1),   // Z up: (x, y, z) -> (x, z, -y)
            0 => new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),   // X up: (x, y, z) -> (-y, x, z)
            _ => Matrix4x4.Identity,
        };
        var toScaled = axis * Matrix4x4.CreateScale((float)(unit * 0.01) * scale);
        SkinnedBody? body = null;
        if (bind.Count > 0)
        {
            var offset = RigFile.GroundOffset(bind.Select(p => Vector3.Transform(p, toScaled)));
            body = new SkinnedBody
            {
                Skeleton = skeleton,
                BindPositions = bind.ToArray(),
                Triangles = tris.ToArray(),
                Joints = joints.ToArray(),
                Weights = weights.ToArray(),
                InverseBind = inverseBind,
                ToAvatar = toScaled * Matrix4x4.CreateTranslation(offset),
                Source = path + (anySkin ? "" : " (no skin: meshes ride on their nodes)"),
            };
        }

        var file = new RigFile { Body = body, Skeleton = skeleton };
        foreach (var (stackId, stack) in stacks)
        {
            var clip = ReadStack(stack, layersOfStack.GetValueOrDefault(stackId) ?? new(), curveNodesOfLayer, curveNodeTarget,
                curvesOfNode, curveNodes, curves, models);
            if (clip != null)
                file.Clips.Add(clip);
        }
        return file;
    }

    private static void Add(Dictionary<long, List<long>> map, long key, long value)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = new();
        list.Add(value);
    }

    private static Model ReadModel(long id, Node obj)
    {
        var m = new Model { Id = id, Name = CleanName(obj.Props[1] as string ?? "") };
        foreach (var p in obj.P70)
        {
            if (p.Props.Count < 5 || p.Props[0] is not string key)
                continue;
            if (key == "RotationOrder")
            {
                m.RotationOrder = Convert.ToInt32(p.Props[4]);
                continue;
            }
            if (p.Props.Count < 7)
                continue;
            var v = new Vector3((float)Convert.ToDouble(p.Props[4]), (float)Convert.ToDouble(p.Props[5]), (float)Convert.ToDouble(p.Props[6]));
            switch (key)
            {
                case "Lcl Translation": m.T = v; break;
                case "Lcl Rotation": m.R = v; break;
                case "Lcl Scaling": m.S = v; break;
                case "PreRotation": m.Pre = v; break;
                case "PostRotation": m.Post = v; break;
            }
        }
        return m;
    }

    /// <summary>FBX's local transform without pivots or offsets, as a row-vector matrix: S · Post⁻¹ · R · Pre · T.</summary>
    private static Matrix4x4 Compose(Vector3 t, Vector3 r, Vector3 s, Vector3 pre, Vector3 post, int order)
    {
        var rotation = Euler(r, order);
        var m = Matrix4x4.CreateScale(s);
        if (post != Vector3.Zero && Matrix4x4.Invert(Euler(post, 0), out var invPost))
            m *= invPost;
        m *= rotation;
        if (pre != Vector3.Zero)
            m *= Euler(pre, 0);
        return m * Matrix4x4.CreateTranslation(t);
    }

    /// <summary>Euler angles in degrees, applied in FBX's rotation order (0 = XYZ: X first).</summary>
    private static Matrix4x4 Euler(Vector3 degrees, int order)
    {
        const float rad = MathF.PI / 180f;
        var x = Matrix4x4.CreateRotationX(degrees.X * rad);
        var y = Matrix4x4.CreateRotationY(degrees.Y * rad);
        var z = Matrix4x4.CreateRotationZ(degrees.Z * rad);
        return order switch
        {
            1 => x * z * y,
            2 => y * z * x,
            3 => y * x * z,
            4 => z * x * y,
            5 => z * y * x,
            _ => x * y * z,
        };
    }

    private static Matrix4x4 ToMatrix(double[] m) => new(
        (float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7],
        (float)m[8], (float)m[9], (float)m[10], (float)m[11], (float)m[12], (float)m[13], (float)m[14], (float)m[15]);

    private sealed class Curve
    {
        public required float[] Times;
        public required float[] Values;

        public float Sample(float t)
        {
            if (Times.Length == 1 || t <= Times[0])
                return Values[0];
            if (t >= Times[^1])
                return Values[^1];
            int hi = Array.BinarySearch(Times, t);
            if (hi >= 0)
                return Values[hi];
            hi = ~hi;
            int lo = hi - 1;
            float f = (t - Times[lo]) / MathF.Max(Times[hi] - Times[lo], 1e-9f);
            return Values[lo] + (Values[hi] - Values[lo]) * f;
        }
    }

    /// <summary>A joint's animated transform channels; components without a curve keep their static value.</summary>
    private sealed class JointTrack
    {
        public required string Joint;
        public required Model Static;
        public Curve?[] T = new Curve?[3], R = new Curve?[3], S = new Curve?[3];
        public Vector3 DefaultT, DefaultR, DefaultS;
    }

    /// <summary>An FBX animation stack. Curves are joined linearly between keys (cubic tangents are not used).</summary>
    private sealed class FbxClip : AnimationClip
    {
        public required List<JointTrack> Tracks { get; init; }
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
                var tr = Tracks[k];
                Vector3 V(Curve?[] c, Vector3 d) => new(c[0]?.Sample(time) ?? d.X, c[1]?.Sample(time) ?? d.Y, c[2]?.Sample(time) ?? d.Z);
                locals[j] = Compose(V(tr.T, tr.DefaultT), V(tr.R, tr.DefaultR), V(tr.S, tr.DefaultS), tr.Static.Pre, tr.Static.Post, tr.Static.RotationOrder);
            }
        }
    }

    private static AnimationClip? ReadStack(Node stack, List<long> layerIds, Dictionary<long, List<long>> curveNodesOfLayer,
        Dictionary<long, (long Model, string Property)> target, Dictionary<long, List<(long Curve, string Channel)>> curvesOfNode,
        Dictionary<long, Node> curveNodes, Dictionary<long, Node> curves, Dictionary<long, Model> models)
    {
        double? start = null, stop = null;
        foreach (var p in stack.P70)
        {
            if (p.Props.Count < 5 || p.Props[0] is not string key)
                continue;
            if (key == "LocalStart")
                start = Convert.ToDouble(p.Props[4]);
            else if (key == "LocalStop")
                stop = Convert.ToDouble(p.Props[4]);
        }
        // Only the first layer: further layers are additive, which this reader does not mix.
        long? layer = layerIds.Count > 0 ? layerIds[0] : null;
        if (layer == null || !curveNodesOfLayer.TryGetValue(layer.Value, out var nodeIds))
            return null;

        var raw = new List<(long Model, string Property, int Axis, long[] Times, float[] Values)>();
        double first = double.MaxValue, last = double.MinValue;
        var defaults = new Dictionary<(long, string), Vector3>();
        foreach (long cn in nodeIds)
        {
            if (!target.TryGetValue(cn, out var t) || t.Property is not ("Lcl Translation" or "Lcl Rotation" or "Lcl Scaling"))
                continue;
            var d = Vector3.Zero;
            foreach (var p in curveNodes[cn].P70)
            {
                if (p.Props.Count < 5 || p.Props[0] is not string key)
                    continue;
                float value = (float)Convert.ToDouble(p.Props[4]);
                if (key == "d|X") d.X = value;
                else if (key == "d|Y") d.Y = value;
                else if (key == "d|Z") d.Z = value;
            }
            defaults[(t.Model, t.Property)] = d;
            foreach (var (curveId, channel) in curvesOfNode.GetValueOrDefault(cn) ?? new())
            {
                int axis = channel switch { "d|X" => 0, "d|Y" => 1, "d|Z" => 2, _ => -1 };
                var curve = curves[curveId];
                if (axis < 0 || curve.Child("KeyTime")?.Props.FirstOrDefault() is not long[] times
                    || curve.Child("KeyValueFloat")?.Props.FirstOrDefault() is not double[] values || times.Length == 0)
                    continue;
                int n = Math.Min(times.Length, values.Length);
                raw.Add((t.Model, t.Property, axis, times[..n], values.Take(n).Select(v => (float)v).ToArray()));
                first = Math.Min(first, times[0]);
                last = Math.Max(last, times[n - 1]);
            }
        }
        if (raw.Count == 0)
            return null;
        double origin = start ?? first;
        double end = stop ?? last;
        if (end <= origin)
            end = last;

        var tracks = new Dictionary<long, JointTrack>();
        foreach (var (modelId, property, axis, times, values) in raw)
        {
            if (!models.TryGetValue(modelId, out var model))
                continue;
            if (!tracks.TryGetValue(modelId, out var track))
            {
                tracks[modelId] = track = new JointTrack
                {
                    Joint = model.Name, Static = model,
                    DefaultT = defaults.GetValueOrDefault((modelId, "Lcl Translation"), model.T),
                    DefaultR = defaults.GetValueOrDefault((modelId, "Lcl Rotation"), model.R),
                    DefaultS = defaults.GetValueOrDefault((modelId, "Lcl Scaling"), model.S),
                };
            }
            var curve = new Curve { Times = times.Select(k => (float)((k - origin) / TicksPerSecond)).ToArray(), Values = values };
            var slot = property switch { "Lcl Translation" => track.T, "Lcl Rotation" => track.R, _ => track.S };
            slot[axis] = curve;
        }
        // Joints listed without any curve keep their stored defaults, which can differ from the rest pose.
        foreach (var ((modelId, property), _) in defaults)
        {
            if (!tracks.ContainsKey(modelId) && models.TryGetValue(modelId, out var model))
                tracks[modelId] = new JointTrack
                {
                    Joint = model.Name, Static = model,
                    DefaultT = defaults.GetValueOrDefault((modelId, "Lcl Translation"), model.T),
                    DefaultR = defaults.GetValueOrDefault((modelId, "Lcl Rotation"), model.R),
                    DefaultS = defaults.GetValueOrDefault((modelId, "Lcl Scaling"), model.S),
                };
        }
        string name = CleanName(stack.Props.Count > 1 ? stack.Props[1] as string ?? "" : "");
        return new FbxClip
        {
            Name = string.IsNullOrEmpty(name) ? "Take" : name,
            Duration = (float)Math.Max((end - origin) / TicksPerSecond, 1.0 / 60.0),
            Tracks = tracks.Values.ToList(),
        };
    }

    private static string CleanName(string name)
    {
        int cut = name.IndexOf('\0');
        if (cut >= 0)
            name = name[..cut];
        int colon = name.LastIndexOf("::", StringComparison.Ordinal);
        return colon >= 0 ? name[(colon + 2)..] : name;
    }

    private static Node? ReadNode(byte[] data, ref long pos, bool wide, int depth)
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
        // Embedded media and the heavy per-corner layers (normals, UVs) are not needed: skip them unread.
        if (node.Name is "Video" or "Texture" or "LayerElementNormal" or "LayerElementUV" or "LayerElementBinormal"
            or "LayerElementTangent" or "LayerElementColor" or "LayerElementMaterial" or "LayerElementSmoothing"
            or "Takes" or "Documents" or "References" or "Definitions" or "Edges"
            || (depth == 0 && node.Name is not ("GlobalSettings" or "Objects" or "Connections")))
        {
            pos = end;
            return node;
        }
        bool arrays = ArrayNodes.Contains(node.Name);
        for (long i = 0; i < count; i++)
            node.Props.Add(ReadProperty(data, ref pos, arrays));
        while (pos < end)
        {
            var child = ReadNode(data, ref pos, wide, depth + 1);
            if (child == null)
                break;
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
                switch (type)
                {
                    case 'd':
                    {
                        var values = new double[length];
                        Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
                        return values;
                    }
                    case 'f':
                    {
                        var values = new double[length];
                        for (int i = 0; i < length; i++)
                            values[i] = BitConverter.ToSingle(raw, 4 * i);
                        return values;
                    }
                    case 'i':
                    {
                        var values = new int[length];
                        Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
                        return values;
                    }
                    case 'l':
                    {
                        var values = new long[length];
                        Buffer.BlockCopy(raw, 0, values, 0, raw.Length);
                        return values;
                    }
                    default:
                        return length;
                }
            }
            default:
                throw new InvalidDataException($"Unknown FBX property type '{type}'.");
        }
    }
}
