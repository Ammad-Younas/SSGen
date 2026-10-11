namespace SSGen.Scene.Editing;

/// <summary>
/// Undo/redo history for one document. Commands that share a merge key and arrive within
/// <see cref="MergeWindow"/> join the previous step, so a drag or typing a word is one undo.
/// Tracks whether the document differs from the last saved state.
/// </summary>
public sealed class EditHistory
{
    private readonly List<ISceneCommand> _done = [];
    private readonly List<ISceneCommand> _undone = [];
    private readonly Func<DateTime> _clock;
    private DateTime _lastPushUtc = DateTime.MinValue;

    /// <summary>Number of done commands at the last save; -1 when the saved state can't be reached any more.</summary>
    private int _savedAt;

    public EditHistory(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public TimeSpan MergeWindow { get; init; } = TimeSpan.FromSeconds(1.5);

    public event EventHandler? Changed;

    public bool CanUndo => _done.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    public string? UndoName => _done.Count > 0 ? _done[^1].Name : null;

    public string? RedoName => _undone.Count > 0 ? _undone[^1].Name : null;

    public bool IsDirty => _savedAt != _done.Count;

    /// <summary>Applies the command and records it.</summary>
    public void Execute(SceneDocument scene, ISceneCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Apply(scene);
        Record(command);
    }

    /// <summary>Records a command whose change is already in the document (live drags, typing).</summary>
    public void Record(ISceneCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = _clock();
        var canMerge = _undone.Count == 0 && _done.Count > 0 && now - _lastPushUtc <= MergeWindow;
        if (canMerge && Merge(_done[^1], command))
        {
            if (_savedAt == _done.Count)
            {
                _savedAt = -1; // the saved step itself changed
            }
        }
        else
        {
            if (_undone.Count > 0 && _savedAt > _done.Count)
            {
                _savedAt = -1; // the saved state was in the redo branch that's about to go
            }

            _done.Add(command);
        }

        _undone.Clear();
        _lastPushUtc = now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo(SceneDocument scene)
    {
        if (_done.Count == 0)
        {
            return false;
        }

        var command = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        command.Revert(scene);
        _undone.Add(command);
        _lastPushUtc = DateTime.MinValue; // never merge into a step that was undone and redone
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo(SceneDocument scene)
    {
        if (_undone.Count == 0)
        {
            return false;
        }

        var command = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        command.Apply(scene);
        _done.Add(command);
        _lastPushUtc = DateTime.MinValue;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void MarkSaved()
    {
        _savedAt = _done.Count;
        _lastPushUtc = DateTime.MinValue; // the next change is a new step
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ends the current merge run: the next change starts a new undo step.</summary>
    public void Break() => _lastPushUtc = DateTime.MinValue;

    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
        _savedAt = 0;
        _lastPushUtc = DateTime.MinValue;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool Merge(ISceneCommand previous, ISceneCommand next)
    {
        switch (previous, next)
        {
            case (ChangeLayerCommand a, ChangeLayerCommand b) when a.MergeKey is not null && a.MergeKey == b.MergeKey && a.LayerId == b.LayerId:
                a.MergeWith(b);
                return true;
            case (ChangeBackgroundCommand a, ChangeBackgroundCommand b) when a.MergeKey is not null && a.MergeKey == b.MergeKey:
                a.MergeWith(b);
                return true;
            default:
                return false;
        }
    }
}
