using System.Windows;
using System.Windows.Controls;
using RVUCounter.Core;
using RVUCounter.Data;
using RVUCounter.ViewModels;

namespace RVUCounter.Views;

/// <summary>
/// Statistics window code-behind.
/// </summary>
public partial class StatisticsWindow : Window
{
    private readonly DataManager _dataManager;

    public StatisticsWindow(DataManager dataManager)
    {
        InitializeComponent();

        // Apply dark title bar based on current theme
        ThemeManager.ApplyCurrentThemeTitleBar(this);

        _dataManager = dataManager;
        DataContext = new StatisticsViewModel(dataManager);

        Loaded += (s, e) =>
        {
            var pos = _dataManager.Settings.StatisticsWindowPosition;
            if (pos != null && pos.X != 0 && pos.Y != 0)
            {
                Left = pos.X;
                Top = pos.Y;
                Width = pos.Width > 0 ? pos.Width : Width;
                Height = pos.Height > 0 ? pos.Height : Height;
            }
            else
            {
                // Center on screen if no saved position
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;
                Left = (screenWidth - Width) / 2;
                Top = (screenHeight - Height) / 2;
            }

            // Restore left pane width
            var savedLeftWidth = _dataManager.Settings.StatisticsLeftPaneWidth;
            if (savedLeftWidth > 0)
            {
                LeftPaneColumn.Width = new GridLength(savedLeftWidth);
            }

            // Restore charts pane width
            var savedChartsWidth = _dataManager.Settings.StatisticsChartsPaneWidth;
            if (savedChartsWidth > 0)
            {
                ChartsPaneColumn.Width = new GridLength(savedChartsWidth);
            }
        };

        Closing += (s, e) =>
        {
            _dataManager.SaveWindowPosition("statistics", Left, Top, Width, Height);

            // Save pane widths
            _dataManager.Settings.StatisticsLeftPaneWidth = LeftPaneColumn.ActualWidth;
            _dataManager.Settings.StatisticsChartsPaneWidth = ChartsPaneColumn.ActualWidth;
            _dataManager.SaveSettings();
        };
    }
}
