using System.Windows;
using RVUCounter.Core;
using RVUCounter.Data;
using RVUCounter.Logic;
using Serilog;

namespace RVUCounter.Views;

/// <summary>
/// A blocking dialog that warns about HIPAA compliance and offers migration.
/// Ported from Python hipaa_warning_window.py.
/// </summary>
public partial class HIPAAWarningWindow : Window
{
    private readonly DataManager _dataManager;
    private readonly BackupManager _backupManager;

    public HIPAAMigrationResult Result { get; private set; } = HIPAAMigrationResult.Ignored;

    public HIPAAWarningWindow(DataManager dataManager, BackupManager backupManager)
    {
        InitializeComponent();

        // Apply dark title bar based on current theme
        ThemeManager.ApplyCurrentThemeTitleBar(this);

        _dataManager = dataManager;
        _backupManager = backupManager;
    }

    private void IgnoreButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure? You will be non-compliant and risk data leakage.",
            "Confirm Ignore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            Result = HIPAAMigrationResult.Ignored;
            DialogResult = false;
            Close();
        }
    }

    private async void MigrateButton_Click(object sender, RoutedEventArgs e)
    {
        // Disable buttons and show progress
        MigrateButton.IsEnabled = false;
        IgnoreButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            var migrator = new HIPAAMigrator(_dataManager, _backupManager);
            migrator.ProgressChanged += OnProgressChanged;

            var migrationResult = await migrator.RunMigrationAsync();

            if (migrationResult.Success)
            {
                var message =
                    "Migration Complete! You are now HIPAA Compliant.\n\n" +
                    "IMPORTANT:\n" +
                    "A folder named 'RVU_PRE_MIGRATION_BACKUP_DO_NOT_UPLOAD' has been created " +
                    "in your application folder.\n\n" +
                    "1. This contains your un-hashed data. Keep it safe offline or delete it " +
                    "if the app works well.\n" +
                    "2. DO NOT upload this folder to the cloud.\n\n" +
                    $"Records migrated: {migrationResult.RecordsMigrated}\n" +
                    $"Backup files sanitized: {migrationResult.BackupFilesMigrated}\n\n" +
                    "Please RESTART the application now.";

                MessageBox.Show(message, "Success - Restart Required", MessageBoxButton.OK, MessageBoxImage.Information);
                Result = HIPAAMigrationResult.Compliant;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show(
                    $"Migration Failed: {migrationResult.ErrorMessage}\n\nSee logs for details.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Result = HIPAAMigrationResult.Failed;

                // Re-enable buttons
                MigrateButton.IsEnabled = true;
                IgnoreButton.IsEnabled = true;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HIPAA migration failed");
            MessageBox.Show(
                $"Migration Failed: {ex.Message}\n\nSee logs for details.",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Result = HIPAAMigrationResult.Failed;

            // Re-enable buttons
            MigrateButton.IsEnabled = true;
            IgnoreButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnProgressChanged(string message, double progress)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressLabel.Text = message;
            ProgressBar.Value = progress * 100;
        });
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // If migration is in progress, prevent closing
        if (!MigrateButton.IsEnabled && ProgressPanel.Visibility == Visibility.Visible)
        {
            e.Cancel = true;
            MessageBox.Show(
                "Migration is in progress. Please wait.",
                "Please Wait",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        base.OnClosing(e);
    }
}

/// <summary>
/// Result of HIPAA migration dialog.
/// </summary>
public enum HIPAAMigrationResult
{
    /// <summary>User chose to ignore the warning.</summary>
    Ignored,
    /// <summary>Migration completed successfully.</summary>
    Compliant,
    /// <summary>Migration failed.</summary>
    Failed
}
