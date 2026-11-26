using System.Globalization;
using System.Numerics;
using System.Text;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.App;

/// <summary>
/// Keeps the 3D viewport's geometry files up to date. Each visible piece gets a mesh file that is rewritten (under a
/// new name, so the renderer notices) whenever its shape or UVs change; the viewport is told which files to show.
/// </summary>
internal sealed class ViewportSync : IDisposable
{
    /// <summary>How opaque a deactivated garment is drawn: still there to see, but plainly out of the simulation.</summary>
    private const float DeactivatedOpacity = 0.35f;

    private sealed class Entry
    {
        public object? Drape;
        public string Key = "";
        public int Hash;
        public string Url = "";
        /// <summary>Bumped whenever the mesh is rebuilt: a "live:" key stays the same while its buffer changes.</summary>
        public int Revision;
        public readonly Queue<string> Files = new();
    }

    private readonly string _folder;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Queue<string> _seamFiles = new();
    private int _generation;
    private string _seamUrl = "";
    private Avatar? _avatar;
    private string _avatarUrl = "";

    public string CheckerUrl { get; }

    /// <summary>Handle meshes for the move/rotate gizmo: a unit arrow along +Y and a unit ring around the Y axis.</summary>
    public string ArrowUrl { get; }
    public string RingUrl { get; }

    /// <summary>When set, the outlines of all faces are written as a line mesh for the topology overlay.</summary>
    public bool ShowWire { get; set; }

    private readonly Queue<string> _wireFiles = new();
    private string _wireUrl = "";
    private readonly Queue<string> _pinFiles = new();
    private string _pinUrl = "";
    private int _pinHash;
    private readonly Queue<string> _pinSelectionFiles = new();
    private string _pinSelectionUrl = "";
    private int _pinSelectionHash;

    /// <summary>
    /// Where on its host piece each vertex of a surface-stitched edge is sewn: a triangle of the host's mesh and the
    /// weights of its corners. Finding them searches the host's triangles, so they are kept for as long as both
    /// meshes and the stitch stay the same; following the cloth while it is simulated then costs next to nothing.
    /// </summary>
    private sealed class StitchAnchors
    {
        public string SourceKey = "", HostKey = "";
        public int Edge;
        public Vector2 Start, End;
        public (int A, int B, int C, float Wa, float Wb, float Wc)[] Anchors = Array.Empty<(int, int, int, float, float, float)>();
    }

    private readonly Dictionary<string, StitchAnchors> _stitchAnchors = new();

    /// <summary>A garment's shape as last shown, kept while its mesh, drapes and arrangement stay the same.</summary>
    private sealed record GarmentShape(GarmentMesh Mesh, int Hash, object?[] Drapes, Vector3[][] Positions, Vector3[][] Normals,
        List<Vector3> SeamLines);

    private readonly Dictionary<string, GarmentShape> _shapes = new();

    // A texture's file and the maps next to it, looked up on disk at most every few seconds rather than every frame.
    private readonly Dictionary<string, (long Checked, (string, string, string, string) Urls)> _textures = new();

    private (string Texture, string Normal, string Rough, string Metal) TextureUrls(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return ("", "", "", "");
        long now = Environment.TickCount64;
        if (_textures.TryGetValue(path, out var known) && now - known.Checked < 3000)
            return known.Urls;
        var urls = (File.Exists(path) ? ToUrl(path) : "", SidecarUrl(path, "_normal.png"), SidecarUrl(path, "_rough.png"),
            SidecarUrl(path, "_metal.png"));
        _textures[path] = (now, urls);
        return urls;
    }
    private readonly Queue<string> _stitchFiles = new();
    private string _stitchUrl = "";

    public ViewportSync()
    {
        string root = OperatingSystem.IsLinux() && Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
        RemoveStaleFolders(root);
        _folder = Path.Combine(root, $"seamstress-{Environment.ProcessId}");
        Directory.CreateDirectory(_folder);
        string checker = Path.Combine(_folder, "checker.png");
        Seamstress.Core.IO.PngWriter.WriteChecker(checker);
        CheckerUrl = ToUrl(checker);
        ArrowUrl = WriteArrow(Path.Combine(_folder, "gizmo_arrow.mesh"));
        RingUrl = WriteRing(Path.Combine(_folder, "gizmo_ring.mesh"));
    }

    private static string WriteArrow(string file)
    {
        const int sides = 16;
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        void Ring(float y, float radius)
        {
            for (int i = 0; i < sides; i++)
            {
                float a = i * MathF.Tau / sides;
                positions.Add(new Vector3(MathF.Cos(a) * radius, y, MathF.Sin(a) * radius));
            }
        }
        void Band(int lower, int upper)
        {
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                triangles.AddRange(new[] { lower + i, upper + i, lower + j, lower + j, upper + i, upper + j });
            }
        }
        Ring(0.12f, 0.022f);   // shaft
        Ring(0.78f, 0.022f);
        Ring(0.78f, 0.07f);    // head
        Ring(1.0f, 0.001f);
        Band(0, sides);
        Band(sides, 2 * sides);
        Band(2 * sides, 3 * sides);
        var p = positions.ToArray();
        var t = triangles.ToArray();
        QtMesh.WriteTriangles(file, p, Mesher.ComputeNormals(p, t), null, t);
        return ToUrl(file);
    }

    private static string WriteRing(string file)
    {
        const int around = 64, tube = 8;
        const float radius = 0.72f, thickness = 0.022f;
        var positions = new Vector3[around * tube];
        var triangles = new List<int>();
        for (int i = 0; i < around; i++)
        {
            float a = i * MathF.Tau / around;
            var centre = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            for (int k = 0; k < tube; k++)
            {
                float b = k * MathF.Tau / tube;
                positions[i * tube + k] = centre * (radius + MathF.Cos(b) * thickness) + Vector3.UnitY * (MathF.Sin(b) * thickness);
                int i2 = (i + 1) % around, k2 = (k + 1) % tube;
                triangles.AddRange(new[] { i * tube + k, i * tube + k2, i2 * tube + k, i2 * tube + k, i * tube + k2, i2 * tube + k2 });
            }
        }
        var t = triangles.ToArray();
        QtMesh.WriteTriangles(file, positions, Mesher.ComputeNormals(positions, t), null, t);
        return ToUrl(file);
    }

    private static void RemoveStaleFolders(string root)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(root, "seamstress-*"))
            {
                string suffix = Path.GetFileName(dir)["seamstress-".Length..];
                if (!int.TryParse(suffix, out int pid))
                    continue;
                bool alive;
                try { alive = !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
                catch (ArgumentException) { alive = false; }
                if (!alive)
                    Directory.Delete(dir, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left-over viewport files from an earlier run are harmless.
        }
    }

    public static string ToUrl(string path) => new Uri(path).AbsoluteUri;

    private static string SidecarUrl(string? basePath, string suffix)
    {
        if (string.IsNullOrEmpty(basePath))
            return "";
        string? dir = Path.GetDirectoryName(basePath);
        string stem = Path.GetFileNameWithoutExtension(basePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem))
            return "";
        string candidate = Path.Combine(dir, stem + suffix);
        return File.Exists(candidate) ? ToUrl(candidate) : "";
    }

    private readonly LiveGeometry _live = new();

    /// <summary>The buffers handed to the 3D view in memory; QML attaches its geometry objects to them.</summary>
    public LiveGeometry Live => _live;

    /// <summary>
    /// Hands a triangle mesh to the 3D view: in memory where Qt allows it (a "live:" key), otherwise as a new file
    /// (its URL). Either way the result is what the QML model is given in place of a source.
    /// </summary>
    private string PutTriangles(string name, Queue<string> history, Vector3[] positions, Vector3[] normals, Vector2[]? uvs,
        int[] triangles, Vector4[]? colours = null)
    {
        if (_live.Available)
            return _live.SetTriangles(name, positions, normals, uvs, triangles, colours);
        string file = NextFile(name, history);
        QtMesh.WriteTriangles(file, positions, normals, uvs, triangles, colours);
        return ToUrl(file);
    }

    /// <summary>The same for line segments; no lines give "".</summary>
    private string PutLines(string name, Queue<string> history, List<Vector3> lines)
    {
        if (lines.Count == 0)
        {
            _live.Remove(name);
            return "";
        }
        if (_live.Available)
            return _live.SetLines(name, lines);
        string file = NextFile(name, history);
        QtMesh.WriteLines(file, lines.ToArray());
        return ToUrl(file);
    }

    private string NextFile(string prefix, Queue<string> history)
    {
        string file = Path.Combine(_folder, $"{prefix}_{++_generation}.mesh");
        history.Enqueue(file);
        // The renderer may still be reading the previous file, so only files a few generations old are removed.
        while (history.Count > 4)
        {
            try { File.Delete(history.Dequeue()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return file;
    }

    public string AvatarUrl(Avatar? avatar)
    {
        if (ReferenceEquals(avatar, _avatar))
            return _avatarUrl;
        _avatar = avatar;
        if (avatar == null)
            return _avatarUrl = "";
        string file = Path.Combine(_folder, $"avatar_{++_generation}.mesh");
        QtMesh.WriteTriangles(file, avatar.Mesh.Positions, Mesher.ComputeNormals(avatar.Mesh.Positions, avatar.Mesh.Triangles), null, avatar.Mesh.Triangles);
        return _avatarUrl = ToUrl(file);
    }

    /// <summary>The fit map the cloth is coloured by, if any (see <see cref="FitMapPainter"/>).</summary>
    public FitMapPainter FitMap { get; } = new();

    /// <summary>True while the Pin tool is on: every pin is then marked, also on pieces that are pinned in full.</summary>
    public bool PinToolActive { get; set; }

    /// <summary>Writes whatever changed and returns the list of models the viewport should display, as JSON.</summary>
    public string Sync(Workspace workspace, Avatar? avatar)
    {
        var doc = workspace.Document;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(2048);
        sb.Append("{\"models\":[");
        bool first = true, anyChanged = false;
        var seen = new HashSet<string>();
        var seamLines = new List<Vector3>();
        var wireLines = ShowWire ? new List<Vector3>() : null;
        var pins = new List<Vector3>();
        var pinSelection = new List<Vector3>();
        // Where the vertices of every shown piece are now, for the surface stitches drawn after the pieces.
        bool anyStitches = doc.Garments.Any(g => g.Visible && g.SurfaceStitches.Count > 0);
        var shownPositions = anyStitches ? new Dictionary<string, Vector3[]>() : null;
        FitMap.BeginUpdate();

        foreach (var garment in doc.Garments)
        {
            if (!garment.Visible || garment.Pieces.Count == 0)
                continue;
            var mesh = workspace.MeshFor(garment);
            var layout = UvLayout.For(garment);
            // A garment that has not moved since the last update (everything but what is being simulated) keeps the
            // positions, normals and seam threads worked out then: recomputing all of them every frame for a layered
            // outfit took most of the time of each update.
            var drapes = new object?[mesh.Pieces.Count];
            var shape = new HashCode();
            shape.Add(garment.ShownThicknessMm);
            for (int i = 0; i < mesh.Pieces.Count; i++)
            {
                var piece = garment.FindPiece(mesh.Pieces[i].PieceId)!;
                drapes[i] = piece.Drape;
                shape.Add(piece.DrapeKey);
                shape.Add(piece.Placement.Position);
                shape.Add(piece.Placement.Rotation);
                shape.Add(piece.Placement.Curvature);
                shape.Add(piece.FlipNormals);
                shape.Add(Workspace.IsEffectivelyVisible(garment, piece));
            }
            int shapeHash = shape.ToHashCode();
            if (_shapes.TryGetValue(garment.Id, out var known) && ReferenceEquals(known.Mesh, mesh) && known.Hash == shapeHash
                && known.Drapes.Length == drapes.Length && known.Drapes.Zip(drapes).All(d => ReferenceEquals(d.First, d.Second)))
            {
                seamLines.AddRange(known.SeamLines);
                if (shownPositions != null)
                {
                    for (int i = 0; i < mesh.Pieces.Count; i++)
                        shownPositions[mesh.Pieces[i].PieceId] = known.Positions[i];
                }
                PieceModels(known.Positions, known.Normals);
                continue;
            }
            var positions = new Vector3[mesh.Pieces.Count][];
            var normals = new Vector3[mesh.Pieces.Count][];
            var garmentSeams = new List<Vector3>();
            for (int i = 0; i < mesh.Pieces.Count; i++)
            {
                var piece = garment.FindPiece(mesh.Pieces[i].PieceId)!;
                positions[i] = Mesher.CurrentPositions(piece, mesh.Pieces[i]);
                normals[i] = Mesher.ComputeNormals(positions[i], mesh.Pieces[i].ShownTriangles(piece.FlipNormals));
                if (shownPositions != null)
                    shownPositions[piece.Id] = positions[i];
            }
            // Shade across closed seams as one surface; draw a thread for stitches that are still apart.
            foreach (var stitch in mesh.Stitches)
            {
                var a = positions[stitch.PieceA][stitch.VertexA];
                var b = positions[stitch.PieceB][stitch.VertexB];
                float distance = Vector3.Distance(a, b);
                if (distance < 0.004f)
                {
                    var na = normals[stitch.PieceA][stitch.VertexA];
                    var nb = normals[stitch.PieceB][stitch.VertexB];
                    if (Vector3.Dot(na, nb) > 0)
                    {
                        var sum = Vector3.Normalize(na + nb);
                        normals[stitch.PieceA][stitch.VertexA] = sum;
                        normals[stitch.PieceB][stitch.VertexB] = sum;
                    }
                }
                else
                {
                    var pa = garment.FindPiece(mesh.Pieces[stitch.PieceA].PieceId)!;
                    var pb = garment.FindPiece(mesh.Pieces[stitch.PieceB].PieceId)!;
                    if (Workspace.IsEffectivelyVisible(garment, pa) && Workspace.IsEffectivelyVisible(garment, pb))
                    {
                        var na = normals[stitch.PieceA][stitch.VertexA];
                        var nb = normals[stitch.PieceB][stitch.VertexB];
                        float ha = garment.ShownThicknessMm * 0.0005f + 0.001f;
                        float hb = garment.ShownThicknessMm * 0.0005f + 0.001f;
                        garmentSeams.Add(a + na * ha);
                        garmentSeams.Add(b + nb * hb);
                        garmentSeams.Add(a - na * ha);
                        garmentSeams.Add(b - nb * hb);
                    }
                }
            }
            seamLines.AddRange(garmentSeams);
            _shapes[garment.Id] = new GarmentShape(mesh, shapeHash, drapes, positions, normals, garmentSeams);
            PieceModels(positions, normals);

            void PieceModels(Vector3[][] positions, Vector3[][] normals)
            {
            for (int i = 0; i < mesh.Pieces.Count; i++)
            {
                var pieceMesh = mesh.Pieces[i];
                var piece = garment.FindPiece(pieceMesh.PieceId)!;
                if (!Workspace.IsEffectivelyVisible(garment, piece) || pieceMesh.TriangleCount == 0)
                    continue;
                seen.Add(piece.Id);
                FitMap.Shown(piece.Id);
                float halfThickness = garment.ShownThicknessMm * 0.0005f;
                // A piece pinned in full is a hard part (a snap, a pen, a held cuff): marking every one of its points
                // would bury it in markers, so it shows its pins only while the Pin tool is in use.
                if (piece.PinKey == pieceMesh.Key && (PinToolActive || piece.Pins.Count < pieceMesh.VertexCount))
                {
                    foreach (int index in piece.Pins)
                    {
                        if (index >= 0 && index < positions[i].Length)
                        {
                            pins.Add(positions[i][index] + normals[i][index] * (halfThickness + 0.0015f));
                            pins.Add(positions[i][index] - normals[i][index] * (halfThickness + 0.0015f));
                        }
                    }
                }
                if (workspace.PinSelectionKey(piece.Id) == pieceMesh.Key
                    && workspace.PinSelection.TryGetValue(piece.Id, out var selectedVertices))
                {
                    foreach (int index in selectedVertices)
                    {
                        if (index >= 0 && index < positions[i].Length)
                        {
                            pinSelection.Add(positions[i][index] + normals[i][index] * (halfThickness + 0.0018f));
                            pinSelection.Add(positions[i][index] - normals[i][index] * (halfThickness + 0.0018f));
                        }
                    }
                }
                if (wireLines != null)
                {
                    // Face outlines, lifted a hair off the surface on both sides so they are not hidden by it.
                    var drawn = new HashSet<long>(EdgeKey.Comparer);
                    foreach (var face in pieceMesh.Faces())
                    {
                        for (int k = 0; k < face.Length; k++)
                        {
                            int a = face[k], b = face[(k + 1) % face.Length];
                            if (!drawn.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a))
                                continue;
                            foreach (float side in new[] { 1f, -1f })
                            {
                                float lift = side * (halfThickness + 0.0008f);
                                wireLines.Add(positions[i][a] + normals[i][a] * lift);
                                wireLines.Add(positions[i][b] + normals[i][b] * lift);
                            }
                        }
                    }
                }
                if (!_entries.TryGetValue(piece.Id, out var entry))
                    _entries[piece.Id] = entry = new Entry();

                var material = doc.FindMaterial(piece.MaterialId) ?? Seamstress.Core.IO.ExportScene.DefaultMaterial;
                bool fitPieceUv = material.FitsTextureToPiece;
                var hash = new HashCode();
                hash.Add(piece.Placement.Position);
                hash.Add(piece.Placement.Rotation);
                hash.Add(piece.Placement.Curvature);
                hash.Add(piece.Origin);
                hash.Add(piece.Uv.Offset);
                hash.Add(piece.Uv.Scale);
                hash.Add(piece.Uv.Rotation);
                hash.Add(layout.UnitsPerCm);
                hash.Add(layout.Min);
                hash.Add(piece.FlipNormals);
                hash.Add(halfThickness);
                hash.Add(fitPieceUv);
                hash.Add(FitMap.Signature(garment, piece, avatar));
                // A tile size is built into the UVs (UvLayout.MapMaterial), so changing it, or adding or removing the
                // texture it needs, rewrites the mesh; without a tile size the repeat is applied by the material.
                hash.Add(!string.IsNullOrEmpty(material.Texture) && material.TileSizeCm > 0 ? material.TileSizeCm : 0f);
                bool usesDrape = piece.Drape != null && piece.DrapeKey == pieceMesh.Key;
                int signature = hash.ToHashCode();
                if (entry.Url.Length == 0 || entry.Key != pieceMesh.Key || entry.Hash != signature
                    || !ReferenceEquals(entry.Drape, usesDrape ? piece.Drape : null))
                {
                    Vector2[] uvs = layout.MapMaterial(piece, pieceMesh, material);
                    var shell = Mesher.BuildShell(positions[i], normals[i], uvs,
                        pieceMesh.ShownTriangles(piece.FlipNormals), halfThickness * 2f);
                    entry.Revision = ++_generation;
                    entry.Url = PutTriangles("p" + piece.Id, entry.Files, shell.Positions, shell.Normals, shell.Uvs,
                        shell.Triangles, FitMap.Colours(garment, piece, pieceMesh, positions[i], shell, avatar));
                    entry.Key = pieceMesh.Key;
                    entry.Hash = signature;
                    entry.Drape = usesDrape ? piece.Drape : null;
                    anyChanged = true;
                }

                if (!first)
                    sb.Append(',');
                first = false;
                sb.Append("{\"id\":\"").Append(piece.Id).Append("\",\"source\":\"").Append(entry.Url).Append('"');
                sb.Append(",\"rev\":").Append(entry.Revision);
                sb.Append(",\"color\":\"").Append(material.Color).Append('"');
                var (texture, normalMap, roughMap, metalMap) = TextureUrls(material.Texture);
                sb.Append(",\"texture\":").Append(System.Text.Json.JsonSerializer.Serialize(texture));
                sb.Append(",\"normalMap\":").Append(System.Text.Json.JsonSerializer.Serialize(normalMap));
                sb.Append(",\"roughMap\":").Append(System.Text.Json.JsonSerializer.Serialize(roughMap));
                sb.Append(",\"metalMap\":").Append(System.Text.Json.JsonSerializer.Serialize(metalMap));
                float shownRepeat = material.TextureScale;
                sb.Append(inv, $",\"repeat\":{shownRepeat:0.####},\"roughness\":{material.Roughness:0.###},\"metallic\":{material.Metallic:0.###},\"opacity\":{material.Opacity * (garment.Deactivated ? DeactivatedOpacity : 1f):0.###}");
                sb.Append(",\"selected\":").Append(workspace.SelectedPieces.Contains(piece.Id) ? "true" : "false");
                sb.Append('}');
            }
            }
        }
        foreach (var id in _shapes.Keys.Where(id => doc.FindGarment(id) == null).ToList())
            _shapes.Remove(id);

        foreach (var id in _entries.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            foreach (string file in _entries[id].Files)
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            _entries.Remove(id);
            _live.Remove("p" + id);
            FitMap.Remove(id);
            anyChanged = true;
        }

        if (anyChanged || (_seamUrl.Length == 0) != (seamLines.Count == 0))
            _seamUrl = PutLines("seams", _seamFiles, seamLines);

        var stitchLines = shownPositions == null ? new List<Vector3>() : SurfaceStitchThreads(workspace, shownPositions);
        if (anyChanged || (_stitchUrl.Length == 0) != (stitchLines.Count == 0))
            _stitchUrl = PutLines("stitches", _stitchFiles, stitchLines);

        if (wireLines == null || wireLines.Count == 0)
            _wireUrl = PutLines("wire", _wireFiles, new List<Vector3>());
        else if (anyChanged || _wireUrl.Length == 0)
            _wireUrl = PutLines("wire", _wireFiles, wireLines);

        WriteMarkers("pins", pins, _pinFiles, ref _pinHash, ref _pinUrl);
        WriteMarkers("pin_selection", pinSelection, _pinSelectionFiles, ref _pinSelectionHash, ref _pinSelectionUrl);

        sb.Append("],\"seams\":\"").Append(_seamUrl).Append('"');
        sb.Append(",\"stitches\":\"").Append(_stitchUrl).Append('"');
        sb.Append(",\"wire\":\"").Append(_wireUrl).Append('"');
        sb.Append(",\"pins\":\"").Append(_pinUrl).Append('"');
        sb.Append(",\"pinSelection\":\"").Append(_pinSelectionUrl).Append('"');
        if (workspace.SelectionCentre() is { } centre)
            sb.Append(inv, $",\"gizmo\":[{centre.X:0.#####},{centre.Y:0.#####},{centre.Z:0.#####}]");
        sb.Append(",\"avatar\":\"").Append(AvatarUrl(avatar)).Append('"');
        FitMap.AppendJson(sb);
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Threads for the surface stitches that have not been pulled together yet: from each vertex of the patch edge to
    /// the place on the host piece it is sewn to. A stitch that is closed (the patch lies on its host, a cloth's
    /// thickness away) draws nothing, like a closed seam.
    /// </summary>
    private List<Vector3> SurfaceStitchThreads(Workspace workspace, Dictionary<string, Vector3[]> shownPositions)
    {
        var doc = workspace.Document;
        var lines = new List<Vector3>();
        var seen = new HashSet<string>();
        foreach (var garment in doc.Garments)
        {
            if (!garment.Visible || garment.SurfaceStitches.Count == 0 || garment.Pieces.Count == 0)
                continue;
            var mesh = workspace.MeshFor(garment);
            foreach (var stitch in garment.SurfaceStitches)
            {
                var source = garment.FindPiece(stitch.SourcePieceId);
                // The host may be a piece of another garment (a pocket in its own garment, sewn onto trousers).
                var hostGarment = doc.GarmentOfPiece(stitch.TargetPieceId);
                var host = hostGarment?.FindPiece(stitch.TargetPieceId);
                if (source == null || host == null || hostGarment == null || !hostGarment.Visible
                    || !Workspace.IsEffectivelyVisible(garment, source) || !Workspace.IsEffectivelyVisible(hostGarment, host))
                    continue;
                var sourceMesh = mesh.Find(source.Id);
                var hostMesh = workspace.MeshFor(hostGarment).Find(host.Id);
                if (sourceMesh == null || hostMesh == null || hostMesh.TriangleCount == 0
                    || stitch.SourceEdge < 0 || stitch.SourceEdge >= sourceMesh.EdgeChains.Length
                    || !shownPositions.TryGetValue(source.Id, out var from) || !shownPositions.TryGetValue(host.Id, out var onto))
                    continue;
                int[] chain = sourceMesh.EdgeChains[stitch.SourceEdge];
                if (chain.Length == 0)
                    continue;
                seen.Add(stitch.Id);
                if (!_stitchAnchors.TryGetValue(stitch.Id, out var anchors) || anchors.SourceKey != sourceMesh.Key
                    || anchors.HostKey != hostMesh.Key || anchors.Edge != stitch.SourceEdge
                    || anchors.Start != stitch.TargetStart || anchors.End != stitch.TargetEnd || anchors.Anchors.Length != chain.Length)
                {
                    anchors = new StitchAnchors
                    {
                        SourceKey = sourceMesh.Key, HostKey = hostMesh.Key, Edge = stitch.SourceEdge,
                        Start = stitch.TargetStart, End = stitch.TargetEnd,
                        Anchors = new (int, int, int, float, float, float)[chain.Length],
                    };
                    for (int i = 0; i < chain.Length; i++)
                    {
                        // The same spacing the solver uses: the edge's vertices spread evenly along the line.
                        float t = chain.Length > 1 ? i / (float)(chain.Length - 1) : 0.5f;
                        anchors.Anchors[i] = Locate(hostMesh, Vector2.Lerp(stitch.TargetStart, stitch.TargetEnd, t));
                    }
                    _stitchAnchors[stitch.Id] = anchors;
                }
                // How far apart the two lie once sewn: half of each cloth's thickness and the gap the solver leaves.
                float sourceThickness = garment.EffectiveThicknessMm(source), hostThickness = hostGarment.EffectiveThicknessMm(host);
                float rest = MathF.Max(0.5f * (sourceThickness + hostThickness) + 1.8f,
                    (garment.BodyOffset + sourceThickness) - (hostGarment.BodyOffset + hostThickness)) * 0.001f;
                for (int i = 0; i < chain.Length; i++)
                {
                    var (a, b, c, wa, wb, wc) = anchors.Anchors[i];
                    if (chain[i] >= from.Length || a >= onto.Length || b >= onto.Length || c >= onto.Length)
                        continue;
                    var start = from[chain[i]];
                    var end = onto[a] * wa + onto[b] * wb + onto[c] * wc;
                    if (Vector3.Distance(start, end) > rest + 0.004f)
                    {
                        lines.Add(start);
                        lines.Add(end);
                    }
                }
            }
        }
        foreach (string id in _stitchAnchors.Keys.Where(id => !seen.Contains(id)).ToList())
            _stitchAnchors.Remove(id);
        return lines;
    }

    /// <summary>The triangle of a piece's mesh that holds a point of its pattern (or lies nearest to it), with the point's weights in it.</summary>
    private static (int A, int B, int C, float Wa, float Wb, float Wc) Locate(PieceMesh mesh, Vector2 point)
    {
        var best = (A: mesh.Triangles[0], B: mesh.Triangles[1], C: mesh.Triangles[2], Wa: 1f, Wb: 0f, Wc: 0f);
        float bestDistance = float.MaxValue;
        for (int t = 0; t + 2 < mesh.Triangles.Length; t += 3)
        {
            int ia = mesh.Triangles[t], ib = mesh.Triangles[t + 1], ic = mesh.Triangles[t + 2];
            Vector2 a = mesh.Points[ia], b = mesh.Points[ib], c = mesh.Points[ic];
            float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (MathF.Abs(area) < 1e-9f)
                continue;
            float wb = ((point.X - a.X) * (c.Y - a.Y) - (point.Y - a.Y) * (c.X - a.X)) / area;
            float wc = ((b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X)) / area;
            float wa = 1 - wb - wc;
            if (wa >= -1e-4f && wb >= -1e-4f && wc >= -1e-4f)
                return (ia, ib, ic, wa, wb, wc);
            // Outside this triangle: the nearest place in it is close enough to where clamped weights point.
            float ca = MathF.Max(wa, 0), cb = MathF.Max(wb, 0), cc = MathF.Max(wc, 0);
            float sum = ca + cb + cc;
            ca /= sum; cb /= sum; cc /= sum;
            float distance = Vector2.DistanceSquared(point, a * ca + b * cb + c * cc);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = (ia, ib, ic, ca, cb, cc);
            }
        }
        return best;
    }

    /// <summary>Small octahedra at the given points; the file is only rewritten when the points move.</summary>
    private void WriteMarkers(string prefix, List<Vector3> centres, Queue<string> history, ref int lastHash, ref string url)
    {
        int hash = 17;
        foreach (var centre in centres)
            hash = hash * 31 + centre.GetHashCode();
        if (centres.Count == 0)
        {
            url = "";
            lastHash = hash;
            _live.Remove(prefix);
            return;
        }
        if (hash == lastHash && url.Length != 0)
            return;

        const float radius = 0.0042f;
        var directions = new[]
        {
            Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitZ, -Vector3.UnitY,
        };
        var positions = new Vector3[centres.Count * 6];
        var triangles = new int[centres.Count * 24];
        for (int i = 0; i < centres.Count; i++)
        {
            int vertex = i * 6;
            for (int k = 0; k < 6; k++)
                positions[vertex + k] = centres[i] + directions[k] * radius;
            int triangle = i * 24;
            foreach (int corner in new[] { 0, 2, 1, 0, 4, 2, 0, 3, 4, 0, 1, 3, 5, 1, 2, 5, 2, 4, 5, 4, 3, 5, 3, 1 })
                triangles[triangle++] = vertex + corner;
        }
        url = PutTriangles(prefix, history, positions, Mesher.ComputeNormals(positions, triangles), null, triangles);
        lastHash = hash;
    }

    public void Dispose()
    {
        _live.Dispose();
        try { Directory.Delete(_folder, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
