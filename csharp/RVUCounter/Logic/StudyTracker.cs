using RVUCounter.Data;
using RVUCounter.Models;
using Serilog;

namespace RVUCounter.Logic;

/// <summary>
/// Tracks active studies and detects when they are completed (read).
/// Ported from Python study_tracker.py with full feature parity.
/// </summary>
public class StudyTracker
{
    private readonly Dictionary<string, TrackedStudy> _activeStudies = new();
    private readonly HashSet<string> _seenAccessions = new();
    private readonly int _minSecondsToCount;
    private readonly object _lock = new();

    public StudyTracker(int minSecondsToCount = 10)
    {
        _minSecondsToCount = minSecondsToCount;
    }

    /// <summary>
    /// Add or update an active study with study type matching.
    /// </summary>
    public void AddStudy(
        string accession,
        string procedure,
        DateTime timestamp,
        Dictionary<string, double>? rvuTable = null,
        Dictionary<string, List<ClassificationCondition>>? classificationRules = null,
        string patientClass = "")
    {
        if (string.IsNullOrEmpty(accession))
            return;

        lock (_lock)
        {
            if (_activeStudies.TryGetValue(accession, out var existing))
            {
                // Update existing study
                existing.LastSeen = timestamp;

                if (!string.IsNullOrEmpty(patientClass))
                    existing.PatientClass = patientClass;

                // If procedure was empty/Unknown before and now we have a valid procedure, update it
                var existingProcedure = existing.Procedure;
                var existingStudyType = existing.StudyType;
                var invalidProcedures = new[] { "", "n/a", "na", "no report" };

                if ((string.IsNullOrEmpty(existingProcedure) ||
                     invalidProcedures.Contains(existingProcedure.ToLowerInvariant()) ||
                     existingStudyType == "Unknown") &&
                    !string.IsNullOrEmpty(procedure) &&
                    !invalidProcedures.Contains(procedure.ToLowerInvariant()) &&
                    rvuTable != null)
                {
                    // Update procedure and re-match study type
                    var (studyType, rvu) = StudyMatcher.MatchStudyType(
                        procedure, rvuTable, classificationRules);

                    existing.Procedure = procedure;
                    existing.StudyType = studyType;
                    existing.Rvu = rvu;

                    Log.Information("Updated study procedure and type: {Accession} - {StudyType} ({Rvu} RVU) (was: {OldType})",
                        accession, studyType, rvu, existingStudyType);
                }
            }
            else
            {
                // New study - match study type if RVU table provided
                var studyType = "Unknown";
                var rvu = 0.0;

                if (rvuTable != null && !string.IsNullOrEmpty(procedure))
                {
                    (studyType, rvu) = StudyMatcher.MatchStudyType(
                        procedure, rvuTable, classificationRules);
                }

                _activeStudies[accession] = new TrackedStudy
                {
                    Accession = accession,
                    Procedure = procedure ?? "",
                    PatientClass = patientClass,
                    StudyType = studyType,
                    Rvu = rvu,
                    FirstSeen = timestamp,
                    LastSeen = timestamp
                };

                Log.Information("Added study: {Accession} - {StudyType} ({Rvu} RVU) - Patient Class: {PatientClass}",
                    accession, studyType, rvu, patientClass);
            }
        }
    }

    /// <summary>
    /// Check for studies that have disappeared (completed).
    /// </summary>
    public List<TrackedStudy> CheckCompleted(DateTime currentTime, string currentAccession = "")
    {
        var completed = new List<TrackedStudy>();
        var toRemove = new List<string>();

        lock (_lock)
        {
            Log.Debug("CheckCompleted: current='{CurrentAccession}', active={ActiveStudies}",
                currentAccession, string.Join(", ", _activeStudies.Keys));

            foreach (var kvp in _activeStudies)
            {
                var accession = kvp.Key;
                var study = kvp.Value;

                // If this accession is currently visible, it's not completed
                if (accession == currentAccession)
                {
                    Log.Debug("CheckCompleted: {Accession} is currently visible, skipping", accession);
                    continue;
                }

                // Study is considered completed if:
                // 1. A different study is now visible (current_accession is set and different), OR
                // 2. No study is visible (current_accession is empty) - complete immediately
                bool shouldComplete = false;

                if (!string.IsNullOrEmpty(currentAccession))
                {
                    // Different study is visible - this one is completed
                    shouldComplete = true;
                    Log.Debug("CheckCompleted: {Accession} should complete - different study '{CurrentAccession}' is visible",
                        accession, currentAccession);
                }
                else
                {
                    // No study is visible - complete immediately
                    shouldComplete = true;
                    Log.Debug("CheckCompleted: {Accession} should complete - no study visible", accession);
                }

                if (shouldComplete)
                {
                    // Use currentTime as end_time when accession is empty (study just closed)
                    // Use lastSeen when a different study is visible (was replaced)
                    var endTime = string.IsNullOrEmpty(currentAccession) ? currentTime : study.LastSeen;
                    var duration = (endTime - study.FirstSeen).TotalSeconds;

                    Log.Debug("CheckCompleted: {Accession} disappeared, duration={Duration:F1}s, min={Min}s",
                        accession, duration, _minSecondsToCount);

                    // Only count if duration >= min_seconds
                    if (duration >= _minSecondsToCount)
                    {
                        study.CompletedAt = endTime;
                        study.Duration = duration;
                        completed.Add(study.Clone());
                        Log.Information("Completed study: {Accession} - {StudyType} ({Duration:F1}s)",
                            accession, study.StudyType, duration);
                    }
                    else
                    {
                        Log.Debug("Ignored short study: {Accession} ({Duration:F1}s < {Min}s)",
                            accession, duration, _minSecondsToCount);
                    }

                    toRemove.Add(accession);
                }
            }

            // Remove completed studies from active tracking
            foreach (var accession in toRemove)
            {
                _activeStudies.Remove(accession);
            }
        }

        Log.Debug("CheckCompleted returning {Count} completed studies", completed.Count);
        return completed;
    }

    /// <summary>
    /// Update the tracker with current accessions from the worklist.
    /// Returns list of studies that were completed (removed from worklist).
    /// </summary>
    public List<TrackedStudy> UpdateWithCurrentAccessions(
        IEnumerable<string> currentAccessions,
        Dictionary<string, string>? procedureMap = null,
        Dictionary<string, double>? rvuTable = null,
        Dictionary<string, List<ClassificationCondition>>? classificationRules = null)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            var currentSet = currentAccessions.ToHashSet();
            var completedStudies = new List<TrackedStudy>();

            // Find studies that disappeared (completed)
            var toRemove = new List<string>();
            foreach (var kvp in _activeStudies)
            {
                if (!currentSet.Contains(kvp.Key))
                {
                    var study = kvp.Value;
                    var elapsed = (now - study.FirstSeen).TotalSeconds;

                    if (elapsed >= _minSecondsToCount)
                    {
                        // Study was on worklist long enough - count it
                        study.CompletedAt = now;
                        study.Duration = elapsed;
                        completedStudies.Add(study.Clone());
                        Log.Information("Study completed: {Accession} - {Procedure} (tracked for {Seconds:F1}s)",
                            study.Accession, study.Procedure, elapsed);
                    }
                    else
                    {
                        Log.Debug("Study removed too quickly: {Accession} ({Seconds:F1}s < {Min}s)",
                            study.Accession, elapsed, _minSecondsToCount);
                    }

                    toRemove.Add(kvp.Key);
                }
            }

            // Remove completed/discarded studies
            foreach (var accession in toRemove)
            {
                _activeStudies.Remove(accession);
            }

            // Add new studies
            foreach (var accession in currentSet)
            {
                if (!_activeStudies.ContainsKey(accession))
                {
                    var procedure = procedureMap?.GetValueOrDefault(accession, "") ?? "";

                    // Match study type if RVU table provided
                    var studyType = "Unknown";
                    var rvu = 0.0;
                    if (rvuTable != null && !string.IsNullOrEmpty(procedure))
                    {
                        (studyType, rvu) = StudyMatcher.MatchStudyType(procedure, rvuTable, classificationRules);
                    }

                    _activeStudies[accession] = new TrackedStudy
                    {
                        Accession = accession,
                        Procedure = procedure,
                        StudyType = studyType,
                        Rvu = rvu,
                        FirstSeen = now,
                        LastSeen = now
                    };
                    Log.Debug("Started tracking: {Accession} - {Procedure}", accession, procedure);
                }
                else
                {
                    // Update last seen time
                    _activeStudies[accession].LastSeen = now;
                }
            }

            return completedStudies;
        }
    }

    /// <summary>
    /// Check if study should be ignored for TRACKING purposes.
    /// Only blocks studies that were part of multi-accession groups.
    /// </summary>
    public bool ShouldIgnore(string accession, bool ignoreDuplicates, DataManager? dataManager = null)
    {
        if (string.IsNullOrEmpty(accession))
            return true;

        lock (_lock)
        {
            // Don't ignore if it's currently active
            if (_activeStudies.ContainsKey(accession))
                return false;

            // Only check for duplicates if ignoreDuplicates is true
            if (!ignoreDuplicates)
                return false;

            // Only block if this accession was part of a multi-accession study
            if (dataManager != null && WasPartOfMultiAccession(accession, dataManager))
            {
                Log.Debug("Ignoring accession {Accession} - it was already recorded as part of a multi-accession study",
                    accession);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Check if study has already been recorded (for display purposes only).
    /// </summary>
    public bool IsAlreadyRecorded(string accession, DataManager? dataManager = null)
    {
        if (string.IsNullOrEmpty(accession))
            return false;

        lock (_lock)
        {
            // Check in-memory cache first (faster)
            if (_seenAccessions.Contains(accession))
                return true;
        }

        // Check database for duplicates in current shift
        if (dataManager != null)
        {
            try
            {
                var currentShift = dataManager.Database.GetCurrentShift();
                if (currentShift != null)
                {
                    var hashedAccession = dataManager.HashAccession(accession);
                    var dbRecord = dataManager.Database.FindRecordByAccession(currentShift.Id, hashedAccession);
                    if (dbRecord != null)
                    {
                        // Add to memory cache
                        lock (_lock)
                        {
                            _seenAccessions.Add(accession);
                        }
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error checking database for duplicate");
            }

            // Also check if this accession was part of a multi-accession study
            if (WasPartOfMultiAccession(accession, dataManager))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if an accession was already recorded as part of a multi-accession study.
    /// Uses from_multi_accession field for direct detection (Python parity).
    /// </summary>
    private bool WasPartOfMultiAccession(string accession, DataManager dataManager)
    {
        try
        {
            var hashedAccession = dataManager.HashAccession(accession);
            var currentShift = dataManager.Database.GetCurrentShift();

            if (currentShift != null)
            {
                var records = dataManager.Database.GetRecordsForShift(currentShift.Id);
                foreach (var record in records)
                {
                    // Check if this accession matches and was from multi-accession (Python parity)
                    if (record.Accession == hashedAccession && record.FromMultiAccession)
                    {
                        Log.Debug("Found multi-accession record for {Accession} in current shift", accession);
                        return true;
                    }

                    // Legacy fallback: check AccessionCount > 1 and metadata
                    if (record.AccessionCount > 1 && !string.IsNullOrEmpty(record.Metadata))
                    {
                        var accessions = record.Metadata.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(a => a.Trim());
                        if (accessions.Contains(accession) || accessions.Contains(hashedAccession))
                            return true;
                    }
                }
            }

            // Check historical shifts too (limit to last 10 for performance)
            var shifts = dataManager.Database.GetAllShifts()
                .Where(s => s.Id != currentShift?.Id)
                .OrderByDescending(s => s.ShiftStart)
                .Take(10);

            foreach (var shift in shifts)
            {
                var records = dataManager.Database.GetRecordsForShift(shift.Id);
                foreach (var record in records)
                {
                    // Check direct from_multi_accession flag
                    if (record.Accession == hashedAccession && record.FromMultiAccession)
                    {
                        Log.Debug("Found multi-accession record for {Accession} in historical shift {ShiftId}",
                            accession, shift.Id);
                        return true;
                    }

                    // Legacy fallback
                    if (record.AccessionCount > 1 && !string.IsNullOrEmpty(record.Metadata))
                    {
                        var accessions = record.Metadata.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(a => a.Trim());
                        if (accessions.Contains(accession) || accessions.Contains(hashedAccession))
                            return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error checking if accession was part of multi-accession");
        }

        return false;
    }

    /// <summary>
    /// Mark accession as seen (recorded).
    /// </summary>
    public void MarkSeen(string accession)
    {
        if (!string.IsNullOrEmpty(accession))
        {
            lock (_lock)
            {
                _seenAccessions.Add(accession);
            }
        }
    }

    /// <summary>
    /// Get all seen accessions.
    /// </summary>
    public HashSet<string> GetSeenAccessions()
    {
        lock (_lock)
        {
            return new HashSet<string>(_seenAccessions);
        }
    }

    /// <summary>
    /// Remove a study from tracking (e.g., manually marked as complete).
    /// </summary>
    public TrackedStudy? RemoveStudy(string accession)
    {
        lock (_lock)
        {
            if (_activeStudies.TryGetValue(accession, out var study))
            {
                _activeStudies.Remove(accession);
                study.CompletedAt = DateTime.Now;
                study.Duration = (study.CompletedAt.Value - study.FirstSeen).TotalSeconds;
                return study;
            }
            return null;
        }
    }

    /// <summary>
    /// Check if a study is currently being tracked.
    /// </summary>
    public bool IsTracking(string accession)
    {
        lock (_lock)
        {
            return _activeStudies.ContainsKey(accession);
        }
    }

    /// <summary>
    /// Get all currently tracked accessions.
    /// </summary>
    public List<string> GetTrackedAccessions()
    {
        lock (_lock)
        {
            return _activeStudies.Keys.ToList();
        }
    }

    /// <summary>
    /// Get count of currently tracked studies.
    /// </summary>
    public int TrackedCount
    {
        get
        {
            lock (_lock)
            {
                return _activeStudies.Count;
            }
        }
    }

    /// <summary>
    /// Clear all tracked studies and seen accessions.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _activeStudies.Clear();
            _seenAccessions.Clear();
        }
    }

    /// <summary>
    /// Get tracking summary for a specific accession.
    /// </summary>
    public TrackedStudy? GetStudy(string accession)
    {
        lock (_lock)
        {
            return _activeStudies.GetValueOrDefault(accession);
        }
    }
}

/// <summary>
/// Represents a study being tracked on the worklist.
/// </summary>
public class TrackedStudy
{
    public string Accession { get; set; } = string.Empty;
    public string Procedure { get; set; } = string.Empty;
    public string StudyType { get; set; } = "Unknown";
    public double Rvu { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double Duration { get; set; }
    public string PatientClass { get; set; } = "Unknown";
    public string Source { get; set; } = "Mosaic";

    /// <summary>
    /// How long the study was tracked (seconds).
    /// </summary>
    public double TrackedSeconds => Duration > 0 ? Duration : ((CompletedAt ?? DateTime.Now) - FirstSeen).TotalSeconds;

    /// <summary>
    /// Create a copy of this study.
    /// </summary>
    public TrackedStudy Clone()
    {
        return new TrackedStudy
        {
            Accession = Accession,
            Procedure = Procedure,
            StudyType = StudyType,
            Rvu = Rvu,
            FirstSeen = FirstSeen,
            LastSeen = LastSeen,
            CompletedAt = CompletedAt,
            Duration = Duration,
            PatientClass = PatientClass,
            Source = Source
        };
    }
}
