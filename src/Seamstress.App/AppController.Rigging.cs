using Seamstress.Core;
using Seamstress.Core.IO;
using Seamstress.Core.Rigging;

namespace Seamstress.App;

/// <summary>Rigged export: garments bound to a rigged avatar's skeleton with skin weights, for game engines.</summary>
public partial class AppController
{
    // Set only while ExportRiggedTo runs ExportTo: whether to bind, and whether to add the body.
    private (bool IncludeAvatar, bool Active) _rigExport;

    /// <summary>How many joints the current avatar's skeleton has; 0 when the body is not rigged.</summary>
    public int AvatarJointCount() => _avatar?.Rig?.Skeleton.Count ?? 0;

    /// <summary>Like <see cref="ExportTo"/> for "gltf" or "fbx", with the garments skinned to the avatar's skeleton.</summary>
    public bool ExportRiggedTo(string format, string fileUrl, bool weld, bool activeOnly, bool includeAvatar)
    {
        if (format == "obj")
        {
            Fail(Lang.T("Export failed."), new InvalidOperationException(Lang.T("OBJ files cannot hold a skeleton. Choose glTF or FBX for a rigged export.")));
            return false;
        }
        _rigExport = (includeAvatar, true);
        try
        {
            return ExportTo(format, fileUrl, weld, activeOnly);
        }
        finally
        {
            _rigExport = default;
        }
    }

    /// <summary>Called by <see cref="ExportTo"/>: binds the scene to the skeleton when a rigged export asked for it.</summary>
    private void ApplyRigExport(ExportScene scene)
    {
        if (!_rigExport.Active)
            return;
        // Waits for an avatar that is still loading, as an agent's tool call does.
        var avatar = ((Seamstress.Mcp.ISessionHost)this).Avatar
            ?? throw new InvalidOperationException(Lang.T("The avatar has no skeleton. Load a rigged body (FBX or glTF with a skin) to export garments ready to animate."));
        RiggedExport.Bind(scene, avatar, _rigExport.IncludeAvatar);
        AppLog.Write($"Bound {scene.Meshes.Count} mesh(es) to the avatar's {scene.Skeleton!.Count} joints");
    }
}
