namespace RVUCounter.Models;

/// <summary>
/// Productivity insights derived from historical data.
/// </summary>
public class ProductivityInsights
{
    /// <summary>
    /// Most productive hour of day (0-23)
    /// </summary>
    public int BestHourOfDay { get; set; }

    /// <summary>
    /// RVU/hour during the best hour
    /// </summary>
    public double BestHourRvuPerHour { get; set; }

    /// <summary>
    /// Most productive day of week
    /// </summary>
    public DayOfWeek BestDayOfWeek { get; set; }

    /// <summary>
    /// Average RVU on the best day
    /// </summary>
    public double BestDayAvgRvu { get; set; }

    /// <summary>
    /// Study type read fastest (lowest avg duration)
    /// </summary>
    public string FastestStudyType { get; set; } = "";

    /// <summary>
    /// Average minutes per study for fastest type
    /// </summary>
    public double FastestAvgMinutes { get; set; }

    /// <summary>
    /// Study type read slowest (highest avg duration)
    /// </summary>
    public string SlowestStudyType { get; set; } = "";

    /// <summary>
    /// Average minutes per study for slowest type
    /// </summary>
    public double SlowestAvgMinutes { get; set; }

    /// <summary>
    /// Modality with highest RVU contribution
    /// </summary>
    public string TopModality { get; set; } = "";

    /// <summary>
    /// Percentage of RVU from top modality
    /// </summary>
    public double TopModalityPercent { get; set; }

    /// <summary>
    /// Natural language recommendations
    /// </summary>
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Hourly breakdown for chart display
    /// </summary>
    public Dictionary<int, double> RvuByHour { get; set; } = new();

    /// <summary>
    /// Day of week breakdown for chart display
    /// </summary>
    public Dictionary<DayOfWeek, double> RvuByDayOfWeek { get; set; } = new();
}

/// <summary>
/// Heatmap cell data for calendar visualization.
/// </summary>
public class HeatmapCell
{
    public DateTime Date { get; set; }
    public double RvuTotal { get; set; }
    public int StudyCount { get; set; }

    /// <summary>
    /// Intensity level 0-4 (GitHub style: 0=none, 1-4 based on RVU quartiles)
    /// </summary>
    public int IntensityLevel { get; set; }

    /// <summary>
    /// Tooltip text for display
    /// </summary>
    public string TooltipText => StudyCount == 0
        ? $"{Date:MMM d}: No studies"
        : $"{Date:MMM d}: {RvuTotal:F1} RVU, {StudyCount} studies";
}

/// <summary>
/// Fatigue analysis for real-time monitoring.
/// </summary>
public class FatigueAnalysis
{
    public bool FatigueDetected { get; set; }
    public double CurrentAvgMinutes { get; set; }  // Last 30 min window
    public double ShiftStartAvgMinutes { get; set; }  // First hour baseline
    public double SlowdownPercent { get; set; }
    public string AlertMessage { get; set; } = "";
    public DateTime? LastAlertTime { get; set; }
    public int StudiesInCurrentWindow { get; set; }
    public int StudiesInBaseline { get; set; }
}
