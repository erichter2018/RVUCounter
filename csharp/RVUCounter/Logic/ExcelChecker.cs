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
    /// </summary>
    public ExcelRvuCheckResult CheckFile(string filePath, IProgress<(int Current, int Total)>? progress = null)
    {
        var result = new ExcelRvuCheckResult { FileName = Path.GetFileName(filePath) };

        try
        {
            using var workbook = new XLWorkbook(filePath);
            var sheet = workbook.Worksheets.FirstOrDefault();
            if (sheet == null)
            {
                result.ErrorMessage = "No worksheets found";
                return result;
            }

            // Find columns by exact name (Python parity)
            int? procCol = null, rvuCol = null;
            var headers = new List<string>();

            for (int col = 1; col <= sheet.LastColumnUsed()?.ColumnNumber(); col++)
            {
                var header = sheet.Cell(1, col).GetString();
                headers.Add(header);
                if (header == "StandardProcedureName") procCol = col;
                else if (header == "wRVU_Matrix") rvuCol = col;
            }

            if (procCol == null || rvuCol == null)
            {
                result.ErrorMessage = "Missing required columns: StandardProcedureName, wRVU_Matrix";
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
        var result = new ExcelAuditResult
        {
            ExcelPath = excelPath,
            StartDate = startDate ?? DateTime.Today.AddDays(-30),
            EndDate = endDate ?? DateTime.Today
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

            // Compare accessions
            var excelAccessions = excelRecords
                .Select(r => NormalizeAccession(r.Accession))
                .Where(a => !string.IsNullOrEmpty(a))
                .ToHashSet();

            var dbAccessions = dbRecords
                .Select(r => NormalizeAccession(r.Accession))
                .Where(a => !string.IsNullOrEmpty(a))
                .ToHashSet();

            // Find discrepancies
            var missingFromDb = excelAccessions.Except(dbAccessions).ToList();
            var extraInDb = dbAccessions.Except(excelAccessions).ToList();
            var matched = excelAccessions.Intersect(dbAccessions).ToList();

            result.Matched = matched.Count;
            result.MissingFromDb = missingFromDb.Count;
            result.ExtraInDb = extraInDb.Count;

            // Get details for missing records
            result.MissingFromDbDetails = excelRecords
                .Where(r => missingFromDb.Contains(NormalizeAccession(r.Accession)))
                .OrderBy(r => r.Timestamp)
                .ToList();

            // Get details for extra records
            result.ExtraInDbDetails = dbRecords
                .Where(r => extraInDb.Contains(NormalizeAccession(r.Accession)))
                .OrderBy(r => r.Timestamp)
                .ToList();

            // Calculate RVU comparison
            result.TotalExcelRvu = excelRecords.Sum(r => r.Rvu);
            result.TotalDbRvu = dbRecords.Sum(r => r.Rvu);
            result.RvuDifference = result.TotalDbRvu - result.TotalExcelRvu;

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
        var worksheet = workbook.Worksheets.FirstOrDefault();
        if (worksheet == null)
        {
            Log.Warning("No worksheets found in Excel file");
            return records;
        }

        // Find header row and column mapping
        var headerRow = FindHeaderRow(worksheet);
        if (headerRow == null)
        {
            Log.Warning("Could not find header row in Excel file");
            return records;
        }

        var columnMap = MapColumns(worksheet, headerRow.Value);
        if (!columnMap.ContainsKey("accession"))
        {
            Log.Warning("Could not find accession column in Excel file");
            return records;
        }

        // Read data rows
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow.Value;
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
    /// </summary>
    private int? FindHeaderRow(IXLWorksheet worksheet)
    {
        var commonHeaders = new[] { "accession", "procedure", "study", "rvu", "date", "time" };

        for (int row = 1; row <= Math.Min(20, worksheet.LastRowUsed()?.RowNumber() ?? 1); row++)
        {
            var cellValues = new List<string>();
            for (int col = 1; col <= Math.Min(20, worksheet.LastColumnUsed()?.ColumnNumber() ?? 1); col++)
            {
                var value = worksheet.Cell(row, col).GetString().ToLowerInvariant();
                cellValues.Add(value);
            }

            var matchCount = commonHeaders.Count(h => cellValues.Any(v => v.Contains(h)));
            if (matchCount >= 2)
            {
                Log.Debug("Found header row at row {Row}", row);
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// Map column names to column numbers.
    /// </summary>
    private Dictionary<string, int> MapColumns(IXLWorksheet worksheet, int headerRow)
    {
        var map = new Dictionary<string, int>();
        var lastCol = worksheet.LastColumnUsed()?.ColumnNumber() ?? 1;

        for (int col = 1; col <= lastCol; col++)
        {
            var header = worksheet.Cell(headerRow, col).GetString().ToLowerInvariant().Trim();

            if (header.Contains("accession"))
                map["accession"] = col;
            else if (header.Contains("procedure") || header.Contains("study") || header.Contains("description"))
                map["procedure"] = col;
            else if (header.Contains("rvu") && !header.Contains("total"))
                map["rvu"] = col;
            else if (header.Contains("date") || header.Contains("time") || header.Contains("performed"))
                map["timestamp"] = col;
            else if (header.Contains("patient") && header.Contains("class"))
                map["patient_class"] = col;
        }

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

    private string NormalizeAccession(string accession)
    {
        if (string.IsNullOrEmpty(accession))
            return "";

        // Remove common prefixes and normalize
        return accession
            .Replace("-", "")
            .Replace(" ", "")
            .ToUpperInvariant()
            .Trim();
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

        sb.AppendLine("SUMMARY:");
        sb.AppendLine($"  Total Excel Records: {result.TotalExcelInRange}");
        sb.AppendLine($"  Total Database Records: {result.TotalDb}");
        sb.AppendLine($"  Matched: {result.Matched}");
        sb.AppendLine($"  Missing from DB: {result.MissingFromDb}");
        sb.AppendLine($"  Extra in DB: {result.ExtraInDb}");
        sb.AppendLine();

        sb.AppendLine("RVU COMPARISON:");
        sb.AppendLine($"  Excel RVU: {result.TotalExcelRvu:F1}");
        sb.AppendLine($"  Database RVU: {result.TotalDbRvu:F1}");
        sb.AppendLine($"  Difference: {result.RvuDifference:F1}");
        sb.AppendLine();

        if (result.MissingFromDbDetails.Count > 0)
        {
            sb.AppendLine("MISSING FROM DATABASE:");
            foreach (var record in result.MissingFromDbDetails.Take(50))
            {
                sb.AppendLine($"  {record.Timestamp:MM/dd HH:mm} - {record.Accession} - {record.Procedure}");
            }
            if (result.MissingFromDbDetails.Count > 50)
            {
                sb.AppendLine($"  ... and {result.MissingFromDbDetails.Count - 50} more");
            }
            sb.AppendLine();
        }

        if (result.ExtraInDbDetails.Count > 0)
        {
            sb.AppendLine("EXTRA IN DATABASE:");
            foreach (var record in result.ExtraInDbDetails.Take(50))
            {
                sb.AppendLine($"  {record.Timestamp:MM/dd HH:mm} - {record.Accession} - {record.Procedure}");
            }
            if (result.ExtraInDbDetails.Count > 50)
            {
                sb.AppendLine($"  ... and {result.ExtraInDbDetails.Count - 50} more");
            }
        }

        return sb.ToString();
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

    public List<ExcelRecord> MissingFromDbDetails { get; set; } = new();
    public List<StudyRecord> ExtraInDbDetails { get; set; } = new();

    public double MatchPercentage => TotalExcelInRange > 0
        ? (100.0 * Matched / TotalExcelInRange)
        : 0;
}

#endregion
