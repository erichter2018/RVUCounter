using System.Globalization;
using System.Windows.Data;

namespace RVUCounter.Converters;

/// <summary>
/// Converts duration in seconds to a "1m 11s" format (matches Python).
/// </summary>
public class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double? seconds = value switch
        {
            double d => d,
            int i => i,
            float f => f,
            null => null,
            _ => null
        };

        if (seconds == null || seconds <= 0)
            return "";

        if (seconds < 60)
        {
            return $"{(int)seconds}s";
        }
        else
        {
            var minutes = (int)(seconds / 60);
            var remainingSeconds = (int)(seconds % 60);
            return $"{minutes}m {remainingSeconds}s";
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
