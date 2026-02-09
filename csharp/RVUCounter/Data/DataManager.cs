using System.IO;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RVUCounter.Core;
using RVUCounter.Logic;
using RVUCounter.Models;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RVUCounter.Data;

/// <summary>
/// Manages application data: settings, RVU rules, and database access.
/// Provides YAML serialization and HIPAA-compliant hashing.
/// </summary>
public class DataManager : IDisposable
{
    private readonly string _baseDir;
    private readonly string _settingsPath;
    private readonly string _rulesPath;
    private readonly string _databasePath;

    private UserSettings _settings;
    private RvuRulesConfig _rules;
    private RecordsDatabase? _database;
    private readonly object _databaseLock = new();
    // _hipaaSaltHex is defined in HIPAA region below

    private readonly ISerializer _yamlSerializer;
    private readonly IDeserializer _yamlDeserializer;

    public DataManager(string? baseDir = null)
    {
        _baseDir = baseDir ?? PlatformUtils.GetAppRoot();

        // Setup paths
        _settingsPath = Config.GetUserSettingsFile(_baseDir);
        _rulesPath = Config.GetRulesFile(_baseDir);
        _databasePath = Config.GetDatabaseFile(_baseDir);

        // Create directories
        PlatformUtils.EnsureDirectoriesExist();

        // Setup YAML serializer
        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        // Load everything
        _settings = LoadSettings();
        _rules = LoadRules();
        LoadOrGenerateSalt();

        // Check for monthly reset of projection settings
        CheckMonthlyReset();

        Log.Information("DataManager initialized at {BaseDir}", _baseDir);
    }

    #region Properties

    public UserSettings Settings => _settings;
    public RvuRulesConfig Rules => _rules;
    public Dictionary<string, double> RvuTable => _rules.RvuTable;
    public Dictionary<string, List<ClassificationCondition>> ClassificationRules => _rules.ClassificationRules;
    public string DatabasePath => _databasePath;

    public RecordsDatabase Database
    {
        get
        {
            if (_database != null) return _database;
            lock (_databaseLock)
            {
                _database ??= new RecordsDatabase(_databasePath);
                return _database;
            }
        }
    }

    #endregion

    #region Settings Management

    private UserSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var yaml = File.ReadAllText(_settingsPath);
                var settings = _yamlDeserializer.Deserialize<UserSettings>(yaml);
                if (settings != null)
                {
                    Log.Debug("Loaded settings from {Path}", _settingsPath);
                    return ValidateWindowPositions(settings);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings, using defaults");
        }

        return new UserSettings();
    }

    public void SaveSettings()
    {
        try
        {
            var yaml = _yamlSerializer.Serialize(_settings);
            File.WriteAllText(_settingsPath, yaml);
            Log.Debug("Saved settings to {Path}", _settingsPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
        }
    }

    private UserSettings ValidateWindowPositions(UserSettings settings)
    {
        // Window position validation is now done in the window's Loaded event
        // using SystemParameters.VirtualScreen* which is more reliable for multi-monitor
        // Don't modify positions here - just return as-is
        return settings;
    }

    /// <summary>
    /// Update a window's saved position.
    /// </summary>
    public void SaveWindowPosition(string windowName, double x, double y, double width, double height)
    {
        var position = new WindowPosition { X = x, Y = y, Width = width, Height = height };

        switch (windowName.ToLower())
        {
            case "main":
                _settings.MainWindowPosition = position;
                break;
            case "settings":
                _settings.SettingsWindowPosition = position;
                break;
            case "statistics":
                _settings.StatisticsWindowPosition = position;
                break;
            case "tools":
                _settings.ToolsWindowPosition = position;
                break;
        }

        SaveSettings();
    }

    #endregion

    #region RVU Rules Management

    private RvuRulesConfig LoadRules()
    {
        try
        {
            // Load from external file in resources/ folder
            if (File.Exists(_rulesPath))
            {
                var yaml = File.ReadAllText(_rulesPath);
                var rules = _yamlDeserializer.Deserialize<RvuRulesConfig>(yaml);
                if (rules != null)
                {
                    // Parse the classification rules into proper objects
                    rules = ParseClassificationRules(rules, yaml);
                    Log.Debug("Loaded RVU rules from {Path}", _rulesPath);
                    return rules;
                }
            }

            Log.Warning("Could not find RVU rules file at {Path}", _rulesPath);
            return new RvuRulesConfig();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load rules, using defaults");
            return new RvuRulesConfig();
        }
    }

    private RvuRulesConfig ParseClassificationRules(RvuRulesConfig rules, string yaml)
    {
        // The YAML structure for classification_rules is complex.
        // We need to manually parse it since the conditions are lists of dicts.
        // For now, use a simpler approach with raw deserialization.
        try
        {
            var rawRules = _yamlDeserializer.Deserialize<Dictionary<string, object>>(yaml);
            if (rawRules != null && rawRules.ContainsKey("classification_rules"))
            {
                var classRules = rawRules["classification_rules"] as Dictionary<object, object>;
                if (classRules != null)
                {
                    rules.ClassificationRules = new Dictionary<string, List<ClassificationCondition>>();

                    foreach (var kvp in classRules)
                    {
                        var studyType = kvp.Key.ToString()!;
                        var conditions = new List<ClassificationCondition>();

                        if (kvp.Value is List<object> conditionsList)
                        {
                            foreach (var condObj in conditionsList)
                            {
                                if (condObj is Dictionary<object, object> condDict)
                                {
                                    var condition = new ClassificationCondition();

                                    if (condDict.TryGetValue("required_keywords", out var required) && required is List<object> reqList)
                                        condition.RequiredKeywords = reqList.Select(x => x.ToString()!).ToList();

                                    if (condDict.TryGetValue("any_of_keywords", out var anyOf) && anyOf is List<object> anyList)
                                        condition.AnyOfKeywords = anyList.Select(x => x.ToString()!).ToList();

                                    if (condDict.TryGetValue("excluded_keywords", out var excluded) && excluded is List<object> exList)
                                        condition.ExcludedKeywords = exList.Select(x => x.ToString()!).ToList();

                                    conditions.Add(condition);
                                }
                            }
                        }

                        rules.ClassificationRules[studyType] = conditions;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse classification rules");
        }

        return rules;
    }

    public void ReloadRules()
    {
        _rules = LoadRules();
        Log.Information("Reloaded RVU rules");
    }

    #endregion

    #region HIPAA Compliance

    // Salt stored as hex string (Python-compatible format)
    private string? _hipaaSaltHex;

    private void LoadOrGenerateSalt()
    {
        // Check for existing salt (hex string, Python-compatible format)
        if (!string.IsNullOrEmpty(_settings.HipaaSalt))
        {
            _hipaaSaltHex = _settings.HipaaSalt;
            Log.Debug("Loaded HIPAA salt");
            return;
        }

        // Generate new Python-compatible salt (64-char hex string)
        var saltBytes = RandomNumberGenerator.GetBytes(32);
        _hipaaSaltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
        _settings.HipaaSalt = _hipaaSaltHex;
        SaveSettings();
        Log.Information("Generated new HIPAA salt (Python-compatible format)");
    }

    /// <summary>
    /// Hash an accession number for privacy.
    /// Uses Python-compatible algorithm: SHA256(accession + salt) → 64-char hex
    /// </summary>
    public string HashAccession(string accession)
    {
        if (string.IsNullOrEmpty(_hipaaSaltHex))
            return accession;

        // Python algorithm: SHA256(accession + salt_hex_string) as UTF-8, output as hex
        var salted = accession.Trim() + _hipaaSaltHex;
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(salted));

        // Return full 64-char hex (matches Python's hashlib.sha256().hexdigest())
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    #endregion

    #region Data Access Helpers

    /// <summary>
    /// Get all accessions in a date range.
    /// </summary>
    public HashSet<string> GetAccessionsInRange(DateTime start, DateTime end)
    {
        var records = Database.GetRecordsInDateRange(start, end);
        return records.Select(r => r.Accession).ToHashSet();
    }

    /// <summary>
    /// Get all records.
    /// </summary>
    public List<StudyRecord> GetAllRecords()
    {
        return Database.GetAllRecords();
    }

    /// <summary>
    /// Calculate average durations by study type in a date range.
    /// </summary>
    public Dictionary<string, double> GetAverageDurations(DateTime start, DateTime end)
    {
        return Database.GetAverageDurations(start, end);
    }

    /// <summary>
    /// Get records for audit comparison including RVUs.
    /// </summary>
    public List<StudyRecord> GetAuditDbData(DateTime start, DateTime end)
    {
        return Database.GetRecordsInDateRange(start, end);
    }

    #endregion

    #region Audit and Reconciliation

    /// <summary>
    /// Perform a full audit comparing Excel data with database.
    /// </summary>
    public async Task<ExcelAuditResult> PerformFullAuditAsync(
        string excelPath,
        DateTime? startDate = null,
        DateTime? endDate = null,
        IProgress<int>? progress = null)
    {
        var checker = new ExcelChecker(this);
        return await checker.AuditExcelFileAsync(excelPath, startDate, endDate, progress);
    }

    /// <summary>
    /// Apply reconciliation fixes to the database based on audit results.
    /// </summary>
    public async Task<ReconciliationResult> ReconcileDatabaseAsync(ExcelAuditResult auditResult)
    {
        var result = new ReconciliationResult();

        try
        {
            await Task.Run(() =>
            {
                // 1. Delete extra records in DB (not in Excel)
                foreach (var record in auditResult.ExtraInDbDetails)
                {
                    Database.DeleteRecord(record.Id);
                    result.DeletedCount++;
                }

                // 2. Add missing records from Excel
                var missingRecords = auditResult.MissingFromDbDetails;
                if (missingRecords.Count == 0)
                {
                    result.Success = true;
                    return;
                }

                // Sort by timestamp
                missingRecords = missingRecords.OrderBy(r => r.Timestamp).ToList();

                // Get average durations for this period
                var avgDurations = GetAverageDurations(auditResult.StartDate, auditResult.EndDate);

                // Get existing shifts for this period
                var existingShifts = Database.GetShiftsInDateRange(auditResult.StartDate, auditResult.EndDate);

                // Cluster missing studies (9 hour threshold)
                var clusters = ClusterRecords(missingRecords, TimeSpan.FromHours(9));

                foreach (var cluster in clusters)
                {
                    if (cluster.Count == 0) continue;
                    var clusterStart = cluster[0].Timestamp;
                    var clusterEnd = cluster[cluster.Count - 1].Timestamp;

                    // Find if this cluster fits in an existing shift
                    int? targetShiftId = null;
                    foreach (var shift in existingShifts)
                    {
                        var shiftStart = shift.ShiftStart;
                        var shiftEnd = shift.ShiftEnd ?? shift.ShiftStart.AddHours(9);

                        // If cluster range overlaps or is very close (within 30m)
                        if (clusterStart >= shiftStart.AddMinutes(-30) &&
                            clusterEnd <= shiftEnd.AddMinutes(30))
                        {
                            targetShiftId = shift.Id;
                            break;
                        }
                    }

                    if (targetShiftId == null)
                    {
                        // Create new historical shift
                        var shiftName = clusterStart.ToString("yyyy-MM-dd") + " (a)";
                        targetShiftId = Database.InsertHistoricalShift(
                            clusterStart.AddMinutes(-10),
                            clusterEnd.AddMinutes(10),
                            shiftName);
                        result.ShiftsCreated++;
                    }

                    // Add records to shift
                    foreach (var excelRecord in cluster)
                    {
                        var duration = avgDurations.GetValueOrDefault(excelRecord.Procedure, 120.0);
                        var record = new StudyRecord
                        {
                            Accession = HashAccession(excelRecord.Accession),
                            Procedure = excelRecord.Procedure,
                            StudyType = StudyMatcher.MatchStudyType(excelRecord.Procedure, RvuTable, ClassificationRules).StudyType,
                            Rvu = excelRecord.Rvu,
                            Timestamp = excelRecord.Timestamp.AddSeconds(-duration),
                            PatientClass = excelRecord.PatientClass,
                            DurationSeconds = duration,
                            Source = "Reconciliation"
                        };

                        Database.AddRecord(targetShiftId.Value, record);
                        result.AddedCount++;
                    }
                }

                // 3. Cleanup empty shifts
                Database.DeleteEmptyShifts(auditResult.StartDate, auditResult.EndDate);

                result.Success = true;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during reconciliation");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Cluster records by time gap threshold.
    /// </summary>
    private List<List<ExcelRecord>> ClusterRecords(List<ExcelRecord> records, TimeSpan threshold)
    {
        var clusters = new List<List<ExcelRecord>>();
        if (records.Count == 0) return clusters;

        var currentCluster = new List<ExcelRecord> { records[0] };

        for (int i = 1; i < records.Count; i++)
        {
            var prevTime = records[i - 1].Timestamp;
            var currTime = records[i].Timestamp;

            if ((currTime - prevTime) <= threshold)
            {
                currentCluster.Add(records[i]);
            }
            else
            {
                clusters.Add(currentCluster);
                currentCluster = new List<ExcelRecord> { records[i] };
            }
        }

        clusters.Add(currentCluster);
        return clusters;
    }

    /// <summary>
    /// Calculate projected number of new shifts for missing records.
    /// </summary>
    public int CalculateProjectedShifts(List<ExcelRecord> missingRecords, DateTime startDate, DateTime endDate)
    {
        if (missingRecords.Count == 0) return 0;

        try
        {
            var sorted = missingRecords.OrderBy(r => r.Timestamp).ToList();
            var existingShifts = Database.GetShiftsInDateRange(startDate, endDate);

            var clusters = ClusterRecords(sorted, TimeSpan.FromHours(9));
            var projectedCount = 0;

            foreach (var cluster in clusters)
            {
                if (cluster.Count == 0) continue;
                var clusterStart = cluster[0].Timestamp;
                var clusterEnd = cluster[cluster.Count - 1].Timestamp;

                var fitsExisting = existingShifts.Any(shift =>
                {
                    var shiftStart = shift.ShiftStart;
                    var shiftEnd = shift.ShiftEnd ?? shift.ShiftStart.AddHours(9);
                    return clusterStart >= shiftStart.AddMinutes(-30) &&
                           clusterEnd <= shiftEnd.AddMinutes(30);
                });

                if (!fitsExisting)
                    projectedCount++;
            }

            return projectedCount;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error calculating projected shifts");
            return 0;
        }
    }

    #endregion

    #region Shift Management

    /// <summary>
    /// Clear records for current shift.
    /// </summary>
    public void ClearCurrentShift()
    {
        var currentShift = Database.GetCurrentShift();
        if (currentShift != null)
        {
            Database.EndCurrentShift(DateTime.Now);
            Log.Information("Cleared current shift");
        }
    }

    /// <summary>
    /// Clear all historical data.
    /// </summary>
    public void ClearAllData()
    {
        Database.ClearAllData();
        Log.Information("Cleared all data from database");
    }

    #endregion

    #region Payroll Sync

    /// <summary>
    /// Apply a time offset to all records in the database.
    /// </summary>
    public int ApplyPayrollSync(TimeSpan offset)
    {
        var currentShift = Database.GetCurrentShift();
        if (currentShift == null)
        {
            Log.Warning("No current shift to apply payroll sync");
            return 0;
        }

        var count = Database.BatchUpdateTimestampsByOffset(currentShift.Id, offset);
        Log.Information("Applied payroll sync offset {Offset} to {Count} records", offset, count);
        return count;
    }

    /// <summary>
    /// Save the detected payroll offset to settings.
    /// </summary>
    public void SavePayrollOffset(double offsetHours)
    {
        _settings.PayrollTimeOffsetHours = offsetHours;
        SaveSettings();
        Log.Information("Saved payroll offset: {Offset} hours", offsetHours);
    }

    /// <summary>
    /// Get the stored payroll offset in hours.
    /// </summary>
    public double GetPayrollOffset()
    {
        return _settings.PayrollTimeOffsetHours;
    }

    #endregion

    #region Export/Import

    /// <summary>
    /// Export all records to a JSON file.
    /// </summary>
    public string ExportRecordsToJson(string? filepath = null)
    {
        if (filepath == null)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dataDir = Path.GetDirectoryName(_databasePath) ?? _baseDir;
            filepath = Path.Combine(dataDir, $"rvu_records_backup_{timestamp}.json");
        }

        var data = new
        {
            ExportDate = DateTime.Now,
            Shifts = Database.GetAllShifts().Select(s => new
            {
                s.Id,
                s.ShiftStart,
                s.ShiftEnd,
                s.ShiftName,
                Records = Database.GetRecordsForShift(s.Id)
            })
        };

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(filepath, json);

        Log.Information("Exported records to: {Path}", filepath);
        return filepath;
    }

    /// <summary>
    /// Import records from a JSON backup file.
    /// </summary>
    public bool ImportRecordsFromJson(string filepath)
    {
        try
        {
            var json = File.ReadAllText(filepath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Parse all data first before touching the database
            var parsedShifts = new List<(DateTime start, DateTime? end, string? name, List<StudyRecord> records)>();

            if (root.TryGetProperty("Shifts", out var shifts))
            {
                foreach (var shiftElement in shifts.EnumerateArray())
                {
                    var shiftStart = DateTime.Parse(shiftElement.GetProperty("ShiftStart").GetString() ?? "");
                    var shiftEndStr = shiftElement.GetProperty("ShiftEnd").GetString();
                    var shiftEnd = string.IsNullOrEmpty(shiftEndStr) ? (DateTime?)null : DateTime.Parse(shiftEndStr);
                    var shiftName = shiftElement.TryGetProperty("ShiftName", out var nameProp)
                        ? nameProp.GetString()
                        : null;

                    var recordList = new List<StudyRecord>();
                    if (shiftElement.TryGetProperty("Records", out var records))
                    {
                        foreach (var recElement in records.EnumerateArray())
                        {
                            recordList.Add(new StudyRecord
                            {
                                Accession = recElement.GetProperty("Accession").GetString() ?? "",
                                Procedure = recElement.TryGetProperty("Procedure", out var proc) ? proc.GetString() ?? "" : "",
                                StudyType = recElement.TryGetProperty("StudyType", out var st) ? st.GetString() ?? "" : "",
                                Rvu = recElement.TryGetProperty("Rvu", out var rvu) ? rvu.GetDouble() : 0,
                                Timestamp = DateTime.Parse(recElement.GetProperty("Timestamp").GetString() ?? ""),
                                PatientClass = recElement.TryGetProperty("PatientClass", out var pc) ? pc.GetString() ?? "" : "",
                                DurationSeconds = recElement.TryGetProperty("DurationSeconds", out var dur) ? dur.GetDouble() : null
                            });
                        }
                    }

                    parsedShifts.Add((shiftStart, shiftEnd, shiftName, recordList));
                }
            }

            // All parsing succeeded — now safe to clear and import
            Database.ClearAllData();

            foreach (var (start, end, name, recordList) in parsedShifts)
            {
                int shiftId = end.HasValue
                    ? Database.InsertHistoricalShift(start, end.Value, name)
                    : Database.StartShift(start);

                if (recordList.Count > 0)
                {
                    Database.BatchAddRecords(shiftId, recordList);
                }
            }

            Log.Information("Imported records from: {Path}", filepath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error importing records from JSON");
            return false;
        }
    }

    #endregion

    #region Monthly Reset

    /// <summary>
    /// Check and reset projection settings if a new month has started.
    /// </summary>
    private void CheckMonthlyReset()
    {
        var currentMonth = DateTime.Now.ToString("yyyy-MM");
        var lastMonth = _settings.LastProjectionMonth;

        if (lastMonth != currentMonth)
        {
            Log.Information("New month detected ({Month}). Resetting projection settings.", currentMonth);

            _settings.ProjectionDays = 14;
            _settings.ProjectionExtraDays = 0;
            _settings.ProjectionExtraHours = 0;
            _settings.LastProjectionMonth = currentMonth;

            SaveSettings();
        }
    }

    #endregion

    public void Dispose()
    {
        _database?.Dispose();
    }
}

/// <summary>
/// Result of database reconciliation.
/// </summary>
public class ReconciliationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int DeletedCount { get; set; }
    public int AddedCount { get; set; }
    public int ShiftsCreated { get; set; }
}
