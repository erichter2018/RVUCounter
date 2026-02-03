using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RVUCounter.Converters;

/// <summary>
/// Converts a numeric value to Visibility.
/// Returns Visible if the value is greater than 0, otherwise Collapsed.
/// </summary>
public class PositiveToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return intValue > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (value is double doubleValue)
        {
            return doubleValue > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
