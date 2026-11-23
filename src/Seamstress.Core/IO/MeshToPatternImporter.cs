using System.Globalization;
using System.Numerics;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.IO;

/// <summary>Options controlling how a 3D garment mesh (.obj) is converted into 2D pattern pieces and seams.</summary>
public sealed class MeshImportOptions
{
    /// <summary>Multiplier converting OBJ coordinates to metres (e.g. 0.01 for cm-scale ZBrush/Maya/Unreal exports, 1 for metres).</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Converts Z-up coordinates (x, y, z) to Seamstress Y-up (x, z, -y).</summary>
    public bool ZUpToYUp { get; set; } = false;

    /// <summary>When UVs exist, extract 2D panels from UV islands; otherwise flatten 3D panels via PCA + relaxation.</summary>
    public bool UseUvs { get; set; } = true;

    /// <summary>Split panels across different OBJ group/Polygroup names even if their UVs touch.</summary>
    public bool SplitByGroups { get; set; } = true;

    /// <summary>Douglas-Peucker boundary simplification tolerance in centimetres.</summary>
    public float SimplifyToleranceCm { get; set; } = 0.6f;

    /// <summary>Minimum turning angle in degrees at which a boundary vertex is preserved as a sharp corner.</summary>
    public float CornerAngleDeg { get; set; } = 35f;

    /// <summary>Automatically detect welded 3D boundary edges shared between panels and create seams.</summary>
    public bool AutoSew { get; set; } = true;

    /// <summary>Simulation mesh particle distance (cm) assigned to the imported garment.</summary>
    public float ParticleDistanceCm { get; set; } = 2.0f;

    /// <summary>Optional explicit name for the imported garment; defaults to the file or provided name.</summary>
    public string? GarmentName { get; set; } = null;
}

/// <summary>Result of converting a 3D clothing blockout OBJ into an editable Seamstress garment.</summary>
public sealed class MeshImportResult
{
    public required Garment Garment { get; init; }
    public int SourceVertexCount { get; init; }
    public int SourceTriangleCount { get; init; }
    public int ExtractedPieceCount { get; init; }
    public int AutoSeamCount { get; init; }
    public bool UsedUvIslands { get; init; }
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// Converts a 3D clothing blockout OBJ (e.g. sculpted in ZBrush with Polygroups and/or UV Master UVs, or exported from
/// Blender/Maya) into editable 2D Seamstress pattern pieces with auto-detected seams and 3D placement.
/// </summary>
public static class MeshToPatternImporter
{
    private const float WeldToleranceMetres = 1e-3f;
    private const float UvMatchEpsilonSq = 1e-10f;
    private const float LayoutPaddingCm = 12f;

    /// <summary>
    /// Reads an OBJ file and extracts a <see cref="Garment"/> sized and named against <paramref name="document"/>.
    /// Does not add the garment to <see cref="Document.Garments"/> so callers can commit it through undo history.
    /// </summary>
    public static MeshImportResult ImportFromFile(string objPath, Document document, MeshImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(objPath);
        ArgumentNullException.ThrowIfNull(document);
        string text = File.ReadAllText(objPath);
        string defaultName = Path.GetFileNameWithoutExtension(objPath);
        return ImportFromText(text, defaultName, document, options);
    }

    /// <summary>
    /// Parses Wavefront OBJ text and extracts a <see cref="Garment"/> sized and named against <paramref name="document"/>.
    /// Does not add the garment to <see cref="Document.Garments"/> so callers can commit it through undo history.
    /// </summary>
    public static MeshImportResult ImportFromText(string objText, string defaultName, Document document, MeshImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(objText);
        ArgumentNullException.ThrowIfNull(document);
        var opts = options ?? new MeshImportOptions();
        if (opts.Scale <= 0 || float.IsNaN(opts.Scale) || float.IsInfinity(opts.Scale))
            throw new ArgumentOutOfRangeException(nameof(options), "Scale must be a positive finite number.");

        var warnings = new List<string>();
        var parsed = ParseObj(objText, opts, warnings);
        if (parsed.Triangles.Count == 0)
            throw new InvalidDataException("OBJ contains no valid faces.");

        var weldIds = WeldPositions(parsed.Positions, parsed.Triangles, WeldToleranceMetres);
        var panels = PartitionPanels(parsed, weldIds, opts, warnings);

        bool usedUvIslands = false;
        foreach (var panel in panels)
        {
            FlattenPanel(panel, warnings);
            if (panel.UsedUvs)
                usedUvIslands = true;
            ExtractBoundaryLoops(panel, warnings);
        }

        panels.RemoveAll(p => p.Loops.Count == 0 || p.Area3DCm2 < 1e-3);
        for (int i = 0; i < panels.Count; i++)
        {
            panels[i].Index = i;
            foreach (var loop in panels[i].Loops)
                foreach (var he in loop.HalfEdges)
                    he.PanelIndex = i;
        }

        if (opts.AutoSew)
            MatchSeamPartners(panels);

        PinCornersAndTransitions(panels, opts);
        SimplifyLoops(panels, opts);

        var garment = BuildGarment(panels, defaultName, document, opts, out int seamCount);

        return new MeshImportResult
        {
            Garment = garment,
            SourceVertexCount = parsed.Positions.Length,
            SourceTriangleCount = parsed.Triangles.Count,
            ExtractedPieceCount = garment.Pieces.Count,
            AutoSeamCount = seamCount,
            UsedUvIslands = usedUvIslands,
            Warnings = warnings,
        };
    }

    // ------------------------------------------------------------------------
    // Stage 1: Wavefront OBJ parsing
    // ------------------------------------------------------------------------

    private readonly record struct ObjTri(int V0, int V1, int V2, int Vt0, int Vt1, int Vt2, string Group);

    private sealed class ParsedObj
    {
        public required Vector3[] Positions { get; init; }
        public required Vector2[] Uvs { get; init; }
        public required List<ObjTri> Triangles { get; init; }
    }

    private static ParsedObj ParseObj(string text, MeshImportOptions opts, List<string> warnings)
    {
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<ObjTri>();
        string currentObject = "";
        string currentGroup = "";
        int skippedFaces = 0;
        int degenerateTris = 0;

        using var reader = new StringReader(text);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            int comment = rawLine.IndexOf('#');
            string line = (comment >= 0 ? rawLine[..comment] : rawLine).Trim();
            if (line.Length < 2)
                continue;

            char c0 = line[0];
            if (c0 == 'v' && char.IsWhiteSpace(line[1]))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    TryParseFloat(parts[1], out float x) &&
                    TryParseFloat(parts[2], out float y) &&
                    TryParseFloat(parts[3], out float z))
                {
                    Vector3 p = opts.ZUpToYUp ? new Vector3(x, z, -y) : new Vector3(x, y, z);
                    positions.Add(p * opts.Scale);
                }
            }
            else if (c0 == 'v' && line[1] == 't' && (line.Length == 2 || char.IsWhiteSpace(line[2])))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && TryParseFloat(parts[1], out float u))
                {
                    float v = parts.Length >= 3 && TryParseFloat(parts[2], out float parsedV) ? parsedV : 0f;
                    uvs.Add(new Vector2(u, v));
                }
            }
            else if (c0 == 'g' && (line.Length == 1 || char.IsWhiteSpace(line[1])))
            {
                currentGroup = line.Length > 1 ? line[1..].Trim() : "";
            }
            else if (c0 == 'o' && (line.Length == 1 || char.IsWhiteSpace(line[1])))
            {
                currentObject = line.Length > 1 ? line[1..].Trim() : "";
                currentGroup = "";
            }
            else if (c0 == 'f' && char.IsWhiteSpace(line[1]))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                int count = parts.Length - 1;
                if (count < 3)
                {
                    skippedFaces++;
                    continue;
                }

                var vIdx = new int[count];
                var vtIdx = new int[count];
                bool ok = true;
                for (int i = 0; i < count; i++)
                {
                    if (!ParseFaceCorner(parts[i + 1], positions.Count, uvs.Count, out vIdx[i], out vtIdx[i]))
                    {
                        ok = false;
                        break;
                    }
                }

                if (!ok)
                {
                    skippedFaces++;
                    continue;
                }

                string group = !string.IsNullOrEmpty(currentGroup) ? currentGroup : currentObject;
                for (int i = 1; i < count - 1; i++)
                {
                    int a = vIdx[0], b = vIdx[i], c = vIdx[i + 1];
                    if (a == b || b == c || c == a)
                    {
                        degenerateTris++;
                        continue;
                    }
                    var cross = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    if (cross.LengthSquared() < 1e-18f)
                    {
                        degenerateTris++;
                        continue;
                    }
                    triangles.Add(new ObjTri(a, b, c, vtIdx[0], vtIdx[i], vtIdx[i + 1], group));
                }
            }
        }

        if (skippedFaces > 0)
            warnings.Add($"Skipped {skippedFaces} malformed face(s) during OBJ parsing.");
        if (degenerateTris > 0)
            warnings.Add($"Skipped {degenerateTris} degenerate zero-area triangle(s).");

        return new ParsedObj
        {
            Positions = positions.ToArray(),
            Uvs = uvs.ToArray(),
            Triangles = triangles,
        };
    }

    private static bool TryParseFloat(string token, out float value)
        => float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
           && !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool ParseFaceCorner(string token, int posCount, int uvCount, out int v, out int vt)
    {
        v = -1;
        vt = -1;
        int firstSlash = token.IndexOf('/');
        string vPart = firstSlash >= 0 ? token[..firstSlash] : token;
        if (!int.TryParse(vPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rawV) || rawV == 0)
            return false;
        v = rawV < 0 ? posCount + rawV : rawV - 1;
        if (v < 0 || v >= posCount)
            return false;

        if (firstSlash >= 0)
        {
            int secondSlash = token.IndexOf('/', firstSlash + 1);
            string vtPart = secondSlash >= 0
                ? token[(firstSlash + 1)..secondSlash]
                : token[(firstSlash + 1)..];
            if (vtPart.Length > 0 && int.TryParse(vtPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rawVt) && rawVt != 0)
            {
                int resolved = rawVt < 0 ? uvCount + rawVt : rawVt - 1;
                if (resolved >= 0 && resolved < uvCount)
                    vt = resolved;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------------
    // Stage 2: 3D position welding (for seam detection & cross-panel topology)
    // ------------------------------------------------------------------------

    private static int[] WeldPositions(Vector3[] positions, List<ObjTri> triangles, float toleranceM)
    {
        int n = positions.Length;
        var weldId = new int[n];
        if (n == 0)
            return weldId;

        // Track pairs of vertices that appear in the same triangle so welding never collapses a triangle edge.
        var sameTriPairs = new HashSet<long>(triangles.Count * 3, EdgeKey.Comparer);
        foreach (var t in triangles)
        {
            sameTriPairs.Add(UndirectedKey(t.V0, t.V1));
            sameTriPairs.Add(UndirectedKey(t.V1, t.V2));
            sameTriPairs.Add(UndirectedKey(t.V2, t.V0));
        }

        float invCell = 1f / toleranceM;
        float tolSq = toleranceM * toleranceM;
        var grid = new Dictionary<(int, int, int), List<int>>(n);
        var reps = new List<int>(n);

        for (int i = 0; i < n; i++)
        {
            var p = positions[i];
            int cx = (int)MathF.Floor(p.X * invCell);
            int cy = (int)MathF.Floor(p.Y * invCell);
            int cz = (int)MathF.Floor(p.Z * invCell);

            int matched = -1;
            for (int dx = -1; dx <= 1 && matched < 0; dx++)
            {
                for (int dy = -1; dy <= 1 && matched < 0; dy++)
                {
                    for (int dz = -1; dz <= 1 && matched < 0; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var bucket))
                            continue;
                        foreach (int rep in bucket)
                        {
                            if (Vector3.DistanceSquared(p, positions[rep]) <= tolSq &&
                                !sameTriPairs.Contains(UndirectedKey(i, rep)))
                            {
                                matched = rep;
                                break;
                            }
                        }
                    }
                }
            }

            if (matched >= 0)
            {
                weldId[i] = weldId[matched];
            }
            else
            {
                int id = reps.Count;
                reps.Add(i);
                weldId[i] = id;
                var key = (cx, cy, cz);
                if (!grid.TryGetValue(key, out var bucket))
                {
                    bucket = new List<int>(4);
                    grid[key] = bucket;
                }
                bucket.Add(i);
            }
        }

        return weldId;
    }

    // ------------------------------------------------------------------------
    // Stage 3: Panel partitioning (UV islands and/or Polygroup components)
    // ------------------------------------------------------------------------

    private sealed class PanelData
    {
        public int Index;
        public string GroupName = "";
        public bool HasUvs;
        public bool UsedUvs;
        public required Vector3[] Pos3D;
        public required int[] WeldIds;
        public required Vector2[] Uvs;
        public Vector2[] Pos2D = Array.Empty<Vector2>();
        public required int[] Triangles; // triples of local vertex indices
        public double Area3DCm2;
        public List<BoundaryLoop> Loops = new();
        public PatternPiece? Piece;
    }

    private sealed class BoundaryHalfEdge
    {
        public int PanelIndex;
        public int LoopIndex;
        public int IndexInLoop;
        public int From;
        public int To;
        public int WeldFrom;
        public int WeldTo;
        public BoundaryHalfEdge? Partner;
        public bool Same3DDirection;
    }

    private sealed class BoundaryLoop
    {
        public bool IsHole;
        public List<BoundaryHalfEdge> HalfEdges = new();
        public bool[] Pinned = Array.Empty<bool>();
        public bool[] Kept = Array.Empty<bool>();
    }

    private static List<PanelData> PartitionPanels(ParsedObj obj, int[] weldIds, MeshImportOptions opts, List<string> warnings)
    {
        int triCount = obj.Triangles.Count;
        var triHasUv = new bool[triCount];
        var triUvSign = new int[triCount];
        int uvTriCount = 0;

        for (int t = 0; t < triCount; t++)
        {
            var tri = obj.Triangles[t];
            if (opts.UseUvs && tri.Vt0 >= 0 && tri.Vt1 >= 0 && tri.Vt2 >= 0)
            {
                triHasUv[t] = true;
                uvTriCount++;
                var u0 = obj.Uvs[tri.Vt0];
                var u1 = obj.Uvs[tri.Vt1];
                var u2 = obj.Uvs[tri.Vt2];
                float area = (u1.X - u0.X) * (u2.Y - u0.Y) - (u1.Y - u0.Y) * (u2.X - u0.X);
                triUvSign[t] = area > 1e-12f ? 1 : (area < -1e-12f ? -1 : 0);
            }
        }

        if (opts.UseUvs && uvTriCount > 0 && uvTriCount < triCount)
            warnings.Add($"Only {uvTriCount}/{triCount} triangles had valid UVs; panels without UVs were flattened from 3D.");

        // Map undirected 3D welded edge -> incident triangles.
        var edgeTris = new Dictionary<long, List<int>>(triCount * 2, EdgeKey.Comparer);
        for (int t = 0; t < triCount; t++)
        {
            var tri = obj.Triangles[t];
            int w0 = weldIds[tri.V0], w1 = weldIds[tri.V1], w2 = weldIds[tri.V2];
            AddIncident(edgeTris, UndirectedKey(w0, w1), t);
            AddIncident(edgeTris, UndirectedKey(w1, w2), t);
            AddIncident(edgeTris, UndirectedKey(w2, w0), t);
        }

        var parent = new int[triCount];
        for (int i = 0; i < triCount; i++)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
                parent[rb] = ra;
        }

        // Remember which pairs of triangles are actually connected across each welded edge.
        var connectedPairs = new List<(int T1, int T2, int WA, int WB)>();

        foreach (var (edgeKey, list) in edgeTris)
        {
            if (list.Count < 2)
                continue;
            int wA = (int)(edgeKey >> 32);
            int wB = (int)(edgeKey & 0xFFFFFFFF);

            // For non-manifold edges (>2 triangles), only connect pairs that share an exact UV match or manifold pair.
            if (list.Count == 2)
            {
                int t1 = list[0], t2 = list[1];
                if (ShouldConnectTriangles(obj, weldIds, triHasUv, triUvSign, opts, t1, t2, wA, wB))
                {
                    Union(t1, t2);
                    connectedPairs.Add((t1, t2, wA, wB));
                }
            }
            else
            {
                var used = new bool[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    if (used[i]) continue;
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (used[j]) continue;
                        if (ShouldConnectTriangles(obj, weldIds, triHasUv, triUvSign, opts, list[i], list[j], wA, wB))
                        {
                            Union(list[i], list[j]);
                            connectedPairs.Add((list[i], list[j], wA, wB));
                            used[i] = used[j] = true;
                            break;
                        }
                    }
                }
            }
        }

        // Group triangles by connected component.
        var groups = new Dictionary<int, List<int>>();
        for (int t = 0; t < triCount; t++)
        {
            int root = Find(t);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                groups[root] = list;
            }
            list.Add(t);
        }

        // Index connected triangle pairs per component so we can weld local panel vertices accurately.
        var pairsByComp = new Dictionary<int, List<(int T1, int T2, int WA, int WB)>>();
        foreach (var pair in connectedPairs)
        {
            int root = Find(pair.T1);
            if (!pairsByComp.TryGetValue(root, out var list))
            {
                list = new List<(int, int, int, int)>();
                pairsByComp[root] = list;
            }
            list.Add(pair);
        }

        var panels = new List<PanelData>(groups.Count);
        foreach (var (root, tris) in groups)
        {
            var localTriIdx = new Dictionary<int, int>(tris.Count);
            for (int i = 0; i < tris.Count; i++)
                localTriIdx[tris[i]] = i;

            // Union-Find over the 3*tris.Count triangle corners inside this panel.
            int cornerCount = tris.Count * 3;
            var cParent = new int[cornerCount];
            for (int i = 0; i < cornerCount; i++)
                cParent[i] = i;
            int CFind(int x)
            {
                while (cParent[x] != x)
                    x = cParent[x] = cParent[cParent[x]];
                return x;
            }
            void CUnion(int a, int b)
            {
                int ra = CFind(a), rb = CFind(b);
                if (ra != rb)
                    cParent[rb] = ra;
            }

            if (pairsByComp.TryGetValue(root, out var compPairs))
            {
                foreach (var (t1, t2, wA, wB) in compPairs)
                {
                    int lt1 = localTriIdx[t1];
                    int lt2 = localTriIdx[t2];
                    int c1A = CornerOfWeld(obj.Triangles[t1], weldIds, wA);
                    int c2A = CornerOfWeld(obj.Triangles[t2], weldIds, wA);
                    int c1B = CornerOfWeld(obj.Triangles[t1], weldIds, wB);
                    int c2B = CornerOfWeld(obj.Triangles[t2], weldIds, wB);
                    if (c1A >= 0 && c2A >= 0) CUnion(lt1 * 3 + c1A, lt2 * 3 + c2A);
                    if (c1B >= 0 && c2B >= 0) CUnion(lt1 * 3 + c1B, lt2 * 3 + c2B);
                }
            }

            // Compact corner equivalence classes into local panel vertices.
            var rootToVert = new Dictionary<int, int>();
            var posSum = new List<Vector3>();
            var uvSum = new List<Vector2>();
            var vertCounts = new List<int>();
            var localWeld = new List<int>();
            var localTris = new int[cornerCount];
            bool allHaveUvs = true;
            var groupFreq = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < tris.Count; i++)
            {
                var tri = obj.Triangles[tris[i]];
                if (!triHasUv[tris[i]])
                    allHaveUvs = false;
                if (!string.IsNullOrEmpty(tri.Group))
                    groupFreq[tri.Group] = groupFreq.GetValueOrDefault(tri.Group) + 1;

                int[] vs = { tri.V0, tri.V1, tri.V2 };
                int[] vts = { tri.Vt0, tri.Vt1, tri.Vt2 };
                for (int k = 0; k < 3; k++)
                {
                    int cRoot = CFind(i * 3 + k);
                    if (!rootToVert.TryGetValue(cRoot, out int vId))
                    {
                        vId = posSum.Count;
                        rootToVert[cRoot] = vId;
                        posSum.Add(Vector3.Zero);
                        uvSum.Add(Vector2.Zero);
                        vertCounts.Add(0);
                        localWeld.Add(weldIds[vs[k]]);
                    }
                    posSum[vId] += obj.Positions[vs[k]];
                    if (vts[k] >= 0)
                        uvSum[vId] += obj.Uvs[vts[k]];
                    vertCounts[vId]++;
                    localTris[i * 3 + k] = vId;
                }
            }

            int vCount = posSum.Count;
            var pos3D = new Vector3[vCount];
            var panelUvs = new Vector2[vCount];
            for (int i = 0; i < vCount; i++)
            {
                float inv = 1f / vertCounts[i];
                pos3D[i] = posSum[i] * inv;
                panelUvs[i] = uvSum[i] * inv;
            }

            string dominantGroup = groupFreq.Count > 0
                ? groupFreq.OrderByDescending(kv => kv.Value).First().Key
                : "";

            panels.Add(new PanelData
            {
                Index = panels.Count,
                GroupName = dominantGroup,
                HasUvs = allHaveUvs,
                Pos3D = pos3D,
                WeldIds = localWeld.ToArray(),
                Uvs = panelUvs,
                Triangles = localTris,
            });
        }

        return panels;
    }

    private static bool ShouldConnectTriangles(
        ParsedObj obj, int[] weldIds, bool[] triHasUv, int[] triUvSign,
        MeshImportOptions opts, int t1, int t2, int wA, int wB)
    {
        var a = obj.Triangles[t1];
        var b = obj.Triangles[t2];
        if (opts.SplitByGroups && !string.Equals(a.Group, b.Group, StringComparison.Ordinal))
            return false;

        if (triHasUv[t1] != triHasUv[t2])
            return false;

        if (!triHasUv[t1])
            return true;

        // Require consistent UV winding sign across an interior edge (prevents merging folded-over UV shells).
        if (triUvSign[t1] != 0 && triUvSign[t2] != 0 && triUvSign[t1] != triUvSign[t2])
            return false;

        int vt1A = VtOfWeld(a, weldIds, wA), vt2A = VtOfWeld(b, weldIds, wA);
        int vt1B = VtOfWeld(a, weldIds, wB), vt2B = VtOfWeld(b, weldIds, wB);
        if (vt1A < 0 || vt2A < 0 || vt1B < 0 || vt2B < 0)
            return false;

        bool matchA = vt1A == vt2A || Vector2.DistanceSquared(obj.Uvs[vt1A], obj.Uvs[vt2A]) <= UvMatchEpsilonSq;
        bool matchB = vt1B == vt2B || Vector2.DistanceSquared(obj.Uvs[vt1B], obj.Uvs[vt2B]) <= UvMatchEpsilonSq;
        return matchA && matchB;
    }

    private static int CornerOfWeld(ObjTri tri, int[] weldIds, int w)
    {
        if (weldIds[tri.V0] == w) return 0;
        if (weldIds[tri.V1] == w) return 1;
        if (weldIds[tri.V2] == w) return 2;
        return -1;
    }

    private static int VtOfWeld(ObjTri tri, int[] weldIds, int w)
    {
        if (weldIds[tri.V0] == w) return tri.Vt0;
        if (weldIds[tri.V1] == w) return tri.Vt1;
        if (weldIds[tri.V2] == w) return tri.Vt2;
        return -1;
    }

    private static void AddIncident(Dictionary<long, List<int>> map, long key, int tri)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<int>(2);
            map[key] = list;
        }
        if (!list.Contains(tri))
            list.Add(tri);
    }

    // ------------------------------------------------------------------------
    // Stage 4: Panel flattening (UV metric scaling OR PCA + ARAP/edge relaxation)
    // ------------------------------------------------------------------------

    private static void FlattenPanel(PanelData panel, List<string> warnings)
    {
        int triCount = panel.Triangles.Length / 3;
        double area3DM2 = 0;
        for (int t = 0; t < triCount; t++)
        {
            var a = panel.Pos3D[panel.Triangles[3 * t]];
            var b = panel.Pos3D[panel.Triangles[3 * t + 1]];
            var c = panel.Pos3D[panel.Triangles[3 * t + 2]];
            area3DM2 += 0.5 * Vector3.Cross(b - a, c - a).Length();
        }
        panel.Area3DCm2 = area3DM2 * 10000.0;

        if (panel.HasUvs)
        {
            double signedUvArea = 0;
            for (int t = 0; t < triCount; t++)
            {
                var a = panel.Uvs[panel.Triangles[3 * t]];
                var b = panel.Uvs[panel.Triangles[3 * t + 1]];
                var c = panel.Uvs[panel.Triangles[3 * t + 2]];
                signedUvArea += 0.5 * ((double)(b.X - a.X) * (c.Y - a.Y) - (double)(b.Y - a.Y) * (c.X - a.X));
            }

            if (Math.Abs(signedUvArea) > 1e-10 && panel.Area3DCm2 > 1e-6)
            {
                bool flipU = signedUvArea < 0;
                double scale = Math.Sqrt(panel.Area3DCm2 / Math.Abs(signedUvArea));
                panel.Pos2D = new Vector2[panel.Pos3D.Length];
                for (int i = 0; i < panel.Pos3D.Length; i++)
                {
                    double u = flipU ? -panel.Uvs[i].X : panel.Uvs[i].X;
                    double v = panel.Uvs[i].Y;
                    panel.Pos2D[i] = new Vector2((float)(u * scale), (float)(v * scale));
                }
                panel.UsedUvs = true;
                return;
            }

            warnings.Add($"Panel '{(string.IsNullOrEmpty(panel.GroupName) ? $"#{panel.Index + 1}" : panel.GroupName)}' had degenerate UVs; flattened from 3D instead.");
        }

        FlattenPanelPca(panel);
    }

    private static void FlattenPanelPca(PanelData panel)
    {
        int vCount = panel.Pos3D.Length;
        int triCount = panel.Triangles.Length / 3;

        // Area-weighted 3D centroid and outward normal.
        Vector3 centroid = Vector3.Zero;
        Vector3 avgNormal = Vector3.Zero;
        double totalArea = 0;
        for (int t = 0; t < triCount; t++)
        {
            var a = panel.Pos3D[panel.Triangles[3 * t]];
            var b = panel.Pos3D[panel.Triangles[3 * t + 1]];
            var c = panel.Pos3D[panel.Triangles[3 * t + 2]];
            var cross = Vector3.Cross(b - a, c - a);
            float area = 0.5f * cross.Length();
            centroid += (a + b + c) * (area / 3f);
            avgNormal += cross;
            totalArea += area;
        }
        if (totalArea > 1e-12)
            centroid /= (float)totalArea;
        else
            centroid = panel.Pos3D.Aggregate(Vector3.Zero, (s, p) => s + p) / Math.Max(1, vCount);

        // 3x3 covariance matrix for PCA tangent plane.
        double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
        for (int i = 0; i < vCount; i++)
        {
            var d = panel.Pos3D[i] - centroid;
            cxx += (double)d.X * d.X;
            cxy += (double)d.X * d.Y;
            cxz += (double)d.X * d.Z;
            cyy += (double)d.Y * d.Y;
            cyz += (double)d.Y * d.Z;
            czz += (double)d.Z * d.Z;
        }

        SolveSymmetric3x3(cxx, cxy, cxz, cyy, cyz, czz, out var e0, out _, out var e2);

        Vector3 planeNormal = e2;
        if (avgNormal.LengthSquared() > 1e-12f && Vector3.Dot(planeNormal, avgNormal) < 0)
            planeNormal = -planeNormal;
        if (planeNormal.LengthSquared() < 1e-12f)
            planeNormal = avgNormal.LengthSquared() > 1e-12f ? Vector3.Normalize(avgNormal) : Vector3.UnitZ;
        else
            planeNormal = Vector3.Normalize(planeNormal);

        // Choose upright tangent basis (axisX, axisY, planeNormal) with Cross(axisX, axisY) == planeNormal.
        Vector3 upProj = Vector3.UnitY - Vector3.Dot(Vector3.UnitY, planeNormal) * planeNormal;
        Vector3 axisX, axisY;
        if (upProj.LengthSquared() > 0.05f)
        {
            axisY = Vector3.Normalize(upProj);
            axisX = Vector3.Normalize(Vector3.Cross(axisY, planeNormal));
        }
        else
        {
            Vector3 p0 = e0 - Vector3.Dot(e0, planeNormal) * planeNormal;
            axisX = p0.LengthSquared() > 1e-8f ? Vector3.Normalize(p0) : OrthogonalUnit(planeNormal);
            axisY = Vector3.Normalize(Vector3.Cross(planeNormal, axisX));
        }

        var initial2D = new Vector2[vCount];
        var pos2D = new Vector2[vCount];
        for (int i = 0; i < vCount; i++)
        {
            var dCm = (panel.Pos3D[i] - centroid) * 100f;
            var p = new Vector2(Vector3.Dot(dCm, axisX), Vector3.Dot(dCm, axisY));
            initial2D[i] = p;
            pos2D[i] = p;
        }

        // Collect unique panel edges with exact 3D rest lengths (cm).
        var edgeSet = new HashSet<long>(EdgeKey.Comparer);
        var edges = new List<(int U, int V, float RestCm)>(triCount * 2);
        var restTris = new (int A, int B, int C, Vector2 R0, Vector2 R1, Vector2 R2, float Weight)[triCount];
        float maxInitErr = 0f;

        for (int t = 0; t < triCount; t++)
        {
            int a = panel.Triangles[3 * t];
            int b = panel.Triangles[3 * t + 1];
            int c = panel.Triangles[3 * t + 2];

            AddRelaxEdge(edgeSet, edges, panel.Pos3D, pos2D, a, b, ref maxInitErr);
            AddRelaxEdge(edgeSet, edges, panel.Pos3D, pos2D, b, c, ref maxInitErr);
            AddRelaxEdge(edgeSet, edges, panel.Pos3D, pos2D, c, a, ref maxInitErr);

            // Exact isometric 2D embedding of triangle (a, b, c) in centimetres (CCW).
            var e1 = (panel.Pos3D[b] - panel.Pos3D[a]) * 100f;
            var e2v = (panel.Pos3D[c] - panel.Pos3D[a]) * 100f;
            float l1 = e1.Length();
            float l2 = e2v.Length();
            if (l1 > 1e-6f && l2 > 1e-6f)
            {
                float x2 = Vector3.Dot(e1, e2v) / l1;
                float y2 = MathF.Sqrt(MathF.Max(0f, l2 * l2 - x2 * x2));
                float areaCm2 = 0.5f * l1 * y2;
                restTris[t] = (a, b, c, Vector2.Zero, new Vector2(l1, 0), new Vector2(x2, y2), MathF.Max(areaCm2, 1e-4f));
            }
            else
            {
                restTris[t] = (a, b, c, Vector2.Zero, Vector2.Zero, Vector2.Zero, 0f);
            }
        }

        // Iterative edge-length relaxation + isometric triangle shape-matching (ARAP) for curved surfaces.
        if (maxInitErr > 1e-3f)
        {
            int maxIter = vCount <= 500 ? 180 : (vCount <= 2000 ? 100 : 50);
            var sumPos = new Vector2[vCount];
            var sumWeight = new float[vCount];

            for (int iter = 0; iter < maxIter; iter++)
            {
                // 1. Gauss-Seidel edge-length projection (propagates global arc-length unfolding quickly).
                foreach (var (u, v, restCm) in edges)
                {
                    var diff = pos2D[v] - pos2D[u];
                    float len = diff.Length();
                    Vector2 dir;
                    if (len > 1e-5f)
                    {
                        dir = diff / len;
                    }
                    else
                    {
                        var d3 = panel.Pos3D[v] - panel.Pos3D[u];
                        var fallback = new Vector2(Vector3.Dot(d3, axisX), Vector3.Dot(d3, axisY));
                        dir = fallback.LengthSquared() > 1e-8f ? Vector2.Normalize(fallback) : Vector2.UnitX;
                    }
                    var corr = dir * ((len - restCm) * 0.35f);
                    pos2D[u] += corr;
                    pos2D[v] -= corr;
                }

                // 2. Local-global isometric triangle shape-matching (preserves angles and prevents flips).
                Array.Clear(sumPos, 0, vCount);
                Array.Clear(sumWeight, 0, vCount);
                for (int t = 0; t < triCount; t++)
                {
                    var rt = restTris[t];
                    if (rt.Weight <= 0f)
                        continue;

                    var p0 = pos2D[rt.A];
                    var p1 = pos2D[rt.B];
                    var p2 = pos2D[rt.C];
                    var pc = (p0 + p1 + p2) * (1f / 3f);
                    var rc = (rt.R0 + rt.R1 + rt.R2) * (1f / 3f);

                    var dp0 = p0 - pc; var dp1 = p1 - pc; var dp2 = p2 - pc;
                    var dr0 = rt.R0 - rc; var dr1 = rt.R1 - rc; var dr2 = rt.R2 - rc;

                    float cosTerm = dr0.X * dp0.X + dr0.Y * dp0.Y
                                  + dr1.X * dp1.X + dr1.Y * dp1.Y
                                  + dr2.X * dp2.X + dr2.Y * dp2.Y;
                    float sinTerm = dr0.X * dp0.Y - dr0.Y * dp0.X
                                  + dr1.X * dp1.Y - dr1.Y * dp1.X
                                  + dr2.X * dp2.Y - dr2.Y * dp2.X;
                    float norm = MathF.Sqrt(cosTerm * cosTerm + sinTerm * sinTerm);
                    float cRot = norm > 1e-8f ? cosTerm / norm : 1f;
                    float sRot = norm > 1e-8f ? sinTerm / norm : 0f;

                    float w = rt.Weight;
                    sumPos[rt.A] += (pc + Rotate2D(dr0, cRot, sRot)) * w;
                    sumPos[rt.B] += (pc + Rotate2D(dr1, cRot, sRot)) * w;
                    sumPos[rt.C] += (pc + Rotate2D(dr2, cRot, sRot)) * w;
                    sumWeight[rt.A] += w;
                    sumWeight[rt.B] += w;
                    sumWeight[rt.C] += w;
                }

                float maxMove = 0f;
                for (int i = 0; i < vCount; i++)
                {
                    if (sumWeight[i] <= 0f)
                        continue;
                    var next = sumPos[i] / sumWeight[i];
                    float move = Vector2.Distance(pos2D[i], next);
                    if (move > maxMove)
                        maxMove = move;
                    pos2D[i] = next;
                }

                if (maxMove < 1e-3f)
                    break;
            }

            // Re-align relaxed 2D coordinates back to the initial upright PCA frame via global 2D Procrustes.
            Align2DRigid(pos2D, initial2D);

            // Match total metric surface area to 3D area.
            double area2DCm2 = 0;
            for (int t = 0; t < triCount; t++)
            {
                var a = pos2D[panel.Triangles[3 * t]];
                var b = pos2D[panel.Triangles[3 * t + 1]];
                var c = pos2D[panel.Triangles[3 * t + 2]];
                area2DCm2 += 0.5 * ((double)(b.X - a.X) * (c.Y - a.Y) - (double)(b.Y - a.Y) * (c.X - a.X));
            }
            if (area2DCm2 > 1e-4 && panel.Area3DCm2 > 1e-4)
            {
                float scale = (float)Math.Sqrt(panel.Area3DCm2 / area2DCm2);
                if (MathF.Abs(scale - 1f) > 1e-4f)
                {
                    var avg = Vector2.Zero;
                    for (int i = 0; i < vCount; i++) avg += pos2D[i];
                    avg /= vCount;
                    for (int i = 0; i < vCount; i++)
                        pos2D[i] = avg + (pos2D[i] - avg) * scale;
                }
            }
        }

        panel.Pos2D = pos2D;
        panel.UsedUvs = false;
    }

    private static void AddRelaxEdge(
        HashSet<long> edgeSet, List<(int U, int V, float RestCm)> edges,
        Vector3[] pos3D, Vector2[] pos2D, int u, int v, ref float maxRelErr)
    {
        long key = UndirectedKey(u, v);
        if (!edgeSet.Add(key))
            return;
        float restCm = Vector3.Distance(pos3D[u], pos3D[v]) * 100f;
        if (restCm < 1e-5f)
            return;
        edges.Add((u, v, restCm));
        float curCm = Vector2.Distance(pos2D[u], pos2D[v]);
        float err = MathF.Abs(curCm - restCm) / restCm;
        if (err > maxRelErr)
            maxRelErr = err;
    }

    private static Vector2 Rotate2D(Vector2 v, float c, float s)
        => new(c * v.X - s * v.Y, s * v.X + c * v.Y);

    private static void Align2DRigid(Vector2[] moving, Vector2[] target)
    {
        int n = moving.Length;
        if (n == 0) return;
        Vector2 cm = Vector2.Zero, ct = Vector2.Zero;
        for (int i = 0; i < n; i++)
        {
            cm += moving[i];
            ct += target[i];
        }
        cm /= n;
        ct /= n;

        double cosTerm = 0, sinTerm = 0;
        for (int i = 0; i < n; i++)
        {
            var dm = moving[i] - cm;
            var dt = target[i] - ct;
            cosTerm += (double)dm.X * dt.X + (double)dm.Y * dt.Y;
            sinTerm += (double)dm.X * dt.Y - (double)dm.Y * dt.X;
        }
        double norm = Math.Sqrt(cosTerm * cosTerm + sinTerm * sinTerm);
        float c = norm > 1e-12 ? (float)(cosTerm / norm) : 1f;
        float s = norm > 1e-12 ? (float)(sinTerm / norm) : 0f;
        for (int i = 0; i < n; i++)
            moving[i] = ct + Rotate2D(moving[i] - cm, c, s);
    }

    /// <summary>Jacobi eigensolver for a 3x3 symmetric matrix; returns unit eigenvectors ordered by descending eigenvalue.</summary>
    private static void SolveSymmetric3x3(
        double a00, double a01, double a02, double a11, double a12, double a22,
        out Vector3 e0, out Vector3 e1, out Vector3 e2)
    {
        double[,] a = { { a00, a01, a02 }, { a01, a11, a12 }, { a02, a12, a22 } };
        double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        for (int iter = 0; iter < 32; iter++)
        {
            int p = 0, q = 1;
            double maxOff = Math.Abs(a[0, 1]);
            if (Math.Abs(a[0, 2]) > maxOff) { p = 0; q = 2; maxOff = Math.Abs(a[0, 2]); }
            if (Math.Abs(a[1, 2]) > maxOff) { p = 1; q = 2; maxOff = Math.Abs(a[1, 2]); }
            if (maxOff < 1e-14)
                break;

            double app = a[p, p], aqq = a[q, q], apq = a[p, q];
            double tau = (aqq - app) / (2.0 * apq);
            double t = tau >= 0 ? 1.0 / (tau + Math.Sqrt(1.0 + tau * tau)) : -1.0 / (-tau + Math.Sqrt(1.0 + tau * tau));
            double c = 1.0 / Math.Sqrt(1.0 + t * t);
            double s = t * c;

            a[p, p] = app - t * apq;
            a[q, q] = aqq + t * apq;
            a[p, q] = a[q, p] = 0.0;

            int r = 3 - p - q;
            double arp = a[r, p], arq = a[r, q];
            a[r, p] = a[p, r] = c * arp - s * arq;
            a[r, q] = a[q, r] = s * arp + c * arq;

            for (int k = 0; k < 3; k++)
            {
                double vkp = v[k, p], vkq = v[k, q];
                v[k, p] = c * vkp - s * vkq;
                v[k, q] = s * vkp + c * vkq;
            }
        }

        var cols = new (double Val, Vector3 Vec)[]
        {
            (a[0, 0], Vector3.Normalize(new Vector3((float)v[0, 0], (float)v[1, 0], (float)v[2, 0]))),
            (a[1, 1], Vector3.Normalize(new Vector3((float)v[0, 1], (float)v[1, 1], (float)v[2, 1]))),
            (a[2, 2], Vector3.Normalize(new Vector3((float)v[0, 2], (float)v[1, 2], (float)v[2, 2]))),
        };
        Array.Sort(cols, (x, y) => y.Val.CompareTo(x.Val));
        e0 = cols[0].Vec;
        e1 = cols[1].Vec;
        e2 = cols[2].Vec;
    }

    private static Vector3 OrthogonalUnit(Vector3 n)
    {
        var axis = MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        return Vector3.Normalize(Vector3.Cross(n, axis));
    }

    // ------------------------------------------------------------------------
    // Stage 5: Directed boundary half-edge loop extraction
    // ------------------------------------------------------------------------

    private static void ExtractBoundaryLoops(PanelData panel, List<string> warnings)
    {
        int triCount = panel.Triangles.Length / 3;
        var edgeCounts = new Dictionary<long, int>(triCount * 2, EdgeKey.Comparer);
        var directedEdges = new List<(int From, int To)>(triCount * 3);

        for (int t = 0; t < triCount; t++)
        {
            int a = panel.Triangles[3 * t];
            int b = panel.Triangles[3 * t + 1];
            int c = panel.Triangles[3 * t + 2];
            CountEdge(edgeCounts, a, b);
            CountEdge(edgeCounts, b, c);
            CountEdge(edgeCounts, c, a);
            directedEdges.Add((a, b));
            directedEdges.Add((b, c));
            directedEdges.Add((c, a));
        }

        var outgoing = new Dictionary<int, List<BoundaryHalfEdge>>();
        var allBoundary = new List<BoundaryHalfEdge>();

        foreach (var (u, v) in directedEdges)
        {
            if (edgeCounts[UndirectedKey(u, v)] != 1)
                continue;
            var he = new BoundaryHalfEdge
            {
                PanelIndex = panel.Index,
                From = u,
                To = v,
                WeldFrom = panel.WeldIds[u],
                WeldTo = panel.WeldIds[v],
            };
            allBoundary.Add(he);
            if (!outgoing.TryGetValue(u, out var list))
            {
                list = new List<BoundaryHalfEdge>(2);
                outgoing[u] = list;
            }
            list.Add(he);
        }

        var visited = new HashSet<BoundaryHalfEdge>();
        var rawLoops = new List<(List<BoundaryHalfEdge> Edges, float SignedArea, List<Vector2> Poly)>();

        foreach (var start in allBoundary)
        {
            if (visited.Contains(start))
                continue;

            var chain = new List<BoundaryHalfEdge>();
            var cur = start;
            bool closed = false;
            int guard = allBoundary.Count + 1;
            while (guard-- > 0)
            {
                visited.Add(cur);
                chain.Add(cur);
                if (cur.To == start.From)
                {
                    closed = true;
                    break;
                }
                if (!outgoing.TryGetValue(cur.To, out var nextCandidates))
                    break;

                BoundaryHalfEdge? next = null;
                if (nextCandidates.Count == 1)
                {
                    if (!visited.Contains(nextCandidates[0]))
                        next = nextCandidates[0];
                }
                else
                {
                    // Resolve pinch/bowtie vertex by choosing the unvisited outgoing edge with the tightest left turn.
                    var inDir = panel.Pos2D[cur.From] - panel.Pos2D[cur.To];
                    float bestAngle = float.MaxValue;
                    foreach (var cand in nextCandidates)
                    {
                        if (visited.Contains(cand))
                            continue;
                        var outDir = panel.Pos2D[cand.To] - panel.Pos2D[cand.From];
                        float ang = MathF.Atan2(inDir.X * outDir.Y - inDir.Y * outDir.X, Vector2.Dot(inDir, outDir));
                        if (ang <= 0) ang += MathF.PI * 2f;
                        if (ang < bestAngle)
                        {
                            bestAngle = ang;
                            next = cand;
                        }
                    }
                }

                if (next == null)
                    break;
                cur = next;
            }

            if (!closed || chain.Count < 3)
                continue;

            var poly = chain.Select(e => panel.Pos2D[e.From]).ToList();
            float signedArea = Outline.SignedArea(poly);
            if (MathF.Abs(signedArea) < 0.05f)
                continue;

            rawLoops.Add((chain, signedArea, poly));
        }

        if (rawLoops.Count == 0)
        {
            warnings.Add($"Panel '{(string.IsNullOrEmpty(panel.GroupName) ? $"#{panel.Index + 1}" : panel.GroupName)}' had no closed boundary loop and was skipped.");
            return;
        }

        // Largest-area loop is the outer outline (CCW). Remaining loops inside it are holes (CW).
        rawLoops.Sort((x, y) => MathF.Abs(y.SignedArea).CompareTo(MathF.Abs(x.SignedArea)));
        var outer = rawLoops[0];
        var outerEdges = outer.SignedArea < 0 ? ReverseHalfEdges(outer.Edges) : outer.Edges;
        var outerPoly = outerEdges.Select(e => panel.Pos2D[e.From]).ToList();

        var outerLoop = new BoundaryLoop { IsHole = false, HalfEdges = outerEdges };
        IndexLoopEdges(outerLoop, 0);
        panel.Loops.Add(outerLoop);

        for (int i = 1; i < rawLoops.Count; i++)
        {
            var cand = rawLoops[i];
            var samplePt = cand.Poly[0];
            if (!Outline.Contains(outerPoly, samplePt))
                continue;
            var holeEdges = cand.SignedArea > 0 ? ReverseHalfEdges(cand.Edges) : cand.Edges;
            var holeLoop = new BoundaryLoop { IsHole = true, HalfEdges = holeEdges };
            IndexLoopEdges(holeLoop, panel.Loops.Count);
            panel.Loops.Add(holeLoop);
        }
    }

    private static List<BoundaryHalfEdge> ReverseHalfEdges(List<BoundaryHalfEdge> edges)
    {
        int n = edges.Count;
        var reversed = new List<BoundaryHalfEdge>(n);
        for (int i = n - 1; i >= 0; i--)
        {
            var e = edges[i];
            reversed.Add(new BoundaryHalfEdge
            {
                PanelIndex = e.PanelIndex,
                From = e.To,
                To = e.From,
                WeldFrom = e.WeldTo,
                WeldTo = e.WeldFrom,
            });
        }
        return reversed;
    }

    private static void IndexLoopEdges(BoundaryLoop loop, int loopIndex)
    {
        for (int i = 0; i < loop.HalfEdges.Count; i++)
        {
            loop.HalfEdges[i].LoopIndex = loopIndex;
            loop.HalfEdges[i].IndexInLoop = i;
        }
    }

    private static void CountEdge(Dictionary<long, int> counts, int a, int b)
    {
        long key = UndirectedKey(a, b);
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    // ------------------------------------------------------------------------
    // Stage 6: Cross-panel welded seam matching
    // ------------------------------------------------------------------------

    private static void MatchSeamPartners(List<PanelData> panels)
    {
        var byWeldEdge = new Dictionary<long, List<BoundaryHalfEdge>>(EdgeKey.Comparer);
        foreach (var panel in panels)
        {
            foreach (var loop in panel.Loops)
            {
                foreach (var he in loop.HalfEdges)
                {
                    if (he.WeldFrom == he.WeldTo)
                        continue;
                    long key = UndirectedKey(he.WeldFrom, he.WeldTo);
                    if (!byWeldEdge.TryGetValue(key, out var list))
                    {
                        list = new List<BoundaryHalfEdge>(2);
                        byWeldEdge[key] = list;
                    }
                    list.Add(he);
                }
            }
        }

        foreach (var (_, list) in byWeldEdge)
        {
            if (list.Count < 2)
                continue;

            // Pair cross-panel edges first, then same-panel distinct edges (e.g. cylindrical sleeves).
            var paired = new bool[list.Count];
            for (int pass = 0; pass < 2; pass++)
            {
                bool requireDifferentPanel = pass == 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (paired[i]) continue;
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (paired[j]) continue;
                        var a = list[i];
                        var b = list[j];
                        if (requireDifferentPanel && a.PanelIndex == b.PanelIndex)
                            continue;
                        if (a.PanelIndex == b.PanelIndex && a.LoopIndex == b.LoopIndex && a.IndexInLoop == b.IndexInLoop)
                            continue;

                        bool sameDir = a.WeldFrom == b.WeldFrom && a.WeldTo == b.WeldTo;
                        a.Partner = b;
                        b.Partner = a;
                        a.Same3DDirection = sameDir;
                        b.Same3DDirection = sameDir;
                        paired[i] = paired[j] = true;
                        break;
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------------
    // Stage 7: Corner & seam-transition pinning + synchronized simplification
    // ------------------------------------------------------------------------

    private static void PinCornersAndTransitions(List<PanelData> panels, MeshImportOptions opts)
    {
        foreach (var panel in panels)
        {
            foreach (var loop in panel.Loops)
            {
                int n = loop.HalfEdges.Count;
                loop.Pinned = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    var prev = loop.HalfEdges[(i - 1 + n) % n];
                    var next = loop.HalfEdges[i];

                    if (IsSeamTransition(panels, prev, next))
                        loop.Pinned[i] = true;

                    float turnDeg = TurningAngleDeg(panel.Pos2D[prev.From], panel.Pos2D[next.From], panel.Pos2D[next.To]);
                    if (turnDeg >= opts.CornerAngleDeg)
                        loop.Pinned[i] = true;
                }
            }
        }

        // Propagate pins across seam partners so both sides of every seam share identical endpoints.
        if (opts.AutoSew)
            PropagatePinsAcrossSeams(panels);

        // Ensure every loop has at least 2 (preferably 3) pinned anchors so closed curves without sharp corners split cleanly.
        bool addedAny = false;
        foreach (var panel in panels)
        {
            foreach (var loop in panel.Loops)
            {
                int n = loop.HalfEdges.Count;
                int pinCount = CountTrue(loop.Pinned);
                if (pinCount >= 2)
                    continue;

                if (pinCount == 0)
                {
                    bool allSewn = opts.AutoSew && loop.HalfEdges.All(e => e.Partner != null);
                    if (allSewn)
                    {
                        loop.Pinned[0] = true;
                        loop.Pinned[n / 3] = true;
                        loop.Pinned[(2 * n) / 3] = true;
                        addedAny = true;
                    }
                    else
                    {
                        PickThreeAnchorVertices(panel, loop);
                    }
                }
                else
                {
                    // Exactly 1 pinned vertex: add the opposite vertex halfway round the loop.
                    int firstPin = Array.IndexOf(loop.Pinned, true);
                    loop.Pinned[(firstPin + n / 2) % n] = true;
                    addedAny = true;
                }
            }
        }

        if (addedAny && opts.AutoSew)
            PropagatePinsAcrossSeams(panels);
    }

    private static void PropagatePinsAcrossSeams(List<PanelData> panels)
    {
        bool changed = true;
        for (int pass = 0; pass < 8 && changed; pass++)
        {
            changed = false;
            foreach (var panel in panels)
            {
                foreach (var loop in panel.Loops)
                {
                    int n = loop.HalfEdges.Count;
                    for (int i = 0; i < n; i++)
                    {
                        if (!loop.Pinned[i])
                            continue;

                        var next = loop.HalfEdges[i];
                        if (next.Partner is { } pn)
                        {
                            var pLoop = panels[pn.PanelIndex].Loops[pn.LoopIndex];
                            int pVert = next.Same3DDirection
                                ? pn.IndexInLoop
                                : (pn.IndexInLoop + 1) % pLoop.HalfEdges.Count;
                            if (!pLoop.Pinned[pVert])
                            {
                                pLoop.Pinned[pVert] = true;
                                changed = true;
                            }
                        }

                        var prev = loop.HalfEdges[(i - 1 + n) % n];
                        if (prev.Partner is { } pp)
                        {
                            var pLoop = panels[pp.PanelIndex].Loops[pp.LoopIndex];
                            int pVert = prev.Same3DDirection
                                ? (pp.IndexInLoop + 1) % pLoop.HalfEdges.Count
                                : pp.IndexInLoop;
                            if (!pLoop.Pinned[pVert])
                            {
                                pLoop.Pinned[pVert] = true;
                                changed = true;
                            }
                        }
                    }
                }
            }
        }
    }

    private static void PickThreeAnchorVertices(PanelData panel, BoundaryLoop loop)
    {
        int n = loop.HalfEdges.Count;
        int minX = 0, maxX = 0;
        for (int i = 1; i < n; i++)
        {
            float x = panel.Pos2D[loop.HalfEdges[i].From].X;
            if (x < panel.Pos2D[loop.HalfEdges[minX].From].X) minX = i;
            if (x > panel.Pos2D[loop.HalfEdges[maxX].From].X) maxX = i;
        }
        if (minX == maxX)
        {
            loop.Pinned[0] = true;
            loop.Pinned[n / 3] = true;
            loop.Pinned[(2 * n) / 3] = true;
            return;
        }
        loop.Pinned[minX] = true;
        loop.Pinned[maxX] = true;
        var a = panel.Pos2D[loop.HalfEdges[minX].From];
        var b = panel.Pos2D[loop.HalfEdges[maxX].From];
        int best = -1;
        float bestDist = -1f;
        for (int i = 0; i < n; i++)
        {
            if (i == minX || i == maxX) continue;
            float d = Outline.DistanceToSegment(panel.Pos2D[loop.HalfEdges[i].From], a, b, out _);
            if (d > bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        if (best >= 0)
            loop.Pinned[best] = true;
    }

    private static bool IsSeamTransition(List<PanelData> panels, BoundaryHalfEdge prev, BoundaryHalfEdge next)
    {
        if (prev.Partner == null && next.Partner == null)
            return false;
        if (prev.Partner == null || next.Partner == null)
            return true;
        if (ReferenceEquals(prev.Partner, next))
            return true;
        if (prev.Partner.PanelIndex != next.Partner.PanelIndex ||
            prev.Partner.LoopIndex != next.Partner.LoopIndex ||
            prev.Same3DDirection != next.Same3DDirection)
            return true;

        int pLen = panels[prev.Partner.PanelIndex].Loops[prev.Partner.LoopIndex].HalfEdges.Count;
        if (prev.Same3DDirection)
            return next.Partner.IndexInLoop != (prev.Partner.IndexInLoop + 1) % pLen;
        return prev.Partner.IndexInLoop != (next.Partner.IndexInLoop + 1) % pLen;
    }

    private static float TurningAngleDeg(Vector2 prevPt, Vector2 curPt, Vector2 nextPt)
    {
        var a = curPt - prevPt;
        var b = nextPt - curPt;
        float la = a.Length(), lb = b.Length();
        if (la < 1e-6f || lb < 1e-6f)
            return 0f;
        float dot = Math.Clamp(Vector2.Dot(a, b) / (la * lb), -1f, 1f);
        return MathF.Acos(dot) * (180f / MathF.PI);
    }

    private static void SimplifyLoops(List<PanelData> panels, MeshImportOptions opts)
    {
        float tol = MathF.Max(0f, opts.SimplifyToleranceCm);

        foreach (var panel in panels)
        {
            foreach (var loop in panel.Loops)
            {
                loop.Kept = new bool[loop.HalfEdges.Count];
                Array.Copy(loop.Pinned, loop.Kept, loop.Pinned.Length);
            }
        }

        // Simplify each pinned-to-pinned chain; paired seam chains are simplified synchronously so edge counts stay 1-to-1.
        var processedStarts = new HashSet<BoundaryHalfEdge>();
        foreach (var panel in panels)
        {
            foreach (var loop in panel.Loops)
            {
                int n = loop.HalfEdges.Count;
                var pins = CollectTrueIndices(loop.Pinned);
                for (int p = 0; p < pins.Count; p++)
                {
                    int start = pins[p];
                    int end = pins[(p + 1) % pins.Count];
                    var firstHe = loop.HalfEdges[start];
                    if (!processedStarts.Add(firstHe))
                        continue;

                    int count = (end - start + n) % n;
                    if (count == 0) count = n;
                    if (count <= 1)
                        continue;

                    if (firstHe.Partner == null)
                    {
                        SimplifyChainSingle(panel, loop, start, count, tol);
                    }
                    else
                    {
                        var partnerHe = firstHe.Partner;
                        var pPanel = panels[partnerHe.PanelIndex];
                        var pLoop = pPanel.Loops[partnerHe.LoopIndex];
                        int pn = pLoop.HalfEdges.Count;
                        bool sameDir = firstHe.Same3DDirection;

                        // Locate the partner chain's starting half-edge in forward order on pLoop.
                        int pStart = sameDir
                            ? partnerHe.IndexInLoop
                            : (partnerHe.IndexInLoop - (count - 1) % pn + pn) % pn;
                        processedStarts.Add(pLoop.HalfEdges[pStart]);

                        SimplifyChainPaired(panel, loop, start, pPanel, pLoop, pStart, count, sameDir, tol);
                    }
                }
            }
        }

        // Guarantee at least 3 vertices and non-self-intersecting outlines for every loop.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            bool anyRefined = false;
            foreach (var panel in panels)
            {
                foreach (var loop in panel.Loops)
                {
                    int n = loop.HalfEdges.Count;
                    var keptIndices = CollectTrueIndices(loop.Kept);
                    while (keptIndices.Count < 3 && keptIndices.Count < n)
                    {
                        for (int i = 0; i < n && keptIndices.Count < 3; i++)
                        {
                            if (!loop.Kept[i])
                            {
                                KeepVertexSynced(panels, panel, loop, i);
                                keptIndices = CollectTrueIndices(loop.Kept);
                                anyRefined = true;
                            }
                        }
                    }

                    var poly = keptIndices.Select(i => panel.Pos2D[loop.HalfEdges[i].From]).ToList();
                    if (poly.Count >= 3 && !Outline.IsSimple(poly))
                    {
                        // Subdivide every kept span that has skipped mesh vertices until the polygon becomes simple.
                        bool subdivided = false;
                        for (int k = 0; k < keptIndices.Count; k++)
                        {
                            int s = keptIndices[k];
                            int e = keptIndices[(k + 1) % keptIndices.Count];
                            int span = (e - s + n) % n;
                            if (span > 1)
                            {
                                int mid = (s + span / 2) % n;
                                KeepVertexSynced(panels, panel, loop, mid);
                                subdivided = true;
                            }
                        }
                        if (subdivided)
                            anyRefined = true;
                    }
                }
            }
            if (!anyRefined)
                break;
        }
    }

    private static void KeepVertexSynced(List<PanelData> panels, PanelData panel, BoundaryLoop loop, int vertexIdx)
    {
        if (loop.Kept[vertexIdx])
            return;
        loop.Kept[vertexIdx] = true;
        int n = loop.HalfEdges.Count;
        var next = loop.HalfEdges[vertexIdx];
        if (next.Partner is { } pn)
        {
            var pLoop = panels[pn.PanelIndex].Loops[pn.LoopIndex];
            int pVert = next.Same3DDirection
                ? pn.IndexInLoop
                : (pn.IndexInLoop + 1) % pLoop.HalfEdges.Count;
            pLoop.Kept[pVert] = true;
        }
        var prev = loop.HalfEdges[(vertexIdx - 1 + n) % n];
        if (prev.Partner is { } pp)
        {
            var pLoop = panels[pp.PanelIndex].Loops[pp.LoopIndex];
            int pVert = prev.Same3DDirection
                ? (pp.IndexInLoop + 1) % pLoop.HalfEdges.Count
                : pp.IndexInLoop;
            pLoop.Kept[pVert] = true;
        }
    }

    private static void SimplifyChainSingle(PanelData panel, BoundaryLoop loop, int start, int edgeCount, float tol)
    {
        int n = loop.HalfEdges.Count;
        void Recurse(int lo, int hi)
        {
            if (hi - lo <= 1) return;
            var a = panel.Pos2D[loop.HalfEdges[(start + lo) % n].From];
            var b = panel.Pos2D[loop.HalfEdges[(start + hi) % n].From];
            int best = -1;
            float bestDist = tol;
            for (int k = lo + 1; k < hi; k++)
            {
                var p = panel.Pos2D[loop.HalfEdges[(start + k) % n].From];
                float d = Outline.DistanceToSegment(p, a, b, out _);
                if (d > bestDist)
                {
                    bestDist = d;
                    best = k;
                }
            }
            if (best > 0)
            {
                loop.Kept[(start + best) % n] = true;
                Recurse(lo, best);
                Recurse(best, hi);
            }
        }
        Recurse(0, edgeCount);
    }

    private static void SimplifyChainPaired(
        PanelData panelA, BoundaryLoop loopA, int startA,
        PanelData panelB, BoundaryLoop loopB, int startB,
        int edgeCount, bool sameDir, float tol)
    {
        int nA = loopA.HalfEdges.Count;
        int nB = loopB.HalfEdges.Count;
        int IndexA(int k) => (startA + k) % nA;
        int IndexB(int k) => sameDir ? (startB + k) % nB : (startB + (edgeCount - k)) % nB;

        void Recurse(int lo, int hi)
        {
            if (hi - lo <= 1) return;
            var a0 = panelA.Pos2D[loopA.HalfEdges[IndexA(lo)].From];
            var a1 = panelA.Pos2D[loopA.HalfEdges[IndexA(hi)].From];
            var b0 = panelB.Pos2D[loopB.HalfEdges[IndexB(lo)].From];
            var b1 = panelB.Pos2D[loopB.HalfEdges[IndexB(hi)].From];

            int best = -1;
            float bestDist = tol;
            for (int k = lo + 1; k < hi; k++)
            {
                var pa = panelA.Pos2D[loopA.HalfEdges[IndexA(k)].From];
                var pb = panelB.Pos2D[loopB.HalfEdges[IndexB(k)].From];
                float d = MathF.Max(
                    Outline.DistanceToSegment(pa, a0, a1, out _),
                    Outline.DistanceToSegment(pb, b0, b1, out _));
                if (d > bestDist)
                {
                    bestDist = d;
                    best = k;
                }
            }

            if (best > 0)
            {
                loopA.Kept[IndexA(best)] = true;
                loopB.Kept[IndexB(best)] = true;
                Recurse(lo, best);
                Recurse(best, hi);
            }
        }
        Recurse(0, edgeCount);
    }

    // ------------------------------------------------------------------------
    // Stage 8: PatternPiece, 3D Placement, 2D Layout, and Auto-Seam creation
    // ------------------------------------------------------------------------

    private static Garment BuildGarment(
        List<PanelData> panels, string defaultName, Document document, MeshImportOptions opts, out int seamCount)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in document.Garments)
        {
            usedNames.Add(g.Name);
            foreach (var gr in g.Groups) usedNames.Add(gr.Name);
            foreach (var p in g.Pieces) usedNames.Add(p.Name);
        }
        foreach (var m in document.Materials)
            usedNames.Add(m.Name);

        string NextUniqueName(string baseName)
        {
            if (usedNames.Add(baseName))
                return baseName;
            for (int i = 2; ; i++)
            {
                string cand = $"{baseName} {i}";
                if (usedNames.Add(cand))
                    return cand;
            }
        }

        string rawGarmentName = !string.IsNullOrWhiteSpace(opts.GarmentName)
            ? opts.GarmentName.Trim()
            : (!string.IsNullOrWhiteSpace(defaultName) ? defaultName.Trim() : "Imported Mesh");

        var garment = new Garment
        {
            Name = NextUniqueName(rawGarmentName),
            ParticleDistance = Math.Clamp(opts.ParticleDistanceCm, 0.3f, 20f),
        };

        var material = new FabricMaterial
        {
            Name = NextUniqueName($"{garment.Name} fabric"),
            Color = "#8EA8C3",
        };
        document.Materials.Add(material);

        // Map every mesh boundary half-edge to its simplified Seamstress EdgeRef.
        var halfEdgeToEdgeRef = new Dictionary<BoundaryHalfEdge, EdgeRef>();
        var simplifiedEdges = new List<(EdgeRef Ref, BoundaryHalfEdge FirstHe)>();

        for (int pIdx = 0; pIdx < panels.Count; pIdx++)
        {
            var panel = panels[pIdx];
            string pieceBase = !string.IsNullOrWhiteSpace(panel.GroupName)
                ? panel.GroupName.Trim()
                : $"Panel {pIdx + 1}";

            var piece = new PatternPiece
            {
                Name = NextUniqueName(pieceBase),
                MaterialId = material.Id,
            };

            int edgeOffset = 0;
            for (int l = 0; l < panel.Loops.Count; l++)
            {
                var loop = panel.Loops[l];
                int n = loop.HalfEdges.Count;
                var kept = CollectTrueIndices(loop.Kept);
                if (kept.Count < 3)
                    continue;

                var pts = new List<PatternPoint>(kept.Count);
                for (int k = 0; k < kept.Count; k++)
                {
                    int start = kept[k];
                    int end = kept[(k + 1) % kept.Count];
                    var pos = panel.Pos2D[loop.HalfEdges[start].From];
                    pts.Add(new PatternPoint(pos.X, pos.Y));

                    var edgeRef = new EdgeRef(piece.Id, edgeOffset + k);
                    simplifiedEdges.Add((edgeRef, loop.HalfEdges[start]));

                    int span = (end - start + n) % n;
                    if (span == 0) span = n;
                    for (int s = 0; s < span; s++)
                        halfEdgeToEdgeRef[loop.HalfEdges[(start + s) % n]] = edgeRef;
                }

                if (l == 0)
                    piece.Points = pts;
                else
                    piece.Holes.Add(pts);

                edgeOffset += pts.Count;
            }

            if (piece.Points.Count < 3)
                continue;

            // Shift local coordinates so the outer outline bounding box starts at (0, 0).
            var (min, _) = Outline.Bounds(piece);
            foreach (var pt in piece.AllPoints())
            {
                pt.Position -= min;
                if (pt.Curve is { } c)
                    pt.Curve = c - min;
            }

            piece.Placement = ComputePlacement(panel);
            panel.Piece = piece;
            garment.Pieces.Add(piece);
        }

        // Lay out pieces cleanly in non-overlapping rows in 2D pattern space.
        LayoutPieces(garment.Pieces, document);

        // Create auto-detected seams with exact 3D-derived Reversed orientation.
        seamCount = 0;
        if (opts.AutoSew)
        {
            var seenSeams = new HashSet<(string, int, string, int)>();
            foreach (var (edgeA, firstHe) in simplifiedEdges)
            {
                if (firstHe.Partner is not { } partnerHe)
                    continue;
                if (!halfEdgeToEdgeRef.TryGetValue(partnerHe, out var edgeB))
                    continue;
                if (edgeA.PieceId == edgeB.PieceId && edgeA.Edge == edgeB.Edge)
                    continue;

                var canon = CanonicalSeamKey(edgeA, edgeB);
                if (!seenSeams.Add(canon))
                    continue;

                garment.Seams.Add(new Seam
                {
                    A = edgeA.Clone(),
                    B = edgeB.Clone(),
                    Reversed = firstHe.Same3DDirection,
                });
                seamCount++;
            }
        }

        return garment;
    }

    private static Placement ComputePlacement(PanelData panel)
    {
        int triCount = panel.Triangles.Length / 3;
        Vector3 centroid = Vector3.Zero;
        double totalArea = 0;
        Vector3 sumU = Vector3.Zero;
        Vector3 sumV = Vector3.Zero;
        Vector3 sumN = Vector3.Zero;

        for (int t = 0; t < triCount; t++)
        {
            int ia = panel.Triangles[3 * t];
            int ib = panel.Triangles[3 * t + 1];
            int ic = panel.Triangles[3 * t + 2];

            var p0 = panel.Pos3D[ia];
            var p1 = panel.Pos3D[ib];
            var p2 = panel.Pos3D[ic];
            var e1 = p1 - p0;
            var e2 = p2 - p0;
            var cross = Vector3.Cross(e1, e2);
            float area = 0.5f * cross.Length();
            centroid += (p0 + p1 + p2) * (area / 3f);
            totalArea += area;
            sumN += cross;

            var u0 = panel.Pos2D[ia];
            var u1 = panel.Pos2D[ib];
            var u2 = panel.Pos2D[ic];
            float du1 = u1.X - u0.X, dv1 = u1.Y - u0.Y;
            float du2 = u2.X - u0.X, dv2 = u2.Y - u0.Y;

            // Area-weighted tangent vectors dP/du and dP/dv (det cancels when weighting by 2*area2D).
            sumU += dv2 * e1 - dv1 * e2;
            sumV += -du2 * e1 + du1 * e2;
        }

        if (totalArea > 1e-12)
            centroid /= (float)totalArea;
        else if (panel.Pos3D.Length > 0)
            centroid = panel.Pos3D.Aggregate(Vector3.Zero, (s, p) => s + p) / panel.Pos3D.Length;

        Vector3 normal = sumN.LengthSquared() > 1e-12f
            ? Vector3.Normalize(sumN)
            : (Vector3.Cross(sumU, sumV).LengthSquared() > 1e-12f
                ? Vector3.Normalize(Vector3.Cross(sumU, sumV))
                : Vector3.UnitZ);

        // Project accumulated 2D-to-3D tangents onto the plane orthogonal to normal and build closest orthonormal frame.
        Vector3 uProj = sumU - Vector3.Dot(sumU, normal) * normal;
        Vector3 vProj = sumV - Vector3.Dot(sumV, normal) * normal;

        Vector3 uHat = uProj.LengthSquared() > 1e-12f ? Vector3.Normalize(uProj) : Vector3.Zero;
        Vector3 vHat = vProj.LengthSquared() > 1e-12f ? Vector3.Normalize(vProj) : Vector3.Zero;

        Vector3 uBlend = uHat + (vHat.LengthSquared() > 0.5f ? Vector3.Cross(vHat, normal) : Vector3.Zero);
        Vector3 right = uBlend.LengthSquared() > 1e-8f
            ? Vector3.Normalize(uBlend)
            : (uHat.LengthSquared() > 0.5f ? uHat : OrthogonalUnit(normal));
        Vector3 up = Vector3.Normalize(Vector3.Cross(normal, right));
        right = Vector3.Normalize(Vector3.Cross(up, normal));

        var rotMatrix = new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            normal.X, normal.Y, normal.Z, 0,
            0, 0, 0, 1);

        return new Placement
        {
            Position = centroid,
            Rotation = Workspace.ToEuler(rotMatrix),
            Curvature = 0f,
        };
    }

    private static void LayoutPieces(List<PatternPiece> pieces, Document document)
    {
        if (pieces.Count == 0)
            return;

        float startY = 0f;
        foreach (var existing in document.AllPieces())
        {
            var (_, max) = Outline.Bounds(existing);
            startY = MathF.Max(startY, max.Y + existing.Origin.Y + 15f);
        }

        double totalArea = 0;
        var sizes = new Vector2[pieces.Count];
        for (int i = 0; i < pieces.Count; i++)
        {
            var (min, max) = Outline.Bounds(pieces[i]);
            sizes[i] = max - min;
            totalArea += (double)(sizes[i].X + LayoutPaddingCm) * (sizes[i].Y + LayoutPaddingCm);
        }

        float maxRowWidth = MathF.Max(160f, (float)Math.Sqrt(totalArea) * 1.8f);
        float cursorX = 0f;
        float cursorY = startY;
        float rowHeight = 0f;

        for (int i = 0; i < pieces.Count; i++)
        {
            float w = sizes[i].X;
            float h = sizes[i].Y;
            if (cursorX > 0f && cursorX + w > maxRowWidth)
            {
                cursorX = 0f;
                cursorY += rowHeight + LayoutPaddingCm;
                rowHeight = 0f;
            }
            pieces[i].Origin = new Vector2(cursorX, cursorY);
            cursorX += w + LayoutPaddingCm;
            rowHeight = MathF.Max(rowHeight, h);
        }
    }

    // ------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------

    private static long UndirectedKey(int a, int b)
        => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static (string, int, string, int) CanonicalSeamKey(EdgeRef a, EdgeRef b)
    {
        int cmp = string.CompareOrdinal(a.PieceId, b.PieceId);
        if (cmp < 0 || (cmp == 0 && a.Edge < b.Edge))
            return (a.PieceId, a.Edge, b.PieceId, b.Edge);
        return (b.PieceId, b.Edge, a.PieceId, a.Edge);
    }

    private static int CountTrue(bool[] flags)
    {
        int count = 0;
        for (int i = 0; i < flags.Length; i++)
            if (flags[i]) count++;
        return count;
    }

    private static List<int> CollectTrueIndices(bool[] flags)
    {
        var list = new List<int>();
        for (int i = 0; i < flags.Length; i++)
            if (flags[i]) list.Add(i);
        return list;
    }
}
