using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using StoreShot.Core.Settings;

namespace StoreShot.App.Services;

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

    private void UpdateThemeBrushes()
    {
        var key = IsDark ? SystemColors.AccentColorDark2BrushKey : SystemColors.AccentColorDark1BrushKey;
        if (Application.Current.TryFindResource(key) is System.Windows.Media.Brush brush)
        {
            Application.Current.Resources[NavSelectedBrushKey] = brush;
        }
    }
}
