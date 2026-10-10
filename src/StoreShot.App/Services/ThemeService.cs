using System.Windows;
using StoreShot.Core.Settings;

namespace StoreShot.App.Services;

/// <summary>Applies the WPF Fluent theme (Light/Dark/System) from settings and follows changes live.</summary>
public sealed class ThemeService
{
    private readonly ISettingsService _settings;

    public ThemeService(ISettingsService settings) => _settings = settings;

    public void Initialize()
    {
        Apply(_settings.Current.Theme);
        _settings.Changed += (_, s) => Application.Current.Dispatcher.Invoke(() => Apply(s.Theme));
    }

    private static void Apply(AppTheme theme)
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
    }
}
