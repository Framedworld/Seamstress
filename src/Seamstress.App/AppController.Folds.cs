using Seamstress.Core;
using Seamstress.Core.Editing;

namespace Seamstress.App;

// Settings of the Internal Line, Dart and Pleat tools, for the panel the 2D view shows while one of them is chosen.
public partial class AppController
{
    /// <summary>Fold angle in degrees of the selected internal line, or of the next one drawn (0: a plain line).</summary>
    public double FoldAngle
    {
        get => _editor.FoldAngle;
        set { _editor.FoldAngle = (float)value; Notify(); }
    }

    /// <summary>Whether the selected (or next) fold line folds as a valley rather than a mountain.</summary>
    public bool FoldValley
    {
        get => _editor.FoldValley;
        set { _editor.FoldValley = value; Notify(); }
    }

    /// <summary>How firmly the selected (or next) fold line holds its angle, 0 to 100.</summary>
    public double FoldStrength
    {
        get => _editor.FoldStrength * 100;
        set { _editor.FoldStrength = (float)(value / 100); Notify(); }
    }

    /// <summary>How many pleats the Pleat tool makes over the stretch it is dragged along.</summary>
    public int PleatCount
    {
        get => _editor.PleatCount;
        set { _editor.PleatCount = value; Notify(); }
    }

    /// <summary>How much cloth each pleat folds under, in centimetres.</summary>
    public double PleatDepth
    {
        get => _editor.PleatDepth;
        set { _editor.PleatDepth = (float)value; Notify(); }
    }

    /// <summary>How far down from the edge the Pleat tool stitches each pleat shut, in centimetres (0: only tacked at the edge).</summary>
    public double PleatStitchDown
    {
        get => _editor.PleatStitchDown;
        set { _editor.PleatStitchDown = (float)value; Notify(); }
    }

    /// <summary>Removes the internal line selected with the Internal Line tool.</summary>
    public void DeleteInternalLine()
    {
        if (!_editor.DeleteSelectedLine())
            StatusText = Lang.T("Click a line with the Internal Line tool to select it first.");
    }
}
