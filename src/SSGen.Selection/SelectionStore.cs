using System.Globalization;
using SSGen.Core.Projects;

namespace SSGen.Selection;

/// <summary>One screen's place in the store set, with its (editable) captions.</summary>
public sealed record SelectionItem(
    long ScreenId,
    bool Included,
    int? Position,
    double? Score,
    string? Reject,
    string Label,
    string Headline,
    string Subheadline,
    string Reasoning,
    string Source);

/// <summary>Saves the ranking and the chosen set in the project database.</summary>
public sealed class SelectionStore
{
    private readonly Project _project;

    public SelectionStore(Project project) => _project = project;

    /// <summary>Stores a ranking: every assessment, with the picked ones included in story order.</summary>
    public void SaveRanking(IReadOnlyList<ScreenAssessment> all, IReadOnlyList<ScreenAssessment> picked)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(picked);
        var positions = picked.Select((a, i) => (a.ScreenId, i)).ToDictionary(x => x.ScreenId, x => x.i);
        var pickedById = picked.ToDictionary(a => a.ScreenId);
        using var c = _project.Database.Open();
        using var tx = c.BeginTransaction();
        foreach (var a0 in all)
        {
            var a = pickedById.TryGetValue(a0.ScreenId, out var p) ? p : a0; // picked ones may carry polished captions
            Upsert(c, tx, new SelectionItem(a.ScreenId, positions.ContainsKey(a.ScreenId), positions.TryGetValue(a.ScreenId, out var pos) ? pos : null,
                a.Score, a.Reject, a.Label, a.Headline, a.Subheadline, a.Reasoning, a.Source));
        }

        tx.Commit();
    }

    public void Save(SelectionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var c = _project.Database.Open();
        Upsert(c, null, item);
    }

    /// <summary>Rewrites positions of the included items in the given order (others are excluded).</summary>
    public void SetOrder(IReadOnlyList<long> includedInOrder)
    {
        ArgumentNullException.ThrowIfNull(includedInOrder);
        using var c = _project.Database.Open();
        using var tx = c.BeginTransaction();
        using (var clear = c.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "UPDATE selection SET included = 0, position = NULL;";
            clear.ExecuteNonQuery();
        }

        for (var i = 0; i < includedInOrder.Count; i++)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE selection SET included = 1, position = $p, updated_utc = $t WHERE screen_id = $id;";
            cmd.Parameters.AddWithValue("$p", i);
            cmd.Parameters.AddWithValue("$t", Now());
            cmd.Parameters.AddWithValue("$id", includedInOrder[i]);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<SelectionItem> Load()
    {
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT screen_id, included, position, score, reject, COALESCE(label, ''), COALESCE(headline, ''), COALESCE(subheadline, ''), COALESCE(reasoning, ''), source
            FROM selection ORDER BY included DESC, position, score DESC;
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<SelectionItem>();
        while (r.Read())
        {
            list.Add(new SelectionItem(
                r.GetInt64(0), r.GetInt64(1) != 0, r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetDouble(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9)));
        }

        return list;
    }

    private static void Upsert(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction? tx, SelectionItem item)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO selection(screen_id, included, position, score, reject, label, headline, subheadline, reasoning, source, updated_utc)
            VALUES ($id, $inc, $pos, $score, $rej, $label, $head, $sub, $why, $src, $t)
            ON CONFLICT(screen_id) DO UPDATE SET included = excluded.included, position = excluded.position, score = excluded.score,
                reject = excluded.reject, label = excluded.label, headline = excluded.headline, subheadline = excluded.subheadline,
                reasoning = excluded.reasoning, source = excluded.source, updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$id", item.ScreenId);
        cmd.Parameters.AddWithValue("$inc", item.Included ? 1 : 0);
        cmd.Parameters.AddWithValue("$pos", (object?)item.Position ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$score", (object?)item.Score ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rej", (object?)item.Reject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$label", item.Label);
        cmd.Parameters.AddWithValue("$head", item.Headline);
        cmd.Parameters.AddWithValue("$sub", item.Subheadline);
        cmd.Parameters.AddWithValue("$why", item.Reasoning);
        cmd.Parameters.AddWithValue("$src", item.Source);
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.ExecuteNonQuery();
    }

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
