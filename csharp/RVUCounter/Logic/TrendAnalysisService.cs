using RVUCounter.Data;
using RVUCounter.Models;
using Serilog;

namespace RVUCounter.Logic;

/// <summary>
/// Service for analyzing RVU trends over time.
/// Shift-based: each data point is one shift (not one day).
/// </summary>
public class TrendAnalysisService
{
    private readonly RecordsDatabase _database;

    public TrendAnalysisService(RecordsDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Analyzes trends for the specified date range.
    /// </summary>
    /// <param name="startDate">Start of range</param>
    /// <param name="endDate">End of range</param>
    /// <param name="metric">"rvu" for total RVU per shift, "rvuPerHour" for RVU/h per shift</param>
    /// <param name="minShiftHours">If > 0, exclude shifts shorter than this many hours</param>
    public TrendAnalysis AnalyzeTrends(DateTime startDate, DateTime endDate, string metric = "rvu", double minShiftHours = 0)
    {
        var analysis = new TrendAnalysis();

        try
        {
            // Get all completed shifts in range
            var shifts = _database.GetAllShifts()
                .Where(s => s.ShiftStart >= startDate && s.ShiftStart <= endDate && s.ShiftEnd.HasValue)
                .OrderBy(s => s.ShiftStart)
                .ToList();

            if (shifts.Count == 0)
            {
                analysis.InsightMessage = "No completed shift data available for this period.";
                return analysis;
            }

            // Deduplicate overlapping shifts (e.g., Python + C# tracked the same shift)
            // If two shifts start within 30 minutes, keep the one with more records
            var deduped = new List<Shift>();
            foreach (var shift in shifts)
            {
                var existing = deduped.LastOrDefault();
                if (existing != null && Math.Abs((shift.ShiftStart - existing.ShiftStart).TotalMinutes) < 30)
                {
                    var existingCount = _database.GetRecordsForShift(existing.Id).Count;
                    var newCount = _database.GetRecordsForShift(shift.Id).Count;
                    if (newCount > existingCount)
                    {
                        deduped[deduped.Count - 1] = shift;
                    }
                    // else keep existing
                }
                else
                {
                    deduped.Add(shift);
                }
            }
            shifts = deduped;

            // Build per-shift data
            var dataList = new List<TrendData>();

            foreach (var shift in shifts)
            {
                var shiftHours = (shift.ShiftEnd!.Value - shift.ShiftStart).TotalHours;

                // Filter short shifts if requested
                if (minShiftHours > 0 && shiftHours < minShiftHours)
                    continue;

                var records = _database.GetRecordsForShift(shift.Id);
                var rvu = records.Sum(r => r.Rvu);
                var studies = records.Count;
                var rvuPerHour = shiftHours > 0 ? rvu / shiftHours : 0;

                dataList.Add(new TrendData
                {
                    Date = shift.ShiftStart,
                    ShiftId = shift.Id,
                    ShiftName = shift.ShiftName,
                    TotalRvu = rvu,
                    StudyCount = studies,
                    TotalHours = shiftHours,
                    RvuPerHour = rvuPerHour
                });
            }

            if (dataList.Count == 0)
            {
                analysis.InsightMessage = "No shifts match the current filter criteria.";
                return analysis;
            }

            // Calculate rolling averages based on selected metric
            CalculateRollingAverages(dataList, metric);

            analysis.ShiftData = dataList;

            // Calculate summary stats
            analysis.TotalRvu = dataList.Sum(d => d.TotalRvu);
            analysis.TotalStudies = dataList.Sum(d => d.StudyCount);
            analysis.TotalShifts = dataList.Count;
            analysis.AvgRvuPerShift = dataList.Average(d => d.TotalRvu);

            var totalHours = dataList.Sum(d => d.TotalHours);
            analysis.AvgRvuPerHour = totalHours > 0 ? analysis.TotalRvu / totalHours : 0;

            var bestByRvu = dataList.OrderByDescending(d => d.TotalRvu).First();
            analysis.BestShiftRvu = bestByRvu.TotalRvu;
            analysis.BestShiftDate = bestByRvu.Date;

            var bestByRate = dataList.Where(d => d.TotalHours > 0).OrderByDescending(d => d.RvuPerHour).FirstOrDefault();
            analysis.BestShiftRvuPerHour = bestByRate?.RvuPerHour ?? 0;

            // Calculate period comparisons
            CalculatePeriodComparisons(analysis, dataList, metric);

            // Generate insight message
            analysis.InsightMessage = GenerateInsightMessage(analysis, metric);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error analyzing trends");
            analysis.InsightMessage = "Error analyzing trends.";
        }

        return analysis;
    }

    /// <summary>
    /// Calculates 7-shift and 30-shift rolling averages.
    /// </summary>
    private void CalculateRollingAverages(List<TrendData> data, string metric)
    {
        for (int i = 0; i < data.Count; i++)
        {
            var start7 = Math.Max(0, i - 6);
            var window7 = data.Skip(start7).Take(i - start7 + 1).ToList();

            var start30 = Math.Max(0, i - 29);
            var window30 = data.Skip(start30).Take(i - start30 + 1).ToList();

            if (metric == "rvuPerHour")
            {
                data[i].RollingAvg7 = window7.Count > 0 ? window7.Average(d => d.RvuPerHour) : 0;
                data[i].RollingAvg30 = window30.Count > 0 ? window30.Average(d => d.RvuPerHour) : 0;
            }
            else
            {
                data[i].RollingAvg7 = window7.Count > 0 ? window7.Average(d => d.TotalRvu) : 0;
                data[i].RollingAvg30 = window30.Count > 0 ? window30.Average(d => d.TotalRvu) : 0;
            }
        }
    }

    /// <summary>
    /// Calculates week-over-week and month-over-month changes.
    /// </summary>
    private void CalculatePeriodComparisons(TrendAnalysis analysis, List<TrendData> data, string metric)
    {
        if (data.Count < 7)
        {
            analysis.TrendDirection = "stable";
            return;
        }

        var now = DateTime.Now.Date;
        Func<TrendData, double> getValue = metric == "rvuPerHour" ? d => d.RvuPerHour : d => d.TotalRvu;

        // Week over week: compare shifts from last 7 days to previous 7 days
        var lastWeek = data.Where(d => d.Date.Date > now.AddDays(-7) && d.Date.Date <= now).ToList();
        var prevWeek = data.Where(d => d.Date.Date > now.AddDays(-14) && d.Date.Date <= now.AddDays(-7)).ToList();

        if (lastWeek.Count > 0 && prevWeek.Count > 0)
        {
            var lastWeekAvg = lastWeek.Average(getValue);
            var prevWeekAvg = prevWeek.Average(getValue);

            if (prevWeekAvg > 0)
                analysis.WeekOverWeekChange = ((lastWeekAvg - prevWeekAvg) / prevWeekAvg) * 100;
        }

        // Month over month
        var lastMonth = data.Where(d => d.Date.Date > now.AddDays(-30) && d.Date.Date <= now).ToList();
        var prevMonth = data.Where(d => d.Date.Date > now.AddDays(-60) && d.Date.Date <= now.AddDays(-30)).ToList();

        if (lastMonth.Count > 0 && prevMonth.Count > 0)
        {
            var lastMonthAvg = lastMonth.Average(getValue);
            var prevMonthAvg = prevMonth.Average(getValue);

            if (prevMonthAvg > 0)
                analysis.MonthOverMonthChange = ((lastMonthAvg - prevMonthAvg) / prevMonthAvg) * 100;
        }

        // Determine trend direction based on recent shifts
        if (data.Count >= 6)
        {
            var recent3 = data.TakeLast(3).ToList();
            var older3 = data.SkipLast(3).TakeLast(3).ToList();

            if (older3.Count >= 3)
            {
                var recentAvg = recent3.Average(getValue);
                var olderAvg = older3.Average(getValue);
                var changePercent = olderAvg > 0 ? ((recentAvg - olderAvg) / olderAvg) * 100 : 0;

                if (changePercent > 5)
                    analysis.TrendDirection = "up";
                else if (changePercent < -5)
                    analysis.TrendDirection = "down";
                else
                    analysis.TrendDirection = "stable";
            }
        }
    }

    /// <summary>
    /// Generates a natural language insight message.
    /// </summary>
    private string GenerateInsightMessage(TrendAnalysis analysis, string metric)
    {
        var messages = new List<string>();
        var metricLabel = metric == "rvuPerHour" ? "RVU/h" : "RVU";

        // Trend direction message
        if (analysis.TrendDirection == "up")
        {
            if (analysis.WeekOverWeekChange > 0)
                messages.Add($"Your {metricLabel} is up {analysis.WeekOverWeekChange:F0}% from last week!");
            else if (analysis.MonthOverMonthChange > 0)
                messages.Add($"Your {metricLabel} is up {analysis.MonthOverMonthChange:F0}% from last month.");
        }
        else if (analysis.TrendDirection == "down")
        {
            if (analysis.WeekOverWeekChange < 0)
                messages.Add($"Your {metricLabel} is down {Math.Abs(analysis.WeekOverWeekChange):F0}% from last week.");
            else if (analysis.MonthOverMonthChange < 0)
                messages.Add($"Your {metricLabel} is down {Math.Abs(analysis.MonthOverMonthChange):F0}% from last month.");
        }
        else
        {
            messages.Add($"Your {metricLabel} has been stable.");
        }

        // Best shift highlight
        if (analysis.BestShiftDate.HasValue && analysis.BestShiftRvu > 0)
        {
            var daysAgo = (DateTime.Now.Date - analysis.BestShiftDate.Value.Date).Days;
            if (daysAgo == 0)
                messages.Add($"Best shift today: {analysis.BestShiftRvu:F1} RVU!");
            else if (daysAgo <= 7)
                messages.Add($"Best shift this week: {analysis.BestShiftRvu:F1} RVU on {analysis.BestShiftDate.Value:ddd}.");
        }

        // Average
        messages.Add($"Avg per shift: {analysis.AvgRvuPerShift:F1} RVU ({analysis.AvgRvuPerHour:F1}/hr) over {analysis.TotalShifts} shifts.");

        return string.Join(" ", messages);
    }

    /// <summary>
    /// Gets trend arrow symbol based on direction.
    /// </summary>
    public static string GetTrendArrow(string direction)
    {
        return direction switch
        {
            "up" => "↑",
            "down" => "↓",
            _ => "→"
        };
    }

    /// <summary>
    /// Gets trend color based on direction.
    /// </summary>
    public static string GetTrendColor(string direction)
    {
        return direction switch
        {
            "up" => "#22C55E",    // green
            "down" => "#EF4444",  // red
            _ => "#EAB308"        // yellow
        };
    }
}
