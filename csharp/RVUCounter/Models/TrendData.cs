namespace RVUCounter.Models;

/// <summary>
/// Represents per-shift trend data for analytics.
/// </summary>
public class TrendData
{
    public DateTime Date { get; set; }
    public int ShiftId { get; set; }
    public string? ShiftName { get; set; }
    public double TotalRvu { get; set; }
    public int StudyCount { get; set; }
    public double RvuPerHour { get; set; }
    public double TotalHours { get; set; }
    public double RollingAvg7 { get; set; }
    public double RollingAvg30 { get; set; }
}

/// <summary>
/// Summary of trend analysis including comparisons and insights.
/// </summary>
public class TrendAnalysis
{
    public List<TrendData> ShiftData { get; set; } = new();
    public double WeekOverWeekChange { get; set; }  // percentage
    public double MonthOverMonthChange { get; set; }  // percentage
    public string TrendDirection { get; set; } = "stable";  // "up", "down", "stable"
    public string InsightMessage { get; set; } = "";  // Natural language insight

    // Summary stats
    public double TotalRvu { get; set; }
    public int TotalStudies { get; set; }
    public int TotalShifts { get; set; }
    public double AvgRvuPerShift { get; set; }
    public double AvgRvuPerHour { get; set; }
    public double BestShiftRvu { get; set; }
    public double BestShiftRvuPerHour { get; set; }
    public DateTime? BestShiftDate { get; set; }
}
