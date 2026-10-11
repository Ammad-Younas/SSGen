namespace SSGen.Core.Settings;

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

    /// <summary>Default parent folder for new projects. Null means Documents\SSGen\Projects.</summary>
    public string? ProjectsFolder { get; init; }

    /// <summary>True once the user has finished (or explicitly skipped) the first-run setup assistant.</summary>
    public bool SetupCompleted { get; init; }

    /// <summary>
    /// Emulator page pane proportions (device | screen | app &amp; logcat) as star weights; null = defaults.
    /// Proportions, not pixels, so the layout always fits the window.
    /// </summary>
    public double[]? EmulatorPaneWeights { get; init; }

    /// <summary>
    /// Explore page split proportions as pixel-derived star weights, in pairs: controls/rest, top/screens,
    /// current screen/activity, log/map. Null = defaults.
    /// </summary>
    public double[]? ExplorePaneWeights { get; init; }

    /// <summary>Design page column proportions (screens, preview, options). Null = defaults.</summary>
    public double[]? DesignPaneWeights { get; init; }

    /// <summary>Editor page column proportions (layers, canvas, properties). Null = defaults.</summary>
    public double[]? EditorPaneWeights { get; init; }

    /// <summary>Left navigation collapsed to icons only.</summary>
    public bool NavCollapsed { get; init; }

    /// <summary>Show the emulator's own window as well as the preview inside SSGen.</summary>
    public bool ShowEmulatorWindow { get; init; }

    /// <summary>Agent safe mode: block destructive or costly actions. Always on by default.</summary>
    public bool SafeMode { get; init; } = true;

    /// <summary>Extra words/phrases the agent must never tap, one per line (added to the built-in blocklist).</summary>
    public string AgentBlocklistExtra { get; init; } = "";

    /// <summary>The provider profile used when a task has no explicit assignment. Null = no AI (manual mode).</summary>
    public string? ActiveProviderId { get; init; }

    /// <summary>Spending cap per exploration run for paid cloud providers (USD). Null = not set yet (asked on first paid use).</summary>
    public decimal? CostCapUsd { get; init; }

    /// <summary>The user agreed that screenshots may be sent to cloud AI providers.</summary>
    public bool CloudScreenshotConsent { get; init; }

    /// <summary>The first-run "choose an AI provider or skip" step has been shown.</summary>
    public bool AiOnboardingDone { get; init; }
}
