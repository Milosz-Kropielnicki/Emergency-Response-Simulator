using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Emergency_Response_Simulator.ViewModels;

public static class Converters
{
    /// <summary>Visible when a count is zero; used for "nothing here yet" hints.</summary>
    public static IValueConverter ZeroToVisible { get; } = new ZeroToVisibleConverter();

    /// <summary>Visible when a flag is false, e.g. an "Ack" button for unacknowledged alerts.</summary>
    public static IValueConverter FalseToVisible { get; } = new FalseToVisibleConverter();

    private sealed class FalseToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is false ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ZeroToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
