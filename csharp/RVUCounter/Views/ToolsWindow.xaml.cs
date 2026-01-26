using System.IO;
using System.Windows;
using Microsoft.Win32;
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

    private void ExportDatabase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "SQLite Database|*.db|All Files|*.*",
            FileName = $"rvu_counter_backup_{DateTime.Now:yyyyMMdd}.db"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var sourcePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RVUCounter", "database.db");

                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, dialog.FileName, overwrite: true);
                    MessageBox.Show($"Database exported to:\n{dialog.FileName}", "Export Complete",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Database file not found.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "YAML File|*.yaml|All Files|*.*",
            FileName = $"rvu_counter_settings_{DateTime.Now:yyyyMMdd}.yaml"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var sourcePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RVUCounter", "user_settings.yaml");

                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, dialog.FileName, overwrite: true);
                    MessageBox.Show($"Settings exported to:\n{dialog.FileName}", "Export Complete",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Settings file not found.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ImportDatabase_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will replace your current database. Are you sure?",
            "Confirm Import",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var dialog = new OpenFileDialog
        {
            Filter = "SQLite Database|*.db|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var destPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RVUCounter", "database.db");

                // Create backup of current
                if (File.Exists(destPath))
                {
                    var backupPath = destPath + $".backup_{DateTime.Now:yyyyMMddHHmmss}";
                    File.Move(destPath, backupPath);
                }

                File.Copy(dialog.FileName, destPath);
                MessageBox.Show(
                    "Database imported successfully.\nPlease restart the application.",
                    "Import Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
