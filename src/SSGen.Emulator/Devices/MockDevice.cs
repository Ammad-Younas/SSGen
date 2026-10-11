using System.Collections.Concurrent;
using System.Globalization;
using System.Security;
using SkiaSharp;

namespace SSGen.Emulator.Devices;

public enum MockElementKind
{
    Button,
    TextField,
    PasswordField,
    Label,
    Spinner,
}

/// <summary>
/// A screen in the mock app: a title, elements and where each button leads. Special targets: "!crash" (the app
/// dies and the launcher shows) and "!back" (like pressing Back, e.g. a dialog button).
/// </summary>
public sealed record MockScreen(string Id, string Title, string Activity, IReadOnlyList<MockButton> Buttons, SKColor Color)
{
    /// <summary>Package that owns the screen; another package simulates a system dialog or another app.</summary>
    public string Package { get; init; } = MockDevice.AppPackage;

    /// <summary>For login screens: field label to required text. A button with <see cref="MockButton.Submits"/> only
    /// navigates when all match (and the target then becomes the launch screen, like a saved session).</summary>
    public IReadOnlyDictionary<string, string>? RequiredInput { get; init; }

    /// <summary>Loading screens: after this many UI dumps the screen moves on to <see cref="AutoAdvanceTo"/>.</summary>
    public int AutoAdvanceAfterDumps { get; init; }

    public string? AutoAdvanceTo { get; init; }
}

public sealed record MockButton(string Label, SKRectI Bounds, string? TargetScreenId, MockElementKind Kind = MockElementKind.Button, string? ResourceId = null)
{
    public bool Submits { get; init; }
}

/// <summary>
/// Deterministic in-memory device for tests and UI work without an emulator. Taps on a button navigate to its
/// target screen; Back returns to the previous screen and, on the first screen, leaves the app (like Android).
/// Text fields take typed text; the keyboard shows while a field is focused. Every action is recorded.
/// </summary>
public sealed class MockDevice : IDevice
{
    public const int Width = 1080;
    public const int Height = 2400;
    public const string AppPackage = "com.example.mock";
    public const string LauncherPackage = "com.google.android.apps.nexuslauncher";
    private const string LauncherId = "!launcher";

    private readonly Dictionary<string, MockScreen> _screens;
    private readonly Stack<string> _history = new();
    private readonly Dictionary<string, string> _typed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _dumps = new(StringComparer.Ordinal);
    private string _current;
    private string _launchScreen;
    private string? _focusedField;
    private bool _running = true;

    public MockDevice(IEnumerable<MockScreen>? screens = null, string? startScreenId = null)
    {
        _screens = (screens ?? DefaultScreens()).ToDictionary(s => s.Id);
        _screens[LauncherId] = new MockScreen(LauncherId, "Launcher", ".NexusLauncherActivity", [], SKColors.DarkSlateGray) { Package = LauncherPackage };
        _current = _launchScreen = startScreenId ?? _screens.Keys.First();
    }

    public string Serial => "mock-1";
    public string DisplayName => "Mock device";
    public DeviceKind Kind => DeviceKind.Mock;

    public MockScreen CurrentScreen => _screens[_current];

    public bool KeyboardShown { get; private set; }

    /// <summary>Every action performed, e.g. "tap 540 1200", "key Back", "text hello".</summary>
    public ConcurrentQueue<string> Actions { get; } = new();

    public HashSet<string> InstalledPackages { get; } = new(StringComparer.Ordinal);

    /// <summary>Buttons actually hit by taps, as "screenId:label".</summary>
    public ConcurrentQueue<string> TappedButtons { get; } = new();

    /// <summary>Text currently in a field, by "screenId:label".</summary>
    public string? TypedText(string screenId, string label) => _typed.GetValueOrDefault($"{screenId}:{label}");

    public Task<string> GetAbiAsync(CancellationToken ct = default) => Task.FromResult("x86_64");

    public Task<int> GetApiLevelAsync(CancellationToken ct = default) => Task.FromResult(36);

    public Task<(int Width, int Height)> GetScreenSizeAsync(CancellationToken ct = default) => Task.FromResult((Width, Height));

    public Task<byte[]> CaptureScreenshotPngAsync(CancellationToken ct = default) => Task.FromResult(Render(CurrentScreen, KeyboardShown));

    public Task<string> DumpUiHierarchyAsync(CancellationToken ct = default)
    {
        var s = CurrentScreen;
        var xml = $"<?xml version='1.0' encoding='UTF-8' standalone='yes' ?><hierarchy rotation=\"0\"><node index=\"0\" text=\"{Esc(s.Title)}\" class=\"android.widget.FrameLayout\" package=\"{s.Package}\" enabled=\"true\" bounds=\"[0,0][{Width},{Height}]\">{string.Concat(s.Buttons.Select((b, i) => Node(s, b, i)))}</node></hierarchy>";

        // Loading screens move on after a few looks.
        if (s.AutoAdvanceTo is { } next && (_dumps[s.Id] = _dumps.GetValueOrDefault(s.Id) + 1) >= s.AutoAdvanceAfterDumps)
        {
            _dumps[s.Id] = 0;
            _current = next;
        }

        return Task.FromResult(xml);
    }

    private string Node(MockScreen s, MockButton b, int i)
    {
        var bounds = $"[{b.Bounds.Left},{b.Bounds.Top}][{b.Bounds.Right},{b.Bounds.Bottom}]";
        var id = Esc(b.ResourceId ?? "");
        var pkg = s.Package;
        return b.Kind switch
        {
            MockElementKind.TextField or MockElementKind.PasswordField =>
                // Like Android: an empty field shows its hint as text; a password field never exposes its text.
                $"<node index=\"{i}\" text=\"{Esc(b.Kind == MockElementKind.PasswordField ? (TypedText(s.Id, b.Label) is { Length: > 0 } p ? new string('•', p.Length) : b.Label) : TypedText(s.Id, b.Label) ?? b.Label)}\" resource-id=\"{id}\" class=\"android.widget.EditText\" package=\"{pkg}\" clickable=\"true\" focusable=\"true\" enabled=\"true\" password=\"{(b.Kind == MockElementKind.PasswordField ? "true" : "false")}\" bounds=\"{bounds}\" />",
            MockElementKind.Label => $"<node index=\"{i}\" text=\"{Esc(b.Label)}\" resource-id=\"{id}\" class=\"android.widget.TextView\" package=\"{pkg}\" clickable=\"false\" enabled=\"true\" bounds=\"{bounds}\" />",
            MockElementKind.Spinner => $"<node index=\"{i}\" text=\"\" resource-id=\"{id}\" class=\"android.widget.ProgressBar\" package=\"{pkg}\" clickable=\"false\" enabled=\"true\" bounds=\"{bounds}\" />",
            _ => $"<node index=\"{i}\" text=\"{Esc(b.Label)}\" resource-id=\"{id}\" class=\"android.widget.Button\" package=\"{pkg}\" clickable=\"true\" enabled=\"true\" bounds=\"{bounds}\" />",
        };
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    public Task<string?> GetForegroundActivityAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>($"{CurrentScreen.Package}/{CurrentScreen.Activity}");

    public Task TapAsync(int x, int y, CancellationToken ct = default)
    {
        Actions.Enqueue(string.Create(CultureInfo.InvariantCulture, $"tap {x} {y}"));
        var screen = CurrentScreen;
        var hit = screen.Buttons.FirstOrDefault(b => b.Bounds.Contains(x, y) && b.Kind is not MockElementKind.Label and not MockElementKind.Spinner);
        if (hit is null)
        {
            return Task.CompletedTask;
        }

        TappedButtons.Enqueue($"{_current}:{hit.Label}");
        if (hit.Kind is MockElementKind.TextField or MockElementKind.PasswordField)
        {
            _focusedField = $"{_current}:{hit.Label}";
            KeyboardShown = true;
            return Task.CompletedTask;
        }

        _focusedField = null;
        KeyboardShown = false;
        if (hit.Submits && screen.RequiredInput is { } required &&
            !required.All(kv => string.Equals(TypedText(screen.Id, kv.Key), kv.Value, StringComparison.Ordinal)))
        {
            return Task.CompletedTask; // wrong or missing credentials: stay on the login screen
        }

        switch (hit.TargetScreenId)
        {
            case "!crash":
                _running = false;
                _history.Clear();
                _current = LauncherId;
                break;
            case "!back":
                GoBack();
                break;
            case { } target when _screens.ContainsKey(target):
                if (hit.Submits)
                {
                    _launchScreen = target; // the session is "saved"
                    _history.Clear();
                }
                else
                {
                    _history.Push(_current);
                }

                _current = target;
                break;
        }

        return Task.CompletedTask;
    }

    public Task SwipeAsync(int x1, int y1, int x2, int y2, int durationMs = 300, CancellationToken ct = default)
    {
        Actions.Enqueue(string.Create(CultureInfo.InvariantCulture, $"swipe {x1} {y1} {x2} {y2}"));
        return Task.CompletedTask;
    }

    public Task InputTextAsync(string text, CancellationToken ct = default)
    {
        Actions.Enqueue("text " + text);
        if (_focusedField is { } f)
        {
            _typed[f] = _typed.GetValueOrDefault(f, "") + text;
        }

        return Task.CompletedTask;
    }

    public Task PressKeyAsync(AndroidKey key, CancellationToken ct = default)
    {
        Actions.Enqueue("key " + key);
        switch (key)
        {
            case AndroidKey.Back when KeyboardShown:
            case AndroidKey.Escape:
                KeyboardShown = false; // like Android: Back first closes the keyboard
                break;
            case AndroidKey.Back:
                GoBack();
                break;
            case AndroidKey.Home:
                _history.Clear();
                _current = LauncherId;
                break;
            case AndroidKey.Delete when _focusedField is { } f && _typed.GetValueOrDefault(f) is { Length: > 0 } t:
                _typed[f] = t[..^1];
                break;
        }

        return Task.CompletedTask;
    }

    private void GoBack()
    {
        KeyboardShown = false;
        _focusedField = null;
        if (_history.Count > 0)
        {
            _current = _history.Pop();
        }
        else if (_current != LauncherId)
        {
            _current = LauncherId; // Back on the first screen leaves the app
        }
    }

    public Task InstallAsync(IReadOnlyList<string> apkPaths, CancellationToken ct = default)
    {
        Actions.Enqueue("install " + string.Join(",", apkPaths.Select(Path.GetFileName)));
        InstalledPackages.Add(AppPackage);
        return Task.CompletedTask;
    }

    public Task<bool> IsInstalledAsync(string packageName, CancellationToken ct = default) => Task.FromResult(InstalledPackages.Contains(packageName));

    public Task LaunchAsync(string packageName, string? activity, CancellationToken ct = default)
    {
        Actions.Enqueue("launch " + packageName);
        _history.Clear();
        _running = true;
        KeyboardShown = false;
        _focusedField = null;
        _current = _launchScreen;
        return Task.CompletedTask;
    }

    public Task ForceStopAsync(string packageName, CancellationToken ct = default)
    {
        Actions.Enqueue("stop " + packageName);
        _running = false;
        return Task.CompletedTask;
    }

    public Task<int?> GetPidAsync(string packageName, CancellationToken ct = default) => Task.FromResult<int?>(_running ? 4242 : null);

    public Task<bool> IsKeyboardShownAsync(CancellationToken ct = default) => Task.FromResult(KeyboardShown);

    public bool StatusBarDemo { get; private set; }

    public Task SetStatusBarDemoAsync(bool enabled, CancellationToken ct = default)
    {
        Actions.Enqueue("demo " + (enabled ? "on" : "off"));
        StatusBarDemo = enabled;
        return Task.CompletedTask;
    }

    public static MockButton Btn(string label, int row, string? target, MockElementKind kind = MockElementKind.Button, string? id = null) =>
        new(label, new SKRectI(140, 900 + (row * 260), 940, 1100 + (row * 260)), target, kind, id);

    public static IReadOnlyList<MockScreen> DefaultScreens() =>
    [
        new("home", "Home", ".MainActivity", [Btn("Workouts", 0, "workouts"), Btn("Progress", 1, "progress"), Btn("Settings", 2, "settings")], new SKColor(0x3F, 0x51, 0xB5)),
        new("workouts", "Workouts", ".WorkoutsActivity", [Btn("Start workout", 0, "session"), Btn("Back", 1, "home")], new SKColor(0x00, 0x96, 0x88)),
        new("session", "Workout session", ".SessionActivity", [Btn("Finish", 0, "workouts")], new SKColor(0xE9, 0x1E, 0x63)),
        new("progress", "Progress", ".ProgressActivity", [Btn("Weekly", 0, null), Btn("Monthly", 1, null)], new SKColor(0xFF, 0x98, 0x00)),
        new("settings", "Settings", ".SettingsActivity", [Btn("Delete account", 0, null), Btn("Buy premium", 1, null)], new SKColor(0x60, 0x7D, 0x8B)),
    ];

    /// <summary>Draws the screen as a phone-sized PNG (with a keyboard panel when it's showing).</summary>
    public static byte[] Render(MockScreen screen, bool keyboard = false)
    {
        ArgumentNullException.ThrowIfNull(screen);
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var c = surface.Canvas;
        c.Clear(SKColors.White);
        using var header = new SKPaint { Color = screen.Color };
        c.DrawRect(0, 0, Width, 420, header);
        using var font = new SKFont(SKTypeface.Default, 72);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        c.DrawText(screen.Title, 80, 300, SKTextAlign.Left, font, white);
        using var button = new SKPaint { Color = screen.Color.WithAlpha(40), IsAntialias = true };
        using var text = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var buttonFont = new SKFont(SKTypeface.Default, 56);
        foreach (var b in screen.Buttons)
        {
            c.DrawRoundRect(new SKRoundRect(SKRect.Create(b.Bounds.Left, b.Bounds.Top, b.Bounds.Width, b.Bounds.Height), 40), button);
            c.DrawText(b.Label, b.Bounds.MidX, b.Bounds.MidY + 20, SKTextAlign.Center, buttonFont, text);
        }

        if (keyboard)
        {
            using var kb = new SKPaint { Color = new SKColor(0x30, 0x30, 0x30) };
            c.DrawRect(0, Height - 900, Width, 900, kb);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>Provides the mock device (enabled with the SSGEN_MOCK_DEVICE=1 environment variable, and in tests).</summary>
public sealed class MockEmulatorProvider : IEmulatorProvider
{
    private readonly MockDevice _device;

    public MockEmulatorProvider(MockDevice? device = null) => _device = device ?? new MockDevice();

    public DeviceTargetKind Kind => DeviceTargetKind.Mock;

    public Task<IReadOnlyList<DeviceTarget>> ListTargetsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeviceTarget>>([new DeviceTarget("mock", "Mock device", DeviceTargetKind.Mock, "In-memory test device", _device.Serial)]);

    public Task<IDevice> ConnectAsync(DeviceTarget target, DeviceStartOptions options, IProgress<string>? progress, CancellationToken ct = default) =>
        Task.FromResult<IDevice>(_device);

    public Task StopAsync(DeviceTarget target, CancellationToken ct = default) => Task.CompletedTask;
}
