namespace RVUCounter.Models;

/// <summary>
/// Unified model for time period list items in the Statistics view.
/// Represents a shift, month, or year that the user can select.
/// </summary>
public class PeriodItem
{
    /// <summary>"shifts", "months", or "years"</summary>
    public string Category { get; set; } = "";

    /// <summary>Shown in the listbox</summary>
    public string DisplayLabel { get; set; } = "";

    /// <summary>Start of the period (inclusive)</summary>
    public DateTime StartDate { get; set; }

    /// <summary>End of the period (exclusive for months/years, null for current shift)</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Database shift ID (for shift items only)</summary>
    public int? ShiftId { get; set; }

    /// <summary>Full shift object (for context menu / comparison)</summary>
    public Shift? Shift { get; set; }

    /// <summary>Number of studies in this period</summary>
    public int StudyCount { get; set; }

    /// <summary>Total RVU for this period</summary>
    public double TotalRvu { get; set; }
}
