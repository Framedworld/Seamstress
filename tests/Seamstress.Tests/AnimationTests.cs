using System.Globalization;
using System.Numerics;
using System.Text;
using Seamstress.Core.Animation;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;
using Seamstress.Cuda;
using Xunit.Abstractions;

namespace Seamstress.Tests;

/// <summary>Avatar animation: skinning from a rigged file, cloth on a moving body, and the recorded point cache.</summary>
public class AnimationTests : IClassFixture<AvatarFixture>
{
    private readonly ITestOutputHelper _output;
    private readonly Avatar _mannequin;

    public AnimationTests(AvatarFixture fixture, ITestOutputHelper output)
    {
        _mannequin = fixture.Mannequin;
        _output = output;
    }

    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), $"seamstress-anim-{Guid.NewGuid():N}-{name}");

    /// <summary>
    /// A 1 m tall box of two-by-two rings, skinned to a root joint (bottom) and an "Upper" joint at half height that the
    /// clip turns 90° about Z over one second. The top ring belongs to Upper, the middle ring half to each.
    /// </summary>
    private static string WriteRiggedGltf()
    {
        var positions = new List<Vector3>();
        var joints = new List<byte>();
        var weights = new List<float>();
        float[] heights = { 0f, 0.5f, 1f };
        foreach (float y in heights)
        {
            foreach (var (x, z) in new[] { (-0.1f, -0.1f), (0.1f, -0.1f), (0.1f, 0.1f), (-0.1f, 0.1f) })
            {
                positions.Add(new Vector3(x, y, z));
                float upper = y switch { 0f => 0f, 0.5f => 0.5f, _ => 1f };
                joints.AddRange(new byte[] { 0, 1, 0, 0 });
                weights.AddRange(new[] { 1 - upper, upper, 0f, 0f });
            }
        }
        var indices = new List<ushort>();
        for (int ring = 0; ring < 2; ring++)
        {
            for (int k = 0; k < 4; k++)
            {
                ushort a = (ushort)(ring * 4 + k), b = (ushort)(ring * 4 + (k + 1) % 4), c = (ushort)(a + 4), d = (ushort)(b + 4);
                indices.AddRange(new[] { a, b, d, a, d, c });
            }
        }
        indices.AddRange(new ushort[] { 0, 2, 1, 0, 3, 2, 8, 9, 10, 8, 10, 11 });

        var bin = new MemoryStream();
        var w = new BinaryWriter(bin);
        foreach (var p in positions) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }      // 0: positions
        int jointsAt = (int)bin.Length;
        foreach (byte j in joints) w.Write(j);                                          // joints (u8 x4)
        int weightsAt = (int)bin.Length;
        foreach (float f in weights) w.Write(f);                                       // weights
        int indicesAt = (int)bin.Length;
        foreach (ushort i in indices) w.Write(i);
        while (bin.Length % 4 != 0) w.Write((byte)0);
        int ibmAt = (int)bin.Length;
        // Inverse bind matrices: root at the origin, Upper at y = 0.5.
        foreach (var m in new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -0.5f, 0) })
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) w.Write(m[r, c]);
        int timesAt = (int)bin.Length;
        w.Write(0f); w.Write(1f);
        int rotAt = (int)bin.Length;
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);
        w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W);
        w.Flush();
        string b64 = Convert.ToBase64String(bin.ToArray());
        string F(float v) => v.ToString(CultureInfo.InvariantCulture);
        string json = $$$"""
        {
          "asset": {"version": "2.0"},
          "scene": 0,
          "scenes": [{"nodes": [0, 2]}],
          "nodes": [
            {"name": "Root", "children": [1]},
            {"name": "Upper", "translation": [0, {{{F(0.5f)}}}, 0]},
            {"name": "Body", "mesh": 0, "skin": 0}
          ],
          "skins": [{"joints": [0, 1], "inverseBindMatrices": 4}],
          "meshes": [{"name": "Body", "primitives": [{"attributes": {"POSITION": 0, "JOINTS_0": 1, "WEIGHTS_0": 2}, "indices": 3}]}],
          "animations": [{"name": "Bend", "samplers": [{"input": 5, "output": 6, "interpolation": "LINEAR"}],
                          "channels": [{"sampler": 0, "target": {"node": 1, "path": "rotation"}}]}],
          "buffers": [{"byteLength": {{{bin.Length}}}, "uri": "data:application/octet-stream;base64,{{{b64}}}"}],
          "bufferViews": [
            {"buffer": 0, "byteOffset": 0, "byteLength": {{{jointsAt}}}},
            {"buffer": 0, "byteOffset": {{{jointsAt}}}, "byteLength": {{{weightsAt - jointsAt}}}},
            {"buffer": 0, "byteOffset": {{{weightsAt}}}, "byteLength": {{{indicesAt - weightsAt}}}},
            {"buffer": 0, "byteOffset": {{{indicesAt}}}, "byteLength": {{{indices.Count * 2}}}},
            {"buffer": 0, "byteOffset": {{{ibmAt}}}, "byteLength": 128},
            {"buffer": 0, "byteOffset": {{{timesAt}}}, "byteLength": 8},
            {"buffer": 0, "byteOffset": {{{rotAt}}}, "byteLength": 32}
          ],
          "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": 12, "type": "VEC3", "min": [-0.1, 0, -0.1], "max": [0.1, 1, 0.1]},
            {"bufferView": 1, "componentType": 5121, "count": 12, "type": "VEC4"},
            {"bufferView": 2, "componentType": 5126, "count": 12, "type": "VEC4"},
            {"bufferView": 3, "componentType": 5123, "count": {{{indices.Count}}}, "type": "SCALAR"},
            {"bufferView": 4, "componentType": 5126, "count": 2, "type": "MAT4"},
            {"bufferView": 5, "componentType": 5126, "count": 2, "type": "SCALAR", "min": [0], "max": [1]},
            {"bufferView": 6, "componentType": 5126, "count": 2, "type": "VEC4"}
          ]
        }
        """;
        string path = Temp("rig.gltf");
        File.WriteAllText(path, json, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void SkinsASmallRiggedBodyFromGltf()
    {
        string path = WriteRiggedGltf();
        try
        {
            var avatar = Avatar.FromFile(path);
            var rig = RigFile.Load(path);
            Assert.NotNull(rig.Body);
            Assert.Single(rig.Clips);
            Assert.Equal("Bend", rig.Clips[0].Name);
            Assert.Equal(1f, rig.Clips[0].Duration, 0.001f);
            var body = rig.Body!;
            Assert.True(body.FitTo(avatar.Mesh) < 1e-5f, "the bind pose is the avatar loaded from the same file");

            var locals = new Matrix4x4[body.Skeleton.Count];
            var posed = new Vector3[body.VertexCount];
            rig.Clips[0].Sample(body.Skeleton, 0, body.Skeleton.RestLocal, locals);
            body.Pose(locals, posed);
            for (int i = 0; i < posed.Length; i++)
                Assert.True(Vector3.Distance(posed[i], avatar.Mesh.Positions[i]) < 1e-5f);

            rig.Clips[0].Sample(body.Skeleton, 1, body.Skeleton.RestLocal, locals);
            body.Pose(locals, posed);
            // The avatar was stood on the ground and centred: the joint is at y = 0.5 above the lowest point.
            var pivot = avatar.Mesh.Positions[4] + new Vector3(0.1f, 0, 0.1f);
            var turn = Matrix4x4.CreateRotationZ(MathF.PI / 2);
            for (int i = 8; i < 12; i++)
            {
                var expected = Vector3.Transform(avatar.Mesh.Positions[i] - pivot, turn) + pivot;
                Assert.True(Vector3.Distance(posed[i], expected) < 1e-4f, $"top vertex {i}: {posed[i]} vs {expected}");
            }
            for (int i = 4; i < 8; i++)
            {
                // Half weighted: halfway between staying and turning.
                var turned = Vector3.Transform(avatar.Mesh.Positions[i] - pivot, turn) + pivot;
                var expected = (avatar.Mesh.Positions[i] + turned) / 2;
                Assert.True(Vector3.Distance(posed[i], expected) < 1e-4f, $"middle vertex {i}");
            }
            for (int i = 0; i < 4; i++)
                Assert.True(Vector3.Distance(posed[i], avatar.Mesh.Positions[i]) < 1e-5f, "the root ring stays put");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuiltInPosesMoveOnlyTheArms()
    {
        var anim = AnimationLibrary.Prepare(_mannequin, null, BuiltInAnimations.TPoseToAPose);
        var rest = _mannequin.Mesh.Positions;
        var tpose = new Vector3[rest.Length];
        anim.PoseAt(1.15f, tpose); // holding the T-pose
        var rig = AutoRig.Build(_mannequin.Mesh);
        float shoulderY = rig.Body.Skeleton.RestLocal[rig.Shoulder[0]].Translation.Y;
        var hands = Enumerable.Range(0, rest.Length).Where(i => tpose[i].X > 0.5f).ToList();
        Assert.True(hands.Count > 20, "the arm reaches out sideways in the T-pose");
        float handY = hands.Average(i => tpose[i].Y);
        Assert.InRange(handY, shoulderY - 0.08f, shoulderY + 0.08f);
        var moved = Enumerable.Range(0, rest.Length).Where(i => (MathF.Abs(rest[i].X) < 0.06f || (rest[i].Y < 1.05f && MathF.Abs(rest[i].X) < 0.2f)) && Vector3.Distance(rest[i], tpose[i]) > 1e-4f).ToList();
        foreach (int i in moved.Take(5))
            _output.WriteLine($"torso vertex {rest[i]} moved {Vector3.Distance(rest[i], tpose[i]) * 1000:0.0} mm");
        _output.WriteLine($"pivot {rig.Body.Skeleton.RestLocal[rig.Shoulder[0]].Translation}, droop {rig.DroopDegrees[0]:0.0}");
        Assert.True(moved.Count == 0, $"{moved.Count} torso vertices moved");
        var start = new Vector3[rest.Length];
        anim.PoseAt(0, start);
        Assert.True(start.Zip(rest).All(p => Vector3.Distance(p.First, p.Second) < 1e-4f), "the clip starts from the draped pose");
    }

    /// <summary>A closed capsule-ish cylinder along +X from the origin, at height <paramref name="y"/>.</summary>
    private static TriMesh Arm(float y, float length, float radius)
    {
        const int rings = 31, sides = 24;
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        for (int r = 0; r < rings; r++)
        {
            float x = length * r / (rings - 1);
            for (int s = 0; s < sides; s++)
            {
                float a = MathF.Tau * s / sides;
                positions.Add(new Vector3(x, y + radius * MathF.Cos(a), radius * MathF.Sin(a)));
            }
        }
        int startCap = positions.Count;
        positions.Add(new Vector3(0, y, 0));
        int endCap = positions.Count;
        positions.Add(new Vector3(length, y, 0));
        void Tri(int a, int b, int c, Vector3 outward)
        {
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            if (Vector3.Dot(n, outward) < 0)
                (b, c) = (c, b);
            triangles.AddRange(new[] { a, b, c });
        }
        for (int r = 0; r + 1 < rings; r++)
        {
            for (int s = 0; s < sides; s++)
            {
                int a = r * sides + s, b = r * sides + (s + 1) % sides, c = a + sides, d = b + sides;
                var centre = (positions[a] + positions[d]) / 2;
                var outward = centre - new Vector3(centre.X, y, 0);
                Tri(a, b, d, outward);
                Tri(a, d, c, outward);
            }
        }
        for (int s = 0; s < sides; s++)
        {
            Tri(startCap, s, (s + 1) % sides, -Vector3.UnitX);
            int last = (rings - 1) * sides;
            Tri(endCap, last + s, last + (s + 1) % sides, Vector3.UnitX);
        }
        return new TriMesh { Positions = positions.ToArray(), Triangles = triangles.ToArray() };
    }

    /// <summary>An arm on a shoulder joint at the origin of the arm, held for 1.5 s, raised 40° over 1 s, held.</summary>
    private static BodyAnimation RaisingArm(TriMesh mesh, float y)
    {
        var pivot = new Vector3(0, y, 0);
        var skeleton = new Skeleton
        {
            Names = new[] { "Root", "Shoulder" },
            Parents = new[] { -1, 0 },
            RestLocal = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(pivot) },
        };
        int n = mesh.Positions.Length;
        var joints = new int[n * 4];
        var weights = new float[n * 4];
        for (int v = 0; v < n; v++)
        {
            joints[v * 4] = 1;
            weights[v * 4] = 1;
        }
        var body = new SkinnedBody
        {
            Skeleton = skeleton,
            BindPositions = mesh.Positions,
            Triangles = mesh.Triangles,
            Joints = joints,
            Weights = weights,
            InverseBind = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(-pivot) },
        };
        var clip = new ProceduralClip
        {
            Name = "Raise",
            Duration = 3f,
            Evaluate = (t, rest, locals) =>
            {
                float angle = 40f * MathF.PI / 180f * Pose.Ease((t - 1.5f) / 1f);
                locals[1] = Matrix4x4.CreateRotationZ(angle) * rest[1];
            },
        };
        return BodyAnimation.Create(body, clip, 0f);
    }

    public static IEnumerable<object[]> Backends() => new[] { new object[] { false }, new object[] { true } };

    [Theory]
    [MemberData(nameof(Backends))]
    public void ClothStaysOnAMovingLimbAndTheRecordingRoundTrips(bool gpu)
    {
        if (gpu && !CudaClothBackend.Register())
        {
            _output.WriteLine("No CUDA device: GPU case skipped.");
            return;
        }
        const float y = 1.0f, length = 0.6f, radius = 0.05f;
        var arm = Arm(y, length, radius);
        var anim = RaisingArm(arm, y);
        anim.TrackPenetration = true;

        var ws = new Workspace();
        var sleeve = new Garment { Name = "Sleeve", ParticleDistance = 1.5f };
        ws.Document.Garments.Add(sleeve);
        GarmentBlocks.AddTube(ws.Document, sleeve, "Sleeve", new Vector3(0.12f, y, 0), Vector3.UnitX, Vector3.UnitY,
            MathF.Tau * (radius + 0.012f) * 100, MathF.Tau * (radius + 0.012f) * 100, 36, splitStart: false);
        var settings = new SimulationSettings { Substeps = 12, GroundPlane = false, SelfCollision = false };

        bool before = ClothAcceleration.Enabled;
        ClothAcceleration.Enabled = gpu;
        string glb = Temp("cache.glb");
        try
        {
            using var solver = ClothSolver.Create(new[] { ws.MeshFor(sleeve) }, Array.Empty<GarmentMesh>(), anim.Field.Grid, settings);
            _output.WriteLine($"{solver.BackendName}, {solver.ParticleCount} particles, field {anim.Field.CellCount} cells");
            if (gpu)
                Assert.StartsWith("CUDA", solver.BackendName);
            int frames = (int)MathF.Ceiling(anim.Duration / solver.FrameDuration);
            float settledPenetration = 0;
            for (int f = 0; f < frames; f++)
            {
                anim.BeforeStep(solver);
                solver.Step();
                anim.AfterStep(solver);
                if (f == 80)
                {
                    settledPenetration = anim.MaxPenetration;
                    anim.ResetStatistics(); // the raise starts at 1.5 s (frame 90)
                }
            }
            Assert.True(solver.BackendName.StartsWith("CUDA") == gpu, "the backend kept up with the moving body");
            _output.WriteLine($"settle penetration {settledPenetration * 1000:0.0} mm; moving: max {anim.MaxPenetration * 1000:0.0} mm, " +
                $"{anim.MaxPenetratingParticles} particles; peak {anim.PeakSpeed:0.00} m/s; field {anim.FieldMilliseconds / Math.Max(1, anim.Frames):0.00} ms/frame");
            Assert.True(anim.MaxPenetration < 0.004f, $"cloth went {anim.MaxPenetration * 1000:0.0} mm into the moving arm");

            // The sleeve went up with the arm: every particle is still round the raised arm.
            var turn = Matrix4x4.CreateRotationZ(40f * MathF.PI / 180f);
            var axis = Vector3.TransformNormal(Vector3.UnitX, turn);
            var pivot = new Vector3(0, y, 0);
            float worst = 0;
            foreach (var p in solver.Positions)
            {
                var d = p - pivot;
                float along = Vector3.Dot(d, axis);
                worst = MathF.Max(worst, (d - axis * along).Length());
                Assert.InRange(along, 0.0f, length + 0.05f);
            }
            _output.WriteLine($"farthest cloth from the raised arm's axis: {worst * 100:0.0} cm");
            Assert.True(worst < radius + 0.06f, $"cloth was left behind: {worst * 100:0.0} cm from the arm's axis");

            // Recording round trip.
            var recording = anim.Recording;
            var recorded = recording.Snapshot();
            Assert.InRange(recorded.Count, 85, 95); // 3 s at 30 frames a second
            var result = PointCacheExporter.Write(glb, recording, ws);
            Assert.Equal(recorded.Count, result.Frames);
            var cache = PointCacheReader.Read(glb);
            Assert.Equal(2, cache.Meshes.Count);
            Assert.Equal(recorded.Count, cache.Times.Length);
            var cloth = cache.Meshes.Single(m => m.Name == "Sleeve");
            var bodyMesh = cache.Meshes.Single(m => m.Name == "Avatar");
            for (int f = 0; f < recorded.Count; f++)
            {
                Assert.Equal(recorded[f].Time - recorded[0].Time, cache.Times[f], 0.0001f);
                for (int i = 0; i < recorded[f].Cloth.Length; i++)
                    Assert.True(Vector3.Distance(recorded[f].Cloth[i], cloth.Frames[f][i]) < 1e-5f);
                for (int i = 0; i < recorded[f].Body.Length; i += 7)
                    Assert.True(Vector3.Distance(recorded[f].Body[i], bodyMesh.Frames[f][i]) < 1e-5f);
            }
            Assert.Equal(ws.MeshFor(sleeve).Pieces[0].Triangles, cloth.Triangles);
            // Ordinary glTF readers take it as a mesh file too.
            var asMesh = GltfMeshReader.Load(glb, _ => true);
            Assert.Contains(asMesh.Parts, p => p.Name == "Sleeve");
        }
        finally
        {
            ClothAcceleration.Enabled = before;
            File.Delete(glb);
        }
    }
}
