using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Simulation;

/// <summary>What the 3D view colours the cloth by.</summary>
public enum FitMapKind
{
    Off,
    /// <summary>How far the cloth is stretched or compressed against its pattern: per vertex, the mean strain of its edges.</summary>
    Stretch,
    /// <summary>How hard the cloth presses on the body: in contact, and how taut it is there.</summary>
    Pressure,
    /// <summary>How far the cloth stands off the body, beyond the room the garment keeps (its thickness and room underneath).</summary>
    Distance,
}

/// <summary>The edges of a piece's mesh and the lengths they have at rest, in metres.</summary>
public sealed class PieceEdges
{
    public required int[] A { get; init; }
    public required int[] B { get; init; }
    public required float[] Rest { get; init; }
    /// <summary>One over the number of edges at each vertex (0 for a vertex on no edge).</summary>
    public required float[] InverseValence { get; init; }
    public required string Key { get; init; }
    public float ShrinkWeft { get; init; }
    public float ShrinkWarp { get; init; }
}

/// <summary>One of the worst places of a garment for a map: where it is and how bad.</summary>
public sealed record FitHotSpot(string PieceId, string PieceName, float Value, Vector3 Position, Vector2 PatternCm, string Region);
// PatternCm is in the piece's local pattern space, as locate_on_piece takes it.

/// <summary>A map's numbers for one garment.</summary>
public sealed record GarmentFitSummary(string GarmentId, string GarmentName, FitMapKind Kind, int Vertices, float Min, float Max,
    float Mean, float ShareOver, float ShareUnder, IReadOnlyList<FitHotSpot> Worst);

/// <summary>
/// Fit maps, as Marvelous Designer shows them: the cloth coloured by how it is stretched, how hard it presses on the body,
/// or how far it stands off it. The numbers come from the mesh as it is drawn, so they follow the cloth live while it
/// simulates; the 3D view colours its vertices with them and the <c>fit_map</c> tool sums them up for agents.
/// </summary>
public static class FitMaps
{
    /// <summary>Strain at which the stretch map is fully red (and, negated, fully blue): about 10%.</summary>
    public const float FullStrain = 0.10f;

    /// <summary>The solver's skin offset: cloth is held this far off the body on top of its thickness and room underneath.</summary>
    public const float SkinOffsetMetres = 0.002f;

    /// <summary>Within this distance of where the solver holds it off the body, cloth counts as touching (fading out to it).</summary>
    public const float ContactBandMetres = 0.005f;

    /// <summary>The distance map's far end: cloth this far off the body, or further, is "loose".</summary>
    public const float LooseCm = 5f;

    /// <summary>Strain above which a vertex counts as overstretched in a summary, and below which (negated) as compressed.</summary>
    public const float OverStrain = 0.05f;

    /// <summary>Pressure above which a vertex counts as tight in a summary.</summary>
    public const float TightPressure = 0.6f;

    /// <summary>Distance (cm) under which a vertex counts as touching in a summary, and over which (×4) as loose.</summary>
    public const float TouchingCm = 0.5f;

    public static FitMapKind Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "stretch" or "strain" => FitMapKind.Stretch,
        "pressure" or "stress" => FitMapKind.Pressure,
        "distance" or "fit" => FitMapKind.Distance,
        _ => FitMapKind.Off,
    };

    public static string Name(FitMapKind kind) => kind switch
    {
        FitMapKind.Stretch => "stretch",
        FitMapKind.Pressure => "pressure",
        FitMapKind.Distance => "distance",
        _ => "off",
    };

    /// <summary>
    /// The unique edges of a piece's triangles with their rest lengths: the pattern's lengths after the fabric's
    /// shrink factors (weft across, warp along), as the solver builds its stretch constraints.
    /// </summary>
    public static PieceEdges Edges(PieceMesh mesh, float shrinkWeft = 1f, float shrinkWarp = 1f)
    {
        float sx = Math.Clamp(shrinkWeft, 0.5f, 1.5f), sy = Math.Clamp(shrinkWarp, 0.5f, 1.5f);
        var seen = new HashSet<long>(EdgeKey.Comparer);
        var a = new List<int>(mesh.Triangles.Length);
        var b = new List<int>(mesh.Triangles.Length);
        var rest = new List<float>(mesh.Triangles.Length);
        var valence = new int[mesh.VertexCount];
        for (int t = 0; t + 2 < mesh.Triangles.Length; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                int i = mesh.Triangles[t + k], j = mesh.Triangles[t + (k + 1) % 3];
                long key = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
                if (!seen.Add(key))
                    continue;
                var d = mesh.Points[j] - mesh.Points[i];
                float length = new Vector2(d.X * sx, d.Y * sy).Length() * 0.01f;
                if (length < 1e-6f)
                    continue;
                a.Add(i);
                b.Add(j);
                rest.Add(length);
                valence[i]++;
                valence[j]++;
            }
        }
        var inverse = new float[valence.Length];
        for (int i = 0; i < valence.Length; i++)
            inverse[i] = valence[i] > 0 ? 1f / valence[i] : 0f;
        return new PieceEdges
        {
            A = a.ToArray(), B = b.ToArray(), Rest = rest.ToArray(), InverseValence = inverse, Key = mesh.Key,
            ShrinkWeft = shrinkWeft, ShrinkWarp = shrinkWarp,
        };
    }

    /// <summary>
    /// Strain at each vertex: the mean of (length / rest length − 1) over its edges. Positive is stretched, negative
    /// compressed; 0.1 is 10% longer than the pattern.
    /// </summary>
    public static void Strain(PieceEdges edges, ReadOnlySpan<Vector3> positions, Span<float> strain)
    {
        strain.Clear();
        int[] a = edges.A, b = edges.B;
        float[] rest = edges.Rest;
        for (int e = 0; e < a.Length; e++)
        {
            int i = a[e], j = b[e];
            if (i >= positions.Length || j >= positions.Length)
                continue;
            float s = Vector3.Distance(positions[i], positions[j]) / rest[e] - 1f;
            strain[i] += s;
            strain[j] += s;
        }
        float[] inverse = edges.InverseValence;
        int n = Math.Min(strain.Length, inverse.Length);
        for (int i = 0; i < n; i++)
            strain[i] *= inverse[i];
    }

    /// <summary>
    /// How far from the body's surface the solver holds a piece's cloth, in metres: its collision thickness, the
    /// garment's room underneath and the skin offset.
    /// </summary>
    public static float ContactDistance(Garment garment, PatternPiece piece)
        => ContactDistance(garment.EffectiveThicknessMm(piece), garment.BodyOffset);

    public static float ContactDistance(float thicknessMm, float bodyOffsetMm)
        => (MathF.Max(thicknessMm, 0.1f) + MathF.Max(bodyOffsetMm, 0f)) * 0.001f + SkinOffsetMetres;

    /// <summary>
    /// The air between the cloth and the body at each vertex, in metres: its distance from the body's surface less
    /// the contact distance, so 0 is as close as the garment can come. No body, or a point beyond the body's field,
    /// gives <see cref="float.PositiveInfinity"/>.
    /// </summary>
    public static void Gap(SdfGrid? body, ReadOnlySpan<Vector3> positions, float contactDistance, Span<float> gap)
    {
        for (int i = 0; i < positions.Length && i < gap.Length; i++)
        {
            float d = body?.Sample(positions[i], out _) ?? float.MaxValue;
            gap[i] = d >= float.MaxValue * 0.5f ? float.PositiveInfinity : d - contactDistance;
        }
    }

    /// <summary>
    /// How hard the cloth presses on the body, from 0 (not touching) to 1 (tight). Cloth presses where it touches
    /// (within <see cref="ContactBandMetres"/> of where the solver holds it, fading out over that band) and presses
    /// the harder the more it is stretched there, as a taut band does: touching but slack gives 0.25, touching and
    /// stretched by <see cref="FullStrain"/> or more gives 1. Cloth pushed inside its contact distance (squeezed
    /// between the body and an outer layer, or by a pin) counts as tight in proportion.
    /// </summary>
    public static float Pressure(float gapMetres, float strain)
    {
        if (!float.IsFinite(gapMetres))
            return 0f;
        float contact = Math.Clamp(1f - gapMetres / ContactBandMetres, 0f, 1f);
        float tension = Math.Clamp(strain / FullStrain, 0f, 1f);
        float squeezed = Math.Clamp(-gapMetres / ContactBandMetres, 0f, 1f);
        return MathF.Max(contact * (0.25f + 0.75f * tension), squeezed);
    }

    /// <summary>The distance map's value: the air under the cloth in centimetres (<see cref="LooseCm"/> or more is loose).</summary>
    public static float DistanceCm(float gapMetres) => float.IsFinite(gapMetres) ? MathF.Max(gapMetres, 0f) * 100f : float.PositiveInfinity;

    /// <summary>
    /// A map's value at every vertex of one piece: strain, pressure (0..1) or distance (cm). The buffers are the
    /// caller's so that a frame of a live simulation allocates nothing.
    /// </summary>
    public static void Values(FitMapKind kind, PieceEdges edges, ReadOnlySpan<Vector3> positions, SdfGrid? body, float contactDistance,
        Span<float> values, Span<float> scratch)
    {
        switch (kind)
        {
            case FitMapKind.Stretch:
                Strain(edges, positions, values);
                break;
            case FitMapKind.Distance:
                Gap(body, positions, contactDistance, values);
                for (int i = 0; i < positions.Length; i++)
                    values[i] = DistanceCm(values[i]);
                break;
            case FitMapKind.Pressure:
                Strain(edges, positions, scratch);
                Gap(body, positions, contactDistance, values);
                for (int i = 0; i < positions.Length; i++)
                    values[i] = Pressure(values[i], scratch[i]);
                break;
            default:
                values.Clear();
                break;
        }
    }

    // ---------------------------------------------------------------- colours

    private static readonly Vector3 Blue = Srgb(0x2f, 0x6f, 0xe4), Cyan = Srgb(0x22, 0xb8, 0xcf), Green = Srgb(0x37, 0xb3, 0x4a),
        Yellow = Srgb(0xf2, 0xc9, 0x1d), Red = Srgb(0xe0, 0x30, 0x2c), Slate = Srgb(0x5a, 0x66, 0x78), Orange = Srgb(0xf0, 0x80, 0x24);

    /// <summary>The legend colours of each map from its low end to its high end, as #rrggbb, for the interface: five stops, evenly spaced.</summary>
    public static string[] LegendColours(FitMapKind kind) => kind switch
    {
        FitMapKind.Stretch => new[] { "#2f6fe4", "#22b8cf", "#37b34a", "#f2c91d", "#e0302c" },
        FitMapKind.Pressure => new[] { "#5a6678", "#37b34a", "#f2c91d", "#f08024", "#e0302c" },
        FitMapKind.Distance => new[] { "#e0302c", "#f2c91d", "#37b34a", "#22b8cf", "#2f6fe4" },
        _ => Array.Empty<string>(),
    };

    private static Vector3 Srgb(int r, int g, int b) => new(Linear(r / 255f), Linear(g / 255f), Linear(b / 255f));

    private static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static Vector3 Ramp(ReadOnlySpan<Vector3> stops, float t)
    {
        t = Math.Clamp(float.IsNaN(t) ? 0f : t, 0f, 1f) * (stops.Length - 1);
        int i = Math.Min((int)t, stops.Length - 2);
        return Vector3.Lerp(stops[i], stops[i + 1], t - i);
    }

    /// <summary>
    /// The colour (linear RGB, as the renderer takes vertex colours) of a map's value. Stretch: blue at 10% compressed,
    /// green at rest, yellow at 5%, red at 10% stretched or more. Pressure: grey (free), green touching but slack (0.25),
    /// then yellow and orange to red (tight). Distance: red touching, yellow at 1 cm, green at 2.5 cm, blue at 5 cm or more.
    /// </summary>
    public static Vector3 Colour(FitMapKind kind, float value)
    {
        switch (kind)
        {
            case FitMapKind.Stretch:
                return Ramp(stackalloc[] { Blue, Cyan, Green, Yellow, Red }, 0.5f + 0.5f * value / FullStrain);
            case FitMapKind.Pressure:
                return Ramp(stackalloc[] { Slate, Green, Yellow, Orange, Red }, value);
            case FitMapKind.Distance:
                if (!float.IsFinite(value))
                    return Blue;
                // Stops at 0, 1, 2.5, 3.75 and 5 cm: the first centimetre is where fit is judged, so it gets the most colour.
                float t = value <= 1f ? value * 0.25f : value <= 2.5f ? 0.25f + (value - 1f) / 1.5f * 0.25f : 0.5f + (value - 2.5f) / (LooseCm - 2.5f) * 0.5f;
                return Ramp(stackalloc[] { Red, Yellow, Green, Cyan, Blue }, t);
            default:
                return Vector3.One;
        }
    }

    // ---------------------------------------------------------------- summaries for agents

    /// <summary>
    /// A map's numbers for each visible garment (or one): the range and mean of its values, how much of it is over
    /// (stretched, tight or loose) or under (compressed or touching) the map's thresholds, and its worst places, at
    /// least <paramref name="spacingCm"/> apart, with where they are on the body and on the pattern.
    /// </summary>
    public static List<GarmentFitSummary> Summarize(Document doc, Func<Garment, GarmentMesh> meshFor, Avatar? avatar, FitMapKind kind,
        string? garmentId = null, int worst = 5, float spacingCm = 8f)
    {
        var result = new List<GarmentFitSummary>();
        if (kind == FitMapKind.Off)
            return result;
        var regions = avatar != null ? new BodyRegions(avatar.Mesh) : null;
        foreach (var garment in doc.Garments)
        {
            if (garmentId != null ? garment.Id != garmentId : !garment.Visible)
                continue;
            var mesh = meshFor(garment);
            var candidates = new List<(float Score, float Value, int Piece, int Vertex, Vector3 Position)>();
            double sum = 0;
            int count = 0, finite = 0, over = 0, under = 0;
            float min = float.MaxValue, max = float.MinValue;
            var positions = new Vector3[mesh.Pieces.Count][];
            for (int p = 0; p < mesh.Pieces.Count; p++)
            {
                var pieceMesh = mesh.Pieces[p];
                var piece = garment.FindPiece(pieceMesh.PieceId);
                if (piece == null || pieceMesh.VertexCount == 0 || !Editing.Workspace.IsEffectivelyVisible(garment, piece))
                    continue;
                var shown = positions[p] = Mesher.CurrentPositions(piece, pieceMesh);
                var values = new float[shown.Length];
                var scratch = new float[shown.Length];
                Values(kind, Edges(pieceMesh, piece.Physics.ShrinkWeft, piece.Physics.ShrinkWarp), shown, avatar?.Sdf,
                    ContactDistance(garment, piece), values, scratch);
                for (int i = 0; i < values.Length; i++)
                {
                    float v = values[i];
                    if (!float.IsFinite(v))
                    {
                        // Beyond the body's field (or no body): as loose as cloth gets, but no number to average.
                        if (kind == FitMapKind.Distance)
                        {
                            count++;
                            over++;
                        }
                        continue;
                    }
                    count++;
                    finite++;
                    sum += v;
                    min = MathF.Min(min, v);
                    max = MathF.Max(max, v);
                    switch (kind)
                    {
                        case FitMapKind.Stretch:
                            if (v > OverStrain) over++;
                            if (v < -OverStrain) under++;
                            break;
                        case FitMapKind.Pressure:
                            if (v > TightPressure) over++;
                            if (v <= 0f) under++;
                            break;
                        case FitMapKind.Distance:
                            if (v > TouchingCm * 4) over++;
                            if (v < TouchingCm) under++;
                            break;
                    }
                    // Worst: most stretched, tightest, or (distance) loosest; compressed cloth also ranks on the stretch map.
                    float score = kind == FitMapKind.Stretch ? MathF.Max(v, -v * 0.5f) : v;
                    candidates.Add((score, v, p, i, shown[i]));
                }
            }
            if (count == 0)
                continue;
            candidates.Sort((x, y) => y.Score.CompareTo(x.Score));
            var spots = new List<FitHotSpot>();
            float spacing = spacingCm * 0.01f;
            foreach (var c in candidates)
            {
                if (spots.Count >= worst)
                    break;
                if (spots.Any(s => Vector3.Distance(s.Position, c.Position) < spacing))
                    continue;
                var pieceMesh = mesh.Pieces[c.Piece];
                var piece = garment.FindPiece(pieceMesh.PieceId)!;
                spots.Add(new FitHotSpot(piece.Id, piece.Name, c.Value, c.Position, pieceMesh.Points[c.Vertex],
                    regions?.Describe(c.Position) ?? ""));
            }
            if (finite == 0)
                min = max = float.PositiveInfinity;
            result.Add(new GarmentFitSummary(garment.Id, garment.Name, kind, count, min, max, finite > 0 ? (float)(sum / finite) : float.PositiveInfinity,
                over / (float)count, under / (float)count, spots));
        }
        return result;
    }

    /// <summary>Names a place on the body in words ("front, left, chest") from its height and side, for summaries.</summary>
    public sealed class BodyRegions
    {
        private const int Slices = 48;
        private readonly float _floor, _height, _centreX;
        // The middle of the body front to back at each height, from the torso and legs only (not the arms), so that a
        // belly or a foot sticking forward does not move "front" and "back" elsewhere.
        private readonly float[] _centreZ = new float[Slices];

        public BodyRegions(TriMesh body)
        {
            var (min, max) = body.Bounds();
            _floor = min.Y;
            _height = MathF.Max(max.Y - min.Y, 0.1f);
            _centreX = (min.X + max.X) * 0.5f;
            var low = new float[Slices];
            var high = new float[Slices];
            Array.Fill(low, float.MaxValue);
            Array.Fill(high, float.MinValue);
            float reach = 0.12f * _height;
            foreach (var v in body.Positions)
            {
                if (MathF.Abs(v.X - _centreX) > reach)
                    continue;
                int slice = Slice(v.Y);
                low[slice] = MathF.Min(low[slice], v.Z);
                high[slice] = MathF.Max(high[slice], v.Z);
            }
            float fallback = (min.Z + max.Z) * 0.5f;
            for (int i = 0; i < Slices; i++)
                _centreZ[i] = low[i] <= high[i] ? (low[i] + high[i]) * 0.5f : fallback;
        }

        private int Slice(float y) => Math.Clamp((int)((y - _floor) / _height * Slices), 0, Slices - 1);

        public string Describe(Vector3 p)
        {
            float h = (p.Y - _floor) / _height;
            string band = h switch
            {
                > 0.93f => "head",
                > 0.84f => "neck",
                > 0.76f => "shoulders",
                > 0.66f => "chest",
                > 0.57f => "waist",
                > 0.47f => "hips",
                > 0.31f => "thighs",
                > 0.25f => "knees",
                > 0.08f => "calves",
                _ => "ankles",
            };
            float x = p.X - _centreX;
            // The figure faces +Z, so its left is +X.
            string side = MathF.Abs(x) < 0.03f ? "centre" : x > 0 ? "left" : "right";
            string face = p.Z >= _centreZ[Slice(p.Y)] ? "front" : "back";
            return $"{face}, {side}, {band}";
        }
    }
}
