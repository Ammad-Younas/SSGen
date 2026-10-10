using Microsoft.Data.Sqlite;
using SSGen.Core.Data;

namespace SSGen.Core.Tests;

/// <summary>A temp directory deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ssgen-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Creates an empty file (and its parent folders) at a relative path.</summary>
    public string Touch(params string[] parts)
    {
        var file = Combine(parts);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Empty);
        return file;
    }

    public void Dispose()
    {
        // SQLite may hold files briefly after connections close.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class TestDb
{
    public static SqliteDatabase CreateApp(TempDir dir)
    {
        var db = new SqliteDatabase(dir.Combine("app.db"), MigrationSet.App);
        db.Migrate();
        return db;
    }
}
