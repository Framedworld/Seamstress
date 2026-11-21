using System.Diagnostics;
using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;
using Seamstress.Cuda;

namespace Seamstress.Mcp;

/// <summary>
/// Headless session holding the active Seamstress workspace, avatar, CUDA acceleration,
/// fit diagnostics, and software 3-view preview rendering.
/// </summary>
public sealed class GarmentSession
{
    public Workspace Workspace { get; } = new();
    public BodyLibrary BodyLibrary { get; } = new();
    public Avatar Avatar { get; private set; }
    public bool CudaAvailable { get; }
    public string ComputeBackend => ClothAcceleration.RegisteredDeviceName ?? "CPU";

    public GarmentSession()
    {
        CudaAvailable = CudaClothBackend.Register();
        Avatar = Avatar.CreateMannequin();
    }

    public Document Doc => Workspace.Document;

    public void SetAvatar(Avatar avatar) => Avatar = avatar;

    public void LoadObjAvatar(string path, float scale = 1f)
    {
        Avatar = Avatar.FromObj(path, scale);
    }

    public void ResetMannequinAvatar()
    {
        Avatar = Avatar.CreateMannequin();
    }

    /// <summary>Computes anatomical cross-section measurements of the active avatar for pattern drafting.</summary>
    public object GetAvatarLandmarks()
    {
        var (min, max) = Avatar.Mesh.Bounds();
        float height = max.Y - min.Y;

        object MeasureSlice(string name, float relY)
        {
            float y = min.Y + height * relY;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            const float band = 0.025f;
            int hits = 0;
            foreach (var p in Avatar.Mesh.Positions)
            {
                if (MathF.Abs(p.Y - y) > band)
                    continue;
                // Exclude outstretched arms for torso slices (chest/waist/hips)
                if ((name is "chest" or "waist" or "hips") && MathF.Abs(p.X) > 0.22f)
                    continue;
                minX = MathF.Min(minX, p.X);
                maxX = MathF.Max(maxX, p.X);
                minZ = MathF.Min(minZ, p.Z);
                maxZ = MathF.Max(maxZ, p.Z);
                hits++;
            }
            if (hits == 0)
                return new { name, yMetres = MathF.Round(y, 3), widthCm = 0f, depthCm = 0f, estCircumferenceCm = 0f };
            float wCm = (maxX - minX) * 100f;
            float dCm = (maxZ - minZ) * 100f;
            // Ramanujan ellipse circumference approximation
            float a = wCm * 0.5f, b = dCm * 0.5f;
            float h = ((a - b) * (a - b)) / MathF.Max(1e-6f, (a + b) * (a + b));
            float circ = MathF.PI * (a + b) * (1f + 3f * h / (10f + MathF.Sqrt(4f - 3f * h)));
            return new
            {
                name,
                yMetres = MathF.Round(y, 3),
                centerZMetres = MathF.Round((minZ + maxZ) * 0.5f, 3),
                widthCm = MathF.Round(wCm, 1),
                depthCm = MathF.Round(dCm, 1),
                estCircumferenceCm = MathF.Round(circ, 1),
            };
        }

        return new
        {
            avatarName = Avatar.Name,
            heightMetres = MathF.Round(height, 3),
            boundsMin = new[] { MathF.Round(min.X, 3), MathF.Round(min.Y, 3), MathF.Round(min.Z, 3) },
            boundsMax = new[] { MathF.Round(max.X, 3), MathF.Round(max.Y, 3), MathF.Round(max.Z, 3) },
            slices = new[]
            {
                MeasureSlice("shoulders", 0.80f),
                MeasureSlice("chest", 0.72f),
                MeasureSlice("waist", 0.62f),
                MeasureSlice("hips", 0.52f),
                MeasureSlice("midThigh", 0.38f),
                MeasureSlice("knee", 0.28f),
                MeasureSlice("ankle", 0.06f),
            },
            placementTips = "Pattern units are centimetres (Y up, counter-clockwise outlines). 3D placement is metres (Y up, avatar faces +Z, torso center Z ~ 0.045m). Front pieces usually sit at Z=+0.22..0.25 with Rotation=[0,0,0]; Back pieces at Z=-0.18..-0.22 with Rotation=[0,180,0].",
        };
    }

    public object RunSimulation(string? garmentId, int frames, bool allGarments, bool useGpu, bool selfCollision, int substeps)
    {
        var targets = new List<Garment>();
        var obstacles = new List<Garment>();
        foreach (var g in Doc.Garments)
        {
            if (!g.Visible || g.Pieces.Count == 0)
                continue;
            bool sim = allGarments
                || (garmentId != null ? g.Id == garmentId : (Workspace.ActiveGarment?.Id == g.Id || g.SimulateWithOthers));
            if (sim) targets.Add(g);
            else obstacles.Add(g);
        }
        if (targets.Count == 0)
            throw new InvalidOperationException("No visible garments with pieces to simulate.");

        var simMeshes = targets.Select(Workspace.MeshFor).ToList();
        var obsMeshes = obstacles.Select(Workspace.MeshFor).ToList();

        var settings = Doc.Simulation.Clone();
        settings.Substeps = Math.Clamp(substeps, 1, 64);
        settings.SelfCollision = selfCollision;

        bool prevGpu = ClothAcceleration.Enabled;
        ClothAcceleration.Enabled = useGpu;
        try
        {
            using var solver = ClothSolver.Create(simMeshes, obsMeshes, Avatar.Sdf, settings);
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
                solver.Step();
            sw.Stop();

            // Write draped positions back into the document pieces
            foreach (var cp in solver.Pieces)
            {
                if (!cp.Simulated) continue;
                cp.Piece.Drape = solver.ExtractDrape(cp);
                cp.Piece.DrapeKey = cp.Mesh.Key;
            }

            return new
            {
                backend = solver.BackendName,
                frames,
                substeps = settings.Substeps,
                selfCollision = settings.SelfCollision,
                particles = solver.ParticleCount,
                constraints = solver.ConstraintCount,
                elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                msPerFrame = Math.Round(sw.Elapsed.TotalMilliseconds / Math.Max(1, frames), 2),
                seamGapMetres = MathF.Round(solver.SeamGap, 4),
                peakSpeedMetresPerSec = MathF.Round(solver.PeakSpeed, 3),
                settled = solver.PeakSpeed < 0.08f && solver.SeamGap < 0.015f,
                fit = InspectFit(garmentId),
            };
        }
        finally
        {
            ClothAcceleration.Enabled = prevGpu;
        }
    }

    public object InspectFit(string? garmentId = null)
    {
        var garments = garmentId != null
            ? Doc.Garments.Where(g => g.Id == garmentId).ToList()
            : Doc.Garments.Where(g => g.Visible).ToList();

        var seamReports = new List<object>();
        int totalVerts = 0, totalTris = 0, penetratedVerts = 0;
        float maxPenetrationMm = 0f;
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);

        foreach (var g in garments)
        {
            var mesh = Workspace.MeshFor(g);
            var posByPiece = new Dictionary<string, Vector3[]>();
            foreach (var pm in mesh.Pieces)
            {
                var piece = g.FindPiece(pm.PieceId)!;
                var pts = Mesher.CurrentPositions(piece, pm);
                posByPiece[piece.Id] = pts;
                totalVerts += pm.VertexCount;
                totalTris += pm.Triangles.Length / 3;
                foreach (var p in pts)
                {
                    bmin = Vector3.Min(bmin, p);
                    bmax = Vector3.Max(bmax, p);
                    float d = Avatar.Sdf.Sample(p, out _);
                    if (d < 0f)
                    {
                        penetratedVerts++;
                        maxPenetrationMm = MathF.Max(maxPenetrationMm, -d * 1000f);
                    }
                }
            }

            foreach (var s in g.Seams)
            {
                var pa = g.FindPiece(s.A.PieceId);
                var pb = g.FindPiece(s.B.PieceId);
                if (pa == null || pb == null) continue;
                float lenA = Outline.EdgeLength(pa, s.A.Edge);
                float lenB = Outline.EdgeLength(pb, s.B.Edge);
                float mismatchPct = MathF.Max(lenA, lenB) > 1e-4f
                    ? MathF.Abs(lenA - lenB) / MathF.Max(lenA, lenB) * 100f
                    : 0f;
                seamReports.Add(new
                {
                    seamId = s.Id,
                    pieceA = pa.Name,
                    edgeA = s.A.Edge,
                    lengthACm = MathF.Round(lenA, 2),
                    pieceB = pb.Name,
                    edgeB = s.B.Edge,
                    lengthBCm = MathF.Round(lenB, 2),
                    mismatchPercent = MathF.Round(mismatchPct, 1),
                    reversed = s.Reversed,
                });
            }
        }

        return new
        {
            garmentCount = garments.Count,
            vertexCount = totalVerts,
            triangleCount = totalTris,
            penetratedVertices = penetratedVerts,
            maxPenetrationMm = MathF.Round(maxPenetrationMm, 2),
            boundsMin = totalVerts > 0 ? new[] { MathF.Round(bmin.X, 3), MathF.Round(bmin.Y, 3), MathF.Round(bmin.Z, 3) } : null,
            boundsMax = totalVerts > 0 ? new[] { MathF.Round(bmax.X, 3), MathF.Round(bmax.Y, 3), MathF.Round(bmax.Z, 3) } : null,
            seams = seamReports,
        };
    }

    /// <summary>
    /// Renders a headless 3-view (Front, Side, Back) shaded software PNG of the avatar and all visible garments
    /// using <see cref="PngWriter"/> — no GPU/Qt window required.
    /// </summary>
    public void RenderDrapePreviewPng(string outputPath, int width = 960, int height = 480)
    {
        var rgba = new byte[width * height * 4];
        // Dark studio background
        for (int i = 0; i < width * height; i++)
        {
            rgba[4 * i] = 26;
            rgba[4 * i + 1] = 29;
            rgba[4 * i + 2] = 36;
            rgba[4 * i + 3] = 255;
        }
        var zbuf = new float[width * height];
        Array.Fill(zbuf, float.NegativeInfinity);

        var (amin, amax) = Avatar.Mesh.Bounds();
        var center = (amin + amax) * 0.5f;
        float spanY = MathF.Max(0.5f, amax.Y - amin.Y) * 1.15f;

        // Collect garment meshes + colors
        var garmentDraws = new List<(Vector3[] Pos, int[] Tris, (byte R, byte G, byte B) Color)>();
        foreach (var g in Doc.Garments.Where(g => g.Visible))
        {
            var gm = Workspace.MeshFor(g);
            foreach (var pm in gm.Pieces)
            {
                var piece = g.FindPiece(pm.PieceId)!;
                var mat = Doc.FindMaterial(piece.MaterialId);
                var col = ParseHex(mat?.Color ?? "#4F8FD9");
                garmentDraws.Add((Mesher.CurrentPositions(piece, pm), pm.Triangles, col));
            }
        }

        int panelW = width / 3;
        float scale = height / spanY;
        float[] angles = { 0f, MathF.PI * 0.5f, MathF.PI }; // Front (+Z), Side (+X), Back (-Z)

        for (int view = 0; view < 3; view++)
        {
            int xOff = view * panelW;
            float cos = MathF.Cos(angles[view]);
            float sin = MathF.Sin(angles[view]);

            Vector3 Project(Vector3 p)
            {
                var q = p - center;
                float rx = q.X * cos - q.Z * sin;
                float rz = q.X * sin + q.Z * cos;
                return new Vector3(
                    xOff + panelW * 0.5f + rx * scale,
                    height * 0.5f - q.Y * scale,
                    rz);
            }

            void DrawMesh(Vector3[] positions, int[] triangles, (byte R, byte G, byte B) baseColor, float zBias)
            {
                var lightDir = Vector3.Normalize(new Vector3(0.35f, 0.65f, 0.7f));
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    var p0 = Project(positions[triangles[t]]);
                    var p1 = Project(positions[triangles[t + 1]]);
                    var p2 = Project(positions[triangles[t + 2]]);
                    var n = Vector3.Cross(p1 - p0, p2 - p0);
                    float nl = n.Length();
                    if (nl < 1e-6f) continue;
                    n /= nl;
                    float ndl = MathF.Abs(Vector3.Dot(n, lightDir));
                    float shade = 0.30f + 0.70f * ndl;
                    byte r = (byte)Math.Clamp(baseColor.R * shade, 0, 255);
                    byte g = (byte)Math.Clamp(baseColor.G * shade, 0, 255);
                    byte b = (byte)Math.Clamp(baseColor.B * shade, 0, 255);

                    int minX = Math.Max(xOff, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
                    int maxX = Math.Min(xOff + panelW - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
                    int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
                    int maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));

                    float denom = (p1.Y - p2.Y) * (p0.X - p2.X) + (p2.X - p1.X) * (p0.Y - p2.Y);
                    if (MathF.Abs(denom) < 1e-6f) continue;
                    float invDenom = 1f / denom;

                    for (int py = minY; py <= maxY; py++)
                    {
                        for (int px = minX; px <= maxX; px++)
                        {
                            float w0 = ((p1.Y - p2.Y) * (px - p2.X) + (p2.X - p1.X) * (py - p2.Y)) * invDenom;
                            float w1 = ((p2.Y - p0.Y) * (px - p2.X) + (p0.X - p2.X) * (py - p2.Y)) * invDenom;
                            float w2 = 1f - w0 - w1;
                            if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                            float z = w0 * p0.Z + w1 * p1.Z + w2 * p2.Z + zBias;
                            int idx = py * width + px;
                            if (z > zbuf[idx])
                            {
                                zbuf[idx] = z;
                                rgba[4 * idx] = r;
                                rgba[4 * idx + 1] = g;
                                rgba[4 * idx + 2] = b;
                                rgba[4 * idx + 3] = 255;
                            }
                        }
                    }
                }
            }

            DrawMesh(Avatar.Mesh.Positions, Avatar.Mesh.Triangles, (145, 148, 158), 0f);
            foreach (var (pos, tris, col) in garmentDraws)
                DrawMesh(pos, tris, col, 0.002f);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        PngWriter.WriteRgba(outputPath, width, height, rgba);
    }

    private static (byte R, byte G, byte B) ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b))
            return (r, g, b);
        return (79, 143, 217);
    }
}
