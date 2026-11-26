using System.Numerics;
using Seamstress.Core.Model;

namespace Seamstress.Core.Editing;

// Internal lines, fold lines, darts and pleats as undoable edits (the geometry is in PatternFolds).
public sealed partial class Workspace
{
    /// <summary>
    /// Draws an internal line inside a piece (local centimetres); a fold angle above 0 makes it a fold line. Throws
    /// <see cref="ArgumentException"/> with a message for the user when it cannot be drawn there.
    /// </summary>
    public InternalLine AddInternalLine(string pieceId, IReadOnlyList<PatternPoint> points, float foldAngle = 0, bool valley = false,
        float strength = InternalLine.DefaultFoldStrength)
    {
        InternalLine? made = null;
        History.Execute(foldAngle > 0.5f ? Lang.T("Add fold line") : Lang.T("Add internal line"), doc =>
        {
            var piece = doc.FindPiece(pieceId) ?? throw new ArgumentException(Lang.T("No such piece."));
            made = PatternFolds.AddLine(piece, points, foldAngle, valley, strength);
            piece.ClearDrape();
        });
        return made!;
    }

    /// <summary>Changes an internal line's fold; values left null stay as they are.</summary>
    public bool SetInternalLineFold(string pieceId, string lineId, float? foldAngle, bool? valley, float? strength)
    {
        if (Document.FindPiece(pieceId)?.InternalLines.FirstOrDefault(l => l.Id == lineId) == null)
            return false;
        History.Execute(Lang.T("Change fold line"), doc =>
        {
            var line = doc.FindPiece(pieceId)!.InternalLines.First(l => l.Id == lineId);
            if (foldAngle is { } angle && float.IsFinite(angle))
                line.FoldAngle = Math.Clamp(angle, 0, InternalLine.MaxFoldAngle);
            if (valley is { } v)
                line.Valley = v;
            if (strength is { } s && float.IsFinite(s))
                line.FoldStrength = Math.Clamp(s, 0, 1);
        }, $"fold:{pieceId}:{lineId}");
        return true;
    }

    public bool DeleteInternalLine(string pieceId, string lineId)
    {
        if (Document.FindPiece(pieceId)?.InternalLines.Any(l => l.Id == lineId) != true)
            return false;
        History.Execute(Lang.T("Delete internal line"), doc =>
        {
            var piece = doc.FindPiece(pieceId)!;
            piece.InternalLines.RemoveAll(l => l.Id == lineId);
            piece.ClearDrape();
        });
        return true;
    }

    /// <summary>Adds a dart to a piece (see <see cref="PatternFolds.AddDart"/>); throws <see cref="ArgumentException"/> when it cannot.</summary>
    public PatternFolds.DartResult AddDart(string pieceId, int edge, Vector2 legA, Vector2 legB, Vector2 apex)
    {
        PatternFolds.DartResult? result = null;
        History.Execute(Lang.T("Add dart"), doc =>
        {
            var garment = doc.GarmentOfPiece(pieceId) ?? throw new ArgumentException(Lang.T("No such piece."));
            result = PatternFolds.AddDart(garment, garment.FindPiece(pieceId)!, edge, legA, legB, apex);
        });
        return result!;
    }

    /// <summary>Adds knife pleats along an edge (see <see cref="PatternFolds.AddPleats"/>); throws <see cref="ArgumentException"/> when they do not fit.</summary>
    public PatternFolds.PleatResult AddPleats(string pieceId, int edge, float from, float to, int count, float depth,
        float foldAngle = PatternFolds.DefaultPleatFoldAngle, float strength = PatternFolds.DefaultPleatStrength, bool tack = true)
    {
        PatternFolds.PleatResult? result = null;
        History.Execute(count == 1 ? Lang.T("Add pleat") : Lang.T("Add pleats"), doc =>
        {
            var garment = doc.GarmentOfPiece(pieceId) ?? throw new ArgumentException(Lang.T("No such piece."));
            result = PatternFolds.AddPleats(garment, garment.FindPiece(pieceId)!, edge, from, to, count, depth, foldAngle, strength, tack);
        });
        return result!;
    }
}
