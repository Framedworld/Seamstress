using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Editing;

/// <summary>
/// Set creases: the cloth remembers the folds it has now, the way canvas or denim keeps the creases it was worn or
/// pressed into. Pull the cloth into shape (pinch it in the app, or pull_cloth), set creases, and let go: the folds stay
/// instead of springing flat. Setting them again later takes the shape as it is then, old creases included.
/// </summary>
public static class Creases
{
    /// <summary>Default firmness: the folds come back strongly but the cloth can still be reshaped.</summary>
    public const float DefaultStrength = 0.8f;

    /// <summary>
    /// Takes the draped shape of each piece as its creases. Pieces that have not been draped are left alone (their
    /// flat placement has no folds to keep). Returns the number of pieces creased.
    /// </summary>
    public static int Set(IEnumerable<PatternPiece> pieces, Func<PatternPiece, PieceMesh?> meshOf, float strength = DefaultStrength)
    {
        strength = Math.Clamp(strength, 0f, 1f);
        int count = 0;
        foreach (var piece in pieces)
        {
            if (meshOf(piece) is not { } mesh || piece.Drape == null || piece.DrapeKey != mesh.Key)
                continue;
            if (strength <= 0)
            {
                piece.ClearCreases();
                continue;
            }
            piece.Crease = (float[])piece.Drape.Clone();
            piece.CreaseKey = mesh.Key;
            piece.CreaseStrength = strength;
            count++;
        }
        return count;
    }

    /// <summary>Forgets the creases of these pieces: they bend back towards flat again. Returns how many had any.</summary>
    public static int Clear(IEnumerable<PatternPiece> pieces)
    {
        int count = 0;
        foreach (var piece in pieces)
        {
            if (piece.Crease != null)
                count++;
            piece.ClearCreases();
        }
        return count;
    }
}
