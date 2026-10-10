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

    /// <summary>Creates the file if needed and applies pending migrations. Returns the resulting schema version.</summary>
    public int Migrate()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        using var connection = Open();
        return MigrationRunner.Apply(connection, Migrations);
    }
}
