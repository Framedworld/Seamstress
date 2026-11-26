using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Editing;

/// <summary>What switching quality carried over to the other meshes.</summary>
/// <param name="Pieces">Pieces whose mesh changed.</param>
/// <param name="Draped">Of those, the pieces whose draped shape was carried across.</param>
/// <param name="Pinned">Pieces whose pins were carried across.</param>
/// <param name="Creased">Pieces whose creases were carried across (approximately: folds finer than the coarser mesh are lost).</param>
public readonly record struct QualityChange(int Pieces, int Draped, int Pinned, int Creased);

public sealed partial class Workspace
{
    public SimulationQuality Quality => Document.Simulation.Quality;

    /// <summary>
    /// Switches between Draft (every garment meshed no finer than the draft particle distance, for quick work on big
    /// outfits) and Final (each garment's own particle distance), as one undoable step. Drapes, pins and creases belong
    /// to a mesh, so they are carried to the new one through the 2D pattern: every new vertex's pattern point lies in a
    /// triangle of the old mesh, whose 3D positions are interpolated there. Pins go to the nearest pattern points.
    /// </summary>
    public QualityChange SetQuality(SimulationQuality quality)
    {
        if (Document.Simulation.Quality == quality)
            return default;
        return ChangeMeshes(quality == SimulationQuality.Draft ? Lang.T("Switch to Draft quality") : Lang.T("Refine to Final quality"),
            doc => doc.Simulation.Quality = quality);
    }

    /// <summary>
    /// Sets how coarse Draft meshes are, in centimetres. At Draft the garments are meshed again and keep their shape,
    /// as when switching quality; consecutive changes (a slider drag) are one undo step.
    /// </summary>
    public QualityChange SetDraftParticleDistance(float centimetres)
    {
        if (!float.IsFinite(centimetres))
            return default;
        float value = Math.Clamp(centimetres, 0.3f, 20f);
        if (MathF.Abs(Document.Simulation.DraftParticleDistance - value) < 1e-4f)
            return default;
        return ChangeMeshes(Lang.T("Change draft particle distance"), doc => doc.Simulation.DraftParticleDistance = value,
            "sim:draftParticleDistance");
    }

    /// <summary>
    /// Makes a change that re-meshes garments (quality, the draft distance) as one undo step, carrying every piece's
    /// drape, creases and pins from its old mesh to its new one.
    /// </summary>
    private QualityChange ChangeMeshes(string name, Action<Document> change, string? mergeKey = null)
    {
        var result = default(QualityChange);
        History.Execute(name, doc =>
        {
            // Everything as it is on the meshes in use now, before they change.
            var before = new Dictionary<string, PieceState>();
            foreach (var garment in doc.Garments.Where(g => g.Pieces.Count > 0))
            {
                var mesh = MeshFor(garment);
                foreach (var pieceMesh in mesh.Pieces)
                {
                    if (garment.FindPiece(pieceMesh.PieceId) is { } piece)
                        before[piece.Id] = PieceState.Of(piece, pieceMesh);
                }
            }

            change(doc);

            int pieces = 0, draped = 0, pinned = 0, creased = 0;
            foreach (var garment in doc.Garments.Where(g => g.Pieces.Count > 0))
            {
                var mesh = MeshFor(garment);
                foreach (var pieceMesh in mesh.Pieces)
                {
                    if (garment.FindPiece(pieceMesh.PieceId) is not { } piece || !before.TryGetValue(piece.Id, out var old)
                        || old.Mesh.Key == pieceMesh.Key)
                        continue; // the same mesh at both qualities (already coarse, or a hard part): nothing to carry
                    pieces++;
                    if (old.Drape != null)
                    {
                        piece.Drape = Transfer(old.Mesh, old.Drape, pieceMesh);
                        piece.DrapeKey = pieceMesh.Key;
                        draped++;
                    }
                    if (old.Crease != null)
                    {
                        piece.Crease = Transfer(old.Mesh, old.Crease, pieceMesh);
                        piece.CreaseKey = pieceMesh.Key;
                        piece.CreaseStrength = old.CreaseStrength;
                        creased++;
                    }
                    if (old.Pins.Length > 0)
                    {
                        piece.Pins = TransferPins(old.Mesh, old.Pins, pieceMesh);
                        piece.PinKey = pieceMesh.Key;
                        pinned++;
                    }
                }
            }
            result = new QualityChange(pieces, draped, pinned, creased);
        }, mergeKey);
        return result;
    }

    /// <summary>A piece's mesh-bound state: its drape, creases and pins, each only if it is valid for that mesh.</summary>
    private sealed record PieceState(PieceMesh Mesh, Vector3[]? Drape, Vector3[]? Crease, float CreaseStrength, int[] Pins)
    {
        public static PieceState Of(PatternPiece piece, PieceMesh mesh)
        {
            Vector3[]? drape = piece.Drape != null && piece.DrapeKey == mesh.Key && piece.Drape.Length == mesh.VertexCount * 3
                ? Mesher.CurrentPositions(piece, mesh) : null;
            Vector3[]? crease = piece.HasCreasesFor(mesh.Key, mesh.VertexCount) ? Vectors(piece.Crease!) : null;
            int[] pins = piece.PinKey == mesh.Key
                ? piece.Pins.Where(i => i >= 0 && i < mesh.VertexCount).ToArray()
                : Array.Empty<int>();
            return new PieceState(mesh, drape, crease, piece.CreaseStrength, pins);
        }
    }

    private static Vector3[] Vectors(float[] xyz)
    {
        var result = new Vector3[xyz.Length / 3];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Vector3(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]);
        return result;
    }

    /// <summary>
    /// The 3D shape of <paramref name="from"/> (one position per vertex) carried to the mesh <paramref name="to"/> of
    /// the same piece: each of its vertices takes the position interpolated at its pattern point in the old mesh.
    /// </summary>
    internal static float[] Transfer(PieceMesh from, Vector3[] positions, PieceMesh to)
    {
        var sampler = new SurfaceSampler(from, positions, Mesher.ComputeNormals(positions, from.Triangles));
        var result = new float[to.VertexCount * 3];
        for (int i = 0; i < to.VertexCount; i++)
        {
            var (p, _) = sampler.At(to.Points[i]);
            result[3 * i] = p.X;
            result[3 * i + 1] = p.Y;
            result[3 * i + 2] = p.Z;
        }
        return result;
    }

    /// <summary>
    /// Pins carried to another mesh of the same piece by nearest pattern point, both ways: a new vertex is pinned when
    /// the old vertex nearest to it was, and the new vertex nearest to each old pin is pinned too, so a pinned area
    /// stays pinned on a finer mesh and a single pin is not lost on a coarser one.
    /// </summary>
    internal static HashSet<int> TransferPins(PieceMesh from, int[] pins, PieceMesh to)
    {
        var oldGrid = new NearestPoint(from.Points);
        var newGrid = new NearestPoint(to.Points);
        var pinnedBefore = pins.ToHashSet();
        var result = new HashSet<int>();
        for (int i = 0; i < to.VertexCount; i++)
        {
            if (pinnedBefore.Contains(oldGrid.Find(to.Points[i])))
                result.Add(i);
        }
        foreach (int pin in pinnedBefore)
            result.Add(newGrid.Find(from.Points[pin]));
        result.Remove(-1);
        return result;
    }

    /// <summary>Nearest of a set of 2D points, through a uniform grid.</summary>
    private sealed class NearestPoint
    {
        private readonly Vector2[] _points;
        private readonly Dictionary<(int, int), List<int>> _cells = new();
        private readonly Vector2 _min;
        private readonly float _cell;

        public NearestPoint(Vector2[] points)
        {
            _points = points;
            if (points.Length == 0)
                return;
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (var p in points)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
            _min = min;
            _cell = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y) / MathF.Max(1, MathF.Sqrt(points.Length)), 1e-3f);
            for (int i = 0; i < points.Length; i++)
            {
                var key = Cell(points[i]);
                if (!_cells.TryGetValue(key, out var list))
                    _cells[key] = list = new List<int>();
                list.Add(i);
            }
        }

        private (int X, int Y) Cell(Vector2 p) => ((int)MathF.Floor((p.X - _min.X) / _cell), (int)MathF.Floor((p.Y - _min.Y) / _cell));

        public int Find(Vector2 at)
        {
            if (_points.Length == 0)
                return -1;
            var (cx, cy) = Cell(at);
            int best = -1;
            float bestDistance = float.MaxValue;
            // Widen the search ring by ring; once something is found, one more ring settles it, since a point in a
            // further ring is at least (ring - 1) cells away.
            for (int ring = 0; ring < 4096; ring++)
            {
                for (int x = cx - ring; x <= cx + ring; x++)
                {
                    for (int y = cy - ring; y <= cy + ring; y++)
                    {
                        if (Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) != ring || !_cells.TryGetValue((x, y), out var list))
                            continue;
                        foreach (int i in list)
                        {
                            float d = Vector2.DistanceSquared(_points[i], at);
                            if (d < bestDistance)
                                (best, bestDistance) = (i, d);
                        }
                    }
                }
                if (best >= 0 && MathF.Sqrt(bestDistance) <= ring * _cell)
                    break;
            }
            return best;
        }
    }
}
