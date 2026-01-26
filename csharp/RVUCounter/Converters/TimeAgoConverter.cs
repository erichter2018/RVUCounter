using System.Globalization;
using System.Windows.Data;

namespace RVUCounter.Converters;

/// <summary>
/// Converts a DateTime to a "X minute(s) ago" format (matches Python).
/// </summary>
public class TimeAgoConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DateTime timestamp)
        {
            var elapsed = DateTime.Now - timestamp;

            if (elapsed.TotalSeconds < 60)
            {
                var seconds = (int)elapsed.TotalSeconds;
                return seconds == 1 ? "1 second ago" : $"{seconds} seconds ago";
            }
            else if (elapsed.TotalMinutes < 60)
            {
                var minutes = (int)elapsed.TotalMinutes;
                return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
            }
            else
            {
                var hours = (int)elapsed.TotalHours;
                return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
            }
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
