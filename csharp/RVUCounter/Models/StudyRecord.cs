namespace RVUCounter.Models;

/// <summary>
/// Represents a radiology study record.
/// Matches the SQLite schema from the Python version.
/// </summary>
public class StudyRecord
{
    /// <summary>
    /// Database primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to the shift this record belongs to
    /// </summary>
    public int ShiftId { get; set; }

    /// <summary>
    /// Accession number (may be hashed for HIPAA compliance)
    /// </summary>
    public string Accession { get; set; } = string.Empty;

    /// <summary>
    /// Original procedure text from PowerScribe/Mosaic
    /// </summary>
    public string Procedure { get; set; } = string.Empty;

    /// <summary>
    /// Classified study type (e.g., "CT CAP", "MRI Brain")
    /// </summary>
    public string StudyType { get; set; } = string.Empty;

    /// <summary>
    /// RVU value for this study
    /// </summary>
    public double Rvu { get; set; }

    /// <summary>
    /// When the study was opened/performed (time_performed in DB)
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// When the study was finished/completed (time_finished in DB)
    /// Used for hour groupings and compensation calculations
    /// </summary>
    public DateTime? TimeFinished { get; set; }

    /// <summary>
    /// Patient class (Inpatient, Outpatient, ER, Unknown)
    /// </summary>
    public string PatientClass { get; set; } = "Unknown";

    /// <summary>
    /// Number of accessions if this is a multi-accession record
    /// </summary>
    public int AccessionCount { get; set; } = 1;

    /// <summary>
    /// Source application (Mosaic, Clario)
    /// </summary>
    public string Source { get; set; } = "Mosaic";

    /// <summary>
    /// JSON string with extra metadata (optional)
    /// </summary>
    public string? Metadata { get; set; }

    /// <summary>
    /// Duration in seconds that study was tracked (optional)
    /// </summary>
    public double? DurationSeconds { get; set; }

    /// <summary>
    /// Whether this record came from a multi-accession group
    /// </summary>
    public bool FromMultiAccession { get; set; }

    /// <summary>
    /// Group ID for related multi-accession studies (Python parity)
    /// </summary>
    public string? MultiAccessionGroup { get; set; }

    /// <summary>
    /// Create a copy of this record
    /// </summary>
    public StudyRecord Clone()
    {
        return new StudyRecord
        {
            Id = Id,
            ShiftId = ShiftId,
            Accession = Accession,
            Procedure = Procedure,
            StudyType = StudyType,
            Rvu = Rvu,
            Timestamp = Timestamp,
            TimeFinished = TimeFinished,
            PatientClass = PatientClass,
            AccessionCount = AccessionCount,
            Source = Source,
            Metadata = Metadata,
            DurationSeconds = DurationSeconds,
            FromMultiAccession = FromMultiAccession,
            MultiAccessionGroup = MultiAccessionGroup
        };
    }
}
