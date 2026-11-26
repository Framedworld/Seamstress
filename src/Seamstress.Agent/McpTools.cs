using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

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
        Tool("load_avatar", "Use a body mesh file as this project's avatar: OBJ (Y up), binary FBX or glTF/GLB (their own axes and units are converted). Returns tape measurements. The saved project remembers the file and scale.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = Prop("string", "Body file: .obj, .fbx, .glb or .gltf."),
                ["scale"] = Prop("number", "Extra scale (default 1). For OBJ this is metres per unit: 0.01 for a centimetre file."),
                ["meshes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "FBX/glTF only: names of the meshes to use. Default: every mesh except hair, brows, lashes, teeth and tongue.",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
            },
            ["required"] = new JsonArray("path"),
        }),
        Tool("load_avatar_obj", "Same as load_avatar (kept for older scripts).", Props(
            ("path", "string", "Body file path."),
            ("scale", "number", "Metres per OBJ unit (default 1, or 0.01 for centimetres).")), "path"),
        Tool("get_avatar_landmarks", "Get exact anatomical measurements (height, shoulder/chest/waist/hip widths, depths, circumferences, Y heights) of the current avatar for drafting fitted patterns and 3D placement.", Props()),
        Tool("locate_on_piece", "Locate a point on the current draped or arranged surface. Use the returned position and outward normal to place trim without reading raw drape arrays. A point outside the outline snaps to the nearest point of the piece (snappedCm says how far).", Props(
            ("pieceId", "string", "Host piece ID."),
            ("xCm", "number", "Local pattern X in centimetres."),
            ("yCm", "number", "Local pattern Y in centimetres.")), "pieceId", "xCm", "yCm"),
        Tool("add_template_garment", "Add a ready-made garment. The Fitted ones (Fitted Tee, Fitted Trousers, Fitted Jacket, Fitted Hoodie) are drafted from the avatar's own tape measurements, placed round it, sewn, with named edges, and set to be worn over whatever the figure already has on: add them from the skin outwards and simulate each. The older blocks (T-Shirt, Tank Top, Skirt, Dress) are one size, scaled to the avatar's height.", Props(
            ("template", "string", "Fitted Tee, Fitted Trousers, Fitted Jacket, Fitted Hoodie, T-Shirt, Tank Top, Skirt or Dress."),
            ("sleeves", "string", "Fitted tops: none, short or long (default: tee short, jacket and hoodie long)."),
            ("openFront", "boolean", "Fitted jacket/hoodie: leave the front open instead of sewn shut (default false). An open front drapes straighter with a temporary strip sewn across it while simulating."),
            ("frontGapCm", "number", "Fitted jacket/hoodie with openFront: how far apart the two front edges are cut (default 8)."),
            ("hemGirthCm", "number", "Fitted trousers: the leg opening round one leg in cm (default: a straight leg from the knee). Narrow enough to rest on the shoe, extra length (extraLengthCm) stacks in folds above it."),
            ("outseamBackCm", "number", "Fitted trousers: move the side seam this far towards the back (default 0), so pockets or a holster at the front's outer edge sit on the side of the leg."),
            ("hood", "boolean", "Fitted hoodie: add the hood now (default true). Pass false and call add_hood after the outer layers, so the hood lies over them."),
            ("easePercent", "number", "Fitted: extra width on top of the template's own ease (5 = roomier, -3 = closer)."),
            ("extraLengthCm", "number", "Fitted: longer body or legs by this much."),
            ("particleDistance", "number", "Fitted: mesh edge length in cm (default 1.0; 0.8 for finer folds)."),
            ("simulate", "boolean", "Fitted: drape it straight away (default false)."),
            ("frames", "integer", "Frames when simulating (default 130).")), "template"),
        Tool("add_collar", "Building block: a band standing up from a garment's neckline: a collar stand, a ribbed neck, or (with easePercent 15-30 and a soft fabric) the cowl a pushed-back hood makes round the neck. For garments made by the fitted templates, or any garment whose back has edges named neck_l and neck_r and whose two fronts have an edge named neck. It goes in its own garment over what is worn round the neck so far.", Props(
            ("garmentId", "string", "The garment whose neckline it is sewn to."),
            ("heightCm", "number", "Height of the band (default 8)."),
            ("easePercent", "number", "How much longer than the neckline it is cut; 0 stands smooth, 15-30 gathers into folds (default 0)."),
            ("frontPercent", "number", "How far down each front neck edge it runs, from the shoulder (default 100; about 30 keeps it to the back and sides of the neck)."),
            ("flarePercent", "number", "How much wider the top edge is than the bottom, so it leans out (default 10)."),
            ("rollCm", "number", "Above 0, the top edge is sewn down too, this far out from the neckline, so the band closes into a padded roll round the neck: the ring a pushed-down hood makes, a shawl collar or a neck roll. heightCm is then the distance round the roll (about 3x the roll's thickness)."),
            ("simulate", "boolean", "Drape it straight away (default true)."),
            ("frames", "integer", "Frames when simulating (default 110).")), "garmentId"),
        Tool("add_hood", "Building block: a hood for a garment made by Fitted Jacket or Fitted Hoodie (or any garment whose back has edges named neck_l and neck_r and whose two fronts have an edge named neck). Two panels joined down the middle, sewn round the neckline and laid open down the back as a hood falls when not worn. It goes in its own garment, over everything worn so far: add it after the outer layers.", Props(
            ("garmentId", "string", "The garment to put the hood on."),
            ("lengthCm", "number", "Hood length from the neck to its tip (default 36)."),
            ("widthCm", "number", "Half width of the open hood (default 26)."),
            ("easePercent", "number", "How much longer than the neckline the hood's neck edge is cut; more gives a fuller cowl (default 12)."),
            ("frontPercent", "number", "How far down each front neck edge the hood is sewn, from the shoulder (default 100). About 30 keeps the hood behind the neck, under a jacket whose collar should show."),
            ("simulate", "boolean", "Drape it straight away (default true)."),
            ("frames", "integer", "Frames when simulating (default 120).")), "garmentId"),
        Tool("create_garment", "Create a new empty garment layer.", Props(
            ("name", "string", "Garment name."),
            ("particleDistance", "number", "Target mesh edge length in cm (default 2.0; smaller = finer folds)."),
            ("topology", "string", "Triangles or Quads (default Triangles)."),
            ("thicknessMm", "number", "Optional garment-wide collision thickness in mm."),
            ("layer", "integer", "Layer priority (0 = base layer, higher = outer garment pushed strictly outside lower layers)."),
            ("bodyOffsetMm", "number", "Room in mm kept between this garment and the skin for what is worn under it (0 for the innermost garment; about 3 over a tee, 8 for a jacket over a tee). Does not thicken the cloth itself.")), "name"),
        Tool("set_garment", "Change a garment's settings after it was created: name, layer priority, body offset, mesh density, thickness, or whether it is visible (hidden garments are neither simulated nor collided with).", Props(
            ("garmentId", "string", "Garment ID (defaults to the active garment)."),
            ("name", "string", "New name."),
            ("layer", "integer", "Layer priority (0 = base layer, higher = outer)."),
            ("bodyOffsetMm", "number", "Room in mm kept between this garment and the skin for what is worn under it."),
            ("particleDistance", "number", "Target mesh edge length in cm. Changing it re-meshes the garment and clears its drape."),
            ("thicknessMm", "number", "Garment-wide collision thickness in mm."),
            ("visible", "boolean", "Show or hide the garment."),
            ("frozen", "boolean", "Freeze: it holds still, and the garments you simulate still collide with it."),
            ("deactivated", "boolean", "Deactivate: leave it out of the simulation entirely (not simulated, not collided with; other cloth passes through it). It stays visible, drawn faded. False switches it back on."))),
        Tool("set_quality", "Switch simulation quality for the whole project. Draft meshes every garment no finer than about 2 cm (hard parts keep their detail), so big outfits simulate several times faster while you build and arrange; Final uses each garment's own particle distance. Drapes, pins and creases are carried across to the other meshes through the 2D pattern. Refine to final before exporting or judging fine folds; creases carried from Draft are softer and may need setting again.", Props(
            ("quality", "string", "draft or final."),
            ("draftParticleDistance", "number", "Optional: the Draft mesh's particle distance in cm (default 2)."))),
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
                            ["edge"] = Prop("string", "Optional name for the edge that leaves this point (e.g. 'hem', 'side_l'); seams can then refer to it by name"),
                        },
                        ["required"] = new JsonArray("x", "y"),
                    },
                },
                ["originX"] = Prop("number", "2D canvas origin X in cm (default: a free place in the pattern window)."),
                ["originY"] = Prop("number", "2D canvas origin Y in cm."),
                ["drawnOn"] = Prop("string", "Easiest placement: 'front' or 'back'. The points are then body centimetres (x = cm towards the avatar's left of the centre line, y = cm above the floor, as seen from the front for both) and the piece is put where it was drawn, depthCm in front of or behind the body, facing outwards. No pos3d/rot3d needed. Note: a back piece's own pattern x runs the other way (pattern x = -body x) when you later refer to points on it."),
                ["depthCm"] = Prop("number", "With drawnOn: distance of the piece from the body's centre plane (default 25)."),
                ["mirrorX"] = Prop("boolean", "With drawnOn: reflect the outline to the other side of the body first (draw the left piece once, add the right one with mirrorX)."),
                ["fabric"] = new JsonObject { ["type"] = "object", ["description"] = "Fabric physics to set at once, same fields as set_fabric: preset, density, stretch, shear, bend, damping, friction, thickness, shrinkWeft, shrinkWarp." },
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
            ("originX", "number", "2D origin X in cm (default: a free place in the pattern window)."),
            ("originY", "number", "2D origin Y in cm."),
            ("x", "number", "3D X of the piece centre in metres."),
            ("y", "number", "3D Y of the piece centre in metres."),
            ("z", "number", "3D Z of the piece centre in metres."),
            ("ry", "number", "Rotation about Y in degrees (180 for a back piece)."),
            ("curvature", "number", "Cylindrical wrap in degrees (0 = flat)."),
            ("fabricPreset", "string", "Fabric preset name."),
            ("color", "string", "Hex color like #4F8FD9.")), "name", "widthCm", "heightCm"),
        Tool("add_patch", "Building block: sew a patch, pocket or flap onto a draped piece in one step. Give the footprint in the host piece's own pattern cm (x, y = lower-left corner, widthCm, heightCm; or 'points'), or centre it on a seam with acrossEdge + fromCm/toCm + widthCm (a cargo pocket on an outseam). It is cut with ease, placed on the surface, put in a trim garment with the right layer and body offset, sewn (pocket: bottom and sides; flap: top only; patch: all round) and simulated.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["hostPieceId"] = Prop("string", "The draped piece to sew onto."),
                ["kind"] = Prop("string", "patch (default, sewn all round), pocket (top left open) or flap (sewn along its top only)."),
                ["name"] = Prop("string", "Name for the new piece."),
                ["x"] = Prop("number", "Left of the footprint in host pattern cm."),
                ["y"] = Prop("number", "Bottom of the footprint in host pattern cm."),
                ["widthCm"] = Prop("number", "Footprint width (for a seam patch: total width, half on each side of the seam)."),
                ["heightCm"] = Prop("number", "Footprint height."),
                ["points"] = new JsonObject { ["type"] = "array", ["description"] = "Footprint outline in host pattern cm instead of x/y/width/height: [{x, y}].", ["items"] = new JsonObject { ["type"] = "object" } },
                ["acrossEdge"] = Prop("edge", "Centre the patch on the seam along this edge of the host (name or index). Use with fromCm, toCm and widthCm."),
                ["fromCm"] = Prop("number", "Seam patch: where it starts, in cm along acrossEdge from the start of that edge."),
                ["toCm"] = Prop("number", "Seam patch: where it ends, in cm along acrossEdge."),
                ["easePercent"] = Prop("number", "How much larger than its footprint the patch is cut (default 4, seam patches 12; 0 lies flat, 30+ makes a bellows pocket)."),
                ["level"] = Prop("integer", "1 = lying on the host (default), 2 = over other trim (default for flaps)."),
                ["stitchEdges"] = new JsonObject { ["type"] = "array", ["description"] = "Sew only these edges of the footprint (0-based, edge i leaves point i of counter-clockwise 'points') instead of what 'kind' implies: one edge for a collar leaf, a facing or a hanging strap.", ["items"] = new JsonObject { ["type"] = "integer" } },
                ["garmentName"] = Prop("string", "Put the patch in a trim garment of this name (made if missing) instead of the host's default trim garment."),
                ["color"] = Prop("string", "Hex colour; by default the patch uses the host's fabric."),
                ["bend"] = Prop("number", "0-100 stiffness override."),
                ["simulate"] = Prop("boolean", "Simulate the trim garment afterwards (default true)."),
                ["frames"] = Prop("integer", "Frames to simulate (default 60)."),
            },
            ["required"] = new JsonArray("hostPieceId"),
        }),
        Tool("add_tube", "Building block: a tube round a limb (sleeve, cuff, trouser leg), wrapped round a line from 'start' along 'axis' with its own seam already sewn on the underside. Use limb to follow a measured arm. sewStartTo sews its start edge to existing edges (a sleeve to the front and back armholes, a cuff to the end of a sleeve).", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["garmentId"] = Prop("string", "Garment to add it to (defaults to the active garment). Must be the garment of the edges in sewStartTo."),
                ["name"] = Prop("string", "Piece name."),
                ["limb"] = Prop("string", "left_arm or right_arm: take start and axis from the avatar's measured arm."),
                ["offsetCm"] = Prop("number", "With limb: distance along the arm from the shoulder at which the tube starts (default 0)."),
                ["start"] = NumArrayProp("Without limb: centre of the start end [x, y, z] in metres."),
                ["axis"] = NumArrayProp("Without limb: direction from the start end to the far end."),
                ["startCircCm"] = Prop("number", "Circumference at the start end. Omit to use the total length of the sewStartTo edges."),
                ["endCircCm"] = Prop("number", "Circumference at the far end."),
                ["lengthCm"] = Prop("number", "Length along the limb."),
                ["capCm"] = Prop("number", "Height of a shallow sleeve cap at the start (default 0; 2-3 for a set-in sleeve)."),
                ["split"] = Prop("boolean", "Split the start edge in two on top (default: true when sewStartTo has two edges)."),
                ["sewStartTo"] = new JsonObject { ["type"] = "array", ["description"] = "Edges to sew the start to: [{pieceId, edge}] (edge by name or index). Two edges = front and back armhole.", ["items"] = new JsonObject { ["type"] = "object" } },
                ["likePieceId"] = Prop("string", "Copy fabric and material from this piece (default: the first sewStartTo piece)."),
            },
            ["required"] = new JsonArray("name", "endCircCm", "lengthCm"),
        }),
        Tool("add_hardware", "Building block: a hard part set on a draped piece and pinned so it never flops: snap, button, rivet, bar, buckle, plate, pen, handle, cylinder, wrench, or your own outline. Not sewn and not simulated.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["hostPieceId"] = Prop("string", "The draped piece it sits on."),
                ["x"] = Prop("number", "Where, in host pattern cm."),
                ["y"] = Prop("number", "Where, in host pattern cm (the bottom of a pen, handle or tool)."),
                ["shape"] = Prop("string", "snap (default), button, rivet, bar, buckle, plate, pen, handle, cylinder or wrench."),
                ["widthCm"] = Prop("number", "Width or diameter (default 1.7; for pen/handle/cylinder the diameter)."),
                ["heightCm"] = Prop("number", "Height for bars, pens, handles and tools."),
                ["points"] = new JsonObject { ["type"] = "array", ["description"] = "Custom outline in cm [{x, y}] instead of shape.", ["items"] = new JsonObject { ["type"] = "object" } },
                ["name"] = Prop("string", "Piece name."),
                ["garmentName"] = Prop("string", "Garment that collects hard parts (default 'Hardware')."),
                ["thicknessMm"] = Prop("number", "How thick the part is drawn (default 2.2; used when the garment is first made)."),
                ["liftMm"] = Prop("number", "Distance from the host surface (default: just clear of it)."),
                ["spinDegrees"] = Prop("number", "Turn about the surface normal."),
                ["color"] = Prop("string", "Hex colour (default antique brass)."),
                ["metallic"] = Prop("number", "0..1 (default 0.9)."),
                ["roughness"] = Prop("number", "0..1 (default 0.3)."),
            },
            ["required"] = new JsonArray("hostPieceId", "x", "y"),
        }),
        Tool("pin_piece", "Hold a whole piece exactly where it is during simulation (or release it): a cuff that must stay on the forearm while the sleeve above it is pushed up, a collar stand, a belt. Pinned cloth does not move at all until unpinned.", Props(
            ("pieceId", "string", "Piece ID."),
            ("pinned", "boolean", "true to pin every point of the piece (default), false to release it.")), "pieceId"),
        Tool("pull_cloth", "Shape folds by hand: grab the draped cloth round a point of a piece and pull it a few centimetres while the rest of the garment simulates, the way a garment artist tugs fabric into place (drag folds from a pulled hem, a knee pushed forward, a sleeve hiked up). The grabbed patch fades from full pull at the point to none at the radius. It is let go afterwards and the cloth settles, keeping the folds the pull made. Simulate the garment first.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["pieceId"] = Prop("string", "Piece to grab."),
                ["x"] = Prop("number", "Point to grab, in the piece's pattern cm."),
                ["y"] = Prop("number", "Point to grab, in the piece's pattern cm."),
                ["radiusCm"] = Prop("number", "How much cloth is grabbed round the point (default 5)."),
                ["offsetCm"] = NumArrayProp("World pull [dx, dy, dz] in cm (y up, z towards the front of the avatar)."),
                ["outCm"] = Prop("number", "Pull along the cloth's own outward normal at the point, in cm (negative pushes it in). Added to offsetCm."),
                ["steps"] = Prop("integer", "The pull is made in this many moves, simulating between them (default 4)."),
                ["framesPerStep"] = Prop("integer", "Frames simulated after each move (default 20)."),
                ["settleFrames"] = Prop("integer", "Frames simulated after letting go (default 40; 0 keeps the result exactly as pulled)."),
                ["hold"] = Prop("boolean", "Keep the grabbed cloth pinned where it was pulled to instead of letting go (default false)."),
                ["creasePercent"] = Prop("number", "Set creases in the whole garment at the pulled shape before letting go, this firmly (0-100; default 0 = no). Without it stiff cloth springs back towards flat once released; with it the pulled folds stay. Same as calling set_creases while holding."),
            },
            ["required"] = new JsonArray("pieceId", "x", "y"),
        }),
        Tool("set_creases", "Set creases: the cloth remembers the folds it has now, like canvas or denim keeps the creases it was worn or pressed into, instead of springing back flat when simulated again. Shape the folds first (pull_cloth, a pose, a simulation), then set creases. Setting them again takes the shape as it is then, old creases included. Saved with the project.", Props(
            ("garmentId", "string", "Garment to crease (every piece)."),
            ("pieceId", "string", "Or a single piece."),
            ("strengthPercent", "number", "How firmly the folds are held: 100 fully set, 50 halfway to flat (default 80)."),
            ("clear", "boolean", "Forget the creases instead: the cloth bends back towards flat again (default false)."))),
        Tool("layout_pattern", "Lay every piece out again in tidy rows in the 2D pattern window, one garment under the other. New pieces are placed automatically; use this after deleting or resizing pieces.", Props()),
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
            ("edgeIndex", "edge", "Edge to inset: its name, or its 0-based index."),
            ("widthCm", "number", "Band width in cm (default 3.0).")), "pieceId", "edgeIndex"),
        Tool("delete_piece", "Delete a pattern piece and its connected seams.", Props(("pieceId", "string", "Piece ID.")), "pieceId"),
        Tool("sew_edges", "Sew edgeA of pieceA to edgeB of pieceB. Returns edge lengths and length mismatch %.", Props(
            ("pieceAId", "string", "First piece ID."),
            ("edgeA", "edge", "Edge of the first piece: its name, or its 0-based index."),
            ("pieceBId", "string", "Second piece ID."),
            ("edgeB", "edge", "Edge of the second piece: its name, or its 0-based index."),
            ("reversed", "boolean", "Leave this out: the direction that does not twist is then worked out from where the pieces are. Set it only to force start-to-end (false) or start-to-start (true).")), "pieceAId", "edgeA", "pieceBId", "edgeB"),
        Tool("sew_named", "Sew two pieces along every edge that has the same name on both (for example side_l, side_r, shoulder_l, shoulder_r), in one call and without twists. Edges are named with 'edge' on the points of add_piece.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["pieceAId"] = Prop("string", "First piece ID."),
                ["pieceBId"] = Prop("string", "Second piece ID."),
                ["names"] = new JsonObject { ["type"] = "array", ["description"] = "Edge names to sew, present on both pieces.", ["items"] = new JsonObject { ["type"] = "string" } },
            },
            ["required"] = new JsonArray("pieceAId", "pieceBId", "names"),
        }),
        Tool("auto_sew", "Sew edges that already lie against each other in 3D (same length, ends within a tolerance): neighbouring panels in one plane, a band along a hem, an imported or draped garment. Does not guess seams between a front and a back that are apart: use sew_named for those.", Props(
            ("garmentId", "string", "Garment to look in (defaults to the active garment)."),
            ("toleranceCm", "number", "How close the ends of two edges must be (default 2)."),
            ("dryRun", "boolean", "Only list what would be sewn (default false).")), "garmentId"),
        Tool("sew_surface_stitch", "Sew a boundary edge of a patch piece (chest pocket, cargo pocket, flap, knee patch, belt loop, or rag) directly onto a 2D line segment (x1,y1)->(x2,y2) inside a host piece (same garment or obstacle garment).", Props(
            ("sourcePieceId", "string", "Patch/pocket/belt piece ID."),
            ("sourceEdge", "edge", "Edge of the patch: its name, or its 0-based index."),
            ("targetPieceId", "string", "Host piece ID (in the same garment or an already-draped lower-layer garment)."),
            ("x1", "number", "Target line start X in host piece local cm."),
            ("y1", "number", "Target line start Y in host piece local cm."),
            ("x2", "number", "Target line end X in host piece local cm."),
            ("y2", "number", "Target line end Y in host piece local cm.")), "sourcePieceId", "sourceEdge", "targetPieceId", "x1", "y1", "x2", "y2"),
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
            ("thickness", "number", "Collision thickness in mm."),
            ("shrinkWeft", "number", "Horizontal rest-length multiplier (e.g. 0.88 = 12% elastic cinch for waistbands/cuffs)."),
            ("shrinkWarp", "number", "Vertical rest-length multiplier (default 1.0)."))),
        Tool("set_material", "Set the color, texture, PBR roughness/metallic, and material name on a piece or garment.", Props(
            ("pieceId", "string", "Target piece ID (optional if garmentId given)."),
            ("garmentId", "string", "Target garment ID (optional if pieceId given)."),
            ("name", "string", "Material name."),
            ("color", "string", "Hex colour #RRGGBB. It tints (multiplies) the texture, so leave it out with a texture: it then defaults to white."),
            ("texture", "string", "Optional absolute path to a base-color PNG/JPG texture. Files next to it named *_normal, *_rough and *_metal are picked up as PBR maps."),
            ("tileCm", "number", "Real-world width in cm that one copy of the texture covers. The fabric then looks the same size on every garment. Preferred over repeat."),
            ("repeat", "number", "Texture tiling count across the garment's whole 2D layout (default 1; ignored with tileCm). Use 0 to fit an image once across each piece, for patches and decals."),
            ("roughness", "number", "Optional PBR roughness 0..1 (default 0.8)."),
            ("metallic", "number", "Optional PBR metallic 0..1 (default 0.0)."))),
        Tool("simulate", "Run cloth simulation (CUDA GPU-accelerated on RTX 4090 when available) to sew and drape the garment on the avatar.", Props(
            ("garmentId", "string", "Optional garment ID (defaults to active garment)."),
            ("frames", "integer", "Number of 60Hz frames to simulate (default 90)."),
            ("allGarments", "boolean", "Simulate all visible garments together (default false)."),
            ("useGpu", "boolean", "Use CUDA GPU acceleration if available (default true)."),
            ("selfCollision", "boolean", "Enable cloth-to-cloth & hem collision (default true)."),
            ("substeps", "integer", "XPBD substeps per frame (default 12)."))),
        Tool("reset_drape", "Clear simulated drape and return pieces to their flat 3D arrangement.", Props(("garmentId", "string", "Optional garment ID (defaults to all garments)."))),
        Tool("inspect_fit", "Inspect seam length mismatches, avatar penetrations, vertex/triangle counts, and 3D bounding box.", Props(("garmentId", "string", "Optional garment ID."))),
        Tool("fit_map", "The numbers behind the 3D view's fit maps, per garment: stretch (strain against the pattern), pressure (how hard the cloth presses on the body) and distance (cm of air off the body). Each gives min, max, mean, the share over and under its thresholds, and the worst places with the piece, a body region, the 3D position and the local pattern point. Simulate first: it measures the cloth as it lies now.", Props(
            ("map", "string", "Optional: stretch, pressure or distance (default: all three)."),
            ("garmentId", "string", "Optional garment ID (default: every visible garment)."),
            ("worst", "integer", "Optional number of worst places per garment (default 5)."))),
        Tool("ease_pattern", "Let the pattern out where the draped cloth is stretched across by more than a target: each piece is widened row by row, only where it is tight, and stays draped where it was. Without apply it only reports what it would change (per piece: stretch across and along, cm added and where, edge lengths; seams whose two edges now differ). Simulate again after applying and check with fit_map; repeat while still over. Stretch along a piece is reported, not eased; elastic and fully pinned pieces are skipped.", Props(
            ("garmentId", "string", "Optional garment ID (default: the active garment)."),
            ("targetStretchPercent", "number", "Stretch across that is fine (default 2)."),
            ("strength", "number", "Share of the correction to make in one pass, 0.05..1 (default 0.6)."),
            ("pieceIds", "string", "Optional comma-separated piece IDs to ease (default: every visible piece)."),
            ("includeElastic", "boolean", "Also ease pieces with elastic across below 1 (default false)."),
            ("apply", "boolean", "Change the pattern (default false: only report)."))),
        Tool("render_preview", "Render a shaded, textured PNG of the draped outfit without the desktop app (and/or a 2D pattern SVG). Any camera: named views side by side, or yaw/pitch; frame the whole figure, a point, or one piece for a close look.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["pngPath"] = Prop("string", "Output PNG path."),
                ["svgPath"] = Prop("string", "Optional output path for a 1:1 cm 2D sewing pattern SVG."),
                ["width"] = Prop("integer", "PNG width in pixels (default 960; shared between the views)."),
                ["height"] = Prop("integer", "PNG height in pixels (default 480)."),
                ["views"] = Prop("string", "Comma-separated views drawn side by side: front, back, side, left, right, quarter, quarter_right, quarter_back, top (default 'front,side,back')."),
                ["yaw"] = Prop("number", "Custom single view: degrees round the figure (0 = front, 90 = the avatar's left side, 180 = back)."),
                ["pitch"] = Prop("number", "Custom single view: degrees the camera is raised (default 5)."),
                ["focusPieceId"] = Prop("string", "Frame this piece (with some margin) instead of the whole figure."),
                ["focus"] = NumArrayProp("Frame this point [x, y, z] in metres instead of the whole figure (with spanCm)."),
                ["spanCm"] = Prop("number", "Height of the framed area in cm (default: fit everything, or the focused piece)."),
                ["showAvatar"] = Prop("boolean", "Draw the body (default true)."),
            },
        }),
        Tool("export_garment", "Export the garment to .glb, .gltf, .obj, .fbx, or .svg (2D sewing pattern). With rigged, garments are bound to a rigged avatar's skeleton with skin weights, ready to animate in a game engine.", Props(
            ("path", "string", "Output file path with extension (.glb, .gltf, .obj, .fbx, or .svg)."),
            ("weldSeams", "boolean", "Weld closed seams on 3D export (default true)."),
            ("rigged", "boolean", "glTF/FBX only: export the avatar's skeleton and bind the garments to it as skinned meshes (default false). Needs an avatar loaded from a rigged FBX or glTF."),
            ("includeAvatar", "boolean", "With rigged: also export the body as a skinned mesh (default false).")), "path"),
        Tool("import_mesh_as_pattern", "Import a sculpted 3D clothing blockout .obj (e.g. from ZBrush with UV Master UVs or Polygroups) into editable 2D Seamstress pattern pieces with automatic 3D-welded seam detection and 3D placement.", Props(
            ("path", "string", "Path to source .obj file."),
            ("scale", "number", "Unit scale to metres (default 1.0; use 0.01 for cm-scale Unreal/ZBrush/Maya exports)."),
            ("zUpToYUp", "boolean", "Convert Z-up coordinates to Y-up (default false)."),
            ("useUvs", "boolean", "Use UV islands when present (default true; falls back to PCA/ARAP flattening of Polygroups)."),
            ("splitByGroups", "boolean", "Split panels across OBJ g/o Polygroup boundaries (default true)."),
            ("autoSew", "boolean", "Automatically create seams across 3D-welded panel borders (default true)."),
            ("simplifyToleranceCm", "number", "Outline simplification tolerance in cm (default 0.6)."),
            ("particleDistanceCm", "number", "Target simulation mesh edge length in cm (default 2.0)."),
            ("garmentName", "string", "Optional name for the imported garment.")), "path"),
        Tool("list_body_profiles", "List all saved parametric body profiles in the Seamstress Body Library.", Props()),
        Tool("save_body_profile", "Save a parametric body profile (Mannequin scales, OBJ, MorphTargetObj, or MetaHuman .dna) to the Body Library.", Props(
            ("name", "string", "Profile name."),
            ("description", "string", "Optional notes."),
            ("sourceType", "string", "Mannequin, Obj, MorphTargetObj, or MetaHumanDna (default Mannequin)."),
            ("baseMeshPath", "string", "Optional path to base .obj or .dna file."),
            ("scale", "number", "Unit scale to metres (default 1.0; 0.01 for cm Unreal/MetaHuman OBJs)."),
            ("zUpToYUp", "boolean", "Convert Z-up to Y-up (default false)."),
            ("heightScale", "number", "Parametric vertical scale (default 1.0)."),
            ("widthScale", "number", "Parametric lateral scale (default 1.0)."),
            ("depthScale", "number", "Parametric front-to-back scale (default 1.0).")), "name"),
        Tool("load_body_profile", "Switch the active simulation avatar to a saved Body Library profile (or inline parametric Mannequin scales).", Props(
            ("profile", "string", "Saved profile ID or name (optional if passing inline scales)."),
            ("heightScale", "number", "Inline mannequin height scale (default 1.0)."),
            ("widthScale", "number", "Inline mannequin width scale (default 1.0)."),
            ("depthScale", "number", "Inline mannequin depth scale (default 1.0)."))),
        Tool("inspect_metahuman_dna", "Inspect a MetaHuman .dna file header, format version, size, and whether an OpenRigLogic Python extraction bridge is available.", Props(
            ("path", "string", "Path to MetaHuman .dna file.")), "path"),
        Tool("refit_garment_to_body", "Auto-fit a garment from the current avatar onto a same-topology target body profile (or parametric mannequin scales) via barycentric transfer, 2D pattern auto-grading, and CUDA morph-drape simulation.", Props(
            ("garmentId", "string", "Optional garment ID (defaults to active garment)."),
            ("targetProfile", "string", "Saved target Body Library profile ID or name (optional if passing inline scales)."),
            ("targetHeightScale", "number", "Inline target mannequin height scale (default 1.0)."),
            ("targetWidthScale", "number", "Inline target mannequin width scale (default 1.0)."),
            ("targetDepthScale", "number", "Inline target mannequin depth scale (default 1.0)."),
            ("grade2DPatterns", "boolean", "Auto-grade 2D pattern outlines to match target body proportions (default true)."),
            ("runMorphDrape", "boolean", "Run staged morph-drape cloth simulation onto target body (default true)."),
            ("morphStages", "integer", "Intermediate morph stages (default 3)."),
            ("settleFrames", "integer", "Final settling frames on target body (default 30)."))),
        Tool("step_pose", "Advance the avatar to the next posed same-topology OBJ mesh (e.g. T-pose -> A-pose), smoothly carrying draped garments along the bone motion without resizing 2D patterns and stepping CUDA cloth physics.", Props(
            ("targetObjPath", "string", "Path to the next posed avatar .obj file."),
            ("frames", "integer", "CUDA cloth relaxation frames to run at this pose step (default 12)."),
            ("substeps", "integer", "XPBD substeps per frame (default 14)."),
            ("carryCloth", "boolean", "Carry existing draped garment vertices incrementally via barycentric triangle binding before simulating (default true)."),
            ("allGarments", "boolean", "Simulate all visible garments simultaneously instead of layer-by-layer (default false)."),
            ("selfCollision", "boolean", "Enable cloth self-collision during this pose step (default false)."),
            ("useGpu", "boolean", "Use CUDA GPU acceleration (default true).")), "targetObjPath")
    );

    public object Dispatch(string name, JsonElement args)
    {
        return name switch
        {
            "new_project" => NewProject(args),
            "open_project" => OpenProject(args),
            "save_project" => SaveProject(args),
            "get_project_summary" => GetProjectSummary(),
            "load_avatar" or "load_avatar_obj" => LoadAvatarObj(args),
            "get_avatar_landmarks" => _session.GetAvatarLandmarks(),
            "locate_on_piece" => LocateOnPiece(args),
            "add_template_garment" => AddTemplateGarment(args),
            "add_hood" => AddHood(args),
            "add_collar" => AddCollar(args),
            "create_garment" => CreateGarment(args),
            "set_garment" => SetGarment(args),
            "set_quality" => SetQuality(args),
            "add_piece" => AddPiece(args),
            "add_rectangle_piece" => AddRectanglePiece(args),
            "add_patch" => AddPatch(args),
            "add_tube" => AddTube(args),
            "add_hardware" => AddHardware(args),
            "pin_piece" => PinPiece(args),
            "pull_cloth" => PullCloth(args),
            "set_creases" => SetCreases(args),
            "layout_pattern" => new { ok = true, movedPieces = PatternLayout.ArrangeAll(_session.Doc) },
            "mirror_piece" => MirrorPiece(args),
            "cut_piece" => CutPiece(args),
            "inset_band" => InsetBand(args),
            "delete_piece" => DeletePiece(args),
            "sew_edges" => SewEdges(args),
            "sew_named" => SewNamed(args),
            "auto_sew" => AutoSew(args),
            "sew_surface_stitch" => SewSurfaceStitch(args),
            "reverse_seam" => ReverseSeam(args),
            "delete_seam" => DeleteSeam(args),
            "arrange_piece_3d" => ArrangePiece3D(args),
            "set_fabric" => SetFabric(args),
            "set_material" => SetMaterial(args),
            "simulate" => Simulate(args),
            "reset_drape" => ResetDrape(args),
            "inspect_fit" => _session.InspectFit(OptString(args, "garmentId")),
            "ease_pattern" => EaseReport.Run(_session.Workspace, OptString(args, "garmentId"), OptFloat(args, "targetStretchPercent", 2f),
                OptFloat(args, "strength", 0.6f), OptString(args, "pieceIds"), OptBool(args, "includeElastic", false), OptBool(args, "apply", false)),
            "fit_map" => FitMapReport.Build(_session.Doc, _session.Workspace.MeshFor, _session.ActiveAvatar,
                FitMaps.Parse(OptString(args, "map")), OptString(args, "garmentId"), Math.Clamp(OptInt(args, "worst", 5), 1, 50)),
            "render_preview" => RenderPreview(args),
            "export_garment" => ExportGarment(args),
            "import_mesh_as_pattern" => ImportMeshAsPattern(args),
            "list_body_profiles" => ListBodyProfiles(),
            "save_body_profile" => SaveBodyProfile(args),
            "load_body_profile" => LoadBodyProfile(args),
            "inspect_metahuman_dna" => MetaHumanDnaLoader.Inspect(ReqString(args, "path")),
            "refit_garment_to_body" => RefitGarmentToBody(args),
            "step_pose" => StepPose(args),
            _ => throw new ArgumentException($"Unknown tool '{name}'."),
        };
    }

    private object NewProject(JsonElement args)
    {
        _session.NewProject();
        if (OptString(args, "name") is { Length: > 0 } n)
            _session.Doc.Name = n;
        return new { ok = true, name = _session.Doc.Name, computeBackend = _session.ComputeBackend };
    }

    private object OpenProject(JsonElement args)
    {
        string path = ReqString(args, "path");
        _session.OpenProject(path);
        return GetProjectSummary();
    }

    private object LocateOnPiece(JsonElement args)
    {
        string id = ReqString(args, "pieceId");
        var piece = _session.Doc.FindPiece(id) ?? throw new ArgumentException($"Piece '{id}' was not found.");
        var garment = _session.Doc.GarmentOfPiece(id)!;
        var mesh = _session.Workspace.MeshFor(garment).Find(id)!;
        var point = new Vector2(ReqFloat(args, "xCm"), ReqFloat(args, "yCm"));
        var positions = Mesher.CurrentPositions(piece, mesh);
        if (!SurfaceLocator.TryLocate(mesh, positions, point, piece.FlipNormals, out var position, out var normal, out float offCm))
            throw new ArgumentException("This piece has no surface yet.");
        return new
        {
            positionMetres = new[] { position.X, position.Y, position.Z },
            normal = new[] { normal.X, normal.Y, normal.Z },
            draped = piece.Drape != null,
            snappedCm = MathF.Round(offCm, 2),
        };
    }

    private object LoadAvatarObj(JsonElement args)
    {
        var path = Path.GetFullPath(ReqString(args, "path"));
        float scale = OptFloat(args, "scale", 1f);
        if (!float.IsFinite(scale) || scale <= 0)
            throw new ArgumentException("Scale must be positive and finite.");
        if (!Avatar.MeshExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new ArgumentException("Choose a body mesh saved as OBJ, binary FBX or glTF/GLB.");
        HashSet<string>? names = null;
        if (args.TryGetProperty("meshes", out var meshes) && meshes.ValueKind == JsonValueKind.Array && meshes.GetArrayLength() > 0)
            names = meshes.EnumerateArray().Select(m => m.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parts = _session.LoadAvatar(path, scale, names);
        return new
        {
            meshesUsed = parts.Used,
            meshesSkipped = parts.Skipped,
            skeletonJoints = _session.ActiveAvatar?.Rig?.Skeleton.Count ?? 0, // > 0: export_garment can use rigged
            landmarks = _session.GetAvatarLandmarks(),
        };
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
            server = BuildInfo.Describe(GetToolDefinitions().Count),
            avatar = _session.ActiveAvatar?.Name,
            quality = doc.Simulation.Quality.ToString().ToLowerInvariant(),
            garments = doc.Garments.Select(g => new
            {
                id = g.Id,
                name = g.Name,
                visible = g.Visible,
                frozen = g.Frozen,
                deactivated = g.Deactivated,
                particleDistance = g.ParticleDistance,
                topology = g.Topology.ToString(),
                thicknessMm = g.Thickness,
                layer = g.Layer,
                bodyOffsetMm = g.BodyOffset,
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
                    hasDrape = p.Drape != null && _session.Workspace.MeshFor(g).Find(p.Id)?.Key == p.DrapeKey,
                    points = p.Points.Select(pt => new { x = MathF.Round(pt.Position.X, 2), y = MathF.Round(pt.Position.Y, 2), edge = pt.EdgeName }),
                    edges = Enumerable.Range(0, p.EdgeCount).Select(e => new
                    {
                        edge = e,
                        name = p.PointAt(e).EdgeName,
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
        var body = _session.ActiveAvatar?.Mesh;
        var bounds = body?.Bounds();
        var options = new FitOptions
        {
            Sleeves = OptString(args, "sleeves"),
            OpenFront = OptBool(args, "openFront", false),
            FrontGapCm = OptFloat(args, "frontGapCm", 8f),
            Hood = OptBool(args, "hood", true),
            Ease = OptFloat(args, "easePercent", 0f) / 100f,
            ExtraLengthCm = OptFloat(args, "extraLengthCm", 0f),
            OutseamBackCm = OptFloat(args, "outseamBackCm", 0f),
            HemGirthCm = OptFloat(args, "hemGirthCm", 0f),
            ParticleDistance = TryOptFloat(args, "particleDistance"),
        };
        var made = Templates.CreateAll(template, _session.Doc, bounds?.Max.Y - bounds?.Min.Y ?? Templates.ReferenceBodyHeight, body, options);
        _session.Doc.Garments.AddRange(made);
        _session.Workspace.SetActiveGarment(made[0].Id);
        var simulations = new List<object>();
        if (OptBool(args, "simulate", false))
        {
            foreach (var g in made)
                simulations.Add(_session.RunSimulation(g.Id, OptInt(args, "frames", 130), false, true, false, 12));
        }
        return new
        {
            garmentId = made[0].Id,
            name = made[0].Name,
            garments = made.Select(g => new
            {
                garmentId = g.Id,
                name = g.Name,
                layer = g.Layer,
                bodyOffsetMm = MathF.Round(g.BodyOffset, 1),
                seamCount = g.Seams.Count,
                pieces = g.Pieces.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    points = p.Points.Select(pt => new { x = MathF.Round(pt.Position.X, 2), y = MathF.Round(pt.Position.Y, 2), edge = pt.EdgeName }),
                    edges = Enumerable.Range(0, p.EdgeCount).Select(e => new { edge = e, name = p.PointAt(e).EdgeName, lengthCm = MathF.Round(Outline.EdgeLength(p, e), 1) }),
                }),
            }),
            simulations,
            note = simulations.Count == 0 ? "Simulate each garment to drape it, innermost first." : null,
        };
    }

    private object AddCollar(JsonElement args)
    {
        var host = ResolveGarment(OptString(args, "garmentId"));
        var collar = FittedGarments.Collar(_session.Doc, host, BodyMeasurements.Measure(_session.Avatar.Mesh), OptFloat(args, "heightCm", 8f),
            1f + OptFloat(args, "easePercent", 0f) / 100f, OptFloat(args, "frontPercent", 100f) / 100f, 1f + OptFloat(args, "flarePercent", 10f) / 100f,
            OptFloat(args, "rollCm", 0f));
        _session.Doc.Garments.Add(collar);
        return BlockReport(new BlockResult(collar, collar.Pieces.ToList(), "Simulate '" + collar.Name + "' to drape it."), args, 110);
    }

    private object AddHood(JsonElement args)
    {
        var host = ResolveGarment(OptString(args, "garmentId"));
        var hood = FittedGarments.Hood(_session.Doc, host, BodyMeasurements.Measure(_session.Avatar.Mesh),
            OptFloat(args, "lengthCm", 36f), OptFloat(args, "widthCm", 26f), 1f + OptFloat(args, "easePercent", 12f) / 100f,
            OptFloat(args, "frontPercent", 100f) / 100f);
        _session.Doc.Garments.Add(hood);
        return BlockReport(new BlockResult(hood, hood.Pieces.ToList(), $"Simulate '{hood.Name}' to drape it."), args, 120);
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
            Layer = OptInt(args, "layer", 0),
            BodyOffset = Math.Clamp(OptFloat(args, "bodyOffsetMm", 0f), 0f, 100f),
        };
        _session.Doc.Garments.Add(g);
        _session.Workspace.SetActiveGarment(g.Id);
        return new { garmentId = g.Id, name = g.Name };
    }

    private object SetGarment(JsonElement args)
    {
        var g = ResolveGarment(OptString(args, "garmentId"));
        if (OptString(args, "name") is { Length: > 0 } name && name != g.Name)
            g.Name = _session.Doc.UniqueName(name);
        if (args.TryGetProperty("layer", out var layer) && layer.ValueKind == JsonValueKind.Number)
            g.Layer = layer.GetInt32();
        if (TryOptFloat(args, "bodyOffsetMm") is { } offset)
            g.BodyOffset = Math.Clamp(offset, 0f, 100f);
        if (TryOptFloat(args, "thicknessMm") is { } thickness)
            g.Thickness = thickness;
        if (TryOptFloat(args, "particleDistance") is { } distance && MathF.Abs(distance - g.ParticleDistance) > 1e-4f)
        {
            g.ParticleDistance = distance;
            foreach (var p in g.Pieces)
            {
                p.ClearDrape();
                p.ClearPins();
            }
        }
        if (args.TryGetProperty("visible", out var visible) && visible.ValueKind is JsonValueKind.True or JsonValueKind.False)
            g.Visible = visible.GetBoolean();
        if (args.TryGetProperty("frozen", out var frozen) && frozen.ValueKind is JsonValueKind.True or JsonValueKind.False)
            g.Frozen = frozen.GetBoolean();
        if (args.TryGetProperty("deactivated", out var off) && off.ValueKind is JsonValueKind.True or JsonValueKind.False)
            g.Deactivated = off.GetBoolean();
        return new
        {
            garmentId = g.Id, name = g.Name, layer = g.Layer, bodyOffsetMm = g.BodyOffset, particleDistance = g.ParticleDistance,
            thicknessMm = g.Thickness, visible = g.Visible, frozen = g.Frozen, deactivated = g.Deactivated,
        };
    }

    private object SetQuality(JsonElement args)
    {
        var quality = ReqString(args, "quality").Trim().ToLowerInvariant() switch
        {
            "draft" => SimulationQuality.Draft,
            "final" => SimulationQuality.Final,
            var other => throw new ArgumentException($"quality must be 'draft' or 'final', not '{other}'."),
        };
        var draftChange = TryOptFloat(args, "draftParticleDistance") is { } draft
            ? _session.Workspace.SetDraftParticleDistance(draft) : default;
        var change = _session.Workspace.SetQuality(quality);
        var doc = _session.Doc;
        return new
        {
            quality = doc.Simulation.Quality.ToString().ToLowerInvariant(),
            draftParticleDistance = doc.Simulation.DraftParticleDistance,
            remeshedPieces = Math.Max(change.Pieces, draftChange.Pieces),
            drapesCarried = Math.Max(change.Draped, draftChange.Draped),
            pinsCarried = Math.Max(change.Pinned, draftChange.Pinned),
            creasesCarried = Math.Max(change.Creased, draftChange.Creased),
            particles = doc.Garments.Where(g => g.Pieces.Count > 0).Sum(g => _session.Workspace.MeshFor(g).VertexCount),
            note = change.Creased > 0 && quality == SimulationQuality.Final
                ? "Creases were carried over from the coarser Draft mesh and are softer; set them again (set_creases) where fine folds matter."
                : null,
        };
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
            if (pt.TryGetProperty("edge", out var edgeEl) && edgeEl.ValueKind == JsonValueKind.String)
                p.EdgeName = edgeEl.GetString();
            points.Add(p);
        }
        if (points.Count < 3)
            throw new ArgumentException("A pattern piece needs at least 3 points.");

        // Body-space drafting: reflect for the other side of the body and for a piece seen from behind.
        string? drawnOn = OptString(args, "drawnOn")?.ToLowerInvariant();
        if (drawnOn is not (null or "front" or "back"))
            throw new ArgumentException("drawnOn must be 'front' or 'back'.");
        bool back = drawnOn == "back";
        if (drawnOn != null && (OptBool(args, "mirrorX", false) ^ back))
        {
            foreach (var p in points)
            {
                p.Position = new Vector2(-p.Position.X, p.Position.Y);
                if (p.Curve is { } c)
                    p.Curve = new Vector2(-c.X, c.Y);
            }
        }

        // Ensure counter-clockwise winding
        EnsureCcw(points);

        var piece = new PatternPiece
        {
            Name = _session.Doc.UniqueName(name),
            Points = points,
            Physics = OptString(args, "fabricPreset") is { Length: > 0 } preset
                ? FabricPhysics.FromPreset(preset)
                : new FabricPhysics(),
        };

        if (drawnOn != null)
        {
            var (lo, hi) = Outline.Bounds(piece);
            float depth = OptFloat(args, "depthCm", 25f) * 0.01f;
            // A back piece is turned round, so its pattern x runs towards the avatar's right.
            piece.Placement.Position = new Vector3((lo.X + hi.X) / 2 * (back ? -0.01f : 0.01f), (lo.Y + hi.Y) / 2 * 0.01f, back ? -depth : depth);
            piece.Placement.Rotation = new Vector3(0, back ? 180 : 0, 0);
        }
        if (args.TryGetProperty("fabric", out var fabricEl) && fabricEl.ValueKind == JsonValueKind.Object)
            ApplyFabric(piece, fabricEl);
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

        PlaceIn2D(g, piece, args);
        g.Pieces.Add(piece);
        return new
        {
            garmentId = g.Id,
            pieceId = piece.Id,
            name = piece.Name,
            edges = Enumerable.Range(0, piece.EdgeCount).Select(e => new { edge = e, name = piece.PointAt(e).EdgeName, lengthCm = MathF.Round(Outline.EdgeLength(piece, e), 2) }),
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
            Points = new List<PatternPoint>
            {
                new(0, 0) { EdgeName = "bottom" }, new(w, 0) { EdgeName = "right" }, new(w, h) { EdgeName = "top" }, new(0, h) { EdgeName = "left" }
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
        if (TryOptFloat(args, "x") is { } px && TryOptFloat(args, "y") is { } py && TryOptFloat(args, "z") is { } pz)
            piece.Placement.Position = new Vector3(px, py, pz);
        piece.Placement.Rotation = new Vector3(0, OptFloat(args, "ry", 0f), 0);
        piece.Placement.Curvature = OptFloat(args, "curvature", 0f);
        PlaceIn2D(g, piece, args);
        g.Pieces.Add(piece);
        return new { garmentId = g.Id, pieceId = piece.Id, name = piece.Name, edges = new[] { "0:bottom", "1:right", "2:top", "3:left" } };
    }

    /// <summary>Uses the 2D origin that was asked for, or finds the piece a free place in the pattern window.</summary>
    private void PlaceIn2D(Garment garment, PatternPiece piece, JsonElement args)
    {
        if (TryOptFloat(args, "originX") is { } ox && TryOptFloat(args, "originY") is { } oy)
            piece.Origin = new Vector2(ox, oy);
        else
            PatternLayout.Place(_session.Doc, garment, piece);
    }

    private static List<Vector2>? Points2(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array || el.GetArrayLength() < 3)
            return null;
        return el.EnumerateArray().Select(p => new Vector2(p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle())).ToList();
    }

    private object BlockReport(BlockResult block, JsonElement args, int defaultFrames = 60)
    {
        object? sim = null;
        if (OptBool(args, "simulate", true))
            sim = _session.RunSimulation(block.Garment.Id, OptInt(args, "frames", defaultFrames), false, true, false, 12);
        return new
        {
            garmentId = block.Garment.Id,
            garment = block.Garment.Name,
            layer = block.Garment.Layer,
            bodyOffsetMm = MathF.Round(block.Garment.BodyOffset, 1),
            pieces = block.Pieces.Select(p => new
            {
                pieceId = p.Id,
                name = p.Name,
                edges = Enumerable.Range(0, p.EdgeCount).Select(e => new { edge = e, name = p.PointAt(e).EdgeName, lengthCm = MathF.Round(Outline.EdgeLength(p, e), 1) }),
            }),
            note = sim == null ? block.Note : null,
            simulation = sim,
        };
    }

    private object AddPatch(JsonElement args)
    {
        string hostId = ReqString(args, "hostPieceId");
        var host = _session.Doc.FindPiece(hostId) ?? throw new ArgumentException($"Piece '{hostId}' not found.");
        string kind = OptString(args, "kind") ?? "patch";
        bool flap = kind.Equals("flap", StringComparison.OrdinalIgnoreCase);
        string name = OptString(args, "name") ?? $"{host.Name} {kind}";
        int level = OptInt(args, "level", flap ? 2 : 1);
        BlockResult block;
        if (args.TryGetProperty("acrossEdge", out _))
        {
            int edge = ResolveEdge(host, args, "acrossEdge");
            float ease = 1f + OptFloat(args, "easePercent", flap ? 6f : 12f) / 100f;
            block = GarmentBlocks.AddSeamPatch(_session.Doc, _session.Workspace.MeshFor, hostId, edge, ReqFloat(args, "fromCm"),
                ReqFloat(args, "toCm"), ReqFloat(args, "widthCm"), kind, name, ease, level, garmentName: OptString(args, "garmentName"));
        }
        else
        {
            var footprint = Points2(args, "points");
            if (footprint == null)
            {
                float x = ReqFloat(args, "x"), y = ReqFloat(args, "y"), w = ReqFloat(args, "widthCm"), h = ReqFloat(args, "heightCm");
                footprint = new List<Vector2> { new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h) };
            }
            float ease = 1f + OptFloat(args, "easePercent", flap ? 0f : 4f) / 100f;
            List<int>? only = args.TryGetProperty("stitchEdges", out var se) && se.ValueKind == JsonValueKind.Array
                ? se.EnumerateArray().Select(e => e.GetInt32()).ToList() : null;
            block = GarmentBlocks.AddPatch(_session.Doc, _session.Workspace.MeshFor, hostId, footprint, only != null ? "free" : kind, name,
                new Vector2(ease, kind.Equals("patch", StringComparison.OrdinalIgnoreCase) ? ease : 1f), level, only,
                garmentName: OptString(args, "garmentName"));
        }
        var piece = block.Pieces[0];
        if (TryOptFloat(args, "bend") is { } bend)
            piece.Physics.Bend = bend;
        if (OptString(args, "color") is { Length: > 0 } hex)
        {
            var mat = new FabricMaterial { Name = _session.Doc.UniqueName($"{piece.Name} fabric"), Color = hex };
            _session.Doc.Materials.Add(mat);
            piece.MaterialId = mat.Id;
        }
        return BlockReport(block, args);
    }

    private object AddTube(JsonElement args)
    {
        string name = ReqString(args, "name");
        var targets = new List<(PatternPiece Piece, int Edge)>();
        if (args.TryGetProperty("sewStartTo", out var sew) && sew.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in sew.EnumerateArray())
            {
                string id = t.GetProperty("pieceId").GetString() ?? "";
                var p = _session.Doc.FindPiece(id) ?? throw new ArgumentException($"Piece '{id}' not found.");
                targets.Add((p, ResolveEdge(p, t, "edge")));
            }
        }
        if (targets.Count > 2)
            throw new ArgumentException("sewStartTo takes one edge (cuff, band) or two (front and back armhole).");
        var garment = targets.Count > 0 ? _session.Doc.GarmentOfPiece(targets[0].Piece.Id)! : ResolveGarment(OptString(args, "garmentId"));
        PatternPiece? like = OptString(args, "likePieceId") is { } likeId ? _session.Doc.FindPiece(likeId) : targets.FirstOrDefault().Piece;

        Vector3 start, axis;
        ArmMeasure? measured = null;
        float offsetCm = OptFloat(args, "offsetCm", 0f);
        if (OptString(args, "limb") is { Length: > 0 } limb)
        {
            var arms = BodyMeasurements.Measure(_session.Avatar.Mesh).Arms;
            string side = limb.StartsWith("left", StringComparison.OrdinalIgnoreCase) ? "left" : "right";
            var arm = arms.FirstOrDefault(a => a.Side == side) ?? throw new ArgumentException($"The avatar's {side} arm could not be measured; give start and axis instead.");
            measured = arm;
            axis = arm.Axis;
            start = arm.Shoulder + axis * (offsetCm * 0.01f);
        }
        else if (TryFloat3(args, "start", out start) && TryFloat3(args, "axis", out axis))
        {
        }
        else
        {
            throw new ArgumentException("Give limb (left_arm or right_arm), or both start and axis.");
        }

        // A tube narrower than the limb plus the room its garment keeps from the skin can never close its seam.
        string? fitWarning = null;
        if (measured != null)
        {
            float room = 2f * MathF.PI * (garment.BodyOffset + (garment.Thickness ?? like?.Physics.Thickness ?? 2.5f) + 2f) * 0.1f;
            float Girth(float alongCm)
            {
                var best = measured.Sections.MinBy(sec => MathF.Abs(Vector3.Dot(sec.Centre - measured.Shoulder, measured.Axis) * 100f - alongCm));
                return best?.CircumferenceCm ?? 0;
            }
            float endAt = offsetCm + ReqFloat(args, "lengthCm");
            float needEnd = Girth(endAt) + room, needStart = Girth(offsetCm) + room;
            float end = ReqFloat(args, "endCircCm");
            float? startGiven = TryOptFloat(args, "startCircCm");
            if (end < needEnd - 0.5f || (startGiven is { } sg && sg < needStart - 0.5f))
                fitWarning = $"Too narrow to close: the arm is {Girth(offsetCm):0.#} cm round where this tube starts and {Girth(endAt):0.#} cm where it ends, "
                    + $"and this garment keeps {room:0.#} cm of extra circumference for the layers under it. Use at least {needStart:0.#} cm at the start and {needEnd:0.#} cm at the end "
                    + "(a cuff can then grip with shrinkWeft about 0.95).";
        }

        float startCirc = TryOptFloat(args, "startCircCm") ?? (targets.Count > 0
            ? targets.Sum(t => Outline.EdgeLength(t.Piece, t.Edge))
            : throw new ArgumentException("Give startCircCm, or sewStartTo so the tube can match those edges."));
        bool split = OptBool(args, "split", targets.Count == 2);
        var piece = GarmentBlocks.AddTube(_session.Doc, garment, name, start, axis, Vector3.UnitY, startCirc,
            ReqFloat(args, "endCircCm"), ReqFloat(args, "lengthCm"), split, OptFloat(args, "capCm", 0f), like: like);
        _session.Workspace.SetActiveGarment(garment.Id);

        // Sew each target edge to the half of the start edge that is nearest to it.
        var seams = new List<object>();
        if (targets.Count > 0)
        {
            var starts = Enumerable.Range(0, piece.EdgeCount).Where(e => piece.PointAt(e).EdgeName?.StartsWith("start", StringComparison.Ordinal) == true).ToList();
            var mesh = _session.Workspace.MeshFor(garment);
            Vector3 Mid(PatternPiece p, int e)
            {
                var pm = mesh.Find(p.Id)!;
                var pos = Mesher.CurrentPositions(p, pm);
                var chain = pm.EdgeChains[e];
                return pos[chain[chain.Length / 2]];
            }
            var free = new List<int>(starts);
            foreach (var t in targets.OrderBy(t => free.Min(e => Vector3.Distance(Mid(piece, e), Mid(t.Piece, t.Edge)))))
            {
                if (free.Count == 0)
                    break;
                int nearest = free.MinBy(e => Vector3.Distance(Mid(piece, e), Mid(t.Piece, t.Edge)));
                free.Remove(nearest);
                seams.Add(Sew(piece, nearest, t.Piece, t.Edge, null));
            }
        }
        return new
        {
            garmentId = garment.Id,
            pieceId = piece.Id,
            name = piece.Name,
            startCircCm = MathF.Round(startCirc, 1),
            edges = Enumerable.Range(0, piece.EdgeCount).Select(e => new { edge = e, name = piece.PointAt(e).EdgeName, lengthCm = MathF.Round(Outline.EdgeLength(piece, e), 1) }),
            seams,
            fitWarning,
            note = "Its own seam (seam_a to seam_b) is already sewn. Simulate the garment to close everything.",
        };
    }

    private object PinPiece(JsonElement args)
    {
        string id = ReqString(args, "pieceId");
        var piece = _session.Doc.FindPiece(id) ?? throw new ArgumentException($"Piece '{id}' not found.");
        if (OptBool(args, "pinned", true))
        {
            var mesh = _session.Workspace.MeshFor(_session.Doc.GarmentOfPiece(id)!).Find(id)!;
            piece.Pins = Enumerable.Range(0, mesh.VertexCount).ToHashSet();
            piece.PinKey = mesh.Key;
        }
        else
        {
            piece.ClearPins();
        }
        return new { ok = true, pieceId = id, name = piece.Name, pinnedPoints = piece.Pins.Count };
    }

    private object PullCloth(JsonElement args)
    {
        string id = ReqString(args, "pieceId");
        var piece = _session.Doc.FindPiece(id) ?? throw new ArgumentException($"Piece '{id}' not found.");
        var garment = _session.Doc.GarmentOfPiece(id)!;
        var mesh = _session.Workspace.MeshFor(garment).Find(id)!;
        if (piece.Drape == null || piece.DrapeKey != mesh.Key)
            throw new ArgumentException($"'{piece.Name}' has not been draped yet: simulate '{garment.Name}' first, then pull it.");
        var at = new Vector2(ReqFloat(args, "x"), ReqFloat(args, "y"));
        float radius = MathF.Max(OptFloat(args, "radiusCm", 5f), 0.5f);
        var positions = Mesher.CurrentPositions(piece, mesh);
        if (!SurfaceLocator.TryLocate(mesh, positions, at, piece.FlipNormals, out _, out var normal, out float offCm) || offCm > radius)
            throw new ArgumentException($"({at.X:0.#}, {at.Y:0.#}) is not on '{piece.Name}'.");
        var pull = TryFloat3(args, "offsetCm", out var o) ? o * 0.01f : Vector3.Zero;
        pull += normal * (OptFloat(args, "outCm", 0f) * 0.01f);
        if (pull.Length() < 1e-5f)
            throw new ArgumentException("Give offsetCm or outCm: which way, and how far, to pull.");

        // The grabbed patch: full pull at the point, fading smoothly to nothing at the radius.
        var weights = new Dictionary<int, float>();
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            float d = Vector2.Distance(mesh.Points[i], at);
            if (d < radius)
            {
                float t = 1f - d / radius;
                weights[i] = t * t * (3f - 2f * t);
            }
        }
        int steps = Math.Clamp(OptInt(args, "steps", 4), 1, 20);
        int frames = Math.Clamp(OptInt(args, "framesPerStep", 20), 1, 400);
        var oldPins = new HashSet<int>(piece.Pins);
        string? oldKey = piece.PinKey;
        object? last = null;
        for (int k = 0; k < steps; k++)
        {
            var drape = (float[])piece.Drape!.Clone();       // drapes are replaced, never changed in place (undo shares them)
            foreach (var (i, w) in weights)
            {
                drape[i * 3] += pull.X * w / steps;
                drape[i * 3 + 1] += pull.Y * w / steps;
                drape[i * 3 + 2] += pull.Z * w / steps;
            }
            piece.Drape = drape;
            piece.Pins = new HashSet<int>(oldKey == mesh.Key ? oldPins.Concat(weights.Keys) : weights.Keys);
            piece.PinKey = mesh.Key;
            last = _session.RunSimulation(garment.Id, frames, false, true, false, 12);
        }
        float creasePercent = OptFloat(args, "creasePercent", 0f);
        int creased = creasePercent > 0
            ? Creases.Set(garment.Pieces, p => _session.Workspace.MeshFor(garment).Find(p.Id), creasePercent / 100f)
            : 0;
        bool hold = OptBool(args, "hold", false);
        if (!hold)
        {
            piece.Pins = oldKey == mesh.Key ? oldPins : new HashSet<int>();
            piece.PinKey = oldKey == mesh.Key ? oldKey : null;
            int settle = Math.Clamp(OptInt(args, "settleFrames", 40), 0, 600);
            if (settle > 0)
                last = _session.RunSimulation(garment.Id, settle, false, true, false, 12);
        }
        return new { ok = true, pieceId = id, name = piece.Name, grabbedPoints = weights.Count, pulledCm = MathF.Round(pull.Length() * 100f, 1), held = hold, creasedPieces = creased, simulation = last };
    }

    private object SetCreases(JsonElement args)
    {
        var pieces = new List<(Garment Garment, PatternPiece Piece)>();
        if (OptString(args, "pieceId") is { } pid)
        {
            var piece = _session.Doc.FindPiece(pid) ?? throw new ArgumentException($"Piece '{pid}' not found.");
            pieces.Add((_session.Doc.GarmentOfPiece(pid)!, piece));
        }
        else
        {
            var garment = ResolveGarment(OptString(args, "garmentId"));
            pieces.AddRange(garment.Pieces.Select(p => (garment, p)));
        }
        if (OptBool(args, "clear", false))
            return new { ok = true, cleared = Creases.Clear(pieces.Select(x => x.Piece)) };
        float strength = Math.Clamp(OptFloat(args, "strengthPercent", Creases.DefaultStrength * 100f), 0f, 100f) / 100f;
        int creased = 0;
        foreach (var group in pieces.GroupBy(x => x.Garment))
            creased += Creases.Set(group.Select(x => x.Piece), p => _session.Workspace.MeshFor(group.Key).Find(p.Id), strength);
        int skipped = pieces.Count - creased;
        return new
        {
            ok = true, creasedPieces = creased, strengthPercent = MathF.Round(strength * 100f),
            note = skipped > 0 ? $"{skipped} piece(s) have not been draped, so they have no folds to keep: simulate first." : null,
        };
    }

    private object AddHardware(JsonElement args)
    {
        string hostId = ReqString(args, "hostPieceId");
        var host = _session.Doc.FindPiece(hostId) ?? throw new ArgumentException($"Piece '{hostId}' not found.");
        string shape = OptString(args, "shape") ?? "snap";
        bool rolled = shape.ToLowerInvariant() is "pen" or "handle" or "cylinder";
        float width = OptFloat(args, "widthCm", rolled ? 0.8f : 1.7f);
        float height = OptFloat(args, "heightCm", rolled ? 9f : shape.Equals("wrench", StringComparison.OrdinalIgnoreCase) ? 20f : 2.5f);
        const float rollArc = 330f;
        // A rolled part is drawn as the unrolled sheet: its width is the circumference of the cylinder.
        var outline = Points2(args, "points") ?? GarmentBlocks.HardwareShape(shape, rolled ? width * MathF.PI * rollArc / 360f : width, height);
        float thickness = OptFloat(args, "thicknessMm", 2.2f);
        string garmentName = OptString(args, "garmentName") ?? "Hardware";
        var piece = GarmentBlocks.AddHardware(_session.Doc, _session.Workspace.MeshFor, hostId,
            new Vector2(ReqFloat(args, "x"), ReqFloat(args, "y")), outline, OptString(args, "name") ?? $"{shape} on {host.Name}",
            garmentName, thickness, OptFloat(args, "liftMm", thickness * 0.5f + 1.5f), OptFloat(args, "spinDegrees", 0f),
            rolled ? rollArc : 0f,
            // Flat parts and pens lie along the cloth they sit on; tools hanging at the hip stand upright.
            followTilt: shape.ToLowerInvariant() is not ("handle" or "cylinder" or "wrench"));
        string color = OptString(args, "color") ?? "#9A7A45";
        float metallic = OptFloat(args, "metallic", 0.9f), roughness = OptFloat(args, "roughness", 0.3f);
        var mat = GarmentBlocks.HardMaterial(_session.Doc, color, metallic, roughness);
        piece.MaterialId = mat.Id;
        var garment = _session.Doc.GarmentOfPiece(piece.Id)!;
        return new { garmentId = garment.Id, garment = garment.Name, pieceId = piece.Id, name = piece.Name, pinned = piece.Pins.Count, materialId = mat.Id };
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
            mp.EdgeName = piece.Points[prev].EdgeName;
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
        var insetPiece = _session.Doc.FindPiece(pieceId) ?? throw new ArgumentException($"Piece '{pieceId}' not found.");
        int edge = ResolveEdge(insetPiece, args, "edgeIndex");
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
        int stitches = 0;
        foreach (var other in _session.Doc.Garments)
            stitches += other.SurfaceStitches.RemoveAll(s => s.SourcePieceId == pieceId || s.TargetPieceId == pieceId);
        return new { ok = true, deletedPieceId = pieceId, removedSurfaceStitches = stitches };
    }

    private object SewEdges(JsonElement args)
    {
        string pA = ReqString(args, "pieceAId");
        string pB = ReqString(args, "pieceBId");
        var pieceA = _session.Doc.FindPiece(pA) ?? throw new ArgumentException($"Piece '{pA}' not found.");
        var pieceB = _session.Doc.FindPiece(pB) ?? throw new ArgumentException($"Piece '{pB}' not found.");
        int eA = ResolveEdge(pieceA, args, "edgeA");
        int eB = ResolveEdge(pieceB, args, "edgeB");
        bool? forced = args.TryGetProperty("reversed", out var rv) && rv.ValueKind is JsonValueKind.True or JsonValueKind.False ? rv.GetBoolean() : null;
        return Sew(pieceA, eA, pieceB, eB, forced);
    }

    private object Sew(PatternPiece pieceA, int eA, PatternPiece pieceB, int eB, bool? forced)
    {
        var gA = _session.Doc.GarmentOfPiece(pieceA.Id)!;
        var gB = _session.Doc.GarmentOfPiece(pieceB.Id)!;
        if (gA.Id != gB.Id)
            throw new ArgumentException("Both pieces must belong to the same garment to be sewn together. To sew a piece onto another garment, use sew_surface_stitch or add_patch.");
        if (pieceA.Id == pieceB.Id && eA == eB)
            throw new ArgumentException("An edge cannot be sewn to itself.");
        if (gA.Seams.Any(x => (x.A.Matches(pieceA.Id, eA) && x.B.Matches(pieceB.Id, eB)) || (x.A.Matches(pieceB.Id, eB) && x.B.Matches(pieceA.Id, eA))))
            throw new ArgumentException($"'{pieceA.Name}' edge {eA} is already sewn to '{pieceB.Name}' edge {eB}.");

        bool reversed = forced ?? UntwistedIsReversed(pieceA, eA, pieceB, eB);
        var seam = new Seam { A = new EdgeRef(pieceA.Id, eA), B = new EdgeRef(pieceB.Id, eB), Reversed = reversed };
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
            direction = forced == null ? "auto" : "as given",
            warning = mismatchPct > 12f ? "These edges differ a lot in length: the longer one will gather." : null,
        };
    }

    private object SewNamed(JsonElement args)
    {
        string pA = ReqString(args, "pieceAId");
        string pB = ReqString(args, "pieceBId");
        var pieceA = _session.Doc.FindPiece(pA) ?? throw new ArgumentException($"Piece '{pA}' not found.");
        var pieceB = _session.Doc.FindPiece(pB) ?? throw new ArgumentException($"Piece '{pB}' not found.");
        if (!args.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array || names.GetArrayLength() == 0)
            throw new ArgumentException("Give the edge names to sew in 'names'.");
        var sewn = new List<object>();
        foreach (var n in names.EnumerateArray())
        {
            string name = n.GetString() ?? "";
            int eA = pieceA.EdgeNamed(name), eB = pieceB.EdgeNamed(name);
            if (eA < 0 || eB < 0)
                throw new ArgumentException($"Edge '{name}' is not on {(eA < 0 ? pieceA.Name : pieceB.Name)}.");
            sewn.Add(Sew(pieceA, eA, pieceB, eB, null));
        }
        return new { sewn = sewn.Count, seams = sewn };
    }

    private object AutoSew(JsonElement args)
    {
        var g = ResolveGarment(OptString(args, "garmentId"));
        float tolerance = OptFloat(args, "toleranceCm", 2f) * 0.01f;
        bool dryRun = OptBool(args, "dryRun", false);
        var mesh = _session.Workspace.MeshFor(g);
        var edges = new List<(PatternPiece Piece, int Edge, Vector3 Start, Vector3 End, float Length)>();
        foreach (var pm in mesh.Pieces)
        {
            var piece = g.FindPiece(pm.PieceId)!;
            var positions = Mesher.CurrentPositions(piece, pm);
            for (int e = 0; e < pm.EdgeChains.Length; e++)
            {
                var chain = pm.EdgeChains[e];
                if (chain.Length < 2 || g.Seams.Any(x => x.A.Matches(piece.Id, e) || x.B.Matches(piece.Id, e)))
                    continue;
                edges.Add((piece, e, positions[chain[0]], positions[chain[^1]], Outline.EdgeLength(piece, e)));
            }
        }
        var used = new HashSet<int>();
        var found = new List<object>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (used.Contains(i))
                continue;
            int best = -1;
            float bestCost = tolerance * 2;
            for (int j = i + 1; j < edges.Count; j++)
            {
                if (used.Contains(j) || (edges[i].Piece.Id == edges[j].Piece.Id && edges[i].Edge == edges[j].Edge))
                    continue;
                if (MathF.Abs(edges[i].Length - edges[j].Length) > 0.12f * MathF.Max(edges[i].Length, edges[j].Length))
                    continue;
                float natural = MathF.Max(Vector3.Distance(edges[i].Start, edges[j].End), Vector3.Distance(edges[i].End, edges[j].Start));
                float reversed = MathF.Max(Vector3.Distance(edges[i].Start, edges[j].Start), Vector3.Distance(edges[i].End, edges[j].End));
                float cost = MathF.Min(natural, reversed);
                if (cost <= tolerance && cost < bestCost)
                {
                    bestCost = cost;
                    best = j;
                }
            }
            if (best < 0)
                continue;
            used.Add(i);
            used.Add(best);
            found.Add(dryRun
                ? new { pieceA = edges[i].Piece.Name, edgeA = edges[i].Edge, pieceB = edges[best].Piece.Name, edgeB = edges[best].Edge, apartCm = MathF.Round(bestCost * 100f, 1) }
                : Sew(edges[i].Piece, edges[i].Edge, edges[best].Piece, edges[best].Edge, null));
        }
        return new { dryRun, count = found.Count, seams = found };
    }

    private object SewSurfaceStitch(JsonElement args)
    {
        string srcId = ReqString(args, "sourcePieceId");
        string dstId = ReqString(args, "targetPieceId");
        var start = new Vector2(ReqFloat(args, "x1"), ReqFloat(args, "y1"));
        var end = new Vector2(ReqFloat(args, "x2"), ReqFloat(args, "y2"));

        var srcPiece = _session.Doc.FindPiece(srcId) ?? throw new ArgumentException($"Piece '{srcId}' not found.");
        var dstPiece = _session.Doc.FindPiece(dstId) ?? throw new ArgumentException($"Piece '{dstId}' not found.");
        var srcGarment = _session.Doc.GarmentOfPiece(srcId)!;
        int srcEdge = ResolveEdge(srcPiece, args, "sourceEdge");

        var ss = new SurfaceStitch
        {
            SourcePieceId = srcId,
            SourceEdge = srcEdge,
            TargetPieceId = dstId,
            TargetStart = start,
            TargetEnd = end,
        };
        srcGarment.SurfaceStitches.Add(ss);
        return new
        {
            stitchId = ss.Id,
            sourcePiece = srcPiece.Name,
            sourceEdge = srcEdge,
            targetPiece = dstPiece.Name,
            targetLengthCm = MathF.Round(Vector2.Distance(start, end), 2),
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
            ApplyFabric(p, args);
        return new { ok = true, updatedPieces = targets.Count };
    }

    private static void ApplyFabric(PatternPiece p, JsonElement args)
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
        if (TryOptFloat(args, "shrinkWeft") is { } sw) p.Physics.ShrinkWeft = sw;
        if (TryOptFloat(args, "shrinkWarp") is { } sk) p.Physics.ShrinkWarp = sk;
    }

    private object SetMaterial(JsonElement args)
    {
        string? texture = OptString(args, "texture") is { Length: > 0 } tex ? Path.GetFullPath(tex) : null;
        if (texture != null && !File.Exists(texture))
            throw new ArgumentException($"Texture file not found: {texture}");
        // The colour multiplies the texture: white shows the texture as it is.
        string color = OptString(args, "color") ?? (texture != null ? "#FFFFFF" : "#C8CED8");
        string name = OptString(args, "name") ?? "Fabric";
        var mat = new FabricMaterial { Name = _session.Doc.UniqueName(name), Color = color, Texture = texture };
        if (TryOptFloat(args, "tileCm") is { } tile)
            mat.TileSizeCm = MathF.Max(0, tile);
        if (TryOptFloat(args, "repeat") is { } rep)
            mat.Repeat = rep;
        if (TryOptFloat(args, "roughness") is { } rough)
            mat.Roughness = rough;
        if (TryOptFloat(args, "metallic") is { } metal)
            mat.Metallic = metal;
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
        string? tintNote = texture != null && !color.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase)
            ? "The colour tints the texture (they are multiplied). Leave colour out to show the texture as it is." : null;
        return new { ok = true, materialId = mat.Id, color, tileCm = mat.TileSizeCm, updatedPieces = updated, note = tintNote };
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
        {
            var views = new List<PreviewView>();
            if (TryOptFloat(args, "yaw") is { } yaw)
                views.Add(new PreviewView("custom", yaw, OptFloat(args, "pitch", 5f)));
            else
            {
                foreach (string name in (OptString(args, "views") ?? "front,side,back").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    views.Add(PreviewRenderer.Named.TryGetValue(name, out var view) ? view
                        : throw new ArgumentException($"Unknown view '{name}'. Use: {string.Join(", ", PreviewRenderer.Named.Keys)}."));
                }
            }
            Vector3? focus = TryFloat3(args, "focus", out var f) ? f : null;
            float? span = TryOptFloat(args, "spanCm") is { } cm ? cm * 0.01f : null;
            if (OptString(args, "focusPieceId") is { Length: > 0 } focusId)
            {
                var piece = _session.Doc.FindPiece(focusId) ?? throw new ArgumentException($"Piece '{focusId}' not found.");
                var mesh = _session.Workspace.MeshFor(_session.Doc.GarmentOfPiece(focusId)!).Find(focusId)!;
                var positions = Mesher.CurrentPositions(piece, mesh);
                var lo = positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                var hi = positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                focus = (lo + hi) / 2;
                span ??= MathF.Max((hi - lo).Length() * 1.5f, 0.2f);
            }
            _session.RenderDrapePreviewPng(png, OptInt(args, "width", 960), OptInt(args, "height", 480), views, focus, span,
                OptBool(args, "showAvatar", true));
        }
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
        bool rigged = OptBool(args, "rigged", false);
        if (rigged)
        {
            if (ext == ".obj")
                throw new ArgumentException("OBJ cannot hold a skeleton. Use .glb, .gltf or .fbx for a rigged export.");
            Seamstress.Core.Rigging.RiggedExport.Bind(scene, _session.Avatar, OptBool(args, "includeAvatar", false));
        }
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
            joints = scene.Skeleton?.Count ?? 0,
        };
    }

    private object ImportMeshAsPattern(JsonElement args)
    {
        string path = ReqString(args, "path");
        var options = new MeshImportOptions
        {
            Scale = OptFloat(args, "scale", 1f),
            ZUpToYUp = OptBool(args, "zUpToYUp", false),
            UseUvs = OptBool(args, "useUvs", true),
            SplitByGroups = OptBool(args, "splitByGroups", true),
            AutoSew = OptBool(args, "autoSew", true),
            SimplifyToleranceCm = OptFloat(args, "simplifyToleranceCm", 0.6f),
            ParticleDistanceCm = OptFloat(args, "particleDistanceCm", 2.0f),
            GarmentName = OptString(args, "garmentName"),
        };
        var result = MeshToPatternImporter.ImportFromFile(path, _session.Doc, options);
        _session.Doc.Garments.Add(result.Garment);
        _session.Workspace.SetActiveGarment(result.Garment.Id);
        return new
        {
            garmentId = result.Garment.Id,
            name = result.Garment.Name,
            sourceVertices = result.SourceVertexCount,
            sourceTriangles = result.SourceTriangleCount,
            extractedPieces = result.ExtractedPieceCount,
            autoSeams = result.AutoSeamCount,
            usedUvIslands = result.UsedUvIslands,
            warnings = result.Warnings,
            pieces = result.Garment.Pieces.Select(p => new { id = p.Id, name = p.Name, edges = p.EdgeCount }),
        };
    }

    private object ListBodyProfiles()
    {
        var profiles = _session.BodyLibrary.ListProfiles();
        return new
        {
            folder = BodyLibrary.DefaultFolder,
            activeAvatar = _session.ActiveAvatar?.Name,
            profiles = profiles.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                sourceType = p.SourceType,
                baseMeshPath = p.BaseMeshPath,
                heightScale = p.HeightScale,
                widthScale = p.WidthScale,
                depthScale = p.DepthScale,
                description = p.Description,
            }),
        };
    }

    private object SaveBodyProfile(JsonElement args)
    {
        var profile = new BodyProfile
        {
            Name = ReqString(args, "name"),
            Description = OptString(args, "description") ?? "",
            SourceType = OptString(args, "sourceType") ?? "Mannequin",
            BaseMeshPath = OptString(args, "baseMeshPath"),
            Scale = OptFloat(args, "scale", 1f),
            ZUpToYUp = OptBool(args, "zUpToYUp", false),
            HeightScale = OptFloat(args, "heightScale", 1f),
            WidthScale = OptFloat(args, "widthScale", 1f),
            DepthScale = OptFloat(args, "depthScale", 1f),
        };
        _session.BodyLibrary.SaveProfile(profile);
        return new { ok = true, id = profile.Id, name = profile.Name, sourceType = profile.SourceType };
    }

    private object LoadBodyProfile(JsonElement args)
    {
        BodyProfile profile;
        if (OptString(args, "profile") is { Length: > 0 } key)
        {
            profile = _session.BodyLibrary.LoadProfile(key)
                ?? throw new ArgumentException($"Body profile '{key}' not found.");
        }
        else
        {
            profile = new BodyProfile
            {
                Name = "Parametric Mannequin",
                SourceType = "Mannequin",
                HeightScale = OptFloat(args, "heightScale", 1f),
                WidthScale = OptFloat(args, "widthScale", 1f),
                DepthScale = OptFloat(args, "depthScale", 1f),
            };
        }
        var avatar = _session.BodyLibrary.BuildAvatar(profile);
        _session.SetAvatar(avatar);
        if (!string.IsNullOrWhiteSpace(profile.BaseMeshPath) && File.Exists(profile.BaseMeshPath))
        {
            _session.Doc.Avatar.Kind = AvatarKind.CustomObj;
            _session.Doc.Avatar.Path = Path.GetFullPath(profile.BaseMeshPath);
            _session.Doc.Avatar.Scale = profile.Scale;
        }
        return new
        {
            ok = true,
            avatarName = avatar.Name,
            vertexCount = avatar.Mesh.Positions.Length,
            triangleCount = avatar.Mesh.Triangles.Length / 3,
            landmarks = _session.GetAvatarLandmarks(),
        };
    }

    private object RefitGarmentToBody(JsonElement args)
    {
        var garment = ResolveGarment(OptString(args, "garmentId"));
        var sourceBody = _session.Avatar;

        BodyProfile targetProfile;
        if (OptString(args, "targetProfile") is { Length: > 0 } key)
        {
            targetProfile = _session.BodyLibrary.LoadProfile(key)
                ?? throw new ArgumentException($"Target body profile '{key}' not found.");
        }
        else
        {
            targetProfile = new BodyProfile
            {
                Name = "Target Parametric Mannequin",
                SourceType = "Mannequin",
                HeightScale = OptFloat(args, "targetHeightScale", 1f),
                WidthScale = OptFloat(args, "targetWidthScale", 1f),
                DepthScale = OptFloat(args, "targetDepthScale", 1f),
            };
        }
        var targetBody = _session.BodyLibrary.BuildAvatar(targetProfile);
        var options = new RefitOptions
        {
            Grade2DPatterns = OptBool(args, "grade2DPatterns", true),
            RunMorphDrape = OptBool(args, "runMorphDrape", true),
            MorphStages = OptInt(args, "morphStages", 3),
            SettleFrames = OptInt(args, "settleFrames", 30),
        };
        var result = GarmentRefitter.RefitGarment(_session.Doc, _session.Workspace, garment, sourceBody, targetBody, options);
        _session.SetAvatar(targetBody);
        return new
        {
            ok = true,
            garmentId = garment.Id,
            targetAvatar = targetBody.Name,
            transferredVertices = result.TransferredVertices,
            gradedPieces = result.GradedPieces,
            simulatedFrames = result.SimulatedFrames,
            computeBackend = result.ComputeBackend,
            averageScale = new[] { MathF.Round(result.AverageScaleX, 3), MathF.Round(result.AverageScaleY, 3), MathF.Round(result.AverageScaleZ, 3) },
            penetratedVerticesAfter = result.PenetratedVerticesAfter,
            maxPenetrationMmAfter = MathF.Round(result.MaxPenetrationMmAfter, 2),
        };
    }

    private object StepPose(JsonElement args)
    {
        string objPath = Path.GetFullPath(ReqString(args, "targetObjPath"));
        int frames = OptInt(args, "frames", 12);
        int substeps = OptInt(args, "substeps", 14);
        bool carryCloth = OptBool(args, "carryCloth", true);
        bool allGarments = OptBool(args, "allGarments", false);
        bool selfCollision = OptBool(args, "selfCollision", false);
        bool useGpu = OptBool(args, "useGpu", true);

        var prevAvatar = _session.Avatar;
        var nextAvatar = Avatar.FromObj(objPath, 1f);

        int carriedVerts = 0;
        if (carryCloth
            && prevAvatar.Mesh.Positions.Length == nextAvatar.Mesh.Positions.Length
            && prevAvatar.Mesh.Triangles.Length == nextAvatar.Mesh.Triangles.Length)
        {
            foreach (var g in _session.Doc.Garments)
            {
                if (!g.Visible || g.Pieces.Count == 0) continue;
                var gm = _session.Workspace.MeshFor(g);
                foreach (var pm in gm.Pieces)
                {
                    var piece = g.FindPiece(pm.PieceId);
                    if (piece?.Drape == null || piece.Drape.Length != pm.VertexCount * 3) continue;
                    var cur = new Vector3[pm.VertexCount];
                    for (int i = 0; i < pm.VertexCount; i++)
                        cur[i] = new Vector3(piece.Drape[i * 3], piece.Drape[i * 3 + 1], piece.Drape[i * 3 + 2]);
                    var next = GarmentRefitter.BindAndTransfer(cur, prevAvatar.Mesh, nextAvatar.Mesh);
                    var flat = new float[next.Length * 3];
                    for (int i = 0; i < next.Length; i++)
                    {
                        flat[i * 3] = next[i].X;
                        flat[i * 3 + 1] = next[i].Y;
                        flat[i * 3 + 2] = next[i].Z;
                    }
                    piece.Drape = flat;
                    piece.DrapeKey = pm.Key;
                    carriedVerts += next.Length;
                }
            }
        }

        _session.SetAvatar(nextAvatar);
        _session.Doc.Avatar.Kind = AvatarKind.CustomObj;
        _session.Doc.Avatar.Path = objPath;
        _session.Doc.Avatar.Scale = 1f;

        if (frames > 0)
        {
            if (allGarments)
            {
                _session.RunSimulation(null, frames, true, useGpu, selfCollision, substeps);
            }
            else
            {
                foreach (var g in _session.Doc.Garments.Where(g => g.Visible && g.Pieces.Count > 0).OrderBy(g => g.Layer))
                    _session.RunSimulation(g.Id, frames, false, useGpu, selfCollision, substeps);
            }
        }

        return new
        {
            ok = true,
            avatarPath = objPath,
            carriedVertices = carriedVerts,
            frames,
            fit = _session.InspectFit(null),
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
                var p = new PatternPoint(pts[src].Position.X, pts[src].Position.Y) { Curve = pts[prev].Curve, EdgeName = pts[prev].EdgeName };
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

    private static JsonObject Prop(string type, string desc) => type == "edge"
        ? new() { ["type"] = new JsonArray("integer", "string"), ["description"] = desc }
        : new() { ["type"] = type, ["description"] = desc };

    /// <summary>An edge given as its 0-based number or as the name it was given when the piece was drafted.</summary>
    private static int ResolveEdge(PatternPiece piece, JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var v))
            throw new ArgumentException($"Missing required edge '{key}'.");
        int edge = -1;
        if (v.ValueKind == JsonValueKind.Number)
            edge = v.GetInt32();
        else if (v.ValueKind == JsonValueKind.String)
        {
            string name = v.GetString() ?? "";
            edge = piece.EdgeNamed(name);
            if (edge < 0 && !int.TryParse(name, out edge))
            {
                var names = piece.AllPoints().Select(p => p.EdgeName).Where(n => !string.IsNullOrEmpty(n)).ToList();
                throw new ArgumentException(names.Count > 0
                    ? $"Piece '{piece.Name}' has no edge named '{name}'. Its edges are: {string.Join(", ", names)}."
                    : $"Piece '{piece.Name}' has no named edges; give {key} as a number (0..{piece.EdgeCount - 1}).");
            }
        }
        if (edge < 0 || edge >= piece.EdgeCount)
            throw new ArgumentException($"{key} {edge} is out of range for '{piece.Name}' (0..{piece.EdgeCount - 1}).");
        return edge;
    }

    /// <summary>
    /// Whether sewing these two edges start-to-start (rather than start-to-end) is the way that does not twist,
    /// judged from where the ends of the edges are now, arranged or draped.
    /// </summary>
    private bool UntwistedIsReversed(PatternPiece a, int edgeA, PatternPiece b, int edgeB)
    {
        (Vector3 Start, Vector3 End)? Ends(PatternPiece piece, int edge)
        {
            var garment = _session.Doc.GarmentOfPiece(piece.Id);
            var mesh = garment == null ? null : _session.Workspace.MeshFor(garment).Find(piece.Id);
            if (mesh == null || edge >= mesh.EdgeChains.Length || mesh.EdgeChains[edge].Length < 2)
                return null;
            var positions = Mesher.CurrentPositions(piece, mesh);
            var chain = mesh.EdgeChains[edge];
            return (positions[chain[0]], positions[chain[^1]]);
        }
        if (Ends(a, edgeA) is not { } ea || Ends(b, edgeB) is not { } eb)
            return false;
        float natural = Vector3.Distance(ea.Start, eb.End) + Vector3.Distance(ea.End, eb.Start);
        float reversed = Vector3.Distance(ea.Start, eb.Start) + Vector3.Distance(ea.End, eb.End);
        return reversed < natural * 0.9f;
    }
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
