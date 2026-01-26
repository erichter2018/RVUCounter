using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RVUCounter.Data;
using RVUCounter.Logic;
using RVUCounter.Models;
using Serilog;
using System.IO;

namespace RVUCounter.ViewModels;

/// <summary>
/// ViewModel for the Tools window.
/// Provides database utilities: manual entry, repair, export, Excel audit, payroll sync.
/// </summary>
public partial class ToolsViewModel : ObservableObject
{
    private readonly DataManager _dataManager;
    private readonly Action? _onDatabaseChanged;
    private readonly ExcelChecker _excelChecker;
    private readonly PayrollSyncManager _payrollSyncManager;
    private readonly BackupManager _backupManager;
    private readonly DatabaseRepair _databaseRepair;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _reportText = "";

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private int _progress;

    // Manual entry fields
    [ObservableProperty]
    private string _manualAccession = "";

    [ObservableProperty]
    private string _manualProcedure = "";

    [ObservableProperty]
    private string _manualStudyType = "";

    [ObservableProperty]
    private double _manualRvu;

    [ObservableProperty]
    private string _manualPatientClass = "";

    // Database stats
    [ObservableProperty]
    private int _totalShifts;

    [ObservableProperty]
    private int _totalStudies;

    [ObservableProperty]
    private double _totalRvu;

    // Excel audit fields
    [ObservableProperty]
    private string _excelFilePath = "";

    [ObservableProperty]
    private DateTime _auditStartDate = DateTime.Today.AddDays(-30);

    [ObservableProperty]
    private DateTime _auditEndDate = DateTime.Today;

    [ObservableProperty]
    private ExcelAuditResult? _lastAuditResult;

    // Payroll sync fields
    [ObservableProperty]
    private PayrollSyncResult? _lastSyncResult;

    [ObservableProperty]
    private int _selectedShiftId;

    // Backup status
    [ObservableProperty]
    private BackupStatus? _backupStatus;

    public ToolsViewModel(DataManager dataManager, Action? onDatabaseChanged = null)
    {
        _dataManager = dataManager;
        _onDatabaseChanged = onDatabaseChanged;
        _excelChecker = new ExcelChecker(dataManager);
        _payrollSyncManager = new PayrollSyncManager(dataManager);
        _databaseRepair = new DatabaseRepair(dataManager);

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RVUCounter", "database.db");
        _backupManager = new BackupManager(
            dbPath,
            () => dataManager.Settings,
            s => dataManager.SaveSettings(),
            () => dataManager.Database.Checkpoint());

        LoadDatabaseStats();
        LoadBackupStatus();
    }

    private void LoadDatabaseStats()
    {
        try
        {
            TotalShifts = _dataManager.Database.GetAllShifts().Count;
            var allRecords = _dataManager.Database.GetAllRecords();
            TotalStudies = allRecords.Count;
            TotalRvu = allRecords.Sum(r => r.Rvu);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error loading database stats");
        }
    }

    private void LoadBackupStatus()
    {
        try
        {
            BackupStatus = _backupManager.GetBackupStatus();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error loading backup status");
        }
    }

    #region Manual Entry

    [RelayCommand]
    private void AddManualStudy()
    {
        if (string.IsNullOrWhiteSpace(ManualStudyType))
        {
            StatusText = "Error: Study Type is required";
            return;
        }

        var shift = _dataManager.Database.GetCurrentShift();
        if (shift == null)
        {
            StatusText = "Error: No active shift";
            return;
        }

        var accession = string.IsNullOrWhiteSpace(ManualAccession)
            ? $"MANUAL-{DateTime.Now:HHmmss}"
            : ManualAccession;

        var record = new StudyRecord
        {
            Accession = _dataManager.HashAccession(accession),
            Procedure = ManualProcedure,
            StudyType = ManualStudyType,
            Rvu = ManualRvu > 0 ? ManualRvu : LookupRvu(ManualStudyType),
            Timestamp = DateTime.Now,
            PatientClass = ManualPatientClass,
            Source = "Manual"
        };

        _dataManager.Database.AddRecord(shift.Id, record);
        LoadDatabaseStats();
        _onDatabaseChanged?.Invoke();

        // Clear form
        ManualAccession = "";
        ManualProcedure = "";
        ManualStudyType = "";
        ManualRvu = 0;
        ManualPatientClass = "";

        StatusText = $"Added: {record.StudyType} ({record.Rvu:F2} RVU)";
        Log.Information("Manual study added: {StudyType}", record.StudyType);
    }

    private double LookupRvu(string studyType)
    {
        if (_dataManager.RvuTable.TryGetValue(studyType, out var rvu))
            return rvu;

        foreach (var kvp in _dataManager.RvuTable)
        {
            if (kvp.Key.Contains(studyType, StringComparison.OrdinalIgnoreCase) ||
                studyType.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        return 1.0;
    }

    #endregion

    #region Database Operations

    [RelayCommand]
    private async Task ScanDatabase()
    {
        IsProcessing = true;
        StatusText = "Scanning database...";
        ReportText = "";

        await Task.Run(() =>
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== DATABASE SCAN REPORT ===");
                sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();

                var shifts = _dataManager.Database.GetAllShifts();
                sb.AppendLine($"Total Shifts: {shifts.Count}");

                var allRecords = _dataManager.Database.GetAllRecords();
                sb.AppendLine($"Total Studies: {allRecords.Count}");
                sb.AppendLine($"Total RVU: {allRecords.Sum(r => r.Rvu):F1}");
                sb.AppendLine();

                // Stats by study type
                var statsByType = _dataManager.Database.GetStatsByStudyType();
                sb.AppendLine("BREAKDOWN BY STUDY TYPE:");
                foreach (var kvp in statsByType.Take(15))
                {
                    sb.AppendLine($"  {kvp.Key}: {kvp.Value.Count} studies, {kvp.Value.TotalRvu:F1} RVU");
                }
                if (statsByType.Count > 15)
                {
                    sb.AppendLine($"  ... and {statsByType.Count - 15} more types");
                }
                sb.AppendLine();

                // Check for issues
                var orphans = allRecords.Where(r => r.ShiftId == 0).ToList();
                if (orphans.Any())
                {
                    sb.AppendLine($"WARNING: Orphan records (no shift): {orphans.Count}");
                }

                var duplicates = allRecords.GroupBy(r => r.Accession)
                    .Where(g => g.Count() > 1)
                    .ToList();
                if (duplicates.Any())
                {
                    sb.AppendLine($"WARNING: Duplicate accessions: {duplicates.Count}");
                }

                var zeroRvu = allRecords.Where(r => r.Rvu == 0).ToList();
                if (zeroRvu.Any())
                {
                    sb.AppendLine($"INFO: Zero-RVU studies: {zeroRvu.Count}");
                }

                var unknown = allRecords.Where(r => r.StudyType.Contains("Unknown")).ToList();
                if (unknown.Any())
                {
                    sb.AppendLine($"INFO: Unknown study types: {unknown.Count}");
                }

                sb.AppendLine();
                sb.AppendLine("Scan complete.");

                ReportText = sb.ToString();
                StatusText = "Scan complete";
            }
            catch (Exception ex)
            {
                ReportText = $"Error: {ex.Message}";
                StatusText = "Scan failed";
                Log.Error(ex, "Database scan failed");
            }
        });

        IsProcessing = false;
    }

    [RelayCommand]
    private async Task FixDatabase()
    {
        IsProcessing = true;
        StatusText = "Fixing database...";
        var report = new System.Text.StringBuilder();
        report.AppendLine("=== DATABASE FIX REPORT ===");

        await Task.Run(() =>
        {
            try
            {
                var allRecords = _dataManager.Database.GetAllRecords();
                int fixedRvuCount = 0;
                int fixedTypeCount = 0;

                // 1. Fix Zero RVUs
                foreach (var record in allRecords.Where(r => r.Rvu == 0))
                {
                    if (_dataManager.RvuTable.TryGetValue(record.StudyType, out var rvu) && rvu > 0)
                    {
                        record.Rvu = rvu;
                        _dataManager.Database.UpdateRecord(record);
                        fixedRvuCount++;
                    }
                }

                if (fixedRvuCount > 0)
                    report.AppendLine($"Fixed {fixedRvuCount} studies with 0 RVU.");
                else
                    report.AppendLine("No Zero-RVU studies could be fixed.");

                // 2. Re-classify Unknown study types
                foreach (var record in allRecords.Where(r => r.StudyType == "Unknown" && !string.IsNullOrEmpty(r.Procedure)))
                {
                    var (studyType, rvu) = StudyMatcher.MatchStudyType(
                        record.Procedure,
                        _dataManager.RvuTable,
                        _dataManager.ClassificationRules);

                    if (studyType != "Unknown")
                    {
                        record.StudyType = studyType;
                        record.Rvu = rvu;
                        _dataManager.Database.UpdateRecord(record);
                        fixedTypeCount++;
                    }
                }

                if (fixedTypeCount > 0)
                    report.AppendLine($"Re-classified {fixedTypeCount} Unknown studies.");

                report.AppendLine("Fix complete.");
                ReportText = report.ToString();
                LoadDatabaseStats();
                _onDatabaseChanged?.Invoke();
                StatusText = "Fix complete";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fix database failed");
                ReportText = $"Error fixing database: {ex.Message}";
                StatusText = "Fix failed";
            }
        });

        IsProcessing = false;
    }

    /// <summary>
    /// Full database repair using DatabaseRepair class (Python parity).
    /// Finds all records where study_type or RVU don't match current rules and fixes them.
    /// </summary>
    [RelayCommand]
    private async Task RepairDatabase()
    {
        IsProcessing = true;
        Progress = 0;
        StatusText = "Scanning for mismatches...";

        var progressReporter = new Progress<(string Status, double Percent)>(p =>
        {
            StatusText = p.Status;
            Progress = (int)(p.Percent * 100);
        });

        var result = await _databaseRepair.RepairDatabaseAsync(progressReporter);

        var report = new System.Text.StringBuilder();
        report.AppendLine("=== DATABASE REPAIR REPORT ===");
        report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        if (result.Success)
        {
            report.AppendLine($"Mismatches Found: {result.MismatchesFound}");
            report.AppendLine($"Records Fixed: {result.RecordsFixed}");
            report.AppendLine();

            if (result.MismatchesFound == 0)
            {
                report.AppendLine("All records match current RVU rules.");
            }
            else
            {
                report.AppendLine("All mismatches have been corrected to match current rules.");
            }

            LoadDatabaseStats();
            _onDatabaseChanged?.Invoke();
            StatusText = $"Repair complete: {result.RecordsFixed} records fixed";
        }
        else
        {
            report.AppendLine($"ERROR: {result.ErrorMessage}");
            StatusText = "Repair failed";
        }

        ReportText = report.ToString();
        IsProcessing = false;
    }

    #endregion

    #region Excel Audit

    [RelayCommand]
    private void BrowseExcelFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel Files|*.xlsx;*.xls|All Files|*.*",
            Title = "Select Payroll Excel File"
        };

        if (dialog.ShowDialog() == true)
        {
            ExcelFilePath = dialog.FileName;
            StatusText = $"Selected: {Path.GetFileName(dialog.FileName)}";
        }
    }

    [RelayCommand]
    private async Task RunExcelAudit()
    {
        if (string.IsNullOrEmpty(ExcelFilePath) || !File.Exists(ExcelFilePath))
        {
            StatusText = "Please select an Excel file first";
            return;
        }

        IsProcessing = true;
        Progress = 0;
        StatusText = "Running Excel audit...";

        var progressReporter = new Progress<int>(p => Progress = p);

        LastAuditResult = await _excelChecker.AuditExcelFileAsync(
            ExcelFilePath,
            AuditStartDate,
            AuditEndDate,
            progressReporter);

        if (LastAuditResult.Success)
        {
            ReportText = _excelChecker.GenerateReportText(LastAuditResult);
            StatusText = $"Audit complete: {LastAuditResult.Matched} matched, {LastAuditResult.MissingFromDb} missing";
        }
        else
        {
            ReportText = $"Audit failed: {LastAuditResult.ErrorMessage}";
            StatusText = "Audit failed";
        }

        IsProcessing = false;
    }

    /// <summary>
    /// Check Excel file for RVU outliers (Python parity: check_file).
    /// Compares StandardProcedureName/wRVU_Matrix against current rules.
    /// </summary>
    [RelayCommand]
    private async Task CheckExcelRvu()
    {
        if (string.IsNullOrEmpty(ExcelFilePath) || !File.Exists(ExcelFilePath))
        {
            StatusText = "Please select an Excel file first";
            return;
        }

        IsProcessing = true;
        Progress = 0;
        StatusText = "Checking RVU values...";

        var progressReporter = new Progress<(int Current, int Total)>(p =>
        {
            Progress = p.Total > 0 ? (int)(100.0 * p.Current / p.Total) : 0;
        });

        var result = await Task.Run(() => _excelChecker.CheckFile(ExcelFilePath, progressReporter));

        if (result.Success)
        {
            ReportText = _excelChecker.GenerateReportText(result);
            StatusText = $"Check complete: {result.Outliers.Count} outliers found in {result.TotalProcessed} records";
        }
        else
        {
            ReportText = $"Check failed: {result.ErrorMessage}";
            StatusText = "Check failed";
        }

        IsProcessing = false;
    }

    #endregion

    #region Payroll Sync

    [RelayCommand]
    private void DetectTimeOffset()
    {
        if (string.IsNullOrEmpty(ExcelFilePath) || !File.Exists(ExcelFilePath))
        {
            StatusText = "Please select an Excel file first";
            return;
        }

        IsProcessing = true;
        StatusText = "Detecting time offset...";

        LastSyncResult = _payrollSyncManager.DetectTimeOffset(
            ExcelFilePath,
            AuditStartDate,
            AuditEndDate);

        if (LastSyncResult.Success)
        {
            ReportText = _payrollSyncManager.GenerateReport(LastSyncResult);
            StatusText = $"Detected offset: {LastSyncResult.OffsetDisplay}";
        }
        else
        {
            ReportText = $"Detection failed: {LastSyncResult.ErrorMessage}";
            StatusText = "Detection failed";
        }

        IsProcessing = false;
    }

    [RelayCommand]
    private async Task ApplyTimeOffset()
    {
        if (LastSyncResult == null || !LastSyncResult.Success)
        {
            StatusText = "Please detect time offset first";
            return;
        }

        var shift = _dataManager.Database.GetCurrentShift();
        if (shift == null)
        {
            StatusText = "No active shift to update";
            return;
        }

        IsProcessing = true;
        StatusText = "Applying time offset...";

        var (success, count, message) = await _payrollSyncManager.ApplyOffsetAsync(
            shift.Id,
            LastSyncResult.Offset);

        StatusText = message;
        if (success)
        {
            _onDatabaseChanged?.Invoke();
        }

        IsProcessing = false;
    }

    #endregion

    #region Backup

    [RelayCommand]
    private async Task CreateBackup()
    {
        IsProcessing = true;
        StatusText = "Creating backup...";

        var result = await _backupManager.CreateBackupAsync("manual");

        if (result.Success)
        {
            StatusText = $"Backup created: {Path.GetFileName(result.BackupPath ?? "")}";
            LoadBackupStatus();
        }
        else
        {
            StatusText = $"Backup failed: {result.Message}";
        }

        IsProcessing = false;
    }

    [RelayCommand]
    private void ViewBackups()
    {
        var backupFolder = _backupManager.GetOneDriveBackupFolder();
        if (!string.IsNullOrEmpty(backupFolder) && Directory.Exists(backupFolder))
        {
            System.Diagnostics.Process.Start("explorer.exe", backupFolder);
        }
        else
        {
            StatusText = "Backup folder not found";
        }
    }

    [RelayCommand]
    private async Task RestoreBackup()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Database Files|*.db|All Files|*.*",
            Title = "Select Backup to Restore",
            InitialDirectory = _backupManager.GetOneDriveBackupFolder() ?? ""
        };

        if (dialog.ShowDialog() == true)
        {
            IsProcessing = true;
            StatusText = "Restoring backup...";

            var (success, message) = await _backupManager.RestoreFromBackupAsync(dialog.FileName);

            StatusText = message;
            if (success)
            {
                LoadDatabaseStats();
                _onDatabaseChanged?.Invoke();
            }

            IsProcessing = false;
        }
    }

    #endregion

    #region Utilities

    [RelayCommand]
    private void ViewLogs()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (Directory.Exists(logDir))
        {
            System.Diagnostics.Process.Start("explorer.exe", logDir);
        }
        else
        {
            StatusText = "Log directory not found";
        }
    }

    [RelayCommand]
    private void OpenDatabase()
    {
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RVUCounter", "database.db");
        if (File.Exists(dbPath))
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (dir != null)
            {
                System.Diagnostics.Process.Start("explorer.exe", dir);
            }
        }
        else
        {
            StatusText = "Database file not found";
        }
    }

    /// <summary>
    /// Get list of common study types for autocomplete.
    /// </summary>
    public List<string> GetStudyTypes()
    {
        return _dataManager.RvuTable.Keys.OrderBy(k => k).ToList();
    }

    /// <summary>
    /// Get list of shifts for dropdown.
    /// </summary>
    public List<Shift> GetShifts()
    {
        var shifts = new List<Shift>();

        var current = _dataManager.Database.GetCurrentShift();
        if (current != null)
            shifts.Add(current);

        shifts.AddRange(_dataManager.Database.GetAllShifts().Take(20));
        return shifts;
    }

    /// <summary>
    /// Get backup history.
    /// </summary>
    public List<BackupInfo> GetBackupHistory()
    {
        return _backupManager.GetBackupHistory();
    }

    #endregion
}
