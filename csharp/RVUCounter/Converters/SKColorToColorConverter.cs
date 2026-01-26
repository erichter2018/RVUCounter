using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SkiaSharp;

namespace RVUCounter.Converters;

/// <summary>
/// Converts SkiaSharp SKColor to WPF Color
/// </summary>
public class SKColorToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SKColor skColor)
            return Color.FromArgb(skColor.Alpha, skColor.Red, skColor.Green, skColor.Blue);
        return Colors.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Color color)
            return new SKColor(color.R, color.G, color.B, color.A);
        return SKColors.Gray;
    }
}
