using System.Numerics;
using Seamstress.Core.Rigging;

namespace Seamstress.Core.IO;

public static partial class FbxMeshReader
{
    /// <summary>
    /// Collects the skin deformers of an FBX file while its meshes are read, then builds one skeleton for all of
    /// them. Skin → Geometry, Cluster → Skin and bone Model → Cluster are object-to-object connections; each Cluster
    /// holds the bone's vertex indices and weights and the bone's bind pose (TransformLink).
    /// </summary>
    private sealed class SkinCollector
    {
        private readonly Dictionary<long, Node> _deformers;
        private readonly List<(long Child, long Owner)> _links;
        private readonly Dictionary<long, long> _parent;
        private readonly Func<long, Matrix4x4> _world;
        private readonly Func<long, string> _name;
        private readonly Func<long, bool> _isModel;
        private readonly Matrix4x4 _toSeamstress;

        private readonly List<long> _bones = new();                 // temporary joint index -> bone model id
        private readonly Dictionary<long, int> _slot = new();
        private readonly Dictionary<long, Matrix4x4> _bind = new(); // bone model id -> bind pose, Seamstress space
        private readonly List<int> _skinnedParts = new();

        public SkinCollector(Dictionary<long, Node> deformers, List<(long, long)> links, Dictionary<long, long> parent,
            Func<long, Matrix4x4> world, Func<long, string> name, Func<long, bool> isModel, Matrix4x4 toSeamstress)
        {
            _isModel = isModel;
            _deformers = deformers;
            _links = links;
            _parent = parent;
            _world = world;
            _name = name;
            _toSeamstress = toSeamstress;
        }

        /// <summary>The skin of a geometry, with joints as temporary indices; null when it has none.</summary>
        public VertexWeights? Read(long geometryId, int vertexCount)
        {
            var influences = new List<(int, float)>[vertexCount];
            bool any = false;
            foreach (var (skinId, _) in _links.Where(l => l.Owner == geometryId && IsDeformer(l.Child, "Skin")))
            {
                foreach (var (clusterId, _) in _links.Where(l => l.Owner == skinId && IsDeformer(l.Child, "Cluster")))
                {
                    var cluster = _deformers[clusterId];
                    long bone = _links.FirstOrDefault(l => l.Owner == clusterId && _isModel(l.Child)).Child;
                    if (bone == 0 || cluster.Child("Indexes")?.Props.FirstOrDefault() is not int[] indexes
                        || cluster.Child("Weights")?.Props.FirstOrDefault() is not double[] weights)
                        continue;
                    if (!_slot.TryGetValue(bone, out int slot))
                    {
                        slot = _bones.Count;
                        _slot[bone] = slot;
                        _bones.Add(bone);
                    }
                    // TransformLink is the bone's world transform at bind time. (Exporters disagree on what
                    // "Transform" holds, so like Assimp this relies on the mesh being where it was when bound.)
                    if (!_bind.ContainsKey(bone) && Matrix(cluster, "TransformLink") is { } link)
                        _bind[bone] = Skeleton.Rigid(link * _toSeamstress);
                    for (int k = 0; k < indexes.Length && k < weights.Length; k++)
                    {
                        int v = indexes[k];
                        if (v < 0 || v >= vertexCount || weights[k] <= 0)
                            continue;
                        (influences[v] ??= new List<(int, float)>(4)).Add((slot, (float)weights[k]));
                        any = true;
                    }
                }
            }
            if (!any)
                return null;
            // Vertices no cluster lists follow the first bone that holds any vertex.
            int fallback = influences.First(l => l != null)[0].Item1;
            return VertexWeights.FromInfluences(influences.Select(l => (IEnumerable<(int, float)>?)l ?? Array.Empty<(int, float)>()).ToList(),
                _ => fallback);
        }

        public void Skinned(int partIndex) => _skinnedParts.Add(partIndex);

        /// <summary>Builds the skeleton from the bones used and their ancestors, and renumbers the parts' joints to it.</summary>
        public void Finish(MeshFile file)
        {
            if (_skinnedParts.Count == 0)
                return;
            var needed = new HashSet<long>();
            foreach (long bone in _bones)
            {
                long n = bone;
                for (int guard = 0; guard < 1024 && n != 0 && needed.Add(n) && _parent.TryGetValue(n, out long up); guard++)
                    n = up;
            }
            int Depth(long n)
            {
                int d = 0;
                while (d < 1024 && _parent.TryGetValue(n, out n))
                    d++;
                return d;
            }
            var order = needed.OrderBy(Depth).ThenBy(n => n).ToList();
            var indexOf = new Dictionary<long, int>();
            for (int i = 0; i < order.Count; i++)
                indexOf[order[i]] = i;
            var joints = order.Select(n => new Joint(
                _name(n),
                _parent.TryGetValue(n, out long up) && indexOf.TryGetValue(up, out int parent) ? parent : -1,
                _bind.TryGetValue(n, out var bind) ? bind : Skeleton.Rigid(_world(n) * _toSeamstress))).ToList();
            file.Skeleton = new Skeleton(joints);
            foreach (int p in _skinnedParts)
            {
                var skin = file.Parts[p].Skin!;
                var remapped = skin.Joints.Select((slot, k) => skin.Weights[k] > 0 ? indexOf[_bones[slot]] : 0).ToArray();
                file.Parts[p] = file.Parts[p] with { Skin = new VertexWeights(remapped, skin.Weights) };
            }
        }

        private bool IsDeformer(long id, string kind) => _deformers.TryGetValue(id, out var d) && d.Props.Count > 2 && d.Props[2] as string == kind;

        private static Matrix4x4? Matrix(Node node, string name)
        {
            if (node.Child(name)?.Props.FirstOrDefault() is not double[] v || v.Length != 16)
                return null;
            // FBX stores column-vector matrices column by column, which reads as the row-vector matrix row by row.
            return new Matrix4x4(
                (float)v[0], (float)v[1], (float)v[2], (float)v[3], (float)v[4], (float)v[5], (float)v[6], (float)v[7],
                (float)v[8], (float)v[9], (float)v[10], (float)v[11], (float)v[12], (float)v[13], (float)v[14], (float)v[15]);
        }
    }

    /// <summary>The file's axes and unit turned into Seamstress's (metres, Y up) as a row-vector matrix.</summary>
    private static Matrix4x4 ToSeamstress(int upAxis, float metres)
    {
        var axes = upAxis switch
        {
            2 => new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1),  // Z up: (x, y, z) -> (x, z, -y)
            0 => new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),  // X up: (x, y, z) -> (-y, x, z)
            _ => Matrix4x4.Identity,
        };
        return axes * Matrix4x4.CreateScale(metres);
    }
}
