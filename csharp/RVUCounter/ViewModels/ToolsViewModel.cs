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
/// Provides: Database Repair, Payroll Reconciliation, Backup & Restore.
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

    // Database stats
    [ObservableProperty]
    private int _totalShifts;

    [ObservableProperty]
    private int _totalStudies;

    [ObservableProperty]
    private double _totalRvu;

    // Payroll Reconciliation - single Excel file for all operations
    [ObservableProperty]
    private string _excelFilePath = "";

    [ObservableProperty]
    private DateTime _auditStartDate = DateTime.Today.AddDays(-30);

    [ObservableProperty]
    private DateTime _auditEndDate = DateTime.Today;

    [ObservableProperty]
    private ExcelAuditResult? _lastAuditResult;

    [ObservableProperty]
    private PayrollSyncResult? _lastSyncResult;

    // Computed property for whether reconcile is available
    public bool CanReconcile => LastAuditResult?.Success == true &&
        (LastAuditResult.MissingFromDb > 0 || LastAuditResult.ExtraInDb > 0);

    // Computed property for whether time sync is available
    public bool CanApplyTimeSync => LastSyncResult?.Success == true;

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

    partial void OnLastAuditResultChanged(ExcelAuditResult? value)
    {
        OnPropertyChanged(nameof(CanReconcile));
    }

    partial void OnLastSyncResultChanged(PayrollSyncResult? value)
    {
        OnPropertyChanged(nameof(CanApplyTimeSync));
    }

    #region Database Repair

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
                var allRecords = _dataManager.Database.GetAllRecords();

                // Check for issues FIRST
                var orphans = allRecords.Where(r => r.ShiftId == 0).ToList();
                var duplicates = allRecords.GroupBy(r => r.Accession)
                    .Where(g => g.Count() > 1)
                    .ToList();
                var zeroRvu = allRecords.Where(r => r.Rvu == 0).ToList();
                var unknown = allRecords.Where(r => r.StudyType.Contains("Unknown")).ToList();

                var totalIssues = orphans.Count + duplicates.Sum(g => g.Count() - 1) + zeroRvu.Count + unknown.Count;

                sb.AppendLine("─────────────────────────────────");
                sb.AppendLine("ISSUES FOUND");
                sb.AppendLine("─────────────────────────────────");

                if (totalIssues == 0)
                {
                    sb.AppendLine("  No issues found!");
                }
                else
                {
                    if (duplicates.Any())
                    {
                        var dupCount = duplicates.Sum(g => g.Count() - 1);
                        sb.AppendLine($"  Duplicate accessions: {duplicates.Count} unique ({dupCount} extra records)");
                        sb.AppendLine();

                        // List all duplicate records with full details
                        foreach (var group in duplicates.Take(20))
                        {
                            sb.AppendLine($"    Accession: {group.Key.Substring(0, Math.Min(16, group.Key.Length))}...");
                            foreach (var record in group.OrderBy(r => r.Timestamp))
                            {
                                var ts = record.Timestamp.ToString("yyyy-MM-dd HH:mm");
                                var proc = record.Procedure?.Length > 30
                                    ? record.Procedure.Substring(0, 30) + "..."
                                    : record.Procedure ?? "N/A";
                                sb.AppendLine($"      ID:{record.Id} | {ts} | {record.Rvu:F1} RVU | Shift:{record.ShiftId} | {proc}");
                            }
                            sb.AppendLine();
                        }
                        if (duplicates.Count > 20)
                        {
                            sb.AppendLine($"    ... and {duplicates.Count - 20} more duplicate groups");
                            sb.AppendLine();
                        }
                    }
                    if (orphans.Any())
                    {
                        sb.AppendLine($"  Orphan records (no shift): {orphans.Count}");
                    }
                    if (zeroRvu.Any())
                    {
                        sb.AppendLine($"  Zero-RVU studies: {zeroRvu.Count}");
                    }
                    if (unknown.Any())
                    {
                        sb.AppendLine($"  Unknown study types: {unknown.Count}");
                    }
                    sb.AppendLine();
                    sb.AppendLine("  Click 'Fix' to resolve these issues.");
                }
                sb.AppendLine();

                // TOTALS
                sb.AppendLine("─────────────────────────────────");
                sb.AppendLine("TOTALS");
                sb.AppendLine("─────────────────────────────────");
                sb.AppendLine($"  Shifts: {shifts.Count}");
                sb.AppendLine($"  Studies: {allRecords.Count}");
                sb.AppendLine($"  Total RVU: {allRecords.Sum(r => r.Rvu):F1}");

                ReportText = sb.ToString();
                StatusText = totalIssues > 0 ? $"Found {totalIssues} issues" : "No issues found";
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
        report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        await Task.Run(() =>
        {
            try
            {
                var allRecords = _dataManager.Database.GetAllRecords();
                int fixedRvuCount = 0;
                int fixedTypeCount = 0;
                int duplicatesRemoved = 0;
                int orphansFixed = 0;

                // 1. Remove duplicate accessions (keep the oldest by timestamp)
                var duplicateGroups = allRecords
                    .GroupBy(r => r.Accession)
                    .Where(g => g.Count() > 1)
                    .ToList();

                foreach (var group in duplicateGroups)
                {
                    // Keep the record with the earliest timestamp, delete others
                    var toKeep = group.OrderBy(r => r.Timestamp).First();
                    foreach (var record in group.Where(r => r.Id != toKeep.Id))
                    {
                        _dataManager.Database.DeleteRecord(record.Id);
                        duplicatesRemoved++;
                    }
                }

                if (duplicatesRemoved > 0)
                {
                    report.AppendLine($"DUPLICATES: Removed {duplicatesRemoved} duplicate records");
                    report.AppendLine($"  (kept oldest record for each accession)");
                    report.AppendLine();
                    // Refresh records after deleting duplicates
                    allRecords = _dataManager.Database.GetAllRecords();
                }

                // 2. Fix orphan records (shift_id = 0 or invalid)
                var shifts = _dataManager.Database.GetAllShifts();
                var orphans = allRecords.Where(r => r.ShiftId == 0).ToList();

                foreach (var orphan in orphans)
                {
                    // Find the best shift for this record based on timestamp
                    var bestShift = shifts
                        .Where(s => orphan.Timestamp >= s.ShiftStart &&
                                   (s.ShiftEnd == null || orphan.Timestamp <= s.ShiftEnd.Value.AddHours(2)))
                        .OrderBy(s => Math.Abs((orphan.Timestamp - s.ShiftStart).TotalMinutes))
                        .FirstOrDefault();

                    if (bestShift != null)
                    {
                        // Update the record's shift_id
                        _dataManager.Database.AssignRecordToShift(orphan.Id, bestShift.Id);
                        orphansFixed++;
                    }
                    else
                    {
                        // No suitable shift found - delete the orphan
                        _dataManager.Database.DeleteRecord(orphan.Id);
                        orphansFixed++;
                    }
                }

                if (orphansFixed > 0)
                {
                    report.AppendLine($"ORPHANS: Fixed {orphansFixed} orphan records");
                    report.AppendLine($"  (assigned to matching shifts or deleted)");
                    report.AppendLine();
                    // Refresh records
                    allRecords = _dataManager.Database.GetAllRecords();
                }

                // 3. Fix Zero RVUs
                var zeroRvuRecords = allRecords.Where(r => r.Rvu == 0).ToList();
                foreach (var record in zeroRvuRecords)
                {
                    if (_dataManager.RvuTable.TryGetValue(record.StudyType, out var rvu) && rvu > 0)
                    {
                        record.Rvu = rvu;
                        _dataManager.Database.UpdateRecord(record);
                        fixedRvuCount++;
                    }
                }

                if (fixedRvuCount > 0)
                {
                    report.AppendLine($"ZERO RVU: Fixed {fixedRvuCount} records with 0 RVU");
                    report.AppendLine($"  (looked up RVU from study type)");
                    report.AppendLine();
                }

                // 4. Re-classify Unknown study types
                var unknownRecords = allRecords.Where(r => r.StudyType == "Unknown" && !string.IsNullOrEmpty(r.Procedure)).ToList();
                foreach (var record in unknownRecords)
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
                {
                    report.AppendLine($"UNKNOWN TYPES: Re-classified {fixedTypeCount} records");
                    report.AppendLine($"  (matched procedure text to study types)");
                    report.AppendLine();
                }

                // Summary
                report.AppendLine("─────────────────────────────────");
                var totalFixed = duplicatesRemoved + orphansFixed + fixedRvuCount + fixedTypeCount;
                if (totalFixed == 0)
                {
                    report.AppendLine("No issues found to fix.");
                }
                else
                {
                    report.AppendLine($"TOTAL: {totalFixed} issues fixed");
                }

                ReportText = report.ToString();
                LoadDatabaseStats();
                _onDatabaseChanged?.Invoke();
                StatusText = totalFixed > 0 ? $"Fixed {totalFixed} issues" : "No issues to fix";
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
    private void OpenDatabaseFolder()
    {
        try
        {
            var dbPath = _dataManager.DatabasePath;
            if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
            {
                var dir = Path.GetDirectoryName(dbPath);
                if (dir != null && Directory.Exists(dir))
                {
                    // Use ProcessStartInfo with UseShellExecute for proper Explorer opening
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{dir}\"",
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                    StatusText = $"Opened: {dir}";
                    return;
                }
            }
            StatusText = $"Database folder not found. Path: {dbPath ?? "unknown"}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error opening folder: {ex.Message}";
            Log.Error(ex, "Error opening database folder");
        }
    }

    #endregion

    #region Payroll Reconciliation

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
            // Clear previous results when new file selected
            LastAuditResult = null;
            LastSyncResult = null;
            ReportText = "";

            // Try to auto-detect date range from filename
            var (startDate, endDate) = ParseDateRangeFromFilename(dialog.FileName);
            if (startDate.HasValue && endDate.HasValue)
            {
                AuditStartDate = startDate.Value;
                AuditEndDate = endDate.Value;
                StatusText = $"Selected: {Path.GetFileName(dialog.FileName)} ({startDate.Value:MMM yyyy})";
            }
            else
            {
                StatusText = $"Selected: {Path.GetFileName(dialog.FileName)}";
            }
        }
    }

    /// <summary>
    /// Parse date range from filename. Supports formats like:
    /// - "2026-01 Payroll.xlsx" (year-month)
    /// - "Jan-26 Report.xlsx" (month-year)
    /// - "January 2026.xlsx" (full month name)
    /// </summary>
    private (DateTime? Start, DateTime? End) ParseDateRangeFromFilename(string filePath)
    {
        var filename = Path.GetFileNameWithoutExtension(filePath);

        // Month name mapping
        var monthMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            {"jan", 1}, {"january", 1}, {"feb", 2}, {"february", 2},
            {"mar", 3}, {"march", 3}, {"apr", 4}, {"april", 4},
            {"may", 5}, {"jun", 6}, {"june", 6}, {"jul", 7}, {"july", 7},
            {"aug", 8}, {"august", 8}, {"sep", 9}, {"sept", 9}, {"september", 9},
            {"oct", 10}, {"october", 10}, {"nov", 11}, {"november", 11},
            {"dec", 12}, {"december", 12}
        };

        // Try pattern: "2026-01" or "2026-1" at start
        var yearMonthMatch = System.Text.RegularExpressions.Regex.Match(
            filename, @"^(\d{4})-(\d{1,2})");
        if (yearMonthMatch.Success)
        {
            var year = int.Parse(yearMonthMatch.Groups[1].Value);
            var month = int.Parse(yearMonthMatch.Groups[2].Value);
            if (month >= 1 && month <= 12)
            {
                var start = new DateTime(year, month, 1);
                var end = start.AddMonths(1).AddDays(-1);
                return (start, end);
            }
        }

        // Try pattern: "Jan-26" or "Jan-2026"
        var monthYearMatch = System.Text.RegularExpressions.Regex.Match(
            filename, @"^([A-Za-z]+)-(\d{2,4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (monthYearMatch.Success)
        {
            var monthStr = monthYearMatch.Groups[1].Value;
            var yearStr = monthYearMatch.Groups[2].Value;
            if (monthMap.TryGetValue(monthStr, out var month))
            {
                var year = int.Parse(yearStr);
                if (year < 100) year += 2000;
                var start = new DateTime(year, month, 1);
                var end = start.AddMonths(1).AddDays(-1);
                return (start, end);
            }
        }

        // Try pattern: "January 2026" anywhere in filename
        foreach (var (monthName, monthNum) in monthMap)
        {
            var pattern = $@"{monthName}\s*(\d{{4}})";
            var match = System.Text.RegularExpressions.Regex.Match(
                filename, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var year = int.Parse(match.Groups[1].Value);
                var start = new DateTime(year, monthNum, 1);
                var end = start.AddMonths(1).AddDays(-1);
                return (start, end);
            }
        }

        return (null, null);
    }

    [RelayCommand]
    private async Task RunAudit()
    {
        if (string.IsNullOrEmpty(ExcelFilePath) || !File.Exists(ExcelFilePath))
        {
            StatusText = "Please select an Excel file first";
            return;
        }

        IsProcessing = true;
        Progress = 0;
        StatusText = "Auditing accessions...";

        var progressReporter = new Progress<int>(p => Progress = p);

        LastAuditResult = await _excelChecker.AuditExcelFileAsync(
            ExcelFilePath,
            AuditStartDate,
            AuditEndDate,
            progressReporter);

        if (LastAuditResult.Success)
        {
            ReportText = _excelChecker.GenerateReportText(LastAuditResult);
            StatusText = $"Audit: {LastAuditResult.Matched} matched, {LastAuditResult.MissingFromDb} missing, {LastAuditResult.ExtraInDb} extra";
        }
        else
        {
            ReportText = $"Audit failed: {LastAuditResult.ErrorMessage}";
            StatusText = "Audit failed";
        }

        IsProcessing = false;
    }

    [RelayCommand]
    private async Task CheckRvu()
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
            StatusText = $"RVU Check: {result.Outliers.Count} outliers in {result.TotalProcessed} records";
        }
        else
        {
            ReportText = $"Check failed: {result.ErrorMessage}";
            StatusText = "Check failed";
        }

        IsProcessing = false;
    }

    [RelayCommand]
    private async Task ReconcileDatabase()
    {
        if (LastAuditResult == null || !LastAuditResult.Success)
        {
            StatusText = "Please run audit first";
            return;
        }

        if (!CanReconcile)
        {
            StatusText = "Nothing to reconcile - no missing or extra records";
            return;
        }

        IsProcessing = true;
        Progress = 0;
        StatusText = "Reconciling database...";

        var progressReporter = new Progress<(string Status, int Percent)>(p =>
        {
            StatusText = p.Status;
            Progress = p.Percent;
        });

        var result = await _excelChecker.ReconcileDatabaseAsync(LastAuditResult, progressReporter);

        if (result.Success)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== RECONCILIATION COMPLETE ===");
            sb.AppendLine($"Records Deleted: {result.RecordsDeleted}");
            sb.AppendLine($"Records Added: {result.RecordsAdded}");
            sb.AppendLine($"Shifts Created: {result.ShiftsCreated}");
            sb.AppendLine();
            sb.AppendLine("Run audit again to verify changes.");

            ReportText = sb.ToString();
            StatusText = result.Message;
            LoadDatabaseStats();
            _onDatabaseChanged?.Invoke();

            // Clear audit result to force re-run
            LastAuditResult = null;
        }
        else
        {
            ReportText = $"Reconciliation failed: {result.ErrorMessage}";
            StatusText = "Reconciliation failed";
        }

        IsProcessing = false;
    }

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

    [RelayCommand]
    private void ExportReport()
    {
        if (string.IsNullOrEmpty(ReportText))
        {
            StatusText = "No report to export";
            return;
        }

        var defaultName = !string.IsNullOrEmpty(ExcelFilePath)
            ? Path.GetFileNameWithoutExtension(ExcelFilePath) + "_report.txt"
            : $"rvu_report_{DateTime.Now:yyyyMMdd}.txt";

        var dialog = new SaveFileDialog
        {
            Filter = "Text Files|*.txt|All Files|*.*",
            FileName = defaultName,
            Title = "Export Report"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.WriteAllText(dialog.FileName, ReportText);
                StatusText = $"Report exported to {Path.GetFileName(dialog.FileName)}";
            }
            catch (Exception ex)
            {
                StatusText = $"Export failed: {ex.Message}";
            }
        }
    }

    #endregion

    #region Backup & Restore

    [RelayCommand]
    private async Task ExportDatabase()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "SQLite Database|*.db|All Files|*.*",
            FileName = $"rvu_counter_backup_{DateTime.Now:yyyyMMdd}.db",
            Title = "Export Database"
        };

        if (dialog.ShowDialog() == true)
        {
            IsProcessing = true;
            StatusText = "Exporting database...";

            try
            {
                var sourcePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RVUCounter", "database.db");

                if (File.Exists(sourcePath))
                {
                    await Task.Run(() => File.Copy(sourcePath, dialog.FileName, overwrite: true));
                    StatusText = $"Database exported to {Path.GetFileName(dialog.FileName)}";
                }
                else
                {
                    StatusText = "Database file not found";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Export failed: {ex.Message}";
            }

            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task ExportSettings()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "YAML File|*.yaml|All Files|*.*",
            FileName = $"rvu_counter_settings_{DateTime.Now:yyyyMMdd}.yaml",
            Title = "Export Settings"
        };

        if (dialog.ShowDialog() == true)
        {
            IsProcessing = true;
            StatusText = "Exporting settings...";

            try
            {
                var sourcePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RVUCounter", "user_settings.yaml");

                if (File.Exists(sourcePath))
                {
                    await Task.Run(() => File.Copy(sourcePath, dialog.FileName, overwrite: true));
                    StatusText = $"Settings exported to {Path.GetFileName(dialog.FileName)}";
                }
                else
                {
                    StatusText = "Settings file not found";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Export failed: {ex.Message}";
            }

            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task ImportDatabase()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SQLite Database|*.db|All Files|*.*",
            Title = "Import Database"
        };

        if (dialog.ShowDialog() == true)
        {
            IsProcessing = true;
            StatusText = "Importing database...";

            try
            {
                var destPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RVUCounter", "database.db");

                // Create backup of current
                if (File.Exists(destPath))
                {
                    var backupPath = destPath + $".backup_{DateTime.Now:yyyyMMddHHmmss}";
                    await Task.Run(() => File.Move(destPath, backupPath));
                }

                await Task.Run(() => File.Copy(dialog.FileName, destPath));
                StatusText = "Database imported. Please restart the application.";
                LoadDatabaseStats();
                _onDatabaseChanged?.Invoke();
            }
            catch (Exception ex)
            {
                StatusText = $"Import failed: {ex.Message}";
            }

            IsProcessing = false;
        }
    }

    #endregion
}
