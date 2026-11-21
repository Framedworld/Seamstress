using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;

namespace Seamstress.Core.Simulation;

/// <summary>Header and capability metadata inspected from a MetaHuman <c>.dna</c> binary file.</summary>
public sealed class DnaMetadata
{
    public string FilePath { get; init; } = "";
    public bool IsValidDna { get; init; }
    public ushort FileFormatGeneration { get; init; }
    public ushort FileFormatVersion { get; init; }
    public long FileSizeBytes { get; init; }
    public string Summary { get; init; } = "";
    public bool HasOpenRigLogicBridge { get; init; }
}

/// <summary>
/// Inspects MetaHuman <c>.dna</c> binary headers per Epic's open DNA binary specification, optionally bridges to a
/// user-installed Python <c>dna</c>/<c>openriglogic</c> module to extract the neutral mesh, and loads Unreal/MetaHuman
/// exported body OBJ meshes (cm, Z-up) as Seamstress avatars (metres, Y-up, grounded on Y=0).
/// </summary>
public static class MetaHumanDnaLoader
{
    private const int HeaderMinLength = 7; // "DNA" (3 bytes) + big-endian uint16 generation + uint16 version
    private const int InspectReadLimit = 4096;

    private static readonly Lazy<string?> DetectedPythonCommand = new(DetectPythonWithDnaModule);

    /// <summary>True when a user-installed Python environment with <c>dna</c> or <c>openriglogic</c> is available.</summary>
    public static bool HasOpenRigLogicBridge => DetectedPythonCommand.Value != null;

    /// <summary>
    /// Validates a MetaHuman <c>.dna</c> binary header (<c>"DNA"</c> magic + big-endian format generation and version)
    /// and safely reads file size and printable descriptor metadata without throwing on unknown versions.
    /// </summary>
    public static DnaMetadata Inspect(string dnaPath)
    {
        bool hasBridge = HasOpenRigLogicBridge;
        if (string.IsNullOrWhiteSpace(dnaPath) || !File.Exists(dnaPath))
        {
            return new DnaMetadata
            {
                FilePath = dnaPath ?? "",
                IsValidDna = false,
                FileSizeBytes = 0,
                Summary = "DNA file not found.",
                HasOpenRigLogicBridge = hasBridge,
            };
        }

        try
        {
            var info = new FileInfo(dnaPath);
            long size = info.Length;
            int toRead = (int)Math.Min(size, InspectReadLimit);
            var buffer = new byte[toRead];
            using (var stream = File.OpenRead(dnaPath))
            {
                int offset = 0;
                while (offset < toRead)
                {
                    int read = stream.Read(buffer, offset, toRead - offset);
                    if (read <= 0)
                        break;
                    offset += read;
                }
                toRead = offset;
            }

            if (toRead < HeaderMinLength || buffer[0] != (byte)'D' || buffer[1] != (byte)'N' || buffer[2] != (byte)'A')
            {
                return new DnaMetadata
                {
                    FilePath = dnaPath,
                    IsValidDna = false,
                    FileSizeBytes = size,
                    Summary = "Invalid DNA header: missing 'DNA' binary signature.",
                    HasOpenRigLogicBridge = hasBridge,
                };
            }

            ushort generation = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(3, 2));
            ushort version = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(5, 2));

            var descriptors = ExtractPrintableTokens(buffer.AsSpan(HeaderMinLength, toRead - HeaderMinLength), maxTokens: 4);
            string summary = descriptors.Count > 0
                ? $"MetaHuman DNA v{generation}.{version} ({size:N0} bytes) — {string.Join(", ", descriptors)}"
                : $"MetaHuman DNA v{generation}.{version} ({size:N0} bytes)";

            return new DnaMetadata
            {
                FilePath = dnaPath,
                IsValidDna = true,
                FileFormatGeneration = generation,
                FileFormatVersion = version,
                FileSizeBytes = size,
                Summary = summary,
                HasOpenRigLogicBridge = hasBridge,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DnaMetadata
            {
                FilePath = dnaPath,
                IsValidDna = false,
                FileSizeBytes = 0,
                Summary = $"Unable to read DNA file: {ex.Message}",
                HasOpenRigLogicBridge = hasBridge,
            };
        }
    }

    /// <summary>
    /// Attempts to extract the neutral mesh from a <c>.dna</c> file into a Wavefront OBJ using a user-installed Python
    /// <c>dna</c> or <c>openriglogic</c> module. Never bundles proprietary Epic binaries.
    /// </summary>
    public static bool TryExtractMeshViaPython(string dnaPath, string outputObjPath, out string? error)
    {
        var metadata = Inspect(dnaPath);
        if (!metadata.IsValidDna)
        {
            error = $"'{dnaPath}' is not a valid MetaHuman DNA file ({metadata.Summary}).";
            return false;
        }

        string? python = DetectedPythonCommand.Value;
        if (python == null)
        {
            error = "No Python environment with the 'dna' or 'openriglogic' module was found on PATH. "
                + "Install Epic's open-source RigLogic Python bindings (dna / openriglogic) or export the neutral body mesh as an OBJ from Unreal Engine and load it with LoadUnrealBodyObj.";
            return false;
        }

        try
        {
            string? outDir = Path.GetDirectoryName(Path.GetFullPath(outputObjPath));
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            var psi = new ProcessStartInfo
            {
                FileName = python,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(ExtractionScript);
            psi.ArgumentList.Add(dnaPath);
            psi.ArgumentList.Add(outputObjPath);

            using var process = Process.Start(psi);
            if (process == null)
            {
                error = $"Failed to launch '{python}' for DNA mesh extraction.";
                return false;
            }

            string stderr = process.StandardError.ReadToEnd();
            string stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                error = "Timed out while extracting neutral mesh from DNA via Python.";
                return false;
            }

            if (process.ExitCode != 0 || !File.Exists(outputObjPath) || new FileInfo(outputObjPath).Length == 0)
            {
                string detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                error = string.IsNullOrEmpty(detail)
                    ? $"Python DNA extraction failed with exit code {process.ExitCode}."
                    : $"Python DNA extraction failed: {detail}";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Python DNA extraction failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Loads a MetaHuman/Unreal exported body OBJ (default centimetres, Z-up) as a Seamstress avatar (metres, Y-up,
    /// centered on X=0 and grounded on Y=0).
    /// </summary>
    public static Avatar LoadUnrealBodyObj(string objPath, float scaleToMetres = 0.01f, bool zUpToYUp = true)
    {
        if (string.IsNullOrWhiteSpace(objPath) || !File.Exists(objPath))
            throw new FileNotFoundException($"Unreal body OBJ not found: '{objPath}'.", objPath);

        var obj = ObjFile.Load(objPath);
        var mesh = BuildGroundedMesh(obj, scaleToMetres, zUpToYUp);
        return new Avatar
        {
            Name = Path.GetFileNameWithoutExtension(objPath),
            Mesh = mesh,
            Sdf = SdfGrid.FromMesh(mesh),
        };
    }

    internal static Vector3 ConvertPoint(Vector3 p, float scale, bool zUpToYUp)
        => zUpToYUp ? new Vector3(p.X, p.Z, -p.Y) * scale : p * scale;

    internal static TriMesh BuildGroundedMesh(ObjFile obj, float scale, bool zUpToYUp)
    {
        var source = obj.Triangulate(_ => true);
        var remap = new Dictionary<int, int>();
        var positions = new List<Vector3>();
        var triangles = new int[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            if (!remap.TryGetValue(source[i], out int index))
            {
                index = positions.Count;
                remap[source[i]] = index;
                positions.Add(ConvertPoint(obj.Positions[source[i]], scale, zUpToYUp));
            }
            triangles[i] = index;
        }
        var raw = new TriMesh { Positions = positions.ToArray(), Triangles = triangles };
        return GroundMesh(raw);
    }

    internal static TriMesh GroundMesh(TriMesh mesh)
    {
        if (mesh.Positions.Length == 0)
            return mesh;
        var (min, max) = mesh.Bounds();
        var offset = new Vector3(-(min.X + max.X) * 0.5f, -min.Y, 0f);
        var moved = new Vector3[mesh.Positions.Length];
        for (int i = 0; i < moved.Length; i++)
            moved[i] = mesh.Positions[i] + offset;
        return new TriMesh { Positions = moved, Triangles = mesh.Triangles };
    }

    private static List<string> ExtractPrintableTokens(ReadOnlySpan<byte> data, int maxTokens)
    {
        var tokens = new List<string>(maxTokens);
        var current = new StringBuilder();
        for (int i = 0; i < data.Length && tokens.Count < maxTokens; i++)
        {
            byte b = data[i];
            if (IsDescriptorChar(b))
            {
                current.Append((char)b);
            }
            else
            {
                FlushToken(current, tokens);
            }
        }
        if (tokens.Count < maxTokens)
            FlushToken(current, tokens);
        return tokens;
    }

    private static bool IsDescriptorChar(byte b)
        => (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b == (byte)'_' || b == (byte)'-' || b == (byte)'.' || b == (byte)' ';

    private static void FlushToken(StringBuilder current, List<string> tokens)
    {
        if (current.Length >= 4)
        {
            string trimmed = current.ToString().Trim();
            if (trimmed.Length >= 4 && trimmed.Any(char.IsLetter))
                tokens.Add(trimmed.Length > 48 ? trimmed[..48] : trimmed);
        }
        current.Clear();
    }

    private static string? DetectPythonWithDnaModule()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "python", "python3", "py" }
            : new[] { "python3", "python" };

        foreach (string cmd in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cmd,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("import importlib.util, sys; ok = importlib.util.find_spec('dna') or importlib.util.find_spec('openriglogic'); sys.exit(0 if ok else 1)");
                using var process = Process.Start(psi);
                if (process != null && process.WaitForExit(2500) && process.ExitCode == 0)
                    return cmd;
            }
            catch
            {
                // Executable not present on PATH; try next candidate.
            }
        }
        return null;
    }

    private const string ExtractionScript = """
import sys
dna_path, out_path = sys.argv[1], sys.argv[2]
try:
    import dna as rig_dna
except ImportError:
    import openriglogic as rig_dna

stream = rig_dna.FileStream(dna_path, rig_dna.FileStream.AccessMode_Read, rig_dna.FileStream.OpenMode_Binary)
reader = rig_dna.BinaryStreamReader(stream, rig_dna.DataLayer_Geometry)
reader.read()
if not rig_dna.Status.isOk():
    raise RuntimeError(rig_dna.Status.get().message)

mesh_index = 0
xs = reader.getVertexPositionXs(mesh_index)
ys = reader.getVertexPositionYs(mesh_index)
zs = reader.getVertexPositionZs(mesh_index)
layout_pos = reader.getVertexLayoutPositionIndices(mesh_index)
face_count = reader.getFaceCount(mesh_index)

with open(out_path, 'w', encoding='utf-8') as f:
    f.write('# Extracted from MetaHuman DNA via RigLogic\n')
    for x, y, z in zip(xs, ys, zs):
        f.write(f'v {x:.6f} {y:.6f} {z:.6f}\n')
    for fi in range(face_count):
        layout_indices = reader.getFaceVertexLayoutIndices(mesh_index, fi)
        verts = [layout_pos[li] + 1 for li in layout_indices]
        if len(verts) >= 3:
            f.write('f ' + ' '.join(str(v) for v in verts) + '\n')
""";
}
