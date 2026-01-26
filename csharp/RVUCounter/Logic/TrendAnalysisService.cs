using RVUCounter.Data;
using RVUCounter.Models;
using Serilog;

namespace RVUCounter.Logic;

/// <summary>
/// Service for analyzing RVU trends over time.
/// Calculates rolling averages, comparisons, and generates insights.
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
    public TrendAnalysis AnalyzeTrends(DateTime startDate, DateTime endDate)
    {
        var analysis = new TrendAnalysis();

        try
        {
            // Get all shifts in range
            var shifts = _database.GetAllShifts()
                .Where(s => s.ShiftStart >= startDate && s.ShiftStart <= endDate)
                .OrderBy(s => s.ShiftStart)
                .ToList();

            if (shifts.Count == 0)
            {
                analysis.InsightMessage = "No shift data available for this period.";
                return analysis;
            }

            // Build daily data
            var dailyTotals = new Dictionary<DateTime, (double rvu, int studies, double hours)>();

            foreach (var shift in shifts)
            {
                var records = _database.GetRecordsForShift(shift.Id);
                var shiftDate = shift.ShiftStart.Date;
                var shiftHours = shift.ShiftEnd.HasValue
                    ? (shift.ShiftEnd.Value - shift.ShiftStart).TotalHours
                    : 0;

                var rvu = records.Sum(r => r.Rvu);
                var studies = records.Count;

                if (dailyTotals.ContainsKey(shiftDate))
                {
                    var existing = dailyTotals[shiftDate];
                    dailyTotals[shiftDate] = (existing.rvu + rvu, existing.studies + studies, existing.hours + shiftHours);
                }
                else
                {
                    dailyTotals[shiftDate] = (rvu, studies, shiftHours);
                }
            }

            // Convert to TrendData list
            var allDates = dailyTotals.Keys.OrderBy(d => d).ToList();
            var dataList = new List<TrendData>();

            foreach (var date in allDates)
            {
                var (rvu, studies, hours) = dailyTotals[date];
                var trendData = new TrendData
                {
                    Date = date,
                    TotalRvu = rvu,
                    StudyCount = studies,
                    TotalHours = hours,
                    RvuPerHour = hours > 0 ? rvu / hours : 0
                };
                dataList.Add(trendData);
            }

            // Calculate rolling averages
            CalculateRollingAverages(dataList);

            analysis.DailyData = dataList;

            // Calculate summary stats
            analysis.TotalRvu = dataList.Sum(d => d.TotalRvu);
            analysis.TotalStudies = dataList.Sum(d => d.StudyCount);
            analysis.AvgDailyRvu = dataList.Count > 0 ? analysis.TotalRvu / dataList.Count : 0;

            var totalHours = dataList.Sum(d => d.TotalHours);
            analysis.AvgRvuPerHour = totalHours > 0 ? analysis.TotalRvu / totalHours : 0;

            var bestDay = dataList.OrderByDescending(d => d.TotalRvu).FirstOrDefault();
            if (bestDay != null)
            {
                analysis.BestDayRvu = bestDay.TotalRvu;
                analysis.BestDayDate = bestDay.Date;
            }

            // Calculate period comparisons
            CalculatePeriodComparisons(analysis, dataList);

            // Generate insight message
            analysis.InsightMessage = GenerateInsightMessage(analysis);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error analyzing trends");
            analysis.InsightMessage = "Error analyzing trends.";
        }

        return analysis;
    }

    /// <summary>
    /// Calculates 7-day and 30-day rolling averages.
    /// </summary>
    private void CalculateRollingAverages(List<TrendData> data)
    {
        for (int i = 0; i < data.Count; i++)
        {
            // 7-day rolling average
            var start7 = Math.Max(0, i - 6);
            var window7 = data.Skip(start7).Take(i - start7 + 1).ToList();
            data[i].RollingAvg7Day = window7.Count > 0 ? window7.Average(d => d.TotalRvu) : 0;

            // 30-day rolling average
            var start30 = Math.Max(0, i - 29);
            var window30 = data.Skip(start30).Take(i - start30 + 1).ToList();
            data[i].RollingAvg30Day = window30.Count > 0 ? window30.Average(d => d.TotalRvu) : 0;
        }
    }

    /// <summary>
    /// Calculates week-over-week and month-over-month changes.
    /// </summary>
    private void CalculatePeriodComparisons(TrendAnalysis analysis, List<TrendData> data)
    {
        if (data.Count < 7)
        {
            analysis.TrendDirection = "stable";
            return;
        }

        var now = DateTime.Now.Date;

        // Week over week: compare last 7 days to previous 7 days
        var lastWeek = data.Where(d => d.Date > now.AddDays(-7) && d.Date <= now).ToList();
        var prevWeek = data.Where(d => d.Date > now.AddDays(-14) && d.Date <= now.AddDays(-7)).ToList();

        if (lastWeek.Count > 0 && prevWeek.Count > 0)
        {
            var lastWeekAvg = lastWeek.Average(d => d.TotalRvu);
            var prevWeekAvg = prevWeek.Average(d => d.TotalRvu);

            if (prevWeekAvg > 0)
            {
                analysis.WeekOverWeekChange = ((lastWeekAvg - prevWeekAvg) / prevWeekAvg) * 100;
            }
        }

        // Month over month
        var lastMonth = data.Where(d => d.Date > now.AddDays(-30) && d.Date <= now).ToList();
        var prevMonth = data.Where(d => d.Date > now.AddDays(-60) && d.Date <= now.AddDays(-30)).ToList();

        if (lastMonth.Count > 0 && prevMonth.Count > 0)
        {
            var lastMonthAvg = lastMonth.Average(d => d.TotalRvu);
            var prevMonthAvg = prevMonth.Average(d => d.TotalRvu);

            if (prevMonthAvg > 0)
            {
                analysis.MonthOverMonthChange = ((lastMonthAvg - prevMonthAvg) / prevMonthAvg) * 100;
            }
        }

        // Determine trend direction based on recent data
        if (data.Count >= 3)
        {
            var recent3 = data.TakeLast(3).ToList();
            var older3 = data.SkipLast(3).TakeLast(3).ToList();

            if (older3.Count >= 3)
            {
                var recentAvg = recent3.Average(d => d.TotalRvu);
                var olderAvg = older3.Average(d => d.TotalRvu);
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
    private string GenerateInsightMessage(TrendAnalysis analysis)
    {
        var messages = new List<string>();

        // Trend direction message
        if (analysis.TrendDirection == "up")
        {
            if (analysis.WeekOverWeekChange > 0)
                messages.Add($"You're up {analysis.WeekOverWeekChange:F0}% from last week!");
            else if (analysis.MonthOverMonthChange > 0)
                messages.Add($"You're up {analysis.MonthOverMonthChange:F0}% from last month.");
        }
        else if (analysis.TrendDirection == "down")
        {
            if (analysis.WeekOverWeekChange < 0)
                messages.Add($"You're down {Math.Abs(analysis.WeekOverWeekChange):F0}% from last week.");
            else if (analysis.MonthOverMonthChange < 0)
                messages.Add($"You're down {Math.Abs(analysis.MonthOverMonthChange):F0}% from last month.");
        }
        else
        {
            messages.Add("Your productivity has been stable.");
        }

        // Best day highlight
        if (analysis.BestDayDate.HasValue && analysis.BestDayRvu > 0)
        {
            var daysAgo = (DateTime.Now.Date - analysis.BestDayDate.Value).Days;
            if (daysAgo == 0)
                messages.Add($"Today was your best day with {analysis.BestDayRvu:F1} RVU!");
            else if (daysAgo <= 7)
                messages.Add($"Best day this week: {analysis.BestDayRvu:F1} RVU on {analysis.BestDayDate.Value:dddd}.");
        }

        // Average comparison
        if (analysis.AvgDailyRvu > 0)
        {
            messages.Add($"Daily average: {analysis.AvgDailyRvu:F1} RVU ({analysis.AvgRvuPerHour:F1}/hr).");
        }

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
