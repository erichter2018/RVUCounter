using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RVUCounter.Converters;

/// <summary>
/// Returns dark red color for invalid modalities, otherwise default foreground.
/// Matches Python logic for highlighting procedures that don't start with valid modality prefixes.
/// </summary>
public class ModalityColorConverter : IValueConverter
{
    // Valid modality prefixes (matches Python)
    private static readonly string[] ValidModalityPrefixes =
    {
        "CT", "MR", "US", "XR", "NM", "Multiple", "Fluoro", "PET", "DEXA", "Mammo"
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string studyType && !string.IsNullOrEmpty(studyType))
        {
            // Check if study type starts with a valid modality prefix
            var isValidModality = ValidModalityPrefixes.Any(prefix =>
                studyType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (!isValidModality && studyType != "Unknown")
            {
                // Invalid modality - return dark red
                return new SolidColorBrush(Color.FromRgb(0x8B, 0x00, 0x00)); // #8B0000 - dark red
            }
        }

        // Valid modality - return default foreground from theme
        return Application.Current.TryFindResource("PrimaryTextBrush") ?? Brushes.Black;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
