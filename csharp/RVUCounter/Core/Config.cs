using System.IO;

namespace RVUCounter.Core;

/// <summary>
/// Application configuration constants and feature flags.
/// Ported from Python config.py
/// </summary>
public static class Config
{
    // Version
    public const string AppVersion = "3.1.8";
    public const string AppVersionDate = "02/13/2026";
    public const string AppName = "RVU Counter";

    // Folder structure
    public const string SettingsFolder = "settings";
    public const string DataFolder = "data";
    public const string LogsFolder = "logs";
    public const string HelpersFolder = "helpers";
    public const string ResourcesFolder = "resources";

    // File names
    public const string UserSettingsFileName = "user_settings.yaml";
    public const string RulesFileName = "rvu_rules.yaml";
    public const string DatabaseFileName = "rvu_records.db";

    // Logging configuration
    public const int LogMaxBytes = 10 * 1024 * 1024; // 10MB
    public const int LogCheckInterval = 100;
    public const double LogTrimTargetRatio = 0.9;

    // Default window sizes
    public static readonly (int Width, int Height) MainWindowSize = (240, 500);
    public static readonly (int Width, int Height) SettingsWindowSize = (450, 700);
    public static readonly (int Width, int Height) StatisticsWindowSize = (1350, 800);

    // Shift configuration
    public const int DefaultShiftLengthHours = 9;
    public const int DefaultMinStudySeconds = 10;

    // GitHub configuration
    public const string GitHubOwner = "erichter2018";
    public const string GitHubRepo = "RVUCounter";
    public const string GitHubBackupRepo = "rvu-counter-backups";
    public const string BackupBranch = "main";

    // Database configuration
    public const int DatabaseBusyTimeoutMs = 5000;

    // UI Automation configuration
    public const int UiAutomationTimeoutMs = 5000;
    public const int UiAutomationMaxElements = 3000;
    public const int ClarioMaxElements = 1000;
    public const int ClarioExtractionTimeoutMs = 10000;
    public const int MosaicScanMaxElements = 2000;

    // UIA3 recovery
    public const int UiaConsecutiveFailuresBeforeRecovery = 10;

    // HIPAA Migration
    public const int HipaaBatchSize = 5000;

    // TBWU database
    public const string TbwuDatabaseFileName = "tbwu_rules.db";

    // Full paths (computed at runtime)
    public static string GetSettingsPath(string baseDir) => Path.Combine(baseDir, SettingsFolder);
    public static string GetDataPath(string baseDir) => Path.Combine(baseDir, DataFolder);
    public static string GetLogsPath(string baseDir) => Path.Combine(baseDir, LogsFolder);
    public static string GetResourcesPath(string baseDir) => Path.Combine(baseDir, ResourcesFolder);
    public static string GetUserSettingsFile(string baseDir) => Path.Combine(GetSettingsPath(baseDir), UserSettingsFileName);
    public static string GetRulesFile(string baseDir) => Path.Combine(GetResourcesPath(baseDir), RulesFileName);
    public static string GetDatabaseFile(string baseDir) => Path.Combine(GetDataPath(baseDir), DatabaseFileName);
    public static string GetTbwuDatabaseFile(string baseDir) => Path.Combine(GetResourcesPath(baseDir), TbwuDatabaseFileName);
}
