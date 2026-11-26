using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.Animation;

/// <summary>
/// Turns "what to play" (a built-in pose or a rigged FBX/glTF file) into a <see cref="BodyAnimation"/> for the
/// avatar the garments are draped on. A file's clip plays on that file's own skinned body when it is the avatar;
/// otherwise on the avatar's own rigged file, matched joint by joint by name.
/// </summary>
public static class AnimationLibrary
{
    /// <summary>The bind pose may differ from the avatar by this much (metres) and still count as the same body.</summary>
    public const float MatchTolerance = 0.005f;

    /// <summary>Clip names in a rigged file, for a picker.</summary>
    public static List<string> ClipNames(string path) => RigFile.Load(path).Clips.Select(c => c.Name).ToList();

    /// <param name="animation">A built-in id (<see cref="BuiltInAnimations"/>) or the path of a rigged FBX or glTF file.</param>
    /// <param name="clip">Clip name or number in the file; null takes the first.</param>
    /// <param name="blendIn">Seconds to blend from the draped pose into the clip; null picks (0 for built-ins, 1 s for files).</param>
    /// <param name="cell">Collision grid spacing, metres.</param>
    public static BodyAnimation Prepare(Avatar avatar, AvatarSettings? settings, string animation, string? clip = null,
        float? blendIn = null, float cell = 0.01f)
    {
        if (BuiltInAnimations.IsBuiltIn(animation))
        {
            var rig = AutoRig.Build(avatar.Mesh);
            if (rig.Shoulder.All(j => j < 0))
                throw new InvalidDataException("No arms were found on this avatar, so the built-in poses cannot move them.");
            var builtIn = BuiltInAnimations.Create(animation, rig);
            return BodyAnimation.Create(rig.Body, builtIn, blendIn ?? 0f, cell, BuiltInAnimations.DisplayName(animation));
        }

        string path = Path.GetFullPath(animation);
        if (!File.Exists(path))
            throw new FileNotFoundException($"There is no animation file at '{path}'.", path);
        bool sameFile = settings?.Path is { } avatarPath && Path.GetFullPath(avatarPath).Equals(path, StringComparison.OrdinalIgnoreCase);
        var file = RigFile.Load(path, sameFile ? settings!.Scale : 1f);
        if (file.Clips.Count == 0)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' has no animation in it.");
        AnimationClip chosen = file.Clips[0];
        if (clip != null)
        {
            chosen = file.Clips.FirstOrDefault(c => c.Name.Equals(clip, StringComparison.OrdinalIgnoreCase))
                ?? (int.TryParse(clip, out int n) && n >= 1 && n <= file.Clips.Count ? file.Clips[n - 1]
                    : throw new ArgumentException($"'{Path.GetFileName(path)}' has no clip '{clip}'. Its clips are: {string.Join(", ", file.Clips.Select(c => c.Name))}."));
        }

        SkinnedBody? body = null;
        if (file.Body != null && file.Body.FitTo(avatar.Mesh) < MatchTolerance)
            body = file.Body;
        else if (settings?.Path is { } own && !sameFile && File.Exists(own)
            && Path.GetExtension(own).ToLowerInvariant() is ".fbx" or ".glb" or ".gltf")
        {
            var ownRig = RigFile.Load(own, settings.Scale);
            if (ownRig.Body != null && ownRig.Body.FitTo(avatar.Mesh) < MatchTolerance && file.Skeleton != null)
            {
                var retargeted = new RetargetedClip { Name = chosen.Name, Duration = chosen.Duration, Inner = chosen, Source = file.Skeleton };
                // Most of the skeleton has to be there: a clip for another character's rig would only tear the body.
                if (retargeted.Matched(ownRig.Body.Skeleton) >= ownRig.Body.Skeleton.Count / 2)
                {
                    body = ownRig.Body;
                    chosen = retargeted;
                }
            }
        }
        if (body == null)
            throw new InvalidDataException(
                "This animation's body is not the avatar the garments are on. Load the rigged FBX or glTF file itself as the avatar, or play a built-in pose.");

        float lead = blendIn ?? 1f;
        return BodyAnimation.Create(body, chosen, lead, cell, $"{Path.GetFileName(path)}: {chosen.Name}");
    }
}
