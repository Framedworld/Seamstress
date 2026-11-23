using System.Numerics;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;

namespace Seamstress.Core.Editing;

/// <summary>
/// A hard part (a snap, a rivet, a buckle, a pen in a pocket) attached to the cloth it sits on: when that cloth moves,
/// the part is carried along rigidly, keeping its place and its angle to the surface. Hard parts are pinned, so the
/// simulation itself never moves them; without this a snap stayed in the air when its jacket was pulled away.
/// </summary>
public sealed class PieceAttachment
{
    public required string HostPieceId { get; init; }

    /// <summary>The point of the host's pattern (cm) the part sits on.</summary>
    public required Vector2 At { get; init; }

    // Where the host's surface was, and where the part was, when they were last lined up. Worked out again for each
    // simulation run, so they are not saved.
    internal Vector3 Origin, Normal, Tangent;
    internal float[]? Rest;
    internal string? RestKey;

    public PieceAttachment Clone() => new()
    {
        HostPieceId = HostPieceId, At = At, Origin = Origin, Normal = Normal, Tangent = Tangent, Rest = Rest, RestKey = RestKey,
    };
}

public static class Attachments
{
    /// <summary>How far from cloth a pinned hard part may be and still be taken to sit on it (m).</summary>
    private const float InferReach = 0.03f;

    /// <summary>True for a piece pinned in every vertex: a hard part rather than cloth.</summary>
    public static bool IsHardPart(PatternPiece piece, PieceMesh? mesh)
        => mesh != null && piece.PinKey == mesh.Key && mesh.VertexCount > 0 && piece.Pins.Count >= mesh.VertexCount;

    /// <summary>
    /// Before a simulation: attaches every hard part that has no host yet to the cloth right under it, and records
    /// where each attached part and its host are now, so <see cref="Follow"/> can carry the part along.
    /// </summary>
    public static void Prepare(Document document, Func<Garment, GarmentMesh> meshFor)
    {
        Dictionary<string, (PatternPiece Piece, PieceMesh Mesh, Vector3[] Positions)>? cloth = null;
        foreach (var garment in document.Garments)
        {
            if (!garment.Visible || garment.Pieces.Count == 0)
                continue;
            var mesh = meshFor(garment);
            foreach (var piece in garment.Pieces)
            {
                var pieceMesh = mesh.Find(piece.Id);
                if (pieceMesh == null || (piece.Attachment == null && !IsHardPart(piece, pieceMesh)))
                    continue;
                var positions = Mesher.CurrentPositions(piece, pieceMesh);
                if (piece.Attachment == null)
                {
                    cloth ??= ClothPieces(document, meshFor);
                    piece.Attachment = Infer(garment, positions, cloth);
                    if (piece.Attachment == null)
                        continue;
                }
                Capture(document, meshFor, piece, pieceMesh, positions);
            }
        }
    }

    /// <summary>
    /// Carries the hard parts attached to any of <paramref name="movedPieceIds"/> along with them. Returns how many
    /// parts moved.
    /// </summary>
    public static int Follow(Document document, Func<Garment, GarmentMesh> meshFor, IReadOnlySet<string> movedPieceIds)
    {
        int moved = 0;
        foreach (var garment in document.Garments)
        {
            foreach (var piece in garment.Pieces)
            {
                if (piece.Attachment is not { Rest: { } rest } attachment || !movedPieceIds.Contains(attachment.HostPieceId))
                    continue;
                var pieceMesh = meshFor(garment).Find(piece.Id);
                if (pieceMesh == null || pieceMesh.Key != attachment.RestKey || rest.Length != pieceMesh.VertexCount * 3)
                    continue;
                if (HostFrame(document, meshFor, attachment) is not { } frame)
                    continue;
                var (origin, normal, tangent) = frame;
                var across = Vector3.Cross(normal, tangent);
                var across0 = Vector3.Cross(attachment.Normal, attachment.Tangent);
                var drape = new float[rest.Length];
                for (int i = 0; i < rest.Length; i += 3)
                {
                    var d = new Vector3(rest[i], rest[i + 1], rest[i + 2]) - attachment.Origin;
                    var p = origin + tangent * Vector3.Dot(d, attachment.Tangent) + normal * Vector3.Dot(d, attachment.Normal)
                          + across * Vector3.Dot(d, across0);
                    drape[i] = p.X;
                    drape[i + 1] = p.Y;
                    drape[i + 2] = p.Z;
                }
                piece.Drape = drape;
                piece.DrapeKey = pieceMesh.Key;
                moved++;
            }
        }
        return moved;
    }

    private static void Capture(Document document, Func<Garment, GarmentMesh> meshFor, PatternPiece piece, PieceMesh mesh, Vector3[] positions)
    {
        var attachment = piece.Attachment!;
        if (HostFrame(document, meshFor, attachment) is not { } frame)
        {
            attachment.Rest = null;
            return;
        }
        (attachment.Origin, attachment.Normal, attachment.Tangent) = frame;
        var rest = new float[positions.Length * 3];
        for (int i = 0; i < positions.Length; i++)
        {
            rest[3 * i] = positions[i].X;
            rest[3 * i + 1] = positions[i].Y;
            rest[3 * i + 2] = positions[i].Z;
        }
        attachment.Rest = rest;
        attachment.RestKey = mesh.Key;
    }

    /// <summary>Where the host's surface is now at the attachment point: the point, its normal and the pattern's +x direction.</summary>
    private static (Vector3 Origin, Vector3 Normal, Vector3 Tangent)? HostFrame(Document document, Func<Garment, GarmentMesh> meshFor, PieceAttachment attachment)
    {
        var garment = document.GarmentOfPiece(attachment.HostPieceId);
        var host = garment?.FindPiece(attachment.HostPieceId);
        var mesh = garment == null ? null : meshFor(garment).Find(attachment.HostPieceId);
        if (host == null || mesh == null)
            return null;
        var positions = Mesher.CurrentPositions(host, mesh);
        if (!SurfaceLocator.TryLocate(mesh, positions, attachment.At, host.FlipNormals, out var origin, out var normal, out _))
            return null;
        foreach (var step in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) })
        {
            if (!SurfaceLocator.TryLocate(mesh, positions, attachment.At + step, host.FlipNormals, out var other, out _, out float off) || off > 0.01f)
                continue;
            var direction = other - origin;
            direction -= normal * Vector3.Dot(direction, normal);
            if (direction.LengthSquared() < 1e-10f)
                continue;
            direction = Vector3.Normalize(direction);
            // Turned so it always stands for the pattern's +x, whichever neighbour was used.
            Vector3 tangent = step.X > 0 ? direction
                : step.X < 0 ? -direction
                : Vector3.Normalize(Vector3.Cross(step.Y > 0 ? direction : -direction, normal));
            return (origin, normal, tangent);
        }
        return null;
    }

    private static Dictionary<string, (PatternPiece Piece, PieceMesh Mesh, Vector3[] Positions)> ClothPieces(Document document, Func<Garment, GarmentMesh> meshFor)
    {
        var result = new Dictionary<string, (PatternPiece, PieceMesh, Vector3[])>();
        foreach (var garment in document.Garments)
        {
            if (!garment.Visible || garment.Pieces.Count == 0)
                continue;
            var mesh = meshFor(garment);
            foreach (var piece in garment.Pieces)
            {
                if (mesh.Find(piece.Id) is { } pieceMesh && !IsHardPart(piece, pieceMesh))
                    result[piece.Id] = (piece, pieceMesh, Mesher.CurrentPositions(piece, pieceMesh));
            }
        }
        return result;
    }

    /// <summary>The cloth right under a hard part that was placed without saying what it sits on.</summary>
    private static PieceAttachment? Infer(Garment own, Vector3[] positions, Dictionary<string, (PatternPiece Piece, PieceMesh Mesh, Vector3[] Positions)> cloth)
    {
        if (positions.Length == 0)
            return null;
        var centre = positions.Aggregate(Vector3.Zero, (a, b) => a + b) / positions.Length;
        string? bestPiece = null;
        int bestVertex = -1;
        float best = InferReach * InferReach;
        foreach (var (id, entry) in cloth)
        {
            if (own.FindPiece(id) != null)
                continue;
            for (int i = 0; i < entry.Positions.Length; i++)
            {
                float d = Vector3.DistanceSquared(entry.Positions[i], centre);
                if (d < best)
                {
                    best = d;
                    bestPiece = id;
                    bestVertex = i;
                }
            }
        }
        return bestPiece == null ? null : new PieceAttachment { HostPieceId = bestPiece, At = cloth[bestPiece].Mesh.Points[bestVertex] };
    }
}
