using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RVUCounter.Converters;

/// <summary>
/// Converts ShowCriticalOnly bool to background color for the filter button.
/// Active (filtering): red background
/// Inactive: gray/transparent background
/// </summary>
public class CriticalFilterBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isActive && isActive)
        {
            return new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)); // Red when active
        }
        return new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)); // Gray when inactive
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts ShowCriticalOnly bool to text color for the filter button.
/// Active (filtering): white text
/// Inactive: light gray text
/// </summary>
public class CriticalFilterTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isActive && isActive)
        {
            return Brushes.White;
        }
        return new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)); // Light gray when inactive
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
