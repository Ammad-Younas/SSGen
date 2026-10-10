using System.Globalization;
using SSGen.Agent.Graph;
using SSGen.Core.Projects;

namespace SSGen.Agent.Runs;

/// <summary>Persists exploration runs, their screen graph and log into the project database.</summary>
public sealed class AgentRunStore
{
    private readonly Project _project;

    public AgentRunStore(Project project) => _project = project;

    public long StartRun(string strategy)
    {
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO agent_runs(started_utc, strategy, status) VALUES ($t, $s, 'running'); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.Parameters.AddWithValue("$s", strategy);
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

    public void SaveNode(long runId, ScreenNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO screen_nodes(run_id, node_key, screen_id, activity, dhash, label, visits) VALUES ($r, $k, $s, $a, $h, $l, $v)
            ON CONFLICT(run_id, node_key) DO UPDATE SET screen_id = excluded.screen_id, label = excluded.label, visits = excluded.visits;
            """;
        cmd.Parameters.AddWithValue("$r", runId);
        cmd.Parameters.AddWithValue("$k", node.Id);
        cmd.Parameters.AddWithValue("$s", (object?)node.ScreenId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", (object?)node.Activity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", node.Fingerprint.DHash.ToString("x16", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$l", node.Label);
        cmd.Parameters.AddWithValue("$v", node.Visits);
        cmd.ExecuteNonQuery();
    }

    public void SaveEdge(long runId, ScreenEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO screen_edges(run_id, from_node, to_node, action) VALUES ($r, $f, $t, $a);";
        cmd.Parameters.AddWithValue("$r", runId);
        cmd.Parameters.AddWithValue("$f", edge.From);
        cmd.Parameters.AddWithValue("$t", edge.To);
        cmd.Parameters.AddWithValue("$a", edge.Action);
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
}
