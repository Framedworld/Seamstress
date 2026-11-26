using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.Rigging;

/// <summary>Turns an export into one ready to animate: garments bound to the avatar's skeleton with skin weights.</summary>
public static class RiggedExport
{
    public static readonly FabricMaterial BodyMaterial = new()
    {
        Id = "avatar-body",
        Name = "Body",
        Color = "#C9A58E",
        Roughness = 0.6f,
    };

    /// <summary>Whether the avatar carries a skeleton that garments can be bound to.</summary>
    public static bool CanBind(Avatar? avatar) => avatar?.Rig is { Skeleton.Count: > 0 };

    /// <summary>
    /// Gives every mesh of <paramref name="scene"/> skin weights taken from the avatar and attaches the avatar's
    /// skeleton, so the glTF and FBX writers export skinned meshes. With <paramref name="includeAvatar"/> the body is
    /// added as a skinned mesh too.
    /// </summary>
    public static void Bind(ExportScene scene, Avatar avatar, bool includeAvatar, SkinTransferOptions? options = null)
    {
        if (avatar.Rig is not { Skeleton.Count: > 0 } rig)
            throw new InvalidOperationException(Lang.T("The avatar has no skeleton. Load a rigged body (FBX or glTF with a skin) to export garments ready to animate."));
        var locator = new TriangleLocator(avatar.Mesh.Positions, avatar.Mesh.Triangles);
        foreach (var mesh in scene.Meshes)
        {
            var triangles = mesh.Parts.SelectMany(p => p.PositionIndices).ToArray();
            mesh.Skin = SkinTransfer.Transfer(avatar.Mesh, rig.Weights, mesh.Positions, triangles, options, locator);
        }
        if (includeAvatar)
            scene.Meshes.Insert(0, AvatarMesh(avatar, rig));
        scene.Skeleton = rig.Skeleton;
    }

    /// <summary>The avatar's body as an export mesh with its own weights (it has no UVs, so they are all zero).</summary>
    public static ExportMesh AvatarMesh(Avatar avatar, AvatarRig rig)
    {
        var positions = avatar.Mesh.Positions;
        var triangles = avatar.Mesh.Triangles;
        return new ExportMesh
        {
            Name = string.IsNullOrWhiteSpace(avatar.Name) ? "Avatar" : avatar.Name,
            Positions = positions,
            Normals = Mesher.ComputeNormals(positions, triangles),
            Uvs = new Vector2[positions.Length],
            Parts = new List<ExportPart>
            {
                new()
                {
                    Name = "Body",
                    Material = BodyMaterial,
                    PositionIndices = triangles,
                    UvIndices = triangles,
                    FaceSizes = Enumerable.Repeat(3, triangles.Length / 3).ToArray(),
                    FacePositionIndices = triangles,
                    FaceUvIndices = triangles,
                },
            },
            Skin = rig.Weights,
        };
    }
}
