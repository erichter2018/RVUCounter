namespace RVUCounter.Models;

/// <summary>
/// Represents daily trend data for analytics.
/// </summary>
public class TrendData
{
    public DateTime Date { get; set; }
    public double TotalRvu { get; set; }
    public int StudyCount { get; set; }
    public double RvuPerHour { get; set; }
    public double TotalHours { get; set; }
    public double RollingAvg7Day { get; set; }
    public double RollingAvg30Day { get; set; }
}

/// <summary>
/// Summary of trend analysis including comparisons and insights.
/// </summary>
public class TrendAnalysis
{
    public List<TrendData> DailyData { get; set; } = new();
    public double WeekOverWeekChange { get; set; }  // percentage
    public double MonthOverMonthChange { get; set; }  // percentage
    public string TrendDirection { get; set; } = "stable";  // "up", "down", "stable"
    public string InsightMessage { get; set; } = "";  // Natural language insight

    // Summary stats
    public double TotalRvu { get; set; }
    public int TotalStudies { get; set; }
    public double AvgDailyRvu { get; set; }
    public double AvgRvuPerHour { get; set; }
    public double BestDayRvu { get; set; }
    public DateTime? BestDayDate { get; set; }
}
