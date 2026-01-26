namespace RVUCounter.Models;

/// <summary>
/// Represents a work shift containing study records.
/// Matches the SQLite schema from the Python version.
/// </summary>
public class Shift
{
    /// <summary>
    /// Database primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// When the shift started (actual clock time)
    /// </summary>
    public DateTime ShiftStart { get; set; }

    /// <summary>
    /// When the shift ended (null if still active)
    /// </summary>
    public DateTime? ShiftEnd { get; set; }

    /// <summary>
    /// Effective start time (for RVU calculations, may differ from ShiftStart)
    /// </summary>
    public DateTime? EffectiveShiftStart { get; set; }

    /// <summary>
    /// Projected end time (for projections)
    /// </summary>
    public DateTime? ProjectedShiftEnd { get; set; }

    /// <summary>
    /// Optional name/label for the shift
    /// </summary>
    public string? ShiftName { get; set; }

    /// <summary>
    /// Whether this is the currently active shift
    /// </summary>
    public bool IsActive => ShiftEnd == null;

    /// <summary>
    /// Total number of studies in this shift (populated when loading)
    /// </summary>
    public int TotalStudies { get; set; }

    /// <summary>
    /// Total RVU for this shift (populated when loading)
    /// </summary>
    public double TotalRvu { get; set; }

    /// <summary>
    /// Calculate shift duration
    /// </summary>
    public TimeSpan Duration
    {
        get
        {
            var end = ShiftEnd ?? DateTime.Now;
            return end - ShiftStart;
        }
    }

    /// <summary>
    /// Get display-friendly shift label matching Python format:
    /// "Sunday 2d ago (211, 213.9 RVU)"
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var now = DateTime.Now;
            var daysAgo = (int)(now.Date - ShiftStart.Date).TotalDays;

            string timeDesc;
            if (daysAgo == 0)
            {
                timeDesc = "Today";
            }
            else if (daysAgo == 1)
            {
                timeDesc = "Yesterday";
            }
            else if (daysAgo <= 7)
            {
                // "Sunday 2d ago"
                timeDesc = $"{ShiftStart:dddd} {daysAgo}d ago";
            }
            else
            {
                // "01/12 10pm" for older shifts
                timeDesc = ShiftStart.ToString("MM/dd h") + ShiftStart.ToString("tt").ToLower();
            }

            // Add stats if available
            if (TotalStudies > 0 || TotalRvu > 0)
            {
                return $"{timeDesc} ({TotalStudies:N0}, {TotalRvu:N1} RVU)";
            }

            return timeDesc;
        }
    }
}
