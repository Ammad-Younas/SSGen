using System.Collections.Concurrent;
using System.Globalization;
using SkiaSharp;

namespace StoreShot.Emulator.Devices;

/// <summary>A screen in the mock app: a title, buttons (label + bounds) and where each button leads.</summary>
public sealed record MockScreen(string Id, string Title, string Activity, IReadOnlyList<MockButton> Buttons, SKColor Color);

public sealed record MockButton(string Label, SKRectI Bounds, string? TargetScreenId);

/// <summary>
/// Deterministic in-memory device for tests and UI work without an emulator. Taps on a button navigate to its
/// target screen; Back returns to the previous screen. Every action is recorded.
/// </summary>
public sealed class MockDevice : IDevice
{
    public const int Width = 1080;
    public const int Height = 2400;

    private readonly Dictionary<string, MockScreen> _screens;
    private readonly Stack<string> _history = new();
    private string _current;

    public MockDevice(IEnumerable<MockScreen>? screens = null, string? startScreenId = null)
    {
        _screens = (screens ?? DefaultScreens()).ToDictionary(s => s.Id);
        _current = startScreenId ?? _screens.Keys.First();
    }

    public string Serial => "mock-1";
    public string DisplayName => "Mock device";
    public DeviceKind Kind => DeviceKind.Mock;

    public MockScreen CurrentScreen => _screens[_current];

    /// <summary>Every action performed, e.g. "tap 540 1200", "key Back", "text hello".</summary>
    public ConcurrentQueue<string> Actions { get; } = new();

    public HashSet<string> InstalledPackages { get; } = new(StringComparer.Ordinal);

    public Task<string> GetAbiAsync(CancellationToken ct = default) => Task.FromResult("x86_64");

    public Task<int> GetApiLevelAsync(CancellationToken ct = default) => Task.FromResult(36);

    public Task<(int Width, int Height)> GetScreenSizeAsync(CancellationToken ct = default) => Task.FromResult((Width, Height));

    public Task<byte[]> CaptureScreenshotPngAsync(CancellationToken ct = default) => Task.FromResult(Render(CurrentScreen));

    public Task<string> DumpUiHierarchyAsync(CancellationToken ct = default)
    {
        var s = CurrentScreen;
        var nodes = string.Concat(s.Buttons.Select((b, i) =>
            $"<node index=\"{i}\" text=\"{System.Security.SecurityElement.Escape(b.Label)}\" class=\"android.widget.Button\" package=\"com.example.mock\" clickable=\"true\" enabled=\"true\" " +
            $"bounds=\"[{b.Bounds.Left},{b.Bounds.Top}][{b.Bounds.Right},{b.Bounds.Bottom}]\" />"));
        var xml = $"<?xml version='1.0' encoding='UTF-8' standalone='yes' ?><hierarchy rotation=\"0\"><node index=\"0\" text=\"{System.Security.SecurityElement.Escape(s.Title)}\" class=\"android.widget.FrameLayout\" package=\"com.example.mock\" bounds=\"[0,0][{Width},{Height}]\">{nodes}</node></hierarchy>";
        return Task.FromResult(xml);
    }

    public Task<string?> GetForegroundActivityAsync(CancellationToken ct = default) => Task.FromResult<string?>("com.example.mock/" + CurrentScreen.Activity);

    public Task TapAsync(int x, int y, CancellationToken ct = default)
    {
        Actions.Enqueue(string.Create(CultureInfo.InvariantCulture, $"tap {x} {y}"));
        var hit = CurrentScreen.Buttons.FirstOrDefault(b => b.Bounds.Contains(x, y));
        if (hit?.TargetScreenId is { } target && _screens.ContainsKey(target))
        {
            _history.Push(_current);
            _current = target;
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
        return Task.CompletedTask;
    }

    public Task PressKeyAsync(AndroidKey key, CancellationToken ct = default)
    {
        Actions.Enqueue("key " + key);
        if (key == AndroidKey.Back && _history.Count > 0)
        {
            _current = _history.Pop();
        }
        else if (key == AndroidKey.Home)
        {
            _history.Clear();
            _current = _screens.Keys.First();
        }

        return Task.CompletedTask;
    }

    public Task InstallAsync(IReadOnlyList<string> apkPaths, CancellationToken ct = default)
    {
        Actions.Enqueue("install " + string.Join(",", apkPaths.Select(Path.GetFileName)));
        InstalledPackages.Add("com.example.mock");
        return Task.CompletedTask;
    }

    public Task<bool> IsInstalledAsync(string packageName, CancellationToken ct = default) => Task.FromResult(InstalledPackages.Contains(packageName));

    public Task LaunchAsync(string packageName, string? activity, CancellationToken ct = default)
    {
        Actions.Enqueue("launch " + packageName);
        _history.Clear();
        _current = _screens.Keys.First();
        return Task.CompletedTask;
    }

    public Task ForceStopAsync(string packageName, CancellationToken ct = default)
    {
        Actions.Enqueue("stop " + packageName);
        return Task.CompletedTask;
    }

    public Task<int?> GetPidAsync(string packageName, CancellationToken ct = default) => Task.FromResult<int?>(4242);

    public static IReadOnlyList<MockScreen> DefaultScreens()
    {
        static MockButton Btn(string label, int row, string? target) => new(label, new SKRectI(140, 900 + (row * 260), 940, 1100 + (row * 260)), target);
        return
        [
            new("home", "Home", ".MainActivity", [Btn("Workouts", 0, "workouts"), Btn("Progress", 1, "progress"), Btn("Settings", 2, "settings")], new SKColor(0x3F, 0x51, 0xB5)),
            new("workouts", "Workouts", ".WorkoutsActivity", [Btn("Start workout", 0, "session"), Btn("Back", 1, "home")], new SKColor(0x00, 0x96, 0x88)),
            new("session", "Workout session", ".SessionActivity", [Btn("Finish", 0, "workouts")], new SKColor(0xE9, 0x1E, 0x63)),
            new("progress", "Progress", ".ProgressActivity", [Btn("Weekly", 0, null), Btn("Monthly", 1, null)], new SKColor(0xFF, 0x98, 0x00)),
            new("settings", "Settings", ".SettingsActivity", [Btn("Delete account", 0, null), Btn("Buy premium", 1, null)], new SKColor(0x60, 0x7D, 0x8B)),
        ];
    }

    /// <summary>Draws the screen as a phone-sized PNG.</summary>
    public static byte[] Render(MockScreen screen)
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

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>Provides the mock device (enabled with the STORESHOT_MOCK_DEVICE=1 environment variable, and in tests).</summary>
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
