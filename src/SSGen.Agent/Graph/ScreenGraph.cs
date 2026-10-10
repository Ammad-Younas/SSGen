using SSGen.Agent.Observation;

namespace SSGen.Agent.Graph;

/// <summary>A unique screen discovered during exploration.</summary>
public sealed class ScreenNode
{
    internal ScreenNode(int id, ScreenFingerprint fingerprint)
    {
        Id = id;
        Fingerprint = fingerprint;
    }

    public int Id { get; }

    public ScreenFingerprint Fingerprint { get; }

    public string? Activity => Fingerprint.Activity;

    /// <summary>Short human name ("Workout list"), from the AI or derived from the activity.</summary>
    public string Label { get; set; } = "";

    public int Visits { get; internal set; }

    /// <summary>Element keys already acted on (or deliberately skipped) on this screen.</summary>
    public HashSet<string> Tried { get; } = new(StringComparer.Ordinal);

    public bool ScrolledDown { get; set; }

    /// <summary>Keys of every tappable element seen on this screen (union over visits).</summary>
    public HashSet<string> Known { get; } = new(StringComparer.Ordinal);

    public bool HasScrollable { get; set; }

    /// <summary>Back from here leaves the app (the launch screen), so "go back" is never useful here.</summary>
    public bool IsRoot { get; set; }

    /// <summary>How often each element was re-tapped just to travel to an unfinished screen (loop guard).</summary>
    public Dictionary<string, int> Revisits { get; } = new(StringComparer.Ordinal);

    /// <summary>Everything seen here has been tried (or blocked) and the list was scrolled once.</summary>
    public bool IsFinished => Known.All(Tried.Contains) && (ScrolledDown || !HasScrollable);

    /// <summary>Saved screenshot (project screens table id), if captured.</summary>
    public long? ScreenId { get; set; }

    public string? ScreenFile { get; set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Label) ? Label : ShortActivity ?? $"Screen {Id}";

    public string? ShortActivity => Activity?.Split('/').LastOrDefault()?.TrimStart('.');
}

/// <summary>A transition; <see cref="ElementKey"/> is the tapped element, so the route can be replayed.</summary>
public sealed record ScreenEdge(int From, int To, string Action, string? ElementKey = null);

/// <summary>Unique screens (nodes) and the actions that lead between them (edges).</summary>
public sealed class ScreenGraph
{
    private readonly List<ScreenNode> _nodes = [];
    private readonly List<ScreenEdge> _edges = [];

    public IReadOnlyList<ScreenNode> Nodes => _nodes;

    public IReadOnlyList<ScreenEdge> Edges => _edges;

    /// <summary>Returns the matching node (visits+1) or adds a new one.</summary>
    public (ScreenNode Node, bool IsNew) Observe(ScreenFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var existing = _nodes.FirstOrDefault(n => n.Fingerprint.Matches(fingerprint));
        if (existing is not null)
        {
            existing.Visits++;
            return (existing, false);
        }

        var node = new ScreenNode(_nodes.Count + 1, fingerprint) { Visits = 1 };
        _nodes.Add(node);
        return (node, true);
    }

    /// <summary>Adds the edge unless it is a self-loop or already known. Returns the new edge, or null.</summary>
    public ScreenEdge? AddEdge(int from, int to, string action, string? elementKey = null)
    {
        if (from == to || _edges.Any(e => e.From == from && e.To == to && e.Action == action))
        {
            return null;
        }

        var edge = new ScreenEdge(from, to, action, elementKey);
        _edges.Add(edge);
        return edge;
    }

    public ScreenNode? Get(int id) => _nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>
    /// First tap of the shortest known tap-route from <paramref name="from"/> to any unfinished screen, or null.
    /// Only routes made of replayable taps count; routes start with an element that is on screen now.
    /// </summary>
    public ScreenEdge? FirstHopToUnfinished(ScreenNode from, IReadOnlySet<string> onScreenKeys, int maxRevisits = 3)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(onScreenKeys);
        var queue = new Queue<(int Node, ScreenEdge First)>();
        var seen = new HashSet<int> { from.Id };
        foreach (var e in EdgesFrom(from.Id).Where(e => e.ElementKey is { } k && onScreenKeys.Contains(k) && from.Revisits.GetValueOrDefault(k) < maxRevisits))
        {
            if (seen.Add(e.To))
            {
                queue.Enqueue((e.To, e));
            }
        }

        while (queue.Count > 0)
        {
            var (id, first) = queue.Dequeue();
            if (Get(id) is { IsFinished: false })
            {
                return first;
            }

            foreach (var e in EdgesFrom(id).Where(e => e.ElementKey is not null))
            {
                if (seen.Add(e.To))
                {
                    queue.Enqueue((e.To, first));
                }
            }
        }

        return null;
    }

    public IEnumerable<ScreenEdge> EdgesFrom(int nodeId) => _edges.Where(e => e.From == nodeId);
}
