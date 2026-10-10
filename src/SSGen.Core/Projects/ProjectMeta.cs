namespace SSGen.Core.Projects;

/// <summary>Small per-project values (JSON documents) stored in project_meta.</summary>
public static class ProjectMeta
{
    /// <summary>Reads/writes a JSON value in project_meta (small per-project documents such as the imported APK).</summary>
    public static string? Get(Project project, string key)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var connection = project.Database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM project_meta WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public static void Set(Project project, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var connection = project.Database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO project_meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
