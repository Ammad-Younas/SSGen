using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SSGen.Core.Settings;

namespace SSGen.App.Services;

/// <summary>
/// Applies the WPF Fluent theme (Light/Dark/System) from settings and follows changes live. Also publishes
/// <c>NavSelectedBrush</c>: the accent shade used for selected navigation/tabs, which differs per theme
/// (light: AccentColorDark1, the same red as accent buttons; dark: the deeper AccentColorDark2).
/// </summary>
public sealed class ThemeService
{
    public const string NavSelectedBrushKey = "NavSelectedBrush";

    private readonly ISettingsService _settings;

    public ThemeService(ISettingsService settings) => _settings = settings;

    public void Initialize()
    {
        Apply(_settings.Current.Theme);
        _settings.Changed += (_, s) => Application.Current.Dispatcher.Invoke(() => Apply(s.Theme));

        // In System mode the effective theme follows Windows; refresh the per-theme brushes when it changes.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            {
                Application.Current.Dispatcher.BeginInvoke(UpdateThemeBrushes);
            }
        };
    }

    /// <summary>True if the app currently renders dark (explicit Dark, or System while Windows apps use dark).</summary>
    public bool IsDark => _settings.Current.Theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0,
    };

    private void Apply(AppTheme theme)
    {
        var mode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (Application.Current.ThemeMode != mode)
        {
            Application.Current.ThemeMode = mode;
        }

        UpdateThemeBrushes();
        // Fluent reloads its dictionaries asynchronously after a theme switch; refresh once more afterwards.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateThemeBrushes);
    }

    // Fluent's dark theme fills accent controls with a pale accent and dark text. In dark mode these keys are
    // overridden so accent buttons, checked boxes/toggles/radios use the same deep red as the selected tab, with
    // white text. Value = opacity of the deep accent (hover/pressed are slightly lighter); "fg:" keys get white.
    private static readonly (string Key, double Opacity, bool Foreground)[] DarkAccentOverrides =
    [
        ("AccentFillColorDefaultBrush", 1.0, false), ("AccentFillColorSecondaryBrush", 0.9, false), ("AccentFillColorTertiaryBrush", 0.8, false),
        ("AccentButtonBackground", 1.0, false), ("AccentButtonBackgroundPointerOver", 0.9, false), ("AccentButtonBackgroundPressed", 0.8, false),
        ("AccentButtonForeground", 1.0, true), ("AccentButtonForegroundPointerOver", 1.0, true), ("AccentButtonForegroundPressed", 0.8, true),
        ("TextOnAccentFillColorPrimaryBrush", 1.0, true), ("TextOnAccentFillColorSecondaryBrush", 0.8, true),
        ("CheckBoxCheckBackgroundFillChecked", 1.0, false), ("CheckBoxCheckBackgroundFillCheckedPointerOver", 0.9, false), ("CheckBoxCheckBackgroundFillCheckedPressed", 0.8, false),
        ("CheckBoxCheckBackgroundStrokeChecked", 1.0, false), ("CheckBoxCheckBackgroundStrokeCheckedPointerOver", 0.9, false), ("CheckBoxCheckBackgroundStrokeCheckedPressed", 0.8, false),
        ("CheckBoxCheckGlyphForeground", 1.0, true), ("CheckBoxCheckGlyphForegroundPressed", 0.8, true),
        ("RadioButtonOuterEllipseCheckedFill", 1.0, false), ("RadioButtonOuterEllipseCheckedStroke", 1.0, false),
        ("RadioButtonOuterEllipseCheckedStrokePointerOver", 0.9, false), ("RadioButtonCheckOuterEllipseCheckedFillPressed", 0.8, false),
        ("ToggleButtonBackgroundChecked", 1.0, false), ("ToggleButtonBackgroundCheckedPointerOver", 0.9, false), ("ToggleButtonBackgroundCheckedPressed", 0.8, false),
        ("ToggleButtonForegroundChecked", 1.0, true), ("ToggleButtonForegroundCheckedPressed", 0.8, true),
    ];

    private void UpdateThemeBrushes()
    {
        var resources = Application.Current.Resources;
        var key = IsDark ? SystemColors.AccentColorDark2BrushKey : SystemColors.AccentColorDark1BrushKey;
        if (Application.Current.TryFindResource(key) is SolidColorBrush brush)
        {
            resources[NavSelectedBrushKey] = brush;
        }

        foreach (var o in DarkAccentOverrides)
        {
            resources.Remove(o.Key);
        }

        if (IsDark && Application.Current.TryFindResource(SystemColors.AccentColorDark2BrushKey) is SolidColorBrush dark2)
        {
            foreach (var (k, opacity, foreground) in DarkAccentOverrides)
            {
                resources[k] = Frozen(foreground ? Colors.White : dark2.Color, opacity);
            }
        }
    }
    private static SolidColorBrush Frozen(Color color, double opacity)
    {
        var b = new SolidColorBrush(color) { Opacity = opacity };
        b.Freeze();
        return b;
    }
}
