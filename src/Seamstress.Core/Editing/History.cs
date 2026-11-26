using Seamstress.Core.Model;

namespace Seamstress.Core.Editing;

/// <summary>
/// Undo/redo for the whole document. Every edit records the document as it was before the change, so any operation
/// (including ones added later) is undoable without writing a matching "inverse" action.
/// </summary>
public sealed class History
{
    private const int Limit = 200;
    private readonly List<(string Name, Document State, long Id)> _undo = new();
    private readonly List<(string Name, Document State, long Id)> _redo = new();
    private Document? _pending;
    private string _pendingName = "";
    private long _nextId = 1;
    private long _currentId;
    private long _savedId;
    private string? _mergeKey;
    private long _mergeTime;
    private bool _grouped;

    public Document Current { get; private set; }

    /// <summary>Raised after the document changed in any way.</summary>
    public event Action? Changed;

    /// <summary>Raised just before an edit, undo, redo or document replacement starts.</summary>
    public event Action? BeforeChange;

    public History(Document document) => Current = document;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string UndoName => CanUndo ? _undo[^1].Name : "";
    public string RedoName => CanRedo ? _redo[^1].Name : "";
    public bool InTransaction => _pending != null;

    /// <summary>True when the document differs from the last saved or loaded state.</summary>
    public bool IsDirty => _currentId != _savedId;

    /// <summary>Replaces the document (new/open) and clears the history.</summary>
    public void Reset(Document document)
    {
        BeforeChange?.Invoke();
        Current = document;
        _undo.Clear();
        _redo.Clear();
        _pending = null;
        _grouped = false;
        _currentId = _savedId = _nextId++;
        Changed?.Invoke();
    }

    public void MarkSaved()
    {
        _savedId = _currentId;
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies an edit as a single undoable step. Consecutive edits with the same <paramref name="mergeKey"/> made in
    /// quick succession (dragging a slider) collapse into one step.
    /// </summary>
    public void Execute(string name, Action<Document> edit, string? mergeKey = null)
    {
        BeforeChange?.Invoke();
        if (_grouped)
        {
            // Part of the group's single step (an agent's tool call that makes several edits).
            edit(Current);
            Changed?.Invoke();
            return;
        }
        if (_pending != null)
            Commit();
        long now = Environment.TickCount64;
        bool merge = mergeKey != null && mergeKey == _mergeKey && now - _mergeTime < 1500 && CanUndo && _redo.Count == 0;
        if (merge)
        {
            edit(Current);
        }
        else
        {
            var before = Current.Clone();
            try
            {
                edit(Current);
            }
            catch
            {
                // An edit that fails half way (a block that finds no surface to sew onto after adding its garment)
                // leaves nothing behind.
                Current = before;
                Changed?.Invoke();
                throw;
            }
            Push(name, before);
        }
        _mergeKey = mergeKey;
        _mergeTime = now;
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies an edit as part of the last undo step (keeping the pulled shape of a pinch that was just recorded), so
    /// one undo takes both back. With nothing to undo it is a step of its own.
    /// </summary>
    public void Amend(string name, Action<Document> edit)
    {
        if (!CanUndo || _pending != null || _redo.Count > 0 || _grouped)
        {
            Execute(name, edit);
            return;
        }
        BeforeChange?.Invoke();
        edit(Current);
        _currentId = _nextId++;
        _mergeKey = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Starts an edit that happens gradually (a drag, a running simulation). Change <see cref="Current"/> freely,
    /// then call <see cref="Commit"/> to record one undo step or <see cref="Cancel"/> to roll everything back.
    /// </summary>
    public void Begin(string name)
    {
        BeforeChange?.Invoke();
        if (_grouped)
            return;
        if (_pending != null)
            Commit();
        _pending = Current.Clone();
        _pendingName = name;
    }

    public void Commit()
    {
        if (_grouped)
        {
            Changed?.Invoke();
            return;
        }
        if (_pending == null)
            return;
        Push(_pendingName, _pending);
        _pending = null;
        Changed?.Invoke();
    }

    public void Cancel()
    {
        if (_pending == null)
            return;
        Current = _pending;
        _pending = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Starts a group: everything done until <see cref="EndGroup"/>, including edits that would each be a step of their
    /// own, becomes one undo step. Used for an agent's tool call, so the user undoes what it did in one go.
    /// </summary>
    public void BeginGroup(string name)
    {
        BeforeChange?.Invoke();
        if (_pending != null)
            Commit();
        _pending = Current.Clone();
        _pendingName = name;
        _grouped = true;
    }

    /// <summary>Ends the group: records it as one step, or with <paramref name="keep"/> false undoes all of it.</summary>
    public void EndGroup(bool keep)
    {
        if (!_grouped)
            return;
        _grouped = false;
        if (keep)
            Commit();
        else
            Cancel();
    }

    /// <summary>Tells listeners the document changed mid-transaction (live feedback while dragging).</summary>
    public void NotifyLiveChange() => Changed?.Invoke();

    private void Push(string name, Document before)
    {
        _mergeKey = null;
        _undo.Add((name, before, _currentId));
        _currentId = _nextId++;
        _redo.Clear();
        if (_undo.Count > Limit)
            _undo.RemoveAt(0);
    }

    public bool Undo()
    {
        BeforeChange?.Invoke();
        if (_pending != null)
            Cancel();
        if (!CanUndo)
            return false;
        _mergeKey = null;
        var (name, state, id) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((name, Current, _currentId));
        Current = state;
        _currentId = id;
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        BeforeChange?.Invoke();
        if (_pending != null)
            Commit();
        if (!CanRedo)
            return false;
        _mergeKey = null;
        var (name, state, id) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((name, Current, _currentId));
        Current = state;
        _currentId = id;
        Changed?.Invoke();
        return true;
    }
}
