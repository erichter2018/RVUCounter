using System.Globalization;
using System.Windows.Data;

namespace RVUCounter.Converters;

/// <summary>
/// Converts a percentage (0.0-1.0) and a container width to a pixel width.
/// Used by PaceCarControl.
/// </summary>
public class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 &&
            values[0] is double factor &&
            values[1] is double containerWidth)
        {
            var width = factor * containerWidth;
            if (width < 0) width = 0;
            return width;
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        return Array.Empty<object>();
    }
}

/// <summary>
/// Converts a percentage (0-100) to a fixed pixel width.
/// Used for team dashboard progress bars.
/// </summary>
public class TeamBarWidthConverter : IValueConverter
{
    private const double MaxBarWidth = 140; // Fixed max width in pixels

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double percent)
        {
            // Clamp between 5 and 100 to ensure minimum visibility
            percent = Math.Max(5, Math.Min(100, percent));
            return (percent / 100.0) * MaxBarWidth;
        }
        return MaxBarWidth;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
