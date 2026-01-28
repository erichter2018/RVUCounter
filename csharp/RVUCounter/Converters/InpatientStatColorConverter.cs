using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RVUCounter.Converters;

/// <summary>
/// Converts inpatient stat percentage to color:
/// - Red: &lt; 11%
/// - Yellow: 11-13% (inclusive)
/// - Carolina Blue (#4B9CD3): &gt; 13%
/// </summary>
public class InpatientStatColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double percentage)
        {
            if (percentage < 11)
                return new SolidColorBrush(Color.FromRgb(220, 53, 69));      // Red
            if (percentage <= 13)
                return new SolidColorBrush(Color.FromRgb(255, 193, 7));      // Yellow
            return new SolidColorBrush(Color.FromRgb(75, 156, 211));         // Carolina Blue #4B9CD3
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
