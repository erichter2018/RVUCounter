using RVUCounter.Data;
using Serilog;

namespace RVUCounter.Logic;

/// <summary>
/// Fixes discrepancies in the SQLite database based on current RVU rules.
/// Ported from Python database_repair.py.
/// </summary>
public class DatabaseRepair
{
    private readonly DataManager _dataManager;

    public DatabaseRepair(DataManager dataManager)
    {
        _dataManager = dataManager;
    }

    /// <summary>
    /// Scan all records in the database and find ones that don't match current rules.
    /// </summary>
    public List<RecordMismatch> FindMismatches(IProgress<(int Current, int Total)>? progress = null)
    {
        var mismatches = new List<RecordMismatch>();

        try
        {
            var allRecords = _dataManager.Database.GetAllRecords();
            var total = allRecords.Count;

            for (int i = 0; i < allRecords.Count; i++)
            {
                var record = allRecords[i];
                progress?.Report((i + 1, total));

                var (newType, newRvu) = StudyMatcher.MatchStudyType(
                    record.Procedure,
                    _dataManager.RvuTable,
                    _dataManager.ClassificationRules);

                // Check for mismatch (with small epsilon for float comparison)
                if (newType != record.StudyType || Math.Abs(record.Rvu - newRvu) > 0.01)
                {
                    mismatches.Add(new RecordMismatch
                    {
                        Id = record.Id,
                        Procedure = record.Procedure,
                        OldType = record.StudyType,
                        OldRvu = record.Rvu,
                        NewType = newType,
                        NewRvu = newRvu
                    });
                }
            }

            Log.Information("Found {Count} mismatches out of {Total} records", mismatches.Count, total);
            return mismatches;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error finding mismatches");
            return mismatches;
        }
    }

    /// <summary>
    /// Update records in the database to match current rules.
    /// Uses batch update for efficiency (Python parity).
    /// </summary>
    public int FixMismatches(List<RecordMismatch> mismatches, IProgress<(int Current, int Total)>? progress = null)
    {
        var count = 0;

        try
        {
            var total = mismatches.Count;

            // Batch update using direct SQL for efficiency (like Python)
            count = _dataManager.Database.BatchUpdateStudyTypes(
                mismatches.Select(m => (m.Id, m.NewType, m.NewRvu)).ToList(),
                (current, tot) => progress?.Report((current, tot)));

            Log.Information("Database repair complete: updated {Count} records", count);
            return count;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error fixing mismatches");
            return count;
        }
    }

    /// <summary>
    /// Find and fix all mismatches in one operation.
    /// </summary>
    public async Task<RepairResult> RepairDatabaseAsync(IProgress<(string Status, double Percent)>? progress = null)
    {
        var result = new RepairResult();

        await Task.Run(() =>
        {
            try
            {
                // Phase 1: Find mismatches
                progress?.Report(("Scanning database...", 0.0));

                var findProgress = new Progress<(int Current, int Total)>(p =>
                {
                    var pct = 0.5 * p.Current / Math.Max(1, p.Total);
                    progress?.Report(($"Scanning: {p.Current}/{p.Total}", pct));
                });

                var mismatches = FindMismatches(findProgress);
                result.MismatchesFound = mismatches.Count;

                if (mismatches.Count == 0)
                {
                    progress?.Report(("No mismatches found. Fixing shift end times...", 0.5));
                    result.ShiftEndsFixed = FixShiftEndTimes();
                    result.Success = true;
                    var msg = result.ShiftEndsFixed > 0
                        ? $"No mismatches. Fixed {result.ShiftEndsFixed} shift end times."
                        : "No mismatches found. All shift end times OK.";
                    progress?.Report((msg, 1.0));
                    return;
                }

                // Phase 2: Fix mismatches
                progress?.Report(($"Fixing {mismatches.Count} records...", 0.35));

                var fixProgress = new Progress<(int Current, int Total)>(p =>
                {
                    var pct = 0.35 + 0.35 * p.Current / Math.Max(1, p.Total);
                    progress?.Report(($"Fixing: {p.Current}/{p.Total}", pct));
                });

                result.RecordsFixed = FixMismatches(mismatches, fixProgress);

                // Phase 3: Fix shift end times to be 1 minute after last study
                progress?.Report(("Fixing shift end times...", 0.7));
                result.ShiftEndsFixed = FixShiftEndTimes();

                result.Success = true;

                var summary = $"Fixed {result.RecordsFixed} records, {result.ShiftEndsFixed} shift end times";
                progress?.Report((summary, 1.0));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Database repair failed");
                result.ErrorMessage = ex.Message;
            }
        });

        return result;
    }

    /// <summary>
    /// Fix shift end times: set each completed shift's end to 1 minute after its last study.
    /// Skips the current (active) shift.
    /// </summary>
    public int FixShiftEndTimes()
    {
        var fixCount = 0;
        try
        {
            var db = _dataManager.Database;
            var shifts = db.GetAllShifts()
                .Where(s => s.ShiftEnd.HasValue)
                .ToList();

            foreach (var shift in shifts)
            {
                var records = db.GetRecordsForShift(shift.Id);
                if (records.Count == 0) continue;

                var lastRecordTime = records.Max(r => r.Timestamp);
                var correctEnd = lastRecordTime.AddMinutes(1);

                // Only fix if the difference is more than 2 minutes
                var diff = Math.Abs((shift.ShiftEnd!.Value - correctEnd).TotalMinutes);
                if (diff > 2)
                {
                    Log.Information("Fixing shift {Id} end time: {Old} -> {New} (last study at {Last})",
                        shift.Id, shift.ShiftEnd.Value, correctEnd, lastRecordTime);
                    db.UpdateShiftEnd(shift.Id, correctEnd);
                    fixCount++;
                }
            }

            if (fixCount > 0)
                Log.Information("Fixed {Count} shift end times", fixCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error fixing shift end times");
        }
        return fixCount;
    }
}

/// <summary>
/// Represents a mismatch between stored record and current rules.
/// </summary>
public class RecordMismatch
{
    public int Id { get; set; }
    public string Procedure { get; set; } = "";
    public string OldType { get; set; } = "";
    public double OldRvu { get; set; }
    public string NewType { get; set; } = "";
    public double NewRvu { get; set; }

    public override string ToString() =>
        $"{Procedure}: {OldType} ({OldRvu:F2}) -> {NewType} ({NewRvu:F2})";
}

/// <summary>
/// Result of database repair operation.
/// </summary>
public class RepairResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int MismatchesFound { get; set; }
    public int RecordsFixed { get; set; }
    public int ShiftEndsFixed { get; set; }
}
