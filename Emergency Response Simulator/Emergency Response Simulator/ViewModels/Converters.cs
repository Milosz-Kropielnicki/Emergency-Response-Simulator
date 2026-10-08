using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Emergency_Response_Simulator.ViewModels;

public static class Converters
{
    /// <summary>Visible when a count is zero; used for "nothing here yet" hints.</summary>
    public static IValueConverter ZeroToVisible { get; } = new ZeroToVisibleConverter();

    private sealed class ZeroToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
