using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace StoreShot.App.ViewModels;

/// <summary>Small converters used only by the Emulator view.</summary>
public static class EmulatorViewHelpers
{
    /// <summary>Visible when a project is open (values[0]) but no APK is imported yet (values[1]).</summary>
    public static IMultiValueConverter ShowImportCard { get; } = new ShowImportCardConverter();

    private sealed class ShowImportCardConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [true, false] ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
