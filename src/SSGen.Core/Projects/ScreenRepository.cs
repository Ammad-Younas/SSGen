using System.Globalization;

namespace SSGen.Core.Projects;

/// <param name="Title">App bar title from the UI tree (agent captures), if known.</param>
/// <param name="UiText">Visible text on the screen, newline-separated (agent captures), if known.</param>
public sealed record CapturedScreen(long Id, string File, string Source, string? Activity, int Width, int Height, DateTime CapturedUtc, string? Title = null, string? UiText = null)
{
    public string FullPath(Project project) => Path.Combine((project ?? throw new ArgumentNullException(nameof(project))).Folder, File);
}

/// <summary>Screens captured into a project (screens/ folder + screens table).</summary>
public static class ScreenRepository
{
    /// <summary>Saves PNG bytes into the project's screens folder and records them.</summary>
    public static CapturedScreen Add(Project project, byte[] png, string source, string? activity, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(png);
        Directory.CreateDirectory(project.ScreensDir);
        var now = DateTime.UtcNow;
        var name = $"{source}_{now.ToLocalTime():yyyyMMdd_HHmmss_fff}.png";
        File.WriteAllBytes(Path.Combine(project.ScreensDir, name), png);
        var relative = Path.Combine("screens", name);

        using var connection = project.Database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO screens(file, source, activity, width, height, captured_utc) VALUES ($f, $s, $a, $w, $h, $t);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$f", relative);
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$a", (object?)activity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", width);
        cmd.Parameters.AddWithValue("$h", height);
        cmd.Parameters.AddWithValue("$t", now.ToString("O", CultureInfo.InvariantCulture));
        var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new CapturedScreen(id, relative, source, activity, width, height, now);
    }

    /// <summary>Stores the screen's title and visible text (used for headlines without AI).</summary>
    public static void SetText(Project project, long screenId, string? title, string? uiText)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var connection = project.Database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE screens SET title = $t, ui_text = $u WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$u", (object?)uiText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", screenId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes a captured screen: its row (selection and captions go with it; renders and exploration records
    /// keep their files but forget the screen) and its image file.
    /// </summary>
    public static void Delete(Project project, long screenId)
    {
        ArgumentNullException.ThrowIfNull(project);
        string? file = null;
        using (var connection = project.Database.Open())
        {
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT file FROM screens WHERE id = $id;";
                read.Parameters.AddWithValue("$id", screenId);
                file = read.ExecuteScalar() as string;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM screens WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", screenId);
            cmd.ExecuteNonQuery();
        }

        if (file is not null)
        {
            var path = Path.Combine(project.Folder, file);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public static IReadOnlyList<CapturedScreen> List(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var connection = project.Database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, file, source, activity, width, height, captured_utc, title, ui_text FROM screens ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var list = new List<CapturedScreen>();
        while (r.Read())
        {
            list.Add(new CapturedScreen(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4), r.GetInt32(5),
                DateTime.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8)));
        }

        return list;
    }
}
