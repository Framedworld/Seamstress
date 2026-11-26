using Seamstress.Core;
using Seamstress.Core.Editing;

namespace Seamstress.Mcp;

/// <summary>
/// The <c>ease_pattern</c> tool: lets a garment's pattern out where its drape is stretched too much
/// (<see cref="PatternEasing"/>), or with <c>apply</c> false only says what it would change.
/// </summary>
public static class EaseReport
{
    public static object Run(Workspace workspace, string? garmentId, float targetPercent, float strength, string? pieceIds,
        bool includeElastic, bool apply)
    {
        var garment = (garmentId != null ? workspace.Document.FindGarment(garmentId) : workspace.ActiveGarment)
            ?? throw new ArgumentException(garmentId != null ? $"No garment '{garmentId}'." : "There is no garment.");
        var options = new EaseOptions
        {
            TargetStrain = Math.Clamp(targetPercent, 0f, 30f) / 100f,
            Strength = Math.Clamp(strength, 0.05f, 1f),
            IncludeElastic = includeElastic,
            PieceIds = string.IsNullOrWhiteSpace(pieceIds) ? null
                : pieceIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(),
        };
        var plan = PatternEasing.Plan(garment, workspace.MeshFor(garment), options);
        if (apply)
            PatternEasing.Apply(workspace, plan, Lang.T("Ease tight spots"));
        return Describe(plan, apply);
    }

    public static object Describe(EasePlan plan, bool applied) => new
    {
        garmentId = plan.GarmentId,
        garment = plan.GarmentName,
        applied = applied && plan.ChangedPieces > 0,
        changedPieces = plan.ChangedPieces,
        pieces = plan.Pieces.Select(p => new
        {
            pieceId = p.PieceId,
            piece = p.PieceName,
            skipped = p.Skipped,
            eased = p.Changes,
            maxStretchAcrossPercent = MathF.Round(p.MaxRowStrain * 100f, 1),
            maxStretchAlongPercent = MathF.Round(p.MaxColumnStrain * 100f, 1),
            mostAddedCm = MathF.Round(p.MostAddedCm, 2),
            creasesCleared = p.Changes && p.HasCreases,
            atPatternYCm = MathF.Round(p.AtYCm, 1),
            edges = p.Edges.Select(e => new { edge = e.Edge, name = e.Name, beforeCm = MathF.Round(e.BeforeCm, 2), afterCm = MathF.Round(e.AfterCm, 2) }),
        }),
        seams = plan.Seams.Select(s => new
        {
            seamId = s.SeamId,
            a = $"{s.PieceA} edge {s.EdgeA}",
            b = $"{s.PieceB} edge {s.EdgeB}",
            beforeCm = new[] { MathF.Round(s.BeforeA, 2), MathF.Round(s.BeforeB, 2) },
            afterCm = new[] { MathF.Round(s.AfterA, 2), MathF.Round(s.AfterB, 2) },
            mismatchCm = MathF.Round(s.MismatchAfter, 2),
        }),
        note = plan.ChangedPieces == 0
            ? "Nothing is stretched across by more than the target where it can be eased."
            : applied
                ? "Eased and still draped where it was: simulate again to let the cloth relax, then check with fit_map. Set creases on eased pieces were cleared (creasesCleared): they held the stretch; set them again on the relaxed cloth if wanted. Repeat while it is still over the target; each pass makes part of the correction. Seams whose edges now differ in length are eased in when sewn; if one differs by more than a centimetre or two, let the other side out too."
                : "Nothing changed yet: call again with apply true. Stretch along a piece is only reported: lengthen it by hand if it matters.",
    };
}
