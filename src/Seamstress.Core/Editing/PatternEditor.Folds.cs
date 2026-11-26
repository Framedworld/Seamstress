using System.Globalization;
using System.Numerics;
using System.Text;
using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.Editing;

// The Internal Line, Dart and Pleat tools of the 2D view.
//
//   Internal Line: click inside a piece to start a line, click to add points (drag while clicking to bend the
//   stretch to that point), and double-click, press Enter or click on the outline to finish. With a fold angle it is
//   a fold line. Clicking an existing line selects it, so its fold can be changed or the line deleted.
//   Dart: click the dart's point inside a piece, then the two ends of its legs on an edge.
//   Pleat: drag along an edge over the stretch to pleat; pulling into the piece while dragging sets the depth.
public sealed partial class PatternEditor
{
    public const float DefaultPleatDepth = 3f;
    public const int DefaultPleatCount = 4;

    private readonly List<PatternPoint> _lineDraft = new();       // world positions; Curve bends the stretch after a point
    private string? _linePiece;
    private Vector2? _lineCursor;
    private Vector2? _linePress;                                    // where the button went down for the next point
    private Vector2? _lineBend;                                     // where it is being dragged to, bending that stretch
    private (string PieceId, string LineId)? _selectedLine;
    private float _foldAngle;
    private bool _foldValley;
    private float _foldStrength = InternalLine.DefaultFoldStrength;

    private string? _dartPiece;
    private Vector2? _dartApex;                                     // world
    private (int Edge, Vector2 At)? _dartLeg;                       // first leg end, world
    private Vector2? _dartCursor;

    private int _pleatCount = DefaultPleatCount;
    private float _pleatDepth = DefaultPleatDepth;
    private EdgeRef? _pleatEdge;
    private float _pleatFrom, _pleatTo;
    private bool _pleatDragging;
    private float? _pleatPulledDepth;

    /// <summary>What the fold tools did, or why they could not: shown in the status bar.</summary>
    public event Action<string>? FoldToolMessage;

    private bool IsFoldTool => _tool is PatternTool.InternalLine or PatternTool.Dart or PatternTool.Pleat;

    /// <summary>Fold angle of the line selected with the Internal Line tool, or of the next one drawn (0: a plain line).</summary>
    public float FoldAngle
    {
        get => SelectedLine()?.FoldAngle ?? _foldAngle;
        set
        {
            _foldAngle = Math.Clamp(float.IsFinite(value) ? value : 0, 0, InternalLine.MaxFoldAngle);
            if (_selectedLine is { } s)
                _workspace.SetInternalLineFold(s.PieceId, s.LineId, _foldAngle, null, null);
            Changed?.Invoke();
        }
    }

    /// <summary>Whether the selected (or next) fold line is a valley fold rather than a mountain fold.</summary>
    public bool FoldValley
    {
        get => SelectedLine()?.Valley ?? _foldValley;
        set
        {
            _foldValley = value;
            if (_selectedLine is { } s)
                _workspace.SetInternalLineFold(s.PieceId, s.LineId, null, value, null);
            Changed?.Invoke();
        }
    }

    /// <summary>How firmly the selected (or next) fold line holds its angle, 0 to 1.</summary>
    public float FoldStrength
    {
        get => SelectedLine()?.FoldStrength ?? _foldStrength;
        set
        {
            _foldStrength = Math.Clamp(float.IsFinite(value) ? value : InternalLine.DefaultFoldStrength, 0, 1);
            if (_selectedLine is { } s)
                _workspace.SetInternalLineFold(s.PieceId, s.LineId, null, null, _foldStrength);
            Changed?.Invoke();
        }
    }

    /// <summary>How many pleats the Pleat tool makes across the stretch dragged.</summary>
    public int PleatCount
    {
        get => _pleatCount;
        set
        {
            _pleatCount = Math.Clamp(value, 1, PatternFolds.MaxPleats);
            Changed?.Invoke();
        }
    }

    /// <summary>How much cloth each pleat folds under, in centimetres.</summary>
    public float PleatDepth
    {
        get => _pleatDepth;
        set
        {
            _pleatDepth = Math.Clamp(float.IsFinite(value) ? value : DefaultPleatDepth, 0.2f, 100f);
            Changed?.Invoke();
        }
    }

    public bool HasSelectedLine => SelectedLine() != null;

    private InternalLine? SelectedLine()
        => _selectedLine is { } s ? Doc.FindPiece(s.PieceId)?.InternalLines.FirstOrDefault(l => l.Id == s.LineId) : null;

    /// <summary>Delete with the Internal Line tool: removes the selected line. False when no line is selected.</summary>
    public bool DeleteSelectedLine()
    {
        if (_tool != PatternTool.InternalLine || _selectedLine is not { } s)
            return false;
        _selectedLine = null;
        _workspace.DeleteInternalLine(s.PieceId, s.LineId);
        Changed?.Invoke();
        return true;
    }

    private string FoldToolHint => _tool switch
    {
        PatternTool.InternalLine => _lineDraft.Count == 0
            ? Lang.T("Click inside a piece to start a line, or click a line to select it. Give it a fold angle to make a fold line.")
            : Lang.T("Click to add points (drag to curve the stretch); double-click, press Enter or click the outline to finish. Esc cancels."),
        PatternTool.Dart => _dartApex == null
            ? Lang.T("Click inside a piece where the point of the dart goes.")
            : _dartLeg == null
                ? Lang.T("Click on an edge where the first leg of the dart ends.")
                : Lang.T("Click on the same edge where the second leg ends. Esc cancels."),
        PatternTool.Pleat => Lang.T("Drag along an edge over the stretch to pleat; pull into the piece while dragging to set the depth."),
        _ => "",
    };

    private void Message(string text) => FoldToolMessage?.Invoke(text);

    private void CancelFoldTools()
    {
        _lineDraft.Clear();
        _linePiece = null;
        _lineCursor = _linePress = _lineBend = null;
        _dartPiece = null;
        _dartApex = null;
        _dartLeg = null;
        _dartCursor = null;
        _pleatEdge = null;
        _pleatDragging = false;
        _pleatPulledDepth = null;
    }

    private (PatternPiece Piece, int Edge, Vector2 Closest)? HitEdgeOf(string? pieceId, Vector2 world, float tolerance)
        => HitEdge(world, tolerance) is { } hit && (pieceId == null || hit.Piece.Id == pieceId) ? hit : null;

    // ---------------------------------------------------------------- pointer events

    private void FoldToolDown(Vector2 world, float tolerance)
    {
        switch (_tool)
        {
            case PatternTool.InternalLine:
                LineDown(world, tolerance);
                break;
            case PatternTool.Dart:
                DartDown(world, tolerance);
                break;
            case PatternTool.Pleat:
                if (HitEdge(world, tolerance) is { } edge)
                {
                    _pleatEdge = new EdgeRef(edge.Piece.Id, edge.Edge);
                    _pleatFrom = _pleatTo = PatternFolds.EdgeFraction(edge.Piece, edge.Edge, edge.Closest);
                    _pleatDragging = true;
                    _pleatPulledDepth = null;
                    Changed?.Invoke();
                }
                else
                {
                    Message(Lang.T("Start the drag on an edge of a piece."));
                }
                break;
        }
    }

    /// <summary>Pointer moves for the fold tools; true when the move was theirs.</summary>
    private bool FoldToolMove(Vector2 world, float tolerance)
    {
        if (!IsFoldTool || _uvMode)
            return false;
        switch (_tool)
        {
            case PatternTool.InternalLine:
                if (_linePress is { } press)
                {
                    if (Vector2.Distance(press, world) > tolerance * 0.35f)
                        _lineBend = world;
                }
                else if (_lineDraft.Count > 0)
                {
                    _lineCursor = LineSnap(world, tolerance);
                }
                break;
            case PatternTool.Dart:
                _dartCursor = _dartApex == null ? null
                    : HitEdgeOf(_dartPiece, world, tolerance) is { } edge && (_dartLeg == null || edge.Edge == _dartLeg.Value.Edge)
                        ? edge.Closest + edge.Piece.Origin : world;
                break;
            case PatternTool.Pleat:
                if (_pleatDragging && _pleatEdge is { } e && Doc.FindPiece(e.PieceId) is { } piece && e.Edge < piece.EdgeCount)
                {
                    var local = world - piece.Origin;
                    _pleatTo = PatternFolds.EdgeFraction(piece, e.Edge, local);
                    // Pulled into the piece: that far in is the depth of each pleat.
                    var onEdge = PatternFolds.PointAlong(piece, e.Edge, _pleatTo);
                    float pulled = Vector2.Dot(local - onEdge, PatternFolds.Inward(piece, e.Edge, _pleatTo));
                    _pleatPulledDepth = pulled > tolerance ? pulled : null;
                }
                break;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Pointer releases for the fold tools; true when the release was theirs.</summary>
    private bool FoldToolUp(Vector2 world)
    {
        if (!IsFoldTool || _uvMode)
            return false;
        if (_tool == PatternTool.InternalLine && _linePress is { } press)
        {
            _linePress = null;
            var bend = _lineBend;
            _lineBend = null;
            AddLinePoint(press, bend);
        }
        else if (_tool == PatternTool.Pleat && _pleatDragging)
        {
            _pleatDragging = false;
            ApplyPleats();
        }
        _drag = Drag.None;
        Changed?.Invoke();
        return true;
    }

    private bool FoldToolDoubleClick()
    {
        if (_tool != PatternTool.InternalLine || _lineDraft.Count == 0)
            return false;
        // The first click of the double-click added a point; a second one on top of it is dropped.
        if (_lineDraft.Count >= 2 && Vector2.Distance(_lineDraft[^1].Position, _lineDraft[^2].Position) < 0.05f)
            _lineDraft.RemoveAt(_lineDraft.Count - 1);
        FinishLine();
        return true;
    }

    private bool FoldToolConfirm()
    {
        if (_tool != PatternTool.InternalLine || _lineDraft.Count == 0)
            return false;
        FinishLine();
        return true;
    }

    // ---------------------------------------------------------------- internal lines

    /// <summary>Near the outline of the piece being drawn on, a point catches on the edge.</summary>
    private Vector2 LineSnap(Vector2 world, float tolerance)
    {
        if (HitEdgeOf(_linePiece, world, tolerance) is { } edge)
            return edge.Closest + edge.Piece.Origin;
        return _constrain ? SnapToGrid(world) : world;
    }

    private void LineDown(Vector2 world, float tolerance)
    {
        if (_lineDraft.Count == 0)
        {
            // A click on an existing line selects it.
            foreach (var (_, piece) in VisiblePieces())
            {
                if (PatternFolds.HitLine(piece, world - piece.Origin, tolerance) is { } line)
                {
                    _selectedLine = (piece.Id, line.Id);
                    _workspace.SelectPiece(piece.Id);
                    Message(line.IsFold
                        ? Lang.F("Fold line selected: {0}°, {1}. Delete removes it.", MathF.Round(line.FoldAngle), line.Valley ? Lang.T("valley") : Lang.T("mountain"))
                        : Lang.T("Line selected. Delete removes it."));
                    return;
                }
            }
            _selectedLine = null;
            var host = HitPiece(world) ?? HitEdge(world, tolerance)?.Piece;
            if (host == null)
            {
                Message(Lang.T("Click inside a piece to start a line."));
                return;
            }
            _linePiece = host.Id;
            Message(Lang.T("Click to add points (drag to curve the stretch); double-click, press Enter or click the outline to finish. Esc cancels."));
        }
        _linePress = LineSnap(world, tolerance);
        _lineBend = null;
        _drag = Drag.None;
    }

    private void AddLinePoint(Vector2 at, Vector2? bend)
    {
        if (_lineDraft.Count > 0 && bend is { } through)
        {
            // The stretch from the last point bends so that its middle passes where the pointer was let go.
            var from = _lineDraft[^1].Position;
            _lineDraft[^1].Curve = 2 * through - (from + at) / 2;
        }
        _lineDraft.Add(new PatternPoint { Position = at });
        _lineCursor = at;
        // A second point on the outline ends the line there.
        if (_lineDraft.Count >= 2 && Doc.FindPiece(_linePiece) is { } piece
            && Outline.DistanceToPolyline(at - piece.Origin, Outline.Flatten(piece), true) < 1e-3f)
            FinishLine();
    }

    private void FinishLine()
    {
        var piece = Doc.FindPiece(_linePiece);
        var points = _lineDraft.Select(p => new PatternPoint
        {
            Position = p.Position - (piece?.Origin ?? Vector2.Zero),
            Curve = p.Curve - (piece?.Origin ?? Vector2.Zero),
        }).ToList();
        CancelFoldTools();
        if (piece == null || points.Count < 2)
        {
            Changed?.Invoke();
            return;
        }
        try
        {
            var line = _workspace.AddInternalLine(piece.Id, points, _foldAngle, _foldValley, _foldStrength);
            _selectedLine = (piece.Id, line.Id);
            Message(line.IsFold
                ? Lang.F("Added a fold line to “{0}”. Simulate to see it fold.", piece.Name)
                : Lang.F("Added a line to “{0}”.", piece.Name));
        }
        catch (ArgumentException e)
        {
            Message(e.Message);
        }
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- darts

    private void DartDown(Vector2 world, float tolerance)
    {
        if (_dartApex == null)
        {
            if (HitPiece(world) is not { } piece)
            {
                Message(Lang.T("Click inside a piece where the point of the dart goes."));
                return;
            }
            _dartPiece = piece.Id;
            _dartApex = world;
            Message(FoldToolHint);
            Changed?.Invoke();
            return;
        }
        if (HitEdgeOf(_dartPiece, world, tolerance) is not { } edge || (_dartLeg is { } first && first.Edge != edge.Edge))
        {
            Message(_dartLeg == null ? Lang.T("Click on an edge of the same piece.") : Lang.T("Both legs of a dart end on the same edge."));
            return;
        }
        var at = edge.Closest + edge.Piece.Origin;
        if (_dartLeg is not { } leg)
        {
            _dartLeg = (edge.Edge, at);
            Message(FoldToolHint);
            Changed?.Invoke();
            return;
        }
        var piece2 = edge.Piece;
        var apex = _dartApex.Value - piece2.Origin;
        CancelFoldTools();
        try
        {
            _workspace.AddDart(piece2.Id, leg.Edge, leg.At - piece2.Origin, at - piece2.Origin, apex);
            Message(Lang.F("Added a dart to “{0}”; its legs are sewn together.", piece2.Name));
        }
        catch (ArgumentException e)
        {
            Message(e.Message);
        }
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- pleats

    private float PleatDepthNow => _pleatPulledDepth ?? _pleatDepth;

    /// <summary>The pleats the current drag would make, worked out on a copy of the piece: their fold lines and the depth.</summary>
    private (PatternPiece Piece, PatternFolds.PleatResult Result)? PleatPreview(out string? problem)
    {
        problem = null;
        if (_pleatEdge is not { } e || Doc.GarmentOfPiece(e.PieceId) is not { } garment || garment.FindPiece(e.PieceId) is not { } piece
            || e.Edge >= piece.EdgeCount)
            return null;
        if (MathF.Abs(_pleatTo - _pleatFrom) * Outline.EdgeLength(piece, e.Edge) < 1f)
            return null;
        var copy = garment.Clone();
        var copyPiece = copy.FindPiece(piece.Id)!;
        copyPiece.InternalLines.Clear();
        try
        {
            return (piece, PatternFolds.AddPleats(copy, copyPiece, e.Edge, _pleatFrom, _pleatTo, _pleatCount, PleatDepthNow));
        }
        catch (ArgumentException ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    private void ApplyPleats()
    {
        var preview = PleatPreview(out var problem);
        var edge = _pleatEdge;
        _pleatEdge = null;
        if (_pleatPulledDepth is { } pulled)
            _pleatDepth = MathF.Round(pulled * 2) / 2;
        _pleatPulledDepth = null;
        if (preview is not { } p || edge == null)
        {
            Message(problem ?? Lang.T("Drag further along the edge to choose the stretch to pleat."));
            return;
        }
        try
        {
            var made = _workspace.AddPleats(edge.PieceId, edge.Edge, _pleatFrom, _pleatTo, _pleatCount, p.Result.Depth);
            string text = Lang.F("Added {0} pleats, {1} deep, to “{2}”.", made.Tacks.Count > 0 ? made.Tacks.Count : made.Lines.Count / 2,
                LengthText(made.Depth), p.Piece.Name);
            if (made.RemovedSeams > 0)
                text += " " + Lang.T("The seam on that edge was taken off: sew the new edges again.");
            Message(text);
        }
        catch (ArgumentException ex)
        {
            Message(ex.Message);
        }
    }

    private static string LengthText(float cm) => cm.ToString("0.#", CultureInfo.InvariantCulture) + " cm";

    // ---------------------------------------------------------------- scene description

    private void AppendFoldScene(StringBuilder sb)
    {
        if (_uvMode)
            return;
        var inv = CultureInfo.InvariantCulture;
        // Every internal line: {piece, id, points (world, flattened), fold (degrees, 0 = plain), valley, selected}.
        sb.Append(",\"internalLines\":[");
        bool first = true;
        foreach (var garment in Doc.Garments)
        {
            foreach (var piece in garment.Pieces)
            {
                if (piece.Points.Count < 3 || !Workspace.IsEffectivelyVisible(garment, piece))
                    continue;
                foreach (var line in piece.InternalLines)
                {
                    sb.Append(first ? "" : ",");
                    first = false;
                    sb.Append("{\"piece\":").Append(Q(piece.Id)).Append(",\"id\":").Append(Q(line.Id));
                    sb.Append(",\"points\":");
                    AppendPoints(sb, line.Flatten().Select(p => p + piece.Origin));
                    sb.Append(inv, $",\"fold\":{line.FoldAngle:0.#},\"valley\":{(line.Valley ? "true" : "false")}");
                    sb.Append(",\"selected\":").Append(_selectedLine is { } s && s.LineId == line.Id && s.PieceId == piece.Id ? "true" : "false");
                    sb.Append('}');
                }
            }
        }
        sb.Append(']');

        if (!IsFoldTool)
            return;
        // The settings the tool panel shows.
        sb.Append(inv, $",\"foldTool\":{{\"angle\":{FoldAngle:0.#},\"valley\":{(FoldValley ? "true" : "false")},\"strength\":{FoldStrength:0.###}");
        sb.Append(inv, $",\"count\":{_pleatCount},\"depth\":{PleatDepthNow:0.###},\"selected\":{(HasSelectedLine ? "true" : "false")}}}");

        if (_tool == PatternTool.InternalLine && _lineDraft.Count > 0)
        {
            // The line being drawn, with the stretch to the pointer (bent while a point is being dragged).
            var draft = new InternalLine { Points = _lineDraft.Select(p => p.Clone()).ToList() };
            if (_linePress is { } press)
            {
                if (_lineBend is { } through)
                    draft.Points[^1].Curve = 2 * through - (draft.Points[^1].Position + press) / 2;
                draft.Points.Add(new PatternPoint { Position = press });
            }
            else if (_lineCursor is { } cursor)
            {
                draft.Points.Add(new PatternPoint { Position = cursor });
            }
            sb.Append(",\"lineDraft\":");
            AppendPoints(sb, draft.Flatten());
            sb.Append(inv, $",\"lineDraftFold\":{_foldAngle:0.#},\"lineDraftValley\":{(_foldValley ? "true" : "false")}");
        }
        if (_tool == PatternTool.Dart && _dartApex is { } apex)
        {
            // Leg, point, leg: as much of the wedge as has been clicked, then the pointer.
            var wedge = new List<Vector2>();
            if (_dartLeg is { } leg)
                wedge.Add(leg.At);
            wedge.Add(apex);
            if (_dartCursor is { } c)
                wedge.Add(c);
            sb.Append(",\"dartDraft\":");
            AppendPoints(sb, wedge);
        }
        if (_tool == PatternTool.Pleat && _pleatEdge is { } e && Doc.FindPiece(e.PieceId) is { } pleated && e.Edge < pleated.EdgeCount)
        {
            var span = new List<Vector2>();
            float lo = MathF.Min(_pleatFrom, _pleatTo), hi = MathF.Max(_pleatFrom, _pleatTo);
            for (int i = 0; i <= 16; i++)
                span.Add(PatternFolds.PointAlong(pleated, e.Edge, lo + (hi - lo) * i / 16f) + pleated.Origin);
            sb.Append(",\"pleatDraft\":{\"span\":");
            AppendPoints(sb, span);
            var preview = PleatPreview(out var problem);
            sb.Append(",\"lines\":[");
            if (preview is { } p)
            {
                for (int i = 0; i < p.Result.Lines.Count; i++)
                {
                    sb.Append(i == 0 ? "" : ",").Append("{\"valley\":").Append(p.Result.Lines[i].Valley ? "true" : "false").Append(",\"points\":");
                    AppendPoints(sb, p.Result.Lines[i].Flatten().Select(q => q + pleated.Origin));
                    sb.Append('}');
                }
            }
            sb.Append(']');
            sb.Append(inv, $",\"count\":{_pleatCount},\"depth\":{(preview?.Result.Depth ?? PleatDepthNow):0.##}");
            sb.Append(inv, $",\"width\":{(hi - lo) * Outline.EdgeLength(pleated, e.Edge):0.##}");
            if (problem != null)
                sb.Append(",\"problem\":").Append(Q(problem));
            sb.Append('}');
        }
    }
}
