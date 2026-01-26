using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace RVUCounter.Converters;

/// <summary>
/// Extracts just the dollar amount portion from a string (e.g., "$5,909" from "$5,909 (100 studies)")
/// </summary>
public class DollarPartConverter : IValueConverter
{
    // Matches dollar amounts at the start: $X,XXX or $X,XXX.XX
    private static readonly Regex DollarRegex = new(@"^(\$[\d,]+(?:\.\d{2})?)", RegexOptions.Compiled);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            var match = DollarRegex.Match(text.TrimStart());
            if (match.Success)
                return match.Groups[1].Value;
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Extracts everything AFTER the dollar amount (e.g., " (100 studies)" from "$5,909 (100 studies)")
/// </summary>
public class NonDollarPartConverter : IValueConverter
{
    // Matches dollar amounts at the start: $X,XXX or $X,XXX.XX
    private static readonly Regex DollarRegex = new(@"^(\$[\d,]+(?:\.\d{2})?)", RegexOptions.Compiled);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            var trimmed = text.TrimStart();
            var match = DollarRegex.Match(trimmed);
            if (match.Success)
                return trimmed.Substring(match.Length);
            // No dollar amount found, return the whole string
            return text;
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
