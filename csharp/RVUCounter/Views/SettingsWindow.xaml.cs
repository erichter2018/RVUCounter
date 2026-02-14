using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RVUCounter.Core;
using RVUCounter.Data;
using RVUCounter.ViewModels;

namespace RVUCounter.Views;

/// <summary>
/// Settings window code-behind.
/// </summary>
public partial class SettingsWindow : Window
{
    private Point _dragStartPoint;
    private int _lastDragOverIndex = -1;
    private Popup? _dragGhostPopup;
    private MainStatOrderItem? _draggedStatItem;
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
        // Revert any live-previewed changes
        _viewModel.RevertFontSize();
        _viewModel.RevertTheme();
        DialogResult = false;
        Close();
    }

    private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is ColorOverrideItem item)
        {
            var dialog = new System.Windows.Forms.ColorDialog();

            // Try to set current color
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(item.HexColor);
                dialog.Color = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
            }
            catch { }

            dialog.FullOpen = true;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var c = dialog.Color;
                item.HexColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
        }
    }

    private void DropboxHelp_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "1. Go to dropbox.com/developers/apps and click \"Create app\"\n" +
            "2. Choose \"Scoped access\" and \"App folder\"\n" +
            "3. Name it anything (e.g. \"RVU Backup\") and click Create\n" +
            "4. Go to the Permissions tab and enable:\n" +
            "     - files.metadata.read\n" +
            "     - files.content.read\n" +
            "     - files.content.write\n" +
            "5. Click Submit on the Permissions page\n" +
            "6. Go to the Settings tab and copy your App Key\n" +
            "7. Paste the App Key into the field below and click \"Authorize Dropbox\"\n" +
            "8. Sign in to Dropbox in the browser and click Allow\n" +
            "9. Paste the authorization code back into the app\n\n" +
            "Important: Each user must create their own Dropbox app\n" +
            "and authorize their own account. Never share your App Key\n" +
            "or settings folder with other users.",
            "Dropbox Backup Setup",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
                    // Save the app key and refresh token together
                    _dataManager.Settings.DropboxAppKey = appKey;
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

    private void DisplayOrderList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _lastDragOverIndex = -1;
    }

    private void DisplayOrderList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not System.Windows.Controls.ListBox listBox) return;

        var currentPos = e.GetPosition(null);
        if (Math.Abs(currentPos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var listBoxItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (listBoxItem?.DataContext is not MainStatOrderItem draggedItem) return;

        _draggedStatItem = draggedItem;
        ShowDragGhost(draggedItem.Label, e.GetPosition(this));
        DragDrop.DoDragDrop(listBoxItem, draggedItem, System.Windows.DragDropEffects.Move);
        HideDragGhost();
        _draggedStatItem = null;
        _lastDragOverIndex = -1;
    }

    private void DisplayOrderList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(MainStatOrderItem))) return;
        if (sender is not System.Windows.Controls.ListBox listBox) return;
        UpdateDragGhostPosition(e.GetPosition(this));

        var sourceItem = (MainStatOrderItem)e.Data.GetData(typeof(MainStatOrderItem))!;
        var targetListBoxItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        var targetItem = targetListBoxItem?.DataContext as MainStatOrderItem;
        if (targetItem == null || ReferenceEquals(sourceItem, targetItem))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        var oldIndex = _viewModel.MainStatOrderItems.IndexOf(sourceItem);
        var newIndex = _viewModel.MainStatOrderItems.IndexOf(targetItem);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (newIndex != _lastDragOverIndex)
        {
            _viewModel.MainStatOrderItems.Move(oldIndex, newIndex);
            _viewModel.NotifyMainStatOrderChanged();
            _lastDragOverIndex = newIndex;
        }

        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
    }

    private void DisplayOrderList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(MainStatOrderItem))) return;
        if (sender is not System.Windows.Controls.ListBox listBox) return;

        var sourceItem = (MainStatOrderItem)e.Data.GetData(typeof(MainStatOrderItem))!;
        var targetListBoxItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        var targetItem = targetListBoxItem?.DataContext as MainStatOrderItem;
        if (targetItem == null || ReferenceEquals(sourceItem, targetItem)) return;

        var oldIndex = _viewModel.MainStatOrderItems.IndexOf(sourceItem);
        var newIndex = _viewModel.MainStatOrderItems.IndexOf(targetItem);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;

        _viewModel.MainStatOrderItems.Move(oldIndex, newIndex);
        _viewModel.NotifyMainStatOrderChanged();
        listBox.SelectedItem = sourceItem;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T found)
                return found;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ShowDragGhost(string label, Point position)
    {
        if (_dragGhostPopup == null)
        {
            var text = new TextBlock
            {
                Name = "DragGhostText",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 30, 136, 229)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 5, 10, 5),
                Child = text
            };

            _dragGhostPopup = new Popup
            {
                AllowsTransparency = true,
                Placement = PlacementMode.Relative,
                PlacementTarget = this,
                StaysOpen = true,
                IsHitTestVisible = false,
                Child = border
            };
        }

        if (_dragGhostPopup.Child is Border b && b.Child is TextBlock t)
        {
            t.Text = $"Moving: {label}";
        }

        UpdateDragGhostPosition(position);
        _dragGhostPopup.IsOpen = true;
    }

    private void UpdateDragGhostPosition(Point position)
    {
        if (_dragGhostPopup == null) return;
        _dragGhostPopup.HorizontalOffset = position.X + 14;
        _dragGhostPopup.VerticalOffset = position.Y + 10;
    }

    private void HideDragGhost()
    {
        if (_dragGhostPopup != null)
        {
            _dragGhostPopup.IsOpen = false;
        }
    }

}
