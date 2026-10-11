using Microsoft.Data.Sqlite;

namespace SSGen.Core.Data;

/// <summary>
/// A SQLite file plus the migration set that defines its schema.
/// Connections are short-lived; open one per unit of work.
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(string path, MigrationSet migrations)
    {
        Path = path;
        Migrations = migrations;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public string Path { get; }
    public MigrationSet Migrations { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Creates the file if needed and applies pending migrations. Before upgrading an existing database it saves a
    /// copy next to it (name.v{old version}.bak, the newest three kept). Returns the resulting schema version.
    /// </summary>
    public int Migrate()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        using var connection = Open();
        var current = MigrationRunner.GetVersion(connection);
        if (current > 0 && MigrationRunner.Load(Migrations).Any(m => m.Version > current))
        {
            Backup(connection, current);
        }

        return MigrationRunner.Apply(connection, Migrations);
    }

    /// <summary>The backups made before schema upgrades, newest first.</summary>
    public IReadOnlyList<string> Backups()
    {
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, System.IO.Path.GetFileName(Path) + ".v*.bak").OrderByDescending(File.GetLastWriteTimeUtc).ToList()
            : [];
    }

    private void Backup(SqliteConnection connection, int version)
    {
        // SQLite's online backup copies a consistent snapshot, including pages still in the WAL file.
        var target = $"{Path}.v{version}.bak";
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()))
        {
            copy.Open();
            connection.BackupDatabase(copy);
        }

        foreach (var old in Backups().Skip(3))
        {
            File.Delete(old);
        }
    }
}
