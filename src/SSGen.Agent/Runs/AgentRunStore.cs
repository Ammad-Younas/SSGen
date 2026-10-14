using System.Globalization;
using System.Text.Json;
using SSGen.Agent.Graph;
using SSGen.Agent.Observation;
using SSGen.Core.Projects;

namespace SSGen.Agent.Runs;

/// <summary>A past run as listed in the UI.</summary>
public sealed record AgentRunInfo(long Id, DateTime StartedUtc, string Status, int Steps, int Screens, string? Summary, string Strategy)
{
    /// <summary>Runs that ended early and can be continued.</summary>
    public bool CanResume => Screens > 0 && Status is "stopped" or "limitreached" or "failed" or "interrupted";
}

/// <summary>A saved screen with its screenshot file, for showing a past run.</summary>
public sealed record SavedScreen(int NodeId, string Label, string? Activity, string? File);

/// <summary>Persists exploration runs, their screen graph (with enough state to resume) and log into the project database.</summary>
public sealed class AgentRunStore
{
    private readonly Project _project;

    public AgentRunStore(Project project) => _project = project;

    public long StartRun(string strategy, long? parentRunId = null)
    {
        using var c = _project.Database.Open();

        // A run still marked "running" belongs to a session that ended without finishing it (crash, power loss).
        using (var fix = c.CreateCommand())
        {
            fix.CommandText = "UPDATE agent_runs SET status = 'interrupted', ended_utc = COALESCE(ended_utc, $t) WHERE status = 'running';";
            fix.Parameters.AddWithValue("$t", Now());
            fix.ExecuteNonQuery();
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO agent_runs(started_utc, strategy, status, parent_run_id) VALUES ($t, $s, 'running', $p); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.Parameters.AddWithValue("$s", strategy);
        cmd.Parameters.AddWithValue("$p", (object?)parentRunId ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void FinishRun(long runId, AgentRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_runs SET ended_utc = $t, status = $st, steps = $steps, screens = $screens,
                input_tokens = $in, output_tokens = $out, cost_usd = $cost, summary = $sum WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.Parameters.AddWithValue("$st", summary.Status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$steps", summary.Steps);
        cmd.Parameters.AddWithValue("$screens", summary.Screens);
        cmd.Parameters.AddWithValue("$in", summary.Usage.InputTokens);
        cmd.Parameters.AddWithValue("$out", summary.Usage.OutputTokens);
        cmd.Parameters.AddWithValue("$cost", summary.CostUsd is { } cost ? (double)cost : DBNull.Value);
        cmd.Parameters.AddWithValue("$sum", summary.Message);
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The most recent run, or null if the project has none.</summary>
    public AgentRunInfo? LatestRun()
    {
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.started_utc, r.status, r.steps, (SELECT COUNT(*) FROM screen_nodes n WHERE n.run_id = r.id), r.summary, r.strategy
            FROM agent_runs r ORDER BY r.id DESC LIMIT 1;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new AgentRunInfo(
            reader.GetInt64(0),
            DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6));
    }

    /// <summary>Screens of a run with their screenshot files (for showing a past run's results).</summary>
    public IReadOnlyList<SavedScreen> Screens(long runId)
    {
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT n.node_key, COALESCE(n.label, ''), n.activity, s.file
            FROM screen_nodes n LEFT JOIN screens s ON s.id = n.screen_id
            WHERE n.run_id = $r ORDER BY n.node_key;
            """;
        cmd.Parameters.AddWithValue("$r", runId);
        using var reader = cmd.ExecuteReader();
        var list = new List<SavedScreen>();
        while (reader.Read())
        {
            list.Add(new SavedScreen(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : Path.Combine(_project.Folder, reader.GetString(3))));
        }

        return list;
    }

    /// <summary>Rebuilds a run's screen graph, including what was tried on each screen, so exploration can continue.</summary>
    public ScreenGraph LoadGraph(long runId)
    {
        var graph = new ScreenGraph();
        using var c = _project.Database.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT node_key, activity, dhash, label, visits, screen_id, state FROM screen_nodes WHERE run_id = $r ORDER BY node_key;";
            cmd.Parameters.AddWithValue("$r", runId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var state = reader.IsDBNull(6) ? new NodeState() : JsonSerializer.Deserialize<NodeState>(reader.GetString(6)) ?? new NodeState();
                var fingerprint = new ScreenFingerprint(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    ulong.Parse(reader.GetString(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    state.Structure.ToHashSet(StringComparer.Ordinal),
                    state.Selection.ToHashSet(StringComparer.Ordinal),
                    state.Title);
                var node = graph.Restore(reader.GetInt32(0), fingerprint, reader.GetInt32(4));
                node.Label = reader.IsDBNull(3) ? "" : reader.GetString(3);
                node.ScreenId = reader.IsDBNull(5) ? null : reader.GetInt64(5);
                state.ApplyTo(node);
            }
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT from_node, to_node, action, element_key FROM screen_edges WHERE run_id = $r ORDER BY id;";
            cmd.Parameters.AddWithValue("$r", runId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                graph.RestoreEdge(new ScreenEdge(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return graph;
    }

    public void SaveNode(long runId, ScreenNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO screen_nodes(run_id, node_key, screen_id, activity, dhash, label, visits, state) VALUES ($r, $k, $s, $a, $h, $l, $v, $st)
            ON CONFLICT(run_id, node_key) DO UPDATE SET screen_id = excluded.screen_id, label = excluded.label, visits = excluded.visits, state = excluded.state;
            """;
        cmd.Parameters.AddWithValue("$r", runId);
        cmd.Parameters.AddWithValue("$k", node.Id);
        cmd.Parameters.AddWithValue("$s", (object?)node.ScreenId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", (object?)node.Activity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", node.Fingerprint.DHash.ToString("x16", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$l", node.Label);
        cmd.Parameters.AddWithValue("$v", node.Visits);
        cmd.Parameters.AddWithValue("$st", JsonSerializer.Serialize(NodeState.From(node)));
        cmd.ExecuteNonQuery();
    }

    public void SaveEdge(long runId, ScreenEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO screen_edges(run_id, from_node, to_node, action, element_key) VALUES ($r, $f, $t, $a, $k);";
        cmd.Parameters.AddWithValue("$r", runId);
        cmd.Parameters.AddWithValue("$f", edge.From);
        cmd.Parameters.AddWithValue("$t", edge.To);
        cmd.Parameters.AddWithValue("$a", edge.Action);
        cmd.Parameters.AddWithValue("$k", (object?)edge.ElementKey ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Log(long runId, int step, AgentLogKind kind, string message)
    {
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO agent_steps(run_id, step, kind, message, ts_utc) VALUES ($r, $s, $k, $m, $t);";
        cmd.Parameters.AddWithValue("$r", runId);
        cmd.Parameters.AddWithValue("$s", step);
        cmd.Parameters.AddWithValue("$k", kind.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$m", message);
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.ExecuteNonQuery();
    }

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>What a node remembers beyond its pixels: everything the strategies use to avoid repeating work.</summary>
    private sealed class NodeState
    {
        public List<string> Structure { get; set; } = [];
        public List<string> Selection { get; set; } = [];
        public string? Title { get; set; }
        public List<string> Known { get; set; } = [];
        public List<string> Tried { get; set; } = [];
        public Dictionary<string, int> Revisits { get; set; } = [];
        public bool ScrolledDown { get; set; }
        public bool IsRoot { get; set; }
        public bool HasScrollable { get; set; }

        public static NodeState From(ScreenNode n) => new()
        {
            Structure = [.. n.Fingerprint.Structure],
            Selection = [.. n.Fingerprint.Selection],
            Title = n.Fingerprint.Title,
            Known = [.. n.Known],
            Tried = [.. n.Tried],
            Revisits = new Dictionary<string, int>(n.Revisits),
            ScrolledDown = n.ScrolledDown,
            IsRoot = n.IsRoot,
            HasScrollable = n.HasScrollable,
        };

        public void ApplyTo(ScreenNode n)
        {
            n.Known.UnionWith(Known);
            n.Tried.UnionWith(Tried);
            foreach (var (k, v) in Revisits)
            {
                n.Revisits[k] = v;
            }

            n.ScrolledDown = ScrolledDown;
            n.IsRoot = IsRoot;
            n.HasScrollable = HasScrollable;
        }
    }
}
