using System.Globalization;
using System.Windows;
using System.Windows.Data;
using RVUCounter.ViewModels;

namespace RVUCounter.Converters;

/// <summary>
/// Converts a hashed accession to patient name by looking up in the MainViewModel cache.
/// Returns the patient name or empty string if not found.
/// </summary>
public class PatientNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hashedAccession || string.IsNullOrEmpty(hashedAccession))
            return string.Empty;

        // Get the MainWindow to access the ViewModel
        if (parameter is MainWindow mainWindow && mainWindow.DataContext is MainViewModel vm)
        {
            var patientName = vm.GetPatientName(hashedAccession);
            return patientName ?? string.Empty;
        }

        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts a hashed accession to Visibility based on whether patient name exists in cache.
/// Returns Visible if patient name exists, Collapsed otherwise.
/// </summary>
public class PatientNameVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hashedAccession || string.IsNullOrEmpty(hashedAccession))
            return Visibility.Collapsed;

        // Get the MainWindow to access the ViewModel
        if (parameter is MainWindow mainWindow && mainWindow.DataContext is MainViewModel vm)
        {
            var patientName = vm.GetPatientName(hashedAccession);
            return !string.IsNullOrEmpty(patientName) ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts a hashed accession to site code by looking up in the MainViewModel cache.
/// Returns "Site: XXX" or empty string if not found.
/// </summary>
public class SiteCodeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hashedAccession || string.IsNullOrEmpty(hashedAccession))
            return string.Empty;

        // Get the MainWindow to access the ViewModel
        if (parameter is MainWindow mainWindow && mainWindow.DataContext is MainViewModel vm)
        {
            var siteCode = vm.GetSiteCode(hashedAccession);
            return !string.IsNullOrEmpty(siteCode) ? $"Site: {siteCode}" : string.Empty;
        }

        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts a hashed accession to Visibility based on whether site code exists in cache.
/// Returns Visible if site code exists, Collapsed otherwise.
/// </summary>
public class SiteCodeVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hashedAccession || string.IsNullOrEmpty(hashedAccession))
            return Visibility.Collapsed;

        // Get the MainWindow to access the ViewModel
        if (parameter is MainWindow mainWindow && mainWindow.DataContext is MainViewModel vm)
        {
            var siteCode = vm.GetSiteCode(hashedAccession);
            return !string.IsNullOrEmpty(siteCode) ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
