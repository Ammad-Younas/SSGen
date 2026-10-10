using System.Runtime.CompilerServices;

namespace StoreShot.Emulator.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "storeshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Touch(params string[] parts)
    {
        var file = Combine(parts);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Empty);
        return file;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public static class Repo
{
    /// <summary>Repository root, found from this source file's location.</summary>
    public static string Root([CallerFilePath] string here = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>Sample APK downloaded by tools/fetch-deps.ps1, or null if it hasn't been fetched.</summary>
    public static string? SampleApk()
    {
        var p = System.IO.Path.Combine(Root(), "third_party", "samples", "privacyfriendlynotes-105.apk");
        return File.Exists(p) ? p : null;
    }

    public static string? Aapt2()
    {
        var sdk = Core.Setup.AndroidSdkLocator.Locate(null).Sdk;
        return sdk?.Aapt2Path;
    }
}

/// <summary>A fact that runs only when the sample APK has been fetched (tools/fetch-deps.ps1).</summary>
public sealed class SampleApkFactAttribute : FactAttribute
{
    public SampleApkFactAttribute()
    {
        if (Repo.SampleApk() is null)
        {
            Skip = "Sample APK missing: run tools/fetch-deps.ps1";
        }
    }
}

/// <summary>
/// A fact that boots a real emulator. Opt in with STORESHOT_INTEGRATION=1 (slow: minutes, needs the SDK).
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STORESHOT_INTEGRATION") != "1")
        {
            Skip = "Set STORESHOT_INTEGRATION=1 to run emulator integration tests.";
        }
        else if (Repo.SampleApk() is null)
        {
            Skip = "Sample APK missing: run tools/fetch-deps.ps1";
        }
    }
}
