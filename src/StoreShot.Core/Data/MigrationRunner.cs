using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace StoreShot.Core.Data;

/// <summary>Which embedded migration folder a database uses.</summary>
public enum MigrationSet
{
    /// <summary>Global app database: settings, secrets, recent projects, provider profiles.</summary>
    App,

    /// <summary>Per-project database: metadata, screens, screen graph, renders.</summary>
    Project,
}

/// <summary>
/// Applies numbered SQL scripts embedded under Data/Migrations/{Set}/NNNN_name.sql.
/// The schema version is tracked in PRAGMA user_version; each script runs in its own transaction.
/// </summary>
public static partial class MigrationRunner
{
    [GeneratedRegex(@"\.Data\.Migrations\.(?<set>\w+)\.(?<num>\d{4})_[\w]+\.sql$")]
    private static partial Regex ResourceName();

    public static IReadOnlyList<(int Version, string Sql)> Load(MigrationSet set)
    {
        var asm = typeof(MigrationRunner).Assembly;
        var list = new List<(int, string)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            var m = ResourceName().Match(name);
            if (!m.Success || m.Groups["set"].Value != set.ToString())
            {
                continue;
            }

            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add((int.Parse(m.Groups["num"].Value, System.Globalization.CultureInfo.InvariantCulture), reader.ReadToEnd()));
        }

        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Item1 != i + 1)
            {
                throw new InvalidOperationException($"Migrations for {set} must be numbered 0001.. without gaps; found {list[i].Item1:D4} at position {i + 1}.");
            }
        }

        return list;
    }

    public static int Apply(SqliteConnection connection, MigrationSet set)
    {
        var current = GetVersion(connection);
        var migrations = Load(set);
        if (current > migrations.Count)
        {
            throw new UserFacingException(
                $"This {set.ToString().ToLowerInvariant()} database was created by a newer version of SSGen (schema v{current}).",
                "Update SSGen to the latest version.");
        }

        foreach (var (version, sql) in migrations.Where(m => m.Version > current))
        {
            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"PRAGMA user_version = {version};";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return GetVersion(connection);
    }

    public static int GetVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
