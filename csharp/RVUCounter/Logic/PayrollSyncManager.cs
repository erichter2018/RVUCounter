using System.IO;
using ClosedXML.Excel;
using RVUCounter.Data;
using RVUCounter.Models;
using Serilog;

namespace RVUCounter.Logic;

/// <summary>
/// Manages time synchronization between database and payroll Excel files.
/// Ported from Python payroll_sync.py.
/// </summary>
public class PayrollSyncManager
{
    private readonly DataManager _dataManager;

    public PayrollSyncManager(DataManager dataManager)
    {
        _dataManager = dataManager;
    }

    /// <summary>
    /// Calculate time offset between database and Excel timestamps.
    /// Python parity: Uses ExamAccession and ExamFinalReportDT_CT columns.
    /// </summary>
    public PayrollSyncResult CalculateOffset(string excelPath)
    {
        var result = new PayrollSyncResult();

        if (!File.Exists(excelPath))
        {
            result.ErrorMessage = "Excel file not found";
            return result;
        }

        try
        {
            Log.Information("Reading Excel file for sync: {Path}", excelPath);

            using var workbook = new XLWorkbook(excelPath);
            var sheet = workbook.Worksheets.FirstOrDefault();
            if (sheet == null)
            {
                result.ErrorMessage = "No worksheets found in Excel file";
                return result;
            }

            // Find columns by exact name (Python parity)
            var headerRow = sheet.Row(1);
            int? colAcc = null, colFinal = null;

            for (int col = 1; col <= sheet.LastColumnUsed()?.ColumnNumber(); col++)
            {
                var header = sheet.Cell(1, col).GetString();
                if (header == "ExamAccession") colAcc = col;
                else if (header == "ExamFinalReportDT_CT") colFinal = col;
            }

            if (colAcc == null || colFinal == null)
            {
                result.ErrorMessage = "Missing required columns (ExamAccession, ExamFinalReportDT_CT)";
                return result;
            }

            // Read Excel data
            var excelData = new Dictionary<string, DateTime>();
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

            for (int row = 2; row <= lastRow; row++)
            {
                var acc = sheet.Cell(row, colAcc.Value).GetString()?.Trim();
                var dtCell = sheet.Cell(row, colFinal.Value);

                if (!string.IsNullOrEmpty(acc))
                {
                    DateTime? dt = null;
                    if (dtCell.DataType == XLDataType.DateTime)
                        dt = dtCell.GetDateTime();
                    else if (DateTime.TryParse(dtCell.GetString(), out var parsed))
                        dt = parsed;

                    if (dt.HasValue)
                        excelData[acc] = dt.Value;
                }
            }

            if (excelData.Count == 0)
            {
                result.ErrorMessage = "No valid data found in Excel file";
                return result;
            }

            // Get all DB records for comparison
            var allRecords = _dataManager.Database.GetAllRecords();

            // Calculate differences
            var rawDiffs = new List<double>();

            foreach (var record in allRecords)
            {
                var acc = record.Accession?.Trim() ?? "";
                if (excelData.TryGetValue(acc, out var excelDt))
                {
                    var dbDt = record.TimeFinished ?? record.Timestamp;
                    // Diff = Excel - DB (Python parity)
                    var diff = (excelDt - dbDt).TotalSeconds;
                    rawDiffs.Add(diff);
                }
            }

            if (rawDiffs.Count == 0)
            {
                result.ErrorMessage = "No matching accessions found between Database and Excel";
                return result;
            }

            // Calculate statistics (Python parity)
            rawDiffs.Sort();
            var medianDiff = rawDiffs[rawDiffs.Count / 2];
            var meanDiff = rawDiffs.Average();

            // Round offset to nearest 0.5 hours (Python parity)
            var offsetHours = Math.Round(medianDiff / 1800.0) / 2.0;

            // Check consistency - within 5 minutes of median (Python parity)
            var closeMatches = rawDiffs.Count(d => Math.Abs(d - medianDiff) < 300);
            var consistency = (double)closeMatches / rawDiffs.Count;

            // Determine confidence (Python parity)
            string confidence = "Low";
            if (rawDiffs.Count > 10 && consistency > 0.8)
                confidence = "High";
            else if (rawDiffs.Count > 5 && consistency > 0.5)
                confidence = "Medium";

            result.Success = true;
            result.OffsetHours = offsetHours;
            result.Offset = TimeSpan.FromHours(offsetHours);
            result.OffsetSeconds = (int)(offsetHours * 3600);
            result.Confidence = consistency;
            result.ConfidenceLevel = confidence;
            result.MatchCount = rawDiffs.Count;
            result.MedianDiffSeconds = medianDiff;
            result.MeanDiffSeconds = meanDiff;
            result.SampleExcelTime = excelData.Values.FirstOrDefault();

            Log.Information("Detected time offset: {Hours:F1} hours, confidence: {Confidence} ({Matches} matches)",
                offsetHours, confidence, rawDiffs.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Time offset calculation failed");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Detect time offset between database and Excel timestamps (legacy method).
    /// </summary>
    public PayrollSyncResult DetectTimeOffset(
        string excelPath,
        DateTime? startDate = null,
        DateTime? endDate = null)
    {
        // Use the new Python-parity method
        return CalculateOffset(excelPath);
    }

    /// <summary>
    /// Apply time offset to database records.
    /// </summary>
    public async Task<(bool Success, int UpdatedCount, string Message)> ApplyOffsetAsync(
        int shiftId,
        TimeSpan offset)
    {
        try
        {
            var count = await Task.Run(() =>
                _dataManager.Database.BatchUpdateTimestampsByOffset(shiftId, offset));

            Log.Information("Applied offset {Offset} to {Count} records", offset, count);
            return (true, count, $"Updated {count} records by {offset.TotalHours:F1} hours");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply time offset");
            return (false, 0, $"Failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Read timestamps from Excel file.
    /// </summary>
    private List<(string Accession, DateTime Timestamp)> ReadExcelTimestamps(
        string path,
        DateTime startDate,
        DateTime endDate)
    {
        var results = new List<(string Accession, DateTime Timestamp)>();

        using var workbook = new XLWorkbook(path);
        var worksheet = workbook.Worksheets.FirstOrDefault();
        if (worksheet == null)
        {
            Log.Warning("No worksheets found in Excel file");
            return results;
        }

        // Find header row
        var headerRow = FindHeaderRow(worksheet);
        if (headerRow == null)
        {
            Log.Warning("Could not find header row");
            return results;
        }

        // Find columns
        var accessionCol = FindColumn(worksheet, headerRow.Value, "accession");
        var timestampCol = FindColumn(worksheet, headerRow.Value, "date", "time", "performed");

        if (accessionCol == null || timestampCol == null)
        {
            Log.Warning("Could not find required columns");
            return results;
        }

        // Read data
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow.Value;

        for (int row = headerRow.Value + 1; row <= lastRow; row++)
        {
            try
            {
                var accession = worksheet.Cell(row, accessionCol.Value).GetString().Trim();
                if (string.IsNullOrEmpty(accession))
                    continue;

                DateTime? timestamp = null;
                var cell = worksheet.Cell(row, timestampCol.Value);

                if (cell.DataType == XLDataType.DateTime)
                {
                    timestamp = cell.GetDateTime();
                }
                else if (DateTime.TryParse(cell.GetString(), out var dt))
                {
                    timestamp = dt;
                }

                if (timestamp.HasValue &&
                    timestamp.Value >= startDate &&
                    timestamp.Value <= endDate)
                {
                    results.Add((accession, timestamp.Value));
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error reading row {Row}", row);
            }
        }

        return results;
    }

    private int? FindHeaderRow(IXLWorksheet worksheet)
    {
        var commonHeaders = new[] { "accession", "procedure", "date", "time" };

        for (int row = 1; row <= Math.Min(20, worksheet.LastRowUsed()?.RowNumber() ?? 1); row++)
        {
            var rowValues = new List<string>();
            for (int col = 1; col <= Math.Min(20, worksheet.LastColumnUsed()?.ColumnNumber() ?? 1); col++)
            {
                rowValues.Add(worksheet.Cell(row, col).GetString().ToLowerInvariant());
            }

            var matches = commonHeaders.Count(h => rowValues.Any(v => v.Contains(h)));
            if (matches >= 2)
                return row;
        }

        return null;
    }

    private int? FindColumn(IXLWorksheet worksheet, int headerRow, params string[] keywords)
    {
        var lastCol = worksheet.LastColumnUsed()?.ColumnNumber() ?? 1;

        for (int col = 1; col <= lastCol; col++)
        {
            var header = worksheet.Cell(headerRow, col).GetString().ToLowerInvariant();
            if (keywords.Any(k => header.Contains(k)))
                return col;
        }

        return null;
    }

    private string NormalizeAccession(string accession)
    {
        if (string.IsNullOrEmpty(accession))
            return "";

        return accession
            .Replace("-", "")
            .Replace(" ", "")
            .ToUpperInvariant()
            .Trim();
    }

    /// <summary>
    /// Generate sync report.
    /// </summary>
    public string GenerateReport(PayrollSyncResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== PAYROLL SYNC ANALYSIS ===");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        if (!result.Success)
        {
            sb.AppendLine($"Error: {result.ErrorMessage}");
            return sb.ToString();
        }

        sb.AppendLine($"Matched Records: {result.MatchCount}");
        sb.AppendLine($"Confidence: {result.Confidence:P0}");
        sb.AppendLine();

        sb.AppendLine("DETECTED OFFSET:");
        sb.AppendLine($"  {result.Offset}");
        sb.AppendLine($"  {result.OffsetHours:F2} hours");
        sb.AppendLine($"  {result.OffsetSeconds} seconds");
        sb.AppendLine();

        if (result.Confidence < 0.8)
        {
            sb.AppendLine("WARNING: Low confidence. Review records manually before applying.");
        }
        else if (Math.Abs(result.OffsetHours) < 0.05)
        {
            sb.AppendLine("No significant offset detected.");
        }
        else
        {
            sb.AppendLine($"Recommendation: Apply offset of {result.OffsetHours:F1} hours to align timestamps.");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Result of payroll sync analysis.
/// Python parity: includes all fields from Python version.
/// </summary>
public class PayrollSyncResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public TimeSpan Offset { get; set; }
    public int OffsetSeconds { get; set; }
    public double OffsetHours { get; set; }
    public double Confidence { get; set; }
    public string ConfidenceLevel { get; set; } = "Low";
    public int MatchCount { get; set; }
    public double StandardDeviation { get; set; }
    public double MedianDiffSeconds { get; set; }
    public double MeanDiffSeconds { get; set; }
    public DateTime? SampleExcelTime { get; set; }

    public string OffsetDisplay => OffsetHours switch
    {
        0 => "No offset",
        > 0 => $"+{OffsetHours:F1} hours",
        _ => $"{OffsetHours:F1} hours"
    };
}
