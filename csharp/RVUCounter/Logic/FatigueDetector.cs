using RVUCounter.Models;
using Serilog;

namespace RVUCounter.Logic;

/// <summary>
/// Detects fatigue during shifts by monitoring study duration patterns.
/// Compares rolling average study time to first-hour baseline.
/// </summary>
public class FatigueDetector
{
    // Configuration
    private const int MinShiftHoursForDetection = 3;  // Only trigger after 3+ hours
    private const int BaselineWindowMinutes = 60;     // First hour establishes baseline
    private const int RollingWindowMinutes = 30;      // Current performance window
    private const double SlowdownThreshold = 1.4;     // 40% slower triggers alert
    private const int MinStudiesForBaseline = 3;      // Need at least 3 studies for baseline
    private const int MinStudiesForCurrent = 2;       // Need at least 2 studies in current window
    private const int AlertCooldownMinutes = 30;      // Don't alert more than once per 30 min

    // State
    private DateTime? _shiftStart;
    private readonly List<StudyDurationEntry> _studyDurations = new();
    private DateTime? _lastAlertTime;
    private bool _baselineEstablished;
    private double _baselineAvgMinutes;

    private class StudyDurationEntry
    {
        public DateTime CompletedAt { get; set; }
        public double DurationMinutes { get; set; }
    }

    /// <summary>
    /// Resets the detector for a new shift.
    /// </summary>
    public void StartNewShift(DateTime shiftStart)
    {
        _shiftStart = shiftStart;
        _studyDurations.Clear();
        _lastAlertTime = null;
        _baselineEstablished = false;
        _baselineAvgMinutes = 0;
        Log.Debug("FatigueDetector: Started new shift at {ShiftStart}", shiftStart);
    }

    /// <summary>
    /// Records a completed study for fatigue tracking.
    /// </summary>
    public void RecordStudy(DateTime completedAt, double durationSeconds)
    {
        if (_shiftStart == null) return;
        if (durationSeconds <= 0) return;

        var entry = new StudyDurationEntry
        {
            CompletedAt = completedAt,
            DurationMinutes = durationSeconds / 60.0
        };

        _studyDurations.Add(entry);

        // Check if we can establish baseline (after first hour with enough studies)
        if (!_baselineEstablished)
        {
            var baselineEnd = _shiftStart.Value.AddMinutes(BaselineWindowMinutes);
            if (completedAt >= baselineEnd)
            {
                var baselineStudies = _studyDurations
                    .Where(s => s.CompletedAt >= _shiftStart.Value && s.CompletedAt <= baselineEnd)
                    .ToList();

                if (baselineStudies.Count >= MinStudiesForBaseline)
                {
                    _baselineAvgMinutes = baselineStudies.Average(s => s.DurationMinutes);
                    _baselineEstablished = true;
                    Log.Debug("FatigueDetector: Baseline established at {BaselineAvg:F1} minutes per study ({Count} studies)",
                        _baselineAvgMinutes, baselineStudies.Count);
                }
            }
        }
    }

    /// <summary>
    /// Analyzes current fatigue state.
    /// </summary>
    public FatigueAnalysis Analyze()
    {
        var analysis = new FatigueAnalysis();

        if (_shiftStart == null) return analysis;

        var now = DateTime.Now;
        var shiftHours = (now - _shiftStart.Value).TotalHours;

        // Don't analyze if shift too short
        if (shiftHours < MinShiftHoursForDetection)
        {
            return analysis;
        }

        // Need baseline established
        if (!_baselineEstablished)
        {
            return analysis;
        }

        analysis.ShiftStartAvgMinutes = _baselineAvgMinutes;
        analysis.StudiesInBaseline = _studyDurations
            .Count(s => s.CompletedAt <= _shiftStart.Value.AddMinutes(BaselineWindowMinutes));

        // Get current window studies (last 30 minutes)
        var windowStart = now.AddMinutes(-RollingWindowMinutes);
        var currentStudies = _studyDurations
            .Where(s => s.CompletedAt >= windowStart)
            .ToList();

        analysis.StudiesInCurrentWindow = currentStudies.Count;

        if (currentStudies.Count < MinStudiesForCurrent)
        {
            return analysis;
        }

        analysis.CurrentAvgMinutes = currentStudies.Average(s => s.DurationMinutes);

        // Calculate slowdown
        if (_baselineAvgMinutes > 0)
        {
            var slowdownRatio = analysis.CurrentAvgMinutes / _baselineAvgMinutes;
            analysis.SlowdownPercent = (slowdownRatio - 1) * 100;

            // Check if fatigue threshold exceeded
            if (slowdownRatio >= SlowdownThreshold)
            {
                // Check cooldown
                if (_lastAlertTime == null || (now - _lastAlertTime.Value).TotalMinutes >= AlertCooldownMinutes)
                {
                    analysis.FatigueDetected = true;
                    analysis.AlertMessage = $"You may be slowing down - {analysis.SlowdownPercent:F0}% longer per study than start of shift";
                    analysis.LastAlertTime = now;
                    _lastAlertTime = now;

                    Log.Information("FatigueDetector: Fatigue detected - Current avg {Current:F1}min vs baseline {Baseline:F1}min ({Slowdown:F0}% slower)",
                        analysis.CurrentAvgMinutes, _baselineAvgMinutes, analysis.SlowdownPercent);
                }
            }
        }

        return analysis;
    }

    /// <summary>
    /// Clears state when shift ends.
    /// </summary>
    public void EndShift()
    {
        _shiftStart = null;
        _studyDurations.Clear();
        _lastAlertTime = null;
        _baselineEstablished = false;
        _baselineAvgMinutes = 0;
    }

    /// <summary>
    /// Gets summary stats for debugging/display.
    /// </summary>
    public (int totalStudies, double baselineAvg, bool isTracking) GetStatus()
    {
        return (_studyDurations.Count, _baselineAvgMinutes, _shiftStart != null);
    }
}
