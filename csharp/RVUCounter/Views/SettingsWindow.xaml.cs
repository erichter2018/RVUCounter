using System.Windows;
using RVUCounter.Core;
using RVUCounter.Data;
using RVUCounter.ViewModels;

namespace RVUCounter.Views;

/// <summary>
/// Settings window code-behind.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly DataManager _dataManager;
    private readonly MainViewModel? _mainViewModel;

    public SettingsWindow(DataManager dataManager, MainViewModel? mainViewModel = null)
    {
        InitializeComponent();

        // Apply dark title bar based on current theme
        ThemeManager.ApplyCurrentThemeTitleBar(this);

        _dataManager = dataManager;
        _mainViewModel = mainViewModel;
        _viewModel = new SettingsViewModel(dataManager);
        DataContext = _viewModel;

        Loaded += (s, e) =>
        {
            var pos = _dataManager.Settings.SettingsWindowPosition;
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
            _dataManager.SaveWindowPosition("settings", Left, Top, Width, Height);
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Save();
        
        // Refresh main window display settings
        _mainViewModel?.RefreshSettings();
        
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // Revert any live-previewed font size changes
        _viewModel.RevertFontSize();
        DialogResult = false;
        Close();
    }

    private async void AuthorizeDropbox_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var appKey = _viewModel.DropboxAppKey;
            if (string.IsNullOrWhiteSpace(appKey))
            {
                MessageBox.Show("Please enter your Dropbox App Key first.", "Dropbox Authorization",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Start PKCE flow - opens browser
            BackupManager.StartPkceAuthorization(appKey);

            // Prompt user for the authorization code
            var codeDialog = new DropboxCodeDialog();
            codeDialog.Owner = this;
            if (codeDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(codeDialog.AuthorizationCode))
            {
                var (success, refreshToken, error) = await BackupManager.CompletePkceAuthorizationAsync(
                    appKey, codeDialog.AuthorizationCode);

                if (success && refreshToken != null)
                {
                    // Save the refresh token
                    _dataManager.Settings.DropboxRefreshToken = refreshToken;
                    _dataManager.Settings.DropboxBackupEnabled = true;
                    _dataManager.SaveSettings();

                    MessageBox.Show("Dropbox authorization successful!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Authorization failed: {error}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Dropbox authorization failed");
            MessageBox.Show($"Authorization failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}
