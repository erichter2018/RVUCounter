using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace RVUCounter.Core;

/// <summary>
/// Manages application themes (Light/Dark mode) and global font size scaling.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _currentTheme;
    private static bool _isDarkMode;
    private static double _fontSizeAdjustment = 0;

    // Base font sizes used throughout the app
    private const double BaseFontSizeXSmall = 9;
    private const double BaseFontSizeSmall = 10;
    private const double BaseFontSizeNormal = 11;
    private const double BaseFontSizeMedium = 12;
    private const double BaseFontSizeLarge = 14;
    private const double BaseFontSizeXLarge = 18;

    #region Win32 API for Dark Title Bar

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    #endregion

    /// <summary>
    /// Apply the specified theme to the application.
    /// </summary>
    /// <param name="isDarkMode">True for dark theme, false for light theme.</param>
    public static void ApplyTheme(bool isDarkMode)
    {
        try
        {
            _isDarkMode = isDarkMode;

            var themePath = isDarkMode
                ? "pack://application:,,,/Themes/DarkTheme.xaml"
                : "pack://application:,,,/Themes/LightTheme.xaml";

            var themeUri = new Uri(themePath, UriKind.Absolute);
            var newTheme = new ResourceDictionary { Source = themeUri };

            var app = Application.Current;
            if (app == null) return;

            // Remove old theme if present
            if (_currentTheme != null)
            {
                app.Resources.MergedDictionaries.Remove(_currentTheme);
            }

            // Add new theme
            app.Resources.MergedDictionaries.Add(newTheme);
            _currentTheme = newTheme;

            // Apply dark title bar to all open windows
            foreach (Window window in app.Windows)
            {
                ApplyDarkTitleBar(window, isDarkMode);
            }

            Log.Information("Applied {Theme} theme", isDarkMode ? "Dark" : "Light");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply theme");
        }
    }

    /// <summary>
    /// Apply dark/light title bar to a specific window.
    /// Call this when a new window is created.
    /// </summary>
    public static void ApplyDarkTitleBar(Window window, bool isDarkMode)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                // Window not yet initialized, hook into SourceInitialized
                window.SourceInitialized += (s, e) => ApplyDarkTitleBarToHandle(window, isDarkMode);
                return;
            }

            ApplyDarkTitleBarToHandle(window, isDarkMode);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to apply dark title bar to window");
        }
    }

    /// <summary>
    /// Apply dark title bar using the window handle.
    /// </summary>
    private static void ApplyDarkTitleBarToHandle(Window window, bool isDarkMode)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useImmersiveDarkMode = isDarkMode ? 1 : 0;

            // Try the newer attribute first (Windows 10 20H1+)
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int)) != 0)
            {
                // Fall back to older attribute (Windows 10 1809-1909)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
            }

            Log.Debug("Applied {Mode} title bar to {Window}", isDarkMode ? "dark" : "light", window.GetType().Name);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to set dark title bar attribute");
        }
    }

    /// <summary>
    /// Apply current theme's title bar to a window.
    /// Call this from window's constructor or Loaded event.
    /// </summary>
    public static void ApplyCurrentThemeTitleBar(Window window)
    {
        ApplyDarkTitleBar(window, _isDarkMode);
    }

    /// <summary>
    /// Gets whether dark mode is currently active.
    /// </summary>
    public static bool IsDarkMode => _isDarkMode;

    /// <summary>
    /// Gets the current font size adjustment.
    /// </summary>
    public static double FontSizeAdjustment => _fontSizeAdjustment;

    /// <summary>
    /// Apply global font size adjustment to all windows.
    /// </summary>
    /// <param name="adjustment">The font size adjustment in points (-2.0 to +4.0).</param>
    public static void ApplyFontSize(double adjustment)
    {
        try
        {
            _fontSizeAdjustment = Math.Clamp(adjustment, -2.0, 4.0);

            var app = Application.Current;
            if (app == null) return;

            // Set font size resources that can be referenced throughout the app
            app.Resources["FontSizeXSmall"] = BaseFontSizeXSmall + _fontSizeAdjustment;
            app.Resources["FontSizeSmall"] = BaseFontSizeSmall + _fontSizeAdjustment;
            app.Resources["FontSizeNormal"] = BaseFontSizeNormal + _fontSizeAdjustment;
            app.Resources["FontSizeMedium"] = BaseFontSizeMedium + _fontSizeAdjustment;
            app.Resources["FontSizeLarge"] = BaseFontSizeLarge + _fontSizeAdjustment;
            app.Resources["FontSizeXLarge"] = BaseFontSizeXLarge + _fontSizeAdjustment;

            Log.Information("Applied font size adjustment: {Adjustment} pt", _fontSizeAdjustment);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply font size adjustment");
        }
    }

    /// <summary>
    /// Get the adjusted font size for a given base size.
    /// </summary>
    public static double GetAdjustedFontSize(double baseSize)
    {
        return Math.Max(6, baseSize + _fontSizeAdjustment); // Minimum 6pt
    }
}
