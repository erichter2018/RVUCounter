using System.Windows;
using RVUCounter.Core;
using RVUCounter.Data;
using RVUCounter.ViewModels;

namespace RVUCounter.Views;

/// <summary>
/// Tools window code-behind.
/// </summary>
public partial class ToolsWindow : Window
{
    private readonly ToolsViewModel _viewModel;
    private readonly DataManager _dataManager;

    public ToolsWindow(DataManager dataManager, Action? onDatabaseChanged = null)
    {
        InitializeComponent();

        // Apply dark title bar based on current theme
        ThemeManager.ApplyCurrentThemeTitleBar(this);

        _dataManager = dataManager;
        _viewModel = new ToolsViewModel(dataManager, onDatabaseChanged);
        DataContext = _viewModel;

        Loaded += (s, e) =>
        {
            var pos = _dataManager.Settings.ToolsWindowPosition;
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
        };

        Closing += (s, e) =>
        {
            _dataManager.SaveWindowPosition("tools", Left, Top, Width, Height);
        };
    }
}
