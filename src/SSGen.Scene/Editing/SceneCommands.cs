using System.Text.Json;

namespace SSGen.Scene.Editing;

/// <summary>One undoable change to a scene document (command pattern).</summary>
public interface ISceneCommand
{
    /// <summary>Shown in the Undo/Redo tooltips, e.g. "Move", "Change colour".</summary>
    string Name { get; }

    void Apply(SceneDocument scene);

    void Revert(SceneDocument scene);
}

/// <summary>Layers are stored as JSON in commands, so later edits to the live layer never change the history.</summary>
internal static class LayerJson
{
    public static string Write(Layer layer) => JsonSerializer.Serialize(layer, SceneJson.Options);

    public static Layer Read(string json) => JsonSerializer.Deserialize<Layer>(json, SceneJson.Options) ?? throw new JsonException("Empty layer.");

    public static int IndexOf(SceneDocument scene, string id)
    {
        var index = scene.Layers.FindIndex(l => l.Id == id);
        return index >= 0 ? index : throw new InvalidOperationException($"Layer {id} isn't in the document.");
    }
}

/// <summary>Any change to one layer's properties: the layer before and after.</summary>
public sealed class ChangeLayerCommand : ISceneCommand
{
    public ChangeLayerCommand(string name, string layerId, string before, string after, string? mergeKey = null)
    {
        Name = name;
        LayerId = layerId;
        Before = before;
        After = after;
        MergeKey = mergeKey;
    }

    public string Name { get; }

    public string LayerId { get; }

    public string Before { get; }

    public string After { get; private set; }

    /// <summary>Consecutive changes with the same key (typing in one box, one drag) become one undo step.</summary>
    public string? MergeKey { get; }

    public void Apply(SceneDocument scene) => Replace(scene, After);

    public void Revert(SceneDocument scene) => Replace(scene, Before);

    internal void MergeWith(ChangeLayerCommand later) => After = later.After;

    private void Replace(SceneDocument scene, string json) => scene.Layers[LayerJson.IndexOf(scene, LayerId)] = LayerJson.Read(json);
}

public sealed class AddLayerCommand : ISceneCommand
{
    private readonly int _index;
    private readonly string _json;
    private readonly string _id;

    public AddLayerCommand(Layer layer, int index, string name = "Add layer")
    {
        ArgumentNullException.ThrowIfNull(layer);
        _index = index;
        _json = LayerJson.Write(layer);
        _id = layer.Id;
        Name = name;
    }

    public string Name { get; }

    public void Apply(SceneDocument scene) => scene.Layers.Insert(Math.Clamp(_index, 0, scene.Layers.Count), LayerJson.Read(_json));

    public void Revert(SceneDocument scene) => scene.Layers.RemoveAt(LayerJson.IndexOf(scene, _id));
}

public sealed class RemoveLayerCommand : ISceneCommand
{
    private readonly int _index;
    private readonly string _json;
    private readonly string _id;

    public RemoveLayerCommand(SceneDocument scene, string layerId)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _index = LayerJson.IndexOf(scene, layerId);
        _json = LayerJson.Write(scene.Layers[_index]);
        _id = layerId;
    }

    public string Name => "Delete layer";

    public void Apply(SceneDocument scene) => scene.Layers.RemoveAt(LayerJson.IndexOf(scene, _id));

    public void Revert(SceneDocument scene) => scene.Layers.Insert(Math.Clamp(_index, 0, scene.Layers.Count), LayerJson.Read(_json));
}

/// <summary>Changes the drawing order (index 0 = bottom).</summary>
public sealed class ReorderLayerCommand : ISceneCommand
{
    private readonly string _id;
    private readonly int _from;
    private readonly int _to;

    public ReorderLayerCommand(string layerId, int from, int to)
    {
        _id = layerId;
        _from = from;
        _to = to;
    }

    public string Name => _to > _from ? "Bring forward" : "Send backward";

    public void Apply(SceneDocument scene) => Move(scene, _to);

    public void Revert(SceneDocument scene) => Move(scene, _from);

    private void Move(SceneDocument scene, int to)
    {
        var index = LayerJson.IndexOf(scene, _id);
        var layer = scene.Layers[index];
        scene.Layers.RemoveAt(index);
        scene.Layers.Insert(Math.Clamp(to, 0, scene.Layers.Count), layer);
    }
}

public sealed class ChangeBackgroundCommand : ISceneCommand
{
    public ChangeBackgroundCommand(Fill before, Fill after, string? mergeKey = null)
    {
        Before = before;
        After = after;
        MergeKey = mergeKey;
    }

    public string Name => "Change background";

    public Fill Before { get; }

    public Fill After { get; private set; }

    public string? MergeKey { get; }

    public void Apply(SceneDocument scene) => scene.Background = After;

    public void Revert(SceneDocument scene) => scene.Background = Before;

    internal void MergeWith(ChangeBackgroundCommand later) => After = later.After;
}

/// <summary>Replaces the whole document (applying a style from another image is one undo step).</summary>
public sealed class ReplaceDocumentCommand : ISceneCommand
{
    private readonly string _before;
    private readonly string _after;

    public ReplaceDocumentCommand(string name, SceneDocument before, SceneDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        Name = name;
        _before = before.ToJson();
        _after = after.ToJson();
    }

    public string Name { get; }

    public void Apply(SceneDocument scene) => CopyInto(scene, _after);

    public void Revert(SceneDocument scene) => CopyInto(scene, _before);

    private static void CopyInto(SceneDocument scene, string json)
    {
        var source = SceneDocument.FromJson(json);
        scene.Width = source.Width;
        scene.Height = source.Height;
        scene.Background = source.Background;
        scene.Layers = source.Layers;
        scene.Meta = source.Meta;
    }
}
