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
/// The project a session works on when it is not its own: the Seamstress window, which runs the tools on the
/// project the user has open (see <see cref="LiveLink"/>).
/// </summary>
public interface ISessionHost
{
    Workspace Workspace { get; }

    /// <summary>The avatar the window shows; setting it makes the window show that one.</summary>
    Avatar? Avatar { get; set; }

    /// <summary>Opens a project in the window. Refuses (InvalidOperationException) while the window has unsaved changes.</summary>
    void Open(string path);

    /// <summary>Starts a new project in the window, with the same refusal.</summary>
    void New();
}

/// <summary>
/// Session holding the active Seamstress workspace, avatar, CUDA acceleration, fit diagnostics, and software 3-view
/// preview rendering: headless, or the open window's project through an <see cref="ISessionHost"/>.
/// </summary>
public sealed class GarmentSession
{
    private Workspace _workspace = new();
    private Avatar? _avatar;

    /// <summary>The window whose project this session works on, or null for a headless session of its own.</summary>
    public ISessionHost? Host { get; }

    public Workspace Workspace => Host?.Workspace ?? _workspace;
    public BodyLibrary BodyLibrary { get; } = new();

    public Avatar? ActiveAvatar
    {
        get => Host != null ? Host.Avatar : _avatar;
        private set
        {
            if (Host != null)
                Host.Avatar = value;
            else
                _avatar = value;
        }
    }

    public Avatar Avatar => ActiveAvatar ?? throw new InvalidOperationException("This project has no avatar.");
    public bool CudaAvailable { get; }
    public string ComputeBackend => ClothAcceleration.RegisteredDeviceName ?? "CPU";

    public GarmentSession()
    {
        CudaAvailable = CudaClothBackend.Register();
        ResetMannequinAvatar();
    }

    /// <summary>A session on the open window's project.</summary>
    public GarmentSession(ISessionHost host)
    {
        Host = host;
        CudaAvailable = CudaClothBackend.Register();
    }

    public Document Doc => Workspace.Document;

    public void SetAvatar(Avatar avatar) => ActiveAvatar = avatar;

    public void LoadObjAvatar(string path, float scale = 1f) => LoadAvatar(path, scale, null);

    /// <summary>Loads an OBJ, FBX or glTF body; returns which of the file's meshes were used and which were left out.</summary>
    public (List<string> Used, List<string> Skipped) LoadAvatar(string path, float scale, HashSet<string>? meshes)
    {
        var used = new List<string>();
        var skipped = new List<string>();
        Func<string, bool>? include = meshes == null ? null : meshes.Contains;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".obj")
        {
            var file = ext == ".fbx" ? FbxMeshReader.Load(path, include) : GltfMeshReader.Load(path, include);
            used.AddRange(file.Parts.Select(p => $"{p.Name} ({p.Positions.Length} vertices)"));
            skipped.AddRange(file.Skipped);
        }
        var avatar = Avatar.FromFile(path, scale, include);
        // The project's avatar settings first, so a window taking the avatar knows it is the one they describe.
        Workspace.SetAvatar(AvatarKind.CustomObj, path, scale);
        ActiveAvatar = avatar;
        return (used, skipped);
    }

    public void ResetMannequinAvatar()
    {
        ActiveAvatar = Avatar.CreateMannequin();
    }

    /// <summary>Starts an empty project with the mannequin.</summary>
    public void NewProject()
    {
        if (Host != null)
        {
            Host.New();
            return;
        }
        Workspace.New();
        ResetMannequinAvatar();
    }

    public void OpenProject(string path)
    {
        if (Host != null)
        {
            Host.Open(path);
            return;
        }
        var next = new Workspace();
        next.Open(path);
        var settings = next.Document.Avatar;
        var avatar = settings.Kind switch
        {
            AvatarKind.None => null,
            AvatarKind.Mannequin => Avatar.CreateMannequin(),
            AvatarKind.MakeHuman => Avatar.FromMakeHumanBase(Seamstress.Core.AppSettings.Load().MakeHumanBaseMesh, settings),
            _ => Avatar.FromFile(settings.Path ?? throw new InvalidDataException("The saved project is missing its avatar path."), settings.Scale),
        };
        _workspace = next;
        ActiveAvatar = avatar;
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
            tape = TapeMeasurements(),
            placementTips = "Pattern units are centimetres (Y up, counter-clockwise outlines). 3D placement is metres (Y up, avatar faces +Z, torso center Z ~ 0.045m). Front pieces usually sit at Z=+0.22..0.25 with Rotation=[0,0,0]; Back pieces at Z=-0.18..-0.22 with Rotation=[0,180,0].",
        };
    }

    /// <summary>Per-limb tape measurements, arm axes and the shoulder ridge of the active avatar.</summary>
    public object TapeMeasurements()
    {
        var m = BodyMeasurements.Measure(Avatar.Mesh);
        static float R(float v) => MathF.Round(v, 1);
        static float[] V(Vector3 v) => new[] { MathF.Round(v.X, 3), MathF.Round(v.Y, 3), MathF.Round(v.Z, 3) };
        object Section(TapeSection s) => new
        {
            name = s.Name,
            circumferenceCm = R(s.CircumferenceCm),
            widthCm = R(s.WidthCm),
            depthCm = R(s.DepthCm),
            centreMetres = V(s.Centre),
        };
        return new
        {
            note = "Circumferences are tape measurements round the convex outline, one limb at a time. Left is the avatar's left (+X).",
            heightCm = R(m.HeightCm),
            crotchHeightCm = R(m.CrotchHeightCm),
            armpitHeightCm = R(m.ArmpitHeightCm),
            neckHeightCm = R(m.NeckHeightCm),
            shoulderWidthCm = R(m.ShoulderWidthCm),
            torso = m.Torso.Select(Section),
            leftLeg = m.LeftLeg.Select(Section),
            rightLeg = m.RightLeg.Select(Section),
            arms = m.Arms.Select(a => new
            {
                side = a.Side,
                shoulderMetres = V(a.Shoulder),
                wristMetres = V(a.Wrist),
                axis = V(a.Axis),
                droopDegrees = R(a.DroopDegrees),
                lengthCm = R(a.LengthCm),
                sections = a.Sections.Select(Section),
            }),
            shoulderRidge = m.ShoulderRidge.Select(p => new { xCm = R(p.XCm), topYCm = R(p.TopYCm), zCm = R(p.ZCm) }),
            shoulderRidgeNote = "A seam over the shoulder must be drafted above topYCm at each x, plus the garment's thickness and body offset.",
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
            bool sim = !g.Frozen && (allGarments
                || (garmentId != null ? g.Id == garmentId : (Workspace.ActiveGarment?.Id == g.Id || g.SimulateWithOthers)));
            if (sim) targets.Add(g);
            else obstacles.Add(g);
        }
        if (targets.Count == 0)
            throw new InvalidOperationException("No visible garments with pieces to simulate.");

        Attachments.Prepare(Doc, Workspace.MeshFor);
        var simMeshes = targets.Select(Workspace.MeshFor).ToList();
        var obsMeshes = obstacles.Select(Workspace.MeshFor).ToList();

        var settings = Doc.Simulation.Clone();
        settings.Substeps = Math.Clamp(substeps, 1, 64);
        settings.SelfCollision = selfCollision;

        bool prevGpu = ClothAcceleration.Enabled;
        ClothAcceleration.Enabled = useGpu;
        try
        {
            using var solver = ClothSolver.Create(simMeshes, obsMeshes, ActiveAvatar?.Sdf, settings);
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
                solver.Step();
            sw.Stop();

            // Write draped positions back into the document pieces; hard parts on them are carried along.
            var moved = new HashSet<string>();
            foreach (var cp in solver.Pieces)
            {
                if (!cp.Simulated) continue;
                cp.Piece.Drape = solver.ExtractDrape(cp);
                cp.Piece.DrapeKey = cp.Mesh.Key;
                moved.Add(cp.Piece.Id);
            }
            Attachments.Follow(Doc, Workspace.MeshFor, moved);

            return new
            {
                backend = solver.BackendName,
                frames,
                substeps = solver.EffectiveSubsteps,
                selfCollision = settings.SelfCollision,
                particles = solver.ParticleCount,
                constraints = solver.ConstraintCount,
                elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                msPerFrame = Math.Round(sw.Elapsed.TotalMilliseconds / Math.Max(1, frames), 2),
                seamGapMetres = MathF.Round(solver.SeamGap, 4),
                peakSpeedMetresPerSec = MathF.Round(solver.PeakSpeed, 3),
                settled = solver.PeakSpeed < 0.08f && solver.SeamGap < 0.015f,
                openSeams = targets.SelectMany(g => SeamReport.OpenSeams(Doc, Workspace.MeshFor, g)).Take(12).ToList(),
                advice = solver.SeamGap > 0.015f && solver.PeakSpeed > 0.3f
                    ? "The cloth was still moving and seams were still closing when the frames ran out: simulate again before judging the open seams."
                    : null,
                fit = InspectFit(garmentId, includeSeams: false),
            };
        }
        finally
        {
            ClothAcceleration.Enabled = prevGpu;
        }
    }

    public object InspectFit(string? garmentId = null, bool includeSeams = true)
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
                    float d = ActiveAvatar?.Sdf.Sample(p, out _) ?? 0f;
                    if (d < 0f)
                    {
                        penetratedVerts++;
                        maxPenetrationMm = MathF.Max(maxPenetrationMm, -d * 1000f);
                    }
                }
            }

            foreach (var s in includeSeams ? g.Seams : Enumerable.Empty<Seam>())
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
            openSeams = includeSeams ? garments.SelectMany(g => SeamReport.OpenSeams(Doc, Workspace.MeshFor, g)).ToList() : null,
            seams = includeSeams ? seamReports : null,
        };
    }

    /// <summary>
    /// Renders a headless 3-view (Front, Side, Back) shaded software PNG of the avatar and all visible garments
    /// using <see cref="PngWriter"/> — no GPU/Qt window required.
    /// </summary>
    public void RenderDrapePreviewPng(string outputPath, int width = 960, int height = 480,
        IReadOnlyList<PreviewView>? views = null, Vector3? focus = null, float? spanMetres = null, bool showBody = true)
    {
        views ??= new[] { PreviewRenderer.Named["front"], PreviewRenderer.Named["side"], PreviewRenderer.Named["back"] };
        PreviewRenderer.Render(Doc, Workspace.MeshFor, ActiveAvatar?.Mesh, outputPath, width, height, views, focus, spanMetres, showBody);
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
