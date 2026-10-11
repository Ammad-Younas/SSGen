namespace SSGen.Emulator.Devices;

public enum DeviceKind
{
    Emulator,
    Phone,
    Mock,
}

/// <summary>Android key codes SSGen uses (android.view.KeyEvent).</summary>
public enum AndroidKey
{
    Home = 3,
    Back = 4,
    DpadUp = 19,
    DpadDown = 20,
    Tab = 61,
    Enter = 66,
    Delete = 67,
    Menu = 82,
    Escape = 111,
    MoveEnd = 123,
    AppSwitch = 187,
}

/// <summary>
/// A running Android device SSGen can observe and control. The agent (M3) works only through this interface,
/// so it can be driven by a mock in tests.
/// </summary>
public interface IDevice
{
    string Serial { get; }

    string DisplayName { get; }

    DeviceKind Kind { get; }

    /// <summary>Primary CPU ABI, e.g. "x86_64".</summary>
    Task<string> GetAbiAsync(CancellationToken ct = default);

    Task<int> GetApiLevelAsync(CancellationToken ct = default);

    /// <summary>Physical display size in pixels (portrait orientation as reported by `wm size`).</summary>
    Task<(int Width, int Height)> GetScreenSizeAsync(CancellationToken ct = default);

    Task<byte[]> CaptureScreenshotPngAsync(CancellationToken ct = default);

    /// <summary>The UI hierarchy XML from uiautomator. May be empty for games/canvas apps.</summary>
    Task<string> DumpUiHierarchyAsync(CancellationToken ct = default);

    /// <summary>"package/.Activity" of the resumed activity, or null if unknown.</summary>
    Task<string?> GetForegroundActivityAsync(CancellationToken ct = default);

    Task TapAsync(int x, int y, CancellationToken ct = default);

    Task SwipeAsync(int x1, int y1, int x2, int y2, int durationMs = 300, CancellationToken ct = default);

    Task InputTextAsync(string text, CancellationToken ct = default);

    Task PressKeyAsync(AndroidKey key, CancellationToken ct = default);

    Task InstallAsync(IReadOnlyList<string> apkPaths, CancellationToken ct = default);

    Task<bool> IsInstalledAsync(string packageName, CancellationToken ct = default);

    /// <summary>Launches the app via its launcher activity (or the package's default launcher intent if null).</summary>
    Task LaunchAsync(string packageName, string? activity, CancellationToken ct = default);

    Task ForceStopAsync(string packageName, CancellationToken ct = default);

    /// <summary>Process id of a running package, or null.</summary>
    Task<int?> GetPidAsync(string packageName, CancellationToken ct = default);

    /// <summary>
    /// Whether the soft keyboard is showing. The keyboard never appears in the UI dump, so it needs its own check
    /// (on API 36: `mInputShown=true` in `dumpsys input_method`; the Escape key hides it).
    /// </summary>
    Task<bool> IsKeyboardShownAsync(CancellationToken ct = default);
}
