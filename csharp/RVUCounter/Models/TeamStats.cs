using System.Text.Json.Serialization;

namespace RVUCounter.Models;

/// <summary>
/// Anonymous stats shared with team members.
/// Contains ONLY aggregate data - no identifying information.
/// </summary>
public class TeamMemberStats
{
    /// <summary>
    /// Random anonymous ID (not tied to machine/user)
    /// Generated fresh on each enable
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>
    /// Last update timestamp (ISO 8601)
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Whether shift is currently active
    /// </summary>
    [JsonPropertyName("shift_active")]
    public bool ShiftActive { get; set; }

    /// <summary>
    /// Total RVU for current shift
    /// </summary>
    [JsonPropertyName("total_rvu")]
    public double TotalRvu { get; set; }

    /// <summary>
    /// Number of studies completed
    /// </summary>
    [JsonPropertyName("study_count")]
    public int StudyCount { get; set; }

    /// <summary>
    /// RVU per hour rate
    /// </summary>
    [JsonPropertyName("rvu_per_hour")]
    public double RvuPerHour { get; set; }

    /// <summary>
    /// Shift duration in minutes
    /// </summary>
    [JsonPropertyName("shift_duration_mins")]
    public int ShiftDurationMins { get; set; }

    /// <summary>
    /// Percentage of studies that are inpatient stat
    /// </summary>
    [JsonPropertyName("inpatient_stat_pct")]
    public double InpatientStatPct { get; set; }
}

/// <summary>
/// Team data stored in cloud JSON storage.
/// </summary>
public class TeamData
{
    /// <summary>
    /// Team code (6 characters)
    /// </summary>
    [JsonPropertyName("team_code")]
    public string TeamCode { get; set; } = "";

    /// <summary>
    /// When team was created
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Array of team member stats
    /// </summary>
    [JsonPropertyName("members")]
    public List<TeamMemberStats> Members { get; set; } = new();
}

/// <summary>
/// Local team configuration stored in user settings.
/// </summary>
public class TeamConfig
{
    /// <summary>
    /// Whether team dashboard is enabled
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Team code joined/created
    /// </summary>
    public string TeamCode { get; set; } = "";

    /// <summary>
    /// Anonymous ID for this user (random, not machine-tied)
    /// </summary>
    public string AnonymousId { get; set; } = "";

    /// <summary>
    /// JSON bin URL for team data storage
    /// </summary>
    public string StorageUrl { get; set; } = "";
}

/// <summary>
/// Display model for team member in UI.
/// </summary>
public class TeamMemberDisplay
{
    public double TotalRvu { get; set; }
    public double RvuPerHour { get; set; }
    public double InpatientStatPct { get; set; }
    public double DisplayValue { get; set; }  // Current display value (rate or total based on mode)
    public double PositionPercent { get; set; }  // Position on number line (0.0-1.0)
    public bool IsYou { get; set; }
    public string DisplayText => IsYou
        ? $"You: {DisplayValue:F1} | IP Stat: {InpatientStatPct:F0}%"
        : $"{DisplayValue:F1} | IP Stat: {InpatientStatPct:F0}%";
}
