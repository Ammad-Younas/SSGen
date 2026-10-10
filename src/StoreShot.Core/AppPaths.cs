namespace StoreShot.Core;

/// <summary>
/// Well-known folders used by the app. Everything lives under a single data root so tests
/// can point it at a temp directory.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? dataRoot = null)
    {
        DataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoreShotStudio");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CacheDir);
    }

    public string DataRoot { get; }
    public string AppDatabase => Path.Combine(DataRoot, "app.db");
    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string CacheDir => Path.Combine(DataRoot, "cache");

    public static string DefaultProjectsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "StoreShot Studio", "Projects");
}
