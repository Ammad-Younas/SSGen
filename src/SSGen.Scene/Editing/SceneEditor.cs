namespace SSGen.Scene.Editing;

/// <summary>
/// A document being edited: every change goes through here so it lands in the undo history.
/// Live gestures (drags) change the layer directly and are recorded once when they end.
/// </summary>
public sealed class SceneEditor
{
    private string? _gestureLayerId;
    private string? _gestureBefore;

    public SceneEditor(SceneDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        History.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public SceneDocument Document { get; }

    public EditHistory History { get; } = new();

    /// <summary>The document changed (any edit, undo, redo or save).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised during a gesture, when the document changed but nothing is recorded yet.</summary>
    public event EventHandler? Previewed;

    public bool IsDirty => History.IsDirty;

    public Layer? Find(string? id) => id is null ? null : Document.Layers.FirstOrDefault(l => l.Id == id);

    /// <summary>Changes one layer as one undo step (merged with the previous step when the merge key matches).</summary>
    public void Modify(string layerId, Action<Layer> change, string name, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        var layer = Find(layerId) ?? throw new InvalidOperationException($"Layer {layerId} isn't in the document.");
        var before = LayerJson.Write(layer);
        change(layer);
        var after = LayerJson.Write(layer);
        if (before != after)
        {
            History.Record(new ChangeLayerCommand(name, layerId, before, after, mergeKey));
        }
    }

    public void SetBackground(Fill fill, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (fill != Document.Background)
        {
            History.Execute(Document, new ChangeBackgroundCommand(Document.Background, fill, mergeKey));
        }
    }

    /// <summary>Adds a layer on top (or at <paramref name="index"/>), giving it a unique id.</summary>
    public Layer Add(Layer layer, int? index = null, string name = "Add layer")
    {
        ArgumentNullException.ThrowIfNull(layer);
        while (Document.Layers.Any(l => l.Id == layer.Id))
        {
            layer.Id = Guid.NewGuid().ToString("N")[..8];
        }

        History.Execute(Document, new AddLayerCommand(layer, index ?? Document.Layers.Count, name));
        return Find(layer.Id)!;
    }

    public void Remove(string layerId) => History.Execute(Document, new RemoveLayerCommand(Document, layerId));

    /// <summary>A copy just above the original, offset so it's visible.</summary>
    public Layer Duplicate(string layerId)
    {
        var source = Find(layerId) ?? throw new InvalidOperationException($"Layer {layerId} isn't in the document.");
        var copy = LayerJson.Read(LayerJson.Write(source));
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.Name = (source.Name.Length > 0 ? source.Name : "Layer") + " copy";
        copy.X += Math.Max(12, Document.Width * 0.02);
        copy.Y += Math.Max(12, Document.Width * 0.02);
        copy.Locked = false;
        return Add(copy, Document.Layers.IndexOf(source) + 1, "Duplicate layer");
    }

    /// <summary>Moves a layer up (+1, towards the front) or down (-1) in the drawing order.</summary>
    public bool Reorder(string layerId, int delta)
    {
        var from = Document.Layers.FindIndex(l => l.Id == layerId);
        var to = Math.Clamp(from + delta, 0, Document.Layers.Count - 1);
        if (from < 0 || to == from)
        {
            return false;
        }

        History.Execute(Document, new ReorderLayerCommand(layerId, from, to));
        return true;
    }

    /// <summary>Replaces the whole document content as one undo step.</summary>
    public void Replace(SceneDocument replacement, string name) =>
        History.Execute(Document, new ReplaceDocumentCommand(name, Document, replacement));

    public bool Undo() => History.Undo(Document);

    public bool Redo() => History.Redo(Document);

    // ------------------------------------------------------------------ gestures

    public bool InGesture => _gestureLayerId is not null;

    /// <summary>Starts a drag on a layer: changes are previewed live and recorded as one step at the end.</summary>
    public void BeginGesture(string layerId)
    {
        var layer = Find(layerId) ?? throw new InvalidOperationException($"Layer {layerId} isn't in the document.");
        _gestureLayerId = layerId;
        _gestureBefore = LayerJson.Write(layer);
    }

    public void UpdateGesture(Action<Layer> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Find(_gestureLayerId) is { } layer)
        {
            change(layer);
            Previewed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void EndGesture(string name)
    {
        if (Find(_gestureLayerId) is { } layer && _gestureBefore is { } before)
        {
            var after = LayerJson.Write(layer);
            if (after != before)
            {
                History.Break();
                History.Record(new ChangeLayerCommand(name, layer.Id, before, after));
                History.Break();
            }
        }

        _gestureLayerId = null;
        _gestureBefore = null;
    }

    /// <summary>Undoes an unfinished gesture (Escape during a drag).</summary>
    public void CancelGesture()
    {
        if (_gestureLayerId is { } id && _gestureBefore is { } before)
        {
            var index = Document.Layers.FindIndex(l => l.Id == id);
            if (index >= 0)
            {
                Document.Layers[index] = LayerJson.Read(before);
            }

            Previewed?.Invoke(this, EventArgs.Empty);
        }

        _gestureLayerId = null;
        _gestureBefore = null;
    }
}
