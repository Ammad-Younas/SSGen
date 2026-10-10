using System.Globalization;
using System.Windows;
using System.Windows.Data;
using StoreShot.Core.Setup;

namespace StoreShot.App.Converters;

/// <summary>Maps a <see cref="CheckStatus"/> to a Segoe Fluent Icons glyph.</summary>
public sealed class StatusToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CheckStatus.Pass => "",     // CheckMark
        CheckStatus.Warning => "",  // Warning
        CheckStatus.Fail => "",     // Cancel
        CheckStatus.Running => "", // Sync
        _ => "",                    // CircleRing (pending)
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="CheckStatus"/> to a theme brush from the Fluent dictionaries, so colours follow light/dark.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            CheckStatus.Pass => "SystemFillColorSuccessBrush",
            CheckStatus.Warning => "SystemFillColorCautionBrush",
            CheckStatus.Fail => "SystemFillColorCriticalBrush",
            CheckStatus.Running => "AccentFillColorDefaultBrush",
            _ => "TextFillColorTertiaryBrush",
        };
        return Application.Current.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is non-null and not an empty string. Pass "invert" to flip.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value is not null && value is not string { Length: 0 };
        if (parameter is "invert")
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool -> Visibility. Pass "invert" to flip.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter is "invert")
        {
            b = !b;
        }

        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats a UTC timestamp as a short, human relative time ("5 min ago", "yesterday", or a date).</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime utc)
        {
            return string.Empty;
        }

        var span = DateTime.UtcNow - utc;
        return span switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)span.TotalHours} h ago",
            { TotalDays: < 2 } => "yesterday",
            { TotalDays: < 7 } => $"{(int)span.TotalDays} days ago",
            _ => utc.ToLocalTime().ToString("d", culture),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
