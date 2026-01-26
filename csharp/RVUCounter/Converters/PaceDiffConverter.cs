using System.Globalization;
using System.Windows.Data;

namespace RVUCounter.Converters;

/// <summary>
/// Converts pace difference to formatted string with arrow symbols (matches Python).
/// Example: +5.3 -> "▲ +5.3 ahead", -2.1 -> "▼ 2.1 behind"
/// </summary>
public class PaceDiffConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double diff)
        {
            if (Math.Abs(diff) < 0.1)
                return "on pace";

            if (diff > 0)
                return $"▲ +{diff:F1} ahead";
            else
                return $"▼ {Math.Abs(diff):F1} behind";
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
