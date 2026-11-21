using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;

namespace Seamstress.Mcp;

public sealed class McpToolRegistry
{
    private readonly GarmentSession _session;

    public McpToolRegistry(GarmentSession session) => _session = session;

    public JsonArray GetToolDefinitions() => new(
        Tool("new_project", "Start a fresh empty Seamstress project.", Props(("name", "string", "Optional project name."))),
        Tool("open_project", "Open an existing *.garment.json project file.", Props(("path", "string", "Path to .garment.json file.")), "path"),
        Tool("save_project", "Save the current project to a *.garment.json file (can be opened directly in the Seamstress desktop GUI).", Props(("path", "string", "Output .garment.json path.")), "path"),
        Tool("get_project_summary", "Inspect all garments, pattern pieces (with point coordinates and edge lengths), seams, materials, and compute backend.", Props()),
        Tool("get_avatar_landmarks", "Get exact anatomical measurements (height, shoulder/chest/waist/hip widths, depths, circumferences, Y heights) of the current avatar for drafting fitted patterns and 3D placement.", Props()),
        Tool("add_template_garment", "Add a built-in ready-made garment template (T-Shirt, Tank Top, Skirt, Dress) scaled to the avatar.", Props(("template", "string", "Template name: T-Shirt, Tank Top, Skirt, or Dress.")), "template"),
        Tool("create_garment", "Create a new empty garment layer.", Props(
            ("name", "string", "Garment name."),
            ("particleDistance", "number", "Target mesh edge length in cm (default 2.0; smaller = finer folds)."),
            ("topology", "string", "Triangles or Quads (default Triangles)."),
            ("thicknessMm", "number", "Optional garment-wide collision thickness in mm.")), "name"),
        Tool("add_piece", "Draft a custom 2D pattern piece with counter-clockwise polygon points (cm, Y up), optional quadratic Bezier curve control points, optional holes, 3D placement (metres), fabric preset, and color.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["garmentId"] = Prop("string", "Target garment ID (defaults to active garment)."),
                ["name"] = Prop("string", "Piece name (e.g. 'Front Bodice', 'Left Sleeve')."),
                ["points"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Counter-clockwise outline points in cm: [{x, y, cx?, cy?}] where (cx, cy) bends the edge leaving that point.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["x"] = Prop("number", "X in cm"),
                            ["y"] = Prop("number", "Y in cm"),
                            ["cx"] = Prop("number", "Optional Bezier control point X in cm"),
                            ["cy"] = Prop("number", "Optional Bezier control point Y in cm"),
                        },
                        ["required"] = new JsonArray("x", "y"),
                    },
                },
                ["originX"] = Prop("number", "2D canvas origin X in cm (default 0)."),
                ["originY"] = Prop("number", "2D canvas origin Y in cm (default 0)."),
                ["pos3d"] = NumArrayProp("3D placement [x, y, z] in metres."),
                ["rot3d"] = NumArrayProp("3D Euler rotation [rx, ry, rz] in degrees."),
                ["curvature"] = Prop("number", "3D cylindrical wrap arc in degrees (0 = flat, e.g. 150 for sleeves)."),
                ["fabricPreset"] = Prop("string", "Cotton, Silk, Jersey Knit, Denim, Leather, Wool, or Chiffon."),
                ["color"] = Prop("string", "Hex color like #4F8FD9."),
            },
            ["required"] = new JsonArray("name", "points"),
        }),
        Tool("add_rectangle_piece", "Draft a rectangular pattern piece (width x height in cm) with optional 3D placement.", Props(
            ("garmentId", "string", "Target garment ID (defaults to active garment)."),
            ("name", "string", "Piece name."),
            ("widthCm", "number", "Width in cm."),
            ("heightCm", "number", "Height in cm."),
            ("originX", "number", "2D origin X in cm."),
            ("originY", "number", "2D origin Y in cm."),
            ("fabricPreset", "string", "Fabric preset name."),
            ("color", "string", "Hex color like #4F8FD9.")), "name", "widthCm", "heightCm"),
        Tool("mirror_piece", "Create a symmetric left/right mirrored copy of an existing pattern piece (mirrors 2D outline keeping CCW winding, and mirrors 3D X position and Y/Z rotation).", Props(
            ("pieceId", "string", "Source piece ID."),
            ("newName", "string", "Name for the mirrored piece."),
            ("offset2dX", "number", "2D X shift in cm (default 60).")), "pieceId"),
        Tool("cut_piece", "Split a pattern piece into two pieces along a 2D cut line (x1,y1)->(x2,y2) in pattern-space cm.", Props(
            ("x1", "number", "Start X in cm."),
            ("y1", "number", "Start Y in cm."),
            ("x2", "number", "End X in cm."),
            ("y2", "number", "End Y in cm.")), "x1", "y1", "x2", "y2"),
        Tool("inset_band", "Cut a waistband, collar band, or cuff strip of widthCm off one or more edges of a piece.", Props(
            ("pieceId", "string", "Piece ID."),
            ("edgeIndex", "integer", "Edge index to inset."),
            ("widthCm", "number", "Band width in cm (default 3.0).")), "pieceId", "edgeIndex"),
        Tool("delete_piece", "Delete a pattern piece and its connected seams.", Props(("pieceId", "string", "Piece ID.")), "pieceId"),
        Tool("sew_edges", "Sew edgeA of pieceA to edgeB of pieceB. Returns edge lengths and length mismatch %.", Props(
            ("pieceAId", "string", "First piece ID."),
            ("edgeA", "integer", "0-based edge index on first piece."),
            ("pieceBId", "string", "Second piece ID."),
            ("edgeB", "integer", "0-based edge index on second piece."),
            ("reversed", "boolean", "If true, sews start-to-start instead of start-to-end (use to fix twisted seams).")), "pieceAId", "edgeA", "pieceBId", "edgeB"),
        Tool("reverse_seam", "Flip the direction of an existing seam to fix twisting.", Props(("seamId", "string", "Seam ID.")), "seamId"),
        Tool("delete_seam", "Delete a seam by ID.", Props(("seamId", "string", "Seam ID.")), "seamId"),
        Tool("arrange_piece_3d", "Set a piece's 3D placement around the avatar before simulation.", Props(
            ("pieceId", "string", "Piece ID."),
            ("x", "number", "3D X in metres."),
            ("y", "number", "3D Y in metres."),
            ("z", "number", "3D Z in metres."),
            ("rx", "number", "Rotation X in degrees (default 0)."),
            ("ry", "number", "Rotation Y in degrees (default 0)."),
            ("rz", "number", "Rotation Z in degrees (default 0)."),
            ("curvature", "number", "Cylindrical wrap in degrees (0 = flat).")), "pieceId", "x", "y", "z"),
        Tool("set_fabric", "Set fabric physics on a piece (or all pieces in a garment) using a preset or custom parameters.", Props(
            ("pieceId", "string", "Target piece ID (or omit and pass garmentId to update all pieces in the garment)."),
            ("garmentId", "string", "Target garment ID when updating the whole garment."),
            ("preset", "string", "Cotton, Silk, Jersey Knit, Denim, Leather, Wool, or Chiffon."),
            ("density", "number", "Weight in g/m^2."),
            ("stretch", "number", "0-100 stretch resistance."),
            ("shear", "number", "0-100 shear resistance."),
            ("bend", "number", "0-100 bending stiffness."),
            ("damping", "number", "0-100 damping."),
            ("friction", "number", "0-100 friction."),
            ("thickness", "number", "Collision thickness in mm."))),
        Tool("set_material", "Set the color and material name on a piece or garment.", Props(
            ("pieceId", "string", "Target piece ID (optional if garmentId given)."),
            ("garmentId", "string", "Target garment ID (optional if pieceId given)."),
            ("name", "string", "Material name."),
            ("color", "string", "Hex color #RRGGBB.")), "color"),
        Tool("simulate", "Run cloth simulation (CUDA GPU-accelerated on RTX 4090 when available) to sew and drape the garment on the avatar.", Props(
            ("garmentId", "string", "Optional garment ID (defaults to active garment)."),
            ("frames", "integer", "Number of 60Hz frames to simulate (default 90)."),
            ("allGarments", "boolean", "Simulate all visible garments together (default false)."),
            ("useGpu", "boolean", "Use CUDA GPU acceleration if available (default true)."),
            ("selfCollision", "boolean", "Enable cloth-to-cloth & hem collision (default true)."),
            ("substeps", "integer", "XPBD substeps per frame (default 12)."))),
        Tool("reset_drape", "Clear simulated drape and return pieces to their flat 3D arrangement.", Props(("garmentId", "string", "Optional garment ID (defaults to all garments)."))),
        Tool("inspect_fit", "Inspect seam length mismatches, avatar penetrations, vertex/triangle counts, and 3D bounding box.", Props(("garmentId", "string", "Optional garment ID."))),
        Tool("render_preview", "Render a 2D pattern SVG and/or a 3-view (Front/Side/Back) 3D shaded PNG preview of the draped garment.", Props(
            ("pngPath", "string", "Optional output path for 3-view 3D draped PNG preview."),
            ("svgPath", "string", "Optional output path for 1:1 cm 2D sewing pattern SVG."),
            ("width", "integer", "PNG width in pixels (default 960)."),
            ("height", "integer", "PNG height in pixels (default 480)."))),
        Tool("export_garment", "Export the garment to .glb, .gltf, .obj, .fbx, or .svg (2D sewing pattern).", Props(
            ("path", "string", "Output file path with extension (.glb, .gltf, .obj, .fbx, or .svg)."),
            ("weldSeams", "boolean", "Weld closed seams on 3D export (default true).")), "path")
    );

    public object Dispatch(string name, JsonElement args)
    {
        return name switch
        {
            "new_project" => NewProject(args),
            "open_project" => OpenProject(args),
            "save_project" => SaveProject(args),
            "get_project_summary" => GetProjectSummary(),
            "get_avatar_landmarks" => _session.GetAvatarLandmarks(),
            "add_template_garment" => AddTemplateGarment(args),
            "create_garment" => CreateGarment(args),
            "add_piece" => AddPiece(args),
            "add_rectangle_piece" => AddRectanglePiece(args),
            "mirror_piece" => MirrorPiece(args),
            "cut_piece" => CutPiece(args),
            "inset_band" => InsetBand(args),
            "delete_piece" => DeletePiece(args),
            "sew_edges" => SewEdges(args),
            "reverse_seam" => ReverseSeam(args),
            "delete_seam" => DeleteSeam(args),
            "arrange_piece_3d" => ArrangePiece3D(args),
            "set_fabric" => SetFabric(args),
            "set_material" => SetMaterial(args),
            "simulate" => Simulate(args),
            "reset_drape" => ResetDrape(args),
            "inspect_fit" => _session.InspectFit(OptString(args, "garmentId")),
            "render_preview" => RenderPreview(args),
            "export_garment" => ExportGarment(args),
            _ => throw new ArgumentException($"Unknown tool '{name}'."),
        };
    }

    private object NewProject(JsonElement args)
    {
        _session.Workspace.New();
        if (OptString(args, "name") is { Length: > 0 } n)
            _session.Doc.Name = n;
        return new { ok = true, name = _session.Doc.Name, computeBackend = _session.ComputeBackend };
    }

    private object OpenProject(JsonElement args)
    {
        string path = ReqString(args, "path");
        _session.Workspace.Open(path);
        return GetProjectSummary();
    }

    private object SaveProject(JsonElement args)
    {
        string path = ReqString(args, "path");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _session.Workspace.Save(path);
        return new { ok = true, savedTo = Path.GetFullPath(path) };
    }

    private object GetProjectSummary()
    {
        var doc = _session.Doc;
        return new
        {
            name = doc.Name,
            filePath = _session.Workspace.FilePath,
            computeBackend = _session.ComputeBackend,
            cudaAvailable = _session.CudaAvailable,
            avatar = _session.Avatar.Name,
            garments = doc.Garments.Select(g => new
            {
                id = g.Id,
                name = g.Name,
                visible = g.Visible,
                particleDistance = g.ParticleDistance,
                topology = g.Topology.ToString(),
                thicknessMm = g.Thickness,
                pieces = g.Pieces.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    origin = new[] { MathF.Round(p.Origin.X, 2), MathF.Round(p.Origin.Y, 2) },
                    placement = new
                    {
                        pos = new[] { MathF.Round(p.Placement.Position.X, 3), MathF.Round(p.Placement.Position.Y, 3), MathF.Round(p.Placement.Position.Z, 3) },
                        rot = new[] { MathF.Round(p.Placement.Rotation.X, 1), MathF.Round(p.Placement.Rotation.Y, 1), MathF.Round(p.Placement.Rotation.Z, 1) },
                        curvature = p.Placement.Curvature,
                    },
                    fabric = p.Physics.Preset,
                    hasDrape = p.Drape != null,
                    edges = Enumerable.Range(0, p.EdgeCount).Select(e => new
                    {
                        edge = e,
                        lengthCm = MathF.Round(Outline.EdgeLength(p, e), 2),
                        curved = p.PointAt(e).Curve != null,
                    }),
                }),
                seams = g.Seams.Select(s => new
                {
                    id = s.Id,
                    pieceAId = s.A.PieceId,
                    edgeA = s.A.Edge,
                    pieceBId = s.B.PieceId,
                    edgeB = s.B.Edge,
                    reversed = s.Reversed,
                }),
            }),
        };
    }

    private object AddTemplateGarment(JsonElement args)
    {
        string template = ReqString(args, "template");
        var (min, max) = _session.Avatar.Mesh.Bounds();
        var g = Templates.Create(template, _session.Doc, max.Y - min.Y);
        _session.Doc.Garments.Add(g);
        _session.Workspace.SetActiveGarment(g.Id);
        return new
        {
            garmentId = g.Id,
            name = g.Name,
            pieces = g.Pieces.Select(p => new { id = p.Id, name = p.Name, edges = p.EdgeCount }),
            seamCount = g.Seams.Count,
        };
    }

    private object CreateGarment(JsonElement args)
    {
        string name = ReqString(args, "name");
        var g = new Garment
        {
            Name = _session.Doc.UniqueName(name),
            ParticleDistance = OptFloat(args, "particleDistance", 2.0f),
            Topology = string.Equals(OptString(args, "topology"), "Quads", StringComparison.OrdinalIgnoreCase)
                ? MeshTopology.Quads : MeshTopology.Triangles,
            Thickness = TryOptFloat(args, "thicknessMm"),
        };
        _session.Doc.Garments.Add(g);
        _session.Workspace.SetActiveGarment(g.Id);
        return new { garmentId = g.Id, name = g.Name };
    }

    private Garment ResolveGarment(string? garmentId)
    {
        if (garmentId != null)
            return _session.Doc.FindGarment(garmentId) ?? throw new ArgumentException($"Garment '{garmentId}' not found.");
        if (_session.Workspace.ActiveGarment is { } active)
            return active;
        var g = new Garment { Name = _session.Doc.UniqueName("Garment") };
        _session.Doc.Garments.Add(g);
        _session.Workspace.SetActiveGarment(g.Id);
        return g;
    }

    private object AddPiece(JsonElement args)
    {
        var g = ResolveGarment(OptString(args, "garmentId"));
        string name = ReqString(args, "name");
        if (!args.TryGetProperty("points", out var ptsEl) || ptsEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("points must be an array of {x, y, cx?, cy?}.");

        var points = new List<PatternPoint>();
        foreach (var pt in ptsEl.EnumerateArray())
        {
            float x = pt.GetProperty("x").GetSingle();
            float y = pt.GetProperty("y").GetSingle();
            var p = new PatternPoint(x, y);
            if (pt.TryGetProperty("cx", out var cxEl) && pt.TryGetProperty("cy", out var cyEl)
                && cxEl.ValueKind == JsonValueKind.Number && cyEl.ValueKind == JsonValueKind.Number)
                p.Curve = new Vector2(cxEl.GetSingle(), cyEl.GetSingle());
            points.Add(p);
        }
        if (points.Count < 3)
            throw new ArgumentException("A pattern piece needs at least 3 points.");

        // Ensure counter-clockwise winding
        EnsureCcw(points);

        var piece = new PatternPiece
        {
            Name = _session.Doc.UniqueName(name),
            Origin = new Vector2(OptFloat(args, "originX", 0f), OptFloat(args, "originY", 0f)),
            Points = points,
            Physics = OptString(args, "fabricPreset") is { Length: > 0 } preset
                ? FabricPhysics.FromPreset(preset)
                : new FabricPhysics(),
        };

        if (TryFloat3(args, "pos3d", out var pos))
            piece.Placement.Position = pos;
        if (TryFloat3(args, "rot3d", out var rot))
            piece.Placement.Rotation = rot;
        if (TryOptFloat(args, "curvature") is { } curv)
            piece.Placement.Curvature = curv;
        if (OptString(args, "color") is { Length: > 0 } hex)
        {
            var mat = new FabricMaterial { Name = _session.Doc.UniqueName($"{name} fabric"), Color = hex };
            _session.Doc.Materials.Add(mat);
            piece.MaterialId = mat.Id;
        }

        g.Pieces.Add(piece);
        return new
        {
            garmentId = g.Id,
            pieceId = piece.Id,
            name = piece.Name,
            edges = Enumerable.Range(0, piece.EdgeCount).Select(e => new { edge = e, lengthCm = MathF.Round(Outline.EdgeLength(piece, e), 2) }),
        };
    }

    private object AddRectanglePiece(JsonElement args)
    {
        var g = ResolveGarment(OptString(args, "garmentId"));
        string name = ReqString(args, "name");
        float w = ReqFloat(args, "widthCm");
        float h = ReqFloat(args, "heightCm");
        var piece = new PatternPiece
        {
            Name = _session.Doc.UniqueName(name),
            Origin = new Vector2(OptFloat(args, "originX", 0f), OptFloat(args, "originY", 0f)),
            Points = new List<PatternPoint>
            {
                new(0, 0), new(w, 0), new(w, h), new(0, h)
            },
            Physics = OptString(args, "fabricPreset") is { Length: > 0 } preset
                ? FabricPhysics.FromPreset(preset)
                : new FabricPhysics(),
        };
        if (OptString(args, "color") is { Length: > 0 } hex)
        {
            var mat = new FabricMaterial { Name = _session.Doc.UniqueName($"{name} fabric"), Color = hex };
            _session.Doc.Materials.Add(mat);
            piece.MaterialId = mat.Id;
        }
        g.Pieces.Add(piece);
        return new { garmentId = g.Id, pieceId = piece.Id, name = piece.Name, edges = new[] { "0:bottom", "1:right", "2:top", "3:left" } };
    }

    private object MirrorPiece(JsonElement args)
    {
        string pieceId = ReqString(args, "pieceId");
        var piece = _session.Doc.FindPiece(pieceId) ?? throw new ArgumentException($"Piece '{pieceId}' not found.");
        var garment = _session.Doc.GarmentOfPiece(pieceId)!;
        var (bmin, bmax) = Outline.Bounds(piece);
        float pivotX = (bmin.X + bmax.X) * 0.5f;

        // Mirror X and reverse order to maintain counter-clockwise winding + remap Bezier curves onto the reversed edges.
        int n = piece.Points.Count;
        var mirrored = new List<PatternPoint>(n);
        for (int i = 0; i < n; i++)
        {
            int src = (n - i) % n;
            int prev = (src - 1 + n) % n;
            var pt = piece.Points[src];
            var mp = new PatternPoint(2 * pivotX - pt.Position.X, pt.Position.Y);
            if (piece.Points[prev].Curve is { } c)
                mp.Curve = new Vector2(2 * pivotX - c.X, c.Y);
            mirrored.Add(mp);
        }

        var copy = piece.Clone();
        copy.Id = Document.NewId();
        copy.Name = _session.Doc.UniqueName(OptString(args, "newName") ?? $"{piece.Name} Mirror");
        copy.Points = mirrored;
        copy.Origin = new Vector2(piece.Origin.X + OptFloat(args, "offset2dX", 60f), piece.Origin.Y);
        copy.Placement.Position = new Vector3(-piece.Placement.Position.X, piece.Placement.Position.Y, piece.Placement.Position.Z);
        copy.Placement.Rotation = new Vector3(piece.Placement.Rotation.X, -piece.Placement.Rotation.Y, -piece.Placement.Rotation.Z);
        copy.Drape = null;
        copy.DrapeKey = null;
        garment.Pieces.Add(copy);

        return new { garmentId = garment.Id, pieceId = copy.Id, name = copy.Name, edgeCount = copy.EdgeCount };
    }

    private object CutPiece(JsonElement args)
    {
        var a = new Vector2(ReqFloat(args, "x1"), ReqFloat(args, "y1"));
        var b = new Vector2(ReqFloat(args, "x2"), ReqFloat(args, "y2"));
        var outcome = _session.Workspace.CutPieces(a, b);
        return new
        {
            sourcePieces = outcome.SourcePieces,
            problem = outcome.Problem,
            createdPieces = outcome.Pieces.Select(p => new { id = p.Id, name = p.Name }),
        };
    }

    private object InsetBand(JsonElement args)
    {
        string pieceId = ReqString(args, "pieceId");
        int edge = ReqInt(args, "edgeIndex");
        float width = OptFloat(args, "widthCm", 3f);
        var created = _session.Workspace.InsetBand(new[] { new EdgeRef(pieceId, edge) }, width);
        return new { createdCount = created.Count, pieces = created.Select(p => new { id = p.Id, name = p.Name }) };
    }

    private object DeletePiece(JsonElement args)
    {
        string pieceId = ReqString(args, "pieceId");
        var g = _session.Doc.GarmentOfPiece(pieceId) ?? throw new ArgumentException($"Piece '{pieceId}' not found.");
        g.Pieces.RemoveAll(p => p.Id == pieceId);
        g.Seams.RemoveAll(s => s.A.PieceId == pieceId || s.B.PieceId == pieceId);
        return new { ok = true, deletedPieceId = pieceId };
    }

    private object SewEdges(JsonElement args)
    {
        string pA = ReqString(args, "pieceAId");
        int eA = ReqInt(args, "edgeA");
        string pB = ReqString(args, "pieceBId");
        int eB = ReqInt(args, "edgeB");
        bool reversed = OptBool(args, "reversed", false);

        var pieceA = _session.Doc.FindPiece(pA) ?? throw new ArgumentException($"Piece '{pA}' not found.");
        var pieceB = _session.Doc.FindPiece(pB) ?? throw new ArgumentException($"Piece '{pB}' not found.");
        var gA = _session.Doc.GarmentOfPiece(pA)!;
        var gB = _session.Doc.GarmentOfPiece(pB)!;
        if (gA.Id != gB.Id)
            throw new ArgumentException("Both pieces must belong to the same garment to be sewn together.");
        if (eA < 0 || eA >= pieceA.EdgeCount)
            throw new ArgumentException($"edgeA {eA} out of range (0..{pieceA.EdgeCount - 1}).");
        if (eB < 0 || eB >= pieceB.EdgeCount)
            throw new ArgumentException($"edgeB {eB} out of range (0..{pieceB.EdgeCount - 1}).");

        var seam = new Seam { A = new EdgeRef(pA, eA), B = new EdgeRef(pB, eB), Reversed = reversed };
        gA.Seams.Add(seam);

        float lenA = Outline.EdgeLength(pieceA, eA);
        float lenB = Outline.EdgeLength(pieceB, eB);
        float mismatchPct = MathF.Max(lenA, lenB) > 1e-4f
            ? MathF.Abs(lenA - lenB) / MathF.Max(lenA, lenB) * 100f
            : 0f;

        return new
        {
            seamId = seam.Id,
            pieceA = pieceA.Name,
            edgeA = eA,
            lengthACm = MathF.Round(lenA, 2),
            pieceB = pieceB.Name,
            edgeB = eB,
            lengthBCm = MathF.Round(lenB, 2),
            mismatchPercent = MathF.Round(mismatchPct, 1),
            reversed,
        };
    }

    private object ReverseSeam(JsonElement args)
    {
        string seamId = ReqString(args, "seamId");
        foreach (var g in _session.Doc.Garments)
        {
            var s = g.Seams.FirstOrDefault(x => x.Id == seamId);
            if (s != null)
            {
                s.Reversed = !s.Reversed;
                return new { ok = true, seamId, reversed = s.Reversed };
            }
        }
        throw new ArgumentException($"Seam '{seamId}' not found.");
    }

    private object DeleteSeam(JsonElement args)
    {
        string seamId = ReqString(args, "seamId");
        foreach (var g in _session.Doc.Garments)
            if (g.Seams.RemoveAll(x => x.Id == seamId) > 0)
                return new { ok = true, deletedSeamId = seamId };
        throw new ArgumentException($"Seam '{seamId}' not found.");
    }

    private object ArrangePiece3D(JsonElement args)
    {
        string pieceId = ReqString(args, "pieceId");
        var piece = _session.Doc.FindPiece(pieceId) ?? throw new ArgumentException($"Piece '{pieceId}' not found.");
        piece.Placement.Position = new Vector3(ReqFloat(args, "x"), ReqFloat(args, "y"), ReqFloat(args, "z"));
        piece.Placement.Rotation = new Vector3(OptFloat(args, "rx", 0f), OptFloat(args, "ry", 0f), OptFloat(args, "rz", 0f));
        piece.Placement.Curvature = OptFloat(args, "curvature", 0f);
        piece.Drape = null;
        piece.DrapeKey = null;
        return new { ok = true, pieceId, name = piece.Name };
    }

    private object SetFabric(JsonElement args)
    {
        var targets = new List<PatternPiece>();
        if (OptString(args, "pieceId") is { Length: > 0 } pid)
            targets.Add(_session.Doc.FindPiece(pid) ?? throw new ArgumentException($"Piece '{pid}' not found."));
        else
            targets.AddRange(ResolveGarment(OptString(args, "garmentId")).Pieces);

        foreach (var p in targets)
        {
            if (OptString(args, "preset") is { Length: > 0 } preset)
                p.Physics = FabricPhysics.FromPreset(preset);
            if (TryOptFloat(args, "density") is { } d) p.Physics.Density = d;
            if (TryOptFloat(args, "stretch") is { } st) p.Physics.Stretch = st;
            if (TryOptFloat(args, "shear") is { } sh) p.Physics.Shear = sh;
            if (TryOptFloat(args, "bend") is { } b) p.Physics.Bend = b;
            if (TryOptFloat(args, "damping") is { } dm) p.Physics.Damping = dm;
            if (TryOptFloat(args, "friction") is { } fr) p.Physics.Friction = fr;
            if (TryOptFloat(args, "thickness") is { } th) p.Physics.Thickness = th;
        }
        return new { ok = true, updatedPieces = targets.Count };
    }

    private object SetMaterial(JsonElement args)
    {
        string color = ReqString(args, "color");
        string name = OptString(args, "name") ?? "Fabric";
        var mat = new FabricMaterial { Name = _session.Doc.UniqueName(name), Color = color };
        _session.Doc.Materials.Add(mat);

        int updated = 0;
        if (OptString(args, "pieceId") is { Length: > 0 } pid)
        {
            var p = _session.Doc.FindPiece(pid) ?? throw new ArgumentException($"Piece '{pid}' not found.");
            p.MaterialId = mat.Id;
            updated = 1;
        }
        else
        {
            var g = ResolveGarment(OptString(args, "garmentId"));
            foreach (var p in g.Pieces) { p.MaterialId = mat.Id; updated++; }
        }
        return new { ok = true, materialId = mat.Id, color, updatedPieces = updated };
    }

    private object Simulate(JsonElement args)
    {
        return _session.RunSimulation(
            OptString(args, "garmentId"),
            OptInt(args, "frames", 90),
            OptBool(args, "allGarments", false),
            OptBool(args, "useGpu", true),
            OptBool(args, "selfCollision", true),
            OptInt(args, "substeps", 12));
    }

    private object ResetDrape(JsonElement args)
    {
        string? gid = OptString(args, "garmentId");
        int count = 0;
        foreach (var g in _session.Doc.Garments)
        {
            if (gid != null && g.Id != gid) continue;
            foreach (var p in g.Pieces)
            {
                p.Drape = null;
                p.DrapeKey = null;
                count++;
            }
        }
        return new { ok = true, resetPieces = count };
    }

    private object RenderPreview(JsonElement args)
    {
        string? png = OptString(args, "pngPath");
        string? svg = OptString(args, "svgPath");
        if (png == null && svg == null)
            throw new ArgumentException("Provide at least one of pngPath or svgPath.");
        if (png != null)
            _session.RenderDrapePreviewPng(png, OptInt(args, "width", 960), OptInt(args, "height", 480));
        if (svg != null)
            SvgPatternExporter.Export(_session.Doc, svg);
        return new
        {
            ok = true,
            pngPath = png != null ? Path.GetFullPath(png) : null,
            svgPath = svg != null ? Path.GetFullPath(svg) : null,
        };
    }

    private object ExportGarment(JsonElement args)
    {
        string path = ReqString(args, "path");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        if (ext == ".svg")
        {
            SvgPatternExporter.Export(_session.Doc, path);
            return new { ok = true, format = "svg", path = Path.GetFullPath(path) };
        }

        var options = new ExportOptions { WeldSeams = OptBool(args, "weldSeams", true) };
        var scene = ExportScene.Build(_session.Doc, _session.Workspace.MeshFor, options);
        switch (ext)
        {
            case ".obj": ObjExporter.Export(scene, path); break;
            case ".glb":
            case ".gltf": GltfExporter.Export(scene, path); break;
            case ".fbx": FbxExporter.Export(scene, path); break;
            default: throw new ArgumentException($"Unsupported export extension '{ext}'. Use .glb, .gltf, .obj, .fbx, or .svg.");
        }
        return new
        {
            ok = true,
            format = ext.TrimStart('.'),
            path = Path.GetFullPath(path),
            meshes = scene.Meshes.Count,
            triangles = scene.Meshes.Sum(m => m.TriangleCount),
        };
    }

    private static void EnsureCcw(List<PatternPoint> pts)
    {
        float area2 = 0f;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i].Position;
            var b = pts[(i + 1) % pts.Count].Position;
            area2 += a.X * b.Y - b.X * a.Y;
        }
        if (area2 < 0f)
        {
            // Reverse while keeping Bezier control points on the same geometric edges
            int n = pts.Count;
            var rev = new List<PatternPoint>(n);
            for (int i = 0; i < n; i++)
            {
                int src = (n - i) % n;
                int prev = (src - 1 + n) % n;
                var p = new PatternPoint(pts[src].Position.X, pts[src].Position.Y) { Curve = pts[prev].Curve };
                rev.Add(p);
            }
            pts.Clear();
            pts.AddRange(rev);
        }
    }

    // ---- JSON Schema & Argument Helpers ----

    private static JsonObject Tool(string name, string description, JsonObject inputSchema, params string[] required)
    {
        if (required.Length > 0 && !inputSchema.ContainsKey("required"))
        {
            var reqArr = new JsonArray();
            foreach (var r in required) reqArr.Add(r);
            inputSchema["required"] = reqArr;
        }
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema,
        };
    }

    private static JsonObject Props(params (string Name, string Type, string Desc)[] fields)
    {
        var props = new JsonObject();
        foreach (var (name, type, desc) in fields)
            props[name] = Prop(type, desc);
        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }

    private static JsonObject Prop(string type, string desc) => new() { ["type"] = type, ["description"] = desc };
    private static JsonObject NumArrayProp(string desc) => new()
    {
        ["type"] = "array",
        ["description"] = desc,
        ["items"] = new JsonObject { ["type"] = "number" },
    };

    private static string ReqString(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new ArgumentException($"Missing required string '{k}'.");
    private static string? OptString(JsonElement a, string k)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static float ReqFloat(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : throw new ArgumentException($"Missing required number '{k}'.");
    private static float OptFloat(JsonElement a, string k, float def)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : def;
    private static float? TryOptFloat(JsonElement a, string k)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
    private static int ReqInt(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : throw new ArgumentException($"Missing required integer '{k}'.");
    private static int OptInt(JsonElement a, string k, int def)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
    private static bool OptBool(JsonElement a, string k, bool def)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
    private static bool TryFloat3(JsonElement a, string k, out Vector3 vec)
    {
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 3)
        {
            vec = new Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle());
            return true;
        }
        vec = default;
        return false;
    }
}
