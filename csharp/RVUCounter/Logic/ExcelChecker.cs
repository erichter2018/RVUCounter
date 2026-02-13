using System.IO;
using ClosedXML.Excel;
using RVUCounter.Data;
using RVUCounter.Models;
using Serilog;

namespace RVUCounter.Logic;

/// <summary>
/// Validates payroll Excel reports against database records.
/// Ported from Python excel_checker.py with full parity.
/// </summary>
public class ExcelChecker
{
    private readonly DataManager _dataManager;

    public ExcelChecker(DataManager dataManager)
    {
        _dataManager = dataManager;
    }

    /// <summary>
    /// Check Excel file for RVU outliers (Python parity: check_file).
    /// Uses StandardProcedureName and wRVU_Matrix columns.
    /// Searches all worksheets to find the one with required columns.
    /// </summary>
    public ExcelRvuCheckResult CheckFile(string filePath, IProgress<(int Current, int Total)>? progress = null)
    {
        var result = new ExcelRvuCheckResult { FileName = Path.GetFileName(filePath) };

        try
        {
            using var workbook = new XLWorkbook(filePath);
            if (workbook.Worksheets.Count == 0)
            {
                result.ErrorMessage = "No worksheets found";
                return result;
            }

            // Search all worksheets for the one with required columns
            IXLWorksheet? sheet = null;
            int? procCol = null, rvuCol = null;
            var allHeaders = new List<string>();

            foreach (var ws in workbook.Worksheets)
            {
                procCol = null;
                rvuCol = null;
                var headers = new List<string>();

                var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 1;
                for (int col = 1; col <= Math.Min(lastCol, 50); col++)
                {
                    var header = ws.Cell(1, col).GetString();
                    headers.Add(header);
                    if (header == "StandardProcedureName") procCol = col;
                    else if (header == "wRVU_Matrix") rvuCol = col;
                }

                if (procCol != null && rvuCol != null)
                {
                    sheet = ws;
                    Log.Information("Found RVU columns in worksheet: {SheetName}", ws.Name);
                    break;
                }

                allHeaders.AddRange(headers);
            }

            if (sheet == null || procCol == null || rvuCol == null)
            {
                result.ErrorMessage = $"Missing required columns: StandardProcedureName, wRVU_Matrix. Found headers: {string.Join(", ", allHeaders.Take(20))}";
                return result;
            }

            var outliers = new List<RvuOutlier>();
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            var totalRows = lastRow - 1;

            for (int row = 2; row <= lastRow; row++)
            {
                progress?.Report((row - 1, totalRows));

                var procText = sheet.Cell(row, procCol.Value).GetString();
                var excelRvuStr = sheet.Cell(row, rvuCol.Value).GetString();

                if (string.IsNullOrEmpty(procText) || string.IsNullOrEmpty(excelRvuStr))
                    continue;

                if (!double.TryParse(excelRvuStr, out var excelRvu))
                    continue;

                var (matchedType, matchedRvu) = StudyMatcher.MatchStudyType(
                    procText,
                    _dataManager.RvuTable,
                    _dataManager.ClassificationRules);

                // Compare with epsilon (Python parity)
                if (Math.Abs(excelRvu - matchedRvu) > 0.01)
                {
                    outliers.Add(new RvuOutlier
                    {
                        Procedure = procText,
                        ExcelRvu = excelRvu,
                        MatchedType = matchedType,
                        MatchedRvu = matchedRvu,
                        Row = row
                    });
                }
            }

            result.Success = true;
            result.TotalProcessed = totalRows;
            result.Outliers = outliers;

            Log.Information("Excel check complete: {Outliers} outliers in {Total} records",
                outliers.Count, totalRows);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking Excel file");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Generate text report for RVU outliers (Python parity).
    /// </summary>
    public string GenerateReportText(ExcelRvuCheckResult results)
    {
        if (!string.IsNullOrEmpty(results.ErrorMessage))
            return $"ERROR: {results.ErrorMessage}";

        var uniqueOutliers = results.Outliers
            .GroupBy(o => (o.Procedure, o.ExcelRvu, o.MatchedType, o.MatchedRvu))
            .ToDictionary(g => g.Key, g => g.Count());

        var lines = new List<string>
        {
            new string('=', 80),
            "RVU COMPARISON REPORT",
            new string('=', 80),
            $"Excel File: {results.FileName}",
            $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            new string('-', 80),
            $"Total Procedures Processed: {results.TotalProcessed}",
            $"Total Outliers Found: {results.Outliers.Count}",
            new string('-', 80)
        };

        if (results.Outliers.Count == 0)
        {
            lines.Add("SUCCESS: All procedures match the rules!");
        }
        else
        {
            lines.Add($"Unique Outlier Procedures: {uniqueOutliers.Count}");
            lines.Add("");

            foreach (var (key, count) in uniqueOutliers)
            {
                lines.Add($"  Procedure: {key.Procedure}");
                lines.Add($"    Excel RVU: {key.ExcelRvu}");
                lines.Add($"    Matched Type: {key.MatchedType}");
                lines.Add($"    Matched RVU: {key.MatchedRvu}");
                lines.Add($"    Instances: {count}");
                lines.Add("");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Parse month/year from filename (Python parity).
    /// Supports formats like "Dec-25", "Jan-2026".
    /// </summary>
    private (DateTime? Start, DateTime? End) ParseDateFromFilename(string filename)
    {
        var monthMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            {"jan", 1}, {"feb", 2}, {"mar", 3}, {"apr", 4}, {"may", 5}, {"jun", 6},
            {"jul", 7}, {"aug", 8}, {"sep", 9}, {"oct", 10}, {"nov", 11}, {"dec", 12}
        };

        var parts = Path.GetFileName(filename).Split(' ')[0].Split('-');
        if (parts.Length >= 2 && monthMap.TryGetValue(parts[0], out var month))
        {
            if (int.TryParse(parts[1], out var yearPart))
            {
                var year = yearPart < 100 ? 2000 + yearPart : yearPart;
                var lastDay = DateTime.DaysInMonth(year, month);
                return (new DateTime(year, month, 1), new DateTime(year, month, lastDay, 23, 59, 59));
            }
        }
        return (null, null);
    }

    /// <summary>
    /// Check accessions in Excel against database (Python parity: check_accessions).
    /// </summary>
    public ExcelAccessionCheckResult CheckAccessions(
        string filePath,
        Func<DateTime, DateTime, HashSet<string>> dbLookup,
        Func<string, string>? hasher = null,
        IProgress<(int Current, int Total)>? progress = null)
    {
        var result = new ExcelAccessionCheckResult { FileName = Path.GetFileName(filePath) };

        try
        {
            var (startDate, endDate) = ParseDateFromFilename(filePath);
            if (startDate == null || endDate == null)
            {
                result.ErrorMessage = $"Could not determine date from filename. Expected format: 'Mmm-YY ...' (e.g. Dec-25)";
                return result;
            }

            result.TargetMonth = startDate.Value.ToString("MMMM yyyy");

            using var workbook = new XLWorkbook(filePath);
            var sheet = workbook.Worksheets.FirstOrDefault();
            if (sheet == null)
            {
                result.ErrorMessage = "No worksheets found";
                return result;
            }

            // Find ExamAccession column
            int? accCol = null;
            for (int col = 1; col <= sheet.LastColumnUsed()?.ColumnNumber(); col++)
            {
                if (sheet.Cell(1, col).GetString() == "ExamAccession")
                {
                    accCol = col;
                    break;
                }
            }

            if (accCol == null)
            {
                result.ErrorMessage = "Missing required column: ExamAccession";
                return result;
            }

            // Read accessions from Excel
            var accessions = new List<string>();
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

            for (int row = 2; row <= lastRow; row++)
            {
                var val = sheet.Cell(row, accCol.Value).GetString()?.Trim();
                if (!string.IsNullOrEmpty(val))
                    accessions.Add(val);

                if (row % 100 == 0)
                    progress?.Report((row, lastRow * 2));
            }

            if (accessions.Count == 0)
            {
                result.ErrorMessage = "No accessions found in file";
                return result;
            }

            // Get DB accessions
            var dbAccessions = dbLookup(startDate.Value, endDate.Value);

            // Compare
            var excelSet = accessions.ToHashSet();
            var missingFromDb = new List<string>();

            foreach (var acc in accessions)
            {
                var lookupKey = hasher != null ? hasher(acc) : acc;
                if (!dbAccessions.Contains(lookupKey))
                    missingFromDb.Add(acc);
            }

            var excelLookupSet = hasher != null
                ? accessions.Select(hasher).ToHashSet()
                : excelSet;

            var extraInDb = dbAccessions.Except(excelLookupSet).OrderBy(x => x).ToList();

            progress?.Report((lastRow, lastRow));

            result.Success = true;
            result.TotalChecked = accessions.Count;
            result.MissingFromDb = missingFromDb;
            result.ExtraInDb = extraInDb;

            Log.Information("Accession check: {Missing} missing from DB, {Extra} extra in DB",
                missingFromDb.Count, extraInDb.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking accessions");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Compare Excel file with database records.
    /// </summary>
    public async Task<ExcelAuditResult> AuditExcelFileAsync(
        string excelPath,
        DateTime? startDate = null,
        DateTime? endDate = null,
        IProgress<int>? progress = null)
    {
        // Normalize dates to cover full days (DatePicker gives midnight, which excludes the last day)
        var normalizedStart = (startDate ?? DateTime.Today.AddDays(-30)).Date;
        var normalizedEnd = (endDate ?? DateTime.Today).Date.AddDays(1).AddTicks(-1);

        var result = new ExcelAuditResult
        {
            ExcelPath = excelPath,
            StartDate = normalizedStart,
            EndDate = normalizedEnd
        };

        if (!File.Exists(excelPath))
        {
            result.ErrorMessage = "Excel file not found";
            return result;
        }

        try
        {
            // Read Excel file
            var excelRecords = await Task.Run(() => ReadExcelFile(excelPath, progress));
            result.TotalExcel = excelRecords.Count;
            Log.Information("Read {Count} records from Excel", excelRecords.Count);

            // Filter by date range
            excelRecords = excelRecords
                .Where(r => r.Timestamp >= result.StartDate && r.Timestamp <= result.EndDate)
                .ToList();
            result.TotalExcelInRange = excelRecords.Count;

            // Get database records
            var dbRecords = _dataManager.Database.GetRecordsInDateRange(result.StartDate, result.EndDate);
            result.TotalDb = dbRecords.Count;

            // Compare accessions - hash Excel accessions to match HIPAA-hashed DB accessions
            // Create a mapping from hashed accession -> raw accession for lookup
            var excelHashToRaw = new Dictionary<string, string>();
            foreach (var record in excelRecords)
            {
                var raw = record.Accession;
                if (!string.IsNullOrEmpty(raw))
                {
                    var hashed = _dataManager.HashAccession(raw);
                    excelHashToRaw[hashed] = raw;
                }
            }

            var excelHashedAccessions = excelHashToRaw.Keys.ToHashSet();

            var dbAccessions = dbRecords
                .Select(r => r.Accession) // DB accessions are already hashed
                .Where(a => !string.IsNullOrEmpty(a))
                .ToHashSet();

            // Find discrepancies using hashed accessions
            var missingHashedFromDb = excelHashedAccessions.Except(dbAccessions).ToList();
            var extraInDb = dbAccessions.Except(excelHashedAccessions).ToList();
            var matchedHashed = excelHashedAccessions.Intersect(dbAccessions).ToList();

            result.Matched = matchedHashed.Count;
            result.MissingFromDb = missingHashedFromDb.Count;
            result.ExtraInDb = extraInDb.Count;

            // Get details for missing records (convert back to raw accession for display)
            var missingRawAccessions = missingHashedFromDb
                .Where(h => excelHashToRaw.ContainsKey(h))
                .Select(h => excelHashToRaw[h])
                .ToHashSet();

            result.MissingFromDbDetails = excelRecords
                .Where(r => missingRawAccessions.Contains(r.Accession))
                .OrderBy(r => r.Timestamp)
                .ToList();

            // Get details for extra records
            result.ExtraInDbDetails = dbRecords
                .Where(r => extraInDb.Contains(r.Accession))
                .OrderBy(r => r.Timestamp)
                .ToList();

            // Calculate RVU comparison
            result.TotalExcelRvu = excelRecords.Sum(r => r.Rvu);
            result.TotalDbRvu = dbRecords.Sum(r => r.Rvu);
            result.RvuDifference = result.TotalDbRvu - result.TotalExcelRvu;

            // Build matched records mapping (hashed accession -> Excel record) for payroll sync
            var excelByHash = excelRecords
                .Where(r => !string.IsNullOrEmpty(r.Accession))
                .ToDictionary(r => _dataManager.HashAccession(r.Accession), r => r);
            result.MatchedRecords = matchedHashed
                .Where(h => excelByHash.ContainsKey(h))
                .ToDictionary(h => h, h => excelByHash[h]);

            result.Success = true;
            Log.Information("Excel audit complete: {Matched} matched, {Missing} missing, {Extra} extra",
                result.Matched, result.MissingFromDb, result.ExtraInDb);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Excel audit failed");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Read records from Excel file.
    /// </summary>
    private List<ExcelRecord> ReadExcelFile(string path, IProgress<int>? progress = null)
    {
        var records = new List<ExcelRecord>();

        using var workbook = new XLWorkbook(path);

        if (workbook.Worksheets.Count == 0)
        {
            Log.Warning("No worksheets found in Excel file");
            return records;
        }

        // Find the worksheet with ExamAccession column (the detail data sheet)
        IXLWorksheet? worksheet = null;
        foreach (var ws in workbook.Worksheets)
        {
            // Check first row for ExamAccession column
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 1;
            for (int col = 1; col <= Math.Min(lastCol, 50); col++)
            {
                var header = ws.Cell(1, col).GetString();
                if (header.Equals("ExamAccession", StringComparison.OrdinalIgnoreCase))
                {
                    worksheet = ws;
                    Log.Information("Found ExamAccession column in worksheet: {SheetName}", ws.Name);
                    break;
                }
            }
            if (worksheet != null) break;

            // Also check row 2 in case headers are there
            for (int col = 1; col <= Math.Min(lastCol, 50); col++)
            {
                var header = ws.Cell(2, col).GetString();
                if (header.Equals("ExamAccession", StringComparison.OrdinalIgnoreCase))
                {
                    worksheet = ws;
                    Log.Information("Found ExamAccession column in worksheet (row 2): {SheetName}", ws.Name);
                    break;
                }
            }
            if (worksheet != null) break;
        }

        // Fallback to first worksheet if ExamAccession not found
        if (worksheet == null)
        {
            worksheet = workbook.Worksheets.First();
            Log.Warning("ExamAccession column not found in any worksheet, using first: {SheetName}", worksheet.Name);
        }

        Log.Information("Excel file has {SheetCount} worksheets, using: {SheetName}",
            workbook.Worksheets.Count, worksheet.Name);

        // Find header row and column mapping
        var headerRow = FindHeaderRow(worksheet);
        if (headerRow == null)
        {
            Log.Warning("Could not find header row in Excel file");
            return records;
        }

        Log.Information("Header row found at row {Row}", headerRow.Value);

        var columnMap = MapColumns(worksheet, headerRow.Value);

        Log.Information("Column mapping: accession={Acc}, procedure={Proc}, timestamp={Time}, rvu={Rvu}",
            columnMap.GetValueOrDefault("accession", -1),
            columnMap.GetValueOrDefault("procedure", -1),
            columnMap.GetValueOrDefault("timestamp", -1),
            columnMap.GetValueOrDefault("rvu", -1));

        if (!columnMap.ContainsKey("accession"))
        {
            // Log all headers for debugging
            var lastCol = worksheet.LastColumnUsed()?.ColumnNumber() ?? 1;
            var headers = new List<string>();
            for (int col = 1; col <= lastCol; col++)
            {
                headers.Add(worksheet.Cell(headerRow.Value, col).GetString());
            }
            Log.Warning("Could not find accession column. Available headers: {Headers}", string.Join(", ", headers));
            return records;
        }

        // Read data rows
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow.Value;
        Log.Information("Reading rows {Start} to {End}", headerRow.Value + 1, lastRow);
        var totalRows = lastRow - headerRow.Value;
        var processedRows = 0;

        for (int row = headerRow.Value + 1; row <= lastRow; row++)
        {
            try
            {
                var record = ReadRow(worksheet, row, columnMap);
                if (record != null)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error reading row {Row}", row);
            }

            processedRows++;
            if (processedRows % 100 == 0)
            {
                progress?.Report((int)(100.0 * processedRows / totalRows));
            }
        }

        return records;
    }

    /// <summary>
    /// Find header row by looking for common column names.
    /// Supports payroll Excel format with ExamAccession, StandardProcedureName columns.
    /// </summary>
    private int? FindHeaderRow(IXLWorksheet worksheet)
    {
        // Exact column names from payroll Excel
        var payrollHeaders = new[] { "examaccession", "standardprocedurename", "examfinalreportdt" };
        // Generic column names
        var commonHeaders = new[] { "accession", "procedure", "study", "rvu", "date", "time" };

        for (int row = 1; row <= Math.Min(20, worksheet.LastRowUsed()?.RowNumber() ?? 1); row++)
        {
            var cellValues = new List<string>();
            for (int col = 1; col <= Math.Min(30, worksheet.LastColumnUsed()?.ColumnNumber() ?? 1); col++)
            {
                var value = worksheet.Cell(row, col).GetString().ToLowerInvariant().Replace("_", "");
                cellValues.Add(value);
            }

            // First check for payroll-specific headers
            var payrollMatch = payrollHeaders.Count(h => cellValues.Any(v => v.Contains(h)));
            if (payrollMatch >= 2)
            {
                Log.Debug("Found payroll header row at row {Row}", row);
                return row;
            }

            // Then check for generic headers
            var genericMatch = commonHeaders.Count(h => cellValues.Any(v => v.Contains(h)));
            if (genericMatch >= 2)
            {
                Log.Debug("Found generic header row at row {Row}", row);
                return row;
            }
        }

        // Default to row 1 if nothing found
        Log.Debug("No header row found, defaulting to row 1");
        return 1;
    }

    /// <summary>
    /// Map column names to column numbers.
    /// Supports both payroll-specific and generic column names.
    /// </summary>
    private Dictionary<string, int> MapColumns(IXLWorksheet worksheet, int headerRow)
    {
        var map = new Dictionary<string, int>();
        var lastCol = worksheet.LastColumnUsed()?.ColumnNumber() ?? 1;

        for (int col = 1; col <= lastCol; col++)
        {
            var header = worksheet.Cell(headerRow, col).GetString().Trim();
            var headerLower = header.ToLowerInvariant().Replace("_", "");

            // Exact payroll column names (case-insensitive)
            if (header.Equals("ExamAccession", StringComparison.OrdinalIgnoreCase))
                map["accession"] = col;
            else if (header.Equals("StandardProcedureName", StringComparison.OrdinalIgnoreCase))
                map["procedure"] = col;
            else if (header.StartsWith("ExamFinalReportDT", StringComparison.OrdinalIgnoreCase))
                map["timestamp"] = col;
            else if (header.Equals("wRVU_Matrix", StringComparison.OrdinalIgnoreCase))
                map["rvu"] = col;
            // Generic column matching
            else if (!map.ContainsKey("accession") && headerLower.Contains("accession"))
                map["accession"] = col;
            else if (!map.ContainsKey("procedure") && (headerLower.Contains("procedure") || headerLower.Contains("description")))
                map["procedure"] = col;
            else if (!map.ContainsKey("rvu") && headerLower.Contains("rvu") && !headerLower.Contains("total"))
                map["rvu"] = col;
            else if (!map.ContainsKey("timestamp") && (headerLower.Contains("date") || headerLower.Contains("time") || headerLower.Contains("performed") || headerLower.Contains("final")))
                map["timestamp"] = col;
            else if (!map.ContainsKey("patient_class") && headerLower.Contains("patient") && headerLower.Contains("class"))
                map["patient_class"] = col;
        }

        Log.Debug("Mapped columns: {Columns}", string.Join(", ", map.Select(kvp => $"{kvp.Key}={kvp.Value}")));
        return map;
    }

    /// <summary>
    /// Read a single row into an ExcelRecord.
    /// </summary>
    private ExcelRecord? ReadRow(IXLWorksheet worksheet, int row, Dictionary<string, int> columnMap)
    {
        var accession = columnMap.ContainsKey("accession")
            ? worksheet.Cell(row, columnMap["accession"]).GetString().Trim()
            : "";

        if (string.IsNullOrEmpty(accession))
            return null;

        var record = new ExcelRecord
        {
            Accession = accession,
            Procedure = columnMap.ContainsKey("procedure")
                ? worksheet.Cell(row, columnMap["procedure"]).GetString().Trim()
                : "",
            Rvu = columnMap.ContainsKey("rvu")
                ? ParseDouble(worksheet.Cell(row, columnMap["rvu"]).GetString())
                : 0,
            PatientClass = columnMap.ContainsKey("patient_class")
                ? worksheet.Cell(row, columnMap["patient_class"]).GetString().Trim()
                : "Unknown"
        };

        // Parse timestamp
        if (columnMap.ContainsKey("timestamp"))
        {
            var cell = worksheet.Cell(row, columnMap["timestamp"]);
            if (cell.DataType == XLDataType.DateTime)
            {
                record.Timestamp = cell.GetDateTime();
            }
            else if (DateTime.TryParse(cell.GetString(), out var dt))
            {
                record.Timestamp = dt;
            }
        }

        return record;
    }

    private double ParseDouble(string value)
    {
        if (double.TryParse(value, out var result))
            return result;
        return 0;
    }

    /// <summary>
    /// Generate audit report text.
    /// </summary>
    public string GenerateReportText(ExcelAuditResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== EXCEL AUDIT REPORT ===");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Excel File: {Path.GetFileName(result.ExcelPath)}");
        sb.AppendLine($"Date Range: {result.StartDate:yyyy-MM-dd} to {result.EndDate:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("─────────────────────────────────");
        sb.AppendLine("TOTALS");
        sb.AppendLine("─────────────────────────────────");
        sb.AppendLine($"Excel Records (total): {result.TotalExcel}");
        sb.AppendLine($"Excel Records (in range): {result.TotalExcelInRange}");
        sb.AppendLine($"Database Records: {result.TotalDb}");
        sb.AppendLine();
        sb.AppendLine("─────────────────────────────────");
        sb.AppendLine("COMPARISON");
        sb.AppendLine("─────────────────────────────────");
        sb.AppendLine($"Matched: {result.Matched}");
        sb.AppendLine($"Missing from DB: {result.MissingFromDb}");
        sb.AppendLine($"Extra in DB: {result.ExtraInDb}");
        sb.AppendLine();
        sb.AppendLine($"Excel RVU: {result.TotalExcelRvu:F1}");
        sb.AppendLine($"Database RVU: {result.TotalDbRvu:F1}");
        sb.AppendLine($"Difference: {result.RvuDifference:F1}");

        if (result.MissingFromDbDetails.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("─────────────────────────────────");
            sb.AppendLine($"MISSING FROM DATABASE ({result.MissingFromDbDetails.Count})");
            sb.AppendLine("─────────────────────────────────");
            foreach (var record in result.MissingFromDbDetails.Take(20))
            {
                sb.AppendLine($"  {record.Timestamp:MM/dd HH:mm} - {record.Accession} - {record.Procedure}");
            }
            if (result.MissingFromDbDetails.Count > 20)
                sb.AppendLine($"  ... and {result.MissingFromDbDetails.Count - 20} more");
        }

        if (result.ExtraInDbDetails.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("─────────────────────────────────");
            sb.AppendLine($"EXTRA IN DATABASE ({result.ExtraInDbDetails.Count})");
            sb.AppendLine("─────────────────────────────────");
            foreach (var record in result.ExtraInDbDetails.Take(20))
            {
                sb.AppendLine($"  {record.Timestamp:MM/dd HH:mm} - {record.Accession} - {record.Procedure}");
            }
            if (result.ExtraInDbDetails.Count > 20)
                sb.AppendLine($"  ... and {result.ExtraInDbDetails.Count - 20} more");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reconcile database with Excel audit results (Python parity: reconcile_database).
    /// Deletes extra records from DB, adds missing records from Excel, creates shifts for orphans.
    /// </summary>
    public async Task<ReconcileResult> ReconcileDatabaseAsync(
        ExcelAuditResult auditResult,
        IProgress<(string Status, int Percent)>? progress = null)
    {
        var result = new ReconcileResult();

        var hasChanges = auditResult.MissingFromDb > 0 || auditResult.ExtraInDb > 0;
        var hasMatchedToSync = auditResult.MatchedRecords.Count > 0;

        if (!auditResult.Success || (!hasChanges && !hasMatchedToSync))
        {
            result.Success = true;
            result.Message = "Nothing to reconcile";
            return result;
        }

        try
        {
            await Task.Run(() =>
            {
                progress?.Report(("Deleting extra records...", 10));

                // 1. Delete extra records (in DB but not in Excel)
                if (auditResult.ExtraInDbDetails.Count > 0)
                {
                    var extraAccessions = auditResult.ExtraInDbDetails.Select(r => r.Accession).ToList();
                    result.RecordsDeleted = _dataManager.Database.DeleteRecordsByAccessions(
                        extraAccessions, auditResult.StartDate, auditResult.EndDate);
                }

                progress?.Report(("Adding missing records...", 30));

                // 2. Add missing records (in Excel but not in DB)
                if (auditResult.MissingFromDbDetails.Count > 0)
                {
                    // Get average durations for estimating study times
                    var avgDurations = _dataManager.Database.GetAverageDurations(
                        auditResult.StartDate, auditResult.EndDate);

                    double GetDuration(string studyType) =>
                        avgDurations.TryGetValue(studyType, out var d) ? d : 120.0;

                    // Sort missing records by timestamp
                    var missingRecords = auditResult.MissingFromDbDetails
                        .OrderBy(r => r.Timestamp)
                        .ToList();

                    // Get existing shifts in the date range
                    var existingShifts = _dataManager.Database.GetShiftsInDateRange(
                        auditResult.StartDate, auditResult.EndDate);

                    // Cluster missing records into groups (9 hour threshold)
                    var clusters = ClusterRecords(missingRecords, TimeSpan.FromHours(9));

                    progress?.Report(("Creating shifts for orphan studies...", 50));

                    foreach (var cluster in clusters)
                    {
                        var clusterStart = cluster.First().Timestamp;
                        var clusterEnd = cluster.Last().Timestamp;

                        // Try to find an existing shift that contains this cluster
                        int? targetShiftId = null;
                        foreach (var shift in existingShifts)
                        {
                            var shiftEnd = shift.ShiftEnd ?? shift.ShiftStart.AddHours(9);
                            // Check if cluster overlaps or is within 30 minutes of shift
                            if (clusterStart >= shift.ShiftStart.AddMinutes(-30) &&
                                clusterEnd <= shiftEnd.AddMinutes(30))
                            {
                                targetShiftId = shift.Id;
                                break;
                            }
                        }

                        // Create records for this cluster
                        var studyRecords = cluster.Select(excelRec =>
                        {
                            var (studyType, rvu) = StudyMatcher.MatchStudyType(
                                excelRec.Procedure,
                                _dataManager.RvuTable,
                                _dataManager.ClassificationRules);

                            var duration = GetDuration(studyType);
                            var finishTime = excelRec.Timestamp;
                            var startTime = finishTime.AddSeconds(-duration);

                            return new StudyRecord
                            {
                                Accession = _dataManager.HashAccession(excelRec.Accession),
                                Procedure = excelRec.Procedure,
                                StudyType = studyType,
                                Rvu = rvu > 0 ? rvu : excelRec.Rvu,
                                Timestamp = startTime,
                                TimeFinished = finishTime,
                                DurationSeconds = duration,
                                PatientClass = excelRec.PatientClass,
                                Source = "PayrollReconcile"
                            };
                        }).ToList();

                        if (targetShiftId.HasValue)
                        {
                            // Add to existing shift
                            _dataManager.Database.BatchAddRecords(targetShiftId.Value, studyRecords);
                        }
                        else
                        {
                            // Create new historical shift
                            var shiftStart = clusterStart.AddMinutes(-10);
                            var shiftEnd = clusterEnd.AddMinutes(10);
                            var shiftName = $"{clusterStart:yyyy-MM-dd} (reconciled)";

                            _dataManager.Database.InsertHistoricalShift(
                                shiftStart, shiftEnd, studyRecords, shiftName);
                            result.ShiftsCreated++;
                        }

                        result.RecordsAdded += studyRecords.Count;
                    }
                }

                progress?.Report(("Syncing payroll data for matched records...", 70));

                // 3. Sync time_finished and RVU for matched records from Excel
                if (auditResult.MatchedRecords.Count > 0)
                {
                    var updates = auditResult.MatchedRecords
                        .Select(kvp => (kvp.Key, kvp.Value.Timestamp, kvp.Value.Rvu))
                        .ToList();
                    result.RecordsUpdated = _dataManager.Database.BatchUpdatePayrollData(updates);
                }

                progress?.Report(("Cleaning up empty shifts...", 90));

                // 4. Delete empty shifts in the date range
                _dataManager.Database.DeleteEmptyShifts(auditResult.StartDate, auditResult.EndDate);

                progress?.Report(("Reconciliation complete", 100));
            });

            result.Success = true;
            result.Message = $"Deleted {result.RecordsDeleted}, Added {result.RecordsAdded}, Updated {result.RecordsUpdated}, Created {result.ShiftsCreated} shifts";
            Log.Information("Reconciliation complete: {Message}", result.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reconciliation failed");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Cluster records by time gap threshold.
    /// </summary>
    private List<List<ExcelRecord>> ClusterRecords(List<ExcelRecord> records, TimeSpan maxGap)
    {
        var clusters = new List<List<ExcelRecord>>();
        if (records.Count == 0) return clusters;

        var currentCluster = new List<ExcelRecord> { records[0] };

        for (int i = 1; i < records.Count; i++)
        {
            var gap = records[i].Timestamp - records[i - 1].Timestamp;
            if (gap <= maxGap)
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
}

#region Models

/// <summary>
/// Result of RVU comparison check (Python parity: check_file).
/// </summary>
public class ExcelRvuCheckResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string FileName { get; set; } = "";
    public int TotalProcessed { get; set; }
    public List<RvuOutlier> Outliers { get; set; } = new();
}

/// <summary>
/// RVU outlier found during comparison.
/// </summary>
public class RvuOutlier
{
    public string Procedure { get; set; } = "";
    public double ExcelRvu { get; set; }
    public string MatchedType { get; set; } = "";
    public double MatchedRvu { get; set; }
    public int Row { get; set; }
}

/// <summary>
/// Result of accession comparison check (Python parity: check_accessions).
/// </summary>
public class ExcelAccessionCheckResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string FileName { get; set; } = "";
    public string TargetMonth { get; set; } = "";
    public int TotalChecked { get; set; }
    public List<string> MissingFromDb { get; set; } = new();
    public List<string> ExtraInDb { get; set; } = new();
}

/// <summary>
/// Record from Excel file.
/// </summary>
public class ExcelRecord
{
    public string Accession { get; set; } = "";
    public string Procedure { get; set; } = "";
    public double Rvu { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string PatientClass { get; set; } = "Unknown";
}

/// <summary>
/// Result of Excel audit.
/// </summary>
public class ExcelAuditResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string ExcelPath { get; set; } = "";
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public int TotalExcel { get; set; }
    public int TotalExcelInRange { get; set; }
    public int TotalDb { get; set; }
    public int Matched { get; set; }
    public int MissingFromDb { get; set; }
    public int ExtraInDb { get; set; }

    public double TotalExcelRvu { get; set; }
    public double TotalDbRvu { get; set; }
    public double RvuDifference { get; set; }

    /// <summary>
    /// Matched records: hashed accession -> Excel record (for payroll sync of timestamps/RVUs).
    /// </summary>
    public Dictionary<string, ExcelRecord> MatchedRecords { get; set; } = new();

    public List<ExcelRecord> MissingFromDbDetails { get; set; } = new();
    public List<StudyRecord> ExtraInDbDetails { get; set; } = new();

    public double MatchPercentage => TotalExcelInRange > 0
        ? (100.0 * Matched / TotalExcelInRange)
        : 0;
}

/// <summary>
/// Result of database reconciliation.
/// </summary>
public class ReconcileResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string Message { get; set; } = "";
    public int RecordsDeleted { get; set; }
    public int RecordsAdded { get; set; }
    public int RecordsUpdated { get; set; }
    public int ShiftsCreated { get; set; }
}

#endregion
