using System.Numerics;
using Seamstress.Core.Rigging;

namespace Seamstress.Core.IO;

public static partial class FbxExporter
{
    /// <summary>How many objects of each kind <see cref="AddSkeleton"/> writes, for the Definitions section.</summary>
    private static (int Joints, int Skins, int Clusters) SkinCounts(ExportScene scene)
    {
        if (scene.Skeleton is not { Count: > 0 })
            return (0, 0, 0);
        var skinned = scene.Meshes.Where(m => m.Skin != null).ToList();
        return (scene.Skeleton.Count, skinned.Count, skinned.Sum(m => UsedJoints(m.Skin!).Count));
    }

    private static SortedSet<int> UsedJoints(VertexWeights skin)
    {
        var used = new SortedSet<int>();
        for (int i = 0; i < skin.Joints.Length; i++)
        {
            if (skin.Weights[i] > 0)
                used.Add(skin.Joints[i]);
        }
        return used;
    }

    /// <summary>
    /// Writes the skeleton as LimbNode models, a Skin deformer per skinned mesh with a Cluster per joint it uses, and
    /// a bind pose. <paramref name="meshes"/> holds the geometry and model id of each scene mesh, in order.
    /// </summary>
    private static void AddSkeleton(ExportScene scene, Node objects, Node connections, Func<long> newId,
        IReadOnlyList<(long Geometry, long Model)> meshes, double cm)
    {
        if (scene.Skeleton is not { Count: > 0 } skeleton)
            return;

        // FBX matrices are stored column by column for column vectors, which is a row-vector matrix row by row.
        double[] Array16(Matrix4x4 m) => new double[]
        {
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41 * cm, m.M42 * cm, m.M43 * cm, m.M44,
        };

        var jointIds = new long[skeleton.Count];
        for (int j = 0; j < skeleton.Count; j++)
        {
            var joint = skeleton.Joints[j];
            long attributeId = newId(), modelId = newId();
            jointIds[j] = modelId;
            string name = joint.Name;

            var attribute = objects.Add("NodeAttribute", attributeId, name + "\0\u0001NodeAttribute", "LimbNode");
            attribute.Add("Properties70").P("Size", "double", "Number", "", 10.0);
            attribute.Add("TypeFlags", "Skeleton");

            var local = skeleton.BindLocal(j);
            var t = local.Translation;
            var r = EulerXyzDegrees(local);
            var model = objects.Add("Model", modelId, name + "\0\u0001Model", "LimbNode");
            model.Add("Version", 232);
            var props = model.Add("Properties70");
            props.P("InheritType", "enum", "", "", 1);
            props.P("DefaultAttributeIndex", "int", "Integer", "", 0);
            props.P("Lcl Translation", "Lcl Translation", "", "A", t.X * cm, t.Y * cm, t.Z * cm);
            props.P("Lcl Rotation", "Lcl Rotation", "", "A", (double)r.X, (double)r.Y, (double)r.Z);
            props.P("Lcl Scaling", "Lcl Scaling", "", "A", 1.0, 1.0, 1.0);
            model.Add("Shading", true);
            model.Add("Culling", "CullingOff");

            connections.Add("C", "OO", attributeId, modelId);
            connections.Add("C", "OO", modelId, joint.Parent < 0 ? 0L : jointIds[joint.Parent]);
        }

        var pose = objects.Add("Pose", newId(), "BindPose\0\u0001Pose", "BindPose");
        pose.Add("Type", "BindPose");
        pose.Add("Version", 100);
        var poseNodes = new List<(long Id, Matrix4x4 World)>();

        for (int i = 0; i < scene.Meshes.Count; i++)
        {
            if (scene.Meshes[i].Skin is not { } skin)
                continue;
            var (geometryId, meshModelId) = meshes[i];
            poseNodes.Add((meshModelId, Matrix4x4.Identity));
            long skinId = newId();
            var skinNode = objects.Add("Deformer", skinId, "\0\u0001Deformer", "Skin");
            skinNode.Add("Version", 101);
            skinNode.Add("Link_DeformAcuracy", 50.0);
            connections.Add("C", "OO", skinId, geometryId);

            foreach (int j in UsedJoints(skin))
            {
                var indexes = new List<int>();
                var weights = new List<double>();
                for (int v = 0; v < skin.VertexCount; v++)
                {
                    for (int k = 0; k < VertexWeights.MaxInfluences; k++)
                    {
                        int at = v * VertexWeights.MaxInfluences + k;
                        if (skin.Joints[at] == j && skin.Weights[at] > 0)
                        {
                            indexes.Add(v);
                            weights.Add(skin.Weights[at]);
                        }
                    }
                }
                var bind = skeleton.Joints[j].BindWorld;
                long clusterId = newId();
                var cluster = objects.Add("Deformer", clusterId, skeleton.Joints[j].Name + "\0\u0001SubDeformer", "Cluster");
                cluster.Add("Version", 100);
                cluster.Add("UserData", "", "");
                cluster.Add("Indexes", indexes.ToArray());
                cluster.Add("Weights", weights.ToArray());
                // As Blender writes it: Transform is the mesh's matrix relative to the bone, TransformLink the bone's
                // world matrix, both at bind time. The mesh sits at the origin.
                Matrix4x4.Invert(bind, out var meshInBone);
                cluster.Add("Transform", Array16(meshInBone));
                cluster.Add("TransformLink", Array16(bind));
                connections.Add("C", "OO", clusterId, skinId);
                connections.Add("C", "OO", jointIds[j], clusterId);
            }
        }

        for (int j = 0; j < skeleton.Count; j++)
            poseNodes.Add((jointIds[j], skeleton.Joints[j].BindWorld));
        pose.Add("NbPoseNodes", poseNodes.Count);
        foreach (var (id, world) in poseNodes)
        {
            var node = pose.Add("PoseNode");
            node.Add("Node", id);
            node.Add("Matrix", Array16(world));
        }
    }

    /// <summary>
    /// The X, Y, Z Euler angles in degrees of a rotation applied X first, then Y, then Z (FBX's default order), so
    /// that RotationX(x) * RotationY(y) * RotationZ(z) gives back <paramref name="m"/>.
    /// </summary>
    public static Vector3 EulerXyzDegrees(Matrix4x4 m)
    {
        const double deg = 180.0 / Math.PI;
        double sy = Math.Clamp(-m.M13, -1f, 1f);
        double x, y = Math.Asin(sy), z;
        if (Math.Abs(sy) < 0.99999)
        {
            x = Math.Atan2(m.M23, m.M33);
            z = Math.Atan2(m.M12, m.M11);
        }
        else
        {
            // Gimbal lock: Y is ±90°, so only X and Z together are known; put it all in X.
            x = Math.Atan2(-m.M32, m.M22);
            z = 0;
        }
        return new Vector3((float)(x * deg), (float)(y * deg), (float)(z * deg));
    }
}
