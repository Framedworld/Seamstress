using System.Numerics;
using System.Text;
using System.Text.Json;
using Seamstress.Mcp;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Rigging;
using Seamstress.Core.Simulation;
using Xunit;

namespace Seamstress.Tests;

/// <summary>
/// Rigged export: reading a body's skeleton and skin, carrying the weights over to garments, and writing skinned
/// glTF and FBX that load back with their skin. The body is a small skinned column built here: three joints up its
/// middle (Hips, Spine, Chest) with weights blending between them.
/// </summary>
public class RiggingTests : IDisposable
{
    // SEAMSTRESS_DUMP keeps the exported files, e.g. to open them in Blender.
    private readonly string _folder = Environment.GetEnvironmentVariable("SEAMSTRESS_DUMP") is { } dump
        ? Path.Combine(dump, "rig")
        : Path.Combine(Path.GetTempPath(), "seamstress-rig-" + Guid.NewGuid().ToString("N"));

    public RiggingTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("SEAMSTRESS_DUMP") != null)
            return;
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private static readonly float[] JointHeights = { 0.5f, 1.0f, 1.3f };

    /// <summary>A closed column 1.6 m tall, 15 cm in radius, skinned to Hips, Spine and Chest.</summary>
    internal static (TriMesh Mesh, AvatarRig Rig) TestBody()
    {
        var (positions, triangles) = Tube(0.15f, 0f, 1.6f, 33, 24, capped: true);
        var joints = new List<Joint>
        {
            new("Hips", -1, Matrix4x4.CreateTranslation(0, JointHeights[0], 0)),
            new("Spine", 0, Matrix4x4.CreateTranslation(0, JointHeights[1], 0)),
            new("Chest", 1, Matrix4x4.CreateTranslation(0, JointHeights[2], 0)),
        };
        var influences = positions.Select(p => (IEnumerable<(int, float)>)HeightWeights(p.Y)).ToList();
        var rig = new AvatarRig { Skeleton = new Skeleton(joints), Weights = VertexWeights.FromInfluences(influences) };
        return (new TriMesh { Positions = positions, Triangles = triangles }, rig);
    }

    private static (int, float)[] HeightWeights(float y)
    {
        if (y <= JointHeights[0])
            return new[] { (0, 1f) };
        for (int j = 0; j + 1 < JointHeights.Length; j++)
        {
            if (y <= JointHeights[j + 1])
            {
                float t = (y - JointHeights[j]) / (JointHeights[j + 1] - JointHeights[j]);
                return new[] { (j, 1 - t), (j + 1, t) };
            }
        }
        return new[] { (JointHeights.Length - 1, 1f) };
    }

    /// <summary>A cylinder of rings round the Y axis; capped, it is closed at both ends.</summary>
    internal static (Vector3[] Positions, int[] Triangles) Tube(float radius, float y0, float y1, int rings, int segments, bool capped)
    {
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        for (int r = 0; r < rings; r++)
        {
            float y = y0 + (y1 - y0) * r / (rings - 1);
            for (int s = 0; s < segments; s++)
            {
                float a = 2 * MathF.PI * s / segments;
                positions.Add(new Vector3(radius * MathF.Cos(a), y, radius * MathF.Sin(a)));
            }
        }
        for (int r = 0; r + 1 < rings; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                int a = r * segments + s, b = r * segments + (s + 1) % segments, c = a + segments, d = b + segments;
                triangles.AddRange(new[] { a, c, b, b, c, d });
            }
        }
        if (capped)
        {
            int bottom = positions.Count, top = bottom + 1;
            positions.Add(new Vector3(0, y0, 0));
            positions.Add(new Vector3(0, y1, 0));
            int last = (rings - 1) * segments;
            for (int s = 0; s < segments; s++)
            {
                triangles.AddRange(new[] { bottom, s, (s + 1) % segments });
                triangles.AddRange(new[] { top, last + (s + 1) % segments, last + s });
            }
        }
        return (positions.ToArray(), triangles.ToArray());
    }

    private static Avatar TestAvatar()
    {
        var (mesh, rig) = TestBody();
        return new Avatar { Name = "Column", Mesh = mesh, Sdf = SdfGrid.FromMesh(mesh), Rig = rig };
    }

    /// <summary>A shirt-like tube 2 cm out from the body, from below the Spine joint to above the Chest joint.</summary>
    private static (Vector3[] Positions, int[] Triangles) Shirt() => Tube(0.17f, 0.85f, 1.45f, 25, 32, capped: false);

    private static float Difference(Matrix4x4 a, Matrix4x4 b)
    {
        var d = a - b;
        return new[] { d.M11, d.M12, d.M13, d.M14, d.M21, d.M22, d.M23, d.M24, d.M31, d.M32, d.M33, d.M34, d.M41, d.M42, d.M43, d.M44 }
            .Max(MathF.Abs);
    }

    private static void AssertNormalised(VertexWeights weights, int jointCount)
    {
        for (int v = 0; v < weights.VertexCount; v++)
        {
            float sum = 0;
            int used = 0;
            for (int k = 0; k < VertexWeights.MaxInfluences; k++)
            {
                float w = weights.Weights[v * 4 + k];
                Assert.InRange(w, 0f, 1f);
                if (w > 0)
                {
                    used++;
                    Assert.InRange(weights.Joints[v * 4 + k], 0, jointCount - 1);
                }
                sum += w;
            }
            Assert.InRange(used, 1, VertexWeights.MaxInfluences);
            Assert.InRange(sum, 0.999f, 1.001f);
        }
    }

    [Fact]
    public void TransferredWeightsSumToOneWithAtMostFourInfluences()
    {
        var (body, rig) = TestBody();
        var (positions, triangles) = Shirt();
        var weights = SkinTransfer.Transfer(body, rig.Weights, positions, triangles);
        Assert.Equal(positions.Length, weights.VertexCount);
        AssertNormalised(weights, rig.Skeleton.Count);

        // Cloth over the chest follows the chest, cloth over the waist mostly the spine and hips.
        int Strongest(int v) => weights.Of(v).OrderByDescending(i => i.Weight).First().Joint;
        Assert.Equal(2, Strongest(positions.Length - 1));
        Assert.NotEqual(2, Strongest(0));
    }

    [Fact]
    public void FourStrongestInfluencesAreKeptAndNormalised()
    {
        var w = VertexWeights.FromInfluences(new[]
        {
            (IEnumerable<(int, float)>)new[] { (0, 0.1f), (1, 0.4f), (2, 0.2f), (3, 0.05f), (4, 0.25f), (1, 0.1f) },
            Array.Empty<(int, float)>(),
        }, _ => 3);
        var first = w.Of(0).ToDictionary(i => i.Joint, i => i.Weight);
        Assert.Equal(new[] { 0, 1, 2, 4 }, first.Keys.OrderBy(k => k));
        Assert.Equal(0.5f / 1.05f, first[1], 1e-4f);
        Assert.Equal(1f, first.Values.Sum(), 1e-4f);
        Assert.Equal(new[] { (3, 1f) }, w.Of(1).ToArray()); // nothing given: bound fully to the fallback joint
    }

    [Fact]
    public void SmoothingKeepsLooseClothInOnePiece()
    {
        // A skirt hanging between two legs: without smoothing each side would follow its own leg and tear apart.
        var (body, rig) = TestBody();
        var (positions, triangles) = Shirt();
        var raw = SkinTransfer.Transfer(body, rig.Weights, positions, triangles, new SkinTransferOptions { SmoothingIterations = 0 });
        var smooth = SkinTransfer.Transfer(body, rig.Weights, positions, triangles);
        float Roughness(VertexWeights w)
        {
            // Largest weight jump along any cloth edge.
            float worst = 0;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = triangles[t + e], b = triangles[t + (e + 1) % 3];
                    var wa = Enumerable.Range(0, 3).Select(j => w.Of(a).Where(i => i.Joint == j).Sum(i => i.Weight)).ToArray();
                    var wb = Enumerable.Range(0, 3).Select(j => w.Of(b).Where(i => i.Joint == j).Sum(i => i.Weight)).ToArray();
                    worst = MathF.Max(worst, wa.Zip(wb, (x, y) => MathF.Abs(x - y)).Max());
                }
            }
            return worst;
        }
        Assert.True(Roughness(smooth) <= Roughness(raw) + 1e-4f);
    }

    [Fact]
    public void BendingAJointCarriesTheGarmentWithTheBody()
    {
        var (body, rig) = TestBody();
        var (positions, triangles) = Shirt();
        var weights = SkinTransfer.Transfer(body, rig.Weights, positions, triangles);

        // Bend the spine 35 degrees sideways.
        int spine = rig.Skeleton.IndexOf("Spine");
        var pose = rig.Skeleton.Pose(new Dictionary<int, Matrix4x4>
        {
            [spine] = Matrix4x4.CreateRotationZ(35 * MathF.PI / 180) * rig.Skeleton.BindLocal(spine),
        });
        var bentBody = Skinning.Deform(body.Positions, rig.Weights, rig.Skeleton, pose);
        var bentShirt = Skinning.Deform(positions, weights, rig.Skeleton, pose);

        var before = new TriangleLocator(body.Positions, body.Triangles);
        var after = new TriangleLocator(bentBody, body.Triangles);
        float worst = 0, total = 0;
        for (int v = 0; v < positions.Length; v++)
        {
            float change = MathF.Abs(after.Closest(bentShirt[v]).Distance - before.Closest(positions[v]).Distance);
            worst = MathF.Max(worst, change);
            total += change;
        }
        // The cloth stays about as far from the body as it was draped...
        Assert.True(total / positions.Length < 0.006f, $"mean gap change {total / positions.Length * 100:F2} cm");
        Assert.True(worst < 0.03f, $"worst gap change {worst * 100:F2} cm");
        // ...while the top of the shirt really moved with the chest.
        int topVertex = positions.Length - 1;
        Assert.True(Vector3.Distance(positions[topVertex], bentShirt[topVertex]) > 0.1f);
        Assert.True(Vector3.Distance(bentShirt[topVertex], bentBody[body.Positions.Length - 1]) < 0.4f);
    }

    private static ExportScene ShirtScene()
    {
        var (positions, triangles) = Shirt();
        var material = new FabricMaterial { Id = "cotton", Name = "Cotton" };
        var mesh = new ExportMesh
        {
            Name = "Shirt",
            Positions = positions,
            Normals = Seamstress.Core.Geometry.Mesher.ComputeNormals(positions, triangles),
            Uvs = positions.Select(p => new Vector2(MathF.Atan2(p.Z, p.X), p.Y)).ToArray(),
            Parts = new List<ExportPart>
            {
                new()
                {
                    Name = "Body", Material = material, PositionIndices = triangles, UvIndices = triangles,
                    FaceSizes = Enumerable.Repeat(3, triangles.Length / 3).ToArray(), FacePositionIndices = triangles, FaceUvIndices = triangles,
                },
            },
        };
        return new ExportScene { Meshes = new List<ExportMesh> { mesh } };
    }

    private static JsonDocument ReadGlbJson(string path, out byte[] bin)
    {
        var data = File.ReadAllBytes(path);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(data, 0));
        Assert.Equal((uint)data.Length, BitConverter.ToUInt32(data, 8));
        int jsonLength = BitConverter.ToInt32(data, 12);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(data, 20, jsonLength));
        int binAt = 20 + jsonLength;
        bin = data.AsSpan(binAt + 8, BitConverter.ToInt32(data, binAt)).ToArray();
        return json;
    }

    private static Matrix4x4 NodeLocal(JsonElement node)
    {
        var t = node.TryGetProperty("translation", out var tt) ? new Vector3(tt[0].GetSingle(), tt[1].GetSingle(), tt[2].GetSingle()) : Vector3.Zero;
        var r = node.TryGetProperty("rotation", out var rr) ? new Quaternion(rr[0].GetSingle(), rr[1].GetSingle(), rr[2].GetSingle(), rr[3].GetSingle()) : Quaternion.Identity;
        return Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
    }

    [Fact]
    public void SkinnedGlbHasAValidSkinAndLoadsBackWithIt()
    {
        var scene = ShirtScene();
        RiggedExport.Bind(scene, TestAvatar(), includeAvatar: true);
        string path = Path.Combine(_folder, "rigged.glb");
        GltfExporter.Export(scene, path);

        // Structure: the skin lists every joint with an inverse bind matrix that undoes the joint's world transform.
        using (var json = ReadGlbJson(path, out var bin))
        {
            var root = json.RootElement;
            var nodes = root.GetProperty("nodes");
            var skin = root.GetProperty("skins")[0];
            var joints = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
            Assert.Equal(3, joints.Length);
            var accessors = root.GetProperty("accessors");
            var views = root.GetProperty("bufferViews");
            var ibm = accessors[skin.GetProperty("inverseBindMatrices").GetInt32()];
            Assert.Equal("MAT4", ibm.GetProperty("type").GetString());
            Assert.Equal(5126, ibm.GetProperty("componentType").GetInt32());
            Assert.Equal(joints.Length, ibm.GetProperty("count").GetInt32());
            int ibmOffset = views[ibm.GetProperty("bufferView").GetInt32()].GetProperty("byteOffset").GetInt32();

            var parentOf = new Dictionary<int, int>();
            for (int n = 0; n < nodes.GetArrayLength(); n++)
            {
                if (nodes[n].TryGetProperty("children", out var children))
                    foreach (var c in children.EnumerateArray())
                        parentOf[c.GetInt32()] = n;
            }
            Matrix4x4 World(int n) => parentOf.TryGetValue(n, out int p) ? NodeLocal(nodes[n]) * World(p) : NodeLocal(nodes[n]);
            for (int k = 0; k < joints.Length; k++)
            {
                var v = Enumerable.Range(0, 16).Select(e => BitConverter.ToSingle(bin, ibmOffset + 64 * k + 4 * e)).ToArray();
                var inverse = new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
                var product = inverse * World(joints[k]);
                Assert.True(Difference(product, Matrix4x4.Identity) < 1e-4f, $"joint {k}");
            }
            Assert.Equal("Spine", nodes[joints[1]].GetProperty("name").GetString());

            // Both meshes (avatar and shirt) are skinned, with one JOINTS_0 and WEIGHTS_0 per vertex.
            var meshNodes = Enumerable.Range(0, nodes.GetArrayLength()).Where(n => nodes[n].TryGetProperty("mesh", out _)).ToList();
            Assert.Equal(2, meshNodes.Count);
            foreach (int n in meshNodes)
            {
                Assert.Equal(0, nodes[n].GetProperty("skin").GetInt32());
                foreach (var prim in root.GetProperty("meshes")[nodes[n].GetProperty("mesh").GetInt32()].GetProperty("primitives").EnumerateArray())
                {
                    var attributes = prim.GetProperty("attributes");
                    var positions = accessors[attributes.GetProperty("POSITION").GetInt32()];
                    var jointsAccessor = accessors[attributes.GetProperty("JOINTS_0").GetInt32()];
                    var weightsAccessor = accessors[attributes.GetProperty("WEIGHTS_0").GetInt32()];
                    int count = positions.GetProperty("count").GetInt32();
                    Assert.Equal(count, jointsAccessor.GetProperty("count").GetInt32());
                    Assert.Equal(count, weightsAccessor.GetProperty("count").GetInt32());
                    Assert.Equal("VEC4", jointsAccessor.GetProperty("type").GetString());
                    Assert.Equal(5123, jointsAccessor.GetProperty("componentType").GetInt32());
                    int at = views[weightsAccessor.GetProperty("bufferView").GetInt32()].GetProperty("byteOffset").GetInt32();
                    for (int v = 0; v < count; v++)
                    {
                        float sum = 0;
                        for (int k = 0; k < 4; k++)
                            sum += BitConverter.ToSingle(bin, at + 16 * v + 4 * k);
                        Assert.InRange(sum, 0.999f, 1.001f);
                    }
                }
            }
        }

        // And it loads back as a rigged avatar.
        var file = GltfMeshReader.Load(path, _ => true);
        Assert.NotNull(file.Skeleton);
        Assert.Equal(new[] { "Hips", "Spine", "Chest" }, file.Skeleton!.Joints.Select(j => j.Name));
        Assert.Equal(new[] { -1, 0, 1 }, file.Skeleton.Joints.Select(j => j.Parent));
        Assert.Equal(1.3f, file.Skeleton.Joints[2].BindWorld.Translation.Y, 1e-4f);
        Assert.All(file.Parts, p => Assert.NotNull(p.Skin));
        var avatar = Avatar.FromFile(path, 1f, name => name == "Column");
        Assert.NotNull(avatar.Rig);
        Assert.Equal(avatar.Mesh.Positions.Length, avatar.Rig!.Weights.VertexCount);
        AssertNormalised(avatar.Rig.Weights, 3);
    }

    [Fact]
    public void SkinnedFbxLoadsBackWithItsSkin()
    {
        var scene = ShirtScene();
        RiggedExport.Bind(scene, TestAvatar(), includeAvatar: false);
        string path = Path.Combine(_folder, "rigged.fbx");
        FbxExporter.Export(scene, path);

        var file = FbxMeshReader.Load(path, _ => true);
        Assert.NotNull(file.Skeleton);
        Assert.Equal(new[] { "Hips", "Spine", "Chest" }, file.Skeleton!.Joints.Select(j => j.Name));
        Assert.Equal(new[] { -1, 0, 1 }, file.Skeleton.Joints.Select(j => j.Parent));
        for (int j = 0; j < 3; j++)
            Assert.Equal(JointHeights[j], file.Skeleton.Joints[j].BindWorld.Translation.Y, 1e-3f);
        var part = Assert.Single(file.Parts);
        Assert.NotNull(part.Skin);
        AssertNormalised(part.Skin!, 3);
        // The weights read back are the ones written.
        var written = scene.Meshes[0].Skin!;
        for (int v = 0; v < written.VertexCount; v += 37)
        {
            var a = written.Of(v).ToDictionary(i => file.Skeleton.IndexOf(scene.Skeleton!.Joints[i.Joint].Name), i => i.Weight);
            var b = part.Skin!.Of(v).ToDictionary(i => i.Joint, i => i.Weight);
            Assert.Equal(a.Keys.OrderBy(k => k), b.Keys.OrderBy(k => k));
            foreach (var (joint, weight) in a)
                Assert.Equal(weight, b[joint], 1e-4f);
        }
    }

    private static string Asset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "Seamstress.Tests", "Assets")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."), "tests", "Seamstress.Tests", "Assets", name);
    }

    /// <summary>The same column rigged in Blender and saved with its glTF and FBX exporters (Assets/make_rigged_column.py).</summary>
    [Theory]
    [InlineData("rigged-column.glb")]
    [InlineData("rigged-column.fbx")]
    public void ReadsTheSkinOfABodyRiggedInBlender(string name)
    {
        var avatar = Avatar.FromFile(Asset(name));
        var rig = avatar.Rig ?? throw new Xunit.Sdk.XunitException("no skeleton read");
        Assert.Equal(avatar.Mesh.Positions.Length, rig.Weights.VertexCount);
        AssertNormalised(rig.Weights, rig.Skeleton.Count);
        var (min, max) = avatar.Mesh.Bounds();
        Assert.Equal(1.6f, max.Y - min.Y, 1e-3f);

        // The bones are where Blender put them, carried into Seamstress space (metres, Y up) like the mesh.
        for (int j = 0; j < 3; j++)
        {
            int index = rig.Skeleton.IndexOf(new[] { "Hips", "Spine", "Chest" }[j]);
            Assert.True(index >= 0, $"joint {j} missing: {string.Join(", ", rig.Skeleton.Joints.Select(k => k.Name))}");
            var at = rig.Skeleton.Joints[index].BindWorld.Translation;
            Assert.Equal(JointHeights[j], at.Y, 1e-3f);
            Assert.Equal(0f, at.X, 1e-3f);
            Assert.Equal(0f, at.Z, 1e-3f);
        }
        Assert.Equal(rig.Skeleton.IndexOf("Spine"), rig.Skeleton.Joints[rig.Skeleton.IndexOf("Chest")].Parent);

        // Every vertex carries the weights it was painted with.
        for (int v = 0; v < avatar.Mesh.Positions.Length; v++)
        {
            var expected = HeightWeights(avatar.Mesh.Positions[v].Y)
                .Where(e => e.Item2 > 1e-4f)
                .ToDictionary(e => rig.Skeleton.IndexOf(new[] { "Hips", "Spine", "Chest" }[e.Item1]), e => e.Item2);
            foreach (var (joint, weight) in rig.Weights.Of(v))
                Assert.Equal(expected.GetValueOrDefault(joint), weight, 2e-3f);
        }

        // Bending the spine bends the top of the body and leaves its feet where they are.
        int spine = rig.Skeleton.IndexOf("Spine");
        var pose = rig.Skeleton.Pose(new Dictionary<int, Matrix4x4>
        {
            [spine] = Matrix4x4.CreateRotationZ(0.6f) * rig.Skeleton.BindLocal(spine),
        });
        var bent = Skinning.Deform(avatar.Mesh.Positions, rig.Weights, rig.Skeleton, pose);
        int top = Array.IndexOf(avatar.Mesh.Positions, avatar.Mesh.Positions.MaxBy(p => p.Y));
        int bottom = Array.IndexOf(avatar.Mesh.Positions, avatar.Mesh.Positions.MinBy(p => p.Y));
        Assert.True(Vector3.Distance(bent[top], avatar.Mesh.Positions[top]) > 0.2f);
        Assert.True(Vector3.Distance(bent[bottom], avatar.Mesh.Positions[bottom]) < 1e-4f);
    }

    [Fact]
    public void FbxJointRotationsSurviveTheEulerRoundTrip()
    {
        var rotation = Matrix4x4.CreateFromYawPitchRoll(0.7f, -0.4f, 1.1f);
        var e = FbxExporter.EulerXyzDegrees(rotation) * (MathF.PI / 180);
        var rebuilt = Matrix4x4.CreateRotationX(e.X) * Matrix4x4.CreateRotationY(e.Y) * Matrix4x4.CreateRotationZ(e.Z);
        Assert.True(Difference(rebuilt, rotation) < 1e-5f);
    }

    [Fact]
    public void ExportGarmentToolWritesARiggedGlb()
    {
        // A rigged body on disk, loaded the way a user would.
        string bodyPath = Path.Combine(_folder, "column.glb");
        var (mesh, rig) = TestBody();
        var avatar = new Avatar { Name = "Column", Mesh = mesh, Sdf = SdfGrid.FromMesh(mesh), Rig = rig };
        GltfExporter.Export(new ExportScene { Meshes = new List<ExportMesh> { RiggedExport.AvatarMesh(avatar, rig) }, Skeleton = rig.Skeleton }, bodyPath);

        var session = new GarmentSession();
        var tools = new McpToolRegistry(session);
        using (var load = JsonDocument.Parse(JsonSerializer.Serialize(new { path = bodyPath })))
        {
            var loaded = JsonSerializer.SerializeToElement(tools.Dispatch("load_avatar", load.RootElement));
            Assert.Equal(3, loaded.GetProperty("skeletonJoints").GetInt32());
        }
        using (var add = JsonDocument.Parse("{\"template\":\"Skirt\"}"))
            tools.Dispatch("add_template_garment", add.RootElement);

        string outPath = Path.Combine(_folder, "skirt-rigged.glb");
        using (var export = JsonDocument.Parse(JsonSerializer.Serialize(new { path = outPath, rigged = true, includeAvatar = true })))
        {
            var result = JsonSerializer.SerializeToElement(tools.Dispatch("export_garment", export.RootElement));
            Assert.Equal(3, result.GetProperty("joints").GetInt32());
            Assert.Equal(2, result.GetProperty("meshes").GetInt32());
        }
        var file = GltfMeshReader.Load(outPath, _ => true);
        Assert.Equal(3, file.Skeleton!.Count);
        Assert.Equal(2, file.Parts.Count);
        Assert.All(file.Parts, p => AssertNormalised(p.Skin!, 3));

        // OBJ has nowhere to put a skeleton, and an unrigged body has no skeleton to give.
        using (var obj = JsonDocument.Parse(JsonSerializer.Serialize(new { path = Path.Combine(_folder, "x.obj"), rigged = true })))
            Assert.ThrowsAny<Exception>(() => tools.Dispatch("export_garment", obj.RootElement));
        session.ResetMannequinAvatar();
        using (var glb = JsonDocument.Parse(JsonSerializer.Serialize(new { path = Path.Combine(_folder, "y.glb"), rigged = true })))
            Assert.ThrowsAny<Exception>(() => tools.Dispatch("export_garment", glb.RootElement));
    }
}
