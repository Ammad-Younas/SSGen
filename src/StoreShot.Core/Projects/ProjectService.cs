using System.Globalization;
using StoreShot.Core.Data;

namespace StoreShot.Core.Projects;

public sealed record RecentProject(string Path, string Name, DateTime LastOpenedUtc)
{
    public bool Exists => File.Exists(System.IO.Path.Combine(Path, ProjectService.ProjectDbFileName));
}

/// <summary>An open project: a folder on disk containing project.db and asset subfolders.</summary>
public sealed class Project
{
    internal Project(string folder, string name, SqliteDatabase db)
    {
        Folder = folder;
        Name = name;
        Database = db;
    }

    public string Folder { get; }
    public string Name { get; }
    public SqliteDatabase Database { get; }

    public string ScreensDir => Path.Combine(Folder, "screens");
    public string RendersDir => Path.Combine(Folder, "renders");
    public string ExportDir => Path.Combine(Folder, "export");
}

public interface IProjectService
{
    Project? Current { get; }

    event EventHandler<Project?>? CurrentChanged;

    Project Create(string name, string parentFolder);

    Project Open(string folder);

    void Close();

    IReadOnlyList<RecentProject> GetRecent(int limit = 10);

    void RemoveRecent(string path);
}

public sealed class ProjectService : IProjectService
{
    public const string ProjectDbFileName = "project.db";

    private readonly SqliteDatabase _appDb;

    public ProjectService(SqliteDatabase appDatabase) => _appDb = appDatabase;

    public Project? Current { get; private set; }

    public event EventHandler<Project?>? CurrentChanged;

    public Project Create(string name, string parentFolder)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new UserFacingException("Project name is empty.", "Enter a name for the project.");
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
        {
            throw new UserFacingException($"\"{name}\" can't be used as a folder name.", "Remove characters such as \\ / : * ? \" < > | and trailing dots.");
        }

        if (string.IsNullOrWhiteSpace(parentFolder))
        {
            throw new UserFacingException("No location chosen for the project.", "Pick a folder to create the project in.");
        }

        var folder = Path.Combine(parentFolder, name);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new UserFacingException($"The folder \"{folder}\" already exists and isn't empty.", "Choose a different name or location.");
        }

        Directory.CreateDirectory(folder);
        var db = new SqliteDatabase(Path.Combine(folder, ProjectDbFileName), MigrationSet.Project);
        db.Migrate();
        using (var connection = db.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO project_meta(key, value) VALUES ('name', $name), ('created_utc', $t);";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        return Activate(folder, name, db);
    }

    public Project Open(string folder)
    {
        var dbPath = Path.Combine(folder, ProjectDbFileName);
        if (!File.Exists(dbPath))
        {
            throw new UserFacingException($"\"{folder}\" isn't a StoreShot project (no {ProjectDbFileName}).", "Pick the project folder itself, not its parent.");
        }

        var db = new SqliteDatabase(dbPath, MigrationSet.Project);
        db.Migrate();
        string name;
        using (var connection = db.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM project_meta WHERE key = 'name';";
            name = cmd.ExecuteScalar() as string ?? Path.GetFileName(folder);
        }

        return Activate(folder, name, db);
    }

    public void Close()
    {
        if (Current is null)
        {
            return;
        }

        Current = null;
        CurrentChanged?.Invoke(this, null);
    }

    public IReadOnlyList<RecentProject> GetRecent(int limit = 10)
    {
        using var connection = _appDb.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT path, name, last_opened_utc FROM recent_projects ORDER BY last_opened_utc DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$l", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<RecentProject>();
        while (reader.Read())
        {
            list.Add(new RecentProject(
                reader.GetString(0),
                reader.GetString(1),
                DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return list;
    }

    public void RemoveRecent(string path)
    {
        using var connection = _appDb.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM recent_projects WHERE path = $p;";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    private Project Activate(string folder, string name, SqliteDatabase db)
    {
        var project = new Project(Path.GetFullPath(folder), name, db);
        Directory.CreateDirectory(project.ScreensDir);
        Directory.CreateDirectory(project.RendersDir);

        using (var connection = _appDb.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO recent_projects(path, name, last_opened_utc) VALUES ($p, $n, $t)
                ON CONFLICT(path) DO UPDATE SET name = excluded.name, last_opened_utc = excluded.last_opened_utc;
                """;
            cmd.Parameters.AddWithValue("$p", project.Folder);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        Current = project;
        CurrentChanged?.Invoke(this, project);
        return project;
    }
}
