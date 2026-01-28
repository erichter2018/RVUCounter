using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RVUCounter.Converters;

/// <summary>
/// Converts a string to Visibility.
/// Returns Visible if the string is non-empty, otherwise Collapsed.
/// </summary>
public class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            return !string.IsNullOrEmpty(str) ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
