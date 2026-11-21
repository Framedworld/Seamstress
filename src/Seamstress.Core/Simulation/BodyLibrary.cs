using System.Numerics;
using System.Text.Json;
using Seamstress.Core.Model;

namespace Seamstress.Core.Simulation;

/// <summary>JSON-serializable definition of a body shape in the Seamstress body library.</summary>
public sealed class BodyProfile
{
    public string Id { get; set; } = Document.NewId();
    public string Name { get; set; } = "Body";
    public string Description { get; set; } = "";

    /// <summary>Source mesh type: <c>"Mannequin"</c>, <c>"Obj"</c>, <c>"MorphTargetObj"</c>, or <c>"MetaHumanDna"</c>.</summary>
    public string SourceType { get; set; } = "Mannequin";

    /// <summary>Path to the base OBJ or MetaHuman <c>.dna</c> file, when applicable.</summary>
    public string? BaseMeshPath { get; set; }

    /// <summary>Unit conversion factor to metres (e.g. <c>0.01f</c> for centimetre Unreal/ZBrush meshes).</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Converts Z-up source meshes (such as Unreal/MetaHuman exports) to Seamstress Y-up.</summary>
    public bool ZUpToYUp { get; set; }

    /// <summary>Parametric vertical scale applied around Y = 0 while preserving exact mesh topology.</summary>
    public float HeightScale { get; set; } = 1f;

    /// <summary>Parametric lateral scale applied around X = 0 while preserving exact mesh topology.</summary>
    public float WidthScale { get; set; } = 1f;

    /// <summary>Parametric front-to-back scale applied around torso Z centre while preserving exact mesh topology.</summary>
    public float DepthScale { get; set; } = 1f;

    /// <summary>Named same-topology morph target OBJ file paths (morph name -> OBJ path).</summary>
    public Dictionary<string, string> MorphTargetPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Blend weights for morph targets in <see cref="MorphTargetPaths"/> (morph name -> weight).</summary>
    public Dictionary<string, float> MorphWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Manages saved <see cref="BodyProfile"/> definitions on disk and builds live <see cref="Avatar"/> instances
/// (mesh + collision SDF) from parametric mannequins, OBJ meshes, morph-target OBJ blends, or MetaHuman DNA files.
/// </summary>
public sealed class BodyLibrary
{
    private static readonly Lazy<TriMesh> BaseMannequinMesh = new(() => Avatar.CreateMannequin().Mesh);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultFolder
        => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "bodies");

    public string Folder { get; }

    public BodyLibrary(string? folder = null)
    {
        Folder = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder;
    }

    /// <summary>Lists every valid <see cref="BodyProfile"/> stored in <see cref="Folder"/>.</summary>
    public List<BodyProfile> ListProfiles()
    {
        var profiles = new List<BodyProfile>();
        if (!Directory.Exists(Folder))
            return profiles;

        foreach (string file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            var profile = TryReadProfile(file);
            if (profile != null)
                profiles.Add(profile);
        }

        profiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return profiles;
    }

    /// <summary>Loads a saved profile by its <see cref="BodyProfile.Id"/> or <see cref="BodyProfile.Name"/>.</summary>
    public BodyProfile? LoadProfile(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName) || !Directory.Exists(Folder))
            return null;

        string directPath = ProfileFilePath(idOrName);
        if (File.Exists(directPath) && TryReadProfile(directPath) is { } direct)
            return direct;

        foreach (var profile in ListProfiles())
        {
            if (profile.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase)
                || profile.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
                return profile;
        }
        return null;
    }

    /// <summary>Saves a profile as JSON in <see cref="Folder"/>.</summary>
    public void SaveProfile(BodyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id))
            profile.Id = Document.NewId();
        Directory.CreateDirectory(Folder);
        string path = ProfileFilePath(profile.Id);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    /// <summary>Deletes a saved profile matching <paramref name="idOrName"/>. Returns true when a file was removed.</summary>
    public bool DeleteProfile(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName) || !Directory.Exists(Folder))
            return false;

        string directPath = ProfileFilePath(idOrName);
        if (File.Exists(directPath))
        {
            File.Delete(directPath);
            return true;
        }

        foreach (string file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            var profile = TryReadProfile(file);
            if (profile != null
                && (profile.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase)
                    || profile.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(file);
                return true;
            }
        }
        return false;
    }

    /// <summary>Builds a live <see cref="Avatar"/> (render mesh + collision <see cref="SdfGrid"/>) from a profile.</summary>
    public Avatar BuildAvatar(BodyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string sourceType = (profile.SourceType ?? "Mannequin").Trim();

        if (sourceType.Equals("Mannequin", StringComparison.OrdinalIgnoreCase))
            return BuildMannequinAvatar(profile);

        if (sourceType.Equals("Obj", StringComparison.OrdinalIgnoreCase)
            || sourceType.Equals("MorphTargetObj", StringComparison.OrdinalIgnoreCase))
            return BuildObjAvatar(profile);

        if (sourceType.Equals("MetaHumanDna", StringComparison.OrdinalIgnoreCase))
            return BuildMetaHumanDnaAvatar(profile);

        throw new NotSupportedException($"Unsupported body profile SourceType '{profile.SourceType}'.");
    }

    private static Avatar BuildMannequinAvatar(BodyProfile profile)
    {
        var baseMesh = BaseMannequinMesh.Value;
        var scaled = ScaleBodyMesh(baseMesh, profile.WidthScale, profile.HeightScale, profile.DepthScale);
        return new Avatar
        {
            Name = string.IsNullOrWhiteSpace(profile.Name) ? "Mannequin" : profile.Name,
            Mesh = scaled,
            Sdf = SdfGrid.FromMesh(scaled),
        };
    }

    private static Avatar BuildObjAvatar(BodyProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.BaseMeshPath) || !File.Exists(profile.BaseMeshPath))
            throw new FileNotFoundException($"Base mesh OBJ not found: '{profile.BaseMeshPath}'.", profile.BaseMeshPath);

        var baseObj = ObjFile.Load(profile.BaseMeshPath);
        int vertexCount = baseObj.Positions.Length;
        var baseConverted = new Vector3[vertexCount];
        var blended = new Vector3[vertexCount];
        float scale = profile.Scale > 0 ? profile.Scale : 1f;
        for (int i = 0; i < vertexCount; i++)
        {
            var p = MetaHumanDnaLoader.ConvertPoint(baseObj.Positions[i], scale, profile.ZUpToYUp);
            baseConverted[i] = p;
            blended[i] = p;
        }

        if (profile.MorphTargetPaths.Count > 0 && profile.MorphWeights.Count > 0)
        {
            foreach (var (morphName, weight) in profile.MorphWeights)
            {
                if (MathF.Abs(weight) < 1e-6f)
                    continue;
                if (!profile.MorphTargetPaths.TryGetValue(morphName, out string? targetPath)
                    || string.IsNullOrWhiteSpace(targetPath))
                    continue;
                if (!File.Exists(targetPath))
                    throw new FileNotFoundException($"Morph target OBJ '{morphName}' not found: '{targetPath}'.", targetPath);

                var targetObj = ObjFile.Load(targetPath);
                if (targetObj.Positions.Length != vertexCount)
                {
                    throw new InvalidDataException(
                        $"Morph target '{morphName}' has {targetObj.Positions.Length} vertices, expected {vertexCount} to match base mesh.");
                }

                for (int i = 0; i < vertexCount; i++)
                {
                    var targetPos = MetaHumanDnaLoader.ConvertPoint(targetObj.Positions[i], scale, profile.ZUpToYUp);
                    blended[i] += weight * (targetPos - baseConverted[i]);
                }
            }
        }

        var morphedObj = new ObjFile { Positions = blended, Faces = baseObj.Faces };
        var mesh = MetaHumanDnaLoader.BuildGroundedMesh(morphedObj, scale: 1f, zUpToYUp: false);
        mesh = ScaleBodyMesh(mesh, profile.WidthScale, profile.HeightScale, profile.DepthScale);
        mesh = MetaHumanDnaLoader.GroundMesh(mesh);

        return new Avatar
        {
            Name = string.IsNullOrWhiteSpace(profile.Name)
                ? Path.GetFileNameWithoutExtension(profile.BaseMeshPath)
                : profile.Name,
            Mesh = mesh,
            Sdf = SdfGrid.FromMesh(mesh),
        };
    }

    private Avatar BuildMetaHumanDnaAvatar(BodyProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.BaseMeshPath) || !File.Exists(profile.BaseMeshPath))
            throw new FileNotFoundException($"MetaHuman DNA file not found: '{profile.BaseMeshPath}'.", profile.BaseMeshPath);

        var metadata = MetaHumanDnaLoader.Inspect(profile.BaseMeshPath);
        if (!metadata.IsValidDna)
            throw new InvalidDataException($"'{profile.BaseMeshPath}' is not a valid MetaHuman DNA file: {metadata.Summary}");

        string cacheDir = Path.Combine(Folder, ".dna-cache");
        string safeId = SanitizeFileName(string.IsNullOrWhiteSpace(profile.Id) ? Path.GetFileNameWithoutExtension(profile.BaseMeshPath) : profile.Id);
        string extractedObj = Path.Combine(cacheDir, safeId + ".obj");

        if (!MetaHumanDnaLoader.TryExtractMeshViaPython(profile.BaseMeshPath, extractedObj, out string? error))
            throw new InvalidOperationException(error ?? "Failed to extract mesh from MetaHuman DNA.");

        float scale = profile.Scale > 0 ? profile.Scale : 0.01f;
        var avatar = MetaHumanDnaLoader.LoadUnrealBodyObj(extractedObj, scale, profile.ZUpToYUp);
        if (MathF.Abs(profile.WidthScale - 1f) > 1e-5f
            || MathF.Abs(profile.HeightScale - 1f) > 1e-5f
            || MathF.Abs(profile.DepthScale - 1f) > 1e-5f)
        {
            var scaled = MetaHumanDnaLoader.GroundMesh(ScaleBodyMesh(avatar.Mesh, profile.WidthScale, profile.HeightScale, profile.DepthScale));
            avatar = new Avatar
            {
                Name = string.IsNullOrWhiteSpace(profile.Name) ? avatar.Name : profile.Name,
                Mesh = scaled,
                Sdf = SdfGrid.FromMesh(scaled),
            };
        }
        else if (!string.IsNullOrWhiteSpace(profile.Name))
        {
            avatar = new Avatar
            {
                Name = profile.Name,
                Mesh = avatar.Mesh,
                Sdf = avatar.Sdf,
            };
        }
        return avatar;
    }

    private static TriMesh ScaleBodyMesh(TriMesh baseMesh, float widthScale, float heightScale, float depthScale)
    {
        float sx = Math.Clamp(widthScale, 0.2f, 5f);
        float sy = Math.Clamp(heightScale, 0.2f, 5f);
        float sz = Math.Clamp(depthScale, 0.2f, 5f);

        int count = baseMesh.Positions.Length;
        double sumZ = 0;
        for (int i = 0; i < count; i++)
            sumZ += baseMesh.Positions[i].Z;
        float bodyCenterZ = count > 0 ? (float)(sumZ / count) : 0f;

        var scaled = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            var p = baseMesh.Positions[i];
            scaled[i] = new Vector3(
                p.X * sx,
                p.Y * sy,
                bodyCenterZ + (p.Z - bodyCenterZ) * sz);
        }

        // Preserve exact topology by reusing the triangle index buffer (read-only by convention).
        var triangles = new int[baseMesh.Triangles.Length];
        Array.Copy(baseMesh.Triangles, triangles, triangles.Length);
        return new TriMesh { Positions = scaled, Triangles = triangles };
    }

    private string ProfileFilePath(string id) => Path.Combine(Folder, SanitizeFileName(id) + ".json");

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        string cleaned = new string(chars);
        return string.IsNullOrWhiteSpace(cleaned) ? "body" : cleaned;
    }

    private static BodyProfile? TryReadProfile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<BodyProfile>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
