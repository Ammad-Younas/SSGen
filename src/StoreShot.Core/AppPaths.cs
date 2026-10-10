namespace StoreShot.Core;

/// <summary>
/// Well-known folders used by the app. Everything lives under a single data root so tests
/// can point it at a temp directory.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Data folder name before the app was renamed to SSGen; migrated on first start.</summary>
    private const string LegacyFolderName = "StoreShotStudio";

    public AppPaths(string? dataRoot = null)
    {
        if (dataRoot is null)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dataRoot = Path.Combine(localAppData, "SSGen");
            MigrateLegacyFolder(Path.Combine(localAppData, LegacyFolderName), dataRoot);
        }

        DataRoot = dataRoot;
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CacheDir);
    }

    public string DataRoot { get; }
    public string AppDatabase => Path.Combine(DataRoot, "app.db");
    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string CacheDir => Path.Combine(DataRoot, "cache");

    public static string DefaultProjectsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SSGen", "Projects");

    /// <summary>Moves settings, recent projects, secrets and logs from the old data folder, once.</summary>
    internal static void MigrateLegacyFolder(string legacy, string target)
    {
        if (Directory.Exists(target) || !Directory.Exists(legacy))
        {
            return;
        }

        try
        {
            Directory.Move(legacy, target);
        }
        catch (IOException)
        {
            // Locked or on another volume: start fresh rather than fail to launch; the old folder stays intact.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
