using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Geometry;

/// <summary>
/// Maps pattern space to texture space. By default the 2D pattern layout of a garment is scaled uniformly into the
/// unit square, so every piece gets the same texel density; each piece can then be adjusted individually.
/// </summary>
public sealed class UvLayout
{
    private const float Margin = 0.02f;

    /// <summary>UV units per centimetre before per-piece adjustment.</summary>
    public float UnitsPerCm { get; private init; } = 0.01f;

    /// <summary>Pattern-space position (cm) that maps to UV (Margin, Margin).</summary>
    public Vector2 Min { get; private init; }

    public static UvLayout For(Garment garment)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        bool any = false;
        foreach (var piece in garment.Pieces)
        {
            if (piece.Points.Count < 3)
                continue;
            var (lo, hi) = Outline.Bounds(piece);
            min = Vector2.Min(min, lo + piece.Origin);
            max = Vector2.Max(max, hi + piece.Origin);
            any = true;
        }
        if (!any)
            return new UvLayout();
        float extent = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y), 1e-3f);
        return new UvLayout { UnitsPerCm = (1f - 2 * Margin) / extent, Min = min };
    }

    /// <summary>UV of the piece's bounding-box centre; per-piece scale and rotation pivot here.</summary>
    public Vector2 PieceCentre(PatternPiece piece)
    {
        var (lo, hi) = Outline.Bounds(piece);
        return BaseMap((lo + hi) / 2 + piece.Origin);
    }

    private Vector2 BaseMap(Vector2 world) => (world - Min) * UnitsPerCm + new Vector2(Margin);

    /// <summary>UV (V up) of a point given in the piece's local pattern coordinates.</summary>
    public Vector2 Map(PatternPiece piece, Vector2 local) => Map(piece, local, PieceCentre(piece));

    public Vector2 Map(PatternPiece piece, Vector2 local, Vector2 pieceCentre)
    {
        var uv = BaseMap(local + piece.Origin);
        var adjust = piece.Uv;
        var d = (uv - pieceCentre) * adjust.Scale;
        if (adjust.Rotation != 0)
        {
            float r = adjust.Rotation * MathF.PI / 180f;
            float c = MathF.Cos(r), s = MathF.Sin(r);
            d = new Vector2(d.X * c - d.Y * s, d.X * s + d.Y * c);
        }
        return pieceCentre + d + adjust.Offset;
    }

    public Vector2[] Map(PatternPiece piece, PieceMesh mesh)
    {
        var centre = PieceCentre(piece);
        var result = new Vector2[mesh.Points.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Map(piece, mesh.Points[i], centre);
        return result;
    }

    public Vector2[] MapMaterial(PatternPiece piece, PieceMesh mesh, FabricMaterial? material)
        => material is { TileSizeCm: > 0, Texture.Length: > 0 } ? TileUvs(piece, mesh, material.TileSizeCm)
         : material?.FitsTextureToPiece == true ? FitPieceUvs(mesh) : Map(piece, mesh);

    /// <summary>
    /// UVs in units of one texture tile of <paramref name="tileCm"/> centimetres: the texture repeats at its real
    /// size along the grain, however large the garment's 2D layout is. The piece's own UV rotation and scale apply.
    /// </summary>
    public static Vector2[] TileUvs(PatternPiece piece, PieceMesh mesh, float tileCm)
    {
        var (lo, hi) = Outline.Bounds(piece);
        var centre = (lo + hi) / 2;
        float scale = piece.Uv.Scale / MathF.Max(tileCm, 0.01f);
        float r = piece.Uv.Rotation * MathF.PI / 180f;
        float c = MathF.Cos(r), s = MathF.Sin(r);
        var result = new Vector2[mesh.Points.Length];
        for (int i = 0; i < result.Length; i++)
        {
            var d = (mesh.Points[i] - centre) * scale;
            if (piece.Uv.Rotation != 0)
                d = new Vector2(d.X * c - d.Y * s, d.X * s + d.Y * c);
            result[i] = (centre + piece.Origin) / MathF.Max(tileCm, 0.01f) + d + piece.Uv.Offset;
        }
        return result;
    }

    /// <summary>Maps a piece's local 2D pattern bounding box directly to [0,1]x[0,1] for piece-specific decal/detail textures (Repeat &lt;= 0).</summary>
    private static Vector2[] FitPieceUvs(PieceMesh pieceMesh)
    {
        var pts = pieceMesh.Points;
        var uvs = new Vector2[pts.Length];
        if (pts.Length == 0)
            return uvs;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        float invW = 1f / MathF.Max(maxX - minX, 1e-4f);
        float invH = 1f / MathF.Max(maxY - minY, 1e-4f);
        for (int i = 0; i < pts.Length; i++)
            uvs[i] = new Vector2((pts[i].X - minX) * invW, (pts[i].Y - minY) * invH);
        return uvs;
    }

    /// <summary>Texture pixels per centimetre of fabric for a piece.</summary>
    public float TexelDensity(PatternPiece piece, FabricMaterial? material, int textureResolution)
        => material is { TileSizeCm: > 0 }
            ? piece.Uv.Scale * textureResolution / material.TileSizeCm
            : UnitsPerCm * piece.Uv.Scale * textureResolution * MathF.Max(material?.Repeat ?? 1f, 1e-3f);
}
