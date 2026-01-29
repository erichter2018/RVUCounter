using RVUCounter.Core;
using YamlDotNet.Serialization;

namespace RVUCounter.Models;

/// <summary>
/// User settings loaded from user_settings.yaml
/// </summary>
public class UserSettings
{
    // Shift settings
    public double ShiftLengthHours { get; set; } = 9;
    public int MinStudySeconds { get; set; } = 1;  // Python default: 1 second
    public bool AutoResumeOnStartup { get; set; } = true;

    // Window positions
    public WindowPosition? MainWindowPosition { get; set; }
    public WindowPosition? SettingsWindowPosition { get; set; }
    public WindowPosition? StatisticsWindowPosition { get; set; }
    public WindowPosition? ToolsWindowPosition { get; set; }

    // UI preferences
    public bool AlwaysOnTop { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public bool DarkMode { get; set; } = true;   // Python default: true (dark mode)
    public bool ShowTime { get; set; } = true;   // Python default: true (show time)
    public bool ShowInpatientStatPercentage { get; set; } = true;  // Show IP Stat % in stats panel

    // Global font size adjustment (-2.0 to +4.0 in 0.5 increments, 0 = default)
    public double GlobalFontSizeAdjustment { get; set; } = 0.0;

    // Theme settings
    public string ThemePreset { get; set; } = "default_dark";
    public Dictionary<string, string>? CustomThemeOverrides { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";
    public Dictionary<string, SavedTheme>? CustomThemes { get; set; }


    // Counter visibility toggles (Python parity)
    public bool ShowTotal { get; set; } = true;
    public bool ShowAvg { get; set; } = true;
    public bool ShowLastHour { get; set; } = true;
    public bool ShowLastFullHour { get; set; } = true;
    public bool ShowProjected { get; set; } = true;
    public bool ShowProjectedShift { get; set; } = true;
    public bool ShowPaceCar { get; set; } = true;  // Python default: true

    // Compensation visibility toggles (Python parity)
    public bool ShowCompTotal { get; set; } = false;
    public bool ShowCompAvg { get; set; } = false;
    public bool ShowCompLastHour { get; set; } = false;
    public bool ShowCompLastFullHour { get; set; } = false;
    public bool ShowCompProjected { get; set; } = false;
    public bool ShowCompProjectedShift { get; set; } = true;

    // Compensation rates
    public double BaseRvuRate { get; set; } = 0.0;
    public double BonusRvuRate { get; set; } = 0.0;
    public double MonthlyRvuTarget { get; set; } = 0.0;
    public double CurrentMonthRvu { get; set; } = 0.0;

    // Role (Partner/Associate)
    public string Role { get; set; } = "Partner";

    // Data handling
    public bool IgnoreDuplicateAccessions { get; set; } = true;

    // MosaicTools integration - wait for signed/unsigned confirmation
    public bool MosaicToolsIntegrationEnabled { get; set; } = true;
    public int MosaicToolsTimeoutSeconds { get; set; } = 5;

    // HIPAA compliance - always hash accessions
    public string? HipaaSalt { get; set; }

    // Update settings
    public bool AutoUpdateEnabled { get; set; } = true;
    public string? SkippedVersion { get; set; }
    public string? LastSeenVersion { get; set; }

    // Statistics preferences
    public string DefaultStatsPeriod { get; set; } = "current_shift";
    public string DefaultStatsView { get; set; } = "summary";
    public double StatisticsLeftPaneWidth { get; set; } = 280;
    public double StatisticsChartsPaneWidth { get; set; } = 320;

    // Backup settings
    public bool AutoBackupOnShiftEnd { get; set; } = false;
    public bool CloudBackupEnabled { get; set; } = false;
    public string BackupSchedule { get; set; } = "shift_end";
    public DateTime? LastBackup { get; set; }
    public string? OneDrivePath { get; set; }
    public string? GitHubToken { get; set; }
    public string? BackupRepoName { get; set; }
    public bool DropboxEnabled { get; set; } = false;
    public bool DropboxBackupEnabled { get; set; } = false;
    public string? DropboxAppKey { get; set; }
    public string? DropboxRefreshToken { get; set; }
    public int BackupRetentionCount { get; set; } = 30;

    // Mini interface settings
    public string MiniMetric1 { get; set; } = "pace";
    public string MiniMetric2 { get; set; } = "current_total";

    // Pace comparison mode
    public string PaceComparisonMode { get; set; } = "best_week";  // Python default: "best_week"

    // Pace goal settings
    public double PaceGoalRvuPerHour { get; set; } = 25.0;   // Python default: 25.0
    public double PaceGoalShiftHours { get; set; } = 9.0;
    public double PaceGoalTotalRvu { get; set; } = 225.0;    // Python default: 225.0

    // Payroll sync
    public double PayrollTimeOffsetHours { get; set; } = -1.0;  // Python default: -1.0

    // Projection settings (reset monthly)
    public int ProjectionDays { get; set; } = 14;
    public int ProjectionExtraDays { get; set; } = 1;   // Python default: 1
    public int ProjectionExtraHours { get; set; } = 2;  // Python default: 2
    public string? LastProjectionMonth { get; set; }

    // ===========================================
    // TEAM DASHBOARD (Privacy-First)
    // ===========================================

    /// <summary>
    /// Enable team dashboard (opt-in only)
    /// </summary>
    public bool TeamDashboardEnabled { get; set; } = false;

    /// <summary>
    /// Team code (6 characters)
    /// </summary>
    public string? TeamCode { get; set; }

    /// <summary>
    /// Anonymous ID for this user (random, regenerated on each enable)
    /// </summary>
    public string? TeamAnonymousId { get; set; }

    /// <summary>
    /// Storage URL for team data
    /// </summary>
    public string? TeamStorageUrl { get; set; }
}

/// <summary>
/// Represents a saved window position
/// </summary>
public class WindowPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
