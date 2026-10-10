namespace StoreShot.Core.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// User preferences persisted as JSON in the app database. Add new properties with safe defaults;
/// unknown or missing JSON fields are tolerated so older/newer files still load.
/// </summary>
public sealed record AppSettings
{
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Explicit Android SDK root. Null means auto-detect (ANDROID_HOME, ANDROID_SDK_ROOT, default location).</summary>
    public string? AndroidSdkPath { get; init; }

    /// <summary>Explicit Java home (folder containing bin\java.exe). Null means auto-detect.</summary>
    public string? JavaHomePath { get; init; }

    /// <summary>Default parent folder for new projects. Null means Documents\StoreShot Studio\Projects.</summary>
    public string? ProjectsFolder { get; init; }

    /// <summary>True once the user has finished (or explicitly skipped) the first-run setup assistant.</summary>
    public bool SetupCompleted { get; init; }

    /// <summary>Agent safe mode: block destructive or costly actions. Always on by default.</summary>
    public bool SafeMode { get; init; } = true;
}
