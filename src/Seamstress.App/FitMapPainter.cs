using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.App;

/// <summary>
/// Colours the 3D view's cloth by a fit map (<see cref="FitMaps"/>) while it is on: each time a piece's mesh is built
/// for the view, its map values are worked out from the positions being shown and handed over as a vertex colour.
/// Edge rest lengths are kept per mesh and the buffers are reused, so a frame of a live simulation costs one pass
/// over the edges and, for the body maps, one look-up in the body's distance field per vertex.
/// </summary>
internal sealed class FitMapPainter
{
    private readonly Dictionary<string, PieceEdges> _edges = new();
    private readonly Dictionary<string, (float Min, float Max)> _ranges = new();
    // Per piece, how many of its vertices fall in each of Bins steps of the map's range (the ends take what lies
    // beyond): the legend reports the 1st and 99th percentile, so a handful of extreme vertices (a pinched corner,
    // a seam pulled tight at one point) do not stand for the whole garment.
    private const int Bins = 256;
    private readonly Dictionary<string, int[]> _histograms = new();
    private readonly HashSet<string> _shown = new();
    private float[] _values = Array.Empty<float>();
    private float[] _scratch = Array.Empty<float>();
    private Vector4[] _colours = Array.Empty<Vector4>();
    private Vector4[] _sheet = Array.Empty<Vector4>();

    public FitMapKind Kind { get; set; }

    public bool On => Kind != FitMapKind.Off;

    /// <summary>
    /// What a piece's colours depend on besides its positions, for the view's rebuild check: the map, and for the
    /// body maps the body and how far the garment is held off it; for stretch, the fabric's shrink.
    /// </summary>
    public int Signature(Garment garment, PatternPiece piece, Avatar? avatar)
    {
        if (!On)
            return 0;
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(piece.Physics.ShrinkWeft);
        hash.Add(piece.Physics.ShrinkWarp);
        if (Kind != FitMapKind.Stretch)
        {
            hash.Add(avatar == null ? 0 : RuntimeHelpers.GetHashCode(avatar.Sdf));
            hash.Add(FitMaps.ContactDistance(garment, piece));
        }
        return hash.ToHashCode();
    }

    /// <summary>A new view update starts: the legend's range is taken over the pieces shown in it.</summary>
    public void BeginUpdate() => _shown.Clear();

    /// <summary>A piece is shown in this update (whether or not its mesh was rebuilt).</summary>
    public void Shown(string pieceId) => _shown.Add(pieceId);

    /// <summary>
    /// The colour of every vertex of a piece's shell, or null with the map off. The array is reused by the next call,
    /// so it must be handed over before then.
    /// </summary>
    public Vector4[]? Colours(Garment garment, PatternPiece piece, PieceMesh mesh, Vector3[] positions, SurfaceShell shell, Avatar? avatar)
    {
        if (!On)
            return null;
        if (!_edges.TryGetValue(piece.Id, out var edges) || edges.Key != mesh.Key
            || edges.ShrinkWeft != piece.Physics.ShrinkWeft || edges.ShrinkWarp != piece.Physics.ShrinkWarp)
            _edges[piece.Id] = edges = FitMaps.Edges(mesh, piece.Physics.ShrinkWeft, piece.Physics.ShrinkWarp);
        int n = positions.Length;
        if (_values.Length < n)
        {
            _values = new float[n];
            _scratch = new float[n];
        }
        var values = _values.AsSpan(0, n);
        FitMaps.Values(Kind, edges, positions, avatar?.Sdf, FitMaps.ContactDistance(garment, piece), values, _scratch.AsSpan(0, n));
        float min = float.MaxValue, max = float.MinValue;
        foreach (float v in values)
        {
            if (!float.IsFinite(v))
                continue;
            min = MathF.Min(min, v);
            max = MathF.Max(max, v);
        }
        _ranges[piece.Id] = (min, max);
        if (!_histograms.TryGetValue(piece.Id, out var histogram))
            _histograms[piece.Id] = histogram = new int[Bins];
        Array.Clear(histogram);
        var (low, high) = Span(Kind);
        foreach (float v in values)
        {
            int bin = float.IsFinite(v) ? (int)((v - low) / (high - low) * Bins) : Bins - 1;
            histogram[Math.Clamp(bin, 0, Bins - 1)]++;
        }

        if (_colours.Length < shell.Positions.Length)
            _colours = new Vector4[shell.Positions.Length];
        if (_sheet.Length < n)
            _sheet = new Vector4[n];
        var sheet = _sheet;
        for (int i = 0; i < n; i++)
            sheet[i] = new Vector4(FitMaps.Colour(Kind, values[i]), 1f);
        var sources = shell.Sources;
        for (int i = 0; i < shell.Positions.Length; i++)
            _colours[i] = i < sources.Length && sources[i] < n ? sheet[sources[i]] : Vector4.One;
        return _colours;
    }

    /// <summary>Forgets the pieces that are gone.</summary>
    public void Remove(string pieceId)
    {
        _edges.Remove(pieceId);
        _ranges.Remove(pieceId);
        _histograms.Remove(pieceId);
    }

    /// <summary>The range the legend's percentiles are measured over: strain, pressure, centimetres.</summary>
    private static (float Low, float High) Span(FitMapKind kind) => kind switch
    {
        FitMapKind.Stretch => (-0.3f, 0.7f),
        FitMapKind.Distance => (0f, 2 * FitMaps.LooseCm),
        _ => (0f, 1f),
    };

    /// <summary>The map and the range of its values over the shown pieces, as the JSON the 3D view's legend reads.</summary>
    public void AppendJson(StringBuilder sb)
    {
        var inv = CultureInfo.InvariantCulture;
        sb.Append(",\"fitMap\":{\"kind\":\"").Append(FitMaps.Name(Kind)).Append('"');
        if (On)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (string id in _shown)
            {
                if (_ranges.TryGetValue(id, out var range))
                {
                    min = MathF.Min(min, range.Min);
                    max = MathF.Max(max, range.Max);
                }
            }
            if (min <= max)
                sb.Append(inv, $",\"min\":{min:0.####},\"max\":{max:0.####}");
            var total = new long[Bins];
            long count = 0;
            foreach (string id in _shown)
            {
                if (!_histograms.TryGetValue(id, out var histogram))
                    continue;
                for (int i = 0; i < Bins; i++)
                    total[i] += histogram[i];
                count += histogram.Sum();
            }
            if (count > 0)
            {
                var (low, high) = Span(Kind);
                float At(double share)
                {
                    long seen = 0, wanted = (long)Math.Ceiling(share * count);
                    for (int i = 0; i < Bins; i++)
                    {
                        seen += total[i];
                        if (seen >= Math.Max(wanted, 1))
                            return low + (high - low) * (i + 0.5f) / Bins;
                    }
                    return high;
                }
                sb.Append(inv, $",\"low\":{At(0.01):0.####},\"high\":{At(0.99):0.####},\"top\":{high:0.####}");
            }
            sb.Append(",\"colours\":[").Append(string.Join(",", FitMaps.LegendColours(Kind).Select(c => "\"" + c + "\""))).Append(']');
        }
        sb.Append('}');
    }
}
