using System.Text.Json;
using System.Text.Json.Nodes;
using Seamstress.Core.Animation;

namespace Seamstress.Mcp;

public sealed partial class McpToolRegistry
{
    private static JsonObject PlayAnimationTool() => Tool("play_animation",
        "Play an animation on the avatar for N frames with every visible, unfrozen garment simulating on the moving body (Marvelous Designer style), and report how the cloth held up: peak speed, seam gaps, open seams, how deep and how much cloth went into the body, and frame times. Built-ins need no file: 'builtin:t-pose-to-a-pose' (arms lift level, then lower to 45 degrees) and 'builtin:arm-raise'. A rigged FBX or glTF/GLB path plays its clip on that file's skinned body, which must be the avatar the garments are on. The drape and avatar go back to how they were unless keepResult is true.",
        Props(
            ("animation", "string", "'builtin:t-pose-to-a-pose', 'builtin:arm-raise', or the path of a rigged .fbx/.glb/.gltf with animation clips."),
            ("clip", "string", "Clip name or 1-based number in the file (default: the first)."),
            ("frames", "integer", "Solver frames to run (1/60 s each; default: the whole animation at the given speed)."),
            ("speed", "number", "Animation seconds per simulated second (default 1; 0.5 plays in slow motion, which the cloth follows more easily)."),
            ("blendInSeconds", "number", "Seconds blending from the draped pose into the clip's first frame (default 0 for built-ins, 1 for files)."),
            ("substeps", "integer", "XPBD substeps per frame (default: the project's)."),
            ("selfCollision", "boolean", "Cloth-to-cloth collision (default: the project's)."),
            ("useGpu", "boolean", "Use CUDA when available (default true)."),
            ("cellCm", "number", "Collision grid spacing for the moving body, cm (default 1; 1.5-2 updates faster, less exact)."),
            ("recordPath", "string", "Optional .glb path: write the recorded cloth and body as a point cache (one glTF morph target per frame, weights animated) that Blender and game engines import."),
            ("keepResult", "boolean", "Keep the cloth and the avatar in the last frame's pose (default false: put both back).")),
        "animation");

    private object PlayAnimation(JsonElement args) => _session.PlayAnimation(new GarmentSession.AnimationRequest(
        ReqString(args, "animation"),
        OptString(args, "clip") ?? (args.TryGetProperty("clip", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32().ToString() : null),
        args.TryGetProperty("frames", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : null,
        OptFloat(args, "speed", 1f),
        TryOptFloat(args, "blendInSeconds"),
        args.TryGetProperty("substeps", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null,
        args.TryGetProperty("selfCollision", out var sc) && sc.ValueKind is JsonValueKind.True or JsonValueKind.False ? sc.GetBoolean() : null,
        OptBool(args, "useGpu", true),
        OptFloat(args, "cellCm", 1f),
        OptString(args, "recordPath"),
        OptBool(args, "keepResult", false)));

    /// <summary>The animations an agent can name without a file.</summary>
    public static IReadOnlyList<string> BuiltInAnimationIds => BuiltInAnimations.Ids;
}
