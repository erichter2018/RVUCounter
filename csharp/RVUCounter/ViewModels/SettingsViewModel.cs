using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RVUCounter.Core;
using RVUCounter.Data;
using RVUCounter.Logic;
using Serilog;

namespace RVUCounter.ViewModels;

/// <summary>
/// ViewModel for the Settings window.
/// Features Python parity with all counter/compensation visibility toggles.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly DataManager _dataManager;
    private double _originalFontSizeAdjustment;

    // ===========================================
    // GENERAL SETTINGS
    // ===========================================

    [ObservableProperty]
    private double _shiftLengthHours;

    [ObservableProperty]
    private int _minStudySeconds;

    [ObservableProperty]
    private bool _alwaysOnTop;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _autoResumeOnStartup;

    [ObservableProperty]
    private bool _darkMode;

    [ObservableProperty]
    private bool _showTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontSizeDisplayText))]
    private double _globalFontSizeAdjustment;

    /// <summary>
    /// Display text for the current font size adjustment.
    /// </summary>
    public string FontSizeDisplayText
    {
        get
        {
            if (GlobalFontSizeAdjustment == 0)
                return "Default";
            var sign = GlobalFontSizeAdjustment > 0 ? "+" : "";
            return $"{sign}{GlobalFontSizeAdjustment:F1} pt";
        }
    }

    /// <summary>
    /// Apply font size changes in real-time for live preview.
    /// </summary>
    partial void OnGlobalFontSizeAdjustmentChanged(double value)
    {
        ThemeManager.ApplyFontSize(value);
    }

    /// <summary>
    /// Revert font size to original value (called on Cancel).
    /// </summary>
    public void RevertFontSize()
    {
        ThemeManager.ApplyFontSize(_originalFontSizeAdjustment);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompensationRateInfo))]
    private string _role = "Partner";

    /// <summary>
    /// Displays the compensation rate range for the selected role.
    /// </summary>
    public string CompensationRateInfo
    {
        get
        {
            var isPartner = Role.StartsWith("Partner", StringComparison.OrdinalIgnoreCase);
            var (min, max) = CompensationRates.GetRateRange(isPartner);
            var roleLabel = isPartner ? "Partner" : "Associate";
            return $"{roleLabel}: ${min} - ${max}/RVU (varies by time of day and weekday/weekend)";
        }
    }

    [ObservableProperty]
    private bool _ignoreDuplicateAccessions;

    // MosaicTools integration
    [ObservableProperty]
    private bool _mosaicToolsIntegrationEnabled;

    [ObservableProperty]
    private int _mosaicToolsTimeoutSeconds;

    // Auto-update
    [ObservableProperty]
    private bool _autoUpdateEnabled;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private string _updateStatusMessage = "";

    /// <summary>
    /// Display string for the current app version.
    /// </summary>
    public string AppVersionDisplay => $"Version {UpdateManager.GetCurrentVersion()} (C#)";

    // ===========================================
    // COUNTER VISIBILITY TOGGLES
    // ===========================================

    [ObservableProperty]
    private bool _showTotal;

    [ObservableProperty]
    private bool _showAvg;

    [ObservableProperty]
    private bool _showLastHour;

    [ObservableProperty]
    private bool _showLastFullHour;

    [ObservableProperty]
    private bool _showProjected;

    [ObservableProperty]
    private bool _showProjectedShift;

    [ObservableProperty]
    private bool _showPaceCar;

    // ===========================================
    // COMPENSATION VISIBILITY TOGGLES
    // ===========================================

    [ObservableProperty]
    private bool _showCompTotal;

    [ObservableProperty]
    private bool _showCompAvg;

    [ObservableProperty]
    private bool _showCompLastHour;

    [ObservableProperty]
    private bool _showCompLastFullHour;

    [ObservableProperty]
    private bool _showCompProjected;

    [ObservableProperty]
    private bool _showCompProjectedShift;

    // ===========================================
    // COMPENSATION RATES
    // ===========================================

    [ObservableProperty]
    private double _baseRvuRate;

    [ObservableProperty]
    private double _bonusRvuRate;

    // ===========================================
    // BACKUP SETTINGS
    // ===========================================

    [ObservableProperty]
    private bool _cloudBackupEnabled;

    [ObservableProperty]
    private string _backupSchedule = "shift_end";

    [ObservableProperty]
    private bool _autoBackupOnShiftEnd;

    [ObservableProperty]
    private string _gitHubToken = "";

    [ObservableProperty]
    private string _backupRepoName = "";

    [ObservableProperty]
    private bool _dropboxEnabled;

    [ObservableProperty]
    private string _dropboxAppKey = "";

    // Backup lists
    [ObservableProperty]
    private ObservableCollection<BackupInfo> _oneDriveBackups = new();

    [ObservableProperty]
    private ObservableCollection<DropboxBackupInfo> _dropboxBackups = new();

    [ObservableProperty]
    private bool _isLoadingBackups;

    [ObservableProperty]
    private BackupInfo? _selectedOneDriveBackup;

    [ObservableProperty]
    private DropboxBackupInfo? _selectedDropboxBackup;

    // ===========================================
    // MINI INTERFACE
    // ===========================================

    [ObservableProperty]
    private string _miniMetric1 = "pace";

    [ObservableProperty]
    private string _miniMetric2 = "current_total";

    // ===========================================
    // TEAM DASHBOARD
    // ===========================================

    [ObservableProperty]
    private bool _teamDashboardEnabled;

    [ObservableProperty]
    private string _teamCode = "";

    [ObservableProperty]
    private string _teamStorageUrl = "";

    [ObservableProperty]
    private string _teamStatusMessage = "";

    [ObservableProperty]
    private bool _isTeamOperationInProgress;

    public SettingsViewModel(DataManager dataManager)
    {
        _dataManager = dataManager;
        LoadSettings();
    }

    [RelayCommand]
    private void IncreaseFontSize()
    {
        if (GlobalFontSizeAdjustment < 4.0)
        {
            GlobalFontSizeAdjustment += 0.5;
        }
    }

    [RelayCommand]
    private void DecreaseFontSize()
    {
        if (GlobalFontSizeAdjustment > -2.0)
        {
            GlobalFontSizeAdjustment -= 0.5;
        }
    }

    [RelayCommand]
    private void ResetFontSize()
    {
        GlobalFontSizeAdjustment = 0;
    }

    private const string JsonBlobBaseUrl = "https://jsonblob.com/api/jsonBlob/";

    /// <summary>
    /// Converts a team code (blob ID) to the full storage URL.
    /// </summary>
    private static string CodeToUrl(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        // If it's already a full URL, return as-is
        if (code.StartsWith("http")) return code;
        // Otherwise, construct the URL from the blob ID
        return JsonBlobBaseUrl + code.Trim();
    }

    /// <summary>
    /// Extracts the team code (blob ID) from a storage URL.
    /// </summary>
    private static string UrlToCode(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        // Extract the blob ID from the end of the URL
        var parts = url.TrimEnd('/').Split('/');
        return parts.Length > 0 ? parts[^1] : "";
    }

    [RelayCommand]
    private async Task CreateTeam()
    {
        if (IsTeamOperationInProgress) return;

        IsTeamOperationInProgress = true;
        TeamStatusMessage = "Creating team...";

        try
        {
            using var teamService = new TeamSyncService();
            var (success, _, storageUrl, error) = await teamService.CreateTeamAsync();

            if (success)
            {
                // Extract blob ID from URL to use as the shareable code
                TeamCode = UrlToCode(storageUrl);
                TeamStorageUrl = storageUrl;
                TeamDashboardEnabled = true;

                // Copy code to clipboard for easy sharing
                try
                {
                    System.Windows.Clipboard.SetText(TeamCode);
                    TeamStatusMessage = "Team created! Code copied to clipboard.";
                }
                catch
                {
                    TeamStatusMessage = "Team created! Share the code with colleagues.";
                }

                Log.Information("Created team with code {TeamCode}", TeamCode);
            }
            else
            {
                TeamStatusMessage = $"Failed: {error}";
                Log.Warning("Failed to create team: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            TeamStatusMessage = "Error creating team";
            Log.Error(ex, "Error creating team");
        }
        finally
        {
            IsTeamOperationInProgress = false;
        }
    }

    [RelayCommand]
    private async Task JoinTeam()
    {
        if (IsTeamOperationInProgress) return;

        if (string.IsNullOrWhiteSpace(TeamCode))
        {
            TeamStatusMessage = "Please enter a team code";
            return;
        }

        IsTeamOperationInProgress = true;
        TeamStatusMessage = "Joining team...";

        try
        {
            // Construct URL from code
            var url = CodeToUrl(TeamCode);

            using var teamService = new TeamSyncService();
            var (members, success) = await teamService.GetTeamStatsAsync(url);

            if (success)
            {
                TeamStorageUrl = url;
                TeamDashboardEnabled = true;
                TeamStatusMessage = $"Joined team! {members.Count} members online";
                Log.Information("Joined team {TeamCode}", TeamCode);
            }
            else
            {
                TeamStatusMessage = "Could not connect - check the code";
            }
        }
        catch (Exception ex)
        {
            TeamStatusMessage = "Error joining team - invalid code?";
            Log.Error(ex, "Error joining team");
        }
        finally
        {
            IsTeamOperationInProgress = false;
        }
    }

    [RelayCommand]
    private void CopyTeamCode()
    {
        if (string.IsNullOrWhiteSpace(TeamCode))
        {
            TeamStatusMessage = "No team code to copy";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(TeamCode);
            TeamStatusMessage = "Code copied to clipboard!";
        }
        catch
        {
            TeamStatusMessage = "Could not copy to clipboard";
        }
    }

    [RelayCommand]
    private void LeaveTeam()
    {
        TeamCode = "";
        TeamStorageUrl = "";
        TeamDashboardEnabled = false;
        TeamStatusMessage = "Left team";
        Log.Information("Left team");
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsCheckingForUpdates) return;

        IsCheckingForUpdates = true;
        UpdateStatusMessage = "Checking for updates...";

        try
        {
            var updateManager = new UpdateManager();
            var updateInfo = await updateManager.CheckForUpdateAsync();

            if (updateInfo == null)
            {
                UpdateStatusMessage = "Could not check for updates";
                return;
            }

            if (updateInfo.IsUpdateAvailable)
            {
                UpdateStatusMessage = $"Update available: v{updateInfo.Version}";

                var result = MessageBox.Show(
                    $"A new version is available: v{updateInfo.Version}\n\n" +
                    $"Release: {updateInfo.Name}\n" +
                    $"Size: {updateInfo.AssetSizeDisplay}\n\n" +
                    "Would you like to download and install it now?",
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    if (string.IsNullOrEmpty(updateInfo.DownloadUrl))
                    {
                        UpdateStatusMessage = "No download available";
                        UpdateManager.OpenReleasePage(updateInfo.ReleaseUrl);
                        return;
                    }

                    UpdateStatusMessage = "Downloading update...";

                    var progress = new Progress<double>(p =>
                    {
                        UpdateStatusMessage = $"Downloading: {p * 100:F0}%";
                    });

                    var success = await updateManager.DownloadAndApplyUpdateAsync(
                        updateInfo.DownloadUrl, progress);

                    if (success)
                    {
                        UpdateStatusMessage = "Update ready. Restarting...";

                        var restartResult = MessageBox.Show(
                            "Update downloaded successfully.\n\n" +
                            "The application will now restart to apply the update.",
                            "Update Ready",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);

                        UpdateManager.RestartApp();
                    }
                    else
                    {
                        UpdateStatusMessage = "Update failed";
                        MessageBox.Show(
                            "Failed to download or apply the update.\n\n" +
                            "You can download it manually from the releases page.",
                            "Update Failed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        UpdateManager.OpenReleasePage(updateInfo.ReleaseUrl);
                    }
                }
            }
            else
            {
                UpdateStatusMessage = "You have the latest version";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking for updates");
            UpdateStatusMessage = "Error checking for updates";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private async Task RefreshBackupLists()
    {
        if (IsLoadingBackups) return;

        IsLoadingBackups = true;

        try
        {
            // Get OneDrive backups
            var backupManager = new BackupManager(
                _dataManager.DatabasePath,
                () => _dataManager.Settings,
                s => _dataManager.SaveSettings());

            OneDriveBackups.Clear();
            var oneDriveList = backupManager.GetBackupHistory();
            foreach (var backup in oneDriveList)
            {
                OneDriveBackups.Add(backup);
            }

            // Get Dropbox backups
            DropboxBackups.Clear();
            var dropboxList = await backupManager.GetDropboxBackupsAsync();
            foreach (var backup in dropboxList)
            {
                DropboxBackups.Add(backup);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error refreshing backup lists");
        }
        finally
        {
            IsLoadingBackups = false;
        }
    }

    [RelayCommand]
    private void DeleteOneDriveBackup(BackupInfo? backup)
    {
        if (backup == null) return;

        var result = MessageBox.Show(
            $"Delete backup?\n\n{backup.FileName}",
            "Delete Backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            var backupManager = new BackupManager(
                _dataManager.DatabasePath,
                () => _dataManager.Settings,
                s => _dataManager.SaveSettings());

            if (backupManager.DeleteOneDriveBackup(backup.FilePath))
            {
                OneDriveBackups.Remove(backup);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting OneDrive backup");
            MessageBox.Show($"Failed to delete backup: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteDropboxBackup(DropboxBackupInfo? backup)
    {
        if (backup == null) return;

        var result = MessageBox.Show(
            $"Delete Dropbox backup?\n\n{backup.FileName}",
            "Delete Backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            var backupManager = new BackupManager(
                _dataManager.DatabasePath,
                () => _dataManager.Settings,
                s => _dataManager.SaveSettings());

            if (await backupManager.DeleteDropboxBackupAsync(backup.RemotePath))
            {
                DropboxBackups.Remove(backup);
            }
            else
            {
                MessageBox.Show("Failed to delete Dropbox backup", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting Dropbox backup");
            MessageBox.Show($"Failed to delete backup: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task RestoreOneDriveBackup(BackupInfo? backup)
    {
        if (backup == null) return;

        var result = MessageBox.Show(
            $"Restore database from this backup?\n\n{backup.FileName}\n\n" +
            "The current database will be backed up first.\n" +
            "The application will need to restart after restore.",
            "Restore Backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            var backupManager = new BackupManager(
                _dataManager.DatabasePath,
                () => _dataManager.Settings,
                s => _dataManager.SaveSettings());

            var (success, message) = await backupManager.RestoreFromBackupAsync(backup.FilePath);

            if (success)
            {
                MessageBox.Show(message + "\n\nThe application will now close. Please restart it.",
                    "Restore Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                // Close the application
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                MessageBox.Show($"Restore failed: {message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error restoring backup");
            MessageBox.Show($"Restore failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task RestoreDropboxBackup(DropboxBackupInfo? backup)
    {
        if (backup == null) return;

        var result = MessageBox.Show(
            $"Download and restore from this Dropbox backup?\n\n{backup.FileName}\n\n" +
            "The current database will be backed up first.\n" +
            "The application will need to restart after restore.",
            "Restore Backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            var backupManager = new BackupManager(
                _dataManager.DatabasePath,
                () => _dataManager.Settings,
                s => _dataManager.SaveSettings());

            var (success, message) = await backupManager.DownloadAndRestoreDropboxBackupAsync(backup.RemotePath);

            if (success)
            {
                MessageBox.Show(message + "\n\nThe application will now close. Please restart it.",
                    "Restore Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                // Close the application
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                MessageBox.Show($"Restore failed: {message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error restoring Dropbox backup");
            MessageBox.Show($"Restore failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task BackupNow()
    {
        try
        {
            var backupManager = new BackupManager(
                _dataManager.DatabasePath,
                () => _dataManager.Settings,
                s => _dataManager.SaveSettings());

            var result = await backupManager.CreateBackupAsync("manual");

            var messages = new List<string>();
            if (result.OneDriveSuccess)
                messages.Add($"OneDrive: {result.OneDriveMessage}");
            else if (!string.IsNullOrEmpty(result.OneDriveMessage))
                messages.Add($"OneDrive: {result.OneDriveMessage}");

            if (result.DropboxSuccess)
                messages.Add($"Dropbox: {result.DropboxMessage}");
            else if (!string.IsNullOrEmpty(result.DropboxMessage))
                messages.Add($"Dropbox: {result.DropboxMessage}");

            var message = string.Join("\n", messages);
            if (result.Success)
            {
                MessageBox.Show(message, "Backup Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(message, "Backup",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Refresh the lists
            await RefreshBackupLists();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating backup");
            MessageBox.Show($"Backup failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadSettings()
    {
        var settings = _dataManager.Settings;

        // General
        ShiftLengthHours = settings.ShiftLengthHours;
        MinStudySeconds = settings.MinStudySeconds;
        AlwaysOnTop = settings.AlwaysOnTop;
        StartMinimized = settings.StartMinimized;
        AutoResumeOnStartup = settings.AutoResumeOnStartup;
        DarkMode = settings.DarkMode;
        ShowTime = settings.ShowTime;
        _originalFontSizeAdjustment = settings.GlobalFontSizeAdjustment;
        GlobalFontSizeAdjustment = settings.GlobalFontSizeAdjustment;
        Role = settings.Role;
        IgnoreDuplicateAccessions = settings.IgnoreDuplicateAccessions;
        MosaicToolsIntegrationEnabled = settings.MosaicToolsIntegrationEnabled;
        MosaicToolsTimeoutSeconds = settings.MosaicToolsTimeoutSeconds;
        AutoUpdateEnabled = settings.AutoUpdateEnabled;

        // Counter visibility
        ShowTotal = settings.ShowTotal;
        ShowAvg = settings.ShowAvg;
        ShowLastHour = settings.ShowLastHour;
        ShowLastFullHour = settings.ShowLastFullHour;
        ShowProjected = settings.ShowProjected;
        ShowProjectedShift = settings.ShowProjectedShift;
        ShowPaceCar = settings.ShowPaceCar;

        // Compensation visibility
        ShowCompTotal = settings.ShowCompTotal;
        ShowCompAvg = settings.ShowCompAvg;
        ShowCompLastHour = settings.ShowCompLastHour;
        ShowCompLastFullHour = settings.ShowCompLastFullHour;
        ShowCompProjected = settings.ShowCompProjected;
        ShowCompProjectedShift = settings.ShowCompProjectedShift;

        // Compensation rates
        BaseRvuRate = settings.BaseRvuRate;
        BonusRvuRate = settings.BonusRvuRate;

        // Backup
        CloudBackupEnabled = settings.CloudBackupEnabled;
        BackupSchedule = settings.BackupSchedule;
        AutoBackupOnShiftEnd = settings.AutoBackupOnShiftEnd;
        GitHubToken = settings.GitHubToken ?? "";
        BackupRepoName = settings.BackupRepoName ?? "";
        DropboxEnabled = settings.DropboxEnabled;
        DropboxAppKey = settings.DropboxAppKey ?? "";

        // Mini interface
        MiniMetric1 = settings.MiniMetric1;
        MiniMetric2 = settings.MiniMetric2;

        // Team Dashboard
        TeamDashboardEnabled = settings.TeamDashboardEnabled;
        TeamCode = settings.TeamCode ?? "";
        TeamStorageUrl = settings.TeamStorageUrl ?? "";
    }

    public void Save()
    {
        var settings = _dataManager.Settings;

        // General
        settings.ShiftLengthHours = ShiftLengthHours;
        settings.MinStudySeconds = MinStudySeconds;
        settings.AlwaysOnTop = AlwaysOnTop;
        settings.StartMinimized = StartMinimized;
        settings.AutoResumeOnStartup = AutoResumeOnStartup;
        settings.DarkMode = DarkMode;
        settings.ShowTime = ShowTime;
        settings.GlobalFontSizeAdjustment = GlobalFontSizeAdjustment;
        settings.Role = Role;
        settings.IgnoreDuplicateAccessions = IgnoreDuplicateAccessions;
        settings.MosaicToolsIntegrationEnabled = MosaicToolsIntegrationEnabled;
        settings.MosaicToolsTimeoutSeconds = MosaicToolsTimeoutSeconds;
        settings.AutoUpdateEnabled = AutoUpdateEnabled;

        // Counter visibility
        settings.ShowTotal = ShowTotal;
        settings.ShowAvg = ShowAvg;
        settings.ShowLastHour = ShowLastHour;
        settings.ShowLastFullHour = ShowLastFullHour;
        settings.ShowProjected = ShowProjected;
        settings.ShowProjectedShift = ShowProjectedShift;
        settings.ShowPaceCar = ShowPaceCar;

        // Compensation visibility
        settings.ShowCompTotal = ShowCompTotal;
        settings.ShowCompAvg = ShowCompAvg;
        settings.ShowCompLastHour = ShowCompLastHour;
        settings.ShowCompLastFullHour = ShowCompLastFullHour;
        settings.ShowCompProjected = ShowCompProjected;
        settings.ShowCompProjectedShift = ShowCompProjectedShift;

        // Compensation rates
        settings.BaseRvuRate = BaseRvuRate;
        settings.BonusRvuRate = BonusRvuRate;

        // Backup
        settings.CloudBackupEnabled = CloudBackupEnabled;
        settings.BackupSchedule = BackupSchedule;
        settings.AutoBackupOnShiftEnd = AutoBackupOnShiftEnd;
        settings.GitHubToken = GitHubToken;
        settings.BackupRepoName = BackupRepoName;
        settings.DropboxEnabled = DropboxEnabled;
        settings.DropboxAppKey = DropboxAppKey;

        // Mini interface
        settings.MiniMetric1 = MiniMetric1;
        settings.MiniMetric2 = MiniMetric2;

        // Team Dashboard
        settings.TeamDashboardEnabled = TeamDashboardEnabled;
        settings.TeamCode = TeamCode;
        settings.TeamStorageUrl = TeamStorageUrl;

        _dataManager.SaveSettings();
    }
}
