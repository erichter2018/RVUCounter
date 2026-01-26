using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace RVUCounter.Converters;

/// <summary>
/// Returns true if text is ONLY a dollar amount (e.g., "$1,234,567" or "$56.78"), otherwise false.
/// Does NOT match mixed text like "$5,909 (100 studies, 140.4 RVU)".
/// </summary>
public class DollarToGreenConverter : IValueConverter
{
    // Matches pure dollar amounts: $X,XXX,XXX or $X,XXX.XX (with optional commas and decimals)
    private static readonly Regex PureDollarRegex = new(@"^\s*\$[\d,]+(\.\d{2})?\s*$", RegexOptions.Compiled);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Only return true for cells that are ONLY a dollar amount (no other text)
        if (value is string text && PureDollarRegex.IsMatch(text))
            return true;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
